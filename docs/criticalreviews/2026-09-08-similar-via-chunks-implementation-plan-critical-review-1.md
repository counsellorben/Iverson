# Critical Implementation Review: 2026-09-08-similar-via-chunks-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-08-similar-via-chunks-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA f5105a9); cited file:line references re-checked under §1. (The one commit, `8bf70bd`, adds the plan file itself; no source file changed.)

Reviewed against `main` `8bf70bd`. Prior review files for this plan basename: none (the two `…-design-critical-review-{1,2}.md` files belong to the spec). Verification aids used in this round: the Qdrant.Client 1.18.1 reflection probe re-run; a scratch copy of `Iverson.Vector` with Task 1's and Task 2's code applied verbatim, and a scratch copy of `Iverson.Api` with Task 4's code applied verbatim plus Task 3's declared signatures (`TryBuildChunksFilter` verbatim, the reshaped `BuildChunksFilter`, `ChunkPipeline`, `SearchChunksFusedAsync` with a stub body, `WriteSimilarResponsesAsync` with the moved body) — both build with 0 errors; a live read of the box's Qdrant and of the campaign directory (read-only). Nothing was added to the repository.

## 0. Coverage enumeration

**Task 1 — vector-service reads**

- T1 step prose (Step 0 floors; Step 3 test scenario): ok — floors are recorded from `main` (P14); the scenario maps onto `_svc.UpsertAsync` with a typed payload (precedent `QdrantIntegrationTests.cs:252-269`, integer canonicalised to its decimal string) and `_mgr.EnsureCollectionAsync`. The "+2" floor in Step 4 presumes two tests while Step 3 reads as one scenario: dropped — either way the outcome is unaffected; the implementer splits or the floor reads +1.
- T1 code, interface block: ok — compiles in the scratch copy; nested `IReadOnlyDictionary` return type is fine; NSubstitute substitutes (`ObjectSearchGrpcServiceTests.cs:41`, `DocumentTemplateValidationTests.cs:321`, `QdrantVectorServiceTests.cs:27,44,118`) absorb new members; `QdrantVectorServiceTests.cs:248` asserts the production class implements the interface, which Task 1 keeps true.
- T1 code, implementation block: ok — `RetrieveAsync(string, IReadOnlyList<PointId>, bool withPayload, bool withVectors, ReadConsistency, ShardKeySelector, CancellationToken)` confirmed by reflection; `ToCanonicalString` is `internal static` (`IntelligenceVectorService.cs:202`); `Telemetry.Source` / `ActivityStatusCode` in scope (`:17`, `:30`); `(PointId)id` and `ids.Chunk(512)` are the `:157-161` idiom; compiled.
- T1 commands (Steps 0, 4, 5): ok — both csproj paths exist; `TESTCONTAINERS_RYUK_DISABLED=false` matches `~/.testcontainers.properties` and the shell env; the three `git add` paths exist.
- T1 dynamic: ok — `client.UpsertAsync` waits by default (every existing integration test searches immediately after upsert); a never-written id is simply absent from `RetrieveAsync` (`:385` relies on the same for vectors); `GetCollectionInfoAsync` on a missing collection raises `RpcException` (NotFound), which Task 4 catches as "counts unavailable".

**Task 2 — the option**

- T2 code (property + blank check): ok — compiled; `?.Trim()` tolerates a null element; the trimmed value is written back in place; the check sits before `Options.Create(opts)` as instructed.
- T2 prose (compose entry): ok — the two λ entries are at `docker-compose.yml:445-446` under `iverson-api`; the value-less form is A23.
- T2 tests prose: ok — `BuildConfig` prefixes `VectorRanking:` (`VectorRankingOptionsTests.cs:11-15`); the read-back pattern for tests 1/3/4 exists at `:166-172`; test 2 relies on A22 (an empty in-memory value binds as one blank element).
- T2 rule — blank-entry check, both directions: ok — over-inclusion: `"  BenchmarkDocument "` is accepted and normalised; under-inclusion: `""`, whitespace and null are all rejected with the index in the message. An entry with an interior space passes; a CLR type name cannot contain one, so nothing real is mis-accepted.
- T2 command (Step 5 floor +6): ok — Task 1's 2 + Task 2's 4.

**Task 3 — extractions**

- T3 Step 1 prose + code: ok — `BuildChunksFilter` reads `request` only at `:873` and `:878`; exactly one caller (`:361`, P23); `RepeatedField<SearchClause>` satisfies the new parameter (A24); `TryBuildChunksFilter` compiled; a private method unused until Task 4 is not a build error (no `TreatWarningsAsErrors` / `EnforceCodeStyleInBuild` in any `Directory.Build.props` or csproj).
- T3 Step 2 prose, line references: dropped — the cited ranges (`:412-424`, `:425-438`, `:449-455`, `:457-468`) sit 2–7 lines short of the blocks they name (actual `:412-426`, `:431-443`, `:453-462`, `:464-475`); in particular `:457-462` is the chunk-vector retrieve the step says to *exclude*. The content labels ("scoped key … NotFound → results = []", "distinct parent ids, centroid retrieve with degrade", "decayField, now, candidates") are unambiguous and the file's existing `SearchChunks` tests guard the result, so the outcome cannot be broken by the offset; the implementer should follow the labels, not the numbers.
- T3 Step 2 code (record + helper signature): ok — compiled; `AuthorizationDecision.TenantValue` exists (`IRowFieldAuthorizationEvaluator.cs:59`); the helper is an instance method so `logger`, `tenantScope`, `vector`, `reranker`, `_decayOptions` all resolve inside it.
- T3 Step 2 prose, `SearchChunks` reassembly: ok — the chunk-vector retrieve (`:453-462`) reads only `results`, `chunksCollection`, `vectorName`, `topK`; `byId`, the diversify projection over the fused list and the stream loop are unchanged.
- T3 Step 3 code (`WriteSimilarResponsesAsync`): ok — the moved body depends only on schema / decision / payload / score / trace id (A11); `ConvertPayloadValue` (`:1015`) and `AuthorizationFieldMasking.MaskDisallowedFields` (`AuthorizationFieldMasking.cs:195`) are static, so a static method is valid; compiled with the moved body.
- T3 Step 4 test prose: → §1 P17 — an existing test already asserts the `SearchChunks` limit is `topK × 4`.
- T3 commands (Steps 5, 6): ok — command and paths exist; floor +1 holds if the (duplicate) test is still added.

**Task 4 — the routed branch**

- T4 Step 1 code (branch point): ok — compiled; `List<string>.Contains(string, StringComparer)` resolves to the LINQ overload; `centroidPossible` becomes `chunkDesc is not null`. The listed-type-but-embedded-only fallback: → §2.1.
- T4 Step 2 prose (condition order): ok — logic test → clause test → counts → zero checks matches spec §3.2's "counts are read only when 1–3 hold".
- T4 Step 2 code: ok — compiled verbatim; `using (…) x = await …;` leaves `objectCount`/`chunkCount` definitely assigned because the `catch` returns; `NotFound` catch precedes the general catch; `selected.Select(s => s.Id).ToList()` preserves collapsed order for the "exactly the top-10 ids in order" assertion; the `rows` tuple element names match the writer's parameter.
- T4 Step 2 dynamic — count reads: ok — each read runs under a freshly minted 30-second key scoped to its own collection (A20 live-probed in CDR round 1); a chunks collection that does not exist yet surfaces as `RpcException` → "counts unavailable" → head path, before any chunk search.
- T4 Step 2 dynamic — hydration: ok — `NotFound` swallowed → every selected parent is logged missing and the stream is empty (spec §3.6 row 7 behaviour, chosen by the spec); other `RpcException` → `Unavailable`; the delete window (A17) is the skip-and-warn case.
- T4 Step 2 dynamic — helper errors: ok — a non-`NotFound` `RpcException` from `SearchNamedAsync` propagates exactly as `SearchChunks` propagates it today; no new status code.
- T4 rule — collapse identity key `KeyToUlong(parent_id)`, both directions: ok — over-inclusion: two chunks of one parent collapse to the first sighting in fused-descending order (P6), which is the intended max-passage rule; two distinct parents conflate only on a 64-bit collision, the same risk the object point-id scheme itself carries (`IntelligenceStoreConsumer.cs:548`); under-inclusion: a chunk without `parent_id` is dropped (spec §3.5.1), a chunk whose parent is gone drops at hydration. The centroid dictionary is keyed by the same function (`:434`, `:471`), so `Centroids.TryGetValue(parentId, …)` is the right key.
- T4 rule — list membership: ok — `OrdinalIgnoreCase` on `schema.TypeName`, the registry's comparison (A3); Task 5's value `BenchmarkDocument` is that type's `TypeName` (`LoadTest/Program.cs:161`).
- T4 rule — chunk-expressibility, both directions, one row per producer of "not routed": ok — OR over ≥2 clauses is refused before `BuildChunksFilter` (which would otherwise silently AND, `:878-912`); a MustNot / non-EQUALS / scalar-column / unauthorised-metadata clause throws `InvalidArgument` inside and is caught; a key-column EQUALS becomes `parent_id`, a metadata EQUALS the same column on chunk points (A19). Producers: logic, clauses, count throw, object 0, chunk 0 — each has a test in Step 3.5. The remaining producers, unlisted type and un-chunked property, are the §2.1 row.
- T4 rule — budget arithmetic: ok — 13/10 → 2 → 80; 108/10 → 11 → 440; `objectCount > 0` is guaranteed before the division.
- T4 Step 3 tests prose: ok — the fixture shape at `:1119-1120` is valid; stub names `articles_test-tenant` / `articles_chunks_test-tenant` follow `ResolveCollectionName`; an unstubbed `GetPointCountAsync` returns 0, so every existing `SearchSimilar` test (empty list on `_sut`) stays on the head path; the ownership test needs its own owned + dual-annotated fixture, which "register a schema for these tests" permits. The "no existing fixture" claim: → §1 P26.
- T4 count floor +15: ok — 4 + 8 + 2 + Task 3's 1.

**Task 5 — reproduction check**

- T5 Step 1 commands: ok — the snapshot directory holds exactly two `.snapshot` files with the `-6802952876034638` infix (P20); the loop is `scifact-512-qdrant-snapshots/RESTORE.md`'s loop, which the fs-2048 `RESTORE.md` points at; the expected 6,000 / 18,622 match `keymap.json.stats.json`; `_iverson_schema.type_name` is the PK (`Iverson.Sql/SchemaRegistryRepository.cs:8-9`); the DELETE is the precedent of two earlier plans. The box currently holds the SciFact arm (5,183 / 19,967), so the restore is required, not optional.
- T5 Step 1 → Step 3 fixture handoff (schema row deleted in Postgres, then needed by the run) — persistence boundary: ok — `benchmark-query` is in `needsTenantAndSchema` (`LoadTest/Program.cs:88-89`) and `RegisterAllAsync` re-registers `BenchmarkDocument` before the scenario runs (`:151-163`).
- T5 Step 2 commands: ok — `build: context: ..` (`docker-compose.yml:427-429`) builds the checkout the shell is in; `VECTOR_RANKING_LAMBDA_CHUNKS` is the shell name at `:446`; the pass-through entry is A23; `/build` returns `composite` (`Iverson.Api/Program.cs:304-308`); no `Iverson.Server/.env` exists, so a worktree checkout resolves the same compose defaults as the main checkout; the `docker inspect` grep pattern matches `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument`.
- T5 Step 3 commands: ok — flags exist (`LoadTest/Program.cs:416-423`); `bench-env.sh` present; `ChunkBudgetGuard` at multiplier 4: 200 / 3.104 = 64.4 ≥ 50 → `Ok` (`ChunkBudgetGuard.cs`, `Ok = reachable >= documentBudget`); the harness's `SearchSimilar` queries `Body` with `TopK(50)` (`BenchmarkQueryScenario.cs:344-346`) → routed limit 50 × ⌈3.104⌉ × 4 = 800, as the step says; the "not routed" grep is meaningful at `Information` (P25) and no request in the run hits a fallback (no filter, both counts positive).
- T5 Step 3 `# 33600 each`: dropped — for `.chunks.trec` at 200 chunks per query, 50 rows per query is an expectation, not a guarantee (every prior run in `runs/` used multiplier 11 and shows 33,600; multiplier 4 has not been run), and it is not a gate — Step 4's coverage / duplicate block is. The `.similar.trec` count is exact by construction (`TopK(50)` + `CollapseByDocId`).
- T5 Step 4 commands: ok — `--run`, `--qrels`, `--baseline` exist (`report.py:827-849`); the structural block prints `covered by this run N / M` (`:313`) and `duplicate doc ids` (`:314-321`); `BUILD MISMATCH` at `:593`; `python-libs` holds `ir_measures`, `pytrec_eval`, `pyndeval`.
- T5 Step 5 commands: ok — Step 2 set both variables inline, so a fresh shell has neither; compose recreates the container on the config change.
- T5 Step 6 prose + command: ok — `### 3.` heading at `docs/…:94`; commit subject/trailer convention matches `git log -6`.
- T5 Step 3 dynamic — the restored snapshot was written by `ingest.py`, not the C# consumer, so A21's id agreement is not established for it by A21's evidence: → §1 span check (verified in-round, holds).

**Cross-task interface contracts**

- Task 1 → Task 4, `GetPointCountAsync(string)` (two call sites, one per collection) and `RetrievePayloadAsync(string, IReadOnlyList<ulong>)` (one call site): ok — signatures identical to the interface block; `selected.Select(s => s.Id).ToList()` is `List<ulong>`; compiled together.
- Task 2 → Task 4, `_ranking.SimilarViaChunksTypes`: ok — `List<string>` on the `VectorRankingOptions` the handler already holds (`:47`).
- Task 3 → Task 4, `TryBuildChunksFilter(schema, IReadOnlyList<SearchClause>, allowedFields, out Filter?)`: ok — Task 4 passes `request.Filter` (A24) and `decision.AllowedFields`.
- Task 3 → Task 4, `SearchChunksFusedAsync(schema, chunkDesc, decision, queryVector, filter, chunkLimit)` → `ChunkPipeline.{Results, Fused, Centroids}`: ok — all three fields consumed; `Centroids` is keyed by parent ulong (`:434-443`), which is what the collapse looks up.
- Task 3 → `SearchChunks` (second call site of the same helper, `topK * OverFetchFactor`): ok — 40 for `TopK = 10`, asserted by Step 4 and already by `:2924` (12 for `TopK = 3`).
- Task 3 → Task 4, `WriteSimilarResponsesAsync(schema, decision, rows, traceId, responseStream, ct)`: ok — Task 4's call matches positionally; `request.TraceId` is a string.
- Task 3 → head path (second call site of the writer): ok — projection `(r.Payload, ranked.FusedScore)` with the `byId` skip, per Step 3's prose.
- Task 4 → Task 5 (the routed binary; env `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument`): ok — index-0 binding into `List<string>` is A22's binder run; the value equals `schema.TypeName`.
- Task 5 Step 3 → Step 4 (`fs2048-routed.{similar,chunks}.trec` + one `.meta.json`): ok — both run files and the meta come from one harness process (`BenchmarkQueryScenario.cs:210, 289-293`), so the first pairing cannot `BUILD MISMATCH`; the second pairing's mismatch is expected and unscored, as the plan says.

## 1. Verified-plan-assumptions cross-check

| # | Verdict | Evidence re-read |
|---|---|---|
| P1 | still holds | `ls -l`: 16,809 B and 20,036 B |
| P2 | still holds | probe re-run: `RetrieveAsync(String, IReadOnlyList<PointId>, Boolean withPayload, Boolean withVectors, …)`; `RetrievedPoint.Id : PointId`, `PointId.Num : UInt64`; `RetrievedPoint.Payload : MapField<,>` |
| P3 | still holds | probe: `CollectionInfo.PointsCount : UInt64`, `HasPointsCount=True`; `GetCollectionInfoAsync(String, CancellationToken)` |
| P4 | still holds | `IntelligenceVectorService.cs:17` (`Telemetry.Source`), `:30` (`ActivityStatusCode.Ok`); `using System.Diagnostics` at `:2` |
| P5 | still holds | grep: `IntelligenceVectorService.cs:9` is the only `: IVectorQueryService`; every other reference is `Substitute.For<IVectorQueryService>()` |
| P6 | still holds | `.OrderByDescending(r => r.FusedScore)` is at `ResultReranker.cs:58-59`, not `:44-45` as cited (the plan's Task 4 code comment repeats the stale number; harmless) |
| P7 | still holds | `ObjectSearchGrpcService.cs:784` |
| P8 | still holds | `IRowFieldAuthorizationEvaluator.cs:16-59` (sealed record; `OwnershipRequired`, `OwnerValue`, `AllowedFields`, `TenantValue` at `:59`); `SchemaDescriptor.cs:112` (`ChunkDescriptor`, sealed record, `PropertyName`) |
| P9 | still holds | `object_search.proto:74-77` (`AND = 0`, `OR = 1`), `:107-108` (`filter`, `filter_logic` on `SearchSimilarRequest`) |
| P10 | still holds | `RequestHeaders.Use` already used at `:242`; `IntelligenceTenantScope.cs:18`; `IntelligenceFilterBuilder.cs:89` |
| P11 | still holds | `:181`, `:363` |
| P12 | still holds | `:31-43` |
| P13 | still holds | both csproj present; `~/.testcontainers.properties` has `ryuk.disabled=false`; env `TESTCONTAINERS_RYUK_DISABLED=false` |
| P14 | still holds | no numeric claim |
| P15 | still holds | `QdrantIntegrationTests.cs:13-48` |
| P16 | still holds | `VectorRankingOptionsTests.cs:11-15` |
| P17 | **failed** (third clause) | `SearchChunks_OverFetchesFourTimesTopK_AndTrimsToTopK` at `ObjectSearchGrpcServiceTests.cs:2905-2926` already asserts `CapturedLimit(_vector).Should().Be(12)` for `TopK = 3` — i.e. the chunk limit is `topK × 4`. The plan's grep looked for a literal `40` and so missed it. Consequence: Task 3 Step 4's "one regression test" duplicates an existing guard; the extraction is already covered by `:2905` (and by the rows assertion in `:1443-1467`). The first two clauses (per-test construction at `:2613-2620`; the `body_vector` stub with a `parent_id` payload at `:1450-1455`) hold. |
| P18 | still holds | by construction; confirmed by the scratch builds (Task 3's declarations compile without Task 4; Task 4 compiles against Tasks 1–3) |
| P19 | still holds | `LoadTest/Program.cs:416-423`; `bench-env.sh` 329 B; `/build` is `Iverson.Api/Program.cs:304` (the plan's "Program.cs:304" is the API's, not the harness's); compose `:436`, `:10`, `:31-36`; `_iverson_schema` is in `Iverson.Sql/SchemaRegistryRepository.cs:8-9` |
| P20 | still holds | `benchmark_documents_chunks_tenant_bypass-6802952876034638-2026-09-07-10-49-03.snapshot`, `benchmark_documents_tenant_bypass-6802952876034638-2026-09-07-10-48-59.snapshot` |
| P21 | still holds | `git log -6` |
| P22 | still holds | every identifier resolved by the scratch compile of Task 4's block |
| P23 | still holds | `:361` |
| P24 | still holds | `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md:94` |
| P25 | still holds | `appsettings.json:3-5`; no `appsettings.Production.json` exists to override it under compose's `ASPNETCORE_ENVIRONMENT=Production` |
| P26 | **failed** | `DualAnnotatedSchema()` at `ObjectSearchGrpcServiceTests.cs:2466-2480` registers type `Doc` with `VectorFields = [("Body", 768, …)]` and `ChunkFields = [("Body", 512, 64, …, 768)]` — a property that is both embedded and chunked, used by six `SearchSimilar` tests (`:2537`, `:2697`, `:2835`, `:2894`, `:3053`, …). Consequence: none for the outcome — Task 4 Step 3 may register its own `Article`-shaped fixture as written, or reuse `DualAnnotatedSchema()` (collection `docs_test-tenant`, `docs_chunks_test-tenant`); the plan's stated reason for a new fixture is simply untrue. |

**Span check** — plan dependencies with no covering assumption, verified in-round:

- The fs-2048 snapshot's object point ids and chunk `parent_id` values were written by `Iverson.LoadTest/scripts/ingest.py`, not by `IntelligenceStoreConsumer`, so A21's evidence does not reach Task 5's hydration (`RetrievePayloadAsync(objectCollection, KeyToUlong(parent_id) …)`). Verified: `ingest.py:230` computes `int.from_bytes(uuid.UUID(key).bytes[8:16], "little")` — byte-for-byte `KeyToUlong` — and `:588, :637, :648` write the object point at that id with `parent_id = key` on every chunk; a live probe on the SciFact snapshot now loaded on the box (same script) took a chunk's `parent_id 323fbe8d-9d43-53a2-9c65-94a8540736aa`, derived `12264998695377069468`, and retrieved the object point at that id with `key` equal to the parent id. Holds.
- Task 5 Step 1 deletes the `BenchmarkDocument` schema row; Step 3 needs it. Verified: `benchmark-query` triggers `RegisterAllAsync` (`LoadTest/Program.cs:88-89, 151-163`). Holds.
- `--chunk-budget-multiplier 4` must pass `ChunkBudgetGuard` for 6,000 / 18,622. Verified: reachable = 200 / 3.104 = 64.4 ≥ 50. Holds.
- Task 3 leaves `TryBuildChunksFilter` unreferenced until Task 4; verified no warnings-as-errors or code-style-in-build setting exists. Holds.
- The plan's code blocks compile against the real types (P22 covers identifier existence, not composition). Verified by the two scratch builds described above: 0 errors (the single warning, `VectorOutput.Data` obsolete at `:168`, is pre-existing). Holds.

## 2. Literal-wrongness findings

1. **Task 4 Step 1 — a listed type whose property is embedded but not chunked falls back silently, contrary to spec §3.2.**
   - Description: spec §3.2 states "Each failed condition on a *listed* type logs one `LogInformation` line, `[SearchSimilar] type={Type} not routed via chunks: {Reason}`, so an operator can see why a request kept the head path", and lists "`schema.ChunkFields` has a descriptor for `vectorDesc.PropertyName`" as condition 2. The plan's branch point is `chunkDesc is not null && _ranking.SimilarViaChunksTypes.Contains(…) && await TrySearchSimilarViaChunksAsync(…)`, so when the type is listed and `chunkDesc` is null the method — and its `NotRouted` logger — is never entered. The operator-facing behaviour the spec specifies for that case does not exist in the plan; Task 4's test 5 ("listed type whose property is embedded but not chunked") asserts only that the object search ran, so nothing in the plan would catch the omission.
   - Evidence: plan Task 4 Step 1 code block (`if (chunkDesc is not null && … )`); spec §3.2 conditions 1–2 and the sentence quoted above; `TrySearchSimilarViaChunksAsync` has no path that logs for a null descriptor (its parameter is non-nullable `ChunkDescriptor`).
   - Proposed fix: test list membership first and let the routed method own condition 2 — change the parameter to `ChunkDescriptor? chunkDesc`, open the method with `if (chunkDesc is null) return NotRouted("property is not chunked");`, and make the branch point `if (_ranking.SimilarViaChunksTypes.Contains(schema.TypeName, StringComparer.OrdinalIgnoreCase) && await TrySearchSimilarViaChunksAsync(schema, chunkDesc, …)) return;`. Alternatively keep the branch as written and add an explicit `else if (chunkDesc is null && listed) logger.LogInformation(…)` beside it. Either way the head path's `centroidPossible = chunkDesc is not null` is unchanged. (The scratch compile of the block as written passed; this is a behavioural omission, not a compile error.)

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has two failed test-convention assumptions (P17: the `SearchChunks` limit regression test already exists at `:2905-2926`; P26: a dual-annotated fixture already exists at `:2466-2480`), neither of which can break the spec's outcome but both of which the plan text states as false facts and builds steps on; §2 has one finding (the un-logged condition-2 fallback on a listed type). §3 is empty. Address via `update-implementation-plan` or manual edits, then proceed to `subagent-driven-development`.
