# Postgres backend SIGPIPE crash — diagnosis design

A Postgres **backend** in the dev stack dies of `signal 13: Broken pipe`, which forces the postmaster
to terminate every other backend and reinitialize. Four occurrences are on record. Each one severs
in-flight gRPC calls, and one of them refused a 42-minute benchmark run.

The cause is **unknown**. This spec does not guess at it and does not fix it. It buys the one piece
of evidence that every downstream decision depends on: **which service and database owned the backend
that died.**

## 1. The problem

```
LOG:  server process (PID 139873) was terminated by signal 13: Broken pipe
LOG:  terminating any other active server processes
LOG:  all server processes terminated; reinitializing
LOG:  database system was not properly shut down; automatic recovery in progress
FATAL:  the database system is in recovery mode      <- every concurrent connection
```

Recorded occurrences, all `signal 13`:

| # | When (UTC) | Dying PID | Benchmark running? |
|---|---|---|---|
| 1 | 2026-09-08 13:28:18 | 92587 | unknown |
| 2 | 2026-09-09 13:28:59 | 139873 | yes |
| 3 | 2026-09-09 13:30:10 | 139928 | yes |
| 4 | 2026-09-09 14:56:55 | 142842 | **no** |

The container never restarts (`RestartCount = 0`, `StartedAt` days earlier), so `docker ps` reports it
healthy throughout and only the Postgres log records the event.

**Why this is abnormal rather than routine.** Postgres sets `SIGPIPE` to `SIG_IGN` in backends
precisely so that a vanished client cannot kill one. A backend actually dying of signal 13 means
something reset that disposition. Nothing has been confirmed about what.

**Why it matters to Iverson.** The API-side failure is a gRPC `StatusCode=Unknown`, whose server
stack is `Npgsql EndOfStreamException -> PostgresRepository.QuerySingleOrDefaultAsync ->
TenantStatusCache.GetStatusAsync -> ActingUserInterceptor` — the call dies in the **acting-user
interceptor, before the search runs**, so it can hit any RPC regardless of the query. On 2026-09-09
this cost 2 of 1344 RPCs, one of them a `SearchChunks`, which silently truncated a chunk-hit dump to
671/672 queries. The harness's fail-closed guard refused the run; that guard is the only thing that
stood between this crash and a believed-but-wrong measurement.

**Two observations recorded as evidence to test a future answer against — deliberately NOT used to
shape this design.** Crashes 1 and 2 fall within 41 seconds of the same wall-clock minute on
consecutive days, which would be consistent with something periodic. Crash 4 occurred with no
benchmark running, which is evidence against "benchmark load causes it" — a framing this document's
author initially held on a sample of two.

## 2. Goal and non-goals

**Goal.** Answer one question: which user and database owned the dying backend.

This is the largest branch in the diagnosis. **Authentik shares this Postgres instance** (its own
`authentik` database and role), and a postmaster reset kills every backend in every database — so the
process that died may not be an Iverson one at all, and the Iverson RPC failures may be pure
collateral. Nothing currently on disk distinguishes these two worlds, and they lead to completely
different responses.

**Non-goals.** Fixing the crash. Identifying what reset the signal handler. Statement-level capture.
Core dumps. Separating Authentik onto its own instance. Each is a consequence of the answer, not a
prerequisite to getting it.

## 3. Why the answer is not already available

`log_line_prefix` is `%m [%p] ` — timestamp and PID only. When the postmaster names the dying PID
there is no user, database, or application on any line to map it back to. `log_connections` and
`log_disconnections` are both `off`, so no connect-time record exists either. That single gap is why
four crashes have produced no diagnosis.

## 4. The change

Three settings, applied with `ALTER SYSTEM SET` followed by `SELECT pg_reload_conf()`:

| Setting | From | To |
|---|---|---|
| `log_line_prefix` | `%m [%p] ` | `%m [%p] %q%u@%d ` |
| `log_connections` | `off` | `on` |
| `log_disconnections` | `off` | `on` |

`%q` suppresses the user/database portion for non-session processes, so postmaster and auxiliary
lines are unchanged. `%a` is deliberately absent: nothing in the codebase or compose sets
`Application Name`, so it would be empty for every client.

**No restart, and no container recreate.** This matters beyond convenience: the live compose
containers were created from worktree paths that no longer exist, so a tier-wide `up` from main
would recreate `postgres` and `authentik-server`
(`docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §14). Only single-service
`--no-deps` actions are safe, and this design needs none of them.

### 4.1 Ordering precondition — apply while no client is connected

`log_connections` and `log_disconnections` are **`superuser-backend`**, not `sighup`. `ALTER SYSTEM`
plus a reload does set them, but they take effect only for connections **established after the
reload**. Iverson pools its connections, so applying this to a running stack would leave every
already-open backend permanently unlogged — and those long-lived backends are exactly the population
most likely to be present when a crash occurs.

Therefore:

1. `docker start iverson-postgres` — alone, no recreate
2. Apply the three settings and `SELECT pg_reload_conf()`
3. Only then bring the rest of the stack up

Every client connection is then post-reload and logged. If this order is not followed, the design's
success criterion fails silently on the connections that matter most.

**If the stack is already up when this is applied,** the settings still take, but only for
connections opened afterwards. Bring the client services down first (single-service `--no-deps`
actions only, per §4 — never a tier-wide `up`), apply, then bring them back. Do not skip this on the
grounds that the pool will churn on its own: a busy pool can hold a connection open indefinitely, and
an unlogged backend is indistinguishable from a logged one until the moment it dies, at which point
the evidence is already lost.

`log_line_prefix` is `sighup` and applies to all backends immediately, so it is unaffected by this
constraint.

### 4.2 Reversal

`ALTER SYSTEM RESET log_line_prefix;` (and the other two), then `SELECT pg_reload_conf()`.
`postgresql.auto.conf` lives in the `postgres_data` named volume, so these settings survive container
restart **and recreate**. Restarting does not undo them; only an explicit `RESET` does.

## 5. Procedure when the next crash occurs

1. `docker logs iverson-postgres 2>&1 | grep -n "terminated by signal"` — take the PID.
2. Search backwards for that PID's `connection authorized: user=… database=…` line.
3. Record: crash timestamp, PID, user, database, the client `host=…port=…` from the connection line,
   every other line that PID emitted, and whether a benchmark was running.

## 6. Decision tree

| Answer | What it means | Next |
|---|---|---|
| `authentik@authentik` | Not an Iverson defect. Iverson's exposure is that it shares an instance with a crashing tenant. | The §7 mitigation becomes Iverson's primary response; separately decide whether to isolate Authentik's database. |
| `iverson@iverson` | Ours. | Escalate to core dumps for a stack trace naming what reset the handler. Statement logging only if a specific statement is suspected, and never during a measurement campaign. |
| PID unmappable | The instrumentation was insufficient — most likely §4.1's ordering was not followed. | Fix that before spending anything further. |

## 7. Mitigation fallback

Implemented **only if** the cause proves to be outside our control (an upstream Postgres or Authentik
defect): a single retry in `TenantStatusCache.GetStatusAsync` on Npgsql's broken-connection exception
classes.

This is measurement-safe for a specific reason: it runs in `ActingUserInterceptor` **before the
search executes**, so a retry cannot change a candidate set or bias a ranking. Retrying a *search*
would not have that property.

**Explicitly out of scope, permanently:** adding retry or failure tolerance to `benchmark-query`'s
RPC-failure guard. That guard is what caught the truncated run. Weakening it to survive an
infrastructure fault is how a corrupt run becomes a believed number.

## 8. Success criterion

One crash observed with its dying PID mapped to a user and a database. Nothing more. If that answer
arrives and the cause is still unknown, this spec has succeeded.

## 9. Verified assumptions

Verified 2026-09-09 against the live container (started alone for verification, then returned to its
stopped state; **no settings were changed** — `log_line_prefix` re-confirmed as `%m [%p] ` afterwards).

| # | Assumption | Evidence | Result |
|---|---|---|---|
| 1 | `log_line_prefix` is `sighup` | `pg_settings`: context `sighup` | ✅ |
| 2 | `log_connections` is `sighup` | `pg_settings`: context **`superuser-backend`** | ❌ **failed — forced §4.1** |
| 3 | `log_disconnections` is `sighup` | `pg_settings`: context **`superuser-backend`** | ❌ **failed — forced §4.1** |
| 4 | `ALTER SYSTEM` applies to all three | none is `postmaster`/`internal` context | ✅ |
| 5 | The role can `ALTER SYSTEM` | `pg_roles`: `iverson` `rolsuper = t` | ✅ |
| 6 | `pg_reload_conf()` applies them | `sighup` for #1; #2/#3 per §4.1's constraint | ✅ with constraint |
| 7 | `ALTER SYSTEM RESET` reverses | standard; auto.conf is the only source | ✅ |
| 8 | Current prefix lacks `%u`/`%d` | `show log_line_prefix` = `%m [%p] ` | ✅ |
| 9 | auto.conf is in the volume | `data_directory` = `/var/lib/postgresql/data`, the `postgres_data` mount | ✅ |
| 10 | `log_connections` names PID + user/db | PG16 `connection authorized: user=… database=…` | ✅ |
| 11 | `%a` is useful | **no** `Application Name` anywhere in code or compose — it would be empty | ❌ **dropped from the design** |
| 12 | The crash line carries the PID | 4 recorded instances all do | ✅ |
| 13 | Log retention outlives the crash interval | driver is **journald** (not json-file); oldest entry 2026-08-25 ≈ 15 days; crash rate 1–2/day | ✅ with §10 residual |
| 14 | Iverson connects as `iverson`/`iverson` | compose `POSTGRES_USER/DB` | ✅ |
| 15 | Authentik connects as `authentik`/`authentik` | `deploy/postgres/init-authentik-db.sql` | ✅ |
| 16 | The role set is closed | `pg_roles`: exactly `iverson` (super) and `authentik` | ✅ |
| 17 | Nothing consumes the log format | no log parser in the repo; Prometheus scrapes only `iverson-api:8081` | ✅ |
| 18 | `TenantStatusCache.GetStatusAsync` is on the crash path | server stack trace in `docker logs iverson-api` | ✅ |
| 19 | Npgsql distinguishes a severed connection | observed `NpgsqlException` wrapping `EndOfStreamException` | ✅ — to be confirmed before §7 is built |

## 10. Known issues, accepted as out of scope

- **User + database cannot distinguish the API from the worker.** Both connect as `iverson@iverson`.
  The `host=…port=…` on the `log_connections` line maps to a container IP via `docker inspect`, which
  is expected to be sufficient. If it is not, setting `Application Name` on the connection strings is
  the fix — a code change plus a service restart, not needed to answer the primary fork.
- **Journald is near its default size cap** (3.9G in use) and rotating. Retention is ample now, but
  would compress sharply if log volume rose — a further reason statement logging stays off.
- **Assumption 19 is confirmed only by an observed instance**, not by a survey of Npgsql's exception
  taxonomy. It must be confirmed properly before §7 is implemented, not before this spec lands.
- **The design cannot rule out that a crash occurs with no logged connection for its PID** if §4.1's
  ordering is broken by a future stack restart that reloads settings while clients are attached. The
  §6 "unmappable" branch exists for that case.
