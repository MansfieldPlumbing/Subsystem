using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Subsystem.Windows;

// The ouroboros pack — rebuild this self-contained single-file exe IN-PROCESS with the bundled Roslyn and a
// home-rolled bundle codec: no dotnet SDK, no MSBuild, no external packages. A single-file exe is three
// regions concatenated — [ native apphost ][ embedded files ][ manifest ]. We reuse our OWN apphost (it
// already carries the icon and the app-path marker) and swap only the managed assembly; the compile
// references come from our own bundle. The byte format below is mirrored from the .NET host + AsmResolver.

internal enum BundleFileType : byte
{
    Unknown = 0, Assembly = 1, NativeBinary = 2, DepsJson = 3, RuntimeConfigJson = 4, Symbols = 5,
}

internal sealed class BundleFile
{
    public string RelativePath = string.Empty;
    public BundleFileType Type;
    public long Offset;
    public long Size;             // decompressed length
    public long CompressedSize;   // 0 == stored uncompressed
    public byte[] StoredBytes = Array.Empty<byte>();   // raw bytes as they sit in the source image

    public byte[] GetData()
    {
        if (CompressedSize == 0) return StoredBytes;
        using var ms = new MemoryStream(StoredBytes);
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        var outBuf = new byte[Size];
        int total = 0;
        while (total < Size)
        {
            int n = deflate.Read(outBuf, total, (int)Size - total);
            if (n == 0) throw new EndOfStreamException($"deflate ended {total}/{Size} bytes into '{RelativePath}'.");
            total += n;
        }
        return outBuf;
    }
}

internal sealed class BundleManifest
{
    public uint MajorVersion;
    public uint MinorVersion;
    public string BundleId = string.Empty;
    public ulong Flags;
    public long DepsJsonOffset, DepsJsonSize, RuntimeConfigOffset, RuntimeConfigSize;
    public List<BundleFile> Files = new();
}

internal static class SelfBundle
{
    // The 32-byte marker the bundler embeds in the apphost. The manifest offset is the little-endian UInt64
    // in the 8 bytes immediately BEFORE it. The runtime host reads that fixed location — it does NOT scan.
    private static readonly byte[] Signature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    };
    private const int OffsetSize = 8;
    private const long AssemblyAlignment = 4096;   // assemblies are page-aligned so the runtime can mmap them

    public static BundleManifest Read(byte[] exe)
    {
        int sig = IndexOfSignature(exe);
        if (sig < OffsetSize) throw new InvalidDataException("no bundle signature in the image.");
        long manifestOffset = (long)BitConverter.ToUInt64(exe, sig - OffsetSize);

        using var ms = new MemoryStream(exe);
        ms.Seek(manifestOffset, SeekOrigin.Begin);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var m = new BundleManifest { MajorVersion = r.ReadUInt32(), MinorVersion = r.ReadUInt32() };
        int count = r.ReadInt32();
        m.BundleId = r.ReadString();
        if (m.MajorVersion >= 2)
        {
            m.DepsJsonOffset = (long)r.ReadUInt64(); m.DepsJsonSize = (long)r.ReadUInt64();
            m.RuntimeConfigOffset = (long)r.ReadUInt64(); m.RuntimeConfigSize = (long)r.ReadUInt64();
            m.Flags = r.ReadUInt64();
        }
        for (int i = 0; i < count; i++)
        {
            var f = new BundleFile { Offset = (long)r.ReadUInt64(), Size = (long)r.ReadUInt64() };
            if (m.MajorVersion >= 6) f.CompressedSize = (long)r.ReadUInt64();
            f.Type = (BundleFileType)r.ReadByte();
            f.RelativePath = r.ReadString();

            long stored = f.CompressedSize != 0 ? f.CompressedSize : f.Size;
            if (f.Offset < 0 || f.Offset + stored > exe.Length)
                throw new InvalidDataException($"file '{f.RelativePath}' data out of range.");
            f.StoredBytes = new byte[stored];
            Array.Copy(exe, f.Offset, f.StoredBytes, 0, stored);
            m.Files.Add(f);
        }
        return m;
    }

    public static IReadOnlyList<(string Name, byte[] Image)> ManagedAssemblies(BundleManifest m) =>
        m.Files.Where(f => f.Type == BundleFileType.Assembly).Select(f => (f.RelativePath, f.GetData())).ToList();

    public static byte[] Write(byte[] originalExe, BundleManifest m, IDictionary<string, byte[]> replacements)
    {
        if (m.Files.Count == 0) throw new InvalidOperationException("empty manifest.");

        long apphostSize = m.Files.Min(f => f.Offset);   // template = the native host (keeps icon + app-path marker)
        using var outMs = new MemoryStream();
        outMs.Write(originalExe, 0, (int)apphostSize);

        var written = new List<BundleFile>(m.Files.Count);
        foreach (var f in m.Files)
        {
            byte[] payload; long size, compressed;
            if (replacements.TryGetValue(f.RelativePath, out var repl)) { payload = repl; size = repl.Length; compressed = 0; }
            else { payload = f.StoredBytes; size = f.Size; compressed = f.CompressedSize; }

            if (f.Type == BundleFileType.Assembly)
            {
                long pad = (AssemblyAlignment - (outMs.Position % AssemblyAlignment)) % AssemblyAlignment;
                if (pad != 0) outMs.Write(new byte[pad], 0, (int)pad);
            }
            long offset = outMs.Position;
            outMs.Write(payload, 0, payload.Length);
            written.Add(new BundleFile { RelativePath = f.RelativePath, Type = f.Type, Offset = offset, Size = size, CompressedSize = compressed });
        }

        var deps = written.FirstOrDefault(f => f.Type == BundleFileType.DepsJson);
        var rtc = written.FirstOrDefault(f => f.Type == BundleFileType.RuntimeConfigJson);

        long manifestOffset = outMs.Position;
        using (var w = new BinaryWriter(outMs, new UTF8Encoding(false), leaveOpen: true))
        {
            w.Write(m.MajorVersion); w.Write(m.MinorVersion); w.Write(written.Count); w.Write(m.BundleId);
            if (m.MajorVersion >= 2)
            {
                w.Write((ulong)(deps?.Offset ?? 0)); w.Write((ulong)(deps?.Size ?? 0));
                w.Write((ulong)(rtc?.Offset ?? 0)); w.Write((ulong)(rtc?.Size ?? 0));
                w.Write(m.Flags);
            }
            foreach (var f in written)
            {
                w.Write((ulong)f.Offset); w.Write((ulong)f.Size);
                if (m.MajorVersion >= 6) w.Write((ulong)f.CompressedSize);
                w.Write((byte)f.Type); w.Write(f.RelativePath);
            }
        }

        byte[] newExe = outMs.ToArray();
        // Patch the apphost's existing marker IN PLACE — the 8 bytes before its embedded signature. Append
        // nothing: a trailing signature leaves the host reading the stale marker and the exe will not launch.
        int sig = IndexOfSignature(newExe);
        if (sig < OffsetSize) throw new InvalidDataException("apphost signature missing from the packed image.");
        BitConverter.GetBytes((ulong)manifestOffset).CopyTo(newExe, sig - OffsetSize);

        // Prove the codec round-trips. NOTE: this does NOT prove the exe launches — only `ss diag` on it does.
        var check = Read(newExe);
        if (check.Files.Count != written.Count) throw new InvalidDataException("post-pack file-count mismatch.");
        return newExe;
    }

    // Forward scan: the apphost is at the file head, so its signature has the lowest index — found before any
    // astronomically-unlikely coincidental match in the payload.
    private static int IndexOfSignature(byte[] bytes)
    {
        int last = bytes.Length - Signature.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < Signature.Length && bytes[i + j] == Signature[j]) j++;
            if (j == Signature.Length) return i;
        }
        return -1;
    }

    // The entry assembly = the managed dll the host launches. The runtimeconfig is named after it
    // (<app>.runtimeconfig.json -> <app>.dll); fall back to the deps.json name.
    public static string EntryAssembly(BundleManifest m)
    {
        var rtc = m.Files.FirstOrDefault(f => f.Type == BundleFileType.RuntimeConfigJson);
        if (rtc != null && rtc.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            return rtc.RelativePath[..^".runtimeconfig.json".Length] + ".dll";
        var deps = m.Files.FirstOrDefault(f => f.Type == BundleFileType.DepsJson);
        if (deps != null && deps.RelativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            return deps.RelativePath[..^".deps.json".Length] + ".dll";
        throw new InvalidOperationException("could not derive the entry assembly (no runtimeconfig/deps in bundle).");
    }
}

internal static class SelfBuild
{
    // `ss build self` — the zero-dotnet rebuild. Read our own bundle, compile the SubsystemWin source with
    // the bundled Roslyn against the bundle's own assemblies, swap the managed dll, re-pack. Returns the new
    // exe bytes or the compile diagnostics.
    public static (byte[]? Exe, string[]? Errors) Compile(string sourceRoot)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("no process path.");
        var exe = File.ReadAllBytes(exePath);
        var manifest = SelfBundle.Read(exe);

        string entryDll = SelfBundle.EntryAssembly(manifest);
        if (!manifest.Files.Any(f => f.Type == BundleFileType.Assembly && f.RelativePath.Equals(entryDll, StringComparison.OrdinalIgnoreCase)))
            return (null, new[] { $"derived entry assembly '{entryDll}' is not in the bundle." });
        string asmName = Path.GetFileNameWithoutExtension(entryDll);

        // Reference every managed assembly in the bundle EXCEPT the one we are recompiling (else the new
        // 'ss' compilation collides with a reference also named 'ss').
        var references = SelfBundle.ManagedAssemblies(manifest)
            .Where(a => !a.Name.Equals(entryDll, StringComparison.OrdinalIgnoreCase))
            .Select(a => MetadataReference.CreateFromImage(a.Image))
            .ToList<MetadataReference>();

        // SubsystemWin.csproj's exact compile set: host-windows + the linked runspace Vom/Cm. CodeContext is
        // a separate bundled assembly — referenced above, NOT recompiled.
        var roots = new[] { "src/host-windows/", "src/runspace/Vom/", "src/runspace/Cm/" };
        var sources = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(sourceRoot, p).Replace('\\', '/');
                return roots.Any(r => rel.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
        if (sources.Count == 0) return (null, new[] { "no source matched the SubsystemWin compile set." });

        var trees = sources.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p)).ToList();
        // ImplicitUsings=enable: the SDK injects these globals at build; synthesize the same set here.
        trees.Add(CSharpSyntaxTree.ParseText(
            "global using System;\n" +
            "global using System.Collections.Generic;\n" +
            "global using System.IO;\n" +
            "global using System.Linq;\n" +
            "global using System.Net.Http;\n" +
            "global using System.Threading;\n" +
            "global using System.Threading.Tasks;\n"));

        var options = new CSharpCompilationOptions(
            OutputKind.ConsoleApplication,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create(asmName, trees, references, options);

        // The three manifest resources the original carries (LogicalName == the name). Pull them from THIS
        // running assembly so they are always present and exact — no dependency on a dump file being on disk.
        var resources = new List<ResourceDescription>();
        foreach (var name in new[] { "SystemCatalog.json", "ss-source.dump", "app.ico" })
        {
            byte[]? data = SelfResource(name) ?? DiskResource(sourceRoot, name);
            if (data != null)
            {
                var bytes = data;
                resources.Add(new ResourceDescription(name, () => new MemoryStream(bytes), isPublic: true));
            }
        }

        using var pe = new MemoryStream();
        var result = compilation.Emit(pe, manifestResources: resources);
        if (!result.Success)
            return (null, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray());

        var newExe = SelfBundle.Write(exe, manifest,
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [entryDll] = pe.ToArray() });
        return (newExe, null);
    }

    // The verb: ss build self [--path <repo>] [--out <file>] [--replace]
    public static int Run(string[] args)
    {
        Console.WriteLine("ss build self — zero-dotnet rebuild (in-proc Roslyn + home-rolled bundle pack).");
        var root = Build.ResolveSource(Build.PathArg(args));
        if (root == null) { Console.Error.WriteLine("  [FAIL] source: no repo / embedded source / --path."); return 2; }
        Console.WriteLine($"  source: {root}");

        var (exe, errors) = Compile(root);
        if (exe == null)
        {
            var es = errors ?? Array.Empty<string>();
            Console.Error.WriteLine($"ss build self: RED — compile failed ({es.Length} errors). First 40:");
            foreach (var e in es.Take(40)) Console.Error.WriteLine("  " + e);
            return 1;
        }

        var drive = Path.GetPathRoot(Environment.ProcessPath ?? root) ?? root;
        var outPath = ArgValue(args, "--out") ?? Path.Combine(drive, "tmp", "ss-self", "ss.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, exe);
        Console.WriteLine($"ss build self: GREEN — wrote {exe.Length / (1024 * 1024):n0} MB -> {outPath}");
        Console.WriteLine($"  prove the ouroboros:  \"{outPath}\" diag");

        if (Build.HasFlag(args, "--replace"))
        {
            var self = Environment.ProcessPath!;
            var old = self + ".old";
            try { if (File.Exists(old)) File.Delete(old); File.Move(self, old); File.WriteAllBytes(self, exe); Console.WriteLine($"  + self-replaced {self} (old -> {old})"); }
            catch (Exception ex) { Console.Error.WriteLine("  ! self-replace failed: " + ex.Message); }
        }
        return 0;
    }

    private static byte[]? SelfResource(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[]? DiskResource(string root, string fileName)
    {
        var hit = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        return hit == null ? null : File.ReadAllBytes(hit);
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
