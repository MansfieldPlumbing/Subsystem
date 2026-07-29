using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Subsystem.Windows;

// SystemTray — the system-tray presence and control surface for the Windows head.
// Houses the Tests submenu (WebView DirectPort Adapter, surface test pattern, live VOM namespace TUI)
// and opens the OBP shell as the primary action.
public static class SystemTray
{
    private static string AppName =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Name ?? "app";

    private static BrokerHost? _broker;
    private static System.Windows.Forms.Timer? _brokerTimer;

    public static int Run(string[] args)
    {
        var exited = new ManualResetEventSlim(false);
        var trayThread = new Thread(() => { try { RunTray(); } finally { exited.Set(); } });
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();
        exited.Wait();
        return 0;
    }

    private static void RunTray()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var tray = new NotifyIcon
        {
            Text = AppName,
            Icon = TrayIcon(),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => LaunchUi();   // double-click the icon = open the shell (windowless: no console)

        Application.Run(new ApplicationContext());
        StopBroker();   // tray closing — reclaim the in-proc broker
    }

    private static void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(AppName) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open Shell", null, (_, _) => LaunchUi());
        menu.Items.Add(new ToolStripSeparator());

        // Virtual camera — start/stop
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
        menu.Items.Add(sources);

        // Tests submenu — OBPs are self-discovered from shell/tests/
        var tests = new ToolStripMenuItem("Tests");
        tests.DropDownItems.Add("DirectPort Interop", null, (_, _) => LaunchUi("http://shell/tests/DirectPortInterop.obp"));
        tests.DropDownItems.Add("Test pattern (ss surface)", null, (_, _) => Launch("surface --grant"));
        tests.DropDownItems.Add("Open TUI (live namespace)", null, (_, _) => Launch("tui"));
        menu.Items.Add(tests);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { Application.ExitThread(); });
    }

    private static void LaunchUi() => Launch("shell");

    private static void LaunchUi(string url) => Launch($"shell --url {url}");

    private static void Launch(string verbAndArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe, verbAndArgs) { UseShellExecute = true }); }
        catch (Exception ex) { Warn($"ss {verbAndArgs}", ex); }
    }

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

    private static void ToggleBroker()
    {
        if (_broker != null) { StopBroker(); return; }
        var host = BrokerHost.Start("VirtuaCam", grid: true, fps: 30, grant: true, out int code, out string? reason);
        if (host == null) { Warn("virtual camera", new InvalidOperationException(reason ?? $"refused (code {code})")); return; }
        _broker = host;
        _brokerTimer = new System.Windows.Forms.Timer { Interval = host.PeriodMs };
        _brokerTimer.Tick += (_, _) => _broker?.RenderFrame();
        _brokerTimer.Start();
    }

    private static void StopBroker()
    {
        _brokerTimer?.Stop();
        _brokerTimer?.Dispose();
        _brokerTimer = null;
        _broker?.Stop();
        _broker = null;
    }

    private static void Warn(string what, Exception ex) =>
        MessageBox.Show($"Could not launch {what}: {ex.Message}", AppName,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

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
            return SystemIcons.Application;
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
