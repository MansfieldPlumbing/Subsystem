using System;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Subsystem.Windows;

// View — `ss view <host> [port]`: the scrcpy render leg in pure C#. A NATIVE Win32 window (user32/gdi32
// P/Invoke, real HWND + message loop + WndProc — no UI framework) that mirrors the phone's screen over
// WiFi, NO adb. It connects to the phone's existing /screen WebSocket (binary JPEG frames, port 8081),
// decodes each frame, and blits it with StretchDIBits. This is the consumer endpoint that replaces
// scrcpy's SDL window; input injection (back to the phone via its AccessibilityService) is the next leg
// and drops the Shizuku/adb dependency entirely.
//
// Progressive ladder: [now] JPEG + GDI blit -> input injection (A11y) -> H.264 (MF decode) -> D3D11
// swapchain / zero-copy -> SPAKE2-paired LAN -> a standalone published binary. All foreign names fenced
// to this file (ouroboros rule 0).
internal static class View
{
    public static int Run(string[] args)
    {
        // ss view <host> [port] [--grant]            WiFi: the phone's /screen JPEG WebSocket (no adb)
        // ss view --screencap <serial> [--grant]     adb shell `screencap` PNG poll — slow, no tap, proves the pipe
        string? host = null; int port = 8081; bool grant = false; string? screencap = null;
        var positional = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--grant" or "-grant") { grant = true; continue; }
            if (a is "--screencap" or "-screencap") { if (i + 1 < args.Length) screencap = args[++i]; continue; }
            positional.Add(a);
        }

        if (screencap != null)
        {
            if (!CapabilityGate.Allow("View", "screencap-" + screencap, grant, out _)) return 13;
            Console.WriteLine($"ss view: mirroring {screencap} via `screencap` (shell capture, no tap). Close the window to stop.");
            using var w = new ViewWindow(screencapSerial: screencap);
            return w.RunBlocking();
        }

        if (positional.Count > 0) host = positional[0];
        if (positional.Count > 1) int.TryParse(positional[1], out port);
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("usage: ss view <host> [port] [--grant]   |   ss view --screencap <serial> [--grant]");
            return 1;
        }

        if (!CapabilityGate.Allow("View", host!, grant, out _)) return 13;
        Console.WriteLine($"ss view: opening a native window for ws://{host}:{port}/. Close the window to stop.");
        using var win = new ViewWindow(host!, port);
        return win.RunBlocking();
    }
}

// The native window + the WiFi frame receiver. One instance per `ss view`.
internal sealed class ViewWindow : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _screencapSerial;
    private readonly object _lock = new();
    private byte[]? _frame;          // latest decoded BGRA, top-down
    private int _fw, _fh;            // frame dimensions
    private long _frames;            // received-frame counter (for the title bar)
    private IntPtr _hwnd;
    private readonly WndProcDelegate _wndProc;   // kept alive so the GC never collects the thunk
    private readonly CancellationTokenSource _cts = new();

    public ViewWindow(string host, int port) { _host = host; _port = port; _wndProc = WndProc; }
    public ViewWindow(string screencapSerial) { _host = ""; _port = 0; _screencapSerial = screencapSerial; _wndProc = WndProc; }

    public int RunBlocking()
    {
        IntPtr hInstance = GetModuleHandleW(IntPtr.Zero);
        string cls = "SsViewWindow";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0002 | 0x0001,                 // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),    // IDC_ARROW
            hbrBackground = GetStockObject(4),       // BLACK_BRUSH
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);

        _hwnd = CreateWindowExW(0, cls, $"ss view — {_host}  (connecting…)",
            0x00CF0000 | 0x10000000,                 // WS_OVERLAPPEDWINDOW | WS_VISIBLE
            0x80000000, 0, 0x80000000, 0,            // CW_USEDEFAULT x/size
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Console.Error.WriteLine("ss view: CreateWindowEx failed"); return 1; }

        // Default to a phone-ish portrait until the first frame tells us the real size.
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 420, 900, 0x0002 /*SWP_NOMOVE*/ | 0x0004 /*SWP_NOZORDER*/);
        ShowWindow(_hwnd, 5 /*SW_SHOW*/); UpdateWindow(_hwnd);

        // Frame source runs off the UI thread; it InvalidateRect's the window per frame.
        var owner = Subsystem.Vom.Vom.CreateOwner("\\View");
        if (_screencapSerial != null)
        {
            Subsystem.Vom.Vom.Spawn(owner, "screencap", _ => ScreencapLoop(_cts.Token));
        }
        else
        {
            Subsystem.Vom.Vom.Spawn(owner, "ReceiveLoop", _ => ReceiveLoopAsync(_cts.Token).GetAwaiter().GetResult());
        }

        // The classic Win32 message pump — this IS the window's lifetime.
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _cts.Cancel();
        return 0;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), ct);
                SetTitle($"ss view — {_host}  (connected)");

                var acc = new MemoryStream(256 * 1024);
                var buf = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    acc.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        acc.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Binary && acc.Length > 0)
                        OnImageFrame(acc.GetBuffer(), (int)acc.Length);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                SetTitle($"ss view — {_host}  (reconnecting: {ex.GetType().Name})");
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }
    }

    // adb `screencap -p` emits a full-screen PNG to stdout — shell capture, no MediaProjection tap. Slow
    // (a fresh PNG per frame, ~2-3 fps) but it proves the phone -> our pipeline -> our window path with
    // zero scrcpy. The smooth path swaps this source for the loopback-shell MediaCodec H.264 encoder.
    private void ScreencapLoop(CancellationToken ct)
    {
        string adb = Environment.GetEnvironmentVariable("SS_ADB") ?? "adb";
        long n = 0;
        SetTitle($"ss view — screencap {_screencapSerial} (starting…)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(adb, $"-s {_screencapSerial} exec-out screencap -p")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                using var ms = new MemoryStream(4 * 1024 * 1024);
                p.StandardOutput.BaseStream.CopyTo(ms);
                p.WaitForExit();
                if (ms.Length > 0) { OnImageFrame(ms.GetBuffer(), (int)ms.Length); n++; }
            }
            catch (Exception ex) { SetTitle($"ss view — screencap error: {ex.GetType().Name}"); }
            if (ct.WaitHandle.WaitOne(15)) break;
        }
    }

    private void OnImageFrame(byte[] jpeg, int count)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, 0, count, writable: false);
            using var bmp = new System.Drawing.Bitmap(ms);
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);   // 32bppArgb in memory == BGRA bytes
            var bgra = new byte[w * 4 * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
            bmp.UnlockBits(data);

            lock (_lock) { _frame = bgra; _fw = w; _fh = h; _frames++; }
            if (_frames == 1) SetTitle($"ss view — {_host}  {w}x{h}");
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
        catch (Exception ex) { Dg.Warn("view", ex); /* a corrupt/partial frame just gets skipped */ }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x000F:  // WM_PAINT
                Paint(hWnd);
                return IntPtr.Zero;
            case 0x0002:  // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        IntPtr hdc = BeginPaint(hWnd, out var ps);
        try
        {
            byte[]? frame; int fw, fh;
            lock (_lock) { frame = _frame; fw = _fw; fh = _fh; }
            GetClientRect(hWnd, out var rc);
            int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
            if (frame == null || fw == 0 || fh == 0 || cw == 0 || ch == 0) return;

            // Aspect-fit (letterbox) the frame into the client area.
            double sa = (double)fw / fh, da = (double)cw / ch;
            int dw, dh;
            if (da > sa) { dh = ch; dw = (int)(ch * sa); } else { dw = cw; dh = (int)(cw / sa); }
            int dx = (cw - dw) / 2, dy = (ch - dh) / 2;

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = fw, biHeight = -fh,        // negative = top-down BGRA
                biPlanes = 1, biBitCount = 32, biCompression = 0 /*BI_RGB*/,
            };
            SetStretchBltMode(hdc, 3 /*COLORONCOLOR*/);
            StretchDIBits(hdc, dx, dy, dw, dh, 0, 0, fw, fh, frame, ref bmi, 0 /*DIB_RGB_COLORS*/, 0x00CC0020 /*SRCCOPY*/);
        }
        finally { EndPaint(hWnd, ref ps); }
    }

    private void SetTitle(string s) { if (_hwnd != IntPtr.Zero) SetWindowTextW(_hwnd, s); }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }

    // ---- Win32 interop (fenced to this file) ------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("kernel32")] private static extern IntPtr GetModuleHandleW(IntPtr zero);
    [DllImport("user32")] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, uint x, int y, uint w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] private static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] private static extern void PostQuitMessage(int code);
    [DllImport("user32")] private static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [DllImport("user32")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string s);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("gdi32")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32")] private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
}
