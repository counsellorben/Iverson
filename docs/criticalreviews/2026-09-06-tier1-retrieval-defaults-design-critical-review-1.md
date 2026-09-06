# Critical Design Review: 2026-09-06-tier1-retrieval-defaults-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-06-tier1-retrieval-defaults-design.md`
**Verified Assumptions section:** present

Reviewed against main at `22cbc26`. Live checks (read-only) on the running stack: `tei-embed` `/info`, Qdrant
collection info for the two benchmark collections. Data checks run over
`~/repositories/iverson-benchmark-corpora/` with `ingest.py`'s own `split_into_chunks`.

## 0. Coverage enumeration

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | Header / Depends-on | ok — the slice dir holds `beir/`, `qrels.trec` (20,209 rows, iteration 0) and all five topics have `freshstack/qrels.tsv` (4 columns each) |
| S2 | §1 Why | dropped — "Diversification off costs `SearchSimilar` 9 % / 12.8 % of R@50" inverts the cited finding (`centroid-weighting-proposal.md` "The MMR finding": turning diversification **off** *improves* R@50, 123 queries up / 30 down). §7.2's own rationale ("the R@50 price is measured") states the true direction and no rule reads §1, so no verdict changes; wording only |
| S3 | §2 Scope | ok — the Out list (no `SearchSimilar` change, no Helm entry: `deploy/helm` has no `VectorRanking` key) matches the code |
| S4 | §2.1 Decisions | ok — 129+99+203+184+57 = 672 queries recomputed from the five `queries.jsonl` files; both windows in scope |
| S5 | §3.1 Arms and runs | → §2.1 (the 18,626 / 64,763 chunk figures are raw-chunker yields, not what the sidecar will hold); multiplier arithmetic itself ok (see R7) |
| S6 | §3.2 Phase A | ok — `Lambda` at `VectorRankingOptions.cs:17`, validation `ServiceCollectionExtensions.cs:60-78`, the two call sites `ObjectSearchGrpcService.cs:302,488`, compose `:443`; the 1.00 reduction holds on both branches (R12) |
| S7 | §3.3 Harness changes | ok except the `--baseline` pairing (→ §2.2, row D3); `--chunk-budget-multiplier` replaces the const at `:41` used at `:113` and `:369`; the raw hit list with `ParentKey` exists at `:372-378` before the collapse at `:389` |
| S8 | §3.4 Gated consequence | ok — the six sites hold exactly the literals the table cites (`IversonChunkAttribute.cs:10`, `annotations.ts:166`, `tags.go:168-171,293-294`, `IversonChunk.java:18,21`, `annotations.py:42-43,64-65`, `IngestContractTests.cs` `ChunkMaxTokens`/`ChunkOverlap`); `EmitContract` derives `chunkWindow` *and* the golden chunking cases from the same `ChunkWindow(...)` call (`IngestContractTests.cs:114,161`) so a regenerated contract is self-consistent; `Article.cs:14` is an explicit pin |
| S9 | §4 Failure semantics | → §2.1 (the count-equality check fails on a correct FreshStack ingest); the other checks ok (R13, R6) |
| S10 | §5 Component contract | ok — every file named exists at the cited location; `Iverson.LoadTest.Tests` exists; `test_report.py` exists; `similar_arms.py` does not (new, as stated) |
| S11 | §6 Evaluation protocol | → §2.2 (the `report.py` invocations in steps 1–2 do not produce the comparisons §7.2 and §7.3 read); step 4's restore is a snapshot DELETE-then-upload (migration plan P38, Task 7), not an ingest, so the §3.4 default-window change cannot reach it |
| S12 | §7.1 Window rule | ok — see R1, R2 |
| S13 | §7.2 λ rule | → §2.2 (operands never produced) and → §3.1 (rule not total over its inputs) |
| S14 | §7.3 Representation rule | → §2.2 (operands never produced) |
| S15 | §8 Testing | ok — each test names an observable of the design; the "fewer than 50 hits" diversity case is the only reachable degenerate input and it is listed |
| S16 | §9 Measurements | ok — rows 2, 7, 8, 10 reconfirmed live/from data (see §1); row 3's chunk figures are correct as raw-chunker yields (see §2.1 for how §4 misuses them) |
| S17 | §10 Verified assumptions | see §1 |
| S18 | §11 Known issues | ok — the `BUILD MISMATCH` note matches `report.py:495-497` (a print, not an exit); the "already-registered types keep their window" note is consistent with `IngestContractTests`' own doc comment |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | §7.1(a) `sci-2048` vs `bge-base` (M1): run files `sci-2048.chunks.trec` vs `…/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec`; measures nDCG@10, R@50; statistic = 95 % CI lower bound of the paired delta (`report.py:452-455`); threshold > −0.02 | ok — step 1's `--baseline …/bge-base.chunks.trec --run <dir>/runs` produces exactly this block; the CI does not depend on the Holm family, so the `.similar` and raw files swept into the same family only add ignored blocks. NaN CI (single query / zero variance) compares False → FAIL, fails closed. One arm missing → `report.py` exits before scoring (`:772`). Same rule text as the migration gate (`2026-09-GATE-embedding-migration.md:401-402`) |
| R2 | §7.1(b) `fs-2048-l070` vs `fs-512-l070`: explicit `--baseline fs-512-l070.chunks.trec --run fs-2048-l070.chunks.trec`, one qrels (the two run dirs carry identical copies of the query-level `qrels.trec`) | ok — explicit files, family of one; both runs from the post-Phase-A build |
| R3 | §7.2 `LambdaSimilar`: operands are α-nDCG@10 per-query values of `fs-<w>-l{050,085,100}.similar.trec` each paired against `fs-<w>-l070.similar.trec`, Holm p_adj over that family, from `qrels.nugget.trec` | → §2.2: no invocation in §6 pairs a `.similar` run against the `.similar` 0.70 run (`report.py:538-556` pairs every discovered run against the *one* `--baseline`, which §6 sets to the `.chunks` run). → §3.1: the rule has no output when a λ separates from 0.70 only downward, or when the two arms' argmax differ |
| R4 | §7.2 `LambdaChunks`: mean distinct parents in the top-10 chunk hits at l100 vs l070, both arms, threshold |Δ| < 1.0 | ok — operands are the two sidecars' means; the top-10 can see MMR because `SearchChunks` fetches `topK × OverFetchFactor` (= 4, `ObjectSearchGrpcService.cs:408,747`) candidates and MMR chooses the set, not just the order; NaN impossible (672 queries, ≥ 50 hits each at 6,000 docs) |
| R5 | §7.3 representation: `centroid-raw.similar.trec` vs `head-raw.similar.trec` on three arms; Holm p_adj on nDCG@10 and R@50 on both FreshStack arms; CI lower bounds > −0.02 on SciFact | → §2.2: §6 never invokes `report.py` with `--baseline head-raw.similar.trec` on any arm, so no such block exists |
| R6 | obsolete-key rejection: `VectorRanking:Lambda` present → throw | ok — over-inclusion: the only setter is compose `:443`, replaced in the same change (grep over `*.json`, `*.yml`, `*.yaml`, `*.py`, `*.sh`, `*.tpl`, `deploy/`: no other hit); under-inclusion: `VectorRanking__Lambda` binds to exactly that key; `AddVectorRanking` is called once (`Iverson.Api/Program.cs:188`), the worker never calls it |
| R7 | multiplier per corpus: 5 / 11 | ok — `ChunkBudgetGuard.Evaluate`: 550 / 3.10 = 177 ≥ 50; 550 / 10.789 = 50.98 ≥ 50; `Ceiling(10.789)` = 11 — unchanged by the corrected chunk count (64,735 / 6,000 = 10.789); 250 / 1.27 on `sci-2048` ≥ 50 |
| R8 | diversity count: distinct `ParentKey` among the first 10 / 50 hits of the diversified stream | ok — the stream is written in `Diversify` order (`ObjectSearchGrpcService.cs:488`); under a `--drop` ingest one entity per doc, so distinct parent ⇔ distinct document; fewer than 10 hits is unreachable at 6,000 docs / 550 budget; the < 50 case is a listed test |
| R9 | nugget-qrels filter to 672 query ids and 6,000 corpus ids | ok — real data: 0 query ids and 0 corpus ids appear in more than one topic; all 672 slice queries keep ≥ 1 relevant row; all 2,131 nuggets keep ≥ 1 relevant row; the 65,848 rows dropped by the corpus-id filter are **all** rel = 0 (over-inclusion none, under-inclusion none). The existing query-level `qrels.trec` equals the max-collapse of the filtered nugget rows on all 20,209 pairs, so "as the earlier runs did" is reproducible |
| R10 | query-prefix composition | ok — `ingest.embed` is `document_prefix + text` (`ingest.py:535`), a plain concatenation; the bge instruction with its trailing space is at `EmbeddingPrefixes.cs:37` |
| R11 | §4 count equality: sidecar `chunks` == chunker prediction (6,587 / 18,626 / 64,763) | → §2.1: `ingest_document` drops whitespace-only windows before counting (`ingest.py:594`, `:658`); the slice has 4 such windows at 2048/1792 and 28 at 512/448, so a correct ingest writes 18,622 / 64,735. The 2026-08-30 sidecar of the same corpus at the same window already reads 18,622. SciFact has none at either window (6,587 / 19,967 reproduce exactly) |
| R12 | "at 1.00 the diversifier reduces to `Take(topK)` bit-exactly on both branches" | ok — `ResultDiversifier.cs:75-78`: `1.0·s − 0.0·maxSim` = `s` for finite `maxSim` (cosine); `1.0·s` = `s`; strict `>` keeps fused-descending order |
| R13 | "every run file 50 × queries rows" | ok — API `.similar`: 50 of 5,183 / 6,000 entities; `.chunks`: the guard guarantees ≥ 50 reachable documents; raw `.similar`: `limit 50`, `body_centroid` present on every object point that has a non-zero chunk vector |
| R14 | §6 precondition "the box on the SciFact bge-base baseline" | ok — live: 5,183 object points with named vectors `body_vector` + `body_centroid` (768, Cosine), 19,967 chunk points; `tei-embed` serves `BAAI/bge-base-en-v1.5`, `max_input_length` 512, `auto_truncate` true |

### Data-flow arrows (persistence boundaries flagged ⧉)

| Row | Arrow | Disposition |
|---|---|---|
| D1 | raw chunk hits (in-memory `(ParentKey, Score, Text)`) ⧉ `<label>.chunks.diversity.json` → `report.py` prints two means | ok — the design defines the sidecar's two means; `report.py` needs its own path derivation for it (the `.meta.json` rule at `:163-172` strips `.chunks` and would not find it) — implementation detail, no design gap |
| D2 | topic `qrels.tsv` (qid, nugget, cid, rel) → filter ⧉ `qrels.nugget.trec` → `read_trec_qrels` (keeps iteration) → pyndeval `subtopic_id = record.iteration` (`pyndeval_provider.py:80-88`) → `iter_calc` per-query `Metric` (`:113-121`) → `paired_comparison` | ok — column order is already TREC's (qid iter docid rel); ids contain no whitespace (the converter enforces it, `freshstack_to_jsonl.py:185`); per-query values flow through the same `iter_calc` path `per_query_values` uses |
| D3 | `similar_arms.py` ⧉ `head-raw.similar.trec` / `centroid-raw.similar.trec` → `report.py` structural check, scores (`build: unknown`, A25) → `--baseline` pairing | → §2.2 (never paired against each other by the protocol) |
| D4 | λ env vars → `docker compose up -d --no-deps iverson-api` → `benchmark-query --config-label fs-<w>-l<λ>` → `report.py` pairing by file name | ok — λ is recorded only in the label, but the protocol's explicit `docker inspect` confirm step covers it; the `.meta.json` composite proves the build, not the λ, and the rule does not claim otherwise |
| D5 | §3.4 change set → regenerated `ingest-contract.json` ⧉ → `ingest.py` `MAX_CHARS`/`STEP` (`:211-213`) → invocations relying on the default window | ok — every §6 ingest passes `--chunk-max-chars`/`--chunk-step`; the SciFact baseline restore is a snapshot upload (migration plan P38 / Task 7 step 5), not an ingest; the golden chunking cases regenerate from the same window and `_verify_algorithm_goldens` checks them at contract values; `test_report.py`/`test_multivector.py` carry no window literal |
| D6 | `--chunk-budget-multiplier` → `ChunkBudgetGuard.Evaluate` (`:113`) and `TopK` (`:369`) → `.meta.json` | ok — both consumers of the const are named by A7 |
| D7 | `ResultDiversifier` constructor loses `IOptions<VectorRankingOptions>` → six existing construction sites (`VectorRankingOptionsTests.cs:59,65`; `ObjectSearchVectorIntegrationTests.cs:93`; `DocumentTemplateValidationTests.cs:337`; `ObjectSearchGrpcServiceTests.cs:78,2617`) | dropped — compile-time and mechanical; a consumer-impact item for the implementation plan, not a design defect (Phase A's "no behaviour change" is unaffected) |
| D8 | `ObjectSearchGrpcService` gains `IOptions<VectorRankingOptions>` → DI | ok — registered by `AddVectorRanking` via `Options.Create` (`ServiceCollectionExtensions.cs:80`), called at `Iverson.Api/Program.cs:188` |
| D9 | §3.1 ingest ETAs (≈ 3 h / 20 h / 25 h) | dropped — planning figures; no rule or check reads them |

## 1. Verified-assumptions cross-check

| # | Status |
|---|---|
| A1 | holds — `Lambda = 0.70` at `VectorRankingOptions.cs:17`; finiteness first (`:60-64`) then `[0,1]` (`:76-78`); `Options.Create` at `:80` |
| A2 | holds — `ResultDiversifier.cs:77-78` is the only read of `_o.Lambda` |
| A3 | holds — ctor `:31-43` with `IOptions<DecayOptions>` at `:42`; `Diversify` at `:302` and `:488` |
| A4 | holds — compose `:441-443`; `VectorRankingOptionsTests.cs:57-65`; no `VectorRanking` key in `deploy/helm` or any appsettings |
| A5 | holds — `AddVectorRanking(this IServiceCollection, IConfiguration)` at `:55` |
| A6/A23 | holds — no Api test references `IResultDiversifier` or mocks `Diversify`; the Api tests construct the concrete `ResultDiversifier` (row D7) |
| A7 | holds — const `:41`, uses `:113`/`:369`, `StrFlag`/`IntFlag` at `Program.cs:408-419`, sidecar `JsonObject` at `:203-222` |
| A8/A24 | holds — `chunks.Add((r.ParentKey, r.Score, r.ChunkText))` at `:378`, before `Aggregate` at `:389` |
| A9/A21 | holds — `PyNdEvalProvider.initialize` imports `pyndeval` (`pyndeval_provider.py:73-78`); `python-libs` has no `pyndeval` |
| A10 | holds — the provider maps `record.iteration` to `subtopic_id` (`:80-88`) |
| A11 | holds — recomputed: 1,228 / 848 / 2,309 / 1,224 / 391 docs and 129 / 99 / 203 / 184 / 57 queries; `qrels.tsv` present in all five |
| A12 | holds **as scoped** — 18,626 and 64,763 are what `split_into_chunks` yields on the slice, and the minimum multiplier is 11 either way. It does not cover what §4 uses it for: the sidecar counts windows *after* `ingest_document` drops whitespace-only ones (`ingest.py:594`), which yields 18,622 / 64,735 — see §2.1 |
| A13 | holds — 6,587 at 2048/1792 on `scifact-run-2026-08-26/beir/corpus.jsonl`, zero whitespace-only windows |
| A14/A15 | holds — live: 5,183 / 19,967 points; `body_vector` + `body_centroid` named vectors, 768 dims; `docId` is the payload key `ingest.py:632` writes |
| A16 | holds — contract carries `documentPrefixes` / `defaultDocumentPrefix` only; the bge query instruction is at `EmbeddingPrefixes.cs:37` (the spec cites `:35`, the comment line above it — same entry) |
| A17/A22 | holds — a grep for `512` across `Iverson.Clients/**` (non-test source) hits exactly the five client files plus `Article.cs`; `IngestContractTests.cs` copies the pair as `ChunkMaxTokens`/`ChunkOverlap` |
| A18 | not re-read (grep-verified by the spec; nothing in this round contradicts it) |
| A19 | not re-read (file times; nothing in this round depends on it) |
| A20 | holds — compose only; `deploy/helm` has no `VectorRanking` key |
| A25 | holds — `sidecar_path_for("head-raw.similar.trec")` → `head-raw.meta.json`, absent → `None` → "unknown" (`report.py:163-198`) |

**Span check** — dependencies no listed assumption covers:

- *The stats sidecar's `chunks` equals the raw chunker's yield.* Not covered; false — `ingest.py:594` filters `[c for c in chunks if c[0]]` before `:658` counts. → §2.1.
- *`report.py --baseline` pairs each `.similar` λ run against the `.similar` 0.70 run, and `centroid-raw` against `head-raw`.* Not covered; false — `run_paired_statistics` (`:538-556`) pairs every discovered run against the single `--baseline`. → §2.2.
- *The λ rule (§7.2) yields a verdict for every possible sweep outcome.* Not covered; false for two input classes. → §3.1.
- *The five topics' query and corpus ids do not collide, and every slice query's relevant documents are inside the slice.* Verified in-round: 0 collisions in either id space; 0 relevant (rel = 1) rows outside the slice.
- *The SciFact baseline restore (migration Task 7 step 5) does not re-ingest.* Verified in-round: it is a DELETE-then-upload snapshot restore (migration plan P38), so §3.4's `ingest.py` default-window change cannot reach it.

## 2. Literal-wrongness findings

### 2.1 The §4 chunk-count check rejects a correct FreshStack ingest

**Description.** §4 requires "each ingest's sidecar must equal the chunker's prediction for its window (6,587 / 18,626 / 64,763 chunks)", and §6 steps 2–3 say "expect 6,000 / 18,626" and "expect 64,763". Those two FreshStack figures are the raw yield of `split_into_chunks`. The sidecar does not record that: `ingest_document` drops whitespace-only windows before it embeds or counts anything (`ingest.py:594` `chunks = [c for c in chunks if c[0]]`; `:658` `stats["chunks"] += len(chunk_points)`). The slice contains 4 such windows at 2048/1792 and 28 at 512/448 (recomputed this round with `ingest.py`'s own chunker over `freshstack-chunk512-2026-08-30/beir/corpus.jsonl`), so a correct `fs-2048` ingest writes **18,622** and a correct `fs-512` ingest writes **64,735**. The existing `freshstack-chunk512-2026-08-30/keymap.json.stats.json` — the same corpus at the same 2048/1792 window — already reads `"chunks": 18622`, which the spec's own §9 row 3 quotes without reconciling it against the 18,626 it predicts. SciFact is unaffected (no whitespace-only windows: 6,587 and 19,967 reproduce exactly).

**Why it fails the spec's outcome.** The check is the protocol's fail-loud gate on each ~20 h ingest. As written it fires on both FreshStack arms after a correct ingest, so either the protocol halts twice or the operator overrides the gate by hand — and an overridable count check no longer proves anything about the ingest.

**Evidence.** `ingest.py:594,658`; `freshstack-chunk512-2026-08-30/keymap.json.stats.json` (`"documents": 6000, "chunks": 18622`); chunker run: slice 2048/1792 → 18,622 kept + 4 empty; slice 512/448 → 64,735 kept + 28 empty; scifact 2048/1792 → 6,587 + 0; scifact 512/448 → 19,967 + 0.

**Proposed fix.** State the prediction the way the sidecar counts it: "the chunker's prediction *after dropping whitespace-only windows*, exactly as `ingest_document` does" — 6,587 / 18,622 / 64,735 — in §3.1's table, §4, §6 steps 2–3 and §9 row 3 (row 3 can keep the raw figures beside the kept ones; the 08-30 run then reconciles). The multiplier arithmetic is unchanged (64,735 / 6,000 = 10.79, minimum 11).

### 2.2 The protocol's `report.py` invocations never produce the comparisons §7.2 and §7.3 read

**Description.** §7.2 (`LambdaSimilar`) decides on "α-nDCG@10 on `.similar` … with Holm p_adj < 0.05", i.e. each `fs-<w>-l{050,085,100}.similar.trec` paired against `fs-<w>-l070.similar.trec`. §7.3 decides on `centroid-raw.similar` paired against `head-raw.similar` on all three arms (Holm p_adj on the FreshStack arms, CI lower bounds on SciFact). §6 invokes `report.py` with exactly one baseline per arm — `--baseline …/bge-base.chunks.trec` (step 1) and `--baseline fs-2048-l070.chunks.trec` / `fs-512-l070.chunks.trec` (steps 2–3). `run_paired_statistics` pairs **every** other discovered run against that single baseline (`report.py:538-556`, `:566-594`) and nothing else. So the protocol yields `fs-2048-l085.similar` vs `fs-2048-l070.chunks` (a cross-endpoint pairing that no rule reads) and never `fs-2048-l085.similar` vs `fs-2048-l070.similar`, nor `centroid-raw.similar` vs `head-raw.similar` on any arm. Verdicts 2 and 3 cannot be computed from the written protocol. `--pair RUN=BASELINE` is not an escape: it enforces pool invariance (`check_pool`, `:636-665`), and MMR at a different λ (or a different named vector) changes each query's top-50 set — the arm would be declared invalid (this is the multivector spec's A13, which the spec's own A25 cites).

A second consequence of the same invocation shape: with `--run <dir>/runs`, the Holm family for the FreshStack arms is the 9 other `.trec` files in the directory (three `.similar` λ runs, four `.chunks` runs, two raw runs), so even if the right baseline were given, "p_adj < 0.05" in §7.2 would be corrected across cross-endpoint and raw pairings the rule never reads. The rule's threshold is undefined until the family is named.

**Evidence.** `report.py:538-556` (one baseline, all discovered runs), `:566-585` (Holm family = every valid comparison in the invocation), `:636-665` (`--pair` pool check); spec §6 steps 1–3, §7.2, §7.3.

**Proposed fix.** Give §6 one `report.py` invocation per rule, with `--run` listing only that rule's files (`--run` accepts individual files, repeatable, `report.py:719-722`), so the family is explicit:

- §7.2, per FreshStack arm: `--baseline fs-<w>-l070.similar.trec --run fs-<w>-l050.similar.trec --run fs-<w>-l085.similar.trec --run fs-<w>-l100.similar.trec --qrels qrels.trec --nugget-qrels qrels.nugget.trec` — family of 3 per arm per measure.
- §7.3, per arm: `--baseline head-raw.similar.trec --run centroid-raw.similar.trec` — family of 1; on SciFact the CI bounds come from the same block.
- §7.1(a) keeps step 1's invocation (its CI is family-independent); the "reported, not gated" `.similar` blocks can stay in a separate directory-wide invocation.

Then state in §7.2 what the family is (the three λ arms within one endpoint and one FreshStack arm), since the p_adj threshold has no meaning without it.

## 3. Forced decisions

### 3.1 §7.2's `LambdaSimilar` rule has no verdict for two reachable sweep outcomes

**The choice.** What `LambdaSimilar` becomes when (i) some λ separates from 0.70 on α-nDCG@10 *only downward* (significantly worse on an arm) and no λ qualifies under the first clause, and (ii) the two FreshStack arms disagree on which λ has "the highest α-nDCG@10".

**Why it is forced.** The rule is written as two clauses: pick the argmax λ if it beats 0.70 with Holm p_adj < 0.05 on at least one arm and is not significantly worse on the other; "otherwise, if no λ separates from 0.70 on α-nDCG@10 on either arm, 1.00". Case (i) satisfies neither — a λ *did* separate (downward), so the second clause's condition is false, and nothing qualifies for the first. This is not exotic: λ = 1.00 being significantly worse than 0.70 on α-nDCG is exactly the outcome that would show MMR's benefit, and the rule then says nothing. Case (ii): "the λ with the highest α-nDCG@10" is evaluated on two arms with no tie-break across arms; the first clause's "beats 0.70 on at least one arm" presupposes a single candidate λ. §6 step 6 sets the default "per §7.2 (one commit, gate cited beside the values)" — an input class with no output leaves that commit unwritable.

**The options** (reviewer does not pick):

- For (i): (a) 0.70 stays whenever any λ is significantly worse than 0.70 on either arm (a demonstrated MMR effect keeps MMR); (b) 1.00 unless λ = 1.00 *itself* is significantly worse than 0.70 on either arm (only the off-vs-on comparison protects 0.70); (c) rewrite the second clause as "if no λ *beats* 0.70 …, 1.00" (downward separation is ignored; matches the "price is measured, benefit is not" rationale literally).
- For (ii): (a) the argmax of the mean α-nDCG@10 across the two arms; (b) evaluate the first clause for every λ and apply the existing tie-break ("ties between qualifying values go to the larger λ") among all qualifiers; (c) require the same argmax on both arms, else fall through to the second clause.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty (one item); §2 has two literal-wrongness findings to address alongside it.
