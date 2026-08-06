using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace DbSignal.SqlServer;

/// <summary>
/// Detects changes to SQL Server tables using Change Tracking, and reports <em>which rows</em>
/// changed — regardless of which application wrote them.
/// </summary>
/// <remarks>
/// <para>
/// Change Tracking lives inside the engine. There are no triggers to maintain, no Service
/// Broker queues to provision and clean up, and no restriction on the shape of your queries.
/// That last point matters: <c>SqlDependency</c> and <c>SqlTableDependency</c> forbid
/// <c>JOIN</c>s in the watched query, a rule easy to miss and expensive to discover — one
/// reported six-table join hung a database badly enough to need a restart.
/// </para>
/// <para>
/// <strong>Cheap when idle.</strong> A quiet poll is one scalar read of
/// <c>CHANGE_TRACKING_CURRENT_VERSION()</c>. Only when that number moves does the feed ask
/// each watched table what changed, so poll intervals can be short without cost.
/// </para>
/// <para>
/// <strong>Requires provisioning</strong> — see <see cref="SqlServerProvisioner"/>. The feed
/// checks and fails with a clear message rather than returning an empty stream forever.
/// </para>
/// </remarks>
public sealed class SqlServerChangeFeed : IChangeFeed
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<WatchedTable> _tables;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates a feed over the given tables.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    /// <param name="tables">Tables to watch. At least one is required.</param>
    /// <param name="pollInterval">
    /// How often to check. The idle check is a single scalar read; 250ms reads as instant.
    /// </param>
    /// <exception cref="ArgumentException">No connection string, or no tables.</exception>
    public SqlServerChangeFeed(
        string connectionString,
        IReadOnlyList<WatchedTable> tables,
        TimeSpan? pollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
        {
            throw new ArgumentException(
                "At least one table is required. Change Tracking is per-table, so a feed " +
                "watching nothing would silently report nothing.",
                nameof(tables));
        }

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Poll interval must be positive.");
        }

        _connectionString = connectionString;
        _tables = tables;
        _pollInterval = interval;
    }

    /// <inheritdoc />
    public string ProviderName => "SQL Server";

    /// <inheritdoc />
    /// <remarks>
    /// <c>FiltersOwnWrites</c> is <see langword="false"/>: Change Tracking records every
    /// change, including those made by the application consuming this feed. Handlers will
    /// see an echo of their own writes and must tolerate it.
    /// </remarks>
    public FeedCapabilities Capabilities { get; } = new(
        Detail: ChangeDetail.KeysChanged,
        DurableAcrossRestart: true,
        SurvivesDowntime: true,
        FiltersOwnWrites: false,
        RequiresProvisioning: true);

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeBatch> ReadAsync(
        Checkpoint from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await AssertProvisionedAsync(connection, ct).ConfigureAwait(false);

        var lastSeen = await ResolveStartVersionAsync(connection, from, ct).ConfigureAwait(false);

        while (true)
        {
            long current;
            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                current = await GetCurrentVersionAsync(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (current <= lastSeen)
            {
                continue;
            }

            // Cleanup may have discarded the window we were about to read. Detect it
            // before querying, and say so loudly — a hand-rolled poller typically finds
            // no rows here and reports "nothing changed", losing the data silently.
            await AssertNotExpiredAsync(connection, lastSeen, ct).ConfigureAwait(false);

            var tables = await ReadChangedTablesAsync(connection, lastSeen, ct).ConfigureAwait(false);
            lastSeen = current;

            // The version moves for any tracked table in the database. If none of OUR
            // tables changed, advance quietly rather than waking every handler.
            if (tables.Count == 0)
            {
                continue;
            }

            yield return new ChangeBatch(
                new Checkpoint(current.ToString(CultureInfo.InvariantCulture)),
                tables,
                DateTimeOffset.UtcNow);
        }
    }

    private async Task AssertProvisionedAsync(SqlConnection connection, CancellationToken ct)
    {
        if (!await SqlServerProvisioner.IsDatabaseEnabledAsync(connection, ct).ConfigureAwait(false))
        {
            throw new ProvisioningRequiredException(
                $"Change Tracking is not enabled on database '{connection.Database}'. " +
                "Enable it with SqlServerProvisioner.EnsureAsync(), or run the script from " +
                "GetScript() as an administrator.");
        }

        foreach (var table in _tables)
        {
            if (!await SqlServerProvisioner.IsTableEnabledAsync(connection, table, ct).ConfigureAwait(false))
            {
                throw new ProvisioningRequiredException(
                    $"Change Tracking is not enabled on table '{table.QualifiedName}'. " +
                    "Enable it with SqlServerProvisioner.EnsureAsync(), or run the script from " +
                    "GetScript() as an administrator.");
            }
        }
    }

    private async Task AssertNotExpiredAsync(SqlConnection connection, long since, CancellationToken ct)
    {
        foreach (var table in _tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@table));";
            command.Parameters.AddWithValue("@table", table.QualifiedName);

            var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null || raw == DBNull.Value)
            {
                continue;
            }

            var minValid = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (minValid > since)
            {
                throw new ResyncRequiredException(
                    $"Change history for '{table.QualifiedName}' no longer reaches back to version {since} " +
                    $"(the oldest available is {minValid}). The retention window elapsed while this feed " +
                    "was not reading. Reload the data in full and restart from Checkpoint.Now.");
            }
        }
    }

    private static async Task<long> GetCurrentVersionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return raw is null || raw == DBNull.Value
            ? 0
            : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ResolveStartVersionAsync(
        SqlConnection connection, Checkpoint from, CancellationToken ct)
    {
        if (from.IsNow)
        {
            return await GetCurrentVersionAsync(connection, ct).ConfigureAwait(false);
        }

        if (from.IsBeginning)
        {
            return 0;
        }

        return long.TryParse(from.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            // A checkpoint from a different provider, or a corrupted store. Starting from
            // "now" loses history; starting from 0 would replay everything. Refuse instead,
            // because guessing either way hides a real configuration mistake.
            : throw new DbSignalException(
                  $"'{from.Value}' is not a SQL Server change-tracking version. " +
                  "Checkpoints are provider-specific and cannot be shared between providers.");
    }

    private async Task<List<TableChange>> ReadChangedTablesAsync(
        SqlConnection connection, long since, CancellationToken ct)
    {
        var changed = new List<TableChange>();

        foreach (var table in _tables)
        {
            var keys = await ReadChangedKeysAsync(connection, table, since, ct).ConfigureAwait(false);
            if (keys.Count > 0)
            {
                changed.Add(new TableChange(table.Schema, table.Name, keys, Array.Empty<RowImage>()));
            }
        }

        return changed;
    }

    private static async Task<List<ChangeKey>> ReadChangedKeysAsync(
        SqlConnection connection, WatchedTable table, long since, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        // The table name cannot be a parameter here — CHANGETABLE takes an identifier, not
        // a value. WatchedTable.QuotedName doubles any embedded ']' so the identifier
        // cannot break out of its brackets.
        command.CommandText =
            $"SELECT * FROM CHANGETABLE(CHANGES {table.QuotedName}, @since) AS ct;";
        command.Parameters.AddWithValue("@since", since);

        var keys = new List<ChangeKey>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // CHANGETABLE returns the primary key columns plus SYS_CHANGE_* metadata. Rather
        // than querying sys.index_columns for the key, take everything that isn't metadata —
        // which is the key, by definition of what CHANGETABLE returns.
        var keyOrdinals = new List<int>();
        var operationOrdinal = -1;

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.Equals(name, "SYS_CHANGE_OPERATION", StringComparison.OrdinalIgnoreCase))
            {
                operationOrdinal = i;
            }
            else if (!name.StartsWith("SYS_CHANGE_", StringComparison.OrdinalIgnoreCase))
            {
                keyOrdinals.Add(i);
            }
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object?[keyOrdinals.Count];
            for (var i = 0; i < keyOrdinals.Count; i++)
            {
                var ordinal = keyOrdinals[i];
                values[i] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            var kind = operationOrdinal >= 0 && !reader.IsDBNull(operationOrdinal)
                ? MapOperation(reader.GetString(operationOrdinal))
                : ChangeKind.Unknown;

            keys.Add(new ChangeKey(values, kind));
        }

        return keys;
    }

    private static ChangeKind MapOperation(string operation) => operation switch
    {
        "I" => ChangeKind.Insert,
        "U" => ChangeKind.Update,
        "D" => ChangeKind.Delete,
        _ => ChangeKind.Unknown,
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;
}
