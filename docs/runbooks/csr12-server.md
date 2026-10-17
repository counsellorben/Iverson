# CSR Round-12 Server — Operator Runbook

**Why:** on Postgres, the api no longer holds an owner, BYPASSRLS or DDL credential, and every StarRocks data connection carries one tenant's role.
- **Postgres.** The api logs in as `iverson_api`: a member of `iverson_runtime`, not the owner, with no BYPASSRLS, no `iverson_maintenance` and no DDL.
- **StarRocks.** Every data connection logs in as its tenant's own login, `iverson_t_<suffix>`, which holds only that tenant's role. `iverson_app` keeps only `OPERATE ON SYSTEM`, for `/health` and the readiness probes.
- **The worker.** The owner, maintenance and provisioning credentials stay with the worker and the deploy hooks. The worker also runs the privileged request-driven work: RegisterSchema's DDL and registry write, `/admin/reconcile/{type}` and DLQ replay.
- **What does not change (spec §10, accepted).** The api mounts the StarRocks tenant key, from which every tenant's StarRocks password can be derived. Its Postgres login sets `app.tenant_id` itself, and row-level security keys on that setting, so it too reaches every tenant's Postgres rows. The worker keeps the owner, BYPASSRLS and provisioning rights. A compromised api or worker process can therefore still reach every tenant. This is inherent to a single multi-tenant service.

## 1. Before the upgrade

- **StarRocks must hold no tenant data written before this release.** There is no backfill. A tenant's login is created by its first write after the upgrade, and a tenant with no login reads as empty. On compose, run `docker compose down -v` before the first `up` of this release. On kind, create a new cluster.
- **Run `helm dependency build`** in the chart directory. A stale `charts/*.tgz` renders the old subcharts.
- **Upgrade with `helm upgrade --wait`.** The StarRocks role revoke (§2, step 5) relies on every pod of the previous release being gone when the post-upgrade hooks run, and Helm waits for the rollout before the hooks only under `--wait`. Without it the hooks run as soon as the manifests are applied, and a previous-release worker still running can grant a newly provisioned tenant's role to `iverson_app` after the revoke, which nothing reports. Re-running the upgrade re-runs the revoke.
- **Compose:** run `scripts/generate-compose-secrets.sh` once. It adds `IVERSON_STARROCKS_TENANT_KEY` to `.env` and leaves the existing values alone.
- **The `stack.py query` benchmark tier** (`Iverson.Server/Iverson.LoadTest/scripts/stack.py`) starts `iverson-api` with `--no-deps`, so the worker does not run. The api's login `iverson_api` and its grants exist only once the worker has started on that Postgres volume. On a fresh volume, run the full stack once (`docker compose up -d`, until `iverson-api` is healthy) before the first `stack.py query`. Registering a type also needs the worker (§2, release notes).

## 2. What the upgrade does

1. **Two new Secrets.** Both are lookup-or-generate and kept on uninstall.
   - `<release>-postgres-api` (`kubernetes.io/basic-auth`) holds `iverson_api`'s password.
   - `<release>-starrocks-tenant-key` (profiles with StarRocks) holds the master key. Each tenant login's password is the hex HMAC-SHA256 of `starrocks-tenant:<tenantId>` under it. **Never change or delete it** (§4). Provisioning creates a login only when it is missing and never resets a password, so a new key makes every existing tenant's reads and writes fail with `Unavailable`. Deleting the Secret has the same effect, because the next upgrade generates a new key.
2. **CNPG creates the managed role `iverson_api`**, as a member of `iverson_runtime` (`charts/postgres/templates/cluster.yaml`). It does this on existing clusters too.
3. **The worker, at startup and as the database owner:**
   - creates the plumbing tables;
   - seeds the legacy tenants where `global.provisionTestIdentities` is true (the api no longer does);
   - grants `iverson_api`:

   | Object | Grant |
   |---|---|
   | database `iverson` | `CONNECT` |
   | entity tables | through `iverson_runtime`, as before |
   | `IversonReconciliationQueue` | `INSERT`, `DELETE`, `SELECT ("Id")` |
   | `IversonDlqMessages` | `SELECT` on every column except `MessageValue` |
   | `IversonTenants` | `SELECT`, `INSERT`, `UPDATE`, `DELETE` |
   | `iverson_token_revocations` | `SELECT`, `INSERT`, `UPDATE` |
   | `_iverson_schema` | `SELECT` |
   | `IversonPrivilegedRequests` | `INSERT`, `SELECT`, `UPDATE ("Status")` |
   | `IversonStarRocksTenants` | `SELECT` |

   A worker that starts before CNPG has created `iverson_api` waits up to a minute for the role, then fails its startup and is restarted. It succeeds once the role exists.
4. **The api connects as `iverson_api` and runs no DDL.**
   - A new api pod stays not-ready until the worker's grants are in place and the pod's schema registry has loaded, so the order in which pods restart does not matter.
   - The api no longer mounts `<release>-postgres-app` (the owner's Secret).
5. **The hooks, after the rollout:**
   - `<release>-authentik-revoke-cross-db` also waits for `iverson_api` and grants it `CONNECT`. This repeats the worker's grant, which is the one the api relies on.
   - `<release>-starrocks-create-user` (profiles with StarRocks) revokes every role still granted to `iverson_app`, as read from `SHOW GRANTS`. That covers the hashed `role_tenant_<readable>_<hash>` names and the legacy `role_tenant_<tenantId>` ones. Under `helm upgrade --wait` (§1), every pod of the previous release is gone by then, so nothing re-grants them.
   - If this hook fails with `unexpected role name granted to iverson_app`, a role whose name is not `[A-Za-z0-9_]` is granted to `iverson_app`. Iverson never creates one. Revoke it by hand, quoting the name with backticks, then re-run the upgrade.

**Release notes (behaviour changes for operators and callers):**
- **The StarRocks tenant key must never change** on a stack with tenant logins. See §4.
- **Registration needs a running worker.**
  - With no worker, `RegisterSchema` returns `Unavailable` after 15 s.
  - A registration that the worker has claimed but not finished within a further 10 s returns `DeadlineExceeded`, and completes in the worker. Registering again is safe.
- **The admin routes can answer before the worker finishes.** `/admin/reconcile/{type}` returns `202 {requestId}` when the reconcile is still running after 60 s, and `/admin/dlq/{id}/replay` does the same after 30 s. The worker finishes both.
- **Oversized writes from .NET clients** now fail at the API with `InvalidArgument`, instead of dead-lettering later in StarRocks. The size guard now recognises their camelCase keys as the columns they name.

## 3. Checking

Postgres:

```bash
kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -c '\du iverson_api' -c '\drg iverson_api'
kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -tA -c "SELECT
  has_database_privilege('iverson_api', 'iverson', 'CONNECT'),
  has_table_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'INSERT'),
  has_column_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'Payload', 'SELECT'),
  has_column_privilege('iverson_api', '\"IversonDlqMessages\"', 'MessageValue', 'SELECT'),
  pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')"
```

Expected:
- `\du` lists `iverson_api` with no attributes: no Superuser, Create role, Create DB or Bypass RLS.
- `\drg` shows it as a member of `iverson_runtime` only.
- The query prints `t|t|f|f|f`.

StarRocks, as root. The root password is the `root-password` key of `<release>-starrocks-app`.

```bash
kubectl -n <ns> port-forward svc/<release>-starrocks-fe-service 9030:9030
mysql -h 127.0.0.1 -P 9030 -u root -p --ssl-mode=REQUIRED -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW USERS;"
```

Expected:
- `iverson_app` holds `OPERATE ON SYSTEM`, and no role other than `public`.
- `SHOW USERS` lists `'iverson_t_<suffix>'@'%'` for each tenant that has written since the upgrade.

Compose:

```bash
docker compose exec postgres psql -U iverson -d iverson -c '\du iverson_api' -c '\drg iverson_api'
docker compose exec starrocks mysql -h 127.0.0.1 -P 9030 -u root -e "SHOW GRANTS FOR 'iverson_app'@'%';"
```

On compose, iverson-worker starts first, and iverson-api waits until the worker's `/health` answers 200. The worker connects as the superuser `iverson`, and creates `iverson_api` with the dev-only password in `docker-compose.yml`. `starrocks-init` runs the same role revoke as the chart's hook on every `up`.

## 4. The StarRocks tenant key

**Never change the key on a stack that has tenant logins.** It lives in `<release>-starrocks-tenant-key` (Helm) or as `IVERSON_STARROCKS_TENANT_KEY` in `.env` (compose).
- Each tenant's login, `iverson_t_<suffix>`, is created once, by its first provisioning, with a password derived from the key.
- Provisioning only creates logins that are missing. It never resets a password.
- So after a key change, every existing tenant's StarRocks reads and writes fail with `Unavailable` until its login is re-keyed. Tenants first provisioned after the change are unaffected.

**If the key has changed anyway**, set each recorded tenant's password to the one the new key derives. Run this as root or as `iverson_provisioner`, which holds `user_admin`.

1. **List the recorded tenants.** Every provisioned tenant has a row:

   ```bash
   kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -tA -c 'SELECT "TenantId" FROM "IversonStarRocksTenants" ORDER BY 1' > tenants.txt
   ```

2. **Derive each login's name and password from the current key.** The name is `iverson_t_` plus the same 25-digit fingerprint as the tenant's role name: the first 16 bytes of SHA-256 over the tenant id, read big-endian, written as 25 base-36 digits. The password is the lower-case hex HMAC-SHA256 of `starrocks-tenant:<tenantId>` under the key's UTF-8 bytes. This prints one statement per tenant:

   ```bash
   TENANT_KEY="$(kubectl -n <ns> get secret <release>-starrocks-tenant-key -o jsonpath='{.data.tenant-key}' | base64 -d)" \
   python3 -c '
   import hashlib, hmac, os, sys
   key, digits = os.environ["TENANT_KEY"].encode(), "0123456789abcdefghijklmnopqrstuvwxyz"
   for tenant in (l.strip() for l in sys.stdin if l.strip()):
       n, fp = int.from_bytes(hashlib.sha256(tenant.encode()).digest()[:16], "big"), ""
       for _ in range(25):
           fp, n = digits[n % 36] + fp, n // 36
       pw = hmac.new(key, ("starrocks-tenant:" + tenant).encode(), hashlib.sha256).hexdigest()
       print(f"ALTER USER '"'"'iverson_t_{fp}'"'"'@'"'"'%'"'"' IDENTIFIED BY '"'"'{pw}'"'"';")
   ' < tenants.txt > rekey.sql
   ```

3. **Run `rekey.sql` against the FE** as root, as in §3. Then delete `tenants.txt` and `rekey.sql`: the statements carry the passwords.

The derivation matches the server's (`TenantIdentifier.LoginName` and `LoginPassword`). For the tenant id `acme` and the key `test-tenant-login-key`, it gives `iverson_t_7pfddyte668561tz83en42m5m` and a password starting `401cfbf7`, the vector the unit tests pin.

## 5. Rolling back

The previous release reads StarRocks as `iverson_app` through `SET ROLE` on the tenant's role, and this release's hook revoked those roles from `iverson_app`. After `helm rollback` to it, the previous release treats a tenant whose role is not granted as having no data, so every tenant's StarRocks-backed reads return empty, with no error, until that tenant's next write, when the previous release's worker grants the role again. Re-granting each `role_tenant_*` role to `iverson_app` as root restores the reads at once.
