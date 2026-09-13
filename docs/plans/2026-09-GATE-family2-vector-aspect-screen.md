# Family 2 — Chunk-Vector Aspect Coverage — Screen Verdict

Measurements recorded 2026-09-13 from the `family2-vector-screen` worktree at HEAD `b3c1201`
(`extract pair_row so the reconstruction-to-statistics seam is testable offline`), which carries Task 1's
contract extension and Task 2's instrument from
`docs/plans/2026-09-13-family2-vector-aspect-screen-implementation-plan.md`. The design is
`docs/specs/2026-09-13-family2-vector-aspect-screen-design.md`.

The parent is `docs/plans/2026-09-GATE-aspect-coverage-oracle.md` and its 2026-09-13 amendment, which
closed ranked-changes item 15 on the **measurability** ground while recording that one candidate family —
chunk-vector-derived aspect coverage — had **never been screened**. This gate screens it, and answers one
pre-registered question from spec §1:

> **Does the spread of a document's matched chunk vectors, relative to the query, proxy the number of
> distinct query aspects that document covers?**

This is a screen, not a term. It decides whether a signal exists; it does not specify one.

---

## Verdict: FAIL — no candidate beats the `n_chunks` null

Spec §2.4's rule, pre-registered before the run and applied here verbatim:

> A candidate **passes** iff, on the query-clustered bootstrap over the primary population, its
> Spearman ρ against aspect count exceeds `n_chunks`' ρ with the 95 % CI on the **difference** excluding
> zero, and Holm-adjusted p < 0.05 across the three candidates.

**No candidate satisfies it.** Every 95 % CI on the difference against `n_chunks` contains zero and every
Holm-adjusted p is 1.0000. Two of the three exceed the null's ρ by a margin smaller than a third of its
own CI half-width; the third falls **below** the null.

### The three candidates, 2,733-pair primary population (decides)

Null: **`n_chunks` ρ = +0.0714** against per-document aspect count.

| Candidate | ρ | diff vs null | 95 % CI on the difference | boot. p | Holm p_adj (3) | §2.4 |
|---|---|---|---|---|---|---|
| `residual_spread` | +0.0578 | **−0.0136** | [−0.0593, +0.0330] | 0.5819 | 1.0000 | **FAIL** |
| `greedy_cover` (τ = 0.90) | +0.0732 | +0.0017 | [−0.0114, +0.0151] | 0.7827 | 1.0000 | **FAIL** |
| `effective_rank` | +0.0727 | +0.0012 | [−0.0283, +0.0318] | 0.9227 | 1.0000 | **FAIL** |

Bootstrap: 10,000 resamples of **queries** (610 units), never pairs, seed 20260913. Holm family size 3,
fixed before the run. τ = 0.90 is the pre-registered primary.

### This is a failure by `residual_spread`, which is the strong form

Spec §5 records that `greedy_cover` and `effective_rank` are **bounded above by `n_chunks` by
construction**, and that a failure by those two alone would be weaker evidence against the family than a
failure by `residual_spread`. **That mitigation does not apply here.** All three failed, and
`residual_spread` — the one candidate that is not count-bounded, and the one that directly encodes spec
§1's structural argument (project the query out, measure what is left) — did not merely tie the null. It
scored **below** it, at +0.0578 against +0.0714.

The measured degeneracies confirm which candidate carried the real test:

| Candidate | Spearman ρ **with `n_chunks`** | What that says |
|---|---|---|
| `greedy_cover` (τ = 0.90) | **+0.9493** — and numerically **equal** to `n_chunks` on 2,426 / 2,733 pairs (88.8 %) | is a chunk count in all but name |
| `effective_rank` | +0.7593; range [1.000, 1.797], mean 1.299 | compressed almost onto 1; near-count |
| `residual_spread` | **+0.4087**; range [0.0000, 0.7486], mean 0.3304 | a genuinely different quantity — and it still lost |

So the family's one non-degenerate member was measured on its own terms and did not carry aspect
information that the chunk count did not already carry.

---

## Inputs and provenance

| Input | Path | Provenance |
|---|---|---|
| Top-50 ranking (run B) | `~/repositories/iverson-benchmark-corpora/chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.trec` | md5 `8e81aadde8500bb95e70d76737c66d0c`; V24 — set-identical per query to `aspect-oracle-2026-09-13/oracle-A.trec` on 672/672 |
| Chunk-hit pool | `…/chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv` | md5 `dd7ad7b4b7cab7858027afa2f4c8e0a3`; `queryId parentKey rank score`, parents not chunks (V20) |
| Query text | `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/beir/queries.jsonl` | md5 `1f749b6b981321e77f54df6350c9c295` |
| Aspect labels | `…/freshstack-2048-2026-09-07/qrels.nugget.trec` | md5 `04dcc6f51f3d8cf92ac69852074f3c23` |
| Key map | `…/freshstack-2048-2026-09-07/keymap.json` | md5 `8aa843cd1f34f885ff40869403209083`; 6,000 entries, 0 unresolved |
| Chunk vectors | Qdrant `benchmark_documents_chunks_tenant_bypass` | 18,622 points, `body_vector` 768-d Cosine, status green |
| Centroid vectors | Qdrant `benchmark_documents_tenant_bypass` | 6,000 points, `body_centroid` 768-d Cosine, status green |
| Query vectors | TEI `BAAI/bge-base-en-v1.5` on `:8091` | `/info` reports `BAAI/bge-base-en-v1.5`, 768-d, TEI 1.8.3 |
| Instrument | `Iverson.Server/Iverson.LoadTest/scripts/aspect_vectors.py` @ `b3c1201` | 54 pytest tests pass offline |

**Outputs**, all written to `~/repositories/iverson-benchmark-corpora/family2-vector-screen-2026-09-13/`:

| File | bytes | md5 |
|---|---|---|
| `faithfulness.txt` | 1,305 | `5102ef461d37f2676c347e8595163660` |
| `pair-signals.tsv` | 273,620 | `c10eedf54eb277a677a91012677121ab` |
| `screen.txt` | 2,103 | `39c29e339980b5182140566964c1e7bb` |

> **Every figure in this document is transcribed from `faithfulness.txt`, `screen.txt` and
> `pair-signals.tsv` in that corpora directory. That directory is _not_ a git repository** — it is
> un-versioned bulk data. Those three files are the only record of these numbers; nothing inside this
> repository reproduces them without re-running the instrument against the same inputs with Qdrant and TEI
> up. The md5s above are pinned so a later reader can confirm the file they are reading is the file these
> figures came from.

Two containers were started, each **single-service with `--no-deps`** (Global Constraint 1): `qdrant` and
`tei-embed`. **`iverson-api` was never started and never built** (Global Constraint 2). No server code was
touched by this task, no arm was retrieved, and no metric was scored.

---

## The faithfulness check — PASSED, and it is what makes the rest trustworthy

Spec §2.7's reconstruction ran first, over the screened population's `(queryId, parentKey)` groups, and
was read before any ρ (plan Step 4). From `faithfulness.txt`:

```
rows checked                      : 9184
rows with no match                : 0
rows with an ambiguous match      : 0
rows with a duplicate match       : 0
maximum residual over matched rows: 1.670942e-07
verdict: PASS
```

Match tolerance 5.000e-07. Every one of the 9,184 recorded fused scores was reproduced from
`(W_base · cos(q, chunkVector) + W_centroid · cos(q, parentCentroid)) / (W_base + W_centroid)` and assigned
to **exactly one** of its parent's chunks. The unmatched, ambiguous and duplicate counts are the falsifying
statistics and all three are zero; the maximum residual is a selection artifact bounded by the tolerance by
construction and is **not** evidence of anything (spec §2.7).

A clean pass here exercises, in one comparison, the query prefix, the model identity, the chunk vectors,
the `parent_id` payload lookup, the centroid vectors and the fusion constants — and does so *by assigning*
chunk vectors rather than merely reading them. In particular it is the live confirmation that Task 1's
contract extension is correct: the instrument resolved
`'Represent this sentence for searching relevant passages: '` from `ingest.query_prefix_for()`, and a wrong
or absent prefix would have produced query vectors that reproduce no recorded score at all.

### What the reconstruction does and does not say about the fusion weights

Spec §5 says E7 "is the only check that they were 0.45/0.45". **That over-claims, and the weaker statement
is the true one.** The fused expression divides by `(W_base + W_centroid)`, so at any **equal** pair of
weights it collapses to the plain arithmetic mean of the two cosines: 0.45/0.45, 0.50/0.50 and 1.00/1.00
are numerically indistinguishable to this check. What the 9,184-for-9,184 reconstruction establishes is
that **`W_base` and `W_centroid` were equal** when the dump was produced, and that `W_decay` contributed
nothing (V14 — `BenchmarkDocument` carries no timestamp). It does not, and cannot, recover their common
value. `VectorRankingOptions.cs:14-16` remains the only source for that, and it is a code default rather
than a record of what this run used.

### The restore that was not performed, and why the result is not weaker for it

Spec §2.6 and plan Step 2 call for restoring both snapshots from
`freshstack-2048-qdrant-snapshots` before the run. **That step was skipped by operator ruling.** Qdrant
came up on a preserved volume that already held both collections at exactly the counts, vector names and
distance metric that preconditions E1, E2, E3 and E5 assert — 18,622 / 6,000, `body_vector` /
`body_centroid`, 768-d, Cosine, both green — and restoring over them would have overwritten the very state
the faithfulness check was about to test.

This is recorded as a deviation, not hidden. Its effect on the result is the opposite of a weakening: V25
argued from snapshot timestamps that the restored collections *would be* the state the dump was produced
from, whereas the faithfulness check **measured** it — 9,184 exact reconstructions against a dump produced
by a different process in a different language is not a property the wrong collection state could have. The
plan's own contingency (stop and report BLOCKED, then restore as the diagnostic path) was not needed
because the check passed.

---

## The screened population — confirmed against the pre-registration before any ρ was read

Plan Step 5, verified directly from `pair-signals.tsv` rather than taken from the instrument's own banner:

| Quantity | Observed | Pre-registered | |
|---|---|---|---|
| rows | **2,733** | 2,733 | ✔ |
| distinct query ids | **610** | 610 | ✔ |
| distinct (query, doc) pairs | **2,733** | 2,733 | ✔ |
| recorded rows reconstructed | **9,184** | 9,184 | ✔ |
| `n_chunks` distribution | **{1:450, 2:400, 3:487, 4:615, 5:674, 6:106, 7:1}** | V2, identical | ✔ |

No `*** WARNING: the screened population is NOT the pre-registered one ***` line appears in either
`faithfulness.txt` or `screen.txt`. The top-50 restriction took: the unrestricted join would have given
4,360 pairs over 653 queries (V24).

Two further §3 assumptions reproduced exactly on the live data:

- **V3** — 1,258 pairs (46.0 %) carry ≥ 2 aspects; **1,075** (39.3 %) carry ≥ 2 aspects **and** ≥ 2 chunks.
- **V4** — `n_chunks` ρ against aspect count is **+0.0714**, reproducing the figure the aspect-coverage
  gate's amendment recorded for the same signal over the same 2,733 pairs, to four decimals, by a separate
  instrument built for this screen. The null was not moved to suit the result.

Aspect distribution over the primary: {1:1475, 2:788, 3:365, 4:80, 5:19, 6:5, 7:1}. Chunk vectors and
centroids were scrolled for the 2,022 distinct parents the population touches.

---

## Sensitivity population — reported alongside; **the primary decides**

Spec §2.3: the 1,075 pairs with ≥ 2 aspects **and** ≥ 2 chunks, the sub-population where a spread signal
can discriminate at all. Observed at 1,075 pairs over 470 queries. It is **descriptive, outside the
pre-registered family, carries no Holm adjustment and decides nothing.**

Null on this sub-population: **`n_chunks` ρ = −0.0203** — the count signal inverts once single-chunk and
single-aspect pairs are removed.

| Candidate | ρ | diff vs null | 95 % CI on the difference | boot. p (unadjusted) |
|---|---|---|---|---|
| `residual_spread` | +0.0110 | +0.0313 | [−0.0709, +0.1296] | 0.5599 |
| `greedy_cover` (τ = 0.90) | −0.0145 | +0.0058 | [−0.0232, +0.0335] | 0.6901 |
| `effective_rank` | +0.0025 | +0.0228 | [−0.0424, +0.0859] | 0.5093 |

**This is the one place any candidate is nominally ahead of its null, and it is nothing.** All three point
differences are positive here only because the null goes negative; every CI straddles zero by a wide
margin, every unadjusted p exceeds 0.50, and the largest effect (`residual_spread`, +0.0313) has a CI four
times its own width. Under §2.4's rule it would fail on this population too, before any Holm adjustment.
The primary decides and the primary says FAIL; this is recorded because the spec required it to be, not
because it qualifies the verdict.

### The inert 16.5 % is not what sank `residual_spread`

Spec §5 records that single-chunk pairs take a constant on every candidate and can only dilute — 450 of
2,733 pairs, 16.5 %, exactly as predicted, and `residual_spread` is 0 on all 450. Removing them does not
rescue it. Over the 2,283 pairs with ≥ 2 chunks, `residual_spread` against aspect count is **+0.0194**
while `n_chunks` over that same subset is **+0.0376** — the candidate is still behind its null by roughly
the same relative margin. The dilution caveat is real and it is not the explanation.

---

## ρ(τ) for `greedy_cover` — published whatever the result (spec §2.2, Global Constraint 3)

τ steered the outcome of the coverage-term screen's `n_within_tau`, so the whole curve goes into the record
rather than the pre-registered point alone.

| population | τ = 0.80 | τ = 0.85 | **τ = 0.90 (pre-registered)** | τ = 0.95 |
|---|---|---|---|---|
| primary (2,733) | +0.0537 | +0.0716 | **+0.0732** | +0.0719 |
| sensitivity (1,075) | +0.0117 | +0.0062 | **−0.0145** | −0.0193 |

**τ = 0.90 is the argmax of the primary curve.** The pre-registered value is the most favourable of the
four for the candidate, and it still fails — so no post-hoc choice of τ available in this grid changes the
verdict. The curve's whole primary range, +0.0537 to +0.0732, sits within ±0.02 of the null's +0.0714; the
signal is insensitive to τ because it is not a threshold effect, it is a chunk count.

---

## Validity checks

- **The null reproduces.** `n_chunks` ρ = +0.0714 here matches the aspect-coverage gate amendment's
  recorded +0.0714 for the same signal on the same 2,733 pairs, computed by a separate instrument. The two
  share their input files and their Spearman implementation, so this is a check on the join and the
  population, not on the statistic itself.
- **The population reproduces exactly**, including V2's chunk-count distribution down to the single
  7-chunk pair, and V3's 1,258 / 1,075 split.
- **The reconstruction is sound at 9,184 / 9,184**, which is simultaneously the check on the prefix, the
  model, the payload lookup, the centroid resolution and the weight equality.
- **The bootstrap resamples queries, not pairs** (Global Constraint 4), so the 610 query blocks are the
  independent units; Task 2's suite pins this falsifiably.
- **Holm family size is 3** and was fixed before the run; the instrument refuses any other size.
- **Global Constraint 6 held**: the instrument restored nothing and wrote only into the dated corpora
  directory.

### Vectors bought nothing over scalars — the comparison the screen existed to make

The aspect-coverage gate's probe 1 measured six **score-derived** signals over this identical 2,733-pair
population, topping out at `spread` = **+0.0790**. The best family-2 candidate here reaches **+0.0732**,
and the best non-degenerate one **+0.0578**. Chunk vectors, the query vector, the centroids and the true
geometry of the match are all available to these three candidates and to none of those six — and the
ceiling did not move. Spec §1's argument was that a scalar score per chunk cannot say *which* part of the
query a chunk answered, and that vectors could. The first half of that stands. **The second half is what
this screen measured, and it did not hold on this corpus.**

---

## What this result does and does not license

**Licensed.** Ranked-changes item 15's remaining open branch closes. The family the item was written to
protect — "a coverage term whose value does not collapse onto the count" — has now been screened on the one
axis that was left, and its three natural constructions do not separate from the count. Item 15's closure
no longer rests on measurability alone.

**Not licensed.** No claim that chunk-vector geometry is inert in general, and no claim that aspect
coverage is unreachable. This screen tested three definitions of spread on one corpus under one model; a
different definition of "distinct aspect" — the thing the gate amendment noted nobody has pinned down —
is not refuted by it. What is refuted is the specific proposition that *these* spread signals proxy aspect
count better than a chunk count does.

**And a pass would not have licensed building a term either.** This must be said even though the screen
failed, because it is the condition on the whole line of work: the aspect-coverage gate's own conversion
figure (3.63 %, reproduced from `rerank-a0`/`rerank-a1`) puts a **realised** aspect-coverage term at
**≈ +0.002 α-nDCG@10 against a measured MDE of 0.0097** on this corpus. A clean pass here would have told
us a signal exists and that vectors carry what scores do not; it would **not** have predicted a detectable
gain, and the term would still have been below the measurement floor. The FAIL removes a candidate; it does
not change that arithmetic, which was already the binding constraint.

### Known limits, carried forward from spec §5

- **A pass would not have made a term worth building** — the ≈ +0.002 vs MDE 0.0097 arithmetic above. It
  binds whatever signal a term is built on.
- **16.5 % of the primary population is structurally inert.** 450 of 2,733 pairs have exactly one chunk
  and take a constant on every candidate. Confirmed as predicted, and shown above not to be the cause of
  `residual_spread`'s failure.
- **`greedy_cover` and `effective_rank` are bounded above by `n_chunks` by construction**, so their
  failure is weaker evidence against the family than `residual_spread`'s. Measured here at ρ = +0.9493 and
  +0.7593 with the null respectively, with `greedy_cover` numerically equal to `n_chunks` on 88.8 % of
  pairs. **`residual_spread` failed too, so the strong case was tested and the mitigation does not apply.**
- **One corpus, one window, one model.** This binds FreshStack-2048 under bge-base at 2048/1792. No other
  corpus in this project carries subtopic labels.
- **The fusion weights are not recorded in the dump's sidecar** (the same gap as λ, ranked-changes item 16).
  The reconstruction confirms they were **equal**, not what they were — see above.
- **Query vectors are reproduced, not recovered.** V27: `EmbeddingService.cs:117` narrows each component to
  `float` while `ingest.embed` keeps the JSON double, so the instrument's query vector is not bit-identical
  to the server's. The maximum residual of 1.67e-07 is that error made visible, and it sits comfortably
  inside both the 5e-7 tolerance and the screened population's 8.94e-07 minimum within-group separation.

## What shipped

**Harness only.** `Iverson.Server/Iverson.LoadTest/scripts/aspect_vectors.py` and its 54-test suite, plus
one contract extension that removes a live cross-language hazard rather than adding one: `queryPrefixes`
and `defaultQueryPrefix` in `ingest-contract.json`, emitted by `IngestContractTests` from the same
`EmbeddingPrefixes.Table` that already feeds the document side, with `ingest.query_prefix_for()` mirroring
`document_prefix_for()`. That change stands on its own merits and is independent of this verdict — before
it, a query embedded Python-side was not the vector retrieval uses, for bge-base and for every other model
in the table.

No `VectorRankingOptions` constant, no ranking code, no configuration change, no server behaviour change.
