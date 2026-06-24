using System;
using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS025 — a retired term must not cross into our territory. The motivating case is "session": NT/Cutler
    /// has no Session dispatcher object (Process, Thread, Job, Token, Event, Section…; \Sessions is a namespace
    /// partition, never a type), and the VOM already carries the right primitive — Owner (a handle-table
    /// container with a token + cascade, the Process/Job analog, i.e. the container AND the lifetime). So a
    /// foreign session, the instant it is OURS, is renamed to our vocabulary — the Owner, a handle (the thing),
    /// a Device (the mount) — never kept as "session" (a userland label AND a second store of truth,
    /// invariants 1 + 6).
    ///
    /// This is a BOUNDARY rule, not a string scraper. It flags the retired token ONLY in:
    ///   (a) symbols WE declare (types/methods/properties/fields/events/parameters) — the foreign SDK's own
    ///       Session-named types (InitialSessionState, SessionState*) are the SEAM itself; references to them
    ///       are metadata, not source symbols, so they are never flagged — that is the point: the foreign word
    ///       lives at the seam, our names do not adopt it; and
    ///   (b) OUR interpolated-string text (a \Sessions\ path) — the companion to SS024, which already bans the
    ///       same retired tokens in plain string literals.
    /// The retired tokens are DATA (SystemCatalog.json banned.stringLiterals), shared with SS024. Census-pending
    /// ratchet: existing hits ride SS-BASELINE.txt; a NEW occurrence bleeds red at the gate.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS025RetiredTermAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS025";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Retired term crossed into a name or path we author",
            "{0} carries the retired term '{1}' — a foreign concept is translated at the seam to our vocabulary (the VOM Owner = the container/lifetime, a handle = the thing, a Device = the mount), never adopted as-is",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "A term the contract retired must not re-enter our domain naming. SS025 scans only the symbols WE declare (foreign SDK types are the seam, exempt) + our interpolated strings; SS024 covers plain string literals. SystemCatalog.json banned.stringLiterals. Census-pending ratchet.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null || cat.BannedStringLiterals.Count == 0) return;

                start.RegisterSymbolAction(ctx => AnalyzeSymbol(ctx, cat),
                    SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property,
                    SymbolKind.Field, SymbolKind.Event, SymbolKind.Parameter);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeInterpolated(ctx, cat),
                    SyntaxKind.InterpolatedStringText);
            });
        }

        private static string? Hit(string? name, SystemCatalogFile cat)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var word in cat.BannedStringLiterals)
                if (name!.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return word;
            return null;
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext ctx, SystemCatalogFile cat)
        {
            var sym = ctx.Symbol;
            if (sym.IsImplicitlyDeclared) return;
            // P/Invoke (extern / [DllImport]) is a foreign C-ABI binding — its name is the C symbol (engine.h's
            // litert_lm_session_config_*), the SEAM, not ours to rename (same exemption as SS013). Skip the extern
            // method AND its parameters. Also skip accessors/ctors/operators (they carry the property/type name).
            if (sym is IMethodSymbol m && (m.IsExtern || m.MethodKind != MethodKind.Ordinary)) return;
            if (sym is IParameterSymbol p && p.ContainingSymbol is IMethodSymbol pm && (pm.IsExtern || pm.MethodKind != MethodKind.Ordinary)) return;

            var word = Hit(sym.Name, cat);
            if (word == null) return;
            var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc == null || SystemCatalogFile.IsGeneratedPath(loc.SourceTree?.FilePath)) return;

            ctx.ReportDiagnostic(Diagnostic.Create(Rule, loc, Describe(sym.Kind) + " '" + sym.Name + "'", word.ToLowerInvariant()));
        }

        private static void AnalyzeInterpolated(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (SystemCatalogFile.IsGeneratedPath(ctx.Node.SyntaxTree.FilePath)) return;
            var text = (InterpolatedStringTextSyntax)ctx.Node;
            var word = Hit(text.TextToken.ValueText, cat);
            if (word != null)
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, text.GetLocation(), "interpolated string", word.ToLowerInvariant()));
        }

        private static string Describe(SymbolKind k) => k switch
        {
            SymbolKind.NamedType => "type",
            SymbolKind.Method => "method",
            SymbolKind.Property => "property",
            SymbolKind.Field => "field",
            SymbolKind.Event => "event",
            SymbolKind.Parameter => "parameter",
            _ => "name",
        };
    }
}
