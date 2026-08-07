# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`IChangeFeed` is the product — breaking it is a major version.

## [Unreleased]

## [0.3.0] — 2026-08-07

Packaging and discoverability. **No API changes**; upgrading is safe.

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

[Unreleased]: https://github.com/rahibkhan44/DbSignal/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/rahibkhan44/DbSignal/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/rahibkhan44/DbSignal/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/rahibkhan44/DbSignal/releases/tag/v0.1.0
