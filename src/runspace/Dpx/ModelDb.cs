#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
namespace Subsystem.Dpx
{
    // Reverse of Program.cs's WriteModelDb: rebuild a runnable ModelProto from the SQLite model store
    // (one signature). Lets the engine RUN a graph that exists only as a .db (the ONNX q4 export) — no
    // .onnx/.onnx_data on disk. Tensors referenced by node_attr.tensor_id are attribute tensors; every
    // other tensor row is a graph initializer. Split out of Program.cs (CRQ166) — the dev-CLI's top-level
    // Main can't compile into ss.exe (CS0017), so the loader lives here instead, compiled into both.
    public static class ModelDb
    {
        public static ModelProto LoadGraphFromDb(int sig, string dbPath)
        {
            using var c = new SqliteConnection($"Data Source={dbPath}");
            c.Open();
            SqliteCommand Q(string sql, params (string, object)[] ps)
            { var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v); return cmd; }
            long[] Dims(string s) => string.IsNullOrEmpty(s) ? System.Array.Empty<long>()
                : s.Split(',').Where(x => x.Length > 0).Select(long.Parse).ToArray();
            bool HasCol(string tbl, string col)
            { using var cmd = c.CreateCommand(); cmd.CommandText = $"PRAGMA table_info({tbl})"; using var r = cmd.ExecuteReader();
              while (r.Read()) if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase)) return true; return false; }
            bool HasTable(string tbl)
            { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n"; cmd.Parameters.AddWithValue("$n", tbl); return cmd.ExecuteScalar() != null; }
            bool ms = HasCol("node", "sig");                 // litert store filters by sig; the ONNX store is single-graph (no sig column)
            bool hasBlob = HasTable("tensor_blob");
            string W = ms ? " WHERE sig=$s" : "";
            (string, object)[] S = ms ? new (string, object)[] { ("$s", sig) } : System.Array.Empty<(string, object)>();
            byte[] Blob(long id)
            { if (!hasBlob) return null; using var r = Q("SELECT data FROM tensor_blob WHERE tensor_id=$id ORDER BY ord", ("$id", id)).ExecuteReader();
              using var buf = new System.IO.MemoryStream(); while (r.Read()) if (!r.IsDBNull(0)) { var b = (byte[])r.GetValue(0); buf.Write(b, 0, b.Length); } return buf.Length > 0 ? buf.ToArray() : null; }

            var attrTids = new HashSet<long>();
            using (var r = Q("SELECT tensor_id FROM node_attr WHERE tensor_id IS NOT NULL").ExecuteReader())
                while (r.Read()) attrTids.Add(r.GetInt64(0));

            var g = new GraphProto();
            var tById = new Dictionary<long, TensorProto>();
            int nullBlobs = 0;
            using (var r = Q($"SELECT id,name,dtype,dims,data FROM tensor{W}", S).ExecuteReader())
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    var t = new TensorProto { Name = r.IsDBNull(1) ? "" : r.GetString(1), DataType = r.GetInt32(2) };
                    foreach (var d in Dims(r.IsDBNull(3) ? "" : r.GetString(3))) t.Dims.Add(d);
                    byte[] data = r.IsDBNull(4) ? Blob(id) : (byte[])r.GetValue(4);
                    if (data == null || data.Length == 0) nullBlobs++; else t.RawData = new ByteString(data);
                    tById[id] = t;
                    if (!attrTids.Contains(id)) g.Initializer.Add(t);
                }
            if (nullBlobs > 0) Console.Error.WriteLine($"  WARN: {nullBlobs} tensor(s) had no data in {dbPath}");

            using (var r = Q($"SELECT kind,name,elem_type,shape FROM graph_io{W}", S).ExecuteReader())
                while (r.Read())
                {
                    var sh = new TensorShapeProto();
                    var shape = r.IsDBNull(3) ? "" : r.GetString(3);
                    if (shape.Length > 0) foreach (var d in shape.Split(','))
                        sh.Dim.Add(long.TryParse(d, out var dv)
                            ? new TensorShapeProto.Dimension { DimValue = dv }
                            : new TensorShapeProto.Dimension { DimParam = d });
                    var vi = new ValueInfoProto { Name = r.IsDBNull(1) ? "" : r.GetString(1),
                        Type = new TypeProto { TensorType = new TypeProto.Types.Tensor { ElemType = r.GetInt32(2), Shape = sh } } };
                    if (r.GetString(0) == "in") g.Input.Add(vi); else g.Output.Add(vi);
                }

            var nodeIds = new List<(long id, string op, string name)>();
            using (var r = Q($"SELECT id,op_type,name FROM node{W} ORDER BY ord", S).ExecuteReader())
                while (r.Read()) nodeIds.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
            foreach (var (id, op, name) in nodeIds)
            {
                var nd = new NodeProto { OpType = op, Name = name };
                var ins = new SortedDictionary<int, string>(); var outs = new SortedDictionary<int, string>();
                using (var r = Q("SELECT slot,kind,value_name FROM node_io WHERE node_id=$id", ("$id", id)).ExecuteReader())
                    while (r.Read()) { var d = r.GetString(1) == "in" ? ins : outs; d[r.GetInt32(0)] = r.IsDBNull(2) ? "" : r.GetString(2); }
                foreach (var kv in ins) nd.Input.Add(kv.Value);
                foreach (var kv in outs) nd.Output.Add(kv.Value);
                using (var r = Q("SELECT name,type,i,f,s,ints,floats,tensor_id FROM node_attr WHERE node_id=$id", ("$id", id)).ExecuteReader())
                    while (r.Read())
                    {
                        var a = new AttributeProto { Name = r.IsDBNull(0) ? "" : r.GetString(0), Type = (AttributeProto.Types.AttributeType)r.GetInt32(1) };
                        if (!r.IsDBNull(2)) a.I = r.GetInt64(2);
                        if (!r.IsDBNull(3)) a.F = (float)r.GetDouble(3);
                        if (!r.IsDBNull(4)) a.S = new ByteString(System.Text.Encoding.UTF8.GetBytes(r.GetString(4)));
                        if (!r.IsDBNull(5)) foreach (var x in r.GetString(5).Split(',')) if (x.Length > 0) a.Ints.Add(long.Parse(x));
                        if (!r.IsDBNull(6)) foreach (var x in r.GetString(6).Split(',')) if (x.Length > 0) a.Floats.Add(float.Parse(x));
                        if (!r.IsDBNull(7) && tById.TryGetValue(r.GetInt64(7), out var at)) a.T = at;
                        nd.Attribute.Add(a);
                    }
                g.Node.Add(nd);
            }
            Console.Error.WriteLine($"loaded sig{sig} from {dbPath}: {g.Node.Count} nodes, {g.Initializer.Count} initializers, {g.Input.Count} inputs, {g.Output.Count} outputs");
            return new ModelProto { Graph = g };
        }

        // Open a named face (embed/decoder/vision/audio) from a CONSOLIDATED db: resolve face->sig through the
        // manifest (or the signature.role fallback), then reuse the sig-filtered loader. The old per-file path
        // — LoadGraphFromDb(0, faceDb) — keeps working; this is the second rung, not a replacement.
        public static ModelProto LoadGraphFromDb(string face, string dbPath)
            => LoadGraphFromDb(ResolveFaceSig(face, dbPath), dbPath);

        // Manifest-first face resolution: the consolidated db's `manifest` table maps face->sig; a db that only
        // carries `signature` rows (role=face) resolves there instead. Throws if the face is absent so a caller
        // never silently loads the wrong graph.
        static int ResolveFaceSig(string face, string dbPath)
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            c.Open();
            bool Has(string tbl)
            { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n"; cmd.Parameters.AddWithValue("$n", tbl); return cmd.ExecuteScalar() != null; }
            long? Pick(string sql)
            { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$f", face); var o = cmd.ExecuteScalar(); return o == null || o is DBNull ? (long?)null : Convert.ToInt64(o); }
            long? sig = Has("manifest") ? Pick("SELECT sig FROM manifest WHERE face=$f LIMIT 1") : null;
            if (sig == null && Has("signature")) sig = Pick("SELECT sig FROM signature WHERE role=$f LIMIT 1");
            if (sig == null) throw new KeyNotFoundException($"face '{face}' not found in {dbPath} (no manifest/signature row)");
            return (int)sig.Value;
        }

        // The face names carried by a consolidated db (manifest first, else the signature roles). Empty for an
        // old single-graph per-file db — the discovery path uses that emptiness to tell the two shapes apart.
        public static List<string> QueryFaces(string dbPath)
        {
            var faces = new List<string>();
            if (!File.Exists(dbPath)) return faces;
            try
            {
                using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                c.Open();
                bool Has(string tbl)
                { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n"; cmd.Parameters.AddWithValue("$n", tbl); return cmd.ExecuteScalar() != null; }
                string sql = Has("manifest") ? "SELECT face FROM manifest ORDER BY sig"
                           : Has("signature") ? "SELECT role FROM signature ORDER BY sig" : null;
                if (sql != null)
                { using var cmd = c.CreateCommand(); cmd.CommandText = sql; using var r = cmd.ExecuteReader();
                  while (r.Read()) if (!r.IsDBNull(0)) faces.Add(r.GetString(0)); }
            }
            catch (Exception ex) { Console.Error.WriteLine($"  WARN QueryFaces({dbPath}): {ex.Message}"); }
            return faces;
        }

        public const long DefaultChunkBytes = 256L * 1024 * 1024;   // tensor_blob chunk ceiling (< SQLite's 1GB blob cap)

        // Write one or more signatures (sig, role, graph) into the SQLite model store — the SINGLE writer for
        // the ONNX/litertlm export AND the consolidated multi-face db (invariant 9). node/tensor ids are GLOBAL
        // across signatures so node_attr.tensor_id stays unique; the `signature` table is the section index;
        // `sigs` is streamed (each graph written then dropped, so only one face is resident). A tensor larger
        // than chunkBytes is split into ordered `tensor_blob` rows (data column NULL); the loader reassembles
        // them transparently — this is what keeps the 1.17GB PLE embedding table from being dropped on re-export.
        public static int WriteModelDb(string dbPath, IEnumerable<(int sig, string role, GraphProto g)> sigs, long chunkBytes = DefaultChunkBytes)
        {
            if (chunkBytes <= 0) chunkBytes = DefaultChunkBytes;
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
CREATE TABLE tensor_blob(tensor_id INTEGER, ord INTEGER, data BLOB);
CREATE INDEX ix_tensor_blob ON tensor_blob(tensor_id, ord);
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
            SqliteCommand P(string sql, params (string, object)[] ps)
            {
                var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
                foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                return cmd;
            }
            int tid = 0, nid = 0, sigCount = 0, totalNodes = 0, chunkedTensors = 0;
            int WriteTensor(int sig, TensorProto t)
            {
                int id = tid++;
                byte[] data = t.RawData != null && t.RawData.Length > 0 ? t.RawData.ToByteArray() : TypedBytes(t);
                bool chunk = data != null && data.LongLength > chunkBytes;
                // Row is always present (id/sig/name/dtype/dims); an over-cap payload moves to tensor_blob (data NULL).
                using (var cmd = P("INSERT INTO tensor(id,sig,name,dtype,dims,data) VALUES($id,$sig,$n,$dt,$dm,$d)",
                    ("$id", id), ("$sig", sig), ("$n", t.Name ?? ""), ("$dt", t.DataType), ("$dm", string.Join(",", t.Dims)),
                    ("$d", chunk ? null : (object)data)))
                    cmd.ExecuteNonQuery();
                if (chunk)
                {
                    int off = 0, ord = 0;
                    while (off < data.Length)
                    {
                        int n = (int)Math.Min(chunkBytes, (long)(data.Length - off));
                        var slice = new byte[n];
                        Buffer.BlockCopy(data, off, slice, 0, n);
                        using (var cmd = P("INSERT INTO tensor_blob(tensor_id,ord,data) VALUES($id,$o,$d)", ("$id", id), ("$o", ord), ("$d", slice)))
                            cmd.ExecuteNonQuery();
                        off += n; ord++;
                    }
                    chunkedTensors++;
                }
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
                        object aTid = a.T != null ? WriteTensor(sig, a.T) : null;
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
            Console.WriteLine($"wrote {dbPath}  signatures={sigCount} nodes={totalNodes} tensors={tid} chunked={chunkedTensors} (chunk<= {chunkBytes / (1024 * 1024)}MB)");
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

        // manifest: (face, sig, sha256 of the source face db, quant tag, provenance file) — the consolidated db's
        // self-description of where each named graph came from. CREATE-IF so the row-write composes onto a db
        // that WriteModelDb already produced.
        public static void WriteManifestRow(string dbPath, string face, int sig, string sha256, string quant, string provenance)
        {
            using var c = new SqliteConnection($"Data Source={dbPath}");
            c.Open();
            using (var ddl = c.CreateCommand()) { ddl.CommandText = "CREATE TABLE IF NOT EXISTS manifest(face TEXT PRIMARY KEY, sig INTEGER, sha256 TEXT, quant TEXT, provenance TEXT)"; ddl.ExecuteNonQuery(); }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO manifest(face,sig,sha256,quant,provenance) VALUES($f,$sig,$sha,$q,$p)";
            cmd.Parameters.AddWithValue("$f", face);
            cmd.Parameters.AddWithValue("$sig", sig);
            cmd.Parameters.AddWithValue("$sha", (object)sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", (object)quant ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object)provenance ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // tokenizer: the .spm bytes ride IN the db (self-describing model, no loose sidecar). name = the source
        // file leaf so a caller can tell which tokenizer it is.
        public static void WriteTokenizerBlob(string dbPath, string name, byte[] spm)
        {
            using var c = new SqliteConnection($"Data Source={dbPath}");
            c.Open();
            using (var ddl = c.CreateCommand()) { ddl.CommandText = "CREATE TABLE IF NOT EXISTS tokenizer(name TEXT PRIMARY KEY, data BLOB)"; ddl.ExecuteNonQuery(); }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO tokenizer(name,data) VALUES($n,$d)";
            cmd.Parameters.AddWithValue("$n", name ?? "");
            cmd.Parameters.AddWithValue("$d", (object)spm ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // Read the .spm tokenizer bytes back out (null name => the first/only row). Null if the db carries none.
        public static byte[] ExtractTokenizerBlob(string dbPath, string name = null)
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            c.Open();
            using (var chk = c.CreateCommand()) { chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='tokenizer'"; if (chk.ExecuteScalar() == null) return null; }
            using var cmd = c.CreateCommand();
            cmd.CommandText = name != null ? "SELECT data FROM tokenizer WHERE name=$n LIMIT 1" : "SELECT data FROM tokenizer LIMIT 1";
            if (name != null) cmd.Parameters.AddWithValue("$n", name);
            var o = cmd.ExecuteScalar();
            return o == null || o is DBNull ? null : (byte[])o;
        }

        public static byte[] GetTunedShader(string dbPath, string adapterName, string opType, int m, int n, int k, out int threadM, out int threadN)
        {
            threadM = 16;
            threadN = 16;
            if (!System.IO.File.Exists(dbPath)) return null;
            try
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                
                // check if gpu_tactic_plan table exists
                using (var cmdTable = c.CreateCommand())
                {
                    cmdTable.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='gpu_tactic_plan'";
                    if (cmdTable.ExecuteScalar() == null) return null;
                }
                
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT dxil_bytecode, thread_m, thread_n FROM gpu_tactic_plan WHERE adapter_name=$a AND op_type=$o AND m=$m AND n=$n AND k=$k LIMIT 1";
                cmd.Parameters.AddWithValue("$a", adapterName);
                cmd.Parameters.AddWithValue("$o", opType);
                cmd.Parameters.AddWithValue("$m", m);
                cmd.Parameters.AddWithValue("$n", n);
                cmd.Parameters.AddWithValue("$k", k);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    byte[] data = r.IsDBNull(0) ? null : (byte[])r.GetValue(0);
                    threadM = r.IsDBNull(1) ? 16 : r.GetInt32(1);
                    threadN = r.IsDBNull(2) ? 16 : r.GetInt32(2);
                    return data;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  WARN: error reading gpu_tactic_plan: {ex.Message}");
            }
            return null;
        }

        public static void WriteTunedShader(string dbPath, string adapterName, string opType, int m, int n, int k, int blockM, int blockN, int blockK, int threadM, int threadN, int useSharedMem, int unrollFactor, byte[] dxil, double latencyMs)
        {
            try
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                
                // create table if not exists
                using (var cmdTable = c.CreateCommand())
                {
                    cmdTable.CommandText = @"
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
                    cmdTable.ExecuteNonQuery();
                }
                
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
INSERT OR REPLACE INTO gpu_tactic_plan 
(adapter_name, op_type, m, n, k, block_m, block_n, block_k, thread_m, thread_n, use_shared_mem, unroll_factor, dxil_bytecode, latency_ms)
VALUES ($a, $o, $m, $n, $k, $bm, $bn, $bk, $tm, $tn, $usm, $uf, $dxil, $lat);";
                cmd.Parameters.AddWithValue("$a", adapterName);
                cmd.Parameters.AddWithValue("$o", opType);
                cmd.Parameters.AddWithValue("$m", m);
                cmd.Parameters.AddWithValue("$n", n);
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$bm", blockM);
                cmd.Parameters.AddWithValue("$bn", blockN);
                cmd.Parameters.AddWithValue("$bk", blockK);
                cmd.Parameters.AddWithValue("$tm", threadM);
                cmd.Parameters.AddWithValue("$tn", threadN);
                cmd.Parameters.AddWithValue("$usm", useSharedMem);
                cmd.Parameters.AddWithValue("$uf", unrollFactor);
                cmd.Parameters.AddWithValue("$dxil", (object?)dxil ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lat", latencyMs);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR: failed saving tuned shader to DB: {ex.Message}");
            }
        }
    }
}
