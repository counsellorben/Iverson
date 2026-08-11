# Server-generated IDs and mapped-CRUD parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md` (commit SHA: `0bfdda7bc0f8d6d7f11381b88871d2b4480def52`)

**Goal:** Make every entity key server-generated and rejected-if-client-supplied on both write paths, and give the four non-.NET clients the mapped CRUD surface that makes the returned key usable.

**Architecture:** A single `AssignNewKey` method on the shared `IEntityKeyAccessor` becomes the only place either `Post` method assigns a key, so the two services cannot drift again. Each of Python, TypeScript, Go and Java gains `getMapped`/`postMapped`/`updateMapped` on its existing `EntityCoordinator`, built from that language's own `persist`/`get` body against the already-generated `ObjectMappingService` stub. The .NET sample, the Java and TypeScript samples, and both load-test write sites are corrected to stop assigning keys and to thread each returned key forward.

**Tech stack:** .NET 10 / xUnit / FluentAssertions / NSubstitute (server + one client); Go 1.x stdlib `testing`; TypeScript / vitest; Python / pytest; Java 21 / Maven / JUnit 5 / Mockito.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **Clients never assign keys.** A create carrying a non-empty, non-all-zeroes key is rejected with `InvalidArgument`, never silently overwritten.
- **"Not supplied" is the predicate both services' `Update` methods already use**: `string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString()`. Absent field, `""`, JSON `null` and the all-zeroes `Guid`/`UUID` all read as absent.
- **Gate ordering does not change.** Key assignment stays after `EnforceWriteAuthorization` and after `ValidateAndNormalizeRelations`. An unauthorized caller must receive `PermissionDenied`, never a key rejection that confirms the type exists.
- **`Update` and `Delete` are untouched** on both services.
- **No client-side pre-validation of the key field.** The server rejects authoritatively; a second copy of the rule in five languages is the duplication this change exists to remove.
- **No mutation of the caller's entity in place.** Go's generic `T` cannot be mutated uniformly, and a rule holding in three languages but not the other two is how this divergence started. Callers read the key off the return value.
- **Error convention stays per-library.** Python/TypeScript/Go/Java raise/throw/return-error on `success == false`; .NET logs and returns `null`. New methods match whatever their own library already does.
- **Mutation testing is required** for every task that adds tests. Each new test must be shown to kill a named mutation; the specific mutations are listed per task.

## File Structure

**Modify — server**
- `Iverson.Server/Iverson.Api/Grpc/EntityKeyAccessor.cs` — add `AssignNewKey` to the interface and the implementation; the one place key assignment lives.
- `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — collapse `:47-49` onto `AssignNewKey`; correct the stale doc comment at `:11`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — collapse `:300-305` onto `AssignNewKey`.

**Modify — clients**
- `Iverson.Clients/Go/iverson/coordinator.go` — widen and rename the mapping dependency interface and its adapter; add three methods.
- `Iverson.Clients/Python/iverson_client/core.py` — add three methods.
- `Iverson.Clients/TypeScript/src/core.ts` — add three methods.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java` — add three methods plus a mapping-stub `stubFor` equivalent.
- `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs` — add `Metadata? headers = null` to the three existing mapped methods.

**Modify — samples and load test (callers that break)**
- `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs` — remove all ten client-minted keys; thread returned keys forward; supply an identity.
- `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java` — remove two `UUID.randomUUID()` keys; thread the returned author key into the article's FK.
- `Iverson.Clients/TypeScript/sample/main.ts` — remove the client-minted `article.id`.
- `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs` — remove three `Id = Guid.NewGuid(),` initializers; correct the stale comment.
- `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs` — remove three `Id = Guid.NewGuid(),` initializers.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Grpc/EntityKeyAccessorTests.cs` — modify: `AssignNewKey` boundary and round-trip tests.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — modify: invert one old-contract test; add rejection, assignment and gate-ordering tests.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — modify: invert one old-contract test; repair eleven pre-set-key `Post` tests; add rejection, assignment and gate-ordering tests.
- `Iverson.Clients/Go/iverson/coordinator_test.go` — modify: mock `MappingClient` plus three tests.
- `Iverson.Clients/Python/tests/test_entity_coordinator.py` — modify: three tests.
- `Iverson.Clients/TypeScript/tests/core.test.ts` — modify: three tests.
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java` — modify: three tests.
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMappedWriteTests.cs` — create: three header-threading tests.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here. Trusted as ground truth: A1–A10, A12–A15, A17, A19, A20 of the spec's "Verified assumptions" table, plus A11 (recorded there as FAILED — Go's mapping dependency is Delete-only, which is why Task 2 widens it).

Two inherited assumptions this plan's own verification found to be **incomplete**, corrected below in "Verified plan-level assumptions" rather than trusted:

- **A17** states one existing test asserts the old behaviour. Twelve `Post` tests carry a pre-set key; two of them assert the old contract. See PA1.
- **A18** states the two load-test sites are the only non-test client-write callers. The Java and TypeScript samples also write with client-minted keys. See PA2.

## Verified plan-level assumptions

Newly introduced by this plan and verified against the codebase on 2026-08-11 at `0bfdda7`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| PA1 | Consumer impact | **Corrects spec A17.** Twelve `Post` tests supply a key, not one. Two assert the old contract and need inversion | Enumerated every `_sut.Post(` test in both service test files: `ObjectPersistenceGrpcServiceTests.cs:147`; `ObjectMappingGrpcServiceTests.cs:593,616,634,669,707,725,781,805,830,849,871,900`. Old-contract asserters are `Post_IgnoresClientProvidedKey_AndAssignsServerKey:147` (`response.Key.Should().NotBe(clientGuid)`) and `Post_WithExistingKey_PreservesClientKey:593` (`evt!.Key.Should().Be(AuthorId)`) |
| PA2 | Consumer impact | **Corrects spec A18.** Two more non-test client-write callers break | `Java/sample/src/main/java/io/iverson/sample/Main.java:43-46,54,60` mints `UUID.randomUUID()` for Author and Article and reuses `authorId` as the article's FK — the same dangling-FK bug §3 documents for the .NET sample. `TypeScript/sample/main.ts:32` sets `article.id = crypto.randomUUID()` before `persist` |
| PA3 | Consumer impact | Go and Python samples are unaffected — they never write | `Go/sample/main.go` is schema-inspection and QueryBuilder only (its own header says it "does NOT connect to a live server"); `Python/sample/main.py` calls only `demo_query_builder`/`demo_in_operator` |
| PA4 | Consumer impact | Widening `IEntityKeyAccessor` breaks no implementor or test double | The only implementor is `EntityKeyAccessor` (`Program.cs:193` registers exactly that pair). The one substitute, `RegisterSchemaAuthorizationIntegrationTests.cs:228`, is passed to `ObjectMappingGrpcService` in a test that exercises only `RegisterSchema` (`:245`) and never `Post`, so its auto-implemented `AssignNewKey` is never invoked |
| PA5 | Code validity | `AssignNewKey`'s body needs `using Grpc.Core;` added to `EntityKeyAccessor.cs` | That file's only using is `Google.Protobuf.WellKnownTypes` (`:1`); `RpcException`/`Status`/`StatusCode` come from `Grpc.Core`, referenced via `Grpc.AspNetCore` 2.80.0 (`Iverson.Api.csproj:19`) |
| PA6 | Code validity | `Guid.CreateVersion7()` is available | `Iverson.Api.csproj:4` targets `net10.0`; already called at `ObjectPersistenceGrpcService.cs:48` |
| PA7 | File path / test seam | **Java needs no new `EntityCoordinator` test seam** — one already exists on `IversonClient` | `IversonClient.java:85-91` is a package-private `IversonClient(ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStub)` whose other three stubs are null; already used at `SchemaRegistrarTest.java:746,798,815,827` |
| PA8 | Signature | Java's `mappingStub` carries `CallCredentials`, so a `withOption(ACTING_USER_TOKEN, …)` equivalent of `stubFor` actually emits the header | `IversonClient.java:75` applies `.withCallCredentials(credentials)` to `mappingStub`; `OAuth2ClientCredentials.java:35-36` declares `ACTING_USER_TOKEN` and `:56` reads it off the call options to emit `x-acting-user-authorization` (`:37`) |
| PA9 | Signature | .NET's new tests must use the existing `TestCoordinatorFactory`, which already accepts a mapping client | `TestCoordinatorFactory.Create<T>(search, mapping, persistence, retrieval)` — all optional, substitutes filled in for the rest |
| PA10 | Consumer impact | Adding `Metadata? headers = null` to the three .NET mapped methods breaks no caller | The only call sites outside `EntityCoordinator.cs` are in `Iverson.Client.Sample/Program.cs:74,85,117,125,140,144,148,152,159,164`; none passes an argument positionally past `depth`, so inserting `headers` before `ct` is source-compatible |
| PA11 | Consumer impact | Renaming Go's `MappingDeleteClient` → `MappingClient` and its adapter touches nothing outside one file | All six references are in `coordinator.go` (`:29,30,119,150,694,698`); no sample or test names either symbol |
| PA12 | File path | All four generated mapping stubs already expose `Get`/`Post`/`Update` | Python `object_mapping_pb2_grpc.py:39,44,49`; TypeScript `generated/object_mapping.ts:2872,2881,2890`; Go `generated/object_mapping_grpc.pb.go:37-39`; Java `ObjectMappingServiceGrpc.java:424,431,438` (blocking stub) |
| PA13 | Signature | `MappingGetRequest` carries `depth`, so `getMapped` has something to pass through | `Common/Proto/object_mapping.proto:165-170` — `type_name`, `key`, `depth` (int32), `trace_id` |
| PA14 | Code validity | An unset key serializes as absent in every language whose sample or tests exercise it | Python `core.py:389` skips `value is None`; TypeScript `core.ts:454` skips `value === undefined`, and `sample/models/Article.ts:18` declares `id: string = ''` so a `new Article()` sends `""`; Java `StructConverter.java:103` maps a null `UUID` to `NullValue`, which `ExtractKey` reads as `""`; .NET/Go send the all-zeroes `Guid` / empty `string` |
| PA15 | Signature | The .NET sample needs `Guid.Parse` at each hand-off, and its FK types are as the spec quotes | `Models/Article.cs:9,22,23` — `Guid Id`, `Guid AuthorId`, `Guid[] TagIds`; `Models/UserArticle.cs:9,11,12` — `Guid Id/UserId/ArticleId`; `PersistAsync` returns `string?` (`EntityCoordinator.cs:101`) |
| PA16 | Signature | `Query.Where`'s value parameter is bound to the property type, so the sample's `userId` filter also needs `Guid.Parse` | `QueryBuilder.cs:26-29` — `Where<TValue>(Expression<Func<T,TValue>>, SearchOperator, TValue)`; `Program.cs:204` filters on `ua.UserId`, a `Guid` |
| PA17 | Signature | The .NET sample can supply an identity with no new dependency | `AddIversonClient` takes `credentials` and `actingUserTokenProvider` (`ServiceCollectionExtensions.cs:31,33`); `IversonClientCredentials` and the `WithActingUser` extension (`ActingUserMetadata.cs:9`) both live in `Iverson.Client.Core`, which the sample already references (`Iverson.Client.Sample.csproj:4`) |
| PA18 | Signature | Go's test injection point exists and reaches the mapping dependency | `newEntityCoordinatorWithDeps(coordinatorDeps, T)` (`coordinator.go:159`) with `coordinatorDeps.mapping` (`:119`); already used from the white-box `iverson/coordinator_test.go:449` |
| PA19 | Command | Server tests: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` | `.csproj` exists at that path and is listed in `Iverson.slnx` |
| PA20 | Command | .NET client tests: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj` | Same; listed in `Iverson.slnx` |
| PA21 | Command | Go: `go test ./... && go vet ./...` from `Iverson.Clients/Go` | `go.mod` is at `Iverson.Clients/Go/go.mod` |
| PA22 | Command | Python: `pytest` from `Iverson.Clients/Python` | `pyproject.toml:25-26` — `[tool.pytest.ini_options] testpaths = ["tests"]` |
| PA23 | Command | TypeScript: `npm test` from `Iverson.Clients/TypeScript`, which also type-checks the sample | `package.json` `scripts.test` = `"npm run typecheck && vitest run"`, and `typecheck` = `tsc -p tsconfig.test.json` |
| PA24 | Command | Java: `mvn -f Iverson.Clients/Java/pom.xml test` | `pom.xml` exists at that path with the `client` and `sample` modules; JUnit 5.11.0 and Mockito 5.14.2 (`:29-30`) |
| PA25 | Command | The load test needs its own build command — it is **not** in the solution | `Iverson.slnx` lists no `Iverson.LoadTest` project; the csproj is at `Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj` |
| PA26 | Task ordering | Tasks 2–7 touch pairwise-disjoint file sets and consume no symbol any other introduces; only Task 1 is a prerequisite (it defines the contract the samples and load test are corrected for) | File sets per task listed under "File Structure" share no path. Task 1 changes server-internal code only; no client or sample references `AssignNewKey` |
| PA27 | Test convention | Each language's coordinator-test mocking convention, which the new tests must follow rather than invent | Go: white-box `package iverson` with a hand-written mock struct and a `newTestCoordinator` helper (`coordinator_test.go:447-454`). Python: `MagicMock()` swapped onto the private stub attribute (`test_entity_coordinator.py:39-46`). TypeScript: vitest with `makeClientLike({ _mappingClient: … })` (`core.test.ts:105-118`). Java: `@Mock` + `MockitoExtension` (`EntityCoordinatorTest.java:17-19`). .NET: `Substitute.For<…Client>()` returning a hand-constructed `AsyncUnaryCall<T>` (`EntityCoordinatorPersistAsyncTests.cs:23-36`) |

---

## Tasks

### Task 1: Server key-assignment contract

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/EntityKeyAccessor.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs:11,47-49`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:300-305`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/EntityKeyAccessorTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Produces: the wire contract every later task's sample and load-test correction is written against. Nothing later imports a symbol from this task.

The production change and the test-census repair are one task on purpose: the production change is what breaks the twelve existing tests, so splitting them would hand the next task a red suite.

- [ ] **Step 1: Write the `AssignNewKey` unit tests first (they fail — the method does not exist yet)**

Add to `EntityKeyAccessorTests.cs`, following its existing `_sut` / `Struct` fixture style. The boundary of "not supplied" is the load-bearing part:

```csharp
[Theory]
[InlineData(null)]                                      // field absent entirely
[InlineData("")]                                        // empty string
[InlineData("00000000-0000-0000-0000-000000000000")]    // .NET/Java unset Guid/UUID
public void AssignNewKey_StampsFreshKey_WhenKeyNotSupplied(string? supplied)
{
    var payload = new Struct();
    if (supplied is not null)
        payload.Fields["Id"] = Value.ForString(supplied);

    var assigned = _sut.AssignNewKey(payload, "Id");

    Guid.TryParse(assigned, out _).Should().BeTrue();
    _sut.ExtractKey(payload, "Id").Should().Be(assigned);
}

[Fact]
public void AssignNewKey_StampsFreshKey_WhenKeyIsJsonNull()
{
    var payload = new Struct();
    payload.Fields["Id"] = Value.ForNull();

    var assigned = _sut.AssignNewKey(payload, "Id");

    Guid.TryParse(assigned, out _).Should().BeTrue();
    _sut.ExtractKey(payload, "Id").Should().Be(assigned);
}

[Fact]
public void AssignNewKey_Throws_WhenClientSuppliedKey()
{
    var payload = new Struct();
    payload.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());

    var act = () => _sut.AssignNewKey(payload, "Id");

    var ex = act.Should().Throw<RpcException>();
    ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    ex.Which.Status.Detail.Should().Contain("server-generated");
}

[Fact]
public void AssignNewKey_Throws_WhenSuppliedKeyIsNotAGuid()
{
    var payload = new Struct();
    payload.Fields["Id"] = Value.ForString("client-chosen");

    var act = () => _sut.AssignNewKey(payload, "Id");

    act.Should().Throw<RpcException>()
       .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
}
```

The last test pins that the guard is "any non-empty value", not "any parseable Guid" — a client cannot smuggle a key in by making it unparseable. `EntityKeyAccessorTests.cs` needs `using Grpc.Core;` added for `RpcException`/`StatusCode`.

- [ ] **Step 2: Add `AssignNewKey` to the interface and the implementation**

In `EntityKeyAccessor.cs`, add `using Grpc.Core;` alongside the existing `Google.Protobuf.WellKnownTypes` using (PA5), then:

```csharp
public interface IEntityKeyAccessor
{
    string ExtractKey(Struct payload, string keyColumn);
    void SetKey(Struct payload, string keyColumn, string key);

    /// <summary>
    /// Assigns a fresh server-generated UUID v7 key. Clients never assign keys:
    /// a payload that already carries one is rejected, not silently overwritten.
    /// </summary>
    string AssignNewKey(Struct payload, string keyColumn);
}
```

```csharp
public string AssignNewKey(Struct payload, string keyColumn)
{
    var supplied = ExtractKey(payload, keyColumn);
    if (!string.IsNullOrWhiteSpace(supplied) && supplied != Guid.Empty.ToString())
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"'{keyColumn}' is server-generated and cannot be set by the client. " +
            $"Omit it on create; the assigned key is returned in the response."));

    var key = Guid.CreateVersion7().ToString();
    SetKey(payload, keyColumn, key);
    return key;
}
```

- [ ] **Step 3: Run the unit tests — they now pass**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~EntityKeyAccessorTests"
```

- [ ] **Step 4: Collapse both `Post` call sites onto it**

In `ObjectPersistenceGrpcService.cs`, replace `:47-49` (the comment and the two assignment lines) with:

```csharp
var key = keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);
```

In `ObjectMappingGrpcService.cs`, replace `:300-305` (the `ExtractKey` call and the `if` block) with:

```csharp
var key = _keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);
```

Both replacements go exactly where the current assignment sits — after `EnforceWriteAuthorization`, after `ValidateAndNormalizeRelations`, and before `SerializePayload`. Do not move either.

- [ ] **Step 5: Correct the stale doc comment**

`ObjectPersistenceGrpcService.cs:11` currently claims the service stamps a key "when the client sends an empty key", which the code has never done. Make it state the rule:

```csharp
/// <summary>
/// Lightweight write path. Assigns the server-generated UUID v7 key on create — a client
/// never assigns an ID, and a payload that already carries one is rejected — writes directly
/// to Postgres, then publishes an EntityEvent for StarRocks and Qdrant to consume via their
/// consumer groups.
/// </summary>
```

- [ ] **Step 6: Invert the two tests that assert the old contract**

`ObjectPersistenceGrpcServiceTests.cs:147` — rename `Post_IgnoresClientProvidedKey_AndAssignsServerKey` to `Post_WithClientProvidedKey_ThrowsInvalidArgument` and replace its assertion:

```csharp
[Fact]
public async Task Post_WithClientProvidedKey_ThrowsInvalidArgument()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    var payload = MakePayload(new()
    {
        ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
        ["Name"] = Value.ForString("Bob")
    });
    var request = new PersistRequest { TypeName = "Author", Payload = payload };

    var act = () => _sut.Post(request, TestServerCallContext.Create());

    var ex = await act.Should().ThrowAsync<RpcException>();
    ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    ex.Which.Status.Detail.Should().Contain("server-generated");
}
```

`ObjectMappingGrpcServiceTests.cs:593` — rename `Post_WithExistingKey_PreservesClientKey` to `Post_WithClientProvidedKey_ThrowsInvalidArgument` and give it the same shape against `MappingWriteRequest`. Its now-unused `_sql`/`_events` arrangement lines can go with it.

Neither test is deleted. A test asserting the old contract is asserting the bug; the fix is inversion.

- [ ] **Step 7: Repair the remaining ten pre-set-key `Post` tests**

In `ObjectMappingGrpcServiceTests.cs`, delete the payload's key entry from each of these; their assertions are about SQL, owner-stamping, tenant-stamping or the response object and are unaffected by the key no longer being caller-chosen:

| Line | Test | Change beyond dropping the key |
|---|---|---|
| `:616` | `Post_ExecutesUpsertSql_DirectlyToPostgres` | none |
| `:634` | `Post_InsertsReconciliationQueueRowInSameTransactionAsUpsert` | none |
| `:669` | `Post_EmitsCreatedEvent_WithCorrectTypeName` | replace `evt.Key.Should().Be(AuthorId)` with `Guid.TryParse(evt.Key, out _).Should().BeTrue()` |
| `:707` | `Post_ReturnsPayloadAsData_NotDbRefetch` | sets its key through the other `MakePayload(schema.KeyColumn.Name, sentKey)` overload — drop `sentKey` and call the no-key form; keep the `BeSameAs(request.Payload)` assertion, which is the point of the test |
| `:725` | `Post_WithInvalidFkGuid_ThrowsInvalidArgument` | none — see note below |
| `:781` | `Post_ForOrdinaryCaller_ForceSetsOwnerFieldToActingUserSub` | none |
| `:805` | `Post_WithBypassRole_LeavesOwnerFieldUntouched` | none |
| `:830` | `Post_ForOrdinaryCaller_StampsTenantOntoPayload` | none |
| `:849` | `Post_WithBypassRole_StillStampsTenantOntoPayload` | none |
| `:871` | `Post_WithRestrictedFieldInWritePayload_ThrowsInvalidArgument` | none — see note below |
| `:900` | `Post_ForOrdinaryCaller_WithFieldPermissionRestrictingOwnerColumn_StillForceSetsOwnerField` | none |

`:725` and `:871` both assert only `StatusCode.InvalidArgument` and both throw at a gate that fires *before* key assignment, so they would keep passing whether or not their key is dropped. Drop it anyway: with the key present, a gate-ordering regression would make them pass for the wrong reason instead of failing.

- [ ] **Step 8: Add the parallel rejection and assignment tests to both services**

Each service gets one rejection test and one assignment test. Step 6 supplies the rejection half for both. The assignment half already exists on each and already asserts a parsed, non-empty `Guid`, so **add nothing** here — `Post_ReturnsSuccess_WithGeneratedKey_WhenKeyAbsent` (`ObjectPersistenceGrpcServiceTests.cs:134`, asserting `Guid.TryParse(response.Key, out _)`) and `Post_WithMissingKey_GeneratesValidGuid` (`ObjectMappingGrpcServiceTests.cs:572`, asserting `Guid.TryParse(evt!.Key, out var g)` and `g.Should().NotBe(Guid.Empty)`).

This step exists to record that the pair is complete, not to add code. The pair across the two services *is* the regression test for the divergence: either passing alone is the state the codebase was already in.

- [ ] **Step 9: Add the gate-ordering test to both services**

Both orderings "work" under an authorized caller, so without this a wrong implementation passes. Add to each test file, following that file's existing `PermissionDenied` test arrangement (`ObjectPersistenceGrpcServiceTests.cs:311`, `ObjectMappingGrpcServiceTests.cs:749`):

```csharp
[Fact]
public async Task Post_WithSuppliedKey_AndUnauthorizedCaller_ThrowsPermissionDenied_NotInvalidArgument()
{
    var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
    await _registry.RegisterAsync(schema);
    var payload = MakePayload(new()
    {
        ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
        ["Name"] = Value.ForString("Alice")
    });

    var act = () => _sut.Post(
        new PersistRequest { TypeName = "Author", Payload = payload },
        TestServerCallContext.Create());

    var ex = await act.Should().ThrowAsync<RpcException>();
    ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
}
```

Use `MappingWriteRequest` and that file's context helper for the mapping version.

- [ ] **Step 10: Run the full server suite green**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 11: Mutation-test the new server tests**

Apply each mutation, confirm a named test fails, then revert it. Record which test caught which.

| Mutation | Must be killed by |
|---|---|
| Invert the `AssignNewKey` guard so supplied keys are accepted | `AssignNewKey_Throws_WhenClientSuppliedKey`, plus `Post_WithClientProvidedKey_ThrowsInvalidArgument` on **both** services |
| Drop the `supplied != Guid.Empty.ToString()` clause | `AssignNewKey_StampsFreshKey_WhenKeyNotSupplied` (all-zeroes case) |
| Move the `AssignNewKey` call above `EnforceWriteAuthorization` | `Post_WithSuppliedKey_AndUnauthorizedCaller_ThrowsPermissionDenied_NotInvalidArgument` on both services |
| Have `AssignNewKey` return a fresh key without calling `SetKey` | `AssignNewKey_StampsFreshKey_WhenKeyNotSupplied` (the `ExtractKey` round-trip) and `Post_ReturnsPayloadAsData_NotDbRefetch` |

- [ ] **Step 12: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/EntityKeyAccessor.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/EntityKeyAccessorTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "assign every entity key on the server and reject client-supplied keys"
```

---

### Task 2: Go mapped CRUD parity

**Files:**
- Modify: `Iverson.Clients/Go/iverson/coordinator.go:29-32,119,150,694-700`
- Test: `Iverson.Clients/Go/iverson/coordinator_test.go`

Go needs one step the other three clients do not: its mapping dependency is Delete-only (spec A11), so the interface and adapter are widened first.

- [ ] **Step 1: Widen and rename the mapping dependency interface**

Replace `:29-32`:

```go
// MappingClient is the interface for the ObjectMappingService operations the coordinator
// uses: full CRUD with server-side relation resolution, plus Delete.
type MappingClient interface {
	Get(ctx context.Context, req *pb.MappingGetRequest) (*pb.MappingResponse, error)
	Post(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error)
	Update(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error)
	Delete(ctx context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error)
}
```

Update `coordinatorDeps.mapping` at `:119` to `mapping MappingClient`. The rename is breaking only for code constructing a coordinator with custom `deps`, which is test-only in this repo (PA11).

- [ ] **Step 2: Extend the adapter**

Rename `mappingDeleteAdapter` to `mappingAdapter` at `:694-700` and add the three forwarding methods beside the existing `Delete`, each matching the shape `Delete` already uses:

```go
type mappingAdapter struct {
	stub pb.ObjectMappingServiceClient
}

func (a *mappingAdapter) Get(ctx context.Context, req *pb.MappingGetRequest) (*pb.MappingResponse, error) {
	return a.stub.Get(ctx, req)
}

func (a *mappingAdapter) Post(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	return a.stub.Post(ctx, req)
}

func (a *mappingAdapter) Update(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	return a.stub.Update(ctx, req)
}

func (a *mappingAdapter) Delete(ctx context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error) {
	return a.stub.Delete(ctx, req)
}
```

Update the construction site at `:150` to `mapping: &mappingAdapter{client.MappingStub},`.

- [ ] **Step 2b: Write the three tests (they fail — the methods do not exist yet)**

In `coordinator_test.go`, add a `mockMappingClient` struct following the file's existing hand-written mock convention (PA27), capturing the last request of each kind and returning a canned `*pb.MappingResponse`. Add a `newTestMappedCoordinator(t, mapping)` helper mirroring `newTestCoordinator` at `:447`, passing `coordinatorDeps{mapping: mapping}`.

The three tests assert against the captured request, not merely a non-nil return:
- `TestCoordinatorGetMapped_PassesDepthThrough` — calls `GetMapped(ctx, "k", 2)` and asserts the captured `MappingGetRequest.Depth == 2` and `Key == "k"`.
- `TestCoordinatorPostMapped_ReturnsEntityHydratedFromData` — canned `Data` carries a server-assigned `Id` differing from the entity's; asserts the returned `T` has the response's `Id`.
- `TestCoordinatorUpdateMapped_SendsKeyItWasGiven` — asserts the captured `MappingWriteRequest.Payload` carries the entity's key field.

- [ ] **Step 3: Add the three methods**

Each body is `Persist`/`Get`'s existing body with the stub and request type swapped. Place them beside `Delete`, before the Object Persistence section:

```go
// GetMapped retrieves an entity by key with server-side relation resolution to the given depth.
func (c *EntityCoordinator[T]) GetMapped(ctx context.Context, id string, depth int32) (T, error) {
	var zero T
	resp, err := c.deps.mapping.Get(ctx, &pb.MappingGetRequest{
		TypeName: c.typeName,
		Key:      id,
		Depth:    depth,
	})
	if err != nil {
		return zero, fmt.Errorf("GetMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("GetMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
}

// PostMapped creates an entity through the mapping path, which resolves its relations
// server-side. Returns the entity hydrated from the response, carrying the
// server-assigned key — the caller never assigns one.
func (c *EntityCoordinator[T]) PostMapped(ctx context.Context, entity T) (T, error) {
	var zero T
	payload, err := entityToStruct(entity)
	if err != nil {
		return zero, err
	}
	resp, err := c.deps.mapping.Post(ctx, &pb.MappingWriteRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return zero, fmt.Errorf("PostMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("PostMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
}

// UpdateMapped updates an existing entity through the mapping path.
func (c *EntityCoordinator[T]) UpdateMapped(ctx context.Context, entity T) (T, error) {
	var zero T
	payload, err := entityToStruct(entity)
	if err != nil {
		return zero, err
	}
	resp, err := c.deps.mapping.Update(ctx, &pb.MappingWriteRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return zero, fmt.Errorf("UpdateMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("UpdateMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
}
```

`structToEntity[T]` already returns `(T, error)` (`coordinator.go:244`), so returning it directly is correct. The acting-user header is attached by `auth.go:52-53` at the channel level, so these methods inherit it (spec A20).

- [ ] **Step 4: Run tests and vet**

```bash
cd Iverson.Clients/Go && go test ./... && go vet ./...
```

- [ ] **Step 5: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Hard-code `Depth: 1` in `GetMapped` | `TestCoordinatorGetMapped_PassesDepthThrough` |
| Return a fresh `var zero T` instead of `structToEntity[T](resp.Data)` in `PostMapped` | `TestCoordinatorPostMapped_ReturnsEntityHydratedFromData` |
| Send an empty `Payload` in `UpdateMapped` | `TestCoordinatorUpdateMapped_SendsKeyItWasGiven` |

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Go/iverson/coordinator.go Iverson.Clients/Go/iverson/coordinator_test.go
git commit -m "add mapped CRUD to the Go client coordinator"
```

---

### Task 3: Python mapped CRUD parity

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/test_entity_coordinator.py`

- [ ] **Step 1: Write the three tests (they fail — the methods do not exist yet)**

Add a `make_mapped_coordinator()` helper beside `make_coordinator` (`:39`) that swaps `coordinator._mapping` for a `MagicMock()`, and a `_mapped_response(**fields)` helper returning a `mapping_pb.MappingResponse(success=True, data=…)`. The file needs `object_mapping_pb2 as mapping_pb` imported.

Three tests in a new `TestEntityCoordinatorMappedCrud` class, each asserting against the captured request via `coordinator._mapping.Get.call_args`:
- `test_get_mapped_passes_depth_through` — `get_mapped("k", depth=2)`; assert the sent request's `depth == 2` and `key == "k"`.
- `test_post_mapped_returns_entity_hydrated_from_data` — canned `data` carries an `Id` the input entity does not; assert the returned entity's `id` is the response's.
- `test_update_mapped_sends_the_key_it_was_given` — assert the sent `payload` carries the entity's key field.

- [ ] **Step 2: Add the three methods**

Each body is `persist`/`get`'s existing body with the stub and request type swapped. Place them after `delete` (`:512`), following the file's `raise RuntimeError(...)` error convention:

```python
    def get_mapped(self, id: str, depth: int = 1, trace_id: str = "") -> Optional[T]:
        """Retrieve an entity by key with server-side relation resolution to ``depth``.
        Returns None if not found."""
        response = self._mapping.Get(
            mapping_pb.MappingGetRequest(
                type_name=self._type_name,
                key=id,
                depth=depth,
                trace_id=trace_id,
            )
        )
        if not response.success:
            return None
        return self._from_struct(response.data)

    def post_mapped(self, entity: T, trace_id: str = "") -> Optional[T]:
        """Create an entity through the mapping path, which resolves its relations
        server-side. Returns the entity hydrated from the response, carrying the
        server-assigned key — the caller never assigns one."""
        response = self._mapping.Post(
            mapping_pb.MappingWriteRequest(
                type_name=self._type_name,
                payload=_entity_to_struct(entity),
                trace_id=trace_id,
            )
        )
        if not response.success:
            raise RuntimeError(f"post_mapped failed: {response.error}")
        return self._from_struct(response.data)

    def update_mapped(self, entity: T, trace_id: str = "") -> Optional[T]:
        """Update an existing entity through the mapping path."""
        response = self._mapping.Update(
            mapping_pb.MappingWriteRequest(
                type_name=self._type_name,
                payload=_entity_to_struct(entity),
                trace_id=trace_id,
            )
        )
        if not response.success:
            raise RuntimeError(f"update_mapped failed: {response.error}")
        return self._from_struct(response.data)
```

`get_mapped` returns `None` rather than raising, matching `get`'s own not-found convention (`:533-534`); the two write methods raise, matching `persist`/`update` (`:495`, `:509`). The acting-user header comes from the channel interceptor (`core.py:662-664`), so these inherit it.

- [ ] **Step 3: Run tests**

```bash
cd Iverson.Clients/Python && pytest
```

- [ ] **Step 4: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Hard-code `depth=1` in `get_mapped` | `test_get_mapped_passes_depth_through` |
| Return `self._cls()` instead of `self._from_struct(response.data)` in `post_mapped` | `test_post_mapped_returns_entity_hydrated_from_data` |
| Send an empty `struct_pb2.Struct()` payload in `update_mapped` | `test_update_mapped_sends_the_key_it_was_given` |

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_entity_coordinator.py
git commit -m "add mapped CRUD to the Python client coordinator"
```

---

### Task 4: TypeScript mapped CRUD parity, and its sample's client-minted key

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Modify: `Iverson.Clients/TypeScript/sample/main.ts:32`
- Test: `Iverson.Clients/TypeScript/tests/core.test.ts`

- [ ] **Step 1: Write the three tests (they fail — the methods do not exist yet)**

In `core.test.ts`, add a `describe('EntityCoordinator — mapped CRUD')` block using the file's existing `makeClientLike({ _mappingClient: { … } })` convention (`:105-118`) and its `vi.fn()` unary-call shape (`:81`). Assert against the captured request:
- `getMapped() passes depth through` — call with `depth: 2`; assert the captured request's `depth === 2`.
- `postMapped() returns an entity hydrated from Data` — canned `data` carries an `id` the input lacks; assert the returned instance carries it.
- `updateMapped() sends the key it was given` — assert the captured `payload.Id`.

- [ ] **Step 2: Add the three methods**

Place after `delete` (`:583`). Each is `persist`/`get`'s body with the stub and request type swapped, routed through `callUnary` with the client's credentials and acting-user token exactly as `delete` does:

```typescript
    /** Retrieve an entity by key with server-side relation resolution to `depth`. */
    async getMapped(id: string, depth: number = 1, traceId: string = ''): Promise<T | null> {
        const request: MappingGetRequest = {
            typeName: this._typeName,
            key: id,
            depth,
            traceId,
        };
        const response = await callUnary<MappingGetRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.get(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._client._actingUserToken,
        );
        if (!response.success) return null;
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /**
     * Create an entity through the mapping path, which resolves its relations server-side.
     * Resolves to the entity hydrated from the response, carrying the server-assigned key —
     * the caller never assigns one.
     */
    async postMapped(entity: T, traceId: string = ''): Promise<T> {
        const request: MappingWriteRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<MappingWriteRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.post(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._client._actingUserToken,
        );
        if (!response.success) {
            throw new Error(`postMapped failed: ${response.error}`);
        }
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /** Update an existing entity through the mapping path. */
    async updateMapped(entity: T, traceId: string = ''): Promise<T> {
        const request: MappingWriteRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<MappingWriteRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.update(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._client._actingUserToken,
        );
        if (!response.success) {
            throw new Error(`updateMapped failed: ${response.error}`);
        }
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }
```

Add `MappingGetRequest`, `MappingWriteRequest` and `MappingResponse` to the existing `object_mapping.js` import block if not already imported. `getMapped` returns `null` on failure, matching `get` (`:598`); the writes throw, matching `persist`/`update`.

- [ ] **Step 3: Remove the sample's client-minted key**

In `sample/main.ts`, delete line 32 (`article.id = crypto.randomUUID();`). `Article.id` is declared `id: string = ''` (`sample/models/Article.ts:18`), so a `new Article()` now sends an empty key, which the server reads as absent (PA14). The `const key = await articles.persist(article)` on the following lines already prints the server-assigned key, so nothing downstream changes.

- [ ] **Step 4: Run tests — this also type-checks the sample**

```bash
cd Iverson.Clients/TypeScript && npm test
```

`npm test` runs `tsc -p tsconfig.test.json` before vitest, which type-checks `sample/` and `tests/`.

- [ ] **Step 5: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Hard-code `depth: 1` in `getMapped` | `getMapped() passes depth through` |
| Return `new this._cls()` instead of `payloadToEntity(…)` in `postMapped` | `postMapped() returns an entity hydrated from Data` |
| Send `payload: {}` in `updateMapped` | `updateMapped() sends the key it was given` |

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/TypeScript/src/core.ts \
        Iverson.Clients/TypeScript/tests/core.test.ts \
        Iverson.Clients/TypeScript/sample/main.ts
git commit -m "add mapped CRUD to the TypeScript client coordinator and stop the sample assigning a key"
```

---

### Task 5: Java mapped CRUD parity, and its sample's dangling foreign key

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`
- Modify: `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java:43-46,51-65`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java`

- [ ] **Step 1: Write the three tests (they fail — the methods do not exist yet)**

No new test seam is needed: `IversonClient` already has a package-private constructor taking only a mapping stub (`IversonClient.java:85-91`, used at `SchemaRegistrarTest.java:746`) (PA7). Build the coordinator as `new EntityCoordinator<>(new IversonClient(mockMappingStub), CoordinatorTestArticle.class)`.

Add a `@Mock ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mockMappingStub` beside the existing search mock, following the file's `@Mock`/`MockitoExtension` convention (`:17-19`). Stub `withOption(any(), any())` to return the mock itself so `mappingStubFor` is transparent in tests. Assert with an `ArgumentCaptor` on the sent request:
- `getMappedPassesDepthThrough`
- `postMappedReturnsEntityHydratedFromData`
- `updateMappedSendsTheKeyItWasGiven`

- [ ] **Step 2: Add the mapping-stub `stubFor` equivalent**

`delete` (`:137-144`) uses the bare `client.mappingStub`, which carries the service's own client-credentials token but no acting-user identity. The three new methods must not copy that. Add beside `stubFor` (`:269-273`):

```java
    /**
     * Returns the mapping stub to invoke, attaching the acting-user token as a call option
     * (consumed by {@link OAuth2ClientCredentials}) when one is given. The constructor's
     * {@link io.grpc.CallCredentials} are the service's own client-credentials token and do
     * <em>not</em> identify an acting user; the server denies any write without one.
     */
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStubFor(String actingUserToken) {
        return actingUserToken != null
            ? client.mappingStub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, actingUserToken)
            : client.mappingStub;
    }
```

- [ ] **Step 3: Add the three methods**

Place them in a new `// ── Object Mapping (full CRUD with relation resolution) ──` section before Object Search. Each is `persist`/`get`'s body with the stub and request type swapped, taking the trailing `actingUserToken` parameter the search family already uses (`:178`, `:198`, `:214`):

```java
    /**
     * Fetches a single entity by key with server-side relation resolution to {@code depth}.
     * Returns {@code null} if not found.
     */
    public T getMapped(String id, int depth, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingGetRequest request = ObjectMapping.MappingGetRequest.newBuilder()
            .setTypeName(typeName)
            .setKey(id)
            .setDepth(depth)
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).get(request);
        if (!response.getSuccess()) return null;
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    /**
     * Creates an entity through the mapping path, which resolves its relations server-side.
     * Returns the entity hydrated from the response, carrying the server-assigned key — the
     * caller never assigns one.
     */
    public T postMapped(T entity, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingWriteRequest request = ObjectMapping.MappingWriteRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).post(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
        return StructConverter.fromStruct(response.getData(), entityType);
    }

    /** Updates an existing entity through the mapping path. */
    public T updateMapped(T entity, String actingUserToken) throws StatusRuntimeException {
        ObjectMapping.MappingWriteRequest request = ObjectMapping.MappingWriteRequest.newBuilder()
            .setTypeName(typeName)
            .setPayload(StructConverter.toStruct(entity))
            .build();
        ObjectMapping.MappingResponse response = mappingStubFor(actingUserToken).update(request);
        if (!response.getSuccess()) {
            throw new StatusRuntimeException(
                io.grpc.Status.INTERNAL.withDescription(response.getError()));
        }
        return StructConverter.fromStruct(response.getData(), entityType);
    }
```

`getMapped` returns `null` on an unsuccessful response, matching `get`'s not-found convention (`:111`); the writes throw `StatusRuntimeException`, matching `persist`/`update` (`:79`, `:95`).

- [ ] **Step 4: Fix the sample's client-minted keys and dangling FK**

`Main.java` currently mints an author UUID, persists (the server discards it), then uses that discarded UUID as the article's `authorId` — the same bug spec §3 documents for the .NET sample. Rewrite `:43-65`:

```java
            Author author = new Author(null, "Jane Smith", "jane@example.com");
            author.setTenantId(TENANT_ID);
            // The server assigns the key; persist returns it. Write order is load-bearing:
            // the author must exist before an article can reference it.
            String persistedAuthorId = authorCoordinator.persist(author);
            System.out.println("Persisted author: " + persistedAuthorId);

            // ── Persist an article ─────────────────────────────────────────────
            EntityCoordinator<Article> articleCoordinator =
                new EntityCoordinator<>(client, Article.class);

            Article article = new Article(
                null,
                "The Rise of Functional Programming",
                "Functional programming is transforming how we write software...",
                "technology",
                850,
                OffsetDateTime.now(),
                UUID.fromString(persistedAuthorId)
            );
```

A null `UUID` serializes to `NullValue`, which the server reads as absent (PA14), so both `null` key arguments are accepted. `UUID.fromString(persistedAuthorId)` is the Java analogue of the .NET sample's `Guid.Parse` friction — the honest cost of not mutating the caller's entity. The `java.util.UUID` import (`:13`) is still needed for that call.

- [ ] **Step 5: Run tests**

```bash
mvn -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 6: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Hard-code `.setDepth(1)` in `getMapped` | `getMappedPassesDepthThrough` |
| Return `entityType.getDeclaredConstructor().newInstance()` instead of `fromStruct(response.getData(), …)` in `postMapped` | `postMappedReturnsEntityHydratedFromData` |
| Send `Struct.getDefaultInstance()` as the payload in `updateMapped` | `updateMappedSendsTheKeyItWasGiven` |
| Route through the bare `client.mappingStub` instead of `mappingStubFor(actingUserToken)` | a `verify(mockMappingStub).withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), eq("tok"))` assertion in `postMappedReturnsEntityHydratedFromData` |

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java \
        Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
git commit -m "add mapped CRUD to the Java client coordinator and fix the sample's dangling author foreign key"
```

---

### Task 6: .NET mapped-write headers, tests, and the sample correction

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs:33,50,66`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs`
- Test (create): `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMappedWriteTests.cs`

**Interfaces:**
- Consumes: Task 1's contract — the sample cannot run against a server without it.

Spec §5 scopes client testing to "three tests per language, twelve total" across the four clients gaining new methods. The three .NET tests below are a deliberate extension of that, authorized by the user at plan-write time: .NET's mapped methods gain a header parameter with no test coverage at all today, and a header-threading regression would otherwise surface only as a `PermissionDenied` at sample-run time. Fifteen client tests total.

- [ ] **Step 1: Write the three header-threading tests (they fail — the parameter does not exist yet)**

Create `EntityCoordinatorMappedWriteTests.cs`, following `EntityCoordinatorPersistAsyncTests.cs`'s conventions exactly (PA27): an internal `[IversonEntity]` fixture class, `Substitute.For<ObjectMappingService.ObjectMappingServiceClient>()`, a hand-constructed `AsyncUnaryCall<MappingResponse>`, and `TestCoordinatorFactory.Create<T>(mapping: mapping)` (PA9).

```csharp
[Fact]
public async Task PostMappedAsync_PassesSuppliedHeaders_ToPostAsync()
{
    var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
    Metadata? capturedHeaders = null;
    mapping
        .PostAsync(
            Arg.Any<MappingWriteRequest>(),
            Arg.Do<Metadata>(h => capturedHeaders = h),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>())
        .Returns(new AsyncUnaryCall<MappingResponse>(
            Task.FromResult(new MappingResponse { Success = true, Data = new Struct() }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    var sut = TestCoordinatorFactory.Create<MappedWriteTestEntity>(mapping: mapping);
    var headers = new Metadata { { "x-acting-user-authorization", "Bearer test-token" } };

    await sut.PostMappedAsync(new MappedWriteTestEntity { Name = "x" }, headers);

    capturedHeaders.Should().NotBeNull();
    capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
}
```

Add the same shape for `UpdateMappedAsync` against `UpdateAsync`, and for `GetMappedAsync` against `GetAsync` (whose request type is `MappingGetRequest`).

- [ ] **Step 2: Add `Metadata? headers = null` to the three mapped methods**

Mirror `PersistAsync:101`, placing `headers` before `ct` and passing it as the second positional argument to the stub call, exactly as `PersistAsync` does at `:111`:

```csharp
public async Task<T?> GetMappedAsync(
    string key, int depth = 1, Metadata? headers = null, CancellationToken ct = default)
```
```csharp
public async Task<T?> PostMappedAsync(
    T entity, Metadata? headers = null, CancellationToken ct = default)
```
```csharp
public async Task<T?> UpdateMappedAsync(
    T entity, Metadata? headers = null, CancellationToken ct = default)
```

In each body, change `cancellationToken: ct` to `headers, cancellationToken: ct`. This is source-compatible for every existing caller (PA10). The shared `ObjectMappingServiceClient`'s schema-registration credentials are untouched.

- [ ] **Step 3: Give the sample an identity**

Every write is denied without an acting user, and the sample currently configures none. `ActingUserTokenProvider` and the Authentik flow executor live inside `Iverson.Server/Iverson.LoadTest/Auth/`, which the sample cannot reference, so the sample reads a pre-obtained token from the environment instead. Replace `Program.cs:11-16`:

```csharp
// Every Iverson write is authorized against an acting user; there is no anonymous write.
// Obtain a user access token from your IdP and export it before running this sample.
var actingUserToken = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_TOKEN");
if (string.IsNullOrWhiteSpace(actingUserToken))
{
    Console.Error.WriteLine(
        "IVERSON_ACTING_USER_TOKEN is not set. Every Iverson write is denied without an\n" +
        "acting user, so this sample cannot seed anything. Export a user access token and re-run.");
    return 1;
}

var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddIversonClient(
        grpcEndpoint: "https://localhost:7142",
        credentials: new IversonClientCredentials(
            Environment.GetEnvironmentVariable("IVERSON_CLIENT_ID") ?? "",
            Environment.GetEnvironmentVariable("IVERSON_CLIENT_SECRET") ?? "",
            Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT") ?? "",
            Scope: "admin schema_admin"),
        actingUserTokenProvider: () => Task.FromResult(actingUserToken),
        entityAssemblies: [typeof(Article).Assembly])
    .BuildServiceProvider();

var headers = new Metadata().WithActingUser(actingUserToken);
```

`return 1` from a top-level program requires no signature change. `Metadata` comes from the file's existing `using Grpc.Core;` (`:1`), and `WithActingUser` from `Iverson.Client.Core` (`:3`).

- [ ] **Step 4: Remove all ten client-minted keys and thread the returned keys forward**

Delete the `Guid.NewGuid()` pre-allocations at `:38-39`, `:60-62`, `:71-72`, `:99`, `:114-115`, remove the `Id = …` initializer from all ten writes, and capture each write's return value. The variables become `string?` rather than `Guid`, so every downstream `.ToString()` becomes `!` and every use as a `Guid` needs `Guid.Parse`:

```csharp
// Write order is load-bearing: an entity must exist before another can reference its key.
// Authors and tags before articles; articles before user-articles.
var authorAiId   = await authors.PersistAsync(new Author
{
    TenantId = sampleTenant,
    Name  = "Allen Iverson",
    Email = "ai@iverson.dev",
    Bio   = "The original AI. Point guard. Hall of Famer."
}, headers);
var authorKobeId = await authors.PersistAsync(new Author { /* … */ }, headers);
Console.WriteLine($"Created authors: {authorAiId}, {authorKobeId}");

var tagBballId   = await tags.PersistAsync(new Tag { Label = "Basketball", Slug = "basketball", TenantId = sampleTenant }, headers);
var tagCultureId = await tags.PersistAsync(new Tag { Label = "Culture",    Slug = "culture",    TenantId = sampleTenant }, headers);
var tagLegacyId  = await tags.PersistAsync(new Tag { Label = "Legacy",     Slug = "legacy",     TenantId = sampleTenant }, headers);

var article1 = await articles.PostMappedAsync(new Article
{
    TenantId    = sampleTenant,
    AuthorId    = Guid.Parse(authorAiId!),
    TagIds      = [Guid.Parse(tagBballId!), Guid.Parse(tagCultureId!)],
    Title       = "The Original AI: Allen Iverson's Legacy",
    Body        = "Before large language models, Allen Iverson was already doing the impossible on the hardwood.",
    PublishedAt = DateTime.UtcNow.AddDays(-7),
    IsPublished = true
}, headers);
```

`Guid.Parse` at each hand-off is intended: `PersistAsync` returns `string?` and the FK fields are `Guid` (PA15). Show it plainly rather than hiding it behind a sample-only helper. This is also the fix for the sample's real dangling-FK bug — the articles have referenced three nonexistent tags since the sample was written.

- [ ] **Step 5: Update the sites that consumed the pre-allocated keys**

The user-article writes must capture their returned entities, because cleanup and the mapped reads need their keys:

```csharp
var ua1 = await userArticles.PostMappedAsync(new UserArticle
{
    TenantId  = sampleTenant,
    UserId    = Guid.Parse(userId!),
    ArticleId = article1!.Id,   // read off the entity PostMappedAsync returned
    CreatedAt = DateTime.UtcNow
}, headers);
```

`article1!.Id` is the round-trip Task 2–5 exist to enable in the other four languages. Then update each remaining consumer:

| Original | Becomes |
|---|---|
| `:140` `authorAiId.ToString()` | `authorAiId!` |
| `:144`, `:148` `article1Id.ToString()` | `article1!.Id.ToString()` |
| `:152` `ua1Id.ToString()` | `ua1!.Id.ToString()` |
| `:164` `tagBballId.ToString()` | `tagBballId!` |
| `:172` `authorKobeId.ToString()` | `authorKobeId!` |
| `:177` the three `tagXId.ToString()` | `tagBballId!`, `tagCultureId!`, `tagLegacyId!` |
| `:182` `userId.ToString()` | `userId!` |
| `:186` `ua2Id.ToString()` | `ua2!.Id.ToString()` |
| `:204` `EqualTo, userId` | `EqualTo, Guid.Parse(userId!)` — `Where`'s value is bound to the property's `Guid` type (PA16) |
| `:319-322` the four `DeleteAsync(…Id.ToString())` | `ua1!.Id.ToString()`, `ua2!.Id.ToString()`, `article1!.Id.ToString()`, `article2!.Id.ToString()` |

Add `headers` to the three mapped read/update calls at `:140`, `:144`, `:148`, `:152`, `:159`, `:164` as well — they are authorized the same way writes are.

- [ ] **Step 6: Build the sample and run the client tests**

```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Sample/Iverson.Client.Sample.csproj
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
```

- [ ] **Step 7: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Drop `headers` from the `PostAsync` call in `PostMappedAsync` (pass only `cancellationToken: ct`) | `PostMappedAsync_PassesSuppliedHeaders_ToPostAsync` |
| Same for `UpdateMappedAsync` | `UpdateMappedAsync_PassesSuppliedHeaders_ToUpdateAsync` |
| Same for `GetMappedAsync` | `GetMappedAsync_PassesSuppliedHeaders_ToGetAsync` |

- [ ] **Step 8: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorMappedWriteTests.cs \
        Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
git commit -m "thread acting-user headers through the .NET mapped writes and fix the sample's discarded keys"
```

---

### Task 7: Load-test write sites

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs:84-90,99,111,122`
- Modify: `Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs:116,178,254`

**Interfaces:**
- Consumes: Task 1's contract. This task has no test of its own — the load test is not unit-tested — so the build is the verification, and the reasoning below is why it cannot be deferred.

Both call sites already catch `InvalidArgument` and treat it as expected, because the field-permission rules deliberately reject the regular identity's writes (`WritePathRunner.cs:145-148`; `DirectSeeder.cs:104-109`). After Task 1, *every* load-test write would fail and be absorbed into the expected-rejection counter: the load test would report success while writing nothing. The breakage is silent, which is why this is part of this change and not follow-up cleanup.

- [ ] **Step 1: Remove the three `Id` initializers in `WritePathRunner`**

Delete the `Id = Guid.NewGuid(),` line from the `BenchmarkAuthor` (`:99`), `BenchmarkTag` (`:111`) and `BenchmarkArticle` (`:122`) initializers. Each model's `Id` is a `Guid` property, so omitting it leaves `Guid.Empty`, which the server reads as absent (PA14).

Leave `:127` alone: `BenchmarkAuthorId = … : Guid.NewGuid()` is a foreign key, not a key. FKs are not existence-checked, so it stays legal.

- [ ] **Step 2: Correct the stale comment**

`WritePathRunner.cs:84-90` documents the old behaviour — that `Post` "always assigns its own server-generated UUIDv7 key and ignores whatever Id the client sets locally". Replace the "ignores" clause with the rule, keeping the still-accurate part about `null` being a distinct failure mode:

```csharp
                    // ObjectPersistenceGrpcService.Post assigns the server-generated UUIDv7
                    // key and rejects any client-supplied Id with InvalidArgument, so these
                    // entities deliberately leave Id unset. The key that lands in Postgres is
                    // PersistAsync's return value (response.Key). A null return means an
                    // application-level failure (response.Success == false), a different failure
                    // mode than a thrown RpcException, so it must be recorded as an error too
                    // rather than silently treated as a success.
```

- [ ] **Step 3: Remove the three `Id` initializers in `DirectSeeder`**

Delete the `Id = Guid.NewGuid(),` line from the three entity initializers inside the `PostToStarRocksAsync` callbacks at `:116`, `:178` and `:254`.

Leave `:89`, `:161` and `:224` alone: those `ids[i] = Guid.NewGuid()` assignments feed the Postgres `COPY` half, which mints its own IDs and bypasses gRPC entirely. Leave the `ownerId` fallbacks at `:90`, `:162`, `:227` alone as well — those are owner subjects, not keys.

- [ ] **Step 4: Build**

```bash
dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj
```

`Iverson.LoadTest` is not in `Iverson.slnx` (PA25), so it needs this explicit build; a solution-wide build will not cover it.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Scenarios/WritePathRunner.cs \
        Iverson.Server/Iverson.LoadTest/Seeding/DirectSeeder.cs
git commit -m "stop the load test assigning entity keys it cannot own"
```

---

## Tasks NOT in this plan

Inherited from the spec's "Out of scope" section. A new spec → new plan cycle is required to add any of these.

- **The conformance harness rework.** Scenario S1 was built around caller-chosen keys and needs rewriting to thread server-returned keys between phases. That happens against this contract once it lands, as a separate change to the existing 11-task harness plan.
- **Foreign-key existence validation.** `RelationValidator` checks FK shape, not row existence. That gap is what let the sample's dangling FKs go unnoticed, and it is worth its own design pass — but adding it here would change the write path's failure modes well beyond the ID rule.
- **Collapsing the two write paths.** Considered and rejected for this change: both `ObjectPersistence` and `ObjectMapping` remain, with all five clients able to use either.
- **The .NET/other-clients error-convention split** (log-and-return-null vs. throw).
- **Go's `PermissionDenied` on write**, observed during the harness's first run and not yet root-caused. Unrelated to the key rule.

## Known issues inherited from spec

The spec records no separate "Known issues" section. Two items from its design body are accepted consequences rather than defects, and are carried into the implementation deliberately:

- **`Guid.Parse` at each hand-off in the .NET sample.** `PersistAsync` returns `string?` and the FK fields are `Guid`. This friction is the honest cost of not mutating the caller's entity; the sample shows it plainly rather than hiding it behind a sample-only helper. Java's `UUID.fromString` is the same cost in that language.
- **Write order is load-bearing in the samples.** Authors and tags before articles, articles before user-articles. Both samples are already in that order, so nothing is restructured — but it is now a requirement rather than an accident, and the comments say so.
