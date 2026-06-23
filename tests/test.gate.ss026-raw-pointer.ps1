#requires -Version 7
# test.gate.ss026-raw-pointer.ps1 — Receipt for SS026 (a raw pointer must not cross a boundary).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# Loads the built analyzer and FIRES it over sample sources via Roslyn: a non-extern public method
# returning nint OUTSIDE the Vom kernel must flag SS026; the kernel, an [DllImport] extern, and a
# handle/object return must NOT. Dot-source-safe; no mutation.
#   Dogfood:  ss run tests/test.gate.ss026-raw-pointer.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# the freshly-built analyzer assembly (Release)
$dll = Get-ChildItem "S:\subsystem-project\build\Subsystem.Analyzers\bin\Release\netstandard2.0\Subsystem.Analyzers.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if(-not $dll){ Write-Host "Subsystem.Analyzers.dll (Release) not found — build src/analyzers first." -ForegroundColor Red; return }
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
$t = $asm.GetType('Subsystem.Analyzers.SS026RawPointerBoundaryAnalyzer')
if(-not $t){ Write-Host "SS026 type not present in the assembly." -ForegroundColor Red; return }
$analyzer = [Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer][Activator]::CreateInstance($t)

# descriptor sanity (loads + registers SS026 as Error = default-deny)
$desc = $analyzer.SupportedDiagnostics | Where-Object { $_.Id -eq 'SS026' } | Select-Object -First 1
Assert ($null -ne $desc) "SS026 is a registered diagnostic in the analyzer roster"
Assert ($null -ne $desc -and $desc.DefaultSeverity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) "SS026 default severity is Error (default-deny)"

# reference assemblies: the clean ref pack (a single-file host's TPA list is empty)
$refDir = Get-ChildItem "S:\bin\dotnet\packs\Microsoft.NETCore.App.Ref\*\ref\*" -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if(-not $refDir){ Write-Host "ref pack not found under S:\bin\dotnet\packs — cannot fire." -ForegroundColor Yellow; return }
$refs = [Microsoft.CodeAnalysis.MetadataReference[]]@(
    Get-ChildItem "$($refDir.FullName)\*.dll" | ForEach-Object { try { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName) } catch {} } | Where-Object { $_ }
)

function Fire([string]$src){
    $trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($src))
    $opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true)
    $comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create("probe", $trees, $refs, $opts)
    $ia = [System.Collections.Immutable.ImmutableArray]::Create[Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer]($analyzer)
    $ao = [Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions]::new([System.Collections.Immutable.ImmutableArray[Microsoft.CodeAnalysis.AdditionalText]]::Empty)
    $cwa = [Microsoft.CodeAnalysis.Diagnostics.CompilationWithAnalyzers]::new($comp, $ia, $ao)
    $d = $cwa.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
    @($d | Where-Object { $_.Id -eq 'SS026' }).Count
}

$violation = 'namespace Subsystem.Device { public static class X { public static nint GetPtr() { return default; } } }'
$kernel    = 'namespace Subsystem.Vom { public static class Y { public static nint Alloc() { return default; } } }'
$handle    = 'namespace Subsystem.Device { public static class Z { public static object GetHandle() { return null; } } }'
$ffi       = 'using System.Runtime.InteropServices; namespace Subsystem.Device { public static class W { [DllImport("x")] public static extern nint P(); } }'

$nv = Fire $violation; $nk = Fire $kernel; $nh = Fire $handle; $nf = Fire $ffi
Write-Host "fire: violation=$nv kernel=$nk handle=$nh ffi=$nf  (expect 1,0,0,0)"
Assert ($nv -ge 1) "flags a public nint-returning method outside the Vom kernel"
Assert ($nk -eq 0) "exempts the Vom kernel (it owns native memory)"
Assert ($nh -eq 0) "does not flag a handle/object return (the capability is fine)"
Assert ($nf -eq 0) "exempts the FFI edge ([DllImport] extern)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — SS026 default-denies a raw pointer crossing a boundary; kernel + FFI + handle returns exempt."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.gate.ss026-raw-pointer'; pass=$pass
    violation=$nv; kernel=$nk; handle=$nh; ffi=$nf
    verdict=$(if($pass){'SS026 fires on a boundary-crossing raw pointer; exempts kernel/FFI/handle'}else{'see failures'})
}
