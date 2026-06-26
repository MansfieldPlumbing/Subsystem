using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RIFE_TRT;

class Program
{
    // --- TRT DLL ---
    [DllImport("rife_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr Rife_Init(string enginePath, int height, int width);
    [DllImport("rife_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int Rife_Interpolate(IntPtr ctx, float[] img0, float[] img1, float timestep, float[] output);
    [DllImport("rife_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void Rife_Destroy(IntPtr ctx);

    // --- MF BRIDGE DLL ---
    const string MfDll = "mf_bridge.dll";
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern int MF_Init();
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern void MF_Deinit();
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] 
    static extern IntPtr Reader_Open(string path, out int w, out int h, out int num, out int den);
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern int Reader_Read(IntPtr ctx, IntPtr buffer);
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern void Reader_Close(IntPtr ctx);
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] 
    static extern IntPtr Writer_Open(string path, int w, int h, int num, int den, int bitrate);
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern int Writer_PushFrame(IntPtr ctx, IntPtr buffer, int size);
    [DllImport(MfDll, CallingConvention = CallingConvention.Cdecl)] static extern void Writer_Close(IntPtr ctx);

    static int Main(string[] args)
    {
        ConfigureEnvironment();
        var cfg = ParseArgs(args);
        if (cfg == null) { PrintUsage(); return 1; }

        if (!File.Exists(cfg.InputPath)) { Console.WriteLine($"[Error] Input not found: {cfg.InputPath}"); return 1; }
        if (!File.Exists(cfg.EnginePath)) { Console.WriteLine($"[Error] Engine not found: {cfg.EnginePath}"); return 1; }

        if (MF_Init() != 0) { Console.WriteLine("[Error] Failed to init Media Foundation."); return 1; }

        try {
            return RunPipeline(cfg);
        }
        finally {
            MF_Deinit();
        }
    }

    static int RunPipeline(Config cfg)
    {
        Console.WriteLine($"Opening: {Path.GetFileName(cfg.InputPath)}");
        IntPtr reader = Reader_Open(cfg.InputPath, out int W, out int H, out int fpsNum, out int fpsDen);
        if (reader == IntPtr.Zero) { Console.WriteLine("Failed to open video reader."); return 1; }
        
        double srcFps = (double)fpsNum / fpsDen;
        Console.WriteLine($"  Source: {W}x{H} @ {srcFps:F2} fps");

        Console.Write("  Init TRT... ");
        IntPtr trt = Rife_Init(cfg.EnginePath, H, W);
        if (trt == IntPtr.Zero) { Console.WriteLine("Failed."); return 1; }
        Console.WriteLine("OK");

        int targetFpsNum = fpsNum * cfg.Multiplier;
        int targetFpsDen = fpsDen;
        int bitrate = (int)(W * H * srcFps * cfg.Multiplier * 0.15); 
        
        string tempOut = Path.Combine(Path.GetDirectoryName(cfg.OutputPath) ?? ".", "temp_video.mp4");
        IntPtr writer = Writer_Open(tempOut, W, H, targetFpsNum, targetFpsDen, bitrate);
        if (writer == IntPtr.Zero) { Console.WriteLine("Failed to open video writer."); return 1; }

        int byteSize = W * H * 4;
        IntPtr bufRaw = Marshal.AllocHGlobal(byteSize);
        float[] img0 = new float[3 * H * W];
        float[] img1 = new float[3 * H * W];
        float[] output = new float[3 * H * W];

        Console.WriteLine($"  Interpolating {cfg.Multiplier}x...");
        var sw = Stopwatch.StartNew();
        int frameCount = 0;

        if (Reader_Read(reader, bufRaw) != 0) { Console.WriteLine("Empty video."); return 1; }
        BytesToFloats(bufRaw, img0, W, H); 
        
        Writer_PushFrame(writer, bufRaw, byteSize);
        frameCount++;

        while (true)
        {
            int res = Reader_Read(reader, bufRaw);
            if (res == 2) break; 
            if (res != 0) continue; 

            BytesToFloats(bufRaw, img1, W, H);

            ProcessInterval(trt, writer, img0, img1, output, bufRaw, byteSize, W, H, cfg.Depth);

            FloatsToBytes(img1, bufRaw, W, H);
            Writer_PushFrame(writer, bufRaw, byteSize);
            frameCount++;

            Array.Copy(img1, img0, img0.Length);
            Console.Write($"\r  Frames: {frameCount}");
        }

        sw.Stop();
        Console.WriteLine($"\n  Done. {frameCount} frames in {sw.Elapsed.TotalSeconds:F2}s ({frameCount / sw.Elapsed.TotalSeconds:F1} fps)");

        Reader_Close(reader);
        Writer_Close(writer);
        Rife_Destroy(trt);
        Marshal.FreeHGlobal(bufRaw);

        Console.Write("  Muxing audio... ");
        RunProcessLive("ffmpeg", $"-i \"{tempOut}\" -i \"{cfg.InputPath}\" -map 0:v -map 1:a? -c:v copy -c:a copy \"{cfg.OutputPath}\" -y -v error");
        File.Delete(tempOut);
        Console.WriteLine("Done.");
        
        return 0;
    }

    static void ProcessInterval(IntPtr trt, IntPtr writer, float[] A, float[] B, float[] Out, IntPtr bufRaw, int bufSize, int W, int H, int depth)
    {
        if (depth == 0) return;
        Rife_Interpolate(trt, A, B, 0.5f, Out);
        float[] mid = (float[])Out.Clone();
        ProcessInterval(trt, writer, A, mid, Out, bufRaw, bufSize, W, H, depth - 1);
        FloatsToBytes(mid, bufRaw, W, H);
        Writer_PushFrame(writer, bufRaw, bufSize);
        ProcessInterval(trt, writer, mid, B, Out, bufRaw, bufSize, W, H, depth - 1);
    }

    static unsafe void BytesToFloats(IntPtr pRaw, float[] floats, int W, int H)
    {
        byte* raw = (byte*)pRaw;
        int count = W * H;
        Parallel.For(0, count, i => {
            float r = raw[i * 4 + 2] * (1.0f / 255.0f);
            float g = raw[i * 4 + 1] * (1.0f / 255.0f);
            float b = raw[i * 4 + 0] * (1.0f / 255.0f);
            floats[i] = r;
            floats[count + i] = g;
            floats[count * 2 + i] = b;
        });
    }

    static unsafe void FloatsToBytes(float[] floats, IntPtr pRaw, int W, int H)
    {
        byte* raw = (byte*)pRaw;
        int count = W * H;
        Parallel.For(0, count, i => {
            float r = floats[i];
            float g = floats[count + i];
            float b = floats[count * 2 + i];
            raw[i * 4 + 2] = (byte)(r >= 1.0f ? 255 : (r <= 0.0f ? 0 : r * 255.0f));
            raw[i * 4 + 1] = (byte)(g >= 1.0f ? 255 : (g <= 0.0f ? 0 : g * 255.0f));
            raw[i * 4 + 0] = (byte)(b >= 1.0f ? 255 : (b <= 0.0f ? 0 : b * 255.0f));
            raw[i * 4 + 3] = 255;
        });
    }

    static void RunProcessLive(string exe, string args) {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!; p.WaitForExit();
    }
    
    static void ConfigureEnvironment() {
        string baseDir = AppContext.BaseDirectory;
        Environment.SetEnvironmentVariable("PATH", baseDir + ";" + Environment.GetEnvironmentVariable("PATH"));
    }

    class Config { public string InputPath = "", OutputPath = "", EnginePath = ""; public int Multiplier = 2, Depth = 1; }
    
    static Config? ParseArgs(string[] args) {
        if (args.Length < 1) return null;
        var cfg = new Config { InputPath = Path.GetFullPath(args[0]) };
        string ext = Path.GetExtension(cfg.InputPath);
        string noExt = Path.GetFileNameWithoutExtension(cfg.InputPath);
        string dir = Path.GetDirectoryName(cfg.InputPath) ?? ".";
        
        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "-e": if (i+1 < args.Length) cfg.EnginePath = args[++i]; break;
                case "-o": if (i+1 < args.Length) cfg.OutputPath = args[++i]; break;
                case "-2x": cfg.Multiplier = 2; cfg.Depth = 1; break;
                case "-4x": cfg.Multiplier = 4; cfg.Depth = 2; break;
                case "-8x": cfg.Multiplier = 8; cfg.Depth = 3; break;
            }
        }
        if (string.IsNullOrEmpty(cfg.OutputPath)) cfg.OutputPath = Path.Combine(dir, $"{noExt}_{cfg.Multiplier}x{ext}");
        
        if (string.IsNullOrEmpty(cfg.EnginePath)) {
            var found = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "models"), "*.engine");
            if (found.Length > 0) cfg.EnginePath = found[0];
        }
        return cfg;
    }

    static void PrintUsage() { Console.WriteLine("RIFE TRT (In-Memory Pipeline)\nUsage: RIFE_TRT.exe input.mp4 [-4x] [-o out.mp4]"); }
}
