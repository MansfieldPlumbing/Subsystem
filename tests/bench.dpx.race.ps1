#requires -Version 7
# bench.dpx.race.ps1 - MVP-1 receipt: the per-op race (DpxRace.Resolve), the flow-scheduler seed. For each
# standing MatMulNBits shape, two VOM-owned worker threads race per iteration - CPU lane = MatMulNBitsSimd,
# GPU lane = GpuMatMulNBits (the packed-q4 seam, per-call array copies included) - conducted over CpuFences:
# Signal both work fences, Fence.WaitAny names the winner, Fence.WaitAll closes the barrier, outputs are
# parity-checked lane-vs-lane and (once per shape) against the scalar oracle (Dpx.ForceScalarMatMulNBits path).
# Each lane times its own compute; the table below reports median per-lane ms, win rate, and max rel diff.
# Ties at WaitAny's recheck resolve to cpu (index 0) - the win rate is by WaitAny, not by the stopwatches.
# Lane times INCLUDE cross-lane contention (a race is concurrent by construction: the GPU lane's per-call
# copies/driver work steal bandwidth from the CPU lane's Parallel.For); solo baselines live in
# tests/bench.dpx.matmulnbits-gpu.ps1 - do not compare the two tables as if measured alone.
# SKIPS clean without gemm_q4.dxil next to the exe or without a live D3D12 device (probe race, 1 iteration).
#   Dogfood:  ss run tests/bench.dpx.race.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dxil = Join-Path (Split-Path ([Environment]::ProcessPath) -Parent) 'gemm_q4.dxil'
if (-not (Test-Path $dxil)) {
    Write-Host "SKIP - no gemm_q4.dxil next to the exe (dxc -T cs_6_2 -E main src/native/dpx/gpu/gemm_q4.hlsl -Fo gemm_q4.dxil)." -ForegroundColor Yellow
    return
}

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss run tests/bench.dpx.race.ps1)." -ForegroundColor Yellow; return }
$asm = $dpType.Assembly
$tensorT = $asm.GetType('Subsystem.Dpx.Tensor')
$nodeT   = $asm.GetType('Onnx.NodeProto')
$attrT   = $asm.GetType('Onnx.AttributeProto')
$raceT   = $asm.GetType('Subsystem.Dpx.DpxRace')
$resolve = $raceT.GetMethod('Resolve', [System.Reflection.BindingFlags]'Public,Static')
Assert ($null -ne $resolve) 'DpxRace.Resolve reachable in-proc'

$rng = [Random]::new(191)
function RandBytes([int]$n) { $b = New-Object 'byte[]' $n; $rng.NextBytes($b); ,$b }
function RandFloats([int]$n) { $f = New-Object 'float[]' $n; for ($i = 0; $i -lt $n; $i++) { $f[$i] = [float]($rng.NextDouble() * 2.0 - 1.0) }; ,$f }
function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}
function NewCase([int]$M,[int]$Kd,[int]$Nd) {
    $bs = 32; $nBlk = [int]($Kd / $bs)
    $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
    $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
    $x = [Array]::CreateInstance($tensorT, 3); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS
    ,@($x, (NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }))
}

# Device probe: a 1-iteration race on the smallest shape. A GPU-lane fault here is the no-device /
# dead-seam case -> clean SKIP (the CPU lane faulting would be a real failure, asserted below).
$probeCase = NewCase 1 2048 2048
$probe = $resolve.Invoke($null, @($probeCase[0], $probeCase[1], 1))
if ($probe.GpuError) {
    Write-Host "SKIP - GPU lane unavailable: $($probe.GpuError)" -ForegroundColor Yellow
    return
}
Assert ($null -eq $probe.CpuError) 'probe: CPU lane clean'

$iters = 10
$shapes = @(
    @{ name = 'decode qkv   M=1  K=2048 N=2048';  M = 1;  K = 2048; N = 2048  },
    @{ name = 'decode mlp+  M=1  K=2048 N=8192';  M = 1;  K = 2048; N = 8192  },
    @{ name = 'decode mlp-  M=1  K=8192 N=2048';  M = 1;  K = 8192; N = 2048  },
    @{ name = 'decode head  M=1  K=2048 N=32768'; M = 1;  K = 2048; N = 32768 },
    @{ name = 'prefill qkv  M=32 K=2048 N=2048';  M = 32; K = 2048; N = 2048  }
)
$rows = @()
foreach ($sh in $shapes) {
    $case = NewCase ([int]$sh.M) ([int]$sh.K) ([int]$sh.N)
    [void]$resolve.Invoke($null, @($case[0], $case[1], 2))          # warmup race (PSO compile, JIT, page-in)
    $r = $resolve.Invoke($null, @($case[0], $case[1], $iters))

    Assert ($null -eq $r.CpuError -and $null -eq $r.GpuError) "$($sh.name): both lanes clean"
    Assert ($r.Completed -eq $iters) "$($sh.name): $($r.Completed)/$iters iterations reached the barrier"
    Assert ($r.ParityMaxRel -lt 1e-3) "$($sh.name): lane parity CPU vs GPU (max rel = $($r.ParityMaxRel.ToString('E2')))"
    Assert ($r.OracleCpuMaxRel -lt 1e-3) "$($sh.name): CPU lane vs scalar oracle (max rel = $($r.OracleCpuMaxRel.ToString('E2')))"
    Assert ($r.OracleGpuMaxRel -lt 1e-3) "$($sh.name): GPU lane vs scalar oracle (max rel = $($r.OracleGpuMaxRel.ToString('E2')))"

    $winner  = if ($r.CpuWins -ge $r.GpuWins) { 'cpu' } else { 'gpu' }
    $winRate = "cpu $($r.CpuWins)/$($r.Completed) gpu $($r.GpuWins)/$($r.Completed)"
    $rows += [pscustomobject]@{
        shape = $sh.name; winner = $winner; winRate = $winRate
        medCpuMs = [math]::Round($r.MedianCpuMs, 3); medGpuMs = [math]::Round($r.MedianGpuMs, 3)
        parityMaxRel = $r.ParityMaxRel
    }
    Write-Host ("  {0}   winner {1,-3} ({2})   median CPU {3,8:F3} ms   GPU {4,8:F3} ms   parity {5:E2}" -f `
        $sh.name, $winner, $winRate, $r.MedianCpuMs, $r.MedianGpuMs, $r.ParityMaxRel)
}

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - per-op race conducted over CpuFences (WaitAny winner, WaitAll barrier); winners named per shape above, both lanes parity-checked against each other and the scalar oracle."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.race'; pass=$pass; iterations=$iters; rows=$rows; verdict=$(if($pass){'race seam works; per-shape winner is the flow-scheduler signal'}else{'see failures'}) }
