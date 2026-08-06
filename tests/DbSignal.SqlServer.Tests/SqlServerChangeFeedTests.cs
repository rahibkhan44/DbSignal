using FluentAssertions;
using Xunit;

namespace DbSignal.SqlServer.Tests;

/// <summary>
/// SQL Server behaviour beyond the shared suite — the things Change Tracking can do that
/// SQLite cannot.
/// </summary>
[Collection("SqlServer")]
public sealed class SqlServerChangeFeedTests
{
    private readonly SqlServerFixture _fixture;

    public SqlServerChangeFeedTests(SqlServerFixture fixture) => _fixture = fixture;

    private string ConnectionString => _fixture.Database.ConnectionString;

    private void RequireServer() =>
        Skip.IfNot(_fixture.Database.IsAvailable, $"{_fixture.Database.Description} is not available.");

    [SkippableFact]
    public async Task Declares_the_capabilities_Change_Tracking_can_actually_back()
    {
        RequireServer();

        await using var feed = SqlServerFeed.For(ConnectionString).Watch("dbo.Products").Build();

        feed.ProviderName.Should().Be("SQL Server");
        feed.Capabilities.Detail.Should().Be(ChangeDetail.KeysChanged,
            "CHANGETABLE returns the changed primary keys, but not the column values");
        feed.Capabilities.DurableAcrossRestart.Should().BeTrue(
            "a change-tracking version is meaningful to any connection, at any time");
        feed.Capabilities.SurvivesDowntime.Should().BeTrue(
            "changes remain readable for the retention window");
        feed.Capabilities.RequiresProvisioning.Should().BeTrue(
            "ALTER DATABASE and ALTER TABLE are needed before anything is recorded");
        feed.Capabilities.FiltersOwnWrites.Should().BeFalse(
            "Change Tracking records every writer, including the app consuming this feed");
    }

    /// <summary>
    /// The upgrade over SQLite: not just "something changed" but which table, which row,
    /// and whether it was an insert, an update or a delete.
    /// </summary>
    [SkippableFact]
    public async Task Reports_which_rows_changed_and_how()
    {
        RequireServer();

        await using var feed = SqlServerFeed.For(ConnectionString)
                                            .Watch("dbo.Products")
                                            .PollEvery(TimeSpan.FromMilliseconds(100))
                                            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = Task.Run(async () =>
        {
            await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
            {
                return batch;
            }
            return null;
        }, CancellationToken.None);

        await Task.Delay(300, CancellationToken.None);
        await _fixture.Database.InsertProductAsync("Traceable widget");

        var observed = await reader;

        observed.Should().NotBeNull();
        observed!.Tables.Should().ContainSingle();

        var table = observed.Tables[0];
        table.Schema.Should().Be("dbo");
        table.Name.Should().Be("Products");
        table.QualifiedName.Should().Be("dbo.Products");

        table.Keys.Should().ContainSingle("one row was inserted");
        table.Keys[0].Kind.Should().Be(ChangeKind.Insert);
        table.Keys[0].Values.Should().ContainSingle("Products has a single-column primary key");
        table.Keys[0].Values[0].Should().BeOfType<int>("the key column is INT IDENTITY");

        table.Rows.Should().BeEmpty("KeysChanged carries keys, not before/after images");
    }

    /// <summary>
    /// A change to a table nobody asked about must not wake the handler. The database
    /// version moves for every tracked table, so this is a real filtering decision, not
    /// something that falls out for free.
    /// </summary>
    [SkippableFact]
    public async Task Ignores_tables_it_was_not_asked_to_watch()
    {
        RequireServer();

        await using var feed = SqlServerFeed.For(ConnectionString)
                                            .Watch("dbo.Products")
                                            .PollEvery(TimeSpan.FromMilliseconds(100))
                                            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var batches = 0;

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in feed.ReadAsync(Checkpoint.Now, cts.Token))
                {
                    Interlocked.Increment(ref batches);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        await Task.Delay(300, CancellationToken.None);
        await _fixture.Database.InsertUnwatchedAsync("not your table");

        await reader;

        batches.Should().Be(0, "dbo.Unwatched is not change-tracked and was never watched");
    }

    /// <summary>
    /// The capability SQLite does not have: stop, restart from a stored checkpoint, and
    /// receive what happened in between.
    /// </summary>
    [SkippableFact]
    public async Task Resumes_from_a_checkpoint_and_delivers_what_was_missed()
    {
        RequireServer();

        var store = new InMemoryCheckpointStore();

        // Session one: observe a change and remember where we got to.
        await using (var first = SqlServerFeed.For(ConnectionString)
                                              .Watch("dbo.Products")
                                              .PollEvery(TimeSpan.FromMilliseconds(100))
                                              .Build())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var reader = Task.Run(async () =>
            {
                await foreach (var batch in first.ReadAsync(Checkpoint.Now, cts.Token))
                {
                    return batch;
                }
                return null;
            }, CancellationToken.None);

            await Task.Delay(300, CancellationToken.None);
            await _fixture.Database.InsertProductAsync("Before the restart");

            var batch = await reader;
            batch.Should().NotBeNull();
            await store.SaveAsync("products", batch!.Position);
        }

        // Nobody is watching. This is the write that a non-durable feed would lose.
        await _fixture.Database.InsertProductAsync("While nobody was listening");

        // Session two: resume from the stored position.
        var resumeFrom = await store.LoadAsync("products");
        resumeFrom.Should().NotBeNull();

        await using (var second = SqlServerFeed.For(ConnectionString)
                                               .Watch("dbo.Products")
                                               .PollEvery(TimeSpan.FromMilliseconds(100))
                                               .Build())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            ChangeBatch? recovered = null;
            await foreach (var batch in second.ReadAsync(resumeFrom!.Value, cts.Token))
            {
                recovered = batch;
                break;
            }

            recovered.Should().NotBeNull(
                "the write made while the feed was stopped must still be delivered — " +
                "this is exactly what DurableAcrossRestart promises");
            recovered!.Tables.Should().ContainSingle();
            recovered.Tables[0].Keys.Should().NotBeEmpty();
        }
    }

    [SkippableFact]
    public async Task Distinguishes_updates_and_deletes_from_inserts()
    {
        RequireServer();

        await _fixture.Database.InsertProductAsync("Doomed");

        await using var feed = SqlServerFeed.For(ConnectionString)
                                            .Watch("dbo.Products")
                                            .PollEvery(TimeSpan.FromMilliseconds(100))
                                            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = Task.Run(async () =>
        {
            await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
            {
                return batch;
            }
            return null;
        }, CancellationToken.None);

        await Task.Delay(300, CancellationToken.None);

        var id = await GetLatestProductIdAsync();
        await _fixture.Database.DeleteProductAsync(id);

        var observed = await reader;

        observed.Should().NotBeNull();
        observed!.Tables[0].Keys.Should().Contain(k => k.Kind == ChangeKind.Delete,
            "a delete is reported as a delete — with its key, which the row itself no longer has");
    }

    [SkippableFact]
    public async Task Rejects_a_checkpoint_from_a_different_provider()
    {
        RequireServer();

        await using var feed = SqlServerFeed.For(ConnectionString)
                                            .Watch("dbo.Products")
                                            .PollEvery(TimeSpan.FromMilliseconds(100))
                                            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var act = async () =>
        {
            await foreach (var _ in feed.ReadAsync(new Checkpoint("not-a-version"), cts.Token))
            {
                break;
            }
        };

        await act.Should().ThrowAsync<DbSignalException>(
            "checkpoints are provider-specific; silently guessing a start position would " +
            "either lose history or replay everything");
    }

    [Fact]
    public void Refuses_to_build_a_feed_that_watches_nothing()
    {
        var act = () => SqlServerFeed.For("Server=x;Database=y").Build();

        act.Should().Throw<InvalidOperationException>(
            "Change Tracking is per-table, so a feed watching nothing would report nothing forever");
    }

    [Theory]
    [InlineData("Products", "dbo", "Products")]
    [InlineData("dbo.Products", "dbo", "Products")]
    [InlineData("sales.Orders", "sales", "Orders")]
    [InlineData("[dbo].[Products]", "dbo", "Products")]
    public void Parses_table_names_the_way_a_developer_writes_them(
        string input, string expectedSchema, string expectedName)
    {
        var table = WatchedTable.Parse(input);

        table.Schema.Should().Be(expectedSchema);
        table.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Quotes_identifiers_so_a_hostile_table_name_cannot_break_out()
    {
        var table = new WatchedTable("dbo", "Ev]il");

        table.QuotedName.Should().Be("[dbo].[Ev]]il]",
            "a closing bracket must be doubled, or the identifier escapes its quoting");
    }

    private async Task<int> GetLatestProductIdAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 Id FROM dbo.Products ORDER BY Id DESC;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
