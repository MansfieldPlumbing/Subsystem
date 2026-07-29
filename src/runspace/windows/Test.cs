using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Subsystem.Cm;
using Subsystem.Remedy;

namespace Subsystem.Windows;

// `ss test` — run the tests/ receipts in-proc and DERIVE the verdict from each run, then (optionally) write
// that verdict to a Remedy request's EOS log. The receipt the run prints is the authority (tests/README),
// so this just collects each [pscustomobject]{test,pass,verdict}, tallies, and records to smoketest-log.md
// exactly as `ss selftest` does for the kernel. --request appends the SAME receipt to the durable request
// plane (RequestHive) so a graduation is proven by a logged receipt, never a hand-typed claim — no manual
// copy-paste of test output into Add-EosLog.
//   ss test                          run every tests/test.*.ps1, print + record the receipts
//   ss test vom                      only receipts whose filename contains "vom"
//   ss test --bench                  include the bench.*.ps1 benchmarks too
//   ss test <filter> --request 145 [--disposition "..."]   also append the receipts to request #145's EOS log
internal static class Test
{
    public static int Run(string[] args)
    {
        var root = SmokeLog.ResolveRepoRoot(Build.PathArg(args));
        if (root == null) { Console.Error.WriteLine("ss test: no repo beside the exe (pass --path <repo>)."); return 2; }
        var testsDir = Path.Combine(root, "tests");
        if (!Directory.Exists(testsDir)) { Console.Error.WriteLine($"ss test: no tests directory at {testsDir}."); return 2; }

        bool bench   = Build.HasFlag(args, "--bench") || Build.HasFlag(args, "--all");
        long? request = RequestId(args);
        string? disposition = ArgValue(args, "--disposition") ?? ArgValue(args, "-d");
        string? filter = PositionalFilter(args);

        // Select receipts: test.*.ps1 always; bench.*.ps1 only with --bench. A positional filter is a
        // case-insensitive filename substring (e.g. "vom", "test.vom.refcount").
        var files = Directory.GetFiles(testsDir, "*.ps1")
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                bool isTest  = n.StartsWith("test.",  StringComparison.OrdinalIgnoreCase);
                bool isBench = n.StartsWith("bench.", StringComparison.OrdinalIgnoreCase);
                return isTest || (isBench && bench);
            })
            .Where(f => filter == null || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) { Console.Error.WriteLine($"ss test: no receipts matched{(filter != null ? $" '{filter}'" : "")}."); return 2; }

        // ISS template: full built-ins + the project cmdlets, built once. Each receipt then runs in its OWN
        // fresh runspace + host (RunOne), so a non-conformant receipt that calls `exit` or faults cannot poison
        // the next — the same isolation `ss run <one script>` already gives, applied per receipt across the suite.
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        Shim.LoadProjectCmdlets(iss);

        var results = new List<TestResult>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Console.WriteLine();
            Console.WriteLine($"-- {name} --");
            results.Add(RunOne(iss, file, name));
        }

        int pass = results.Count(r => r.Status == "PASS");
        int fail = results.Count(r => r.Status == "FAIL");
        int skip = results.Count(r => r.Status == "SKIP");
        bool green = fail == 0;

        Console.WriteLine();
        Console.WriteLine($"ss test — {pass} pass / {fail} fail / {skip} skip  ({results.Count} receipts)");
        foreach (var r in results)
            Console.WriteLine($"  {r.Status,-4} {r.Name}{(r.Verdict.Length > 0 ? " — " + r.Verdict : "")}");
        Console.WriteLine();
        Console.WriteLine(green ? "GREEN — no failing receipt" : $"RED — {fail} failing receipt(s)");

        // Record to the append-only smoke ledger (same mechanism as `ss selftest`/`ss check --gate`), so the
        // green baseline is derived from this run and a GREEN->RED flip is a tracked regression.
        var details = results.Select(r => $"{r.Status} {r.Name}{(r.Verdict.Length > 0 ? " — " + r.Verdict : "")}").ToList();
        SmokeLog.Record(root, "test" + (filter != null ? " " + filter : ""), green, details);

        // --request: append the receipt to the request's EOS log — the auto-log into Remedy.
        if (request.HasValue)
            LogToRequest(request.Value, disposition, filter, pass, fail, skip, results);

        return green ? 0 : 1;
    }

    private sealed record TestResult(string Name, string Status, string Verdict);

    // Run ONE receipt in its own fresh runspace, exactly as `pwsh -File` would (AddCommand on the path, so
    // $PSScriptRoot/$PSCommandPath resolve). Classify by the receipt object the run emits: a [pscustomobject]
    // carrying a `pass` field is PASS/FAIL; a clean SKIP returns no such object. A fresh host per receipt keeps
    // an `exit` or fault contained to that receipt.
    private static TestResult RunOne(InitialSessionState iss, string file, string name)
    {
        var host = new ConsoleHost();
        var rs = RunspaceFactory.CreateRunspace(host, iss);
        rs.Open();
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddCommand(file, useLocalScope: false);
            var output = ps.Invoke();

            PSObject? receipt = null;
            foreach (var o in output)
                if (o != null && o.Properties["pass"] != null) receipt = o;   // last receipt wins

            if (receipt != null)
            {
                bool pass;
                try { pass = Convert.ToBoolean(receipt.Properties["pass"].Value); }
                catch { pass = false; }
                string verdict = receipt.Properties["verdict"]?.Value?.ToString() ?? "";
                return new TestResult(name, pass ? "PASS" : "FAIL", verdict);
            }

            // No receipt object: a clean SKIP (the test returned early) vs a real fault. Native commands
            // (dp-onnx, dotnet, git) chatter on stderr while succeeding — PowerShell surfaces that as
            // NativeCommandError records that are NOT a failure (the Shim F9 rule); ignore them. A genuine
            // PowerShell error, or a non-zero `exit`, is a fault.
            if (host.ExitCode is int ec && ec != 0)
                return new TestResult(name, "FAIL", $"non-conforming: exited {ec} without a receipt");
            var realError = ps.Streams.Error.FirstOrDefault(e =>
                !(e.FullyQualifiedErrorId ?? string.Empty).Contains("NativeCommand", StringComparison.OrdinalIgnoreCase));
            if (realError != null)
                return new TestResult(name, "FAIL", "faulted: " + Truncate(realError.ToString(), 160));
            return new TestResult(name, "SKIP", "no receipt (skipped or external dep absent)");
        }
        catch (Exception ex)
        {
            return new TestResult(name, "FAIL", "threw: " + Truncate(ex.Message, 160));
        }
        finally { rs.Dispose(); }
    }

    // Append the run's receipts to request #id's EOS log. Disposition defaults to a derived verdict
    // (verified / regression) when not given; the body is the per-receipt list, terse and factual.
    private static void LogToRequest(long id, string? disposition, string? filter, int pass, int fail, int skip,
                                     List<TestResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("ss test").Append(filter != null ? " " + filter : "")
          .Append(" — ").Append(pass).Append(" pass / ").Append(fail).Append(" fail / ").Append(skip).Append(" skip\n");
        foreach (var r in results)
            sb.Append("- ").Append(r.Status).Append(' ').Append(r.Name)
              .Append(r.Verdict.Length > 0 ? " — " + r.Verdict : "").Append('\n');

        var disp = !string.IsNullOrWhiteSpace(disposition) ? disposition!
                 : fail == 0 ? $"verified ({pass} green, {skip} skip)"
                 : $"regression ({fail} failing)";
        try
        {
            var entry = RequestHive.WriteEosLog(id, disp, sb.ToString().TrimEnd());
            Console.WriteLine(entry == null
                ? $"ss test: no request #{id} — receipt not logged."
                : $"ss test: receipt appended to request #{id} EOS log [{disp}].");
        }
        catch (Exception ex) { Console.Error.WriteLine("ss test: EOS log not written — " + ex.Message); }
    }

    private static long? RequestId(string[] args)
    {
        var v = ArgValue(args, "--request") ?? ArgValue(args, "-r");
        return long.TryParse(v, out var id) ? id : (long?)null;
    }

    // The first bare token that is not a flag and not a flag's value — the optional filename filter.
    private static string? PositionalFilter(string[] args)
    {
        var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "--request", "-r", "--disposition", "-d", "--path", "-p" };
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (valueFlags.Contains(a)) { i++; continue; }
            if (a.StartsWith("-", StringComparison.Ordinal)) continue;
            return a;
        }
        return null;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
