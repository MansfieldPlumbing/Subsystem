using System;
using System.Diagnostics;
using System.IO;

namespace Subsystem.Windows;

// `ss git <args...>` — git through ss, using the SANCTIONED git: S:\bin\git\cmd\git.exe (the launcher), NEVER
// git\usr\bin (so bash/grep/sed stay unreachable — the no-bash floor holds). The pragmatic bridge while the
// native libgit2 write-verbs are built; it pairs with Get-GitGraphContext (git as read-only OBJECTS). git
// inherits ss's console (no capture), so output is exactly `git`'s; ss returns git's own exit code.
internal static class Git
{
    public static int Run(string[] args)
    {
        var git = Resolve();
        if (git == null)
        {
            Console.Error.WriteLine("ss git: git not found. Looked at SS_GIT, <drive>\\bin\\git\\cmd\\git.exe, then PATH.");
            Console.Error.WriteLine("  (the agent PATH is intentionally naive — `ss git` is how you reach the sanctioned git)");
            return 2;
        }
        var psi = new ProcessStartInfo(git) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { using var p = Process.Start(psi)!; p.WaitForExit(); return p.ExitCode; }
        catch (Exception ex) { Console.Error.WriteLine("ss git: " + ex.Message); return 1; }
    }

    // SS_GIT override → the sanctioned <drive>\bin\git\cmd\git.exe → a PATH fallback (a human session may have it).
    // Always git\cmd\git.exe (the porcelain launcher), never git\usr\bin\* (the MSYS unix tools — banned).
    internal static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("SS_GIT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? "") ?? "S:\\";
        var sanctioned = Path.Combine(drive, "bin", "git", "cmd", "git.exe");
        if (File.Exists(sanctioned)) return sanctioned;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var g = Path.Combine(dir, "git.exe"); if (File.Exists(g)) return g; } catch { }
        return null;
    }
}
