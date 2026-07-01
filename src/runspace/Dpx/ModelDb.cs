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
    }
}
