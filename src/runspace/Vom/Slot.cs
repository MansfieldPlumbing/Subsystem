using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace Subsystem.Vom;

// The VOM as the invariant loader — the ONE mechanism (handle = authority, free-at-zero) pointed at
// CODE. A "slot" is a unit of swappable code held as a refcounted VOM handle: a caller Opens it on
// entry, Closes on exit, and the last Close at refcount 0 fires the handle's Reclaim — unload. So a
// swapped-out slot frees exactly when its last in-flight caller drains. Free-on-zero, applied to code.
//
// Backend today: a collectible AssemblyLoadContext (Reclaim = Alc.Unload). The unload TRIGGER is
// deterministic (refcount-driven); actual reclamation is GC-timed while the CLR GC is present. When the
// GC is removed (invariant 7) the SAME seam frees a VOM-owned code region — graded rungs behind one
// seam (invariant 9); the loader's shape does not change.
public sealed class Slot
{
    public string   Path     { get; }            // the VOM handle path: \Slots\Objects\<name>
    public uint     Id       { get; }
    public Assembly Assembly { get; }
    internal readonly AssemblyLoadContext Alc;
    internal Slot(string path, uint id, Assembly asm, AssemblyLoadContext alc)
        { Path = path; Id = id; Assembly = asm; Alc = alc; }
}

public static unsafe partial class Vom
{
    public const string SlotsOwner = "\\Slots";

    // Load an assembly image into a fresh collectible ALC and register it as a refcounted handle under
    // \Slots (RefCount 1 = the loader's own reference). The handle's Reclaim unloads the ALC, so
    // Close-to-zero frees the slot. name keys the leaf so the slot re-resolves by path.
    public static Slot LoadSlot(string name, byte[] image)
    {
        var owner = CreateOwner(SlotsOwner);
        var alc = new AssemblyLoadContext($"slot:{name}", isCollectible: true);
        Assembly asm;
        using (var ms = new MemoryStream(image)) asm = alc.LoadFromStream(ms);
        var h = Register(owner, "Slot", alc, onReclaim: () => alc.Unload(), name: name);
        return new Slot(h.Path, h.Id, asm, alc);
    }

    // Open/Close a slot around an in-flight call (refcount up / down). Close at zero frees the slot.
    public static bool OpenSlot(Slot s) { var o = GetOwner(SlotsOwner); return o != null && Open(o, s.Path); }
    public static bool CloseSlot(Slot s) { var o = GetOwner(SlotsOwner); return o != null && Close(o, s.Path); }

    // Drop a slot's loader reference (free-on-zero: frees once any in-flight callers also Leave).
    public static bool FreeSlot(Slot s) { var o = GetOwner(SlotsOwner); return o != null && Close(o, s.Path); }

    // Swap with rollback — the A/B rollback half (CRQ001). Load newImage as a fresh slot and run probe
    // against it; if it cannot load, or probe returns false / throws, free the candidate and KEEP
    // current (rollback). Otherwise retire current and go live on the candidate. The loser is freed by
    // the same free-on-zero path as any handle — a bad swap leaves nothing behind.
    public static (Slot Live, bool RolledBack) ApplySlotWithRollback(Slot current, string name, byte[] newImage, Func<Slot, bool> probe)
    {
        Slot candidate;
        try { candidate = LoadSlot(name, newImage); }
        catch { return (current, true); }            // could not even load — keep current

        bool ok;
        try { ok = probe(candidate); } catch { ok = false; }

        if (ok) { FreeSlot(current); return (candidate, false); }   // candidate good — retire old
        FreeSlot(candidate); return (current, true);                // candidate bad — roll back
    }

    // --- Slot-swap self-test (the loader thesis: free-on-zero on CODE) ---

    // Load slot v1, a caller enters then leaves it, then v2 comes up and v1 is freed at refcount 0.
    // Assert v1 served 1, v2 serves 2 (and still serves after v1 is gone), and v1's ALC is reclaimed
    // once its refcount hits zero. GC is pumped because the unload TRIGGER is deterministic but
    // reclamation is GC-timed until the GC is removed (invariant 7). A JSON verdict, like the others.
    public static string ApplySlotSwapTest()
    {
        var (wr, v1Value, v2Value, v2Live) = SwapCycle();

        bool v1Reclaimed = false;
        for (int i = 0; i < 12 && !v1Reclaimed; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            v1Reclaimed = !wr.IsAlive;
        }

        var owner = GetOwner(SlotsOwner);   // tidy: drop \Slots (and v2) so a re-run starts clean
        if (owner != null) Terminate(owner);

        return JsonSerializer.Serialize(new
        {
            v1Value,
            v2Value,
            v1Served = v1Value == 1,
            v2Served = v2Value == 2,
            v2LiveAfterSwap = v2Live,
            v1ReclaimedOnZero = v1Reclaimed,
            note = "code slot = refcounted VOM handle; Close-to-zero -> Reclaim -> ALC.Unload (free-on-zero). Trigger deterministic; reclamation GC-timed until the GC is removed (invariant 7).",
        });
    }

    // The cycle in its own frame so the strong refs to v1 (its Slot + reflected Assembly) die on return
    // and the ALC can actually unload — only the WeakReference survives to witness the reclaim.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference V1Alc, int V1Value, int V2Value, bool V2Live) SwapCycle()
    {
        var owner = CreateOwner(SlotsOwner);

        var v1 = LoadSlot("v1", EmitValueAssembly("SlotV1", 1));
        Open(owner, v1.Path);                 // a caller enters v1 (refcount 2)
        int v1Value = InvokeValue(v1);
        Close(owner, v1.Path);                // caller leaves (refcount 1)
        var wr = new WeakReference(v1.Alc);

        var v2 = LoadSlot("v2", EmitValueAssembly("SlotV2", 2));   // v2 comes up
        int v2Value = InvokeValue(v2);
        Close(owner, v1.Path);                // free v1 at zero -> Reclaim -> ALC.Unload
        bool v2Live = InvokeValue(v2) == 2;   // v2 still serves after v1 is gone

        return (wr, v1Value, v2Value, v2Live);
    }

    private static int InvokeValue(Slot s)
        => (int)s.Assembly.GetType("Entry")!.GetMethod("Value")!.Invoke(null, null)!;

    // Emit { public static class Entry { public static int Value() => n; } } as a PE image via
    // Reflection.Emit — NO Roslyn, NO reference assemblies (a single-file exe's TPA list is empty); the
    // only reference is the loaded CoreLib. This is the throwaway code the slot test loads and swaps.
    private static byte[] EmitValueAssembly(string asmName, int value)
    {
        var ab  = new PersistedAssemblyBuilder(new AssemblyName(asmName), typeof(object).Assembly);
        var mod = ab.DefineDynamicModule(asmName);
        var tb  = mod.DefineType("Entry", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var mb  = tb.DefineMethod("Value", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il  = mb.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        tb.CreateType();
        using var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }

    // --- Slot-rollback self-test (the A/B rollback half: a bad swap reverts and leaves nothing) ---

    // Good slot v1 is live; a swap to a slot that LOADS but FAULTS when invoked is attempted; the probe
    // throws; the loader frees the bad slot and keeps v1. Assert: rolled back, v1 still serves, and the
    // bad slot's handle is gone (freed-on-zero — Reclaim fired). A JSON verdict, like the others.
    public static string ApplySlotRollbackTest()
    {
        var (v1Still, faultingGone, rolledBack) = RollbackCycle();
        var owner = GetOwner(SlotsOwner);
        if (owner != null) Terminate(owner);
        return JsonSerializer.Serialize(new
        {
            rolledBack,
            v1StillServes = v1Still,
            faultingSlotReclaimed = faultingGone,
            note = "swap to a slot that faults on invoke: probe throws -> bad slot freed-on-zero, prior slot kept. A/B rollback on the VOM loader.",
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool V1Still, bool FaultingGone, bool RolledBack) RollbackCycle()
    {
        var owner = CreateOwner(SlotsOwner);
        var v1 = LoadSlot("good", EmitValueAssembly("SlotGood", 1));
        var (live, rolledBack) = ApplySlotWithRollback(v1, "faulting", EmitFaultingAssembly("SlotFaulting"), s => InvokeValue(s) == 1);
        bool v1Still = ReferenceEquals(live, v1) && InvokeValue(v1) == 1;   // we kept v1
        bool faultingGone = !TryGetByPath(owner, "\\Slots\\Objects\\faulting", out _); // the faulting slot handle is freed
        return (v1Still, faultingGone, rolledBack);
    }

    // Emit { public static class Entry { public static int Value() => throw new InvalidOperationException(); } }
    // — a slot that loads cleanly but faults on invoke: the realistic "new version is broken at runtime" case.
    private static byte[] EmitFaultingAssembly(string asmName)
    {
        var ab  = new PersistedAssemblyBuilder(new AssemblyName(asmName), typeof(object).Assembly);
        var mod = ab.DefineDynamicModule(asmName);
        var tb  = mod.DefineType("Entry", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var mb  = tb.DefineMethod("Value", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il  = mb.GetILGenerator();
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        tb.CreateType();
        using var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }
}
