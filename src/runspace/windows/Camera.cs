using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Subsystem.Cm;
using Subsystem.Vom;

namespace Subsystem.Windows;

// Camera — `ss camera`: the managed VirtuaCam GATEWAY. It is the in-proc replacement for VirtuaCam.exe's
// UI.cpp tray: instead of a separate controller process, the Windows head itself hosts the broker
// (DirectPortBroker.dll, P/Invoked via VirtuaCamNative) and runs its render loop, compositing whatever
// DirectPort producers are live (`ss surface`, the capture producers) into the virtual-camera output.
//
// Producers stay SEPARATE PROCESSES mounted over DirectPort (the reference pattern); the broker + the MF
// client (DirectPortClient.dll, the webcam the OS sees) are the consumer side, hosted here. The whole
// thing is a CAPABILITY, fail-closed from the start: \Capability\virtualcamera\<name> is DEFAULT-DENY and
// the operation is refused until it is granted (CapabilityGate). Possession of the resulting VOM
// \VirtualCamera\<name> handle is the authority; Terminate reclaims it deterministically (ShutdownBroker),
// the let-it-crash kill path.
//
// The MF client is a COM server that must be registered once (regsvr32, Administrator) before any app
// sees "VirtuaCam" — `ss camera --register` / `--unregister` drive that; the broker render loop does not
// need it (the camera simply has no live feed until both the client is registered and the broker runs).
public static class Camera
{
    private const int Denied = 13;

    public static int Run(string[] args)
    {
        // ss camera [name] [--grant] [--no-grid] [--fps N] [--register|--unregister] [--probe]
        string name = "VirtuaCam";
        bool grant = false, grid = true, register = false, unregister = false, probe = false;
        int fps = 30;
        var positional = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--grant": case "-grant": case "/grant": grant = true; break;
                case "--no-grid": grid = false; break;
                case "--grid": grid = true; break;
                case "--register": register = true; break;
                case "--unregister": unregister = true; break;
                case "--probe": probe = true; break;
                case "--fps": if (i + 1 < args.Length && int.TryParse(args[i + 1], out var f)) { fps = f; i++; } break;
                default: positional.Add(a); break;
            }
        }
        if (positional.Count > 0 && positional[0].Length > 0) name = positional[0];
        if (fps < 1 || fps > 240) { Console.Error.WriteLine("ss camera: --fps must be 1..240"); return 1; }

        // --probe: no device, no gate — just prove the binding + the bundled artifact resolve.
        if (probe)
        {
            bool ok = VirtuaCamNative.Probe(out string detail);
            Console.WriteLine("ss camera --probe: " + detail);
            return ok ? 0 : 1;
        }

        // --register / --unregister: drive regsvr32 on the MF client. HKLM write — needs Administrator.
        if (register || unregister) return RegisterClient(register);

        // The gate + bring-up live in BrokerHost (shared with the gateway tray); Host just drives the pump.
        return Host(name, grid, fps, grant);
    }

    // ss camera's PUMP: drive the shared BrokerHost from a console while-loop (the gateway tray drives the
    // SAME host from a WinForms timer — one broker, two pumps; the pump is the only thing that changes).
    private static int Host(string name, bool grid, int fps, bool grant)
    {
        var host = BrokerHost.Start(name, grid, fps, grant, out int code, out string? reason);
        if (host == null)
        {
            if (reason != null) Console.Error.WriteLine("ss camera: " + reason);
            if (code == 1)
                Console.Error.WriteLine("  the broker artifact lives at src/native/virtuacam/DirectPortBroker.dll and is copied beside ss.exe at build.");
            return code;
        }

        Console.WriteLine($"ss camera: hosting broker '{name}'  (mode={(grid ? "auto-discovery grid" : "priority-list")}, {fps} fps)");
        Console.WriteLine($"  VOM:      \\VirtualCamera\\{name}   (gated by {host.CapabilityPath})");
        Console.WriteLine($"  MF client:{VirtuaCamNative.ClientDll}  (register once: ss camera --register, as Administrator)");
        Console.WriteLine("  Feed it with `ss surface` (a DirectPort producer); then pick VirtuaCam in your camera app. Ctrl+C to stop.");

        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var last = (VirtuaCamNative.BrokerState)(-1);
        while (!stop.IsSet)
        {
            host.RenderFrame();                           // discover -> composite -> signal the output fence
            var state = host.State;
            if (state != last) { Console.WriteLine($"  broker: {state}"); last = state; }
            stop.Wait(host.PeriodMs);                     // wakes immediately on Ctrl+C
        }

        Console.WriteLine($"\nss camera: stopping — Terminate(\\VirtualCamera) reclaims the broker (ShutdownBroker)");
        host.Stop();                                      // cascades Reclaim -> ShutdownBroker deterministically
        return 0;
    }

    // regsvr32 /s on DirectPortClient.dll (beside ss.exe). Self-registration writes HKLM — Administrator
    // only. regsvr32 + the System32 dir are resolved through Environment (no path literal scattered in code).
    private static int RegisterClient(bool register)
    {
        string verb = register ? "register" : "unregister";
        string dll = Path.Combine(AppContext.BaseDirectory, VirtuaCamNative.ClientDll);
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"ss camera --{verb}: {dll} not found (built + copied beside ss.exe?)");
            return 1;
        }
        if (!IsAdministrator())
        {
            Console.Error.WriteLine($"ss camera --{verb}: Administrator required — the MF client self-registers into HKLM.");
            Console.Error.WriteLine("  re-run from an elevated shell.");
            return Denied;
        }
        string regsvr = Path.Combine(Environment.SystemDirectory, "regsvr32.exe");
        string args = register ? $"/s \"{dll}\"" : $"/s /u \"{dll}\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(regsvr, args) { UseShellExecute = false })!;
            p.WaitForExit();
            if (p.ExitCode == 0) { Console.WriteLine($"ss camera: {verb}ed {VirtuaCamNative.ClientDll}"); return 0; }
            Console.Error.WriteLine($"ss camera --{verb}: regsvr32 exit {p.ExitCode}");
            return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"ss camera --{verb}: {ex.Message}"); return 1; }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
