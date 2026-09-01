# Critical Design Review: 2026-09-01-helm-embedding-model-configuration-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-01-helm-embedding-model-configuration-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Problem | ok — both stated failures traced: the pull/request 404 and the api/worker divergence; the Part B motivation (`ServiceCollectionExtensions.cs:21` singleton) re-read and correct |
| Scope and decomposition | → §3.1 — the A-then-B ordering claim |
| Part A / Values shape | ok — `global` placement matches `generativeModel`'s own recorded rationale; the two seeded families match `EmbeddingPrefixes.Table` |
| Part A / Three-state prefix mechanism | ok — re-rendered the prototype: nomic-active omits both prefix vars, arctic-active emits `value: ""`; binder probe reconfirms absent→`null`, empty→`""` |
| Part A / Active-entry helper | ok — `_helpers.tpl` still absent from the chart; the prototype's parent-chart named template resolves from the api subchart |
| Part A / Pull loop | ok — `range` over the list rendered three correctly-indented pull lines |
| Part A / Env wiring | ok — insertion anchors present at `api:124` and `worker:119`; both deployments run the same image per `worker/values.yaml:1-2` |
| Part A / Profiles | ok — five profiles carry `global:`, `values-local.yaml` does not; list override replaces |
| Part B / The attribute | ok — `AttributeTargets.Class` precedent at `IversonEntityAttribute.cs:7`; Go's optional-interface precedent at `registrar.go:199-209` |
| Part B / Transport | ok — `model_id` field 8 and `chunk_model_id` field 12 exist; four clients assign `""`, Go omits |
| Part B / Server-side resolution | ok — one production `BuildDescriptor` site; `EmbeddingService` ctor takes `IOptions<>` so a per-model instance is constructible |
| Part B / Dimension probing | ok — `SchemaRegistrationOrchestrator.cs:52-63` already probes before `BuildDescriptor` |
| Part B / Re-registration guard | → §2.1 |
| Part B / Query path | ok — `type_name` present on both search requests |
| Part B / Conformance harness | → §2.2 |
| Out of scope | ok — each exclusion is a scope statement, none load-bearing for correctness |
| Known issues | ok — the `.tgz` hazard reproduced during this review; the prefix-table limitation follows from the stated prefix rule |
| Follow-up project | ok — deferred membership, no design dependency |
| Verified assumptions | → §1 |

### Rules and operands (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | `hasKey $active "documentPrefix"` | ok — over-inclusion: a present-but-empty key still emits (correct, that is arctic). Under-inclusion: an absent key emits nothing, so the property stays `null`. Both rendered |
| R2 | `$active.name \| default .Values.global.activeEmbeddingModel` | ok — re-rendered with a typo'd active model: emits `value: "typo-model"`, which 404s by name |
| R3 | Helper's `eq .name $active` as an identity key | dropped — with a duplicated `name` the helper concatenates two `toYaml` blobs and the second entry's prefixes are silently lost (rendered and confirmed). A duplicate entry is malformed input, not an input class the design must handle; the asked-for behaviour holds for well-formed values |
| R4 | Profile list override semantics | ok — a one-entry override rendered exactly one pull line, so replace not merge |
| R5 | Model resolution: declared → else default | ok — `""` means undeclared on all five clients, so the fallback arm is the current behaviour |
| R6 | Prefix rule keyed on "is this the default model" | ok — a type declaring the same id as the default resolves to the same service and therefore the same overrides; the rule's "a different model" wording covers both directions unambiguously |
| R7 | Guard's "its model differs from the newly declared one" | → §2.1 — both directions fail: absent→present and present→absent |

### Data-flow arrows

| # | Arrow → operation | Disposition |
|---|---|---|
| D1 | values → helm render → env var → .NET binder → `EmbeddingServiceOptions` | ok — **crosses a serialization boundary**; both ends probed, absent/empty/set all distinguishable. Residual at the kubelet hop is already recorded in A10 |
| D2 | class attribute → `model_id`/`chunk_model_id` → `TypeDescriptor` → `BuildDescriptor` → `SchemaDescriptor` | ok — every parameter the consuming operation needs exists on the wire today; no proto change |
| D3 | `SchemaDescriptor` → Postgres JSON → `LoadAsync` → in-memory registry | ok — **crosses a persistence boundary the spec never mentions.** `JsonSerializer.Serialize(descriptor, s_jsonOptions)` at `SchemaRegistry.cs:212`; `s_jsonOptions` (`:263-267`) sets only camelCase and `WriteIndented`, with no `IgnoreReadOnlyProperties`, so `VectorDescriptor`'s positional `ModelId` round-trips as `modelId`. Rows written before this change already carry the singleton's model, so the guard has a value to compare after a cold reload |
| D4 | search request `type_name` → registry → model → embed | ok — an unregistered type fails closed on every RPC (`SchemaRegistry.cs:44-47` comment), so there is no silent default-model arm |
| D5 | driver registration → server state → harness assertion | → §2.2 |
| D6 | `SchemaDescriptor` → `ApplyCollectionAsync` → Qdrant | ok — per-vector dimension flows through; the existing dimension-mismatch throw is the mechanism behind the spec's "fails loudly when dimensions differ" claim |

## 1. Verified-assumptions cross-check

A1–A21, B1–B15, C1–C4 spot-checked against the cited evidence at `main@ce798f8`. All hold as stated, with two notes:

- **B11 — citation stale, fact intact.** The spec cites `SchemaBuilder.cs:330,339-340` for per-vector `NamedVector(name, dimension)`. Those lines now read the chunks-collection return; the actual `NamedVector` sites are `:341` and `:350-351`. The eleven-line shift is the single-chunk-field guard merged at `359893f` — the same commit the spec names as its verification point, so the citation was already stale when written. The assumption itself reconfirms.
- **B12 reconfirmed exactly** — `new VectorDescriptor` / `embedding.ModelId` at `:67`, `:78`, `:168`, no production reader.

### Span check

**One uncovered dependency.** C3 is verified as written — "the harness can assert server-side state directly" is true, `PostgresProbe` exists. But the design does not need *state*; it needs **the resolved model**, and no listed assumption covers "the harness can observe which model a registration resolved to." That is precisely the gap between a correctly-verified item and the fact the design rests on. Verified in-round: it cannot. See §2.2.

## 2. Literal-wrongness findings

### 2.1 — The re-registration guard rejects legitimate schema evolution, in both directions

**Description.** The guard is specified as: "`SchemaRegistry.Get(typeName)` returns the previously registered descriptor. If its model differs from the newly declared one, registration is **rejected**." A descriptor's model lives on `VectorFields[].ModelId` / `ChunkFields[]`, which exist only when the type has embedding or chunk properties. Two ordinary evolutions therefore trip it:

- A type registered with **no** embedded property gains its first `[IversonEmbedding]` or `[IversonChunk]`. Prior model: absent. New model: the default. "Differs" → rejected, with an instruction to drop a collection that does not exist — `SchemaBuilder.cs:197` sets `CollectionName` to `null` when a type has neither vectors nor chunks.
- A type loses its last embedded property. Prior model present, new absent → "differs" → rejected.

**Evidence.** Adding a named vector to an existing type is a supported, implemented migration today: `IntelligenceCollectionManager.ApplyCollectionAsync` (`:69-89`) collects `missingVectors` and calls `MigrateCollectionAsync`, under the comment "Detect dimension mismatches (breaking) and missing named vectors (migration)." The guard as worded would reject the exact case that path exists to serve.

**Proposed fix.** Scope the guard to a model *change*, not to any difference: fire only when the prior descriptor carries a model **and** the new one does **and** the two differ. Absent→present is the migration `ApplyCollectionAsync` already handles; present→absent removes the vectors and cannot corrupt a space that no longer has writers.

### 2.2 — The conformance harness's positive assertion cannot be implemented as described

**Description.** The spec states: "A positive parity assertion made server-side. The harness already probes state directly (`PostgresProbe.cs`), so it asserts that all five drivers' registrations resolve to the same model — without requiring a second model pulled in the conformance environment." The named mechanism cannot reach the model.

**Evidence.**
- `PostgresProbe.FetchRowAsync` (`:54-63`) issues `SELECT * FROM {Quote(TableName(typeName))} WHERE {Quote(keyColumn)} = @key` — an **entity projection row**, not schema state. The resolved model lives in the schema-registry table's serialized `SchemaDescriptor` JSON (`SchemaRegistry.cs:212`).
- `Iverson.ClientConformance.csproj:10-11` references only `Iverson.LoadTest` and `Iverson.Client.Core`. There is no reference to `Iverson.Api`, so the harness cannot deserialize `SchemaDescriptor` — and that separation is deliberate: `PostgresProbe.cs:20-23` records that the harness keeps its *own* copies of server constants precisely so it can catch drift.
- `GetSchemaResponse.SchemaField` carries no model id (the spec's own C4), so the wire is closed too.

This matters beyond a wrong citation: the harness option was selected on the stated premise that the probe already existed, so the decision rested on a capability that does not.

**Proposed fix.** Pick one and state it in the spec. (a) Add a schema-table read to the harness — its own table-name constant and a minimal JSON field read, mirroring the existing deliberate-duplication pattern, without taking a reference to `Iverson.Api`. (b) Expose the model on `SchemaField` and assert it per driver, which the spec currently excludes. (c) Drop the positive assertion and keep only the rejection scenario, which is fully implementable as specified and does pin the cross-client behaviour that actually differs.

## 3. Forced decisions

### 3.1 — Shipping Part A alone opens the silent-corruption window Part B's guard exists to close

**The choice.** Ship A and B together; or ship A with `activeEmbeddingModel` treated as pinned until B lands; or ship A and accept the window.

**Why it is forced.** The spec states the two parts "remain independently implementable in the order A then B — B's fallback is A's default, so A landing alone is a coherent state." A codebase fact cuts across that. `ApplyCollectionAsync` (`:75-79`) already throws when an existing collection's vector dimension differs from the schema's — "Drop and re-register the schema to change dimensions" — so a model swap that *changes* dimension is caught today, before and after Part A. A swap between two models of the **same** dimension is caught by nothing until Part B's guard exists; points upsert by deterministic id and the collection accumulates two vector spaces silently. The spec says exactly this in its guard rationale.

Part A's entire purpose is to make changing the model a one-line values edit. It therefore converts a same-dimension model swap from something nobody would attempt casually into something trivially easy, during whatever interval separates A from B. The spec's ordering claim and its guard rationale are each defensible alone and point opposite ways together.

**The options.**
- **Ship A and B as one unit.** No window. Costs the sequencing the spec set out, and makes the first deliverable the whole thing.
- **Ship A, pin the key.** Land A, and record in `values.yaml` beside `activeEmbeddingModel` that changing it is unsafe until the guard lands. Cheap; relies on a comment.
- **Ship A, accept the window.** Defensible if no model change is planned before B — but the spec should say so rather than leave the ordering claim unqualified.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3 is non-empty, and §2 carries two findings — one that would reject ordinary schema evolution, one that specifies a verification mechanism the harness cannot perform. The design's core is sound: the three-state prefix path is verified at both ends, the wire contract already carries the fields Part B needs, and the persistence boundary the spec never mentions turns out to round-trip the model correctly.
