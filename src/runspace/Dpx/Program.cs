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
using Subsystem.Dpx;

// DPX_BUDGET_MB caps the engine's self-imposed RAM budget (simulate an 8GB phone on a big box; the Android
// head sets DpxMem.BudgetOverride from ActivityManager instead). 0/unset => derive from the device.
if (long.TryParse(Environment.GetEnvironmentVariable("DPX_BUDGET_MB"), out var _bmb) && _bmb > 0) DpxMem.BudgetOverride = _bmb << 20;

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
     : args[0] == "gpu-test-q4" ? GpuTestQ4(args)
     : args[0] == "gpu-bench" ? GpuBench(args)
     : args[0] == "gpu-tune" ? GpuTune(args)
     : args[0] == "db" ? ToDb(args)
     : args[0] == "db-stats" ? DbStats(args)
     : args[0] == "dumpsg" ? DumpSg(args)
     : args[0] == "loaddb" ? LoadDbTest(args)
     : args[0] == "generate" ? Generate(args)
     : args[0] == "gen-onnx" ? GenOnnx(args)
     : args[0] == "tokenize" ? Tokenize(args)
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

// db: compile a model into a queryable SQLite model store. The graph becomes rows — op triage ("which ops
// can't run on a backend?") is a SELECT, per-op backend routing is the `backend` column, and weights are
// raw-byte BLOBs (dtype + dims). The model becomes a Cm-projectable capability instead of an opaque
// protobuf/flatbuffer blob (CRQ143/158). Sources: an ONNX graph (one signature), or a .litertlm — every
// TFLiteModel section becomes a `signature` row (the container is multi-signature: embed / PLE / vision /
// decoder / MTP …), translated through the sovereign tflite reader (Tflite.ToModelProto, nativeQuant) so
// weights are stored PACKED — the quantized truth-at-rest the VOM gather feeds, never fp32-expanded.
// `--section N` writes just that section. Signatures are built+written one at a time so only one graph is
// resident (the 818MB decoder must not co-reside with the 1.28GB PLE).
static int ToDb(string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: dp-onnx db <model.onnx|.litertlm> <out.db> [--section <N>]"); return 1; }
    string srcPath = args[1], dbPath = args[2];
    int section = -1;
    for (int i = 3; i < args.Length - 1; i++) if (args[i] == "--section") int.TryParse(args[i + 1], out section);

    if (srcPath.EndsWith(".litertlm", StringComparison.OrdinalIgnoreCase))
    {
        var secs = LiteRtLm.ReadSections(srcPath);
        var pick = new List<int>();
        if (section >= 0)
        {
            if (section >= secs.Count || secs[section].DataType != 3) { Console.Error.WriteLine($"section [{section}] is not a TFLiteModel"); return 1; }
            pick.Add(section);
        }
        else for (int i = 0; i < secs.Count; i++) if (secs[i].DataType == 3) pick.Add(i);

        IEnumerable<(int sig, string role, GraphProto g)> Sigs()
        {
            foreach (int i in pick)
            {
                var g = Tflite.ToModelProto(LiteRtLm.ReadSectionBytes(srcPath, secs[i]), 0, out string summary, nativeQuant: true, lenient: true).Graph;
                Console.Error.WriteLine($"[{i}] {summary.Split('\n')[0].Trim()}");
                yield return (i, $"section{i}", g);
            }
        }
        return WriteModelDb(dbPath, Sigs());
    }
    return WriteModelDb(dbPath, new[] { (0, "onnx", ModelProto.Parser.ParseFrom(File.ReadAllBytes(srcPath)).Graph) });
}

static int GpuTune(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dp-onnx gpu-tune <model.db>"); return 1; }
    return ShaderTournament.RunTournament(args[1]);
}

// Write one or more signatures (sig, role, graph) into the SQLite model store. One writer for the ONNX and
// litertlm paths (invariant 9). node/tensor ids are GLOBAL across signatures so node_attr.tensor_id stays
// unique; the `signature` table is the section index. `sigs` is streamed — each graph is written then dropped.
static int WriteModelDb(string dbPath, IEnumerable<(int sig, string role, GraphProto g)> sigs)
{
    if (File.Exists(dbPath)) File.Delete(dbPath);
    using var c = new SqliteConnection($"Data Source={dbPath}");
    c.Open();
    using (var ddl = c.CreateCommand())
    {
        ddl.CommandText = @"
CREATE TABLE signature(sig INTEGER PRIMARY KEY, role TEXT, nodes INTEGER, tensors INTEGER, inputs INTEGER, outputs INTEGER);
CREATE TABLE graph_io(sig INTEGER, kind TEXT, name TEXT, elem_type INTEGER, shape TEXT);
CREATE TABLE node(id INTEGER PRIMARY KEY, sig INTEGER, ord INTEGER, op_type TEXT, name TEXT, backend TEXT);
CREATE TABLE node_io(node_id INTEGER, slot INTEGER, kind TEXT, value_name TEXT);
CREATE TABLE node_attr(node_id INTEGER, name TEXT, type INTEGER, i INTEGER, f REAL, s TEXT, ints TEXT, floats TEXT, tensor_id INTEGER);
CREATE TABLE tensor(id INTEGER PRIMARY KEY, sig INTEGER, name TEXT, dtype INTEGER, dims TEXT, data BLOB);
CREATE TABLE IF NOT EXISTS gpu_tactic_plan (
    adapter_name TEXT,
    op_type TEXT,
    m INTEGER,
    n INTEGER,
    k INTEGER,
    block_m INTEGER,
    block_n INTEGER,
    block_k INTEGER,
    thread_m INTEGER,
    thread_n INTEGER,
    use_shared_mem INTEGER,
    unroll_factor INTEGER,
    dxil_bytecode BLOB,
    latency_ms REAL,
    PRIMARY KEY(adapter_name, op_type, m, n, k)
);";
        ddl.ExecuteNonQuery();
    }
    using var tx = c.BeginTransaction();
    SqliteCommand P(string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }
    const long BlobCap = 1_000_000_000;   // SQLite default SQLITE_MAX_LENGTH; a larger native tensor is stored NULL + warned (chunked-BLOB is a later slice)
    int tid = 0, nid = 0, sigCount = 0, totalNodes = 0;
    int WriteTensor(int sig, TensorProto t)
    {
        int id = tid++;
        byte[] data = t.RawData != null && t.RawData.Length > 0 ? t.RawData.ToByteArray() : TypedBytes(t);
        if (data != null && data.LongLength >= BlobCap) { Console.Error.WriteLine($"  WARN tensor '{t.Name}' {data.LongLength}B >= blob cap — stored NULL (needs chunked BLOB)"); data = null; }
        using var cmd = P("INSERT INTO tensor(id,sig,name,dtype,dims,data) VALUES($id,$sig,$n,$dt,$dm,$d)",
            ("$id", id), ("$sig", sig), ("$n", t.Name ?? ""), ("$dt", t.DataType), ("$dm", string.Join(",", t.Dims)), ("$d", (object?)data));
        cmd.ExecuteNonQuery();
        return id;
    }
    foreach (var (sig, role, g) in sigs)
    {
        sigCount++;
        foreach (var t in g.Initializer) WriteTensor(sig, t);
        foreach (var vi in g.Input)  using (var cmd = P("INSERT INTO graph_io(sig,kind,name,elem_type,shape) VALUES($sig,'in',$n,$e,$s)",  ("$sig", sig), ("$n", vi.Name ?? ""), ("$e", vi.Type?.TensorType?.ElemType ?? 0), ("$s", ShapeStr(vi)))) cmd.ExecuteNonQuery();
        foreach (var vi in g.Output) using (var cmd = P("INSERT INTO graph_io(sig,kind,name,elem_type,shape) VALUES($sig,'out',$n,$e,$s)", ("$sig", sig), ("$n", vi.Name ?? ""), ("$e", vi.Type?.TensorType?.ElemType ?? 0), ("$s", ShapeStr(vi)))) cmd.ExecuteNonQuery();
        int sigNodes = 0;
        foreach (var nd in g.Node)
        {
            int id = nid++;
            using (var cmd = P("INSERT INTO node(id,sig,ord,op_type,name,backend) VALUES($id,$sig,$o,$op,$n,NULL)", ("$id", id), ("$sig", sig), ("$o", id), ("$op", nd.OpType ?? ""), ("$n", nd.Name ?? ""))) cmd.ExecuteNonQuery();
            for (int k = 0; k < nd.Input.Count; k++)  using (var cmd = P("INSERT INTO node_io VALUES($id,$s,'in',$v)",  ("$id", id), ("$s", k), ("$v", nd.Input[k] ?? ""))) cmd.ExecuteNonQuery();
            for (int k = 0; k < nd.Output.Count; k++) using (var cmd = P("INSERT INTO node_io VALUES($id,$s,'out',$v)", ("$id", id), ("$s", k), ("$v", nd.Output[k] ?? ""))) cmd.ExecuteNonQuery();
            foreach (var a in nd.Attribute)
            {
                object? aTid = a.T != null ? WriteTensor(sig, a.T) : null;
                using var cmd = P("INSERT INTO node_attr(node_id,name,type,i,f,s,ints,floats,tensor_id) VALUES($id,$n,$t,$i,$f,$s,$ii,$ff,$tt)",
                    ("$id", id), ("$n", a.Name ?? ""), ("$t", (int)a.Type), ("$i", a.I), ("$f", a.F),
                    ("$s", a.S != null ? a.S.ToStringUtf8() : null), ("$ii", string.Join(",", a.Ints)), ("$ff", string.Join(",", a.Floats)), ("$tt", aTid));
                cmd.ExecuteNonQuery();
            }
            sigNodes++;
        }
        using (var cmd = P("INSERT INTO signature(sig,role,nodes,tensors,inputs,outputs) VALUES($sig,$r,$n,$t,$i,$o)",
            ("$sig", sig), ("$r", role), ("$n", sigNodes), ("$t", g.Initializer.Count), ("$i", g.Input.Count), ("$o", g.Output.Count))) cmd.ExecuteNonQuery();
        totalNodes += sigNodes;
    }
    tx.Commit();
    Console.WriteLine($"wrote {dbPath}  signatures={sigCount} nodes={totalNodes} tensors={tid}");
    return 0;
}

// Reverse of WriteModelDb: rebuild a runnable ModelProto from the SQLite model store (one signature).
// Lets the engine RUN a graph that exists only as a .db (the ONNX q4 export) — no .onnx/.onnx_data on disk.
// Extracted into ModelDb.cs (CRQ166) so it compiles into ss.exe too (this file's top-level Main can't).
static ModelProto LoadGraphFromDb(int sig, string dbPath) => ModelDb.LoadGraphFromDb(sig, dbPath);

// loaddb <model.db> [sig] — verify the db->GraphProto loader reconstructs the graph (op histogram + data presence).
static int LoadDbTest(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dp-onnx loaddb <model.db> [sig]"); return 1; }
    int sig = args.Length > 2 ? int.Parse(args[2]) : 0;
    var m = LoadGraphFromDb(sig, args[1]);
    Console.WriteLine($"nodes={m.Graph.Node.Count} inits={m.Graph.Initializer.Count} in={m.Graph.Input.Count} out={m.Graph.Output.Count}");
    foreach (var grp in m.Graph.Node.GroupBy(nd => nd.OpType).OrderByDescending(grp => grp.Count()))
        Console.WriteLine($"  {grp.Key,-28} {grp.Count()}");
    int noData = m.Graph.Initializer.Count(t => t.RawData == null || t.RawData.Length == 0);
    Console.WriteLine($"initializers with empty data: {noData}");
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

// db-stats: op triage straight off the .db — the op histogram + the backend-unfriendly ops, as queries.
// (analyze_onnx.py / trace_node.py collapse to SELECTs once the model is rows; per-op routing is node.backend.)
// generate <model.litertlm> <tokenizer.spm> "<prompt>" [maxNewTokens] — sovereign autoregressive decode on the
// dpx engine: tokenize -> per token (sig2 embed + sig3 PLE) -> sig10 decoder(+KV) -> logits -> argmax -> loop.
// KV fed at the baked 32003 capacity; param_tensor carries the position (the per-layer sliding-window math is
// baked into the graph). No ORT, no litert lib — gemma talking on our own interpreter, on the VOM.
static int Generate(string[] args)
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: dp-onnx generate <model.litertlm> <tokenizer.spm> \"<prompt>\" [maxNewTokens]"); return 1; }
    string path = args[1], spmPath = args[2], prompt = args[3];
    int maxNew = args.Length > 4 && int.TryParse(args[4], out var mn) ? mn : 32;

    var secs = LiteRtLm.ReadSections(path);
    Console.Error.WriteLine("loading sig2 embed / sig3 PLE / sig10 decoder (packed q2/q4/q8 — dequant deferred to kernel)…");
    var embed = new Dp(Tflite.ToModelProto(LiteRtLm.ReadSectionBytes(path, secs[2]), 0, out _, true, true));
    var ple   = new Dp(Tflite.ToModelProto(LiteRtLm.ReadSectionBytes(path, secs[3]), 0, out _, true, true));
    var dec   = new Dp(Tflite.ToModelProto(LiteRtLm.ReadSectionBytes(path, secs[10]), 0, out _, true, true));
    var tok   = new SentencePieceTokenizer(SpModelProto.Parse(File.ReadAllBytes(spmPath)));

    int bos = tok.FindPieceId("<bos>"); if (bos < 0) bos = 2;
    int eos = tok.FindPieceId("<eos>"); if (eos < 0) eos = 1;
    int eot = tok.FindPieceId("<end_of_turn>");
    bool rawMode = prompt.StartsWith("raw:", StringComparison.Ordinal);   // raw: skip the chat template (fast completion)
    string text = rawMode ? prompt.Substring(4) : $"<start_of_turn>user\n{prompt}<end_of_turn>\n<start_of_turn>model\n";
    var promptIds = new List<int> { bos };
    promptIds.AddRange(tok.Encode(text));
    Console.Error.WriteLine($"prompt = {promptIds.Count} tokens (raw={rawMode}); bos={bos} eos={eos} eot={eot}");

    // Initialize Native TensorArena for activations
    TensorArena.Initialize(1536L * 1024 * 1024);
    TensorArena.Active = true;

    const int KVCAP = 32003;
    int Dim(int l) => (l == 4 || l == 9 || l == 14) ? 512 : 256;   // global layers carry head_dim 512
    var kv = new Dictionary<string, Tensor>();
    for (int l = 0; l < 15; l++)
    {
        kv[$"decode_kv_cache_k_{l}:0"] = Tensor.AllocNative(1, 1, KVCAP, Dim(l));
        kv[$"decode_kv_cache_v_{l}:0"] = Tensor.AllocNative(1, 1, Dim(l), KVCAP);
    }
    // output:N -> the kv cache it updates (traced from the graph; lexicographic layer order)
    int[] kOrder = { 0, 1, 10, 11, 12, 13, 14, 2, 3, 4, 5, 6, 7, 8, 9 };
    var outToKv = new Dictionary<string, string>();
    for (int i = 0; i < 15; i++) { outToKv[$"StatefulPartitionedCall:{i + 1}"] = $"decode_kv_cache_k_{kOrder[i]}:0"; outToKv[$"StatefulPartitionedCall:{i + 16}"] = $"decode_kv_cache_v_{kOrder[i]}:0"; }

    var seq = new List<int>(promptIds);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"\n>>> {prompt}\n");
    for (int pos = 0; pos < promptIds.Count + maxNew; pos++)
    {
        long tid = seq[pos];
        if (pos < promptIds.Count) Console.Error.Write($"\rprefill {pos + 1}/{promptIds.Count}  ({DpxMem.WorkingSet / 1e9:F1}GB)    ");
        var emb = embed.Run(new() { ["embedder_token_ids:0"] = Tensor.I(new[] { tid }, 1, 1) })["StatefulPartitionedCall:0"];
        var pl  = ple.Run(new()   { ["per_layer_embedder_token_ids:0"] = Tensor.I(new[] { tid }, 1, 1) })["StatefulPartitionedCall:0"];
        var mask = TensorArena.AllocSpan(KVCAP); for (int j = 0; j < KVCAP; j++) mask[j] = (j <= pos) ? 1f : 0f;   // decode_mask is BOOL (sig10 type=6): 1=true=attend (0..pos), 0=false=masked. NOT additive.
        var feed = new Dictionary<string, Tensor>(kv)
        {
            ["decode_embeddings:0"] = emb,
            ["decode_per_layer_embeddings:0"] = pl,
            ["decode_input_pos:0"] = Tensor.I(new long[] { pos }, 1),
            ["decode_mask:0"] = Tensor.F(mask, 1, 1, 1, KVCAP),
            ["decode_param_tensor:0"] = Tensor.I(new long[] { pos, pos + 1, pos + 1, 0, 0, 0, 0 }, 1, 1, 1, 7),   // {start_index, end_index, end_index} KV-cache write-range (litert FillSingleBufferCacheParamTensor); NOT positions
        };
        var o = dec.Run(feed);
        foreach (var kvp in outToKv) kv[kvp.Value] = o[kvp.Key];   // carry the updated caches forward

        if (pos >= promptIds.Count - 1)   // past prefill: sample + emit
        {
            var logits = o["StatefulPartitionedCall:31"].AsF();
            int next = 0; float best = float.NegativeInfinity;
            for (int v = 0; v < logits.Length; v++) if (logits[v] > best) { best = logits[v]; next = v; }
            if (next == eos || next == eot) { Console.Error.WriteLine(" [end]"); break; }
            Console.Write(tok.Detokenize(new[] { next })); Console.Error.Write($"[id={next}]"); Console.Out.Flush();
            seq.Add(next);
        }
        emb.FreeNative();
        pl.FreeNative();
        feed["decode_mask:0"].FreeNative();
        var cacheOuts = new HashSet<string>(outToKv.Keys);
        foreach (var kvp in o)
        {
            if (!cacheOuts.Contains(kvp.Key))
            {
                kvp.Value?.FreeNative();
            }
        }
        TensorArena.Reset();
    }
    sw.Stop();
    Console.WriteLine($"\n\n-- {seq.Count - promptIds.Count} tokens / {sw.Elapsed.TotalSeconds:F1}s · {DpxMem.Snapshot()} --");
    Console.WriteLine($"Peak activation memory: {TensorArena.PeakOffset / 1e6:F2} MB");

    foreach (var kvp in kv.Values)
    {
        kvp.FreeNative();
    }
    TensorArena.Active = false;
    TensorArena.Release();

    return 0;
}

// gen-onnx <embed.db> <decoder.db> <tokenizer.spm> "<prompt>" [maxNew] — sovereign Gemma-4 E2B decode straight off
// the q4 ONNX export, no ORT/litert: embed(input_ids) -> inputs_embeds + per_layer_inputs; decoder(+position/mask/
// past_kv) -> logits + present_kv; greedy argmax; carry present.N -> past_key_values.N (DYNAMIC cache, no baked cap).
// This is the de-obfuscated KV contract litert hides behind a fixed 32003-slot buffer: 15 caches shared across 35L.
static int GenOnnx(string[] args)
{
    if (args.Length < 5) { Console.Error.WriteLine("usage: dp-onnx gen-onnx <embed.db> <decoder.db> <tokenizer.spm> \"<prompt>\" [maxNew]"); return 1; }
    string embDb = args[1], decDb = args[2], spmPath = args[3], prompt = args[4];
    int maxNew = args.Length > 5 && int.TryParse(args[5], out var mn) ? mn : 64;

    Console.Error.WriteLine("loading embed + decoder graphs from .db (q4, dequant deferred to the kernel)…");
    Dp.ActiveModelDbPath = decDb;
    TensorArena.Active = true;   // activations on the native off-GC arena (the proto-Sub-VOM); real Vom.Alloc-region + Spawn-SubGraph ownership port follows the correctness proof
    var embed = new Dp(LoadGraphFromDb(0, embDb));
    var dec   = new Dp(LoadGraphFromDb(0, decDb));
    var tok   = new SentencePieceTokenizer(SpModelProto.Parse(File.ReadAllBytes(spmPath)));

    int bos = tok.FindPieceId("<bos>"); if (bos < 0) bos = 2;
    int eos = tok.FindPieceId("<eos>"); if (eos < 0) eos = 1;
    int eot = tok.FindPieceId("<end_of_turn>");
    bool raw = prompt.StartsWith("raw:", StringComparison.Ordinal);
    string text = raw ? prompt.Substring(4) : $"<start_of_turn>user\n{prompt}<end_of_turn>\n<start_of_turn>model\n";
    var ids = new List<int> { bos }; ids.AddRange(tok.Encode(text));
    Console.Error.WriteLine($"prompt = {ids.Count} tokens (raw={raw}); bos={bos} eos={eos} eot={eot}");

    const int L = 15;                                              // cached KV layers (past_key_values.0..14); 15..34 share them in-graph
    int KvDim(int l) => (l == 4 || l == 9 || l == 14) ? 512 : 256; // global layers carry head_dim 512
    var past = new Dictionary<string, Tensor>();
    for (int l = 0; l < L; l++)
    { past[$"past_key_values.{l}.key"]   = Tensor.F(Array.Empty<float>(), 1, 1, 0, KvDim(l));
      past[$"past_key_values.{l}.value"] = Tensor.F(Array.Empty<float>(), 1, 1, 0, KvDim(l)); }

    var seq = new List<int>(ids); int pastLen = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"\n>>> {prompt}\n");
    for (int step = 0; step < maxNew; step++)
    {
        int[] cur = step == 0 ? seq.ToArray() : new[] { seq[^1] };   // prefill all tokens, then one per step
        int S = cur.Length, totalSeq = pastLen + S;
        var e = embed.Run(new() { ["input_ids"] = Tensor.I(Array.ConvertAll(cur, t => (long)t), 1, S) });
        var posArr = new long[S]; for (int i = 0; i < S; i++) posArr[i] = pastLen + i;
        var amask = new long[totalSeq]; for (int i = 0; i < totalSeq; i++) amask[i] = 1;
        var feed = new Dictionary<string, Tensor>(past)
        {
            ["inputs_embeds"]     = e["inputs_embeds"],
            ["per_layer_inputs"]  = e["per_layer_inputs"],
            ["position_ids"]      = Tensor.I(posArr, 1, S),
            ["attention_mask"]    = Tensor.I(amask, 1, totalSeq),
            ["num_logits_to_keep"]= Tensor.I(new long[] { 1 }),
        };
        var o = dec.Run(feed);
        for (int l = 0; l < L; l++)
        { past[$"past_key_values.{l}.key"]   = o[$"present.{l}.key"];
          past[$"past_key_values.{l}.value"] = o[$"present.{l}.value"]; }
        pastLen = totalSeq;
        if (step == 0) Console.Error.Write($"prefill {S} tok ({DpxMem.WorkingSet / 1e9:F1}GB)  ");

        var logits = o["logits"]; var lf = logits.AsF(); int V = logits.Shape[^1]; int last = (int)(logits.Count / V) - 1;
        int next = 0; float best = float.NegativeInfinity;
        for (int v = 0; v < V; v++) { float val = lf[last * V + v]; if (val > best) { best = val; next = v; } }
        if (next == eos || next == eot) { Console.Error.WriteLine(" [end]"); break; }
        Console.Write(tok.Detokenize(new[] { next })); Console.Error.Write($"[id={next}]"); Console.Out.Flush();
        seq.Add(next);
    }
    sw.Stop();
    Console.WriteLine($"\n\n-- {seq.Count - ids.Count} tokens / {sw.Elapsed.TotalSeconds:F1}s · {DpxMem.Snapshot()} · arena peak {TensorArena.PeakOffset / 1e6:F0}MB --");
    TensorArena.Active = false;
    return 0;
}

// tokenize <spm> "<text>" — diagnose the SentencePiece tokenizer (piece scores, segmentation) without a model.
static int Tokenize(string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: dp-onnx tokenize <spm> \"<text>\""); return 1; }
    var spm = SpModelProto.Parse(File.ReadAllBytes(args[1]));
    Console.WriteLine($"pieces={spm.Pieces.Count}");
    for (int i = 0; i < 6 && i < spm.Pieces.Count; i++)
        Console.WriteLine($"  piece[{i}] \"{spm.Pieces[i].Piece}\" score={spm.Pieces[i].Score} type={spm.Pieces[i].Type}");
    var tok = new SentencePieceTokenizer(spm);
    foreach (var id in new[] { 7001, 506, 1000, 50000, 200000 })
        if (id < spm.Pieces.Count) Console.WriteLine($"  REAL piece[{id}] \"{spm.Pieces[id].Piece}\" score={spm.Pieces[id].Score} type={spm.Pieces[id].Type}");
    Console.WriteLine($"  id('▁France')={tok.FindPieceId("▁France")}  id('France')={tok.FindPieceId("France")}  id('▁the')={tok.FindPieceId("▁the")}");
    var ids = tok.Encode(args[2]);
    Console.WriteLine($"text=\"{args[2]}\" -> {ids.Count} tokens:");
    foreach (var id in ids) Console.Write($"[{id}:{tok.Detokenize(new[] { id })}]");
    Console.WriteLine();
    return 0;
}

// dumpsg <model.litertlm> <section> <subgraph> — raw subgraph structure for composite-recursion debugging.
static int DumpSg(string[] args)
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: dp-onnx dumpsg <model.litertlm> <section> <subgraph>"); return 1; }
    var secs = LiteRtLm.ReadSections(args[1]);
    int sec = int.Parse(args[2]), sgix = int.Parse(args[3]);
    Tflite.DumpSubgraph(LiteRtLm.ReadSectionBytes(args[1], secs[sec]), sgix);
    return 0;
}

static int DbStats(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dp-onnx db-stats <model.db>"); return 1; }
    using var c = new SqliteConnection($"Data Source={args[1]}");
    c.Open();
    long Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? (object)0L); }
    void Each(string sql, Action<string, long> row) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; using var r = cmd.ExecuteReader(); while (r.Read()) row(r.GetString(0), r.GetInt64(1)); }
    Console.WriteLine($"nodes={Scalar("SELECT COUNT(*) FROM node")}  distinct ops={Scalar("SELECT COUNT(DISTINCT op_type) FROM node")}  tensors={Scalar("SELECT COUNT(*) FROM tensor")}");
    Console.WriteLine("-- op histogram --");
    Each("SELECT op_type, COUNT(*) c FROM node GROUP BY op_type ORDER BY c DESC", (op, n) => Console.WriteLine($"  {op,-24} {n}"));
    const string Risky = "'STFT','DFT','RFFT','IRFFT','FFT','Mel','MelWeightMatrix','ScatterND','GridSample','NonZero','Loop','If','Scan','Pow'";
    Console.WriteLine($"-- backend-risky (HTP can't delegate / VTCM): {Scalar($"SELECT COUNT(*) FROM node WHERE op_type IN ({Risky})")} node(s) --");
    Each($"SELECT op_type, COUNT(*) c FROM node WHERE op_type IN ({Risky}) GROUP BY op_type ORDER BY c DESC", (op, n) => Console.WriteLine($"  RISKY {op,-18} {n}"));
    return 0;
}

static int Usage() { Console.WriteLine("usage: dp-onnx selftest | probe <model.onnx|.tflite|.litertlm> | run <model.onnx> [--inputs <dir>] [--out <wav>] | run <model.litertlm> --section <N> | db <model.onnx|.litertlm> <out.db> [--section <N>] | addoutput <in> <out> <tensorName...> | emit <model.onnx> <out.cs> | gpu-tune <model.db>"); return 1; }

// compile front-half (#69 / shared with the #92 D3D12 frame-graph): walk the ONNX graph and emit a
// straight-line C# Tier-1 forward pass. Design (fixes the 5 blockers in the H1 draft):
//  - calls Dp.Dispatch per node  -> covers all 53 ops for free (no partial per-op switch);
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
    sb.AppendLine("// AUTO-EMITTED by `dp-onnx emit` — Tier-1 straight-line forward pass (calls Dp.Dispatch).");
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using Onnx;");
    sb.AppendLine("using Subsystem.Dpx;");
    sb.AppendLine("namespace Subsystem.Dpx.Compiled {");
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
        sb.AppendLine($"      {{ var o = Dp.Dispatch(n{i}, new Tensor[]{{ {ins} }});");
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
    var cpu = Dp.Dispatch(new NodeProto { OpType = "MatMul" }, new[] { Tensor.F(A, M, K), Tensor.F(B, K, N) })[0].Fp;
    double maxd = 0; for (int i = 0; i < C.Length; i++) maxd = Math.Max(maxd, Math.Abs(C[i] - cpu[i]));
    Console.WriteLine($"dp-onnx -> GPU dpgpu_gemm [{M}x{K}]@[{K}x{N}]  vs CPU Dp.MatMul:  max|diff|={maxd:E3}  =>  {(maxd < 1e-3 ? "MATCH — dp-onnx dispatched a MatMul to the D3D12 GPU; the mount works" : "MISMATCH")}");
    return maxd < 1e-3 ? 0 : 2;
}

// gpu-test-q4 [dxil]: dispatch MatMulNBits's q4 contraction to the GPU (GemmQ4) and diff vs the CPU oracle
// (Dp.MatMulNBits, reflected — the SAME kernel test.dpx.q4-packing-order.ps1 pins). Fixture mirrors that test:
// SEQUENTIAL nibble layout (byte k>>1, low nibble = even k), K=64 (2 blocks of 32), N=2 rows, zp defaulted to 8.
static int GpuTestQ4(string[] args)
{
    int Kd = 64, Nd = 2, bs = 32, nBlk = 2;
    byte[] row0 = System.Linq.Enumerable.Repeat(new byte[] { 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE }, 4).SelectMany(a => a).ToArray();
    byte[] row1 = System.Linq.Enumerable.Repeat(new byte[] { 0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5, 0x96, 0x87, 0x78, 0x69, 0x5A, 0x4B, 0x3C, 0x2D, 0x1E, 0x0F }, 2).SelectMany(a => a).ToArray();
    byte[] packed = row0.Concat(row1).ToArray();
    float[] scales = { 1.0f, 0.5f, 2.0f, 1.0f };   // sc[n*nBlk+b]

    var ident = new float[Kd * Kd]; for (int i = 0; i < Kd; i++) ident[i * Kd + i] = 1.0f;   // A = I(64)

    string dxilPath = args.Length > 1 ? args[1] : @"S:\qnn-project\workspace\onnx-interp\_gpu\gemm_q4.dxil";
    byte[] dxil = File.Exists(dxilPath) ? File.ReadAllBytes(dxilPath) : Array.Empty<byte>();
    var c = new float[Kd * Nd];
    int rc = Gpu.dpgpu_gemm_q4(ident, packed, scales, Array.Empty<byte>(), c, (uint)Kd, (uint)Nd, (uint)Kd, (uint)bs, false, dxil);
    if (rc != 0) { Console.WriteLine($"dpgpu_gemm_q4 failed rc={rc}"); return 1; }

    // CPU oracle: same fixture through Dp.MatMulNBits (defZp=8, per-block scale sc[n*nBlk+b])
    int DecodeSeq(byte[] b, int k) => (b[k >> 1] >> ((k & 1) * 4)) & 0xF;
    var rows = new[] { row0, row1 };
    double maxd = 0;
    for (int nn = 0; nn < Nd; nn++)
        for (int k = 0; k < Kd; k++)
        {
            float s = scales[nn * nBlk + (k / bs)];
            float exp = (DecodeSeq(rows[nn], k) - 8.0f) * s;
            float got = c[k * Nd + nn];
            maxd = Math.Max(maxd, Math.Abs(got - exp));
        }
    Console.WriteLine($"GPU dpgpu_gemm_q4 [{Kd}x{Kd}]@dequant(q4)[{Kd}x{Nd}]  vs CPU oracle decode:  max|diff|={maxd:E3}  =>  {(maxd < 1e-3 ? "MATCH — GPU q4 GEMM dequants+multiplies+accumulates the SEQUENTIAL nibble layout correctly" : "MISMATCH")}");
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
    sw.Restart(); var cpu = Dp.Dispatch(new NodeProto { OpType = "MatMul" }, new[] { Tensor.F(A, S, S), Tensor.F(B, S, S) })[0].Fp; sw.Stop(); double cpus = sw.Elapsed.TotalSeconds;
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
// Tier-1 reuses Dp's kernels, so this MUST match `run` bit-for-bit — the parity proof for the compile path.
int RunCompiled(string[] args)
{
    if (args.Length < 3) return Usage();
    string dll = args[1], onnx = args[2], inputsDir = null, outPath = null;
    for (int i = 3; i < args.Length; i++) switch (args[i]) { case "--inputs": inputsDir = args[++i]; break; case "--out": outPath = args[++i]; break; }
    var g = ModelProto.Parser.ParseFrom(File.ReadAllBytes(onnx)).Graph;
    var W = new Dictionary<string, Tensor>();
    foreach (var init in g.Initializer) W[init.Name] = Dp.FromProto(init);
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input) { if (g.Initializer.Any(i => i.Name == vi.Name)) continue; feed[vi.Name] = LoadBin(Path.Combine(inputsDir, vi.Name + ".bin")); }

    var asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(dll));
    var t = asm.GetType("Subsystem.Dpx.Compiled.ModelInstance") ?? throw new Exception("ModelInstance type not found in " + dll);
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
    var outs = new Dp(model).Run(new() { ["X"] = X });
    var Y = outs["Y"];
    // expected: X@W = [[1+3,2+3],[4+6,5+6]] = [[4,5],[10,11]]; +B = [[4.5,-5],[10.5,1]]; relu => [[4.5,0],[10.5,1]]
    var exp = new[] { 4.5f, 0f, 10.5f, 1f };
    double max = 0; for (int i = 0; i < 4; i++) max = Math.Max(max, Math.Abs(Y.Fp[i] - exp[i]));
    Console.WriteLine($"Y = [{string.Join(", ", Y.Fp)}]  shape=[{string.Join(",", Y.Shape)}]");
    Console.WriteLine($"expected [4.5, 0, 10.5, 1]  max|diff|={max:E2}  =>  {(max < 1e-5 ? "PASS" : "FAIL")}");
    return max < 1e-5 ? 0 : 1;
}

// LITERTLM container probe: list the sections, then for each TFLiteModel section enumerate the tflite
// operator set and map it onto dp-onnx kernels (the coverage receipt the .onnx `probe` prints for ONNX).
static int ProbeLiteRtLm(string path)
{
    var secs = LiteRtLm.ReadSections(path);
    Console.WriteLine(path);
    Console.WriteLine($"LITERTLM container: {secs.Count} sections");
    for (int i = 0; i < secs.Count; i++)
    {
        var s = secs[i];
        Console.WriteLine($"  [{i,2}] {LiteRtLm.TypeName(s.DataType),-18} {(s.End - s.Begin) / 1e6,9:F1} MB   [{s.Begin}..{s.End})");
    }
    for (int i = 0; i < secs.Count; i++)
    {
        var s = secs[i];
        if (s.DataType != 3) continue;   // TFLiteModel
        Console.WriteLine($"\n-- [{i}] TFLiteModel --");
        try { ProbeTfliteBytes(LiteRtLm.ReadSectionBytes(path, s), $"[{i}]"); }
        catch (Exception ex) { Console.WriteLine($"  parse failed: {ex.GetType().Name} {ex.Message}"); }
    }
    return 0;
}

// Translate ONE .litertlm TFLiteModel section into Onnx.ModelProto and RUN it through Dp, off a synthesized
// feed (the probe->executable proof: the sovereign tflite reader produces a graph the dp-onnx engine executes).
static int RunSection(string path, int sectionIx)
{
    var secs = LiteRtLm.ReadSections(path);
    if (sectionIx < 0 || sectionIx >= secs.Count) { Console.Error.WriteLine($"section {sectionIx} out of range (0..{secs.Count - 1})"); return 1; }
    var s = secs[sectionIx];
    if (s.DataType != 3) { Console.Error.WriteLine($"section [{sectionIx}] is {LiteRtLm.TypeName(s.DataType)}, not TFLiteModel"); return 1; }

    var tfl = LiteRtLm.ReadSectionBytes(path, s);
    var model = Tflite.ToModelProto(tfl, 0, out string summary);
    var g = model.Graph;
    Console.WriteLine($"-- [{sectionIx}] TFLiteModel -> Onnx.ModelProto --");
    Console.Write(summary);

    // synthesize a safe feed: a dynamic / non-positive dim -> SEQ; int inputs -> small in-range indices; float -> 0.
    const int SEQ = 8;
    var feed = new Dictionary<string, Tensor>();
    foreach (var vi in g.Input)
    {
        var tt = vi.Type.TensorType;
        var dims = tt.Shape.Dim.Select(d => d.DimValue <= 0 ? SEQ : (int)d.DimValue).ToArray();
        if (dims.Length == 0) dims = new[] { 1 };
        long n = 1; foreach (var d in dims) n *= d;
        bool isInt = tt.ElemType is 6 or 7 or 9;
        if (isInt) { var a = new long[n]; for (long k = 0; k < n; k++) a[k] = k % 4; feed[vi.Name] = Tensor.I(a, dims); }
        else feed[vi.Name] = Tensor.F(new float[n], dims);
        Console.WriteLine($"  feed {vi.Name} [{string.Join(",", dims)}] {(isInt ? "int64" : "float")}");
    }

    int ran = 0; Dictionary<string, Tensor> outs = null; string err = null;
    try { outs = new Dp(model).Run(feed, onNode: (_, __, ___) => ran++); }
    catch (Exception ex) { err = $"[{ex.GetType().Name}] {ex.Message}"; }
    if (err != null) { Console.WriteLine($"[{sectionIx}] FAILED after {ran}/{g.Node.Count} nodes: {err}"); return 1; }

    bool finite = true; var shapes = new List<string>();
    foreach (var kv in outs)
    {
        var t = kv.Value; shapes.Add($"{kv.Key}[{string.Join(",", t.Shape)}]");
        foreach (var v in t.AsF()) if (float.IsNaN(v) || float.IsInfinity(v)) { finite = false; break; }
    }
    Console.WriteLine($"[{sectionIx}] RAN nodes={ran} inputs={g.Input.Count} outputs={outs.Count} finite={finite}");
    Console.WriteLine($"  outputs: {string.Join("  ", shapes)}");
    Console.WriteLine($"  mem: {DpxMem.Snapshot()}");
    return 0;
}

static void ProbeTfliteBytes(byte[] tfl, string label)
{
    var (hist, subgraphs, tensors, ops) = Tflite.OpHistogram(tfl);
    int distinct = hist.Count;
    int impl = hist.Keys.Count(k => { var o = Tflite.MapToOnnx(k); return o != null && Dp.Implemented.Contains(o); });
    Console.WriteLine($"  {label}  {tfl.Length / 1e6:F1} MB  subgraphs={subgraphs} tensors={tensors} ops={ops} distinct={distinct}  mapped-types={impl}/{distinct}");
    foreach (var kv in hist.OrderByDescending(k => k.Value))
    {
        var o = Tflite.MapToOnnx(kv.Key);
        bool ok = o != null && Dp.Implemented.Contains(o);
        Console.WriteLine($"    {kv.Value,5}  {kv.Key,-24} {(o == null ? "—" : "-> " + o),-18} {(ok ? "" : "MISSING")}");
    }
}

static int Probe(string path, bool stopOnMissing = true)
{
    var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
    if (ext == ".litertlm") return ProbeLiteRtLm(path);
    if (ext == ".tflite") { ProbeTfliteBytes(System.IO.File.ReadAllBytes(path), System.IO.Path.GetFileName(path)); return 0; }
    var model = ModelProto.Parser.ParseFrom(File.ReadAllBytes(path));
    var g = model.Graph;
    // op histogram
    var hist = new Dictionary<string, int>();
    foreach (var n in g.Node) hist[n.OpType] = hist.GetValueOrDefault(n.OpType) + 1;
    var impl = Dp.Implemented;
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
    try { new Dp(model).Run(feed, onNode: (_, __, ___) => ran++); }
    catch (NotImplementedException ex) { stoppedAt = ex.Message; }
    catch (Exception ex) { stoppedAt = $"[{ex.GetType().Name}] {ex.Message}"; }
    Console.WriteLine(stoppedAt == null
        ? $"RAN ALL {ran} nodes ✓"
        : $"ran {ran}/{g.Node.Count} nodes, stopped at: {stoppedAt}");
    return 0;
}

// ----- run with real inputs (the validation pivot: load kokoro-tts's dumped tensors, run to
//       completion, diff the waveform against ORT's oracle.bin) -----
int Run(string[] args)
{
    string path = null, inputsDir = null, outPath = null, dumpNode = null, compareDir = null, injectNode = null; bool trace = false; int stopAfter = 0; int sectionIx = -1;
    for (int i = 1; i < args.Length; i++)
        switch (args[i])
        {
            case "--section": sectionIx = int.Parse(args[++i]); break;   // translate+run one .litertlm TFLiteModel section through Dp
            case "--inputs": inputsDir = args[++i]; break;
            case "--out": outPath = args[++i]; break;
            case "--trace": trace = true; break;
            case "--stop-after": stopAfter = int.Parse(args[++i]); break;
            case "--dump-node": dumpNode = args[++i]; break;
            case "--compare": compareDir = args[++i]; break;
            case "--inject": injectNode = args[++i]; break;   // replace matching nodes' output w/ oracle (needs --compare <dir>)
            case "--gpu-matmul": Dp.UseGpuMatMul = true; break;   // offload every MatMul to dpgpu.dll (D3D12); CPU fallback on mount failure
            case "--gpu-matmulnbits": Dp.UseGpuMatMulNBits = true; break;   // offload the q4 MatMulNBits contraction to the GPU (GemmQ4); CPU fallback on mount failure
            case "--prof": Dp.Profile = true; break;   // per-op-type wall-time breakdown
            case "--drop": Dp.DropP = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;   // stale-read drop prob on residual merges
            case "--drop-scope": Dp.DropScope = args[++i]; break;   // gate drops to node names containing this (e.g. "generator")
            default: if (path == null) path = args[i]; break;
        }
    if (path == null) return Usage();
    if (sectionIx >= 0) return RunSection(path, sectionIx);             // sovereign tflite-section -> ModelProto -> Dp
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
            if (t0 != null && t0.Count <= 4000000) { var f = t0.AsF(); double a = 0; for (long i = 0; i < f.Length; i++) a += (double)f[(int)i] * f[(int)i]; rms = $" rms={(f.Length > 0 ? Math.Sqrt(a / f.Length) : 0):F4}"; }
            string vals = (t0 != null && t0.Count <= 64) ? "  = [" + (t0.IsInt ? string.Join(",", t0.Ip) : string.Join(",", Array.ConvertAll(t0.AsF().ToArray(), v => v.ToString("F3")))) + "]" : "";
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
    try { outs = new Dp(model).Run(feed, onNode: cb); }
    catch (NotImplementedException ex) { stoppedAt = ex.Message; }
    catch (Exception ex) { stoppedAt = $"[{ex.GetType().Name}] {ex.Message}"; }
    sw.Stop();

    if (stoppedAt != null) { Console.WriteLine($"ran {ran}/{g.Node.Count} nodes, stopped at: {stoppedAt}"); return 1; }
    Console.WriteLine($"RAN ALL {ran} nodes ✓  ({sw.Elapsed.TotalSeconds:F2}s)");
    if (Dp.Profile)
    {
        double tot = 0; foreach (var kv in Dp.Prof) tot += kv.Value.ms;
        Console.WriteLine($"\nPER-OP PROFILE (wall, {tot:F0} ms total dispatch):");
        foreach (var kv in Dp.Prof.OrderByDescending(k => k.Value.ms))
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
      if (Dp.DropP > 0 || nan > 0) Console.WriteLine($"  STALE-READ TEST: dropped {Dp.Dropped} residual merges (--drop {Dp.DropP});  NaN/Inf samples: {nan}/{wav.Length}"); }
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
int Stream(string[] args)
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
    var interp = new Dp(mp);
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
        pieces.Add(wav.ToArray());
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
        new Dp(model).Run(feed, onNode: (nd, outs, env) =>
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

static void WriteWav(string path, ReadOnlySpan<float> s, int sr)
{
    using var bw = new BinaryWriter(File.Create(path));
    int dataBytes = s.Length * 2;
    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataBytes); bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(dataBytes);
    foreach (var f in s) bw.Write((short)Math.Round(Math.Clamp(f, -1f, 1f) * 32767));
}

static double Rms(ReadOnlySpan<float> s) { double a = 0; foreach (var f in s) a += (double)f * f; return s.Length > 0 ? Math.Sqrt(a / s.Length) : 0; }
static double Peak(ReadOnlySpan<float> s) { double p = 0; foreach (var f in s) p = Math.Max(p, Math.Abs(f)); return p; }

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

