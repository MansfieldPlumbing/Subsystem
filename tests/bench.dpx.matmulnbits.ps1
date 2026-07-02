#requires -Version 7
# bench.dpx.matmulnbits.ps1 - The standing per-kernel benchmark for the DPX hot op (MatMulNBits = 98% of
# decode, CRQ190). Drives the REAL kernel in-proc via reflection over gemma4-e2b-shaped inputs (decode M=1
# and prefill M=32; K/N from the 2048-hidden decoder, lm_head capped at N=32768 - scale linearly for the
# 262144 full vocab). Reports ms/call and effective GB/s of packed-weight traffic (the op is bandwidth-bound:
# DDR ceiling is the target, see CRQ190). When DPX_MMNB knobs exist, run once per kernel variant and diff
# against the scalar oracle; today it receipts whatever kernel is live.
#   Dogfood:  ss test dpx.matmulnbits
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dpType = 'Subsystem.Dpx.Dp' -as [type]
if (-not $dpType) { $dpType = [AppDomain]::CurrentDomain.GetAssemblies().ForEach({ $_.GetType('Subsystem.Dpx.Dp') }).Where({ $_ }) | Select-Object -First 1 }
if (-not $dpType) { Write-Host "SKIP - Subsystem.Dpx not loaded (run in-proc: ss test dpx.matmulnbits)." -ForegroundColor Yellow; return }
$asm     = $dpType.Assembly
$tensorT = $asm.GetType('Subsystem.Dpx.Tensor')
$nodeT   = $asm.GetType('Onnx.NodeProto')
$attrT   = $asm.GetType('Onnx.AttributeProto')
$mmnb    = $dpType.GetMethod('MatMulNBits', [System.Reflection.BindingFlags]'NonPublic,Static')
Assert ($null -ne $mmnb) 'Dp.MatMulNBits reachable in-proc'

function NewNode([hashtable]$attrs) {
    $n = [Activator]::CreateInstance($nodeT); $n.OpType = 'MatMulNBits'
    foreach ($kv in $attrs.GetEnumerator()) { $a = [Activator]::CreateInstance($attrT); $a.Name = $kv.Key; $a.I = [long]$kv.Value; $n.Attribute.Add($a) }
    return $n
}

# deterministic fill (fixed-seed Random; a bench must be reproducible run to run)
$rng = [Random]::new(190)
function RandBytes([int]$n) { $b = New-Object 'byte[]' $n; $rng.NextBytes($b); ,$b }
function RandFloats([int]$n) { $f = New-Object 'float[]' $n; for ($i = 0; $i -lt $n; $i++) { $f[$i] = [float]($rng.NextDouble() * 2.0 - 1.0) }; ,$f }

# gemma4-e2b decoder shapes (hidden 2048): attention proj, mlp up, mlp down, capped lm_head
$shapes = @(
    @{ name = 'decode  qkv   M=1  K=2048 N=2048';  M = 1;  K = 2048; N = 2048  },
    @{ name = 'decode  mlp+  M=1  K=2048 N=8192';  M = 1;  K = 2048; N = 8192  },
    @{ name = 'decode  mlp-  M=1  K=8192 N=2048';  M = 1;  K = 8192; N = 2048  },
    @{ name = 'decode  head  M=1  K=2048 N=32768'; M = 1;  K = 2048; N = 32768 },
    @{ name = 'prefill qkv   M=32 K=2048 N=2048';  M = 32; K = 2048; N = 2048  }
)

$results = @()
foreach ($sh in $shapes) {
    $M = [int]$sh.M; $Kd = [int]$sh.K; $Nd = [int]$sh.N; $bs = 32; $nBlk = [int]($Kd / $bs)
    $tA = [Activator]::CreateInstance($tensorT); $tA.Fp = (RandFloats ($M * $Kd)); $tA.Shape = [int[]]($M, $Kd)
    $tB = [Activator]::CreateInstance($tensorT); $tB.Rawb = (RandBytes ($Nd * $nBlk * 16)); $tB.Shape = [int[]]($Nd, $nBlk, 16)
    $tS = [Activator]::CreateInstance($tensorT); $tS.Fp = (RandFloats ($Nd * $nBlk)); $tS.Shape = [int[]]($Nd, $nBlk)
    $node = NewNode @{ K = $Kd; N = $Nd; bits = 4; block_size = $bs }
    $x = [Array]::CreateInstance($tensorT, 3); $x[0] = $tA; $x[1] = $tB; $x[2] = $tS

    [void]$mmnb.Invoke($null, @($x, $node))                       # warmup (JIT + caches)
    $iters = if ($M -eq 1 -and $Nd -le 8192) { 20 } else { 8 }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $iters; $i++) { [void]$mmnb.Invoke($null, @($x, $node)) }
    $sw.Stop()
    $msCall = $sw.Elapsed.TotalMilliseconds / $iters
    $bytes  = ($Nd * $nBlk * 16) + ($M * $Kd * 4)                 # packed weights + activations per call
    $gbs    = $bytes / ($msCall / 1000.0) / 1e9
    $results += [pscustomobject]@{ shape = $sh.name; msPerCall = [math]::Round($msCall, 3); effGBs = [math]::Round($gbs, 2) }
    Write-Host ("  {0}  {1,10:F3} ms/call  {2,7:F2} GB/s effective" -f $sh.name, $msCall, $gbs)
}

Assert ($results.Count -eq $shapes.Count) 'all shapes benchmarked'
Assert (($results | Where-Object msPerCall -le 0).Count -eq 0) 'timings are non-zero'

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - MatMulNBits per-shape receipt above; compare ms/call across kernel variants and against the DDR bandwidth ceiling."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ bench='dpx.matmulnbits'; pass=$pass; rows=$results; verdict=$(if($pass){'standing kernel bench - the tweak/measure loop is seconds, not 90s decodes'}else{'see failures'}) }
