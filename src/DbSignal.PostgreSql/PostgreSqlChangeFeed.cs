using System.Runtime.CompilerServices;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace DbSignal.PostgreSql;

/// <summary>
/// Streams changes from PostgreSQL using logical replication, reporting the rows another
/// application changed <em>and their before and after values</em>.
/// </summary>
/// <remarks>
/// <para>
/// The first streaming provider in DbSignal. SQLite and SQL Server poll; this one holds a
/// replication connection open and the server pushes as transactions commit. Consumers
/// cannot tell the difference, which is the point of <see cref="IChangeFeed"/>.
/// </para>
/// <para>
/// <strong>One transaction becomes one <see cref="ChangeBatch"/>.</strong> pgoutput sends a
/// flat stream — <c>Begin</c>, then row messages, then <c>Commit</c> — and this feed
/// accumulates between them, emitting at the commit with the commit LSN as the position. A
/// consumer therefore never sees half a transaction, which the polling providers cannot
/// promise.
/// </para>
/// <para>
/// <strong>Requires provisioning</strong> — see <see cref="PostgreSqlProvisioner"/>. The feed
/// checks and fails with a message naming the gap rather than streaming nothing forever.
/// </para>
/// </remarks>
public sealed class PostgreSqlChangeFeed : IChangeFeed
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<PublishedTable> _tables;
    private readonly string _publicationName;
    private readonly string _slotName;

    /// <summary>Creates a feed over the given tables.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    /// <param name="tables">Tables to watch. At least one is required.</param>
    /// <param name="publicationName">The publication the slot streams from.</param>
    /// <param name="slotName">The replication slot to read.</param>
    /// <exception cref="ArgumentException">No connection string, or no tables.</exception>
    public PostgreSqlChangeFeed(
        string connectionString,
        IReadOnlyList<PublishedTable> tables,
        string publicationName,
        string slotName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
        {
            throw new ArgumentException(
                "At least one table is required. A publication covering nothing would stream " +
                "nothing, silently.",
                nameof(tables));
        }

        if (string.IsNullOrWhiteSpace(publicationName))
        {
            throw new ArgumentException("A publication name is required.", nameof(publicationName));
        }

        if (string.IsNullOrWhiteSpace(slotName))
        {
            throw new ArgumentException("A slot name is required.", nameof(slotName));
        }

        _connectionString = connectionString;
        _tables = tables;
        _publicationName = publicationName;
        _slotName = slotName;
    }

    /// <inheritdoc />
    public string ProviderName => "PostgreSQL";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <c>RowImages</c> is only honest because the provisioner sets <c>REPLICA IDENTITY FULL</c>
    /// on every watched table, and the feed refuses to start if one is not. With PostgreSQL's
    /// default identity an <c>UPDATE</c> carries no old row and a <c>DELETE</c> carries only
    /// the key — which would make the declaration a lie.
    /// </para>
    /// <para>
    /// <c>FiltersOwnWrites</c> is false: the WAL records every writer, including the
    /// application consuming this feed.
    /// </para>
    /// </remarks>
    public FeedCapabilities Capabilities { get; } = new(
        Detail: ChangeDetail.RowImages,
        DurableAcrossRestart: true,
        SurvivesDowntime: true,
        FiltersOwnWrites: false,
        RequiresProvisioning: true);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="from"/> carries a WAL LSN. <see cref="Checkpoint.Now"/> resumes at the
    /// slot's own confirmed position, which is what makes downtime survivable: the slot has
    /// been holding WAL while nobody was reading.
    /// </remarks>
    public async IAsyncEnumerable<ChangeBatch> ReadAsync(
        Checkpoint from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await AssertProvisionedAsync(ct).ConfigureAwait(false);

        var startAt = await ResolveStartAsync(from, ct).ConfigureAwait(false);
        var primaryKeys = await LoadPrimaryKeysAsync(ct).ConfigureAwait(false);

        await using var connection = new LogicalReplicationConnection(_connectionString);
        await connection.Open(ct).ConfigureAwait(false);

        var slot = new PgOutputReplicationSlot(_slotName);
        // binary: true is load-bearing. In pgoutput's default TEXT mode every value arrives
        // as its string representation, so an integer column reads back as "10" — which
        // satisfies every shape assertion and breaks the first consumer that does arithmetic
        // on it. Binary lets Npgsql decode through the column's type OID to a real CLR type.
        var options = new PgOutputReplicationOptions(
            _publicationName, protocolVersion: 1, binary: true);

        // Column names arrive once per relation, not per row.
        var relations = new Dictionary<uint, RelationInfo>();

        // Accumulated between Begin and Commit — one transaction, one batch.
        var pending = new List<PendingChange>();

        await foreach (var message in connection
                           .StartReplication(slot, options, ct, startAt)
                           .ConfigureAwait(false))
        {
            ChangeBatch? batch = null;

            switch (message)
            {
                case BeginMessage:
                    pending.Clear();
                    break;

                case RelationMessage relation:
                    relations[relation.RelationId] = RelationInfo.From(relation, primaryKeys);
                    break;

                case InsertMessage insert:
                    await CollectAsync(pending, relations, insert.Relation.RelationId,
                                       ChangeKind.Insert, before: null, after: insert.NewRow, ct)
                        .ConfigureAwait(false);
                    break;

                case FullUpdateMessage update:
                    await CollectAsync(pending, relations, update.Relation.RelationId,
                                       ChangeKind.Update, before: update.OldRow, after: update.NewRow, ct)
                        .ConfigureAwait(false);
                    break;

                case UpdateMessage update:
                    // DefaultUpdateMessage — no old row, because replica identity is not FULL.
                    // AssertProvisionedAsync should have prevented this, but a table altered
                    // while the feed runs would land here.
                    await CollectAsync(pending, relations, update.Relation.RelationId,
                                       ChangeKind.Update, before: null, after: update.NewRow, ct)
                        .ConfigureAwait(false);
                    break;

                case FullDeleteMessage delete:
                    await CollectAsync(pending, relations, delete.Relation.RelationId,
                                       ChangeKind.Delete, before: delete.OldRow, after: null, ct)
                        .ConfigureAwait(false);
                    break;

                case KeyDeleteMessage delete:
                    await CollectAsync(pending, relations, delete.Relation.RelationId,
                                       ChangeKind.Delete, before: delete.Key, after: null, ct)
                        .ConfigureAwait(false);
                    break;

                case CommitMessage commit:
                    // TransactionEndLsn, not CommitLsn. Checkpoint.Position means "the
                    // position that FOLLOWS these changes"; starting a later feed at the
                    // commit record's own LSN re-delivers the transaction it belongs to.
                    batch = Drain(pending, commit.TransactionEndLsn);
                    break;

                default:
                    // Keepalives, truncates, origin and type messages. Nothing to report — and
                    // emitting an empty batch here would break the conformance suite's
                    // "reports nothing while idle" contract.
                    break;
            }

            // Acknowledge before yielding. The server keeps every WAL segment this slot has
            // not confirmed, so a feed that never acknowledges eventually fills the disk.
            connection.SetReplicationStatus(message.WalEnd);

            if (batch is not null)
            {
                yield return batch;
            }
        }
    }

    private async Task AssertProvisionedAsync(CancellationToken ct)
    {
        // A regular connection: LogicalReplicationConnection speaks the replication protocol
        // and cannot run ordinary queries.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var provisioner = new PostgreSqlProvisioner(
            _connectionString, _tables, _publicationName, _slotName);

        var missing = await provisioner.DescribeMissingAsync(connection, ct).ConfigureAwait(false);

        if (missing is not null)
        {
            throw new ProvisioningRequiredException(
                $"{missing} Fix it with PostgreSqlProvisioner.EnsureAsync(), or run the script " +
                "from GetScript() as an administrator.");
        }
    }

    /// <summary>Turns a <see cref="Checkpoint"/> into the WAL position to start at.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Checkpoint.Beginning"/> maps to <c>null</c>, which tells Npgsql to resume at
    /// the slot's confirmed position — everything the slot has been retaining since it was
    /// created. That is this provider's "as far back as I can go".
    /// </para>
    /// <para>
    /// <strong><see cref="Checkpoint.Now"/> must NOT map to null.</strong> The slot is a
    /// backlog, so resuming at its confirmed position replays every change since the last
    /// acknowledged read — which for a caller asking for "now" is history it did not ask for
    /// and, on a slot left behind by an earlier run, can be arbitrarily much of it. Reading the
    /// server's current LSN is what makes <c>Now</c> mean now.
    /// </para>
    /// </remarks>
    private async Task<NpgsqlLogSequenceNumber?> ResolveStartAsync(Checkpoint from, CancellationToken ct)
    {
        if (from.IsBeginning)
        {
            return null;
        }

        if (from.IsNow)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_current_wal_lsn();";

            var current = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return (NpgsqlLogSequenceNumber)current!;
        }

        try
        {
            return NpgsqlLogSequenceNumber.Parse(from.Value);
        }
        catch (FormatException ex)
        {
            throw new DbSignalException(
                $"'{from.Value}' is not a PostgreSQL WAL LSN. " +
                "Checkpoints are provider-specific and cannot be shared between providers.",
                ex);
        }
    }

    /// <summary>
    /// Reads a tuple's values into the pending list. <strong>This must happen before the next
    /// message is read.</strong>
    /// </summary>
    /// <remarks>
    /// Column values are streamed, not buffered: advance the enumerator and the previous
    /// message's data is gone. Collecting eagerly here, rather than holding the message and
    /// reading later, is what makes the whole feed correct.
    /// </remarks>
    private static async Task CollectAsync(
        List<PendingChange> pending,
        IReadOnlyDictionary<uint, RelationInfo> relations,
        uint relationId,
        ChangeKind kind,
        ReplicationTuple? before,
        ReplicationTuple? after,
        CancellationToken ct)
    {
        if (!relations.TryGetValue(relationId, out var relation))
        {
            // The server sends a relation message before any row for it; a row without one
            // means a table we have no schema for, which we cannot describe.
            return;
        }

        var beforeValues = before is null
            ? null
            : await ReadRowAsync(relation, before, ct).ConfigureAwait(false);

        var afterValues = after is null
            ? null
            : await ReadRowAsync(relation, after, ct).ConfigureAwait(false);

        var keyValues = relation.KeyColumns.Count == 0
            ? Array.Empty<object?>()
            : relation.KeyColumns
                      .Select(column => (afterValues ?? beforeValues)?.GetValueOrDefault(column))
                      .ToArray();

        pending.Add(new PendingChange(
            relation,
            new ChangeKey(keyValues, kind),
            new RowImage(kind, beforeValues, afterValues)));
    }

    private static async Task<Dictionary<string, object?>> ReadRowAsync(
        RelationInfo relation, ReplicationTuple tuple, CancellationToken ct)
    {
        // Ordinal comparer: PostgreSQL folds unquoted identifiers to lower case, so the names
        // arriving here are already canonical. Case-insensitive would silently merge a quoted
        // "Name" with an unquoted name.
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var index = 0;

        await foreach (var value in tuple.ConfigureAwait(false))
        {
            var column = index < relation.ColumnNames.Count
                ? relation.ColumnNames[index]
                : index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // The non-generic Get is the one that maps through the column's type OID.
            // Get<object>() hands back the raw text pgoutput sent, so an integer column
            // arrives as "10" — passes every shape assertion, and quietly breaks the first
            // consumer that does arithmetic on it.
            values[column] = value.IsDBNull
                ? null
                : await value.Get(ct).ConfigureAwait(false);

            index++;
        }

        return values;
    }

    private static ChangeBatch? Drain(List<PendingChange> pending, NpgsqlLogSequenceNumber nextLsn)
    {
        if (pending.Count == 0)
        {
            return null;
        }

        var tables = pending
            .GroupBy(change => change.Relation)
            .Select(group => new TableChange(
                group.Key.Schema,
                group.Key.Name,
                group.Select(change => change.Key).ToArray(),
                group.Select(change => change.Row).ToArray()))
            .ToArray();

        pending.Clear();

        return new ChangeBatch(
            new Checkpoint(nextLsn.ToString()),
            tables,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release: the replication connection is owned by the enumeration, so it is
    /// disposed when the caller stops iterating. Unlike the polling providers this is a
    /// deliberate choice rather than a trivial one — holding the connection at instance level
    /// would make this method load-bearing.
    /// </remarks>
    public ValueTask DisposeAsync() => default;

    /// <summary>
    /// Reads the real primary-key columns of every watched table, keyed by
    /// <c>schema.table</c>.
    /// </summary>
    /// <remarks>
    /// <strong>pgoutput's own key flags cannot be used here.</strong> Under
    /// <c>REPLICA IDENTITY FULL</c> — which this provider requires, because it is what makes
    /// before-images available — the server marks <em>every</em> column as part of the replica
    /// identity. Trusting that flag would put all of a row's columns into
    /// <see cref="ChangeKey.Values"/>, so a consumer written against a
    /// <see cref="ChangeDetail.KeysChanged"/> provider would read <c>Values[0]</c> and get the
    /// right answer by luck on narrow tables and the wrong one everywhere else.
    /// </remarks>
    private async Task<Dictionary<string, IReadOnlyList<string>>> LoadPrimaryKeysAsync(CancellationToken ct)
    {
        var keys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        foreach (var table in _tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT a.attname
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(i.indkey)
                WHERE i.indisprimary AND n.nspname = @schema AND c.relname = @name
                ORDER BY array_position(i.indkey, a.attnum);
                """;
            _ = command.Parameters.AddWithValue("@schema", table.Schema);
            _ = command.Parameters.AddWithValue("@name", table.Name);

            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }

            if (columns.Count > 0)
            {
                keys[table.QualifiedName] = columns;
            }
        }

        return keys;
    }

    private sealed record RelationInfo(
        string Schema, string Name, IReadOnlyList<string> ColumnNames, IReadOnlyList<string> KeyColumns)
    {
        public static RelationInfo From(
            RelationMessage message, Dictionary<string, IReadOnlyList<string>> primaryKeys)
        {
            var columns = message.Columns.Select(c => c.ColumnName).ToArray();
            var qualified = $"{message.Namespace}.{message.RelationName}";

            // The real primary key where the table has one. Falling back to the replica
            // identity flags covers a table without a PK, where the identity IS the key the
            // server would use.
            var keys = primaryKeys.TryGetValue(qualified, out var declared)
                ? declared
                : message.Columns
                         .Where(c => c.Flags.HasFlag(RelationMessage.Column.ColumnFlags.PartOfKey))
                         .Select(c => c.ColumnName)
                         .ToArray();

            return new RelationInfo(message.Namespace, message.RelationName, columns, keys);
        }
    }

    private sealed record PendingChange(RelationInfo Relation, ChangeKey Key, RowImage Row);
}
