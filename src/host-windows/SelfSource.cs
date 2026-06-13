using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Subsystem.Windows;

// ss carries its OWN source. `ss extract [dir]` writes the embedded ♦/♠ source dump back out as a real
// tree (the project's own Get-CodeContext / Restore-CodeContext format), so a dropped ss.exe can
// reconstitute the code it was built from — no repo required. Paired with the embedded Roslyn
// (Microsoft.CodeAnalysis.CSharp, verified present) and the self-contained runtime, source + compiler +
// runtime all live in one binary — the road to a self-sufficient Windows head (APK-untouched: this is
// host-windows only).
internal static class SelfSource
{
    public static int Extract(string[] args)
    {
        string dir = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))
                     ?? Path.Combine(Directory.GetCurrentDirectory(), "ss-source");
        var text = ReadResourceText("ss-source.dump");
        if (text == null)
        {
            Console.Error.WriteLine("ss extract: this build carries no embedded source (it was built without the dump). " +
                                    "Rebuild via `ss -File scripts/build-ss.ps1`, which dumps + embeds the source.");
            return 2;
        }
        try
        {
            var full = Path.GetFullPath(dir);
            if (IsPopulated(full)) { Console.Error.WriteLine($"ss extract: refusing to hydrate a populated directory: {full} — choose a fresh/empty target."); return 2; }
            int written = Restore(text, full);
            RestoreBinaryAssets(full);
            Console.WriteLine($"ss extract: {written} files written to {full}");
            Console.WriteLine($"Verify it:  ss describe --map --path \"{full}\"");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ss extract: " + ex.Message); return 1; }
    }

    // Reconstitute the embedded source into <dir> (used by `ss build` when there is no repo on disk).
    // Returns false when this build carries no embedded source.
    public static bool ExtractEmbedded(string dir)
    {
        var text = ReadResourceText("ss-source.dump");
        if (text == null) return false;
        var full = Path.GetFullPath(dir);
        if (IsPopulated(full)) throw new IOException($"refusing to reconstitute source into a populated directory: {full} (extract only into a fresh/empty dir)");
        Restore(text, full);
        RestoreBinaryAssets(full);
        return true;
    }

    // Standing rule: source reconstitution must NEVER hydrate into a populated directory — a dropped ss.exe
    // may sit in a folder full of the user's files. Extract only into a fresh or empty target.
    private static bool IsPopulated(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    // Binary assets can't ride in the TEXT ♦/♠ dump, so they are embedded as their own resources and written
    // back to their known repo paths here. Today that is just the launcher icon, so a blind rebuild (no repo)
    // still satisfies <ApplicationIcon> and produces an icon-bearing exe. Cosmetic — never fail over it.
    private static void RestoreBinaryAssets(string destFull)
    {
        var ico = ReadResourceBytes("app.ico");
        if (ico == null) return;
        var target = Path.Combine(destFull, "src", "host-windows", "app.ico");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, ico);
        }
        catch { }
    }

    private static byte[]? ReadResourceBytes(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // Parse the ♦/♠ CodeContext dump and write each block to <dir>/<relpath>. Mirrors RestoreCodeContextCmdlet
    // (the dogfood format) with a directory-traversal guard. Pure C#, no runspace needed.
    private static int Restore(string dumpText, string destFull)
    {
        Directory.CreateDirectory(destFull);
        var lines = dumpText.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("♠ ", StringComparison.Ordinal)) i++;   // skip header + index

        int count = 0;
        string? cur = null;
        var buf = new List<string>();
        void Flush()
        {
            if (cur == null) return;
            var target = Path.GetFullPath(Path.Combine(destFull, cur.Replace('/', Path.DirectorySeparatorChar)));
            if (target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))   // refuse ../ escapes
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllLines(target, buf);
                count++;
            }
            cur = null;
            buf.Clear();
        }
        for (; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("♠ ", StringComparison.Ordinal)) { Flush(); cur = lines[i].Substring(2).Trim(); }
            else if (cur != null) buf.Add(lines[i]);
        }
        Flush();
        return count;
    }

    // Suite-facing accessors (the `ss diag` self-carry fundamentals): the embedded source dump and icon,
    // the payloads that let a dropped ss.exe reconstitute itself. Null when this build carries neither.
    internal static string? EmbeddedDumpText() => ReadResourceText("ss-source.dump");
    internal static byte[]? EmbeddedIconBytes() => ReadResourceBytes("app.ico");

    private static string? ReadResourceText(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
