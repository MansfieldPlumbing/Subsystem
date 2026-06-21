using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Subsystem.Windows;

// The cells -> VT console renderer (#51). Ported from tui-dwm (C:\tui-dwm\src\TuiDwm.Engine\VtRenderer.cs):
// the diffing painter that turns a CellBuffer into the minimum stream of ANSI escapes. Double-buffered —
// it compares current vs previous and emits only changed runs, so a live refresh doesn't flicker. conhost
// leaves VT processing off by default, so Initialize turns it on (kernel32) and the same engine renders
// identically in conhost or Windows Terminal.
public sealed class VtRenderer
{
    private const int  STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN        = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly StringBuilder _sb = new(1024 * 32);
    private byte _lastFg = 255;
    private byte _lastBg = 255;
    private byte _lastStyle = 255;

    public void Initialize()
    {
        IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(hOut, out uint outMode))
            SetConsoleMode(hOut, outMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);

        Console.OutputEncoding = Encoding.UTF8;
        // alternate screen buffer, hide cursor, reset attrs, clear
        Console.Out.Write("\x1b[?1049h\x1b[?25l\x1b[0m\x1b[2J");
        Console.Out.Flush();
        _lastFg = _lastBg = _lastStyle = 255;
    }

    // Force a full re-render on the next frame (call on terminal resize).
    public void Invalidate()
    {
        Console.Out.Write("\x1b[0m\x1b[2J");
        Console.Out.Flush();
        _lastFg = _lastBg = _lastStyle = 255;
    }

    public void Shutdown()
    {
        // reset styles, show cursor, leave the alternate screen (restores the user's scrollback)
        Console.Out.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }

    public void Render(CellBuffer current, CellBuffer previous)
    {
        _sb.Clear();
        int width = current.Width;
        int height = current.Height;

        if (previous.Width != width || previous.Height != height)
        {
            previous.Resize(width, height);
            previous.Clear(new Cell('\0', 255, 255));   // force a difference on every cell
        }

        for (int y = 0; y < height; y++)
        {
            bool inSequence = false;
            for (int x = 0; x < width; x++)
            {
                ref Cell curr = ref current.At(x, y);
                ref Cell prev = ref previous.At(x, y);

                if (curr == prev) { inSequence = false; continue; }

                if (!inSequence)
                {
                    _sb.Append("\x1b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
                    inSequence = true;
                }
                ApplyStyles(curr.Fg, curr.Bg);
                _sb.Append(curr.Rune == '\0' ? ' ' : curr.Rune);
            }
        }

        if (_sb.Length > 0)
        {
            Console.Out.Write(_sb.ToString());
            Console.Out.Flush();
        }
        // the just-drawn frame becomes the baseline for the next diff
        current.Cells.AsSpan().CopyTo(previous.Cells.AsSpan());
    }

    private void ApplyStyles(byte fg, byte bg)
    {
        if (_lastFg != fg)
        {
            _sb.Append("\x1b[38;5;").Append(fg).Append('m');
            _lastFg = fg;
        }
        if (_lastBg != bg)
        {
            if (bg == 0) _sb.Append("\x1b[49m");                       // terminal default background
            else _sb.Append("\x1b[48;5;").Append(bg).Append('m');
            _lastBg = bg;
        }
    }
}
