# DbSignal.SqlServer

**Know which rows someone else changed — whichever application wrote them.**

```csharp
await using var feed = SqlServerFeed.For(connectionString)
                                    .Watch("dbo.Products")
                                    .Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    foreach (var table in batch.Tables)
        foreach (var key in table.Keys)
            Console.WriteLine($"{key.Kind} row {key.Values[0]} in {table.QualifiedName}");
```

```
+  Insert  Id=1
~  Update  Id=1
-  Delete  Id=1
```

Built on **Change Tracking** — not CDC, not Service Broker, not triggers.

## What it gives you

| | |
|---|---|
| Detail | `KeysChanged` — the primary keys that changed, and insert/update/delete |
| Survives app restart | Yes, via checkpoint |
| Survives downtime | Yes, within the retention window |
| Setup required | `ALTER DATABASE … SET CHANGE_TRACKING = ON`, plus per table |
| Privileges | `ALTER` to provision; `SELECT` + `VIEW CHANGE TRACKING` to read |

**Works on every edition, including Express and LocalDB.** Change Tracking is not the
Enterprise-tier feature — that is CDC, which this package deliberately does not use.

## Why not `SqlDependency` / `SqlTableDependency`

`SqlDependency` is documented by Microsoft as *"not designed for use in client
applications"* and does not support SQL Server Express. `SqlTableDependency` builds Service
Broker infrastructure it must then tear down, and its queries must not contain `JOIN`s.
This package queries `CHANGETABLE(CHANGES …)` and leaves no infrastructure behind beyond
the Change Tracking flag you turned on.

## Retention expiry is loud, and that matters

If your checkpoint falls outside the retention window, the gap is genuinely unreadable.
This package throws `ResyncRequiredException` so you can reload from source.

A hand-rolled poller typically finds no rows in that situation and reports "nothing
changed" — silent data loss. This is the single biggest correctness advantage over rolling
your own.

Targets `net8.0`. MIT licensed.
[Source and full documentation](https://github.com/rahibkhan44/DbSignal).
