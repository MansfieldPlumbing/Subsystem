using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Subsystem.Remedy;

namespace Subsystem.Host;

public sealed class SubsystemHostApi : ISubsystemApi
{
    private static readonly Lazy<SubsystemHostApi> _instance = new(() => new SubsystemHostApi());
    public static SubsystemHostApi Instance => _instance.Value;

    public IWorkflowService Workflow { get; } = new WorkflowDispatch();
    public IFilesystemService Filesystem { get; } = new FilesystemDispatch();
    public IDatabaseService Database { get; } = new DatabaseDispatch();

    private SubsystemHostApi() { }

    private class WorkflowDispatch : IWorkflowService
    {
        public object CreateRequest(string kind, string category, string summary, string[]? relatedFiles, int severity)
        {
            return RequestHive.Create(kind, category, summary, relatedFiles, severity);
        }

        public object? CloseRequest(long id, string disposition)
        {
            return RequestHive.Close(id, disposition);
        }

        public object? WriteWorkLog(long id, string disposition, string body)
        {
            return RequestHive.WriteEosLog(id, disposition, body);
        }

        public List<object> QueryRequests(string? kind, string? category, string? status, long? id, string? search, long since, long before)
        {
            return RequestHive.Query(kind, category, status, id, search, since, before).Cast<object>().ToList();
        }
    }

    private class FilesystemDispatch : IFilesystemService
    {
        public string ListDirectory(string path, int depth, string filter)
        {
            var dir = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path;
            if (!Directory.Exists(dir)) return $"ERROR: directory not found: {dir}";
            var sb = new StringBuilder();
            sb.AppendLine($"[{dir}]");
            Walk(dir, dir, depth == 0 ? int.MaxValue : depth, 0, string.IsNullOrWhiteSpace(filter) ? "*" : filter, sb);
            return sb.ToString();
        }

        private void Walk(string root, string dir, int maxDepth, int curDepth, string glob, StringBuilder sb)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, glob))
                    sb.AppendLine($"  {new string(' ', curDepth * 2)}FILE  {Path.GetFileName(f),40}  {new FileInfo(f).Length,12} bytes");
                if (curDepth < maxDepth)
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        sb.AppendLine($"  {new string(' ', curDepth * 2)}DIR   {Path.GetFileName(d)}/");
                        Walk(root, d, maxDepth, curDepth + 1, glob, sb);
                    }
            }
            catch (UnauthorizedAccessException) { sb.AppendLine($"  {new string(' ', curDepth * 2)}[access denied]"); }
        }

        public string ReadFile(string path, int startLine, int endLine)
        {
            if (string.IsNullOrWhiteSpace(path)) return "ERROR: path is required.";
            if (!File.Exists(path)) return $"ERROR: file not found: {path}";
            try
            {
                var lines = File.ReadAllLines(path);
                int start = Math.Max(1, startLine);
                int end = endLine <= 0 ? lines.Length : Math.Min(lines.Length, endLine);
                var sb = new StringBuilder();
                sb.AppendLine($"[{path}] lines {start}-{end} of {lines.Length}");
                for (int i = start - 1; i < end; i++)
                    sb.AppendLine($"{i + 1,6}: {lines[i]}");
                return sb.ToString();
            }
            catch (Exception ex) { return $"ERROR: {ex.Message}"; }
        }

        public List<string> FindFiles(string root, string pattern, int maxDepth)
        {
            var dir = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root;
            if (!Directory.Exists(dir)) return new List<string> { $"ERROR: directory not found: {dir}" };
            var hits = new List<string>();
            FindWalk(dir, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, maxDepth == 0 ? int.MaxValue : maxDepth, 0, hits);
            return hits;
        }

        private void FindWalk(string dir, string pattern, int maxDepth, int cur, List<string> hits)
        {
            if (hits.Count >= 200) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, pattern))
                { hits.Add(f); if (hits.Count >= 200) return; }
                if (cur < maxDepth)
                    foreach (var d in Directory.GetDirectories(dir))
                        FindWalk(d, pattern, maxDepth, cur + 1, hits);
            }
            catch (UnauthorizedAccessException) { hits.Add($"[access denied: {dir}]"); }
        }

        public string StatPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "ERROR: path is required.";
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return $"type=file\npath={fi.FullName}\nsize={fi.Length} bytes\ncreated={fi.CreationTimeUtc:o}\nmodified={fi.LastWriteTimeUtc:o}\nreadonly={fi.IsReadOnly}";
            }
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return $"type=directory\npath={di.FullName}\ncreated={di.CreationTimeUtc:o}\nmodified={di.LastWriteTimeUtc:o}";
            }
            return $"type=notfound\npath={path}";
        }
    }

    private class DatabaseDispatch : IDatabaseService
    {
        public int ExecuteNonQuery(string dbPath, string sql, Dictionary<string, object?>? parameters = null)
        {
            return Sql.ExecuteNonQuery(dbPath, sql, parameters);
        }

        public object? ExecuteScalar(string dbPath, string sql, Dictionary<string, object?>? parameters = null)
        {
            return Sql.ExecuteScalar(dbPath, sql, parameters);
        }

        public void ExecuteReader(string dbPath, string sql, Dictionary<string, object?>? parameters = null, Action<System.Data.Common.DbDataReader> onRow = null!)
        {
            Sql.ExecuteReader(dbPath, sql, parameters, reader => onRow(reader));
        }
    }
}
