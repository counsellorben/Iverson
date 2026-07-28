# Declarative tenant field across all five clients

**Date:** 2026-07-28
**Status:** Design approved, not yet implemented
**Origin:** Closes the blocker inherited by the ingest-enrichment spec — client schema
registration cannot populate `TypeDescriptor.tenant_field`.

## Problem

`TypeDescriptor.tenant_field` (proto field 5) is marked REQUIRED, and
`SchemaRegistrationOrchestrator` rejects any schema without it:

> `tenant_field is required on '<TypeName>'.`

Four of the five clients cannot supply it, so schema registration from them fails outright:

| Client | State today |
|---|---|
| .NET | Supplies it — via an optional `tenantFieldByTypeName` dictionary on `RegisterAllAsync` |
| TypeScript | Hardcodes `tenantField: ''` (`src/core.ts:258`) |
| Java, Python, Go | Never set it; their `registerAll` signatures have no parameter for it |

The scope recorded in the ingest-enrichment spec ("neither the Go nor the TypeScript
client") was wrong: Java and Python are equally affected.

**No client has a declarative tenant marker.** Every other schema concern — `[IversonKey]`,
`[IversonSearchKey]`, `[IversonMetadata]`, `[IversonLargeField]`, and the enrichment targets
— is declared on the property. Tenancy alone is out-of-band and stringly-keyed by type name.

The .NET registrar documents this as deliberate:

> *tenant_field is REQUIRED by the server … mirrors `authorizationByTypeName`'s out-of-band,
> per-type-name mechanism since `tenant_field`, like `owner_field`, isn't attribute-derived.*

That rationale describes how .NET does it, not a constraint. The tenant field **is** a
declared scalar property; `[IversonKey]` marks one the same way. And the empirical evidence
is against the out-of-band shape: four of five clients never implemented it, TypeScript
shipped a hardcoded `''`, and even .NET's own Sample calls `RegisterAllAsync()` with no
arguments and therefore cannot register.

## Goals

- Every client can register a schema the server accepts.
- Forgetting the tenant field is impossible to do silently.

## Non-goals

- **`authorization` and `owner_field` for the four non-.NET clients.** They are the same
  out-of-band trio and equally unimplemented there, but nothing is blocked on them — the
  server rejects only on missing `tenant_field`. Folding them in would double the design to
  fix a problem nobody has hit.
- Any proto change. `tenant_field` already exists as field 5 and is already REQUIRED.
- Replicating the server's scalar-type validation client-side (see §2).

## Design

### 1. A declarative marker, per language

Each client gains a marker naming the property that holds the row's tenant id. The
registrar finds the marked property and sets `TypeDescriptor.tenant_field` to its name.

| Client | Marker | Follows |
|---|---|---|
| .NET | `[IversonTenant]` | `[IversonKey]`, `[IversonMetadata]` |
| Java | `@IversonTenant` | `@IversonKey`, `@IversonMetadata` |
| Python | `iverson_tenant()` field spec | `iverson_key()`, `iverson_metadata()` |
| Go | `iverson_tenant:"true"` struct tag | `iverson_meta:"true"` |
| TypeScript | `@IversonTenant()` decorator | `@IversonKey()`, `@IversonMetadata()` |

**Go uses an independent tag key, not an `iverson:"tenant"` kind.** Kinds are mutually
exclusive; the tenant property may legitimately also be a search key. This is the lesson
recorded in part 1's `e4a77ff`, where `metadata` had to be moved from a kind to an
independent key for exactly this reason. `tags.go` already carries six independent keys
(`iverson_desc`, `_meta`, `_summary`, `_keywords`, `_extract`, `_contextual`); this is the
seventh and composes the same way.

This replaces TypeScript's hardcoded `''` and fills the field Java, Python and Go never set.

### 2. Validation, and where it lives

Each registrar performs exactly two checks before building the descriptor:

**Multiple markers on one type → throw, naming both properties.** This check *must* be
client-side: `tenant_field` is a single string on the wire, so the server only ever receives
one name and cannot detect that the author marked two. Silently picking the first would
register a schema against the wrong column — a silent wrong answer rather than a visible
failure.

**Zero markers on a registered type → throw, naming the type.** The server already rejects
this, so this check is a convenience. It is included because it keeps one consistent story
per client (both failures surface at the same point, in the same way) and because the local
message can name the marker the author needs to add — which the server cannot, since it does
not know the caller's language.

Messages follow the precedent all five clients already set for the blank extraction hint:
name the offending type or properties, and state the consequence rather than restating the
rule. Each client throws its own idiomatic type, matching what it already does for that case:

| Client | Error type |
|---|---|
| .NET | `ArgumentException` |
| Java | `IllegalArgumentException` |
| Python | `ValueError` |
| Go | returned `error` via `fmt.Errorf` |
| TypeScript | `Error` |

**Deliberately not validated client-side:** that the marked property is a declared scalar,
and that its SQL type is on the server's four-type allow-list. Both are server invariants
with clear messages (`ValidateFieldReference`), and the allow-list exists because
`IntelligenceStoreConsumer.ExtractTypedValue` only produces a clean scalar string for those
four types. Replicating that in five languages creates five things that can drift from the
authority.

### 3. .NET migration

`RegisterAllAsync`'s `tenantFieldByTypeName` parameter and its lookup block are **removed**.
The marker becomes the only way to supply the field.

`authorizationByTypeName` is untouched — a separate concern, out of scope.

Two mechanisms for one required field is the drift class this repo has repeatedly been bitten
by (`generativeModel`, `engagementEnabled`); keeping both would let a stale map silently
override a correct annotation.

Blast radius, all in-repo:

| Site | Change |
|---|---|
| `Iverson.Client.Core/SchemaRegistrar.cs:21,35-36` | drop the parameter and the lookup |
| `Iverson.LoadTest/Program.cs:153,159` | delete the dictionary; annotate the three benchmark entities |
| `Iverson.Client.Core.Tests/SchemaRegistrarTests.cs:608,635` | the two tenant-map tests become marker tests |

The two authorization tests (`:526`, `:561`) are unaffected.

### 4. The .NET Sample

The Sample (`Iverson.Client.Sample/Program.cs:18`) calls `RegisterAllAsync()` with no
arguments and so cannot register today — independent of this blocker, it has always been
broken.

**Five of its six models are registered entities and none has a tenant property.** Each of
`Article`, `Author`, `Tag`, `User` and `UserArticle` gains a `TenantId` string property
carrying the marker. `AuthorArticleCount` is not an `[IversonEntity]` — it is an aggregation
result type — and is left alone.

This is a data-model change to the Sample, not merely an annotation pass. It is included
deliberately, on the user's decision, so the Sample runs for the first time and demonstrates
the convention where a reader will look for it.

## Testing

Per client, in the existing registrar test file:

- the marked property's name reaches `TypeDescriptor.tenant_field`
- zero markers on a registered type throws, and the message names the type
- two markers on one type throws, and the message names both properties

Plus, in .NET only: a test that `RegisterAllAsync` no longer accepts a tenant map — the
marker is the only path.

## Verified assumptions

Thirteen assumptions were enumerated against the design and checked against the codebase
before this spec was written. Twelve held; one failed and changed the design.

| # | Assumption | Result |
|---|---|---|
| A1 | All five clients have a declaration site and registrar at the assumed paths | PASS — `IversonKeyAttribute.cs`, `IversonKey.java`, `annotations.py`, `tags.go`, `annotations.ts` all present |
| A2 | No existing `IversonTenant` / `iverson_tenant` symbol collides | PASS — zero hits across all clients |
| A3 | Every registrar walks properties per-member, so "find the marked one" is expressible | PASS — walk sites found in all five (DotNet 1, Java 3, Python 2, Go 6, TS 4) |
| A4 | Every client already has the blank-hint throw precedent whose error type and message shape §2 mirrors | PASS — `IversonExtractedAttribute.cs:15`, `SchemaRegistrar.java:52,94`, `annotations.py:159`, `tags.go:226`, `annotations.ts:202` |
| A5 | `tenant_field` is proto field 5 and already REQUIRED; no proto change, no codegen re-run | PASS — `object_mapping.proto:99` |
| A6 | A seventh independent Go tag key composes with `iverson` kinds | PASS — six independent keys already coexist in `tags.go` |
| A7 | `core.ts:258` is TypeScript's only `tenantField` site | PASS — single hit |
| A8 | The .NET blast radius is exactly the sites listed in §3 | PASS — `SchemaRegistrar.cs:21,35-36`; `SchemaRegistrarTests.cs:608,635`; `LoadTest/Program.cs:153,159`. No other consumer |
| A9 | LoadTest's three benchmark entities each have a tenant property | PASS — `BenchmarkArticle:16`, `BenchmarkAuthor:13`, `BenchmarkTag:12`, all `public string TenantId` |
| A10 | Annotating the Sample is one line per entity | **FAIL** — no Sample model has any tenant property; five registered entities each need one added. Drove §4 |
| A11 | The properties to be marked satisfy the server's scalar + four-type allow-list | PASS — all are `string` |
| A12 | Nothing else reads a client-built `tenant_field` in a way removing the map breaks | PASS — the only other `TenantField` readers are server-side orchestrator tests that construct descriptors directly |
| A13 | Every client has a registrar test file to extend | PASS — all five present |

## Known issues / accepted

**The `authorization` gap remains open in four clients.** Java, Python, Go and TypeScript
cannot supply `AuthorizationRules` either — TypeScript hardcodes `authorization: undefined`
(`core.ts:257`), and the other three have no mechanism at all. This is the same out-of-band
trio as `tenant_field` and was found during the same investigation. It is deliberately out of
scope: the server does not reject on its absence, so nothing is blocked. It needs its own
spec if per-type authorization from non-.NET clients is ever wanted.

**`owner_field` travels with `authorization`**, inside `AuthorizationRules` rather than as a
top-level `TypeDescriptor` field, so it is covered by the same deferral.
