#requires -Version 7
# test.vom.refcount.ps1 — Proves free-on-zero for a native region (COLD-START: "free-on-zero").
# Authority = the binary; this comment is not. Open/Close drive the refcount; the LAST Close at zero
# fires Reclaim (NativeMemory.AlignedFree) and the bytes return. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.refcount.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__rc_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
try {
    $h = $V::Alloc($o, 1MB)
    $bytesAfterAlloc = $o.CurrentBytes
    [void]$V::Open($o, $h.Path)                 # caller enters -> refcount 2
    $closed1 = $V::Close($o, $h.Path)           # refcount 1 -> NOT freed
    $stillThere = $o.Handles.IsValid($h.Id)
    $closed2 = $V::Close($o, $h.Path)           # refcount 0 -> freed (Reclaim)
    $gone = -not $o.Handles.IsValid($h.Id)
    $bytesAfterFree = $o.CurrentBytes
    Write-Host "refcount: alloc=$bytesAfterAlloc B; close@2=$closed1 (still=$stillThere); close@1=$closed2 (gone=$gone); bytesAfterFree=$bytesAfterFree"
    Assert ($bytesAfterAlloc -ge 1MB)  "Alloc accounted >= 1MB native"
    Assert (-not $closed1)             "Close at refcount 2->1 does NOT free (returns false)"
    Assert $stillThere                 "handle still resolvable while refcount > 0"
    Assert $closed2                    "Close at refcount 1->0 frees (returns true)"
    Assert $gone                       "freed handle is gone from the namespace"
    Assert ($bytesAfterFree -eq 0)     "native bytes reclaimed to zero on free-at-zero"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — free-on-zero: a native region frees deterministically when its last reference Closes."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.refcount'; pass=$pass; verdict=$(if($pass){'Open/Close refcount; free-at-zero reclaims native bytes'}else{'see failures'}) }
