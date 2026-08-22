# Authentik migration startup race

**Status:** FIXED 2026-08-22 in compose and in the Helm chart. Verified by reproducing the original
failure conditions — a dropped authentik database with the server and worker started concurrently —
which now comes up clean. The mechanism section below was also corrected once the actual bootstrap
code was read; the original diagnosis was right about the symptom and imprecise about the cause.
**Found:** 2026-08-22, on a `docker compose up` of `Iverson.Server/docker-compose.yml` from an empty authentik volume.
**Affects:** the compose stack and the Helm chart (`deploy/helm/iverson/charts/authentik`) equally — kind and cloud included.
**Out of scope of:** the client-conformance / client-standard work this was found during. Written up as its own initiative.

## Symptom

On a fresh install, `iverson-authentik-server` never becomes healthy. Its log freezes mid-migration —
in the observed case on `Applying authentik_core.0056_user_roles` — and stays frozen indefinitely.
The container keeps burning CPU (~17%), so it does not look hung to `docker stats`, and
`restart: unless-stopped` never fires because the container has not exited. It simply never converges.

## Root cause (corrected 2026-08-22 after reading `lifecycle/migrate.py`)

The original write-up called this a race between the server and the worker. That is the trigger, but
the mechanism is narrower and more specific, and it matters for choosing a fix.

`lifecycle/migrate.py`'s `run_migrations()`:

1. opens **its own** psycopg connection,
2. takes `pg_advisory_lock(1000)` on it (`wait_for_lock`),
3. runs the system migrations and then hands the Django work to
   `execute_from_command_line(["", "migrate_schemas"])` — which uses **Django's own, separate**
   connection(s),
4. releases the lock in a `finally`.

Between steps 2 and 3 that first connection issues `SET search_path = …` and reads
`authentik_version_history` outside any explicit transaction block, so psycopg leaves an implicit
transaction open on it. The connection then sits **idle in transaction**, holding both the advisory
lock and whatever table locks those statements took, for the entire duration of the Django migration
running on a different connection.

A second container's Django connection then blocks on a table lock that idle backend holds. Postgres
cannot report this as a deadlock, because the idle backend is not itself waiting on anything — there
is no cycle to detect, `deadlock_timeout` never applies, and nothing times out an idle transaction.

## Original observation



Both `ak server` and `ak worker` run database migrations on startup. Authentik tries to serialize
them with `pg_advisory_lock`, so the naive "two migrators race" reading is not quite right — the
observed wedge was three-deep, and the advisory lock is part of what makes it permanent:

| pid | state | what it was doing |
|---|---|---|
| 306 | **idle in transaction** since 14:01 | held a lock on `authentik_core_group` after `SELECT * FROM authentik_version_history` |
| 358 | active, blocked on a `relation` lock | `ALTER TABLE "authentik_core_group" DROP CONSTRAINT …` — waiting on 306 |
| 349 | active, blocked on an `advisory` lock | waiting on `pg_advisory_lock($1)` — held by 358 |

Confirmed with `pg_blocking_pids()`: both 349 and 358 reported `blocked_by = {306}`.

PostgreSQL's deadlock detector cannot break this. 306 was not *waiting* on anything — it was idle —
so there is no cycle to detect, and no `deadlock_timeout` applies. Nothing in the stack times out an
idle transaction either. The wedge is permanent until something kills a connection.

**Unblocking it is not the same as recovering from it.** Terminating the stale backend does resume
migrations immediately:

```sql
-- find it
SELECT pid, pg_blocking_pids(pid), state FROM pg_stat_activity WHERE datname = 'authentik';
-- clear it
SELECT pg_terminate_backend(<the idle-in-transaction pid>);
```

...but in the observed case the schema was already **corrupt**, and that only surfaced later.

## The wedge corrupts the schema (observed, not theoretical)

After unblocking, the server reached healthy — but every custom blueprint failed, no OAuth2 provider
was ever created, and token issuance returned `invalid_client`. The worker log carried the reason:

```
django.db.utils.ProgrammingError: relation "authentik_t_queue_n_7b09fb_idx" already exists
```

A migration step had physically created the index, but Django's migration ledger never recorded the
step as applied. Every subsequent apply re-runs it and re-fails identically. **This does not
self-heal.** Restarting the worker did not fix it; the blueprints stayed in `error` and the provider
table stayed empty.

Recovery required rebuilding the database:

```sh
docker compose stop authentik-server authentik-worker
docker exec iverson-postgres psql -U iverson -d postgres -c "DROP DATABASE authentik;"
docker exec iverson-postgres psql -U iverson -d postgres -c "CREATE DATABASE authentik OWNER authentik;"
```

**This materially worsens the kind/cloud assessment below.** Liveness-driven restarts do not merely
cost churn — a restart landing mid-migration is one of the ways this corruption is produced. A
cluster can therefore converge into a *permanently* broken authentik whose pods all report healthy,
because the server serves fine on a schema whose blueprints can never apply. The visible symptom is
not a crash loop; it is `invalid_client` on every service-client token request, which looks like a
credentials problem and not a migration problem.

## Serial startup verified clean

Rebuilding the database and starting the two components **serially** produced a clean result on the
first attempt, which is the empirical case for the fixes below:

| step | outcome |
|---|---|
| `up -d authentik-server` alone | healthy after ~210s, migrations applied once, no contention |
| then `up -d authentik-worker` | healthy after ~30s |
| custom blueprints | all 3 `successful` after ~195s |
| OAuth2 providers | all 5 created; `client_credentials` token issued successfully |

Compare with the concurrent start: wedged indefinitely, then corrupt.

## Why the Helm chart has the same exposure

`templates/deployment-server.yaml` and `templates/deployment-worker.yaml` run the same image with
`args: ["server"]` and `args: ["worker"]`. The image's `ak` entrypoint runs migrations in both. There
is no migration `Job`, no `initContainer`, and no ordering between the two Deployments — the only
`initContainers` anywhere in the chart tree belongs to ollama.

Compose has `depends_on: {postgres, redis}` on both services, which staggers them slightly by
accident. Kubernetes has no cross-Deployment ordering primitive at all, so both pods schedule as soon
as their nodes are ready. **The concurrent-start window in kind/cloud is wider than in compose, not
narrower.**

### The failure signature does differ

- **kind / cloud: restart churn, and restarts are themselves a corruption vector** (see above).** The server's `livenessProbe` is `httpGet /-/health/live/` with
  `initialDelaySeconds: 60`, `periodSeconds: 30`, `failureThreshold: 5`. A wedged server never binds
  9000, so kubelet restarts it after roughly 3.5 minutes, killing its connections and releasing the
  locks. The worker's `ak healthcheck` exec probe behaves the same way. Expect several minutes of
  restart churn on a fresh install that looks like a broken deploy. The "self-heal" is not reliable:
  a restart landing mid-`ALTER TABLE` produces the partially-migrated schema documented above, which
  no further restart repairs.
- **compose: permanent hang.** `restart: unless-stopped` does not act on an unhealthy container, only
  an exited one, and there is no autoheal sidecar. Nothing consumes the healthcheck result. This is
  the observed case.

## The fix, as implemented

One process migrates; nothing else does. Authentik supports this directly — `run_migrations()` opens
with `if CONFIG.get_bool("skip_migrations", False): return`, and `AUTHENTIK_SKIP_MIGRATIONS=true`
sets it (verified against the pinned 2026.5.3 image, not assumed).

**compose:** a one-shot `authentik-migrate` service; `authentik-server` and `authentik-worker` gate on
it with `condition: service_completed_successfully` and both carry `AUTHENTIK_SKIP_MIGRATIONS=true`.

**Helm:** a `migrate` initContainer on the server Deployment, with `AUTHENTIK_SKIP_MIGRATIONS=true` on
both the server and worker containers.

Two details worth recording, both verified rather than assumed:

- **`ak migrate` is NOT the right command.** The image entrypoint routes a bare subcommand to
  `python -m manage`, so `ak migrate` runs only Django's `migrate` — skipping
  `lifecycle/system_migrations/*`, `migrate_schemas`, and the tenant-template pass. The correct
  invocation is `python -m lifecycle.migrate`, which needs the entrypoint overridden because that
  same routing swallows any bare command. (One of the two open questions the first draft listed.)
- It is idempotent: a re-run against a migrated database exits 0 with "No migrations to apply."

**Known trade-off in Kubernetes:** on a fresh install the worker pod can start before the server's
initContainer finishes and crash-loop briefly against an incomplete schema. That is noisy but safe —
it never migrates, so it cannot corrupt — and it clears as soon as migrations complete. Gating the
worker on the server's readiness would remove the churn at the cost of coupling the worker's pod
start to the server being up.

### Verification

Reproduced the original failure conditions: dropped the `authentik` database, then started the server
and worker **concurrently** (`docker compose up -d authentik-server authentik-worker`).

| step | result |
|---|---|
| `authentik-migrate` | ran once, exited 0, before either role started |
| server + worker | both started after it; both healthy ~30s later |
| total | 3m09s, versus an indefinite wedge before |
| custom blueprints | all 3 `successful` (~75s) |
| OAuth2 providers | all 5 created; `client_credentials` token issued |

This is the first time the stack has come up clean from an empty authentik volume with both roles
started concurrently.

## Options considered

### 1. Fixed-delay staggering — rejected

A `sleep`, a `helm.sh/hook-weight`, or a larger `initialDelaySeconds` on the worker only shifts the
window. The wedge does not require simultaneous *start*; it requires one process to hold an open
transaction while the other needs an `ACCESS EXCLUSIVE` lock. First-boot migrations ran well over 20
minutes here, so a worker delayed by seconds or minutes still lands inside the window. Making a fixed
delay reliable means exceeding the full migration duration, which is unbounded and grows with every
skipped authentik version. This is a probability reduction presented as a fix, and it leaves the same
silent indefinite hang — rarer, and therefore harder to diagnose when it does hit.

### 2. Readiness-gated staggering — correct for the observed case

Gate the worker on the server being *ready* rather than on a clock. In compose that is
`depends_on: authentik-server: {condition: service_healthy}`. In Kubernetes it takes an
`initContainer` on the worker polling the server's `/-/health/ready/` — structurally the same trick
`templates/job-revoke-cross-db.yaml:63` already uses to poll for the CNPG-provisioned role.

**Load-bearing assumption, NOT yet confirmed:** that authentik's server reaches ready only *after*
migrations complete. This is consistent with the entrypoint's ordering and with the observed run (the
server reported unhealthy for the entire migration), but it has not been verified against the pinned
version and the entire fix rests on it.

Caveats:
- **Upgrade-shaped gap.** A rolling update brings up a new server *and* a new worker, both of which
  migrate. The poll must distinguish the new server from an old one still reporting ready, or the
  worker sails past a stale ready.
- **Hard-couples the worker to the server.** The worker can no longer start while the server is down,
  including during an unrelated server outage. A real availability trade, not free.
- **`replicas: 1` is doing quiet work.** Both Deployments are pinned to one replica. Scaling the
  server to 2 puts two server pods into concurrent migration, which worker-gating does not cover.

### 3. A dedicated migration Job — CHOSEN, see "The fix, as implemented" above

Take migrations away from both long-running processes: a `helm.sh/hook: pre-install,pre-upgrade` Job
running `ak migrate`, with server and worker starting only after it completes. Mirror it in compose as
a one-shot `authentik-migrate` service that both gate on with
`condition: service_completed_successfully`.

This gives exactly one migrator by construction rather than by lock contention. It covers first boot
*and* upgrades, survives scaling either component, and does not couple the worker's lifecycle to the
server's. Option 2 solves the case we hit; option 3 solves the class.

## Open questions before implementing

1. Confirm, against the pinned `imageTag`, that the server's `/-/health/ready/` cannot report ready
   before migrations finish. Option 2 depends on it entirely; option 3 does not.
2. Confirm `ak migrate` exists as a standalone subcommand in the pinned version and is safe to run
   with no server or worker present.
3. Decide whether the migration Job should also gate `iverson-api`, which today depends on authentik
   only for token issuance at request time, not at startup.
4. Related but distinct: `job-revoke-cross-db.yaml` already documents a post-install race against
   CNPG's async role reconciler. Worth checking whether a pre-install migration Job interacts with
   that hook's ordering.
