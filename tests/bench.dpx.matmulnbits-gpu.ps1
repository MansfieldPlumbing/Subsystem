#requires -Version 7
# bench.dpx.matmulnbits-gpu.ps1 - CPU vs GPU receipt for the q4-aware GEMM seam (Dp.UseGpuMatMulNBits ->
# Gpu.dpgpu_gemm_q4 -> GpuD3D12.GemmQ4). Packed uint8 B/zp + fp32 scales go to the GPU AS-IS (SEQUENTIAL
# nibble layout, tests/test.dpx.q4-packing-order.ps1) - dequant+multiply+accumulate fused in the shader, fp32
# weight never materialized on CPU. Two stages:
#   1. ShaderTournament.ResolveQ4 (CRQ190): compiles gemm_q4.hlsl (naive rung) + gemm_q4_gemv.hlsl (M==1
#      GEMV, groupshared A + Load4 blocks) + gemm_q4_tiled.hlsl (M>1 16x16 tile) with dxc, prints per-shape
#      naive-vs-variant ms + max|diff| vs the SCALAR oracle, and places only measured winners beside the exe.
#   2. End-to-end Dp.MatMulNBits rows: CPU (default SIMD path) vs GPU (the tournament-selected rung) per
#      shape, parity vs the scalar oracle (ForceScalarMatMulNBits) with max|diff| printed.
# All ms on this box are PROVISIONAL (other builders share it); parity verdicts are binding.
# SKIPS clean without dxc (Windows Kits) or the in-proc Subsystem.Dpx assembly.
#   Dogfood:  ss run tests/bench.dpx.matmulnbits-gpu.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dxc = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\dxc.exe' -EA SilentlyContinue | Select-Object -First 1
if (-not $dxc) { Write-Host "SKIP - no dxc.exe under Windows Kits (needed to compile the q4 kernel variants)." -ForegroundColor Yellow; return }

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss run tests/bench.dpx.matmulnbits-gpu.ps1)." -ForegroundColor Yellow; return }
$asm = $dpType.Assembly
$tensorT = $asm.GetType('Subsystem.Dpx.Tensor')
$nodeT   = $asm.GetType('Onnx.NodeProto')
$attrT   = $asm.GetType('Onnx.AttributeProto')
$mmnb = $dpType.GetMethod('MatMulNBits', [System.Reflection.BindingFlags]'NonPublic,Static')
$useGpuField = $dpType.GetField('UseGpuMatMulNBits', [System.Reflection.BindingFlags]'Public,Static')
$forceScalarField = $dpType.GetField('ForceScalarMatMulNBits', [System.Reflection.BindingFlags]'Public,Static')
$gpuDeadField = $dpType.GetField('_gpuQ4Dead', [System.Reflection.BindingFlags]'NonPublic,Static')
Assert ($null -ne $mmnb) 'Dp.MatMulNBits reachable in-proc'
Assert ($null -ne $useGpuField) 'Dp.UseGpuMatMulNBits reachable in-proc'

# --- stage 1: the tournament compiles all q4 kernel variants, prints naive-vs-variant per shape, places winners
$tournT = $asm.GetType('Subsystem.Dpx.ShaderTournament')
Write-Host ""
$trc = $tournT.GetMethod('ResolveQ4').Invoke($null, @([string]$null))
Assert ($trc -eq 0) 'ShaderTournament.ResolveQ4 ran (compile + parity vs scalar oracle + measure on the real adapter)'
$exeDir = Split-Path ([Environment]::ProcessPath) -Parent
Assert (Test-Path (Join-Path $exeDir 'gemm_q4.dxil')) 'naive gemm_q4.dxil placed beside the exe (the fallback rung)'
$gemvPlaced  = Test-Path (Join-Path $exeDir 'gemm_q4_gemv.dxil')
$tiledPlaced = Test-Path (Join-Path $exeDir 'gemm_q4_tiled.dxil')
Write-Host "  variant files placed by measurement: gemv=$gemvPlaced tiled=$tiledPlaced (absent = naive won that class)"

# --- stage 2: end-to-end Dp.MatMulNBits rows, CPU default path vs GPU tournament-selected rung
$rng = [Random]::new(190)
function RandBytes([int]$n) { $b = New-Object 'byte[]' $n; $rng.NextBytes($b); ,$b }
function RandFloats([int]$n) { $f = New-Object 'float[]' $n; for ($i = 0; $i -lt $n; $i++) { $f[$i] = [float]($rng.NextDouble() * 2.0 - 1.0) }; ,$f }
function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}

$shapes = @(
    @{ name = 'decode  qkv   M=1  K=2048 N=2048';  M = 1;  K = 2048; N = 2048;  reps = 10 },
    @{ name = 'decode  mlp+  M=1  K=2048 N=8192';  M = 1;  K = 2048; N = 8192;  reps = 10 },
    @{ name = 'decode  head  M=1  K=2048 N=32768'; M = 1;  K = 2048; N = 32768; reps = 10 },
    @{ name = 'prefill qkv   M=64 K=2048 N=2048';  M = 64; K = 2048; N = 2048;  reps = 5  },
    @{ name = 'prefill mlp+  M=64 K=2048 N=8192';  M = 64; K = 2048; N = 8192;  reps = 5  }
)
$rows = @()
foreach ($sh in $shapes) {
    $M = [int]$sh.M; $Kd = [int]$sh.K; $Nd = [int]$sh.N; $reps = [int]$sh.reps; $bs = 32; $nBlk = [int]($Kd / $bs)
    $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
    $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
    $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }
    $x = [Array]::CreateInstance($tensorT, 3); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS

    # the ORACLE: the scalar MatMulNBits path, forced
    $useGpuField.SetValue($null, $false); $forceScalarField.SetValue($null, $true)
    $yOracle = $mmnb.Invoke($null, @($x, $node))
    $forceScalarField.SetValue($null, $false)

    # CPU: the default path (SIMD when available)
    [void]$mmnb.Invoke($null, @($x, $node))                        # warmup
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $reps; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }; $sw.Stop()
    $cpuMs = $sw.Elapsed.TotalMilliseconds / $reps

    # GPU: the tournament-selected rung (gemv for M=1 / tiled for M>1 when placed, else naive)
    $useGpuField.SetValue($null, $true)
    $yGpu = $mmnb.Invoke($null, @($x, $node))                      # warmup (PSO + weight residency) + parity sample
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $reps; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }; $sw.Stop()
    $gpuMs = $sw.Elapsed.TotalMilliseconds / $reps
    $useGpuField.SetValue($null, $false)
    Assert (-not [bool]$gpuDeadField.GetValue($null)) "$($sh.name): GPU path stayed live (no _gpuQ4Dead latch mid-bench)"

    $maxDiff = 0.0
    $oF = $yOracle.Fp; $gF = $yGpu.Fp   # arena inactive in-proc -> Fp is the managed payload (Span can't cross into pwsh)
    for ($i = 0; $i -lt $oF.Length; $i++) { $d = [math]::Abs($oF[$i] - $gF[$i]); if ($d -gt $maxDiff) { $maxDiff = $d } }
    Assert ($maxDiff -lt 1e-3) "$($sh.name): GPU matches the scalar oracle (max|diff| = $maxDiff)"

    $speedup = $cpuMs / $gpuMs
    $rows += [pscustomobject]@{ shape = $sh.name; cpuMs = [math]::Round($cpuMs,3); gpuMs = [math]::Round($gpuMs,3); speedup = [math]::Round($speedup,2) }
    Write-Host ("  {0}   CPU {1,8:F3} ms   GPU {2,8:F3} ms   speedup {3,6:F2}x   (PROVISIONAL - shared box)" -f $sh.name, $cpuMs, $gpuMs, $speedup)
}

# zero-point parity probe: the tournament/bench rows run the e2b export's no-zp shape; this pins the packed
# zp nibble plane (low nibble = even block) through BOTH variant kernels against the scalar oracle.
foreach ($sh in @(
    @{ name = 'zp decode  M=1  K=2048 N=2048'; M = 1;  K = 2048; N = 2048 },
    @{ name = 'zp prefill M=64 K=2048 N=2048'; M = 64; K = 2048; N = 2048 }
)) {
    $M = [int]$sh.M; $Kd = [int]$sh.K; $Nd = [int]$sh.N; $bs = 32; $nBlk = [int]($Kd / $bs); $zpRowBytes = [int](($nBlk + 1) / 2)
    $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
    $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
    $tZ = [Activator]::CreateInstance($tensorT); $tZ.Rawb = (RandBytes ($Nd * $zpRowBytes)); $tZ.Shape = [int[]]($Nd, $zpRowBytes)
    $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }
    $x = [Array]::CreateInstance($tensorT, 4); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS; $x[3] = $tZ

    $useGpuField.SetValue($null, $false); $forceScalarField.SetValue($null, $true)
    $yOracle = $mmnb.Invoke($null, @($x, $node))
    $forceScalarField.SetValue($null, $false)
    $useGpuField.SetValue($null, $true)
    $yGpu = $mmnb.Invoke($null, @($x, $node))
    $useGpuField.SetValue($null, $false)
    Assert (-not [bool]$gpuDeadField.GetValue($null)) "$($sh.name): GPU path stayed live"
    $maxDiff = 0.0
    $oF = $yOracle.Fp; $gF = $yGpu.Fp
    for ($i = 0; $i -lt $oF.Length; $i++) { $d = [math]::Abs($oF[$i] - $gF[$i]); if ($d -gt $maxDiff) { $maxDiff = $d } }
    Assert ($maxDiff -lt 1e-3) "$($sh.name): explicit zp plane matches the scalar oracle (max|diff| = $maxDiff)"
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - q4 GPU seam parity-exact vs the scalar oracle on every shape; kernel per shape-class chosen by tournament measurement (naive stays the fallback rung). ms PROVISIONAL under shared-box load."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.matmulnbits-gpu'; pass=$pass; gemvPlaced=$gemvPlaced; tiledPlaced=$tiledPlaced; rows=$rows; verdict=$(if($pass){'GPU q4 path correct; tiled/GEMV rungs tournament-selected'}else{'see failures'}) }
