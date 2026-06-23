#requires -Version 7
# test.vom.dropprefix.ps1 — Proves bulk deterministic reclaim: DropPrefix frees every handle under a
# namespace prefix in one pass (the substrate Terminate rides). Authority = the binary; this comment is
# not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.dropprefix.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__drop_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
try {
    1..5 | ForEach-Object { [void]$V::Alloc($o, 1MB) }
    $before = $o.Handles.LiveCount
    $r = $V::DropPrefix($p)                      # (handles, bytes)
    $n = $r.Item1; $bytes = $r.Item2
    $after = $o.Handles.LiveCount
    Write-Host "dropprefix: before=$before -> reclaimed handles=$n bytes=$bytes -> after=$after"
    Assert ($before -eq 5)        "5 handles allocated under the prefix"
    Assert ($n -eq 5)             "DropPrefix reclaimed all 5 in one pass"
    Assert ($bytes -ge 5MB)       "DropPrefix reported the reclaimed bytes (>= 5MB)"
    Assert ($after -eq 0)         "no handles remain under the prefix"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — DropPrefix: bulk deterministic reclaim of every handle under a prefix."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.dropprefix'; pass=$pass; verdict=$(if($pass){'one-pass prefix reclaim, counted'}else{'see failures'}) }
