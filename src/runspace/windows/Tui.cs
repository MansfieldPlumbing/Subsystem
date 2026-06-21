using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Subsystem.Vom;                    // the Owner type + VomFormat etc.

namespace Subsystem.Windows;

// ss tui — the low-level dotnet renderer (#51): paints the LIVE VOM object namespace as cells -> VT.
// The portability/recovery FLOOR beneath the WebView/GPU surfaces (three-renderers-one-truth); when
// everything else is down, this rung still draws. It consumes ONE truth — Vom.Snapshot()/Totals() — and
// holds nothing of its own (invariant 4). The TUI registers itself as a real VOM owner (\Sessions\Tui) and
// Register's its framebuffer as a managed handle, so it appears in the very namespace it paints (the
// ouroboros) and proves the kernel end-to-end; on exit it Terminate's that owner (cascade reclaim).
//   ss tui            interactive: alternate-screen, live refresh, q/Esc/Ctrl-C to quit
//   ss tui --once     one frame to stdout (no alt-screen) — scriptable / used when output is redirected
internal static class Tui
{
    private const string OwnerPath = "\\Sessions\\Tui";

    public static int Run(string[] args)
    {
        bool once = args.Any(a => a is "--once" or "-once" or "/once")
                    || Console.IsOutputRedirected || Console.IsInputRedirected;

        // Dogfood the kernel: the renderer is itself an owned object in the namespace it renders.
        var owner = Vom.Vom.CreateOwner(OwnerPath);
        try
        {
            return once ? RenderOnce(owner) : RenderLoop(owner);
        }
        finally
        {
            Vom.Vom.Terminate(owner);   // cascade reclaim — the \Sessions\Tui owner + its framebuffer handle
        }
    }

    // ---- interactive: alternate screen, live diff-refresh ----
    private static int RenderLoop(Owner owner)
    {
        int w = Math.Clamp(SafeWidth(100), 40, 400);
        int h = Math.Clamp(SafeHeight(24), 8, 200);
        var current  = new CellBuffer(w, h);
        var previous = new CellBuffer(w, h);
        Vom.Vom.Register(owner, "CellBuffer", current, name: "Framebuffer");

        bool priorCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;   // Ctrl-C becomes a keypress we read, so Shutdown always runs
        var renderer = new VtRenderer();
        renderer.Initialize();
        try
        {
            bool running = true;
            while (running)
            {
                int nw = Math.Clamp(SafeWidth(w), 40, 400);
                int nh = Math.Clamp(SafeHeight(h), 8, 200);
                if (nw != current.Width || nh != current.Height)
                {
                    current.Resize(nw, nh);
                    previous.Resize(nw, nh);
                    renderer.Invalidate();
                }

                Compose(current, owner);
                renderer.Render(current, previous);

                // poll ~400ms in short slices so keypresses feel responsive without busy-spinning
                for (int waited = 0; waited < 400 && !Console.KeyAvailable; waited += 25)
                    Thread.Sleep(25);

                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(intercept: true);
                    if (k.Key is ConsoleKey.Q or ConsoleKey.Escape
                        || (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.C))
                        running = false;
                }
            }
        }
        finally
        {
            renderer.Shutdown();
            Console.TreatControlCAsInput = priorCtrlC;
        }
        return 0;
    }

    // ---- one-shot: a single frame to stdout as plain text (scriptable / output redirected) ----
    private static int RenderOnce(Owner owner)
    {
        var rows = ReadNamespace();
        int w = Math.Clamp(SafeWidth(100), 60, 160);
        int h = rows.Count + 5;
        var buf = new CellBuffer(w, h);
        Vom.Vom.Register(owner, "CellBuffer", buf, name: "Framebuffer");
        Compose(buf, owner);
        PrintPlain(buf);
        return 0;
    }

    // ---- composition: paint ONE projection of the live namespace into the cell grid ----
    private static void Compose(CellBuffer b, Owner self)
    {
        b.Clear(Cell.Empty);
        const byte accent = 24, dim = 244, text = 15, bar = 39, alarm = 203, footBg = 236;

        var (owners, handles, bytes) = Vom.Vom.Totals();
        var rows = ReadNamespace();
        rows.Sort((x, y) => y.Bytes != x.Bytes ? y.Bytes.CompareTo(x.Bytes) : y.Handles.CompareTo(x.Handles));

        // title bar
        b.Write(0, 0, new string(' ', b.Width), text, accent);
        b.Write(1, 0, "Subsystem · live VOM namespace", text, accent);
        string totals = $"{Plural(owners, "owner")}   {Plural(handles, "handle")}   {Human(bytes)} ";
        b.Write(Math.Max(0, b.Width - totals.Length - 1), 0, totals, text, accent);

        // column headers
        int cOwner = 0, cHandles = 42, cBytes = 54, cBar = 66;
        b.Write(cOwner, 2, "OWNER", dim);
        b.Write(cHandles, 2, "HANDLES", dim);
        b.Write(cBytes, 2, "BYTES", dim);
        b.Write(cBar, 2, "FOOTPRINT", dim);

        long max = rows.Count > 0 ? Math.Max(1L, rows.Max(r => Math.Max(r.Bytes, r.Handles))) : 1L;
        int barWidth = Math.Max(6, b.Width - cBar - 1);

        int row = 3;
        foreach (var r in rows)
        {
            if (row >= b.Height - 1) break;
            byte fg = r.Cancelled ? alarm : text;
            b.Write(cOwner, row, Trunc(r.Owner, cHandles - cOwner - 1), fg);
            b.Write(cHandles, row, r.Handles.ToString(), fg);
            b.Write(cBytes, row, Human(r.Bytes), fg);

            long metric = r.Bytes > 0 ? r.Bytes : r.Handles;
            int fill = (int)(barWidth * metric / max);
            for (int i = 0; i < fill && cBar + i < b.Width; i++)
            {
                ref Cell c = ref b.At(cBar + i, row);
                c.Rune = '█'; c.Fg = bar;
            }
            row++;
        }

        if (rows.Count == 0)
            b.Write(cOwner, 3, "(no owners registered in this process yet)", dim);

        // footer
        int last = b.Height - 1;
        b.Write(0, last, new string(' ', b.Width), dim, footBg);
        b.Write(1, last, "q/Esc quit · live · this view is itself \\Sessions\\Tui in the namespace", dim, footBg);
    }

    // Read the live namespace off the public introspection surface (Vom.GetOwners returns the real
    // Owner objects; this is strongly-typed and avoids reflection).
    private static List<OwnerRow> ReadNamespace()
    {
        var rows = new List<OwnerRow>();
        foreach (var o in Vom.Vom.GetOwners())
        {
            rows.Add(new OwnerRow(
                o.Path,
                o.PathToId.Count,
                Interlocked.Read(ref o.CurrentBytes),
                o.Cts.IsCancellationRequested
            ));
        }
        return rows;
    }

    private static void PrintPlain(CellBuffer b)
    {
        var sb = new System.Text.StringBuilder(b.Width * b.Height);
        for (int y = 0; y < b.Height; y++)
        {
            int lastGlyph = -1;
            for (int x = 0; x < b.Width; x++)
            {
                char r = b.At(x, y).Rune;
                if (r != ' ' && r != '\0') lastGlyph = x;
            }
            for (int x = 0; x <= lastGlyph; x++)
            {
                char r = b.At(x, y).Rune;
                sb.Append(r == '\0' ? ' ' : r);
            }
            sb.Append('\n');
        }
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    private static string Plural(long n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Human(long bytes)
    {
        if (bytes <= 0) return "0";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }

    private static string Trunc(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        return max <= 1 ? s.Substring(0, max) : s.Substring(0, max - 1) + "…";
    }

    private static int SafeWidth(int fallback)
    {
        try { int w = Console.WindowWidth; return w > 0 ? w : fallback; }
        catch (IOException) { return fallback; }
    }

    private static int SafeHeight(int fallback)
    {
        try { int h = Console.WindowHeight; return h > 0 ? h : fallback; }
        catch (IOException) { return fallback; }
    }

    private readonly record struct OwnerRow(string Owner, long Handles, long Bytes, bool Cancelled);
}
