# Critical Implementation Review: 2026-08-31-benchmark-run-integrity-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-08-31-benchmark-run-integrity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA `e7e06f4`); cited file:line references re-checked under §1. All three are documentation — the plan, round 1's review, and round 1's fixes. No code changed.

Enumeration built before re-reading round 1; its fixes are rows here, not the search area.

## 0. Coverage enumeration

### Task 1 × surfaces

| Surface | Disposition |
|---|---|
| Code block — `BuildIdentity.cs` | ok — `internal` is right: `Program.cs` is top-level statements in the same assembly and carries `using Iverson.Api;` at `:4`. `Convert.ToHexString` returns uppercase and the block calls `.ToLowerInvariant()`, so the test's `^[0-9a-f]{16}$` assertion is satisfiable |
| Code block — `MapGet("/build")` | ok — the anonymous type's property names infer from the destructured locals `composite`/`assemblies`, already lowercase, so the emitted JSON matches the documented shape without relying on a naming policy |
| Step prose — containment-not-count | ok — re-confirmed: 8 `Iverson.*.dll` in the Api.Tests output (7 production + `Iverson.Api.Tests.dll`) |
| Commands + commit | ok — three named paths matching the Files list; message style matches `git log` |

### Task 2 × surfaces

| Surface | Disposition |
|---|---|
| Step prose + code — `LoadTestConfig` fourth parameter | ok — `Env` is declared at `Program.cs:267` but called from `:26`, which is legal because C# local functions are not order-dependent; the existing `IVERSON_GRPC_URL` read at `:26` already relies on this, so a new call beside it inherits proven behaviour |
| Code block — `ChunkBudgetGuard.cs` | ok — now `public` on the class, `Result` and `Evaluate`, matching `DocumentRanking.cs:13`, `KeyMap.cs:11`, `TrecRunWriter.cs:10`, `MaxPassageAggregator.cs:23` |
| Code block — the refusal message | ok — every interpolated member exists on the `Result` the same step defines; `chunks`/`documents` are bound where the sidecar is read |
| Step prose — sidecar-absent handling | ok — print one line and proceed, matching the spec |
| Step prose — `/build` fetch, directory creation, sidecar write | ok — `Directory.CreateDirectory(flags.OutputDir)` now precedes the write, and the prose states why it is required rather than defensive |
| Step prose — the guard's test table | ok — all five rows recomputed independently: 64,763/6,000 = 10.7938 (ceil 11), 33,950/6,000 = 5.6583 (ceil 6), 18,622/6,000 = 3.1037 (allow at mult 5), 24,282/8,674 = 2.7994 (allow), and 10.79 at mult 20 (allow) |
| **Dynamic — does a pre-flight `throw` actually fail the run?** | ok — `case "benchmark-query":` at `Program.cs:190-191` has no enclosing try/catch (the two `catch (Exception)` blocks at `:108` and `:164` sit in earlier startup sections and are not in this path), so the exception propagates out of the top-level program. The three existing pre-flight validations use the identical `Console.Error.WriteLine` + `throw` pattern, so the guard inherits behaviour the workflow already depends on |
| Commands + commit | ok — both suites; four named paths matching the Files list |

### Task 3 × surfaces

| Surface | Disposition |
|---|---|
| Step prose — `--baseline` and comparison-set-only exclusion | ok — `resolve_run_paths` is now left alone, so the baseline keeps its structural check and scores; the exclusion moved to where the comparison set is built |
| Step prose — sidecar derivation | ok — `runs/` really does contain `reference.chunks.trec` and `reference.similar.trec`, so both collapsing to `reference.meta.json` is the correct rule for real filenames |
| Step prose + block — statistics section | ok — measure-objects constraint carried; Holm within one measure; MDE over the sd of differences; seed printed |
| Step prose — the <10% banner | ok — 5/300 = 1.7% on the incident it exists for |
| Commands — ArguAna verification run | ok — the directory holds exactly 14 `*.trec` files and no sidecars, so "one fewer `[compare]` block than run files, per measure" resolves to 13, consistent with the baseline being scored but not compared |
| Step prose — hand-made sidecars, then delete | ok — `w0500.chunks.trec` and `reference.chunks.trec` both exist in that directory, so the two named sidecars have real runs to attach to; the step says to delete them and leave the directory as found |
| Commands + commit | ok — single named path |

### Cross-task interface contracts

| Contract | Disposition |
|---|---|
| Task 2 consumes Task 1's `GET /build` | ok — consumer needs `composite` and `assemblies`; Task 1 Step 2 emits exactly those two keys |
| Task 3's sidecar lookup consumes Task 2's `<label>.meta.json` **(persistence boundary)** | ok — reads `composite`; Task 2 Step 4's JSON carries `configLabel`, `composite`, `assemblies`, `recordedAtUtc` |
| Task 3's mismatch comparison — the same artifact, **second call site** | ok — checked as its own row: same `composite` field, same derivation; and after round 1's fix the baseline is scored, so it too carries a `build:` line and can participate in the comparison rather than being silently absent from one side |
| Task 2's guard consumes `ingest.py`'s stats sidecar **(persistence boundary)** | ok — real key set re-dumped: `documents` and `chunks` both present |
| Task 2 consumes `LoadTestConfig.HttpUrl` | ok — same task; DI registration at `Program.cs:128` makes the record resolvable into the scenario, and `BenchmarkQueryScenario` has no direct construction anywhere |

### Rule-like content (both failure directions)

| Rule | Disposition |
|---|---|
| `reachable = topK / chunksPerDoc`, refuse below budget | over-inclusion: refuses 3 of 9 corpora at mult 5 — stated and accepted. under-inclusion: ok, `multiplier ≥ chunksPerDoc` is exact |
| Sidecar path derivation | over-inclusion: ok — only the two fixed suffixes are stripped. under-inclusion: ok — a name ending in neither takes the `unknown` path |
| Baseline identity (absolute path) | ok — right mechanic, and after round 1's fix applied at the right stage |
| Holm family membership | ok — one family per measure, sized by comparisons actually made |
| *Candidate:* `--run runs/` mixes `.chunks` and `.similar`, so a chunks baseline is compared against similar runs | **dropped** — fails literal-wrongness. Both sides are document rankings over the same qrels, so the computation is valid; and cross-endpoint comparison is sometimes exactly the intent (it is the question ArguAna was built to answer). Unwanted output is not broken output |
| *Candidate:* `print_scores(path, measures, results)` has no parameter for the composite, so the `build:` line needs a signature change | **dropped** — fails literal-wrongness. Adding a parameter is ordinary implementation, not a defect in the plan; nothing about the stated outcome becomes impossible |

## 1. Verified-plan-assumptions cross-check

All 21 listed assumptions still hold under a fresh read, including P21 added last round. Re-confirmed rather than carried forward: P2/P3 (four helpers, three tests), P5 (`BenchmarkQueryScenario.cs:28-31`), P6 (`Program.cs:376,53,128`), P9 (8 DLLs in the Api.Tests output), P15 (9 `LoadTestConfig` references, 1 construction), P16 (no endpoint-enumeration test), P20 (four pre-flight validations at `:44-62`), P21 (no `InternalsVisibleTo` in `Iverson.LoadTest.csproj`; all four siblings `public static`).

The drift's three commits touch only `docs/`, so no cited `file:line` moved.

### Span check

Span check found no uncovered dependency. Round 1's single gap — type accessibility across the test-assembly boundary — is now covered by P21, and this round's enumeration surfaced no fact a task needs that neither the plan's table nor the inherited spec list verifies as scoped.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (`ChunkBudgetGuard` declared `internal`, test in another assembly, no `InternalsVisibleTo`). Resolved: class, `Result` and `Evaluate` are all `public`, and the plan states why `public` rather than adding the attribute — `Iverson.LoadTest` has never carried one and its four existing helpers solved the same problem by being public.
- **Round 1 §2.2** (sidecar written before anything creates the output directory). Resolved: `Directory.CreateDirectory(flags.OutputDir)` immediately precedes the write, with prose recording that the call is required rather than defensive, that `TrecRunWriter.cs:18-20` is the only other creation and runs at the end of the scenario, and that the failure would otherwise present as intermittent.
- **Round 1 §2.3** (baseline excluded from run discovery rather than the comparison set). Resolved: `resolve_run_paths` is explicitly left untouched, the exclusion moved to where the comparison set is built, and the plan records why the qrels precedent does not transfer — qrels raises a `ValueError` on column count, whereas the baseline is a valid run that must simply not be compared with itself.
- **Round 1 §1 span 1.a** (no assumption covered type accessibility). Resolved: P21 added with the `InternalsVisibleTo` grep and the four sibling citations.

## 5. Recommendation

✅ **Approve as-is** — §1 has no failed assumptions, §2 and §3 are both empty. Every §0 row has a disposition, the two candidates generated this round failed the literal-wrongness test and were dropped rather than promoted, and all four round-1 items are closed. Plan is ready for `subagent-driven-development`.
