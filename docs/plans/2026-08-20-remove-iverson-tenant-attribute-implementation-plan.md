# Remove the IversonTenant attribute — implementation plan

**Source spec:** `docs/specs/2026-08-20-remove-iverson-tenant-attribute-design.md` (commit `c4ceb6c99d5edb36c48a9334aa0c7906401ede73`)
**Worktree:** `/home/ben/repositories/Iverson-conformance`, branch `client-conformance-harness`.

> All seven tasks execute in that worktree. The conformance harness, `docs/standards/`, and the
> `IVC-*` requirement IDs exist only on this branch — they are absent from `main`.

## Global Constraints

1. **Enforcement is server-side.** No client gains a reserved-name check, a `__TenantId` filter, or
   any other tenant logic. Clients lose tenant code; they never gain it.
2. **`__TenantId` never appears on the wire.** No response, projection, catalog, or search result
   may carry it. See T2 for the chokepoints.
3. **Statement-cell immutability.** A retired requirement row's `Statement` cell stays
   byte-identical. Rationale for the retirement goes in prose, never in the row.
4. **The gate must pass at every commit, not merely at the end.** `RequirementsCoverageGateTests`
   check 1 is bidirectional and check 2 requires every const in `Requirements.cs` to be cited under
   `Iverson.ClientConformance/`. Standard rows, consts, and citing assertions therefore move in one
   commit or the build is red.
5. **`.superpowers/` stays untracked.** Never `git add -f` anything under it. Three prior
   implementers on this branch force-added report files and had to be reverted.
6. **`docs/plans`, `docs/specs`, `docs/criticalreviews` are gitignored** and need `git add -f`.
   `docs/standards/` is tracked and commits normally.

## Task ordering and why it is what it is

This is one atomic breaking change. Four dependencies fix the sequence:

- The server must stop *reading* `tenant_field` before it starts *rejecting* it (T1 before T4).
- Clients must stop *sending* it before the rejection lands (T3 before T4).
- `IVC-DECL-002` and `IVC-DECL-005` grade a declaration that ceases to exist in T3, so they retire
  in that same commit — split apart, the live matrix is red across a task boundary.
- `TenantColumn`'s nullability is load-bearing for *legacy pre-cutover schema rows*
  (`SchemaDescriptor.cs:23`, `EngagementStoreConsumer.cs:56`). Those rows disappear only at T6's
  teardown, so T7 runs last.

| # | Task | Depends on |
|---|---|---|
| 1 | Server owns the tenant column | — |
| 2 | Write-path injection and the outbound strip | T1 |
| 3 | Clients stop declaring the tenant; DECL-002/005 retire | T1 |
| 4 | Inbound rejections and the orchestrator fixtures | T3 |
| 5 | REG and IDN requirements | T4 |
| 6 | Teardown, re-register, live verification | T5 |
| 7 | `TenantColumn` becomes non-nullable | T6 |

---

## Task 1 — Server owns the tenant column

**Modify:**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs`
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- `Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs`
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`

### Steps

1. In `SchemaBuilder.cs`, stop deriving the tenant column from the client. Line 172 currently reads
   `TenantColumn = string.IsNullOrEmpty(typeDesc.TenantField) ? null : typeDesc.TenantField;`.
   Replace the derivation with the server-owned constant `__TenantId`, and append exactly
   `new ColumnDescriptor("__TenantId", "TEXT", false)` to `scalars` (line 164's
   `ScalarColumns = scalars`) before the descriptor is constructed, so the column physically exists
   in the table. The type is not incidental: `ValidateFieldReference` runs for the tenant field on
   every registration (`SchemaRegistrationOrchestrator.cs:66`) and rejects any `SqlType` outside
   `TEXT`/`UUID`/`BYTEA`/`TIMESTAMPTZ` (`:182-186`), so a wrong type breaks registration globally.
   `TEXT` also matches the RLS predicate's text comparison at `PostgresSchemaManager.cs:139`. The
   `false` is `IsNullable` — the column is **NOT NULL**, so the silent-overwrite path in T2 fails
   loudly with a constraint violation rather than orphaning the row behind RLS. A test asserts both
   the type and the nullability.

2. Define the name once, as a public constant on `SchemaDescriptor` (or a sibling type in
   `Iverson.Api/Schema/`). Every consumer below references that constant — no string literals.

3. Give each of the **eight** `ScalarColumns` consumer sites an explicit, commented position on
   whether `__TenantId` is in or out. The spec names six; verification found eight:

   | Site | Position |
   |---|---|
   | `ObjectMappingGrpcService.cs:83` (`GetSchema` candidates) | **Exclude** — catalog is client-facing |
   | `ObjectSearchGrpcService.cs:773` (known-property check) | **Exclude** — not addressable by clients |
   | `ObjectSearchGrpcService.cs:787` | **Exclude** — same reason |
   | `DecayFieldResolver.cs:46` | **Exclude** — never a decay field |
   | `RowFieldAuthorizationEvaluator.cs:74` | **Exclude** — not a permissionable field |
   | `RelationValidator.cs:97` (FK column lookup) | **Exclude** — never an FK |
   | `IntelligenceStoreConsumer.cs:268`, `:377` | **Include** — passes through to projection |
   | `SchemaBuilder.cs:183/192/203/222` (four downstream projections) | **Include** — the column must reach Postgres, StarRocks, and the engagement schemas |

   `SchemaBuilder`'s four projections and `RelationValidator` have no stated position in the spec;
   the positions above are this plan's, and each needs its comment in code.

4. Tests: assert the column is injected for a descriptor that declares no tenant; assert each
   exclusion site does not see it; assert each inclusion site does.

5. **Guarantee the tenant value exists, so the NOT NULL column can always be filled.**
   `decision.TenantValue` is currently null on three paths in
   `RowFieldAuthorizationEvaluator.Evaluate` — `rules is null` (`:11-12`), `actingUser is null`
   (`:14-15`), and an absent or empty `tenant_id` claim (`:21-22`) — and the first is the ordinary
   case, since `Authorization` is optional on a schema. Two changes make the value non-null by
   construction on every write path:
   - `ActingUserInterceptor.cs:44-45` currently validates tenant status only `if (tenantId is not
     null)`. Reject an absent or empty `tenant_id` claim with `PermissionDenied`, so no request
     reaches a write path without a tenant.
   - `Evaluate`'s `rules is null` and `actingUser is null` early returns must still populate
     `TenantColumn` and `TenantValue` from the schema and the claim, rather than returning nulls.
     Only the authorization fields are unrestricted on those paths; the tenant boundary is not.

   Tests: a write to a schema registered with **no** `AuthorizationRules` stores the correct
   `__TenantId`; a call carrying a token with no `tenant_id` claim is rejected with
   `PermissionDenied`.

**Verify:** `dotnet test Iverson.slnx`

---

## Task 2 — Write-path injection and the outbound strip

**Modify:**
- `Iverson.Server/Iverson.Sql/OutboxWriter.cs`
- `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`

### Steps

1. **Inject at the one chokepoint.** All four writers reach
   `OutboxWriter.UpsertAndEnqueueOutboxAsync` — `ObjectMappingGrpcService.cs:305`, `:351`,
   `ObjectPersistenceGrpcService.cs:60`, `:124` — and each already passes
   `tenantId: decision.TenantValue`. `OutboxWriter.cs:18-28` upserts through
   `json_populate_record(null::"{table}", @Json::json)` with `updateSet` covering every column, so
   any payload arriving without `__TenantId` writes NULL over a valid tenant id. Inject there rather
   than at each caller: it is the one chokepoint no future caller can bypass, and with the column
   NOT NULL a missed injection fails loudly.

   The method receives `payloadJson` as a `string`, not a `Struct`, so the injection is a JSON
   round-trip: deserialise, set `__TenantId` from the `tenantId` parameter, re-serialise. Key casing
   must survive it — `StructSerializer.SerializePayload` (`ProtoPayloadHelper.cs:8`) upper-cases the
   first character of every key because `json_populate_record` matches column names
   case-sensitively, and `__TenantId` must reach Postgres in exactly that form.

   No clone of `request.Payload` is needed. The caller's `Struct` is never mutated on this path, so
   decision 6 holds without a second strip on the response.

   Add a test that upserts a payload with the column absent and asserts the stored value is
   unchanged.

2. **Unconditional strip.** `AuthorizationFieldMasking.MaskDisallowedFields` returns early at `:129`
   when `allowedFields is null`. The `__TenantId` strip must sit **before** that guard — a schema
   with no field permissions is exactly the case where the early return would leak the column.

3. The strip covers all five `MaskDisallowedFields` call sites:
   `ObjectRetrievalGrpcService.cs:65`, `:137`; `ObjectSearchGrpcService.cs:275`;
   `EntityRelationResolver.cs:64`; `ObjectMappingGrpcService.cs:273`.

4. **StarRocks projection.** `BuildSelectColumns` is at
   `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:113` (called from `:54` and `:84`) —
   not in `Iverson.Api` as the spec implies. Exclude `__TenantId` from the projected column list
   there.

5. Tests: for each of the five masking call sites and both StarRocks call sites, assert
   `__TenantId` is absent from the result. Include one case with `allowedFields == null` — that is
   the case step 2 exists for.

**Verify:** `dotnet test Iverson.slnx`

---

## Task 3 — Clients stop declaring the tenant; DECL-002/005 retire

**Modify (five clients):**
- **.NET** — delete `IversonTenantAttribute`; `SchemaRegistrar.cs:77` (`typeDesc.TenantField = ...`)
  and `ResolveTenantField` (`:130-146`); the markers in `Iverson.Client.Sample/Models/User.cs:16`
  and `UserArticle.cs:15`; the test models in `SchemaRegistrarTests.cs` (`:21,35,54,77,88,108,630`),
  `EntityCoordinatorPipelineTests.cs:20`, `EntityCoordinatorGroupByTests.cs:20`,
  `EntityCoordinatorVectorSearchTests.cs:20`, `EntityCoordinatorNavPropertyOmissionTests.cs:14,21,28,35`;
  and the two tests that assert the field (`SchemaRegistrarTests.cs:602-624`, `:642`).
- **Java** — delete `client/src/main/java/io/iverson/client/annotations/IversonTenant.java` and its
  resolution in `SchemaRegistrar.java`; the sample models `Author.java:18`, `Article.java:19`,
  `Tag.java:18` (fields, getters, setters) and `Main.java:31,81,98`; the test models in
  `SchemaRegistrarTest.java:53,77,102,122`.
- **Python** — `annotations.py`: `iverson_tenant` (`:193`), `tenant_fields` (`:265,299,335`);
  `core.py`: `:223`, `:293`, `_resolve_tenant_field` (`:349-362`); the package export at
  `__init__.py:12` **and** `:40` (both the import and the `__all__` entry — TypeScript's explicit
  export block at `src/index.ts:14-29` never listed the symbol, so Python is the only client with
  this second site); `sample/models.py:11,22,31,43`; `tests/test_schema_registrar.py` throughout,
  including class `TestTenantField` (`:547-594`).
- **TypeScript** — `src/annotations.ts`: `IVERSON_TENANT_FIELDS` (`:39`), `IversonTenant` (`:295`),
  `getTenantFields` (`:304`); `src/core.ts:67,387-406`; sample models `Article.ts:8,19`,
  `Tag.ts:2,10`, `Author.ts:2,10`.
- **Go** — `iverson/tags.go`: `TenantTagKey` (`:80`), the `Tenant` field-meta member (`:162`), the
  `tenantFields` collection and both errors (`:226,323-339`); `iverson/registrar.go:149-162`;
  sample models `author.go:9`, `article.go:14`, `tag.go:8`.

**Also modify (the retirement, same commit — Global Constraint 4):**
- `docs/standards/iverson-client-standard.md` — flip `IVC-DECL-002` (`:92`) and `IVC-DECL-005`
  (`:95`) to `Retired`, Statement cells byte-identical; **delete the whole Coverage row at `:103`**
  (`| Tenant field declaration | Covered | IVC-DECL-002, IVC-DECL-005 |`) — both its IDs retire, and
  `RequirementsCoverageGateTests.cs:325` fails on a Retired ID in an Evidence cell while `:280`
  fails on an empty one, so the row cannot be kept in any form. Add the retirement rationale as
  prose.
- `Iverson.ClientConformance/Requirements.cs` — delete `DeclTenantFieldDeclared` (`:34`) and
  `DeclTenantFieldTypedString` (`:63`), plus the doc comment at `:61` that cross-references
  `IVC-DECL-002`.
- `Iverson.ClientConformance/Verifier.cs` — delete the assertions at `:168` and `:180`.
- `Iverson.ClientConformance.Tests/VerifierTests.cs` — delete the tests at `:484` and `:528`.

### Note on the `tenant_id` properties themselves

Deleting the marker does not delete the property. Where a sample or test model carries a
`TenantId`/`tenant_id` field that existed *only* to be marked, delete the field too; where it
carries data the sample also reads, leave it as an ordinary scalar. Decide per model and say which
in the commit message.

**Verify:**
- `dotnet test Iverson.slnx`
- `cd Iverson.Clients/Java && mvn test`
- `cd Iverson.Clients/Python && pytest`
- `cd Iverson.Clients/TypeScript && npm test` (runs `tsc -p tsconfig.test.json` then `vitest run`)
- `cd Iverson.Clients/Go && go test ./...`

---

## Task 4 — Inbound rejections and the orchestrator fixtures

**Modify:**
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`
- the orchestrator fixtures and `Iverson.ClientConformance.Tests/SchemaCatalogScenarioTests.cs`

### Steps

1. **Invert the registration guard.** `SchemaRegistrationOrchestrator.cs:61-66` currently *requires*
   `tenant_field` and throws `InvalidArgument` when it is empty, then calls
   `ValidateFieldReference`. Replace both with the opposite rule: a descriptor carrying a non-empty
   `tenant_field` is rejected with `InvalidArgument`. Proto field 5 stays declared
   (`Iverson.Clients/Common/Proto/object_mapping.proto:100`) — update its comment to say the field
   is rejected if set.

2. **Reject `__TenantId` as a declared property.** The check runs on the **inbound
   `TypeDescriptor`** (`typeDesc`), before the `SchemaBuilder.BuildDescriptor` call at `:50` — never
   on the built `SchemaDescriptor`, which after T1 always contains the server's own injected
   `__TenantId` and would therefore self-reject every registration. A `typeDesc` declaring a
   property named `__TenantId` (as a scalar, the key, or an FK) is rejected with `InvalidArgument`.
   Case-insensitive, matching the comparison style at `:165` and `:173`. The fixture for this
   rejection must send the name through the proto, not construct a `SchemaDescriptor`.

3. **Reject `__TenantId` in a write payload.** In `EnforceWriteAuthorization`
   (`AuthorizationFieldMasking.cs`), reject a payload carrying `__TenantId` with `InvalidArgument`
   — decision 5: rejected on the way in, never silently overwritten. This is distinct from the
   existing immutability check at `:73-77`, which compares a *declared* tenant field's value.

4. **Fixtures.** Strip `tenant_field` from the four orchestrator fixtures and from
   `SchemaCatalogScenarioTests.cs`. Add the two purpose-built fixtures that step 1 and step 2's
   rejections are asserted against.

5. Tests: one per rejection, each asserting `StatusCode.InvalidArgument` and the message.

**Verify:** `dotnet test Iverson.slnx`

---

## Task 5 — REG and IDN requirements

**Modify:** `docs/standards/iverson-client-standard.md`, `Requirements.cs`, and the orchestrator
assertion code under `Iverson.ClientConformance/`.

### Steps

1. **Two REG rows** for the registration rejections T4 added: a descriptor carrying `tenant_field`
   is rejected, and a descriptor declaring `__TenantId` is rejected. Add both consts and both citing
   assertions in this same commit (Global Constraint 4), plus their `#### Coverage` rows.

2. **One IDN row** — the axis currently has Active rows but no assertions. This requirement
   **cannot be graded through the driver channel**: a driver reports what its client did, and the
   client never sees `__TenantId`, so a driver-side assertion would be manufacturing a capability
   the client lacks. Grade it orchestrator-side and **two-sided**: the orchestrator's own gRPC read
   must show the column absent, paired with a `PostgresProbe` read showing it present with the
   expected value. Two-sided is what makes it fail if injection silently stops — a one-sided
   absence check passes when the column was never written at all.

   **Cross-task contract:** `PostgresProbe` derives the table name from its *own copy* of
   `SchemaBuilder`'s naming rule, kept deliberately separate because `NamingExtensions` is internal
   to `Iverson.Api` (`PostgresProbe.cs:11,20`). If T1's edits change table naming, this copy must
   change with it or the IDN probe reads the wrong table. Use
   `PostgresProbe.FetchRowAsync` (`:40`), which returns `IReadOnlyDictionary<string, object?>?`.

3. Add IDN's `#### Coverage` table. The axis has none today; check 4 mode 1 fails on an axis with
   Active rows and no Coverage table, so the table lands with the requirement.

4. Fix the prose at `:227` describing which axes carry no assertions — IDN no longer belongs in that
   list.

**Verify:** `dotnet test Iverson.slnx` (the gate tests are the real check here)

---

## Task 6 — Teardown, re-register, live verification

This task is **partly manual and cannot be fully verified in advance.** Spec assumption A22 is
recorded as FAILED: teardown is required for correctness and nothing reports a skipped step. Treat
the ordering below as load-bearing.

Re-registration alone is insufficient. `PostgresSchemaManager.cs:88-90` ALTERs new columns in and
**never drops orphans**, `:76-78` throws only on a *type* mismatch, and `:126-150` creates the RLS
policy only when no policy of that name exists. A re-register over a live table therefore leaves the
old client-declared tenant column in place and leaves the old RLS policy pointing at it.

### Steps, in order

1. **Drop the tables and their `_iverson_schema` rows** for every registered type. Not ALTER — drop.
   This is what removes the legacy pre-cutover schema rows that T7 depends on being gone.
2. **Drop the RLS policies** by name, so `:126`'s existence check does not skip re-creation against
   `__TenantId`.
3. **Re-register** from each client. This is when `__TenantId`'s `NOT NULL` constraint is created,
   and it is why the constraint could not simply be added in place: Postgres rejects it while any
   legacy NULL remains, so steps 1-2 must have run first.
4. Run the full live conformance matrix — **all six scenarios** (`CrudRoundtrip`, `Interop`,
   `NamingRejected`, `NavPropertyRejected`, `Query`, `SchemaCatalog`), all five clients. The
   entrypoint is `Iverson.ClientConformance/Program.cs`, configured by `IVERSON_GRPC_URL` and
   `IVERSON_POSTGRES_CS`.

**Known failure mode:** if step 2 is skipped, registration succeeds and reads silently return zero
rows, because the policy still filters on a column that no longer exists.

**Out of scope, will still be red:** the StarRocks hyphenated-tenant-role defect
(`Iverson.StarRocks/TenantIdentifier.cs:13`) fails any tenant id containing a hyphen. It is a
pre-existing separate initiative, not a regression from this work.

---

## Task 7 — `TenantColumn` becomes non-nullable

Runs **after** T6. Null currently means "legacy pre-cutover schema row"
(`SchemaDescriptor.cs:23`, `EngagementStoreConsumer.cs:56`); those rows exist until T6's teardown
drops them.

Scope is larger than the spec's "two dead branches" — verification found roughly 36 sites across
**three** nullable declarations:

- `Iverson.Api/Schema/SchemaDescriptor.cs:25` — `public string? TenantColumn { get; init; }`
- `Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs:32` — record parameter
- `Iverson.StarRocks/AuthorizationConstraint.cs:7` — `string? TenantColumn = null`
- (`Iverson.Sql/IRecordStoreRoles.cs:127` also carries a nullable `TenantColumn` parameter)

### Steps

1. Make all three declarations non-nullable.
2. Remove the now-unreachable null guards. Grouped by file: `EngagementStoreConsumer.cs:60,123`;
   `EnrichmentConsumer.cs:122,244`; `IntelligenceStoreConsumer.cs:116,474`;
   `ObjectRetrievalGrpcService.cs:37,48,107,122`; `ObjectMappingGrpcService.cs:244,259,383,397,428`;
   `EntityRelationResolver.cs:61,84,117,158`; `RowFieldAuthorizationEvaluator.cs:18`;
   `AuthorizationFieldMasking.cs:55,66`; `PostgresSchemaManager.cs:126`.

   **Do not touch `SchemaRegistrationOrchestrator.cs:61`.** T4 step 1 replaced the null guard there
   with the inbound `tenant_field` rejection; by T7 it is no longer a null guard, and deleting it
   reopens the path T4 exists to close.
3. **Handle the StarRocks guards with care.** `StarRocksQueryBuilder.cs:73,97,237,343,784` and
   `StarRocksPipelineBuilder.cs:405,560` are **live tenant-predicate guards**, not dead branches —
   each gates an actual predicate in generated SQL. Each is a compound condition
   (`authz is not null && authz.TryGetValue(...) && ...TenantColumn is not null`); only the final
   conjunct becomes unreachable. The surrounding `authz`/`TryGetValue` checks must stay.
4. Delete the comments that document the nullable contract (`SchemaDescriptor.cs:23`,
   `AuthorizationConstraint.cs:7`, `EngagementStoreConsumer.cs:56-59`,
   `EnrichmentConsumer.cs:116`, and the "additive and unconditional" comments in both StarRocks
   builders).
5. Re-run the live matrix — this task edits tenant-isolation code, and unit tests alone do not
   cover the generated SQL's tenant predicate.

**Verify:** `dotnet test Iverson.slnx`, then the live matrix.

---

## Tasks NOT in this plan

From the spec's `Out of scope`:

- The StarRocks hyphenated-tenant-role defect.
- `Aggregate` grouped on `__TenantId`, ruled unreachable.
- Giving Python's `IversonClient` a channel-accepting constructor.

## Inherited from spec

The spec's 22-row `Verified assumptions` table is ground truth and was not re-verified here. Note
that A3, A11, A12, A22, A24 and A27 are recorded as **FAILED** and A19 as FALSE-benign; T6's manual
character follows directly from A22.

## Verified plan-level assumptions

Listed cold against the draft plan, then verified. Evidence is a path:line or command output.
Paths are relative to `/home/ben/repositories/Iverson-conformance`.

| # | Assumption | Status | Evidence |
|---|---|---|---|
| A1 | `SchemaBuilder.cs` is at `Iverson.Api/Grpc/`, injection at `:158` | **FAILED** | It is at `Iverson.Api/Schema/SchemaBuilder.cs`; tenant derivation is `:172`, `ScalarColumns` `:164`. Plan corrected. |
| A2 | `AuthorizationFieldMasking.cs` exists at `Iverson.Api/Grpc/` | holds | 153 lines; early return `:129` |
| A3 | `ObjectMappingGrpcService.cs` exists at `Iverson.Api/Grpc/` | holds | 472 lines; `Post` `:286`, `Update` `:322` |
| A4 | `OutboxWriter.cs` exists at `Iverson.Sql/` | holds | 105 lines |
| A5 | The four exclusion sites are all under `Grpc/` | **FAILED** | `RowFieldAuthorizationEvaluator.cs` is under `Iverson.Api/Authorization/`. Plan corrected. |
| A6 | `SchemaRegistrationOrchestrator.cs` exists | holds | `Iverson.Api/Grpc/`; tenant guard `:61-66` |
| A7 | `Verifier.cs` / `Requirements.cs` / gate tests exist | holds | `Iverson.ClientConformance/{Verifier,Requirements}.cs`, `Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs` |
| A8 | The five client marker files exist | holds | .NET `SchemaRegistrar.cs:130`; Java `annotations/IversonTenant.java`; Python `annotations.py:193`; TS `annotations.ts:295`; Go `tags.go:80` |
| A9 | Orchestrator fixtures, `SchemaCatalogScenarioTests`, `PostgresProbe` exist | holds | all three present under `Iverson.ClientConformance*/` |
| A10 | The standard lives under `docs/standards/` | holds *(on this branch only)* | `docs/standards/iverson-client-standard.md`. Absent from `main` — this was the wrong-worktree error. |
| A11 | `BuildSelectColumns` is in `Iverson.Api` | **FAILED** | It is `Iverson.StarRocks/StarRocksQueryBuilder.cs:113`, called from `:54`, `:84`. Plan corrected. |
| A12 | `ProjectField` exists and is the catalog projection | holds | `ObjectMappingGrpcService.cs:184`, called `:88` |
| A13 | `MaskDisallowedFields` is the outbound chokepoint | holds — **five** call sites | `ObjectRetrievalGrpcService.cs:65,:137`; `ObjectSearchGrpcService.cs:275`; `EntityRelationResolver.cs:64`; `ObjectMappingGrpcService.cs:273` |
| A14 | `PostgresProbe` exposes a read the IDN assertion can call | holds | `FetchRowAsync` `:40` → `IReadOnlyDictionary<string, object?>?` |
| A15 | `decision.TenantColumn` / `TenantValue` exist | holds | `IRowFieldAuthorizationEvaluator.cs:32-33` |
| A16 | .NET tests run from a solution file | holds | `Iverson.slnx` at repo root |
| A17 | The four non-.NET test commands are valid | holds | TS `package.json` `test` = `npm run typecheck && vitest run`; Python `pyproject.toml:25` `[tool.pytest.ini_options] testpaths=["tests"]`; Java reactor `Iverson.Clients/Java/pom.xml` modules client/sample/conformance; Go `Iverson.Clients/Go/go.mod` |
| A18 | The live matrix runs four scenarios | **FAILED** | Six exist: `CrudRoundtrip`, `Interop`, `NamingRejected`, `NavPropertyRejected`, `Query`, `SchemaCatalog`. Entrypoint `Program.cs`, env-configured. Plan corrected. |
| A19 | No task imports a later task's new symbols | holds | Only new symbol introduced across tasks is T1's `__TenantId` constant; T2–T7 consume it, none precede T1. |
| A20 | The gate must pass per-commit, not just at the end | holds — and it binds | Check 1 is bidirectional (`RequirementsCoverageGateTests.cs:104-127`); check 2 requires every const cited under `Iverson.ClientConformance/` (`:129-148`) |
| A21 | Retiring DECL-002/005 in T3 does not trip check 4 | holds — only if the Coverage row is deleted outright | `:325` fails on a Retired ID in Evidence; `:280` fails on empty Evidence. Both IDs retire, so the row at standard `:103` cannot survive in any form. |
| A22 | `SchemaBuilder`'s callers absorb the change | holds, with one contract | 18 call sites, all via `To*Schema` projections. **`PostgresProbe.cs:11,20` keeps a deliberate separate copy of the table-naming rule** (`NamingExtensions` is internal to `Iverson.Api`) — captured as a cross-task contract in T5. |
| A23 | `TenantColumn` non-nullable = two dead branches | **FAILED** | ~36 sites across three declarations (`SchemaDescriptor.cs:25`, `IRowFieldAuthorizationEvaluator.cs:32`, `AuthorizationConstraint.cs:7`), incl. eight *live* tenant-predicate guards in `StarRocksQueryBuilder`/`StarRocksPipelineBuilder`. Became T7 by user decision. |
| A24 | Client marker consumers are confined to the client libs | holds | Also in samples, driver models and tests across all five; enumerated per-client in T3. Java lives under `Java/client/src/main` and `Java/sample/`, not `Java/src/main`. |
| A25 | `ScalarColumns` has six consumers (spec's count) | **FAILED** | Eight production sites. `RelationValidator.cs:97` and `SchemaBuilder.cs:183,192,203,222` have no position in the spec; assigned in T1. |
| A26 | `OutboxWriter` has four call sites, all covered by T2's injection | holds | `ObjectMappingGrpcService.cs:305,:351`; `ObjectPersistenceGrpcService.cs:60,:124`; interface `OutboxWriter.cs:5` |
| A27 | `decision.TenantValue` is non-null on every write path | **FAILED** | Null on three paths in `RowFieldAuthorizationEvaluator.Evaluate`: `rules is null` (`:11-12`), `actingUser is null` (`:14-15`), empty `tenant_id` claim (`:21-22`). `ActingUserInterceptor.cs:44-45` validates tenant status only when the claim is present. Drives T1 step 5. |

**Sibling-set sweeps run:** all `ScalarColumns` consumers (A25), all five clients and every symbol each
exports (A24), all `MaskDisallowedFields` call sites (A13), all four non-.NET test commands (A17), all
`TenantColumn` declarations and guards (A23).

**Six of twenty-five failed.** Five were corrected in place; A23 changed the plan's shape and became
Task 7 on the user's decision.
