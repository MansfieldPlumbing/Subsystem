// DpxKokoro.cs — Kokoro-82M speech synthesis in-proc on the shared Dp interpreter (no ORT), ONE code
// path for BOTH heads (Windows ss.exe + Android APK; the Dpx glob compiles this into each). Loads the
// graph from a .onnx or a ModelDb .db, feeds reference tensors (<input>.bin, dp-onnx bin format),
// runs every node, writes a 24 kHz 16-bit mono wav, and returns a one-line measured receipt.
// First caller: tests/test.dpx.kokoro-android.ps1 (Windows lane in-proc; Android lane over /api/exec).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Onnx;

namespace Subsystem.Dpx;

public static class DpxKokoro
{
    public const int SampleRate = 24000;   // kokoro's fixed output rate (HF onnx-community export)

    // Synthesize: model (.onnx | ModelDb .db sig0) + inputsDir (<graph-input>.bin each) -> wav at
    // outWavPath. Returns the measured receipt line; the same line goes to Dg (logcat lane on-device).
    public static string Project(string modelPath, string inputsDir, string outWavPath)
    {
        var swLoad = System.Diagnostics.Stopwatch.StartNew();
        var model = modelPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? ModelDb.LoadGraphFromDb(0, modelPath)
            : ModelProto.Parser.ParseFrom(File.ReadAllBytes(modelPath));
        swLoad.Stop();

        var g = model.Graph;
        var initNames = new HashSet<string>(g.Initializer.Select(i => i.Name));
        var feed = new Dictionary<string, Tensor>();
        foreach (var vi in g.Input)
        {
            if (initNames.Contains(vi.Name)) continue;
            string f = Path.Combine(inputsDir, vi.Name + ".bin");
            if (!File.Exists(f)) throw new FileNotFoundException($"missing input tensor {f}");
            feed[vi.Name] = ReadTensorBin(f);
        }

        int totalNodes = g.Node.Count, ran = 0;
        var swRun = System.Diagnostics.Stopwatch.StartNew();
        var outs = new Dp(model).Run(feed, onNode: (_, _, _) =>
        {
            ran++;
            if ((ran % 500) == 0)
                Dg.Log("dpx-kokoro", $"node {ran}/{totalNodes} ({swRun.Elapsed.TotalSeconds:F1}s)");
        });
        swRun.Stop();

        var wav = outs.Values.First().AsF();
        double sumsq = 0, peak = 0; long nanInf = 0;
        for (int i = 0; i < wav.Length; i++)
        {
            float v = wav[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) { nanInf++; continue; }
            sumsq += (double)v * v;
            if (Math.Abs(v) > peak) peak = Math.Abs(v);
        }
        double rms = wav.Length > 0 ? Math.Sqrt(sumsq / wav.Length) : 0;
        WriteWav(outWavPath, wav, SampleRate);

        string receipt = string.Create(CultureInfo.InvariantCulture,
            $"kokoro nodes={ran}/{totalNodes} load_ms={swLoad.ElapsedMilliseconds} run_ms={swRun.ElapsedMilliseconds} samples={wav.Length} sr={SampleRate} dur_s={wav.Length / (double)SampleRate:F2} rms={rms:F5} peak={peak:F5} naninf={nanInf} wav={outWavPath}");
        Dg.Log("dpx-kokoro", receipt);
        return receipt;
    }

    // dp-onnx tensor bin format (same layout Program.cs's LoadBin/WriteTensorBin use; that file is the
    // standalone CLI's Main and is excluded from both heads, so the reader lives here too):
    //   int32 dtype (1=f32, 7=i64) · int32 rank · int64[rank] dims · payload.
    public static Tensor ReadTensorBin(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        int dtype = br.ReadInt32(), rank = br.ReadInt32();
        var shape = new int[rank]; long n = 1;
        for (int i = 0; i < rank; i++) { shape[i] = (int)br.ReadInt64(); n *= shape[i]; }
        if (dtype == 7) { var d = new long[n]; for (long i = 0; i < n; i++) d[i] = br.ReadInt64(); return Tensor.I(d, shape); }
        var fd = new float[n]; for (long i = 0; i < n; i++) fd[i] = br.ReadSingle(); return Tensor.F(fd, shape);
    }

    // Minimal RIFF writer: PCM16 mono. Float samples clamped to [-1,1].
    public static void WriteWav(string path, ReadOnlySpan<float> s, int sampleRate)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int dataBytes = s.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataBytes);
        foreach (var f in s) bw.Write((short)Math.Round(Math.Clamp(f, -1f, 1f) * 32767));
    }
}
