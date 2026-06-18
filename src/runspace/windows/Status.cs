using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Subsystem.Windows;

// `ss status` (alias `ss st`) — the polished "where am I" dashboard, ONE clean command instead of a noisy
// Write-Output/2>&1/Select-Object dance: branch · working-tree changes · recent commits · the gate baseline.
// ss does the machine's job (resolve git, capture, format, page) so you READ, not compose. Default path = CWD.
internal static class Status
{
    public static int Run(string[] args)
    {
        var root = args.FirstOrDefault(a => !a.StartsWith("-")) ?? Directory.GetCurrentDirectory();
        root = Path.GetFullPath(root);

        Console.WriteLine($"ss status · {root}");
        var branch = Git(root, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Console.WriteLine($"  branch:  {(branch.Length == 0 ? "(no repo here)" : branch)}");

        if (branch.Length > 0)
        {
            var dirty = Git(root, "status", "--short").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            Console.WriteLine($"  changes: {(dirty.Length == 0 ? "clean" : dirty.Length + " file(s)")}");
            foreach (var l in dirty.Take(12)) Console.WriteLine("    " + l.TrimEnd());
            if (dirty.Length > 12) Console.WriteLine($"    … +{dirty.Length - 12} more");

            Console.WriteLine("  recent:");
            foreach (var l in Git(root, "log", "--oneline", "-5").Split('\n').Where(l => l.Trim().Length > 0).Take(5))
                Console.WriteLine("    " + l.TrimEnd());
        }

        // the gate baseline — the smoke ledger's last verdict line (written BY the binary, never re-frozen).
        var log = Path.Combine(root, "smoketest-log.md");
        if (File.Exists(log))
        {
            var gate = File.ReadLines(log).Where(l => l.Contains("GREEN") || l.Contains("RED") || l.Contains("gate:")).LastOrDefault();
            if (gate != null) Console.WriteLine("  gate:    " + gate.Trim().TrimStart('-', ' '));
        }
        return 0;
    }

    private static string Git(string root, params string[] args)
    {
        var git = Subsystem.Windows.Git.Resolve();
        if (git == null) return "";
        var psi = new ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o; }
        catch { return ""; }
    }
}
