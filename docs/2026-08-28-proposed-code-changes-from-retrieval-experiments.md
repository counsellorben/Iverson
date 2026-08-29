# Proposed code changes from the retrieval experiments

**Status: preliminary.** ArguAna is still in flight and may add or remove items — in particular
anything about `SearchSimilar`, whose only clean test is that run. Nothing here has been through
`thorough-brainstorming`; this is the input to that, not a substitute for it.

Every item states the evidence that supports it and the evidence it lacks. Items are ranked by
strength of evidence, not by appeal.

## Tier 1 — supported by significant, replicated evidence

### 1. Fusion weights and MMR λ become configuration, not compile-time constants

**The strongest recommendation here, and the one the data most directly forces.**

`ResultReranker.WBase/WCentroid/WDecay` and `ResultDiversifier.Lambda` are `private const double`.
A single compile-time value cannot be correct, because the optimum differs by corpus and the
difference is **significant in both directions**:

| corpus | chunks/doc | w = 0.500 vs shipped w = 0.333 |
|---|---|---|
| SciFact | 3.96 | nDCG@10 **-0.0082** (t = -2.54, sig) — raising it *hurts* |
| FreshStack | 10.79 | AP **+0.0059** (p = 0.0008, Holm-sig) — keeping 0.333 *hurts* |

Same change, opposite sign, both significant. Any constant is wrong for half the deployments.

**Proposal:** bind these to an options class from configuration. **Keep the defaults exactly as
shipped** (0.60 / 0.30 / 0.10, λ 0.70) — this change enables tuning, it does not retune anything.

**Risk:** low. No behavioural change at default values. `ResultRerankerTests` hand-computes expected
values at the shipped constants and must be re-pointed at the same values via the options object.

### 2. Embedding prefixes become model-conditional

`EmbeddingService` hard-codes `search_document: ` / `search_query: `. These are **nomic-specific**.
We are currently sending them to `snowflake-arctic-embed:s`, for which they are simply wrong — arctic
uses a query-side instruction and no document prefix. Any model swap silently mis-prefixes every
vector on both sides, and nothing in the code or tests would notice.

The prefixes also carry **no demonstrated benefit**: measured on SciFact at +0.0134 nDCG@10
(t = +1.30, not significant). So today's constants are an unmeasured benefit paired with a real
correctness hazard.

**Proposal:** prefixes move into embedding configuration alongside `ModelId`, defaulting to empty,
with nomic's pair configured wherever nomic is configured. **Same values today, so no re-ingest.**

**Risk:** moderate — a configuration mistake changes every stored vector and requires a full
re-ingest to correct. The drift gate in `IngestContractTests` already pins the document prefix into
`ingest-contract.json`; that gate must extend to whatever the configured value is, or it becomes
vacuous.

## Tier 2 — harness and tooling; cheap, and each prevents a real error that already happened

### 3. Record deployed-artifact identity in every run file

A stale `iverson-api` image — built from a checkout that no longer existed — invalidated two SciFact
runs. A full SDD pipeline, four task reviews, two design reviews, an implementation review and a
whole-branch review all verified the *code*, and none verified that the code under review was the
code running. The interim workaround is a manual `docker cp` and a string search.

**Proposal:** `benchmark-query` obtains a build identity from the API and writes it into the run's
metadata beside `--config-label`, so a run file carries evidence of what produced it.

### 4. Emit a permutation test alongside the t-test

The SciFact centroid recall claim (−0.0167, t = −2.25, p = 0.025) was **retracted** because it fails
a sign-flip permutation test (p = 0.063). Only 5 of 300 queries changed; a paired *t*-test on a
distribution that is 98.3% exact zeros violates its own assumptions. The failure mode is detectable
in advance from the count of changed queries.

**Proposal:** fold `scratchpad/stats.py` into `report.py` — paired *t*, permutation p, 95% CI,
Cohen's *d_z*, Holm correction, minimum detectable effect, and the number of queries whose score
changed. Flag loudly when fewer than ~10% changed.

### 5. Chunk-budget guard in the harness

`SearchChunks` `top_k` counts **chunks**, and the server does not dedup by parent. At FreshStack's
10.79 chunks/document the shipped `ChunkBudgetMultiplier = 5` reaches only ~23 distinct documents
against a `DocumentBudget` of 50 — R@50 would have measured the budget, not retrieval, and would
have looked like a retrieval finding.

**Proposal (harness only):** derive chunks/document from the keymap stats sidecar and refuse, or
loudly warn, when `DocumentBudget × ChunkBudgetMultiplier` cannot plausibly reach `DocumentBudget`
distinct documents.

**Production analogue, documentation only for now:** the same arithmetic binds any caller who wants
N documents out of `SearchChunks`. Worth documenting; **not** worth a proto change on this evidence.

## Tier 3 — spike first, do not ship

### 6. Centroid weight conditioned on chunks per document

The request-scaled design, revived on a different variable. `top_k` never predicted anything, but
chunks/document has three data points: 3.96 → 0.333, 4.05 → 0.333, 10.79 → 0.5–0.667. **Two of the
three sit at nearly the same x.** That is a hypothesis, not a schedule. It needs a fourth corpus at
an intermediate chunk density (~6–8 chunks/doc) before any curve is fitted. The server already knows
chunks/document at scoring time, so no request-shape change would be required if it survives.

### 7. Per-endpoint λ

MMR is neutral on `SearchChunks` across two corpora and four measures, and costs `SearchSimilar`
12.8% of R@50 (FreshStack +0.0558, t = 9.69; NFCorpus +0.0216, t = 4.46 — replicated). That is the
**price only**: these judgments award no credit for diversity, so the benefit remains unmeasured and
unmeasurable with the current metrics. Implement α-nDCG over FreshStack's nugget qrels first — they
are subtopic judgments and exist for exactly this — then revisit.

## Explicitly not proposed, with reasons

- **Changing the shipped `WCentroid` default.** Raising it to 0.5 is significantly *worse* on
  SciFact (−0.0082 nDCG@10, t = −2.54). Configurability (item 1) is the answer, not a new constant.
- **Removing the centroid.** FreshStack settles this: removing it costs −0.0263 nDCG@10 / −0.0199 AP,
  Holm-significant, *d_z* ≈ 0.3.
- **Changing the production embedding model.** arctic-embed-m beats nomic on published MTEB Retrieval
  (54.90 vs 52.81) and is 2.5x faster here, but **we have never compared them on our own benchmark
  with correct prefixes.** The speed numbers are specific to this CPU-only box.
- **Adding bpref generally.** BEIR qrels contain no `rel=0` rows, so bpref reduces algebraically to
  recall — it reproduced R@50 to six decimals. It is meaningful only on FreshStack, which records
  35,876 judged negatives.
- **Request-shape changes to `SearchSimilarRequest` / `SearchChunksRequest`.** No evidence supports a
  caller-set knob, and a bad caller choice would produce worse ranking with no error.
