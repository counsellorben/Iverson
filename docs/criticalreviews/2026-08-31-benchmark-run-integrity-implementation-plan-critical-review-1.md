# Critical Implementation Review: 2026-08-31-benchmark-run-integrity-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-benchmark-run-integrity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `e7e06f4`); cited file:line references re-checked under §1. It is the plan's own commit; no code changed.

## 0. Coverage enumeration

### Task 1 × surfaces

| Surface | Disposition |
|---|---|
| Code block — `BuildIdentity.cs` | ok — `internal` is correct here: `Program.cs` is top-level statements in the same assembly and already carries `using Iverson.Api;` at `:4`. `PEReader`/`GetModuleDefinition().Mvid`/`GetGuid` compile with no package reference |
| Code block — `MapGet("/build")` | ok — `Results.Ok(new { composite, assemblies })` serialises a `SortedDictionary<string,string>` as a JSON object; `AllowAnonymous()` matches `/health/live` at `:302`; no route collision |
| Step prose — the containment-not-count rule for the test | ok — this is executable prose and it is right: `Iverson.Api.Tests/bin/Debug/net10.0/` holds 8 `Iverson.*.dll` including `Iverson.Api.Tests.dll`, so an exact-count assertion would pass locally and mean nothing |
| Commands — `dotnet test …Api.Tests` | ok — run in this session |
| Commands — `git add`/`git commit` | ok — three named paths matching the Files list, no `-A`, message style matches `git log --format=%s` |

### Task 2 × surfaces

| Surface | Disposition |
|---|---|
| Step prose + code — `LoadTestConfig` fourth parameter | ok — 9 references, 1 positional construction (`Program.cs:53`); the six consumers take the record by type and are unaffected. `BenchmarkQueryScenario` is DI-only (no direct construction anywhere), so adding a constructor parameter breaks no call site |
| Code block — `ChunkBudgetGuard.cs` | **→ §2.1** — declared `internal`, but its test lives in another assembly and there is no `InternalsVisibleTo` |
| Code block — the refusal message | ok — `chunks` and `documents` are bound when the sidecar is read, and `r.ChunksPerDocument`/`ChunkTopK`/`ReachableDocuments`/`MinimumMultiplier` all exist on the `Result` the same step defines. `MinimumMultiplier = ceil(chunksPerDoc)` is the correct inverse of `reachable ≥ documentBudget` |
| Step prose — sidecar-absent handling | ok — matches the spec: print one line and proceed, since the C# ingest path writes no sidecar |
| Step prose + wiring — `/build` fetch and sidecar write | **→ §2.2** — the write precedes the only code that creates the output directory |
| Step prose — the guard's test table | ok — the five rows reproduce the real corpus densities; recomputed 64,763/6,000 = 10.79 → refuse at mult 5, allow at 20; 33,950/6,000 = 5.66 → refuse; 18,622/6,000 = 3.10 → allow; 24,282/8,674 = 2.80 → allow |
| Commands — both suites | ok — `Iverson.LoadTest.Tests.csproj` carries `Microsoft.NET.Test.Sdk` 17.12.0 and `xunit` 2.9.3 |
| Commands — `git add`/`git commit` | ok — four named paths matching the Files list |

### Task 3 × surfaces

| Surface | Disposition |
|---|---|
| Step prose — `--baseline` and its exclusion | **→ §2.3** — the plan excludes from run *discovery*, which is broader than the spec's "comparison set" and silently drops the baseline's own scores |
| Step prose — sidecar derivation | ok — strip `.trec`, strip `.similar`/`.chunks`, append `.meta.json`; the two suffixes are fixed constants at `BenchmarkQueryScenario.cs:39-40`, and real filenames confirm the shape (`reference.chunks.trec`, `lambda1.similar.trec`) |
| Step prose + block — the statistics section | ok — measure-objects constraint carried from the Global Constraints; `iter_calc` yields per-query `Metric(query_id, measure, value)`; Holm within one measure; MDE formula uses the sd of differences; seed printed |
| Step prose — the <10% banner | ok — 5/300 = 1.7% on the incident it exists for, well inside the threshold |
| Commands — verification run against ArguAna | ok — that path exists and holds 14 `*.trec` files and no sidecars, so the invocation exercises the absent-sidecar and exclusion paths as claimed |
| Step prose — hand-made sidecars, then delete | ok — writes into a corpora directory holding irreplaceable measured results, and the step says explicitly to delete both files and leave it as found |
| Commands — `git add`/`git commit` | ok — single named path |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 consumes Task 1's `GET /build` | ok — traced to the operation, not the task name: the consumer needs `composite` and `assemblies`, and Task 1 Step 2's `Results.Ok(new { composite, assemblies })` emits exactly those two keys |
| Task 3 consumes Task 2's sidecar **(persistence boundary)** | ok on fields — Task 3 reads `composite`; Task 2 Step 4's JSON writes `configLabel`, `composite`, `assemblies`, `recordedAtUtc`. Naming coupling is covered by Task 3's derivation rule |
| Task 3's second consumer of the same artifact — the mismatch comparison **(separate call site)** | ok — checked as its own row: same `composite` field, sourced the same way; no field the lookup has that the comparison lacks |
| Task 2's guard consumes `ingest.py`'s stats sidecar **(persistence boundary)** | ok — real record's key set re-dumped: `{documents, chunks, embed_calls, embeds_saved, elapsed_seconds, started_at, finished_at}`; the guard's two required fields are present |
| Task 2 consumes `LoadTestConfig.HttpUrl` produced by Task 2 Step 1 | ok — same task, and the DI registration at `Program.cs:128` makes the record resolvable into the scenario |

### Rule-like content (both failure directions)

| Rule | Disposition |
|---|---|
| `reachable = topK / chunksPerDoc`, refuse below `documentBudget` | over-inclusion: refuses 3 of 9 corpora at mult 5 — stated and accepted in the spec. under-inclusion: ok, the equivalent `multiplier ≥ chunksPerDoc` is exact |
| Sidecar path derivation | over-inclusion: ok — only `.similar`/`.chunks` are stripped, both fixed constants, so no unrelated filename is rewritten. under-inclusion: ok — a name ending in neither takes the `unknown` path by construction |
| Baseline identity (which run is "the same file") | **→ §2.3** — absolute-path comparison is the right mechanic, but it is applied at the wrong stage |
| Holm family membership | ok — one family per measure, sized by comparisons actually made |

## 1. Verified-plan-assumptions cross-check

All 20 listed assumptions still hold under a fresh read. Re-confirmed rather than carried forward: P2/P3 (`LoadTest/Benchmark/` holds four helpers; `LoadTest.Tests/Benchmark/` holds their three tests), P5 (`BenchmarkQueryScenario.cs:28-31`), P6 (`Program.cs:376,53,128`), P9 (8 `Iverson.*.dll` in the Api.Tests output), P15 (9 `LoadTestConfig` references, 1 construction), P16 (no `EndpointDataSource` usage anywhere), P20 (four pre-flight validations at `:44-62`).

P20 deserves a note: it records that the spec's inherited A19 understates the count as three. That is the correct handling — the plan uses the measured value and says so rather than propagating the error.

### Span check — uncovered dependencies

**1.a — The plan depends on `ChunkBudgetGuard` being visible to `Iverson.LoadTest.Tests`, and no assumption covers type accessibility across that boundary.** P3 establishes where the test file goes; nothing establishes that the type under test can be referenced from there. Verified in-round, and it **fails as written** → §2.1.

## 2. Literal-wrongness findings

### 2.1 — `ChunkBudgetGuard` is declared `internal`, so its test cannot compile

**Description.** Task 2 Step 2's code block declares `internal static class ChunkBudgetGuard` with `internal` members, and Step 5 places `ChunkBudgetGuardTests.cs` in `Iverson.LoadTest.Tests` — a different assembly. Step 6 then runs that suite, so the failure surfaces as a build error inside the task.

**Evidence.** `Iverson.LoadTest.csproj` contains no `InternalsVisibleTo` (grep returns nothing). All four existing helpers in the same directory are `public static`: `DocumentRanking.cs:13`, `KeyMap.cs:11`, `TrecRunWriter.cs:10`, `MaxPassageAggregator.cs:23` — and their tests live in `Iverson.LoadTest.Tests/Benchmark/`, which is only possible because they are public. The plan's own P2 cites that directory as the precedent for where the helper belongs, but the accessibility half of the precedent was not carried across.

**Proposed fix.** Declare `public static class ChunkBudgetGuard` and make `Result` and `Evaluate` public, matching the four siblings. Do not add `InternalsVisibleTo` to `Iverson.LoadTest` — that would introduce a convention the assembly does not have, to solve a problem its four existing helpers solved by being public.

### 2.2 — The sidecar is written before anything creates the output directory, so the first run into a fresh `--output-dir` fails in pre-flight

**Description.** Task 2 Step 4 writes `<output-dir>/<config-label>.meta.json` from the pre-flight block. Nothing has created `<output-dir>` at that point: the only `Directory.CreateDirectory` on this path is inside `TrecRunWriter.WriteAsync`, which runs at the *end* of the scenario when the run files are written. `File.WriteAllText` to a non-existent directory raises `DirectoryNotFoundException`.

**Evidence.** `TrecRunWriter.cs:18-20` — `var dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);`. `BenchmarkQueryScenario.cs:109-113` shows those writes are the last thing the scenario does. The pre-flight block (`:44-62`) does no filesystem work today, so the harness's current contract is that `--output-dir` need not pre-exist — `TrecRunWriter` establishes it lazily.

This is not hypothetical for the normal workflow: a sweep writes each configuration into a `runs/` directory that may not exist until the first invocation. The failure is also order-dependent in the worst way — it passes on a second run into the same directory, so it will look intermittent.

**Proposed fix.** Create the directory in the pre-flight block before writing the sidecar, mirroring what `TrecRunWriter` already does: `Directory.CreateDirectory(flags.OutputDir)` immediately before `File.WriteAllText`. One line, and it makes the pre-flight write self-sufficient rather than dependent on a later step.

### 2.3 — Excluding the baseline from run *discovery* also removes its structural check and its scores, which the spec did not ask for

**Description.** Task 3 Step 1 says to "Exclude the baseline from the discovered run set by absolute path, exactly as `resolve_run_paths` already excludes the `--qrels` file." The spec's wording is narrower: "The baseline is excluded from the **comparison set** by absolute path" (spec line 112). Those are different stages, and `resolve_run_paths` is the wrong one.

**Evidence.** `report.py:322` assigns `run_paths = resolve_run_paths(args.run, args.qrels)`, and that single list drives both the structural-check loop (`:332-333`) and the scoring loop (`:335-336`). Excluding the baseline there means it receives no structural check and no `nDCG@10`/`R@50`/`AP` line — while every `[compare]` block in the new section names it as the reference. The reader sees deltas against a baseline whose own value is never printed, and loses the coverage check on the one run every comparison depends on.

The qrels precedent does not transfer: the qrels file is excluded because handing it to `ir_measures` as a run raises a `ValueError` on column count. The baseline is a perfectly valid run that should be scored — it just must not be compared against itself.

**Proposed fix.** Leave `resolve_run_paths` alone. Exclude the baseline only where the comparison set is built — filter it out of the runs the new section iterates, by absolute path, and report it there (`[compare] excluded … (it is the --baseline file)`). The baseline then keeps its structural check and its scores, and the Holm family is sized by the comparisons actually made, as the spec says.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has three findings, §3 is empty. All three are contained: an accessibility keyword, one `Directory.CreateDirectory` line, and moving an exclusion from one stage to another. None disturbs the endpoint, the composite, the guard arithmetic or the statistics.
