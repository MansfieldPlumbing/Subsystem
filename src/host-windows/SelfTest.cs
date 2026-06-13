using System;
using System.Linq;
using System.Text.Json;
using VomKernel = Subsystem.Vom.Vom;   // the class, not the namespace (avoids the Subsystem.Vom ambiguity)

namespace Subsystem.Windows;

// Layers 1-2: the VOM kernel + Cm registry, run and observed. Both self-tests return JSON verdicts;
// a false field is a real kernel/registry regression, not a styling problem.
internal static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("sswin — Windows head (self-test)");
        Console.WriteLine();
        bool ok = true;

        Console.WriteLine("--- Vom.SelfTest (native alloc, fence, teardown, stale-handle rejection) ---");
        ok &= RunTest(VomKernel.SelfTest(), "fenceWorks", "ownerRemoved", "staleHandleRejected");

        Console.WriteLine();
        Console.WriteLine("--- Vom.SpawnKillTest (cascade Terminate down the owner tree) ---");
        ok &= RunTest(VomKernel.SpawnKillTest(), "rootRemoved", "childRemoved", "grandchildRemoved", "childObservedCancel");

        Console.WriteLine();
        Console.WriteLine("--- Cm.SelfTest (register probe, confirm in BOTH planes, unregister) ---");
        ok &= RunTest(JsonSerializer.Serialize(Subsystem.Cm.Cm.SelfTest()), "ok", "inMemory", "inDurable");

        Console.WriteLine();
        Console.WriteLine("--- Cm rehydration (durable marker across process restarts) ---");
        const string marker = "\\Capability\\Probe\\WinHeadBoot";
        bool rehydrated = Subsystem.Cm.Cm.Get(marker) != null;
        Subsystem.Cm.Cm.Register(new Subsystem.Cm.CapabilityRecord
        {
            Path = marker, Name = "WinHeadBoot", Type = "Probe", Integrity = "System",
            StartType = "manual", Enabled = true,
        });
        var records = Subsystem.Cm.Cm.List();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            dbPath = Subsystem.Cm.Cm.DbPath,
            total = records.Length,
            rehydratedFromPriorRun = rehydrated,
            paths = records.Select(r => r.Path).ToArray(),
        }));

        Console.WriteLine();
        Console.WriteLine(ok ? "LAYERS 1-2 GREEN — VOM kernel + Cm registry run on the Windows head" : "RED — see FAIL fields above");
        return ok ? 0 : 1;
    }

    static bool RunTest(string json, params string[] mustBeTrue)
    {
        Console.WriteLine(json);
        using var doc = JsonDocument.Parse(json);
        bool pass = true;
        foreach (var field in mustBeTrue)
        {
            if (!doc.RootElement.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.True)
            {
                Console.WriteLine($"  FAIL: {field}");
                pass = false;
            }
        }
        return pass;
    }
}
