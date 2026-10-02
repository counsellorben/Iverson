# Rename `ClrType` to `ObjectType` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-02-object-type-rename-design.md` (commit SHA: `c06229a29b357ebbc5af7800d113be573ee22e2d`)

**Goal:** Rename the proto enum `ClrType` to `ObjectType`, drop the `CLR_` prefix from its values, and rename the `clr_type` fields to `object_type`, across the proto, the server, all five SDKs and the agent, as one atomic change.

**Architecture:** The proto change is wire-safe in binary, because every field and value number is unchanged. Each language's generated code is regenerated from the renamed proto. Every hand-written reference to the enum or field is renamed by one ordered perl script plus seven hand edits of prose lines. A `git grep` gate proves completeness: afterwards, only the six genuine .NET-runtime "CLR" references remain.

**Tech stack:** protoc 29.3 (`~/sdk/protoc`), Grpc.Tools (C#, at build time), protobuf-maven-plugin (Java, at build time), protoc-gen-go (Go 1.22, `~/sdk/go1.22`), grpc_tools (system `python3`), ts-proto 2.12.4.

---

## Global Constraints

- **Branch from LOCAL `main`** at `c06229a2` or later, never `origin/main`. Local `main` is 24 commits ahead of origin, and `origin/main` lacks the TypeScript scalar-resolution merge `139cfcc2` whose code this plan renames.
- **No compatibility aliases.** Don't use `allow_alias`, `json_name` shims or deprecated re-exports.
- **Committed generated files are regenerated, never hand-edited.** These are:
  - `Iverson.Clients/Go/generated/object_mapping.pb.go`
  - `Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py`
  - `Iverson.Clients/TypeScript/generated/object_mapping.ts`
- **`docs/` and `Iverson.Server/docs/` stay as written.** Other worktrees are out of scope.
- **These six lines keep "CLR"**, because they are about the .NET runtime, not the enum. The rename script never matches them, and no task edits them:
  - `Iverson.Server/Iverson.Sql.Tests/TenantRepositoryPostgresIntegrationTests.cs:57`
  - `Iverson.Server/Iverson.Sql.Tests/DlqRepositoryPostgresIntegrationTests.cs:10`
  - `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs:33`
  - `Iverson.Server/Iverson.ClientConformance/Verifier.cs:387`
  - `Iverson.Server/Iverson.ClientConformance/Requirements.cs:98`
  - `Iverson.Clients/DotNet/Iverson.Client.Search/SearchValueConverter.cs:6`
- **The rename script.** Every task applies this one script to its own hand-written files. If `/tmp/object-type-rename.pl` doesn't exist, write it with exactly this content. The rule order matters: the `ClrType.CLR_` rule must run before the bare-value rules.

  ```perl
  s/ClrType(\\?)\.CLR_/ObjectType$1./g;
  s/CLR_(STRING|GUID|INT32|INT64|DOUBLE|FLOAT|BOOL|DATETIME|BYTES)\b/$1/g;
  s/Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)/$1/g;
  s/ClrType/ObjectType/g;
  s/clrType/objectType/g;
  s/clr_type/object_type/g;
  s/ToClr(?![a-z])/ToObjectType/g;
  s/_to_clr\b/_to_object_type/g;
  s/_PY_TO_CLR\b/_PY_TO_OBJECT_TYPE/g;
  s/\bClr\b/ObjectType/g;
  s/\bclr\b/objectType/g;
  ```

  What each rule handles:
  1. `ClrType.CLR_X`, `ClrType.CLR_…` in TypeScript messages, and `ClrType\.CLR_` in TypeScript test regexes.
  2. Bare `CLR_X` in Python, Java, JSON and comments. It also turns Go's `ClrType_CLR_X` into `ClrType_X`.
  3. C# values, plus test names such as `YieldsClrGuidArray` and `RegistersAsClrBytesScalar`.
  4. `ClrType` everywhere, including `ClrTypeToSql`, `GetClrType`, `detectClrType` and `IsTotalOverClrType`.
  5. The rules for `clrType` and `clr_type` cover locals, fields and JSON keys.
  6. `jsTypeToClr`, `runtimeValueToClr`, `goTypeToClr`, `goScalarToClr`, `SqlTypeToClr`, `TrySqlTypeToClr` and `SqlTypeToClrMap`.
  7. The `\bClr\b` rule covers the tuple element in `SchemaBuilder.cs:447-449`.
  8. The `\bclr\b` rule covers Go locals.

  Apply it with `xargs perl -pi /tmp/object-type-rename.pl < <list-file>`, or with `perl -pi /tmp/object-type-rename.pl <files…>`. It is line-preserving.
- **The completeness gate regex.** Each task runs it scoped to its own directories:

  ```
  ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b
  ```

## File Structure

All paths are relative to the worktree root. Every file is modified; none is created.

- **Proto (Task 1):** `Iverson.Clients/Common/Proto/object_mapping.proto`. Renames the enum, the values and the two fields; every number is unchanged.
- **C# (Task 1).** Grpc.Tools generates the C# code at build time, so nothing generated is committed.
  - `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`
  - `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs`
  - `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
  - `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs`
  - `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
  - `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs`
  - `Iverson.Server/Iverson.ClientConformance/Verifier.cs`
  - `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
  - `Iverson.Server/Iverson.ClientConformance/Scenarios/NamingRejectedScenario.cs`
  - `Iverson.Server/Iverson.ClientConformance/Scenarios/NavPropertyRejectedScenario.cs`
  - `Iverson.Server/Iverson.ClientConformance/Scenarios/TenantRejectedScenario.cs`
  - `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Schema/AuthorizationRulesOwnerFieldTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs`
  - `Iverson.Server/Iverson.Api.Tests/Schema/ServerOwnedTenantColumnTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/CrudRoundtripScenarioTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/InheritedModelScenarioTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/ModelRejectedScenarioTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/NavPropertyRejectedScenarioTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/ReregistrarTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/SchemaCatalogScenarioTests.cs`
  - `Iverson.Server/Iverson.ClientConformance.Tests/VerifierTests.cs`
  - `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs`
- **Java (Task 2).** protobuf-maven-plugin generates the Java code at build time.
  - `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
  - `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java`
  - `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`
  - `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`
- **Go (Task 3):**
  - regenerate `Iverson.Clients/Go/generated/object_mapping.pb.go`
  - `Iverson.Clients/Go/iverson/registrar.go`
  - `Iverson.Clients/Go/iverson/tags.go`
  - `Iverson.Clients/Go/conformance/models.go`
  - `Iverson.Clients/Go/iverson/coordinator_test.go`
  - `Iverson.Clients/Go/iverson_test/registrar_test.go`
- **Python, the agent and LoadTest (Task 4):**
  - regenerate `Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py`
  - `Iverson.Clients/Python/iverson_client/core.py`
  - `Iverson.Clients/Python/tests/test_auth.py`
  - `Iverson.Clients/Python/tests/test_schema_registrar.py`
  - `Iverson.Agents/Python/iverson_agent/schema.py`
  - `Iverson.Agents/Python/tests/test_schema.py`
  - `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py`
- **TypeScript (Task 5):**
  - regenerate `Iverson.Clients/TypeScript/generated/object_mapping.ts`
  - `Iverson.Clients/TypeScript/src/core.ts`
  - `Iverson.Clients/TypeScript/src/annotations.ts`
  - `Iverson.Clients/TypeScript/src/index.ts`
  - `Iverson.Clients/TypeScript/tests/annotations.test.ts`
  - `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

The spec names `conformance/models.ts` (TypeScript) and the Java conformance driver as components, but neither contains a gate match today, so neither is touched.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` at spec-write time and are NOT re-verified here. Trusted as ground truth:

| # | Assumption | Evidence |
|---|---|---|
| 1 | The renamed proto compiles with no package-scope collisions | Rename applied to a scratch copy; `protoc` 29.3 `--descriptor_set_out` over all 6 protos succeeds. No other enum in package `iverson` has a value named STRING/GUID/INT32/INT64/DOUBLE/FLOAT/BOOL/DATETIME/BYTES (all enum values listed across the 6 protos) |
| 2 | Generated names match the table | Regenerated from the renamed proto. Go: `ObjectType_STRING ObjectType = 0`, `GetObjectType()`, `json=objectType`. Python: `m.STRING == 0`, `m.ObjectType.Name(7) == 'DATETIME'`, `PropertyDescriptor(object_type=m.GUID)`. TS: `export enum ObjectType { STRING = 0 …`, `objectType: ObjectType`, `objectTypeFromJSON`/`objectTypeToJSON`. C#: `public enum ObjectType { [OriginalName("STRING")] String = 0 … Datetime = 7`, property `ObjectType ObjectType` |
| 3 | C#: the property `ObjectType` of type `ObjectType` compiles and `p.ObjectType == ObjectType.Guid` resolves | `Iverson.Client.Contracts` built from the renamed proto, with a probe using both: Build succeeded, 0 warnings |
| 4 | C#: the enum member `String` doesn't disturb `System.String` | The same probe also uses `String.Empty`: build succeeded |
| 5 | Python: module-level `STRING` … `BYTES` don't collide | `dir(object_mapping_pb2)` uppercase names are unique (the 9 values plus RelationKind/SchemaEnrichmentKind values) |
| 6 | The server persists no enum names | `SchemaDescriptor.cs:117` `ColumnDescriptor(string Name, string SqlType, bool IsNullable)`; no `Clr` member in `SchemaDescriptor.cs` |
| 7 | No non-code file refers to the names | `grep` over all file types outside source code: hits only in `docs/`, `Iverson.Server/docs/superpowers/plans/` and `.worktrees/` |
| 8 | Each regeneration path runs locally | protoc 29.3 at `~/sdk/protoc`, `protoc-gen-go` in `~/go/bin`, `grpc_tools` importable, ts-proto in `node_modules`, `mvn` 3.9.9, `dotnet`, Go 1.22 |
| 9 | Regeneration adds no unrelated churn | Regenerating the **unchanged** proto: Go and Python are byte-identical to the committed files. TS differs only in the header line (`v2.12.3` installed vs `v2.12.4` committed and pinned) → the `npm ci` step |
| 10 | Drivers' JSON keys follow the generated code | Python `MessageToJson`, C# `JsonFormatter`, TS `TypeDescriptor.toJSON`, Go `protojson.Marshal`, Java `JsonFormat.printer`; no hand-written `"clrType"` key in any driver |
| 11 | Test commands exist | root `Iverson.slnx` holds the .NET SDK projects (`Iverson.Client.Core.Tests`, `Iverson.Client.Search.Tests`) and the server ones (incl. `Iverson.ClientConformance.Tests`); `mvn`; Go 1.22; system `python3` with pytest 9.1.1, grpc and protobuf; agent `.venv/bin/python -m pytest` |
| 12 | The agent's `iverson_client` import resolves | **False**: its `.pth` points at the deleted `.worktrees/reasoning-agent` → the environment prerequisite |
| 13 | The Java conformance driver builds against the client | The parent `pom.xml` reactor modules are `client`, `sample` and `conformance`; conformance depends on `io.iverson:iverson-client:${project.version}` |
| 14 | The genuine .NET-runtime exceptions are enumerable | `grep '\bCLR\b|\bclr\b'` across code: 6 runtime references (Exceptions); the rest refer to the enum |
| 15 | Every mapping site is covered | .NET `DetectType`, Java `detectClrType`, Go `goTypeToClr`/`goScalarToClr`, Python `_PY_TO_CLR`/`_python_type_to_clr`, TS `jsTypeToClr`/`runtimeValueToClr`; server `ScalarTypeMap`/`ArrayTypeOverrides`/`SqlTypeToClr*` |
| — | The completeness gate matches exactly the renamed population | `git grep` with `\bClr\b` adds only `SchemaBuilder.cs:447-449` over the original pattern outside docs; `git grep` returns 0 files under `.superpowers/` or `TypeScript/dist*`, where a working-tree grep finds 18 |
| — | Each `Done means` suite command runs as written | `.venv/bin/pytest` has the shebang `#!…/.worktrees/reasoning-agent/…/python` and fails with a bad interpreter; `.venv/bin/python` runs 3.14.4 with pytest 9.1.1; bare `tsc` is not on PATH (`npx` resolves it); `test_dialogue_patterns.py:4-5` documents the LoadTest command, and no CI or C# code runs that suite |
| — | The breaking-change examples name real per-language APIs | `git grep -l IversonArray -- '*.cs'` = 0 (TS-only: `annotations.ts`, `core.ts`); current forms: `object_mapping.pb.go:263` `GetClrType`, `SchemaRegistrar.java:189` `.setClrType`, `schema.py:88` `f.clr_type`; renamed forms compile/generate (C# `field.ObjectType == ObjectType.Guid` builds; Go `GetObjectType()`, Python `object_type=`) |
| — | A stale JSON name degrades silently (the reason for approach A) | Ran `json_format.Parse(…, ignore_unknown_fields=True)` on the current proto: unknown key → `CLR_STRING`; unknown enum name `"GUID"` → accepted, stays `CLR_STRING`. The harness parses with `WithIgnoreUnknownFields(true)` (`Verifier.cs:73-74`) |

## Verified plan-level assumptions

Every row was verified by applying this whole plan, task by task, to a throwaway detached worktree of `c06229a2`. The worktree has since been removed.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | The five task file lists (48 hand-written files) exist and, with the proto, the 4 exception-only files and the 3 generated files, are exactly the files the gate matches today | `xargs ls` over the lists succeeded; `comm` of the lists against `git grep -l <gate>` left only the proto, `SearchValueConverter.cs`, `IngestContractTests.cs`, `DlqRepositoryPostgresIntegrationTests.cs`, `TenantRepositoryPostgresIntegrationTests.cs` and the 3 generated files |
| 2 | Code validity | The script, the proto block and the seven prose edits leave a gate result of exactly the six Exception lines | Whole-repo `git grep -nE <gate> -- ':!docs' ':!Iverson.Server/docs'` after all 5 tasks: 6 lines, `diff`-identical to the same 6 lines captured before the rename; 52 files changed |
| 3 | Code validity | The script never alters an Exception line and preserves line numbers | Same `diff`: the 6 lines are byte-identical, at the same line numbers |
| 4 | Code validity | Spot-check: `SchemaBuilder` tuple and `Requirements` prose rename correctly | Diff: `(Sql: kv.Value.SqlType, ObjectType: kv.Key, IsArray: false)`, `x => (x.ObjectType, x.IsArray)`, `SqlTypeToObjectTypeMap`, `TrySqlTypeToObjectType`; Requirements `<c>Guid</c> is exactly the object`/`type the server maps` |
| 5 | Command | `bash scripts/generate_protos.sh` (Go) changes only `object_mapping.pb.go` | `git status --porcelain -- Iverson.Clients/Go` after generating: ` M …/generated/object_mapping.pb.go` only |
| 6 | Command | `bash scripts/generate_protos.sh` (Python) changes only `object_mapping_pb2.py` | `git status --porcelain`: ` M …/generated/object_mapping_pb2.py` only |
| 7 | Command | `npm ci` then `npm run generate` (TS) changes only `generated/object_mapping.ts` | `npm ls ts-proto` = `2.12.4`; `git status --porcelain`: ` M …/generated/object_mapping.ts` only |
| 8 | Command | `dotnet test Iverson.slnx` passes after Task 1 | 11 projects. Ten passed: 125, 74, 54, 638, 41, 113, 112, 183, 620 and 1318. `Iverson.StarRocks.Tests` failed 56 of 497 in the full-solution run; every failure was a `TypeInitializationException` from `TestcontainersSettings`/`ResourceReaper` with an inner `RegexMatchTimeoutException`, which is Testcontainers initialization under load. Run alone with `--no-build`, it passed 497/497. The rename touches no file in that project. **Total: 3775 passed.** The first background attempt also died once with "Internal CLR error (0x80131506)" before any test ran, while `mvn` was running concurrently. A re-run passed. |
| 9 | Command | `mvn test` from `Iverson.Clients/Java` passes after Task 2 | exit 0; surefire totals: run 220, fail 0, err 0, skip 0 |
| 10 | Command | `go test ./...` passes after Task 3, with Go 1.22 on PATH | `ok` for `conformance`, `iverson`, `iverson_test` (before and after `gofmt -w`); `go vet ./...` clean |
| 11 | Convention | The rename un-formats only `iverson/registrar.go` and `iverson_test/registrar_test.go`. `conformance/models.go` is already not gofmt-clean on `main`. | On `main`, `gofmt -l iverson conformance iverson_test` gives `conformance/models.go`. After the rename it also lists `iverson/registrar.go` and `iverson_test/registrar_test.go`, where composite-literal alignment broke because `ObjectType:` is 3 characters longer than `ClrType:`. After `gofmt -w` on those two files, only `models.go` remains. |
| 12 | Command | Python SDK `python3 -m pytest -q` passes after Task 4 | `225 passed` |
| 13 | Command | The agent suite passes when run with the main checkout's venv python from the worktree's agent directory, with the `.pth` pointed at the worktree's SDK. `iverson_agent` and `iverson_client` both resolve from the worktree. | `55 passed`; `iverson_agent.schema.__file__` and `iverson_client.__file__` both under the scratch worktree. The `.pth` was restored afterwards. No worktree has its own agent `.venv`, because `.venv` is untracked. |
| 14 | Command | The LoadTest suite run from the worktree root uses that worktree's SDK | `109 passed`. `pattern_leg.py:95-100` derives `REPO_ROOT` from its own file path, and the suite's `mapping_pb.GUID` exists only in the regenerated module. |
| 15 | Command | `npm test` and `npx tsc -p tsconfig.conformance.json --noEmit` pass after Task 5 | `Tests 284 passed (284)`; tsc exit 0 |
| 16 | Ordering | After Task 1 alone, Go and Python still pass on their stale committed generated code | `go test ./...` ok ×3; Python `225 passed` (TS has the same committed-generated-code shape) |
| 17 | Ordering | Between Task 1 and Task 2 the Java build is red, so Task 2 should follow Task 1 directly | `mvn -pl client test-compile` after Task 1 only: `cannot find symbol` at `SchemaRegistrar.java:[6,29]` (the `ClrType` import), exit 1 |
| 18 | Ordering | Tasks 2–5 don't depend on each other. Task 4's agent and LoadTest depend on Task 4's own Python regeneration. | Each task touches disjoint directories. The agent and LoadTest import `iverson_client.generated.object_mapping_pb2`, which Task 4 regenerates. |
| 19 | Consumer impact | No tracked consumer outside the task lists refers to a renamed symbol or JSON name | the whole-repo gate (row 2), plus every suite green (rows 8–15) |
| 20 | Consumer impact | No existing `ObjectType`/`objectType`/`object_type`/`OBJECT_TYPE` identifier collides with the new names | `git grep -nE '\bObjectType\b\|\bobjectType\b\|\bobject_type\b\|OBJECT_TYPE'` outside docs: 0 hits |
| 21 | Gate | Each task's scoped gate returns only the Exception lines in its scope | T1 scope: exactly the 6 Exception lines; T2–T5 scopes: 0 lines (`git grep` exit 1) |
| 22 | Convention | Commit messages are lowercase, imperative, without a prefix | `git log --format=%s -25`: e.g. `add the typescript IversonType decorator`, `resolve typescript scalar column types from metadata, initializer or @IversonType` |
| 23 | Branch base | `origin/main` lacks `139cfcc2`, the merge whose TypeScript code Task 5 renames | `git merge-base --is-ancestor 139cfcc2 origin/main` → exit 1 |

## Tasks

### Task 1: Proto, server and .NET SDK

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto:32-42,46,143`
- Modify (script): the 28 C# files listed under "C# (Task 1)" in File Structure
- Modify (prose): `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs:175`, `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:309`, `Iverson.Server/Iverson.ClientConformance/Requirements.cs:30,48`, `Iverson.Server/Iverson.ClientConformance/Verifier.cs:248,278`

**Interfaces:**
- Produces: the renamed proto that Tasks 2–5 regenerate from (`enum ObjectType`, `object_type` fields, numbers unchanged).

- [ ] **Step 1: Rename the enum and the two fields in the proto**

Replace the enum block at `object_mapping.proto:32-42`:

```proto
enum ObjectType {
    STRING   = 0;
    GUID     = 1;
    INT32    = 2;
    INT64    = 3;
    DOUBLE   = 4;
    FLOAT    = 5;
    BOOL     = 6;
    DATETIME = 7;
    BYTES    = 8;
}
```

Replace line 46, `    ClrType clr_type      = 2;`, with `    ObjectType object_type = 2;`. Replace line 143, `    ClrType clr_type         = 3;`, with `    ObjectType object_type   = 3;`.

- [ ] **Step 2: Apply the rename script to the C# files**

Write `/tmp/object-type-rename.pl` from Global Constraints if it doesn't exist. Then, from the worktree root:

```bash
perl -pi /tmp/object-type-rename.pl \
  Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs \
  Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs \
  Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/PatternDoc.cs \
  Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs \
  Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs \
  Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
  Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs \
  Iverson.Server/Iverson.ClientConformance/Verifier.cs \
  Iverson.Server/Iverson.ClientConformance/Requirements.cs \
  Iverson.Server/Iverson.ClientConformance/Scenarios/NamingRejectedScenario.cs \
  Iverson.Server/Iverson.ClientConformance/Scenarios/NavPropertyRejectedScenario.cs \
  Iverson.Server/Iverson.ClientConformance/Scenarios/TenantRejectedScenario.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/AuthorizationRulesOwnerFieldTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/ServerOwnedTenantColumnTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/CrudRoundtripScenarioTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/InheritedModelScenarioTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/ModelRejectedScenarioTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/NavPropertyRejectedScenarioTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/ReregistrarTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/SchemaCatalogScenarioTests.cs \
  Iverson.Server/Iverson.ClientConformance.Tests/VerifierTests.cs \
  Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs
```

What this changes:
- Strings that embed enum names change with the code. For example:
  - the orchestrator's message renders `'Guid'` instead of `'ClrGuid'`;
  - the Verifier's labels read `objectType=Guid` and `does not declare STRING`;
  - the harness test fixtures' JSON uses `"objectType": "GUID"` and `"object_type":"GUID"`.
- The tests that assert those strings change with them.
- Test names lose `Clr`. For example, `SqlTypeToClr_RecoversEveryClrType_ScalarAndArray` becomes `SqlTypeToObjectType_RecoversEveryObjectType_ScalarAndArray`.

- [ ] **Step 3: Rename the six prose "CLR type" lines**

These lines mean the enum, but the script deliberately has no `\bCLR\b` rule, so edit them by hand:

```bash
perl -pi -e 's/\bCLR\b/object/ if $. == 175' Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs
perl -pi -e 's/\bCLR\b/object/ if $. == 309' Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs
perl -pi -e 's/\bCLR\b/object/ if $. == 30 || $. == 48' Iverson.Server/Iverson.ClientConformance/Requirements.cs
perl -pi -e 's/\bCLR\b/object/ if $. == 248 || $. == 278' Iverson.Server/Iverson.ClientConformance/Verifier.cs
```

The results read:
- "the declared object/SQL type"
- "whatever type its object type maps to"
- "`<c>Guid</c>` is exactly the object / type the server maps"
- "declares its object type as a delimited string"
- "`ClrGuid` is / exactly the object type", which becomes "`Guid` is / exactly the object type" because the script has already renamed `ClrGuid`
- "declare its object type as a"

Don't touch `Requirements.cs:98` or `Verifier.cs:387`; they are Exceptions.

- [ ] **Step 4: Run the scoped gate**

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- Iverson.Server Iverson.Clients/DotNet Iverson.Clients/Common ':!Iverson.Server/docs' ':!Iverson.Server/Iverson.LoadTest/scripts'
```

Expected: exactly the six Exception lines from Global Constraints and nothing else. (`Iverson.LoadTest/scripts` belongs to Task 4.)

- [ ] **Step 5: Run the .NET suite**

From the worktree root, run `dotnet test Iverson.slnx`.

Expected: all 11 test projects pass, 3775 tests in total.

`Iverson.StarRocks.Tests` can fail under the full-solution run: every failure is a `TypeInitializationException` from `TestcontainersSettings`/`ResourceReaper` wrapping a `RegexMatchTimeoutException`. If that happens, re-run that project alone with `dotnet test Iverson.Server/Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --no-build`; expect 497/497. Any other failure is real.

Don't run `mvn` concurrently. A concurrent run once crashed the .NET host with "Internal CLR error. (0x80131506)" before any test ran.

- [ ] **Step 6: Commit, with the breaking-change note as the body**

```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Clients/DotNet Iverson.Server
git commit -F - <<'MSG'
rename ClrType to ObjectType in the proto, the server and the .net sdk

The proto enum ClrType is now ObjectType and its values drop the CLR_
prefix (STRING, GUID, INT32, INT64, DOUBLE, FLOAT, BOOL, DATETIME, BYTES);
the PropertyDescriptor and SchemaField fields clr_type are now object_type.
Every field and value number is unchanged, so the binary wire format is
identical and binary gRPC peers on either version interoperate.

Breaking for SDK consumers in every language:
- C#: field.ClrType == ClrType.ClrString -> field.ObjectType == ObjectType.String
  (SchemaField / PropertyDescriptor from Iverson.Client.Contracts)
- TypeScript: ClrType.CLR_INT32 -> ObjectType.INT32; clrType -> objectType
- Python: mapping_pb.CLR_FLOAT -> mapping_pb.FLOAT; clr_type -> object_type
- Go: pb.ClrType_CLR_FLOAT -> pb.ObjectType_FLOAT; GetClrType() -> GetObjectType()
- Java: ClrType.CLR_FLOAT -> ObjectType.FLOAT; getClrType()/setClrType() ->
  getObjectType()/setObjectType()
Proto-JSON consumers see the key objectType and the bare value names.

The Java, Go, Python and TypeScript SDKs follow in the next commits; the
Java build is red until its commit lands.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
```

Run `git status --porcelain -- Iverson.Server Iverson.Clients/DotNet Iverson.Clients/Common` first. It should list only the 29 files of this task. `git add` on the directories also picks up any stray build output that isn't gitignored, so if anything else appears, add the 29 paths explicitly instead.

### Task 2: Java SDK

**Files:**
- Modify (script):
  - `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
  - `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java`
  - `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`
  - `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java`

**Interfaces:**
- Consumes: Task 1's renamed proto. protobuf-maven-plugin generates `iverson.ObjectMapping.ObjectType` at build time.

- [ ] **Step 1: Apply the rename script**

Write `/tmp/object-type-rename.pl` from Global Constraints if it doesn't exist. Then:

```bash
perl -pi /tmp/object-type-rename.pl \
  Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java \
  Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java \
  Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java \
  Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java
```

What this changes:
- `import iverson.ObjectMapping.ClrType` becomes `import iverson.ObjectMapping.ObjectType`.
- `detectClrType` becomes `detectObjectType`.
- The record component `clrType` becomes `objectType`, so its accessor is `detected.objectType()`.
- `setClrType` becomes `setObjectType`.
- The test `registerAll_byteArrayField_stillRegistersAsClrBytesScalar` becomes `registerAll_byteArrayField_stillRegistersAsBytesScalar`.

- [ ] **Step 2: Run the scoped gate**

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- Iverson.Clients/Java
```

Expected: no output (exit 1).

- [ ] **Step 3: Run the Java suite**

From `Iverson.Clients/Java`, run `mvn test`.

Expected: BUILD SUCCESS. Across `*/target/surefire-reports/*.txt` the totals are 220 run, 0 failures, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java \
  Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java \
  Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java \
  Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/StructConverterTest.java
git commit -m "rename ClrType to ObjectType in the java sdk" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: Go SDK

**Files:**
- Regenerate: `Iverson.Clients/Go/generated/object_mapping.pb.go`
- Modify (script):
  - `Iverson.Clients/Go/iverson/registrar.go`
  - `Iverson.Clients/Go/iverson/tags.go`
  - `Iverson.Clients/Go/conformance/models.go`
  - `Iverson.Clients/Go/iverson/coordinator_test.go`
  - `Iverson.Clients/Go/iverson_test/registrar_test.go`

**Interfaces:**
- Consumes: Task 1's renamed proto.

- [ ] **Step 1: Regenerate the Go stubs**

From `Iverson.Clients/Go`, run `bash scripts/generate_protos.sh`. The script puts `~/sdk/go1.22/bin`, `~/go/bin` and `~/sdk/protoc` on its own PATH.

Then `git status --porcelain -- .` should show only `generated/object_mapping.pb.go` modified.

- [ ] **Step 2: Apply the rename script**

Write `/tmp/object-type-rename.pl` from Global Constraints if it doesn't exist. Then, from the worktree root:

```bash
perl -pi /tmp/object-type-rename.pl \
  Iverson.Clients/Go/iverson/registrar.go \
  Iverson.Clients/Go/iverson/tags.go \
  Iverson.Clients/Go/conformance/models.go \
  Iverson.Clients/Go/iverson/coordinator_test.go \
  Iverson.Clients/Go/iverson_test/registrar_test.go
```

What this changes:
- `pb.ClrType_CLR_GUID` becomes `pb.ObjectType_GUID`.
- `goTypeToClr` becomes `goTypeToObjectType`, and `goScalarToClr` becomes `goScalarToObjectType`.
- The locals `clr` and `clrType` become `objectType`.
- `TestGuidTagYieldsClrGuid` becomes `TestGuidTagYieldsGuid`, and `TestGuidTagOnStringSliceYieldsClrGuidArray` becomes `TestGuidTagOnStringSliceYieldsGuidArray`.

- [ ] **Step 3: Restore gofmt alignment in the two files the rename un-formatted**

`ObjectType:` is 3 characters longer than `ClrType:`, which breaks composite-literal alignment:

```bash
~/sdk/go1.22/bin/gofmt -w Iverson.Clients/Go/iverson/registrar.go Iverson.Clients/Go/iverson_test/registrar_test.go
```

Don't gofmt `conformance/models.go`: it is already not gofmt-clean on `main`, and reformatting it is not part of this change. Afterwards, `~/sdk/go1.22/bin/gofmt -l Iverson.Clients/Go/iverson Iverson.Clients/Go/iverson_test` must print nothing.

- [ ] **Step 4: Run the scoped gate and the Go suite**

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- Iverson.Clients/Go
```

Expected: no output (exit 1).

From `Iverson.Clients/Go`:

```bash
PATH=~/sdk/go1.22/bin:$PATH GOROOT=~/sdk/go1.22 go vet ./... && PATH=~/sdk/go1.22/bin:$PATH GOROOT=~/sdk/go1.22 go test ./...
```

Expected: `ok` for `conformance`, `iverson` and `iverson_test`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Go/generated/object_mapping.pb.go Iverson.Clients/Go/iverson/registrar.go \
  Iverson.Clients/Go/iverson/tags.go Iverson.Clients/Go/conformance/models.go \
  Iverson.Clients/Go/iverson/coordinator_test.go Iverson.Clients/Go/iverson_test/registrar_test.go
git commit -m "regenerate the go stubs and rename ClrType to ObjectType in the go sdk" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Python SDK, agent and LoadTest dialogue suite

**Files:**
- Regenerate: `Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py`
- Modify (script):
  - `Iverson.Clients/Python/iverson_client/core.py`
  - `Iverson.Clients/Python/tests/test_auth.py`
  - `Iverson.Clients/Python/tests/test_schema_registrar.py`
  - `Iverson.Agents/Python/iverson_agent/schema.py`
  - `Iverson.Agents/Python/tests/test_schema.py`
  - `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py`
- Modify (prose): `Iverson.Agents/Python/iverson_agent/schema.py:95`

**Interfaces:**
- Consumes: Task 1's renamed proto. The agent and the LoadTest suite both import this task's regenerated `iverson_client.generated.object_mapping_pb2`.

- [ ] **Step 1: Regenerate the Python stubs**

From `Iverson.Clients/Python`, run `bash scripts/generate_protos.sh`. Then `git status --porcelain -- .` should show only `iverson_client/generated/object_mapping_pb2.py` modified.

- [ ] **Step 2: Apply the rename script, then the one prose edit**

Write `/tmp/object-type-rename.pl` from Global Constraints if it doesn't exist. Then, from the worktree root:

```bash
perl -pi /tmp/object-type-rename.pl \
  Iverson.Clients/Python/iverson_client/core.py \
  Iverson.Clients/Python/tests/test_auth.py \
  Iverson.Clients/Python/tests/test_schema_registrar.py \
  Iverson.Agents/Python/iverson_agent/schema.py \
  Iverson.Agents/Python/tests/test_schema.py \
  Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py
perl -pi -e 's/\bCLR\b/object/ if $. == 95' Iverson.Agents/Python/iverson_agent/schema.py
```

What this changes:
- `_PY_TO_CLR` becomes `_PY_TO_OBJECT_TYPE`, and `_python_type_to_clr` becomes `_python_type_to_object_type`.
- `mapping_pb.CLR_GUID` becomes `mapping_pb.GUID`.
- `clr_type=` becomes `object_type=`.
- In the agent, `mpb.ClrType.Name` becomes `mpb.ObjectType.Name`, and the `_NUMERIC`/`_TEXTUAL` sets use `mpb.INT32` and so on.
- `schema.py:95` now reads `raise ValueError(f"unsupported object type {mpb.ObjectType.Name(f.object_type)}")`.
- The schema text the LLM sees changes from `(CLR_STRING)` to `(STRING)`.

- [ ] **Step 3: Run the scoped gate**

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- Iverson.Clients/Python Iverson.Agents Iverson.Server/Iverson.LoadTest
```

Expected: no output (exit 1).

- [ ] **Step 4: Run the Python SDK and LoadTest suites**

From `Iverson.Clients/Python`, run `python3 -m pytest -q`. Expected: `225 passed`.

From the worktree root, run:

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py -q
```

Expected: `109 passed`.

- [ ] **Step 5: Point the agent venv at this worktree's SDK, then run the agent suite**

The agent's `.venv` exists only in the main checkout; it is untracked. Its `.pth` currently names the deleted `.worktrees/reasoning-agent`. This is the spec's environment prerequisite, a local step and not a repo change. Overwrite that one line with this worktree's SDK path:

```bash
printf '%s\n' "<WORKTREE_ROOT>/Iverson.Clients/Python" > /home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/lib/python3.14/site-packages/iverson_client.pth
```

`<WORKTREE_ROOT>` is the absolute path of the worktree.

From `<WORKTREE_ROOT>/Iverson.Agents/Python`, run:

```bash
/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python -m pytest -q
```

Expected: `55 passed`. Use the module form: the `.venv/bin/pytest` launcher's shebang names the deleted worktree, so it fails with a bad interpreter. `-m` puts the current directory first on `sys.path`, so `iverson_agent` comes from the worktree.

After the branch merges, repoint the `.pth` at `/home/ben/repositories/Iverson/Iverson.Clients/Python` before re-running the agent suite on `main`.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py \
  Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_auth.py \
  Iverson.Clients/Python/tests/test_schema_registrar.py Iverson.Agents/Python/iverson_agent/schema.py \
  Iverson.Agents/Python/tests/test_schema.py Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py
git commit -m "regenerate the python stubs and rename ClrType to ObjectType in the python sdk, agent and loadtest suite" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: TypeScript SDK, and the whole-repo gate

**Files:**
- Regenerate: `Iverson.Clients/TypeScript/generated/object_mapping.ts`
- Modify (script):
  - `Iverson.Clients/TypeScript/src/core.ts`
  - `Iverson.Clients/TypeScript/src/annotations.ts`
  - `Iverson.Clients/TypeScript/src/index.ts`
  - `Iverson.Clients/TypeScript/tests/annotations.test.ts`
  - `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

**Interfaces:**
- Consumes: Task 1's renamed proto. The whole-repo gate in Step 4 also consumes Tasks 1–4's results.

- [ ] **Step 1: Install the pinned toolchain and regenerate**

From `Iverson.Clients/TypeScript`, run `npm ci`, then `npm ls ts-proto` (it must show `2.12.4`), then `npm run generate`. An older ts-proto rewrites every generated header line.

Then `git status --porcelain -- .` should show only `generated/object_mapping.ts` modified.

- [ ] **Step 2: Apply the rename script**

Write `/tmp/object-type-rename.pl` from Global Constraints if it doesn't exist. Then, from the worktree root:

```bash
perl -pi /tmp/object-type-rename.pl \
  Iverson.Clients/TypeScript/src/core.ts \
  Iverson.Clients/TypeScript/src/annotations.ts \
  Iverson.Clients/TypeScript/src/index.ts \
  Iverson.Clients/TypeScript/tests/annotations.test.ts \
  Iverson.Clients/TypeScript/tests/schema-registrar.test.ts
```

What this changes:
- `jsTypeToClr` becomes `jsTypeToObjectType`, and `runtimeValueToClr` becomes `runtimeValueToObjectType`.
- The package export `ClrType` becomes `ObjectType`.
- The field `clrType` becomes `objectType`.
- The error messages' `@IversonType(ClrType.CLR_…)` and `@IversonArray(ClrType.CLR_GUID)` become `@IversonType(ObjectType.…)` and `@IversonArray(ObjectType.GUID)`.
- The test regexes `ClrType\.CLR_` become `ObjectType\.`, so they still match the new messages.

- [ ] **Step 3: Run the scoped gate and the TypeScript suite**

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- Iverson.Clients/TypeScript
```

Expected: no output (exit 1). `git grep` searches tracked files only, so untracked `dist/` and `dist-conformance/` are out of scope.

From `Iverson.Clients/TypeScript`, run `npm test`, then `npx tsc -p tsconfig.conformance.json --noEmit`. Expected: `Tests 284 passed (284)`, and tsc exits 0.

- [ ] **Step 4: Run the whole-repo completeness gate**

From the worktree root:

```bash
git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b' -- ':!docs' ':!Iverson.Server/docs'
```

Expected: exactly six lines, the six Exceptions from Global Constraints, at those line numbers. Any other line is an incomplete rename. Fix it in the task that owns the file.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/TypeScript/generated/object_mapping.ts Iverson.Clients/TypeScript/src/core.ts \
  Iverson.Clients/TypeScript/src/annotations.ts Iverson.Clients/TypeScript/src/index.ts \
  Iverson.Clients/TypeScript/tests/annotations.test.ts Iverson.Clients/TypeScript/tests/schema-registrar.test.ts
git commit -m "regenerate the typescript stubs and rename ClrType to ObjectType in the typescript sdk" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```
