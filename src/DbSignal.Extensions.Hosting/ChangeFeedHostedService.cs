using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbSignal.Hosting;

/// <summary>
/// Runs the feed for the lifetime of the application: reads batches, dispatches them to
/// every registered handler, persists the checkpoint, and recovers from faults.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint is saved <strong>after</strong> handlers succeed, never before. That
/// ordering is what makes the at-least-once promise true: a crash mid-handler replays the
/// batch rather than skipping it. The reverse ordering would turn every crash into silent
/// data loss, which is the failure a change-notification library least gets forgiven for.
/// </para>
/// <para>
/// Handlers are resolved from a fresh scope per batch, so scoped services — a DbContext, a
/// unit of work — behave as they would in a web request.
/// </para>
/// </remarks>
internal sealed partial class ChangeFeedHostedService : BackgroundService
{
    private readonly IChangeFeed _feed;
    private readonly DbSignalOptions _options;
    private readonly ICheckpointStore _checkpoints;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChangeFeedHostedService> _logger;

    public ChangeFeedHostedService(
        IChangeFeed feed,
        DbSignalOptions options,
        ICheckpointStore checkpoints,
        IServiceScopeFactory scopeFactory,
        ILogger<ChangeFeedHostedService> logger)
    {
        _feed = feed;
        _options = options;
        _checkpoints = checkpoints;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fail fast and loudly. A capability mismatch is a wiring mistake, and discovering
        // it at startup costs a restart; discovering it in production costs trust.
        if (_options.RequiredDetail is { } required)
        {
            _feed.Capabilities.Require(required, _feed.ProviderName);
        }

        LogWatching(
            _feed.ProviderName, _feed.Capabilities.Detail, _feed.Capabilities.DurableAcrossRestart);

        var delay = _options.InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);

                // A clean return means cancellation, not a fault.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ResyncRequiredException ex)
            {
                // The gap is unreadable, so there is nothing to retry into. Say so at
                // warning level — consumers holding a cache need to reload it — and
                // restart from the present rather than silently delivering nothing.
                LogResyncRequired(ex);

                await _checkpoints.SaveAsync(_options.CheckpointKey, Checkpoint.Now, CancellationToken.None)
                                  .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFaulted(ex, delay);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Exponential backoff, capped. Several workstations reconnecting to a database
            // that just came back should not arrive as a thundering herd.
            delay = delay >= _options.MaxRetryDelay
                ? _options.MaxRetryDelay
                : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var stored = await _checkpoints.LoadAsync(_options.CheckpointKey, ct).ConfigureAwait(false);

        // A stored checkpoint is only meaningful if the provider can resume into it.
        var from = stored is { } saved && _feed.Capabilities.DurableAcrossRestart
            ? saved
            : _options.StartAt;

        await foreach (var batch in _feed.ReadAsync(from, ct).ConfigureAwait(false))
        {
            await DeliverAsync(batch, ct).ConfigureAwait(false);

            if (_feed.Capabilities.DurableAcrossRestart)
            {
                await _checkpoints.SaveAsync(_options.CheckpointKey, batch.Position, ct)
                                  .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Hands a batch to the handlers, redelivering the same batch until it succeeds when
    /// <see cref="DbSignalOptions.RetryFailedBatches"/> is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrying has to redeliver <em>this</em> object rather than wait for the feed to
    /// offer the position again. A provider advances its own cursor as it yields, so the
    /// next batch off the stream covers only what happened after this one. Skipping ahead
    /// and then saving that later position would step the checkpoint over changes no
    /// handler ever accepted, which is precisely the silent loss at-least-once exists to
    /// prevent.
    /// </para>
    /// <para>
    /// The loop is unbounded on purpose. A batch that never succeeds blocks the stream,
    /// which is the documented cost of <c>RetryFailedBatches = true</c>, and the option
    /// exists to buy out of it. Backoff keeps a persistently failing handler from spinning.
    /// </para>
    /// </remarks>
    private async Task DeliverAsync(ChangeBatch batch, CancellationToken ct)
    {
        var delay = _options.InitialRetryDelay;

        while (true)
        {
            if (await DispatchAsync(batch, ct).ConfigureAwait(false) || !_options.RetryFailedBatches)
            {
                return;
            }

            LogRetryingBatch(batch.Position, delay);

            await Task.Delay(delay, ct).ConfigureAwait(false);

            delay = delay >= _options.MaxRetryDelay
                ? _options.MaxRetryDelay
                : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    private async Task<bool> DispatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IChangeHandler>().ToList();

        if (handlers.Count == 0)
        {
            LogNoHandlers();
            return true;
        }

        var allSucceeded = true;

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(batch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad handler must not stop the others from seeing the batch.
                LogHandlerFaulted(ex, handler.GetType().Name, batch.Position);
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    // Source-generated log delegates. The analyzer insists (CA1848), and it is right to:
    // these are on the hot path of a loop that runs for the process lifetime, and the
    // generated code skips formatting entirely when the level is disabled.

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "DbSignal watching via {provider} ({detail}, durable across restart: {durable})")]
    private partial void LogWatching(string provider, ChangeDetail detail, bool durable);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DbSignal fell outside the change-retention window and cannot enumerate what it missed. " +
                  "Resuming from now; reload any cached data in full.")]
    private partial void LogResyncRequired(Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "DbSignal feed faulted; retrying in {delay}")]
    private partial void LogFaulted(Exception ex, TimeSpan delay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DbSignal detected a change but no IChangeHandler is registered — nothing will react. " +
                  "Register one with AddHandler<T>().")]
    private partial void LogNoHandlers();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Handler {handler} threw while processing the batch at {position}")]
    private partial void LogHandlerFaulted(Exception ex, string handler, Checkpoint position);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DbSignal is redelivering the batch at {position} in {delay}; the checkpoint " +
                  "stays put until a handler accepts it, so the stream is held here")]
    private partial void LogRetryingBatch(Checkpoint position, TimeSpan delay);
}
