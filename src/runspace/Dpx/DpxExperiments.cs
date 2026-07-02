// DpxExperiments.cs — deadline-drop fault-injection knobs for the MVP-2 best-effort-inference receipt
// (tests/bench.dpx.deadline-drop.ps1). Models a contribution that MISSES its deadline: a target
// MatMulNBits correction is simply SKIPPED — its output is replaced by a zero tensor of the correct
// shape, so the residual stream carries forward WITHOUT that layer's update (the merge adds zero).
//
// Same static-knob pattern class as Dp.UseGpuMatMulNBits / Dp.ForceScalarMatMulNBits (SS015-baselined):
// statics COMPUTE, they never remember/authorize/rendezvous across owners. The receipt sets these in-proc
// between decode runs and reads the capture back in the SAME process; nothing here crosses a turn owner or
// persists past the run — ResetRun() clears the per-run state.
using System.Collections.Generic;
using Onnx;

namespace Subsystem.Dpx;

public static class DpxExperiments
{
    // Drop the target node's contribution every Nth execution (0 = never drop). The target node runs once
    // per graph execution (a per-layer projection: once in prefill, once per decode step), so the per-target
    // call counter's modulus IS the execution cadence — no external token counter is threaded through the
    // decode loop.
    public static int DropEveryN = 0;

    // Match against a MatMulNBits node's FIRST output name (e.g. a mid-stack MLP down-proj). Empty = no target.
    public static string DropNodeContains = "";

    // How many target-node executions have been seen this run (prefill + each decode step). Reset per run.
    public static long TargetCalls = 0;

    // How many of those executions were actually dropped (returned a zero tensor). Reset per run.
    public static long DroppedCalls = 0;

    // When true, the lm_head MatMulNBits output (the final logits, M=1 under num_logits_to_keep) is captured
    // per step into CapturedLogits so the receipt can diff each drop-rate's logit trajectory against the
    // no-drop baseline. Captured value is a COPY (a managed float[]), safe after the source tensor is freed.
    public static bool CaptureLogits = false;
    public static readonly List<float[]> CapturedLogits = new();

    // Reset the per-run counters + capture buffer. The receipt calls this before each rate's decode so
    // counters and logit-capture belong to exactly one run.
    public static void ResetRun()
    {
        TargetCalls = 0;
        DroppedCalls = 0;
        CapturedLogits.Clear();
    }

    // Drop decision for the hook at the top of Dp.MatMulNBits. Increments the target counter on every target-
    // node execution and returns true when this execution lands on the drop cadence (counter % DropEveryN == 0).
    public static bool ShouldDrop(NodeProto n)
    {
        if (DropEveryN <= 0 || DropNodeContains.Length == 0) return false;
        if (n.Output.Count == 0 || !n.Output[0].Contains(DropNodeContains)) return false;
        long c = ++TargetCalls;
        if (c % DropEveryN != 0) return false;
        DroppedCalls++;
        return true;
    }

    // A zero tensor of MatMulNBits's output shape: A's shape (x[0]) with the last dim replaced by N (the node's
    // output-channel attribute). The residual merge downstream simply adds zero — the correction this call
    // would have produced is missed, exactly as if the contribution arrived after its deadline.
    public static Tensor AllocZeros(Tensor[] x, NodeProto n)
    {
        int N = 0;
        foreach (var a in n.Attribute) if (a.Name == "N") { N = (int)a.I; break; }
        var shape = (int[])x[0].Shape.Clone();
        shape[shape.Length - 1] = N;
        long count = 1;
        foreach (var d in shape) count *= d;
        return Tensor.F(new float[count], shape);
    }

    // Capture hook invoked from the Dispatch MatMulNBits case AFTER the kernel returns. Copies the LAST row
    // (the position actually sampled) of the lm_head logits into CapturedLogits when enabled.
    public static void RecordLogits(NodeProto n, Tensor y)
    {
        if (!CaptureLogits || y == null || n.Output.Count == 0 || !n.Output[0].Contains("lm_head")) return;
        int vocab = y.Shape[y.Shape.Length - 1];
        if (vocab <= 0) return;
        long rows = y.Count / vocab;
        var src = y.AsF();
        int off = (int)((rows - 1) * vocab);
        var row = new float[vocab];
        for (int i = 0; i < vocab; i++) row[i] = src[off + i];
        CapturedLogits.Add(row);
    }

    // Max |a[step][v] - b[step][v]| over the common step/vocab range — the receipt's per-rate logit-divergence
    // scalar (each drop rate's captured trajectory vs the no-drop baseline). Pure compute over passed buffers;
    // no static state. Lives here (compiled, fast) because the single-file bundle can't Add-Type at runtime and
    // an elementwise PowerShell loop over 262k*steps is impractical.
    public static double QueryDivergence(float[][] a, float[][] b)
    {
        if (a == null || b == null) return 0.0;
        int steps = a.Length < b.Length ? a.Length : b.Length;
        double m = 0.0;
        for (int s = 0; s < steps; s++)
        {
            float[] x = a[s], y = b[s];
            if (x == null || y == null) continue;
            int n = x.Length < y.Length ? x.Length : y.Length;
            for (int i = 0; i < n; i++)
            {
                double d = System.Math.Abs((double)x[i] - (double)y[i]);
                if (d > m) m = d;
            }
        }
        return m;
    }
}
