using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VomKernel = Subsystem.Vom.Vom;   // the class, not the namespace (avoids the Subsystem.Vom ambiguity)

namespace Subsystem.Windows;

// Layers 1-2: the VOM kernel + Cm registry, run and observed. The verdicts come from Kernel() (shared with
// the `ss diag` suite) and are logged to smoketest-log.md, so a false field is a tracked regression, not a
// styling problem. `--path <repo>` picks the tree to log into (default: the repo beside the exe).
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        Console.WriteLine("ss — Windows head (self-test)");
        var results = Kernel();
        bool ok = true;
        foreach (var r in results) { Console.WriteLine(); ok &= Diag.Print(r); }

        Console.WriteLine();
        Console.WriteLine(ok ? "LAYERS 1-2 GREEN — VOM kernel + Cm registry run on the Windows head" : "RED — see FAIL fields above");
        SmokeLog.Record(SmokeLog.ResolveRepoRoot(Build.PathArg(args)), "selftest", ok, Diag.Details(results));
        return ok ? 0 : 1;
    }

    // The Layer 1-2 fundamentals as verdicts (no printing, no logging) so both `ss selftest` and the
    // `ss diag` suite consume the same source of truth instead of two drifting copies.
    internal static List<DiagResult> Kernel() => new()
    {
        Check("Vom.SelfTest", VomKernel.SelfTest(), "fenceWorks", "ownerRemoved", "staleHandleRejected"),
        Check("Vom.SpawnKillTest", VomKernel.SpawnKillTest(), "rootRemoved", "childRemoved", "grandchildRemoved", "childObservedCancel"),
        Check("Vom.WaitPhaseLockTest", VomKernel.WaitPhaseLockTest(), "waitAnyCorrect", "barrierHeldForLaggard", "phaseLocked"),
        Check("Cm.SelfTest", JsonSerializer.Serialize(Subsystem.Cm.Cm.SelfTest()), "ok", "inMemory", "inDurable"),
        Rehydration(),
    };

    // Durable-plane check: register a marker and confirm it lands in the registry list. rehydratedFromPriorRun
    // is informational (false on the first-ever run, true thereafter — both legitimate), so the pass criterion
    // is markerPresent, not rehydration.
    static DiagResult Rehydration()
    {
        const string marker = "\\Capability\\Probe\\WinHeadBoot";
        bool rehydrated = Subsystem.Cm.Cm.Get(marker) != null;
        Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
        {
            Path = marker, Name = "WinHeadBoot", Type = "Probe", Integrity = "System",
            StartType = "manual", Enabled = true,
        });
        var records = Subsystem.Cm.Cm.List();
        bool present = records.Any(r => r.Path == marker);
        var detail = JsonSerializer.Serialize(new
        {
            dbPath = Subsystem.Cm.Cm.DbPath,
            total = records.Length,
            markerPresent = present,
            rehydratedFromPriorRun = rehydrated,
        });
        return new DiagResult("Cm.Rehydration", present, detail);
    }

    // Parse a JSON verdict and assert the named fields are literally true. The verdict JSON itself is the
    // detail, so a red field (e.g. "fenceWorks":false) is visible in the printed/logged output.
    static DiagResult Check(string name, string json, params string[] mustBeTrue)
    {
        using var doc = JsonDocument.Parse(json);
        bool pass = true;
        foreach (var field in mustBeTrue)
            if (!doc.RootElement.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.True)
                pass = false;
        return new DiagResult(name, pass, json);
    }
}
