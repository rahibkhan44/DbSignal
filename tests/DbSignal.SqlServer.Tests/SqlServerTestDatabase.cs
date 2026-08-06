using Microsoft.Data.SqlClient;

namespace DbSignal.SqlServer.Tests;

/// <summary>
/// A real SQL Server database with Change Tracking enabled, created and dropped per test class.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order: the <c>DBSIGNAL_SQLSERVER</c> environment variable (what CI sets, and
/// what points at a Testcontainers instance), then LocalDB. If neither answers, every test
/// <strong>skips visibly</strong> — the suite never reports green for a database it did not
/// touch.
/// </para>
/// <para>
/// LocalDB is enough: Change Tracking is available in every SQL Server edition, including
/// Express. CDC is the one that needs a bigger edition, and this provider deliberately does
/// not use CDC.
/// </para>
/// </remarks>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private const string LocalDbServer = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private readonly string? _masterConnectionString;
    private readonly string _databaseName;

    public SqlServerTestDatabase()
    {
        _databaseName = $"dbsignal_test_{Guid.NewGuid():N}";
        _masterConnectionString = ResolveServer();

        if (_masterConnectionString is null)
        {
            ConnectionString = string.Empty;
            return;
        }

        var builder = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = _databaseName,
        };
        ConnectionString = builder.ConnectionString;
    }

    /// <summary>Connection string for the test database. Empty when no server was found.</summary>
    public string ConnectionString { get; }

    /// <summary>False when no SQL Server answered, so tests skip rather than fail.</summary>
    public bool IsAvailable => _masterConnectionString is not null;

    /// <summary>How the server was found, for the skip message.</summary>
    public string Description { get; } =
        Environment.GetEnvironmentVariable("DBSIGNAL_SQLSERVER") is not null
            ? "SQL Server (DBSIGNAL_SQLSERVER)"
            : "SQL Server LocalDB";

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await ExecuteAsync(_masterConnectionString!, $"CREATE DATABASE [{_databaseName}];");

        await ExecuteAsync(ConnectionString,
            "CREATE TABLE dbo.Products (Id INT IDENTITY(1,1) PRIMARY KEY, Name NVARCHAR(200) NOT NULL);");

        // Deliberately NOT change-tracked, so a test can prove the feed ignores tables it
        // was not asked to watch.
        await ExecuteAsync(ConnectionString,
            "CREATE TABLE dbo.Unwatched (Id INT IDENTITY(1,1) PRIMARY KEY, Note NVARCHAR(200) NULL);");
    }

    /// <summary>
    /// Writes on a brand-new connection — the ERP job, the script, the person in SSMS.
    /// Nothing shared with the feed.
    /// </summary>
    public async Task InsertProductAsync(string name)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dbo.Products (Name) VALUES (@name);";
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateProductAsync(int id, string name)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Products SET Name = @name WHERE Id = @id;";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteProductAsync(int id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Products WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertUnwatchedAsync(string note)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dbo.Unwatched (Note) VALUES (@note);";
        command.Parameters.AddWithValue("@note", note);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        SqlConnection.ClearAllPools();

        try
        {
            await ExecuteAsync(
                _masterConnectionString!,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_databaseName}];");
        }
        catch (SqlException)
        {
            // A leftover test database is not worth failing a run over.
        }
    }

    private static string? ResolveServer()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DBSIGNAL_SQLSERVER");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && CanConnect(fromEnvironment))
        {
            return fromEnvironment;
        }

        return CanConnect(LocalDbServer) ? LocalDbServer : null;
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                ConnectTimeout = 15,
            };

            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
