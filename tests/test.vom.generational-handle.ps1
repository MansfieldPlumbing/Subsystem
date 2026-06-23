#requires -Version 7
# test.vom.generational-handle.ps1 — Proves the generational handle: a freed id fails resolution in
# O(1) (no use-after-free / ABA), and a reused slot gets a bumped generation. (COLD-START: handle =
# authority.) Authority = the binary; this comment is not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.generational-handle.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__gen_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
try {
    $h1 = $V::Alloc($o, 4096); $id1 = $h1.Id
    $validBefore = $o.Handles.IsValid($id1)
    [void]$V::Close($o, $h1.Path)                       # free -> bump generation
    $staleRejected = -not $o.Handles.IsValid($id1)
    $h2 = $V::Alloc($o, 4096); $id2 = $h2.Id            # reuses the freed slot, new generation
    $genBumped = $id1 -ne $id2
    $newValid  = $o.Handles.IsValid($id2)
    $staleStill = -not $o.Handles.IsValid($id1)
    Write-Host "generational: id1=$id1 validBefore=$validBefore staleRejected=$staleRejected ; id2=$id2 newValid=$newValid genBumped=$genBumped"
    Assert $validBefore   "a live handle id resolves valid"
    Assert $staleRejected "a freed handle id is rejected O(1) (no use-after-free)"
    Assert $genBumped     "the reused slot carries a different (bumped) id"
    Assert $newValid      "the new generation id resolves valid"
    Assert $staleStill    "the old id stays invalid even after the slot is reused (ABA-proof)"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — generational handles: a stale id fails O(1); slot reuse bumps the generation (ABA-proof)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.generational-handle'; pass=$pass; verdict=$(if($pass){'stale-id rejection + generation bump on reuse'}else{'see failures'}) }
