using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Subsystem.Windows;

// `ss check`, IN-PROCESS — no dotnet, no MSBuild, no pre-published checker. ss.exe already carries Roslyn
// (Microsoft.CodeAnalysis.CSharp, the version the PowerShell SDK ships) and its OWN source, including
// src/analyzers/*.cs. So the gate compiles the analyzer suite in-proc from that carried source with the
// bundled Roslyn, then runs it over an in-proc CSharpCompilation of the runspace tree — the ouroboros
// gating itself from itself, exactly as SelfBuild compiles the head. References come from ss.exe's own
// bundle (a single-file TPA list is empty), so the platform-agnostic core binds; the net11.0-android-only
// files leave unresolved types (CS errors) — analyzers run regardless, and the syntactic suite is exact.
// Findings feed the same SS-BASELINE.txt ratchet the published checker used.
internal static class InProcGate
{
    public static int Run(string[] args)
    {
        bool list = Build.HasFlag(args, "--list") || Build.HasFlag(args, "-l");
        bool gate = Build.HasFlag(args, "--gate");
        bool writeBaseline = Build.HasFlag(args, "--write-baseline");

        var root = Build.ResolveSource(Build.PathArg(args));
        if (root == null) { Console.Error.WriteLine("ss check: no source (no repo beside the exe, no embedded source, no --path)."); return 2; }

        var refs = BundleReferences();
        if (refs.Count == 0) { Console.Error.WriteLine("ss check: this exe carries no bundle — cannot reference assemblies in-proc."); return 2; }

        var (analyzers, loadErrors) = LoadAnalyzers(root, refs);
        if (analyzers.IsDefaultOrEmpty)
        {
            Console.Error.WriteLine("ss check: could not build the analyzer suite in-proc:");
            foreach (var e in (loadErrors ?? Array.Empty<string>()).Take(20)) Console.Error.WriteLine("  " + e);
            return 2;
        }

        if (list) return ListMode(analyzers);

        Console.WriteLine($"ss check: in-proc gate — {analyzers.Length} analyzers over {root}");
        var findings = RunSuite(root, refs, analyzers);

        if (gate || writeBaseline)
        {
            int code = GateMode(findings, Path.Combine(root, "src", "analyzers", "SS-BASELINE.txt"), writeBaseline, out var gateLines);
            if (gate)
            {
                SmokeLog.Record(root, "check --gate", code == 0, gateLines);
                if (code != 0) return Build.GateVerdict(code, Build.IsOverride(args));
            }
            return code;
        }
        return CheckMode(findings);
    }

    // Reference set = every managed assembly ss.exe carries in its own bundle (a single-file TPA list is
    // empty, so read the bundle the way SelfBuild does): the .NET 11 BCL + PowerShell SDK + Roslyn + Sqlite.
    // EXCLUDE desktop/COM assemblies whose ROOT namespace collides with a Microsoft.CodeAnalysis type the
    // analyzer source uses unqualified: 'Accessibility' is both a .NET Windows assembly AND the Roslyn enum,
    // so carrying Accessibility.dll makes `Accessibility.Public` ambiguous (CS0234). The published checker
    // compiles against netstandard2.0 and never sees it; the analyzers (and the runspace) need none of these.
    static readonly HashSet<string> CollidingAssemblies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Accessibility" };

    static List<MetadataReference> BundleReferences()
    {
        var exe = File.ReadAllBytes(Environment.ProcessPath!);
        var manifest = SelfBundle.Read(exe);
        return SelfBundle.ManagedAssemblies(manifest)
            .Where(a => !CollidingAssemblies.Contains(Path.GetFileNameWithoutExtension(a.Name)))
            .Select(a => (MetadataReference)MetadataReference.CreateFromImage(a.Image))
            .ToList();
    }

    // Compile src/analyzers/*.cs in-proc with the bundled Roslyn, emit to memory, load it, and instantiate
    // every DiagnosticAnalyzer — the suite, built from the source the binary carries (no pre-published dll).
    static (ImmutableArray<DiagnosticAnalyzer> Analyzers, string[]? Errors) LoadAnalyzers(string root, List<MetadataReference> refs)
    {
        var dir = Path.Combine(root, "src", "analyzers");
        if (!Directory.Exists(dir)) return (ImmutableArray<DiagnosticAnalyzer>.Empty, new[] { "no analyzer source at " + dir });
        var trees = SourceTrees(dir);
        if (trees.Count == 0) return (ImmutableArray<DiagnosticAnalyzer>.Empty, new[] { "no analyzer .cs under " + dir });

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable);
        var comp = CSharpCompilation.Create("Subsystem.Analyzers.InProc", trees, refs, options);
        using var pe = new MemoryStream();
        var emit = comp.Emit(pe);
        if (!emit.Success)
            return (ImmutableArray<DiagnosticAnalyzer>.Empty,
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray());

        var asm = Assembly.Load(pe.ToArray());
        var made = asm.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();
        return (made, null);
    }

    // The full suite over the runspace tree (the published checker gated src/runspace/Subsystem.csproj), PLUS
    // the host-windows syntax guards (SS020/SS021) — the heads are not in the runspace project, so without
    // that second pass a hardcoded model/prompt in a head escapes the gate.
    static List<Diagnostic> RunSuite(string root, List<MetadataReference> refs, ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var trees = SourceTrees(Path.Combine(root, "src", "runspace"));
        var comp = CSharpCompilation.Create("subsystem-runspace-scan", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var opts = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(CatalogText(root)));
        var diags = comp.WithAnalyzers(analyzers, opts).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

        var ss = diags.Where(d => d.Id.StartsWith("SS", StringComparison.Ordinal)).ToList();
        ss.AddRange(HostScan(root, refs, analyzers));
        return ss;
    }

    // Syntax-only host-windows pass: SS020 (hardcoded model/prompt) + SS021 (Streisand comment) over the
    // head's own tree — CS errors from missing refs don't matter, the rules read syntax.
    static List<Diagnostic> HostScan(string root, List<MetadataReference> refs, ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var hostDir = Path.Combine(root, "src", "host-windows");
        if (!Directory.Exists(hostDir)) return new List<Diagnostic>();
        var trees = SourceTrees(hostDir);
        if (trees.Count == 0) return new List<Diagnostic>();
        var hostAnalyzers = analyzers
            .Where(a => a.GetType().Name is "SS020ModelPromptHardcodeAnalyzer" or "SS019StreisandAnalyzer")
            .ToImmutableArray();
        if (hostAnalyzers.IsEmpty) return new List<Diagnostic>();
        var comp = CSharpCompilation.Create("hostwin-scan", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return comp.WithAnalyzers(hostAnalyzers).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .Where(d => d.Id.StartsWith("SS", StringComparison.Ordinal)).ToList();
    }

    static List<SyntaxTree> SourceTrees(string dir)
    {
        var trees = new List<SyntaxTree>();
        if (!Directory.Exists(dir)) return trees;
        var sep = Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}bin{sep}")) continue;
            try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)); }
            catch (Exception) { continue; }
        }
        return trees;
    }

    // The catalog the naming/structure rules read (SS000/SS011-017) — handed in as the AdditionalFile they
    // look for by name, so the gate is the SAME live SystemCatalog.json the build's AdditionalFiles supply.
    static AdditionalText CatalogText(string root)
    {
        var path = Path.Combine(root, "src", "analyzers", "SystemCatalog.json");
        var content = File.Exists(path) ? File.ReadAllText(path) : "";
        return new InMemoryAdditionalText(path, content);
    }

    sealed class InMemoryAdditionalText : AdditionalText
    {
        readonly string _path;
        readonly SourceText _text;
        public InMemoryAdditionalText(string path, string content) { _path = path; _text = SourceText.From(content); }
        public override string Path => _path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    // --gate: fail on any SS finding NOT in the baseline (the ratchet — new code bleeds red, legacy tracked).
    // --write-baseline: regenerate from the current tree. Keys are "SSxxx|file|message" WITHOUT line numbers,
    // so unrelated edits don't false-positive; duplicate keys are counted. gateLines mirrors the printed
    // summary so the smoke-log carries the real numbers (DERIVED), not a paraphrase.
    static int GateMode(IReadOnlyList<Diagnostic> ss, string baselinePath, bool write, out List<string> gateLines)
    {
        gateLines = new List<string>();
        var keys = ss.Where(d => d.Id.StartsWith("SS", StringComparison.Ordinal))
                     .Select(d => $"{d.Id}|{Path.GetFileName(d.Location.GetLineSpan().Path)}|{d.GetMessage()}")
                     .OrderBy(k => k, StringComparer.Ordinal)
                     .ToList();

        if (write)
        {
            File.WriteAllLines(baselinePath, keys);
            var w = $"gate: baseline written — {keys.Count} entries -> {baselinePath}";
            Console.WriteLine(w); gateLines.Add(w);
            return 0;
        }

        var baseline = File.Exists(baselinePath) ? File.ReadAllLines(baselinePath).ToList() : new List<string>();
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in baseline) budget[k] = budget.TryGetValue(k, out var n) ? n + 1 : 1;

        var fresh = new List<string>();
        foreach (var k in keys)
        {
            if (budget.TryGetValue(k, out var n) && n > 0) budget[k] = n - 1;
            else fresh.Add(k);
        }

        int retired = budget.Values.Where(v => v > 0).Sum();
        var summary = $"gate: {keys.Count} findings; baseline {baseline.Count}; new {fresh.Count}; retired {retired}";
        Console.WriteLine(summary); gateLines.Add(summary);
        if (retired > 0)
        {
            var r = "gate: baseline entries no longer firing — shrink the baseline (--write-baseline) and commit the diff.";
            Console.WriteLine(r); gateLines.Add(r);
        }
        if (fresh.Count > 0)
        {
            Console.WriteLine("\ngate: NEW violations (not in baseline) — the gate bleeds red here:");
            gateLines.Add("gate: NEW violations (not in baseline) — the gate bleeds red here:");
            foreach (var k in fresh) Console.WriteLine("  " + k);
            return 1;
        }
        var green = "gate: GREEN — no new violations.";
        Console.WriteLine(green); gateLines.Add(green);
        return 0;
    }

    // Default `ss check`: the full finding roster, grouped by rule.
    static int CheckMode(IReadOnlyList<Diagnostic> diags)
    {
        var ss = diags.Where(d => d.Id.StartsWith("SS", StringComparison.Ordinal))
                      .OrderBy(d => d.Id)
                      .ThenBy(d => d.Location.GetLineSpan().Path)
                      .ToList();
        foreach (var group in ss.GroupBy(d => d.Id).OrderBy(g => g.Key))
        {
            Console.WriteLine($"\n=== {group.Key} ({group.Count()}) — {group.First().Descriptor.Title} ===");
            foreach (var d in group)
            {
                var lsp = d.Location.GetLineSpan();
                Console.WriteLine($"  {Path.GetFileName(lsp.Path)}:{lsp.StartLinePosition.Line + 1}  {d.GetMessage()}");
            }
        }
        Console.WriteLine($"\n--- {ss.Count} findings across {ss.Select(d => d.Location.GetLineSpan().Path).Distinct().Count()} files ---");
        return 0;
    }

    // --list: the analyzer roster (id + what it enforces). The count IS the suite the gate runs.
    static int ListMode(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var rules = analyzers.SelectMany(a => a.SupportedDiagnostics)
            .GroupBy(d => d.Id).Select(g => g.First())
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"Subsystem analyzers — {analyzers.Length} loaded, {rules.Count} rules (in-proc, no dotnet — this IS the gate's suite):");
        foreach (var d in rules)
            Console.WriteLine($"  {d.Id}  [{d.DefaultSeverity}]  {d.Title}");
        return 0;
    }
}
