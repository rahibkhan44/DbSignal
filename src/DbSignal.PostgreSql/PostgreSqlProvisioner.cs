using System.Text;
using Npgsql;

namespace DbSignal.PostgreSql;

/// <summary>
/// Sets up logical replication, or hands you the script to give a DBA.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the feed, and never invoked implicitly. Provisioning creates a publication,
/// alters replica identity and creates a replication slot — all of which need elevated
/// rights, and plenty of production databases will not grant them to an application login.
/// </para>
/// <para>
/// One requirement this provisioner <strong>cannot</strong> satisfy: <c>wal_level = logical</c>
/// is a server-wide setting that needs a restart. Unlike SQL Server's
/// <c>ALTER DATABASE … SET CHANGE_TRACKING</c>, there is no runtime fix, so it is reported as
/// a restart instruction rather than retried.
/// </para>
/// </remarks>
public sealed class PostgreSqlProvisioner
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<PublishedTable> _tables;
    private readonly string _publicationName;
    private readonly string _slotName;

    /// <summary>Creates a provisioner.</summary>
    /// <param name="connectionString">Connection string for the database to configure.</param>
    /// <param name="tables">Tables to publish.</param>
    /// <param name="publicationName">Publication to create; must match the feed's.</param>
    /// <param name="slotName">Replication slot to create; must match the feed's.</param>
    public PostgreSqlProvisioner(
        string connectionString,
        IReadOnlyList<PublishedTable> tables,
        string publicationName,
        string slotName)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _publicationName = publicationName ?? throw new ArgumentNullException(nameof(publicationName));
        _slotName = slotName ?? throw new ArgumentNullException(nameof(slotName));
    }

    /// <summary>
    /// The exact SQL <see cref="EnsureAsync"/> would run. Print it, review it, or hand it to
    /// whoever owns the server.
    /// </summary>
    public string GetScript()
    {
        var sql = new StringBuilder();

        _ = sql.AppendLine("-- DbSignal: enable logical replication. Safe to run repeatedly.")
               .AppendLine("-- Requires wal_level = logical, which needs a SERVER RESTART:")
               .AppendLine("--   ALTER SYSTEM SET wal_level = logical;   then restart PostgreSQL")
               .AppendLine();

        _ = sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                           $"CREATE PUBLICATION \"{Escape(_publicationName)}\" FOR TABLE")
               .AppendLine("    " + string.Join(", ", _tables.Select(t => t.QuotedName)) + ";")
               .AppendLine();

        foreach (var table in _tables)
        {
            // FULL is what makes before/after row images possible. Without it an UPDATE
            // carries no old row and a DELETE carries only the key.
            _ = sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                               $"ALTER TABLE {table.QuotedName} REPLICA IDENTITY FULL;");
        }

        _ = sql.AppendLine()
               .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                           $"SELECT pg_create_logical_replication_slot('{_slotName.Replace("'", "''", StringComparison.Ordinal)}', 'pgoutput');");

        return sql.ToString();
    }

    /// <summary>True when every prerequisite is already in place.</summary>
    /// <param name="ct">Cancels the check.</param>
    public async Task<bool> IsProvisionedAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        return await DescribeMissingAsync(connection, ct).ConfigureAwait(false) is null;
    }

    /// <summary>
    /// Applies the script. Idempotent — safe against an already-configured database.
    /// </summary>
    /// <param name="ct">Cancels the work.</param>
    /// <exception cref="ProvisioningRequiredException">
    /// <c>wal_level</c> is not <c>logical</c>. No amount of SQL fixes that; the server needs a
    /// restart.
    /// </exception>
    /// <exception cref="DbSignalException">
    /// The login lacks the rights. The message carries the script so an administrator can run
    /// it by hand.
    /// </exception>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Checked first and separately: everything below is pointless without it, and it is
        // the one thing EnsureAsync genuinely cannot repair.
        if (!await IsWalLevelLogicalAsync(connection, ct).ConfigureAwait(false))
        {
            throw new ProvisioningRequiredException(
                "PostgreSQL is not configured for logical replication (wal_level is not 'logical'). " +
                "This cannot be enabled at runtime. Run:" + Environment.NewLine +
                "    ALTER SYSTEM SET wal_level = logical;" + Environment.NewLine +
                "then RESTART the server, and try again.");
        }

        try
        {
            // Statement by statement, not one batch: a partial success is still progress, and
            // a failure names the statement that could not run.
            if (!await PublicationCoversTablesAsync(connection, ct).ConfigureAwait(false))
            {
                await DropPublicationIfExistsAsync(connection, ct).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    $"CREATE PUBLICATION \"{Escape(_publicationName)}\" FOR TABLE " +
                    string.Join(", ", _tables.Select(t => t.QuotedName)) + ";",
                    ct).ConfigureAwait(false);
            }

            foreach (var table in _tables)
            {
                if (!await IsReplicaIdentityFullAsync(connection, table, ct).ConfigureAwait(false))
                {
                    await ExecuteAsync(
                        connection, $"ALTER TABLE {table.QuotedName} REPLICA IDENTITY FULL;", ct)
                        .ConfigureAwait(false);
                }
            }

            if (!await SlotExistsAsync(connection, ct).ConfigureAwait(false))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_create_logical_replication_slot(@slot, 'pgoutput');";
                _ = command.Parameters.AddWithValue("slot", _slotName);
                _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
        }
        catch (PostgresException ex)
        {
            throw new DbSignalException(
                "Could not enable logical replication. This needs rights to create a publication, " +
                "alter the watched tables, and create a replication slot (the REPLICATION attribute). " +
                $"Run the script from GetScript() as an administrator instead.{Environment.NewLine}" +
                $"{Environment.NewLine}{GetScript()}",
                ex);
        }
    }

    /// <summary>
    /// Drops the replication slot. <strong>Call this when you are finished with a feed for
    /// good.</strong>
    /// </summary>
    /// <remarks>
    /// An abandoned slot is the most dangerous thing about logical replication: PostgreSQL
    /// retains every WAL segment the slot has not consumed, forever, and will eventually fill
    /// the disk and stop accepting writes. A slot nobody reads is not free.
    /// </remarks>
    /// <param name="ct">Cancels the work.</param>
    public async Task DropSlotAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!await SlotExistsAsync(connection, ct).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_drop_replication_slot(@slot);";
        _ = command.Parameters.AddWithValue("slot", _slotName);
        _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Names the first missing prerequisite, or null when everything is in place. Shared by
    /// the provisioner and the feed so both report the same thing.
    /// </summary>
    internal async Task<string?> DescribeMissingAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (!await IsWalLevelLogicalAsync(connection, ct).ConfigureAwait(false))
        {
            return "PostgreSQL is not configured for logical replication (wal_level is not 'logical'). " +
                   "Run 'ALTER SYSTEM SET wal_level = logical;' and RESTART the server — this cannot " +
                   "be enabled at runtime.";
        }

        if (!await PublicationCoversTablesAsync(connection, ct).ConfigureAwait(false))
        {
            return $"Publication '{_publicationName}' is missing or does not cover every watched table " +
                   $"({string.Join(", ", _tables.Select(t => t.QualifiedName))}).";
        }

        foreach (var table in _tables)
        {
            if (!await IsReplicaIdentityFullAsync(connection, table, ct).ConfigureAwait(false))
            {
                return $"Table '{table.QualifiedName}' is not REPLICA IDENTITY FULL, so PostgreSQL will " +
                       "not write before-images to the WAL. This feed declares ChangeDetail.RowImages " +
                       "and cannot honour it without them.";
            }
        }

        if (!await SlotExistsAsync(connection, ct).ConfigureAwait(false))
        {
            return $"Replication slot '{_slotName}' does not exist.";
        }

        return null;
    }

    private static async Task<bool> IsWalLevelLogicalAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW wal_level;";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return string.Equals(value, "logical", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> PublicationCoversTablesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pg_publication_tables " +
            "WHERE pubname = @pub AND schemaname = @schema AND tablename = @table;";

        _ = command.Parameters.AddWithValue("pub", _publicationName);
        var schema = command.Parameters.Add("schema", NpgsqlTypes.NpgsqlDbType.Text);
        var table = command.Parameters.Add("table", NpgsqlTypes.NpgsqlDbType.Text);

        foreach (var watched in _tables)
        {
            schema.Value = watched.Schema;
            table.Value = watched.Name;

            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            if (count == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> IsReplicaIdentityFullAsync(
        NpgsqlConnection connection, PublishedTable table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // 'f' = FULL. 'd' = default (primary key only), 'n' = nothing, 'i' = index.
        command.CommandText =
            "SELECT c.relreplident = 'f' FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @schema AND c.relname = @table;";
        _ = command.Parameters.AddWithValue("schema", table.Schema);
        _ = command.Parameters.AddWithValue("table", table.Name);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is bool isFull && isFull;
    }

    private async Task<bool> SlotExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_replication_slots WHERE slot_name = @slot;";
        _ = command.Parameters.AddWithValue("slot", _slotName);

        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        return count > 0;
    }

    private async Task DropPublicationIfExistsAsync(NpgsqlConnection connection, CancellationToken ct) =>
        await ExecuteAsync(connection, $"DROP PUBLICATION IF EXISTS \"{Escape(_publicationName)}\";", ct)
            .ConfigureAwait(false);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Escape(string identifier) =>
        identifier.Replace("\"", "\"\"", StringComparison.Ordinal);
}
