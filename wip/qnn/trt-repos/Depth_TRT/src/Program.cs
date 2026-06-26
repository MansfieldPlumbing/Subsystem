using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Depth_TRT;

class Program
{
    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr Depth_Init(string enginePath);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int Depth_Infer(IntPtr ctx, float[] rgbChw518, float[] depthOut);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void Depth_Normalize(float[] depth, int count, int invert);

    [DllImport("depth_trt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void Depth_Destroy(IntPtr ctx);

    const int SIZE = 518;
    const int PLANE = SIZE * SIZE;

    static int Main(string[] args)
    {
        ConfigureEnvironment();
        var cfg = ParseArgs(args);
        if (cfg == null) { PrintUsage(); return 1; }

        if (!File.Exists(cfg.InputPath)) { Console.WriteLine($"[Error] Input not found: {cfg.InputPath}"); return 1; }
        if (!File.Exists(cfg.EnginePath)) { Console.WriteLine($"[Error] Engine not found: {cfg.EnginePath}"); return 1; }

        Console.WriteLine("[App] Loading TensorRT Engine...");
        IntPtr ctx = Depth_Init(cfg.EnginePath);
        if (ctx == IntPtr.Zero)
        {
            Console.WriteLine("[Error] Failed to initialize TensorRT.");
            return 1;
        }

        try
        {
            Console.WriteLine($"[App] Processing {Path.GetFileName(cfg.InputPath)}...");
            var sw = Stopwatch.StartNew();

            // 1. Load and Resize image
            using Bitmap origBmp = new Bitmap(cfg.InputPath);
            using Bitmap resizeBmp = new Bitmap(origBmp, new Size(SIZE, SIZE));

            // 2. Extract to CHW [0,1] floats
            float[] inputCHW = new float[3 * PLANE];
            BitmapData data = resizeBmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        int idx = y * SIZE + x;
                        // Format24bppRgb is actually BGR in memory
                        byte b = ptr[y * stride + x * 3 + 0];
                        byte g = ptr[y * stride + x * 3 + 1];
                        byte r = ptr[y * stride + x * 3 + 2];
                        
                        inputCHW[idx]             = r / 255f;
                        inputCHW[PLANE + idx]     = g / 255f;
                        inputCHW[PLANE * 2 + idx] = b / 255f;
                    }
                }
            }
            resizeBmp.UnlockBits(data);

            // 3. Inference
            float[] depthOut = new float[PLANE];
            int res = Depth_Infer(ctx, inputCHW, depthOut);
            if (res != 0)
            {
                Console.WriteLine($"[Error] Inference failed with code {res}");
                return 1;
            }

            // 4. Normalize depth
            Depth_Normalize(depthOut, PLANE, cfg.Invert);

            // 5. Reconstruct Grayscale Bitmap
            using Bitmap depthBmp = new Bitmap(SIZE, SIZE, PixelFormat.Format24bppRgb);
            BitmapData outData = depthBmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                byte* ptr = (byte*)outData.Scan0;
                int stride = outData.Stride;
                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        byte val = (byte)Math.Clamp(depthOut[y * SIZE + x] * 255f, 0, 255);
                        ptr[y * stride + x * 3 + 0] = val;
                        ptr[y * stride + x * 3 + 1] = val;
                        ptr[y * stride + x * 3 + 2] = val;
                    }
                }
            }
            depthBmp.UnlockBits(outData);

            // 6. Scale back to original resolution and save
            using Bitmap finalOut = new Bitmap(depthBmp, origBmp.Width, origBmp.Height);
            finalOut.Save(cfg.OutputPath, ImageFormat.Png);
            
            sw.Stop();
            Console.WriteLine($"[App] Saved to {cfg.OutputPath} in {sw.ElapsedMilliseconds}ms");
            return 0;
        }
        finally
        {
            Depth_Destroy(ctx);
        }
    }

    static void ConfigureEnvironment() {
        string baseDir = AppContext.BaseDirectory;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            path = baseDir + Path.PathSeparator + path;

        string ini = Path.Combine(baseDir, "config.ini");
        if (File.Exists(ini)) {
            try {
                bool inRuntime = false;
                foreach (var line in File.ReadAllLines(ini)) {
                    string l = line.Trim();
                    if (l.StartsWith(';') || l.Length == 0) continue;
                    if (l.StartsWith('[') && l.EndsWith(']')) {
                        inRuntime = l.Equals("[runtime]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inRuntime) continue;
                    int eq = l.IndexOf('=');
                    if (eq < 1) continue;
                    string key = l[..eq].Trim();
                    string val = l[(eq + 1)..].Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        (key.Equals("TRT_BIN", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("CUDA_BIN", StringComparison.OrdinalIgnoreCase)) &&
                        Directory.Exists(val) &&
                        !path.Contains(val, StringComparison.OrdinalIgnoreCase))
                    {
                        path += Path.PathSeparator + val;
                    }
                }
            } catch { }
        }
        Environment.SetEnvironmentVariable("PATH", path);
    }

    class Config { 
        public string InputPath = "", OutputPath = "", EnginePath = ""; 
        public int Invert = 1;
    }
    
    static Config? ParseArgs(string[] args) {
        if (args.Length < 1) return null;
        var cfg = new Config { InputPath = Path.GetFullPath(args[0]) };
        string ext = Path.GetExtension(cfg.InputPath);
        string noExt = Path.GetFileNameWithoutExtension(cfg.InputPath);
        string dir = Path.GetDirectoryName(cfg.InputPath) ?? ".";
        
        for (int i = 1; i < args.Length; i++) {
            switch (args[i].ToLower()) {
                case "-e": if (i+1 < args.Length) cfg.EnginePath = args[++i]; break;
                case "-o": if (i+1 < args.Length) cfg.OutputPath = args[++i]; break;
                case "--no-invert": cfg.Invert = 0; break;
            }
        }
        if (string.IsNullOrEmpty(cfg.OutputPath)) cfg.OutputPath = Path.Combine(dir, $"{noExt}_depth.png");
        
        if (string.IsNullOrEmpty(cfg.EnginePath)) {
            var found = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "models"), "*.engine");
            if (found.Length > 0) cfg.EnginePath = found[0];
        }
        return cfg;
    }

    static void PrintUsage() { 
        Console.WriteLine("Depth TRT (In-Memory Pipeline)");
        Console.WriteLine("Usage: Depth_TRT.exe input.jpg [-o output.png] [--no-invert]");
    }
}
