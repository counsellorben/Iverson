# Declarative Tenant Field Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-28-declarative-tenant-field-design.md` (commit SHA: `637726082d77f8a997d91664a6c53baa5b788cfc`)

**Goal:** Give every client a declarative marker for the tenant property so schema registration succeeds, and make omitting it impossible to do silently.

**Architecture:** Each client gains a property-level marker; its registrar finds the marked property and sets `TypeDescriptor.tenant_field` to that property's name. Two checks run client-side — zero markers and multiple markers both throw. .NET's out-of-band `tenantFieldByTypeName` map is removed so the marker is the only mechanism.

**Tech stack:** .NET (attributes + reflection), Java (annotations + reflection), Python (`FieldMeta` dataclass attached as attribute defaults), Go (struct tags + reflect), TypeScript (property decorators + `Reflect.defineMetadata`). Proto contract unchanged — `tenant_field` already exists as field 5.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **`EngagementNotReady`-style reuse is not the pattern here — each client throws its own idiomatic error type**, matching what it already does for the blank extraction hint: `ArgumentException` (.NET), `IllegalArgumentException` (Java), `ValueError` (Python), returned `error` via `fmt.Errorf` (Go), `Error` (TypeScript).
- **Messages name the offending type or properties and state the consequence**, rather than restating the rule — the precedent all five clients already set for the blank extraction hint.
- **Do NOT validate client-side** that the marked property is a declared scalar, or that its SQL type is on the server's four-type allow-list. Both are server invariants with clear messages (`ValidateFieldReference`); replicating them in five languages creates five things that can drift from the authority.
- **No proto change and no codegen re-run.** `tenant_field` is already field 5 and already REQUIRED; the checked-in `generated/` directories for Python, Go and TypeScript are untouched.
- **Commit messages use Conventional Commits with the per-client scope already in use** — the log shows `feat(go-client)`, `feat(python-client)`, `feat(ts-client)`.

## File Structure

**Create**
- `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonTenantAttribute.cs` — the .NET marker.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonTenant.java` — the Java marker.

**Modify**
- `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs` — scan + validate; drop the map parameter and its lookup.
- `Iverson.Server/Iverson.LoadTest/Program.cs` — delete the dictionary; annotate the three benchmark entities.
- `Iverson.Server/Iverson.LoadTest/Entities/{BenchmarkArticle,BenchmarkAuthor,BenchmarkTag}.cs` — mark `TenantId`.
- `Iverson.Clients/DotNet/Iverson.Client.Sample/Models/{Article,Author,Tag,User,UserArticle}.cs` — add and mark `TenantId`.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java` — scan + validate + set.
- `Iverson.Clients/Python/iverson_client/annotations.py` — `tenant: bool` on `FieldMeta`, `iverson_tenant()`, dispatch.
- `Iverson.Clients/Python/iverson_client/core.py` — set `tenant_field` on the descriptor; validate.
- `Iverson.Clients/Go/iverson/tags.go` — `TenantTagKey`, `FieldMeta.Tenant`, validation in `InspectType`.
- `Iverson.Clients/Go/iverson/registrar.go` — set `TenantField` from the marked field.
- `Iverson.Clients/TypeScript/src/annotations.ts` — `@IversonTenant()` decorator + accessor.
- `Iverson.Clients/TypeScript/src/core.ts` — replace the hardcoded `tenantField: ''`; validate.

**Test**
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` — the two map tests become marker tests; add the zero/multiple cases.
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`
- `Iverson.Clients/Python/tests/test_schema_registrar.py`
- `Iverson.Clients/Go/iverson_test/registrar_test.go`
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here (spec's A1–A14):

- A1–A2: all five clients have a declaration site and registrar at the expected paths; no `IversonTenant`/`iverson_tenant` name collision anywhere.
- A3: every registrar walks properties per-member, so "find the marked one" is expressible in each.
- A4: every client already has the blank-hint throw precedent whose error type and message shape this plan mirrors.
- A5: `tenant_field` is proto field 5, already REQUIRED — no proto change, no codegen re-run.
- A6: a seventh independent Go tag key composes with `iverson` kinds.
- A7: `core.ts:258` is TypeScript's only `tenantField` site.
- A8: the .NET map's sites are `SchemaRegistrar.cs:21,35-36`, `SchemaRegistrarTests.cs:608,635`, `LoadTest/Program.cs:153,159`.
- A9/A11: LoadTest's three benchmark entities each have a `public string TenantId`, satisfying the server's scalar + four-type allow-list.
- A10: no Sample model has a tenant property; five registered entities each need one added.
- A12: nothing else reads a client-built `tenant_field` in a way removing the map breaks.
- A13: every client has a registrar test file to extend.
- A14: Python's `FieldMeta` supports an independent flag beside `kind`, and an empty `kind` degrades to a plain field via the dispatch's terminal `else`.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The two new marker files do not already exist | `[ -f ]` on `IversonTenantAttribute.cs` and `IversonTenant.java` — both absent |
| P2 | File path | Python/Go/TS markers extend existing files; no new files needed | markers live in `annotations.py`, `tags.go`, `annotations.ts`, all present |
| P3 | File path | Every Modify target exists | `[ -f ]` on all five registrars plus `LoadTest/Program.cs` — all present |
| P4 | Signature | Each of the five registrars has a `TypeDescriptor` build site with per-property marker info still in scope | `SchemaRegistrar.cs:61` `BuildTypeDescriptor`; `SchemaRegistrar.java:72` `newBuilder` → `:116` `build()`; `core.py:193` `TypeDescriptor(`; `registrar.go:118` `&pb.TypeDescriptor{`; `core.ts:179` `describeEntity` |
| P5 | Signature | **Go needs no `EntityMeta` change** — it has only `TypeName`, `Fields []FieldMeta`, `Relations []FieldMeta` (`tags.go:190-197`), so the registrar scans `Fields` for the marked one rather than reading a type-level slot | Read of `tags.go:190-197` |
| P6 | Signature | Python's generated pb2 carries `tenant_field`, so `TypeDescriptor(tenant_field=...)` binds | `grep tenant_field` in `Python/iverson_client/generated/object_mapping_pb2.py` — present |
| P7 | Signature | TypeScript's descriptor literal exposes `tenantField` in place | `core.ts:257-258` — `authorization: undefined,` then `tenantField: '',` |
| P8 | Command | Per-client test commands | .NET `Iverson.Clients/DotNet/Iverson.Client.slnx`; TS `package.json:15` `"test": "vitest run"`; Java Maven; Python `pytest`; Go `go test ./...` |
| P9 | Command | Commit convention with per-client scopes | `git log`: `feat(go-client)`, `feat(python-client)`, `feat(ts-client)`, `docs(specs)` |
| P10 | Ordering | Task 2 consumes only Task 1's attribute; Tasks 3-6 touch disjoint client directories with no shared symbols | Cross-checked each task's inputs against every other task's created symbols |
| P11 | Ordering | Removing the map and updating its caller in one commit keeps the tree building | `LoadTest/Program.cs:159` is the only production caller (P15) |
| P12 | Code validity | `FieldMeta` is a `@dataclass` whose defaulted fields already follow the undefaulted `kind`, so appending `tenant: bool = False` is valid | `annotations.py:20-21` `@dataclass`; existing defaulted members `is_summary_target: bool = False`, `extract_hint: str = ""` |
| P13 | Code validity | Java's reflective registrar requires `@Target(ElementType.FIELD)` + `@Retention(RetentionPolicy.RUNTIME)` | `IversonMetadata.java:15-16` carries exactly those |
| P14 | Code validity | TS markers register via `Reflect.defineMetadata` on `target.constructor` under a symbol key | `annotations.ts:222-229` (`IversonMetadata`) |
| P15 | Consumer impact | Removing `tenantFieldByTypeName` touches no caller beyond the spec's enumerated set | `grep` returns 7 hits, matching A8 exactly |
| P16 | Consumer impact | Appending a field to Go's or Python's `FieldMeta` breaks no construction site | Go uses a keyed literal `FieldMeta{Name: fieldName}` (`tags.go:125`); Python's `FieldMeta(` calls are keyword-based |
| P17 | Consumer impact | Adding `TenantId` to the Sample models breaks no assertion | The Sample is referenced only by the two `.slnx` files and its own sources; no test asserts its property set |
| P18 | Consumer impact | The new zero-marker validation invalidates every client's pre-existing registrar-test fixtures, none of which carries a tenant property. **Scope differs by the client's discovery mechanism:** Java, Python, Go and TypeScript pass explicit class lists, so the test file bounds the set; .NET scans an assembly, so the whole test project does | Explicit-list clients: `registerAll(Class<?>...)`; `test_schema_registrar.py:89`; `registrar_test.go:48`; `schema-registrar.test.ts:115`. .NET: `SchemaRegistrarTests.cs:111` `new EntityRegistry([…Assembly])` → `EntityRegistry.cs:12-16` `Scan`; `grep -rc "\[IversonEntity\]"` over the test project → 8 files, ~14 classes (`SchemaRegistrarTests.cs` alone: 6). `grep -ci tenant` → 0 in the four non-.NET registrar test files. Registering call sites: .NET 16, Java 56, Python 15, TS 20, Go every `NewSchemaRegistrar(mock, registrarArticle{})`. Added after critical-implementation-review round 1; scope corrected after round 2 |

## Tasks

### Task 1: .NET client

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonTenantAttribute.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs`, `BenchmarkAuthor.cs`, `BenchmarkTag.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

**Interfaces:**
- Produces: `IversonTenantAttribute` (Task 2 consumes it).

- [ ] **Step 1: Add the attribute**

Match the style of `IversonMetadataAttribute.cs` — file-scoped namespace, XML doc, `AttributeUsage` on `Property`:

```csharp
namespace Iverson.Client.Attributes;

/// <summary>
/// Marks the property holding the row's tenant id. The server requires every schema to
/// declare a tenant boundary and rejects registration without one, so exactly one property
/// per entity must carry this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonTenantAttribute : Attribute;
```

- [ ] **Step 2: Write the registrar tests first**

In `SchemaRegistrarTests.cs`, the two existing map tests (`RegisterAllAsync_SetsTenantField_WhenSupplementProvidesEntry` at `:588` and `RegisterAllAsync_LeavesTenantFieldEmpty_WhenSupplementHasNoEntryForType` at `:615`) are replaced. Add three tests:

- the marked property's name reaches `TypeDescriptor.TenantField`
- an entity with no marked property throws `ArgumentException`, message naming the type
- an entity with two marked properties throws `ArgumentException`, message naming both properties

The two authorization tests at `:526` and `:561` are unaffected — do not touch them.

Before running the suite, **every `[IversonEntity]` class in the `Iverson.Client.Core.Tests` assembly** must carry a marked string tenant property — not just the ones in this file. The test fixture builds its registry by *scanning an assembly* (`SchemaRegistrarTests.cs:111` → `EntityRegistry.cs:12-16`), and `RegisterAllAsync` iterates everything that scan finds, so the new zero-marker check throws on the first unmarked class regardless of which test is running. Enumerate them with `grep -rn "\[IversonEntity\]" Iverson.Clients/DotNet/Iverson.Client.Core.Tests/` rather than working from a list here, which would go stale; at time of writing that is 8 files and ~14 classes, including `TestArticle` variants in five `EntityCoordinator*` files and two classes in `GraphAssemblerTests.cs`.

None of them has a tenant property today — the `"TenantId"` string at `:608`/`:611`/`:635` is a dictionary *value* in the map tests, not a property — so each needs a `public string TenantId { get; set; } = string.Empty;` carrying `[IversonTenant]`. The two replaced map tests must then assert against that real property rather than the fabricated name the unvalidated map accepted.

- [ ] **Step 3: Scan and validate in the registrar**

In `BuildTypeDescriptor` (`SchemaRegistrar.cs:61`), find the properties carrying `IversonTenantAttribute`. Zero or more-than-one throws; exactly one sets `TenantField` to that property's name.

The multiple-marker check must be client-side: `tenant_field` is a single string on the wire, so the server receives one name and cannot detect that the author marked two.

- [ ] **Step 4: Remove the map**

Delete the `tenantFieldByTypeName` parameter (`:21`) and its lookup block (`:35-36`). Leave `authorizationByTypeName` alone.

- [ ] **Step 5: Update LoadTest**

Mark `TenantId` on `BenchmarkArticle`, `BenchmarkAuthor` and `BenchmarkTag` (each already has `public string TenantId`). In `Program.cs`, delete the `tenantFieldByTypeName` dictionary (`:153-158`) and drop the second argument from the `RegisterAllAsync` call (`:159`).

This must land in the same commit as Step 4 — `Program.cs:159` is the only production caller, so splitting them leaves the tree unbuildable.

- [ ] **Step 6: Run tests**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx
dotnet build Iverson.Server/Iverson.Server.slnx
```
The second command is what catches the LoadTest call-site change; LoadTest is in the server solution, not the client one.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonTenantAttribute.cs Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs Iverson.Server/Iverson.LoadTest/Entities/BenchmarkAuthor.cs Iverson.Server/Iverson.LoadTest/Entities/BenchmarkTag.cs
git commit -m "feat(dotnet-client): declare the tenant field with [IversonTenant]"
```

---

### Task 2: .NET Sample

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Article.cs`, `Author.cs`, `Tag.cs`, `User.cs`, `UserArticle.cs`

**Interfaces:**
- Consumes: Task 1's `IversonTenantAttribute`.

- [ ] **Step 1: Add and mark a tenant property on each registered entity**

Five of the six models carry `[IversonEntity]`; `AuthorArticleCount` does not — it is an aggregation result type and must be left alone.

Each of the five gains:

```csharp
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
```

placed with the other scalar properties, matching each file's existing formatting. `string` satisfies the server's scalar + four-type allow-list.

The Sample calls `RegisterAllAsync()` with no arguments (`Program.cs:18`) and therefore could not register before this change — it never passed the map either. This task is what makes it work.

- [ ] **Step 2: Build**
```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.slnx
```
The Sample has no tests; a successful build plus the attribute resolving is the check available here.

- [ ] **Step 3: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Article.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Author.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Models/Tag.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Models/User.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Models/UserArticle.cs
git commit -m "feat(dotnet-client): give the sample entities a tenant field"
```

---

### Task 3: Java client

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonTenant.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`

- [ ] **Step 1: Add the annotation**

Mirror `IversonMetadata.java`, which carries `@Target(ElementType.FIELD)` and `@Retention(RetentionPolicy.RUNTIME)` at `:15-16`. The reflective registrar requires both.

- [ ] **Step 2: Write the tests first**

Three cases in `SchemaRegistrarTest.java`: the marked field's name reaches `tenant_field`; zero markers throws `IllegalArgumentException` naming the type; two markers throws naming both fields.

Before running the suite, every entity fixture in this file that a test registers must carry the marker, or the new zero-marker check throws on every existing registering test. Enumerate the fixtures in this file rather than assuming a count — it defines several (`SchemaTestAuthor`, `SchemaTestArticle`, `SearchAnnotationTestEntity`, `MetadataAnnotationTestEntity`, `EnrichmentAnnotationTestEntity`, …). Each needs a string tenant field carrying the marker. Fixtures that are *deliberately* invalid — ones whose test asserts a registration error — must be left alone, since they never reach the descriptor build.

- [ ] **Step 3: Scan, validate, set**

In the field loop that already populates the builder (`SchemaRegistrar.java:72` `newBuilder` through `:116` `build()`), collect fields carrying `IversonTenant`. Zero or more-than-one throws `IllegalArgumentException`; exactly one calls `builder.setTenantField(name)`.

- [ ] **Step 4: Run tests**
```bash
cd Iverson.Clients/Java/client && mvn -o test
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonTenant.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java
git commit -m "feat(java-client): declare the tenant field with @IversonTenant"
```

---

### Task 4: Python client

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/annotations.py`
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`

- [ ] **Step 1: Write the tests first**

Three cases in `test_schema_registrar.py`: the marked field's name reaches `tenant_field` on the built descriptor; zero markers raises `ValueError` naming the type; two markers raises `ValueError` naming both fields.

Before running the suite, every entity fixture in this file that a test registers must carry the marker, or the new zero-marker check raises on every existing registering test. Enumerate the fixtures in this file rather than assuming a count — it defines several (`RegArticle`, `RegAuthor`, `RegDescribedArticle`, `RegEnrichedArticle`, …). Each needs a string tenant field carrying the marker. Fixtures that are *deliberately* invalid — ones whose test asserts a registration error — must be left alone, since they never reach the descriptor build.

- [ ] **Step 2: Add the flag and the factory**

`FieldMeta` is a `@dataclass` whose defaulted members already follow the undefaulted `kind`, so append:

```python
    tenant: bool = False          # marks the field holding the row's tenant id
```

and add the factory beside the others:

```python
def iverson_tenant(description: str = "") -> FieldMeta:
    """Mark the field holding the row's tenant id.

    The server requires every schema to declare a tenant boundary and rejects
    registration without one, so exactly one field per entity must carry this.
    """
    return FieldMeta(kind="", tenant=True, description=description)
```

An independent flag rather than `kind="tenant"`: a `FieldMeta` is the attribute's default value, so a field carries exactly one — modelling tenancy as a kind would stop a tenant field from carrying any other declaration.

- [ ] **Step 3: Collect the marked field in the entity dispatch**

Inside the existing `if meta.kind not in _RELATION_KINDS:` block (`annotations.py:244-248`), which already reads `meta.metadata` and `meta.description`, collect fields where `meta.tenant` is true and record the result on `cls._iverson_meta` alongside the other keys (`:287-302`).

`kind=""` falls through the kind chain to its terminal `else` (`:280-281`), which appends to `plain_fields` — so the field is a plain scalar, which is exactly what the server needs it to be.

- [ ] **Step 4: Validate and set in core.py**

In `_build_request` (`core.py:193`), where `mapping_pb.TypeDescriptor(...)` is constructed, pass `tenant_field=<name>`. Zero or more-than-one marked field raises `ValueError` before the descriptor is built.

- [ ] **Step 5: Run tests**
```bash
cd Iverson.Clients/Python && python3 -m pytest -q
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/annotations.py Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_schema_registrar.py
git commit -m "feat(python-client): declare the tenant field with iverson_tenant"
```

---

### Task 5: Go client

**Files:**
- Modify: `Iverson.Clients/Go/iverson/tags.go`
- Modify: `Iverson.Clients/Go/iverson/registrar.go`
- Test: `Iverson.Clients/Go/iverson_test/registrar_test.go`

- [ ] **Step 1: Write the tests first**

Three cases in `registrar_test.go`: the tagged field's name reaches `TenantField`; zero tags returns an error naming the type; two tags returns an error naming both fields.

Before running the suite, every entity fixture in this file that a test registers must carry the tag, or the new zero-tag check errors on every existing registering test — each `NewSchemaRegistrar(mock, registrarArticle{})` call site included. Enumerate the fixtures in this file rather than assuming a count (`registrarArticle`, …). Each needs a string tenant field carrying the tag. Fixtures that are *deliberately* invalid — ones whose test asserts a registration error — must be left alone, since they never reach the descriptor build.

- [ ] **Step 2: Add the tag key and the field**

Add `TenantTagKey = "iverson_tenant"` beside the other independent key constants, and a `Tenant bool` member to `FieldMeta` beside `Metadata`. Parse it the way `iverson_meta` is parsed — the same `== "true"` rule, not a new truthiness convention.

An independent tag key rather than an `iverson:"tenant"` kind: kinds are mutually exclusive, and the tenant field may legitimately also be a search key. This is the `e4a77ff` lesson, where `metadata` had to move from a kind to an independent key for the same reason.

- [ ] **Step 3: Validate in `InspectType`**

Both checks live in `tags.go`'s `InspectType`, not `registrar.go` — that is where the blank-extraction-hint check already lives (`tags.go:226`), and `InspectType` already returns `(EntityMeta, error)` (`:201`), so no signature change is needed. Follow that precedent rather than the spec's "each registrar performs" phrasing, which describes the other four clients.

- [ ] **Step 4: Set the field in the registrar**

`EntityMeta` carries only `TypeName`, `Fields` and `Relations` (`tags.go:190-197`) — no type-level tenant slot is needed. At the descriptor build (`registrar.go:118` `&pb.TypeDescriptor{`), scan `meta.Fields` for the one with `Tenant` true and set `TenantField` to its name.

- [ ] **Step 5: Run tests**
```bash
cd Iverson.Clients/Go && go test ./... && gofmt -l .
```
`gofmt -l .` must print nothing.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Go/iverson/tags.go Iverson.Clients/Go/iverson/registrar.go Iverson.Clients/Go/iverson_test/registrar_test.go
git commit -m "feat(go-client): declare the tenant field with an iverson_tenant tag"
```

---

### Task 6: TypeScript client

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/annotations.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Test: `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

- [ ] **Step 1: Write the tests first**

Three cases in `schema-registrar.test.ts`: the decorated property's name reaches `tenantField`; zero decorators throws `Error` naming the type; two throws naming both properties.

Before running the suite, every entity fixture in this file that a test registers must carry the decorator, or the new zero-decorator check throws on every existing registering test. Enumerate the fixtures in this file rather than assuming a count — it defines several at module scope (`RegArticle`, `RegDoc`, …) plus classes declared inline inside individual tests. Each needs a string tenant property carrying the decorator. Fixtures that are *deliberately* invalid — such as the undecorated class whose test asserts the `@IversonEntity()` error at `:126-130` — must be left alone, since they never reach the descriptor build.

- [ ] **Step 2: Add the decorator**

Follow `IversonMetadata` (`annotations.ts:222-229`), which registers via `Reflect.defineMetadata` on `target.constructor` under a symbol key, plus an accessor for the registrar to read back.

- [ ] **Step 3: Validate and set in `describeEntity`**

`describeEntity` (`core.ts:179`) currently returns a literal with `tenantField: ''` hardcoded (`:258`). Replace that with the marked property's name, and throw before returning when zero or more than one property is marked.

- [ ] **Step 4: Run tests**
```bash
cd Iverson.Clients/TypeScript && npx vitest run && npx tsc --noEmit
```
Run `npm install` first if `node_modules` is absent. `vitest run`, never bare `vitest` — watch mode never exits.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/src/annotations.ts Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/tests/schema-registrar.test.ts
git commit -m "feat(ts-client): declare the tenant field with @IversonTenant"
```

## Tasks NOT in this plan

Inherited from the spec's Non-goals. A new spec → plan cycle is required to add any of these.

- **`authorization` and `owner_field` for the four non-.NET clients.** They are the same out-of-band trio and equally unimplemented there, but nothing is blocked on them — the server rejects only on missing `tenant_field`. Folding them in would double the design to fix a problem nobody has hit.
- Any proto change. `tenant_field` already exists as field 5 and is already REQUIRED.
- Replicating the server's scalar-type validation client-side.

## Known issues inherited from spec

**The `authorization` gap remains open in four clients.** Java, Python, Go and TypeScript cannot supply `AuthorizationRules` either — TypeScript hardcodes `authorization: undefined` (`core.ts:257`), and the other three have no mechanism at all. This is the same out-of-band trio as `tenant_field` and was found during the same investigation. It is deliberately out of scope: the server does not reject on its absence, so nothing is blocked. It needs its own spec if per-type authorization from non-.NET clients is ever wanted.

**`owner_field` travels with `authorization`**, inside `AuthorizationRules` rather than as a top-level `TypeDescriptor` field, so it is covered by the same deferral.
