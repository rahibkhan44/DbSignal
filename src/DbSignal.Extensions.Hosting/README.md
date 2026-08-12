# DbSignal.Extensions.Hosting

**Register a feed and a handler. The library owns the loop.**

```csharp
builder.Services.AddDbSignal(o =>
{
    o.UseFeed(_ => SqlServerFeed.For(connectionString).Watch("dbo.Products").Build());
    o.RequireAtLeast(ChangeDetail.KeysChanged);
})
.AddHandler<ProductCacheInvalidator>();
```

```csharp
public sealed class ProductCacheInvalidator : IChangeHandler
{
    public Task HandleAsync(ChangeBatch batch, CancellationToken ct)
    {
        foreach (var table in batch.Tables)
            foreach (var key in table.Keys)
                cache.Evict(key.Values[0]);

        return Task.CompletedTask;
    }
}
```

That is the whole integration. No `BackgroundService` to write, no retry loop, no checkpoint
bookkeeping.

## What it does for you

- **Runs the feed** for the lifetime of the application, as a hosted service.
- **Dispatches each batch** to every registered handler, resolved from a fresh DI scope — so
  a scoped `DbContext` behaves exactly as it does in a web request.
- **Saves the checkpoint after handlers succeed**, never before. That ordering is what makes
  at-least-once true: a crash mid-handler replays the batch instead of skipping it.
- **Retries with exponential backoff** when the feed faults, capped, so a database coming
  back up is not hit by every workstation at once.
- **Handles retention expiry** — on `ResyncRequiredException` it logs a warning telling you
  to reload your cache, then resumes from now rather than silently delivering nothing.

## The line worth writing

```csharp
o.RequireAtLeast(ChangeDetail.KeysChanged);
```

Point that at SQLite, which can only report "something changed", and **the application
refuses to start** with a message naming both tiers. Without it, a "patch the changed rows"
app pointed at the wrong provider runs happily and quietly does a fraction of its job.

## Provider-neutral on purpose

Configuration takes `UseFeed(Func<IServiceProvider, IChangeFeed>)` rather than
`UseSqlServer(connectionString)`. This package therefore references **no database driver** —
adding a provider to your app never widens this package's dependency graph, and this package
never dictates your driver version.

## Handlers must be idempotent

Delivery is **at-least-once**. A crash between handling a batch and persisting its checkpoint
replays that batch. One handler throwing does not stop its siblings from seeing the batch, and
by default that batch is delivered again — with backoff, and with the checkpoint held where it
is — until a handler accepts it. Set `RetryFailedBatches = false` if a poison batch blocking
the stream would be worse than dropping it.

Retrying holds the stream at the failed batch rather than reading past it. It has to: a
provider advances its own cursor as it yields, so the next batch off the feed covers only what
happened afterwards. Reading on and then saving that later position would carry the checkpoint
over changes no handler ever accepted, and no restart could reach them again.

Checkpoints are kept in memory unless you supply a store with `UseCheckpointStore<T>()`.
Deliberately not a file by default: writing checkpoints somewhere you did not choose shows up
months later as a stale resume nobody can explain.

Targets `net8.0`. MIT licensed.
[Source and full documentation](https://github.com/rahibkhan44/DbSignal).
