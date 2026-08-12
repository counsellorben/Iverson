# Acting-user identity parity across the five clients

**Status:** design approved 2026-08-12, not yet planned or implemented.

**Origin:** the one Important finding from the whole-branch review of `server-generated-ids`
(branch at `356708d`). That branch made every entity key server-generated and gave the four
non-.NET clients a mapped-CRUD surface; the review found that no sample can complete a live run,
because acting-user identity reaches only the write and mapped-CRUD paths.

## Problem

Acting-user identity is plumbed unevenly, and the unevenness is invisible until a live run.

In .NET, 6 of 14 `EntityCoordinator<T>` methods accept a `Metadata? headers` argument;
`DeleteAsync`, `UpdateAsync`, `GetAsync`, `GetManyAsync`, `SearchAsync`, `PipelineAsync`,
`SearchSimilarAsync` and `SearchChunksAsync` accept none. In Java, 5 search-family methods have
token overloads while `get`, `getMany`, `delete`, `persist`, `update` and `search` have none —
and `search` is exactly what the Java sample calls.

The failure is silent in the worst way. `GetMappedAsync` and `GetAsync` log and return `null`
rather than throwing, so an unauthorized read is indistinguishable from an empty result. The .NET
sample therefore seeds and writes successfully, then every subsequent read returns null, every
search loop yields nothing, and the four cleanup deletes delete nothing — while the sample prints
`Deleted user-articles and articles` and exits 0. Orphaned rows are left behind and the run
reports success.

Two further facts make this a class of bug rather than an instance. The five clients do not share
one identity model: Go carries identity in `context.Context`, Python and TypeScript bind it at
client construction, and .NET and Java pass it per call. And the server does not tolerate a
duplicated identity header — `Headers["x-acting-user-authorization"].ToString()` joins two values
into `"Bearer A, Bearer B"`, which still satisfies the `StartsWith("Bearer ")` check, so
`context.Token` becomes the corrupt `"A, Bearer B"` and JWT validation fails. A caller that sends
identity twice becomes unauthenticated rather than denied.

## Design

### 1. One resolution rule, five clients

Every coordinator call resolves its acting-user identity from the first present source:

1. an identity supplied for **this call** (per-call override),
2. the identity bound to this coordinator instance by `WithActingUser` (bound view),
3. the identity configured on the client at construction (ambient default),
4. none — the call carries only the service's own client-credentials token, exactly as today.

Rule 4 is the backward-compatibility guarantee: a client constructed without an acting-user
identity behaves precisely as it does now, so no existing caller changes behaviour until it opts
in.

`WithActingUser(token)` returns a **new** coordinator sharing the original's dependencies and
differing only in bound identity. It never mutates the receiver: two coordinators bound to two
identities must be safely usable from concurrent tasks, which the load test does today with two
identities across sixteen or more parallel workers.

No client-side validation that a token is present, well-formed, or unexpired. The server is the
authority on authorization; a second copy of that judgment in five languages is the divergence
this line of work exists to remove. A missing identity yields the server's `PermissionDenied` or
an empty result, never a client-side throw.

### 2. .NET

`EntityCoordinator<T>` gains the ambient identity as an eighth constructor dependency and a
`WithActingUser(token)` method returning a copy with a bound identity. Because the type is
registered as `AddTransient(typeof(EntityCoordinator<>))` and activated by reflection, the
ambient identity must be a container-resolvable type: a small `ActingUserIdentity` wrapping the
optional `Func<Task<string>>?` that `AddIversonClient` already accepts, registered as a
singleton.

`SchemaCatalogClient` is **not** changed. It already applies the ambient provider correctly
(`SchemaCatalogClient.cs:19-20`), and it has its own tests that construct it with the raw
`Func<Task<string>>?`. Sharing one injectable type between the two consumers would buy tidiness
and cost test churn on a class with no defect.

There is no single choke point for identity: all 15 stub call sites invoke their own stub, and
only `GroupBy` (`:299`) and `Aggregate` (`:319`) pass headers today. Each of the 15 therefore
routes its headers through one private `ResolveHeadersAsync(Metadata? headers)` helper that
applies the resolution rule. The eight header-less methods gain no parameter but do gain a body
change.

**Merge semantic**, since `Metadata` is a bag rather than an identity: if the supplied `Metadata`
already carries `x-acting-user-authorization`, it wins untouched; if it carries other headers but
no identity, the resolved identity is added to it. A caller cannot suppress identity by passing an
unrelated header, and cannot have an explicit identity silently overwritten. `Metadata.Get(key)`
supports the check.

### 3. Java

`IversonClient` gains an optional acting-user identity. `EntityCoordinator<T>` gains
`withActingUser(token)` returning a new coordinator with a bound identity, and both existing
identity helpers — `stubFor` (`:319`) and `mappingStubFor` (`:329`) — fall back bound → ambient.
Those two helpers are the only places identity attaches (`withOption` appears nowhere else in the
file), so the resolution rule has exactly two implementation sites.

Two constraints the implementation must respect:

- `EntityCoordinator` has a second, package-private constructor `(searchStub, entityType)` at
  `:55` that never sets `client`. The clone path and the ambient lookup must both tolerate a null
  client, or the existing search tests fail with a `NullPointerException`.
- `search` at `:208` calls `searchStub.search(request)` directly, bypassing `stubFor`. Closing
  the Java sample's failing call requires routing `:208` through the helper — ambient alone does
  not reach it.

The five existing token overloads remain as the explicit per-call form.

### 4. TypeScript

The smallest change of the five. `callUnary` already takes an `actingUserToken` per call and
resolves it into fresh metadata (`core.ts:146-165`), and the coordinator already threads
`this._client._actingUserToken` at 16 sites. `withActingUser(token)` returns a new
`EntityCoordinator(this._cls, this._client)` carrying a bound token it passes instead of the
client's. No signature changes, no duplicate-header risk.

`ActingUserToken` is already `string | (() => Promise<string>)` (`core.ts:135`), which matches
.NET's `Func<Task<string>>` ambient — the two clients express the same concept.

### 5. Go

Go gets the ambient default only. `WithActingUserToken(ctx, token)` (`auth.go:23`) is already
exported and is the idiomatic Go per-call override; adding a `WithActingUser` coordinator wrapper
would be a second way to do one thing. `OAuth2ClientCredentials` gains an optional default token
consulted when the context carries none (`auth.go:52`). Every coordinator method already takes and
forwards `ctx`, so reads, writes, deletes and searches all reach the resolution rule with no
signature change.

Go's per-call override therefore stays `ctx`-shaped rather than `WithActingUser`-shaped: a
deliberate divergence in spelling, not in model. The resolution rule is identical.

### 6. Python

Python's identity moves out of channel call-credentials and onto the client, matching
TypeScript's model.

Today `_ActingUserAuthPlugin` attaches a static token to **every** call from channel
call-credentials (`auth.py:82-93`), so a per-call override would emit a second
`x-acting-user-authorization` header — which, per the Problem section, corrupts the token rather
than overriding it. Instead the client holds the ambient token and coordinators pass it per call
as `metadata=`, with `with_acting_user(token)` returning a bound coordinator.

`EntityCoordinator.__init__(entity_class, channel)` takes a channel rather than a client
(`core.py:478`), so it cannot see a client-level token today; the token becomes a third, optional
parameter, threaded from the client's factory at `core.py:756`. Keeping it optional preserves the
four existing two-argument constructions in `tests/test_entity_coordinator.py`.

Removing the acting-user plugin leaves `_BearerTokenAuthPlugin` intact, and relaxes a constraint:
grpcio rejects `CallCredentials` on a bare insecure channel (`core.py:727-730`), a hoop per-call
metadata does not need. The `if credentials is not None or acting_user_token is not None:` guard
narrows to `credentials is not None`.

**Sequencing risk.** Nothing in this repo currently passes `metadata=` to a Python stub call. It
is standard grpcio, but unprecedented here, so the first Python task must prove one method
end-to-end before the remaining methods follow.

### 7. Callers

**The three samples switch to ambient and get shorter.** The .NET sample already supplies
`actingUserTokenProvider` (`Program.cs:41`), so once the coordinator consumes the ambient
identity, its explicit `headers` argument is redundant at all 16 call sites, and its eight
non-mapped calls start working for the first time. The `headers` variable and every `, headers` /
`headers: headers` argument are deleted. Java and TypeScript follow the same shape: bind identity
at client construction, drop the per-call token arguments.

**The load test is unchanged, and is the compatibility proof.** Its read-path calls raw generated
stubs with explicit headers (`ReadPathScenario.cs:66-71,230-235`) rather than coordinators, and
its write-path passes explicit `headers` to `PersistAsync` at six sites, which rule 1 preserves as
the winning identity. It is the one in-repo caller that genuinely switches identity per request,
so its continuing to work without edits is the evidence that per-call override survives.

### 8. Testing

Each client gets tests for the resolution rule, because the rule is the design and it is what
will rot:

- bound identity wins over ambient,
- ambient applies when nothing is bound,
- no identity configured anywhere sends no acting-user header (the rule-4 compatibility case),
- `WithActingUser` does not mutate the receiver — the original still resolves to ambient.

Plus, in .NET only, the two merge cases: an explicit identity in `Metadata` wins untouched, and
non-identity headers receive the resolved identity. And in Python specifically, a test that
exactly **one** `x-acting-user-authorization` header is emitted — the failure mode that motivated
moving off channel credentials needs a test that would catch a regression to it.

Every assertion is on the metadata actually handed to the stub, never on a client-side field: a
bound-but-unused token is precisely the bug that would otherwise pass.

## Out of scope

- **The server.** No change to how identity is read or authorization evaluated.
- **The write and mapped-CRUD contracts** established by `server-generated-ids`.
- **Client-side token validation** of any kind.
- **Go and Python samples.** Neither writes; both are schema-inspection and query-builder demos.
- **The conformance harness.** The `client-conformance-harness` branch consumes all five clients,
  and Python's identity mechanism moving out of channel credentials is a breaking change for
  whatever it does today. Those two branches need reconciling whichever lands second. Not examined
  here; pulling it into scope would widen this well past the finding.

## Verified assumptions

Verified against the codebase on 2026-08-12 at `356708d`.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `EntityCoordinator<T>` is DI-activated by reflection, so an ambient dependency must be container-resolvable | `ServiceCollectionExtensions.cs:75` — `services.AddTransient(typeof(EntityCoordinator<>))`, no explicit factory |
| A2 | `actingUserTokenProvider` is a `Func<Task<string>>?` and `SchemaCatalogClient` is its only consumer | Declared `ServiceCollectionExtensions.cs:33`, passed only at `:81`; consumed only at `SchemaCatalogClient.cs:13,19-20` |
| A3 | **Corrects the draft design.** There is no single choke point in .NET: 15 stub call sites, only 2 passing headers | `EntityCoordinator.cs:37,54,70,86,105,121,140,170,199,218,236,255,275,299,319`; `headers` present only at `:299` (`GroupBy`) and `:319` (`Aggregate`) |
| A4 | `Metadata.Get(key)` supports the merge rule's existing-identity check | Used at `EntityCoordinatorMappedWriteTests.cs:45,72,99`, `EntityCoordinatorAggregateTests.cs:73`, `EntityCoordinatorGroupByTests.cs:65` |
| A5 | The .NET sample already supplies an ambient provider, so ambient needs no new sample wiring | `Iverson.Client.Sample/Program.cs:41` — `actingUserTokenProvider: () => Task.FromResult(actingUserToken)` |
| A6 | Dropping the sample's `headers` arguments leaves valid calls (16 sites, two argument forms) | `Program.cs:101,117,121,122,123,137,147,158,170,177` positional; `:185,189,193,197,209` named `headers:`; `:204` positional |
| A7 | The load test keeps working unedited: coordinator writes pass explicit headers, reads use raw stubs | `WritePathRunner.cs:104,114,130` and `DirectSeeder.cs:122,182,261` pass `(entity, headers, ct)`; `ReadPathScenario.cs:66-71,230-235,298-303` calls `retrieval.GetMany` / `search.Search` / `search.AggregateAsync` directly |
| A8 | Java's public coordinator constructor is `(client, entityType)`, cheap to clone | `EntityCoordinator.java:41` |
| A9 | `stubFor` and `mappingStubFor` are the only identity-attachment points in Java | `withOption` appears only at `EntityCoordinator.java:321,333`, inside those two helpers |
| A10 | `IversonClient` has no acting-user field today; 4 public constructors plus 1 package-private test seam | `IversonClient.java:43,51,63,73` public; `:85` package-private `(mappingStub)` |
| A11 | Java's `search(QueryBuilder)` — the Java sample's failing call — has no token overload, unlike the five search-family methods that do | `EntityCoordinator.java:206` takes only `QueryBuilder<T>`; compare the paired overloads at `:223/228` (`groupBy`), `:243/248` (`aggregate`), `:259/264` (`pipeline`), `:276/281` (`searchSimilar`), `:298/303` (`searchChunks`) |
| A12 | **Corrects the draft design.** Java has a second package-private constructor that never sets `client`, and `search` bypasses `stubFor` | `EntityCoordinator.java:55` — `EntityCoordinator(searchStub, entityType)` sets `this.searchStub` at `:63` but no client; `:208` calls `searchStub.search(request)` directly |
| A13 | TypeScript already threads the client's identity broadly | 16 references to `_client._actingUserToken` / `_client._callCredentials` in `core.ts` |
| A14 | TypeScript's coordinator constructor is `(_cls, _client)`, so a clone is trivial | `core.ts:510-513` |
| A15 | `ActingUserToken` is `string \| (() => Promise<string>)`, matching .NET's ambient shape | `core.ts:135`; resolved at `:146-150` |
| A16 | Python composes the acting-user plugin into channel call-credentials, attaching a static token to every call | `core.py:713-723`; `auth.py:82-93` — `_ActingUserAuthPlugin.__call__` returns the header unconditionally |
| A17 | Python's coordinator takes a channel, not a client, so it cannot see a client-level token today | `core.py:478` — `def __init__(self, entity_class: type, channel: grpc.Channel)` |
| A19 | Removing the acting-user plugin leaves the bearer plugin intact, and relaxes the insecure-channel constraint | `core.py:715-719` composes them independently; `:727-730` documents grpcio rejecting `CallCredentials` on a bare insecure channel |
| A20 | The client factory constructs coordinators, and 4 test sites construct them with two arguments | `core.py:756` — `return EntityCoordinator(entity_class, self._channel)`; `tests/test_entity_coordinator.py:45,52,76,205` |
| A18 | **Sequencing risk.** No in-repo precedent for `metadata=` on a Python stub call | Only match for `metadata=` in `core.py` is `:242` `is_metadata=`, unrelated |
| A21 | Go's `OAuth2ClientCredentials` is built as a field-named struct literal, so adding a field breaks nothing | `Go/iverson/auth_test.go:19,38`; no production construction site in-repo |
| A22 | Every Go coordinator method takes and forwards `ctx`, so ambient/ctx reaches reads too | `coordinator.go:183,202,221,236,255,275,295,315,356,382,408,431,454,476` |
| A24 | The server corrupts rather than disambiguates a duplicated identity header | `Iverson.Api/Program.cs:131-134` — `Headers[key].ToString()` joins `StringValues` with `", "`, still matches `StartsWith("Bearer ")`, so `context.Token` becomes `"A, Bearer B"` |
| A23 | The Go and Python samples never write, so they need no identity and are untouched | `Go/sample/main.go` is schema-inspection and QueryBuilder only (its own header states it does not connect to a live server); `Python/sample/main.py` calls only `demo_query_builder` / `demo_in_operator` |
| A25 | Each client's existing coordinator-test mocking convention, which the new resolution-rule tests must follow rather than invent | Go: white-box `package iverson` hand-written mock plus `newTestCoordinator` (`coordinator_test.go:447-454`). Python: `MagicMock()` swapped onto the private stub attribute (`test_entity_coordinator.py:39-46`). TypeScript: `makeClientLike({ _mappingClient: … })` (`core.test.ts:105-118`). Java: `@Mock` + `MockitoExtension`, `withOption` stubs declared `lenient()` (`EntityCoordinatorTest.java:17-19,57-58`). .NET: `Substitute.For<…Client>()` with a hand-built `AsyncUnaryCall<T>` via `TestCoordinatorFactory` |
| A26 | The only in-repo consumers of the five coordinators are the samples and the clients' own tests | Full-tree grep for `EntityCoordinator` outside client source: `Python/sample/main.py`, `DotNet/Iverson.Client.Sample/Program.cs`, `Java/sample/.../Main.java`, plus each client's test files |
| A27 | **Corrects the draft design.** `SchemaCatalogClient` has its own tests constructing it with the raw `Func`, so changing its signature is avoidable churn | `SchemaCatalogClientTests.cs` exists; ctor is `SchemaCatalogClient(mapping, Func<Task<string>>? = null)` at `SchemaCatalogClient.cs:11-13` |

## Known issues / accepted as out of scope

- **Go's per-call override is spelled differently** from the other four: `WithActingUserToken(ctx, token)`
  rather than a bound coordinator. Accepted by Ben as the idiomatic Go form — the resolution rule
  is identical, only the spelling differs.
- **`GetAsync` and `GetMappedAsync` still log and return `null`** on an unauthorized read rather
  than surfacing the denial, so an authorization failure remains indistinguishable from an empty
  result at the call site. This design makes the reads *authorized*; it does not change .NET's
  log-and-return-null error convention, which the `server-generated-ids` Global Constraints
  deliberately preserve. A caller that wants to distinguish the two cases still cannot.
