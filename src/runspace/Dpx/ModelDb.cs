using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Onnx;

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
