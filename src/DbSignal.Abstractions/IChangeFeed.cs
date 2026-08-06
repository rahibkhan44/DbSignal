namespace DbSignal;

/// <summary>
/// A stream of database changes, however the underlying engine detects them.
/// </summary>
/// <remarks>
/// <para>
/// The one contract every provider implements. Implementations poll (SQL Server, SQLite)
/// or hold a streaming connection (PostgreSQL, MySQL); consumers cannot tell the
/// difference, and should not need to.
/// </para>
/// <para>
/// <strong>Delivery is at-least-once.</strong> A crash between handling a batch and
/// persisting its checkpoint replays that batch. Handlers must be idempotent.
/// Exactly-once is not achievable across these mechanisms, and claiming it would be a lie.
/// </para>
/// <para>
/// What a feed can actually tell you varies by engine — always check
/// <see cref="Capabilities"/> rather than assuming.
/// </para>
/// </remarks>
public interface IChangeFeed : IAsyncDisposable
{
    /// <summary>What this feed can honestly report. Fixed for the lifetime of the instance.</summary>
    FeedCapabilities Capabilities { get; }

    /// <summary>A short name for logs and error messages, e.g. <c>"SQLite"</c>.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Streams batches until cancelled. Yields nothing while the database is quiet, so
    /// the loop parks rather than spinning.
    /// </summary>
    /// <param name="from">
    /// Where to resume. Use <see cref="Checkpoint.Now"/> for "only changes from here on" —
    /// the right default for a cache or a screen.
    /// </param>
    /// <param name="ct">Stops the stream.</param>
    /// <returns>Batches, in order. Each carries the checkpoint that follows it.</returns>
    /// <exception cref="ResyncRequiredException">
    /// <paramref name="from"/> is older than the history the database still holds. Reload
    /// everything and restart from <see cref="Checkpoint.Now"/>.
    /// </exception>
    /// <exception cref="ProvisioningRequiredException">The database is missing one-time setup.</exception>
    IAsyncEnumerable<ChangeBatch> ReadAsync(Checkpoint from, CancellationToken ct = default);
}

/// <summary>
/// Where checkpoints are kept between runs. Only worth implementing for feeds that
/// report <see cref="FeedCapabilities.DurableAcrossRestart"/>.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>Reads the stored checkpoint, or null when there is none yet.</summary>
    /// <param name="key">Identifies the feed, so several can share one store.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<Checkpoint?> LoadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Stores a checkpoint. Call only <em>after</em> the batch has been handled.
    /// </summary>
    /// <param name="key">Identifies the feed.</param>
    /// <param name="checkpoint">The position to resume from next time.</param>
    /// <param name="ct">Cancels the write.</param>
    Task SaveAsync(string key, Checkpoint checkpoint, CancellationToken ct = default);
}

/// <summary>
/// Handles a batch of changes. Registered with the hosting extensions; several may run
/// for one feed.
/// </summary>
public interface IChangeHandler
{
    /// <summary>
    /// Reacts to a batch. <strong>Must be idempotent</strong> — delivery is at-least-once,
    /// so this can be called twice for the same batch.
    /// </summary>
    /// <param name="batch">What changed.</param>
    /// <param name="ct">Cancels the work.</param>
    Task HandleAsync(ChangeBatch batch, CancellationToken ct = default);
}
