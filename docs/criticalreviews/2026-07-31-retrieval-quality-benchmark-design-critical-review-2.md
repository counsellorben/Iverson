# Critical Design Review: 2026-07-31-retrieval-quality-benchmark-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-31-retrieval-quality-benchmark-design.md`
**Verified Assumptions section:** present

Spec re-read at `a78d1b8`; codebase at the same commit. The §0 enumeration below was built before
re-reading round 1, so round 1's fix is one row in it rather than the search area.

## 0. Coverage enumeration

**Sections**

| Row | Disposition |
|---|---|
| §1 The two ablations | ok — re-derived independently of round 1: both constants still `private const double` (`ResultReranker.cs:12`, `ResultDiversifier.cs:12`), each read at one site |
| §2 Corpora | ok — corpus-scale figures remain external claims the spec marks unfetched (A2's own text). Document *size* generated a candidate — see the dropped row below |
| §3 Corpus → Iverson mapping | ok — every declaration checked against the attribute definitions: `[IversonChunk]` defaults to `(maxTokens: 512, overlap: 64)` (`IversonChunkAttribute.cs:10`), so the spec's bare annotation is legal; dual annotation re-confirmed additive (`SchemaRegistrar.cs:186-200`); authorization paragraph now present (round 1's fix — see §4) |
| §4 Query execution and run files | → §2.1 |
| §5 Scoring is external | ok — no Iverson code path; run-file format is the harness's own output |
| §6 Harness location | ok — `Iverson.LoadTest/Program.cs:117-122` unchanged |
| §7 Running the sweep | ok — `ResultRerankerTests.cs:28` still hand-computes `0.77`; the new non-empty-run-file check is a harness-side step with no code dependency |
| Testing | ok — both named targets are pure functions over harness-owned data |
| Known issues / Not in this spec | ok — A10 ingest feasibility still carried; no code claim |

**Rules and operands**

| Row | Disposition |
|---|---|
| `top_k` semantics — `SearchSimilar` (documents) vs `SearchChunks` (chunks) | → §2.1. Mandatory row: two structurally-similar operands the spec treats identically. Under-inclusion direction is where it breaks |
| Max-passage aggregation, both directions | ok — the *rule* is sound (one entry per parent; every returned parent appears). Round 1 disposed this row on the rule alone; the *budget* feeding it is a separate operand, which is §2.1 |
| Ablation "off" settings, both directions | ok — re-derived: at Λ=1 both branches of `Mmr` reduce to `Score` (`ResultDiversifier.cs:76-77`); at `WCentroid=0` the term is absent from numerator and denominator (`ResultReranker.cs:35-42`) |
| Eligibility predicate — readable identities | ok — resolved by round 1's fix; A21 now covers it |
| Identity rule — `ParentKey → DocId` | ok — `parent_id` is `ev.Key` (`IntelligenceStoreConsumer.cs:253`), unique per document; no over-merge |

**Data-flow arrows**

| Row | Disposition |
|---|---|
| query text → server-side embedding — **crosses the RPC boundary** | ok — the operation needs a vector the harness never computes. `SearchSimilarRequest.query` is `// text to embed and compare` (`object_search.proto:104`); the server embeds. No client-side embedding step is missing from the spec |
| `SearchChunks` topK → run-file document rows | → §2.1 |
| `Body` → StarRocks scalar column — **crosses a persistence boundary** | dropped — real mechanism, fails literal-wrongness. `Body` does land in `ScalarColumns` and every scalar is projected into StarRocks as `STRING` (`SchemaBuilder.cs:192-193`, `:314-315`), which is hard-capped at 65,533 bytes, and the large-field exclusion filter was reverted, so an oversized document errors on insert. But `SearchSimilar`/`SearchChunks` read **Qdrant**, not StarRocks, and the failure dead-letters that one message via `MessageDispatcher` (`:64-75`) without blocking the topic. The spec's asked-for outcome — run files from the two vector RPCs — is unaffected |
| ingest → `ParentKey → DocId` map | ok — `PersistAsync` returns `response.Key` (`EntityCoordinator.cs:111`); re-confirmed |
| centroid lookup on the chunk path | ok — `centroids.TryGetValue(KeyToUlong(parent))` (`:417-419`) keys on the same `parent_id`; consistent with the map above |

## 1. Verified-assumptions cross-check

All twenty-one assumptions **still hold**, including A21 added by round 1's fix — re-read at
`SchemaRegistrar.cs:26-30`, `RowFieldAuthorizationEvaluator.cs:11-12`, and
`ObjectSearchGrpcService.cs:126-127` / `:298-299`.

**Span check — one uncovered dependency, verified in-round and promoted to §2.1:**

The design depends on `top_k` bounding the same unit the metrics are defined over. A13 verifies
only that `top_k` has no *upper clamp*; nothing states what `top_k` counts on each RPC. That gap is
where §2.1 lives.

## 2. Literal-wrongness findings

### 2.1 On `SearchChunks`, `top_k` bounds chunks, not documents — so `topK = 50` cannot produce a 50-document run file

**Description.** §4 sets one budget for both RPCs: "`topK` is set to at least 50 to serve
Recall@50." That holds for `SearchSimilar`, which returns one result per entity. It does not hold
for `SearchChunks`, which returns one response per *chunk*, with no parent deduplication. The
harness then collapses chunks to documents by max score — so 50 returned chunks yield at most 50
documents and typically far fewer, because a chunked `Body` contributes several chunks and nothing
stops multiple chunks of one document from occupying the budget.

With `[IversonChunk]`'s default 512-token window, a 2,000-token FreshStack document is ~4–5 chunks;
if the top 50 chunks average even 3 per parent, the chunk-path run file carries ~17 documents per
query. Recall@50 is then computed over a list that structurally cannot reach 50, and is understated
for every configuration in the sweep. Coverage@20 is exposed the same way. Because the shortfall is
uniform across configurations it does not obviously look wrong — it silently compresses the very
metric range the λ ablation is meant to move.

**Evidence.**

- `ObjectSearchGrpcService.cs:437` — `foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK))`, where the candidates are chunk points; `topK` bounds that loop.
- `:442-450` — each iteration writes one `ChunkSearchResponse` carrying `ParentKey`; nothing dedups by `parent_id`, so N chunks of one parent produce N responses.
- Contrast `SearchSimilar` at `:267`, whose candidates are entity points — there `topK` and "documents" coincide. The spec's single budget is correct for one RPC and wrong for the other.
- `IversonChunkAttribute.cs:10` — `maxTokens = 512, overlap = 64` defaults, which the spec's bare `[IversonChunk]` adopts.

**Proposed fix.** State the two budgets separately in §4. For `SearchSimilar`, `topK = 50` is
already right. For `SearchChunks`, request enough chunks that ≥50 *distinct parents* survive
aggregation, and truncate to 50 documents after collapsing — either by requesting a multiple of 50
(a fixed multiplier is simplest, and the 4× server-side over-fetch already absorbs the extra ANN
cost) or by re-querying with a larger `top_k` when a query yields fewer than 50 distinct parents.
Whichever is chosen, the run file must be truncated to exactly the top 50 documents so Recall@50 is
computed over the intended cutoff.

Worth recording in the same edit: the chunk budget must be chosen once and held constant across all
eight configurations, or the ablation compares run files built from different candidate-pool sizes.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (benchmark entity denied on read, silently, because no authorization rules were
  declared) is resolved by the spec's current §3 paragraph and the new A21. Re-read at HEAD: the
  paragraph names the dictionary entry, the bypass identity, why no `OwnerId` is needed, and the
  `tenant_id` requirement. The §7 non-empty-run-file check that came with it would also have caught
  §2.1's failure mode partially — though not fully, since a short run file is non-empty.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.**

One finding, in the seam between A13 (`top_k` has no upper clamp) and the metric definitions
(`Recall@50` over documents). Round 1 checked the aggregation rule and the over-fetch multiplier and
marked both `ok` — correctly, on their own terms — without checking what the budget feeding them
counts. The fix is spec text in §4, not a design change.
