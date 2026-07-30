// kokoro-tts — python-free Kokoro TTS over ONNX Runtime (.NET).
//
// Pipeline (mirrors the TRT-repo runner shape; chatterbox_trt.cpp is the 1:1 native twin):
//   IPA phonemes --vocab--> int64 token ids  ┐
//   voice .pt   --row[n]--> float style[256] ┼--> ONNX (input_ids, style, speed) --> waveform --> 24kHz WAV
//   speed                 --> float[1]        ┘
//
// Backend here is ORT/CPU. The IKokoroBackend seam keeps Init/Run/Dispose identical to
// chatterbox_trt.cpp's Trt_Init/Trt_Process/Trt_Destroy, so a TRT C-ABI bridge (clone
// chatterbox_trt.cpp + style/speed inputs) or a QNN .bin runner slots in unchanged.
//
// NOT YET (front-end): G2P (text->IPA). Kokoro needs IPA phonemes; espeak-ng (a C lib,
// P/Invoke-able, python-free) is the drop-in. v1 takes phonemes directly.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

const int SR = 24000;

string model = "", voice = "af_heart", phonemes = "", outPath = "out.wav";
string configPath = @"S:\reference\Kokoro-82M\config.json";
string voicesDir = @"S:\reference\Kokoro-82M\voices";
string? phonemesFile = null, validateAgainst = null, dumpDir = null, probeOut = null, dumpAllDir = null;
float speed = 1.0f;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model": model = args[++i]; break;
        case "--voice": voice = args[++i]; break;
        case "--phonemes": phonemes = args[++i]; break;
        case "--phonemes-file": phonemesFile = args[++i]; break;
        case "--speed": speed = float.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--config": configPath = args[++i]; break;
        case "--voices": voicesDir = args[++i]; break;
        case "--validate": validateAgainst = args[++i]; break;
        case "--dump": dumpDir = args[++i]; break;
        case "--probe-out": probeOut = args[++i]; break;
        case "--dump-all": dumpAllDir = args[++i]; break;
        default: if (model == "") model = args[i]; break;
    }
}
if (phonemesFile != null) phonemes = File.ReadAllText(phonemesFile, Encoding.UTF8).Trim();
if (model == "" || phonemes == "")
{
    Console.Error.WriteLine("usage: kokoro-tts --model <onnx> --phonemes \"<ipa>\" | --phonemes-file <f>");
    Console.Error.WriteLine("       [--voice af_heart] [--speed 1.0] [--out out.wav] [--validate <other.onnx>]");
    return 1;
}

// 1. vocab (read live from Kokoro config.json — no embedded copy to drift)
var vocab = LoadVocab(configPath);

// 2. tokenize: [0] + phoneme ids + [0]  (kokoro convention)
var ids = new List<long> { 0 };
int unknown = 0;
foreach (var ch in phonemes)
{
    if (vocab.TryGetValue(ch, out var id)) ids.Add(id);
    else if (!char.IsWhiteSpace(ch) || vocab.ContainsKey(' ')) { if (vocab.TryGetValue(ch, out var id2)) ids.Add(id2); else unknown++; }
}
ids.Add(0);
int realCount = ids.Count - 2;
Console.WriteLine($"phonemes chars={phonemes.Length}  mapped tokens={realCount}  unknown={unknown}");

// 3. style row for this phoneme count
float[] style = LoadVoiceStyle(Path.Combine(voicesDir, voice + ".pt"), realCount);
Console.WriteLine($"voice={voice}  style[256] row={Math.Clamp(realCount, 0, 509)}  style[0..3]=[{style[0]:F4},{style[1]:F4},{style[2]:F4}]");

// debug: fetch one internal ORT tensor by name (optimizations off so node names survive)
if (probeOut != null)
{
    using var pso = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
    using var psess = new InferenceSession(model, pso);
    int pL = ids.Count; var pIds = ids.ToArray();
    var pin = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(pIds, new[] { 1, pL })),
        NamedOnnxValue.CreateFromTensor("style",     new DenseTensor<float>(style, new[] { 1, style.Length })),
        NamedOnnxValue.CreateFromTensor("speed",     new DenseTensor<float>(new[] { speed }, new[] { 1 })),
    };
    var pnames = probeOut.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    using var pres = psess.Run(pin, pnames);
    foreach (var nv in pres)
    {
        var pt = nv.AsTensor<float>(); var arr = pt.ToArray();
        double a = 0; foreach (var v in arr) a += (double)v * v; double prms = arr.Length > 0 ? Math.Sqrt(a / arr.Length) : 0;
        var head = string.Join(",", arr.Take(12).Select(v => v.ToString("F3")));
        Console.WriteLine($"PROBE {nv.Name} [{string.Join(",", pt.Dimensions.ToArray())}] rms={prms:F4} head=[{head}]");
        var dir = Path.GetDirectoryName(Path.GetFullPath(model))!;
        var bn = "ort_" + new string(nv.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_') + ".bin";
        using var bw = new BinaryWriter(File.Create(Path.Combine(dir, bn)));
        bw.Write(1); bw.Write(pt.Dimensions.Length); foreach (var d in pt.Dimensions.ToArray()) bw.Write((long)d); foreach (var v in arr) bw.Write(v);
    }
    return 0;
}

// --dump-all: write EVERY float graph output to <dir>/ort_<sani>.bin (pair with addoutput-all -> dpx run --compare)
if (dumpAllDir != null)
{
    Directory.CreateDirectory(dumpAllDir);
    using var aso = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
    using var asess = new InferenceSession(model, aso);
    int aL = ids.Count; var aIds = ids.ToArray();
    var ain = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(aIds, new[] { 1, aL })),
        NamedOnnxValue.CreateFromTensor("style",     new DenseTensor<float>(style, new[] { 1, style.Length })),
        NamedOnnxValue.CreateFromTensor("speed",     new DenseTensor<float>(new[] { speed }, new[] { 1 })),
    };
    using var ares = asess.Run(ain);
    int wrote = 0;
    foreach (var nv in ares)
    {
        try
        {
            var t = nv.AsTensor<float>(); var arr = t.ToArray();
            var bn = "ort_" + new string(nv.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_') + ".bin";
            using var bw = new BinaryWriter(File.Create(Path.Combine(dumpAllDir, bn)));
            bw.Write(1); bw.Write(t.Dimensions.Length); foreach (var d in t.Dimensions.ToArray()) bw.Write((long)d); foreach (var v in arr) bw.Write(v);
            wrote++;
        }
        catch { }   // non-float output -> skip
    }
    Console.WriteLine($"dumped {wrote} float tensors to {dumpAllDir}");
    return 0;
}

// 4. run
var wav = Run(model, ids, style, speed, out int seqLen, out string[] providers, dumpDir);
Console.WriteLine($"model={Path.GetFileName(model)}  EP={string.Join(",", providers)}  input_ids len={seqLen}");
Console.WriteLine($"waveform samples={wav.Length}  ({wav.Length / (double)SR:F2}s)  rms={Rms(wav):F5}  peak={Peak(wav):F5}");
WriteWav(outPath, wav, SR);
Console.WriteLine($"wrote {outPath}");
if (dumpDir != null)
{
    Directory.CreateDirectory(dumpDir);
    WriteTensorF(Path.Combine(dumpDir, "oracle.bin"), wav, wav.Length);
    Console.WriteLine($"dumped oracle.bin ({wav.Length} samples) to {dumpDir}");
}

// 5. optional: validate this graph against another (the STFT-decomposition gate)
if (validateAgainst != null)
{
    var wav2 = Run(validateAgainst, ids, style, speed, out _, out _);
    int n = Math.Min(wav.Length, wav2.Length);
    double maxd = 0, sumsq = 0;
    for (int i = 0; i < n; i++) { double d = Math.Abs(wav[i] - wav2[i]); maxd = Math.Max(maxd, d); sumsq += d * d; }
    Console.WriteLine($"VALIDATE vs {Path.GetFileName(validateAgainst)}: len {wav.Length} vs {wav2.Length}  max|diff|={maxd:E3}  rmse={Math.Sqrt(sumsq / n):E3}");
}
return 0;

// ---------------------------------------------------------------------------
static Dictionary<char, long> LoadVocab(string configPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
    var v = doc.RootElement.GetProperty("vocab");
    var d = new Dictionary<char, long>();
    foreach (var p in v.EnumerateObject())
        if (p.Name.Length == 1) d[p.Name[0]] = p.Value.GetInt64();
    return d;
}

static float[] LoadVoiceStyle(string ptPath, int tokenCount)
{
    using var zip = ZipFile.OpenRead(ptPath);
    var entry = zip.Entries.First(e => e.FullName.EndsWith("data/0"));
    using var s = entry.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    var raw = ms.ToArray();                 // 510*256 float32, row-major
    int rows = raw.Length / (256 * 4);
    int row = Math.Clamp(tokenCount, 0, rows - 1);
    var style = new float[256];
    Buffer.BlockCopy(raw, row * 256 * 4, style, 0, 256 * 4);
    return style;
}

static float[] Run(string model, List<long> idsList, float[] style, float speed, out int seqLen, out string[] providers, string? dumpDir = null)
{
    using var so = new SessionOptions();
    using var sess = new InferenceSession(model, so);
    providers = OrtEnv.Instance().GetAvailableProviders();

    // honor a static input_ids length (the folded graph is fixed [1,256]); pad with 0
    int L = idsList.Count;
    if (sess.InputMetadata.TryGetValue("input_ids", out var m) && m.Dimensions.Length >= 2 && m.Dimensions[1] > 0)
        L = m.Dimensions[1];
    seqLen = L;

    var idArr = new long[L];
    for (int i = 0; i < L && i < idsList.Count; i++) idArr[i] = idsList[i];

    if (dumpDir != null)
    {
        Directory.CreateDirectory(dumpDir);
        WriteTensorI(Path.Combine(dumpDir, "input_ids.bin"), idArr, 1, L);
        WriteTensorF(Path.Combine(dumpDir, "style.bin"), style, 1, style.Length);
        WriteTensorF(Path.Combine(dumpDir, "speed.bin"), new[] { speed }, 1);
        Console.WriteLine($"dumped inputs (input_ids[1,{L}], style[1,{style.Length}], speed[1]) to {dumpDir}");
    }

    var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(idArr, new[] { 1, L })),
        NamedOnnxValue.CreateFromTensor("style",     new DenseTensor<float>(style, new[] { 1, style.Length })),
        NamedOnnxValue.CreateFromTensor("speed",     new DenseTensor<float>(new[] { speed }, new[] { 1 })),
    };
    using var res = sess.Run(inputs);
    return res.First().AsTensor<float>().ToArray();
}

static void WriteWav(string path, float[] s, int sr)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    int dataBytes = s.Length * 2;
    bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataBytes); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataBytes);
    foreach (var f in s) bw.Write((short)Math.Round(Math.Clamp(f, -1f, 1f) * 32767));
}

static double Rms(float[] s) { double a = 0; foreach (var f in s) a += (double)f * f; return s.Length > 0 ? Math.Sqrt(a / s.Length) : 0; }
static double Peak(float[] s) { double p = 0; foreach (var f in s) p = Math.Max(p, Math.Abs(f)); return p; }

// tensor dump format (shared with dpx run --inputs): int32 dtype(1=f32,7=i64), int32 rank, int64[rank] dims, raw payload
static void WriteTensorI(string path, long[] data, params long[] dims)
{ using var bw = new BinaryWriter(File.Create(path)); bw.Write(7); bw.Write(dims.Length); foreach (var d in dims) bw.Write(d); foreach (var v in data) bw.Write(v); }
static void WriteTensorF(string path, float[] data, params long[] dims)
{ using var bw = new BinaryWriter(File.Create(path)); bw.Write(1); bw.Write(dims.Length); foreach (var d in dims) bw.Write(d); foreach (var v in data) bw.Write(v); }
