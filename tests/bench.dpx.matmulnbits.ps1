#requires -Version 7
# bench.dpx.matmulnbits.ps1 - The standing per-kernel benchmark for the DPX hot op (MatMulNBits = 98% of
# decode, CRQ190). Drives the REAL kernel in-proc via reflection over gemma4-e2b-shaped inputs (decode M=1
# and prefill M=32; K/N from the 2048-hidden decoder, lm_head capped at N=32768 - scale linearly for the
# 262144 full vocab). Reports ms/call and effective GB/s of packed-weight traffic (the op is bandwidth-bound:
# DDR ceiling is the target, see CRQ190). Runs once per kernel variant and diffs each against the scalar
# oracle: scalar (ForceScalarMatMulNBits), 128-lane (DPX_MMNB=128 pins the Vector128 rung), 512-lane
# (default on AVX-512 hardware). max|diff| vs scalar is the binding receipt; ms/call on a shared box is
# PROVISIONAL (timed as min of 5 interleaved rounds per rung so both see the same neighbor load).
#   Dogfood:  ss test dpx.matmulnbits
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { $dpType = [AppDomain]::CurrentDomain.GetAssemblies().ForEach({ $_.GetType('Subsystem.Dpx.Dp') }).Where({ $_ }) | Select-Object -First 1 }
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss test dpx.matmulnbits)." -ForegroundColor Yellow; return }
$asm     = $dpType.Assembly
$tensorT = $asm.GetType('Subsystem.Dpx.Tensor')
$arenaT  = $asm.GetType('Subsystem.Dpx.TensorArena')
$nodeT   = $asm.GetType('Onnx.NodeProto')
$attrT   = $asm.GetType('Onnx.AttributeProto')
$mmnb    = $dpType.GetMethod('MatMulNBits', [System.Reflection.BindingFlags]'NonPublic,Static')
$forceScalar = $dpType.GetField('ForceScalarMatMulNBits')
Assert ($null -ne $mmnb) 'Dp.MatMulNBits reachable in-proc'
Assert ($null -ne $forceScalar) 'ForceScalarMatMulNBits knob reachable in-proc'
$has512 = [System.Runtime.Intrinsics.Vector512]::IsHardwareAccelerated
Write-Host "  Vector512.IsHardwareAccelerated = $has512 (512-lane rung $(if($has512){'live'}else{'absent - 512 rows skipped'}))"

function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}

# deterministic fill (fixed-seed Random; a bench must be reproducible run to run)
$rng = [Random]::new(190)
function RandBytes([int]$n) { $b = New-Object 'byte[]' $n; $rng.NextBytes($b); ,$b }
function RandFloats([int]$n) { $f = New-Object 'float[]' $n; for ($i = 0; $i -lt $n; $i++) { $f[$i] = [float]($rng.NextDouble() * 2.0 - 1.0) }; ,$f }
function MaxDiff([float[]]$ref,[float[]]$got) { $d = 0.0; for ($i = 0; $i -lt $ref.Length; $i++) { $e = [math]::Abs($ref[$i] - $got[$i]); if ($e -gt $d) { $d = $e } }; $d }

# gemma4-e2b decoder shapes (hidden 2048): attention proj, mlp up, mlp down, capped lm_head
$shapes = @(
    @{ name = 'decode  qkv   M=1  K=2048 N=2048';  M = 1;  K = 2048; N = 2048  },
    @{ name = 'decode  mlp+  M=1  K=2048 N=8192';  M = 1;  K = 2048; N = 8192  },
    @{ name = 'decode  mlp-  M=1  K=8192 N=2048';  M = 1;  K = 8192; N = 2048  },
    @{ name = 'decode  head  M=1  K=2048 N=32768'; M = 1;  K = 2048; N = 32768 },
    @{ name = 'prefill qkv   M=32 K=2048 N=2048';  M = 32; K = 2048; N = 2048  }
)

$wasActive = $arenaT.GetProperty('Active').GetValue($null)
$arenaT.GetProperty('Active').SetValue($null, $false)   # outputs land in .Fp so variants can be diffed
$results = @()
try {
    foreach ($sh in $shapes) {
        $M = [int]$sh.M; $Kd = [int]$sh.K; $Nd = [int]$sh.N; $bs = 32; $nBlk = [int]($Kd / $bs)
        $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
        $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
        $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
        $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }
        $x = [Array]::CreateInstance($tensorT, 3); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS

        # scalar oracle: reference output + a light timing (2 iters; it is the slow rung by design)
        $forceScalar.SetValue($null, $true)
        $ref = ($mmnb.Invoke($null, @($x, $node))).Fp
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt 2; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }
        $sw.Stop(); $msScalar = $sw.Elapsed.TotalMilliseconds / 2
        $forceScalar.SetValue($null, $false)

        $variants = @('128'); if ($has512) { $variants += '512' }
        $bytes = ($Nd * $nBlk * 16) + ($M * $Kd * 4)              # packed weights + activations per call
        $row = [ordered]@{ shape = $sh.name; scalarMs = [math]::Round($msScalar, 3) }
        $refAbs = 0.0; foreach ($r in $ref) { $a = [math]::Abs($r); if ($a -gt $refAbs) { $refAbs = $a } }
        $best = @{}
        foreach ($v in $variants) {
            [Environment]::SetEnvironmentVariable('DPX_MMNB', $(if ($v -eq '128') { '128' } else { $null }))
            $y = ($mmnb.Invoke($null, @($x, $node))).Fp                 # warmup (JIT + caches) + diff capture
            $maxd = MaxDiff $ref $y
            $row["maxDiff$v"] = $maxd; $best[$v] = [double]::MaxValue
            Assert ($maxd -le (1e-3 * [math]::Max(1.0, $refAbs))) "$($sh.name) [$v-lane] matches scalar oracle (max|diff| = $maxd, max|ref| = $([math]::Round($refAbs,1)))"
        }
        # timing: 5 rounds interleaved across variants, min-of-rounds — a single timing window on a
        # shared box drifts 2-4x with neighbor load; interleaving + min gives both rungs the same weather
        $iters = if ($M -eq 1 -and $Nd -le 8192) { 10 } else { 4 }
        for ($round = 0; $round -lt 5; $round++) {
            foreach ($v in $variants) {
                [Environment]::SetEnvironmentVariable('DPX_MMNB', $(if ($v -eq '128') { '128' } else { $null }))
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                for ($i = 0; $i -lt $iters; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }
                $sw.Stop()
                $t = $sw.Elapsed.TotalMilliseconds / $iters
                if ($t -lt $best[$v]) { $best[$v] = $t }
            }
        }
        foreach ($v in $variants) {
            $row["ms$v"]  = [math]::Round($best[$v], 3)
            $row["gbs$v"] = [math]::Round($bytes / ($best[$v] / 1000.0) / 1e9, 2)
        }
        $results += [pscustomobject]$row
        $spd = if ($has512 -and $row['ms512'] -gt 0) { [math]::Round($row['ms128'] / $row['ms512'], 2) } else { 'n/a' }
        Write-Host ("  {0}  scalar {1,8:F3} ms | 128-lane {2,8:F3} ms | 512-lane {3,8} ms | 128/512 = {4}x" -f `
            $sh.name, $msScalar, $row['ms128'], $(if ($has512) { '{0,8:F3}' -f $row['ms512'] } else { 'skip' }), $spd)
    }
}
finally {
    $forceScalar.SetValue($null, $false)
    [Environment]::SetEnvironmentVariable('DPX_MMNB', $null)
    $arenaT.GetProperty('Active').SetValue($null, $wasActive)
}

Assert ($results.Count -eq $shapes.Count) 'all shapes benchmarked'
Assert (($results | Where-Object ms128 -le 0).Count -eq 0) 'timings are non-zero'

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host "  NOTE: ms/call above is PROVISIONAL when the box is shared (other builders running); max|diff| receipts are binding."
Write-Host ($(if($pass){"PASS - MatMulNBits per-variant receipt above; compare ms/call across rungs and against the DDR bandwidth ceiling."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.matmulnbits'; pass=$pass; vector512=$has512; rows=$results; verdict=$(if($pass){'per-rung kernel bench - the tweak/measure loop is seconds, not 90s decodes'}else{'see failures'}) }
