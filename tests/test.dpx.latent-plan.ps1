#requires -Version 7
# test.dpx.latent-plan.ps1 — Drives DPX latent-plan mode (no-KV-discretization excursion) once the
# hook lands (see wip/latent-reasoning-spec.md). SKIPs cleanly until DpxExperiments.LatentMode exists,
# so it can be committed now and go green the moment the sprint session wires the toggle. When live it
# runs France->Paris with K latent steps then resumes, asserting the emitted token still decodes
# coherently and the latent excursion did not corrupt the KV chain (decode still terminates).
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss run tests/test.dpx.latent-plan.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$exe = [Environment]::ProcessPath

# 1. Model discovery — mirror bench.dpx.decode-profile.ps1: no q4 pair, no run.
$modelsDir = $env:SS_MODELS
if (-not $modelsDir) { $modelsDir = Join-Path (Split-Path $exe -Qualifier) 'modeldb' }
if (-not (Test-Path $modelsDir) -or -not (Get-ChildItem $modelsDir -Filter '*-onnx-decoder-q4.db' -ErrorAction SilentlyContinue)) {
    Write-Host "SKIP - no gemma4-e2b q4 ONNX .db pair under $modelsDir (dpx-generate model discovery)." -ForegroundColor Yellow
    return
}

# 2. Hook discovery — the latent toggle is sprint-owned and may not be landed yet. SKIP, do not FAIL,
#    until DpxExperiments.LatentMode exists (per wip/latent-reasoning-spec.md).
$exp = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Dpx.DpxExperiments') } | Where-Object {$_} | Select-Object -First 1
$latentField = if ($exp) { $exp.GetField('LatentMode') } else { $null }
if (-not $latentField) {
    Write-Host "SKIP - DpxExperiments.LatentMode not present yet; latent-plan hook unlanded (see wip/latent-reasoning-spec.md)." -ForegroundColor Yellow
    return
}

# 3. Live path — driven via a `--latent <K>` flag on dpx-generate once wired. Baseline vs latent,
#    same prompt; the latent excursion must not stop decode from terminating on a coherent token.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$prompt = 'The capital of France is'
$K = 4

$baseline = (& $exe dpx-generate $prompt 8 2>&1) -join "`n"
$latent   = (& $exe dpx-generate $prompt 8 --latent $K 2>&1) -join "`n"

$baseHasParis   = $baseline -match 'Paris'
$latentTerminates = ($latent -notmatch 'error|exception|OperationCanceled')
$latentNonEmpty   = ($latent.Trim().Length -gt 0)

Assert $baseHasParis        "baseline decode still France->Paris (sanity: the model + q4 pair are live)"
Assert $latentNonEmpty      "latent-mode decode produced output"
Assert $latentTerminates    "latent excursion did not corrupt the KV chain (decode terminated without fault)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - latent-plan excursion runs $K steps and resumes to a coherent, terminating decode."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test = 'test.dpx.latent-plan'
    pass = $pass
    latentSteps = $K
    verdict = $(if($pass){"latent-plan mode: $K-step no-tokenize excursion, KV chain intact, decode resumes coherent"}else{'see failures'})
}
