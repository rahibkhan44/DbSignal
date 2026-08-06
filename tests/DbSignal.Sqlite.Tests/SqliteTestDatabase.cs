using Microsoft.Data.Sqlite;

namespace DbSignal.Sqlite.Tests;

/// <summary>
/// A real SQLite file in the temp directory, deleted on dispose.
/// </summary>
/// <remarks>
/// Deliberately a <strong>file</strong>, not <c>:memory:</c>. The scenario under test is
/// "another process wrote to my database", and an in-memory database cannot be opened by
/// a second connection in the ordinary way — the test would pass against a setup that
/// does not resemble the situation the library exists for.
/// </remarks>
public sealed class SqliteTestDatabase : IDisposable
{
    public SqliteTestDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"dbsignal_test_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={Path}";

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public string Path { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// Writes on a brand-new connection — standing in for the ERP job, the script, or the
    /// person in a database GUI. Nothing here is shared with the feed.
    /// </summary>
    public void WriteFromSeparateConnection(string name)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Products (Name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Pooled connections keep a handle on the file; release them before deleting.
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
