namespace DbSignal.PostgreSql;

/// <summary>Entry point for building a PostgreSQL feed without dependency injection.</summary>
/// <example>
/// <code>
/// await using var feed = PostgreSqlFeed.For(connectionString)
///                                      .Watch("public.products")
///                                      .Build();
///
/// await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
///     foreach (var table in batch.Tables)
///         foreach (var row in table.Rows)
///             Console.WriteLine($"{row.Kind}: {row.Before?["name"]} -> {row.After?["name"]}");
/// </code>
/// </example>
public static class PostgreSqlFeed
{
    /// <summary>Starts building a feed over the given database.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    public static PostgreSqlFeedBuilder For(string connectionString) => new(connectionString);
}

/// <summary>Fluent builder for <see cref="PostgreSqlChangeFeed"/>.</summary>
public sealed class PostgreSqlFeedBuilder
{
    private readonly string _connectionString;
    private readonly List<PublishedTable> _tables = [];
    private string _publicationName = "dbsignal_pub";
    private string _slotName = "dbsignal_slot";

    internal PostgreSqlFeedBuilder(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Adds tables to watch. Accepts <c>products</c> or <c>public.products</c>; unqualified
    /// names get the <c>public</c> schema, and unquoted names are lower-cased to match how
    /// PostgreSQL will resolve them.
    /// </summary>
    /// <param name="tables">Table names.</param>
    public PostgreSqlFeedBuilder Watch(params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables.AddRange(tables.Select(PublishedTable.Parse));
        return this;
    }

    /// <summary>
    /// Names the publication. Defaults to <c>dbsignal_pub</c>.
    /// </summary>
    /// <remarks>
    /// Give each application its own publication and slot. Two applications sharing a slot
    /// steal each other's changes — whichever reads first advances the position for both.
    /// </remarks>
    /// <param name="name">Publication name.</param>
    public PostgreSqlFeedBuilder WithPublication(string name)
    {
        _publicationName = name;
        return this;
    }

    /// <summary>Names the replication slot. Defaults to <c>dbsignal_slot</c>.</summary>
    /// <param name="name">Slot name.</param>
    public PostgreSqlFeedBuilder WithSlot(string name)
    {
        _slotName = name;
        return this;
    }

    /// <summary>
    /// The provisioner for these tables — create the publication, set replica identity and
    /// create the slot, or print the script for a DBA. Never run implicitly by
    /// <see cref="Build"/>.
    /// </summary>
    public PostgreSqlProvisioner Provisioner() =>
        new(_connectionString, _tables, _publicationName, _slotName);

    /// <summary>Builds the feed.</summary>
    /// <exception cref="InvalidOperationException">No tables were specified.</exception>
    public PostgreSqlChangeFeed Build()
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException(
                "Call Watch(...) with at least one table. A publication covering nothing would " +
                "stream nothing, silently.");
        }

        return new PostgreSqlChangeFeed(_connectionString, _tables, _publicationName, _slotName);
    }
}
