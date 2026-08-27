# Distribution playbook

Everything here is ready to post. Nothing in it is automated — each one needs you signed in
somewhere.

## Why this file exists

As of 2026-08-27: **1,616 NuGet downloads, 0 GitHub stars, 1 unique repo visitor in 14 days,
0 referrers.**

Those numbers are not in tension. NuGet's counter includes mirrors, security scanners and CI
restore bots that pull every new package automatically — the tell is that `Sqlite`,
`SqlServer` and `Hosting` sat at 364 / 364 / 358, near-identical, which is machines sweeping
everything rather than humans picking a provider.

The count of humans known to have found this library is **one**: Ivan Rossouw, who arrived
from dev.to and filed a real bug. That is the whole evidence base, and it says something
useful — the single channel that was tried is the one that produced the only real user.

So: downloads are a vanity metric here and should be ignored. **Stars, unique visitors, and
issues filed** are the real ones. One issue from a stranger outweighs a thousand bot pulls,
because it proves somebody ran the code against their own database and cared enough to come
back.

---

## 1. GitHub repo settings — 30 seconds, do this first

Requires the `rahibkhan44` account (the `gh` CLI on this machine is signed in as
`hammadkhandev888`, which can read the repo but not administer it).

**Settings → General → Topics**, paste:

```
dotnet, csharp, database, sql-server, postgresql, sqlite, change-data-capture, cdc, change-tracking, logical-replication, nuget, dotnet-library
```

**Description:**

```
Know when another application changed your database. Change notification for .NET over SQL Server, PostgreSQL and SQLite — no Kafka, no triggers, no polling loop of your own.
```

**Website:** `https://www.nuget.org/packages/DbSignal.Abstractions`

Topics are how GitHub's own search and "explore" surfaces find a repo. Right now the repo is
invisible to both. This is the only item on the list that keeps working while you sleep.

---

## 2. dev.to — the proven channel

`docs/announcement-draft.md` is ready. Set `published: true`, delete the posting-notes
comment block, add a cover image.

**The one thing that would most improve it:** a GIF near the top showing a grid that does
*not* update when a row is inserted from SSMS, then the same insert updating it. That image
carries the argument better than any paragraph, because the reader recognises their own app
in it.

Post it, then delete the draft from the repo.

---

## 3. Reddit — r/dotnet

Post as a **text post**, not a link. Link posts to your own project read as promotion; a text
post that opens with the problem reads as an experience report.

**Title:**

> Your app doesn't know when another program changed your database — so I wrote the library
> that tells it

**Body:** the first three sections of the dev.to post (the stale-grid problem, "every
database can already tell you", "what people actually do instead"), then a link for the rest.
Do not paste the whole article — r/dotnet responds better to a story with a link than to a
wall.

**Post the honest limitation as your own first comment.** It earns more goodwill than any
feature list, and it pre-empts the top critical reply:

> Three providers — SQL Server, PostgreSQL and SQLite — and I only list one once the shared
> conformance suite passes against a real running instance of that database. MySQL is
> designed but not written. Happy to answer anything about the capability model; it's the
> part I'm least sure I got right, and the Postgres provider is what tested whether it
> actually holds.

Best time to post: Tuesday–Thursday, 13:00–16:00 UTC.

---

## 4. Hacker News — Show HN

**Title:**

> Show HN: DbSignal – database change notification for .NET, without Kafka

HN rewards the *engineering* story over the product. Your strongest material for that
audience is not the library — it's **the four bugs the PostgreSQL provider surfaced**,
especially the test-suite hole where a provider claiming the top capability tier was asserted
against by nothing at all. That is a genuinely interesting failure of test design and it is
the kind of thing that thread will engage with.

Lead the first comment with it rather than with a feature list.

---

## 5. Stack Overflow — the channel that compounds

This is the slowest and the most durable. Launch posts scroll away in a day; a good SO answer
is found by people searching the exact problem, for years.

Search these, sort by votes, answer the ones where the accepted answer is outdated or
recommends something now discouraged:

| Query | Why it's a good target |
|---|---|
| `SqlDependency alternative` | Microsoft documents `SqlDependency` as *"not designed for use in client applications"*, and it doesn't work on Express — so most accepted answers are recommending something the vendor advises against |
| `SqlTableDependency not working` | Known production failure modes: Service Broker queries must not contain `JOIN`s, endpoints go idle silently |
| `detect database changes c#` | Broad, high traffic, mostly answered with "use a timer" |
| `entity framework detect external changes` | Exactly the misconception the library exists to correct — EF cannot see writes it didn't make |
| `postgres notify c# listen changes` | `LISTEN`/`NOTIFY` requires a trigger you have to write and maintain; logical replication doesn't |

**Rules that keep this from backfiring:**

- Answer the question that was asked, fully, **without** the library. Explain the mechanism —
  Change Tracking, logical replication, `PRAGMA data_version`. That answer must stand alone
  and be useful to someone who never installs anything.
- Mention the package once, at the end, disclosed: *"I maintain a library that wraps this if
  you'd rather not hand-roll it."* SO requires the disclosure and removes answers without it.
- Never post the same text twice. Duplicated answers get flagged as spam and can cost you the
  account.

One good answer on a question with 40k views beats the entire launch week.

---

## 6. Lists and aggregators

- **`awesome-dotnet`** — open a PR adding it under *Object Relational Mapping* or *Misc*. Read
  the contribution rules first; low-effort PRs get closed.
- **`.NET newsletters`** — The .NET Weekly, Dev Leader Weekly, Milan Jovanović's newsletter.
  Most accept submissions. A one-paragraph email with the link is the whole ask.
- **NuGet package page** — already carries a README, an icon and tags. Nothing to do.

---

## What "working" looks like

Watch these, weekly, and ignore the download counter:

```bash
gh repo view rahibkhan44/DbSignal --json stargazerCount --jq .stargazerCount
gh api repos/rahibkhan44/DbSignal/traffic/views --jq '"views \(.count), uniques \(.uniques)"'
gh api repos/rahibkhan44/DbSignal/traffic/popular/referrers
```

The referrers list is the important one — it tells you which of the channels above is actually
sending people, so you can spend your time on that one instead of guessing.

**Realistic expectation:** a good dev.to post plus an r/dotnet thread is worth roughly 20–60
stars and a handful of real installs. That is not a disappointing outcome; that is what
successful small .NET libraries look like in their first months. The compounding comes from
item 5, slowly.
