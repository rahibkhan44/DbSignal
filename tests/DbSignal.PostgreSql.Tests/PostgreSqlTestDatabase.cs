using Npgsql;

namespace DbSignal.PostgreSql.Tests;

/// <summary>
/// A real PostgreSQL database with logical replication available, created and dropped per
/// test class.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order: the <c>DBSIGNAL_POSTGRES</c> environment variable (what CI sets), then a
/// local default. If neither answers, every test <strong>skips visibly</strong> — the suite
/// never reports green for a database it did not touch.
/// </para>
/// <para>
/// The server must be running with <c>wal_level = logical</c>. That cannot be set at runtime,
/// so a server without it counts as unavailable rather than failing every test with the same
/// message.
/// </para>
/// </remarks>
public sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private const string LocalServer =
        "Host=localhost;Port=5432;Username=postgres;Password=dbsignal;Database=postgres";

    private readonly string? _adminConnectionString;
    private readonly string _databaseName;

    public PostgreSqlTestDatabase()
    {
        _databaseName = $"dbsignal_test_{Guid.NewGuid():N}";
        _adminConnectionString = ResolveServer();

        if (_adminConnectionString is null)
        {
            ConnectionString = string.Empty;
            return;
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;
    }

    /// <summary>Connection string for the test database. Empty when no server was found.</summary>
    public string ConnectionString { get; }

    /// <summary>False when no PostgreSQL answered, so tests skip rather than fail.</summary>
    public bool IsAvailable => _adminConnectionString is not null;

    /// <summary>How the server was found, for the skip message.</summary>
    public string Description { get; } =
        Environment.GetEnvironmentVariable("DBSIGNAL_POSTGRES") is not null
            ? "PostgreSQL (DBSIGNAL_POSTGRES)"
            : "PostgreSQL (localhost)";

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await ExecuteAsync(_adminConnectionString!, $"CREATE DATABASE \"{_databaseName}\";");

        await ExecuteAsync(ConnectionString,
            "CREATE TABLE public.products (id SERIAL PRIMARY KEY, name TEXT NOT NULL);");

        // Deliberately not published, so a test can prove the feed ignores tables it was not
        // asked to watch.
        await ExecuteAsync(ConnectionString,
            "CREATE TABLE public.unwatched (id SERIAL PRIMARY KEY, note TEXT NULL);");
    }

    /// <summary>
    /// Writes on a brand-new connection — the ERP job, the script, the person in psql.
    /// Nothing shared with the feed.
    /// </summary>
    public async Task<int> InsertProductAsync(string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO public.products (name) VALUES (@name) RETURNING id;";
        _ = command.Parameters.AddWithValue("@name", name);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task UpdateProductAsync(int id, string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE public.products SET name = @name WHERE id = @id;";
        _ = command.Parameters.AddWithValue("@name", name);
        _ = command.Parameters.AddWithValue("@id", id);
        _ = await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteProductAsync(int id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM public.products WHERE id = @id;";
        _ = command.Parameters.AddWithValue("@id", id);
        _ = await command.ExecuteNonQueryAsync();
    }

    public async Task InsertUnwatchedAsync(string note)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO public.unwatched (note) VALUES (@note);";
        _ = command.Parameters.AddWithValue("@note", note);
        _ = await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts several products inside one transaction, then commits or rolls back.
    /// </summary>
    public async Task InsertProductsInTransactionAsync(bool commit, params string[] names)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var name in names)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO public.products (name) VALUES (@name);";
            _ = command.Parameters.AddWithValue("@name", name);
            _ = await command.ExecuteNonQueryAsync();
        }

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>Drops the replication slots first, then the database.</summary>
    /// <remarks>
    /// <strong>The slot drop is not tidiness.</strong> An abandoned logical replication slot
    /// retains WAL indefinitely, and PostgreSQL refuses to drop a database that still has one.
    /// A run that leaked slots would eventually fill the disk of whatever machine it ran on.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        try
        {
            await ExecuteAsync(_adminConnectionString!,
                "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots " +
                "WHERE database = @db;",
                _databaseName);

            await ExecuteAsync(_adminConnectionString!,
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                "WHERE datname = @db AND pid <> pg_backend_pid();",
                _databaseName);

            await ExecuteAsync(_adminConnectionString!, $"DROP DATABASE \"{_databaseName}\";");
        }
        catch (NpgsqlException)
        {
            // A leftover test database is not worth failing a run over.
        }
    }

    private static string? ResolveServer()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DBSIGNAL_POSTGRES");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && HasLogicalWal(fromEnvironment))
        {
            return fromEnvironment;
        }

        return HasLogicalWal(LocalServer) ? LocalServer : null;
    }

    private static bool HasLogicalWal(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "postgres",
                Timeout = 10,
            };

            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SHOW wal_level;";
            return (string?)command.ExecuteScalar() == "logical";
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, string? db = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (db is not null)
        {
            _ = command.Parameters.AddWithValue("@db", db);
        }

        _ = await command.ExecuteNonQueryAsync();
    }
}
