#nullable disable
// DpxGang.cs - the brutally-synchronous worker-gang primitive (CRQ195, Scott's design). "we do not
// async... brutal synchrony... call it DpxGang". N lanes move in LOCKSTEP through phases gated by
// Fence.WaitAll (every lane before advancing) / Fence.WaitN (quorum, CRQ190's already-banked
// quorum-drop shape: WaitN(n, phase=t) IS the deadline, no timer). ZERO async/await, Task.Run,
// ThreadPool, .ContinueWith (CRQ181, hard law) — the gang STRUCTURE (lane lifecycle, phase
// advancement, role assignment) is Owner/Fence-based throughout, same vocabulary DpxRace.cs's
// two-lane race already proved (Vom.Spawn worker + CpuFence work/done pair), generalized to N
// lanes. Parallel.For remains acceptable only at LEAF level, inside a single lane's own phase body
// (e.g. a lane fanning out its own row range) — never for the gang's own lifecycle/advancement.
//
// Buffers: each lane owns a resident, 256-byte-aligned VOM region (Vom.Alignment — the SAME
// alignment every VOM handle already gets via Vom.Alloc; DpxGang invents no new alignment).
//
// The HNIC (gang boss): between phases, decides which LOGICAL lane-role maps to which PHYSICAL
// resident buffer by REBINDING THE HANDLE the role's slot points at — never copying bytes. This
// extends Slot.cs's A/B code-swap pattern (refcount discipline, free-on-zero) to DATA buffers: a
// role's current buffer is Open'd (refcount+1) while lanes may read through it and Close'd (refcount-1)
// when a lane is done with it for the phase; a rebind is refused (THROWS, not a comment) unless the
// prior holder's registry refcount is already at the floor the gang requires — see TryBind/Bind
// below. This is the exact hazard the kv-ring reviewer flagged this session in TensorArena.Free
// (unconditional free, no VOM-refcount branch): here the check is real and enforced.
using System;
using System.Threading;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx;

// Thrown when a role rebind is attempted while the prior physical buffer still has an in-flight
// holder (registry refcount above the floor). The caller must drain (Close) stragglers before the
// HNIC may repoint the role — never silently rebind under a live reader.
public sealed class DpGangHazardException : InvalidOperationException
{
    public int Role { get; }
    public uint PriorHandleId { get; }
    public int ObservedRefCount { get; }
    public DpGangHazardException(int role, uint priorHandleId, int observedRefCount)
        : base($"DpxGang role {role}: refused to rebind handle 0x{priorHandleId:X8} while refcount={observedRefCount} (>1 floor) - a straggler lane may still be reading through the old alias.")
    {
        Role = role; PriorHandleId = priorHandleId; ObservedRefCount = observedRefCount;
    }
}

// One lane's resident buffer: a VOM handle (cache-line/256-byte aligned, Vom.Alloc) plus the Owner
// it lives under (needed to Open/Close/RefCount it). Value-type wrapper so the role table (below)
// can hold plain arrays with no extra indirection.
public readonly struct DpGangBuffer
{
    public readonly Owner Owner;
    public readonly Handle Handle;
    public DpGangBuffer(Owner owner, Handle handle) { Owner = owner; Handle = handle; }
    // Read* (verb catalog "Read", matching DpxTensor.ReadF32/ReadBf16/ReadBytes) - resolves the
    // handle's native region to a typed Span; no copy, same region DpGangHnic.Bind repoints.
    public unsafe Span<float> ReadFloat() => new((void*)Handle.Resource, Handle.ByteCount / sizeof(float));
    public unsafe Span<byte> ReadBytes() => new((void*)Handle.Resource, Handle.ByteCount);
}

// The HNIC — the gang boss / coordinating handle table. Owns the LOGICAL role -> PHYSICAL buffer
// map (an array of DpGangBuffer indexed by role) and the rebind gate. One HNIC per gang.
public sealed class DpGangHnic
{
    private readonly Owner _owner;
    private DpGangBuffer[] _roleTable;   // role index -> currently-assigned physical buffer
    private readonly object _gate = new();

    public DpGangHnic(Owner owner, int roleCount)
    {
        _owner = owner;
        _roleTable = new DpGangBuffer[roleCount];
    }

    public int RoleCount => _roleTable.Length;

    // Set role's INITIAL buffer (no prior holder to drain - used once at gang stand-up).
    public void Set(int role, DpGangBuffer buf)
    {
        lock (_gate) { _roleTable[role] = buf; }
    }

    // Read (verb catalog) the buffer a role currently aliases.
    public DpGangBuffer Read(int role) { lock (_gate) { return _roleTable[role]; } }

    // The hazard check, explicit and enforced (not a comment promising it holds): a role may be
    // rebound to a different physical buffer ONLY while the prior buffer's registry refcount is at
    // the floor (1 = the HNIC's own standing reference, nobody else holding it open). Any lane that
    // Open'd the buffer for an in-flight phase read bumps this above the floor; the HNIC must see it
    // drop back before rebinding. Returns false (does NOT throw) so a caller can retry/backoff; Bind
    // (below) is the throwing variant for callers that want a hard failure instead.
    public bool TryBind(int role, DpGangBuffer next, int refcountFloor = 1)
    {
        lock (_gate)
        {
            var prior = _roleTable[role];
            int rc = VomClass.QueryRefCount(prior.Owner, prior.Handle.Id);
            if (rc > refcountFloor) return false;         // straggler still holds the old alias - refuse
            _roleTable[role] = next;                       // no byte copy: rewrite which buffer the role aliases
            return true;
        }
    }

    // Throwing variant: the same gate, but a violation is a hard stop (DpGangHazardException) rather
    // than a bool the caller might ignore. This is the call site tests exercise as the negative test.
    public void Bind(int role, DpGangBuffer next, int refcountFloor = 1)
    {
        lock (_gate)
        {
            var prior = _roleTable[role];
            int rc = VomClass.QueryRefCount(prior.Owner, prior.Handle.Id);
            if (rc > refcountFloor) throw new DpGangHazardException(role, prior.Handle.Id, rc);
            _roleTable[role] = next;
        }
    }
}

// The gang: N lanes, each a VOM-owned worker thread (Vom.Spawn, mirroring DpxRace.cs's lane
// pattern), moving through phases via a work/done CpuFence pair per lane. The conductor (caller's
// thread) drives phase advancement with Fence.WaitAll (every lane must reach phase t before t+1
// starts) or Fence.WaitN (quorum - CRQ190's already-banked drop shape). No Task, no ThreadPool, no
// async anywhere in this lifecycle; a lane's OWN phase body may use Parallel.For internally at leaf
// level (that is a within-phase fan-out, not gang advancement) - DpxGang itself never does.
public sealed class DpxGang : IDisposable
{
    private readonly Owner _owner;
    private readonly CpuFence[] _work;
    private readonly CpuFence[] _done;
    private readonly Exception[] _err;
    private ulong _phase;
    public int LaneCount { get; }
    public DpGangHnic Hnic { get; }

    // Per-lane phase body: (laneIndex, phase) -> do this lane's work for the phase. Set by the
    // caller before Advance(); the SAME delegate runs every phase (a gang runs one kernel shape for
    // its life) - swap gangs, not delegates mid-run, if the work changes.
    public Action<int, ulong>? LaneWork;

    public DpxGang(Owner parentOwner, int laneCount, int roleCount = 0)
        : this($"{parentOwner.Path}\\Gang\\{Guid.NewGuid():N}", laneCount, roleCount)
    { }

    // Root-owner shape (DpxRace.cs's standalone pattern: a fresh \Agent\Dpx\... owner, no parent) for
    // call sites with no natural parent Owner to nest under - e.g. a static kernel dispatch like
    // Dpx.MatMulNBits that stands a gang up for one call and tears it down when done. rootPath is the
    // gang's own owner path (a guid leaf is NOT appended here - the caller supplies one, matching
    // DpxRace.Resolve's `\Agent\Dpx\Race\{guid}` shape) so callers see the exact path they asked for.
    public DpxGang(string rootPath, int laneCount, int roleCount = 0)
    {
        if (laneCount < 1) throw new ArgumentOutOfRangeException(nameof(laneCount), "a gang needs at least one lane");
        LaneCount = laneCount;
        _owner = VomClass.CreateOwner(rootPath);
        Hnic = new DpGangHnic(_owner, roleCount > 0 ? roleCount : laneCount);
        _work = new CpuFence[laneCount];
        _done = new CpuFence[laneCount];
        _err = new Exception[laneCount];
        for (int i = 0; i < laneCount; i++) { _work[i] = new CpuFence(); _done[i] = new CpuFence(); }

        for (int lane = 0; lane < laneCount; lane++)
        {
            int L = lane;
            VomClass.Spawn(_owner, $"lane{L}", _ => LaneLoop(L));
        }
    }

    // Allocate one resident, 256-byte-aligned buffer per role under this gang's owner and Set the
    // HNIC's role table. Called once at stand-up; roles may later be rebound onto a DIFFERENT
    // physical buffer via Hnic.Bind/TryBind (never re-Alloc'd — the whole point is repointing the
    // alias, not reallocating).
    public DpGangBuffer[] AllocResidentBuffers(int elementsPerBuffer, VomFormat format = VomFormat.Float32)
    {
        var bufs = new DpGangBuffer[Hnic.RoleCount];
        int elemBytes = format switch
        {
            VomFormat.Float32 => 4, VomFormat.Bf16 => 2, VomFormat.Half => 2,
            VomFormat.Int64 => 8, VomFormat.Bytes => 1, VomFormat.Raw32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        for (int r = 0; r < bufs.Length; r++)
        {
            var h = VomClass.Alloc(_owner, elementsPerBuffer * elemBytes, format, "DpxGang.Resident", name: $"role{r}");
            bufs[r] = new DpGangBuffer(_owner, h);
            Hnic.Set(r, bufs[r]);
        }
        return bufs;
    }

    // Wait for the WHOLE gang to clear one phase: signal every lane's work fence at the new phase,
    // then Fence.WaitAll on the done fences before returning - the barrier (invariant 8: WaitAll is
    // the ONLY all-lanes sync). A lane fault surfaces here (rethrown), never swallowed.
    public void WaitAll()
    {
        ulong ph = Interlocked.Increment(ref _phase);
        var targets = new ulong[LaneCount];
        for (int i = 0; i < LaneCount; i++) { targets[i] = ph; _work[i].Signal(ph); }
        Fence.WaitAll(_done, targets);
        ThrowFirstFault();
    }

    // Wait for a QUORUM of n lanes to clear one phase - CRQ190's quorum-drop shape: Fence.WaitN(n,
    // phase) IS the deadline (no timer). Stragglers keep running; the conductor does not wait for
    // them here (a later WaitAll/WaitN call still sees their done fence catch up because the fence
    // value is the clock - no wake is lost).
    public int WaitN(int n)
    {
        ulong ph = Interlocked.Increment(ref _phase);
        var targets = new ulong[LaneCount];
        for (int i = 0; i < LaneCount; i++) { targets[i] = ph; _work[i].Signal(ph); }
        int met = Fence.WaitN(_done, targets, n);
        ThrowFirstFault();
        return met;
    }

    private void ThrowFirstFault()
    {
        for (int i = 0; i < LaneCount; i++)
            if (_err[i] != null) { var e = _err[i]; _err[i] = null; throw new AggregateException($"DpxGang lane {i} faulted", e); }
    }

    // One lane's whole life: park on its OWN work fence at each phase, run LaneWork(lane, phase),
    // ring its OWN done fence. Account for all memory within our runspace - fences only, no Task, no pool, no async
    // (mirrors DpxRace.LaneLoop exactly, generalized from 2 named lanes to N indexed lanes).
    private void LaneLoop(int lane)
    {
        ulong i = 0;
        while (true)
        {
            i++;
            _work[lane].Wait(i);
            try { LaneWork?.Invoke(lane, i); }
            catch (Exception ex) { _err[lane] = ex; _done[lane].Signal(i); return; }
            _done[lane].Signal(i);
        }
    }

    // Tear the gang down: Terminate cascades (cancel -> Interrupt a parked lane -> reclaim every
    // resident buffer's native memory) through the SAME Owner/Terminate path every VOM owner uses -
    // no bespoke gang-shutdown code.
    public void Dispose() => VomClass.Terminate(_owner);
}
