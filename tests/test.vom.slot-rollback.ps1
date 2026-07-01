#requires -Version 7
# test.vom.slot-rollback.ps1 — Receipt for the A/B rollback half of the VOM loader (CRQ001).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# A good slot is live; a swap to a slot that LOADS but FAULTS on invoke is attempted; the probe throws;
# the loader frees the bad slot (free-on-zero) and keeps the good one. Dot-source-safe (no `exit`);
# mutates only throwaway \Slots owners it Terminates itself.
#   Dogfood:  ss run tests/test.vom.slot-rollback.ps1
#
#   A  ROLLBACK  bad swap reverts: rolledBack, v1 still serves, the bad slot's handle is gone. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded in this host — cannot run." -ForegroundColor Red; return }
if(-not $V.GetMethod('ApplySlotRollbackTest')){ Write-Host "Vom.ApplySlotRollbackTest not present — this binary predates slot rollback." -ForegroundColor Red; return }

$r = $V::ApplySlotRollbackTest() | ConvertFrom-Json
Write-Host "A  rollback: rolledBack=$($r.rolledBack) v1StillServes=$($r.v1StillServes) faultingSlotReclaimed=$($r.faultingSlotReclaimed)"
Assert ([bool]$r.rolledBack)        "a faulting swap rolled back instead of going live"
Assert ([bool]$r.v1StillServes)     "the prior good slot is still live and serving after the bad swap"
Assert ([bool]$r.faultingSlotReclaimed)  "the bad slot was freed-on-zero — no half-loaded residue"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — A/B rollback on the VOM loader: a bad swap reverts to the known-good slot and leaves nothing behind."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.vom.slot-rollback'; pass=$pass
    rolledBack=[bool]$r.rolledBack; v1StillServes=[bool]$r.v1StillServes; faultingSlotReclaimed=[bool]$r.faultingSlotReclaimed
    verdict=$(if($pass){'bad swap rolls back to the good slot; loser freed-on-zero'}else{'see failures'})
}
