using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Subsystem.Dpx;
using Subsystem.RuntimeBroker;

namespace Subsystem.Windows
{
    // ss dpx-generate "<prompt>" — the resident proof-of-life for CRQ166/CRQ144/CRQ157: interrogate a
    // gemma4-e2b q4 ONNX export IN-PROC, no dp-onnx.exe child process, no RuntimeBroker (Dpx is
    // native-native, no boundary — this mirrors Chat.cs's shape only for the CLI dispatch/discovery
    // convention, not for Rb). Same discovery doctrine as Chat.cs: env override, else drive-derived dir,
    // never a hardcoded path — models are found, not baked in.
    internal static class DpxGenerate
    {
        public static int Run(string[] args)
        {
            // `--gpu-matmul auto` (CRQ190 R0): per-shape CPU/GPU routing for MatMulNBits. The mode rides
            // DPGPU_BACKEND, which Dp hoists into a static readonly on first touch - so the variable must
            // be set HERE, before anything below initializes a Dp static. Bare `--gpu-matmul` stays the
            // all-or-nothing q4 GPU knob.
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--gpu-matmul" && string.Equals(args[i + 1], "auto", StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("DPGPU_BACKEND", "auto");

            bool verbose = args.Contains("--verbose") || args.Contains("-v") || args.Contains("-verbose");
            bool profile = args.Contains("--profile");
            var clean = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--verbose" || a == "-v" || a == "-verbose" || a == "--profile") continue;
                if (a == "--gpu-matmul")
                {
                    if (i + 1 < args.Length && string.Equals(args[i + 1], "auto", StringComparison.OrdinalIgnoreCase)) i++;   // env already set above
                    else Dp.UseGpuMatMulNBits = true;
                    continue;
                }
                clean.Add(a);
            }
            var cleanArgs = clean.ToArray();

            if (cleanArgs.Length == 0)
            {
                Console.Error.WriteLine("Error: Please provide a prompt. Usage: ss dpx-generate \"<prompt>\" [maxNewTokens] [--verbose]");
                return 1;
            }
            string prompt = cleanArgs[0];
            int maxNew = cleanArgs.Length > 1 && int.TryParse(cleanArgs[1], out var mn) ? mn : 64;

            string modelsDir = Environment.GetEnvironmentVariable("SS_MODELS") ?? "";
            if (string.IsNullOrEmpty(modelsDir))
            {
                var driveRoot = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
                modelsDir = Path.Combine(driveRoot, "modeldb");
            }

            if (!Directory.Exists(modelsDir))
            {
                Console.Error.WriteLine($"Error: models dir not found: {modelsDir}");
                Console.Error.WriteLine("Set SS_MODELS to the directory holding the gemma4 q4 .db pair + .spm tokenizer.");
                return 1;
            }

            string? embedDb = Directory.EnumerateFiles(modelsDir, "*-onnx-embed-q4.db").FirstOrDefault();
            string? decoderDb = Directory.EnumerateFiles(modelsDir, "*-onnx-decoder-q4.db").FirstOrDefault();
            string? spm = Directory.EnumerateFiles(modelsDir, "*.spm").FirstOrDefault();

            if (embedDb == null || decoderDb == null || spm == null)
            {
                Console.Error.WriteLine($"Error: could not discover the embed/decoder .db pair + .spm tokenizer under {modelsDir}");
                Console.Error.WriteLine($"  embed:   {embedDb ?? "MISSING (*-onnx-embed-q4.db)"}");
                Console.Error.WriteLine($"  decoder: {decoderDb ?? "MISSING (*-onnx-decoder-q4.db)"}");
                Console.Error.WriteLine($"  spm:     {spm ?? "MISSING (*.spm)"}");
                return 1;
            }

            string unitId = Path.GetFileNameWithoutExtension(decoderDb);
            Console.WriteLine($"[DPX] embed={Path.GetFileName(embedDb)} decoder={Path.GetFileName(decoderDb)} spm={Path.GetFileName(spm)}");

            Dg.ConsoleVerbose = verbose;
            if (profile) { Dp.Profile = true; Dp.Prof.Clear(); }
            using var decoder = new DpxDecoder(embedDb, decoderDb, spm, unitId, maxTokens: maxNew) { Verbose = verbose };
            var fault = decoder.BringUp();
            if (fault != null)
            {
                Console.Error.WriteLine($"Error: BringUp failed. Class: {fault.Class}, Detail: {fault.NativeDetail}");
                return 1;
            }

            Console.WriteLine($"[DPX] running prompt in-proc (no standalone CLI): \"{prompt}\"");
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            DpxStreamOutput(decoder, prompt, cts.Token);
            Console.WriteLine();
            if (profile) PrintProfile();
            return 0;
        }

        // The named-hot-op receipt (CRQ190 step 1): Dp.Prof accumulates (total ms, call count) per ONNX
        // OpType across every Dispatch call in the run — prefill AND decode. Sorted by total ms so the
        // hottest op class is the first line, not a guess.
        private static void PrintProfile()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[DPX Op Profile]  (op | total ms | calls | ms/call)");
            double grandTotal = Dp.Prof.Values.Sum(e => e.ms);
            foreach (var kv in Dp.Prof.OrderByDescending(e => e.Value.ms))
            {
                double pct = grandTotal > 0 ? kv.Value.ms / grandTotal * 100.0 : 0;
                Console.WriteLine($"  {kv.Key,-24} {kv.Value.ms,10:F1} ms  {kv.Value.n,6} calls  {kv.Value.ms / kv.Value.n,8:F3} ms/call  ({pct:F1}%)");
            }
            Console.WriteLine($"  {"TOTAL",-24} {grandTotal,10:F1} ms");
            Console.ResetColor();
        }

        private static void DpxStreamOutput(DpxDecoder decoder, string prompt, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tStart = sw.ElapsedTicks;
            long tFirstToken = 0;
            int genTokensCount = 0;

            try
            {
                var enumerator = decoder.StreamTurnAsync(prompt, null, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                    {
                        var delta = enumerator.Current;
                        if (delta.Kind == AgentDeltaKind.Token && !string.IsNullOrEmpty(delta.Text))
                        {
                            if (tFirstToken == 0)
                            {
                                tFirstToken = sw.ElapsedTicks;
                            }
                            Console.Write(delta.Text);
                            genTokensCount++;
                        }
                        else if (delta.Kind == AgentDeltaKind.Error)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Error.WriteLine($"\n[Error] {delta.Text}");
                            Console.ResetColor();
                        }
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().GetAwaiter().GetResult();
                }

                long tEnd = sw.ElapsedTicks;
                double freq = System.Diagnostics.Stopwatch.Frequency;

                int promptTokens = decoder.PromptTokensCount;
                double dtPrefillMs = (tFirstToken > 0 ? (tFirstToken - tStart) : 0) * 1000.0 / freq;
                double dtGenMs = (tFirstToken > 0 ? (tEnd - tFirstToken) : 0) * 1000.0 / freq;
                double dtTotalMs = (tEnd - tStart) * 1000.0 / freq;

                double prefillTokSec = dtPrefillMs > 0 ? (promptTokens / (dtPrefillMs / 1000.0)) : 0;
                double genTokSec = dtGenMs > 0 ? (genTokensCount / (dtGenMs / 1000.0)) : 0;
                double totalTokSec = dtTotalMs > 0 ? ((promptTokens + genTokensCount) / (dtTotalMs / 1000.0)) : 0;

                Console.WriteLine();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("[DPX Telemetry]");
                Console.WriteLine($"  Prefill (Prompt): {promptTokens} tokens | {dtPrefillMs:F0} ms | {prefillTokSec:F1} tok/s");
                Console.WriteLine($"  Decode (Gen):     {genTokensCount} tokens | {dtGenMs:F0} ms | {genTokSec:F1} tok/s");
                Console.WriteLine($"  End-to-End:       {promptTokens + genTokensCount} tokens | {dtTotalMs:F0} ms | {totalTokSec:F1} tok/s");
                Console.ResetColor();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[Cancelled]");
            }
        }
    }
}
