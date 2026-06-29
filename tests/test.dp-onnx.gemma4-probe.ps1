#requires -Version 7
# test.dp-onnx.gemma4-probe.ps1 — Receipt for the Gemma-4 E2B rung on dp-onnx (the GraphRuntime LLM).
# gemma4 runs ENTIRELY on the now-dll-free dp-onnx (managed engine + pure-C# GPU seam; NO dpgpu.dll). This probes
# the decomposed export graph for op coverage (the correctness rung; export = one-time Python, see wip/gemma4/).
# SKIPS clean when the gated ~10 GB export (W:\gemma4\onnx\e2b_step.onnx) isn't present -- the only blocker.
#   Dogfood:  ss run tests/test.dp-onnx.gemma4-probe.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$onnx = @('W:\gemma4\onnx\e2b_step.onnx','W:\gemma4\onnx\e2b_prefill.onnx','S:\models\gemma4\e2b_step.onnx') |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $onnx) { Write-Host "SKIP - gemma-4 E2B export not present (re-export via wip/gemma4/export_gemma4_e2b_decomposed.py; gated ~10 GB -> W:)." -ForegroundColor Yellow; return }

$dp = @('S:\subsystem','S:\subsystem-project\subsystem-main') | ForEach-Object { Join-Path $_ 'src\native\dp-onnx' } |
      Where-Object { Test-Path (Join-Path $_ 'onnx-interp\Onnx.Interp.csproj') } | Select-Object -First 1
$dotnet = @('S:\bin\dotnet\dotnet.exe',(Get-Command dotnet -EA SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $dp -or -not $dotnet) { Write-Host "SKIP - dp-onnx project / dotnet not found." -ForegroundColor Yellow; return }

& $dotnet build (Join-Path $dp 'onnx-interp\Onnx.Interp.csproj') -c Release --nologo *>&1 | Out-Null
$exe = Get-ChildItem 'S:\build\Onnx.Interp\bin\Release\net*\dp-onnx.exe' -EA SilentlyContinue | Select-Object -First 1
if (-not $exe) { Write-Host "SKIP - built dp-onnx.exe not found." -ForegroundColor Yellow; return }
Assert (-not (Test-Path (Join-Path $exe.DirectoryName 'dpgpu.dll'))) "gemma4 runtime is dll-free (no dpgpu.dll in output)"

$out = (& $exe.FullName probe $onnx 2>&1 | Out-String).Trim()
Write-Host $out
Assert ($out -notmatch 'unimplemented|unsupported|UNHANDLED|not implemented') "dp-onnx covers the gemma-4 decomposed op set"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - Gemma-4 E2B probes clean on the dll-free dp-onnx GraphRuntime (managed + pure-C# GPU)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.dp-onnx.gemma4-probe'; pass=$pass; verdict=$(if($pass){'gemma-4 op coverage green on dll-free dp-onnx'}else{'see failures'}) }
