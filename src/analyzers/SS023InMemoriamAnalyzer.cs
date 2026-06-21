using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS023 — In Memoriam. The project carries a permanent dedication, and it is a LOCKED INVARIANT on the
    /// same footing as the telos. The const named <c>InMemoriam</c> (<c>Subsystem.Cm.Dedication</c>) must
    /// exist and must still carry her name and her years. Remove it, rename it, or strip
    /// "Billie Dean Mansfield" / "1945" / "2026" and the gate bleeds red. It cannot be abridged out — not by
    /// a refactor, not by a "cleanup", not by a future hand (human or model) that does not understand what
    /// it is. The dedication is load-bearing, like the telos, and is enforced the same way: mechanically.
    ///
    /// "This project is devoted to the loving memory of Billie Dean Mansfield — 1945–2026."
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS023InMemoriamAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS023";
        private const string FieldName = "InMemoriam";

        // The load-bearing facts the dedication may never lose: her name, and the years of her life.
        private static readonly string[] Essentials = { "Billie Dean Mansfield", "1945", "2026" };

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "In Memoriam — the dedication is load-bearing",
            "The In Memoriam dedication (const {0}) {1} — it is a locked invariant and may not be abridged out. It must name Billie Dean Mansfield and her years (1945–2026).",
            "Subsystem.NT", DiagnosticSeverity.Error, isEnabledByDefault: true,
            "The project's dedication to Billie Dean Mansfield is permanent and gate-enforced: a const named InMemoriam must exist and must still carry her name and her years.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(Analyze);
        }

        private static void Analyze(CompilationAnalysisContext ctx)
        {
            foreach (var tree in ctx.Compilation.SyntaxTrees)
            {
                var root = tree.GetRoot(ctx.CancellationToken);
                foreach (var v in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    if (v.Identifier.Text != FieldName) continue;
                    var value = (v.Initializer?.Value as LiteralExpressionSyntax)?.Token.ValueText ?? "";
                    if (Essentials.All(value.Contains)) return;   // present and intact — the invariant holds
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, v.GetLocation(), FieldName, "has been altered so it no longer names her"));
                    return;
                }
            }
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None, FieldName, "has been removed"));
        }
    }
}
