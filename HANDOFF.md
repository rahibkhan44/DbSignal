# DbSignal — Handoff

**For the next agent.** You are picking up a working .NET library at `C:\Users\hamma\source\repos\DbSignal`. This document is everything: what it is, why it exists, what is done, what is deliberately not done, the traps already paid for, and how to publish it.

Read §6 (Traps) before changing anything. Every item there cost real debugging and is invisible in the code.

**Current state in one line:** two database providers working and proven, 34 tests green, zero warnings, no git history (removed on purpose — see §8).

---

## 1. What this is

A **database-agnostic change-notification library for .NET**. It tells your application when *something else* changed the database — another app, an ERP sync job, a script, a person in a GUI tool.

```csharp
await using var feed = SqlServerFeed.For(connectionString)
                                    .Watch("dbo.Products")
                                    .Build();

await foreach (var batch in feed.ReadAsync(Checkpoint.Now, ct))
    foreach (var table in batch.Tables)
        foreach (var key in table.Keys)
            Console.WriteLine($"{key.Kind} row {key.Values[0]} in {table.QualifiedName}");
```

Switching database is meant to be one line: `SqlServerFeed.For(...)` → `SqliteFeed.For(...)`. Everything else stays.

---

## 2. Why it exists

An application holding data in memory or on a screen cannot tell when another process changed the database. It shows stale data until a human clicks Refresh.

Every serious database *can* report this. **The capability exists everywhere; the abstraction existed nowhere.**

| Existing option | Why it does not close the gap |
|---|---|
| **Debezium** — industry standard | JVM + Kafka Connect. Even "Kafka-less" variants embed the JVM engine. Not shippable inside a .NET app |
| **`SqlDependency`** | SQL Server only. Microsoft: *"not designed for use in client applications"*. **Does not support SQL Server Express** |
| **`SqlTableDependency`** | SQL Server only; builds Service Broker infrastructure it must then clean up |
| **Per-engine clients** (`MySqlCdc`, Npgsql replication) | Excellent — and our building blocks — but four different APIs and mental models |
| **Hand-rolled polling** | What everyone actually does. Reinvented per project, untested, misses retention and restart edge cases |

**The incumbents actively hurt people.** Documented production failures with `SqlDependency`/`SqlTableDependency`: simple `UPDATE`s taking two minutes from query-plan parallelism; Service Broker queries **must not contain `JOIN`s** (one six-table join hung a database badly enough to need a restart); endpoints silently going idle, worked around with keepalive timers; an open issue titled *"SqlTableDependency stopped working after some time"*. Several teams report giving up and standing up Kafka — enormous infrastructure to answer *"did that row change?"*

---

## 3. Prior art — read this before believing the idea is unproven

**`SQLDBEntityNotifier` / `SQLEFTableNotification`** (GitHub: jatinrdave) claims almost exactly this design: multi-database CDC, unified API, SQL Server + MySQL + PostgreSQL.

Measured 2026-08-05: **1,040 downloads across all versions ever**, 17 stars, 36 commits, first release Aug 2025, **last release Sep 2025**, v1.2.0 deprecated for critical bugs a month in, and **no MySQL or PostgreSQL implementation** despite advertising both. For scale, single-engine `SqlTableDependency` has millions of downloads.

**Demand is proven. That attempt did not land.** Six lessons, each already baked into decisions here — do not undo them:

| What it did | Cost | What this library does |
|---|---|---|
| Advertised "advanced CDC", "intelligent routing", "historical replay", "379+ tests" against 36 commits and one working engine | An evaluator checks once and never returns for v3 | **Never claim a provider before its conformance run is green** |
| Polls (`PollForChangesAsync()`) while branded CDC | Mislabelling burns trust in every other claim | Each adapter states exactly what it does |
| Generic over your EF entity (`Service<User>` → `User` objects) | Excludes Dapper/raw ADO — and is backwards, since the writer isn't your EF app | **Zero EF dependency anywhere.** Table/key level only |
| One package pulling `SqlClient` + `EF Core` + `EF Relational` + `MySql.Data` + `Npgsql`, always | Install a phone, receive a fridge | One package per provider; `Abstractions` depends on nothing |
| CLR events (`OnChanged += …`) | No backpressure, no cancellation, handler leaks | `IAsyncEnumerable<ChangeBatch>` |
| Critical-bug deprecation one month in | No safety net | Conformance suite gates everything |

**The moat is not cleverness. It is a green CI badge proving N databases, where N is honest.**

---

## 4. What is DONE

### Repository

```
C:\Users\hamma\source\repos\DbSignal\
├─ DbSignal.sln                       classic .sln (see §6)
├─ Directory.Build.props              nullable, warnings-as-errors, deterministic, SourceLink-ready
├─ Directory.Packages.props           central package management
├─ .gitignore · LICENSE (MIT) · README.md
├─ src/
│  ├─ DbSignal.Abstractions/          net8.0 + netstandard2.0, ZERO dependencies
│  ├─ DbSignal.Sqlite/                Microsoft.Data.Sqlite
│  ├─ DbSignal.SqlServer/             Microsoft.Data.SqlClient
│  └─ DbSignal.Extensions.Hosting/    ⚠️ WRITTEN BUT NOT WIRED — see §7
├─ tests/
│  ├─ DbSignal.Conformance/           the shared suite — the core asset
│  ├─ DbSignal.Sqlite.Tests/          15 tests
│  └─ DbSignal.SqlServer.Tests/       19 tests
└─ samples/
   ├─ DbSignal.Sample.Sqlite/         live demo, --simulate flag
   └─ DbSignal.Sample.SqlServer/      live demo, --simulate and --connection flags
```

### Verified working

```
dotnet build DbSignal.sln     →  0 errors, 0 warnings  (warnings-as-errors is ON)
dotnet test  DbSignal.sln     →  34 passed, 0 failed, 0 skipped
```

- **SQLite** — `PRAGMA data_version`. Detects writes from other processes. Proven manually against DB Browser for SQLite.
- **SQL Server** — Change Tracking. Reports which rows changed and whether it was insert/update/delete. Proven manually against SSMS and LocalDB.
- **Measured latency:** 262 ms average against a 250 ms poll interval → ~12 ms library overhead. `LatencyProbeTests` keeps this honest permanently.

### Public surface

```csharp
public interface IChangeFeed : IAsyncDisposable
{
    FeedCapabilities Capabilities { get; }
    string ProviderName { get; }
    IAsyncEnumerable<ChangeBatch> ReadAsync(Checkpoint from, CancellationToken ct = default);
}

public sealed record ChangeBatch(Checkpoint Position, IReadOnlyList<TableChange> Tables, DateTimeOffset ObservedUtc);
public sealed record TableChange(string Schema, string Name, IReadOnlyList<ChangeKey> Keys, IReadOnlyList<RowImage> Rows);
public sealed record ChangeKey(IReadOnlyList<object?> Values, ChangeKind Kind);
public readonly record struct Checkpoint(string Value);   // opaque, provider-specific
```

Plus `ICheckpointStore`, `IChangeHandler`, `InMemoryCheckpointStore`, and four exception types (`DbSignalException`, `CapabilityNotSupportedException`, `ResyncRequiredException`, `ProvisioningRequiredException`).

---

## 5. The load-bearing design decision — do not undo this

**Do not make every database look the same.** That is the obvious move and it is the trap.

| | SQL Server | SQLite | PostgreSQL | MySQL |
|---|---|---|---|---|
| Mechanism | Change Tracking | `PRAGMA data_version` | logical replication | binlog (`ROW`) |
| Granularity | table + changed keys | **whole database** | table + full row | table + full row |
| Survives app restart | yes | **no** | yes (slot) | yes (GTID/pos) |
| Survives downtime | within retention | **no** | yes — slot retains WAL | within retention |
| Setup required | `ALTER DATABASE` + tables | **none** | `wal_level=logical` | `binlog_format=ROW` |
| Privilege | `ALTER` | none | `REPLICATION` | `REPLICATION SLAVE` |

A uniform API over that must either lie about SQLite or cripple PostgreSQL to SQLite's floor.

**Instead: every feed declares what it can do.**

```csharp
public sealed record FeedCapabilities(
    ChangeDetail Detail,          // DatabaseChanged < TableChanged < KeysChanged < RowImages
    bool DurableAcrossRestart,
    bool SurvivesDowntime,
    bool FiltersOwnWrites,
    bool RequiresProvisioning);
```

and consumers declare what they need:

```csharp
o.RequireAtLeast(ChangeDetail.KeysChanged);   // throws at STARTUP on SQLite
```

**Leaky and declared beats uniform and wrong.** The conformance suite enforces both directions: a provider that over-claims fails, and one that under-delivers fails.

### The conformance suite is the product

`tests/DbSignal.Conformance/ChangeFeedConformance.cs` — one abstract class, six tests, inherited by every provider, run **unbent**. A provider implements three hooks and adds nothing:

```csharp
protected abstract Task<IChangeFeed> CreateFeedAsync();
protected abstract Task WriteAsForeignApplicationAsync();   // separate connection, no ORM
protected virtual Task<bool> IsAvailableAsync();            // skip when no server
```

**If a provider needs a special case in the suite to pass, the abstraction is wrong and the abstraction is what should change.** This rule is why SQLite and SQL Server — which work nothing alike — both pass identically.

---

## 6. Traps already paid for

Each of these cost debugging time and is invisible from the code alone.

### 6.1 `PRAGMA data_version` needs ONE long-lived connection
SQLite's docs: the value is only comparable against a previous reading **from the same connection**. Opening a fresh connection per poll compares two unrelated counters. `SqliteChangeFeed.ReadAsync` holds one connection for the lifetime of the enumeration, with a comment saying so. **Do not "optimise" this into a per-tick connection.**

### 6.2 SQLite declares `FiltersOwnWrites: false` — and that is correct
`data_version` ignores commits on the connection that reads it, which *looks* like own-write filtering. But the feed's connection never writes; your application writes on its own connection, so **your writes do surface**. Declaring `true` would be technically defensible and practically a lie. This was caught by writing the capability honestly and is the design philosophy working on its first provider.

### 6.3 `GetAsyncEnumerator` is lazy — the reader must run BEFORE the write
An async iterator does nothing until the first `MoveNextAsync()`. A test that calls `GetAsyncEnumerator`, writes, then calls `MoveNextAsync` will have the feed take its baseline *after* the change and wait forever. This broke the first latency test. Start the reader in a background task, let it settle, then write. A hosted background service does this naturally.

### 6.4 Conformance must SKIP, never pass vacuously
The suite originally returned early when a database was unavailable — a green tick that tested nothing. That is precisely the failure mode of the prior art. It now uses `Xunit.SkippableFact` + `Skip.IfNot(...)`, so an absent database shows as **skipped** in the output. **Never revert this to an early return.**

### 6.5 Analyzer suppressions, and why each is legitimate
- `tests/Directory.Build.props`: `NoWarn CA1707;CA1711` — CA1707 forbids underscores in member names (right for a library, wrong for tests where `Underscored_Sentence_Names` *are* the documentation); CA1711 forbids type names ending in `Collection`, but xUnit's own `[CollectionDefinition]` convention is exactly that.
- `tests/DbSignal.Conformance/*.csproj`: same `CA1707`, because that project is technically a library and semantically a test suite.
- `ChangeKey.FromValue` is named that, not `Single`, because CA1720 forbids type names in identifiers.

### 6.6 `.slnx` vs `.sln`
SDK 10 creates `.slnx` (new XML format) by default. Deliberately regenerated as classic `.sln` via `dotnet new sln --format sln` so older tooling and CI images still work. **Do not let a regeneration silently switch it back.**

### 6.7 A running sample locks the DLLs
`dotnet run` on a sample holds `DbSignal.Abstractions.dll` etc. A full-solution build then fails with MSB3027 "file is locked by DbSignal.Sample.Sqlite". Stop the sample before building. Not a bug.

### 6.8 SQL Server table names cannot be parameters
`CHANGETABLE(CHANGES <table>, @since)` takes an identifier, not a value. `WatchedTable.QuotedName` doubles embedded `]` so the identifier cannot break out of its brackets. There is a test for a hostile name (`Ev]il`). **Do not replace this with naive interpolation.**

### 6.9 Retention expiry must be loud
If `CHANGE_TRACKING_MIN_VALID_VERSION(...) > lastSeen`, the gap is unreadable. `SqlServerChangeFeed` throws `ResyncRequiredException`. A hand-rolled poller typically finds no rows here and reports "nothing changed" — silent data loss. **This behaviour is the single biggest correctness advantage over a DIY implementation.**

---

## 7. What is LEFT

### 7.1 Hosting extensions — WRITTEN, NOT WIRED ⚠️

`src/DbSignal.Extensions.Hosting/` contains three finished files:
- `DbSignalOptions.cs` — `UseFeed(factory)`, `RequireAtLeast`, checkpoint key, retry/backoff knobs
- `ChangeFeedHostedService.cs` — `BackgroundService` loop, scoped handler dispatch, checkpoint-after-success, exponential backoff, `ResyncRequiredException` handling
- `ServiceCollectionExtensions.cs` — `AddDbSignal(...)`, `AddHandler<T>()`, `UseCheckpointStore<T>()`

**They are NOT in `DbSignal.sln` and have never been compiled.** The owner paused this deliberately. To activate:

```bash
dotnet sln DbSignal.sln add src/DbSignal.Extensions.Hosting/DbSignal.Extensions.Hosting.csproj
dotnet build DbSignal.sln     # expect first-compile errors; nothing has type-checked yet
```

Then add `tests/DbSignal.Extensions.Hosting.Tests/` covering: startup failure when `RequireAtLeast` exceeds capability; checkpoint saved only after handlers succeed; a throwing handler not blocking siblings; backoff on fault.

Design note already decided: **the hosting package is provider-neutral.** It takes `UseFeed(Func<IServiceProvider, IChangeFeed>)` rather than `UseSqlServer(cs)`, so it never references a database driver. Convenience `UseXxx()` methods would force provider→hosting dependencies; defer until someone asks.

### 7.2 CI + green badge — the highest-value remaining item

This is the moat (§3). `.github/workflows/ci.yml`:

```yaml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest        # LocalDB for the SQL Server suite
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet build DbSignal.sln -c Release
      - run: dotnet test  DbSignal.sln -c Release --no-build
```

Alternative for the SQL Server suite on `ubuntu-latest`: a `mcr.microsoft.com/mssql/server:2022-latest` service container, with `DBSIGNAL_SQLSERVER` set to its connection string — `SqlServerTestDatabase` already reads that variable first and falls back to LocalDB. **The fixture is already CI-ready; only the workflow file is missing.**

Then put the badge at the top of the README.

### 7.3 PostgreSQL provider — the real test of the design

SQLite and SQL Server both **poll**. Postgres logical replication **streams**, gives full before/after row images, and survives days of downtime via the replication slot. If the same conformance suite passes unbent against a streaming provider, the abstraction is proven well beyond polling.

**Use logical replication (`pgoutput` via Npgsql), NOT `LISTEN`/`NOTIFY`.** Npgsql only processes notifications during query interaction, so *"if the client is not connected and changes happen, the event will obviously be missed"* — a workstation closed overnight silently loses everything. Logical replication buffers in the WAL and re-sends unacknowledged changes. Requires `wal_level=logical` and `REPLICATION` privilege.

Capabilities: `RowImages`, durable, survives downtime, `RequiresProvisioning: true` (slot creation).

### 7.4 MySQL provider

Binlog via the **`MySqlCdc`** NuGet package (mature; already speaks the replication protocol — do not implement it yourself). Needs `binlog_format=ROW` and `REPLICATION SLAVE` privilege. Note the security caveat: the binlog stream includes changes to **all** databases on the server.

### 7.5 Packaging polish before publishing

- **`Microsoft.CodeAnalysis.PublicApiAnalyzers`** — checks the public surface into `PublicAPI.Shipped.txt` so an accidental break fails the build rather than a customer's upgrade. Deliberately deferred: adding it while the API was still moving would have meant fighting it on every edit. **Now is the right time.**
- **MinVer** — version from the git tag so releases cannot drift from source.
- Per-package `README.md` (the props file already wires `PackageReadmeFile`).
- `RepositoryUrl`/`PackageProjectUrl` in `Directory.Build.props` currently point at a placeholder `github.com/dbsignal/dbsignal` — **update before publishing.**
- Decide the final package name. `DbSignal` is a working name; check NuGet availability first.

### 7.6 Deliberately out of scope for v1
- Transformation / routing / sinks — this is a *notification* library, not ETL. Do not drift toward Debezium's job.
- Exactly-once delivery — not achievable across these mechanisms; at-least-once + idempotent handlers is the honest contract.
- Schema-change (DDL) events — every engine surfaces these differently; own design pass.
- Oracle / MongoDB / Cassandra — the contract should fit them; leave adapters until asked.

---

## 8. How to publish and host

### 8.1 Git — deliberately absent

**There is no `.git` directory. This was intentional.** Three commits existed and were removed at the owner's request because they carried the wrong account identity; the owner intends to publish from a different GitHub account. All files are intact on disk.

```bash
cd C:\Users\hamma\source\repos\DbSignal
git init
git config user.name  "<the right name>"
git config user.email "<the right email>"
git add .
git commit -m "Initial commit — DbSignal v0.1"
git branch -M main
git remote add origin https://github.com/<account>/<repo>.git
git push -u origin main
```

`.gitignore` already excludes `bin/`, `obj/`, `*.db`, `*.nupkg`, `TestResults/`.

### 8.2 Publishing to NuGet

```bash
dotnet pack DbSignal.sln -c Release -o ./artifacts

# Verify BEFORE pushing — the packages are the product
ls ./artifacts                        # expect .nupkg + .snupkg per src project
dotnet nuget push ./artifacts/*.nupkg --api-key <KEY> --source https://api.nuget.org/v3/index.json
```

Get the key from nuget.org → API Keys. Push the `.snupkg` symbol packages too — `Directory.Build.props` already produces them.

**Packages that will be produced:** `DbSignal.Abstractions`, `DbSignal.Sqlite`, `DbSignal.SqlServer` (+ `DbSignal.Extensions.Hosting` once wired). Test and sample projects are `IsPackable=false` and will not be pushed.

### 8.3 Release discipline — the part that decides whether anyone trusts it

- **Never list a provider as supported until its conformance run is green in CI.** Two proven beats four promised. This is the entire lesson of §3.
- **SemVer honestly.** `IChangeFeed` is the product; breaking it is a major version.
- Put the capability matrix and the CI badge at the top of the README, above everything else.
- If a claim cannot be clicked and verified, do not make it.

---

## 9. Verification — run this first, before changing anything

```bash
cd C:\Users\hamma\source\repos\DbSignal
dotnet build DbSignal.sln          # expect: 0 errors, 0 warnings
dotnet test  DbSignal.sln          # expect: 34 passed, 0 failed, 0 skipped
```

If SQL Server tests **skip** rather than pass, no server was found — set `DBSIGNAL_SQLSERVER`, or install SQL Server LocalDB. A skip is honest; a silent pass would not be.

### Manual proof — SQLite

```bash
dotnet run --project samples/DbSignal.Sample.Sqlite
```

Open the printed `sample.db` in DB Browser for SQLite, run an `INSERT`, then click **Write Changes** (it holds a transaction until you do — the query alone commits nothing). The console reacts within ~250 ms.

### Manual proof — SQL Server

```bash
dotnet run --project samples/DbSignal.Sample.SqlServer
```

Connect SSMS to `(localdb)\MSSQLLocalDB` and run:

```sql
USE DbSignalDemo;
INSERT INTO dbo.Products (Name) VALUES ('Hello from SSMS');
UPDATE dbo.Products SET Name = 'Renamed' WHERE Id = 1;
DELETE FROM dbo.Products WHERE Id = 1;
```

Expected output — note it names the row and the operation, which SQLite cannot:

```
[00:45:17] change #1 — version 7
          dbo.Products
            +  Insert  Id=1     Hello from SSMS
            ~  Update  Id=1     Renamed
            -  Delete  Id=1     (row deleted)
```

Both samples accept `--simulate` to write from a second connection, if no GUI tool is handy.

### Environment on the machine this was built on
- .NET SDKs 9.0.315 and 10.0.201/10.0.300; **.NET 8 runtime present** (8.0.27). Projects target `net8.0`.
- **No Docker.** SQL Server tests run against **LocalDB** (`MSSQLLocalDB`), which is enough — Change Tracking is available in every SQL Server edition including Express. CDC is the one needing a bigger edition, and this library deliberately does not use CDC.
