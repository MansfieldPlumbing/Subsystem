#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Subsystem.Host;

// Sql — standalone SQLite execution helper.
// Can be driven with any database file path.
public static class Sql
{
    private static string GetConnectionString(string dbPath) => $"Data Source={dbPath}";

    public static void Initialize(string dbPath, string schemaSql)
    {
        ExecuteNonQuery(dbPath, schemaSql);
    }

    public static int ExecuteNonQuery(string dbPath, string sql, Dictionary<string, object?>? parameters = null, SqliteTransaction? transaction = null)
    {
        if (transaction != null)
        {
            using var cmd = transaction.Connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            BindParameters(cmd, parameters);
            return cmd.ExecuteNonQuery();
        }

        using var conn = new SqliteConnection(GetConnectionString(dbPath));
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        BindParameters(command, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? ExecuteScalar(string dbPath, string sql, Dictionary<string, object?>? parameters = null, SqliteTransaction? transaction = null)
    {
        if (transaction != null)
        {
            using var cmd = transaction.Connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            BindParameters(cmd, parameters);
            return cmd.ExecuteScalar();
        }

        using var conn = new SqliteConnection(GetConnectionString(dbPath));
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        BindParameters(command, parameters);
        return command.ExecuteScalar();
    }

    public static void ExecuteReader(string dbPath, string sql, Dictionary<string, object> parameters = null, Action<SqliteDataReader> onRow = null!)
    {
        using var conn = new SqliteConnection(GetConnectionString(dbPath));
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        BindParameters(command, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }

    public static void RunInTransaction(string dbPath, Action<SqliteTransaction> action)
    {
        using var conn = new SqliteConnection(GetConnectionString(dbPath));
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            action(transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void BindParameters(SqliteCommand cmd, Dictionary<string, object> parameters)
    {
        if (parameters == null) return;
        foreach (var kvp in parameters)
        {
            cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
        }
    }
}
