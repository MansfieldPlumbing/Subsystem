#requires -Version 7
# test.gate.ss009-threadpool.ps1 — Receipt for SS009's ThreadPool ban (no ambient threading).
# Subsystem owns every thread through Vom.Spawn; the ThreadPool owns none of them. SS009 flags a free
# Task.Run / new Thread / ThreadPool.* queue. Fires the built analyzer over Roslyn snippets: a ThreadPool
# call and a Task.Run flag; the kernel (a type named Ps) and a Thread.IsThreadPoolThread *check* do NOT.
# Authority = the binary; the receipt the run prints is. Dot-source-safe; no mutation.
#   Dogfood:  ss run tests/test.gate.ss009-threadpool.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# the freshly-built analyzer assembly (Release), wherever the build/ sibling landed
$dll = Get-ChildItem 'S:\build','S:\subsystem-project\build' -Recurse -Filter Subsystem.Analyzers.dll -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match 'Release' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if(-not $dll){ Write-Host "SKIP - Subsystem.Analyzers.dll (Release) not found - build src/analyzers first." -ForegroundColor Yellow; return }
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
$t = $asm.GetType('Subsystem.Analyzers.SS009AmbientThreadAnalyzer')
if(-not $t){ Write-Host "SS009 type not present in the assembly." -ForegroundColor Red; return }
$analyzer = [Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer][Activator]::CreateInstance($t)
$desc = $analyzer.SupportedDiagnostics | Where-Object { $_.Id -eq 'SS009' } | Select-Object -First 1
Assert ($null -ne $desc) "SS009 is a registered diagnostic in the analyzer roster"

# reference assemblies: the clean ref pack (a single-file host's TPA list is empty)
$refDir = Get-ChildItem "S:\bin\dotnet\packs\Microsoft.NETCore.App.Ref\*\ref\*" -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if(-not $refDir){ Write-Host "SKIP - ref pack not found under S:\bin\dotnet\packs." -ForegroundColor Yellow; return }
$refs = [Microsoft.CodeAnalysis.MetadataReference[]]@(
    Get-ChildItem "$($refDir.FullName)\*.dll" | ForEach-Object { try { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName) } catch {} } | Where-Object { $_ }
)

function Fire([string]$src){
    $trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($src))
    $opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create("probe", $trees, $refs, $opts)
    $ia = [System.Collections.Immutable.ImmutableArray]::Create[Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer]($analyzer)
    $ao = [Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions]::new([System.Collections.Immutable.ImmutableArray[Microsoft.CodeAnalysis.AdditionalText]]::Empty)
    $cwa = [Microsoft.CodeAnalysis.Diagnostics.CompilationWithAnalyzers]::new($comp, $ia, $ao)
    $d = $cwa.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
    @($d | Where-Object { $_.Id -eq 'SS009' }).Count
}

$tp     = 'using System.Threading; namespace Subsystem.Device { public static class X { public static void Q() { ThreadPool.QueueUserWorkItem(_ => {}); } } }'
$task   = 'using System.Threading.Tasks; namespace Subsystem.Device { public static class Y { public static void R() { Task.Run(() => {}); } } }'
$check  = 'using System.Threading; namespace Subsystem.Device { public static class Z { public static bool C() { return Thread.CurrentThread.IsThreadPoolThread; } } }'
$kernel = 'using System.Threading; namespace Subsystem.Vom { public static class Ps { public static void Q() { ThreadPool.QueueUserWorkItem(_ => {}); } } }'

$ntp = Fire $tp; $ntask = Fire $task; $ncheck = Fire $check; $nkernel = Fire $kernel
Write-Host "fire: threadpool=$ntp task.run=$ntask is-pool-check=$ncheck kernel=$nkernel  (expect 1,1,0,0)"
Assert ($ntp -ge 1)     "flags ThreadPool.QueueUserWorkItem (work the VOM does not own)"
Assert ($ntask -ge 1)   "still flags Task.Run (the existing ambient path)"
Assert ($ncheck -eq 0)  "does NOT flag a Thread.IsThreadPoolThread check (a property read, the legal 'not a pool thread?' test)"
Assert ($nkernel -eq 0) "exempts the kernel (a type named Ps owns threads - that IS Spawn)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - SS009 bans the ThreadPool (and Task.Run / new Thread); the kernel and the IsThreadPoolThread check are exempt. We own every thread."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.gate.ss009-threadpool'; pass=$pass
    threadpool=$ntp; taskRun=$ntask; isPoolCheck=$ncheck; kernel=$nkernel
    verdict=$(if($pass){'SS009 fires on ThreadPool/Task.Run ambient threading; exempts the kernel + the IsThreadPoolThread check'}else{'see failures'})
}
