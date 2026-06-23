using System;

namespace Subsystem.Shell.Cell;

// shell/tui — the SHARED cell renderer (re-integration of tui-dwm; #51). Heads are peers: Cell/CellBuffer,
// the compositor and the multi-session loop are ONE implementation compiled into both the Windows and the
// Android head; only the ICellSurface present seam branches per head (VtRenderer console / D3D12 on Windows
// <-> Vulkan/SurfaceView on Android — a given). This file is pure System.*; it holds no head dependency.
//
// A Cell is 4 bytes: a 16-bit rune + 8-bit fg + 8-bit bg (xterm-256), so a frame is a flat row-major Cell[]
// that diffs cheaply against the previous frame.

// 4-byte cell: rune + fg + bg. Style is reserved (no bits in the 32-bit cell today) but kept so the VT
// renderer's SGR path stays intact for when attributes get bit-packed into the spare ordering.
public struct Cell
{
    public char Rune;   // 16-bit character (full BMP)
    public byte Fg;     // 8-bit foreground (xterm-256 index)
    public byte Bg;     // 8-bit background (0 = terminal default, so Mica/acrylic shows through)

    public readonly byte Style => 0;   // reserved; ApplyStyles reads it so the SGR path is ready

    public Cell(char rune, byte fg = 7, byte bg = 16)
    {
        Rune = rune;
        Fg = fg;
        Bg = bg;
    }

    public static readonly Cell Empty = new Cell(' ', 7, 16);

    public override readonly bool Equals(object? obj) => obj is Cell cell && Equals(cell);
    public readonly bool Equals(Cell other) => Rune == other.Rune && Fg == other.Fg && Bg == other.Bg;
    public override readonly int GetHashCode() => HashCode.Combine(Rune, Fg, Bg);
    public static bool operator ==(Cell left, Cell right) => left.Equals(right);
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);
}

// A flat row-major grid of cells (index = y * Width + x). Resize preserves the overlapping region; the
// renderer diffs current vs previous to emit only the changed runs.
public sealed class CellBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Cell[] Cells;   // flat, row-major

    public CellBuffer(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Cells = new Cell[Width * Height];
        Clear(Cell.Empty);
    }

    public ref Cell At(int x, int y) => ref Cells[y * Width + x];

    public void Resize(int newWidth, int newHeight)
    {
        newWidth = Math.Max(0, newWidth);
        newHeight = Math.Max(0, newHeight);
        if (newWidth == Width && newHeight == Height) return;

        var newCells = new Cell[newWidth * newHeight];
        Array.Fill(newCells, Cell.Empty);

        int minW = Math.Min(Width, newWidth);
        int minH = Math.Min(Height, newHeight);
        for (int y = 0; y < minH; y++)
            Cells.AsSpan(y * Width, minW).CopyTo(newCells.AsSpan(y * newWidth, minW));

        Cells = newCells;
        Width = newWidth;
        Height = newHeight;
    }

    public void Clear(Cell fill = default) => Array.Fill(Cells, fill);

    // Write a string starting at (x,y), clipped to the row. Returns the column past the last glyph.
    public int Write(int x, int y, string text, byte fg = 7, byte bg = 16)
    {
        if (y < 0 || y >= Height) return x;
        foreach (char c in text)
        {
            if (x < 0) { x++; continue; }
            if (x >= Width) break;
            ref Cell cell = ref At(x, y);
            cell.Rune = c; cell.Fg = fg; cell.Bg = bg;
            x++;
        }
        return x;
    }

    // Blit another buffer's cells into this one at (dx,dy), clipped. The compositor's one paste primitive —
    // each session pane composites into the frame through this (no ANSI between panes; cells all the way).
    public void Blit(CellBuffer src, int dx, int dy)
    {
        for (int sy = 0; sy < src.Height; sy++)
        {
            int ty = dy + sy;
            if (ty < 0 || ty >= Height) continue;
            for (int sx = 0; sx < src.Width; sx++)
            {
                int tx = dx + sx;
                if (tx < 0 || tx >= Width) continue;
                At(tx, ty) = src.At(sx, sy);
            }
        }
    }
}
