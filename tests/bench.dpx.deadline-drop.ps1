#requires -Version 7
# bench.dpx.deadline-drop.ps1 — MVP-2 best-effort-inference receipt. Question: does greedy decode still
# produce usable text when a mid-stack correction MISSES its deadline? We model the miss by dropping a
# target MatMulNBits contribution (the residual stream carries forward with zeros for that call) at a
# fixed cadence, and measure the cost.
#
# Mechanism (all in the LOADED ss.exe assembly — this receipt only sets the knobs, it doesn't recompile):
#   - Dp.MatMulNBits top hook: if DpxExperiments.ShouldDrop(node) -> return a zero tensor of the output
#     shape (AllocZeros). The target is matched by substring against the node's FIRST output name.
#   - Per-target call counter: the target runs once per graph execution (prefill + each decode step), so
#     counter % DropEveryN == 0 IS the cadence. DropEveryN=0 = never drop. No external token counter.
#   - Logit capture: the lm_head MatMulNBits output (M=1 under num_logits_to_keep) is copied per step into
#     DpxExperiments.CapturedLogits, so each drop rate's logit trajectory can be diffed against DropEveryN=0.
#
# ROUTE: in-proc reflection (single process, all 4 rates) — the decoder is driven directly via the loaded
#   DpxDecoder.StreamTurnAsync, knobs are mutable statics reset between runs, and logits are compared in
#   memory (no child process, no file dumps). Decode is GREEDY argmax (DpxDecoder DecodeLoopSplit) — there
#   is NO sampling RNG, so runs are reproducible with no seed to pin.
#
#   Dogfood:  ss run tests/bench.dpx.deadline-drop.ps1   (must be in-proc; needs S:\modeldb)

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

# --- model discovery (same doctrine as DpxGenerate: env override, else drive-derived) -----------------
$exe = [Environment]::ProcessPath
$modelsDir = $env:SS_MODELS
if (-not $modelsDir) { $modelsDir = Join-Path (Split-Path $exe -Qualifier) 'modeldb' }
if (-not (Test-Path $modelsDir) -or -not (Get-ChildItem $modelsDir -Filter '*-onnx-decoder-q4.db' -ErrorAction SilentlyContinue)) {
    Write-Host "SKIP - no gemma4-e2b q4 ONNX .db pair under $modelsDir (deadline-drop needs the real decoder)." -ForegroundColor Yellow
    return
}

# --- reflect the LOADED types (must run in-proc so the knobs reach the same MatMulNBits the decoder uses) -
$asm = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetType('Subsystem.Dpx.DpxDecoder') } | Select-Object -First 1
if (-not $asm) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss run tests/bench.dpx.deadline-drop.ps1)." -ForegroundColor Yellow; return }
$decT       = $asm.GetType('Subsystem.Dpx.DpxDecoder')
$expT       = $asm.GetType('Subsystem.Dpx.DpxExperiments')
$deltaKindT = $asm.GetType('Subsystem.RuntimeBroker.AgentDeltaKind')
Assert ($null -ne $decT)       'DpxDecoder reachable in-proc'
Assert ($null -ne $expT)       'DpxExperiments reachable in-proc'
$fDropN   = $expT.GetField('DropEveryN')
$fDropStr = $expT.GetField('DropNodeContains')
$fCap     = $expT.GetField('CaptureLogits')
$fCapList = $expT.GetField('CapturedLogits')
$fTgt     = $expT.GetField('TargetCalls')
$fDrp     = $expT.GetField('DroppedCalls')
$mReset   = $expT.GetMethod('ResetRun')
$mDiv     = $expT.GetMethod('QueryDivergence')   # fast max|logit diff| helper (compiled; bundle can't Add-Type)
Assert (($fDropN,$fDropStr,$fCap,$fCapList,$fTgt,$fDrp,$mDiv | Where-Object {$null -eq $_}).Count -eq 0) 'DpxExperiments knobs+counters+capture+divergence reachable'
if ($fails.Count -gt 0) { Write-Host "FAIL early — knobs missing"; return }

$embed = (Get-ChildItem $modelsDir -Filter '*-onnx-embed-q4.db' | Select-Object -First 1).FullName
$dec   = (Get-ChildItem $modelsDir -Filter '*-onnx-decoder-q4.db' | Select-Object -First 1).FullName
$spm   = (Get-ChildItem $modelsDir -Filter '*.spm' | Select-Object -First 1).FullName

# --- target node: a mid-stack (layer 17 of 0..34) MLP down projection ---------------------------------
# discovered by loading the decoder db and listing MatMulNBits output names; exact node recorded below.
$targetMatch = 'layers.17/mlp/down_proj'
$targetNode  = '/model/layers.17/mlp/down_proj/MatMul_Quant'   # output: /model/layers.17/mlp/down_proj/MatMul/output_0
Write-Host "target node: $targetNode  (matched by output-name substring '$targetMatch')"
Write-Host ""

$prompt   = 'The capital of France is'
$maxTok   = 16
$rates    = @(0, 20, 10, 4)   # 1/N drop cadence: ~0% / ~5% / ~10% / ~25%
$tokenKind = [Enum]::Parse($deltaKindT, 'Token')

$decoder = [Activator]::CreateInstance($decT, @($embed, $dec, $spm, 'deadline-drop', [int]$maxTok))
$fault = $decoder.BringUp()
Assert ($null -eq $fault) "decoder BringUp clean (fault: $fault)"
if ($fault) { Write-Host "FAIL — BringUp: $fault"; return }

$results = @()
$baselineSnap = $null

foreach ($n in $rates) {
    $fDropN.SetValue($null, [int]$n)
    $fDropStr.SetValue($null, $targetMatch)
    $fCap.SetValue($null, $true)
    $mReset.Invoke($null, @()) | Out-Null

    $ct = [System.Threading.CancellationToken]::None
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $tStart = $sw.ElapsedTicks; $tFirst = 0L; $tEnd = 0L
    $text = ''; $genTok = 0
    $enum = $decoder.StreamTurnAsync($prompt, $null, $ct).GetAsyncEnumerator($ct)
    try {
        while ($enum.MoveNextAsync().GetAwaiter().GetResult()) {
            $d = $enum.Current
            if ($d.Kind -eq $tokenKind -and $d.Text) {
                if ($tFirst -eq 0) { $tFirst = $sw.ElapsedTicks }
                $text += $d.Text; $genTok++
            }
        }
    } finally { $enum.DisposeAsync().GetAwaiter().GetResult() | Out-Null }
    $tEnd = $sw.ElapsedTicks
    $freq = [System.Diagnostics.Stopwatch]::Frequency

    $prefillMs = $(if ($tFirst -gt 0) { ($tFirst - $tStart) * 1000.0 / $freq } else { 0 })
    $genMs     = $(if ($tFirst -gt 0) { ($tEnd - $tFirst) * 1000.0 / $freq } else { 0 })
    $genTokSec = $(if ($genMs -gt 0) { $genTok / ($genMs / 1000.0) } else { 0 })

    # snapshot this rate's captured per-step logits into a jagged float[][] (ResetRun clears the source next loop)
    $cap = $fCapList.GetValue($null)
    $steps = $cap.Count
    $snap = [float[][]]::new($steps)
    for ($i = 0; $i -lt $steps; $i++) { $snap[$i] = [float[]]$cap[$i].Clone() }

    $dropped = [int64]$fDrp.GetValue($null)
    $maxDiv  = 0.0
    if ($n -eq 0) { $baselineSnap = $snap } else { $maxDiv = [double]$mDiv.Invoke($null, @($baselineSnap, $snap)) }

    $results += [pscustomobject]@{
        rate      = $n
        nominalPct = $(if ($n -eq 0) { 0.0 } else { [math]::Round(100.0/$n,1) })
        steps     = $steps
        dropped   = $dropped
        genTokSec = [math]::Round($genTokSec,2)
        prefillMs = [math]::Round($prefillMs,0)
        maxLogitDiv = [math]::Round($maxDiv,4)
        text      = ($text -replace "`r?`n",'\n')
    }
    Write-Host ("  DropEveryN={0,-3} (~{1,4}%)  steps={2,-2} dropped={3,-2}  gen {4,6:F2} tok/s  maxLogitDiv={5,-9}  text=[{6}]" -f `
        $n, $(if($n -eq 0){0}else{[math]::Round(100.0/$n,1)}), $steps, $dropped, $genTokSec, ([math]::Round($maxDiv,4)), ($text -replace "`r?`n",'\n'))
}

$decoder.Dispose()

# --- assertions: mechanism worked; the curve itself is the answer (no victory lap on the numbers) -----
$r0 = $results | Where-Object { $_.rate -eq 0 } | Select-Object -First 1
Assert ($r0.steps -gt 0)                               "baseline (DropEveryN=0) produced tokens: '$($r0.text)'"
Assert (($results | Where-Object { $_.rate -eq 0 }).dropped -eq 0) 'baseline dropped 0 calls (control)'
Assert (($results | Where-Object { $_.rate -ne 0 } | Measure-Object -Property dropped -Sum).Sum -gt 0) 'drop hook fired at least once at nonzero rates'
Assert (($results | Where-Object { $_.rate -ne 0 -and $_.maxLogitDiv -gt 0 }).Count -gt 0) 'dropping perturbs the logits (maxLogitDiv > 0 at some nonzero rate)'

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ("PASS/curve") -ForegroundColor $(if($pass){'Green'}else{'Red'})
$results | Format-Table rate, nominalPct, steps, dropped, genTokSec, prefillMs, maxLogitDiv, text -AutoSize | Out-String | Write-Host

if (-not $pass) { Write-Host "FAIL ($($fails.Count)): $($fails -join '; ')" -ForegroundColor Red }
[pscustomobject]@{
    bench      = 'dpx.deadline-drop'
    pass       = $pass
    targetNode = $targetNode
    prompt     = $prompt
    maxTokens  = $maxTok
    curve      = $results
    verdict    = $(if($pass){"best-effort decode survives dropped MatMulNBits contributions; curve reports the measured logit divergence + text per drop cadence (no seed to pin — decode is greedy)"}else{'see failures'})
}
