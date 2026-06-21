using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Subsystem.Windows;

// Append-only smoke-test ledger. `ss selftest` and `ss check --gate` record their verdict here so the
// green baseline is DERIVED from each run, never hand-copied into a handoff (a handoff that says "passed"
// with no numbers is the gap this closes — next-session has nothing concrete to diff a regression against).
// The file lives at <sourceRoot>\smoketest-log.md, committed beside the code it certifies; the memory index
// just points at it and carries the latest green line. Newest entry at the bottom; a GREEN->RED flip on any
// field is a real regression. Best-effort by construction: a logging failure never fails the test.
internal static class SmokeLog
{
    public const string FileName = "smoketest-log.md";

    private const string Header =
        "# Subsystem smoke-test log\n\n" +
        "Append-only ledger, written BY the binary (`ss selftest`, `ss check --gate`) so the green baseline is\n" +
        "DERIVED from each run, never hand-copied into a handoff. Newest entries at the bottom. A GREEN->RED flip\n" +
        "on any field is a real regression, not a styling nit.\n";

    // Append one entry under `root` (the resolved source tree). `details` are the already-formatted lines —
    // raw JSON verdicts for selftest, the `gate:` summary for check — kept verbatim so the log is the real
    // machine output, not a paraphrase. A null/absent root means there is nowhere to record (a dropped exe
    // with no repo beside it); say so once rather than failing silently.
    public static void Record(string? root, string command, bool pass, IEnumerable<string> details)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.WriteLine("smoke-log: not recorded — no repo to write into (pass --path <repo>).");
            return;
        }
        try
        {
            var path = Path.Combine(root, FileName);
            var sb = new StringBuilder();
            if (!File.Exists(path)) sb.Append(Header);
            sb.Append('\n')
              .Append("## ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
              .Append(" · ").Append(command)
              .Append(" · ").Append(pass ? "GREEN" : "RED").Append('\n');
            foreach (var d in details) sb.Append("- ").Append(d).Append('\n');
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"smoke-log: recorded {command} ({(pass ? "GREEN" : "RED")}) -> {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("smoke-log: not recorded — " + ex.Message);
        }
    }

    // Find the on-disk repo to log into WITHOUT the embedded-source extraction Build.ResolveSource performs —
    // reconstituting a whole tree just to append a log line is the wrong trade. Mirrors the on-disk arms of
    // Build.ResolveSource (explicit --path, the exe inside the repo, a `subsystem` repo beside the exe) and
    // stops there: no repo on disk returns null, and Record reports the skip.
    internal static string? ResolveRepoRoot(string? pathArg)
    {
        if (!string.IsNullOrWhiteSpace(pathArg) && Directory.Exists(pathArg)) return Path.GetFullPath(pathArg);
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(exeDir, "src", "runspace", "Subsystem.csproj"))) return exeDir;
        var beside = Path.Combine(exeDir, "subsystem");
        if (File.Exists(Path.Combine(beside, "src", "runspace", "Subsystem.csproj"))) return beside;
        return null;
    }
}
