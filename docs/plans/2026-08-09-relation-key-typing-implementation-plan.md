# UUID Key and Foreign-Key Column Typing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-09-relation-key-typing-design.md` (commit SHA: `a199df21dd82ecb178c847f8f22623fb952a8b4a`)

**Goal:** Make the system's existing UUID-key invariant enforceable at registration and declarable from every client, so text-keyed schemas can no longer register and then fail at read time.

**Architecture:** One new guard in `SchemaRegistrationOrchestrator` rejects a non-`UUID` key column, a non-`UUID` `ManyToOne`/`OneToOne` foreign-key column, and a non-`UUID[]` `ManyToMany` foreign-key column. Go and TypeScript gain a way to declare a UUID-typed property (a struct tag and a property decorator respectively); Go, Python and TypeScript retype their synthesized relation foreign-key property from `CLR_STRING` to `CLR_GUID`. .NET and Java already conform.

**Tech stack:** .NET 10 / xUnit / FluentAssertions / NSubstitute (server); Go 1.x stdlib `testing`; TypeScript / vitest / `reflect-metadata`; Python / pytest.

---

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — add the key-column and foreign-key-column SQL-type guard.
- `Iverson.Clients/Go/iverson/tags.go` — add the `iverson_guid` tag key and its `FieldMeta` flag.
- `Iverson.Clients/Go/iverson/registrar.go` — honour the tag in the property loop; retype the synthesized foreign key.
- `Iverson.Clients/Go/sample/models/{article,author,tag}.go` — tag the key fields.
- `Iverson.Clients/TypeScript/src/annotations.ts` — add `@IversonGuid()` and `getGuidFields()`.
- `Iverson.Clients/TypeScript/src/core.ts` — consult the guid metadata; retype the synthesized foreign key.
- `Iverson.Clients/TypeScript/sample/models/{Article,Author,Tag}.ts` — decorate the key fields.
- `Iverson.Clients/Python/iverson_client/core.py` — retype the synthesized foreign key.
- `Iverson.Clients/Python/sample/models.py` — declare `id: uuid.UUID`.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — new guard cases; two existing fixtures retyped (see Task 1, Step 1).
- `Iverson.Clients/Go/iverson/coordinator_test.go` — `assertFkProperty` expectation flips; new tag test.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts` — new decorator test; new synthesized-FK type test.
- `Iverson.Clients/Python/tests/test_schema_registrar.py` — synthesized-FK type assertions flip.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here:

- **B1** Go's tag vocabulary can take a new tag without disturbing the relation/tenant split — `iverson_key`, `iverson_tenant`, `iverson_search_key` are independent tags parsed alongside `iverson`.
- **B2** TypeScript has a precedent for a decorator overriding an unobservable type — `@IversonArray(elementType)` at `annotations.ts:250`.
- **B3** Python's `uuid` annotation yields `CLR_GUID` — `_PY_TO_CLR` maps both `"uuid"` and `"UUID"` (`core.py:37-38`).
- **B4** The guard's blast radius is limited to samples on the client side — client test fixtures mock the transport, so a server-side guard never evaluates them. (Scoped to *client* fixtures; see the server-fixture note in Task 1.)
- **B5** .NET and Java samples already declare UUID keys — `Article.cs:9` `public Guid Id`; `Article.java:17` `private UUID id`.
- **B6** The guard belongs in `SchemaRegistrationOrchestrator` — `:53-66` already performs `owner_field` and mandatory `tenant_field` checks, both throwing `RpcException(InvalidArgument)`.
- **B7** ❌ FAILED — Go/Python/TS emit `CLR_STRING` → `TEXT`; `EntityRelationResolver:154` → `FetchByColumnAsync` casts `@Key::uuid`. This plan fixes it.
- **B8** ❌ FAILED — `EntityRepository` hardcodes `@Key::uuid` in four predicates and `Guid.Parse` in a fifth. This plan fixes it by making text keys unregisterable.
- **B9** A ManyToMany foreign-key column's SQL type is distinguishable from a ManyToOne's — `ClrTypeToSql(t, isArray)` (`SchemaBuilder.cs:278-281`); `ClrGuid` + array → `UUID[]` (`:252`), scalar `ClrGuid` → `UUID` (`:236`).

Also inherited: Go's `goScalarToClr` has no UUID case and TypeScript's `jsTypeToClr` defaults to `CLR_STRING`, so neither client can currently declare a UUID column at all.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All nine modify-targets and four test files exist at the cited paths | Each read or grep'd in-round; e.g. `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (not `.../Schema/`, where the spec's prose might suggest) |
| 2 | Signature | `ColumnDescriptor` exposes the SQL type as `SqlType` | `SchemaDescriptor.cs:51` `public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable)` |
| 3 | Signature | The guard's kind switch uses `Iverson.Api.Schema.RelationKind` with members `OneToOne, OneToMany, ManyToOne, ManyToMany` | `SchemaDescriptor.cs:67` |
| 4 | Ordering | The `SchemaDescriptor` is available where the guard goes | `SchemaRegistrationOrchestrator.cs:50` builds it; the `owner_field`/`tenant_field` checks are at `:54-66`; the relation loop is at `:71-80` |
| 5 | Ordering | The guard covers dependent types, not just the root | `:33` `foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))` — the whole body, including the guard, runs per type |
| 6 | Consumer impact | Exactly two existing server tests break under the new guard | `SchemaRegistrationOrchestratorTests.cs:205` (`SimpleType("Comment","Body","ArticleId")` → `ArticleId` is `ClrString`) and `:441` (`SimpleType("Widget","Name","OwnerId")` → `OwnerId` is `ClrString`). Swept all six relations in the file: `:233`/`:432` declare `ClrGuid` + `IsArray` explicitly, `:394` names no column (membership check fires first), `:413` is `OneToMany` (exempt) |
| 7 | Consumer impact | No other server test reaches the guard | Only three test files construct a `SchemaRegistrationOrchestrator` (`SchemaRegistrationOrchestratorTests`, `ObjectMappingGrpcServiceTests`, `RegisterSchemaAuthorizationIntegrationTests`); the latter two declare no `Client.Contracts.RelationDescriptor` and their `SimpleType` helpers use `ClrType.ClrGuid` keys (`ObjectMappingGrpcServiceTests.cs:153`, `RegisterSchemaAuthorizationIntegrationTests.cs:200`) |
| 8 | Consumer impact | The ten other test files that build `ColumnDescriptor`s directly are unaffected | They construct `SchemaDescriptor`s in-memory and never call `RegisterAsync`; the guard is registration-only |
| 9 | Signature | Go's property loop has a single override point that preserves `isArray` | `registrar.go:69` `clrType, isArray, err := goTypeToClr(sf.Type)`, feeding `:87-88`. `goTypeToClr` has exactly one caller (`:69`) and `goScalarToClr` two (`:212`, `:218`), both inside `goTypeToClr` |
| 10 | Signature | Go tag parsing is per-tag via `sf.Tag.Get` | `tags.go:230-237`, e.g. `fm.IsKey = sf.Tag.Get(KeyTagKey) == "true"` |
| 11 | Signature | TypeScript's decorator-metadata pattern to mirror | `annotations.ts:240` `const IVERSON_ARRAY_KEY = Symbol('iverson:array')`, `:250` the decorator, `:259` `getArrayFields(target)` |
| 12 | Signature | TypeScript's single clrType derivation point, and the function enclosing it | `core.ts:280` `const clrType = arrayElement ?? (designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING)`; `jsTypeToClr` (`:73`) has this one caller. The loop (`:265-291`), the `arrayFields` resolution (`:216`), and `:280` all live inside **`describeEntity`** (`:200`) |
| 13 | Code validity | A guid key is scalar, so `core.ts:271-278`'s "array without `@IversonArray`" throw is not engaged | `looksArray` is `designType === Array || Array.isArray(instance[field])`; a `string` key is neither |
| 14 | Consumer impact | The synthesized-FK `clr_type` assertions that must flip | Go `coordinator_test.go:190` in `assertFkProperty`; Python `test_schema_registrar.py:315`. **TypeScript has none** — its `CLR_STRING` hits at `:570`, `:625`, `:658` are `@IversonArray` and `GetSchema` fixtures, so Task 3 adds a new test rather than flipping one |
| 15 | Consumer impact | Sample key fields to change | Go `sample/models/{tag,author,article}.go:5,5,7` `Id string \`iverson_key:"true"\``; TS `sample/models/{Tag,Article,Author}.ts:7,15,7` `id: string = ''`; Python `sample/models.py:19,26,35` `id: str = iverson_key()` |
| 16 | Command | Server tests: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` | The `.csproj` exists at that path |
| 17 | Command | Go tests: `go test ./...` from `Iverson.Clients/Go` | `go.mod` is at `Iverson.Clients/Go/go.mod` |
| 18 | Command | TypeScript tests: `npm test` from `Iverson.Clients/TypeScript` | `package.json` `scripts.test` = `"npm run typecheck && vitest run"` — type-checks `tsconfig.test.json` before running |
| 19 | Command | Python tests: `pytest` from `Iverson.Clients/Python` | `pyproject.toml:25-26` `[tool.pytest.ini_options] testpaths = ["tests"]` |
| 20 | Ordering | Tasks 1–4 are mutually independent | Disjoint file sets across four language trees; the client tasks mock the transport (B4), so none depends on the server guard existing |
| 21 | Code validity | Server test style | `SchemaRegistrationOrchestratorTests.cs:1-10` — xUnit `[Fact]`, FluentAssertions, NSubstitute; existing rejection tests assert `ex.Which.Status.Detail.Should().Contain(...)` (`:401`) |
| 22 | Code validity | Client test styles and the specific helpers/fixtures the plan's test code calls | Go: plain `testing` with `t.Errorf` helpers, no testify (`coordinator_test.go:184-200`). TS: vitest `describe`/`it`/`expect` with decorators enabled; the relations block is `describe('_buildRequest — relations')` (`schema-registrar.test.ts:244`), which indexes properties inline via `Object.fromEntries` (`:164`, `:206`) rather than a helper — **`propsOf` (`:335`, `:410`) is scoped to two other describes and there is no `propertiesOf`**; top-level fixtures are `RegAuthor` (`:37`) and `RegArticle` (`:48`, ManyToOne only — **no many-to-many fixture exists**). Python: pytest plain asserts with a `make_stub()` helper (`test_schema_registrar.py:305-320`) |

## Tasks

### Task 1: Server registration guard

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:71-80`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

Note on fixtures: the spec's B4 scopes fixture impact to *client* fixtures. Two **server** fixtures also break, and correctly so — their foreign key really is a `TEXT` column. Step 1 retypes them; that is a fixture correction, not a weakening of the guard.

- [ ] **Step 1: Retype the two server fixtures whose foreign key is currently text**

  In `SchemaRegistrationOrchestratorTests.cs`, `SimpleType`'s `extraScalars` are all `ClrString`, which makes an FK named through that helper a `TEXT` column. Two tests rely on such an FK registering successfully. Give each its own `ClrGuid` FK property instead of routing it through `extraScalars`:

  - `RegisterAsync_WithManyToOneRelation_DoesNotThrow` (`:205`) — build `SimpleType("Comment", "Body")` and add
    ```csharp
    td.Properties.Add(new PropertyDescriptor { Name = "ArticleId", ClrType = ClrType.ClrGuid });
    ```
  - `RegisterAsync_WithWellFormedManyToOneForeignKey_Registers` (`:441`) — build `SimpleType("Widget", "Name")` and add
    ```csharp
    td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrGuid });
    ```

  Leave `:233` and `:432` alone — both already declare `ClrType = ClrType.ClrGuid, IsArray = true`.

- [ ] **Step 2: Write the failing guard tests**

  Add to `SchemaRegistrationOrchestratorTests.cs`. Each rejection test asserts `InvalidArgument` and that the detail names the offending field, matching the existing convention at `:401`.

  ```csharp
  [Fact]
  public async Task RegisterAsync_WithNonUuidKeyColumn_ThrowsInvalidArgument()
  {
      var td = new TypeDescriptor { TypeName = "Widget", TenantField = "TenantId" };
      td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrString, IsKey = true });
      td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

      var ex = await act.Should().ThrowAsync<RpcException>();
      ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
      ex.Which.Status.Detail.Should().Contain("Id").And.Contain("UUID");
  }

  [Fact]
  public async Task RegisterAsync_WithNonUuidManyToOneForeignKeyColumn_ThrowsInvalidArgument()
  {
      var td = SimpleType("Widget", "Name", "OwnerId");   // OwnerId is ClrString → TEXT
      td.Relations.Add(new Client.Contracts.RelationDescriptor
      {
          PropertyName = "Owner",
          Kind         = Client.Contracts.RelationKind.ManyToOne,
          RelatedType  = "User",
          ForeignKey   = "OwnerId"
      });

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

      var ex = await act.Should().ThrowAsync<RpcException>();
      ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
      ex.Which.Status.Detail.Should().Contain("OwnerId").And.Contain("UUID");
  }

  [Fact]
  public async Task RegisterAsync_WithScalarManyToManyForeignKeyColumn_ThrowsInvalidArgument()
  {
      var td = SimpleType("Widget", "Name");
      td.Properties.Add(new PropertyDescriptor
          { Name = "TagIds", ClrType = ClrType.ClrGuid });   // UUID, not UUID[]
      td.Relations.Add(new Client.Contracts.RelationDescriptor
      {
          PropertyName = "Tags",
          Kind         = Client.Contracts.RelationKind.ManyToMany,
          RelatedType  = "Tag",
          ForeignKey   = "TagIds"
      });

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

      var ex = await act.Should().ThrowAsync<RpcException>();
      ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
      ex.Which.Status.Detail.Should().Contain("TagIds").And.Contain("UUID[]");
  }

  [Fact]
  public async Task RegisterAsync_WithOneToManyRelation_DoesNotCheckForeignKeyColumnType()
  {
      // The FK lives on the related type's row; nothing on this type is checked.
      var td = SimpleType("Widget", "Name");
      td.Relations.Add(new Client.Contracts.RelationDescriptor
      {
          PropertyName = "Children",
          Kind         = Client.Contracts.RelationKind.OneToMany,
          RelatedType  = "Gadget",
          ForeignKey   = "WidgetId"
      });

      var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

      await act.Should().NotThrowAsync();
  }
  ```

  The existing `RegisterAsync_WithManyToManyArrayForeignKeyColumn_DoesNotThrow` (`:422`) is already the well-formed-`UUID[]` acceptance case; Step 1's retyped `:441` is the well-formed-`UUID` one. No further acceptance test is needed.

- [ ] **Step 3: Run the tests and confirm the new ones fail**

  ```bash
  dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter FullyQualifiedName~SchemaRegistrationOrchestratorTests
  ```

  The four new tests must fail (three expecting a throw that doesn't happen; the `OneToMany` one should already pass). If a rejection test passes before the guard exists, it is asserting something else — fix the test, not the expectation.

- [ ] **Step 4: Implement the guard**

  In `SchemaRegistrationOrchestrator.RegisterAsync`, add the key check immediately after the `tenant_field` validation at `:66`:

  ```csharp
  // The key column is compared against a uuid parameter in every EntityRepository
  // predicate (FetchByKey/FetchMany/FetchByColumn/Delete/Update). A non-UUID key
  // registers cleanly and then fails every read with Postgres 42883.
  if (!string.Equals(descriptor.KeyColumn.SqlType, "UUID", StringComparison.Ordinal))
  {
      throw new RpcException(new Status(StatusCode.InvalidArgument,
          $"Key property '{descriptor.KeyColumn.Name}' on '{descriptor.TypeName}' has SQL type " +
          $"'{descriptor.KeyColumn.SqlType}', but a key column must be UUID. Declare the key as a " +
          $"GUID/UUID-typed property in your client model."));
  }
  ```

  Then extend the existing relation loop at `:71-80`. Keep the membership check first — `RegisterAsync_WithManyToOneForeignKeyMatchingNoColumn_ThrowsInvalidArgument` (`:386`) asserts the "not a declared property" message, which the type check must not preempt:

  ```csharp
  foreach (var relation in descriptor.Relations.Where(r => r.Kind != Schema.RelationKind.OneToMany))
  {
      var column = descriptor.ScalarColumns.FirstOrDefault(c =>
          string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

      if (column is null)
      {
          throw new RpcException(new Status(StatusCode.InvalidArgument,
              $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
              $"declares foreign key '{relation.ForeignKey}', which is not a declared property."));
      }

      // ManyToMany's foreign key is a list of ids (UUID[]); the others are a single id (UUID).
      // Only the OneToMany reverse lookup compares an FK column in SQL, so the UUID[] arm is a
      // consistency rule — but a TEXT[] column would still be wrong by construction.
      var requiredSqlType = relation.Kind == Schema.RelationKind.ManyToMany ? "UUID[]" : "UUID";
      if (!string.Equals(column.SqlType, requiredSqlType, StringComparison.Ordinal))
      {
          throw new RpcException(new Status(StatusCode.InvalidArgument,
              $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
              $"declares foreign key '{relation.ForeignKey}' with SQL type '{column.SqlType}', " +
              $"but a {relation.Kind} foreign key must be {requiredSqlType}. Declare it as a " +
              $"GUID/UUID-typed property{(relation.Kind == Schema.RelationKind.ManyToMany ? " array" : "")}."));
      }
  }
  ```

- [ ] **Step 5: Run the full API test suite**

  ```bash
  dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
  ```

  All tests pass, including the four new ones and the two retyped in Step 1.

- [ ] **Step 6: Commit**
  ```bash
  git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
          Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
  git commit -m "require uuid key and foreign key column types at registration"
  ```

### Task 2: Go — `iverson_guid` tag and UUID foreign keys

**Files:**
- Modify: `Iverson.Clients/Go/iverson/tags.go`, `Iverson.Clients/Go/iverson/registrar.go:69,128`
- Modify: `Iverson.Clients/Go/sample/models/article.go:7`, `author.go:5`, `tag.go:5`
- Test: `Iverson.Clients/Go/iverson/coordinator_test.go:190`

- [ ] **Step 1: Write the failing tests**

  In `coordinator_test.go`, change `assertFkProperty`'s expectation from `pb.ClrType_CLR_STRING` to `pb.ClrType_CLR_GUID` (`:190-192`, message text included). Then add a tag test:

  ```go
  func TestGuidTagYieldsClrGuid(t *testing.T) {
      type GuidTagEntity struct {
          Id       string `iverson_key:"true" iverson_guid:"true"`
          Name     string
          TenantId string `iverson_tenant:"true"`
      }

      props := propsByName(t, &GuidTagEntity{})

      if got := props["Id"].ClrType; got != pb.ClrType_CLR_GUID {
          t.Errorf("Id.ClrType = %v, want CLR_GUID", got)
      }
      if got := props["Name"].ClrType; got != pb.ClrType_CLR_STRING {
          t.Errorf("Name.ClrType = %v, want CLR_STRING (untagged string stays a string)", got)
      }
  }
  ```

- [ ] **Step 2: Run and confirm failure**
  ```bash
  cd Iverson.Clients/Go && go test ./...
  ```
  Both the retyped `assertFkProperty` assertions and `TestGuidTagYieldsClrGuid` must fail.

- [ ] **Step 3: Add the tag**

  In `tags.go`, beside `KeyTagKey` (`:100`):
  ```go
  // GuidTagKey marks a property as a UUID column: `iverson_guid:"true"`. Go has no
  // UUID type in this client's dependency set, so the tag carries what the type cannot.
  const GuidTagKey = "iverson_guid"
  ```
  Add `IsGuid bool` to `FieldMeta` beside `IsKey` (`:132`), and parse it beside the other boolean tags (`:235`):
  ```go
  fm.IsGuid = sf.Tag.Get(GuidTagKey) == "true"
  ```

- [ ] **Step 4: Honour the tag and retype the synthesized foreign key**

  In `registrar.go`, immediately after `:69`'s `goTypeToClr` call — this preserves `isArray`, so a tagged slice becomes `UUID[]`:
  ```go
  if fm.IsGuid {
      clrType = pb.ClrType_CLR_GUID
  }
  ```
  At `:128`, change the synthesized foreign-key property's `ClrType` from `pb.ClrType_CLR_STRING` to `pb.ClrType_CLR_GUID`. Leave `:129`'s `IsArray: fm.RelationKind == KindManyToMany` unchanged — it is what makes a many-to-many foreign key render as `UUID[]`.

- [ ] **Step 5: Tag the sample key fields**

  `sample/models/tag.go:5`, `author.go:5`, `article.go:7`:
  ```go
  Id string `iverson_key:"true" iverson_guid:"true"`
  ```

- [ ] **Step 6: Run the suite**
  ```bash
  cd Iverson.Clients/Go && go test ./... && go vet ./...
  ```

- [ ] **Step 7: Commit**
  ```bash
  git add Iverson.Clients/Go/iverson/tags.go Iverson.Clients/Go/iverson/registrar.go \
          Iverson.Clients/Go/iverson/coordinator_test.go Iverson.Clients/Go/sample/models/
  git commit -m "declare uuid columns and foreign keys from the go client"
  ```

### Task 3: TypeScript — `@IversonGuid()` and UUID foreign keys

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/annotations.ts:240-261`, `src/core.ts:280,318`
- Modify: `Iverson.Clients/TypeScript/sample/models/Article.ts:15`, `Author.ts:7`, `Tag.ts:7`
- Test: `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

- [ ] **Step 1: Write the failing tests**

  No existing assertion covers the synthesized foreign key's `clrType`, so both tests here are new. Add them inside `describe('_buildRequest — relations')` (`schema-registrar.test.ts:244`), following that block's own idiom — build with `registrar._buildRequest(...)` and index with `Object.fromEntries(...)`, as at `:164` and `:206`. The block already declares fixtures locally inside a test (`NavArticle`, `:265-275`); the many-to-many one does the same, since no top-level fixture carries a `@ManyToMany`.

  ```typescript
  it('registers a @IversonGuid property as CLR_GUID and leaves untagged strings alone', () => {
      @IversonEntity()
      class GuidKeyEntity {
          @IversonKey() @IversonGuid()
          id: string = '';
          name: string = '';
          @IversonTenant()
          tenantId: string = '';
      }

      const registrar = new SchemaRegistrar(makeStub());
      const req = registrar._buildRequest(GuidKeyEntity);
      const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

      expect(props['Id'].clrType).toBe(ClrType.CLR_GUID);
      expect(props['Name'].clrType).toBe(ClrType.CLR_STRING);
  });

  it('synthesizes relation foreign keys as CLR_GUID', () => {
      const registrar = new SchemaRegistrar(makeStub());
      const props = Object.fromEntries(
          registrar._buildRequest(RegArticle).rootType!.properties.map(p => [p.name, p]));
      expect(props['RegAuthorId'].clrType).toBe(ClrType.CLR_GUID);
      expect(props['RegAuthorId'].isArray).toBe(false);

      @IversonEntity()
      class TaggedPost {
          @IversonKey() id: string = '';
          @IversonTenant() tenantId: string = '';
          @ManyToMany(() => RegAuthor)
          regAuthorIds: string[] = [];
      }

      const mtm = Object.fromEntries(
          new SchemaRegistrar(makeStub())._buildRequest(TaggedPost).rootType!.properties.map(p => [p.name, p]));
      expect(mtm['RegAuthorIds'].clrType).toBe(ClrType.CLR_GUID);
      expect(mtm['RegAuthorIds'].isArray).toBe(true);
  });
  ```

  Match the surrounding tests' exact registrar/stub construction rather than the sketch above if the block instantiates them differently.

- [ ] **Step 2: Run and confirm failure**
  ```bash
  cd Iverson.Clients/TypeScript && npm test
  ```

- [ ] **Step 3: Add the decorator**

  In `annotations.ts`, mirroring `IversonArray` (`:240-261`):
  ```typescript
  const IVERSON_GUID_KEY = Symbol('iverson:guid');

  /**
   * Declares a property as a UUID column. TypeScript has no UUID type — a GUID is
   * carried as a `string` — so the runtime cannot distinguish it from any other
   * string. The server requires key and foreign-key columns to be UUID.
   */
  export function IversonGuid(): PropertyDecorator {
      return (target, propertyKey) => {
          const existing: Set<string> =
              Reflect.getMetadata(IVERSON_GUID_KEY, target.constructor) ?? new Set();
          existing.add(String(propertyKey));
          Reflect.defineMetadata(IVERSON_GUID_KEY, existing, target.constructor);
      };
  }

  export function getGuidFields(target: Function): Set<string> {
      return Reflect.getMetadata(IVERSON_GUID_KEY, target) ?? new Set();
  }
  ```

- [ ] **Step 4: Consult it in the property loop and retype the synthesized foreign key**

  In `core.ts`, import `getGuidFields` alongside the existing `getArrayFields` import and add `const guidFields = getGuidFields(cls);` beside `const arrayFields = getArrayFields(cls);` at `core.ts:216`, inside `describeEntity`. Then change `:280` to consult it before falling back to `jsTypeToClr`:
  ```typescript
  const clrType = arrayElement
      ?? (guidFields.has(fieldName)
          ? ClrType.CLR_GUID
          : (designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING));
  ```
  `arrayElement` keeps precedence so an `@IversonArray`-declared element type still wins; a guid key is scalar, so `:271-278`'s array-without-decorator throw is not engaged.

  At `:318`, change the synthesized foreign-key property's `clrType` from `ClrType.CLR_STRING` to `ClrType.CLR_GUID`. Leave `:319`'s `isArray: rel.kind === 'many_to_many'` unchanged.

- [ ] **Step 5: Decorate the sample key fields**

  `sample/models/Tag.ts:7`, `Article.ts:15`, `Author.ts:7`, adding the import to each:
  ```typescript
  @IversonKey()
  @IversonGuid()
  id: string = '';
  ```

- [ ] **Step 6: Run the suite**
  ```bash
  cd Iverson.Clients/TypeScript && npm test
  ```
  `npm test` runs `tsc -p tsconfig.test.json` before vitest, so this also type-checks the samples and tests.

- [ ] **Step 7: Commit**
  ```bash
  git add Iverson.Clients/TypeScript/src/annotations.ts Iverson.Clients/TypeScript/src/core.ts \
          Iverson.Clients/TypeScript/tests/schema-registrar.test.ts Iverson.Clients/TypeScript/sample/models/
  git commit -m "declare uuid columns and foreign keys from the typescript client"
  ```

### Task 4: Python — UUID foreign keys and sample keys

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py:254`
- Modify: `Iverson.Clients/Python/sample/models.py:19,26,35`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py:315`

Python needs no new declaration mechanism: `_PY_TO_CLR` already maps the `uuid.UUID` annotation to `CLR_GUID` (B3), and `core.py:90` resolves the annotation through `__name__`, which for `uuid.UUID` is `"UUID"`.

- [ ] **Step 1: Write the failing test changes**

  In `test_schema_registrar.py`, flip the synthesized foreign-key assertion at `:315` from `CLR_STRING` to `CLR_GUID`, and add the same assertion for the many-to-many foreign key in that test (`RegTagIds`, whose `is_array` is already asserted `True`):
  ```python
  assert fk_prop.clr_type == mapping_pb.CLR_GUID
  ```

  Sweep the file for any other assertion on a synthesized foreign key's `clr_type` and flip those too; leave assertions on ordinary declared properties alone.

- [ ] **Step 2: Run and confirm failure**
  ```bash
  cd Iverson.Clients/Python && pytest
  ```

- [ ] **Step 3: Retype the synthesized foreign key**

  In `core.py`, at the `PropertyDescriptor` built for each non-`one_to_many` relation (`:252-259`), change `clr_type=mapping_pb.CLR_STRING` to `clr_type=mapping_pb.CLR_GUID`. Leave `is_array=(rel["kind"] == "many_to_many")` unchanged.

- [ ] **Step 4: Declare the sample keys as UUIDs**

  In `sample/models.py`, add `import uuid` beside the existing `from datetime import datetime`, and change `id: str = iverson_key()` to `id: uuid.UUID = iverson_key()` on `Tag` (`:19`), `Author` (`:26`) and `Article` (`:35`).

  Leave `author_id: str = many_to_one("Author")` (`:41`) as-is — a relation member's annotation is not read for its column type; the registrar synthesizes that column separately (`:252-259`), which Step 3 already retyped.

- [ ] **Step 5: Run the suite**
  ```bash
  cd Iverson.Clients/Python && pytest
  ```

- [ ] **Step 6: Commit**
  ```bash
  git add Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/sample/models.py \
          Iverson.Clients/Python/tests/test_schema_registrar.py
  git commit -m "declare uuid foreign keys and sample keys from the python client"
  ```

## Known issues inherited from spec

**No migration path for existing text-keyed tables.** A deployment carrying one must alter the column by hand before the type will re-register. The schema-drift detector will report the mismatch, but this spec adds no automated migration.

**`Iverson.LoadTest`'s hardcoded `@id::uuid`** (`WritePathRunner.cs:203`) is left alone. It queries `BenchmarkArticle`, whose key is a Guid, so it is correct today — but it repeats the assumption this spec makes explicit elsewhere.

**The `::uuid` casts in `EntityRepository` remain hardcoded.** They become correct by construction rather than by defensive coding. If the UUID-key invariant is ever relaxed, they are the code to revisit.
