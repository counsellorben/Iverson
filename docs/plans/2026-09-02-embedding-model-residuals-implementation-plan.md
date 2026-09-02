# Embedding-Model Residuals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-02-embedding-model-residuals-design.md` (commit SHA: `2291d0d`)

**Goal:** Close the five residuals left open by the embedding-model configuration initiative, plus one latent Go defect that verification surfaced on the path this work touches.

**Architecture:** Two server-side registration fixes in one file; a single inheritance semantic implemented across five client libraries; a conformance scenario that grades that semantic, landed as `IVC-DECL-007` through the standard's four-way gate; and two infrastructure items — an enforceable project-reference rule and a Helm template extraction.

**Tech stack:** .NET 10, Java 21/Maven, Python 3 + pytest, TypeScript + vitest, Go, Helm 3, protobuf/gRPC.

---

## Global Constraints

Copied verbatim from the spec. Every task holds to these.

1. **One model per type.** The declaration is class-level, never per-property.
2. **`""` on the wire means "not declared".** A client that sends nothing, and one that sends `""`, are indistinguishable and both resolve to the deployment default. proto3 implicit presence makes this structural, not conventional.
3. **Configured prefix overrides apply to the default model only.** Nothing here changes that.

---

## File Structure

**Create**
- `Iverson.Server/Iverson.ClientConformance/Scenarios/InheritedModelScenario.cs` — the S12 scenario.
- Five fixture pairs, one per language (`S12Declared<Lang>` + `S12Inherited<Lang>`), in each driver's existing models home.
- One arch test in `Iverson.Server/Iverson.ClientConformance.Tests/`.

**Modify**
- Server: `Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`.
- Clients: `DotNet/Iverson.Client.Attributes/IversonEmbeddingModelAttribute.cs`, `Java/.../annotations/IversonEmbeddingModel.java`, `Python/iverson_client/annotations.py`, `Go/iverson/tags.go`.
- Drivers: `DotNet/.../Driver/Program.cs`, `Java/conformance/.../Driver.java`, `Python/conformance/driver.py`, `TypeScript/conformance/driver.ts`, `Go/conformance/main.go`.
- Harness: `Iverson.ClientConformance/Program.cs`, `Requirements.cs`, `docs/standards/iverson-client-standard.md`.
- Harness tests: `Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs` (accessibility only — see T9).
- Chart: `deploy/helm/iverson/templates/_helpers.tpl`, `charts/{api,worker}/templates/deployment.yaml`.

**Test**
- `Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, one registrar test file per client language, and `Iverson.ClientConformance.Tests/`.

---

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here. Load-bearing ones:

- `DeclaredModel` has one caller at `:61`, outside every `try`; `EnsureInitializedAsync` (`:113`) and `BuildDescriptor` (`:126`) are the only service consumers (A3–A6).
- `Iverson.Embeddings` has no Grpc.Core reference (A7); `EmbeddingService.ModelId` returns the options value verbatim (A41); `Get` cannot throw (A42).
- .NET's lookup passes no `inherit:` argument (A13); Java uses `getAnnotation` (A15); TypeScript reads through `Reflect.getMetadata` (A17).
- **A18 is FALSE** — an embedded Go struct registers as a phantom `CLR_STRING` property; that is the defect T6 fixes.
- Python's decorated subclass does not inherit the model (A19); `__mro__` is already walked at decoration time (A20).
- `Capture` records the **outgoing** request, for the .NET driver only; Go and TypeScript omit proto3 defaults (A23).
- `recognizedScenarios` is a flat array of `.Name` constants (A24/A28); `Phase.Register` needs no new value (A25); `IVC-DECL-007` is unused (A26); DECL is in the gate's axis list (A27); no DECL row would double-claim (A39).
- **A31 is PARTLY FALSE** — three files mention `Iverson.Api`, one is a real dependency (A31); the production harness project has none (A33).
- The two env blocks are byte-identical (A34).
- A Go fixture's key needs **both** `iverson_key` and `iverson_guid` (A40).

---

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `2291d0d`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The four client files this plan modifies exist at the cited paths | `IversonEmbeddingModelAttribute.cs`, `IversonEmbeddingModel.java`, `annotations.py`, `tags.go` all read successfully |
| P8 | File path | `Scenarios/InheritedModelScenario.cs` does not exist | `ls` → No such file |
| P15 | Signature | `Verifier.ParseDescriptor(JsonElement)` returns `TypeDescriptor` | `Verifier.cs:85` |
| P16 | File path | `ScriptedDriverRunner` exists for T8's tests | `Iverson.ClientConformance.Tests/ScriptedDriverRunner.cs` |
| P17 | Signature | **FALSE as the spec assumed.** `StripCommentLines` is `private static` in the `public class RequirementsCoverageGateTests`, so a new test class cannot call it | `RequirementsCoverageGateTests.cs:64,274` — T9 widens it to `internal static` |
| P19–P23 | Command | The five test invocations | `dotnet test Iverson.slnx`; `mvn -B -f Iverson.Clients/Java/pom.xml test`; `pytest` (`pyproject.toml:25-26`, `testpaths = ["tests"]`); `npm test` (`package.json:15-16`, `typecheck && vitest run`); `go test ./...` |
| P25 | Command | T10's render sequence needs `helm dependency build` **first**: gitignored `charts/*.tgz` build artifacts silently shadow live subchart edits, so a render without it can show the pre-change output | Twelve `.tgz` present under `deploy/helm/iverson/charts/`; the hazard is recorded in the source initiative's spec and was hit during its verification |
| P26 | Command | Commit messages are lowercase sentences with no Conventional-Commits prefix | `git log --oneline -8` |
| P31 | Code validity | `sf.Anonymous` is a real `reflect.StructField` field, but has **no existing usage in this Go tree** — new usage, not a copied pattern | `grep -rn "\.Anonymous" Iverson.Clients/Go/` → no hits |
| P40 | Consumer impact | `InspectType` has six consumers, all of which see T6's changed field list | `sample/main.go:18`, `conformance/main.go:345`, `registrar.go:81`, `coordinator.go:138,164,701`. No-op for every existing entity: the spec's `go/ast` scan found zero anonymous fields tree-wide |
| P43 | Consumer impact | The five GUID-key declaration forms, one per language | .NET `[IversonKey] public Guid Id` (`S11ModelDotnet.cs:27`); Java `@IversonKey private UUID id` (`S11ModelJava.java:33-34`); Python `id: uuid.UUID = iverson_key()` (`models.py:224`); TypeScript `@IversonGuid()` (`models.ts:32`); Go `iverson_key:"true" iverson_guid:"true"` on a `string` (`models.go`) |

---

## Tasks

### Task 1: Server — resolve after the guard, and reject a multi-valued declaration

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

- [ ] **Step 1: Rewrite `DeclaredModel` to detect a multi-valued declaration.** Read both flag-halves, not just `ModelId`, so a dual-flag property's `ChunkModelId` is no longer dropped unnoticed:

```csharp
var declared = typeDesc.Properties
    .SelectMany(p => new[] { p.IsEmbedding ? p.ModelId      : null,
                             p.IsChunk     ? p.ChunkModelId : null })
    .Where(m => !string.IsNullOrEmpty(m))
    .Distinct(StringComparer.Ordinal)
    .ToList();
```

Zero returns `null` and one returns that value, both unchanged from today. More than one throws `RpcException(InvalidArgument)` naming the type and the conflicting values — `InvalidArgument`, not `FailedPrecondition`, because this file splits input validation from checks against registered state and a multi-valued declaration is malformed input. The call site is outside every `try`, so the exception reaches the client unmodified.

- [ ] **Step 2: Bind the resolved service rather than re-deriving it.** Replace the single pre-guard `resolver.Get(DeclaredModel(typeDesc))` with:

```csharp
var declared = DeclaredModel(typeDesc);

// Resolved once, and bound rather than re-derived. On the undeclared arm this is the loop's only
// Get call; re-deriving the default below the guard would make it two.
var defaultService = declared is null ? resolver.Get(null) : null;
var nextModel      = hasEmbedded ? (declared ?? defaultService!.ModelId) : null;
```

and below the guard's throw, still above `EnsureInitializedAsync` and `BuildDescriptor`:

```csharp
var service = defaultService ?? resolver.Get(declared);
```

`Get(null)` returns the injected default singleton and writes no cache entry, so the one pre-guard call does not weaken the bound.

- [ ] **Step 3: Tests.**
  - Two properties naming different models → `InvalidArgument`.
  - A dual-flag property whose `ModelId` and `ChunkModelId` disagree → `InvalidArgument`.
  - Two properties naming the **same** model → accepted; not a conflict.
  - After a guard rejection, `resolver.DidNotReceive().Get(<rejected model>)`.
  - **`SchemaRegistrationOrchestratorTests.cs:661` (`Received(1).Get(null)`) must stay green unedited.** If it needs touching, Step 2 has been implemented wrongly — that is the whole reason the service is bound rather than re-derived.

- [ ] **Step 4: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "resolve the embedding service after the guard and reject a multi-valued declaration"
```

---

### Task 2: .NET client — inherit the declaration

Independent of every other task except that T7's fixture depends on it.

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonEmbeddingModelAttribute.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

- [ ] **Step 1: Flip the attribute to inherited.** `[AttributeUsage(AttributeTargets.Class, Inherited = false)]` becomes `Inherited = true`. No registrar change is needed: `GetCustomAttribute<T>()` already passes no `inherit:` argument, so it defaults to walking the base chain, and `AllowMultiple = false` means a derived attribute hides the base's rather than raising `AmbiguousMatchException`.

- [ ] **Step 2: Two tests.** A derived type with no declaration of its own sends the parent's model on **both** `ModelId` and `ChunkModelId`; a derived type declaring its own sends **its own**. The second is what keeps "inherits" from becoming "cannot override".

- [ ] **Step 3: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Clients/DotNet/
git commit -m "inherit a declared embedding model from a base class in the dotnet client"
```

---

### Task 3: Java client — inherit the declaration

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonEmbeddingModel.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`

- [ ] **Step 1: Add `@Inherited`** from `java.lang.annotation`, alongside the existing `@Target(ElementType.TYPE)` and `@Retention(RetentionPolicy.RUNTIME)`. No registrar change: the code already calls `cls.getAnnotation(...)`, which honours the marker, rather than `getDeclaredAnnotation`.

- [ ] **Step 2: Two tests.** A subclass with no declaration of its own sends the superclass's model on **both** `modelId` and `chunkModelId`; a subclass declaring its own sends **its own**.

- [ ] **Step 3: Run and commit.**
```bash
mvn -B -f Iverson.Clients/Java/pom.xml test
git add Iverson.Clients/Java/
git commit -m "inherit a declared embedding model from a superclass in the java client"
```

---

### Task 4: Python client — inherit the declaration

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/annotations.py`
- Test: `Iverson.Clients/Python/tests/`

- [ ] **Step 1: Fall back to the MRO when `embedding_model` resolves empty.** `@iverson_entity` sets `cls._iverson_meta` outright with `embedding_model` defaulting to `""`, so a decorated subclass currently gets a fresh meta and does not inherit — even though the same function already walks the MRO to gather inherited annotations. Mirror that existing walk: when the parameter resolves to empty, take the nearest base whose `_iverson_meta` carries a non-empty one.

"Resolves to empty" covers both "not supplied" and an explicit `embedding_model=""`; Python cannot distinguish them given the `""` default, and inheriting on empty is the correct reading under Global Constraint 2.

- [ ] **Step 2: Two tests.** A decorated subclass with no `embedding_model` of its own sends the parent's on **both** `model_id` and `chunk_model_id`; a decorated subclass passing its own sends **its own**.

- [ ] **Step 3: Run and commit.**
```bash
cd Iverson.Clients/Python && pytest
git add Iverson.Clients/Python/
git commit -m "inherit a declared embedding model through the mro in the python client"
```

---

### Task 5: TypeScript client — pin the inherited declaration

**No production change.** `Reflect.getMetadata` already walks the prototype chain, and own metadata already takes precedence over it. This task exists to pin both directions so a future change to the metadata key or lookup cannot silently break them.

**Files:**
- Test: `Iverson.Clients/TypeScript/tests/`

- [ ] **Step 1: Two tests.** A subclass with no decorator of its own sends the parent's model on **both** `modelId` and `chunkModelId`; a subclass carrying its own decorator sends **its own**.

- [ ] **Step 2: Run and commit.**
```bash
cd Iverson.Clients/TypeScript && npm test
git add Iverson.Clients/TypeScript/
git commit -m "pin inherited and overridden embedding-model declarations in the typescript client"
```

---

### Task 6: Go client — skip embedded fields, and inherit by promotion

**Files:**
- Modify: `Iverson.Clients/Go/iverson/tags.go`
- Test: `Iverson.Clients/Go/iverson_test/registrar_test.go`

- [ ] **Step 1: Skip anonymous fields in the field walk.** Add `if sf.Anonymous { continue }` to `InspectType`'s loop, on the rationale that an embedded struct is a method-promotion mechanism, not a property.

This fixes a latent defect, not merely an obstacle: `ParseTag` returns a plain field for an empty tag and `goTypeToClr` discards the `supported` flag on the non-array path, so an embedded struct is currently registered as a string property named after itself. `sf.Anonymous` has no existing usage in this tree — it is standard `reflect.StructField`, but this is new usage rather than a copied pattern.

`InspectType` has six consumers (`sample/main.go:18`, `conformance/main.go:345`, `registrar.go:81`, `coordinator.go:138,164,701`); all see the changed field list, and the change is a no-op for every existing entity because no entity in the tree embeds a struct.

- [ ] **Step 2: Two tests** — a struct embedding a declaring struct inherits its model; a struct that also defines its own `IversonEmbeddingModel()` shadows the promoted one.

- [ ] **Step 3: Run and commit.**
```bash
cd Iverson.Clients/Go && go test ./... && go vet ./...
git add Iverson.Clients/Go/
git commit -m "skip embedded fields in the go field walk and inherit a promoted model declaration"
```

---

### Task 7: Five S12 fixtures and five driver steps

Depends on T2–T6. Lands **before** T8: a driver may carry a step for a scenario nobody runs, because the step is only reached when the harness requests it — but a registered scenario meeting a driver that does not recognise it exits non-zero and reports Broken.

**Files:**
- Create: one fixture pair per language in each driver's existing models home.
- Modify: the five driver files.

**Interfaces:**
- Produces: the scenario name `model-inherited`, the step name `register_inherited_doc`, and the five registered type names `S12Inherited<Lang>` — all consumed by T8.

- [ ] **Step 1: Per language, add the fixture pair.**
  - `S12Declared<Lang>` — carries the declaration, **field-less**, and never registered.
  - `S12Inherited<Lang>` — registered, declares nothing, inherits. Name it exactly `"S12Inherited" + Titlecase(language)`, matching what T8 derives and asserts ordinally, which for the five drivers' tokens gives `S12InheritedDotnet`, `S12InheritedJava`, `S12InheritedPython`, `S12InheritedTypescript`, `S12InheritedGo`.

Each `S12Inherited<Lang>` needs its language's GUID-key declaration — .NET `Guid`, Java `UUID`, Python `uuid.UUID`, TypeScript `@IversonGuid()`, Go the `iverson_guid:"true"` tag *alongside* `iverson_key`, where the tag rather than the field's type drives the column's SQL type. The orchestrator rejects any key whose built `SqlType` is not `UUID`, so a fixture missing it fails registration and takes its language's column with it. Model each on the existing `S11Model<Lang>` fixture, which already does this correctly.

Go's declaring parent is a field-less embedded struct carrying only the method, which T6's change makes harmless:

```go
type S12DeclaredGo struct{}
func (S12DeclaredGo) IversonEmbeddingModel() string { return "nomic-embed-text" }

type S12InheritedGo struct {
    S12DeclaredGo
    Id string `iverson_key:"true" iverson_guid:"true"`
    // tenant, owner, one embedding property, one chunk property — as S11ModelGo
}
```

- [ ] **Step 2: Per driver, add the register step.** Register **only** `S12Inherited<Lang>`, report step `register_inherited_doc` with the captured descriptor, and add `model-inherited` to that driver's recognised-scenario set. Mirror the driver's existing `register_model_doc` step in shape.

- [ ] **Step 3: Confirm each driver still builds.**
```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj
mvn -B -f Iverson.Clients/Java/pom.xml -pl conformance -am -DskipTests package
cd Iverson.Clients/Python && python3 -m py_compile conformance/driver.py conformance/models.py
cd Iverson.Clients/TypeScript && npx tsc -p tsconfig.conformance.json
cd Iverson.Clients/Go && go build -o bin/conformance ./conformance
```

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Clients/
git commit -m "add the S12 inherited-model fixtures and driver steps for all five clients"
```

---

### Task 8: `InheritedModelScenario` and `IVC-DECL-007`

Depends on T7.

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance/Scenarios/InheritedModelScenario.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/Program.cs`, `Requirements.cs`, `docs/standards/iverson-client-standard.md`
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/`

**Interfaces:**
- Consumes: T7's scenario name, step name and the five type names.

- [ ] **Step 1: The scenario.** Mirror `ModelRejectedScenario` in shape: run the register phase across all requested languages, capture each driver's reported descriptor, and assert `model_id` and `chunk_model_id` **equal** the parent's declared value.

Two constraints are load-bearing and neither is optional:

1. **Read the captured descriptor through `Verifier.ParseDescriptor`** (`Verifier.cs:85`), never by indexing the raw `JsonElement`. The five drivers do not serialize alike — .NET, Java and Python emit `"modelId": ""` for an undeclared model, while Go and TypeScript omit the field entirely — and the parser lands an omitted field on the same default as an explicitly-default one.
2. **Assert equality with the expected model id, never inequality with `""`.** Against raw JSON a "not `""`" assertion reads null for an absent field, `null != ""` passes, and the Go and TypeScript columns go green on the exact regression this scenario exists to catch.

- [ ] **Step 2: Land `IVC-DECL-007` in all four places.** `Requirements.cs` is not a list to append to — `RequirementsCoverageGateTests` gates it, so a bare const turns Step 4's run red.
  1. **The standard** — an Active row in DECL's requirement table: *"A type that does not declare an embedding model inherits its parent's declaration"*. Check 1 is bidirectional.
  2. **The const** in `Requirements.cs`, with the rationale-and-discharging-assertion doc comment the axis preamble requires.
  3. **The citation** — the const's identifier referenced from the scenario's assertions, outside `Requirements.cs`, build output and the test project.
  4. **The coverage row** — one new Covered area in DECL's ledger. The existing rows claim `IVC-DECL-001/003/004` and `IVC-DECL-006`, so there is no second claimant.

- [ ] **Step 3: Register the scenario** in `Program.cs`'s `recognizedScenarios`, a flat array append of `InheritedModelScenario.Name`.

- [ ] **Step 4: Tests** in `Iverson.ClientConformance.Tests`, driving the scenario through `ScriptedDriverRunner` as the existing scenario tests do. Include a test that the assertion fails when a driver reports an empty model — the regression the two constraints in Step 1 exist to catch.

- [ ] **Step 5: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.ClientConformance/ Iverson.Server/Iverson.ClientConformance.Tests/ docs/standards/iverson-client-standard.md
git commit -m "grade inherited embedding-model declarations as IVC-DECL-007"
```

---

### Task 9: Make the `Iverson.Api` project-reference rule enforceable

**Files:**
- Create: an arch test in `Iverson.Server/Iverson.ClientConformance.Tests/`
- Modify: `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs` (accessibility only)

- [ ] **Step 1: Widen `StripCommentLines` to `internal static`.** It is currently `private static` (`RequirementsCoverageGateTests.cs:274`) inside a `public class` (`:64`), so a new test class cannot call it. This is the smallest change that lets the arch test reuse the comment stripper rather than duplicate it; no behaviour changes.

- [ ] **Step 2: The arch test, two assertions.**
  1. Every `.cs` under the test project with a real `Iverson.Api` code dependency is in an allowlist of exactly `SchemaProbeTests.cs`.
  2. `Iverson.ClientConformance` itself declares no `Iverson.Api` reference.

**The matching rule must strip whole-line comments and ignore string literals.** Reuse `StripCommentLines` for the first; the second needs **new code** — the gate provides no string-literal handling, and without it the test fails on `RequirementsCoverageGateTests.cs:1361`'s own `Path.Combine("Iverson.Server", "Iverson.Api")`. The other known false positive is prose in `TenantRejectedScenarioTests.cs:31,33`, which the comment stripper covers.

The failure message carries the reason, not just the violation: a probe sharing the server's own constant cannot catch the server changing it.

- [ ] **Step 3: Run and commit.**
```bash
dotnet test Iverson.slnx
git add Iverson.Server/Iverson.ClientConformance.Tests/
git commit -m "enforce that only the schema-probe test depends on iverson.api"
```

---

### Task 10: Extract the env-block emission

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl`, `charts/api/templates/deployment.yaml`, `charts/worker/templates/deployment.yaml`

- [ ] **Step 1: Capture the four renders before changing anything.** `helm dependency build` first — stale `charts/*.tgz` silently shadow live subchart edits.
```bash
cd Iverson.Server/deploy/helm/iverson && helm dependency build
helm template iverson . > /tmp/before-default.yaml
helm template iverson . -f values-local.yaml > /tmp/before-local.yaml
helm template iverson . --set global.activeEmbeddingModel=snowflake-arctic-embed:s > /tmp/before-arctic.yaml
helm template iverson . --set global.activeEmbeddingModel=typo > /tmp/before-typo.yaml
```

- [ ] **Step 2: Extract the emission** into a second named template beside `iverson.activeEmbeddingModel`, and replace both deployments' eleven-line blocks with `{{- include "iverson.embeddingEnv" . | nindent 12 }}`. Both render at twelve spaces, so one `nindent` serves both, and parent-chart template reach from a subchart is already exercised by the existing helper.

- [ ] **Step 3: Re-render and assert byte-identical output.** An extraction that alters one character of emitted YAML has failed regardless of how the result reads.
```bash
for c in default local arctic typo; do diff /tmp/before-$c.yaml /tmp/after-$c.yaml && echo "$c identical"; done
```

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/deploy/helm/iverson/
git commit -m "extract the embedding env block into a shared named template"
```

---

## Tasks NOT in this plan

Inherited from the spec's "Out of scope" section:

- **Cross-language *field* inheritance.** Four of the five inherit a base class's fields as schema properties — .NET, Python (an explicit MRO walk), Java (`getAllFields` walks `getSuperclass()`) and TypeScript (the prototype chain) — while after T6 Go's embedded fields will not. That divergence concerns which *properties* a derived type registers, not which *model* embeds them, and is a separate pre-existing question. Not addressed here.
- **The `IversonDescription` inheritance divergence**, which mirrors the model one and is equally untested. Left alone; only the model declaration is in scope.
- **Any explicit cap on the resolver cache.** Considered, designed, and dropped — the ordering fix in Task 1 is the bound.
