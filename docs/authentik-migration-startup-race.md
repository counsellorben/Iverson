# Authentik migration startup race

**Status:** open defect with a known operator workaround; no code change made yet.
**Found:** 2026-08-22, on a `docker compose up` of `Iverson.Server/docker-compose.yml` from an empty authentik volume.
**Affects:** the compose stack and the Helm chart (`deploy/helm/iverson/charts/authentik`) equally — kind and cloud included.
**Out of scope of:** the client-conformance / client-standard work this was found during. Written up as its own initiative.

## Symptom

On a fresh install, `iverson-authentik-server` never becomes healthy. Its log freezes mid-migration —
in the observed case on `Applying authentik_core.0056_user_roles` — and stays frozen indefinitely.
The container keeps burning CPU (~17%), so it does not look hung to `docker stats`, and
`restart: unless-stopped` never fires because the container has not exited. It simply never converges.

## Root cause

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

**Workaround (verified):** terminate the stale backend and migrations resume immediately.

```sql
-- find it
SELECT pid, pg_blocking_pids(pid), state FROM pg_stat_activity WHERE datname = 'authentik';
-- clear it
SELECT pg_terminate_backend(<the idle-in-transaction pid>);
```

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

- **kind / cloud: noisy self-heal.** The server's `livenessProbe` is `httpGet /-/health/live/` with
  `initialDelaySeconds: 60`, `periodSeconds: 30`, `failureThreshold: 5`. A wedged server never binds
  9000, so kubelet restarts it after roughly 3.5 minutes, killing its connections and releasing the
  locks. The worker's `ak healthcheck` exec probe behaves the same way. Expect several minutes of
  restart churn on a fresh install that looks like a broken deploy, plus a residual risk of a restart
  landing mid-`ALTER TABLE` on a partially migrated schema.
- **compose: permanent hang.** `restart: unless-stopped` does not act on an unhealthy container, only
  an exited one, and there is no autoheal sidecar. Nothing consumes the healthcheck result. This is
  the observed case.

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

### 3. A dedicated migration Job — recommended

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
