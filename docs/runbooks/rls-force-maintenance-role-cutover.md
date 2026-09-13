# Forced RLS + `iverson_maintenance` — Upgrade Runbook

**Why:** CSR round-3 finding #5 made two changes that a **populated kubernetes deployment** cannot
absorb on its own. Entity tables now get `ALTER TABLE … FORCE ROW LEVEL SECURITY`, so the tables'
owner — which is what the api connects as — is no longer exempt from its own tenant-isolation
policy; and every entity-table access now runs under one of two explicit non-login roles,
`iverson_runtime` (RLS-enforced) or the new `iverson_maintenance` (`BYPASSRLS`, for the
deliberately cross-tenant reconciliation and tenant/owner re-derivation paths).

`iverson_maintenance` is created by the CNPG cluster's `bootstrap.initdb.postInitApplicationSQL`
(`charts/postgres/templates/cluster.yaml`) — **which runs once, at initdb, and never again.** On an
already-initialised cluster the role simply will not exist, and the api cannot create it itself:
`enableSuperuserAccess: false` means the app user has neither `CREATEROLE` nor the right to grant
`BYPASSRLS`.

On a **fresh** install nothing here applies. On docker-compose nothing here applies either — the app
user there is the Postgres superuser and creates both roles itself on first boot. This is a
kubernetes-upgrade-only procedure, the same shape as `tenant-column-cutover.md`.

## The two statements, and the one people forget

```sql
CREATE ROLE iverson_maintenance NOLOGIN BYPASSRLS;
GRANT iverson_maintenance TO iverson;
```

Run **both**, as a superuser, against the `iverson` database, *before* the `helm upgrade`:

```bash
kubectl cnpg psql <release>-postgres -- -d iverson
```

The `GRANT` is not optional and is not cosmetic. Creating the role without granting it produces a
cluster where every startup check passes and the failure lands hours later in production — see the
next section. The same pair already exists for `iverson_runtime`; a cluster provisioned before that
role was introduced needs its two statements too:

```sql
CREATE ROLE iverson_runtime NOLOGIN;
GRANT iverson_runtime TO iverson;
```

`CREATE ROLE` is not idempotent — if a statement reports `42710 role already exists`, that half is
already done; run the other half anyway, it is the one that is usually missing.

## What happens if you skip it

**If you skip both statements**, `PostgresSchemaManager.EnsureRolesAsync` fails startup on the
`CREATE ROLE`'s `42501` and rethrows as an `InvalidOperationException` naming the exact SQL. The
pod crash-loops, no traffic is served, and nothing is half-migrated. This is the good failure.

**If you run `CREATE ROLE` but forget the `GRANT`**, every check downstream of it passes on its own
terms:

1. `EnsureRolesAsync`'s existence check short-circuits — the role is there.
2. Its `rolbypassrls` check passes — the role's attributes are correct.
3. `ApplySchemaAsync`'s `GRANT SELECT, INSERT, UPDATE, DELETE ON <table> TO iverson_maintenance`
   **succeeds**, because a table's owner may grant privileges to a role it is not a member of.

Startup would therefore come up green, and the first `SET LOCAL ROLE iverson_maintenance` at
runtime would throw `42501 permission denied to set role` — taking out every reconciliation replay,
every document re-render queue item, and every consumer path that re-derives an entity's
authoritative tenant or owner value, while the health endpoint kept reporting healthy.

`EnsureRolesAsync` closes that hole by **probing** rather than inspecting the catalogue: it issues
the same `SET LOCAL ROLE` the runtime issues, inside a rolled-back transaction, for both roles, and
converts a `42501` into a startup failure naming `GRANT … TO CURRENT_USER`. So the forgotten-`GRANT`
case now also crash-loops at deploy time instead of degrading silently later. Read the pod's first
log line; it names the statement to run.

## Confirming it actually applied

After the upgrade, against the `iverson` database:

```sql
-- both roles present, and ONLY maintenance carries BYPASSRLS
SELECT rolname, rolcanlogin, rolbypassrls
FROM pg_roles
WHERE rolname IN ('iverson_runtime', 'iverson_maintenance');
--  iverson_runtime      | f | f
--  iverson_maintenance  | f | t

-- the app user is a member of both (this is the check people skip)
SELECT pg_has_role('iverson', 'iverson_runtime', 'MEMBER'),
       pg_has_role('iverson', 'iverson_maintenance', 'MEMBER');
--  t | t

-- every entity table is FORCEd, not merely ENABLEd
SELECT relname, relrowsecurity, relforcerowsecurity
FROM pg_class
WHERE relnamespace = 'public'::regnamespace AND relkind = 'r' AND relrowsecurity
ORDER BY relname;
--  relforcerowsecurity must be t on every row
```

`relforcerowsecurity` is the one to read. `relrowsecurity` was already `t` before this change and
tells you nothing about whether the fix landed — that was the finding.

If some tables are `ENABLE` but not `FORCE`, you do not need to do anything by hand: `Program.cs`
re-runs `ApplySchemaAsync` over every registered descriptor on each startup, and both the `FORCE`
and the maintenance `GRANT` are idempotent, so a restart self-heals them. Only the two role
statements need a human.

## No rollback beyond `helm rollback`

There is no data migration here, so rolling the chart back is sufficient — the extra role and the
`FORCE` flag are harmless to an older api image (it simply never enters the maintenance role, and it
connects as a role that the older code paths exempt by... nothing, which is the bug being fixed, so
**do not linger in a rolled-back state**). Dropping the role is not necessary and not recommended:
`REVOKE`/`DROP ROLE` would fail while any table still grants to it.

## Related

- `docs/runbooks/tenant-column-cutover.md` — the other hard Postgres cutover; read it if this
  deployment also predates the server-owned `__TenantId` column.
- `charts/postgres/templates/cluster.yaml` — the `postInitApplicationSQL` block these statements
  mirror, for fresh installs.
