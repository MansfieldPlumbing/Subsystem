// dp-onnx — a native .NET ONNX interpreter over Onnx.dll (protobuf), NO onnxruntime.
//
// "ONNX is protobuf": Onnx.dll already decomposes a .onnx into walkable objects.
// This walks graph.Node in topological order over a Dictionary<string,Tensor>,
// dispatching each OpType to a hand-rolled kernel (math decomposed from ggml).
//
//   dp-onnx selftest                 build a tiny graph in-proc, run, verify
//   dp-onnx probe   <model.onnx>     run with zero inputs until an unimplemented op; report op coverage
//   dp-onnx run     <model.onnx> [--inputs <dir>] [--out <wav>]
//                                    --inputs: load real tensors (<name>.bin) + run to completion;
//                                    diff against oracle.bin if present. No --inputs = legacy zero-feed.
//
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Data.Sqlite;
using Onnx;

return args.Length == 0 ? Usage()
     : args[0] == "selftest" ? SelfTest()
     : args[0] == "probe" ? Probe(args[1])
     : args[0] == "run" ? Run(args)
     : args[0] == "specdiff" ? SpecDiff(args)
     : args[0] == "stream" ? Stream(args)
     : args[0] == "fold" ? Fold(args)
     : args[0] == "addoutput" ? AddOutput(args)
     : args[0] == "addoutput-all" ? AddOutputAll(args)
     : args[0] == "nodeinfo" ? NodeInfo(args)
     : args[0] == "emit" ? Emit(args)
     : args[0] == "run-compiled" ? RunCompiled(args)
     : args[0] == "gpu-test" ? GpuTest(args)
     : args[0] == "gpu-bench" ? GpuBench(args)
     : args[0] == "db" ? ToDb(args)
     : Usage();

// surgery: expose EVERY node output as a graph output (no type -> ORT infers) for a full divergence map
static int AddOutputAll(string[] args)
{
    var m = ModelProto.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    var have = new HashSet<string>(m.Graph.Output.Select(o => o.Name));
    int added = 0;
    foreach (var nd in m.Graph.Node)
        foreach (var on in nd.Output)
            if (!string.IsNullOrEmpty(on) && have.Add(on)) { m.Graph.Output.Add(new ValueInfoProto { Name = on }); added++; }
    File.WriteAllBytes(args[2], m.ToByteArray());
    Console.WriteLine($"wrote {args[2]}  (+{added} node outputs exposed)");
    return 0;
}

// inspect node(s) whose name contains a substring: optype, attributes, inputs (+initializer shapes), outputs
static int NodeInfo(string[] args)
{
    var m = ModelProto.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    var init = m.Graph.Initializer.ToDictionary(i => i.Name, i => $"[{string.Join(",", i.Dims)}]");
    foreach (var nd in m.Graph.Node)
    {
        if (args.Length > 2 && !nd.Name.Contains(args[2])) continue;
        Console.WriteLine($"{nd.OpType}  {nd.Name}");
        foreach (var a in nd.Attribute)
        {
            string v = a.Type switch
            {
                AttributeProto.Types.AttributeType.Ints => string.Join(",", a.Ints),
                AttributeProto.Types.AttributeType.Int => a.I.ToString(),
                AttributeProto.Types.AttributeType.Float => a.F.ToString(),
                AttributeProto.Types.AttributeType.String => a.S.ToStringUtf8(),
                AttributeProto.Types.AttributeType.Floats => string.Join(",", a.Floats),
                _ => $"<{a.Type}>"
            };
            Console.WriteLine($"    @{a.Name} = {v}");
        }
        for (int k = 0; k < nd.Input.Count; k++) Console.WriteLine($"    in[{k}] {nd.Input[k]}" + (init.TryGetValue(nd.Input[k], out var s) ? $"  init{s}" : ""));
        foreach (var o in nd.Output) Console.WriteLine($"    out {o}");
    }
    return 0;
}

// db: compile the ONNX graph into a queryable SQLite model store (the 6-table schema). The graph becomes
// rows — op triage ("which ops can't run on a backend?") is a SELECT, and per-op backend routing is the
// `backend` column. Weights are raw-byte BLOBs (dtype + dims carried). The model becomes a Cm-projectable
// capability instead of an opaque protobuf blob (CRQ143).
static int ToDb(string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: dp-onnx db <model.onnx> <out.db>"); return 1; }
    var g = ModelProto.Parser.ParseFrom(File.ReadAllBytes(args[1])).Graph;
    var dbPath = args[2];
    if (File.Exists(dbPath)) File.Delete(dbPath);
    using var c = new SqliteConnection($"Data Source={dbPath}");
    c.Open();
    using (var ddl = c.CreateCommand())
    {
        ddl.CommandText = @"
CREATE TABLE graph_io(kind TEXT, name TEXT, elem_type INTEGER, shape TEXT);
CREATE TABLE node(id INTEGER PRIMARY KEY, ord INTEGER, op_type TEXT, name TEXT, backend TEXT);
CREATE TABLE node_io(node_id INTEGER, slot INTEGER, kind TEXT, value_name TEXT);
CREATE TABLE node_attr(node_id INTEGER, name TEXT, type INTEGER, i INTEGER, f REAL, s TEXT, ints TEXT, floats TEXT, tensor_id INTEGER);
CREATE TABLE tensor(id INTEGER PRIMARY KEY, name TEXT, dtype INTEGER, dims TEXT, data BLOB);";
        ddl.ExecuteNonQuery();
    }
    using var tx = c.BeginTransaction();
    SqliteCommand P(string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }
    int tid = 0;
    int WriteTensor(TensorProto t)
    {
        int id = tid++;
        byte[] data = t.RawData != null && t.RawData.Length > 0 ? t.RawData.ToByteArray() : TypedBytes(t);
        using var cmd = P("INSERT INTO tensor(id,name,dtype,dims,data) VALUES($id,$n,$dt,$dm,$d)",
            ("$id", id), ("$n", t.Name ?? ""), ("$dt", t.DataType), ("$dm", string.Join(",", t.Dims)), ("$d", data));
        cmd.ExecuteNonQuery();
        return id;
    }
    foreach (var t in g.Initializer) WriteTensor(t);
    foreach (var vi in g.Input)  using (var cmd = P("INSERT INTO graph_io(kind,name,elem_type,shape) VALUES('in',$n,$e,$s)",  ("$n", vi.Name ?? ""), ("$e", vi.Type?.TensorType?.ElemType ?? 0), ("$s", ShapeStr(vi)))) cmd.ExecuteNonQuery();
    foreach (var vi in g.Output) using (var cmd = P("INSERT INTO graph_io(kind,name,elem_type,shape) VALUES('out',$n,$e,$s)", ("$n", vi.Name ?? ""), ("$e", vi.Type?.TensorType?.ElemType ?? 0), ("$s", ShapeStr(vi)))) cmd.ExecuteNonQuery();
    int nid = 0;
    foreach (var nd in g.Node)
    {
        int id = nid++;
        using (var cmd = P("INSERT INTO node(id,ord,op_type,name,backend) VALUES($id,$o,$op,$n,NULL)", ("$id", id), ("$o", id), ("$op", nd.OpType ?? ""), ("$n", nd.Name ?? ""))) cmd.ExecuteNonQuery();
        for (int k = 0; k < nd.Input.Count; k++)  using (var cmd = P("INSERT INTO node_io VALUES($id,$s,'in',$v)",  ("$id", id), ("$s", k), ("$v", nd.Input[k] ?? ""))) cmd.ExecuteNonQuery();
        for (int k = 0; k < nd.Output.Count; k++) using (var cmd = P("INSERT INTO node_io VALUES($id,$s,'out',$v)", ("$id", id), ("$s", k), ("$v", nd.Output[k] ?? ""))) cmd.ExecuteNonQuery();
        foreach (var a in nd.Attribute)
        {
            object? aTid = a.T != null ? WriteTensor(a.T) : null;
            using var cmd = P("INSERT INTO node_attr(node_id,name,type,i,f,s,ints,floats,tensor_id) VALUES($id,$n,$t,$i,$f,$s,$ii,$ff,$tt)",
                ("$id", id), ("$n", a.Name ?? ""), ("$t", (int)a.Type), ("$i", a.I), ("$f", a.F),
                ("$s", a.S != null ? a.S.ToStringUtf8() : null), ("$ii", string.Join(",", a.Ints)), ("$ff", string.Join(",", a.Floats)), ("$tt", aTid));
            cmd.ExecuteNonQuery();
        }
    }
    tx.Commit();
    Console.WriteLine($"wrote {dbPath}  nodes={nid} tensors={tid} inputs={g.Input.Count} outputs={g.Output.Count}");
    return 0;
}

static byte[] TypedBytes(TensorProto t)
{
    if (t.FloatData.Count > 0)  { var a = t.FloatData.ToArray();  var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    if (t.Int64Data.Count > 0)  { var a = t.Int64Data.ToArray();  var b = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    if (t.Int32Data.Count > 0)  { var a = t.Int32Data.ToArray();  var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    if (t.DoubleData.Count > 0) { var a = t.DoubleData.ToArray(); var b = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    return Array.Empty<byte>();
}

static string ShapeStr(ValueInfoProto vi)
{
    var dim = vi.Type?.TensorType?.Shape?.Dim;
    if (dim == null) return "";
    return string.Join(",", dim.Select(d => !string.IsNullOrEmpty(d.DimParam) ? d.DimParam : d.DimValue.ToString()));
}

static int Usage() { Console.WriteLine("usage: dp-onnx selftest | probe <model.onnx> | run <model.onnx> [--inputs <dir>] [--out <wav>] | db <model.onnx> <out.db> | addoutput <in> <out> <tensorName...> | emit <model.onnx> <out.cs>"); return 1; }

// compile front-half (#69 / shared with the #92 D3D12 frame-graph): walk the ONNX graph and emit a
// straight-line C# Tier-1 forward pass. Design (fixes the 5 blockers in the H1 draft):
//  - calls Interp.Dispatch per node  -> covers all 53 ops for free (no partial per-op switch);
//  - binds ALL node outputs          -> multi-output ops (LSTM x3, Split xN) work;
//  - bakes each node by base64'ing its NodeProto -> robust attrs incl. tensor attrs (Constant), no per-type code;
//  - weights come from an Init(weights) dict, NOT inlined C# literals -> no multi-GB .cs (82M params).
// Roslyn compile + a sidecar weight loader + run-compiled parity = the next slice.
static int Emit(string[] args)
{
    if (args.Length < 3) return Usage();
    var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    var g = model.Graph;
    var inits = new HashSet<string>(g.Initializer.Select(i => i.Name));
    var nodes = g.Node;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("// AUTO-EMITTED by `dp-onnx emit` — Tier-1 straight-line forward pass (calls Interp.Dispatch).");
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using Onnx;");
    sb.AppendLine("namespace DpOnnx.Compiled {");
    sb.AppendLine("  public static class ModelInstance {");
    sb.AppendLine("    static Dictionary<string,Tensor> W = new();");
    sb.AppendLine("    public static void Init(Dictionary<string,Tensor> weights) { W = weights; }");
    sb.AppendLine("    static Tensor G(Dictionary<string,Tensor> e, string n) => e.TryGetValue(n, out var t) ? t : W[n];");
    for (int i = 0; i < nodes.Count; i++)
        sb.AppendLine($"    static readonly NodeProto n{i} = NodeProto.Parser.ParseFrom(Convert.FromBase64String({Q(Convert.ToBase64String(nodes[i].ToByteArray()))}));");
    sb.AppendLine("    public static Dictionary<string,Tensor> Forward(Dictionary<string,Tensor> feed) {");
    sb.AppendLine("      var e = new Dictionary<string,Tensor>();");
    foreach (var vi in g.Input) if (!inits.Contains(vi.Name)) sb.AppendLine($"      e[{Q(vi.Name)}] = feed[{Q(vi.Name)}];");
    for (int i = 0; i < nodes.Count; i++)
    {
        var nd = nodes[i];
        string ins = string.Join(", ", nd.Input.Select(x => string.IsNullOrEmpty(x) ? "null" : $"G(e,{Q(x)})"));
        sb.AppendLine($"      {{ var o = Interp.Dispatch(n{i}, new Tensor[]{{ {ins} }});");
        for (int k = 0; k < nd.Output.Count; k++) if (!string.IsNullOrEmpty(nd.Output[k])) sb.AppendLine($"        e[{Q(nd.Output[k])}] = o[{k}];");
        sb.AppendLine("      }");
    }
    sb.AppendLine("      var outs = new Dictionary<string,Tensor>();");
    foreach (var o in g.Output) sb.AppendLine($"      outs[{Q(o.Name)}] = e[{Q(o.Name)}];");
    sb.AppendLine("      return outs;");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    File.WriteAllText(args[2], sb.ToString());
    Console.WriteLine($"emitted {nodes.Count} nodes, {g.Initializer.Count} weights, {g.Output.Count} outputs -> {args[2]} ({sb.Length:N0} chars)");
    return 0;
}

static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

// gpu-test [dxil]: dispatch a MatMul to the D3D12 GPU through dpgpu.dll and diff vs the CPU kernel (the mount proof).
static int GpuTest(string[] args)
{
    int M = 64, K = 48, N = 32; var rnd = new Random(1);
    var A = new float[M * K]; for (int i = 0; i < A.Length; i++) A[i] = (float)(rnd.NextDouble() * 2 - 1);
    var B = new float[K * N]; for (int i = 0; i < B.Length; i++) B[i] = (float)(rnd.NextDouble() * 2 - 1);
    var C = new float[M * N];
    string dxilPath = args.Length > 1 ? args[1] : @"S:\qnn-project\workspace\onnx-interp\_gpu\gemm.dxil";
    byte[] dxil = File.ReadAllBytes(dxilPath);
    int rc = Gpu.dpgpu_gemm(A, B, C, (uint)M, (uint)N, (uint)K, dxil, (uint)dxil.Length);
    if (rc != 0) { Console.WriteLine($"dpgpu_gemm failed rc={rc}"); return 1; }
    var cpu = Interp.Dispatch(new NodeProto { OpType = "MatMul" }, new[] { Tensor.F(A, M, K), Tensor.F(B, K, N) })[0].Fp;
    double maxd = 0; for (int i = 0; i < C.Length; i++) maxd = Math.Max(maxd, Math.Abs(C[i] - cpu[i]));
    Console.WriteLine($"dp-onnx -> GPU dpgpu_gemm [{M}x{K}]@[{K}x{N}]  vs CPU Interp.MatMul:  max|diff|={maxd:E3}  =>  {(maxd < 1e-3 ? "MATCH — dp-onnx dispatched a MatMul to the D3D12 GPU; the mount works" : "MISMATCH")}");
    return maxd < 1e-3 ? 0 : 2;
}

// gpu-bench [S]: time an SxSxS GEMM on GPU (naive kernel, incl per-call device-create) vs CPU (16-thread, double-acc).
static int GpuBench(string[] args)
{
    int S = args.Length > 1 ? int.Parse(args[1]) : 512;
    byte[] dxil = File.ReadAllBytes(@"S:\qnn-project\workspace\onnx-interp\_gpu\gemm.dxil");
    var rnd = new Random(1);
    var A = new float[S * S]; for (int i = 0; i < A.Length; i++) A[i] = (float)(rnd.NextDouble() * 2 - 1);
    var B = new float[S * S]; for (int i = 0; i < B.Length; i++) B[i] = (float)(rnd.NextDouble() * 2 - 1);
    var C = new float[S * S];
    double gflop = 2.0 * S * S * S / 1e9;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Gpu.dpgpu_gemm(A, B, C, (uint)S, (uint)S, (uint)S, dxil, (uint)dxil.Length);   // warmup: pays the one-time device init
    sw.Stop(); double init = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"  device: {Gpu.DeviceName()}");
    double gpu = 1e9;
    for (int r = 0; r < 5; r++) { sw.Restart(); Gpu.dpgpu_gemm(A, B, C, (uint)S, (uint)S, (uint)S, dxil, (uint)dxil.Length); sw.Stop(); gpu = Math.Min(gpu, sw.Elapsed.TotalSeconds); }
    sw.Restart(); var cpu = Interp.Dispatch(new NodeProto { OpType = "MatMul" }, new[] { Tensor.F(A, S, S), Tensor.F(B, S, S) })[0].Fp; sw.Stop(); double cpus = sw.Elapsed.TotalSeconds;
    double maxd = 0; for (int i = 0; i < C.Length; i++) maxd = Math.Max(maxd, Math.Abs(C[i] - cpu[i]));
    Console.WriteLine($"GEMM {S}x{S}x{S}  ({gflop:F2} GFLOP)   max|diff|={maxd:E2}");
    Console.WriteLine($"  one-time device init (first call) : {init * 1000,8:F1} ms");
    Console.WriteLine($"  GPU naive, persistent (best of 5) : {gpu * 1000,8:F1} ms   {gflop / gpu,7:F1} GFLOP/s");
    Console.WriteLine($"  CPU 16-thread double-acc          : {cpus * 1000,8:F1} ms   {gflop / cpus,7:F1} GFLOP/s");
    Console.WriteLine($"  GPU vs CPU (steady-state): {cpus / gpu,5:F1}x   (naive untiled kernel — tiled GEMM is the next multiplier)");
    return 0;
}

// run-compiled <model_dll> <model.onnx> --inputs <dir> [--out wav]: reflection-load the emitted ModelInstance,
// inject weights (the model's initializers — the sidecar loader is a later optimization), run Forward, validate.
// Tier-1 reuses Interp's kernels, so this MUST match `run` bit-for-bit — the parity proof for the compile path.
static int RunCompiled(string[] args)
{
    if (args.Length < 3) return Usage();
    string dll = args[1], onnx = args[2], inputsDir = null, outPath = null;
    for (int i = 3; i < args.Length; i++) switch (args[i]) { case "--inputs": inputsDir = args[++i]; break; case "--out": outPath = args[++i]; break; }
    var g = ModelProto.Parser.ParseFrom(File.ReadAllBytes(onnx)).Graph;
    var W = new Dictionary<string, Tensor>();
    foreach (var init in g.Initializer) W[init.Name] = Interp.FromProto(init);
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input) { if (g.Initializer.Any(i => i.Name == vi.Name)) continue; feed[vi.Name] = LoadBin(Path.Combine(inputsDir, vi.Name + ".bin")); }

    var asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(dll));
    var t = asm.GetType("DpOnnx.Compiled.ModelInstance") ?? throw new Exception("ModelInstance type not found in " + dll);
    t.GetMethod("Init").Invoke(null, new object[] { W });
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var outs = (Dictionary<string, Tensor>)t.GetMethod("Forward").Invoke(null, new object[] { feed });
    sw.Stop();
    var y = outs.Values.First(); var wav = y.AsF();
    Console.WriteLine($"COMPILED ran ALL {g.Node.Count} nodes ✓  ({sw.Elapsed.TotalSeconds:F2}s)  [{string.Join(",", y.Shape)}]  samples={wav.Length}  rms={Rms(wav):F5}  peak={Peak(wav):F5}");
    if (outPath != null) { WriteWav(outPath, wav, 24000); Console.WriteLine($"wrote {outPath}"); }
    string oracle = Path.Combine(inputsDir, "oracle.bin");
    if (File.Exists(oracle)) { var o = LoadBin(oracle).AsF(); int n = Math.Min(o.Length, wav.Length); double md = 0, ss = 0; for (int i = 0; i < n; i++) { double d = Math.Abs(wav[i] - o[i]); md = Math.Max(md, d); ss += d * d; } Console.WriteLine($"vs ORT oracle.bin: len {wav.Length}/{o.Length}  max|diff|={md:E3}  rmse={Math.Sqrt(ss / n):E3}  (interpreter gives rmse 2.516E-002 — compiled must match)"); }
    return 0;
}

// surgery: append internal tensors as graph outputs so ORT can fetch them (debug oracle for any node).
// each arg may be a NODE name (resolved to its first output tensor) or an output-tensor name directly.
static int AddOutput(string[] args)
{
    var m = ModelProto.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    for (int i = 3; i < args.Length; i++)
    {
        var node = m.Graph.Node.FirstOrDefault(nd => nd.Name == args[i]);
        string tensor = node != null ? node.Output[0] : args[i];
        m.Graph.Output.Add(new ValueInfoProto { Name = tensor, Type = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = 1 } } });
        Console.WriteLine($"  +output tensor '{tensor}'" + (node != null ? $"  (from node {args[i]})" : ""));
    }
    File.WriteAllBytes(args[2], m.ToByteArray());
    Console.WriteLine($"wrote {args[2]}");
    return 0;
}

// ---------------------------------------------------------------------------
static int SelfTest()
{
    // Y = relu(X @ W + B);  X[2,3], W[3,2], B[2]
    var g = new GraphProto { Name = "t" };
    g.Input.Add(ValueInfo("X"));
    g.Initializer.Add(FloatInit("W", new[] { 1f, 0, 0, 1, 1, 1 }, 3, 2));   // [[1,0],[0,1],[1,1]]
    g.Initializer.Add(FloatInit("B", new[] { 0.5f, -10f }, 2));
    g.Node.Add(Node("MatMul", new[] { "X", "W" }, new[] { "M" }));
    g.Node.Add(Node("Add", new[] { "M", "B" }, new[] { "S" }));
    g.Node.Add(Node("Relu", new[] { "S" }, new[] { "Y" }));
    g.Output.Add(ValueInfo("Y"));
    var model = new ModelProto { Graph = g };

    var X = Tensor.F(new[] { 1f, 2, 3, 4, 5, 6 }, 2, 3);          // [[1,2,3],[4,5,6]]
    var outs = new Interp(model).Run(new() { ["X"] = X });
    var Y = outs["Y"];
    // expected: X@W = [[1+3,2+3],[4+6,5+6]] = [[4,5],[10,11]]; +B = [[4.5,-5],[10.5,1]]; relu => [[4.5,0],[10.5,1]]
    var exp = new[] { 4.5f, 0f, 10.5f, 1f };
    double max = 0; for (int i = 0; i < 4; i++) max = Math.Max(max, Math.Abs(Y.Fp[i] - exp[i]));
    Console.WriteLine($"Y = [{string.Join(", ", Y.Fp)}]  shape=[{string.Join(",", Y.Shape)}]");
    Console.WriteLine($"expected [4.5, 0, 10.5, 1]  max|diff|={max:E2}  =>  {(max < 1e-5 ? "PASS" : "FAIL")}");
    return max < 1e-5 ? 0 : 1;
}

static int Probe(string path, bool stopOnMissing = true)
{
    var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(path));
    var g = model.Graph;
    // op histogram
    var hist = new Dictionary<string, int>();
    foreach (var n in g.Node) hist[n.OpType] = hist.GetValueOrDefault(n.OpType) + 1;
    var impl = Interp.Implemented;
    int haveTypes = hist.Keys.Count(k => impl.Contains(k));
    Console.WriteLine($"{path}");
    Console.WriteLine($"nodes={g.Node.Count}  distinct ops={hist.Count}  implemented op-types={haveTypes}/{hist.Count}");
    Console.WriteLine("MISSING op-types: " + string.Join(", ",
        hist.Where(kv => !impl.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}({kv.Value})")));

    // feed zero inputs at a guessed length so tensors flow
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input)
    {
        if (g.Initializer.Any(i => i.Name == vi.Name)) continue;
        var (shape, isInt) = InputShape(vi);
        long n = 1; foreach (var d in shape) n *= d;
        feed[vi.Name] = isInt ? Tensor.I(new long[n], shape) : Tensor.F(new float[n], shape);
        Console.WriteLine($"  feed {vi.Name} [{string.Join(",", shape)}] {(isInt ? "int64" : "float")}");
    }

    int ran = 0; string stoppedAt = null;
    try { new Interp(model).Run(feed, onNode: (_, __, ___) => ran++); }
    catch (NotImplementedException ex) { stoppedAt = ex.Message; }
    catch (Exception ex) { stoppedAt = $"[{ex.GetType().Name}] {ex.Message}"; }
    Console.WriteLine(stoppedAt == null
        ? $"RAN ALL {ran} nodes ✓"
        : $"ran {ran}/{g.Node.Count} nodes, stopped at: {stoppedAt}");
    return 0;
}

// ----- run with real inputs (the validation pivot: load kokoro-tts's dumped tensors, run to
//       completion, diff the waveform against ORT's oracle.bin) -----
static int Run(string[] args)
{
    string path = null, inputsDir = null, outPath = null, dumpNode = null, compareDir = null, injectNode = null; bool trace = false; int stopAfter = 0;
    for (int i = 1; i < args.Length; i++)
        switch (args[i])
        {
            case "--inputs": inputsDir = args[++i]; break;
            case "--out": outPath = args[++i]; break;
            case "--trace": trace = true; break;
            case "--stop-after": stopAfter = int.Parse(args[++i]); break;
            case "--dump-node": dumpNode = args[++i]; break;
            case "--compare": compareDir = args[++i]; break;
            case "--inject": injectNode = args[++i]; break;   // replace matching nodes' output w/ oracle (needs --compare <dir>)
            case "--gpu-matmul": Interp.UseGpuMatMul = true; break;   // offload every MatMul to dpgpu.dll (D3D12); CPU fallback on mount failure
            case "--prof": Interp.Profile = true; break;   // per-op-type wall-time breakdown
            case "--drop": Interp.DropP = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;   // stale-read drop prob on residual merges
            case "--drop-scope": Interp.DropScope = args[++i]; break;   // gate drops to node names containing this (e.g. "generator")
            default: if (path == null) path = args[i]; break;
        }
    if (path == null) return Usage();
    if (inputsDir == null) return Probe(path, stopOnMissing: false);   // legacy zero-feed

    var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(path));
    var g = model.Graph;
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input)
    {
        if (g.Initializer.Any(i => i.Name == vi.Name)) continue;
        string f = Path.Combine(inputsDir, vi.Name + ".bin");
        if (!File.Exists(f)) { Console.Error.WriteLine($"missing input file: {f}"); return 1; }
        var t = LoadBin(f); feed[vi.Name] = t;
        Console.WriteLine($"  feed {vi.Name} [{string.Join(",", t.Shape)}] {(t.IsInt ? "int64" : "float")}");
    }

    int ran = 0; string stoppedAt = null; Dictionary<string, Tensor> outs = null;
    using var traceW = trace ? new StreamWriter(Path.Combine(inputsDir, "trace.log")) : null;
    var cmp = compareDir != null ? new List<(string op, string name, double corr, double rmse, double wrel, string shape)>() : null;
    int injected = 0;
    Action<NodeProto, Tensor[], Dictionary<string, Tensor>> cb = (nd, o2, env) =>
    {
        ran++;
        var t0 = (o2 != null && o2.Length > 0) ? o2[0] : null;
        if (trace)
        {
            string shp = t0 != null ? string.Join(",", t0.Shape) : "";
            string rms = "";
            if (t0 != null && t0.Count <= 4000000) { var f = t0.AsF(); double a = 0; for (long i = 0; i < f.Length; i++) a += (double)f[i] * f[i]; rms = $" rms={(f.Length > 0 ? Math.Sqrt(a / f.Length) : 0):F4}"; }
            string vals = (t0 != null && t0.Count <= 64) ? "  = [" + (t0.IsInt ? string.Join(",", t0.Ip) : string.Join(",", Array.ConvertAll(t0.Fp, v => v.ToString("F3")))) + "]" : "";
            traceW.WriteLine($"{ran}: {nd.OpType} {nd.Name} -> [{shp}]{rms}{vals}");
        }
        if (dumpNode != null && t0 != null)
            foreach (var sub in dumpNode.Split(',')) if (nd.Name.Contains(sub)) { WriteTensorBin(Path.Combine(inputsDir, "dpd_" + Sani(nd.Name) + ".bin"), t0); break; }
        if (cmp != null && t0 != null && !t0.IsInt && nd.Output.Count > 0 && !string.IsNullOrEmpty(nd.Output[0]))
        {
            string f = Path.Combine(compareDir, "ort_" + Sani(nd.Output[0]) + ".bin");
            if (File.Exists(f))
            {
                var or = LoadBin(f).Fp; var dp = t0.Fp; int n = Math.Min(or.Length, dp.Length);
                double sdo = 0, na = 0, nb = 0, ss = 0, wss = 0, wsum = 0;
                for (int i = 0; i < n; i++) { double a = dp[i], b = or[i]; sdo += a * b; na += a * a; nb += b * b; double d = a - b; ss += d * d; double w = Math.Abs(b); wss += w * d * d; wsum += w; }
                double corr = (na > 0 && nb > 0) ? sdo / Math.Sqrt(na * nb) : 1.0;
                double orms = Math.Sqrt(nb / Math.Max(1, n));          // oracle rms (energy scale)
                double wrmse = wsum > 0 ? Math.Sqrt(wss / wsum) : 0;    // |oracle|-weighted rmse -> dead (zero-mag) bins contribute ~0
                double wrel = orms > 0 ? wrmse / orms : 0;             // relative weighted error (the HONEST per-node divergence)
                cmp.Add((nd.OpType, nd.Name, corr, Math.Sqrt(ss / Math.Max(1, n)), wrel, string.Join(",", t0.Shape)));
            }
        }
        if (injectNode != null && nd.Output.Count > 0 && !string.IsNullOrEmpty(nd.Output[0]))
            foreach (var sub in injectNode.Split(','))
                if (nd.Name.Contains(sub))
                {
                    string f = Path.Combine(compareDir ?? "", "ort_" + Sani(nd.Output[0]) + ".bin");
                    if (File.Exists(f)) { env[nd.Output[0]] = LoadBin(f); injected++; }
                    break;
                }
        if (stopAfter > 0 && ran >= stopAfter) throw new Exception($"stop-after {stopAfter}");
    };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { outs = new Interp(model).Run(feed, onNode: cb); }
    catch (NotImplementedException ex) { stoppedAt = ex.Message; }
    catch (Exception ex) { stoppedAt = $"[{ex.GetType().Name}] {ex.Message}"; }
    sw.Stop();

    if (stoppedAt != null) { Console.WriteLine($"ran {ran}/{g.Node.Count} nodes, stopped at: {stoppedAt}"); return 1; }
    Console.WriteLine($"RAN ALL {ran} nodes ✓  ({sw.Elapsed.TotalSeconds:F2}s)");
    if (Interp.Profile)
    {
        double tot = 0; foreach (var kv in Interp.Prof) tot += kv.Value.ms;
        Console.WriteLine($"\nPER-OP PROFILE (wall, {tot:F0} ms total dispatch):");
        foreach (var kv in Interp.Prof.OrderByDescending(k => k.Value.ms))
            Console.WriteLine($"  {kv.Value.ms,9:F1} ms  {100 * kv.Value.ms / tot,5:F1}%  {kv.Value.n,5}x  {kv.Key}");
    }
    if (injectNode != null) Console.WriteLine($"INJECTED {injected} oracle tensors (matching: {injectNode})");

    if (cmp != null)
    {
        const double WTHRESH = 0.01;
        var lowCorr = cmp.Where(e => e.corr < 0.95).ToList();
        var real = cmp.Where(e => e.wrel >= WTHRESH).OrderByDescending(e => e.wrel).ToList();
        Console.WriteLine($"\nDIVERGENCE MAP vs oracle ({cmp.Count} float nodes) — energy-weighted ranking (wrel = |oracle|-weighted rmse / oracle rms):");
        Console.WriteLine($"  CAVEAT: wrel weights error by |oracle value|, so atan2 phase-singularity nodes (Div=imag/real at zero magnitude) are UP-weighted, not suppressed.");
        Console.WriteLine($"          To prove a node doesn't reach the output, use --inject (the causal test), not this metric. {lowCorr.Count} nodes corr<0.95; {real.Count} with wrel>={WTHRESH}:");
        foreach (var e in real.Take(40))
            Console.WriteLine($"  wrel={e.wrel:F4} corr={e.corr:F4} rmse={e.rmse:E2}  {e.op,-14} [{e.shape}]  {e.name}");
    }

    var y = outs.Values.First(); var wav = y.AsF();
    Console.WriteLine($"output [{string.Join(",", y.Shape)}]  samples={wav.Length}  ({wav.Length / 24000.0:F2}s)  rms={Rms(wav):F5}  peak={Peak(wav):F5}");
    { long nan = 0; foreach (var f in wav) if (float.IsNaN(f) || float.IsInfinity(f)) nan++;
      if (Interp.DropP > 0 || nan > 0) Console.WriteLine($"  STALE-READ TEST: dropped {Interp.Dropped} residual merges (--drop {Interp.DropP});  NaN/Inf samples: {nan}/{wav.Length}"); }
    if (outPath != null) { WriteWav(outPath, wav, 24000); Console.WriteLine($"wrote {outPath}"); }

    string oracle = Path.Combine(inputsDir, "oracle.bin");
    if (File.Exists(oracle))
    {
        var o = LoadBin(oracle).AsF(); int n = Math.Min(o.Length, wav.Length);
        double maxd = 0, sumsq = 0;
        for (int i = 0; i < n; i++) { double d = Math.Abs(wav[i] - o[i]); maxd = Math.Max(maxd, d); sumsq += d * d; }
        double rmse = Math.Sqrt(sumsq / Math.Max(1, n));
        bool ok = o.Length == wav.Length && maxd <= 1e-3;
        Console.WriteLine($"VALIDATE vs oracle.bin: len {wav.Length} vs {o.Length}  max|diff|={maxd:E3}  rmse={rmse:E3}  =>  {(ok ? "ALLCLOSE ✓ — ORT can be dropped" : "MISMATCH")}");
    }
    return 0;
}

// ----- stream: breath-group chunked streaming synthesis on the ORT-free interp (the push-pipeline foundation) -----
// Splits the IPA phoneme string into ~breath-group chunks, synthesizes each independently through ONE parsed
// graph, and overlap-add stitches. Proves the two load-bearing claims: streaming latency (first audio after
// chunk 0, not the whole utterance) and click-free seams. Grounded sizes: ~13 kokoro tokens ~= 1.6s, so a
// 2.5-3.5s breath group ~= 25-40 tokens (dp-onnx-receipts + breath-group prosody).
static int Stream(string[] args)
{
    string model = null, phonemes = null, phonemesFile = null, outPath = "stream.wav", voice = "af_heart";
    string configPath = @"S:\reference\Kokoro-82M\config.json", voicesDir = @"S:\reference\Kokoro-82M\voices";
    float speed = 1.0f; int budget = 32, min = 12; double xfadeMs = 6, gapMs = 0;
    for (int i = 1; i < args.Length; i++)
        switch (args[i])
        {
            case "--phonemes": phonemes = args[++i]; break;
            case "--phonemes-file": phonemesFile = args[++i]; break;
            case "--voice": voice = args[++i]; break;
            case "--speed": speed = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            case "--out": outPath = args[++i]; break;
            case "--budget": budget = int.Parse(args[++i]); break;
            case "--min": min = int.Parse(args[++i]); break;
            case "--xfade-ms": xfadeMs = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            case "--gap-ms": gapMs = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            case "--config": configPath = args[++i]; break;
            case "--voices": voicesDir = args[++i]; break;
            case "--model": model = args[++i]; break;
            default: if (model == null && !args[i].StartsWith("--")) model = args[i]; break;
        }
    if (phonemesFile != null) phonemes = File.ReadAllText(phonemesFile, System.Text.Encoding.UTF8).Trim();
    if (model == null || phonemes == null)
    { Console.Error.WriteLine("usage: dp-onnx stream <model.onnx> --phonemes-file <ipa.txt> [--voice af_heart] [--out out.wav] [--budget 32] [--min 12] [--xfade-ms 6] [--gap-ms 0]"); return 1; }

    const int SR = 24000;
    var vocab = LoadVocab(configPath);
    var chunks = SplitBreathGroups(phonemes, budget, min, vocab);
    Console.WriteLine($"phonemes={phonemes.Length} chars  vocab={vocab.Count}  ->  {chunks.Count} breath-group chunks (budget={budget} tok, min={min})\n");

    var swP = System.Diagnostics.Stopwatch.StartNew();
    var mp = ModelProto.Parser.ParseFrom(File.ReadAllBytes(model));
    var interp = new Interp(mp);
    swP.Stop();
    Console.WriteLine($"parsed {mp.Graph.Node.Count}-node graph ({new FileInfo(model).Length / 1e6:F0} MB) in {swP.Elapsed.TotalSeconds:F2}s\n");

    var pieces = new List<float[]>();
    double firstChunk = 0, totalCompute = 0; long totalNan = 0;
    for (int c = 0; c < chunks.Count; c++)
    {
        var ph = chunks[c];
        var ids = new List<long> { 0 };
        foreach (var ch in ph) if (vocab.TryGetValue(ch, out var id)) ids.Add(id);
        ids.Add(0);
        int realCount = ids.Count - 2;
        var style = LoadVoiceStyle(Path.Combine(voicesDir, voice + ".pt"), realCount);
        var feed = new Dictionary<string, Tensor>
        {
            ["input_ids"] = Tensor.I(ids.ToArray(), 1, ids.Count),
            ["style"] = Tensor.F(style, 1, style.Length),
            ["speed"] = Tensor.F(new[] { speed }, 1),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outs = interp.Run(feed);
        sw.Stop();
        var wav = outs.Values.First().AsF();
        if (c == 0) firstChunk = sw.Elapsed.TotalSeconds;
        totalCompute += sw.Elapsed.TotalSeconds;
        long nan = 0; foreach (var f in wav) if (float.IsNaN(f) || float.IsInfinity(f)) nan++; totalNan += nan;
        pieces.Add(wav);
        Console.WriteLine($"  chunk {c,2}: {realCount,3} tok -> {wav.Length,7} samp ({wav.Length / (double)SR,5:F2}s)  compute={sw.Elapsed.TotalSeconds,5:F2}s  rms={Rms(wav):F4}  nan={nan}  \"{Trunc(ph, 30)}\"");
    }

    int xfade = (int)(xfadeMs * SR / 1000.0), gap = (int)(gapMs * SR / 1000.0);
    var (stitched, seamMax, p99) = OverlapAddStitch(pieces, xfade, gap);
    WriteWav(outPath, stitched, SR);

    double audioSec = stitched.Length / (double)SR;
    Console.WriteLine($"\nSTREAM  {chunks.Count} chunks -> {stitched.Length} samp ({audioSec:F2}s audio)  total compute {totalCompute:F2}s  ({audioSec / Math.Max(totalCompute, 1e-9):F1}x realtime)  nan={totalNan}");
    Console.WriteLine($"  LATENCY  first audio after {firstChunk:F2}s (single-pass blocks {totalCompute:F2}s for the whole graph before any sample)  ->  {totalCompute / Math.Max(firstChunk, 1e-9):F1}x faster to first sound");
    Console.WriteLine($"  SEAMS    max |delta| at {xfade}-samp crossfades = {seamMax:E3}   in-chunk p99 |delta| = {p99:E3}   ->  {(seamMax <= p99 * 2 ? "CLICK-FREE (seam <= 2x in-chunk step)" : "AUDIBLE STEP — raise --xfade-ms")}");
    Console.WriteLine($"  wrote {outPath}");
    return 0;
}

// fold: evaluate the graph at static inputs and FREEZE matching nodes' outputs to Constant initializers —
// the python-free static-ifier / "fix-shape" gap. Kills the dynamic /encoder/Range (shape-derived limit) that
// blocks qairt-converter (`Dynamic value for ... is not supported`), without onnxsim/python.
//   dp-onnx fold <in.onnx> <out.onnx> [--inputs <dir>] --nodes <substr>[,<substr>...]
// Inputs default to zero-fill at the model's declared (static) shapes — correct for shape-derived nodes.
static int Fold(string[] args)
{
    if (args.Length < 3) return Usage();
    string inP = args[1], outP = args[2], inputsDir = null; string[] subs = null;
    for (int i = 3; i < args.Length; i++)
        switch (args[i]) { case "--inputs": inputsDir = args[++i]; break; case "--nodes": subs = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break; }
    if (subs == null) { Console.Error.WriteLine("fold needs --nodes <substr>[,...]"); return 1; }
    var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(inP));
    var g = model.Graph;
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input)
    {
        if (g.Initializer.Any(i => i.Name == vi.Name)) continue;
        string f = inputsDir != null ? Path.Combine(inputsDir, vi.Name + ".bin") : null;
        bool fromBin = f != null && File.Exists(f);
        if (fromBin) feed[vi.Name] = LoadBin(f);
        else { var (sh, isInt) = InputShape(vi); long n = 1; foreach (var d in sh) n *= d; feed[vi.Name] = isInt ? Tensor.I(new long[n], sh) : Tensor.F(new float[n], sh); }
        Console.WriteLine($"  feed {vi.Name} [{string.Join(",", feed[vi.Name].Shape)}] {(feed[vi.Name].IsInt ? "i64" : "f32")}{(fromBin ? " (.bin)" : " (zero-fill)")}");
    }
    var frozen = new List<(NodeProto node, Tensor val)>();
    var seen = new HashSet<string>();
    try
    {
        new Interp(model).Run(feed, onNode: (nd, outs, env) =>
        {
            if (outs.Length > 0 && outs[0] != null && nd.Output.Count > 0 && !string.IsNullOrEmpty(nd.Output[0]) && !seen.Contains(nd.Name))
                foreach (var s in subs) if (nd.Name.Contains(s)) { frozen.Add((nd, outs[0])); seen.Add(nd.Name); break; }
        });
    }
    catch (Exception ex) { Console.WriteLine($"  (run stopped: {ex.GetType().Name} {ex.Message} — froze {frozen.Count} before stop)"); }

    var frozenNames = new HashSet<string>(frozen.Select(c => c.node.Name));
    foreach (var (nd, val) in frozen) g.Initializer.Add(MakeInit(nd.Output[0], val));
    for (int i = g.Node.Count - 1; i >= 0; i--) if (frozenNames.Contains(g.Node[i].Name)) g.Node.RemoveAt(i);
    File.WriteAllBytes(outP, model.ToByteArray());
    Console.WriteLine($"FOLDED {frozen.Count} node(s) -> Constant initializers, wrote {outP}  ({g.Node.Count} nodes remain)");
    foreach (var (nd, val) in frozen)
        Console.WriteLine($"  {nd.OpType,-10} {nd.Name}  ->  init {nd.Output[0]} [{string.Join(",", val.Shape)}] {(val.IsInt ? "i64" : "f32")}{(val.Count <= 8 ? " = [" + (val.IsInt ? string.Join(",", val.Ip) : string.Join(",", val.Fp)) + "]" : "")}");
    return 0;
}

static TensorProto MakeInit(string name, Tensor t)
{
    var tp = new TensorProto { Name = name, DataType = t.IsInt ? 7 : 1 };
    foreach (var d in t.Shape) tp.Dims.Add((long)d);
    if (t.IsInt) tp.Int64Data.Add(t.Ip); else tp.FloatData.Add(t.Fp);
    return tp;
}

// IPA phoneme string -> breath-group chunks. HARD cut at sentence marks (. ! ? …), SOFT at clause marks
// (, ; :) once past --min tokens, else at a space once a chunk reaches --budget tokens (the no-punctuation
// fallback). Bias-after->cut mirrors how a speaker breaks at an intonational-phrase boundary.
static List<string> SplitBreathGroups(string ph, int budget, int min, Dictionary<char, long> vocab)
{
    var chunks = new List<string>(); var cur = new System.Text.StringBuilder(); int tok = 0;
    const string hard = ".!?…", soft = ";:,";
    foreach (var ch in ph)
    {
        cur.Append(ch);
        if (vocab.ContainsKey(ch) && !char.IsWhiteSpace(ch)) tok++;
        bool cut = (hard.IndexOf(ch) >= 0 && tok >= 1) || (soft.IndexOf(ch) >= 0 && tok >= min) || (ch == ' ' && tok >= budget);
        if (cut) { var s = cur.ToString().Trim(); if (s.Length > 0) chunks.Add(s); cur.Clear(); tok = 0; }
    }
    { var s = cur.ToString().Trim(); if (s.Length > 0) chunks.Add(s); }
    return chunks;
}

// Stitch per-chunk waveforms: equal-power crossfade over `xfade` samples at each join (the [0]-pad makes
// chunk edges near-silent, so the fade lands on quiet audio and erases any DC step) + optional `gap` of
// silence (the inhalation pause). Returns seam max|delta| (at splices) vs in-chunk p99 |delta| (the click test).
static (float[] outw, double seamMax, double p99) OverlapAddStitch(List<float[]> pieces, int xfade, int gap)
{
    var outl = new List<float>(); var spliceIdx = new List<int>();
    for (int p = 0; p < pieces.Count; p++)
    {
        var piece = pieces[p];
        if (p == 0) { outl.AddRange(piece); continue; }
        if (gap > 0) { for (int k = 0; k < gap; k++) outl.Add(0f); }
        int ov = Math.Max(0, Math.Min(xfade, Math.Min(outl.Count, piece.Length)));
        int start = outl.Count - ov;
        for (int k = 0; k < ov; k++)
        {
            double t = (k + 0.5) / ov, a = Math.Cos(t * Math.PI / 2), b = Math.Sin(t * Math.PI / 2);   // equal-power
            outl[start + k] = (float)(outl[start + k] * a + piece[k] * b);
        }
        spliceIdx.Add(start);
        for (int k = ov; k < piece.Length; k++) outl.Add(piece[k]);
    }
    var w = outl.ToArray();
    int win = Math.Max(1, xfade);
    var seamSet = new HashSet<int>();
    foreach (var s in spliceIdx) for (int k = -win; k <= win; k++) { int idx = s + k; if (idx >= 0 && idx < w.Length - 1) seamSet.Add(idx); }
    double seamMax = 0; var bulk = new List<double>(Math.Max(0, w.Length - 1));
    for (int i = 0; i < w.Length - 1; i++)
    {
        double d = Math.Abs(w[i + 1] - w[i]);
        if (seamSet.Contains(i)) seamMax = Math.Max(seamMax, d); else bulk.Add(d);
    }
    bulk.Sort();
    double p99 = bulk.Count > 0 ? bulk[Math.Min(bulk.Count - 1, (int)(bulk.Count * 0.99))] : 0;
    return (w, seamMax, p99);
}

static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, Math.Max(0, n - 1)) + "…";

// kokoro front-end, ported from kokoro-tts (pure BCL, NO onnxruntime): vocab from config.json, voice style row.
static Dictionary<char, long> LoadVocab(string configPath)
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
    var v = doc.RootElement.GetProperty("vocab"); var d = new Dictionary<char, long>();
    foreach (var p in v.EnumerateObject()) if (p.Name.Length == 1) d[p.Name[0]] = p.Value.GetInt64();
    return d;
}

static float[] LoadVoiceStyle(string ptPath, int tokenCount)
{
    using var zip = System.IO.Compression.ZipFile.OpenRead(ptPath);
    var entry = zip.Entries.First(e => e.FullName.EndsWith("data/0"));
    using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
    var raw = ms.ToArray(); int rows = raw.Length / (256 * 4);
    int row = Math.Clamp(tokenCount, 0, rows - 1);
    var style = new float[256]; Buffer.BlockCopy(raw, row * 256 * 4, style, 0, 256 * 4);
    return style;
}

static Tensor LoadBin(string path)
{
    using var br = new BinaryReader(File.OpenRead(path));
    int dtype = br.ReadInt32(); int rank = br.ReadInt32();
    var shape = new int[rank]; long n = 1;
    for (int i = 0; i < rank; i++) { shape[i] = (int)br.ReadInt64(); n *= shape[i]; }
    if (dtype == 7) { var d = new long[n]; for (long i = 0; i < n; i++) d[i] = br.ReadInt64(); return Tensor.I(d, shape); }
    var fd = new float[n]; for (long i = 0; i < n; i++) fd[i] = br.ReadSingle(); return Tensor.F(fd, shape);
}

static void WriteWav(string path, float[] s, int sr)
{
    using var bw = new BinaryWriter(File.Create(path));
    int dataBytes = s.Length * 2;
    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataBytes); bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(dataBytes);
    foreach (var f in s) bw.Write((short)Math.Round(Math.Clamp(f, -1f, 1f) * 32767));
}

static double Rms(float[] s) { double a = 0; foreach (var f in s) a += (double)f * f; return s.Length > 0 ? Math.Sqrt(a / s.Length) : 0; }
static double Peak(float[] s) { double p = 0; foreach (var f in s) p = Math.Max(p, Math.Abs(f)); return p; }

// ----- specdiff: the PERCEPTUAL validator. Compares two wavs in the MAGNITUDE-spectrogram domain
//   (multi-resolution STFT loss — exactly how these vocoders are trained), gain-aligned so a pure
//   loudness offset isn't counted. This is the HONEST oracle for "does it sound like ORT": waveform
//   rmse over-weights the meaningless low-magnitude phase, magnitude-spectra do not.
//     dp-onnx specdiff <wavA> <wavB>
static int SpecDiff(string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: dp-onnx specdiff <wavA> <wavB>"); return 1; }
    var a = ReadWav(args[1]); var b = ReadWav(args[2]);
    double rmsA = Rms(a), rmsB = Rms(b);
    Console.WriteLine($"A {Path.GetFileName(args[1])}: {a.Length} samp  rms={rmsA:F5}  peak={Peak(a):F5}");
    Console.WriteLine($"B {Path.GetFileName(args[2])}: {b.Length} samp  rms={rmsB:F5}  peak={Peak(b):F5}");
    double g = rmsB / Math.Max(1e-9, rmsA);
    Console.WriteLine($"gain B/A = {g:F3}  ({20 * Math.Log10(Math.Max(1e-9, g)):+0.0;-0.0} dB)  <- the loudness difference; removed below");
    var ag = new float[a.Length]; for (int i = 0; i < a.Length; i++) ag[i] = (float)(a[i] * g);   // gain-align A to B

    int[] nffts = { 256, 512, 1024, 2048 };
    double scSum = 0; int scN = 0;
    Console.WriteLine("\nmagnitude-spectrogram divergence (gain-aligned):");
    foreach (var nf in nffts)
    {
        int hop = nf / 4;
        var (sc, lsd, corr) = SpecMetrics(ag, b, nf, hop);
        Console.WriteLine($"  nfft={nf,4} hop={hop,4}:  spectral-convergence={sc:F4}  log-spec-dist={lsd:F2} dB  mag-corr={corr:F4}");
        scSum += sc; scN++;
    }
    double scAvg = scSum / scN;
    Console.WriteLine($"\nMULTI-RES spectral convergence = {scAvg:F4}  =>  {(scAvg < 0.10 ? "SPECTRALLY EQUIVALENT — perceptually matches the reference" : "audible spectral difference")}");
    return 0;
}

static float[] ReadWav(string path)
{
    var raw = File.ReadAllBytes(path);
    int p = 12, dataOff = -1, dataLen = 0; short bits = 16, ch = 1;   // skip RIFF....WAVE
    while (p + 8 <= raw.Length)
    {
        string id = System.Text.Encoding.ASCII.GetString(raw, p, 4);
        int sz = BitConverter.ToInt32(raw, p + 4);
        if (id == "fmt ") { ch = BitConverter.ToInt16(raw, p + 8 + 2); bits = BitConverter.ToInt16(raw, p + 8 + 14); }
        else if (id == "data") { dataOff = p + 8; dataLen = sz; break; }
        p += 8 + sz + (sz & 1);
    }
    if (dataOff < 0) throw new Exception("no data chunk in " + path);
    int bytesPer = bits / 8, n = dataLen / bytesPer / Math.Max(1, (int)ch);
    var s = new float[n];   // first channel only (mono expected)
    for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(raw, dataOff + i * bytesPer * ch) / 32768f;
    return s;
}

// Hann-windowed onesided magnitude STFT (own kernel — the interpreter validates itself).
static float[][] MagStft(float[] x, int nfft, int hop)
{
    int bins = nfft / 2 + 1;
    int frames = x.Length >= nfft ? (x.Length - nfft) / hop + 1 : 0;
    var win = new double[nfft];
    for (int m = 0; m < nfft; m++) win[m] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * m / (nfft - 1));
    var cs = new double[bins * nfft]; var sn = new double[bins * nfft];
    for (int k = 0; k < bins; k++) for (int m = 0; m < nfft; m++) { double an = -2.0 * Math.PI * k * m / nfft; cs[k * nfft + m] = Math.Cos(an); sn[k * nfft + m] = Math.Sin(an); }
    var outm = new float[frames][];
    System.Threading.Tasks.Parallel.For(0, frames, f =>
    {
        int st = f * hop; var mag = new float[bins];
        for (int k = 0; k < bins; k++)
        {
            double re = 0, im = 0; int kb = k * nfft;
            for (int m = 0; m < nfft; m++) { double v = x[st + m] * win[m]; re += v * cs[kb + m]; im += v * sn[kb + m]; }
            mag[k] = (float)Math.Sqrt(re * re + im * im);
        }
        outm[f] = mag;
    });
    return outm;
}

// spectral convergence ||Mb-Ma||/||Mb||, log-spectral distance (dB), magnitude correlation — over common frames/bins.
static (double sc, double lsd, double corr) SpecMetrics(float[] a, float[] b, int nfft, int hop)
{
    var Ma = MagStft(a, nfft, hop); var Mb = MagStft(b, nfft, hop);
    int F = Math.Min(Ma.Length, Mb.Length); if (F == 0) return (0, 0, 1);
    int K = Ma[0].Length;
    double num = 0, den = 0, lsd = 0, sab = 0, saa = 0, sbb = 0; long cnt = 0;
    for (int f = 0; f < F; f++)
        for (int k = 0; k < K; k++)
        {
            double va = Ma[f][k], vb = Mb[f][k];
            double d = vb - va; num += d * d; den += vb * vb;
            double la = 20 * Math.Log10(va + 1e-7), lb = 20 * Math.Log10(vb + 1e-7); lsd += Math.Abs(la - lb);
            sab += va * vb; saa += va * va; sbb += vb * vb; cnt++;
        }
    return (Math.Sqrt(num / Math.Max(1e-12, den)), lsd / Math.Max(1, cnt), sab / Math.Max(1e-12, Math.Sqrt(saa * sbb)));
}

static void WriteTensorBin(string path, Tensor t)
{
    using var bw = new BinaryWriter(File.Create(path));
    bw.Write(t.IsInt ? 7 : 1); bw.Write(t.Shape.Length);
    foreach (var d in t.Shape) bw.Write((long)d);
    if (t.IsInt) foreach (var v in t.Ip) bw.Write(v); else foreach (var v in t.Fp) bw.Write(v);
}
static string Sani(string s) { var c = s.ToCharArray(); for (int i = 0; i < c.Length; i++) if (!char.IsLetterOrDigit(c[i])) c[i] = '_'; return new string(c).Trim('_'); }

// ----- proto builders (for selftest) -----
static ValueInfoProto ValueInfo(string n) => new() { Name = n };
static NodeProto Node(string op, string[] ins, string[] outs)
{ var n = new NodeProto { OpType = op }; n.Input.Add(ins); n.Output.Add(outs); return n; }
static TensorProto FloatInit(string name, float[] data, params long[] dims)
{ var t = new TensorProto { Name = name, DataType = 1 }; t.Dims.Add(dims); t.FloatData.Add(data); return t; }

static (int[] shape, bool isInt) InputShape(ValueInfoProto vi)
{
    var tt = vi.Type?.TensorType;
    bool isInt = tt != null && (tt.ElemType == 7 || tt.ElemType == 6);
    var shape = new List<int>();
    if (tt?.Shape != null)
        foreach (var d in tt.Shape.Dim)
            shape.Add(d.DimValue > 0 ? (int)d.DimValue : 15);   // guess dynamic axes = 15 (a short utterance)
    if (shape.Count == 0) shape.Add(1);
    return (shape.ToArray(), isInt);
}

// ===========================================================================
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
        "Relu","LeakyRelu","Sigmoid","Tanh","Sqrt","Exp","Neg","Abs","Floor","Sin","Cos","Erf","Clip","Softplus","Reciprocal",
        "Reshape","Transpose","Unsqueeze","Squeeze","Concat","Identity","Constant","Cast","Shape","Gather","Flatten",
        "Equal","Greater","Less","GreaterOrEqual","LessOrEqual","And","Or","Not","Min","Max","Mod","Round","Atan","Sign",
        "Where","Expand","ConstantOfShape","Range","ReduceMean","ReduceSum","CumSum","Slice","Pad",
        "Conv","ConvTranspose","LayerNormalization","Softmax","LSTM","Resize","STFT","NonZero","ScatterND",
        "Split","Tile","GroupNormalization","Gelu","InstanceNormalization","ReduceProd",
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
            case "Reshape": return One(Reshape(x[0], x[1]));
            case "Flatten": return One(Reshape(x[0], null, flattenAxis: (int)L(n, "axis", 1), src: x[0]));
            case "Squeeze": return One(Squeeze(x[0], x.Length > 1 ? x[1] : null, n));
            case "Unsqueeze": return One(Unsqueeze(x[0], x.Length > 1 ? x[1] : null, n));
            case "Transpose": return One(Transpose(x[0], Ints(n, "perm")));
            case "Concat": return One(Concat(x, (int)L(n, "axis", 0)));
            case "Shape": return One(Tensor.I(Array.ConvertAll(x[0].Shape, s => (long)s), x[0].Shape.Length));
            case "Gather": return One(Gather(x[0], x[1], (int)L(n, "axis", 0)));
            case "Equal": return One(Cmp(x[0], x[1], (a, b) => a == b));
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
            case "ReduceMean": return One(Reduce(x, n, mean: true));
            case "ReduceSum": return One(Reduce(x, n, mean: false));
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
            default: throw new NotImplementedException($"node #?: {n.OpType} (name={n.Name})");
        }
    }

    // ---- kernels ----
    static Tensor[] One(Tensor t) => new[] { t };

    static Tensor Un(Tensor a, Func<float, float> f)
    { var d = a.AsF(); var o = new float[d.Length]; for (int i = 0; i < d.Length; i++) o[i] = f(d[i]); return Tensor.F(o, a.Shape); }

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
    {   // mirrors MatMul's 4D broadcast, but dispatches each batch's GEMM to the GPU; CPU fallback if the mount fails
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
        byte[] dxil = GemmDxil(); var bidx = new int[nb];
        for (long bi = 0; bi < outBatch; bi++)
        {
            long aB = 0, bB = 0; for (int k = 0; k < nb; k++) { aB += bidx[k] * sA[k]; bB += bidx[k] * sB[k]; }
            Array.Copy(a, aB * M * K, aSub, 0, (long)M * K);
            Array.Copy(b, bB * K * N, bSub, 0, (long)K * N);
            int rc;
            try { rc = Gpu.dpgpu_gemm(aSub, bSub, cSub, (uint)M, (uint)N, (uint)K, dxil, (uint)dxil.Length); }
            catch (DllNotFoundException) { _gpuDead = true; return MatMul(A, B); }
            if (rc != 0) { _gpuDead = true; return MatMul(A, B); }
            Array.Copy(cSub, 0, o, bi * M * N, (long)M * N);
            for (int k = nb - 1; k >= 0; k--) { if (++bidx[k] < lead[k]) break; bidx[k] = 0; }
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

    static Tensor Reduce(Tensor[] x, NodeProto n, bool mean)
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
        int m = axes.Count; bool trailing = m > 0; for (int k = r - m; k < r; k++) if (k < 0 || !axes.Contains(k)) trailing = false;
        if (trailing)   // reducing a contiguous trailing block -> each output = SIMD sum of a contiguous run (LayerNorm/InstanceNorm mean)
        {
            var of = new float[outN];
            System.Threading.Tasks.Parallel.For(0L, outN, i => { double s = SimdSum(d.AsSpan(checked((int)(i * reduced)), (int)reduced)); of[i] = (float)(mean ? s / reduced : s); });
            return Tensor.F(of, oshA);
        }
        var acc = new double[outN];
        var idx = new int[r];
        for (long lin = 0; lin < d.Length; lin++)
        {
            long oi = 0; for (int k = 0; k < r; k++) { int od = outDimOfIn[k]; if (od >= 0) { int coord = axes.Contains(k) && keep ? 0 : idx[k]; oi += coord * oStr[od]; } }
            acc[oi] += d[lin];
            for (int k = r - 1; k >= 0; k--) { if (++idx[k] < a.Shape[k]) break; idx[k] = 0; }
        }
        var o = new float[outN];
        for (long i = 0; i < outN; i++) o[i] = (float)(mean ? acc[i] / reduced : acc[i]);
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
