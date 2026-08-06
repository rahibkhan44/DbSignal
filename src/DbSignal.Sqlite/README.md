# DbSignal.Sqlite

**Know when another process wrote to your SQLite file.**

```csharp
await using var feed = SqliteFeed.For("Data Source=app.db").Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    Console.WriteLine($"Something changed at {batch.ObservedUtc:HH:mm:ss}");
```

Open that `.db` in [DB Browser for SQLite](https://sqlitebrowser.org/), change a row, hit
**Write Changes**, and the console reacts. No schema changes, no triggers, no setup.

## What it can and cannot tell you

Built on `PRAGMA data_version`, which is a single integer the SQLite engine bumps when
*any* other connection commits. That is honest but coarse:

| | |
|---|---|
| Detail | `DatabaseChanged` — **"something changed", not which table or row** |
| Survives app restart | **No** — the counter is meaningless across connections |
| Survives downtime | **No** — changes while your app was down are not recoverable |
| Setup required | None |
| Privileges | None |

If you need to know *which rows* changed, this provider will tell you so at startup rather
than pretend:

```csharp
o.RequireAtLeast(ChangeDetail.KeysChanged);   // throws on SQLite, at startup, with a reason
```

Use `DbSignal.SqlServer` where that matters.

## One thing worth knowing

`data_version` is only comparable against a previous reading **from the same connection**,
so the feed holds one connection open for the lifetime of the enumeration. This is
deliberate and load-bearing.

It also ignores commits made on the connection doing the reading — but the feed never
writes, and your application writes on its own connection, so **your own writes do
surface**. The package therefore declares `FiltersOwnWrites: false`, because that is the
truth.

Targets `net8.0`. MIT licensed.
[Source and full documentation](https://github.com/rahibkhan44/DbSignal).
