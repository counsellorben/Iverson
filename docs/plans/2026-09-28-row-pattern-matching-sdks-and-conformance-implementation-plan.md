# MatchPattern SDKs and Conformance (Plan 3 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-17-row-pattern-matching-design.md` (commit SHA: `4005914f`)

**Goal:** Give all five client SDKs (.NET, Java, Python, TypeScript, Go) a `MatchPatternBuilder` and a match-pattern coordinator method over the merged `ObjectSearchService.MatchPattern` RPC, and prove every SDK against a live server with one scalar-pattern case and one `SIMILARITY` case in the client conformance harness (spec §3.5, §9.5).

**Architecture:** The proto already declares the RPC (Plan 2). .NET and Java generate their stubs at build time; Go, Python and TypeScript regenerate their committed stubs (Task 1). Each SDK then gains a builder that sets every request field (pattern and expression strings pass through unchanged) and a coordinator method shaped like that SDK's own `Pipeline` method: .NET streams an `IAsyncEnumerable<MatchPatternResult>`, and the other four return a fully drained list of `MatchPatternResult` (row data plus `match_number` and `classifier`). A shared golden fixture pins all five builders to identical requests. The conformance harness gains a `match-pattern` scenario over a new shared `PatternDoc` type (three rows per language, partitioned by language), with requirements `IVC-QRY-005..007` and a projection wait that uses the RPC itself.

**Tech stack:** .NET 10 (`Grpc.Tools` 2.81.1, xunit 2.9.3, FluentAssertions 8.11.0, NSubstitute 5.3.0); Java 21 + Maven 3.9.9 (protobuf-maven-plugin 0.6.1, JUnit 5, Mockito, JSONAssert); Python 3.14 (grpcio-tools 1.81.1, protobuf 6.33, pytest); TypeScript (Node 22, ts-proto 2.12.4, vitest); Go (protoc-gen-go 1.36.5, protoc-gen-go-grpc 1.5.1, `go test`); `protoc` 29.3 at `~/sdk/protoc`.

---

## Global Constraints

- **Pattern and expression strings pass through unchanged. No builder validates anything** (spec §3.5); the server validates and maps errors (spec §6).
- **Each SDK mirrors its own `Pipeline` convention** — entry-point placement, value conversion for `Where`/`Not`, trace-id handling, acting-user identity, stream handling. .NET streams; Java, Python, TypeScript and Go drain the stream into a list, exactly as their `pipeline` methods do (user decision, 2026-09-27).
- **Row data is the raw `Struct`, converted by the SDK's existing struct helper, with no key-casing change.**
- **Builder defaults:** `limit` stays unset (0 → the server's default of 1,000). Do NOT copy `PipelineBuilder`'s 10,000 default. `after_match` stays unset unless `AfterMatch` is called.
- **Both golden fixtures are shared by all five builder tests** and must not be edited to suit one language.
- **Conformance literals are shared by the orchestrator and all five drivers** (Task 7 defines them; Task 8 must match them byte for byte).
- **Manual mutation check in every task review** (spec §9.6): delete or invert the named branch, confirm a covering test fails, restore, confirm `git status --porcelain` is clean.
- Commit messages are lowercase imperative subject lines, a blank line, then the `Co-Authored-By:` trailer. Commit only the task's files by explicit path; `docs/` is gitignored, so the standard needs `git add -f`.
- Shell `grep` in this repository skips gitignored paths; use `command grep` for any "no other occurrences" claim.

## File Structure

**Regenerated (Task 1):**
- `Iverson.Clients/Python/iverson_client/generated/object_search_pb2.py`, `object_search_pb2_grpc.py`
- `Iverson.Clients/Go/generated/object_search.pb.go`, `object_search_grpc.pb.go`
- all nine files under `Iverson.Clients/TypeScript/generated/`

**Create:**
- `Iverson.Clients/Common/testdata/match-pattern-contract-1.json`, `match-pattern-contract-2.json` — the shared golden fixtures (Task 2).
- `Iverson.Clients/DotNet/Iverson.Client.Search/MatchPatternBuilder.cs` (Task 2).
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/MatchPatternBuilder.java` (Task 3).
- `Iverson.Clients/Python/iverson_client/match_pattern.py` (Task 4).
- `Iverson.Clients/TypeScript/src/match-pattern.ts` (Task 5).
- `Iverson.Clients/Go/iverson/match_pattern.go` (Task 6).
- `Iverson.Server/Iverson.ClientConformance/Scenarios/MatchPatternScenario.cs` (Task 7).
- `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs`, `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/PatternDoc.java` (Task 8).

**Modify:**
- .NET: `Iverson.Client.Search/Query.cs`, `Iverson.Client.Core/EntityCoordinator.cs` (Task 2).
- Java: `client/.../search/Query.java`, `client/.../core/EntityCoordinator.java` (Task 3).
- Python: `iverson_client/core.py`, `iverson_client/__init__.py` (Task 4).
- TypeScript: `src/core.ts`, `src/index.ts` (Task 5).
- Go: `iverson/coordinator.go` (Task 6).
- Conformance orchestrator: `Requirements.cs`, `Program.cs` and `docs/standards/iverson-client-standard.md` (Task 7).
- Conformance drivers: the .NET `Program.cs`, Python `driver.py` and `models.py`, TypeScript `driver.ts` and `models.ts`, Go `main.go` and `models.go`, Java `Driver.java` (Task 8).

**Test:**
- `Iverson.Client.Search.Tests/MatchPatternBuilderTests.cs`, `Iverson.Client.Core.Tests/EntityCoordinatorMatchPatternTests.cs` (Task 2).
- `client/src/test/.../search/MatchPatternBuilderTest.java`; `client/src/test/.../core/EntityCoordinatorTest.java` gains a section (Task 3).
- `tests/test_match_pattern.py`; `tests/test_entity_coordinator.py` gains a test (Task 4).
- `tests/match-pattern.test.ts`; `tests/core.test.ts` gains a test (Task 5).
- `iverson_test/match_pattern_test.go`; `iverson/coordinator_test.go` gains the mock method and tests (Task 6).
- `Iverson.Server/Iverson.ClientConformance.Tests/MatchPatternScenarioTests.cs`; `RequirementsCoverageGateTests.cs` messages (Task 7).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and eight `critical-design-review` rounds at spec-write time, and are NOT re-verified here. They are trusted as ground truth. The list holds only the rows this plan rests on.

- One proto feeds the server and all five SDKs — `Iverson.Client.Contracts.csproj:17`, `Java/client/pom.xml:104`, and the `generate_protos.sh` scripts for Python, TS and Go all read `Common/Proto`
- SDK convention is a builder plus a coordinator method — `DotNet/.../EntityCoordinator.cs:296` `PipelineAsync(PipelineBuilder)`; `PipelineBuilder` in DotNet, Java and Go; `core.py`, `core.ts`
- Output rows are name-keyed `Struct`s: colliding names overwrite or vanish; the tenant-name strip is case-insensitive; names differing only by case survive at the server and in every SDK's untyped map — `SchemaDescriptor.cs:20-21`; `AuthorizationFieldMasking.cs:217-218`; CDR-1 probes P21, P21b, P21c
- Every `[IversonEmbedding]` property is also a `ColumnsFor` column, so a `SIMILARITY` column passes the §3.2 membership check — `SchemaBuilder.cs:60-64` (every non-key property becomes a `ScalarColumn`, before the `IsEmbedding` branch); the only removals are the tenant column, disallowed fields and bytes columns, each already a stated rejection
- Key → point ID; vector naming is `PropertyName.ToSnakeCase() + "_vector"`, with the property resolved case-insensitively from `VectorFields`/`ChunkFields`; chunk `field` holds the canonical property name — `IntelligenceStoreConsumer.cs:650` `KeyToUlong`; `ObjectSearchGrpcService.cs:162-163,253,506-507,638`; CDR-1 probes P2/P7 (Qdrant stores `title_vector`; the unconverted name fails)
- The new proto enum values and message names do not collide inside `package iverson` — CDR-3 §0 S3 greps (both exit 1)
- Global interceptors, no per-RPC name lists — `Program.cs:92-96`; no server references to RPC method names

Plan 2 (merged and pushed at `58133a1f`) delivered the `MatchPattern` RPC this plan's SDKs call.

## Verified plan-level assumptions

These assumptions are introduced by this plan and were verified on 2026-09-27 and 2026-09-28 at repo HEAD `58133a1f`. Every task's code and test blocks were prototyped in throwaway worktrees built from that HEAD, and the listed test counts were observed there.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of the Create paths exists, and `MatchPatternBuilder`, `match_pattern`, `MatchPatternResult`, `PatternDoc`, `MatchPatternScenario` and `IVC-QRY-005..007` appear nowhere in hand-written code | Repository-wide `command grep` for each name (generated code excluded) returns 0 hits |
| 2 | File path | `Iverson.Clients/Common/testdata/pipeline-contract-1.json` exists and is read by all five pipeline builder golden tests, so the new fixtures belong beside it; the .NET test project already copies `../../Common/testdata/**/*.json` | .NET `PipelineBuilderTests`; Java `PipelineBuilderTest` (`JsonFormat` + `JSONAssert`); Python `tests/test_pipeline.py:13-15,161-164`; TS `tests/pipeline.test.ts:20,152-155`; Go `iverson_test/pipeline_test.go:223-244`; `Iverson.Client.Search.Tests.csproj` `Content Include` |
| 3 | Signature | .NET: `PipelineBuilder.Where/Not(string field, SearchOperator op, object value)` build clauses through `AddBaseClause`; `Query.Pipeline(string)`/`Pipeline<T>()`; `EntityCoordinator<T>.PipelineAsync` builds the request, sets `TraceId = CurrentTraceId()`, calls `search.Pipeline(request, await ResolveHeadersAsync(null), cancellationToken: ct)` and yields `StructConverter.ToDictionary(response.Data)`; `SearchResult<T>` is at `EntityCoordinator.cs:380` | `PipelineBuilder.cs:26-33`; `Query.cs:23,26`; `EntityCoordinator.cs:296-309,373,380` |
| 4 | Signature | Java: `Query.pipeline(String)`; `PipelineBuilder.where/not/withLogic`; `EntityCoordinator.pipeline` drains `stubFor(null).pipeline(request)` into `StructConverter.fromStructAsMap` maps; nested records at `:367,370`; the blocking stub's generated `matchPattern` returns `Iterator<MatchPatternResponse>` | `Query.java:56-58`; `EntityCoordinator.java:269-282,367,370`; generated `ObjectSearchServiceGrpc.java:593` after `mvn compile` |
| 5 | Signature | Python: `PipelineBuilder.where/not_/with_logic` with `_to_search_value`; `build(trace_id="")`; `EntityCoordinator.pipeline(request)` drains `self._search.Pipeline(request, metadata=self._acting_user_metadata())` through `_struct_to_dict`; `SearchResult` frozen dataclass; exports in `__init__.py` and `__all__` | `pipeline.py:249-257,285,303`; `search.py:38-53`; `core.py:471-472,582-590,633,801-805`; `__init__.py:23,25` |
| 6 | Signature | TypeScript: `PipelineBuilder.where/not/withLogic` with `toSearchValue`; `build(traceId = '')`; search-family methods live on `IversonClient` (no `implements`), and `pipeline` uses `_collectSearchStream`, which Task 5 generalises over the response type without changing existing callers | `pipeline.ts:281-292`; `search.ts:33-53`; `core.ts:812,945-974`; Task 5 prototype (`npm test` 262 passed) |
| 7 | Signature | Go: `Where/Not(field string, op pb.SearchOperator, val *pb.SearchValue)`; `EntityCoordinator[T].Pipeline` Recv-loops to `io.EOF` with wrapped errors; `SearchClient` (`coordinator.go:46-53`) has exactly two implementers, `searchAdapter` and the test `mockSearchClient`, both updated in Task 6 | `pipeline.go:36-49`; `coordinator.go:461-477,897-923`; `coordinator_test.go:656+`; `command grep -rn SearchClient Iverson.Clients/Go` |
| 8 | Code validity | Generated names after Task 1, as the plan's code uses them: Python `MatchPatternRequest` keyword fields and enum constants; ts-proto camelCase interfaces, numeric enums with `UNRECOGNIZED = -1`, `matchNumber: number`; Go `pb.MatchPatternRequest` fields (`Limit int32`), `pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY`, and client `MatchPattern(ctx, in, opts...) (grpc.ServerStreamingClient[MatchPatternResponse], error)`; .NET and Java generate at build time | Regenerated files in the Task 1 prototype; `obj/Debug/net10.0/ObjectSearch.cs:275,282,9592`; `target/generated-sources/.../ObjectSearch.java:1219,1354,39248` |
| 9 | Code validity | A .NET builder method named `RowsPerMatch(RowsPerMatch value)` compiles; inside the builder the bare name then means the method, so the backing field has no initializer (default `ONE_ROW`) | Scratch compile; Task 2 prototype |
| 10 | Code validity | All five SDKs' JSON emitters reproduce both golden fixtures exactly under each SDK's existing golden comparison | Task 2–6 prototypes: .NET 7/7, Java 25/25, Python 34, TS 53, Go builder tests green against identical fixture files (md5-matched across worktrees) |
| 11 | Command | Python and Go regeneration change only their two `object_search` files (+76/-17 and +830/-153); both suites stay green (218 passed; all Go packages ok) | Task 1 prototype |
| 12 | Command | TypeScript: `npm ci` installs ts-proto 2.12.4 and leaves `package.json`/`package-lock.json` unchanged; `npm run generate` rewrites all nine generated files (+4105/-2472; committed code is 2.11.9); `npm test` then passes 255 | Task 1 prototype; generated file headers |
| 13 | Command | SDK test commands: `dotnet test Iverson.slnx --filter "Category!=Integration"`; `mvn -f Iverson.Clients/Java/pom.xml -pl client -am test` (JAVA_HOME `~/sdk/java21`, Maven `~/sdk/apache-maven-3.9.9`, network for BOM resolution); `python3 -m pytest -q` in `Iverson.Clients/Python`; `npm test` in `Iverson.Clients/TypeScript`; `go test ./...` in `Iverson.Clients/Go` with the `~/sdk/go1.22` env | Each run in the Task 2–6 prototypes, with the counts in each task |
| 14 | Consumer impact | Adding members breaks no implementer: .NET `EntityCoordinator`/`Query` and Java `EntityCoordinator`/`Query` implement no interface and have no subclasses or fakes; TS `IversonClient` implements nothing; Go's two `SearchClient` implementers are both updated in Task 6 | `command grep` across all five SDKs, drivers and samples; Task 2–6 full suites green |
| 15 | File path | The standard `docs/standards/iverson-client-standard.md` is tracked (`git add -f` needed) and read by `RequirementsCoverageGateTests` (walks up to `Iverson.slnx`); Check4 requires each new Active id in exactly one Covered ledger area; the "46 Active" sentence is prose only | `git ls-files`; `RequirementsCoverageGateTests.cs:97,114-115`; standard `:78,660-707` |
| 16 | Signature | Orchestrator: scenarios register in `Program.cs` `recognizedScenarios` plus a dispatch block; `VectorSearchScenario` is the model (register-once, re-register, write, projection wait, read, `GradeReads`, pure `Judge`); its `ExpectedKeys` reads one key per language, so the new scenario has its own three-key reader; its tests exercise `Judge`, readers and `GradeReads` directly | `Program.cs:66-72,121-124,192-199`; `VectorSearchScenario.cs`; `VectorSearchScenarioTests.cs`; Task 7 prototype (609 passed) |
| 17 | Consumer impact | `TokenBroker` requires `IVERSON_ACTING_USER_BYPASS_PASSWORD` and `IVERSON_OTHER_TENANT_PASSWORD` unconditionally, sourced from `.env` keys `IVERSON_BYPASS_PASSWORD` and `IVERSON_SMOKE_TEST_PASSWORD`; the orchestrator's `--help` text says otherwise (fixed in Task 7) | `TokenBroker.cs:59,80,200-204`; Authentik blueprints under `deploy/helm/iverson/charts/authentik/blueprints` |
| 18 | Ordering | Python, TypeScript and Go drivers have catch-all crud-roundtrip register/write/read branches; the new `match-pattern` branches must precede them, and do in Task 8 | Driver dispatch chains; Task 8 smoke runs (every driver reports the `match-pattern` steps; none exits 2) |
| 19 | Code validity | Integer model fields: .NET/Java/Python `int` register as `CLR_INT32`; Go needs `int32` (`int` → `CLR_INT64`); TypeScript cannot declare an integer, which is harmless because only .NET's descriptor is registered; all five write JSON numbers | SDK registrars; Task 8 captured .NET and Python descriptors show `Seq` `CLR_INT32` |
| 20 | Code validity | The scalar case returns one row per partition, `{Label, n: 3, first_seq: 1, last_seq: 3}`, `match_number 1`, `classifier ""`; unqualified `Seq > PREV(Seq)` is accepted | Real engine through `MatchPatternGrpcServiceTests` helpers (throwaway test) |
| 21 | Code validity | The SIMILARITY case returns three rows per partition, `classifier "A"`, `match_number 1`, `s` a float32 score widened to double; the two identical `(Title, text)` terms embed once; an ALL_ROWS read projects `Id, TenantId, OwnerId, Marker, Title, Label, Seq` | Same throwaway test; `MatchRowsQueryBuilder.cs` projection |
| 22 | Code validity | A .NET `int` property becomes SqlType `INTEGER` / StarRocks `INT`; `[IversonEmbedding] Title` is also a StarRocks `VARCHAR(1048576)` column; `[IversonMetadata] Marker` is a plain StarRocks column, so `where Marker EQUALS` builds `WHERE (\`Marker\` = @p0) AND \`__TenantId\` = @__tenantVal` | `SchemaBuilder.cs`; `MatchRowsQueryBuilder` build |
| 23 | Code validity | The projection probe (`FIRST(A.Id)` over a string key, ONE_ROW, `define A AS SIMILARITY(...) IS NOT NULL`) yields one response per row whose similarity is non-NULL, carrying that row's id, and skips a NULL row | Throwaway engine test on 2026-09-28: ids `id-1, id-3` returned when row 2's score is NULL. Registration rejects any key column that is not UUID (`Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:178`), and `SchemaBuilder.cs:390` maps it to StarRocks `VARCHAR(36)`, read back as a string, so `FIRST(A.Id)` reaches the probe as a string value |
| 24 | Command | Live run: compose file `Iverson.Server/docker-compose.yml`; `docker compose build iverson-api` then `docker compose up -d` (api and worker share the image); the orchestrator finds the repository by walking up to `Iverson.slnx` | Compose comment `:433-443`; `DriverRunner.cs:94-121` |
| 25 | Ordering | Task 1 precedes Tasks 4, 5, 6 and 8; Task 2 creates the fixtures Tasks 3–6 read; Task 7 needs only the generated .NET client, which exists today; Task 8 needs Tasks 2–6 (SDK APIs) and Task 7 (literals); Task 9 is last | Each task's Interfaces section; the Task 8 prototype applied the Task 2–6 patches in order |
| 26 | Command | Commit messages are lowercase imperative subject lines | `git log --format=%s -15` |
| 27 | Consumer impact | Dependencies the scenario relies on without stating them: (1) `ProjectionWaiter.WaitAsync` catches every probe exception per attempt and keeps polling, so a failing probe cannot escape the wait; (2) the probe's `limit` of 10,000 is within the server's `MaxOutputRows` default of 10,000 (no override); (3) one `IdPrefix` per scenario run, so every language's read sees every language's rows; (4) the expected sets come from write-phase keys (`MergeKeys`), never from the read being judged; (5) `CompiledPattern.Run` is called per partition, so `MATCH_NUMBER()` is 1 in every language's partition; (6) each SDK's new call carries the acting user exactly as its `Pipeline` call does — required, because the server answers a denied caller with an empty stream, not an error | CIR-1 (2026-09-28) in-round verification: `ProjectionWaiter.cs` per-attempt `catch (Exception)`; `PatternQueryLimitOptions.MaxOutputRows` default; `Program.cs` `BuildContext` `IdPrefix`; `DriverRunner.MergeKeys`; per-partition `Run` in `ObjectSearchGrpcService.MatchPattern.cs`; each SDK's `MatchPattern` call path |

## Tasks

### Task 1: Regenerate the committed Go, Python and TypeScript stubs

**Files:**
- Modify (generated): `Iverson.Clients/Python/iverson_client/generated/object_search_pb2.py`, `Iverson.Clients/Python/iverson_client/generated/object_search_pb2_grpc.py`
- Modify (generated): `Iverson.Clients/Go/generated/object_search.pb.go`, `Iverson.Clients/Go/generated/object_search_grpc.pb.go`
- Modify (generated): all nine files under `Iverson.Clients/TypeScript/generated/` (`google/protobuf/{empty,struct,wrappers}.ts`, `object_mapping.ts`, `object_persistence.ts`, `object_retrieval.ts`, `object_search.ts`, `tenant_admin.ts`, `tenant_lifecycle.ts`)

.NET (`Iverson.Client.Contracts.csproj`) and Java (`protobuf-maven-plugin`) generate their stubs at build time from `Iverson.Clients/Common/Proto`, and nothing generated for them is committed, so they need no step here.

**Interfaces:**
- Produces: the `MatchPattern` stubs and messages in the three committed generated trees, consumed by Tasks 4, 5, 6 and 8.

- [ ] **Step 1: Confirm the committed stubs lack `MatchPattern`**

Run: `command grep -c MatchPattern Iverson.Clients/Python/iverson_client/generated/object_search_pb2_grpc.py Iverson.Clients/Go/generated/object_search_grpc.pb.go Iverson.Clients/TypeScript/generated/object_search.ts`
Expected: `0` for each file.

- [ ] **Step 2: Regenerate Python and Go**

```bash
bash Iverson.Clients/Python/scripts/generate_protos.sh
bash Iverson.Clients/Go/scripts/generate_protos.sh
```
Both scripts change to their own directory, so the caller's working directory does not matter. The Go script sets its own `PATH`/`GOPATH`/`GOROOT` (`~/sdk/go1.22`, `~/go`) and uses `~/sdk/protoc/bin/protoc`.

Run: `git diff --stat -- Iverson.Clients/Python/iverson_client/generated Iverson.Clients/Go/generated`
Expected: exactly four files — Python's `object_search_pb2.py` and `object_search_pb2_grpc.py` (about +76/-17 between them), and Go's `object_search.pb.go` and `object_search_grpc.pb.go` (about +830/-153 between them). No other generated file changes.

- [ ] **Step 3: Run the Python and Go suites**

Run: `cd Iverson.Clients/Python && python3 -m pytest -q`
Expected: `218 passed`.

Run: `cd Iverson.Clients/Go && PATH=~/sdk/go1.22/bin:~/go/bin:$PATH GOPATH=~/go GOROOT=~/sdk/go1.22 go test ./...`
Expected: `ok` for `conformance`, `iverson` and `iverson_test`.

- [ ] **Step 4: Commit the Python and Go stubs**

```bash
git add Iverson.Clients/Python/iverson_client/generated/object_search_pb2.py Iverson.Clients/Python/iverson_client/generated/object_search_pb2_grpc.py Iverson.Clients/Go/generated/object_search.pb.go Iverson.Clients/Go/generated/object_search_grpc.pb.go
git commit -m "regenerate the python and go object search stubs for matchpattern" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 5: Regenerate TypeScript with the locked toolchain**

```bash
cd Iverson.Clients/TypeScript
npm ci
npm run generate
```
`npm ci` installs ts-proto 2.12.4 (the version `package.json` and `package-lock.json` declare) and leaves both files unchanged. The committed stubs were produced by ts-proto 2.11.9, so regeneration rewrites every generated file: each header changes to `v2.12.4`, and every `decode()` gains ts-proto's recursion-depth guard. This whole-tree diff is expected and is committed on its own (user decision, 2026-09-27).

Run: `git diff --stat -- Iverson.Clients/TypeScript/generated`
Expected: 9 files changed, about +4105/-2472; `git status --porcelain -- Iverson.Clients/TypeScript/package.json Iverson.Clients/TypeScript/package-lock.json` prints nothing.

- [ ] **Step 6: Run the TypeScript suite**

Run: `cd Iverson.Clients/TypeScript && npm test`
Expected: the typecheck (`tsc -p tsconfig.test.json`, which includes `conformance/**/*`) is clean, then `Test Files  9 passed (9)` and `Tests  255 passed (255)`.

- [ ] **Step 7: Commit the TypeScript stubs**

```bash
git add Iverson.Clients/TypeScript/generated
git commit -m "regenerate the typescript stubs with ts-proto 2.12.4 for matchpattern" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: .NET SDK — `MatchPatternBuilder`, `EntityCoordinator<T>.MatchPatternAsync` and the golden fixtures

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/MatchPatternBuilder.cs`
- Create: `Iverson.Clients/Common/testdata/match-pattern-contract-1.json`
- Create: `Iverson.Clients/Common/testdata/match-pattern-contract-2.json`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs:26` (add the two `MatchPattern` entry points after `Pipeline<T>()`)
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs:332` (add `MatchPatternAsync` after the typed `PipelineAsync<TResult>`, before `GroupByAsync`) and `:380` (add the `MatchPatternResult` record after `SearchResult<T>`)
- Test: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/MatchPatternBuilderTests.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMatchPatternTests.cs`

**Interfaces:**
- Consumes: the generated `Iverson.Client.Contracts` types `MatchPatternRequest`, `MatchPatternResponse`, `PatternRowSource`, `RowsPerMatch`, `AfterMatchSkipKind`, `AfterMatchSkip`, `PatternSubset`, `NamedExpr` and the `ObjectSearchService.ObjectSearchServiceClient.MatchPattern` streaming call (all generated at build time from `Common/Proto/object_search.proto`; nothing is committed); `SearchValueConverter.ToSearchValue` (Search); `StructConverter.ToDictionary`, `CurrentTraceId()`, `ResolveHeadersAsync` (Core).
- Produces: `Query.MatchPattern(string)`, `Query.MatchPattern<T>()`, `MatchPatternBuilder`, `EntityCoordinator<T>.MatchPatternAsync`, `MatchPatternResult` (used by the .NET conformance driver task); the golden fixtures `match-pattern-contract-1.json` and `match-pattern-contract-2.json` (read by the Java, Python, TypeScript and Go builder tests).

- [ ] **Step 1: Create the golden fixtures and write the failing tests**

The fixtures hold the contract JSON, 4-space indented like `pipeline-contract-1.json`, with a trailing newline. The existing `<Content Include="../../Common/testdata/**/*.json" LinkBase="testdata" …/>` item in `Iverson.Client.Search.Tests.csproj` already copies them next to the test assembly, so no project file changes.

`Iverson.Clients/Common/testdata/match-pattern-contract-1.json`:
```json
{
    "typeName": "PatternDoc",
    "where": [
        {
            "property": "Marker",
            "value": {
                "stringVal": "m1"
            }
        },
        {
            "property": "Label",
            "value": {
                "stringVal": "skip"
            },
            "clauseType": "MUST_NOT"
        }
    ],
    "whereLogic": "OR",
    "partitionBy": [
        "Label"
    ],
    "orderBy": [
        {
            "property": "Seq"
        },
        {
            "property": "Id",
            "descending": true
        }
    ],
    "pattern": "A B+",
    "subsets": [
        {
            "name": "AB",
            "variables": [
                "A",
                "B"
            ]
        }
    ],
    "define": [
        {
            "name": "B",
            "expr": "Seq > PREV(Seq)"
        }
    ],
    "measures": [
        {
            "name": "n",
            "expr": "COUNT(*)"
        },
        {
            "name": "last_seq",
            "expr": "LAST(B.Seq)"
        }
    ],
    "rowsPerMatch": "ALL_ROWS_SHOW_EMPTY",
    "afterMatch": {
        "kind": "TO_FIRST",
        "variable": "B"
    },
    "limit": 100,
    "traceId": "trace-1"
}
```

`Iverson.Clients/Common/testdata/match-pattern-contract-2.json`:
```json
{
    "typeName": "VectorDoc",
    "source": "CHUNKS",
    "chunkProperty": "Body",
    "pattern": "A",
    "define": [
        {
            "name": "A",
            "expr": "SIMILARITY(text, 'refund') > 0.5"
        }
    ]
}
```

`Iverson.Clients/DotNet/Iverson.Client.Search.Tests/MatchPatternBuilderTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class MatchPatternBuilderTests
{
    // ── Cross-language golden-fixture contract ─────────────────────────────────
    // Golden fixtures checked in at Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json.
    // Java, Python, TypeScript and Go each assert that their builder produces the same structural
    // JSON from the same call sequence. Contract 1 sets every TYPE_ROWS field; contract 2 covers the
    // CHUNKS source and a request with no trace id. Do not hand-edit the JSON to make one SDK pass.

    [Fact]
    public void Build_MatchesGoldenFixture_MatchPatternContract1()
    {
        var request = Query.MatchPattern("PatternDoc")
            .Where("Marker", EqualTo, "m1")
            .Not("Label", EqualTo, "skip")
            .WithLogic(SearchLogic.Or)
            .PartitionBy("Label")
            .OrderBy("Seq")
            .OrderBy("Id", descending: true)
            .Pattern("A B+")
            .Subset("AB", "A", "B")
            .Define("B", "Seq > PREV(Seq)")
            .Measure("n", "COUNT(*)")
            .Measure("last_seq", "LAST(B.Seq)")
            .RowsPerMatch(RowsPerMatch.AllRowsShowEmpty)
            .AfterMatch(AfterMatchSkipKind.ToFirst, "B")
            .Limit(100)
            .Build("trace-1");

        AssertMatchesGolden(request, "match-pattern-contract-1.json");
    }

    [Fact]
    public void Build_MatchesGoldenFixture_MatchPatternContract2()
    {
        var request = Query.MatchPattern("VectorDoc")
            .Chunks("Body")
            .Pattern("A")
            .Define("A", "SIMILARITY(text, 'refund') > 0.5")
            .Build("");

        AssertMatchesGolden(request, "match-pattern-contract-2.json");
    }

    [Fact]
    public void Chunks_SetsSourceAndChunkProperty()
    {
        var request = Query.MatchPattern("VectorDoc").Chunks("Body").Build();

        request.Source.Should().Be(PatternRowSource.Chunks);
        request.ChunkProperty.Should().Be("Body");
    }

    [Fact]
    public void AfterMatch_IsUnsetUnlessCalled()
    {
        Query.MatchPattern("PatternDoc").Pattern("A").Build().AfterMatch.Should().BeNull();

        var request = Query.MatchPattern("PatternDoc").AfterMatch(AfterMatchSkipKind.ToNextRow).Build();
        request.AfterMatch.Should().NotBeNull();
        request.AfterMatch.Kind.Should().Be(AfterMatchSkipKind.ToNextRow);
        request.AfterMatch.Variable.Should().BeEmpty();
    }

    [Fact]
    public void Limit_DefaultsToZero()
    {
        Query.MatchPattern("PatternDoc").Build().Limit.Should().Be(0);
    }

    [Fact]
    public void Build_WithoutTraceId_GivesEmptyTraceId()
    {
        Query.MatchPattern("PatternDoc").Build().TraceId.Should().BeEmpty();
    }

    [Fact]
    public void Build_Typed_UsesTypeName()
    {
        Query.MatchPattern<MatchPatternBuilderTests>().Build().TypeName.Should().Be(nameof(MatchPatternBuilderTests));
    }

    private static void AssertMatchesGolden(MatchPatternRequest request, string fixture)
    {
        var actualJson = Google.Protobuf.JsonFormatter.Default.Format(request);
        var actual = System.Text.Json.JsonDocument.Parse(actualJson).RootElement;

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "testdata", fixture);
        var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(goldenPath)).RootElement;

        // Re-serialize both to a canonical compact form, exactly as PipelineBuilderTests does:
        // GetRawText() keeps the golden file's indentation.
        System.Text.Json.JsonSerializer.Serialize(actual).Should().Be(System.Text.Json.JsonSerializer.Serialize(expected));
    }
}
```

`Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMatchPatternTests.cs`:
```csharp
using System.Diagnostics;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Value = Google.Protobuf.WellKnownTypes.Value;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;
using static Iverson.Client.Core.Tests.TestStreamHelper;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorMatchPatternTests
{
    [IversonEntity]
    private sealed class TestArticle
    {
        [IversonKey]
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }

    private static MatchPatternResponse Response(string label, double n, long matchNumber, string classifier)
    {
        var data = new Struct();
        data.Fields["Label"] = Value.ForString(label);
        data.Fields["n"] = Value.ForNumber(n);
        return new MatchPatternResponse { Data = data, MatchNumber = matchNumber, Classifier = classifier };
    }

    [Fact]
    public async Task MatchPatternAsync_StreamsRowsWithMatchNumberAndClassifier()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var responses = new List<MatchPatternResponse> { Response("x", 3, 1, ""), Response("y", 2, 2, "B") };
        search.MatchPattern(Arg.Any<MatchPatternRequest>(), Arg.Any<Metadata>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<MatchPatternResult>();
        await foreach (var row in coordinator.MatchPatternAsync(Query.MatchPattern<TestArticle>().Pattern("A B+")))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].Data["Label"].Should().Be("x");
        rows[0].Data["n"].Should().Be(3d);
        rows[0].MatchNumber.Should().Be(1);
        rows[0].Classifier.Should().BeEmpty();
        rows[1].Data["Label"].Should().Be("y");
        rows[1].MatchNumber.Should().Be(2);
        rows[1].Classifier.Should().Be("B");
    }

    [Fact]
    public async Task MatchPatternAsync_SendsTheBuildersRequest()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        MatchPatternRequest? captured = null;
        search.MatchPattern(
                Arg.Do<MatchPatternRequest>(r => captured = r),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<MatchPatternResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);
        var pattern = Query.MatchPattern<TestArticle>()
            .PartitionBy("Label")
            .OrderBy("Id")
            .Pattern("A B+")
            .Define("B", "Id > PREV(Id)")
            .Measure("n", "COUNT(*)")
            .Limit(7);

        await foreach (var _ in coordinator.MatchPatternAsync(pattern)) { }

        captured.Should().NotBeNull();
        captured!.Should().Be(pattern.Build(captured.TraceId));
    }

    [Fact]
    public async Task MatchPatternAsync_TakesTraceIdFromAmbientActivity()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        MatchPatternRequest? captured = null;
        search.MatchPattern(
                Arg.Do<MatchPatternRequest>(r => captured = r),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<MatchPatternResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        using var activity = new Activity("match-pattern-test").Start();
        await foreach (var _ in coordinator.MatchPatternAsync(Query.MatchPattern<TestArticle>().Pattern("A"))) { }

        captured.Should().NotBeNull();
        captured!.TraceId.Should().NotBeEmpty().And.Be(activity.TraceId.ToString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests --filter "FullyQualifiedName~MatchPatternBuilderTests"`
Expected: build FAIL with 8 × `error CS0117: 'Query' does not contain a definition for 'MatchPattern'`.

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests --filter "FullyQualifiedName~EntityCoordinatorMatchPatternTests"`
Expected: build FAIL with 7 errors: `CS0246` (`MatchPatternResult` not found), 3 × `CS1061` (no `MatchPatternAsync`) and 3 × `CS0117` (no `Query.MatchPattern`).

- [ ] **Step 3: Implement the builder, the entry points and the coordinator method**

`Iverson.Clients/DotNet/Iverson.Client.Search/MatchPatternBuilder.cs`. It mirrors `PipelineBuilder`'s base-step `Where`/`Not`/`WithLogic` and `Build(string? traceId = null)`. It deliberately has NO client-side validation, and it does NOT copy `PipelineBuilder`'s 10,000 limit default: `Limit` stays 0 (the server's default) and `AfterMatch` stays unset unless called. The `_rowsPerMatch` field has no initializer, because inside this class the simple name `RowsPerMatch` in an expression binds to the method group. Its default, `ONE_ROW`, is enum value 0.
```csharp
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder that compiles to a <see cref="MatchPatternRequest"/> (SQL:2016 row pattern
/// recognition). Pattern, DEFINE and MEASURES strings pass through unchanged; the server parses
/// and validates them. String-addressed like <see cref="PipelineBuilder"/>.
/// </summary>
public sealed class MatchPatternBuilder
{
    private readonly string _typeName;
    private readonly List<SearchClause>  _where       = [];
    private readonly List<string>        _partitionBy = [];
    private readonly List<SearchSort>    _orderBy     = [];
    private readonly List<PatternSubset> _subsets     = [];
    private readonly List<NamedExpr>     _define      = [];
    private readonly List<NamedExpr>     _measures    = [];
    private PatternRowSource _source        = PatternRowSource.TypeRows;
    private string           _chunkProperty = string.Empty;
    private SearchLogic      _whereLogic    = SearchLogic.And;
    private string           _pattern       = string.Empty;
    private RowsPerMatch     _rowsPerMatch;   // default ONE_ROW
    private AfterMatchSkip?  _afterMatch;     // unset: the server applies PAST_LAST_ROW
    private int              _limit;          // 0: the server's default

    internal MatchPatternBuilder(string typeName) => _typeName = typeName;

    // ── Row source ──────────────────────────────────────────────────────────────

    /// <summary>Matches over the chunks of an <c>[IversonChunk]</c> property instead of the type's rows.</summary>
    public MatchPatternBuilder Chunks(string chunkProperty)
    {
        _source        = PatternRowSource.Chunks;
        _chunkProperty = chunkProperty;
        return this;
    }

    // ── Pre-filter ──────────────────────────────────────────────────────────────

    public MatchPatternBuilder Where(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.Filter);

    public MatchPatternBuilder Not(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.MustNot);

    public MatchPatternBuilder WithLogic(SearchLogic logic)
    {
        _whereLogic = logic;
        return this;
    }

    // ── Partitioning and ordering ───────────────────────────────────────────────

    public MatchPatternBuilder PartitionBy(params string[] fields)
    {
        _partitionBy.AddRange(fields);
        return this;
    }

    public MatchPatternBuilder OrderBy(string field, bool descending = false)
    {
        _orderBy.Add(new SearchSort { Property = field, Descending = descending });
        return this;
    }

    // ── Pattern ─────────────────────────────────────────────────────────────────

    public MatchPatternBuilder Pattern(string pattern)
    {
        _pattern = pattern;
        return this;
    }

    public MatchPatternBuilder Subset(string name, params string[] variables)
    {
        var subset = new PatternSubset { Name = name };
        subset.Variables.AddRange(variables);
        _subsets.Add(subset);
        return this;
    }

    public MatchPatternBuilder Define(string variable, string expr)
    {
        _define.Add(new NamedExpr { Name = variable, Expr = expr });
        return this;
    }

    public MatchPatternBuilder Measure(string name, string expr)
    {
        _measures.Add(new NamedExpr { Name = name, Expr = expr });
        return this;
    }

    // ── Output ──────────────────────────────────────────────────────────────────

    public MatchPatternBuilder RowsPerMatch(RowsPerMatch mode)
    {
        _rowsPerMatch = mode;
        return this;
    }

    /// <summary><paramref name="variable"/> applies to <c>TO_FIRST</c> and <c>TO_LAST</c> only.</summary>
    public MatchPatternBuilder AfterMatch(AfterMatchSkipKind kind, string variable = "")
    {
        _afterMatch = new AfterMatchSkip { Kind = kind, Variable = variable };
        return this;
    }

    public MatchPatternBuilder Limit(int n)
    {
        _limit = n;
        return this;
    }

    // ── Build ───────────────────────────────────────────────────────────────────

    public MatchPatternRequest Build(string? traceId = null)
    {
        var request = new MatchPatternRequest
        {
            TypeName      = _typeName,
            Source        = _source,
            ChunkProperty = _chunkProperty,
            WhereLogic    = _whereLogic,
            Pattern       = _pattern,
            RowsPerMatch  = _rowsPerMatch,
            AfterMatch    = _afterMatch,
            Limit         = _limit,
            TraceId       = traceId ?? string.Empty
        };
        request.Where.AddRange(_where);
        request.PartitionBy.AddRange(_partitionBy);
        request.OrderBy.AddRange(_orderBy);
        request.Subsets.AddRange(_subsets);
        request.Define.AddRange(_define);
        request.Measures.AddRange(_measures);
        return request;
    }

    private MatchPatternBuilder AddClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _where.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }
}
```

`Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs`: insert after line 26 (`public static PipelineBuilder Pipeline<T>() …`), leaving one blank line before and after:
```csharp
    /// <summary>
    /// Entry point for the row pattern matching (MATCH_RECOGNIZE) DSL. String-based like
    /// Pipeline; pattern and expression strings are validated by the server.
    /// </summary>
    public static MatchPatternBuilder MatchPattern(string typeName) => new(typeName);

    /// <summary>Entry point for the row pattern matching DSL, typed on the matched entity.</summary>
    public static MatchPatternBuilder MatchPattern<T>() where T : class => new(typeof(T).Name);
```

`Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`: insert after line 332 (the closing brace of `PipelineAsync<TResult>`), before the `GroupByAsync` doc comment, separated by one blank line on each side. It is `PipelineAsync` line for line: `Build()`, then `request.TraceId = CurrentTraceId()`, then `ResolveHeadersAsync(null)`.
```csharp
    /// <summary>
    /// Executes a row pattern match (MATCH_RECOGNIZE) and streams one result per output row.
    /// The column set depends on the pattern's measures and rows-per-match mode, so each row's
    /// data comes back as a string-keyed dictionary with the server's keys unchanged.
    /// </summary>
    public async IAsyncEnumerable<MatchPatternResult> MatchPatternAsync(
        MatchPatternBuilder pattern,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = pattern.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.MatchPattern {Entity} pattern={Pattern}",
            _descriptor.EntityName, request.Pattern);

        var stream = search.MatchPattern(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return new MatchPatternResult(
                StructConverter.ToDictionary(response.Data), response.MatchNumber, response.Classifier);
    }
```

Append after line 380 (`public sealed record SearchResult<T>(T Entity, float Score);`), after one blank line:
```csharp
/// <summary>
/// One MatchPattern output row: its columns, the 1-based number of the match it belongs to
/// (0 for an unmatched row) and the pattern variable it matched (empty for ONE_ROW and unmatched rows).
/// </summary>
public sealed record MatchPatternResult(IReadOnlyDictionary<string, object?> Data, long MatchNumber, string Classifier);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests --filter "FullyQualifiedName~MatchPatternBuilderTests"`
Expected: PASS (7 tests).

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests --filter "FullyQualifiedName~EntityCoordinatorMatchPatternTests"`
Expected: PASS (3 tests).

Run: `dotnet build Iverson.slnx`
Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`.

Run: `dotnet test Iverson.slnx --filter "Category!=Integration"`
Expected: 11 assemblies pass with 0 failed and 3054 tests in total: Iverson.Api.Tests 1221, Iverson.ClientConformance.Tests 554, Iverson.StarRocks.Tests 430, Iverson.Patterns.Tests 285, Iverson.Client.Search.Tests 125 (118 + 7), Iverson.LoadTest.Tests 113, Iverson.Vector.Tests 112, Iverson.Client.Core.Tests 71 (68 + 3), Iverson.Sql.Tests 62, Iverson.Embeddings.Tests 52, Iverson.Events.Tests 29.

Mutation check (verified while drafting). Delete `_chunkProperty = chunkProperty;` from `Chunks`, and 2 of the 7 builder tests fail: `Build_MatchesGoldenFixture_MatchPatternContract2` and `Chunks_SetsSourceAndChunkProperty`. Restore the line.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Search/MatchPatternBuilder.cs Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs Iverson.Clients/DotNet/Iverson.Client.Search.Tests/MatchPatternBuilderTests.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMatchPatternTests.cs Iverson.Clients/Common/testdata/match-pattern-contract-1.json Iverson.Clients/Common/testdata/match-pattern-contract-2.json
git commit -m "$(cat <<'EOF'
add MatchPattern to the .NET SDK with cross-language golden fixtures

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Task 3: Java SDK — `MatchPatternBuilder`, `Query.matchPattern`, `EntityCoordinator.matchPattern`

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/MatchPatternBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java:56-58` (add `matchPattern` after `pipeline(String)`)
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java:9` (import), `:269-282` (add `matchPattern` after `pipeline(PipelineBuilder)`, before `searchSimilar` at :284), `:370` (add the nested `MatchPatternResult` record after `ChunkSearchResult`)
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/MatchPatternBuilderTest.java` (create)
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java:10` (import), `:159-168` (add a `matchPattern` section after `pipeline_withActingUserToken_usesWithOption`, before `// ── searchSimilar` at :169)

**Interfaces:**
- Consumes: the golden fixtures `Iverson.Clients/Common/testdata/match-pattern-contract-1.json` and `match-pattern-contract-2.json` from Task 2. The generated `iverson.ObjectSearch` types (`MatchPatternRequest`, `MatchPatternResponse`, `PatternRowSource`, `RowsPerMatch`, `AfterMatchSkipKind`, `AfterMatchSkip`, `PatternSubset`, `NamedExpr`) and the blocking stub's `matchPattern(MatchPatternRequest)` → `Iterator<MatchPatternResponse>`, which `protobuf-maven-plugin` generates from `Common/Proto/object_search.proto` at build time (nothing generated is committed).
- Produces: the Java API that Task 8's Java driver uses:
  - `Query.matchPattern(String typeName)` → `MatchPatternBuilder` (package-private ctor). Its methods are `chunks`, `where`, `not`, `withLogic`, `partitionBy(String...)`, `orderBy(String)`, `orderBy(String, boolean)`, `pattern`, `subset(String, String...)`, `define`, `measure`, `rowsPerMatch`, `afterMatch(kind)`, `afterMatch(kind, variable)`, `limit`, `build()` and `build(String traceId)`.
  - `EntityCoordinator.matchPattern(MatchPatternBuilder)` → `List<EntityCoordinator.MatchPatternResult>`.
  - `public record MatchPatternResult(Map<String, Object> data, long matchNumber, String classifier)`, nested in `EntityCoordinator`.

All commands run from the repo root after `export JAVA_HOME=~/sdk/java21 PATH=~/sdk/java21/bin:~/sdk/apache-maven-3.9.9/bin:$PATH`. Maven needs network access to resolve the BOMs.

- [ ] **Step 1: Write the failing tests**

`Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/MatchPatternBuilderTest.java`:
```java
package io.iverson.client.search;

import com.google.protobuf.util.JsonFormat;
import iverson.ObjectSearch.AfterMatchSkipKind;
import iverson.ObjectSearch.MatchPatternRequest;
import iverson.ObjectSearch.PatternRowSource;
import iverson.ObjectSearch.RowsPerMatch;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import org.json.JSONException;
import org.junit.jupiter.api.Test;
import org.skyscreamer.jsonassert.JSONAssert;
import org.skyscreamer.jsonassert.JSONCompareMode;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

import static org.junit.jupiter.api.Assertions.*;

class MatchPatternBuilderTest {

    @Test
    void chunks_setsSourceAndChunkProperty() {
        MatchPatternRequest req = Query.matchPattern("VectorDoc").chunks("Body").build();

        assertEquals(PatternRowSource.CHUNKS, req.getSource());
        assertEquals("Body", req.getChunkProperty());
    }

    @Test
    void afterMatch_isUnsetUnlessCalled() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertFalse(req.hasAfterMatch());
    }

    @Test
    void limit_isUnsetUnlessCalled() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertEquals(0, req.getLimit());
    }

    @Test
    void build_withoutTraceId_givesEmptyTraceId() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertEquals("", req.getTraceId());
    }

    // ── Cross-language golden-fixture contract ───────────────────────────────
    // Golden fixtures checked in at Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json.
    // Every SDK's builder, driven by the same call sequence, must serialize to the same JSON.
    // Do not hand-edit the JSON files.

    @Test
    void build_matchesGoldenFixture_matchPatternContract1() throws IOException, JSONException {
        MatchPatternRequest request = Query.matchPattern("PatternDoc")
            .where("Marker", SearchOperator.EQUALS, "m1")
            .not("Label", SearchOperator.EQUALS, "skip")
            .withLogic(SearchLogic.OR)
            .partitionBy("Label")
            .orderBy("Seq")
            .orderBy("Id", true)
            .pattern("A B+")
            .subset("AB", "A", "B")
            .define("B", "Seq > PREV(Seq)")
            .measure("n", "COUNT(*)")
            .measure("last_seq", "LAST(B.Seq)")
            .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
            .afterMatch(AfterMatchSkipKind.TO_FIRST, "B")
            .limit(100)
            .build("trace-1");

        String actualJson = JsonFormat.printer().print(request);
        String expectedJson = Files.readString(goldenFixturePath("match-pattern-contract-1.json"));

        JSONAssert.assertEquals(expectedJson, actualJson, JSONCompareMode.STRICT);
    }

    @Test
    void build_matchesGoldenFixture_matchPatternContract2() throws IOException, JSONException {
        MatchPatternRequest request = Query.matchPattern("VectorDoc")
            .chunks("Body")
            .pattern("A")
            .define("A", "SIMILARITY(text, 'refund') > 0.5")
            .build("");

        String actualJson = JsonFormat.printer().print(request);
        String expectedJson = Files.readString(goldenFixturePath("match-pattern-contract-2.json"));

        JSONAssert.assertEquals(expectedJson, actualJson, JSONCompareMode.STRICT);
    }

    /** Resolves a shared golden fixture relative to this module's basedir ({@code Iverson.Clients/Java/client}). */
    private static Path goldenFixturePath(String fileName) {
        return Paths.get("..", "..", "Common", "testdata", fileName);
    }
}
```

`Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java`: add the import next to the other search-builder imports:
```java
import io.iverson.client.search.MatchPatternBuilder;
```
Then insert this section after `pipeline_withActingUserToken_usesWithOption` and before `// ── searchSimilar`:
```java
    // ── matchPattern ────────────────────────────────────────────────────────────

    @Test
    void matchPattern_streamsRowsAsResults() {
        ObjectSearch.MatchPatternResponse first = ObjectSearch.MatchPatternResponse.newBuilder()
            .setData(Struct.newBuilder()
                .putFields("Label", Value.newBuilder().setStringValue("pat-java").build())
                .putFields("Seq", Value.newBuilder().setNumberValue(1).build())
                .build())
            .setMatchNumber(1)
            .setClassifier("A")
            .build();
        ObjectSearch.MatchPatternResponse second = ObjectSearch.MatchPatternResponse.newBuilder()
            .setData(Struct.newBuilder()
                .putFields("Seq", Value.newBuilder().setNumberValue(2).build())
                .build())
            .setMatchNumber(2)
            .setClassifier("B")
            .build();
        when(mockStub.matchPattern(any())).thenReturn(List.of(first, second).iterator());

        MatchPatternBuilder builder = Query.matchPattern("CoordinatorTestArticle")
            .orderBy("Seq")
            .pattern("A B+")
            .define("B", "Seq > PREV(Seq)");
        List<EntityCoordinator.MatchPatternResult> results = sut.matchPattern(builder);

        assertEquals(2, results.size());
        assertEquals("pat-java", results.get(0).data().get("Label"));
        assertEquals(1.0, (Double) results.get(0).data().get("Seq"), 0.001);
        assertEquals(1L, results.get(0).matchNumber());
        assertEquals("A", results.get(0).classifier());
        assertEquals(2.0, (Double) results.get(1).data().get("Seq"), 0.001);
        assertEquals(2L, results.get(1).matchNumber());
        assertEquals("B", results.get(1).classifier());

        ArgumentCaptor<ObjectSearch.MatchPatternRequest> sent =
            ArgumentCaptor.forClass(ObjectSearch.MatchPatternRequest.class);
        verify(mockStub).matchPattern(sent.capture());
        assertEquals(builder.build(), sent.getValue());
    }

    @Test
    void matchPattern_withActingUserToken_usesWithOption() {
        when(mockStub.matchPattern(any())).thenReturn(List.<ObjectSearch.MatchPatternResponse>of().iterator());

        MatchPatternBuilder builder = Query.matchPattern("CoordinatorTestArticle").orderBy("Seq").pattern("A");
        sut.withActingUser("user-token-555").matchPattern(builder);

        verify(mockStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "user-token-555");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `mvn -f Iverson.Clients/Java/pom.xml -pl client -am test -Dtest=MatchPatternBuilderTest,EntityCoordinatorTest`
Expected: BUILD FAILURE at `testCompile`, with `cannot find symbol` for `class MatchPatternBuilder` in package `io.iverson.client.search`.

- [ ] **Step 3: Implement the builder, the factory and the coordinator method**

`Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/MatchPatternBuilder.java` (new). It mirrors `PipelineBuilder`: the same `where`/`not`/`withLogic` with `SearchValues.toSearchValue` and `SearchClause` construction, and the same `build()` → `build("")`. It does NOT copy `PipelineBuilder`'s `limit = 10_000` default. `afterMatch` stays unset unless it is called. The builder does no validation.
```java
package io.iverson.client.search;

import iverson.ObjectSearch.AfterMatchSkip;
import iverson.ObjectSearch.AfterMatchSkipKind;
import iverson.ObjectSearch.MatchPatternRequest;
import iverson.ObjectSearch.NamedExpr;
import iverson.ObjectSearch.PatternRowSource;
import iverson.ObjectSearch.PatternSubset;
import iverson.ObjectSearch.RowsPerMatch;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSort;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Fluent builder that compiles to a {@link MatchPatternRequest} (SQL:2016 row pattern
 * recognition). One method per request field; pattern, DEFINE and MEASURES strings pass
 * through unchanged, because the server validates them. Instantiate via
 * {@link Query#matchPattern(String)}.
 *
 * <p>Does not require a live server — {@link #build()} simply returns the compiled proto.</p>
 */
public final class MatchPatternBuilder {

    private final String typeName;
    private final List<SearchClause>  where       = new ArrayList<>();
    private final List<String>        partitionBy = new ArrayList<>();
    private final List<SearchSort>    orderBy     = new ArrayList<>();
    private final List<PatternSubset> subsets     = new ArrayList<>();
    private final List<NamedExpr>     define      = new ArrayList<>();
    private final List<NamedExpr>     measures    = new ArrayList<>();
    private PatternRowSource source        = PatternRowSource.TYPE_ROWS;
    private String           chunkProperty = "";
    private SearchLogic      whereLogic    = SearchLogic.AND;
    private String           pattern       = "";
    private RowsPerMatch     rowsPerMatch  = RowsPerMatch.ONE_ROW;
    private AfterMatchSkip   afterMatch;   // null: unset, so the server default (PAST_LAST_ROW) applies
    private int              limit;        // 0: the server default

    MatchPatternBuilder(String typeName) {
        this.typeName = typeName;
    }

    /** Matches over the chunks of the given {@code @IversonChunk} property instead of the type's rows. */
    public MatchPatternBuilder chunks(String chunkProperty) {
        this.source = PatternRowSource.CHUNKS;
        this.chunkProperty = chunkProperty;
        return this;
    }

    /** Adds a WHERE (FILTER) pre-filter clause. */
    public MatchPatternBuilder where(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.FILTER);
    }

    /** Adds a MUST_NOT pre-filter clause. */
    public MatchPatternBuilder not(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    /** Sets the logic combining the pre-filter clauses. Default: AND. */
    public MatchPatternBuilder withLogic(SearchLogic logic) {
        this.whereLogic = logic;
        return this;
    }

    /** Appends PARTITION BY columns. */
    public MatchPatternBuilder partitionBy(String... fields) {
        partitionBy.addAll(Arrays.asList(fields));
        return this;
    }

    /** Appends an ascending ORDER BY column. */
    public MatchPatternBuilder orderBy(String field) {
        return orderBy(field, false);
    }

    /** Appends an ORDER BY column. */
    public MatchPatternBuilder orderBy(String field, boolean descending) {
        orderBy.add(SearchSort.newBuilder().setProperty(field).setDescending(descending).build());
        return this;
    }

    /** Sets the row pattern, e.g. {@code "A B+"}. */
    public MatchPatternBuilder pattern(String pattern) {
        this.pattern = pattern;
        return this;
    }

    /** Adds a SUBSET: a union variable over the given pattern variables. */
    public MatchPatternBuilder subset(String name, String... variables) {
        subsets.add(PatternSubset.newBuilder().setName(name).addAllVariables(Arrays.asList(variables)).build());
        return this;
    }

    /** Adds a DEFINE: {@code variable AS expr}. */
    public MatchPatternBuilder define(String variable, String expr) {
        define.add(NamedExpr.newBuilder().setName(variable).setExpr(expr).build());
        return this;
    }

    /** Adds a MEASURE: {@code expr AS name}. */
    public MatchPatternBuilder measure(String name, String expr) {
        measures.add(NamedExpr.newBuilder().setName(name).setExpr(expr).build());
        return this;
    }

    /** Sets ONE ROW / ALL ROWS PER MATCH. Default: ONE_ROW. */
    public MatchPatternBuilder rowsPerMatch(RowsPerMatch mode) {
        this.rowsPerMatch = mode;
        return this;
    }

    /** Sets AFTER MATCH SKIP with no variable. Unset unless called. */
    public MatchPatternBuilder afterMatch(AfterMatchSkipKind kind) {
        return afterMatch(kind, "");
    }

    /** Sets AFTER MATCH SKIP; {@code variable} applies to TO_FIRST and TO_LAST. Unset unless called. */
    public MatchPatternBuilder afterMatch(AfterMatchSkipKind kind, String variable) {
        this.afterMatch = AfterMatchSkip.newBuilder().setKind(kind).setVariable(variable).build();
        return this;
    }

    /** Output row limit. Default: unset (0), so the server default applies. */
    public MatchPatternBuilder limit(int n) {
        this.limit = n;
        return this;
    }

    /** Compiles to the {@link MatchPatternRequest} proto. */
    public MatchPatternRequest build() {
        return build("");
    }

    /** Compiles to the {@link MatchPatternRequest} proto with the given trace ID. */
    public MatchPatternRequest build(String traceId) {
        MatchPatternRequest.Builder request = MatchPatternRequest.newBuilder()
            .setTypeName(typeName)
            .setSource(source)
            .setChunkProperty(chunkProperty)
            .addAllWhere(where)
            .setWhereLogic(whereLogic)
            .addAllPartitionBy(partitionBy)
            .addAllOrderBy(orderBy)
            .setPattern(pattern)
            .addAllSubsets(subsets)
            .addAllDefine(define)
            .addAllMeasures(measures)
            .setRowsPerMatch(rowsPerMatch)
            .setLimit(limit)
            .setTraceId(traceId == null ? "" : traceId);
        if (afterMatch != null) request.setAfterMatch(afterMatch);
        return request.build();
    }

    private MatchPatternBuilder addClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }
}
```

`Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java`: insert after `pipeline(String)`:
```java
    /**
     * Creates a {@link MatchPatternBuilder} scoped to the given type name. Row pattern
     * matching (SQL:2016 MATCH_RECOGNIZE) runs server-side over the type's rows or chunks.
     */
    public static MatchPatternBuilder matchPattern(String typeName) {
        return new MatchPatternBuilder(typeName);
    }
```

`Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`: add the import after `import io.iverson.client.search.GroupByBuilder;`:
```java
import io.iverson.client.search.MatchPatternBuilder;
```
Insert this method after `pipeline(PipelineBuilder)` and before `searchSimilar`. It drains the stream the same way `pipeline` does: `stubFor(null)` and `StructConverter.fromStructAsMap`, with no trace id set.
```java
    /**
     * Executes a row pattern match (MATCH_RECOGNIZE) and returns every output row. Columns
     * depend on the request's rows-per-match mode and measures, so each row's data comes back
     * as a string-keyed map, same as {@link #pipeline(PipelineBuilder)}.
     */
    public List<MatchPatternResult> matchPattern(MatchPatternBuilder builder) throws StatusRuntimeException {
        ObjectSearch.MatchPatternRequest request = builder.build();
        Iterator<ObjectSearch.MatchPatternResponse> stream = stubFor(null).matchPattern(request);
        List<MatchPatternResult> results = new ArrayList<>();
        while (stream.hasNext()) {
            ObjectSearch.MatchPatternResponse response = stream.next();
            results.add(new MatchPatternResult(
                StructConverter.fromStructAsMap(response.getData()),
                response.getMatchNumber(),
                response.getClassifier()));
        }
        return results;
    }
```
Add the record after `ChunkSearchResult`, before the class's closing brace:
```java
    /**
     * One row pattern match output row: its columns (raw keys, no casing transform), its match
     * number (0 for an unmatched row) and its classifier (the pattern variable the row matched;
     * empty for ONE_ROW and for unmatched rows).
     */
    public record MatchPatternResult(Map<String, Object> data, long matchNumber, String classifier) {}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `mvn -f Iverson.Clients/Java/pom.xml -pl client -am test -Dtest=MatchPatternBuilderTest,EntityCoordinatorTest`
Expected: BUILD SUCCESS, `Tests run: 25, Failures: 0`. That is `MatchPatternBuilderTest` 6 and `EntityCoordinatorTest` 19 (17 existing + 2 new).

Run: `mvn -f Iverson.Clients/Java/pom.xml -pl client -am test`
Expected: BUILD SUCCESS, `Tests run: 220, Failures: 0, Errors: 0, Skipped: 0`.

Mutation check (verified while drafting; restore the files afterwards). In `MatchPatternBuilder`, initialise `afterMatch = AfterMatchSkip.getDefaultInstance()`, and in `matchPattern` pass `response.getTraceId()` instead of `response.getClassifier()`. The same `-Dtest=` run then fails 3 tests:
- `afterMatch_isUnsetUnlessCalled` (expected false, was true);
- `build_matchesGoldenFixture_matchPatternContract2` (an extra `afterMatch: {}`);
- `matchPattern_streamsRowsAsResults` (expected `A`, was empty).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/MatchPatternBuilder.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/MatchPatternBuilderTest.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java
git commit -m "add matchPattern to the java sdk

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Python SDK — `match_pattern` builder, `EntityCoordinator.match_pattern` and `MatchPatternResult`

**Files:**
- Create: `Iverson.Clients/Python/iverson_client/match_pattern.py`
- Modify: `Iverson.Clients/Python/iverson_client/core.py:582-591` (add the `MatchPatternResult` dataclass after `SearchResult`) and `:801-805` (add `match_pattern` after `pipeline`)
- Modify: `Iverson.Clients/Python/iverson_client/__init__.py:24-26` (imports) and `:53`, `:59` (`__all__`)
- Test: `Iverson.Clients/Python/tests/test_match_pattern.py` (create)
- Test: `Iverson.Clients/Python/tests/test_entity_coordinator.py:1-3` (docstring), `:20` (import), `:122-130` (add a test after `test_pipeline_converts_rows_via_struct_to_dict`)

**Interfaces:**
- Consumes: Task 1's regenerated `iverson_client/generated/object_search_pb2.py` / `object_search_pb2_grpc.py` (`MatchPatternRequest`, `MatchPatternResponse`, `PatternSubset`, `NamedExpr`, `AfterMatchSkip`, the enum values `TYPE_ROWS`/`CHUNKS`, `ONE_ROW`/`ALL_ROWS_SHOW_EMPTY`/…, `PAST_LAST_ROW`/`TO_FIRST`/…, and the stub method `MatchPattern`); Task 2's fixtures `Iverson.Clients/Common/testdata/match-pattern-contract-1.json` and `match-pattern-contract-2.json`.
- Produces: `MatchPatternBuilder`, `match_pattern(type_name)`, `EntityCoordinator.match_pattern(request) -> List[MatchPatternResult]` and `MatchPatternResult(data, match_number, classifier)`, all exported from `iverson_client`; consumed by Task 8's Python driver.

The builder mirrors `PipelineBuilder`'s base step: the same `_to_search_value` helper, the same `SearchClause` construction (`not_` = `MUST_NOT`), and `build(trace_id: str = "")`. Unlike `PipelineBuilder` its limit defaults to `0` (server default), and `after_match` stays unset unless `after_match(...)` is called. There is no client-side validation: pattern and expression strings pass through unchanged. The coordinator mirrors `pipeline`: one call on `self._search` with `metadata=self._acting_user_metadata()`, and each row's `data` goes through `_struct_to_dict`.

- [ ] **Step 1: Write the failing tests**

`Iverson.Clients/Python/tests/test_match_pattern.py`:
```python
import json
from pathlib import Path

from google.protobuf.json_format import MessageToJson

import iverson_client
from iverson_client import MatchPatternBuilder, match_pattern
from iverson_client.generated import object_search_pb2 as pb

# Shared cross-language golden fixtures, checked in at Iverson.Clients/Common/testdata/.
# Every language's builder must produce the same structural JSON for the same logical
# request. Do not hand-edit the JSON files.
_TESTDATA = Path(__file__).resolve().parents[2] / "Common" / "testdata"


def test_build_matches_golden_fixture_match_pattern_contract_1():
    request = (
        match_pattern("PatternDoc")
        .where("Marker", pb.EQUALS, "m1")
        .not_("Label", pb.EQUALS, "skip")
        .with_logic(pb.OR)
        .partition_by("Label")
        .order_by("Seq")
        .order_by("Id", descending=True)
        .pattern("A B+")
        .subset("AB", "A", "B")
        .define("B", "Seq > PREV(Seq)")
        .measure("n", "COUNT(*)")
        .measure("last_seq", "LAST(B.Seq)")
        .rows_per_match(pb.ALL_ROWS_SHOW_EMPTY)
        .after_match(pb.TO_FIRST, "B")
        .limit(100)
        .build("trace-1")
    )

    actual = json.loads(MessageToJson(request))
    expected = json.loads((_TESTDATA / "match-pattern-contract-1.json").read_text())

    assert actual == expected


def test_build_matches_golden_fixture_match_pattern_contract_2():
    request = (
        match_pattern("VectorDoc")
        .chunks("Body")
        .pattern("A")
        .define("A", "SIMILARITY(text, 'refund') > 0.5")
        .build("")
    )

    actual = json.loads(MessageToJson(request))
    expected = json.loads((_TESTDATA / "match-pattern-contract-2.json").read_text())

    assert actual == expected


def test_chunks_sets_the_source_and_the_chunk_property():
    request = match_pattern("VectorDoc").chunks("Body").build()

    assert request.source == pb.CHUNKS
    assert request.chunk_property == "Body"


def test_after_match_stays_unset_unless_called():
    request = match_pattern("PatternDoc").pattern("A").build()

    assert not request.HasField("after_match")


def test_trace_id_defaults_to_empty():
    request = match_pattern("PatternDoc").build()

    assert request.trace_id == ""


def test_match_pattern_names_are_exported():
    assert isinstance(match_pattern("PatternDoc"), MatchPatternBuilder)
    assert {"MatchPatternBuilder", "match_pattern", "MatchPatternResult"} <= set(iverson_client.__all__)
```

In `Iverson.Clients/Python/tests/test_entity_coordinator.py`, name the new method in the module docstring (line 2):
```python
group_by, aggregate, search_chunks, pipeline, match_pattern). No existing EntityCoordinator execution
```
Import the result type (line 20):
```python
from iverson_client.core import EntityCoordinator, MatchPatternResult, _entity_to_struct
```
Then add this test to `TestEntityCoordinatorSearchFamily`, directly after `test_pipeline_converts_rows_via_struct_to_dict`:
```python
    def test_match_pattern_pairs_each_row_with_its_match_number_and_classifier(self):
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token")
        coordinator._search = MagicMock()
        first = struct_pb2.Struct()
        first.fields["Label"].string_value = "pat-python"
        first.fields["Seq"].number_value = 1
        second = struct_pb2.Struct()
        second.fields["Label"].string_value = "pat-python"
        second.fields["Seq"].number_value = 2
        coordinator._search.MatchPattern.return_value = iter([
            pb.MatchPatternResponse(data=first, match_number=1, classifier="A"),
            pb.MatchPatternResponse(data=second, match_number=1, classifier="B"),
        ])
        request = pb.MatchPatternRequest(type_name="CoordArticle", pattern="A B+")

        results = coordinator.match_pattern(request)

        assert results == [
            MatchPatternResult({"Label": "pat-python", "Seq": 1.0}, 1, "A"),
            MatchPatternResult({"Label": "pat-python", "Seq": 2.0}, 1, "B"),
        ]
        coordinator._search.MatchPattern.assert_called_once_with(
            request, metadata=(("x-acting-user-authorization", "Bearer ambient-token"),))
```

- [ ] **Step 2: Run the tests to verify they fail**

Run (from `Iverson.Clients/Python`): `python3 -m pytest -q tests/test_match_pattern.py tests/test_entity_coordinator.py`
Expected: FAIL, `2 errors during collection`: `ImportError: cannot import name 'MatchPatternBuilder' from 'iverson_client'` and `ImportError: cannot import name 'MatchPatternResult' from 'iverson_client.core'`.

- [ ] **Step 3: Implement the builder, the result type, the coordinator method and the exports**

`Iverson.Clients/Python/iverson_client/match_pattern.py`:
```python
"""
Fluent row-pattern-matching builder that compiles to a ``MatchPatternRequest`` proto.

Pattern and expression strings pass through unchanged; the server validates them.
``build()`` never needs a live server.

Usage:
    request = (
        match_pattern("Order")
        .where("Region", pb.EQUALS, "EU")
        .partition_by("CustomerId")
        .order_by("PlacedAt")
        .pattern("A B+")
        .define("B", "Total > PREV(Total)")
        .measure("n", "COUNT(*)")
        .build()
    )
"""
from __future__ import annotations

from typing import Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value


class MatchPatternBuilder:
    """Fluent DSL builder that compiles to a ``MatchPatternRequest`` proto message.
    Instantiate via the module-level ``match_pattern(type_name)`` factory."""

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._source = _pb.TYPE_ROWS
        self._chunk_property = ""
        self._where: list[_pb.SearchClause] = []
        self._where_logic = _pb.AND
        self._partition_by: list[str] = []
        self._order_by: list[_pb.SearchSort] = []
        self._pattern = ""
        self._subsets: list[_pb.PatternSubset] = []
        self._define: list[_pb.NamedExpr] = []
        self._measures: list[_pb.NamedExpr] = []
        self._rows_per_match = _pb.ONE_ROW
        self._after_match: Optional[_pb.AfterMatchSkip] = None
        self._limit = 0

    def chunks(self, chunk_property: str) -> "MatchPatternBuilder":
        """Match over the chunks of an ``[IversonChunk]`` property instead of the type's rows."""
        self._source = _pb.CHUNKS
        self._chunk_property = chunk_property
        return self

    def where(self, field: str, op: int, value: object) -> "MatchPatternBuilder":
        return self._add_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "MatchPatternBuilder":
        return self._add_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "MatchPatternBuilder":
        self._where_logic = logic
        return self

    def partition_by(self, *fields: str) -> "MatchPatternBuilder":
        self._partition_by.extend(fields)
        return self

    def order_by(self, field: str, descending: bool = False) -> "MatchPatternBuilder":
        self._order_by.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def pattern(self, pattern: str) -> "MatchPatternBuilder":
        self._pattern = pattern
        return self

    def subset(self, name: str, *variables: str) -> "MatchPatternBuilder":
        self._subsets.append(_pb.PatternSubset(name=name, variables=variables))
        return self

    def define(self, variable: str, expr: str) -> "MatchPatternBuilder":
        self._define.append(_pb.NamedExpr(name=variable, expr=expr))
        return self

    def measure(self, name: str, expr: str) -> "MatchPatternBuilder":
        self._measures.append(_pb.NamedExpr(name=name, expr=expr))
        return self

    def rows_per_match(self, mode: int) -> "MatchPatternBuilder":
        self._rows_per_match = mode
        return self

    def after_match(self, kind: int, variable: str = "") -> "MatchPatternBuilder":
        self._after_match = _pb.AfterMatchSkip(kind=kind, variable=variable)
        return self

    def limit(self, n: int) -> "MatchPatternBuilder":
        self._limit = n
        return self

    def build(self, trace_id: str = "") -> _pb.MatchPatternRequest:
        return _pb.MatchPatternRequest(
            type_name=self._type_name,
            source=self._source,
            chunk_property=self._chunk_property,
            where=self._where,
            where_logic=self._where_logic,
            partition_by=self._partition_by,
            order_by=self._order_by,
            pattern=self._pattern,
            subsets=self._subsets,
            define=self._define,
            measures=self._measures,
            rows_per_match=self._rows_per_match,
            after_match=self._after_match,
            limit=self._limit,
            trace_id=trace_id)

    def _add_clause(self, field: str, op: int, value: object,
                    clause_type: int) -> "MatchPatternBuilder":
        self._where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self


def match_pattern(type_name: str) -> MatchPatternBuilder:
    """Start a fluent row-pattern match for the given entity type."""
    return MatchPatternBuilder(type_name)
```

In `Iverson.Clients/Python/iverson_client/core.py`, add the result dataclass directly after `SearchResult` (after line 591, before the `# ── EntityCoordinator` banner):
```python
@dataclass(frozen=True)
class MatchPatternResult:
    """One MatchPattern output row: its columns (partition keys, measures and, for the
    ALL_ROWS modes, the row's own columns) with the match it belongs to and the pattern
    variable it was classified as (empty for ONE_ROW and for unmatched rows).

    Mirrors the DotNet ``MatchPatternResult`` record, Go ``MatchPatternResult`` struct,
    and Java ``MatchPatternResult`` record.
    """

    data: dict
    match_number: int
    classifier: str
```

Add `match_pattern` directly after `EntityCoordinator.pipeline` (after line 805):
```python
    def match_pattern(self, request: search_pb.MatchPatternRequest) -> List[MatchPatternResult]:
        """Execute a MatchPattern request. Rows carry partition keys and measures (and,
        for the ALL_ROWS modes, the row's own columns), so they are not ``T``-shaped:
        each row's data is converted via ``_struct_to_dict`` and paired with its match
        number and classifier in a ``MatchPatternResult``."""
        return [
            MatchPatternResult(_struct_to_dict(row.data), row.match_number, row.classifier)
            for row in self._search.MatchPattern(request, metadata=self._acting_user_metadata())
        ]
```

In `Iverson.Clients/Python/iverson_client/__init__.py`, replace the `core` import (line 24) and add the builder import after the `group_by` import (line 25):
```python
from iverson_client.core import IversonClient, EntityCoordinator, MatchPatternResult, SchemaRegistrar, SearchResult
from iverson_client.group_by import GroupByBuilder
from iverson_client.match_pattern import MatchPatternBuilder, match_pattern
```
In `__all__`, add `"MatchPatternResult",` after `"SearchResult",` (line 53), and add `"MatchPatternBuilder",` and `"match_pattern",` after `"pipeline",` (line 59):
```python
    "SearchResult",
    "MatchPatternResult",
```
```python
    "pipeline",
    "MatchPatternBuilder",
    "match_pattern",
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `Iverson.Clients/Python`): `python3 -m pytest -q tests/test_match_pattern.py tests/test_entity_coordinator.py`
Expected: PASS (34 passed: 6 in `test_match_pattern.py`, 28 in `test_entity_coordinator.py`).

Run the whole suite (from `Iverson.Clients/Python`): `python3 -m pytest -q`
Expected: PASS (225 passed; 218 after Task 1, plus these 7).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/match_pattern.py Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/iverson_client/__init__.py Iverson.Clients/Python/tests/test_match_pattern.py Iverson.Clients/Python/tests/test_entity_coordinator.py
git commit -m "add the python match_pattern builder and coordinator method" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: TypeScript SDK — `matchPattern` builder, `IversonClient.matchPattern` and `MatchPatternResult`

**Files:**
- Create: `Iverson.Clients/TypeScript/src/match-pattern.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts:40-51` (generated imports), `:168-172` (add `MatchPatternResult` after `SearchResult`), `:945-953` (add `matchPattern` after `pipeline`) and `:955-974` (generalise `_collectSearchStream` over the response type)
- Modify: `Iverson.Clients/TypeScript/src/index.ts:44` (type export) and `:52` (add the builder export after the pipeline export)
- Test: `Iverson.Clients/TypeScript/tests/match-pattern.test.ts` (create)
- Test: `Iverson.Clients/TypeScript/tests/core.test.ts:4` (header comment), `:31` (type import), `:48-59` (generated imports), `:394-408` (add a test after the `pipeline()` test)

**Interfaces:**
- Consumes: Task 1's regenerated `Iverson.Clients/TypeScript/generated/object_search.ts` (ts-proto 2.12.4: `MatchPatternRequest` with its `toJSON`, `MatchPatternResponse`, `PatternSubset`, `NamedExpr`, `AfterMatchSkip`, the enums `PatternRowSource`, `RowsPerMatch`, `AfterMatchSkipKind`, and `ObjectSearchServiceClient.matchPattern`); Task 2's fixtures `Iverson.Clients/Common/testdata/match-pattern-contract-1.json` and `match-pattern-contract-2.json`.
- Produces: `MatchPatternBuilder`, `matchPattern(typeName)`, `IversonClient.matchPattern(request): Promise<MatchPatternResult[]>` and the `MatchPatternResult` interface, all exported from `src/index.ts` (the interface as `export type`); consumed by Task 8's TypeScript driver.

The builder mirrors `PipelineBuilder`'s base step: the same `toSearchValue` helper, the same `SearchClause` construction (`not` = `MUST_NOT`), and `build(traceId = '')` returning an object literal with copied arrays. Unlike `PipelineBuilder` its limit defaults to `0` (server default), and `afterMatch` stays `undefined` unless `afterMatch(...)` is called. There is no client-side validation. `matchPattern` goes through `_collectSearchStream` like `pipeline`. That helper was typed to `ClientReadableStream<SearchResponse>`, so it gains a third type parameter `Res = SearchResponse`; the default keeps every existing caller unchanged.

- [ ] **Step 1: Write the failing tests**

`Iverson.Clients/TypeScript/tests/match-pattern.test.ts`:
```typescript
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import * as client from '../src/index.js';
import { MatchPatternBuilder, matchPattern } from '../src/match-pattern.js';
import {
    AfterMatchSkipKind,
    MatchPatternRequest,
    PatternRowSource,
    RowsPerMatch,
    SearchLogic,
    SearchOperator,
} from '../generated/object_search.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Shared cross-language golden fixtures, checked in at Iverson.Clients/Common/testdata/.
// Every language's builder must produce the same structural JSON for the same logical
// request. Do not hand-edit the JSON files.
const TESTDATA = join(__dirname, '..', '..', 'Common', 'testdata');

describe('MatchPatternBuilder', () => {
    it('build() matches the golden fixture match-pattern-contract-1.json', () => {
        const request = matchPattern('PatternDoc')
            .where('Marker', SearchOperator.EQUALS, 'm1')
            .not('Label', SearchOperator.EQUALS, 'skip')
            .withLogic(SearchLogic.OR)
            .partitionBy('Label')
            .orderBy('Seq')
            .orderBy('Id', true)
            .pattern('A B+')
            .subset('AB', 'A', 'B')
            .define('B', 'Seq > PREV(Seq)')
            .measure('n', 'COUNT(*)')
            .measure('last_seq', 'LAST(B.Seq)')
            .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
            .afterMatch(AfterMatchSkipKind.TO_FIRST, 'B')
            .limit(100)
            .build('trace-1');

        const actual = MatchPatternRequest.toJSON(request);
        const expected = JSON.parse(readFileSync(join(TESTDATA, 'match-pattern-contract-1.json'), 'utf-8'));

        expect(actual).toEqual(expected);
    });

    it('build() matches the golden fixture match-pattern-contract-2.json', () => {
        const request = matchPattern('VectorDoc')
            .chunks('Body')
            .pattern('A')
            .define('A', "SIMILARITY(text, 'refund') > 0.5")
            .build('');

        const actual = MatchPatternRequest.toJSON(request);
        const expected = JSON.parse(readFileSync(join(TESTDATA, 'match-pattern-contract-2.json'), 'utf-8'));

        expect(actual).toEqual(expected);
    });

    it('chunks() sets the source and the chunk property', () => {
        const request = matchPattern('VectorDoc').chunks('Body').build();

        expect(request.source).toBe(PatternRowSource.CHUNKS);
        expect(request.chunkProperty).toBe('Body');
    });

    it('afterMatch stays unset unless afterMatch() is called', () => {
        const request = matchPattern('PatternDoc').pattern('A').build();

        expect(request.afterMatch).toBeUndefined();
    });

    it('the trace id defaults to an empty string', () => {
        const request = matchPattern('PatternDoc').build();

        expect(request.traceId).toBe('');
    });

    it('the builder and its factory are exported from the package index', () => {
        expect(client.matchPattern).toBe(matchPattern);
        expect(client.MatchPatternBuilder).toBe(MatchPatternBuilder);
    });
});
```

In `Iverson.Clients/TypeScript/tests/core.test.ts`, update the header comment (line 4):
```typescript
 *  - the 7 search-family execution methods (search/searchSimilar/searchChunks/groupBy/aggregate/pipeline/matchPattern)
```
Add a type import after the `../src/core.js` import (line 31):
```typescript
import type { MatchPatternResult } from '../src/index.js';
```
Extend the `../generated/object_search.js` import (lines 48-59) so it reads:
```typescript
import {
    AggregateRequest,
    AggregateResponse,
    ChunkSearchResponse,
    GroupByRequest,
    MatchPatternRequest,
    MatchPatternResponse,
    PatternRowSource,
    PipelineRequest,
    RowsPerMatch,
    SearchChunksRequest,
    SearchLogic,
    SearchRequest,
    SearchResponse,
    SearchSimilarRequest,
} from '../generated/object_search.js';
```
Then add this test to `describe('IversonClient — search-family execution methods', ...)`, directly after the `pipeline()` test:
```typescript
    it('matchPattern() pairs each row with its match number and classifier', async () => {
        const rows: MatchPatternResponse[] = [
            { data: { Label: 'pat-typescript', Seq: 1 }, matchNumber: 1, classifier: 'A', traceId: '' },
            { data: { Label: 'pat-typescript', Seq: 2 }, matchNumber: 1, classifier: 'B', traceId: '' },
        ];
        const { fn, calls } = makeStreamStub<MatchPatternRequest, MatchPatternResponse>(rows);
        const client = new IversonClient('localhost', 0, false);
        (client as unknown as { _searchClient: unknown })._searchClient = { matchPattern: fn, close: vi.fn() };
        (client as unknown as { _actingUserToken: unknown })._actingUserToken = 'tok-mp';

        const req: MatchPatternRequest = {
            typeName: 'SearchArticle', source: PatternRowSource.TYPE_ROWS, chunkProperty: '', where: [], whereLogic: SearchLogic.AND,
            partitionBy: [], orderBy: [{ property: 'Seq', descending: false }], pattern: 'A B+', subsets: [],
            define: [], measures: [], rowsPerMatch: RowsPerMatch.ALL_ROWS_SHOW_EMPTY, afterMatch: undefined,
            limit: 0, traceId: '',
        };
        const results = await client.matchPattern(req);

        const expected: MatchPatternResult[] = [
            { data: { Label: 'pat-typescript', Seq: 1 }, matchNumber: 1, classifier: 'A' },
            { data: { Label: 'pat-typescript', Seq: 2 }, matchNumber: 1, classifier: 'B' },
        ];
        expect(results).toEqual(expected);
        expect(calls[0].req).toBe(req);
        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok-mp']);

        client.close();
    });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run (from `Iverson.Clients/TypeScript`): `npx vitest run tests/match-pattern.test.ts tests/core.test.ts`
Expected: FAIL. `tests/match-pattern.test.ts` fails to load (`Cannot find module '../src/match-pattern.js'`), and in `tests/core.test.ts` the new test fails with `TypeError: client.matchPattern is not a function` (`Tests  1 failed | 46 passed (47)`).

- [ ] **Step 3: Implement the builder, the result type, the client method and the exports**

`Iverson.Clients/TypeScript/src/match-pattern.ts`:
```typescript
/**
 * Fluent row-pattern-matching builder that compiles to a MatchPatternRequest proto.
 *
 * Pattern and expression strings pass through unchanged; the server validates them.
 * `build()` never needs a live server.
 */

import {
    AfterMatchSkip,
    AfterMatchSkipKind,
    MatchPatternRequest,
    NamedExpr,
    PatternRowSource,
    PatternSubset,
    RowsPerMatch,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSort,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

/** Fluent DSL builder that compiles to a MatchPatternRequest proto. */
export class MatchPatternBuilder {
    private readonly _typeName: string;
    private _source: PatternRowSource = PatternRowSource.TYPE_ROWS;
    private _chunkProperty = '';
    private readonly _where: SearchClause[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private readonly _partitionBy: string[] = [];
    private readonly _orderBy: SearchSort[] = [];
    private _pattern = '';
    private readonly _subsets: PatternSubset[] = [];
    private readonly _define: NamedExpr[] = [];
    private readonly _measures: NamedExpr[] = [];
    private _rowsPerMatch: RowsPerMatch = RowsPerMatch.ONE_ROW;
    private _afterMatch: AfterMatchSkip | undefined = undefined;
    private _limit = 0;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    /** Match over the chunks of an `@IversonChunk` property instead of the type's rows. */
    chunks(chunkProperty: string): this {
        this._source = PatternRowSource.CHUNKS;
        this._chunkProperty = chunkProperty;
        return this;
    }

    where(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.FILTER);
    }

    not(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    withLogic(logic: SearchLogic): this {
        this._whereLogic = logic;
        return this;
    }

    partitionBy(...fields: string[]): this {
        this._partitionBy.push(...fields);
        return this;
    }

    orderBy(field: string, descending = false): this {
        this._orderBy.push({ property: field, descending });
        return this;
    }

    pattern(pattern: string): this {
        this._pattern = pattern;
        return this;
    }

    subset(name: string, ...variables: string[]): this {
        this._subsets.push({ name, variables });
        return this;
    }

    define(variable: string, expr: string): this {
        this._define.push({ name: variable, expr });
        return this;
    }

    measure(name: string, expr: string): this {
        this._measures.push({ name, expr });
        return this;
    }

    rowsPerMatch(mode: RowsPerMatch): this {
        this._rowsPerMatch = mode;
        return this;
    }

    afterMatch(kind: AfterMatchSkipKind, variable = ''): this {
        this._afterMatch = { kind, variable };
        return this;
    }

    limit(n: number): this {
        this._limit = n;
        return this;
    }

    build(traceId = ''): MatchPatternRequest {
        return {
            typeName: this._typeName,
            source: this._source,
            chunkProperty: this._chunkProperty,
            where: [...this._where],
            whereLogic: this._whereLogic,
            partitionBy: [...this._partitionBy],
            orderBy: [...this._orderBy],
            pattern: this._pattern,
            subsets: [...this._subsets],
            define: [...this._define],
            measures: [...this._measures],
            rowsPerMatch: this._rowsPerMatch,
            afterMatch: this._afterMatch,
            limit: this._limit,
            traceId,
        };
    }

    private _addClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._where.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }
}

/** Start a fluent row-pattern match for the given entity type. */
export function matchPattern(typeName: string): MatchPatternBuilder {
    return new MatchPatternBuilder(typeName);
}
```

In `Iverson.Clients/TypeScript/src/core.ts`, extend the `../generated/object_search.js` import (lines 40-51) so it reads:
```typescript
import {
    AggregateRequest,
    AggregateResponse,
    ChunkSearchResponse,
    GroupByRequest,
    MatchPatternRequest,
    MatchPatternResponse,
    ObjectSearchServiceClient,
    PipelineRequest,
    SearchChunksRequest,
    SearchRequest,
    SearchResponse,
    SearchSimilarRequest,
} from '../generated/object_search.js';
```
Add the result interface directly after `SearchResult<T>` (after line 172):
```typescript
/**
 * A single MatchPattern result row: its columns (partition keys, measures and, for the ALL_ROWS
 * modes, the row's own columns) with the match it belongs to and the pattern variable it was
 * classified as (empty for ONE_ROW and for unmatched rows).
 */
export interface MatchPatternResult {
    data: Record<string, unknown>;
    matchNumber: number;
    classifier: string;
}
```
Add `matchPattern` directly after `IversonClient.pipeline` (after line 953):
```typescript
    /** Execute a MatchPattern request. Rows carry partition keys and measures (and, for the ALL_ROWS
     * modes, the row's own columns), so they are not entity-shaped: each row's data is returned as a
     * plain record, paired with its match number and classifier. */
    async matchPattern(request: MatchPatternRequest): Promise<MatchPatternResult[]> {
        return this._collectSearchStream<MatchPatternRequest, MatchPatternResult, MatchPatternResponse>(
            (req, metadata, options) => this._searchClient.matchPattern(req, metadata, options),
            request,
            (row) => ({
                data: (row.data ?? {}) as Record<string, unknown>,
                matchNumber: row.matchNumber,
                classifier: row.classifier,
            }),
        );
    }
```
Replace `_collectSearchStream`'s doc comment and signature (lines 955-971) so the method reads:
```typescript
    /**
     * Shared streaming path for the search-family RPCs (Search/SearchSimilar/GroupBy/Pipeline, all
     * of which respond with SearchResponse, and MatchPattern, which responds with
     * MatchPatternResponse): opens the stream with the acting-user token resolved into metadata,
     * then applies the caller-supplied `map` to each response row. Search/SearchSimilar map to a
     * `SearchResult<T>` (entity converted via payloadToEntity, plus the row's score); GroupBy/
     * Pipeline map to a plain record, since aggregated/derived columns don't correspond to any entity's
     * own fields or carry a meaningful per-row score; MatchPattern maps to a `MatchPatternResult`.
     */
    private async _collectSearchStream<Req, T, Res = SearchResponse>(
        method: (
            req: Req,
            metadata: grpc.Metadata,
            options: Partial<grpc.CallOptions>,
        ) => grpc.ClientReadableStream<Res>,
        request: Req,
        map: (row: Res) => T,
    ): Promise<T[]> {
        const stream = await openStream(method, request, this._callCredentials, this._actingUserToken);
        return collectStream(stream, map);
    }
```

In `Iverson.Clients/TypeScript/src/index.ts`, replace the type export (line 44):
```typescript
export type { SearchResult, MatchPatternResult } from './core.js';
```
and add the builder export after the pipeline export (line 52), keeping a blank line on each side as the neighbouring exports do:
```typescript
export { MatchPatternBuilder, matchPattern } from './match-pattern.js';
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `Iverson.Clients/TypeScript`): `npx vitest run tests/match-pattern.test.ts tests/core.test.ts`
Expected: PASS (`Test Files  2 passed (2)`, `Tests  53 passed (53)`: 6 in `match-pattern.test.ts`, 47 in `core.test.ts`).

Run the whole suite, typecheck included (from `Iverson.Clients/TypeScript`): `npm test`
Expected: `tsc -p tsconfig.test.json` clean, then `Test Files  10 passed (10)`, `Tests  262 passed (262)` (255 after Task 1, plus these 7).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/src/match-pattern.ts Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/src/index.ts Iverson.Clients/TypeScript/tests/match-pattern.test.ts Iverson.Clients/TypeScript/tests/core.test.ts
git commit -m "add the typescript matchPattern builder and client method" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: Go SDK — `MatchPatternBuilder` and `EntityCoordinator[T].MatchPattern`

**Files:**
- Create: `Iverson.Clients/Go/iverson/match_pattern.go`
- Modify: `Iverson.Clients/Go/iverson/coordinator.go:46-53` (`SearchClient` gains `MatchPattern`), `:62-64` (add `MatchPatternStream` after `ChunkSearchStream`), `:356-359` (add `MatchPatternResult` after `SearchResult[T]`), `:458-477` (add `EntityCoordinator[T].MatchPattern` after `Pipeline`), `:828-830` (`structToMap` doc comment), `:921-923` (add `searchAdapter.MatchPattern` after `searchAdapter.Pipeline`)
- Modify: `Iverson.Clients/Go/iverson/coordinator_test.go:656-676` (`mockMatchPatternStream` before `mockSearchClient`; new mock fields), `:718-724` (add `mockSearchClient.MatchPattern` after `mockSearchClient.Pipeline`), `:936` (MatchPattern tests before the Aggregate section)
- Test: `Iverson.Clients/Go/iverson_test/match_pattern_test.go`

**Interfaces:**
- Consumes: Task 1's regenerated Go code (`generated/object_search.pb.go`, `generated/object_search_grpc.pb.go`: `pb.MatchPatternRequest`, `pb.MatchPatternResponse`, `pb.PatternSubset`, `pb.NamedExpr`, `pb.AfterMatchSkip`, the `pb.PatternRowSource`/`pb.RowsPerMatch`/`pb.AfterMatchSkipKind` enums, and the client's `MatchPattern(ctx, in, opts...) (grpc.ServerStreamingClient[MatchPatternResponse], error)`); Task 2's fixtures `Iverson.Clients/Common/testdata/match-pattern-contract-1.json` and `match-pattern-contract-2.json`.
- Produces: `iverson.NewMatchPattern(typeName) *MatchPatternBuilder` (`Chunks`, `Where`, `Not`, `WithLogic`, `PartitionBy`, `OrderBy`, `Pattern`, `Subset`, `Define`, `Measure`, `RowsPerMatch`, `AfterMatch`, `Limit`, `Build(traceId ...string) *pb.MatchPatternRequest`), `iverson.MatchPatternResult`, `iverson.MatchPatternStream`, `SearchClient.MatchPattern` and `EntityCoordinator[T].MatchPattern(ctx, req) ([]MatchPatternResult, error)`, consumed by Task 8's Go driver.

- [ ] **Step 1: Write the failing tests**

`Iverson.Clients/Go/iverson_test/match_pattern_test.go` (`strVal` is the existing helper in `iverson_test/group_by_test.go`):
```go
package iverson_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
	"google.golang.org/protobuf/encoding/protojson"
)

// assertMatchesMatchPatternFixture compares the request's protojson against a checked-in
// golden fixture as parsed JSON, the same way TestPipelineBuild_MatchesGoldenFixture_Contract1 does.
func assertMatchesMatchPatternFixture(t *testing.T, req *pb.MatchPatternRequest, fixture string) {
	t.Helper()
	actualBytes, err := protojson.Marshal(req)
	if err != nil {
		t.Fatalf("protojson.Marshal: %v", err)
	}
	var actual map[string]interface{}
	if err := json.Unmarshal(actualBytes, &actual); err != nil {
		t.Fatalf("unmarshal actual: %v", err)
	}

	goldenBytes, err := os.ReadFile(filepath.Join("..", "..", "Common", "testdata", fixture))
	if err != nil {
		t.Fatalf("read golden fixture: %v", err)
	}
	var expected map[string]interface{}
	if err := json.Unmarshal(goldenBytes, &expected); err != nil {
		t.Fatalf("unmarshal golden fixture: %v", err)
	}

	if !reflect.DeepEqual(actual, expected) {
		t.Errorf("golden fixture mismatch:\n  actual:   %s\n  expected: %s", actualBytes, goldenBytes)
	}
}

// ── Cross-language golden-fixture contract ─────────────────────────────────
// Fixtures generated from the C# builder (the reference implementation), checked in at
// Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json. The same call sequence,
// built here via Go's iverson.NewMatchPattern(...), must serialize to the same JSON structure.

func TestMatchPatternBuild_MatchesGoldenFixture_Contract1(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").
		Where("Marker", pb.SearchOperator_EQUALS, strVal("m1")).
		Not("Label", pb.SearchOperator_EQUALS, strVal("skip")).
		WithLogic(pb.SearchLogic_OR).
		PartitionBy("Label").
		OrderBy("Seq", false).
		OrderBy("Id", true).
		Pattern("A B+").
		Subset("AB", "A", "B").
		Define("B", "Seq > PREV(Seq)").
		Measure("n", "COUNT(*)").
		Measure("last_seq", "LAST(B.Seq)").
		RowsPerMatch(pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY).
		AfterMatch(pb.AfterMatchSkipKind_TO_FIRST, "B").
		Limit(100).
		Build("trace-1")

	assertMatchesMatchPatternFixture(t, req, "match-pattern-contract-1.json")
}

func TestMatchPatternBuild_MatchesGoldenFixture_Contract2(t *testing.T) {
	req := iverson.NewMatchPattern("VectorDoc").
		Chunks("Body").
		Pattern("A").
		Define("A", "SIMILARITY(text, 'refund') > 0.5").
		Build("")

	assertMatchesMatchPatternFixture(t, req, "match-pattern-contract-2.json")
}

// ── Focused builder facts ──────────────────────────────────────────────────

func TestMatchPatternChunks_SetsSourceAndChunkProperty(t *testing.T) {
	req := iverson.NewMatchPattern("VectorDoc").Chunks("Body").Build()

	if req.Source != pb.PatternRowSource_CHUNKS {
		t.Errorf("source = %v, want CHUNKS", req.Source)
	}
	if req.ChunkProperty != "Body" {
		t.Errorf("chunkProperty = %q, want %q", req.ChunkProperty, "Body")
	}
}

func TestMatchPatternBuild_LeavesAfterMatchAndLimitUnsetByDefault(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").OrderBy("Seq", false).Pattern("A").Build()

	if req.AfterMatch != nil {
		t.Errorf("afterMatch = %+v, want nil (server default PAST_LAST_ROW)", req.AfterMatch)
	}
	if req.Limit != 0 {
		t.Errorf("limit = %d, want 0 (server default)", req.Limit)
	}
}

func TestMatchPatternAfterMatch_WithoutVariable_SetsKindOnly(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").AfterMatch(pb.AfterMatchSkipKind_TO_NEXT_ROW).Build()

	if req.AfterMatch == nil || req.AfterMatch.Kind != pb.AfterMatchSkipKind_TO_NEXT_ROW || req.AfterMatch.Variable != "" {
		t.Errorf("afterMatch = %+v, want {TO_NEXT_ROW, \"\"}", req.AfterMatch)
	}
}

func TestMatchPatternBuild_DefaultTraceIdIsEmpty(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").Build()

	if req.TraceId != "" {
		t.Errorf("traceId = %q, want empty", req.TraceId)
	}
}
```

`Iverson.Clients/Go/iverson/coordinator_test.go` — insert immediately before `type mockSearchClient struct {`:
```go
type mockMatchPatternStream struct {
	responses []*pb.MatchPatternResponse
	idx       int
	streamErr error
}

func (m *mockMatchPatternStream) Recv() (*pb.MatchPatternResponse, error) {
	if m.idx < len(m.responses) {
		r := m.responses[m.idx]
		m.idx++
		return r, nil
	}
	if m.streamErr != nil {
		return nil, m.streamErr
	}
	return nil, io.EOF
}
```

Replace the `mockSearchClient` struct with (new: `matchPatternStream`, `matchPatternErr`, `capturedMatchPattern`):
```go
type mockSearchClient struct {
	searchStream   *mockSearchStream
	searchErr      error
	similarStream  *mockSearchStream
	similarErr     error
	chunksStream   *mockChunkSearchStream
	chunksErr      error
	groupByStream  *mockSearchStream
	groupByErr     error
	pipelineStream *mockSearchStream
	pipelineErr    error
	aggregateResp  *pb.AggregateResponse
	aggregateErr   error

	matchPatternStream *mockMatchPatternStream
	matchPatternErr    error

	capturedSearch        *pb.SearchRequest
	capturedSearchSimilar *pb.SearchSimilarRequest
	capturedSearchChunks  *pb.SearchChunksRequest
	capturedGroupBy       *pb.GroupByRequest
	capturedPipeline      *pb.PipelineRequest
	capturedAggregate     *pb.AggregateRequest
	capturedMatchPattern  *pb.MatchPatternRequest
}
```

Add after `func (m *mockSearchClient) Pipeline(...)`:
```go
func (m *mockSearchClient) MatchPattern(_ context.Context, req *pb.MatchPatternRequest) (MatchPatternStream, error) {
	m.capturedMatchPattern = req
	if m.matchPatternErr != nil {
		return nil, m.matchPatternErr
	}
	return m.matchPatternStream, nil
}
```

Insert immediately before the `// ── Aggregate ──…` section comment:
```go
// ── MatchPattern ──────────────────────────────────────────────────────────────

func TestCoordinatorMatchPattern_ReturnsResults(t *testing.T) {
	search := &mockSearchClient{
		matchPatternStream: &mockMatchPatternStream{responses: []*pb.MatchPatternResponse{
			{Data: mustStruct(t, map[string]interface{}{"Label": "pat-go", "n": 3.0}), MatchNumber: 1},
			{Data: mustStruct(t, map[string]interface{}{"Seq": 2.0}), MatchNumber: 2, Classifier: "B"},
		}},
	}
	c := newTestCoordinator(t, search)
	req := NewMatchPattern("coordinatorArticle").OrderBy("Seq", false).Pattern("A B+").Build("trace-1")

	results, err := c.MatchPattern(context.Background(), req)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(results) != 2 {
		t.Fatalf("expected 2 results, got %d", len(results))
	}
	if results[0].Data["Label"] != "pat-go" || results[0].Data["n"] != 3.0 ||
		results[0].MatchNumber != 1 || results[0].Classifier != "" {
		t.Errorf("unexpected result 0: %+v", results[0])
	}
	if results[1].Data["Seq"] != 2.0 || results[1].MatchNumber != 2 || results[1].Classifier != "B" {
		t.Errorf("unexpected result 1: %+v", results[1])
	}
	if search.capturedMatchPattern != req {
		t.Errorf("request not passed through unchanged: %+v", search.capturedMatchPattern)
	}
}

func TestCoordinatorMatchPattern_WrapsInitialError(t *testing.T) {
	boom := errors.New("boom")
	search := &mockSearchClient{matchPatternErr: boom}
	c := newTestCoordinator(t, search)

	_, err := c.MatchPattern(context.Background(), &pb.MatchPatternRequest{})
	if !errors.Is(err, boom) || !strings.HasPrefix(err.Error(), "MatchPattern: ") {
		t.Fatalf("expected wrapped \"MatchPattern: boom\", got %v", err)
	}
}

func TestCoordinatorMatchPattern_WrapsStreamError(t *testing.T) {
	boom := errors.New("stream boom")
	search := &mockSearchClient{
		matchPatternStream: &mockMatchPatternStream{
			responses: []*pb.MatchPatternResponse{{Data: mustStruct(t, map[string]interface{}{"n": 1.0}), MatchNumber: 1}},
			streamErr: boom,
		},
	}
	c := newTestCoordinator(t, search)

	results, err := c.MatchPattern(context.Background(), &pb.MatchPatternRequest{})
	if !errors.Is(err, boom) || !strings.HasPrefix(err.Error(), "MatchPattern stream: ") {
		t.Fatalf("expected wrapped \"MatchPattern stream: stream boom\", got %v", err)
	}
	if results != nil {
		t.Errorf("expected no partial results on a stream error, got %+v", results)
	}
}

```

- [ ] **Step 2: Run the tests to verify they fail**

Run (from `Iverson.Clients/Go`): `export PATH=~/sdk/go1.22/bin:~/go/bin:$PATH GOPATH=~/go GOROOT=~/sdk/go1.22 && go test ./...`
Expected: build FAIL in both test packages — `iverson/coordinator_test.go`: `undefined: MatchPatternStream`, `undefined: NewMatchPattern`, `c.MatchPattern undefined (type *EntityCoordinator[coordinatorArticle] has no field or method MatchPattern)`; `iverson_test/match_pattern_test.go`: `undefined: iverson.NewMatchPattern`. `conformance` stays `ok`.

- [ ] **Step 3: Implement the builder and the coordinator method**

The builder mirrors `pipeline.go`'s base-step `Where`/`Not`/`WithLogic` (value is `*pb.SearchValue`; `Not` is `MUST_NOT`) but performs NO validation (a nil value passes through; the server validates), so `Build` has no error return. Unlike `NewPipeline`, it does NOT default the limit to 10,000: every field starts at its proto default, and `AfterMatch` stays nil unless called.

`Iverson.Clients/Go/iverson/match_pattern.go`:
```go
package iverson

import (
	pb "github.com/iverson/clients/go/generated"
)

// MatchPatternBuilder builds a MatchPatternRequest (SQL:2016 row pattern recognition) using a
// fluent API. It performs no validation: pattern and expression strings pass through unchanged
// and the server validates the request, so Build has no error return.
type MatchPatternBuilder struct {
	typeName      string
	source        pb.PatternRowSource
	chunkProperty string
	where         []*pb.SearchClause
	whereLogic    pb.SearchLogic
	partitionBy   []string
	orderBy       []*pb.SearchSort
	pattern       string
	subsets       []*pb.PatternSubset
	define        []*pb.NamedExpr
	measures      []*pb.NamedExpr
	rowsPerMatch  pb.RowsPerMatch
	afterMatch    *pb.AfterMatchSkip
	limit         int32
}

// NewMatchPattern creates a MatchPatternBuilder for the given entity type name. Every field
// starts at its proto default: TYPE_ROWS, AND, ONE_ROW, no AFTER MATCH clause (the server
// applies PAST_LAST_ROW) and limit 0 (the server applies its default).
func NewMatchPattern(typeName string) *MatchPatternBuilder {
	return &MatchPatternBuilder{typeName: typeName}
}

// Chunks matches over the chunks of the given [IversonChunk] property instead of the type's rows.
func (m *MatchPatternBuilder) Chunks(chunkProperty string) *MatchPatternBuilder {
	m.source = pb.PatternRowSource_CHUNKS
	m.chunkProperty = chunkProperty
	return m
}

// Where adds a pre-filter (FILTER) clause.
func (m *MatchPatternBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *MatchPatternBuilder {
	return m.addClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT pre-filter clause.
func (m *MatchPatternBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *MatchPatternBuilder {
	return m.addClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the logic combining the pre-filter clauses. Default: AND.
func (m *MatchPatternBuilder) WithLogic(logic pb.SearchLogic) *MatchPatternBuilder {
	m.whereLogic = logic
	return m
}

// PartitionBy appends PARTITION BY columns.
func (m *MatchPatternBuilder) PartitionBy(fields ...string) *MatchPatternBuilder {
	m.partitionBy = append(m.partitionBy, fields...)
	return m
}

// OrderBy appends an ORDER BY column.
func (m *MatchPatternBuilder) OrderBy(field string, descending bool) *MatchPatternBuilder {
	m.orderBy = append(m.orderBy, &pb.SearchSort{Property: field, Descending: descending})
	return m
}

// Pattern sets the PATTERN, e.g. "A B+".
func (m *MatchPatternBuilder) Pattern(pattern string) *MatchPatternBuilder {
	m.pattern = pattern
	return m
}

// Subset appends a SUBSET name = (variables...).
func (m *MatchPatternBuilder) Subset(name string, variables ...string) *MatchPatternBuilder {
	m.subsets = append(m.subsets, &pb.PatternSubset{Name: name, Variables: variables})
	return m
}

// Define appends a DEFINE variable AS expr.
func (m *MatchPatternBuilder) Define(variable, expr string) *MatchPatternBuilder {
	m.define = append(m.define, &pb.NamedExpr{Name: variable, Expr: expr})
	return m
}

// Measure appends a MEASURES expr AS name.
func (m *MatchPatternBuilder) Measure(name, expr string) *MatchPatternBuilder {
	m.measures = append(m.measures, &pb.NamedExpr{Name: name, Expr: expr})
	return m
}

// RowsPerMatch sets ONE ROW or one of the ALL ROWS modes. Default: ONE_ROW.
func (m *MatchPatternBuilder) RowsPerMatch(mode pb.RowsPerMatch) *MatchPatternBuilder {
	m.rowsPerMatch = mode
	return m
}

// AfterMatch sets AFTER MATCH SKIP. The optional variable names the target of TO_FIRST/TO_LAST.
func (m *MatchPatternBuilder) AfterMatch(kind pb.AfterMatchSkipKind, variable ...string) *MatchPatternBuilder {
	v := ""
	if len(variable) > 0 {
		v = variable[0]
	}
	m.afterMatch = &pb.AfterMatchSkip{Kind: kind, Variable: v}
	return m
}

// Limit caps the output rows. Default: 0, which leaves the limit to the server.
func (m *MatchPatternBuilder) Limit(n int32) *MatchPatternBuilder {
	m.limit = n
	return m
}

// Build constructs the MatchPatternRequest proto. An optional traceId may be supplied.
func (m *MatchPatternBuilder) Build(traceId ...string) *pb.MatchPatternRequest {
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.MatchPatternRequest{
		TypeName:      m.typeName,
		Source:        m.source,
		ChunkProperty: m.chunkProperty,
		Where:         m.where,
		WhereLogic:    m.whereLogic,
		PartitionBy:   m.partitionBy,
		OrderBy:       m.orderBy,
		Pattern:       m.pattern,
		Subsets:       m.subsets,
		Define:        m.define,
		Measures:      m.measures,
		RowsPerMatch:  m.rowsPerMatch,
		AfterMatch:    m.afterMatch,
		Limit:         m.limit,
		TraceId:       id,
	}
}

func (m *MatchPatternBuilder) addClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *MatchPatternBuilder {
	m.where = append(m.where, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return m
}
```

`Iverson.Clients/Go/iverson/coordinator.go` — the `SearchClient` interface becomes:
```go
// SearchClient is the interface for ObjectSearchService stub.
type SearchClient interface {
	Search(ctx context.Context, req *pb.SearchRequest) (SearchStream, error)
	SearchSimilar(ctx context.Context, req *pb.SearchSimilarRequest) (SearchStream, error)
	SearchChunks(ctx context.Context, req *pb.SearchChunksRequest) (ChunkSearchStream, error)
	Aggregate(ctx context.Context, req *pb.AggregateRequest) (*pb.AggregateResponse, error)
	GroupBy(ctx context.Context, req *pb.GroupByRequest) (SearchStream, error)
	Pipeline(ctx context.Context, req *pb.PipelineRequest) (SearchStream, error)
	MatchPattern(ctx context.Context, req *pb.MatchPatternRequest) (MatchPatternStream, error)
}
```

Add after `ChunkSearchStream`:
```go
// MatchPatternStream is the interface for the streaming MatchPattern response.
type MatchPatternStream interface {
	Recv() (*pb.MatchPatternResponse, error)
}
```

Add after `SearchResult[T]`:
```go
// MatchPatternResult is one MatchPattern output row: its columns (the partition columns and
// measures for ONE_ROW, every visible column plus the measures for the ALL_ROWS modes), the
// 1-based match number (0 for an unmatched row) and the row's pattern variable (ALL_ROWS
// modes only; empty for ONE_ROW and unmatched rows).
type MatchPatternResult struct {
	Data        map[string]any
	MatchNumber int64
	Classifier  string
}
```

Add after `EntityCoordinator[T].Pipeline` (same Recv-until-`io.EOF` loop and error wrapping):
```go
// MatchPattern executes a row pattern matching request and returns every output row, drained
// from the stream. Columns depend on the measures and the rows-per-match mode, so each row's
// Data is an untyped map, same as Pipeline.
func (c *EntityCoordinator[T]) MatchPattern(ctx context.Context, req *pb.MatchPatternRequest) ([]MatchPatternResult, error) {
	stream, err := c.deps.search.MatchPattern(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("MatchPattern: %w", err)
	}

	var results []MatchPatternResult
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("MatchPattern stream: %w", err)
		}
		results = append(results, MatchPatternResult{
			Data:        structToMap(resp.Data),
			MatchNumber: resp.MatchNumber,
			Classifier:  resp.Classifier,
		})
	}
	return results, nil
}
```

Update the `structToMap` doc comment to name its new caller:
```go
// structToMap converts a google.protobuf.Struct to an untyped map, for results whose
// columns are aggregated/aliased and don't correspond to any single entity's fields
// (GroupBy, Pipeline, MatchPattern) — unlike structToEntity[T], it isn't driven by a target reflect.Type.
```

Add after `searchAdapter.Pipeline` (the generated `grpc.ServerStreamingClient[pb.MatchPatternResponse]` satisfies `MatchPatternStream`):
```go
func (a *searchAdapter) MatchPattern(ctx context.Context, req *pb.MatchPatternRequest) (MatchPatternStream, error) {
	return a.stub.MatchPattern(ctx, req)
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run (from `Iverson.Clients/Go`): `export PATH=~/sdk/go1.22/bin:~/go/bin:$PATH GOPATH=~/go GOROOT=~/sdk/go1.22 && go test ./...`
Expected: `ok` for `conformance`, `iverson` and `iverson_test`.

Run: `go test -v ./... 2>&1 | command grep -c -- '--- PASS'`
Expected: `210` (201 before this task, plus 3 coordinator tests and 6 builder tests).

Run: `gofmt -l iverson iverson_test && go vet ./iverson ./iverson_test`
Expected: no output.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Go/iverson/match_pattern.go Iverson.Clients/Go/iverson/coordinator.go Iverson.Clients/Go/iverson/coordinator_test.go Iverson.Clients/Go/iverson_test/match_pattern_test.go
git commit -m "add the go matchpattern builder and coordinator method" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 7: Client-conformance orchestrator — the `match-pattern` scenario and `IVC-QRY-005..007`

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/MatchPatternScenario.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Requirements.cs:390` (three consts after `QryAggregateCountsExactlyMatchingRows`, before the VEC banner at :392)
- Modify: `Iverson.Server/Iverson.ClientConformance/Program.cs:66-72` (`recognizedScenarios`), `:127` (construction), `:250-258` (dispatch after `inherited-model`), `:314` (`--help` credentials text)
- Modify: `docs/standards/iverson-client-standard.md:78` (Active count), `:665` (QRY table), `:693-694` (QRY prose), `:704` and `:707` (QRY Coverage ledger), `:716` (QRY backstop note). Tracked, but `docs/` is gitignored — stage it with `git add -f`
- Modify: `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs:1320` and `:1335` (the "all 46" count in two failure messages becomes 49)
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/MatchPatternScenarioTests.cs`

**Interfaces:**
- Consumes: the generated .NET client `ObjectSearchService.ObjectSearchServiceClient.MatchPattern(MatchPatternRequest, Metadata, DateTime?, CancellationToken)` → `AsyncServerStreamingCall<MatchPatternResponse>` and the messages `MatchPatternRequest`, `NamedExpr`, `SearchSort`, `PatternRowSource`, `RowsPerMatch` — all generated today by `Iverson.Client.Contracts` from `Iverson.Clients/Common/Proto/object_search.proto`; nothing new is generated. Also the existing harness types `IDriverRunner` (`RunPhaseAsync`, `KeysByLanguage`), `IReregistrar`, `ProjectionWaiter`/`ProbeOutcome`, `ScenarioCells`, `Assertion`, `ReportCell`.
- Produces (Task 8's five drivers must match these literals exactly — contracts.md §3):
  - scenario `match-pattern`; type `PatternDoc`; `Label = "pat-<lang>"` with `<lang>` ∈ `dotnet`, `python`, `typescript`, `go`, `java`;
  - register step `register_pattern_doc` (.NET only, reports `TypeDescriptor`);
  - write step `write_pattern_docs`: three rows, `Marker = --id-prefix`, `Seq` 1..3, `Title = "a note about row pattern matching, part <n>"`, reporting the server-returned keys as `Keys = {"pattern_doc_1": …, "pattern_doc_2": …, "pattern_doc_3": …}`;
  - read steps `match_pattern_scalar` and `match_pattern_similarity`, each with entity `{"rows":[{"matchNumber":<number>,"classifier":"<string>","data":{<row data, keys verbatim>}}, …]}` in stream order;
  - `match_pattern_scalar`: TYPE_ROWS, `Where("Marker", EQUALS, idPrefix)`, `PartitionBy("Label")`, `OrderBy("Seq")`, `Pattern("A B+")`, `Define("B", "Seq > PREV(Seq)")`, `Measure("n", "COUNT(*)")`, `Measure("first_seq", "FIRST(A.Seq)")`, `Measure("last_seq", "LAST(B.Seq)")`, `RowsPerMatch(ONE_ROW)`, `Limit(100)`;
  - `match_pattern_similarity`: TYPE_ROWS, `Where("Marker", EQUALS, idPrefix)`, `PartitionBy("Label")`, `OrderBy("Seq")`, `Pattern("A+")`, `Define("A", "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL")`, `Measure("s", "SIMILARITY(Title, 'a note about row pattern matching')")`, `RowsPerMatch(ALL_ROWS_SHOW_EMPTY)`, `Limit(100)`;
  - requirement consts `Requirements.QryMatchPatternReachable` (`IVC-QRY-005`), `Requirements.QryMatchPatternReturnsExactlyExpectedMatches` (`IVC-QRY-006`), `Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow` (`IVC-QRY-007`).

The orchestrator judges only what the drivers report. It never builds the drivers' two requests itself. Its own `MatchPattern` call is the projection probe (contracts.md §3): pattern `A` with `A AS SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL`, measure `id AS FIRST(A.Id)`, ONE_ROW, limit 10000. Each response carries the `Id` of one row whose vector exists. The wait is Ready when every expected key is among the returned ids, a superset check, so rows from a language that did not report all three keys cannot block it. No client report is ever compared against the probe.

- [ ] **Step 1: Write the failing tests**

`Iverson.Server/Iverson.ClientConformance.Tests/MatchPatternScenarioTests.cs`:
```csharp
using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for the <c>match-pattern</c> scenario's judgement, which is pure over reported data
/// (<see cref="MatchPatternScenario.Judge"/>, <see cref="MatchPatternScenario.ReadRows"/>,
/// <see cref="MatchPatternScenario.ExpectedRows"/>) and so is exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and each of <c>IVC-QRY-005</c>..<c>007</c> must go red for exactly the defect its
/// statement describes and for nothing else.
/// </summary>
public class MatchPatternScenarioTests
{
    private static readonly Guid D1 = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid D2 = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid D3 = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid P2 = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid P3 = Guid.Parse("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid Stranger = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>The harness's expectation when dotnet and python each seeded their three rows.</summary>
    private static Dictionary<Guid, string> Seeded() => new()
    {
        [D1] = "dotnet", [D2] = "dotnet", [D3] = "dotnet",
        [P1] = "python", [P2] = "python", [P3] = "python",
    };

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static JsonElement Rows(params object[] rows) => JsonSerializer.SerializeToElement(new { rows });

    private static object ScalarRow(
        string label, double n = 3, double firstSeq = 1, double lastSeq = 3, double matchNumber = 1) => new
    {
        matchNumber,
        classifier = "",
        data = new Dictionary<string, object?>
        {
            ["Label"] = label, ["n"] = n, ["first_seq"] = firstSeq, ["last_seq"] = lastSeq,
        },
    };

    private static object SimilarityRow(
        Guid id, string label, object? s = null, string classifier = "A", double matchNumber = 1) => new
    {
        matchNumber,
        classifier,
        data = new Dictionary<string, object?>
        {
            ["Id"] = id.ToString(), ["Label"] = label, ["Seq"] = 1, ["s"] = s ?? 0.6000000238418579,
        },
    };

    private static StepResult ScalarStep(params object[] rows) =>
        new(MatchPatternScenario.ScalarStepName, true, Entity: Rows(rows));

    private static StepResult SimilarityStep(params object[] rows) =>
        new(MatchPatternScenario.SimilarityStepName, true, Entity: Rows(rows));

    private static StepResult GoodScalarStep() =>
        ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"));

    private static StepResult GoodSimilarityStep() => SimilarityStep(
        SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
        SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"));

    private static IReadOnlyList<Assertion> JudgeSeeded(params StepResult[] steps) =>
        MatchPatternScenario.Judge("dotnet", Seeded(), ReadDocument(steps));

    private static Assertion Scalar(IReadOnlyList<Assertion> assertions) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

    private static Assertion Similarity(IReadOnlyList<Assertion> assertions) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);

    private static Assertion Reachable(IReadOnlyList<Assertion> assertions, string which) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternReachable
                               && a.Name.Contains($"a {which} row pattern match", StringComparison.Ordinal));

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_BothPatternsReturnTheExpectedRows_AllAssertionsPass()
    {
        var assertions = JudgeSeeded(GoodScalarStep(), GoodSimilarityStep());

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Where(a => a.RequirementId == Requirements.QryMatchPatternReachable).Should().HaveCount(2);
        Scalar(assertions).Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_RowsArriveInAnotherOrder_StillPasses()
    {
        // Both comparisons are keyed, not positional: the partitions' output order is the server's
        // business and no QRY requirement constrains it.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-python"), ScalarRow("pat-dotnet")),
            SimilarityStep(
                SimilarityRow(P3, "pat-python"), SimilarityRow(D1, "pat-dotnet"), SimilarityRow(P1, "pat-python"),
                SimilarityRow(D3, "pat-dotnet"), SimilarityRow(P2, "pat-python"), SimilarityRow(D2, "pat-dotnet")));

        assertions.Should().OnlyContain(a => a.Passed);
    }

    // ── IVC-QRY-005: reachability, and what a missing or failed step does to the content half ──

    [Fact]
    public void Judge_ScalarStepAbsent_FailsScalarReachabilityAndTheScalarResult_NamingTheStep()
    {
        var assertions = JudgeSeeded(GoodSimilarityStep());

        Reachable(assertions, "scalar").Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Detail.Should().Contain(MatchPatternScenario.ScalarStepName);
        Scalar(assertions).Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_ScalarStepFailed_FailsScalarReachabilityWithTheDriversErrorAndTheScalarResult()
    {
        var assertions = JudgeSeeded(
            new StepResult(MatchPatternScenario.ScalarStepName, false, "InvalidArgument: unknown column 'Seq'"),
            GoodSimilarityStep());

        Reachable(assertions, "scalar").Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Detail.Should().Contain("unknown column 'Seq'");
        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("failed");
    }

    [Fact]
    public void Judge_SimilarityStepAbsent_FailsSimilarityReachabilityAndTheSimilarityResult_NamingTheStep()
    {
        var assertions = JudgeSeeded(GoodScalarStep());

        Reachable(assertions, "SIMILARITY").Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Detail.Should().Contain(MatchPatternScenario.SimilarityStepName);
        Similarity(assertions).Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Passed.Should().BeTrue();
        Scalar(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityStepFailed_FailsSimilarityReachabilityWithTheDriversErrorAndTheResult()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            new StepResult(MatchPatternScenario.SimilarityStepName, false, "Unavailable: embedding service down"));

        Reachable(assertions, "SIMILARITY").Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Detail.Should().Contain("embedding service down");
        Similarity(assertions).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_MalformedRows_FailTheContentAssertionButNotReachability()
    {
        // The call completed, so QRY-005 holds; what came back cannot be graded, so the content
        // half fails naming the malformation rather than reading as "no rows".
        var assertions = JudgeSeeded(
            new StepResult(MatchPatternScenario.ScalarStepName, true,
                Entity: JsonSerializer.SerializeToElement(new { rows = new object[] { new { classifier = "" } } })),
            new StepResult(MatchPatternScenario.SimilarityStepName, true,
                Entity: JsonSerializer.SerializeToElement(new { labels = new[] { "pat-dotnet" } })));

        Reachable(assertions, "scalar").Passed.Should().BeTrue();
        Reachable(assertions, "SIMILARITY").Passed.Should().BeTrue();
        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("malformed").And.Contain("matchNumber");
        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("malformed").And.Contain("'rows'");
    }

    // ── IVC-QRY-006: one exact match per seeded language ──────────────────────────────────────

    [Theory]
    [InlineData("n", 2, 1, 3, 1)]
    [InlineData("first_seq", 3, 2, 3, 1)]
    [InlineData("last_seq", 3, 1, 2, 1)]
    [InlineData("matchNumber", 3, 1, 3, 2)]
    public void Judge_ScalarMatchWithAWrongValue_FailsOnlyTheScalarResult_NamingTheField(
        string field, double n, double firstSeq, double lastSeq, double matchNumber)
    {
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python", n, firstSeq, lastSeq, matchNumber)),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-python").And.Contain(field);
        Scalar(assertions).Detail.Should().NotContain("pat-dotnet");
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_ScalarMeasureAbsent_FailsTheScalarResult()
    {
        var row = new
        {
            matchNumber = 1,
            classifier = "",
            data = new Dictionary<string, object?> { ["Label"] = "pat-python", ["n"] = 3, ["first_seq"] = 1 },
        };

        var assertions = JudgeSeeded(ScalarStep(ScalarRow("pat-dotnet"), row), GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("last_seq");
    }

    [Fact]
    public void Judge_ScalarResultCarriesALabelNoSeededLanguageWrote_FailsNamingIt()
    {
        // A client that dropped the marker filter would see an earlier run's partitions.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"), ScalarRow("pat-someone-else")),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-someone-else");
    }

    [Fact]
    public void Judge_ScalarResultLacksASeededLanguagesMatch_FailsNamingTheMissingLabel()
    {
        var assertions = JudgeSeeded(ScalarStep(ScalarRow("pat-dotnet")), GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-python").And.Contain("0 match(es)");
    }

    [Fact]
    public void Judge_ScalarResultRepeatsASeededLanguagesMatch_Fails()
    {
        // "Exactly one" per language: a client that re-yields a buffered response must not pass
        // on the strength of the label set alone.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"), ScalarRow("pat-python")),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("2 match(es)");
    }

    // ── IVC-QRY-007: every seeded row scored, under the right label ──────────────────────────

    [Fact]
    public void Judge_SimilarityResultDroppedASeededRow_FailsOnlyTheSimilarityResult()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(P3.ToString());
        Scalar(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityResultCarriesARowTheRunNeverSeeded_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"),
                SimilarityRow(Stranger, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(Stranger.ToString());
    }

    [Fact]
    public void Judge_SimilarityRowWithNoUsableId_Fails()
    {
        var noId = new
        {
            matchNumber = 1,
            classifier = "A",
            data = new Dictionary<string, object?> { ["Id"] = "not-a-uuid", ["Label"] = "pat-python", ["s"] = 0.5 },
        };

        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"),
                noId));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("'Id'");
    }

    [Fact]
    public void Judge_SimilarityRowWithAWrongClassifier_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"),
                SimilarityRow(P3, "pat-python", classifier: "")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("classifier").And.Contain(P3.ToString());
    }

    [Fact]
    public void Judge_SimilarityRowWithAWrongMatchNumber_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"),
                SimilarityRow(D3, "pat-dotnet", matchNumber: 0),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("matchNumber").And.Contain(D3.ToString());
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("1e400")]
    [InlineData("null")]
    [InlineData("\"0.5\"")]
    public void Judge_SimilarityScoreThatIsNotAFiniteNumber_Fails(string rawScore)
    {
        var assertions = JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("not a finite number").And.Contain(P3.ToString());
    }

    [Fact]
    public void Judge_SimilarityScoreAbsent_Fails()
    {
        var noScore = new
        {
            matchNumber = 1,
            classifier = "A",
            data = new Dictionary<string, object?> { ["Id"] = P3.ToString(), ["Label"] = "pat-python" },
        };

        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), noScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("'s'");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1.01")]
    public void Judge_SimilarityScoreOutsideTheCosineRange_Fails(string rawScore)
    {
        var assertions = JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("outside [-1, 1]");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Judge_SimilarityScoreOnOrInsideTheCosineRange_Passes(string rawScore)
    {
        // Scores are never compared to absolute values — only finiteness and the cosine range.
        Similarity(JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore))).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityRowUnderAnotherLanguagesLabel_Fails()
    {
        // The set of Ids is right, but P3 came back under dotnet's partition: the client mixed up
        // which row is which.
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-dotnet")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(P3.ToString()).And.Contain("pat-python");
    }

    private static StepResult SimilarityStepWithP3Score(string rawScore)
    {
        var p3 = JsonDocument.Parse(
            $$"""{"matchNumber":1,"classifier":"A","data":{"Id":"{{P3}}","Label":"pat-python","s":""" + rawScore + "}}")
            .RootElement;

        return SimilarityStep(
            SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
            SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), p3);
    }

    // ── the backstop (uncited by design) ──────────────────────────────────────────────────────

    [Fact]
    public void Judge_NothingWasSeeded_FailsTheBackstopEvenThoughBothComparisonsAgree()
    {
        var assertions = MatchPatternScenario.Judge(
            "go", new Dictionary<Guid, string>(), ReadDocument(ScalarStep(), SimilarityStep()));

        Scalar(assertions).Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();

        var backstop = assertions.Single(a => a.Name.Contains("seeded at least one row", StringComparison.Ordinal));
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull();
    }

    // ── the reporting reader ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadRows_ReadsEachRowsMatchNumberClassifierAndData()
    {
        var (rows, problem) = MatchPatternScenario.ReadRows(Rows(ScalarRow("pat-go"), SimilarityRow(D1, "pat-go")));

        problem.Should().BeNull();
        rows.Should().HaveCount(2);
        rows![0].MatchNumber.Should().Be(1);
        rows[0].Classifier.Should().Be("");
        rows[0].Data.GetProperty("first_seq").GetDouble().Should().Be(1);
        rows[1].Classifier.Should().Be("A");
    }

    [Fact]
    public void ReadRows_AnEmptyRowsArray_IsAnEmptyResultNotAMalformedOne() =>
        MatchPatternScenario.ReadRows(Rows()).Rows.Should().BeEmpty();

    [Theory]
    [InlineData("\"not an object\"", "not a JSON object")]
    [InlineData("""{"labels":[]}""", "'rows'")]
    [InlineData("""{"rows":7}""", "'rows'")]
    [InlineData("""{"rows":[7]}""", "rows[0]")]
    [InlineData("""{"rows":[{"classifier":"A","data":{}}]}""", "matchNumber")]
    [InlineData("""{"rows":[{"matchNumber":"1","classifier":"A","data":{}}]}""", "matchNumber")]
    [InlineData("""{"rows":[{"matchNumber":1,"data":{}}]}""", "classifier")]
    [InlineData("""{"rows":[{"matchNumber":1,"classifier":"A","data":[]}]}""", "data")]
    public void ReadRows_MalformedDocument_YieldsNoRowsAndNamesTheProblem(string json, string named)
    {
        var (rows, problem) = MatchPatternScenario.ReadRows(JsonDocument.Parse(json).RootElement);

        rows.Should().BeNull();
        problem.Should().Contain(named);
    }

    [Fact]
    public void ReadRows_AbsentDocument_YieldsNoRowsRatherThanThrowing() =>
        MatchPatternScenario.ReadRows(null).Rows.Should().BeNull();

    // ── the harness's own expectation ────────────────────────────────────────────────────────

    [Fact]
    public void ExpectedRows_MapsEveryKeyOfEachLanguageThatReportedAllThree_ToThatLanguage()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["dotnet"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = D1.ToString(), ["pattern_doc_2"] = D2.ToString(), ["pattern_doc_3"] = D3.ToString(),
            },
            // Spelled differently: parsed as UUIDs, so the five languages' spellings compare.
            ["python"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = P1.ToString().ToUpperInvariant(),
                ["pattern_doc_2"] = P2.ToString("B"),
                ["pattern_doc_3"] = P3.ToString(),
            },
            // Only two of the three: this language did not seed its partition, so it is not
            // expected in either comparison.
            ["go"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = Guid.NewGuid().ToString(), ["pattern_doc_2"] = Guid.NewGuid().ToString(),
            },
            // An unparsable key is the same, and must not throw.
            ["java"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = Guid.NewGuid().ToString(), ["pattern_doc_2"] = Guid.NewGuid().ToString(),
                ["pattern_doc_3"] = "not-a-uuid",
            },
            // Keys under another scenario's logical name only.
            ["typescript"] = new Dictionary<string, string> { ["vector_doc"] = Stranger.ToString() },
        };

        MatchPatternScenario.ExpectedRows(keys).Should().BeEquivalentTo(Seeded());
    }

    [Fact]
    public void LabelFor_IsThePerLanguageIdentityTheDriversStamp() =>
        MatchPatternScenario.LabelFor("typescript").Should().Be("pat-typescript");

    [Fact]
    public void RowKeyNames_AreTheThreeLogicalKeysEveryDriverReports() =>
        MatchPatternScenario.RowKeyNames.Should().Equal("pattern_doc_1", "pattern_doc_2", "pattern_doc_3");

    // ── the projection-wait predicate ────────────────────────────────────────────────────────

    [Fact]
    public void ProjectionReady_EveryExpectedIdReturned_IsReady() =>
        MatchPatternScenario.ProjectionReady(Seeded().Keys, Seeded().Keys.ToHashSet()).Should().BeTrue();

    [Fact]
    public void ProjectionReady_OneExpectedIdNotYetReturned_IsNotReady_AndCountsItMissing()
    {
        var visible = Seeded().Keys.Where(k => k != P3).ToHashSet();

        MatchPatternScenario.ProjectionReady(Seeded().Keys, visible).Should().BeFalse();
        MatchPatternScenario.MissingFromProbe(Seeded().Keys, visible).Should().Be(1);
    }

    [Fact]
    public void ProjectionReady_ExtraIdsFromAPartiallySeededLanguage_DoNotBlockReadiness()
    {
        // The mutation this pins: an exact-count (or set-equality) predicate. A language that
        // wrote rows under the run marker but did not report all three keys is absent from the
        // expectation, yet its rows are visible to the probe — they must not stall every
        // language's wait.
        var visible = Seeded().Keys.Append(Stranger).Append(Guid.NewGuid()).ToHashSet();

        MatchPatternScenario.ProjectionReady(Seeded().Keys, visible).Should().BeTrue();
        MatchPatternScenario.MissingFromProbe(Seeded().Keys, visible).Should().Be(0);
    }

    [Fact]
    public void ProjectionReady_NothingWasSeeded_IsDeliberatelyNotReady()
    {
        // Zero expected is NOT ready on purpose: no language seeded anything, so satisfying the
        // wait would let the read phase grade five clients against nothing.
        MatchPatternScenario.ProjectionReady([], new HashSet<Guid>()).Should().BeFalse();
        MatchPatternScenario.ProjectionReady([], new HashSet<Guid> { Stranger }).Should().BeFalse();
    }

    // ── RunAsync plumbing and the read-phase grading seam ─────────────────────────────────────

    private static DriverContext Context() => new(
        Scenario: MatchPatternScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "mp-");

    private static MatchPatternScenario BuildScenario(string repoRoot = "/tmp")
    {
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:1");
        return new MatchPatternScenario(
            new DriverRunner(repoRoot: repoRoot),
            new Reregistrar(new Iverson.Client.Contracts.ObjectMappingService.ObjectMappingServiceClient(channel)),
            new Iverson.Client.Contracts.ObjectSearchService.ObjectSearchServiceClient(channel));
    }

    private static Dictionary<string, MatchPatternScenario.LanguageState> States(params string[] languages) =>
        languages.ToDictionary(l => l, _ => new MatchPatternScenario.LanguageState(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// THE mutation this test exists for: deleting the <c>Judge</c> call inside
    /// <c>GradeReads</c>, which would leave every other test green while no QRY-005..007
    /// assertion reached a cell.
    /// </summary>
    [Fact]
    public void GradeReads_EachLanguagesJudgement_ReachesItsOwnCell()
    {
        var cells = MatchPatternScenario.GradeReads(States("dotnet", "python"),
            [
                ("dotnet", ReadDocument(GoodScalarStep(), GoodSimilarityStep())),
                ("python", ReadDocument(GoodScalarStep(), SimilarityStep())),
            ],
            Seeded());

        cells.Should().HaveCount(2);

        var dotnet = cells.Single(c => c.Language == "dotnet");
        dotnet.Status.Should().Be(CellStatus.Ok);
        dotnet.Assertions.Should().Contain(a => a.RequirementId == Requirements.QryMatchPatternReachable);
        dotnet.Assertions.Should().Contain(a => a.RequirementId == Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

        var python = cells.Single(c => c.Language == "python");
        python.Status.Should().Be(CellStatus.Fail);
        python.Assertions.Should().Contain(
            a => a.RequirementId == Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow && !a.Passed);
    }

    [Fact]
    public void GradeReads_ALanguageWhoseDriverReportedNoReadDocument_IsNotGreen()
    {
        var cells = MatchPatternScenario.GradeReads(States("dotnet", "go"),
            [("dotnet", ReadDocument(GoodScalarStep(), GoodSimilarityStep()))],
            Seeded());

        cells.Single(c => c.Language == "go").Status.Should().NotBe(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await BuildScenario().RunAsync([], Context(), "acting-token")).Should().BeEmpty();

    [Fact]
    public async Task RunAsync_TheRegisterDriverBreaks_FailsEveryRequestedLanguage()
    {
        var cells = await BuildScenario().RunAsync(["dotnet", "python"], Context(), "acting-token");

        cells.Should().HaveCount(2);
        cells.Should().NotContain(c => c.Status == CellStatus.Ok);
        cells.Should().OnlyContain(c => c.Scenario == MatchPatternScenario.Name);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "FullyQualifiedName~MatchPatternScenarioTests"`
Expected: build FAIL — `error CS0246: The type or namespace name 'MatchPatternScenario' could not be found`.

- [ ] **Step 3: Add the three QRY requirement consts**

In `Iverson.Server/Iverson.ClientConformance/Requirements.cs`, insert after line 390 (`public const string QryAggregateCountsExactlyMatchingRows = "IVC-QRY-004";`), leaving one blank line on each side and keeping the `// ── VEC — Vector` banner below:
```csharp
    /// <summary>
    /// A row pattern match is reachable through the client's public API. Discharged by
    /// <c>MatchPatternScenario.Judge</c>'s two "a scalar / a SIMILARITY row pattern match is
    /// reachable through the client's public API" assertions, over each driver's own
    /// <c>match_pattern_scalar</c> and <c>match_pattern_similarity</c> steps — the driver builds each
    /// request with its own client library's pattern builder (<c>Query.MatchPattern(...)</c>,
    /// <c>match_pattern(...)</c>, <c>matchPattern(...)</c>, <c>iverson.NewMatchPattern(...)</c>,
    /// <c>Query.matchPattern(...)</c>) and executes it through that library's own
    /// <c>MatchPattern</c> entry point, never through a raw generated stub. This is a
    /// <c>Capability</c>: it is satisfied by the call completing, and says nothing about what came
    /// back — that is <c>IVC-QRY-006</c> and <c>IVC-QRY-007</c>. Distinct from <c>IVC-QRY-001</c>
    /// because <c>MatchPattern</c> is its own RPC with its own request and response shapes; a client
    /// can reach <c>Search</c> without it. A driver reporting that its client cannot express the
    /// pattern at all is a FAIL, not a skip.
    /// </summary>
    public const string QryMatchPatternReachable = "IVC-QRY-005";

    /// <summary>
    /// A row pattern match over scalar columns returns exactly one match per partition the pattern
    /// matches, with measures computed from exactly that partition's ordered rows. Discharged by
    /// <c>MatchPatternScenario.Judge</c>'s "the scalar row pattern returned exactly one expected
    /// match per seeded language" assertion: every language seeds one three-row partition
    /// (<c>Label = "pat-&lt;lang&gt;"</c>, <c>Seq</c> 1..3) under the run's marker, and the pattern
    /// <c>A B+</c> with <c>B AS Seq &gt; PREV(Seq)</c> must yield, for each seeded language, exactly
    /// one row with <c>n = 3</c>, <c>first_seq = 1</c>, <c>last_seq = 3</c> and match number 1 — and
    /// no row for any label no seeded language wrote. The measures depend on the filter, the
    /// partitioning and the ordering all being carried through the client unchanged, so a client
    /// that drops or reorders any of them disagrees here. The expected labels come from
    /// <c>DriverRunner.KeysByLanguage</c> (<c>MatchPatternScenario.ExpectedRows</c>), never from the
    /// read phase being judged.
    /// </summary>
    public const string QryMatchPatternReturnsExactlyExpectedMatches = "IVC-QRY-006";

    /// <summary>
    /// A row pattern match whose definitions use <c>SIMILARITY</c> returns every row its filter
    /// admits, each with a finite score in [-1, 1]. Discharged by <c>MatchPatternScenario.Judge</c>'s "the SIMILARITY row
    /// pattern scored exactly the seeded rows" assertion, over the pattern <c>A+</c> with
    /// <c>A AS SIMILARITY(Title, '…') IS NOT NULL</c> and all rows per match: the set of returned
    /// <c>Id</c>s must equal exactly the row keys the WRITE phase reported (both directions), and
    /// every row must carry classifier <c>A</c>, match number 1, a score <c>s</c> that is a finite
    /// number in [-1, 1], and the <c>Label</c> of the language that wrote it. Scores are never
    /// compared to absolute values: they belong to the embedding model, and the requirement
    /// constrains only that the client carries every scored row back intact.
    /// </summary>
    public const string QryMatchPatternSimilarityScoresEveryFilteredRow = "IVC-QRY-007";

```

- [ ] **Step 4: Create the scenario**

`Iverson.Server/Iverson.ClientConformance/Scenarios/MatchPatternScenario.cs`:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S13 <c>match-pattern</c>: proves each client library can express a row pattern match
/// (<c>MatchPattern</c>, SQL:2016 <c>MATCH_RECOGNIZE</c>) through its OWN builder API, and that all
/// five agree on what comes back for the same two patterns over the same seeded rows.
///
/// The shape follows S7 vector-search's, for the same reasons: the subject is one shared type
/// (<c>PatternDoc</c>) that every language writes into and every language then matches, so
/// disagreement between two client libraries is observable. Only the .NET driver ever runs the
/// register phase — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor wholesale —
/// and the orchestrator re-registers the reported descriptor once with an authorization block
/// before any write, without which every seeded write is denied.
///
/// <para><b>The fixture.</b> Every language writes THREE rows — one partition — stamped with the
/// run's <c>--id-prefix</c> as <c>Marker</c>, its own <see cref="LabelFor"/> as <c>Label</c>, and
/// <c>Seq</c> 1, 2 and 3, reporting their server-assigned keys as <c>pattern_doc_1</c>..<c>3</c>.
/// Both read steps filter on the marker and partition by <c>Label</c> ordered by <c>Seq</c>, so
/// each seeded language is exactly one partition and no earlier run's rows can match.</para>
///
/// <para><b>The two patterns.</b> <c>match_pattern_scalar</c> is <c>A B+</c> with
/// <c>B AS Seq &gt; PREV(Seq)</c>, one row per match, measuring <c>n = COUNT(*)</c>,
/// <c>first_seq = FIRST(A.Seq)</c> and <c>last_seq = LAST(B.Seq)</c>: over one strictly increasing
/// partition of three rows that is exactly one match with <c>n = 3, first_seq = 1, last_seq = 3</c>
/// — values that depend on the filter, the partitioning AND the ordering all being honoured.
/// <c>match_pattern_similarity</c> is <c>A+</c> with <c>A AS SIMILARITY(Title, '…') IS NOT NULL</c>,
/// all rows per match, measuring the score <c>s</c>: every seeded row is scored and returned under
/// classifier <c>A</c> in match 1. Scores are never compared to absolute values — only finiteness
/// and the cosine range — because they belong to the embedding model, not to any client.</para>
///
/// <para><b>The expected sets are the harness's own accounting.</b> Both content assertions grade
/// against what the WRITE phase reported (<c>DriverRunner.KeysByLanguage</c>, read by
/// <see cref="ExpectedRows"/>), never against anything the read phase being judged reported.</para>
///
/// <para><b>The projection wait.</b> A TYPE_ROWS match reads its rows from StarRocks and resolves
/// <c>SIMILARITY</c> against the Qdrant object vectors, and a mapped write reaches both
/// asynchronously through the outbox — Qdrant only after the embedding model has vectorized
/// <c>Title</c>. Between the write and read phases the orchestrator polls its OWN
/// <c>MatchPattern</c> probe, whose pattern matches a row only once it is both visible and scoreable
/// and which reports each such row's <c>Id</c>, until every seeded row's key is among them. The check
/// is a superset, not an exact count: rows written under the marker by a language that did not
/// report all three keys are not expected, and must not stall every other language's wait. Expiry
/// is reported as a failed step on every language, worded as the harness's own precondition failing
/// and carrying no requirement ID.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the run seeded at least one row for
/// these pattern queries to match" assertion is this scenario's backstop, exactly as
/// <see cref="QueryScenario"/>'s is for the rest of the QRY axis: with an empty expectation both
/// content comparisons would agree with an empty result. It carries no requirement ID.</para>
/// </summary>
public sealed class MatchPatternScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    ObjectSearchService.ObjectSearchServiceClient search,
    ProjectionWaiter? waiter = null,
    Action<string>? log = null)
{
    public const string Name = "match-pattern";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and matches. Relation-free on purpose.</summary>
    internal const string TypeName = "PatternDoc";

    /// <summary>The <c>[IversonMetadata]</c> property every pattern query filters on.</summary>
    internal const string MarkerProperty = "Marker";

    /// <summary>The integer property every pattern query orders by.</summary>
    internal const string SeqProperty = "Seq";

    /// <summary>
    /// The query text of every <c>SIMILARITY</c> term — the drivers' similarity step and the
    /// orchestrator's probe alike. Each seeded <c>Title</c> is <c>"a note about row pattern
    /// matching, part &lt;n&gt;"</c>, so the text is close to every row without equalling any.
    /// </summary>
    internal const string SimilarityText = "a note about row pattern matching";

    /// <summary>The <c>SIMILARITY</c> term, spelled exactly as the drivers spell it.</summary>
    internal const string SimilarityExpression = "SIMILARITY(Title, '" + SimilarityText + "')";

    /// <summary>The logical key names every driver reports its three seeded rows under, Seq 1..3.</summary>
    internal static readonly string[] RowKeyNames = ["pattern_doc_1", "pattern_doc_2", "pattern_doc_3"];

    internal const string RegisterStepName = "register_pattern_doc";
    internal const string WriteStepName = "write_pattern_docs";
    internal const string ScalarStepName = "match_pattern_scalar";
    internal const string SimilarityStepName = "match_pattern_similarity";

    /// <summary>
    /// The <c>Label</c> value a given language's driver stamps on its three rows — and therefore the
    /// partition key that language's rows form. Spelled once here and mirrored in each driver.
    /// </summary>
    internal static string LabelFor(string language) => $"pat-{language}";

    /// <summary>
    /// Why the wait is as patient as vector-search's: the probe is gated on the same embedding work
    /// (a row is scoreable only once <c>Title</c> is vectorized), and each attempt embeds the query
    /// text through the same backend that work is using.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(6);

    /// <summary>Why a MatchPattern read lags a write — the wait's <c>StoreExplanation</c>.</summary>
    internal const string StoreExplanation =
        "MatchPattern reads TYPE_ROWS from the StarRocks projection and scores SIMILARITY against " +
        "the Qdrant object vectors, both of which a mapped write reaches asynchronously through the " +
        "outbox — Qdrant only after the embedding model has vectorized the embedded field";

    private readonly ProjectionWaiter _waiter = waiter ?? new ProjectionWaiter(
        WaitTimeout, WaitInterval, StoreExplanation);

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        DriverContext context,
        string actingToken,
        CancellationToken ct = default)
    {
        if (languages.Count == 0)
            return [];

        var states = languages.ToDictionary(
            l => l, _ => new LanguageState(), StringComparer.OrdinalIgnoreCase);

        // ── register (dotnet only, register-once — see the class doc comment) ──────────────────
        var registerOutcomes = await runner.RunPhaseAsync(Phase.Register, [RegisterLanguage], context, ct);
        var registerOutcome = registerOutcomes.Count > 0 ? registerOutcomes[0] : null;

        var (descriptorJson, registerFailure) = registerOutcome switch
        {
            DriverPhaseOutcome.Success success => TryCaptureDescriptor(success.Document),
            DriverPhaseOutcome.Skipped skipped => (null, $"'{RegisterLanguage}' driver skipped: {skipped.Reason}"),
            DriverPhaseOutcome.Broken broken => (null,
                $"'{RegisterLanguage}' driver broke during the register phase (exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}"),
            _ => (null, $"'{RegisterLanguage}' produced no register-phase outcome"),
        };

        if (registerFailure is not null)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S13 match-pattern's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S13 match-pattern's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language seeds one three-row partition carrying the marker ──
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            var step = document.Steps.FirstOrDefault(s => s.Name == WriteStepName);
            if (step is null)
            {
                state.Assertions.Add(Assertion.Fail($"step '{WriteStepName}'",
                    "the driver reported no such step, so this language seeded no rows for these patterns to match"));
                continue;
            }

            state.Assertions.Add(Assertion.From(
                $"step '{WriteStepName}' succeeded", step.Ok, step.Error ?? "ok"));
        }

        // The expectation: taken from the runner's accumulated key map rather than from any
        // read-phase report, so the content assertions grade against something the thing being
        // judged did not produce.
        var expectedRows = ExpectedRows(runner.KeysByLanguage);
        var expectedKeys = expectedRows.Keys.ToHashSet();

        // ── wait until every seeded row is visible AND scoreable ───────────────────────────────
        var marker = context.IdPrefix;
        var wait = await _waiter.WaitAsync(
            $"the {expectedKeys.Count} '{TypeName}' row(s) marked '{marker}' (rows and their Title vectors)",
            async token =>
            {
                var scoreable = await ScoreableIdsAsync(marker, actingToken, token);
                var missing = MissingFromProbe(expectedKeys, scoreable);
                return ProjectionReady(expectedKeys, scoreable)
                    ? ProbeOutcome.Ready(
                        $"all {expectedKeys.Count} seeded row(s) scoreable ({scoreable.Count} marked row(s) visible)")
                    : ProbeOutcome.NotYet(
                        $"{missing} of {expectedKeys.Count} seeded row(s) not yet scoreable " +
                        $"({scoreable.Count} marked row(s) visible)");
            },
            ct);

        log?.Invoke($"  projection wait: {(wait.Satisfied ? "satisfied" : "TIMED OUT")} " +
                    $"after {wait.Elapsed.TotalSeconds:0.0}s over {wait.Attempts} attempt(s) — {wait.LastDetail}");

        if (!wait.Satisfied)
        {
            // A shared precondition, so it fails every row: running the read phase after it would
            // grade five client libraries on rows the stores cannot yet see.
            foreach (var (language, state) in states)
            {
                if (state.Terminal is not null) continue;
                state.Assertions.Add(Assertion.Fail(
                    $"{language}: the seeded rows reached both stores within the bounded wait",
                    wait.TimeoutDetail));
            }

            return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
        }

        // ── read: every alive language issues the same two pattern queries ─────────────────────
        return GradeReads(states, await RunPhaseAsync(Phase.Read, states, context, ct), expectedRows);
    }

    /// <summary>
    /// Wires the read phase's documents through <see cref="Judge"/> and into cells. Extracted from
    /// <see cref="RunAsync"/> — and internal — because the wiring is exactly as safety-critical as
    /// the judgement: drop the <see cref="Judge"/> call below and every QRY-005..007 assertion
    /// silently stops reaching a cell. That mutation must redden a named test.
    /// </summary>
    internal static IReadOnlyList<ReportCell> GradeReads(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> reads,
        IReadOnlyDictionary<Guid, string> expectedRows)
    {
        foreach (var (language, document) in reads)
            states[language].Assertions.AddRange(Judge(language, expectedRows, document));

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>One reported output row: <c>{"matchNumber":…,"classifier":"…","data":{…}}</c>.</summary>
    internal sealed record PatternRow(double MatchNumber, string Classifier, JsonElement Data);

    /// <summary>
    /// Judges one language's read phase against the write phase's accounting,
    /// <paramref name="expectedRows"/> (every seeded row key → the language that wrote it). Pure
    /// over reported data (no I/O), so every branch below is exercisable from a unit test.
    ///
    /// Every assertion fires unconditionally: a missing step, a failed step and a malformed report
    /// each become an explicit failure naming its consequence, never a silent skip, so no QRY
    /// requirement can be discharged vacuously.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(
        string language,
        IReadOnlyDictionary<Guid, string> expectedRows,
        PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the backstop (uncited by design — see the class doc comment) ──────────────────────
        assertions.Add(Assertion.From(
            $"{language}: the run seeded at least one row for these pattern queries to match",
            expectedRows.Count > 0,
            $"the write phase produced {expectedRows.Count} complete row key(s); with none, an empty " +
            "scalar result and an empty similarity result would both compare equal to the expectation"));

        var scalarStep = document.Steps.FirstOrDefault(s => s.Name == ScalarStepName);
        var similarityStep = document.Steps.FirstOrDefault(s => s.Name == SimilarityStepName);

        assertions.Add(Reachable(language, "scalar", ScalarStepName, scalarStep));
        assertions.Add(Reachable(language, "SIMILARITY", SimilarityStepName, similarityStep));
        assertions.Add(JudgeScalar(language, expectedRows, scalarStep));
        assertions.Add(JudgeSimilarity(language, expectedRows, similarityStep));

        return assertions;
    }

    private static Assertion Reachable(string language, string which, string stepName, StepResult? step)
    {
        var name = $"{language}: a {which} row pattern match is reachable through the client's public API";
        return step is null
            ? Assertion.Fail(name, $"the driver reported no '{stepName}' step", Requirements.QryMatchPatternReachable)
            : Assertion.From(name, step.Ok, step.Error ?? "ok", Requirements.QryMatchPatternReachable);
    }

    /// <summary><c>IVC-QRY-006</c>: exactly one match per seeded language, with the right measures.</summary>
    private static Assertion JudgeScalar(
        string language, IReadOnlyDictionary<Guid, string> expectedRows, StepResult? step)
    {
        var name = $"{language}: the scalar row pattern returned exactly one expected match per seeded language";
        var (rows, unusable) = UsableRows(ScalarStepName, step);
        if (rows is null)
            return Assertion.Fail(name, unusable, Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

        var expectedLabels = ExpectedLabels(expectedRows);
        var problems = new List<string>();

        foreach (var label in expectedLabels.Order(StringComparer.Ordinal))
        {
            var matches = rows.Where(r => StringField(r.Data, "Label") == label).ToList();
            if (matches.Count != 1)
            {
                problems.Add($"'{label}': {matches.Count} match(es), expected exactly 1");
                continue;
            }

            var match = matches[0];
            if (match.MatchNumber != 1)
                problems.Add($"'{label}': matchNumber {match.MatchNumber}, expected 1");

            foreach (var (measure, expected) in new[] { ("n", 3.0), ("first_seq", 1.0), ("last_seq", 3.0) })
            {
                var actual = NumberField(match.Data, measure);
                if (actual != expected)
                {
                    problems.Add(actual is null
                        ? $"'{label}': measure '{measure}' is absent or not a number, expected {expected}"
                        : $"'{label}': measure '{measure}' = {actual}, expected {expected}");
                }
            }
        }

        var unexpected = rows
            .Select(r => StringField(r.Data, "Label") ?? "(no Label)")
            .Where(l => !expectedLabels.Contains(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unexpected.Count > 0)
            problems.Add($"returned-but-unseeded label(s): [{string.Join(", ", unexpected)}]");

        return Assertion.From(
            name,
            problems.Count == 0,
            problems.Count == 0
                ? $"{rows.Count} match(es), one per seeded language, each with n = 3, first_seq = 1, last_seq = 3"
                : string.Join("; ", problems),
            Requirements.QryMatchPatternReturnsExactlyExpectedMatches);
    }

    /// <summary>
    /// <c>IVC-QRY-007</c>: exactly the seeded rows, each classified <c>A</c> in match 1, carrying a
    /// finite score in [-1, 1], under the label of the language that wrote it.
    /// </summary>
    private static Assertion JudgeSimilarity(
        string language, IReadOnlyDictionary<Guid, string> expectedRows, StepResult? step)
    {
        var name = $"{language}: the SIMILARITY row pattern scored exactly the seeded rows";
        var (rows, unusable) = UsableRows(SimilarityStepName, step);
        if (rows is null)
            return Assertion.Fail(name, unusable, Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);

        var problems = new List<string>();
        var returned = new HashSet<Guid>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = Guid.TryParse(StringField(row.Data, "Id"), out var parsed) ? parsed : (Guid?)null;
            var where = id is { } k ? $"row {i} (Id {k})" : $"row {i}";

            if (id is null)
                problems.Add($"{where}: 'Id' is absent or not a UUID");
            else
                returned.Add(id.Value);

            if (row.Classifier != "A")
                problems.Add($"{where}: classifier '{row.Classifier}', expected 'A'");

            if (row.MatchNumber != 1)
                problems.Add($"{where}: matchNumber {row.MatchNumber}, expected 1");

            if (ScoreProblem(row.Data) is { } scoreProblem)
                problems.Add($"{where}: {scoreProblem}");

            if (id is { } key && expectedRows.TryGetValue(key, out var writer)
                && StringField(row.Data, "Label") is var label && label != LabelFor(writer))
            {
                problems.Add($"{where}: Label '{label ?? "(none)"}', but '{LabelFor(writer)}' wrote that row");
            }
        }

        var missing = expectedRows.Keys.Where(k => !returned.Contains(k)).ToList();
        var unexpected = returned.Where(k => !expectedRows.ContainsKey(k)).ToList();
        if (missing.Count > 0 || unexpected.Count > 0)
            problems.Add($"seeded-but-absent: [{Join(missing)}]; returned-but-unseeded: [{Join(unexpected)}]");

        return Assertion.From(
            name,
            problems.Count == 0,
            problems.Count == 0
                ? $"{rows.Count} row(s), matching the {expectedRows.Count} the write phase seeded, each scored"
                : string.Join("; ", problems),
            Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);
    }

    /// <summary>
    /// A score is a JSON number that is finite and a cosine. System.Text.Json refuses to read an
    /// overflowing literal such as <c>1e400</c> as a double, and a driver serializing NaN or an
    /// infinity must spell it as a string — so "not a finite number" covers all three.
    /// </summary>
    private static string? ScoreProblem(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var s))
            return "has no score 's'";

        if (s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out var score) || !double.IsFinite(score))
            return $"score 's' is not a finite number (got {s.GetRawText()})";

        return score is < -1 or > 1 ? $"score 's' = {score} is outside [-1, 1]" : null;
    }

    /// <summary>The step's rows, or why there are none to judge.</summary>
    private static (IReadOnlyList<PatternRow>? Rows, string Unusable) UsableRows(string stepName, StepResult? step)
    {
        if (step is null)
            return (null, $"the driver reported no '{stepName}' step, so there is no result to judge");

        if (!step.Ok)
            return (null, $"the '{stepName}' step failed, so there is no result to judge: {step.Error ?? "(no error)"}");

        var (rows, problem) = ReadRows(step.Entity);
        return rows is null ? (null, $"the '{stepName}' step's report is malformed: {problem}") : (rows, "");
    }

    /// <summary>
    /// The output rows a driver's read step reported, in stream order, out of
    /// <c>{"rows":[{"matchNumber":…,"classifier":"…","data":{…}}, …]}</c>. All five drivers emit this
    /// same shape, with <c>data</c>'s keys verbatim from the server. Unlike the vector-search
    /// readers this one does not degrade to an empty set: a malformed report yields
    /// <c>Rows = null</c> and names the malformation, because an empty result is a legitimate
    /// (and gradeable) answer while an unreadable one is not. Nothing here judges.
    /// </summary>
    internal static (IReadOnlyList<PatternRow>? Rows, string? Problem) ReadRows(JsonElement? entity)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return (null, "the entity is absent or not a JSON object");

        if (!document.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array)
            return (null, "the entity has no 'rows' array");

        var rows = new List<PatternRow>();
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return (null, $"rows[{i}] is not an object");

            if (!element.TryGetProperty("matchNumber", out var matchNumber)
                || matchNumber.ValueKind != JsonValueKind.Number
                || !matchNumber.TryGetDouble(out var number))
            {
                return (null, $"rows[{i}] has no numeric 'matchNumber'");
            }

            if (!element.TryGetProperty("classifier", out var classifier) || classifier.ValueKind != JsonValueKind.String)
                return (null, $"rows[{i}] has no string 'classifier'");

            if (!element.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (null, $"rows[{i}] has no 'data' object");

            rows.Add(new PatternRow(number, classifier.GetString()!, data));
            i++;
        }

        return (rows, null);
    }

    private static string? StringField(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? NumberField(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                                 && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>
    /// Every seeded row key → the language that wrote it, for each language whose write phase
    /// reported ALL THREE <see cref="RowKeyNames"/> as UUIDs. A language that reported fewer did not
    /// seed its partition, so it is expected in neither comparison. Keys are parsed as
    /// <see cref="Guid"/> so the five languages' UUID spellings are comparable. This is the
    /// harness's own accounting of what it seeded — the independent expectation both content
    /// assertions grade against.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, string> ExpectedRows(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage)
    {
        var rows = new Dictionary<Guid, string>();
        foreach (var (language, byName) in keysByLanguage)
        {
            var keys = new List<Guid>();
            foreach (var keyName in RowKeyNames)
            {
                if (byName.TryGetValue(keyName, out var raw) && Guid.TryParse(raw, out var parsed))
                    keys.Add(parsed);
            }

            if (keys.Count != RowKeyNames.Length)
                continue;

            foreach (var key in keys)
                rows[key] = language;
        }

        return rows;
    }

    private static IReadOnlySet<string> ExpectedLabels(IReadOnlyDictionary<Guid, string> expectedRows) =>
        expectedRows.Values.Select(LabelFor).ToHashSet(StringComparer.Ordinal);

    // ── the orchestrator's own probe ─────────────────────────────────────────────────────────

    /// <summary>
    /// The projection-wait predicate: ready when every expected key is among the ids the probe
    /// returned. A SUPERSET check, not an exact count — the probe also sees rows written under the
    /// run marker by a language that did not report all three keys (so is not expected), and those
    /// must not stall every other language's wait. <paramref name="expected"/> empty is deliberately
    /// NOT ready — no language seeded anything, so satisfying the wait would let the read phase
    /// grade against nothing and the harness must instead report its own precondition failing.
    /// </summary>
    internal static bool ProjectionReady(IReadOnlyCollection<Guid> expected, IReadOnlySet<Guid> visible) =>
        expected.Count > 0 && MissingFromProbe(expected, visible) == 0;

    /// <summary>How many expected keys the probe has not yet returned — the wait's progress detail.</summary>
    internal static int MissingFromProbe(IReadOnlyCollection<Guid> expected, IReadOnlySet<Guid> visible) =>
        expected.Count(key => !visible.Contains(key));

    /// <summary>
    /// The ids of the marked rows that are visible AND scoreable, through the orchestrator's OWN
    /// <c>MatchPattern</c> call: pattern <c>A</c> with <c>A AS SIMILARITY(…) IS NOT NULL</c>, one
    /// row per match, measuring <c>id = FIRST(A.Id)</c>, so each response is one row whose Title
    /// vector exists and carries that row's <c>Id</c>. This is a projection probe, not a
    /// conformance observation: nothing it returns is ever compared against a client's report, so
    /// a driver cannot manufacture readiness.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ScoreableIdsAsync(string marker, string actingToken, CancellationToken ct)
    {
        var request = new MatchPatternRequest
        {
            TypeName = TypeName,
            Source = PatternRowSource.TypeRows,
            Where =
            {
                new SearchClause
                {
                    Property = MarkerProperty,
                    Operator = SearchOperator.Equals,
                    ClauseType = SearchClauseType.Filter,
                    Value = new SearchValue { StringVal = marker },
                },
            },
            OrderBy = { new SearchSort { Property = SeqProperty } },
            Pattern = "A",
            Define = { new NamedExpr { Name = "A", Expr = SimilarityExpression + " IS NOT NULL" } },
            Measures = { new NamedExpr { Name = "id", Expr = "FIRST(A.Id)" } },
            RowsPerMatch = RowsPerMatch.OneRow,
            Limit = 10_000,
        };

        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        using var call = search.MatchPattern(request, headers, cancellationToken: ct);

        var ids = new HashSet<Guid>();
        while (await call.ResponseStream.MoveNext(ct))
        {
            if (call.ResponseStream.Current.Data?.Fields.TryGetValue("id", out var id) == true
                && Guid.TryParse(id.StringValue, out var parsed))
            {
                ids.Add(parsed);
            }
        }

        return ids;
    }

    // ── register-phase descriptor capture ────────────────────────────────────────────────────

    internal static (JsonElement? Descriptor, string? Failure) TryCaptureDescriptor(PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == RegisterStepName);
        if (step is null)
            return (null, $"the {RegisterLanguage} driver reported no '{RegisterStepName}' step");

        if (!step.Ok)
            return (null, step.Error ?? "registration failed");

        if (step.TypeDescriptor is not { } json)
            return (null, "typeDescriptor was null on the register step");

        try
        {
            // Parsed but discarded: parsing is the validation — the Reregistrar needs the raw JSON,
            // and a descriptor that cannot be parsed here would fail there with a worse message.
            Verifier.ParseDescriptor(json);
            return (json, null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ── phase plumbing (mirrors VectorSearchScenario's) ──────────────────────────────────────

    private async Task<IReadOnlyList<(string Language, PhaseDocument Document)>> RunPhaseAsync(
        Phase phase, Dictionary<string, LanguageState> states, DriverContext context, CancellationToken ct)
    {
        var alive = ScenarioCells.Alive(states).ToList();
        if (alive.Count == 0)
            return [];

        log?.Invoke($"  phase {PhaseNames.ToToken(phase)}: {string.Join(", ", alive)}");

        var documents = new List<(string, PhaseDocument)>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(phase, alive, context, ct))
        {
            reported.Add(outcome.Language);
            var state = states[outcome.Language];
            switch (outcome)
            {
                case DriverPhaseOutcome.Success success:
                    documents.Add((outcome.Language, success.Document));
                    break;
                case DriverPhaseOutcome.Skipped skipped:
                    state.Terminal = ReportCell.Skip(outcome.Language, Name, skipped.Reason, state.Assertions);
                    break;
                case DriverPhaseOutcome.Broken broken:
                    state.Terminal = ReportCell.Fail(outcome.Language, Name,
                        $"driver broke during the {PhaseNames.ToToken(phase)} phase " +
                        $"(exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}", state.Assertions);
                    break;
            }
        }

        foreach (var language in alive.Where(l => !reported.Contains(l)))
        {
            states[language].Terminal = ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language", states[language].Assertions);
        }

        return documents;
    }

    private static string Join(IEnumerable<Guid> keys) => string.Join(", ", keys.Select(k => k.ToString()));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
```

- [ ] **Step 5: Register and dispatch the scenario, and correct the `--help` text**

`Iverson.Server/Iverson.ClientConformance/Program.cs`. Make four replacements.

1. `recognizedScenarios` (lines 66-72).

   Before:
```csharp
        TenantRejectedScenario.Name, ModelRejectedScenario.Name, InheritedModelScenario.Name,
    ];
```
   After:
```csharp
        TenantRejectedScenario.Name, ModelRejectedScenario.Name, InheritedModelScenario.Name,
        MatchPatternScenario.Name,
    ];
```

2. Construction (line 127).

   Before:
```csharp
    var inheritedModel = new InheritedModelScenario(runner, log: Console.WriteLine);
```
   After:
```csharp
    var inheritedModel = new InheritedModelScenario(runner, log: Console.WriteLine);
    var matchPattern = new MatchPatternScenario(
        runner, new Reregistrar(mapping),
        new ObjectSearchService.ObjectSearchServiceClient(channel),
        log: Console.WriteLine);
```

3. Dispatch, after the `inherited-model` block (lines 250-258).

   Before:
```csharp
                     languages, BuildContext(InheritedModelScenario.Name), actingToken))
        {
            report.Add(cell);
        }
    }
```
   After:
```csharp
                     languages, BuildContext(InheritedModelScenario.Name), actingToken))
        {
            report.Add(cell);
        }
    }

    // As with the blocks above, this dispatch — not `recognizedScenarios` — is what actually runs
    // the scenario.
    if (scenarios.Contains(MatchPatternScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{MatchPatternScenario.Name}'...");
        foreach (var cell in await matchPattern.RunAsync(
                     languages, BuildContext(MatchPatternScenario.Name), actingToken))
        {
            report.Add(cell);
        }
    }
```

4. `--help` text (line 314). `TokenBroker.cs:59` and `:80` read these two variables with `RequireEnv`, which throws when they are unset; every other acting-user and other-tenant variable goes through `Env` with a default.

   Before:
```csharp
      Every IVERSON_ACTING_USER_* has a working compose default (TokenBroker.cs).
```
   After:
```csharp
      IVERSON_ACTING_USER_BYPASS_PASSWORD and IVERSON_OTHER_TENANT_PASSWORD have NO default and
      are required (TokenBroker.cs throws without them): the compose stack's Authentik passwords
      are generated per stack, so read them out of Iverson.Server/.env — IVERSON_BYPASS_PASSWORD
      and IVERSON_SMOKE_TEST_PASSWORD respectively. Every other IVERSON_ACTING_USER_* and
      IVERSON_OTHER_TENANT_* variable has a working compose default.
```

- [ ] **Step 6: Update the standard**

`docs/standards/iverson-client-standard.md`. Make these replacements, each of which matches exactly once. Line numbers refer to the file before any edit.

1. The Active count (line 78).

   Before:
```markdown
declares 46 `Active` requirements across nine axes; each takes a const in `Requirements.cs` and is
```
   After:
```markdown
declares 49 `Active` requirements across nine axes; each takes a const in `Requirements.cs` and is
```

2. QRY table rows 005-007, appended after the `IVC-QRY-004` row (line 665).

   Before:
```markdown
| IVC-QRY-004 | Active | Behaviour | An aggregation over a filtered set reports a value computed from exactly the rows that filter matches |
```
   After:
```markdown
| IVC-QRY-004 | Active | Behaviour | An aggregation over a filtered set reports a value computed from exactly the rows that filter matches |
| IVC-QRY-005 | Active | Capability | A row pattern match is reachable through the client's public API |
| IVC-QRY-006 | Active | Behaviour | A row pattern match over scalar columns returns exactly one match per partition the pattern matches, with measures computed from exactly that partition's ordered rows |
| IVC-QRY-007 | Active | Behaviour | A row pattern match whose definitions use `SIMILARITY` returns every row its filter admits, each with a finite score in [-1, 1] |
```

3. QRY prose (lines 693-694): new paragraphs for 005-007 before the closing "deliberately not authored" sentence, which is reworded to say `Search` sort order.

   Before:
```markdown
Pagination, sort order, joins, bucketing aggregations and `HAVING` are deliberately not authored
here; see the Coverage table below.
```
   After:
```markdown
`IVC-QRY-005` is the reachability half of row pattern matching (`MatchPattern`, SQL:2016
`MATCH_RECOGNIZE`) and `IVC-QRY-006`/`IVC-QRY-007` its content half, split for the reason the
search and aggregation requirements are: `MatchPattern` is its own RPC, and a client can reach it
yet carry the wrong rows back. The `match-pattern` scenario has every language seed one three-row
partition (`Label = "pat-<lang>"`, `Seq` 1..3) under a run-unique marker, and every client then
issues the same two patterns over all seeded partitions, filtered on that marker, partitioned by
`Label` and ordered by `Seq`.

`IVC-QRY-006` uses a scalar pattern, `A B+` with `B AS Seq > PREV(Seq)`, one row per match. Over
one strictly increasing three-row partition that is exactly one match with `n = COUNT(*) = 3`,
`first_seq = FIRST(A.Seq) = 1` and `last_seq = LAST(B.Seq) = 3`. The measures depend on the
filter, the partitioning and the ordering all reaching the server unchanged, so a client that
drops or reorders any of them disagrees with the other four. Each seeded language must yield
exactly one such match, and no label that no seeded language wrote may appear.

`IVC-QRY-007` uses a `SIMILARITY` pattern, `A+` with
`A AS SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL`, all rows per match. The
set of returned `Id`s must equal exactly the row keys the write phase reported, in both directions.
Every row must carry classifier `A`, match number 1, the `Label` of the language that wrote it,
and a score `s` that is a finite number in [-1, 1]. Scores are never compared to absolute values,
because they belong to the embedding model and not to any client.

A TYPE_ROWS pattern reads its rows from StarRocks and resolves `SIMILARITY` against the Qdrant
object vectors, so the `match-pattern` scenario's bounded wait polls the orchestrator's own
`MatchPattern` probe. That probe matches a row only once it is both visible and scoreable, and it
reports each such row's `Id`. The wait ends once every row key the write phase reported is among
those ids. Rows from a language that did not report all three keys do not hold up the wait.

Pagination, `Search` sort order, joins, bucketing aggregations and `HAVING` are deliberately not
authored here; see the Coverage table below.
```

4. QRY Coverage ledger (line 704): one Covered area claims all three ids, and the Deferred "Pagination and sort order" row is reworded so it no longer claims that no ordering is observed.

   Before:
```markdown
| Pagination and sort order | Deferred | Every client's query builder can express paging and sort (`page`/`limit`/`offset`, `orderBy`), but the `query` scenario seeds one row per language — far below any page boundary — so no assertion observes a page boundary or an ordering, and no requirement constrains them. Authoring them needs a seed set large enough that a wrong page size or a dropped sort is observable, which is a scenario change, not a wording change. |
```
   After:
```markdown
| Row pattern matching | Covered | IVC-QRY-005, IVC-QRY-006, IVC-QRY-007 |
| Pagination and `Search` sort order | Deferred | Every client's query builder can express paging and sort for `Search` (`page`/`limit`/`offset`, `orderBy`), but the `query` scenario seeds one row per language — far below any page boundary — so no assertion observes a `Search` page boundary or a `Search` result ordering, and no requirement constrains them. Authoring them needs a seed set large enough that a wrong page size or a dropped sort is observable, which is a scenario change, not a wording change. `MatchPattern`'s `order_by` is not in this row: `IVC-QRY-006`'s measures are computed over the ordered partition, so a dropped or reversed ordering is observable there. `MatchPattern`'s output `limit` is never reached by the fixture and stays unconstrained. |
```

5. QRY Coverage ledger (line 707): the Deferred "Vector-backed query paths" row is reworded to name the one vector-backed path the axis now covers.

   Before:
```markdown
| Vector-backed query paths | Deferred | `SearchSimilar` and `SearchChunks` are served from Qdrant rather than StarRocks and belong to the `VEC` axis, not this one. |
```
   After:
```markdown
| Vector-backed query paths | Deferred | `SearchSimilar` and `SearchChunks` are served from Qdrant rather than StarRocks and belong to the `VEC` axis, not this one. The one vector-backed path this axis does cover is `SIMILARITY` inside a TYPE_ROWS row pattern (`IVC-QRY-007`), which scores StarRocks rows against their Qdrant object vectors. No assertion observes a score's value or a CHUNKS-source pattern (`source = CHUNKS`), and no requirement constrains either. |
```

6. QRY backstop note (line 716).

   Before:
```markdown
this query to match" assertion is therefore `QRY`'s backstop. It fires unconditionally, on every
```
   After:
```markdown
this query to match" assertion is therefore `QRY`'s backstop, and `MatchPatternScenario.Judge`'s
"the run seeded at least one row for these pattern queries to match" assertion is the same backstop
for `IVC-QRY-006` and `IVC-QRY-007`. It fires unconditionally, on every
```

- [ ] **Step 7: Bring the coverage gate's stale count up to date**

`Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs`. Two failure messages quote the registry's size, so change 46 to 49 in each. Only the message text changes; no assertion reads the number.

1. Line 1320.

   Before:
```csharp
            "Requirements.cs DECLARES every const, so reading it back reports all 46 cited by "
```
   After:
```csharp
            "Requirements.cs DECLARES every const, so reading it back reports all 49 cited by "
```

2. Line 1335.

   Before:
```csharp
            + "above is satisfied by a selection that excludes everything and reports all 46 uncited");
```
   After:
```csharp
            + "above is satisfied by a selection that excludes everything and reports all 49 uncited");
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "FullyQualifiedName~MatchPatternScenarioTests"`
Expected: PASS (55 tests).

Run: `dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj`
Expected: PASS (609 tests: the 554 before this task plus 55). This run includes `RequirementsCoverageGateTests`. Check1 matches the three new Active ids to the three consts, Check2 finds each const cited in `MatchPatternScenario.cs`, and Check4 finds each id claimed by exactly one Covered area, "Row pattern matching".

Run: `dotnet build Iverson.Server/Iverson.ClientConformance/Iverson.ClientConformance.csproj`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet run --project Iverson.Server/Iverson.ClientConformance -- --help`
Expected: the Credentials section ends:
```text
  IVERSON_ACTING_USER_BYPASS_PASSWORD and IVERSON_OTHER_TENANT_PASSWORD have NO default and
  are required (TokenBroker.cs throws without them): the compose stack's Authentik passwords
  are generated per stack, so read them out of Iverson.Server/.env — IVERSON_BYPASS_PASSWORD
  and IVERSON_SMOKE_TEST_PASSWORD respectively. Every other IVERSON_ACTING_USER_* and
  IVERSON_OTHER_TENANT_* variable has a working compose default.
  Full procedure: docs/runbooks/client-conformance-matrix.md
```

Mutation checks run against this code, each restored afterwards:
- Flipping `if (actual != expected)` to `==` in `JudgeScalar` fails 9 of the 55 `MatchPatternScenarioTests`.
- Replacing both `Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow` citations with `null` fails `Check2_EveryRegistryConst_IsCitedByAssertionCodeOutsideRequirementsAndTests` and `Check2_GradedTheFullInputSet_NotANarrowedSubset`, naming the const.
- Dropping `IVC-QRY-006` from the "Row pattern matching" ledger row fails `Check4_AxisCoverageLedgers_BindClaimedAreasToActiveRequirements` with "'IVC-QRY-006' (axis 'QRY') is Active but claimed by no Covered area".
- Deleting the `Judge` call in `GradeReads` fails `GradeReads_EachLanguagesJudgement_ReachesItsOwnCell`.
- Appending `&& visible.Count == expected.Count` to `ProjectionReady`, which reintroduces the exact-count check, fails `ProjectionReady_ExtraIdsFromAPartiallySeededLanguage_DoNotBlockReadiness`.

- [ ] **Step 9: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance/Scenarios/MatchPatternScenario.cs \
        Iverson.Server/Iverson.ClientConformance/Requirements.cs \
        Iverson.Server/Iverson.ClientConformance/Program.cs \
        Iverson.Server/Iverson.ClientConformance.Tests/MatchPatternScenarioTests.cs \
        Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs
git add -f docs/standards/iverson-client-standard.md
git commit -m "grade row pattern matching across the five clients in the conformance harness

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 8: the five conformance drivers — `PatternDoc` and the `match-pattern` steps

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs`
- Create: `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/PatternDoc.java`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs:65` (scenario constant), `:76` (label constant), `:77-79` (`supportedScenarios`), `:167-170` (dispatch branch after `ModelInheritedScenario`'s), `:534` (`RunMatchPatternAsync` before the S9 error-contract section)
- Modify: `Iverson.Clients/Python/conformance/driver.py:39` (imports), `:41-44` (models import), `:66-69` (`SCENARIOS`), `:85` (label constant), `:440` (`match_rows_to_json` before `main`), `:694` (three `match-pattern` branches before the error-contract register branch)
- Modify: `Iverson.Clients/Python/conformance/models.py:139` (`PatternDoc` after `VectorDoc`)
- Modify: `Iverson.Clients/TypeScript/conformance/driver.ts:20`, `:30`, `:33` (imports), `:58-61` (`SCENARIOS`), `:77` (label constant), `:147` (`matchRowsToReport` before `step`), `:841` (two `match-pattern` branches before the catch-all `phase === 'write'` branch)
- Modify: `Iverson.Clients/TypeScript/conformance/models.ts:202` (`PatternDoc` after `VectorDoc`)
- Modify: `Iverson.Clients/Go/conformance/main.go:75` (`supportedScenarios`), `:97` (label constant and `matchRowsReport` after the vector const block), `:688` (two `match-pattern` cases before the identity write case)
- Modify: `Iverson.Clients/Go/conformance/models.go:125` (`PatternDoc` after `VectorDoc`)
- Modify: `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java:18`, `:20`, `:24` (imports), `:100-104` (scenario constant and `SUPPORTED_SCENARIOS`), `:126` (label constant), `:205` (dispatch block before identity's), `:750` (S13 methods before the `write` section)

**Interfaces:**
- Consumes: the SDK APIs from Tasks 2–6.
  - .NET: `Query.MatchPattern<T>()`, `MatchPatternBuilder`, `EntityCoordinator<T>.MatchPatternAsync` and `MatchPatternResult`.
  - Java: `Query.matchPattern`, `MatchPatternBuilder`, `EntityCoordinator.matchPattern` and `EntityCoordinator.MatchPatternResult`.
  - Python: `iverson_client.match_pattern.match_pattern` and `EntityCoordinator.match_pattern` → `MatchPatternResult`.
  - TypeScript: `matchPattern` from `src/match-pattern.ts`, `IversonClient.matchPattern` and `MatchPatternResult`.
  - Go: `iverson.NewMatchPattern`, `EntityCoordinator[T].MatchPattern` and `iverson.MatchPatternResult`.
  - Task 1's regenerated enums: `RowsPerMatch` (.NET `OneRow`/`AllRowsShowEmpty`; Java, Python and TS `ONE_ROW`/`ALL_ROWS_SHOW_EMPTY`; Go `pb.RowsPerMatch_ONE_ROW`/`pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY`).
  - Task 7's scenario literals, which must match `MatchPatternScenario`:
    - scenario `match-pattern`, type `PatternDoc`;
    - `LabelFor(language)` = `pat-<lang>`;
    - step names `register_pattern_doc`, `write_pattern_docs`, `match_pattern_scalar`, `match_pattern_similarity`;
    - key names `pattern_doc_1`..`pattern_doc_3`;
    - the title prefix `a note about row pattern matching`.
- Produces: the driver phase documents that Task 7's orchestrator reads.
  - `register` (.NET only in a harness run) → `register_pattern_doc` carrying the `PatternDoc` `typeDescriptor`.
  - `write` → `write_pattern_docs` carrying `keys` `{"pattern_doc_1": id1, "pattern_doc_2": id2, "pattern_doc_3": id3}`, taken from the server-returned keys.
  - `read` → `match_pattern_scalar` and `match_pattern_similarity`, each carrying `entity` `{"rows":[{"matchNumber":…,"classifier":"…","data":{…}}, …]}` in stream order, with `data` keys verbatim.

Placement rules:
- Every driver follows its own `vector-search` precedent.
- Only .NET and Python have a register branch. Python's is for hand runs; TypeScript, Go and Java have never had a vector-search register branch.
- Python, TypeScript and Go have catch-all crud-roundtrip `register`/`write`/`read` branches (Python `:932`/`:1012`/`:1063`, TS `:628`/`:841`/`:898`, Go `:1099`/`:1138`/`:1202`). The new branches sit right after the vector-search ones, so they come before every catch-all.
- Write steps write the three rows in sequence and stop at the first thrown error. Every key the server returned is still reported. .NET's `PostMappedAsync` signals a refusal by returning null plus a logged error, so there the remaining rows are still attempted.

- [ ] **Step 1: .NET — `PatternDoc` and `RunMatchPatternAsync`**

`Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs`:
```csharp
using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S13 <c>match-pattern</c>'s subject type. Every one of the five drivers declares the same type
/// name and shape; only the .NET driver ever registers it (register-once rule, as for S7's
/// <c>VectorDoc</c>), and every driver writes three rows into it and then matches over them.
///
/// Deliberately relation-free and chunk-free: both requests read TYPE_ROWS, and no assertion
/// observes a chunk.
///
/// <list type="bullet">
/// <item><description><c>Marker</c> carries the run's <c>--id-prefix</c> and is the property both
/// requests pre-filter on. It is <c>[IversonMetadata]</c> exactly as on <c>VectorDoc</c>.</description>
/// </item>
/// <item><description><c>Label</c> is the row's per-language identity and the PARTITION BY column,
/// so each language's three rows form one partition. Its spelling (<c>pat-&lt;lang&gt;</c>) must
/// match the orchestrator's <c>MatchPatternScenario</c>.</description></item>
/// <item><description><c>Seq</c> is the ORDER BY column and the value <c>DEFINE B AS Seq &gt;
/// PREV(Seq)</c> compares; an <c>int</c> so it registers as CLR_INT32.</description></item>
/// <item><description><c>Title</c> is the <c>[IversonEmbedding]</c> property the similarity
/// request's <c>SIMILARITY(Title, …)</c> scores.</description></item>
/// </list>
/// </summary>
[IversonEntity]
public class PatternDoc
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    [IversonMetadata] public string Marker { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Seq { get; set; }
    [IversonEmbedding] public string Title { get; set; } = string.Empty;
}
```

In `Program.cs`, insert after `const string ModelInheritedScenario = "model-inherited";` (line 65):
```csharp
// match-pattern (S13): register (this driver only, register-once), write, read. Seeds three
// PatternDoc rows (Seq 1..3) carrying the run's marker, then issues a scalar row pattern match and
// a SIMILARITY-backed one through the client library's own MatchPatternBuilder.
const string MatchPatternScenario = "match-pattern";
```

Insert after `const uint VectorTopK = 50;` (line 76):
```csharp
// The Label every PatternDoc row this driver writes carries: the PARTITION BY column, so this
// language's three rows form one partition. Must stay in step with
// Iverson.ClientConformance/Scenarios/MatchPatternScenario.cs's LabelFor.
const string PatternDocLabel = $"pat-{Language}";
```

Replace the `supportedScenarios` initializer (lines 77-79) with:
```csharp
var supportedScenarios = new[]
    { CrudRoundtripScenario, InteropScenario, SchemaCatalogScenario, QueryScenario, VectorSearchScenario,
      IdentityScenario, ErrorContractScenario, ModelRejectedScenario, ModelInheritedScenario,
      MatchPatternScenario };
```

Insert after the `else if (scenario == ModelInheritedScenario) { … }` branch (lines 167-170), before the final `else`:
```csharp
else if (scenario == MatchPatternScenario)
{
    await RunMatchPatternAsync();
}
```

Insert before `// ── S9 error-contract ───…` (line 534), followed by one blank line:
```csharp
// ── S13 match-pattern ────────────────────────────────────────────────────────────────────────
async Task RunMatchPatternAsync()
{
    switch (phase)
    {
        case "register":
        {
            // Only the .NET driver ever runs this phase for match-pattern (register-once rule; see
            // Scenarios/MatchPatternScenario.cs). Registered WITHOUT an authorization block — the
            // orchestrator re-registers it with one before any driver's write phase.
            capture.OnlySendTypeName = nameof(PatternDoc);
            var registerOutcome = await Run(async () =>
            {
                var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                await registrar.RegisterAllAsync();
            });
            capture.OnlySendTypeName = null;

            steps.Add(new StepResult(
                "register_pattern_doc",
                Ok: registerOutcome is null,
                Error: registerOutcome,
                TypeDescriptor: Json.Element(capture.Select(nameof(PatternDoc)))));
            break;
        }

        case "write":
        {
            // Three rows, Seq 1..3, stamped with the run's marker and this language's label. Each
            // server-returned key is reported (via `always`, so also when a later row failed) — it
            // is the orchestrator's expected-set accounting for the similarity match, and a row
            // seeded but never reported would silently shrink what every language is graded against.
            var keys = new Dictionary<string, string>();
            var written = new List<PatternDoc?>();
            await Step("write_pattern_docs",
                async result =>
                {
                    for (var seq = 1; seq <= 3; seq++)
                    {
                        var doc = await Coordinator<PatternDoc>().PostMappedAsync(new PatternDoc
                        {
                            TenantId = tenant,
                            OwnerId = ownerId,
                            Marker = idPrefix,
                            Label = PatternDocLabel,
                            Seq = seq,
                            Title = $"a note about row pattern matching, part {seq}",
                        });
                        if (doc is not null) keys[$"pattern_doc_{seq}"] = doc.Id.ToString();
                        written.Add(doc);
                    }
                    return result with { Entity = Json.Element(written) };
                },
                result => keys.Count > 0 ? result with { Keys = keys } : result);
            break;
        }

        case "read":
        {
            // Both requests are built with the client library's own builder API
            // (Query.MatchPattern) and executed through EntityCoordinator.MatchPatternAsync, never
            // through the generated stub. Every output row is reported in stream order with its
            // data keys verbatim; the orchestrator decides what they mean.
            await Step("match_pattern_scalar", async result =>
            {
                var pattern = Query.MatchPattern<PatternDoc>()
                    .Where("Marker", SearchOperator.Equals, idPrefix)
                    .PartitionBy("Label")
                    .OrderBy("Seq")
                    .Pattern("A B+")
                    .Define("B", "Seq > PREV(Seq)")
                    .Measure("n", "COUNT(*)")
                    .Measure("first_seq", "FIRST(A.Seq)")
                    .Measure("last_seq", "LAST(B.Seq)")
                    .RowsPerMatch(RowsPerMatch.OneRow)
                    .Limit(100);

                return result with { Entity = await MatchRowsAsync(pattern) };
            });

            await Step("match_pattern_similarity", async result =>
            {
                var pattern = Query.MatchPattern<PatternDoc>()
                    .Where("Marker", SearchOperator.Equals, idPrefix)
                    .PartitionBy("Label")
                    .OrderBy("Seq")
                    .Pattern("A+")
                    .Define("A", "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL")
                    .Measure("s", "SIMILARITY(Title, 'a note about row pattern matching')")
                    .RowsPerMatch(RowsPerMatch.AllRowsShowEmpty)
                    .Limit(100);

                return result with { Entity = await MatchRowsAsync(pattern) };
            });
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"unknown phase '{phase}' for scenario '{scenario}'");
            Environment.Exit(2);
            break;
    }

    // {"rows":[{"matchNumber":…,"classifier":…,"data":{…}}, …]} in stream order. The anonymous
    // members are spelled camelCase because Json.Element keeps declared names; data's keys are
    // the server's, untouched.
    async Task<JsonElement?> MatchRowsAsync(MatchPatternBuilder pattern)
    {
        var rows = new List<object>();
        await foreach (var row in Coordinator<PatternDoc>().MatchPatternAsync(pattern))
            rows.Add(new { matchNumber = row.MatchNumber, classifier = row.Classifier, data = row.Data });
        return Json.Element(new { rows });
    }
}
```

Run: `dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj`
Expected: `Build succeeded.`, `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 2: Python — `PatternDoc` and the three branches**

In `Iverson.Clients/Python/conformance/models.py`, insert after `VectorDoc` (whose last line, `    label: str = None`, is line 139). Leave two blank lines on each side:
```python
@iverson_entity
class PatternDoc:
    """S13 ``match-pattern``'s subject type. Every one of the five drivers declares the same type
    name and shape; only the .NET driver ever registers it (register-once rule, as for
    ``VectorDoc``), and every driver writes three rows into it and then matches over them.

    Deliberately relation-free and chunk-free: both requests read TYPE_ROWS. ``marker`` carries the
    run's ``--id-prefix`` and is metadata exactly as on ``VectorDoc``; ``label`` is the row's
    per-language identity and the PARTITION BY column, and its spelling must match
    ``MatchPatternScenario.LabelFor``; ``seq`` is the ORDER BY column that
    ``DEFINE B AS Seq > PREV(Seq)`` compares; ``title`` is the embedding source the similarity
    request's ``SIMILARITY(Title, ...)`` scores."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = None
    owner_id: str = None
    marker: str = iverson_metadata()
    label: str = None
    seq: int = None
    title: str = iverson_embedding()
```

In `conformance/driver.py`, insert after `from iverson_client.vector_search import chunks as chunks_builder, similar as similar_builder` (line 39):
```python
from iverson_client.match_pattern import match_pattern as match_pattern_builder
from iverson_client.generated import object_search_pb2 as search_pb
```

Replace the `from conformance.models import (…)` block (lines 41-44) with:
```python
from conformance.models import (
    ErrorDoc, ErrorUnregisteredDoc, IdentityDoc, PatternDoc, PyArticle, PyAuthor, PyBadArticle,
    PyTag, QueryDoc, S11ModelPython, S12InheritedPython, SharedArticle, SharedAuthor, VectorDoc,
)
```

Replace `SCENARIOS = {…}` (lines 66-69) with the following. The first three lines are a new comment appended to the scenario comment block above it:
```python
# match-pattern (S13) is register-phase-NEVER for this driver in a harness run: only .NET registers
# PatternDoc (register-once rule). This driver seeds three rows and then issues a scalar row pattern
# match and a SIMILARITY-backed one through the client library's own MatchPatternBuilder.
SCENARIOS = {
    "crud-roundtrip", "naming-rejected", "interop", "schema-catalog", "query", "vector-search",
    "identity", "error-contract", "model-rejected", "model-inherited", "match-pattern",
}
```

Insert after `VECTOR_TOP_K = 50` (line 85), with one blank line before it:
```python
# The Label every PatternDoc row this driver writes carries: the PARTITION BY column, so this
# language's three rows form one partition. Must stay in step with MatchPatternScenario.LabelFor.
PATTERN_DOC_LABEL = f"pat-{LANGUAGE}"
```

Insert before `def main(argv: List[str]) -> int:` (line 440), with two blank lines on each side:
```python
def match_rows_to_json(rows: List[Any]) -> dict:
    """``{"rows": [{"matchNumber", "classifier", "data"}, ...]}`` in stream order, every
    ``MatchPatternResult`` copied verbatim — ``data``'s keys are the server's, untouched."""
    return {
        "rows": [
            {"matchNumber": row.match_number, "classifier": row.classifier, "data": _json_safe(row.data)}
            for row in rows
        ]
    }
```

Insert before `    elif phase == "register" and scenario == "error-contract":` (line 694), after the vector-search read branch. Keep one blank line after it:
```python
    elif phase == "register" and scenario == "match-pattern":
        # S13 match-pattern: only the .NET driver ever runs this phase (register-once rule), so this
        # branch exists for completeness and is never reached in a harness run — kept so a hand-run
        # of this driver behaves the same as the others rather than falling into crud-roundtrip's.
        error: Optional[str] = None
        try:
            registrar = SchemaRegistrar(capture, PatternDoc)
            registrar.register_all()
        except Exception as exc:  # noqa: BLE001 - reported as data, not raised
            error = describe(exc)

        descriptor_json = capture.select("PatternDoc")
        steps.append(StepResult(
            name="register_pattern_doc",
            ok=error is None,
            error=error,
            type_descriptor=json.loads(descriptor_json) if descriptor_json else None,
        ))

    elif phase == "write" and scenario == "match-pattern":
        # Three rows, seq 1..3, stamped with the run's marker and this language's label. Every key
        # persist() returned is reported, also when a later row failed — it is the orchestrator's
        # expected-set accounting for the similarity match.
        written_keys: Dict[str, str] = {}
        written: List[dict] = []
        try:
            for seq in (1, 2, 3):
                entity = PatternDoc()
                entity.tenant_id = tenant
                entity.owner_id = owner_id
                entity.marker = id_prefix
                entity.label = PATTERN_DOC_LABEL
                entity.seq = seq
                entity.title = f"a note about row pattern matching, part {seq}"
                written_key = coordinator(PatternDoc).persist(entity)
                if written_key is not None:
                    written_keys[f"pattern_doc_{seq}"] = str(written_key)
                written.append(entity_to_dict(entity))
            result = StepResult("write_pattern_docs", True, entity=written)
        except Exception as exc:  # noqa: BLE001
            result = StepResult("write_pattern_docs", False, error=describe(exc))
        if written_keys:
            result.keys = written_keys
        steps.append(result)

    elif phase == "read" and scenario == "match-pattern":
        # Both requests are built with the client library's own MatchPatternBuilder and executed
        # through EntityCoordinator.match_pattern, never through the generated stub. Every output
        # row is reported in stream order; the orchestrator decides what they mean.
        try:
            request = (
                match_pattern_builder("PatternDoc")
                .where("Marker", SearchOperator.EQUALS, id_prefix)
                .partition_by("Label")
                .order_by("Seq")
                .pattern("A B+")
                .define("B", "Seq > PREV(Seq)")
                .measure("n", "COUNT(*)")
                .measure("first_seq", "FIRST(A.Seq)")
                .measure("last_seq", "LAST(B.Seq)")
                .rows_per_match(search_pb.ONE_ROW)
                .limit(100)
                .build()
            )
            rows = coordinator(PatternDoc).match_pattern(request)
            steps.append(StepResult("match_pattern_scalar", True, entity=match_rows_to_json(rows)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("match_pattern_scalar", False, error=describe(exc)))

        try:
            request = (
                match_pattern_builder("PatternDoc")
                .where("Marker", SearchOperator.EQUALS, id_prefix)
                .partition_by("Label")
                .order_by("Seq")
                .pattern("A+")
                .define("A", "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL")
                .measure("s", "SIMILARITY(Title, 'a note about row pattern matching')")
                .rows_per_match(search_pb.ALL_ROWS_SHOW_EMPTY)
                .limit(100)
                .build()
            )
            rows = coordinator(PatternDoc).match_pattern(request)
            steps.append(StepResult("match_pattern_similarity", True, entity=match_rows_to_json(rows)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("match_pattern_similarity", False, error=describe(exc)))
```

Run (from `Iverson.Clients/Python`): `python3 -m py_compile conformance/driver.py && python3 -m pytest tests/test_conformance_driver.py`
Expected: `4 passed`.

- [ ] **Step 3: TypeScript — `PatternDoc` and the two branches**

In `Iverson.Clients/TypeScript/conformance/models.ts`, insert after `VectorDoc` (whose closing `}` is line 202), with one blank line on each side:
```ts
/**
 * S13 `match-pattern`'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule, as for `VectorDoc`), and every
 * driver writes three rows into it and then matches over them.
 *
 * Deliberately relation-free and chunk-free: both requests read TYPE_ROWS. `marker` carries the
 * run's `--id-prefix` and is `@IversonMetadata()` exactly as on `VectorDoc`; `label` is the row's
 * per-language identity and the PARTITION BY column, and its spelling must match
 * `MatchPatternScenario.LabelFor`; `seq` is the ORDER BY column that `DEFINE B AS Seq > PREV(Seq)`
 * compares; `title` is the embedding source the similarity request's `SIMILARITY(Title, ...)`
 * scores. `seq` is a plain `number`: TypeScript cannot declare an integer, which is harmless here
 * because only the .NET descriptor is ever registered — this driver sends a JSON number either way.
 */
@IversonEntity()
export class PatternDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    @IversonMetadata()
    marker: string = '';

    label: string = '';

    seq: number = 0;

    @IversonEmbedding()
    title: string = '';
}
```

In `conformance/driver.ts`, replace line 20 (`import { IversonClient, SchemaRegistrar } from '../src/core.js';`) with:
```ts
import { IversonClient, SchemaRegistrar, type MatchPatternResult } from '../src/core.js';
```

Replace line 30 (the `./models.js` import) with:
```ts
import { ErrorDoc, ErrorUnregisteredDoc, IdentityDoc, PatternDoc, QueryDoc, S11ModelTypescript, S12InheritedTypescript, SharedArticle, SharedAuthor, TsArticle, TsAuthor, TsBadArticle, TsTag, VectorDoc } from './models.js';
```

Insert after line 33 (`import { chunks as chunksBuilder, similar as similarBuilder } from '../src/vector-search.js';`):
```ts
import { matchPattern as matchPatternBuilder } from '../src/match-pattern.js';
import { RowsPerMatch } from '../generated/object_search.js';
```

Replace `const SCENARIOS = new Set([…]);` (lines 58-61) with the following. The first three lines are a new comment appended to the scenario comment block above it:
```ts
// match-pattern (S13): register (dotnet-only, register-once), write and read — this driver seeds
// three PatternDoc rows and then issues a scalar row pattern match and a SIMILARITY-backed one
// through the client library's own MatchPatternBuilder.
const SCENARIOS = new Set([
    'crud-roundtrip', 'naming-rejected', 'interop', 'schema-catalog', 'query', 'vector-search',
    'identity', 'error-contract', 'model-rejected', 'model-inherited', 'match-pattern',
]);
```

Insert after `const VECTOR_TOP_K = 50;` (line 77), with one blank line before it:
```ts
/** The Label every PatternDoc row this driver writes carries: the PARTITION BY column, so this
 *  language's three rows form one partition. Must stay in step with `MatchPatternScenario.LabelFor`. */
const PATTERN_DOC_LABEL = `pat-${LANGUAGE}`;
```

Insert before `function step(` (line 147), with one blank line after it:
```ts
/** `{"rows": [{matchNumber, classifier, data}, ...]}` in stream order, every `MatchPatternResult`
 *  copied verbatim — `data`'s keys are the server's, untouched. */
function matchRowsToReport(rows: MatchPatternResult[]): unknown {
    return {
        rows: rows.map((r) => ({ matchNumber: r.matchNumber, classifier: r.classifier, data: r.data })),
    };
}
```

Insert immediately before `    } else if (phase === 'write') {` (line 841), which is the catch-all crud-roundtrip write branch; the block continues the `else if` chain after the vector-search read branch:
```ts
    } else if (phase === 'write' && scenario === 'match-pattern') {
        // Three rows, seq 1..3, stamped with the run's marker and this language's label. Every key
        // persist() resolved with is reported, also when a later row failed — it is the
        // orchestrator's expected-set accounting for the similarity match.
        const patternDocKeys: Record<string, string> = {};
        const written: Array<Record<string, unknown> | null> = [];
        let result: StepResult;
        try {
            for (const seq of [1, 2, 3]) {
                const entity = new PatternDoc();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.marker = idPrefix;
                entity.label = PATTERN_DOC_LABEL;
                entity.seq = seq;
                entity.title = `a note about row pattern matching, part ${seq}`;
                patternDocKeys[`pattern_doc_${seq}`] = await client.coordinator(PatternDoc).persist(entity);
                written.push(entityToPlain(entity));
            }
            result = step('write_pattern_docs', true, { entity: written });
        } catch (err) {
            result = step('write_pattern_docs', false, { error: describe(err) });
        }
        if (Object.keys(patternDocKeys).length > 0) result.keys = patternDocKeys;
        steps.push(result);
    } else if (phase === 'read' && scenario === 'match-pattern') {
        // Both requests are built with the client library's own MatchPatternBuilder and executed
        // through IversonClient's own matchPattern entry point, never through a raw generated
        // stub. Every output row is reported in stream order; the orchestrator decides what they
        // mean.
        try {
            const request = matchPatternBuilder('PatternDoc')
                .where('Marker', SearchOperator.EQUALS, idPrefix)
                .partitionBy('Label')
                .orderBy('Seq')
                .pattern('A B+')
                .define('B', 'Seq > PREV(Seq)')
                .measure('n', 'COUNT(*)')
                .measure('first_seq', 'FIRST(A.Seq)')
                .measure('last_seq', 'LAST(B.Seq)')
                .rowsPerMatch(RowsPerMatch.ONE_ROW)
                .limit(100)
                .build();
            const rows = await client.matchPattern(request);
            steps.push(step('match_pattern_scalar', true, { entity: matchRowsToReport(rows) }));
        } catch (err) {
            steps.push(step('match_pattern_scalar', false, { error: describe(err) }));
        }

        try {
            const request = matchPatternBuilder('PatternDoc')
                .where('Marker', SearchOperator.EQUALS, idPrefix)
                .partitionBy('Label')
                .orderBy('Seq')
                .pattern('A+')
                .define('A', "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL")
                .measure('s', "SIMILARITY(Title, 'a note about row pattern matching')")
                .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
                .limit(100)
                .build();
            const rows = await client.matchPattern(request);
            steps.push(step('match_pattern_similarity', true, { entity: matchRowsToReport(rows) }));
        } catch (err) {
            steps.push(step('match_pattern_similarity', false, { error: describe(err) }));
        }
```

Run (from `Iverson.Clients/TypeScript`): `npx tsc -p tsconfig.conformance.json && npm test`
Expected: `tsc` prints nothing. Then `Test Files  10 passed (10)` and `Tests  262 passed (262)`: the count is unchanged from Task 5, but the `tsconfig.test.json` typecheck now covers the new `conformance/` code.

- [ ] **Step 4: Go — `PatternDoc` and the two cases**

In `Iverson.Clients/Go/conformance/models.go`, insert after `VectorDoc` (whose closing `}` is line 125), with one blank line before it:
```go
// PatternDoc is S13 match-pattern's subject type. Every one of the five drivers declares the same
// type name and shape; only the .NET driver ever registers it (register-once rule, as for
// VectorDoc), and every driver writes three rows into it and then matches over them.
//
// Deliberately relation-free and chunk-free: both requests read TYPE_ROWS. Marker carries the
// run's --id-prefix and is iverson_meta exactly as on VectorDoc; Label is the row's per-language
// identity and the PARTITION BY column, and its spelling must match MatchPatternScenario.LabelFor;
// Seq is the ORDER BY column that DEFINE B AS Seq > PREV(Seq) compares — int32, because a Go int
// maps to CLR_INT64; Title is the embedding source the similarity request's SIMILARITY(Title, ...)
// scores.
type PatternDoc struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string
	OwnerId  string
	Marker   string `iverson_meta:"true"`
	Label    string
	Seq      int32
	Title    string `iverson_embedding:"true"`
}
```

In `conformance/main.go`, insert inside `supportedScenarios`, after `"model-inherited": true,` (line 75):
```go
	// match-pattern (S13) is register-phase-NEVER for this driver: only .NET registers PatternDoc
	// (register-once rule). This driver seeds three rows and then issues a scalar row pattern
	// match and a SIMILARITY-backed one through the client library's own MatchPatternBuilder.
	"match-pattern": true,
```

Insert after the `const ( … vectorTopK = uint32(50) )` block (closing `)` at line 97), with one blank line on each side:
```go
// patternDocLabel is the Label every PatternDoc row this driver writes carries: the PARTITION BY
// column, so this language's three rows form one partition. Must stay in step with
// MatchPatternScenario.LabelFor.
const patternDocLabel = "pat-" + language

// matchRowsReport is {"rows":[{"matchNumber","classifier","data"}, ...]} in stream order, every
// MatchPatternResult copied verbatim — data's keys are the server's, untouched.
func matchRowsReport(rows []iverson.MatchPatternResult) json.RawMessage {
	reported := make([]map[string]interface{}, 0, len(rows))
	for _, row := range rows {
		reported = append(reported, map[string]interface{}{
			"matchNumber": row.MatchNumber,
			"classifier":  row.Classifier,
			"data":        row.Data,
		})
	}
	return entityJSON(map[string]interface{}{"rows": reported})
}
```

Insert before `	case phase == "write" && sc == "identity":` (line 688), after the vector-search read case, followed by one blank line:
```go
	case phase == "write" && sc == "match-pattern":
		// S13 match-pattern: three rows, Seq 1..3, stamped with the run's marker and this
		// language's label. Every key Persist returned is reported, also when a later row failed —
		// it is the orchestrator's expected-set accounting for the similarity match.
		patCoord, patCoordErr := iverson.NewEntityCoordinator(client, PatternDoc{})
		var patStep stepResult
		if patCoordErr != nil {
			patStep = failStep("write_pattern_docs", patCoordErr)
		} else {
			keys := map[string]string{}
			written := make([]PatternDoc, 0, 3)
			var writeErr error
			for seq := int32(1); seq <= 3; seq++ {
				entity := PatternDoc{
					TenantId: tenant,
					OwnerId:  ownerID,
					Marker:   idPrefix,
					Label:    patternDocLabel,
					Seq:      seq,
					Title:    fmt.Sprintf("a note about row pattern matching, part %d", seq),
				}
				key, err := patCoord.Persist(ctx, entity)
				if key != "" {
					keys[fmt.Sprintf("pattern_doc_%d", seq)] = key
				}
				if err != nil {
					writeErr = err
					break
				}
				written = append(written, entity)
			}
			if writeErr != nil {
				patStep = failStep("write_pattern_docs", writeErr)
			} else {
				patStep = okStep("write_pattern_docs")
				patStep.Entity = entityJSON(written)
			}
			if len(keys) > 0 {
				patStep.Keys = keys
			}
		}
		steps = append(steps, patStep)

	case phase == "read" && sc == "match-pattern":
		// Both requests are built with the client library's own builder (iverson.NewMatchPattern)
		// and executed through EntityCoordinator.MatchPattern, never through the generated stub.
		// Every output row is reported in stream order; the orchestrator decides what they mean.
		// Where takes a *pb.SearchValue directly, like Go's aggregate builder (see the query read).
		patCoord, patCoordErr := iverson.NewEntityCoordinator(client, PatternDoc{})
		marker := &pb.SearchValue{Kind: &pb.SearchValue_StringVal{StringVal: idPrefix}}

		steps = append(steps, func() stepResult {
			if patCoordErr != nil {
				return failStep("match_pattern_scalar", patCoordErr)
			}
			req := iverson.NewMatchPattern("PatternDoc").
				Where("Marker", pb.SearchOperator_EQUALS, marker).
				PartitionBy("Label").
				OrderBy("Seq", false).
				Pattern("A B+").
				Define("B", "Seq > PREV(Seq)").
				Measure("n", "COUNT(*)").
				Measure("first_seq", "FIRST(A.Seq)").
				Measure("last_seq", "LAST(B.Seq)").
				RowsPerMatch(pb.RowsPerMatch_ONE_ROW).
				Limit(100).
				Build()
			rows, err := patCoord.MatchPattern(ctx, req)
			if err != nil {
				return failStep("match_pattern_scalar", err)
			}
			out := okStep("match_pattern_scalar")
			out.Entity = matchRowsReport(rows)
			return out
		}())

		steps = append(steps, func() stepResult {
			if patCoordErr != nil {
				return failStep("match_pattern_similarity", patCoordErr)
			}
			req := iverson.NewMatchPattern("PatternDoc").
				Where("Marker", pb.SearchOperator_EQUALS, marker).
				PartitionBy("Label").
				OrderBy("Seq", false).
				Pattern("A+").
				Define("A", "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL").
				Measure("s", "SIMILARITY(Title, 'a note about row pattern matching')").
				RowsPerMatch(pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY).
				Limit(100).
				Build()
			rows, err := patCoord.MatchPattern(ctx, req)
			if err != nil {
				return failStep("match_pattern_similarity", err)
			}
			out := okStep("match_pattern_similarity")
			out.Entity = matchRowsReport(rows)
			return out
		}())
```

Run (from `Iverson.Clients/Go`): `export PATH=~/sdk/go1.22/bin:~/go/bin:$PATH GOPATH=~/go GOROOT=~/sdk/go1.22 && go vet ./conformance && go build -o bin/conformance ./conformance && go test ./conformance/`
Expected: `go vet` and `go build` print nothing. Then `ok  	github.com/iverson/clients/go/conformance`.

- [ ] **Step 5: Java — `PatternDoc` and the S13 methods**

`Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/PatternDoc.java`:
```java
package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEmbedding;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonMetadata;

import java.util.UUID;

/**
 * S13 {@code match-pattern}'s subject type. Every one of the five drivers declares the same type
 * name and shape; only the .NET driver ever registers it (register-once rule, as for
 * {@code VectorDoc}), and every driver writes three rows into it and then matches over them.
 *
 * <p>Deliberately relation-free and chunk-free: both requests read TYPE_ROWS. {@code marker}
 * carries the run's {@code --id-prefix} and is {@code @IversonMetadata} exactly as on
 * {@code VectorDoc}; {@code label} is the row's per-language identity and the PARTITION BY column,
 * and its spelling must match {@code MatchPatternScenario.LabelFor}; {@code seq} is the ORDER BY
 * column that {@code DEFINE B AS Seq > PREV(Seq)} compares; {@code title} is the embedding source
 * the similarity request's {@code SIMILARITY(Title, ...)} scores.
 */
@IversonEntity
public class PatternDoc {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;

    @IversonMetadata
    private String marker;

    private String label;

    private int seq;

    @IversonEmbedding
    private String title;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getMarker() { return marker; }
    public void setMarker(String marker) { this.marker = marker; }

    public String getLabel() { return label; }
    public void setLabel(String label) { this.label = label; }

    public int getSeq() { return seq; }
    public void setSeq(int seq) { this.seq = seq; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }
}
```

In `Driver.java`, insert after `import io.iverson.conformance.models.IdentityDoc;` (line 18):
```java
import io.iverson.conformance.models.PatternDoc;
```

Insert after `import io.iverson.client.search.AggregateBuilder;` (line 20):
```java
import io.iverson.client.search.MatchPatternBuilder;
```

Insert before `import iverson.ObjectSearch.SearchOperator;` (line 24):
```java
import iverson.ObjectSearch.RowsPerMatch;
```

Replace the `SUPPORTED_SCENARIOS` declaration (lines 101-104, right after `MODEL_INHERITED_SCENARIO` at line 100) with:
```java
    // match-pattern (S13) is register-phase-NEVER for this driver: only .NET registers PatternDoc
    // (register-once rule). This driver seeds three rows and then issues a scalar row pattern match
    // and a SIMILARITY-backed one through the client library's own MatchPatternBuilder.
    private static final String MATCH_PATTERN_SCENARIO = "match-pattern";
    private static final java.util.Set<String> SUPPORTED_SCENARIOS =
        java.util.Set.of(CRUD_ROUNDTRIP_SCENARIO, INTEROP_SCENARIO, SCHEMA_CATALOG_SCENARIO,
            QUERY_SCENARIO, VECTOR_SEARCH_SCENARIO, IDENTITY_SCENARIO, ERROR_CONTRACT_SCENARIO,
            MODEL_REJECTED_SCENARIO, MODEL_INHERITED_SCENARIO, MATCH_PATTERN_SCENARIO);
```

Insert after `    private static final int VECTOR_TOP_K = 50;` (line 126):
```java
    /**
     * The Label every PatternDoc row this driver writes carries: the PARTITION BY column, so this
     * language's three rows form one partition. Must stay in step with
     * {@code MatchPatternScenario.LabelFor}.
     */
    private static final String PATTERN_DOC_LABEL = "pat-" + LANGUAGE;
```

Insert before `            } else if (IDENTITY_SCENARIO.equals(scenario)) {` (line 205). This continues the `if/else if` chain after the vector-search block:
```java
            } else if (MATCH_PATTERN_SCENARIO.equals(scenario)) {
                switch (phase) {
                    case "write" -> doMatchPatternWrite(client, tenant, ownerId, idPrefix, steps);
                    case "read" -> doMatchPatternRead(client, idPrefix, steps);
                    default -> {
                        System.err.println("unknown phase '" + phase + "' for scenario '" + scenario + "'");
                        System.exit(2);
                        return;
                    }
                }
```

Insert before `    // ── write ───…` (line 750), followed by one blank line:
```java
    // ── S13 match-pattern ────────────────────────────────────────────────────────────────────

    /**
     * Seeds three {@code PatternDoc} rows, seq 1..3, stamped with the run's marker and this
     * language's label. Every key {@code persist} returned is reported, also when a later row
     * failed — it is the orchestrator's expected-set accounting for the similarity match.
     */
    private static void doMatchPatternWrite(
            IversonClient client, String tenant, String ownerId, String idPrefix, List<StepResult> steps) {
        Map<String, String> docKeys = new java.util.LinkedHashMap<>();
        StepResult result = step("write_pattern_docs", r -> {
            for (int seq = 1; seq <= 3; seq++) {
                PatternDoc doc = new PatternDoc();
                doc.setTenantId(tenant);
                doc.setOwnerId(ownerId);
                doc.setMarker(idPrefix);
                doc.setLabel(PATTERN_DOC_LABEL);
                doc.setSeq(seq);
                doc.setTitle("a note about row pattern matching, part " + seq);
                String key = new EntityCoordinator<>(client, PatternDoc.class).persist(doc);
                if (key != null) docKeys.put("pattern_doc_" + seq, key);
            }
        });
        if (!docKeys.isEmpty()) result.keys = docKeys;
        steps.add(result);
    }

    /**
     * Issues the scalar match and the similarity match, both built with the client library's own
     * {@code Query.matchPattern} builder and executed through {@code EntityCoordinator.matchPattern},
     * never through a raw generated stub. Every output row is reported in stream order; the
     * orchestrator decides what they mean.
     */
    private static void doMatchPatternRead(IversonClient client, String idPrefix, List<StepResult> steps) {
        steps.add(step("match_pattern_scalar", r -> {
            MatchPatternBuilder pattern = Query.matchPattern("PatternDoc")
                .where("Marker", SearchOperator.EQUALS, idPrefix)
                .partitionBy("Label")
                .orderBy("Seq")
                .pattern("A B+")
                .define("B", "Seq > PREV(Seq)")
                .measure("n", "COUNT(*)")
                .measure("first_seq", "FIRST(A.Seq)")
                .measure("last_seq", "LAST(B.Seq)")
                .rowsPerMatch(RowsPerMatch.ONE_ROW)
                .limit(100);
            r.entity = matchRowsReport(new EntityCoordinator<>(client, PatternDoc.class).matchPattern(pattern));
        }));

        steps.add(step("match_pattern_similarity", r -> {
            MatchPatternBuilder pattern = Query.matchPattern("PatternDoc")
                .where("Marker", SearchOperator.EQUALS, idPrefix)
                .partitionBy("Label")
                .orderBy("Seq")
                .pattern("A+")
                .define("A", "SIMILARITY(Title, 'a note about row pattern matching') IS NOT NULL")
                .measure("s", "SIMILARITY(Title, 'a note about row pattern matching')")
                .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
                .limit(100);
            r.entity = matchRowsReport(new EntityCoordinator<>(client, PatternDoc.class).matchPattern(pattern));
        }));
    }

    /**
     * {@code {"rows":[{"matchNumber","classifier","data"}, ...]}} in stream order, every
     * {@code MatchPatternResult} copied verbatim — {@code data}'s keys are the server's, untouched.
     */
    private static JsonElement matchRowsReport(List<EntityCoordinator.MatchPatternResult> rows) {
        List<Map<String, Object>> reported = new ArrayList<>();
        for (EntityCoordinator.MatchPatternResult row : rows) {
            Map<String, Object> entry = new java.util.LinkedHashMap<>();
            entry.put("matchNumber", row.matchNumber());
            entry.put("classifier", row.classifier());
            entry.put("data", row.data());
            reported.add(entry);
        }
        Map<String, Object> document = new java.util.LinkedHashMap<>();
        document.put("rows", reported);
        return GSON.toJsonTree(document);
    }
```

Run: `export JAVA_HOME=~/sdk/java21 PATH=~/sdk/java21/bin:~/sdk/apache-maven-3.9.9/bin:$PATH && mvn -B -f Iverson.Clients/Java/pom.xml -pl conformance -am -DskipTests package`
Expected: `Iverson Client — Core ... SUCCESS`, `Iverson Conformance Driver ... SUCCESS` and `BUILD SUCCESS`.

- [ ] **Step 6: Smoke-run every driver against a dead endpoint**

This step proves each driver's dispatch and branch ordering. Before Steps 1–5, every driver's scenario whitelist rejects `match-pattern` with exit 2. After them, every phase writes a document naming the S13 steps. With a dead endpoint, each step is `ok:false` with an error, and none falls through to a crud-roundtrip step. Run from the repository root, after Steps 1–5 have built all five drivers:
```bash
export PATH=~/sdk/java21/bin:$PATH JAVA_HOME=~/sdk/java21
S=$(mktemp -d)
F="--scenario match-pattern --tenant t1 --owner-id o1 --id-prefix p1 --grpc http://localhost:1"
for phase in register write read; do
  dotnet run --no-build --project Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver -- $F --phase $phase --out $S/dotnet-$phase.json; echo "dotnet $phase exit $?"
done
for phase in register write read; do
  (cd Iverson.Clients/Python && python3 conformance/driver.py $F --phase $phase --out $S/python-$phase.json); echo "python $phase exit $?"
done
for phase in write read; do
  (cd Iverson.Clients/TypeScript && node dist-conformance/conformance/driver.js $F --phase $phase --out $S/typescript-$phase.json); echo "typescript $phase exit $?"
  (cd Iverson.Clients/Go && bin/conformance $F --phase $phase --out $S/go-$phase.json); echo "go $phase exit $?"
  java -jar Iverson.Clients/Java/conformance/target/iverson-conformance-driver.jar $F --phase $phase --out $S/java-$phase.json 2>/dev/null; echo "java $phase exit $?"
done
for f in $S/*.json; do python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['language'], d['phase'], [(s['name'], s['ok'], s['error'] is not None) for s in d['steps']])" $f; done
```
Expected: every `exit` line reads `exit 0`, and then:
```
dotnet read [('match_pattern_scalar', False, True), ('match_pattern_similarity', False, True)]
dotnet register [('register_pattern_doc', False, True)]
dotnet write [('write_pattern_docs', False, True)]
go read [('match_pattern_scalar', False, True), ('match_pattern_similarity', False, True)]
go write [('write_pattern_docs', False, True)]
java read [('match_pattern_scalar', False, True), ('match_pattern_similarity', False, True)]
java write [('write_pattern_docs', False, True)]
python read [('match_pattern_scalar', False, True), ('match_pattern_similarity', False, True)]
python register [('register_pattern_doc', False, True)]
python write [('write_pattern_docs', False, True)]
typescript read [('match_pattern_scalar', False, True), ('match_pattern_similarity', False, True)]
typescript write [('write_pattern_docs', False, True)]
```
The errors are `Unavailable`/`UNAVAILABLE` connection failures, as expected with no server. `register_pattern_doc` still carries the captured descriptor, because capture happens before the send. In both the .NET and the Python descriptor, `Seq` is `CLR_INT32`, `Marker` is `isMetadata` and `Title` is `isEmbedding`.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs Iverson.Clients/Python/conformance/driver.py Iverson.Clients/Python/conformance/models.py Iverson.Clients/TypeScript/conformance/driver.ts Iverson.Clients/TypeScript/conformance/models.ts Iverson.Clients/Go/conformance/main.go Iverson.Clients/Go/conformance/models.go Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/models/PatternDoc.java
git commit -m "add the match-pattern steps to the five conformance drivers" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 9: Live full-matrix conformance run

No automated test can prove the SDKs against a real server: CI runs neither the live matrix nor the Go, Python and Java drivers. This task runs the whole matrix against the local compose stack, built from this branch.

**Files:** none, unless the run finds a defect. A fix goes into the file of the task that owns it (Tasks 2–8), with its own test and commit, and the run is repeated.

**Interfaces:**
- Consumes: everything Tasks 1–8 produce.

- [ ] **Step 1: Rebuild the server image from this branch and start the stack**

Step 1 runs from the repository root; Steps 2–4 run in one shell, whose working directory Step 2 sets to `Iverson.Server/Iverson.ClientConformance` (as the runbook does; `DriverRunner.LocateRepoRoot` finds the repository by walking up from the binary to `Iverson.slnx`, so the working directory only has to be the project for `dotnet run`). The api and the worker share one image tag (`iverson-api`), and only images built after `58133a1f` serve `MatchPattern`.
```bash
cd Iverson.Server
docker compose build iverson-api
docker compose up -d
```
`up -d` recreates `iverson-api` and `iverson-worker` on the new image. `docker` here is Podman 5.7 with Docker Compose 2.40. Check that `iverson-api`, `iverson-worker`, `starrocks-init` (exited 0), `tei-embed`, `qdrant`, StarRocks and Authentik are up (`docker compose ps`). The worker is required: every projection consumer runs only in the worker role, so without it neither StarRocks nor Qdrant is populated.

- [ ] **Step 2: Export the orchestrator's environment**

Values come from `Iverson.Server/.env`; do not print them.
```bash
cd Iverson.Server/Iverson.ClientConformance
export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id
export IVERSON_CLIENT_SECRET="$(command grep '^IVERSON_LOADTEST_CLIENT_SECRET=' ../.env | cut -d= -f2-)"
export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
export IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(command grep '^IVERSON_BYPASS_PASSWORD=' ../.env | cut -d= -f2-)"
export IVERSON_OTHER_TENANT_PASSWORD="$(command grep '^IVERSON_SMOKE_TEST_PASSWORD=' ../.env | cut -d= -f2-)"
```
`TokenBroker` requires the last two unconditionally (they authenticate `iverson-loadtest-bypass-user` and `iverson-acting-user-smoke-test`). The stale runbook and the pre-Task-7 `--help` text say they have defaults; they do not.

- [ ] **Step 3: Run the new scenario alone**

Run: `dotnet run -- --scenarios match-pattern`
Expected:
- preflight passes;
- the log shows `phase register: dotnet`, `phase write: dotnet, python, typescript, go, java`, then a projection wait that is satisfied (it can take several minutes while TEI embeds 15 titles), then `phase read: …`;
- the matrix row `match-pattern` reads `ok` in all five language columns.

Narrowing `--scenarios` turns off the untouched-requirements gate, so this step does not check coverage.

- [ ] **Step 4: Run the full matrix**

Run: `dotnet run -- --json /tmp/conformance-full.json`
Expected:
- `match-pattern` is `ok` in all five columns;
- the summary line reads `requirements: 0 untouched of 49 registered`;
- no row that does not involve MatchPattern regresses. Task 1 regenerated every TypeScript stub, so every TypeScript cell exercises new generated code; a TypeScript cell that fails in a scenario other than `match-pattern` must be investigated, not assumed pre-existing.

The accepted non-green cells are those the standard already documents (for example, Java's `naming-rejected` skip).

- [ ] **Step 5: Investigate any failure**

Use superpowers:systematic-debugging. For authorization symptoms, read the server's audit log first: `docker logs iverson-api 2>&1 | command grep Audit.Denied`. Fix the defect in the owning task's files, with a test that fails without the fix. Re-run the owning task's suite, then repeat Steps 3–4. Record each run's matrix in the task report.

- [ ] **Step 6: Record the result**

No commit is needed when the run is green on the first attempt. Put the full-matrix text output (the matrix rows plus the requirements line) in the task report.


## Tasks NOT in this plan

From the spec's Out of scope section:

- A Postgres row source; SQL pushdown of simple patterns; the window-clause form of row pattern
  recognition (`SEEK`/`INITIAL`).
- `SIMILARITY` on a related type's vector (through an FK), or on anything other than the row's own
  type (or, for `CHUNKS`, the chunk's own vector).
- Using a pattern result as a ranking signal inside `SearchSimilar`/`SearchChunks`.
- Changing `SearchChunks`' existing silent AND of an OR filter.

Also not here: sample-program updates. Only the .NET sample demos `Pipeline`; the Java, Python, TypeScript and Go samples have no precedent of gaining new RPCs.

## Known issues inherited from spec

- Ingest lag between StarRocks and Qdrant produces `NULL` similarity (§8).
- History-dependent `define` predicates can make matching exponential in the worst case. It is
  bounded by `MaxActiveThreads`, `MaxSteps` and the timeout (through the budget's token), not prevented.
  Evaluating `measures` for output rows is bounded only by `limit` and the timeout, which is checked
  before each output row, so a timeout can overrun by one output row's `measures`.
- Compilation is bounded separately. `PERMUTE` expands to every ordering and bounded quantifiers
  unroll, so `MaxProgramInstructions` rejects with `InvalidArgument` some patterns well inside
  `MaxPatternLength`: at the default, `PERMUTE(A, B, C, D, E, F)` (5,759 instructions), `A{10000}`
  and `(A{0,100}){0,100}`.
- Decided by the §9.3 pause test: if StarRocks stops responding while a reader still has unread
  rows, disposing the reader drains until StarRocks answers again, and no MySqlConnector call
  bounds it. The RPC therefore never awaits the row source's disposal: after `limit`, and on any
  error raised above the store (the batcher's budgets, `define`/`measures` evaluation, scoring),
  it returns its status and the drain finishes in the background, logged if it fails, with the
  request's timeout token disposed only after the drain ends. A drained connection is held until
  then. One narrow case still drains inline: a failure inside the store itself while the
  connection is open with rows unread (the token cancelled while rows are already buffered on the
  client, or a value-conversion error mid-row) runs the store's own release before the exception
  leaves it, so against a StarRocks frozen at that moment the status waits for the drain. A row
  read that times out is not this case: the timeout closes the socket, and the release then does
  no network I/O.
- The `CHUNKS` source scans the filtered chunk set twice (the phase-1 parent list, then the batched
  phase-2 reads) to keep memory bounded.
- `NaN` from a zero-magnitude vector is kept (§2): `NOT`/`<>` predicates over it with a non-`NULL`
  operand qualify the row, and how each SDK decodes a `NaN` `number_value` is unverified.
- The unconfigured-vector case (§3.3) is detected by Qdrant message text, which may change across
  Qdrant versions, and it would also mask a wrong-vector-name bug.
