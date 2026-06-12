using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS013 — method grammar (CONTRACT.md §3): a public/internal method on a registered component
    /// starts with a verb from the closed list (approved ∪ triage). NT's Action-Object law
    /// (IoAllocateIrp, KeSetEvent) with the prefix carried by the namespace instead of the identifier.
    /// Scoped to component folders only: unassigned areas join as their components are registered.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS013VerbGrammarAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS013";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Method does not start with a registered verb",
            "Method '{0}' starts with '{1}', which is not in the verb catalog — register the verb deliberately or use an approved one",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "Verb [+ Noun] over the closed verb list (SystemCatalog.json verbs.approved + verbs.triage; triage entries are pending the Get->Query style campaign). Census-pending ratchet.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null) return;

                start.RegisterSymbolAction(ctx => Analyze(ctx, cat), SymbolKind.Method);
            });
        }

        private static readonly ImmutableHashSet<string> ClrConventional = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "ToString", "Equals", "GetHashCode", "Dispose", "GetEnumerator", "CompareTo", "Deconstruct", "Main");

        private static void Analyze(SymbolAnalysisContext ctx, SystemCatalogFile cat)
        {
            var m = (IMethodSymbol)ctx.Symbol;
            if (m.MethodKind != MethodKind.Ordinary) return;
            if (m.IsOverride || m.IsImplicitlyDeclared) return;
            if (m.ExplicitInterfaceImplementations.Length > 0) return;
            if (m.DeclaredAccessibility != Accessibility.Public && m.DeclaredAccessibility != Accessibility.Internal) return;
            if (ClrConventional.Contains(m.Name)) return;

            var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc == null) return;
            var component = cat.ComponentOfPath(loc.SourceTree?.FilePath);
            if (component == null || component == "(host)") return;   // component folders only

            var tokens = SystemCatalogFile.Tokens(m.Name);
            if (tokens.Count == 0) return;
            var verb = tokens[0];
            if (cat.VerbsApproved.Contains(verb) || cat.VerbsTriage.Contains(verb)) return;

            ctx.ReportDiagnostic(Diagnostic.Create(Rule, loc, m.Name, verb));
        }
    }
}
