using System;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Subsystem.Vom;

namespace Subsystem.Shell.Cell;

// ss repl — THE FLIP (#flip), one-shot: a pwsh command rendered as CELLS, never ANSI.
//
// The session runs IN-PROCESS; its pipeline OBJECTS are captured and formatted to plain text (Out-String, no
// SGR), and that text is painted straight into a CellBuffer. No ConPTY, no second console, no ANSI for native
// pwsh — so the "pwsh in pwsh in WT" nesting failure cannot exist. The window is a real VOM owner
// (\Sessions\Repl); its framebuffer is a registered handle (the ouroboros), and Terminate cascades on exit —
// the session lives and dies with it.
//
//   ss repl <command...>     run one command, paint its object output as cells to stdout (the testable flip)
//
// The VT parser survives ONLY for foreign programs (git/vim/ssh) — "tui mode" — never for native pwsh.
// `loadCmdlets` is the per-head cmdlet seam (Windows -> Shim.LoadProjectCmdlets; Android -> its host loader)
// so this shared file never binds to a head-specific type.
internal static class Repl
{
    private const string OwnerPath = "\\Sessions\\Repl";

    public static int Run(string[] args, Action<InitialSessionState> loadCmdlets)
    {
        string command = string.Join(' ', args).Trim();
        if (command.Length == 0)
        {
            Console.Error.WriteLine("usage: ss repl <command>   — runs pwsh in-proc and paints its OBJECT output as cells (no ANSI)");
            return 2;
        }

        // Dogfood the kernel: the repl window is itself an owned object in the namespace it renders.
        var owner = Vom.Vom.CreateOwner(OwnerPath);
        try
        {
            string text = Invoke(command, loadCmdlets);
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

            int w = Math.Clamp(lines.Length == 0 ? 60 : lines.Max(l => l.Length) + 2, 60, 400);
            int h = lines.Length + 3;
            var buf = new CellBuffer(w, h);
            Vom.Vom.Register(owner, "CellBuffer", buf, name: "Framebuffer");

            Compose(buf, command, lines);
            PrintPlain(buf);
            return 0;
        }
        finally
        {
            Vom.Vom.Terminate(owner);   // cascade reclaim — the \Sessions\Repl owner + its framebuffer handle
        }
    }

    // The session: an IN-PROCESS runspace (the SAME loader the shell + MCP use), command pipeline captured as
    // OBJECTS, formatted to plain text via Out-String — text, never ANSI. This is the flip's whole point.
    private static string Invoke(string command, Action<InitialSessionState> loadCmdlets)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        loadCmdlets(iss);                              // the dogfood cmdlet surface, beside the built-ins
        using var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(command).AddCommand("Out-String");   // pipeline OBJECTS -> text (no Out-Default, no SGR)
        var sb = new StringBuilder();
        try { foreach (var o in ps.Invoke()) sb.Append(o?.ToString()); }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
        foreach (var e in ps.Streams.Error) sb.Append("\n[error] ").Append(e.ToString());
        return sb.ToString();
    }

    // Paint the captured text into the cell grid — a title row, then the command's object output as cells.
    private static void Compose(CellBuffer b, string command, string[] lines)
    {
        b.Clear(Cell.Empty);
        const byte accent = 24, text = 15, dim = 244;

        b.Write(0, 0, new string(' ', b.Width), text, accent);
        b.Write(1, 0, Trunc("PS> " + command, b.Width - 2), text, accent);

        for (int i = 0; i < lines.Length && i + 2 < b.Height; i++)
            b.Write(0, i + 2, Trunc(lines[i], b.Width - 1), text);

        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0))
            b.Write(0, 2, "(no output)", dim);
    }

    // Cells -> stdout as plain text (the --once floor; the VtRenderer drives the interactive surface).
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
}
