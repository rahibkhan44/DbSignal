using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace DbSignal.Sqlite;

/// <summary>
/// Detects changes to a SQLite database made by <em>other connections</em>, including
/// connections in other processes, using <c>PRAGMA data_version</c>.
/// </summary>
/// <remarks>
/// <para>
/// SQLite's own documentation describes this exact use case: <em>"Interactive programs
/// that hold database content in memory or that display database content on-screen can
/// use the PRAGMA data_version command to determine if they need to flush and reload
/// their memory or update the screen display."</em>
/// </para>
/// <para>
/// <strong>Why not <c>sqlite3_update_hook</c>?</strong> It only fires for changes made on
/// the connection it was registered on — not other threads, and certainly not other
/// processes. It cannot see the writer you actually care about. (It is also unavailable
/// in <c>Microsoft.Data.Sqlite</c>; see dotnet/efcore#13827, open since 2017.)
/// </para>
/// <para>
/// <strong>Granularity is the whole database.</strong> <c>data_version</c> is one number
/// for the file, so this feed reports <see cref="ChangeDetail.DatabaseChanged"/> and can
/// never name a table. Any table list passed to it is ignored — deliberately, and loudly
/// documented, rather than accepted and quietly disregarded.
/// </para>
/// </remarks>
public sealed class SqliteChangeFeed : IChangeFeed
{
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates a feed over a SQLite database.</summary>
    /// <param name="connectionString">Connection string for the database to watch.</param>
    /// <param name="pollInterval">
    /// How often to check. The check is a single integer read, so short intervals are
    /// cheap; 250ms feels instant to a person watching a screen.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pollInterval"/> is not positive.</exception>
    public SqliteChangeFeed(string connectionString, TimeSpan? pollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Poll interval must be positive.");
        }

        _connectionString = connectionString;
        _pollInterval = interval;
    }

    /// <inheritdoc />
    public string ProviderName => "SQLite";

    /// <inheritdoc />
    /// <remarks>
    /// <para><c>FiltersOwnWrites</c> is <see langword="false"/>, and that deserves explaining,
    /// because the underlying pragma might suggest otherwise.</para>
    /// <para>
    /// <c>data_version</c> ignores commits made on the connection that reads it. This feed's
    /// connection only ever reads, so that exclusion protects nothing: your application writes
    /// on its <em>own</em> separate connection, and those writes <strong>will</strong> be
    /// reported. Declaring <see langword="true"/> here would be technically defensible and
    /// practically a lie — the flag answers "will I hear about my own writes?", and for
    /// SQLite the answer is yes.
    /// </para>
    /// </remarks>
    public FeedCapabilities Capabilities { get; } = new(
        Detail: ChangeDetail.DatabaseChanged,
        DurableAcrossRestart: false,
        SurvivesDowntime: false,
        FiltersOwnWrites: false,
        RequiresProvisioning: false);

    /// <inheritdoc />
    /// <remarks>
    /// The <paramref name="from"/> checkpoint is <strong>ignored</strong>. SQLite keeps no
    /// change history, so there is nothing to resume into — which is exactly what
    /// <c>DurableAcrossRestart: false</c> advertises. The first poll establishes a baseline
    /// and reports nothing; every later poll compares against it.
    /// <para>
    /// Cancellation ends the stream cleanly rather than throwing, so a hosted loop can shut
    /// down without a try/catch around the enumeration.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ChangeBatch> ReadAsync(
        Checkpoint from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One long-lived connection, deliberately. `PRAGMA data_version` is only
        // comparable against a previous reading FROM THE SAME CONNECTION — SQLite's
        // docs are explicit about that. Opening a fresh connection per poll would be
        // comparing two unrelated counters.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        long lastSeen = await ReadDataVersionAsync(connection, ct).ConfigureAwait(false);

        while (true)
        {
            long current;
            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                current = await ReadDataVersionAsync(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (current == lastSeen)
            {
                continue;
            }

            lastSeen = current;

            // No table list: at DatabaseChanged the honest answer is "something moved".
            yield return new ChangeBatch(
                new Checkpoint(current.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Array.Empty<TableChange>(),
                DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Reads the current value. Serving this pragma starts an implicit read transaction,
    /// which is what refreshes the connection's view of the file header — so an otherwise
    /// idle connection still observes other writers.
    /// </summary>
    private static async Task<long> ReadDataVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;
}
