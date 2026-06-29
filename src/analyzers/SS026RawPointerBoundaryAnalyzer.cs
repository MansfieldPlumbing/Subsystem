using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Subsystem.Analyzers
{
    /// <summary>
    /// SS026 — a raw pointer never crosses a boundary; the capability (a VOM handle) does. A non-extern
    /// public/internal method OUTSIDE the Vom kernel that returns a raw pointer (nint/IntPtr/void*) is a
    /// mechanism to share a pointer — the thing the system never has (CONTRACT invariant 2; the isolation
    /// discipline that lets owners be protection domains). Default-deny: we don't do it, so we forbid the
    /// mechanism. Generalizes SS008 (cmdlets) to the whole non-kernel surface. Two exemptions only: the
    /// FFI edge — caller (extern / [DllImport] / [LibraryImport]) AND callee ([UnmanagedCallersOnly], a
    /// native callback that must return the C-ABI value, e.g. a Win32 HANDLE) — is the one sanctioned
    /// raw-pointer surface, and the kernel (Subsystem.Vom) owns native memory.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SS026RawPointerBoundaryAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SS026";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, "Raw pointer crosses a boundary instead of a VOM handle",
            "Method '{0}' returns {1} outside the Vom kernel — a raw pointer must not cross a boundary; return a VOM handle (the capability crosses, the pointer never does)",
            "Subsystem.NT", DiagnosticSeverity.Error, isEnabledByDefault: true,
            "We never share a raw pointer across a boundary, so default-deny the mechanism: return a VOM handle, or mark the FFI edge extern / [DllImport].");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
        }

        private static void Analyze(SyntaxNodeAnalysisContext ctx)
        {
            var method = (MethodDeclarationSyntax)ctx.Node;
            if (ctx.SemanticModel.GetDeclaredSymbol(method, ctx.CancellationToken) is not IMethodSymbol sym) return;

            // The boundary surface only — a private helper is not a cross-boundary mechanism.
            if (sym.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return;

            // The FFI edge is the one sanctioned raw-pointer surface — caller (extern / [DllImport] /
            // [LibraryImport]) and callee ([UnmanagedCallersOnly]: a native callback bound to a
            // delegate* unmanaged, which must return the C-ABI value such as a Win32 HANDLE).
            if (sym.IsExtern || HasInteropAttribute(sym)) return;

            // The kernel owns native memory — exempt.
            var ns = sym.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns == "Subsystem.Vom" || ns.StartsWith("Subsystem.Vom.", System.StringComparison.Ordinal)) return;

            var kind = PointerKind(sym.ReturnType);
            if (kind == null) return;

            ctx.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), sym.Name, kind));
        }

        private static string? PointerKind(ITypeSymbol t)
        {
            if (t is IPointerTypeSymbol) return "a raw pointer";
            if (t.SpecialType == SpecialType.System_IntPtr) return "nint";
            if (t.SpecialType == SpecialType.System_UIntPtr) return "nuint";
            if (t.Name is "IntPtr" or "UIntPtr") return t.Name;
            return null;
        }

        private static bool HasInteropAttribute(IMethodSymbol sym) =>
            sym.GetAttributes().Any(a => a.AttributeClass?.Name is "DllImportAttribute" or "LibraryImportAttribute" or "UnmanagedCallersOnlyAttribute");
    }
}
