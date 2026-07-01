using System;

namespace Subsystem.Vom;

// strongly typed records for device drivers and diagnostics (Move M6)
public record BatteryStatusRecord(int Level, bool IsCharging, double Temperature, int Voltage, string Technology);
public record DeviceInfoRecord(string Model, string Manufacturer, string Device, string Board, string Hardware, string OSVersion, int SdkInt, bool IsEmulator);
public record StorageInfoRecord(long TotalBytes, long FreeBytes, long UsedBytes, double TotalGB, double FreeGB);
public record MemoryInfoRecord(long TotalBytes, long AvailableBytes, long UsedBytes, bool LowMemory, long ThresholdBytes, double TotalGB);
public record SensorRecord(string Name, string Type, string Vendor, int Version, float Power, float MaximumRange);
public record NetworkInfoRecord(bool IsConnected, bool HasWiFi, bool HasCellular, bool HasVpn, int DownstreamKbps, int UpstreamKbps);
public record DisplayInfoRecord(int WidthPixels, int HeightPixels, int DensityDpi, float Density, float XDpi, float YDpi, double RefreshRate);
public record NotificationMessageRecord(string Package, long PostTime, bool IsClearable, string Title, string Text);
public record InstalledAppRecord(string Package, string Label, bool Enabled, bool IsSystem);
public record WallpaperRecord(string Id, string Name, bool Active);
