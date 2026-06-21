using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS024 — a retired word in a STRING LITERAL. The companion to SS017 (which refuses the same kind of word
    /// in IDENTIFIERS): a rename that stops at the identifier layer is a Streisand fractal — the word reappears
    /// one layer down as a database name, a table name, an SQL fragment, or any other string literal. This
    /// closes that door. The list is DATA (SystemCatalog.json banned.stringLiterals), deliberately NARROW — only
    /// genuinely-retired tokens, never the whole alwaysFlag set (that would scrape every string). Case-
    /// insensitive substring, so a renamed-away word cannot smuggle itself back in any casing or affix.
    /// Census-pending ratchet: existing hits ride SS-BASELINE.txt; a NEW occurrence bleeds red at the gate.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS024BannedStringLiteralAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS024";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Retired word in a string literal",
            "string literal contains the retired word '{0}' — a renamed-away word must not survive one layer down as a db/table/SQL string (SystemCatalog.json banned.stringLiterals)",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "A word the contract retired must not reappear in a string literal (db name, table, SQL). The string-scoped companion to SS017's identifier ban. Census-pending ratchet.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null || cat.BannedStringLiterals.Count == 0) return;
                start.RegisterSyntaxNodeAction(ctx => Analyze(ctx, cat), SyntaxKind.StringLiteralExpression);
            });
        }

        private static void Analyze(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            var lit = (LiteralExpressionSyntax)ctx.Node;
            var val = lit.Token.ValueText;
            if (string.IsNullOrEmpty(val)) return;
            foreach (var word in cat.BannedStringLiterals)
                if (val.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, lit.GetLocation(), word.ToLowerInvariant()));
                    break;
                }
        }
    }
}
