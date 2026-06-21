using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Subsystem.Windows;

// `ss contextualize --map` — the LIVING architecture map. Re-derived from the source tree on EVERY call, so it
// can never go stale. It replaces docs/ARCHITECTURE-MAP.md, a frozen 555-line prose dump that hardcoded
// absolute S:\ paths and rotted the moment the drive letter changed — the exact "stale truth held outside
// the live system" disease this whole project exists to cure.
//
// It scans the CURRENT WORKING DIR recursively by default (describe HERE, deeply) — never walks up, never
// auto-picks "the repo beside the exe"; `--path <dir>` targets another dir/drive, intentional only. It attributes every .cs file to a component via the
// embedded SystemCatalog folder map, lists each file's top-level type definitions, and surfaces the
// INTERNAL include edges (the file's `using Subsystem.*` directives) — "how the files include one another."
// This is the seed of the SQLite code index (the webcrawler-for-code): one walk today, a persisted,
// query-many-ways graph next.
internal static class LiveMap
{
    // A top-level type definition: optional attributes/modifiers, then the kind keyword + the name.
    private static readonly Regex TypeDecl = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:public |internal |private |protected |sealed |static |abstract |partial |unsafe |readonly |ref |file )*\b(record\s+struct|record\s+class|class|struct|interface|enum|record)\b\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex UsingDecl = new(@"^\s*using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled);

    public static int Run(string overridePath, string? catalogJson)
    {
        string? root = ResolveRoot(overridePath);
        if (root == null)
        {
            Console.Error.WriteLine(
                "ss contextualize --map: '" + (string.IsNullOrWhiteSpace(overridePath) ? Environment.CurrentDirectory : overridePath) +
                "' is not a directory. The default scans the CURRENT dir recursively; target elsewhere with --path <dir>.");
            return 2;
        }

        var components = ParseComponents(catalogJson, out var hostPaths);
        var files = EnumerateCs(root)
            .Select(p => Parse(root, p))
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int totalLines = files.Sum(f => f.Lines);
        Console.WriteLine($"ss context map  ·  {root}");
        Console.WriteLine($"{files.Count} files · {totalLines:n0} lines · components from the contract; tree + include edges read from source on this call");
        Console.WriteLine();

        // Bucket files by component (catalog order), then host, then unassigned.
        var byComponent = new Dictionary<string, List<FileNode>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var key = Attribute(f.Rel, components, hostPaths);
            if (!byComponent.TryGetValue(key, out var list)) byComponent[key] = list = new();
            list.Add(f);
        }

        var order = components.Select(c => c.Code).Concat(new[] { "(host)", "(unassigned)" }).Distinct().ToList();
        foreach (var key in order)
        {
            if (!byComponent.TryGetValue(key, out var list) || list.Count == 0) continue;
            var meta = components.FirstOrDefault(c => c.Code == key);
            string title = meta.Code != null
                ? $"{meta.Code} · {meta.Name} · {meta.Folder}/"
                : key;
            Console.WriteLine($"=== {title} · {list.Count} files · {list.Sum(f => f.Lines):n0} lines ===");
            foreach (var f in list)
            {
                string types = f.Types.Count > 0 ? string.Join(", ", f.Types) : "(no top-level type)";
                Console.WriteLine($"  {f.Rel,-46} {types}");
                if (f.Internal.Count > 0)
                    Console.WriteLine($"  {"",-46} ⇐ {string.Join(", ", f.Internal)}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("(⇐ = this file's internal `using Subsystem.*` edges — what it includes from the rest of the tree.)");
        return 0;
    }

    private sealed class FileNode
    {
        public string Rel = "";
        public int Lines;
        public List<string> Types = new();
        public List<string> Internal = new();   // distinct `using Subsystem.*` namespaces, the include edges
    }

    private static FileNode? Parse(string root, string path)
    {
        try
        {
            var rel = NormRel(root, path);
            var node = new FileNode { Rel = rel };
            var internalUsings = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(path))
            {
                node.Lines++;
                var line = raw;
                var u = UsingDecl.Match(line);
                if (u.Success)
                {
                    var ns = u.Groups[1].Value;
                    if (ns.StartsWith("Subsystem", StringComparison.Ordinal)) internalUsings.Add(ns);
                    continue;
                }
                var t = TypeDecl.Match(line);
                if (t.Success)
                {
                    var name = t.Groups[2].Value;
                    if (!node.Types.Contains(name)) node.Types.Add(name);
                }
            }
            node.Internal = internalUsings.ToList();
            return node;
        }
        catch { return null; }   // never let one unreadable file abort the map
    }

    // --- root + attribution ---

    private static string? ResolveRoot(string overridePath)
    {
        // Default = the CURRENT WORKING DIR, scanned recursively (EnumerateCs walks it downward). Never walk
        // UP the tree, never auto-pick "the repo beside the exe" — `-p`/`--path` points at another dir or
        // drive, and that is the ONLY way to look anywhere but here. (Scott: scan here, deeply; elsewhere only on purpose.)
        var target = string.IsNullOrWhiteSpace(overridePath) ? Environment.CurrentDirectory : overridePath;
        return Directory.Exists(target) ? Path.GetFullPath(target) : null;
    }

    private static IEnumerable<string> EnumerateCs(string root)
    {
        var src = Path.Combine(root, "src");
        var baseDir = Directory.Exists(src) ? src : root;
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(baseDir, "*.cs", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var p in all)
        {
            var n = p.Replace('\\', '/');
            if (n.Contains("/obj/") || n.Contains("/bin/") || n.Contains("/vendor/")) continue;
            yield return p;
        }
    }

    private static string NormRel(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        // strip a leading "src/" so component folders (src/runspace/Vom) read as runspace/Vom in the listing
        return rel.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ? rel.Substring(4) : rel;
    }

    private static string Attribute(string rel, List<(string Code, string Name, string Folder)> comps, List<string> hostPaths)
    {
        // rel has the leading "src/" stripped; component folders carry it — compare on the suffix.
        foreach (var c in comps)
        {
            var folder = c.Folder.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ? c.Folder.Substring(4) : c.Folder;
            if (folder.Length > 0 && (rel.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
                                      || rel.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                return c.Code;
        }
        foreach (var h in hostPaths)
        {
            var hp = h.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ? h.Substring(4) : h;
            if (hp.Length > 0 && rel.IndexOf(hp, StringComparison.OrdinalIgnoreCase) >= 0) return "(host)";
        }
        return "(unassigned)";
    }

    private static List<(string Code, string Name, string Folder)> ParseComponents(string? json, out List<string> hostPaths)
    {
        var list = new List<(string, string, string)>();
        hostPaths = new List<string>();
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Object)
                foreach (var c in comps.EnumerateObject())
                {
                    var name = c.Value.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    var folder = c.Value.TryGetProperty("folder", out var f) ? (f.GetString() ?? "") : "";
                    list.Add((c.Name, name, folder.Replace('\\', '/').TrimEnd('/')));
                }
            if (root.TryGetProperty("hostPaths", out var hp) && hp.ValueKind == JsonValueKind.Array)
                foreach (var h in hp.EnumerateArray())
                    if (h.GetString() is string s) hostPaths.Add(s.Replace('\\', '/').TrimEnd('/'));
        }
        catch { }
        return list;
    }
}
