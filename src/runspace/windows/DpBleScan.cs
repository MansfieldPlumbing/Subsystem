using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Subsystem.Device;
using Subsystem.Vom;

namespace Subsystem.Windows;

// DpBleScan — pure Win32 P/Invoke GATT client for an ALREADY-PAIRED BLE device. Never scans for new/
// unpaired devices — see [[no-winrt-native-c-abi]] for why THAT specific piece needs WinRT and is closed
// out of scope. Pair the phone through Windows' normal "Add a Bluetooth device" flow once (DpBleAdvert.cs
// makes it show up there like any BLE accessory, since it's connectable + advertises the ss service
// UUID); after that one manual step, this enumerates it via SetupDiGetClassDevs against
// GUID_BLUETOOTHLE_DEVICE_INTERFACE — the SAME pattern DpWinUsb.cs uses for WinUSB — opens it with
// CreateFile, and talks GATT via bluetoothapis.dll (BluetoothGATTGetServices/GetCharacteristics/
// Get|SetCharacteristicValue/RegisterEvent). No COM, no WinRT, no managed projection — Scott, 2026-07-02:
// "surely i would want them ... to chit chat over bluetooth" — after pairing, yes, and entirely in CoreCLR.
//
// UNVERIFIED AGAINST A LIVE DEVICE: authored from Microsoft's published bluetoothleapis.h/bthledef.h
// signatures and struct layouts (web research — learn.microsoft.com + the winsdk-10 header source — not
// compiled/run against real hardware in this environment). BTH_LE_UUID's explicit layout follows standard
// C struct alignment rules that were reasoned about, not observed on this machine. Pair a phone via
// Windows Settings, then run `ss ble-scan` to validate before relying on this for anything real.
//
// No async. No Task. Vom.Spawn threads, Thread.Interrupt on stop. SS009/SS018.
internal static class DpBleScan
{
    // Same UUIDs Device/Android/DpBleAdvert.cs advertises.
    private static readonly Guid ServiceUuid       = new("5b3f1a10-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid DeviceNameUuid    = new("5b3f1a11-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid CapabilitiesUuid  = new("5b3f1a12-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid TransportUuid     = new("5b3f1a13-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid McpEndpointUuid   = new("5b3f1a14-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid McpRxUuid         = new("5b3f1a15-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid McpTxUuid         = new("5b3f1a16-6b1e-4e2a-9c2e-737562737973");

    private static readonly Guid GuidBluetoothLeDeviceInterface = new("781aee18-7733-4ce4-add0-91f41c67b592");

    // `ss ble-scan` — enumerate already-paired ss peers, mount each at \Device\Peers\<serial>, and (if the
    // peer offers nothing better than ble-gatt) subscribe to its Mcp Tx notify characteristic.
    public static int Run(string[] args)
    {
        var owner = Vom.Vom.CreateOwner("\\Device\\Peers");
        int found = 0;
        foreach (string devicePath in EnumerateBleDevicePaths())
        {
            IntPtr h = OpenDevice(devicePath);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (MountPeer(owner, h)) found++;
            }
            finally { CloseHandle(h); }
        }
        Console.WriteLine($"ss ble-scan: {found} ss peer(s) mounted under \\Device\\Peers");
        return 0;
    }

    private static bool MountPeer(Owner root, IntPtr hDevice)
    {
        if (!TryFindService(hDevice, ServiceUuid, out var service)) return false;

        var chars = GetCharacteristics(hDevice, service);
        if (!TryFind(chars, DeviceNameUuid, out var nameChar)) return false;

        string name        = ReadUtf8(hDevice, nameChar)  ?? "peer";
        byte[]? capsBytes  = ReadBytes(hDevice, chars, CapabilitiesUuid);
        byte   caps        = capsBytes is { Length: > 0 } ? capsBytes[0] : (byte)0;
        string transport   = ReadUtf8(hDevice, chars, TransportUuid)   ?? "ble";
        string mcpEndpoint = ReadUtf8(hDevice, chars, McpEndpointUuid) ?? "ble-gatt";

        int sp = name.LastIndexOf(' ');
        string serial = sp >= 0 ? name[(sp + 1)..] : name;

        var owner = Vom.Vom.CreateOwner($"\\Device\\Peers\\{serial}");
        Vom.Vom.Register(owner, "BlePeer", new PeerRecord(name, caps, transport, mcpEndpoint), subdir: "", name: "Info");
        Dg.Log("dp-ble", $"mounted \\Device\\Peers\\{serial} caps=0x{caps:X2} transport={transport} endpoint={mcpEndpoint}");

        if (mcpEndpoint == "ble-gatt" && TryFind(chars, McpTxUuid, out var txChar))
        {
            // Own handle per subscription — BluetoothGATTRegisterEvent needs the handle to stay open for
            // the life of the subscription; the spawned thread just parks (the callback fires on its own
            // OS thread), Vom.Spawn gives it a tracked lifetime + cascade-kill on Terminate.
            IntPtr subHandle = DuplicateForSubscription(hDevice);
            if (subHandle != IntPtr.Zero)
                Vom.Vom.Spawn(owner, "BleMcpTx", _ => WatchNotify(subHandle, txChar));
        }
        return true;
    }

    private static string? ReadUtf8(IntPtr h, BTH_LE_GATT_CHARACTERISTIC c) { var b = ReadValue(h, c); return b == null ? null : Encoding.UTF8.GetString(b); }
    private static string? ReadUtf8(IntPtr h, BTH_LE_GATT_CHARACTERISTIC[] chars, Guid uuid)
        => TryFind(chars, uuid, out var c) ? ReadUtf8(h, c) : null;
    private static byte[]? ReadBytes(IntPtr h, BTH_LE_GATT_CHARACTERISTIC[] chars, Guid uuid)
        => TryFind(chars, uuid, out var c) ? ReadValue(h, c) : null;

    private static bool TryFind(BTH_LE_GATT_CHARACTERISTIC[] chars, Guid uuid, out BTH_LE_GATT_CHARACTERISTIC found)
    {
        foreach (var c in chars) { if (ToGuid(c.CharacteristicUuid) == uuid) { found = c; return true; } }
        found = default; return false;
    }

    // ---- GATT calls ------------------------------------------------------------------------------

    private static bool TryFindService(IntPtr hDevice, Guid uuid, out BTH_LE_GATT_SERVICE service)
    {
        service = default;
        ushort count = 0;
        BluetoothGATTGetServices(hDevice, 0, null, out count, 0);
        if (count == 0) return false;
        var buf = new BTH_LE_GATT_SERVICE[count];
        int hr = BluetoothGATTGetServices(hDevice, count, buf, out ushort actual, 0);
        if (hr != 0) return false;
        for (int i = 0; i < actual; i++)
        {
            if (ToGuid(buf[i].ServiceUuid) == uuid) { service = buf[i]; return true; }
        }
        return false;
    }

    private static BTH_LE_GATT_CHARACTERISTIC[] GetCharacteristics(IntPtr hDevice, BTH_LE_GATT_SERVICE service)
    {
        ushort count = 0;
        BluetoothGATTGetCharacteristics(hDevice, ref service, 0, null, out count, 0);
        if (count == 0) return Array.Empty<BTH_LE_GATT_CHARACTERISTIC>();
        var buf = new BTH_LE_GATT_CHARACTERISTIC[count];
        int hr = BluetoothGATTGetCharacteristics(hDevice, ref service, count, buf, out ushort actual, 0);
        return hr == 0 ? buf : Array.Empty<BTH_LE_GATT_CHARACTERISTIC>();
    }

    // BTH_LE_GATT_CHARACTERISTIC_VALUE is a variable-length C struct ([ULONG DataSize][UCHAR Data[1]...]) —
    // marshal it as a raw native buffer, never Marshal.PtrToStructure (no fixed-size CLR shape exists for it).
    private static byte[]? ReadValue(IntPtr hDevice, BTH_LE_GATT_CHARACTERISTIC characteristic)
    {
        BluetoothGATTGetCharacteristicValue(hDevice, ref characteristic, 0, IntPtr.Zero, out ushort sizeNeeded, 0);
        if (sizeNeeded == 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(sizeNeeded);
        try
        {
            int hr = BluetoothGATTGetCharacteristicValue(hDevice, ref characteristic, sizeNeeded, buf, out _, 0);
            if (hr != 0) return null;
            int dataSize = Marshal.ReadInt32(buf, 0);   // ULONG DataSize @ offset 0
            byte[] data = new byte[dataSize];
            Marshal.Copy(buf + 4, data, 0, dataSize);   // UCHAR Data[] @ offset 4
            return data;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool WriteValue(IntPtr hDevice, BTH_LE_GATT_CHARACTERISTIC characteristic, byte[] data, bool withoutResponse)
    {
        int total = 4 + data.Length;
        IntPtr buf = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.WriteInt32(buf, 0, data.Length);
            Marshal.Copy(data, 0, buf + 4, data.Length);
            uint flags = withoutResponse ? BLUETOOTH_GATT_FLAG_WRITE_WITHOUT_RESPONSE : BLUETOOTH_GATT_FLAG_NONE;
            int hr = BluetoothGATTSetCharacteristicValue(hDevice, ref characteristic, buf, IntPtr.Zero, flags);
            return hr == 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Send an MCP JSON-RPC frame to a connected peer, fragmented through McpRelay over the McpRx characteristic.
    // Not yet called from anywhere (no dispatcher exists to hand it a reply to send — see the filing CRQ);
    // private since BTH_LE_GATT_CHARACTERISTIC can't appear in a signature more visible than that.
    private static bool SendMcp(IntPtr hDevice, BTH_LE_GATT_CHARACTERISTIC mcpRx, byte[] message, int mtu = 180)
    {
        foreach (var chunk in McpRelay.Fragment(message, mtu))
            if (!WriteValue(hDevice, mcpRx, chunk, withoutResponse: true)) return false;
        return true;
    }

    // ---- notifications -----------------------------------------------------------------------------

    // A duplicate device handle dedicated to one subscription's lifetime — BluetoothGATTRegisterEvent keys
    // its callback to the handle used to register; keeping a distinct handle per watcher thread means one
    // peer's Stop()/Terminate() never yanks a handle another subscription still owns.
    private static IntPtr DuplicateForSubscription(IntPtr hDevice)
    {
        // Re-derive the symbolic link path is not available here (hDevice is opaque) — duplicate the OS
        // handle instead (same open file object, safe to close independently via reference-counted handle table).
        IntPtr proc = GetCurrentProcess();
        return DuplicateHandle(proc, hDevice, proc, out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS) ? dup : IntPtr.Zero;
    }

    private static void WatchNotify(IntPtr hDevice, BTH_LE_GATT_CHARACTERISTIC txChar)
    {
        // cb roots the BluetoothGattEventCallback delegate for the whole subscription — the OS holds a raw
        // function pointer into it and can invoke it at any time until BluetoothGATTUnregisterEvent, so the
        // delegate must never become GC-eligible while registered. GC.KeepAlive(cb) after the parked loop
        // (reached only via Thread.Interrupt, i.e. Terminate) is what pins it across the whole wait.
        var cb = new GcHandleCallback(new McpRelay.Reassembler(), txChar.CharacteristicValueHandle);
        try
        {
            // The registration struct (BLUETOOTH_GATT_VALUE_CHANGED_EVENT_REGISTRATION) is also variable-
            // length ([USHORT NumCharacteristics][BTH_LE_GATT_CHARACTERISTIC Characteristics[]]) — one entry here.
            int charSize = Marshal.SizeOf<BTH_LE_GATT_CHARACTERISTIC>();
            IntPtr reg = Marshal.AllocHGlobal(4 + charSize);   // USHORT padded to 4 for the array's natural alignment
            try
            {
                Marshal.WriteInt16(reg, 0, 1);   // NumCharacteristics = 1
                Marshal.StructureToPtr(txChar, reg + 4, false);

                int hr = BluetoothGATTRegisterEvent(hDevice, BTH_LE_GATT_EVENT_TYPE.CharacteristicValueChangedEvent,
                    reg, cb.Callback, IntPtr.Zero, out IntPtr eventHandle, 0);
                if (hr != 0) { Dg.Warn("dp-ble", $"RegisterEvent failed: 0x{hr:X8}"); return; }

                Dg.Log("dp-ble", "subscribed to peer McpTx notifications");
                // Park this VOM-tracked thread for the life of the subscription — the OS calls cb.Callback
                // on its own thread; Thread.Interrupt (Vom.Terminate) wakes this Sleep to unwind cleanly.
                while (true) Thread.Sleep(1000);
            }
            finally { Marshal.FreeHGlobal(reg); }
        }
        catch (ThreadInterruptedException) { Dg.Log("dp-ble", "subscription thread unwound (Terminate interrupt)"); }
        finally { CloseHandle(hDevice); GC.KeepAlive(cb); }
    }

    // Bridges the unmanaged callback back to a McpRelay.Reassembler instance without a static/global map.
    // Callback is a field (not a local delegate elsewhere) specifically so the instance that roots it for
    // the GC is the same instance WatchNotify keeps alive via GC.KeepAlive for the subscription's lifetime.
    private sealed class GcHandleCallback
    {
        private readonly McpRelay.Reassembler _rx;
        private readonly ushort _watchedHandle;
        public readonly BluetoothGattEventCallback Callback;
        public GcHandleCallback(McpRelay.Reassembler rx, ushort watchedHandle)
        { _rx = rx; _watchedHandle = watchedHandle; Callback = OnEvent; }

        public void OnEvent(BTH_LE_GATT_EVENT_TYPE eventType, IntPtr eventOutParameter, IntPtr context)
        {
            try
            {
                if (eventType != BTH_LE_GATT_EVENT_TYPE.CharacteristicValueChangedEvent) return;
                // BLUETOOTH_GATT_VALUE_CHANGED_EVENT { USHORT ChangedAttributeHandle; size_t DataSize; PBTH_LE_GATT_CHARACTERISTIC_VALUE Value; }
                ushort changedHandle = (ushort)Marshal.ReadInt16(eventOutParameter, 0);
                if (changedHandle != _watchedHandle) return;
                IntPtr valuePtr = Marshal.ReadIntPtr(eventOutParameter, IntPtr.Size == 8 ? 16 : 8);
                if (valuePtr == IntPtr.Zero) return;
                int dataSize = Marshal.ReadInt32(valuePtr, 0);
                byte[] chunk = new byte[dataSize];
                Marshal.Copy(valuePtr + 4, chunk, 0, dataSize);
                if (_rx.Feed(chunk, out byte[] frame))
                    Dg.Log("dp-ble", $"mcp frame from peer ({frame.Length}B) — dispatch not yet wired, see CRQ (ss mcp is Windows-head only today)");
            }
            catch (Exception ex) { Dg.Warn("dp-ble", $"notify callback: {ex.Message}"); }
        }
    }

    // ---- device enumeration (SetupDi — same recipe DpWinUsb.cs uses for WinUSB) -----------------------

    private static System.Collections.Generic.IEnumerable<string> EnumerateBleDevicePaths()
    {
        Guid guid = GuidBluetoothLeDeviceInterface;
        IntPtr di = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (di == new IntPtr(-1)) yield break;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(di, IntPtr.Zero, ref guid, i, ref ifData); i++)
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
                    if (path.Length > 0) yield return path;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(di); }
    }

    private static IntPtr OpenDevice(string path) =>
        CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    private static Guid ToGuid(BTH_LE_UUID u) => u.IsShortUuid != 0
        ? new Guid(u.ShortUuid, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB)   // Bluetooth SIG base UUID
        : u.LongUuid;

    private static BTH_LE_UUID FromGuid(Guid g) => new() { IsShortUuid = 0, LongUuid = g };

    private sealed record PeerRecord(string Name, byte Capabilities, string Transport, string McpEndpoint);

    // ---- native structs/signatures ------------------------------------------------------------------

    private const uint DIGCF_PRESENT         = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ          = 0x80000000;
    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint FILE_SHARE_READ       = 0x01;
    private const uint FILE_SHARE_WRITE      = 0x02;
    private const uint OPEN_EXISTING         = 3;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    private const uint BLUETOOTH_GATT_FLAG_NONE                  = 0x00000000;
    private const uint BLUETOOTH_GATT_FLAG_WRITE_WITHOUT_RESPONSE = 0x00000008;

    private enum BTH_LE_GATT_EVENT_TYPE { CharacteristicValueChangedEvent }

    // BTH_LE_UUID { BOOLEAN IsShortUuid; union { USHORT ShortUuid; GUID LongUuid; } Value; } — the union
    // sits at offset 4 (BOOLEAN + 3 bytes padding to the GUID's 4-byte alignment); total size 20 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct BTH_LE_UUID
    {
        [FieldOffset(0)] public byte IsShortUuid;
        [FieldOffset(4)] public ushort ShortUuid;
        [FieldOffset(4)] public Guid LongUuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BTH_LE_GATT_SERVICE
    {
        public BTH_LE_UUID ServiceUuid;
        public ushort AttributeHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BTH_LE_GATT_CHARACTERISTIC
    {
        public ushort ServiceHandle;
        public BTH_LE_UUID CharacteristicUuid;
        public ushort AttributeHandle;
        public ushort CharacteristicValueHandle;
        public byte IsBroadcastable, IsReadable, IsWritable, IsWritableWithoutResponse,
                    IsSignedWritable, IsNotifiable, IsIndicatable, HasExtendedProperties;
    }

    private delegate void BluetoothGattEventCallback(BTH_LE_GATT_EVENT_TYPE eventType, IntPtr eventOutParameter, IntPtr context);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTGetServices(IntPtr hDevice, ushort servicesBufferCount,
        [In, Out] BTH_LE_GATT_SERVICE[]? servicesBuffer, out ushort servicesBufferActual, uint flags);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTGetCharacteristics(IntPtr hDevice, ref BTH_LE_GATT_SERVICE service,
        ushort characteristicsBufferCount, [In, Out] BTH_LE_GATT_CHARACTERISTIC[]? characteristicsBuffer,
        out ushort characteristicsBufferActual, uint flags);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTGetCharacteristicValue(IntPtr hDevice, ref BTH_LE_GATT_CHARACTERISTIC characteristic,
        uint characteristicValueDataSize, IntPtr characteristicValue, out ushort characteristicValueSizeRequired, uint flags);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTSetCharacteristicValue(IntPtr hDevice, ref BTH_LE_GATT_CHARACTERISTIC characteristic,
        IntPtr characteristicValue, IntPtr reliableWriteContext, uint flags);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGATTRegisterEvent(IntPtr hService, BTH_LE_GATT_EVENT_TYPE eventType,
        IntPtr eventParameterIn, BluetoothGattEventCallback callback, IntPtr callbackContext, out IntPtr eventHandle, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    { public uint cbSize; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr di, IntPtr diData, ref Guid guid, uint idx, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr di, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, ref uint required, IntPtr ddData);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr di, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, ref uint required, ref SP_DEVINFO_DATA ddData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcess, IntPtr hSource, IntPtr hTargetProcess,
        out IntPtr lpTargetHandle, uint access, bool inherit, uint options);
}
