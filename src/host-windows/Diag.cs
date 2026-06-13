using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Subsystem.Windows;

// One fundamental's verdict: a name, pass/fail, and the raw JSON detail logged verbatim to the ledger.
internal readonly record struct DiagResult(string Name, bool Pass, string Detail);

// `ss diag` — the living diagnostic suite. A growing set of fundamental tests that ENSHRINE the smoke
// tests: the VOM/Cm kernel self-tests (Layers 1-2, shared with `ss selftest`) plus toolchain and
// self-carry checks. Every run records one suite verdict to smoketest-log.md (SmokeLog) so the green
// baseline is derived. Adding a fundamental = appending one DiagResult to Run — that is the whole API.
// `--path <repo>` picks the tree to log into (default: the repo beside the exe).
internal static class Diag
{
    public static int Run(string[] args)
    {
        Console.WriteLine("ss — Windows head (diagnostic suite)");

        var results = new List<DiagResult>();
        results.AddRange(SelfTest.Kernel());     // Layers 1-2: VOM kernel + Cm registry (shared producer)
        results.Add(DotnetPresent());            // the compiler for self-build (last external dep)
        results.Add(GatePublished());            // the anti-slop ratchet is installed
        results.Add(EmbeddedSourceWellFormed()); // the ouroboros: ss carries its own source, parseable
        results.Add(IconResource());             // the launcher mark rides inside the binary

        bool ok = true;
        foreach (var r in results) { Console.WriteLine(); ok &= Print(r); }

        Console.WriteLine();
        Console.WriteLine($"diag: {results.Count(r => r.Pass)}/{results.Count} green");
        Console.WriteLine(ok ? "DIAG GREEN — fundamentals hold on the Windows head" : "DIAG RED — see FAIL above");

        SmokeLog.Record(SmokeLog.ResolveRepoRoot(Build.PathArg(args)), "diag", ok, Details(results));
        return ok ? 0 : 1;
    }

    // Print one verdict: a labelled header, the raw JSON, and a FAIL marker when red. Shared by `ss diag`
    // and `ss selftest` so both render identically.
    internal static bool Print(DiagResult r)
    {
        Console.WriteLine($"--- {r.Name} ---");
        Console.WriteLine(r.Detail);
        if (!r.Pass) Console.WriteLine($"  FAIL: {r.Name}");
        return r.Pass;
    }

    // Verdicts formatted for the ledger: one "Name: {json}" line each, verbatim machine output.
    internal static IEnumerable<string> Details(IEnumerable<DiagResult> results) =>
        results.Select(r => $"{r.Name}: {r.Detail}");

    // ---- toolchain fundamentals ----

    static DiagResult DotnetPresent()
    {
        var dotnet = Build.ResolveDotnet();
        return new DiagResult("Toolchain.Dotnet", dotnet != null,
            JsonSerializer.Serialize(new { found = dotnet != null, path = dotnet }));
    }

    static DiagResult GatePublished()
    {
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? Directory.GetCurrentDirectory()) ?? "";
        var dll = Path.Combine(drive, "bin", "check", "subsystem-check.dll");
        bool installed = File.Exists(dll);
        return new DiagResult("Toolchain.Gate", installed,
            JsonSerializer.Serialize(new { installed, dll }));
    }

    // ---- self-carry fundamentals (the ouroboros holds) ----

    static DiagResult EmbeddedSourceWellFormed()
    {
        var text = SelfSource.EmbeddedDumpText();
        bool present = text != null;
        int fileBlocks = 0;
        if (text != null)
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                if (line.StartsWith("♠ ", StringComparison.Ordinal)) fileBlocks++;  // column-0 file-boundary delimiters
        bool ok = present && fileBlocks >= 20;   // a real tree, not a stub
        return new DiagResult("SelfCarry.EmbeddedSource", ok,
            JsonSerializer.Serialize(new { present, fileBlocks }));
    }

    static DiagResult IconResource()
    {
        var ico = SelfSource.EmbeddedIconBytes();
        int bytes = ico?.Length ?? 0;
        return new DiagResult("SelfCarry.Icon", bytes > 0,
            JsonSerializer.Serialize(new { embeddedIconBytes = bytes }));
    }
}
