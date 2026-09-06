# Critical Design Review: 2026-08-31-benchmark-run-integrity-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-benchmark-run-integrity-design.md`
**Verified Assumptions section:** present

Enumeration built before re-reading round 1; round 1's fix is a row here, not the search area.

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Header / scope | ok — three components, scope statement unchanged and still matches the body |
| `## Why these three` | ok — the three incident descriptions still trace to the source document |
| `### The identity is a composite over assemblies` | ok — the motivating measurement re-checked; a single entry-assembly MVID remains insufficient |
| `### MVIDs are read from files` | ok — `PEReader` + `GetModuleDefinition().Mvid` compiled and ran against real build output; no package reference needed |
| `### The endpoint` | ok — `/build` collides with no existing route (`grep MapGet` shows `/health`, `/health/live`, `/probe/{sql,starrocks,vector}`, `/admin/dlq`); `AllowAnonymous` matches `Program.cs:302` |
| `### The harness records it before the sweep` | **→ §2.1** — the sidecar's name cannot be derived from a run file's name, and the spec never states a rule |
| `## Item 4` (statistics) | **→ §2.2** — `--run` accepts a directory, and the natural invocation sweeps the baseline into its own comparison set |
| `### report.py also reads the item-3 sidecars` | ok on the absent case (round 1's fix reads correctly and names the 168/0 situation); the *locating* problem is §2.1 |
| `## Item 5` (guard) | ok — sidecar path is `<key-map-path>.stats.json`, matching `flags.KeyMapPath` + suffix; arithmetic re-derived across all nine corpora, same 3-refuse/6-ok split at multiplier 5 |
| `### What this guard implies about existing results` | ok — the decay-sweep caveat still holds; that comparison was within-arm over identical captured candidates |
| `## Verified assumptions` | see §1 |
| `## Out of scope` | ok — item 1 shipped, item 2 still blocked behind `centroid-ablation` (9 ahead, unmerged) |
| `## Known issues / accepted as out of scope` | ok — both carry the user's decision explicitly |

### Rules and operands (both failure directions)

| Rule | Disposition |
|---|---|
| Composite over `Iverson.*.dll` in `AppContext.BaseDirectory` | over-inclusion: ok — container `/app` holds exactly 7, no test or tooling assemblies. under-inclusion: ok — all 7 project references present as files |
| **Run file → sidecar path derivation** | **→ §2.1** — the rule does not exist in the spec, and no naive form works |
| **`--run` membership (which runs enter the comparison set)** | over-inclusion: **→ §2.2** — directory expansion admits the baseline itself. under-inclusion: ok — every `*.trec` in a passed directory is included |
| Guard: `reachable = (DocumentBudget × ChunkBudgetMultiplier) / chunksPerDoc` | both directions re-checked against the nine corpora; unchanged from round 1 |
| Holm family = runs within one measure | over-inclusion: **→ §2.2** — an accidental self-comparison inflates the family. under-inclusion: ok |
| `queries changed < 10%` banner | ok — 5/300 = 1.7% on the incident it exists for, well inside the threshold |
| Absent-sidecar handling (item 4, added round 1) | ok — `build: unknown`, excluded from the mismatch check, and the section states rather than implies agreement |
| Absent-sidecar handling (item 5) | ok — prints one line and proceeds; unchanged |

### Data-flow arrows (persistence boundaries flagged)

| Arrow → consuming operation | Disposition |
|---|---|
| `/build` JSON → harness → `<config-label>.meta.json` **(persistence boundary)** | ok on content — the consumer needs `composite`, the producer writes it |
| `<config-label>.meta.json` → report.py's per-run composite lookup **(persistence boundary)** | **→ §2.1** — the consumer cannot construct the artifact's path from what it holds (a run file path) |
| `<config-label>.meta.json` → report.py's mismatch comparison **(second call site, separate row)** | **→ §2.1** — same missing derivation; checked as its own row rather than assumed a copy of the lookup |
| `ingest.py` sidecar → guard **(persistence boundary)** | ok — real record's key set dumped again: `documents` and `chunks` both present |
| `iter_calc(...)` → paired statistics | ok — `Metric(query_id, measure, value)`, 1,401 rows per measure on a real run |
| `AppContext.BaseDirectory` → file enumeration in the deployed image | ok — `WORKDIR /app`, framework-dependent publish, 7 DLLs present |

## 1. Verified-assumptions cross-check

All 23 listed assumptions still hold under a fresh read, including A1 and A4, which the spec records as **failed with the design already corrected** — the correct state, not an outstanding defect.

Re-confirmed this round rather than carried forward: A8 (`docker-compose.yml:367`), A9/A17/A18/A19 (`BenchmarkQueryScenario.cs:37-40,42-60` — and `:109-113`, which is where the run-file naming that drives §2.1 lives), A16 (real sidecar key set), A20 (`resolve_run_paths` still globs `*.trec` and excludes only the qrels path), and both rows added last round — A22 (`parse_measure` still raises `AttributeError` on Python 3.14) and A23 (Dockerfile runtime stage unchanged).

### Span check

Span check found no uncovered dependency. Both gaps raised in round 1 are now covered by A22 and A23, and this round's two findings are defects in stated rules rather than facts nothing verifies.

## 2. Literal-wrongness findings

### 2.1 — `report.py` cannot derive a run file's sidecar path, so every run reports `build: unknown` and the mismatch check never fires

**Description.** The harness writes one sidecar per invocation named from the config label alone. The run files it writes alongside carry an endpoint suffix the sidecar does not. Nothing in the spec says how a consumer holding a run file path arrives at the sidecar path, and no naive construction works:

| what report.py holds | naive `<path>.meta.json` | naive `<stem>.meta.json` | what exists |
|---|---|---|---|
| `runs/reference.chunks.trec` | `runs/reference.chunks.trec.meta.json` ✗ | `runs/reference.chunks.meta.json` ✗ | `runs/reference.meta.json` |

**Evidence.** `BenchmarkQueryScenario.cs:109-110` writes `$"{flags.ConfigLabel}.{SimilarRunSuffix}.trec"` and `$"{flags.ConfigLabel}.{ChunksRunSuffix}.trec"`, with the suffix constants `"similar"` and `"chunks"` at `:39-40`. The real directory confirms it: `arguana-run-2026-08-29/runs/` contains `reference.chunks.trec`, `reference.similar.trec`, `lambda1.chunks.trec`, … The spec's sidecar is `<output-dir>/<config-label>.meta.json` (spec line 81) — `reference.meta.json`.

This is worse than an unimplemented detail because round 1's fix gives it a silent landing: a run whose sidecar is not found prints `build: unknown` and is excluded from the mismatch check. An implementation that gets the derivation wrong therefore looks exactly like an implementation running on pre-sidecar data — every run `unknown`, no mismatch ever reported, no error. Item 3 writes evidence and item 4 never reads it, which is the outcome the spec's own sentence "item 3 writes evidence nobody reads" exists to prevent.

**Proposed fix.** State the derivation as a rule in the spec: strip the trailing `.trec`, then strip a trailing `.similar` or `.chunks` if present, then append `.meta.json` — i.e. `runs/reference.chunks.trec` → `runs/reference.meta.json`. The two endpoint suffixes are fixed constants at `BenchmarkQueryScenario.cs:39-40`, so the rule is closed rather than open-ended. Note in the spec that a run file whose name does not end in one of the two suffixes has no sidecar by construction and takes the `unknown` path.

### 2.2 — The natural `--baseline` invocation sweeps the baseline into its own comparison set, producing a `nan` row and weakening every other comparison's Holm correction

**Description.** `--run` accepts a directory and expands it to every `*.trec` inside. The obvious way to run a sweep is `--baseline runs/reference.chunks.trec --run runs/`, which includes `reference.chunks.trec` — the baseline compared against itself. The spec defines the comparison set as "every `--run` is compared against the baseline" and never excludes it.

**Evidence.** `resolve_run_paths` (`report.py:95-108`) excludes exactly one thing — the `--qrels` file, by absolute path — and nothing else. There is no baseline exclusion, because `--baseline` does not exist yet.

Measured consequences, not asserted:

```
self-comparison paired t: t=nan  p=nan
sd of differences: 0.0  ->  MDE = 0.0
```

The junk row itself is visible. The damage that is *not* visible is to the other comparisons: Holm's correction scales with family size, so one extra null test weakens every real result in the family.

```
Holm over 3 real tests:       [0.0021, 0.0080, 0.0300]
Holm with a 4th null added:   [0.0028, 0.0120, 0.0600]
```

The third comparison crosses 0.05 and is reported non-significant purely because the baseline was swept in. A statistics section whose corrected p-values depend on whether the user passed a directory or listed files individually does not deliver the spec's stated outcome.

**Proposed fix.** State that the baseline is excluded from the comparison set by absolute path, mirroring the qrels exclusion `resolve_run_paths` already performs, and that the exclusion is reported the same way (`[report] excluded … from run discovery (it is the --baseline file)`). Add that the Holm family size is the number of comparisons actually made, after that exclusion.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (`report.py`'s absent-sidecar behaviour undefined). Resolved: the spec now specifies `build: unknown`, exclusion from the mismatch check, an explicit statement rather than silent agreement when a compared run lacks a composite, and names the 168-runs/0-sidecars situation so the first `--baseline` users are not surprised.
- **Round 1 §1 span 1.a** (measure objects vs `parse_measure`). Resolved: A22 added, with the `AttributeError` evidence and the `report.py:52-57` cross-reference.
- **Round 1 §1 span 1.b** (deployed image must contain the assemblies as files). Resolved: A23 added, recording the framework-dependent publish and the 7 DLLs at `/app`.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has two findings, §3 is empty. Both are rules the spec does not state rather than mechanisms that are wrong: the sidecar derivation and the baseline exclusion. Each fix is a sentence or two, and neither disturbs the endpoint, the composite, the guard arithmetic or the statistics.
