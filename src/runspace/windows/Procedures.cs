namespace Subsystem.Windows;

/// <summary>
/// The living onboarder's anchor (WHITEPAPER §8; CONTRACT invariant 10). Each operating procedure is a
/// member whose &lt;summary&gt; is the procedure and whose &lt;see cref="..."/&gt; is the breadcrumb to the
/// mechanism it governs. `ss procedures` walks these and resolves every cref against the live compilation —
/// cite-or-refuse; the SS025 deny-analyzer (pending) fails the build on a dangling cref. No prose here is
/// authority on its own — the cref is. Edit the mechanism's own doc-comment and the procedure follows;
/// delete the mechanism and the cref goes red. That is the anti-rot guarantee made structural.
/// </summary>
internal static class Procedures
{
    /// <summary>
    /// No garbage collector — the VOM is the heap. Memory is acquired by taking a handle
    /// (<see cref="Subsystem.Vom.Vom.Alloc"/>); reclaim is deterministic at refcount zero
    /// (<see cref="Subsystem.Vom.Vom.Close"/>) or by owner cascade
    /// (<see cref="Subsystem.Vom.Vom.Terminate"/>) — never a tracing sweep. Receipt: test.vom.no-gc.
    /// </summary>
    public static void QueryNoGarbageCollector() { }

    /// <summary>
    /// No GC'd control plane of ours to concede — our control plane is VOM-managed; the GC belongs to a
    /// guest we mount and can cascade-kill. pwsh is mounted as a handle (a VomBoundary) via
    /// <see cref="Subsystem.Vom.Vom.Register"/> and revoked — with the guest's own collector — by
    /// <see cref="Subsystem.Vom.Vom.Terminate"/>. "GC-free in the runspace" = our code allocates only through
    /// <see cref="Subsystem.Vom.Vom.Alloc"/> (alloc-zero); the CLR collector stays vestigial behind the pwsh
    /// boundary and reaches zero when the guest is dropped. Scott direct 2026-06-29.
    /// </summary>
    public static void QueryPwshIsTheGcBoundary() { }

    /// <summary>
    /// Handle = authority — an object exists iff a handle roots it. A managed object becomes a handle via
    /// <see cref="Subsystem.Vom.Vom.Register"/>; it is resolved by path with
    /// <see cref="Subsystem.Vom.Vom.TryGetByPath"/> and released at refcount zero by
    /// <see cref="Subsystem.Vom.Vom.Close"/>.
    /// </summary>
    public static void QueryHandleIsAuthority() { }

    /// <summary>
    /// Bulk reclaim — every handle under a namespace prefix is freed in one deterministic pass by
    /// <see cref="Subsystem.Vom.Vom.DropPrefix"/>; this is the substrate
    /// <see cref="Subsystem.Vom.Vom.Terminate"/> rides to revoke an owner and its children.
    /// </summary>
    public static void QueryDeterministicReclaim() { }
}
