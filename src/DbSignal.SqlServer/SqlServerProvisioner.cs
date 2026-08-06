using System.Text;
using Microsoft.Data.SqlClient;

namespace DbSignal.SqlServer;

/// <summary>
/// Turns Change Tracking on, or hands you the script to give a DBA.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the feed, and never invoked implicitly. Enabling Change Tracking is DDL:
/// it needs <c>ALTER</c> on the database and on each table, and plenty of production
/// databases will not grant that to an application login. A library that silently issues
/// <c>ALTER DATABASE</c> is a library that gets banned from a customer's server.
/// </para>
/// <para>
/// Every statement is guarded, so running it twice is safe and running it against an
/// already-configured database does nothing.
/// </para>
/// </remarks>
public sealed class SqlServerProvisioner
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<WatchedTable> _tables;
    private readonly int _retentionDays;

    /// <summary>Creates a provisioner.</summary>
    /// <param name="connectionString">Connection string for the database to configure.</param>
    /// <param name="tables">Tables to enable Change Tracking on.</param>
    /// <param name="retentionDays">
    /// How long SQL Server keeps change history. A consumer offline longer than this cannot
    /// resume and must do a full reload — see <see cref="ResyncRequiredException"/>. Two days
    /// covers a weekend outage.
    /// </param>
    public SqlServerProvisioner(
        string connectionString,
        IReadOnlyList<WatchedTable> tables,
        int retentionDays = 2)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _retentionDays = retentionDays;
    }

    /// <summary>
    /// The exact SQL <see cref="EnsureAsync"/> would run. Print it, review it, or hand it to
    /// whoever owns the server.
    /// </summary>
    public string GetScript()
    {
        var sql = new StringBuilder();

        sql.AppendLine("-- DbSignal: enable Change Tracking. Safe to run repeatedly.")
           .AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())")
           .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                       $"    ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = {_retentionDays} DAYS, AUTO_CLEANUP = ON);")
           .AppendLine();

        foreach (var table in _tables)
        {
            sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                           $"IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(N'{table.QualifiedName.Replace("'", "''", StringComparison.Ordinal)}'))")
               .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                           $"    ALTER TABLE {table.QuotedName} ENABLE CHANGE_TRACKING;");
        }

        return sql.ToString();
    }

    /// <summary>True when the database and every watched table are already configured.</summary>
    /// <param name="ct">Cancels the check.</param>
    public async Task<bool> IsProvisionedAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!await IsDatabaseEnabledAsync(connection, ct).ConfigureAwait(false))
        {
            return false;
        }

        foreach (var table in _tables)
        {
            if (!await IsTableEnabledAsync(connection, table, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the script. Idempotent — safe on an already-configured database.
    /// </summary>
    /// <param name="ct">Cancels the work.</param>
    /// <exception cref="DbSignalException">
    /// The login lacks <c>ALTER</c> rights. The message carries the script so an
    /// administrator can run it by hand.
    /// </exception>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        try
        {
            // Statement by statement, not as one batch: a partial success is still
            // progress, and a failure names the statement that could not run.
            if (!await IsDatabaseEnabledAsync(connection, ct).ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    $"ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = {_retentionDays} DAYS, AUTO_CLEANUP = ON);",
                    ct).ConfigureAwait(false);
            }

            foreach (var table in _tables)
            {
                if (!await IsTableEnabledAsync(connection, table, ct).ConfigureAwait(false))
                {
                    await ExecuteAsync(
                        connection,
                        $"ALTER TABLE {table.QuotedName} ENABLE CHANGE_TRACKING;",
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (SqlException ex)
        {
            throw new DbSignalException(
                "Could not enable Change Tracking. This needs ALTER permission on the database " +
                "and on each watched table. Run the script from GetScript() as an administrator " +
                $"instead.{Environment.NewLine}{Environment.NewLine}{GetScript()}",
                ex);
        }
    }

    internal static async Task<bool> IsDatabaseEnabledAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.change_tracking_databases WHERE database_id = DB_ID();";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    internal static async Task<bool> IsTableEnabledAsync(
        SqlConnection connection, WatchedTable table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(@table);";
        command.Parameters.AddWithValue("@table", table.QualifiedName);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
