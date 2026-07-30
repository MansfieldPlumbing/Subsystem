using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS027 — Android API floor. The canonical Android head's minimum is API 36 (Android 16): the
    /// AppFunction integrations are an API-36 framework, so a lower floor silently breaks them. The floor is
    /// the &lt;SupportedOSPlatformVersion&gt; declaration in src/runspace/Subsystem.csproj, which is wired to
    /// itself as an AdditionalFile so this rule can read it (no file I/O — AdditionalText is the only door).
    ///
    /// A sub-36 floor is permitted ONLY behind the SsLegacyApi build flag — the one sanctioned seam for an
    /// exceptional legacy-device build (an Android-15 device) that never moves the default. An unconditional
    /// or otherwise-gated value below 36, or a missing floor, bleeds the gate red. The binary remembers the
    /// floor so a person does not have to (a prior session lowered it 36 -> 35).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS027AndroidApiFloorAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS027";
        private const int Floor = 36;                       // Android 16 — the AppFunction floor
        private const string ExceptionFlag = "SsLegacyApi"; // the one sanctioned seam for a sub-floor build
        private const string CsprojName = "subsystem.master.csproj";

        // <SupportedOSPlatformVersion [Condition="..."]>NN[.N]</SupportedOSPlatformVersion>
        private static readonly Regex FloorDecl = new Regex(
            "<SupportedOSPlatformVersion(?:\\s+Condition\\s*=\\s*\"(?<cond>[^\"]*)\")?\\s*>\\s*(?<ver>\\d+)(?:\\.\\d+)?\\s*</SupportedOSPlatformVersion>",
            RegexOptions.IgnoreCase);

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Android API floor below 36 (Android 16)",
            "The Android API floor {0} — API 36 (Android 16) is the canonical minimum (AppFunction integrations require it); a sub-36 floor is allowed only behind the SsLegacyApi flag.",
            "Subsystem.NT", DiagnosticSeverity.Error, isEnabledByDefault: true,
            "src/runspace/Subsystem.csproj must declare SupportedOSPlatformVersion >= 36 by default; any lower value must be gated behind -p:SsLegacyApi=true (the sanctioned legacy-device exception).");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(Analyze);
        }

        private static void Analyze(CompilationAnalysisContext ctx)
        {
            AdditionalText? csproj = null;
            foreach (var f in ctx.Options.AdditionalFiles)
                if (f.Path != null && f.Path.EndsWith(CsprojName, StringComparison.OrdinalIgnoreCase)) { csproj = f; break; }

            if (csproj == null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None,
                    "cannot be verified — " + CsprojName + " is not wired as an AdditionalFile"));
                return;
            }

            var src = csproj.GetText(ctx.CancellationToken);
            if (src == null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None,
                    "cannot be verified — " + CsprojName + " could not be read"));
                return;
            }
            var text = src.ToString();

            var decls = FloorDecl.Matches(text).Cast<Match>()
                .Select(m => (Ver: int.Parse(m.Groups["ver"].Value), Cond: m.Groups["cond"].Value))
                .ToList();

            if (decls.Count == 0)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None,
                    "is not declared — no <SupportedOSPlatformVersion> exists"));
                return;
            }

            // An unsanctioned sub-floor: a value < 36 not gated behind the SsLegacyApi flag (the 36 -> 35 drift).
            var rogue = decls
                .Where(d => d.Ver < Floor && d.Cond.IndexOf(ExceptionFlag, StringComparison.OrdinalIgnoreCase) < 0)
                .Select(d => (int?)d.Ver).FirstOrDefault();
            if (rogue is int low)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None,
                    $"is lowered to {low} without the {ExceptionFlag} exception"));
                return;
            }

            // A canonical floor (one not gated behind the exception) must still stand at >= 36.
            bool canonicalHolds = decls.Any(d =>
                d.Cond.IndexOf(ExceptionFlag, StringComparison.OrdinalIgnoreCase) < 0 && d.Ver >= Floor);
            if (!canonicalHolds)
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, Location.None,
                    $"has no canonical value >= {Floor} (only the {ExceptionFlag} exception remains)"));
        }
    }
}
