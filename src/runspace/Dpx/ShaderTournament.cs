using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Onnx;

namespace Subsystem.Dpx
{
    public static class ShaderTournament
    {
        // fixed seed: a tournament must be reproducible run to run (and SS006: shared, never per-call)
        private static readonly Random s_rnd = new Random(42);

        private static string FindDxcPath()
        {
            string baseDir = @"C:\Program Files (x86)\Windows Kits\10\bin";
            if (!Directory.Exists(baseDir)) return null;
            try
            {
                var files = Directory.GetFiles(baseDir, "dxc.exe", SearchOption.AllDirectories);
                var x64Files = files.Where(f => f.Contains("x64")).OrderByDescending(f => f);
                return x64Files.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static int RunTournament(string dbPath)
        {
            Console.WriteLine("=== DPX GPU SHADER TOURNAMENT ===");
            string dxcPath = FindDxcPath();
            if (dxcPath == null)
            {
                Console.Error.WriteLine("ERROR: dxc.exe not found in Windows Kits. Cannot compile HLSL variants.");
                return 1;
            }
            Console.WriteLine($"Compiler: {dxcPath}");

            // Warm up GPU and get adapter name
            GpuD3D12.EnsureInit();
            string adapterName = Gpu.DeviceName();
            Console.WriteLine($"GPU Adapter: {adapterName}");

            // Step 1: Scan unique MatMul/Gemm shapes in the database
            Console.WriteLine("Scanning model database for GEMM shapes...");
            var shapes = new List<(int m, int n, int k)>();
            using (var c = new SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                string sql = @"
SELECT DISTINCT n.id, ni1.value_name, t1.dims
FROM node n
JOIN node_io ni1 ON n.id = ni1.node_id AND ni1.kind = 'in' AND ni1.slot = 1
JOIN tensor t1 ON ni1.value_name = t1.name
WHERE n.op_type IN ('MatMul', 'Gemm')";

                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                var uniquePairs = new HashSet<(int k, int n)>();
                while (r.Read())
                {
                    long nodeId = r.GetInt64(0);
                    string dimsStr = r.IsDBNull(2) ? "" : r.GetString(2);
                    var dims = dimsStr.Split(',').Select(int.Parse).ToArray();
                    if (dims.Length == 2)
                    {
                        int k = dims[0];
                        int n = dims[1];

                        // check transB attribute
                        using var cmdAttr = c.CreateCommand();
                        cmdAttr.CommandText = "SELECT i FROM node_attr WHERE node_id=$nid AND name='transB'";
                        cmdAttr.Parameters.AddWithValue("$nid", nodeId);
                        var val = cmdAttr.ExecuteScalar();
                        bool isTransB = val != null && Convert.ToInt32(val) == 1;

                        int realK = isTransB ? dims[1] : dims[0];
                        int realN = isTransB ? dims[0] : dims[1];

                        uniquePairs.Add((realK, realN));
                    }
                }

                foreach (var pair in uniquePairs)
                {
                    // Tune for decode (M=1) and prefill (M=64)
                    shapes.Add((1, pair.n, pair.k));
                    shapes.Add((64, pair.n, pair.k));
                }
            }

            if (shapes.Count == 0)
            {
                Console.WriteLine("No MatMul/Gemm nodes with initializers found. Benchmarking a set of default Gemma4-e2b shapes.");
                // Fallback shapes: typical projections in Gemma4-e2b
                shapes.Add((1, 256, 1536));   // Audio projection decode
                shapes.Add((64, 256, 1536));  // Audio projection prefill
                shapes.Add((1, 1536, 1536));  // Layer projection decode
                shapes.Add((64, 1536, 1536)); // Layer projection prefill
            }

            Console.WriteLine($"Found {shapes.Count} shapes to tune.");

            // Create temporary folder for shader compilations
            string tmpDir = Path.Combine(Path.GetTempPath(), "dpx_tournament_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tmpDir);

            try
            {
                foreach (var shape in shapes.OrderBy(s => s.m).ThenBy(s => s.k))
                {
                    TuneShape(dxcPath, dbPath, adapterName, tmpDir, shape.m, shape.n, shape.k);
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch (Exception ex) { Subsystem.Dg.Log("dpx", $"tournament tmp cleanup failed: {ex.Message}"); }
            }

            Console.WriteLine("=== TOURNAMENT COMPLETE ===");
            return 0;
        }

        private static void TuneShape(string dxcPath, string dbPath, string adapterName, string tmpDir, int M, int N, int K)
        {
            Console.WriteLine($"\nTuning shape: M={M}, N={N}, K={K} ({M * N * K * 2.0 / 1e6:F2} MFLOP)");

            // Generate CPU reference for validation
            var rnd = s_rnd;
            float[] A = new float[M * K]; for (int i = 0; i < A.Length; i++) A[i] = (float)(rnd.NextDouble() * 2 - 1);
            float[] B = new float[K * N]; for (int i = 0; i < B.Length; i++) B[i] = (float)(rnd.NextDouble() * 2 - 1);
            float[] C_ref = new float[M * N];
            
            // Standard CPU GEMM reference
            for (int r = 0; r < M; r++)
            {
                for (int c = 0; c < N; c++)
                {
                    float acc = 0;
                    for (int k = 0; k < K; k++)
                    {
                        acc += A[r * K + k] * B[k * N + c];
                    }
                    C_ref[r * N + c] = acc;
                }
            }

            var tactics = GenerateTactics();
            string bestTacticName = "fallback";
            double bestTimeMs = double.MaxValue;
            byte[] bestDxil = null;
            int bestThreadM = 16, bestThreadN = 16;
            int bestBlockM = 0, bestBlockN = 0, bestBlockK = 0, bestShared = 0, bestUnroll = 1;

            foreach (var t in tactics)
            {
                // Verify thread dimensions are valid for the shape.
                // For tiled/shared memory, threadM and threadN must equal blockM and blockN.
                if (t.UseSharedMem == 1)
                {
                    if (t.ThreadM != t.BlockM || t.ThreadN != t.BlockN) continue;
                    // If shared dimensions exceed M or N, or don't align, skip to keep simple
                    if (t.ThreadM != t.ThreadN) continue; // must be square for current simple tiled kernel
                }

                string hlsl = GenerateHlsl(t);
                string hlslFile = Path.Combine(tmpDir, "temp.hlsl");
                string dxilFile = Path.Combine(tmpDir, "temp.dxil");
                File.WriteAllText(hlslFile, hlsl);

                // Compile using dxc
                var startInfo = new ProcessStartInfo
                {
                    FileName = dxcPath,
                    Arguments = $"-T cs_6_0 -E main -Fo \"{dxilFile}\" \"{hlslFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string errText = "";
                using var proc = Process.Start(startInfo);
                if (proc != null)
                {
                    errText = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                }
                if (proc == null || proc.ExitCode != 0)
                {
                    // Failed compilation (e.g. out of registers / local memory limit)
                    Console.WriteLine($"  Tactic: {t.Name,-40} | Compile FAILED: {errText.Replace("\r", "").Replace("\n", " ").Trim()}");
                    continue;
                }

                byte[] dxil = File.ReadAllBytes(dxilFile);

                // Run correctness test
                float[] C_gpu = new float[M * N];
                int rc = GpuD3D12.Gemm(A, B, C_gpu, (uint)M, (uint)N, (uint)K, dxil, dxil.Length, t.ThreadM, t.ThreadN);
                if (rc != 0)
                {
                    Console.WriteLine($"  Tactic: {t.Name,-40} | Run FAILED rc={rc}");
                    continue;
                }

                // Check parity
                double maxd = 0;
                for (int i = 0; i < C_gpu.Length; i++) maxd = Math.Max(maxd, Math.Abs(C_gpu[i] - C_ref[i]));
                if (maxd > 1e-3)
                {
                    Console.WriteLine($"  Tactic: {t.Name,-40} | Parity check FAILED maxdiff={maxd:E2}");
                    continue;
                }

                // Profile latency (best of 5)
                double minTimeMs = double.MaxValue;
                var sw = new Stopwatch();
                for (int run = 0; run < 5; run++)
                {
                    sw.Restart();
                    GpuD3D12.Gemm(A, B, C_gpu, (uint)M, (uint)N, (uint)K, dxil, dxil.Length, t.ThreadM, t.ThreadN);
                    sw.Stop();
                    double elapsed = sw.Elapsed.TotalMilliseconds;
                    if (elapsed < minTimeMs) minTimeMs = elapsed;
                }

                Console.WriteLine($"  Tactic: {t.Name,-40} | Latency: {minTimeMs:F3} ms | MaxDiff: {maxd:E2}");

                if (minTimeMs < bestTimeMs)
                {
                    bestTimeMs = minTimeMs;
                    bestDxil = dxil;
                    bestTacticName = t.Name;
                    bestThreadM = t.ThreadM;
                    bestThreadN = t.ThreadN;
                    bestBlockM = t.BlockM;
                    bestBlockN = t.BlockN;
                    bestBlockK = t.BlockK;
                    bestShared = t.UseSharedMem;
                    bestUnroll = t.UnrollFactor;
                }
            }

            if (bestDxil != null)
            {
                Console.WriteLine($"Winner: {bestTacticName} in {bestTimeMs:F3} ms");
                ModelDb.WriteTunedShader(dbPath, adapterName, "Gemm", M, N, K, 
                    bestBlockM, bestBlockN, bestBlockK, bestThreadM, bestThreadN, 
                    bestShared, bestUnroll, bestDxil, bestTimeMs);
            }
            else
            {
                Console.WriteLine("No valid custom tactic found. Using default.");
            }
        }

        private static string GenerateHlsl(Tactic t)
        {
            if (t.UseSharedMem == 1)
            {
                // Tiled Shared-Memory GEMM Template
                return $@"
cbuffer Params {{ uint M; uint N; uint K; }};
ByteAddressBuffer A : register(t0);
ByteAddressBuffer B : register(t1);
RWByteAddressBuffer C : register(u0);

#define BLOCK_M {t.BlockM}
#define BLOCK_N {t.BlockN}
#define BLOCK_K {t.BlockK}

groupshared float ashare[BLOCK_M][BLOCK_K];
groupshared float bshare[BLOCK_K][BLOCK_N];

[numthreads({t.ThreadN}, {t.ThreadM}, 1)]
void main(uint3 gid : SV_GroupID, uint3 gtid : SV_GroupThreadID, uint3 tid : SV_DispatchThreadID)
{{
    uint row = tid.y;
    uint col = tid.x;
    uint localRow = gtid.y;
    uint localCol = gtid.x;

    float acc = 0.0f;

    for (uint ko = 0; ko < K; ko += BLOCK_K)
    {{
        if (row < M && (ko + localCol) < K)
            ashare[localRow][localCol] = A.Load<float>((row * K + (ko + localCol)) * 4);
        else
            ashare[localRow][localCol] = 0.0f;

        if ((ko + localRow) < K && col < N)
            bshare[localRow][localCol] = B.Load<float>(((ko + localRow) * N + col) * 4);
        else
            bshare[localRow][localCol] = 0.0f;

        GroupMemoryBarrierWithGroupSync();

        [unroll({t.UnrollFactor})]
        for (uint ki = 0; ki < BLOCK_K; ki++)
        {{
            acc += ashare[localRow][ki] * bshare[ki][localCol];
        }}

        GroupMemoryBarrierWithGroupSync();
    }}

    if (row < M && col < N)
    {{
        C.Store<float>((row * N + col) * 4, acc);
    }}
}}
";
            }
            else
            {
                // Naive Parameterized GEMM Template
                return $@"
cbuffer Params {{ uint M; uint N; uint K; }};
ByteAddressBuffer A : register(t0);
ByteAddressBuffer B : register(t1);
RWByteAddressBuffer C : register(u0);

[numthreads({t.ThreadN}, {t.ThreadM}, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{{
    uint row = tid.y, col = tid.x;
    if (row >= M || col >= N) return;
    float acc = 0.0f;
    [unroll({t.UnrollFactor})]
    for (uint k = 0; k < K; k++)
        acc += A.Load<float>((row * K + k) * 4) * B.Load<float>((k * N + col) * 4);
    C.Store<float>((row * N + col) * 4, acc);
}}
";
            }
        }

        private static List<Tactic> GenerateTactics()
        {
            var list = new List<Tactic>();

            // Naive tactics: vary threads and unrolling
            int[] naiveThreads = { 8, 16, 32 };
            int[] unrolls = { 1, 4, 8 };

            foreach (var tx in naiveThreads)
            {
                foreach (var ty in naiveThreads)
                {
                    foreach (var u in unrolls)
                    {
                        list.Add(new Tactic
                        {
                            Name = $"Naive_{tx}x{ty}_u{u}",
                            ThreadM = ty,
                            ThreadN = tx,
                            BlockM = 0,
                            BlockN = 0,
                            BlockK = 0,
                            UseSharedMem = 0,
                            UnrollFactor = u
                        });
                    }
                }
            }

            // Shared memory tactics: threadM == blockM, threadN == blockN
            // We use square block/thread groups: 8x8, 16x16
            int[] blockSizes = { 8, 16 };
            foreach (var b in blockSizes)
            {
                foreach (var u in unrolls)
                {
                    list.Add(new Tactic
                    {
                        Name = $"Shared_{b}x{b}_u{u}",
                        ThreadM = b,
                        ThreadN = b,
                        BlockM = b,
                        BlockN = b,
                        BlockK = b,
                        UseSharedMem = 1,
                        UnrollFactor = u
                    });
                }
            }

            return list;
        }

        private class Tactic
        {
            public string Name { get; set; }
            public int ThreadM { get; set; }
            public int ThreadN { get; set; }
            public int BlockM { get; set; }
            public int BlockN { get; set; }
            public int BlockK { get; set; }
            public int UseSharedMem { get; set; }
            public int UnrollFactor { get; set; }
        }
    }
}
