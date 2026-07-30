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

    private static AcrylicTrayMenuWindow? _activeTrayWindow = null;

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

        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left)
            {
                ShowAcrylicTrayMenu();
            }
        };

        tray.DoubleClick += (_, _) => LaunchUi();   // double-click the icon = open the shell (windowless: no console)

        Application.Run(new ApplicationContext());
        StopBroker();   // tray closing — reclaim the in-proc broker
    }

    private static void ShowAcrylicTrayMenu()
    {
        if (_activeTrayWindow != null && !_activeTrayWindow.IsDisposed)
        {
            _activeTrayWindow.Close();
            _activeTrayWindow = null;
            return;
        }

        var entries = BuildMenuEntries();
        bool isDark = AcrylicTrayMenu.IsDarkTheme();
        _activeTrayWindow = new AcrylicTrayMenuWindow(entries, isDark);

        Point pos = Cursor.Position;
        Rectangle screen = Screen.FromPoint(pos).WorkingArea;

        int x = pos.X;
        int y = pos.Y - _activeTrayWindow.Height;

        if (x + _activeTrayWindow.Width > screen.Right) x = screen.Right - _activeTrayWindow.Width - 4;
        if (x < screen.Left) x = screen.Left + 4;
        if (y < screen.Top) y = pos.Y + 4;
        if (y + _activeTrayWindow.Height > screen.Bottom) y = screen.Bottom - _activeTrayWindow.Height - 4;

        _activeTrayWindow.Location = new Point(x, y);
        _activeTrayWindow.Show();
    }

    private static List<AcrylicTrayMenuWindow.MenuItemEntry> BuildMenuEntries()
    {
        var list = new List<AcrylicTrayMenuWindow.MenuItemEntry>
        {
            new() { Text = AppName, IsHeader = true },
            new() { IsSeparator = true },
            new() { Text = "Open Shell", Action = () => LaunchUi() },
            new() { IsSeparator = true },
            new() {
                Text = _broker == null ? "Virtual camera — start (in-proc grid)" : "Virtual camera — stop",
                Action = () => ToggleBroker()
            }
        };

        var windowSubItems = new List<AcrylicTrayMenuWindow.MenuItemEntry>();
        foreach (var w in EnumerateWindows())
        {
            var hwnd = w.Hwnd;
            windowSubItems.Add(new AcrylicTrayMenuWindow.MenuItemEntry
            {
                Text = Trunc(w.Title, 48),
                Action = () => LaunchProducer($"--type capture --hwnd {(long)hwnd}")
            });
        }
        if (windowSubItems.Count == 0)
        {
            windowSubItems.Add(new AcrylicTrayMenuWindow.MenuItemEntry { Text = "(no capturable windows)", Enabled = false });
        }

        var sourcesItem = new AcrylicTrayMenuWindow.MenuItemEntry
        {
            Text = "Add a camera source",
            SubItems = new List<AcrylicTrayMenuWindow.MenuItemEntry>
            {
                new() { Text = "Capture a window", SubItems = windowSubItems },
                new() { Text = "Virtual display", Action = () => LaunchProducer("--type display") }
            }
        };
        list.Add(sourcesItem);

        var testsItem = new AcrylicTrayMenuWindow.MenuItemEntry
        {
            Text = "Tests",
            SubItems = new List<AcrylicTrayMenuWindow.MenuItemEntry>
            {
                new() { Text = "DirectPort Interop", Action = () => LaunchUi("http://shell/tests/DirectPortInterop.obp") },
                new() { Text = "Test pattern (ss surface)", Action = () => Launch("surface --grant") },
                new() { Text = "Open TUI (live namespace)", Action = () => Launch("tui") }
            }
        };
        list.Add(testsItem);

        list.Add(new AcrylicTrayMenuWindow.MenuItemEntry { IsSeparator = true });
        list.Add(new AcrylicTrayMenuWindow.MenuItemEntry { Text = "Exit", Action = () => Application.ExitThread() });

        return list;
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
            var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "runspace", "windows", "app.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath);

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch (Exception ex)
        {
            Dg.Warn("tray", ex);
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

    private sealed class AcrylicTrayMenuWindow : Form
    {
        private readonly List<MenuItemEntry> _entries;
        private readonly bool _isDark;
        private readonly int _itemHeight = 32;
        private int _hoverIndex = -1;
        private AcrylicTrayMenuWindow? _openSubmenu = null;
        public AcrylicTrayMenuWindow? ParentMenu;

        public class MenuItemEntry
        {
            public string Text { get; set; } = "";
            public bool IsHeader { get; set; }
            public bool IsSeparator { get; set; }
            public bool Enabled { get; set; } = true;
            public Action? Action { get; set; }
            public List<MenuItemEntry>? SubItems { get; set; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public AcrylicTrayMenuWindow(List<MenuItemEntry> entries, bool isDark)
        {
            _entries = entries;
            _isDark = isDark;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            int width = 220;
            int height = 8;
            foreach (var item in _entries)
            {
                if (item.IsSeparator) height += 9;
                else height += _itemHeight;

                using var g = CreateGraphics();
                int textW = (int)g.MeasureString(item.Text, Font).Width + 40;
                if (textW > width) width = textW;
            }
            height += 8;
            ClientSize = new Size(width, height);

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.FromArgb(1, 1, 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AcrylicTrayMenu.EnableAcrylic(Handle, _isDark);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (IsDisposed) return;

            Point cursor = Cursor.Position;
            if (Bounds.Contains(cursor)) return;
            if (_openSubmenu != null && !_openSubmenu.IsDisposed && _openSubmenu.Bounds.Contains(cursor)) return;

            CloseAll();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ACTIVATEAPP = 0x001C;
            if (m.Msg == WM_ACTIVATEAPP && m.WParam == IntPtr.Zero)
            {
                CloseAll();
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (IsDisposed) return;
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Background
            Color bg = _isDark ? Color.FromArgb(140, 20, 20, 25) : Color.FromArgb(150, 245, 245, 250);
            using (var b = new SolidBrush(bg))
            {
                g.FillRectangle(b, ClientRectangle);
            }

            // Border
            Color border = _isDark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0);
            using (var p = new Pen(border))
            {
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }

            // Render Items
            int y = 4;
            for (int i = 0; i < _entries.Count; i++)
            {
                var item = _entries[i];
                if (item.IsSeparator)
                {
                    int sepY = y + 4;
                    Color sepColor = _isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
                    using var p = new Pen(sepColor);
                    g.DrawLine(p, 10, sepY, Width - 10, sepY);
                    y += 9;
                    continue;
                }

                Rectangle itemRect = new Rectangle(4, y, Width - 8, _itemHeight);
                if (i == _hoverIndex && item.Enabled && !item.IsHeader)
                {
                    Color hoverColor = _isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
                    using var b = new SolidBrush(hoverColor);
                    using var path = GetRoundedPath(itemRect, 4);
                    g.FillPath(b, path);
                }

                // Text
                Color textColor;
                if (item.IsHeader)
                {
                    textColor = _isDark ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0);
                }
                else if (!item.Enabled)
                {
                    textColor = _isDark ? Color.FromArgb(100, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0);
                }
                else
                {
                    textColor = _isDark ? Color.FromArgb(240, 240, 245) : Color.FromArgb(20, 20, 25);
                }

                Font fontToUse = item.IsHeader ? new Font(Font, FontStyle.Bold) : Font;
                TextRenderer.DrawText(g, item.Text, fontToUse,
                    new Rectangle(itemRect.X + 10, itemRect.Y, itemRect.Width - 25, itemRect.Height),
                    textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                // Submenu indicator (chevron)
                if (item.SubItems != null && item.SubItems.Count > 0)
                {
                    TextRenderer.DrawText(g, "▶", fontToUse,
                        new Rectangle(itemRect.Right - 18, itemRect.Y, 15, itemRect.Height),
                        textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }

                y += _itemHeight;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            base.OnMouseMove(e);
            int newHover = GetItemIndexAt(e.Location);
            if (newHover != _hoverIndex)
            {
                _hoverIndex = newHover;
                Invalidate();

                if (_hoverIndex >= 0 && _hoverIndex < _entries.Count)
                {
                    var item = _entries[_hoverIndex];
                    if (item.SubItems != null && item.SubItems.Count > 0 && item.Enabled)
                    {
                        OpenSubmenu(_hoverIndex, item.SubItems);
                    }
                    else
                    {
                        CloseSubmenu();
                    }
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            base.OnMouseClick(e);
            int idx = GetItemIndexAt(e.Location);
            if (idx >= 0 && idx < _entries.Count)
            {
                var item = _entries[idx];
                if (item.Enabled && !item.IsHeader && !item.IsSeparator && item.Action != null)
                {
                    Action act = item.Action;
                    CloseAll();
                    act.Invoke();
                }
            }
        }

        private int GetItemIndexAt(Point pt)
        {
            int y = 4;
            for (int i = 0; i < _entries.Count; i++)
            {
                var item = _entries[i];
                if (item.IsSeparator)
                {
                    y += 9;
                    continue;
                }
                if (pt.Y >= y && pt.Y < y + _itemHeight) return i;
                y += _itemHeight;
            }
            return -1;
        }

        private void OpenSubmenu(int index, List<MenuItemEntry> subItems)
        {
            if (IsDisposed || !IsHandleCreated) return;
            CloseSubmenu();

            int y = 4;
            for (int i = 0; i < index; i++)
            {
                y += _entries[i].IsSeparator ? 9 : _itemHeight;
            }

            _openSubmenu = new AcrylicTrayMenuWindow(subItems, _isDark);
            _openSubmenu.ParentMenu = this;

            Point screenPt = new Point(Location.X + Width - 4, Location.Y + y);
            Rectangle screen = Screen.FromPoint(screenPt).WorkingArea;
            if (screenPt.X + _openSubmenu.Width > screen.Right)
            {
                screenPt.X = Location.X - _openSubmenu.Width + 4;
            }
            if (screenPt.Y + _openSubmenu.Height > screen.Bottom)
            {
                screenPt.Y = screen.Bottom - _openSubmenu.Height - 4;
            }

            _openSubmenu.Location = screenPt;
            _openSubmenu.Show();
        }

        private void CloseSubmenu()
        {
            if (_openSubmenu != null)
            {
                var sub = _openSubmenu;
                _openSubmenu = null;
                if (!sub.IsDisposed)
                {
                    sub.CloseSubmenu();
                    sub.Close();
                    sub.Dispose();
                }
            }
        }

        public void CloseAll()
        {
            if (ParentMenu != null && !ParentMenu.IsDisposed)
            {
                ParentMenu.CloseAll();
                return;
            }
            CloseSubmenu();
            if (!IsDisposed)
            {
                Close();
                Dispose();
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private static class AcrylicTrayMenu
    {
        public static bool IsDarkTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                if (val is int light) return light == 0;
            }
            catch { }
            return true;
        }

        public static void Apply(ContextMenuStrip menu)
        {
            bool isDark = IsDarkTheme();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Padding = new Padding(4, 4, 4, 4);
            menu.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            menu.Renderer = new AcrylicRenderer(isDark);

            WireDropDown(menu, isDark);
        }

        private static void WireDropDown(ToolStripDropDown dropDown, bool isDark)
        {
            dropDown.Opened += (s, e) =>
            {
                if (s is ToolStripDropDown d && d.IsHandleCreated)
                {
                    EnableAcrylic(d.Handle, isDark);
                }
            };

            foreach (ToolStripItem item in dropDown.Items)
            {
                if (item is ToolStripDropDownItem itemWithDown && itemWithDown.HasDropDownItems)
                {
                    if (itemWithDown.DropDown is ToolStripDropDownMenu subMenu)
                    {
                        subMenu.ShowImageMargin = false;
                        subMenu.ShowCheckMargin = false;
                        subMenu.Padding = new Padding(4, 4, 4, 4);
                        subMenu.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
                    }
                    WireDropDown(itemWithDown.DropDown, isDark);
                }
            }
        }

        public static void EnableAcrylic(IntPtr hwnd, bool isDark)
        {
            try
            {
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref darkMode, sizeof(int));

                int backdropType = 3; // DWMSBT_ACRYLIC
                DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdropType, sizeof(int));

                uint gradientColor = isDark ? 0x88202025 : 0x88F5F5FA; // AABBGGRR (88 alpha = ~53% opacity acrylic blur)
                var accent = new AccentPolicy
                {
                    AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                    GradientColor = (int)gradientColor
                };

                int accentSize = Marshal.SizeOf(accent);
                IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
                try
                {
                    Marshal.StructureToPtr(accent, accentPtr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = 19, // WCA_ACCENT_POLICY
                        Data = accentPtr,
                        SizeOfData = accentSize
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(accentPtr);
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    }

    private sealed class AcrylicRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _isDark;
        private readonly Color _bgColor;
        private readonly Color _textColor;
        private readonly Color _disabledTextColor;
        private readonly Color _hoverBgColor;
        private readonly Color _borderColor;
        private readonly Color _separatorColor;

        public AcrylicRenderer(bool isDark) : base(new AcrylicColorTable(isDark))
        {
            _isDark = isDark;
            if (isDark)
            {
                _bgColor = Color.FromArgb(170, 24, 24, 28);
                _textColor = Color.FromArgb(240, 240, 245);
                _disabledTextColor = Color.FromArgb(110, 110, 120);
                _hoverBgColor = Color.FromArgb(45, 255, 255, 255);
                _borderColor = Color.FromArgb(40, 255, 255, 255);
                _separatorColor = Color.FromArgb(30, 255, 255, 255);
            }
            else
            {
                _bgColor = Color.FromArgb(180, 245, 245, 250);
                _textColor = Color.FromArgb(20, 20, 25);
                _disabledTextColor = Color.FromArgb(130, 130, 140);
                _hoverBgColor = Color.FromArgb(30, 0, 0, 0);
                _borderColor = Color.FromArgb(35, 0, 0, 0);
                _separatorColor = Color.FromArgb(25, 0, 0, 0);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_bgColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_borderColor);
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled) return;
            if (e.Item.Selected)
            {
                var rect = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                using var brush = new SolidBrush(_hoverBgColor);
                using var path = GetRoundedPath(rect, 4);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _textColor : _disabledTextColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(_separatorColor);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private static System.Drawing.Drawing2D.GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class AcrylicColorTable : ProfessionalColorTable
    {
        private readonly bool _isDark;
        public AcrylicColorTable(bool isDark) { _isDark = isDark; }

        public override Color ToolStripDropDownBackground => _isDark ? Color.FromArgb(170, 24, 24, 28) : Color.FromArgb(180, 245, 245, 250);
        public override Color ImageMarginGradientBegin => Color.Transparent;
        public override Color ImageMarginGradientMiddle => Color.Transparent;
        public override Color ImageMarginGradientEnd => Color.Transparent;
        public override Color MenuBorder => Color.Transparent;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.Transparent;
        public override Color MenuItemSelectedGradientBegin => Color.Transparent;
        public override Color MenuItemSelectedGradientEnd => Color.Transparent;
        public override Color MenuStripGradientBegin => Color.Transparent;
        public override Color MenuStripGradientEnd => Color.Transparent;
    }
}
