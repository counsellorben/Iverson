# Critical Implementation Review: 2026-08-24-vector-search-entity-binding-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson-followups/docs/plans/2026-08-24-vector-search-entity-binding-implementation-plan.md
**Verified plan-level assumptions section:** present (23 rows)

⚠️ 1 commit since plan-write time (SHA 1faae1c): `3a98b34 add vector-search entity binding implementation plan` — the plan's own commit, touching no source. Cited file:line references re-checked under §1 regardless.

## 0. Coverage enumeration

| Row | Disposition |
|---|---|
| T1 · step prose 1 (test-first, host class, three assertions, both-cases schema) | → §2.1, §2.2, §2.3 |
| T1 · step prose 2 (run and watch fail for the stated reason) | ok — the existing sibling test `SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload:96` already reaches the `:278` loop and passes, so the path is live and a new assertion on `Data` fails for the intended reason, not a setup error |
| T1 · step prose 3 (name lookup + `key` special case + `UpperFirst` fallback) | ok — `ToCamelCase` (`NamingExtensions.cs:17`) and `UpperFirst` (`ProtoPayloadHelper.cs:12`) are exact inverses; the `key` special case is required because `ToCamelCase("Id")` is `"id"` while `IntelligenceStoreConsumer.cs:417` writes `["key"]`, and `SchemaBuilder.cs:53` keeps the key out of `ScalarColumns` |
| T1 · step prose 3 (type mapping: case-insensitive, exact-not-prefix, parse-failure fallback) | ok — every `SqlType` producer is `SchemaBuilder.cs:57/:191/:198`; the emitted vocabulary (`:351-359`, `:367-380`) is fully covered by the spec's list, with array forms falling to the `Value.ForString` default |
| T1 · step prose 3 (`using System.Globalization`) | ok — absent from the file (`grep -c` = 0); the plan adds it |
| T1 · step prose 3 (`exemptField` → `schema.KeyColumn.Name`) | → §2.1 |
| T1 · step prose 3 (do not reuse `SqlTypeToPayloadKind`) | ok — it assigns `INTEGER[]` the `Integer` kind (`SchemaBuilder.cs:369`) though the payload value is a serialized string, and it is `private` to `SchemaBuilder` |
| T1 · step prose 4-5 (re-run class, full suite, reap) | ok — `Iverson.Api.Tests.csproj` is in `Iverson.slnx`; `scripts/reap-testcontainers.sh` exists |
| T1 · step prose 6 (commit convention) | ok — `git log --format=%s` is lowercase imperative, no prefix |
| T1 · code blocks | none — the task is stated wholly in prose; nothing to syntax-check |
| T1 · wiring/integration text (lookup built once per request, before the loop) | ok — `schema` is bound at `:130`, the loop is at `:278` in the same method body (`SearchSimilar` declared `:125`) |
| T2 · step prose 1-2 (rebuild image, redeploy, run matrix) | ok — `docs/runbooks/client-conformance-matrix.md` exists |
| T2 · step prose 3 (acceptance criteria) | → §2.4 |
| T2 · step prose 4 (report only cells read to completion) | ok — matches the standing reporting constraint |
| Contract T1 → T2 (T2 verifies T1's change across a deploy boundary) | ok — T2 rebuilds and redeploys before running, so the matrix cannot grade a stale image; T1 introduces no symbol T2 references |
| Contract T2 → conformance harness (`vector-search` scenario name) | ok — real scenario S7; `VectorSearchScenario = "vector-search"` at `Iverson.Client.Conformance.Driver/Program.cs:35` |
| Rule · name resolution, under-inclusion (a key that should resolve but doesn't) | ok — payload keys are produced by `ToCamelCase(name)` at `IntelligenceStoreConsumer.cs:422/:435/:440`, so the camelCase-keyed lookup matches exactly; vector fields and FK columns intentionally take the `UpperFirst` fallback |
| Rule · name resolution, over-inclusion (a key wrongly resolved) | dropped — a scalar property literally named `Key` would collide with the `"key"` special case, but that collision already exists on the write path (`IntelligenceStoreConsumer.cs:417` and `:435` both target `pointPayload["key"]`) and is not introduced by this plan |
| Rule · tenant-column stripping survives the rename | ok — `RemoveTenantColumn` (`AuthorizationFieldMasking.cs:171-178`) filters on `IsTenantColumn`, which compares `OrdinalIgnoreCase` against `"__TenantId"` (`SchemaDescriptor.cs:13,20-21`); `ToCamelCase("__TenantId")` is unchanged, and the PascalCase rename is equally matched |
| Rule · type mapping, over-inclusion (prefix collision) | ok — the plan mandates exact matching; `"DOUBLE PRECISION[]"` would otherwise prefix-match `"DOUBLE PRECISION"` |
| Rule · type mapping, under-inclusion (a production SqlType outside the vocabulary) | ok — enumerated all three `ColumnDescriptor` producers; the only literal outside `ClrTypeToSql` is `"TEXT"` at `SchemaBuilder.cs:191`, which the vocabulary covers |
| Dynamic · int64 precision loss through `Value.ForNumber(double)` | dropped — `ToProtoValue` already does `long l => Value.ForNumber(l)` (`ObjectSearchGrpcService.cs:939`); proto `Value` has no integer kind, so this is the existing contract, not a new defect |
| Dynamic · parse failure on a malformed numeric payload | ok — the plan falls back to `Value.ForString` rather than failing the row |

## 1. Verified-plan-assumptions cross-check

A1-A16, A18, A19, A20, A22, A23 — **still hold** under fresh reads of the cited evidence in the correct worktree (`tenant-followups`). A17 is recorded in the plan as failed-and-folded-in; that record is accurate (`grep -c "using System.Globalization"` returns 0).

A21 — **fails as scoped.** The row states the `exemptField` change "holds, conditionally", the condition being that spec §1's `"key"` special case is implemented alongside it, and asserts "step 1 tests it." The second half is false: the test the plan specifies cannot exercise the condition. See §2.1.

**Span check — uncovered dependencies, both verified in-round:**

1. *No assumption covers the flattened payload string round-tripping through the plan's parse.* Task 1's type mapping parses `r.Payload`'s string values, but the plan never states what form the flattening produces. Verified: `IntelligenceVectorService.ToCanonicalString` (`:202-207`) renders `IntegerValue`/`DoubleValue` with `CultureInfo.InvariantCulture` and `BoolValue` as lowercase `"true"`/`"false"`. `double.TryParse(..., InvariantCulture)` and `bool.TryParse` both accept these. **Holds.**
2. *No assumption covers tenant-column stripping surviving the rename.* `RemoveTenantColumn` runs at `AuthorizationFieldMasking.cs:203`, deliberately before the `allowedFields is null` early return, and the plan renames every payload key. Verified: the match is `OrdinalIgnoreCase` against the `"__TenantId"` const, so both `__TenantId` and any re-cased form are removed. **Holds.**

## 2. Literal-wrongness findings

### 2.1 The plan's masking assertion cannot fail, so the change spec §3 requires is untested

**Evidence.** Step 1 asserts "the identity field is emitted as the key column's name (`Id`) and survives masking", against a schema built from `SchemaFixtures.ArticleSchema()`. That fixture uses `BypassAuthorization()` (`SchemaFixtures.cs:32-33`), which passes an **empty** `List<FieldPermission>`. `RowFieldAuthorizationEvaluator.cs:82` builds `AllowedFields` only `if (excluded.Count > 0)`, so with no field permissions `AllowedFields` is null. `MaskDisallowedFields` then returns at `AuthorizationFieldMasking.cs:205` (`if (allowedFields is null) return;`) — before the loop that reads `exemptField`.

The `exemptField` argument is therefore dead in this test. The assertion passes identically whether the implementation uses `exemptField: schema.KeyColumn.Name`, leaves it at `"Key"`, or passes `null` — i.e. it cannot detect the exact regression spec §3 exists to prevent ("without this the masking strips the identifier as soon as the keys become canonical").

**Proposed fix.** The test must drive a schema whose `Authorization` carries at least one `FieldPermission` excluding some field, so `AllowedFields` is non-null and the masking loop runs. Then assert that the identity field is still present in `written[0].Data` while the excluded field is gone.

### 2.2 The identity assertion has no subject — no `key` entry is seeded

**Evidence.** Step 1 models the new test on `SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload`, whose two `UpsertNamedAsync` calls seed exactly `["wordCount"]` and `["tenantId"]` (`ObjectSearchVectorIntegrationTests.cs:115-120`). There is no `"key"` entry. The plan's `"key"` → `schema.KeyColumn.Name` special case — the load-bearing half of §2.1's pairing — therefore has nothing to act on, and the third assertion ("identity field is emitted as `Id`") would fail on a missing key rather than passing on a correct rename.

**Proposed fix.** Seed `["key"] = <the point's key>` in the test's payload dictionaries, alongside `wordCount`.

### 2.3 The uppercase-SqlType column is declared but never populated, so the A22 mitigation is inert

**Evidence.** Step 1 requires the test schema to "carry both an uppercase and a lowercase `SqlType` — e.g. keep `new ColumnDescriptor("WordCount", "integer", false)` and add one declared `"BIGINT"`." Adding a `ColumnDescriptor` only extends `ScalarColumns`; the emitted `Struct` contains a field for a column only when the Qdrant payload carries that key. The step does not say to seed a payload value for the new `"BIGINT"` column, so the uppercase arm of the type mapping never executes and a lowercase-only implementation still passes — which is precisely the failure A22 was folded into the plan to prevent.

**Proposed fix.** Seed a payload entry for the uppercase-declared column in both `UpsertNamedAsync` calls and assert its emitted `KindCase` is `NumberValue`.

### 2.4 Task 2's acceptance criterion names output the harness does not emit

**Evidence.** Step 3 says to confirm "the run reports `0 untouched of 43`". The harness prints an untouched line **only on failure**: `Program.cs:252-257` guards `if (flags.IsFullMatrix && untouched.Count > 0)` and writes a `FAIL: ... but N was/were not: ...` message. A clean full-matrix run prints nothing about untouched requirements; success is expressed as the process exit code via `Report.RunSucceeded(report.AllPassed, flags.IsFullMatrix, untouched)` at `:260`. An executing agent held to "never report an exit code not personally read to completion" cannot satisfy this criterion as written.

(The count itself is correct — `Requirements.cs` declares exactly 43 `public const string` requirement IDs.)

**Proposed fix.** State the criterion as the harness's actual success semantics: the full-matrix run exits 0, and no `FAIL: this was a full-matrix run...` line appears on stderr.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** §3 is empty. §2 carries four findings, three of which (2.1, 2.2, 2.3) are the same underlying defect class: Task 1's test, as specified, cannot falsify the parts of the implementation the plan most depends on. A21's cross-check fails for the same reason. Address these before dispatching to `subagent-driven-development`, or the task will report green while leaving both spec §3's masking change and the A22 casing mitigation unverified.
