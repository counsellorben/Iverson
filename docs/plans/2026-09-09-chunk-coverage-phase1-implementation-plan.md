# Chunk-Coverage Signal — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-08-chunk-coverage-signal-design.md` (commit SHA: `4b3ee0a`)

**Goal:** Build the harness capability to measure a document's *pooled* chunk tail, then measure it — so Phase 2's β ladder can be calibrated from real numbers rather than derived from an artefact that does not contain them.

**Architecture:** Three harness additions in `Iverson.LoadTest` — a tail-sum aggregator beside the existing max-passage collapse, a raw-chunk-hit dump from the chunks query path, and an offline `benchmark-aggregate` command that replays the dump at a given β. Then one FreshStack-2048 run at chunk-budget multiplier 11, a β = 0 byte-identity check against the in-run file, and a stats script reporting the in-pool tail-depth histogram and the tail-score level `s`. No server change.

**Tech stack:** C# / .NET 10 (`net10.0`), xunit 2.9.3 + FluentAssertions 7.0.0, Python 3 + pytest for the analysis script.

---

## Global Constraints

Copied from the spec. Every task holds to these.

- **`β = 0` must reproduce today's ranking bit-for-bit** (spec §2). The aggregator short-circuits at `beta == 0` rather than relying on IEEE `max + 0.0 · tail == max` incidentally. This is what the Phase 1 identity check asserts; a task that breaks it invalidates the whole experiment.
- **The tail is capped at 3 chunks** (spec §2) — "up to the next 3 highest chunk scores of that doc".
- **The new aggregator must be a NEW function, not a modification of `CollapseByDocId`** (spec §3 item 1). `CollapseByDocId` is shared with the `SearchSimilar` same-`DocId` dedup path (`BenchmarkQueryScenario.cs:383`); changing it would silently alter `SearchSimilar` run files, which have no chunks and no tail.
- **Existing outputs must stay byte-unchanged.** `<label>.chunks.trec`, `<label>.similar.trec`, `<label>.meta.json` and `<label>.chunks.diversity.json` must be identical to what today's harness writes, or Phase 2 and every prior run stop being comparable.
- **Chunk-budget multiplier is 11** on every arm (spec §7, B18) — not the harness default of 5.
- **`LambdaChunks` = 1.00** on the pool run (spec §4:139 — "the primary arms run at `LambdaChunks` = 1.00, MMR off, the unconfounded test"). The shipped default is 0.70 (`docker-compose.yml:446`), and at 0.70 MMR *re-selects* the pool rather than re-ordering it (`ObjectSearchGrpcService.cs:543`, `:577`), evicting a document's own 2nd–4th chunks — the exact quantity Phase 1 measures. Getting this wrong does not fail loudly; it returns a plausibly shallow histogram.

## File Structure

**Create:**
- `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkHitDumpWriter.cs` — writes the raw per-hit dump
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkAggregateScenario.cs` — the offline replay command
- `Iverson.Server/Iverson.LoadTest/scripts/tail_stats.py` — histogram and `s` from the dump

**Modify:**
- `Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs` — add `CollapseByDocIdWithTail`
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — emit the dump
- `Iverson.Server/Iverson.LoadTest/Program.cs` — `--beta` / `--hits-path` flags, `DblFlag` helper, DI registration, dispatch case, help text

**Test:**
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs` — tail-aggregator cases (existing file)
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkHitDumpWriterTests.cs`
- `Iverson.Server/Iverson.LoadTest/scripts/test_tail_stats.py`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time across three critical-design-review rounds. **Not re-verified here.** Evidence for each is in spec §8.

| # | Claim (abbreviated) |
|---|---|
| B1 | `MaxPassageAggregator.Aggregate` is pure over `(ParentKey, Score)` |
| B2 | The harness receives several chunks per parent |
| B3 | Chunk scores are server-fused and cross-document comparable |
| B4 | Raw hits are discarded today |
| B5 | The dump's field set must be `(QueryId, ParentKey, Score)` to serve all three consumers |
| B6 | No server change is needed |
| B7 | The routed `.similar` byte-identity is max-only and does **not** extend to a tail aggregator |
| B8–B11 | `LambdaChunks` settable by env + restart; λ = 1.00 reduces MMR to `Take(topK)`; the MMR confound is real; snapshots exist for all four corpora |
| B12–B14 | Density figures reproduce the ingest records; only FreshStack has nugget qrels; SciFact's control is the per-document invariant, not the statistic |
| B15–B17 | `--baseline` Holm-corrects within each measure; `--pair` would reject these arms; α-nDCG computes on FreshStack |
| B18 | The chunk budget is 550 on every arm (multiplier 11 — a deliberate deviation from Tier 1's per-corpus policy) |
| B19 | `CollapseByDocId` has four production call sites and 8 tests; `:383` is the `SearchSimilar` dedup path |
| B21–B22 | All four corpora have keymap **and** qrels **and** snapshots; the ranking's decision scale on the primary arm |
| B23 | The tail-score level is **unmeasured** — no artefact holds raw chunk-hit scores. **This plan's Phase 1 is what measures it.** |
| B24 | `ChunkBudgetGuard` admits all four arms at multiplier 11 and refuses FreshStack-512 at 5 |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-09).

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Benchmark/DocumentRanking.cs`, `Scenarios/BenchmarkQueryScenario.cs`, `Program.cs`, `Benchmark/DocumentRankingTests.cs` all exist at the cited paths | `[ -f ]` check on all four: EXISTS |
| P2 | File path | `Scenarios/` and `scripts/` are the directories for new scenarios and analysis scripts | Both directories present; `scripts/` holds `report.py`, `similar_arms.py`, `multivector.py` |
| P3 | File path | No collision for the new files | No `*aggregate*` in `Scenarios/`, no `*tail*` in `scripts/` |
| P4 | Signature | `DocumentRanking.CollapseByDocId` has a 2-tuple and a 3-tuple overload; the 2-tuple delegates to the 3-tuple | `DocumentRanking.cs:15-21` and `:28` |
| P5 | Signature | `MaxPassageAggregator.Aggregate` returns `ChunkAggregation(Ranked, UnresolvedParentKeys)` and resolves parents via `keyMap` | `MaxPassageAggregator.cs:40-57` |
| P6 | Signature | `TrecRunWriter.WriteAsync(path, IEnumerable<(string QueryId, IReadOnlyList<(string DocId, double Score)> Ranked)>, runTag, ct)`; `runTag` is written as column 6 | `TrecRunWriter.cs:12-16`, `:31` |
| P7 | Signature | The per-query chunk list is `List<(string ParentKey, double Score, string Text)>`, built inside `RunChunksAsync` and not mutated after construction | `BenchmarkQueryScenario.cs:396-402`; later uses at `:413`, `:418`, `:424` are all non-mutating projections |
| P8 | Signature | `.meta.json` carries a `composite` key, alongside `configLabel`, `assemblies`, `recordedAtUtc`, `chunkBudgetMultiplier`, `reranker` | `BenchmarkQueryScenario.cs:204-220` |
| P9 | Signature | **`StrFlag` and `IntFlag` exist; there is NO `double` flag helper** — Task 3 adds `DblFlag`, parsing with `CultureInfo.InvariantCulture` | `Program.cs:411-422`; no `DblFlag`/`DoubleFlag` in the file |
| P10 | Signature | **Scenarios resolve from DI** — `services.GetRequiredService<BenchmarkQueryScenario>()`; Task 3 must register the new scenario | `Program.cs:192-194` dispatch; `:148` `.AddSingleton<BenchmarkQueryScenario>()` |
| P11 | Command | LoadTest tests: `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`; project is in `Iverson.slnx` | csproj exists; `Iverson.LoadTest.Tests.csproj` present in `Iverson.slnx` |
| P12 | Command | Python tests run by explicit path: `python3 -m pytest <path> -q`. No `pytest.ini` or `conftest.py` anywhere | `test_report.py:1-6` docstring; no config files found |
| P13 | Ordering | Task 3 consumes Task 1's aggregator and Task 2's dump format; Task 4 consumes Task 2's format; Task 5 consumes all. No task imports a symbol a later task introduces | `BenchmarkQueryScenario.cs` already references `DocumentRanking` (`:383`) and `MaxPassageAggregator` (`:418`, `:424`) — no new cross-task import is created |
| P14 | Code validity | `Iverson.LoadTest` targets `net10.0`; tests use xunit 2.9.3 + FluentAssertions 7.0.0 | `Iverson.LoadTest.csproj`, `Iverson.LoadTest.Tests.csproj` |
| P15 | Consumer impact | Adding a method to `DocumentRanking` breaks no caller: four production call sites (`MaxPassageAggregator.cs:56,75`; `BenchmarkQueryScenario.cs:383,451`) and 8 tests in `DocumentRankingTests.cs`, none of which is modified | `grep -rn 'CollapseByDocId'`; `grep -c '\[Fact\]\|\[Theory\]'` → 8 |
| P16 | Consumer impact | Adding a dump write leaves the four existing output writes untouched — they are separate `Path.Combine` calls on the same `OutputDir` | `BenchmarkQueryScenario.cs:222`, `:289`, `:290`, `:313` |
| P17 | Consumer impact | A new `case "benchmark-aggregate"` collides with no existing command | Dispatch cases: `seed`, `write-path`, `read-path`, `benchmark-ingest`, `benchmark-query`, `all`, `clear-data`, `acting-user-smoke-test` |
| P18 | Sibling sweep (flags) | `--config-label`, `--output-dir`, `--key-map-path` exist with those exact spellings; `--beta` and `--hits-path` do not exist | `grep -oE '"--[a-z-]+"' Program.cs` — full flag set enumerated, no collision |
| P19 | Sibling sweep (file naming) | The `{ConfigLabel}.{suffix}` convention holds for `.meta.json`, `.similar.trec`, `.chunks.trec`, `.chunks.diversity.json`, so `.chunks.hits.tsv` fits it | `BenchmarkQueryScenario.cs:222`, `:289`, `:290`, `:313` |
| P20 | File path / command | Task 5's restore targets exist and the counts it checks are the recorded ones: `freshstack-2048-qdrant-snapshots/` holds both `.snapshot` files, its own `RESTORE.md` points at `../scifact-512-qdrant-snapshots/RESTORE.md` for the loop and at `../freshstack-2048-2026-09-07/keymap.json` for the key map | `ls` of the snapshot dir; `RESTORE.md:1-4`; `keymap.json.stats.json` records `documents: 6000`, `chunks: 18622` |
| P21 | Consumer impact | The pool run's `LambdaChunks` must be 1.00, and nothing in the harness records or checks it | Spec `:139` requires 1.00; `docker-compose.yml:446` defaults to 0.70; `.meta.json` records `chunkBudgetMultiplier` but no λ (`BenchmarkQueryScenario.cs:204-220`) — hence the explicit verification in Task 5 Step 2a |
| P22 | Code validity | The dump's score column must round-trip exactly, or the β = 0 identity check fails on near-ties | Scores are `float32` (`ObjectSearchGrpcService.cs:589`), ULP ≈ 6×10⁻⁸ near 0.7; on `fs-2048-l100.chunks.trec` 153 adjacent pairs share an `F6` value, 20 of them between unrelated documents — distinct values, not exact ties |
| P23 | Signature | `DocumentBudget` is `private const` on `BenchmarkQueryScenario` and not reachable from a second scenario | `BenchmarkQueryScenario.cs:42` — `private const int DocumentBudget = 50;`. Task 3 declares its own, with the β = 0 identity check as the drift detector |

## Tasks

### Task 1: Tail-sum aggregator

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs`

**Interfaces:**
- Produces: `DocumentRanking.CollapseByDocIdWithTail(scored, limit, beta)` and `MaxPassageAggregator.Aggregate(chunks, keyMap, limit, beta)` — both consumed by Task 3.

- [ ] **Step 1: Write the failing tests** in `DocumentRankingTests.cs`, alongside the existing 8. Cover, at minimum: a document with 5 chunks sums only its 2nd–4th (cap at 3); a document with 2 chunks sums only its 2nd; a single-chunk document scores exactly its max at every β; ordering is by the augmented score, not the max; `limit` truncates after collapsing, not before; and **`beta == 0` returns a result equal to `CollapseByDocId`'s on the same input**.

- [ ] **Step 2: Add the function** beside `CollapseByDocId`. Do **not** modify the existing overloads — Global Constraints.

```csharp
private const int TailCap = 3;

/// <summary>
/// Collapses chunk rows to one row per parent, scoring each as its maximum chunk score plus
/// <paramref name="beta"/> times the sum of up to its next <see cref="TailCap"/> highest chunk
/// scores (spec §2). At beta == 0 this delegates to <see cref="CollapseByDocId"/> outright, so
/// the ranking is bit-identical to today's max-passage rather than incidentally equal through
/// IEEE arithmetic — the Phase 1 identity check asserts exactly that.
/// </summary>
public static IReadOnlyList<(string DocId, double Score)> CollapseByDocIdWithTail(
    IEnumerable<(string DocId, double Score)> scored,
    int limit,
    double beta)
{
    if (beta == 0) return CollapseByDocId(scored, limit);

    var byDoc = new Dictionary<string, List<double>>();
    foreach (var (docId, score) in scored)
    {
        if (!byDoc.TryGetValue(docId, out var scores))
            byDoc[docId] = scores = [];
        scores.Add(score);
    }

    return byDoc
        .Select(kv =>
        {
            var descending = kv.Value.OrderByDescending(s => s).ToList();
            var tail       = descending.Skip(1).Take(TailCap).Sum();
            return (DocId: kv.Key, Score: descending[0] + beta * tail);
        })
        .OrderByDescending(r => r.Score)
        .Take(limit)
        .ToList();
}
```

- [ ] **Step 3: Add a β-aware overload to `MaxPassageAggregator`**, so the offline scenario gets key-map resolution and unresolved-parent handling without duplicating either:

```csharp
public static ChunkAggregation Aggregate(
    IEnumerable<(string ParentKey, double Score)> chunks,
    IReadOnlyDictionary<string, string> keyMap,
    int limit,
    double beta)
```

It resolves exactly as the existing overload does, then calls `DocumentRanking.CollapseByDocIdWithTail(resolved, limit, beta)`. **The existing 2-tuple overload delegates to it at `beta: 0`**, so there is one resolution rule and one unresolved-parent rule; `CollapseByDocId` is untouched, and the β = 0 path still reaches it structurally through Step 2's short-circuit. Add a test asserting the 3-arg overload and the 4-arg overload at `beta: 0` return equal results on the same input.

- [ ] **Step 4: Run the tests.** `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/DocumentRanking.cs Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/DocumentRankingTests.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs
git commit -m "add a tail-sum collapse beside max-passage, bit-identical at beta 0"
```

### Task 2: Raw-chunk-hit dump

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkHitDumpWriter.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkHitDumpWriterTests.cs`

**Interfaces:**
- Consumes: the per-query `List<(string ParentKey, double Score, string Text)>` at `BenchmarkQueryScenario.cs:396`.
- Produces: `<label>.chunks.hits.tsv` — consumed by Tasks 3, 4 and 5.

- [ ] **Step 1: Write the failing tests.** The writer emits a header row `queryId\tparentKey\trank\tscore`; ranks are 1-based and per query; rows preserve the order they were supplied in; scores are written **round-trip, not display-formatted** — `score.ToString(CultureInfo.InvariantCulture)`, which is shortest-round-trippable on .NET Core 3.0+ (`"R"` or `"G17"` also work), and exact here because the value originates as a widened `float32`; an empty hit list yields a header-only file. **Do not use `F6`.** The dump is machine-read intermediate state, not a scored artefact, so it has no reason to share `TrecRunWriter`'s display precision — and `F6` would collapse genuinely distinct near-tied scores into exact ties, whose order then falls back to insertion and breaks the β = 0 byte identity for reasons unrelated to the aggregator.

- [ ] **Step 2: Write `ChunkHitDumpWriter`**, mirroring `TrecRunWriter`'s shape — a static class with `WriteAsync(string path, IEnumerable<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>, CancellationToken)`. Create the directory if absent, as `TrecRunWriter` does.

- [ ] **Step 3: Collect hits per query in `BenchmarkQueryScenario`** and write the dump beside the other outputs, using the established naming: `Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.chunks.hits.tsv")`. **Do not alter the existing writes at `:222`, `:289`, `:290`, `:313`** — Global Constraints.

- [ ] **Step 4: Run the tests.** `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/ChunkHitDumpWriter.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkHitDumpWriterTests.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs
git commit -m "dump raw chunk hits per query so beta can be replayed offline"
```

### Task 3: `benchmark-aggregate` command

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkAggregateScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`

**Interfaces:**
- Consumes: `CollapseByDocIdWithTail` (Task 1); `<label>.chunks.hits.tsv` (Task 2).
- Produces: `<label>.chunks.trec` and `<label>.meta.json` in `--output-dir` — consumed by Task 5's identity check and by Phase 2.

- [ ] **Step 1: Add the flags and the helper to `Program.cs`.** `--beta` (default `0`) and `--hits-path` (default `""`) on `CommandFlags`. `StrFlag`/`IntFlag` exist but no `double` helper does (P9). Add `using System.Globalization;` to `Program.cs`'s using block — it is **not** an implicit using for a console `net10.0` project and the snippet below does not compile without it — and put the helper **on `CommandFlags`, beside `StrFlag` and `IntFlag`**, which are `private static` there and unreachable from elsewhere:

```csharp
static double DblFlag(string[] args, string name, double fallback)
{
    var raw = StrFlag(args, name, "");
    return string.IsNullOrEmpty(raw)
        ? fallback
        : double.Parse(raw, CultureInfo.InvariantCulture);
}
```

`InvariantCulture` is not optional — a comma-decimal locale would otherwise misparse `--beta 0.003`.

- [ ] **Step 2: Register the scenario in DI** — `.AddSingleton<BenchmarkAggregateScenario>()` beside `.AddSingleton<BenchmarkQueryScenario>()` at `Program.cs:148`. Scenarios resolve through `GetRequiredService` (P10); without this the dispatch throws at run time.

- [ ] **Step 3: Add the dispatch case and help text**, beside `benchmark-query`.

- [ ] **Step 4: Write `BenchmarkAggregateScenario`.** It reads the hits TSV and `keymap.json`, groups hits by `QueryId`, and calls `MaxPassageAggregator.Aggregate(hits, keyMap, DocumentBudget, beta)` — the β-aware overload Task 1 adds — so key-map resolution and unresolved-parent handling have exactly one implementation. It then writes the run file through `TrecRunWriter.WriteAsync` with `--config-label` as the run tag.

  **Truncation limit:** declare `private const int DocumentBudget = 50;` on `BenchmarkAggregateScenario`, matching `BenchmarkQueryScenario.cs:42` — that one is `private const` and not reachable from here. The duplication is deliberate and self-checking: if the two drift, the β = 0 identity check in Task 5 Step 3 fails, because a different truncation limit produces a different run file.

  **Guards:** refuse an empty `--key-map-path` or `--hits-path` up front with a clear message, matching the `benchmark-query` guards at `BenchmarkQueryScenario.cs:62-81`. Both default to `""` (`Program.cs:418`) and `KeyMap.LoadAsync` has no emptiness guard (`KeyMap.cs:23-27`), so an omitted flag currently surfaces as a bare file-not-found rather than the missing argument it is.

  **Row order is load-bearing and must be preserved end to end.** Read the hits in file order; group with LINQ `GroupBy`, which preserves both key first-appearance order and within-group element order; **do not sort at any point**; emit queries in the dump's first-appearance order, not sorted `QueryId` order. Exact score ties break by insertion order inside `CollapseByDocId`'s dictionary (`DocumentRanking.cs:32-44`), so the in-run tie order *is* the Qdrant stream order — any reordering here fails the β = 0 byte identity while looking like an aggregator defect. Add a test asserting that two documents with **equal** scores appear in the output in the order their hits appeared in the dump, so this constraint is falsifiable before Task 5 depends on it.

  **Sidecar:** copy `composite` from the pool run's sidecar, located by **deriving from `--hits-path`** (replace the `.chunks.hits.tsv` suffix with `.meta.json`), not from `--config-label`. That derivation is what makes both callers work: the identity check runs at the pool's own label in a different directory, while Phase 2's β arms carry different labels against the same pool. Record the aggregator's own build under a separate key; `report.py` reads only `composite` (`:184-202`), so the second key is documentation, not an enforced check.

- [ ] **Step 5: Build and run the full LoadTest suite.** `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkAggregateScenario.cs Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "add benchmark-aggregate to replay a chunk-hit dump at a given beta"
```

### Task 4: `tail_stats.py`

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/tail_stats.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_tail_stats.py`

**Interfaces:**
- Consumes: `<label>.chunks.hits.tsv` (Task 2), the β = 0 `<label>.chunks.trec`, and `keymap.json`.

- [ ] **Step 1: Write the failing pytest suite**, in the style of `test_report.py`. Cover: the histogram counts documents by in-pool chunk depth (2, 3, 4+); `s` averages only the 2nd–4th chunks; a document with one pooled chunk contributes to neither; top-50 scoping selects on the run file's document set, not the dump's; and the pool-wide figure covers every parent in the dump.

- [ ] **Step 2: Write the script.** It reports three things (spec §2):
  1. the **in-pool tail-depth histogram** — how many top-50 documents have 2, 3, 4 or more pooled chunks;
  2. **`s`** — the mean score of the 2nd–4th pooled chunks **of the documents reaching the top 50 of the β = 0 ranking**;
  3. the **pool-wide** equivalent of `s`, over every parent in the dump, printed beside it.

  Also print the derived ladder endpoints for Phase 2 — tie-break `median_top10_gap / s` and parity `span / (3 · s)` — computed from the same run file, so Phase 2 does not have to recompute them by hand.

- [ ] **Step 3: Run the tests.** `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_tail_stats.py -q`

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/tail_stats.py Iverson.Server/Iverson.LoadTest/scripts/test_tail_stats.py
git commit -m "add tail_stats.py: in-pool tail depth and the tail-score level s"
```

### Task 5: Phase 1 execution

**Files:** none — this task runs the harness and records numbers.

**Interfaces:**
- Consumes: everything Tasks 1–4 produce.
- Produces: the tail-depth histogram and `s`, which are Phase 2's only inputs for setting β.

- [ ] **Step 1: Restore FreshStack-2048.** Use the curl upload loop in `~/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots/RESTORE.md` against `freshstack-2048-qdrant-snapshots/`. **Verify 6,000 object and 18,622 chunk points before running anything** — a short restore silently understates every measure.

- [ ] **Step 2a: Set λ and restart the API.** `VECTOR_RANKING_LAMBDA_CHUNKS=1.00 docker compose up -d --no-deps iverson-api` (the restart form `docker-compose.yml:444` documents). **Then verify it took effect** — `docker compose exec iverson-api env | grep VectorRanking__LambdaChunks` must print `1.00`. The harness cannot observe the server's λ and `.meta.json` does not record it, so a silent restart failure is indistinguishable from success and would invalidate the run without any visible error.

- [ ] **Step 2b: Run the pool query run** at `--chunk-budget-multiplier 11` (Global Constraints — the default of 5 is wrong for this arm, and `ChunkBudgetGuard` exists to catch that class of error), with `--config-label fs2048-pool` and `--key-map-path` pointing at `freshstack-2048-2026-09-07/keymap.json`.

- [ ] **Step 3: Run the β = 0 identity check.** `benchmark-aggregate --beta 0 --hits-path <pool dir>/fs2048-pool.chunks.hits.tsv --key-map-path <corpus>/freshstack-2048-2026-09-07/keymap.json --config-label fs2048-pool --output-dir <a DIFFERENT directory>`, then require byte identity against the in-run file. The key map must be **the same one Step 2b used** — a different key map resolves parents differently, silently changing the run file and failing the diff for a reason that has nothing to do with the aggregator:

```bash
diff <pool-dir>/fs2048-pool.chunks.trec <check-dir>/fs2048-pool.chunks.trec && echo "IDENTITY OK"
```

  The separate output directory is what makes this possible: `TrecRunWriter` derives its path from the config label, so an equal label in the same directory would overwrite the comparand rather than reproduce it. **A failure here blocks Phase 2** — it means the shared aggregator does not reduce to today's behaviour at β = 0, and no β result from it could be trusted.

- [ ] **Step 4: Run `tail_stats.py`** against the dump, the β = 0 run file and the key map. Record the histogram, `s`, the pool-wide `s`, and the derived tie-break and parity endpoints.

- [ ] **Step 5: Record the outcome** in `docs/plans/2026-09-GATE-chunk-coverage.md`, following the existing gate-document convention (`docs/plans/2026-09-GATE-tier1-defaults.md`). State the measured numbers, the identity-check result, and — per spec §2 — whether the measured in-pool tail depth is sufficient for Phase 2 to be worth running at all. **A histogram showing most top-50 documents contribute a single pooled chunk is a valid Phase 1 result that ends the experiment**, and it costs one run to learn.

- [ ] **Step 6: Commit**
```bash
git add -f docs/plans/2026-09-GATE-chunk-coverage.md
git commit -m "record chunk-coverage Phase 1: in-pool tail depth and s"
```

## Tasks NOT in this plan

**Phase 2 in its entirety** — calibrating the β ladder and running the sweep across the four corpora. Spec §2 makes this deliberate: the ladder is derived from Phase 1's measured `s`, and specifying its arms before that measurement exists would repeat the error the spec corrected twice.

Inherited verbatim from the spec's "What this cannot show" (§7):

- Whether coverage helps at chunk budgets other than **550** (`DocumentBudget` 50 × `--chunk-budget-multiplier` 11), and whether it interacts with the fusion weights. Both are held fixed. A null is therefore scoped to this budget and this triple. The budget is not a nuisance parameter here: it determines how many of a document's 2nd–4th chunks are in the pool at all, which is the quantity the tail term reads.
- Whether a winning β transfers to the routed `SearchSimilar` path. That path collapses a larger pool which scales with the caller's `top_k` (`ObjectSearchGrpcService.cs:358-362`), so the available tail depth varies per request — no fixed-budget harness arm measures it.

## Known issues inherited from spec

Inherited verbatim from spec §9. These exist by design, accepted during brainstorming.

- **The deep-tail evidence is 512-window only.** FreshStack-2048 is the sole production-window arm; both density arms — FreshStack-512 (77.7 % of documents at ≥4 chunks) and NFCorpus (71.0 %) — are 512/448 ingests, and §5 forbids re-ingest. At the production window NFCorpus would be 1.33 chunks/doc with 0.2 % at ≥4, a second null control rather than an instrument. So a null on FreshStack-2048 cannot be triangulated against any deep-tail arm **at the window that ships**. This is the gap `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` item 1 already names.
- **The tail cap of 3 rarely binds on FreshStack-2048.** At a mean of 3.10 chunks per document the typical tail is 1–2 chunks; the cap binds for the 46.0 % with four or more.
- **Chunk-level MMR still runs on the primary arms** at λ = 1.00 only in the sense that diversification is off; the chunk pool is still whatever Qdrant returned under the shipped fusion triple. This experiment does not disentangle the fusion weights from the aggregation rule.
