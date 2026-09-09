# Postgres SIGPIPE Diagnosis Ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-09-postgres-backend-sigpipe-diagnosis-design.md` (commit SHA: `bbdcf00`)

**Goal:** Stand up the instrumentation that identifies what the dying Postgres backend was, so the next `signal 13` crash is diagnosable instead of anonymous.

**Architecture:** A bash poller reads `pg_stat_activity` once per second and appends only previously-unseen `(pid, backend_start)` rows to a CSV ledger, alongside a periodically-sampled container name→IP map. Three Postgres log settings are applied separately as a complement that closes the ledger's sampling gap. Nothing analyses the ledger — spec §5 is the procedure a human runs after a crash.

**Tech stack:** bash 5.3, `docker exec` into `iverson-postgres` (PostgreSQL 16.15), `psql` COPY-to-CSV. No new dependency.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **No tier-wide `docker compose up`, ever.** 12 of the 19 `iverson-*` containers were created from working directories that no longer exist (`iverson-api` among them), so a tier-wide `up` would recreate them. Every container already exists; `docker start <name>` restores one without creating anything. (spec §4.4)
- **The poller requires no `ALTER SYSTEM` and no ordering precondition.** It sees backends that already exist, so there is nothing to sequence and no requirement about when the stack is started. (spec §4.1)
- **The ledger is written outside the repo.** It is growing data, not source.
- **`application_name=sigpipe-poller`** on every poller connection, so its own rows are self-evident. (spec §4.1, §6 row 2)

## File Structure

**Create**
- `scripts/sigpipe-ledger.sh` — the poller. Appends new `(pid, backend_start)` rows to `<out-dir>/ledger.csv` and samples the container map into `<out-dir>/containers.csv`.

**Modify** — none.

**Test** — none. This is an operational script with no unit-testable pure logic; Task 1's verification steps exercise it against the live stack instead, which is the only thing that could actually falsify it.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here:

- `pg_stat_activity` exposes all ten recorded columns in PG16 (spec §9 #1).
- `backend_start` is immutable per backend (#3); `PGAPPNAME` sets `application_name` (#4).
- A superuser session sees backends in every database (#7); `iverson` is superuser (#10).
- Non-client processes carry a distinguishing `backend_type` (#8), and `autovacuum worker` is a real value (#9).
- A ledger row always carries a usable `usename`/`datname` — pgstat registers identity only after authentication (#26).
- The poller's own connection appears in its own ledger (#27).
- A host-origin connection records `iverson-postgres`'s **own** container address (#17); container IPs are dynamic (#18).
- `log_line_prefix` is `sighup`; `log_connections`/`log_disconnections` are `superuser-backend` (#2).
- Nothing consumes the Postgres log format (#19).
- The container has its own PID namespace, so PIDs collide across restarts (#11).

## Verified plan-level assumptions

Newly introduced by this plan and verified 2026-09-09 against the live container (started alone, returned to `Exited`; **no settings applied, no poller left running**).

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `scripts/sigpipe-ledger.sh` does not already exist | `ls` returned no such file |
| 2 | File path | `scripts/` at repo root is tracked and is the ops-script location | `git ls-files scripts/` → `scripts/reap-testcontainers.sh` |
| 3 | File path | `~/iverson-sigpipe-ledger/` collides with nothing | `ls -d` returned no such directory |
| 4 | Signature | `docker exec -e VAR=value` sets the env for the exec'd process | ran it; the row came back with `application_name=sigpipe-poller` |
| 5 | Code validity | The exact `copy (…) to stdout with csv` query runs and yields the ten columns | ran verbatim against PG16; 6 rows returned |
| 6 | Signature | One `docker inspect -f` call yields name→IP for all running containers | `docker inspect -f '{{.Name}} {{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $(docker ps -q)` → `iverson-postgres 10.89.0.13` |
| 7 | Command | bash supports associative arrays (needs ≥ 4) | `bash --version` → 5.3.9 |
| 8 | Command | No lint/format/pre-commit gate applies to a new shell script | no `.pre-commit-config.yaml`, no `.husky`; `.github/workflows/` holds `codeql.yml` and `deploy-validate.yml`, neither running shellcheck |
| 9 | Command | Commit convention is lowercase imperative, no Conventional-Commits prefix | `git log --oneline -20`: "add tail_stats.py: …", "close two round-2 items …" |
| 10 | Ordering | Tasks 1 and 2 are independent | Task 2 changes runtime settings only; Task 1 reads a view. Neither consumes the other's output |
| 11 | **Code validity** | **`set -euo pipefail` KILLS the poll loop on a failed tick unless each tick is `if`-guarded** | ran both forms: unguarded, the loop exited on the first failure and printed nothing; `if out=$(…); then … else … fi`, all three ticks ran. **The loop must be guarded — a failing tick is the normal case during postmaster recovery** |
| 12 | **Code validity** | **`query` contains embedded newlines, so raw COPY output is multi-line CSV and breaks line-based dedup** | the unmodified query returned a record spanning 3 lines. `regexp_replace(coalesce(query,''), '\s+', ' ', 'g')` gives exactly one line per record (6 rows → 6 lines) |
| 13 | Code validity | `host(client_addr)` strips the netmask; raw `client_addr` carries `/32` | raw read gave `10.89.0.10/32`; `host()` gave `10.89.0.11`. The plan uses `host()` |
| 14 | Code validity | `psql -tA` alone suffices with COPY CSV; `-F','` is redundant | COPY does the delimiting; `-tA` output parsed as CSV correctly |
| 15 | Consumer impact | `log_connections=on` does not threaten journald retention | journald holds 3.9 G over 15.2 days ≈ 263 MB/day. Connections are ~49k/day from the application **plus ~47k/day from the poller itself** (86,400 ÷ 1.85 s) ≈ 96k/day; at ~3 log lines each (received + authorized + disconnection) ≈ **25–34 MB/day, ~+10%** → retention ≈ 13.5–14 days, still far outside a 1–2 day crash interval |
| 16 | Command | `docker`, `date`, `mkdir` all present and used | `command -v` for each returned a path |
| 17 | **Code validity** | **The effective tick interval is ~2× the configured one.** `--interval 1` yields a mean of **2.16 s**, because `docker exec` plus psql startup costs ~1.1 s per tick | ran the script for 12 s: 6 poller rows, gaps 3.46/1.81/1.79/1.82/1.91 s. Spec §4.1 says "once per second" and §10's sampling-gap acceptance is written against 1 s — the real gap is about twice that. `--interval 0` tightens it to ~1.1 s at the cost of continuous `docker exec` load |
| 19 | Code validity | `backend_start` is never NULL, so the dedup key `pid\|backend_start` is always well-formed | `count(*) filter (where backend_start is null), count(*)` → `0|6`, across all six `backend_type` values the instance emits |
| 20 | Consumer impact | Nothing higher-precedence overrides `postgresql.auto.conf` for the three settings | `Config.Cmd` = `["postgres"]`, no `-c` flags; mounts are the data volume and `init-authentik-db.sql` only; all three at `source = default`, `sourcefile` empty |
| 21 | Code validity | **`ALTER SYSTEM` must be one statement per `psql -c`** | a multi-statement `-c` returns `ERROR: ALTER SYSTEM cannot run inside a transaction block`; the one-per-`-c` loop ran all three plus `pg_reload_conf()` with `postgresql.auto.conf` md5 unchanged |
| 18 | Code validity | The whole script runs end-to-end and produces what §6 needs | ran it against the live container: header written; `client backend` rows with `usename`/`datname`; non-client types `autovacuum launcher`, `background writer`, `checkpointer`, `logical replication launcher`, `walwriter`; 6 poller rows; **zero duplicate `(pid, backend_start)`**; 12 records = 12 lines (no multi-line breakage); a timestamped container-map row |

**Two corrections to the spec, carried into this plan.**

**The sampling gap is about twice what the spec assumes.** §4.1 specifies a one-second poll and §10
accepts that sub-interval backends are missed. Measured, a tick takes **2.16 s** at `--interval 1`
(assumption 17), because each tick pays `docker exec` plus psql startup. The gap §10 accepts is
therefore ~2 s wide, not ~1 s. This does not change the design — the log complement in §4.3 exists
precisely to cover short-lived *client* backends — but a short-lived non-client process has twice the
window to slip through than the spec's wording implies.

**The storage estimate is wrong.** Spec §4.1 estimates ledger growth at "order 5 MB/day". That is wrong: each poller tick opens a new connection, so its own row has a new `(pid, backend_start)` every tick and is appended every tick — 86,400 rows/day from the poller alone, roughly tripling the estimate. CDR round 3 recorded this as bookkeeping rather than a finding, so it was never applied to the spec. This plan cites the real behaviour.

## Tasks

### Task 1: The ledger poller

**Files:**
- Create: `scripts/sigpipe-ledger.sh`

- [ ] **Step 1: Write `scripts/sigpipe-ledger.sh`.**

Match `scripts/reap-testcontainers.sh`'s conventions: `#!/usr/bin/env bash`, a WHY-THIS-EXISTS header explaining what the script buys and why it exists, `set -euo pipefail`, long-flag parsing with a `usage`.

```bash
#!/usr/bin/env bash
#
# Append-only identity ledger for pg_stat_activity, for diagnosing the Postgres backend SIGPIPE crash.
#
# WHY THIS EXISTS. A Postgres backend in this dev stack dies of `signal 13: Broken pipe`, forcing the
# postmaster to terminate every other backend and reinitialize. Four occurrences are on record and the
# cause is unknown. Every one of the four dying PIDs emitted ZERO log lines of its own, so there is
# nothing on disk to map a PID to a process class or a client — which is why four crashes have produced
# no diagnosis. This script records that mapping continuously, so the next crash is diagnosable.
# Design: docs/specs/2026-09-09-postgres-backend-sigpipe-diagnosis-design.md
#
# WHAT IT RECORDS. Two files under --out-dir:
#   ledger.csv     one row per (pid, backend_start) first seen, from pg_stat_activity
#   containers.csv timestamped container name -> IP samples, because container IPs are DYNAMIC and an
#                  address captured today cannot be resolved after a restart
#
# READING IT AFTER A CRASH is spec section 5. Two things there are not optional: scope the lookup to the
# crash's postmaster epoch (the container has its own PID namespace, so PIDs collide across restarts and
# an unsampled backend otherwise resolves to a previous epoch's row -- a confident wrong answer), and
# resolve client_addr against the map AS RECORDED NEAREST THE CRASH, not against the current stack.
#
# RUNNING IT. Foreground by default. To leave it running:  setsid ./scripts/sigpipe-ledger.sh &
# Stopping it is the whole teardown -- it changes no Postgres state and creates no containers.
#
# state and query are recorded as of the FIRST tick that saw a backend, not as of the crash.

set -euo pipefail

CONTAINER="iverson-postgres"
OUT_DIR="${HOME}/iverson-sigpipe-ledger"
INTERVAL=1
MAP_EVERY=30

usage() {
    cat <<USAGE
Usage: $(basename "$0") [--container NAME] [--out-dir DIR] [--interval SECONDS] [--map-every TICKS]

  --container   Postgres container to poll        (default: ${CONTAINER})
  --out-dir     where ledger.csv/containers.csv go (default: ${OUT_DIR})
  --interval    seconds between ticks              (default: ${INTERVAL})
  --map-every   sample the container map every N ticks (default: ${MAP_EVERY})
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --container) CONTAINER="$2"; shift 2 ;;
        --out-dir)   OUT_DIR="$2";   shift 2 ;;
        --interval)  INTERVAL="$2";  shift 2 ;;
        --map-every) MAP_EVERY="$2"; shift 2 ;;
        -h|--help)   usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# A non-positive --map-every divides by zero inside the guarded (( … )) below, which is swallowed
# exactly as the modulo bug was.
[[ "$MAP_EVERY" =~ ^[1-9][0-9]*$ ]] || { echo "--map-every must be a positive integer" >&2; exit 2; }

mkdir -p "$OUT_DIR"
LEDGER="${OUT_DIR}/ledger.csv"
MAP="${OUT_DIR}/containers.csv"

[[ -s "$LEDGER" ]] || echo "sampled_at,pid,backend_start,backend_type,usename,datname,client_addr,client_port,application_name,state,query" > "$LEDGER"
[[ -s "$MAP" ]]    || echo "sampled_at,container,ip" > "$MAP"

# regexp_replace collapses whitespace in `query`: without it a query containing a newline produces a
# multi-line CSV record, and the line-based dedup below silently mis-parses it.
# host() strips the netmask that raw client_addr carries (10.89.0.10/32 -> 10.89.0.10).
read -r -d '' QUERY <<'SQL' || true
copy (select now(), pid, backend_start, backend_type,
             coalesce(usename,''), coalesce(datname,''),
             coalesce(host(client_addr),''), coalesce(client_port::text,''),
             coalesce(application_name,''), coalesce(state,''),
             regexp_replace(coalesce(query,''), '\s+', ' ', 'g')
      from pg_stat_activity) to stdout with csv
SQL

declare -A SEEN

echo "[sigpipe-ledger] polling ${CONTAINER} every ${INTERVAL}s -> ${LEDGER}"
echo "[sigpipe-ledger] container map every ${MAP_EVERY} ticks -> ${MAP}"

tick=0
while true; do
    tick=$(( tick + 1 ))

    # if-guarded ON PURPOSE. Under `set -e` an unguarded failing command exits the script, and a failing
    # tick is the NORMAL case: while the postmaster recovers it refuses every connection for 2-9 seconds.
    # An unguarded loop would die during exactly the event this script exists to observe.
    if snapshot=$(docker exec -e PGAPPNAME=sigpipe-poller "$CONTAINER" \
                    psql -U iverson -d postgres -tA -c "$QUERY" 2>/dev/null); then
        while IFS= read -r row; do
            [[ -z "$row" ]] && continue
            # Fields 1-3 are now(), pid, backend_start -- none can contain a comma, so cut is safe here
            # even though later fields are CSV-quoted.
            key="$(cut -d, -f2 <<<"$row")|$(cut -d, -f3 <<<"$row")"
            if [[ -z "${SEEN[$key]+set}" ]]; then
                SEEN[$key]=1
                printf '%s\n' "$row" >> "$LEDGER"
            fi
        done <<<"$snapshot"
    fi

    # (tick - 1) % MAP_EVERY, NOT tick % MAP_EVERY: the latter is never true for MAP_EVERY=1,
    # so --map-every 1 silently wrote no container map at all.
    if (( (tick - 1) % MAP_EVERY == 0 )); then
        if ids=$(docker ps -q) && [[ -n "$ids" ]]; then
            now="$(date -Is)"
            docker inspect -f '{{.Name}} {{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $ids 2>/dev/null \
              | while read -r name ip; do printf '%s,%s,%s\n' "$now" "${name#/}" "$ip" >> "$MAP"; done || true
        fi
    fi

    sleep "$INTERVAL"
done
```

- [ ] **Step 2: Make it executable and start the container it polls.**
```bash
chmod +x scripts/sigpipe-ledger.sh
docker start iverson-postgres
until docker exec iverson-postgres pg_isready -U iverson >/dev/null 2>&1; do sleep 1; done
```
`docker start` on the existing container — never `docker compose up` (Global Constraints).

- [ ] **Step 3: Run it for ~20 seconds and stop it.**
```bash
./scripts/sigpipe-ledger.sh --out-dir /tmp/sigpipe-smoke &
SMOKE=$!
sleep 20
kill "$SMOKE"
```

- [ ] **Step 4: Verify the ledger holds what spec §6 needs.** Each check below maps to a decision-tree row that would be undecidable without it. **If any fails, the instrumentation is not working and fixing it now is the whole point of this step** — otherwise the failure surfaces days later, after a crash has been missed.
```bash
L=/tmp/sigpipe-smoke/ledger.csv
head -1 "$L"                                    # header present
awk -F, '$4=="client backend"' "$L" | head -2    # a client backend, with usename+datname (§6 rows 3-7)
awk -F, '$4!="client backend" && NR>1' "$L" | head -2   # a non-client backend_type (§6 row 1)
grep -c sigpipe-poller "$L"                      # the poller's own rows (§6 row 2)
cut -d, -f2,3 "$L" | tail -n +2 | sort | uniq -d # MUST be empty: no duplicate (pid,backend_start)
# Records vs lines. Counting BOTH with a line counter is a tautology; count records by the rows
# whose first field parses as a timestamp, which a continuation line from an embedded newline cannot.
recs=$(awk -F, 'NR>1 && $1 ~ /^[0-9]{4}-[0-9]{2}-[0-9]{2} /' "$L" | wc -l)
lines=$(( $(wc -l < "$L") - 1 ))
echo "records=$recs lines=$lines"; [ "$recs" -eq "$lines" ] || echo "MULTI-LINE RECORDS PRESENT"
head -2 /tmp/sigpipe-smoke/containers.csv        # a timestamped container map row (§4.2)
```

- [ ] **Step 5: Clean up the smoke output.**
```bash
rm -rf /tmp/sigpipe-smoke
```
Do **not** stop the container here — Step 7 needs it, and the point of this plan is to leave
instrumentation live.

- [ ] **Step 6: Commit**
```bash
git add scripts/sigpipe-ledger.sh
git commit -m "add sigpipe-ledger.sh: identity ledger for the Postgres backend SIGPIPE crash"
```

- [ ] **Step 7: Launch the poller against the real out-dir and confirm it is recording.** Without
this the plan ends with nothing watching, and spec §8's success criterion — one crash whose PID
resolves — is unreachable.
```bash
setsid ./scripts/sigpipe-ledger.sh --out-dir ~/iverson-sigpipe-ledger >/dev/null 2>&1 &
sleep 10
wc -l ~/iverson-sigpipe-ledger/ledger.csv     # must be > 1 (header plus rows)
sleep 10
wc -l ~/iverson-sigpipe-ledger/ledger.csv     # must have grown
```

**The poller does not survive a reboot or a logout.** `setsid` detaches it from the terminal, not
from the session's lifetime on every configuration, and nothing supervises it. Over a multi-day
watch — and the crash interval is 1–2 days — it must be relaunched after either. Checking that
`ledger.csv` is still growing is the cheap way to notice. A systemd user unit would fix this
properly and is deliberately not built here.

### Task 2: The log complement

Closes the ledger's sampling gap for client backends (spec §4.3). **This task changes runtime state only — it creates and modifies no repo file, so it has no commit step.**

**Files:** none.

- [ ] **Step 1: Apply the three settings.**
```bash
docker start iverson-postgres
until docker exec iverson-postgres pg_isready -U iverson >/dev/null 2>&1; do sleep 1; done
# ALTER SYSTEM is forbidden inside a transaction block, and psql -c with several statements wraps
# them in ONE implicit transaction. One statement per -c, or nothing is applied.
for s in "SET log_line_prefix = '%m [%p] %q%u@%d '" \
         "SET log_connections = on" \
         "SET log_disconnections = on"; do
  docker exec iverson-postgres psql -U iverson -c "ALTER SYSTEM $s"
done
docker exec iverson-postgres psql -U iverson -c "SELECT pg_reload_conf()"
```

- [ ] **Step 2: Verify they took.**
```bash
docker exec iverson-postgres psql -U iverson -tAc \
  "select name, setting, pending_restart from pg_settings
   where name in ('log_line_prefix','log_connections','log_disconnections');"
```
`log_line_prefix` is `sighup` and applies immediately. `log_connections`/`log_disconnections` are `superuser-backend`: they apply to connections opened **after** this reload, so pre-existing pooled backends stay unlogged. That is expected and tolerable — the ledger covers them (spec §4.3).

- [ ] **Step 3: Record how to undo it.** These persist in `postgresql.auto.conf` inside the `postgres_data` volume, surviving container restart **and recreate**. Restarting does not undo them; only this does:
```bash
for s in "RESET log_line_prefix" "RESET log_connections" "RESET log_disconnections"; do
  docker exec iverson-postgres psql -U iverson -c "ALTER SYSTEM $s"
done
docker exec iverson-postgres psql -U iverson -c "SELECT pg_reload_conf()"
```

## Tasks NOT in this plan

Inherited from the spec's non-goals (§2), preserved in its form:

Fixing the crash. Identifying what reset the signal handler. Core dumps. Separating Authentik onto its own instance. Each is a consequence of the answer, not a prerequisite to it.

Additionally, and not from §2: **spec §7's mitigation fallback is not planned here.** The spec gates it on the diagnosis outcome and explicitly leaves its central problem — that the backoff is per-call, not per-tenant, because `TenantStatusCache` caches only after a successful read — to implementation. It is not specified enough to plan, and planning it now would presuppose the answer this work exists to find.

## Known issues inherited from spec

Inherited verbatim from spec §10.

- **The ledger's sampling gap.** A backend forking and dying inside one poll interval never appears in it. §4.3's log settings close this for client backends established after their reload; a short-lived **non-client** process in that window would be missed by both. Accepted: no mechanism short of core dumps covers it.
- **Crash-time statement attribution is out of scope.** The ledger's `state` and `query` describe the backend at first sighting, not at the crash. Obtaining the crash-time statement is a *different* mechanism — a second append keyed on `(pid, backend_start, query_start)`, at a storage cost this spec has not derived — and is deliberately not designed here.
- **The API and its `SchemaRefreshWorker` share one container IP** and cannot be told apart by address. If that distinction matters, setting `Application Name` on the connection strings is the fix — a code change plus a service restart, not needed to answer the primary branch.
- **The container map is only as good as its sampling.** If the stack is restarted and the ledger's map is stale at crash time, an address may resolve to the wrong container or to none. §5 step 5 records whether a restart intervened so this is visible rather than silent.
- **Assumption 28 is confirmed only by observed instances**, not by a survey of Npgsql's exception classes. Which classes actually signal a severed connection must be settled before §7 is implemented — not before this spec lands.
- **The poller is itself a Postgres client** and appears in its own ledger. §6 has a row for that. It adds one short-lived connection per second to a system whose fork rate is already ~34/min.
