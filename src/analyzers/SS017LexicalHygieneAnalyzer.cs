using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS017 — lexical hygiene (the anti-slop dictionary), SCOPED so it is not a string scraper:
    ///   (a) banned.alwaysFlag — anthropomorphic / metaphor words refused in IDENTIFIERS WE DECLARE (a
    ///       type/member named Brain/Soul/Ouroboros is an ontology error: name the mechanism, never the
    ///       metaphor — CONTRACT.md §3). Read from the DECLARATION-name tokens, NOT from comments, string
    ///       literals, or REFERENCES: a comment may legitimately name the concept it tells you not to
    ///       encode, and a reference to a foreign BCL/SDK type (BadImageFormatException) is the seam, not
    ///       our naming (mirrors SS025). Scope to the names we author.
    ///   (b) banned.commentPatterns — casual / inflammatory comment tells (lol, hacky, blazingly, …). These
    ///       ARE a comment smell, so they stay scoped to comments.
    /// Census-pending ratchet: existing hits ride SS-BASELINE.txt; NEW slop bleeds red at the gate.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS017LexicalHygieneAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS017";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Banned word in a name, or casual/inflammatory comment",
            "{0}",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "Anthropomorphic/metaphor IDENTIFIERS and casual/inflammatory comments are slop the contract refuses (CONTRACT.md §3; SystemCatalog.json banned.alwaysFlag / banned.commentPatterns). Census-pending ratchet.");

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
                start.RegisterSyntaxTreeAction(ctx => Analyze(ctx, cat));
            });
        }

        private static void Analyze(SyntaxTreeAnalysisContext ctx, SystemCatalogFile cat)
        {
            var root = ctx.Tree.GetRoot(ctx.CancellationToken);

            // 1. Comments — casual/inflammatory PATTERNS only (a comment smell). Banned metaphor WORDS are
            //    NOT scraped from prose: a comment may legitimately name a concept it tells you not to
            //    encode in a type. The metaphor rule is enforced on NAMES below.
            if (cat.CommentPatterns.Count > 0)
            {
                foreach (var trivia in root.DescendantTrivia())
                {
                    if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                        !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                        !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                        !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                        continue;

                    var text = trivia.ToString();
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
            }

            // 2. Identifiers — a banned metaphor word as a camel token in a NAME is the ontology error.
            //    This reads identifier tokens (mechanism, never metaphor), not arbitrary text.
            if (cat.AlwaysFlag.Count > 0)
            {
                foreach (var token in root.DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
                    // Only the names WE declare — never a reference to a foreign type (e.g. a catch of
                    // System.BadImageFormatException is the seam, not our naming). Mirrors SS025's symbol scope.
                    if (!IsDeclarationName(token)) continue;
                    foreach (var part in SystemCatalogFile.Tokens(token.Text))
                        if (cat.AlwaysFlag.Contains(part))
                        {
                            ctx.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(),
                                "identifier '" + token.Text + "' contains the banned word '" + part.ToLowerInvariant() + "' — mechanism names only"));
                            break;
                        }
                }
            }
        }

        // The token is the NAME being declared (type/member/parameter/variable/type-param/foreach/local-fn) —
        // not a reference. A reference to a foreign type is the seam (the SS025 discipline), never our naming.
        private static bool IsDeclarationName(SyntaxToken token) => token.Parent switch
        {
            BaseTypeDeclarationSyntax t        => t.Identifier == token,   // class/struct/interface/enum/record
            DelegateDeclarationSyntax d        => d.Identifier == token,
            MethodDeclarationSyntax m          => m.Identifier == token,
            PropertyDeclarationSyntax p        => p.Identifier == token,
            EventDeclarationSyntax e           => e.Identifier == token,
            EnumMemberDeclarationSyntax em     => em.Identifier == token,
            VariableDeclaratorSyntax v         => v.Identifier == token,   // fields / locals / consts
            ParameterSyntax pa                 => pa.Identifier == token,
            TypeParameterSyntax tp             => tp.Identifier == token,
            ForEachStatementSyntax fe          => fe.Identifier == token,
            SingleVariableDesignationSyntax sv => sv.Identifier == token,
            LocalFunctionStatementSyntax lf    => lf.Identifier == token,
            _                                  => false,
        };
    }
}
