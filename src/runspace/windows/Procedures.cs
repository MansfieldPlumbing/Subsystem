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
