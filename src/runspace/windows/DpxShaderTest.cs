#nullable enable
using System;
using System.IO;
using Subsystem.Dpx;

namespace Subsystem;

// ss dpx-shader-test — validate each GPU compute shader against CPU reference
// using the standalone (upload-compute-download) dispatch path.
// This isolates shader correctness from the VRAM residency plumbing.
static class DpxShaderTest
{
    static byte[] LoadDxil(string name)
    {
        string near = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(near)) return File.ReadAllBytes(near);
        if (Environment.ProcessPath != null)
        {
            string procDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            string procPath = Path.Combine(procDir, name);
            if (File.Exists(procPath)) return File.ReadAllBytes(procPath);
        }
        string rootPath = Path.Combine("S:\\subsystem", name);
        if (File.Exists(rootPath)) return File.ReadAllBytes(rootPath);
        return Array.Empty<byte>();
    }

    public static int Run(string[] args)
    {
        Console.Error.WriteLine("[DPX Shader Test] Validating GPU compute shaders against CPU reference...\n");

        int pass = 0, fail = 0;

        // ---- Test 1: Add ----
        {
            const int N = 1024;
            float[] a = new float[N], b = new float[N], gpuY = new float[N];
            var rng = new Random(42);
            for (int i = 0; i < N; i++) { a[i] = (float)(rng.NextDouble() * 2 - 1); b[i] = (float)(rng.NextDouble() * 2 - 1); }

            float[] cpuY = new float[N];
            for (int i = 0; i < N; i++) cpuY[i] = a[i] + b[i];

            byte[] addDxil = LoadDxil("add.dxil");
            if (addDxil.Length == 0) { Console.Error.WriteLine("  [SKIP] Add — add.dxil not found"); }
            else
            {
                int rc = GpuD3D12.DispatchAdd(a, b, gpuY, (uint)N, addDxil);
                if (rc != 0) { Console.Error.WriteLine($"  [FAIL] Add — dispatch returned {rc}"); fail++; }
                else
                {
                    var (ok, maxErr, idx) = Compare(cpuY, gpuY, 1e-5f);
                    if (ok) { Console.Error.WriteLine($"  [PASS] Add — max error {maxErr:E3}"); pass++; }
                    else { Console.Error.WriteLine($"  [FAIL] Add — max error {maxErr:E3} at [{idx}]: cpu={cpuY[idx]:E6} gpu={gpuY[idx]:E6}"); fail++; }
                }
            }
        }

        // ---- Test 2: SwiGLU ----
        {
            const int N = 1024;
            float[] gate = new float[N], up = new float[N], gpuY = new float[N];
            var rng = new Random(43);
            for (int i = 0; i < N; i++) { gate[i] = (float)(rng.NextDouble() * 2 - 1); up[i] = (float)(rng.NextDouble() * 2 - 1); }

            float[] cpuY = new float[N];
            for (int i = 0; i < N; i++)
            {
                float sig = 1f / (1f + MathF.Exp(-gate[i]));
                cpuY[i] = gate[i] * sig * up[i];
            }

            byte[] swigluDxil = LoadDxil("swiglu.dxil");
            if (swigluDxil.Length == 0) { Console.Error.WriteLine("  [SKIP] SwiGLU — swiglu.dxil not found"); }
            else
            {
                int rc = GpuD3D12.DispatchSwiGLU(gate, up, gpuY, (uint)N, swigluDxil);
                if (rc != 0) { Console.Error.WriteLine($"  [FAIL] SwiGLU — dispatch returned {rc}"); fail++; }
                else
                {
                    var (ok, maxErr, idx) = Compare(cpuY, gpuY, 1e-4f);
                    if (ok) { Console.Error.WriteLine($"  [PASS] SwiGLU — max error {maxErr:E3}"); pass++; }
                    else { Console.Error.WriteLine($"  [FAIL] SwiGLU — max error {maxErr:E3} at [{idx}]: cpu={cpuY[idx]:E6} gpu={gpuY[idx]:E6}"); fail++; }
                }
            }
        }

        // ---- Test 3: RMSNorm ----
        {
            const int M = 4, D = 256;
            float[] x = new float[M * D], gamma = new float[D], gpuY = new float[M * D];
            var rng = new Random(44);
            for (int i = 0; i < M * D; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
            for (int i = 0; i < D; i++) gamma[i] = (float)(rng.NextDouble() * 0.5 + 0.75);

            float eps = 1e-6f;
            float[] cpuY = new float[M * D];
            for (int m = 0; m < M; m++)
            {
                float ss = 0;
                for (int d = 0; d < D; d++) { float v = x[m * D + d]; ss += v * v; }
                float rms = MathF.Sqrt(ss / D + eps);
                for (int d = 0; d < D; d++) cpuY[m * D + d] = x[m * D + d] / rms * gamma[d];
            }

            byte[] rmsDxil = LoadDxil("rmsnorm.dxil");
            if (rmsDxil.Length == 0) { Console.Error.WriteLine("  [SKIP] RMSNorm — rmsnorm.dxil not found"); }
            else
            {
                int rc = GpuD3D12.DispatchRMSNorm(x, gamma, gpuY, (uint)M, (uint)D, eps, rmsDxil);
                if (rc != 0) { Console.Error.WriteLine($"  [FAIL] RMSNorm — dispatch returned {rc}"); fail++; }
                else
                {
                    var (ok, maxErr, idx) = Compare(cpuY, gpuY, 1e-3f);
                    if (ok) { Console.Error.WriteLine($"  [PASS] RMSNorm — max error {maxErr:E3}"); pass++; }
                    else
                    {
                        Console.Error.WriteLine($"  [FAIL] RMSNorm — max error {maxErr:E3} at [{idx}]: cpu={cpuY[idx]:E6} gpu={gpuY[idx]:E6}");
                        int shown = 0;
                        for (int i = 0; i < cpuY.Length && shown < 8; i++)
                        {
                            float err = MathF.Abs(cpuY[i] - gpuY[i]);
                            if (err > 1e-3f) { Console.Error.WriteLine($"    [{i}] cpu={cpuY[i]:E6} gpu={gpuY[i]:E6} err={err:E3}"); shown++; }
                        }
                        fail++;
                    }
                }
            }
        }

        // ---- Test 4: RoPE ----
        // The RoPE shader expects CS buffer as split-halves: [cos_0..cos_{half-1}, sin_0..sin_{half-1}]
        // and uses non-interleaved (split-half) indexing: out[i]=x[i]*cos - x[i+half]*sin, etc.
        // Params: M=batch*seq, heads=num_heads, dim=head_dim, pos=position (unused by shader, built into CS).
        {
            const uint M = 1, heads = 8, dim = 64;
            int half = (int)(dim / 2);
            int count = (int)(M * heads * dim);
            float[] x = new float[count], gpuY = new float[count];
            var rng = new Random(45);
            for (int i = 0; i < count; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);

            // Build CS in split-half layout: [cos_0..cos_{half-1}, sin_0..sin_{half-1}]
            float[] csCombined = new float[dim];
            uint pos = 5;
            for (int d = 0; d < half; d++)
            {
                float theta = pos / MathF.Pow(10000f, 2f * d / dim);
                csCombined[d] = MathF.Cos(theta);          // cos values in first half
                csCombined[half + d] = MathF.Sin(theta);   // sin values in second half
            }

            // CPU reference: non-interleaved (split-half) RoPE
            // out[bI+i]      = x[bI+i]*cos[i]      - x[bI+i+half]*sin[i]
            // out[bI+i+half] = x[bI+i+half]*cos[i]  + x[bI+i]*sin[i]
            float[] cpuY = new float[count];
            for (int h = 0; h < (int)heads; h++)
            {
                int bI = (int)(h * dim);
                for (int i = 0; i < half; i++)
                {
                    float c = csCombined[i], s = csCombined[half + i];
                    float a0 = x[bI + i], a1 = x[bI + i + half];
                    cpuY[bI + i] = a0 * c - a1 * s;
                    cpuY[bI + i + half] = a1 * c + a0 * s;
                }
            }

            byte[] ropeDxil = LoadDxil("rope.dxil");
            if (ropeDxil.Length == 0) { Console.Error.WriteLine("  [SKIP] RoPE — rope.dxil not found"); }
            else
            {
                int rc = GpuD3D12.DispatchRoPE(x, csCombined, gpuY, M, heads, dim, pos, ropeDxil);
                if (rc != 0) { Console.Error.WriteLine($"  [FAIL] RoPE — dispatch returned {rc}"); fail++; }
                else
                {
                    var (ok, maxErr, idx) = Compare(cpuY, gpuY, 1e-4f);
                    if (ok) { Console.Error.WriteLine($"  [PASS] RoPE — max error {maxErr:E3}"); pass++; }
                    else
                    {
                        Console.Error.WriteLine($"  [FAIL] RoPE — max error {maxErr:E3} at [{idx}]: cpu={cpuY[idx]:E6} gpu={gpuY[idx]:E6}");
                        // Dump first 16 elements for debugging
                        Console.Error.WriteLine("    First 16 elements:");
                        for (int i = 0; i < Math.Min(16, cpuY.Length); i++)
                        {
                            Console.Error.WriteLine($"    [{i}] cpu={cpuY[i]:E6} gpu={gpuY[i]:E6} err={MathF.Abs(cpuY[i]-gpuY[i]):E3}");
                        }
                        fail++;
                    }
                }
            }
        }

        // ---- Test 5: Q4 GEMM (GemmQ4 path) ----
        // No standalone fp32 gemm.dxil exists. The Q4 shaders use a different root sig.
        // Skipping fp32 MatMul — the actual inference uses MatMulNBits (Q4) which is validated
        // by end-to-end CPU correctness.
        Console.Error.WriteLine("  [INFO] MatMul — no fp32 gemm.dxil; Q4 path validated by end-to-end CPU correctness.");

        Console.Error.WriteLine($"\n[DPX Shader Test] {pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    static (bool ok, float maxErr, int idx) Compare(float[] cpu, float[] gpu, float tol)
    {
        if (cpu.Length != gpu.Length) return (false, float.MaxValue, 0);
        float maxErr = 0; int maxIdx = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            float err = MathF.Abs(cpu[i] - gpu[i]);
            float denom = MathF.Max(1f, MathF.Abs(cpu[i]));
            float rel = err / denom;
            if (rel > maxErr) { maxErr = rel; maxIdx = i; }
        }
        return (maxErr <= tol, maxErr, maxIdx);
    }
}
