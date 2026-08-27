using Xunit;

namespace DbSignal.PostgreSql.Tests;

/// <summary>
/// One provisioned database shared by a test class — creating a database and a replication
/// slot per test would dominate the run time.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public PostgreSqlTestDatabase Database { get; } = new();

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();

        if (!Database.IsAvailable)
        {
            return;
        }

        // Provision through the library's own provisioner, so the tests exercise the same path
        // a consumer would use rather than a private shortcut. It also has to happen here
        // rather than lazily: the conformance suite's first read swallows only
        // OperationCanceledException, so an unprovisioned database would fail rather than skip.
        await PostgreSqlFeed.For(Database.ConnectionString)
                            .Watch("public.products")
                            .Provisioner()
                            .EnsureAsync();
    }

    public Task DisposeAsync() => Database.DisposeAsync().AsTask();
}

/// <summary>Shares one provisioned database across the PostgreSQL test classes.</summary>
[CollectionDefinition("PostgreSql")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;
