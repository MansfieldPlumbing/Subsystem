// Interp.cs - the dp-onnx engine (Tensor, Interp kernels, Gpu backend seam), split out of Program.cs
// so it compiles as library code in-proc (no CLI Main). The CLI entry + mode handlers stay in Program.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Onnx;

namespace DpOnnx;

public class Tensor
{
    public int[] Shape;
    public float[] Fp;     // float payload (null if int)
    public long[] Ip;      // int64 payload (null if float)
    public bool IsInt => Ip != null;
    public long Count { get { long n = 1; foreach (var d in Shape) n *= d; return n; } }
    public static Tensor F(float[] d, params int[] s) => new() { Fp = d, Shape = s };
    public static Tensor I(long[] d, params int[] s) => new() { Ip = d, Shape = s };
    public float[] AsF() => Fp ?? Array.ConvertAll(Ip, x => (float)x);
    public long[] AsI() => Ip ?? Array.ConvertAll(Fp, x => (long)x);
}

public class Interp
{
    readonly ModelProto _m;
    public Interp(ModelProto m) => _m = m;

    public static readonly HashSet<string> Implemented = new()
    {
        "Add","Sub","Mul","Div","Pow","MatMul","Gemm",
        "Relu","LeakyRelu","Sigmoid","Tanh","Sqrt","Rsqrt","Exp","Neg","Abs","Floor","Sin","Cos","Erf","Clip","Softplus","Reciprocal",
        "Reshape","Transpose","Unsqueeze","Squeeze","Concat","Identity","Constant","Cast","Shape","Gather","Flatten",
        "Equal","NotEqual","Greater","Less","GreaterOrEqual","LessOrEqual","And","Or","Not","Min","Max","Mod","Round","Atan","Sign",
        "Where","Expand","ConstantOfShape","Fill","Range","ReduceMean","ReduceSum","ReduceMax","CumSum","Slice","Pad",
        "Conv","ConvTranspose","LayerNormalization","Softmax","LSTM","Resize","STFT","NonZero","ScatterND","DynamicUpdateSlice",
        "Split","Unpack","Pack","Tile","GroupNormalization","Gelu","InstanceNormalization","ReduceProd","DequantizeLinear","QuantizeLinear",
    };

    public static bool Profile = false;
    public static readonly Dictionary<string, (double ms, long n)> Prof = new();
    public static double DropP = 0;                       // --drop p: prob of skipping a residual merge (stale-read / drop-path model)
    public static string DropScope = "";                  // --drop-scope: restrict drops to nodes whose Name contains this (gate to the data plane)
    public static readonly Random DropRng = new(1234);    // seeded -> reproducible rmse(p) sweep
    public static long Dropped = 0;

    Dictionary<string, Tensor> _winit;   // decoded initializers, cached: streaming Run()s many short feeds over ONE graph -> decode the 82M weights ONCE, not per chunk
    public Dictionary<string, Tensor> Run(Dictionary<string, Tensor> feed, Action<NodeProto, Tensor[], Dictionary<string, Tensor>> onNode = null)
    {
        if (_winit == null) { _winit = new Dictionary<string, Tensor>(); foreach (var init in _m.Graph.Initializer) _winit[init.Name] = FromProto(init); }
        var env = new Dictionary<string, Tensor>(_winit);   // shallow: kernels allocate fresh outputs, never mutate weight arrays in place -> safe to reuse across chunks
        foreach (var kv in feed) env[kv.Key] = kv.Value;
        foreach (var node in _m.Graph.Node)
        {
            var ins = node.Input.Select(n => string.IsNullOrEmpty(n) ? null : env[n]).ToArray();
            Tensor[] outs;
            if (Profile)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                outs = Dispatch(node, ins);
                double dt = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var e = Prof.GetValueOrDefault(node.OpType); Prof[node.OpType] = (e.ms + dt, e.n + 1);
            }
            else outs = Dispatch(node, ins);
            // stale-read model: with prob p, a residual merge's second-branch transfer is "skipped" -> the residual carries (drop-path).
            if (DropP > 0 && node.OpType == "Add" && (DropScope.Length == 0 || node.Name.Contains(DropScope)) && outs.Length > 0 && outs[0] != null && !outs[0].IsInt
                && ins.Length > 0 && ins[0] != null && ins[0].Count == outs[0].Count && DropRng.NextDouble() < DropP)
            { outs[0] = ins[0]; Dropped++; }
            for (int i = 0; i < node.Output.Count && i < outs.Length; i++) if (!string.IsNullOrEmpty(node.Output[i])) env[node.Output[i]] = outs[i];
            onNode?.Invoke(node, outs, env);   // env passed so a hook can INJECT an oracle value for downstream
        }
        return _m.Graph.Output.ToDictionary(o => o.Name, o => env[o.Name]);
    }

    public static Tensor[] Dispatch(NodeProto n, Tensor[] x)
    {
        switch (n.OpType)
        {
            case "Add": return One(BcastV(x[0], x[1], BinOp.Add));
            case "Sub": return One(BcastV(x[0], x[1], BinOp.Sub));
            case "Mul": return One(BcastV(x[0], x[1], BinOp.Mul));
            case "Div": return One(BcastV(x[0], x[1], BinOp.Div));
            case "Pow": return One(Bcast(x[0], x[1], (a, b) => MathF.Pow(a, b)));
            case "Relu": return One(Un(x[0], a => MathF.Max(0, a)));
            case "LeakyRelu": { float al = F(n, "alpha", 0.01f); return One(Un(x[0], a => a < 0 ? al * a : a)); }
            case "Sigmoid": return One(Un(x[0], a => 1f / (1f + MathF.Exp(-a))));
            case "Tanh": return One(Un(x[0], MathF.Tanh));
            case "Sqrt": return One(Un(x[0], MathF.Sqrt));
            case "Rsqrt": return One(Un(x[0], a => 1f / MathF.Sqrt(a)));   // gemma RMSNorm reciprocal-sqrt (tflite RSQRT)
            case "Exp": return One(Un(x[0], MathF.Exp));
            case "Neg": return One(Un(x[0], a => -a));
            case "Abs": return One(Un(x[0], MathF.Abs));
            case "Floor": return One(Un(x[0], MathF.Floor));
            case "Sin": return One(Un(x[0], a => (float)Math.Sin(a)));   // double range-reduction: huge SineGen phase (~1.5e5)
            case "Cos": return One(Un(x[0], a => (float)Math.Cos(a)));
            case "Erf": return One(Un(x[0], Erf));
            case "Reciprocal": return One(Un(x[0], a => 1f / a));
            case "Softplus": return One(Un(x[0], a => MathF.Log(1 + MathF.Exp(a))));
            case "Clip": { float lo = x.Length > 1 && x[1] != null ? x[1].AsF()[0] : float.NegativeInfinity;
                           float hi = x.Length > 2 && x[2] != null ? x[2].AsF()[0] : float.PositiveInfinity;
                           return One(Un(x[0], a => MathF.Min(hi, MathF.Max(lo, a)))); }
            case "MatMul": return One(UseGpuMatMul ? GpuMatMul(x[0], x[1]) : MatMul(x[0], x[1]));
            case "Gemm": return One(Gemm(n, x));
            case "Identity": return One(x[0]);
            case "Constant": return One(FromProto(n.Attribute.First(a => a.Name == "value").T));
            case "Cast": return One(x[0]);   // payloads are already widened; layout-preserving
            case "DequantizeLinear": return One(DequantizeLinear(x, n));
            case "QuantizeLinear": return One(QuantizeLinear(x, n));
            case "Reshape": return One(Reshape(x[0], x[1]));
            case "Flatten": return One(Reshape(x[0], null, flattenAxis: (int)L(n, "axis", 1), src: x[0]));
            case "Squeeze": return One(Squeeze(x[0], x.Length > 1 ? x[1] : null, n));
            case "Unsqueeze": return One(Unsqueeze(x[0], x.Length > 1 ? x[1] : null, n));
            case "Transpose": return One(Transpose(x[0], Ints(n, "perm")));
            case "Concat": return One(Concat(x, (int)L(n, "axis", 0)));
            case "Shape": return One(Tensor.I(Array.ConvertAll(x[0].Shape, s => (long)s), x[0].Shape.Length));
            case "Gather": return One(Gather(x[0], x[1], (int)L(n, "axis", 0)));
            case "Equal": return One(Cmp(x[0], x[1], (a, b) => a == b));
            case "NotEqual": return One(Cmp(x[0], x[1], (a, b) => a != b));
            case "Greater": return One(Cmp(x[0], x[1], (a, b) => a > b));
            case "Less": return One(Cmp(x[0], x[1], (a, b) => a < b));
            case "GreaterOrEqual": return One(Cmp(x[0], x[1], (a, b) => a >= b));
            case "LessOrEqual": return One(Cmp(x[0], x[1], (a, b) => a <= b));
            case "And": return One(Cmp(x[0], x[1], (a, b) => a != 0 && b != 0));
            case "Or": return One(Cmp(x[0], x[1], (a, b) => a != 0 || b != 0));
            case "Not": return One(UnI(x[0], v => v == 0 ? 1L : 0L));
            case "Min": return One(VarEl(x, MathF.Min));
            case "Max": return One(VarEl(x, MathF.Max));
            case "Mod": { bool fm = L(n, "fmod", 0) != 0; return One(Bcast(x[0], x[1], (a, b) => fm ? a % b : (float)((((long)a % (long)b) + (long)b) % (long)b))); }
            case "Round": return One(Un(x[0], a => MathF.Round(a, MidpointRounding.ToEven)));
            case "Atan": return One(Un(x[0], MathF.Atan));
            case "Sign": return One(Un(x[0], a => (float)MathF.Sign(a)));
            case "Where": return One(Where(x[0], x[1], x[2]));
            case "Expand": return One(Expand(x[0], x[1]));
            case "ConstantOfShape": return One(ConstantOfShape(x[0], n));
            case "Range": return One(Range(x[0], x[1], x[2]));
            case "ReduceMean": return One(Reduce(x, n, "mean"));
            case "ReduceSum": return One(Reduce(x, n, "sum"));
            case "ReduceMax": return One(Reduce(x, n, "max"));
            case "CumSum": return One(CumSum(x[0], (int)x[1].AsI()[0], L(n, "exclusive", 0) != 0, L(n, "reverse", 0) != 0));
            case "Slice": return One(Slice(x, n));
            case "Pad": return One(Pad(x, n));
            case "Conv": return One(Conv(x, n));
            case "ConvTranspose": return One(ConvTranspose(x, n));
            case "LayerNormalization": return One(LayerNorm(x, n));
            case "Softmax": return One(Softmax(x[0], (int)L(n, "axis", -1)));
            case "Split": return Split(x, n);
            case "Tile": return One(Tile(x[0], x[1]));
            case "GroupNormalization": return One(GroupNorm(x, n));
            case "Gelu": return One(Gelu(x[0], n));
            case "Resize": return One(Resize(x, n));
            case "STFT": return One(Stft(x, n));
            case "NonZero": return One(NonZero(x[0]));
            case "ScatterND": return One(ScatterND(x, n));
            case "LSTM": return Lstm(x, n);
            case "InstanceNormalization": return One(InstanceNorm(x, n));
            case "ReduceProd": return One(ReduceProd(x, n));
            case "Unpack": return UnpackOp(x, n);
            case "DynamicUpdateSlice": return One(DynamicUpdateSliceOp(x, n));
            case "Pack": return One(PackOp(x, n));
            case "Fill": return One(FillOp(x, n));
            default: throw new NotImplementedException($"node #?: {n.OpType} (name={n.Name})");
        }
    }

    // ---- kernels ----
    static Tensor[] One(Tensor t) => new[] { t };

    static Tensor Un(Tensor a, Func<float, float> f)
    { var d = a.AsF(); var o = new float[d.Length]; for (int i = 0; i < d.Length; i++) o[i] = f(d[i]); return Tensor.F(o, a.Shape); }

    // The coordinate along `axis` for linear index i, given shape sh (for per-axis quant scale/zero-point lookup).
    static int AxisCoord(long i, int[] sh, int axis)
    {
        if (axis < 0) axis += sh.Length;
        long inner = 1; for (int k = axis + 1; k < sh.Length; k++) inner *= sh[k];
        return (int)((i / inner) % sh[axis]);
    }

    // DequantizeLinear (ONNX): y = (x - zero_point) * scale. Per-tensor when scale is scalar, else per-axis along
    // `axis`. The tflite DEQUANTIZE that lifts gemma's int weights/activations back to float for the f32 interp.
    static Tensor DequantizeLinear(Tensor[] x, NodeProto n)
    {
        var q = x[0].AsF(); var scale = x[1].AsF();
        var zp = x.Length > 2 && x[2] != null ? x[2].AsF() : null;
        bool perAxis = scale.Length > 1; int axis = (int)L(n, "axis", 1);
        var o = new float[q.Length];
        for (long i = 0; i < q.Length; i++)
        {
            int c = perAxis ? AxisCoord(i, x[0].Shape, axis) : 0;
            o[i] = (q[i] - (zp != null ? zp[c] : 0f)) * scale[c];
        }
        return Tensor.F(o, x[0].Shape);
    }

    // QuantizeLinear (ONNX): y = clamp(round(x/scale) + zero_point). Per-tensor or per-axis along `axis`. The
    // saturation range defaults to int8 [-128,127] (the common LLM activation width); per-quantized-dtype
    // saturation is refined when the tflite translator wires the tensor's quantized type. The fake-quant the
    // gemma graph round-trips activations through (a QUANTIZE/DEQUANTIZE pair around each int matmul).
    static Tensor QuantizeLinear(Tensor[] x, NodeProto n)
    {
        var v = x[0].AsF(); var scale = x[1].AsF();
        var zp = x.Length > 2 && x[2] != null ? x[2].AsF() : null;
        bool perAxis = scale.Length > 1; int axis = (int)L(n, "axis", 1);
        const long QMIN = -128, QMAX = 127;
        var o = new long[v.Length];
        for (long i = 0; i < v.Length; i++)
        {
            int c = perAxis ? AxisCoord(i, x[0].Shape, axis) : 0;
            long q = (long)MathF.Round(v[i] / scale[c], MidpointRounding.ToEven) + (long)(zp != null ? zp[c] : 0f);
            o[i] = Math.Clamp(q, QMIN, QMAX);
        }
        return Tensor.I(o, x[0].Shape);
    }

    // SIMD one contiguous run: dst = a OP b, where a/b are each either a contiguous run or a broadcast scalar (1-elem span).
    static void VecBinRun(Span<float> dst, ReadOnlySpan<float> a, bool aScalar, ReadOnlySpan<float> b, bool bScalar, BinOp op)
    {
        int vw = Vector<float>.Count, n = dst.Length, i = 0;
        var sa = aScalar ? new Vector<float>(a[0]) : default; var sb = bScalar ? new Vector<float>(b[0]) : default;
        for (; i + vw <= n; i += vw)
        {
            var va = aScalar ? sa : new Vector<float>(a.Slice(i));
            var vb = bScalar ? sb : new Vector<float>(b.Slice(i));
            (op switch { BinOp.Add => va + vb, BinOp.Sub => va - vb, BinOp.Mul => va * vb, _ => va / vb }).CopyTo(dst.Slice(i));
        }
        float af = aScalar ? a[0] : 0, bf = bScalar ? b[0] : 0;
        for (; i < n; i++) { float x = aScalar ? af : a[i], y = bScalar ? bf : b[i]; dst[i] = op switch { BinOp.Add => x + y, BinOp.Sub => x - y, BinOp.Mul => x * y, _ => x / y }; }
    }

    public enum BinOp { Add, Sub, Mul, Div }
    // Vectorized Add/Sub/Mul/Div: SIMD Vector<float> when shapes match (the common generator case); op-switch (no delegate) on broadcast.
    static Tensor BcastV(Tensor a, Tensor b, BinOp op)
    {
        var fa = a.AsF(); var fb = b.AsF();
        if (a.Shape.AsSpan().SequenceEqual(b.Shape))
        {
            int n = fa.Length, vw = Vector<float>.Count, i = 0; var o = new float[n];
            for (; i + vw <= n; i += vw)
                (op switch { BinOp.Add => new Vector<float>(fa, i) + new Vector<float>(fb, i), BinOp.Sub => new Vector<float>(fa, i) - new Vector<float>(fb, i), BinOp.Mul => new Vector<float>(fa, i) * new Vector<float>(fb, i), _ => new Vector<float>(fa, i) / new Vector<float>(fb, i) }).CopyTo(o, i);
            for (; i < n; i++) o[i] = op switch { BinOp.Add => fa[i] + fb[i], BinOp.Sub => fa[i] - fb[i], BinOp.Mul => fa[i] * fb[i], _ => fa[i] / fb[i] };
            return Tensor.F(o, a.Shape);
        }
        int[] sh = BroadcastShape(a.Shape, b.Shape);
        long n2 = 1; foreach (var d in sh) n2 *= d; var oo = new float[n2];
        var (sa, sb) = (Strides(a.Shape, sh), Strides(b.Shape, sh));
        int rank = sh.Length, last = rank - 1, inner = rank > 0 ? sh[last] : 1; long outer = inner > 0 ? n2 / inner : 0;
        if (rank > 0 && sa[last] <= 1 && sb[last] <= 1 && inner >= Vector<float>.Count)   // innermost contiguous(1)/broadcast(0) in both -> SIMD the inner run, parallel over outer
        {
            bool sa0 = sa[last] == 0, sb0 = sb[last] == 0;
            System.Threading.Tasks.Parallel.For(0L, outer, o =>
            {
                long rem = o, ia0 = 0, ib0 = 0;
                for (int k = last - 1; k >= 0; k--) { int d = (int)(rem % sh[k]); rem /= sh[k]; ia0 += d * sa[k]; ib0 += d * sb[k]; }
                VecBinRun(oo.AsSpan(checked((int)(o * inner)), inner), fa.AsSpan(checked((int)ia0), sa0 ? 1 : inner), sa0, fb.AsSpan(checked((int)ib0), sb0 ? 1 : inner), sb0, op);
            });
            return Tensor.F(oo, sh);
        }
        var idx = new int[rank];
        for (long lin = 0; lin < n2; lin++)
        {
            long ia = 0, ib = 0;
            for (int k = 0; k < rank; k++) { ia += idx[k] * sa[k]; ib += idx[k] * sb[k]; }
            float x = fa[ia], y = fb[ib];
            oo[lin] = op switch { BinOp.Add => x + y, BinOp.Sub => x - y, BinOp.Mul => x * y, _ => x / y };
            for (int k = rank - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
        }
        return Tensor.F(oo, sh);
    }

    static Tensor Bcast(Tensor a, Tensor b, Func<float, float, float> f)
    {
        var fa = a.AsF(); var fb = b.AsF();
        int[] sh = BroadcastShape(a.Shape, b.Shape);
        long n = 1; foreach (var d in sh) n *= d;
        var o = new float[n];
        var (sa, sb) = (Strides(a.Shape, sh), Strides(b.Shape, sh));
        var idx = new int[sh.Length];
        for (long lin = 0; lin < n; lin++)
        {
            long ia = 0, ib = 0;
            for (int k = 0; k < sh.Length; k++) { ia += idx[k] * sa[k]; ib += idx[k] * sb[k]; }
            o[lin] = f(fa[ia], fb[ib]);
            for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
        }
        return Tensor.F(o, sh);
    }

    static int[] BroadcastShape(int[] a, int[] b)
    {
        int r = Math.Max(a.Length, b.Length); var o = new int[r];
        for (int k = 0; k < r; k++)
        { int da = k < r - a.Length ? 1 : a[k - (r - a.Length)]; int db = k < r - b.Length ? 1 : b[k - (r - b.Length)]; o[k] = Math.Max(da, db); }
        return o;
    }
    static long[] Strides(int[] shape, int[] outShape)
    {   // strides of `shape` aligned to outShape (0 where broadcast)
        int r = outShape.Length; var st = new long[r]; long acc = 1; int off = r - shape.Length;
        for (int k = shape.Length - 1; k >= 0; k--) { st[off + k] = shape[k] == 1 ? 0 : acc; acc *= shape[k]; }
        for (int k = 0; k < off; k++) st[k] = 0;
        return st;
    }

    // --- GPU MatMul: route each 2D [M,K]@[K,N] batch through dpgpu.dll (D3D12). Opt-in via --gpu-matmul. ---
    public static bool UseGpuMatMul = false;
    static byte[] _gemmDxil; static bool _gpuDead = false;
    static byte[] GemmDxil()
    {
        if (_gemmDxil != null) return _gemmDxil;
        string near = Path.Combine(AppContext.BaseDirectory, "gemm.dxil");
        string p = File.Exists(near) ? near : @"S:\qnn-project\workspace\onnx-interp\_gpu\gemm.dxil";
        return _gemmDxil = File.ReadAllBytes(p);
    }
    static Tensor GpuMatMul(Tensor A, Tensor B)
    {   // mirrors MatMul's 4D broadcast, but dispatches each batch's GEMM to the GPU; ANY GPU fault degrades to the
        // bit-exact CPU path (inv-9): missing loader (vulkan-1.dll/libvulkan.so/d3d12.dll), no device, no shader
        // (gemm.dxil/gemm.spv), or a nonzero rc — all fall back to MatMul, once, logged. Assume Vulkan on Android 16;
        // if it isn't there, CPU carries the GEMM. _gpuDead latches so we don't re-probe a dead GPU each node.
        if (_gpuDead) return MatMul(A, B);
        var a = A.AsF(); var b = B.AsF();
        int ra = A.Shape.Length, rb = B.Shape.Length;
        int M = A.Shape[ra - 2], K = A.Shape[ra - 1], N = B.Shape[rb - 1];
        int[] leadA = A.Shape[..(ra - 2)], leadB = B.Shape[..(rb - 2)];
        int[] lead = BroadcastShape(leadA, leadB);
        int nb = lead.Length; long outBatch = 1; foreach (var d in lead) outBatch *= d;
        long[] sA = Strides(leadA, lead), sB = Strides(leadB, lead);
        var o = new float[outBatch * M * N];
        var aSub = new float[(long)M * K]; var bSub = new float[(long)K * N]; var cSub = new float[(long)M * N];
        try
        {
            byte[] dxil = GemmDxil(); var bidx = new int[nb];
            for (long bi = 0; bi < outBatch; bi++)
            {
                long aB = 0, bB = 0; for (int k = 0; k < nb; k++) { aB += bidx[k] * sA[k]; bB += bidx[k] * sB[k]; }
                Array.Copy(a, aB * M * K, aSub, 0, (long)M * K);
                Array.Copy(b, bB * K * N, bSub, 0, (long)K * N);
                int rc = Gpu.dpgpu_gemm(aSub, bSub, cSub, (uint)M, (uint)N, (uint)K, dxil, (uint)dxil.Length);
                if (rc != 0) throw new InvalidOperationException($"dpgpu_gemm rc={rc}");
                Array.Copy(cSub, 0, o, bi * M * N, (long)M * N);
                for (int k = nb - 1; k >= 0; k--) { if (++bidx[k] < lead[k]) break; bidx[k] = 0; }
            }
        }
        catch (Exception ex)
        {
            _gpuDead = true;
            Console.Error.WriteLine($"dp-onnx: GPU GEMM unavailable ({ex.GetType().Name}: {ex.Message}); falling back to CPU.");
            return MatMul(A, B);
        }
        var sh = new int[nb + 2]; for (int k = 0; k < nb; k++) sh[k] = lead[k]; sh[nb] = M; sh[nb + 1] = N;
        return Tensor.F(o, sh);
    }

    // dst[j] += a * src[j], SIMD over j (System.Numerics.Vector<float>; JIT emits FMA on AVX2). float-accumulate = GPU-GEMM precision.
    static void AxpyInto(Span<float> dst, ReadOnlySpan<float> src, float a)
    {
        int vw = Vector<float>.Count, n = dst.Length, j = 0; var va = new Vector<float>(a);
        for (; j + vw <= n; j += vw)
            (new Vector<float>(dst.Slice(j)) + va * new Vector<float>(src.Slice(j))).CopyTo(dst.Slice(j));
        for (; j < n; j++) dst[j] += a * src[j];
    }

    static Tensor MatMul(Tensor A, Tensor B)
    {   // [..,M,K] x [..,K,N] with FULL leading-dim broadcast (preserves rank — attention is 4D)
        var a = A.AsF(); var b = B.AsF();
        int ra = A.Shape.Length, rb = B.Shape.Length;
        int M = A.Shape[ra - 2], K = A.Shape[ra - 1], N = B.Shape[rb - 1];
        int[] leadA = A.Shape[..(ra - 2)];
        int[] leadB = B.Shape[..(rb - 2)];
        int[] lead = BroadcastShape(leadA, leadB);        // broadcasted batch dims
        int nb = lead.Length; long outBatch = 1; foreach (var d in lead) outBatch *= d;
        long[] sA = Strides(leadA, lead);                  // batch-unit strides (0 where broadcast)
        long[] sB = Strides(leadB, lead);
        var o = new float[outBatch * M * N];
        // precompute per-batch input offsets (outBatch is small), then parallelize over (batch,row) — k-order preserved -> bit-identical
        var aOff = new long[outBatch]; var bOff = new long[outBatch];
        { var bidx = new int[nb]; for (long bi = 0; bi < outBatch; bi++) { long aB = 0, bB = 0; for (int k = 0; k < nb; k++) { aB += bidx[k] * sA[k]; bB += bidx[k] * sB[k]; } aOff[bi] = aB * M * K; bOff[bi] = bB * K * N; for (int k = nb - 1; k >= 0; k--) { if (++bidx[k] < lead[k]) break; bidx[k] = 0; } } }
        System.Threading.Tasks.Parallel.For(0L, outBatch * M, r =>
        {
            long bi = r / M; int i = (int)(r % M);
            long bo = bOff[bi], orow = (bi * M + i) * (long)N, aRow = aOff[bi] + (long)i * K;
            var dst = o.AsSpan(checked((int)orow), N); dst.Clear();
            for (int k = 0; k < K; k++)   // axpy: dst += a[i,k] * B_row_k (SIMD over N); k-order preserved
            {
                float aik = a[checked((int)(aRow + k))];
                if (aik != 0f) AxpyInto(dst, b.AsSpan(checked((int)(bo + (long)k * N)), N), aik);
            }
        });
        var sh = new int[nb + 2];
        for (int k = 0; k < nb; k++) sh[k] = lead[k];
        sh[nb] = M; sh[nb + 1] = N;
        return Tensor.F(o, sh);
    }

    static Tensor Gemm(NodeProto n, Tensor[] x)
    {
        float alpha = F(n, "alpha", 1), beta = F(n, "beta", 1);
        bool ta = L(n, "transA", 0) != 0, tb = L(n, "transB", 0) != 0;
        var A = ta ? Transpose(x[0], new[] { 1, 0 }) : x[0];
        var B = tb ? Transpose(x[1], new[] { 1, 0 }) : x[1];
        var m = MatMul(A, B); var md = m.Fp;
        for (int i = 0; i < md.Length; i++) md[i] *= alpha;
        if (x.Length > 2 && x[2] != null) { var cb = Bcast(m, x[2], (p, q) => p + beta * q); return cb; }
        return m;
    }

    static Tensor Reshape(Tensor a, Tensor shapeT, int flattenAxis = -1, Tensor src = null)
    {
        int[] sh;
        if (flattenAxis >= 0)
        { long outer = 1; for (int k = 0; k < flattenAxis; k++) outer *= src.Shape[k]; sh = new[] { (int)outer, (int)(src.Count / outer) }; }
        else
        {
            var want = shapeT.AsI(); sh = new int[want.Length]; long known = 1; int neg = -1;
            for (int k = 0; k < want.Length; k++) { if (want[k] == -1) neg = k; else if (want[k] == 0) { sh[k] = a.Shape[k]; known *= sh[k]; } else { sh[k] = (int)want[k]; known *= sh[k]; } }
            if (neg >= 0) sh[neg] = (int)(a.Count / known);
        }
        return a.IsInt ? Tensor.I(a.Ip, sh) : Tensor.F(a.Fp, sh);
    }

    static Tensor Squeeze(Tensor a, Tensor axesT, NodeProto n)
    {
        var axes = axesT?.AsI()?.Select(v => (int)(v < 0 ? v + a.Shape.Length : v)).ToHashSet();
        var sh = new List<int>();
        for (int k = 0; k < a.Shape.Length; k++) if (!(a.Shape[k] == 1 && (axes == null || axes.Contains(k)))) sh.Add(a.Shape[k]);
        return a.IsInt ? Tensor.I(a.Ip, sh.ToArray()) : Tensor.F(a.Fp, sh.ToArray());
    }

    static Tensor Unsqueeze(Tensor a, Tensor axesT, NodeProto n)
    {
        var axesList = (axesT?.AsI() ?? Ints(n, "axes").Select(v => (long)v).ToArray());
        int r = a.Shape.Length + axesList.Length;
        var axes = axesList.Select(v => (int)(v < 0 ? v + r : v)).ToHashSet();
        var sh = new int[r]; int si = 0;
        for (int k = 0; k < r; k++) sh[k] = axes.Contains(k) ? 1 : a.Shape[si++];
        return a.IsInt ? Tensor.I(a.Ip, sh) : Tensor.F(a.Fp, sh);
    }

    static Tensor Transpose(Tensor a, int[] perm)
    {
        int r = a.Shape.Length;
        if (perm == null || perm.Length == 0) { perm = new int[r]; for (int k = 0; k < r; k++) perm[k] = r - 1 - k; }
        var outShape = new int[r]; for (int k = 0; k < r; k++) outShape[k] = a.Shape[perm[k]];
        var inStr = ContigStrides(a.Shape); var d = a.AsF(); var o = new float[d.Length];
        var idx = new int[r];
        for (long lin = 0; lin < d.Length; lin++)
        {
            long src = 0; for (int k = 0; k < r; k++) src += idx[k] * inStr[perm[k]];
            o[lin] = d[src];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < outShape[k]) break; idx[k] = 0; }
        }
        return Tensor.F(o, outShape);
    }

    static Tensor Concat(Tensor[] xs, int axis)
    {
        int r = xs[0].Shape.Length; if (axis < 0) axis += r;
        var outShape = (int[])xs[0].Shape.Clone(); outShape[axis] = xs.Sum(t => t.Shape[axis]);
        long n = 1; foreach (var d in outShape) n *= d; var o = new float[n];
        long outStrAxis = 1; for (int k = axis + 1; k < r; k++) outStrAxis *= outShape[k];
        long blocks = 1; for (int k = 0; k < axis; k++) blocks *= outShape[k];
        long outRow = outShape[axis] * outStrAxis;
        long offAxis = 0;
        foreach (var t in xs)
        {
            var d = t.AsF(); long inRow = t.Shape[axis] * outStrAxis;
            for (long bl = 0; bl < blocks; bl++) Array.Copy(d, bl * inRow, o, bl * outRow + offAxis * outStrAxis, inRow);
            offAxis += t.Shape[axis];
        }
        return Tensor.F(o, outShape);
    }

    static Tensor Gather(Tensor data, Tensor indices, int axis)
    {
        int r = data.Shape.Length; if (axis < 0) axis += r;
        long outer = 1; for (int k = 0; k < axis; k++) outer *= data.Shape[k];
        long axisLen = data.Shape[axis];
        long inner = 1; for (int k = axis + 1; k < r; k++) inner *= data.Shape[k];
        var idx = indices.AsI();
        var outShape = new List<int>(); for (int k = 0; k < axis; k++) outShape.Add(data.Shape[k]);
        foreach (var di in indices.Shape) outShape.Add(di);
        for (int k = axis + 1; k < r; k++) outShape.Add(data.Shape[k]);
        bool fInt = data.IsInt; var df = fInt ? null : data.Fp; var dl = fInt ? data.Ip : null;
        long total = outer * idx.Length * inner;
        var of = fInt ? null : new float[total]; var ol = fInt ? new long[total] : null;
        long w = 0;
        for (long o = 0; o < outer; o++)
            foreach (var ix0 in idx)
            { long ix = ix0 < 0 ? ix0 + axisLen : ix0; long baseI = (o * axisLen + ix) * inner;
              if (fInt) Array.Copy(dl, baseI, ol, w, inner); else Array.Copy(df, baseI, of, w, inner); w += inner; }
        return fInt ? Tensor.I(ol, outShape.ToArray()) : Tensor.F(of, outShape.ToArray());
    }

    static Tensor UnI(Tensor a, Func<long, long> f)
    { var d = a.AsI(); var o = new long[d.Length]; for (int i = 0; i < d.Length; i++) o[i] = f(d[i]); return Tensor.I(o, a.Shape); }

    static Tensor Cmp(Tensor a, Tensor b, Func<float, float, bool> f)
    {
        var fa = a.AsF(); var fb = b.AsF();
        int[] sh = BroadcastShape(a.Shape, b.Shape); long n = 1; foreach (var d in sh) n *= d;
        var o = new long[n]; var (sa, sb) = (Strides(a.Shape, sh), Strides(b.Shape, sh)); var idx = new int[sh.Length];
        for (long lin = 0; lin < n; lin++)
        {
            long ia = 0, ib = 0; for (int k = 0; k < sh.Length; k++) { ia += idx[k] * sa[k]; ib += idx[k] * sb[k]; }
            o[lin] = f(fa[ia], fb[ib]) ? 1L : 0L;
            for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
        }
        return Tensor.I(o, sh);
    }

    static Tensor VarEl(Tensor[] xs, Func<float, float, float> f)
    { var acc = xs[0]; for (int i = 1; i < xs.Length; i++) acc = Bcast(acc, xs[i], f); return acc; }

    static Tensor Where(Tensor c, Tensor x, Tensor y)
    {
        int[] sh = BroadcastShape(BroadcastShape(c.Shape, x.Shape), y.Shape); long n = 1; foreach (var d in sh) n *= d;
        var sc = Strides(c.Shape, sh); var sx = Strides(x.Shape, sh); var sy = Strides(y.Shape, sh);
        var cf = c.AsF(); var idx = new int[sh.Length];
        if (x.IsInt && y.IsInt)
        {
            var xi = x.Ip; var yi = y.Ip; var o = new long[n];
            for (long lin = 0; lin < n; lin++)
            { long ic = 0, ix = 0, iy = 0; for (int k = 0; k < sh.Length; k++) { ic += idx[k] * sc[k]; ix += idx[k] * sx[k]; iy += idx[k] * sy[k]; }
              o[lin] = cf[ic] != 0 ? xi[ix] : yi[iy]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } }
            return Tensor.I(o, sh);
        }
        var xf = x.AsF(); var yf = y.AsF(); var of = new float[n];
        for (long lin = 0; lin < n; lin++)
        { long ic = 0, ix = 0, iy = 0; for (int k = 0; k < sh.Length; k++) { ic += idx[k] * sc[k]; ix += idx[k] * sx[k]; iy += idx[k] * sy[k]; }
          of[lin] = cf[ic] != 0 ? xf[ix] : yf[iy]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } }
        return Tensor.F(of, sh);
    }

    static Tensor Expand(Tensor a, Tensor shapeT)
    {
        var tgt = Array.ConvertAll(shapeT.AsI(), v => (int)v);
        int[] sh = BroadcastShape(a.Shape, tgt); long n = 1; foreach (var d in sh) n *= d;
        var sa = Strides(a.Shape, sh); var idx = new int[sh.Length];
        if (a.IsInt) { var d = a.Ip; var o = new long[n]; for (long lin = 0; lin < n; lin++) { long ia = 0; for (int k = 0; k < sh.Length; k++) ia += idx[k] * sa[k]; o[lin] = d[ia]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } } return Tensor.I(o, sh); }
        else { var d = a.Fp; var o = new float[n]; for (long lin = 0; lin < n; lin++) { long ia = 0; for (int k = 0; k < sh.Length; k++) ia += idx[k] * sa[k]; o[lin] = d[ia]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } } return Tensor.F(o, sh); }
    }

    static Tensor ConstantOfShape(Tensor shapeT, NodeProto n)
    {
        var sh = Array.ConvertAll(shapeT.AsI(), v => (int)v); long cnt = 1; foreach (var d in sh) cnt *= d;
        var va = n.Attribute.FirstOrDefault(a => a.Name == "value");
        if (va != null && va.T != null)
        {
            var vt = FromProto(va.T);
            if (vt.IsInt) { var o = new long[cnt]; var v = vt.Ip[0]; for (long i = 0; i < cnt; i++) o[i] = v; return Tensor.I(o, sh); }
            else { var o = new float[cnt]; var v = vt.Fp[0]; for (long i = 0; i < cnt; i++) o[i] = v; return Tensor.F(o, sh); }
        }
        return Tensor.F(new float[cnt], sh);
    }

    static Tensor Range(Tensor start, Tensor limit, Tensor delta)
    {
        if (start.IsInt)
        { long s = start.Ip[0], l = limit.AsI()[0], d = delta.AsI()[0]; var li = new List<long>();
          if (d > 0) for (long v = s; v < l; v += d) li.Add(v); else if (d < 0) for (long v = s; v > l; v += d) li.Add(v); return Tensor.I(li.ToArray(), li.Count); }
        else
        { float s = start.Fp[0], l = limit.AsF()[0], d = delta.AsF()[0]; var lf = new List<float>();
          if (d > 0) for (float v = s; v < l; v += d) lf.Add(v); else if (d < 0) for (float v = s; v > l; v += d) lf.Add(v); return Tensor.F(lf.ToArray(), lf.Count); }
    }

    static double SimdSum(ReadOnlySpan<float> s)
    {
        int vw = Vector<float>.Count, n = s.Length, i = 0; var acc = Vector<float>.Zero;
        for (; i + vw <= n; i += vw) acc += new Vector<float>(s.Slice(i));
        double r = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) r += s[i];
        return r;
    }

    static Tensor Reduce(Tensor[] x, NodeProto n, string mode)   // mode: "sum" | "mean" | "max" -- one loop, not a per-op copy
    {
        var a = x[0]; var d = a.AsF(); int r = a.Shape.Length;
        bool keep = L(n, "keepdims", 1) != 0;
        long[] axesL = (x.Length > 1 && x[1] != null && x[1].Count > 0) ? x[1].AsI() : Array.ConvertAll(Ints(n, "axes"), v => (long)v);
        var axes = axesL.Length == 0 ? new HashSet<int>(Enumerable.Range(0, r)) : new HashSet<int>(axesL.Select(v => (int)(v < 0 ? v + r : v)));
        var osh = new List<int>(); var outDimOfIn = new int[r];
        for (int k = 0; k < r; k++)
        {
            if (axes.Contains(k)) { if (keep) { outDimOfIn[k] = osh.Count; osh.Add(1); } else outDimOfIn[k] = -1; }
            else { outDimOfIn[k] = osh.Count; osh.Add(a.Shape[k]); }
        }
        int[] oshA = osh.Count == 0 ? new[] { 1 } : osh.ToArray(); var oStr = ContigStrides(oshA);
        long outN = 1; foreach (var dd in oshA) outN *= dd; long reduced = d.Length / Math.Max(1, outN);
        bool isMax = mode == "max", isMean = mode == "mean";
        int m = axes.Count; bool trailing = m > 0; for (int k = r - m; k < r; k++) if (k < 0 || !axes.Contains(k)) trailing = false;
        if (trailing && !isMax)   // contiguous trailing block -> each output = SIMD sum of a contiguous run (sum/mean only)
        {
            var of = new float[outN];
            System.Threading.Tasks.Parallel.For(0L, outN, i => { double s = SimdSum(d.AsSpan(checked((int)(i * reduced)), (int)reduced)); of[i] = (float)(isMean ? s / reduced : s); });
            return Tensor.F(of, oshA);
        }
        var acc = new double[outN];
        if (isMax) for (long i = 0; i < outN; i++) acc[i] = double.NegativeInfinity;
        var idx = new int[r];
        for (long lin = 0; lin < d.Length; lin++)
        {
            long oi = 0; for (int k = 0; k < r; k++) { int od = outDimOfIn[k]; if (od >= 0) { int coord = axes.Contains(k) && keep ? 0 : idx[k]; oi += coord * oStr[od]; } }
            if (isMax) acc[oi] = Math.Max(acc[oi], d[lin]); else acc[oi] += d[lin];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        var o = new float[outN];
        for (long i = 0; i < outN; i++) o[i] = (float)(isMean ? acc[i] / reduced : acc[i]);
        return Tensor.F(o, oshA);
    }

    static Tensor CumSum(Tensor a, int axis, bool excl, bool rev)
    {
        int r = a.Shape.Length; if (axis < 0) axis += r; var src = a.AsF();
        long inner = 1; for (int k = axis + 1; k < r; k++) inner *= a.Shape[k];
        int A = a.Shape[axis]; long outer = 1; for (int k = 0; k < axis; k++) outer *= a.Shape[k];
        var o = new float[src.Length];
        for (long ob = 0; ob < outer; ob++) for (long inr = 0; inr < inner; inr++)
        {
            long baseI = ob * A * inner + inr; float run = 0;
            for (int t = 0; t < A; t++) { int ti = rev ? A - 1 - t : t; long pos = baseI + (long)ti * inner; if (excl) { o[pos] = run; run += src[pos]; } else { run += src[pos]; o[pos] = run; } }
        }
        return Tensor.F(o, a.Shape);
    }

    static Tensor Slice(Tensor[] x, NodeProto n)
    {
        var a = x[0]; int r = a.Shape.Length;
        long[] starts = x[1].AsI(); long[] ends = x[2].AsI();
        long[] axesA = (x.Length > 3 && x[3] != null) ? x[3].AsI() : Enumerable.Range(0, starts.Length).Select(i => (long)i).ToArray();
        long[] stepsA = (x.Length > 4 && x[4] != null) ? x[4].AsI() : Enumerable.Repeat(1L, starts.Length).ToArray();
        var st = new int[r]; var en = new int[r]; var stp = new int[r];
        for (int k = 0; k < r; k++) { st[k] = 0; en[k] = a.Shape[k]; stp[k] = 1; }
        for (int i = 0; i < axesA.Length; i++)
        {
            int ax = (int)axesA[i]; if (ax < 0) ax += r; int step = (int)stepsA[i]; stp[ax] = step;
            long s = starts[i], e = ends[i]; int dim = a.Shape[ax];
            if (s < 0) s += dim; if (e < 0) e += dim;
            if (step > 0) { s = Math.Clamp(s, 0, dim); e = Math.Clamp(e, 0, dim); } else { s = Math.Clamp(s, 0, dim - 1); e = Math.Clamp(e, -1, dim - 1); }
            st[ax] = (int)s; en[ax] = (int)e;
        }
        var osh = new int[r]; for (int k = 0; k < r; k++) osh[k] = (int)Math.Max(0, Math.Ceiling((en[k] - st[k]) / (double)stp[k]));
        long outN = 1; foreach (var dd in osh) outN *= dd; var inStr = ContigStrides(a.Shape); var idx = new int[r];
        if (a.IsInt) { var d = a.Ip; var o = new long[outN]; for (long lin = 0; lin < outN; lin++) { long si = 0; for (int k = 0; k < r; k++) si += (st[k] + idx[k] * stp[k]) * inStr[k]; o[lin] = d[si]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < osh[k]) break; idx[k] = 0; } } return Tensor.I(o, osh); }
        else { var d = a.Fp; var o = new float[outN]; for (long lin = 0; lin < outN; lin++) { long si = 0; for (int k = 0; k < r; k++) si += (st[k] + idx[k] * stp[k]) * inStr[k]; o[lin] = d[si]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < osh[k]) break; idx[k] = 0; } } return Tensor.F(o, osh); }
    }

    static Tensor Pad(Tensor[] x, NodeProto n)
    {
        var a = x[0]; int r = a.Shape.Length; long[] pads = x[1].AsI(); string mode = Str(n, "mode", "constant");
        float cval = (x.Length > 2 && x[2] != null) ? x[2].AsF()[0] : 0f;
        long[] axesA = (x.Length > 3 && x[3] != null) ? x[3].AsI() : Enumerable.Range(0, r).Select(i => (long)i).ToArray();
        var begin = new int[r]; var end = new int[r];
        for (int i = 0; i < axesA.Length; i++) { int ax = (int)axesA[i]; if (ax < 0) ax += r; begin[ax] = (int)pads[i]; end[ax] = (int)pads[i + axesA.Length]; }
        var osh = new int[r]; for (int k = 0; k < r; k++) osh[k] = a.Shape[k] + begin[k] + end[k];
        long outN = 1; foreach (var dd in osh) outN *= dd; var inStr = ContigStrides(a.Shape); var d = a.AsF(); var o = new float[outN];
        if (mode == "constant") for (long i = 0; i < outN; i++) o[i] = cval;
        var idx = new int[r];
        for (long lin = 0; lin < outN; lin++)
        {
            bool inside = true; long si = 0;
            for (int k = 0; k < r; k++)
            {
                int src = idx[k] - begin[k];
                if (mode == "reflect") { int dim = a.Shape[k]; if (dim > 1) { int period = 2 * dim - 2; src = ((src % period) + period) % period; if (src >= dim) src = period - src; } else src = 0; }
                else if (mode == "edge") { src = Math.Clamp(src, 0, a.Shape[k] - 1); }
                else { if (src < 0 || src >= a.Shape[k]) { inside = false; break; } }
                si += (long)src * inStr[k];
            }
            if (mode == "constant") { if (inside) o[lin] = d[si]; } else o[lin] = d[si];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < osh[k]) break; idx[k] = 0; }
        }
        return Tensor.F(o, osh);
    }

    static int[] AttrIntsOr(NodeProto n, string name, int count, int def)
    { var v = Ints(n, name); if (v.Length > 0) return v; var o = new int[count]; for (int i = 0; i < count; i++) o[i] = def; return o; }

    static float Sig(float v) => 1f / (1f + MathF.Exp(-v));

    // ONNX LSTM. X[seq,batch,inp]; W[dir,4H,inp]; R[dir,4H,H]; B[dir,8H]; gate order i,o,f,c.
    // Outputs Y[seq,dir,batch,H], Y_h[dir,batch,H], Y_c[dir,batch,H].
    static Tensor[] Lstm(Tensor[] x, NodeProto n)
    {
        var X = x[0].AsF(); int seq = x[0].Shape[0], batch = x[0].Shape[1], inp = x[0].Shape[2];
        var Wf = x[1].AsF(); var Rf = x[2].AsF(); int numDir = x[1].Shape[0];
        int H = (int)L(n, "hidden_size", x[1].Shape[1] / 4);
        var Bf = (x.Length > 3 && x[3] != null) ? x[3].AsF() : null;
        var initH = (x.Length > 5 && x[5] != null) ? x[5].AsF() : null;
        var initC = (x.Length > 6 && x[6] != null) ? x[6].AsF() : null;
        string dir = Str(n, "direction", "forward");
        var Y = new float[(long)seq * numDir * batch * H];
        var Yh = new float[(long)numDir * batch * H]; var Yc = new float[(long)numDir * batch * H];
        int wDS = 4 * H * inp, rDS = 4 * H * H, bDS = 8 * H;
        for (int d = 0; d < numDir; d++)
        {
            bool rev = dir == "reverse" || (dir == "bidirectional" && d == 1);
            var h = new float[batch * H]; var c = new float[batch * H];
            if (initH != null) Array.Copy(initH, (long)d * batch * H, h, 0, batch * H);
            if (initC != null) Array.Copy(initC, (long)d * batch * H, c, 0, batch * H);
            var gate = new float[4 * H];
            for (int ti = 0; ti < seq; ti++)
            {
                int t = rev ? seq - 1 - ti : ti;
                for (int b = 0; b < batch; b++)
                {
                    for (int row = 0; row < 4 * H; row++)
                    {
                        float v = 0; long wb = (long)d * wDS + (long)row * inp; long xb = ((long)t * batch + b) * inp;
                        for (int k = 0; k < inp; k++) v += Wf[wb + k] * X[xb + k];
                        long rb = (long)d * rDS + (long)row * H; long hb = (long)b * H;
                        for (int k = 0; k < H; k++) v += Rf[rb + k] * h[hb + k];
                        if (Bf != null) v += Bf[(long)d * bDS + row] + Bf[(long)d * bDS + 4 * H + row];
                        gate[row] = v;
                    }
                    for (int j = 0; j < H; j++)
                    {
                        float it = Sig(gate[j]), ot = Sig(gate[H + j]), ft = Sig(gate[2 * H + j]), ctil = MathF.Tanh(gate[3 * H + j]);
                        int ci = b * H + j; float ct = ft * c[ci] + it * ctil; c[ci] = ct;
                        float ht = ot * MathF.Tanh(ct); h[ci] = ht;
                        Y[(((long)t * numDir + d) * batch + b) * H + j] = ht;
                    }
                }
            }
            Array.Copy(h, 0, Yh, (long)d * batch * H, batch * H);
            Array.Copy(c, 0, Yc, (long)d * batch * H, batch * H);
        }
        return new[] { Tensor.F(Y, seq, numDir, batch, H), Tensor.F(Yh, numDir, batch, H), Tensor.F(Yc, numDir, batch, H) };
    }

    static Tensor Conv(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var W = x[1]; var bf = (x.Length > 2 && x[2] != null) ? x[2].AsF() : null;
        var xf = X.AsF(); var wf = W.AsF();
        int rank = X.Shape.Length, sp = rank - 2;
        int N = X.Shape[0], M = W.Shape[0], CinG = W.Shape[1];
        int group = (int)L(n, "group", 1);
        int[] ksh = new int[sp]; for (int i = 0; i < sp; i++) ksh[i] = W.Shape[2 + i];
        int[] strides = AttrIntsOr(n, "strides", sp, 1);
        int[] dil = AttrIntsOr(n, "dilations", sp, 1);
        int[] pads = AttrIntsOr(n, "pads", 2 * sp, 0);
        int[] isz = new int[sp]; for (int i = 0; i < sp; i++) isz[i] = X.Shape[2 + i];
        string ap = Str(n, "auto_pad", "NOTSET");
        if (ap == "SAME_UPPER" || ap == "SAME_LOWER")
            for (int i = 0; i < sp; i++) { int outS = (isz[i] + strides[i] - 1) / strides[i]; int eff = (ksh[i] - 1) * dil[i] + 1; int tot = Math.Max(0, (outS - 1) * strides[i] + eff - isz[i]); if (ap == "SAME_UPPER") { pads[i] = tot / 2; pads[sp + i] = tot - tot / 2; } else { pads[sp + i] = tot / 2; pads[i] = tot - tot / 2; } }
        else if (ap == "VALID") for (int i = 0; i < sp; i++) { pads[i] = 0; pads[sp + i] = 0; }
        int[] osz = new int[sp]; for (int i = 0; i < sp; i++) { int eff = (ksh[i] - 1) * dil[i] + 1; osz[i] = (isz[i] + pads[i] + pads[sp + i] - eff) / strides[i] + 1; }
        var outShape = new int[rank]; outShape[0] = N; outShape[1] = M; for (int i = 0; i < sp; i++) outShape[2 + i] = osz[i];
        long outN = 1; foreach (var d in outShape) outN *= d; var o = new float[outN];
        var xStr = ContigStrides(X.Shape);
        int mPerG = M / group;
        long spatial = 1; foreach (var s in osz) spatial *= s;
        long kcount = 1; foreach (var kk in ksh) kcount *= kk;
        int K = (int)(CinG * kcount);   // GEMM inner dim = Cin/group * kernel-volume
        // im2col + GEMM: each (batch,group) conv = W_g[mPerG,K] @ col[K,spatial].
        // col row index (c*kcount+kk) preserves the direct-conv c-outer/kk-inner accumulation order -> oracle-equivalent.
        for (int nn = 0; nn < N; nn++)
            for (int g = 0; g < group; g++)
            {
                var col = new float[(long)K * spatial];
                System.Threading.Tasks.Parallel.For(0, K, ck =>
                {
                    int c = ck / (int)kcount, kk = ck % (int)kcount, cin = g * CinG + c;
                    long xBaseC = (long)nn * xStr[0] + (long)cin * xStr[1];
                    var kpos = new int[sp]; { long t = kk; for (int d = sp - 1; d >= 0; d--) { kpos[d] = (int)(t % ksh[d]); t /= ksh[d]; } }
                    var pos = new int[sp]; long colBase = (long)ck * spatial;
                    for (long s = 0; s < spatial; s++)
                    {
                        bool valid = true; long xoff = xBaseC;
                        for (int d = 0; d < sp; d++) { int ip = pos[d] * strides[d] + kpos[d] * dil[d] - pads[d]; if (ip < 0 || ip >= isz[d]) valid = false; xoff += (long)ip * xStr[2 + d]; }
                        col[colBase + s] = valid ? xf[xoff] : 0f;
                        for (int d = sp - 1; d >= 0; d--) { if (++pos[d] < osz[d]) break; pos[d] = 0; }
                    }
                });
                float[] wg; if (group == 1) wg = wf; else { wg = new float[(long)mPerG * K]; Array.Copy(wf, (long)g * mPerG * K, wg, 0, (long)mPerG * K); }
                var prod = (UseGpuMatMul ? GpuMatMul(Tensor.F(wg, mPerG, K), Tensor.F(col, K, (int)spatial))
                                         : MatMul(Tensor.F(wg, mPerG, K), Tensor.F(col, K, (int)spatial))).Fp;
                for (int m = 0; m < mPerG; m++)
                {
                    int oc = g * mPerG + m; long oBase = ((long)nn * M + oc) * spatial, pBase = (long)m * spatial;
                    float bias = bf != null ? bf[oc] : 0f;
                    for (long s = 0; s < spatial; s++) o[oBase + s] = prod[pBase + s] + bias;
                }
            }
        return Tensor.F(o, outShape);
    }

    // ONNX ConvTranspose (deconv) as scatter-add. X[N,Cin,*isz]; W[Cin,Cout/group,*ksz]; B[Cout].
    static Tensor ConvTranspose(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var W = x[1]; var bf = (x.Length > 2 && x[2] != null) ? x[2].AsF() : null;
        var xf = X.AsF(); var wf = W.AsF();
        int rank = X.Shape.Length, sp = rank - 2;
        int N = X.Shape[0], Cin = X.Shape[1];
        int group = (int)L(n, "group", 1);
        int CoutPerG = W.Shape[1], Cout = CoutPerG * group, CinPerG = Cin / group;
        int[] ksh = new int[sp]; for (int i = 0; i < sp; i++) ksh[i] = W.Shape[2 + i];
        int[] strides = AttrIntsOr(n, "strides", sp, 1);
        int[] dil = AttrIntsOr(n, "dilations", sp, 1);
        int[] outPad = AttrIntsOr(n, "output_padding", sp, 0);
        int[] pads = AttrIntsOr(n, "pads", 2 * sp, 0);
        int[] isz = new int[sp]; for (int i = 0; i < sp; i++) isz[i] = X.Shape[2 + i];
        int[] outShapeAttr = Ints(n, "output_shape");
        int[] osz = new int[sp];
        if (outShapeAttr.Length == sp)
        {
            string ap = Str(n, "auto_pad", "NOTSET");
            for (int i = 0; i < sp; i++)
            {
                osz[i] = outShapeAttr[i];
                int total = Math.Max(0, strides[i] * (isz[i] - 1) + outPad[i] + ((ksh[i] - 1) * dil[i] + 1) - osz[i]);
                if (ap == "SAME_UPPER") { pads[i] = total / 2; pads[sp + i] = total - total / 2; }
                else { pads[sp + i] = total / 2; pads[i] = total - total / 2; }
            }
        }
        else for (int i = 0; i < sp; i++) osz[i] = strides[i] * (isz[i] - 1) + outPad[i] + ((ksh[i] - 1) * dil[i] + 1) - pads[i] - pads[sp + i];

        var outShape = new int[rank]; outShape[0] = N; outShape[1] = Cout; for (int i = 0; i < sp; i++) outShape[2 + i] = osz[i];
        long outN = 1; foreach (var d in outShape) outN *= d; var o = new float[outN];
        var xStr = ContigStrides(X.Shape); var wStr = ContigStrides(W.Shape); var oStr = ContigStrides(outShape);
        long inSpatial = 1; foreach (var s in isz) inSpatial *= s;
        long kcount = 1; foreach (var k in ksh) kcount *= k;
        // parallelize over (batch,group,out-channel) with cinL inner — each out-channel region is written by exactly one task,
        // and the cinL->s->kk accumulation order into each output element is unchanged -> bit-identical, no races.
        int ctTasks = N * group * CoutPerG;
        System.Threading.Tasks.Parallel.For(0, ctTasks, t =>
        {
            int coutL = t % CoutPerG; int g = (t / CoutPerG) % group; int nn = t / (CoutPerG * group);
            int cout = g * CoutPerG + coutL;
            long oBaseC = (long)nn * oStr[0] + (long)cout * oStr[1];
            var ipos = new int[sp]; var kpos = new int[sp];
            for (int cinL = 0; cinL < CinPerG; cinL++)
            {
                int cin = g * CinPerG + cinL;
                long wBase = (long)cin * wStr[0] + (long)coutL * wStr[1];
                long xBase = (long)nn * xStr[0] + (long)cin * xStr[1];
                Array.Clear(ipos, 0, sp);
                for (long s = 0; s < inSpatial; s++)
                {
                    long xoff = xBase; for (int d = 0; d < sp; d++) xoff += (long)ipos[d] * xStr[2 + d];
                    float val = xf[xoff];
                    if (val != 0)
                    {
                        Array.Clear(kpos, 0, sp);
                        for (long kk = 0; kk < kcount; kk++)
                        {
                            bool valid = true; long ooff = oBaseC; long woff = wBase;
                            for (int d = 0; d < sp; d++)
                            {
                                int op = ipos[d] * strides[d] + kpos[d] * dil[d] - pads[d];
                                if (op < 0 || op >= osz[d]) valid = false;
                                ooff += (long)op * oStr[2 + d]; woff += (long)kpos[d] * wStr[2 + d];
                            }
                            if (valid) o[ooff] += val * wf[woff];
                            for (int d = sp - 1; d >= 0; d--) { if (++kpos[d] < ksh[d]) break; kpos[d] = 0; }
                        }
                    }
                    for (int d = sp - 1; d >= 0; d--) { if (++ipos[d] < isz[d]) break; ipos[d] = 0; }
                }
            }
        });
        if (bf != null)
        {
            long spat = 1; foreach (var s in osz) spat *= s;
            for (int nn = 0; nn < N; nn++)
                for (int cout = 0; cout < Cout; cout++)
                { long b = (long)nn * oStr[0] + (long)cout * oStr[1]; for (long s = 0; s < spat; s++) o[b + s] += bf[cout]; }
        }
        return Tensor.F(o, outShape);
    }

    static Tensor LayerNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var sf = x[1].AsF(); var bf = (x.Length > 2 && x[2] != null) ? x[2].AsF() : null;
        var xf = X.AsF(); int r = X.Shape.Length; int axis = (int)L(n, "axis", -1); if (axis < 0) axis += r;
        float eps = F(n, "epsilon", 1e-5f);
        long inner = 1; for (int k = axis; k < r; k++) inner *= X.Shape[k];
        long outer = X.Count / inner; var o = new float[X.Count];
        for (long ob = 0; ob < outer; ob++)
        {
            long baseI = ob * inner;
            double mean = 0; for (long i = 0; i < inner; i++) mean += xf[baseI + i]; mean /= inner;
            double var = 0; for (long i = 0; i < inner; i++) { double dd = xf[baseI + i] - mean; var += dd * dd; } var /= inner;
            double inv = 1.0 / Math.Sqrt(var + eps);
            for (long i = 0; i < inner; i++)
            { float norm = (float)((xf[baseI + i] - mean) * inv); o[baseI + i] = norm * sf[i % sf.Length] + (bf != null ? bf[i % bf.Length] : 0f); }
        }
        return Tensor.F(o, X.Shape);
    }

    static Tensor Softmax(Tensor a, int axis)
    {
        int r = a.Shape.Length; if (axis < 0) axis += r; var d = a.AsF(); var o = new float[d.Length];
        long inner = 1; for (int k = axis + 1; k < r; k++) inner *= a.Shape[k];
        int A = a.Shape[axis]; long outer = 1; for (int k = 0; k < axis; k++) outer *= a.Shape[k];
        for (long ob = 0; ob < outer; ob++) for (long inr = 0; inr < inner; inr++)
        {
            long baseI = ob * A * inner + inr;
            float mx = float.NegativeInfinity; for (int t = 0; t < A; t++) mx = MathF.Max(mx, d[baseI + (long)t * inner]);
            double sum = 0; for (int t = 0; t < A; t++) { double e = Math.Exp(d[baseI + (long)t * inner] - mx); o[baseI + (long)t * inner] = (float)e; sum += e; }
            for (int t = 0; t < A; t++) o[baseI + (long)t * inner] /= (float)sum;
        }
        return Tensor.F(o, a.Shape);
    }

    // ONNX Split — split data along axis into chunks (split input or num_outputs attr). [drafted by Antigravity #3, reviewed]
    static Tensor[] Split(Tensor[] x, NodeProto n)
    {
        var data = x[0]; int r = data.Shape.Length; int axis = (int)L(n, "axis", 0); if (axis < 0) axis += r;
        int dimVal = data.Shape[axis];
        int[] splitSizes;
        if (x.Length > 1 && x[1] != null && x[1].Count > 0) splitSizes = Array.ConvertAll(x[1].AsI(), v => (int)v);
        else
        {
            int num = (int)L(n, "num_outputs", -1);
            if (num <= 0) throw new ArgumentException("Split: needs split input or num_outputs");
            splitSizes = new int[num]; int chunk = (dimVal + num - 1) / num; int rem = dimVal;
            for (int i = 0; i < num; i++) { int cur = Math.Min(chunk, rem); splitSizes[i] = Math.Max(0, cur); rem -= cur; }
        }
        long innerSize = 1; for (int k = axis + 1; k < r; k++) innerSize *= data.Shape[k];
        long blocks = 1; for (int k = 0; k < axis; k++) blocks *= data.Shape[k];
        long inRow = dimVal * innerSize; long offAxis = 0;
        var outputs = new Tensor[splitSizes.Length]; bool isInt = data.IsInt;
        for (int i = 0; i < splitSizes.Length; i++)
        {
            var outShape = (int[])data.Shape.Clone(); outShape[axis] = splitSizes[i];
            long outRow = splitSizes[i] * innerSize; long outSize = blocks * outRow;
            if (isInt) { var o = new long[outSize]; for (long bl = 0; bl < blocks; bl++) Array.Copy(data.Ip, bl * inRow + offAxis * innerSize, o, bl * outRow, outRow); outputs[i] = Tensor.I(o, outShape); }
            else { var o = new float[outSize]; for (long bl = 0; bl < blocks; bl++) Array.Copy(data.Fp, bl * inRow + offAxis * innerSize, o, bl * outRow, outRow); outputs[i] = Tensor.F(o, outShape); }
            offAxis += splitSizes[i];
        }
        return outputs;
    }

    // ONNX Tile — repeat a along each axis by repeats[d]. [drafted by Antigravity #3, reviewed]
    static Tensor Tile(Tensor a, Tensor repeats)
    {
        int r = a.Shape.Length; var rep = repeats.AsI();
        var outShape = new int[r]; for (int i = 0; i < r; i++) outShape[i] = a.Shape[i] * (int)rep[i];
        long outN = 1; foreach (var d in outShape) outN *= d; var inStr = ContigStrides(a.Shape); var idx = new int[r];
        if (a.IsInt) { var d = a.Ip; var o = new long[outN]; for (long lin = 0; lin < outN; lin++) { long src = 0; for (int k = 0; k < r; k++) src += (idx[k] % a.Shape[k]) * inStr[k]; o[lin] = d[src]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < outShape[k]) break; idx[k] = 0; } } return Tensor.I(o, outShape); }
        else { var d = a.Fp; var o = new float[outN]; for (long lin = 0; lin < outN; lin++) { long src = 0; for (int k = 0; k < r; k++) src += (idx[k] % a.Shape[k]) * inStr[k]; o[lin] = d[src]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < outShape[k]) break; idx[k] = 0; } } return Tensor.F(o, outShape); }
    }

    // ONNX GroupNormalization — normalize over groups of channels + spatial, per-channel affine. [drafted by Antigravity #3, reviewed]
    static Tensor GroupNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var sf = x[1].AsF(); var bf = x[2].AsF(); var xf = X.AsF();
        int r = X.Shape.Length, N = X.Shape[0], C = X.Shape[1];
        long H = 1; for (int k = 2; k < r; k++) H *= X.Shape[k];
        int num = (int)L(n, "num_groups", -1); if (num <= 0) throw new ArgumentException("GroupNormalization: num_groups");
        float eps = F(n, "epsilon", 1e-5f); int G = C / num; long groupElements = (long)G * H;
        var o = new float[X.Count];
        for (int nn = 0; nn < N; nn++)
            for (int g = 0; g < num; g++)
            {
                double sum = 0;
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; for (long h = 0; h < H; h++) sum += xf[b + h]; }
                double mean = sum / groupElements;
                double sumSq = 0;
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; for (long h = 0; h < H; h++) { double diff = xf[b + h] - mean; sumSq += diff * diff; } }
                double invStd = 1.0 / Math.Sqrt(sumSq / groupElements + eps);
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; float sv = sf[c], bv = bf[c]; for (long h = 0; h < H; h++) o[b + h] = (float)((xf[b + h] - mean) * invStd * sv + bv); }
            }
        return Tensor.F(o, X.Shape);
    }

    // ONNX InstanceNormalization (input, scale, B; epsilon) — normalize each channel independently over its
    // spatial dims (= GroupNormalization with num_groups = channels). scale/B are per-channel [C].
    static Tensor InstanceNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var sf = x[1].AsF(); var bf = x[2].AsF(); var xf = X.AsF();
        int r = X.Shape.Length, N = X.Shape[0], C = X.Shape[1];
        long H = 1; for (int k = 2; k < r; k++) H *= X.Shape[k];
        float eps = F(n, "epsilon", 1e-5f);
        var o = new float[X.Count];
        for (int nn = 0; nn < N; nn++)
            for (int c = 0; c < C; c++)
            {
                long b = ((long)nn * C + c) * H;
                double sum = 0; for (long h = 0; h < H; h++) sum += xf[b + h];
                double mean = sum / H;
                double sumSq = 0; for (long h = 0; h < H; h++) { double diff = xf[b + h] - mean; sumSq += diff * diff; }
                double invStd = 1.0 / Math.Sqrt(sumSq / H + eps);
                float sv = sf[c], bv = bf[c];
                for (long h = 0; h < H; h++) o[b + h] = (float)((xf[b + h] - mean) * invStd * sv + bv);
            }
        return Tensor.F(o, X.Shape);
    }

    // ONNX ReduceProd — product reduction over axes (axes/keepdims like ReduceMean/Sum, multiplicative).
    // Preserves int for the common shape-product case (feeds Reshape/Expand); float otherwise.
    static Tensor ReduceProd(Tensor[] x, NodeProto n)
    {
        var a = x[0]; int r = a.Shape.Length;
        bool keep = L(n, "keepdims", 1) != 0;
        long[] axesL = (x.Length > 1 && x[1] != null && x[1].Count > 0) ? x[1].AsI() : Array.ConvertAll(Ints(n, "axes"), v => (long)v);
        var axes = axesL.Length == 0 ? new HashSet<int>(Enumerable.Range(0, r)) : new HashSet<int>(axesL.Select(v => (int)(v < 0 ? v + r : v)));
        var osh = new List<int>(); var outDimOfIn = new int[r];
        for (int k = 0; k < r; k++)
        {
            if (axes.Contains(k)) { if (keep) { outDimOfIn[k] = osh.Count; osh.Add(1); } else outDimOfIn[k] = -1; }
            else { outDimOfIn[k] = osh.Count; osh.Add(a.Shape[k]); }
        }
        int[] oshA = osh.Count == 0 ? new[] { 1 } : osh.ToArray(); var oStr = ContigStrides(oshA);
        long outN = 1; foreach (var dd in oshA) outN *= dd;
        var idx = new int[r];
        long OutIndex() { long oi = 0; for (int k = 0; k < r; k++) { int od = outDimOfIn[k]; if (od >= 0) { int coord = axes.Contains(k) && keep ? 0 : idx[k]; oi += coord * oStr[od]; } } return oi; }
        void Step() { for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; } }
        if (a.IsInt)
        {
            var ip = a.Ip; var acc = new long[outN]; for (long i = 0; i < outN; i++) acc[i] = 1;
            for (long lin = 0; lin < ip.Length; lin++) { acc[OutIndex()] *= ip[lin]; Step(); }
            return Tensor.I(acc, oshA);
        }
        var d = a.AsF(); var accF = new double[outN]; for (long i = 0; i < outN; i++) accF[i] = 1.0;
        for (long lin = 0; lin < d.Length; lin++) { accF[OutIndex()] *= d[lin]; Step(); }
        var o = new float[outN]; for (long i = 0; i < outN; i++) o[i] = (float)accF[i];
        return Tensor.F(o, oshA);
    }

    // tflite UNPACK — split `num`=Shape[axis] slices along `axis`, each with `axis` removed (the inverse of PACK).
    // Multi-output: returns one squeezed tensor per slice; the Run loop maps outs[i] -> node.Output[i].
    static Tensor[] UnpackOp(Tensor[] x, NodeProto n)
    {
        var a = x[0]; int r = a.Shape.Length;
        int axis = (int)L(n, "axis", 0); if (axis < 0) axis += r;
        int num = a.Shape[axis];
        var osh = new List<int>(); for (int k = 0; k < r; k++) if (k != axis) osh.Add(a.Shape[k]);
        int[] oshA = osh.Count == 0 ? new[] { 1 } : osh.ToArray();
        var oStr = ContigStrides(oshA);
        long sliceN = 1; foreach (var dd in oshA) sliceN *= dd;
        bool isInt = a.IsInt;
        var di = isInt ? null : a.AsF(); var ip = isInt ? a.Ip : null;
        var bufF = isInt ? null : new float[num][]; var bufI = isInt ? new long[num][] : null;
        for (int s = 0; s < num; s++) { if (isInt) bufI[s] = new long[sliceN]; else bufF[s] = new float[sliceN]; }
        var idx = new int[r];
        long len = isInt ? ip.LongLength : di.LongLength;
        for (long lin = 0; lin < len; lin++)
        {
            int s = idx[axis];
            long oi = 0; int od = 0;
            for (int k = 0; k < r; k++) { if (k == axis) continue; oi += (long)idx[k] * oStr[od]; od++; }
            if (isInt) bufI[s][oi] = ip[lin]; else bufF[s][oi] = di[lin];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        var res = new Tensor[num];
        for (int s = 0; s < num; s++) res[s] = isInt ? Tensor.I(bufI[s], oshA) : Tensor.F(bufF[s], oshA);
        return res;
    }

    // StableHLO DYNAMIC_UPDATE_SLICE — (operand, update, start_0..start_{r-1}) -> operand with `update`
    // written at the clamped start position. The KV-cache write (a new token's K/V overlaid into the cache).
    static Tensor DynamicUpdateSliceOp(Tensor[] x, NodeProto n)
    {
        var operand = x[0]; var update = x[1]; int r = operand.Shape.Length;
        var starts = new int[r];
        for (int k = 0; k < r; k++)
        {
            int si = (x.Length > 2 + k && x[2 + k] != null) ? (int)x[2 + k].AsI()[0] : 0;
            starts[k] = Math.Max(0, Math.Min(si, operand.Shape[k] - update.Shape[k]));
        }
        var oStr = ContigStrides(operand.Shape);
        var idx = new int[r];
        if (operand.IsInt)
        {
            var o = (long[])operand.Ip.Clone(); var u = update.Ip;
            for (long lin = 0; lin < u.LongLength; lin++)
            {
                long oi = 0; for (int k = 0; k < r; k++) oi += (long)(starts[k] + idx[k]) * oStr[k];
                o[oi] = u[lin];
                for (int k = r - 1; k >= 0; k--) { if (++idx[k] < update.Shape[k]) break; idx[k] = 0; }
            }
            return Tensor.I(o, operand.Shape);
        }
        else
        {
            var o = (float[])operand.AsF().Clone(); var u = update.AsF();
            for (long lin = 0; lin < u.LongLength; lin++)
            {
                long oi = 0; for (int k = 0; k < r; k++) oi += (long)(starts[k] + idx[k]) * oStr[k];
                o[oi] = u[lin];
                for (int k = r - 1; k >= 0; k--) { if (++idx[k] < update.Shape[k]) break; idx[k] = 0; }
            }
            return Tensor.F(o, operand.Shape);
        }
    }

    // tflite PACK — stack `num` inputs (same shape) along a NEW axis `axis` (the inverse of UNPACK).
    static Tensor PackOp(Tensor[] x, NodeProto n)
    {
        int axis = (int)L(n, "axis", 0);
        var first = x[0]; int r = first.Shape.Length; if (axis < 0) axis += r + 1;
        int num = x.Length;
        var osh = new List<int>(first.Shape); osh.Insert(axis, num);
        int[] oshA = osh.ToArray(); long outN = 1; foreach (var dd in oshA) outN *= dd;
        var oStr = ContigStrides(oshA);
        bool isInt = first.IsInt;
        var of = isInt ? null : new float[outN]; var oiArr = isInt ? new long[outN] : null;
        for (int s = 0; s < num; s++)
        {
            var t = x[s];
            var td = isInt ? null : t.AsF(); var ti = isInt ? t.Ip : null;
            var idx = new int[r];
            long len = isInt ? ti.LongLength : td.LongLength;
            for (long lin = 0; lin < len; lin++)
            {
                long pos = 0; int id = 0;
                for (int k = 0; k <= r; k++) { int c = (k == axis) ? s : idx[id++]; pos += (long)c * oStr[k]; }
                if (isInt) oiArr[pos] = ti[lin]; else of[pos] = td[lin];
                for (int k = r - 1; k >= 0; k--) { if (++idx[k] < t.Shape[k]) break; idx[k] = 0; }
            }
        }
        return isInt ? Tensor.I(oiArr, oshA) : Tensor.F(of, oshA);
    }

    // tflite FILL — (dims, value) -> a tensor of shape `dims` filled with the scalar `value`.
    static Tensor FillOp(Tensor[] x, NodeProto n)
    {
        int[] shape = Array.ConvertAll(x[0].AsI(), v => (int)v);
        long count = 1; foreach (var d in shape) count *= d;
        int[] osh = shape.Length == 0 ? new[] { 1 } : shape;
        var val = x[1];
        if (val.IsInt)
        {
            long v = val.Ip[0]; var o = new long[count]; for (long i = 0; i < count; i++) o[i] = v;
            return Tensor.I(o, osh);
        }
        else
        {
            float v = val.AsF()[0]; var o = new float[count]; for (long i = 0; i < count; i++) o[i] = v;
            return Tensor.F(o, osh);
        }
    }

    // ONNX Gelu (opset 20) — none=exact erf, tanh=approx. [drafted by Antigravity #3, reviewed]
    static Tensor Gelu(Tensor a, NodeProto n)
    {
        if (Str(n, "approximate", "none") == "tanh") { float k = MathF.Sqrt(2f / MathF.PI); return Un(a, v => 0.5f * v * (1f + MathF.Tanh(k * (v + 0.044715f * v * v * v)))); }
        float inv = 1f / MathF.Sqrt(2f); return Un(a, v => 0.5f * v * (1f + Erf(v * inv)));
    }

    // ONNX Resize. inputs: X, roi, scales, sizes (roi/scales/sizes optional, may be null/empty).
    // N-D nearest + (multi)linear; coordinate transforms half_pixel/pytorch_half_pixel/align_corners/asymmetric.
    static Tensor Resize(Tensor[] x, NodeProto n)
    {
        var X = x[0]; int r = X.Shape.Length;
        string mode = Str(n, "mode", "nearest");
        string ct = Str(n, "coordinate_transformation_mode", "half_pixel");
        string nm = Str(n, "nearest_mode", "round_prefer_floor");
        Tensor scalesT = x.Length > 2 ? x[2] : null;
        Tensor sizesT = x.Length > 3 ? x[3] : null;
        int[] axes = Ints(n, "axes");
        var outDim = (int[])X.Shape.Clone();
        var scale = new double[r]; for (int d = 0; d < r; d++) scale[d] = 1.0;

        if (sizesT != null && sizesT.Count > 0)
        {
            var sz = sizesT.AsI();
            if (axes.Length > 0) for (int i = 0; i < axes.Length; i++) { int d = axes[i] < 0 ? axes[i] + r : axes[i]; outDim[d] = (int)sz[i]; scale[d] = (double)outDim[d] / X.Shape[d]; }
            else for (int d = 0; d < r; d++) { outDim[d] = (int)sz[d]; scale[d] = (double)outDim[d] / X.Shape[d]; }
        }
        else if (scalesT != null && scalesT.Count > 0)
        {
            var sc = scalesT.AsF();
            if (axes.Length > 0) for (int i = 0; i < axes.Length; i++) { int d = axes[i] < 0 ? axes[i] + r : axes[i]; scale[d] = sc[i]; outDim[d] = (int)Math.Floor(X.Shape[d] * sc[i]); }
            else for (int d = 0; d < r; d++) { scale[d] = sc[d]; outDim[d] = (int)Math.Floor(X.Shape[d] * sc[d]); }
        }

        double SrcCoord(int d, int oc)
        {
            int li = X.Shape[d], lo = outDim[d]; double s = scale[d];
            return ct switch
            {
                "asymmetric" => oc / s,
                "align_corners" => lo == 1 ? 0 : oc * (li - 1.0) / (lo - 1),
                "pytorch_half_pixel" => lo > 1 ? (oc + 0.5) / s - 0.5 : 0,
                _ => (oc + 0.5) / s - 0.5,   // half_pixel
            };
        }

        var xf = X.AsF(); var inStr = ContigStrides(X.Shape);
        long outN = 1; foreach (var d in outDim) outN *= d; var o = new float[outN];
        var idx = new int[r];

        if (mode == "nearest")
        {
            var map = new int[r][];
            for (int d = 0; d < r; d++) { map[d] = new int[outDim[d]]; for (int oc = 0; oc < outDim[d]; oc++) map[d][oc] = Math.Clamp(NearestIdx(SrcCoord(d, oc), nm), 0, X.Shape[d] - 1); }
            for (long lin = 0; lin < outN; lin++)
            {
                long si = 0; for (int d = 0; d < r; d++) si += (long)map[d][idx[d]] * inStr[d];
                o[lin] = xf[si];
                for (int d = r - 1; d >= 0; d--) { if (++idx[d] < outDim[d]) break; idx[d] = 0; }
            }
        }
        else // linear / cubic-fallback-to-linear: multilinear over 2^r corners
        {
            var lo0 = new int[r][]; var hi0 = new int[r][]; var w0 = new double[r][];
            for (int d = 0; d < r; d++)
            {
                lo0[d] = new int[outDim[d]]; hi0[d] = new int[outDim[d]]; w0[d] = new double[outDim[d]];
                for (int oc = 0; oc < outDim[d]; oc++)
                { double xi = Math.Clamp(SrcCoord(d, oc), 0, X.Shape[d] - 1); int lo = (int)Math.Floor(xi); lo0[d][oc] = lo; hi0[d][oc] = Math.Min(lo + 1, X.Shape[d] - 1); w0[d][oc] = xi - lo; }
            }
            for (long lin = 0; lin < outN; lin++)
            {
                double acc = 0;
                for (int corner = 0; corner < (1 << r); corner++)
                {
                    double wgt = 1; long si = 0; bool skip = false;
                    for (int d = 0; d < r; d++)
                    {
                        double w = w0[d][idx[d]];
                        if ((corner & (1 << d)) != 0) { wgt *= w; si += (long)hi0[d][idx[d]] * inStr[d]; }
                        else { wgt *= 1 - w; si += (long)lo0[d][idx[d]] * inStr[d]; }
                        if (wgt == 0) { skip = true; break; }
                    }
                    if (!skip) acc += wgt * xf[si];
                }
                o[lin] = (float)acc;
                for (int d = r - 1; d >= 0; d--) { if (++idx[d] < outDim[d]) break; idx[d] = 0; }
            }
        }
        return Tensor.F(o, outDim);
    }

    static int NearestIdx(double xi, string nm)
    {
        double fr = xi - Math.Floor(xi);
        return nm switch
        {
            "floor" => (int)Math.Floor(xi),
            "ceil" => (int)Math.Ceiling(xi),
            "round_prefer_ceil" => fr >= 0.5 ? (int)Math.Ceiling(xi) : (int)Math.Floor(xi),
            _ => fr > 0.5 ? (int)Math.Ceiling(xi) : (int)Math.Floor(xi),   // round_prefer_floor (tie -> floor)
        };
    }

    // ONNX STFT (opset 17) as an exact windowed-DFT — sidesteps the surgeon's Conv-basis decomposition.
    // signal[batch, signal_length, 1] (real); frame_step scalar; window[frame_length]; frame_length scalar.
    // out[batch, num_frames, bins, 2] (real,imag); onesided -> bins = frame_length/2+1.
    static Tensor Stft(Tensor[] x, NodeProto n)
    {
        var sig = x[0];
        int frameStep = (int)x[1].AsI()[0];
        Tensor window = (x.Length > 2 && x[2] != null) ? x[2] : null;
        int frameLength = (x.Length > 3 && x[3] != null) ? (int)x[3].AsI()[0]
                        : window != null ? (int)window.Count
                        : throw new NotImplementedException("STFT: needs frame_length or window");
        bool onesided = L(n, "onesided", 1) != 0;

        int batch = sig.Shape[0];
        int sigLen = sig.Shape.Length >= 2 ? sig.Shape[1] : sig.Shape[0];
        var s = sig.AsF();
        var win = window?.AsF();
        int numFrames = (sigLen - frameLength) / frameStep + 1;
        int bins = onesided ? frameLength / 2 + 1 : frameLength;

        // precompute the cos/sin DFT basis (bins x frameLength)
        var cs = new double[bins * frameLength]; var sn = new double[bins * frameLength];
        for (int k = 0; k < bins; k++)
            for (int m = 0; m < frameLength; m++)
            { double ang = -2.0 * Math.PI * k * m / frameLength; cs[k * frameLength + m] = Math.Cos(ang); sn[k * frameLength + m] = Math.Sin(ang); }

        var o = new float[(long)batch * numFrames * bins * 2];
        System.Threading.Tasks.Parallel.For(0, batch * numFrames, bfi =>
        {
            int b = bfi / numFrames, f = bfi % numFrames;
            int start = f * frameStep; long sBase = (long)b * sigLen + start;
            for (int k = 0; k < bins; k++)
            {
                double re = 0, im = 0; int kb = k * frameLength;
                for (int m = 0; m < frameLength; m++)
                { double xv = s[sBase + m]; if (win != null) xv *= win[m]; re += xv * cs[kb + m]; im += xv * sn[kb + m]; }
                long ob = (((long)b * numFrames + f) * bins + k) * 2;
                o[ob] = (float)re; o[ob + 1] = (float)im;
            }
        });
        return Tensor.F(o, new[] { batch, numFrames, bins, 2 });
    }

    // ONNX NonZero. out[rank, K] = the row-major coordinates of the K nonzero elements.
    static Tensor NonZero(Tensor a)
    {
        int r = a.Shape.Length; long total = a.Count; bool isInt = a.IsInt;
        var coords = new List<int[]>(); var idx = new int[r];
        for (long lin = 0; lin < total; lin++)
        {
            if (isInt ? a.Ip[lin] != 0 : a.Fp[lin] != 0) coords.Add((int[])idx.Clone());
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        int K = coords.Count; var o = new long[(long)r * K];
        for (int d = 0; d < r; d++) for (int j = 0; j < K; j++) o[(long)d * K + j] = coords[j][d];
        return Tensor.I(o, new[] { r, K });
    }

    // ONNX ScatterND. output = data with updates scattered at indices (last index dim = q <= rank);
    // each scattered slice has size prod(data.shape[q:]). reduction none|add|mul|max|min.
    static Tensor ScatterND(Tensor[] x, NodeProto n)
    {
        var data = x[0]; var indices = x[1]; var updates = x[2];
        string red = Str(n, "reduction", "none");
        int r = data.Shape.Length, q = indices.Shape[indices.Shape.Length - 1];
        long numUpdates = 1; for (int k = 0; k < indices.Shape.Length - 1; k++) numUpdates *= indices.Shape[k];
        long sliceSize = 1; for (int k = q; k < r; k++) sliceSize *= data.Shape[k];
        var dataStr = ContigStrides(data.Shape); var ix = indices.AsI();
        bool isInt = data.IsInt;
        var of = isInt ? null : (float[])data.AsF().Clone(); var oi = isInt ? (long[])data.AsI().Clone() : null;
        var uf = isInt ? null : updates.AsF(); var ui = isInt ? updates.AsI() : null;
        for (long u = 0; u < numUpdates; u++)
        {
            long baseOff = 0;
            for (int c = 0; c < q; c++) { long ic = ix[u * q + c]; if (ic < 0) ic += data.Shape[c]; baseOff += ic * dataStr[c]; }
            long uBase = u * sliceSize;
            for (long sIdx = 0; sIdx < sliceSize; sIdx++)
            {
                long dpos = baseOff + sIdx;
                if (isInt) { long v = ui[uBase + sIdx]; oi[dpos] = red == "add" ? oi[dpos] + v : red == "mul" ? oi[dpos] * v : red == "max" ? Math.Max(oi[dpos], v) : red == "min" ? Math.Min(oi[dpos], v) : v; }
                else { float v = uf[uBase + sIdx]; of[dpos] = red == "add" ? of[dpos] + v : red == "mul" ? of[dpos] * v : red == "max" ? MathF.Max(of[dpos], v) : red == "min" ? MathF.Min(of[dpos], v) : v; }
            }
        }
        return isInt ? Tensor.I(oi, data.Shape) : Tensor.F(of, data.Shape);
    }

    static string Str(NodeProto n, string name, string def) { foreach (var a in n.Attribute) if (a.Name == name) return a.S.ToStringUtf8(); return def; }

    static long[] ContigStrides(int[] shape)
    { var st = new long[shape.Length]; long acc = 1; for (int k = shape.Length - 1; k >= 0; k--) { st[k] = acc; acc *= shape[k]; } return st; }

    static float Erf(float x)
    { float t = 1f / (1f + 0.3275911f * MathF.Abs(x));
      float y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
      return MathF.Sign(x) * y; }

    // ---- proto helpers ----
    static long L(NodeProto n, string name, long def) { foreach (var a in n.Attribute) if (a.Name == name) return a.I; return def; }
    static float F(NodeProto n, string name, float def) { foreach (var a in n.Attribute) if (a.Name == name) return a.F; return def; }
    static int[] Ints(NodeProto n, string name) { foreach (var a in n.Attribute) if (a.Name == name) return a.Ints.Select(v => (int)v).ToArray(); return Array.Empty<int>(); }

    public static Tensor FromProto(TensorProto t)
    {
        var dims = t.Dims.Select(d => (int)d).ToArray();
        long n = 1; foreach (var d in dims) n *= d;
        switch (t.DataType)
        {
            case 1: { float[] f = t.FloatData.Count > 0 ? t.FloatData.ToArray() : Cast<float>(t.RawData.Span, (int)n); return Tensor.F(f, dims); }
            case 11: { var raw = t.RawData.Span; var dd = Cast<double>(raw, (int)n); return Tensor.F(Array.ConvertAll(dd, x => (float)x), dims); }
            case 7: { long[] l = t.Int64Data.Count > 0 ? t.Int64Data.ToArray() : Cast<long>(t.RawData.Span, (int)n); return Tensor.I(l, dims); }
            case 6: { int[] i = t.Int32Data.Count > 0 ? t.Int32Data.ToArray() : Cast<int>(t.RawData.Span, (int)n); return Tensor.I(Array.ConvertAll(i, x => (long)x), dims); }
            case 9: { var raw = t.RawData.Span; var l = new long[n]; for (int k = 0; k < n; k++) l[k] = raw[k]; return Tensor.I(l, dims); }
            // Gemma-4 E2B gap #1 (CRQ135): half-precision initializer load. bf16 = top 16 bits of fp32;
            // fp16 via Half. Unblocks the bf16 weights (768 MB tied embed + per-layer matrices). The
            // 4.48 GB PLE still exceeds the float[] / 2 GB cap here — that's gap #2 (lazy region gather).
            case 16: { var raw = t.RawData.Span; var f = new float[n]; for (int k = 0; k < n; k++) f[k] = BitConverter.Int32BitsToSingle((int)((uint)(ushort)(raw[2 * k] | (raw[2 * k + 1] << 8)) << 16)); return Tensor.F(f, dims); } // BFLOAT16
            case 10: { var raw = t.RawData.Span; var f = new float[n]; for (int k = 0; k < n; k++) f[k] = (float)BitConverter.UInt16BitsToHalf((ushort)(raw[2 * k] | (raw[2 * k + 1] << 8))); return Tensor.F(f, dims); } // FLOAT16
            default: throw new NotImplementedException($"initializer dtype {t.DataType} ({t.Name})");
        }
    }
    static T[] Cast<T>(ReadOnlySpan<byte> b, int n) where T : struct
    { var o = new T[n]; MemoryMarshal.Cast<byte, T>(b).Slice(0, n).CopyTo(o); return o; }
}

// the GPU MOUNT seam: dp-onnx (C#) -> PURE-C# D3D12 (GpuD3D12.cs) or Vulkan (GpuVulkan.cs). NO dpgpu.dll/C++/MSVC.
// DPGPU_BACKEND=vulkan flips to the cross-platform spine (vulkan-1.dll + gemm.spv) -> Android (Adreno/Mali);
// default is D3D12 (reuses the caller's gemm DXIL). Both reproduce the CPU GEMM bit-for-bit.
static class Gpu
{
    static readonly bool s_vk = string.Equals(Environment.GetEnvironmentVariable("DPGPU_BACKEND"), "vulkan", StringComparison.OrdinalIgnoreCase);
    static byte[] s_spv;
    public static int dpgpu_gemm(float[] A, float[] B, float[] C, uint M, uint N, uint K, byte[] dxil, uint dxilLen)
    {
        if (s_vk)
        {
            s_spv ??= System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "gemm.spv"));
            return GpuVulkan.Gemm(A, B, C, M, N, K, s_spv);
        }
        return GpuD3D12.Gemm(A, B, C, M, N, K, dxil, (int)dxilLen);
    }
    public static string DeviceName() { if (s_vk) { GpuVulkan.EnsureInit(); return GpuVulkan.Name; } GpuD3D12.EnsureInit(); return GpuD3D12.Name; }
}
