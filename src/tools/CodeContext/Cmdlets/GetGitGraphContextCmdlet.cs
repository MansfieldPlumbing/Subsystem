using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Management.Automation;
using System.Collections.Generic;

namespace Subsystem.Tools.CodeContext.Cmdlets;

// Get-GitGraphContext — git as OBJECTS, parsed straight from `.git/` in pure C#: NO git.exe, NO bash, no
// text-scraping. So it works for a naive agent (nothing on PATH) and it can't be lied to by porcelain.
// Projects a structured graph: the current Head/Branch, every local Branch, a recent Commit log (walked by
// decompressing loose objects), and the staged Index. The .git/index parse is the original; the rest is the
// 2026-06-16 expansion. (Endgame: libgit2 for packfile/delta reads + write verbs — this is the read graph.)
[Cmdlet(VerbsCommon.Get, "GitGraphContext")]
public class GetGitGraphContextCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = false)]
    public string GitRoot { get; set; } = Directory.GetCurrentDirectory();

    // How many commits to walk back from HEAD (first-parent). Loose objects only; packed history stops early.
    [Parameter(Mandatory = false)]
    public int CommitDepth { get; set; } = 20;

    protected override void ProcessRecord()
    {
        var gitDir = Path.Combine(GitRoot, ".git");
        if (!Directory.Exists(gitDir))
        {
            WriteWarning($"No .git folder at {GitRoot}. (Default scans the current dir; pass -GitRoot to point elsewhere.)");
            return;
        }

        var (branch, headSha) = ReadHead(gitDir);
        WriteObject(new
        {
            Root     = Path.GetFullPath(GitRoot),
            Branch   = branch,
            Head     = Short(headSha),
            Branches = ReadBranches(gitDir),
            Commits  = ReadLog(gitDir, headSha, CommitDepth),
            Index    = ReadIndex(gitDir),
            Status   = ReadStatus(gitDir),
        });
    }

    // --- HEAD: .git/HEAD is "ref: refs/heads/<branch>" (attached) or a raw 40-hex SHA (detached). ---
    private (string Branch, string? Sha) ReadHead(string gitDir)
    {
        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile)) return ("(none)", null);
        var content = File.ReadAllText(headFile).Trim();
        if (content.StartsWith("ref: ", StringComparison.Ordinal))
        {
            var refPath = content.Substring(5).Trim();
            var branch = refPath.StartsWith("refs/heads/", StringComparison.Ordinal) ? refPath.Substring(11) : refPath;
            return (branch, ResolveRef(gitDir, refPath));
        }
        return ("(detached)", content);
    }

    // A ref resolves to a loose file (.git/refs/...) or a line in .git/packed-refs.
    private static string? ResolveRef(string gitDir, string refPath)
    {
        var loose = Path.Combine(gitDir, refPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
        var packed = Path.Combine(gitDir, "packed-refs");
        if (File.Exists(packed))
            foreach (var line in File.ReadAllLines(packed))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                var sp = line.Split(' ', 2);
                if (sp.Length == 2 && sp[1].Trim() == refPath) return sp[0].Trim();
            }
        return null;
    }

    // --- Branches: every file under .git/refs/heads + any refs/heads/* in packed-refs. ---
    private static object[] ReadBranches(string gitDir)
    {
        var list = new List<object>();
        var headsDir = Path.Combine(gitDir, "refs", "heads");
        if (Directory.Exists(headsDir))
            foreach (var f in Directory.EnumerateFiles(headsDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(headsDir, f).Replace('\\', '/');
                try { list.Add(new { Name = name, Sha = Short(File.ReadAllText(f).Trim()) }); } catch { }
            }
        var packed = Path.Combine(gitDir, "packed-refs");
        if (File.Exists(packed))
            foreach (var line in File.ReadAllLines(packed))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                var sp = line.Split(' ', 2);
                if (sp.Length == 2 && sp[1].Trim().StartsWith("refs/heads/", StringComparison.Ordinal))
                    list.Add(new { Name = sp[1].Trim().Substring(11), Sha = Short(sp[0].Trim()) });
            }
        return list.ToArray();
    }

    // --- Commit log: walk first-parent from HEAD, decompressing each loose commit object. ---
    private object[] ReadLog(string gitDir, string? sha, int depth)
    {
        var list = new List<object>();
        var seen = new HashSet<string>();
        while (sha != null && sha.Length >= 40 && depth-- > 0 && seen.Add(sha))
        {
            var body = ReadLooseObject(gitDir, sha);
            if (body == null) break;   // packed or missing — loose-only read stops here
            var lines = body.Split('\n');
            string? parent = null, author = null; string subject = "";
            int i = 0;
            for (; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.Length == 0) { i++; break; }            // blank line → message follows
                if (parent == null && l.StartsWith("parent ", StringComparison.Ordinal)) parent = l.Substring(7).Trim();
                else if (l.StartsWith("author ", StringComparison.Ordinal)) author = l.Substring(7);
            }
            if (i < lines.Length) subject = lines[i];
            var (name, when) = ParseAuthor(author);
            list.Add(new { Sha = Short(sha), Subject = subject, Author = name, Date = when, Parent = Short(parent) });
            sha = parent;
        }
        return list.ToArray();
    }

    // A loose object is zlib(2-byte header + DEFLATE). Strip "commit <size>\0", return the rest.
    private static string? ReadLooseObject(string gitDir, string sha)
    {
        try
        {
            var p = Path.Combine(gitDir, "objects", sha.Substring(0, 2), sha.Substring(2));
            if (!File.Exists(p)) return null;
            var raw = File.ReadAllBytes(p);
            if (raw.Length < 3) return null;
            using var ms = new MemoryStream(raw, 2, raw.Length - 2);     // skip the 2-byte zlib header
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            def.CopyTo(outp);
            var text = Encoding.UTF8.GetString(outp.ToArray());
            var nul = text.IndexOf('\0');
            return nul >= 0 ? text.Substring(nul + 1) : text;
        }
        catch { return null; }
    }

    // "Name <email> <unixtime> <tz>" → (Name, ISO date).
    private static (string Name, string? Date) ParseAuthor(string? author)
    {
        if (string.IsNullOrEmpty(author)) return ("", null);
        var lt = author!.IndexOf('<');
        var name = lt > 0 ? author.Substring(0, lt).Trim() : author.Trim();
        var gt = author.IndexOf('>');
        string? date = null;
        if (gt > 0 && gt + 2 < author.Length)
        {
            var rest = author.Substring(gt + 1).Trim().Split(' ');
            if (rest.Length >= 1 && long.TryParse(rest[0], out var unix))
                date = DateTimeOffset.FromUnixTimeSeconds(unix).ToString("u");
        }
        return (name, date);
    }

    // --- Index: the original native parse of .git/index (DIRC) — the staged files. ---
    private object[] ReadIndex(string gitDir)
    {
        var indexPath = Path.Combine(gitDir, "index");
        if (!File.Exists(indexPath)) return Array.Empty<object>();
        var results = new List<object>();
        try
        {
            using var stream = File.OpenRead(indexPath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 12) return Array.Empty<object>();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "DIRC") return Array.Empty<object>();
            ReadUInt32BE(reader);                       // version
            uint entryCount = ReadUInt32BE(reader);
            for (int i = 0; i < entryCount; i++)
            {
                if (stream.Position + 62 > stream.Length) break;
                stream.Seek(40, SeekOrigin.Current);                       // ctime..size metadata
                byte[] sha = reader.ReadBytes(20);
                ushort flags = ReadUInt16BE(reader);
                int nameLength = flags & 0x0FFF;
                if (stream.Position + nameLength > stream.Length) break;
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                int pad = 8 - ((62 + nameLength) % 8);
                stream.Seek(pad, SeekOrigin.Current);
                results.Add(new { Path = name, Sha = BitConverter.ToString(sha).Replace("-", "").ToLowerInvariant().Substring(0, 8) });
            }
        }
        catch (Exception ex) { WriteWarning("index parse stopped: " + ex.Message); }
        return results.ToArray();
    }

    // --- Working-tree STATUS: each TRACKED file compared to its staged index blob — the "unstaged"
    // column git shows as " M"/" D". M = content differs, D = missing from the worktree. CRLF is
    // normalized git's way (the clean filter) so an autocrlf=true repo (git stores LF blobs; the
    // worktree may be CRLF) does NOT false-positive — the exact trap a naive byte-hash falls into.
    // Worktree-vs-index only (no HEAD tree / packfile read); staged-vs-HEAD + untracked-vs-.gitignore
    // are the libgit2 endgame, same line as the rest of this reader.
    private object[] ReadStatus(string gitDir)
    {
        var indexPath = Path.Combine(gitDir, "index");
        if (!File.Exists(indexPath)) return Array.Empty<object>();
        var status = new List<object>();
        bool autocrlf = ReadAutocrlf(gitDir);
        try
        {
            using var stream = File.OpenRead(indexPath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 12) return Array.Empty<object>();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "DIRC") return Array.Empty<object>();
            ReadUInt32BE(reader);                       // version
            uint entryCount = ReadUInt32BE(reader);
            using var sha1 = SHA1.Create();
            for (int i = 0; i < entryCount; i++)
            {
                if (stream.Position + 62 > stream.Length) break;
                stream.Seek(40, SeekOrigin.Current);                       // ctime..size metadata
                byte[] shaBytes = reader.ReadBytes(20);
                ushort flags = ReadUInt16BE(reader);
                int nameLength = flags & 0x0FFF;
                if (stream.Position + nameLength > stream.Length) break;
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                int pad = 8 - ((62 + nameLength) % 8);
                stream.Seek(pad, SeekOrigin.Current);

                var wt = Path.Combine(GitRoot, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(wt)) { status.Add(new { Path = name, State = "D" }); continue; }
                string indexSha = BitConverter.ToString(shaBytes).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(WorktreeBlobSha(sha1, wt, autocrlf), indexSha, StringComparison.Ordinal))
                    status.Add(new { Path = name, State = "M" });
            }
        }
        catch (Exception ex) { WriteWarning("status stopped: " + ex.Message); }
        return status.ToArray();
    }

    // The git blob id of a worktree file: sha1("blob <len>\0" + content), content normalized CRLF->LF
    // for text when autocrlf is on (binary = a NUL byte in the first 8000, git's own heuristic).
    private static string WorktreeBlobSha(SHA1 sha1, string path, bool autocrlf)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] content = (autocrlf && !IsBinary(raw)) ? CrlfToLf(raw) : raw;
        byte[] header = Encoding.ASCII.GetBytes("blob " + content.Length + "\0");
        byte[] buf = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        Buffer.BlockCopy(content, 0, buf, header.Length, content.Length);
        return BitConverter.ToString(sha1.ComputeHash(buf)).Replace("-", "").ToLowerInvariant();
    }

    private static bool IsBinary(byte[] b)
    {
        int n = Math.Min(b.Length, 8000);
        for (int i = 0; i < n; i++) if (b[i] == 0) return true;
        return false;
    }

    // git's clean filter for CRLF: drop the CR of every CRLF pair (a lone CR is left untouched).
    private static byte[] CrlfToLf(byte[] b)
    {
        var outp = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] == 0x0D && i + 1 < b.Length && b[i + 1] == 0x0A) continue;
            outp.Add(b[i]);
        }
        return outp.ToArray();
    }

    // core.autocrlf across the config cascade (system -> global -> local, last wins). true AND input
    // both mean "clean to LF" for the blob id we compute. Parsed natively — no git.exe, no libgit2 yet.
    private bool ReadAutocrlf(string gitDir)
    {
        bool? val = null;
        foreach (var cfg in EnumConfigFiles(gitDir))
            if (TryReadCoreAutocrlf(cfg, out var v)) val = v;
        return val ?? false;
    }

    private static IEnumerable<string> EnumConfigFiles(string gitDir)
    {
        var drive = Path.GetPathRoot(Environment.ProcessPath ?? "") ?? "S:\\";
        yield return Path.Combine(drive, "bin", "git", "etc", "gitconfig");      // sanctioned system git
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "Git", "etc", "gitconfig");
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".gitconfig");                       // global
            yield return Path.Combine(home, ".config", "git", "config");
        }
        yield return Path.Combine(gitDir, "config");                             // local (wins)
    }

    private static bool TryReadCoreAutocrlf(string file, out bool value)
    {
        value = false;
        if (!File.Exists(file)) return false;
        bool inCore = false, found = false;
        try
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[') { inCore = line.StartsWith("[core", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inCore) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("autocrlf", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line.Substring(eq + 1).Trim();
                value = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("input", StringComparison.OrdinalIgnoreCase);
                found = true;
            }
        }
        catch { return false; }
        return found;
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha!.Substring(0, Math.Min(8, sha.Length));
    private static uint ReadUInt32BE(BinaryReader r) { var b = r.ReadBytes(4); Array.Reverse(b); return BitConverter.ToUInt32(b, 0); }
    private static ushort ReadUInt16BE(BinaryReader r) { var b = r.ReadBytes(2); Array.Reverse(b); return BitConverter.ToUInt16(b, 0); }
}
