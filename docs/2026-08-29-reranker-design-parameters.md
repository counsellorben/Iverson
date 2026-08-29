# Reranker design parameters, derived from the retrieval experiments

Status: proposal. Every number below is measured on preserved TREC run files in
`~/repositories/iverson-benchmark-corpora/`, not estimated.

## 1. The case for a reranker, quantified

The *oracle ceiling* is what nDCG@10 becomes if the candidates we already retrieve are
reordered by true relevance. It is the hard upper bound on what any reranker can buy, and it
is computable from the existing run files.

| corpus | config | endpoint | actual | oracle@10 | oracle@20 | oracle@50 | R@50 | headroom |
|---|---|---|---|---|---|---|---|---|
| SciFact  | nomic, correct | chunks  | 0.6954 | 0.8273 | 0.8819 | 0.9108 | 0.9099 | **+0.2154** |
| SciFact  | nomic, correct | similar | 0.6664 | 0.7955 | 0.8385 | 0.8703 | 0.8691 | +0.2039 |
| NFCorpus | nomic, correct | chunks  | 0.3522 | 0.4157 | 0.4806 | 0.5589 | 0.2489 | **+0.2067** |
| NFCorpus | nomic, correct | similar | 0.3398 | 0.4020 | 0.4648 | 0.5186 | 0.2241 | +0.1788 |
| ArguAna    | arctic, MISCONFIGURED | chunks | 0.5204 | 0.7980 | 0.8972 | 0.9400 | 0.9400 | +0.4197 |
| FreshStack | arctic, MISCONFIGURED | chunks | 0.2642 | 0.3746 | 0.4921 | 0.6441 | 0.5094 | +0.3799 |

**Only the first four rows are evidence.** ArguAna (ingested 2026-08-28T20:49) and FreshStack
(2026-08-28T08:24) both predate the prefix discovery and ran arctic under *nomic's* prefixes —
the condition measured to cost up to 0.11 nDCG@10 on NFCorpus. A badly-encoded query degrades
ordering, which *inflates* apparent reranking headroom. Treat their numbers as an upper bound
on a broken system. (The FreshStack re-measurement is running now.)

Headroom on the trustworthy corpora is **+0.21 nDCG@10** — an order of magnitude larger than
anything the fusion-weight sweeps produced (±0.02 at best). Reranking is where the remaining
quality lives.

## 2. Parameters

### P1 — Candidate depth K = 50

Marginal oracle gain by depth: SciFact 10→20 +0.055, 20→50 +0.029 (flattening). NFCorpus
10→20 +0.065, 20→50 +0.078 (**still climbing at 50**). K=50 is free — it is already
`DocumentBudget`. For corpora that behave like NFCorpus, test K=100, which requires raising
`DocumentBudget`; do not raise it blindly, since P2's cost is linear in K.

### P2 — Rerank unit: the document, scored through its max-passage chunk

Score exactly one `(query, passage)` pair per candidate document, reusing the chunk
`MaxPassageAggregator` already selects. Cost is then **exactly K pairs per query, independent
of chunk density**.

The alternative — score every chunk — costs `K × chunks/doc`: 1.3× on NFCorpus but **10.79× on
FreshStack**. Measured local per-item latency for this model class (6-layer, 384-hidden;
all-minilm 137 ms, arctic-s 234 ms) makes the difference decisive on this hardware:

- doc-level, K=50: 7–12 s/query → ~35–60 min per benchmark arm. Tractable.
- chunk-level on FreshStack: ~120 s/query → 40+ h per arm. Not tractable here.

The 15W power limit is a measured property of this box, not a guess. A reranker must therefore
be tiered/optional in production, not on the default path.

### P3 — Stage 1's objective flips from ordering to recall

This is the non-obvious consequence. With a reranker downstream, retrieval's job is to get
relevant documents *into* the top K, not to order them. The optima differ, measured on
NFCorpus at multiplier 20:

| w = WCentroid/(WBase+WCentroid) | nDCG@10 | R@50 |
|---|---|---|
| 0.000 | 0.3319 | 0.2370 |
| **0.167** | **0.3343** | 0.2420 |
| **0.333** | 0.3304 | **0.2459** |
| 0.500 | 0.3299 | 0.2432 |
| 0.667 | 0.3208 | 0.2421 |

The ordering-optimal weight is 0.167; the **recall-optimal weight is 0.333**, and 0.5/0.667
both beat 0.167 on recall. Re-sweep `w` against R@K once a reranker exists. Also set
`ChunkBudgetMultiplier = 20`: worth +0.0101 R@50 at high w, query-side only, no re-ingest.

### P4 — MMR off: λ = 1.0

Measured λ=0.70 (shipped) against λ=1.00 (no diversification) across 4 corpora × 2 endpoints:

| corpus | chunks | similar |
|---|---|---|
| SciFact | **+0.0088** | −0.0002 |
| NFCorpus | +0.0006 | +0.0021 |
| ArguAna | −0.0000 | +0.0014 |
| FreshStack | −0.0000 | −0.0008 |

(positive = λ=1.0 better). Diversification never helps, and the single largest effect favours
turning it off. Do not let MMR reorder a reranker's output. If diversity is wanted it must be
justified against a metric that rewards it — α-nDCG or S-recall — which BEIR and FreshStack
qrels do not.

### P5 — Blend β: default 1.0 (pure reranker score)

Expose `final = β·rerank + (1−β)·retrieval` as **configuration, not a `private const`**, and
sweep it. Default to β = 1.0 until a measurement says otherwise.

The reason is the centroid's history: its apparent optimum tracked *prefix wrongness*
monotonically — w=1.000 mis-prefixed, 0.667 unprefixed, 0.167 correct. A blend weight that
looks like it helps is often compensating for a misconfiguration elsewhere. Any β > 0 that
appears to earn its keep must be re-checked under a known-correct encoder.

### P6 — Input format is the highest-variance parameter in the system

The largest single effect measured in this entire project was getting arctic's query
instruction right: **+0.11 nDCG@10, +49% relative at w=0**. Cross-encoders have their own
required input format, and instruction-tuned rerankers take a task instruction.

Therefore: make the format model-conditional, verify it in the *deployed artifact*
(`docker cp` the DLL, search UTF-16 literals), and add a startup guard that logs model +
format — the analogue of the API-init/dimension guard that caught run C's silent failure after
14 empty run files. Getting P6 wrong costs more than every other parameter here combined.

### P7 — Evaluation protocol

MDE at 80% power on SciFact (n=300) is ≈0.019 nDCG@10. Expected reranker effect is oracle
headroom (0.21) × typical cross-encoder recovery (30–50%) ≈ **0.06–0.10** — 3–5× MDE. Unlike
the fusion sweeps, which chased effects at or below MDE, this is decisively detectable on a
single corpus.

Report paired *t* **and** sign-flip permutation p, 95% CI, Cohen's *d_z*, and Holm across the
arm family (`scratchpad/stats.py`). Never report bpref on BEIR — it reduces algebraically to
recall there, since BEIR qrels carry no `rel=0` rows. It *is* meaningful on FreshStack
(35,876 judged negatives).

### P8 — What a reranker will not fix

NFCorpus R@50 = 0.249 caps its oracle@50 at 0.559. No reranker reaches a document retrieval
never surfaced. On recall-bound corpora, reranking is second priority behind recall itself
(hybrid/lexical retrieval, higher K). SciFact is the opposite case — R@50 = 0.910 and
oracle@50 = 0.911, i.e. **ordering is the entire remaining gap**.

## 3. Defect found while producing this

Removing nomic's document prefix exposed a latent crash the prefix had been masking.
`SplitIntoChunks` (`IntelligenceStoreConsumer.cs:686`) yields `text[start..end].Trim()` with no
empty filter; a whitespace-only window becomes `""`. `EmbedDocumentAsync` then sends Ollama an
empty input, which returns `{"embeddings": []}`, and `EmbeddingService.cs:89`'s
`.GetProperty("embeddings")[0]` throws. The consumer path has no guard —
`IntelligenceStoreConsumer.cs:226` checks the whole field, never the individual chunk.

Masked until now because nomic prefixes documents with `"search_document: "`, so the input was
never empty. Arctic's document prefix is `""`. Frequency on FreshStack: **28 windows across 17
of 6,000 documents (0.28%)** — it killed the first re-ingest at document 383.

This is a production defect, not a benchmark-script defect, and it is a direct consequence of
making prefixes model-conditional (item 2 of the code-changes proposal). Ship the empty-chunk
guard *with* that change.
