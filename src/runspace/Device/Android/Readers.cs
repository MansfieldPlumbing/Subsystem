using System;
using Subsystem.Vom;

namespace Subsystem.Device;



// \Device\Android\* introspection drivers — decomposed VERBATIM from the VirtualObjectManager god-object
// (VOM-SPEC §1: device capabilities are drivers, not kernel/host methods). Bodies are unchanged; only the
// containing type moved. Each class maps to one \Device\Android\<X> mount.

// \Device\Android\Power
public static class Power
{
    public static BatteryStatusRecord GetBatteryStatus()
    {
        int level = 0;
        bool isCharging = false;
        double temp = 0.0;
        int volt = 0;
        string tech = "Unknown";
        try {
            var ctx = Android.App.Application.Context;
            using var bm = (Android.OS.BatteryManager?)ctx.GetSystemService(Android.Content.Context.BatteryService);
            if (bm != null) {
                level = bm.GetIntProperty((int)Android.OS.BatteryProperty.Capacity);

                using var filter = new Android.Content.IntentFilter(Android.Content.Intent.ActionBatteryChanged);
                using var batteryStatus = ctx.RegisterReceiver(null, filter);
                if (batteryStatus != null) {
                    int status = batteryStatus.GetIntExtra(Android.OS.BatteryManager.ExtraStatus, -1);
                    isCharging = status == (int)Android.OS.BatteryStatus.Charging || status == (int)Android.OS.BatteryStatus.Full;
                    temp = batteryStatus.GetIntExtra(Android.OS.BatteryManager.ExtraTemperature, 0) / 10.0;
                    volt = batteryStatus.GetIntExtra(Android.OS.BatteryManager.ExtraVoltage, 0);
                    tech = batteryStatus.GetStringExtra(Android.OS.BatteryManager.ExtraTechnology) ?? "Unknown";
                }
            }
        } catch (Exception ex) { Dg.Warn("power", ex); }
        return new BatteryStatusRecord(level, isCharging, temp, volt, tech);
    }
}

// \Device\Android\Info
public static class Info
{
    public static DeviceInfoRecord GetDeviceInfo()
    {
        return new DeviceInfoRecord(
            Android.OS.Build.Model ?? "Unknown",
            Android.OS.Build.Manufacturer ?? "Unknown",
            Android.OS.Build.Device ?? "Unknown",
            Android.OS.Build.Board ?? "Unknown",
            Android.OS.Build.Hardware ?? "Unknown",
            Android.OS.Build.VERSION.Release ?? "Unknown",
            (int)Android.OS.Build.VERSION.SdkInt,
            Android.OS.Build.Fingerprint?.Contains("generic") ?? false
        );
    }
}

// \Device\Android\Storage
public static class Storage
{
    public static StorageInfoRecord GetStorageInfo()
    {
        long total = 0;
        long free = 0;
        long used = 0;
        double totalGB = 0.0;
        double freeGB = 0.0;
        try {
            using var dataDir = Android.OS.Environment.DataDirectory;
            var path = dataDir?.Path ?? "/data";
            using var stat = new Android.OS.StatFs(path);
            long blockSize = stat.BlockSizeLong;
            total = stat.BlockCountLong * blockSize;
            free = stat.AvailableBlocksLong * blockSize;
            used = total - free;
            totalGB = System.Math.Round(total / 1073741824.0, 2);
            freeGB = System.Math.Round(free / 1073741824.0, 2);
        } catch (Exception ex) { Dg.Warn("storage", ex); }
        return new StorageInfoRecord(total, free, used, totalGB, freeGB);
    }
}

// \Device\Android\Memory
public static class Memory
{
    public static MemoryInfoRecord GetMemoryInfo()
    {
        long total = 0;
        long available = 0;
        long used = 0;
        bool lowMemory = false;
        long threshold = 0;
        double totalGB = 0.0;
        try {
            var ctx = Android.App.Application.Context;
            using var am = (Android.App.ActivityManager?)ctx.GetSystemService(Android.Content.Context.ActivityService);
            if (am != null) {
                using var mi = new Android.App.ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                total = mi.TotalMem;
                available = mi.AvailMem;
                used = mi.TotalMem - mi.AvailMem;
                lowMemory = mi.LowMemory;
                threshold = mi.Threshold;
                totalGB = System.Math.Round(mi.TotalMem / 1073741824.0, 2);
            }
        } catch (Exception ex) { Dg.Warn("memory", ex); }
        return new MemoryInfoRecord(total, available, used, lowMemory, threshold, totalGB);
    }
}

// \Device\Android\Sensors
public static class Sensors
{
    public static System.Collections.Generic.List<SensorRecord> GetSensors()
    {
        var list = new System.Collections.Generic.List<SensorRecord>();
        try {
            var ctx = Android.App.Application.Context;
            using var sm = (Android.Hardware.SensorManager?)ctx.GetSystemService(Android.Content.Context.SensorService);
            var sensors = sm?.GetSensorList(Android.Hardware.SensorType.All);
            if (sensors != null) {
                foreach (var s in sensors) {
                    list.Add(new SensorRecord(
                        s.Name ?? "",
                        s.Type.ToString(),
                        s.Vendor ?? "",
                        s.Version,
                        s.Power,
                        s.MaximumRange
                    ));
                    s.Dispose(); // Dispose the individual sensor wrappers
                }
            }
        } catch (Exception ex) { Dg.Warn("sensors", ex); }
        return list;
    }
}

// \Device\Android\Network
public static class Network
{
    public static NetworkInfoRecord GetNetworkInfo() {
        bool isConnected = false;
        bool hasWiFi = false;
        bool hasCellular = false;
        bool hasVpn = false;
        int downKbps = 0;
        int upKbps = 0;
        try {
            var ctx = Android.App.Application.Context;
            var cm = (Android.Net.ConnectivityManager?)ctx.GetSystemService(Android.Content.Context.ConnectivityService);
            if (cm != null) {
                var nc = cm.GetNetworkCapabilities(cm.ActiveNetwork);
                isConnected = nc != null;
                if (nc != null) {
                    hasWiFi = nc.HasTransport(Android.Net.TransportType.Wifi);
                    hasCellular = nc.HasTransport(Android.Net.TransportType.Cellular);
                    hasVpn = nc.HasTransport(Android.Net.TransportType.Vpn);
                    downKbps = nc.LinkDownstreamBandwidthKbps;
                    upKbps = nc.LinkUpstreamBandwidthKbps;
                }
            }
        } catch (Exception ex) { Dg.Warn("network", ex); }
        return new NetworkInfoRecord(isConnected, hasWiFi, hasCellular, hasVpn, downKbps, upKbps);
    }
}
