# DbSignal.Abstractions

**The contract. Depends on nothing.**

This package defines what a change feed *is*. It contains no database code — reference it
from a domain or contract project that must not drag a database driver along, then
reference a provider package where the wiring happens.

```csharp
public interface IChangeFeed : IAsyncDisposable
{
    FeedCapabilities Capabilities { get; }
    string ProviderName { get; }
    IAsyncEnumerable<ChangeBatch> ReadAsync(Checkpoint from, CancellationToken ct = default);
}
```

## Capabilities, not lies

Databases are not equally good at reporting change. SQLite can only say "something in this
file changed"; SQL Server names the individual rows and whether each was an insert, update
or delete. A uniform API over that would have to either lie about SQLite or cripple the
others down to its floor.

So every feed declares what it can actually do:

```csharp
public sealed record FeedCapabilities(
    ChangeDetail Detail,          // DatabaseChanged < TableChanged < KeysChanged < RowImages
    bool DurableAcrossRestart,
    bool SurvivesDowntime,
    bool FiltersOwnWrites,
    bool RequiresProvisioning);
```

and consumers declare what they need. Asking for more than a provider offers fails at
**startup**, with a message saying why — not quietly, six months later, when someone
notices half the job was never being done.

## Providers

| Package | Mechanism | Detail |
|---|---|---|
| `DbSignal.Sqlite` | `PRAGMA data_version` | `DatabaseChanged` |
| `DbSignal.SqlServer` | Change Tracking | `KeysChanged` |

## Delivery guarantee

**At-least-once.** A crash between handling a batch and saving its checkpoint replays that
batch, so handlers must be idempotent. Exactly-once is not achievable across these
mechanisms, and claiming it would be a lie.

Targets `net8.0` and `netstandard2.0`. MIT licensed.
[Source and full documentation](https://github.com/rahibkhan44/DbSignal).
