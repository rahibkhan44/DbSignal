# DbSignal.PostgreSql

**Know what someone else changed — and what the row said before they changed it.**

```csharp
await using var feed = PostgreSqlFeed.For(connectionString)
                                     .Watch("public.products")
                                     .Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    foreach (var table in batch.Tables)
        foreach (var row in table.Rows)
            Console.WriteLine($"{row.Kind}: {row.Before?["name"]} -> {row.After?["name"]}");
```

```
Insert:  -> Widget
Update: Widget -> Widget Mk II
Delete: Widget Mk II ->
```

Built on **logical replication** — not triggers, not polling, not an audit table.

## What it gives you

| | |
|---|---|
| Detail | `RowImages` — the whole row, before and after |
| Delivery | Streaming. The server pushes on commit; nothing waits for a poll interval |
| Transactions | One transaction arrives as one batch. You never see half of one |
| Survives app restart | Yes, via checkpoint (a WAL LSN) |
| Survives downtime | Yes — the replication slot holds the WAL while you are away |
| Setup required | `wal_level = logical`, a publication, `REPLICA IDENTITY FULL`, a slot |
| Privileges | `REPLICATION` to read; ownership or superuser to provision |

## `wal_level` needs a server restart

It is the one setting the provisioner cannot fix for you. `EnsureAsync()` and
`IsProvisionedAsync()` both check it first and say so plainly rather than creating a
publication that will never stream:

```sql
ALTER SYSTEM SET wal_level = logical;   -- then restart the server
```

Everything else — the publication, the replica identity, the slot — the provisioner will
create, or hand you as a script for a DBA who will not grant you the rights:

```csharp
var provisioner = PostgreSqlFeed.For(cs).Watch("public.products").Provisioner();

if (!await provisioner.IsProvisionedAsync())
    Console.WriteLine(provisioner.GetScript());   // runnable SQL, nothing hidden
```

## `REPLICA IDENTITY FULL` is not free

It writes **every column of every `UPDATE`** into the WAL, which is real overhead on wide or
frequently-updated tables. It is also the only reason this package can honestly declare
`RowImages`: under PostgreSQL's default identity an `UPDATE` carries no old row and a
`DELETE` carries only the key.

If you only need to know *which* rows changed, you can skip it and treat the feed as
`KeysChanged` — the trade is that deletes then tell you nothing but the key.

## An abandoned slot will fill your disk

This is the most dangerous operational property of logical replication, and it is not
DbSignal-specific: a replication slot retains WAL **indefinitely** for a consumer that never
comes back. Drop slots you stop using.

```csharp
await provisioner.DropSlotAsync();
```

Give each application its own publication and slot. Two applications sharing one slot steal
each other's changes — whichever reads first advances the position for both.

## Why not Debezium

Debezium does this well and does far more, at the cost of Kafka, Kafka Connect, and a JVM to
run them in. This is a NuGet package and a connection string. If you are already running that
platform, use it; if the question is "did someone else change this row", you should not have
to.

Targets `net8.0`. MIT licensed.
[Source and full documentation](https://github.com/rahibkhan44/DbSignal).
