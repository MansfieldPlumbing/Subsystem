#nullable disable
// Dpx.cs - the dpx engine (Tensor, Dp kernels, Gpu backend seam), split out of Program.cs
// so it compiles as library code in-proc (no CLI Main). The CLI entry + mode handlers stay in Program.cs.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Subsystem.Vom;
using VomClass = Subsystem.Vom.Vom;

namespace Subsystem.Dpx;

// The activation-arena seam. Alloc is PRIVATE: the raw pointer never crosses this class's boundary
// (SS026) - kernels get a Span, Tensor carries the pointer internally and reclaims through Free() at
// refcount zero. The slab port (one VOM region per run under an arena NODE, tensors as offsets, a
// per-token cursor Reset) is the next rung - its state lives on the node, never in statics (SS015).
public static unsafe class TensorArena
{
    private static bool _active;
    private static long _peakOffset;
    private static long _currentAlloc;
    private static readonly ConcurrentDictionary<IntPtr, int> _refCounts = new();

    public static bool Active
    {
        get => _active;
        set => _active = value;
    }

    public static long PeakOffset => _peakOffset;

    public static void Initialize(long capacityBytes)
    {
    }

    public static void Reset()
    {
    }

    public static void Release()
    {
    }

    public static void TrackAlloc(long count)
    {
        long bytes = count * sizeof(float);
        long padded = (bytes + 255) & ~255;
        _currentAlloc += padded;
        if (_currentAlloc > _peakOffset) _peakOffset = _currentAlloc;
    }

    public static void TrackFree(long count)
    {
        long bytes = count * sizeof(float);
        long padded = (bytes + 255) & ~255;
        _currentAlloc -= padded;
    }

    private static float* Alloc(long count)
    {
        long bytes = count * sizeof(float);
        long padded = (bytes + 255) & ~255;
        float* ptr = (float*)NativeMemory.AlignedAlloc((nuint)padded, 256);
        TrackAlloc(count);
        return ptr;
    }

    public static Span<float> AllocSpan(long count)
    {
        if (_active)
        {
            return new Span<float>(Alloc(count), (int)count);
        }
        return new float[count];
    }

    // Free-at-zero for a pointer this class (or Tensor.AllocNative) handed out: DecRef, reclaim on zero.
    // Returns true when the memory was actually freed.
    public static bool Free(float* ptr, long count)
    {
        if (ptr == null || !DecRef(ptr)) return false;
        TrackFree(count);
        NativeMemory.AlignedFree(ptr);
        return true;
    }

    public static void AddRef(float* ptr)
    {
        if (ptr == null) return;
        IntPtr ip = (IntPtr)ptr;
        lock (_refCounts)
        {
            _refCounts[ip] = _refCounts.GetValueOrDefault(ip) + 1;
        }
    }

    public static bool DecRef(float* ptr)
    {
        if (ptr == null) return false;
        IntPtr ip = (IntPtr)ptr;
        lock (_refCounts)
        {
            if (_refCounts.TryGetValue(ip, out int count))
            {
                if (count <= 1)
                {
                    _refCounts.TryRemove(ip, out _);
                    return true;
                }
                else
                {
                    _refCounts[ip] = count - 1;
                    return false;
                }
            }
        }
        // untracked pointer: every freeable pointer was AddRef'd by the NativePtr setter, so an unknown
        // (or post-Release stale) pointer must NOT be freed here - refusing is the safe side of the race.
        return false;
    }
}

public class Tensor
{
    public int[] Shape;
    // >0: this tensor windows a persistent KV ring region laid out [B,Nkv,KvCap,H] (CRQ190) - Shape[2]
    // rows are valid, the physical row stride per (batch,head) is KvCap. Ring-aware kernels (GQA, the
    // seq-axis Concat) append in place at row Shape[2] instead of re-copying the whole cache.
    public int KvCap;
    public float[] Fp;     // float payload (null if int)
    private unsafe float* _nativePtr;
    public unsafe float* NativePtr
    {
        get => _nativePtr;
        set
        {
            if (_nativePtr != value)
            {
                _nativePtr = value;
                if (_nativePtr != null) TensorArena.AddRef(_nativePtr);
            }
        }
    }
    public long[] Ip;      // int64 payload (null if float)
    // --- packed quantized payload: weights kept in their native 2/4/8-bit form; dequant is DEFERRED to the kernel
    // (QGemm / quant Gather). Nothing ever materializes the full fp32 weight on the hot path -> resident stays packed.
    public byte[] Qb;      // packed sub-byte/byte weights; little-endian, low bits first, signed
    public int Qbits;      // 2 / 4 / 8 (derived from byte-count vs element-count at load)
    public float[] Qscale; // per-row scale along Qaxis (length = Shape[Qaxis])
    public float[] Qzero;  // per-row zero-point (same length); null => symmetric (0)
    public int Qaxis;      // quantized dimension (axis 0 for gemma's per-output-channel weights)
    public byte[] Rawb;    // raw integer-weight bytes (UINT8/INT8), un-widened — the block-q4 contrib ops
                           // (MatMulNBits / GatherBlockQuantized) read the packed nibbles straight from here.
                           // null when NativeRawb is set (VOM-region-backed weight load, CRQ164).
    private unsafe byte* _nativeRawb;
    private int _nativeRawbLen;
    public unsafe void SetNativeRawb(byte* ptr, int len) { _nativeRawb = ptr; _nativeRawbLen = len; }
    // The AHardwareBuffer handle backing this weight's bytes (Android zero-copy residency), or 0 when the
    // bytes are a plain VOM region / GC array. Instance state (rides the weight Tensor whose owner refcount
    // gates the region — SS015-clean, no static cache). GpuVulkan.GemmQ4 imports this buffer once per weightKey.
    private nint _ahbWeight;
    public nint AhbWeight => _ahbWeight;
    public void SetAhbWeight(nint h) { _ahbWeight = h; }
    // The VkBuffer/VkDeviceMemory GpuVulkan imported over AhbWeight (one import per weight, first GPU
    // sight). Rides the tensor like the AHB handle itself — weights live for the model's lifetime, so the
    // imported pair is freed with the device, not per-call. 0 = not (yet) imported.
    private nint _vkWeightBuf, _vkWeightMem;
    private ulong _vkWeightBytes;
    public nint VkWeightBuf => _vkWeightBuf;
    public ulong VkWeightBytes => _vkWeightBytes;
    public void SetVkWeight(nint buf, nint mem, ulong bytes) { _vkWeightBuf = buf; _vkWeightMem = mem; _vkWeightBytes = bytes; }
    // ReadRawb: the packed-byte accessor kernels pin via `fixed` (the SAME statement transparently pins a
    // managed array OR is a no-op over already-stable native memory — no kernel code branches on which).
    public unsafe Span<byte> ReadRawb() => _nativeRawb != null ? new Span<byte>(_nativeRawb, _nativeRawbLen) : Rawb;
    public bool IsQuant => Qb != null;
    public bool IsInt => Ip != null;
    public long Count { get { long n = 1; foreach (var d in Shape) n *= d; return n; } }
    public static Tensor F(float[] d, params int[] s) => new() { Fp = d, Shape = s };
    public static unsafe Tensor F(float* ptr, params int[] s) => new() { NativePtr = ptr, Shape = s };
    public static unsafe Tensor F(Span<float> span, params int[] s)
    {
        if (TensorArena.Active)
        {
            fixed (float* p = span)
            {
                return new Tensor { NativePtr = p, Shape = s };
            }
        }
        return new Tensor { Fp = span.ToArray(), Shape = s };
    }
    public static Tensor I(long[] d, params int[] s) => new() { Ip = d, Shape = s };
    // AsF on a quant tensor materializes the full fp32 — the FALLBACK for any op that isn't quant-aware. The hot
    // consumers (Gemm, Gather) read Qb directly and never hit this; if some other op touches a weight it gets a
    // transient fp32 (freed by the caller), so correctness is never wrong, only that one op pays.
    public unsafe Span<float> AsF() => IsQuant ? Dequant() : (NativePtr != null ? new Span<float>(NativePtr, (int)Count) : (Fp ?? Array.ConvertAll(Ip, x => (float)x)));
    public unsafe long[] AsI()
    {
        if (Ip != null) return Ip;
        var f = AsF();
        var res = new long[f.Length];
        for (int i = 0; i < f.Length; i++) res[i] = (long)f[i];
        return res;
    }

    public static unsafe Tensor AllocNative(params int[] shape)
    {
        long count = 1;
        foreach (var d in shape) count *= d;
        long bytes = count * sizeof(float);
        long padded = (bytes + 255) & ~255;
        void* ptr = NativeMemory.AlignedAlloc((nuint)padded, 256);
        new Span<float>(ptr, (int)count).Clear();
        TensorArena.TrackAlloc(count);
        return Tensor.F((float*)ptr, shape);
    }

    public unsafe void FreeNative()
    {
        if (_nativePtr != null)
        {
            TensorArena.Free(_nativePtr, Count);   // free-at-zero: VOM region -> Vom.Close, legacy buffer -> AlignedFree
            _nativePtr = null;
        }
    }

    public unsafe Tensor Clone()
    {
        var t = new Tensor
        {
            Shape = Shape,
            KvCap = KvCap,
            Fp = Fp,
            Ip = Ip,
            Qb = Qb,
            Qbits = Qbits,
            Qscale = Qscale,
            Qzero = Qzero,
            Qaxis = Qaxis
        };
        t.NativePtr = _nativePtr; // calls AddRef!
        return t;
    }

    // Dequant a single weight element by flat (row-major) index: (q - zero[row]) * scale[row].
    public float Deq(long elem)
    {
        int per = 8 / Qbits, mask = (1 << Qbits) - 1, half = 1 << (Qbits - 1);
        int q = (Qb[(int)(elem / per)] >> (int)((elem % per) * Qbits)) & mask;
        if (q >= half) q -= (1 << Qbits);                       // signed sub-element
        long row = QRow(elem);
        return (q - (Qzero != null ? Qzero[row] : 0f)) * Qscale[row];
    }
    // Which Qaxis-row a flat index falls in (inner = product of dims after Qaxis).
    public long QRow(long elem)
    {
        long inner = 1; for (int k = Qaxis + 1; k < Shape.Length; k++) inner *= Shape[k];
        return (elem / inner) % Shape[Qaxis];
    }
    public static readonly System.Collections.Generic.HashSet<string> QFbSeen = new();
    float[] Dequant()
    {
        long n = Count;
        if (n > 1_000_000) { var key = string.Join("x", Shape); lock (QFbSeen) if (QFbSeen.Add(key)) System.Console.Error.WriteLine($"\n[QFALLBACK] full dequant {key} = {n * 4 / 1e6:F0}MB fp32 — a quant weight hit a non-quant-aware op"); }
        var o = new float[n]; for (long i = 0; i < n; i++) o[i] = Deq(i); return o;
    }
}

public class Dpx
{
    readonly ModelProto _m;
    // Owner for weight storage (CRQ164): a caller that already has one (e.g. DpxDecoder's agent owner)
    // should pass it so weights cascade-terminate with the rest of that agent's handles; otherwise one
    // is created lazily on first use, scoped to this Dp instance's own lifetime.
    Owner _owner;
    public Dpx(ModelProto m, Owner owner = null) { _m = m; _owner = owner; }
    // Exposed so a caller that didn't supply an owner (weights got a lazily-created one, scoped to this
    // Dp instance) can Terminate it when this Dp is no longer needed - null if Run() was never called.
    public Owner WeightsOwner => _owner;

    public static readonly HashSet<string> Implemented = new()
    {
        "Add","Sub","Mul","Div","Pow","MatMul","Gemm",
        "Relu","LeakyRelu","Sigmoid","Tanh","Sqrt","Rsqrt","Exp","Neg","Abs","Floor","Sin","Cos","Erf","Clip","Softplus","Reciprocal",
        "Reshape","Transpose","Unsqueeze","Squeeze","Concat","Identity","Constant","Cast","Shape","Gather","Flatten",
        "Equal","NotEqual","Greater","Less","GreaterOrEqual","LessOrEqual","And","Or","Not","Min","Max","Mod","Round","Atan","Sign",
        "Where","Expand","ConstantOfShape","Fill","Range","ReduceMean","ReduceSum","ReduceMax","CumSum","Slice","Pad",
        "Conv","ConvTranspose","LayerNormalization","Softmax","LSTM","Resize","STFT","NonZero","ScatterND","DynamicUpdateSlice",
        "Split","Unpack","Pack","Tile","GroupNormalization","Gelu","InstanceNormalization","ReduceProd","DequantizeLinear","QuantizeLinear",
        "MatMulNBits","GatherBlockQuantized","RotaryEmbedding","SimplifiedLayerNormalization","GroupQueryAttention",
        "ReduceAll","OneHot",
    };

    public static bool Profile = false;
    public static System.Collections.Immutable.ImmutableDictionary<string, (double ms, long n)> Prof = System.Collections.Immutable.ImmutableDictionary<string, (double ms, long n)>.Empty;
    public static double DropP = 0;                       // --drop p: prob of skipping a residual merge (stale-read / drop-path model)
    public static string DropScope = "";                  // --drop-scope: restrict drops to nodes whose Name contains this (gate to the data plane)
    public static readonly Random DropRng = new(1234);    // seeded -> reproducible rmse(p) sweep
    public static long Dropped = 0;

    public bool Verbose { get; set; }
    [ThreadStatic]
    public static bool ActiveVerbose;

    Dictionary<string, Tensor> _winit;   // decoded initializers, cached: streaming Run()s many short feeds over ONE graph -> decode the 82M weights ONCE, not per chunk
    // Graph-invariant dispatch scaffolding, computed once per model beside _winit (CRQ190 R0 per-Run
    // hoist): rebuilding these per token was pure Run-loop overhead for a graph that never changes.
    Dictionary<string, int> _lastUse;    // input name -> index of its LAST consumer (liveness reclaim)
    HashSet<string> _pinned;             // graph output names - never reclaimed mid-run
    Tensor[][] _nodeIns;                 // one reusable input buffer per node (arity is graph-invariant)
    // Per-model MatMulNBits route table (CRQ190 R0): non-null only under DPGPU_BACKEND=auto; passed into
    // Dispatch so bare static callers (benches, DpxRace's oracle) never route.
    readonly DpxRoute _routes = AutoRouteMatMulNBits ? new DpxRoute() : null;
    public unsafe Dictionary<string, Tensor> Run(Dictionary<string, Tensor> feed, Action<NodeProto, Tensor[], Dictionary<string, Tensor>> onNode = null)
    {
        ActiveVerbose = Verbose;
        try
        {
            if (_winit == null)
            {
                // Decode initializers into the cached _winit (the ONLY copy kernels read across Run()s), freeing each
                // weight's source bytes AS it is decoded — so the ModelProto fp32 bytes and the _winit copy never
                // both pile up (keeps the fp32-path transient near steady, not 2x). Periodic reclaim of the freed LOH.
                _winit = new Dictionary<string, Tensor>();
                // Weight storage is VOM-native (CRQ164): the packed block-q4 bytes (Rawb) go into a real VOM
                // handle, not a GC array, so a ~1GB weight blob doesn't sit on the managed heap. Lazily own one
                // if the caller didn't supply theirs.
                _owner ??= VomClass.CreateOwner($"\\Agent\\Dpx\\Weights\\{Guid.NewGuid():N}");
                // Index initializers by name so a packed weight can find its sibling "<name>_scale"/"<name>_zp"
                // (EmitNativeQuant emits the three separately). A weight with a _scale sibling and a 2/4/8-bit
                // byte:element ratio stays PACKED (a quant Tensor); everything else widens via FromProto as before.
                var byName = new Dictionary<string, TensorProto>();
                foreach (var it in _m.Graph.Initializer) byName[it.Name] = it;
                int k = 0;
                foreach (var init in _m.Graph.Initializer)
                {
                    byName.TryGetValue(init.Name + "_scale", out var scP);
                    if (IsPackedQuant(init, scP))
                        _winit[init.Name] = MakeQuant(init, scP, byName.GetValueOrDefault(init.Name + "_zp"));
                    else
                        _winit[init.Name] = FromProto(init, _owner);
                    init.RawData = new ByteString(System.Array.Empty<byte>());   // source bytes consumed; free them now
                    if ((++k & 63) == 0) GC.Collect();
                }
                _m.Graph.Initializer.Clear();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                // Liveness reclaim scaffolding: free each intermediate once its LAST consumer has run. A
                // 4630-node decoder otherwise keeps every output forever (hundreds of 32MB [.,32003,.]
                // tensors -> ~10GB). Weights live in _winit (resolved through it below), so reclaim can't
                // free them; graph outputs are pinned. Graph-invariant -> computed here ONCE per model,
                // never per streaming Run() (per token).
                var gnodes = _m.Graph.Node;
                _lastUse = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < gnodes.Count; i++) foreach (var inp in gnodes[i].Input) if (!string.IsNullOrEmpty(inp)) _lastUse[inp] = i;
                _pinned = new HashSet<string>(_m.Graph.Output.Select(o => o.Name), StringComparer.Ordinal);
                _nodeIns = new Tensor[gnodes.Count][];
                for (int i = 0; i < gnodes.Count; i++) _nodeIns[i] = new Tensor[gnodes[i].Input.Count];
            }
            // env holds feed + per-run intermediates only; weights resolve through _winit (a per-token
            // dictionary copy of the whole weight map bought nothing - kernels never mutate weights).
            var env = new Dictionary<string, Tensor>(feed);
            var nodes = _m.Graph.Node;
            DpxMem.Sample();   // ambient RAM reading at entry (telemetry inlet for the host)
            for (int ni = 0; ni < nodes.Count; ni++)
            {
                var node = nodes[ni];
                var ins = _nodeIns[ni];   // reused across Runs; refs cleared at the end of this iteration
                for (int i = 0; i < ins.Length; i++)
                {
                    var nm = node.Input[i];
                    ins[i] = string.IsNullOrEmpty(nm) ? null : (env.TryGetValue(nm, out var tv) ? tv : _winit[nm]);
                }
                Tensor[] outs;
                try
                {
                    if (Profile)
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        outs = Dispatch(node, ins, _routes);
                        double dt = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        var e = Prof.GetValueOrDefault(node.OpType); Prof = Prof.SetItem(node.OpType, (e.ms + dt, e.n + 1));
                    }
                    else
                    {
                        outs = Dispatch(node, ins, _routes);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error executing node '{node.Name}' (op={node.OpType}): {ex.Message}", ex);
                }
                // stale-read model: with prob p, a residual merge's second-branch transfer is "skipped" -> the residual carries (drop-path).
                if (DropP > 0 && node.OpType == "Add" && (DropScope.Length == 0 || node.Name.Contains(DropScope)) && outs.Length > 0 && outs[0] != null && !outs[0].IsInt
                    && ins.Length > 0 && ins[0] != null && ins[0].Count == outs[0].Count && DropRng.NextDouble() < DropP)
                { outs[0] = ins[0]; Dropped++; }
                for (int i = 0; i < node.Output.Count && i < outs.Length; i++) if (!string.IsNullOrEmpty(node.Output[i])) env[node.Output[i]] = outs[i];
                onNode?.Invoke(node, outs, env);   // env passed so a hook can INJECT an oracle value for downstream
                foreach (var inp in node.Input)    // reclaim dead intermediates (last use is this node; never a pinned graph output)
                    if (!string.IsNullOrEmpty(inp) && _lastUse.TryGetValue(inp, out var lu) && lu == ni && !_pinned.Contains(inp))
                    {
                        if (env.Remove(inp, out var t))
                        {
                            if (!_winit.ContainsKey(inp) && !feed.ContainsKey(inp))
                            {
                                t?.FreeNative();
                            }
                        }
                    }
                for (int i = 0; i < ins.Length; i++) ins[i] = null;   // drop refs so reclaimed intermediates aren't held until the next token
            }
            var result = new Dictionary<string, Tensor>();
            foreach (var o in _m.Graph.Output) result[o.Name] = env.TryGetValue(o.Name, out var ot) ? ot : _winit[o.Name];
            foreach (var kvp in env)
            {
                if (!_winit.ContainsKey(kvp.Key) && !feed.ContainsKey(kvp.Key) && !_pinned.Contains(kvp.Key))
                {
                    kvp.Value?.FreeNative();
                }
            }
            return result;
        }
        finally
        {
            ActiveVerbose = false;
        }
    }

    // route: the per-model MatMulNBits route table (CRQ190 R0) - null (every bare caller) routes nothing
    // and preserves the standing knob behavior exactly. Only Run passes its per-model table through.
    public static Tensor[] Dispatch(NodeProto n, Tensor[] x, DpxRoute route = null)
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
            case "MatMul":
                if (x[1].IsQuant) return One(QGemm(x[0], x[1], transB: false, 1.0f, null, 0.0f));
                return One(UseGpuMatMul ? GpuMatMul(x[0], x[1]) : MatMul(x[0], x[1]));
            case "Gemm": return One(Gemm(n, x));
            case "Identity": return One(x[0].Clone());
            case "Constant": return One(FromProto(n.Attribute.First(a => a.Name == "value").T));
            case "Cast": return One(x[0].Clone());   // payloads are already widened; layout-preserving
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
            case "ReduceAll": return One(Reduce(x, n, "all"));
            case "OneHot": return One(OneHot(x, n));
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
            // ---- Gemma-3n E2B q4 ONNX contrib ops (com.microsoft), grounded against the q4 .db export ----
            case "MatMulNBits": { var r = ResolveMatMulNBits(x, n, route); DpxExperiments.RecordLogits(n, r); return One(r); }
            case "GatherBlockQuantized": return One(GatherBlockQuantized(x, n));
            case "RotaryEmbedding": return One(RotaryEmbedding(x, n));
            case "SimplifiedLayerNormalization": return One(SimplifiedLayerNorm(x, n));
            case "GroupQueryAttention": return GroupQueryAttention(x, n);
            default: throw new NotImplementedException($"node #?: {n.OpType} (name={n.Name})");
        }
    }

    // ---- kernels ----
    static Tensor[] One(Tensor t) => new[] { t };

    static Tensor Un(Tensor a, Func<float, float> f)
    { var d = a.AsF(); var o = TensorArena.AllocSpan(d.Length); for (int i = 0; i < d.Length; i++) o[i] = f(d[i]); return Tensor.F(o, a.Shape); }

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
        if (x[0].IsQuant) return x[0].Clone();   // packed weight: scale/zp already attached at load -> defer to QGemm/Gather, no fp32 blow-up
        var q = x[0].AsF(); var scale = x[1].AsF();
        var zp = x.Length > 2 && x[2] != null ? x[2].AsF() : null;
        bool perAxis = scale.Length > 1; int axis = (int)L(n, "axis", 1);
        var o = TensorArena.AllocSpan(q.Length);
        for (long i = 0; i < q.Length; i++)
        {
            int c = perAxis ? AxisCoord(i, x[0].Shape, axis) : 0;
            o[(int)i] = (q[(int)i] - (!zp.IsEmpty ? zp[c] : 0f)) * scale[c];
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
            int c = perAxis ? AxisCoord((int)i, x[0].Shape, axis) : 0;
            long q = (long)MathF.Round(v[(int)i] / scale[c], MidpointRounding.ToEven) + (long)(!zp.IsEmpty ? zp[c] : 0f);
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
    static unsafe Tensor BcastV(Tensor a, Tensor b, BinOp op)
    {
        var fa = a.AsF(); var fb = b.AsF();
        if (a.Shape.AsSpan().SequenceEqual(b.Shape))
        {
            int n = fa.Length, vw = Vector<float>.Count, i = 0; var o = TensorArena.AllocSpan(n);
            for (; i + vw <= n; i += vw)
                (op switch { BinOp.Add => new Vector<float>(fa.Slice(i)) + new Vector<float>(fb.Slice(i)), BinOp.Sub => new Vector<float>(fa.Slice(i)) - new Vector<float>(fb.Slice(i)), BinOp.Mul => new Vector<float>(fa.Slice(i)) * new Vector<float>(fb.Slice(i)), _ => new Vector<float>(fa.Slice(i)) / new Vector<float>(fb.Slice(i)) }).CopyTo(o.Slice(i));
            for (; i < n; i++) o[i] = op switch { BinOp.Add => fa[i] + fb[i], BinOp.Sub => fa[i] - fb[i], BinOp.Mul => fa[i] * fb[i], _ => fa[i] / fb[i] };
            return Tensor.F(o, a.Shape);
        }
        int[] sh = BroadcastShape(a.Shape, b.Shape);
        long n2 = 1; foreach (var d in sh) n2 *= d; var oo = TensorArena.AllocSpan(n2);
        var (sa, sb) = (Strides(a.Shape, sh), Strides(b.Shape, sh));
        int rank = sh.Length, last = rank - 1, inner = rank > 0 ? sh[last] : 1; long outer = inner > 0 ? n2 / inner : 0;
        if (rank > 0 && sa[last] <= 1 && sb[last] <= 1 && inner >= Vector<float>.Count)   // innermost contiguous(1)/broadcast(0) in both -> SIMD the inner run, parallel over outer
        {
            bool sa0 = sa[last] == 0, sb0 = sb[last] == 0;
            fixed (float* p_oo = oo)
            fixed (float* p_fa = fa)
            fixed (float* p_fb = fb)
            {
                float* ptr_oo = p_oo; float* ptr_fa = p_fa; float* ptr_fb = p_fb;
                int ooLen = oo.Length; int faLen = fa.Length; int fbLen = fb.Length;
                System.Threading.Tasks.Parallel.For(0L, outer, o =>
                {
                    long rem = o, ia0 = 0, ib0 = 0;
                    for (int k = last - 1; k >= 0; k--) { int d = (int)(rem % sh[k]); rem /= sh[k]; ia0 += d * sa[k]; ib0 += d * sb[k]; }
                    var span_oo = new Span<float>(ptr_oo, ooLen);
                    var span_fa = new Span<float>(ptr_fa, faLen);
                    var span_fb = new Span<float>(ptr_fb, fbLen);
                    VecBinRun(span_oo.Slice(checked((int)(o * inner)), (int)inner), span_fa.Slice(checked((int)ia0), sa0 ? 1 : (int)inner), sa0, span_fb.Slice(checked((int)ib0), sb0 ? 1 : (int)inner), sb0, op);
                });
            }
            return Tensor.F(oo, sh);
        }
        var idx = new int[rank];
        for (long lin = 0; lin < n2; lin++)
        {
            long ia = 0, ib = 0;
            for (int k = 0; k < rank; k++) { ia += idx[k] * sa[k]; ib += idx[k] * sb[k]; }
            float x = fa[(int)ia], y = fb[(int)ib];
            oo[(int)lin] = op switch { BinOp.Add => x + y, BinOp.Sub => x - y, BinOp.Mul => x * y, _ => x / y };
            for (int k = rank - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
        }
        return Tensor.F(oo, sh);
    }

    static Tensor Bcast(Tensor a, Tensor b, Func<float, float, float> f)
    {
        var fa = a.AsF(); var fb = b.AsF();
        int[] sh = BroadcastShape(a.Shape, b.Shape);
        long n = 1; foreach (var d in sh) n *= d;
        var o = TensorArena.AllocSpan(n);
        var (sa, sb) = (Strides(a.Shape, sh), Strides(b.Shape, sh));
        var idx = new int[sh.Length];
        for (long lin = 0; lin < n; lin++)
        {
            long ia = 0, ib = 0;
            for (int k = 0; k < sh.Length; k++) { ia += idx[k] * sa[k]; ib += idx[k] * sb[k]; }
            o[(int)lin] = f(fa[(int)ia], fb[(int)ib]);
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
    public static string ActiveModelDbPath;
    static byte[] _gemmDxil; static bool _gpuDead = false;
    static byte[] ReadDxilResource(string name)
    {
        string near = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(near)) return File.ReadAllBytes(near);
        if (Environment.ProcessPath != null)
        {
            string procDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            string procPath = Path.Combine(procDir, name);
            if (File.Exists(procPath)) return File.ReadAllBytes(procPath);
        }
        string driveRoot = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "S:\\";
        string rootPath = Path.Combine(driveRoot, "subsystem", name);
        if (File.Exists(rootPath)) return File.ReadAllBytes(rootPath);
        using var stream = typeof(Dpx).Assembly.GetManifestResourceStream(name);
        if (stream != null)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        return Array.Empty<byte>();
    }
    static byte[] GemmDxil()
    {
        if (_gemmDxil != null) return _gemmDxil;
        return _gemmDxil = ReadDxilResource("gemm.dxil");
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
        var o = TensorArena.AllocSpan(outBatch * M * N);
        var aSub = new float[(long)M * K]; var bSub = new float[(long)K * N]; var cSub = new float[(long)M * N];
        try
        {
            byte[] dxil = GemmDxil(); var bidx = new int[nb];
            int threadM = 16;
            int threadN = 16;
            if (!string.IsNullOrEmpty(ActiveModelDbPath))
            {
                string adapterName = Gpu.DeviceName();
                byte[] tuned = ModelDb.GetTunedShader(ActiveModelDbPath, adapterName, "Gemm", M, N, K, out var tm, out var tn);
                if (tuned != null)
                {
                    dxil = tuned;
                    threadM = tm;
                    threadN = tn;
                }
            }
            for (long bi = 0; bi < outBatch; bi++)
            {
                long aB = 0, bB = 0; for (int k = 0; k < nb; k++) { aB += bidx[k] * sA[k]; bB += bidx[k] * sB[k]; }
                a.Slice((int)(aB * M * K), M * K).CopyTo(aSub);
                b.Slice((int)(bB * K * N), K * N).CopyTo(bSub);
                int rc = Gpu.dpgpu_gemm(aSub, bSub, cSub, (uint)M, (uint)N, (uint)K, dxil, (uint)dxil.Length, threadM, threadN);
                if (rc != 0) throw new InvalidOperationException($"dpgpu_gemm rc={rc}");
                cSub.CopyTo(o.Slice((int)(bi * M * N), M * N));
                for (int k = nb - 1; k >= 0; k--) { if (++bidx[k] < lead[k]) break; bidx[k] = 0; }
            }
        }
        catch (Exception ex)
        {
            _gpuDead = true;
            Console.Error.WriteLine($"dpx: GPU GEMM unavailable ({ex.GetType().Name}: {ex.Message}); falling back to CPU.");
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

    static unsafe Tensor MatMul(Tensor A, Tensor B)
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
        var o = TensorArena.AllocSpan(outBatch * M * N);
        // precompute per-batch input offsets (outBatch is small), then parallelize over (batch,row) — k-order preserved -> bit-identical
        var aOff = new long[outBatch]; var bOff = new long[outBatch];
        { var bidx = new int[nb]; for (long bi = 0; bi < outBatch; bi++) { long aB = 0, bB = 0; for (int k = 0; k < nb; k++) { aB += bidx[k] * sA[k]; bB += bidx[k] * sB[k]; } aOff[bi] = aB * M * K; bOff[bi] = bB * K * N; for (int k = nb - 1; k >= 0; k--) { if (++bidx[k] < lead[k]) break; bidx[k] = 0; } } }
        fixed (float* p_o = o)
        fixed (float* p_a = a)
        fixed (float* p_b = b)
        {
            float* ptr_o = p_o; float* ptr_a = p_a; float* ptr_b = p_b;
            int oLen = o.Length; int aLen = a.Length; int bLen = b.Length;
            System.Threading.Tasks.Parallel.For(0L, outBatch * M, r =>
            {
                long bi = r / M; int i = (int)(r % M);
                long bo = bOff[bi], orow = (bi * M + i) * (long)N, aRow = aOff[bi] + (long)i * K;
                var span_o = new Span<float>(ptr_o, oLen);
                var span_a = new Span<float>(ptr_a, aLen);
                var span_b = new Span<float>(ptr_b, bLen);
                var dst = span_o.Slice(checked((int)orow), N); dst.Clear();
                for (int k = 0; k < K; k++)   // axpy: dst += a[i,k] * B_row_k (SIMD over N); k-order preserved
                {
                    float aik = span_a[checked((int)(aRow + k))];
                    if (aik != 0f) AxpyInto(dst, span_b.Slice(checked((int)(bo + (long)k * N)), N), aik);
                }
            });
        }
        var sh = new int[nb + 2];
        for (int k = 0; k < nb; k++) sh[k] = lead[k];
        sh[nb] = M; sh[nb + 1] = N;
        return Tensor.F(o, sh);
    }

    static Tensor Gemm(NodeProto n, Tensor[] x)
    {
        float alpha = F(n, "alpha", 1), beta = F(n, "beta", 1);
        bool ta = L(n, "transA", 0) != 0, tb = L(n, "transB", 0) != 0;
        if (x[1].IsQuant)   // packed weight: dequant fused INTO the dot -> the fp32 weight is never built
            return QGemm(ta ? Transpose(x[0], new[] { 1, 0 }) : x[0], x[1], tb, alpha, x.Length > 2 ? x[2] : null, beta);
        var A = ta ? Transpose(x[0], new[] { 1, 0 }) : x[0];
        var B = tb ? Transpose(x[1], new[] { 1, 0 }) : x[1];
        var m = MatMul(A, B); var md = m.AsF();
        for (int i = 0; i < md.Length; i++) md[i] *= alpha;
        if (x.Length > 2 && x[2] != null) { var cb = Bcast(m, x[2], (p, q) => p + beta * q); return cb; }
        return m;
    }

    // Fused dequant-Gemm: Y = alpha*(A @ dequant(W)) [+ beta*C]. A is fp32 [M,K]; W is a PACKED weight, never
    // expanded to fp32. Mario-hop accumulation: sum the raw (q · a) in fp32 across K, then lift to real units with
    // the per-row scale/zp at the ROW boundary — int-domain throughput, fp32-domain precision.
    // Fast path = the gemma shape (transB, per-output-row scale on Qaxis 0): Y[m,n] = scale[n]*(Σ_k a[m,k]·q[n,k] − zp[n]·Σ_k a[m,k]).
    static unsafe Tensor QGemm(Tensor A, Tensor W, bool transB, float alpha, Tensor C, float beta)
    {
        var a = A.AsF();
        int M = A.Shape[A.Shape.Length - 2], K = A.Shape[A.Shape.Length - 1];
        int N = transB ? W.Shape[0] : W.Shape[1];
        var qb = W.Qb; var sc = W.Qscale; var zp = W.Qzero;
        int bits = W.Qbits, per = 8 / bits, mask = (1 << bits) - 1, half = 1 << (bits - 1);
        var y = TensorArena.AllocSpan((long)M * N);
        if (transB && W.Qaxis == 0)
        {
            var asum = TensorArena.AllocSpan(M);                                  // Σ_k a[m,k] for the zero-point term (per row, once)
            for (int m = 0; m < M; m++) { float s = 0f; int o = m * K; for (int kk = 0; kk < K; kk++) s += a[o + kk]; asum[m] = s; }
            fixed (float* p_y = y)
            fixed (float* p_asum = asum)
            fixed (float* p_a = a)
            {
                float* ptr_y = p_y; float* ptr_asum = p_asum; float* ptr_a = p_a;
                int yLen = y.Length; int asumLen = asum.Length; int aLen = a.Length;
                System.Threading.Tasks.Parallel.For(0, N, nn =>
                {
                    long rb = (long)nn * K; float s = sc[nn], z = zp != null ? zp[nn] : 0f;
                    var span_y = new Span<float>(ptr_y, yLen);
                    var span_asum = new Span<float>(ptr_asum, asumLen);
                    var span_a = new Span<float>(ptr_a, aLen);
                    for (int m = 0; m < M; m++)
                    {
                        int ao = m * K; float acc = 0f;
                        for (int k = 0; k < K; k++)
                        {
                            long ei = rb + k;
                            int q = (qb[(int)(ei / per)] >> (int)((ei % per) * bits)) & mask;
                            if (q >= half) q -= (1 << bits);
                            acc += span_a[ao + k] * q;
                        }
                        span_y[(int)((long)m * N + nn)] = alpha * s * (acc - z * span_asum[m]);
                    }
                });
            }
        }
        else   // general fallback: per-element Deq (folds scale+zp inside) — correct for any transB/axis, just slower
        {
            fixed (float* p_y = y)
            fixed (float* p_a = a)
            {
                float* ptr_y = p_y; float* ptr_a = p_a;
                int yLen = y.Length; int aLen = a.Length;
                System.Threading.Tasks.Parallel.For(0, N, nn =>
                {
                    var span_y = new Span<float>(ptr_y, yLen);
                    var span_a = new Span<float>(ptr_a, aLen);
                    for (int m = 0; m < M; m++)
                    {
                        int ao = m * K; float acc = 0f;
                        for (int k = 0; k < K; k++)
                            acc += span_a[ao + k] * W.Deq(transB ? ((long)nn * K + k) : ((long)k * N + nn));
                        span_y[(int)((long)m * N + nn)] = alpha * acc;
                    }
                });
            }
        }
        var outT = Tensor.F(y, M, N);
        return C != null ? Bcast(outT, C, (p, q) => p + beta * q) : outT;
    }

    // ===== Gemma-3n E2B q4 ONNX contrib ops (com.microsoft). Block-wise UNSIGNED uint4 with an explicit per-block
    // zero-point — distinct from QGemm's per-row SIGNED packing. Contracts read straight off the q4 .db export. =====

    // one 4-bit nibble at logical index `idx` in a packed row starting at byte `rowOff` (low nibble = even idx).
    static unsafe int Nib4(byte* buf, int rowOff, int idx) => (buf[rowOff + (idx >> 1)] >> ((idx & 1) << 2)) & 0xF;

    // GPU MatMulNBits: opt-in (mirrors --gpu-matmul / DPGPU_BACKEND). Routes the q4 contraction straight through
    // GpuD3D12/GpuVulkan's GemmQ4 — the packed uint8 B/zp buffers and fp32 scales go to the GPU as-is; the
    // dequantized fp32 weight is never materialized on the CPU. Fast path only (bits==4, no ONNX zero-point axis
    // quirks beyond what MatMulNBits itself already assumes); any GPU fault latches _gpuQ4Dead and degrades to the
    // CPU scalar path (the oracle), once, logged — same inv-9 shape as GpuMatMul.
    public static bool UseGpuMatMulNBits = false;
    static bool _gpuQ4Dead = false;
    // Presence-flow (re-landed once AHB residency deleted the per-call upload churn that killed the razr):
    // q4 GEMMs flow to the GPU whenever the seam initializes — no switch, no env var. A seam-init fault or
    // an absent GPU latches _gpuQ4Dead once, and everything flows to CPU (inv-8/9). Probes at most once.
    static bool GpuPresent()
    {
        try { Gpu.DeviceName(); return true; }
        catch (Exception ex)
        {
            _gpuQ4Dead = true;
            Console.Error.WriteLine($"GPU rung absent ({ex.GetType().Name}: {ex.Message}); q4 flows to CPU.");
            return false;
        }
    }
    // Auto-route mode (CRQ190 R0): DPGPU_BACKEND=auto (the CLI's `--gpu-matmul auto` sets the variable
    // before the first Dp touch). Hoisted to a static readonly so the router costs ONE env probe per
    // process, none per dispatch. Default (unset/other values) leaves behavior exactly the standing knobs.
    public static readonly bool AutoRouteMatMulNBits =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DPGPU_BACKEND")) ||
        !string.Equals(Environment.GetEnvironmentVariable("DPGPU_BACKEND"), "cpu", StringComparison.OrdinalIgnoreCase);
    // Resident-weight cache key: stable per weight Tensor object for the lifetime of the process (weights are
    // loaded once at model-load time and reused every decode step). RuntimeHelpers.GetHashCode is the object's
    // IDENTITY hash (never overridden, ignores Tensor's own Equals/GetHashCode), so it stays fixed across calls
    // and two distinct Tensor objects essentially never collide; folded together with the byte length in the
    // low bits so an (unlikely) identity-hash collision across two different-sized weights still disambiguates.
    // key<=0 means "don't cache" (GpuD3D12 old per-call behavior) - the sign bit is cleared so the token space
    // stays strictly positive and unambiguous.
    static long GpuWeightKey(Tensor bTensor, int byteLen)
    {
        long h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bTensor);
        long key = (h << 20) ^ (uint)byteLen;
        return key & long.MaxValue;   // clear sign bit: negative/zero are reserved for "no cache"
    }
    internal static Tensor GpuMatMulNBits(Tensor[] x, NodeProto n, int K, int N, int bs, int nBlk, int rowBytes, int zpRowBytes, bool hasZp, float[] a, float[] scsp, byte[] bArr, byte[] zpArr, int M)
    {
        var c = new float[(long)M * N];
        // key derives from the GEOMETRY byte length (rowBytes*N == the packed weight's full length), not
        // bArr.Length - resident-aware callers legitimately pass an empty bArr once the weight is uploaded.
        long key = GpuWeightKey(x[1], rowBytes * N);
        // variant rungs, placed beside the exe by the tournament (ShaderTournament.ResolveQ4): M==1 decode ->
        // the GEMV kernel (8 output rows per group, 16 lanes each, tileN=8/tileM=1), M>1 prefill -> the 16x16
        // tiled kernel. Both are BlockSize==32-only; an absent file or ineligible shape falls back to the naive
        // gemm_q4.dxil rung (and any GPU fault still latches _gpuQ4Dead down to the CPU oracle).
        byte[] dxil = GemmDxilQ4(); int tileN = 16, tileM = 16;
        if (bs == 32)
        {
            // Both variant kernels drop the naive kernel's per-thread bounds check in exchange for uniform,
            // branch-free control flow (CRQ190) - safe ONLY when the dispatch is exactly tile-aligned, since an
            // out-of-range Load on these root-descriptor SRVs is undefined, not guaranteed-zero. An unaligned
            // shape falls back to the naive rung (which still bounds-checks every thread) until the tournament
            // ships an aligned/peeled or padded variant (tracked, not yet built).
            if (M == 1 && N % 8 == 0 && (long)N <= 8L * 65535)   // D3D12 dispatch cap: 65535 groups of 8 rows
            {
                var v = Q4Dxil(1);
                if (v.Length > 0) { dxil = v; tileN = 8; tileM = 1; }
            }
            else if (M > 1 && M % 16 == 0)
            {
                var v = Q4Dxil(2);
                if (v.Length > 0) dxil = v;
            }
        }
        int rc = Gpu.dpgpu_gemm_q4(a, bArr, scsp, zpArr, c, (uint)M, (uint)N, (uint)K, (uint)bs, hasZp, dxil, key, tileN, tileM, x[1]);
        _ = n; _ = nBlk; _ = rowBytes; _ = zpRowBytes;   // geometry re-derived inside the GPU kernel from M/N/K/bs
        if (rc != 0) throw new InvalidOperationException($"dpgpu_gemm_q4 rc={rc}");
        var outShape0 = (int[])x[0].Shape.Clone(); outShape0[outShape0.Length - 1] = N;
        return Tensor.F(c, outShape0);
    }
    // [0]=naive gemm_q4.dxil (the fallback rung), [1]=gemm_q4_gemv.dxil (M==1), [2]=gemm_q4_tiled.dxil (M>1);
    // Array.Empty = probed and absent. The tournament rewrites these files and then nulls this cache (reflection).
    static byte[][] _gemmQ4Dxil;
    static byte[] Q4Dxil(int variant)
    {
        _gemmQ4Dxil ??= new byte[3][];
        if (_gemmQ4Dxil[variant] != null) return _gemmQ4Dxil[variant];
        string name = variant == 1 ? "gemm_q4_gemv.dxil" : variant == 2 ? "gemm_q4_tiled.dxil" : "gemm_q4.dxil";
        return _gemmQ4Dxil[variant] = ReadDxilResource(name);
    }
    internal static byte[] GemmDxilQ4() => Q4Dxil(0);

    // GPU dispatch that skips the b/scale/zp ToArray copies once the weight is resident on the adapter
    // (GpuD3D12 keeps them in a DEFAULT-heap buffer per weightKey; on a cache hit GemmQ4 never reads
    // those managed arrays). First sight of a weight still pays the one-time copy+upload; Vulkan reads
    // the arrays every call, so Gpu.QueryResidentQ4 reports false there and the copies stay.
    static Tensor GpuMatMulNBitsResident(Tensor[] x, NodeProto n, int K, int N, int bs, int M)
    {
        int nBlk = K / bs, rowBytes = nBlk * (bs * 4 / 8), zpRowBytes = (nBlk * 4 + 7) / 8;
        var a = x[0].AsF(); var scsp = x[2].AsF();
        var bSpan = x[1].ReadRawb(); bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        bool resident = Gpu.QueryResidentQ4(GpuWeightKey(x[1], rowBytes * N));
        // An AHB-backed weight never needs the Bq managed copy: GpuVulkan imports (or, on import fault,
        // reads) the SAME bytes straight from the weight's map — the 33MB/GEMM churn is structurally gone.
        // Scales/zp stay per-call uploads on Vulkan (KBs), so their copies key on D3D12 residency only.
        bool ahb = x[1].AhbWeight != 0 || x[1].VkWeightBuf != 0;
        return GpuMatMulNBits(x, n, K, N, bs, nBlk, rowBytes, zpRowBytes, hasZp,
            a.ToArray(),
            resident ? Array.Empty<float>() : scsp.ToArray(),
            resident || ahb ? Array.Empty<byte>() : bSpan.ToArray(),
            resident || !hasZp ? Array.Empty<byte>() : zpSpan.ToArray(), M);
    }

    // ---- per-shape CPU/GPU router (CRQ190 R0) ----
    // Routed entry for the Dispatch MatMulNBits case. With no route table (auto mode off, or a bare
    // static Dispatch caller) or with any explicit knob engaged, this IS the standing MatMulNBits call.
    // Routing engages only for the SIMD-comparable fast shape (bits=4, block_size=32).
    static Tensor ResolveMatMulNBits(Tensor[] x, NodeProto n, DpxRoute route)
    {
        if (route == null || UseGpuMatMulNBits || ForceScalarMatMulNBits || _gpuQ4Dead || !Vector128.IsHardwareAccelerated)
            return MatMulNBits(x, n);
        int K = (int)L(n, "K", 0), N = (int)L(n, "N", 0), bits = (int)L(n, "bits", 4), bs = (int)L(n, "block_size", 32);
        if (bits != 4 || bs != 32 || K <= 0 || (K % bs) != 0)
            return MatMulNBits(x, n);
        int M = (int)(x[0].Count / K);
        if (!route.Query(M, N, K, bs, out bool gpuWins))
            return ResolveMatMulNBitsWinner(x, n, route, M, N, K, bs);
        if (gpuWins)
        {
            if (DpxExperiments.ShouldDrop(n)) return DpxExperiments.AllocZeros(x, n);
            try { return GpuMatMulNBitsResident(x, n, K, N, bs, M); }
            catch (Exception ex)
            {
                _gpuQ4Dead = true;   // inv-9: a GPU fault latches, degrades to CPU once, logged
                Console.Error.WriteLine($"GPU MatMulNBits unavailable ({ex.GetType().Name}: {ex.Message}); falling back to CPU.");
            }
        }
        return MatMulNBits(x, n);   // CPU side: the standing knob-free path (SIMD here; scalar oracle untouched)
    }

    // First sight of a shape under auto-route: a direct timed comparison, winner cached for the model's
    // lifetime. One untimed warmup per lane (GPU warmup pays PSO compile + weight residency, CPU warmup
    // pays JIT/page-in), then two timed reps each; min wins - min is the steady-state signal a dispatch-
    // time race can afford on a shared box. A GPU fault latches _gpuQ4Dead (inv-9) and every later shape
    // resolves CPU through the gate above. Returns the winner's LAST output; discarded CPU outputs are
    // FreeNative'd (they never enter Run's liveness map).
    static unsafe Tensor ResolveMatMulNBitsWinner(Tensor[] x, NodeProto n, DpxRoute route, int M, int N, int K, int bs)
    {
        if (DpxExperiments.ShouldDrop(n)) return DpxExperiments.AllocZeros(x, n);
        const int REPS = 2;
        double freq = System.Diagnostics.Stopwatch.Frequency;
        Tensor gpuOut = null; double gpuMs = double.MaxValue;
        try
        {
            GpuMatMulNBitsResident(x, n, K, N, bs, M);   // warmup: PSO + weight upload
            for (int r = 0; r < REPS; r++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                gpuOut = GpuMatMulNBitsResident(x, n, K, N, bs, M);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
                if (ms < gpuMs) gpuMs = ms;
            }
        }
        catch (Exception ex)
        {
            _gpuQ4Dead = true;
            Console.Error.WriteLine($"GPU MatMulNBits unavailable ({ex.GetType().Name}: {ex.Message}); falling back to CPU.");
            gpuOut = null;
        }
        // CPU lane: the same prep MatMulNBits does, the SIMD kernel called knob-free (DpxRace.CpuLane's shape).
        int nBlk = K / bs, rowBytes = nBlk * (bs * 4 / 8), zpRowBytes = (nBlk * 4 + 7) / 8, defZp = 8;
        var a = x[0].AsF(); var scsp = x[2].AsF(); int scLen = scsp.Length;
        var bSpan = x[1].ReadRawb(); bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        Tensor cpuOut = null; double cpuMs = double.MaxValue;
        MatMulNBitsSimd(x, a, scsp, bSpan, zpSpan, K, N, M, nBlk, rowBytes, zpRowBytes, defZp, scLen).FreeNative();   // warmup
        for (int r = 0; r < REPS; r++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var o = MatMulNBitsSimd(x, a, scsp, bSpan, zpSpan, K, N, M, nBlk, rowBytes, zpRowBytes, defZp, scLen);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
            if (ms < cpuMs) cpuMs = ms;
            if (r < REPS - 1) o.FreeNative(); else cpuOut = o;
        }
        bool gpuWins = gpuOut != null && gpuMs < cpuMs;
        if (gpuOut != null)
        {
            route.Register(M, N, K, bs, gpuWins, cpuMs, gpuMs);
            Console.Error.WriteLine($"[DPX route] M={M} N={N} K={K} bs={bs} -> {(gpuWins ? "gpu" : "cpu")} (cpu {cpuMs:F3} ms, gpu {gpuMs:F3} ms)");
        }
        if (gpuWins) { cpuOut.FreeNative(); return gpuOut; }
        return cpuOut;
    }

    // MatMulNBits: Y = A @ dequant(B)^T. B is uint8 [N, K/bs, bs*bits/8] (k-major nibbles, byte=k/2). scale/zp are
    // per (output-row n, block b=k/bs); 4-bit unsigned, dequant = (q - zp)·scale. zp defaults to 2^(bits-1) when absent.
    static unsafe Tensor MatMulNBits(Tensor[] x, NodeProto n)
    {
        if (DpxExperiments.ShouldDrop(n)) return DpxExperiments.AllocZeros(x, n);   // deadline-drop hook: this correction missed its deadline -> residual carries forward with zeros
        int K = (int)L(n, "K", 0), N = (int)L(n, "N", 0), bits = (int)L(n, "bits", 4), bs = (int)L(n, "block_size", 32);
        int nBlk = K / bs, rowBytes = nBlk * (bs * bits / 8), zpRowBytes = (nBlk * bits + 7) / 8, defZp = 1 << (bits - 1);
        var a = x[0].AsF(); var scsp = x[2].AsF(); int M = (int)(x[0].Count / K), scLen = scsp.Length;
        var bSpan = x[1].ReadRawb(); bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        // GPU q4 engagement is OPT-IN on Android until the Vulkan q4 shader + AHB import are parity-verified
        // ON THE ADRENO (the RTX proved D3D12+Vulkan; the phone's first turn hung — unvalidated on that GPU).
        // The AHB residency machinery below is fully built and validated-at-alloc; a single-GEMM on-device
        // parity+timing harness is the gate before presence-flow re-lands. UseGpuMatMulNBits forces it on.
        if (bits == 4 && !_gpuQ4Dead && UseGpuMatMulNBits)
        {
            try
            {
                return GpuMatMulNBitsResident(x, n, K, N, bs, M);   // skips the weight re-copies once resident
            }
            catch (Exception ex)
            {
                _gpuQ4Dead = true;
                Console.Error.WriteLine($"GPU MatMulNBits unavailable ({ex.GetType().Name}: {ex.Message}); falling back to CPU.");
            }
        }
        if (!ForceScalarMatMulNBits && bits == 4 && bs == 32 && Vector128.IsHardwareAccelerated)
            return MatMulNBitsSimd(x, a, scsp, bSpan, zpSpan, K, N, M, nBlk, rowBytes, zpRowBytes, defZp, scLen);
        var y = TensorArena.AllocSpan((long)M * N);
        // pin: Span can't cross the lambda boundary (CS8175) - `fixed` transparently pins a managed
        // array OR no-ops over already-stable VOM-region memory, so B/zp being weight-load-time VOM
        // handles or legacy GC arrays needs no branch here (CRQ164 handle-indirection payoff).
        fixed (float* p_y = y) fixed (float* p_a = a) fixed (float* p_sc = scsp)
        fixed (byte* p_b = bSpan) fixed (byte* p_zp = zpSpan)
        {
            float* py = p_y; float* pa = p_a; float* psc = p_sc; int yl = y.Length, al = a.Length;
            byte* pB = p_b; byte* pZp = p_zp;
            // Scalar per-N fan-out over DpxGang lanes (CRQ195), gated by Fence.WaitAll - brutal
            // synchrony, no Task/ThreadPool. The pinned pointers above stay valid for the whole call
            // because WaitAll blocks the `fixed` block's exit until every lane's phase-1 LaneWork has
            // returned. Each lane owns a static, disjoint [lo,hi) row range; the per-row math below is
            // unchanged from the sequential form.
            DpGangForEachN(N, nn =>
            {
                var sy = new Span<float>(py, yl); var sa = new Span<float>(pa, al); var sc = new Span<float>(psc, scLen);
                int rb = nn * rowBytes, zb = nn * zpRowBytes;
                for (int m = 0; m < M; m++)
                {
                    int ao = m * K; float acc = 0f;
                    for (int b = 0; b < nBlk; b++)
                    {
                        float s = sc[nn * nBlk + b]; int zp = pZp != null ? Nib4(pZp, zb, b) : defZp;
                        float aq = 0f, asum = 0f; int k0 = b * bs;
                        for (int i = 0; i < bs; i++)
                        { int k = k0 + i; int q = (pB[rb + (k >> 1)] >> ((k & 1) << 2)) & 0xF; float av = sa[ao + k]; aq += av * q; asum += av; }
                        acc += s * (aq - zp * asum);   // int-domain accumulate, lift to real units at the block boundary
                    }
                    sy[m * N + nn] = acc;
                }
            });
        }
        var outShape = (int[])x[0].Shape.Clone(); outShape[outShape.Length - 1] = N;
        return Tensor.F(y, outShape);
    }

    // The scalar MatMulNBits fallback's DpxGang fan-out (CRQ195 first caller, mirroring how
    // DpxQnn.Project got test.dpx.qnn-project.ps1): partitions [0,N) into LaneCount disjoint,
    // contiguous ranges (static split - every lane gets a fixed range up front, no work-stealing;
    // that would need a shared cursor, which is exactly the ad hoc synchronization DpxGang replaces)
    // and drives ONE gang through ONE phase via DpxGang.WaitAll - the barrier, per invariant 8. One
    // gang per call (stand up, run one phase, tear down) since MatMulNBits is a one-shot kernel
    // dispatch, not a long-lived loop; DpxDecoder's per-token decode loop is the future caller that
    // would reuse a gang across phases instead.
    static void DpGangForEachN(int N, Action<int> perN)
    {
        int lanes = Math.Max(1, Math.Min(N, Environment.ProcessorCount));
        using var gang = new DpxGang($"\\Agent\\Dpx\\Gang\\MatMulNBits\\{Guid.NewGuid():N}", lanes);
        int baseCount = N / lanes, rem = N % lanes;
        gang.LaneWork = (lane, _) =>
        {
            // lanes [0,rem) take one extra row so the whole [0,N) range is covered exactly once.
            int lo = lane * baseCount + Math.Min(lane, rem);
            int hi = lo + baseCount + (lane < rem ? 1 : 0);
            for (int nn = lo; nn < hi; nn++) perN(nn);
        };
        gang.WaitAll();   // the barrier: every lane's row range must finish before the caller reads y
    }

    // Force the scalar MatMulNBits path - the numerical oracle every faster variant is diffed against.
    public static bool ForceScalarMatMulNBits = false;

    // SIMD MatMulNBits inner loop (recipe: ggml-org/llama.cpp ggml_vec_dot_q4_0_q8_0's f32-activation shape -
    // reimplemented, not linked; thank you ggml). Unpack order stays OURS: sequential nibbles, byte k>>1, low
    // nibble = even k (test.dpx.q4-packing-order.ps1) - NEVER ggml's split order. Portable Vector128 lanes so
    // the same code JITs to SSE on x64 and NEON on the Android arm64 head. Two per-call precomputes amortized
    // over all N output rows: activations deinterleaved into even/odd halves (so the nibble planes FMA against
    // contiguous loads), and per-(m,block) activation sums (the zero-point term drops out of the inner loop).
    // On AVX-512 hardware a Vector512 rung takes over: one q4 block = 16 packed bytes, so a single 64-byte load
    // carries 4 blocks; rows with nBlk % 4 != 0 (and NEON/SSE-only hardware) take the Vector128 rung unchanged.
    // DPX_MMNB=128 pins the Vector128 rung so both rungs stay benchable on 512-capable hardware.
    internal static unsafe Tensor MatMulNBitsSimd(Tensor[] x, Span<float> a, Span<float> scsp, Span<byte> bSpan, Span<byte> zpSpan,
                                         int K, int N, int M, int nBlk, int rowBytes, int zpRowBytes, int defZp, int scLen)
    {
        int half = K / 2;
        var y = TensorArena.AllocSpan((long)M * N);
        var ae = TensorArena.AllocSpan((long)M * half);    // a[m, even k], contiguous per (m, block): 16 floats/block
        var ao = TensorArena.AllocSpan((long)M * half);    // a[m, odd k]
        var asums = TensorArena.AllocSpan((long)M * nBlk); // per-(m, block) activation sum for the zp term
        fixed (float* p_y = y) fixed (float* p_a = a) fixed (float* p_sc = scsp)
        fixed (float* p_ae = ae) fixed (float* p_ao = ao) fixed (float* p_as = asums)
        fixed (byte* p_b = bSpan) fixed (byte* p_zp = zpSpan)
        {
            float* py = p_y; float* pa = p_a; float* psc = p_sc; float* pae = p_ae; float* pao = p_ao; float* pas = p_as;
            byte* pB = p_b; byte* pZp = p_zp; int yl = y.Length;
            // per-row precompute is O(M·K): M=1 decode runs it inline, prefill (M rows) splits across cores
            // like the main loop below (it was a serial stall ahead of the parallel region at prefill).
            Action<int> preRow = m =>
            {
                int ao0 = m * K, h0 = m * half;
                for (int j = 0; j < half; j++) { pae[h0 + j] = pa[ao0 + 2 * j]; pao[h0 + j] = pa[ao0 + 2 * j + 1]; }
                for (int b = 0; b < nBlk; b++)
                {
                    float s = 0f; int k0 = b * bs_32;
                    for (int i = 0; i < bs_32; i++) s += pa[ao0 + k0 + i];
                    pas[m * nBlk + b] = s;
                }
            };
            if (M > 1) System.Threading.Tasks.Parallel.For(0, M, preRow); else preRow(0);
            if (Vector512.IsHardwareAccelerated && (nBlk & 3) == 0
                && Environment.GetEnvironmentVariable("DPX_MMNB") != "128")
            {
                var mask512 = Vector512.Create((byte)0x0F);
                System.Threading.Tasks.Parallel.For(0, N, nn =>
                {
                    var sy = new Span<float>(py, yl); var sc = new Span<float>(psc, scLen);
                    int rb = nn * rowBytes, zb = nn * zpRowBytes, scBase = nn * nBlk;
                    for (int m = 0; m < M; m++)
                    {
                        int h0 = m * half, as0 = m * nBlk;
                        var accv = Vector512<float>.Zero; float zpAcc = 0f;
                        for (int b = 0; b < nBlk; b += 4)
                        {
                            var vb = Vector512.Load(pB + rb + (b << 4));             // 64 bytes = blocks b..b+3
                            var lo = vb & mask512;                                   // even-k nibbles, j-order
                            var hi = Vector512.ShiftRightLogical(vb, 4) & mask512;   // odd-k nibbles, j-order
                            int e0 = h0 + (b << 4);
                            // Widen keeps element order, so each uint quarter is exactly one block's 16 nibbles
                            var (lo01, lo23) = Vector512.Widen(lo);
                            var (hi01, hi23) = Vector512.Widen(hi);
                            var (l0, l1) = Vector512.Widen(lo01); var (l2, l3) = Vector512.Widen(lo23);
                            var (o0, o1) = Vector512.Widen(hi01); var (o2, o3) = Vector512.Widen(hi23);
                            var aq0 = Vector512.ConvertToSingle(l0.AsInt32()) * Vector512.Load(pae + e0)
                                    + Vector512.ConvertToSingle(o0.AsInt32()) * Vector512.Load(pao + e0);
                            var aq1 = Vector512.ConvertToSingle(l1.AsInt32()) * Vector512.Load(pae + e0 + 16)
                                    + Vector512.ConvertToSingle(o1.AsInt32()) * Vector512.Load(pao + e0 + 16);
                            var aq2 = Vector512.ConvertToSingle(l2.AsInt32()) * Vector512.Load(pae + e0 + 32)
                                    + Vector512.ConvertToSingle(o2.AsInt32()) * Vector512.Load(pao + e0 + 32);
                            var aq3 = Vector512.ConvertToSingle(l3.AsInt32()) * Vector512.Load(pae + e0 + 48)
                                    + Vector512.ConvertToSingle(o3.AsInt32()) * Vector512.Load(pao + e0 + 48);
                            float s0 = sc[scBase + b], s1 = sc[scBase + b + 1], s2 = sc[scBase + b + 2], s3 = sc[scBase + b + 3];
                            // scale in-lane, one horizontal Sum per (m,nn) after the loop; zp term stays scalar
                            accv += aq0 * Vector512.Create(s0) + aq1 * Vector512.Create(s1)
                                  + aq2 * Vector512.Create(s2) + aq3 * Vector512.Create(s3);
                            int z0 = defZp, z1 = defZp, z2 = defZp, z3 = defZp;
                            if (pZp != null) { z0 = Nib4(pZp, zb, b); z1 = Nib4(pZp, zb, b + 1); z2 = Nib4(pZp, zb, b + 2); z3 = Nib4(pZp, zb, b + 3); }
                            zpAcc += s0 * z0 * pas[as0 + b] + s1 * z1 * pas[as0 + b + 1]
                                   + s2 * z2 * pas[as0 + b + 2] + s3 * z3 * pas[as0 + b + 3];
                        }
                        sy[m * N + nn] = Vector512.Sum(accv) - zpAcc;
                    }
                });
            }
            else
            {
            var maskF = Vector128.Create((byte)0x0F);
            System.Threading.Tasks.Parallel.For(0, N, nn =>
            {
                var sy = new Span<float>(py, yl); var sc = new Span<float>(psc, scLen);
                int rb = nn * rowBytes, zb = nn * zpRowBytes, scBase = nn * nBlk;
                for (int m = 0; m < M; m++)
                {
                    int h0 = m * half, as0 = m * nBlk; float acc = 0f;
                    for (int b = 0; b < nBlk; b++)
                    {
                        var vb = Vector128.Load(pB + rb + (b << 4));
                        var lo = vb & maskF;                                     // even-k nibbles, j-order
                        var hi = Vector128.ShiftRightLogical(vb, 4) & maskF;     // odd-k nibbles, j-order
                        int e0 = h0 + (b << 4);
                        var (lo01, lo23) = Vector128.Widen(lo);
                        var (hi01, hi23) = Vector128.Widen(hi);
                        var (l0, l1) = Vector128.Widen(lo01); var (l2, l3) = Vector128.Widen(lo23);
                        var (h1_, h2_) = Vector128.Widen(hi01); var (h3_, h4_) = Vector128.Widen(hi23);
                        var aqv = Vector128.ConvertToSingle(l0.AsInt32()) * Vector128.Load(pae + e0)
                                + Vector128.ConvertToSingle(l1.AsInt32()) * Vector128.Load(pae + e0 + 4)
                                + Vector128.ConvertToSingle(l2.AsInt32()) * Vector128.Load(pae + e0 + 8)
                                + Vector128.ConvertToSingle(l3.AsInt32()) * Vector128.Load(pae + e0 + 12)
                                + Vector128.ConvertToSingle(h1_.AsInt32()) * Vector128.Load(pao + e0)
                                + Vector128.ConvertToSingle(h2_.AsInt32()) * Vector128.Load(pao + e0 + 4)
                                + Vector128.ConvertToSingle(h3_.AsInt32()) * Vector128.Load(pao + e0 + 8)
                                + Vector128.ConvertToSingle(h4_.AsInt32()) * Vector128.Load(pao + e0 + 12);
                        int zp = pZp != null ? Nib4(pZp, zb, b) : defZp;
                        acc += sc[scBase + b] * (Vector128.Sum(aqv) - zp * pas[as0 + b]);
                    }
                    sy[m * N + nn] = acc;
                }
            });
            }
        }
        var outShape = (int[])x[0].Shape.Clone(); outShape[outShape.Length - 1] = N;
        return Tensor.F(y, outShape);
    }
    const int bs_32 = 32;   // the SIMD path is gated to block_size == 32 (gemma4 q4 export); other sizes stay scalar

    // GatherBlockQuantized: gather rows of a block-q4 table by indices, dequant inline (gemma embed: data uint8
    // [V, H·bits/8], quantize_axis=1 along H, gather_axis=0). out = dequant(data[indices]) -> [*indices.shape, H].
    static unsafe Tensor GatherBlockQuantized(Tensor[] x, NodeProto n)
    {
        int bits = (int)L(n, "bits", 4), bs = (int)L(n, "block_size", 32);
        int gAxis = (int)L(n, "gather_axis", 0), qAxis = (int)L(n, "quantize_axis", 1);
        if (gAxis != 0 || qAxis != 1) throw new NotImplementedException($"GatherBlockQuantized gather_axis={gAxis} quantize_axis={qAxis} (only 0/1 wired)");
        var dataSpan = x[0].ReadRawb(); var idx = x[1].AsI(); var sc = x[2].AsF();
        bool hasZp = x.Length > 3 && x[3] != null; var zpSpan = hasZp ? x[3].ReadRawb() : default;
        int V = x[0].Shape[0]; int rowBytes = 1; for (int k = 1; k < x[0].Shape.Length; k++) rowBytes *= x[0].Shape[k];
        int H = rowBytes * 8 / bits, nBlk = H / bs, zpRowBytes = (nBlk * bits + 7) / 8, defZp = 1 << (bits - 1);
        long outN = (long)idx.Length * H; var o = TensorArena.AllocSpan(outN);
        // fixed transparently pins a managed array OR no-ops over already-stable VOM-region memory (same
        // reasoning as MatMulNBits) - no branch on whether the embedding table loaded via DpxTensor/VOM.
        fixed (byte* p_data = dataSpan) fixed (byte* p_zp = zpSpan)
        {
            byte* data = p_data; byte* zpb = p_zp;
            for (int t = 0; t < idx.Length; t++)
            {
                long r = idx[t]; if (r < 0) r += V; int rb = (int)r * rowBytes, zb = (int)r * zpRowBytes, sb = (int)r * nBlk; long ob = (long)t * H;
                for (int k = 0; k < H; k++)
                {
                    int b = k / bs; int q = (data[rb + (k >> 1)] >> ((k & 1) << 2)) & 0xF;
                    int zp = zpb != null ? Nib4(zpb, zb, b) : defZp;
                    o[(int)(ob + k)] = (q - zp) * sc[sb + b];
                }
            }
        }
        var outShape = new List<int>(x[1].Shape) { H };
        return Tensor.F(o, outShape.ToArray());
    }

    // RotaryEmbedding (com.microsoft): rotate-half RoPE. cos/sin caches are [maxpos, head_dim/2]; position_ids index
    // them. interleaved=0 pairs the two halves: out[i]=x[i]·cos - x[i+half]·sin; out[i+half]=x[i+half]·cos + x[i]·sin.
    // num_heads inferred from a 3D [B,S,hidden] input (head_dim = 2·cos_cols); a 4D [B,N,S,H] input is used as-is.
    static Tensor RotaryEmbedding(Tensor[] x, NodeProto n)
    {
        var inp = x[0]; var pos = x[1].AsI(); var cos = x[2].AsF(); var sin = x[3].AsF();
        bool interleaved = L(n, "interleaved", 0) != 0;
        int half = x[2].Shape[x[2].Shape.Length - 1], rotDim = 2 * half, rank = inp.Shape.Length;
        int B, Nh, S, Hd;
        if (rank == 4) { B = inp.Shape[0]; Nh = inp.Shape[1]; S = inp.Shape[2]; Hd = inp.Shape[3]; }
        else { B = inp.Shape[0]; S = inp.Shape[1]; int heads = (int)L(n, "num_heads", 0); Hd = rotDim; Nh = heads > 0 ? heads : inp.Shape[2] / Hd; }
        var src = inp.AsF(); var o = TensorArena.AllocSpan(src.Length); src.CopyTo(o);
        int posCols = x[1].Shape[x[1].Shape.Length - 1];
        for (int b = 0; b < B; b++)
            for (int s = 0; s < S; s++)
            {
                long p = pos[(b * posCols + s) % pos.Length], cb = p * half;
                for (int h = 0; h < Nh; h++)
                {
                    long bI = rank == 4 ? (((long)b * Nh + h) * S + s) * Hd : (((long)b * S + s) * Nh + h) * Hd;
                    for (int i = 0; i < half; i++)
                    {
                        float c = cos[(int)(cb + i)], sn = sin[(int)(cb + i)];
                        if (interleaved)
                        { float a0 = src[(int)(bI + 2 * i)], a1 = src[(int)(bI + 2 * i + 1)]; o[(int)(bI + 2 * i)] = a0 * c - a1 * sn; o[(int)(bI + 2 * i + 1)] = a1 * c + a0 * sn; }
                        else
                        { float a0 = src[(int)(bI + i)], a1 = src[(int)(bI + i + half)]; o[(int)(bI + i)] = a0 * c - a1 * sn; o[(int)(bI + i + half)] = a1 * c + a0 * sn; }
                    }
                }
            }
        return Tensor.F(o, inp.Shape);
    }

    // SimplifiedLayerNormalization (RMSNorm): y = x / sqrt(mean(x²)+eps) · weight. No mean-subtract, no bias.
    static Tensor SimplifiedLayerNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var w = x[1].AsF(); var xf = X.AsF();
        int r = X.Shape.Length, axis = (int)L(n, "axis", -1); if (axis < 0) axis += r;
        float eps = F(n, "epsilon", 1e-5f);
        long inner = 1; for (int k = axis; k < r; k++) inner *= X.Shape[k];
        long outer = X.Count / inner; var o = TensorArena.AllocSpan(X.Count);
        for (long ob = 0; ob < outer; ob++)
        {
            long bI = ob * inner; double ss = 0;
            for (long i = 0; i < inner; i++) { double d = xf[(int)(bI + i)]; ss += d * d; }
            float inv = (float)(1.0 / Math.Sqrt(ss / inner + eps));
            for (long i = 0; i < inner; i++) o[(int)(bI + i)] = xf[(int)(bI + i)] * inv * w[(int)(i % w.Length)];
        }
        return Tensor.F(o, X.Shape);
    }

    static long KvIdx(Tensor t, int b, int h, int s, int S, int Nkv, int H)
        => t.Shape.Length == 4 ? (((long)b * Nkv + h) * S + s) * H : (((long)b * S + s) * Nkv + h) * H;
    // additive attention_bias lookup with size-1 broadcast over a 4D [.,.,Sq,Tk] bias.
    static float BiasAt(ReadOnlySpan<float> bf, int[] sh, int b, int qh, int qi, int kj)
    {
        if (sh == null || sh.Length < 4) return 0f;
        int i0 = sh[0] == 1 ? 0 : b, i1 = sh[1] == 1 ? 0 : qh, i2 = sh[2] == 1 ? 0 : qi, i3 = sh[3] == 1 ? 0 : kj;
        return bf[(int)((((long)i0 * sh[1] + i1) * sh[2] + i2) * sh[3] + i3)];
    }

    // GroupQueryAttention (com.microsoft): grouped MQA with KV-cache append, causal + sliding-window mask, additive
    // attention_bias[10]. do_rotary=0 here (RoPE applied by upstream RotaryEmbedding nodes). q[B,S,Nq·H]; k/v[B,S,Nkv·H];
    // past_k/v[B,Nkv,past,H]. Outputs: attn[B,S,Nq·H], present_k/v[B,Nkv,total,H] (the full cache; window only masks).
    static unsafe Tensor[] GroupQueryAttention(Tensor[] x, NodeProto n)
    {
        if (ActiveVerbose)
        {
            Console.Error.WriteLine($"[DEBUG GQA] Node: {n.Name}");
            for (int i = 0; i < x.Length; i++)
            {
                var t = x[i];
                if (t == null)
                {
                    Console.Error.WriteLine($"  x[{i}]: null");
                }
                else
                {
                    string shapeStr = string.Join(",", t.Shape);
                    Console.Error.WriteLine($"  x[{i}]: shape=[{shapeStr}], count={t.Count}, FpIsInstantiated={t.Fp != null}, NativePtr={(nint)t.NativePtr:X}, IpIsInstantiated={t.Ip != null}");
                }
            }
        }

        int Nq = (int)L(n, "num_heads", 0), Nkv = (int)L(n, "kv_num_heads", 0), win = (int)L(n, "local_window_size", -1);
        float scaleAttr = F(n, "scale", 0f), softcap = F(n, "softcap", 0f);
        var pastK = x[3]; var pastV = x[4];
        int B = pastK.Shape[0], past = pastK.Shape[2], H = pastK.Shape[3];
        int S = x[0].Shape.Length == 4 ? x[0].Shape[2] : x[0].Shape[1];
        int total = past + S; float scale = scaleAttr != 0 ? scaleAttr : 1f / MathF.Sqrt(H);
        if (ActiveVerbose)
        {
            Console.Error.WriteLine($"  PARAMS: Nq={Nq}, Nkv={Nkv}, win={win}, scaleAttr={scaleAttr}, softcap={softcap}, B={B}, past={past}, H={H}, S={S}, total={total}, scale={scale}");
        }
        var qf = x[0].AsF(); var kf = x[1].AsF(); var vf = x[2].AsF();
        // KV ring lane (CRQ190): a ring-backed past (Tensor.KvCap = physical seq capacity of a persistent
        // [B,Nkv,cap,H] region) skips the O(total) re-copy - past rows are read where they already live and
        // this step's K/V land in place at row `past`. Everything else takes the original append-copy lane.
        // Same loops, same scalar math either way; only the row stride and the backing storage differ.
        bool ring = pastK.KvCap >= total && pastK.KvCap == pastV.KvCap
            && pastK.NativePtr != null && pastV.NativePtr != null
            && pastK.Shape.Length == 4 && pastV.Shape.Length == 4
            && pastK.Shape[1] == Nkv && pastV.Shape[1] == Nkv
            && pastV.Shape[0] == B && pastV.Shape[2] == past && pastV.Shape[3] == H;
        int kvStride = ring ? pastK.KvCap : total;
        long kvCount = (long)B * Nkv * kvStride * H;
        Span<float> pk, pv;
        if (ring) { pk = new Span<float>(pastK.NativePtr, (int)kvCount); pv = new Span<float>(pastV.NativePtr, (int)kvCount); }
        else
        {
            pk = TensorArena.AllocSpan(kvCount); pv = TensorArena.AllocSpan(kvCount);
            var pkf = pastK.Count > 0 ? pastK.AsF() : default; var pvf = pastV.Count > 0 ? pastV.AsF() : default;
            for (int b = 0; b < B; b++)
                for (int hh = 0; hh < Nkv; hh++)
                    for (int t = 0; t < past; t++)
                    { long d = (((long)b * Nkv + hh) * total + t) * H, sI = (((long)b * Nkv + hh) * past + t) * H; for (int e = 0; e < H; e++) { pk[(int)(d + e)] = pkf[(int)(sI + e)]; pv[(int)(d + e)] = pvf[(int)(sI + e)]; } }
        }
        for (int b = 0; b < B; b++)
            for (int hh = 0; hh < Nkv; hh++)
                for (int t = 0; t < S; t++)
                { long d = (((long)b * Nkv + hh) * kvStride + past + t) * H, sK = KvIdx(x[1], b, hh, t, S, Nkv, H), sV = KvIdx(x[2], b, hh, t, S, Nkv, H); for (int e = 0; e < H; e++) { pk[(int)(d + e)] = kf[(int)(sK + e)]; pv[(int)(d + e)] = vf[(int)(sV + e)]; } }
        var bias = x.Length > 10 ? x[10] : null; var bf = bias != null && bias.Count > 0 ? bias.AsF() : default; var bsh = bias != null && bias.Count > 0 ? bias.Shape : null;
        int g = Nq / Nkv; var outp = TensorArena.AllocSpan((long)B * S * Nq * H);
        int qfLen = qf.Length, pkLen = pk.Length, pvLen = pv.Length, outpLen = outp.Length, bfLen = bf.Length;
        bool q4d = x[0].Shape.Length == 4;
        // Parallel over (batch, query-head): each head's K/V slice is independent, so this is safe with no
        // shared mutable state - `scores` becomes a per-work-item local (Parallel.For's thread-local state),
        // not the single shared buffer the sequential version used. During decode (S=1) this is the ONLY
        // parallelism available in this kernel (Nq heads, e.g. 8, split across cores instead of one thread
        // walking them one at a time); during prefill (S>1) it's additive to that.
        fixed (float* p_qf = qf) fixed (float* p_pk = pk) fixed (float* p_pv = pv) fixed (float* p_outp = outp) fixed (float* p_bf = bf)
        {
            float* pqf = p_qf; float* ppk = p_pk; float* ppv = p_pv; float* poutp = p_outp; float* pbf = p_bf;
            System.Threading.Tasks.Parallel.For(0, B * Nq, () => new float[total], (bqh, _, scores) =>
            {
                int b = bqh / Nq, qh = bqh % Nq, kvh = qh / g;
                var qfL = new Span<float>(pqf, qfLen); var pkL = new Span<float>(ppk, pkLen);
                var pvL = new Span<float>(ppv, pvLen); var outpL = new Span<float>(poutp, outpLen);
                var bfL = bfLen > 0 ? new Span<float>(pbf, bfLen) : default;
                for (int qi = 0; qi < S; qi++)
                {
                    int qpos = past + qi; long qBase = q4d ? (((long)b * Nq + qh) * S + qi) * H : (((long)b * S + qi) * Nq + qh) * H;
                    float mx = float.NegativeInfinity;
                    for (int kj = 0; kj < total; kj++)
                    {
                        if (kj > qpos || (win > 0 && qpos - kj >= win)) { scores[kj] = float.NegativeInfinity; continue; }
                        long kBase = (((long)b * Nkv + kvh) * kvStride + kj) * H; float dot = 0f;
                        for (int e = 0; e < H; e++) dot += qfL[(int)(qBase + e)] * pkL[(int)(kBase + e)];
                        dot *= scale; if (softcap > 0) dot = softcap * MathF.Tanh(dot / softcap);
                        dot += BiasAt(bfL, bsh, b, qh, qi, kj);
                        scores[kj] = dot; if (dot > mx) mx = dot;
                    }
                    double sum = 0; for (int kj = 0; kj < total; kj++) { if (float.IsNegativeInfinity(scores[kj])) { scores[kj] = 0f; continue; } float e = MathF.Exp(scores[kj] - mx); scores[kj] = e; sum += e; }
                    float inv = sum > 0 ? (float)(1.0 / sum) : 0f; long oBase = (((long)b * S + qi) * Nq + qh) * H;
                    for (int e = 0; e < H; e++) { float acc = 0f; for (int kj = 0; kj < total; kj++) if (scores[kj] != 0f) acc += scores[kj] * pvL[(int)((((long)b * Nkv + kvh) * kvStride + kj) * H + e)]; outpL[(int)(oBase + e)] = acc * inv; }
                }
                return scores;
            }, _ => { });
        }
        Tensor presentK, presentV;
        if (ring)
        {
            // present aliases the ring zero-copy: KvCap marks it so the decode loop knows the append
            // already happened in place (and so a re-fed present keeps the ring lane engaged).
            presentK = Tensor.F(pastK.NativePtr, B, Nkv, total, H); presentK.KvCap = pastK.KvCap;
            presentV = Tensor.F(pastV.NativePtr, B, Nkv, total, H); presentV.KvCap = pastV.KvCap;
        }
        else { presentK = Tensor.F(pk, B, Nkv, total, H); presentV = Tensor.F(pv, B, Nkv, total, H); }
        return new[] { Tensor.F(outp, B, S, Nq * H), presentK, presentV };
    }

    static unsafe Tensor Reshape(Tensor a, Tensor shapeT, int flattenAxis = -1, Tensor src = null)
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
        return a.IsInt ? Tensor.I(a.Ip, sh) : (a.NativePtr != null ? Tensor.F(a.NativePtr, sh) : Tensor.F(a.Fp, sh));
    }

    static unsafe Tensor Squeeze(Tensor a, Tensor axesT, NodeProto n)
    {
        var axes = axesT?.AsI()?.Select(v => (int)(v < 0 ? v + a.Shape.Length : v)).ToHashSet();
        var sh = new List<int>();
        for (int k = 0; k < a.Shape.Length; k++) if (!(a.Shape[k] == 1 && (axes == null || axes.Contains(k)))) sh.Add(a.Shape[k]);
        return a.IsInt ? Tensor.I(a.Ip, sh.ToArray()) : (a.NativePtr != null ? Tensor.F(a.NativePtr, sh.ToArray()) : Tensor.F(a.Fp, sh.ToArray()));
    }

    static unsafe Tensor Unsqueeze(Tensor a, Tensor axesT, NodeProto n)
    {
        var axesList = (axesT?.AsI() ?? Ints(n, "axes").Select(v => (long)v).ToArray());
        int r = a.Shape.Length + axesList.Length;
        var axes = axesList.Select(v => (int)(v < 0 ? v + r : v)).ToHashSet();
        var sh = new int[r]; int si = 0;
        for (int k = 0; k < r; k++) sh[k] = axes.Contains(k) ? 1 : a.Shape[si++];
        return a.IsInt ? Tensor.I(a.Ip, sh) : (a.NativePtr != null ? Tensor.F(a.NativePtr, sh) : Tensor.F(a.Fp, sh));
    }

    static Tensor Transpose(Tensor a, int[] perm)
    {
        int r = a.Shape.Length;
        if (perm == null || perm.Length == 0) { perm = new int[r]; for (int k = 0; k < r; k++) perm[k] = r - 1 - k; }
        var outShape = new int[r]; for (int k = 0; k < r; k++) outShape[k] = a.Shape[perm[k]];
        var inStr = ContigStrides(a.Shape); var d = a.AsF(); var o = TensorArena.AllocSpan(d.Length);
        var idx = new int[r];
        for (long lin = 0; lin < d.Length; lin++)
        {
            long src = 0; for (int k = 0; k < r; k++) src += idx[k] * inStr[perm[k]];
            o[(int)lin] = d[(int)src];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < outShape[k]) break; idx[k] = 0; }
        }
        return Tensor.F(o, outShape);
    }

    static unsafe Tensor Concat(Tensor[] xs, int axis)
    {
        int r = xs[0].Shape.Length; if (axis < 0) axis += r;
        // KV ring lane (CRQ190): the manual (non-GQA) attention layers carry KV as Concat(past, new) on
        // the seq axis. With past ring-backed and a single (batch,head) lane, the new rows land in place
        // at row `past` and the ring aliases back out - layout-identical to the copied result because the
        // leading dims are 1, so downstream readers see the same contiguous bytes. Everything else
        // (including any multi-lane concat) takes the general copy below.
        if (xs.Length == 2 && r == 4 && axis == 2 && xs[0].KvCap > 0 && xs[0].NativePtr != null
            && xs[0].Shape[0] == 1 && xs[0].Shape[1] == 1 && !xs[1].IsInt && !xs[1].IsQuant
            && xs[1].Shape.Length == 4 && xs[1].Shape[0] == 1 && xs[1].Shape[1] == 1
            && xs[1].Shape[3] == xs[0].Shape[3] && xs[0].Shape[2] + xs[1].Shape[2] <= xs[0].KvCap)
        {
            int past = xs[0].Shape[2], S = xs[1].Shape[2], H = xs[0].Shape[3];
            xs[1].AsF().CopyTo(new Span<float>(xs[0].NativePtr + (long)past * H, S * H));
            var appended = Tensor.F(xs[0].NativePtr, 1, 1, past + S, H); appended.KvCap = xs[0].KvCap;
            return appended;
        }
        var outShape = (int[])xs[0].Shape.Clone(); outShape[axis] = xs.Sum(t => t.Shape[axis]);
        long n = 1; foreach (var d in outShape) n *= d; var o = TensorArena.AllocSpan(n);
        long outStrAxis = 1; for (int k = axis + 1; k < r; k++) outStrAxis *= outShape[k];
        long blocks = 1; for (int k = 0; k < axis; k++) blocks *= outShape[k];
        long outRow = outShape[axis] * outStrAxis;
        long offAxis = 0;
        foreach (var t in xs)
        {
            var d = t.AsF(); long inRow = t.Shape[axis] * outStrAxis;
            for (long bl = 0; bl < blocks; bl++) d.Slice((int)(bl * inRow), (int)inRow).CopyTo(o.Slice((int)(bl * outRow + offAxis * outStrAxis), (int)inRow));
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
        bool fInt = data.IsInt; var df = (fInt || data.IsQuant) ? (Span<float>)default : data.AsF(); var dl = fInt ? data.Ip : null;
        long total = outer * idx.Length * inner;
        var of = fInt ? (Span<float>)default : TensorArena.AllocSpan(total); var ol = fInt ? new long[total] : null;
        long w = 0;
        for (long o = 0; o < outer; o++)
            foreach (var ix0 in idx)
            { long ix = ix0 < 0 ? ix0 + axisLen : ix0; long baseI = (o * axisLen + ix) * inner;
              if (data.IsQuant) { for (long j = 0; j < inner; j++) of[(int)(w + j)] = data.Deq(baseI + j); }   // dequant just this row
              else if (fInt) Array.Copy(dl, baseI, ol, w, inner); else df.Slice((int)baseI, (int)inner).CopyTo(of.Slice((int)w, (int)inner)); w += inner; }
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
            o[lin] = f(fa[(int)ia], fb[(int)ib]) ? 1L : 0L;
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
            {
                long ic = 0, ix = 0, iy = 0; for (int k = 0; k < sh.Length; k++) { ic += idx[k] * sc[k]; ix += idx[k] * sx[k]; iy += idx[k] * sy[k]; }
                o[lin] = cf[(int)ic] != 0 ? xi[ix] : yi[iy]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
            }
            return Tensor.I(o, sh);
        }
        var xf = x.AsF(); var yf = y.AsF(); var of = TensorArena.AllocSpan(n);
        for (long lin = 0; lin < n; lin++)
        {
            long ic = 0, ix = 0, iy = 0; for (int k = 0; k < sh.Length; k++) { ic += idx[k] * sc[k]; ix += idx[k] * sx[k]; iy += idx[k] * sy[k]; }
            of[(int)lin] = cf[(int)ic] != 0 ? xf[(int)ix] : yf[(int)iy]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; }
        }
        return Tensor.F(of, sh);
    }

    static Tensor Expand(Tensor a, Tensor shapeT)
    {
        var tgt = Array.ConvertAll(shapeT.AsI(), v => (int)v);
        int[] sh = BroadcastShape(a.Shape, tgt); long n = 1; foreach (var d in sh) n *= d;
        var sa = Strides(a.Shape, sh); var idx = new int[sh.Length];
        if (a.IsInt) { var d = a.Ip; var o = new long[n]; for (long lin = 0; lin < n; lin++) { long ia = 0; for (int k = 0; k < sh.Length; k++) ia += idx[k] * sa[k]; o[lin] = d[ia]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } } return Tensor.I(o, sh); }
        else { var d = a.AsF(); var o = TensorArena.AllocSpan(n); for (long lin = 0; lin < n; lin++) { long ia = 0; for (int k = 0; k < sh.Length; k++) ia += idx[k] * sa[k]; o[(int)lin] = d[(int)ia]; for (int k = sh.Length - 1; k >= 0; k--) { if (++idx[k] < sh[k]) break; idx[k] = 0; } } return Tensor.F(o, sh); }
    }

    static Tensor ConstantOfShape(Tensor shapeT, NodeProto n)
    {
        var sh = Array.ConvertAll(shapeT.AsI(), v => (int)v); long cnt = 1; foreach (var d in sh) cnt *= d;
        var va = n.Attribute.FirstOrDefault(a => a.Name == "value");
        if (va != null && va.T != null)
        {
            var vt = FromProto(va.T);
            if (vt.IsInt) { var o = new long[cnt]; var v = vt.Ip[0]; for (long i = 0; i < cnt; i++) o[i] = v; return Tensor.I(o, sh); }
            else { var o = TensorArena.AllocSpan(cnt); var v = vt.AsF()[0]; for (long i = 0; i < cnt; i++) o[(int)i] = v; return Tensor.F(o, sh); }
        }
        return Tensor.F(TensorArena.AllocSpan(cnt), sh);
    }

    static Tensor Range(Tensor start, Tensor limit, Tensor delta)
    {
        if (start.IsInt)
        { long s = start.Ip[0], l = limit.AsI()[0], d = delta.AsI()[0]; var li = new List<long>();
          if (d > 0) for (long v = s; v < l; v += d) li.Add(v); else if (d < 0) for (long v = s; v > l; v += d) li.Add(v); return Tensor.I(li.ToArray(), li.Count); }
        else
        { float s = start.AsF()[0], l = limit.AsF()[0], d = delta.AsF()[0]; var lf = new List<float>();
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

    static unsafe Tensor Reduce(Tensor[] x, NodeProto n, string mode)   // mode: "sum" | "mean" | "max" | "all" -- one loop, not a per-op copy
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
        bool isMax = mode == "max", isMean = mode == "mean", isAll = mode == "all";
        int m = axes.Count; bool trailing = m > 0; for (int k = r - m; k < r; k++) if (k < 0 || !axes.Contains(k)) trailing = false;
        if (trailing && (mode == "sum" || mode == "mean"))   // contiguous trailing block -> each output = SIMD sum of a contiguous run (sum/mean only)
        {
            var of = TensorArena.AllocSpan(outN);
            fixed (float* p_of = of)
            fixed (float* p_d = d)
            {
                float* ptr_of = p_of; float* ptr_d = p_d;
                int ofLen = of.Length; int dLen = d.Length;
                System.Threading.Tasks.Parallel.For(0L, outN, i =>
                {
                    var span_of = new Span<float>(ptr_of, ofLen);
                    var span_d = new Span<float>(ptr_d, dLen);
                    double s = SimdSum(span_d.Slice(checked((int)(i * reduced)), (int)reduced));
                    span_of[(int)i] = (float)(isMean ? s / reduced : s);
                });
            }
            return Tensor.F(of, oshA);
        }
        var acc = new double[outN];
        if (isMax) for (long i = 0; i < outN; i++) acc[i] = double.NegativeInfinity;
        else if (isAll) for (long i = 0; i < outN; i++) acc[i] = 1.0;   // booleans are 0.0f/1.0f floats in this engine (dpx convention); AND starts true
        var idx = new int[r];
        for (long lin = 0; lin < d.Length; lin++)
        {
            long oi = 0; for (int k = 0; k < r; k++) { int od = outDimOfIn[k]; if (od >= 0) { int coord = axes.Contains(k) && keep ? 0 : idx[k]; oi += coord * oStr[od]; } }
            if (isMax) acc[oi] = Math.Max(acc[oi], d[(int)lin]);
            else if (isAll) acc[oi] = (acc[oi] != 0.0 && d[(int)lin] != 0f) ? 1.0 : 0.0;
            else acc[oi] += d[(int)lin];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        var o = TensorArena.AllocSpan(outN);
        for (long i = 0; i < outN; i++) o[(int)i] = (float)(isMean ? acc[i] / reduced : acc[i]);
        return Tensor.F(o, oshA);
    }

    // OneHot: out[..., d, ...] (d inserted at axis) = onVal where indices==d else offVal. values = [offVal, onVal].
    static unsafe Tensor OneHot(Tensor[] x, NodeProto n)
    {
        var indices = x[0]; var depthT = x[1]; var valuesT = x[2];
        int depth = (int)(depthT.IsInt ? depthT.Ip[0] : depthT.AsF()[0]);
        var valSpan = valuesT.AsF(); float offVal = valSpan[0], onVal = valSpan[1];
        int rank = indices.Shape.Length; int axis = (int)L(n, "axis", -1); if (axis < 0) axis += rank + 1;
        var outShape = new int[rank + 1];
        for (int i = 0, j = 0; i < rank + 1; i++) outShape[i] = i == axis ? depth : indices.Shape[j++];
        long outN = 1; foreach (var dd in outShape) outN *= dd;
        var o = TensorArena.AllocSpan(outN);
        long outer = 1; for (int i = 0; i < axis; i++) outer *= outShape[i];
        long inner = 1; for (int i = axis + 1; i < outShape.Length; i++) inner *= outShape[i];
        int outLen = o.Length;
        fixed (float* p_o = o)
        {
            float* ptr_o = p_o;
            if (indices.IsInt)
            {
                var idxArr = indices.Ip;
                System.Threading.Tasks.Parallel.For(0L, outer, oo =>
                {
                    var span_o = new Span<float>(ptr_o, outLen);
                    for (int d = 0; d < depth; d++)
                    {
                        long outBase = (oo * depth + d) * inner;
                        for (long ii = 0; ii < inner; ii++)
                            span_o[(int)(outBase + ii)] = idxArr[oo * inner + ii] == d ? onVal : offVal;
                    }
                });
            }
            else
            {
                var idxF = indices.AsF(); int idxLen = idxF.Length;
                fixed (float* p_idx = idxF)
                {
                    float* ptr_idx = p_idx;
                    System.Threading.Tasks.Parallel.For(0L, outer, oo =>
                    {
                        var span_o = new Span<float>(ptr_o, outLen);
                        var span_idx = new Span<float>(ptr_idx, idxLen);
                        for (int d = 0; d < depth; d++)
                        {
                            long outBase = (oo * depth + d) * inner;
                            for (long ii = 0; ii < inner; ii++)
                                span_o[(int)(outBase + ii)] = (long)span_idx[(int)(oo * inner + ii)] == d ? onVal : offVal;
                        }
                    });
                }
            }
        }
        return Tensor.F(o, outShape);
    }

    static Tensor CumSum(Tensor a, int axis, bool excl, bool rev)
    {
        int r = a.Shape.Length; if (axis < 0) axis += r; var src = a.AsF();
        long inner = 1; for (int k = axis + 1; k < r; k++) inner *= a.Shape[k];
        int A = a.Shape[axis]; long outer = 1; for (int k = 0; k < axis; k++) outer *= a.Shape[k];
        var o = TensorArena.AllocSpan(src.Length);
        for (long ob = 0; ob < outer; ob++) for (long inr = 0; inr < inner; inr++)
        {
            long baseI = ob * A * inner + inr; float run = 0;
            for (int t = 0; t < A; t++) { int ti = rev ? A - 1 - t : t; long pos = baseI + (long)ti * inner; if (excl) { o[(int)pos] = run; run += src[(int)pos]; } else { run += src[(int)pos]; o[(int)pos] = run; } }
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
        else { var d = a.AsF(); var o = TensorArena.AllocSpan(outN); for (long lin = 0; lin < outN; lin++) { long si = 0; for (int k = 0; k < r; k++) si += (st[k] + idx[k] * stp[k]) * inStr[k]; o[(int)lin] = d[(int)si]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < osh[k]) break; idx[k] = 0; } } return Tensor.F(o, osh); }
    }

    static Tensor Pad(Tensor[] x, NodeProto n)
    {
        var a = x[0]; int r = a.Shape.Length; long[] pads = x[1].AsI(); string mode = Str(n, "mode", "constant");
        float cval = (x.Length > 2 && x[2] != null) ? x[2].AsF()[0] : 0f;
        long[] axesA = (x.Length > 3 && x[3] != null) ? x[3].AsI() : Enumerable.Range(0, r).Select(i => (long)i).ToArray();
        var begin = new int[r]; var end = new int[r];
        for (int i = 0; i < axesA.Length; i++) { int ax = (int)axesA[i]; if (ax < 0) ax += r; begin[ax] = (int)pads[i]; end[ax] = (int)pads[i + axesA.Length]; }
        var osh = new int[r]; for (int k = 0; k < r; k++) osh[k] = a.Shape[k] + begin[k] + end[k];
        long outN = 1; foreach (var dd in osh) outN *= dd; var inStr = ContigStrides(a.Shape); var d = a.AsF(); var o = TensorArena.AllocSpan(outN);
        if (mode == "constant") for (long i = 0; i < outN; i++) o[(int)i] = cval;
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
            if (mode == "constant") { if (inside) o[(int)lin] = d[(int)si]; } else o[(int)lin] = d[(int)si];
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
        var Y = TensorArena.AllocSpan((long)seq * numDir * batch * H);
        var Yh = TensorArena.AllocSpan((long)numDir * batch * H); var Yc = TensorArena.AllocSpan((long)numDir * batch * H);
        int wDS = 4 * H * inp, rDS = 4 * H * H, bDS = 8 * H;
        for (int d = 0; d < numDir; d++)
        {
            bool rev = dir == "reverse" || (dir == "bidirectional" && d == 1);
            var h = new float[batch * H]; var c = new float[batch * H];
            if (!initH.IsEmpty) initH.Slice((int)((long)d * batch * H), batch * H).CopyTo(h);
            if (!initC.IsEmpty) initC.Slice((int)((long)d * batch * H), batch * H).CopyTo(c);
            var gate = new float[4 * H];
            for (int ti = 0; ti < seq; ti++)
            {
                int t = rev ? seq - 1 - ti : ti;
                for (int b = 0; b < batch; b++)
                {
                    for (int row = 0; row < 4 * H; row++)
                    {
                        float v = 0; long wb = (long)d * wDS + (long)row * inp; long xb = ((long)t * batch + b) * inp;
                        for (int k = 0; k < inp; k++) v += Wf[(int)(wb + k)] * X[(int)(xb + k)];
                        long rb = (long)d * rDS + (long)row * H; long hb = (long)b * H;
                        for (int k = 0; k < H; k++) v += Rf[(int)(rb + k)] * h[(int)(hb + k)];
                        if (!Bf.IsEmpty) v += Bf[(int)((long)d * bDS + row)] + Bf[(int)((long)d * bDS + 4 * H + row)];
                        gate[row] = v;
                    }
                    for (int j = 0; j < H; j++)
                    {
                        float it = Sig(gate[j]), ot = Sig(gate[H + j]), ft = Sig(gate[2 * H + j]), ctil = MathF.Tanh(gate[3 * H + j]);
                        int ci = b * H + j; float ct = ft * c[ci] + it * ctil; c[ci] = ct;
                        float ht = ot * MathF.Tanh(ct); h[ci] = ht;
                        Y[(int)((((long)t * numDir + d) * batch + b) * H + j)] = ht;
                    }
                }
            }
            h.CopyTo(Yh.Slice((int)((long)d * batch * H), batch * H));
            c.CopyTo(Yc.Slice((int)((long)d * batch * H), batch * H));
        }
        return new[] { Tensor.F(Y, seq, numDir, batch, H), Tensor.F(Yh, numDir, batch, H), Tensor.F(Yc, numDir, batch, H) };
    }

    static unsafe Tensor Conv(Tensor[] x, NodeProto n)
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
        long outN = 1; foreach (var d in outShape) outN *= d; var o = TensorArena.AllocSpan(outN);
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
                var col = TensorArena.AllocSpan((long)K * spatial);
                fixed (float* p_col = col)
                fixed (float* p_xf = xf)
                {
                    float* ptr_col = p_col; float* ptr_xf = p_xf;
                    int colLen = col.Length; int xfLen = xf.Length;
                    System.Threading.Tasks.Parallel.For(0, K, ck =>
                    {
                        int c = ck / (int)kcount, kk = ck % (int)kcount, cin = g * CinG + c;
                        long xBaseC = (long)nn * xStr[0] + (long)cin * xStr[1];
                        var kpos = new int[sp]; { long t = kk; for (int d = sp - 1; d >= 0; d--) { kpos[d] = (int)(t % ksh[d]); t /= ksh[d]; } }
                        var pos = new int[sp]; long colBase = (long)ck * spatial;
                        var span_col = new Span<float>(ptr_col, colLen);
                        var span_xf = new Span<float>(ptr_xf, xfLen);
                        for (long s = 0; s < spatial; s++)
                        {
                            bool valid = true; long xoff = xBaseC;
                            for (int d = 0; d < sp; d++) { int ip = pos[d] * strides[d] + kpos[d] * dil[d] - pads[d]; if (ip < 0 || ip >= isz[d]) valid = false; xoff += (long)ip * xStr[2 + d]; }
                            span_col[(int)(colBase + s)] = valid ? span_xf[(int)xoff] : 0f;
                            for (int d = sp - 1; d >= 0; d--) { if (++pos[d] < osz[d]) break; pos[d] = 0; }
                        }
                    });
                }
                Span<float> wg;
                if (group == 1) wg = wf;
                else
                {
                    wg = TensorArena.AllocSpan((long)mPerG * K);
                    wf.Slice((int)((long)g * mPerG * K), mPerG * K).CopyTo(wg);
                }
                var prod = (UseGpuMatMul ? GpuMatMul(Tensor.F(wg, mPerG, K), Tensor.F(col, K, (int)spatial))
                                         : MatMul(Tensor.F(wg, mPerG, K), Tensor.F(col, K, (int)spatial))).AsF();
                for (int m = 0; m < mPerG; m++)
                {
                    int oc = g * mPerG + m; long oBase = ((long)nn * M + oc) * spatial, pBase = (long)m * spatial;
                    float bias = !bf.IsEmpty ? bf[oc] : 0f;
                    for (long s = 0; s < spatial; s++) o[(int)(oBase + s)] = prod[(int)(pBase + s)] + bias;
                }
            }
        return Tensor.F(o, outShape);
    }

    // ONNX ConvTranspose (deconv) as scatter-add. X[N,Cin,*isz]; W[Cin,Cout/group,*ksz]; B[Cout].
    static unsafe Tensor ConvTranspose(Tensor[] x, NodeProto n)
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
        long outN = 1; foreach (var d in outShape) outN *= d; var o = TensorArena.AllocSpan(outN);
        var xStr = ContigStrides(X.Shape); var wStr = ContigStrides(W.Shape); var oStr = ContigStrides(outShape);
        long inSpatial = 1; foreach (var s in isz) inSpatial *= s;
        long kcount = 1; foreach (var k in ksh) kcount *= k;
        // parallelize over (batch,group,out-channel) with cinL inner — each out-channel region is written by exactly one task,
        // and the cinL->s->kk accumulation order into each output element is unchanged -> bit-identical, no races.
        int ctTasks = N * group * CoutPerG;
        fixed (float* p_o = o)
        fixed (float* p_xf = xf)
        fixed (float* p_wf = wf)
        {
            float* ptr_o = p_o; float* ptr_xf = p_xf; float* ptr_wf = p_wf;
            int oLen = o.Length; int xfLen = xf.Length; int wfLen = wf.Length;
            System.Threading.Tasks.Parallel.For(0, ctTasks, t =>
            {
                int coutL = t % CoutPerG; int g = (t / CoutPerG) % group; int nn = t / (CoutPerG * group);
                int cout = g * CoutPerG + coutL;
                long oBaseC = (long)nn * oStr[0] + (long)cout * oStr[1];
                var ipos = new int[sp]; var kpos = new int[sp];
                var span_o = new Span<float>(ptr_o, oLen);
                var span_xf = new Span<float>(ptr_xf, xfLen);
                var span_wf = new Span<float>(ptr_wf, wfLen);
                for (int cinL = 0; cinL < CinPerG; cinL++)
                {
                    int cin = g * CinPerG + cinL;
                    long wBase = (long)cin * wStr[0] + (long)coutL * wStr[1];
                    long xBase = (long)nn * xStr[0] + (long)cin * xStr[1];
                    Array.Clear(ipos, 0, sp);
                    for (long s = 0; s < inSpatial; s++)
                    {
                        long xoff = xBase; for (int d = 0; d < sp; d++) xoff += (long)ipos[d] * xStr[2 + d];
                        float val = span_xf[(int)xoff];
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
                                if (valid) span_o[(int)ooff] += val * span_wf[(int)woff];
                                for (int d = sp - 1; d >= 0; d--) { if (++kpos[d] < ksh[d]) break; kpos[d] = 0; }
                            }
                        }
                        for (int d = sp - 1; d >= 0; d--) { if (++ipos[d] < isz[d]) break; ipos[d] = 0; }
                    }
                }
            });
        }
        if (!bf.IsEmpty)
        {
            long spat = 1; foreach (var s in osz) spat *= s;
            for (int nn = 0; nn < N; nn++)
                for (int cout = 0; cout < Cout; cout++)
                { long b = (long)nn * oStr[0] + (long)cout * oStr[1]; for (long s = 0; s < spat; s++) o[(int)(b + s)] += bf[cout]; }
        }
        return Tensor.F(o, outShape);
    }

    static Tensor LayerNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var sf = x[1].AsF(); var bf = (x.Length > 2 && x[2] != null) ? x[2].AsF() : null;
        var xf = X.AsF(); int r = X.Shape.Length; int axis = (int)L(n, "axis", -1); if (axis < 0) axis += r;
        float eps = F(n, "epsilon", 1e-5f);
        long inner = 1; for (int k = axis; k < r; k++) inner *= X.Shape[k];
        long outer = X.Count / inner; var o = TensorArena.AllocSpan(X.Count);
        for (long ob = 0; ob < outer; ob++)
        {
            long baseI = ob * inner;
            double mean = 0; for (long i = 0; i < inner; i++) mean += xf[(int)(baseI + i)]; mean /= inner;
            double var = 0; for (long i = 0; i < inner; i++) { double dd = xf[(int)(baseI + i)] - mean; var += dd * dd; } var /= inner;
            double inv = 1.0 / Math.Sqrt(var + eps);
            for (long i = 0; i < inner; i++)
            { float norm = (float)((xf[(int)(baseI + i)] - mean) * inv); o[(int)(baseI + i)] = norm * sf[(int)(i % sf.Length)] + (!bf.IsEmpty ? bf[(int)(i % bf.Length)] : 0f); }
        }
        return Tensor.F(o, X.Shape);
    }

    static Tensor Softmax(Tensor a, int axis)
    {
        int r = a.Shape.Length; if (axis < 0) axis += r; var d = a.AsF(); var o = TensorArena.AllocSpan(d.Length);
        long inner = 1; for (int k = axis + 1; k < r; k++) inner *= a.Shape[k];
        int A = a.Shape[axis]; long outer = 1; for (int k = 0; k < axis; k++) outer *= a.Shape[k];
        for (long ob = 0; ob < outer; ob++) for (long inr = 0; inr < inner; inr++)
        {
            long baseI = ob * A * inner + inr;
            float mx = float.NegativeInfinity; for (int t = 0; t < A; t++) mx = MathF.Max(mx, d[(int)(baseI + (long)t * inner)]);
            double sum = 0; for (int t = 0; t < A; t++) { double e = Math.Exp(d[(int)(baseI + (long)t * inner)] - mx); o[(int)(baseI + (long)t * inner)] = (float)e; sum += e; }
            for (int t = 0; t < A; t++) o[(int)(baseI + (long)t * inner)] /= (float)sum;
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
            else
            {
                var o = TensorArena.AllocSpan(outSize);
                var df = data.AsF();
                for (long bl = 0; bl < blocks; bl++)
                    df.Slice((int)(bl * inRow + offAxis * innerSize), (int)outRow).CopyTo(o.Slice((int)(bl * outRow), (int)outRow));
                outputs[i] = Tensor.F(o, outShape);
            }
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
        else { var d = a.AsF(); var o = TensorArena.AllocSpan(outN); for (long lin = 0; lin < outN; lin++) { long src = 0; for (int k = 0; k < r; k++) src += (idx[k] % a.Shape[k]) * inStr[k]; o[(int)lin] = d[(int)src]; for (int k = r - 1; k >= 0; k--) { if (++idx[k] < outShape[k]) break; idx[k] = 0; } } return Tensor.F(o, outShape); }
    }

    // ONNX GroupNormalization — normalize over groups of channels + spatial, per-channel affine. [drafted by Antigravity #3, reviewed]
    static Tensor GroupNorm(Tensor[] x, NodeProto n)
    {
        var X = x[0]; var sf = x[1].AsF(); var bf = x[2].AsF(); var xf = X.AsF();
        int r = X.Shape.Length, N = X.Shape[0], C = X.Shape[1];
        long H = 1; for (int k = 2; k < r; k++) H *= X.Shape[k];
        int num = (int)L(n, "num_groups", -1); if (num <= 0) throw new ArgumentException("GroupNormalization: num_groups");
        float eps = F(n, "epsilon", 1e-5f); int G = C / num; long groupElements = (long)G * H;
        var o = TensorArena.AllocSpan(X.Count);
        for (int nn = 0; nn < N; nn++)
            for (int g = 0; g < num; g++)
            {
                double sum = 0;
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; for (long h = 0; h < H; h++) sum += xf[(int)(b + h)]; }
                double mean = sum / groupElements;
                double sumSq = 0;
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; for (long h = 0; h < H; h++) { double diff = xf[(int)(b + h)] - mean; sumSq += diff * diff; } }
                double invStd = 1.0 / Math.Sqrt(sumSq / groupElements + eps);
                for (int c = g * G; c < (g + 1) * G; c++) { long b = ((long)nn * C + c) * H; float sv = sf[c], bv = bf[c]; for (long h = 0; h < H; h++) o[(int)(b + h)] = (float)((xf[(int)(b + h)] - mean) * invStd * sv + bv); }
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
        var o = TensorArena.AllocSpan(X.Count);
        for (int nn = 0; nn < N; nn++)
            for (int c = 0; c < C; c++)
            {
                long b = ((long)nn * C + c) * H;
                double sum = 0; for (long h = 0; h < H; h++) sum += xf[(int)(b + h)];
                double mean = sum / H;
                double sumSq = 0; for (long h = 0; h < H; h++) { double diff = xf[(int)(b + h)] - mean; sumSq += diff * diff; }
                double invStd = 1.0 / Math.Sqrt(sumSq / H + eps);
                float sv = sf[c], bv = bf[c];
                for (long h = 0; h < H; h++) o[(int)(b + h)] = (float)((xf[(int)(b + h)] - mean) * invStd * sv + bv);
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
        for (long lin = 0; lin < d.Length; lin++) { accF[OutIndex()] *= d[(int)lin]; Step(); }
        var o = TensorArena.AllocSpan(outN); for (long i = 0; i < outN; i++) o[(int)i] = (float)accF[i];
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
        var di = isInt ? (Span<float>)default : a.AsF(); var ip = isInt ? a.Ip : null;
        var res = new Tensor[num];
        for (int s = 0; s < num; s++) res[s] = isInt ? Tensor.I(new long[sliceN], oshA) : Tensor.F(TensorArena.AllocSpan(sliceN), oshA);
        var idx = new int[r];
        long len = isInt ? ip.LongLength : di.Length;
        for (long lin = 0; lin < len; lin++)
        {
            int s = idx[axis];
            long oi = 0; int od = 0;
            for (int k = 0; k < r; k++) { if (k == axis) continue; oi += (long)idx[k] * oStr[od]; od++; }
            if (isInt) res[s].Ip[oi] = ip[lin]; else res[s].AsF()[(int)oi] = di[(int)lin];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        return res;
    }

    // StableHLO DYNAMIC_UPDATE_SLICE — (operand, update, start_0..start_{r-1}) -> operand with `update`
    // written at the clamped start position. The KV-cache write (a new token's K/V overlaid into the cache).
    static unsafe Tensor DynamicUpdateSliceOp(Tensor[] x, NodeProto n)
    {
        var operand = x[0]; var update = x[1]; int r = operand.Shape.Length;
        var starts = new int[r];
        // The cache-update composite's DYNAMIC_UPDATE_SLICE passes ALL start indices as ONE 1D tensor in x[2]
        // (e.g. [0,0,pos,0]); the StableHLO/scalar variant passes start_k as separate inputs x[2+k]. Handle both —
        // reading only x[2][0] before zeroed every write to position 0, corrupting the KV cache (the babble).
        long[] startIndices = (x.Length == 3 && x[2] != null && x[2].Count >= r) ? x[2].AsI() : null;
        for (int k = 0; k < r; k++)
        {
            int si = startIndices != null ? (int)startIndices[k]
                   : (x.Length > 2 + k && x[2 + k] != null) ? (int)x[2 + k].AsI()[0] : 0;
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
            if (operand.NativePtr != null)
            {
                var u = update.AsF();
                var dst = operand.AsF();
                for (long lin = 0; lin < u.Length; lin++)
                {
                    long oi = 0; for (int k = 0; k < r; k++) oi += (long)(starts[k] + idx[k]) * oStr[k];
                    dst[(int)oi] = u[(int)lin];
                    for (int k = r - 1; k >= 0; k--) { if (++idx[k] < update.Shape[k]) break; idx[k] = 0; }
                }
                return operand;
            }
            else
            {
                var o = operand.AsF().ToArray(); var u = update.AsF();
                for (long lin = 0; lin < u.Length; lin++)
                {
                    long oi = 0; for (int k = 0; k < r; k++) oi += (long)(starts[k] + idx[k]) * oStr[k];
                    o[(int)oi] = u[(int)lin];
                    for (int k = r - 1; k >= 0; k--) { if (++idx[k] < update.Shape[k]) break; idx[k] = 0; }
                }
                return Tensor.F(o, operand.Shape);
            }
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
        var of = isInt ? (Span<float>)default : TensorArena.AllocSpan(outN); var oiArr = isInt ? new long[outN] : null;
        for (int s = 0; s < num; s++)
        {
            var t = x[s];
            // robust to mixed payloads: a shape-vector pack can mix an int const with a float-promoted dim
            // (Max/Sub/Add on int dims widen to float) — convert per-input, never read a null raw payload.
            var td = isInt ? (Span<float>)default : t.AsF(); var ti = isInt ? t.AsI() : null;
            var idx = new int[r];
            long len = isInt ? ti.LongLength : td.Length;
            for (long lin = 0; lin < len; lin++)
            {
                long pos = 0; int id = 0;
                for (int k = 0; k <= r; k++) { int c = (k == axis) ? s : idx[id++]; pos += (long)c * oStr[k]; }
                if (isInt) oiArr[pos] = ti[lin]; else of[(int)pos] = td[(int)lin];
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
        long outN = 1; foreach (var d in outDim) outN *= d; var o = TensorArena.AllocSpan(outN);
        var idx = new int[r];

        if (mode == "nearest")
        {
            var map = new int[r][];
            for (int d = 0; d < r; d++) { map[d] = new int[outDim[d]]; for (int oc = 0; oc < outDim[d]; oc++) map[d][oc] = Math.Clamp(NearestIdx(SrcCoord(d, oc), nm), 0, X.Shape[d] - 1); }
            for (long lin = 0; lin < outN; lin++)
            {
                long si = 0; for (int d = 0; d < r; d++) si += (long)map[d][idx[d]] * inStr[d];
                o[(int)lin] = xf[(int)si];
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
                    if (!skip) acc += wgt * xf[(int)si];
                }
                o[(int)lin] = (float)acc;
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
    static unsafe Tensor Stft(Tensor[] x, NodeProto n)
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
        var win = window != null ? window.AsF() : default;
        int numFrames = (sigLen - frameLength) / frameStep + 1;
        int bins = onesided ? frameLength / 2 + 1 : frameLength;

        // precompute the cos/sin DFT basis (bins x frameLength)
        var cs = new double[bins * frameLength]; var sn = new double[bins * frameLength];
        for (int k = 0; k < bins; k++)
            for (int m = 0; m < frameLength; m++)
            { double ang = -2.0 * Math.PI * k * m / frameLength; cs[k * frameLength + m] = Math.Cos(ang); sn[k * frameLength + m] = Math.Sin(ang); }

        var o = TensorArena.AllocSpan((long)batch * numFrames * bins * 2);
        fixed (float* p_o = o)
        fixed (float* p_s = s)
        fixed (float* p_win = win)
        {
            float* ptr_o = p_o; float* ptr_s = p_s; float* ptr_win = p_win;
            int oLen = o.Length; int sLen = s.Length; int winLen = win.Length;
            System.Threading.Tasks.Parallel.For(0, batch * numFrames, bfi =>
            {
                int b = bfi / numFrames, f = bfi % numFrames;
                int start = f * frameStep; long sBase = (long)b * sigLen + start;
                var span_o = new Span<float>(ptr_o, oLen);
                var span_s = new Span<float>(ptr_s, sLen);
                var span_win = new Span<float>(ptr_win, winLen);
                bool hasWin = !span_win.IsEmpty;
                for (int k = 0; k < bins; k++)
                {
                    double re = 0, im = 0; int kb = k * frameLength;
                    for (int m = 0; m < frameLength; m++)
                    {
                        double xv = span_s[(int)(sBase + m)];
                        if (hasWin) xv *= span_win[m];
                        re += xv * cs[kb + m]; im += xv * sn[kb + m];
                    }
                    long ob = (((long)b * numFrames + f) * bins + k) * 2;
                    span_o[(int)ob] = (float)re; span_o[(int)(ob + 1)] = (float)im;
                }
            });
        }
        return Tensor.F(o, new[] { batch, numFrames, bins, 2 });
    }

    // ONNX NonZero. out[rank, K] = the row-major coordinates of the K nonzero elements.
    static Tensor NonZero(Tensor a)
    {
        int r = a.Shape.Length; long total = a.Count; bool isInt = a.IsInt;
        var coords = new List<int[]>(); var idx = new int[r];
        var af = isInt ? (Span<float>)default : a.AsF();
        for (long lin = 0; lin < total; lin++)
        {
            if (isInt ? a.Ip[lin] != 0 : af[(int)lin] != 0) coords.Add((int[])idx.Clone());
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
        var of = isInt ? (Span<float>)default : TensorArena.AllocSpan(data.AsF().Length); if (!isInt) data.AsF().CopyTo(of);
        var oi = isInt ? (long[])data.AsI().Clone() : null;
        var uf = isInt ? (Span<float>)default : updates.AsF(); var ui = isInt ? updates.AsI() : null;
        for (long u = 0; u < numUpdates; u++)
        {
            long baseOff = 0;
            for (int c = 0; c < q; c++) { long ic = ix[u * q + c]; if (ic < 0) ic += data.Shape[c]; baseOff += ic * dataStr[c]; }
            long uBase = u * sliceSize;
            for (long sIdx = 0; sIdx < sliceSize; sIdx++)
            {
                long dpos = baseOff + sIdx;
                if (isInt) { long v = ui[uBase + sIdx]; oi[dpos] = red == "add" ? oi[dpos] + v : red == "mul" ? oi[dpos] * v : red == "max" ? Math.Max(oi[dpos], v) : red == "min" ? Math.Min(oi[dpos], v) : v; }
                else { float v = uf[(int)(uBase + sIdx)]; of[(int)dpos] = red == "add" ? of[(int)dpos] + v : red == "mul" ? of[(int)dpos] * v : red == "max" ? MathF.Max(of[(int)dpos], v) : red == "min" ? MathF.Min(of[(int)dpos], v) : v; }
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

    public static unsafe Tensor FromProto(TensorProto t, Owner owner = null)
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
            // UINT8/INT8 weight bytes, un-widened - block-q4 ops (MatMulNBits / GatherBlockQuantized) read
            // the nibbles straight from here. VOM-region-backed (CRQ164) when an owner is supplied - the
            // packed q4 embed/lm-head tables run into the hundreds of MB, off the GC entirely; falls back
            // to a GC array only for the owner-less call sites (Constant/attribute folding, small tensors).
            case 2: case 3:
            {
                int byteLen = t.RawData.Span.Length;
                if (owner != null)
                {
                    string? nm = string.IsNullOrEmpty(t.Name) ? null : t.Name;
                    // Android: back the weight bytes with an AHardwareBuffer blob so GpuVulkan can import the
                    // SAME memory (zero-copy residency on UMA). Fault-degrades to a plain VOM region (inv-9):
                    // AHB alloc can fail under fragmentation/OOM, and the CPU rung (ReadRawb over Resource)
                    // is byte-identical either way. Windows always takes the plain path.
                    DpxTensor dt; nint ahb = 0;
                    try
                    {
                        if (OperatingSystem.IsAndroid())
                            dt = DpxTensor.AllocBlobAhb(owner, dims, byteLen, out ahb, name: nm);
                        else
                            dt = DpxTensor.Alloc(owner, dims, VomFormat.Bytes, subdir: "Weights", name: nm);
                    }
                    catch (Exception ex)
                    {
                        Subsystem.Dg.Log("dpx", $"AHB weight alloc failed, CPU rung: {ex.Message}");
                        dt = DpxTensor.Alloc(owner, dims, VomFormat.Bytes, subdir: "Weights", name: nm);
                        ahb = 0;
                    }
                    t.RawData.Span.CopyTo(dt.ReadBytes());
                    var rt = new Tensor { Shape = dims };
                    rt.SetNativeRawb((byte*)dt.Data.Resource, byteLen);
                    if (ahb != 0) rt.SetAhbWeight(ahb);
                    return rt;
                }
                return new Tensor { Rawb = t.RawData.Span.ToArray(), Shape = dims };
            }
            default: throw new NotImplementedException($"initializer dtype {t.DataType} ({t.Name})");
        }
    }
    static T[] Cast<T>(ReadOnlySpan<byte> b, int n) where T : struct
    { var o = new T[n]; MemoryMarshal.Cast<byte, T>(b).Slice(0, n).CopyTo(o); return o; }

    // A weight stays packed iff it has a sibling _scale AND its bytes:elements ratio is a sub-byte/byte quant
    // width (2/4/8 bit). That self-validating check sidesteps the tflite-vs-ONNX DataType-code collision
    // (tflite INT8=9 == ONNX BOOL=9) — we never trust the code, only the geometry.
    static bool IsPackedQuant(TensorProto w, TensorProto scale)
    {
        if (scale == null) return false;
        long n = 1; foreach (var d in w.Dims) n *= d; if (n <= 0) return false;
        long bits = (long)w.RawData.Span.Length * 8 / n;
        return bits == 2 || bits == 4 || bits == 8;
    }
    static float[] ReadFloats(TensorProto t)
        => t.FloatData.Count > 0 ? t.FloatData.ToArray() : MemoryMarshal.Cast<byte, float>(t.RawData.Span).ToArray();
    static float[] ReadZero(TensorProto t)
        => t == null ? null
         : t.Int64Data.Count > 0 ? Array.ConvertAll(t.Int64Data.ToArray(), x => (float)x)
         : t.RawData.Span.Length > 0 ? Array.ConvertAll(MemoryMarshal.Cast<byte, long>(t.RawData.Span).ToArray(), x => (float)x)
         : null;
    // Build a packed quant Tensor: keep the bytes verbatim, derive the bit width from geometry, attach per-row
    // scale/zp, and set Qaxis = the axis whose dim matches the scale length (axis 0 for gemma's per-out-channel).
    static Tensor MakeQuant(TensorProto w, TensorProto scale, TensorProto zp)
    {
        var dims = w.Dims.Select(d => (int)d).ToArray();
        long n = 1; foreach (var d in dims) n *= d;
        int bits = (int)((long)w.RawData.Span.Length * 8 / n);
        var sc = ReadFloats(scale);
        int axis = 0; for (int a = 0; a < dims.Length; a++) if (dims[a] == sc.Length) { axis = a; break; }
        return new Tensor { Qb = w.RawData.Span.ToArray(), Qbits = bits, Qscale = sc, Qzero = ReadZero(zp), Qaxis = axis, Shape = dims };
    }
}

// the GPU MOUNT seam: dpx (C#) -> PURE-C# D3D12 (GpuD3D12.cs) or Vulkan (GpuVulkan.cs). NO dpgpu.dll/C++/MSVC.
// DPGPU_BACKEND=vulkan flips to the cross-platform spine (vulkan-1.dll + gemm.spv) -> Android (Adreno/Mali);
// default is D3D12 (reuses the caller's gemm DXIL). Both reproduce the CPU GEMM bit-for-bit.
static class Gpu
{
    // D3D12 doesn't exist off Windows, so Android always carries the GPU path over Vulkan (Adreno/Mali) -
    // DPGPU_BACKEND can still force vulkan on Windows for the cross-platform-parity receipt.
    static readonly bool s_vk = OperatingSystem.IsAndroid()
        || string.Equals(Environment.GetEnvironmentVariable("DPGPU_BACKEND"), "vulkan", StringComparison.OrdinalIgnoreCase);
    // Shader blobs ship next to ss.exe as loose files on Windows; the APK has no such filesystem path -
    // an Android-only bootstrap (MainActivity.cs, which alone references Android.Content.Res.AssetManager)
    // swaps this to read from the AndroidAsset package. Neither this file nor GpuVulkan.cs may reference
    // Android.* directly - both compile into the Windows head too, which has no binding to that assembly.
    public static Func<string, byte[]> ShaderAssetReader =
        name => System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, name));
    static byte[] s_spv;
    public static int dpgpu_gemm(float[] A, float[] B, float[] C, uint M, uint N, uint K, byte[] dxil, uint dxilLen, int threadM = 16, int threadN = 16)
    {
        if (s_vk)
        {
            s_spv ??= ShaderAssetReader("gemm.spv");
            return GpuVulkan.Gemm(A, B, C, M, N, K, s_spv);
        }
        return GpuD3D12.Gemm(A, B, C, M, N, K, dxil, (int)dxilLen, threadM, threadN);
    }
    static byte[] s_spvQ4;
    static byte[] s_spvQ4Gemv;
    // Variant-rung reader: an absent .spv is ABSENCE (the naive rung stands), not an error — returns the
    // Array.Empty sentinel so ??= probes the asset once, never per call.
    static byte[] ReadSpvOrEmpty(string name)
    {
        try { return ShaderAssetReader(name) ?? Array.Empty<byte>(); }
        catch (Exception ex) { Console.Error.WriteLine($"spv variant '{name}' absent ({ex.GetType().Name}); naive rung stands."); return Array.Empty<byte>(); }
    }
    // q4 seam: A/Scales fp32, Bq/Zp are the packed uint8 SEQUENTIAL-nibble buffers straight off the model (never
    // dequantized to fp32 on the CPU side). dxil/spv resolved by the caller (mirrors dpgpu_gemm's dxil plumbing).
    // weightKey: stable per-weight-tensor cache token (Dpx.GpuWeightKey) so GpuD3D12 can keep Bq/scales/zp resident
    // in a default-heap buffer across calls instead of re-uploading 33MB+ of weight bytes every single GEMM.
    // key<=0 disables residency (old per-call upload behavior) - Vulkan backend doesn't take the key yet.
    // tileN/tileM: output elements per threadgroup of the supplied dxil (dispatch divisors — 16x16 naive/tiled,
    // 8x1 GEMV). D3D12-only; the Vulkan backend still carries the naive gemm_q4.spv with its own fixed geometry.
    public static int dpgpu_gemm_q4(float[] A, byte[] Bq, float[] scales, byte[] zp, float[] C, uint M, uint N, uint K, uint blockSize, bool hasZp, byte[] dxil, long weightKey = -1, int tileN = 16, int tileM = 16, Tensor bWeight = null)
    {
        if (s_vk)
        {
            s_spvQ4 ??= ShaderAssetReader("gemm_q4.spv");
            // The GEMV variant rung (M==1 decode). Prefer the fp16-arithmetic kernel (mediump/RelaxedPrecision
            // — the Adreno runs it ~2x); fall back to the fp32 GEMV, then naive. Array.Empty sentinel = probe once.
            if (s_spvQ4Gemv == null)
            {
                var f16 = ReadSpvOrEmpty("gemm_q4_gemv_f16.spv");
                s_spvQ4Gemv = f16.Length > 0 ? f16 : ReadSpvOrEmpty("gemm_q4_gemv.spv");
            }
            // bWeight carries the AHB handle (Android zero-copy residency); the D3D12 rung has its own
            // weightKey-keyed residency and ignores it.
            return GpuVulkan.GemmQ4(A, Bq, scales, zp, C, M, N, K, blockSize, hasZp, s_spvQ4, bWeight,
                s_spvQ4Gemv.Length > 0 ? s_spvQ4Gemv : null);
        }
        return GpuD3D12.GemmQ4(A, Bq, scales, zp, C, M, N, K, blockSize, hasZp, dxil, dxil.Length, weightKey, tileN, tileM);
    }
    public static string DeviceName() { if (s_vk) { GpuVulkan.EnsureInit(); return GpuVulkan.Name; } GpuD3D12.EnsureInit(); return GpuD3D12.Name; }
    // Residency probe for the q4 weight cache: true only when the D3D12 backend already holds this
    // weightKey's Bq/scales/zp in a DEFAULT-heap buffer (a GemmQ4 cache hit ignores those arrays, so
    // the caller can skip materializing them). The Vulkan backend re-reads the managed arrays every
    // call and always reports false - callers keep passing real copies there.
    public static bool QueryResidentQ4(long weightKey) => !s_vk && GpuD3D12.QueryResidentQ4(weightKey);
}
