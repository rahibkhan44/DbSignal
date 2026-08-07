# DbSignal

[![ci](https://github.com/rahibkhan44/DbSignal/actions/workflows/ci.yml/badge.svg)](https://github.com/rahibkhan44/DbSignal/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DbSignal.Abstractions?logo=nuget&label=nuget)](https://www.nuget.org/packages/DbSignal.Abstractions)
[![Downloads](https://img.shields.io/nuget/dt/DbSignal.Abstractions?label=downloads)](https://www.nuget.org/packages/DbSignal.Abstractions)
[![Targets](https://img.shields.io/badge/targets-net8.0%20%7C%20netstandard2.0-512BD4)](https://www.nuget.org/packages/DbSignal.Abstractions)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**Know when someone else changed your database.**

Your app shows a list. Another program — an ERP sync, a script, a person in a database
tool — changes a row. Your app has no idea, and keeps showing the old value until a human
clicks Refresh.

Every serious database can tell you: SQL Server has Change Tracking, PostgreSQL has
logical replication, MySQL has the binlog, SQLite has `PRAGMA data_version`. The
capability exists everywhere. The abstraction didn't.

## Install

```bash
dotnet add package DbSignal.SqlServer     # or DbSignal.Sqlite
```

```csharp
await using var feed = SqlServerFeed.For(connectionString)
                                    .Watch("dbo.Products")
                                    .Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    foreach (var table in batch.Tables)
        foreach (var key in table.Keys)
            Console.WriteLine($"{key.Kind} row {key.Values[0]} in {table.QualifiedName}");
```

That reports rows changed by **any** writer — another application, an ETL job, someone in
SSMS — not just changes your own code made.

**Switching database is one line.** `SqlServerFeed.For(...)` becomes
`SqliteFeed.For(...)` and nothing else moves:

```csharp
await using var feed = SqliteFeed.For("Data Source=app.db").Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    Console.WriteLine($"Something changed at {batch.ObservedUtc:HH:mm:ss}");
```

Note the second example prints less — deliberately. SQLite can only say *that* the
database changed, and the library won't pretend otherwise. See
[the design decision](#the-design-decision-everything-rests-on).

### Packages

| Package | What it's for |
|---|---|
| [`DbSignal.SqlServer`](https://www.nuget.org/packages/DbSignal.SqlServer) | SQL Server, via Change Tracking |
| [`DbSignal.Sqlite`](https://www.nuget.org/packages/DbSignal.Sqlite) | SQLite, via `PRAGMA data_version` |
| [`DbSignal.Extensions.Hosting`](https://www.nuget.org/packages/DbSignal.Extensions.Hosting) | `AddDbSignal(...)` + a background service that owns the loop |
| [`DbSignal.Abstractions`](https://www.nuget.org/packages/DbSignal.Abstractions) | The contract. Pulled in automatically; reference directly only to implement a provider |

Release history is in [CHANGELOG.md](CHANGELOG.md).

## Status

**Early. Two providers, both proven.**

| Provider | Package | Mechanism | Detail | Status |
|---|---|---|---|---|
| **SQLite** | [![nuget](https://img.shields.io/nuget/v/DbSignal.Sqlite?label=DbSignal.Sqlite)](https://www.nuget.org/packages/DbSignal.Sqlite) | `PRAGMA data_version` | `DatabaseChanged` | ✅ 15 tests green |
| **SQL Server** | [![nuget](https://img.shields.io/nuget/v/DbSignal.SqlServer?label=DbSignal.SqlServer)](https://www.nuget.org/packages/DbSignal.SqlServer) | Change Tracking | `KeysChanged` | ✅ 19 tests green (real LocalDB) |
| PostgreSQL | — | logical replication | `RowImages` | planned |
| MySQL | — | binlog | `RowImages` | planned |

The same conformance suite runs against both, unbent — no provider-specific exemptions.
SQLite polls one integer and can only say "something changed"; SQL Server queries change
tables and names individual rows. Both satisfy one contract.

A provider is listed as working only when the shared conformance suite passes against a
real running instance of that database. Nothing here is claimed on the strength of a
design document.

## Using it in an application

[![nuget](https://img.shields.io/nuget/v/DbSignal.Extensions.Hosting?label=DbSignal.Extensions.Hosting)](https://www.nuget.org/packages/DbSignal.Extensions.Hosting)

```csharp
builder.Services.AddDbSignal(o =>
{
    o.UseFeed(_ => SqlServerFeed.For(connectionString).Watch("dbo.Products").Build());
    o.RequireAtLeast(ChangeDetail.KeysChanged);
})
.AddHandler<ProductCacheInvalidator>();
```

The hosted service owns the loop: it dispatches each batch to your handlers from a fresh DI
scope, **saves the checkpoint only after they succeed**, retries with capped exponential
backoff when the feed faults, and turns retention expiry into a loud warning rather than
silence. One handler throwing does not stop the others.

It references no database driver — `UseFeed` takes a factory, so adding a provider never
widens this package's dependencies.

## The design decision everything rests on

**Databases are not equally good at this, and the library refuses to pretend otherwise.**

| | SQL Server | SQLite | PostgreSQL | MySQL |
|---|---|---|---|---|
| Granularity | table + changed keys | **whole database** | table + full row | table + full row |
| Survives app restart | yes | **no** | yes | yes |
| Survives downtime | within retention | **no** | yes | within retention |
| Setup required | `ALTER DATABASE` | **none** | `wal_level=logical` | `binlog_format=ROW` |

A uniform API over that would have to either lie about SQLite or cripple PostgreSQL down
to SQLite's floor. So instead, every feed **declares what it can do**:

```csharp
public sealed record FeedCapabilities(
    ChangeDetail Detail,
    bool DurableAcrossRestart,
    bool SurvivesDowntime,
    bool FiltersOwnWrites,
    bool RequiresProvisioning);
```

and consumers declare what they need:

```csharp
o.RequireAtLeast(ChangeDetail.KeysChanged);   // "I need to know WHICH rows"
```

Point that at SQLite and **the app refuses to start**, with a message saying why. It does
not quietly do half the job for six months until someone notices.

Leaky and declared beats uniform and wrong.

## Try it

```bash
dotnet run --project samples/DbSignal.Sample.Sqlite -- --simulate
```

Or leave off `--simulate`, open the printed `.db` file in
[DB Browser for SQLite](https://sqlitebrowser.org/), change a row, hit **Write Changes**,
and watch the console react.

## Delivery guarantee

**At-least-once.** A crash between handling a batch and persisting its checkpoint replays
that batch, so **handlers must be idempotent**. Exactly-once is not achievable across
these mechanisms and claiming it would be a lie.

## Not in scope

- **Transformation, routing, sinks.** This reports changes; it is not an ETL pipeline.
  Debezium's job is a different job.
- **Schema-change (DDL) events.** Every engine surfaces these differently; needs its own
  design pass.

## Building

```bash
dotnet build DbSignal.sln
dotnet test DbSignal.sln
```

No Docker needed for the SQLite suite. Provider suites that need a real server skip
cleanly when one isn't available, so the repo stays testable by anyone who clones it.

## Licence

MIT.
