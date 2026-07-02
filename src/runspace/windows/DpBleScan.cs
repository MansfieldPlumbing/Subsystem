using System;

namespace Subsystem.Windows;

// DpBleScan — the Windows counterpart to Device/Android/DpBleAdvert.cs: CRQ188's announce/scan/connect
// recipe carried onto a radio with no shared filesystem (invariant-9's lowest transport rung; USB-NCM/AOA
// per CRQ185 and UDP+mDNS per CRQ169 both outrank it when available).
//
// NOT YET IMPLEMENTED. A first pass reached for Windows.Devices.Bluetooth.Advertisement (WinRT) and
// bumped SubsystemWin.csproj's TargetFramework to unlock the CsWinRT projection — Scott, 2026-07-02
// (raw prompt): "virtuacam should be cpp only. if u see winrt, it's ai slop and dont pursue it and we
// dont need any of that anyhow to cross boundaries with cs". Reverted. See [[no-winrt-native-c-abi]].
//
// Windows ships no classic Win32 API for passive BLE advertisement scanning — BluetoothLEAdvertisementWatcher
// is the only OS-provided mechanism, and it is a WinRT runtime class. The correct seam (same shape as
// directport.dll / DpWinUsb.cs's WinUSB P/Invoke) is a small native C++ DLL that raw-COM-activates that
// runtime class directly (RoGetActivationFactory against the published IIDs — no C++/WinRT projection
// headers, no .NET WinRT projection, no TargetFramework change) and exposes a plain C ABI: dpble_start_scan,
// dpble_stop_scan, a discovered-peer callback. ss.exe P/Invokes that DLL exactly like directport.dll.
// Build toolchain is on-drive: S:\bin\msvc (cl.exe/link.exe) + the pinned dotnet SDK at S:\bin\dotnet.
//
// This file stays as the wiring point (`ss ble`) so Program.cs and the CRQ192 discovery story don't need
// re-plumbing once the native dpble.dll lands — it just fails loud instead of pretending to scan.
internal static class DpBleScan
{
    public static int Run(string[] args)
    {
        Console.Error.WriteLine("ss ble: not yet implemented — needs the native dpble.dll C-ABI shim (see DpBleScan.cs header). No WinRT/CsWinRT path will be taken.");
        return 1;
    }

    public static bool Start() => false;

    public static void Stop() { }
}
