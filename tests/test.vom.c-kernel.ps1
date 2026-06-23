#requires -Version 7
# test.vom.c-kernel.ps1 — Receipt for the C VOM kernel (src/native/vom): the dual-language parity seam.
# Builds vom.c + vom_test.c with cl and runs the native self-test, proving the C side matches the C#
# VOM contract (256-aligned regions, refcount free-on-zero, generational handles). SKIPS clean if no
# complete MSVC is present (a floor box without VS); the proof is the native receipt, not this wrapper.
#   Dogfood:  ss run tests/test.vom.c-kernel.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$build = 'S:\subsystem-project\subsystem-main\src\native\vom\build.ps1'
if (-not (Test-Path $build)) { Write-Host "build.ps1 not found." -ForegroundColor Red; return }

$haveVc = @($env:SS_VCVARS, 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
            'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat') |
          Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $haveVc) { Write-Host "SKIP - no complete MSVC (vcvars64) on this box; C VOM build not exercised here." -ForegroundColor Yellow; return }

$out = (& $build *>&1 | Out-String)
Write-Host $out.Trim()
Assert ($out -match 'built ->')                              "C VOM compiled with cl (vom.c + vom_test.c)"
Assert ($out -match '"pass":true' -or $out -match 'PASS - C VOM') "C VOM self-test PASS (256-align, free-on-zero, generational)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - the C VOM kernel (parity seam) compiles and matches the C# VOM contract."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.c-kernel'; pass=$pass; verdict=$(if($pass){'C VOM compiles + self-tests green - dual-language parity seam'}else{'see failures'}) }
