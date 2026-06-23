#requires -Version 7
# test.vom.fence-timeline.ps1 — Proves the "timeline fence" half of the primitive: a CpuFence is a
# MONOTONIC u64 timeline — Signal advances it, a lower Signal never regresses it, and Wait returns at
# once when the value is already met. (Park-then-wake under contention is proven by the WaitQuorum /
# WaitPhaseLock kernel tests.) Authority = the binary; this comment is not. Dot-source-safe.
#   Dogfood:  ss run tests/test.vom.fence-timeline.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$fenceType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.CpuFence') } | Where-Object {$_} | Select-Object -First 1
if(-not $fenceType){ Write-Host "Subsystem.Vom.CpuFence not loaded." -ForegroundColor Red; return }

$f = [Activator]::CreateInstance($fenceType)
$v0 = $f.CompletedValue
$f.Signal(5);  $v5 = $f.CompletedValue
$f.Signal(3);  $vNoRegress = $f.CompletedValue        # lower signal must not regress
$sw = [Diagnostics.Stopwatch]::StartNew(); $f.Wait(5); $waitMs = $sw.ElapsedMilliseconds   # already met -> instant
$f.Signal(7);  $v7 = $f.CompletedValue
Write-Host "fence: start=$v0 afterSignal5=$v5 afterSignal3=$vNoRegress wait(5)=${waitMs}ms afterSignal7=$v7"
Assert ($v0 -eq 0)          "a fresh fence starts at 0"
Assert ($v5 -eq 5)          "Signal advances the completed value"
Assert ($vNoRegress -eq 5)  "a lower Signal never regresses the timeline (monotonic)"
Assert ($waitMs -lt 50)     "Wait returns immediately when the value is already met"
Assert ($v7 -eq 7)          "Signal advances forward again"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — monotonic timeline fence: forward-only Signal, instant Wait-when-met."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.fence-timeline'; pass=$pass; verdict=$(if($pass){'monotonic u64 timeline; no regress; instant wait-when-met'}else{'see failures'}) }
