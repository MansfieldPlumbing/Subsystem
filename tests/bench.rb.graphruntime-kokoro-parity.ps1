#requires -Version 7
# bench.rb.graphruntime-kokoro-parity.ps1 — the ORT-free .NET ONNX interpreter (future Rb GraphRuntime leaf)
#   runs Kokoro-82M end-to-end and measures waveform parity vs the ORT oracle. The NUMBER is the point.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   What it proves (the real frontier, 2026-06-23): coverage is DONE (all 2463 nodes run, every op-type
#   implemented — the "implement Slice" charter is stale); the OPEN frontier is numerical parity, and the
#   residual divergence localizes to the vocoder source path (/decoder/decoder/generator/*).
#   Floor (regression guard): selftest passes AND all nodes run AND rmse stays <= the known baseline.
#   GOAL (open): VALIDATE => ALLCLOSE (max|diff| <= 1e-3) so ORT can be dropped.
#   Dogfood:  ss run tests/bench.rb.graphruntime-kokoro-parity.ps1
#
# Assets (external — this exercises the qnn-project standalone runtime, like test.staging.*):
#   exe   S:\qnn-project\workspace\onnx-interp\publish\dpx.exe   (build: S:\bin\dotnet\dotnet.exe publish -c Release -r win-x64 --self-contained -o publish)
#   model S:\reference\Kokoro-82M\onnx\model.onnx                    (HF onnx-community/Kokoro-82M-v1.0-ONNX, dynamic fp32)
#   io    S:\qnn-project\workspace\onnx-interp\io                    (input_ids/style/speed/oracle .bin + io\all per-node ORT dump)

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$exe   = 'S:\qnn-project\workspace\onnx-interp\publish\dpx.exe'
$model = 'S:\reference\Kokoro-82M\onnx\model.onnx'
$io    = 'S:\qnn-project\workspace\onnx-interp\io'
$RMSE_FLOOR = 3.0e-2   # regression guard just above the known 2.516e-2 baseline
$ALLCLOSE   = 1.0e-3   # the open parity goal

$assetsOk = (Test-Path $exe) -and (Test-Path $model) -and (Test-Path (Join-Path $io 'oracle.bin'))
Assert $assetsOk "assets present (exe + model + io\oracle.bin)"
if(-not $assetsOk){
  if(-not (Test-Path $exe))  { Write-Host "    missing exe   $exe   (publish the onnx-interp project)" -ForegroundColor Yellow }
  if(-not (Test-Path $model)){ Write-Host "    missing model $model (curl from HF onnx-community/Kokoro-82M-v1.0-ONNX/onnx/model.onnx)" -ForegroundColor Yellow }
  Write-Host "FAIL: assets missing — cannot run." -ForegroundColor Red
  return [pscustomobject]@{ test='bench.rb.graphruntime-kokoro-parity'; pass=$false; verdict='assets missing' }
}

# A) kernel math — the in-proc selftest (relu(X@W+B)); no model needed
$st = & $exe selftest 2>&1 | Out-String
Assert ($st -match 'PASS') "A selftest: kernel math (MatMul/Add/Relu) exact"

# B) coverage — every op-type in the kokoro graph is implemented (the stale 'implement Slice' charter)
$pb = & $exe probe $model 2>&1 | Out-String
$mImpl = [regex]::Match($pb,'implemented op-types=(\d+)/(\d+)')
$haveTypes = if($mImpl.Success){[int]$mImpl.Groups[1].Value}else{0}
$allTypes  = if($mImpl.Success){[int]$mImpl.Groups[2].Value}else{-1}
Write-Host ("    coverage: {0}/{1} op-types implemented" -f $haveTypes,$allTypes)
Assert ($haveTypes -eq $allTypes -and $allTypes -gt 0) "B coverage: all $allTypes op-types implemented (no MISSING)"

# C) parity — run the full graph on real inputs, diff the waveform vs ORT oracle.bin
$rn = & $exe run $model --inputs $io --compare (Join-Path $io 'all') 2>&1 | Out-String
$mRan  = [regex]::Match($rn,'RAN ALL (\d+) nodes')
$nodes = if($mRan.Success){[int]$mRan.Groups[1].Value}else{0}
Assert ($nodes -gt 0) "C end-to-end: RAN ALL $nodes nodes (graph completes)"

$mVal = [regex]::Match($rn,'VALIDATE vs oracle\.bin: len (\d+) vs (\d+)\s+max\|diff\|=([\d.eE+-]+)\s+rmse=([\d.eE+-]+)\s+=>\s+(\S+)')
$maxd = if($mVal.Success){[double]$mVal.Groups[3].Value}else{[double]::NaN}
$rmse = if($mVal.Success){[double]$mVal.Groups[4].Value}else{[double]::NaN}
$verd = if($mVal.Success){$mVal.Groups[5].Value}else{'?'}
$allclose = ($verd -eq 'ALLCLOSE') -or ($maxd -le $ALLCLOSE)
Write-Host ("    parity: max|diff|={0:E3}  rmse={1:E3}  ORT-verdict={2}" -f $maxd,$rmse,$verd)
Assert ($rmse -le $RMSE_FLOOR) ("C parity floor: rmse {0:E3} <= baseline {1:E3} (no regression)" -f $rmse,$RMSE_FLOOR)

# D) localize the open divergence — top energy-weighted node from the --compare map
$top = ($rn -split "`n" | Select-String -Pattern 'wrel=([\d.]+) corr=' | Select-Object -First 1).ToString().Trim()
if($top){ Write-Host "    top divergence: $top" }
$inGenerator = $top -match 'generator'
Assert ($inGenerator) "D frontier: worst divergence is in the vocoder generator path (where the fix belongs)"

$pass = $fails.Count -eq 0
Write-Host ""
$verdict = if($allclose){ "PARITY MET — ORT can be dropped" }
           elseif($pass){ "coverage DONE; parity OPEN — residual divergence in /decoder/decoder/generator/* (rmse {0:E3})" -f $rmse }
           else{ "see failures" }
Write-Host ($(if($pass){"PASS — $verdict"}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
  test='bench.rb.graphruntime-kokoro-parity'; pass=$pass
  opTypes="$haveTypes/$allTypes"; nodes=$nodes
  maxdiff=$maxd; rmse=$rmse; ortVerdict=$verd; allclose=$allclose
  topDivergence=$top; verdict=$verdict
}
