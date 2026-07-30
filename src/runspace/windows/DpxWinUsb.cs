using System;
using System.Runtime.InteropServices;
using System.Threading;
using Subsystem.Vom;

namespace Subsystem.Windows;

// DpWinUsb � Windows-side AOA host + WinUSB DMA driver.
// Drives the AOA handshake over USB control transfers, opens the bulk IN/OUT endpoints,
// and DMA-fills a VOM handle on every received frame (zero CPU copy in the frame path).
//
// Frame receive path:
//   WinUsb_ReadPipe(RAW_IO) ? handle.Resource (256-aligned VOM region = DirectPortProducer.Scratch)
//   ? Fence.Signal ? DirectPortProducer.Commit() ? D3D12 blit ? Global\DirectPortTexture_{pid}
//
// Frame send path (executive / reverse flow):
//   D3D12 readback ? handle.Resource ? WinUsb_WritePipe ? AOA bulk OUT
//
// No async. No Task. No CancellationToken.
// Vom.Spawn threads, Thread.Interrupt on stop. SS009/SS018.
internal sealed unsafe class DpWinUsb : IDisposable
{
    private readonly string _serial;
    private readonly DirectPortProducer? _producer;
    private readonly Owner _owner;

    private IntPtr _winUsbHandle = IntPtr.Zero;
    private IntPtr _deviceHandle = IntPtr.Zero;
    private byte _bulkIn;
    private byte _bulkOut;
    private ulong _fence;
    private int _stopped;

    // Wire descriptor: [fence:8][x:2][y:2][w:2][h:2][size:4] = 20 bytes
    private const int DescriptorSize = 20;

    // AOA vendor request codes (USB IF Open Accessory spec)
    private const byte AoaGetProtocol    = 0x33;
    private const byte AoaSendString     = 0x34;
    private const byte AoaStartAccessory = 0x35;

    // AOA VID after mode switch
    private const ushort AoaVid    = 0x18D1;
    private const ushort AoaPidAcc = 0x2D00;

    private DpWinUsb(string serial, Owner owner, DirectPortProducer? producer)
    {
        _serial   = serial;
        _owner    = owner;
        _producer = producer;
    }

    // Open: enumerate ? AOA handshake ? WinUSB init ? Vom.Spawn receive + blit threads.
    // Returns null + reason on failure.
    public static DpWinUsb? Open(string serial, int width, int height, string capPath, out string? error)
    {
        error = null;

        // 1. Find device � already in AOA mode, or switch it
        IntPtr devHandle = FindDevice(serial, AoaVid, AoaPidAcc);
        if (devHandle == IntPtr.Zero)
        {
            devHandle = FindAndSwitchToAoa(serial, out error);
            if (devHandle == IntPtr.Zero) return null;
        }

        // 2. WinUSB initialize
        if (!WinUsb_Initialize(devHandle, out IntPtr winUsb))
        {
            error = $"WinUsb_Initialize failed: 0x{Marshal.GetLastWin32Error():X8}";
            CloseHandle(devHandle);
            return null;
        }

        // 3. Query interface, locate bulk IN/OUT endpoints
        if (!WinUsb_QueryInterfaceSettings(winUsb, 0, out USB_INTERFACE_DESCRIPTOR ifDesc))
        {
            error = "WinUsb_QueryInterfaceSettings failed";
            WinUsb_Free(winUsb); CloseHandle(devHandle);
            return null;
        }

        byte bulkIn = 0, bulkOut = 0;
        for (byte i = 0; i < ifDesc.bNumEndpoints; i++)
        {
            if (!WinUsb_QueryPipe(winUsb, 0, i, out WINUSB_PIPE_INFORMATION pipe)) continue;
            if (pipe.PipeType == 3) // bulk
            {
                if ((pipe.PipeId & 0x80) != 0) bulkIn  = pipe.PipeId;
                else                            bulkOut = pipe.PipeId;
            }
        }

        if (bulkIn == 0 || bulkOut == 0)
        {
            error = "AOA bulk IN/OUT endpoints not found";
            WinUsb_Free(winUsb); CloseHandle(devHandle);
            return null;
        }

        // 4. RAW_IO � zero-copy DMA into caller-supplied buffer
        uint rawIo = 1;
        WinUsb_SetPipePolicy(winUsb, bulkIn, PIPE_POLICY_RAW_IO, sizeof(uint), &rawIo);

        // 5. Stand up DirectPort producer � Scratch IS the DMA target
        var producer = DirectPortProducer.Create($"UsbSurface_{serial}", width, height, capPath, out string? dpErr);
        if (producer == null)
        {
            error = $"DirectPortProducer failed: {dpErr}";
            WinUsb_Free(winUsb); CloseHandle(devHandle);
            return null;
        }

        var owner = Vom.Vom.CreateOwner($"\\Device\\Usb\\{serial}");
        var dp    = new DpWinUsb(serial, owner, producer)
        {
            _winUsbHandle = winUsb,
            _deviceHandle = devHandle,
            _bulkIn       = bulkIn,
            _bulkOut      = bulkOut,
        };

        // 6. Spawn receive + blit threads under the VOM owner (cascade-killed on Terminate)
        Vom.Vom.Spawn(owner, "UsbRx",   _ => dp.ReceiveLoop());
        Vom.Vom.Spawn(owner, "UsbBlit", _ => dp.BlitLoop());

        Dg.Log("dp-usb", $"opened serial={serial} {width}x{height} in=0x{bulkIn:X2} out=0x{bulkOut:X2}");
        return dp;
    }

    // ReceiveLoop: Vom.Spawn'd thread. Reads 20-byte descriptor then full YUV frame directly
    // into DirectPortProducer.Scratch via WinUSB DMA. Latest-wins: prior frame overwritten in place.
    private void ReceiveLoop()
    {
        byte[] desc = new byte[DescriptorSize];
        fixed (byte* descPtr = desc)
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                try
                {
                    uint got = 0;
                    if (!WinUsb_ReadPipe(_winUsbHandle, _bulkIn, descPtr, DescriptorSize, &got, IntPtr.Zero) || got != DescriptorSize)
                    { Dg.Warn("dp-usb", "descriptor read failed"); break; }

                    ulong fenceVal  = *(ulong*)(descPtr + 0);
                    uint  frameBytes = *(uint*)(descPtr + 16);
                    if (frameBytes == 0) continue;

                    // DMA into Scratch (the 256-aligned region DirectPortProducer already owns)
                    uint rxd = 0;
                    if (!WinUsb_ReadPipe(_winUsbHandle, _bulkIn, (byte*)_producer!.Scratch, frameBytes, &rxd, IntPtr.Zero) || rxd != frameBytes)
                    { Dg.Warn("dp-usb", "frame DMA failed"); continue; }

                    Volatile.Write(ref _fence, fenceVal); // doorbell: BlitLoop wakes on next poll
                }
                catch (ThreadInterruptedException) { break; }
                catch (Exception ex) { Dg.Warn("dp-usb", $"rx: {ex.Message}"); break; }
            }
        }
    }

    // BlitLoop: Vom.Spawn'd thread. Watches _fence, calls Commit() to blit Scratch ? D3D12
    // texture and publish the shared GPU handle. Drop if behind � never queue.
    private void BlitLoop()
    {
        ulong lastSeen = 0;
        while (Volatile.Read(ref _stopped) == 0)
        {
            try
            {
                ulong current = Volatile.Read(ref _fence);
                if (current == lastSeen) { Thread.Sleep(1); continue; }
                lastSeen = current;
                _producer!.Commit(); // blit Scratch ? D3D12, signals DirectPort FrameValue
            }
            catch (ThreadInterruptedException) { break; }
            catch (Exception ex) { Dg.Warn("dp-usb", $"blit: {ex.Message}"); }
        }
    }

    // Send raw bytes over AOA bulk OUT � executive / reverse path (tap injection, PC?phone frame).
    public void Send(ReadOnlySpan<byte> data)
    {
        if (_winUsbHandle == IntPtr.Zero || Volatile.Read(ref _stopped) != 0) return;
        fixed (byte* p = data)
        {
            uint sent = 0;
            if (!WinUsb_WritePipe(_winUsbHandle, _bulkOut, p, (uint)data.Length, &sent, IntPtr.Zero))
                Dg.Warn("dp-usb", $"send: 0x{Marshal.GetLastWin32Error():X8}");
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _producer?.Stop();
        Vom.Vom.Terminate(_owner);
        if (_winUsbHandle != IntPtr.Zero) { WinUsb_Free(_winUsbHandle); _winUsbHandle = IntPtr.Zero; }
        if (_deviceHandle != IntPtr.Zero) { CloseHandle(_deviceHandle); _deviceHandle = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    // ---- AOA handshake ------------------------------------------------------------------

    private static IntPtr FindDevice(string serial, ushort vid, ushort pid)
    {
        // SetupDi walk � only match devices already in AOA mode (VID 0x18D1 PID 0x2D00)
        return SetupDiWalkUsbDevices(serial, vid, pid);
    }

    private static IntPtr FindAndSwitchToAoa(string serial, out string? error)
    {
        error = null;

        IntPtr devHandle = SetupDiWalkUsbDevices(serial, 0, 0);
        if (devHandle == IntPtr.Zero) { error = $"Device not found (serial={serial})"; return IntPtr.Zero; }

        if (!WinUsb_Initialize(devHandle, out IntPtr winUsb))
        {
            error = $"pre-AOA WinUsb_Initialize: 0x{Marshal.GetLastWin32Error():X8}";
            CloseHandle(devHandle); return IntPtr.Zero;
        }

        // GET_PROTOCOL
        byte[] proto = new byte[2]; uint tx = 0;
        var sp = new WINUSB_SETUP_PACKET { RequestType = 0xC0, Request = AoaGetProtocol, Length = 2 };
        fixed (byte* pp = proto) WinUsb_ControlTransfer(winUsb, sp, pp, 2, &tx, IntPtr.Zero);
        if (tx < 2 || proto[0] < 1)
        {
            error = "AOA not supported by device (protocol < 1)";
            WinUsb_Free(winUsb); CloseHandle(devHandle); return IntPtr.Zero;
        }

        // SEND_STRINGs
        string[] strings = { "Subsystem", "ss", "Subsystem DirectPort Surface", "1.0", "", serial };
        for (ushort idx = 0; idx < strings.Length; idx++)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(strings[idx] + "\0");
            fixed (byte* bp = b)
            {
                uint stx = 0;
                var s = new WINUSB_SETUP_PACKET { RequestType = 0x40, Request = AoaSendString, Index = idx, Length = (ushort)b.Length };
                WinUsb_ControlTransfer(winUsb, s, bp, (uint)b.Length, &stx, IntPtr.Zero);
            }
        }

        // START_ACCESSORY � device re-enumerates
        var start = new WINUSB_SETUP_PACKET { RequestType = 0x40, Request = AoaStartAccessory };
        uint dummy = 0;
        WinUsb_ControlTransfer(winUsb, start, null, 0, &dummy, IntPtr.Zero);
        WinUsb_Free(winUsb); CloseHandle(devHandle);

        Thread.Sleep(2000); // wait for re-enumeration
        IntPtr aoa = SetupDiWalkUsbDevices(serial, AoaVid, AoaPidAcc);
        if (aoa == IntPtr.Zero) error = $"AOA device did not appear after 2s (serial={serial})";
        return aoa;
    }

    private static IntPtr SetupDiWalkUsbDevices(string serial, ushort vid, ushort pid)
    {
        Guid usbGuid = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        IntPtr di = SetupDiGetClassDevs(ref usbGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (di == new IntPtr(-1)) return IntPtr.Zero;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(di, IntPtr.Zero, ref usbGuid, i, ref ifData); i++)
            {
                uint needed = 0;
                SetupDiGetDeviceInterfaceDetailW(di, ref ifData, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    SP_DEVINFO_DATA dd = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiGetDeviceInterfaceDetailW(di, ref ifData, buf, needed, ref needed, ref dd)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(buf, IntPtr.Size)) ?? "";
                    if (vid != 0 && !path.Contains($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pid != 0 && !path.Contains($"pid_{pid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(serial) && !path.Contains(serial, StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                    if (h != new IntPtr(-1)) return h;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(di); }
        return IntPtr.Zero;
    }

    // ---- P/Invoke (all Windows in-box: WinUSB + SetupAPI + Kernel32) --------------------

    private const uint DIGCF_PRESENT         = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ          = 0x80000000;
    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint FILE_SHARE_READ       = 0x01;
    private const uint FILE_SHARE_WRITE      = 0x02;
    private const uint OPEN_EXISTING         = 3;
    private const uint FILE_FLAG_OVERLAPPED  = 0x40000000;
    private const uint PIPE_POLICY_RAW_IO    = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct USB_INTERFACE_DESCRIPTOR
    { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_PIPE_INFORMATION
    { public uint PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WINUSB_SETUP_PACKET
    { public byte RequestType, Request; public ushort Value, Index, Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(IntPtr deviceHandle, out IntPtr winUsbHandle);
    [DllImport("winusb.dll")] private static extern bool WinUsb_Free(IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr h, byte alt, out USB_INTERFACE_DESCRIPTOR desc);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr h, byte alt, byte idx, out WINUSB_PIPE_INFORMATION info);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr h, byte pipeId, uint policy, uint len, void* val);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr h, byte pipeId, byte* buf, uint len, uint* transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr h, byte pipeId, byte* buf, uint len, uint* transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(IntPtr h, WINUSB_SETUP_PACKET setup, byte* buf, uint len, uint* transferred, IntPtr overlapped);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr di, IntPtr diData, ref Guid guid, uint idx, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr di, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, ref uint required, ref SP_DEVINFO_DATA ddData);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr di, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, ref uint required, IntPtr ddData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
