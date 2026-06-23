#requires -Version 7
# test.vom.quota.ps1 — Proves per-owner quota ACCOUNTING and its honest Phase-1 state: ADVISORY
# (counted + logged, NOT enforced). The VOM tracks CurrentBytes/Elements against the owner's budget;
# exceeding it today is allowed (the enforcement flip is a later phase — the isolation work, CRQ128).
# Authority = the binary; this comment is not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.quota.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__quota_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p, [long]2MB, 100)            # small budget on purpose
try {
    $threw = $false
    try { 1..4 | ForEach-Object { [void]$V::Alloc($o, 1MB) } } catch { $threw = $true }   # 4MB > 2MB budget
    Write-Host "quota: max=$($o.MaxBytes) current=$($o.CurrentBytes) elements=$($o.CurrentElements) threw=$threw"
    Assert (-not $threw)                       "exceeding the quota does NOT throw (Phase-1 advisory)"
    Assert ($o.CurrentElements -eq 4)          "element count tracked exactly (4)"
    Assert ($o.CurrentBytes -ge 4MB)           "byte count tracked (>= 4MB)"
    Assert ($o.CurrentBytes -gt $o.MaxBytes)   "advisory: over-budget is observed but allowed"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — quota is accounted per owner and ADVISORY in Phase 1 (tracked, not enforced)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.quota'; pass=$pass; maxBytes=$o.MaxBytes; currentBytes=$o.CurrentBytes; verdict=$(if($pass){'quota counted; advisory (enforcement is the later isolation phase)'}else{'see failures'}) }
