# Critical Implementation Review: 2026-08-31-decay-weight-sensitivity-implementation-plan (Round 3)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-decay-weight-sensitivity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 7 commits since plan-write time (SHA 18fd90a); cited file:line references re-checked under §1.

## 0. Coverage enumeration

**Task 1 × surfaces**

| row | disposition |
|---|---|
| T1 step prose — scratch-only, no commit | ok — Step 7 restores all four modified files |
| T1 Step 1 command | ok — branch created from clean `main`, unwound in Step 7 |
| T1 Step 2 code | ok — fresh recount: 13 `new RerankCandidate(` sites; trailing defaulted parameter keeps all compiling |
| T1 Step 3 code (similar) | ok — `payloadSelector: true` (`IntelligenceVectorService.cs:99`, `:130`) puts `key` on the search result |
| T1 Step 3 code (chunks) | ok — `parent_id` read and `KeyToUlong` match the existing lines |
| T1 Step 4 code (capture) | ok — `_callIndex` from -1 yields 0 first; `System.Globalization` correctly the only using to add |
| T1 Step 5 commands | ok — build/deploy/model-check match `crossover.sh` |
| T1 Step 6 loop — sed + guard | ok — `= [0-9]+;` matches both states; `= $M;` guard cannot false-positive on `= 15;` |
| T1 Step 6 — `_callIndex` continuation | ok — 1,344 calls yield indices 0..1343, so run 2 starts at 1344, even, parity preserved |
| T1 Step 6 — flush before `docker cp` | ok — `File.AppendAllLines` closes per call |
| T1 Step 7 gate + restore | ok — per-arm gate; all four modified files restored |

**Task 2 × surfaces**

| row | disposition |
|---|---|
| T2 step prose — ages per `parentId` | ok — matches A20 |
| T2 Step 1 code (`fuse`) | ok — re-derived against `ResultReranker.cs:24-51` |
| T2 Step 1 code (`decay_for`, `SCENARIOS`) | ok — matches `DecayFieldResolver.cs:14,85`; numpy signatures correct |
| T2 Step 2 — `to_documents` collapse semantics | ok — max-per-document with unresolved parents dropped mirrors `MaxPassageAggregator.cs:17-20`, `:40-45` |
| T2 Step 2 — `to_documents` identifiers | → §2.1 (`DOCUMENT_BUDGET`); `rows`/`fused`/`keymap` are parameters, `best`/`doc`/`s` locals, `r.parent_id`/`r.candidate_id` map to the captured `parentId`/`candidateId` columns — all resolve |
| T2 Step 2 — "no-op on the similar path" claim | ok — verified on real data: `keymap.json` has 6,000 entries and 6,000 **distinct** docIds, zero duplicates, so `CollapseByDocId` is genuinely a no-op here. (`DocumentRanking.cs`'s own comment says two entities *can* share a docId when a corpus is ingested twice; that is not this corpus.) |
| T2 Step 2 — docId source parity with the harness | ok — the harness resolves the similar path's docId from the gRPC `DocId` data field (`BenchmarkQueryScenario.cs:164-172`), while the replica goes `parentId` → `keymap`. Checked 200 live object points: `keymap[payload.key] == payload.docId` for **200/200**, zero keys missing from the key map. The two routes agree |
| T2 Step 2 — `uniform` control under the new collapse | ok — verified the collapse cannot break the control: at constant decay, A is a strictly increasing affine function of B (slope 1.010101, constant across the range), so candidate order, the argmax per document, and hence document order are all preserved |
| T2 Step 2 — `callIndex` normalisation | ok — `(callIndex - min) // 2` correct given run 2 starts even |
| T2 Step 2 — nDCG@10 on the collapsed ranking | ok — collapse removes the repeated-docid problem, so the derived run is now well-formed |
| T2 Step 3 decision rule | ok — per-arm application and the disagreement clause are internally consistent |
| T2 Step 4 commit | ok — single path, plain-imperative message |

**Cross-task interface contracts**

| row | disposition |
|---|---|
| T1 produces two captures → T2 consumes both (**persistence boundary**) | ok — names consistent across File Structure, Produces and Consumes; emitted columns match those the replica reads |
| T2's `to_documents` ← `keymap.json` | ok — named in Consumes; 6,000 entries covering all 6,000 objects |
| T2's scoring operation ← collapsed ranking (**replica of the harness's call site**) | ok — now performs the same collapse the harness does; this was round 2's finding and the fix reproduces both aggregators' semantics |

**Rule-like content**

| row | disposition |
|---|---|
| `hasCentroid` branch selection, both directions | ok — mirrors `ResultReranker.cs:20` |
| Identity rule: `parentId` as age key **and** docId key | ok — one key space across endpoints; keymap resolves both; no conflation (6,000 keys → 6,000 distinct docIds) |
| Collapse rule: max-per-document, both directions | ok — over-merge impossible (docIds are distinct); under-merge matches the harness, which also drops key-map-unresolved parents |
| Control-assertion rule ("non-zero control means the implementation is wrong") | dropped — the zero-change guarantee holds only while every candidate takes one fusion branch: the centroid-present slope is 1.010101 and the centroid-absent slope 1.018519, so a *mixed* result set could reorder at constant decay and the stated diagnosis would misattribute it. A18 (inherited, trusted) makes the set unmixed, and the plan's own `hasCentroid` column is already designated as the check on A18. Fails literal-wrongness under trusted ground truth |

## 1. Verified-plan-assumptions cross-check

P1 (14 / 61 lines), P2 (13 sites), P3, P4, P5 (3 command forms in `crossover.sh`), P6, P7 (`/tmp` writable), P8, P9 (`fatal: not a git repository`), P10, P11 (plain-imperative log), P12, P13 — **all still hold** under fresh reads this round.

P12 and P13 re-verified against live data rather than only source: `keymap[payload.key] == payload.docId` on 200/200 sampled object points, and both aggregators' doc comments re-read.

**Span check:** no uncovered dependency. The dependency round 2 surfaced — "the captured ranking is the ranking that qrels score" — is now covered by P13 and by the `to_documents` step itself.

## 2. Literal-wrongness findings

### 2.1 `DOCUMENT_BUDGET` is undefined in the plan

**Description.** The `to_documents` block ends with:

```python
    return sorted(best.items(), key=lambda kv: -kv[1])[:DOCUMENT_BUDGET]
```

`DOCUMENT_BUDGET` is never defined or given a value anywhere in the plan. Its only other appearance is inside a quoted C# expression about a different constant (`.TopK((uint)(DocumentBudget * ChunkBudgetMultiplier))`, line 174), which states the multiplier's role but not the budget's value. Executed as written the script raises `NameError` at the first call.

The value is not guessable from the plan: it lives in the harness at `BenchmarkQueryScenario.cs:37` as `private const int DocumentBudget = 50;`, and the plan never cites that line.

**Evidence.** `grep -n "DOCUMENT_BUDGET\|DocumentBudget"` over the plan returns exactly two hits — line 174 (the C# quotation) and line 283 (the undefined use). `BenchmarkQueryScenario.cs:37` holds the actual value.

**Proposed fix.** Define it alongside the other offline constants in Task 2 Step 1, with the citation that fixes its provenance:

```python
DOCUMENT_BUDGET = 50        # BenchmarkQueryScenario.cs:37 — the harness's DocumentBudget
```

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **R2 §2.1** (replica scored a ranking the system never produces) — resolved: Task 2 Step 2 now collapses to document level via `to_documents` before every metric, mirroring `MaxPassageAggregator` and `CollapseByDocId`, and nDCG@10 is computed on that ranking. P13 covers the dependency.
- **R2 §1** (P10's stale evidence) — resolved: P10's evidence now names the two capture files, `keymap.json` and `qrels.trec`.
- **R1 §2.1** (similar-path `ParentId` unjoinable) and **R1 §3.1** (chunk budget unstated) — remain resolved; re-confirmed this round against live data and the two-arm Step 6 loop.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty.
