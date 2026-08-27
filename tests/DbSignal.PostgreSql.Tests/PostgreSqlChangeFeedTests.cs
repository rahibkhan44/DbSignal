using FluentAssertions;
using Xunit;

namespace DbSignal.PostgreSql.Tests;

/// <summary>
/// PostgreSQL behaviour beyond the shared suite — the things logical replication can do that
/// neither polling provider can.
/// </summary>
[Collection("PostgreSql")]
public sealed class PostgreSqlChangeFeedTests
{
    private readonly PostgreSqlFixture _fixture;

    public PostgreSqlChangeFeedTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private string ConnectionString => _fixture.Database.ConnectionString;

    private void RequireServer() =>
        Skip.IfNot(_fixture.Database.IsAvailable, $"{_fixture.Database.Description} is not available.");

    private PostgreSqlChangeFeed Feed() =>
        PostgreSqlFeed.For(ConnectionString).Watch("public.products").Build();

    [SkippableFact]
    public async Task Declares_the_capabilities_logical_replication_can_actually_back()
    {
        RequireServer();

        await using var feed = Feed();

        feed.ProviderName.Should().Be("PostgreSQL");
        feed.Capabilities.Detail.Should().Be(ChangeDetail.RowImages,
            "pgoutput carries column values, not just keys — the first provider that can");
        feed.Capabilities.DurableAcrossRestart.Should().BeTrue(
            "an LSN is meaningful to any connection, at any time");
        feed.Capabilities.SurvivesDowntime.Should().BeTrue(
            "the replication slot retains WAL while nobody is reading");
        feed.Capabilities.RequiresProvisioning.Should().BeTrue(
            "wal_level, a publication, replica identity and a slot are all needed first");
        feed.Capabilities.FiltersOwnWrites.Should().BeFalse(
            "the WAL records every writer, including the app consuming this feed");
    }

    /// <summary>
    /// The upgrade over SQL Server: not just "row 42 was updated" but what it said before and
    /// what it says now. This is the first positive <c>RowImages</c> assertion in the repo.
    /// </summary>
    [SkippableFact]
    public async Task Reports_the_row_before_and_after_an_update()
    {
        RequireServer();

        var id = await _fixture.Database.InsertProductAsync("Before the edit");

        await using var feed = Feed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = ReadUntilAsync(
            feed,
            batch => batch.Tables.SelectMany(t => t.Rows).Any(r => r.Kind == ChangeKind.Update),
            from: null, cts.Token);

        await Task.Delay(500, CancellationToken.None);
        await _fixture.Database.UpdateProductAsync(id, "After the edit");

        var observed = await reader;

        observed.Should().NotBeNull();

        var row = observed!.Tables.SelectMany(t => t.Rows).Single(r => r.Kind == ChangeKind.Update);

        row.Before.Should().NotBeNull("REPLICA IDENTITY FULL is what makes the old row available");
        row.Before!["name"].Should().Be("Before the edit");
        row.After.Should().NotBeNull();
        row.After!["name"].Should().Be("After the edit");

        // Values arrive typed, not as strings — SERIAL comes back as an int.
        row.After["id"].Should().Be(id);
    }

    /// <summary>
    /// A delete under the default replica identity carries only the key. Under FULL it carries
    /// the whole row, which is the difference between "row 42 is gone" and knowing what was
    /// lost.
    /// </summary>
    [SkippableFact]
    public async Task Reports_the_whole_row_that_was_deleted()
    {
        RequireServer();

        var id = await _fixture.Database.InsertProductAsync("Doomed widget");

        await using var feed = Feed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = ReadUntilAsync(
            feed,
            batch => batch.Tables.SelectMany(t => t.Rows).Any(r => r.Kind == ChangeKind.Delete),
            from: null, cts.Token);

        await Task.Delay(500, CancellationToken.None);
        await _fixture.Database.DeleteProductAsync(id);

        var observed = await reader;

        observed.Should().NotBeNull();

        var row = observed!.Tables.SelectMany(t => t.Rows).Single(r => r.Kind == ChangeKind.Delete);

        row.Before.Should().NotBeNull();
        row.Before!["name"].Should().Be("Doomed widget");
        row.After.Should().BeNull("there is no 'after' for a deleted row");
    }

    /// <summary>
    /// A <c>RowImages</c> feed must still populate <c>Keys</c>, or code written against a
    /// <c>KeysChanged</c> provider breaks on the one-line swap the library exists to make safe.
    /// </summary>
    [SkippableFact]
    public async Task Populates_keys_as_well_as_rows()
    {
        RequireServer();

        await using var feed = Feed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = ReadUntilAsync(feed, _ => true, from: null, cts.Token);

        await Task.Delay(500, CancellationToken.None);
        var id = await _fixture.Database.InsertProductAsync("Keyed widget");

        var observed = await reader;

        observed.Should().NotBeNull();

        var table = observed!.Tables.Single();
        table.Schema.Should().Be("public");
        table.Name.Should().Be("products");

        var key = table.Keys.Single();
        key.Kind.Should().Be(ChangeKind.Insert);
        key.Values.Should().ContainSingle().Which.Should().Be(id);
    }

    /// <summary>
    /// One transaction becomes one batch. The polling providers cannot promise this — they see
    /// whatever was committed by the time they looked, which can be a fragment.
    /// </summary>
    [SkippableFact]
    public async Task Delivers_one_transaction_as_one_batch()
    {
        RequireServer();

        await using var feed = Feed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var reader = ReadUntilAsync(feed, _ => true, from: null, cts.Token);

        await Task.Delay(500, CancellationToken.None);
        await _fixture.Database.InsertProductsInTransactionAsync(
            commit: true, "Batched A", "Batched B", "Batched C");

        var observed = await reader;

        observed.Should().NotBeNull();
        observed!.Tables.SelectMany(t => t.Rows).Should().HaveCount(3,
            "all three inserts committed together, so they arrive together");
    }

    /// <summary>A rolled-back transaction never reaches the feed.</summary>
    [SkippableFact]
    public async Task Reports_nothing_for_a_rolled_back_transaction()
    {
        RequireServer();

        await using var feed = Feed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var reader = ReadUntilAsync(feed, _ => true, from: null, cts.Token);

        await Task.Delay(500, CancellationToken.None);
        await _fixture.Database.InsertProductsInTransactionAsync(
            commit: false, "Never committed");

        var observed = await reader;

        observed.Should().BeNull("nothing was committed, so nothing entered the WAL stream");
    }

    /// <summary>
    /// Durable providers <em>skip</em> the conformance resume check, so nothing else in the
    /// suite proves an LSN can be stored and reused. Without this, the whole point of a
    /// permanent slot goes untested.
    /// </summary>
    [SkippableFact]
    public async Task Resumes_from_a_stored_position_after_the_feed_is_gone()
    {
        RequireServer();

        Checkpoint position;

        await using (var first = Feed())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reader = ReadUntilAsync(first, _ => true, from: null, cts.Token);

            await Task.Delay(500, CancellationToken.None);
            await _fixture.Database.InsertProductAsync("Before the restart");

            var observed = await reader;
            observed.Should().NotBeNull();
            position = observed!.Position;
        }

        // The feed is disposed. The write below happens while nothing is reading — the slot is
        // what holds the WAL until a reader comes back.
        await _fixture.Database.InsertProductAsync("While nobody was watching");

        await using var second = Feed();
        using var resumeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var resumed = await ReadUntilAsync(second, _ => true, position, resumeCts.Token);

        resumed.Should().NotBeNull("the write that happened during the gap must still arrive");
        resumed!.Tables.SelectMany(t => t.Rows)
                .Should().Contain(r => (string?)r.After!["name"] == "While nobody was watching");
    }

    /// <summary>
    /// A checkpoint is an opaque string, so a position saved against SQL Server ("42") is
    /// syntactically a valid <see cref="Checkpoint"/> and only this provider can tell it is
    /// nonsense. Failing is the only safe answer: silently starting from the beginning would
    /// replay history, and silently starting from now would lose it.
    /// </summary>
    [SkippableFact]
    public async Task Rejects_a_position_that_is_not_an_LSN()
    {
        RequireServer();

        await using var feed = Feed();
        var foreign = new Checkpoint("42");

        var read = async () =>
        {
            await foreach (var _ in feed.ReadAsync(foreign, CancellationToken.None))
            {
                break;
            }
        };

        _ = await read.Should().ThrowAsync<DbSignalException>();
    }

    /// <summary>
    /// An unprovisioned database fails loudly. The alternative — an empty stream — is the worst
    /// possible outcome, because it looks exactly like a quiet database.
    /// </summary>
    [SkippableFact]
    public async Task Refuses_to_stream_from_an_unprovisioned_table()
    {
        RequireServer();

        await using var feed = PostgreSqlFeed.For(ConnectionString)
                                             .Watch("public.unwatched")
                                             .WithPublication("dbsignal_pub")
                                             .WithSlot("dbsignal_slot")
                                             .Build();

        var read = async () =>
        {
            await foreach (var _ in feed.ReadAsync(Checkpoint.Now, CancellationToken.None))
            {
                break;
            }
        };

        var thrown = await read.Should().ThrowAsync<ProvisioningRequiredException>();
        _ = thrown.Which.Message.Should().Contain("unwatched",
            "the message has to name the gap, or the operator has to guess");
    }

    /// <summary>
    /// A table name cannot be a parameter in <c>CREATE PUBLICATION</c>, so quoting is the only
    /// defence. Doubling the embedded quote is what makes it one.
    /// </summary>
    [Fact]
    public void Quotes_a_hostile_identifier_safely()
    {
        var table = new PublishedTable("public", "Ev\"il");

        table.QuotedName.Should().Be("\"public\".\"Ev\"\"il\"");
    }

    /// <summary>
    /// PostgreSQL folds unquoted identifiers to lower case, so a name written as
    /// <c>Products</c> resolves to <c>products</c> on the server. Watching the un-folded name
    /// produces no error — just a feed that never reports anything.
    /// </summary>
    [Fact]
    public void Folds_an_unquoted_identifier_the_way_the_server_will()
    {
        PublishedTable.Parse("Products")
                      .Should().Be(new PublishedTable("public", "products"));

        PublishedTable.Parse("MySchema.Products")
                      .Should().Be(new PublishedTable("myschema", "products"));

        PublishedTable.Parse("\"MixedCase\"")
                      .Should().Be(new PublishedTable("public", "MixedCase"),
                                   "a quoted identifier keeps its case");
    }

    /// <summary>
    /// Reads until a batch satisfies <paramref name="predicate"/>, or the token fires. Starting
    /// the reader before the write matters: the enumerator is lazy, so a feed created but not
    /// yet enumerated has not connected, and the write would land before anyone was listening.
    /// </summary>
    private static Task<ChangeBatch?> ReadUntilAsync(
        PostgreSqlChangeFeed feed,
        Func<ChangeBatch, bool> predicate,
        Checkpoint? from,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in feed.ReadAsync(from ?? Checkpoint.Now, ct))
                {
                    if (predicate(batch))
                    {
                        return batch;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The timeout is how "nothing arrived" is expressed.
            }

            return null;
        }, CancellationToken.None);
}
