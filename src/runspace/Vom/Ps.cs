using System;
using System.Linq;
using System.Threading;
using System.Text.Json;

namespace Subsystem.Vom;

// Ps — the dispatcher (VOM-SPEC §4d). The VOM owns thread creation: Spawn(parent, name, work)
// replaces ambient Task.Run with a TRACKED, quota'd, token-wired child Sub-VOM on its own thread.
// The child's cancellation token is LINKED to the parent's (see Owner), so Terminate(parent) cascades
// the termination down the owner tree. Escalation on Terminate (VOM-SPEC §5): cooperative token cancel →
// Thread.Interrupt() (wakes a thread parked in a managed wait so it unwinds cleanly) → resourceless quarantine
// (handles revoked, owner dropped) for a busy/native wedge CoreCLR still cannot abort.
public static unsafe partial class Vom
{
    // Spawn a child Sub-VOM under `parent` and run `work` on its own thread. Delegated quota can't
    // exceed the parent's (0 = inherit). Returns the child Owner. When `work` returns/throws, the
    // child self-Terminates (idempotent with the cascade).
    public static Owner Spawn(Owner parent, string name, Action<Owner> work,
                              long maxBytes = 0, int maxElements = 0, bool background = true, bool sta = false)
    {
        long mb = maxBytes > 0    ? Math.Min(maxBytes, parent.MaxBytes)       : parent.MaxBytes;
        int  el = maxElements > 0 ? Math.Min(maxElements, parent.MaxElements) : parent.MaxElements;
        string path = $"{parent.Path}\\Ps\\{name}";

        var child = _owners.GetOrAdd(path, p => new Owner(p, mb, el, parent));
        parent.Children[path] = child;
        Dg.Log("vom", $"SPAWN {path} (quota {mb}B / {el} elem) under {parent.Path}");

        var t = new Thread(() =>
        {
            try { work(child); }
            catch (OperationCanceledException) { }   // cooperative cancel — expected on Terminate
            catch (Exception ex) { Dg.Log("vom", $"SPAWN {path} faulted: {ex.GetType().Name}: {ex.Message}"); }
            finally { Terminate(child); }             // self-cleanup once work returns
        }) { IsBackground = background, Name = path };
        // onReclaim is the thread's kill escalation, run AFTER Terminate cancelled the token (rung 1). Rung 2:
        // Thread.Interrupt() wakes a thread parked in a MANAGED wait (Sleep/Monitor.Wait/WaitHandle) so it throws
        // ThreadInterruptedException and unwinds cleanly (finallys run). A busy/native wedge ignores it (the
        // exception only lands at a managed wait) and stays resourceless residual — CoreCLR cannot abort it.
        // Skip self-reclaim: a thread finishing its own work is already leaving (joining self only burns the grace).
        Register(child, "Thread", t, subdir: "Thread", onReclaim: () =>
        {
            if (!t.IsAlive || t == Thread.CurrentThread) return;
            try { t.Interrupt(); } catch (Exception ex) { Dg.Log("vom", $"INTERRUPT {path}\\Thread: {ex.GetType().Name} (raced to exit)"); }
            Dg.Log("vom", t.Join(50)
                ? $"INTERRUPT {path}\\Thread: unwound on interrupt"
                : $"RESIDUAL {path}\\Thread: still alive at reclaim (interrupt unreached — busy/native wedge)");
        });
        if (sta) t.SetApartmentState(ApartmentState.STA);   // a VOM-OWNED UI thread (WinForms needs STA) — still SS009-clean: the Vom type owns it
        t.Start();
        return child;
    }

    // Nested-spawn termination test (VOM-SPEC §11): root -> child -> grandchild, each allocating a native
    // handle; the grandchild WEDGES (Sleep(Infinite), unabortable by design) while the child parks on
    // its token. Terminate(root) must cascade — cooperatively cancel the child, reclaim ALL three
    // owners' native handles, and drop all three owners — even the wedged grandchild becomes
    // resourceless. Run on device via Test-Ps; drives the DOM autopsy.
    public static string SpawnKillTest()
    {
        string root = $"\\Sessions\\__pstest_{DateTime.Now:HHmmss}";
        var r = CreateOwner(root);
        Alloc(r, 1024, type: "RootRegion");

        var ready     = new ManualResetEventSlim();
        var childWoke = new ManualResetEventSlim();

        Spawn(r, "child", c =>
        {
            Alloc(c, 1024, type: "ChildRegion");
            Spawn(c, "grandchild", g =>
            {
                Alloc(g, 1024, type: "GrandRegion");
                ready.Set();                                   // whole tree exists + allocated
                try { Thread.Sleep(Timeout.Infinite); } catch { }   // wedged leaf — cannot be aborted
            });
            try { c.Token.WaitHandle.WaitOne(); }              // park; cascade cancel wakes us
            finally { childWoke.Set(); }
        });

        ready.Wait(3000);
        Thread.Sleep(100);

        int ownersBefore  = OwnerCount;
        int threadHandles = _owners.Values.Sum(o => o.PathToId.Keys.Count(k => k.Contains("\\Thread\\")));  // child + grandchild = thread HANDLES
        long bytesBefore  = Interlocked.Read(ref r.CurrentBytes);
        Terminate(r);
        bool childObservedCancel = childWoke.Wait(3000);

        return JsonSerializer.Serialize(new
        {
            root,
            ownersBefore,
            threadHandles,
            bytesBefore,
            rootRemoved       = GetOwner(root) == null,
            childRemoved      = GetOwner($"{root}\\Ps\\child") == null,
            grandchildRemoved = GetOwner($"{root}\\Ps\\child\\Ps\\grandchild") == null,
            childObservedCancel,                               // linked token cascaded to the parked child
            ownersAfter       = OwnerCount,
            note = "cascade Terminate: linked-token cancel -> Thread.Interrupt() -> bulk native reclaim down the owner tree; the grandchild parks in a managed Sleep, so Interrupt unwinds it (see the INTERRUPT log) — a busy/native wedge would stay resourceless residual.",
        });
    }

    // GC isolated OUTSIDE pwsh threads (invariant 7 corollary; pwsh is the VomBoundary). Spawn a thread the VOM
    // owns — not the pwsh runspace thread, not the ThreadPool — allocate native VOM regions ON it, and measure:
    // the worker is a distinct non-pool thread; its 200 MB of native work costs ZERO GC collections and adds only
    // per-handle managed bookkeeping; the regions stay live until Terminate(parent) cascades and reclaims them
    // deterministically (Interrupt->Join + AlignedFree). The collector's domain is the managed pwsh guest, never
    // the spawned control plane. Synchronous JSON verdict; drives the test.vom.no-gc receipt (probe E).
    public static string SpawnGcIsolationTest(int regions = 200, int regionBytes = 1024 * 1024)
    {
        int callerTid = Environment.CurrentManagedThreadId;
        bool callerIsPool = Thread.CurrentThread.IsThreadPoolThread;
        string root = $"\\Sessions\\__gciso_{DateTime.Now:HHmmssfff}";
        var r = CreateOwner(root, maxBytes: (long)regions * regionBytes + (64L << 20), maxElements: regions + 1024);

        int spawnTid = 0; bool spawnIsPool = true, spawnIsBg = false;
        long gc0 = -1, gc1 = -1, gc2 = -1, managedDelta = -1, nativeBytes = 0;
        var ready = new ManualResetEventSlim();

        Spawn(r, "worker", c =>
        {
            spawnTid = Environment.CurrentManagedThreadId;
            spawnIsPool = Thread.CurrentThread.IsThreadPoolThread;
            spawnIsBg = Thread.CurrentThread.IsBackground;
            long m0 = GC.GetAllocatedBytesForCurrentThread();
            int b0 = GC.CollectionCount(0), b1 = GC.CollectionCount(1), b2 = GC.CollectionCount(2);
            for (int i = 0; i < regions; i++) Alloc(c, regionBytes, type: "GcIsoRegion");
            managedDelta = GC.GetAllocatedBytesForCurrentThread() - m0;
            gc0 = GC.CollectionCount(0) - b0; gc1 = GC.CollectionCount(1) - b1; gc2 = GC.CollectionCount(2) - b2;
            nativeBytes = Interlocked.Read(ref c.CurrentBytes);
            ready.Set();
            try { c.Token.WaitHandle.WaitOne(); } catch { }   // park: keep the regions live until the cascade reclaim
        });

        ready.Wait(10000);
        Terminate(r);                                          // cascade: cancel worker, Interrupt->Join, AlignedFree all regions
        bool gone = GetOwner(root) == null && GetOwner($"{root}\\Ps\\worker") == null;

        return JsonSerializer.Serialize(new
        {
            callerTid, callerIsPool, spawnTid, spawnIsPool, spawnIsBg,
            distinctThread = spawnTid != 0 && spawnTid != callerTid,
            nativeMB = Math.Round(nativeBytes / (1024.0 * 1024.0), 1),
            managedDeltaKB = Math.Round(managedDelta / 1024.0, 1),
            gc0, gc1, gc2,
            reclaimedOnTerminate = gone,
            note = "a VOM-owned worker thread (not pwsh, not ThreadPool) carried native VOM memory with zero GC collections; Terminate cascaded the reclaim — the collector's domain is the pwsh guest, not the spawned control plane.",
        });
    }

    // Phase-lock self-test (the multiplexer is a phase lock, not a switchboard). Two producer fences feed a
    // consumer that uses WaitAny (switchboard: first worker to its phase) then WaitAll (barrier: parks until
    // EVERY fence reaches phase N). Proves the barrier holds for the laggard — async/ThreadPool jitter can't
    // tear it. Synchronous, futex-parked, no async; the fence value IS the clock.
    // CRQ107 — WaitN, the general quorum. Prove n=2-of-3 returns at once when two fences are phased, and
    // n=3 parks until the laggard signals (~120ms) then wakes — the middle between WaitAny and WaitAll.
    public static string WaitQuorumTest()
    {
        var f = new[] { new CpuFence(), new CpuFence(), new CpuFence() };
        var fences  = new Fence[] { f[0], f[1], f[2] };
        var targets = new ulong[] { 1, 1, 1 };

        f[0].Signal(1); f[1].Signal(1);                 // two of three are phased
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int metImmediate = Fence.WaitN(fences, targets, 2);
        long immediateMs = sw.ElapsedMilliseconds;

        var t = new Thread(() => { Thread.Sleep(120); f[2].Signal(1); }) { IsBackground = true };
        sw.Restart(); t.Start();
        int metFull = Fence.WaitN(fences, targets, 3);  // parks until the laggard signals
        long blockedMs = sw.ElapsedMilliseconds;
        t.Join();

        return JsonSerializer.Serialize(new
        {
            metImmediate,
            immediateMs,
            metFull,
            blockedMs,
            quorumImmediate       = metImmediate >= 2 && immediateMs < 50,
            quorumBlockedThenWoke = metFull == 3 && blockedMs >= 80,
            note = "WaitN: n=2 of 3 returns at once when two are phased; n=3 parks until the laggard (~120ms) then wakes - the quorum between WaitAny (n=1) and WaitAll (n=M).",
        });
    }

    public static string WaitPhaseLockTest()
    {
        var vision = new CpuFence();
        var audio  = new CpuFence();
        var fences = new Fence[] { vision, audio };

        // WaitAny (control): only audio advances -> WaitAny returns audio's index.
        var t0 = new Thread(() => { Thread.Sleep(25); audio.Signal(1); }) { IsBackground = true };
        t0.Start();
        int who = Fence.WaitAny(fences, new ulong[] { 1, 1 });
        t0.Join();

        // WaitAll (data): vision reaches phase 5 first; the barrier must NOT release until audio also hits 5.
        var t1 = new Thread(() => { Thread.Sleep(25); vision.Signal(5); }) { IsBackground = true };
        var t2 = new Thread(() => { Thread.Sleep(75); audio.Signal(5);  }) { IsBackground = true };
        t1.Start(); t2.Start();
        Fence.WaitAll(fences, new ulong[] { 5, 5 });
        bool laggardBehindAtRelease = audio.CompletedValue < 5;   // MUST be false — the barrier held for the laggard
        t1.Join(); t2.Join();

        return JsonSerializer.Serialize(new
        {
            waitAnyIndex          = who,                                 // expect 1 (audio)
            waitAnyCorrect        = who == 1,
            visionPhase           = vision.CompletedValue,               // 5
            audioPhase            = audio.CompletedValue,                // 5
            barrierHeldForLaggard = !laggardBehindAtRelease,             // expect true
            phaseLocked           = vision.CompletedValue == 5 && audio.CompletedValue == 5,
            note = "WaitAny = switchboard (first worker to its phase); WaitAll = barrier (parks until ALL at phase N). Futex-parked, synchronous, no async — the fence value is the clock.",
        });
    }
}
