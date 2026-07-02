#requires -Version 7
# bench.dpx.matmulnbits-gpu.ps1 - CPU vs GPU receipt for the q4-aware GEMM seam (Dp.UseGpuMatMulNBits ->
# Gpu.dpgpu_gemm_q4 -> GpuD3D12.GemmQ4 / GpuVulkan.GemmQ4). Packed uint8 B/zp + fp32 scales go to the GPU
# AS-IS (SEQUENTIAL nibble layout, tests/test.dpx.q4-packing-order.ps1) - dequant+multiply+accumulate fused
# in the shader, fp32 weight never materialized on CPU. HONEST STATUS: the GPU kernel is naive (one thread
# per output element, no tiling/groupshared - matches the existing fp32 gemm.hlsl's off-road status) and
# per-call D3D12 buffer create+upload+readback is unamortized, so results are MIXED, not a clean win yet -
# this receipt exists so the next tiling pass has a real before/after, not a vibe.
# SKIPS clean without a live D3D12 device or gemm_q4.dxil next to the exe (dxc -T cs_6_2 -E main
# src/native/dp-onnx/gpu/gemm_q4.hlsl -Fo gemm_q4.dxil).
#   Dogfood:  ss run tests/bench.dpx.matmulnbits-gpu.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dxil = Join-Path (Split-Path ([Environment]::ProcessPath) -Parent) 'gemm_q4.dxil'
if (-not (Test-Path $dxil)) {
    Write-Host "SKIP - no gemm_q4.dxil next to the exe (dxc -T cs_6_2 -E main src/native/dp-onnx/gpu/gemm_q4.hlsl -Fo gemm_q4.dxil)." -ForegroundColor Yellow
    return
}

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss run tests/bench.dpx.matmulnbits-gpu.ps1)." -ForegroundColor Yellow; return }
$asm = $dpType.Assembly
$tensorT = $asm.GetType('Subsystem.Dpx.Tensor')
$nodeT   = $asm.GetType('Onnx.NodeProto')
$attrT   = $asm.GetType('Onnx.AttributeProto')
$mmnb = $dpType.GetMethod('MatMulNBits', [System.Reflection.BindingFlags]'NonPublic,Static')
$useGpuField = $dpType.GetField('UseGpuMatMulNBits', [System.Reflection.BindingFlags]'Public,Static')
Assert ($null -ne $mmnb) 'Dp.MatMulNBits reachable in-proc'
Assert ($null -ne $useGpuField) 'Dp.UseGpuMatMulNBits reachable in-proc'

$rng = [Random]::new(190)
function RandBytes([int]$n) { $b = New-Object 'byte[]' $n; $rng.NextBytes($b); ,$b }
function RandFloats([int]$n) { $f = New-Object 'float[]' $n; for ($i = 0; $i -lt $n; $i++) { $f[$i] = [float]($rng.NextDouble() * 2.0 - 1.0) }; ,$f }
function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}

$shapes = @(
    @{ name = 'decode  qkv   M=1  K=2048 N=2048';  M = 1; K = 2048; N = 2048  },
    @{ name = 'decode  mlp+  M=1  K=2048 N=8192';  M = 1; K = 2048; N = 8192  },
    @{ name = 'decode  head  M=1  K=2048 N=32768'; M = 1; K = 2048; N = 32768 }
)
$rows = @()
foreach ($sh in $shapes) {
    $M = [int]$sh.M; $Kd = [int]$sh.K; $Nd = [int]$sh.N; $bs = 32; $nBlk = [int]($Kd / $bs)
    $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
    $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
    $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }
    $x = [Array]::CreateInstance($tensorT, 3); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS

    $useGpuField.SetValue($null, $false)
    [void]$mmnb.Invoke($null, @($x, $node))                        # warmup
    $yCpu = $mmnb.Invoke($null, @($x, $node))
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt 10; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }; $sw.Stop()
    $cpuMs = $sw.Elapsed.TotalMilliseconds / 10

    $useGpuField.SetValue($null, $true)
    $yGpu = $mmnb.Invoke($null, @($x, $node))                       # warmup (PSO compile) + oracle-diff sample
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt 10; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }; $sw.Stop()
    $gpuMs = $sw.Elapsed.TotalMilliseconds / 10
    $useGpuField.SetValue($null, $false)

    $maxDiff = 0.0
    for ($i = 0; $i -lt $yCpu.Fp.Length; $i++) { $d = [math]::Abs($yCpu.Fp[$i] - $yGpu.Fp[$i]); if ($d -gt $maxDiff) { $maxDiff = $d } }
    Assert ($maxDiff -lt 1e-3) "$($sh.name): GPU matches CPU oracle (max|diff| = $maxDiff)"

    $speedup = $cpuMs / $gpuMs
    $rows += [pscustomobject]@{ shape = $sh.name; cpuMs = [math]::Round($cpuMs,3); gpuMs = [math]::Round($gpuMs,3); speedup = [math]::Round($speedup,2) }
    Write-Host ("  {0}   CPU {1,8:F3} ms   GPU {2,8:F3} ms   speedup {3,6:F2}x" -f $sh.name, $cpuMs, $gpuMs, $speedup)
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - GPU q4 seam numerically exact; naive kernel gives MIXED perf (per-call buffer overhead dominates at small/large N, wins only mid-range) - tiling/groupshared/persistent-buffer work is the next rung, not this one."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.matmulnbits-gpu'; pass=$pass; rows=$rows; verdict=$(if($pass){'GPU q4 path correct, perf work still ahead'}else{'see failures'}) }
