# Critical Implementation Review: 2026-08-31-decay-weight-sensitivity-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-decay-weight-sensitivity-implementation-plan.md
**Verified plan-level assumptions section:** present

## 0. Coverage enumeration

**Task 1 × surfaces**

| row | disposition |
|---|---|
| T1 step prose — scratch-only, no commit | ok — consistent with Step 7's restore; no commit step is deliberate and stated |
| T1 Step 1 command (`git checkout -b`) | ok — branch is cosmetic (changes stay in the working tree) but harmless; Step 7's `git checkout --` + `branch -D` unwinds it |
| T1 Step 2 code (`RerankCandidate`) | ok — trailing defaulted `string? ParentId = null`; re-counted 13 construction sites, 11 in `ResultRerankerTests.cs` all named-argument, so all still compile |
| T1 Step 3 code (similar path) | → §2.1 |
| T1 Step 3 code (chunks path) | ok — `r.Payload.TryGetValue("parent_id", …)` and `IntelligenceStoreConsumer.KeyToUlong` both already used verbatim at `ObjectSearchGrpcService.cs:444-445`; a live chunk point confirms `parent_id` = `966ded21-0d91-5f57-b309-fc8e71ca6e48`, a Guid |
| T1 Step 4 code (capture) | ok — `Interlocked` / `File` covered by `ImplicitUsings` (both csproj, net10.0); `string.Create(IFormatProvider, ref DefaultInterpolatedStringHandler)` exists on net10.0; `_callIndex` starts at -1 so the first call is 0, and Step 5's redeploy resets the process before Step 6 runs |
| T1 Step 4 — lock placement | ok — one `File.AppendAllLines` per `Rerank` call under `CaptureLock`, before the `return`; keeps a call's rows contiguous and satisfies A13 |
| T1 Step 5 commands | ok — `docker compose build iverson-api`, `stack.py query`, and the `EmbeddingService initialized` grep all match `crossover.sh:46-47` |
| T1 Step 6 commands | ok — flags match `BenchmarkQueryScenario.cs:46-62`; `docker exec … rm -f` then `docker cp` both valid against container `iverson-api` |
| T1 Step 6 — **candidate-pool size** | → §3.1 |
| T1 Step 7 gate | ok — `cut -d, -f1 | sort -u | wc -l` yields distinct call indices; 2 x 672 = 1,344 is right; `grep -c … || echo` handles grep's exit-1-on-no-match correctly |
| T1 Step 7 — zero-candidate call would also read 1,343 | ok — A17 (inherited) establishes 50 results for every query on both endpoints, so no `Rerank` call sees an empty list on this corpus |
| T1 Step 7 restore | ok — `git checkout --` on the three modified paths, then `checkout main` + `branch -D`; rebuild/redeploy restores the shipped image |

**Task 2 × surfaces**

| row | disposition |
|---|---|
| T2 step prose — ages keyed by `parentId` | ok — matches A20; keying per candidate is explicitly forbidden in the prose |
| T2 Step 1 code (`fuse`) | ok — re-derived against `ResultReranker.cs:24-51`: short-circuit when neither centroid nor decay is present, otherwise weighted mean over present signals only. Matches |
| T2 Step 1 code (`decay_for`) | ok — `min(1.0, 0.5 ** (age/180.0))` mirrors `DecayFieldResolver.cs:85` including the clamp |
| T2 Step 1 code (`SCENARIOS`) | ok — numpy `Generator.uniform(low, high, n)` and `Generator.choice(seq, n)` signatures correct; `uniform` control is a constant list, which the algebra says must reorder nothing |
| T2 Step 2 — set-change / displacement / Kendall tau | ok — computable from `callIndex` grouping alone; needs no external identifier |
| T2 Step 2 — **nDCG@10 against qrels** | → §2.1 |
| T2 Step 3 decision rule | ok — thresholds and scenario names match the spec verbatim |
| T2 Step 4 commit | ok — `git add` names one path, message is plain imperative matching `git log --oneline -20` |

**Cross-task interface contracts**

| row | disposition |
|---|---|
| T1 produces `fusion-capture.csv` → T2 consumes it (**persistence boundary**) | → §2.1 — the consuming nDCG operation requires a BEIR docId, and no column of the written artifact yields one for half the rows |
| T2's qrels operation ← `qrels.trec` | ok — `freshstack-chunk256-2026-08-30/qrels.trec` present (1.5 MB) |
| T2's age-assignment operation ← `parentId` column | ok — chunks rows carry a Guid that keys `keymap.json`; similar rows carry a different key space, but each endpoint's A-vs-B comparison uses internally consistent ages, so age assignment itself is unaffected |

**Rule-like content**

| row | disposition |
|---|---|
| `callIndex` parity rule (`2i` similar, `2i+1` chunks), over- and under-count | ok — `BenchmarkQueryScenario.cs:95-96` calls `RunSimilarAsync` then `RunChunksAsync` per query, so parity holds; the Step 7 gate catches the under-count direction |
| `hasCentroid` branch-selection rule, both directions | ok — mirrors `ResultReranker.cs:20`'s compound test (non-null **and** length match); A18 predicts it is uniformly 1 here, and the column doubles as a check on that |
| Identity rule: `parentId` as the age key | → §2.1 — the two endpoints populate it from different key spaces |

## 1. Verified-plan-assumptions cross-check

P1, P3, P4, P5, P6, P7, P8, P10, P11 — **all still hold** under fresh reads this round (paths re-stat'd; `stack.py:84`/`:214`; `BenchmarkQueryScenario.cs:46-62`; `docker ps`; `docker exec … id`; corpus listing; `git log --oneline -20`).

P2 — **still holds.** Re-counted: 13 `new RerankCandidate(` sites, 11 of them named-argument test constructions.

P9 — **still holds.** `git -C iverson-benchmark-corpora rev-parse` → `fatal: not a git repository`.

**Span check — uncovered dependencies:**

1. *"Task 2 can map a captured row to the BEIR docId its qrels use."* No assumption covers this, and neither the plan's Consumes list nor the capture's columns provide it. **Verified in-round and FALSE for the similar path** — see §2.1.
2. *"`keymap.json` is available to Task 2."* Not listed in Task 2's Consumes. Verified present on disk (6,000 entries) but the contract omits it; folded into §2.1's fix.
3. *"The candidate pool at capture time matches the pool the spec's packing statistic came from."* No assumption covers the budget constants. **Verified in-round and FALSE** — see §3.1.

## 2. Literal-wrongness findings

### 2.1 The similar path's `ParentId` is in a key space that cannot reach a BEIR docId

**Description.** Task 1 Step 3 sets, on the `SearchSimilar` path:

```csharp
ParentId:  r.Id.ToString(CultureInfo.InvariantCulture)
```

`r.Id` is the **Qdrant point id** — a synthesised ulong such as `2492450271043741`. Task 2 Step 2 then computes nDCG@10 against `qrels.trec`, whose document identifiers are BEIR `_id` strings, and `keymap.json` is keyed by **Guid**, not by point id:

```
'7c5a8e9b-82ef-5c17-91d6-7e55ac356209' -> 'TypeScript/tests/baselines/reference/1.0lib-noErrors.types_82577_90120'
```

So for every similar-path row the join has no key: the point id is not a `keymap.json` key, and `KeyToUlong` is one-way, so it cannot be inverted. The chunks path is fine — a live chunk point's `parent_id` is `966ded21-0d91-5f57-b309-fc8e71ca6e48`, exactly a `keymap.json` key.

The spec has a dedicated "Reported but explicitly not decisive" section requiring nDCG@10 to be recorded, so half of that requirement is unexecutable as written. Separately, Task 2's `Consumes` lists only `fusion-capture.csv`, omitting `keymap.json`, which the join needs even once the key space is corrected.

**Evidence.** Live object point payload from `benchmark_documents_tenant_bypass`:

```
point id: 2492450271043741
  key:   cfad0138-89de-5053-9da0-06cddeda0800
  docId: langchainjs/docs/core_docs/docs/how_to/debugging.mdx_160771_168252
```

`keymap.json` (6,000 entries) is Guid-keyed. `ObjectSearchGrpcService.cs:444-445` shows the chunks path already sourcing the Guid from the payload.

**Proposed fix.** Populate the similar path's `ParentId` from the object payload's `key` field rather than the point id, putting both endpoints in one key space and making both joinable through `keymap.json`:

```csharp
var candidates = results.Select(r => new RerankCandidate(
    Id:        r.Id,
    BaseScore: r.Score,
    Centroid:  centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
    Decay:     DecayFor(r, decayField, now),
    ParentId:  r.Payload.TryGetValue("key", out var k) ? k : null)).ToList();
```

`r.Payload` is already in scope at this call site — `DecayFor(r, decayField, now)` reads it. Add `keymap.json` to Task 2's `Consumes` list.

(The object payload also carries `docId` directly, which would skip `keymap.json` entirely; using `key` is preferred only because it keeps the two endpoints in a single key space, which the age-assignment step also reads.)

## 3. Forced decisions

### 3.1 `ChunkBudgetMultiplier` is 5 at capture time, but the spec's significance argument rests on multiplier-20 data

**The choice.** Which chunk budget the capture run uses.

**Why it's forced.** `BenchmarkQueryScenario.cs:37-38` currently reads `DocumentBudget = 50`, `ChunkBudgetMultiplier = 5` — the crossover script restored the shipped value when it finished. The plan does not mention the constant, so executing it as written captures at multiplier 5.

That is a different regime from the one the spec's argument is built on. Every run behind the spec's "median adjacent top-10 score gap is 0.00173" statistic — the number the ±0.00909 bound is compared against to justify running this experiment at all — was taken at multiplier 20. At 5.66 chunks/document a 250-chunk budget reaches only ~44 documents against a `DocumentBudget` of 50, which is the same under-budgeting the crossover explicitly raised the multiplier to avoid.

The A-vs-B comparison remains internally valid at either value, which is why this is a decision rather than a defect — but the decision rule's ">= 99% of top-10 sets unchanged" would be evaluated on a differently-packed candidate pool than the one that motivated the threshold.

**The options.**

- **(a)** Set `ChunkBudgetMultiplier = 20` for the capture run (and restore it in Step 7 alongside the other edits), matching the conditions the spec's packing statistic and bound comparison came from.
- **(b)** Capture at the shipped value of 5, and state in the outcome that the sensitivity result describes the shipped budget rather than the crossover's regime.
- **(c)** Capture both and report the decision rule against each, at the cost of a second ~30-minute run.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty (§2 also carries one finding).
