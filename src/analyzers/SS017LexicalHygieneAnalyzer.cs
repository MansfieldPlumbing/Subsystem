using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS017 — lexical hygiene (the anti-slop dictionary). Two data-driven checks, both reading their
    /// word lists from SystemCatalog.json (one seed, edited deliberately):
    ///   (a) banned.alwaysFlag — words refused EVERYWHERE (identifiers, string literals, comments).
    ///       The big-brother list: anthropomorphic / metaphor names the contract forbids. Mechanism,
    ///       never metaphor (CONTRACT.md §3).
    ///   (b) banned.commentPatterns — casual / inflammatory comment tells (the slop signal). Comments
    ///       record facts, not editorializing.
    /// Census-pending ratchet: existing hits ride SS-BASELINE.txt; NEW slop bleeds red at the gate.
    /// Fires everywhere, hosts included — these words are universally refused, not context-scoped.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS017LexicalHygieneAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS017";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Banned word or casual/inflammatory comment",
            "{0}",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "Anthropomorphic/metaphor names and casual or inflammatory comments are slop the contract refuses (CONTRACT.md §3; SystemCatalog.json banned.alwaysFlag / banned.commentPatterns). Census-pending ratchet.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null) return;
                if (cat.AlwaysFlag.Count == 0 && cat.CommentPatterns.Count == 0) return;

                Regex? always = cat.AlwaysFlag.Count > 0
                    ? new Regex(@"\b(" + string.Join("|", cat.AlwaysFlag.Select(Regex.Escape)) + @")\b",
                                RegexOptions.IgnoreCase | RegexOptions.Compiled)
                    : null;

                start.RegisterSyntaxTreeAction(ctx => Analyze(ctx, cat, always));
            });
        }

        private static void Analyze(SyntaxTreeAnalysisContext ctx, SystemCatalogFile cat, Regex? always)
        {
            var root = ctx.Tree.GetRoot(ctx.CancellationToken);

            // 1. Comments — banned words AND casual/inflammatory patterns.
            foreach (var trivia in root.DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    continue;

                var text = trivia.ToString();

                if (always != null)
                {
                    var m = always.Match(text);
                    if (m.Success)
                        ctx.ReportDiagnostic(Diagnostic.Create(Rule, trivia.GetLocation(),
                            "comment uses the banned word '" + m.Value.ToLowerInvariant() + "' — mechanism names only, no metaphor"));
                }

                foreach (var pat in cat.CommentPatterns)
                {
                    if (text.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(Rule, trivia.GetLocation(),
                            "casual/inflammatory comment ('" + pat + "') — record facts, not editorializing"));
                        break;
                    }
                }
            }

            // 2. Identifiers + string literals — banned words live nowhere (big-brother).
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    foreach (var part in SystemCatalogFile.Tokens(token.Text))
                        if (cat.AlwaysFlag.Contains(part))
                        {
                            ctx.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(),
                                "identifier '" + token.Text + "' contains the banned word '" + part.ToLowerInvariant() + "' — mechanism names only"));
                            break;
                        }
                }
                else if (always != null && token.IsKind(SyntaxKind.StringLiteralToken))
                {
                    var m = always.Match(token.Text);
                    if (m.Success)
                        ctx.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(),
                            "string contains the banned word '" + m.Value.ToLowerInvariant() + "' — mechanism names only"));
                }
            }
        }
    }
}
