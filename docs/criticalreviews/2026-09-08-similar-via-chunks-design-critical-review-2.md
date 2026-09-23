# Critical Design Review: 2026-09-08-similar-via-chunks-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-08-similar-via-chunks-design.md`
**Verified Assumptions section:** present

Reviewed against local `main` `ee4227c` (round 1's five fixes applied), with the working-tree
amendment of `docs/plans/2026-09-GATE-similar-centroid.md` and the run directories under
`~/repositories/iverson-benchmark-corpora/` read as-is. The enumeration below was re-derived from
the whole spec before round 1 was consulted. Four claims were tested empirically rather than by
reading, all read-only and outside the repo: docker compose's treatment of a value-less
`environment` entry and of a shell variable carrying the container-side name (Compose 2.40.3,
`docker compose config` on a throwaway file in the scratch directory); Qdrant's response to a
search with `limit: 0` (REST `POST /collections/{name}/points/search` on the box's chunks
collection); and a re-run of the A20 scoped-JWT probe.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| §1 Why — three-arm table | ok — `fs-2048` row re-read against `freshstack-2048-2026-09-07/report-chunks-vs-similar-l100.txt` (nDCG@10 +0.0132 [+0.0053, +0.0212] p_adj 0.0016; R@50 +0.0267); the `sci-2048` citation gap round 1 noted is unchanged and not re-raised. New sentence "the served path differs in the size of the pool it fuses (§5)" now matches `ObjectSearchGrpcService.cs:410` (baseline raw pool = request × 4) |
| §2 Scope + Out of scope | ok — `centroidPossible` is `:228-229`; the new "no new gate rule; §4 checks … same raw pool, within a tolerance" bullet matches §4/§5 as rewritten. "Everything else keeps today's path unchanged" is load-bearing → tested under the rules rows; one input class breaks it → §2.1 |
| §3.1 Configuration | ok — value-less compose entry verified: with the shell variable unset `docker compose config` renders the key as `null` (omitted from the container), with it set the value passes through; `${…:-}` avoided as A22 requires. Set-but-empty in the shell passes `""` and the validator rejects it at startup with a message naming the section — that is the validator working, not a defect; dropped |
| §3.2 Routing decision, conditions 1–3 | ok — see rules rows |
| §3.2 condition 4 | → §2.1 — "object count is positive" is the only positivity check; a zero *chunk* count yields `chunkLimit = 0` |
| §3.2 ownership on the routed branch | ok — unchanged from round 1; `IntelligenceStoreConsumer.cs:314-315` (owner under `OwnerField.ToCamelCase()`), `:317-` (metadata columns); `ApplyOwnership` at `IntelligenceFilterBuilder.cs:89-93` adds the same `MatchKeyword` as `SearchChunks` `:368-372` |
| §3.3 Shared chunk pipeline | ok — boundaries unchanged since round 1 (`:402-478`, retrieve at `:453-461`); the `AuthzResult` naming slip and the ~5-line drift in cited line numbers were noted in round 1 and are not re-raised. New: the helper takes `chunkLimit` as its own parameter and `SearchChunks` passes `topK * OverFetchFactor` (`:410`) — the extraction changes no value on that path |
| §3.4 Density-scaled budget | mechanics → §2.1 (zero chunk count); otherwise ok — `GetCollectionInfoAsync(string, CancellationToken)` (`Qdrant.Client.xml:14382`), `CollectionInfo.PointsCount` (`:4333`), the call at `IntelligenceCollectionManager.cs:65`; both names from `ResolveCollectionName` (`IntelligenceTenantScope.cs:11`); scoped read key re-probed (A20) |
| §3.5.1 Collapse | ok — see identity row |
| §3.5.2 Diversify | ok — `Mmr(i) = lambda × Score` when no similarity term, strict `>` keeps the earlier candidate (`ResultDiversifier.cs:57-75`), so λ 1.00 over a fused-descending input is `Take(topK)`; the head path passes the centroid as the diversity vector at `:284-288` |
| §3.5.3 Hydrate | ok — `RetrieveAsync(string, IReadOnlyList<PointId>, bool, bool, ReadConsistency, ShardKeySelector, CancellationToken)` (`Qdrant.Client.xml:14806`); `ToCanonicalString` is `internal static` (`IntelligenceVectorService.cs:202`); 512-batch loop at `:154-171` |
| §3.5.4 Stream | ok — the block at `:291-327` reads `schema`, `decision.AllowedFields`, `r.Payload`, `ranked.FusedScore`, `request.TraceId`, `context.CancellationToken` only |
| §3.6 Errors and degradation | one row incomplete → §2.1 (the zero-chunk case is neither "counts unavailable" nor "empty object collection", and no row catches the Qdrant error it produces). The `:217-218` citation for a Qdrant-failure code is the embedding catch — round 1 noted this; not re-raised |
| §4 Reproduction check, step 1 | ok — `freshstack-2048-qdrant-snapshots/RESTORE.md` names 6,000 / 18,622 and both snapshot files are present |
| §4 step 2 | → §2.2 — `VectorRanking__LambdaChunks=1.00` in the launching shell does not reach the container |
| §4 step 3 | ok — `keymap.json.stats.json` reads `documents: 6000, chunks: 18622`; `ChunkBudgetGuard.Evaluate(6000, 18622, 50, 4)` → topK 200, reachable 64.4 ≥ 50, `MinimumMultiplier` 4; the harness requests `TopK(50 × 4)` on `SearchChunks` (`BenchmarkQueryScenario.cs:393`) and `TopK(50)` on `SearchSimilar` (`:346`), both on `Body`, both without a filter, both under one `Metadata` of acting-user headers, so the two raw pools are the same 800-limit search against the same collection under the same ownership filter |
| §4 step 4 | ok — `report.py` prints the structural block (`scripts/report.py:240-305`: rows, distinct queries, `covered by this run N / M`, `duplicate doc ids`) and the paired comparison against `--baseline`; the second, continuity-only invocation against `fs-2048-l070.chunks.trec` (composite `9714c660…`, multiplier 11 per its `meta.json`) will print `BUILD MISMATCH` as the spec says |
| §4 step 5 | ok — probe: shell without the variable → key rendered `null` → container starts with an empty list; the §2.1 crash from round 1 is gone |
| §5 The rule | ok — the qrels file holds 672 distinct query ids (`awk '{print $1}' qrels.trec \| sort -u`), matching "672 / 672"; the collapse-boundary argument holds: both sides fuse the same 800, the harness keeps the first 200 fused (λ 1.00 = prefix), the routed path collapses all 800; a parent's max fused score is its first appearance in fused order, so the top-50 parents agree whenever the first 200 fused chunks contain ≥ 50 distinct parents. The argument *requires* λ_chunks = 1.00 on the baseline run → its procurement in §4 step 2 is §2.2 |
| §6 Consequences | ok — `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` has an item 3 (`:94`); FAIL diagnosis now starts with the budget/collapse boundary as round 1 asked |
| §7 Testing | ok — `_vector = Substitute.For<IVectorQueryService>()` at `ObjectSearchGrpcServiceTests.cs:41`; constructor `:74-79` with `Options.Create(new VectorRankingOptions { … })`; `VectorRankingOptionsTests.cs:11` `BuildConfig`, `:71-99` throw pattern. The "not routed" list has no case for a zero chunk count → covered by §2.1's fix. "with a scalar column clause → not routed" is consistent: `ValidateFilterProperty` (`:839-851`) accepts any non-tenant scalar or FK column, so the preamble passes it and condition 3 step 2 rejects it |
| Verified assumptions | → §1 |
| Known issues | ok — each names a value/latency consequence; "enabling the option never breaks an existing caller" is a stated property of the design and is what §2.1 breaks |

### Rules and operands

| Row | Disposition |
|---|---|
| Condition 1: `schema.TypeName ∈ SimilarViaChunksTypes`, `OrdinalIgnoreCase` | ok — `SchemaRegistry.cs:11`; over-inclusion none, under-inclusion none (case-insensitive both sides). The list's entries are now non-blank by construction (§3.1 validation) |
| Condition 2: chunk descriptor for `vectorDesc.PropertyName` | ok — same predicate as `centroidPossible` over `schema.ChunkFields` (`SchemaDescriptor.cs:38`) |
| Condition 3 step 1: `Filter.Count <= 1 \|\| FilterLogic == And` | ok — proto `SearchLogic { AND = 0; OR = 1; }` (`object_search.proto:74-77`), so an unset logic is AND; a single MUST_NOT clause passes step 1 and is rejected by step 2's clause-type test (`:880`). Both directions: OR over ≥ 2 clauses falls back (the head path honours OR via `Should`, `IntelligenceFilterBuilder.cs:43-44`); AND over any count proceeds to step 2 |
| Condition 3 step 2: reshaped `BuildChunksFilter(SchemaDescriptor, IReadOnlyList<SearchClause>, allowedFields)` | ok — the routed caller passes `request.Filter`, a `RepeatedField<SearchClause>`; `Google.Protobuf.dll` 3.35.1 references `IReadOnlyList\`1` (it is `RepeatedField<T>`'s interface), and `IntelligenceFilterBuilder.Build` already takes `IReadOnlyList<SearchClause>` (`:23`). Operands: key column (`:887-888`, `OrdinalIgnoreCase`), metadata columns (`:889-903`, `MetadataColumns` is `OrdinalIgnoreCase`, `SchemaDescriptor.cs:78-82`), everything else throws → wrapper returns `false`. The routed path passes the caller's raw property spelling, which is what `BuildChunksFilter` compares against (schema spelling, case-insensitive) — no camelCase mismatch |
| Condition 3, masked metadata column | ok — the preamble's own `AllowedFields` check (`:164-166`) throws `InvalidArgument` before routing on both paths |
| Condition 4: counts readable, object count > 0 | → §2.1 — the chunk count is read but never tested for zero; `ceil(0 / n) = 0` → `chunkLimit = 0` |
| Budget rule `ceil(chunks / objects) × topK × 4` | values ok (3.10 → 4 → 800 for §4). Note for the implementer, not a design finding: the spec writes the quotient mathematically; two `ulong` counts divided as integers give `ceil(3) = 3`, not 4. That is `critical-implementation-review`'s row; dropped here |
| Identity rule: collapse key = `KeyToUlong(payload["parent_id"])` | ok — `parent_id` = `ev.Key` (`IntelligenceStoreConsumer.cs:310`), object point id = `KeyToUlong(ev.Key)` (`:548`), the same identity the object collection uses, so over-merge would already be one point; every chunk of a parent carries the same key, so no under-merge. A21 covers it |
| Exclusion rule: chunk without `parent_id` dropped | ok — ingest always writes it (`:310`); fails closed |
| Exclusion rule: parent missing at hydration | ok — sole producer is `HandleDeleteAsync` (`:553` before `:562`, A17); the stream shortens and the spec says so |
| Eligibility predicate: "listed type" — producers | ok — one producer, `section.Bind` (`ServiceCollectionExtensions.cs:65`); the compose line now emits nothing when unset (probe) |
| Eligibility predicate: "empty chunks collection" — producers of a zero chunk count with a positive object count | → §2.1 — two producers found: (a) `IntelligenceStoreConsumer.cs:175` creates the tenant's chunks collection whenever `ChunkFields.Count > 0`, *before* the blank-text guard at `:230`, so a tenant whose documents of that type all have blank chunk text has object points and an empty chunks collection; (b) `:228` deletes a field's chunks on update before the same guard, so updating every document's chunked field to blank empties the collection while the object points remain |
| Key-column clause on the head path | dropped — the object payload stores the key under the literal `"key"` (`IntelligenceStoreConsumer.cs:432`) while the head path filters on the camelCased column name, so a key clause matches nothing on the head path today; pre-existing head-path behaviour, not this design's, and the routed path (`MatchParentId`) is the correct one |
| PASS rule ±0.005 + structural block | ok — see §5 row |

### Data-flow arrows

| Row | Disposition |
|---|---|
| Compose value-less entry → container env → `section.Bind` → `List<string>` (serialization boundary) | ok — unset → absent → `[]`; set → one entry (probe + A22) |
| Shell `VectorRanking__LambdaChunks=1.00` → compose → container `VectorRanking__LambdaChunks` (§4 step 2) | → §2.2 — compose assigns that key from `${VECTOR_RANKING_LAMBDA_CHUNKS:-0.70}` (`docker-compose.yml:446`); a shell variable of the container-side name is not consulted (probe: rendered `"0.70"`) |
| Config → `VectorRankingOptions` → `_ranking` | ok — `Options.Create(opts)` (`:92`), `rankingOptions.Value` (`:47`) |
| Preamble → `queryVector`, `vectorDesc`, `decision`, `schema`, `request.Filter`, `request.FilterLogic` → routing decision | ok — all in scope at `:221` |
| Two `GetPointCountAsync` reads → `chunksPerDoc` → `chunkLimit` → `SearchNamedAsync(chunksCollection, …, chunkLimit, filter)` | → §2.1 — the consuming operation requires `limit ≥ 1`; Qdrant rejects 0 (probe: `422 … search_request.limit: value 0 invalid, must be 1 or larger`), and the helper catches only `NotFound` (`:419-424`) |
| `SearchChunksFusedAsync` — caller 1, `SearchChunks` | ok — `topK × 4` as today; returns `results`, fused list, centroids — what `:453-506` reads |
| `SearchChunksFusedAsync` — caller 2, routed `SearchSimilar` | ok — `chunkDesc` from condition 2, `filter` from condition 3 + `ApplyOwnership`, `queryVector` from the preamble, `chunkLimit` from §3.4; `parent_id` per fused id is available from `results` keyed by `RerankedResult.Id` |
| Fused list → collapse → `DiversifyCandidate(parentId, maxScore, centroid)` | ok — centroid dictionary keyed by the same `KeyToUlong` parent id |
| Diversified parents → `RetrievePayloadAsync` (object collection, scoped key) → payload dictionaries | ok — same point, same payload keys (`"key"`, camelCased scalars, vector-field text), same `ToCanonicalString` shape as `SearchNamedAsync` |
| `WriteSimilarResponseAsync` — caller 1 (head) / caller 2 (routed) | ok — `r.Payload` + `ranked.FusedScore` / hydrated payload + collapsed score; `DocId` is a scalar column on the object point, which the harness reads at `BenchmarkQueryScenario.cs:363-371` |
| Harness `SearchSimilar` → `.similar.trec` → `report.py` (write-then-read) | ok — `CollapseByDocId(results, 50)` then TREC; ≤ 50 distinct parents per query |
| Harness `SearchChunks` at multiplier 4 → server raw 800 → fused → λ-selected 200 → harness collapse → `.chunks.trec` (the §5 baseline) | ok as designed — but only when λ_chunks is 1.00 on the box, which is §2.2 |
| Delete → object point then chunks | ok — `:553` then `:562` |

## 1. Verified-assumptions cross-check

| # | Status |
|---|---|
| A1 | still holds — `section.Bind(opts)` at `ServiceCollectionExtensions.cs:65`; validation `:67-90` |
| A2 | still holds — `docker-compose.yml:445-446` under `iverson-api`; no `env_file` and no `Iverson.Server/.env`; `AddVectorRanking` called only from `Iverson.Api/Program.cs:188` |
| A3 | still holds — `SchemaRegistry.cs:11` |
| A4 | still holds — `:143-148`, `:228-229` |
| A5 | still holds — proto `:34`, `:74`, `:79`; `BuildChunksFilter` `:870-915` `private static`, throws `RpcException` |
| A6 | still holds — retrieve at `:453-461` reads `results`, `chunksCollection`, `vectorName`, `topK`; after the centroid retrieve `:438-443` |
| A7 | still holds — `IVectorRoles.cs:5-21`; XML members `:4333`, `:14382`, `:14806` |
| A8 | still holds — `IResultReranker.cs:9,13` |
| A9 | still holds — `IResultDiversifier.cs:8,14`; `ResultDiversifier.cs:57-75` |
| A10 | still holds — `IVectorRoles.cs:49-52`; `KeyToUlong` `internal static` at `IntelligenceStoreConsumer.cs:701` |
| A11 | still holds — `:291-327` |
| A12 | still holds — `IntelligenceTenantScope.cs:11-16`; `:223`, `:403` |
| A13 | still holds — `ObjectSearchGrpcServiceTests.cs:41`, `:74-79`; `VectorRankingOptionsTests.cs:11,71-99` |
| A14 | still holds — `BenchmarkDocument.cs:16-18`; `RESTORE.md` 6,000 / 18,622; `DocumentBudget = 50` (`:42`) |
| A15 | still holds — `:58` |
| A16 | still holds — `IntelligenceVectorService.cs:202-209` |
| A17 | still holds — `:553` then `:562` |
| A18 | still holds — `:133-158` and `:336-356` |
| A19 | still holds — `IntelligenceStoreConsumer.cs:314-315`, `:317-`; `ObjectSearchGrpcService.cs:368-372` |
| A20 | still holds — re-probed this round against the box's Qdrant with a locally minted HS256 token scoped `r` to `benchmark_documents_chunks_tenant_bypass`: `GET /collections/benchmark_documents_chunks_tenant_bypass` → 200 with `points_count`; `GET /collections/benchmark_documents_tenant_bypass` under the same token → 403 |
| A21 | still holds — `:310` (`parent_id = ev.Key`), `:548` (`pointId = KeyToUlong(ev.Key)`); `:431-436` already depends on it |
| A22 | ground truth as stated (round 1 empirical run); the design no longer produces an empty-string entry on the shipped path, so it is now defensive rather than load-bearing |

**Span check** — design dependencies no listed assumption covers:

- *A positive object count implies a usable chunk limit* (§3.2 condition 4, §3.4) — no assumption covers the chunk count's range; verified in-round: **false** for an empty chunks collection → §2.1.
- *Setting `VectorRanking__LambdaChunks` in the launching shell configures the container* (§4 step 2) — A2 verifies which service carries the variables, not how a shell value reaches them; verified in-round by `docker compose config`: **false** → §2.2.
- *A value-less compose entry is omitted when unset* (§3.1) — no assumption; verified in-round: holds (rendered `null` when unset, the value when set).
- *`RepeatedField<SearchClause>` satisfies the reshaped `IReadOnlyList<SearchClause>` parameter* (§3.2 condition 3) — no assumption; verified in-round: holds (`Google.Protobuf.dll` 3.35.1 declares the `IReadOnlyList\`1` reference; `IntelligenceFilterBuilder.Build` already takes the same interface).
- *The qrels file holds exactly 672 queries* (§5) — holds (672 distinct query ids).

## 2. Literal-wrongness findings

### 2.1 — §3.2 condition 4 / §3.4 / §3.6: an empty chunks collection routes with `chunkLimit = 0`, and Qdrant rejects that search — the routed request fails where the head path returns results

**Description.** Condition 4 falls back only when a count read throws or the *object* count is zero. When the chunks collection exists and is empty, both reads succeed, `chunksPerDoc = ceil(0 / objectPoints) = 0`, and `chunkLimit = topK × 0 × 4 = 0`. The helper then calls `SearchNamedAsync(chunksCollection, …, 0, filter)`. Qdrant validates `limit ≥ 1` on search; the helper catches only `NotFound` (`:419-424`), so the resulting `RpcException` propagates and `SearchSimilar` fails for a request that the head path answers today from the object collection. That breaks §2 ("Everything else keeps today's path unchanged") and the Known-issues property that "enabling the option never breaks an existing caller". Neither §3.6 row covers it: the counts *were* available and the object collection is *not* empty.

The input class is producible by ordinary ingest, not only by a race: `IntelligenceStoreConsumer.cs:175` creates the tenant's chunks collection for any type with a chunk field, before the blank-text guard at `:230` decides whether that document gets chunk points. A tenant whose first documents of a listed type all carry blank text for the chunked property (or whose documents were later updated to blank — `:228` deletes the field's chunks before the same guard) has object points and a zero-count chunks collection.

**Evidence.**
- Spec §3.4: `chunksPerDoc = ceil(chunkPoints / objectPoints)`, `chunkLimit = topK * chunksPerDoc * OverFetchFactor`; §3.2 condition 4: "the object count is positive" (only).
- Qdrant, read-only probe this round on `benchmark_documents_chunks_tenant_bypass`: `POST /collections/{name}/points/search` with `limit: 0` → `HTTP 422 {"status":{"error":"Validation error in JSON body: [search_request.limit: value 0 invalid, must be 1 or larger]"}}`; the same body with `limit: 1` → 200. (Whether the gRPC client surfaces this as `InvalidArgument` or some other non-`NotFound` status, the routed request returns none of the objects the head path returns.)
- `ObjectSearchGrpcService.cs:415-425`: only `StatusCode.NotFound` is caught around `SearchNamedAsync`.
- `IntelligenceStoreConsumer.cs:171-175` (collection ensured when `ChunkFields.Count > 0`), `:227-230` (chunks deleted, then `if (string.IsNullOrWhiteSpace(text)) continue;`).

**Proposed fix.** Make condition 4 "both point counts read successfully and **both are positive**", add a §3.6 row `Chunk count is 0 → fall back; reason 'empty chunks collection'`, and add "chunk count 0" to §7's not-routed list. (Clamping `chunksPerDoc` to a minimum of 1 would also avoid the zero limit, but would route a request to an empty collection and stream nothing where the head path streams objects — the fallback is the behaviour the spec's own scope statement requires.)

### 2.2 — §4 step 2: `VectorRanking__LambdaChunks=1.00` in the launching shell does not reach the container, so the baseline run is selected at λ 0.70 and the §5 premise does not hold

**Description.** Step 2 starts `iverson-api` "with `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument` and `VectorRanking__LambdaChunks=1.00`". The first name is passed through as §3.1 designs. The second is the *container-side* name: `docker-compose.yml:446` assigns `VectorRanking__LambdaChunks=${VECTOR_RANKING_LAMBDA_CHUNKS:-0.70}`, and compose does not consult a shell variable that happens to share the container-side name. The container starts at λ_chunks 0.70. §5's argument — "the harness collapses the first 200 fused chunks (λ 1.00 selection) while the routed path collapses all 800 … they agree wherever the first 200 fused chunks hold at least 50 distinct parents" — requires the baseline's 200 to be the fused *prefix*; at 0.70 they are an MMR selection over the 800, so the two sides no longer differ "only in what they collapse", and a non-zero delta no longer "locates queries where they do not" hold ≥ 50 parents. Step 2's `docker inspect` confirmation would show `VectorRanking__LambdaChunks=0.70` — it detects the mismatch but the spec gives no instruction that would produce 1.00.

**Evidence.**
- `docker-compose.yml:445-446` (both λ entries assigned from `VECTOR_RANKING_LAMBDA_*` with defaults).
- Probe this round (Compose 2.40.3, throwaway file with the same two entry shapes): `VectorRanking__LambdaChunks=1.00 docker compose config` renders `VectorRanking__LambdaChunks: "0.70"`; `VECTOR_RANKING_LAMBDA_CHUNKS=1.00 docker compose config` renders `"1.00"`.
- Round 1's §2.3 proposed fix named `VECTOR_RANKING_LAMBDA_CHUNKS=1.00`; the applied spec text carries the container-side name.

**Proposed fix.** In §4 step 2 replace `VectorRanking__LambdaChunks=1.00` with `VECTOR_RANKING_LAMBDA_CHUNKS=1.00` (the shell name the compose line reads), and state that `docker inspect` must show `VectorRanking__LambdaChunks=1.00` and `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument`. Step 5's restore then also needs "without either variable" so the box returns to 0.70.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round 1 §2.1 (compose `${…:-}` bound a blank entry and the new validator rejected it, so the API could not start with the option disabled) — resolved: §3.1 now uses a value-less entry, verified this round to be omitted when unset; A22 records the binder behaviour.
- Round 1 §2.2 (`BuildChunksFilter` never reads `FilterLogic`, so an OR request would have been routed with AND semantics) — resolved: §3.2 condition 3 now tests the logic explicitly in the routing decision (`Filter.Count <= 1 || FilterLogic == And`) before `TryBuildChunksFilter`, and says why it cannot live in the shared function.
- Round 1 §2.3 (the §5 baseline was fused over a 2,200-chunk raw pool while the design fuses 800; the superset/sign claim was false; §6's FAIL diagnosis pointed at the wrong component) — resolved: §4 now builds a same-binary baseline at `--chunk-budget-multiplier 4` with λ_chunks 1.00 (subject to §2.2 above), §5 states the actual 200-versus-800 collapse relation, and §6 starts the FAIL diagnosis with the budget and the collapse boundary.
- Round 1 span-check gaps "a collection-scoped read key can read collection info" and "chunk `parent_id` agrees with the object point id under `KeyToUlong`" — resolved as A20 and A21; A20 re-probed clean this round.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has two items (§2.1: an empty chunks collection routes to a `limit = 0` search that Qdrant rejects, breaking the "unchanged for everything else" scope for a producible input class; §2.2: §4 step 2 names the container-side variable, so the reproduction baseline would run at λ 0.70 and the §5 premise would not hold); §3 is empty. Address §2, then proceed to implementation planning.
