using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS018 — No async in the runspace. Subsystem owns its runspace (and the federated ss runspaces, and
    /// them only); it is brutally synchronous: every node owns a real thread and hands off through a Fence
    /// (push, best-effort, copy-then-share). The fence value IS the clock — a waiter parks on the address
    /// (futex), a producer wakes it (WakeByAddressAll); no scheduler, no continuation, no allocation. async
    /// surrenders exactly the two things Subsystem exists to own — memory and ordering: it colors callers,
    /// hands the owning thread to the ThreadPool's scheduler, allocates hidden state machines, and births the
    /// sync-over-async deadlock (.Result / .Wait() / .GetAwaiter().GetResult()). So the <c>async</c> keyword,
    /// <c>await</c>, and sync-over-async are REFUSED in owned component code.
    ///
    /// Async is permitted ONLY at a boundary the owner declares upfront is not his: the host/seam
    /// (catalog <c>hostPaths</c> — the head, Services, the WebView side of the fence). Foreign generated code
    /// (obj/) is exempt. The boundary ratchets by editing the catalog, never by a silent default and never by
    /// a runtime override — the gate has none. — Scott: "we do not async, we own our runspace."
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS018BlockingWaitAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS018";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Async in the runspace (the runspace is synchronous)",
            "{0} in owned runspace code — Subsystem owns its runspace and is synchronous (real threads + Fence handoff, the fence value is the clock). async colors callers, hands the owning thread to the scheduler, allocates a state machine, and enables the sync-over-async deadlock. Make it synchronous, or move it to a declared host/seam boundary (catalog hostPaths)",
            "Subsystem.NT", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            "The VOM passes data via fences (best-effort push, copy-then-share, futex wake) — async buys nothing in the runspace and costs caller-coloring, a lost thread model, state-machine allocations, and the sync-over-async deadlock. Permitted ONLY at declared host/seam boundaries (hostPaths); generated code (obj/) is exempt. The gate is fail-closed with no override — a RED gate is always fatal. Census-pending ratchet.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null) return;   // SS000 fail-closed; stay silent rather than guess
                start.RegisterSyntaxNodeAction(ctx => AnalyzeAsyncKeyword(ctx, cat),
                    SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement,
                    SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.SimpleLambdaExpression,
                    SyntaxKind.AnonymousMethodExpression);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeAwait(ctx, cat), SyntaxKind.AwaitExpression);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, cat), SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeMemberAccess(ctx, cat), SyntaxKind.SimpleMemberAccessExpression);
            });
        }

        // In scope for ALL owned components (the runspace). Exempt: foreign generated code (obj/) and the
        // host/seam the owner declared not-his (catalog hostPaths — ComponentOfPath returns "(host)"). A path
        // that maps to no component is unowned surface, also exempt. The no-async boundary ratchets OUTWARD by
        // editing the catalog's hostPaths, never by a silent default.
        private static bool OutOfScope(SystemCatalogFile cat, string path)
        {
            if (SystemCatalogFile.IsGeneratedPath(path)) return true;
            var c = cat.ComponentOfPath(path);
            return c == null || c == "(host)";
        }

        // The `async` keyword itself — the declaration that colors this method and every caller. Catches the
        // async IAsyncEnumerable "costume" (yield, no await) that the await check alone would miss.
        private static void AnalyzeAsyncKeyword(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;
            var mods = ctx.Node switch
            {
                MethodDeclarationSyntax m => m.Modifiers,
                LocalFunctionStatementSyntax lf => lf.Modifiers,
                ParenthesizedLambdaExpressionSyntax pl => pl.Modifiers,
                SimpleLambdaExpressionSyntax sl => sl.Modifiers,
                AnonymousMethodExpressionSyntax am => am.Modifiers,
                _ => default,
            };
            var kw = mods.FirstOrDefault(t => t.IsKind(SyntaxKind.AsyncKeyword));
            if (kw.IsKind(SyntaxKind.AsyncKeyword))
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, kw.GetLocation(), "the `async` keyword"));
        }

        // `await` — the universal async signal in source.
        private static void AnalyzeAwait(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;
            var aw = (AwaitExpressionSyntax)ctx.Node;
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, aw.AwaitKeyword.GetLocation(), "await"));
        }

        // Sync-over-async: .Wait()/.WaitAll()/.WaitAny() on Task, and explicit .GetResult() on an awaiter.
        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;
            var inv = (InvocationExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetSymbolInfo(inv, ctx.CancellationToken).Symbol is not IMethodSymbol m) return;
            if ((m.Name == "Wait" || m.Name == "WaitAll" || m.Name == "WaitAny") && IsTaskType(m.ContainingType))
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "Task." + m.Name + "()"));
            else if (m.Name == "GetResult" && IsAwaiterType(m.ContainingType))
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), ".GetAwaiter().GetResult()"));
        }

        // Sync-over-async: .Result on Task<T> / ValueTask<T>.
        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;
            var ma = (MemberAccessExpressionSyntax)ctx.Node;
            if (ma.Name.Identifier.Text != "Result") return;
            if (IsTaskType(ctx.SemanticModel.GetTypeInfo(ma.Expression, ctx.CancellationToken).Type))
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, ma.Name.GetLocation(), ".Result"));
        }

        private static bool IsTaskType(ITypeSymbol? t)
        {
            for (var b = t; b != null; b = b.BaseType)
            {
                var n = b.OriginalDefinition?.ToDisplayString();
                if (n == "System.Threading.Tasks.Task" || n == "System.Threading.Tasks.Task<TResult>"
                    || n == "System.Threading.Tasks.ValueTask" || n == "System.Threading.Tasks.ValueTask<TResult>")
                    return true;
            }
            return false;
        }

        private static bool IsAwaiterType(ITypeSymbol? t)
        {
            var n = t?.OriginalDefinition?.ToDisplayString() ?? "";
            return n.StartsWith("System.Runtime.CompilerServices.") && n.Contains("Awaiter");
        }
    }
}
