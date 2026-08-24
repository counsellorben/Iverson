# Server-Owned Tenant Column (`__TenantId`) — Upgrade Runbook

**Why:** the `remove-IversonTenant` branch moves the tenant column from a **client-declared**
property (`[IversonTenant] public string TenantId`, and the four other languages' equivalents) to a
**server-owned** column named `__TenantId` that the server injects and the client can neither declare
nor name. On a *fresh* install nothing here applies. On a **populated deployment** the change makes
the old client-declared column an ORPHAN for the first time, and the orphan-drop path collides with
the row-level-security policy that predicates on it. **Registration then FAILS mid-migration.**

This is a hard cutover with no warn-only window, the same shape as
`grpc-admin-auth-cutover.md`. Read the failure account before running anything.

## What happens if you skip the teardown

`SchemaRegistrationOrchestrator.RegisterAsync` phase 3 (`SchemaRegistrationOrchestrator.cs:251-264`)
loops per descriptor and calls `PostgresSchemaManager.ApplySchemaAsync` for each. For an existing
table it runs, in this order and **outside any transaction** (`PostgresSchemaManager.cs:58-61` states
this deliberately):

1. **Drift detection** (`:69-86`) — passes. It compares only the SQL *type* of columns present in
   both, and the new `__TenantId` is present in neither the old table nor the old descriptor.
2. **`ADD COLUMN IF NOT EXISTS "__TenantId" TEXT NOT NULL DEFAULT ('')`** (`:88-96`) — **succeeds.**
   Postgres accepts a `NOT NULL` on `ADD COLUMN` when a `DEFAULT` is supplied, and backfills every
   existing row. Verified live: a pre-existing row comes back with `__TenantId = ''`. There is no
   "Postgres rejects the constraint while legacy NULLs remain" problem; the plan text used to claim
   one and it was false.
3. **`ALTER TABLE "<table>" DROP COLUMN IF EXISTS "TenantId"`** (`:104-110`) — **REFUSED:**

   ```
   cannot drop column TenantId of table <table> because other objects depend on it
   DETAIL:  policy <table>_tenant_isolation on table <table> depends on column TenantId
   ```

   The policy was created by this same manager on a previous registration
   (`:126-150`, `USING ("TenantId" = current_setting('app.tenant_id', true))`), and the
   existence check at `:129-131` is by policy NAME — so a re-register never recreates or repoints
   it, it just finds one already there and moves on.

Four consequences, all of which a diagnostician should expect:

- **The error surfaces as gRPC `Unknown`, not `FailedPrecondition`.** The orchestrator's catch at
  `:257` handles only `SchemaDriftException` (thrown at `PostgresSchemaManager.cs:78`, and only for a
  type mismatch). A raw `PostgresException` escapes uncaught.
- **The table is left HALF-MIGRATED.** The statements are not transactional, so `__TenantId` has been
  added and `TenantId` is still there. Nothing rolls back.
- **Every retry fails identically** until an operator drops the policy by hand. The failing statement
  is the same one each time.
- **Earlier types in the batch are already registered.** Phase 3 is a per-descriptor loop, and
  `registry.RegisterAsync` runs immediately after each successful `ApplySchemaAsync` — so a batch
  that fails on type 7 has types 1-6 applied and persisted in `_iverson_schema`.

## Teardown procedure (the verified path)

This is what Task 6 of the plan actually ran and what the live matrix was graded against. It is
**destructive to entity data** — export first if that data matters; see "If you cannot drop the data"
below for the alternative. `_iverson_schema` is the registry table.

1. **Stop the writers.** Scale the API and the worker role to zero, or otherwise stop accepting
   registrations and writes. Both roles run schema self-heal loops on boot.

2. **Drop the entity tables and their registry rows.** Not `ALTER` — drop. `DROP TABLE` takes the
   table's RLS policy with it, so no separate policy step is needed on this path. This is also what
   removes the pre-cutover `_iverson_schema` rows (see "Why the registry rows must go" below).

   ```sql
   DO $$
   DECLARE r record;
   BEGIN
     FOR r IN SELECT schema_json->>'tableName' AS t FROM _iverson_schema
     LOOP
       EXECUTE format('DROP TABLE IF EXISTS %I CASCADE', r.t);
     END LOOP;
   END $$;
   DELETE FROM _iverson_schema;
   ```

3. **Confirm no policy survived.** A table in `pg_policies` but not in `_iverson_schema` — a stale
   registry, a hand-created table — was not dropped above and will still refuse the orphan drop:

   ```sql
   SELECT tablename, policyname FROM pg_policies WHERE policyname LIKE '%\_tenant\_isolation';
   -- expect zero rows; drop any survivor by name:
   -- DROP POLICY "<policyname>" ON "<tablename>";
   ```

4. **Bring the API and worker back up**, then **re-register from each client**. This creates the
   tables with `__TenantId` and creates each RLS policy against it.

5. **Verify** before declaring it done:

   ```sql
   -- every registry row must carry the reserved name, and no row may carry a client-declared one
   SELECT type_name, schema_json->>'tenantColumn' FROM _iverson_schema;
   -- every policy must predicate on __TenantId
   SELECT tablename, policyname, qual FROM pg_policies WHERE policyname LIKE '%\_tenant\_isolation';
   ```

   Then run the live conformance matrix (`Iverson.ClientConformance/Program.cs`, configured by
   `IVERSON_GRPC_URL` and `IVERSON_POSTGRES_CS`) across all ten scenarios and all five clients.

## If you cannot drop the data

**UNVERIFIED — derived from the mechanism above, NOT exercised on this branch.** The destructive path
is the one that was run and graded; this one is written down because an operator with production rows
has no other option and will otherwise improvise. The shape is: break the dependency that refuses the
orphan drop, snapshot the tenant values, let the manager migrate, then carry the values across. Per
table, with the writers stopped:

```sql
-- 1. snapshot the mapping BEFORE anything drops the column
CREATE TABLE "<table>_tenant_backup" AS SELECT "Id", "TenantId" FROM "<table>";
-- 2. remove the dependency that refuses the orphan DROP
DROP POLICY "<table>_tenant_isolation" ON "<table>";
```

Then re-register. `ADD COLUMN "__TenantId" ... NOT NULL DEFAULT ('')` fills every existing row with
the EMPTY STRING, the orphan `DROP COLUMN "TenantId"` now succeeds, and the manager creates a fresh
policy predicating on `__TenantId`. **Until the backfill runs, every existing row has an empty tenant
and is invisible to every tenant-scoped read.** Backfill immediately:

```sql
UPDATE "<table>" t SET "__TenantId" = b."TenantId"
  FROM "<table>_tenant_backup" b WHERE b."Id" = t."Id";
```

Because the registration statements are not transactional and phase 3 is a per-descriptor loop,
verify each table individually rather than assuming a batch either fully succeeded or fully failed.

## Run the matrix TWICE and grade only the second

The teardown empties the registry, so every type is newly registered. `SchemaRefreshWorker` polls
Postgres every 30 s, and the store consumers `return` on an unknown type — **committing the Kafka
offset**, so the drop is terminal and never retried. Any write landing inside that staleness window
is permanently lost from every projection, and the matrix reads it as
`0 of N row(s) visible to Search` across every language. This is a real pre-existing defect
(no fix on this branch), and post-teardown is exactly when it fires. Either run the matrix twice and
grade the second run, or restart the worker after re-registration and then run it once.

## Why the registry rows must go

`SchemaRegistry.LoadAsync` rehydrates `_iverson_schema` rows verbatim, with no normalisation. A row
written between 2026-07-17 and this branch persisted `tenantColumn` as the **client-declared** name
(typically `TenantId`); a row older than that carries no `tenantColumn` key at all. At HEAD:

- **No key, or an explicit null/empty:** the row is REFUSED, logged at Error, and the type is simply
  not registered. It fails closed — every read, write and projection for it fails until re-registered.
- **A client-declared NAME:** the row IS admitted, and the type runs with its boundary on the client's
  own column. That is deliberate and it is safe — the RLS policy, the write-path injection and the
  read-time predicate all follow `SchemaDescriptor.TenantColumn` — but it is a *second* shape of live
  schema, and several branches exist only to serve it (the tenant-immutability branch at
  `AuthorizationFieldMasking.cs:99-104`, and the reserved-name gating in
  `SchemaBuilder.ToEngagementQuerySchema`). Leaving such rows in place indefinitely is supported but
  not intended; the teardown is what retires them.

## Known unrelated failures on a two-role deployment

If both the API and the worker boot **simultaneously against a non-empty `_iverson_schema`**, one of
them can die with Postgres `XX000 "tuple concurrently updated"` from `PostgresSchemaManager.cs:149`
(the `GRANT ... TO iverson_runtime`) inside the self-heal loop. `XX000` is caught nowhere; the process
exits and only `restart: unless-stopped` recovers it. This is pre-existing, untouched by this branch,
and fires on **every routine redeploy** of the two-role deployment, not just this upgrade. It is not a
symptom of the cutover — do not chase it here.
