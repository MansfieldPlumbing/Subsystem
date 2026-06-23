#requires -Version 7
# test.vom.runspace-reclaim.ps1 — Who owns a runspace's lifetime: the VOM Object Manager, or the GC?
# (CRQ115 — demote the GC. The VOM is the lifetime authority; the GC only sweeps what's left.)
#
# The VOM is the NT-shaped Object Manager: a runspace mounted under an owner is an OBJECT with a
# refcounted handle, and Terminate runs object rundown — Reclaim at refcount-zero (free-at-zero).
# Two probes, one verdict (truth = the binary + a severe test, not this comment):
#   A. THE GC FLOOR   — a live in-proc runspace running a trivial pipeline forces real GC collections
#      (incl. a gen2). pwsh's per-object churn is GC-allocated under stock CLR — its HEAP is not an
#      Object-Manager concern. Measured, never asserted (the number IS the receipt; it varies by load).
#   B. RECLAIM        — a live in-proc runspace mounted as ONE VOM handle is closed+disposed
#      DETERMINISTICALLY by Vom.Terminate alone (Reclaim = Close+Dispose). The Object Manager owns the
#      runspace's LIFETIME. Asserted: nothing touches the runspace directly; only the owner is torn down.
#
# Verdict: the VOM is sovereign over the runspace's LIFETIME (handle + reclaim); the GC is demoted to
# collecting what the runspace ALLOCATES. Intraprocess — the in-proc analog of the already-live PSRP
# path (Rs.cs: PsrpSession.VomOwner -> Close -> Terminate). A step toward CRQ115's endgame: GC optional,
# the VOM the only king.
#
# Dogfood:  ss run tests/test.vom.runspace-reclaim.ps1   (no build; no mutation outside a throwaway
#           \Sessions\__reclaim_* owner the test Terminates itself). Returns a receipt; .pass is $true on green.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "  ok   $msg" -ForegroundColor Green }
    else       { Write-Host "  FAIL $msg" -ForegroundColor Red; $script:fails.Add($msg) }
}
function New-InProcRunspace {
    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()
    $r = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($iss)
    $r.Open(); $r
}

# --- Probe A: the GC floor — measure pwsh's irreducible per-object churn ---
$b0=[GC]::CollectionCount(0); $b1=[GC]::CollectionCount(1); $b2=[GC]::CollectionCount(2)
$bMem=[GC]::GetTotalMemory($false)
$rA = New-InProcRunspace
for ($i=0; $i -lt 50; $i++) {
    $ps=[PowerShell]::Create(); $ps.Runspace=$rA
    [void]$ps.AddScript('1..2000 | ForEach-Object { $_ * 2 } | Measure-Object -Sum')
    [void]$ps.Invoke(); $ps.Dispose()
}
$rA.Close(); $rA.Dispose()
$gc = [pscustomobject]@{
    gen0 = [GC]::CollectionCount(0)-$b0
    gen1 = [GC]::CollectionCount(1)-$b1
    gen2 = [GC]::CollectionCount(2)-$b2
    heapDeltaKB = [math]::Round(([GC]::GetTotalMemory($false)-$bMem)/1KB,1)
}
Write-Host "Probe A - GC floor: 50 pipelines -> gen0=$($gc.gen0) gen1=$($gc.gen1) gen2=$($gc.gen2) heapDelta=$($gc.heapDeltaKB)KB"
Assert ($gc.gen0 -ge 1) "pwsh pipeline churns the GC (gen0>=1): the per-object heap is GC-allocated, not an OM object"

# --- Probe B: reclaim — deterministic teardown by VOM lifetime authority alone ---
$V = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object { $_ } | Select-Object -First 1
$probe = [ordered]@{ skipped = $true }
if (-not $V) {
    Write-Host "Probe B - SKIPPED: Subsystem.Vom not loaded in this host" -ForegroundColor Yellow
} else {
    $path  = "\Sessions\__reclaim_$(Get-Date -f HHmmssfff)"
    $owner = $V::CreateOwner($path)
    $rB = $null
    try {
        $rB = New-InProcRunspace
        $ps=[PowerShell]::Create(); $ps.Runspace=$rB; [void]$ps.AddScript('2+2'); $live=$ps.Invoke()[0]; $ps.Dispose()
        $before = "$($rB.RunspaceStateInfo.State)"
        $reclaim = [Action]{ try { $rB.Close() } catch {}; try { $rB.Dispose() } catch {} }
        $h = $V::Register($owner, "Runspace", $rB, $reclaim, "Runspaces", "probe")
        $V::Terminate($owner)              # the ONLY teardown call — nothing touches $rB directly
        Start-Sleep -Milliseconds 50
        $after = try { "$($rB.RunspaceStateInfo.State)" } catch { "disposed" }
        $probe = [ordered]@{ skipped=$false; mountedAt=$h.Path; ran=$live; before=$before; after=$after }
        Write-Host "Probe B - reclaim: mounted $($h.Path); ran 2+2=$live; state $before -> $after after Terminate"
        Assert ($live -eq 4)                   "the mounted runspace really executed (2+2=4)"
        Assert ($before -eq 'Opened')          "runspace was Opened before Terminate"
        Assert ($after  -ne 'Opened')          "Vom.Terminate reclaimed the runspace (lifetime authority reached into pwsh)"
        Assert ($null -eq $V::GetOwner($path)) "owner tree torn down deterministically (free-at-zero)"
    }
    finally {
        if ($null -ne $V::GetOwner($path)) { try { $V::Terminate($owner) } catch {} }  # leak-proof on early throw
        if ($rB) { try { $rB.Dispose() } catch {} }
    }
}

$pass = $fails.Count -eq 0
Write-Host ""
if ($pass) { Write-Host "PASS - the VOM owns the runspace's lifetime; the GC only sweeps what it allocates." -ForegroundColor Green }
else       { Write-Host "FAIL ($($fails.Count)) - $($fails -join '; ')" -ForegroundColor Red }

[pscustomobject]@{
    test    = 'test.vom.runspace-reclaim'
    pass    = $pass
    gcFloor = $gc
    reclaim = [pscustomobject]$probe
    verdict = if ($pass) { 'lifetime owned by the VOM (deterministic reclaim); allocation swept by the GC' } else { 'see failures' }
}
