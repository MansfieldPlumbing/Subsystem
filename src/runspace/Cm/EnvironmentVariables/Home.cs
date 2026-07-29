using System;
using System.IO;

namespace Subsystem.Cm.EnvironmentVariables;

// Home — product-agnostic path resolver for the application's root home directory.
// _androidFilesDir and _value are volatile lazy-init state: written once under _lock,
// then read-only. volatile satisfies SS015 (mutable static); the lock satisfies correctness.
public static class Home
{
    private const string CompiledDefault = "";
    private static volatile string? _androidFilesDir;
    private static volatile string? _value;
    private static readonly object _lock = new();

    public static void SetAndroidFilesDir(string path) => _androidFilesDir = path;

    public static string Value
    {
        get
        {
            if (_value != null) return _value;
            lock (_lock)
            {
                if (_value != null) return _value;
                _value = Resolve();
                return _value;
            }
        }
    }

    private static string Resolve()
    {
        if (!string.IsNullOrEmpty(_androidFilesDir))
            return Prepare(_androidFilesDir!);

        var env = Environment.GetEnvironmentVariable("SS_HOME");
        if (!string.IsNullOrEmpty(env))
            return Prepare(env!);

        if (!string.IsNullOrEmpty(CompiledDefault))
            return Prepare(CompiledDefault!);

        return Prepare(AppContext.BaseDirectory);
    }

    private static string Prepare(string root)
    {
        root = Path.GetFullPath(root);
        try { Directory.CreateDirectory(root); }
        catch (Exception ex) { Dg.Warn("home", $"Directory.CreateDirectory failed for home root '{root}': {ex.Message}"); }
        return root;
    }
}
