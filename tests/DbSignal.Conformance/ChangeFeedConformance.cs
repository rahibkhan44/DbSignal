using FluentAssertions;
using Xunit;

namespace DbSignal.Conformance;

/// <summary>
/// The shared contract suite. Every provider inherits it and it runs unchanged — that is
/// the whole point. A provider is not "supported" until this is green against a real
/// instance of its database.
/// </summary>
/// <remarks>
/// <para>
/// Tests assert only what the provider's own <see cref="FeedCapabilities"/> promise. A
/// feed that declares <c>DurableAcrossRestart: false</c> has its resume test skipped —
/// <strong>and the skip itself is asserted</strong>, so a provider cannot quietly claim a
/// guarantee it does not honour, and cannot quietly drop one it does.
/// </para>
/// <para>
/// Derive, implement the three hooks, and add nothing. If a provider needs a special case
/// here to pass, the abstraction is wrong and the abstraction is what should change.
/// </para>
/// </remarks>
public abstract class ChangeFeedConformance
{
    /// <summary>How long a test waits for a change before deciding it will never arrive.</summary>
    protected virtual TimeSpan DetectionTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Creates a feed over a database this test owns. Called once per test; the returned
    /// feed is disposed by the harness.
    /// </summary>
    protected abstract Task<IChangeFeed> CreateFeedAsync();

    /// <summary>
    /// Writes to the database the way a <em>foreign application</em> would — a separate
    /// connection, no ORM, nothing the feed could have been told about in advance.
    /// </summary>
    /// <remarks>
    /// This is the method that makes the suite meaningful. If a provider implements it by
    /// writing through the feed's own connection, the headline test proves nothing.
    /// </remarks>
    protected abstract Task WriteAsForeignApplicationAsync();

    /// <summary>
    /// False when the backing database is unavailable (no Docker, no server), so the suite
    /// <strong>skips visibly</strong> instead of failing a machine that was never going to
    /// run it — and, just as importantly, instead of passing vacuously. A green tick that
    /// tested nothing is how a README ends up claiming four databases and shipping one.
    /// </summary>
    protected virtual Task<bool> IsAvailableAsync() => Task.FromResult(true);

    /// <summary>What is missing, named in the skip message so the reason is obvious.</summary>
    protected virtual string DatabaseDescription => GetType().Name;

    // ── The headline ────────────────────────────────────────────────────────────

    /// <summary>
    /// A write by another application is reported. This is the entire reason the library
    /// exists; if it fails, nothing else matters.
    /// </summary>
    [SkippableFact]
    public async Task Detects_a_write_made_by_another_application()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();
        using var cts = new CancellationTokenSource(DetectionTimeout);

        var observed = ReadFirstBatchAsync(feed, cts.Token);

        // Give the feed a moment to take its baseline before the write lands, so the
        // test proves detection rather than accidentally passing on start-up noise.
        await Task.Delay(200, CancellationToken.None);
        await WriteAsForeignApplicationAsync();

        var batch = await observed;

        batch.Should().NotBeNull(
            "a write from another connection is exactly what this library promises to detect");
        batch!.ObservedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── Quietness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An idle database produces nothing. A feed that reports phantom changes is worse
    /// than useless — it retrains people to ignore it.
    /// </summary>
    [SkippableFact]
    public async Task Reports_nothing_while_the_database_is_idle()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var batch = await ReadFirstBatchAsync(feed, cts.Token);

        batch.Should().BeNull("nothing wrote to the database");
    }

    // ── Capability honesty ──────────────────────────────────────────────────────

    /// <summary>
    /// The declared capabilities are internally coherent. Cheap, and it catches a
    /// copy-pasted capability block — the most likely way a new provider lies.
    /// </summary>
    [SkippableFact]
    public async Task Declares_coherent_capabilities()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();
        var caps = feed.Capabilities;

        feed.ProviderName.Should().NotBeNullOrWhiteSpace("error messages name the provider");

        if (caps.SurvivesDowntime)
        {
            caps.DurableAcrossRestart.Should().BeTrue(
                "a feed that retains changes across a disconnect must also be able to resume into them — " +
                "otherwise the retained changes are unreachable");
        }
    }

    /// <summary>
    /// A batch never carries more detail than the provider claims. This is the test that
    /// stops a README from outrunning the code: over-claiming fails, and so does
    /// under-delivering.
    /// </summary>
    [SkippableFact]
    public async Task Never_emits_more_detail_than_it_declares()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();
        using var cts = new CancellationTokenSource(DetectionTimeout);

        var observed = ReadFirstBatchAsync(feed, cts.Token);
        await Task.Delay(200, CancellationToken.None);
        await WriteAsForeignApplicationAsync();
        var batch = await observed;

        if (batch is null)
        {
            return; // covered by the headline test
        }

        var detail = feed.Capabilities.Detail;

        if (detail == ChangeDetail.DatabaseChanged)
        {
            batch.Tables.Should().BeEmpty(
                "a feed declaring DatabaseChanged cannot know which table moved, so it must not pretend to");
        }

        if (detail < ChangeDetail.KeysChanged)
        {
            batch.Tables.SelectMany(t => t.Keys).Should().BeEmpty(
                "row keys require at least ChangeDetail.KeysChanged");
        }

        if (detail < ChangeDetail.RowImages)
        {
            batch.Tables.SelectMany(t => t.Rows).Should().BeEmpty(
                "before/after images require ChangeDetail.RowImages");
        }

        // The other direction. Every check above catches a provider claiming MORE than it
        // delivers; without these, a provider claiming the TOP tier is checked by nothing at
        // all, because each `detail < …` branch is skipped. A feed declaring RowImages could
        // return empty Keys and Rows forever and pass this entire suite.
        if (detail >= ChangeDetail.KeysChanged)
        {
            batch.Tables.SelectMany(t => t.Keys).Should().NotBeEmpty(
                "a feed declaring KeysChanged or better must name the rows that changed");
        }

        if (detail >= ChangeDetail.RowImages)
        {
            batch.Tables.SelectMany(t => t.Rows).Should().NotBeEmpty(
                "a feed declaring RowImages must carry before/after values, not just keys");
        }
    }

    /// <summary>
    /// A feed that cannot resume must not hand out checkpoints that look resumable.
    /// </summary>
    /// <remarks>
    /// The inverse of a skipped test: rather than silently not checking durability, we
    /// assert the provider behaves like something that has none. That is what stops
    /// <c>DurableAcrossRestart: false</c> from being a free pass.
    /// </remarks>
    [SkippableFact]
    public async Task Non_durable_feeds_ignore_the_starting_checkpoint()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();

        if (feed.Capabilities.DurableAcrossRestart)
        {
            return; // durability is exercised by the provider's own resume test
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // A checkpoint from a previous life. A non-durable feed must treat it as "start
        // fresh" and stay quiet, not replay history it does not have.
        var batch = await ReadFirstBatchAsync(feed, cts.Token, new Checkpoint("999999"));

        batch.Should().BeNull(
            "a non-durable feed has no history to replay, so an old checkpoint must not produce phantom batches");
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancellation stops the stream promptly. A feed that ignores its token blocks
    /// application shutdown, which is how a background service becomes a support ticket.
    /// </summary>
    [SkippableFact]
    public async Task Stops_promptly_when_cancelled()
    {
        Skip.IfNot(await IsAvailableAsync(), $"{DatabaseDescription} is not available on this machine.");

        await using var feed = await CreateFeedAsync();
        using var cts = new CancellationTokenSource();

        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in feed.ReadAsync(Checkpoint.Now, cts.Token))
                {
                    // drain
                }
            }
            catch (OperationCanceledException)
            {
                // Acceptable: either ending the stream or throwing is a valid response.
            }
        }, CancellationToken.None);

        cts.Cancel();

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().Be(run, "the feed must observe its cancellation token");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first batch, or null if the token fires first. Never throws on
    /// cancellation, so a test can express "I expect nothing" as cleanly as "I expect one".
    /// </summary>
    private static async Task<ChangeBatch?> ReadFirstBatchAsync(
        IChangeFeed feed, CancellationToken ct, Checkpoint? from = null)
    {
        try
        {
            await foreach (var batch in feed.ReadAsync(from ?? Checkpoint.Now, ct))
            {
                return batch;
            }
        }
        catch (OperationCanceledException)
        {
            // "nothing arrived in time" — a result, not a failure.
        }

        return null;
    }
}
