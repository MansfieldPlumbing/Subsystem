using System;
using System.Text.Json;
using System.Threading;
using Subsystem.Cm;
using Subsystem.Vom;

namespace Subsystem.Windows;

// Surface — `ss surface`: the Windows head becomes a DirectPort PRODUCER that VirtuaCam composites.
// It is a CAPABILITY, gated from the start: the operation is refused unless \Capability\Surface\<name>
// is granted and the caller's token dominates its integrity (Subsystem.Cm.AccessCheck, fail-closed).
// Possession of the resulting VOM \Surfaces\<name> handle is the authority; Terminate reclaims it
// deterministically (Reclaim = unmap -> dp_close -> shutdown), the let-it-crash kill path.
//
// M0 path (no device, no tunnel — fully verifiable on the dev box): create a CPU-mappable shared D3D12
// texture (is_system_ram = true: CUSTOM/L0/ROW_MAJOR, cross-process shareable), publish the manifest,
// and drive an animated test pattern into it each frame. VERIFY: run VirtuaCam, pick this producer as a
// source, open the Windows Camera app -> the pattern is the live webcam feed.
public static class Surface
{
    private const int Denied = 13;

    public static int Run(string[] args)
    {
        // ss surface [name] [width] [height] [--grant]
        string name = "DirectPort";
        int width = 1920, height = 1080;
        bool grant = false;
        var positional = new System.Collections.Generic.List<string>();
        foreach (var a in args)
        {
            if (a is "--grant" or "-grant" or "/grant") { grant = true; continue; }
            positional.Add(a);
        }
        if (positional.Count > 0 && positional[0].Length > 0) name = positional[0];
        if (positional.Count > 1) int.TryParse(positional[1], out width);
        if (positional.Count > 2) int.TryParse(positional[2], out height);
        if (width <= 0 || height <= 0) { Console.Error.WriteLine("ss surface: width/height must be positive"); return 1; }

        string capPath = "\\Capability\\Surface\\" + name;
        SeedCapabilityIfAbsent(capPath, name);

        // The grant is a deliberate, audited consent action (the Set-Capability equivalent, inline).
        if (grant)
        {
            Cm.Cm.Set(capPath, enabled: true, startType: null);
            Console.WriteLine($"ss surface: granted (audited) {capPath}");
        }

        // THE GATE. Default-deny: an ungranted surface is refused here, before any resource is minted.
        var caller = Caller.Local();   // local operator, User integrity (host seam may elevate later)
        var access = AccessCheck.Resolve(caller, capPath);
        if (!access.Granted)
        {
            Console.Error.WriteLine("ss surface: DENIED — " + access.Reason);
            Console.Error.WriteLine($"  grant it:  ss Set-Capability -Path '{capPath}' -Enabled $true");
            Console.Error.WriteLine($"  or re-run: ss surface {name} --grant");
            return Denied;
        }

        return Produce(name, width, height, capPath);
    }

    private static unsafe int Produce(string name, int width, int height, string capPath)
    {
        var producer = DirectPortProducer.Create(name, width, height, capPath, out string? error);
        if (producer == null) { Console.Error.WriteLine("ss surface: " + error); return 1; }

        // Cooperative stop on Ctrl+C (cancellable per the wedged-thread hard limit).
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        Console.WriteLine($"ss surface: producing '{name}' {width}x{height} BGRA  (pitch {producer.RowPitch}B, pid {Environment.ProcessId})");
        Console.WriteLine($"  VOM:     \\Surfaces\\{name}   (gated by {capPath})");
        Console.WriteLine("  Open VirtuaCam -> Source -> this producer; then the Windows Camera app. Ctrl+C to stop.");

        // Draw the test pattern straight into the producer's 256-aligned scratch, then push it.
        ulong frame = 0;
        while (!stop.IsSet)
        {
            DrawTestPattern((byte*)producer.Scratch, producer.RowPitch, width, height, frame);
            frame++;
            if (!producer.Commit()) { Console.Error.WriteLine("ss surface: dp_upload_bgra failed"); break; }
            stop.Wait(16);                                          // ~60 fps; wakes immediately on Ctrl+C
        }

        Console.WriteLine($"\nss surface: stopping — Terminate(\\Surfaces) reclaims the texture/fence/manifest");
        producer.Stop();
        return 0;
    }

    // Animated BGRA test pattern; respects the 256-aligned row pitch (write past width*4 is padding).
    private static unsafe void DrawTestPattern(byte* p, uint rowPitch, int w, int h, ulong frame)
    {
        byte t = (byte)frame;
        for (int y = 0; y < h; y++)
        {
            byte* row = p + (long)y * rowPitch;
            for (int x = 0; x < w; x++)
            {
                byte* px = row + (x << 2);
                px[0] = (byte)(x + t);   // B
                px[1] = (byte)(y + t);   // G
                px[2] = (byte)(x ^ y);   // R
                px[3] = 255;             // A
            }
        }
    }

    // Seed the surface capability default-DENY (Enabled=false, User integrity, no Source). Seed-if-absent
    // so a prior grant survives. This record IS the switch, the permission, and (later) the agent tool.
    private static void SeedCapabilityIfAbsent(string capPath, string name)
    {
        if (Cm.Cm.Get(capPath) != null) return;
        var manifest = new
        {
            version = 1, kind = "surface", id = name,
            transport = "directport-d3d12", format = "B8G8R8A8_UNORM",
            note = "DirectPort producer for VirtuaCam. Off by default (default-deny); grant to allow ss surface.",
        };
        Cm.Cm.Register(new CapabilityRecord
        {
            Path = capPath, Name = name, Type = "Mount", Owner = "\\System",
            Integrity = "User", StartType = "manual", Enabled = false,
            ManifestJson = JsonSerializer.Serialize(manifest),
        });
    }

}
