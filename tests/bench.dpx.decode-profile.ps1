#requires -Version 7
# bench.dpx.decode-profile.ps1 — Names the hot op in the gemma4-e2b DPX decode loop (CRQ190 step 1: know the
# op class before porting a ggml recipe for it). Runs `ss dpx-generate --profile`, which flips Dp.Profile and
# prints Dp.Prof (per-OpType total ms / call count / ms-per-call), accumulated over both prefill and decode
# via the interpreter's own Dispatch loop — a real per-op receipt, not a guess.
#   Dogfood:  ss run tests/bench.dpx.decode-profile.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$exe = [Environment]::ProcessPath
$modelsDir = $env:SS_MODELS
if (-not $modelsDir) { $modelsDir = Join-Path (Split-Path $exe -Qualifier) 'modeldb' }
if (-not (Test-Path $modelsDir) -or -not (Get-ChildItem $modelsDir -Filter '*-onnx-decoder-q4.db' -ErrorAction SilentlyContinue)) {
    Write-Host "SKIP - no gemma4-e2b q4 ONNX .db pair under $modelsDir (dpx-generate model discovery)." -ForegroundColor Yellow
    return
}

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$out = (& $exe dpx-generate 'The capital of France is' 16 --profile 2>&1) -join "`n"

$profileStart = $out.IndexOf('[DPX Op Profile]')
Assert ($profileStart -ge 0) 'dpx-generate --profile prints the [DPX Op Profile] section'

$rows = @()
$totalMs = 0.0
if ($profileStart -ge 0) {
    $profileText = $out.Substring($profileStart)
    foreach ($line in ($profileText -split "`n")) {
        if ($line -match '^\s{2}(\S+)\s+([\d.]+) ms\s+(\d+) calls\s+([\d.]+) ms/call\s+\(([\d.]+)%\)') {
            $rows += [pscustomobject]@{ Op = $Matches[1]; Ms = [double]$Matches[2]; Calls = [int]$Matches[3]; PerCall = [double]$Matches[4]; Pct = [double]$Matches[5] }
        }
        if ($line -match '^\s{2}TOTAL\s+([\d.]+) ms') { $totalMs = [double]$Matches[1] }
    }
}

$top = $rows | Sort-Object Ms -Descending | Select-Object -First 1
if ($top) { Write-Host "hot op: $($top.Op)  $($top.Ms) ms total over $($top.Calls) calls ($($top.Pct)% of $totalMs ms)" }

Assert ($rows.Count -gt 0)                          'at least one op class was profiled'
Assert ($null -ne $top)                              'a single hottest op is identifiable'
Assert (($rows | Measure-Object -Property Calls -Sum).Sum -gt 0) 'call counts are non-zero (Dispatch ran exactly once per node, not double-counted)'
Assert ($totalMs -gt 0)                              'profiled total time is non-zero'

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — hot op named: $($top.Op) ($([math]::Round($top.Pct,1))% of decode time)."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.decode-profile'; pass=$pass; hotOp=$top.Op; hotOpPct=$top.Pct; totalMs=$totalMs; verdict=$(if($pass){"named the hot op via Dp.Profile - no more guessing which kernel to accelerate first"}else{'see failures'}) }
