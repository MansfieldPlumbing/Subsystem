using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Subsystem.Vom;

namespace Subsystem.Shell.Cell;

// ss CellShell — the multi-session pwsh surface (THE FLIP, multiplexed). The product face of the tui-dwm
// re-integration: it LAUNCHES, hosts MULTIPLE sessions, and ENDS THEM POLITELY — Scott's bar, not ergonomics.
//
// Recursion made load-bearing: the surface is a VOM owner \Sessions\CellShell; each session is a real CHILD
// owner (Vom.Spawn) running its OWN in-proc runspace on its OWN thread. A session's pipeline OBJECTS are
// formatted to text (Out-String, no SGR) and painted as cells — so there is NO ANSI between sessions (the
// original ask). Closing a session is Terminate(child) (cooperative cancel -> Thread.Interrupt -> reclaim,
// the runspace disposed as the thread unwinds); quitting is Terminate(parent), which CASCADES to every child.
// "Polite" = deterministic: a closed session leaves no thread, no runspace, no handle.
//
// Heads are peers: this whole file is shared (shell/tui). Only the ICellSurface present seam differs per head
// (VtRenderer console / D3D12 on Windows <-> Vulkan/SurfaceView on Android). `loadCmdlets` is the per-head
// cmdlet seam so shared code binds to no head type.
//
//   ss CellShell [--session "<cmd>"]...     interactive: Tab cycles · Ctrl+N new · Ctrl+W close · Enter runs · Esc quits
//   ss CellShell --once [--session "<cmd>"] headless proof: spawn N sessions, paint panes, then prove cascade teardown
internal static class CellShell
{
    private const string Root = "\\Sessions\\CellShell";

    public static int Run(string[] args, Action<InitialSessionState> loadCmdlets)
    {
        bool once = args.Any(a => a is "--once" or "-once" or "/once")
                    || Console.IsOutputRedirected || Console.IsInputRedirected;
        var initial = ParseInitialCommands(args);

        var parent = Vom.Vom.CreateOwner(Root);
        try
        {
            return once
                ? RunOnce(parent, initial, loadCmdlets)
                : RunInteractive(parent, initial, loadCmdlets, new VtRenderer());
        }
        finally
        {
            Vom.Vom.Terminate(parent);   // idempotent cascade: every child session owner + thread + handle
        }
    }

    // ===== a session: a child Sub-VOM + its own runspace on its own thread =====
    private sealed class Session
    {
        public string Name = "";
        public string Title = "pwsh";
        public Owner Owner = null!;
        public int X, Y, W = 58, H = 18;   // float-window rect (the tui-dwm look) — CRQ000000000002
        public readonly object Lock = new();
        public string[] Output = { "(starting…)" };
        public string Status = "starting";
        public readonly ConcurrentQueue<string> Queue = new();
        public readonly AutoResetEvent Signal = new(false);
        public long Commands;

        public void Set(string[] output, string status)
        {
            lock (Lock) { Output = output; Status = status; }
        }
        public (string[] Output, string Status) Snapshot()
        {
            lock (Lock) { return (Output, Status); }
        }
    }

    private static Session SpawnSession(Owner parent, int idx, Action<InitialSessionState> loadCmdlets, string? seed)
    {
        var s = new Session { Name = idx.ToString(), Title = $"pwsh {idx}" };
        s.X = 2 + ((idx - 1) % 6) * 6;     // staggered float placement
        s.Y = 1 + ((idx - 1) % 5) * 3;
        // Vom.Spawn: child owner \Sessions\CellShell\Ps\<idx> on its own thread, token LINKED to parent's, so
        // Terminate(parent) cascades. When the work delegate returns, the child self-Terminates (idempotent).
        s.Owner = Vom.Vom.Spawn(parent, s.Name, child => SessionWork(child, s, loadCmdlets, seed));
        return s;
    }

    // The session thread: an in-proc runspace (the SAME loader the shell + MCP use), then a command pump that
    // parks on the cancel token. Terminate cancels the token -> WaitAny returns -> loop exits -> `using rs`
    // disposes the runspace. Native pwsh objects -> text -> cells; never ANSI.
    private static void SessionWork(Owner child, Session s, Action<InitialSessionState> loadCmdlets, string? seed)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        loadCmdlets(iss);
        using var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();
        // The runspace is a first-class handle in this session's owner — enumerable in the namespace it serves.
        Vom.Vom.Register(child, "Runspace", rs, name: "Runspace");

        if (!string.IsNullOrWhiteSpace(seed)) s.Queue.Enqueue(seed);
        else s.Set(new[] { "session ready — type a command, Enter to run" }, "idle");

        var token = child.Token;
        var waits = new WaitHandle[] { token.WaitHandle, s.Signal };
        while (!token.IsCancellationRequested)
        {
            while (s.Queue.TryDequeue(out var cmd))
            {
                if (token.IsCancellationRequested) return;
                s.Set(s.Snapshot().Output, "running");
                var lines = RunCaptured(rs, cmd);
                Interlocked.Increment(ref s.Commands);
                s.Set(lines, "idle");
            }
            WaitHandle.WaitAny(waits, 250);   // wake on a new command or on cancel
        }
    }

    private static string[] RunCaptured(Runspace rs, string command)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(command).AddCommand("Out-String");   // pipeline OBJECTS -> text (no Out-Default, no SGR)
        var sb = new StringBuilder();
        try { foreach (var o in ps.Invoke()) sb.Append(o?.ToString()); }
        catch (Exception ex) { return new[] { "ERROR: " + ex.Message }; }
        foreach (var e in ps.Streams.Error) sb.Append("\n[error] ").Append(e.ToString());
        var text = sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
        return text.Length == 0 ? new[] { "(no output)" } : text.Split('\n');
    }

    // ===== interactive: alt-screen, live diff-refresh, multiplexed =====
    private static int RunInteractive(Owner parent, List<string> initial, Action<InitialSessionState> loadCmdlets, ICellPresenter surface)
    {
        var sessions = new List<Session>();
        if (initial.Count == 0) initial.Add("");                  // start with one empty session
        for (int i = 0; i < initial.Count; i++)
            sessions.Add(SpawnSession(parent, i + 1, loadCmdlets, string.IsNullOrWhiteSpace(initial[i]) ? null : initial[i]));
        int nextIdx = sessions.Count + 1;
        int focus = 0;
        var input = new StringBuilder();

        int w = Math.Clamp(SafeWidth(100), 40, 400);
        int h = Math.Clamp(SafeHeight(24), 8, 200);
        var cur = new CellBuffer(w, h);
        var prev = new CellBuffer(w, h);
        Vom.Vom.Register(parent, "CellBuffer", cur, name: "Framebuffer");

        bool priorCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        surface.Initialize();
        try
        {
            bool running = true;
            while (running)
            {
                int nw = Math.Clamp(SafeWidth(w), 40, 400);
                int nh = Math.Clamp(SafeHeight(h), 8, 200);
                if (nw != cur.Width || nh != cur.Height) { cur.Resize(nw, nh); prev.Resize(nw, nh); surface.Invalidate(); }
                if (focus >= sessions.Count) focus = Math.Max(0, sessions.Count - 1);

                ComposeInteractive(cur, sessions, focus, input.ToString());
                surface.Present(cur, prev);

                for (int waited = 0; waited < 120 && !Console.KeyAvailable; waited += 20) Thread.Sleep(20);
                if (!Console.KeyAvailable) continue;
                var k = Console.ReadKey(intercept: true);
                bool ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control);
                bool shift = k.Modifiers.HasFlag(ConsoleModifiers.Shift);
                if (k.Key == ConsoleKey.Escape || (ctrl && (k.Key == ConsoleKey.C || k.Key == ConsoleKey.Q))) running = false;
                else if (k.Key == ConsoleKey.Tab && sessions.Count > 0) focus = shift ? (focus - 1 + sessions.Count) % sessions.Count : (focus + 1) % sessions.Count;
                else if (ctrl && k.Key == ConsoleKey.N) { sessions.Add(SpawnSession(parent, nextIdx++, loadCmdlets, null)); focus = sessions.Count - 1; }
                else if (ctrl && k.Key == ConsoleKey.W) CloseSession(sessions, ref focus);
                else if (k.Key == ConsoleKey.Enter) { if (focus < sessions.Count && input.Length > 0) { sessions[focus].Queue.Enqueue(input.ToString()); sessions[focus].Signal.Set(); input.Clear(); } }
                else if (k.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; }
                else if (!char.IsControl(k.KeyChar) && k.KeyChar != '\0') input.Append(k.KeyChar);
            }
        }
        finally
        {
            surface.Shutdown();
            Console.TreatControlCAsInput = priorCtrlC;
        }
        return 0;
    }

    // effective on-screen rect (clamped to the buffer; taskbar row reserved) — shared by draw + hit-test
    private static (int x, int y, int w, int h) Rect(Session s, int bufW, int bufH)
    {
        int x = Math.Max(0, s.X), y = Math.Max(0, s.Y);
        return (x, y, Math.Min(s.W, bufW - x), Math.Min(s.H, bufH - 1 - y));
    }


    // Polite close of ONE session: cancel token -> thread unwinds (runspace disposed) -> handles reclaimed ->
    // owner dropped. Observed by the next frame: the session is simply gone.
    private static void CloseSession(List<Session> sessions, ref int focus)
    {
        if (sessions.Count == 0) return;
        var s = sessions[focus];
        Vom.Vom.Terminate(s.Owner);
        sessions.RemoveAt(focus);
        if (focus >= sessions.Count) focus = Math.Max(0, sessions.Count - 1);
    }

    // The float-window compositor (the tui-dwm look, de-larped): Mica desktop, each session a floating
    // window with chrome, focused on top, lean taskbar. Ported from the reference Compositor — CRQ000000000002.
    private static void ComposeInteractive(CellBuffer b, List<Session> sessions, int focus, string input)
    {
        b.Clear(new Cell(' ', 7, 0));   // Mica desktop: Bg=0 -> \x1b[49m -> terminal acrylic shows through

        if (sessions.Count == 0)
            b.Write(2, 2, "no sessions — Ctrl+N to spawn, Esc to quit", 244, 0);
        else
            // two passes: unfocused windows first, focused last (drawn on top)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < sessions.Count; i++)
                {
                    bool f = i == focus;
                    if (f != (pass == 1)) continue;
                    var (output, status) = sessions[i].Snapshot();
                    DrawWindow(b, sessions[i], f, output, status, input);
                }

        DrawTaskbar(b, sessions, focus);
    }

    // One floating window: rounded chrome + focus-colored title bar with ● ● + the session's cells inside;
    // the focused window carries the prompt + input on its last content row.
    private static void DrawWindow(CellBuffer b, Session s, bool focused, string[] output, string status, string input)
    {
        var (x, y, w, h) = Rect(s, b.Width, b.Height);
        if (w < 12 || h < 4) return;

        byte bFg  = focused ? (byte)51  : (byte)240;   // border: cyan focused / grey
        byte tbBg = focused ? (byte)17  : (byte)233;   // title bar bg
        byte tbFg = focused ? (byte)51  : (byte)244;
        byte cBg  = focused ? (byte)234 : (byte)235;   // content bg (opaque over Mica so text reads)

        // top frame
        Put(b, x, y, '╭', bFg, 0); Put(b, x + w - 1, y, '╮', bFg, 0);
        for (int c = x + 1; c < x + w - 1; c++) Put(b, c, y, '─', bFg, 0);

        // title row
        int ty = y + 1;
        Put(b, x, ty, '│', bFg, tbBg); Put(b, x + w - 1, ty, '│', bFg, tbBg);
        for (int c = x + 1; c < x + w - 1; c++) Put(b, c, ty, ' ', tbFg, tbBg);
        if (w >= 10)
        {
            Put(b, x + w - 2, ty, '●', focused ? (byte)203 : (byte)238, tbBg);   // close
            Put(b, x + w - 4, ty, '●', focused ? (byte)226 : (byte)238, tbBg);   // minimize
        }
        string t = $" {s.Title}  [{status}] ";
        int maxLen = w - 2 - (w >= 10 ? 6 : 0);
        if (maxLen > 0) { if (t.Length > maxLen) t = t[..maxLen]; for (int i = 0; i < t.Length; i++) Put(b, x + 1 + i, ty, t[i], tbFg, tbBg); }

        // content area + side borders
        int innerTop = y + 2, innerBot = y + h - 1, innerLeft = x + 1, innerW = w - 2;
        for (int row = innerTop; row < innerBot; row++)
        {
            Put(b, x, row, '│', bFg, 0); Put(b, x + w - 1, row, '│', bFg, 0);
            for (int c = innerLeft; c < innerLeft + innerW; c++) Put(b, c, row, ' ', (byte)250, cBg);
        }

        // bottom frame
        int by = y + h - 1;
        Put(b, x, by, '╰', bFg, 0); Put(b, x + w - 1, by, '╯', bFg, 0);
        for (int c = x + 1; c < x + w - 1; c++) Put(b, c, by, '─', bFg, 0);

        // cells inside: output lines, clipped; focused window reserves its last row for the prompt
        int rows = innerBot - innerTop;
        int outRows = focused ? rows - 1 : rows;
        for (int i = 0; i < output.Length && i < outRows; i++)
        {
            string line = output[i];
            for (int j = 0; j < line.Length && j < innerW; j++) Put(b, innerLeft + j, innerTop + i, line[j], (byte)250, cBg);
        }
        if (focused && rows > 0)
        {
            int iy = innerBot - 1;
            string prompt = "PS> " + input + "▏";
            if (prompt.Length > innerW) prompt = prompt[^innerW..];
            for (int j = 0; j < innerW; j++) Put(b, innerLeft + j, iy, j < prompt.Length ? prompt[j] : ' ', (byte)15, (byte)236);
        }
    }

    // Lean taskbar across the bottom row — session chips (focused highlighted) + the key hints.
    private static void DrawTaskbar(CellBuffer b, List<Session> sessions, int focus)
    {
        int y = b.Height - 1;
        const byte bg = 236, fg = 250, selBg = 24;
        for (int x = 0; x < b.Width; x++) Put(b, x, y, ' ', fg, bg);
        int cx = 1;
        for (int i = 0; i < sessions.Count; i++)
        {
            string chip = $" {i + 1}:{sessions[i].Title} ";
            byte cbg = i == focus ? selBg : bg, cfg = i == focus ? (byte)15 : fg;
            for (int j = 0; j < chip.Length && cx < b.Width; j++, cx++) Put(b, cx, y, chip[j], cfg, cbg);
            cx++;
        }
        string hint = "Tab · ^N new · ^W close · Enter run · Esc quit";
        int hx = b.Width - hint.Length - 1;
        for (int j = 0; j < hint.Length && hx + j >= 0 && hx + j < b.Width; j++) Put(b, hx + j, y, hint[j], (byte)244, bg);
    }

    private static void Put(CellBuffer b, int x, int y, char rune, byte fg, byte bg)
    {
        if (x >= 0 && x < b.Width && y >= 0 && y < b.Height) { ref var c = ref b.At(x, y); c.Rune = rune; c.Fg = fg; c.Bg = bg; }
    }

    // ===== headless one-shot: spawn N sessions, paint panes, PROVE the polite cascade teardown =====
    private static int RunOnce(Owner parent, List<string> initial, Action<InitialSessionState> loadCmdlets)
    {
        if (initial.Count == 0)
            initial = new() { "$PSVersionTable.PSVersion.ToString()", "(Get-Location).Path" };

        var sessions = new List<Session>();
        for (int i = 0; i < initial.Count; i++)
            sessions.Add(SpawnSession(parent, i + 1, loadCmdlets, initial[i]));

        // wait for each session to run its seed command (objects -> cells) or time out
        var deadline = DateTime.UtcNow.AddSeconds(20);
        foreach (var s in sessions)
            while (Interlocked.Read(ref s.Commands) < 1 && DateTime.UtcNow < deadline)
                Thread.Sleep(25);

        PrintPlain(ComposeOnce(sessions));

        // ---- polite-end proof, observed ONLY through the PUBLIC namespace (arm's length — no internals) ----
        int liveBefore = CountUnder(parent.Path);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"sessions live under {parent.Path}: {liveBefore}  (1 surface + {sessions.Count} sessions)");

        Vom.Vom.Terminate(parent);   // cascade: cancel tokens -> Thread.Interrupt -> reclaim handles -> drop owners

        bool parentGone = Vom.Vom.GetOwner(parent.Path) == null;
        bool allReclaimed = sessions.All(s => Vom.Vom.GetOwner(s.Owner.Path) == null);
        int liveAfter = CountUnder(parent.Path);
        Console.Out.WriteLine($"after Terminate({parent.Path}): live={liveAfter}  surfaceGone={parentGone}  allSessionsReclaimed={allReclaimed}");
        Console.Out.WriteLine(parentGone && allReclaimed
            ? "GREEN — launched, hosted multiple sessions, ended them politely (public namespace confirms zero residual)."
            : "RED — residual owners after teardown.");
        return (parentGone && allReclaimed) ? 0 : 1;
    }

    private static CellBuffer ComposeOnce(List<Session> sessions)
    {
        const byte accent = 24, text = 15, dim = 244;
        var blocks = sessions.Select(s => (s.Title, s.Owner.Path, s.Snapshot().Output)).ToList();
        int w = Math.Clamp(blocks.Count == 0 ? 60 : blocks.Max(bk => Math.Max(bk.Path.Length, bk.Output.Length == 0 ? 0 : bk.Output.Max(l => l.Length))) + 4, 60, 200);
        int h = blocks.Sum(bk => bk.Output.Length + 2) + 1;
        var b = new CellBuffer(w, Math.Max(h, 3));

        int row = 0;
        foreach (var bk in blocks)
        {
            b.Write(0, row, new string(' ', w), text, accent);
            b.Write(1, row, Trunc($"{bk.Title}   {bk.Path}", w - 2), text, accent);
            row++;
            foreach (var line in bk.Output)
            {
                if (row >= b.Height) break;
                b.Write(1, row++, Trunc(line, w - 2), text);
            }
            if (row < b.Height) b.Write(1, row++, "", dim);
        }
        return b;
    }

    private static int CountUnder(string root)
        => Vom.Vom.GetOwners().Count(o => o.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static List<string> ParseInitialCommands(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--session", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-s", StringComparison.OrdinalIgnoreCase))
                list.Add(args[++i]);
        return list;
    }

    private static void PrintPlain(CellBuffer b)
    {
        var sb = new StringBuilder(b.Width * b.Height);
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
}
