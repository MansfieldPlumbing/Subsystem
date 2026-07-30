// DpxRoute.cs - per-model MatMulNBits route table (CRQ190 R0). One winner per (M, N, K, block_size)
// shape, decided at RUNTIME by a direct timed comparison on first sight of the shape (Dp's resolver) -
// never a hardcoded winner table, which would be wrong on every other adapter. The table lives on the
// Dp instance (per loaded model) and dies with it; bare static Dispatch callers pass no table and are
// untouched. After a shape has raced, a dispatch costs one lock + TryGetValue on a value-tuple key -
// no allocation on the hot path.
using System.Collections.Generic;

namespace Subsystem.Dpx;

public sealed class DpxRoute
{
    static readonly Dictionary<(int M, int N, int K, int bs), (bool gpu, double cpuMs, double gpuMs)> s_winners = new();

    // The routed decision for a shape. Returns false when the shape has not raced yet.
    public bool Query(int M, int N, int K, int bs, out bool gpuWins)
    {
        lock (s_winners)
        {
            if (s_winners.TryGetValue((M, N, K, bs), out var w)) { gpuWins = w.gpu; return true; }
            gpuWins = false;
            return false;
        }
    }

    // Record a raced winner with its measured lane times (min over the timed reps, ms).
    public void Register(int M, int N, int K, int bs, bool gpuWins, double cpuMs, double gpuMs)
    {
        lock (s_winners) s_winners[(M, N, K, bs)] = (gpuWins, cpuMs, gpuMs);
    }

    // Receipt lines, one per raced shape: "M=1 N=2048 K=2048 bs=32 -> gpu (cpu 12.678 ms, gpu 5.621 ms)".
    public string[] EnumerateRoutes()
    {
        lock (s_winners)
        {
            var lines = new string[s_winners.Count];
            int i = 0;
            foreach (var kv in s_winners)
                lines[i++] = $"M={kv.Key.M} N={kv.Key.N} K={kv.Key.K} bs={kv.Key.bs} -> {(kv.Value.gpu ? "gpu" : "cpu")} (cpu {kv.Value.cpuMs:F3} ms, gpu {kv.Value.gpuMs:F3} ms)";
            return lines;
        }
    }
}
