# Teacher ceiling check — design

Written 2026-09-20. Stage 1 of a three-stage line of work on **per-tenant distilled rerankers**.
Stages 2 (distillation) and 3 (tenant-specificity) are explicitly NOT designed here; they get their
own spec only if this stage passes its gate.

## 1. The question

Can a **self-hostable open reasoning model**, reordering the candidates Iverson already retrieves,
reach an nDCG@10 high enough to justify building per-tenant distilled rerankers?

This is a ceiling measurement, not a shipping proposal. Nothing in this spec changes server code.

**Why the teacher must be self-hostable.** The eventual architecture trains one small reranking
model per tenant on that tenant's own documents, labelled by a teacher. A hosted frontier API cannot
be that teacher: labelling sends every tenant document to a third party, which contradicts the
tenant-isolation and at-rest-encryption work. The ceiling that matters is therefore the one an
open model sets, and a hosted model's ceiling — however high — would not be actionable. Ben ruled
this 2026-09-20: the open teacher *is* the architectural decision.

**Why the gate can be trusted to close the line.** A student distilled from a teacher lands below
that teacher. If the teacher cannot clear the bar, nothing distilled from it can, and the
per-tenant reranking line is closed by this one measurement.

## 2. Prior art this rests on

| Source | What it establishes |
|---|---|
| `docs/plans/2026-09-GATE-reranker-phase1.md` | GATE FAILED. ms-marco +0.0082 (p_adj 1.0000); bge-reranker-base −0.1126 (significantly worse). MDE@80% ≈ 0.043 at n=300 |
| `docs/specs/2026-09-03-reranker-design.md` | P4: MMR off (λ=1.00) for reranking. §7.1.2 pool-invariance rule. The Phase 2 server-side design, unbuilt |
| A4 document-input trial (2026-09-04) | Full `corpus.jsonl` text as reranker input gained nothing for ms-marco: +0.0032, p 0.8193. **This spec uses that same input format** — what differs here is the model class, not the input |
| `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md` | Fusion-weight sweeps move ±0.02; ordering, not retrieval, is the bottleneck |

## 3. Method

Offline listwise rescoring of a preserved TREC run. **No Iverson server, no Qdrant, no compose, no
Authentik** — which removes every operational failure that hit the Phase 1 arms (the missing
`X-authentik-CSRF` header, the 2 h `access_token_validity` boundary, a mid-arm host reboot).

- **Pool / baseline:** `rerank-a0prime.chunks.trec` (λ=1.00, MMR off, per P4). 300 queries × 50
  documents, 4,083 distinct doc ids.
- **One call per query.** The prompt carries the query text and all 50 documents (title + abstract
  from `corpus.jsonl`), each labelled by doc id. The response is a JSON array of exactly those 50
  doc ids, most relevant first.
- **Documents are presented in a seeded shuffle, not fusion order.** Presenting them in A0′ order
  anchors the teacher to the ranking under test; an anchored teacher measures the old ranking as
  much as itself. The shuffle seed is recorded.
- **Scores are rank-derived** (`score = 51 − position`, so ranks 1..50 map to 50..1), so ties are
  structurally impossible. The `+1` is deliberate: a score of 0.000000 at rank 50 would make
  `report.py`'s structural check report 14,700 of 15,000 non-zero scores and read as a defect. See
  §7 for why ties matter.
- **Scored with** `report.py --pair teacher=rerank-a0prime.chunks.trec`.

### Reference points

| Quantity | Value | Provenance |
|---|---|---|
| Baseline A0′ nDCG@10 | 0.6980 | reproduced 2026-09-20 from the preserved run |
| Baseline R@50 | 0.9193 | same |
| Oracle on this exact pool | **0.9196** | measured 2026-09-20 (each query's own 50 candidates ordered by qrel grade) |
| Headroom | **+0.2216** | derived |
| Best Phase 1 arm (A1) | 0.7043, p_adj 1.0000 | gate doc |
| MDE@80% at n=300 | ≈0.043 | gate doc |
| Relevant documents inside the pools | 314 | measured 2026-09-20 |

The §3 table of `2026-09-03-reranker-design.md` gives SciFact chunks oracle@10 as 0.8273; the
measurement above (0.9196) matches the gate doc's 0.9216 instead. **This spec uses 0.9196**, which
was computed directly from the pool under test and is reproducible by the command in §9.

## 4. Teacher and serving

- **Instance:** one 80 GB GPU, per-second billing. RunPod Community A100 80 GB ≈ $1.39/hr or
  H100 ≈ $1.99/hr (third-party price listings, August 2026 — confirm at rental).
- **Primary teacher: gpt-oss-120b** (Apache 2.0, ~117B total / ~5B active, MXFP4, 128K context,
  runs on one 80 GB GPU). The 128K context matters: the largest single query pool is 137,152
  characters ≈ 34K tokens (§10, A16).
- **Fallback:** a 32B-class reasoning model in FP8 on a 48 GB box, if cost pressure demands it.
- **Serving:** vLLM's OpenAI-compatible route, called with the same stdlib `urllib.request` POST to
  `/v1/chat/completions` that `enrich_bench.py:56-62` uses, with `temperature: 0`, `stream: false`,
  and **`max_tokens: 8192` set explicitly**. `enrich_bench.py`'s `MAX_TOKENS = 256`
  (`enrich_bench.py:30`) is **not** carried over: it was sized for that script's 2–3-sentence
  enrichment replies, whereas one listwise answer is 569–594 characters of doc ids alone, before the
  reasoning trace that shares the same budget. A response that stops on length
  (`finish_reason: "length"`) is a §6 row-1 failure and is never parsed. **No new dependency** — no
  `openai` package, no `requests`.
- **Output constraint:** vLLM guided decoding to a JSON-array schema. The parameter is mid-rename
  (`guided_json` → `structured_outputs`); the executing session verifies the name against the
  installed vLLM version and records it in the run log.
- **Determinism:** `temperature: 0` plus a fixed seed. `seed` support on vLLM's OpenAI route is
  unconfirmed and is an execution-time check; vLLM's continuous batching can perturb results
  regardless. §8's repeat check measures the residual rather than assuming it away.
- **No truncation.** `max_model_len` 128K. A prompt that would exceed the budget fails loud (§6).

## 5. The script

`Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`, patterned on `popularity_rerank.py`
(the existing offline TREC-run rescorer: `load_run`, `write_run`, order checks, `--run/--out`).
Per-script `load_run` helpers are the local convention — four scripts define their own; this spec
does not propose extracting a shared module.

```
teacher_rerank.py --run <a0prime.chunks.trec> --corpus <corpus.jsonl> --queries <queries.jsonl>
                  --base-url <vllm> --model <id> --seed N --shuffle-seed N
                  --responses <raw.jsonl> --out <teacher.chunks.trec>
```

### Sidecar

The script writes `<label>.meta.json` beside the run file carrying **`"composite":
"31583db5aea49136"`** — A0′'s own composite, because the retrieval build that produced the pool is
the same one under comparison. A teacher-derived composite would make `report.py:592` print
`!! BUILD MISMATCH ... Any comparison between them is confounded` on every comparison. The teacher's
identity goes in a `"reranker"` block, exactly as `rerank-a1.meta.json` records its cross-encoder:

```json
{
  "configLabel": "teacher-ceiling",
  "composite": "31583db5aea49136",
  "recordedAtUtc": "...",
  "reranker": {
    "baseUrl": "...", "modelId": "...", "quantisation": "...", "vllmVersion": "...",
    "temperature": 0, "seed": N, "shuffleSeed": N, "maxModelLen": 131072,
    "maxCompletionTokens": 8192,
    "promptTemplateSha256": "...", "instanceType": "..."
  }
}
```

`report.py` reads only `composite`; extra keys are inert.

### Tests

`test_teacher_rerank.py`, matching the existing `test_*.py` convention. Everything except the model
call is tested on the dev box **before any GPU is rented**: permutation validation (missing,
duplicated, invented ids), JSON parsing, the refusal-to-write rule, seeded-shuffle reproducibility,
and run-file formatting.

## 6. Failure behaviour

Following `TeiRerankClient`'s doctrine — a silent fallback "writes a run file indistinguishable from
a reranked one":

| Failure | Response |
|---|---|
| Response is not valid JSON, or not a permutation of the 50 input ids | One retry with the same prompt; on a second failure the query is recorded unscored |
| Any query unscored at the end | **The run file is not written.** Exit non-zero, listing the failures. Never filled from fusion order |
| Prompt exceeds the token budget | Same as above. No truncation |
| Instance dies mid-run | Raw per-query responses are appended to `--responses` as they arrive; a rerun resumes from that file |

## 7. Why listwise, and what it avoids

Coarse pointwise grading was rejected on measured evidence. Four-level grades that **preserve** the
fusion order score **nDCG@10 0.2672** when written as bare integers, against **0.6980** with an
epsilon tie-break on fusion rank (measured 2026-09-20). Identical rankings, identical grades; the
only difference is that `ir_measures` breaks tied scores by doc id. That is a 0.43 nDCG artifact —
roughly ten times the effect under test.

A pointwise design therefore survives only via a tie-break on the existing fusion order, which makes
the result a measurement of the teacher *plus* the ranking it is supposed to replace. Listwise
ranks are a total order and cannot tie.

**Cost of this choice:** listwise produces no per-pair scores, which stage 2 would want as
distillation labels, and a 50-document window carries position bias. If the result lands near the
threshold, the contingency is a sliding-window pass (20 documents, stride 10) to test whether
position bias was the limiter. That contingency is not built now.

## 8. Gate rule

**The comparison family is exactly one:** teacher vs A0′ on nDCG@10. No Holm correction is required
at m=1. Read the permutation p (10,000 sign flips, seeded), per `report.py`'s own guidance.

**PASS requires both:**

1. **permutation p < 0.05**, and
2. **delta ≥ +0.055** — 25% of the measured headroom (0.2216), i.e. nDCG@10 ≥ 0.753.

Ben set the 25% threshold on 2026-09-20. Its justification is entirely local: +0.055 clears the
0.043 MDE, so it is detectable at n=300, and it is about 7× A1's +0.0082 null result. **No
literature ceiling is claimed** — an earlier draft cited a published SciFact reranker ceiling of
~0.75–0.77; that figure could not be verified and has been withdrawn.

A student distilled from the teacher lands below it, so a teacher that merely clears significance
with a small delta would distil to nothing. That is what the second condition exists to prevent.

### Structural validity checks

All five must hold. The first two are enforced by `report.py --pair`, verified 2026-09-20 to exit 1
on violation:

1. **Pool invariance** — doc set changed on 0 of 300 queries.
2. **At least 25% of queries reordered.**
3. **R@50 identical to 0.9193** at four decimals. Guaranteed by construction; a deviation means the
   script corrupted the pool.
4. **No result may exceed the oracle, 0.9196.** Above it means label leakage, not a good teacher.
5. **Zero duplicate `(qid, score)` pairs.** Rank-derived scores make ties impossible; a duplicate
   indicates a writer bug.

### Repeat check (reported, not gating)

Rerun a seeded 50-query subsample and report how many orderings differ, plus the nDCG@10 delta
between passes. Phase 1 never needed this — retrieval is bit-deterministic (0 of 323 sequences
differed across independent invocations) — but a language model is not, and the size of that noise
floor conditions how much any near-threshold result can be trusted.

## 9. Artifacts and verdict

Preserved under `~/repositories/iverson-benchmark-corpora/teacher-ceiling-2026-09/`: raw per-query
responses (JSONL), the run file, the `.meta.json` sidecar, full `report.py` output, and the run log.
**md5s of the run file and the report output go in the verdict doc** — the convention that makes a
surprising result provably measured.

Verdict: `docs/plans/2026-09-GATE-teacher-ceiling.md`. A PASS triggers the stage 2 spec. A FAIL
closes the per-tenant reranking line, and the verdict says so in those words.

Reproducing the two reference measurements:

```bash
# from the repo root
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
B=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --run $B/runs/rerank-a0prime.chunks.trec --qrels $B/qrels.trec     # nDCG@10 0.6980, R@50 0.9193
```

The oracle (0.9196) is reproduced by ordering each query's own 50 candidates by qrel grade and
scoring the result; the dry-run check (§11 step 2) must reproduce 0.6980 exactly under plain
`--run`/`--qrels` scoring, without `--pair`.

## 10. Verified assumptions

Verified 2026-09-20 against the repo and the preserved corpora.

| # | Assumption | Verdict |
|---|---|---|
| A1 | A0′ run is 300 queries × 50 rows, scores 0.6980 / 0.9193 | **Confirmed** — `report.py` output |
| A2 | Every doc id in the run is in `corpus.jsonl` | **Confirmed** — 4,083 ids, 0 missing |
| A3 | Every query id is in `queries.jsonl` with text | **Confirmed** — 0 missing |
| A4 | qrels covers the 300 queries | **Confirmed** — 300/300 |
| A5 | Pool oracle | **Measured: 0.9196**, not the spec §3 table's 0.8273 |
| A6 | Doc-id formats match between corpus and run | **Confirmed** — bare numeric `_id` |
| A7 | `--pair` enforces both halves and exits non-zero | **Confirmed** — exit 1 on pool violation, on 10% churn, *and* on 0% churn (an unchanged ordering: `ARM INVALID … at least 25% is required`); exit 0 on a valid rescore |
| A8 | `report.py` needs `PYTHONPATH=python-libs` | **Confirmed** — system python3 lacks ir_measures |
| A9 | Sidecar schema is emittable | **Confirmed, and it changed the design** — must reuse A0′'s composite `31583db5aea49136`; `report.py:184-202` reads only `composite` |
| A10 | ir_measures ranks by score, ties by doc id | **Confirmed** — 0.2672 vs 0.6980 tie probe |
| A11 | Stdlib-only HTTP is the in-repo pattern | **Confirmed for the transport only** — `enrich_bench.py:56-62`. Its body is **not** adopted wholesale: `MAX_TOKENS = 256` (`enrich_bench.py:30`) would truncate a 569–594-character answer, so §4 sets `max_tokens` explicitly |
| A12 | pytest available | **Confirmed** — 9.1.1 |
| A13 | vLLM exposes an OpenAI-compatible chat route | **Confirmed** (docs); `seed` support **unverified** — execution-time check |
| A14 | vLLM guided decoding for a JSON array | **Confirmed**, parameter name mid-rename — execution-time check |
| A15 | A model fits 80 GB and is current | **Confirmed** — gpt-oss-120b, Apache 2.0, 128K context, single 80 GB GPU (September 2026 listings) |
| A16 | 50 documents fit the context | **Corrected** — worst case 137,152 chars ≈ 34K tokens, not the ~20K first assumed |
| A17 | RunPod Community ≈ $1.39/hr A100 80 GB, per-second | **Third-party listings only** — confirm at rental |
| A18 | temperature 0 + seed is near-deterministic | **Partially** — batching caveat stands; §8's repeat check measures it |
| A19 | MDE@80% ≈ 0.043 at n=300 | **Confirmed** — gate doc |
| A20 | `report.py` handles a one-comparison family | **Confirmed** — Holm family size follows the declared pairs |
| A21 | Published SciFact reranker ceiling ~0.75–0.77 | **WITHDRAWN** — could not be verified; no longer load-bearing (§8) |
| A22 | `docs/specs`, `docs/plans` gitignored | **Confirmed** — `.gitignore:49,51`; commit with `git add -f` |
| A23 | Gate-doc naming convention | **Confirmed** — `docs/plans/2026-09-GATE-*.md`, 11 existing |
| A24 | Nothing in code consumes the A0′ run file | **Confirmed** — docs-only references |
| A25 | Scripts need no manifest registration | **Confirmed** — standalone scripts + `test_*.py` |
| A26 | The sidecar is found by **run filename**, not `configLabel` | **Confirmed** — `report.py:167-182` (`sidecar_path_for`) strips `.trec`, then a trailing `.chunks`/`.similar`, then appends `.meta.json`. So `--out` must be `<label>.chunks.trec`; a mismatch degrades to `BUILD UNKNOWN`, which is loud, not silent |
| A27 | `report.py`'s delta equals `teacher − 0.6980` | **Confirmed** — delta is a mean over the query-set intersection (`report.py:517-527`), and §6 row 2 forbids a partial run file, so the intersection is all 300. §8's two PASS conditions are therefore equivalent, not independent |
| A28 | Doc ids survive a JSON round-trip losslessly | **Confirmed** — all 4,083 ids are pure digits, 4–9 chars, 0 leading zeros, max < 2^53; number-or-string decoding is lossless either way |

## 11. Execution outline

1. Write `teacher_rerank.py` + `test_teacher_rerank.py`; tests pass on the dev box.
2. **Dry run with a stub teacher that returns each query's candidate ids ordered by their rank in
   the input run** — i.e. it ignores the shuffled presentation order and reconstructs fusion order.
   The written run must score `nDCG@10 0.6980 / R@50 0.9193` exactly under
   `report.py --run <dry-run>.chunks.trec --qrels <qrels>` — **without `--pair`**, because the
   artifact is order-identical to A0′ and `check_pool`'s ≥25%-reordered floor
   (`report.py:700,760-766`) correctly rejects an unchanged ordering as "the reranker did not run".
   This proves prompt construction, permutation validation, the run writer and the scorer, with no
   GPU and no spend; the `--pair` stage is exercised by the real run, whose ordering differs.
3. Rent the instance, serve the model, record vLLM version and the structured-output parameter name.
4. Run 300 queries. Verify the five structural checks.
5. Run the 50-query repeat.
6. Write the verdict doc with md5s; destroy the instance.

Estimated spend: $2–5 per pass, under $20 including a failed pass and the repeat.

## 12. Out of scope

Stage 2 (query generation, LoRA distillation, per-tenant adapters), stage 3 (NFCorpus repeat and
adapter cross-application), `SearchSimilar`, multi-LoRA serving, deletion/retraining policy for
per-tenant models, and every server-side change. None of it is designed here, and none of it
proceeds unless §8's gate passes.
