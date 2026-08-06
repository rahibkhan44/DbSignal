using Xunit;

namespace DbSignal.SqlServer.Tests;

/// <summary>
/// One provisioned database shared by a test class — creating and dropping a SQL Server
/// database per test would dominate the run time.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public SqlServerTestDatabase Database { get; } = new();

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();

        if (!Database.IsAvailable)
        {
            return;
        }

        // Enable Change Tracking through the library's own provisioner, so the tests
        // exercise the same path a consumer would use rather than a private shortcut.
        await SqlServerFeed.For(Database.ConnectionString)
                           .Watch("dbo.Products")
                           .Provisioner()
                           .EnsureAsync();
    }

    public Task DisposeAsync() => Database.DisposeAsync().AsTask();
}

/// <summary>Shares one provisioned database across the SQL Server test classes.</summary>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
