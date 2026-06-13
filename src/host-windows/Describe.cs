using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Subsystem.Windows;

// `ss describe` — the impeccable self-description. A cold agent (or Scott, or a blind user) must be
// able to understand this system AND its source FROM THE BINARY, without re-ingesting the tree or
// reaching for memory. ss.exe embeds the CONTRACT (SystemCatalog.json — data, gated, can't rot); the
// architecture MAP is no longer an embedded doc — `ss describe --map` re-derives it LIVE from the source
// tree beside the binary (LiveMap), so it can never go stale. This is also the seed of the MCP tools/list
// surface ("one JSON, N consumers").
internal static class Describe
{
    public static int Run(string[] args)
    {
        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        bool map  = args.Any(a => a.Equals("--map",  StringComparison.OrdinalIgnoreCase));
        string path = ArgValue(args, "--path") ?? ArgValue(args, "-p") ?? "";   // empty => auto-discover the repo beside the binary

        var catalog = ReadResource("SystemCatalog.json");

        if (map)  return LiveMap.Run(path, catalog);     // LIVE, re-derived from source — never the stale doc
        if (json) { Console.WriteLine(catalog ?? "{}"); return 0; }

        PrintHuman(catalog);
        return 0;
    }

    // `--path <dir>` value lookup (for `ss describe --map --path <repo>`).
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void PrintHuman(string? catalogJson)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"ss — Subsystem Windows head  (v{v})");
        Console.WriteLine("An in-process, NT-Object-Manager-shaped CoreCLR + PowerShell 7.7 runtime.");
        Console.WriteLine("ONE object namespace · refcounted handles · per-owner quotas · deterministic cascade-kill.");
        Console.WriteLine("The registry (Cm) PROJECTS the namespace; the UI is a presenter; behaviors are verbs.");
        Console.WriteLine("\"It's NT, and it's a fractal.\"  Law: docs/CONTRACT.md.");
        Console.WriteLine();

        if (catalogJson != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(catalogJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Object)
                {
                    Console.WriteLine("COMPONENTS (the dependency DAG — upward refs are a compile error):");
                    foreach (var c in comps.EnumerateObject())
                    {
                        var name = c.Value.TryGetProperty("name", out var n) ? n.GetString() : "";
                        var deps = c.Value.TryGetProperty("dependsOn", out var d) && d.ValueKind == JsonValueKind.Array
                            ? string.Join(", ", d.EnumerateArray().Select(x => x.GetString()))
                            : "";
                        Console.WriteLine($"  {c.Name,-4} {name,-26}{(deps.Length > 0 ? "→ " + deps : "(root)")}");
                    }
                    Console.WriteLine();
                }

                if (root.TryGetProperty("verbs", out var verbs) && verbs.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.Array)
                {
                    Console.WriteLine("APPROVED VERBS (the closed method vocabulary):");
                    Console.WriteLine("  " + string.Join(" ", ap.EnumerateArray().Select(x => x.GetString())));
                    Console.WriteLine();
                }
            }
            catch { Console.WriteLine("(contract catalog embedded but unparseable)\n"); }
        }
        else
        {
            Console.WriteLine("(contract catalog not embedded in this build)\n");
        }

        var cmdlets = ProjectCmdletNames().OrderBy(s => s).ToArray();
        Console.WriteLine($"PROJECT CMDLETS loaded beside every pwsh built-in ({cmdlets.Length}):");
        foreach (var name in cmdlets) Console.WriteLine("  " + name);
        Console.WriteLine();

        Console.WriteLine("LEARN MORE FROM THE BINARY:");
        Console.WriteLine("  ss describe --map    architecture, subsystem by subsystem (the deep map)");
        Console.WriteLine("  ss describe --json   the contract, structured (for agents / MCP)");
        Console.WriteLine("  ss Get-Command       every command available here");
        Console.WriteLine("  ss help              usage");
    }

    private static IEnumerable<string> ProjectCmdletNames()
    {
        Type[] types;
        try { types = typeof(Subsystem.Tools.CodeContext.Cmdlets.GetCodeContextCmdlet).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        catch { yield break; }
        foreach (var t in types)
        {
            var attr = t.GetCustomAttribute<CmdletAttribute>();
            if (attr != null) yield return attr.VerbName + "-" + attr.NounName;
        }
    }

    private static string? ReadResource(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
