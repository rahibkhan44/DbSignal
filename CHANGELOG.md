# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`IChangeFeed` is the product — breaking it is a major version.

## [Unreleased]

## [1.0.2] — 2026-08-27

### Added

- **`DbSignal.PostgreSql`** — a third provider, and the first that streams rather than
  polls. Built on logical replication (`pgoutput`), it declares `ChangeDetail.RowImages`
  and delivers the rows' values **before and after** the change, including the whole row
  for a `DELETE`. One transaction arrives as one `ChangeBatch`, so a consumer never sees
  half of one — something neither polling provider can promise.
- `PostgreSqlProvisioner`, following the established `IsProvisionedAsync` /
  `EnsureAsync` / `GetScript()` shape, plus `DropSlotAsync`. It checks `wal_level`,
  publication coverage, replica identity and the slot, and names whichever is missing.
  `wal_level` is the one it cannot fix — it says so and tells you to restart the server
  rather than creating a publication that would never stream.
- A `postgres` CI job on Linux, so the provider is exercised against a real server on every
  push. SQL Server skips visibly there; PostgreSQL skips visibly on the Windows job.
- `samples/DbSignal.Sample.PostgreSql`.

### Changed

- **The conformance suite now asserts the top detail tiers positively.** Every existing
  check caught a provider claiming *more* than it delivered; none checked a provider
  claiming the *top* tier, because each `detail < …` branch was skipped for it. A feed
  declaring `RowImages` could have returned empty `Keys` and `Rows` forever and passed the
  entire suite. It cannot now.

### Notes

The interesting result is that `PostgreSqlConformanceTests` needed no exemptions — a
streaming feed satisfies the same unmodified contract as two polling ones. That was the
open question with only two providers, and it is why this provider was built next.

Three real bugs were caught by running the suite against a live server rather than
reasoning about the API:

- `Checkpoint.Now` resumed at the replication slot's confirmed position, replaying an
  arbitrary backlog for a caller that asked for *now*. It resolves the current WAL LSN.
- `pgoutput`'s default text mode returned every value as its string form, so an integer
  column read back as `"10"` — which satisfies every shape assertion and breaks the first
  consumer that does arithmetic. The feed now requests binary.
- Under `REPLICA IDENTITY FULL` the server flags *every* column as part of the replica
  identity, so `ChangeKey.Values` held the whole row. The real primary key is read from
  the catalogue instead.

## [1.0.1] — 2026-08-12

Ships the first externally-reported bug fix. **Upgrade if you use
`DbSignal.Extensions.Hosting`** — 1.0.0 can lose changes when a handler throws.

### Fixed
- **`RetryFailedBatches` did not retry, and could skip changes permanently.** When a handler
  threw, the hosted service left the checkpoint alone and continued the same enumeration,
  assuming the feed would offer the position again. It does not: both providers advance their
  cursor before yielding, so the failed batch was never redelivered, and the next successful
  batch saved a checkpoint *past* it. Those changes became unreachable even across a restart,
  which broke the at-least-once guarantee. A failed batch is now redelivered from memory, with
  backoff, and the checkpoint does not move until a handler accepts it.

  The existing test missed this because its feed yielded a single batch and completed, so no
  later batch could advance the checkpoint. Regression tests now cover two batches, a retry
  that eventually succeeds, and the `RetryFailedBatches = false` drop.

  Reported by Ivan Rossouw.

## [1.0.0] — 2026-08-07

**First stable release.** The API has now shipped twice, been consumed by a production
application, and is proven by one conformance suite running unchanged against two engines
that work nothing alike. `IChangeFeed` is stable from here — breaking it means 2.0.0.

No code changes since 0.2.0; this release is packaging and discoverability plus the
commitment that comes with a 1.0.

### Added
- Package icon, shared by all four packages.
- Install instructions and a package table in the README — the package IDs were listed
  but the `dotnet add package` command never appeared.
- This changelog.

### Changed
- **Per-package tags.** Every package previously shipped the same generic tag string, so
  none was findable by the technology it implements. `DbSignal.Sqlite` now carries
  `sqlite`, `DbSignal.SqlServer` carries `sqlserver` / `change-tracking` /
  `sqldependency`, and so on. Tags and the package ID are the only things nuget.org
  search matches on.

## [0.2.0] — 2026-08-06

### Added
- **`DbSignal.Extensions.Hosting`** — `AddDbSignal(...)`, `IChangeHandler`, and a hosted
  service that owns the loop: scoped dispatch per batch, checkpoint saved only *after*
  handlers succeed, capped exponential backoff on fault, and retention expiry surfaced as
  a warning rather than silence. 11 tests.
- Per-package READMEs, so each package renders its own overview on nuget.org.
- NuGet version, download and target-framework badges.
- Release workflow publishing to NuGet via trusted publishing.

### Fixed
- Package push expanded its glob in bash rather than handing it to `dotnet`.

## [0.1.0] — 2026-08-05

First release.

### Added
- **`DbSignal.Abstractions`** — `IChangeFeed`, `ChangeBatch`, `Checkpoint`,
  `FeedCapabilities`, `ICheckpointStore`. Targets `net8.0` and `netstandard2.0`, and
  depends on nothing.
- **`DbSignal.Sqlite`** — detection via `PRAGMA data_version`, which sees writes from
  other *processes*. (`sqlite3_update_hook` cannot: it only fires for the connection it
  was registered on.) 15 tests.
- **`DbSignal.SqlServer`** — detection via Change Tracking, reporting which rows changed
  and whether each was an insert, update or delete. No triggers, no Service Broker, and
  no restriction on query shape. Includes a provisioner that either enables Change
  Tracking or prints the script for a DBA. 19 tests.
- **The conformance suite** — one set of tests every provider inherits and runs unchanged,
  asserting only what that provider's declared capabilities promise. A provider that
  over-claims fails; one that under-delivers fails too.

### Notes
- Delivery is **at-least-once**. Handlers must be idempotent. Exactly-once is not
  achievable across these mechanisms, and claiming it would be untrue.

[Unreleased]: https://github.com/rahibkhan44/DbSignal/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/rahibkhan44/DbSignal/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/rahibkhan44/DbSignal/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/rahibkhan44/DbSignal/compare/v0.2.0...v1.0.0
[0.2.0]: https://github.com/rahibkhan44/DbSignal/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/rahibkhan44/DbSignal/releases/tag/v0.1.0
