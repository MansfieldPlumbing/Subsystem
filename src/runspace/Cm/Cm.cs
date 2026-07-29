using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Subsystem.Cm.EnvironmentVariables;

namespace Subsystem.Cm;

// Cm — the Configuration Manager (NT's registry subsystem; the COM/CLSID analog). Two planes
// (VOM-SPEC §6/§7): an in-memory VOLATILE namespace (the live, resolvable records — the fast read path)
// + a durable SQLITE plane (WAL, atomic upsert) that rehydrates on boot. This is what makes real-time
// cmdlets persist (lock-in/promotion, the north-star loop) and is the SCM database for the Sc services
// layer + the rocker-toggle settings. Lazy-inits on first use; db lives in the app's private files dir.
public static class Cm
{
    private static readonly ConcurrentDictionary<string, CapabilityRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();
    private static bool _initialized;
    private static string _dbPath = "";
    private static readonly char Sep = (char)0x1f;   // unit-separator: joins DependsOn (illegal in paths)

    public static string DbPath { get { Ensure(); return _dbPath; } }

    private static void Ensure()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            try { SQLitePCL.Batteries_V2.Init(); } catch (Exception ex) { Dg.Warn("cm", ex); /* newer bundles auto-init */ }
            // All path resolution lives in Cm.EnvironmentVariables.DbConfig.
            _dbPath = Db.Config;
            using (var c = Open())
            {
                Exec(c,
                    "PRAGMA journal_mode=WAL;" +
                    "CREATE TABLE IF NOT EXISTS Capabilities(" +
                    " path TEXT PRIMARY KEY, name TEXT, type TEXT, source TEXT, manifest_json TEXT," +
                    " owner TEXT, integrity TEXT, start_type TEXT, enabled INTEGER, depends_on TEXT," +
                    " created TEXT, modified TEXT, hash TEXT);" +
                    "CREATE TABLE IF NOT EXISTS CapabilityRefs(" +
                    " from_path TEXT, to_path TEXT, PRIMARY KEY(from_path,to_path));");

                if (IsDatabaseEmpty(c))
                {
                    SeedFromEmbedded(c);
                }

                Rehydrate(c);
            }
            _initialized = true;
            Dg.Log("cm", $"registry init: {_records.Count} capabilities rehydrated from {_dbPath}");
        }
    }

    private static bool IsDatabaseEmpty(SqliteConnection c)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Capabilities";
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            return count == 0;
        }
        catch
        {
            return true;
        }
    }

    private static void SeedFromEmbedded(SqliteConnection c)
    {
        try
        {
            var asm = typeof(Cm).Assembly;
            var names = asm.GetManifestResourceNames()
                .Where(n => n.StartsWith("Registry/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (names.Count == 0) return;

            var records = new List<CapabilityRecord>();
            var slugToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: Parse and insert
            foreach (var name in names)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();

                var parts = name.Split('/');
                if (parts.Length < 3) continue;
                var slug = Path.GetFileNameWithoutExtension(parts.Last()).Trim();
                var pathType = parts[parts.Length - 2].Trim(); // e.g. Reference, Feedback

                ParseMemoryFile(content, slug, pathType, out var rec);
                if (rec != null)
                {
                    records.Add(rec);
                    slugToPath[slug] = rec.Path;
                }
            }

            // Pass 2: Extract links, resolve dependencies, and save to SQLite
            using var transaction = c.BeginTransaction();
            foreach (var rec in records)
            {
                var links = new List<string>();
                if (!string.IsNullOrEmpty(rec.Source))
                {
                    var matches = Regex.Matches(rec.Source, @"\[\[([^\]]+)\]\]");
                    foreach (Match m in matches)
                    {
                        var targetSlug = m.Groups[1].Value.Trim();
                        if (slugToPath.TryGetValue(targetSlug, out var targetPath))
                        {
                            links.Add(targetPath);
                            
                            using var refCmd = c.CreateCommand();
                            refCmd.Transaction = transaction;
                            refCmd.CommandText = "INSERT OR IGNORE INTO CapabilityRefs(from_path, to_path) VALUES($from, $to)";
                            refCmd.Parameters.AddWithValue("$from", rec.Path);
                            refCmd.Parameters.AddWithValue("$to", targetPath);
                            refCmd.ExecuteNonQuery();
                        }
                    }
                }
                rec.DependsOn = links.ToArray();

                using var cmd = c.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "INSERT INTO Capabilities(path,name,type,source,manifest_json,owner,integrity,start_type,enabled,depends_on,created,modified,hash)" +
                    " VALUES($p,$n,$t,$s,$m,$o,$i,$st,$e,$d,$cr,$mo,$h)" +
                    " ON CONFLICT(path) DO UPDATE SET name=$n,type=$t,source=$s,manifest_json=$m,owner=$o,integrity=$i," +
                    " start_type=$st,enabled=$e,depends_on=$d,modified=$mo,hash=$h;";
                cmd.Parameters.AddWithValue("$p", rec.Path);
                cmd.Parameters.AddWithValue("$n", rec.Name);
                cmd.Parameters.AddWithValue("$t", rec.Type);
                cmd.Parameters.AddWithValue("$s", rec.Source ?? "");
                cmd.Parameters.AddWithValue("$m", rec.ManifestJson ?? "");
                cmd.Parameters.AddWithValue("$o", rec.Owner);
                cmd.Parameters.AddWithValue("$i", rec.Integrity);
                cmd.Parameters.AddWithValue("$st", rec.StartType);
                cmd.Parameters.AddWithValue("$e", rec.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$d", string.Join(Sep.ToString(), rec.DependsOn));
                var now = DateTime.UtcNow.ToString("o");
                cmd.Parameters.AddWithValue("$cr", now);
                cmd.Parameters.AddWithValue("$mo", now);
                cmd.Parameters.AddWithValue("$h", rec.Hash ?? "");
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            Dg.Log("cm", $"seeded {records.Count} capabilities from embedded registry");
        }
        catch (Exception ex)
        {
            Dg.Log("cm", $"failed to seed from embedded registry: {ex.Message}");
        }
    }

    private static void ParseMemoryFile(string content, string slug, string pathType, out CapabilityRecord? rec)
    {
        rec = null;
        try
        {
            var fmStart = content.IndexOf("---");
            if (fmStart < 0) return;
            var fmEnd = content.IndexOf("---", fmStart + 3);
            if (fmEnd < 0) return;

            var fmText = content.Substring(fmStart + 3, fmEnd - (fmStart + 3)).Trim();
            var body = content.Substring(fmEnd + 3).Trim();

            var lines = fmText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var name = slug;
            var type = pathType;
            var description = "";
            var originSessionId = "";

            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                var val = line.Substring(idx + 1).Trim().Trim('"', '\'');
                if (key == "name") name = val;
                else if (key == "type") type = val;
                else if (key == "description") description = val;
                else if (key == "originsessionid") originSessionId = val;
            }

            if (type.Length > 0)
                type = char.ToUpper(type[0]) + type.Substring(1).ToLowerInvariant();

            rec = new CapabilityRecord
            {
                Path = $"\\Registry\\{type}\\{slug}",
                Name = name,
                Type = type,
                Source = body,
                Owner = "\\System\\Memory",
                Integrity = "User",
                StartType = "manual",
                Enabled = true
            };

            var manifest = new Dictionary<string, string>();
            manifest["summary"] = description;
            manifest["description"] = description;
            if (!string.IsNullOrEmpty(originSessionId))
                manifest["originSessionId"] = originSessionId;

            rec.ManifestJson = JsonSerializer.Serialize(manifest);
        }
        catch (Exception ex)
        {
            Dg.Warn("cm", ex);
            rec = null;
        }
    }

    private static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string name, object? val)
        => cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);

    private static void Rehydrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT path,name,type,source,manifest_json,owner,integrity,start_type,enabled,depends_on,created,modified,hash FROM Capabilities";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rec = new CapabilityRecord
            {
                Path        = r.GetString(0),
                Name        = r.IsDBNull(1) ? "" : r.GetString(1),
                Type        = r.IsDBNull(2) ? "Capability" : r.GetString(2),
                Source      = r.IsDBNull(3) ? null : r.GetString(3),
                ManifestJson= r.IsDBNull(4) ? null : r.GetString(4),
                Owner       = r.IsDBNull(5) ? "\\System" : r.GetString(5),
                Integrity   = r.IsDBNull(6) ? "User" : r.GetString(6),
                StartType   = r.IsDBNull(7) ? "manual" : r.GetString(7),
                Enabled     = !r.IsDBNull(8) && r.GetInt32(8) != 0,
                DependsOn   = r.IsDBNull(9) || r.GetString(9).Length == 0
                                ? Array.Empty<string>()
                                : r.GetString(9).Split(Sep, StringSplitOptions.RemoveEmptyEntries),
                Created     = r.IsDBNull(10) ? "" : r.GetString(10),
                Modified    = r.IsDBNull(11) ? "" : r.GetString(11),
                Hash        = r.IsDBNull(12) ? "" : r.GetString(12),
            };
            _records[rec.Path] = rec;
        }
    }

    // Register (upsert) a capability into both planes. Atomic on the durable side via ON CONFLICT.
    public static CapabilityRecord Register(CapabilityRecord rec)
    {
        Ensure();
        var now = DateTime.UtcNow.ToString("o");
        rec.Created  = string.IsNullOrEmpty(rec.Created)
            ? (_records.TryGetValue(rec.Path, out var ex) ? ex.Created : now)
            : rec.Created;
        rec.Modified = now;
        _records[rec.Path] = rec;

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO Capabilities(path,name,type,source,manifest_json,owner,integrity,start_type,enabled,depends_on,created,modified,hash)" +
            " VALUES($p,$n,$t,$s,$m,$o,$i,$st,$e,$d,$cr,$mo,$h)" +
            " ON CONFLICT(path) DO UPDATE SET name=$n,type=$t,source=$s,manifest_json=$m,owner=$o,integrity=$i," +
            " start_type=$st,enabled=$e,depends_on=$d,modified=$mo,hash=$h;";
        Bind(cmd, "$p", rec.Path);  Bind(cmd, "$n", rec.Name);  Bind(cmd, "$t", rec.Type);
        Bind(cmd, "$s", rec.Source); Bind(cmd, "$m", rec.ManifestJson); Bind(cmd, "$o", rec.Owner);
        Bind(cmd, "$i", rec.Integrity); Bind(cmd, "$st", rec.StartType); Bind(cmd, "$e", rec.Enabled ? 1 : 0);
        Bind(cmd, "$d", string.Join(Sep, rec.DependsOn)); Bind(cmd, "$cr", rec.Created);
        Bind(cmd, "$mo", rec.Modified); Bind(cmd, "$h", rec.Hash);
        cmd.ExecuteNonQuery();
        Dg.Log("cm", $"REGISTER {rec.Path} ({rec.Type}/{rec.Integrity}, start={rec.StartType}, enabled={rec.Enabled})");
        return rec;
    }

    public static CapabilityRecord? Get(string path)  { Ensure(); return _records.TryGetValue(path, out var r) ? r : null; }
    public static CapabilityRecord[] List()           { Ensure(); return _records.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray(); }

    public static bool Unregister(string path)
    {
        Ensure();
        bool had = _records.TryRemove(path, out _);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Capabilities WHERE path=$p; DELETE FROM CapabilityRefs WHERE from_path=$p OR to_path=$p;";
        Bind(cmd, "$p", path);
        cmd.ExecuteNonQuery();
        Dg.Log("cm", $"UNREGISTER {path} (existed={had})");
        return had;
    }

    public static CapabilityRecord? Set(string path, bool? enabled, string? startType)
    {
        Ensure();
        if (!_records.TryGetValue(path, out var r)) return null;
        if (enabled.HasValue) r.Enabled = enabled.Value;
        if (!string.IsNullOrEmpty(startType)) r.StartType = startType!;
        return Register(r);   // re-persist
    }

    // Self-test (like Test-Vom): register a probe capability, read it back from BOTH planes, then clean up.
    // The full rehydrate-on-boot proof is: register a capability -> relaunch the app -> Get-Capability.
    public static object SelfTest()
    {
        Ensure();
        string p = $"\\Capability\\__cmtest_{DateTime.Now:HHmmss}";
        Register(new CapabilityRecord
        {
            Path = p, Name = "CmTest", Type = "Probe", Integrity = "System",
            StartType = "manual", Enabled = true, DependsOn = new[] { "\\Capability\\Projection" }
        });
        var back = Get(p);

        int durable;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Capabilities WHERE path=$p";
            Bind(cmd, "$p", p);
            durable = Convert.ToInt32(cmd.ExecuteScalar());
        }
        Unregister(p);

        return new
        {
            ok        = back != null && durable == 1 && Get(p) == null,
            dbPath    = _dbPath,
            inMemory  = back != null,
            inDurable = durable == 1,
            total     = _records.Count,
            note      = "registered a probe capability, confirmed in-memory + SQLite (WAL), then unregistered",
        };
    }
}
