#requires -Version 7
# test.dpx.pipeline-shard.ps1 — The federation pipeline-shard proving ground.
# Splits a deterministic multi-layer forward across TWO Vom.Spawn worker threads, handing the
# boundary activation between them through a VOM region + CpuFence (Signal-after-write on the
# producer, WaitAll barrier on the consumer). Proves (a) a layer-split forward is bit-identical to
# a single-worker forward, and (b) measures the fenced-handoff wake cost — the intraprocess FLOOR
# for what a cross-device shard boundary must beat before layer-splitting a model over the wire is
# worth it. Zero engine files: compiles against the already-loaded Subsystem.Vom only.
# Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.dpx.pipeline-shard.ps1

$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$VOM = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if (-not $VOM) { Write-Host "Subsystem.Vom type not found — cannot run." -ForegroundColor Red; return }

# Extract references directly from the executing ss.exe single-file bundle (mirror decode-loop bootstrap).
$selfBundleType = $VOM.Assembly.GetType('Subsystem.Windows.SelfBundle')
if (-not $selfBundleType) { throw "SelfBundle type not found in ss assembly" }
$exePath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$exeBytes = [System.IO.File]::ReadAllBytes($exePath)
$manifest = $selfBundleType.GetMethod('Read').Invoke($null, @(,$exeBytes))
$bundleAssemblies = $selfBundleType.GetMethod('ManagedAssemblies').Invoke($null, @($manifest))

$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($tup in $bundleAssemblies) {
    try { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromImage($tup.Item2)) } catch { }
}

Write-Host "Compiling PipelineShardHarness via Roslyn (Subsystem.Vom only)..."
$harnessCode = @"
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Subsystem.Vom;

// The proving ground: a deterministic L-layer elementwise-affine forward over a hidden vector,
// run once whole (reference) and once split across two VOM-owned worker threads with a fenced
// activation handoff at the shard boundary. Same op order both ways => bit-identical is the bar.
public static unsafe class PipelineShardHarness
{
    const int H = 1536;   // hidden width (gemma-e2b hidden_size, representative)
    const int L = 8;      // synthetic decoder layers
    const int SPLIT = 4;  // shard boundary: worker A runs [0,SPLIT), worker B runs [SPLIT,L)

    static float Wt(int layer) => 1.0f + (layer * 0.013f);   // fixed per-layer affine — deterministic
    static float Bs(int layer) => (layer * 0.007f) - 0.05f;

    static void RunLayers(float* h, int from, int to)
    {
        for (int l = from; l < to; l++)
        {
            float w = Wt(l), b = Bs(l);
            for (int i = 0; i < H; i++) h[i] = h[i] * w + b;
        }
    }

    public static string Run()
    {
        const int iters = 200;
        var root = Vom.CreateOwner("\\Sessions\\__pipeshard");
        long handlesBefore = Vom.Totals().Handles;

        float[] input = new float[H];
        for (int i = 0; i < H; i++) input[i] = (float)Math.Sin(i * 0.001) * 0.5f;

        // --- single-worker reference: all L layers, one thread ---
        var refH = Vom.Alloc(root, H * 4, VomFormat.Float32, "RefActivation");
        float* rp = (float*)refH.Resource;
        for (int i = 0; i < H; i++) rp[i] = input[i];
        RunLayers(rp, 0, L);

        // --- 2-shard pipeline: worker A [0,SPLIT) -> fenced handoff -> worker B [SPLIT,L) ---
        var actH = Vom.Alloc(root, H * 4, VomFormat.Float32, "ShardActivation");  // boundary activation
        float* ap = (float*)actH.Resource;
        var outH = Vom.Alloc(root, H * 4, VomFormat.Float32, "ShardOutput");
        float* op = (float*)outH.Resource;

        var handoff = new CpuFence();                 // A doorbell: boundary activation complete
        var done    = new CpuFence();                 // B doorbell: final output complete
        var fToB = new Fence[] { handoff };
        var fToC = new Fence[] { done };
        long totalWakeTicks = 0;

        var swAll = Stopwatch.StartNew();

        Vom.Spawn(root, "shardA", a =>
        {
            for (ulong it = 1; it <= (ulong)iters; it++)
            {
                for (int i = 0; i < H; i++) ap[i] = input[i];   // re-seed: identical work every iter
                RunLayers(ap, 0, SPLIT);
                handoff.Signal(it);                             // publish boundary activation
                done.Wait(it);                                  // ping-pong: let B consume before reseeding
            }
        });

        Vom.Spawn(root, "shardB", b =>
        {
            for (ulong it = 1; it <= (ulong)iters; it++)
            {
                var sw = Stopwatch.StartNew();
                Fence.WaitAll(fToB, new ulong[] { it });        // barrier: park until A's phase `it`
                sw.Stop();
                Interlocked.Add(ref totalWakeTicks, sw.ElapsedTicks);
                for (int i = 0; i < H; i++) op[i] = ap[i];      // read handed-off activation
                RunLayers(op, SPLIT, L);
                done.Signal(it);
            }
        });

        long handlesDuring = Vom.Totals().Handles - handlesBefore;   // regions + 2 spawned thread handles
        Fence.WaitAll(fToC, new ulong[] { (ulong)iters });           // conductor: await final phase
        swAll.Stop();

        double maxDiff = 0;                                          // sharded vs single-worker, bit-exact bar
        for (int i = 0; i < H; i++) { double d = Math.Abs((double)op[i] - (double)rp[i]); if (d > maxDiff) maxDiff = d; }

        Vom.Terminate(root);                                        // cascade reclaim: root + shardA + shardB
        long handlesAfter = Vom.Totals().Handles - handlesBefore;

        double usPerHandoff = (totalWakeTicks / (double)iters) * (1000000.0 / Stopwatch.Frequency);

        var sb = new StringBuilder();
        sb.AppendLine($"iters:{iters}");
        sb.AppendLine($"hidden:{H}");
        sb.AppendLine($"layers:{L}");
        sb.AppendLine($"split:{SPLIT}");
        sb.AppendLine($"max_diff:{maxDiff:R}");
        sb.AppendLine($"handles_during:{handlesDuring}");
        sb.AppendLine($"handles_after_teardown:{handlesAfter}");
        sb.AppendLine($"us_per_handoff:{usPerHandoff:F3}");
        sb.AppendLine($"wall_ms:{swAll.Elapsed.TotalMilliseconds:F1}");
        return sb.ToString();
    }
}
"@

$syntaxTrees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($harnessCode))

$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    ("PipeShard_" + [Guid]::NewGuid().ToString("N")),
    $syntaxTrees,
    $references,
    $options)

$ms = [System.IO.MemoryStream]::new()
$emit = $comp.Emit($ms)
if (-not $emit.Success) {
    foreach ($d in $emit.Diagnostics) { if ($d.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) { Write-Error $d.ToString() } }
    throw "Compilation of pipeline-shard harness failed."
}
$ms.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
$asm = [System.Reflection.Assembly]::Load($ms.ToArray())
$harness = $asm.GetType('PipelineShardHarness')
Assert ($null -ne $harness) "PipelineShardHarness compiled and loaded"

$resultStr = $harness.GetMethod('Run').Invoke($null, $null)
$r = @{}
foreach ($line in ($resultStr -split "`n")) { if ($line -match '^([^:]+):(.*)$') { $r[$Matches[1]] = $Matches[2].Trim() } }

$maxDiff        = [double]$r['max_diff']
$handlesDuring  = [int]$r['handles_during']
$handlesAfter   = [int]$r['handles_after_teardown']
$usPerHandoff   = [double]$r['us_per_handoff']

Assert ($maxDiff -eq 0.0) "2-shard layer-split forward is BIT-IDENTICAL to single-worker (max|diff|=$maxDiff)"
Assert ($handlesDuring -gt 0) "VOM regions + spawned worker threads live during the shard run (handles=$handlesDuring)"
Assert ($handlesAfter -eq 0) "Terminate cascaded — root + both shard workers fully reclaimed (leaked=$handlesAfter)"
Assert ($usPerHandoff -ge 0) "Fenced-handoff wake cost measured ($([math]::Round($usPerHandoff,3)) us/handoff)"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS — a dense forward splits across VOM-owned workers bit-exact; the fenced handoff floor is measured."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})

[pscustomobject]@{
    test = 'test.dpx.pipeline-shard'
    pass = $pass
    max_diff = $maxDiff
    us_per_handoff = [math]::Round($usPerHandoff,3)
    reclaimed = ($handlesAfter -eq 0)
    verdict = $(if($pass){"2-shard split bit-identical (max|diff|=0); fenced handoff ~$([math]::Round($usPerHandoff,2))us intraprocess (the floor a cross-device shard boundary must beat); handles reclaimed on cascade Terminate"}else{'see failures'})
}
