#requires -Version 7
# test.vom.register-managed.ps1 — Proves a MANAGED object becomes a first-class refcounted VOM handle
# (Register): resolvable via its GCHandle, and its close-procedure (Reclaim) fires on free-at-zero.
# This is how a thread / Sub-VOM / any object becomes an enumerable, reclaimed handle. Authority = the
# binary; this comment is not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.register-managed.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__reg_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
$script:reclaimed = $false
try {
    $obj = [pscustomobject]@{ tag = 'vom-managed-probe' }
    $reclaim = [Action]{ $script:reclaimed = $true }
    $h = $V::Register($o, "TestManaged", $obj, $reclaim, "Objects", "mgd")
    $resolved = [System.Runtime.InteropServices.GCHandle]::FromIntPtr($h.Resource).Target
    $byteCount = $h.ByteCount
    $closed = $V::Close($o, $h.Path)                 # refcount 1 -> 0 -> Reclaim (onReclaim + GCHandle.Free)
    Write-Host "register: resolvedTag=$($resolved.tag) byteCount=$byteCount closed=$closed reclaimFired=$($script:reclaimed)"
    Assert ($resolved.tag -eq 'vom-managed-probe') "the managed object resolves through its handle (GCHandle)"
    Assert ($byteCount -eq 0)                        "a managed entry carries no native bytes (ByteCount 0)"
    Assert $closed                                   "Close freed the handle at refcount zero"
    Assert $script:reclaimed                         "the close-procedure (Reclaim) fired on free-at-zero"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — a managed object is a refcounted VOM handle; its Reclaim runs on free-at-zero."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.register-managed'; pass=$pass; verdict=$(if($pass){'managed-object-as-handle, Reclaim on free-at-zero'}else{'see failures'}) }
