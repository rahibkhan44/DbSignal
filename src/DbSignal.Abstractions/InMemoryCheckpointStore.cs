using System.Collections.Concurrent;

namespace DbSignal;

/// <summary>
/// Keeps checkpoints in memory only. The right choice for tests, and for feeds that
/// cannot resume across a restart anyway (SQLite) — persisting a checkpoint that will
/// be meaningless next launch only invites someone to trust it.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, Checkpoint> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Checkpoint?> LoadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(key, out var cp) ? cp : (Checkpoint?)null);

    /// <inheritdoc />
    public Task SaveAsync(string key, Checkpoint checkpoint, CancellationToken ct = default)
    {
        _store[key] = checkpoint;
        return Task.CompletedTask;
    }
}
