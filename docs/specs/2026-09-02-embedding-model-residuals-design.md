# Embedding-Model Residuals Design

**Goal:** Close the five residuals left open by the embedding-model configuration initiative
(`docs/specs/2026-09-01-helm-embedding-model-configuration-design.md`, merged at `68d68f6`), and fix
one latent Go defect that verification surfaced on the path this work touches.

**Source of the residuals:** each was raised by a task review or the whole-branch review during that
initiative, adjudicated at the time, and deliberately deferred rather than dropped.

---

## Why these five, together

Four of the five are small and independent. The fifth — cross-language subclass semantics — is the
only one with real behavioural risk: it silently decides *which model embeds your vectors*, and it
currently resolves differently in different client languages with nothing testing it. The rest are
batched with it because they touch the same files and the same test suites, so splitting them would
pay the review and CI cost four extra times for no benefit.

---

## Global constraints

Carried forward from the source initiative; every section below holds to them.

1. **One model per type.** The declaration is class-level, never per-property.
2. **`""` on the wire means "not declared".** A client that sends nothing, and one that sends `""`,
   are indistinguishable and both resolve to the deployment default. proto3 implicit presence makes
   this structural, not conventional.
3. **Configured prefix overrides apply to the default model only.** Nothing here changes that.

---

## Section 1 — Server registration-time fixes

Both changes are in `SchemaRegistrationOrchestrator.RegisterAsync`'s phase-1 loop.

### 1a. Resolve after the guard, not before

`resolver.Get(DeclaredModel(typeDesc))` currently runs at `:61`, before the re-registration guard.
A registration the guard then rejects has already permanently cached an `EmbeddingService`. The
conformance harness demonstrates this on every run: its rejection scenario resolves an override model
that is then refused.

The guard needs the resolved model id, not the service, so it is computed without touching the cache:

```csharp
var declared = DeclaredModel(typeDesc);

// Resolved once, and bound rather than re-derived. On the undeclared arm this is the loop's only
// Get call; re-deriving the default below the guard would make it two.
var defaultService = declared is null ? resolver.Get(null) : null;
var nextModel      = hasEmbedded ? (declared ?? defaultService!.ModelId) : null;
```

and below the guard's throw, still above `EnsureInitializedAsync` (`:113`) and `BuildDescriptor`
(`:126`), the only two consumers of the service:

```csharp
var service = defaultService ?? resolver.Get(declared);
```

`resolver.Get(null)` returns the already-registered default singleton and writes no cache entry, so
making that one call before the guard does not weaken the bound.

**The equivalence is of the returned value, not of the call count** — which is why the undeclared arm
binds the service instead of re-deriving it. The value holds in all three cases: a declared model M
resolves to options carrying `ModelId = M`; a declared model equal to the default returns the
singleton whose `ModelId` is that same value; an absent declaration yields the default. The count
matters because `SchemaRegistrationOrchestratorTests.cs:661` asserts `Received(1).Get(null)` —
exactly-once in NSubstitute — and that test exists specifically to observe which argument phase 1
passes the resolver. Re-deriving would turn one call into two and redden it.

**This ordering fix is the whole of the cache bound.** Once resolution happens after the guard, the
reachable key set is the declared models of registrations that get **past the guard** — narrower than
today, and bounded by a `SchemaAdmin`-gated RPC. It is not only *accepted* registrations: `Get` still
precedes `BuildDescriptor` (`:126`), phase-2 cross-validation and the phase-3 writes, any of which can
still reject a registration whose model is by then cached. `Get` stays total — it never throws — so
the four non-registration callers (`IntelligenceStoreConsumer.cs:144,259` and
`ObjectSearchGrpcService.cs:206,387`) are unaffected.

**Rejected alternative, and why (Ben's call, revised):** an explicit `MaxCachedModels` cap was
designed and then dropped. It read as a deployment-wide ceiling — "the 33rd registration fails" — but
`_byModel` is instance state on a per-process DI singleton, so the cap is per-process, per-replica and
order-dependent: with api running multiple replicas behind an HPA, one replica could refuse a
registration its sibling accepts, and a worker could hit the cap during ingest and dead-letter
documents. A bound that cannot be reasoned about deployment-wide is worse than the ordering fix it
was meant to reinforce.

### 1b. Reject a multi-valued declaration

`DeclaredModel` currently takes the first non-empty model and discards the rest. It also reads only
`ModelId` on a property carrying **both** flags, so that property's `ChunkModelId` is dropped
unnoticed. Both close together:

```csharp
var declared = typeDesc.Properties
    .SelectMany(p => new[] { p.IsEmbedding ? p.ModelId      : null,
                             p.IsChunk     ? p.ChunkModelId : null })
    .Where(m => !string.IsNullOrEmpty(m))
    .Distinct(StringComparer.Ordinal)
    .ToList();
```

Exactly one distinct value returns it; zero returns `null`, unchanged from today. **More than one**
throws `InvalidArgument` — this file splits input validation (`InvalidArgument`) from checks against
registered state (`FailedPrecondition`), and a multi-valued declaration is malformed input. It joins the `ValidateIdentifier` cluster directly above it, outside
every `try`, so the exception reaches the client unmodified.

Unreachable from all five clients, which stamp one class-level value. It closes the path a
hand-crafted gRPC request could take.

### Tests

- Two properties naming different models → `InvalidArgument`.
- A dual-flag property whose `ModelId` and `ChunkModelId` disagree → `InvalidArgument`.
- Two properties naming the **same** model → accepted (not a conflict).
- After a guard rejection, `resolver.DidNotReceive().Get(<rejected model>)` — tests the ordering
  directly rather than by proxy, and is what pins the cache bound now that no cap enforces it.
- The existing `SchemaRegistrationOrchestratorTests.cs:661` (`Received(1).Get(null)`) stays green
  **unedited**. That is the point of binding the service rather than re-deriving it; if this test
  needs touching, the implementation has diverged from §1a.

---

## Section 2 — Client inheritance semantics

### The semantic, stated once

> A registered type that does not itself declare an embedding model, but whose parent declares one,
> sends the parent's model. A type that declares its own model overrides the parent's.

### Per-language mechanism

| Client | Mechanism | Change |
|---|---|---|
| .NET | `GetCustomAttribute<T>()` already walks the base chain; only the attribute blocks it | `Inherited = true` |
| Java | code already calls `getAnnotation`, which honours the marker | add `@Inherited` |
| TypeScript | `Reflect.getMetadata` already walks the prototype chain | none — test only |
| Go | method promotion from an embedded struct | see 2a |
| Python | neither, cleanly | see 2b |

### 2a. Go — fix the phantom property first

Go's field walk has no skip for anonymous (embedded) fields. `ParseTag` returns a plain field for an
empty tag, and `goTypeToClr` discards the `supported` flag on the non-array path
(`clr, _ := goScalarToClr(t)`, with unsupported scalars keeping "their historical CLR_STRING
fallback"). An embedded struct is therefore registered as a **string property named after itself**,
which flows into the schema and the table.

This is a latent defect, not merely an obstacle: it means Go has no usable inheritance analogue today,
and any struct embedding silently corrupts the registered schema. The fix is one line in the walk —
skip `sf.Anonymous` — on the rationale that an embedded struct is a method-promotion mechanism, not a
property. It cannot break existing users, and the evidence for that is a scan rather than the
semantics: a `go/ast` pass over all 33 `.go` files finds zero anonymous fields anywhere in the tree.
The semantic argument would be too strong — an embedded struct whose fields are all untagged registers
successfully today, producing a spurious always-null column rather than corruption.

### 2b. Python — follow the pattern already in the function

`@iverson_entity` sets `cls._iverson_meta` outright, with `embedding_model` defaulting to `""`. A
decorated subclass therefore gets a fresh meta and does **not** inherit — even though the same
function already walks the MRO to gather inherited *annotations*:

```python
for base in reversed(cls.__mro__):
    if base is object: continue
    annotations.update(getattr(base, "__annotations__", {}))
```

So fields already inherit and the model does not. The fix makes `embedding_model` consistent with
that existing walk: **when the parameter resolves to empty** — which covers both "not supplied" and
an explicit `embedding_model=""`, since Python cannot distinguish them given the `""` default — take
the nearest base whose `_iverson_meta` carries a non-empty one. Inheriting on empty is the correct
reading under Global Constraint 2, where `""` means "not declared" rather than "declared as nothing".
This reuses an established in-function pattern rather than introducing a mechanism.

### Tests — two per client, ten total

For each of the five: a derived type with no declaration sends the parent's model on **both**
`model_id` and `chunk_model_id`; and a derived type declaring its own model sends **its own**. The
second test is what keeps "inherits" from quietly becoming "cannot override".

---

## Section 3 — Conformance scenario and `IVC-DECL-007`

### What makes it observable

The fixtures declare `nomic-embed-text`, the deployment default, so a subclass that inherited and one
that fell back resolve to the same model server-side and are indistinguishable in `_iverson_schema`.
The harness does not read the resolved value: each driver's capture records the **outgoing**
`SchemaRequest.RootType`, so the scenario sees what the client sent. That discriminates in a
single-model environment and needs no second model pulled.

**Two constraints make that true, and both are load-bearing.** The five drivers do not serialize
alike: .NET, Java and Python emit `"modelId": ""` for an undeclared model, while Go
(`protojson.Marshal` with default options) and TypeScript (ts-proto `toJSON`) **omit the field
entirely**. The discrimination is therefore `"nomic-embed-text"` versus *absent-or-empty*, not
versus `""`.

1. The scenario **must** read the captured descriptor through `Verifier.ParseDescriptor`, which runs
   the driver's JSON through protobuf's own `JsonParser` and lands an omitted field on the same
   default as an explicitly-default one. Never index the raw `JsonElement`.
2. The assertion **must** be equality with the expected model id, never inequality with `""`. Read
   against raw JSON, a "not `""`" assertion sees null for an absent field, `null != ""` passes, and
   the Go and TypeScript columns go green on the exact regression the scenario exists to catch.

Declaring a non-default model instead would require a second Ollama pull, which is why the source
initiative's fixtures declare the default and why these do too.

### Fixtures — one registered type per language

The declaring parent only has to exist; it is never registered.

- `S12Declared<Lang>` — carries the declaration, **not** registered, field-less
- `S12Inherited<Lang>` — registered, declares nothing, inherits

Go's parent is a field-less embedded struct carrying only the method. Once 2a lands it is *harmless*
rather than inert: `InspectType` skips it, but `entityToStruct` (`coordinator.go:498`) and
`fillEntityValue` (`coordinator.go:643`) do not, so a write would still serialize a null-valued key
named after the embedded type. `json_populate_record` ignores keys with no matching column, and the
S12 fixture is register-only regardless.

```go
type S12DeclaredGo struct{}
func (S12DeclaredGo) IversonEmbeddingModel() string { return "nomic-embed-text" }

type S12InheritedGo struct {
    S12DeclaredGo
    Id string `iverson_key:"true" iverson_guid:"true"`
    // tenant, owner, one embedding property, one chunk property
}
```

**Every S12 fixture needs its language's GUID-key declaration**, the same as the S11 fixtures they are
modelled on: .NET `Guid`, Java `UUID`, Python `uuid.UUID`, TypeScript `@IversonGuid()`, and Go the
`iverson_guid:"true"` tag *alongside* `iverson_key` — in Go the tag, not the field's type, drives the
column's SQL type. The orchestrator rejects any key whose built `SqlType` is not `UUID`, so a fixture
missing it fails registration and takes its language's column with it.

### Scenario

`InheritedModelScenario`, mirroring `ModelRejectedScenario`: run the register phase across all
requested languages, capture each driver's reported descriptor, assert `model_id` and
`chunk_model_id` **equal** the parent's declared value. Type names derive mechanically as
`"S12Inherited" + Titlecase(language)`, matching the existing contract and satisfying the
orchestrator's `^[A-Za-z][A-Za-z0-9]*$` identifier pattern. Registration into `recognizedScenarios`
is a flat array append.

### `IVC-DECL-007`

The DECL axis governs client declaration semantics, which is exactly what this is. The ID lands in
four places, each satisfying a different gate check:

1. **The standard** — an Active row in DECL's requirement table: *"A type that does not declare an
   embedding model inherits its parent's declaration"*. Check 1 is bidirectional.
2. **The const** in `Requirements.cs`, with the rationale-and-discharging-assertion doc comment the
   axis preamble requires.
3. **The citation** — the const's identifier referenced from the scenario's assertions, outside
   `Requirements.cs`, build output and the test project.
4. **The coverage row** — one new Covered area in DECL's ledger. The existing rows claim
   `IVC-DECL-001/003/004` and `IVC-DECL-006`, so there is no second claimant.

### Sequencing constraint

The harness emits `Skipped` only for an absent toolchain. A driver that runs but does not recognise a
scenario exits non-zero and reports **Broken** — deliberately, so a real build break is never
reported as a skip. **All five driver steps must therefore land in the same change as the scenario.**
They cannot be added one language at a time without the live matrix going red in between.

---

## Section 4 — Infrastructure

### 4a. Make the project-reference rule enforceable

`Iverson.ClientConformance.Tests` references `Iverson.Api` to build a real serialized-descriptor
oracle. The rule that it must not be used to replace a harness copy — which would defeat the
divergence detection the reference exists to strengthen — is enforced by a comment and nothing else.

A source-scanning test, following `RequirementsCoverageGateTests`' established approach:

1. Every `.cs` under the test project with a real `Iverson.Api` code dependency is in an allowlist of
   exactly `SchemaProbeTests.cs`.
2. `Iverson.ClientConformance` itself declares no `Iverson.Api` reference.

**The matching rule must strip whole-line comments and ignore string literals.** Verification found
three files mentioning `Iverson.Api` and only one is a real dependency — the others are prose in a doc
comment (`TenantRejectedScenarioTests.cs`) and a `Path.Combine` literal
(`RequirementsCoverageGateTests.cs`). A substring match would fail on both.

The existing citation gate covers only half of it. `StripCommentLines`
(`RequirementsCoverageGateTests.cs:274-282`) drops whole-line comments, which handles the doc-comment
case; nothing there knows about string literals, so the `Path.Combine` case needs new code. Reuse the
comment stripper — do not assume the literal handling arrives with it, or the test fails on that gate's
own file.

The failure message carries the reason, not just the violation: a probe sharing the server's own
constant cannot catch the server changing it.

### 4b. Extract the env-block emission

Both deployments carry a byte-identical eleven-line block (verified by `diff`); only the entry lookup
is centralised today. It moves into a second named template beside the existing one, included as
`{{- include "iverson.embeddingEnv" . | nindent 12 }}`. Both render at twelve spaces, so one
`nindent` serves both, and parent-chart template reach from a subchart is already exercised by
`iverson.activeEmbeddingModel`.

**The test is that the extraction changes nothing.** Capture the four renders from the source
initiative's verification set — default, `values-local`, arctic override, and the typo case — before
the change; re-render after; assert byte-identical output. An extraction that alters one character of
emitted YAML has failed regardless of how the result reads.

---

## Verified assumptions

Checked against the codebase at `68d68f6` before this spec was written.

| # | Assumption | Evidence |
|---|---|---|
| A3–A6 | `DeclaredModel` has one caller at `:61`, outside every `try`; `EnsureInitializedAsync` `:113` and `BuildDescriptor` `:126` are the only service consumers | `SchemaRegistrationOrchestrator.cs:34,61,113,126` |
| A7 | `Iverson.Embeddings` has no Grpc.Core reference — verified while the cap was still in scope; retained because it is why `Get` must stay free of transport concerns | `Iverson.Embeddings.csproj` — no Grpc entry |
| A13 | .NET's lookup passes no `inherit:` argument, so it defaults to walking the chain | `SchemaRegistrar.cs:95` |
| A15 | Java uses `getAnnotation`, which honours `@Inherited` | `SchemaRegistrar.java:145` |
| A17 | TypeScript reads through `Reflect.getMetadata`, which walks the prototype chain | `annotations.ts:87-88` |
| A18 | **FALSE** — an embedded struct is not inert; it registers as a phantom `CLR_STRING` property | `tags.go:233-241` (no anonymous skip), `ParseTag` `:186-188`, `registrar.go:306-307` |
| A19 | Python's decorated subclass does not inherit the model | `annotations.py` — `embedding_model` is a defaulted parameter stored directly |
| A20 | `__mro__` is already walked at decoration time | `annotations.py` — the annotations gather loop |
| A23 | The **.NET** driver's `Capture` records the outgoing request. Scoped deliberately: that file is the .NET interceptor only. Go (`protojson.Marshal`, default options) and TypeScript (ts-proto `toJSON`) **omit** proto3 defaults rather than emitting `""` | `Capture.cs:11,62`; `conformance/main.go:373`; `generated/object_mapping.ts:795-796,807-808` |
| A24/A28 | `recognizedScenarios` is a flat array of `.Name` constants | `Program.cs:66-73` |
| A25 | `Phase.Register` exists; no new enum value needed | `DriverProtocol.cs:10-12` |
| A26 | `IVC-DECL-007` is unused | zero occurrences in the standard and `Requirements.cs` |
| A27 | DECL is in the gate's known-axis list | `RequirementsCoverageGateTests.cs:94` |
| A31 | **PARTLY FALSE** — three files mention `Iverson.Api`; one is a real dependency | `SchemaProbeTests.cs:3` (real), `TenantRejectedScenarioTests.cs:31,33` (comment), `RequirementsCoverageGateTests.cs:1361` (path literal) |
| A33 | The production harness project has no `Iverson.Api` reference | `Iverson.ClientConformance.csproj:10-11` |
| A34 | The two env blocks are byte-identical | `diff` of the two extracted blocks returns empty |
| A39 | No DECL coverage row would double-claim the new ID | `iverson-client-standard.md:112-118` |
| A40 | A Go fixture's key needs **both** `iverson_key` and `iverson_guid`; the server rejects any key whose built `SqlType` is not `UUID` | `tags.go:96,100`; `SchemaRegistrationOrchestrator`'s key check, whose message names the Go tag |
| A41 | `EmbeddingService.ModelId` returns the options value verbatim — no normalisation or trimming — so `resolver.Get(M).ModelId == M` for every non-empty M | `EmbeddingService.cs:30` |
| A42 | `Get` cannot throw: beyond a dictionary lookup its only work is constructing an `EmbeddingService`, whose field initializers call `EmbeddingPrefixes.For`, which is total | `EmbeddingPrefixes.cs:44-45` |

**Taken on faith, not individually verified:** `ConcurrentDictionary.Count` cost characteristics,
`@Inherited`'s semantics on
`@Target(TYPE)` annotations, and `nindent`/parent-template reach — the last already exercised by the
existing helper.

---

## Out of scope

- **Cross-language *field* inheritance.** Four of the five inherit a base class's fields as schema
  properties — .NET, Python (an explicit MRO walk), Java (`getAllFields` walks `getSuperclass()`) and
  TypeScript (the prototype chain) — while after 2a Go's embedded fields will not. That
  divergence concerns which *properties* a derived type registers, not which *model* embeds them, and
  is a separate pre-existing question. Not addressed here.
- **The `IversonDescription` inheritance divergence**, which mirrors the model one and is equally
  untested. Left alone; only the model declaration is in scope.
- **Any explicit cap on the resolver cache.** Considered, designed, and dropped — see §1a's rejected
  alternative. The ordering fix is the bound.
