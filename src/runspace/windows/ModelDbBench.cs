using System;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Subsystem.Windows;

// Benchmark for the model.db -> VOM-region gather: the two-tier seam — model.db is the truth at rest;
// 256-aligned VOM native regions are the data plane in flight. Streams each tensor BLOB from the db into a
// native VOM region (Vom.Alloc + Marshal.Copy), measures gather throughput, shows the resident model
// weights live off the GC, then Terminate reclaims the whole working set deterministically (free-on-zero,
// no sweep). The matmul never touches SQLite. Drives tests/bench.dp-onnx.model-db-gather.ps1.
public static class ModelDbBench
{
    public static string Gather(string dbPath)
    {
        var owner = Subsystem.Vom.Vom.CreateOwner($"\\System\\__gather_{DateTime.Now:HHmmssfff}",
                                                  8L * 1024 * 1024 * 1024, 1_000_000);
        long managed0 = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        long bytes = 0; int count = 0;
        using (var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            cn.Open();
            // (rowid, length) first, so the gather loop streams each BLOB STRAIGHT into its native region via
            // SqliteBlob — no intermediate managed byte[], so the model never lands on the GC heap at all.
            var rows = new System.Collections.Generic.List<(long id, int len)>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, length(data) FROM tensor WHERE data IS NOT NULL AND length(data) > 0";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), (int)r.GetInt64(1)));
            }
            foreach (var (id, len) in rows)
            {
                var h = Subsystem.Vom.Vom.Alloc(owner, len, type: "TensorRegion");
                using var blob = new SqliteBlob(cn, "tensor", "data", id, readOnly: true);
                unsafe
                {
                    var dst = new Span<byte>((void*)h.Resource, len);   // the 256-aligned native region
                    int off = 0;
                    while (off < len) { int n = blob.Read(dst.Slice(off)); if (n <= 0) break; off += n; }
                }
                count++; bytes += len;
            }
        }
        sw.Stop();
        long nativeBytes = owner.CurrentBytes;
        long managed1 = GC.GetTotalMemory(false);
        double gbps = sw.Elapsed.TotalSeconds > 0 ? (bytes / 1e9) / sw.Elapsed.TotalSeconds : 0;
        var swr = Stopwatch.StartNew();
        Subsystem.Vom.Vom.Terminate(owner);   // free-on-zero, deterministic, no GC sweep
        swr.Stop();
        return JsonSerializer.Serialize(new
        {
            tensors = count,
            MB = Math.Round(bytes / 1048576.0, 1),
            gatherSec = Math.Round(sw.Elapsed.TotalSeconds, 3),
            GBps = Math.Round(gbps, 2),
            residentNativeMB = Math.Round(nativeBytes / 1048576.0, 1),
            managedDeltaMB = Math.Round((managed1 - managed0) / 1048576.0, 1),
            reclaimMs = Math.Round(swr.Elapsed.TotalMilliseconds, 1),
        });
    }
}
