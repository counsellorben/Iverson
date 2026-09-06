# Critical Design Review: 2026-08-31-benchmark-run-integrity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-benchmark-run-integrity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Header / scope | ok — three components, each tied to a named prior failure; scope statement matches the body |
| `## Why these three` | ok — all three incident descriptions trace to the source document's own text; no new claim introduced |
| `### The identity is a composite over assemblies` | ok — the motivating measurement (a `Iverson.Vector`-only change leaving `Iverson.Api`'s MVID identical) is reproducible and is the reason a single MVID is rejected |
| `### MVIDs are read from files` | ok — `PEReader`/`GetModuleDefinition().Mvid` compiled and ran against the real build output; `System.Reflection.Metadata` needed no package reference |
| `### The endpoint` | ok — `AllowAnonymous` matches `Program.cs:302`; the both-listeners statement re-verified (no `RequireHost`/`MapWhen` anywhere) |
| `### The harness records it before the sweep` | ok — `RunAsync` has an existing validation block with three throws; `flags.OutputDir` validated there; 8081 published at `docker-compose.yml:367` |
| `## Item 4` (statistics) | ok — output shape, Holm family and the <10% banner all internally consistent; see the two rule rows below |
| `### report.py also reads the item-3 sidecars` | **→ §2.1** — absent-sidecar behaviour unspecified, and absent is the state of every run on disk |
| `## Item 5` (guard) | ok — arithmetic re-derived against all nine corpora; refusal pattern matches the three existing `RunAsync` validations |
| `### What this guard implies about existing results` | ok — the decay-sweep caveat is correct: that sweep compared two triples over identical captured candidate sets, so a budget limit applies to both arms equally |
| `## Verified assumptions` | see §1 |
| `## Out of scope` | ok — item 1 shipped, item 2 blocked behind `centroid-ablation`, items 6/7 correctly characterised |
| `## Known issues / accepted as out of scope` | ok — both carry the user's decision explicitly rather than being buried |

### Rules and operands (both failure directions)

| Rule | Disposition |
|---|---|
| Composite = SHA-256 over sorted `name:mvid` for `Iverson.*.dll` in `AppContext.BaseDirectory` | over-inclusion: ok — the container's `/app` holds exactly 7 `Iverson.*.dll` and no test or tooling assemblies. under-inclusion: ok — all 7 project references are present as files, including `Iverson.Client.Contracts`, which is precisely the one the loaded-set approach could miss |
| Guard: `reachable = (DocumentBudget × ChunkBudgetMultiplier) / chunksPerDoc`, refuse below `DocumentBudget` | over-inclusion: **checked against real data, not asserted** — refuses 3 of 9 corpora at multiplier 5 (two at 10.79 c/d → 23.2; chunk256 at 5.66 → 44.2) and 0 of 9 at multiplier 20. The spec names this consequence and the user accepted it. under-inclusion: ok — the equivalent form `ChunkBudgetMultiplier ≥ chunksPerDoc` is exact, so nothing below the threshold escapes |
| Holm family = runs within one measure | over-inclusion: ok — pooling three measures would over-correct each. under-inclusion: ok — every non-baseline `--run` enters its measure's family |
| `queries changed < 10%` → banner | ok — re-derived against the incident it exists for: 5 of 300 changed is 1.7%, well inside the threshold, so the retracted SciFact claim would have been flagged |
| Build-mismatch comparison across runs | **→ §2.1** — the rule is defined only for runs that have a sidecar |
| Sidecar-absent handling | ok for item 5 (line 186, explicit); **→ §2.1** for item 4 (unspecified) |

### Data-flow arrows (persistence boundaries flagged)

| Arrow → consuming operation | Disposition |
|---|---|
| `/build` JSON → harness → `<config-label>.meta.json` **(persistence boundary)** | ok — the consuming operation is report.py's mismatch comparison, which needs `composite`; the producing side writes `composite` plus the assembly map. Field checked against the shape the spec itself specifies, not inferred from a type |
| `<config-label>.meta.json` → report.py mismatch check **(persistence boundary)** | **→ §2.1** — the artifact does not exist for any of the 168 run files on disk, and the consumer's behaviour in that case is undefined |
| `ingest.py` sidecar → guard **(persistence boundary)** | ok — dumped a real record's key set: `{documents, chunks, embed_calls, embeds_saved, elapsed_seconds, started_at, finished_at}`. The guard's two required fields are both present |
| `iter_calc(measures, qrels, run)` → paired statistics | ok — the consuming operation needs `query_id` and `value` per query; ran it and confirmed `Metric(query_id=…, measure=…, value=…)`, 1,401 rows per measure on a real ArguAna run |
| `AppContext.BaseDirectory` → file enumeration, in the deployed image | ok — Dockerfile runtime stage is `WORKDIR /app` with `ENTRYPOINT ["dotnet", "Iverson.Api.dll"]`, and `/app` in the running container holds the 7 DLLs |

## 1. Verified-assumptions cross-check

All 21 listed assumptions still hold under a fresh read, including the two the spec itself records as **failed** (A1, A4) — both are recorded as failures with the design already corrected, which is the correct state, not an outstanding defect.

Re-confirmed this round rather than carried forward: A2 (`Program.cs:302`), A7 (no `Deterministic` override in any `.csproj`/`.props`), A8 (`docker-compose.yml:367`), A9/A17/A18/A19 (`BenchmarkQueryScenario.cs:37-38,42-60` — the three existing `Console.Error.WriteLine` + `throw` validations), A12 (`iter_calc` present and per-query), A16 (real sidecar key set), A20 (`resolve_run_paths` globs `*.trec` only).

### Span check — uncovered dependencies

**1.a — The design depends on constructing ir_measures measures as OBJECTS, never via `parse_measure`, and no assumption covers it.** A12 establishes that `iter_calc` exists and returns per-query values; it does not establish how the measures reaching it are built. Verified in-round, and it is not hypothetical: `parse_measure("nDCG@10")` raises `AttributeError: module 'ast' has no attribute 'Num'` on this box's Python 3.14, because `ast.Num` was removed in 3.12. `report.py:52-57` already carries a `CRITICAL:` comment about exactly this, so an implementer editing that file is likely to see it — but the spec does not say it. Holds, with a covering assumption recommended.

**1.b — The design depends on the deployed image containing the `Iverson.*` assemblies as discrete files, and no assumption covers it.** A4 settles that reading files beats reading loaded assemblies; it says nothing about whether files are what the *container* has. A single-file or trimmed publish would leave `AppContext.BaseDirectory` without them and void item 3 entirely. Verified in-round: the Dockerfile's runtime stage copies a plain framework-dependent publish to `/app`, and the running container lists exactly 7 `Iverson.*.dll`. Holds.

## 2. Literal-wrongness findings

### 2.1 — `report.py`'s sidecar read has no defined behaviour when the sidecar is absent, which is the state of every run file currently on disk

**Description.** The spec says report.py "reads the item-3 sidecars", prints each run's composite, and warns on mismatch. It never says what happens when a run has no sidecar. Item 5's guard gets explicit absence handling ("prints one line saying so and proceeds", line 186) and item 3's writer gets an explicit unreachable-API rule, so the omission is specific to item 4's reader — it reads as an oversight rather than a decision.

**Evidence.** `find /home/ben/repositories/iverson-benchmark-corpora -name "*.meta.json"` returns nothing, against **168** run files under `*/runs/`. Every historical corpus — the ArguAna sweep, all three NFCorpus runs, both FreshStack chunk arms, SciFact — has run files and no sidecar, and always will: the sidecars are written by a feature that does not exist yet. So the first real invocation of the new `--baseline` mode will be against runs that have none.

Without a defined path, the natural implementations diverge sharply: an unguarded `open()` raises `FileNotFoundError` and takes down a `report.py` invocation that scores fine today, while a bare `except` silently reports no mismatch and makes the guarantee vacuous. The spec's stated outcome — a run carries evidence of what produced it, and comparisons across builds are flagged — is not achievable on any existing data either way, and the spec does not acknowledge that.

**Proposed fix.** State the absent case explicitly, mirroring the wording item 5 already uses. A run with no sidecar prints `build: unknown` in its scores block; the mismatch check runs only over the subset of compared runs that *have* a composite, and when at least one run in a comparison lacks one, the section says so rather than reporting agreement. Add a line to the spec noting that all runs predating this work fall in that subset, so the first users of `--baseline` are not surprised by it.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty. The fix is a paragraph in the spec, not a design change: the mechanism, the endpoint, the composite, the guard arithmetic and the statistics all survive the sweep intact. The two span-check dependencies were verified in-round and both hold; adding covering assumptions for them would harden the spec but neither blocks planning.
