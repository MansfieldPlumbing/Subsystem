using System;
using System.Collections.Generic;

namespace Subsystem.Host;

// ISubsystemApi — strongly-typed central API contract.
// This is the core banner for all subsystem operations.
public interface ISubsystemApi
{
    IWorkflowService Workflow { get; }
    IFilesystemService Filesystem { get; }
    IDatabaseService Database { get; }
}

public interface IWorkflowService
{
    object CreateRequest(string kind, string category, string summary, string[]? relatedFiles, int severity);
    object? CloseRequest(long id, string disposition);
    object? WriteWorkLog(long id, string disposition, string body);
    List<object> QueryRequests(string? kind, string? category, string? status, long? id, string? search, long since, long before);
}

public interface IFilesystemService
{
    string ListDirectory(string path, int depth, string filter);
    string ReadFile(string path, int startLine, int endLine);
    List<string> FindFiles(string root, string pattern, int maxDepth);
    string StatPath(string path);
}

public interface IDatabaseService
{
    int ExecuteNonQuery(string dbPath, string sql, Dictionary<string, object?>? parameters = null);
    object? ExecuteScalar(string dbPath, string sql, Dictionary<string, object?>? parameters = null);
    void ExecuteReader(string dbPath, string sql, Dictionary<string, object?>? parameters = null, Action<System.Data.Common.DbDataReader> onRow = null!);
}
