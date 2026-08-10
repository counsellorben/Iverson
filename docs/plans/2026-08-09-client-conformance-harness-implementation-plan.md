# Cross-client conformance harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-09-client-conformance-harness-design.md` (commit SHA: `ece0171`)

**Goal:** Build a .NET orchestrator that drives five thin per-language driver subprocesses against a live stack and asserts, in one place, that every client registers a correct schema and round-trips an entity through create, read, update and delete without corrupting a relation.

**Architecture:** One orchestrator (`Iverson.Server/Iverson.ClientConformance`) owns authentication, assertions, reporting and every depth-resolved read. Five drivers (one per client, living beside the client they exercise) are invoked once per phase — `register`, `write`, `read`, `update`, `delete` — and report JSON to a `--out` path, never asserting. Four scenarios run over that protocol: `crud-roundtrip`, `naming-rejected`, `nav-property-rejected` and `interop`.

**Tech stack:** .NET 10 (orchestrator, .NET driver), Java 21 + Maven (grpc-java 1.71), Python 3.11+ (grpcio), TypeScript 5.8 + Node 22 (`@grpc/grpc-js`, ts-proto), Go 1.25 (google.golang.org/protobuf 1.36), Npgsql + Dapper for the Postgres verification leg.

---

## Global Constraints

Project-wide rules every task must hold to. Copied from the spec and from verified codebase facts.

- **Drivers report, never assert.** A driver has no test framework, no assertions, and no knowledge of expected values. Every assertion lives in `Verifier.cs`.
- **Drivers use only their client's public API** — `SchemaRegistrar`, `EntityCoordinator`, and the client's public constructors. Raw gRPC in a driver is a defect except where this plan names it (the capture seam of Task 3, which wraps the public stub parameter rather than bypassing the client).
- **A failed step is data.** A step that throws sets `"ok": false` with `"error"` and the driver still exits 0. A non-zero exit means the driver itself broke.
- **Stdout is not the channel.** Every driver writes its JSON document to `--out <path>`; TypeScript's `console.log` and Java's SLF4J default output would corrupt stdout (A14).
- **Row keys are UUIDs** (A18), driver-chosen, incorporating the run id, and reported by logical name on the write step. There is no shared derivation algorithm.
- **Type names are stable across runs** so schema drift detection stays meaningful. A driver entity-shape change therefore requires the manual remedy under *Known issues*.
- **Commit messages are plain lowercase imperative with no Conventional-Commits prefix** — verified against `git log --oneline -15` (`add critical design review round 5 for the client conformance harness design`, `reject iverson_guid on non-string fields at go schema-build time`).
- **Authorization rules the orchestrator re-registers with** (Ben's decision, 2026-08-09): `OwnerField = "OwnerId"` **and** a `RowPermission` for role `iverson-loadtest-bypass` with `CanReadAll`/`CanWriteAll`/`CanDeleteAll`. Every driver entity therefore carries an `OwnerId` property and every driver takes `--owner-id <sub>`.

## File Structure

**Create — orchestrator** (`Iverson.Server/Iverson.ClientConformance/`)

- `Iverson.ClientConformance.csproj` — net10.0 Exe; references `Iverson.LoadTest` and `Iverson.Client.Core`; `Npgsql`, `Dapper`.
- `Program.cs` — CLI: `--languages`, `--scenarios`, `--json <path>`, `--keep`.
- `Preflight.cs` — API / Authentik / Postgres reachability, each failure naming what is down and the command to start it.
- `TokenBroker.cs` — mints the acting-user token once via `ActingUserTokenProvider`; exposes the acting user's `sub`; provisions the tenant via `TenantLifecycleGrpcService`.
- `DriverRunner.cs` — builds a driver once, execs it once per phase, reads each phase's JSON, captures stderr, records toolchain-absent skips.
- `DriverProtocol.cs` — the phase-document model (`PhaseDocument`, `StepResult`, `KeyMap`).
- `Reregistrar.cs` — re-registers a driver's reported `TypeDescriptor` with the authorization block replaced, nothing else changed.
- `Verifier.cs` — every assertion, including the three-way comparison.
- `Scenarios/CrudRoundtripScenario.cs`, `Scenarios/NamingRejectedScenario.cs`, `Scenarios/NavPropertyRejectedScenario.cs`, `Scenarios/InteropScenario.cs`.
- `Report.cs` — console matrix and `--json` output.

**Create — drivers**

- `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/` — `.csproj`, `Program.cs`, `Auth.cs`, `Capture.cs`, `Models/`.
- `Iverson.Clients/Python/conformance/driver.py`, `conformance/models.py`.
- `Iverson.Clients/TypeScript/conformance/driver.ts`, `conformance/models.ts`; `tsconfig.conformance.json` at the TypeScript root.
- `Iverson.Clients/Go/conformance/main.go`, `conformance/models.go`.
- `Iverson.Clients/Java/conformance/pom.xml`, `conformance/src/main/java/io/iverson/conformance/{Driver,DualHeaderCredentials,CaptureInterceptor}.java`, `conformance/src/main/java/io/iverson/conformance/models/`.

**Modify**

- `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:93` — `IVERSON_TOTP_SECRET` fallback ahead of the cached-file read.
- `Iverson.Server/Iverson.Server.slnx` — add the orchestrator project.
- `Iverson.slnx` — add the orchestrator and the .NET driver.
- `Iverson.Clients/Java/pom.xml` — add `<module>conformance</module>`.
- `Iverson.Clients/TypeScript/tsconfig.test.json` — add `conformance/**/*` to `include` so `npm test`'s type-check covers the driver.

**Test**

No new unit-test projects. The harness's falsifiability discipline is Task 11's mutation demonstration, per the spec's *Testing the harness* section; the existing suites (`dotnet test`, `npm test`, `pytest`, `go test ./...`, `mvn test`) must stay green and are run as regression gates in the tasks that touch their trees.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here.

- **A1** LoadTest's auth types are public and referencable — `AuthentikFlowExecutorClient`, `ActingUserTokenProvider` are `public sealed`.
- **A2** A project can reference `Iverson.LoadTest` (an Exe) — `net10.0` Exe; legal in .NET.
- **A3 (partial)** LoadTest's tenant provisioning is not reusable: `EnsureTenantProvisionedAsync` is a static local in `Program.cs`. The orchestrator calls `TenantLifecycle` directly.
- **A4** The TOTP secret has one read point, so an env fallback is local.
- **A5** All five clients expose register/write/get/update/delete.
- **A6 (failed)** Only .NET exposes a depth-resolved read (`EntityCoordinator.GetMappedAsync(key, depth)`); the depth checks belong to the orchestrator.
- **A7 (failed)** Only .NET's registrar accepts authorization rules; the orchestrator re-registers with permissions.
- **A8 (failed for Go and TypeScript at spec-write time)** — since remedied on `main@b67458d`; see *Expected failures*.
- **A9** All five support `many_to_many`, `one_to_many` and a tenant field.
- **A10** Naming enforcement raises a catchable client-side error — observed live for Python before any RPC.
- **A11** Postgres table naming is derivable: `ToSnakeCase(TypeName) + "s"` (`SchemaBuilder.cs:30`).
- **A12** Re-registering an identical shape is idempotent.
- **A13** The five toolchains are available locally.
- **A14 (risk)** Client libraries may write to stdout; drivers write JSON to `--out <path>`.
- **A15** Referencing LoadTest's auth from a second project breaks nothing.
- **A16** The orchestrator's direct Postgres query is not blinded by RLS — no `FORCE ROW LEVEL SECURITY`, and the app connection is superuser.
- **A17 (failed)** `GetSchema` cannot reconstruct a registerable `TypeDescriptor` (`SchemaType`/`SchemaField` carry no `tenant_field`); the driver reports the descriptor it sent.
- **A18 (failed)** Row keys may not be arbitrary strings; a key column is `UUID` and FK values must be well-formed GUIDs.
- **A19 (failed)** The harness cannot re-register after a driver's entity shape changes (`SchemaDriftPolicy.Throw`, no unregister RPC); the remedy is manual.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All six new directories are absent, so every driver path is a create | `ls -d` on all six returned `No such file or directory` |
| 2 | File path | The orchestrator belongs in `Iverson.Server.slnx`, which is where `Iverson.LoadTest` is listed; the root `Iverson.slnx` lists client projects and the server set separately | `Iverson.Server/Iverson.Server.slnx` lists `Iverson.LoadTest/Iverson.LoadTest.csproj`; root `Iverson.slnx` does not |
| 3 | File path | `Iverson.Clients/Java/pom.xml` is a `pom`-packaged reactor with `<modules>client, sample</modules>` — a third module is additive | `Java/pom.xml:16-19` |
| 4 | File path | Go's module path is `github.com/iverson/clients/go` at `go 1.25.0`, so `Go/conformance` is a package in the existing module and imports `github.com/iverson/clients/go/iverson` | `Go/go.mod:1,3` |
| 5 | File path | Python's `pyproject.toml` scopes packaging to `iverson_client*` and pytest to `testpaths = ["tests"]`, so a new `conformance/` package needs no packaging change and is not collected by pytest | `Python/pyproject.toml` `[tool.setuptools.packages.find]`, `[tool.pytest.ini_options]` |
| 6 | File path | `tsconfig.json`'s `include` is `["src/**/*","generated/**/*"]` and `tsconfig.test.json` adds tests/sample — neither covers `conformance/`, so the driver needs its own config to be compiled and an entry in `tsconfig.test.json` to be type-checked | `TypeScript/tsconfig.json:12`, `tsconfig.test.json:15` |
| 7 | Signature | The TOTP read point is `private string? LoadCachedTotpSecret()` at `AuthentikFlowExecutorClient.cs:93`; `:146` (which the spec names) is its sole call site | `grep -rn "LoadCachedTotpSecret"` across the repo returns exactly those two lines |
| 8 | Signature | `ActingUserTokenProvider` exposes `GetTokenAsync(CancellationToken)` **and** `GetSubAsync(CancellationToken)` — the second is what supplies `--owner-id` | `ActingUserTokenProvider.cs:10,51` |
| 9 | Signature | Tenant provisioning is `TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient` with `ListTenantsAsync` / `CreateTenantAsync`, called with a bearer `Metadata` — about ten lines to duplicate | `Iverson.LoadTest/Program.cs:267-287` |
| 10 | Signature | `MappingGetRequest` carries `int32 depth = 3`, so the orchestrator's depth-1 reads are a field on an existing request | `Iverson.Clients/Common/Proto/object_mapping.proto:165-170` |
| 11 | Signature | The mapping service exposes exactly six RPCs — `Get`, `Post`, `Update`, `Delete`, `RegisterSchema`, `GetSchema` — and S3's hand-built payload posts `MappingWriteRequest` | `object_mapping.proto:10-16` |
| 12 | Signature | .NET: `SchemaRegistrar(EntityRegistry, ObjectMappingServiceClient, ILogger)` and `RegisterAllAsync(IReadOnlyDictionary<string, AuthorizationRules>?, CancellationToken)`; `EntityCoordinator<T>` has a public 7-arg constructor, so a driver can construct both against its own stubs | `SchemaRegistrar.cs:14-20`, `EntityCoordinator.cs:15-22` |
| 13 | Signature | Python: `SchemaRegistrar(mapping_stub, *entity_classes).register_all(trace_id)`; `EntityCoordinator.persist/update/get/delete` | `Python/iverson_client/core.py:145,499,512,524` |
| 14 | Signature | TypeScript: `new SchemaRegistrar(mappingClient, classes, callCredentials).registerAll(traceId)`; coordinator `persist/update/get/delete(id, traceId)` | `TypeScript/src/core.ts:413-421,526,545,563,586` |
| 15 | Signature | Go: `NewSchemaRegistrar(client MappingClient, entities ...interface{}).RegisterAll(ctx, traceID)`; coordinator methods all take `ctx` | `Go/iverson/registrar.go:28,33`; `Go/iverson/coordinator.go:179,198,217,232` |
| 16 | Signature | Java: `new SchemaRegistrar(IversonClient).registerAll(Class<?>...)`; `EntityCoordinator.persist/update/get/delete` | `Java/.../SchemaRegistrar.java:34,49`; `EntityCoordinator.java:72,88,105,137` |
| 17 | Signature | **No client exposes its `TypeDescriptor` builder publicly** — Go's `buildRequest` is package-private, .NET's `BuildTypeDescriptor` is `private static`, Java's `buildTypeDescriptor` is private, TypeScript's `_buildRequest` is `private`. The descriptor must be captured at the transport seam | `Go/iverson/registrar.go:51`, `DotNet/.../SchemaRegistrar.cs:52`, `Java/.../SchemaRegistrar.java` (`buildTypeDescriptor`), `TypeScript/src/core.ts` (`_buildRequest`) |
| 18 | Signature | A capture seam exists in every language: Go's registrar takes a `MappingClient` **interface**; Python's and TypeScript's take a stub/client object; .NET's takes the generated client (wrap via `Grpc.Core.Interceptors`); Java's reads `client.mappingStub`, so capture goes on the channel via `ManagedChannelBuilder.intercept(...)` | `Go/iverson/registrar.go:12-16,28`; `Python/core.py:137-143`; `TypeScript/src/core.ts:414-418`; `Java/.../IversonClient.java:73-79` |
| 19 | Signature | The acting-user token is **mandatory**, not optional: `Evaluate` returns `Denied` when `actingUser is null` on a schema that has rules — the same `Denied` the spec cites for the null-rules case | `RowFieldAuthorizationEvaluator.cs:11-16` |
| 20 | Signature | Java's and .NET's CRUD methods take no acting-user parameter (only the search family does), so drivers must attach the header at the credential/channel layer | `Java/.../EntityCoordinator.java:72,88,105,137` vs `:178,198`; `DotNet/.../EntityCoordinator.cs:33-82` vs `:101` |
| 21 | Signature | Each language can attach the acting-user header without touching client code: Java via a `CallCredentials` subclass (`IversonClient(channel, credentials)` is public); Go via `WithActingUserToken(ctx, token)`; Python via `IversonClient(acting_user_token=…)`; TypeScript via the `actingUserToken` constructor parameter; .NET via a `CallInvoker` interceptor feeding the public `EntityCoordinator<T>` constructor | `Java/.../IversonClient.java:63,73`; `Go/iverson/auth.go:23`; `Python/core.py:651`; `TypeScript/src/core.ts:646`; `DotNet/.../EntityCoordinator.cs:15` |
| 22 | Signature | Python's client accepts only `IversonClientCredentials(client_id, client_secret, token_endpoint)` for the service identity — no pre-minted-token path outside `_CachedTokenProvider` (private). Drivers therefore receive client credentials, not a minted service token | `Python/iverson_client/core.py:644-682` |
| 23 | Signature | The env-var names the orchestrator reads already exist in LoadTest: `IVERSON_CLIENT_ID`, `IVERSON_CLIENT_SECRET`, `IVERSON_TOKEN_ENDPOINT`, `IVERSON_CLIENT_SCOPE`, `IVERSON_ACTING_USER_*` | `Iverson.LoadTest/Program.cs:27-47` |
| 24 | Signature | The authorization-rules shape is `AuthorizationRules { OwnerField, RowPermissions[], FieldPermissions[] }` with `RowPermission { Role, CanReadAll, CanWriteAll, CanDeleteAll }` | `Iverson.LoadTest/Program.cs:289-306` |
| 25 | Code validity | Canonical proto3 JSON is available in every language for the reported descriptor: `JsonFormatter` (.NET, Google.Protobuf), `MessageToJson` (Python), `protojson` (Go, `google.golang.org/protobuf v1.36.11`), ts-proto's generated `toJSON` (TypeScript), `JsonFormat` (Java) | `Go/go.mod:7`; `Java/client/pom.xml:72-76` |
| 26 | Code validity | Java's `protobuf-java-util` (which provides `JsonFormat`) is **test-scoped** in the client pom, so the conformance module must declare it at compile scope itself | `Java/client/pom.xml:71-76` |
| 27 | Code validity | Entity declaration differs by client family and the plan's models must follow each: Go/Python/TypeScript put the relation on the FK member itself; .NET and Java declare a separate FK field plus an annotated navigation property. This is why S2 skips .NET and Java | `Go/sample/models/article.go`, `TypeScript/sample/models/Article.ts`, `Python/sample/models.py` vs `DotNet/.../Models/Article.cs`, `Java/.../models/Article.java` |
| 28 | Code validity | A UUID key is declared as `Guid` (.NET), `UUID` (Java), `uuid.UUID` (Python), `iverson_guid:"true"` (Go), `@IversonGuid()` (TypeScript) — the mechanisms merged at `main@b67458d` | `Go/sample/models/article.go:7`, `TypeScript/sample/models/Article.ts:15-17` |
| 29 | Code validity | The TypeScript driver must be compiled by `tsc` (not vitest/esbuild) or `emitDecoratorMetadata` produces no `design:type` and `@IversonGuid()`'s validation path is never exercised — the exact trap the key-typing work hit | `tsconfig.json:7-8`; `tsconfig.test.json` header comment |
| 30 | Code validity | TypeScript sample style is ESM with explicit `.js` specifiers against `../src/*.js`; Node is v22.16.0, so compiled output runs directly under `node` | `TypeScript/sample/main.ts:5-8`; `node --version` |
| 31 | Command | Java's reactor builds with `mvn -B -f Iverson.Clients/Java/pom.xml`; the sample module is a plain jar with no shading, so the conformance module needs `maven-shade-plugin` for `DriverRunner` to exec `java -jar` | `Java/sample/pom.xml`; `.github/workflows/codeql.yml` Java step |
| 32 | Command | `npm test` is `npm run typecheck && vitest run`, where typecheck is `tsc -p tsconfig.test.json` — so adding `conformance/**/*` to that config puts the driver under CI type-checking | `TypeScript/package.json` scripts |
| 33 | Ordering | No task imports a symbol a later task introduces: Task 2's `DriverProtocol` types are consumed by Tasks 3–7; Task 8's `Verifier` consumes documents Tasks 3–7 emit; Task 10 modifies drivers Tasks 3–7 create | Task-by-task read of the Consumes/Produces entries below |
| 34 | Consumer impact | `LoadCachedTotpSecret` has exactly one caller and no test references it or `IVERSON_TOTP_SECRET`, so the env fallback breaks nothing | `grep -rn "LoadCachedTotpSecret\|IVERSON_TOTP" --include=*.cs` → 2 hits, both in `AuthentikFlowExecutorClient.cs` |
| 35 | Consumer impact | CodeQL builds C# with `autobuild`, Go with `autobuild`, and Java with an explicit `mvn -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install` — so the new .NET projects, the Go package and the new Maven module must all compile in CI without a live server | `.github/workflows/codeql.yml` |
| 36 | Consumer impact | Python's `conformance/` is invisible to `pytest` (`testpaths = ["tests"]`) and to packaging (`include = ["iverson_client*"]`) | `Python/pyproject.toml` |

## Tasks

### Task 1: Orchestrator skeleton — CLI, preflight, tokens, tenant, report

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Iverson.ClientConformance.csproj`
- Create: `Iverson.Server/Iverson.ClientConformance/Program.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/Preflight.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/Report.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:93`
- Modify: `Iverson.Server/Iverson.Server.slnx`, `Iverson.slnx`

**Interfaces:**
- Produces: `TokenBroker` (acting-user token, acting-user `sub`, service client credentials, provisioned tenant id); `Report` (matrix cells: `ok` / `FAIL` / `skip` / `xfail` with reason); the `--languages` / `--scenarios` / `--json` / `--keep` CLI surface.

- [ ] **Step 1: Create the project and wire both solutions**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Iverson.ClientConformance</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Iverson.LoadTest/Iverson.LoadTest.csproj" />
    <ProjectReference Include="../../Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="10.0.3" />
    <PackageReference Include="Dapper" Version="2.1.79" />
  </ItemGroup>
</Project>
```
Add `<Project Path="Iverson.ClientConformance/Iverson.ClientConformance.csproj" />` to `Iverson.Server/Iverson.Server.slnx` and the equivalent repo-relative path to the `/Iverson.Server/` folder in `Iverson.slnx`.

- [ ] **Step 2: Add the `IVERSON_TOTP_SECRET` fallback**
At `AuthentikFlowExecutorClient.cs:93`, the env var takes precedence over the cached file; the `?? throw` at the sole call site (`:146`) then fires only when neither source has a secret.
```csharp
    private string? LoadCachedTotpSecret() =>
        Environment.GetEnvironmentVariable("IVERSON_TOTP_SECRET") is { Length: > 0 } env ? env :
        File.Exists(CachePath) ? File.ReadAllText(CachePath).Trim() is { Length: > 0 } s ? s : null : null;
```

- [ ] **Step 3: Implement `Preflight`** — three checks, each failing with what is down and the command to bring it up: a `MappingGetRequest` for a non-existent type against the gRPC endpoint (transport reachability, not a found row), an HTTP GET against the Authentik base URL, and an `NpgsqlConnection.OpenAsync`. The harness never starts or stops compose.

- [ ] **Step 4: Implement `TokenBroker`** — reads `IVERSON_CLIENT_ID` / `IVERSON_CLIENT_SECRET` / `IVERSON_TOKEN_ENDPOINT` / `IVERSON_CLIENT_SCOPE` and the `IVERSON_ACTING_USER_*` set; builds one `ActingUserTokenProvider` over an `AuthentikFlowExecutorClient` for the bypass identity; exposes `GetActingTokenAsync()` and `GetOwnerIdAsync()` (`GetSubAsync`). Provision the tenant by duplicating LoadTest's ten lines against `TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient` (A3) — `ListTenantsAsync`, and `CreateTenantAsync` only when absent.

- [ ] **Step 5: Implement `Report`** — languages down, scenarios across; each cell `ok` / `FAIL` / `skip` / `xfail`. A skip renders distinctly and always carries a reason. Failure detail prints the assertion, the three observed values and the driver's captured stderr. `--json <path>` writes the same content. Exit 0 only when every non-skipped, non-expected-fail cell passed.

- [ ] **Step 6: Build and run the existing .NET suites**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance Iverson.Server/Iverson.Server.slnx Iverson.slnx Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs
git commit -m "add the client conformance orchestrator skeleton with preflight, token broker and report"
```

---

### Task 2: Driver protocol, runner and re-registration

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/DriverProtocol.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/Reregistrar.cs`

**Interfaces:**
- Consumes: Task 1's `TokenBroker` and `Report`.
- Produces: the JSON contract every driver in Tasks 3–7 must emit; `DriverRunner.RunPhaseAsync`; `Reregistrar.ReregisterAsync`.

- [ ] **Step 1: Define the phase document**
One document per phase, at that phase's `--out` path. `keys` appears on the write step; `typeDescriptor` on the register step; `entity` on read steps.
```csharp
public sealed record PhaseDocument(string Language, string Phase, IReadOnlyList<StepResult> Steps);

public sealed record StepResult(
    string Name,
    bool Ok,
    string? Error = null,
    JsonElement? TypeDescriptor = null,
    IReadOnlyDictionary<string, string>? Keys = null,
    JsonElement? Entity = null);
```
The phase enum is `register`, `write`, `read`, `update`, `delete` and it partitions the steps: each phase label selects exactly one contiguous run of driver work.

- [ ] **Step 2: Implement `DriverRunner`** — one build per driver per run, then one exec per phase. The build command and the exec command are per-language (Task 3–7 each supply theirs). Absent toolchain (`mvn`, `go`, `python3`, `npm`, `dotnet` not on PATH, or a build that fails on a missing toolchain) records `skip (<tool> not found)` for that language's whole row and leaves the other four running. Non-zero exit is a driver break: the row fails with captured stderr. Phases after `register` receive `--keys <json>`, the accumulated logical-name-to-key map; every driver's `write` phase output feeds it.

- [ ] **Step 3: Implement `Reregistrar`** — take the driver's reported `TypeDescriptor` verbatim, parse it back into the proto message, set **only** `Authorization`, and call `RegisterSchema`. Nothing else may change: `SchemaRegistry.RegisterAsync` replaces the stored descriptor wholesale (`SchemaRegistry.cs:47-56`), so a reconstructed shape would overwrite the very relation descriptor S1's depth-1 check exists to inspect.
```csharp
    private static AuthorizationRules Rules(string ownerField = "OwnerId") => new()
    {
        OwnerField = ownerField,
        RowPermissions =
        {
            new RowPermission { Role = "iverson-loadtest-bypass", CanReadAll = true, CanWriteAll = true, CanDeleteAll = true },
        },
    };
```

- [ ] **Step 4: Build**
```bash
dotnet build Iverson.Server/Iverson.ClientConformance/Iverson.ClientConformance.csproj
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance
git commit -m "add the conformance driver protocol, phase runner and authorization-only re-registration"
```

---

### Task 3: .NET driver — the canonical shape

This task establishes the shape Tasks 4–7 mirror: models, phase dispatch, dual-token wiring, descriptor capture, `--out` document. Read it before implementing any other driver.

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj`
- Create: `.../Program.cs`, `.../Auth.cs`, `.../Capture.cs`
- Create: `.../Models/DotNetAuthor.cs`, `.../Models/DotNetArticle.cs`, `.../Models/DotNetTag.cs`
- Modify: `Iverson.slnx`

**Interfaces:**
- Consumes: Task 2's phase-document contract.
- Produces: the driver-side conventions Tasks 4–7 mirror; the `{Lang}Author` / `{Lang}Article` / `{Lang}Tag` type triple for S1.

- [ ] **Step 1: Declare the S1 models**
`.NET` and Java declare the FK as a separate field alongside an annotated navigation property (assumption 27). Every entity carries `OwnerId` and a tenant field.
```csharp
[IversonEntity]
public class DotNetArticle
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public Guid DotNetAuthorId { get; set; }
    public Guid[] DotNetTagIds { get; set; } = [];

    [ManyToOne(typeof(DotNetAuthor))] public DotNetAuthor? DotNetAuthor { get; set; }
    [ManyToMany(typeof(DotNetTag))] public List<DotNetTag> DotNetTags { get; set; } = [];
}
```
`DotNetAuthor` carries `[OneToMany(typeof(DotNetArticle))] public List<DotNetArticle> DotNetArticles { get; set; } = [];` — the reverse direction the foreign-key-only work broke.

- [ ] **Step 2: Wire both identities**
The service identity comes from `--client-id/--client-secret/--token-endpoint`; the acting-user token from `--acting-token`. Because `GetMappedAsync`/`PostMappedAsync`/`UpdateMappedAsync`/`DeleteAsync` take no header parameter (assumption 20), attach both at the channel: a `CallCredentials` for the bearer token and a `Grpc.Core.Interceptors.Interceptor` adding `x-acting-user-authorization`. Construct `EntityCoordinator<T>` directly against the resulting stubs using its public constructor (assumption 12) rather than `AddIversonClient`, which routes the acting-user token only to `SchemaCatalogClient`.

- [ ] **Step 3: Capture the sent `TypeDescriptor`**
No client exposes its descriptor builder (assumption 17). Wrap the mapping stub in an interceptor that records the outgoing `SchemaRequest.RootType` and forwards it unchanged; serialize the captured message with `JsonFormatter` for the register step's `typeDescriptor` field.

- [ ] **Step 4: Implement phase dispatch**
`--phase` selects exactly one block. Keys are driver-chosen UUIDs incorporating `--id-prefix`; the write phase reports them by logical name (`author`, `tag`, `article`).
```
register → registrar.RegisterAllAsync()               → steps: [register]
write    → persist author, tag, article (both FKs)    → steps: [write] with keys{}
read     → get article at depth 0                     → steps: [get] with entity
update   → change title, update the existing row      → steps: [update]
delete   → delete the article; get again              → steps: [delete]
```
Every step wraps its body so a throw becomes `"ok": false` with `"error"` and the process still exits 0.

- [ ] **Step 5: Build and add to the root solution**
```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver Iverson.slnx
git commit -m "add the dotnet conformance driver"
```

---

### Task 4: Python driver

Mirrors Task 3. Independent of Tasks 5–7.

**Files:**
- Create: `Iverson.Clients/Python/conformance/driver.py`, `Iverson.Clients/Python/conformance/models.py`

**Interfaces:**
- Consumes: Task 2's phase-document contract; Task 3's conventions.

- [ ] **Step 1: Declare the models** — Python puts the relation on the FK member itself (assumption 27), and `uuid.UUID` already maps to `CLR_GUID`:
```python
@iverson_entity
class PyArticle:
    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    title: str = None
    py_author_id: str = many_to_one("PyAuthor")
    py_tag_ids: str = many_to_many("PyTag")
```

- [ ] **Step 2: Wire identities** — `IversonClient(host, port, credentials=IversonClientCredentials(...), acting_user_token=...)` covers both in one constructor (assumption 21/22).

- [ ] **Step 3: Capture the descriptor** — pass a wrapper around the mapping stub into `SchemaRegistrar(stub, *classes)` (assumption 18) that records `request.root_type` and forwards; serialize with `google.protobuf.json_format.MessageToJson`.

- [ ] **Step 4: Implement phase dispatch and `--out` writing** — same five phases, same step names, same failed-step-is-data rule.

- [ ] **Step 5: Verify nothing regressed**
```bash
cd Iverson.Clients/Python && python3 -m pytest
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python/conformance
git commit -m "add the python conformance driver"
```

---

### Task 5: TypeScript driver

Mirrors Task 3. Independent of Tasks 4, 6, 7.

**Files:**
- Create: `Iverson.Clients/TypeScript/conformance/driver.ts`, `conformance/models.ts`, `Iverson.Clients/TypeScript/tsconfig.conformance.json`
- Modify: `Iverson.Clients/TypeScript/tsconfig.test.json`

**Interfaces:**
- Consumes: Task 2's phase-document contract; Task 3's conventions.

- [ ] **Step 1: Add the compile config**
The driver must be compiled by `tsc`, not run through esbuild, or `emitDecoratorMetadata` emits no `design:type` and `@IversonGuid()`'s validation path goes unexercised (assumption 29). A separate config keeps the driver out of the published `dist` (assumption 6).
```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": { "outDir": "dist-conformance" },
  "include": ["src/**/*", "generated/**/*", "conformance/**/*"]
}
```
Add `"conformance/**/*"` to `tsconfig.test.json`'s `include` so `npm test`'s type-check covers it (assumption 32).

- [ ] **Step 2: Declare the models** — relation on the FK member, `@IversonGuid()` on the key, ESM imports with explicit `.js` specifiers against `../src/*.js` (assumption 30).

- [ ] **Step 3: Wire identities** — `new IversonClient(host, port, false, callCredentials, actingUserToken)`; build `callCredentials` from the client-credentials flags.

- [ ] **Step 4: Capture the descriptor** — construct `new SchemaRegistrar(wrappedMappingClient, classes, callCredentials)` directly (assumption 18); serialize the captured `TypeDescriptor` with ts-proto's generated `toJSON`.

- [ ] **Step 5: Implement phase dispatch, then verify**
```bash
cd Iverson.Clients/TypeScript && npx tsc -p tsconfig.conformance.json && npm test
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/TypeScript/conformance Iverson.Clients/TypeScript/tsconfig.conformance.json Iverson.Clients/TypeScript/tsconfig.test.json
git commit -m "add the typescript conformance driver"
```

---

### Task 6: Go driver

Mirrors Task 3. Independent of Tasks 4, 5, 7.

**Files:**
- Create: `Iverson.Clients/Go/conformance/main.go`, `Iverson.Clients/Go/conformance/models.go`

**Interfaces:**
- Consumes: Task 2's phase-document contract; Task 3's conventions.

- [ ] **Step 1: Declare the models** — package in the existing module (assumption 4); key needs `iverson_guid:"true"`:
```go
type GoArticle struct {
	Id        string   `iverson_key:"true" iverson_guid:"true"`
	TenantId  string   `iverson_tenant:"true"`
	OwnerId   string
	Title     string
	GoAuthorId string  `iverson:"many_to_one:GoAuthor"`
	GoTagIds  []string `iverson:"many_to_many:GoTag"`
}
```

- [ ] **Step 2: Wire identities** — dial with a `PerRPCCredentials` carrying the service bearer, and attach the acting-user token per call with `iverson.WithActingUserToken(ctx, token)` (assumption 21).

- [ ] **Step 3: Capture the descriptor** — `NewSchemaRegistrar` takes the `MappingClient` interface, so the wrapper is a struct with one method that records `req.RootType` and forwards (assumption 18); serialize with `protojson`.

- [ ] **Step 4: Implement phase dispatch, then verify**
```bash
cd Iverson.Clients/Go && go build ./... && go test ./...
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Go/conformance
git commit -m "add the go conformance driver"
```

---

### Task 7: Java driver

Mirrors Task 3. Independent of Tasks 4–6.

**Files:**
- Create: `Iverson.Clients/Java/conformance/pom.xml`
- Create: `conformance/src/main/java/io/iverson/conformance/Driver.java`, `DualHeaderCredentials.java`, `CaptureInterceptor.java`
- Create: `conformance/src/main/java/io/iverson/conformance/models/{JavaAuthor,JavaArticle,JavaTag}.java`
- Modify: `Iverson.Clients/Java/pom.xml`

**Interfaces:**
- Consumes: Task 2's phase-document contract; Task 3's conventions.

- [ ] **Step 1: Add the module** — `<module>conformance</module>` in the reactor (assumption 3); module pom modelled on `sample/pom.xml` plus two additions: `protobuf-java-util` at compile scope (assumption 26) and `maven-shade-plugin` producing a runnable jar so `DriverRunner` can exec `java -jar` (assumption 31).

- [ ] **Step 2: Declare the models** — Java declares the FK as a separate `UUID` field plus an annotated navigation property (assumption 27), with `OwnerId` and `@IversonTenant`.

- [ ] **Step 3: Wire identities** — Java's CRUD methods take no acting-user parameter (assumption 20), so `DualHeaderCredentials extends CallCredentials` emits both `Authorization: Bearer <service>` and `x-acting-user-authorization: Bearer <acting>` on every call, modelled on `OAuth2ClientCredentials.applyRequestMetadata`. Pass it to the public `IversonClient(channel, credentials)` constructor.

- [ ] **Step 4: Capture the descriptor** — `SchemaRegistrar` reads the package-private `client.mappingStub`, so capture goes on the channel: `ManagedChannelBuilder.forAddress(...).usePlaintext().intercept(new CaptureInterceptor()).build()` (assumption 18); serialize with `JsonFormat.printer()`.

- [ ] **Step 5: Implement phase dispatch, then verify**
```bash
mvn -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install && mvn -B -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Java/conformance Iverson.Clients/Java/pom.xml
git commit -m "add the java conformance driver"
```

---

### Task 8: Verifier and S1 `crud-roundtrip`

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Verifier.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/CrudRoundtripScenario.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/PostgresProbe.cs`

**Interfaces:**
- Consumes: Tasks 3–7's phase documents; Task 2's `DriverRunner` and `Reregistrar`; Task 1's `TokenBroker`.
- Produces: the assertion surface Tasks 9–10 reuse.

- [ ] **Step 1: Implement the registration assertions**
Against the descriptor the driver reported: for each non-`OneToMany` relation, `propertyName != foreignKey`; the foreign key appears among the declared properties; `isArray` is set only for `many_to_many`.

- [ ] **Step 2: Implement `PostgresProbe`** — table name is `ToSnakeCase(TypeName) + "s"` (`SchemaBuilder.cs:30`), so no configuration is needed; a superuser connection is not blinded by RLS (A16).

- [ ] **Step 3: Implement the three-way comparison** — the driver's reported entity, the orchestrator's own `MappingGet`, and the Postgres row must agree. Report which pair disagrees: driver vs gRPC isolates the client's read path; gRPC vs Postgres isolates the server's read path; both agreeing but differing from what was written isolates the write path.

- [ ] **Step 4: Sequence S1**
```
driver register  →  orchestrator re-register with row permissions
driver write     →  driver read (depth 0)
orchestrator     →  MappingGet(article, depth 1)   FK survives hydration, nav property beside it
orchestrator     →  MappingGet(author,  depth 1)   one_to_many resolves via the reverse FK lookup
driver update    →  driver delete
```
The phase boundaries are what make this order real: the driver exits between phases, so the orchestrator's reads run against live rows.

- [ ] **Step 5: Run S1 against a live stack for all five languages** and record the matrix in the implementation report. The harness is expected green on first run — the key-typing fix it depends on landed at `main@b67458d`.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance
git commit -m "add the conformance verifier and the crud-roundtrip scenario"
```

---

### Task 9: S2 `naming-rejected` and S3 `nav-property-rejected`

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/NamingRejectedScenario.cs`
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/NavPropertyRejectedScenario.cs`
- Modify: the Go, Python and TypeScript drivers (one misnamed type each)

**Interfaces:**
- Consumes: Task 8's `Verifier`; Tasks 4–6's drivers.

- [ ] **Step 1: S2** — Go, Python and TypeScript each register a type whose `many_to_one` member is misnamed (`writer_id` against an `Author`). Registration must fail client-side before any RPC (A10); the driver reports the failure and the orchestrator asserts the message names both the actual and the required name. This is a `register`-phase-only scenario. .NET and Java render as `skip` with the recorded reason that their foreign key is a separate declared field, so the server's registration check governs them instead.

- [ ] **Step 2: S3** — orchestrator only, no driver. Hand-build a `Struct` carrying a navigation-property key and post it as a `MappingWriteRequest` over raw gRPC (assumption 11); assert `InvalidArgument` with a message naming both the property and the foreign key. No client can produce this payload any more, which is the point of the FK-only work.

- [ ] **Step 3: Run both scenarios live and record the matrix.**

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the naming-rejected and nav-property-rejected conformance scenarios"
```

---

### Task 10: S4 `interop`

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/InteropScenario.cs`
- Modify: all five drivers (a second entity declaration each)

**Interfaces:**
- Consumes: Task 8's `Verifier`; Task 2's `--keys` fan-out; Tasks 3–7's drivers.

- [ ] **Step 1: Declare `SharedAuthor` and `SharedArticle` in all five drivers** — the same type names and shapes in every language. This is the second entity declaration per driver that S4 costs.

- [ ] **Step 2: Register once** — only the .NET driver runs a `register` phase for S4. The other four must not: `SchemaRegistry.RegisterAsync` replaces the stored descriptor wholesale, so five registrations would leave four overwrites of the descriptor under test.

- [ ] **Step 3: Write, then cross-read** — every language writes one row under its own run-scoped UUID key; the orchestrator collects all five `keys` maps from the write phase and hands the union to every driver's `read` phase via `--keys`; every language reads all five rows. Assert all twenty-five reads agree on the foreign-key value.

- [ ] **Step 4: Run S4 live and record the matrix.**

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.ClientConformance Iverson.Clients
git commit -m "add the interop conformance scenario"
```

---

### Task 11: Demonstrate every scenario can fail

The spec is explicit that this is not optional ceremony: the work that motivated this harness produced three tests that could not fail, and a conformance harness is a large green tick that people will trust.

**Files:** none committed — the evidence goes in the implementation report.

- [ ] **Step 1: Mutate and observe red, one assertion at a time.** At minimum, the two the spec names: reverting Python's relation-property-name helper must turn S1's depth-1 check red, and stubbing out Go's slice branch must turn the many-to-many leg red. Extend the same treatment to the registration assertions, the three-way comparison's Postgres leg, S2's message assertion, S3's `InvalidArgument` assertion and S4's twenty-five-read agreement.

- [ ] **Step 2: Record each mutation, the cell that went red, and the restoration** in the implementation report. A mutation that leaves the matrix green is a defect in the assertion, not a curiosity — fix the assertion and re-run.

- [ ] **Step 3: Confirm the tree is clean** — every mutation reverted, all suites green:
```bash
git status --porcelain
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
cd Iverson.Clients/TypeScript && npm test
cd Iverson.Clients/Python && python3 -m pytest
cd Iverson.Clients/Go && go test ./...
mvn -B -f Iverson.Clients/Java/pom.xml test
```

## Known issues inherited from spec

**Two live defects found while verifying this design are fixed elsewhere.** Both were confirmed against a running stack on 2026-08-09 and are specified in `2026-08-09-relation-key-typing-design.md`, which **Ben chose on 2026-08-09** to split out rather than fold in — it is a production correctness fix, not a test tool, and warrants its own review.

- **Go- and TypeScript-registered entities cannot be read by key.** Neither client maps any type to `CLR_GUID` (A8), so their key columns are `text`, and `EntityRepository` hardcodes `@Key::uuid`. A text-keyed type accepted a write, then failed both `depth=0` and `depth=1` reads.
- **One-to-many resolution is broken for Go, Python and TypeScript.** All three synthesize their relation foreign key as `CLR_STRING` → a `TEXT` column, while `EntityRelationResolver:154` resolves the reverse direction through `FetchByColumnAsync`, which casts to uuid.

The second was introduced by the foreign-key-only work days earlier and passed every review layer, because the many-to-one direction — the one exercised live — is unaffected. Finding it during the design of a conformance harness, rather than by running one, is the argument for building it.

**A driver entity-shape change requires dropping its table by hand.** Type names are stable, registration applies with `SchemaDriftPolicy.Throw` (`SchemaRegistrationOrchestrator.cs:113`), and the service exposes no unregister or drop RPC (`object_mapping.proto:10-15`) — so when a driver's entity gains or loses a column, that type's registration fails with `FailedPrecondition` on every subsequent run, masking the rest of that language's row. The remedy is manual: drop the table and delete its `_iverson_schema` row, then re-run. Accepted rather than automated — a `--reset` would give the harness schema-mutating power it otherwise does not need.

**The harness does not manage the docker compose stack.** It verifies the stack is up and fails with instructions otherwise.

**CI execution is not implemented**, only kept possible. Seeding the TOTP secret on an ephemeral runner remains unsolved and is the one genuine obstacle.
