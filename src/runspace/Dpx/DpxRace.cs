#nullable disable
// DpxRace.cs - MVP-1: the per-op race, the flow-scheduler seed. One MatMulNBits input set, TWO lanes
// racing per iteration: CPU = MatMulNBitsSimd (the Vector128 kernel), GPU = GpuMatMulNBits (the packed-q4
// D3D12/Vulkan seam). Both are called DIRECTLY through their internal seams with the same prep MatMulNBits
// itself does, so neither lane reads UseGpuMatMulNBits / ForceScalarMatMulNBits and nothing races on the
// global knobs. Two long-lived VOM-owned worker threads (Vom.Spawn under \Agent\Dpx\Race\{guid}, Terminated
// at the end - SS009: the VOM owns threads) each park on their own work CpuFence and ring a done CpuFence
// per iteration. The conductor Signals both work fences, Fence.WaitAny names the iteration's winner (a tie
// at the recheck resolves to index 0 = cpu; WaitAny scans in order), Fence.WaitAll closes the barrier, then
// the two outputs are parity-checked against each other and - once, on iteration 1 - against the scalar
// oracle (Dpx.ForceScalarMatMulNBits path). Each lane times ITS OWN compute: Stopwatch around the kernel
// call, recorded by the worker. Account for all memory within our runspace - fences only, no Task, no pool, no async.
using System;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx;

// The race receipt: per-iteration winners/timings plus the parity numbers. Arrays index by iteration.
// A lane fault ends the race and lands in CpuError/GpuError - the harness SKIPs on a device-absent
// GpuError instead of failing (same clean-skip shape as the standing GPU benches).
public sealed class DpxRaceRecord
{
    public int Iterations;          // requested
    public int Completed;           // iterations that ran to the barrier
    public string[] Winners;        // "cpu"/"gpu" per completed iteration, named by Fence.WaitAny
    public double[] CpuMs;          // per-iteration CPU-lane compute, timed by the CPU worker itself
    public double[] GpuMs;          // per-iteration GPU-lane compute, timed by the GPU worker itself
    public int CpuWins;
    public int GpuWins;
    public double MedianCpuMs;      // over completed iterations
    public double MedianGpuMs;
    public double ParityMaxRel;     // max rel diff, CPU lane vs GPU lane, across completed iterations
    public double OracleCpuMaxRel;  // scalar oracle vs CPU lane, iteration 1
    public double OracleGpuMaxRel;  // scalar oracle vs GPU lane, iteration 1
    public string CpuError;         // first CPU-lane fault (null = clean)
    public string GpuError;         // first GPU-lane fault (null = clean)
}

public static class DpxRace
{
    // Race the two MatMulNBits lanes over the same inputs for `iterations` rounds and return the receipt.
    // The conductor runs on the caller's thread; the lanes run on two VOM-owned workers spawned once here
    // and reclaimed by Terminate in the finally (cascade interrupts a parked worker if the race broke early).
    public static DpxRaceRecord Resolve(Tensor[] x, NodeProto n, int iterations)
    {
        if (x is null) throw new ArgumentNullException(nameof(x));
        if (n is null) throw new ArgumentNullException(nameof(n));
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), "at least one iteration");
        var rec = new DpxRaceRecord
        {
            Iterations = iterations,
            Winners = new string[iterations],
            CpuMs = new double[iterations],
            GpuMs = new double[iterations],
        };
        var cpuWork = new CpuFence(); var cpuDone = new CpuFence();
        var gpuWork = new CpuFence(); var gpuDone = new CpuFence();
        // one-element slots: the fence Signal/Wait pair is the publication barrier (Monitor = full fence),
        // and the conductor only reads a slot after WaitAll passed that iteration's phase.
        var cpuSlot = new Tensor[1]; var gpuSlot = new Tensor[1];
        var cpuErr = new Exception[1]; var gpuErr = new Exception[1];
        var owner = VomClass.CreateOwner($"\\Agent\\Dpx\\Race\\{Guid.NewGuid():N}");
        try
        {
            VomClass.Spawn(owner, "cpu", _ => LaneLoop(cpuWork, cpuDone, iterations, () => CpuLane(x, n), rec.CpuMs, cpuSlot, cpuErr));
            VomClass.Spawn(owner, "gpu", _ => LaneLoop(gpuWork, gpuDone, iterations, () => GpuLane(x, n), rec.GpuMs, gpuSlot, gpuErr));
            var done = new Fence[] { cpuDone, gpuDone };
            var targets = new ulong[2];
            double parityMax = 0, oracleCpu = 0, oracleGpu = 0;
            for (int it = 0; it < iterations; it++)
            {
                ulong ph = (ulong)(it + 1);
                targets[0] = ph; targets[1] = ph;
                cpuWork.Signal(ph); gpuWork.Signal(ph);
                int first = Fence.WaitAny(done, targets);   // the winner: first done fence at this phase
                Fence.WaitAll(done, targets);               // barrier: both lanes done before outputs are read
                if (cpuErr[0] != null) { rec.CpuError = $"{cpuErr[0].GetType().Name}: {cpuErr[0].Message}"; break; }
                if (gpuErr[0] != null) { rec.GpuError = $"{gpuErr[0].GetType().Name}: {gpuErr[0].Message}"; break; }
                rec.Winners[it] = first == 0 ? "cpu" : "gpu";
                if (first == 0) rec.CpuWins++; else rec.GpuWins++;
                parityMax = Math.Max(parityMax, MaxRelDiff(cpuSlot[0].AsF(), gpuSlot[0].AsF()));
                if (it == 0)
                {
                    // scalar-oracle spot check, ONCE per shape. The workers are parked at the NEXT phase and
                    // never read the knobs, so the save/flip/restore here cannot race a lane.
                    bool pf = Dpx.ForceScalarMatMulNBits, pg = Dpx.UseGpuMatMulNBits;
                    Dpx.ForceScalarMatMulNBits = true; Dpx.UseGpuMatMulNBits = false;
                    Tensor oracle;
                    try { oracle = Dpx.Dispatch(n, x)[0]; }
                    finally { Dpx.ForceScalarMatMulNBits = pf; Dpx.UseGpuMatMulNBits = pg; }
                    oracleCpu = MaxRelDiff(oracle.AsF(), cpuSlot[0].AsF());
                    oracleGpu = MaxRelDiff(oracle.AsF(), gpuSlot[0].AsF());
                }
                rec.Completed = it + 1;
            }
            rec.ParityMaxRel = parityMax;
            rec.OracleCpuMaxRel = oracleCpu;
            rec.OracleGpuMaxRel = oracleGpu;
            rec.MedianCpuMs = Median(rec.CpuMs, rec.Completed);
            rec.MedianGpuMs = Median(rec.GpuMs, rec.Completed);
            return rec;
        }
        finally { VomClass.Terminate(owner); }
    }

    // One lane's whole life: park on the work fence at each phase, run the lane once, record the time
    // (the worker times ITS OWN compute), publish output/fault, ring the done fence. A faulted lane rings
    // done and returns - the conductor reports the fault; nothing is swallowed (SS007).
    static void LaneLoop(CpuFence work, CpuFence done, int iterations, Func<Tensor> lane, double[] ms, Tensor[] slot, Exception[] err)
    {
        for (ulong i = 1; i <= (ulong)iterations; i++)
        {
            work.Wait(i);
            var sw = Stopwatch.StartNew();
            Tensor t = null; Exception e = null;
            try { t = lane(); } catch (Exception ex) { e = ex; }
            sw.Stop();
            ms[i - 1] = sw.Elapsed.TotalMilliseconds;
            slot[0] = t; err[0] = e;
            done.Signal(i);
            if (e != null) return;
        }
    }

    // The SAME prep MatMulNBits does before it picks a path: K/N/bits/bs off the node attrs, block geometry
    // derived, M from the activation count. Replicated so the lanes call the kernels directly, knob-free.
    static (int K, int N, int bits, int bs, int nBlk, int rowBytes, int zpRowBytes, int defZp, int M) Geometry(Tensor[] x, NodeProto n)
    {
        int K = (int)Attr(n, "K", 0), N = (int)Attr(n, "N", 0), bits = (int)Attr(n, "bits", 4), bs = (int)Attr(n, "block_size", 32);
        int nBlk = K / bs, rowBytes = nBlk * (bs * bits / 8), zpRowBytes = (nBlk * bits + 7) / 8, defZp = 1 << (bits - 1);
        int M = (int)(x[0].Count / K);
        return (K, N, bits, bs, nBlk, rowBytes, zpRowBytes, defZp, M);
    }

    // CPU lane = MatMulNBitsSimd, exactly as MatMulNBits would dispatch it (bits=4, block_size=32, SIMD).
    static Tensor CpuLane(Tensor[] x, NodeProto n)
    {
        var (K, N, bits, bs, nBlk, rowBytes, zpRowBytes, defZp, M) = Geometry(x, n);
        if (bits != 4 || bs != 32 || !Vector128.IsHardwareAccelerated)
            throw new NotSupportedException($"cpu lane needs bits=4 / block_size=32 / SIMD (bits={bits} bs={bs})");
        var a = x[0].AsF(); var scsp = x[2].AsF(); int scLen = scsp.Length;
        var bSpan = x[1].ReadRawb();
        bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        return Dpx.MatMulNBitsSimd(x, a, scsp, bSpan, zpSpan, K, N, M, nBlk, rowBytes, zpRowBytes, defZp, scLen);
    }

    // GPU lane = GpuMatMulNBits, exactly as MatMulNBits' GPU branch calls it - INCLUDING the per-call
    // ToArray copies, which are that seam's real per-op cost today; hiding them would flatter the GPU.
    static Tensor GpuLane(Tensor[] x, NodeProto n)
    {
        var (K, N, bits, bs, nBlk, rowBytes, zpRowBytes, _, M) = Geometry(x, n);
        if (bits != 4) throw new NotSupportedException($"gpu q4 lane needs bits=4 (bits={bits})");
        var a = x[0].AsF(); var scsp = x[2].AsF();
        var bSpan = x[1].ReadRawb();
        bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        return Dpx.GpuMatMulNBits(x, n, K, N, bs, nBlk, rowBytes, zpRowBytes, hasZp,
                                 a.ToArray(), scsp.ToArray(), bSpan.ToArray(),
                                 hasZp ? zpSpan.ToArray() : Array.Empty<byte>(), M);
    }

    static long Attr(NodeProto n, string name, long def)
    { foreach (var a in n.Attribute) if (a.Name == name) return a.I; return def; }

    // Relative diff with an abs floor of 1: |a-b| / max(1, |a|). For the standing shapes the outputs sit
    // well above 1, so this reads as a true relative error; near zero it degrades to absolute (no 0/0 blowup).
    static double MaxRelDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
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

    static double Median(double[] v, int count)
    {
        if (count <= 0) return 0;
        var s = new double[count];
        Array.Copy(v, s, count);
        Array.Sort(s);
        return (count & 1) == 1 ? s[count / 2] : 0.5 * (s[count / 2 - 1] + s[count / 2]);
    }
}
