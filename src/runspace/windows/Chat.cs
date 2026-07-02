using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Subsystem;

namespace Subsystem.Windows
{
    internal static class Chat
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Error: Please provide a prompt. Usage: ss chat \"<prompt>\"");
                return 1;
            }

            string prompt = args[0];

            // Resolve the model by DISCOVERY, never a hardcoded path or name (doctrine: models are
            // projected/discovered from disk, never baked into a head). Order: SS_MODEL_PATH override →
            // the first *.litertlm under the models dir (SS_MODELS env, else <drive>\models — drive-
            // DERIVED, no hardcoded letter). The unit id is the model file's stem, also discovered.
            string modelPath = Environment.GetEnvironmentVariable("SS_MODEL_PATH") ?? "";
            if (string.IsNullOrEmpty(modelPath))
            {
                var driveRoot = Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
                var modelsDir = Environment.GetEnvironmentVariable("SS_MODELS") ?? Path.Combine(driveRoot, "models");
                if (Directory.Exists(modelsDir))
                    modelPath = Directory.EnumerateFiles(modelsDir, "*.litertlm")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
            }

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                Console.Error.WriteLine("Error: no .litertlm model discovered.");
                Console.Error.WriteLine("Set SS_MODEL_PATH to a .litertlm file, or drop one into the models dir (SS_MODELS, else <drive>\\models).");
                return 1;
            }
            string unitId = Path.GetFileNameWithoutExtension(modelPath);
            // The system prompt is a registry CONTRACT (\Capability\Prompt\broker), never a baked string.
            // The Windows head's Cm may be unseeded — degrade to none, never a literal fallback.
            string? systemInstruction = ResolvePrompt();

            // Verify DLL presence
            if (!CheckDlls(out string missingDetails))
            {
                Console.Error.WriteLine("Error: LiteRT-LM native binaries are missing.");
                Console.Error.WriteLine(missingDetails);
                Console.Error.WriteLine("Recon verdict required the following set of DLLs:");
                Console.Error.WriteLine("  1. litert-lm.dll (facade DLL containing litert_lm_* exports)");
                Console.Error.WriteLine("  2. dxil.dll");
                Console.Error.WriteLine("  3. dxcompiler.dll");
                Console.Error.WriteLine("  4. vendors\\intel_openvino\\dispatch\\LiteRtDispatch.dll");
                return 1;
            }

            Console.WriteLine($"[LiteRT-LM] Loading model {Path.GetFileName(modelPath)}...");
            
            try
            {
                using var client = new LiteRtRuntime(
                    modelPath,
                    unitId,
                    systemInstruction: systemInstruction,
                    cacheDir: null
                );

                var fault = client.BringUp();
                if (fault != null)
                {
                    Console.Error.WriteLine($"Error: BringUp failed. Class: {fault.Class}, Detail: {fault.NativeDetail}");
                    return 1;
                }

                Console.WriteLine($"[LiteRT-LM] Running prompt on CPU: \"{prompt}\"");
                Console.WriteLine();

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                var streamTask = StreamOutputAsync(client, prompt, cts.Token);
                streamTask.GetAwaiter().GetResult();
                
                Console.WriteLine();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Exception: {ex.Message}");
                return 1;
            }
        }

        // The system prompt is a registry CONTRACT (\Capability\Prompt\broker), not a baked string.
        // Resolve it from Cm if present; absent (unseeded Windows Cm) = none — never a literal.
        private static string? ResolvePrompt()
        {
            try
            {
                var rec = Subsystem.Cm.Cm.Get("\\Capability\\Prompt\\broker");
                if (rec?.ManifestJson == null) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(rec.ManifestJson);
                return doc.RootElement.TryGetProperty("systemInstruction", out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }

        private static async Task StreamOutputAsync(LiteRtRuntime client, string prompt, CancellationToken ct)
        {
            try
            {
                await foreach (var delta in client.StreamTurnAsync(prompt, null, ct))
                {
                    if (delta.Kind == AgentDeltaKind.Token && !string.IsNullOrEmpty(delta.Text))
                    {
                        Console.Write(delta.Text);
                    }
                    else if (delta.Kind == AgentDeltaKind.Think && !string.IsNullOrEmpty(delta.Text))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write(delta.Text);
                        Console.ResetColor();
                    }
                    else if (delta.Kind == AgentDeltaKind.Error)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine($"\n[Error] {delta.Text}");
                        Console.ResetColor();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[Cancelled]");
            }
        }

        private static bool CheckDlls(out string missingDetails)
        {
            // Simple check logic mirroring FindExtractionDir
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string driveRoot = Path.GetPathRoot(exeDir) ?? "";
            
            string[] searchDirs = new[]
            {
                // Single-file extraction dir
                GetSingleFileExtractDir(),
                // Exe directory
                exeDir,
                // Dev staging directory
                Path.Combine(driveRoot, "litert")
            };

            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                string mainDll = Path.Combine(dir, "litert-lm.dll");
                if (File.Exists(mainDll))
                {
                    // Found the main DLL, check companions
                    bool dxil = File.Exists(Path.Combine(dir, "dxil.dll"));
                    bool dxcompiler = File.Exists(Path.Combine(dir, "dxcompiler.dll"));
                    bool openvino = File.Exists(Path.Combine(dir, "vendors", "intel_openvino", "dispatch", "LiteRtDispatch.dll"));

                    if (dxil && dxcompiler && openvino)
                    {
                        missingDetails = "";
                        return true;
                    }
                    else
                    {
                        missingDetails = $"Found litert-lm.dll in '{dir}' but companion DLLs were missing:\n" +
                                         $"  dxil.dll: {(dxil ? "OK" : "MISSING")}\n" +
                                         $"  dxcompiler.dll: {(dxcompiler ? "OK" : "MISSING")}\n" +
                                         $"  LiteRtDispatch.dll: {(openvino ? "OK" : "MISSING")}";
                        return false;
                    }
                }
            }

            string stagedDir = Path.Combine(Path.GetPathRoot(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "", "litert");
            missingDetails = $"Could not find 'litert-lm.dll' in any of the search directories (extraction temp, executable folder, or {stagedDir}).";
            return false;
        }

        private static string GetSingleFileExtractDir()
        {
            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ss");
            string netTempParent = Path.Combine(Path.GetTempPath(), ".net", processName);
            if (Directory.Exists(netTempParent))
            {
                foreach (var subDir in Directory.GetDirectories(netTempParent))
                {
                    if (File.Exists(Path.Combine(subDir, "litert-lm.dll")))
                    {
                        return subDir;
                    }
                }
            }
            return "";
        }
    }
}
