# SearchSimilar Centroid Retrieval Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-07-similar-centroid-retrieval-design.md` (commit SHA: `56e0e4e`)

**Goal:** Decide, on the production fused path, whether `SearchSimilar` should retrieve chunked
properties by `<property>_centroid` instead of `<property>_vector`.

**Architecture:** A temporary `VectorRanking:SimilarRetrievalVector` setting selects the retrieval
vector for chunked properties. One binary serves both arms of a six-run campaign over three restored
Qdrant snapshots; a rule mirroring the Tier 1 gate's rule 7.3 decides; the setting is deleted under
either outcome.

**Tech stack:** .NET 10, Qdrant 1.18.2 named vectors, TEI serving `BAAI/bge-base-en-v1.5`,
`Iverson.LoadTest benchmark-query`, `report.py` with `ir_measures`.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **`LambdaSimilar` stays at its default 1.00 for every run** (spec §4). Only
  `SimilarRetrievalVector` varies between arms.
- **Same-build control:** all six runs come from one image. Rebuild the API once, in Task 2, and do
  not rebuild between arms. `report.py` must print no `BUILD MISMATCH` on any gated comparison.
- **Per-corpus chunk-budget multiplier:** 5 on SciFact, 11 on both FreshStack arms. A
  `REFUSING: chunk budget` line in any run log invalidates that run.
- **Scope:** `SearchSimilar` only, and only where `centroidPossible` is true. `SearchChunks` is
  untouched — it has its own centroid fetch at `ObjectSearchGrpcService.cs:438` and its own
  retrieval name at `:402`.
- **`RetrieveVectorsOrDegradeAsync` keeps its signature.** It has three callers (`:262`, `:438`,
  `:456`); only the first changes which name it is passed.
- **Every command block re-establishes its own shell state.** Steps run in fresh shells, so no block
  may rely on a variable (`$A`, `$B`, `$C`, `$K`, `$SNAP`) exported by an earlier one. Each block
  sets what it uses, or uses absolute paths.

## File Structure

**Modify**
- `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` — the temporary setting.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:55-95` — its rejection check.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:221-290` — name selection and the
  λ-gated diversity retrieve.
- `Iverson.Server/docker-compose.yml:443-446` — the env entry.

**Test**
- `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs` — rejection and defaults.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — name selection and the
  diversity-vector case.

**Create**
- `docs/plans/2026-09-GATE-similar-centroid.md` — Task 5.

Campaign artifacts live outside the repo, in the three existing run directories under
`~/repositories/iverson-benchmark-corpora/`.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed across two review rounds.
The spec's `Verified assumptions` table (A1–A23) is authoritative; not re-verified here. The ones
tasks lean on directly:

- **A4** retrieval name is `<prop>.ToSnakeCase() + "_vector"`, passed to one `SearchNamedAsync`.
- **A5** `centroidPossible` (`:228-229`) is exactly "embedded AND chunked".
- **A6** `RetrieveVectorsOrDegradeAsync` takes the vector name as a parameter (`:759-760`).
- **A7** `ResultReranker` treats the second vector generically (`IResultReranker.cs:3-7`).
- **A8** the `rerankIsIdentity` over-fetch keys off `centroidPossible` and `decayField`, neither of
  which the swap changes.
- **A12** `benchmark-query` reaches `SearchSimilar` at `BenchmarkQueryScenario.cs:353`.
- **A14** `report.py --baseline/--run` yields paired stats, Holm and 95 % CIs.
- **A15** snapshot restore repopulates both named vectors and all five payload indexes.
- **A20** existing tests build options with object initializers, so a new defaulted property does
  not disturb them.
- **A22** Qdrant excludes a point lacking the searched named vector — Qdrant product semantics, with
  one-directional risk.
- **A23** `RetrieveVectorsOrDegradeAsync` degrades rather than throws on a missing named vector.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `56e0e4e`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Ordering | `vectorName` (`:221`) is assigned **before** `centroidPossible` (`:228`), so the name block must move below it | `grep -n` — `:221` `var vectorName`, `:228` `var centroidPossible` |
| 2 | Consumer impact | `RetrieveVectorsOrDegradeAsync` has three callers; the plan changes no signature | `:262` (SearchSimilar secondary), `:438` (SearchChunks centroid), `:456` (SearchChunks chunk vectors), definition `:759` |
| 3 | Signature | `DiversifyCandidate(ulong Id, double Score, float[]? DiversityVector)` — third argument is the diversity vector | `IResultDiversifier.cs:8` |
| 4 | Signature | `_ranking` is a `VectorRankingOptions` field in scope in `SearchSimilar` | `ObjectSearchGrpcService.cs:47`; used at `:304` |
| 5 | Signature | `EmptyVectors` is the shared empty default the plan reuses | `:751` definition; used at `:259`, `:453` |
| 6 | File path | `ObjectSearchGrpcServiceTests.cs` is the home for vector-name assertions | 53 `SearchNamedAsync` occurrences there; none in the other Grpc test files |
| 7 | Consumer impact | Nothing existing breaks under the `head` default, because `useCentroidRetrieval` is false there and neither the retrieved name nor the retrieve count moves. Two tests do constrain the diversity vector, and both matter at Task 5's PASS branch (step 3a-bis) | `ObjectSearchGrpcServiceTests.cs:2845-2848` `SearchSimilar_EmbeddingOnlyProperty_ResultsUnchangedFromFusedOrder`; `:2873-2899` `SearchSimilar_Diversification_IssuesNoAdditionalRetrieve`, which asserts `Received(1)` on a chunked schema |
| 8 | Consumer impact | The finiteness guard enumerates five doubles explicitly, so a new string property does not fall into it | `ServiceCollectionExtensions.cs:68-72` |
| 9 | Code validity | `is not ("head" or "centroid")` compiles — `net10.0` | `Iverson.Vector.csproj:4` |
| 10 | Command | Both test projects exist at the cited paths | `Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`, `Iverson.Api.Tests/Iverson.Api.Tests.csproj` |
| 11 | Command | `benchmark-query` accepts `--corpus-path`, `--output-dir`, `--key-map-path`, `--config-label`, `--chunk-budget-multiplier` (default 5) | `Program.cs:416-423` |
| 12 | Command | `report.py` accepts `--qrels` (required), `--baseline` (optional), `--run` (append, required) | `report.py:828-842` |
| 13 | Command | `bench-env.sh` exists and must be sourced before `benchmark-query` | `/home/ben/iverson-benchmark-data/bench-env.sh` |
| 14 | File path | All three arm run directories exist with `keymap.json` and `qrels.trec` | `scifact-2048-2026-09-06` (4 run files), `freshstack-2048-2026-09-07` (10), `freshstack-512-2026-09-07` (10) |
| 15 | File path | The two recorded λ=1.00 control runs exist for the validity check | `fs-2048-l100.similar.trec` and `fs-512-l100.similar.trec`, 33,600 rows each |
| 16 | File path | All three snapshot directories hold two snapshots each | `scifact-2048-`, `freshstack-2048-`, `freshstack-512-qdrant-snapshots/` |
| 17 | File path | The gate document path is free | `docs/plans/2026-09-GATE-similar-centroid.md` does not exist |
| 18 | Sibling sweep | The per-arm command set (restore → clear schema row → recreate API → `benchmark-query` → `report.py`) is identical across all three arms; only multiplier, corpus path, snapshot dir and label differ | Rows 11–16 verified for all three arms, not one |
| 19 | Command | Today's suite sizes, so Task 1 step 9 can detect a lost test | `dotnet test` run at `56e0e4e`: Vector 134/134, Api 878/878 |
| 20 | Span check | The benchmarked property makes `centroidPossible` true — otherwise both arms emit identical runs and every report reads `delta 0.0000`, a campaign that silently measures nothing rather than failing | `BenchmarkDocument.cs:16-18` carries both `[IversonEmbedding]` and `[IversonChunk]` on `Body`; `BenchmarkQueryScenario.cs:344` searches `d => d.Body` |
| 21 | Span check | `--config-label` determines the run and sidecar filenames Tasks 2-5 depend on, and the `tee` is the sole producer of the `.log` | `BenchmarkQueryScenario.cs:289` writes `{ConfigLabel}.similar.trec`, `:222` writes `{ConfigLabel}.meta.json`; the scenario writes no `.log` |
| 22 | Span check | The chunk-budget refusal goes to stderr, so `2>&1` is load-bearing for the Global Constraint's grep | `BenchmarkQueryScenario.cs:125-127` uses `Console.Error.WriteLine` |
| 23 | Span check | One `--run` file plus one `--baseline` yields a Holm family of exactly one per measure | `report.py:153-156` (a file `--run` is not expanded to its directory), `:643-652` (baseline excluded by path), `:678-681` (Holm applied across runs within each measure) |

---

## Tasks

### Task 1: The setting, the swap, and its tests

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:55-95`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:221-290`
- Modify: `Iverson.Server/docker-compose.yml:443-446`
- Test: `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces**
- Produces: the `SimilarRetrievalVector` setting and the API image every later task runs against.

- [ ] **Step 1: Add the setting.** In `VectorRankingOptions.cs`, after `LambdaChunks`:
```csharp
    // Temporary, for the gate in docs/specs/2026-09-07-similar-centroid-retrieval-design.md §5.
    // "head" | "centroid" — which named vector SearchSimilar RETRIEVES by for a chunked property.
    // Deleted once the gate rules; the winner is hard-coded.
    public string SimilarRetrievalVector { get; set; } = "head";
```

- [ ] **Step 2: Reject any other value.** In `AddVectorRanking`, after the `LambdaChunks` range
check and before `services.AddSingleton(Options.Create(opts))`:
```csharp
        if (opts.SimilarRetrievalVector is not ("head" or "centroid"))
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:SimilarRetrievalVector must be \"head\" or " +
                $"\"centroid\" (was \"{opts.SimilarRetrievalVector}\").");
```
The finiteness guard above enumerates the five doubles by name and does not need changing.

- [ ] **Step 3: Select the names in `SearchSimilar`.** Delete the `vectorName` assignment at `:221`.
After `centroidPossible` (`:228-229`), insert:
```csharp
        var snake        = vectorDesc.PropertyName.ToSnakeCase();
        var headName     = snake + "_vector";
        var centroidName = snake + "_centroid";

        // Retrieval by centroid applies only where a centroid exists at all.
        var useCentroidRetrieval = centroidPossible
            && string.Equals(_ranking.SimilarRetrievalVector, "centroid", StringComparison.Ordinal);

        var vectorName    = useCentroidRetrieval ? centroidName : headName;
        var secondaryName = useCentroidRetrieval ? headName     : centroidName;
```
`topK` and `collectionName` (`:222-223`) stay where they are. Leave `rerankIsIdentity` and
`fetchLimit` (`:238-239`) untouched — they key off `centroidPossible`, which the swap does not change.

- [ ] **Step 4: Use `secondaryName` for the fusion fetch.** At the `RetrieveVectorsOrDegradeAsync`
call (`:262-267`), replace the inline `vectorDesc.PropertyName.ToSnakeCase() + "_centroid"` argument
with `secondaryName`. Signature and the other two callers are untouched.

- [ ] **Step 5: λ-gate the diversity vector.** After the `centroids` block and before
`var candidates = …`:
```csharp
        // The diversity vector stays the centroid where it is observable. Under centroid retrieval
        // the fetched secondary is the head vector, so holding it costs a second retrieve — issued
        // only at λ < 1, because at λ = 1.00 both Mmr branches reduce to lambda * Score and
        // selection is bit-identical to Take(topK) whatever this holds.
        var diversityVectors = centroids;
        if (useCentroidRetrieval && _ranking.LambdaSimilar < 1.0 && results.Count > 0)
            diversityVectors = await RetrieveVectorsOrDegradeAsync(
                collectionName,
                results.Select(r => r.Id).ToList(),
                centroidName,
                "SearchSimilar",
                "diversifying without the centroid signal");
```
Then in the `diversityCandidates` projection (`:284-289`), read `diversityVectors` instead of
`centroids`. The `candidates` projection at `:272-276` keeps reading `centroids`.

- [ ] **Step 6: Compose entry.** In `docker-compose.yml`, beside the two λ entries:
```yaml
      - VectorRanking__SimilarRetrievalVector=${VECTOR_RANKING_SIMILAR_RETRIEVAL:-head}
```

- [ ] **Step 7: Options tests.** In `VectorRankingOptionsTests.cs`, following the rejection pattern
at `:71-137`: a test that an unrecognised `SimilarRetrievalVector` throws, and an assertion in the
defaults test that it is `"head"`.

- [ ] **Step 8: Selection tests.** In `ObjectSearchGrpcServiceTests.cs`, four cases:
  1. chunked + `centroid` → `SearchNamedAsync` receives `body_centroid`; the fusion fetch receives
     `body_vector`.
  2. chunked + `head` → `body_vector` retrieved, `body_centroid` fetched (today's behaviour).
  3. not chunked → `body_vector` retrieved regardless of the setting.
  4. Diversity vector, asserted on **what reaches `DiversifyCandidate.DiversityVector`**. A
     name-level assertion cannot pin this site: under `head` one retrieve serves both consumers, so
     it passes whether the implementation feeds it the centroid or `null`. Two branches, and they
     need different instruments:
     - **λ = 0.70 under `centroid`** — the diversifier receives `body_centroid`. Distinct vector
       payloads per name work here, because at λ < 1 the two vectors genuinely reorder the output.
     - **λ = 1.00 under `centroid`** — the diversifier receives `body_vector` and no second retrieve
       is issued. A **capturing `IResultDiversifier` fake is required**: at λ = 1.00 `Select` emits
       `ranked[index].Score`, the fused score rather than the MMR score, so output is byte-identical
       whether the diversifier is fed `body_vector`, `body_centroid` or `null`, and any output-level
       assertion passes whatever the implementation does. `Substitute.For<IResultDiversifier>()`
       auto-substitutes the return type, so `:304`'s `foreach` iterates an empty list. Pair it with
       `DidNotReceive().RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(),
       "body_centroid")` for the no-second-retrieve half.

- [ ] **Step 9: Run the suites.**
```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -3
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
```
Both green, with no test count lower than today's 134 and 878.

- [ ] **Step 10: Commit.**
```bash
git add Iverson.Server/Iverson.Vector Iverson.Server/Iverson.Vector.Tests \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/docker-compose.yml
git commit -m "add the temporary SimilarRetrievalVector setting and its SearchSimilar wiring"
```

### Task 2: `sci-2048` arm

The cheapest arm, run first so the campaign mechanics are proven before two 65-minute arms.

**Files:** none in the repo. Artifacts in `~/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06/`.

**Interfaces**
- Consumes: Task 1 merged and the API image rebuilt from it.
- Produces: `runs/sc-head.similar.trec`, `runs/sc-centroid.similar.trec`, `report-sci.txt`; the
  single API image every later arm reuses.

- [ ] **Step 1: Build the image once, for the whole campaign.**
```bash
cd /home/ben/repositories/Iverson/Iverson.Server
docker compose build iverson-api 2>&1 | tail -2
docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
curl -s 127.0.0.1:8081/build | grep -o '"composite":"[^"]*"'     # record; must be identical in every run's meta.json
docker inspect iverson-api | grep -o '"VectorRanking__[A-Za-z]*=[^"]*"'   # LambdaSimilar 1.00, LambdaChunks 0.70, SimilarRetrievalVector head
```

- [ ] **Step 2: Restore the arm.**
```bash
K=dev-only-not-for-production-qdrant-key-0123456789
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do
  curl -s -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; echo
done
cd /home/ben/repositories/iverson-benchmark-corpora/scifact-2048-qdrant-snapshots
for f in *.snapshot; do
  c="${f%%-6802952876034638*}"
  curl -s -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; echo
done
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*'   # 6587
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'   # 5183
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
```
Any other point count: stop.

- [ ] **Step 3: The two runs.**
```bash
A=/home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06
cd /home/ben/repositories/Iverson/Iverson.Server
for V in head centroid; do
  VECTOR_RANKING_SIMILAR_RETRIEVAL=$V docker compose up -d --no-deps iverson-api
  until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
  docker inspect iverson-api | grep -o '"VectorRanking__SimilarRetrievalVector=[^"]*"'   # must equal $V
  source /home/ben/iverson-benchmark-data/bench-env.sh
  (cd Iverson.LoadTest && dotnet run -c Release -- benchmark-query \
     --corpus-path $A --key-map-path $A/keymap.json --output-dir $A/runs \
     --config-label sc-$V --chunk-budget-multiplier 5 2>&1 | tee $A/runs/sc-$V.log)
done
wc -l $A/runs/sc-head.similar.trec $A/runs/sc-centroid.similar.trec   # 15000 each
grep -c "REFUSING: chunk budget" $A/runs/sc-head.log $A/runs/sc-centroid.log   # 0 each
grep -o '"composite": "[^"]*"' $A/runs/sc-head.meta.json $A/runs/sc-centroid.meta.json   # identical
```
Restore the default afterwards: `docker compose up -d --no-deps iverson-api` from a shell **without**
`VECTOR_RANKING_SIMILAR_RETRIEVAL` set.

- [ ] **Step 4: Report.**
```bash
A=/home/ben/repositories/iverson-benchmark-corpora/scifact-2048-2026-09-06
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 report.py --qrels $A/qrels.trec --baseline $A/runs/sc-head.similar.trec \
  --run $A/runs/sc-centroid.similar.trec 2>&1 | tee $A/report-sci.txt
```
Expect 300/300 queries covered and **no** `BUILD MISMATCH`. Record both CI lower bounds — these are
the rule's SciFact half.

No commit: everything lives outside the repo.

### Task 3: `fs-2048` arm

**Files:** none in the repo. Artifacts in `.../freshstack-2048-2026-09-07/`.

**Interfaces**
- Consumes: Task 2's built image, unchanged.
- Produces: `runs/fs2048-head.similar.trec`, `runs/fs2048-centroid.similar.trec`, `report-fs2048.txt`.

- [ ] **Step 1: Restore and run**, exactly Task 2 steps 2–3 with `SNAP=freshstack-2048-qdrant-snapshots`,
`B=.../freshstack-2048-2026-09-07`, expected counts **18,622 chunks / 6,000 objects**, labels
`fs2048-head` / `fs2048-centroid`, `--chunk-budget-multiplier 11`, and 33,600 rows per run.
**Do not rebuild the image.**

- [ ] **Step 2: Validity check** (record, do not gate):
```bash
B=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
diff <(sort $B/runs/fs2048-head.similar.trec) <(sort $B/runs/fs-2048-l100.similar.trec) >/dev/null \
  && echo "IDENTICAL — setting inert on the control path" \
  || echo "DIFFERS — non-diagnostic (meta.json records no λ; cannot separate a leaky setting from a λ difference)"
```

- [ ] **Step 3: Report**, as Task 2 step 4 with `$B` and `report-fs2048.txt`. Expect 672/672 covered.
Record the nDCG@10 and R@50 deltas and their Holm p_adj — the rule's first FreshStack half.

No commit.

### Task 4: `fs-512` arm

**Files:** none in the repo. Artifacts in `.../freshstack-512-2026-09-07/`.

**Interfaces**
- Consumes: Task 2's built image, unchanged.
- Produces: `runs/fs512-head.similar.trec`, `runs/fs512-centroid.similar.trec`, `report-fs512.txt`.

- [ ] **Step 1: Restore and run**, as Task 3 step 1 with `SNAP=freshstack-512-qdrant-snapshots`,
`C=.../freshstack-512-2026-09-07`, expected counts **64,735 chunks / 6,000 objects**, labels
`fs512-head` / `fs512-centroid`, multiplier 11. **Do not rebuild the image.**

- [ ] **Step 2: Validity check** against `$C/runs/fs-512-l100.similar.trec`, as Task 3 step 2.

- [ ] **Step 3: Report**, as Task 2 step 4 with `$C` and `report-fs512.txt`.

No commit.

### Task 5: Gate document and consequences

**Files:**
- Create: `docs/plans/2026-09-GATE-similar-centroid.md`
- Modify (both outcomes): `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`,
  `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`, `Iverson.Server/docker-compose.yml`,
  `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`,
  `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`
- Modify (PASS only): `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`,
  `Iverson.Server/Iverson.Vector/IResultReranker.cs`, `Iverson.Server/Iverson.Vector/ResultReranker.cs`,
  `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces**
- Consumes: `report-sci.txt`, `report-fs2048.txt`, `report-fs512.txt` and the two validity-check outcomes.

- [ ] **Step 1: Evaluate the rule.** PASS requires centroid beating head on **both** nDCG@10 and
R@50 with Holm p_adj < 0.05 on **both** FreshStack arms, **and** SciFact's 95 % CI lower bounds on
both measures above −0.02. Anything else is a FAIL.

- [ ] **Step 2: Write the gate document** in the shape of `docs/plans/2026-09-GATE-tier1-defaults.md`:
header (date, main HEAD, this plan, the three run directories, the one build composite); Method (the
six runs, one image, per-arm multipliers, one `report.py` invocation per arm with family sizes); Arms
table; Results with every compare block quoted verbatim; the two validity-check outcomes; **Verdict**
PASS or FAIL against each clause of the rule; box state at close.

- [ ] **Step 3a (PASS only): ship the change.** Hard-code centroid retrieval where `centroidPossible`
is true — `useCentroidRetrieval` becomes `centroidPossible`. Keep the λ-gated second retrieve, which
ships as part of the change. Rename `RerankCandidate.Centroid` to reflect that it holds the secondary
vector, updating `ResultReranker` and its tests. Then delete the setting as in step 3b.

- [ ] **Step 3a-bis (PASS only): repair the four existing tests the hard-code breaks.** Hard-coding
centroid retrieval changes the retrieved name for every chunked property, so the four
`DualAnnotatedSchema` stubs in `ObjectSearchGrpcServiceTests.cs` stop matching and NSubstitute returns
an empty auto-substitute — every count and score assertion then fails silently rather than loudly.
Re-point `_vector.SearchNamedAsync("docs_test-tenant", "body_vector", …)` to `"body_centroid"` at
`:2530`, `:2674`, `:2821` and `:2887`. `SearchSimilar_Diversification_IssuesNoAdditionalRetrieve`
(`:2873-2899`) fails a second way — the shared `_sut` runs at `LambdaSimilar = 0.70` (`:79`), which
opens the λ-gated second retrieve and turns its `Received(1)` into 2 — so give it a per-test SUT at
`LambdaSimilar = 1.00`, where "issues no additional retrieve" remains true.
`ObjectSearchVectorIntegrationTests.cs` is unaffected: its `SearchSimilar` tests use `Article`/`Title`,
which is embedding-only.

- [ ] **Step 3b (both outcomes): delete the setting.** Remove `SimilarRetrievalVector`, its rejection
check, its compose entry, its two options tests in `VectorRankingOptionsTests.cs`, **and Task 1
step 8's four tests in `ObjectSearchGrpcServiceTests.cs`**, whose cases 1, 2 and 4 set the property on
a per-test `VectorRankingOptions` — leaving them in place turns the deletion into a C# compile error,
so step 4 could not build under either outcome. Disposition: on **FAIL** delete all four, since they
assert a setting and a code path that no longer exist, and also revert Task 1 step 3–5's wiring so
`SearchSimilar` retrieves by `headName` unconditionally. On **PASS** drop cases 2 and 3's
setting-dependence and rewrite cases 1 and 4 against the now-unconditional centroid retrieval and the
λ-gated diversity vector.

- [ ] **Step 4: Suites and commit.**
```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -3
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
git add -f docs/plans/2026-09-GATE-similar-centroid.md
git add Iverson.Server/Iverson.Vector Iverson.Server/Iverson.Vector.Tests \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests Iverson.Server/docker-compose.yml
git commit -m "record the SearchSimilar centroid-retrieval gate verdict; <ship|revert> per the rule"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's §2 "Out of scope":

- `SearchChunks`. It retrieves from the chunks collection (`:402`, `:417`) and separately fuses the
  **object** collection's `<property>_centroid` (`:441`, per the comment at `:429`). Its retrieval
  vector does not move. Naming all four derivation sites here is deliberate: two of them mention a
  centroid and only one of those is in scope.
- The fusion weights WBase / WCentroid / WDecay.
- The two-query "union head and centroid candidate sets" shape. It would capture recall from both
  vectors but adds a Qdrant round trip per search, and its benefit over centroid-alone is entirely
  unmeasured. Not proposed here.
- Re-ingest. The three arms restore from existing snapshots.

Also deferred by spec §6: fetching both names in one retrieve. It would remove the λ-gated extra
round trip and is the better end state, but it widens a primitive shared with `SearchChunks` and is
deliberately not done for an arm that may be reverted.

## Known issues inherited from spec

- **SciFact is partly a null test.** Single-chunk documents have a centroid equal to their
  normalized head vector, so the swap cannot change their ranking, and 73.7 % of the `sci-2048`
  corpus is single-chunk. 1,363 of 5,183 documents can move. Ben chose (2026-09-07) to keep the arm:
  it is the only near-single-chunk guard available, its multi-chunk minority is substantial, and it
  costs about eight minutes across both settings.
- **`SearchSimilar` under `centroid` cannot return a point that has a head vector but no centroid** —
  it is excluded from retrieval, not degraded. Ben accepted this (2026-09-07); see §3's retrieval
  asymmetry for the writer paths that produce the state and the rejected alternatives. The benchmark
  corpora contain no such points, so the gate cannot measure the exposure.
- **The validity check cannot diagnose a mismatch**, only confirm a match — see A13 and §4.
