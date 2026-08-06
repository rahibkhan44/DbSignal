namespace DbSignal.Sqlite;

/// <summary>
/// Entry point for building a SQLite feed without dependency injection.
/// </summary>
/// <example>
/// <code>
/// await using var feed = SqliteFeed.For("Data Source=app.db").Build();
/// await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
/// {
///     Console.WriteLine($"Something changed at {batch.ObservedUtc:HH:mm:ss}");
/// }
/// </code>
/// </example>
public static class SqliteFeed
{
    /// <summary>Starts building a feed over the given database.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    public static SqliteFeedBuilder For(string connectionString) => new(connectionString);
}

/// <summary>Fluent builder for <see cref="SqliteChangeFeed"/>.</summary>
public sealed class SqliteFeedBuilder
{
    private readonly string _connectionString;
    private TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);

    internal SqliteFeedBuilder(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Sets how often to check for changes. Each check is a single integer read, so this
    /// can be short; the default is 250ms, which reads as instant to a person.
    /// </summary>
    /// <param name="interval">Time between polls.</param>
    public SqliteFeedBuilder PollEvery(TimeSpan interval)
    {
        _pollInterval = interval;
        return this;
    }

    /// <summary>
    /// Accepted and <strong>ignored</strong>, so that code written against a provider that
    /// does support table filtering still compiles when pointed at SQLite.
    /// </summary>
    /// <remarks>
    /// SQLite's <c>data_version</c> is one number for the whole file — there is no table
    /// information to filter on. The feed reports
    /// <see cref="ChangeDetail.DatabaseChanged"/> so a consumer can detect this at startup
    /// via <c>RequireAtLeast</c> rather than discovering it in production.
    /// </remarks>
    /// <param name="tables">Table names. Ignored.</param>
    public SqliteFeedBuilder Watch(params string[] tables) => this;

    /// <summary>Builds the feed.</summary>
    public SqliteChangeFeed Build() => new(_connectionString, _pollInterval);
}
