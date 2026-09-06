# Critical Design Review: 2026-09-06-tier1-retrieval-defaults-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-06-tier1-retrieval-defaults-design.md`
**Verified Assumptions section:** present

Reviewed against main at `17598f4` (the spec as revised by `update-design-doc` after round 1). Live checks
(read-only) on the running stack: `tei-embed` `/info`; Qdrant collection info and one scrolled point of
`benchmark_documents_tenant_bypass`; `benchmark_documents_chunks_tenant_bypass` point count. Data checks run
over `~/repositories/iverson-benchmark-corpora/` with `ingest.py`'s own `split_into_chunks` and a
five-topic nugget-qrels filter written for this round. The enumeration below was built before round 1 was
read.

## 0. Coverage enumeration

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | Header / Depends-on | ok — slice dir holds `beir/` (6,000 docs, 672 queries), `qrels.trec` (20,209 rows), `keymap.json.stats.json` (`chunks: 18622`); all five topics carry `freshstack/qrels.tsv` (4 tab-separated columns, no header row) |
| S2 | §1 Why | dropped — the sentence "Diversification off costs `SearchSimilar` 9 % / 12.8 % of R@50" still carries the sign inverted against its evidence (`docs/2026-09-06-ranked-changes…md` item 2: turning MMR **off** *raises* R@50 by +0.0216 / +0.0558). Literal-wrongness test applied afresh: no rule, invocation or check reads §1; §7.2's own justification for the 1.00 default ("the R@50 price is measured and the benefit is not") states the true direction; the three verdicts are computed from compare blocks, not from §1. The asked-for outcome is unchanged, so this is wording, not a finding |
| S3 | §2 Scope | ok — Phase A "no behaviour change" holds (row R12); Out-list items match the code (no Helm `VectorRanking` key anywhere under `deploy/`; the only λ setter is compose `:443`) |
| S4 | §2.1 Decisions | ok — the two rewritten λ-rule choices "(b) and (b) of CDR-1 §3.1" are the ones the §7.2 text now implements (row R3); "install gcc, use pyndeval" is gated by §6's precondition "pyndeval importable", not by gcc itself, so whether the extension also wants `g++` is Ben's install step, not a design fact |
| S5 | §3.1 Arms and runs | ok — counts as revised reproduce exactly (row R11); the multiplier arithmetic holds (row R8); the ETAs are planning figures no check reads |
| S6 | §3.2 Phase A | ok — `Lambda` at `VectorRankingOptions.cs:17`; validation order finiteness (`ServiceCollectionExtensions.cs:60-64`) then `[0,1]` (`:76-78`); `_o.Lambda` is `ResultDiversifier`'s only use of options (`:77-78`); the two `Diversify` call sites (`ObjectSearchGrpcService.cs:302,488`) sit in `SearchSimilar` and `SearchChunks` respectively; compose `:442-443` is the api service only; the 1.00 reduction (row R12) |
| S7 | §3.3 Harness changes | ok — `IntFlag`/`StrFlag` exist (`Program.cs:407-419`); the raw hit list with `ParentKey` is materialised at `BenchmarkQueryScenario.cs:372-378` before `MaxPassageAggregator.Aggregate` at `:389`; `report.py`'s directory discovery globs `*.trec` only (`:135-153`), so a `.chunks.diversity.json` sidecar beside the runs is never mistaken for a run; `ingest.embed` composes `document_prefix + text` (`ingest.py:535`) and `qdrant_request` is a module-level helper with the api-key header (`:279-303`), both importable the way `multivector.py:26-27` does it; the nugget filter (row R9) |
| S8 | §3.4 Gated consequence | → §2.1 (a sixth default site inside the Python client) and → §3.1 (a server-side producer of the same window); the six listed sites hold the cited literals (`IversonChunkAttribute.cs:10`, `annotations.ts:166`, `tags.go:168-171,293-294`, `IversonChunk.java:18,21`, `annotations.py:42-43,64-65`, `IngestContractTests.cs:75-76`); the token→char arithmetic (128·4 = 512, (128−16)·4 = 448) matches the consumer's rule the contract test copies |
| S9 | §4 Failure semantics | ok — the revised count check (18,622 / 64,735 after whitespace-only windows are dropped, `ingest.py:594`) now matches what `ingest_document` counts (row R11); the guard is evaluated at the arm's multiplier (row R8); the row-count and coverage checks (row R13); the obsolete-key throw (row R7) |
| S10 | §5 Component contract | ok — every named file exists at the cited location; `similar_arms.py`, `test_similar_arms.py` and the gate document are new, as stated (`docs/plans/2026-09-GATE-tier1-defaults.md` does not exist yet) |
| S11 | §6 Evaluation protocol | ok — the three invocations per arm now name their files explicitly (rows D7, D8); the window comparison across two run dirs (row D9); step 4's restore is a snapshot upload, not an ingest (round 1 D5, unchanged) |
| S12 | §7.1 Window rule | ok — rows R1, R2 |
| S13 | §7.2 λ rule | ok — rows R3, R4, R5 |
| S14 | §7.3 Representation rule | ok — row R6 |
| S15 | §8 Testing | ok — every listed test names an observable of the design; the Api wiring test's "hard-coded 0.70 at either site must fail exactly one of them" is the right falsifier for the per-endpoint split |
| S16 | §9 Measurements | ok — rows 2, 3, 4, 7, 8, 10 reconfirmed from data / live (§1 below); row 6 reconfirmed (`python-libs` holds no `pyndeval`; no `gcc` on PATH; `pip` cache holds no pyndeval sdist) |
| S17 | §10 Verified assumptions | see §1 |
| S18 | §11 Known issues | ok — the `BUILD MISMATCH` note: M1's `bge-base.meta.json` composite is `7d3a15092f963723` and `print_compare_block` prints, never exits (`report.py:490-497`). The "halves R@50 relative to the nugget file's judged pairs" bullet is imprecise on the data (the query-level file's 5,445 rel > 0 pairs equal the nugget file's max-collapse exactly; what the nugget file *would* halve is R@50 scored through pytrec_eval's `(q, d)`-keyed reader) — dropped: no rule reads absolute recall, every §7 statistic is a paired delta on one qrels file |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | §7.1(a): `sci-2048.chunks.trec` (run) vs `…/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` (baseline); nDCG@10 and R@50; 95 % CI lower bound > −0.02 | ok — `--baseline` is M1, `--run` is the new arm, so `delta = run − baseline` = 2048 − 512, the direction the rule reads; `ci[0]` is `delta − t_crit·se` (`report.py:454-456`); NaN (n ≤ 1) compares False → FAIL, fails closed, and n = 300 makes it unreachable; a missing arm exits before scoring (`:771-772`); M1's sidecar exists with `chunk_max_chars: 512, chunk_step: 448`, `documents: 5183, chunks: 19967` |
| R2 | §7.1(b): `fs-2048-l070.chunks.trec` vs baseline `fs-512-l070.chunks.trec` | ok — same direction (2048 − 512); both from the post-Phase-A build; both at multiplier 11 (550 chunks), so the 2048 arm reaches up to 177 documents and the 512 arm ≥ 51 — that asymmetry is what the window *is*, and the rule compares the document rankings after the same 50-document collapse |
| R3 | §7.2 `LambdaSimilar` as rewritten: for each λ ∈ {0.50, 0.85, 1.00}, on each of the two FreshStack arms, "beats 0.70 on α-nDCG@10 on `.similar` with Holm p_adj < 0.05" (delta > 0 ∧ p_adj < 0.05) and "not significantly worse on the other" (¬(delta < 0 ∧ p_adj < 0.05)); largest qualifier; else 1.00 unless 1.00 is significantly worse on either arm → 0.70 | ok — total over inputs: every λ is tested by the same predicate, so "beats on both arms" qualifies (not-worse is implied), "beats on one, worse on the other" does not, and the fallback's only branch point (is 1.00 itself significantly worse on either arm) is a comparison the family produces. NaN p (zero-variance α-nDCG deltas) reads as not significant on both sides, so a no-op λ neither qualifies nor blocks 1.00 — consistent with the rule's stated rationale. `comp is None` (no overlapping queries) is unreachable: every run covers the same 672 queries |
| R4 | §7.2 Holm family: "the three λ values against 0.70 within one endpoint on one arm" | ok — `run_paired_statistics` builds `valid` per measure from the `--run` files given (`report.py:566-583`), so the §6 invocation (`--baseline fs-<w>-l070.similar.trec --run` × 3) makes `family_size` 3 for every measure, α-nDCG included, and corrects the permutation p within that measure only; the per-query α-nDCG values reach `paired_comparison` through the same `iter_calc` path as nDCG (`:420-428`), provided the nugget qrels is the `qrels` argument for that measure — which §3.3 states ("the nugget file scores α-nDCG"); that `run_paired_statistics` currently threads one `qrels` through every measure (`:556-563`) is the change the plan makes, not a design gap |
| R5 | §7.2 `LambdaChunks`: \|mean distinct parents in top-10 at 1.00 − at 0.70\| < 1.0 on both arms → 1.00, else 0.70 | ok — "changes by less than 1.0" is a symmetric absolute threshold on the sidecar's mean, computed per arm; total. Fewer than 10 hits is unreachable (550 requested from 18,622 / 64,735 chunks); the < 50 case is a listed test. **Dropped candidate:** the server fetches `topK × OverFetchFactor` = 2,200 candidates for the harness's 550-chunk request (`ObjectSearchGrpcService.cs:408,744`) while a `top_k = 10` caller's MMR runs over 40, so the sidecar's top-10 is the top of a larger MMR than most callers see. At λ = 1.00 the top-10 is pool-independent (fused scores are per-candidate weighted means, `ResultReranker.cs:36-49`, and Take(10) of a superset is the same set), so the pool only *enlarges* the 0.70-side difference: a measured \|Δ\| < 1.0 implies the same at any smaller pool, and the "0.70 stays" branch errs conservative. Neither verdict can be produced spuriously in the direction that changes behaviour; not literal-wrongness |
| R6 | §7.3: `centroid-raw.similar.trec` (run) vs `head-raw.similar.trec` (baseline), three arms; Holm p_adj on nDCG@10 and R@50 on both FreshStack arms; CI lower bounds > −0.02 on SciFact | ok — family of 1 per measure per invocation, so p_adj is the raw permutation p; direction centroid − head; both files are raw searches on the object collection (one point per document, 5,183 / 6,000 live), so no `CollapseByDocId` is needed and 50 rows per query is exact; `sidecar_path_for` resolves both to a non-existent `.meta.json` → `build: unknown`, printed not fatal |
| R7 | obsolete-key rejection: `VectorRanking:Lambda` present → throw naming the two new keys | ok — `Bind` ignores keys the type no longer declares (default `ErrorOnUnknownConfiguration = false`), which is exactly why the explicit read is needed; `VectorRanking__Lambda` binds to `VectorRanking:Lambda` through the env-var provider; over-inclusion: the only non-test setter in the repo is compose `:443`, replaced in the same change (grep over `*.cs *.json *.yml *.yaml *.py *.ts`; the `GraphAssembler.cs:261` hit is `Expression.Lambda`); under-inclusion: no appsettings or Helm file carries a `VectorRanking` section |
| R8 | multiplier per corpus: 5 / 11 | ok — `ChunkBudgetGuard.Evaluate`: 250 / 1.271 = 197 (sci-2048), 250 / 3.852 = 65 (M1), 550 / 3.104 = 177 (fs-2048), 550 / 10.789 = 50.98 (fs-512) — all ≥ 50; `Ceiling(10.789)` = 11; 250 / 10.789 = 23.2 → REFUSED, as §9 row 4 says |
| R9 | nugget-qrels filter to the slice's 672 query ids and 6,000 corpus ids | ok — real data, both directions: 0 of 270,903 corpus ids and 0 of 672 query ids appear in more than one topic; 130,387 rows → 64,539 kept; **every** rel = 1 row (8,917) survives, so the 65,848 dropped rows are all rel = 0; all 672 queries keep ≥ 1 relevant doc; every nugget of every slice query keeps ≥ 1 relevant doc in the slice (no uncoverable subtopic); no duplicate `(q, nugget, d)` rows; the files have no header row, so `sample_corpus.py`'s 4-column pass-through (`:71-79`) would keep the nugget column intact if the plan routes through it |
| R10 | query-prefix composition | ok — `embed(text, model, document_prefix, embed_url)` sends `document_prefix + text` (`ingest.py:535`); the bge instruction with trailing space is `EmbeddingPrefixes.cs:37` (the spec's `:35` is the table header — same entry); `--query-prefix` required because the contract's `embedding` block has only `documentPrefixes` / `defaultDocumentPrefix` |
| R11 | §4 / §3.1 / §9 chunk-count predictions as revised | ok — chunker re-run this round with `ingest.py`'s `split_into_chunks` and the `[c for c in chunks if c[0]]` drop: SciFact 2048/1792 → 6,587 (0 dropped); SciFact 512/448 → 19,967 (0); slice 2048/1792 → 18,626 raw / **18,622** kept; slice 512/448 → 64,763 raw / **64,735** kept; 5,183 / 6,000 documents |
| R12 | "at 1.00 the diversifier reduces to `Take(topK)` bit-exactly on both branches" | ok — `ResultDiversifier.cs:75-78`: `1.0·s − 0.0·maxSim = s` for cosine-bounded `maxSim`; `1.0·s = s`; strict `>` keeps the earlier of equal scores and `ranked` is fused-descending |
| R13 | §4 "every run file 50 × queries rows with full qrels coverage" | ok — API `.similar`: 50 of 5,183 / 6,000 entities; API `.chunks`: the guard guarantees ≥ 50 reachable documents (R8); raw arms: `limit 50` on the object collection, fail-loud below 50; `structural_check` reports coverage against the `--qrels` query set (`:224-244`) and every run carries all 672 / 300 queries |
| R14 | §3.4 eligibility: "the complete set of places the default lives" — every producer of a 512/64-token chunk window enumerated from the code, not from the spec's list | → §2.1: the Python client has a **third** default site, `iverson_chunk(max_tokens: int = 512, overlap: int = 64)` (`annotations.py:153-155`), the exported declaration helper (`__init__.py:13,40`) used bare in the conformance models (`conformance/models.py:228,257`); the change set lists `:42-43,64` only. → §3.1: the server fills `DocumentMaxTokens`/`DocumentOverlap` with `512` / `64` when a registration leaves them 0 (`SchemaBuilder.cs:161-162`), and no client emits `document_max_tokens` (`object_mapping.proto:107` has no client-side reference), so every templated-document type chunks at the same 2048/1792 window through a producer the spec never names. The remaining identifiers in the block check out: TS has one path (`IversonChunk(maxTokens = 512, overlap = 64)`, `ChunkOptions` carries only `contextual`); Go has one (`tags.go:293-294`, overridden by the tag value at `:301,307`); Java and .NET are annotation/attribute-only |
| R15 | §11 M1 build mismatch | ok — composite verified `7d3a15092f963723`; benign because Phase A at 0.70/0.70 is arithmetic-identical (R12 and S6) |

### Data-flow arrows (persistence boundaries flagged ⧉)

| Row | Arrow | Disposition |
|---|---|---|
| D1 | raw `SearchChunks` hits `(ParentKey, Score, Text)` in Diversify order → distinct-parent counts at 10 / 50 ⧉ `<label>.chunks.diversity.json` → `report.py` prints the two means → §7.2 `LambdaChunks` | ok — the counts are computed from the in-memory list before `Aggregate`; the sidecar holds the means the rule reads; `report.py` derives the sidecar path from the run file name (an addition beside `sidecar_path_for`, which strips `.chunks` and would not find it) — implementation detail the spec's contract already implies |
| D2 | topic `qrels.tsv` `(qid, nugget, cid, rel)` → filter ⧉ `qrels.nugget.trec` (4-col TREC, nugget in column 2) → `read_trec_qrels` → `Qrel(query_id, doc_id, relevance, iteration)` (`util.py:16-20,284-285`) → `QrelsConverter.as_namedtuple_iter` (tees the namedtuple iterator unchanged, `:60-63`) → `PyNdEvalProvider._map_qrel_namedtuple` `subtopic_id = record.iteration` (`pyndeval_provider.py:80-88`) → `RelevanceEvaluator(rel ≥ 1, α = 0.5)` → `iter_calc` per-query `Metric` → `per_query_values` / `paired_comparison` | ok — every parameter the α-nDCG operation needs (query id, subtopic, doc id, graded rel) exists in the persisted 4-column file; `alpha` defaults to 0.5 and `rel` to 1 (`measures/diversity.py:68-70`), matching §3.3; the "all queries have only 1 subtopic" warning cannot fire (2,131 nuggets across 672 queries); ids contain no whitespace |
| D3 | `similar_arms.py` raw named-vector searches (`body_vector`, `body_centroid`, limit 50, `with_payload: ["docId"]`) ⧉ `head-raw.similar.trec` / `centroid-raw.similar.trec` → §6 rule-7.3 invocation and the directory-wide invocation | ok — live: both named vectors at 768 dims on all 5,183 points; scrolled payload keys `['__TenantId', 'body', 'docId', 'key', 'ownerId', 'title']` so `docId` is present; `TrecRunWriter`'s six-column line is what `structural_check` and `read_trec_run` parse; the request shape is §9 row 9 |
| D4 | `VECTOR_RANKING_LAMBDA_SIMILAR` / `_CHUNKS` → `docker compose up -d --no-deps iverson-api` → `benchmark-query --config-label fs-<w>-l<λ>` ⧉ run files named by label → §6 rule-7.2 invocation pairs by file name | ok — λ persists only in the label; the protocol's `docker inspect` confirmation precedes each run; the `.meta.json` composite is identical across the sweep (same binary), so no `BUILD MISMATCH` inside a family |
| D5 | §3.4 change set → `IngestContractTests` (`ChunkMaxTokens`/`ChunkOverlap` → `chunkWindow`) ⧉ regenerated `ingest-contract.json` → `ingest.py` `MAX_CHARS`/`STEP` (`:211-212`) → `--chunk-max-chars`/`--chunk-step` defaults (`:735-744`) → invocations relying on the default window | ok — every §6 ingest passes the window explicitly; the baseline restore is a snapshot upload; `multivector.py` imports `ingest` but never calls the chunker; the golden chunking cases regenerate from the same `ChunkWindow` call and `_verify_algorithm_goldens` checks them at contract values. This arrow is unaffected by §2.1 (the Python helper's default never reaches the contract; only the .NET attribute's copy does) |
| D6 | `--chunk-budget-multiplier` → `ChunkBudgetGuard.Evaluate` (`:113`) and `TopK` (`:369`) ⧉ `.meta.json` | ok — both consumers of the const are the ones A7 names; `IntFlag` parses it; the sidecar is a `JsonObject` (`:203-222`) that takes one more key |
| D7 | §6 step 2/3, **per call site**: (i) rule-7.2 invocation `--qrels qrels.trec --nugget-qrels qrels.nugget.trec --baseline fs-<w>-l070.similar.trec --run` × 3 → compare blocks → gate document | ok — family 3 per measure (R4); nDCG/R/AP from `qrels.trec`, α-nDCG from `qrels.nugget.trec` per §3.3's routing; the structural coverage check uses `qrels.trec`'s 672 ids |
| D7 | (ii) rule-7.3 invocation `--baseline head-raw.similar.trec --run centroid-raw.similar.trec` | ok — family 1 (R6) |
| D7 | (iii) directory-wide `--run <dir>/runs` with no baseline → reported scores + diversity means | ok — `resolve_run_paths` excludes only the `--qrels` path (`:135-153`); `qrels.nugget.trec` is specified at the run-dir root (`<run>/qrels.nugget.trec`, beside `qrels.trec`, as the existing layout has it), not under `runs/`, so the sweep never hands a 4-column file to `read_trec_run`; the two raw arms score as `build: unknown` (A25) |
| D8 | §6 step 1, per call site: rule-7.1(a) invocation against M1 (cross-dir baseline, `BUILD MISMATCH` printed); rule-7.3 invocation; directory-wide | ok — `--baseline` may be any file path (`:770-772`); M1's run dir has `beir/`, `qrels.trec`, `runs/bge-base.{chunks,similar}.trec`, `bge-base.meta.json`; `scifact-run-2026-08-26` holds the `beir/` and `qrels.trec` the step copies |
| D9 | §6 step 3 window comparison: `--baseline fs-512-l070.chunks.trec --run fs-2048-l070.chunks.trec` across two run dirs | ok — run files carry corpus doc ids (key maps are per-ingest but resolved before writing), and both dirs copy the same `qrels.trec` |
| D10 | `IOptions<VectorRankingOptions>` → `ObjectSearchGrpcService` ctor | ok — registered by `Options.Create` at `ServiceCollectionExtensions.cs:80` in the same `AddVectorRanking` that registers the diversifier |
| D11 | `ingest.py` `run_stats` ⧉ `keymap.json.stats.json` (`documents`, `chunks`, `chunk_max_chars`, `chunk_step`) → §4 equality check and `benchmark-query`'s guard | ok — the sidecar's `chunks` is the post-drop count (`ingest.py:594` then the per-document increment) that R11 predicts; the guard reads `Documents`/`Chunks` case-insensitively (`BenchmarkQueryScenario.cs:50-58`) |
| D12 | Python `iverson_chunk()` → `iverson_field(chunk=True, chunk_max_tokens=max_tokens, chunk_overlap=overlap)` → `FieldMeta` → registration | → §2.1 — the helper's own parameter defaults (`annotations.py:154-155`) are what a bare `iverson_chunk()` registers; the `FieldMeta` (`:42-43`) and `iverson_field` (`:64-65`) defaults the change set edits are bypassed on this path |
| D13 | templated-document registration (`document_max_tokens` = 0 from every client) → `SchemaBuilder.cs:161-162` fills 512 / 64 → `ChunkDescriptor("Document", …)` → consumer chunks at 2048/1792 | → §3.1 — a second producer of the default window, outside the spec's enumeration |

## 1. Verified-assumptions cross-check

| # | Status |
|---|---|
| A1 | holds — `Lambda = 0.70` (`VectorRankingOptions.cs:17`); finiteness (`ServiceCollectionExtensions.cs:60-64`) before `[0,1]` (`:76-78`); `Options.Create` (`:80`) |
| A2 | holds — `_o.Lambda` read only at `ResultDiversifier.cs:77-78`; repo-wide grep for `\bLambda\b` in non-test source hits only the options class, the DI validation and those two lines |
| A3 | holds — ctor `:31-42` with `IOptions<DecayOptions>` at `:42`; `Diversify` at `:302` (SearchSimilar) and `:488` (SearchChunks) |
| A4 | holds — compose `:442-443`; `VectorRankingOptionsTests.cs:57-65,77-133`; `ResultDiversifierTests.cs:12`; no `VectorRanking` key in any `*.json`/`*.yml`/`*.yaml`/`*.tpl` outside compose |
| A5 | holds — `AddVectorRanking(this IServiceCollection, IConfiguration)` at `:55`, `config.GetSection(...)` at `:58` |
| A6/A23 | holds — no file under `Iverson.Api.Tests/` references `IResultDiversifier` or `Diversify` |
| A7 | holds — const `:41`, uses `:113`/`:369`, `IntFlag`/`StrFlag` at `Program.cs:407-419`, sidecar `JsonObject` `:203-222` |
| A8/A24 | holds — `chunks.Add((r.ParentKey, r.Score, r.ChunkText))` at `:378`, `Aggregate` at `:389` |
| A9/A21 | holds — `PyNdEvalProvider.initialize` imports `pyndeval` (`pyndeval_provider.py:73-78`); `python-libs` holds `ir_measures`, `numpy`, `scipy`, `pytrec_eval` and no `pyndeval`; no `gcc` on PATH |
| A10 | holds — `Qrel` carries `iteration: str = '0'` (`util.py:16-20`), `read_trec_qrels` fills it (`:284-285`), the provider maps it to `subtopic_id` (`:80-88`) |
| A11 | holds — recomputed: 1,228 / 848 / 2,309 / 1,224 / 391 docs; 129 / 99 / 203 / 184 / 57 queries; five `qrels.tsv` files |
| A12 | holds — as revised: 18,626 → 18,622 kept, 64,763 → 64,735 kept (row R11); minimum multiplier 11 |
| A13 | holds — 6,587 on `scifact-run-2026-08-26/beir/corpus.jsonl`, zero dropped |
| A14/A15 | holds — live: 5,183 object points, `body_vector` + `body_centroid` at 768; `docId` in the scrolled payload; 19,967 chunk points; TEI `BAAI/bge-base-en-v1.5`, `max_input_length` 512, `auto_truncate` true |
| A16 | holds — contract `embedding` block has `documentPrefixes` / `defaultDocumentPrefix` only; the bge query instruction at `EmbeddingPrefixes.cs:37` |
| A17/A22 | holds **as scoped** — "five client files" is true as files, and `IngestContractTests.cs:75-76` copies the pair by design. It does not cover the number of default *sites* within a file: `annotations.py` has three (`:42-43`, `:64-65`, `:154-155`), and the §3.4 change set names two → §2.1 |
| A18 | not re-read (grep-verified by the spec; nothing this round depends on it) |
| A19 | not re-read (file times; no rule depends on it) |
| A20 | holds — compose only; nothing under `deploy/` mentions `VectorRanking` |
| A25 | holds — `sidecar_path_for("head-raw.similar.trec")` → `head-raw.meta.json`, absent → `None` → "unknown" |

**Span check** — dependencies no listed assumption covers:

- *Every producer of a 512/64-token chunk window is in the §3.4 change set.* Not covered as stated (A17/A22 count files); false on two counts — the Python helper (→ §2.1) and the server-side template fallback (→ §3.1).
- *The nugget qrels' iteration column survives `QrelsConverter` into pyndeval, and the five topics' id spaces do not collide.* Verified in-round (rows D2, R9).
- *The rewritten §7.2 yields a verdict for every sweep outcome.* Verified in-round by case enumeration (row R3).
- *The chunk-count predictions as revised are what the sidecar records.* Verified in-round (row R11).

## 2. Literal-wrongness findings

### 2.1 The gated Python change leaves `iverson_chunk()` at 512 / 64

**Description.** §3.4's change set edits `Iverson.Clients/Python/iverson_client/annotations.py:42-43,64` — the `FieldMeta` dataclass defaults and the `iverson_field(...)` keyword defaults. The Python client's declaration helper for chunk fields is a third site the table does not name:

```python
def iverson_chunk(
    max_tokens: int = 512,      # annotations.py:154
    overlap: int = 64,          # annotations.py:155
    ...
) -> FieldMeta:
    return iverson_field(chunk=True, chunk_max_tokens=max_tokens, chunk_overlap=overlap, ...)
```

`iverson_chunk` is exported from the package (`iverson_client/__init__.py:13,40`) and is the form the client's own conformance models use bare (`Iverson.Clients/Python/conformance/models.py:228,257`: `body: str = iverson_chunk()`) — the Python spelling of a bare `[IversonChunk]`. Because it passes its own parameter defaults through to `iverson_field`, the two edited sites are bypassed on this path: after the enumerated change a Python type declared with `iverson_chunk()` still registers 512 / 64 tokens, i.e. the 2048/1792 window the FAIL verdict just rejected, while the other four clients register 128 / 16.

**Why it fails the spec's outcome.** §3.4 defines the consequence of a FAIL as the client default becoming "the 512/448 window every gate measured" across the five clients, and asserts the table is "the complete set of places the default lives". On the Python client's primary declaration path it is not, so the gated change would ship with one client — and its conformance fixtures — silently on the failed window.

**Evidence.** `Iverson.Clients/Python/iverson_client/annotations.py:153-160`; `iverson_client/__init__.py:13,40`; `conformance/models.py:228,257`; spec §3.4 table row for Python (`:42-43,64`).

**Proposed fix.** Add `annotations.py:154-155` (`max_tokens = 128`, `overlap = 16`) to the §3.4 table beside `:42-43,64-65`, and amend A17/A22 to count sites, not files ("three sites in `annotations.py`"), so the plan's completeness check inherits the right unit.

## 3. Forced decisions

### 3.1 Whether the server's templated-document window fallback changes with the client default

**The choice.** When §7.1 says FAIL, does `SchemaBuilder.cs:161-162` — which fills `DocumentMaxTokens` / `DocumentOverlap` with `512` / `64` for any registration that leaves them 0 — change to `128` / `16` alongside the five clients, or stay at 512 / 64?

**Why it is forced.** The spec's premise (§1) is that the 2048/1792 window is "the default every fresh deployment gets" and its gated consequence is defined as removing that default. The codebase has a second producer of the same window that the §3.4 enumeration does not name: `SchemaBuilder` derives a `ChunkDescriptor("Document", maxTokens, overlap, …)` for every type with a `DocumentTemplate`, and because no client emits `document_max_tokens` (`Iverson.Clients/Common/Proto/object_mapping.proto:107` has no client-side reference; the code comment at `SchemaBuilder.cs:158` says so), the server-side fallback *is* the effective default for every templated-document type. Executing §3.4 as written therefore produces a deployment in which `[IversonChunk]` fields chunk at 512/448 characters and templated documents at 2048/1792 — a mixed state the spec neither chose nor rejected, and one the plan cannot execute around: it either edits those two lines or it does not. The window rule's evidence (FreshStack at both windows) says nothing specific to templated documents, so this is a product choice, not one the measurement settles.

**The options** (reviewer does not pick):

- (a) Include `SchemaBuilder.cs:161-162` in the §3.4 change set, so one FAIL verdict moves every server-side default to the measured window; add it to A17/A22's enumeration and to the §8 gated tests.
- (b) Leave the template fallback at 512 / 64 and say so in §3.4 and the gate document, on the ground that the templated-document window was never in this spec's scope and carries its own (unmeasured) evidence question.
- (c) Make the template window follow the client's declared value by having clients emit `document_max_tokens` / `document_overlap` from their (now 128 / 16) defaults, so the server fallback stops being a default at all — a larger change than this spec sized for, listed only because it is the third real state of the code.

## 4. Previously addressed

- CDR-1 §2.1 (chunk-count check rejected a correct FreshStack ingest) — resolved: §3.1, §4, §6 and §9 row 3 now carry the post-drop figures 18,622 / 64,735, which reproduce exactly with `ingest.py`'s chunker and the `[c for c in chunks if c[0]]` rule (row R11).
- CDR-1 §2.2 (the `report.py` invocations never produced the §7.2 / §7.3 comparisons) — resolved: §6 now names three invocations per arm with explicit `--baseline`/`--run` files, and §7.2 states the Holm family (three λ values against 0.70 within one endpoint on one arm); `run_paired_statistics` builds exactly that family from those arguments (rows R4, D7, D8).
- CDR-1 §3.1 (the λ rule had no verdict for two reachable outcomes) — resolved by Ben's choice of options (i)(b) and (ii)(b): every λ is tested by one qualify predicate, the largest qualifier wins, and the fallback to 1.00 is guarded only by 1.00's own comparison against 0.70; the rule is total (row R3).

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty (one item); §2 has one literal-wrongness finding to address alongside it.
