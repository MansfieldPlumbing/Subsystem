using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;

namespace Subsystem.Windows;

// ss directport — the DirectPort latency harness (#50). Proves the spine end-to-end in managed C#: a producer
// creates a shared GPU resource + fence, publishes a BroadcastManifest, and signals a rising frame value; a
// consumer Broadcast opens it by name and observes the same fence/manifest. Three modes:
//   ss directport bench [frames]      intraprocess fence signal->observe round-trip (the sync primitive)
//   ss directport produce [seconds]   long-running producer (for the cross-process consumer to attach to)
//   ss directport consume <pid> [n]   attach to a producer and measure cross-process notification latency
//
// Cross-process latency rides a tiny side-channel timestamp mapping (BenchClock): QueryPerformanceCounter is
// system-wide, so a tick written by the producer is directly comparable in the consumer. The canonical
// CPU-mapped DATA path (is_system_ram=true) is unavailable on the bundled directport.dll (INC000000000098:
// create sets ALLOW_CROSS_ADAPTER + ALLOW_UNORDERED_ACCESS -> E_INVALIDARG); these measure the sync/notify
// primitive, not data transport. Session-local NT names (no Global\, no admin).
internal static unsafe class DirectPortBench
{
    private const string Sddl = "D:P(A;;GA;;;AU)";   // the protocol SDDL — grant all to Authenticated Users
    private const uint   W = 256, H = 1;
    private const int    Format = 3;                 // DP_FORMAT_RAW_32BIT

    public static int Run(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "bench";
        switch (sub)
        {
            case "bench":
            case "latency":
                return RunBench(args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 ? n : 1000);
            case "produce":
                return RunProduce(args.Length > 1 && double.TryParse(args[1], out var s) && s > 0 ? s : 15.0);
            case "consume":
                if (args.Length < 2 || !int.TryParse(args[1], out var pid))
                { Console.Error.WriteLine("usage: ss directport consume <producer-pid> [samples]"); return 2; }
                return RunConsume(pid, args.Length > 2 && int.TryParse(args[2], out var c) && c > 0 ? c : 1000);
            default:
                Console.Error.WriteLine("usage: ss directport bench [frames] | produce [seconds] | consume <pid> [samples]");
                return 2;
        }
    }

    // ---- intraprocess: the bare fence signal->observe round-trip (the sync primitive) ----
    static int RunBench(int frames)
    {
        if (!DirectPortNative.dp_init())
        { Console.Error.WriteLine($"ss directport: dp_init failed (hr=0x{DirectPortNative.dp_last_hresult():X8})."); return 1; }

        int pid = Environment.ProcessId;
        IntPtr prod = DirectPortNative.dp_create_shared_resource(W, H, Format, false, $"ss_dp_btex_{pid}", $"ss_dp_bfence_{pid}");
        if (prod == IntPtr.Zero)
        { Console.Error.WriteLine($"ss directport: create_shared_resource failed (hr=0x{DirectPortNative.dp_last_hresult():X8})."); return 1; }

        string name = $"DirectPort_Producer_Manifest_{pid}";
        using var manifest = DirectPortNative.ManifestMap.Create(name, Sddl);
        if (manifest == null) { DirectPortNative.dp_close(prod); Console.Error.WriteLine("ss directport: manifest publish failed."); return 1; }
        FillManifest(manifest.Ptr, $"ss_dp_btex_{pid}", $"ss_dp_bfence_{pid}");

        using var consumer = Broadcast.Open(name);
        if (consumer == null) { DirectPortNative.dp_close(prod); Console.Error.WriteLine("ss directport: consumer Broadcast.Open failed."); return 1; }

        Console.WriteLine($"ss directport bench — intraprocess fence round-trip, {frames} frames, UMA={DirectPortNative.dp_is_uma()}");
        const int warmup = 50;
        var us = new double[frames];
        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
        long allocBefore = 0; int g0 = 0, g1 = 0, g2 = 0;   // GC baseline, captured at the start of the measured frames
        for (int i = 1; i <= frames + warmup; i++)
        {
            if (i == warmup + 1) { allocBefore = GC.GetAllocatedBytesForCurrentThread(); g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2); }
            long sent = Stopwatch.GetTimestamp();
            DirectPortNative.dp_signal_fence(prod, (ulong)i);
            manifest.Ptr->FrameValue = (ulong)i;
            consumer.WaitFrame((ulong)i);
            long delta = Stopwatch.GetTimestamp() - sent;
            if (i > warmup) us[i - warmup - 1] = delta * tickToUs;
        }
        long allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1, dg2 = GC.CollectionCount(2) - g2;
        DirectPortNative.dp_close(prod);
        Report("intraprocess fence signal -> observe", us);
        Console.WriteLine($"  GC across {frames} measured ops: gen0={dg0} gen1={dg1} gen2={dg2}; managed bytes allocated = {allocBytes}");
        return 0;
    }

    // ---- cross-process: a long-running producer the consumer attaches to ----
    static int RunProduce(double seconds)
    {
        if (!DirectPortNative.dp_init())
        { Console.Error.WriteLine($"ss directport: dp_init failed (hr=0x{DirectPortNative.dp_last_hresult():X8})."); return 1; }

        int pid = Environment.ProcessId;
        string tex = $"ss_dp_tex_{pid}", fence = $"ss_dp_fence_{pid}", name = $"DirectPort_Producer_Manifest_{pid}";
        IntPtr prod = DirectPortNative.dp_create_shared_resource(W, H, Format, false, tex, fence);
        if (prod == IntPtr.Zero)
        { Console.Error.WriteLine($"ss directport: create_shared_resource failed (hr=0x{DirectPortNative.dp_last_hresult():X8})."); return 1; }

        using var manifest = DirectPortNative.ManifestMap.Create(name, Sddl);
        if (manifest == null) { DirectPortNative.dp_close(prod); Console.Error.WriteLine("ss directport: manifest publish failed."); return 1; }
        FillManifest(manifest.Ptr, tex, fence);

        using var clock = BenchClock.Create($"ss_dp_ts_{pid}");
        if (clock == null) { DirectPortNative.dp_close(prod); Console.Error.WriteLine("ss directport: bench clock publish failed."); return 1; }

        Console.WriteLine($"ss directport produce — pid {pid}, ~1kHz frames for {seconds:F0}s");
        Console.WriteLine($"  attach with:  ss directport consume {pid}");
        var sw = Stopwatch.StartNew();
        ulong i = 0;
        long* fvp = (long*)&manifest.Ptr->FrameValue;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Volatile.Write(ref *clock.Slot, Stopwatch.GetTimestamp());   // send time (QPC; system-wide)
            DirectPortNative.dp_signal_fence(prod, ++i);
            Interlocked.Exchange(ref *fvp, (long)i);                     // release-publish the frame
            Thread.SpinWait(256);                                        // fast pacing (~us) so samples fill quickly
        }
        DirectPortNative.dp_close(prod);
        Console.WriteLine($"ss directport produce — done ({i} frames).");
        return 0;
    }

    static int RunConsume(int pid, int samples)
    {
        string name = $"DirectPort_Producer_Manifest_{pid}";
        using var consumer = Broadcast.Open(name);
        if (consumer == null)
        { Console.Error.WriteLine($"ss directport consume: no producer #{pid} (run 'ss directport produce' first)."); return 1; }
        using var clock = BenchClock.Open($"ss_dp_ts_{pid}");
        if (clock == null) { Console.Error.WriteLine($"ss directport consume: no bench clock for #{pid}."); return 1; }

        Console.WriteLine($"ss directport consume — attached to producer #{pid}, up to {samples} samples (cross-process)");
        var us = new double[samples];
        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
        ulong last = consumer.FrameValue;
        int got = 0;
        for (int s = 0; s < samples; s++)
        {
            ulong f; long spins = 0; bool stalled = false;
            while ((f = consumer.FrameValue) == last)
            {
                if (++spins > 200_000_000) { stalled = true; break; }   // producer stopped, or between frames
                Thread.SpinWait(1);
            }
            if (stalled) break;
            long now  = Stopwatch.GetTimestamp();
            long sent = Volatile.Read(ref *clock.Slot);
            us[got++] = (now - sent) * tickToUs;
            last = f;
        }
        if (got == 0) { Console.Error.WriteLine("ss directport consume: no frames observed (is the producer running?)."); return 1; }
        Array.Resize(ref us, got);
        Report("cross-process frame publish -> observe", us);
        return 0;
    }

    static void FillManifest(DirectPortNative.BroadcastManifest* mp, string tex, string fence)
    {
        DirectPortNative.dp_get_adapter_luid(out long luid);
        mp->Width = W; mp->Height = H; mp->Format = Format;
        mp->AdapterLuid = DirectPortNative.Luid.FromInt64(luid);
        DirectPortNative.WriteFixed(mp->TextureName, 256, tex);
        DirectPortNative.WriteFixed(mp->FenceName, 256, fence);
        mp->FrameValue = 0;
    }

    static void Report(string label, double[] us)
    {
        Array.Sort(us);
        int p50 = Math.Min(us.Length - 1, (int)(0.50 * us.Length));
        int p99 = Math.Min(us.Length - 1, (int)(0.99 * us.Length));
        Console.WriteLine($"  {label} latency (microseconds):");
        Console.WriteLine($"    min {us[0]:F3}   p50 {us[p50]:F3}   p99 {us[p99]:F3}   max {us[^1]:F3}   mean {us.Average():F3}");
        Console.WriteLine($"  ({us.Length} samples)");
    }

    // A tiny cross-process shared 8-byte slot carrying the producer's send timestamp. Separate from the
    // canonical 1056-byte BroadcastManifest so it never perturbs the VirtuaCam layout. NULL security
    // attributes = default DACL (same-session/user open), session-local name.
    private sealed unsafe class BenchClock : IDisposable
    {
        private IntPtr _h, _view;
        public long* Slot;
        private BenchClock(IntPtr h, IntPtr view) { _h = h; _view = view; Slot = (long*)view.ToPointer(); }

        public static BenchClock? Create(string name)
        {
            IntPtr h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04 /*PAGE_READWRITE*/, 0, 8, name);
            if (h == IntPtr.Zero) return null;
            IntPtr v = MapViewOfFile(h, 0xF001F /*FILE_MAP_ALL_ACCESS*/, 0, 0, (UIntPtr)8);
            if (v == IntPtr.Zero) { CloseHandle(h); return null; }
            return new BenchClock(h, v);
        }

        public static BenchClock? Open(string name)
        {
            IntPtr h = OpenFileMappingW(0x0006 /*READ|WRITE*/, false, name);
            if (h == IntPtr.Zero) return null;
            IntPtr v = MapViewOfFile(h, 0x0006, 0, 0, (UIntPtr)8);
            if (v == IntPtr.Zero) { CloseHandle(h); return null; }
            return new BenchClock(h, v);
        }

        public void Dispose()
        {
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; Slot = null; }
            if (_h != IntPtr.Zero) { CloseHandle(_h); _h = IntPtr.Zero; }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwHigh, uint dwLow, string lpName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.I1)] bool bInheritHandle, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hMap, uint dwDesiredAccess, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
