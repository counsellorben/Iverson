# Critical Implementation Review: 2026-08-31-decay-weight-sensitivity-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-decay-weight-sensitivity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 4 commits since plan-write time (SHA 18fd90a); cited file:line references re-checked under §1.

## 0. Coverage enumeration

**Task 1 × surfaces**

| row | disposition |
|---|---|
| T1 step prose — scratch-only, no commit | ok — Step 7 restores all four modified files; consistent |
| T1 Step 1 command | ok — `git checkout -b` from a clean `main`; Step 7 unwinds |
| T1 Step 2 code | ok — trailing defaulted `ParentId`; fresh recount confirms 13 construction sites, all still compiling under a defaulted trailing parameter |
| T1 Step 3 code (similar) | ok — `payloadSelector: true` at `IntelligenceVectorService.cs:99`/`:130` means the full payload is retrieved, so `key` is genuinely present on the search result, not only on a scroll |
| T1 Step 3 code (chunks) | ok — `parent_id` read and `KeyToUlong` call both match the existing lines verbatim |
| T1 Step 4 code (capture) | ok — `Interlocked`/`File` implicit on net10.0; `System.Globalization` correctly called out as the one using to add; `_callIndex` starting at -1 yields 0 for the first call |
| T1 Step 4 — lock scope | ok — one append per `Rerank` call under `CaptureLock`, before the return |
| T1 Step 5 commands | ok — build/deploy/model-check unchanged and still matching `crossover.sh:46-47` |
| T1 Step 6 loop — sed pattern | ok — `= [0-9]+;` matches the current `= 5;` and the post-run `= 20;`; the `grep -q "… = $M;"` guard cannot false-positive on `= 15;` because the `= ` prefix is part of the match |
| T1 Step 6 loop — absolute paths / re-entrant `cd` | ok — `OUT`, `CORPUS`, `BQ` and the `cd` target are all absolute, so the second iteration is unaffected by the first's `cd` |
| T1 Step 6 — `break` on guard failure | ok — leaves the loop with the constant possibly edited, but Step 7's restore is a separate block that still runs |
| T1 Step 6 — `_callIndex` continuation claim | ok — run 1 issues 1,344 calls returning indices 0..1343, so run 2's first index is 1344; even, so `2i`/`2i+1` parity survives exactly as the prose claims |
| T1 Step 6 — capture flush before `docker cp` | ok — `File.AppendAllLines` opens and closes per call, so no buffered tail is lost at run end |
| T1 Step 7 gate | ok — per-arm; 1,344 arithmetic correct; `grep -c … || echo` handles grep's exit-1 |
| T1 Step 7 restore | ok — all four modified files listed, including `BenchmarkQueryScenario.cs`; rebuild+redeploy restores the shipped image |

**Task 2 × surfaces**

| row | disposition |
|---|---|
| T2 step prose — ages per `parentId` | ok — matches A20; per-candidate keying explicitly forbidden |
| T2 Step 1 code (`fuse`) | ok — re-derived against `ResultReranker.cs:24-51`; short-circuit and present-signals-only accumulation both match |
| T2 Step 1 code (`decay_for`, `SCENARIOS`) | ok — formula, half-life and clamp match `DecayFieldResolver.cs:14,85`; numpy `Generator.uniform`/`choice` signatures correct |
| T2 Step 2 — top-10 set-change metric | → §2.1 |
| T2 Step 2 — nDCG@10 against qrels | → §2.1 |
| T2 Step 2 — `callIndex` normalisation rule | ok — `(callIndex - min) // 2` with even=similar is right given run 2 starts at an even index |
| T2 Step 3 decision rule | ok — thresholds match the spec; per-arm application and the disagreement clause are internally consistent |
| T2 Step 4 commit | ok — single path, plain-imperative message matching `git log --oneline -20` |

**Cross-task interface contracts**

| row | disposition |
|---|---|
| T1 produces two capture files → T2 consumes both (**persistence boundary**) | ok — both named consistently in File Structure, Produces and Consumes; columns listed match Step 4's emitted row |
| T2's docId-join operation ← `parentId` + `keymap.json` | ok — `payloadSelector: true` guarantees `key` on similar rows; chunk rows carry `parent_id`; `keymap.json` is Guid-keyed |
| T2's qrels-scoring operation ← the captured ranking (**second caller of the same scoring concept as the harness**) | → §2.1 — the harness's TREC output is document-level after a client-side collapse the offline replica does not perform |
| T2's decision-rule operation ← the top-10 set | → §2.1 — same root cause |

**Rule-like content**

| row | disposition |
|---|---|
| `hasCentroid` branch selection, both directions | ok — mirrors `ResultReranker.cs:20`'s compound null+length test |
| Identity rule: `parentId` as both age key and docId key | ok — one key space across endpoints after round 1's fix; `keymap.json` resolves both |
| Aggregation rule: captured ranking → scored ranking | → §2.1 — the plan states no aggregation step, but the system has one on both endpoints |

## 1. Verified-plan-assumptions cross-check

P1, P2, P3, P4, P5, P6, P7, P8, P9, P11, P12 — **all still hold** under fresh reads this round. Spot-notes:

- **P2** reconfirmed by fresh recount: 13 `new RerankCandidate(` sites.
- **P3** reconfirmed: 4 `requires --` guards in `BenchmarkQueryScenario.cs`.
- **P6** reconfirmed: `docker ps` shows exactly one `iverson-api`.
- **P12** reconfirmed and *strengthened*: round 1 cited a scroll result; this round confirms `payloadSelector: true` on the search path itself (`IntelligenceVectorService.cs:99`, `:130`), so `key` reaches `r.Payload` in the code path the plan actually modifies.

**P10 — still holds, with stale evidence.** The assumption ("Task 2 consumes Task 1's capture; there is no reverse dependency") is true. Its evidence sentence — "Task 2 reads only `fusion-capture.csv` and `qrels.trec`" — is now outdated: Task 2's Consumes line lists two capture files plus `keymap.json`. Nothing breaks; the ordering claim is unaffected.

**Span check — uncovered dependencies:**

1. *"The captured ranking is the ranking that qrels score."* No assumption covers this, and no "Inherited from spec" item states it. **Verified in-round and FALSE** — see §2.1.

## 2. Literal-wrongness findings

### 2.1 The offline replica scores the captured ranking directly, but the system collapses to document level before anything is scored

**Description.** Task 2 Step 2 instructs: group captured rows by `callIndex`, "score both triples, sort descending", then take the top-10 set and compute nDCG@10 against `qrels.trec`.

The captured rows are what `ResultReranker.Rerank` sees — **chunk** candidates on the `SearchChunks` path and **object** candidates on `SearchSimilar`. The harness does not score those. It post-processes them client-side first:

- `BenchmarkQueryScenario.cs:209` — `MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget)`, documented as collapsing "a stream of chunk-level search results down to one row per parent document (max-passage aggregation): the parent's score is the maximum score among its chunks". It also **drops** parent keys absent from the key map.
- `BenchmarkQueryScenario.cs:184` — `DocumentRanking.CollapseByDocId(results, DocumentBudget)`, max score per docId.

Only after those collapses is a TREC run written, and `qrels.trec` is document-level throughout.

So the offline replica, as written, produces two wrong things for the chunks arm:

1. **nDCG@10 is not computable as specified.** A top-10 taken over chunks contains sibling chunks of the same document — at 5.66 chunks/document, a 250-chunk request spans only ~44 distinct parents and a 1,000-chunk request ~177. The derived run therefore repeats docids inside the top-10, which is not a valid TREC run and silently mis-scores against document-level qrels. The spec has a dedicated "Reported but explicitly not decisive" section requiring this number, so that requirement is unexecutable as written.
2. **The decision rule's "top-10 set" is measured on the wrong unit.** The rule the spec fixes is about the result set a caller sees, which is document-level. Chunk-level top-10 stability does not imply document-level top-10 stability — a document's max-scoring chunk can sit outside the top-10 chunks and still determine its aggregated rank — so this is not merely a conservative proxy; it can move in either direction relative to the metric the rule is written against.

This is the "one row per call site" case: the harness and the offline replica are two callers of the same scoring concept, and the replica sources its input from a persisted artifact that sits at a different stage of the pipeline than the harness's.

**Evidence.** `BenchmarkQueryScenario.cs:184` and `:209`; `MaxPassageAggregator.cs:17-20` (the collapse's own doc comment) and `:40-45` (key-map resolution, unresolved parents excluded); `DocumentRanking.cs:15-27` (max-by-docId). Chunk density: 33,950 chunks / 6,000 documents = 5.66.

**Proposed fix.** Add an aggregation step to Task 2 Step 2, before any metric is computed, mirroring what the harness does — the capture already carries everything it needs:

```python
# collapse captured candidates to one row per document, exactly as the harness does
def to_documents(rows, fused, keymap):
    best = {}                                  # docId -> max fused score
    for r in rows:
        doc = keymap.get(r.parent_id)          # unresolved parents are dropped,
        if doc is None:                        # matching MaxPassageAggregator
            continue
        s = fused[r.candidate_id]
        if doc not in best or s > best[doc]:
            best[doc] = s
    return sorted(best.items(), key=lambda kv: -kv[1])[:DOCUMENT_BUDGET]
```

Compute the top-10 set change, rank displacement, Kendall tau **and** nDCG@10 on this document-level ranking for both triples. On the similar path the same function applies unchanged (it collapses to a no-op when each object maps to a distinct docId, which is what `CollapseByDocId` does there).

Note this makes the `parentId` column load-bearing for the metric itself, not only for age assignment — which the capture already supports.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **R1 §2.1** (similar-path `ParentId` in an unjoinable key space) — resolved: Step 3 now reads `r.Payload["key"]`, and this round confirms `payloadSelector: true` guarantees that field is present on the search path.
- **R1 §3.1** (chunk budget unstated; spec's statistic came from multiplier 20) — resolved as option (c): Step 6 captures at both 5 and 20, Step 7 gates each arm, and Task 2 Step 3 applies the decision rule per arm with an explicit instruction to report disagreement rather than pick.
- **R1 §1 span check** (docId-join dependency, `keymap.json` availability) — resolved: P12 added, and `keymap.json` is now named in Task 2's Consumes.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty.
