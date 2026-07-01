// MemGovernor.cs — DPX ambient RAM awareness + budget enforcement (the engine as a good hardware citizen).
//
// The inference data plane allocates large fp32 tensors on the GC heap, OFF the VOM, so the VOM's per-owner
// quota never sees them — the engine can thrash the device to an OOM-kill. DpxMem is the engine's own sense
// organ + governor: it knows the process working set, the managed heap, and the SYSTEM memory load
// (GC.GetGCMemoryInfo is cross-platform — it reflects the cgroup/device limit on Android and physical RAM on
// Windows), holds a self-imposed budget that leaves headroom for the OS and other apps, and reports pressure
// so callers degrade (bound KV, refuse a load, force reclaim) instead of getting OOM-killed.

using System;

namespace Subsystem.Dpx;

// A cheap, point-in-time RAM reading. SystemFraction = how full the device is; BudgetFraction = how much of
// the engine's self-imposed budget is spent.
public readonly record struct MemSnapshot(long WorkingSet, long ManagedHeap, long SystemLoad, long SystemTotal, long Budget)
{
    public double SystemFraction => SystemTotal > 0 ? (double)SystemLoad / SystemTotal : 0;
    public double BudgetFraction => Budget > 0 && Budget != long.MaxValue ? (double)WorkingSet / Budget : 0;
    public override string ToString() =>
        $"rss={WorkingSet / 1e9:F2}GB managed={ManagedHeap / 1e9:F2}GB sysLoad={SystemFraction * 100:F0}% "
      + $"budget={WorkingSet / 1e9:F2}/{(Budget == long.MaxValue ? double.PositiveInfinity : Budget / 1e9):F2}GB";
}

public static class DpxMem
{
    // Fraction of total RAM the engine leaves for the OS + other apps; the budget is the remainder. A good
    // citizen claims a slice sized to the device, not the whole machine (8GB phone => ~6GB engine budget).
    public static double HeadroomFraction = 0.25;

    // Explicit budget in bytes; 0 = derive from the device. The Android head sets this from ActivityManager
    // (the dalvik heap limit does not bound CoreCLR native allocs, so device RAM is the real ceiling there).
    public static long BudgetOverride = 0;

    // An OS memory-pressure latch the platform feeds (Android onTrimMemory / ComponentCallbacks2). When the
    // system says it is tight, the engine is under pressure regardless of its own accounting.
    public static volatile bool ExternalPressure;

    public static long WorkingSet => Environment.WorkingSet;          // process RSS, cross-platform
    public static long ManagedHeap => GC.GetTotalMemory(false);       // managed heap bytes

    public static long SystemTotal { get { try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { return 0; } } }
    public static long SystemLoad  { get { try { return GC.GetGCMemoryInfo().MemoryLoadBytes; } catch { return 0; } } }

    public static long Budget
    {
        get
        {
            if (BudgetOverride > 0) return BudgetOverride;
            long total = SystemTotal;
            return total > 0 ? (long)(total * (1.0 - HeadroomFraction)) : long.MaxValue;
        }
    }

    public static MemSnapshot Snapshot() => new(WorkingSet, ManagedHeap, SystemLoad, SystemTotal, Budget);

    // Under pressure when the engine is over its budget, the device is ~full, or the OS signalled tight.
    public static bool UnderPressure =>
        ExternalPressure || WorkingSet >= Budget || (SystemTotal > 0 && (double)SystemLoad / SystemTotal >= 0.90);

    // Admission: would `bytes` more keep the engine under budget? Callers gate a model load / big alloc on this.
    public static bool CanAllocate(long bytes) => WorkingSet + Math.Max(0, bytes) <= Budget;

    // Telemetry inlet: the host (Dg) subscribes without the engine depending on it. Sample() emits a reading.
    public static Action<MemSnapshot>? OnSample;
    public static MemSnapshot Sample() { var s = Snapshot(); OnSample?.Invoke(s); return s; }
}
