using System;
using System.Runtime.InteropServices;
using System.Threading;
using Subsystem.Vom;

namespace Subsystem.Windows;

// DirectPortProducer — the ONE in-proc DirectPort producer (#9). dp_init -> create a shared BGRA texture ->
// publish the manifest (SDDL projected from the capability's integrity) -> register the possession-gated VOM
// leaf \Surfaces\<name> -> write the 256-aligned Scratch + Commit() (GPU copy + fence signal + publish
// FrameValue) -> deterministic reclaim. The SOURCE is the boundary: `ss surface` draws a test pattern into
// Scratch; `ss stream` copies live Android frames into it. Same producer, two sources.
//
// Commit() MUST be called single-threaded (the producer's D3D context is single-threaded) — never two pumps.
internal sealed unsafe class DirectPortProducer
{
    private readonly IntPtr _dp;
    private readonly Owner _owner;
    private readonly long* _frameValue;
    private ulong _frame;

    public IntPtr Scratch { get; }
    public uint RowPitch { get; }
    public int Width { get; }
    public int Height { get; }

    private DirectPortProducer(IntPtr dp, IntPtr scratch, Owner owner, long* fvp, int w, int h, uint rowPitch)
    { _dp = dp; Scratch = scratch; _owner = owner; _frameValue = fvp; Width = w; Height = h; RowPitch = rowPitch; }

    // Stand up the producer (the caller does the capability gate first). Returns null + reason on failure.
    public static DirectPortProducer? Create(string name, int width, int height, string capPath, out string? error)
    {
        error = null;
        if (!VerifyManifestLayout(out error)) return null;
        if (!DirectPortNative.dp_init()) { error = "dp_init failed (no D3D12 device / directport.dll not found)"; return null; }

        long luidRaw = 0; DirectPortNative.dp_get_adapter_luid(out luidRaw);
        int pid = Environment.ProcessId;
        string manifestName = $"DirectPort_Producer_Manifest_{pid}";        // session-local (no Global\)
        string texName      = $"Global\\DirectPortTexture_{pid}";
        string fenceName    = $"Global\\DirectPortFence_{pid}";

        IntPtr dp = DirectPortNative.dp_create_shared_resource(
            (uint)width, (uint)height, (int)DirectPortNative.DpFormat.Video, false, texName, fenceName);
        if (dp == IntPtr.Zero)
        {
            error = $"dp_create_shared_resource failed (HRESULT 0x{DirectPortNative.dp_last_hresult():X8}, adapter UMA={DirectPortNative.dp_is_uma()})";
            DirectPortNative.dp_shutdown();
            return null;
        }

        uint rowPitch = (uint)width * 4;
        nuint scratchBytes = (nuint)rowPitch * (nuint)height;
        IntPtr scratch = (IntPtr)NativeMemory.AlignedAlloc(scratchBytes, 256);

        // Publish the manifest with the SDDL PROJECTED from the capability's integrity (cross-process reach).
        string sddl = SddlForIntegrity(Subsystem.Cm.Cm.Get(capPath)?.Integrity ?? "User");
        var map = DirectPortNative.ManifestMap.Create(manifestName, sddl);
        if (map == null)
        {
            error = "failed to publish manifest mapping";
            DirectPortNative.dp_close(dp); DirectPortNative.dp_shutdown();
            NativeMemory.AlignedFree((void*)scratch);
            return null;
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

        // Possession-gated VOM leaf at \Surfaces\<name>; Reclaim is the deterministic close at refcount-zero.
        var owner = Vom.Vom.CreateOwner("\\Surfaces");
        DirectPortNative.ManifestMap mapRef = map; IntPtr dpRef = dp, scratchRef = scratch;
        Vom.Vom.RegisterNative(owner, "DirectPortSurface", dp, reclaim: () =>
        {
            try { mapRef.Dispose(); } catch (Exception ex) { Dg.Log("surface", "reclaim manifest: " + ex.Message); }
            try { DirectPortNative.dp_close(dpRef); } catch (Exception ex) { Dg.Log("surface", "reclaim dp_close: " + ex.Message); }
            try { DirectPortNative.dp_shutdown(); } catch (Exception ex) { Dg.Log("surface", "reclaim dp_shutdown: " + ex.Message); }
            try { NativeMemory.AlignedFree((void*)scratchRef); } catch (Exception ex) { Dg.Log("surface", "reclaim free: " + ex.Message); }
        }, byteCount: (int)scratchBytes, format: VomFormat.Bytes, subdir: "", name: name);

        long* fvp = (long*)&m->FrameValue;   // atomic publish target (matches the producers' InterlockedExchange64)
        return new DirectPortProducer(dp, scratch, owner, fvp, width, height, rowPitch);
    }

    // Push the current Scratch contents: GPU copy + shared-fence signal + release-publish the frame counter.
    public bool Commit()
    {
        _frame++;
        if (!DirectPortNative.dp_upload_bgra(_dp, Scratch, RowPitch, (uint)Width, (uint)Height, _frame)) return false;
        Interlocked.Exchange(ref *_frameValue, (long)_frame);
        return true;
    }

    // Copy a tightly-packed (width*4) BGRA frame into the 256-aligned Scratch, then Commit. For sources that
    // hand back a managed BGRA buffer (the Android screen stream).
    public bool Upload(byte[] bgra)
    {
        long srcPitch = (long)Width * 4;
        fixed (byte* src = bgra)
        {
            byte* dst = (byte*)Scratch;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + y * srcPitch, dst + (long)y * RowPitch, RowPitch, srcPitch);
        }
        return Commit();
    }

    public void Stop() => Vom.Vom.Terminate(_owner);   // cascades Reclaim -> unmap/close/shutdown deterministically

    // Project the capability's integrity tier to the NT security descriptor stamped on the manifest mapping.
    private static string SddlForIntegrity(string integrity) => integrity switch
    {
        "System" => "D:P(A;;GA;;;SY)(A;;GR;;;BA)",   // SYSTEM full; Admins read
        "Admin"  => "D:P(A;;GA;;;BA)(A;;GR;;;AU)",   // Admins full; Authenticated Users read
        _        => "D:P(A;;GA;;;AU)",               // User/Untrusted: Authenticated Users (the SDK baseline)
    };

    // Refuse rather than silently corrupt the broker's read if the manifest struct ever drifts.
    private static bool VerifyManifestLayout(out string? error)
    {
        error = null;
        int size = sizeof(DirectPortNative.BroadcastManifest);
        if (size != DirectPortNative.ManifestSize)
        { error = $"BroadcastManifest size {size} != {DirectPortNative.ManifestSize} (layout drift)"; return false; }
        long off(string f) => (long)Marshal.OffsetOf<DirectPortNative.BroadcastManifest>(f);
        if (off("AdapterLuid") != 20 || off("TextureName") != 28 || off("FenceName") != 540 || off("Command") != 1052)
        { error = "BroadcastManifest field offsets drifted from the verified layout"; return false; }
        return true;
    }
}
