using System.Runtime.CompilerServices;

namespace DbSignal.Hosting.Tests;

/// <summary>
/// A feed whose behaviour each test dictates. Deliberately not a mocking framework: the
/// interesting cases here are ordering and failure, which read better as real async
/// iterators than as configured expectations.
/// </summary>
internal sealed class FakeFeed : IChangeFeed
{
    private readonly Func<Checkpoint, CancellationToken, IAsyncEnumerable<ChangeBatch>> _read;

    public FakeFeed(
        FeedCapabilities capabilities,
        Func<Checkpoint, CancellationToken, IAsyncEnumerable<ChangeBatch>> read)
    {
        Capabilities = capabilities;
        _read = read;
    }

    public FeedCapabilities Capabilities { get; }

    public string ProviderName => "Fake";

    /// <summary>Records what the hosted service asked to resume from.</summary>
    public Checkpoint? RequestedStart { get; private set; }

    public IAsyncEnumerable<ChangeBatch> ReadAsync(Checkpoint from, CancellationToken ct = default)
    {
        RequestedStart = from;
        return _read(from, ct);
    }

    public ValueTask DisposeAsync() => default;

    public static FeedCapabilities Caps(ChangeDetail detail, bool durable = true) =>
        new(detail, DurableAcrossRestart: durable, SurvivesDowntime: false,
            FiltersOwnWrites: false, RequiresProvisioning: false);

    public static ChangeBatch Batch(string position) =>
        new(new Checkpoint(position), Array.Empty<TableChange>(), DateTimeOffset.UtcNow);

    /// <summary>Yields the given batches, then completes.</summary>
    public static async IAsyncEnumerable<ChangeBatch> Yielding(
        params ChangeBatch[] batches)
    {
        foreach (var batch in batches)
        {
            yield return batch;
            await Task.Yield();
        }
    }

    /// <summary>Never produces anything; parks until cancelled, like a quiet database.</summary>
    public static async IAsyncEnumerable<ChangeBatch> Idle(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }
}
