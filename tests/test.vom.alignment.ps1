#requires -Version 7
# test.vom.alignment.ps1 — Proves the "256-region" half of the primitive: every Alloc is padded to a
# 256-byte stride and the backing pointer is 256-aligned (row-pitch / DMA / cache-line ready).
# Authority = the binary; this comment is not. Dot-source-safe; throwaway owner only.
#   Dogfood:  ss run tests/test.vom.alignment.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$p = "\Sessions\__align_$(Get-Date -f HHmmssfff)"
$o = $V::CreateOwner($p)
try {
    $allPadded = $true; $allAligned = $true; $allCover = $true
    foreach ($size in 1, 100, 1000, (1MB + 1)) {
        $h = $V::Alloc($o, $size)
        if (($h.ByteCount % 256) -ne 0)            { $allPadded = $false }
        if (($h.Resource.ToInt64() % 256) -ne 0)   { $allAligned = $false }
        if ($h.ByteCount -lt $size)                { $allCover = $false }
        Write-Host "  size=$size -> byteCount=$($h.ByteCount) ptr%256=$($h.Resource.ToInt64() % 256)"
    }
    Assert $allPadded  "every ByteCount is a multiple of 256 (padded stride)"
    Assert $allAligned "every backing pointer is 256-aligned"
    Assert $allCover   "every padded region covers the requested size"
} finally { if ($null -ne $V::GetOwner($p)) { $V::Terminate($o) } }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — 256-byte alignment: padded stride + aligned pointer on every Alloc."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='test.vom.alignment'; pass=$pass; verdict=$(if($pass){'256-aligned regions, padded stride'}else{'see failures'}) }
