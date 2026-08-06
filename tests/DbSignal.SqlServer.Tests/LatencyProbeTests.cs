using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DbSignal.SqlServer.Tests;

/// <summary>
/// Measures how long the feed actually takes to notice a write, so latency claims are
/// numbers rather than impressions.
/// </summary>
[Collection("SqlServer")]
public sealed class LatencyProbeTests
{
    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LatencyProbeTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public async Task Detection_latency_is_close_to_the_poll_interval()
    {
        Skip.IfNot(_fixture.Database.IsAvailable, $"{_fixture.Database.Description} is not available.");

        var pollInterval = TimeSpan.FromMilliseconds(250);

        await using var feed = SqlServerFeed.For(_fixture.Database.ConnectionString)
                                            .Watch("dbo.Products")
                                            .PollEvery(pollInterval)
                                            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var samples = new List<double>();

        // An async iterator does nothing until the first MoveNextAsync — so the reader has
        // to be running BEFORE the write, or the feed takes its baseline after the change
        // and waits forever for one that already happened.
        using var detected = new System.Collections.Concurrent.BlockingCollection<DateTimeOffset>();

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in feed.ReadAsync(Checkpoint.Now, cts.Token))
                {
                    detected.Add(DateTimeOffset.UtcNow, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        // Let the feed open its connection, run its provisioning checks, and take a
        // baseline. Everything before this point is start-up cost, not detection cost.
        await Task.Delay(2000, CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await _fixture.Database.InsertProductAsync($"Latency probe {i}");

            detected.TryTake(out _, TimeSpan.FromSeconds(15)).Should().BeTrue(
                "the write should be detected well inside 15 seconds");
            stopwatch.Stop();

            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            _output.WriteLine($"write #{i} → detected: {stopwatch.Elapsed.TotalMilliseconds,7:F1} ms");
        }

        await cts.CancelAsync();
        await reader;

        // The first sample carries any remaining warm-up (connection pool, JIT). Report it,
        // but judge the library on the steady state.
        _output.WriteLine($"first {samples[0]:F1} ms");

        var steady = samples.Skip(1).ToList();
        var average = steady.Average();
        var max = steady.Max();
        _output.WriteLine(
            $"steady-state average {average:F1} ms · max {max:F1} ms · poll interval {pollInterval.TotalMilliseconds} ms");

        // A poll-based feed cannot beat its own interval, and the write itself takes time.
        // Allow the interval plus generous room for the round trips; anything beyond that
        // means the feed is doing avoidable work per change.
        average.Should().BeLessThan(pollInterval.TotalMilliseconds + 400,
            "detection should cost roughly one poll interval plus the queries it takes to " +
            "read the changed keys — not seconds");
    }
}
