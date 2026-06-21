using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Subsystem.Windows;

// `ss refs <Symbol> [path]` — the grep-KILLER. A Roslyn SYNTAX search (not a text scan): it parses every .cs in
// the tree and reports DECLARATIONS (type/method/property/delegate/enum named Symbol) and REFERENCES (identifier
// usages) — and it knows a real usage from one inside a string or a comment, which grep never can. Default path =
// CWD, recursive; -p targets elsewhere. (v1 = syntax-level; the semantic SymbolFinder pass that disambiguates
// overloads/namespaces is the v2 — but this already beats grep because it's structural.)
internal static class Refs
{
    public static int Run(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("-")).ToArray();
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("usage: ss refs <Symbol> [path]   (path defaults to the current dir, recursive)");
            return 2;
        }
        var symbol = positional[0];
        var root = Path.GetFullPath(positional.Length > 1 ? positional[1] : Directory.GetCurrentDirectory());
        if (!Directory.Exists(root)) { Console.Error.WriteLine($"ss refs: '{root}' is not a directory."); return 2; }

        var decls = new List<(string Rel, int Line, string Kind)>();
        var refs  = new List<(string Rel, int Line, string Ctx)>();

        foreach (var file in EnumerateCs(root))
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var tree = CSharpSyntaxTree.ParseText(text);
            var node = tree.GetRoot();
            var rel  = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = text.Replace("\r", "").Split('\n');

            foreach (var n in node.DescendantNodes())
            {
                string? name = n switch
                {
                    BaseTypeDeclarationSyntax t => t.Identifier.Text,
                    MethodDeclarationSyntax m   => m.Identifier.Text,
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    DelegateDeclarationSyntax d => d.Identifier.Text,
                    EnumMemberDeclarationSyntax e => e.Identifier.Text,
                    _ => null,
                };
                if (name == symbol)
                    decls.Add((rel, tree.GetLineSpan(n.Span).StartLinePosition.Line + 1,
                               n.Kind().ToString().Replace("Declaration", "").ToLowerInvariant()));
            }

            foreach (var id in node.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (id.Identifier.Text == symbol)
                {
                    int li = tree.GetLineSpan(id.Span).StartLinePosition.Line;
                    var ctx = li < lines.Length ? lines[li].Trim() : "";
                    refs.Add((rel, li + 1, ctx.Length > 90 ? ctx.Substring(0, 90) + "…" : ctx));
                }
        }

        Console.WriteLine($"ss refs · {symbol}  ·  {decls.Count} declaration(s), {refs.Count} reference(s)  (Roslyn syntax, not text)");
        if (decls.Count > 0)
        {
            Console.WriteLine("  DECLARED:");
            foreach (var d in decls.OrderBy(d => d.Rel)) Console.WriteLine($"    {d.Rel}:{d.Line}  ({d.Kind})");
        }
        if (refs.Count > 0)
        {
            Console.WriteLine("  REFERENCED:");
            foreach (var g in refs.GroupBy(r => r.Rel).OrderBy(g => g.Key))
            {
                Console.WriteLine($"    {g.Key}");
                foreach (var r in g.Take(15)) Console.WriteLine($"      :{r.Line,-5} {r.Ctx}");
                if (g.Count() > 15) Console.WriteLine($"      … +{g.Count() - 15} more in this file");
            }
        }
        return 0;
    }

    private static IEnumerable<string> EnumerateCs(string root)
    {
        // IgnoreInaccessible: a walk from a drive root (e.g. S:\) steps into protected dirs like
        // System Volume Information / $RECYCLE.BIN. EnumerateFiles is LAZY, so an UnauthorizedAccessException
        // surfaces mid-iteration and would escape an outer try; this option makes the walk skip such dirs
        // instead of throwing. (Request #46: ss refs crashed with UnauthorizedAccessException from a drive root.)
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(root, "*.cs", opts); }
        catch (UnauthorizedAccessException) { yield break; } // root itself unreadable — nothing to walk
        catch (IOException) { yield break; }                 // root vanished / device error — nothing to walk
        foreach (var p in all)
        {
            var n = p.Replace('\\', '/');
            if (n.Contains("/obj/") || n.Contains("/bin/") || n.Contains("/vendor/")) continue;
            yield return p;
        }
    }
}
