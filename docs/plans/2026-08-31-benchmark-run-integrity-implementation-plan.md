# Benchmark Run Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-31-benchmark-run-integrity-design.md` (commit SHA: `e7e06f4`)

**Goal:** Close three ways a benchmark run can be silently wrong — an unattributable build, a t-test on a distribution that violates its assumptions, and a chunk budget that measures itself instead of retrieval.

**Architecture:** Three independent guards. `Iverson.Api` gains a read-only `/build` endpoint returning a composite of the MVIDs of every `Iverson.*` assembly, read from the DLL files rather than from loaded assemblies. `benchmark-query` fetches it during its existing pre-flight validation and writes a per-invocation sidecar beside the run files, and the same pre-flight refuses a chunk budget that cannot reach `DocumentBudget` distinct documents. `report.py` reads those sidecars, flags comparisons across differing builds, and gains a paired-statistics section behind a new `--baseline` flag.

**Tech stack:** .NET 10 (`System.Reflection.Metadata`, in-box), xunit + FluentAssertions, Python 3.14 with `numpy` / `scipy` / `ir_measures` reached through `PYTHONPATH`.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **Validation is fail-fast and finiteness comes first** where a design validates a number. Every range check is a comparison, and every comparison against `NaN` is false.
- **ir_measures measures are constructed as OBJECTS, never via `parse_measure`.** `parse_measure` raises `AttributeError: module 'ast' has no attribute 'Num'` on this box's Python 3.14 (`ast.Num` removed in 3.12). `report.py:52-57` already carries a `CRITICAL:` comment saying so.
- **The chunk-budget threshold is the source document's formula as written**, not a softened one. It refuses three of the nine corpora on disk at multiplier 5. That is accepted, and the spec records why it does not invalidate the decay-weight result.
- **No new third-party dependency.** `System.Reflection.Metadata` is in-box; `numpy`/`scipy`/`ir_measures` come from the `PYTHONPATH` `report.py` already requires.

## File Structure

**Create**
- `Iverson.Server/Iverson.Api/BuildIdentity.cs` — the file-based MVID composite.
- `Iverson.Server/Iverson.Api.Tests/BuildIdentityEndpointTests.cs` — endpoint test.
- `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkBudgetGuard.cs` — the guard's pure arithmetic.
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkBudgetGuardTests.cs` — its test.

**Modify**
- `Iverson.Server/Iverson.Api/Program.cs` — map `GET /build`.
- `Iverson.Server/Iverson.LoadTest/Program.cs` — `IVERSON_HTTP_URL`; a fourth `LoadTestConfig` parameter.
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — pre-flight guard, `/build` fetch, sidecar write.
- `Iverson.Server/Iverson.LoadTest/scripts/report.py` — `--baseline`, sidecar reading, paired statistics.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and reconfirmed by two design-review rounds. **Not re-verified here.** Full evidence is in the spec's `## Verified assumptions` table.

- **A1 (failed, design corrected)** — endpoints are not listener-specific; `/build` serves on both 8080 and 8081, as `/health` and `/probe/*` already do.
- **A4 (failed, design corrected)** — the loaded-assembly set is load-order dependent (`Iverson.Client.Contracts` is never touched directly by `Program.cs`), so MVIDs are read from files.
- **A2** — `AllowAnonymous()` is the established endpoint pattern (`Program.cs:302`).
- **A7** — deterministic builds, no override, so identical source yields an identical composite.
- **A8** — 8081 is published (`docker-compose.yml:367`).
- **A9/A17/A18/A19** — `RunAsync` has a pre-flight validation block with three `Console.Error.WriteLine` + `throw new InvalidOperationException` failures; `flags.KeyMapPath`, `flags.OutputDir`, `DocumentBudget` and `ChunkBudgetMultiplier` are all reachable there.
- **A10** — the `Env(...)` pattern accepts a new variable (`Iverson.LoadTest/Program.cs:26-33`).
- **A11/A12/A15** — `numpy`/`scipy`/`ir_measures` available; `iter_calc` yields per-query `Metric(query_id, measure, value)`.
- **A13** — `report.py` uses argparse.
- **A14** — compared runs may differ in query coverage; the comparison uses the intersection.
- **A16** — `<key-map-path>.stats.json` carries `documents` and `chunks`.
- **A20** — `resolve_run_paths` globs `*.trec` only, so `.meta.json` sidecars are invisible to run discovery.
- **A21** — a single entry-assembly MVID is insufficient.
- **A22** — measures must be objects, never `parse_measure`.
- **A23** — the deployed image is a framework-dependent publish with the assemblies present as files at `/app`.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The four new files do not exist yet | `ls` on each of `BuildIdentity.cs`, `ChunkBudgetGuard.cs`, `BuildIdentityEndpointTests.cs`, `ChunkBudgetGuardTests.cs` — all absent |
| P2 | File path | `Iverson.LoadTest/Benchmark/` is where this shape of pure helper lives | Directory holds `DocumentRanking.cs`, `KeyMap.cs`, `MaxPassageAggregator.cs`, `TrecRunWriter.cs` |
| P3 | File path | `Iverson.LoadTest.Tests/Benchmark/` is where their tests live | Directory holds `DocumentRankingTests.cs`, `KeyMapTests.cs`, `MaxPassageAggregatorTests.cs` |
| P4 | Signature | `AuthTestWebApplicationFactory` is a sealed `WebApplicationFactory<Program>` usable as `IClassFixture<>` with `CreateClient()` | `Helpers/AuthTestWebApplicationFactory.cs:18`; used exactly this way at `AuthenticationPipelineTests.cs:16-28` |
| P5 | Signature | `BenchmarkQueryScenario` is DI-constructed as `(search, identities, logger)` and receives **no** env-derived configuration today | `BenchmarkQueryScenario.cs:28-31`; registered at `Iverson.LoadTest/Program.cs:146` |
| P6 | Signature | `LoadTestConfig` is `record LoadTestConfig(string PostgresCs, string StarRocksCs, string KafkaBootstrap)`, registered as a singleton | `Iverson.LoadTest/Program.cs:376` (declaration), `:53` (construction), `:128` (registration) |
| P7 | Code validity | `System.Reflection.Metadata`'s `PEReader` + `MetadataReader.GetModuleDefinition().Mvid` + `GetGuid` need no package reference | Probe compiled and ran against the real build output with an empty `ItemGroup`; `Iverson.Api.csproj` has no such reference and needs none |
| P8 | Code validity | `Iverson.LoadTest` targets net10.0 and already uses `HttpClient` | `Iverson.LoadTest.csproj:4`; `Program.cs:272` `using var http = new HttpClient();` |
| P9 | Code validity | In the Api.Tests host, `AppContext.BaseDirectory` contains **8** `Iverson.*.dll` including `Iverson.Api.Tests.dll` | `ls Iverson.Api.Tests/bin/Debug/net10.0/Iverson.*.dll` → the 7 production assemblies plus the test assembly |
| P10 | Command | `dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj` is valid | csproj carries `Microsoft.NET.Test.Sdk` 17.12.0 and `xunit` 2.9.3 |
| P11 | Command | `report.py` is invoked as `PYTHONPATH=/path/to/libs python3 …/report.py --run … --qrels …` | Its own docstring, `report.py:41-49` |
| P12 | Command | Commit style is lowercase imperative, no Conventional-Commits prefix | `git log --format=%s -4` |
| P13 | Ordering | Task 1 shares no C# symbol with Task 2 — Task 2 depends only on the endpoint existing at runtime | `BuildIdentity` is referenced only from `Iverson.Api/Program.cs`; the harness reaches it over HTTP |
| P14 | Ordering | Task 3 depends on Task 2's sidecar **naming**, not on its code | `report.py` is Python; the coupling is a file-name convention, so Task 3 can be written and verified against hand-made sidecars |
| P15 | Consumer impact | Adding a fourth positional parameter to `LoadTestConfig` touches exactly one construction site | 9 references total: 1 declaration (`:376`), 1 construction (`:53`), 1 registration (`:128`), and 6 consumers that take the record by type (`DirectSeeder.cs:19`, `ReadPathScenario.cs:14`, `WritePathRunner.cs:30,220`, `WritePathScenario.cs:13`, `KindWritePathScenario.cs:16`, `BenchmarkIngestScenario.cs:25`) — none construct it |
| P16 | Consumer impact | `MapGet("/build")` collides with no existing route and breaks no endpoint-enumerating test | Existing routes: `/health`, `/health/live`, `/probe/{sql,starrocks,vector}`, `/admin/dlq`, plus the Prometheus scrape endpoint. No `EndpointDataSource`/`GetEndpoints` usage anywhere in the solution |
| P17 | Consumer impact | The new pre-flight refusal breaks no in-repo caller of `benchmark-query` | The only in-repo invocation is `Iverson.LoadTest/Program.cs:190`'s command dispatch; the sweeps are driven from scratchpad scripts outside the repo. Those WILL refuse at multiplier 5 on the three dense corpora — accepted, per the spec's Known issues |
| P18 | Consumer impact | `--baseline` is optional, so existing `report.py` invocations are unaffected | argparse; the new section runs only when the flag is present |
| P20 | Signature | `RunAsync`'s pre-flight block holds **four** `Console.Error.WriteLine` + `throw new InvalidOperationException` validations, not three | `BenchmarkQueryScenario.cs:44-62` — `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label`. The spec's inherited A19 says "three instances"; it understates by one. The plan's steps use the measured count |
| P19 | Sibling sweep | *(meta-class: every identifier the plan's code blocks name resolves at its point of use)* | Framework: `PEReader`, `MetadataReader`, `GetModuleDefinition`, `Mvid`, `GetGuid`, `AppContext.BaseDirectory`, `HttpClient`, `argparse`, `ttest_rel`, `norm.ppf` — all compiled or executed in probes. Repo: `AuthTestWebApplicationFactory`, `CreateClient`, `LoadTestConfig`, `Env`, `flags.KeyMapPath`, `flags.OutputDir`, `DocumentBudget`, `ChunkBudgetMultiplier`, `resolve_run_paths`, `iter_calc` — each read at its cited location |

## Tasks

### Task 1: The `/build` endpoint

**Files:**
- Create: `Iverson.Server/Iverson.Api/BuildIdentity.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/BuildIdentityEndpointTests.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`

**Interfaces:**
- Produces: `GET /build` returning `{ composite, assemblies }`. Task 2 consumes it over HTTP, not as a symbol.

- [ ] **Step 1: Create `BuildIdentity.cs`**

```csharp
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Iverson.Api;

/// <summary>
/// Identity of the code actually running, for attributing benchmark runs.
///
/// MVIDs are read from the assembly FILES, never from loaded assemblies:
/// AppDomain.CurrentDomain.GetAssemblies() is load-order dependent, and
/// Iverson.Client.Contracts is never touched directly by Program.cs, so a
/// loaded-set composite could differ between two requests to one process.
///
/// The composite spans every Iverson.* assembly rather than the entry assembly
/// alone: a change confined to Iverson.Vector leaves Iverson.Api's MVID
/// identical, and Iverson.Vector is where the ranking code being measured lives.
/// </summary>
internal static class BuildIdentity
{
    internal static (string Composite, SortedDictionary<string, string> Assemblies) Compute()
    {
        var assemblies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "Iverson.*.dll"))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            assemblies[Path.GetFileNameWithoutExtension(path)] =
                md.GetGuid(md.GetModuleDefinition().Mvid).ToString();
        }

        var sb = new StringBuilder();
        foreach (var (name, mvid) in assemblies)
            sb.Append(name).Append(':').Append(mvid).Append('\n');

        var composite = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
            .ToLowerInvariant();

        return (composite, assemblies);
    }
}
```

- [ ] **Step 2: Map the endpoint in `Program.cs`**

Beside the existing anonymous endpoints (`/health/live` is at `:302`):

```csharp
app.MapGet("/build", () =>
{
    var (composite, assemblies) = BuildIdentity.Compute();
    return Results.Ok(new { composite, assemblies });
}).WithName("BuildIdentity").AllowAnonymous();
```

- [ ] **Step 3: Add `BuildIdentityEndpointTests.cs`**

Follow `AuthenticationPipelineTests` — `IClassFixture<AuthTestWebApplicationFactory>`, `factory.CreateClient()`, `GetAsync("/build")`.

Assert: status 200; `composite` matches `^[0-9a-f]{16}$`; and the `assemblies` map **contains** each of `Iverson.Api`, `Iverson.Client.Contracts`, `Iverson.Embeddings`, `Iverson.Events`, `Iverson.Sql`, `Iverson.StarRocks`, `Iverson.Vector`.

**Assert containment, never an exact count.** In the test host `AppContext.BaseDirectory` is the test project's output directory, which also holds `Iverson.Api.Tests.dll` — itself matching `Iverson.*.dll`. The map has 8 entries there and 7 in the container (P9, A23).

- [ ] **Step 4: Run the suite**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/BuildIdentity.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api.Tests/BuildIdentityEndpointTests.cs
git commit -m "expose a build identity for attributing benchmark runs

A stale iverson-api image, built from a checkout that no longer existed, served
two SciFact runs; a full review pipeline verified the code and none verified
that the code under review was the code running.

The identity spans every Iverson.* assembly rather than the entry assembly
alone: a change confined to Iverson.Vector leaves Iverson.Api's MVID identical,
and Iverson.Vector is where the ranking code being measured lives. MVIDs are
read from the DLL files rather than from loaded assemblies, because
Iverson.Client.Contracts is never touched directly by Program.cs and a
loaded-set composite could differ between two requests to one process.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 2: Harness pre-flight — chunk-budget guard and build-identity sidecar

Both changes land in the same pre-flight validation block of `BenchmarkQueryScenario.RunAsync`, so they are one task: splitting them would fragment one method's validation across two review surfaces.

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkBudgetGuard.cs`
- Create: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkBudgetGuardTests.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:26-33,53,376`
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:28-31,42-60`

**Interfaces:**
- Consumes: Task 1's `GET /build` over HTTP.
- Produces: `<output-dir>/<config-label>.meta.json`, whose naming Task 3 depends on.

- [ ] **Step 1: Add `IVERSON_HTTP_URL` and carry it to the scenario**

`LoadTestConfig` is the existing DI-registered carrier for env-derived configuration. Add a fourth positional parameter (`Program.cs:376`):

```csharp
public sealed record LoadTestConfig(
    string PostgresCs, string StarRocksCs, string KafkaBootstrap, string HttpUrl);
```

Add the env read beside the others at `:26-33`, and pass it at `:53`:

```csharp
var httpUrl = Env("IVERSON_HTTP_URL", "http://localhost:8081");
...
var config = new LoadTestConfig(postgresCs, starRocksCs, kafkaBoots, httpUrl);
```

Only that one construction site changes — the six consumers take `LoadTestConfig` by type (P15). Then add `LoadTestConfig config` to `BenchmarkQueryScenario`'s primary constructor.

- [ ] **Step 2: Create `ChunkBudgetGuard.cs`**

```csharp
namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Refuses a chunk budget that cannot reach DocumentBudget distinct documents.
///
/// SearchChunks' top_k counts CHUNKS and the server does not dedup by parent, so at
/// high chunk density a nominally-50-document budget collapses to far fewer distinct
/// documents after max-passage aggregation -- and R@50 then measures the budget, not
/// retrieval, while looking like a retrieval finding.
///
/// reachable = topK / chunksPerDoc is a WORST case: it assumes a document's chunks are
/// retrieved together. The true count lies between it and topK.
/// </summary>
internal static class ChunkBudgetGuard
{
    internal readonly record struct Result(
        bool   Ok,
        double ChunksPerDocument,
        int    ChunkTopK,
        double ReachableDocuments,
        int    MinimumMultiplier);

    internal static Result Evaluate(
        int documents, int chunks, int documentBudget, int chunkBudgetMultiplier)
    {
        var chunksPerDoc = (double)chunks / documents;
        var topK         = documentBudget * chunkBudgetMultiplier;
        var reachable    = topK / chunksPerDoc;

        return new Result(
            Ok:                 reachable >= documentBudget,
            ChunksPerDocument:  chunksPerDoc,
            ChunkTopK:          topK,
            ReachableDocuments: reachable,
            MinimumMultiplier:  (int)Math.Ceiling(chunksPerDoc));
    }
}
```

`reachable >= documentBudget` is equivalent to `chunkBudgetMultiplier >= chunksPerDoc`, which is why `MinimumMultiplier` is `ceil(chunksPerDoc)`.

- [ ] **Step 3: Wire the guard into the pre-flight block**

After the four existing `flags` validations in `RunAsync`, read `<flags.KeyMapPath>.stats.json`. If it is absent, print one line and proceed — refusing there would block corpora ingested through the C# path, which writes no sidecar. If present, evaluate and refuse on failure using the same pattern the existing validations use:

```csharp
Console.Error.WriteLine(
    $"""
    REFUSING: chunk budget cannot reach DocumentBudget distinct documents.

      corpus              {r.ChunksPerDocument:F2} chunks/doc  ({chunks:N0} chunks / {documents:N0} documents)
      DocumentBudget      {DocumentBudget}
      ChunkBudgetMult     {ChunkBudgetMultiplier}
      chunk top_k         {r.ChunkTopK}
      reachable documents ~{r.ReachableDocuments:F0}  (worst case: a document's chunks retrieved together)

      Raise ChunkBudgetMultiplier to >= {r.MinimumMultiplier}.
    """);
throw new InvalidOperationException("chunk budget cannot reach DocumentBudget distinct documents.");
```

- [ ] **Step 4: Fetch `/build` and write the sidecar**

Still in the pre-flight block, after the guard. `GET {config.HttpUrl}/build`; on any failure, refuse the same way — a run that cannot be attributed should not start, and an API that cannot answer a read-only GET will not serve the sweep either. Write the response through to `Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.meta.json")`:

```json
{
  "configLabel": "w0500",
  "composite": "4c01b2ba54044c6c",
  "assemblies": { "...": "..." },
  "recordedAtUtc": "2026-08-31T14:22:07Z"
}
```

- [ ] **Step 5: Add `ChunkBudgetGuardTests.cs`**

Table-driven over the real corpus densities, so the test states the consequence the spec accepted rather than an invented case:

| documents | chunks | c/doc | multiplier | expected |
|---|---|---|---|---|
| 6,000 | 64,763 | 10.79 | 5 | refuse, `MinimumMultiplier` 11 |
| 6,000 | 33,950 | 5.66 | 5 | refuse, `MinimumMultiplier` 6 |
| 6,000 | 18,622 | 3.10 | 5 | allow |
| 8,674 | 24,282 | 2.80 | 5 | allow |
| 6,000 | 64,763 | 10.79 | 20 | allow |

- [ ] **Step 6: Run both suites**

```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/ChunkBudgetGuard.cs \
        Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkBudgetGuardTests.cs \
        Iverson.Server/Iverson.LoadTest/Program.cs \
        Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs
git commit -m "refuse an unreachable chunk budget and record the build in every run

At FreshStack's 10.79 chunks/document the shipped multiplier of 5 reaches only
~23 distinct documents against a DocumentBudget of 50, so R@50 measures the
budget rather than retrieval while looking like a retrieval finding. The guard
refuses before the run rather than warning into an eight-hour log.

The threshold is a worst case -- it assumes a document's chunks come back
together -- and refuses three of the nine corpora on disk at multiplier 5. That
is deliberate; the spec records why it does not invalidate the decay-weight
result, which was a within-arm comparison over identical captured candidates.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 3: Paired statistics and build surfacing in `report.py`

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/report.py`

**Interfaces:**
- Consumes: Task 2's `<config-label>.meta.json` naming convention. Verified against hand-made sidecars, since real ones require a multi-hour sweep.

- [ ] **Step 1: Add `--baseline` and exclude it from run discovery**

Optional argument, so existing invocations are unchanged. Exclude the baseline from the discovered run set by absolute path, exactly as `resolve_run_paths` already excludes the `--qrels` file, and report it the same way:

```
[report] excluded <path> from run discovery (it is the --baseline file)
```

Without this, the obvious invocation `--baseline runs/reference.chunks.trec --run runs/` compares the baseline against itself: `t = nan`, and — less visibly — the Holm family inflates by one and weakens every other comparison.

- [ ] **Step 2: Locate and read each run's sidecar**

Derivation: strip the trailing `.trec`, then strip a trailing `.similar` or `.chunks` if present, then append `.meta.json`. So `runs/reference.chunks.trec` and `runs/reference.similar.trec` both resolve to `runs/reference.meta.json`. The two suffixes are fixed constants at `BenchmarkQueryScenario.cs:39-40`.

Print `build: <composite>` in each run's scores block. A run with no sidecar prints `build: unknown` and is excluded from the mismatch check; when at least one compared run lacks a composite, say so rather than reporting agreement.

- [ ] **Step 3: Add the paired-statistics section**

Runs only when `--baseline` is given, after the existing three sections. For each measure in `[nDCG@10, R@50, AP]` — **built as objects, never `parse_measure`** — and each discovered run:

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

Per-query values from `ir_measures.iter_calc`, paired over the intersection of the two runs' query sets, reporting the intersection size when they differ. Holm corrects across the runs **within one measure**. `MDE = (z₀.₉₇₅ + z₀.₈₀) · sd / √n` over the differences. The permutation seed is printed so the number is reproducible.

When fewer than 10% of queries changed, print a banner: the *t*-test's assumptions are likely violated and the permutation p is the one to read. That is the check that would have caught the retracted SciFact claim, where 5 of 300 queries changed.

- [ ] **Step 4: Verify against real data — absent sidecars and baseline exclusion**

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 \
    Iverson.Server/Iverson.LoadTest/scripts/report.py \
    --qrels /home/ben/repositories/iverson-benchmark-corpora/arguana-run-2026-08-29/qrels.trec \
    --baseline /home/ben/repositories/iverson-benchmark-corpora/arguana-run-2026-08-29/runs/reference.chunks.trec \
    --run /home/ben/repositories/iverson-benchmark-corpora/arguana-run-2026-08-29/runs/
```

That directory holds 14 run files and no sidecars. Confirm: the exclusion line prints; every run shows `build: unknown`; no comparison reports `t = nan`; and the `[compare]` blocks number one fewer than the run files, per measure.

- [ ] **Step 5: Verify the sidecar derivation and the mismatch warning**

Real sidecars need a multi-hour sweep, so hand-write two throwaway files in that same `runs/` directory — `reference.meta.json` and `w0500.meta.json` — with **different** `composite` values, re-run Step 4's command, and confirm the mismatch warning fires and that both `reference.chunks.trec` and `reference.similar.trec` resolve to `reference.meta.json`. Then set both composites equal, re-run, and confirm no warning. **Delete both files afterwards** — that directory holds irreplaceable measured results and must be left exactly as found.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/report.py
git commit -m "add paired statistics and build surfacing to report.py

A SciFact centroid recall claim was retracted after failing a sign-flip
permutation test: only 5 of 300 queries changed, and a paired t-test on a
distribution that is 98.3% exact zeros violates its own assumptions. The
queries-changed count makes that detectable before any p-value is trusted.

The baseline is excluded from the discovered run set by absolute path, as the
qrels file already is. Without it the obvious directory invocation compares the
baseline against itself, which yields t = nan and inflates the Holm family so
that every other comparison's corrected p-value is weakened.

Measures are constructed as objects: parse_measure raises AttributeError on
Python 3.14, since ast.Num was removed in 3.12.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope":

- **Items 1 and 2** of the source document. Item 1 shipped 2026-08-31; item 2 is blocked behind the `centroid-ablation` branch landing.
- **Items 6 and 7.** Item 6 was answered by the re-chunking crossover; item 7 needs α-nDCG over FreshStack's nugget qrels first.
- **Recording deployed model or configuration** alongside the build identity. The identity answers "what code produced this"; what the code was *configured* with is a separate question this spec does not take on.
- **Choosing values for the tunables** item 1 introduced.
- **Any change to `ingest.py`** or to the corpora on disk.

## Known issues inherited from spec

- The chunk-budget guard's threshold is a worst-case bound, so it can refuse a configuration whose real distinct-document count would have been adequate. Ben chose the source document's formula as written rather than a softened threshold, accepting that it refuses three of the nine corpora on disk at multiplier 5. The alternative — a half-budget threshold — was rejected as an unmeasured judgement factor.
- `/build` is reachable on the gRPC listener as well as the HTTP one, because this codebase cannot express a per-listener endpoint. This matches every existing endpoint and was accepted rather than fixed, since fixing it means introducing host filtering across all of them.
