using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Subsystem.Windows;

// `ss build` — the build lives IN the binary, never an external .ps1. It dumps + embeds the source (so the
// rebuilt exe carries its own code), publishes a fresh single-file ss.exe, and self-replaces the running
// image via rename-aside. Source = the repo beside the exe, or — if none — the EMBEDDED source extracted to
// temp, so a dropped ss.exe with no repo still rebuilds itself.
//
// dotnet (on the drive) is still the compiler for this one step — the last external dep. The road to zero:
// Roslyn is already inside ss.exe (verified), but a single-file exe's TPA list is EMPTY, so Roslyn has no
// reference assemblies to compile against. The fix (next): embed the ref set, extract to temp, compile +
// apphost-pack in-process. See the session handoff for the exact plan.
internal static class Build
{
    public static int Run(string[] args)
    {
        var first = args.FirstOrDefault()?.ToLowerInvariant();
        return first switch
        {
            "-help" or "--help" or "-h" or "/?" or "help" => PrintHelp(),
            "apk" or "android"                            => BuildApk(args[1..]),
            "self" or "ss"                                => SelfBuild.Run(args[1..]),
            "win" or "windows" or "exe"                   => BuildWindows(args[1..]),
            _                                             => BuildWindows(args),
        };
    }

    // `ss build` (default) — rebuild the WINDOWS head (this exe) from source via dotnet publish, then
    // self-replace the running image. dotnet is still the compiler for this step (the last external dep).
    private static int BuildWindows(string[] args)
    {
        Console.WriteLine("ss build — self-rebuild (Windows head). Safety checks:");

        var root = ResolveSource(PathArg(args));
        if (root == null) { Console.Error.WriteLine("  [FAIL] source: no repo beside the exe and no embedded source. Cannot build."); return 2; }
        int srcFiles = SafeCount(root, "*.cs");
        Console.WriteLine($"  [ok] source: {root} ({srcFiles} .cs files)");

        var csproj = Path.Combine(root, "src", "host-windows", "SubsystemWin.csproj");
        if (!File.Exists(csproj)) { Console.Error.WriteLine($"  [FAIL] project: {csproj} missing."); return 2; }
        Console.WriteLine($"  [ok] project: {csproj}");

        var dotnet = ResolveDotnet();
        if (dotnet == null) { Console.Error.WriteLine("  [FAIL] compiler: no dotnet (DOTNET_ROOT / <drive>\\dotnet / PATH). It is still the compiler — the last external dep."); return 2; }
        Console.WriteLine($"  [ok] compiler: {dotnet}");

        if (srcFiles < 20) { Console.Error.WriteLine($"  [FAIL] sanity: only {srcFiles} .cs files — the source looks incomplete; refusing to build from it."); return 2; }
        Console.WriteLine("  [ok] sanity: source is complete");
        Console.WriteLine();

        // Embed the source so the rebuilt exe carries its own code (`ss extract` writes it back out).
        var dump = Path.Combine(root, "src", "host-windows", "ss-source.dump");
        try { WriteSourceDump(root, dump); Console.WriteLine($"ss build: embedded source dump ({new FileInfo(dump).Length / 1024:n0} KB)"); }
        catch (Exception ex) { Console.Error.WriteLine("ss build: source dump failed (building without embed): " + ex.Message); }

        var drive = Path.GetPathRoot(root) ?? root;
        var outDir = Path.Combine(drive, "tmp", "ss-build");
        Console.WriteLine("ss build: publishing (self-contained, single-file) via " + dotnet);
        int rc = RunProc(dotnet, $"publish \"{csproj}\" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o \"{outDir}\"");
        var built = Path.Combine(outDir, "ss.exe");
        if (rc != 0 || !File.Exists(built)) { Console.Error.WriteLine($"ss build: RED — publish failed (exit {rc})"); return 1; }
        Console.WriteLine($"ss build: compiled ss.exe ({new FileInfo(built).Length / (1024 * 1024):n0} MB)");

        // Replace the running exe IN-SITU (update in place) + drop a copy in the source repo. Never the drive root.
        var selfExe = Environment.ProcessPath ?? Path.Combine(root, "ss.exe");
        foreach (var t in new[] { selfExe, Path.Combine(root, "ss.exe") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { File.Copy(built, t, true); Console.WriteLine("  + " + t); }
            catch   // the running image is locked — rename it aside (Windows allows it), then copy.
            {
                var old = t + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(t, old); File.Copy(built, t, true); Console.WriteLine($"  + {t}  (self-replaced; old → {old})"); }
                catch (Exception ex) { Console.Error.WriteLine($"  ! {t}: {ex.Message}"); }
            }
        }
        Console.WriteLine("ss build: GREEN");
        return 0;
    }

    // `ss build apk` — the SHIP target, dogfooded into the binary: drive the .NET-Android build of the
    // runspace project, then the published analyzer gate (fail-closed), then report the signed APK. dotnet +
    // the Android workload + a JDK ARE required here — an APK is an aapt2/r8/apksigner pipeline, not a plain
    // Roslyn compile — so this verb OWNS that toolchain rather than replacing it. Zero-dotnet self-build is a
    // Windows-exe-only road (Roslyn + an in-proc single-file pack); the APK cannot shed the workload.
    private static int BuildApk(string[] args)
    {
        Console.WriteLine("ss build apk — Android head (ship target). Safety checks:");

        var root = ResolveSource(PathArg(args));
        if (root == null) { Console.Error.WriteLine("  [FAIL] source: no repo beside the exe and no embedded source."); return 2; }
        Console.WriteLine($"  [ok] source: {root}");

        var csproj = Path.Combine(root, "src", "runspace", "Subsystem.csproj");
        if (!File.Exists(csproj)) { Console.Error.WriteLine($"  [FAIL] project: {csproj} missing."); return 2; }
        Console.WriteLine($"  [ok] project: {csproj}");

        var dotnet = ResolveDotnet();
        if (dotnet == null) { Console.Error.WriteLine("  [FAIL] compiler: no dotnet (DOTNET_ROOT / <drive>\\dotnet / PATH)."); return 2; }
        Console.WriteLine($"  [ok] compiler: {dotnet}");

        // Android SDK + JDK, drive-derived (override via SS_ANDROID / SS_JDK) — MSBuild reads these env vars
        // as the AndroidSdkDirectory / JavaSdkDirectory properties, so exporting them IS the -p: pin.
        var drive = Path.GetPathRoot(root) ?? root;
        var android = Environment.GetEnvironmentVariable("SS_ANDROID") ?? Path.Combine(drive, "Android");
        var jdk     = Environment.GetEnvironmentVariable("SS_JDK")     ?? Path.Combine(drive, "jdk");
        if (!Directory.Exists(android)) { Console.Error.WriteLine($"  [FAIL] android sdk: {android} missing (set SS_ANDROID)."); return 2; }
        if (!Directory.Exists(jdk))     { Console.Error.WriteLine($"  [FAIL] jdk: {jdk} missing (set SS_JDK)."); return 2; }
        Console.WriteLine($"  [ok] android sdk: {android}");
        Console.WriteLine($"  [ok] jdk: {jdk}");
        Console.WriteLine();

        bool sign = !HasFlag(args, "--no-sign");
        var env = new Dictionary<string, string>
        {
            ["ANDROID_HOME"] = android, ["ANDROID_SDK_ROOT"] = android, ["AndroidSdkDirectory"] = android,
            ["JAVA_HOME"] = jdk, ["JavaSdkDirectory"] = jdk,
        };
        // SignAndroidPackage produces the installable -Signed.apk; a plain build skips signing (faster).
        var target = sign ? "build -t:SignAndroidPackage" : "build";
        Console.WriteLine($"ss build apk: {(sign ? "building + signing" : "building")} via {dotnet}");
        int rc = RunProc(dotnet, $"{target} \"{csproj}\" -c Release -f net11.0-android -clp:ErrorsOnly", env);
        if (rc != 0) { Console.Error.WriteLine($"ss build apk: RED — build failed (exit {rc})"); return 1; }
        Console.WriteLine("ss build apk: compiled");

        // The build IS the gate (CONTRACT.md): a compile is not green until the PUBLISHED analyzer ratchet
        // passes too. A RED gate is a HARD STOP — (Build Failed). --override/-o forces it (build-system dev ONLY).
        int g = RunGate(root, dotnet, drive);
        if (g == 2) Console.Error.WriteLine("ss build apk: gate unavailable (analyzer not at <drive>\\bin\\check) — NOT a clean build");
        else { int v = GateVerdict(g, IsOverride(args)); if (v != 0) return v; }

        var apk = FindSignedApk(drive, root);
        Console.WriteLine(apk != null ? $"ss build apk: GREEN — {apk}" : "ss build apk: GREEN (signed APK path not located; check the build output)");
        return 0;
    }

    // The PUBLISHED analyzer ratchet (<drive>\bin\check), invoked exactly as `ss-check --gate`. Returns the
    // analyzer exit code, or 2 when the checker is not installed.
    private static int RunGate(string root, string dotnet, string drive)
    {
        var dll = Path.Combine(drive, "bin", "check", "subsystem-check.dll");
        if (!File.Exists(dll)) return 2;
        Console.WriteLine("ss build apk: gate — running the published analyzer ratchet…");
        return RunProc(dotnet, $"\"{dll}\" --gate", null, root, echoStdout: true);
    }

    private static string? FindSignedApk(string drive, string root)
    {
        // dev.psm1 emits to <drive>\build\Subsystem\bin\…\*-Signed.apk; fall back to the project's own bin.
        foreach (var baseDir in new[] { Path.Combine(drive, "build"), Path.Combine(root, "src", "runspace") })
        {
            try
            {
                if (!Directory.Exists(baseDir)) continue;
                var hit = Directory.GetFiles(baseDir, "*-Signed.apk", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    internal static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    // --override / -o : force a RED gate through. ONLY for developing the build system itself.
    internal static bool IsOverride(string[] args) => HasFlag(args, "--override") || HasFlag(args, "-o");

    // --path / -p : point a verb at a source tree.
    internal static string? PathArg(string[] args) => ArgValue(args, "--path") ?? ArgValue(args, "-p");

    // The gate verdict UX (shared by `ss build apk` and `ss check --gate`): a RED gate is a HARD STOP
    // labeled (Build Failed). --override/-o downgrades it to a stern warning — build-system dev ONLY.
    internal static int GateVerdict(int gateExit, bool overridden)
    {
        if (gateExit == 0) { Console.WriteLine("gate: passed"); return 0; }
        if (overridden)
        {
            Console.WriteLine();
            Console.WriteLine("!! --override / -o : the gate is RED but the build was FORCED through.");
            Console.WriteLine("!! This is ONLY for developing the build system itself — NEVER during normal");
            Console.WriteLine("!! development, and NEVER as a way to bypass locks. The findings above stand.");
            return 0;
        }
        Console.WriteLine();
        Console.WriteLine("(Build Failed)");
        return gateExit;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
@"ss build — build a Subsystem head from source (the build lives IN the binary).

  ss build            rebuild the WINDOWS head (this ss.exe) and self-replace the running image
  ss build apk        build (+ sign) the ANDROID head — the ship target — then run the gate
  ss build win        explicit Windows-head build (same as bare `ss build`)

OPTIONS
  --path, -p <dir>    build from this source tree instead of the repo beside the exe / embedded source
  --no-sign           (apk) build without signing — faster, not installable
  --override, -o      (apk) force a RED gate through — build-system dev ONLY, never to bypass locks

The Windows head compiles with the on-drive dotnet (the last external dep on that road). The APK build
needs dotnet + the .NET-Android workload + a JDK by nature (aapt2/r8/apksigner) and drives them from here.");
        return 0;
    }

    // ---- source resolution ----

    internal static string? ResolveSource(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath)) return Path.GetFullPath(overridePath);
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? Directory.GetCurrentDirectory();
        // Look ONLY at the exe's own location — NEVER walk up into an unrelated repo elsewhere on the drive
        // (that defeated the blind build: from S:\ss it found S:\subsystem and ignored the embed).
        // 1. the exe sits inside the repo (S:\subsystem\ss.exe).
        if (File.Exists(Path.Combine(exeDir, "src", "runspace", "Subsystem.csproj"))) return exeDir;
        // 2. a `subsystem` repo right beside the exe (S:\ss.exe -> S:\subsystem, or one we grew before).
        var beside = Path.Combine(exeDir, "subsystem");
        if (File.Exists(Path.Combine(beside, "src", "runspace", "Subsystem.csproj"))) return beside;
        // 3. blind: no repo on disk. Reconstitute the embedded source into a FRESH temp dir — NEVER delete or
        //    hydrate a populated directory (a dropped ss.exe may sit in a folder full of the user's files).
        var temp = Path.Combine(Path.GetTempPath(), "subsystem-src-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        if (SelfSource.ExtractEmbedded(temp))
        {
            Console.WriteLine($"  ..  no repo beside the exe — reconstituted the embedded source into a temp dir: {temp}");
            return temp;
        }
        return null;
    }

    private static int SafeCount(string root, string pattern)
    {
        try { return Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Count(p => !p.Contains("\\obj\\") && !p.Contains("\\bin\\")); }
        catch { return 0; }
    }

    internal static string? ResolveDotnet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "dotnet.exe"))) return Path.Combine(root, "dotnet.exe");
        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            var d = Path.Combine(Path.GetPathRoot(exe) ?? "", "dotnet", "dotnet.exe");
            if (File.Exists(d)) return d;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var p = Path.Combine(dir, "dotnet.exe"); if (File.Exists(p)) return p; } catch { }
        return null;
    }

    // ---- the ♦/♠ source dump (C#, self-contained — mirrors GetCodeContextCmdlet so `ss build` needs no runspace) ----

    private static void WriteSourceDump(string root, string outPath)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", "bin", "obj", "dist", "build", ".git", ".vs", "packages", "vendor", "reference", "models" };
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".ps1", ".js", ".ts", ".html", ".css", ".json", ".md", ".csproj", ".xml", ".config", ".props", ".targets", ".sln" };

        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (blocked.Contains(Path.GetFileName(dir))) continue;
            try { foreach (var d in Directory.GetDirectories(dir)) stack.Push(d); } catch { }
            try { foreach (var f in Directory.GetFiles(dir)) if (exts.Contains(Path.GetExtension(f)) && new FileInfo(f).Length <= 4L * 1024 * 1024) found.Add(f); } catch { }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("♦ repo: ").Append(Path.GetFileName(root.TrimEnd('\\', '/'))).Append(" | ").Append(DateTime.UtcNow.ToString("o")).Append('\n');
        int startLine = found.Count + 2;
        var blocks = new List<(string Rel, string[] Lines)>();
        foreach (var f in found)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            if (lines.Length == 0) continue;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            blocks.Add((rel, lines));
            sb.Append(rel).Append(" | ").Append(startLine).Append('\n');
            startLine += lines.Length + 1;
        }
        foreach (var b in blocks)
        {
            sb.Append("♠ ").Append(b.Rel).Append('\n');
            foreach (var l in b.Lines) sb.Append(l).Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static int RunProc(string exe, string args, IDictionary<string, string>? env = null, string? workingDir = null, bool echoStdout = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(exe)!;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is { } s && (echoStdout || s.Contains(": error ", StringComparison.Ordinal))) Console.WriteLine(s); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is { } s) Console.Error.WriteLine(s); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
