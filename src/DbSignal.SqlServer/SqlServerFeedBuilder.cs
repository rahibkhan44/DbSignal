namespace DbSignal.SqlServer;

/// <summary>Entry point for building a SQL Server feed without dependency injection.</summary>
/// <example>
/// <code>
/// await using var feed = SqlServerFeed.For(connectionString)
///                                     .Watch("dbo.Products", "dbo.Customers")
///                                     .Build();
///
/// await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
/// {
///     foreach (var table in batch.Tables)
///         Console.WriteLine($"{table.QualifiedName}: {table.Keys.Count} rows");
/// }
/// </code>
/// </example>
public static class SqlServerFeed
{
    /// <summary>Starts building a feed over the given database.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    public static SqlServerFeedBuilder For(string connectionString) => new(connectionString);
}

/// <summary>Fluent builder for <see cref="SqlServerChangeFeed"/>.</summary>
public sealed class SqlServerFeedBuilder
{
    private readonly string _connectionString;
    private readonly List<WatchedTable> _tables = [];
    private TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);
    private int _retentionDays = 2;

    internal SqlServerFeedBuilder(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Adds tables to watch. Accepts <c>Products</c> or <c>dbo.Products</c>; unqualified
    /// names get the <c>dbo</c> schema.
    /// </summary>
    /// <param name="tables">Table names.</param>
    public SqlServerFeedBuilder Watch(params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables.AddRange(tables.Select(WatchedTable.Parse));
        return this;
    }

    /// <summary>
    /// Sets how often to check. An idle check is one scalar read, so this can be short.
    /// </summary>
    /// <param name="interval">Time between polls.</param>
    public SqlServerFeedBuilder PollEvery(TimeSpan interval)
    {
        _pollInterval = interval;
        return this;
    }

    /// <summary>
    /// How long SQL Server retains change history, used when provisioning. A consumer
    /// offline longer than this must do a full reload.
    /// </summary>
    /// <param name="days">Retention in days.</param>
    public SqlServerFeedBuilder WithRetention(int days)
    {
        _retentionDays = days;
        return this;
    }

    /// <summary>
    /// The provisioner for these tables — enable Change Tracking, or print the script for
    /// a DBA. Never run implicitly by <see cref="Build"/>.
    /// </summary>
    public SqlServerProvisioner Provisioner() =>
        new(_connectionString, _tables, _retentionDays);

    /// <summary>Builds the feed.</summary>
    /// <exception cref="InvalidOperationException">No tables were specified.</exception>
    public SqlServerChangeFeed Build()
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException(
                "Call Watch(...) with at least one table. Change Tracking is per-table, so a " +
                "feed watching nothing would silently report nothing.");
        }

        return new SqlServerChangeFeed(_connectionString, _tables, _pollInterval);
    }
}
