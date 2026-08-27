using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DbSignal.PostgreSql.Tests;

/// <summary>
/// Measures how long the feed actually takes to notice a write, so latency claims are numbers
/// rather than impressions.
/// </summary>
/// <remarks>
/// The other providers' probes allow "one poll interval plus room". A streaming feed has no
/// poll interval, so the budget here is an <strong>absolute</strong> ceiling. Copying the
/// polling formula would have made this test unfalsifiable — there would be no interval to add
/// to, and any number would look acceptable.
/// </remarks>
[Collection("PostgreSql")]
public sealed class LatencyProbeTests
{
    /// <summary>
    /// Generous for a push feed on a loaded CI runner, and still far below anything a polling
    /// provider can reach.
    /// </summary>
    private const double CeilingMilliseconds = 500;

    private readonly PostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LatencyProbeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public async Task Detection_latency_does_not_wait_on_a_poll_interval()
    {
        Skip.IfNot(_fixture.Database.IsAvailable, $"{_fixture.Database.Description} is not available.");

        await using var feed = PostgreSqlFeed.For(_fixture.Database.ConnectionString)
                                             .Watch("public.products")
                                             .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var samples = new List<double>();

        // An async iterator does nothing until the first MoveNextAsync — so the reader has to
        // be running BEFORE the write, or the feed starts replicating after the change and
        // waits for one that already happened.
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

        // Let the feed open its replication connection and run its provisioning checks.
        // Everything before this point is start-up cost, not detection cost.
        await Task.Delay(2000, CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await _fixture.Database.InsertProductAsync($"Latency probe {i}");

            detected.TryTake(out _, TimeSpan.FromSeconds(15)).Should().BeTrue(
                "the write should be detected well inside 15 seconds");
            stopwatch.Stop();

            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            _output.WriteLine($"write #{i} → detected: {stopwatch.Elapsed.TotalMilliseconds,7:F1} ms");
        }

        await cts.CancelAsync();
        await reader;

        // The first sample carries any remaining warm-up. Report it, but judge the library on
        // the steady state.
        _output.WriteLine($"first {samples[0]:F1} ms");

        var steady = samples.Skip(1).ToList();
        var average = steady.Average();
        var max = steady.Max();
        _output.WriteLine($"steady-state average {average:F1} ms · max {max:F1} ms");

        average.Should().BeLessThan(CeilingMilliseconds,
            "a streaming feed is told about the commit — it does not wait to ask");
    }
}
