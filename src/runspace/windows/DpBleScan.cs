using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Subsystem.Device;
using Subsystem.Vom;

namespace Subsystem.Windows;

// DpBleScan — the Windows counterpart to Device/Android/DpBleAdvert.cs: CRQ188's announce/scan/connect
// recipe carried onto a radio with no shared filesystem (invariant-9's lowest transport rung; USB-NCM/AOA
// per CRQ185 and UDP+mDNS per CRQ169 both outrank it when available). Passively scans for the ss GATT
// service UUID, reads the advertised DeviceName/Capabilities/Transport/McpEndpoint characteristics, and
// mounts the peer under \Device\Peers\<serial>. If the peer offers nothing better than "ble-gatt", opens
// the Mcp Tx notify characteristic and reassembles JSON-RPC fragments through McpRelay.
//
// No async/await left running loose: every WinRT IAsyncOperation is blocked on SYNCHRONOUSLY inside a
// Vom.Spawn'd thread, one per discovered peer — mirrors DpWinUsb.cs's synchronous P/Invoke style so a
// slow/unresponsive peer stalls only its own thread, never the watcher callback or other peers (SS009/SS018).
//
// UNVERIFIED: authored with no net11.0 preview SDK available to build-check (see the filing CRQ). This
// file also required bumping SubsystemWin.csproj's TargetFramework to a versioned Windows SDK
// (net11.0-windows10.0.19041.0) to unlock the Windows.Devices.Bluetooth WinRT projection — build this
// before relying on it; no public Win32 BLE-scan API exists as a P/Invoke fallback.
internal static class DpBleScan
{
    // Same UUIDs Device/Android/DpBleAdvert.cs advertises — the discovery seam's shared constants.
    public static readonly Guid ServiceGuid       = new("5b3f1a10-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid DeviceNameGuid   = new("5b3f1a11-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid CapabilitiesGuid = new("5b3f1a12-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid TransportGuid    = new("5b3f1a13-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid McpEndpointGuid  = new("5b3f1a14-6b1e-4e2a-9c2e-737562737973");
    private static readonly Guid McpTxGuid        = new("5b3f1a16-6b1e-4e2a-9c2e-737562737973");

    private static BluetoothLEAdvertisementWatcher? _watcher;
    private static Owner? _root;

    // De-dupe: a nearby peer re-advertises every ~100-200ms; mount it once per BluetoothAddress until
    // it drops out of range (Stopped/removed handling is a follow-on — see the filing CRQ).
    private static readonly ConcurrentDictionary<ulong, byte> _mounted = new();

    // `ss ble` — run the scanner standalone (diagnostic / manual use); the gateway starts it as a
    // background service once the opt-in door is wired on the Windows side too.
    public static int Run(string[] args)
    {
        if (!Start()) return 1;
        Console.WriteLine($"ss ble — scanning for {ServiceGuid} (Ctrl+C to stop)...");
        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();
        Stop();
        return 0;
    }

    public static bool Start()
    {
        if (_watcher != null) return true;
        _root = Vom.Vom.CreateOwner("\\Device\\Peers");

        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceGuid);
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Start();
        Dg.Log("dp-ble", $"scanning for {ServiceGuid}");
        return true;
    }

    public static void Stop()
    {
        if (_watcher != null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            try { _watcher.Stop(); } catch (Exception ex) { Dg.Warn("dp-ble", ex); }
            _watcher = null;
        }
        if (_root != null) { Vom.Vom.Terminate(_root); _root = null; }
        _mounted.Clear();
    }

    private static void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (!_mounted.TryAdd(args.BluetoothAddress, 0)) return;   // already mounted / mount in progress
        var root = _root;
        if (root == null) return;
        Vom.Vom.Spawn(root, $"Peer_{args.BluetoothAddress:X12}", _ => MountPeer(args.BluetoothAddress));
    }

    private static void MountPeer(ulong address)
    {
        try
        {
            var device = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().GetAwaiter().GetResult();
            if (device == null) { Dg.Warn("dp-ble", $"FromBluetoothAddressAsync(0x{address:X12}) returned null"); return; }

            var servicesResult = device.GetGattServicesForUuidAsync(ServiceGuid).AsTask().GetAwaiter().GetResult();
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            { Dg.Warn("dp-ble", $"no ss service on 0x{address:X12}: {servicesResult.Status}"); return; }

            var service = servicesResult.Services[0];
            string name        = ReadUtf8(service, DeviceNameGuid)  ?? $"peer-{address:X6}";
            byte[]? capsBytes  = ReadBytes(service, CapabilitiesGuid);
            byte   caps        = capsBytes is { Length: > 0 } ? capsBytes[0] : (byte)0;
            string transport   = ReadUtf8(service, TransportGuid)   ?? "ble";
            string mcpEndpoint = ReadUtf8(service, McpEndpointGuid) ?? "ble-gatt";

            // DeviceName is "<model> <serial>" (DpBleAdvert.Start) — the serial is the last token.
            int sp = name.LastIndexOf(' ');
            string serial = sp >= 0 ? name[(sp + 1)..] : name;

            var owner = Vom.Vom.CreateOwner($"\\Device\\Peers\\{serial}");
            Vom.Vom.Register(owner, "BlePeer", new PeerRecord(address, name, caps, transport, mcpEndpoint), subdir: "", name: "Info");
            Dg.Log("dp-ble", $"mounted \\Device\\Peers\\{serial} caps=0x{caps:X2} transport={transport} endpoint={mcpEndpoint}");

            // Lowest rung only: a peer that offered nothing better than BLE gets its Mcp Tx characteristic
            // wired through McpRelay so tool calls can still reach it. A higher rung (AOA/LAN) supersedes
            // this once negotiated — that handoff is a follow-on (see the filing CRQ).
            if (mcpEndpoint == "ble-gatt") OpenMcpRelay(service);
        }
        catch (Exception ex) { Dg.Warn("dp-ble", $"mount 0x{address:X12}: {ex.Message}"); }
    }

    private static void OpenMcpRelay(GattDeviceService service)
    {
        try
        {
            var chars = service.GetCharacteristicsForUuidAsync(McpTxGuid).AsTask().GetAwaiter().GetResult();
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return;
            var tx = chars.Characteristics[0];

            // One reassembler per connection — McpRelay.Reassembler is not thread-safe, but ValueChanged
            // fires serially per characteristic subscription so a single instance is safe here.
            var rx = new McpRelay.Reassembler();
            tx.ValueChanged += (_, args) =>
            {
                byte[] bytes = args.CharacteristicValue.ToArray();
                if (rx.Feed(bytes, out byte[] frame))
                    Dg.Log("dp-ble", $"mcp frame from peer ({frame.Length}B) — dispatch not yet wired, see CRQ (ss mcp is Windows-head only today)");
            };
            tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
              .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) { Dg.Warn("dp-ble", $"mcp relay open: {ex.Message}"); }
    }

    private static string? ReadUtf8(GattDeviceService service, Guid charGuid)
    {
        byte[]? bytes = ReadBytes(service, charGuid);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? ReadBytes(GattDeviceService service, Guid charGuid)
    {
        var chars = service.GetCharacteristicsForUuidAsync(charGuid).AsTask().GetAwaiter().GetResult();
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;
        var result = chars.Characteristics[0].ReadValueAsync().AsTask().GetAwaiter().GetResult();
        return result.Status == GattCommunicationStatus.Success ? result.Value.ToArray() : null;
    }

    private sealed record PeerRecord(ulong Address, string Name, byte Capabilities, string Transport, string McpEndpoint);
}
