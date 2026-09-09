# Postgres backend SIGPIPE crash — diagnosis design

A Postgres **backend** in the dev stack dies of `signal 13: Broken pipe`, which forces the postmaster
to terminate every other backend and reinitialize. Four occurrences are on record. Each one severs
in-flight gRPC calls, and one of them refused a 42-minute benchmark run.

The cause is **unknown**. This spec does not guess at it and does not fix it. It buys the evidence
every downstream decision depends on: **what the dying backend was.**

## 1. The problem

```
LOG:  server process (PID 139873) was terminated by signal 13: Broken pipe
LOG:  terminating any other active server processes
LOG:  all server processes terminated; reinitializing
LOG:  database system was not properly shut down; automatic recovery in progress
FATAL:  the database system is in recovery mode      <- every concurrent connection
```

| # | When (UTC) | Dying PID | Recovery window | Benchmark running? |
|---|---|---|---|---|
| 1 | 2026-09-08 13:28:18 | 92587 | 8.3 s | unknown |
| 2 | 2026-09-09 13:28:59 | 139873 | 2.4 s | yes |
| 3 | 2026-09-09 13:30:10 | 139928 | 3.8 s | yes |
| 4 | 2026-09-09 14:56:55 | 142842 | 5.7 s | **no** |

The container never restarts (`RestartCount = 0`), so `docker ps` reports it healthy throughout and
only the Postgres log records the event.

**Why this is abnormal rather than routine.** Postgres sets `SIGPIPE` to `SIG_IGN` in backends
precisely so that a vanished client cannot kill one. A backend dying of signal 13 means something
reset that disposition. Nothing has been confirmed about what.

**Why it matters to Iverson.** The API-side failure is a gRPC `StatusCode=Unknown`, whose server
stack is `Npgsql EndOfStreamException -> PostgresRepository.QuerySingleOrDefaultAsync ->
TenantStatusCache.GetStatusAsync -> ActingUserInterceptor` — the call dies in the acting-user
interceptor, **before the search runs**, so it can hit any RPC regardless of the query. On
2026-09-09 this cost 2 of 1344 RPCs, one a `SearchChunks`, which silently truncated a chunk-hit dump
to 671/672 queries. The harness's fail-closed guard refused the run; that guard is the only thing
that stood between this crash and a believed-but-wrong measurement.

**Two observations recorded as evidence to test a future answer against — deliberately NOT used to
shape this design.** Crashes 1 and 2 fall within 41 seconds of the same wall-clock minute on
consecutive days. Crash 4 occurred with no benchmark running, which is evidence against "benchmark
load causes it" — a framing this document's author initially held on a sample of two.

## 2. Goal and non-goals

**Goal.** Identify the dying backend: first *what class of process* it was, and if it was a client
backend, *which client*.

The class question comes first because it is the largest branch. Not every process the postmaster
reports as `server process` is a client backend — an autovacuum worker is reported with identical
wording (§9 #12). And **Authentik shares this instance** with its own role and database, so even
among client backends the crash may not be Iverson's at all, and the Iverson RPC failures may be
pure collateral.

**Non-goals.** Fixing the crash. Identifying what reset the signal handler. Core dumps. Separating
Authentik onto its own instance. Each is a consequence of the answer, not a prerequisite to it.

## 3. Why the answer is not already available

Every one of the four dying PIDs emitted **zero** log lines of its own. The postmaster names a bare
PID; `log_line_prefix` is `%m [%p] `, carrying no user or database; `log_connections` is `off`, so no
connect-time record exists; and no snapshot of `pg_stat_activity` is retained. There is nothing on
disk to map a PID to anything. That is why four crashes have produced no diagnosis.

## 4. The mechanism

**A `pg_stat_activity` identity ledger is the primary evidence. The log settings are a complement
that closes its sampling gap.**

The view states as data what a log-only approach must infer: `backend_type` names the process class
outright, `client_addr`/`client_port` identify the peer, `usename`/`datname` the principal, and
`query` what it was running — the last of which statement logging would have cost heavily to obtain.
Critically, the view also covers backends that were **already open** before instrumentation started,
which no `log_connections` setting can do.

### 4.1 The ledger (primary)

A poller reads `pg_stat_activity` once per second and appends to a file **only rows whose
`(pid, backend_start)` it has not already recorded**:

```
now(), pid, backend_start, backend_type, usename, datname,
client_addr, client_port, application_name, state, query
```

- **Append-on-new, not snapshot-per-tick.** Measured in a smoke test, 5 ticks produced 30 rows but
  only 10 distinct `(pid, backend_start)` pairs. At the observed fork rate (~34/min) the ledger grows
  at roughly the fork rate — order 5 MB/day — instead of ~260 MB/day for full snapshots.
- **`(pid, backend_start)`, not `pid`.** `backend_start` is immutable per backend (§9 #3). PID
  wraparound at `pid_max` 4,194,304 and ~34 PIDs/min takes ~86 days, so reuse is not a practical risk
  over a days-long watch; the composite key is cheap insurance, not a necessity.
- **The poller sets `application_name=sigpipe-poller`** so its own connections are self-evident and
  never mistaken for application traffic.
- **It reconnects every tick**, so a postmaster recovery needs no special handling: the failing ticks
  simply error and the loop continues.

**No `ALTER SYSTEM` and no ordering precondition.** The ledger sees backends that already exist, so
there is nothing to sequence and no requirement about when the stack is started. This is the single
biggest reason to prefer it.

### 4.2 The container address map (required, not optional)

Container IPs are assigned dynamically — `docker-compose.yml` declares no static addresses — so an
address recorded at crash time **cannot be resolved after a restart**. The poller must therefore also
record, periodically, a `container name -> IP` map for the running stack. Without it, a `client_addr`
captured today is unresolvable tomorrow.

This also bounds a limit the design must not overstate: a **host-originated** connection through the
published port 5432 arrives NAT'd onto the same container subnet — measured as `client_addr=10.89.0.9`,
`client_port=52400` — so it is **not** distinguishable from a container address by shape. It is
distinguishable only by *absence from the contemporaneous map*. §6 relies on that, not on the address
looking different.

### 4.3 The log complement

The ledger's one real weakness is a sampling gap: a backend that forks **and** dies inside one poll
interval never appears. At ~34 forks/min, much of it short-lived, this is not hypothetical. Three
settings close it for client backends, applied with `ALTER SYSTEM SET` + `SELECT pg_reload_conf()`:

| Setting | From | To |
|---|---|---|
| `log_line_prefix` | `%m [%p] ` | `%m [%p] %q%u@%d ` |
| `log_connections` | `off` | `on` |
| `log_disconnections` | `off` | `on` |

`log_connections` records a client backend's identity at connect time exactly, with no sampling gap.
`%q` is intended to suppress the user/database portion for non-session processes, leaving postmaster
and auxiliary lines unchanged. **This is not verified here** — confirming it needs `elog.c`, which the
container image does not ship — and nothing in this design rests on it: §5 and §6 discriminate on the
presence or absence of the connection lines and the ledger row, never on the prefix's shape. `%a` is omitted: nothing in the codebase or compose sets `Application Name`
(§9 #14), so it would be empty for every client except the poller.

**These are a complement, and their limitation is now tolerable rather than fatal.**
`log_connections` and `log_disconnections` are `superuser-backend`, not `sighup` (§9 #2), so they
take effect only for connections established after the reload. In the log-primary design that forced
a brittle stack-ordering precondition. Here, a backend they miss is still in the ledger, so the
consequence is degraded redundancy rather than an unanswerable crash. Apply them whenever convenient;
applying while the stack is down (its current state) simply maximises their coverage.

`log_line_prefix` is `sighup` and applies to every backend immediately, with no such caveat.

### 4.4 Reversal

Stop the poller and delete its files. For the settings, `ALTER SYSTEM RESET log_line_prefix;` (and
the other two), then `SELECT pg_reload_conf()`. `postgresql.auto.conf` lives in the `postgres_data`
named volume, so these survive container restart **and recreate**; restarting does not undo them,
only an explicit `RESET` does.

**Never run a tier-wide `docker compose up`.** 12 of the 19 `iverson-*` containers were created from
working directories that no longer exist — `iverson-api` among them — so a tier-wide `up` would
recreate them. `postgres` and `authentik-server` are among the 7 that were not. Measured 2026-09-09
via `docker inspect --format '{{index .Config.Labels "com.docker.compose.project.working_dir"}}'`.
Every container needed here already exists, so `docker start <name>` restores it without creating
anything.

## 5. Procedure when the next crash occurs

1. `docker logs iverson-postgres 2>&1 | grep -n "terminated by signal"` — take the PID and timestamp.
2. Look the PID up in the ledger, taking the row whose `backend_start` is the latest at or before the
   crash. Record `backend_type`, `usename`, `datname`, `client_addr`, `client_port`,
   `application_name`, `state` and `query`.
3. If `client_addr` is set, resolve it against the container map (§4.2) **as recorded nearest the
   crash time**, not against the current stack.
4. If the PID is absent from the ledger, fall back to the log: search backwards for its
   `connection received` and `connection authorized` lines.
5. Record whether a benchmark or ingest was running, and whether the stack had been restarted since
   the ledger began.

## 6. Decision tree

Keyed on the ledger row, in order — but if the PID has **no** ledger row at all, go straight to the
final row. Each row is decided by recorded data, not inferred from absence.

| Ledger row for the dying PID | What it means | Next |
|---|---|---|
| `backend_type <> 'client backend'` | Not a client at all. `autovacuum worker` is the concrete candidate on this instance. | Neither an Iverson nor an Authentik application defect. Pursue as a non-client backend. |
| `application_name = 'sigpipe-poller'` | Our own poller. | The instrumentation is implicated; redesign it before drawing any conclusion. |
| `usename = 'authentik'` | Not an Iverson defect. Iverson's exposure is that it shares an instance with a crashing tenant. | §7 becomes Iverson's primary response; separately decide whether to isolate Authentik's database. |
| `usename = 'iverson'`, `client_addr` **null** | A Unix-socket client — the container's own `pg_isready` healthcheck (every 10 s, `local all all trust`), or a `docker exec` session. | Not application code. Identify which before escalating. |
| `usename = 'iverson'`, `client_addr` **matches a container** in the contemporaneous map | Iverson application code — API, its `SchemaRefreshWorker` pool, or worker. | Escalate to core dumps for a stack trace naming what reset the handler. |
| `usename = 'iverson'`, `client_addr` set but **matching no container** in that map | Host-run tooling — `Iverson.LoadTest` or `Iverson.ClientConformance`, both of which default to `Host=localhost;Port=5432` as `iverson` (§9 #15). This was the live workload at crashes 2 and 3. | Reproduce under that tool, not under the API. |
| Any other `usename`/`datname` | An unenumerated client. Four databases accept connections (§9 #16). | Identify it before proceeding. |
| **PID absent from the ledger** | It lived less than one poll interval. | Use §5 step 4's log fallback. If the log has no lines either, and other PIDs of that period do, the process was never a logged client — treat as the first row. |

## 7. Mitigation fallback

Implemented **only if** the cause proves to be outside our control (an upstream Postgres or Authentik
defect): a **bounded backoff spanning at least ~10 s** in `TenantStatusCache.GetStatusAsync`, retrying
on Npgsql's broken-connection exception classes **and on SQLSTATE `57P03` (`cannot_connect_now`)**,
plus `57P01`/`57P02`.

**A single immediate retry would not have mitigated any of the four recorded crashes.** After a
signal-13 death the postmaster refuses all new connections while it recovers; the measured windows
are 8.3 s, 2.4 s, 3.8 s and 5.7 s. An immediate retry executes inside that window in milliseconds and
fails with `FATAL: the database system is in recovery mode` — SQLSTATE `57P03`, which is not a
broken-connection class and so falls outside the catch set. The log already carries **50** such
FATALs across the four events: clients retrying and being refused. The ~10 s figure is calibrated to
the observed worst case over four events; it is not a guaranteed ceiling.

**The cost is NOT bounded to one backoff per tenant per 30 s.** `TenantStatusCache.cs:17-19` calls
`cache.Set` only *after* a successful repository read, so a failed lookup caches nothing and every
subsequent call retries. While Postgres is down, the backoff is **per call**, not per tenant, and
concurrent gRPC calls each hold a thread for the duration. Anyone implementing §7 must decide how to
handle that — negative caching, in-flight deduplication, or a circuit breaker — and that decision is
part of §7's implementation, not settled here. **If a wait of that length in the RPC path is
unacceptable, drop §7 entirely** rather than record a mitigation that cannot mitigate.

**Explicitly out of scope, permanently:** adding retry or failure tolerance to `benchmark-query`'s
RPC-failure guard. That guard is what caught the truncated run. Weakening it to survive an
infrastructure fault is how a corrupt run becomes a believed number.

## 8. Success criterion

One crash whose dying PID resolves to a **`backend_type`**, and — if that is `client backend` — to an
identified client via `usename` plus an address resolved against the contemporaneous container map.
Nothing more. If that answer arrives and the cause is still unknown, this spec has succeeded.

A resolution that stops at "a user and a database" is **not** sufficient: §6's largest branch turns on
whether the address matches a container, and three of its rows are not reachable from user and
database alone.

## 9. Verified assumptions

Verified 2026-09-09 against the live container, started alone for verification and returned to its
stopped state. **No settings were changed** and no poller was left running.

| # | Assumption | Evidence | Result |
|---|---|---|---|
| 1 | `pg_stat_activity` exposes all ten columns the ledger records | `information_schema.columns` — all ten present | ✅ |
| 2 | `log_connections`/`log_disconnections` are `superuser-backend`, `log_line_prefix` is `sighup` | `pg_settings.context` | ✅ — demoted to §4.3, no longer forces an ordering precondition |
| 3 | `backend_start` is immutable per backend | two reads 2 s apart returned an identical value for the same pid | ✅ |
| 4 | The poller can self-identify | `PGAPPNAME=sigpipe-poller` → `application_name` = `sigpipe-poller` | ✅ |
| 5 | The poller loop survives postmaster recovery | each tick is an independent `docker exec`; a failed tick errors and the loop continues. Smoke-tested over 5 ticks | ✅ |
| 6 | One connection per second does not strain the instance | `max_connections` 100, 7 in use | ✅ |
| 7 | A superuser session sees backends in **every** database | opened a session on `authentik`; it was visible from a session on `iverson` | ✅ |
| 8 | Non-client processes appear with a distinguishing `backend_type` | observed `autovacuum launcher`, `background writer`, `checkpointer`, `walwriter`, `logical replication launcher` | ✅ |
| 9 | `autovacuum worker` is a real `backend_type` value | PG16 binary string table, contiguous: `…autovacuum launcher.autovacuum worker.client backend.background w…` | ✅ |
| 10 | A superuser can read other backends' `query` | `rolsuper = t` for `iverson` | ✅ |
| 11 | PID reuse is not a practical risk over a days-long watch | `pid_max` 4,194,304 at ~34 PIDs/min ≈ 86 days to wrap | ✅ — composite key kept as cheap insurance |
| 12 | A signal-killed autovacuum worker is reported as `server process` | the binary's child-exit labels are `startup`/`background writer`/`checkpointer`/`WAL writer`/`autovacuum launcher`/`archiver`/`server process`; there is no `autovacuum worker` label among them. Autovacuum is `on`; 2 of 216 `authentik` user tables show `last_autovacuum` | ✅ — this is why §2 puts class before identity |
| 13 | A Unix-socket client is identifiable | `client_addr` NULL, `client_port` −1 for a socket session; `pg_hba.conf` `local all all trust` lets `pg_isready -U iverson` fully authenticate every 10 s | ✅ |
| 14 | Nothing sets `Application Name` | zero hits across the repo and compose; `%a` would be empty for every client but the poller | ✅ — `%a` dropped from the prefix |
| 15 | Host-run tooling connects as `iverson@iverson` | `Iverson.LoadTest/Program.cs:33` and `Iverson.ClientConformance/Program.cs:17` both default to `Host=localhost;Port=5432;…Username=iverson`; port 5432 is published | ✅ |
| 16 | Only two roles can log in; the database side is **not** closed | `pg_roles`: `iverson` (super), `authentik`, and `iverson_runtime` (`rolcanlogin = f`, reachable only via `SET ROLE`, which does not change `usename`). `pg_database`: `iverson`, `authentik`, `postgres`, `template1` all `datallowconn = t` | ✅ — hence §6's fallthrough row |
| 17 | A host-origin connection is **not** distinguishable from a container by address shape | measured from the host network via published 5432: `client_addr=10.89.0.9`, `client_port=52400` — inside the container subnet | ✅ — §6 keys on absence from the map, not on shape |
| 18 | Container IPs are dynamic | no `ipv4_address` or IPAM pinning in `docker-compose.yml` | ✅ — forces §4.2's contemporaneous map |
| 19 | Nothing else consumes the log format or `pg_stat_activity` | no log parser in the repo; no `pg_stat_activity` reference; Prometheus scrapes only `iverson-api:8081` | ✅ |
| 20 | `TenantStatusCache.GetStatusAsync` is on the crash path | server stack trace in `docker logs iverson-api` | ✅ |
| 21 | A single immediate retry lands after Postgres resumes | **FALSE** — measured recovery windows 8.3/2.4/3.8/5.7 s; 50 `in recovery mode` FATALs already in the log | ❌ — drove §7's backoff |
| 22 | §7's cost is bounded to one backoff per tenant per 30 s | **FALSE** — `TenantStatusCache.cs:17-19` caches only after a successful read, so failures cache nothing and every call retries | ❌ — §7 now states this and defers the remedy to implementation |
| 23 | 12 of 19 containers were created from paths that no longer exist | `docker inspect` `com.docker.compose.project.working_dir` per container, measured | ✅ |
| 24 | Nothing needs a tier-wide `up` | all 19 containers exist and start individually with `docker start` | ✅ |
| 25 | No gRPC deadline in this repo would be exceeded by a ~10 s backoff | the only `Deadline` across `Iverson.Server` and `Iverson.Clients` is `Api.Tests/Helpers/TestServerCallContext.cs:37` (`DateTime.MaxValue`); no production call sets one. **Scope:** this covers this repo's own clients only — an external caller could still set a deadline a §7 backoff would blow through | ✅ scoped |
| 26 | Npgsql raises a distinguishable exception for a severed connection | observed `NpgsqlException` wrapping `EndOfStreamException`, three occurrences in the api log. **Not a survey** of Npgsql's exception classes | ⚠️ observed only — must be confirmed before §7 is implemented |

## 10. Known issues, accepted as out of scope

- **The ledger's sampling gap.** A backend forking and dying inside one poll interval never appears
  in it. §4.3's log settings close this for client backends established after their reload; a
  short-lived **non-client** process in that window would be missed by both. Accepted: no mechanism
  short of core dumps covers it.
- **The API and its `SchemaRefreshWorker` share one container IP** and cannot be told apart by
  address. If that distinction matters, setting `Application Name` on the connection strings is the
  fix — a code change plus a service restart, not needed to answer the primary branch.
- **The container map is only as good as its sampling.** If the stack is restarted and the ledger's
  map is stale at crash time, an address may resolve to the wrong container or to none. §5 step 5
  records whether a restart intervened so this is visible rather than silent.
- **Assumption 26 is confirmed only by observed instances**, not by a survey of Npgsql's exception
  classes. Which classes actually signal a severed connection must be settled before §7 is
  implemented — not before this spec lands.
- **The poller is itself a Postgres client** and appears in its own ledger. §6 has a row for that. It
  adds one short-lived connection per second to a system whose fork rate is already ~34/min.
