# Go Declaration Composability and Key-Field Validation — Design

**Date:** 2026-08-02
**Status:** Approved, awaiting critical design review
**Scope:** the Go client's tag grammar, plus a key-field validation check in all five clients, plus two documentation corrections. No server change, no proto change.

## Problem

Two problems, deliberately specified together because the first widens the second.

### 1. Go still carries the mutually-exclusive `kind` axis

The proto's `PropertyDescriptor` is a flat set of independent booleans. Four of the five clients now match it: .NET and Java use one annotation per declaration, TypeScript uses one decorator per declaration, and Python's axis was removed on 2026-08-02 (`645f160`).

Go does not. `tags.go:88-98` defines nine `Kind*` constants sharing a single `FieldMeta.Kind string`, and `registrar.go:85-91` derives the wire flags by comparison — `IsKey: fm.Kind == KindKey`, `IsLargeField: fm.Kind == KindLargeField`, and so on. The package doc states the rule outright: "kinds are mutually exclusive."

So `large_field`+`chunk` — legal at the server, and expressible in the other four clients — cannot be written in Go. This is the fifth instance of a bug class that has now recurred five times, and the last one outstanding.

Go has already escaped the axis once, partially. `iverson_meta`, `iverson_tenant`, `iverson_desc`, `iverson_summary`, `iverson_keywords`, `iverson_extract` and `iverson_contextual` are all independent struct-tag keys, and `tags.go:32-39` documents exactly why: "`iverson_meta` is a separate tag key rather than an `iverson` kind because it composes with the kinds." Six declarations took that escape hatch; the five scalar kinds did not.

### 2. One class of illegal declaration fails silently, not loudly

The Python work accepted a deliberate regression: removing `kind` made server-rejected combinations newly expressible, and no client-side validation was added, on the reasoning that the server rejects them with a clear `InvalidArgument` so the failure stays loud.

That reasoning holds for most of the surface but not all of it. The server's schema builder collects every per-property declaration inside a loop over **non-key** properties (`SchemaBuilder.cs:53` — `Where(p => !p.IsKey)`), with only the key's description handled separately (`:50-51`). Its own comment says so: "[IversonDescription] is valid on any property including the key… unlike every other collection below, which is built from non-key properties only."

So a declaration such as `search_key` or `metadata` on the key field is **accepted and silently discarded**. The user gets a schema that differs from what they declared, with no error at any layer. That is not the loud failure the original ruling relied on.

## Goals

1. Remove `kind` as the composition axis for Go's scalar declarations, closing the last instance of the bug class.
2. Reject, in all five clients, the one class of declaration the server accepts-then-discards.
3. Correct two documentation claims that are now known to be false.

## Design

### 1. Go: independent tag key per scalar declaration

`FieldMeta.Kind string` becomes `RelationKind string`, holding one of the four relation kinds or `""`. Relations stay exclusive by design — they serialize to `RelationDescriptor`, a different proto message, and a field is either a scalar property or a relation.

Five independent booleans replace the scalar kinds: `IsKey`, `IsSearchKey`, `IsLargeField`, `IsEmbedding`, `IsChunk`. The existing `SearchKeyOrder`, `ChunkMaxTokens`, `ChunkOverlap`, `ChunkContextual`, `Metadata`, `Tenant`, `Description`, `IsSummaryTarget`, `IsKeywordsTarget` and `ExtractHint` fields are unchanged. None of the five new names collides with an existing field.

Five new tag keys join the seven that already exist:

| Tag | Value grammar |
|---|---|
| `iverson_key:"true"` | boolean |
| `iverson_search_key:"N"` | 0-based order integer |
| `iverson_large_field:"true"` | boolean |
| `iverson_embedding:"true"` | boolean |
| `iverson_chunk:"true"` / `"256"` / `"256:32"` | defaults / maxTokens / maxTokens:overlap |

```go
type Article struct {
    Id       string `iverson_key:"true"`
    Body     string `iverson_large_field:"true" iverson_chunk:"256:32"`
    Category string `iverson_search_key:"0" iverson_meta:"true"`
    AuthorId string `iverson:"many_to_one:Author"`
}
```

`iverson_chunk` preserves the existing `maxTokens[:overlap]` sub-grammar rather than inventing a new one. `iverson_search_key` takes the order integer only; today `iverson:"search_key"` with no argument defaults to 0, and that spelling becomes `iverson_search_key:"0"`. One spelling per declaration.

**`ParseTag` keeps its exported signature** — `ParseTag(fieldName, tagValue string) (FieldMeta, error)` — and narrows to parsing relation kinds from `iverson:`. The five new keys are read at the existing assembly point (`tags.go:228-235`), beside the seven independent keys already read there via `sf.Tag.Get(...)`. Changing `ParseTag` to accept a `reflect.StructTag` would break an exported API and force rewrites at `coordinator.go:427` and every test call site, for no gain.

**Five call sites read the scalar `Kind` today and must change.** The design's first draft named only the first of these; all five are in scope:

| Site | Current | Becomes |
|---|---|---|
| `registrar.go:85-91` | `IsKey: fm.Kind == KindKey`, and four siblings | reads the booleans directly |
| `coordinator.go:125` | `if f.Kind == KindKey` | `if f.IsKey` |
| `coordinator.go:151` | `if f.Kind == KindKey` | `if f.IsKey` |
| `tags.go:246` | `if fm.ChunkContextual && fm.Kind != KindChunk` | `&& !fm.IsChunk` |
| `sample/main.go:24` | `if f.Kind == ""` — plain-field detection | see below |

`sample/main.go:24` needs a definition, not a mechanical rename. Under the axis, "plain field" was `Kind == ""`. Under independent flags it becomes "no scalar flag set and no relation kind" — the sample must test that explicitly rather than compare one field to `""`.

`registrar.go:106`, `:227`, `:244` and `tags.go:250` also switch on `Kind`, but over relation kinds only; they follow the `RelationKind` rename mechanically. `tags.go:250` is relation-vs-scalar routing plus tenant collection, not validation: its `switch fm.Kind` becomes a `fm.RelationKind != ""` test with the `default:` branch unchanged. Rewriting it against the scalar booleans would send every plain, untagged field into `meta.Relations`.

The five scalar `Kind*` constants are deleted. The four relation constants stay.

### 2. Key-field validation in all five clients

At registration time, before the request is sent, each client rejects an entity whose key field carries a scalar declaration other than `description`.

**In scope — the silently-discarded set:** `search_key`, `large_field`, `embedding`, `chunk`, `metadata` on the key field. Each is collected only inside `SchemaBuilder.cs`'s non-key loop, so each is accepted and dropped.

**Out of scope — already loud:** `summary`, `keywords` and `extract_hint` on the key field. `SchemaRegistrationOrchestrator.cs:142-150` throws `InvalidArgument` for an enrichment target that is the key, tenant or owner field. Duplicating that client-side would place a server rule in a fifth location for no benefit.

**Out of scope — not discarded:** `tenant` on the key field. Tenant maps to `TypeDescriptor.TenantField` at type level and reaches `SchemaDescriptor.TenantColumn` via `SchemaBuilder.cs:172`, never through the per-property loop. Whether a primary key doubling as the tenant column is *sensible* is a separate question from whether it is silently discarded; it is not, so it stays out.

**`description` on the key remains legal** and must keep working. Java's `AnnotationTest.java:22-24` already declares `@IversonKey` together with `@IversonDescription`, and `SchemaBuilder.cs:50-51` honours it.

**Placement.** .NET, Java and TypeScript declare with independent attributes and decorators, so "is this the key field *and* does it carry another flag" is answerable only once the whole type is assembled. That puts the check in each client's schema-registrar step. Python and Go could check earlier, but siting it identically in all five keeps one rule with one wording.

Each client already performs exactly this shape of registration-time validation for the tenant boundary, and the new check follows it in placement and idiom:

| Client | Precedent | Error type |
|---|---|---|
| .NET | `SchemaRegistrar.cs:93` `ResolveTenantField` | `InvalidOperationException` |
| Java | `SchemaRegistrar.java:125` `resolveTenantField` | `IllegalArgumentException` |
| TypeScript | `core.ts:254-266` tenant-field resolution | `Error` |
| Python | `core.py:204` `_resolve_tenant_field` | `ValueError` |
| Go | `tags.go:265-268` tenant-marker check | returned `error` |

The message names the type, the key field, and the offending declaration.

### 3. Documentation corrections

**The Python spec's parity claim is false.** `docs/specs/2026-08-01-python-declaration-composability-design.md:231-233` states "The other four clients already compose correctly; Go was fixed at `e4a77ff`." `e4a77ff` moved only `metadata` off Go's axis. The line is corrected to record that Go carried the same axis and is fixed by this spec, with .NET, Java and TypeScript verified composable.

**That spec's Known issues understates the surface.** It names only `metadata` + embedding/chunk/array/large_field as newly expressible and server-rejected. Two classes are added: `search_key` + `large_field`/`chunk`/`embedding` (`SchemaBuilder.cs:117-121`; note `IsEmbedding` and `IsChunk` implicitly add the column to `largeFields` at `:63` and `:76`, so `search_key`+`chunk` trips the same rule), and the key-field class — recorded as no longer accepted-and-discarded, because §2 adds the client check.

**Go's package doc is rewritten.** `tags.go:1-43` documents the tag format and asserts "kinds are mutually exclusive"; `tags.go:68-69` repeats it, and `tags.go:29-30` and `:83-84` describe `iverson_contextual` as valid "only alongside `iverson:\"chunk...\"`". All four are false once §1 lands. `tags.go:247` also carries the old form inside a runtime error message ("…is not a chunk field (iverson:\"chunk...\")…"), which would tell users to fix their tag with a syntax that no longer exists; it is rewritten with the other five.

## Testing

- Go: a field carrying `iverson_large_field:"true"` and `iverson_chunk:"256:32"` together produces a `PropertyDescriptor` with **both** `IsLargeField` and `IsChunk` set, and the chunk windowing values. Both halves asserted — a one-sided test lets the other declaration be silently dropped.
- Go: the existing relation tests pass unchanged, confirming the relation axis is intact.
- All five clients: registration of an entity whose key carries `metadata` is rejected, **and** registration of an entity whose key carries only `description` still succeeds. Both halves, so a client that rejects everything on the key passes only the first.

## Migration

Every existing Go struct tag for a scalar field changes. A compatibility shim accepting the old forms is deliberately not offered — it would leave the mutually-exclusive path alive, which is the thing being removed.

Un-migrated tags fail loudly: `ParseTag` rejects an unknown kind (`tags.go:194-195`) and `InspectType` propagates the error, so an old `iverson:"key"` is a registration-time failure rather than a silently-dropped declaration. That matters here — a silent degradation would reproduce the exact failure class §2 exists to eliminate.

Known migration surface: `sample/models/article.go`, `sample/models/author.go`, `sample/models/tag.go`, `iverson/coordinator_test.go:27`, `iverson_test/registrar_test.go` (8 tags), and `iverson_test/tags_test.go` (which calls `ParseTag` directly with scalar-kind strings at roughly a dozen sites, and asserts on `fm.Kind`).

No client's existing sample or test declares a flag on its key field beyond `description`, so §2's check breaks nothing that exists today.

## Verified assumptions

Verified against `main@645f160`.

| # | Assumption | Evidence |
|---|---|---|
| G1 | Go's `FieldMeta` has no name collision with the five new booleans | `tags.go:100-132` — fields are `Name`, `Kind`, `SearchKeyOrder`, `ChunkMaxTokens`, `ChunkOverlap`, `RelatedType`, `Description`, `Metadata`, `Tenant`, `IsSummaryTarget`, `IsKeywordsTarget`, `ExtractHint`, `ChunkContextual` |
| G2 | `ParseTag`'s caller set is bounded | `coordinator.go:427`, `tags.go:228`, and ~12 direct calls in `iverson_test/tags_test.go` |
| G3 | Narrowing `ParseTag` to relations is safe at `coordinator.go:427` | `:425-432` — it parses, then `switch fm.Kind` over the four relation kinds only, to skip relation fields |
| G4 | `tags.go:228-235` is the assembly point to extend | `ParseTag(...)` followed by `fm.Description = sf.Tag.Get(DescriptionTagKey)`, `fm.Metadata = … == "true"`, `fm.Tenant`, `fm.IsSummaryTarget` — the same pattern the five new keys join |
| G5 | **Five** sites read the scalar `Kind`, not one | `registrar.go:85-91`; `coordinator.go:125`, `:151`; `tags.go:246`; `sample/main.go:24`. Four further sites — `registrar.go:106`, `:227`, `:244`, `tags.go:250` — switch over relation kinds only and follow the `RelationKind` rename |
| G6 | The five scalar `Kind*` constants are deletable | Referenced only in `ParseTag` (`tags.go:147-167`), the six sites above, and the test suite |
| G7 | Relation `Kind*` constants survive the rename | Used at `registrar.go:106`, `:227`, `:244` and `coordinator.go:428` — all relation-only |
| G8 | Full Go migration surface *(recurrence — every old-form tag)* | 3 sample model files, `coordinator_test.go:27`, 8 tags in `registrar_test.go`, and `tags_test.go`'s direct `ParseTag` calls |
| G9 | Go module exists for build/test | `Iverson.Clients/Go/go.mod` |
| G10 | Every doc reference to the old forms *(recurrence)* | `tags.go:6-12` (format block), `:29-30` and `:83-84` (`iverson_contextual` "alongside `iverson:\"chunk...\"`"), `:32-39` (composability rationale), `:68-69` ("kinds are mutually exclusive"), `:134` (`ParseTag` doc), and `:247` — a runtime `fmt.Errorf` string carrying the same `iverson:\"chunk...\"` text. Note `:247` is invisible to a `grep 'iverson:"'` because the source escapes the quote; `grep 'iverson:\\"'` returns it and nothing else |
| G11 | *(span-check gap, added round 1)* Un-migrated tags fail loudly, not silently | `tags.go:194-195` — `ParseTag`'s `default:` case returns `fmt.Errorf("iverson tag %q: unknown kind %q", …)`, and `InspectType` propagates it (`:228`). An old `iverson:"key"` tag is a registration-time error, not a silently-dropped declaration |
| G12 | *(span-check gap, added round 1)* The Go assembly point can fail | `InspectType` is declared `(EntityMeta, error)` (`tags.go:249`) and already returns errors for a blank extract hint (`:242`) and the tenant-count rules (`:265-268`), so both the new tag parsing and §2's key-field check have an established path to raise |
| V1–V3 | All five clients have a registrar-step validation precedent *(recurrence — every client)* | .NET `SchemaRegistrar.cs:75,93`; Java `SchemaRegistrar.java:113,125-127`; TypeScript `core.ts:254-273`; Python `core.py:204`; Go `tags.go:265-268` |
| V4 | Each client's test runner is identifiable | Go `go.mod`; TypeScript `package.json:15` → `vitest run`; Java `pom.xml`; Python `pyproject.toml` `testpaths`; .NET `dotnet test` |
| V5 | Non-key-only collection, with the key's description exempt | `SchemaBuilder.cs:53` `Where(p => !p.IsKey)`; `:50-51` collects `keyProp.Description`; the comment at `:47-49` states the rule |
| V6 | Enrichment targets on the key are rejected loudly | `SchemaRegistrationOrchestrator.cs:142-150` — `if (property.IsKey \|\| …)` throws `InvalidArgument` |
| V7 | `tenant` on the key is not discarded | `SchemaBuilder.cs:172` — `TenantColumn` comes from `typeDesc.TenantField`, never the per-property loop |
| V8 | `metadata` on the key is discarded | `metadataColumns` is populated only inside the `!p.IsKey` loop (`SchemaBuilder.cs:91-98`) |
| V9 | *(dependents)* No existing declaration breaks under §2's check | Python `sample/models.py:19,26,35` and tests use `iverson_key()` alone; .NET samples carry `[IversonKey]` alone; TypeScript samples `@IversonKey()` alone; Go `iverson:"key"` alone. Java `AnnotationTest.java:22-24` pairs `@IversonKey` with `@IversonDescription` — legal under the rule and confirms the carve-out is load-bearing |
| D1 | The Python spec's parity claim reads as quoted | `docs/specs/2026-08-01-python-declaration-composability-design.md:231-233` |
| D2 | The `search_key` conflict and implicit large-field adds | `SchemaBuilder.cs:117-121` throws on a search key in `largeFields`; `:63` and `:76` add embedding and chunk columns to that set |

## Known issues / accepted as out of scope

**Client-side validation of the loudly-rejected combinations is still not added.** `metadata` + embedding/chunk/array/large_field, and `search_key` + large_field/chunk/embedding, remain the server's responsibility. Ben's original ruling stands where its reasoning holds: the server throws `InvalidArgument`, the failure is immediate and legible, and duplicating the rule across five clients buys nothing. This spec changes that ruling only for the class where the server does not fail at all.

**This spec covers two independent concerns.** The Go grammar change and the five-client validation check do not depend on each other; they were specified together at Ben's direction after the decomposition was surfaced. Doing the Go work widens the validation check's reach — Go gains the ability to express the illegal combinations — which is the argument for shipping them together.

**No compatibility shim for Go's old tag forms.** Accepting `iverson:"key"` alongside `iverson_key:"true"` would preserve the mutually-exclusive parsing path and leave the bug class open for anyone using the old spelling.
