# Critical Design Review: 2026-09-08-similar-via-chunks-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-08-similar-via-chunks-design.md`
**Verified Assumptions section:** present

Reviewed against local `main` `1746140` (the spec's own commit, on top of `6600810`), with the
working-tree amendment of `docs/plans/2026-09-GATE-similar-centroid.md` and the run directories
under `~/repositories/iverson-benchmark-corpora/` read as-is. Two claims were tested empirically
rather than by reading: the configuration binder's treatment of an empty list entry (a throwaway
`net10.0` console project against `Microsoft.Extensions.Configuration.Binder` 10.0.11, both the
environment-variable and in-memory providers), and whether a collection-scoped read JWT can read
Qdrant collection info (a read-only `GET /collections/{name}` against the box's Qdrant with a
locally minted HS256 token). The spec's `sci-2048` row was reproduced by running `report.py`.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| §1 Why — the three-arm table | ok — `fs-2048` and `fs-512` rows match `report-chunks-vs-similar-l100.txt` in each FreshStack directory verbatim (nDCG@10 +0.0132 [+0.0053, +0.0212] p_adj 0.0016 / R@50 +0.0267 [+0.0153, +0.0381] p_adj 0.0002 / AP +0.0118 p_adj 0.0004; +0.0099 [−0.0006, +0.0204] n.s. / +0.0140 [+0.0009, +0.0272] p_adj 0.0342 / +0.0087 p_adj 0.0442). The `sci-2048` row has **no saved report** — `scifact-2048-2026-09-06/` holds no `report-chunks-vs-similar-l100.txt`, contrary to the parenthetical — but it reproduces exactly by running `report.py --baseline runs/sc-head.similar.trec --run runs/sci-2048.chunks.trec` (+0.0008 p 0.5455 / −0.0117 p 0.1294 / −0.0019 p_adj 0.0010). That pairing is cross-build (`9714c660…` vs `e849cb6a…`, `BUILD MISMATCH` printed); the centroid gate's validity check showed the two builds order the head path identically on `fs-2048`, so the row's "nothing on abstracts" reading stands. Citation gap only; no behaviour rests on it. |
| §1 "the collapsed `.chunks` run *is* this design" | → §2.3 — the harness's collapsed run was fused over a 2,200-chunk raw pool; the design fuses over 800 |
| §2 Scope + Out of scope | ok — `centroidPossible` is `ObjectSearchGrpcService.cs:228-229` as cited; "SearchChunks preserved bit for bit" is load-bearing for §2.2's fix placement (see there); "served ranking is one the harness has already measured" → §2.3 |
| §3.1 Configuration | → §2.1 — "an empty value binds no entry" is false under the real binder |
| §3.2 Routing decision, conditions 1–4 | condition 3 → §2.2; conditions 1, 2, 4 ok (see rules below) |
| §3.2 ownership on the routed branch | ok — chunk points carry the owner under `OwnerField.ToCamelCase()` (`IntelligenceStoreConsumer.cs:307-308`) and every metadata column (`:310-314`); `ApplyOwnership` (`IntelligenceFilterBuilder.cs:89-95`) adds a `MatchKeyword` on that same camelCase key, as `SearchChunks` does at `:368-372` |
| §3.3 Shared chunk pipeline | ok — `:402-478` read; every step the helper is said to contain is present, in the stated order, though the spec's line numbers run about five lines early from the parent-id step on: `:402` vector name, `:403` collection, `:413` key, `:417` search, `:419-424` NotFound, `:431-437` parent ids, `:438-444` centroids, `:464-465` decay, `:467-475` candidates, `:483` Rerank. The chunk-vector retrieve at `:453-461` (spec: `:449-455`) reads `results`, `chunksCollection`, `vectorName`, `topK` only — the caller can recompute the two names (`ResolveCollectionName` is pure, `IntelligenceTenantScope.cs:11`), and moving the await after `Rerank` changes no data dependency. Note the helper signature names `AuthzResult`, which is the *Search* path's private join record (`:805-808`); the value passed is an `AuthorizationDecision` (`IRowFieldAuthorizationEvaluator.cs:10,16`). Naming slip in a sketch; no mechanics rest on it |
| §3.4 Density-scaled budget | ok — arithmetic matches `ChunkBudgetGuard.MinimumMultiplier = ceil(chunks/documents)` (`ChunkBudgetGuard.cs:40`) times `OverFetchFactor = 4` (`:749`); 18,622 / 6,000 = 3.10 → 4 → 50 × 4 × 4 = 800 as §4.3 states. `GetCollectionInfoAsync` is the call `IntelligenceCollectionManager.cs:65` already makes; `CollectionInfo.PointsCount` exists (Qdrant.Client 1.18.1 XML, documented "Approximate number of points"); a collection-scoped read JWT returns it (live probe: 200 with `points_count` on both collections, 403 when the token is scoped to the other collection) |
| §3.5.1 Collapse | ok — see identity-rule row |
| §3.5.2 Diversify | ok — `Diversify` takes `DiversifyCandidate(Id, Score, float[]?)` (`IResultDiversifier.cs:8,14`); at λ 1.00 `Mmr(i) = Score`, strict `>` keeps the earlier candidate (`ResultDiversifier.cs:56-62,72-75`), so the output is `Take(topK)` over the fused-descending input; the head path already passes the centroid as the diversity vector (`:284-288`) |
| §3.5.3 Hydrate | ok — `RetrieveAsync(string, IReadOnlyList<PointId>, bool, bool, …)` exists (XML member at `Qdrant.Client.xml:14806`); `ToCanonicalString` is `internal static` in the same assembly (`IntelligenceVectorService.cs:202`); object point id is `KeyToUlong(ev.Key)` (`IntelligenceStoreConsumer.cs:548`) and chunk `parent_id` is `ev.Key` (`:310`), so the collapsed parent id addresses the object point |
| §3.5.4 Stream | ok — the block at `:291-323` reads `schema` (column lookup), `decision.AllowedFields`, the payload dictionary, `ranked.FusedScore`, `request.TraceId`, `context.CancellationToken` and nothing else |
| §3.6 Errors and degradation | ok — no new status codes; `NotFound` → empty is `:419-424`; centroid degrade is `RetrieveVectorsOrDegradeAsync` (`:759-777`). `:217-218` is the *embedding* `Unavailable` catch, not a Qdrant-failure code (the head path lets a non-NotFound `RpcException` from `SearchNamedAsync` propagate unchanged) — a mis-citation; the routed behaviour is still fully specified. The "chunks collection NotFound at search" row is reachable only in a race, since condition 4's count read hits `NotFound` first and falls back; harmless |
| §4 Reproduction check | steps 1, 2, 3, 5 ok — `freshstack-2048-qdrant-snapshots/RESTORE.md` exists (6,000 / 18,622, delegates to the scifact-512 loop); `DocumentBudget = 50` (`BenchmarkQueryScenario.cs:42`); `benchmark-query` writes `<label>.similar.trec` from `SearchSimilar` at `TopK(50)` with no filter (`:341-383`); the guard passes at 11. Step 5 depends on §2.1 (restart without the variable is exactly the crashing configuration). Step 4's baseline → §2.3 |
| §5 The rule | tolerance and structural clause ok (report.py prints coverage and duplicate-id lines; the baseline's block reads 672 / 672, none). "Baseline's chunk-level MMR cannot explain a difference" ok in substance — the gate's collapsed λ 0.70 vs 1.00 figures differ by 0.0001 / 0.0003, inside ±0.005 (the gate's own "identical to four decimals" wording is loose, not wrong). Superset / sign paragraph → §2.3 |
| §6 Consequences | FAIL-branch diagnosis target → §2.3 |
| §7 Testing | ok — `_vector = Substitute.For<IVectorQueryService>()` is `:41`; the constructor at `:74-79` takes `Options.Create(new VectorRankingOptions{…})`; `VectorRankingOptionsTests.cs:71-99` is the `BuildConfig` + `AddVectorRanking` throw pattern. The "blank entry throws" and "default is empty" tests both pass under the §2.1 defect and do not detect it |
| Verified assumptions | → §1 |
| Known issues | ok — each names a value/latency consequence, not a mechanic; the "two chunked properties" over-count is real (`points_count` is per collection) and lands on the safe side |

### Rules and operands

| Row | Disposition |
|---|---|
| Condition 1: `schema.TypeName ∈ SimilarViaChunksTypes`, `OrdinalIgnoreCase` | ok — over-inclusion: none (an unlisted type never matches); under-inclusion: the registry is `OrdinalIgnoreCase` (`SchemaRegistry.cs:11`) so an operator's casing cannot silently miss. The list's *entries* are the other operand → §2.1 (a blank entry is producible by the shipped compose line) |
| Condition 2: chunk descriptor for `vectorDesc.PropertyName` | ok — same predicate as `centroidPossible` (`:228-229`), over `schema.ChunkFields` (`IReadOnlyList<ChunkDescriptor>`, `SchemaDescriptor.cs:38`); an embedded-only property has no descriptor and keeps the head path, as §7 expects |
| Condition 3: chunk-expressible filter — operator/clause-type/column operands | ok — `BuildChunksFilter` rejects non-EQUALS and MUST_NOT (`:880-885`), accepts the key column (`:887-888`) and metadata columns (`:889-903`, `MetadataColumns` is an `OrdinalIgnoreCase` `HashSet` with `TryGetValue`, `SchemaDescriptor.cs:76-82`), rejects everything else (`:905-911`). The preamble's own `ValidateFilterProperty` / `AllowedFields` checks still run first, so a masked metadata column throws before routing on both paths |
| Condition 3: chunk-expressible filter — the `FilterLogic` operand | → §2.2 — `BuildChunksFilter` never reads it; the only `FilterLogic` read in the file is `:179` on the object path |
| Condition 4: counts readable and object count > 0 | ok — both directions: a zero object count falls back (division by zero avoided); `NotFound` on a never-written tenant throws → fallback → head path → its own `NotFound` → empty, the same result the tenant gets today |
| Budget rule `ceil(chunks/objects) × topK × 4` | ok — see §3.4 row. `points_count` being documented as approximate can move the ceiling only at an integer boundary; the reproduction arm sits at 3.10, far from one. Values, not mechanics |
| Identity rule: collapse key = `KeyToUlong(payload["parent_id"])` | ok — over-merge: two distinct objects share a parent id only if `KeyToUlong` collides, which is the same identity the object collection itself uses for point ids (`IntelligenceStoreConsumer.cs:548`), so a collision would already be one point; under-merge: every chunk of one parent carries the same `parent_id` string (`:310`), so no parent splits. Ties keep first-seen (fused-descending) order, matching `DocumentRanking.CollapseByDocId`'s `score > existing.Score` (`DocumentRanking.cs:36`) |
| Exclusion rule: chunk without `parent_id` dropped | ok — `SearchChunks` already skips such chunks for the centroid retrieve (`:431-435`); ingest always writes the key (`:310`), so the class is empty in practice and the rule fails closed |
| Exclusion rule: parent missing at hydration skipped | ok — the only producer of that state is `HandleDeleteAsync` deleting the object point (`:553`) before the chunks (`:562`), as A17 says; the stream shortens by one row, which the spec states |
| Eligibility predicate: "listed type" — producers of the list | env/config binding only (`section.Bind`, `ServiceCollectionExtensions.cs:65`); one producer, the compose line, emits a blank entry → §2.1 |
| Ownership predicate on the routed branch | ok — see §3.2 ownership row |
| Blank-entry validation ("trimmed and non-blank, else throw") | mechanics correct in isolation; its interaction with the compose line → §2.1 |
| PASS rule ±0.005 on both measures + structural block | ok as a rule; its stated justification → §2.3 |

### Data-flow arrows

| Row | Disposition |
|---|---|
| Compose `${VECTOR_RANKING_SIMILAR_VIA_CHUNKS_0:-}` → container env → `AddVectorRanking` → `List<string>` (crosses a serialization boundary: env text → bound object) | → §2.1 — empirically the unset case produces `[""]`, not `[]` |
| Config → `VectorRankingOptions` → `ObjectSearchGrpcService._ranking` | ok — `Options.Create(opts)` singleton (`:92`), read via `rankingOptions.Value` (`:47`) |
| Preamble → `queryVector`, `vectorDesc`, `decision`, `schema` → routing decision | ok — all four exist before `:221`, where the head path's own post-embedding work begins |
| Two `GetPointCountAsync` reads → `chunksPerDoc` → `chunkLimit` | ok — both collection names from `ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks)` (`:223`, `:403`), each under its own scoped key (the JWT is per collection: live probe returned 403 across collections) |
| `SearchChunksFusedAsync` — caller 1, `SearchChunks` | ok — parameters `schema, chunkDesc, decision, queryVector, filter, topK × 4` are all in scope at `:402`; return values `results` (for `text`/`parent_id` at `:494-495`), fused list, centroids — everything the remainder of `SearchChunks` reads |
| `SearchChunksFusedAsync` — caller 2, routed `SearchSimilar` | ok — `chunkDesc` is condition 2's descriptor; `filter` is condition 3's with ownership applied; `queryVector` from the preamble; `chunkLimit` from §3.4. The routed caller then needs `parent_id` per fused id: available from `results` (`VectorSearchResult.Payload`, `IVectorRoles.cs:49-52`) keyed by `RerankedResult.Id` |
| Fused list → collapse → `(parentId, maxScore)` → `Diversify` | ok — `DiversifyCandidate(ulong, double, float[]?)`; the centroid dictionary is keyed by the same `KeyToUlong` parent id the collapse uses |
| Diversified parents → `RetrievePayloadAsync` (object collection) → payload dictionary | ok — `IReadOnlyDictionary<string,string>` per id via `ToCanonicalString`, the same shape `SearchNamedAsync` returns (`IntelligenceVectorService.cs:136-140`); same point, same payload keys (`key`, camelCased scalars) |
| `WriteSimilarResponseAsync` — caller 1, head path | ok — `r.Payload`, `ranked.FusedScore` |
| `WriteSimilarResponseAsync` — caller 2, routed path | ok — hydrated payload, collapsed max score; `DocId` is a scalar column on the object point, which is what the harness reads (`BenchmarkQueryScenario.cs:363-371`) |
| Harness `SearchSimilar` → `.similar.trec` (write-then-read: TREC file → `report.py`) | ok — `CollapseByDocId(results, 50)` then `TrecRunWriter`; the routed stream carries ≤ 50 parents with one `DocId` each; report.py's structural block is what §5 reads |
| Harness `SearchChunks` at multiplier 11 → server raw fetch → fused → MMR-selected 550 → harness collapse → `fs-2048-l070.chunks.trec` (the §5 baseline) | → §2.3 — the server-side raw pool on this arrow is 2,200 (`:410`), not the 550 the spec compares against |
| Delete → object point then chunk points | ok — `:553` then `:562` (A17) |

## 1. Verified-assumptions cross-check

| # | Status |
|---|---|
| A1 | still holds — `section.Bind(opts)` at `ServiceCollectionExtensions.cs:65`; `Lambda` rejected at `:58-62`; validation follows at `:67-90` |
| A2 | still holds — `VectorRanking__*` appears only at `docker-compose.yml:445-446`, under `iverson-api` (`:431`); `iverson-worker` (`:521`) carries none, and only `Iverson.Api/Program.cs:188` calls `AddVectorRanking` |
| A3 | still holds — `SchemaRegistry.cs:11` |
| A4 | still holds — `vectorDesc` at `:143-148`; `centroidPossible` at `:228-229` over `schema.ChunkFields` |
| A5 | still holds — `SearchClause` (proto `:34`), `SearchLogic` (`:74`), `SearchClauseType` (`:79`) shared; both requests carry `filter` and `filter_logic` (`:107-108`, `:122-123`); `BuildChunksFilter` is `private static` and throws `RpcException` (`:870-915`). Note what A5 does *not* assert: that `BuildChunksFilter` reads `filter_logic`. It does not — see §2.2 |
| A6 | still holds — the chunk-vector retrieve (`:453-461`; the spec's `:449-455`) reads only `results`, `chunksCollection`, `vectorName`, `topK`, and sits after the centroid retrieve (`:438-444`) |
| A7 | still holds — `IVectorRoles.cs:5-21` has search/search-named/retrieve-named-vector only; `GetCollectionInfoAsync(string, CancellationToken)`, `CollectionInfo.PointsCount` (+ `HasPointsCount`), and `RetrieveAsync(string, IReadOnlyList<PointId>, bool, bool, …)` are all in the 1.18.1 XML |
| A8 | still holds — `IResultReranker.cs:9,13` |
| A9 | still holds — `IResultDiversifier.cs:8,14`; the λ 1.00 = `Take(topK)` identity is also visible in `ResultDiversifier.cs:72-75` |
| A10 | still holds — `IVectorRoles.cs:49-52`; `KeyToUlong` is `internal static` at `IntelligenceStoreConsumer.cs:701`, same assembly as the service |
| A11 | still holds — `:291-323` |
| A12 | still holds — `IntelligenceTenantScope.cs:11-16`; call sites `:223` and `:403` |
| A13 | still holds — `ObjectSearchGrpcServiceTests.cs:41`, `:74-80`; `VectorRankingOptionsTests.cs:71-99` |
| A14 | still holds — `BenchmarkDocument.cs:16-18` carries both attributes on `Body`; `freshstack-2048-qdrant-snapshots/RESTORE.md` exists and names 6,000 / 18,622; `DocumentBudget = 50` at `BenchmarkQueryScenario.cs:42` |
| A15 | still holds — only `Lambda` is rejected (`:58`) |
| A16 | still holds — same point id on the same collection; `ToCanonicalString` at `IntelligenceVectorService.cs:202-209` is `internal static` and reusable |
| A17 | still holds — `:553` (`DeleteAsync` object point) precedes `:562` (`DeleteByFilterAsync` chunks) |
| A18 | still holds — `:133-158` and `:336-356` are the same sequence (schema, evaluate, descriptor, collection, field authorization) |
| A19 | still holds — `IntelligenceStoreConsumer.cs:307-314`; `ObjectSearchGrpcService.cs:368-372` |

**Span check** — design dependencies no listed assumption covers:

- *"An empty value binds no entry"* (§3.1) — not covered by A1/A15, which verify the binding site and the absence of a rejected key, not the binder's treatment of an empty string. Verified in-round: **false** → §2.1.
- *"`BuildChunksFilter` enforces `FilterLogic` AND"* (§3.2 condition 3) — A5 verifies the function is static and throws, not what it checks. Verified in-round: **false** → §2.2.
- *"The routed pool is a superset of the harness's 550 chunks"* (§5) and *"the served ranking is one the harness has already measured"* (§2) — no assumption; verified in-round against `BenchmarkQueryScenario.cs:393` and `ObjectSearchGrpcService.cs:410`: **false** → §2.3.
- *A collection-scoped read JWT can read collection info* (§3.4, "each is read under its collection's scoped read key") — no assumption; verified in-round by live probe: holds (200 with `points_count`; 403 for the other collection).
- *Chunk `parent_id` maps to the object point id under one function* (§3.5.1/§3.5.3) — A10 covers reachability of `KeyToUlong`, not the agreement; verified in-round: `IntelligenceStoreConsumer.cs:548` and `:310` agree, and the existing centroid retrieve (`:431-444`) already depends on it.

## 2. Literal-wrongness findings

### 2.1 — §3.1: the compose line plus the blank-entry rejection stops `iverson-api` from starting whenever the option is *not* enabled

**Description.** §3.1 adds `- VectorRanking__SimilarViaChunksTypes__0=${VECTOR_RANKING_SIMILAR_VIA_CHUNKS_0:-}` to `iverson-api` and asserts "An empty value binds no entry." It also adds a validation that "every entry is trimmed and non-blank, else `InvalidOperationException`." With the shell variable unset — the default, and §4 step 5's restore state — compose sets the container variable to the empty string, the binder appends `""` to the list, and the new validation throws inside `AddVectorRanking` at `Program.cs:188`. The API never starts on the shipped configuration.

**Evidence.**
- Empirical, `Microsoft.Extensions.Configuration.Binder` 10.0.11 (the framework the project targets), binding `public List<string> SimilarViaChunksTypes { get; set; } = []` from `VectorRanking__SimilarViaChunksTypes__0=""`:
  ```
  env-provider: count=1 entries=['']
  in-memory:    count=1 entries=['']
  real value:   count=1 entries=[BenchmarkDocument]
  ```
  `BindInstance` converts a non-null section value through the `string` type converter and the collection binder adds it; there is no empty-string skip for `string` elements (only `Nullable<T>` gets one).
- `ServiceCollectionExtensions.cs:65` binds through exactly this path; the existing compose entries at `docker-compose.yml:445-446` all carry a non-empty default (`:-1.00`, `:-0.70`), so nothing today exercises the empty case.
- §7's proposed tests — "blank entry throws", "default is empty", "a listed entry round-trips" — all pass with this defect present; none binds the compose line's actual value.

**Proposed fix.** Either (a) drop the `${…:-}` form: list the variable in compose without a value (`- VectorRanking__SimilarViaChunksTypes__0`), which compose passes through only when set in the launching shell and leaves unset otherwise — §4 step 2 then sets that exact name — or (b) keep the line and make the validator *drop* whitespace-only entries after trimming rather than reject them, inverting §7's "blank entry throws" test to "blank entry is ignored". Either resolves §4 step 5. The spec should state which, since (b) changes the §3.1 contract it currently specifies.

### 2.2 — §3.2 condition 3: `BuildChunksFilter` does not enforce `FilterLogic`, so an OR filter on a listed type would be routed and served with AND semantics

**Description.** Condition 3 requires "`FilterLogic` AND (or a single clause)" and says this "is exactly what `BuildChunksFilter` (`:870-905`) enforces for `SearchChunks`", then defines `TryBuildChunksFilter` as "returns `false` where it would throw". `BuildChunksFilter` never reads `filter_logic`: it puts every clause into `filter.Must` (`:888`, `:902-903`), and `SearchChunksRequest.filter_logic` (proto `:123`) is silently ANDed today. As specified, a two-clause `SearchSimilarRequest` with `filter_logic = OR` on a listed type passes condition 3, is routed, and returns the AND row set — while the head path for the same request honours OR (`:178-179` → `IntelligenceFilterBuilder.Build`, `Should` at `IntelligenceFilterBuilder.cs:40,44`). That breaks the spec's own statements that the routed filter is "exactly" the `SearchChunks` contract and that row selection is identical on both paths, and §7's "with `FilterLogic` OR over two clauses → not routed" test would fail against the design as written.

**Evidence.** `grep -n FilterLogic ObjectSearchGrpcService.cs` → one hit, `:179`, on the `SearchSimilar` object-filter build. `BuildChunksFilter` `:870-915` contains no logic branch.

**Proposed fix.** Make the logic test an explicit sub-condition of the routing decision in `SearchSimilar` — `request.Filter.Count <= 1 || request.FilterLogic == SearchLogic.And` — evaluated before `TryBuildChunksFilter`, and delete the "exactly what `BuildChunksFilter` enforces" sentence. It must live in the routing decision, not in the shared function: §2 requires `SearchChunks` "preserved bit for bit", and `SearchChunks` today accepts OR and ANDs it, so adding a throw to the shared function would change that RPC's behaviour.

### 2.3 — §5 / §2 / §6: the routed chunk pool is not a superset of the harness's, and the baseline run was fused over a different pool than the design serves

**Description.** §5 says "the harness fetched 550 chunks per query (50 × 11), the routed path fetches 800, so the routed pool is a superset … the sign is expected non-negative", and §2 says "the served ranking is one the harness has already measured; §4 reproduces it." The 550 is the harness's *request* (`BenchmarkQueryScenario.cs:393`, `TopK(50 × 11)`); the server answered it by fetching `550 × OverFetchFactor` = **2,200** raw chunks (`ObjectSearchGrpcService.cs:410`), fusing all 2,200 with the 0.45 centroid term (`:467-483`, `VectorRankingOptions.cs:14-15`), MMR-selecting 550 at λ 0.70, and streaming those; the harness collapsed that list. The routed path (§3.4) fetches **800** raw chunks and fuses those. Fused order is not raw order, so a chunk at raw rank 801–2,200 with a strong parent centroid can be inside the harness's 550 and outside the routed pool, and a chunk below the harness's 550-boundary can win a parent in the routed collapse. Neither pool contains the other; the sign is not determined; and the ranking the harness measured was produced from a pool the design never builds. Whether the resulting difference exceeds ±0.005 is exactly what §4 measures — but §6's FAIL branch sends the diagnosis to `MaxPassageAggregator` / `ChunkDiversity`, which cannot be the cause, and omits the one dimension in which the design and its baseline differ by construction.

**Evidence.** `BenchmarkQueryScenario.cs:391-393` (`TopK((uint)(DocumentBudget * chunkBudgetMultiplier))`), `ObjectSearchGrpcService.cs:410` (`fetchLimit = topK * OverFetchFactor`), `:749` (`OverFetchFactor = 4`); spec §3.4 (`50 × 4 × 4 = 800`, §4 step 3).

**Proposed fix.** Replace the superset/sign paragraph with the actual relation (800 raw vs 2,200 raw; fused-order selection in between), and put "budget: the routed raw pool is 800 where the baseline's was 2,200" first in §6's FAIL diagnosis. If the check is meant to be a near-exact reproduction rather than a tolerance check, narrow the gap at the source: a fresh `.chunks` baseline on the same binary with `--chunk-budget-multiplier 4` (request 200 → server raw fetch 800, the routed pool; `ChunkBudgetGuard` passes at 200 / 3.10 ≈ 64 ≥ 50) and `VECTOR_RANKING_LAMBDA_CHUNKS=1.00` so the streamed 200 are the fused top of that same 800. The residual difference is then only that the harness collapses the first 200 fused chunks while the routed path collapses all 800, which agree whenever the first 200 fused chunks hold ≥ 50 distinct parents. Note that also removes the `BUILD MISMATCH` the current §4 step 4 has to excuse.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has three items (§2.1 blocks every deployment that does not enable the option, including §4 step 5; §2.2 and §2.3 are correctness of the routing contract and of the reproduction check's premise); §3 is empty. Address §2, then proceed to implementation planning.
