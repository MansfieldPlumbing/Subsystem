using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Subsystem.Cm;

// One request — an Incident (defect) or Change (enhancement/RFC) row, named after Remedy ITSM's record
// types (we borrow Remedy's vocabulary for recognizability, never where it spites NT). Stored DENSE in the
// registry's durable plane: kind/category/status are integer codes dictionary-encoded against the closed
// vocabularies below, timestamps are unix-epoch seconds, and related files ride a single 0x1f-joined column
// (the unit-separator Cm uses for DependsOn) — no nested JSON at rest. The flat JSON shape is a PROJECTION
// rendered on read; the EOS log (the append-only end-of-session history) loads alongside.
public sealed class RequestRecord
{
    public long          Id           { get; set; }
    public string        Kind         { get; set; } = "";   // Incident | Change
    public string        Category     { get; set; } = "";
    public string        Summary      { get; set; } = "";
    public string[]      RelatedFiles { get; set; } = Array.Empty<string>();
    public int           Severity     { get; set; }
    public string        Status       { get; set; } = "";   // Open | Closed
    public long          Created      { get; set; }         // unix epoch seconds
    public long          Closed       { get; set; }         // 0 == still open
    public string        Disposition  { get; set; } = "";   // how it was closed — required to close (the terminal disposition)
    public EosLogEntry[] EosLog       { get; set; } = Array.Empty<EosLogEntry>();   // append-only end-of-session history

    // Remedy reference — a PROJECTION over the dense integer PK (Remedy-{Type}Request-id), never stored.
    public string        Ref          => RequestHive.GetRef(Kind, Id);
}

// One EOS-log entry — an append-only End-Of-Session record: what a session did to this request and the
// disposition it left. The durable shift-note that makes the raw chat disposable; named for the mechanism (a
// log of end-of-session reports), not metaphor. Stored DENSE in a sibling table (noted unix-epoch); entries
// are WRITTEN, never edited or deleted.
public sealed class EosLogEntry
{
    public long   Request     { get; set; }
    public long   Noted       { get; set; }   // unix epoch seconds
    public string Disposition { get; set; } = "";   // how the session left it
    public string Body        { get; set; } = "";   // the end-of-session report
}

// The request hive — the durable request plane, a projection of the ONE registry (Cm's subsystem-registry.db),
// NOT a second store ("Hive" is Cm-canonical for a durable plane; the requests ARE one). Two-state call-center
// lifecycle: a request is OPENED (Create) and CLOSED with a mandatory DISPOSITION (how it was handled — fixed /
// won't-fix / duplicate / by-design / …), and accrues an append-only EOS log between. Synchronous by Cm's law.
// Verbs are the approved NT vocabulary (Create/Query/Write/Close); the cmdlets are thin host bindings.
public static class RequestHive
{
    // Closed vocabularies, dictionary-encoded: the stored integer IS the index. Append only, never reorder
    // (a reorder rewrites the meaning of every stored row). Expression-bodied so there is no static state.
    private static string[] Kinds      => new[] { "Incident", "Change" };   // Remedy ITSM record types: Incident = defect, Change = enhancement/RFC
    private static string[] Statuses   => new[] { "Open", "Closed" };
    private static string[] Categories => new[] { "Vom", "Cm", "Dg", "Pp", "Rb", "Rs", "Pwsh", "Device", "gate", "build", "shell", "agent" };

    private static readonly char Sep = (char)0x1f;   // unit-separator: joins RelatedFiles (mirrors Cm.DependsOn)

    // The request plane is stored in subsystem-requests.db at the DRIVE ROOT — one hive per drive,
    // cwd-independent, so every ss binary on the drive reads the SAME hive regardless of where it was
    // launched (invariant 1; fixes the per-cwd fork, CRQ125-C). AppData branch when opted in or on Android.
    public static string HivePath
    {
        get
        {
            bool useAppData = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SS_USE_APPDATA")) ||
                              RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));
            if (useAppData)
            {
                return Path.Combine(Path.GetDirectoryName(Cm.DbPath)!, "subsystem-requests.db");
            }
            return Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "subsystem-requests.db");
        }
    }

    private static SqliteConnection Open()
    {
        var dbPath = HivePath;
        bool exists = File.Exists(dbPath);
        var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS Requests(" +
                " id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER, category INTEGER, summary TEXT," +
                " related_files TEXT, severity INTEGER, status INTEGER, created INTEGER, closed INTEGER," +
                " disposition TEXT);" +
                // The EOS log — append-only end-of-session entries, one row each, request -> Requests.id.
                "CREATE TABLE IF NOT EXISTS EosLog(" +
                " id INTEGER PRIMARY KEY AUTOINCREMENT, request INTEGER, noted INTEGER, disposition TEXT, body TEXT);";
            cmd.ExecuteNonQuery();
        }

        if (!exists || IsDatabaseEmpty(c))
        {
            SeedFromEmbedded(c);
        }

        return c;
    }

    private static bool IsDatabaseEmpty(SqliteConnection c)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Requests";
            long count = Convert.ToInt64(cmd.ExecuteScalar());
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

    private static void SeedFromEmbedded(SqliteConnection c)
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

            using var transaction = c.BeginTransaction();
            foreach (var r in records)
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = 
                        "INSERT OR IGNORE INTO Requests(id, kind, category, summary, related_files, severity, status, created, closed, disposition) " +
                        "VALUES($id, $k, $c, $s, $f, $sev, $st, $cr, $cl, $d)";
                    cmd.Parameters.AddWithValue("$id", r.Id);
                    cmd.Parameters.AddWithValue("$k", Code(Kinds, r.Kind, "Type"));
                    cmd.Parameters.AddWithValue("$c", Code(Categories, r.Category, "Category"));
                    cmd.Parameters.AddWithValue("$s", r.Summary ?? "");
                    cmd.Parameters.AddWithValue("$f", string.Join(Sep, r.RelatedFiles ?? Array.Empty<string>()));
                    cmd.Parameters.AddWithValue("$sev", r.Severity);
                    cmd.Parameters.AddWithValue("$st", Code(Statuses, r.Status, "Status"));
                    cmd.Parameters.AddWithValue("$cr", r.Created);
                    cmd.Parameters.AddWithValue("$cl", r.Closed);
                    cmd.Parameters.AddWithValue("$d", r.Disposition ?? "");
                    cmd.ExecuteNonQuery();
                }

                if (r.EosLog != null)
                {
                    foreach (var entry in r.EosLog)
                    {
                        using (var cmd = c.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "INSERT INTO EosLog(request, noted, disposition, body) VALUES($r, $n, $d, $b)";
                            cmd.Parameters.AddWithValue("$r", r.Id);
                            cmd.Parameters.AddWithValue("$n", entry.Noted);
                            cmd.Parameters.AddWithValue("$d", entry.Disposition ?? "");
                            cmd.Parameters.AddWithValue("$b", entry.Body ?? "");
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            transaction.Commit();
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

    // Create (Open) a request. Returns the stored row, projected with labels.
    public static RequestRecord Create(string kind, string category, string summary, string[]? relatedFiles, int severity)
    {
        int kindCode = Code(Kinds, kind, "Type");
        int catCode  = Code(Categories, category, "Category");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = relatedFiles ?? Array.Empty<string>();

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO Requests(kind,category,summary,related_files,severity,status,created,closed,disposition)" +
            " VALUES($k,$c,$s,$f,$sev,0,$cr,0,''); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$k", kindCode);
        cmd.Parameters.AddWithValue("$c", catCode);
        cmd.Parameters.AddWithValue("$s", summary ?? "");
        cmd.Parameters.AddWithValue("$f", string.Join(Sep, files));
        cmd.Parameters.AddWithValue("$sev", severity);
        cmd.Parameters.AddWithValue("$cr", now);
        long id = Convert.ToInt64(cmd.ExecuteScalar());
        Dg.Log("request", $"OPEN #{id} {kind}/{category} sev{severity}: {summary}");
        return new RequestRecord
        {
            Id = id, Kind = Kinds[kindCode], Category = Categories[catCode], Summary = summary ?? "",
            RelatedFiles = files, Severity = severity, Status = Statuses[0], Created = now,
        };
    }

    // Query (Enumerate) requests, newest first. null/empty filters = any. search = case-insensitive substring
    // over summary + disposition. since/before bound the created date (unix epoch; 0 = unbounded). Each row's
    // EOS log loads alongside (oldest-first).
    public static List<RequestRecord> Query(string? kind, string? category, string? status, long? id,
                                           string? search, long since, long before)
    {
        using var c = Open();
        var list = new List<RequestRecord>();

        using (var cmd = c.CreateCommand())
        {
            var where = new List<string>();
            if (id.HasValue)                     { where.Add("id=$id");          cmd.Parameters.AddWithValue("$id", id.Value); }
            if (!string.IsNullOrEmpty(kind))     { where.Add("kind=$k");         cmd.Parameters.AddWithValue("$k", Code(Kinds, kind!, "Type")); }
            if (!string.IsNullOrEmpty(category)) { where.Add("category=$c");     cmd.Parameters.AddWithValue("$c", Code(Categories, category!, "Category")); }
            if (!string.IsNullOrEmpty(status))   { where.Add("status=$st");      cmd.Parameters.AddWithValue("$st", Code(Statuses, status!, "Status")); }
            if (!string.IsNullOrEmpty(search))   { where.Add("(summary LIKE $q OR disposition LIKE $q)"); cmd.Parameters.AddWithValue("$q", "%" + search + "%"); }
            if (since  > 0)                      { where.Add("created>=$since"); cmd.Parameters.AddWithValue("$since", since); }
            if (before > 0)                      { where.Add("created<$before"); cmd.Parameters.AddWithValue("$before", before); }
            cmd.CommandText =
                "SELECT id,kind,category,summary,related_files,severity,status,created,closed,disposition FROM Requests"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC";

            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new RequestRecord
                {
                    Id           = r.GetInt64(0),
                    Kind         = Label(Kinds, r.GetInt64(1)),
                    Category     = Label(Categories, r.GetInt64(2)),
                    Summary      = r.IsDBNull(3) ? "" : r.GetString(3),
                    RelatedFiles = r.IsDBNull(4) || r.GetString(4).Length == 0
                                     ? Array.Empty<string>()
                                     : r.GetString(4).Split(Sep, StringSplitOptions.RemoveEmptyEntries),
                    Severity     = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    Status       = Label(Statuses, r.GetInt64(6)),
                    Created      = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    Closed       = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                    Disposition  = r.IsDBNull(9) ? "" : r.GetString(9),
                });
        }

        // Load each row's EOS log — oldest-first (append order). Same connection, after the request reader has
        // closed (Sqlite has no MARS).
        foreach (var t in list)
            t.EosLog = ReadEosLog(c, t.Id);

        return list;
    }

    // Read a request's EOS-log entries, oldest-first (append order).
    private static EosLogEntry[] ReadEosLog(SqliteConnection c, long requestId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT noted,disposition,body FROM EosLog WHERE request=$id ORDER BY id ASC";
        cmd.Parameters.AddWithValue("$id", requestId);
        var entries = new List<EosLogEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            entries.Add(new EosLogEntry
            {
                Request     = requestId,
                Noted       = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                Disposition = r.IsDBNull(1) ? "" : r.GetString(1),
                Body        = r.IsDBNull(2) ? "" : r.GetString(2),
            });
        return entries.ToArray();
    }

    // Write (append) an EOS-log entry — the end-of-session record. Append-only: no edit, no delete. A
    // disposition and a body are both required (an empty end-of-session note is noise). Returns the projected
    // entry, or null when no such request exists.
    public static EosLogEntry? WriteEosLog(long requestId, string disposition, string body)
    {
        if (string.IsNullOrWhiteSpace(disposition)) throw new ArgumentException("an EOS-log disposition is required.");
        if (string.IsNullOrWhiteSpace(body))        throw new ArgumentException("an EOS-log body is required.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var c = Open();
        using (var chk = c.CreateCommand())
        {
            chk.CommandText = "SELECT 1 FROM Requests WHERE id=$id";
            chk.Parameters.AddWithValue("$id", requestId);
            if (chk.ExecuteScalar() == null) return null;
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO EosLog(request,noted,disposition,body) VALUES($r,$n,$d,$b)";
            cmd.Parameters.AddWithValue("$r", requestId);
            cmd.Parameters.AddWithValue("$n", now);
            cmd.Parameters.AddWithValue("$d", disposition);
            cmd.Parameters.AddWithValue("$b", body);
            cmd.ExecuteNonQuery();
        }
        Dg.Log("request", $"EOSLOG #{requestId} [{disposition}]: {body}");
        return new EosLogEntry { Request = requestId, Noted = now, Disposition = disposition, Body = body };
    }

    // Close a request with a MANDATORY disposition — a request cannot be closed without saying how it was
    // handled. Returns the updated row, or null when no such id exists.
    public static RequestRecord? Close(long id, string disposition)
    {
        if (string.IsNullOrWhiteSpace(disposition))
            throw new ArgumentException("a disposition is required to close a request.");
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE Requests SET status=1,closed=$cl,disposition=$d WHERE id=$id";
            cmd.Parameters.AddWithValue("$cl", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$d", disposition);
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) return null;
        }
        Dg.Log("request", $"CLOSE #{id}: {disposition}");
        return Query(null, null, null, id, null, 0, 0).FirstOrDefault();
    }

    // Remedy reference projection: the ITSM record-type code + the dense id zero-padded to 12 digits
    // (Incident -> INC, Change -> CRQ, Problem -> PBI; e.g. CRQ000000000097). A projection only — the hive
    // stores the dense integer PK; the padded SR code is the human-facing label, never stored. Unknown kind -> SR.
    public static string GetRef(string kind, long id)
        => (kind switch { "Incident" => "INC", "Change" => "CRQ", "Problem" => "PBI", _ => "SR" }) + id.ToString("D12");

}
