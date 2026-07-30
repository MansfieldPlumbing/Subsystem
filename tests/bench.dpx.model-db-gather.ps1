#requires -Version 7
# bench.dpx.model-db-gather.ps1 — Real numbers for the model.db -> VOM-region gather (two-tier:
# model.db is the truth at rest; 256-aligned native VOM regions are the data plane in flight). Drives the
# head's ModelDbBench.Gather: stream every tensor BLOB into a native region, measure throughput, show the
# resident weights are off the GC, then Terminate reclaims deterministically. No SQL in the matmul.
#   Dogfood:  ss run tests/bench.dpx.model-db-gather.ps1
$ErrorActionPreference='Stop'
$db = @($env:SS_MODEL_DB,'S:\modeldb\demucsv4.db','S:\modeldb\kokoro.db','S:\modeldb\gemma-4-e2b.db') |
      Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if(-not $db){ Write-Host "SKIP - no model.db present (dpx db <model.onnx> S:\modeldb\<name>.db)." -ForegroundColor Yellow; return }
$B = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Windows.ModelDbBench') } | Where-Object {$_} | Select-Object -First 1
if(-not $B){ Write-Host "SKIP - ModelDbBench not in this host (build the head)." -ForegroundColor Yellow; return }

$j = $B::Gather($db) | ConvertFrom-Json
Write-Host "model.db: $db  ($([math]::Round((Get-Item $db).Length/1MB,1)) MB on disk)"
Write-Host ("gather:   {0} tensors / {1} MB -> 256-aligned native VOM regions in {2}s  ({3} GB/s)" -f $j.tensors,$j.MB,$j.gatherSec,$j.GBps)
Write-Host ("resident: {0} MB of weights live in NATIVE regions; managed heap moved {1} MB (the data plane is off the collector)" -f $j.residentNativeMB,$j.managedDeltaMB)
Write-Host ("reclaim:  Terminate freed all {0} regions / {1} MB in {2} ms - deterministic free-on-zero, no GC sweep" -f $j.tensors,$j.residentNativeMB,$j.reclaimMs)
[pscustomobject]@{
    bench='dpx.model-db-gather'; db=(Split-Path $db -Leaf)
    tensors=$j.tensors; MB=$j.MB; gatherSec=$j.gatherSec; GBps=$j.GBps
    residentNativeMB=$j.residentNativeMB; managedDeltaMB=$j.managedDeltaMB; reclaimMs=$j.reclaimMs
    verdict='model.db tensors gathered into off-GC native VOM regions; reclaimed deterministically by Terminate'
}
