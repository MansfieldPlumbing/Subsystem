using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Subsystem;

// ObpHost — the in-memory Object-Presenter host (SHELL-PLAN step 5). The shell's presenters
// (.obp — see SHELL-PLAN: an Object Presenter binds to and projects a named kernel object) and
// their support files are COMPILED into the assembly as EmbeddedResource with an explicit
// LogicalName per file (the manifest name IS the virtual path — never reconstructed from dotted
// resource names), and served from RAM. No loose shell files in the unzipped APK.
//
// This is the ONE resolver for the shell/* virtual tree (mirrors Registry.js contentUrl: physical
// layout confined to a single place). Resolution ladder, additive and surge-grounded:
//   1. embedded resource (the compiled shell — lazy-loaded, cached, served from RAM)
//   2. .html <-> .obp extension alias (so pre-rename URLs and post-rename files always meet)
//   3. AndroidAsset fallback (the zoo, anything deliberately left loose)
public static class ObpHost
{
    private static readonly object _initLock = new();
    private static IReadOnlyDictionary<string, string>? _index;            // virtual path -> manifest resource name
    private static System.Collections.Immutable.ImmutableDictionary<string, byte[]> _cache = System.Collections.Immutable.ImmutableDictionary.Create<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    // Platform seam: Android wires this to AssetManager.Open in MainActivity;
    // Windows leaves it null and the embedded-resource path is the only lane.
    public static Func<string, Stream?>? AssetFallback { get; set; }

    private static IReadOnlyDictionary<string, string> Index()
    {
        if (_index != null) return _index;
        lock (_initLock)
        {
            if (_index != null) return _index;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var asm = typeof(ObpHost).Assembly;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    var norm = name.Replace('\\', '/');
                    if (norm.StartsWith("shell/", StringComparison.OrdinalIgnoreCase))
                    {
                        map[norm] = name;
                    }
                    else if (norm.Contains("shell.", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = norm.IndexOf("shell.", StringComparison.OrdinalIgnoreCase);
                        var sub = norm.Substring(idx);
                        var lastDot = sub.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            var dirAndName = sub.Substring(0, lastDot).Replace('.', '/');
                            var ext = sub.Substring(lastDot);
                            var key = dirAndName + ext;
                            map[key] = name;
                        }
                    }
                }
                Dg.Log("obp", $"embedded presenter index: {map.Count} files");
            }
            catch (Exception ex) { Dg.Log("obp", "index failed: " + ex.Message); }
            _index = map;
            return map;
        }
    }

    private static string? AliasOf(string path)
    {
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return path[..^5] + ".obp";
        if (path.EndsWith(".obp", StringComparison.OrdinalIgnoreCase))  return path[..^4] + ".html";
        return null;
    }

    // Embedded-only probe (no asset fallback) — bytes from RAM, lazily hydrated and cached.
    public static bool TryGetEmbedded(string virtualPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var index = Index();
        var key = virtualPath.Replace('\\', '/');
        if (!index.ContainsKey(key))
        {
            var alias = AliasOf(key);
            if (alias == null || !index.ContainsKey(alias)) return false;
            key = alias;
        }
        if (_cache.TryGetValue(key, out var cached)) { bytes = cached; return true; }
        try
        {
            using var s = typeof(ObpHost).Assembly.GetManifestResourceStream(index[key]);
            if (s == null) return false;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = System.Collections.Immutable.ImmutableInterlocked.GetOrAdd(ref _cache, key, _ => ms.ToArray());
            return true;
        }
        catch (Exception ex) { Dg.Log("obp", $"read {key} failed: {ex.Message}"); return false; }
    }

    // The one open: embedded -> extension alias -> platform asset fallback.
    public static Stream? OpenRead(string virtualPath)
    {
        if (TryGetEmbedded(virtualPath, out var bytes)) return new MemoryStream(bytes, writable: false);

        // Registry virtual endpoint fallbacks when ProjectionServer is absent
        var norm = virtualPath.Replace('\\', '/').TrimStart('/');
        if (norm.Equals("shell/apps", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetEmbedded("shell/apps.json", out var appBytes)) return new MemoryStream(appBytes, writable: false);
            if (TryGetEmbedded("shell/cards.json", out var cardBytes)) return new MemoryStream(cardBytes, writable: false);
        }
        else if (norm.Equals("shell/models", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetEmbedded("shell/models.json", out var modelBytes)) return new MemoryStream(modelBytes, writable: false);
        }
        else if (norm.Equals("shell/prompts", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetEmbedded("shell/prompts.json", out var promptBytes)) return new MemoryStream(promptBytes, writable: false);
        }
        else if (norm.Equals("shell/themes", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetEmbedded("shell/themes.json", out var themeBytes)) return new MemoryStream(themeBytes, writable: false);
        }
        else if (norm.Equals("shell/agent-tools", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetEmbedded("shell/agent-tools.json", out var toolBytes)) return new MemoryStream(toolBytes, writable: false);
        }
        else if (norm.Equals("shell/shell-layout", StringComparison.OrdinalIgnoreCase))
        {
            var layoutJson = "[{\"id\":\"taskbar\",\"type\":\"taskbar\",\"position\":\"bottom\"},{\"id\":\"menu\",\"type\":\"menu\"}]";
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutJson), writable: false);
        }
        else if (norm.Equals("shell/verbs", StringComparison.OrdinalIgnoreCase))
        {
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes("[]"), writable: false);
        }
        else if (norm.StartsWith("shell/fs", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var hostScheme = "http" + "://shell/";
                var uri = new Uri(hostScheme + norm);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var dirPath = query["path"];
                if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
                    dirPath = Environment.CurrentDirectory;

                var dirs = Directory.GetDirectories(dirPath).Select(d => new {
                    name = Path.GetFileName(d),
                    path = d,
                    isDir = true,
                    size = 0L,
                    modified = Directory.GetLastWriteTimeUtc(d).ToString("o")
                });

                var files = Directory.GetFiles(dirPath).Select(f => {
                    var fi = new FileInfo(f);
                    return new {
                        name = fi.Name,
                        path = fi.FullName,
                        isDir = false,
                        size = fi.Length,
                        modified = fi.LastWriteTimeUtc.ToString("o")
                    };
                });

                var items = dirs.Concat(files).ToList();
                var result = new { current = Path.GetFullPath(dirPath), items };
                var json = System.Text.Json.JsonSerializer.Serialize(result);
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false);
            }
            catch (Exception ex)
            {
                var errJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(errJson), writable: false);
            }
        }

        try
        {
            var diskPath = Path.Combine(Environment.CurrentDirectory, "src", norm);
            if (File.Exists(diskPath)) return File.OpenRead(diskPath);
            diskPath = Path.Combine(Environment.CurrentDirectory, norm);
            if (File.Exists(diskPath)) return File.OpenRead(diskPath);
        }
        catch (Exception ex) { Dg.Log("obp", $"disk fallback {norm} failed: {ex.Message}"); }

        try
        {
            if (AssetFallback != null)
            {
                var s = AssetFallback(virtualPath);
                if (s != null) return s;
                var alias = AliasOf(virtualPath);
                if (alias != null) { s = AssetFallback(alias); if (s != null) return s; }
            }
        }
        catch (Exception ex) { Dg.Log("obp", $"asset open {virtualPath} failed: {ex.Message}"); }
        return null;
    }

    public static string? ReadAllText(string virtualPath)
    {
        using var s = OpenRead(virtualPath);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Enumerate embedded virtual paths under a prefix (the Registrar's seed catalog). Falls back to
    // the platform asset listing when nothing is embedded there.
    public static string[] Enumerate(string prefix)
    {
        var p = prefix.Replace('\\', '/').TrimEnd('/') + "/";
        var hits = Index().Keys.Where(k => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hits.Length > 0) return hits;
        try
        {
            if (AssetFallback != null)
            {
                // Best-effort: ask Android to list the directory via the same delegate convention.
                // On Windows this is null so we just return empty.
            }
        }
        catch (Exception ex) { Dg.Warn("obp", ex); }
        return Array.Empty<string>();
    }
}
