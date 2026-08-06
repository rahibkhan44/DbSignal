using DbSignal.Conformance;
using Xunit;

namespace DbSignal.SqlServer.Tests;

/// <summary>
/// The same conformance suite SQLite runs, against SQL Server — unbent.
/// </summary>
/// <remarks>
/// This class is the real test of the abstraction. SQLite polls one integer and can only
/// say "something changed"; SQL Server queries change tables and names individual rows.
/// If both pass the identical suite with no provider-specific exemptions, the contract
/// holds. If this class had needed a special case, the contract would have been wrong.
/// </remarks>
[Collection("SqlServer")]
public sealed class SqlServerConformanceTests : ChangeFeedConformance
{
    private readonly SqlServerFixture _fixture;
    private int _counter;

    public SqlServerConformanceTests(SqlServerFixture fixture) => _fixture = fixture;

    protected override Task<bool> IsAvailableAsync() => Task.FromResult(_fixture.Database.IsAvailable);

    protected override string DatabaseDescription => _fixture.Database.Description;

    protected override Task<IChangeFeed> CreateFeedAsync() =>
        Task.FromResult<IChangeFeed>(
            SqlServerFeed.For(_fixture.Database.ConnectionString)
                         .Watch("dbo.Products")
                         .PollEvery(TimeSpan.FromMilliseconds(100))
                         .Build());

    protected override Task WriteAsForeignApplicationAsync() =>
        _fixture.Database.InsertProductAsync($"Product {Interlocked.Increment(ref _counter)}");
}
