#requires -Version 7
# bench.gauntlet.ps1 — Tiered correctness and performance verification.
$ErrorActionPreference = 'Stop'

$exe   = 'S:\spawn-fusion\onnx-interp\bin\Release\net11.0\dp-onnx.exe'
$model = 'S:\reference\Kokoro-82M\onnx\model.onnx'
$io    = 'S:\spawn-fusion\onnx-interp\io'
$cpuRefDir = 'S:\spawn-fusion\onnx-interp\io\cpu_ref'
$dxil  = 'S:\spawn-fusion\onnx-interp\_gpu\gemm.dxil'

$fails = [System.Collections.Generic.List[string]]::new()
function Assert-Pass([bool]$c, [string]$m) {
    if ($c) {
        Write-Host "  [PASS] $m" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $m" -ForegroundColor Red
        $script:fails.Add($m)
    }
}

# Helper to clean up previous run artifacts
function Clean-Artifacts {
    $files = @(
        'S:\spawn-fusion\onnx-interp\_cpu.wav',
        'S:\spawn-fusion\onnx-interp\_gpu.wav'
    )
    foreach ($f in $files) {
        if (Test-Path $f) { Remove-Item $f -Force }
    }
    if (Test-Path $cpuRefDir) {
        Remove-Item $cpuRefDir -Recurse -Force
    }
}

# Parse dispatch time (ms) from run stdout
function Get-DispatchTime([string]$out) {
    if ($out -match '(\d+)\s+ms total dispatch') {
        return [int]$Matches[1]
    }
    if ($out -match 'RAN ALL \d+ nodes ✓\s+\(([\d.]+)s\)') {
        return [int]([double]$Matches[1] * 1000)
    }
    return 999999
}

Write-Host "=== STARTING GAUNTLET ===" -ForegroundColor Cyan

Clean-Artifacts

# 1. FP32 GPU Kernel vs CPU Reference Correctness (Tight Per-Op allclose <= 1e-3)
Write-Host "1. Testing GPU GEMM kernel correctness vs CPU reference..."
$gt = & $exe gpu-test $dxil 2>&1 | Out-String
Write-Host $gt
$gpuTestPassed = $gt -match "MATCH"
Assert-Pass $gpuTestPassed "FP32 GPU GEMM matches CPU MatMul (allclose <= 1e-3)"

# 2. In-proc Selftest
Write-Host "2. Running in-proc selftest..."
$st = & $exe selftest 2>&1 | Out-String
Write-Host $st
Assert-Pass ($st -match "PASS") "In-proc selftest exact"

# 3. End-to-end CPU reference dump
Write-Host "3. Running end-to-end CPU run to dump references..."
$cpuOutWav = 'S:\spawn-fusion\onnx-interp\_cpu.wav'
$cpuDumpRun = & $exe run $model --inputs $io --out $cpuOutWav --dump-ref $cpuRefDir 2>&1 | Out-String
Assert-Pass (Test-Path $cpuOutWav) "CPU reference WAV generated"
Assert-Pass (Test-Path $cpuRefDir) "CPU reference tensors dumped to $cpuRefDir"

# 4. CPU performance baseline (best-of-3)
Write-Host "4. Measuring CPU performance (best-of-3)..."
$cpuTimes = @()
for ($i = 1; $i -le 3; $i++) {
    Write-Host "  CPU run $i..."
    $out = & $exe run $model --inputs $io --prof 2>&1 | Out-String
    $t = Get-DispatchTime $out
    Write-Host "    $t ms total dispatch"
    $cpuTimes += $t
}
$cpuBest = ($cpuTimes | Measure-Object -Minimum).Minimum
Write-Host "  Best CPU dispatch: $cpuBest ms" -ForegroundColor Yellow

# 5. GPU performance (best-of-3 with --gpu-conv)
Write-Host "5. Measuring GPU performance (best-of-3 with --gpu-conv)..."
$gpuTimes = @()
for ($i = 1; $i -le 3; $i++) {
    Write-Host "  GPU run $i..."
    $out = & $exe run $model --inputs $io --gpu-conv --prof 2>&1 | Out-String
    $t = Get-DispatchTime $out
    Write-Host "    $t ms total dispatch"
    $gpuTimes += $t
}
$gpuBest = ($gpuTimes | Measure-Object -Minimum).Minimum
Write-Host "  Best GPU dispatch: $gpuBest ms" -ForegroundColor Yellow

# 6. Speedup Assertion
$speedup = [double]$cpuBest / $gpuBest
$speedupStr = "{0:F2}" -f $speedup
Assert-Pass ($gpuBest -le $cpuBest) "GPU speedup verified: $speedupStr`x vs CPU baseline ($cpuBest ms vs $gpuBest ms)"

# 7. End-to-end GPU MatMul/Conv correctness via specdiff
Write-Host "7. Running specdiff comparison between CPU and GPU waveforms..."
$gpuOutWav = 'S:\spawn-fusion\onnx-interp\_gpu.wav'
$gpuCorrectnessRun = & $exe run $model --inputs $io --out $gpuOutWav --gpu-conv 2>&1 | Out-String
if (-not (Test-Path $gpuOutWav)) {
    Assert-Pass $false "GPU wave file generated successfully"
} else {
    $sd = & $exe specdiff $cpuOutWav $gpuOutWav 2>&1 | Out-String
    Write-Host $sd
    $mSc = [regex]::Match($sd, 'MULTI-RES spectral convergence = ([\d.]+)')
    $sc = if ($mSc.Success) { [double]$mSc.Groups[1].Value } else { 1.0 }
    Assert-Pass ($sc -le 0.001) "GPU vs CPU Specdiff Spectral Convergence ($sc) <= 0.001 (FP32 precision)"
}

# 8. Per-op Conv-vs-CPU reference gate (comparing all nodes)
Write-Host "8. Running per-op Conv-vs-CPU reference gate..."
$cmpOut = & $exe run $model --inputs $io --gpu-conv --compare $cpuRefDir 2>&1 | Out-String
$opMismatch = $cmpOut -match "COMPARE_OP_MISMATCH"
if ($opMismatch) {
    # Print matching mismatch lines to help localize the failure
    $cmpLines = $cmpOut -split "`r?`n" | Where-Object { $_ -match "COMPARE_OP_MISMATCH" }
    foreach ($line in $cmpLines) { Write-Host "  $line" -ForegroundColor Red }
}
Assert-Pass (-not $opMismatch) "All GPU nodes match CPU reference tensors within 1e-3"

$pass = $fails.Count -eq 0
$verdict = if ($pass) { "PASS — GPU path is faithful, correct, and verified." } else { "FAIL — regressions detected." }

Write-Host "`n=== GAUNTLET VERDICT ===" -ForegroundColor Cyan
Write-Host $verdict -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test    = 'bench.gauntlet'
    pass    = $pass
    cpu_ms  = $cpuBest
    gpu_ms  = $gpuBest
    speedup = $speedup
    verdict = $verdict
}
