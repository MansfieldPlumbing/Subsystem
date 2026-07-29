using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Subsystem.Cm.EnvironmentVariables;
using Subsystem.Host;

namespace Subsystem.Remedy;

// The request hive — management layer for Remedy Change Requests and Incidents.
// Uses Sql.cs tool in Subsystem.Host for all database operations.
public static class RequestHive
{
    private static string[] Kinds      => new[] { "Incident", "Change" };
    private static string[] Statuses   => new[] { "Open", "Closed" };
    private static string[] Categories => new[] { "Vom", "Cm", "Dg", "Pp", "Rb", "Rs", "Pwsh", "Device", "gate", "build", "shell", "agent" };

    private static readonly char Sep = (char)0x1f;

    public static string HivePath => Db.Requests;

    static RequestHive()
    {
        EnsureSchema();
    }

    private static void EnsureSchema()
    {
        Sql.Initialize(
            HivePath,
            "CREATE TABLE IF NOT EXISTS Requests(" +
            " id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER, category INTEGER, summary TEXT," +
            " related_files TEXT, severity INTEGER, status INTEGER, created INTEGER, closed INTEGER," +
            " disposition TEXT);" +
            "CREATE TABLE IF NOT EXISTS EosLog(" +
            " id INTEGER PRIMARY KEY AUTOINCREMENT, request INTEGER, noted INTEGER, disposition TEXT, body TEXT);"
        );

        if (IsDatabaseEmpty())
        {
            SeedFromEmbedded();
        }
    }

    private static bool IsDatabaseEmpty()
    {
        try
        {
            long count = Convert.ToInt64(Sql.ExecuteScalar(HivePath, "SELECT COUNT(*) FROM Requests"));
            return count == 0;
        }
        catch
        {
            return true;
        }
    }

    private static string? ReadEmbeddedRequests()
    {
        var asm = typeof(RequestHive).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("requests.json", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void SeedFromEmbedded()
    {
        try
        {
            var json = ReadEmbeddedRequests();
            if (string.IsNullOrWhiteSpace(json)) return;

            var records = System.Text.Json.JsonSerializer.Deserialize<List<RequestRecord>>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (records == null || records.Count == 0) return;

            Sql.RunInTransaction(HivePath, transaction =>
            {
                foreach (var r in records)
                {
                    Sql.ExecuteNonQuery(
                        HivePath,
                        "INSERT OR IGNORE INTO Requests(id, kind, category, summary, related_files, severity, status, created, closed, disposition) " +
                        "VALUES($id, $k, $c, $s, $f, $sev, $st, $cr, $cl, $d)",
                        new Dictionary<string, object?>
                        {
                            ["$id"] = r.Id,
                            ["$k"] = Code(Kinds, r.Kind, "Type"),
                            ["$c"] = Code(Categories, r.Category, "Category"),
                            ["$s"] = r.Summary ?? "",
                            ["$f"] = string.Join(Sep, r.RelatedFiles ?? Array.Empty<string>()),
                            ["$sev"] = r.Severity,
                            ["$st"] = Code(Statuses, r.Status, "Status"),
                            ["$cr"] = r.Created,
                            ["$cl"] = r.Closed,
                            ["$d"] = r.Disposition ?? ""
                        },
                        transaction
                    );

                    if (r.EosLog != null)
                    {
                        foreach (var entry in r.EosLog)
                        {
                            Sql.ExecuteNonQuery(
                                HivePath,
                                "INSERT INTO EosLog(request, noted, disposition, body) VALUES($r, $n, $d, $b)",
                                new Dictionary<string, object?>
                                {
                                    ["$r"] = r.Id,
                                    ["$n"] = entry.Noted,
                                    ["$d"] = entry.Disposition ?? "",
                                    ["$b"] = entry.Body ?? ""
                                },
                                transaction
                            );
                        }
                    }
                }
            });
            Dg.Log("request", $"seeded {records.Count} requests from embedded backlog");
        }
        catch (Exception ex)
        {
            Dg.Log("request", $"failed to seed from embedded requests: {ex.Message}");
        }
    }

    private static int Code(string[] dict, string value, string field)
    {
        int i = Array.FindIndex(dict, d => d.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new ArgumentException($"{field} '{value}' is not in the closed set: {string.Join(", ", dict)}");
        return i;
    }

    private static string Label(string[] dict, long code) => code >= 0 && code < dict.Length ? dict[code] : "?";

    public static RequestRecord Create(string kind, string category, string summary, string[]? relatedFiles, int severity)
    {
        int kindCode = Code(Kinds, kind, "Type");
        int catCode  = Code(Categories, category, "Category");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = relatedFiles ?? Array.Empty<string>();

        long id;
        using (var conn = new SqliteConnection($"Data Source={HivePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO Requests(kind,category,summary,related_files,severity,status,created,closed,disposition)" +
                " VALUES($k,$c,$s,$f,$sev,0,$cr,0,''); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$k", kindCode);
            cmd.Parameters.AddWithValue("$c", catCode);
            cmd.Parameters.AddWithValue("$s", summary ?? "");
            cmd.Parameters.AddWithValue("$f", string.Join(Sep, files));
            cmd.Parameters.AddWithValue("$sev", severity);
            cmd.Parameters.AddWithValue("$cr", now);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }

        Dg.Log("request", $"OPEN #{id} {kind}/{category} sev{severity}: {summary}");
        return new RequestRecord
        {
            Id = id, Kind = Kinds[kindCode], Category = Categories[catCode], Summary = summary ?? "",
            RelatedFiles = files, Severity = severity, Status = Statuses[0], Created = now,
        };
    }

    public static List<RequestRecord> Query(string? kind, string? category, string? status, long? id,
                                           string? search, long since, long before)
    {
        var list = new List<RequestRecord>();
        var where = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (id.HasValue)                     { where.Add("id=$id");          parameters["$id"] = id.Value; }
        if (!string.IsNullOrEmpty(kind))     { where.Add("kind=$k");         parameters["$k"] = Code(Kinds, kind!, "Type"); }
        if (!string.IsNullOrEmpty(category)) { where.Add("category=$c");     parameters["$c"] = Code(Categories, category!, "Category"); }
        if (!string.IsNullOrEmpty(status))   { where.Add("status=$st");      parameters["$st"] = Code(Statuses, status!, "Status"); }
        if (!string.IsNullOrEmpty(search))   { where.Add("(summary LIKE $q OR disposition LIKE $q)"); parameters["$q"] = "%" + search + "%"; }
        if (since  > 0)                      { where.Add("created>=$since"); parameters["$since"] = since; }
        if (before > 0)                      { where.Add("created<$before"); parameters["$before"] = before; }

        var sql = "SELECT id,kind,category,summary,related_files,severity,status,created,closed,disposition FROM Requests"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC";

        Sql.ExecuteReader(HivePath, sql, parameters, reader =>
        {
            list.Add(new RequestRecord
            {
                Id           = reader.GetInt64(0),
                Kind         = Label(Kinds, reader.GetInt64(1)),
                Category     = Label(Categories, reader.GetInt64(2)),
                Summary      = reader.IsDBNull(3) ? "" : reader.GetString(3),
                RelatedFiles = reader.IsDBNull(4) || reader.GetString(4).Length == 0
                                 ? Array.Empty<string>()
                                 : reader.GetString(4).Split(Sep, StringSplitOptions.RemoveEmptyEntries),
                Severity     = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Status       = Label(Statuses, reader.GetInt64(6)),
                Created      = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                Closed       = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                Disposition  = reader.IsDBNull(9) ? "" : reader.GetString(9),
            });
        });

        foreach (var t in list)
        {
            t.EosLog = ReadEosLog(t.Id);
        }

        return list;
    }

    private static EosLogEntry[] ReadEosLog(long requestId)
    {
        var entries = new List<EosLogEntry>();
        Sql.ExecuteReader(
            HivePath,
            "SELECT noted,disposition,body FROM EosLog WHERE request=$id ORDER BY id ASC",
            new Dictionary<string, object?> { ["$id"] = requestId },
            reader =>
            {
                entries.Add(new EosLogEntry
                {
                    Request     = requestId,
                    Noted       = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    Disposition = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Body        = reader.IsDBNull(2) ? "" : reader.GetString(2),
                });
            }
        );
        return entries.ToArray();
    }

    public static EosLogEntry? WriteEosLog(long requestId, string disposition, string body)
    {
        if (string.IsNullOrWhiteSpace(disposition)) throw new ArgumentException("an EOS-log disposition is required.");
        if (string.IsNullOrWhiteSpace(body))        throw new ArgumentException("an EOS-log body is required.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var check = Sql.ExecuteScalar(
            HivePath,
            "SELECT 1 FROM Requests WHERE id=$id",
            new Dictionary<string, object?> { ["$id"] = requestId }
        );
        if (check == null) return null;

        Sql.ExecuteNonQuery(
            HivePath,
            "INSERT INTO EosLog(request,noted,disposition,body) VALUES($r,$n,$d,$b)",
            new Dictionary<string, object?>
            {
                ["$r"] = requestId,
                ["$n"] = now,
                ["$d"] = disposition,
                ["$b"] = body
            }
        );

        Dg.Log("request", $"EOSLOG #{requestId} [{disposition}]: {body}");
        return new EosLogEntry { Request = requestId, Noted = now, Disposition = disposition, Body = body };
    }

    public static RequestRecord? Close(long id, string disposition)
    {
        if (string.IsNullOrWhiteSpace(disposition))
            throw new ArgumentException("a disposition is required to close a request.");

        int rows = Sql.ExecuteNonQuery(
            HivePath,
            "UPDATE Requests SET status=1,closed=$cl,disposition=$d WHERE id=$id",
            new Dictionary<string, object?>
            {
                ["$cl"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["$d"] = disposition,
                ["$id"] = id
            }
        );
        if (rows == 0) return null;

        Dg.Log("request", $"CLOSE #{id}: {disposition}");
        return Query(null, null, null, id, null, 0, 0).FirstOrDefault();
    }

    public static string GetRef(string kind, long id)
        => (kind switch { "Incident" => "INC", "Change" => "CRQ", "Problem" => "PBI", _ => "SR" }) + id.ToString("D12");
}
