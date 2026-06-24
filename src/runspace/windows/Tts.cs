using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Subsystem.Windows;

// `ss tts` — speak with the kokoro voice on the home-rolled, ORT-free dp-onnx engine (CRQ121).
//
// Rung 1 of the speech ladder (invariant 9, "one concept / N implementations"): drive the dp-onnx LEAF —
// an emancipated .NET sibling with a clean CLI/file contract — feed it IPA phonemes, play the WAV. Text->IPA
// G2P is rung 2 (an ONNX grapheme->phoneme model on the SAME dp-onnx engine — no foreign C lib, no GPL), so
// rung 1 takes phonemes directly. Rung 3 absorbs the engine intraprocess (the Borg; shared with the Gemma
// LLM rung). ADDITIVE: the WebView ORT-web kokoro lane and the LiteRT-LM agent runtime are untouched.
//
// Engine + model paths follow the Build.cs convention (env override -> drive-derived discovery), never a
// hardcoded machine path (SS021). dp-onnx owns the kokoro vocab/voices internally.
internal static class Tts
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?") return Help();

        string? phonemes = null, inputs = null, outPath = null;
        string voice = "af_heart";
        float speed = 1.0f;
        bool play = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--phonemes" or "-p": if (i + 1 < args.Length) phonemes = args[++i]; break;
                case "--inputs": if (i + 1 < args.Length) inputs = args[++i]; break;
                case "--voice" or "-v": if (i + 1 < args.Length) voice = args[++i]; break;
                case "--speed": if (i + 1 < args.Length) float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out speed); break;
                case "--out" or "-o": if (i + 1 < args.Length) { outPath = args[++i]; play = false; } break;
                case "--no-play": play = false; break;
                case "--play": play = true; break;
                default:
                    // a bare leading token (no flag) is the phonemes — `ss tts "h@l'oU"`
                    if (phonemes == null && inputs == null && !args[i].StartsWith('-')) phonemes = args[i];
                    break;
            }
        }

        var engine = ResolveEngine();
        if (engine == null)
        {
            Console.Error.WriteLine("ss tts: dp-onnx engine not found. Set SS_TTS_ENGINE to dp-onnx.exe, or build it at");
            Console.Error.WriteLine("        <drive>\\qnn-project\\workspace\\onnx-interp (the ORT-free kokoro/gemma engine).");
            return 2;
        }
        var model = ResolveModel();
        if (model == null)
        {
            Console.Error.WriteLine("ss tts: kokoro model.onnx not found. Set SS_TTS_MODEL, or place it at");
            Console.Error.WriteLine("        <drive>\\reference\\Kokoro-82M\\onnx\\model.onnx (the dynamic model — not a static fold).");
            return 2;
        }

        if (phonemes == null && inputs == null)
        {
            Console.Error.WriteLine("ss tts: nothing to speak.");
            Console.Error.WriteLine("        rung 1 takes IPA:  ss tts --phonemes \"<IPA>\"  [--voice af_heart] [--speed 1.0]");
            Console.Error.WriteLine("        text-in (G2P) is rung 2 (an ONNX grapheme->phoneme model on dp-onnx); not wired yet.");
            return 2;
        }

        var wav = outPath ?? Path.Combine(Path.GetTempPath(), $"ss-tts-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wav))!); }
        catch (Exception ex) { Subsystem.Dg.Log("tts", "could not create output dir: " + ex.Message); }

        string engineArgs = phonemes != null
            ? $"stream \"{model}\" --phonemes \"{phonemes}\" --voice {voice} --speed {speed.ToString(CultureInfo.InvariantCulture)} --out \"{wav}\""
            : $"run \"{model}\" --inputs \"{inputs}\" --out \"{wav}\"";

        Console.WriteLine($"ss tts: engine={Path.GetFileName(engine)}  model={Path.GetFileName(model)}  voice={voice}  (dp-onnx, no ORT)");
        int rc = RunEngine(engine, engineArgs);
        if (rc != 0 || !File.Exists(wav))
        {
            Console.Error.WriteLine($"ss tts: RED — engine exit {rc}" + (File.Exists(wav) ? "" : ", no wav produced") + ".");
            return 1;
        }

        var fi = new FileInfo(wav);
        Console.WriteLine($"ss tts: wrote {wav} ({fi.Length / 1024:n0} KB)");
        if (play)
        {
            try { PlayWav(wav); Console.WriteLine("ss tts: played."); }
            catch (Exception ex) { Console.Error.WriteLine($"ss tts: playback failed ({ex.Message}) — the wav is written above."); }
        }
        return 0;
    }

    // ---- engine / model discovery (env override -> drive-derived; SS021-clean, mirrors Build.ResolveDotnet) ----

    private static string? ResolveEngine()
    {
        var env = Environment.GetEnvironmentVariable("SS_TTS_ENGINE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? Directory.GetCurrentDirectory()) ?? "";
        string[] rels =
        {
            @"qnn-project\workspace\onnx-interp\bin\Release\net11.0\win-x64\native\dp-onnx.exe",
            @"qnn-project\workspace\onnx-interp\publish-aot\dp-onnx.exe",
            @"qnn-project\workspace\onnx-interp\publish\dp-onnx.exe",
            @"bin\dp-onnx.exe",
        };
        foreach (var rel in rels) { var p = Path.Combine(drive, rel); if (File.Exists(p)) return p; }
        return null;
    }

    private static string? ResolveModel()
    {
        var env = Environment.GetEnvironmentVariable("SS_TTS_MODEL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? Directory.GetCurrentDirectory()) ?? "";
        string[] rels =
        {
            @"reference\Kokoro-82M\onnx\model.onnx",
            @"models\kokoro-v1.0.onnx",
        };
        foreach (var rel in rels) { var p = Path.Combine(drive, rel); if (File.Exists(p)) return p; }
        return null;
    }

    // Drive the leaf, streaming its stdout (the realtime/NaN/seam receipt) and stderr to the console.
    private static int RunEngine(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is { } s) Console.WriteLine("  " + s); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } s) Console.Error.WriteLine("  " + s); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    // Dependency-free WAV playback via the Windows multimedia API (no SoundPlayer / desktop pack needed).
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_SYNC = 0x0000, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;

    private static void PlayWav(string path)
    {
        if (!PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_SYNC | SND_NODEFAULT))
            throw new InvalidOperationException("winmm PlaySound returned false (no audio device?)");
    }

    private static int Help()
    {
        Console.WriteLine(
@"ss tts — speak with the kokoro voice on the dp-onnx engine (ORT-free, CRQ121).

  ss tts --phonemes ""<IPA>""        synthesize IPA phonemes and play (rung 1)
  ss tts --inputs <dir>             synthesize from pre-tokenized input_ids.bin/style.bin/speed.bin
  ss tts --phonemes ""<IPA>"" --out a.wav    write the WAV, do not play

OPTIONS
  --phonemes, -p <IPA>   the phonemes to speak (kokoro's 178-symbol IPA set)
  --voice, -v <name>     voice pack (default af_heart)
  --speed <f>            speech rate (default 1.0)
  --out, -o <file.wav>   write the WAV here instead of playing
  --no-play              synthesize to a temp WAV, report it, do not play

Text-in (plain English -> IPA G2P) is rung 2: an ONNX grapheme->phoneme model on the SAME dp-onnx
engine — no espeak, no foreign C lib. Engine/model paths: SS_TTS_ENGINE / SS_TTS_MODEL, else drive-derived.");
        return 0;
    }
}
