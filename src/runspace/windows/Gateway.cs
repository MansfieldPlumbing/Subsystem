using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Subsystem.Vom;

namespace Subsystem.Windows;

// ss gateway — the Subsystem Windows GATEWAY (#53): the system-tray presence and control surface for the
// in-proc virtual-camera server, plus the launcher for the shells. The managed reflection of VirtuaCam's
// UI.cpp tray (studied, not imported; VirtuaCam's own tray is left intact) — Subsystem's OWN virtual camera,
// the DirectPortBroker mounted in-proc, never a separate app to "start".
//
// Model (matching VirtuaCam's App.cpp single-process design, minus its jank): the broker auto-discovers
// DirectPort PRODUCERS and composites them. Sources are separate producer processes the gateway launches
// (VirtuaCamProcess.exe for window/display/camera; `ss surface` for a test pattern) — the broker's
// auto-discovery grid then tiles whatever is live. So the menu LAUNCHES real, individually-tested
// components; nothing here is a stub.
//
// Threading: the tray runs on a VOM-OWNED STA thread (Vom.Spawn sta:true — owned, cancellable,
// cascade-terminated, SS009-clean), NOT the MTA main thread; the loop is Application.Run (a blocking
// GetMessage pump — push, not VirtuaCam's PeekMessage busy-poll). The menu is rebuilt on each open so the
// window list is live.
//
// Next layers (honest, not stubbed here): in-proc broker hosting for PRIORITY/PiP mode (needs the broker
// driven single-threaded off a WinForms timer + UpdateProducerPriorityList); MF camera enumeration for a
// camera picker; Audio + Settings; and the real .obp shell (WinShellUi is still the M0 DirectPort smoke
// test, not the shell).
internal static class Gateway
{
    private const string OwnerPath = "\\Gateway";

    // The tray hosts the broker IN-PROC (no separate process): the VOM-owned STA thread drives RenderFrame()
    // from a WinForms timer — VirtuaCam's single-thread model, made disciplined. Null = not running.
    private static BrokerHost? _broker;
    private static System.Windows.Forms.Timer? _brokerTimer;

    public static int Run(string[] args)
    {
        var owner = Vom.Vom.CreateOwner(OwnerPath);
        var exited = new ManualResetEventSlim(false);
        Vom.Vom.Spawn(owner, "Tray", _ => { try { RunTray(); } finally { exited.Set(); } }, sta: true);
        exited.Wait();
        Vom.Vom.Terminate(owner);   // cascade reclaim of the \Gateway owner + its tray thread handle
        return 0;
    }

    private static void RunTray()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var tray = new NotifyIcon
        {
            Text = "Subsystem — Windows gateway",
            Icon = TrayIcon(),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => LaunchUi();   // double-click the icon = open the shell (windowless: no console)

        Application.Run(new ApplicationContext());
        StopBroker();   // tray closing — reclaim the in-proc broker deterministically (Terminate -> ShutdownBroker)
    }

    // Rebuilt on every open so the window list (and any future live state) is fresh — VirtuaCam's pattern.
    private static void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Subsystem") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        // Subsystem's own virtual camera (the DirectPortBroker, mounted in-proc by `ss camera`): start the
        // server in auto-discovery grid mode, then add sources — the grid composites whatever is live.
        // In-proc: start/stop the broker on the tray's own STA thread (no separate process) — see ToggleBroker.
        menu.Items.Add(_broker == null ? "Virtual camera — start (in-proc grid)" : "Virtual camera — stop",
            null, (_, _) => ToggleBroker());

        var sources = new ToolStripMenuItem("Add a camera source");
        var windowsItem = new ToolStripMenuItem("Capture a window");
        foreach (var w in EnumerateWindows())
        {
            var hwnd = w.Hwnd;
            windowsItem.DropDownItems.Add(Trunc(w.Title, 48), null, (_, _) =>
                LaunchProducer($"--type capture --hwnd {(long)hwnd}"));
        }
        if (windowsItem.DropDownItems.Count == 0)
            windowsItem.DropDownItems.Add(new ToolStripMenuItem("(no capturable windows)") { Enabled = false });
        sources.DropDownItems.Add(windowsItem);
        sources.DropDownItems.Add("Virtual display", null, (_, _) => LaunchProducer("--type display"));
        sources.DropDownItems.Add("Test pattern (ss surface)", null, (_, _) => Launch("surface --grant"));
        menu.Items.Add(sources);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open shell (WebView)", null, (_, _) => LaunchUi());
        menu.Items.Add("Open TUI (live VOM namespace)", null, (_, _) => Launch("tui"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { Application.ExitThread(); });
    }

    // The WebView shell (ss ui) is a GUI verb — launch it WINDOWLESS (no console) so only the WinForms window
    // shows; WinShellUi also hides any console it owns as a backstop. Console verbs use Launch (with a console).
    private static void LaunchUi()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe, "ui") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Warn("ss ui", ex); }
    }

    // Launch an ss verb as its own process (the Windows desktop-leaf pattern; the core stays in-proc).
    private static void Launch(string verbAndArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe, verbAndArgs) { UseShellExecute = true }); }
        catch (Exception ex) { Warn($"ss {verbAndArgs}", ex); }
    }

    // Launch the bundled per-source producer host (VirtuaCamProcess.exe, beside ss.exe). It publishes a
    // DirectPort producer the broker's grid auto-discovers — exactly how VirtuaCam.exe spawns its sources.
    private static void LaunchProducer(string args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "VirtuaCamProcess.exe");
        if (!File.Exists(exe))
        {
            Warn("VirtuaCamProcess.exe", new FileNotFoundException("not bundled beside ss.exe"));
            return;
        }
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
        catch (Exception ex) { Warn($"VirtuaCamProcess {args}", ex); }
    }

    // Start/stop the in-proc broker on THIS (the tray's STA) thread: the WinForms timer is the pump,
    // RenderFrame the tick — the SAME BrokerHost `ss camera` runs, only the pump differs. Default-deny gated.
    private static void ToggleBroker()
    {
        if (_broker != null) { StopBroker(); return; }
        var host = BrokerHost.Start("VirtuaCam", grid: true, fps: 30, grant: true, out int code, out string? reason);
        if (host == null) { Warn("virtual camera", new InvalidOperationException(reason ?? $"refused (code {code})")); return; }
        _broker = host;
        _brokerTimer = new System.Windows.Forms.Timer { Interval = host.PeriodMs };
        _brokerTimer.Tick += (_, _) => _broker?.RenderFrame();   // discover -> composite -> signal, on the STA thread
        _brokerTimer.Start();
    }

    private static void StopBroker()
    {
        _brokerTimer?.Stop();
        _brokerTimer?.Dispose();
        _brokerTimer = null;
        _broker?.Stop();   // Terminate -> Reclaim -> ShutdownBroker
        _broker = null;
    }

    private static void Warn(string what, Exception ex) =>
        MessageBox.Show($"Could not launch {what}: {ex.Message}", "Subsystem gateway",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ---- window enumeration (the window-capture source list) — EnumWindows, the managed of UI.cpp's EnumWindowsProc ----

    private readonly record struct Win(IntPtr Hwnd, string Title);

    private static List<Win> EnumerateWindows()
    {
        var list = new List<Win>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            list.Add(new Win(hwnd, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static Icon TrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch (Exception)
        {
            return SystemIcons.Application;   // icon extraction failed — degrade to the stock app icon
        }
        return SystemIcons.Application;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
