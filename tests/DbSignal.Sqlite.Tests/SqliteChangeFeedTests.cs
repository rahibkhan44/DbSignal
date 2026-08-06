using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DbSignal.Sqlite.Tests;

/// <summary>
/// SQLite-specific behaviour, beyond the shared conformance suite.
/// </summary>
public sealed class SqliteChangeFeedTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    [Fact]
    public async Task Declares_the_capabilities_PRAGMA_data_version_can_actually_back()
    {
        await using var feed = new SqliteChangeFeed(_database.ConnectionString);

        feed.ProviderName.Should().Be("SQLite");
        feed.Capabilities.Detail.Should().Be(ChangeDetail.DatabaseChanged,
            "data_version is one number for the whole file — there is no table information in it");
        feed.Capabilities.DurableAcrossRestart.Should().BeFalse(
            "the counter is only comparable within one connection's lifetime");
        feed.Capabilities.SurvivesDowntime.Should().BeFalse(
            "SQLite keeps no change history to catch up on");
        feed.Capabilities.RequiresProvisioning.Should().BeFalse(
            "no DDL, no server configuration, nothing to switch on");
        feed.Capabilities.FiltersOwnWrites.Should().BeFalse(
            "the feed's connection never writes, so the application's own writes DO surface");
    }

    [Fact]
    public async Task Detects_an_external_write_within_a_few_poll_intervals()
    {
        await using var feed = SqliteFeed.For(_database.ConnectionString)
                                         .PollEvery(TimeSpan.FromMilliseconds(50))
                                         .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reader = Task.Run(async () =>
        {
            await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
            {
                return batch;
            }
            return null;
        }, CancellationToken.None);

        await Task.Delay(200, CancellationToken.None);
        _database.WriteFromSeparateConnection("Widget");

        var observed = await reader;

        observed.Should().NotBeNull();
        observed!.Tables.Should().BeEmpty("DatabaseChanged carries no table detail");
        observed.Position.Value.Should().NotBeNullOrEmpty("the checkpoint carries the data_version");
    }

    [Fact]
    public async Task Reports_every_distinct_external_write_not_just_the_first()
    {
        await using var feed = SqliteFeed.For(_database.ConnectionString)
                                         .PollEvery(TimeSpan.FromMilliseconds(50))
                                         .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = new List<ChangeBatch>();

        var reader = Task.Run(async () =>
        {
            await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
            {
                seen.Add(batch);
                if (seen.Count == 3)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        await Task.Delay(200, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            _database.WriteFromSeparateConnection($"Widget {i}");
            await Task.Delay(150, CancellationToken.None);
        }

        await reader;

        seen.Should().HaveCount(3);
        seen.Select(b => b.Position.Value).Should().OnlyHaveUniqueItems(
            "each commit moves data_version, so each batch carries a distinct position");
    }

    [Fact]
    public async Task Stays_silent_when_nothing_writes()
    {
        await using var feed = SqliteFeed.For(_database.ConnectionString)
                                         .PollEvery(TimeSpan.FromMilliseconds(50))
                                         .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var count = 0;

        try
        {
            await foreach (var _ in feed.ReadAsync(Checkpoint.Now, cts.Token))
            {
                count++;
            }
        }
        catch (OperationCanceledException)
        {
        }

        count.Should().Be(0, "an idle database must not produce phantom notifications");
    }

    [Fact]
    public async Task Reads_do_not_count_as_changes()
    {
        await using var feed = SqliteFeed.For(_database.ConnectionString)
                                         .PollEvery(TimeSpan.FromMilliseconds(50))
                                         .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var count = 0;

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in feed.ReadAsync(Checkpoint.Now, cts.Token))
                {
                    Interlocked.Increment(ref count);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        // Hammer the database with reads from another connection.
        for (var i = 0; i < 20; i++)
        {
            await using var connection = new SqliteConnection(_database.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Products;";
            await command.ExecuteScalarAsync(CancellationToken.None);
        }

        await reader;

        count.Should().Be(0, "data_version tracks commits, not queries");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_missing_connection_string(string? connectionString)
    {
        var act = () => new SqliteChangeFeed(connectionString!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_non_positive_poll_interval()
    {
        var act = () => new SqliteChangeFeed(_database.ConnectionString, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose() => _database.Dispose();
}
