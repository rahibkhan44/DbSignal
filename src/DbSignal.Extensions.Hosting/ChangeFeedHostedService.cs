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
internal sealed class ChangeFeedHostedService : BackgroundService
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

        _logger.LogInformation(
            "DbSignal watching via {Provider} ({Detail}, durable: {Durable})",
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
                _logger.LogWarning(ex,
                    "DbSignal fell outside the change-retention window and cannot enumerate what it missed. " +
                    "Resuming from now; reload any cached data in full.");

                await _checkpoints.SaveAsync(_options.CheckpointKey, Checkpoint.Now, CancellationToken.None)
                                  .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DbSignal feed faulted; retrying in {Delay}", delay);
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
            var handled = await DispatchAsync(batch, ct).ConfigureAwait(false);

            if (!handled && _options.RetryFailedBatches)
            {
                // Leave the checkpoint where it was so the next pass sees this batch again.
                continue;
            }

            if (_feed.Capabilities.DurableAcrossRestart)
            {
                await _checkpoints.SaveAsync(_options.CheckpointKey, batch.Position, ct)
                                  .ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> DispatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IChangeHandler>().ToList();

        if (handlers.Count == 0)
        {
            _logger.LogWarning(
                "DbSignal detected a change but no IChangeHandler is registered — nothing will react. " +
                "Register one with AddHandler<T>().");
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
                _logger.LogError(ex,
                    "Handler {Handler} threw while processing {Batch}",
                    handler.GetType().Name, batch);
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }
}
