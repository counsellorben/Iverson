# Benchmark run integrity: build identity, paired statistics, chunk-budget guard

**Source:** items 3, 4 and 5 of `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`
("Tier 2 — harness and tooling; cheap, and each prevents a real error that already happened").

**Scope:** three independent guards against a benchmark run being silently wrong. Each closes a
failure that has already occurred once in this project.

## Why these three

| # | The error that already happened |
|---|---|
| 3 | A stale `iverson-api` image, built from a checkout that no longer existed, served two SciFact runs. A full SDD pipeline — four task reviews, two design reviews, an implementation review and a whole-branch review — verified the *code*, and none verified that the code under review was the code running. |
| 4 | A SciFact centroid recall claim (−0.0167, *t* = −2.25, *p* = 0.025) was retracted after failing a sign-flip permutation test (*p* = 0.063). Only 5 of 300 queries changed; a paired *t*-test on a distribution that is 98.3% exact zeros violates its own assumptions. The failure is detectable in advance from the count of changed queries. |
| 5 | At FreshStack's 10.79 chunks/document the shipped `ChunkBudgetMultiplier = 5` reaches only ~23 distinct documents against a `DocumentBudget` of 50. R@50 would have measured the budget, not retrieval, and would have looked like a retrieval finding. |

## Item 3 — build identity

### The identity is a composite over assemblies, not one MVID

A single entry-assembly MVID does not work here, and this was measured rather than assumed.
Changing only `Iverson.Vector` moved that assembly's MVID (`a0502abf…` → `d0f9f226…`) and left
`Iverson.Api`'s **identical** (`b98b22f4…` both times). The entry assembly is `Iverson.Api`; the
ranking code these benchmarks measure lives in `Iverson.Vector`. `GetEntryAssembly()`'s MVID would
therefore have reported the same identity across a fusion-weight change.

The identity is the SHA-256 over the sorted `name:mvid` lines of every `Iverson.*` assembly,
truncated to 16 hex characters. The per-assembly map is returned alongside it, because when two
composites differ the next question is always *which assembly moved*.

### MVIDs are read from files, never from loaded assemblies

`AppDomain.CurrentDomain.GetAssemblies()` is load-order dependent: `Iverson.Client.Contracts` is
the one project reference `Program.cs` never touches directly, so a composite built from the loaded
set could legitimately differ between two requests to the same process. A build identity that
changes for reasons unrelated to the build is worse than no identity at all.

Instead, enumerate `Iverson.*.dll` in `AppContext.BaseDirectory` and read each MVID from the file:

```csharp
using var pe = new PEReader(File.OpenRead(path));
var md = pe.GetMetadataReader();
var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
```

No assembly loading, no load-order dependency, complete by construction. `System.Reflection.Metadata`
is in-box for `net10.0` — no package reference.

Deterministic builds are the SDK default here with no override in any `.csproj` or `.props`, so
identical source yields an identical composite: the value identifies the code, not the build event.

### The endpoint

`GET /build`, `AllowAnonymous`, registered beside the existing `/health/live` and `/probe/*`
endpoints in `Program.cs`.

```json
{
  "composite": "4c01b2ba54044c6c",
  "assemblies": {
    "Iverson.Api":              "f4228bb9-d568-4328-95bb-572b698f5466",
    "Iverson.Client.Contracts": "5020584e-c3d1-4b1d-b939-7887f1bdcc82",
    "Iverson.Embeddings":       "e44e48aa-117b-4902-9f34-99dc40c46f4c",
    "Iverson.Events":           "36139427-36df-4eeb-af9f-283bd797902f",
    "Iverson.Sql":              "1bf777f6-c388-4c7f-beb0-aa6abcc7626f",
    "Iverson.StarRocks":        "4ac46099-3f2c-457a-923f-d4bea7649e4f",
    "Iverson.Vector":           "26f2f9d0-ecb1-4ca1-ab82-44cafbf3399e"
  }
}
```

**It serves on both listeners, and that is not a choice this codebase can express.** `Program.cs`
contains no `RequireHost`, no `MapWhen` and no per-listener binding, so every mapped endpoint is
reachable on both `8080` (Http2/h2c) and `8081` (Http1) — as `/health`, `/metrics` and `/probe/*`
already are. `/build` is read-only and reveals compilation GUIDs, so it adds no exposure those
endpoints do not already carry. The harness reaches it on 8081 because that is the HTTP/1 listener.

### The harness records it before the sweep

`benchmark-query` GETs `/build` during its existing pre-flight validation block in
`BenchmarkQueryScenario.RunAsync`, and writes `<output-dir>/<config-label>.meta.json`:

```json
{
  "configLabel": "w0500",
  "composite": "4c01b2ba54044c6c",
  "assemblies": { "...": "..." },
  "recordedAtUtc": "2026-08-31T14:22:07Z"
}
```

A sidecar, not the TREC run tag. Folding the identity into the tag would make every config label
build-unique, which breaks exactly the cross-build comparison the tag exists for.

If `/build` is unreachable the run refuses. It cannot produce an attributable result, and an API
that cannot answer a read-only GET will not serve the sweep either.

A new `IVERSON_HTTP_URL` env var (default `http://localhost:8081`) follows the existing `Env(...)`
pattern at `Program.cs:26-33`. Port 8081 is published in `docker-compose.yml:367`.

## Item 4 — paired statistics in `report.py`

A fourth output section, after the existing structural checks, scores and ingest throughput, which
are untouched. `--baseline <run>` turns it on: every `--run` is compared against the baseline.

The comparison runs for each of the three measures `report.py` already computes — **nDCG@10, R@50
and AP** — because the failure that motivated this item was a *recall* claim, not an nDCG one.

Per-query values come from `ir_measures.iter_calc`, which exists in the installed 0.4.3 alongside
the `calc_aggregate` the script already uses. Comparisons are made over the intersection of the two
runs' query sets, and the section reports the intersection size when the runs differ.

```
[compare] w0500.chunks.trec  vs  reference.chunks.trec        (nDCG@10)
  delta            +0.0089
  paired t         t = 3.41   p = 0.0007
  permutation      p = 0.0011   (10,000 sign flips, seed 20260831)
  95% CI           [+0.0038, +0.0140]
  Cohen's d_z      0.197
  MDE @ 80% power  0.0074
  queries changed  118 / 300  (39.3%)
  Holm (3 tests)   p_adj = 0.0033   significant
```

**Holm corrects across the runs within one measure**, not across measures. A sweep asks "which arms
differ on nDCG@10"; pooling three measures into one family would over-correct each of them.

**The `queries changed` line is load-bearing, not decoration.** It is what makes the retracted
SciFact claim detectable *before* any p-value is trusted. When fewer than 10% of queries changed,
the section prints a banner stating that the *t*-test's assumptions are likely violated and the
permutation p is the one to read.

**The permutation test is seeded and the seed is printed.** With 300–1,400 queries an exact
sign-flip enumeration is 2^n and impossible, so it is 10,000 resamples; without a printed seed the
number is not reproducible, and this project has already been bitten by a silently non-reproducible
statistics helper.

`numpy`, `scipy` and `ir_measures` are reached through the same `PYTHONPATH` the script's existing
scoring already requires — no new dependency, and no install on this PEP 668 externally-managed box.

### `report.py` also reads the item-3 sidecars

Each scored run prints its build composite, and the section warns loudly when runs being compared
came from different binaries:

```
!! BUILD MISMATCH: these runs came from different binaries.
   Any comparison between them is confounded.
```

Without this, item 3 writes evidence nobody reads, and a stale build stays as invisible as it was.
A warning, not a hard failure: comparing across builds is sometimes exactly the intent.

**A run with no sidecar prints `build: unknown` and is excluded from the mismatch check.** The
check runs only over the subset of compared runs that carry a composite, and when at least one run
in a comparison lacks one the section says so rather than reporting agreement — silence would make
the guarantee vacuous exactly where it is least warranted. Every run file predating this work is in
that subset: there are 168 run files under the corpora directory today and no sidecars at all, so
the first invocations of `--baseline` will report `unknown` throughout.

Run discovery is unaffected — `resolve_run_paths` globs `*.trec` only, so `.meta.json` sidecars
sitting beside run files are invisible to it.

## Item 5 — chunk-budget guard

In `BenchmarkQueryScenario.RunAsync`, in the existing pre-flight validation block, before any query
is issued. It reads `<key-map-path>.stats.json` — `ingest.py`'s sidecar, which carries `documents`
and `chunks` — and computes:

```
chunksPerDoc = chunks / documents
reachable    = (DocumentBudget × ChunkBudgetMultiplier) / chunksPerDoc
```

Refuse when `reachable < DocumentBudget`. The condition simplifies to
`ChunkBudgetMultiplier ≥ chunksPerDoc`, so the message names the exact fix:

```
REFUSING: chunk budget cannot reach DocumentBudget distinct documents.

  corpus              5.66 chunks/doc  (33,950 chunks / 6,000 documents)
  DocumentBudget      50
  ChunkBudgetMult     5
  chunk top_k         250
  reachable documents ~44  (worst case: a document's chunks retrieved together)

  Raise ChunkBudgetMultiplier to >= 6.
```

Refusal follows the pattern the three existing validations in `RunAsync` already use —
`Console.Error.WriteLine` then `throw new InvalidOperationException` — not `Environment.Exit`.

**If the sidecar is absent the guard prints one line saying so and proceeds.** Refusing there would
block corpora ingested through the C# path, which writes no sidecar.

### What this guard implies about existing results

Applied to the nine corpora on disk, the guard refuses three at multiplier 5 and none at 20:

| corpus | chunks/doc | reachable @ mult 5 | verdict |
|---|---|---|---|
| freshstack-run-2026-08-28 | 10.79 | 23.2 | REFUSE |
| freshstack-correct-2026-08-29 | 10.79 | 23.2 | REFUSE |
| freshstack-chunk256-2026-08-30 | 5.66 | 44.2 | REFUSE |
| nfcorpus (×3) | 4.05 | 61.7 | ok |
| scifact-run-2026-08-26 | 3.85 | 64.9 | ok |
| freshstack-chunk512-2026-08-30 | 3.10 | 80.5 | ok |
| arguana-run-2026-08-29 | 2.80 | 89.3 | ok |

`freshstack-chunk256-2026-08-30` at multiplier 5 is the corpus the decay-weight sensitivity sweep
ran on — the sweep whose result shipped triple B. **That result is not invalidated by this guard.**
The sweep compared two weight triples over the *same captured candidate sets*, so any budget
limitation applied identically to both arms; it was a within-arm comparison, not an absolute
retrieval measurement. What the guard would have blocked is trusting an absolute R@50 from that
configuration — which that analysis never did.

The threshold is the source document's own formula, kept as written rather than softened. It is a
worst case (every retrieved chunk clustering into as few documents as possible); the true distinct
count lies between it and `topK`.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | **FAILED** — endpoints are not listener-specific | No `RequireHost`, `MapWhen` or per-listener binding in `Program.cs`; `/build` serves on both 8080 and 8081. Design corrected |
| A2 | `AllowAnonymous()` is the established pattern | `Program.cs:302` `/health/live`; `:339-351` `/probe/*` |
| A3-A6 | **Superseded** by the file-based read | The loaded-assembly path was abandoned; see A4 |
| A4 | **FAILED** — the loaded set is load-order dependent | `Iverson.Client.Contracts` is the one project reference `Program.cs` never touches directly (0 hits, against 1 each for the other five). Replaced with a file-based `PEReader` read, probe-verified: 7 assemblies, stable composite |
| A7 | Deterministic builds, no override | No `Deterministic` in any `.csproj` or `.props`; SDK default is `true` |
| A8 | 8081 is reachable from the harness | `docker-compose.yml:367` publishes `8081:8081` |
| A9 | `RunAsync` has a pre-flight point and `flags.OutputDir` | `BenchmarkQueryScenario.cs:42-60` — three existing validations before any work |
| A10 | The `Env(...)` pattern accepts a new variable | `Iverson.LoadTest/Program.cs:26-33` |
| A11 | numpy/scipy/ir_measures available on the script's `PYTHONPATH` | `scipy.stats.ttest_rel` present, `norm.ppf(0.975) = 1.96`; ir_measures 0.4.3 |
| A12 | `ir_measures` exposes per-query values | `ir_measures.iter_calc` exists, signature returns `Iterator[Metric]` |
| A13 | `report.py` uses argparse | `report.py:60,290-298` |
| A14 | Compared runs may differ in query coverage | The existing structural check already reports qrels coverage per run; the comparison uses the intersection and reports its size |
| A15 | scipy provides the paired test and normal quantiles | as A11 |
| A16 | The stats sidecar carries `documents` and `chunks` | `freshstack-chunk256-2026-08-30/keymap.json.stats.json`: `documents: 6000, chunks: 33950` |
| A17-A18 | `flags.KeyMapPath` and both budget constants are reachable at the pre-flight point | `BenchmarkQueryScenario.cs:37-38` (consts), `:49` (KeyMapPath validation) |
| A19 | The established failure path is throw, not exit | Three instances in `RunAsync`: `Console.Error.WriteLine` + `throw new InvalidOperationException` |
| A20 | *(sibling sweep over every sidecar this design reads or writes)* No consumer globs a directory in a way the new `.meta.json` would break | `resolve_run_paths` (`report.py:72-95`) globs `*.trec` only, and already excludes the qrels file by absolute path. `ingest.py`'s sidecar and the new meta sidecar both sit beside files discovered by extension |
| A22 | ir_measures measures must be constructed as OBJECTS, never via `parse_measure` | `parse_measure("nDCG@10")` raises `AttributeError: module 'ast' has no attribute 'Num'` on this box (Python 3.14; `ast.Num` removed in 3.12). `report.py:52-57` already carries a `CRITICAL:` comment about this. `iter_calc([nDCG@10, R@50, AP], …)` verified working: `Metric(query_id=…, measure=…, value=…)`, 1,401 rows per measure on a real ArguAna run |
| A23 | The deployed image contains the `Iverson.*` assemblies as discrete files at `AppContext.BaseDirectory` | `Iverson.Api/Dockerfile` runtime stage: `WORKDIR /app`, `COPY --from=build /app/publish .`, `ENTRYPOINT ["dotnet", "Iverson.Api.dll"]` — a plain framework-dependent publish, not single-file or trimmed. The running container lists exactly 7 `Iverson.*.dll` in `/app`. A single-file or trimmed publish would void item 3 |
| A21 | A single entry-assembly MVID is insufficient | Measured: a `Iverson.Vector`-only change moved that MVID but left `Iverson.Api`'s identical. (The two probes render GUIDs differently — the raw metadata heap is big-endian in its first three fields, .NET's `Guid` is little-endian — so the same MVID appears as `d0f9f226-b1ec-a14c-…` in the narrative above and `26f2f9d0-ecb1-4ca1-…` in the endpoint example. The finding is unaffected; the implementation should use .NET's rendering throughout.) |

## Out of scope

- **Items 1 and 2** of the source document. Item 1 shipped 2026-08-31; item 2 is blocked behind the
  `centroid-ablation` branch landing.
- **Items 6 and 7.** Item 6 was answered by the re-chunking crossover; item 7 needs α-nDCG over
  FreshStack's nugget qrels first.
- **Recording deployed model or configuration** alongside the build identity. The identity answers
  "what code produced this"; what the code was *configured* with is a separate question this spec
  does not take on.
- **Choosing values for the tunables** item 1 introduced.
- **Any change to `ingest.py`** or to the corpora on disk.

## Known issues / accepted as out of scope

- The chunk-budget guard's threshold is a worst-case bound, so it can refuse a configuration whose
  real distinct-document count would have been adequate. Ben chose the source document's formula as
  written rather than a softened threshold, accepting that it refuses three of the nine corpora on
  disk at multiplier 5. The alternative — a half-budget threshold — was rejected as an unmeasured
  judgement factor.
- `/build` is reachable on the gRPC listener as well as the HTTP one, because this codebase cannot
  express a per-listener endpoint. This matches every existing endpoint and was accepted rather than
  fixed, since fixing it means introducing host filtering across all of them.
