#requires -Version 7
# test.vom.slot-swap.ps1 — Receipt for the VOM-as-loader: free-on-zero applied to CODE.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# The loader is the VOM's ONE mechanism (handle = authority, free-at-zero) pointed at a code slot —
# a collectible AssemblyLoadContext registered as a refcounted handle. Close-to-zero -> Reclaim ->
# ALC.Unload. Dot-source-safe (no `exit`); mutates only throwaway \Slots owners it Terminates itself.
#   Dogfood:  ss run tests/test.vom.slot-swap.ps1
#
#   A  SWAP    load slot v1, a caller enters/leaves it, v2 comes up, v1 freed at refcount 0. Asserts
#              v1 served 1, v2 serves 2 (and still after v1 is gone), and v1's ALC is reclaimed on zero.
#   B  RESIDUE the swap cycle leaves NO \Slots owner behind — free-on-zero cascaded to the owner. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded in this host — cannot run." -ForegroundColor Red; return }
if(-not $V.GetMethod('SlotSwapTest')){ Write-Host "Vom.SlotSwapTest not present — this binary predates the slot loader." -ForegroundColor Red; return }

# A — load / swap / free-on-zero on code
$swap = $V::SlotSwapTest() | ConvertFrom-Json
Write-Host "A  swap: v1=$($swap.v1Value) v2=$($swap.v2Value) ; v2LiveAfterSwap=$($swap.v2LiveAfterSwap) ; v1ReclaimedOnZero=$($swap.v1ReclaimedOnZero)"
Assert ([bool]$swap.v1Served)         "slot v1 loaded and served (1)"
Assert ([bool]$swap.v2Served)         "slot v2 loaded and served (2)"
Assert ([bool]$swap.v2LiveAfterSwap)  "v2 still serves after v1 is freed (the swap held)"
Assert ([bool]$swap.v1ReclaimedOnZero) "v1's ALC reclaimed when its refcount hit zero (free-on-zero on code)"

# B — the loader leaves no residual owner (free-on-zero cascaded to \Slots)
$residual = $V::GetOwner('\Slots')
Write-Host "B  residue: \Slots owner after the cycle = $(if($residual){'present'}else{'none'})"
Assert ($null -eq $residual) "no \Slots owner left behind — the cycle reclaimed itself"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — the VOM loads code as a refcounted handle and frees it on zero; a slot swap drops the old slot deterministically (trigger), reclamation GC-timed until the GC is removed (invariant 7)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.vom.slot-swap'; pass=$pass
    v1Value=$swap.v1Value; v2Value=$swap.v2Value
    v1ReclaimedOnZero=[bool]$swap.v1ReclaimedOnZero
    verdict=$(if($pass){'code slot = refcounted VOM handle; swap + free-on-zero on code, no residual owner'}else{'see failures'})
}
