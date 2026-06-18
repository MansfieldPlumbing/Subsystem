using System;
using System.IO;
using System.Linq;

namespace Subsystem.Windows;

// `ss onboard` — the one-shot alignment package. A cold agent (or Scott, or a fresh session) runs this
// ALONE and is fully oriented: the WHY (telos), the laws (invariants + the analyzer gate), the SETTLED
// (locked decisions), the conventions, where-we-are/next, and the live contract. It re-narrates nothing —
// it PROJECTS the canonical sources (README's telos, CLAUDE.md's doctrine, the ledgers) so the brief can
// never drift from them, and delegates the architecture/contract to `contextualize` (one truth).
// Sources are read from the repo (discovered beside the binary or the cwd, or via --path); if one is
// missing because the binary was dropped elsewhere, that section degrades to a pointer rather than failing.
internal static class Onboard
{
    public static int Run(string[] args)
    {
        string explicitPath = ReadArg(args, "--path") ?? ReadArg(args, "-p") ?? "";
        string repo = ResolveRepo(explicitPath);

        Console.WriteLine("ss onboard — the complete alignment package, projected from the canonical sources.");
        Console.WriteLine("Run this first on a cold session; then `ss contextualize --map` and `ss check --list` for depth.");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        // WHY — the telos (locked; always projected from README, never paraphrased).
        WriteSection("WHY — THE TELOS",
            ReadSection(Path.Combine(repo, "README.md"), "## The telos"),
            "README.md `## The telos` not found beside the binary — run from the repo or pass --path.");

        // LAWS — the invariants (the un-analyzed ones); the analyzed ones live in the gate.
        WriteSection("LAWS — THE INVARIANTS",
            ReadSection(Path.Combine(repo, "CLAUDE.md"), "**The invariants"),
            "CLAUDE.md invariants not found — run from the repo or pass --path.");
        Console.WriteLine("  Analyzer-enforced laws: `ss check --list` (the gate; fail-closed on any new violation).");
        Console.WriteLine();

        // SETTLED — locked decisions, so the session does not re-litigate.
        WriteSection("SETTLED — DO NOT RE-LITIGATE",
            ReadSection(Path.Combine(repo, "CLAUDE.md"), "**Already-decided"),
            "CLAUDE.md locked decisions not found — run from the repo or pass --path.");

        // CONVENTIONS — how work is done here.
        WriteSection("CONVENTIONS",
            ReadSection(Path.Combine(repo, "CLAUDE.md"), "## How you work here"),
            "CLAUDE.md conventions not found — run from the repo or pass --path.");

        // STATE / DIRECTION — the ledgers (written BY the binary; the live where-we-are and what's-next).
        WriteSection("STATE — THE GATE",
            ReadTail(Path.Combine(repo, "smoketest-log.md"), 8),
            "smoketest-log.md not found — run `ss check --gate` to write it.");
        WriteSection("WHERE-WE-ARE / NEXT",
            ReadTail(Path.Combine(repo, "ss-log.md"), 18),
            "ss-log.md not found — run from the repo or pass --path.");

        // CONTRACT — delegate to contextualize (one truth; no duplicated description).
        Console.WriteLine("CONTRACT — components · DAG · verbs (live, from the binary):");
        Console.WriteLine(new string('-', 78));
        return Contextualize.Run(Array.Empty<string>());
    }

    private static void WriteSection(string title, string? body, string missing)
    {
        Console.WriteLine(title + ":");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine(string.IsNullOrWhiteSpace(body) ? "  (" + missing + ")" : body!.TrimEnd());
        Console.WriteLine();
    }

    // Auto-discover the repo: honor --path, else walk up from the cwd and the binary's directory for a
    // folder holding both README.md and src/ (the repo-root markers). Mirrors how the map finds source.
    private static string ResolveRepo(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath)) return explicitPath;
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir != null)
            {
                if (IsRepo(dir.FullName)) return dir.FullName;
                var beside = Path.Combine(dir.FullName, "subsystem");  // a `subsystem` repo beside the exe/cwd (S:\ss.exe -> S:\subsystem)
                if (IsRepo(beside)) return beside;
                dir = dir.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }

    private static bool IsRepo(string dir) =>
        File.Exists(Path.Combine(dir, "README.md")) && Directory.Exists(Path.Combine(dir, "src"));

    // Read a markdown section: from the line beginning with `header` up to the next top-level header.
    private static string? ReadSection(string file, string header)
    {
        if (!File.Exists(file)) return null;
        var lines = File.ReadAllLines(file);
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var body = new System.Text.StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            if (i > start && (lines[i].StartsWith("## ") || lines[i].StartsWith("# "))) break;
            body.AppendLine(lines[i]);
        }
        return body.ToString();
    }

    private static string? ReadTail(string file, int count)
    {
        if (!File.Exists(file)) return null;
        var lines = File.ReadAllLines(file);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count)));
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
