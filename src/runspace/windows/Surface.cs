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

        // Layout self-check — refuse rather than silently corrupt the broker's read if the struct ever drifts.
        if (!VerifyManifestLayout(out string layoutErr)) { Console.Error.WriteLine("ss surface: " + layoutErr); return 1; }

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
        if (!DirectPortNative.dp_init())
        {
            Console.Error.WriteLine("ss surface: dp_init failed (no D3D12 device / directport.dll not found)");
            return 1;
        }

        long luidRaw = 0;
        DirectPortNative.dp_get_adapter_luid(out luidRaw);

        int pid = Environment.ProcessId;
        string manifestName = $"DirectPort_Producer_Manifest_{pid}";        // session-local (no Global\)
        string texName      = $"Global\\DirectPortTexture_{pid}";
        string fenceName    = $"Global\\DirectPortFence_{pid}";

        // CPU-mappable shared texture: BGRA, is_system_ram=true so we can write the pattern from the CPU
        // AND the broker can OpenSharedHandleByName it (CUSTOM heap + SHARED_CROSS_ADAPTER).
        IntPtr dp = DirectPortNative.dp_create_shared_resource(
            (uint)width, (uint)height, (int)DirectPortNative.DpFormat.Video, false, texName, fenceName);
        if (dp == IntPtr.Zero)
        {
            Console.Error.WriteLine($"ss surface: dp_create_shared_resource failed (HRESULT 0x{DirectPortNative.dp_last_hresult():X8}, adapter UMA={DirectPortNative.dp_is_uma()})");
            DirectPortNative.dp_shutdown();
            return 1;
        }

        // A discrete-GPU shared texture lives in VRAM and can't be CPU-mapped. Draw the pattern into a
        // native scratch buffer and upload it each frame (dp_upload_bgra does the GPU copy + fence).
        uint rowPitch = (uint)width * 4;
        nuint scratchBytes = (nuint)rowPitch * (nuint)height;
        IntPtr scratch = (IntPtr)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(scratchBytes, 256);

        // Publish the manifest with the SDDL PROJECTED from the capability's integrity (the cross-process
        // form of the capability's reach — data-driven, not a literal scattered in code).
        string sddl = SddlForIntegrity(Cm.Cm.Get(capPath)?.Integrity ?? "User");
        var map = DirectPortNative.ManifestMap.Create(manifestName, sddl);
        if (map == null)
        {
            Console.Error.WriteLine("ss surface: failed to publish manifest mapping");
            DirectPortNative.dp_unmap_memory(dp);
            DirectPortNative.dp_close(dp);
            DirectPortNative.dp_shutdown();
            return 1;
        }

        var m = map.Ptr;
        m->FrameValue  = 0;
        m->Width       = (uint)width;
        m->Height      = (uint)height;
        m->Format      = 87;                                  // DXGI_FORMAT_B8G8R8A8_UNORM
        m->AdapterLuid = DirectPortNative.Luid.FromInt64(luidRaw);
        DirectPortNative.WriteFixed(m->TextureName, 256, texName);
        DirectPortNative.WriteFixed(m->FenceName, 256, fenceName);
        m->Command     = 0;

        // Register the texture as a possession-gated VOM leaf at \Surfaces\<name>. Reclaim is the
        // deterministic close procedure run at refcount-zero / on Terminate — the let-it-crash kill.
        var owner = Vom.Vom.CreateOwner("\\Surfaces");
        DirectPortNative.ManifestMap mapRef = map; IntPtr dpRef = dp, scratchRef = scratch;
        Vom.Vom.RegisterNative(owner, "DirectPortSurface", dp, reclaim: () =>
        {
            try { mapRef.Dispose(); } catch { }
            try { DirectPortNative.dp_close(dpRef); } catch { }
            try { DirectPortNative.dp_shutdown(); } catch { }
            try { System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)scratchRef); } catch { }
        }, byteCount: (int)scratchBytes, format: VomFormat.Bytes, subdir: "", name: name);

        // Cooperative stop on Ctrl+C (cancellable per the wedged-thread hard limit).
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        Console.WriteLine($"ss surface: producing '{name}' {width}x{height} BGRA  (pitch {rowPitch}B, pid {pid})");
        Console.WriteLine($"  texture: {texName}");
        Console.WriteLine($"  fence:   {fenceName}");
        Console.WriteLine($"  manifest:{manifestName}   adapterLuid=0x{luidRaw:X}");
        Console.WriteLine($"  VOM:     \\Surfaces\\{name}   (gated by {capPath})");
        Console.WriteLine("  Open VirtuaCam -> Source -> this producer; then the Windows Camera app. Ctrl+C to stop.");

        byte* p = (byte*)scratch;
        ulong frame = 0;
        long* fvp = (long*)&m->FrameValue;   // atomic publish target (matches the producers' InterlockedExchange64)
        while (!stop.IsSet)
        {
            DrawTestPattern(p, rowPitch, width, height, frame);
            frame++;
            if (!DirectPortNative.dp_upload_bgra(dp, scratch, rowPitch, (uint)width, (uint)height, frame))
            { Console.Error.WriteLine("ss surface: dp_upload_bgra failed"); break; }   // GPU copy + shared-fence signal
            Interlocked.Exchange(ref *fvp, (long)frame);           // release-publish the counter for dirty readers
            stop.Wait(16);                                          // ~60 fps; wakes immediately on Ctrl+C
        }

        Console.WriteLine($"\nss surface: stopping — Terminate(\\Surfaces) reclaims the texture/fence/manifest");
        Vom.Vom.Terminate(owner);   // cascades Reclaim -> unmap/close/shutdown deterministically
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

    // Project the capability's integrity tier to the NT security descriptor stamped on the manifest
    // mapping (the cross-process reach). M0 baseline grants Authenticated Users so the VirtuaCam broker
    // (same interactive user) discovers it; the seam to tighten consumers to the broker SID lives here.
    private static string SddlForIntegrity(string integrity) => integrity switch
    {
        "System" => "D:P(A;;GA;;;SY)(A;;GR;;;BA)",   // SYSTEM full; Admins read
        "Admin"  => "D:P(A;;GA;;;BA)(A;;GR;;;AU)",   // Admins full; Authenticated Users read
        _        => "D:P(A;;GA;;;AU)",               // User/Untrusted: Authenticated Users (the SDK baseline)
    };

    private static unsafe bool VerifyManifestLayout(out string error)
    {
        error = "";
        int size = sizeof(DirectPortNative.BroadcastManifest);
        if (size != DirectPortNative.ManifestSize)
        { error = $"BroadcastManifest size {size} != {DirectPortNative.ManifestSize} (layout drift)"; return false; }
        long off(string f) => (long)System.Runtime.InteropServices.Marshal.OffsetOf<DirectPortNative.BroadcastManifest>(f);
        if (off("AdapterLuid") != 20 || off("TextureName") != 28 || off("FenceName") != 540 || off("Command") != 1052)
        { error = "BroadcastManifest field offsets drifted from the verified layout"; return false; }
        return true;
    }
}
