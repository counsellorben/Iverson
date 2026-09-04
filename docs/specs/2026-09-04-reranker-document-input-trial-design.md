# Cross-encoder reranking with full-document input — trial design

Status: design, verified against the codebase 2026-09-04.
Parent spec: `docs/specs/2026-09-03-reranker-design.md` (closed; its §3.4 gate failed, verdict in
`docs/plans/2026-09-GATE-reranker-phase1.md`). Source proposal: `docs/2026-08-29-reranker-design-parameters.md`, P6.

## 1. Why

Phase 1 rescored each of the 50 max-passage documents through its **winning chunk**: a ≈500-character
fragment of the abstract. Every SciFact abstract's `text` begins with its title (5,183 of 5,183), so only
`chunk_index` 0 carries the title and the cross-encoder saw most documents as title-less fragments. P6
names input format the highest-variance parameter in the system, and Phase 1 varied it not at all. The
published BEIR MiniLM-L-6-v2 figure on SciFact (≈0.69 nDCG@10) was produced on **title + full abstract**,
truncated at 512 tokens — the input this trial reproduces.

This trial asks one question: does giving ms-marco MiniLM-L-6-v2 the whole abstract, as published,
change the Phase 1 verdict? A pass reopens the parent spec's Phase 2 decision for Ben; it builds nothing.

## 2. Scope

- One new run on SciFact `chunks`: **A4**, ms-marco MiniLM-L-6-v2, λ = 0.70, current chunked-512
  collection, `--rerank-input document`.
- One same-session A0 control re-run, asserted identical on columns 1–5 to the preserved `rerank-a0.chunks.trec`
  and `.similar.trec` (the sixth column is the run tag).
- A1 (Phase 1, winning-chunk input) is **reused**, not re-run.
- Harness-only. No server change, no NFCorpus, no bge-reranker-base, no second input variant, no λ
  recording in the sidecar.

## 3. Design

### 3.1 Flag

`benchmark-query` gains `--rerank-input <winning-chunk|document>`, default `winning-chunk`, parsed into
`CommandFlags` (`Program.cs:385-414`, `StrFlag`) beside `--rerank-url` / `--rerank-model`, with a help
line beside theirs (`Program.cs:262-266`). Validation sits with the existing model-mismatch guard
(`BenchmarkQueryScenario.cs:81`): a value other than the two literals is refused; `document` without
`--rerank-url` is refused — an input mode for a reranker that is not there is a mislabelled arm.

### 3.2 Corpus text

In `document` mode the scenario opens `<corpus-path>/beir/corpus.jsonl` once, parses it with the
existing `JsonlCorpusParser.ParseCorpus(TextReader)` (`JsonlCorpusParser.cs:13`, which already refuses
whitespace-only `text`), and keeps `Dictionary<string, string>` doc id → `text`. The parser's `text`
field already begins with the title for every SciFact document, so nothing is prepended. The map is
loaded after the key map and before the first query; its size is logged with the key-map line.

### 3.3 Rescore input

`RunChunksAsync` (`BenchmarkQueryScenario.cs:336-382`) is unchanged up to and including max-passage
selection: `MaxPassageAggregator.Aggregate` still yields `WinningChunkAggregation.Ranked` as
`(DocId, Score, Text)` where `DocId` is the key-map-resolved corpus id (`MaxPassageAggregator.cs:59-77`).
The texts handed to `TeiRerankClient.ScoreAsync` come from a new pure function:

```
static IReadOnlyList<string> RerankInputs.Select(
    RerankInput mode,
    IReadOnlyList<(string DocId, double Score, string Text)> winners,
    IReadOnlyDictionary<string, string>? corpusText)
```

- `WinningChunk` → `winners[i].Text` in order (today's behaviour).
- `Document` → `corpusText[winners[i].DocId]` in order; a missing doc id throws
  `InvalidOperationException` naming the doc id.

The existing empty-text guard (`:373`) runs on the selected inputs, not on the chunk texts, so it
covers both modes with no second guard. Everything after `ScoreAsync` — re-sort through
`DocumentRanking.CollapseByDocId`, positional `TrecRunWriter` — is unchanged.

### 3.4 Attribution

The startup banner (`:151`) prints the input mode beside the model id. The sidecar's `reranker`
object (`:198-204`) gains `"input": "winning-chunk" | "document"`. `report.py` reads only the
sidecar's `composite` key (`report.py:193`), so the extra field breaks nothing.

## 4. Failure semantics — fail loud (parent spec §6)

- Corpus file missing or unparsable in `document` mode: the run aborts at startup.
- A winner whose doc id is absent from the corpus map: throws before any scoring, naming query and doc id.
- Empty input text: unreachable in `document` mode (the parser refuses it); the existing guard still runs.
- Inputs over the model window: TEI `--auto-truncate` (A1 sidecar: `max_input_length` 512,
  `auto_truncate` true) truncates the pair, which is also how the published figure was produced. On this
  corpus that reaches **10.9 % of abstracts for a median 19-token query and 17.1 % for the longest
  62-token query** (§7). The spec records this; it adds no policy.
- Reranker timeouts, count/index mismatches: the existing `TeiRerankClient` guards, unchanged.

## 5. Evaluation protocol

Run directory `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26`, `$RUN`. Query tier up,
`iverson-api` at `VectorRanking__Lambda=0.70`, Qdrant holding 5,183 documents / 19,967 chunks.

1. **Control.** `benchmark-query --config-label rerank-a0-2026-09-04` with no reranker. `TrecRunWriter`
   writes the config label as the run tag on every row (`TrecRunWriter.cs:29-30`), so whole-file
   comparison fails by construction; compare columns 1–5, as Phase 1 did, on both files:
   ```
   diff <(cut -d' ' -f1-5 $RUN/runs/rerank-a0-2026-09-04.chunks.trec)  <(cut -d' ' -f1-5 $RUN/runs/rerank-a0.chunks.trec)
   diff <(cut -d' ' -f1-5 $RUN/runs/rerank-a0-2026-09-04.similar.trec) <(cut -d' ' -f1-5 $RUN/runs/rerank-a0.similar.trec)
   ```
   Both must be empty. A difference means the index or server drifted since Phase 1 and A1 may not be
   reused; stop and report.
2. **Reranker.** `docker compose --profile reranker up -d reranker` (default model id is ms-marco;
   `/info` must report `cross-encoder/ms-marco-MiniLM-L-6-v2`, `max_input_length` 512, `auto_truncate` true).
3. **A4.** `benchmark-query --config-label rerank-a4 --rerank-url http://127.0.0.1:8090 --rerank-model
   cross-encoder/ms-marco-MiniLM-L-6-v2 --rerank-input document`. Confirm the banner and
   `runs/rerank-a4.meta.json` say `document`.
4. **Statistics.** Two invocations, one per pair, so the gate number cannot be lost to the format
   result: `check_pool` runs over every declared pair before any statistic and exits the whole
   invocation on failure, and its ≥ 25 %-reordered rule is a detector for a reranker that did not run —
   applied to A4 vs A1, where both arms ran one, it would measure the format effect itself.
   ```
   PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs python3 \
     Iverson.Server/Iverson.LoadTest/scripts/report.py --qrels $RUN/qrels.trec \
     --run $RUN/runs/rerank-a0.chunks.trec --run $RUN/runs/rerank-a4.chunks.trec \
     --pair $RUN/runs/rerank-a4.chunks.trec=$RUN/runs/rerank-a0.chunks.trec
   PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs python3 \
     Iverson.Server/Iverson.LoadTest/scripts/report.py --qrels $RUN/qrels.trec \
     --run $RUN/runs/rerank-a1.chunks.trec --run $RUN/runs/rerank-a4.chunks.trec \
     --pair $RUN/runs/rerank-a4.chunks.trec=$RUN/runs/rerank-a1.chunks.trec
   ```
   Holm m = 1 in each (a single declared pair, so the adjusted p equals the raw permutation p). The
   gate pair must pass `check_pool` (identical per-query doc-id sets; ≥ 25 % sequences differ). An
   `ARM INVALID` exit on the A4 vs A1 invocation's ≥ 25 % half is a format finding (the input change
   barely moved the ordering), not an invalid arm.
5. **Verdict.** Parent spec §3.4 applied to **A4 vs A0**: positive nDCG@10 delta with p < 0.05 on
   both paired *t* and sign-flip permutation (Holm at m = 1 leaves the permutation p unchanged).
   **A4 vs A1** is the format question and is reported, not gated. Either outcome is appended to `docs/plans/2026-09-GATE-reranker-phase1.md` and to
   the parent spec's closing note under §3.4.

**Expected wall time.** A0 ≈ 11 min. A batch of 8 abstracts costs 0.79 s idle against 0.24 s for 8
chunks (3.3×); A1's reranking share was ≈ 62 min, so A4 ≈ 3.5 h. This exceeds Authentik's 2 h
`access_token_validity`; the flow-executor CSRF fix (`cf9cbb8`, on `main`) is what lets the run survive
the re-mint. Score-collapse and timeout checks from Phase 1 apply unchanged.

## 6. Testing

- `RerankInputs.Select`: `WinningChunk` returns the chunk texts in ranked order; `Document` returns the
  corpus texts in the same order; `Document` with a missing doc id throws with the doc id in the message.
- `CommandFlags.Parse`: `--rerank-input document` parses; an unknown value is refused at the scenario
  guard; `document` without `--rerank-url` is refused. (One test each; the guard test is the same shape
  as the existing model-without-url refusal.)
- The existing 43 `Iverson.LoadTest.Tests` and 6 `test_report.py` tests keep passing. `report.py` needs
  no change: each invocation declares a single `--pair` (`report.py:602-628`, `:668`).

## 7. Measurements taken during design

| What | Value | How |
|---|---|---|
| SciFact texts beginning with their title | 5,183 / 5,183 | `beir/corpus.jsonl` scan |
| Abstract length, words | median 204, p90 309, max 1,541 | same |
| Abstract length, ms-marco tokens | median 314, p90 500, p97 618, max 1,937 | TEI `/tokenize`, `add_special_tokens: false` |
| Query length, tokens | median 19, p90 34, max 62 | same, all 300 queries |
| Abstracts truncated at 512 (pair = 3 specials + query + doc) | 10.9 % (19-token query), 12.5 % (34), 17.1 % (62) | arithmetic on the above |
| Rerank batch of 8, idle | abstracts (median length) 0.79 s; 8 longest (55,193 chars) 1.27 s; 500-char chunks 0.24 s | best of 3 against `/rerank`, `raw_scores: true` |
| TEI client batch cap | 32 (`batch size 64 > maximum allowed batch size 32`) | `/tokenize` with 64 inputs; the harness sends 8 |
| Chunk payload vs corpus | 6 / 6 scrolled chunk texts are substrings of the corpus `text`; `chunk_index` 0 starts with the title | Qdrant scroll, `benchmark_documents_chunks_tenant_bypass` |

## 8. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| V1 | `CommandFlags` is init-only with a `Parse` that reads `--rerank-url`/`--rerank-model`; a validation point exists for the new flag | `Program.cs:385-414`; guard at `BenchmarkQueryScenario.cs:81` |
| V2 | A callable parses `corpus.jsonl` into `CorpusDocument(DocId, Title, Text)` | `JsonlCorpusParser.ParseCorpus(TextReader)`, `JsonlCorpusParser.cs:13`; refuses empty `text` at `:43-46` |
| V3 | Every key-map doc id exists in `corpus.jsonl` | 5,183 key-map ids = 5,183 corpus ids, 0 missing in either direction |
| V4 | The indexed chunk text is the corpus `text` | Qdrant scroll: payload `{text, parent_id, field, chunk_index, ownerId}`; 6 / 6 substrings, index 0 starts with the title |
| V5 | The live stack still holds the Phase 1 SciFact state and the preserved runs exist | Qdrant 19,967 chunks / 5,183 docs; `iverson-api` env `VectorRanking__Lambda=0.70`; `runs/rerank-a0.*`, `runs/rerank-a1.*` present |
| V6 | `Ranked` items carry the key-map-resolved corpus id | `MaxPassageAggregator.cs:59-77` |
| V7 | Nothing parses the sidecar in a way an extra field breaks | `report.py:193` reads only `composite` |
| V8 | `--pair` accepts two pairs sharing the left run, Holm over the declared pairs, per-pair pool check | `report.py:602-628` (paths; no uniqueness constraint on the run side), `:631-666`, `:668-` |
| V9 | TEI truncates, not rejects, long inputs | A1 sidecar `maxInputLength` 512, `autoTruncate` true; 8 longest abstracts scored in one batch |
| V10 | The truncated fraction is small enough to leave the published setup intact | 10.9–17.1 % (§7) — larger than the 3 % estimated during design; recorded, no design change |
| V11 | No client-side length or payload limit trips on abstracts | `TeiRerankClient.cs` has no per-text guard; `BatchSize` 8 < TEI's cap of 32; 55,193-char batch accepted |
| V12 | Extracting the selection function breaks no existing test | `WinningChunkAggregation.Ranked` is `(DocId, Score, Text)` (`MaxPassageAggregator.cs:20-21`); `RunChunksAsync` is private; only `MaxPassageAggregatorTests.cs` mentions the scenario, in a comment |
| V13 | Nothing constructs `CommandFlags` outside `Parse` | `grep -rn "CommandFlags" --include=*.cs` → the declaration (`Program.cs:385`), `Parse` (`:400`, target-typed `new()`), its single call (`:18`), and seven `RunAsync(CommandFlags flags, …)` parameters; the test project never names it |
| V14 | `--config-label L` yields `L.chunks.trec` / `L.similar.trec` / `L.meta.json` | `BenchmarkQueryScenario.cs:258-259` |
| V15 | The CSRF re-mint fix is on `main` | `git branch --contains cf9cbb8` → `main` |
| V16 | The compose reranker defaults to ms-marco and its model volume survives | `docker-compose.yml:160`; volume `iversonserver_reranker_models`; `/info` ready in < 60 s |
| V17 | The gate doc and the parent spec's closing note exist to receive the result | both on `main` (`12571d3`) |
| V18 | No title prepend is needed | 5,183 / 5,183 texts begin with the title |
| V19 | The winners' `Text` has no consumer other than the guard and the rescore | `BenchmarkQueryScenario.cs:373`, `:379` |
| V20 | The live server's build composite equals both preserved sidecars, so `report.py` prints no `BUILD MISMATCH` over the family | `curl 127.0.0.1:8081/build` → `31583db5aea49136` = `rerank-a0.meta.json` = `rerank-a1.meta.json` (CDR round 1) |
| V21 | `corpus.jsonl` `_id` values are unique, so the doc id → text dictionary builds without collapsing documents | 5,183 lines, 5,183 distinct ids (CDR round 1) |
| V22 | `RerankInput` / `RerankInputs` collide with no existing symbol | repo-wide grep over `.cs` and `.py` → zero hits (CDR round 1) |
| V23 | Columns 1–5 are the drift test; the run-tag column differs between labels by construction | `TrecRunWriter.cs:29-30` appends `runTag`; `BenchmarkQueryScenario.cs:262` passes `flags.ConfigLabel`; `rerank-a0` vs `rerank-a0-preplan`: `cmp` differs at byte 34, columns 1–5 identical (CDR round 1) |

## 9. Known issues, accepted as out of scope

- Abstracts over the window lose their tail to `--auto-truncate` (10.9–17.1 %). This is the published
  setup; a windowing or head+tail policy is not part of this trial.
- A1 is reused rather than re-run. The columns-1–5 A0 check in §5 step 1 is the evidence that reuse
  is sound; if it fails, A1 must be re-run before A4 is compared to it.
- NFCorpus is not run. The trial answers the SciFact gate question only.
