#requires -Version 7
# bench.vom.throughput.ps1 — BENCHMARK (not just green): the VOM data-plane rates. Records GB/s for
# native alloc, per-handle free-on-zero, and bulk DropPrefix reclaim, plus handle and fence op rates.
# The numbers ARE the receipt; the sanity floors only keep it from silently regressing to zero.
# Authority = the binary; this comment is not. Dot-source-safe; throwaway owners only.
#   Dogfood:  ss run tests/bench.vom.throughput.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }
$V = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if(-not $V){ Write-Host "Subsystem.Vom not loaded." -ForegroundColor Red; return }

$N = 256; $chunk = 1MB; $totalGB = ($N * $chunk) / 1GB
$sw = [Diagnostics.Stopwatch]::new()

# --- alloc throughput ---
$p = "\Sessions\__bench_$(Get-Date -f HHmmssfff)"; $o = $V::CreateOwner($p)
$paths = [string[]]::new($N)
$sw.Restart(); for ($i=0; $i -lt $N; $i++) { $paths[$i] = ($V::Alloc($o, $chunk)).Path }; $sw.Stop()
$allocGBs = [math]::Round($totalGB / $sw.Elapsed.TotalSeconds, 2)

# --- per-handle free-on-zero throughput ---
$sw.Restart(); foreach ($pp in $paths) { [void]$V::Close($o, $pp) }; $sw.Stop()
$freeGBs = [math]::Round($totalGB / $sw.Elapsed.TotalSeconds, 2)
$V::Terminate($o)

# --- bulk reclaim (DropPrefix) throughput ---
$p2 = "\Sessions\__bench2_$(Get-Date -f HHmmssfff)"; $o2 = $V::CreateOwner($p2)
for ($i=0; $i -lt $N; $i++) { [void]$V::Alloc($o2, $chunk) }
$sw.Restart(); [void]$V::DropPrefix($p2); $sw.Stop()
$bulkGBs = [math]::Round($totalGB / $sw.Elapsed.TotalSeconds, 2)
$V::Terminate($o2)

# --- handle op rate (Open/Close on one live handle) ---
$p3 = "\Sessions\__bench3_$(Get-Date -f HHmmssfff)"; $o3 = $V::CreateOwner($p3)
$h = $V::Alloc($o3, $chunk); $ops = 200000
$sw.Restart(); for ($i=0; $i -lt $ops; $i++) { [void]$V::Open($o3, $h.Path); [void]$V::Close($o3, $h.Path) }; $sw.Stop()
$handleMops = [math]::Round((2 * $ops) / $sw.Elapsed.TotalSeconds / 1e6, 2)
$V::Terminate($o3)

# --- fence op rate (Signal + read) ---
$f = [Subsystem.Vom.CpuFence]::new(); $fops = 500000
$sw.Restart(); for ($i=1; $i -le $fops; $i++) { $f.Signal($i); [void]$f.CompletedValue }; $sw.Stop()
$fenceMops = [math]::Round((2 * $fops) / $sw.Elapsed.TotalSeconds / 1e6, 2)

Write-Host ""
Write-Host ("VOM data plane: alloc {0} GB/s | free-on-zero {1} GB/s | bulk-reclaim {2} GB/s | handle {3} M-ops/s | fence {4} M-ops/s" -f $allocGBs,$freeGBs,$bulkGBs,$handleMops,$fenceMops)
Assert ($allocGBs -gt 0.1)     "native alloc throughput recorded"
Assert ($freeGBs  -gt 0.1)     "free-on-zero throughput recorded"
Assert ($bulkGBs  -ge $freeGBs) "bulk DropPrefix reclaim is at least as fast as per-handle free"
Assert ($handleMops -gt 0.1)   "Open/Close handle-op rate recorded"
Assert ($fenceMops  -gt 0.1)   "fence Signal/read rate recorded"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — VOM data plane benchmarked; the numbers above are the receipt."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='bench.vom.throughput'; pass=$pass; allocGBs=$allocGBs; freeOnZeroGBs=$freeGBs; bulkReclaimGBs=$bulkGBs; handleMOpsSec=$handleMops; fenceMOpsSec=$fenceMops; verdict='VOM data-plane rates recorded' }
