# Rename `ClrType` to `ObjectType` — Design

**Date:** 2026-10-02
**Status:** Approved design, assumptions verified

## Problem

The proto enum that names a property's column type is called `ClrType`, and its values are `CLR_STRING`, `CLR_GUID` and so on. CLR is the .NET runtime, but this enum is consumed by five SDKs (C#, Java, Go, Python, TypeScript), the server and the reasoning agent. The .NET-specific name leaks into every language's public API, for example TypeScript's `ClrType.CLR_INT32` and Go's `pb.ClrType_CLR_FLOAT`.

## Decisions (user, this session)

- **Q1 — field names:** rename the two `clr_type` fields to `object_type` as well as the enum.
- **Q2 — depth:** rename every hand-written identifier, comment and message that refers to the proto enum or field. Keep references to genuine .NET-runtime types (see Exceptions). Historical docs (`docs/`, `Iverson.Server/docs/`) stay as written.
- **Approach A — one atomic change:** the proto, server, conformance harness, all five SDKs and their conformance drivers, and the agent change together in one branch and one merge. There are no compatibility aliases. Rejected: a staged rollout with `json_name`/`allow_alias` shims. The conformance harness and the five drivers are coupled by JSON names, and a stale name degrades **silently** to the zero value (`STRING`) rather than failing (see Verified assumptions). Shims would double the proto surface for a transition that one repo and disposable stacks don't need.

## Design

### Proto

In `Iverson.Clients/Common/Proto/object_mapping.proto`:

- `enum ClrType` → `enum ObjectType`, with every number unchanged: `STRING = 0`, `GUID = 1`, `INT32 = 2`, `INT64 = 3`, `DOUBLE = 4`, `FLOAT = 5`, `BOOL = 6`, `DATETIME = 7`, `BYTES = 8`.
- `PropertyDescriptor`: `ClrType clr_type = 2` → `ObjectType object_type = 2`.
- `SchemaField`: `ClrType clr_type = 3` → `ObjectType object_type = 3`.

Field and value numbers are unchanged, so the protobuf **binary** wire format is identical. Proto-JSON key and enum-value names change.

### Generated names

| Language | Enum value | Field accessor | How it's generated |
|---|---|---|---|
| C# (server + .NET SDK) | `ObjectType.String`, `ObjectType.Guid`, … `ObjectType.Datetime` | `p.ObjectType` | built by Grpc.Tools (`Iverson.Client.Contracts.csproj:17`) |
| TypeScript | `ObjectType.STRING` … | `objectType`; `objectTypeFromJSON` / `objectTypeToJSON` | `scripts/generate_protos.sh` (ts-proto), committed `generated/` |
| Python | `mapping_pb.STRING` …; `mapping_pb.ObjectType.Name(n)` | `.object_type` | `scripts/generate_protos.sh` (grpc_tools), committed `*_pb2.py` |
| Go | `pb.ObjectType_STRING` … | `.ObjectType`, `GetObjectType()` | `scripts/generate_protos.sh` (protoc-gen-go), committed `*.pb.go` |
| Java | `ObjectType.STRING` … | `getObjectType()` / `setObjectType()` | built by protobuf-maven-plugin |

### The rename rule for hand-written code

An identifier, comment or message that refers to the **proto enum or field** is renamed:
- `ClrType` → `ObjectType`
- `clrType` / `clr_type` → `objectType` / `object_type`
- `Clr` in a name → `ObjectType` (e.g. `jsTypeToClr` → `jsTypeToObjectType`)
- an enum name like `CLR_X` or `ClrX` → `X`
- prose "CLR type", where it means the enum → "object type"

A reference to **real .NET-runtime types** keeps "CLR" (see Exceptions).

Components:
- **Server (`Iverson.Server`):**
  - `Iverson.Api`: `SchemaBuilder` (`ClrTypeToSql`, `ClrTypeToStarRocksType`, `ClrTypeToEngagementType`, `SqlTypeToClr`, `TrySqlTypeToClr`, `SqlTypeToClrMap` and its builder's tuple element `Clr` (`:447-449`), `ClrTypeMapping`, `ScalarTypeMap`/`ArrayTypeOverrides` keys, and the `:309` comment), `SchemaCatalogReader`, `SchemaRegistrationOrchestrator` (including the `:730-734` check and message), and the `DocumentRenderer.cs:175` comment.
  - `Iverson.ClientConformance`: `Verifier` (checks, assertion labels such as "does not declare CLR_STRING", and the `:248`/`:278` comments) and `Requirements` (the `:30`/`:48` comments).
  - `Iverson.LoadTest/scripts/test_dialogue_patterns.py`, and every `*.Tests` project, including test names such as `SqlTypeToClr_RecoversEveryClrType_ScalarAndArray`.
- **.NET SDK:** `Iverson.Client.Core/SchemaRegistrar.cs` (`DetectType`'s enum values and the `clrType` locals), the conformance driver's `Models/PatternDoc.cs`, and `SchemaRegistrarTests.cs`, including `registerAll_byteArrayField_stillRegistersAsClrBytesScalar`.
- **Java SDK:** `SchemaRegistrar` (`detectClrType`, `setClrType`), `StructConverter`, their tests, and the conformance driver.
- **Go SDK:** `iverson/registrar.go` (`goTypeToClr`, `goScalarToClr`, and the `clr` locals), `conformance/models.go`, `iverson/coordinator_test.go`, and `iverson_test/registrar_test.go`, including `TestGuidTagOnStringSliceYieldsClrGuidArray`.
- **Python SDK:** `iverson_client/core.py` (`_PY_TO_CLR`, `_python_type_to_clr`), `tests/test_schema_registrar.py` and `tests/test_auth.py`.
- **TypeScript SDK:** `src/core.ts` (`jsTypeToClr`, `runtimeValueToClr` and their comments), `src/annotations.ts`, `src/index.ts` (the `ClrType` value export → `ObjectType`), `conformance/models.ts`, and the tests.
- **Agent (`Iverson.Agents/Python`):** `iverson_agent/schema.py` (`mpb.ClrType.Name` → `mpb.ObjectType.Name`, the `_NUMERIC`/`_TEXTUAL` sets, and "unsupported CLR type" → "unsupported object type") and `tests/test_schema.py`. The schema text the LLM sees changes from `(CLR_STRING)` to `(STRING)`.

Messages and assertion labels that embed an enum name change text. For example, the orchestrator's "it is 'ClrGuid'" becomes "it is 'Guid'". Tests asserting those strings are updated with them.

### Exceptions: references that keep "CLR"

These six references are about the .NET runtime, not the enum:
- `Iverson.Server/Iverson.Sql.Tests/TenantRepositoryPostgresIntegrationTests.cs:57` — Dapper's CLR type mapping
- `Iverson.Server/Iverson.Sql.Tests/DlqRepositoryPostgresIntegrationTests.cs:10` — the `timestamptz`/CLR type mapping
- `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs:33` — an ordering "the CLR does not guarantee"
- `Iverson.Server/Iverson.ClientConformance/Verifier.cs:387` — `StructConverter`'s CLR default
- `Iverson.Server/Iverson.ClientConformance/Requirements.cs:98` — `StructConverter`'s CLR default
- `Iverson.Clients/DotNet/Iverson.Client.Search/SearchValueConverter.cs:6` — "Converts CLR values"

Out of scope by decision Q2: `docs/`, `Iverson.Server/docs/`, and other branches' worktrees under `.worktrees/` and `.claude/worktrees/`.

### Regeneration

- **TypeScript:** run `npm ci` in `Iverson.Clients/TypeScript` first, then `npm run generate`. `node_modules` must hold ts-proto 2.12.4, the version `package.json` pins and that the committed `generated/` was produced with. An older install rewrites every header line.
- **Python:** `Iverson.Clients/Python/scripts/generate_protos.sh`.
- **Go:** `Iverson.Clients/Go/scripts/generate_protos.sh`.
- **C# and Java:** the normal build regenerates them.

Committed generated files are regenerated, never hand-edited.

### Environment prerequisite: the agent's `iverson_client` path

`Iverson.Agents/Python/.venv/lib/python3.14/site-packages/iverson_client.pth` points at `/home/ben/repositories/Iverson/.worktrees/reasoning-agent/Iverson.Clients/Python`, a worktree that no longer exists, so `import iverson_client` fails. Before running the agent's tests, rewrite that one line to the executing checkout's `Iverson.Clients/Python`. The `.venv` is not committed, so this is a local environment step, not a repo change.

### Done means

1. **Completeness.** From the repo root, `git grep -nE 'ClrType|CLR_|clr_type|clrType|Clr(String|Guid|Int32|Int64|Double|Float|Bool|Datetime|Bytes)|[A-Za-z]Clr\b|\bClr\b|ToClr|_CLR|_clr|\bCLR\b|\bclr\b'` returns hits only in the six Exceptions above and in `docs/` and `Iverson.Server/docs/`. `git grep` searches tracked files only, so untracked build output (`Iverson.Clients/TypeScript/dist/`, `dist-conformance/`), `.superpowers/`, `node_modules`, `.venv`, `bin`, `obj`, `target` and other worktrees are out of its scope.
2. **Every suite passes:**
   - C#: `dotnet test Iverson.slnx` from the repo root, which covers the server and the .NET SDK, including `Iverson.ClientConformance.Tests`, `Iverson.Client.Core.Tests` and `Iverson.Client.Search.Tests`
   - Java: `mvn test` from `Iverson.Clients/Java`, the reactor of client, sample and conformance
   - Go: `go test ./...` from `Iverson.Clients/Go`, with `~/sdk/go1.22`
   - the Python SDK: `python3 -m pytest` from `Iverson.Clients/Python`
   - the agent: `.venv/bin/python -m pytest` from `Iverson.Agents/Python`, after the prerequisite above. The `.venv/bin/pytest` launcher's shebang also names the deleted worktree, so use the module form.
   - TypeScript: `npm test` from `Iverson.Clients/TypeScript`, plus `npx tsc -p tsconfig.conformance.json --noEmit`
   - the LoadTest dialogue suite: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py -q` from the repo root

   Integration suites that need containers run where the environment allows them, as today.
3. A live conformance run against a running stack is not required, because the binary wire format is unchanged.

## Breaking change

This is breaking for SDK consumers in every language: rename `ClrType` → `ObjectType` and drop the `CLR_`/`Clr` value prefix. For example:
- C#: `field.ClrType == ClrType.ClrString` → `field.ObjectType == ObjectType.String` (on `SchemaField` / `PropertyDescriptor` from `Iverson.Client.Contracts`)
- TypeScript: `ClrType.CLR_INT32` → `ObjectType.INT32`
- Python: `mapping_pb.CLR_FLOAT` → `mapping_pb.FLOAT`
- Go: `pb.ClrType_CLR_FLOAT` → `pb.ObjectType_FLOAT`
- Java: `ClrType.CLR_FLOAT` → `ObjectType.FLOAT`

The field accessors change too: C# and Go `ClrType` → `ObjectType`, Go `GetClrType()` → `GetObjectType()`, TypeScript `clrType` → `objectType`, Java `getClrType()`/`setClrType()` → `getObjectType()`/`setObjectType()`, Python `clr_type` → `object_type`.

Proto-JSON consumers see `objectType` and the bare value names. Binary gRPC peers on either version interoperate. Record this in the commit message; there is no CHANGELOG.

## Verified assumptions

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
