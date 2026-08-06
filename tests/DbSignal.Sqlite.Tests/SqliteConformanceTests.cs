using DbSignal.Conformance;

namespace DbSignal.Sqlite.Tests;

/// <summary>
/// Runs the shared contract suite against SQLite. No overrides beyond the three hooks —
/// if a provider needs to bend the suite to pass, the abstraction is what is wrong.
/// </summary>
public sealed class SqliteConformanceTests : ChangeFeedConformance, IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private int _counter;

    protected override Task<IChangeFeed> CreateFeedAsync() =>
        Task.FromResult<IChangeFeed>(
            SqliteFeed.For(_database.ConnectionString)
                      .PollEvery(TimeSpan.FromMilliseconds(50))
                      .Build());

    protected override Task WriteAsForeignApplicationAsync()
    {
        _database.WriteFromSeparateConnection($"Product {Interlocked.Increment(ref _counter)}");
        return Task.CompletedTask;
    }

    public void Dispose() => _database.Dispose();
}
