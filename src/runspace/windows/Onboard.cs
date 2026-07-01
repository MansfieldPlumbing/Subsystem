using System;
using System.IO;
using System.Linq;
using System.Reflection;

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

        Console.WriteLine();
        Console.WriteLine("ss onboard — the complete alignment package, projected from the canonical sources.");
        Console.WriteLine("Run this first on a cold start; then `ss contextualize --map` and `ss check --list` for depth.");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        // WHY — the telos (locked; always projected from README, never paraphrased).
        WriteSection("WHY — THE TELOS",
            ReadSection(Path.Combine(repo, "README.md"), "## The telos"),
            "README.md `## The telos` not found beside the binary — run from the repo or pass --path.");

        // ORIENTATION — the one idea (COLD-START canon; embedded so a cold clone still gets the thesis).
        WriteSection("ORIENTATION — THE ONE IDEA",
            ReadCanon(repo, "COLD-START.md", "## The one idea"),
            "COLD-START.md not found on disk or embedded.");

        string claudePath = ResolveClaudePath(repo);

        // LAWS — the invariants. The umbrella CLAUDE.md when present; else the embedded CONTRACT canon, so a
        // clone (where the out-of-repo CLAUDE.md is absent) still gets the laws. The analyzed ones live in the gate.
        WriteSection("LAWS — THE INVARIANTS",
            ReadSection(claudePath, "**The invariants", "## The locked invariants")
                ?? ReadCanon(repo, "CONTRACT.md", "## The 10 invariants"),
            "CLAUDE.md / CONTRACT.md invariants not found.");
        Console.WriteLine("  Analyzer-enforced laws: `ss check --list` (the gate; fail-closed on any new violation).");
        Console.WriteLine();

        // SETTLED — locked decisions, so the session does not re-litigate. CLAUDE.md, else the CONTRACT standing rules.
        WriteSection("SETTLED — DO NOT RE-LITIGATE",
            ReadSection(claudePath, "**Already-decided", "## How we work now")
                ?? ReadCanon(repo, "CONTRACT.md", "## Standing rules"),
            "CLAUDE.md / CONTRACT.md locked decisions not found.");

        // CONVENTIONS — how work is done here. CLAUDE.md, else the COLD-START how-to-work (canon).
        WriteSection("CONVENTIONS",
            ReadSection(claudePath, "## How you work here", "## Conventions")
                ?? ReadCanon(repo, "COLD-START.md", "## Who you're working with"),
            "CLAUDE.md / COLD-START.md conventions not found.");

        // STATE / DIRECTION — the ledgers (written BY the binary; the live where-we-are and what's-next).
        WriteSection("STATE — THE GATE",
            ReadTail(Path.Combine(repo, "smoketest-log.md"), 8),
            "smoketest-log.md not found — run `ss check --gate` to write it.");
        WriteSection("WHERE-WE-ARE / NEXT",
            ReadTail(Path.Combine(repo, "ss-log.md"), 18),
            "ss-log.md not found — run from the repo or pass --path.");

        // FILES — the full manifest, mandatory. Seeing that a file EXISTS must never depend on a session
        // deciding to go look for it (the keyhole fix, CRQ178). Projected by LiveMap — one scanner home.
        WriteSection("FILES — THE FULL MANIFEST",
            IsRepo(repo) ? LiveMap.ProjectManifest(repo) : null,
            "repo root not resolved — run from the repo or pass --path.");

        // CONTRACT — delegate to contextualize (one truth; no duplicated description).
        Console.WriteLine("CONTRACT — components · DAG · verbs (live, from the binary):");
        Console.WriteLine(new string('-', 78));
        int rc = Contextualize.Run(Array.Empty<string>());

        // In Memoriam — the solemn advisory, moved to the FOOT at Scott's direction so it closes the brief.
        // A locked invariant (SS023); the one source of truth is Subsystem.Cm.Dedication.InMemoriam, so it can
        // never drift or be abridged out — repositioned only, never shortened.
        Console.WriteLine();
        Console.WriteLine(Subsystem.Cm.Dedication.InMemoriam);
        Console.WriteLine();
        return rc;
    }

    // A canon section (CONTRACT.md / COLD-START.md): the live copy in docs/ wins; else the copy embedded in the
    // binary, so onboard projects doctrine even on a clone where docs/ is gitignored and absent (the INC131 gap).
    private static string? ReadCanon(string repo, string file, string header)
    {
        var disk = ReadSection(Path.Combine(repo, "docs", file), header);
        if (!string.IsNullOrWhiteSpace(disk)) return disk;
        return SectionOf(EmbeddedDoc(file), header);
    }

    private static string? EmbeddedDoc(string file)
    {
        var asm = typeof(Onboard).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ReadSection over an in-memory string (the embedded copy) — mirrors the on-disk ReadSection.
    private static string? SectionOf(string? text, string header)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(header, StringComparison.OrdinalIgnoreCase) ||
                                                l.Contains(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var body = new System.Text.StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            if (i > start && (lines[i].StartsWith("## ") || lines[i].StartsWith("# "))) break;
            body.AppendLine(lines[i]);
        }
        return body.ToString();
    }

    private static void WriteSection(string title, string? body, string missing)
    {
        Console.WriteLine(title + ":");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine(string.IsNullOrWhiteSpace(body) ? "  (" + missing + ")" : body!.TrimEnd());
        Console.WriteLine();
    }

    // Resolve the repo the doctrine way: --path, else the WORKING DIR, else the binary's own dir — never a
    // walk UP the tree, never a probe beside it (Scott, 2026-07-01: "my thing is and always has been working
    // dir and recursively down into the dir tree"; LiveMap.ResolveRoot is the same rule). A miss degrades
    // each section to its pointer rather than failing.
    private static string ResolveRepo(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath)) return explicitPath;
        string cwd = Directory.GetCurrentDirectory();
        if (IsRepo(cwd)) return cwd;
        if (IsRepo(AppContext.BaseDirectory)) return AppContext.BaseDirectory;
        return cwd;
    }

    private static bool IsRepo(string dir) =>
        File.Exists(Path.Combine(dir, "README.md")) && Directory.Exists(Path.Combine(dir, "src"));

    private static string ResolveClaudePath(string repo)
    {
        string localClaude = Path.Combine(repo, "CLAUDE.md");
        if (File.Exists(localClaude))
        {
            string content = File.ReadAllText(localClaude);
            if (content.Contains("Orientation lives at the umbrella", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("branch worktree", StringComparison.OrdinalIgnoreCase))
            {
                string parentClaude = Path.Combine(repo, "..", "CLAUDE.md");
                if (File.Exists(parentClaude)) return parentClaude;
            }
            return localClaude;
        }
        string fallbackClaude = Path.Combine(repo, "..", "CLAUDE.md");
        if (File.Exists(fallbackClaude)) return fallbackClaude;
        return localClaude;
    }

    // Read a markdown section: from the line matching any of the headers up to the next top-level header.
    private static string? ReadSection(string file, params string[] headers)
    {
        if (!File.Exists(file)) return null;
        var lines = File.ReadAllLines(file);
        int start = -1;
        foreach (var header in headers)
        {
            start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(header, StringComparison.OrdinalIgnoreCase) ||
                                                l.Contains(header, StringComparison.OrdinalIgnoreCase));
            if (start >= 0) break;
        }
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
