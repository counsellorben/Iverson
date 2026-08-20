# Remove the IversonTenant attribute; the server owns the tenant column

**Date:** 2026-08-20
**Branch:** `client-conformance-harness` (see *Sequencing constraint* — this cannot land on `main`)

## The problem

`[IversonTenant]` (and its four siblings) does two jobs, and only one of them is defensible.

Declaring *which column is the tenant boundary* is a real need: the server cannot infer it from an
arbitrary user-defined type, and `SchemaDescriptor.TenantColumn` drives the Postgres RLS policy, the
StarRocks predicates, and the projection consumers.

Exposing the tenant as *an ordinary settable property* is not. The value is never trusted: on create
the server force-overwrites it from the acting user's identity
(`AuthorizationFieldMasking.cs:47-56`), and on update a differing value is rejected with
`PermissionDenied "Tenant field is immutable."` (`:73-77`). So the caller is handed a property whose
value is silently discarded on one path and a hard denial on the other — **the same input, two
different outcomes, neither documented in the type system.**

This is not a security hole. The tenant boundary holds. It is an API that lies about what it accepts.

## Decisions

All six were made by Ben during brainstorming.

1. **The server owns the tenant column entirely.** No marker, and no property on the client model.
   Callers never declare it, never set it, and cannot read their tenant back off an entity.
2. **Breaking change; no migration.** Existing schemas are dropped and re-registered. No
   compatibility shim, no dual-read, no in-place column rewrite.
3. **A descriptor still carrying `TenantField` is rejected** with `InvalidArgument` naming the field.
   The proto field stays at number 5; the server refuses it rather than ignoring it, so a stale or
   hand-rolled client fails loudly at registration instead of silently registering without a tenant
   declaration.
4. **The column is named `__TenantId`** — a reserved double-underscore prefix, PascalCase like every
   other column in the system. A descriptor declaring a property by that name is rejected.
5. **`__TenantId` is rejected on the way in, not silently overwritten.** An inbound `Post`/`Update`
   payload carrying the key is rejected, making the create path symmetric with the update path and
   closing the original complaint.
6. **`__TenantId` never appears on the wire** on any of the ten client-visible read paths.

## Design

### Server-side injection

`SchemaBuilder.FromTypeDescriptor` (the sole `SchemaDescriptor` construction site,
`SchemaBuilder.cs:158`) stops reading `typeDesc.TenantField` and instead appends a `__TenantId`
scalar column to every schema it builds. `SchemaDescriptor.TenantColumn` becomes non-nullable and is
always `__TenantId`.

Two consumers (`EngagementStoreConsumer.cs:56-61`, `EnrichmentConsumer.cs:116-122`) carry explicit
fail-closed handling for a null `TenantColumn`, commented as covering "a legacy pre-cutover schema".
That branch is dead once the column is always present and comes out.

`AuthorizationConstraint.TenantColumn`'s `// null only in tests that predate the tenant boundary`
default likewise goes.

### Adding to `ScalarColumns` — the six consumers

Injecting into `ScalarColumns` is not neutral. Each consumer needs a stated position:

| Consumer | Position |
|---|---|
| `PostgresSchemaManager` DDL + RLS policy (`:126-150`) | intended — this is why the column exists; the policy already quotes the name so the prefix is legal |
| `ObjectMappingGrpcService.cs:83` (`GetSchema` candidates) | **excluded** — see the outbound strip |
| `RowFieldAuthorizationEvaluator.cs:74` (allowed-field set) | excluded, so `__TenantId` can never be named in a `FieldPermission` |
| `ObjectSearchGrpcService.cs:773` (known-filter-property check) | excluded, so a filter naming `__TenantId` is rejected as unknown rather than silently honoured |
| `DecayFieldResolver.cs:46` | excluded — a reserved column is never a decay candidate |
| `IntelligenceStoreConsumer.cs:268,377` | server-internal projection; the column flows through as data |

### Inbound rejection

`SchemaRegistrationOrchestrator` (reached from the single `RegisterSchema` entry point,
`ObjectMappingGrpcService.cs:39`) gains two `InvalidArgument` rejections, both before any DDL runs:

- the descriptor sets `TenantField` at all
- the descriptor declares a property named `__TenantId`

`AuthorizationFieldMasking.EnforceWriteAuthorization` gains a third: an inbound payload carrying a
`__TenantId` key is rejected on both the create and update paths. The create path's
`SetAuthoritativeField` call remains — it is how the column gets populated — but it now populates a
key the caller provably did not supply, rather than overwriting one they did.

### The outbound strip — three chokepoints, ten paths

The client-visible read surface is ten RPCs: `Mapping.Get`, `Retrieval.Get`, `GetMany`, `Search`,
`SearchSimilar`, `SearchChunks`, `Aggregate`, `GroupBy`, `Pipeline`, `GetSchema`. They reach the
caller through three mechanisms, so the strip needs three interventions rather than ten:

| Chokepoint | Covers |
|---|---|
| `AuthorizationFieldMasking.MaskDisallowedFields` | `Mapping.Get`, `Retrieval.Get`, `GetMany`, hydrated children via `EntityRelationResolver`, `SearchSimilar`, `SearchChunks` — six call sites across four services |
| `StarRocksQueryBuilder.BuildSelectColumns` (`:113`) | `Search`, `GroupBy`, `Pipeline`, `Aggregate` — the column is never selected, rather than stripped after the fact |
| `ObjectMappingGrpcService.ProjectField` (`:83,184`) | `GetSchema` |

The strip inside `MaskDisallowedFields` must be **unconditional**. It cannot ride on `AllowedFields`,
which is `null` when no `FieldPermission` is active — meaning "everything allowed".

Qdrant needs no handling: it isolates by collection, not by a payload field, and `Iverson.Vector`
never reads `TenantColumn`.

### The five clients

Each loses its marker and the validation that enforced it. All five currently *refuse to register* a
type with no tenant marker; that validation is deleted outright, not inverted, because the Global
Constraint puts enforcement on the server. No client gains a check that the caller avoided
`__TenantId` — if one is ever wanted it belongs in the non-normative diagnostics appendix.

| Client | Deleted |
|---|---|
| .NET | `IversonTenantAttribute`; `SchemaRegistrar.ResolveTenantField` (`:130-150`); `TenantField` on the built descriptor |
| Java | `IversonTenant` annotation; the registrar's resolution |
| Python | `iverson_tenant()` (`annotations.py:193`); the registrar's resolution |
| TypeScript | `IversonTenant()` decorator, `getTenantFields` (`annotations.ts:295,304`), the `core.ts:415` call site |
| Go | `TenantTagKey` and its two error branches (`tags.go:80,355,358`) |

The proto comment on field 5 (`object_mapping.proto:100`, currently "REQUIRED; names a declared
scalar property holding the row's tenant id") is rewritten to record that the field is rejected.

**Blast radius:** roughly sixty files declare a tenant marker — the five registrars, every sample
model, every conformance driver model, `Iverson.LoadTest/Entities/*` (3 files), and about twenty test
files across the five languages. `Iverson.Api/Tenancy/TenantSchema.cs` matches a grep for the marker
but is a false positive: it defines the `IversonTenants` registry table and is unrelated.

### The standard and the gate

`IVC-DECL-002` and `IVC-DECL-005` become **Retired**, Statement cells byte-identical per the
immutability convention, with the retirement reasoning in prose beneath the table.

Retirement is not free. Gate check 1 is bidirectional Active-IDs ↔ consts, so retiring these two
*requires*:

- deleting `Requirements.DeclTenantFieldDeclared` and `Requirements.DeclTenantFieldTypedString`
- deleting the assertions citing them at `Verifier.cs:162-180`, which have nothing left to check
- deleting the DECL Coverage row `Tenant field declaration | Covered | IVC-DECL-002, IVC-DECL-005`,
  because Check4 mode 3 forbids a Retired ID in an Evidence cell

DECL retains four Active requirements, so Check4 mode 1 does not fire on an emptied axis.

**REG** gains two Active requirements — the server rejects a descriptor carrying `TenantField`, and
rejects one declaring the reserved name — each cited by a new assertion, with matching Coverage rows
appended to REG's existing table.

**IDN** gains its first requirement: the server never emits the tenant column on any client-visible
read path. IDN is currently a bare header with an empty table, so this also requires creating its
`#### Coverage` table and making an explicit backstop decision for the axis.

`IVC-SCH-003` ("a catalogue type carries exactly the field set its registered descriptor declared")
is **preserved unchanged** by the `GetSchema` exclusion. Without that exclusion it would go red for
all five languages — it contains no tenant reference, which is exactly why a grep-driven sweep
misses it.

The prose at standard `:227` drops its "tenant/owner field typing" clause.

## Sequencing constraint

**The standard does not exist on `main`.** It lives entirely on the unmerged
`client-conformance-harness` branch, which is currently parked mid-Task-9 with two open Important
findings, and whose Tasks 10-12 (VEC, IDN, ERR) are unstarted.

Two consequences:

- This work cannot land on `main` as specified. It targets `client-conformance-harness`, or it waits
  for that branch to merge.
- Authoring an IDN requirement here populates an axis that branch's **Task 11** is scheduled to
  author. Ben ruled that authoring here is correct — the gate forbids a requirement without a citing
  assertion, not one out of plan order — but Task 11 will find IDN partly populated.

## Verified assumptions

Verified against the codebase and the running dev stack on 2026-08-20.

| # | Assumption | Result |
|---|---|---|
| A1 | `SchemaBuilder.FromTypeDescriptor` is the only `SchemaDescriptor` construction site | ✅ `SchemaBuilder.cs:158`, sole hit |
| A2 | Nothing hardcodes a tenant column name server-side | ✅ only the conformance harness's own fixtures |
| A3 | `ScalarColumns` injection needs no further change | ❌ **FAILED** — six consumers, each now given a stated position above |
| A4 | The RLS policy quotes the column name | ✅ `PostgresSchemaManager.cs:139` |
| A5 | StarRocks accepts `__tenant_id` | ✅ created and described on the live stack |
| A6 | Qdrant accepts the column as a payload key | ✅ moot — Qdrant isolates by collection; `Iverson.Vector` never reads `TenantColumn` |
| A7 | `SchemaRegistrationOrchestrator` is the single registration-validation entry point | ✅ one `RegisterSchema`, `ObjectMappingGrpcService.cs:39` |
| A8 | `TenantField` is a plain proto string | ✅ `object_mapping.proto:100`, field 5 |
| A11 | The outbound surface is six paths | ❌ **FAILED** — ten RPCs, but three chokepoints; `GetMany`, `GroupBy`, `Pipeline` were missing from the original list |
| A12 | `MaskDisallowedFields` is on every outbound path | ❌ **FAILED** — six of the call sites only; the StarRocks paths authorize at SQL-build time (`StarRocksQueryBuilder.cs:521-539`), which is a different and sound mechanism, not a gap |
| A14 | Check 1 is bidirectional Active ↔ consts | ✅ `RequirementsCoverageGateTests.cs:104-127` |
| A15 | The DECL tenant consts are cited in exactly one place | ✅ `Verifier.cs:162-180` |
| A16 | REG has a Coverage table; IDN has none | ✅ REG has 2 Covered + 3 Deferred rows; IDN is a bare header |
| A18 | The conformance `Verifier` asserts on `TenantField` | ✅ `Verifier.cs:162-168` |
| A21 | `UpperFirst` does not mangle the reserved name | ✅ `ProtoPayloadHelper.cs:12` leaves a leading underscore untouched — which is *why* `__TenantId` was chosen over `__tenant_id`; the latter would be the only snake_case column in the system |
| A24 | Little outside the clients depends on the marker | ❌ **FAILED** — ~60 files; `TenantSchema.cs` is a false positive |
| A26 | `Iverson.Api/Tenancy/TenantSchema.cs` complicates the server side | ✅ resolved — false positive, it is the `IversonTenants` registry table |

**Not verified, carried as risk:** that drop-and-re-register is achievable on the dev stack without
manual DB surgery (A22); that each client's deserializer ignores unknown payload keys, which is a
safety net only now that the strip is unconditional (A10); that the Kafka/outbox event payloads
carrying the column are server-internal and never client-visible (A13).

## Out of scope

- **The StarRocks hyphenated-tenant-role defect.** `TenantIdentifier.IsValid` admits hyphens but
  `RoleName` interpolates them into a StarRocks role name, which StarRocks rejects unconditionally —
  so every hyphenated tenant id is unprovisionable and all StarRocks reads fail for it. Verified
  directly against the running container. Pre-existing, unrelated to this design, and it needs its
  own design pass; it is recorded separately.
- **Aggregate grouped on `__TenantId`.** Ben ruled this unreachable — a caller cannot name a column
  they do not know exists — and it is not defended.
- **Giving Python's `IversonClient` a channel-accepting constructor.** An unrelated follow-up from
  the conformance branch.
