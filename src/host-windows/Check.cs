using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Subsystem.Windows;

// `ss check` — run the anti-drift / anti-slop analyzer ratchet (Subsystem.Analyzers, SS000-SS017) from the
// binary, so reading the gate state never requires pwsh or dev.psm1. Drives the PUBLISHED checker
// (<drive>\bin\check\subsystem-check.dll) exactly as the build gate does; cwd is the source root so the
// analyzer scans the project. Flags after `check` pass straight through: `ss check` lists every finding,
// `ss check --gate` is the fail-closed ratchet (NEW violations above the baseline only). `--path <dir>`
// picks the source tree (default: the repo beside the exe, or the embedded source in a temp dir).
internal static class Check
{
    public static int Run(string[] args)
    {
        bool list = Build.HasFlag(args, "--list");

        var dotnet = Build.ResolveDotnet();
        if (dotnet == null) { Console.Error.WriteLine("ss check: no dotnet to run the checker (DOTNET_ROOT / <drive>\\dotnet / PATH)."); return 2; }

        // The toolchain (bin\check, dotnet) lives on the exe's drive, not necessarily the source drive.
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? Directory.GetCurrentDirectory()) ?? "";
        var dll = Path.Combine(drive, "bin", "check", "subsystem-check.dll");
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"ss check: the published checker is not installed at {dll}.");
            Console.Error.WriteLine("Publish it once:  dotnet publish src/tools/SubsystemCheck -o <drive>\\bin\\check");
            return 2;
        }

        // --list just enumerates the analyzer roster — no project load, no source needed.
        string? root = null;
        if (!list)
        {
            root = Build.ResolveSource(Build.PathArg(args));
            if (root == null) { Console.Error.WriteLine("ss check: no source (no repo beside the exe, no embedded source, no --path)."); return 2; }
        }

        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = root ?? Path.GetDirectoryName(dotnet)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        // Forward checker flags (--gate, --list, --refs, …); drop the ss-level flags the checker does not
        // know — --path/-p carry a value, --override/-o are ours.
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--path", StringComparison.OrdinalIgnoreCase) || a.Equals("-p", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (a.Equals("--override", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase)) continue;
            psi.ArgumentList.Add(a);
        }
        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;

        if (!list) Console.WriteLine($"ss check: {Path.GetFileName(dll)} over {root}");
        // Capture the checker's `gate:` summary lines (findings/baseline/new/retired + the verdict) so the
        // smoke-log carries the real numbers, not a paraphrase.
        var gateLines = new List<string>();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } s)
            {
                Console.WriteLine(s);
                if (s.StartsWith("gate:", StringComparison.Ordinal)) gateLines.Add(s.Trim());
            }
        };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is { } s) Console.Error.WriteLine(s); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        int code = p.ExitCode;

        // --gate is the smoke test for the analyzer ratchet: record its verdict (DERIVED from this run) before
        // applying the hard-stop UX. pass = the true gate result (code 0), even when --override forces a red
        // build through — the log tells the truth regardless.
        if (Build.HasFlag(args, "--gate"))
        {
            if (gateLines.Count == 0) gateLines.Add($"gate: exit {code} (no summary line captured)");
            SmokeLog.Record(root, "check --gate", code == 0, gateLines);
        }

        // The gate is a HARD STOP: a red --gate prints (Build Failed). --override/-o downgrades to a warning.
        if (Build.HasFlag(args, "--gate") && code != 0)
            return Build.GateVerdict(code, Build.IsOverride(args));
        return code;
    }
}
