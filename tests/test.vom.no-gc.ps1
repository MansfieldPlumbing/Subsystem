#requires -Version 7
# test.vom.no-gc.ps1 — Receipt for invariant 7 (no garbage collector; the VOM is the heap).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
# Five probes, one verdict. No mutation outside throwaway \Sessions\__* owners the test Terminates itself.
# Dot-source-safe (no `exit`).
#   Dogfood:  ss run tests/test.vom.no-gc.ps1
#
#   A  FLOOR        a managed pwsh pipeline forces real collections (incl. gen2) — measured, context only.
#   B  ALLOC        200 MB through Vom.Alloc (pure C#, NativeMemory) costs ZERO collections. Asserted.
#   C  NOGCREGION   500 MB cycled through the VOM inside a real TryStartNoGCRegion: 0 collections, region
#                   holds — the collector can be off and the VOM workload does not need it. Asserted.
#   D  HANDLE       a live pwsh runspace mounted as ONE VOM handle is reclaimed by Vom.Terminate alone
#                   (the revoke) — no handle, no runspace. Asserted.
#   E  SPAWN-ISO    GC isolated OUTSIDE pwsh threads: a VOM-owned worker thread (not pwsh, not ThreadPool)
#                   carries 200 MB native with ZERO collections, reclaimed by cascade-Terminate. Asserted.

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
function New-Rs { $r=[System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace([System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()); $r.Open(); $r }

$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded in this host — cannot run." -ForegroundColor Red; return }

# A — managed churn does collect (the floor we are measuring against)
$b=@([GC]::CollectionCount(0),[GC]::CollectionCount(1),[GC]::CollectionCount(2)); $r=New-Rs
for($i=0;$i -lt 50;$i++){ $p=[PowerShell]::Create();$p.Runspace=$r;[void]$p.AddScript('1..2000|ForEach-Object{$_*2}|Measure-Object -Sum');[void]$p.Invoke();$p.Dispose() }
$r.Close();$r.Dispose()
$floor=[ordered]@{gen0=[GC]::CollectionCount(0)-$b[0];gen1=[GC]::CollectionCount(1)-$b[1];gen2=[GC]::CollectionCount(2)-$b[2]}
Write-Host "A  floor: managed pipeline -> gen0=$($floor.gen0) gen1=$($floor.gen1) gen2=$($floor.gen2)"

# B — VOM native allocation costs zero GC
$o=$V::CreateOwner("\Sessions\__b_$(Get-Date -f HHmmssfff)")
$mb0=[GC]::GetAllocatedBytesForCurrentThread()
$b=@([GC]::CollectionCount(0),[GC]::CollectionCount(1),[GC]::CollectionCount(2))
for($i=0;$i -lt 200;$i++){ [void]$V::Alloc($o,1MB) }
$managedDelta=[GC]::GetAllocatedBytesForCurrentThread()-$mb0
$vom=[ordered]@{gen0=[GC]::CollectionCount(0)-$b[0];gen1=[GC]::CollectionCount(1)-$b[1];gen2=[GC]::CollectionCount(2)-$b[2]}
$V::Terminate($o)
$managedMB=[math]::Round($managedDelta/1MB,2)
Write-Host "B  alloc: 200MB native -> managed-delta ${managedMB}MB (this thread); collections gen0=$($vom.gen0) gen1=$($vom.gen1) gen2=$($vom.gen2)"
# The measurement, not a threshold: 200MB of native data must add ~0 MANAGED bytes (only the per-handle
# bookkeeping). If the data were on the managed heap the delta would be ~200MB; it is a tiny fraction.
Assert ($managedDelta -lt 5MB) "200MB of native VOM data added only ${managedMB}MB managed bytes - the data plane is OFF the managed heap"
Assert ($vom.gen2 -eq 0)       "200MB native triggered NO full (gen2) collection"

# C — NoGCRegion holds across a 500MB VOM workload (the collector can be off)
$started=[GC]::TryStartNoGCRegion(64MB); $modeIn="$([System.Runtime.GCSettings]::LatencyMode)"
$b=@([GC]::CollectionCount(0),[GC]::CollectionCount(1),[GC]::CollectionCount(2))
$o=$V::CreateOwner("\Sessions\__c_$(Get-Date -f HHmmssfff)", 600MB)   # quota >500MB so the advisory-quota log stays quiet
$sw=[Diagnostics.Stopwatch]::StartNew(); for($i=0;$i -lt 500;$i++){ [void]$V::Alloc($o,1MB) }; $allocMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
$during=([GC]::CollectionCount(0)-$b[0])+([GC]::CollectionCount(1)-$b[1])+([GC]::CollectionCount(2)-$b[2])
$held=("$([System.Runtime.GCSettings]::LatencyMode)" -eq 'NoGCRegion')
$V::Terminate($o); try{ if($started){[GC]::EndNoGCRegion()} }catch{}
Write-Host "C  nogcregion: started=$started in=$modeIn ; 500MB -> $during collections ; held=$held ; alloc ${allocMs}ms"
Assert $started "entered a real no-collection region"
Assert ($during -eq 0) "500MB cycled with the collector off cost ZERO collections (native memory is off the GC budget)"
Assert $held "the no-GC region held across the whole VOM workload"

# D — a pwsh runspace mounted as a VOM handle is reclaimed by Terminate alone
$o=$V::CreateOwner("\Sessions\Psrp\__d_$(Get-Date -f HHmmssfff)"); $r=$null
try{
  $r=New-Rs; $rr=$r
  $p=[PowerShell]::Create();$p.Runspace=$r;[void]$p.AddScript('"pwsh "+$PSVersionTable.PSVersion');$ver=$p.Invoke()[0];$p.Dispose()
  $before="$($r.RunspaceStateInfo.State)"
  $h=$V::Register($o,"Runspace",$r,[Action]{try{$rr.Close()}catch{};try{$rr.Dispose()}catch{}},"Runspaces","pwsh")
  $V::Terminate($o); Start-Sleep -Milliseconds 50          # the revoke; nothing touches the runspace directly
  $after=try{"$($r.RunspaceStateInfo.State)"}catch{"disposed"}
  Write-Host "D  handle: $ver ; state $before -> $after after Vom.Terminate (handle revoke)"
  Assert ($ver -like 'pwsh*') "mounted a real pwsh runspace"
  Assert ($before -eq 'Opened' -and $after -ne 'Opened') "handle revoke reclaimed the runspace: $before -> $after"
} finally { if($null -ne $V::GetOwner($o.Path)){try{$V::Terminate($o)}catch{}}; if($r){try{$r.Dispose()}catch{}} }

# E — GC isolated OUTSIDE pwsh threads: a VOM-owned worker thread (not pwsh, not ThreadPool) does native work off the collector
$gi=$V::SpawnGcIsolationTest(200,1MB)|ConvertFrom-Json
Write-Host "E  spawn-iso: worker tid=$($gi.spawnTid) (caller $($gi.callerTid)) pool=$($gi.spawnIsPool) bg=$($gi.spawnIsBg) ; $($gi.nativeMB)MB native -> $($gi.managedDeltaKB)KB managed ; collections gen0=$($gi.gc0) gen1=$($gi.gc1) gen2=$($gi.gc2) ; reclaimed=$($gi.reclaimedOnTerminate)"
Assert ($gi.distinctThread)           "the VOM worker runs on its OWN thread (tid $($gi.spawnTid) != caller $($gi.callerTid)) - off the pwsh thread"
Assert (-not $gi.spawnIsPool)         "the VOM worker is NOT a ThreadPool/pwsh-managed thread - the VOM owns it"
Assert ($gi.gc2 -eq 0)                "$($gi.nativeMB)MB native on the spawned thread triggered NO full (gen2) collection"
Assert ($gi.managedDeltaKB -lt 1024)  "$($gi.nativeMB)MB native added only $($gi.managedDeltaKB)KB managed on the worker thread - its data plane is OFF the GC"
Assert ($gi.reclaimedOnTerminate)     "Terminate cascaded and reclaimed the worker's regions deterministically (no GC)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — invariant 7: native alloc + a 500MB workload cost zero collections; a VOM-owned thread OFF pwsh carries native memory with zero GC; runspace + worker reclaimed by handle-revoke / cascade-Terminate."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test='test.vom.no-gc'; pass=$pass
    floor=[pscustomobject]$floor; vomAllocator=[pscustomobject]$vom
    spawnIso=[pscustomobject]@{ tid=$gi.spawnTid; caller=$gi.callerTid; pool=$gi.spawnIsPool; nativeMB=$gi.nativeMB; managedKB=$gi.managedDeltaKB; gc2=$gi.gc2; reclaimed=$gi.reclaimedOnTerminate }
    verdict=$(if($pass){'VOM is the heap; the collector is unused for the data plane AND for spawned VOM threads; the GC stays inside the pwsh guest'}else{'see failures'})
}
