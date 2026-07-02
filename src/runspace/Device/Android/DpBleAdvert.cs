using System;
using System.Text.Json;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.OS;
using Subsystem.Cm;
using Subsystem.Vom;
using CmClass = Subsystem.Cm.Cm;   // the registry CLASS; bare `Cm` binds to the sibling NAMESPACE here (the Dp.cs VomClass precedent)

namespace Subsystem.Device;

// DpBleAdvert — opt-in BLE discoverability + a GATT-relayed MCP endpoint, the LOWEST rung of the
// invariant-9 transport ladder (USB-NCM/AOA above it per CRQ185; UDP+mDNS above THAT per CRQ169). Only
// small commands/notification relay/sensor reads ride this rung (~400kbps at a 512B MTU) — screen
// surface stays on AOA/WinUsb. Same recipe as CRQ188's registry announce/scan/connect, ported to a radio
// with no shared filesystem: advertise -> a scanner reads capabilities off the GATT characteristics ->
// connects -> McpRelay-fragmented JSON-RPC rides the Mcp Rx/Tx characteristics.
//
// ANDROID-ONLY. Scott, 2026-07-02 (raw prompt, re: the Windows-side scanner that used to live at
// windows/DpBleScan.cs): "Android-only advertise ... Windows never actively scans for BLE — it reaches
// phones via USB/AOA or LAN instead". Windows has no public device-object/IOCTL surface for passive BLE
// advertisement scanning (only the gated WinRT BluetoothLEAdvertisementWatcher, which we don't pursue —
// see [[no-winrt-native-c-abi]]), and it doesn't need one: it already reaches phones over faster rungs.
// The scanner side of this recipe is Android scanning Android (phone-to-phone, not yet built).
//
// Gated by \Capability\Mcp\RemoteAccept — default-deny, mirrors windows/CapabilityGate.cs's shape without
// its Console UX (Android has none; denials log via Dg). Off until the user flips the toggle in the ss
// UI; nothing here starts advertising on its own.
//
// UNVERIFIED: this file has not been compiled against the project's pinned Android API level (no net11.0
// preview SDK / Android workload was available in the environment this was authored in — see the filing
// CRQ). The Android.Bluetooth.LE binding surface (AdvertiseMode/AdvertiseTx enum member names especially)
// should be checked against a real build before this ships.
internal static class DpBleAdvert
{
    [Flags]
    public enum Cap : byte { Mcp = 1, Surface = 2, Inference = 4, Executive = 8 }

    private const string CapabilityPath = "\\Capability\\Mcp\\RemoteAccept";

    // ss-derived UUIDs — fixed per install so every ss phone advertises the SAME service; a scanner tells
    // devices apart by the DeviceName characteristic, not the service UUID.
    public static readonly Java.Util.UUID ServiceUuid       = Java.Util.UUID.FromString("5b3f1a10-6b1e-4e2a-9c2e-737562737973");
    public static readonly Java.Util.UUID DeviceNameUuid    = Java.Util.UUID.FromString("5b3f1a11-6b1e-4e2a-9c2e-737562737973");
    public static readonly Java.Util.UUID CapabilitiesUuid  = Java.Util.UUID.FromString("5b3f1a12-6b1e-4e2a-9c2e-737562737973");
    public static readonly Java.Util.UUID TransportUuid     = Java.Util.UUID.FromString("5b3f1a13-6b1e-4e2a-9c2e-737562737973");
    public static readonly Java.Util.UUID McpEndpointUuid   = Java.Util.UUID.FromString("5b3f1a14-6b1e-4e2a-9c2e-737562737973");
    public static readonly Java.Util.UUID McpRxUuid         = Java.Util.UUID.FromString("5b3f1a15-6b1e-4e2a-9c2e-737562737973"); // peer WRITES MCP fragments here
    public static readonly Java.Util.UUID McpTxUuid         = Java.Util.UUID.FromString("5b3f1a16-6b1e-4e2a-9c2e-737562737973"); // peer SUBSCRIBES for MCP fragments here
    private static readonly Java.Util.UUID CccdUuid         = Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb");

    private static BluetoothLeAdvertiser? _advertiser;
    private static BluetoothGattServer? _gattServer;
    private static DpAdvertiseCallback? _advertiseCallback;
    private static DpGattServerCallback? _gattCallback;
    private static Owner? _owner;

    // The opt-in door. grant=true seeds+enables the capability (an audited grant, the same shape as a
    // Set-Capability call); otherwise this DENIES fail-closed unless already granted. Call with grant:true
    // exactly once, from the ss UI toggle handler — everything else (Start on boot if still enabled)
    // calls with grant:false and relies on the persisted Enabled bit.
    public static bool Allow(bool grant)
    {
        if (CmClass.Get(CapabilityPath) is null)
        {
            CmClass.Register(new CapabilityRecord
            {
                Path = CapabilityPath, Name = "RemoteAccept", Type = "Mount", Owner = "\\System",
                Integrity = "User", StartType = "manual", Enabled = false,
                ManifestJson = JsonSerializer.Serialize(new
                {
                    version = 1, kind = "mount", id = "RemoteAccept",
                    note = "Opt-in BLE MCP relay + auto-discovery advertising. Off by default (default-deny); grant to allow.",
                }),
            });
        }

        if (grant) CmClass.Set(CapabilityPath, enabled: true, startType: null);

        var res = AccessCheck.Resolve(Caller.Local(), CapabilityPath);
        if (!res.Granted) { Dg.Log("dp-ble", $"DENIED — {res.Reason}"); return false; }
        return true;
    }

    // Start advertising + the GATT server. Idempotent; returns false (no-op) if the capability was never
    // granted through Allow(grant:true) — this never grants on its own.
    public static bool Start(Android.Content.Context ctx, string serial, Cap caps)
    {
        if (!Allow(grant: false)) return false;
        if (_advertiser != null) return true;

        var btMgr = (BluetoothManager?)ctx.GetSystemService(Android.Content.Context.BluetoothService);
        var adapter = btMgr?.Adapter;
        if (adapter == null || !adapter.IsEnabled) { Dg.Log("dp-ble", "Bluetooth adapter unavailable/disabled"); return false; }

        _advertiser = adapter.BluetoothLeAdvertiser;
        if (_advertiser == null) { Dg.Log("dp-ble", "device does not support BLE peripheral/advertising"); return false; }

        var service = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);
        service.AddCharacteristic(ReadOnlyCharacteristic(DeviceNameUuid, System.Text.Encoding.UTF8.GetBytes($"{Build.Model} {serial}")));
        service.AddCharacteristic(ReadOnlyCharacteristic(CapabilitiesUuid, new[] { (byte)caps }));
        service.AddCharacteristic(ReadOnlyCharacteristic(TransportUuid, System.Text.Encoding.UTF8.GetBytes(BestTransport())));
        service.AddCharacteristic(ReadOnlyCharacteristic(McpEndpointUuid, System.Text.Encoding.UTF8.GetBytes("ble-gatt")));
        service.AddCharacteristic(WriteOnlyCharacteristic(McpRxUuid));
        service.AddCharacteristic(NotifyCharacteristic(McpTxUuid));

        _owner = Vom.Vom.CreateOwner($"\\Device\\Android\\{serial}\\Mcp");
        _gattCallback = new DpGattServerCallback();
        _gattServer = btMgr!.OpenGattServer(ctx, _gattCallback);
        if (_gattServer == null) { Dg.Log("dp-ble", "OpenGattServer failed"); Stop(); return false; }
        _gattServer.AddService(service);

        var settings = new AdvertiseSettings.Builder()
            .SetAdvertiseMode(AdvertiseMode.LowPower)!       // ~0.1mW, always-on per the discoverability spec
            .SetTxPowerLevel(AdvertiseTx.PowerLow)!
            .SetConnectable(true)!
            .Build();
        var data = new AdvertiseData.Builder()
            .AddServiceUuid(new ParcelUuid(ServiceUuid))!
            .SetIncludeDeviceName(false)!                    // DeviceName characteristic carries it — the 31B adv payload can't
            .Build();

        _advertiseCallback = new DpAdvertiseCallback();
        _advertiser.StartAdvertising(settings, data, _advertiseCallback);
        Dg.Log("dp-ble", $"advertising {ServiceUuid} caps=0x{(byte)caps:X2} transport={BestTransport()}");
        return true;
    }

    public static void Stop()
    {
        try { _advertiser?.StopAdvertising(_advertiseCallback); } catch (Exception ex) { Dg.Warn("dp-ble", ex); }
        try { _gattServer?.Close(); } catch (Exception ex) { Dg.Warn("dp-ble", ex); }
        if (_owner != null) { Vom.Vom.Terminate(_owner); _owner = null; }
        _advertiser = null; _gattServer = null; _advertiseCallback = null; _gattCallback = null;
    }

    // Transport ladder (invariant 9): report the floor rung that's ALWAYS true here. A live DpAoa/DpWinUsb
    // session negotiating a higher rung is out of this file's scope — a future pass wires DpAoa.IsConnected
    // so a scanning peer can skip straight past BLE instead of negotiating up from it.
    private static string BestTransport() => "ble";

    private static BluetoothGattCharacteristic ReadOnlyCharacteristic(Java.Util.UUID uuid, byte[] value)
    {
        var c = new BluetoothGattCharacteristic(uuid, GattProperty.Read, GattPermission.Read);
        c.SetValue(value);
        return c;
    }

    private static BluetoothGattCharacteristic WriteOnlyCharacteristic(Java.Util.UUID uuid)
        => new BluetoothGattCharacteristic(uuid, GattProperty.Write | GattProperty.WriteNoResponse, GattPermission.Write);

    private static BluetoothGattCharacteristic NotifyCharacteristic(Java.Util.UUID uuid)
    {
        var c = new BluetoothGattCharacteristic(uuid, GattProperty.Notify, GattPermission.Read);
        c.AddDescriptor(new BluetoothGattDescriptor(CccdUuid, GattDescriptorPermission.Read | GattDescriptorPermission.Write));
        return c;
    }

    // GATT server callback — writes to McpRx are handed to McpRelay for reassembly. Dispatching a
    // completed JSON-RPC frame into the actual `ss mcp` tool surface is NOT wired here: that surface
    // (windows/Mcp.cs) is Windows-head only today — porting/sharing its dispatcher into the Android
    // runspace is the next slice (filed alongside this).
    private sealed class DpGattServerCallback : BluetoothGattServerCallback
    {
        private readonly McpRelay.Reassembler _rx = new();

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            if (characteristic?.Uuid?.Equals(McpRxUuid) == true && value != null && _rx.Feed(value, out byte[] frame))
                Dg.Log("dp-ble", $"mcp frame reassembled ({frame.Length}B) — dispatch not yet wired, see CRQ");

            if (responseNeeded)
                _gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value);
        }

        // Binding quirk: the SERVER callback maps both Java ints to ProfileState (client-side
        // BluetoothGattCallback is the one that takes GattStatus).
        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
            => Dg.Log("dp-ble", $"peer {device?.Address} -> {newState}");
    }

    private sealed class DpAdvertiseCallback : AdvertiseCallback
    {
        public override void OnStartFailure(AdvertiseFailure errorCode) => Dg.Log("dp-ble", $"advertise failed: {errorCode}");
    }
}
