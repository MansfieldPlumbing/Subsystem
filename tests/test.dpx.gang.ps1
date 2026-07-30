#requires -Version 7
# test.dpx.gang.ps1 - DpxGang (CRQ195): the brutally-synchronous worker-gang primitive. Three receipts:
#   (a) lanes execute in LOCKSTEP gated by Fence.WaitAll/WaitN, not ad hoc Parallel.For - a slow lane
#       holds up the barrier and every lane's marker is visible only AFTER WaitAll returns; WaitN(2)
#       returns once a quorum clears without waiting for the slower laggard lanes.
#   (b) a lane-role reassignment (DpGangHnic.Bind) is REFUSED while the prior buffer's registry
#       refcount is nonzero above the floor - a real negative test (Vom.Open holds it open, Bind must
#       throw DpGangHazardException), then Close drops it back to the floor and the SAME Bind succeeds.
#   (c) the retrofitted call site (Dpx.MatMulNBits's scalar per-N fan-out, now DpxGang-driven) still
#       matches a hand-computed dot-product oracle bit-for-bit-modulo-fp-tolerance.
# Lane bodies run on VOM-Spawn'd plain CLR threads (no pwsh Runspace bound), so they cannot be
# PowerShell scriptblocks - the harness is real compiled C# (Roslyn, same pattern as
# test.dpx.decode-loop.ps1 / test.dpx.qnn-project.ps1), calling straight into the shipped DpxGang/Vom/Dp
# types. Authority = the binary. This comment is not authority; the receipt the run prints is.
#   Dogfood:  ss -File tests/test.dpx.gang.ps1
$ErrorActionPreference = 'Stop'
$fails = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$c,[string]$m){ if($c){Write-Host "  ok   $m" -ForegroundColor Green}else{Write-Host "  FAIL $m" -ForegroundColor Red;$script:fails.Add($m)} }

$VOM = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Subsystem.Vom.Vom') } | Where-Object {$_} | Select-Object -First 1
if (-not $VOM) { Write-Host "SKIP - Subsystem.Vom type not loaded (run in-proc: ss -File tests/test.dpx.gang.ps1)." -ForegroundColor Yellow; return }
$gangT = $VOM.Assembly.GetType('Subsystem.Dpx.DpxGang')
if (-not $gangT) { Write-Host "SKIP - Subsystem.Dpx.DpxGang not loaded." -ForegroundColor Yellow; return }

$exe = [Environment]::ProcessPath
$selfBundleType = $VOM.Assembly.GetType('Subsystem.Windows.SelfBundle')
if (-not $selfBundleType) { throw "SelfBundle type not found in ss assembly" }
$exeBytes = [System.IO.File]::ReadAllBytes($exe)
$manifest = $selfBundleType.GetMethod('Read').Invoke($null, @(,$exeBytes))
$bundleAssemblies = $selfBundleType.GetMethod('ManagedAssemblies').Invoke($null, @($manifest))
$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($tup in $bundleAssemblies) { try { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromImage($tup.Item2)) } catch { } }

Write-Host "Compiling GangHarness via Roslyn (DpxGang lockstep + hazard + oracle-parity, all real compiled C#)..."
$harnessCode = @'
using System;
using System.Threading;
using Onnx;
using Subsystem.Dpx;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

// Real compiled C# (not a PowerShell scriptblock - DpxGang lane bodies run on VOM-Spawn'd plain CLR
// threads with no pwsh Runspace bound, so a scriptblock delegate faults there). Three independent
// static entry points, one per receipt; each returns a `key:value` line-oriented block the PowerShell
// side parses, same convention as test.dpx.qnn-project.ps1's harness.
public static class GangHarness
{
    static readonly string Sep = "\\";

    // (a) Lockstep: 4 lanes, lane k sleeps k*40ms then stamps Environment.TickCount64 into a shared
    // array. WaitAll must not return until every stamp is set (the barrier held for the laggard).
    // Then a SECOND phase (stamps reset) uses WaitN(2): the two FASTEST lanes (0,1; sleep 0/40ms)
    // clear the quorum while lanes 2/3 (80/120ms) are still sleeping - WaitN must return well before
    // 80ms, proving it does not wait for the full gang.
    public static string Lockstep()
    {
        int laneCount = 4;
        var gang = new DpxGang(Sep + "Sessions" + Sep + "__gangtest_lockstep_" + Guid.NewGuid().ToString("N"), laneCount);
        try
        {
            var stamps = new long[laneCount];
            var sleepMs = new int[laneCount];
            for (int i = 0; i < laneCount; i++) sleepMs[i] = i * 40;
            gang.LaneWork = (lane, phase) =>
            {
                Thread.Sleep(sleepMs[lane]);
                Volatile.Write(ref stamps[lane], Environment.TickCount64);
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            gang.WaitAll();
            sw.Stop();
            long afterBarrier = Environment.TickCount64;

            bool allStamped = true; long maxStamp = 0;
            for (int i = 0; i < laneCount; i++)
            {
                long s = Volatile.Read(ref stamps[i]);
                if (s == 0) allStamped = false;
                if (s > maxStamp) maxStamp = s;
            }
            long waitAllMs = sw.ElapsedMilliseconds;

            for (int i = 0; i < laneCount; i++) Volatile.Write(ref stamps[i], 0);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            int met = gang.WaitN(2);
            sw2.Stop();

            return "allStamped:" + allStamped
                + "\nafterBarrier:" + afterBarrier
                + "\nmaxStamp:" + maxStamp
                + "\nwaitAllMs:" + waitAllMs
                + "\nquorumMet:" + met
                + "\nwaitNMs:" + sw2.ElapsedMilliseconds
                + "\nsleeps:" + string.Join(",", sleepMs);
        }
        finally { gang.Dispose(); }
    }

    // (b) Hazard guard: seed role 0 with bufA, have a "straggler" Vom.Open it (refcount above the
    // floor), assert Bind THROWS DpGangHazardException and role 0 still aliases bufA, then Close the
    // straggler back to the floor and assert the SAME Bind now succeeds and role 0 aliases bufB.
    public static string Hazard()
    {
        string ownerPath = Sep + "Sessions" + Sep + "__gangtest_hazard_" + Guid.NewGuid().ToString("N");
        var owner = VomClass.CreateOwner(ownerPath);
        try
        {
            var hA = VomClass.Alloc(owner, 256, VomFormat.Bytes, "GangHazardTest", false, "Objects", "bufA");
            var hB = VomClass.Alloc(owner, 256, VomFormat.Bytes, "GangHazardTest", false, "Objects", "bufB");
            var bufA = new DpGangBuffer(owner, hA);
            var bufB = new DpGangBuffer(owner, hB);

            var hnic = new DpGangHnic(owner, 1);
            hnic.Set(0, bufA);
            int rcSeed = VomClass.QueryRefCount(owner, hA.Id);

            VomClass.Open(owner, hA.Id);   // straggler holds it open
            int rcHeld = VomClass.QueryRefCount(owner, hA.Id);

            bool threw = false; string exType = "(none)";
            try { hnic.Bind(0, bufB, 1); }
            catch (DpGangHazardException ex) { threw = true; exType = ex.GetType().Name; }

            var stillA = hnic.Read(0);
            bool stillAliasesA = stillA.Handle.Id == hA.Id;

            VomClass.Close(owner, hA.Id);   // straggler drains back to the floor
            int rcDrained = VomClass.QueryRefCount(owner, hA.Id);

            bool boundOk = false;
            try { hnic.Bind(0, bufB, 1); boundOk = true; } catch { }
            var nowB = hnic.Read(0);
            bool nowAliasesB = nowB.Handle.Id == hB.Id;

            return "rcSeed:" + rcSeed
                + "\nrcHeld:" + rcHeld
                + "\nthrew:" + threw
                + "\nexType:" + exType
                + "\nstillAliasesA:" + stillAliasesA
                + "\nrcDrained:" + rcDrained
                + "\nboundOk:" + boundOk
                + "\nnowAliasesB:" + nowAliasesB;
        }
        finally { VomClass.Terminate(owner); }
    }

    // (c) Numerics: Dpx.MatMulNBits (now DpxGang-driven for its scalar per-N fan-out) vs a hand-rolled
    // fp64 dot-product oracle over the SAME packed q4 bytes / scales / activations. K=64 (2 blocks),
    // N=6 (more rows than most boxes' default lane count, so at least one lane covers >1 row), M=3,
    // zp absent -> default 8. Deterministic PRNG fixture (seeded), not flaky on re-run.
    public static string Oracle()
    {
        const int Kd = 64, Nd = 6, bsz = 32, nBlk = 2, M = 3, bytesPerRow = nBlk * 16;
        var rng = new Random(12345);
        var packed = new byte[Nd * bytesPerRow];
        for (int i = 0; i < packed.Length; i++) packed[i] = (byte)rng.Next(0, 256);
        var scales = new float[Nd * nBlk];
        for (int i = 0; i < scales.Length; i++) scales[i] = (float)(0.1 + rng.NextDouble() * 0.9);
        var act = new float[M * Kd];
        for (int i = 0; i < act.Length; i++) act[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        var dpT = typeof(Dpx);
        var mmnb = dpT.GetMethod("MatMulNBits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var arenaT = typeof(TensorArena);
        bool wasActive = (bool)arenaT.GetProperty("Active").GetValue(null);
        arenaT.GetProperty("Active").SetValue(null, false);
        try
        {
            var tA = new Tensor { Fp = act, Shape = new[] { M, Kd } };
            var tB = new Tensor { Rawb = packed, Shape = new[] { Nd, nBlk, 16 } };
            var tS = new Tensor { Fp = scales, Shape = new[] { Nd, nBlk } };
            var node = new NodeProto { OpType = "MatMulNBits" };
            foreach (var (name, val) in new[] { ("K", (long)Kd), ("N", (long)Nd), ("bits", 4L), ("block_size", (long)bsz) })
                node.Attribute.Add(new AttributeProto { Name = name, I = val });

            var y = (Tensor)mmnb.Invoke(null, new object[] { new[] { tA, tB, tS }, node });

            double maxAbsDiff = 0, maxRef = 0;
            for (int m = 0; m < M; m++)
                for (int nn = 0; nn < Nd; nn++)
                {
                    int rowBase = nn * bytesPerRow;
                    double acc = 0;
                    for (int b = 0; b < nBlk; b++)
                    {
                        float s = scales[nn * nBlk + b];
                        int k0 = b * bsz;
                        for (int i = 0; i < bsz; i++)
                        {
                            int k = k0 + i;
                            int q = (packed[rowBase + (k >> 1)] >> ((k & 1) << 2)) & 0xF;
                            acc += (double)s * (q - 8.0) * (double)act[m * Kd + k];
                        }
                    }
                    double got = y.Fp[m * Nd + nn];
                    double d = Math.Abs(got - acc);
                    if (d > maxAbsDiff) maxAbsDiff = d;
                    double ar = Math.Abs(acc); if (ar > maxRef) maxRef = ar;
                }

            return "maxAbsDiff:" + maxAbsDiff.ToString("E6") + "\nmaxRef:" + maxRef.ToString("F6");
        }
        finally { arenaT.GetProperty("Active").SetValue(null, wasActive); }
    }
}
'@

$syntaxTrees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$syntaxTrees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($harnessCode))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(("GangHarness_" + [Guid]::NewGuid().ToString("N")), $syntaxTrees, $references, $options)
$ms = [System.IO.MemoryStream]::new()
$emit = $comp.Emit($ms)
if (-not $emit.Success) {
    foreach ($d in $emit.Diagnostics) { if ($d.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) { Write-Error $d.ToString() } }
    throw "Compilation of GangHarness failed."
}
$ms.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
$asm = [System.Reflection.Assembly]::Load($ms.ToArray())
$harness = $asm.GetType('GangHarness')
Assert ($null -ne $harness) "GangHarness compiled and loaded"

function ParseKv([string]$s) {
    $r = @{}
    foreach ($line in ($s -split "`n")) { if ($line -match '^([^:]+):(.*)$') { $r[$Matches[1]] = $Matches[2].Trim() } }
    return $r
}

# ============================================================================
# (a) Lockstep
# ============================================================================
Write-Host ""
Write-Host "(a) lockstep via Fence.WaitAll / WaitN (not ad hoc Parallel.For)" -ForegroundColor Cyan
$a = ParseKv ($harness.GetMethod('Lockstep').Invoke($null, @()))
Assert ([bool]::Parse($a['allStamped'])) "every lane stamped its completion marker before WaitAll returned"
Assert ([long]$a['afterBarrier'] -ge [long]$a['maxStamp']) "conductor's post-barrier clock ($($a['afterBarrier'])) is at/after the slowest lane's stamp ($($a['maxStamp'])) - WaitAll held for the laggard"
$expectedFloor = 3 * 40 - 15
Assert ([long]$a['waitAllMs'] -ge $expectedFloor) "WaitAll blocked for at least the slowest lane's sleep (elapsed $($a['waitAllMs']) ms, expected >= $expectedFloor ms; lane sleeps [$($a['sleeps'])] ms)"
Assert ([int]$a['quorumMet'] -ge 2) "WaitN(2) reports quorum met (met=$($a['quorumMet']))"
Assert ([long]$a['waitNMs'] -lt 80) "WaitN(2) returned ($($a['waitNMs']) ms) before the 80/120ms laggard lanes finished - quorum, not a full barrier"
Write-Host "  measured: WaitAll elapsed $($a['waitAllMs']) ms (lane sleeps [$($a['sleeps'])] ms); WaitN(2) elapsed $($a['waitNMs']) ms"

# ============================================================================
# (b) Hazard guard (negative test)
# ============================================================================
Write-Host ""
Write-Host "(b) reassignment refused while refcount nonzero (negative test)" -ForegroundColor Cyan
$b = ParseKv ($harness.GetMethod('Hazard').Invoke($null, @()))
Assert ([int]$b['rcSeed'] -eq 1) "bufA seeded at refcount floor (rc=$($b['rcSeed']), expected 1 = Alloc's own standing reference)"
Assert ([int]$b['rcHeld'] -eq 2) "straggler Open'd bufA (rc=$($b['rcHeld'])) - a live holder exists"
Assert ([bool]::Parse($b['threw'])) "Bind THREW while bufA's refcount ($($b['rcHeld'])) was above the floor (1) - reassignment refused, not silently allowed"
Assert ($b['exType'] -eq 'DpGangHazardException') "the thrown exception is DpGangHazardException (got: $($b['exType']))"
Assert ([bool]::Parse($b['stillAliasesA'])) "role 0 STILL aliases bufA after the refused Bind - no partial/silent reassignment"
Assert ([int]$b['rcDrained'] -eq 1) "straggler Closed bufA back to the floor (rc=$($b['rcDrained']))"
Assert ([bool]::Parse($b['boundOk'])) "Bind SUCCEEDS once the prior holder drained to the floor"
Assert ([bool]::Parse($b['nowAliasesB'])) "role 0 now aliases bufB - the HANDLE was repointed, not a byte copy"

# ============================================================================
# (c) Numerics
# ============================================================================
Write-Host ""
Write-Host "(c) retrofitted call site matches the scalar dot-product oracle" -ForegroundColor Cyan
$c = ParseKv ($harness.GetMethod('Oracle').Invoke($null, @()))
$maxAbsDiff = [double]$c['maxAbsDiff']; $maxRef = [double]$c['maxRef']
$tol = 1e-4 * [Math]::Max(1.0, $maxRef)
Write-Host "  measured: max|diff| = $('{0:E3}' -f $maxAbsDiff) vs max|ref| = $('{0:F4}' -f $maxRef) over M=3 x N=6 (K=64, nBlk=2)"
Assert ($maxAbsDiff -le $tol) "DpxGang-retrofitted MatMulNBits matches the scalar dot-product oracle (max|diff| = $('{0:E3}' -f $maxAbsDiff), tol = $('{0:E3}' -f $tol))"

$pass = $fails.Count -eq 0
Write-Host ""
Write-Host ($(if($pass){"PASS - DpxGang lanes lockstep under Fence.WaitAll/WaitN (not ad hoc Parallel.For), a role reassignment is refused while the prior buffer's refcount is above the floor and succeeds once drained, and the DpxGang-retrofitted Dpx.MatMulNBits still matches the scalar dot-product oracle."}else{"FAIL ($($fails.Count)): $($fails -join '; ')"})) -ForegroundColor $(if($pass){'Green'}else{'Red'})
[pscustomobject]@{
    test        = 'test.dpx.gang'
    pass        = $pass
    waitAllMs   = $a['waitAllMs']
    waitNMs     = $a['waitNMs']
    hazardOk    = ([bool]::Parse($b['threw']) -and [bool]::Parse($b['boundOk']))
    maxAbsDiff  = $c['maxAbsDiff']
    verdict     = $(if($pass){'lockstep (WaitAll/WaitN) + hazard-refused reassignment + oracle-matched retrofit, all measured, not asserted-in-prose'}else{'see failures'})
}
