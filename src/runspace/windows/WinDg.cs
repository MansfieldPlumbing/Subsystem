using System.Runtime.InteropServices;
using System.Text.Json;

namespace Subsystem;

// Dg (Windows head) — the same one logging surface the portable core writes to, sunk to the console
// and %LocalAppData%\Subsystem\log instead of the device. The Android Dg.cs is NOT linked into this
// project (it carries Context, Hb, ModelCatalog, VomInterop — device truth); this file is the seam
// that satisfies the core's Dg references with Windows plumbing. Keep the public surface identical
// to what linked layers call — grow it only when the compiler demands.
public enum DgLevel { Trace, Debug, Info, Warn, Error, Fatal }

public readonly record struct DgRecord(DateTime Time, DgLevel Level, string Source, string Message);

public static class Dg
{
    private const int  RingCapacity = 500;
    private const long MaxFileBytes = 1_000_000;

    private static readonly object _gate = new();
    private static readonly Queue<DgRecord> _ring = new(RingCapacity + 1);
    private static readonly DateTime _start = DateTime.Now;
    private static readonly string? _dir;

    static Dg()
    {
        try
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Subsystem", "log");
            Directory.CreateDirectory(_dir);
        }
        catch { _dir = null; }   // console-only sink; never let logging take the head down
    }

    public static void Log(string source, string message)   => Write(DgLevel.Info,  source, message);

    public static void Trace(string source, string message) => Write(DgLevel.Trace, source, message);
    public static void Debug(string source, string message) => Write(DgLevel.Debug, source, message);
    public static void Info (string source, string message) => Write(DgLevel.Info,  source, message);
    public static void Warn (string source, string message) => Write(DgLevel.Warn,  source, message);
    public static void Error(string source, string message) => Write(DgLevel.Error, source, message);

    public static void Warn (string source, Exception ex)   => Write(DgLevel.Warn,  source, Describe(ex));
    public static void Error(string source, Exception ex)   => Write(DgLevel.Error, source, Describe(ex));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    public static void Write(DgLevel level, string source, string message)
    {
        var rec = new DgRecord(DateTime.Now, level, source, message);
        var line = $"{rec.Time:HH:mm:ss} [{rec.Level}] [{rec.Source}] {rec.Message}";
        lock (_gate)
        {
            _ring.Enqueue(rec);
            while (_ring.Count > RingCapacity) _ring.Dequeue();
            Console.WriteLine(line);
            Append(rec);
        }
    }

    // Caller holds _gate.
    private static void Append(DgRecord r)
    {
        if (_dir == null) return;
        try
        {
            var path = Path.Combine(_dir, "events.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxFileBytes)
                File.Move(path, Path.Combine(_dir, "events.1.log"), overwrite: true);
            File.AppendAllText(path, $"{r.Time:yyyy-MM-dd HH:mm:ss} [{r.Level}] [{r.Source}] {r.Message}\n");
        }
        catch { }
    }

    public static void RecordCrash(Exception? ex, string source)
        => Write(DgLevel.Fatal, "crash", $"{source}: {ex?.GetType().Name}: {ex?.Message}");

    public static string Snapshot()
    {
        var o = new Dictionary<string, object?>();
        try
        {
            o["time"]      = DateTime.Now.ToString("o");
            o["head"]      = "windows";
            o["uptimeSec"] = (int)(DateTime.Now - _start).TotalSeconds;
            o["runtime"]   = RuntimeInformation.FrameworkDescription;
            o["vom"]       = global::Subsystem.Vom.Vom.Snapshot();
            o["recentEvents"] = Recent(15);
        }
        catch (Exception ex) { o["snapshotError"] = ex.Message; }
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string[] Recent(int n)
    {
        lock (_gate)
        {
            var all = _ring.ToArray();
            int take = Math.Min(n, all.Length);
            var res = new string[take];
            for (int i = 0; i < take; i++)
            {
                var r = all[all.Length - take + i];
                res[i] = $"{r.Time:HH:mm:ss} [{r.Level}] [{r.Source}] {r.Message}";
            }
            return res;
        }
    }
}
