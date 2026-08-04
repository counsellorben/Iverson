# Critical Design Review: 2026-07-31-retrieval-quality-benchmark-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-31-retrieval-quality-benchmark-design.md`
**Verified Assumptions section:** present

> **Drift note.** The spec states it was verified at `main@d3c8b3c`; HEAD is `fd4ebab`, 62 commits
> later. Two files the spec cites have changed in that window —
> `ObjectMappingGrpcService.cs` and `ObjectSearchGrpcService.cs` (both touched by the
> array-column-mapping and GetSchema branches). Every cited fact was re-read at HEAD; all still
> hold, but several line references have moved (see §1).

## 0. Coverage enumeration

**Sections**

| Row | Disposition |
|---|---|
| §1 The two ablations | ok — both constants re-read at HEAD; `WCentroid`/`Lambda` are `private const double` at `ResultReranker.cs:12` / `ResultDiversifier.cs:12`, each read at one expression site |
| §2 Corpora | ok — corpus-scale claims (A1, A2) are external-data claims the spec already marks unfetched; no code dependency to check |
| §3 Corpus → Iverson mapping | → §2.1 (entity is registered without authorization rules) |
| §4 Query execution and run files | ok — see arrow rows below |
| §5 Scoring is external | ok — no Iverson code path; `ir_measures`/FreshStack are external tools, correctly out of the harness |
| §6 Harness location | ok — `Iverson.LoadTest/Program.cs:117-122` wires `AddIversonClient` with `entityAssemblies: [typeof(BenchmarkArticle).Assembly]`; `Scenarios/`, `Reporting/`, `Auth/` all present |
| §7 Running the sweep | ok — `ResultRerankerTests.cs:28` still hand-computes `0.77`; ablation builds are red as claimed |
| Testing | ok — both named test targets (corpus parsers, max-passage aggregation) are pure functions over harness-owned data |
| Known issues / Not in this spec | ok — A10's ingest-feasibility risk is carried explicitly; no code claim to verify |

**Rules and operands**

| Row | Disposition |
|---|---|
| Ablation "off" — `WCentroid = 0.00` (both directions) | ok — `ResultReranker.cs:35-42`: `weightedSum += WCentroid * sim` and `weightTotal += WCentroid` are both inside `if (hasCentroid)`, so zero contributes to neither numerator nor denominator → `fused == base`. Over-inclusion direction: no other branch reads the constant's *value* (A16 re-grepped clean at HEAD) |
| Ablation "off" — `Lambda = 1.00` (both directions) | ok — `ResultDiversifier.cs:76-77`: `hasSim ? Λ*Score − (1−Λ)*maxSim : Λ*Score`. At Λ=1 **both** branches reduce to `Score`, including the absent-similarity branch — the reduction does not depend on `hasSim` |
| Eligibility predicate — which identities may read the benchmark type | → §2.1. Producers of the "denied" verdict enumerated at `RowFieldAuthorizationEvaluator.cs:11-22`: null rules, null acting user, absent tenant column, absent `tenant_id` claim. The spec's design satisfies only the tenant-column one |
| Identity rule — `ParentKey → DocId` map | ok — over-merge checked: `parent_id` is set from `ev.Key` (`IntelligenceStoreConsumer.cs:253`), the server-assigned UUIDv7, which is unique per document. No two corpus documents can collide on it |
| Max-passage aggregation (both directions) | ok — collapsing N chunks to one document by max score cannot over-include (one entry per parent) or under-include (every parent with ≥1 returned chunk appears). Spec correctly unit-tests it |
| Ablation matrix totality (A20) | ok — all 8 members are pure constant edits; no member needs a code-shape change |

**Data-flow arrows** (each ends at an operation, not a stage)

| Row | Disposition |
|---|---|
| ingest → harness's `ParentKey → DocId` map — **crosses a network boundary** | ok — the operation needs the server-assigned key, which the client never computes. `EntityCoordinator.PersistAsync` returns `response.Key` (`EntityCoordinator.cs:111`), and `PersistResponse.key` is documented "the assigned or existing entity key" (`object_persistence.proto:21`). The map is buildable |
| `SearchChunks` → run-file row | ok — `ChunkSearchResponse.ParentKey` is populated from payload `parent_id` (`ObjectSearchGrpcService.cs:442-447`), the same value stored at ingest. Joins to the map above |
| `SearchSimilar` → run-file row | ok — `EntityCoordinator.cs:204` yields `SearchResult<T>(entity, score)`; `DocId` is an ordinary property on the deserialized entity, so no map needed |
| query → reranker → diversifier, **for both RPCs** | ok — `rerank`+`Diversify` appear at `:260/:267` inside `SearchSimilar` (declared `:118`) and at `:430/:437` inside `SearchChunks` (declared `:290`). Both ablation axes are live on both RPCs; neither axis is silently inert on one of them |
| `topK ≥ 50` → Qdrant fetch limit | ok — `fetchLimit = topK * OverFetchFactor` with the comment "the over-fetch stays exactly 4x with no ceiling" (`:357-358`); lower-bound-only clamp at `:198` and `:352` confirms A13 |
| dual `[IversonEmbedding]` + `[IversonChunk]` on one property → `centroidPossible` | ok — span-check candidate, closed by reading: `SchemaRegistrar.AddAnnotations` (`:186-200`) uses two **independent** `if`s, so one property can carry both. A4's server-side match at `ObjectSearchGrpcService.cs:204` is reachable only via that dual declaration |

## 1. Verified-assumptions cross-check

All twenty assumptions **still hold**. Re-read at HEAD:

- **A4** `ObjectSearchGrpcService.cs:204` — `centroidPossible` unchanged.
- **A5** still true; **line reference moved** — `Guid.CreateVersion7()` is now at `ObjectMappingGrpcService.cs:303`, not `:127-132` (that file gained the `GetSchema` RPC).
- **A7, A11, A12, A17, A18** — reconfirmed at their cited locations.
- **A13** still true; **second line reference moved** — the clamp is at `:198` and `:352`, not `:347`.
- **A14, A15, A16, A19, A20** — reconfirmed; A16's grep still returns hits only in the two named files.
- **A9** reconfirmed and worth noting: `AddIversonClient` gained an acting-user-token parameter on 2026-08-04, and `Iverson.LoadTest/Program.cs:117-122` already passes it. The assumption survives the change.
- **A1, A2, A3, A10** — external-data and unverifiable-by-reading claims, correctly marked as such in the spec itself. A2 explicitly says the counts were not fetched; that remains the spec's own caveat, not a review finding.

**Span check — one uncovered dependency, verified in-round and promoted to §2.1:**

The design depends on the benchmark entity being *readable by the query path*. A6 verifies that
registering a type needs no server change, and A18 verifies that a dictionary entry is additive —
but neither states that the new type **requires** an entry, nor what the read path does without
one. That gap is exactly where the defect lives. See §2.1.

## 2. Literal-wrongness findings

### 2.1 The benchmark entity returns zero results unless it is given authorization rules *and* queried by an identity those rules admit — and the failure is silent

**Description.** §3 enumerates what `BenchmarkDocument` must declare — `[IversonKey] Guid Id`,
`DocId`, `Title`, dual-annotated `Body`, `[IversonTenant]` — and never mentions authorization
rules. Registered as specified, every query against it returns an empty stream. The harness would
write syntactically valid, entirely empty TREC run files, and every metric on both ablation axes
would score 0.000 for all eight configurations. Nothing raises an error, so the result looks like
"retrieval found nothing" rather than "the entity was never readable."

**Evidence.** Two independent mechanisms, either of which alone is sufficient:

1. **No dictionary entry → denied.** `SchemaRegistrar.RegisterAllAsync` sets `Authorization` only
   for types found in the dictionary (`SchemaRegistrar.cs:26-30`); a type absent from it registers
   with `Authorization` null. Server-side, `RowFieldAuthorizationEvaluator.Evaluate` returns
   `Denied: true` when `rules is null` (`RowFieldAuthorizationEvaluator.cs:11-12`, asserted by
   `Evaluate_NoAuthorizationRules_ReturnsDenied`). Both benchmark RPCs then bail:
   `if (decision.Denied) return; // empty stream — Qdrant never queried` —
   `ObjectSearchGrpcService.cs:126-127` (`SearchSimilar`) and `:298-299` (`SearchChunks`).
   The existing dictionary at `Iverson.LoadTest/Program.cs:147-152` lists only
   `BenchmarkArticle`, `BenchmarkAuthor`, `BenchmarkTag`.

2. **The existing rule shape → filtered to nothing.** If the entity is added to that dictionary via
   the existing helper, `BuildAuthorizationRules` sets `OwnerField = "OwnerId"` and grants
   `CanReadAll` only to role `iverson-loadtest-bypass` (`Program.cs:277-283`). For any identity
   without that role, `ownershipRequired` is true and both RPCs apply
   `IntelligenceFilterBuilder.ApplyOwnership(..., schema.Authorization?.OwnerField?.ToCamelCase(),
   decision.OwnerValue)`. §3's entity has no `OwnerId` property, so the Qdrant filter matches no
   point and the result set is empty again.

**Proposed fix.** State the authorization posture in §3 as part of the entity's definition, picking
one of:

- **(a) Bypass identity.** Add `BenchmarkDocument` to `authorizationByTypeName` with a rule granting
  `CanReadAll` to `iverson-loadtest-bypass`, and have the benchmark scenario query as the existing
  `iverson-loadtest-bypass-user` (already provisioned —
  `charts/authentik/.../service-clients.yaml:237-251`, default credentials at `Program.cs:42-43`).
  No `OwnerId` needed; `ownershipRequired` is false on the bypass path.
- **(b) Owned rows.** Give the entity an `OwnerId` property, populate it at ingest with the acting
  user's `sub`, and keep the ownership filter live.

(a) is the smaller change and keeps the benchmark measuring retrieval rather than authorization.
Either way the acting identity must also carry a `tenant_id` claim, since the evaluator denies
without one (`RowFieldAuthorizationEvaluator.cs:20-22`) — the load test's existing tenant
provisioning already supplies this.

Worth adding to §7's checklist regardless: a smoke assertion that the first configuration's run
file is non-empty before spending eight ingest-and-query cycles on it.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**

The design's core reasoning is sound and unusually well evidenced — both ablation "off" settings are
genuinely exact (the `Lambda = 1.00` reduction holds on *both* branches of the MMR expression, not
just the one the spec cites), one ingest genuinely does serve the whole sweep, and both ablation
axes are live on both RPCs. §2.1 is the one thing that would make the harness produce empty run
files, and it is a spec-text fix plus a scenario detail, not a design change.
