# Critical Design Review: 2026-09-06-tier1-retrieval-defaults-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-06-tier1-retrieval-defaults-design.md` (at `92becdc`)
**Verified Assumptions section:** present

## 0. Coverage enumeration

Built before reading rounds 1–2. Live-box reads were read-only (Qdrant REST collection info + one scrolled point; TEI `/info`).

### Sections

| Row | Section | Disposition |
|---|---|---|
| S1 | Header / Depends-on | ok — the slice dir, `beir/`, the five `freshstack-5topic/<topic>/freshstack/qrels.tsv` files, and both SciFact run dirs exist; TEI `/info` reports `BAAI/bge-base-en-v1.5`, `max_input_length` 512, `auto_truncate` true |
| S2 | §1 Why | ok — the three claims re-read against their sources: `centroid-weighting-proposal.md:243,328` (MMR *on* costs 9 % / 12.8 % of R@50 — the `92becdc` sign now matches the source); `2026-09-GATE-embedding-migration.md:459` (+0.0794 nDCG@10 on `.similar`); `SchemaBuilder`/attribute defaults 512/64 → 2048/1792 chars (contract `chunkWindow`) |
| S3 | §2 Scope / §2.1 decisions | ok — every "In" item has a §3 row and a §5 row; the "Out" items are not referenced by any rule; Helm has no `VectorRanking` key (`deploy/` grep empty) |
| S4 | §3.1 arms and runs | ok — counts match §9 rows 3, 7, 10; multiplier arithmetic 550/10.79 = 50.97 ≥ 50 passes `ChunkBudgetGuard.Evaluate` (`ChunkBudgetGuard.cs:31-40`); ETA source is the migration spec's 720 ms/text (`2026-09-04-embedding-migration-design.md:180`) |
| S5 | §3.2 Phase A | ok — see rules R6–R8 and arrows D1–D4; `:302` is inside `SearchSimilar` (log line `:192`), `:488` inside `SearchChunks` (`:373`) |
| S6 | §3.3 harness changes | ok — see R9–R12, D5–D9 |
| S7 | §3.4 gated change set | → §2.1 (a Go client test pins the 512/64 default and is not in the set); the rest of the surface checked in R13–R15 |
| S8 | §4 fail-loud | ok — the counts are the post-drop figures (`ingest.py:594` filter, `:658` count); `similar_arms.py` semantics are the spec's own contract; the obsolete-key throw is R7 |
| S9 | §5 component contract | ok — every row maps to a §3 item; `ObjectSearchGrpcService.cs:40-42, 302, 488` and compose `:442-443` re-read at those lines |
| S10 | §6 protocol | ok — three `report.py` invocations per arm with explicit files; `resolve_run_paths` (`report.py:117-155`) accepts repeatable file paths and excludes only `--qrels` from a directory sweep — `qrels.nugget.trec` is placed at the run-dir root, not under `runs/`, so the directory-wide invocation cannot sweep it as a run; `qrels.trec` in `scifact-bge-base-2026-09-04` and `scifact-run-2026-08-26` are byte-identical (md5 `f7572e6a…`), so 7.1(a) scores both runs against one qrels |
| S11 | §7.1 window rule | ok — R1, R2 |
| S12 | §7.2 λ rule | ok — R3, R4 |
| S13 | §7.3 representation rule | ok — R5 |
| S14 | §8 testing | ok as far as it reaches — `Iverson.LoadTest.Tests/Benchmark/{ChunkBudgetGuardTests,CommandFlagsTests}.cs` exist for the flag/guard bullets; the gated bullet's `SchemaBuilder` test is the existing `SchemaBuilderTests.cs:619-639` (asserts 512/64 today) retargeted — named in §2.1's fix; the Go client's own default test is the gap (§2.1) |
| S15 | §9 measurements | ok — ground truth; rows 2, 3, 7, 8 fresh-read (see §1) |
| S16 | §10 verified assumptions | → §1 |
| S17 | §11 known issues | ok — BUILD MISMATCH is a printed banner, not an exit (`report.py:492-497`); the registration-guard statement is consistent with §3.4's "already-registered types keep their window" |

### Rules and operands (both failure directions)

| Row | Rule | Disposition |
|---|---|---|
| R1 | §7.1(a) `sci-2048` vs `bge-base` (M1): `--baseline M1 --run sci-2048` → delta = 2048 − 512-char; CI lower bound > −0.02 on nDCG@10 AND R@50 | ok — direction: a worse 2048 window fails (lower bound ≤ −0.02 on either measure); a non-inferior one passes; same qrels file for both (S10); family of 1 so Holm is moot |
| R2 | §7.1(b) `fs-2048-l070` vs `fs-512-l070`, same multiplier 11, same λ, same build | ok — both arms at 550-chunk top-K so the only difference is the window; both directions as R1; "default stands only if both pass" makes either FAIL sufficient |
| R3 | §7.2 `LambdaSimilar`: qualify = beats 0.70 on α-nDCG@10 `.similar` with Holm p_adj < 0.05 on ≥ 1 arm AND not significantly worse on the other; largest qualifier; else 1.00 unless 1.00 itself significantly worse than 0.70 on either arm → 0.70 | ok — enumerated: better-on-both / better-on-one-neutral-other → qualifies; better-on-one-worse-on-other → does not; none qualifies and 1.00 neutral → 1.00; none qualifies and 1.00 worse anywhere → 0.70. Total. The family is the three `.similar` λ files against `l070.similar` per arm — exactly what the §6 invocation passes (`run_paired_statistics` families over the `--run` list per measure, `report.py:566-585`) |
| R4 | §7.2 `LambdaChunks`: 1.00 iff \|mean distinct parents@10 (λ 1.00) − (λ 0.70)\| < 1.0 on both arms | ok — inputs are `fs-<w>-l100.chunks.diversity.json` and `fs-<w>-l070.chunks.diversity.json`, one per labelled run (D6); both directions: a ≥ 1.0 change on either arm keeps 0.70. Candidate dropped: the sidecar's top-10 is taken from a 550-chunk request whose MMR pool is `topK × 4` (`ObjectSearchGrpcService.cs:408, :747`), not the pool a `top_k = 10` caller gets — but the rule decides on the quantity it names, which is well-defined and computed as stated; the "caller-visible" framing is not a rule operand |
| R5 | §7.3 `centroid-raw` vs `head-raw`, family of 1 per arm; Holm p_adj < 0.05 on both nDCG@10 and R@50 on both FreshStack arms AND CI lower bounds > −0.02 on SciFact | ok — each condition has a fail direction (not significant, wrong sign, SciFact bound crossed) that yields "recorded and closed"; raw runs have no sidecar → "BUILD UNKNOWN" printed, not fatal (`report.py:492-493`) |
| R6 | Validation of `LambdaSimilar` / `LambdaChunks`: finiteness first, then [0,1] | ok — mirrors `ServiceCollectionExtensions.cs:60-64, 76-78`; NaN/±∞ caught before the range comparison that they would pass; 0 and 1 inclusive by `is < 0 or > 1` |
| R7 | Obsolete-key rejection: `VectorRanking:Lambda` present → throw naming both new keys; env form `VectorRanking__Lambda` | ok — `config.GetSection("VectorRanking")["Lambda"]` matches only the exact child key, so `LambdaSimilar`/`LambdaChunks` cannot false-trigger (over-inclusion); the env provider maps `__` to `:` so `VectorRanking__Lambda=0.70` (today's compose line) is caught (under-inclusion); the compose change removes the only in-repo producer (`docker-compose.yml:443`), Helm has none |
| R8 | λ = 1.00 reduces to `Take(topK)` bit-exactly on both branches | ok — `1.0 * Score − (1 − 1.0) * maxSim` with `maxSim` always finite (NaN never stored, `ResultDiversifier.cs:103-108`) is `Score − 0.0 = Score`; the absent branch is `1.0 * Score` |
| R9 | Multiplier per corpus: 5 on both SciFact arms, 11 on both FreshStack arms | ok — 6,587/5,183 = 1.27 and 19,967/5,183 = 3.85 both pass at 5; 18,622/6,000 = 3.10 and 64,735/6,000 = 10.79 both pass at 11; the flag reaches the guard (`BenchmarkQueryScenario.cs:113`) and the chunks request (`:369`) via the one constant it replaces |
| R10 | Nugget-qrels filter: rows with query ∈ 672 slice ids AND corpus ∈ 6,000 slice ids | ok — run over the five `qrels.tsv` files this round: 130,387 rows → 64,539 kept, **672 of 672 queries covered**, 20,209 distinct (query, doc) pairs, max 8 nuggets/query; 0 rows with a slice corpus id but a non-slice query id; the query-level `qrels.trec`'s 5,445 rel > 0 pairs are a subset of the kept nugget pairs (0 missing). Column shape `query \t nugget \t corpus_id \t rel` confirmed on `angular/freshstack/qrels.tsv` |
| R11 | `--query-prefix` required; composition `query_prefix + text` | ok — contract `embedding` block has `documentPrefixes` / `defaultDocumentPrefix` only; the bge instruction string is the `Table` entry at `EmbeddingPrefixes.cs:37` (the spec's `:35` is the comment line above it — same entry, dropped as a citation nit); `ingest.embed(text, model, document_prefix, embed_url)` prepends its prefix argument (`ingest.py:535`), so the script can pass the query prefix through that parameter |
| R12 | Chunk-count checks 6,587 / 18,622 / 64,735; 5,183 / 6,000 | ok — post-drop figures per §9 row 3 (ground truth) and the `[c for c in chunks if c[0]]` filter at `ingest.py:594` |
| R13 | §3.4 change set — source sites | ok — `IversonChunkAttribute.cs:10`, `annotations.ts:166`, `tags.go:293-294` (+ doc comments `:168-171`), `IversonChunk.java:18,21`, `annotations.py:42-43, 64-65, 154-155`, `SchemaBuilder.cs:161-162`, `IngestContractTests.cs:75` (+ `:76`) all re-read at those lines with 512/64; the client registrars (`core.py`, `core.ts`, `SchemaRegistrar.cs`, `registrar.go`, `SchemaRegistrar.java`) carry no literal 512 of their own; `Article.cs:14` is an explicit pin; `ingest.py:211-213` reads `MAX_CHARS`/`STEP` from the contract at import, so the regenerated contract is the script's default as claimed |
| R14 | §3.4 change set — conformance fixtures move together | ok — the bare chunk declarations are `S11`/`S12` in all five languages (`S11ModelDotnet.cs:32`, `S12ModelDotnet.cs:31`, `S11ModelJava.java:43`, `S12InheritedJava.java:30`, TS `models.ts:316,350`, Go `models.go:204,231`, Python `models.py:228,257`); `VectorDoc` pins 256/32 explicitly in all five. No language pins 512/64 explicitly on a type another language declares bare, so the five drivers keep registering equal windows after the change (the registration guard cannot bite) |
| R15 | §3.4 change set — tests that pin the default | → §2.1 — `Iverson.Clients/Go/iverson_test/tags_test.go:188-207` (`TestInspectType_Chunk_Defaults`) asserts `ChunkMaxTokens == 512` and `ChunkOverlap == 64` on a bare `iverson_chunk:"true"` field; not in §3.4 or §8. The other clients' registrar tests assert only explicit 256/32 (`schema-registrar.test.ts:228`, `SchemaRegistrarTest.java:465`, `test_schema_registrar.py:321-322,636-637`, `registrar_test.go:229-230,531-534`); DotNet client tests carry no window assertion. Server tests with 512/64 (`SchemaRegistrationOrchestratorTests.cs`, `DocumentTemplateValidationTests.cs`) build descriptors with explicit values and are unaffected; `SchemaBuilderTests.cs:619-639` asserts the fallback and is the test §8's gated bullet retargets |
| R16 | SchemaBuilder fallback × client-emitted `DocumentMaxTokens` ("every producer moves together") | ok — no client source references `document_max_tokens` / `DocumentMaxTokens` / `documentMaxTokens` in any form, and no client references `DocumentTemplate` at all (repo grep: only `object_mapping.proto:107-108` and `SchemaBuilder.cs:161-162`); Go/TS/Java have no templated-document path. The fallback is therefore reachable only through a hand-built `TypeDescriptor`, and after the change every reachable producer of a default window yields 128/16 |

### Data-flow arrows (P = crosses a persistence boundary)

| Row | Arrow → operation | Disposition |
|---|---|---|
| D1 | compose env `VectorRanking__LambdaSimilar/Chunks` → `AddVectorRanking(IConfiguration)` bind + validate | ok — `config.GetSection("VectorRanking").Bind(opts)` (`:58`) binds by property name; the old key is checked on the same section (R7) |
| D2 | `IOptions<VectorRankingOptions>` → `ObjectSearchGrpcService` ctor (beside `IOptions<DecayOptions>` `:42`) → `Diversify(candidates, topK, LambdaSimilar)` at `:302` | ok — the operation's third parameter is sourced from the injected options; the three Api tests that construct the service positionally (`ObjectSearchGrpcServiceTests.cs:74, 2612`, `DocumentTemplateValidationTests.cs:332`) are an implementation-plan edit, not a design gap |
| D3 | same → `Diversify(…, LambdaChunks)` at `:488` | ok — second call site, own λ; §8's cross-endpoint wiring tests make a hard-coded value at either site fail exactly one test |
| D4 | `ResultDiversifier.Diversify(ranked, topK, lambda)` → MMR expression | ok — λ replaces `_o.Lambda` at `:77-78`, its only two reads; the DI registration `AddSingleton<IResultDiversifier, ResultDiversifier>()` needs no options once the ctor is parameterless |
| D5 (P) | `--chunk-budget-multiplier` → `ChunkBudgetGuard.Evaluate(documents, chunks, 50, m)` ← `keymap.json.stats.json` | ok — `documents`/`chunks` are the sidecar keys `ingest.py:663` writes and the scenario reads (`:108-113`); the value is also written to `<label>.meta.json`, which `report.py` reads only for `composite` (`load_build_composite`), so the extra key is inert there |
| D6 (P) | raw `SearchChunks` hits (`chunks.Add((r.ParentKey, r.Score, r.ChunkText))`, `:378`) → distinct-`parent_key` counts @10/@50 → `<label>.chunks.diversity.json` → `report.py` means | ok — stream order is the diversifier's order (`:488` foreach writes in `Diversify` order), so "first 10 raw hits" is MMR's first 10; `ParentKey` is present on every hit (`parent_id ?? ""`); one sidecar per label, so each λ run has its own |
| D7 (P) | `benchmark-query` → `<label>.{chunks,similar}.trec` → `report.py` structural check / `score_run` / `run_paired_statistics` | ok — 6-column TREC via `TrecRunWriter`; coverage checked against the `--qrels` query set (672 both files); `sidecar_path_for` resolves both suffixes to `<label>.meta.json` (`:158-172`) |
| D8 (P) | nugget filter → `qrels.nugget.trec` (4-column, nugget in the iteration column) → `ir_measures.read_trec_qrels` → pyndeval provider `alpha_nDCG@10` → per-query values for the paired block | ok — `read_trec_qrels` keeps `iteration` (A10); the provider maps it to the subtopic id and its `iter_calc` yields per-query `Metric`s (`pyndeval_provider.py:118-131`), which `per_query_values`/`paired_comparison` consume; nugget ids like `76185522_0` are opaque strings. The per-measure qrels dispatch (nugget for α-nDCG, query-level for the rest) is a `report.py` change the spec's "measure-generic" sentence understates — dropped: implementation-level, the design names which file scores which measure |
| D9 (P) | `similar_arms.py`: `beir/queries.jsonl` → `embed(prefix + text)` → raw named-vector search `{vector: {name, vector}}` on the object collection, limit 50, `with_payload ["docId"]` → `<arm>.similar.trec` | ok — §9 rows 7–9 ground truth, fresh-read live: object collection `benchmark_documents_tenant_bypass` has 5,183 points with `body_vector` and `body_centroid` (768, Cosine) and the scrolled payload keys are `__TenantId, body, docId, key, ownerId, title`; `docId` is the TREC doc id the qrels use |
| D10 (P) | `ingest.py --chunk-max-chars/--chunk-step` → chunk points + `stats.json` counts → §4 count check | ok — `ingest_document` receives the run's window explicitly (`:586-587`), filters whitespace-only windows (`:594`), counts what it wrote |
| D11 (P) | `IngestContractTests` regenerate → `ingest-contract.json` `chunkWindow` → `ingest.py` `MAX_CHARS`/`STEP` | ok — emit computes 128×4 = 512 and (128−16)×4 = 448; the script binds the contract at import (`:145-146, :211-213`) |
| D12 | §6 step 1 `--baseline …/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` (a different run dir) → paired block | ok — `run_paired_statistics` excludes the baseline by absolute path and pairs over the query-id intersection (672 = 300 SciFact test queries both sides, same qrels md5); the sidecar `bge-base.meta.json` exists, so the BUILD MISMATCH banner (§11) prints and nothing exits |

**Totals:** 45 rows — 43 ok, 2 → findings (S7 and R15 are the same finding), 0 dropped (three candidates were dropped inside R4, R11 and D8 and are recorded there).

## 1. Verified-assumptions cross-check

| # | Status |
|---|---|
| A1 | holds — `Lambda = 0.70` at `VectorRankingOptions.cs:17`; finiteness `:60-64` before `[0,1]` `:76-78`; `Options.Create` `:80` |
| A2 | holds — `_o.Lambda` read only at `ResultDiversifier.cs:77-78`; repo grep for `Lambda` in non-test source hits only the options class, the DI validation and those two lines |
| A3 | holds — ctor `:31-42`, `IOptions<DecayOptions>` at `:42`; `Diversify` at `:302` (SearchSimilar) and `:488` (SearchChunks) |
| A4 | holds — compose `:442-443`; `VectorRankingOptionsTests.cs:57-65` plus the `("Lambda", "0.70")` configuration rows at `:77, :91, :119, :133` (all in the same file A4 names); `ResultDiversifierTests.cs`; `deploy/` has no `VectorRanking`/`Lambda` |
| A5 | holds — `AddVectorRanking(this IServiceCollection, IConfiguration)` `:55`, `GetSection` `:58` |
| A6/A23 | holds — no Api test references `IResultDiversifier` or `Diversify` (grep) |
| A7 | holds — const `:41`, uses `:113`/`:369`; `IntFlag`/`StrFlag` pattern `Program.cs:407-419`; sidecar `JsonObject` `:203-222` |
| A8/A24 | holds — `:378` collects `ParentKey` before `Aggregate` at `:389` |
| A9/A21 | holds — §9 row 6; `python-libs` has no `pyndeval` (not re-attempted) |
| A10 | holds — provider maps `iteration` → subtopic and yields per-query metrics (`pyndeval_provider.py:118-131`) |
| A11 | holds — recomputed: 672 queries and 6,000 corpus ids in the slice; five `qrels.tsv` files present |
| A12 | holds — as revised (18,622 / 64,735 kept); minimum multiplier 11 |
| A13 | holds — §9 row 10 |
| A14/A15 | holds — live this round: 5,183 / 19,967 points, `body_vector` + `body_centroid` at 768, `docId` in the scrolled payload; TEI on bge-base at 512 / auto-truncate |
| A16 | holds — contract has `documentPrefixes` / `defaultDocumentPrefix` only; the bge query instruction is the `Table` entry at `EmbeddingPrefixes.cs:37` (the cited `:35` is its comment line — same entry) |
| A17/A22 | holds **as scoped** — every source site listed carries 512/64 today, `annotations.py` has exactly the three sites named, `SchemaBuilder.cs:161-162` is the fallback, and `IngestContractTests.cs:75-76` copies the pair. The assumption enumerates source sites plus the one test that copies the default by design; it does not cover other tests that *assert* the default — see the span check |
| A18 | holds — the client standard does not state the default; the conformance fixtures that declare a chunk field bare do so in all five languages (row R14), so conformance pins parity, not the value |
| A19 | not re-read (file times; no rule depends on it) |
| A20 | holds — compose only; `deploy/` grep empty |
| A25 | holds — `sidecar_path_for("head-raw.similar.trec")` → `head-raw.meta.json`, absent → `None` → "unknown" |

**Span check** — dependencies no listed assumption covers:

- *Every test that pins the 512/64 default is in the gated change set.* Not covered (A17/A22 count source sites and the one by-design copy); false — `tags_test.go:188-207` → §2.1.
- *The nugget filter covers every one of the 672 slice queries, so the α-nDCG structural coverage check passes.* Not covered; verified in-round (row R10: 672 of 672).
- *The five conformance drivers keep registering equal chunk windows after the gated change.* Not covered; verified in-round (row R14).
- *The pyndeval provider yields per-query values, which the paired block needs.* Not covered; verified in-round (`pyndeval_provider.py:118-131`).
- *`ingest.py`'s default window is the contract's, not a constant checked against it.* Not covered; verified in-round (`ingest.py:211-213`).

## 2. Literal-wrongness findings

### 2.1 The gated change set leaves the Go client's own default test asserting 512 / 64

**Description.** §3.4 presents its table as "the complete set of places the default lives" and, by including `IngestContractTests.cs:75` and (in §8) the Python client tests and the `SchemaBuilder` test, defines the set to include the tests that pin the default. The Go client has such a test that neither §3.4 nor §8 names:

```go
// Iverson.Clients/Go/iverson_test/tags_test.go:188-207
func TestInspectType_Chunk_Defaults(t *testing.T) {
    meta, err := iverson.InspectType(chunkDefaultsFixture{})   // Summary string `iverson_chunk:"true"`
    …
    if fm.ChunkMaxTokens != 512 {
        t.Errorf("expected default ChunkMaxTokens=512, got %d", fm.ChunkMaxTokens)
    }
    if fm.ChunkOverlap != 64 {
        t.Errorf("expected default ChunkOverlap=64, got %d", fm.ChunkOverlap)
    }
}
```

Executing §3.4 as enumerated changes `tags.go:293-294` to 128 / 16 and leaves this test expecting 512 / 64, so the Go client's suite goes red on the change the spec says is complete. No other client has an equivalent: the TypeScript, Java and Python registrar tests assert only explicit 256 / 32 windows, and the DotNet client tests carry no window assertion (row R15).

**Why it fails the spec's outcome.** The asked-for consequence of a FAIL is "one FAIL verdict moves every producer of the default window together" with a change set the plan can execute without discovery. A red Go suite is either a broken merge or an un-enumerated edit — the same class of gap as CDR-2 §2.1, one language over.

**Evidence.** `Iverson.Clients/Go/iverson_test/tags_test.go:183-207`; spec §3.4 table and §8's gated bullet (which names the Python client's tests, `IngestContractTests` and the `SchemaBuilder` test but not this one).

**Proposed fix.** Add a row to the §3.4 table: `Iverson.Clients/Go/iverson_test/tags_test.go:202-206` (`TestInspectType_Chunk_Defaults`) → `128` / `16`; extend §8's gated bullet to "the Go client's default test" beside the Python one; and, since the same bullet's `SchemaBuilder` test is the existing `SchemaBuilderTests.cs:619-639` (`…UnsetTokenFields_DefaultToFallbackValues`, asserting 512 / 64 today), name that file:line there too so the plan retargets rather than duplicates it. Amend A17/A22's unit once more: sites in source, plus every test that asserts the default (three: `IngestContractTests`, `SchemaBuilderTests`, `tags_test.go`).

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- CDR-2 §2.1 (the Python `iverson_chunk()` helper's own defaults bypassed the two edited sites) — resolved: §3.4 names `annotations.py:42-43,64-65` and `:154-155` as three sites, and A17/A22 now counts sites rather than files.
- CDR-2 §3.1 (whether the `SchemaBuilder` templated-document fallback moves with the client default) — resolved by Ben's choice of option (a): `SchemaBuilder.cs:161-162` is in the §3.4 table, A17/A22 lists it, and §8 carries the gated `SchemaBuilder` test.
- CDR-1 §2.1, §2.2 and §3.1 — remain resolved as CDR-2 §4 recorded; re-read this round: post-drop counts (rows R12, D10), per-rule `report.py` invocations with explicit families (rows S10, R3, D12), and a total λ rule (row R3).

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding (the Go client's default test is missing from the gated change set); §3 is empty.
