# Tier 1 Retrieval Defaults Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-06-tier1-retrieval-defaults-design.md` (commit SHA: `62f1567`; CDR rounds 1–3 applied)

**Goal:** Split MMR λ per endpoint with no behaviour change, run three bge-base ingests (SciFact 2048/1792, FreshStack 6k at 2048/1792 and 512/448) with their query runs, and apply three written rules: the chunk-window default, the two λ defaults, and the object-vector representation.

**Architecture:** Phase A is a server change: `VectorRankingOptions` carries `LambdaSimilar`/`LambdaChunks`, the diversifier takes λ as an argument, and `ObjectSearchGrpcService` passes each endpoint's value. Phase B is a harness campaign: `benchmark-query` gains a chunk-budget flag and a caller-visible chunk-diversity sidecar; `report.py` gains α-nDCG@10 via pyndeval and prints the diversity means; a new `similar_arms.py` writes raw head-vs-centroid `.similar` runs; a new `freshstack_nugget_qrels.py` filters the five topics' subtopic qrels to the slice. Phase C reads the gate document's three verdicts into defaults, executing the client/server window-default change only on a rule 7.1 FAIL.

**Tech stack:** .NET 10 (`Iverson.Vector`, `Iverson.Api`, `Iverson.LoadTest`; xunit + FluentAssertions + NSubstitute), Python 3.14 stdlib scripts + pytest 9.1.1 (`report.py`, `similar_arms.py`), `ir_measures` + `pyndeval` in `~/repositories/iverson-benchmark-corpora/python-libs`, Qdrant 1.18.2 REST, TEI cpu-1.8 on bge-base, docker compose 2.40, five client libraries (DotNet, TypeScript, Go, Java, Python) for the gated change.

---

## Global Constraints

- **Phase A ships no behaviour change:** both λ defaults are `0.70`; every existing ranking test passes unchanged except for the mechanical signature updates this plan enumerates.
- **Box discipline (spec §4):** one arm at a time, nothing else on the box during an ingest; every `docker compose` call is a single-service `--no-deps` action from `Iverson.Server/`; **never `stack.py`** (it stops `tei-embed`); never a tier-wide `up`. TEI stays on bge-base throughout — no model recreate anywhere.
- **Chunk counts are fixed** (spec §4, sidecar counts after whitespace-only windows are dropped): `sci-2048` 5,183 / 6,587; `fs-2048` 6,000 / 18,622; `fs-512` 6,000 / 64,735. Any other number invalidates the arm.
- **Chunk-budget multiplier is per corpus:** 5 on SciFact arms, 11 on both FreshStack arms.
- **Run labels are fixed:** `sci-2048`, `fs-2048-l070`, `fs-2048-l050`, `fs-2048-l085`, `fs-2048-l100`, `fs-512-l<λ>` likewise, `head-raw`, `centroid-raw`. Run directories: `~/repositories/iverson-benchmark-corpora/scifact-2048-<date>/`, `freshstack-2048-<date>/`, `freshstack-512-<date>/`; snapshot directories `<same>-qdrant-snapshots/`.
- **Query prefix for bge-base** on every raw query script invocation: `"Represent this sentence for searching relevant passages: "` (spec §3.3).
- **`report.py` invocations are one per rule** with explicit `--run` files (spec §6); never `--pair`; the directory-wide invocation is for reported-only scores.
- **The gated window change (Task 8 step 4) runs only on a rule 7.1 FAIL**, exactly the spec §3.4 set.
- `docs/plans/` and `docs/specs/` are gitignored but tracked: commit their files with `git add -f`.

## File Structure

**Modify (Phase A)**
- `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` — `LambdaSimilar`/`LambdaChunks` replace `Lambda`.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:55-84` — validate both; reject the obsolete key.
- `Iverson.Server/Iverson.Vector/IResultDiversifier.cs`, `ResultDiversifier.cs` — λ parameter; options dependency removed.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:31-45, 302, 488` — options injected; per-endpoint λ.
- `Iverson.Server/docker-compose.yml:441-443` — two env lines.
- Tests: `Iverson.Server/Iverson.Vector.Tests/{VectorRankingOptionsTests.cs,ResultDiversifierTests.cs}`, `Iverson.Server/Iverson.Api.Tests/Grpc/{ObjectSearchGrpcServiceTests.cs,ObjectSearchVectorIntegrationTests.cs}`, `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`.

**Modify (harness)**
- `Iverson.Server/Iverson.LoadTest/Program.cs:261-270, 389-420` — `--chunk-budget-multiplier` flag + help.
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — flag replaces the constant; diversity sidecar.
- `Iverson.Server/Iverson.LoadTest/scripts/report.py`, `test_report.py` — `--nugget-qrels`, α-nDCG, diversity means.

**Create**
- `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkDiversity.cs` — pure distinct-parent counts.
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkDiversityTests.cs`.
- `Iverson.Server/Iverson.LoadTest/scripts/freshstack_nugget_qrels.py` — slice filter for the subtopic qrels.
- `Iverson.Server/Iverson.LoadTest/scripts/similar_arms.py`, `test_similar_arms.py`.
- `docs/plans/2026-09-GATE-tier1-defaults.md` (Task 8).
- Outside the repo: the three run directories and their snapshot directories; `python-libs/pyndeval`.

**Modify (gated, Task 8 step 4 only)** — the spec §3.4 table verbatim: `IversonChunkAttribute.cs:10`, `annotations.ts:166`, `tags.go:293-294` + `:168-171`, `tags_test.go:202-206`, `IversonChunk.java:18,21`, `annotations.py:42-43,64-65,154-155`, `SchemaBuilder.cs:161-162`, `SchemaBuilderTests.cs:619-639`, `IngestContractTests.cs:75` (+ overlap), `scripts/ingest-contract.json` (regenerated).

## Inherited from spec

Verified by `thorough-brainstorming` (spec §9–§10) and NOT re-verified here:

- A1 `Lambda` default 0.70; validation finiteness-first then [0,1]; `Options.Create` registration (`VectorRankingOptions.cs:14-17`, `ServiceCollectionExtensions.cs:55-80`).
- A2 `ResultDiversifier.cs:77-78` is λ's only consumer. A3 `ObjectSearchGrpcService` ctor takes `IOptions<DecayOptions>` (`:40-42`); `Diversify` at `:302` and `:488`. A5 `AddVectorRanking(services, IConfiguration)`.
- A4/A20 dependents of `Lambda`: compose `:442-443` (api only, worker none), `VectorRankingOptionsTests.cs`, `ResultDiversifierTests.cs`, docs; Helm none.
- A7 `ChunkBudgetMultiplier` const `:41` used `:113`/`:369`; `CommandFlags` in `Program.cs:389-416`; sidecar `JsonObject` `:214-222`. A8/A24 raw hits with `ParentKey` in `RunChunksAsync` before the collapse (`:370-385`).
- A9/A21 `alpha_nDCG@10` needs pyndeval; `pip --target` fails only for want of gcc. A10 `read_trec_qrels` keeps the iteration column.
- A11 slice = 6,000 docs / 672 queries across five topics with `qrels.tsv` each. A12 counts and multipliers per window. A13 SciFact 2048/1792 → 6,587.
- A14/A15 live baseline; object points carry `body_vector` + `body_centroid` + `docId`. A16 contract has no query prefixes. A17/A22 the window default's sites and the three tests that assert it. A18 client standard/conformance don't pin it. A19 ≈ 30 min per FreshStack query run. A25 raw `.similar` runs score as `build: unknown`.

## Verified plan-level assumptions

Verified 2026-09-06 at main `62f1567` against the repo, the corpora directory, and the live box (read-only):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | Signature | `VectorRankingOptions` = `Section` const + `WBase/WCentroid/WDecay/Lambda` with a triple-B comment; `AddVectorRanking` binds, validates (finite → non-negative → sum > 0 → λ range), then registers `Options.Create(opts)`, `IResultReranker`, `IResultDiversifier` singletons | `VectorRankingOptions.cs:1-18`; `ServiceCollectionExtensions.cs:55-84` |
| P2 | Signature | `ResultDiversifier(IOptions<VectorRankingOptions> options)` primary ctor; `_o.Lambda` at `:77-78` only; `using Microsoft.Extensions.Options` at `:2` | read |
| P3 | Signature | ctor params `:31-42` end with `IResultReranker reranker, IResultDiversifier diversifier, IOptions<DecayOptions> decayOptions`; field `_decayOptions = decayOptions.Value` at `:45`; both `Diversify` calls are `diversifier.Diversify(diversityCandidates, (int)topK)` | `:302`, `:488` |
| P4 | Code validity | test conventions: `VectorRankingOptionsTests.BuildConfig(params (string Key, string Value)[])` builds `VectorRanking:<Key>` in-memory config (`:11-15`); `ResultDiversifierTests` holds `_diversifier = new(Options.Create(new VectorRankingOptions()))` (`:10`) and calls `Diversify(candidates, topK: N)` at 13 sites; Api wiring pattern is `SearchSimilar_UsesConfiguredHalfLife_NotAHardCodedOne` (`ObjectSearchGrpcServiceTests.cs:2607-2640`); MMR fixtures `SearchSimilar_PromotesDissimilarCandidate_OverNearDuplicate_DespiteLowerFusedScore` (`:2680`) and the SearchChunks `[A, C]` selection test (`:3075`) | read |
| P5 | Code validity | compose `:441-443`: comment lines + `- VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` under `iverson-api` | read |
| P6 | Command | test projects: `Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`, `…/Iverson.Api.Tests/Iverson.Api.Tests.csproj`, `…/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`; solution `Iverson.slnx` at the root | `ls` |
| P7 | Consumer impact | `new ResultDiversifier(Options.Create(…))` at 6 sites: `VectorRankingOptionsTests.cs:59,65`, `ResultDiversifierTests.cs:10`, `ObjectSearchVectorIntegrationTests.cs:93`, `DocumentTemplateValidationTests.cs:337`, `ObjectSearchGrpcServiceTests.cs:78,2617`; `new ObjectSearchGrpcService(` at 3 sites (`ObjectSearchGrpcServiceTests.cs:74,2612`, `DocumentTemplateValidationTests.cs:332`); no appsettings/Launcher/Helm `VectorRanking` entries | grep |
| P8 | Code validity | `config.GetSection("VectorRanking")["Lambda"]` is `null` when unset (IConfiguration indexer contract); env `VectorRanking__Lambda` maps to that key (the existing compose line relies on it) | `.NET` config binding; compose `:443` |
| P9 | Signature | `Program.cs` has `IntFlag(args, "--name", default)` and `StrFlag` (`:406-416`); help text block at `:255-270`; `CommandFlagsTests.cs` exists | read |
| P10 | Code validity | the query loop appends `(query.QueryId, chunks.Ranked)` per query (`:276-280`) and `RunChunksAsync` returns `(Ranked, Failed, Unresolved)` with the raw `chunks` list of `(ParentKey, Score, Text)` in stream order (`:362-385`); the meta sidecar is a `JsonObject` written at `:220-222` | read |
| P11 | Code validity | `Iverson.LoadTest.Tests/Benchmark/` holds xunit + FluentAssertions tests (`ChunkBudgetGuardTests.cs`, `DocumentRankingTests.cs`, `CommandFlagsTests.cs`) | `ls`; `:1-25` |
| P12 | Code validity | the REFUSING text names `ChunkBudgetMultiplier` at `:121,123,130` | grep |
| P13 | Signature | `report.py`: `score_run(qrels, run_path, measures)` / `print_scores(path, measures, results, composite)` (`:282-293`); `measures = [nDCG@10, R@50, AP]` inside `main()` (`:763`); qrels loaded once via `read_trec_qrels` (`:784`); `sidecar_path_for` strips `.trec` then `.similar`/`.chunks` (`:158-172`); paired stats loop over `measures` (`:561`) | read |
| P14 | Code validity | `test_report.py` helpers `write_qrels(path, rows)`, `write_run(path, rows, tag)`, fixture `qrels_path(tmp_path)` (`:18-45`) | read |
| P15 | Code validity | `from ir_measures import alpha_nDCG`; `alpha_nDCG@10` is a measure object comparable by value | ran |
| P16 | File path | `freshstack-5topic/<t>/freshstack/qrels.tsv` is 4-column TREC (`qid \t nugget \t corpus_id \t rel`, rel ∈ {0,1}); the slice's query-level `qrels.trec` (iteration `0`, 20,209 rows) is at `freshstack-chunk512-2026-08-30/qrels.trec`; slice ids in `…/beir/{corpus,queries}.jsonl` | `head`, `awk`, `wc` |
| P17 | Code validity | `sample_corpus.py` is stdlib + argparse with a module docstring and `def main()`; `freshstack_to_jsonl.py` documents the tsv layout | read |
| P18 | Signature | `multivector.py` exports `trec_lines(query_id, ranked, run_tag)`, `scroll(name, with_vector, payload_fields)`, `collection_info(name)`, `require_collection(name)`; `ingest.embed(text, model, document_prefix, embed_url)`; `ingest.DEFAULT_OBJECT_COLLECTION` | `grep '^def '` |
| P19 | Code validity | `POST /collections/benchmark_documents_tenant_bypass/points/search {vector:{name:"body_centroid",…}, limit, with_payload:["docId"]}` → 200 with `payload.docId` | live |
| P20 | Code validity | `test_multivector.py` import idiom (`sys.path.insert` + `import multivector  # noqa: E402`) | `:8-14` |
| P21 | Command | the multivector plan's executed blocks: preconditions `:711-727`, ingest `:742-758`, snapshot `:760-770`, schema row + API recreate + `benchmark-query` `:772-790`, restore `:840-862` — copied below with labels/paths substituted | read |
| P22 | Command | API recreate with λ env: `VECTOR_RANKING_LAMBDA_SIMILAR=<λ> VECTOR_RANKING_LAMBDA_CHUNKS=<λ> docker compose up -d --no-deps iverson-api` after Task 1 lands the two compose lines; `EMBED_MODEL_ID`/`BENCH_EMBED_MODEL` default to bge-base so are not passed | compose `:443` pattern; Phase 2 defaults |
| P23 | Code validity | the slice `corpus.jsonl` is BEIR-shaped (`_id`, `text`) — `ingest.py --corpus <run>/beir/corpus.jsonl` parses it; keys are `uuid5(dir-name:docId)` so each run dir gets its own key map | `head -c`; `ingest.py` docstring |
| P24 | File path | `freshstack-chunk512-2026-08-30/qrels.trec` (query-level) is the file to copy as each FreshStack run's `qrels.trec` | `wc -l` 20,209 |
| P25 | Code validity | `benchmark-query` survives Authentik's 2 h token boundary (`cf9cbb8` on main) | `git log` |
| P26/P27 | File path | snapshot loop + `RESTORE.md` convention (multivector plan `:760-770`); `scifact-bge-base-qdrant-snapshots/` holds two `.snapshot` files + `RESTORE.md` — the final restore set | `ls` |
| P28 | File path | `docs/plans/2026-09-GATE-multivector.md` exists as the gate-document template | `ls` |
| P30 | Command | `IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract` rewrites `ingest-contract.json`; without the variable the test asserts equality with a line-level diff | `IngestContractTests.cs:26,60,85-100` |
| P32 | File path | `SchemaBuilderTests.cs:620-639` `BuildDescriptor_WithDocumentTemplate_UnsetTokenFields_DefaultToFallbackValues` asserts `MaxTokens 512` / `Overlap 64` | read |
| P31 | Command | commit convention: lowercase imperative subject, `area:` prefix only for compose | `git log` |
| P33 | Ordering | Tasks 1–4 touch disjoint files (`Iverson.Vector`/`Iverson.Api`/compose; `Iverson.LoadTest` C#; `scripts/report.py` + new script; `scripts/similar_arms.py`) and can run in any order; Task 5 needs Tasks 1–4 merged and the API image rebuilt; Tasks 6–7 need Task 3's pyndeval and nugget qrels; Task 8 needs 5–7 | by construction |
| P34 | Command | `iverson-api` builds from `context: ..` with `Iverson.Server/Iverson.Api/Dockerfile` (`docker-compose.yml:427-429`), so `docker compose build iverson-api` picks up Task 1; composite via `curl 127.0.0.1:8081/build` | read |
| P35 | Command | client test commands (Task 8 step 4): `dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx`; `go test ./...` in `Iverson.Clients/Go` (`go.mod`); `npm test` in `Iverson.Clients/TypeScript` (`package.json:16` = `npm run typecheck && vitest run`); `mvn -q test` in `Iverson.Clients/Java` (parent `pom.xml` modules client/sample/conformance; no `mvnw`); `python3 -m pytest tests` in `Iverson.Clients/Python` (`tests/`, `pyproject.toml`); toolchains present: mvn 3.9.9, java 21, go 1.22, node 24 | `ls`, `which` |
| P36 | Code validity | `test_report.py` drives the CLI with `monkeypatch.setattr(sys, "argv", [...])` then `report.main()` (`:175-178`); `report.main()` takes no arguments (`report.py:715`) | read |

## Tasks

### Task 1: Phase A — λ per endpoint (no behaviour change)

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`, `ServiceCollectionExtensions.cs:55-84`, `IResultDiversifier.cs`, `ResultDiversifier.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:31-45,302,488`, `Iverson.Server/docker-compose.yml:441-443`
- Test: `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`, `ResultDiversifierTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `ObjectSearchVectorIntegrationTests.cs`, `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`

**Interfaces**
- Produces: `VectorRankingOptions.LambdaSimilar/LambdaChunks`; `IResultDiversifier.Diversify(ranked, topK, lambda)`; compose env `VectorRanking__LambdaSimilar` / `VectorRanking__LambdaChunks` (Tasks 6–7).

- [ ] **Step 1: Write the failing tests.**

In `VectorRankingOptionsTests.cs`: change every `("Lambda", "0.70")` entry to two entries `("LambdaSimilar", "0.70"), ("LambdaChunks", "0.70")`; replace `AddVectorRanking_LambdaOutOfRange_Throws` with two tests, one per key (`("LambdaSimilar", "1.5")`, `("LambdaChunks", "-0.1")`, each with the other at `0.70`), and add:

```csharp
    [Fact]
    public void AddVectorRanking_ObsoleteLambdaKey_Throws_NamingBothNewKeys()
    {
        var config = BuildConfig(("WBase", "0.45"), ("WCentroid", "0.45"), ("WDecay", "0.10"), ("Lambda", "0.70"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*LambdaSimilar*").WithMessage("*LambdaChunks*");
    }

    [Fact]
    public void AddVectorRanking_Defaults_AreSeventyPercentOnBothEndpoints()
    {
        var provider = new ServiceCollection().AddVectorRanking(BuildConfig()).BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<VectorRankingOptions>>().Value;
        opts.LambdaSimilar.Should().Be(0.70);
        opts.LambdaChunks.Should().Be(0.70);
    }
```

Rewrite `ResultDiversifier_NonDefaultLambda_ChangesSelectionOrderFromDefault` (`:46-68`) to construct `new ResultDiversifier()` once and call `Diversify(candidates, topK: 2, lambda: 0.70)` versus `lambda: 0.99`; the hand-computed expectations in its comments are unchanged.

In `ResultDiversifierTests.cs`: `private readonly ResultDiversifier _diversifier = new();` and every `Diversify(candidates, topK: N)` → `Diversify(candidates, topK: N, lambda: 0.70)` (13 sites; the arithmetic in the comments assumed 0.70). Add:

```csharp
    [Fact]
    public void Diversify_LambdaOne_IsPlainTakeTopK_OnBothBranches()
    {
        // With and without diversity vectors, λ = 1.00 must reduce to Take(topK) bit-for-bit.
        var withVectors = new[]
        {
            new DiversifyCandidate(1, 0.95, new[] { 1f, 0f }),
            new DiversifyCandidate(2, 0.90, new[] { 1f, 0f }),   // near-duplicate of 1
            new DiversifyCandidate(3, 0.60, new[] { 0f, 1f }),
        };
        var without = withVectors.Select(c => c with { DiversityVector = null }).ToArray();

        _diversifier.Diversify(withVectors, topK: 2, lambda: 1.00).Select(r => r.Id).Should().Equal(1UL, 2UL);
        _diversifier.Diversify(without,     topK: 2, lambda: 1.00).Select(r => r.Id).Should().Equal(1UL, 2UL);
    }
```

In `ObjectSearchGrpcServiceTests.cs`, at the three `new ObjectSearchGrpcService(` sites (`:74`, `:2612`, and `DocumentTemplateValidationTests.cs:332`) and `ObjectSearchVectorIntegrationTests.cs:93`, replace `new ResultDiversifier(Options.Create(new VectorRankingOptions()))` with `new ResultDiversifier()` and append `Options.Create(new VectorRankingOptions())` as the argument **before** `Options.Create(new DecayOptions…)` (the new ctor parameter order in step 2). Then add the two wiring tests beside `SearchSimilar_UsesConfiguredHalfLife_NotAHardCodedOne` (`:2607`), each reusing the candidate fixture of the MMR promotion tests (`:2680` for SearchSimilar, `:3075` for SearchChunks), whose hand-computed 0.70 arithmetic promotes C over the near-duplicate B:

```csharp
    // VectorRankingOptionsTests proves the diversifier honours whatever λ it is handed — it does
    // NOT prove ObjectSearchGrpcService passes the CONFIGURED per-endpoint value through. Bind
    // λ = 1.00 on ONE endpoint and assert that endpoint reduces to Take(topK) while the other
    // still promotes the dissimilar candidate: a hard-coded 0.70 at either call site fails
    // exactly one of these two tests.
    [Fact]
    public async Task SearchSimilar_UsesConfiguredLambdaSimilar_NotAHardCodedOne()
    {
        // fixture and stubs as in SearchSimilar_PromotesDissimilarCandidate_…; construct the sut with
        //   Options.Create(new VectorRankingOptions { LambdaSimilar = 1.00, LambdaChunks = 0.70 })
        // expect written ids [A, B] (plain Take(2)), not [A, C].
    }

    [Fact]
    public async Task SearchChunks_UsesConfiguredLambdaChunks_NotAHardCodedOne()
    {
        // fixture and stubs as in the SearchChunks [A, C] selection test at :3075; construct the sut with
        //   Options.Create(new VectorRankingOptions { LambdaSimilar = 0.70, LambdaChunks = 1.00 })
        // expect [A, B] on SearchChunks; and with the two values swapped expect [A, C] again.
    }
```
Write the bodies by copying the two existing tests' arrangement verbatim and changing only the options object and the expected ids.

- [ ] **Step 2: Run the three suites; expect compile failures only from the new names** (`LambdaSimilar`, the 3-arg `Diversify`, the parameterless `ResultDiversifier`, the extra ctor argument):
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -5
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 3: Implement.**

`VectorRankingOptions.cs` — replace the `Lambda` line:
```csharp
    // MMR λ per endpoint. Both 0.70 until the Tier 1 gate (docs/plans/2026-09-GATE-tier1-defaults.md)
    // sets each by rule: SearchChunks returns the chunk list itself, so its λ is a caller-visible
    // choice the document-level benchmark cannot see; SearchSimilar's measured R@50 price for
    // diversification is 9–13 % (spec 2026-09-06-tier1-retrieval-defaults-design §1).
    public double LambdaSimilar { get; set; } = 0.70;
    public double LambdaChunks  { get; set; } = 0.70;
```

`ServiceCollectionExtensions.AddVectorRanking` — before `Bind`, reject the obsolete key; then validate both λ values wherever `opts.Lambda` was validated:
```csharp
        var section = config.GetSection(VectorRankingOptions.Section);
        if (section["Lambda"] is not null)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:Lambda is no longer read. Set " +
                $"{VectorRankingOptions.Section}:LambdaSimilar and {VectorRankingOptions.Section}:LambdaChunks " +
                "(env VectorRanking__LambdaSimilar / VectorRanking__LambdaChunks) instead.");

        var opts = new VectorRankingOptions();
        section.Bind(opts);

        if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
            !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.LambdaSimilar) || !double.IsFinite(opts.LambdaChunks))
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: every value must be finite " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
                $"LambdaSimilar={opts.LambdaSimilar}, LambdaChunks={opts.LambdaChunks}).");
        // … weight checks unchanged …
        if (opts.LambdaSimilar is < 0 or > 1)
            throw new InvalidOperationException($"{VectorRankingOptions.Section}:LambdaSimilar must be in [0,1] (was {opts.LambdaSimilar}).");
        if (opts.LambdaChunks is < 0 or > 1)
            throw new InvalidOperationException($"{VectorRankingOptions.Section}:LambdaChunks must be in [0,1] (was {opts.LambdaChunks}).");
```

`IResultDiversifier.cs`:
```csharp
    /// <param name="ranked">Candidates in fused-descending order, as <c>IResultReranker.Rerank</c> returns them.</param>
    /// <param name="lambda">MMR trade-off in [0,1]; 1.00 reduces to <c>Take(topK)</c> exactly.</param>
    IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK, double lambda);
```

`ResultDiversifier.cs`: drop `using Microsoft.Extensions.Options;`, the primary-constructor parameter and the `_o` field; the signature becomes `public sealed class ResultDiversifier : IResultDiversifier` with `Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK, double lambda)`; at `:77-78` replace `_o.Lambda` with `lambda` (both occurrences).

`ObjectSearchGrpcService.cs`: add `IOptions<VectorRankingOptions> rankingOptions,` immediately before `IOptions<DecayOptions> decayOptions)` in the primary constructor; add `private readonly VectorRankingOptions _ranking = rankingOptions.Value;` beside `_decayOptions`; `:302` → `diversifier.Diversify(diversityCandidates, (int)topK, _ranking.LambdaSimilar)`; `:488` → `diversifier.Diversify(diversityCandidates, (int)topK, _ranking.LambdaChunks)`. `Iverson.Vector`'s namespace is already imported there (the reranker types are used).

`docker-compose.yml:441-443` → 
```yaml
      # MMR lambda per endpoint; 0.70 is VectorRankingOptions' default for both. The Tier 1 sweep
      # recreates the API with both at one value, e.g.
      #   VECTOR_RANKING_LAMBDA_SIMILAR=1.00 VECTOR_RANKING_LAMBDA_CHUNKS=1.00 docker compose up -d --no-deps iverson-api
      - VectorRanking__LambdaSimilar=${VECTOR_RANKING_LAMBDA_SIMILAR:-0.70}
      - VectorRanking__LambdaChunks=${VECTOR_RANKING_LAMBDA_CHUNKS:-0.70}
```

- [ ] **Step 4: Run the suites and the solution build — all green; then prove the wiring tests are falsifiable** by temporarily hard-coding `0.70` at `:302` and confirming exactly `SearchSimilar_UsesConfiguredLambdaSimilar_NotAHardCodedOne` fails, then reverting.
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -3
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
dotnet build Iverson.slnx 2>&1 | tail -2
cd Iverson.Server && docker compose config | grep -c 'VectorRanking__Lambda\(Similar\|Chunks\): "0.70"'   # 2
```

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Server/Iverson.Vector Iverson.Server/Iverson.Vector.Tests Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests Iverson.Server/docker-compose.yml
git commit -m "split MMR lambda per endpoint: LambdaSimilar and LambdaChunks, both 0.70, obsolete Lambda key rejected"
```

### Task 2: benchmark-query — chunk-budget flag and the chunk-diversity sidecar

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/ChunkDiversity.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:261-270, 389-420`, `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/ChunkDiversityTests.cs`, `CommandFlagsTests.cs`

**Interfaces**
- Produces: `--chunk-budget-multiplier`; `<label>.chunks.diversity.json` (Task 3 reads it; Tasks 5–7 run it).

- [ ] **Step 1: Failing tests.** `ChunkDiversityTests.cs` (xunit + FluentAssertions, namespace `Iverson.LoadTest.Tests.Benchmark`):
```csharp
public class ChunkDiversityTests
{
    private static IReadOnlyList<string> Parents(params string[] p) => p;

    [Fact]
    public void Count_DistinctParentsWithinEachPrefix_NotAcrossTheWholeList()
    {
        // 12 hits: parents a,a,b,a,c,c,d,e,e,f | g,h  -> top10 has 6 distinct, top50 has 8
        var hits = Parents("a", "a", "b", "a", "c", "c", "d", "e", "e", "f", "g", "h");
        var (at10, at50) = ChunkDiversity.Count(hits);
        at10.Should().Be(6);
        at50.Should().Be(8);
    }

    [Fact]
    public void Count_FewerHitsThanTheWindow_CountsWhatIsThere()
    {
        var (at10, at50) = ChunkDiversity.Count(Parents("a", "b", "a"));
        at10.Should().Be(2);
        at50.Should().Be(2);
    }

    [Fact]
    public void Count_Empty_IsZero() => ChunkDiversity.Count(Array.Empty<string>()).Should().Be((0, 0));
}
```
In `CommandFlagsTests.cs` add a case that `--chunk-budget-multiplier 11` parses to `ChunkBudgetMultiplier == 11` and that the default is `5`, following the file's existing pattern.

- [ ] **Step 2: Implement.** `ChunkDiversity.cs`:
```csharp
namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Distinct parent documents among the first 10 and first 50 raw SearchChunks hits — the
/// diversity a caller consuming the chunk list actually sees. Taken BEFORE max-passage
/// aggregation, which is the only place the number exists (spec §3.3).
/// </summary>
public static class ChunkDiversity
{
    public static (int At10, int At50) Count(IReadOnlyList<string> parentKeysInRankOrder)
    {
        var at10 = parentKeysInRankOrder.Take(10).Distinct(StringComparer.Ordinal).Count();
        var at50 = parentKeysInRankOrder.Take(50).Distinct(StringComparer.Ordinal).Count();
        return (at10, at50);
    }
}
```
`Program.cs`: `public int ChunkBudgetMultiplier { get; init; } = 5;` in `CommandFlags`, `ChunkBudgetMultiplier = IntFlag(args, "--chunk-budget-multiplier", 5),` in `Parse`, and a help line under `--config-label`: `--chunk-budget-multiplier <n>  benchmark-query only: SearchChunks top_k = 50 × n (default 5; 11 for FreshStack's 10.79 chunks/doc)`.
`BenchmarkQueryScenario.cs`: delete the const at `:41`; read `flags.ChunkBudgetMultiplier` at `:113` and `:369` (thread it into `RunChunksAsync` as a parameter) and in the REFUSING text (`:121,123,130`: "Raise --chunk-budget-multiplier to >= …"); add `["chunkBudgetMultiplier"] = flags.ChunkBudgetMultiplier` to the meta sidecar object at `:214-219`. `RunChunksAsync` returns one more tuple element `(int At10, int At50) Diversity = ChunkDiversity.Count(chunks.Select(c => c.ParentKey).ToList())` computed right after the stream is read; the loop at `:276-280` collects `(query.QueryId, chunks.Diversity)` into a list, and after the two `TrecRunWriter` calls the scenario writes `Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.chunks.diversity.json")` as
```json
{ "label": "...", "queries": 672, "meanDistinctParentsAt10": 7.83, "meanDistinctParentsAt50": 31.2,
  "perQuery": { "<qid>": { "at10": 8, "at50": 30 }, ... } }
```
using the existing `SidecarWriteOptions`.

- [ ] **Step 3: Tests green; smoke the flag.**
```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj 2>&1 | tail -3
cd Iverson.Server/Iverson.LoadTest && dotnet run -c Release -- benchmark-query --help 2>&1 | grep -c "chunk-budget-multiplier"   # 1
```

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest Iverson.Server/Iverson.LoadTest.Tests
git commit -m "benchmark-query: --chunk-budget-multiplier flag and a per-run chunk-diversity sidecar"
```

### Task 3: report.py α-nDCG and diversity means; the slice's nugget qrels; pyndeval

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/report.py`, `test_report.py`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/freshstack_nugget_qrels.py`
- Outside the repo: `~/repositories/iverson-benchmark-corpora/python-libs/pyndeval*`

**Interfaces**
- Consumes: Task 2's `<label>.chunks.diversity.json` shape.
- Produces: `report.py --nugget-qrels`; `<run>/qrels.nugget.trec` (Tasks 6–7).

- [ ] **Step 1: pyndeval (precondition: gcc).**
```bash
which gcc || echo "NO GCC — Ben: sudo apt install gcc, then rerun"
python3 -m pip install --target /home/ben/repositories/iverson-benchmark-corpora/python-libs pyndeval 2>&1 | tail -2
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -c "import pyndeval, ir_measures; from ir_measures import alpha_nDCG; print('ok', alpha_nDCG@10)"
```
Nothing in this task is committed until this prints `ok` — the α-nDCG tests below need the provider.

- [ ] **Step 2: Failing tests in `test_report.py`** using its `write_qrels`/`write_run` helpers:
```python
def test_alpha_ndcg_appears_only_with_nugget_qrels(tmp_path, qrels_path, capsys, monkeypatch):
    run = tmp_path / "x.similar.trec"
    write_run(run, [("q1", "d1", 1, 0.9), ("q1", "d2", 2, 0.8)])
    nugget = tmp_path / "qrels.nugget.trec"
    nugget.write_text("q1 s1 d1 1\nq1 s2 d2 1\n")
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    assert "alpha_nDCG@10" not in capsys.readouterr().out
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path, "--nugget-qrels", str(nugget)])
    report.main()
    out = capsys.readouterr().out
    assert "alpha_nDCG@10" in out and "1.0000" in out     # both subtopics covered in the top 2


def test_diversity_means_print_from_sidecar(tmp_path, qrels_path, capsys, monkeypatch):
    run = tmp_path / "y.chunks.trec"
    write_run(run, [("q1", "d1", 1, 0.9)])
    (tmp_path / "y.chunks.diversity.json").write_text(
        '{"label":"y","queries":1,"meanDistinctParentsAt10":7.5,"meanDistinctParentsAt50":31.0,"perQuery":{}}')
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    out = capsys.readouterr().out
    assert "distinct parents @10  7.50" in out and "@50  31.00" in out
```
(The `monkeypatch.setattr(sys, "argv", …)` + `report.main()` idiom is the file's existing way of driving the CLI in-process, `test_report.py:175-178`; `write_run`'s row tuples follow its existing signature.)

- [ ] **Step 3: Implement in `report.py`.** Add `--nugget-qrels` (`help="TREC qrels with the nugget/subtopic id in the iteration column; adds alpha_nDCG@10 (pyndeval) to every scored run"`). In `main`, after the measures list: `if args.nugget_qrels: from ir_measures import alpha_nDCG; nugget_qrels = list(ir_measures.read_trec_qrels(args.nugget_qrels)); measures.append(alpha_nDCG@10)`. Because `score_run`/`per_query_values`/`run_paired_statistics` take one `qrels` list, route by measure: a helper `qrels_for(measure)` returns `nugget_qrels` when `str(measure).startswith("alpha_nDCG")` else `qrels`, used at every `calc_aggregate`/`iter_calc` call site (`:285`, `:402`, `:559-566`). Diversity: `diversity_sidecar_for(run_path)` = `sidecar_path_for(run_path)` with `.meta.json` → `.chunks.diversity.json` only when the run name ends `.chunks.trec`; when it exists, `print_scores` prints two extra lines `  distinct parents @10  {mean:.2f}` / `  distinct parents @50  {mean:.2f}`. Update the module docstring's measure list.

- [ ] **Step 4: `freshstack_nugget_qrels.py`** (stdlib, argparse, docstring in `sample_corpus.py`'s style):
```python
#!/usr/bin/env python3
"""Filter the five FreshStack topics' subtopic qrels (qid \\t nugget \\t corpus_id \\t rel) to one
harness slice, writing TREC 4-column qrels with the nugget id in the iteration column — the
input `report.py --nugget-qrels` needs for alpha_nDCG@10.

    python3 freshstack_nugget_qrels.py --slice <run>/beir --topics-root .../freshstack-5topic --out <run>/qrels.nugget.trec

Keeps a row iff its query id is in the slice's queries.jsonl AND its corpus id is in the slice's
corpus.jsonl; keeps rel as-is (0 rows included — pyndeval treats rel>0 as relevant). Prints the
kept/dropped counts per topic and exits non-zero if any slice query ends up with no row.
"""
```
Body: a pure `filter_rows(rows, query_ids, corpus_ids)` generator over `(qid, nugget, docid, rel)` tuples, plus `main()` that loads the two id sets from the slice's `queries.jsonl`/`corpus.jsonl`, iterates `<topics-root>/*/freshstack/qrels.tsv`, writes `f"{qid} {nugget} {docid} {rel}\n"` for kept rows, prints kept/dropped per topic, and exits non-zero naming any slice query with no kept row. `test_freshstack_nugget_qrels.py` (the `test_multivector.py` import idiom):

```python
def test_filter_keeps_only_rows_inside_the_slice():
    rows = [("q1", "q1_0", "dA", 1), ("q1", "q1_1", "dZ", 1), ("q9", "q9_0", "dA", 1), ("q1", "q1_0", "dB", 0)]
    kept = list(freshstack_nugget_qrels.filter_rows(rows, {"q1"}, {"dA", "dB"}))
    assert kept == [("q1", "q1_0", "dA", 1), ("q1", "q1_0", "dB", 0)]   # rel 0 kept; dZ and q9 dropped


def test_main_exits_when_a_slice_query_has_no_judged_doc(tmp_path, monkeypatch):
    (tmp_path / "beir").mkdir(); (tmp_path / "t" / "freshstack").mkdir(parents=True)
    (tmp_path / "beir" / "queries.jsonl").write_text('{"_id": "q1", "text": "x"}\n{"_id": "q2", "text": "y"}\n')
    (tmp_path / "beir" / "corpus.jsonl").write_text('{"_id": "dA", "text": "a"}\n')
    (tmp_path / "t" / "freshstack" / "qrels.tsv").write_text("q1\tq1_0\tdA\t1\n")
    monkeypatch.setattr(sys, "argv", ["x", "--slice", str(tmp_path / "beir"), "--topics-root", str(tmp_path), "--out", str(tmp_path / "out.trec")])
    with pytest.raises(SystemExit) as e:
        freshstack_nugget_qrels.main()
    assert "q2" in str(e.value)
```

- [ ] **Step 5: Run the Python suites.**
```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py Iverson.Server/Iverson.LoadTest/scripts/test_freshstack_nugget_qrels.py -q 2>&1 | tail -3
```

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/report.py Iverson.Server/Iverson.LoadTest/scripts/test_report.py Iverson.Server/Iverson.LoadTest/scripts/freshstack_nugget_qrels.py Iverson.Server/Iverson.LoadTest/scripts/test_freshstack_nugget_qrels.py
git commit -m "report.py: alpha-nDCG@10 via --nugget-qrels and chunk-diversity means; add the FreshStack nugget-qrels slice filter"
```

### Task 4: `similar_arms.py` — raw head-vs-centroid `.similar` runs

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/similar_arms.py`, `test_similar_arms.py`

**Interfaces**
- Consumes: `multivector.trec_lines`, `multivector.scroll`, `multivector.require_collection`; `ingest.embed`, `ingest.qdrant_request`, `ingest.DEFAULT_OBJECT_COLLECTION`.
- Produces: `<run>/runs/head-raw.similar.trec`, `<run>/runs/centroid-raw.similar.trec` (Tasks 5–7).

- [ ] **Step 1: Failing tests** (`test_similar_arms.py`, the `test_multivector.py` idiom):
```python
def test_compose_query_applies_the_prefix_verbatim():
    assert similar_arms.compose_query("Represent this sentence for searching relevant passages: ", "q") == \
        "Represent this sentence for searching relevant passages: q"


def test_rank_hits_reads_docid_and_keeps_qdrant_order():
    hits = [{"id": 1, "score": 0.9, "payload": {"docId": "A"}}, {"id": 2, "score": 0.8, "payload": {"docId": "B"}}]
    assert similar_arms.rank_hits(hits, "q1", 2) == [("A", 0.9), ("B", 0.8)]


def test_rank_hits_fails_loud_on_missing_docid_or_short_list():
    with pytest.raises(SystemExit):
        similar_arms.rank_hits([{"id": 1, "score": 0.9, "payload": {}}], "q1", 1)
    with pytest.raises(SystemExit):
        similar_arms.rank_hits([{"id": 1, "score": 0.9, "payload": {"docId": "A"}}], "q1", 2)
```

- [ ] **Step 2: Implement.**
```python
#!/usr/bin/env python3
"""Raw head-vs-centroid .similar runs (spec 2026-09-06-tier1-retrieval-defaults-design §3.3).

For every query in <run>/beir/queries.jsonl: embed once as --query-prefix + text (the ingest
contract carries document prefixes only; bge-base's query instruction is
"Represent this sentence for searching relevant passages: "), then two raw named-vector
searches on the object collection — body_vector (the head-512 object vector) and body_centroid
— limit 50, and write runs/head-raw.similar.trec and runs/centroid-raw.similar.trec. Raw against
raw isolates the representation; the API's fused .similar run is reported beside them.

    python3 similar_arms.py --run-dir <run> --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 \\
        --query-prefix "Represent this sentence for searching relevant passages: "
"""
import argparse, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ingest        # noqa: E402
import multivector   # noqa: E402  (trec_lines, require_collection)

DOCUMENT_BUDGET = 50
ARMS = {"head-raw": "body_vector", "centroid-raw": "body_centroid"}


def compose_query(prefix, text):
    return prefix + text


def rank_hits(hits, query_id, limit):
    ranked = []
    for h in hits:
        doc_id = h.get("payload", {}).get("docId")
        if doc_id is None:
            sys.exit(f"query {query_id}: point {h.get('id')} has no docId payload")
        ranked.append((doc_id, h["score"]))
    if len(ranked) < limit:
        sys.exit(f"query {query_id}: {len(ranked)} hits < {limit} -- R@50 would be understated; not scored")
    return ranked


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run-dir", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--embed-url", required=True)
    ap.add_argument("--query-prefix", required=True, help="the model's QUERY instruction, verbatim, or '' for models that need none")
    ap.add_argument("--object-collection", default=ingest.DEFAULT_OBJECT_COLLECTION)
    args = ap.parse_args()

    multivector.require_collection(args.object_collection)
    with open(os.path.join(args.run_dir, "beir", "queries.jsonl"), encoding="utf-8") as f:
        queries = [json.loads(line) for line in f if line.strip()]
    runs_dir = os.path.join(args.run_dir, "runs"); os.makedirs(runs_dir, exist_ok=True)
    lines = {label: [] for label in ARMS}
    try:
        for i, q in enumerate(queries, start=1):
            vec = ingest.embed(compose_query(args.query_prefix, q["text"]), args.model, "", args.embed_url)
            for label, vector_name in ARMS.items():
                status, resp = ingest.qdrant_request("POST", f"/collections/{args.object_collection}/points/search", {
                    "vector": {"name": vector_name, "vector": vec}, "limit": DOCUMENT_BUDGET, "with_payload": ["docId"]})
                if status != 200:
                    sys.exit(f"query {q['_id']} ({label}): HTTP {status} {resp}")
                lines[label].extend(multivector.trec_lines(q["_id"], rank_hits(resp["result"], q["_id"], DOCUMENT_BUDGET), label))
            if i % 50 == 0 or i == len(queries):
                print(f"[similar_arms] {i}/{len(queries)} queries")
    finally:
        for label, rows in lines.items():
            with open(os.path.join(runs_dir, f"{label}.similar.trec"), "w", encoding="utf-8") as f:
                f.write("\n".join(rows) + ("\n" if rows else ""))
    print(f"[similar_arms] wrote {', '.join(f'{l}.similar.trec' for l in ARMS)} in {runs_dir}")


if __name__ == "__main__":
    main()
```
Note `ingest.embed` is called with an empty *document* prefix because the query prefix is already composed into the text.

- [ ] **Step 3: Tests green; smoke on the live baseline** (bge-base, read-only searches; 3 queries):
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_similar_arms.py -q 2>&1 | tail -2
cd Iverson.Server/Iverson.LoadTest/scripts
mkdir -p /tmp/sa-smoke/beir && head -3 /home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/queries.jsonl > /tmp/sa-smoke/beir/queries.jsonl
python3 similar_arms.py --run-dir /tmp/sa-smoke --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 --query-prefix "Represent this sentence for searching relevant passages: "
wc -l /tmp/sa-smoke/runs/*.similar.trec   # 150 each
head -1 /tmp/sa-smoke/runs/centroid-raw.similar.trec   # "<qid> Q0 <docid> 1 0.xxxxxx centroid-raw"
rm -rf /tmp/sa-smoke
```

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/similar_arms.py Iverson.Server/Iverson.LoadTest/scripts/test_similar_arms.py
git commit -m "add similar_arms.py: raw head-vs-centroid .similar runs with the model's query prefix"
```

### Task 5: `sci-2048` arm

**Files:** (outside the repo) `~/repositories/iverson-benchmark-corpora/scifact-2048-<date>/` and `scifact-2048-qdrant-snapshots/`

**Interfaces**
- Consumes: Tasks 1–4 merged to local main and the API image rebuilt; `$SCI = scifact-run-2026-08-26`; `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec`.
- Produces: `runs/sci-2048.*`, `runs/head-raw.similar.trec`, `runs/centroid-raw.similar.trec`, `report-*.txt` (Task 8).

- [ ] **Step 1: Preconditions and the rebuilt API** (multivector plan `:711-727`, plus the rebuild):
```bash
cd /home/ben/repositories/Iverson && git log --oneline -1 && grep -c 'VectorRanking__LambdaSimilar' Iverson.Server/docker-compose.yml   # merged main; 1
cd Iverson.Server
docker compose --dry-run up -d --no-deps qdrant ollama postgres redis authentik-server iverson-api 2>&1 | grep -o "Container iverson-[a-z-]* *Recreate" | sort -u   # whatever prints, do NOT run that up
docker stop iverson-worker iverson-starrocks iverson-kafka iverson-zookeeper iverson-jaeger 2>/dev/null
docker ps --format '{{.Names}}' | grep -q '^iverson-worker$' && echo WORKER-RUNNING-STOP        # must print nothing
for c in iverson-qdrant iverson-postgres iverson-redis iverson-authentik-server iverson-tei-embed; do docker inspect -f '{{.Name}} {{.State.Status}}' $c; done
K=dev-only-not-for-production-qdrant-key-0123456789
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'                # 5183
curl -s http://127.0.0.1:8091/info | grep -o '"model_id":"[^"]*"'   # BAAI/bge-base-en-v1.5
ls /home/ben/repositories/iverson-benchmark-corpora/scifact-bge-base-qdrant-snapshots/*.snapshot | wc -l   # 2 — the restore set for Task 7
docker compose build iverson-api 2>&1 | tail -2
docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker inspect iverson-api | grep -o '"VectorRanking__Lambda[A-Za-z]*=[^"]*"'   # LambdaSimilar=0.70, LambdaChunks=0.70
curl -s 127.0.0.1:8081/build | grep -o '"composite":"[^"]*"'   # record it: every run this plan produces must carry it
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -c "import pyndeval; print('pyndeval ok')"
```
Any other point count, model, or a missing `pyndeval`: stop.

- [ ] **Step 2: Run directory, ingest, counts** (multivector plan `:742-758` with this arm's window):
```bash
export SCI=/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
export D=$(date +%F)
export A=/home/ben/repositories/iverson-benchmark-corpora/scifact-2048-$D
mkdir -p $A/runs && cp -r $SCI/beir $A/ && cp $SCI/qrels.trec $A/
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
date -u +%FT%TZ > $A/ingest-started-at.txt
PYTHONUNBUFFERED=1 nohup python3 ingest.py --corpus $A/beir/corpus.jsonl --key-map-path $A/keymap.json --drop \
  --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 --chunk-max-chars 2048 --chunk-step 1792 \
  > $A/ingest.log 2>&1 &
# poll: tail -2 $A/ingest.log   (≈ 3 h)
```
On completion:
```bash
tail -3 $A/ingest.log; cat $A/keymap.json.stats.json     # documents 5183, chunks 6587, chunk_max_chars 2048, chunk_step 1792
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 6587, 768
```
Exactly 6,587 or the arm is invalid.

- [ ] **Step 3: Snapshot** (multivector plan `:760-770`, `SNAP=…/scifact-2048-qdrant-snapshots`, RESTORE.md text "5,183 object / 6,587 chunk points, 768 dims, 2048/1792 window, model BAAI/bge-base-en-v1.5; key map ../scifact-2048-$D/keymap.json").

- [ ] **Step 4: Schema row, API run, raw arms, reports.**
```bash
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
source /home/ben/iverson-benchmark-data/bench-env.sh
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest
dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK
dotnet run -c Release -- benchmark-query --corpus-path $A --key-map-path $A/keymap.json --output-dir $A/runs --config-label sci-2048 2>&1 | tee $A/runs/sci-2048.log
wc -l $A/runs/sci-2048.chunks.trec $A/runs/sci-2048.similar.trec   # 15000 each
grep -o '"chunkBudgetMultiplier":[[:space:]]*5' $A/runs/sci-2048.meta.json && ls $A/runs/sci-2048.chunks.diversity.json
cd scripts
python3 similar_arms.py --run-dir $A --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 --query-prefix "Represent this sentence for searching relevant passages: " 2>&1 | tee $A/similar-arms.log
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
M1=/home/ben/repositories/iverson-benchmark-corpora/scifact-bge-base-2026-09-04
python3 report.py --qrels $A/qrels.trec --baseline $M1/runs/bge-base.chunks.trec --run $A/runs/sci-2048.chunks.trec 2>&1 | tee $A/report-rule71a.txt
python3 report.py --qrels $A/qrels.trec --baseline $A/runs/head-raw.similar.trec --run $A/runs/centroid-raw.similar.trec 2>&1 | tee $A/report-rule73.txt
python3 report.py --qrels $A/qrels.trec --stats-path $A/keymap.json.stats.json --run $A/runs 2>&1 | tee $A/report-all.txt
```
Expected: 300 queries covered everywhere; `BUILD MISMATCH` against the M1 run is expected and benign (spec §11); the raw runs score `build: unknown`.

No commit: everything lives outside the repo.

### Task 6: `fs-2048` arm

**Files:** (outside the repo) `freshstack-2048-<date>/`, `freshstack-2048-qdrant-snapshots/`

**Interfaces**
- Consumes: Task 5's box state (collections are replaced by this ingest); Task 3's `freshstack_nugget_qrels.py` and pyndeval; the slice at `freshstack-chunk512-2026-08-30/beir/` and its `qrels.trec`.
- Produces: `runs/fs-2048-l{070,050,085,100}.*`, the two raw `.similar` runs, `qrels.nugget.trec`, `report-*.txt`.

- [ ] **Step 1: Run directory and qrels.**
```bash
export D=$(date +%F); export K=dev-only-not-for-production-qdrant-key-0123456789
export SL=/home/ben/repositories/iverson-benchmark-corpora/freshstack-chunk512-2026-08-30
export B=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-$D
mkdir -p $B/runs && cp -r $SL/beir $B/ && cp $SL/qrels.trec $B/
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 freshstack_nugget_qrels.py --slice $B/beir --topics-root /home/ben/repositories/iverson-benchmark-corpora/freshstack-5topic --out $B/qrels.nugget.trec   # 672/672 queries covered
awk '{print $1}' $B/qrels.nugget.trec | sort -u | wc -l   # 672
```

- [ ] **Step 2: Ingest at 2048/1792** — Task 5 step 2's block with `$A → $B`, `--chunk-max-chars 2048 --chunk-step 1792`, `≈ 20 h`; expect `documents 6000, chunks 18622`; then snapshot as Task 5 step 3 into `freshstack-2048-qdrant-snapshots/` (RESTORE.md: "6,000 object / 18,622 chunk points, 2048/1792"). Exactly 18,622 or the arm is invalid.

- [ ] **Step 3: Base run at λ 0.70/0.70, multiplier 11.**
```bash
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
source /home/ben/iverson-benchmark-data/bench-env.sh
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest
dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK
dotnet run -c Release -- benchmark-query --corpus-path $B --key-map-path $B/keymap.json --output-dir $B/runs --config-label fs-2048-l070 --chunk-budget-multiplier 11 2>&1 | tee $B/runs/fs-2048-l070.log
wc -l $B/runs/fs-2048-l070.chunks.trec   # 33600 (672 × 50)
```
A `REFUSING: chunk budget` line means the multiplier flag did not reach the guard — stop.

- [ ] **Step 4: λ sweep** — for each λ in 0.50, 0.85, 1.00 (labels `l050`, `l085`, `l100`):
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
VECTOR_RANKING_LAMBDA_SIMILAR=<λ> VECTOR_RANKING_LAMBDA_CHUNKS=<λ> docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker inspect iverson-api | grep -o '"VectorRanking__Lambda[A-Za-z]*=[^"]*"'   # both = <λ>
cd Iverson.LoadTest && dotnet run -c Release -- benchmark-query --corpus-path $B --key-map-path $B/keymap.json --output-dir $B/runs --config-label fs-2048-l<λλλ> --chunk-budget-multiplier 11 2>&1 | tee $B/runs/fs-2048-l<λλλ>.log
```
Then restore defaults: `cd /home/ben/repositories/Iverson/Iverson.Server && docker compose up -d --no-deps iverson-api` from a shell **without** the two variables; confirm `docker inspect` shows `0.70` for both.

- [ ] **Step 5: Raw arms and the three reports.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 similar_arms.py --run-dir $B --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 --query-prefix "Represent this sentence for searching relevant passages: " 2>&1 | tee $B/similar-arms.log
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs; R=$B/runs
python3 report.py --qrels $B/qrels.trec --nugget-qrels $B/qrels.nugget.trec --baseline $R/fs-2048-l070.similar.trec --run $R/fs-2048-l050.similar.trec --run $R/fs-2048-l085.similar.trec --run $R/fs-2048-l100.similar.trec 2>&1 | tee $B/report-rule72.txt
python3 report.py --qrels $B/qrels.trec --baseline $R/head-raw.similar.trec --run $R/centroid-raw.similar.trec 2>&1 | tee $B/report-rule73.txt
python3 report.py --qrels $B/qrels.trec --nugget-qrels $B/qrels.nugget.trec --stats-path $B/keymap.json.stats.json --run $R 2>&1 | tee $B/report-all.txt
grep -h "distinct parents @10" $B/report-all.txt   # four lines, one per λ .chunks run — rule 7.2's LambdaChunks input
```

No commit.

### Task 7: `fs-512` arm, the window comparison, and the restore

**Files:** (outside the repo) `freshstack-512-<date>/`, `freshstack-512-qdrant-snapshots/`

**Interfaces**
- Consumes: Task 6's run directory (`$B`) for rule 7.1(b); the same slice and nugget qrels (copy `qrels.nugget.trec` from `$B`).
- Produces: `runs/fs-512-l<λ>.*`, raw `.similar` runs, `report-*.txt`; the box restored to the SciFact bge-base baseline.

- [ ] **Step 1–5: As Task 6** with `$B → $C = …/freshstack-512-$D`, `--chunk-max-chars 512 --chunk-step 448` (≈ 25 h; expect `chunks 64735`), snapshot dir `freshstack-512-qdrant-snapshots/`, labels `fs-512-l<λ>`, `cp $B/qrels.nugget.trec $C/`. Then the window comparison for rule 7.1(b):
```bash
python3 report.py --qrels $C/qrels.trec --baseline $C/runs/fs-512-l070.chunks.trec --run $B/runs/fs-2048-l070.chunks.trec 2>&1 | tee $C/report-rule71b.txt
```

- [ ] **Step 6: Restore the SciFact bge-base baseline and leave the box on it** (multivector plan `:840-862` without the multivector deletion):
```bash
K=dev-only-not-for-production-qdrant-key-0123456789
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do curl -s -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; echo; done
cd /home/ben/repositories/iverson-benchmark-corpora/scifact-bge-base-qdrant-snapshots
for f in *.snapshot; do c="${f%%-6802952876034638*}"; curl -s -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; echo; done
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'                # 5183
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
cd /home/ben/repositories/Iverson/Iverson.Server
docker start iverson-zookeeper iverson-kafka iverson-starrocks iverson-jaeger 2>/dev/null
docker compose up -d --no-deps iverson-api iverson-worker      # defaults: bge-base, λ 0.70/0.70
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker inspect iverson-api | grep -o '"VectorRanking__Lambda[A-Za-z]*=[^"]*"'   # both 0.70
source /home/ben/iverson-benchmark-data/bench-env.sh
cd Iverson.LoadTest && dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK
```

No commit.

### Task 8: Gate document and consequences

**Files:**
- Create: `docs/plans/2026-09-GATE-tier1-defaults.md`
- Modify (rule 7.2): `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` defaults + `docker-compose.yml` env defaults + `VectorRankingOptionsTests` default assertions, only where the rule moves a value
- Modify (rule 7.1 FAIL only): the spec §3.4 set listed under File Structure

**Interfaces**
- Consumes: `$A/report-rule71a.txt`, `$C/report-rule71b.txt`, `$B|$C/report-rule72.txt`, `$A|$B|$C/report-rule73.txt`, the four `distinct parents` lines per FreshStack arm, `report-all.txt` files.

- [ ] **Step 1: Write the gate document** in the shape of `docs/plans/2026-09-GATE-multivector.md`: header (date, main HEAD, this plan, the three run directories and snapshot directories); Method (spec §6, the sidecar chunk counts, the per-corpus multiplier, one `report.py` invocation per rule and the family sizes); Arms table (arm, corpus, window, chunks, multiplier, ingest `elapsed_seconds`/`embed_calls`, query run wall-clocks); Results — every compare block quoted verbatim per rule, the α-nDCG and diversity-mean tables per λ per arm; **Verdicts**: rule 7.1 (a) and (b) each PASS/FAIL with the two CI lower bounds → the window default STANDS / CHANGES; rule 7.2 → `LambdaSimilar = <value>` and `LambdaChunks = <value>` with the clause that fired (qualifying λ / none-qualify-1.00 / 1.00-itself-worse-0.70; the diversity delta for chunks); rule 7.3 → follow-up spec RECOMMENDED / CLOSED with the statistics; box state at close.

- [ ] **Step 2: Apply rule 7.2's defaults** (if either moves): change the two defaults in `VectorRankingOptions.cs` and the two compose `:-0.70` fallbacks, update `AddVectorRanking_Defaults_AreSeventyPercentOnBothEndpoints` to the new values (rename accordingly), cite the gate document in the options comment. Run `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` and `Iverson.Api.Tests` — green.

- [ ] **Step 3: Commit the verdict and the λ defaults.**
```bash
git add -f docs/plans/2026-09-GATE-tier1-defaults.md
git add Iverson.Server/Iverson.Vector Iverson.Server/Iverson.Vector.Tests Iverson.Server/docker-compose.yml
git commit -m "record the Tier 1 defaults gate verdict; set LambdaSimilar/LambdaChunks per rule 7.2"
```

- [ ] **Step 4 (only on a rule 7.1 FAIL): the chunk-window default change**, exactly the spec §3.4 set — `128` / `16` at: `IversonChunkAttribute.cs:10`, `annotations.ts:166`, `tags.go:293-294` (+ comments `:168-171`), `tags_test.go:202-206`, `IversonChunk.java:18,21`, `annotations.py:42-43,64-65,154-155`, `SchemaBuilder.cs:161-162`, `SchemaBuilderTests.cs:620-639` expectations, `IngestContractTests.cs:75` (+ its overlap constant); then regenerate the contract and run every affected suite:
```bash
IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract 2>&1 | tail -3
python3 -c "import json; print(json.load(open('Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json'))['chunkWindow'])"   # maxChars 512, step 448
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx 2>&1 | tail -2
(cd Iverson.Clients/Go && go test ./... 2>&1 | tail -3)
(cd Iverson.Clients/TypeScript && npm test 2>&1 | tail -3)          # typecheck + vitest run
(cd Iverson.Clients/Java && mvn -q test 2>&1 | tail -3)             # parent pom: client, sample, conformance
(cd Iverson.Clients/Python && python3 -m pytest tests -q 2>&1 | tail -2)
```
Commit:
```bash
git add Iverson.Clients Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api.Tests Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json
git commit -m "chunk-window default becomes 128/16 tokens (512/448 chars) across the five clients and the server template fallback, per the Tier 1 gate"
```
The gate document's verdict section states that already-registered types keep their window until re-registered.

## Tasks NOT in this plan

Inherited verbatim from the spec's Out-of-scope statement (§2):

Any change to what `SearchSimilar` searches (a centroid win produces a follow-up spec, not a server change here); NFCorpus and ArguAna; the `SearchChunks.top_k` semantics (item 11 of the ranked document); Helm entries for `VectorRanking` (none exist today; an absent section binds to defaults); re-measuring the fusion triple.

## Known issues inherited from spec

- **≈ 48 h of ingest** across the three arms, one at a time, plus ≈ 5 h of query runs. Nothing else on the box while an arm runs.
- **α-nDCG is a diversity measure over subtopics, not a relevance measure**; the λ rule deliberately lets it decide `LambdaSimilar` because relevance-only measures cannot see MMR's benefit. The `.chunks` α-nDCG is reported for completeness but cannot see chunk-level MMR.
- **The API's fused `.similar` run is not the representation comparison.** Fusion adds the centroid at 0.45 to the head vector on that path; the raw head-vs-centroid arms are what §7.3 reads.
- **FreshStack's query-level `qrels.trec`** (as used by the earlier runs) halves R@50 relative to the nugget file's judged pairs; it is the same file the earlier FreshStack numbers used, so deltas are comparable, absolute recall is not.
- **The window rule's SciFact half compares against M1** (512/448) whose API run was made by build `7d3a15092f963723`; `report.py` will print `BUILD MISMATCH`, benign per the migration's same-build control.
- **Already-registered types keep their chunk window** after a default change; there is no migration and the registration guard blocks an in-place window change. The gate document says so.
