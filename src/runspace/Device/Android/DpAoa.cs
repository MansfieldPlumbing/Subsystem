using System;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Subsystem.Vom;

namespace Subsystem.Device;

// DpAoa — Android AOA driver, the device-mode + host-mode counterpart to windows/DpWinUsb.cs (CRQ185
// rung 2: the ADB-free USB pipe). Same wire contract on every rung — one AOA courier, three hosts
// (Windows WinUsb_ReadPipe / Android-as-host UsbDeviceConnection.BulkTransfer / Android-as-device
// UsbAccessory FD) — a 20-byte descriptor [fence:8][x:2][y:2][w:2][h:2][size:4] followed by `size`
// bytes of BGRA32 frame data. Latest-wins: a stale frame is superseded, never queued (invariant 8).
//
// DpAoaDevice — THIS phone answers as the USB accessory (a Windows PC, or another phone in host mode,
//   drives the handshake). Path: UsbManager.OpenAccessory(UsbAccessory) -> ParcelFileDescriptor ->
//   Java.IO.FileInputStream/FileOutputStream over the kernel's /dev/usb_accessory node.
//
// DpAoaHost — THIS phone drives another phone as USB host (federated mesh, CRQ173 — no PC required):
//   enumerates UsbDevice, drives the SAME AOA control-transfer handshake DpWinUsb.FindAndSwitchToAoa
//   drives (GET_PROTOCOL / SEND_STRING x6 / START_ACCESSORY), waits for the peer to re-enumerate in
//   accessory mode, then opens its bulk endpoints via UsbDeviceConnection.BulkTransfer.
//
// No async. No Task. Vom.Spawn threads, Thread.Interrupt on stop. SS009/SS018.
internal static class DpAoa
{
    public const int DescriptorSize = 20;

    // AOA vendor request codes (USB IF Open Accessory spec) — identical to DpWinUsb.cs's constants.
    public const byte AoaGetProtocol    = 0x33;
    public const byte AoaSendString     = 0x34;
    public const byte AoaStartAccessory = 0x35;

    public const int AoaVid    = 0x18D1;
    public const int AoaPidAcc = 0x2D00;

    // Identity strings sent during SEND_STRING — index 5 (serial) is filled in per-target by the caller.
    // Matches DpWinUsb.FindAndSwitchToAoa's `strings` array verbatim so either end can drive the handshake.
    public static string[] IdentityStrings(string serial) =>
        new[] { "Subsystem", "ss", "Subsystem DirectPort Surface", "1.0", "", serial };

    internal static void WriteDescriptor(Span<byte> desc, ulong fence, int x, int y, int w, int h, int size)
    {
        BitConverter.TryWriteBytes(desc.Slice(0, 8),  fence);
        BitConverter.TryWriteBytes(desc.Slice(8, 2),  (ushort)x);
        BitConverter.TryWriteBytes(desc.Slice(10, 2), (ushort)y);
        BitConverter.TryWriteBytes(desc.Slice(12, 2), (ushort)w);
        BitConverter.TryWriteBytes(desc.Slice(14, 2), (ushort)h);
        BitConverter.TryWriteBytes(desc.Slice(16, 4), (uint)size);
    }
}

// DpAoaDevice — accessory-mode (device role). Opened from MainActivity when
// UsbManager.ActionUsbAccessoryAttached fires (or on cold start, when UsbManager.AccessoryList is
// already non-empty — the OS relaunches the app with the same intent for an already-attached accessory).
internal sealed class DpAoaDevice : IDisposable
{
    private readonly Owner _owner;
    private ParcelFileDescriptor? _pfd;
    private Java.IO.FileInputStream? _rx;
    private Java.IO.FileOutputStream? _tx;
    private int _stopped;
    private ulong _lastFenceRx;

    // Fires on the RX thread with a reverse-direction frame (host -> phone: executive input, PC-pushed
    // frame). Latest-wins: the caller (DirectPortVk consumer) drops a stale callback for a fresher one.
    public Action<ulong, byte[]>? OnFrameReceived;

    private DpAoaDevice(Owner owner) { _owner = owner; }

    public static DpAoaDevice? Open(Context ctx, UsbAccessory accessory, out string? error)
    {
        error = null;
        var manager = (UsbManager?)ctx.GetSystemService(Context.UsbService);
        if (manager == null) { error = "UsbManager unavailable"; return null; }
        if (!manager.HasPermission(accessory)) { error = "no permission (RequestPermission first)"; return null; }

        var pfd = manager.OpenAccessory(accessory);
        if (pfd == null) { error = "OpenAccessory failed"; return null; }

        var fd = pfd.FileDescriptor;
        if (fd == null) { error = "null file descriptor"; pfd.Close(); return null; }

        var owner = Vom.Vom.CreateOwner("\\Device\\Usb\\Accessory");
        var dev = new DpAoaDevice(owner)
        {
            _pfd = pfd,
            _rx = new Java.IO.FileInputStream(fd),
            _tx = new Java.IO.FileOutputStream(fd),
        };

        Vom.Vom.Spawn(owner, "AoaRx", _ => dev.ReceiveLoop());
        Dg.Log("dp-aoa", $"device opened manufacturer={accessory.Manufacturer} model={accessory.Model} serial={accessory.Serial}");
        return dev;
    }

    // Send a captured frame (fence + BGRA32 bytes) to the host — the screen-push path.
    public void SendFrame(ulong fence, int x, int y, int w, int h, byte[] bgra)
    {
        if (_tx == null || Volatile.Read(ref _stopped) != 0) return;
        byte[] desc = new byte[DpAoa.DescriptorSize];
        DpAoa.WriteDescriptor(desc, fence, x, y, w, h, bgra.Length);
        try
        {
            _tx.Write(desc);
            _tx.Write(bgra);
            _tx.Flush();
        }
        catch (Exception ex) { Dg.Warn("dp-aoa", $"send: {ex.Message}"); }
    }

    private void ReceiveLoop()
    {
        byte[] desc = new byte[DpAoa.DescriptorSize];
        while (Volatile.Read(ref _stopped) == 0)
        {
            try
            {
                if (!ReadFull(_rx!, desc, DpAoa.DescriptorSize)) break;
                ulong fenceVal   = BitConverter.ToUInt64(desc, 0);
                uint  frameBytes = BitConverter.ToUInt32(desc, 16);
                if (frameBytes == 0) continue;

                byte[] payload = new byte[frameBytes];
                if (!ReadFull(_rx!, payload, (int)frameBytes)) break;

                Volatile.Write(ref _lastFenceRx, fenceVal);
                OnFrameReceived?.Invoke(fenceVal, payload);
            }
            catch (ThreadInterruptedException) { break; }
            catch (Exception ex) { Dg.Warn("dp-aoa", $"rx: {ex.Message}"); break; }   // pfd closed / IOException
        }
    }

    private static bool ReadFull(Java.IO.FileInputStream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int r = s.Read(buf, read, count - read);
            if (r <= 0) return false;
            read += r;
        }
        return true;
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Vom.Vom.Terminate(_owner);
        try { _rx?.Close(); } catch { }
        try { _tx?.Close(); } catch { }
        try { _pfd?.Close(); } catch { }
    }

    public void Dispose() => Stop();
}

// DpAoaHost — USB host mode (phone drives another phone, CRQ173: the federated mesh, no PC required).
// Two-phase: first attach drives the AOA handshake and returns null (the peer re-enumerates as an
// accessory-mode device); the SECOND ActionUsbDeviceAttached for the same peer opens the bulk pipe.
internal sealed class DpAoaHost : IDisposable
{
    private readonly UsbDeviceConnection _conn;
    private readonly UsbEndpoint _bulkIn;
    private readonly UsbEndpoint _bulkOut;
    private readonly Owner _owner;
    private int _stopped;
    private ulong _lastFenceRx;

    public Action<ulong, byte[]>? OnFrameReceived;

    private DpAoaHost(UsbDeviceConnection conn, UsbEndpoint bulkIn, UsbEndpoint bulkOut, Owner owner)
    { _conn = conn; _bulkIn = bulkIn; _bulkOut = bulkOut; _owner = owner; }

    public static DpAoaHost? Open(Context ctx, UsbDevice device, out string? error)
    {
        error = null;
        var manager = (UsbManager?)ctx.GetSystemService(Context.UsbService);
        if (manager == null) { error = "UsbManager unavailable"; return null; }
        if (!manager.HasPermission(device)) { error = "no permission (RequestPermission first)"; return null; }

        if (device.VendorId != DpAoa.AoaVid || device.ProductId != DpAoa.AoaPidAcc)
        {
            // Not in accessory mode yet — drive the handshake (mirrors DpWinUsb.FindAndSwitchToAoa).
            // The peer re-enumerates; the OS re-fires ActionUsbDeviceAttached and the caller re-attaches.
            if (!SwitchToAoa(manager, device, out error)) return null;
            error = "switched peer to accessory mode; waiting for re-enumeration";
            return null;
        }

        var conn = manager.OpenDevice(device);
        if (conn == null) { error = "OpenDevice failed"; return null; }

        UsbInterface? iface = device.InterfaceCount > 0 ? device.GetInterface(0) : null;
        if (iface == null) { error = "no interface"; conn.Close(); return null; }
        if (!conn.ClaimInterface(iface, true)) { error = "ClaimInterface failed"; conn.Close(); return null; }

        UsbEndpoint? bulkIn = null, bulkOut = null;
        for (int i = 0; i < iface.EndpointCount; i++)
        {
            var ep = iface.GetEndpoint(i);
            if (ep == null || ep.Type != UsbAddressing.XferBulk) continue;
            if (ep.Direction == UsbAddressing.In) bulkIn = ep; else bulkOut = ep;
        }
        if (bulkIn == null || bulkOut == null)
        {
            error = "bulk endpoints not found";
            conn.ReleaseInterface(iface); conn.Close();
            return null;
        }

        var owner = Vom.Vom.CreateOwner($"\\Device\\Usb\\Host\\{device.DeviceName}");
        var host  = new DpAoaHost(conn, bulkIn, bulkOut, owner);
        Vom.Vom.Spawn(owner, "AoaHostRx", _ => host.ReceiveLoop());
        Dg.Log("dp-aoa", $"host opened device={device.DeviceName} in=0x{bulkIn.Address:X2} out=0x{bulkOut.Address:X2}");
        return host;
    }

    // Drives GET_PROTOCOL / SEND_STRING x6 / START_ACCESSORY over a raw (pre-AOA) UsbDevice — the same
    // sequence DpWinUsb.FindAndSwitchToAoa runs when a Windows PC is the host. Android-as-host needs no
    // WinUSB driver install; UsbManager.OpenDevice on a raw device is enough to issue control transfers.
    private static bool SwitchToAoa(UsbManager manager, UsbDevice device, out string? error)
    {
        error = null;
        var conn = manager.OpenDevice(device);
        if (conn == null) { error = "pre-AOA OpenDevice failed"; return false; }
        try
        {
            byte[] proto = new byte[2];
            int got = conn.ControlTransfer((UsbAddressing)0xC0, DpAoa.AoaGetProtocol, 0, 0, proto, 2, 5000);
            if (got < 2 || proto[0] < 1) { error = "AOA not supported by peer (protocol < 1)"; return false; }

            var strings = DpAoa.IdentityStrings(device.DeviceName ?? "");
            for (int idx = 0; idx < strings.Length; idx++)
            {
                byte[] b = System.Text.Encoding.UTF8.GetBytes(strings[idx] + "\0");
                conn.ControlTransfer((UsbAddressing)0x40, DpAoa.AoaSendString, 0, idx, b, b.Length, 5000);
            }

            conn.ControlTransfer((UsbAddressing)0x40, DpAoa.AoaStartAccessory, 0, 0, null, 0, 5000);
            return true;
        }
        finally { conn.Close(); }
    }

    public void SendFrame(ulong fence, int x, int y, int w, int h, byte[] bgra)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        byte[] desc = new byte[DpAoa.DescriptorSize];
        DpAoa.WriteDescriptor(desc, fence, x, y, w, h, bgra.Length);
        try
        {
            _conn.BulkTransfer(_bulkOut, desc, desc.Length, 2000);
            _conn.BulkTransfer(_bulkOut, bgra, bgra.Length, 5000);
        }
        catch (Exception ex) { Dg.Warn("dp-aoa", $"host send: {ex.Message}"); }
    }

    private void ReceiveLoop()
    {
        byte[] desc = new byte[DpAoa.DescriptorSize];
        while (Volatile.Read(ref _stopped) == 0)
        {
            try
            {
                int got = _conn.BulkTransfer(_bulkIn, desc, desc.Length, 2000);
                if (got != DpAoa.DescriptorSize) continue;   // timeout/short read — latest-wins, just retry

                ulong fenceVal   = BitConverter.ToUInt64(desc, 0);
                uint  frameBytes = BitConverter.ToUInt32(desc, 16);
                if (frameBytes == 0) continue;

                byte[] payload = new byte[frameBytes];
                int rxd = _conn.BulkTransfer(_bulkIn, payload, payload.Length, 5000);
                if (rxd != frameBytes) continue;

                Volatile.Write(ref _lastFenceRx, fenceVal);
                OnFrameReceived?.Invoke(fenceVal, payload);
            }
            catch (ThreadInterruptedException) { break; }
            catch (Exception ex) { Dg.Warn("dp-aoa", $"host rx: {ex.Message}"); break; }
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Vom.Vom.Terminate(_owner);
        try { _conn.Close(); } catch { }
    }

    public void Dispose() => Stop();
}
