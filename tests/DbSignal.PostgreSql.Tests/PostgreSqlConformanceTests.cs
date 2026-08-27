using DbSignal.Conformance;
using Xunit;

namespace DbSignal.PostgreSql.Tests;

/// <summary>
/// The same conformance suite SQLite and SQL Server run, against PostgreSQL — unbent.
/// </summary>
/// <remarks>
/// <para>
/// This class is the point of the provider. Both shipped providers <em>poll</em>: SQLite reads
/// one integer, SQL Server reads a version then queries change tables. PostgreSQL holds a
/// replication connection open and the server pushes. If a streaming feed satisfies the
/// identical suite with no exemptions beyond the four hooks, the abstraction reaches past
/// polling — which was the open question.
/// </para>
/// <para>
/// If this class ever needs a fifth override, the contract is wrong, not the test.
/// </para>
/// </remarks>
[Collection("PostgreSql")]
public sealed class PostgreSqlConformanceTests : ChangeFeedConformance
{
    private readonly PostgreSqlFixture _fixture;
    private int _counter;

    public PostgreSqlConformanceTests(PostgreSqlFixture fixture) => _fixture = fixture;

    protected override Task<bool> IsAvailableAsync() => Task.FromResult(_fixture.Database.IsAvailable);

    protected override string DatabaseDescription => _fixture.Database.Description;

    protected override Task<IChangeFeed> CreateFeedAsync() =>
        Task.FromResult<IChangeFeed>(
            PostgreSqlFeed.For(_fixture.Database.ConnectionString)
                          .Watch("public.products")
                          .Build());

    protected override Task WriteAsForeignApplicationAsync() =>
        _fixture.Database.InsertProductAsync($"Product {Interlocked.Increment(ref _counter)}");
}
