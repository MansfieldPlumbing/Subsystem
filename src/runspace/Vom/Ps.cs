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
                              long maxBytes = 0, int maxElements = 0, bool background = true)
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
}
