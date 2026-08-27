---
title: Your app doesn't know when another program changed your database
published: false
description: Every database can already tell you when something changed. .NET had no unified way to ask. Here is what I found when I went looking, what I built instead, and the four bugs that only showed up when I ran it against a real server.
tags: dotnet, csharp, database, postgres
cover_image:
canonical_url:
---

<!--
POSTING NOTES — delete this block before publishing.

  1. Set `published: true` when ready.
  2. Add a cover image, and ideally a GIF of the before/after near the top:
     a grid that does not update on an external INSERT, then the same
     INSERT updating it. That single image is worth more than any
     paragraph here.
  3. dev.to allows 4 tags maximum; the four above are chosen.
  4. If cross-posting, publish on your own blog first and set
     canonical_url so the original gets the search credit.

  Cross-post titles:
    r/dotnet   Your app doesn't know when another program changed your
               database — so I wrote the library that tells it
    HN         Show HN: DbSignal – database change notification for .NET,
               without Kafka

  Opening comment for both — lead with the honest limitation, it earns
  more goodwill than a feature list:

    "Three providers — SQL Server, PostgreSQL and SQLite — and I only
     list one once the shared conformance suite passes against a real
     running instance of that database. MySQL is designed but not
     written. Happy to answer anything about the capability model; it
     is the part I am least sure I got right, and the Postgres provider
     is what tested whether it actually holds."

  Then delete this file from the repo.
-->

There's a bug in almost every line-of-business app I've worked on, and most teams never
file it as a bug. It looks like this:

A planner has a product list open. Somewhere else, an ERP sync job inserts a row straight
into the same database. The planner's screen doesn't change. It won't change in five
seconds, or five minutes. It changes when they click Refresh — and the only reason they
ever click Refresh is that they've learned, over months, not to trust what they're looking
at.

I hit this on a manufacturing scheduling product. Heavy .NET desktop application, several
workstations, one shared SQL Server, and an ERP integration that writes to that database
directly. Every workstation was showing data that was minutes or hours stale, and the
official answer was "click the refresh button."

The interesting part isn't the bug. It's that **the database already knew**, and there was
no reasonable way to ask it.

## Every database can already tell you

- **SQL Server** — Change Tracking, built into the engine, all editions
- **PostgreSQL** — logical replication
- **MySQL** — the binlog
- **SQLite** — `PRAGMA data_version`

Four mature, documented mechanisms. The capability exists everywhere.

The abstraction didn't. And that gap is why teams keep reinventing this badly.

## What people actually do instead

**A timer.** Reload the grid every thirty seconds. Works, in the sense that a stopped clock
works twice a day. Burns queries, and the user still sees stale data for up to thirty
seconds — during which they might act on it.

**A cooperative scheme.** The app stamps a "something changed" row on every write, and other
instances poll that row. This is elegant right up until a *second* application touches the
database, at which point it silently stops working. Ours did exactly this. An ERP writing
directly stamped nothing, so no workstation ever learned. Nothing errored. It just quietly
never happened.

That scheme had a second cost I only noticed when I costed up what replacing it would take:
`ExecuteUpdate` and `ExecuteDelete` **bypass EF Core interceptors entirely**. So every
set-based write had to remember to stamp by hand. That codebase had 19 manual stamp calls
across 26 bulk-write sites, and a 673-line convention test whose entire job was catching
the ones developers forgot — hundreds of lines of machinery to work around one thing EF
doesn't intercept.

Change Tracking sits inside the database engine, so it sees those writes regardless. That
whole category of "did you remember to stamp?" review burden stops being possible.

**`SqlDependency` / `SqlTableDependency`.** The answers you'll find first, and the ones with
the worst production stories. From documented reports:

- Simple `UPDATE`s taking **two minutes**, from unexpected parallelism in the query plan
- Service Broker queries **must not contain `JOIN`s** — an easily-missed rule. One query
  joining six tables hung a database badly enough to need restarting both the service and
  the server
- Endpoints silently going idle, worked around with keepalive timers
- `SqlDependency` **isn't supported on SQL Server Express** at all
- Microsoft's own documentation: *"not designed for use in client applications"*

More than one team has ended up standing up Kafka. That is an enormous amount of
infrastructure to answer *"did that row change?"*

**Debezium** is the real answer at scale, and it's excellent — but it's JVM plus Kafka
Connect. Even the "Kafka-less" options embed Debezium's JVM engine. There is nothing you
can ship inside a .NET desktop app.

## Someone had already tried this

After I built mine, I went looking — as you should, in that order or the other, but
definitely at some point.

There **is** a .NET package advertising exactly this: multi-database CDC, unified API, SQL
Server + MySQL + PostgreSQL. Which was a genuinely uncomfortable thing to find.

Then I looked closer. About a thousand downloads, dormant since September 2025, one version
pulled for critical bugs a month after launch — and no MySQL or PostgreSQL implementation
at all. Those two engines existed only in the README.

I want to be fair to it: **that project is evidence the need is real.** Someone else felt
the same gap and started building the same shape. That's a signal, not a warning.

But it taught me four things I did differently:

1. **It polls while calling itself CDC.** The method is literally `PollForChangesAsync()`.
   Polling is fine — I poll too. Mislabelling it isn't, because when someone opens the code
   and finds a timer, they stop believing everything else on the page.
2. **It's welded to Entity Framework.** The API is generic over your EF entity. Which is
   backwards: the whole reason you need change notification is that *something that isn't
   your EF app* wrote the data.
3. **One package pulls every driver.** `SqlClient` *and* `EF Core` *and* `EF Relational`
   *and* `MySql.Data` *and* `Npgsql` — always. It ships drivers for providers it never
   implemented.
4. **It advertised four databases and shipped one.** A developer evaluates it, discovers
   that, closes the tab — and never comes back to check version 3.

## The design decision everything else follows from

Here's the temptation: make every database look the same. One interface, four
implementations, done.

It's a trap, and this table is why:

| | SQL Server | SQLite | PostgreSQL | MySQL |
|---|---|---|---|---|
| Granularity | table + changed keys | **whole database** | table + full row | table + full row |
| Delivery | polled | polled | **pushed on commit** | pushed |
| Survives app restart | yes | **no** | yes | yes |
| Survives downtime | within retention | **no** | yes | within retention |
| Setup required | `ALTER DATABASE` | **none** | `wal_level=logical` | `binlog_format=ROW` |

SQLite's `PRAGMA data_version` is a single integer for the entire file. It cannot tell you
which table changed, let alone which row, and it means nothing after a restart. PostgreSQL
logical replication gives you the row before and after, and will hold changes for days
while your app is offline.

A uniform API over those two has to either **lie about SQLite** or **cripple PostgreSQL
down to SQLite's floor**.

So I did neither. Every provider declares what it can actually do:

```csharp
public sealed record FeedCapabilities(
    ChangeDetail Detail,           // DatabaseChanged < TableChanged < KeysChanged < RowImages
    bool DurableAcrossRestart,
    bool SurvivesDowntime,
    bool FiltersOwnWrites,
    bool RequiresProvisioning);
```

And the consumer declares what it needs:

```csharp
o.RequireAtLeast(ChangeDetail.KeysChanged);   // "I need to know WHICH rows"
```

Point that at SQLite and **the application refuses to start**, with a message naming both
tiers. It does not quietly do half its job for six months until somebody notices the
numbers are wrong.

That's the whole idea: **leaky and declared beats uniform and wrong.**

The abstraction admits it's leaky. In exchange, the leak is a compile-time-ish contract
instead of a production surprise.

### It caught its own first lie

I originally marked SQLite as `FiltersOwnWrites: true`. `data_version` genuinely does ignore
commits made on the connection that reads it, so it looked correct.

It isn't. The feed's connection never writes — your application writes on its *own*
connection. So your writes **do** surface. Declaring `true` would have been technically
defensible and practically a lie.

Writing capabilities down honestly forced me to notice that on the very first provider.

## Proving it, rather than claiming it

The core asset isn't any provider. It's a **conformance suite**: one set of tests every
provider inherits and runs **unchanged**, asserting only what that provider's declared
capabilities promise.

A provider implements three hooks and adds nothing:

```csharp
protected abstract Task<IChangeFeed> CreateFeedAsync();
protected abstract Task WriteAsForeignApplicationAsync();  // separate connection, no ORM
protected virtual  Task<bool> IsAvailableAsync();          // skip when no server
```

The rule that keeps it honest: **if a provider needs a special case in the suite to pass,
the abstraction is wrong and the abstraction is what changes.**

It tests in both directions. A provider that claims more than it delivers fails. A provider
that quietly delivers less than it declared also fails. And when a database isn't available,
the tests **skip visibly** rather than passing vacuously — because a green tick that tested
nothing is exactly how a README ends up claiming four databases and shipping one.

SQLite polls one integer and can only say "something changed." SQL Server queries change
tables and names individual rows. PostgreSQL holds a replication connection open, is pushed
each transaction as it commits, and reports every column before and after. **All three pass
the identical suite with no exemptions.** That's the evidence the contract holds.

The third one is the one that actually tested it. Two polling providers passing the same
tests proves less than it looks like — they have the same shape. A *streaming* provider
satisfying the same unmodified contract is what tells you the abstraction wasn't quietly
built around polling.

Measured detection latency on SQL Server: **262ms against a 250ms poll interval** — about
12ms of library overhead. There's a permanent test asserting that, so if anyone later adds
per-change work that pushes it into seconds, it fails rather than being noticed by a human
watching a console.

## The provider that tried to prove me wrong

With two providers, "the abstraction is sound" was really "the abstraction fits two things
that work the same way." Both polled. Both said *something changed, go look.* Nothing had
tested the parts of the contract written for a database that pushes.

PostgreSQL logical replication is the opposite shape in every respect. The server streams
each transaction as it commits. It carries **every column, before and after** — including
the whole row for a `DELETE`, which no polling provider can give you, because by the time
you go and look the row is gone. And a replication slot retains changes on the server while
your app is offline, so downtime survival is real rather than best-effort.

It passed the conformance suite with no exemptions. That was the result I wanted.

**It also broke in four ways I would have shipped.**

Before writing it, I found a hole in my own test suite. Every detail check was written as
`if (detail < tier) assert nothing at that tier`. That catches a provider claiming *more*
than it delivers — but a provider claiming the **top** tier skips every branch and is
therefore asserted against by *nothing at all*. A feed declaring `RowImages` could have
returned empty rows forever and passed all six tests.

I added the positive direction. The next three bugs are the ones it caught:

**`Checkpoint.Now` replayed history.** Postgres's slot is a backlog by design, so resuming
"at the slot" means resuming at the oldest thing it still holds. A caller asking for *now*
got an arbitrary amount of the past. It reads the server's current WAL position instead.

**Every value came back as a string.** pgoutput defaults to text mode, so an integer column
arrived as `"10"`. That satisfies every structural assertion — it's non-null, it's the right
column, the test is green — and breaks the first consumer that does arithmetic on it. One
flag fixes it. Nothing but a typed assertion would ever have found it.

**The primary key was the entire row.** To get before-images you must set `REPLICA IDENTITY
FULL`, and under that setting Postgres marks *every* column as part of the replica identity.
So the "changed row's key" contained all of it. On a two-column demo table you'd never
notice; on a real one, code reading `Values[0]` gets the right answer by luck.

None of the three are exotic. All three are the kind that pass review, pass a mocked test,
and surface as someone else's incident months later. They were caught because the suite ran
against a real PostgreSQL rather than a fake — which is the same reason the project's rule
is that a provider isn't listed until it does.

Two costs worth stating plainly, because I'd rather you read them here than discover them:
`REPLICA IDENTITY FULL` writes every column of every `UPDATE` into the WAL, which is a real
write-throughput tax on wide tables. And an abandoned replication slot retains WAL
**indefinitely** and will eventually fill the disk — that's the same mechanism that makes
downtime survivable, pointed the wrong way. One slot per application, and drop it when the
application is retired.

## Back to the scheduling app

The integration was one new class. It ends at the existing cache-refresh dispatcher, so
every cache, every bound collection and every notification handler downstream stayed
untouched. Behind a config switch, off by default.

Then the demo: insert a row from SSMS with the switch off — nothing, forever. Click Refresh
— the row appears, proving the data was always there and only the *signal* was missing.
Relaunch with the switch on, same insert — the grid updates in about a quarter of a second,
nobody touching anything.

Every existing test passed unmodified, because nothing was deleted. That mattered more than
the latency: I wasn't asking anyone to approve a rewrite.

One detail I liked. Change Tracking works on *tables*; the app's cache map worked on *CLR
types*. I resolved table names through EF metadata rather than guessing from class names —
and five entities would have been silently mis-watched otherwise. One was mapped to a
**singular** table because it had no `DbSet`, so EF fell back to the type name. An unwatched
table produces no error. Just a screen that never updates. Which is the bug I started with.

## Where it is

[**DbSignal**](https://github.com/rahibkhan44/DbSignal) — MIT, on NuGet.

```bash
dotnet add package DbSignal.SqlServer     # or DbSignal.PostgreSql, or DbSignal.Sqlite
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

On PostgreSQL the same loop gives you the values, not just the keys:

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

**Three providers, all proven.** SQL Server, PostgreSQL and SQLite. MySQL is designed and
not written — and it stays off the list until the conformance suite passes against a real
running instance of it.

Three proven beats four promised. I learned that from someone else's README.

---

### Notes

- Delivery is **at-least-once**. Handlers must be idempotent. Exactly-once isn't achievable
  across these mechanisms and claiming it would be untrue.
- No EF Core dependency anywhere — Dapper and raw ADO work fine.
- One package per provider. Install SQLite support, get a SQLite driver. Nothing else.
- CI runs the suite against real databases on every push: LocalDB on a Windows job,
  `postgres:17` on a Linux one. Where a database isn't present the tests **skip visibly**
  rather than passing quietly, so a green build never means "we tested nothing."

If you try it and it breaks, please open an issue. The one external bug report this has had
so far was worth more than every download counter on the page.
