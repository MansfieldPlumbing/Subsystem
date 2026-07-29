using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS018 — SS018FenceClockRunspaceAnalyzer. Subsystem owns its runspace memory and ordering:
    /// every node owns a real thread and hands off through a hardware timeline Fence (push, best-effort, copy-then-share).
    /// The fence value IS the clock — a waiter parks on the address (futex WaitOnAddress), a producer wakes it (WakeByAddressAll).
    /// No scheduler, no continuation, no dynamic allocation.
    ///
    /// Prohibited in owned code:
    /// 1. async keyword on methods, local functions, or lambdas.
    /// 2. await expressions.
    /// 3. Sync-over-async invocations (Task.Wait(), Task.WaitAll(), Task.WaitAny(), Task.Result, .GetAwaiter().GetResult()).
    /// 4. ThreadPool delegation (Task.Run(), Task.Factory.StartNew(), ThreadPool.QueueUserWorkItem(), ThreadPool.UnsafeQueueUserWorkItem()).
    ///
    /// Exemptions (Strict fail-closed OutOfScope logic):
    /// 1. Generated code (obj/ directories or IsGeneratedPath).
    /// 2. Declared host seam boundaries (catalog hostPaths -> ComponentOfPath returns "(host)").
    /// Permissive opt-in filters (e.g. SynchronousCore) are strictly banned.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS018FenceClockRunspaceAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS018";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            "Async, sync-over-async, or ThreadPool in owned runspace code (SS018FenceClockRunspaceAnalyzer)",
            "{0} in owned runspace code — Subsystem owns its runspace and is synchronous (real OS threads + Fence handoff, the fence value is the clock). async/ThreadPool colors callers, hands owning thread to ambient schedulers, allocates state machines, and enables sync-over-async deadlocks.",
            "Subsystem.NT",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "DirectPort passes data via fences (best-effort push, copy-then-share, futex wake) — async/ThreadPool buys nothing in owned runspace code and costs caller-coloring, lost thread determinism, state-machine allocations, and sync-over-async deadlocks. Permitted ONLY at declared host/seam boundaries (hostPaths); generated code (obj/) is exempt. Gate is fail-closed with no override.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var cat = SystemCatalogFile.TryLoad(start.Options, out _);
                if (cat == null) return; // SS000 fail-closed; stay silent rather than guess

                start.RegisterSyntaxNodeAction(ctx => AnalyzeAsyncKeyword(ctx, cat),
                    SyntaxKind.MethodDeclaration,
                    SyntaxKind.LocalFunctionStatement,
                    SyntaxKind.ParenthesizedLambdaExpression,
                    SyntaxKind.SimpleLambdaExpression,
                    SyntaxKind.AnonymousMethodExpression);

                start.RegisterSyntaxNodeAction(ctx => AnalyzeAwait(ctx, cat), SyntaxKind.AwaitExpression);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, cat), SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(ctx => AnalyzeMemberAccess(ctx, cat), SyntaxKind.SimpleMemberAccessExpression);
            });
        }

        /// <summary>
        /// Strict fail-closed runspace enforcement.
        /// Exemptions:
        /// 1. Generated code (obj/ or IsGeneratedPath).
        /// 2. Declared host seam boundary (catalog hostPaths -> ComponentOfPath returns "(host)").
        /// All other owned runspace code is strictly analyzed without permissive opt-in filters.
        /// </summary>
        private static bool OutOfScope(SystemCatalogFile cat, string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (SystemCatalogFile.IsGeneratedPath(path)) return true;

            var component = cat.ComponentOfPath(path);
            if (component == "(host)") return true;

            return false; // Fail-closed: All owned runspace code is in scope
        }

        private static void AnalyzeAsyncKeyword(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;

            SyntaxTokenList modifiers = ctx.Node switch
            {
                MethodDeclarationSyntax m => m.Modifiers,
                LocalFunctionStatementSyntax lf => lf.Modifiers,
                ParenthesizedLambdaExpressionSyntax pl => pl.Modifiers,
                SimpleLambdaExpressionSyntax sl => sl.Modifiers,
                AnonymousMethodExpressionSyntax am => am.Modifiers,
                _ => default,
            };

            var kw = modifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.AsyncKeyword));
            if (kw.IsKind(SyntaxKind.AsyncKeyword))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, kw.GetLocation(), "the `async` keyword"));
            }
        }

        private static void AnalyzeAwait(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;

            var aw = (AwaitExpressionSyntax)ctx.Node;
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, aw.AwaitKeyword.GetLocation(), "await"));
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;

            var inv = (InvocationExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetSymbolInfo(inv, ctx.CancellationToken).Symbol is not IMethodSymbol m) return;

            // Check sync-over-async
            if ((m.Name == "Wait" || m.Name == "WaitAll" || m.Name == "WaitAny") && IsTaskType(m.ContainingType))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "Task." + m.Name + "()"));
            }
            else if (m.Name == "GetResult" && IsAwaiterType(m.ContainingType))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), ".GetAwaiter().GetResult()"));
            }
            // Check ThreadPool delegation: Task.Run, Task.Factory.StartNew, ThreadPool.QueueUserWorkItem, etc.
            else if (m.Name == "Run" && IsTaskType(m.ContainingType))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "Task.Run()"));
            }
            else if (m.Name == "StartNew" && m.ContainingType?.Name == "TaskFactory")
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "Task.Factory.StartNew()"));
            }
            else if ((m.Name == "QueueUserWorkItem" || m.Name == "UnsafeQueueUserWorkItem") && IsThreadPoolType(m.ContainingType))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "ThreadPool." + m.Name + "()"));
            }
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext ctx, SystemCatalogFile cat)
        {
            if (OutOfScope(cat, ctx.Node.SyntaxTree.FilePath)) return;

            var ma = (MemberAccessExpressionSyntax)ctx.Node;
            if (ma.Name.Identifier.Text != "Result") return;

            var typeInfo = ctx.SemanticModel.GetTypeInfo(ma.Expression, ctx.CancellationToken);
            if (IsTaskType(typeInfo.Type))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, ma.Name.GetLocation(), ".Result"));
            }
        }

        private static bool IsTaskType(ITypeSymbol? t)
        {
            for (var b = t; b != null; b = b.BaseType)
            {
                var n = b.OriginalDefinition?.ToDisplayString();
                if (n == "System.Threading.Tasks.Task" ||
                    n == "System.Threading.Tasks.Task<TResult>" ||
                    n == "System.Threading.Tasks.ValueTask" ||
                    n == "System.Threading.Tasks.ValueTask<TResult>")
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsAwaiterType(ITypeSymbol? t)
        {
            var n = t?.OriginalDefinition?.ToDisplayString() ?? "";
            return n.StartsWith("System.Runtime.CompilerServices.") && n.Contains("Awaiter");
        }

        private static bool IsThreadPoolType(ITypeSymbol? t)
        {
            var n = t?.OriginalDefinition?.ToDisplayString() ?? "";
            return n == "System.Threading.ThreadPool";
        }
    }
}
