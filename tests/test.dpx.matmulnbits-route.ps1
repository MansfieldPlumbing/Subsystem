#requires -Version 7
# test.dpx.matmulnbits-route.ps1 - CRQ190 R0: the per-shape CPU/GPU router for MatMulNBits. Proves the
# MECHANISM, not a winner table: winners are derived at RUNTIME (first sight of a (M,N,K,block_size)
# shape races a direct timed comparison and caches the verdict for the table's lifetime), so this test
# asserts (1) a routed Dispatch stays parity-exact against the scalar oracle, (2) each shape races ONCE
# (the table holds exactly one entry per shape no matter how many dispatches follow), (3) a null route
# (every bare Dispatch caller) preserves the standing knob behavior, and prints the raced winner table
# as the receipt. Which lane wins is adapter truth, not an assertion. The proof body is C# compiled
# in-proc against the running bundle (the decode-loop test's shape) - the router is exercised through
# the same compiled surface Dp.Run uses, not a shell re-implementation.
# SKIPS clean without gemm_q4.dxil next to the exe or without a live D3D12 device (the router's GPU
# lane faults -> _gpuQ4Dead latch, the inv-9 degrade), or if that latch was already set in-proc.
#   Dogfood:  ss -File tests/test.dpx.matmulnbits-route.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$dxil = Join-Path (Split-Path ([Environment]::ProcessPath) -Parent) 'gemm_q4.dxil'
if (-not (Test-Path $dxil)) {
    Write-Host "SKIP - no gemm_q4.dxil next to the exe (dxc -T cs_6_2 -E main src/native/dp-onnx/gpu/gemm_q4.hlsl -Fo gemm_q4.dxil)." -ForegroundColor Yellow
    return
}

$VOM = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if (-not $VOM) { Write-Host "SKIP - Subsystem assembly not loaded (run in-proc: ss -File tests/test.dpx.matmulnbits-route.ps1)." -ForegroundColor Yellow; return }

# References straight out of the executing ss.exe single-file bundle (decode-loop's extraction shape).
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

$proofCode = @"
using System;
using System.Reflection;
using System.Text;
using Onnx;
using Subsystem.Dpx;

public static class RouteProof
{
    static readonly Random Rng = new Random(190);

    static Tensor[] CreateCase(int M, int K, int N, out NodeProto node)
    {
        int bs = 32, nBlk = K / bs;
        node = new NodeProto { OpType = "MatMulNBits" };
        foreach (var (name, v) in new[] { ("K", (long)K), ("N", (long)N), ("bits", 4L), ("block_size", (long)bs) })
            node.Attribute.Add(new AttributeProto { Name = name, I = v });
        var a = new float[M * K]; for (int i = 0; i < a.Length; i++) a[i] = (float)(Rng.NextDouble() * 2.0 - 1.0);
        var b = new byte[N * nBlk * 16]; Rng.NextBytes(b);
        var sc = new float[N * nBlk]; for (int i = 0; i < sc.Length; i++) sc[i] = (float)(Rng.NextDouble() * 2.0 - 1.0);
        return new[]
        {
            new Tensor { Fp = a, Shape = new[] { M, K } },
            new Tensor { Rawb = b, Shape = new[] { N, nBlk, 16 } },
            new Tensor { Fp = sc, Shape = new[] { N, nBlk } },
        };
    }

    static double MaxRel(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return double.PositiveInfinity;
        double max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = Math.Abs(a[i] - b[i]) / Math.Max(1.0, Math.Abs(a[i]));
            if (d > max) max = d;
        }
        return max;
    }

    public static string RunTest()
    {
        var sb = new StringBuilder();
        var gpuDead = typeof(Dp).GetField("_gpuQ4Dead", BindingFlags.NonPublic | BindingFlags.Static);
        if ((bool)gpuDead.GetValue(null)) return "skip:latched\n";

        var route = new DpxRoute();
        var px = CreateCase(1, 2048, 256, out var pn);    // probe shape is NOT in the asserted set below
        Dp.Dispatch(pn, px, route);                       // probe: first sight races; a GPU fault latches
        if ((bool)gpuDead.GetValue(null)) return "skip:gpu\n";
        sb.AppendLine($"probe_entries:{route.EnumerateRoutes().Length}");

        var shapes = new (string name, int M, int K, int N)[]
        {
            ("qkv", 1, 2048, 2048),
            ("mlp+", 1, 2048, 8192),
            ("mlp-", 1, 8192, 2048),
        };
        foreach (var s in shapes)
        {
            var x = CreateCase(s.M, s.K, s.N, out var n);
            int before = route.EnumerateRoutes().Length;
            var y1 = Dp.Dispatch(n, x, route)[0];         // first sight: races + registers
            int afterFirst = route.EnumerateRoutes().Length;
            var y2 = Dp.Dispatch(n, x, route)[0];         // cached: dispatches the winner
            int afterSecond = route.EnumerateRoutes().Length;
            bool known = route.Query(s.M, s.N, s.K, 32, out bool gpuWins);

            Dp.ForceScalarMatMulNBits = true;             // scalar oracle, null route = the standing path
            Tensor oracle;
            try { oracle = Dp.Dispatch(n, x)[0]; }
            finally { Dp.ForceScalarMatMulNBits = false; }
            var plain = Dp.Dispatch(n, x)[0];             // default knobs, null route: standing SIMD path

            sb.AppendLine($"shape:{s.name}|{before}|{afterFirst}|{afterSecond}|{known}|{(gpuWins ? "gpu" : "cpu")}"
                + $"|{MaxRel(oracle.AsF(), y2.AsF()):E2}|{MaxRel(oracle.AsF(), plain.AsF()):E2}"
                + $"|{(y1.Count == y2.Count && y2.Count == (long)s.M * s.N)}");
        }
        foreach (var line in route.EnumerateRoutes()) sb.AppendLine($"route:{line}");
        return sb.ToString();
    }
}
"@

$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($proofCode))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create("RouteProof_" + [Guid]::NewGuid().ToString("N"), $trees, $references, $options)
$ms = [System.IO.MemoryStream]::new()
$emit = $comp.Emit($ms)
if (-not $emit.Success) {
    foreach ($d in $emit.Diagnostics) { if ($d.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) { Write-Error $d.ToString() } }
    throw "RouteProof compilation failed."
}
[void]$ms.Seek(0, [System.IO.SeekOrigin]::Begin)
$proofAsm = [System.Reflection.Assembly]::Load($ms.ToArray())
Assert ($null -ne $proofAsm.GetType('RouteProof')) 'RouteProof compiled against the running bundle'

$out = $proofAsm.GetType('RouteProof').GetMethod('RunTest').Invoke($null, $null)
if ($out.StartsWith('skip:latched')) { Write-Host "SKIP - _gpuQ4Dead already latched in this process." -ForegroundColor Yellow; return }
if ($out.StartsWith('skip:gpu')) { Write-Host "SKIP - GPU lane unavailable (fault latched during the probe race; router degraded to CPU as designed)." -ForegroundColor Yellow; return }

$routes = @()
foreach ($line in ($out -split "`n")) {
    $line = $line.Trim(); if (-not $line) { continue }
    if ($line.StartsWith('probe_entries:')) {
        Assert (([int]$line.Substring(14)) -eq 1) 'probe shape raced and registered exactly one route entry'
    }
    elseif ($line.StartsWith('shape:')) {
        $p = $line.Substring(6) -split '\|'
        $name = $p[0]; $before = [int]$p[1]; $afterFirst = [int]$p[2]; $afterSecond = [int]$p[3]
        $known = [bool]::Parse($p[4]); $winner = $p[5]
        $relRouted = [double]$p[6]; $relPlain = [double]$p[7]; $shapeOk = [bool]::Parse($p[8])
        Assert ($afterFirst -eq $before + 1 -and $afterSecond -eq $afterFirst) "$($name): raced ONCE, cached for table lifetime (entries $before -> $afterFirst -> $afterSecond)"
        Assert $known "$($name): Query names the cached winner ($winner)"
        Assert ($relRouted -lt 1e-3) "$($name): routed output vs scalar oracle (max rel = $relRouted)"
        Assert ($relPlain -lt 1e-3) "$($name): null-route (standing path) vs scalar oracle (max rel = $relPlain)"
        Assert $shapeOk "$($name): routed output element count M*N, stable across dispatches"
    }
    elseif ($line.StartsWith('route:')) { $routes += $line.Substring(6) }
}

Write-Host ""
Write-Host "  raced winner table (RUNTIME truth for THIS adapter, not an assertion):" -ForegroundColor Cyan
foreach ($r in $routes) { Write-Host "    $r" }

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - per-shape router races each (M,N,K,bs) once at runtime, caches the winner, stays parity-exact vs the scalar oracle, and leaves bare Dispatch callers untouched."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{ test='dpx.matmulnbits-route'; pass=$pass; routes=$routes; verdict=$(if($pass){'router mechanism proven; winners are adapter-local runtime truth'}else{'see failures'}) }
