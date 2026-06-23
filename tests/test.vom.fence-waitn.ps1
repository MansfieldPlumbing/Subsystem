#requires -Version 7
# test.vom.fence-waitn.ps1 — Receipt for Fence.WaitN, the general wait-for-N-of-M quorum (CRQ107).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# WaitAny is n=1, WaitAll is n=M; WaitN fills the middle (e.g. 2-of-3 consensus). Dot-source-safe.
#   Dogfood:  ss run tests/test.vom.fence-waitn.ps1
#
#   A  QUORUM   n=2-of-3 returns at once when two fences are phased (no needless park). Asserted.
#   B  PARK     n=3 parks until the laggard signals (~120ms) then wakes with the full count. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded in this host — cannot run." -ForegroundColor Red; return }
if(-not $V.GetMethod('WaitQuorumTest')){ Write-Host "Vom.WaitQuorumTest not present — this binary predates Fence.WaitN." -ForegroundColor Red; return }

$q = $V::WaitQuorumTest() | ConvertFrom-Json
Write-Host "A  quorum: metImmediate=$($q.metImmediate) in $($q.immediateMs)ms (n=2 of 3)"
Write-Host "B  park:   metFull=$($q.metFull) after $($q.blockedMs)ms (n=3, laggard at ~120ms)"
Assert ([bool]$q.quorumImmediate)       "n=2-of-3 returned at once when two fences were phased"
Assert ([bool]$q.quorumBlockedThenWoke) "n=3 parked until the laggard signalled, then woke with the full count"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — Fence.WaitN: the general N-of-M quorum between WaitAny (n=1) and WaitAll (n=M); futex-parked, no wake lost."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.vom.fence-waitn'; pass=$pass
    metImmediate=$q.metImmediate; immediateMs=$q.immediateMs; metFull=$q.metFull; blockedMs=$q.blockedMs
    verdict=$(if($pass){'WaitN quorum holds: immediate when met, parks-then-wakes otherwise'}else{'see failures'})
}
