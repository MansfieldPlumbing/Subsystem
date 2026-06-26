// ChatterboxTurboWorker.cs — .NET 9 ONNX Orchestrator for chatterbox-turbo-ONNX
// All constants and pipeline logic derived directly from the Python source.
//
// Pipeline summary:
//   1. speech_encoder  → ExtractVoicePack  (audio → cond_emb, prompt_tokens, speaker_emb, speaker_features)
//   2. embed_tokens    → embed text IDs + autoregressive speech tokens  [input_ids → inputs_embeds]
//   3. language_model  → autoregressive loop w/ KV-cache               [inputs_embeds + past_kv → logits + new_kv]
//   4. conditional_decoder → speech_tokens + speaker info → PCM float  [speech_tokens, speaker_embeddings, speaker_features → waveform]
//
// Tokenizer: GPT-2 BPE read directly from tokenizer.json.
//            add_prefix_space = false (per tokenizer_config.json).
//
// Usage (CLI):
//   --text "Hello world."
//   --voice ref.wav          (or --voicepack voice.json)
//   --out output.wav
//   --temp 0.8  --penalty 1.2  --topk 1000  --topp 0.95  --tokens 1024
//   --models .\onnx  --tokenizer .\tokenizer.json
//   --embed --voice ref.wav --out voice.json   (pre-extract VoicePack)
//   --variant q4f16          (q4, fp16, q4f16, quantized; default = q4)
//   --gpu 0                  (GPU device index; -1 = CPU-only)
//
// Usage (Server / stdin→stdout binary):
//   Start without --text; sends one JSON request per line:
//   {"Text":"Hi","Voice":"ref.wav","Temperature":0.8,"RepPenalty":1.2,"TopK":1000,"TopP":0.95,"MaxTokens":1024}
//   Responds: [4 bytes LE length][PCM int16 bytes]

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// ═══════════════════════════════════════════════════════════════════════════════
// CONSTANTS — sourced from Python files listed below
// ═══════════════════════════════════════════════════════════════════════════════

// s3gen/const.py
const int SAMPLE_RATE       = 24_000;   // S3GEN_SR — output audio sample rate
const int SILENCE_TOKEN     = 4_299;    // S3GEN_SIL — appended ×3 at end of speech tokens

// s3tokenizer/__init__.py / t3/modules/t3_config.py
// SPEECH_VOCAB_SIZE = 6561 (valid speech tokens are 0 … 6560)
// start_speech_token = SPEECH_VOCAB_SIZE     = 6561  (BOS seed, never decoded)
// stop_speech_token  = SPEECH_VOCAB_SIZE + 1 = 6562  (EOS — from generation_config.json)
const int SPEECH_VOCAB_SIZE      = 6_561;
const int START_SPEECH_TOKEN     = 6_561;   // tts_turbo.py: hp.start_speech_token
const int STOP_SPEECH_TOKEN      = 6_562;   // generation_config.json eos_token_id

// t3/modules/t3_config.py (turbo overrides in tts_turbo.py from_local)
// text_tokens_dict_size = 50_276  (GPT-2 vocab, turbo)
// speech_tokens_dict_size = 6_563 (includes BOS 6561 + EOS 6562)

// config.json — language_model (GPT2-medium)
// n_layer = 24, n_head = 16, n_embd = 1024
// Each KV layer stores [batch, n_head, seq, head_dim]  head_dim = n_embd / n_head = 64
const int NUM_KV_LAYERS     = 24;
const int NUM_KV_HEADS      = 16;
const int HEAD_DIM          = 64;       // 1024 / 16
const int HIDDEN_SIZE       = 1_024;    // n_embd

// tts_turbo.py defaults for generate()
const float DEFAULT_TEMPERATURE  = 0.8f;
const float DEFAULT_REP_PENALTY  = 1.2f;
const int   DEFAULT_TOP_K        = 1_000;
const float DEFAULT_TOP_P        = 0.95f;
const int   DEFAULT_MAX_TOKENS   = 1_024;

// tts_turbo.py — turbo-specific (reference values, encapsulated in speech_encoder.onnx):
//   speech_cond_prompt_len = 375  (S3 tokens in T3 cond prompt)
//   ENC_COND_LEN = 15 * 16_000 = 240_000  (16 kHz samples fed to S3Tokenizer)
//   DEC_COND_LEN = 10 * 24_000 = 240_000  (24 kHz samples fed to S3Gen)

// s3gen/utils/mel.py — mel-spectrogram for S3Gen (24 kHz reference)
// n_fft=1920, num_mels=80, sampling_rate=24000, hop_size=480, win_size=1920, fmin=0, fmax=12000
// (These are NOT used directly in the ONNX worker — the speech_encoder handles this internally.
//  Listed here for reference if you need to pre-process audio outside of the encoder.)

// s3tokenizer/s3tokenizer.py — S3 speech tokenizer (runs at 16 kHz)
// S3_SR = 16_000, n_fft=400, hop=160, win_size=400, num_mels=80
// (Also encapsulated inside speech_encoder.onnx)

// Text tokenizer (tokenizer_config.json):
//   tokenizer_class = "GPT2Tokenizer"
//   bos_token = eos_token = pad_token = "<|endoftext|>"  id=50256
//   add_prefix_space = false
//   model_max_length = 1024
// NOTE: The Python pipeline calls AutoTokenizer and feeds raw text. The ONNX pipeline
//       must replicate GPT-2 BPE tokenisation including the pre-tokenizer regex split.

// ═══════════════════════════════════════════════════════════════════════════════
// ENTRY POINT
// ═══════════════════════════════════════════════════════════════════════════════

var modelDir       = Environment.GetEnvironmentVariable("CHATTERBOX_MODELS")
                     ?? Path.Combine(AppContext.BaseDirectory, "onnx");
var tokenizerPath  = Environment.GetEnvironmentVariable("CHATTERBOX_TOKENIZER")
                     ?? Path.Combine(AppContext.BaseDirectory, "tokenizer.json");

string variant     = "q4";          // model file suffix: q4 | fp16 | q4f16 | quantized | (empty=fp32)
int    gpuDevice   = 0;             // -1 = CPU only

if (args.Length > 0)
{
    // ── CLI MODE ──────────────────────────────────────────────────────────────
    string? text      = null;
    string? voiceWav  = null;
    string? voicePack = null;
    string? outFile   = null;
    float   temp      = DEFAULT_TEMPERATURE;
    float   penalty   = DEFAULT_REP_PENALTY;
    int     topK      = DEFAULT_TOP_K;
    float   topP      = DEFAULT_TOP_P;
    int     maxTok    = DEFAULT_MAX_TOKENS;
    bool    embedMode = false;

    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--text":      text      = args[++i]; break;
            case "--voice":     voiceWav  = args[++i]; break;
            case "--voicepack": voicePack = args[++i]; break;
            case "--out":       outFile   = args[++i]; break;
            case "--temp":      temp      = float.Parse(args[++i]); break;
            case "--penalty":   penalty   = float.Parse(args[++i]); break;
            case "--topk":      topK      = int.Parse(args[++i]);   break;
            case "--topp":      topP      = float.Parse(args[++i]); break;
            case "--tokens":    maxTok    = int.Parse(args[++i]);   break;
            case "--models":    modelDir  = args[++i]; break;
            case "--tokenizer": tokenizerPath = args[++i]; break;
            case "--variant":   variant   = args[++i]; break;
            case "--gpu":       gpuDevice = int.Parse(args[++i]); break;
            case "--embed":     embedMode = true; break;
            case "--help": case "-h":
                Console.Error.WriteLine("Usage: ChatterboxTurboWorker --text \"Hello.\" --voice ref.wav --out out.wav");
                return 0;
        }

    var opts = BuildSessionOptions(gpuDevice);

    if (embedMode)
    {
        if (voiceWav is null || outFile is null)
        { Console.Error.WriteLine("ERR: --embed requires --voice and --out"); return 1; }
        using var enc = LoadSpeechEncoder(modelDir, variant, opts);
        var vp = ExtractVoicePack(voiceWav, enc);
        if (Path.GetDirectoryName(outFile) == "")
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "output");
            Directory.CreateDirectory(outDir);
            outFile = Path.Combine(outDir, outFile);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        SaveVoicePack(outFile, vp);
        Console.Error.WriteLine($"[worker] Saved VoicePack → {outFile}");
        return 0;
    }

    if (text is null)
    { Console.Error.WriteLine("ERR: --text required"); return 1; }
    if (voiceWav is null && voicePack is null)
    { Console.Error.WriteLine("ERR: --voice or --voicepack required"); return 1; }

    var (tokenizer, enc2, embedTok, lm, condDec) = LoadModels(modelDir, tokenizerPath, variant, opts);

    var vp2 = voicePack is not null
        ? LoadVoicePack(voicePack)
        : ExtractVoicePack(voiceWav!, enc2);

    var pcm = Generate(text, vp2, temp, penalty, topK, topP, maxTok, tokenizer, embedTok, lm, condDec);

    if (outFile is not null)
    {
        // If --out has no directory, default to .\output\ to keep the project root clean
        if (Path.GetDirectoryName(outFile) == "")
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "output");
            Directory.CreateDirectory(outDir);
            outFile = Path.Combine(outDir, outFile);
        }
        WriteWav(outFile, pcm);
        Console.Error.WriteLine($"[worker] Saved → {outFile}");
    }
    else PlayPcm(pcm);
    return 0;
}

// ── SERVER MODE ───────────────────────────────────────────────────────────────
Console.Error.WriteLine($"[worker] Models dir : {modelDir}");
Console.Error.WriteLine($"[worker] Tokenizer  : {tokenizerPath}");
Console.Error.WriteLine($"[worker] Variant    : {variant}  GPU: {gpuDevice}");

var serverOpts = BuildSessionOptions(gpuDevice);
var (sTok, sEnc, sEmb, sLm, sDec) = LoadModels(modelDir, tokenizerPath, variant, serverOpts);
WarmUp(sLm);
Console.Error.WriteLine("[worker] READY");

var vpCache  = new Dictionary<string, VoicePack>(StringComparer.OrdinalIgnoreCase);
using var stdout = Console.OpenStandardOutput();

string? serverLine;
while ((serverLine = Console.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(serverLine)) continue;
    try
    {
        var req = JsonSerializer.Deserialize(serverLine, WorkerJsonContext.Default.WorkerRequest);
        if (req is null) continue;

        Console.Error.WriteLine($"[worker] Generating: \"{req.Text}\"");
        float t = req.Temperature ?? DEFAULT_TEMPERATURE;
        float p = req.RepPenalty  ?? DEFAULT_REP_PENALTY;
        int   k = req.TopK        ?? DEFAULT_TOP_K;
        float pp = req.TopP       ?? DEFAULT_TOP_P;
        int   m = req.MaxTokens   ?? DEFAULT_MAX_TOKENS;

        var vp = ResolveVoicePack(req, sEnc, vpCache);
        var pcm = Generate(req.Text, vp, t, p, k, pp, m, sTok, sEmb, sLm, sDec);

        stdout.Write(BitConverter.GetBytes(pcm.Length));
        stdout.Write(pcm);
        stdout.Flush();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[worker] ERR: {ex}");
        stdout.Write(BitConverter.GetBytes(0));
        stdout.Flush();
    }
}
return 0;

// ═══════════════════════════════════════════════════════════════════════════════
// HELPERS — MODEL LOADING
// ═══════════════════════════════════════════════════════════════════════════════

SessionOptions BuildSessionOptions(int gpu)
{
    var o = new SessionOptions();
    o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
    if (gpu >= 0)
        try   { o.AppendExecutionProvider_CUDA(gpu); Console.Error.WriteLine($"[worker] CUDA EP on device {gpu}"); }
        catch (Exception ex) { Console.Error.WriteLine($"[worker] CUDA unavailable ({ex.Message}), using CPU"); }
    return o;
}

// embed_tokens runs on CPU only — GatherBlockQuantized has a CUDA invalid-argument
// bug on small sequence lengths in the q4 model. The embedding lookup is tiny so
// there is no meaningful performance loss keeping it on CPU.
SessionOptions BuildCpuOptions()
{
    var o = new SessionOptions();
    o.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
    return o;
}

// Model file name helpers
// Variants: q4 | fp16 | q4f16 | quantized | "" (fp32)
string ModelFile(string dir, string baseName, string v)
    => v == "" ? Path.Combine(dir, $"{baseName}.onnx")
               : Path.Combine(dir, $"{baseName}_{v}.onnx");

InferenceSession LoadSpeechEncoder(string dir, string v, SessionOptions o)
{
    // Prefer q4, fall back to fp16 then fp32
    foreach (var candidate in new[] { v, "q4", "fp16", "" })
    {
        var f = ModelFile(dir, "speech_encoder", candidate);
        if (File.Exists(f)) { Console.Error.WriteLine($"[worker] speech_encoder: {Path.GetFileName(f)}"); return new InferenceSession(f, o); }
    }
    throw new FileNotFoundException("speech_encoder*.onnx not found in " + dir);
}

(Gpt2Tokenizer, InferenceSession, InferenceSession, InferenceSession, InferenceSession)
    LoadModels(string dir, string tokPath, string v, SessionOptions o)
{
    Console.Error.WriteLine("[worker] Loading tokenizer …");
    var tok = Gpt2Tokenizer.Load(tokPath);

    Console.Error.WriteLine("[worker] Loading ONNX models …");
    var enc     = LoadSpeechEncoder(dir, v, o);

    // embed_tokens: always CPU — GatherBlockQuantized CUDA bug on small seq lengths.
    // fp16 variant preferred (smaller, still float output); fall back through q4 → fp32.
    var embedFile = new[] { "fp16", v, "q4", "" }.Select(s => ModelFile(dir, "embed_tokens", s)).First(File.Exists);
    Console.Error.WriteLine($"[worker] embed_tokens: {Path.GetFileName(embedFile)} [CPU]");
    var emb = new InferenceSession(embedFile, BuildCpuOptions());

    var lmFile = new[] { v, "q4", "fp16", "" }.Select(s => ModelFile(dir, "language_model", s)).First(File.Exists);
    Console.Error.WriteLine($"[worker] language_model: {Path.GetFileName(lmFile)}");
    var lm = new InferenceSession(lmFile, o);

    var decFile = new[] { v, "q4", "fp16", "" }.Select(s => ModelFile(dir, "conditional_decoder", s)).First(File.Exists);
    Console.Error.WriteLine($"[worker] conditional_decoder: {Path.GetFileName(decFile)}");
    var dec = new InferenceSession(decFile, o);

    return (tok, enc, emb, lm, dec);
}

// ═══════════════════════════════════════════════════════════════════════════════
// VOICE PACK EXTRACTION
// ═══════════════════════════════════════════════════════════════════════════════
// speech_encoder.onnx inputs / outputs (from ONNX model introspection):
//   Input:  "audio_values"  float32[1, num_samples]  — 24 kHz mono PCM (S3GEN_SR)
//   Outputs (4):
//     [0] cond_emb          float32[1, seq, 1024]     — conditioning embedding for language_model
//     [1] prompt_token      int64 [1, prompt_len]     — S3 speech tokens (375 max) for cond prompt
//     [2] speaker_emb       float32[1, 256]           — VoiceEncoder embedding (speaker identity)
//     [3] speaker_features  float32[1, ?, 80]         — mel features for S3Gen ref dict
//
// The encoder internally:
//   • Runs the VoiceEncoder (melspec at 16kHz, 40-mel, n_fft=400, hop=160, win=400)
//   • Runs the S3Tokenizer  (16kHz, n_fft=400, 80-mel, hop=160 → 25 tokens/sec)
//   • Computes a mel-spectrogram at 24kHz (n_fft=1920, 80-mel, hop=480) for S3Gen prompt_feat
//   • Projects speech_emb through embed layer to build cond_emb for the LM

VoicePack ExtractVoicePack(string wavPath, InferenceSession enc)
{
    Console.Error.WriteLine($"[worker] Extracting VoicePack from: {wavPath}");
    // Load reference WAV — must be at least 5 s; 24 kHz mono (S3GEN_SR)
    var samples = LoadWavMono(wavPath, SAMPLE_RATE);
    if (samples.Length < 5 * SAMPLE_RATE)
        throw new InvalidDataException($"Reference audio must be ≥ 5 seconds (got {samples.Length / (float)SAMPLE_RATE:F1} s)");

    var audioTensor = new DenseTensor<float>(samples, new[] { 1, samples.Length });
    var outs = enc.Run(new[] { NamedOnnxValue.CreateFromTensor("audio_values", audioTensor) }).ToList();

    var condEmb      = (DenseTensor<float>)outs[0].Value;
    var promptToken  = (DenseTensor<long>) outs[1].Value;
    var speakerEmb   = (DenseTensor<float>)outs[2].Value;
    var spkFeatures  = (DenseTensor<float>)outs[3].Value;

    return new VoicePack(
        condEmb.ToArray(),     condEmb.Dimensions.ToArray(),
        promptToken.ToArray(), promptToken.Dimensions.ToArray(),
        speakerEmb.ToArray(),  speakerEmb.Dimensions.ToArray(),
        spkFeatures.ToArray(), spkFeatures.Dimensions.ToArray());
}

// ═══════════════════════════════════════════════════════════════════════════════
// TEXT PRE-PROCESSING  (punc_norm from tts_turbo.py)
// ═══════════════════════════════════════════════════════════════════════════════
// The Python pipeline runs punc_norm() before tokenisation.
// Replicate the key transforms here so the token distribution matches training.
string PuncNorm(string text)
{
    if (text.Length == 0) return "You need to add some text for me to talk.";
    // Capitalise first letter
    if (char.IsLower(text[0])) text = char.ToUpper(text[0]) + text[1..];
    // Collapse multiple spaces
    text = Regex.Replace(text, @" {2,}", " ").Trim();
    // Replace uncommon punctuation
    text = text.Replace("…", ", ").Replace(":", ",").Replace("—", "-").Replace("–", "-")
               .Replace(" ,", ",").Replace("\u201C", "\"").Replace("\u201D", "\"")
               .Replace("\u2018", "'").Replace("\u2019", "'");
    // Ensure trailing sentence-ender
    text = text.TrimEnd();
    if (!new[] { '.', '!', '?', '-', ',' }.Contains(text[^1])) text += '.';
    return text;
}

// ═══════════════════════════════════════════════════════════════════════════════
// MAIN GENERATE PIPELINE
// ═══════════════════════════════════════════════════════════════════════════════
byte[] Generate(string rawText, VoicePack vp,
    float temperature, float repPenalty, int topK, float topP, int maxTokens,
    Gpt2Tokenizer tokenizer, InferenceSession embedTok, InferenceSession lm, InferenceSession condDec)
{
    // ── Step 0: Text pre-processing (mirrors Python punc_norm + AutoTokenizer) ──
    string text = PuncNorm(rawText);
    Console.Error.WriteLine($"[worker] Text (normalised): {text}");

    // ── Step 1: Rebuild tensors from VoicePack ────────────────────────────────
    var condEmb         = new DenseTensor<float>(vp.CondEmb,         vp.CondEmbShape);
    var promptToken     = new DenseTensor<long> (vp.PromptToken,     vp.PromptTokenShape);
    var speakerEmbTens  = new DenseTensor<float>(vp.SpeakerEmb,      vp.SpeakerEmbShape);
    var spkFeatTens     = new DenseTensor<float>(vp.SpeakerFeatures, vp.SpeakerFeaturesShape);

    int condSeqLen = condEmb.Dimensions[1];

    // ── Step 2: GPT-2 BPE tokenise text ──────────────────────────────────────
    // tokenizer_config.json: add_prefix_space=false, model_max_length=1024
    // The Python AutoTokenizer.encode() returns plain token ids (no special tokens added
    // by default for GPT2 unless explicitly requested). Chatterbox feeds raw text_tokens
    // directly; the T3Config start/stop text tokens (255 / 0) are not used in turbo mode.
    int[] textIds = tokenizer.Encode(text);
    Console.Error.WriteLine($"[worker] Text token count: {textIds.Length}  [{string.Join(",", textIds.Take(10))}…]");

    // ── Step 3 + 4: Embed [text tokens | BOS speech token] in ONE call ──────────
    // embed_tokens.onnx has ONE input "input_ids" and uses baked Slice constants to
    // split it positionally: text tokens come FIRST, speech tokens LAST.
    // From the ONNX proto symbolic shapes:
    //   Slice_output_0   shape = (batch, sequence_length - 2) → TEXT portion
    //   Slice_1_output_0 shape = (batch, 2)                  → SPEECH portion  (always 2)
    // The model was exported with a fixed 2-token speech suffix, so the Slice constants are:
    //   text  = input_ids[:, 0 : seq_len-2]
    //   speech= input_ids[:, seq_len-2 : seq_len]
    //
    // Wait — the speech output shape "sequence_length - 2" and text "sequence_length" being LARGER
    // than the speech portion means: text_portion_count = total_count - speech_count.
    // Since the export fixed speech_count=2, we must ALWAYS pass exactly 2 speech tokens appended.
    // For the prefill step those 2 are [some_pad | BOS] or [BOS | BOS] — but actually
    // the Python exports with 1 speech token (BOS) padded to len=2 with a duplicate.
    // Safest: pass [text_ids | BOS | BOS] (2 copies of BOS to satisfy the fixed-2 split).
    //
    // Text token IDs do NOT need offset. The ONNX graph has separate Gather nodes for
    // text and speech, fed by separate Slices.
    // Text slice feeds text_emb.weight; Speech slice feeds speech_emb.weight.
    //
    // IMPORTANT: embed_tokens output = [text_embeds | speech_embeds] concatenated along seq dim.
    // We take only the text portion for the prefill and the BOS embed for the final token.
    // In the autoregressive loop each speech token is passed as: [dummy_text | next_speech_token]
    // with 1 dummy text token — but since text_count = total-2 that would require total=3 (1 text + 2 speech).
    //
    // Actually the simplest correct approach confirmed by the graph:
    //   Prefill embed call: input_ids = [t0, t1, ..., tN, BOS, BOS]  → (N+2) tokens
    //   Output: text_embeds[0:N] ++ speech_embeds[0:2] — take ALL N+2 embeddings
    //   The LM then gets: [condEmb | all_N+2_embeds]
    //
    //   Loop embed call: input_ids = [START_SPEECH_TOKEN, nextSpeechToken] → (2 tokens, 1 dummy speech + 1 speech)
    //   Output: [dummy_speech_embed | speech_embed] — take only the last 1 (speech embed)
    //   Wait - if total=2, Text slice is 0:0 (empty), Speech slice is 0:2 (both).
    //   So BOTH tokens go to speech_emb.
    //   So the dummy token must be a valid SPEECH token ID.

    // Build prefill input_ids: text tokens + BOS speech token (×2 for fixed-2 speech slice)
    var prefillIds = new long[textIds.Length + 2];
    for (int i = 0; i < textIds.Length; i++) prefillIds[i] = textIds[i];
    prefillIds[textIds.Length]     = START_SPEECH_TOKEN;
    prefillIds[textIds.Length + 1] = START_SPEECH_TOKEN;

    var prefillTensor  = new DenseTensor<long>(prefillIds, new[] { 1, prefillIds.Length });
    var prefillEmbeds  = (DenseTensor<float>)embedTok
        .Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", prefillTensor) })
        .First().Value;  // shape [1, textIds.Length+2, 1024]

    int textLen  = textIds.Length;
    int totalSeq = condSeqLen + textLen + 2; // cond + text + 2 speech (BOS × 2)

    var initBuf = new float[totalSeq * HIDDEN_SIZE];
    condEmb.Buffer.Span.CopyTo(initBuf.AsSpan(0));
    prefillEmbeds.Buffer.Span.CopyTo(initBuf.AsSpan(condSeqLen * HIDDEN_SIZE));

    var inputsEmbeds = new DenseTensor<float>(initBuf, new[] { 1, totalSeq, HIDDEN_SIZE });
    var attMask = Ones1D(totalSeq);
    var posIds  = Range1D(0, totalSeq);

    // ── Step 5: Autoregressive generation with KV-cache ───────────────────────
    // language_model.onnx inputs:
    //   "inputs_embeds"          float32[1, seq, 1024]
    //   "attention_mask"         int64  [1, total_seq]
    //   "position_ids"           int64  [1, seq]
    //   "past_key_values.{L}.key"   float32 or float16 [1, 16, past_len, 64]  (L = 0…23)
    //   "past_key_values.{L}.value" float32 or float16 [1, 16, past_len, 64]
    //
    // language_model.onnx outputs:
    //   [0] "logits"                 float32[1, seq, vocab_size]
    //   [1..] "present.{L}.key"      float32 or float16 [1, 16, new_len, 64]
    //          "present.{L}.value"   float32 or float16 [1, 16, new_len, 64]
    //
    // Vocab size = speech_tokens_dict_size = 6563 (speech-only head output)

    var pastKV = InitKvCache(fp16: false);
    var generated = new List<long> { START_SPEECH_TOKEN };
    List<DisposableNamedOnnxValue>? prevOuts = null;

    for (int step = 0; step < maxTokens; step++)
    {
        var lmInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("inputs_embeds",  inputsEmbeds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attMask),
            NamedOnnxValue.CreateFromTensor("position_ids",   posIds),
        };
        foreach (var kv in pastKV.Values) lmInputs.Add(kv);

        var lmOuts   = lm.Run(lmInputs).ToList();
        var logits   = (DenseTensor<float>)lmOuts[0].Value;  // [1, seq, vocab]
        int vocabSz  = logits.Dimensions[2];
        // Slice last token's logits
        var lastLogits = logits.Buffer.Span.Slice((logits.Dimensions[1] - 1) * vocabSz, vocabSz).ToArray();

        // Sampling:
        //   inference_turbo uses multinomial sampling with temperature + topK + topP
        //   (RepetitionPenalty also applied)
        ApplyRepetitionPenalty(lastLogits, generated, repPenalty);
        long nextToken = Sample(lastLogits, temperature, topK, topP);
        generated.Add(nextToken);

        prevOuts?.ForEach(o => o.Dispose());
        prevOuts = lmOuts;
        UpdateKvCache(pastKV, lmOuts);

        if (nextToken == STOP_SPEECH_TOKEN) break;

        // Embed the next speech token.
        // embed_tokens always requires (text_count + 2) tokens positionally.
        // For single-step: pass [START_SPEECH_TOKEN | nextToken] → 2 tokens total (0 text + 2 speech).
        // Since input length is 2, text slice is empty, speech slice is both tokens.
        // Both go to speech_emb. START_SPEECH_TOKEN is a valid speech token ID (6561).
        // Then take only the last token's embedding (the speech embed).
        var nextIdTensor = new DenseTensor<long>(
            new long[] { START_SPEECH_TOKEN, nextToken }, new[] { 1, 2 });
        var stepEmbedFull = (DenseTensor<float>)embedTok
            .Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", nextIdTensor) })
            .First().Value;  // [1, 2, 1024] — take last token (index 1 = speech embed)
        var stepBuf = new float[HIDDEN_SIZE];
        stepEmbedFull.Buffer.Span.Slice(HIDDEN_SIZE, HIDDEN_SIZE).CopyTo(stepBuf);
        inputsEmbeds = new DenseTensor<float>(stepBuf, new[] { 1, 1, HIDDEN_SIZE });

        // Extend attention mask by 1; position_ids = last_pos + 1
        long prevPos    = posIds.Buffer.Span[posIds.Buffer.Length - 1];
        var  newAttBuf  = new long[attMask.Dimensions[1] + 1];
        attMask.Buffer.Span.CopyTo(newAttBuf);
        newAttBuf[^1]   = 1L;
        attMask         = new DenseTensor<long>(newAttBuf, new[] { 1, newAttBuf.Length });
        posIds          = new DenseTensor<long>(new[] { prevPos + 1 }, new[] { 1, 1 });
    }
    prevOuts?.ForEach(o => o.Dispose());
    Console.Error.WriteLine($"[worker] Generated {generated.Count - 1} speech tokens");

    // ── Step 6: Build speech token sequence for decoder ───────────────────────
    // tts_turbo.py:
    //   speech_tokens = speech_tokens[speech_tokens < 6561]         # drop OOV (BOS/EOS)
    //   speech_tokens = torch.cat([speech_tokens, silence ×3])      # S3GEN_SIL appended
    //
    // OnnxWorker pattern prepends the prompt tokens (ref audio tokens) so the decoder
    // has the full reference context for voice cloning:
    //   [promptToken] ++ [generated_speech_tokens (valid only)] ++ [SIL SIL SIL]
    var speechToks = new List<long>(promptToken.Buffer.ToArray());
    foreach (var t in generated.Skip(1))     // skip BOS at index 0
    {
        if (t == STOP_SPEECH_TOKEN) break;
        if (t < SPEECH_VOCAB_SIZE) speechToks.Add(t);   // drop OOV tokens
    }
    // Trim trailing silence, then append exactly 3 silence tokens
    while (speechToks.Count > 0 && speechToks[^1] == SILENCE_TOKEN) speechToks.RemoveAt(speechToks.Count - 1);
    speechToks.AddRange(new long[] { SILENCE_TOKEN, SILENCE_TOKEN, SILENCE_TOKEN });

    // ── Step 7: Conditional decoder — speech tokens → PCM ────────────────────
    // conditional_decoder.onnx inputs:
    //   "speech_tokens"      int64  [1, total_tokens]
    //   "speaker_embeddings" float32[1, 256]            — VoiceEncoder embed (speaker_emb)
    //   "speaker_features"   float32[1, mel_len, 80]    — 24 kHz mel (prompt_feat from ref)
    //
    // conditional_decoder.onnx output:
    //   [0] waveform         float32[1, num_samples]    — 24 kHz mono PCM float
    //
    // Internally this runs:
    //   1. S3Token2Wav CFM (flow-matching, n_cfm_timesteps=2 for turbo/meanflow)
    //   2. HiFTGenerator (HiFiGAN vocoder, upsample_rates=[8,5,3], output at S3GEN_SR=24000)
    var stArr   = speechToks.ToArray();
    var decOuts = condDec.Run(new[]
    {
        NamedOnnxValue.CreateFromTensor("speech_tokens",      new DenseTensor<long> (stArr, new[] { 1, stArr.Length })),
        NamedOnnxValue.CreateFromTensor("speaker_embeddings", speakerEmbTens),
        NamedOnnxValue.CreateFromTensor("speaker_features",   spkFeatTens),
    }).ToList();

    var waveform = ((DenseTensor<float>)decOuts[0].Value).Buffer.ToArray();
    Console.Error.WriteLine($"[worker] Waveform: {waveform.Length} samples ({waveform.Length / (float)SAMPLE_RATE:F2} s)");

    return FloatToPcm16(waveform);
}

// ═══════════════════════════════════════════════════════════════════════════════
// KV-CACHE MANAGEMENT
// ═══════════════════════════════════════════════════════════════════════════════
Dictionary<string, NamedOnnxValue> InitKvCache(bool fp16 = false)
{
    var c = new Dictionary<string, NamedOnnxValue>(NUM_KV_LAYERS * 2);
    for (int l = 0; l < NUM_KV_LAYERS; l++)
    {
        var emptyShape = new[] { 1, NUM_KV_HEADS, 0, HEAD_DIM };
        if (fp16)
        {
            c[$"past_key_values.{l}.key"]   = NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.key",   new DenseTensor<Float16>(emptyShape));
            c[$"past_key_values.{l}.value"] = NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.value", new DenseTensor<Float16>(emptyShape));
        }
        else
        {
            c[$"past_key_values.{l}.key"]   = NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.key",   new DenseTensor<float>(emptyShape));
            c[$"past_key_values.{l}.value"] = NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.value", new DenseTensor<float>(emptyShape));
        }
    }
    return c;
}

void UpdateKvCache(Dictionary<string, NamedOnnxValue> cache, List<DisposableNamedOnnxValue> outs)
{
    // Output layout: [0]=logits, [1+l*2]=present.key, [2+l*2]=present.value
    for (int l = 0; l < NUM_KV_LAYERS; l++)
    {
        var kTens = outs[1 + l * 2].Value;
        var vTens = outs[2 + l * 2].Value;
        cache[$"past_key_values.{l}.key"]   = kTens is DenseTensor<Float16> kf16
            ? NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.key",   kf16)
            : NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.key",   (DenseTensor<float>)kTens);
        cache[$"past_key_values.{l}.value"] = vTens is DenseTensor<Float16> vf16
            ? NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.value", vf16)
            : NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.value", (DenseTensor<float>)vTens);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// SAMPLING  (mirrors inference_turbo logits_processors)
// ═══════════════════════════════════════════════════════════════════════════════
// Python: temperature → top_k → top_p → multinomial
void ApplyRepetitionPenalty(float[] logits, List<long> genIds, float penalty)
{
    if (penalty == 1.0f) return;
    foreach (var t in new HashSet<long>(genIds))
        if (t >= 0 && t < logits.Length)
            logits[t] = logits[t] < 0f ? logits[t] * penalty : logits[t] / penalty;
}

long Sample(float[] logits, float temperature, int topK, float topP)
{
    // 1. Temperature
    if (temperature > 0f && temperature != 1.0f)
        for (int i = 0; i < logits.Length; i++) logits[i] /= temperature;

    // 2. Top-K: zero out all but top-k
    if (topK > 0 && topK < logits.Length)
    {
        var threshold = logits.OrderByDescending(x => x).ElementAt(topK - 1);
        for (int i = 0; i < logits.Length; i++) if (logits[i] < threshold) logits[i] = float.NegativeInfinity;
    }

    // 3. Softmax
    float maxL = float.NegativeInfinity;
    for (int i = 0; i < logits.Length; i++) if (logits[i] > maxL) maxL = logits[i];
    var probs = new float[logits.Length];
    float sum = 0f;
    for (int i = 0; i < logits.Length; i++) { probs[i] = MathF.Exp(logits[i] - maxL); sum += probs[i]; }
    for (int i = 0; i < probs.Length;  i++) probs[i] /= sum;

    // 4. Top-P (nucleus)
    if (topP < 1.0f)
    {
        var sorted = probs.Select((p, i) => (p, i)).OrderByDescending(x => x.p).ToArray();
        float cumul = 0f;
        bool mask   = false;
        foreach (var (p, idx) in sorted)
        {
            if (mask) { probs[idx] = 0f; continue; }
            cumul += p;
            if (cumul > topP) mask = true;
        }
        // Re-normalise
        sum = probs.Sum();
        for (int i = 0; i < probs.Length; i++) probs[i] /= sum;
    }

    // 5. Multinomial sample
    float r = (float)Random.Shared.NextDouble();
    float acc = 0f;
    for (int i = 0; i < probs.Length; i++) { acc += probs[i]; if (r < acc) return i; }
    return probs.Length - 1;
}

// ═══════════════════════════════════════════════════════════════════════════════
// TENSOR UTILITIES
// ═══════════════════════════════════════════════════════════════════════════════
DenseTensor<long> Ones1D(int len)
    => new DenseTensor<long>(Enumerable.Repeat(1L, len).ToArray(), new[] { 1, len });

DenseTensor<long> Range1D(int start, int count)
    => new DenseTensor<long>(Enumerable.Range(start, count).Select(i => (long)i).ToArray(), new[] { 1, count });

// ═══════════════════════════════════════════════════════════════════════════════
// WARM-UP
// ═══════════════════════════════════════════════════════════════════════════════
void WarmUp(InferenceSession lmSession)
{
    Console.Error.WriteLine("[worker] Warming up language_model …");
    var dummy = new DenseTensor<float>(new float[HIDDEN_SIZE], new[] { 1, 1, HIDDEN_SIZE });
    var kv    = InitKvCache(fp16: false);
    var inp   = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("inputs_embeds",  dummy),
        NamedOnnxValue.CreateFromTensor("attention_mask", Ones1D(1)),
        NamedOnnxValue.CreateFromTensor("position_ids",   Range1D(0, 1)),
    };
    foreach (var k in kv.Values) inp.Add(k);
    lmSession.Run(inp).Dispose();
    Console.Error.WriteLine("[worker] Warm-up complete.");
}

// ═══════════════════════════════════════════════════════════════════════════════
// VOICE PACK RESOLVER
// ═══════════════════════════════════════════════════════════════════════════════
VoicePack ResolveVoicePack(WorkerRequest req, InferenceSession enc, Dictionary<string, VoicePack> cache)
{
    if (!string.IsNullOrEmpty(req.VoicePack))
    {
        if (!cache.TryGetValue(req.VoicePack, out var vp)) { vp = LoadVoicePack(req.VoicePack); cache[req.VoicePack] = vp; }
        return vp;
    }
    if (!string.IsNullOrEmpty(req.Voice))
    {
        if (!cache.TryGetValue(req.Voice, out var vp)) { vp = ExtractVoicePack(req.Voice, enc); cache[req.Voice] = vp; }
        return vp;
    }
    throw new InvalidOperationException("Request must include Voice or VoicePack");
}

// ═══════════════════════════════════════════════════════════════════════════════
// AUDIO I/O
// ═══════════════════════════════════════════════════════════════════════════════
float[] LoadWavMono(string path, int targetSr)
{
    // Supports WAV, MP3, M4A, FLAC, OGG, and any format ffmpeg handles.
    // ffmpeg decodes → mono → target sample rate → raw float32 LE on stdout.
    // Requires ffmpeg.exe on PATH.
    var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg",
        $"-hide_banner -loglevel error -i \"{path}\" -ac 1 -ar {targetSr} -f f32le -")
    {
        UseShellExecute        = false,
        RedirectStandardOutput = true,
    };
    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new Exception("ffmpeg not found on PATH. Install ffmpeg and ensure it is on PATH.");
    using var ms = new MemoryStream();
    proc.StandardOutput.BaseStream.CopyTo(ms);
    proc.WaitForExit();
    if (proc.ExitCode != 0) throw new Exception($"ffmpeg exited {proc.ExitCode} decoding: {path}");
    var raw = ms.ToArray();
    var samples = new float[raw.Length / 4];
    Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);
    return samples;
}

void WriteWav(string path, byte[] pcm)
{
    using var bw = new BinaryWriter(File.Create(path));
    bw.Write(Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(36 + pcm.Length);
    bw.Write(Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(Encoding.ASCII.GetBytes("fmt "));
    bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(SAMPLE_RATE); bw.Write(SAMPLE_RATE * 2);
    bw.Write((short)2); bw.Write((short)16);
    bw.Write(Encoding.ASCII.GetBytes("data"));
    bw.Write(pcm.Length);
    bw.Write(pcm);
}

byte[] FloatToPcm16(float[] s)
{
    var b = new byte[s.Length * 2];
    for (int i = 0; i < s.Length; i++)
    {
        short v = (short)(Math.Clamp(s[i], -1f, 1f) * 32767f);
        BitConverter.TryWriteBytes(b.AsSpan(i * 2), v);
    }
    return b;
}

void PlayPcm(byte[] pcm)
{
    var si = new System.Diagnostics.ProcessStartInfo("ffplay",
        "-f s16le -ar 24000 -ac 1 -nodisp -autoexit -loglevel quiet -")
    { UseShellExecute = false, RedirectStandardInput = true };
    var p = System.Diagnostics.Process.Start(si)!;
    p.StandardInput.BaseStream.Write(pcm);
    p.StandardInput.Close();
    p.WaitForExit();
}

void SaveVoicePack(string path, VoicePack vp)
    => File.WriteAllText(path, JsonSerializer.Serialize(vp, VoicePackJsonContext.Default.VoicePack), Encoding.UTF8);

VoicePack LoadVoicePack(string path)
    => JsonSerializer.Deserialize(File.ReadAllText(path), VoicePackJsonContext.Default.VoicePack)!;

// ═══════════════════════════════════════════════════════════════════════════════
// GPT-2 BPE TOKENIZER
// Reads tokenizer.json  (HuggingFace format with model.vocab, model.merges, added_tokens)
// Replicates Python transformers GPT2Tokenizer behaviour:
//   • add_prefix_space = false
//   • bytes_to_unicode mapping
//   • pre-tokenizer regex split (same regex as Python tiktoken/GPT-2)
//   • BPE merge
// ═══════════════════════════════════════════════════════════════════════════════
class Gpt2Tokenizer
{
    static readonly Regex _preTok = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Dictionary<int, char> _b2u;

    static Gpt2Tokenizer()
    {
        // GPT-2 bytes_to_unicode()
        var bs = Enumerable.Range('!', '~'-'!'+1)
                           .Concat(Enumerable.Range(0xA1, 0xAC-0xA1+1))
                           .Concat(Enumerable.Range(0xAE, 0xFF-0xAE+1)).ToList();
        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++) if (!bs.Contains(b)) { bs.Add(b); cs.Add(256 + n++); }
        _b2u = new Dictionary<int, char>(bs.Count);
        for (int i = 0; i < bs.Count; i++) _b2u[bs[i]] = (char)cs[i];
    }

    readonly Dictionary<string, int>          _vocab;
    readonly Dictionary<(string, string), int> _merges;
    readonly Dictionary<string, int>           _added;    // added_tokens (event tags etc.)

    Gpt2Tokenizer(Dictionary<string, int> vocab, Dictionary<(string,string),int> merges, Dictionary<string,int> added)
    { _vocab = vocab; _merges = merges; _added = added; }

    public static Gpt2Tokenizer Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var vocab = new Dictionary<string, int>();
        foreach (var p in doc.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject())
            vocab[p.Name] = p.Value.GetInt32();

        var merges = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var m in doc.RootElement.GetProperty("model").GetProperty("merges").EnumerateArray())
        {
            string[] parts = m.ValueKind == JsonValueKind.Array
                ? new[] { m[0].GetString()!, m[1].GetString()! }
                : m.GetString()!.Split(' ');
            if (parts.Length == 2) merges[(parts[0], parts[1])] = rank++;
        }

        var added = new Dictionary<string, int>();
        if (doc.RootElement.TryGetProperty("added_tokens", out var at))
            foreach (var t in at.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object)
                    added[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt32();

        return new Gpt2Tokenizer(vocab, merges, added);
    }

    public int[] Encode(string text)
    {
        var result = new List<int>();
        // Split on added tokens first (event tags like "[chuckle]")
        var parts = SplitOnAdded(text);
        foreach (var part in parts)
        {
            if (_added.TryGetValue(part, out int addedId)) { result.Add(addedId); continue; }
            foreach (Match m in _preTok.Matches(part))
            {
                var sb = new StringBuilder();
                foreach (byte b in Encoding.UTF8.GetBytes(m.Value)) sb.Append(_b2u[b]);
                foreach (var tok in Bpe(sb.ToString()))
                    if (_vocab.TryGetValue(tok, out int id)) result.Add(id);
            }
        }
        return result.ToArray();
    }

    List<string> SplitOnAdded(string text)
    {
        var res = new List<string> { text };
        foreach (var delim in _added.Keys.OrderByDescending(k => k.Length))
        {
            var next = new List<string>();
            foreach (var s in res)
            {
                if (_added.ContainsKey(s)) { next.Add(s); continue; }
                var splits = s.Split(new[] { delim }, StringSplitOptions.None);
                for (int i = 0; i < splits.Length; i++) { if (i > 0) next.Add(delim); if (splits[i].Length > 0) next.Add(splits[i]); }
            }
            res = next;
        }
        return res;
    }

    List<string> Bpe(string word)
    {
        if (word.Length <= 1) return new List<string> { word };
        var parts = word.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            int bestRank = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < parts.Count - 1; i++)
                if (_merges.TryGetValue((parts[i], parts[i+1]), out int r) && r < bestRank)
                { bestRank = r; bestIdx = i; }
            if (bestIdx < 0) break;
            parts[bestIdx] += parts[bestIdx + 1];
            parts.RemoveAt(bestIdx + 1);
        }
        return parts;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// DATA TYPES
// ═══════════════════════════════════════════════════════════════════════════════

// VoicePack: serialisable snapshot of speech_encoder outputs for a reference voice.
// Flat arrays + shape arrays for JSON-friendly storage.
record VoicePack(
    float[] CondEmb,         int[] CondEmbShape,
    long[]  PromptToken,     int[] PromptTokenShape,
    float[] SpeakerEmb,      int[] SpeakerEmbShape,
    float[] SpeakerFeatures, int[] SpeakerFeaturesShape);

// Server protocol: one JSON line in → 4-byte-length + PCM bytes out
record WorkerRequest(
    string  Text,
    string? Voice       = null,
    string? VoicePack   = null,
    float?  Temperature = null,
    float?  RepPenalty  = null,
    int?    TopK        = null,
    float?  TopP        = null,
    int?    MaxTokens   = null);

[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(VoicePack))]
internal partial class WorkerJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(VoicePack))]
internal partial class VoicePackJsonContext : JsonSerializerContext { }