#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Subsystem.Dpx;

namespace Subsystem.Windows;

// ss modeldb-consolidate — ONE MODEL, ONE DB (CRQ191). Fold the per-face gemma4-e2b exports
// (gemma4-e2b-onnx-embed/decoder/vision/audio-*.db) plus the loose gemma-4-e2b.spm tokenizer into a single
// self-describing gemma4-e2b.db: each face is a named signature (row graph), the tokenizer rides in a blob
// table, and a manifest names each face with its sha256 / quant / provenance. The input dbs and the litert
// deadpool db are NEVER touched. Reuses ModelDb.WriteModelDb (the single chunked writer), so the 1.17GB PLE
// embedding table lands as tensor_blob chunks, not a dropped NULL. Mirrors ModelDbBench's head-class shape:
// a reflectable static surface the tests drive, plus a CLI entry routed from windows/Program.cs.
public static class ModelDbConsolidate
{
    public static int Run(string[] args)
    {
        string modelsDir = ArgValue(args, "--models") ?? Environment.GetEnvironmentVariable("SS_MODELS") ?? "";
        if (string.IsNullOrEmpty(modelsDir))
        {
            var driveRoot = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
            modelsDir = Path.Combine(driveRoot, "modeldb");
        }
        if (!Directory.Exists(modelsDir))
        {
            Console.Error.WriteLine($"Error: models dir not found: {modelsDir}  (set SS_MODELS or pass --models <dir>)");
            return 1;
        }

        string outPath = ArgValue(args, "--out") ?? Path.Combine(modelsDir, "gemma4-e2b.db");

        // Discover the per-face ONNX exports (never the *-litert deadpool). Map each file to a face + quant tag.
        var faces = new List<FaceSource>();
        foreach (var f in Directory.EnumerateFiles(modelsDir, "*-onnx-*.db").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var (face, quant) = FaceOf(Path.GetFileNameWithoutExtension(f));
            if (face == null) continue;
            faces.Add(new FaceSource { Face = face, Quant = quant, Path = f, Length = new FileInfo(f).Length });
        }
        if (faces.Count == 0)
        {
            Console.Error.WriteLine($"Error: no per-face *-onnx-*.db exports found under {modelsDir}");
            return 1;
        }

        string spm = Directory.EnumerateFiles(modelsDir, "*.spm").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (spm == null)
        {
            Console.Error.WriteLine($"Error: no .spm tokenizer found under {modelsDir} (the consolidated db must carry it)");
            return 1;
        }

        // Guard: refuse to write over an input or the litert deadpool.
        string outFull = Path.GetFullPath(outPath);
        foreach (var fc in faces)
            if (string.Equals(Path.GetFullPath(fc.Path), outFull, StringComparison.OrdinalIgnoreCase))
            { Console.Error.WriteLine($"Error: output {outPath} collides with an input face db — refusing."); return 1; }
        if (Path.GetFileName(outFull).Contains("litert", StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("Error: refusing to write a file named like the litert deadpool db."); return 1; }

        long inputBytes = faces.Sum(fc => fc.Length) + new FileInfo(spm).Length;
        // The consolidated db is ~the sum of the faces; a single big transaction also stages a rollback journal.
        // Demand headroom for both (2.2x) before committing to a ~4.3GB write.
        long need = (long)(inputBytes * 2.2);
        var drive = new DriveInfo(Path.GetPathRoot(outFull) ?? outFull);
        Console.WriteLine($"[consolidate] models={modelsDir}");
        Console.WriteLine($"[consolidate] faces={faces.Count} inputBytes={inputBytes / 1e6:F0}MB  free={drive.AvailableFreeSpace / 1e9:F1}GB  need~{need / 1e9:F1}GB (2.2x for db+journal)");
        if (drive.AvailableFreeSpace < need)
        {
            Console.Error.WriteLine($"Error: insufficient free space on {drive.Name}: {drive.AvailableFreeSpace / 1e9:F1}GB free < {need / 1e9:F1}GB needed. Stopping.");
            return 2;
        }

        // Assign a sig per face (discovery order) and stream each graph through the single chunked writer so
        // only one face is resident at a time (the 1.17GB embed must not co-reside with the 1.8GB decoder).
        for (int i = 0; i < faces.Count; i++) faces[i].Sig = i;
        IEnumerable<(int sig, string role, GraphProto g)> Graphs()
        {
            foreach (var fc in faces)
            {
                Console.WriteLine($"[consolidate] loading face '{fc.Face}' (sig {fc.Sig}) <- {Path.GetFileName(fc.Path)} ({fc.Length / 1e6:F0}MB)");
                yield return (fc.Sig, fc.Face, ModelDb.LoadGraphFromDb(0, fc.Path).Graph);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ModelDb.WriteModelDb(outPath, Graphs());          // default 256MB chunk ceiling

        // Manifest + tokenizer ride IN the db (self-describing). sha256 of each SOURCE face db is the provenance.
        foreach (var fc in faces)
        {
            fc.Sha256 = Sha256File(fc.Path);
            ModelDb.WriteManifestRow(outPath, fc.Face, fc.Sig, fc.Sha256, fc.Quant, Path.GetFileName(fc.Path));
        }
        ModelDb.WriteTokenizerBlob(outPath, Path.GetFileName(spm), File.ReadAllBytes(spm));
        sw.Stop();

        PrintReceipt(outPath, spm, faces, sw.Elapsed.TotalSeconds);
        return 0;
    }

    private static void PrintReceipt(string outPath, string spm, List<FaceSource> faces, double sec)
    {
        long outLen = new FileInfo(outPath).Length;
        Console.WriteLine();
        Console.WriteLine($"[consolidate] wrote {outPath}  ({outLen / 1e6:F0}MB)  in {sec:F1}s");
        Console.WriteLine("[consolidate] signatures (face graphs):");
        using (var c = new SqliteConnection($"Data Source={outPath};Mode=ReadOnly"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT sig, role, nodes, tensors, inputs, outputs FROM signature ORDER BY sig";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    Console.WriteLine($"    sig {r.GetInt64(0)}  {r.GetString(1),-8}  nodes={r.GetInt64(2),-5} tensors={r.GetInt64(3),-5} in={r.GetInt64(4)} out={r.GetInt64(5)}");
            }
            long chunkRows = Scalar(c, "SELECT COUNT(*) FROM tensor_blob");
            long chunkedTensors = Scalar(c, "SELECT COUNT(DISTINCT tensor_id) FROM tensor_blob");
            long tokLen = Scalar(c, "SELECT length(data) FROM tokenizer LIMIT 1");
            Console.WriteLine($"[consolidate] tensor_blob: {chunkRows} chunk row(s) reassembling {chunkedTensors} over-cap tensor(s)");
            Console.WriteLine("[consolidate] manifest rows (face | sig | quant | sha256[..16] | provenance):");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT face, sig, quant, sha256, provenance FROM manifest ORDER BY sig";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string sha = r.IsDBNull(3) ? "?" : r.GetString(3);
                    Console.WriteLine($"    {r.GetString(0),-8} {r.GetInt64(1)}  {r.GetString(2),-6} {sha.Substring(0, Math.Min(16, sha.Length))}  {r.GetString(4)}");
                }
            }
            Console.WriteLine($"[consolidate] tokenizer: {Path.GetFileName(spm)} ({tokLen} bytes) embedded as blob");
        }
        var facesQ = ModelDb.QueryFaces(outPath);
        Console.WriteLine($"[consolidate] QueryFaces -> [{string.Join(", ", facesQ)}]");
    }

    // Synthetic chunked-tensor roundtrip receipt (drives tests/test.dpx.model-db-chunking.ps1): write ONE
    // tensor of tensorBytes with a SMALL chunkBytes override, read it back, and prove byte-identical + the
    // expected chunk count. Returns JSON. No giant fixture — the caller passes e.g. 8MB chunks / 20MB tensor.
    public static string RoundtripChunk(long chunkBytes, long tensorBytes)
    {
        // Deterministic non-constant fill (a cheap LCG-style byte pattern) — no RNG state, so the fixture is
        // reproducible and the byte-identical check is meaningful without a per-call Random.
        var bytes = new byte[tensorBytes];
        for (long i = 0; i < tensorBytes; i++) bytes[i] = (byte)(((ulong)i * 2654435761UL + 40503UL) >> 13);
        var t = new TensorProto { Name = "synthetic", DataType = 1 };
        t.Dims.Add(tensorBytes / 4);
        t.RawData = new ByteString(bytes);
        var g = new GraphProto { Name = "chunktest" };
        g.Initializer.Add(t);

        string db = Path.Combine(Path.GetTempPath(), $"ss-chunk-{Guid.NewGuid():N}.db");
        long chunksExpected = (tensorBytes + chunkBytes - 1) / chunkBytes;
        try
        {
            ModelDb.WriteModelDb(db, new[] { (0, "chunktest", g) }, chunkBytes);
            long chunkRows, inlineTensors;
            using (var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
            {
                c.Open();
                chunkRows = Scalar(c, "SELECT COUNT(*) FROM tensor_blob");
                inlineTensors = Scalar(c, "SELECT COUNT(*) FROM tensor WHERE data IS NOT NULL");
            }
            var back = ModelDb.LoadGraphFromDb(0, db).Graph.Initializer[0].RawData.ToByteArray();
            bool identical = back.Length == bytes.Length;
            if (identical) for (int i = 0; i < bytes.Length; i++) if (back[i] != bytes[i]) { identical = false; break; }
            string shaSrc = Convert.ToHexString(SHA256.HashData(bytes));
            string shaBack = Convert.ToHexString(SHA256.HashData(back));
            return JsonSerializer.Serialize(new
            {
                tensorBytes,
                chunkBytes,
                chunkRows,
                chunksExpected,
                inlineTensors,
                readBytes = back.Length,
                byteIdentical = identical,
                sha256Match = shaSrc == shaBack,
                sha256 = shaBack,
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();   // release the pooled read handle so the temp db can be deleted
            try { File.Delete(db); } catch (Exception ex) { Console.Error.WriteLine($"  WARN cleanup {db}: {ex.Message}"); }
        }
    }

    private sealed class FaceSource
    {
        public string Face { get; set; } = "";
        public string Quant { get; set; } = "";
        public string Path { get; set; } = "";
        public long Length { get; set; }
        public int Sig { get; set; }
        public string Sha256 { get; set; } = "";
    }

    // "gemma4-e2b-onnx-embed-q4" -> (embed, q4); "…-onnx-vision-fp16" -> (vision, fp16). The face is the token
    // after "onnx", the quant the token after the face. Unknown shapes (no onnx segment) return (null, null).
    private static (string face, string quant) FaceOf(string stem)
    {
        var parts = stem.Split('-');
        int i = Array.FindIndex(parts, p => string.Equals(p, "onnx", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= parts.Length) return (null, null);
        string face = parts[i + 1].ToLowerInvariant();
        string quant = i + 2 < parts.Length ? parts[i + 2].ToLowerInvariant() : "";
        return (face, quant);
    }

    // Share-tolerant on a busy box: FileShare.ReadWrite|Delete so another builder holding the same face db
    // can't lock us out. A read failure degrades to "" (logged) rather than aborting the consolidation.
    private static string Sha256File(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.Create().ComputeHash(fs));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  WARN sha256({path}): {ex.Message} — provenance hash left empty");
            return "";
        }
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var o = cmd.ExecuteScalar();
        return o == null || o is DBNull ? 0L : Convert.ToInt64(o);
    }

    private static string ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
