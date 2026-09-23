# Critical Implementation Review: 2026-09-08-similar-via-chunks-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-08-similar-via-chunks-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA f5105a9); cited file:line references re-checked under §1. (`8bf70bd` adds the plan, `f27c5fb` adds review round 1, `247b351` applies round 1's eight fixes to the plan; no source, test, compose or script file changed — `git log --stat f5105a9..HEAD` touches only the two docs.)

Reviewed against `main` `247b351`. Prior review files for this plan basename: round 1 (`…-implementation-plan-critical-review-1.md`). Verification aids used this round: the Qdrant.Client 1.18.1 reflection probe re-run; round 1's scratch copies of `Iverson.Vector` (Tasks 1–2 applied) and `Iverson.Api` (Task 3's declared signatures plus Task 4) with the Task 4 block updated to the plan's current text — nullable `ChunkDescriptor? chunkDesc`, the early `NotRouted("property is not chunked")` return, and the two-clause branch point — rebuilt: 0 errors, the single pre-existing `VectorOutput.Data` obsolete warning; `docker compose ls` and `docker inspect iverson-api` on the box (read-only); reads of the campaign directory (read-only). Nothing was added to the repository. The §0 enumeration below was rebuilt from the plan before round 1's diff was re-read; the round-1 fix sites are ordinary rows in it.

## 0. Coverage enumeration

**Task 1 — vector-service reads**

- T1 Step 0 prose (floors from `main`): ok — no numeric claim (P14); both csproj paths exist; the Vector run carries `TESTCONTAINERS_RYUK_DISABLED=false`, matching the shell env and `~/.testcontainers.properties` (`ryuk.disabled=false`).
- T1 Step 1 code (interface): ok — two members appended after `RetrieveNamedVectorAsync` (`IVectorRoles.cs:17-20`); the nested `IReadOnlyDictionary` return type compiles in the scratch copy; the only production implementer is `IntelligenceVectorService` (`:9`), every other reference is `Substitute.For<IVectorQueryService>()` (P5), so no other class breaks.
- T1 Step 2 code (implementations): ok — `client.GetCollectionInfoAsync(string)` and `CollectionInfo.PointsCount : UInt64` (probe re-run); `RetrieveAsync(String, IReadOnlyList<PointId>, Boolean withPayload, Boolean withVectors, ReadConsistency, ShardKeySelector, CancellationToken)` is the overload the named arguments select (probe); `(PointId)id` / `ids.Chunk(512)` copy `:157-161`; `ToCanonicalString(Value)` is `internal static` at `:202`; `Telemetry.Source` / `ActivityStatusCode.Ok` at `:17`, `:30`; compiled.
- T1 Step 3 prose (integration scenario): ok — `_svc` / `_mgr` are the fixture's real service and manager (`QdrantIntegrationTests.cs:44-48`); `_mgr.EnsureCollectionAsync(name, dims)` + `_svc.UpsertAsync(collection, id, vector, Dictionary<string, object>)` is the `:252-262` precedent; an `int` payload value is written as `IntegerValue` (`ToQdrantValue`, `:217`) and reads back as its decimal string (`ToCanonicalString`, `:205`); an id never written is simply absent from `RetrieveAsync`'s result (the `:165-169` loop relies on the same).
- T1 Step 4 command + floor (+2): ok — the File Structure names "the two new reads" and Step 3 states one assertion set per read, so two tests is the plain reading; round 1 already dispositioned the one-vs-two ambiguity and it is not re-raised.
- T1 Step 5 command: ok — three `git add` paths exist; subject/trailer match `git log -6` (P21).
- T1 dynamic: ok — `UpsertAsync` waits by default (every existing integration test searches immediately after upsert); `GetCollectionInfoAsync` on a missing collection surfaces as `RpcException`, which Task 4 catches; no shared state between the two new methods.

**Task 2 — the option**

- T2 Step 1 code (property): ok — `List<string>` initialised `[]`; compiled; the only rejected section key is `Lambda` (`ServiceCollectionExtensions.cs:58`, A15).
- T2 Step 2 code (blank check) + placement: ok — the `LambdaChunks` range check ends at `:90` and `Options.Create(opts)` is `:92`, so "after the range check, before the singleton" is a single insertion point; `?.Trim()` tolerates a null element; the trimmed value is written back; compiled.
- T2 rule — blank-entry check, both directions: ok — over-inclusion: `"  BenchmarkDocument "` normalises to `BenchmarkDocument`; under-inclusion: `""`, whitespace-only and null all throw with the index in the message; an interior-space entry passes but no CLR type name contains one.
- T2 Step 3 prose (compose): ok — the two λ entries are `docker-compose.yml:445-446` under `iverson-api`; the value-less form is A23; no `Iverson.Server/.env` exists to interpose a value.
- T2 Step 4 tests prose: ok — `BuildConfig` prefixes `VectorRanking:` (`VectorRankingOptionsTests.cs:11-15`) so `("SimilarViaChunksTypes:0", …)` binds to index 0; the throw pattern is `:71-82`; the read-back pattern is `:166-172`; test 2 rests on A22.
- T2 Step 5 floor (+6 = Task 1's 2 + 4): ok — four tests listed.
- T2 Step 6 command: ok — four paths exist.

**Task 3 — extractions**

- T3 Step 1 prose + code (`BuildChunksFilter` reshape, `TryBuildChunksFilter`): ok — `BuildChunksFilter` reads `request` only at `:873` and `:878`, so `clauses.Count` / `foreach (var clause in clauses)` is the whole change; one caller at `:361` (P23); `RepeatedField<SearchClause>` → `IReadOnlyList<SearchClause>` (A24); `FilterTranslationException` is thrown by `MatchEquality` and already caught at `:363` (P11); `RpcException`/`StatusCode` in scope; compiled. Unreferenced until Task 4 is not a build failure (P30).
- T3 Step 2 prose (the moved range, labels not numbers): ok — round 1 already recorded the 2–7-line offset in the cited sub-ranges; the labels ("scoped key … NotFound → results = []", "distinct parent ids, centroid retrieve with degrade", "decayField, now, candidates") each identify exactly one block (`:412-426`, `:428-443`, `:464-475`); the excluded chunk-vector retrieve is `:453-462`. Not re-raised.
- T3 Step 2 code (`ChunkPipeline`, `SearchChunksFusedAsync`): ok — instance method, so `logger`, `tenantScope`, `vector`, `reranker`, `_decayOptions` resolve; `decision.TenantValue` exists (`IRowFieldAuthorizationEvaluator.cs:59`); `schema.CollectionName!` is `string?` at the helper's signature (the null check is in the caller's preamble, `:350-352`); compiled with a stub body.
- T3 Step 2 prose (`SearchChunks` reassembly): ok — the chunk-vector retrieve reads only `results`, `chunksCollection`, `vectorName`, `topK`, all exposed by the record; moving `Rerank` ahead of that retrieve changes no call on `IVectorQueryService` (the reranker is not a substitute) and no output, so `Received`/`CapturedLimit`-style assertions and the streamed rows are unchanged.
- T3 Step 3 code (`WriteSimilarResponsesAsync`): ok — the moved body depends only on `schema`, `decision`, payload, score, trace id (A11); `ConvertPayloadValue`, `StructSerializer.UpperFirst`, `AuthorizationFieldMasking.MaskDisallowedFields` are all static, so a static method is valid; compiled with the moved body; `Diversify` now runs lazily when the writer enumerates `rows`, after `columnLookup` is built, exactly as today's `foreach` ordering.
- T3 Step 4 prose (no new test): ok — `SearchChunks_OverFetchesFourTimesTopK_AndTrimsToTopK` (`:2905-2926`) asserts `CapturedLimit(_vector) == 12` for `TopK = 3` via `GetArguments()[3]` (`:2511-2514`); `:1443-1467` asserts one streamed row. Both are unmodified by the extraction.
- T3 Step 5 command + floor (≥ Step 0's floor): ok — consistent with "no new test".
- T3 Step 6 command: ok — one path.

**Task 4 — the routed branch**

- T4 Step 1 code (branch point): ok — `chunkDesc` computed once with the `:228-229` predicate; `List<string>.Contains(string, IEqualityComparer<string>)` binds to the LINQ overload (compiled); `centroidPossible = chunkDesc is not null` preserves the head path's gate; the unlisted case never enters the routed method and logs nothing, per spec §3.2 ("each failed condition on a *listed* type").
- T4 Step 2 prose (condition order): ok — chunked → logic → clauses → counts → zero checks matches spec §3.2's "counts are read only when 1–3 hold".
- T4 Step 2 code (`TrySearchSimilarViaChunksAsync`, current text): ok — compiled with the nullable parameter; after `if (chunkDesc is null) return …` flow analysis passes `chunkDesc` to the helper's non-nullable parameter without a warning; `using (…) x = await …;` with a returning `catch` leaves both counts definitely assigned; `NotFound` catch precedes the general catch; `selected.Select(s => s.Id).ToList()` is `List<ulong>` in collapsed order; the `rows` tuple names match the writer's parameter; `IResultDiversifier.Diversify(IReadOnlyList<DiversifyCandidate>, int, double)` returns `IReadOnlyList<RerankedResult>` (`IResultDiversifier.cs:14`), so `selected.Count`, `.Id`, `.FusedScore` all exist.
- T4 Step 2 dynamic — count reads: ok — each read under a fresh 30-second key scoped to its own collection (`IntelligenceTenantScope.cs:25-28`); A20 covers point-count reads under a collection-scoped key; a missing chunks collection → `RpcException` → "counts unavailable" → head path before any chunk search.
- T4 Step 2 dynamic — search / hydration errors: ok — a non-`NotFound` `RpcException` from `SearchNamedAsync` propagates as `SearchChunks` propagates it today; hydration `NotFound` → every selected parent logged missing, empty stream (spec §3.6 row 7); other hydration `RpcException` → `Unavailable` (row 6).
- T4 Step 2 dynamic — filter parity: ok — the routed method's `filter` is its own local (`out var filter`), so the preamble's object `filter` is intact for the head path on every fallback; ownership is applied with the same four arguments as `:368-372`; the preamble's `ValidateFilterProperty` / allowed-field checks have already run on both paths.
- T4 rule — collapse identity key `KeyToUlong(parent_id)`, both directions: ok — over-inclusion: the same parent's later chunks are dropped after its first sighting in fused-descending order (`ResultReranker.cs:58-59`), the intended max-passage rule; under-inclusion: a chunk with no/blank `parent_id` is skipped (spec §3.5.1); the centroid dictionary is keyed by the same function (`:434`, `:471`).
- T4 rule — fused score equals raw score when no signal is present (tests 1 and 3 assert raw scores survive): ok — `ResultReranker.cs:25-33` short-circuits to `BaseScore` when centroid and decay are both absent; `DualAnnotatedSchema` has no timestamp column (`:2472`), so `decayField` is null, and the tests stub an empty centroid dictionary.
- T4 rule — list membership `OrdinalIgnoreCase` on `schema.TypeName`: ok — the registry's comparison (A3); Task 5's value equals `LoadTest/Program.cs:161`'s `"BenchmarkDocument"`.
- T4 rule — chunk-expressibility, one row per producer of "not routed": ok — producers in the code: un-chunked property (`chunkDesc is null`), OR over ≥ 2 clauses, `BuildChunksFilter`'s three `InvalidArgument` throws (`:880-885` non-EQUALS/MUST_NOT, `:898-900` unauthorised metadata column, `:905-911` scalar/unknown column), `MatchEquality`'s `FilterTranslationException`, count `RpcException`, object 0, chunk 0. Step 3.5 tests seven of the nine; the unauthorised-metadata and timestamp-translation producers are caught by the same two `catch` arms as the tested non-EQUALS/scalar cases and cannot reach a different outcome: dropped — no distinct behaviour to break.
- T4 rule — budget arithmetic: ok — 13/10 → 2 → 80; 108/10 → 11 → 440; `objectCount > 0` is established before the division.
- T4 Step 3 tests prose (fixture and stubs): ok — `DualAnnotatedSchema()` (`:2466-2480`) is type `Doc`, `Body` in both lists, tenant column `TenantId`, so the collections are `docs_test-tenant` / `docs_chunks_test-tenant` under the default principal's `test-tenant`; the per-test constructor is `:2613-2620`; an unstubbed `GetPointCountAsync` on the shared `_sut` is never reached (empty list). The tests that need a variant — 5's embedded-only (`with { ChunkFields = [] }`), 6's owner field, 7's field permissions — build one with `with` on the sealed record (`SchemaDescriptor.cs:3`; precedent `:1117-1121`, `:1754`); the plan names the fixture as the base, not the only shape. Test 5's `LogInformation` assertion needs `Substitute.For<ILogger<…>>()` in the per-test constructor, as the plan says.
- T4 Step 3 test 6 (ownership) prose: ok — the pattern at `:1913-1935` (`SearchChunks_OwnershipRequired_MergesMatchKeywordConditionWithKeyFilter`) captures `GetArguments()[4]` and asserts `ownerId` / `test-user`; on the routed path the only `SearchNamedAsync` call is the chunks one, so `ContainSingle` holds.
- T4 Step 4 floor (+15): → §2.1.
- T4 Step 5 command: ok — two paths.

**Task 5 — reproduction check**

- T5 Step 1 commands: ok — two `.snapshot` files with the `-6802952876034638` infix (P20); `RESTORE.md` points at the scifact loop the block reproduces; 6,000 / 18,622 match `keymap.json.stats.json`; `_iverson_schema.type_name` is the PK; the DELETE is the earlier plans' precedent.
- T5 Step 1 → Step 3 fixture handoff (schema row deleted, then needed) — persistence boundary: ok — `benchmark-query` is in `needsTenantAndSchema` and `RegisterAllAsync` runs before the scenario (`LoadTest/Program.cs:88-89`, `:151-163`; P28).
- T5 Step 2 commands: ok — no `name:` in `docker-compose.yml`, so the project name derives from the `Iverson.Server` basename: `docker compose ls` shows one project `iversonserver`, currently labelled with a working dir under a since-removed worktree, so a fresh branch checkout joins the same project and network; `docker compose build` uses `context: ..`; `VECTOR_RANKING_LAMBDA_CHUNKS` is the `:446` shell name; the pass-through entry is Task 2 Step 3 (A23); `/build` returns `composite` (`Iverson.Api/Program.cs:304-308`); the `docker inspect` grep matches `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument`; `LambdaSimilar=1.00` is the compose default and nothing in Step 2's shell sets `VECTOR_RANKING_LAMBDA_SIMILAR`.
- T5 Step 3 commands: ok — the five flags exist (`LoadTest/Program.cs:416-423`); `bench-env.sh` exports only the gRPC URL and client-credential variables, so it cannot alter the container's ranking or model configuration; `ChunkBudgetGuard` at multiplier 4: `topK = 200`, `reachable = 200 / 3.104 = 64.4 ≥ 50` → `Ok` (`ChunkBudgetGuard.cs:31-36`; P29); the harness's `SearchSimilar` is `Query.Similar<BenchmarkDocument>(d => d.Body).TopK(50)` with no filter (`BenchmarkQueryScenario.cs:344-347`) and `SearchChunks` is `TopK(200)` (`:393`), so the routed limit is 50 × ⌈3.104⌉ × 4 = 800 and the chunks raw fetch 200 × 4 = 800, as the step says; the bypass identity carries no ownership condition, so both RPCs search under the same filter; the "not routed" grep is meaningful at `Information` (P25; container logs restart with Step 2's recreate) and no request in the run hits a fallback.
- T5 Step 3 dynamic — hydration on ingest.py-written points: ok — `ingest.py:230` computes `KeyToUlong` byte-for-byte, `:588` writes the object point at that id, `:648` writes `parent_id = key` (P27); the payload the routed path hydrates is the same object point the head path's `SearchNamedAsync` already returns (`DocId` read at `BenchmarkQueryScenario.cs:363`), so the harness's `DocId` lookup is unchanged.
- T5 Step 3 `# 33600 each`: dropped — round 1 already dispositioned this as an expectation, not a gate; Step 4's structural block is the gate.
- T5 Step 4 commands: ok — `--qrels` / `--baseline` / `--run` exist; the baseline run is materialised with `list(...)` at `report.py:660` before the per-measure loop (the old consumed-generator defect is gone), so the nDCG@10 and R@50 deltas the rule reads are both computed against the full baseline; the structural block prints `covered by this run N / M` (`:313`) and `duplicate doc ids` (`:314-321`); `BUILD MISMATCH` at `:593`; `python-libs` present.
- T5 Step 5 commands: ok — Step 2 set both variables inline, so a fresh shell has neither; compose recreates on the config change.
- T5 Step 6 prose + command: ok — `### 3.` heading at `docs/…:94`; commit convention (P21).

**Cross-task interface contracts**

- Task 1 → Task 4, `GetPointCountAsync(string)` — two call sites (object collection, chunks collection): ok — same signature; each sourced from `ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks)`.
- Task 1 → Task 4, `RetrievePayloadAsync(string, IReadOnlyList<ulong>)` — one call site: ok — `selected.Select(s => s.Id).ToList()`.
- Task 2 → Task 4, `_ranking.SimilarViaChunksTypes`: ok — `List<string>` on the options the handler holds at `:47`.
- Task 2 → Task 5, `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument` (env → binder → list): ok — index-0 env binding is A22's binder run; the value equals `schema.TypeName`.
- Task 3 → Task 4, `TryBuildChunksFilter(schema, IReadOnlyList<SearchClause>, IReadOnlySet<string>?, out Filter?)`: ok — Task 4 passes `request.Filter` (A24), `decision.AllowedFields`.
- Task 3 → Task 4, `SearchChunksFusedAsync(schema, ChunkDescriptor, decision, float[], Filter?, ulong)` → `ChunkPipeline.{Results, Fused, Centroids}`: ok — all three consumed; `Centroids` keyed by parent ulong, which the collapse looks up.
- Task 3 → `SearchChunks` (second call site, `topK * OverFetchFactor`): ok — 12 for `TopK = 3` as `:2924` asserts.
- Task 3 → Task 4 and → head path, `WriteSimilarResponsesAsync(schema, decision, rows, traceId, responseStream, ct)` — one row per caller: ok — routed passes a `List<(…)>` in selected order; head passes the diversified projection with the `byId` skip; both `request.TraceId` and `context.CancellationToken`.
- Task 4 → Task 5 (the routed binary): ok — the harness's request shape (listed type, chunked `Body`, no filter, both collections populated) satisfies every routing condition.
- Task 5 Step 3 → Step 4 (`fs2048-routed.{similar,chunks}.trec` + one `.meta.json`): ok — both run files and the sidecar come from one process (`BenchmarkQueryScenario.cs:222-224`, `:289-293`), so the first pairing cannot `BUILD MISMATCH`.

## 1. Verified-plan-assumptions cross-check

| # | Verdict | Evidence re-read |
|---|---|---|
| P1 | still holds | both files present (`ls`) |
| P2 | still holds | probe re-run: `RetrieveAsync(String, IReadOnlyList<PointId>, Boolean withPayload, Boolean withVectors, …)`; `RetrievedPoint.Id : PointId`, `PointId.Num : UInt64`; `RetrievedPoint.Payload : MapField<,>` |
| P3 | still holds | probe: `CollectionInfo.PointsCount : UInt64`, `HasPointsCount=True`; `GetCollectionInfoAsync(String, CancellationToken)` |
| P4 | still holds | `IntelligenceVectorService.cs:17`, `:30`; `using System.Diagnostics` at `:2` |
| P5 | still holds | grep: `IntelligenceVectorService.cs:9` is the only `: IVectorQueryService`; the DI binding at `ServiceCollectionExtensions.cs:43` forwards to it |
| P6 | still holds | `.OrderByDescending(r => r.FusedScore)` at `ResultReranker.cs:58-59` (round 1 noted the plan's `:44-45` citation is off; unchanged, harmless) |
| P7 | still holds | `ObjectSearchGrpcService.cs:784` |
| P8 | still holds | `IRowFieldAuthorizationEvaluator.cs:16` sealed record, `TenantValue` at `:59`; `SchemaDescriptor.cs:112` `sealed record ChunkDescriptor` |
| P9 | still holds | scratch compile resolves `SearchLogic.And` and `request.FilterLogic` / `request.Filter` on `SearchSimilarRequest` |
| P10 | still holds | `RequestHeaders.Use` at `:242`, `:413`, `:766`; `MintScopedApiKey(string, bool)` at `IntelligenceTenantScope.cs:18`; `ApplyOwnership(Filter?, bool, string?, string?)` at `IntelligenceFilterBuilder.cs:89` |
| P11 | still holds | `:181`, `:363` |
| P12 | still holds | `:31-43` |
| P13 | still holds | both csproj present; env and `~/.testcontainers.properties` both `false` |
| P14 | still holds | no numeric claim |
| P15 | still holds | `QdrantIntegrationTests.cs:13-48` |
| P16 | still holds | `VectorRankingOptionsTests.cs:11-15` |
| P17 | still holds | `:2613-2620` per-test constructor; `:1450-1455` `body_vector` stub with `parent_id`; `:2905-2926` asserts `CapturedLimit == 12` for `TopK = 3` |
| P18 | still holds | by construction; the scratch builds compile Task 3's declarations without Task 4 and Task 4 against Tasks 1–3 |
| P19 | still holds | `LoadTest/Program.cs:416-423`; `bench-env.sh` 329 B; `Iverson.Api/Program.cs:304`; compose `:436`, `:10`, `:31-36`; `SchemaRegistryRepository.cs:8` |
| P20 | still holds | the two `.snapshot` files listed, both with the infix |
| P21 | still holds | `git log -6` |
| P22 | still holds | every identifier resolved by the rebuilt scratch compile |
| P23 | still holds | `:361` |
| P24 | still holds | `docs/…:94` `### 3.` |
| P25 | still holds | `appsettings.json:3-5` `Default: Information`; only `appsettings.Development.json` exists beside it and compose runs `Production` |
| P26 | still holds | `:2466-2480`, type `Doc`, `Body` in `VectorFields` and `ChunkFields` |
| P27 | still holds | `ingest.py:230` `int.from_bytes(uuid.UUID(key).bytes[8:16], "little")`; `:588` object point at `parent_id`; `:648` `"parent_id": key` on every chunk |
| P28 | still holds | `LoadTest/Program.cs:88-89`, `:151-163` |
| P29 | still holds | `ChunkBudgetGuard.cs:31-36`: 200 / 3.104 = 64.4 ≥ 50 → `Ok` |
| P30 | still holds | grep: no `TreatWarningsAsErrors` / `EnforceCodeStyleInBuild` in any `.csproj` or `.props` under `Iverson.Server` |
| P31 | still holds | scratch builds re-run this round with the current Task 4 text (nullable descriptor, early return, two-clause branch point): 0 errors, one pre-existing warning |

**Span check** — plan dependencies with no covering assumption, verified in-round:

- Tests 1 and 3 assert that a chunk's raw score reaches the stream unchanged ("the parent appears once with 0.9"), which holds only if fusion is the identity when no centroid or decay signal is present. Verified: `ResultReranker.cs:25-33` short-circuits to `BaseScore` in exactly that case, and `DualAnnotatedSchema` resolves no decay field. Holds.
- Task 5 Step 2 runs `docker compose up` from whichever checkout holds the branch; the plan relies on that joining the box's existing project (network, sibling services) rather than creating a second one. Verified: no `name:` in the compose file, so the project name is the `Iverson.Server` basename in every checkout; `docker compose ls` shows the single project `iversonserver`, whose recorded working dir is already a worktree path that no longer exists, i.e. this exact hand-over has happened before. Holds.
- Task 5 Step 4's rule reads two paired deltas from one `report.py` invocation; an earlier defect consumed the baseline generator on the first measure. Verified: `report.py:660` materialises the baseline run with `list(...)` before the per-measure loop. Holds.

## 2. Literal-wrongness findings

1. **Task 4 Step 4 — the test-count gate demands one more test than Step 3 specifies.**
   - Description: Step 4 reads "Green; count ≥ floor + 15", where the floor is Task 1 Step 0's API count. Step 3 specifies 14 tests: items 1–4 (four), item 5 (eight fallback cases), item 6 (one), item 7 (one). The fifteenth was Task 3's `SearchChunks` limit test, which round 1's P17 fix removed — Task 3 Step 4 now reads "No new test" and Task 3 Step 5's gate is "≥ Step 0's API floor" — but Task 4's gate kept the old sum. Executed as written, an implementer who writes exactly the specified tests fails the step's own pass condition; the gate is unsatisfiable by the plan's steps.
   - Evidence: plan Task 3 Step 4 ("No new test") and Step 5 ("count ≥ Step 0's API floor"); plan Task 4 Step 3 (fourteen enumerated tests) and Step 4 ("count ≥ floor + 15"); review round 1 §0 "T4 count floor +15: ok — 4 + 8 + 2 + Task 3's 1", the last term of which no longer exists.
   - Proposed fix: change Task 4 Step 4 to "count ≥ floor + 14".

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round 1 P17 (the `SearchChunks` limit regression test already exists): the plan now cites `SearchChunks_OverFetchesFourTimesTopK_AndTrimsToTopK` (`:2905-2926`) in P17 and Task 3 Step 4 reads "No new test"; verified the cited test asserts `CapturedLimit == 12` for `TopK = 3`.
- Round 1 P26 (a dual-annotated fixture already exists): the plan now names `DualAnnotatedSchema()` (`:2466-2480`) as the Task 4 fixture and Step 3's stubs use its `docs_test-tenant` / `docs_chunks_test-tenant` collections; verified against the fixture.
- Round 1 §2.1 (a listed type with an un-chunked property fell back without the spec's `LogInformation` line): the branch point no longer null-checks `chunkDesc`; `TrySearchSimilarViaChunksAsync` takes `ChunkDescriptor?` and opens with `NotRouted("property is not chunked")`; test 5 now asserts a `LogInformation` call containing `not routed via chunks` for that case. The block compiles (this round's scratch build).
- Round 1 span-check items are now assumptions P27–P31 (ingest.py id agreement, schema re-registration, `ChunkBudgetGuard` at multiplier 4, no warnings-as-errors, the code blocks compile); each re-verified above.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — all 31 verified plan-level assumptions reconfirmed and the span check found nothing uncovered; §2 has one finding (Task 4 Step 4's `+15` floor is one higher than the 14 tests Task 4 Step 3 specifies, a leftover from round 1's removal of Task 3's duplicate test); §3 is empty. Address via `update-implementation-plan` or a one-word manual edit, then proceed to `subagent-driven-development`.
