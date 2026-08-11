# Server-generated IDs and mapped-CRUD parity

**Date:** 2026-08-10
**Status:** Approved design, not yet planned

## Problem

Iverson has two entity write paths, and they disagree about who owns the key.

`ObjectPersistenceGrpcService.Post` mints a UUIDv7 unconditionally and discards whatever key the
client sent. `ObjectMappingGrpcService.Post` extracts the client's key and generates one only when
that key is blank. The same create, issued through two RPCs, lands at two different keys.

The intended contract is the persistence path's: **a client never assigns an ID. Every ID is
generated inside Iverson and returned to the client.** The mapping path violates it.

Compounding this, mapped CRUD is a .NET-only client feature. Python, TypeScript, Go and Java use
`ObjectMappingService` for schema registration and `Delete` only; all four write exclusively through
`ObjectPersistence`. So four of five clients cannot issue a relation-resolving write at all, and the
one client that can is the one that can also — today — assign its own keys.

This was found by the cross-client conformance harness on its first live run.

### It is already causing damage

The shipped .NET sample mixes both paths. `Iverson.Client.Sample/Program.cs` writes tags via
`PersistAsync` with a client-minted `Id` (discarded by the server), then writes articles via
`PostMappedAsync` carrying `TagIds` that point at those discarded IDs. The articles have referenced
three nonexistent tags since the sample was written.

Nothing caught it because `RelationValidator` validates foreign-key *shape* — that the named FK
column exists on the schema — and never checks that the referenced row exists.

## Design

### 1. Server contract

One new method on `IEntityKeyAccessor` (`Iverson.Server/Iverson.Api/Grpc/EntityKeyAccessor.cs`):

```csharp
/// <summary>
/// Assigns a fresh server-generated UUID v7 key. Clients never assign keys:
/// a payload that already carries one is rejected, not silently overwritten.
/// </summary>
string AssignNewKey(Struct payload, string keyColumn);
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

The "not supplied" predicate is the one already used by both services' `Update` methods, so .NET and
Java's all-zeroes `Guid`/`UUID` and Go's empty `string` continue to read as absent — those languages
serialize every property, including unset ones. `ExtractKey` returns `v.StringValue`, which is `""`
for a JSON `null`, so an explicit null key also reads as absent.

**Both call sites collapse onto it.** `ObjectPersistenceGrpcService.Post:47-49` and
`ObjectMappingGrpcService.Post:300-305` each become:

```csharp
var key = keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);
```

After this, neither service holds local key-assignment logic, so the two cannot drift again. That
structural property — not the line count — is why the rule lives on the shared accessor rather than
being written twice. A gRPC interceptor was rejected as the wrong seam: it would have to resolve the
schema itself to learn `KeyColumn.Name`, duplicating `RequireSchema`, and it fires for `Update` and
`Delete` too, where a supplied key is required.

**Gate ordering does not change.** `AssignNewKey` goes exactly where the current assignment sits:
after `EnforceWriteAuthorization` and after `ValidateAndNormalizeRelations`. An unauthorized caller
must receive `PermissionDenied`, not a key rejection that confirms the type exists.

**`Update` and `Delete` are untouched.** Both require a caller-supplied key and continue to. That key
is one Iverson generated and returned earlier, not one the client invented.

**Response contract is already correct.** `PersistResponse.Key` carries the assigned key;
`MappingResponse.Data` is `request.Payload`, which `SetKey` stamps in place. Both already return what
the client needs.

**Stale doc-comment corrected.** `ObjectPersistenceGrpcService.cs:11` says the service stamps a key
"when the client sends an empty key" — behaviour the code has never had. It becomes a statement of
the rule.

**This is a breaking wire-contract change.** A caller that sets a key on create receives
`InvalidArgument` where it previously received silent success. The in-repo population is enumerated
under "Callers that break" below.

### 2. Client parity surface

Each of the four non-.NET clients gains three methods on its existing `EntityCoordinator`, mirroring
.NET's. No proto change: `ObjectMappingService` already declares `Get`, `Post` and `Update`, and all
five clients already generate and use its stub.

| .NET (existing) | Python | TypeScript | Go | Java |
|---|---|---|---|---|
| `GetMappedAsync(key, depth = 1)` | `get_mapped(id, depth=1, trace_id="")` | `getMapped(id, depth=1, traceId='')` | `GetMapped(ctx, id, depth int32)` | `getMapped(String id, int depth)` |
| `PostMappedAsync(entity)` | `post_mapped(entity, trace_id="")` | `postMapped(entity, traceId='')` | `PostMapped(ctx, entity T)` | `postMapped(T entity)` |
| `UpdateMappedAsync(entity)` | `update_mapped(entity, trace_id="")` | `updateMapped(entity, traceId='')` | `UpdateMapped(ctx, entity T)` | `updateMapped(T entity)` |

Each body is the language's existing `persist`/`get` body with the stub and request type swapped for
`MappingWriteRequest` / `MappingGetRequest` against the mapping stub, so each follows its own
library's conventions rather than an imported one.

**Return shape.** All three return an entity hydrated from `MappingResponse.Data` — Go `(T, error)`,
Python `Optional[T]`, TypeScript `Promise<T>`, Java `T`. That response carries the server-assigned
key, which is what makes "IDs are generated in Iverson and returned to the client" usable from these
four languages; today they can only recover a key from `persist`'s bare string return.

**Error convention stays per-library.** Python, TypeScript, Go and Java raise/throw/return-error on
`success == false`; .NET logs and returns `null`. The new methods match whatever their own library
already does. This spec does not re-litigate that split.

**Go requires an extra step the other three do not.** `EntityCoordinator.deps.mapping` is typed
`MappingDeleteClient` (`coordinator.go:29-32`), a Delete-only interface fed by `mappingDeleteAdapter`
(`coordinator.go:694`). It must be widened to carry `Get`, `Post` and `Update`, with the adapter
extended and the interface renamed to match its new scope. This is a breaking change only for code
constructing a coordinator with custom `deps`, which is test-only in this repo.

**Neither `persist` nor the mapped writes mutate the caller's entity in place.** Go's generic `T`
cannot be mutated uniformly, and a rule that holds in three languages but not the other two is how
this divergence started. The caller reads the key off the return value.

**Not included** — no `depth` parameter on the plain `get`; no mapped batch variant; no client-side
pre-validation that the key field is unset, since the server now rejects it authoritatively and a
second copy of that rule in five languages is the duplication section 1 exists to remove.

### 3. Sample-program correction

After section 1 the sample stops running: all ten of its writes set `Id`, so the first `PersistAsync`
returns `InvalidArgument`.

The `Guid.NewGuid()` pre-allocation idiom is removed (lines 38-39, 60-62, 71-72, 99, 114-115) and each
write's returned key becomes the variable later writes reference:

```csharp
var authorAiId = await authors.PersistAsync(new Author { TenantId = sampleTenant, … });
var tagBballId = await tags.PersistAsync(new Tag { Label = "Basketball", … });

var article1 = await articles.PostMappedAsync(new Article
{
    TenantId = sampleTenant,
    AuthorId = Guid.Parse(authorAiId!),
    TagIds   = [Guid.Parse(tagBballId!), Guid.Parse(tagCultureId!)],
    …
});
```

Two consequences, both intended:

- **`Guid.Parse` at each hand-off.** `PersistAsync` returns `string?` and the FK fields are `Guid`.
  This friction is the honest cost of not mutating the caller's entity; the sample shows it plainly
  rather than hiding it behind a sample-only helper.
- **Write order becomes load-bearing.** Authors and tags before articles, articles before
  user-articles. The sample is already in that order, so nothing is restructured — but it is now a
  requirement rather than an accident, and the comment should say so.

`UserArticle.ArticleId` (lines 121, 129) is the one place the shape changes rather than a line being
deleted: it must read `article1!.Id` off the entity `PostMappedAsync` returned, which is exactly the
round-trip section 2 exists to enable.

This fixes the sample's real dangling-FK bug, not merely adapting it to the new rule.

### 4. Callers that break

Both load-test call sites set a key and then write through a client:

- `Iverson.LoadTest/Scenarios/WritePathRunner.cs:99,111,122` — `Id = Guid.NewGuid()` on Author, Tag
  and Article, then `PersistAsync`.
- `Iverson.LoadTest/Seeding/DirectSeeder.cs:116,178,254` — the same, inside `PostToStarRocksAsync`.
  Its Postgres `COPY` half mints its own IDs but bypasses gRPC entirely and is unaffected.

**The breakage would be silent, which is why fixing these six initializers is part of this change
rather than follow-up cleanup.** Both call sites already catch `InvalidArgument` and treat it as
expected — the field-permission rules deliberately reject the regular identity's writes
(`WritePathRunner.cs:145-148`, and `PostToStarRocksAsync`'s try/catch per the comment at
`DirectSeeder.cs:104-109`). After section 1 lands, every load-test write would fail and be absorbed
into the expected-rejection counter: the load test would report success while writing nothing.

`WritePathRunner.cs:127` falls back to `Guid.NewGuid()` for `BenchmarkAuthorId` when no author IDs are
available. That is a foreign key, not a key, and stays legal — FKs are not existence-checked.

No retry, reconciliation, backfill or import path depends on re-posting at a known key. The outbox
replays events; it never re-issues a `Post`.

No documentation anywhere in the repo describes client-assigned keys, so none needs correcting.

### 5. Testing

**`AssignNewKey` unit tests** extend `EntityKeyAccessorTests`. The boundary of "not supplied" is the
load-bearing part: absent field, `""`, JSON `null` and the all-zeroes `Guid` each accept and stamp;
any other non-empty value throws `RpcException` with `StatusCode.InvalidArgument`. One test asserts
the returned key round-trips through `ExtractKey` and parses as a `Guid`.

**Both write paths, one shared expectation.** `ObjectPersistenceGrpcService.Post` and
`ObjectMappingGrpcService.Post` each get a rejection test and an assignment test. These are
deliberately parallel: the pair *is* the regression test for the divergence, and either passing alone
is the state the codebase was already in.

**Gate ordering gets its own test.** An unauthorized caller sending a supplied key must receive
`PermissionDenied`, not `InvalidArgument`. Both orderings "work" under an authorized caller, so
without this test a wrong implementation passes.

**An existing test asserts the old behaviour and must be inverted, not deleted.**
`ObjectPersistenceGrpcServiceTests.Post_IgnoresClientProvidedKey_AndAssignsServerKey:146` asserts
`response.Key.Should().NotBe(clientGuid)` on a successful response. It becomes a rejection test and is
renamed. Any other test found writing with a pre-set key gets the same treatment — a test asserting
the old contract is asserting the bug, and the fix is inversion, never removal.

**Clients: three tests per language, twelve total**, following each library's existing coordinator-test
convention against a mocked mapping stub. `postMapped` returns the entity hydrated from `Data`;
`updateMapped` sends the key it was given; `getMapped` passes `depth` through. Each asserts against
the stub's captured request, not merely a non-null return.

**Mutation testing is required.** Each new test must be shown to kill a specific mutation: inverting
the `AssignNewKey` guard so supplied keys are accepted; dropping the `Guid.Empty` clause so unset
.NET/Java entities begin failing; moving key assignment before the authorization gate; and, in each
client, hard-coding `depth` to `1` and returning a fresh entity instead of the deserialized `Data`.

**No new integration or live-stack test.** The conformance harness is the live-stack proof for this
contract, and it is out of scope here.

## Out of scope

- **The conformance harness rework.** Scenario S1 was built around caller-chosen keys and needs
  rewriting to thread server-returned keys between phases. That happens against this contract once it
  lands, as a separate change to the existing 11-task harness plan.
- **Foreign-key existence validation.** `RelationValidator` checks FK shape, not row existence. That
  gap is what let the sample's dangling FKs go unnoticed, and it is worth its own design pass — but
  adding it here would change the write path's failure modes well beyond the ID rule.
- **Collapsing the two write paths.** Considered and rejected for this change: both
  `ObjectPersistence` and `ObjectMapping` remain, with all five clients able to use either.
- **The .NET/other-clients error-convention split** (log-and-return-null vs. throw).
- **Go's `PermissionDenied` on write**, observed during the harness's first run and not yet
  root-caused. Unrelated to the key rule.

## Verified assumptions

Verified against the codebase on 2026-08-10. Sixteen held; three changed the spec's content and are
marked.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `EntityKeyAccessor` exposes `ExtractKey`/`SetKey` as quoted, and the project can throw `RpcException` | `EntityKeyAccessor.cs:6-9`; `Iverson.Api.csproj:19` references `Grpc.AspNetCore` 2.80.0 |
| A2 | `ExtractKey` returns `""` for an absent field and for a JSON `null` | `EntityKeyAccessor.cs:13-19` returns `v.StringValue`; `StructFieldAccess.Candidates` supplies the casing fallback |
| A3 | Both services inject `IEntityKeyAccessor` and reach `schema.KeyColumn.Name` | `ObjectPersistenceGrpcService.cs:19`, `ObjectMappingGrpcService.cs:26`; `SchemaBuilder.cs:163` |
| A4 | Gate order in both `Post` methods is auth → relations → key assignment | `ObjectPersistenceGrpcService.cs:33-49`; `ObjectMappingGrpcService.cs:294-305` |
| A5 | No other server path assigns an entity key | The only two `SetKey` call sites are the two `Post` methods; other `CreateVersion7` uses mint outbox/DLQ row IDs (`OutboxWriter.cs:39`, `DlqRepository.cs:19`, `EnrichmentConsumer.cs:160`, `ObjectMappingGrpcService.cs:425`) |
| A6 | The assigned key reaches the client on both paths | `object_persistence.proto:21` (`key`); `object_mapping.proto:186` (`data`), returned as `request.Payload` after `SetKey` stamps it |
| A7 | `Update` requires a caller-supplied key on both services | `ObjectMappingGrpcService.cs:335-338`; `ObjectPersistenceGrpcService.cs:98-101` |
| A8 | All four clients' coordinators can reach a mapping stub | Python `core.py:472`; TypeScript `core.ts:509`; Java `IversonClient.java:35` (package-private `mappingStub`, used at `EntityCoordinator.java:137`); Go via `client.MappingStub` (`coordinator.go:150`) |
| A9 | The generated protos carry the needed messages and RPCs | `object_mapping.proto:10-12` declares `Get`, `Post`, `Update` |
| A10 | Each client has converters in both directions | Python `_entity_to_struct:368` / `_from_struct` (used `core.py:535`); TypeScript `entityToPayload` / `payloadToEntity:468`; Go `entityToStruct:425` / `structToEntity:551`; Java `StructConverter.toStruct:34` / `fromStruct:58` |
| A11 | **FAILED.** Go's mapping dependency is not the full stub | `coordinator.go:29-32` types it `MappingDeleteClient` — Delete only — via `mappingDeleteAdapter` at `coordinator.go:694`. Spec section 2 now requires widening the interface and adapter |
| A12 | .NET's mapped signatures are as quoted | `EntityCoordinator.cs:33`, `:50`, `:66` |
| A13 | Sample models carry `Guid [IversonKey] Id`; `PersistAsync` returns `string?` | `Models/Article.cs:8-9` and the four sibling models; `EntityCoordinator.cs:101` |
| A14 | The sample's write order already satisfies the new dependency | `Program.cs` writes authors (41), tags (64), articles (74), user (100), user-articles (117) in that order |
| A15 | `RelationValidator` never existence-checks FK rows | `RelationValidator.cs:79` resolves the FK *column* against the schema; no repository call anywhere in the type |
| A16 | **CHANGED.** The breaking-caller census is larger than first stated | `WritePathRunner.cs:99,111,122` and `DirectSeeder.cs:116,178,254` both set `Id` then call `PersistAsync`. `DirectSeeder`'s Postgres `COPY` half is unaffected |
| A17 | **CHANGED.** An existing test asserts the old behaviour | `ObjectPersistenceGrpcServiceTests.cs:146` — `Post_IgnoresClientProvidedKey_AndAssignsServerKey` must be inverted and renamed |
| A18 | Nothing depends on client-supplied keys being honoured | No re-post/import/backfill path exists; the only client-write callers outside tests are the two load-test sites in A16. Both absorb `InvalidArgument` as expected, so their breakage would be silent — recorded in section 4 |
| A19 | No documentation describes client-assigned keys | No match across `README.md`, `CLAUDE.md` or `docs/` |
