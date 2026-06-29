using System;
using Subsystem.Cm;
using Subsystem.Vom;

namespace Subsystem.Windows;

// BrokerHost — the in-proc VirtuaCam broker as ONE concept (#9): gate -> probe -> InitializeBroker ->
// register the possession-gated VOM leaf \VirtualCamera\<name> -> RenderFrame() ticks -> deterministic
// reclaim (ShutdownBroker, the let-it-crash kill path). The PUMP is the boundary: `ss camera` drives
// RenderFrame() from a console while-loop; the gateway tray drives the SAME host from a WinForms timer on
// its STA thread. One broker, two pumps — the boundary is the only thing that changes.
internal sealed class BrokerHost
{
    public const int Denied = 13;

    private readonly Owner _owner;
    public string Name { get; }
    public string CapabilityPath { get; }
    public int PeriodMs { get; }

    private BrokerHost(Owner owner, string name, string capPath, int periodMs)
    {
        _owner = owner; Name = name; CapabilityPath = capPath; PeriodMs = periodMs;
    }

    // THE GATE + bring-up, fail-closed. Returns null on denial/unavailability with a code (Denied | 1) and a
    // reason the caller surfaces (console for ss camera, a tray balloon for the gateway). On success the
    // broker is initialized and registered — drive RenderFrame() on whatever single thread owns the pump.
    public static BrokerHost? Start(string name, bool grid, int fps, bool grant, out int code, out string? reason)
    {
        code = 0; reason = null;
        int periodMs = Math.Max(1, 1000 / Math.Clamp(fps, 1, 240));

        // Default-deny: seed-if-absent, optionally record the audited grant, then AccessCheck.
        if (!CapabilityGate.Allow("virtualcamera", name, grant, out string capPath))
        {
            code = Denied;
            reason = $"virtual camera '{name}' is default-deny and not granted — re-run with --grant.";
            return null;
        }
        // Fail fast with a clear reason if the broker DLL / its dependencies aren't present.
        if (!VirtuaCamNative.Probe(out string probeDetail))
        {
            code = 1; reason = probeDetail;
            return null;
        }

        VirtuaCamNative.InitializeBroker();              // D3D11 + discovery + multiplexer + shared output
        VirtuaCamNative.SetCompositingMode(grid);

        // Possession-gated VOM leaf at \VirtualCamera\<name>; Reclaim is the deterministic shutdown at
        // refcount-zero / on Terminate.
        var owner = Vom.Vom.CreateOwner("\\VirtualCamera");
        Vom.Vom.RegisterNative(owner, "VirtuaCamBroker", native: 0, reclaim: () =>
        {
            try { VirtuaCamNative.ShutdownBroker(); }
            catch (Exception ex) { Dg.Log("camera", "ShutdownBroker: " + ex.Message); }   // degrade + report (never vanish)
        }, byteCount: 0, format: VomFormat.Bytes, subdir: "", name: name);

        return new BrokerHost(owner, name, capPath, periodMs);
    }

    // One render tick: discover -> composite -> signal the output fence. MUST be called on a single thread
    // (the broker's D3D context is single-threaded) — never two pumps at once.
    public void RenderFrame() => VirtuaCamNative.RenderBrokerFrame();

    public VirtuaCamNative.BrokerState State => VirtuaCamNative.GetBrokerState();

    // Deterministic shutdown: Terminate cascades Reclaim -> ShutdownBroker.
    public void Stop() => Vom.Vom.Terminate(_owner);
}
