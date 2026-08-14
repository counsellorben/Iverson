# Acting-user identity parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-12-acting-user-identity-parity-design.md` (commit SHA: `dc490215b1c0c38db18270a8539ff8d57cf62785`)

**Goal:** Give all five clients one acting-user identity resolution rule — per-call, then bound, then ambient, then none — so every coordinator operation can carry an identity, not just writes and mapped CRUD.

**Architecture:** Each client gains an ambient identity at client construction and a `WithActingUser(token)` that returns an identity-bound coordinator; the existing per-call forms stay as the highest-priority override. Go is the exception by design: its per-call override already exists as `WithActingUserToken(ctx, token)`, so Go gains only the ambient fallback. Python additionally moves its identity off channel call-credentials, because a channel-level plugin plus a per-call override would emit the header twice and corrupt the token.

**Tech stack:** .NET 10 / xUnit / FluentAssertions / NSubstitute; Java 21 / Maven / JUnit 5 / Mockito 5.14.2; Python / pytest / grpcio ≥ 1.81.1; TypeScript / vitest; Go 1.x stdlib `testing`.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **The resolution order is per-call → bound → ambient → none.** First present source wins.
- **Rule 4 is a compatibility guarantee.** A client constructed with no acting-user identity must send no acting-user header, exactly as today. No existing caller changes behaviour until it opts in.
- **`WithActingUser` never mutates the receiver.** It returns a new coordinator; the original still resolves to ambient. Two bound coordinators must be safely usable from concurrent tasks.
- **No client-side validation** that a token is present, well-formed, or unexpired. The server is the authority; a missing identity yields `PermissionDenied` or an empty result, never a client-side throw.
- **Every assertion is on the metadata actually handed to the stub**, never on a client-side field. A bound-but-unused token is precisely the bug that would otherwise pass.
- **Mutation testing is required** for every task. Each new test must be shown to kill a named mutation; the specific mutations are listed per task. This is not from the spec — it is this repo's standing discipline, after green suites here previously hid three unfalsifiable tests.

## File Structure

**Create**
- `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs` — the container-resolvable ambient wrapper for .NET
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorIdentityResolutionTest.java`

**Modify — clients**
- `Iverson.Clients/TypeScript/src/core.ts` — bound-token field, `withActingUser`, one resolver replacing 8 ambient reads
- `Iverson.Clients/Go/iverson/auth.go` — `DefaultActingUserToken` field plus its fallback in `GetRequestMetadata`
- `Iverson.Clients/Python/iverson_client/core.py` — client-held token, coordinator third parameter, `with_acting_user`, `get_schema` metadata, credential-guard narrowing, 14 stub call sites
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java` — ambient identity
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java` — copy path, `withActingUser`, the generic `withIdentity` helper, and routing all four stub families through it
- `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs` — 8th ctor parameter, `ResolveHeadersAsync`, `WithActingUser`, 9 call sites
- `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs` — register `ActingUserIdentity`

**Modify — samples**
- `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java` — bind identity at construction, drop 3 per-call token arguments
- `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs` — delete the `headers` variable and all 16 arguments

**Test**
- `Iverson.Clients/TypeScript/tests/core.test.ts` — modify: 4 resolution-rule cases
- `Iverson.Clients/Go/iverson/auth_test.go` — modify: 3 fallback cases
- `Iverson.Clients/Python/tests/test_auth.py` — modify: `get_schema` identity case, single-header case
- `Iverson.Clients/Python/tests/test_entity_coordinator.py` — modify: 4 resolution-rule cases

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here. Trusted as ground truth: A1–A28 of the spec's "Verified assumptions" table.

Three of those are load-bearing for task shape and worth restating: `SchemaCatalogClient` is deliberately **not** changed (A27); the load test needs **no** edit and is the per-call-override compatibility proof (A7); and Python's `registrar()` is unaffected because `RegisterSchema` is authorized by the `schema_admin` client-credentials scope rather than the acting user (spec §6).

## Verified plan-level assumptions

Newly introduced by this plan and verified against the codebase on 2026-08-12 at `dc49021`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| PA1 | File path | All three created files are genuinely new | `ActingUserIdentity.cs`, `EntityCoordinatorIdentityResolutionTests.cs`, `EntityCoordinatorIdentityResolutionTest.java` — existence check returned absent for each |
| PA2 | Signature | **Corrects the draft.** TypeScript has **8** ambient-identity read sites, not 16 | `grep -c "_client._actingUserToken" TypeScript/src/core.ts` → 8; `_client._callCredentials` → 8 separately. The spec's A13 counted both symbols together |
| PA3 | Signature | TypeScript's coordinator constructor is `(_cls, _client)`, so the clone is a two-argument construction | `core.ts:510-513` |
| PA4 | Test convention | TypeScript tests already assert on emitted metadata, which is what the Global Constraints require | `tests/core.test.ts:114-121` defines `makeClientLike`; `:151` and `:166` assert `calls[0].metadata.get(ACTING_USER_METADATA_KEY)` against an ambient token — both the string and provider forms |
| PA5 | Code validity | `ACTING_USER_METADATA_KEY` lives in `src/auth.ts`, not `src/core.ts`, so the new tests import it from there | `src/auth.ts:71` exports it; `tests/core.test.ts:29` imports it as `from '../src/auth.js'` |
| PA6 | Signature | Go's `GetRequestMetadata` sets the acting-user key only inside the ctx-present branch, so a struct-field fallback slots in as the else arm | `Go/iverson/auth.go:46-56` — `md[ActingUserMetadataKey]` assigned only within `if actingUserToken, ok := ctx.Value(...)`; `ActingUserMetadataKey` declared at `:18` |
| PA7 | Test convention | Go's `auth_test.go` constructs credentials as a field-named struct literal and exercises `GetRequestMetadata`, so three fallback cases fit its existing shape | `auth_test.go:11` `TestOAuth2ClientCredentials_GetRequestMetadata_FetchesAndCachesToken`, `:19` `&OAuth2ClientCredentials{ClientID: …}`, `:37` `…_RequireTransportSecurity_ReturnsFalse` |
| PA8 | File path | Python's `get_schema` is covered in `tests/test_auth.py`, not a client-specific test file | `grep -rln get_schema tests/` → `tests/test_auth.py` only |
| PA9 | Code validity | Python's generated stubs are `channel.unary_unary` multicallables, whose `__call__` accepts `metadata=` | `iverson_client/generated/object_mapping_pb2_grpc.py:64-68` — `self.GetSchema = channel.unary_unary(...)`; `pyproject.toml:11` pins `grpcio>=1.81.1` |
| PA10 | Consumer impact | Removing `_ActingUserAuthPlugin` from the credential composition breaks no other consumer | Only three references repo-wide: its definition (`auth.py:82`), the import (`core.py:18`), and the single composition site (`core.py:722`) |
| PA11 | Consumer impact | **Corrects the draft.** Python has **14** coordinator stub call sites needing `metadata=`, not the spec's "ten methods" | `core.py:503,517` (persistence Post/Update), `:529,542,558,571` (mapping Delete/Get/Post/Update), `:584,598` (retrieval Get/GetMany), `:617,626,631,637,642,648` (search Search/SearchSimilar/SearchChunks/GroupBy/Aggregate/Pipeline) |
| PA12 | Signature | Java's `IversonClient` stubs are package-private `final` fields, so a coordinator copy path can read them | `IversonClient.java:35-38` — `final ObjectMappingServiceGrpc…mappingStub` and three siblings, no access modifier |
| PA13 | Consumer impact | The Java sample passes a per-call token at exactly three sites and calls the token-less `search` at one | `Main.java:81` `postMapped(author, actingUserToken)`, `:99` `postMapped(article, actingUserToken)`, `:103-104` `getMapped(…, 1, actingUserToken)`; `:120` `articleCoordinator.search(` with no token |
| PA14 | Code validity | .NET primary-constructor parameters are in scope in the class body, so `WithActingUser` can construct a sibling instance from them | `EntityCoordinator.cs:25` uses `registry` in a field initializer and `:36,53` use `logger` inside methods — captured parameters are demonstrably usable in the body |
| PA15 | Consumer impact | **The 8th .NET constructor parameter must have a default**, or `TestCoordinatorFactory` stops compiling | `TestCoordinatorFactory.cs:30-38` calls `new EntityCoordinator<T>(...)` with exactly seven positional arguments. Declaring the parameter `ActingUserIdentity? identity = null` keeps that file untouched while DI still injects the registered singleton |
| PA16 | Consumer impact | **Corrects the draft.** .NET has 15 stub call sites, 6 already passing headers, so **9 sites across 8 methods** change | Headers already passed at `EntityCoordinator.cs:45,61,77,112,299,319`. The 8 header-less methods span 9 sites because `PipelineAsync` calls the stub twice (`:255`, `:275`) |
| PA17 | Command | The five per-client commands run as written | All five executed during this session's prior work: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj` (49 passed), `mvn -f Iverson.Clients/Java/pom.xml test` (170 passed), `cd Iverson.Clients/Python && python3 -m pytest -q` (180 passed), `cd Iverson.Clients/TypeScript && npm test` (176 passed), `cd Iverson.Clients/Go && go test ./... && go vet ./...` (clean) |
| PA18 | Command | Python's runner is `python3`, not `python` | `python -m pytest` failed with `python: command not found`; `python3 -m pytest -q` succeeded |
| PA19 | Task ordering | No task consumes a symbol another task introduces; the five file sets are pairwise disjoint | Per "File Structure" — each task's paths sit under a different client directory, and the only new cross-file symbol (`ActingUserIdentity`) is created and consumed inside Task 5 alone |
| PA20 | Sibling sweep | Every identifier the plan names resolves at its point of use | Pre-existing and confirmed: `ActingUserToken` (`core.ts:135`), `makeClientLike` (`core.test.ts:114`), `ACTING_USER_METADATA_KEY` (`auth.ts:71`), `WithActingUserToken`/`actingUserTokenKey`/`ActingUserMetadataKey` (`auth.go:23,14,18`), `_ActingUserAuthPlugin`/`_BearerTokenAuthPlugin` (`auth.py:82`, composed `core.py:715-722`), `stubFor`/`mappingStubFor` (`EntityCoordinator.java:319,329`), `OAuth2ClientCredentials.ACTING_USER_TOKEN`, `TestCoordinatorFactory.Create` (`TestCoordinatorFactory.cs:18`), `Metadata.Get` (5 test call sites), `ActingUserMetadata.WithActingUser` (`ActingUserMetadata.cs:9`). Newly introduced by this plan, so unresolvable until their task runs: `ActingUserIdentity`, `ResolveHeadersAsync`, `withActingUser`/`with_acting_user`, `DefaultActingUserToken` |
| PA21 | Command | Commit messages are lowercase imperative sentences with no Conventional-Commits prefix | `git log --oneline -8`: "stop the load test assigning entity keys it cannot own", "add acting-user identity parity design spec", "rename the Go registrar rules test after the collapsed RegisterAll signature" |
| PA22 | Signature | The Python coordinator **discards** the channel after building four stubs from it, and its tests inject mocks by overwriting those stub attributes after construction — so a bound clone must copy state, not reconstruct from a channel | `core.py:478-490` assigns `_cls`, `_type_name`, `_key_field`, `_mapping`, `_persistence`, `_retrieval`, `_search` and no `self._channel`. `tests/test_entity_coordinator.py:44-46` does `coordinator._search = MagicMock()` after constructing, `:52-53` the same for `_mapping` — mocks a rebuilt clone would not carry |
| PA23 | Consumer impact | Java's coordinator reaches its four stubs by four different routes, so a helper covers a stub family rather than a method: `mappingStubFor` serves the mapped trio, `stubFor` serves the search family, and five methods call a stub field directly | `EntityCoordinator.java:78,94` use `client.persistenceStub`; `:111,124` use `client.retrievalStub`; `:145` uses the bare `client.mappingStub` rather than `mappingStubFor` (`:331`); `:208` uses the bare `searchStub` rather than `stubFor` (`:319`) |
| PA24 | Code validity | `EntityCoordinator.java` imports exactly one `io.grpc.*` type, so Step 4's generic helper needs `io.grpc.stub.AbstractStub` added — the existing `withOption` calls compile only because the concrete generated stubs inherit it, without the base type ever being named | `EntityCoordinator.java:3` is the file's only `io.grpc.*` import (`StatusRuntimeException`); `:321,333` call `withOption` on `ObjectSearchServiceBlockingStub` / `ObjectMappingServiceBlockingStub`, which extend `AbstractBlockingStub<Self>` and thence `AbstractStub<Self>` |

---

## Tasks

### Task 1: TypeScript identity resolution

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Test: `Iverson.Clients/TypeScript/tests/core.test.ts`

The smallest of the five: `callUnary` already accepts a per-call `actingUserToken` and resolves it into fresh metadata, so nothing about the wire path changes. Only where the token comes from changes.

- [ ] **Step 1: Write the four resolution-rule tests (they fail — `withActingUser` does not exist)**

In `tests/core.test.ts`, following the file's `makeClientLike` convention (`:114-121`) and asserting on emitted metadata exactly as `:151` already does. Import `ACTING_USER_METADATA_KEY` from `'../src/auth.js'` (PA5) — it is not exported from `core.ts`.

- `withActingUser() binds an identity that wins over the client's ambient one` — client built with `_actingUserToken: 'ambient'`, coordinator bound to `'bound'`, assert the emitted header is `['Bearer bound']`.
- `the client's ambient identity applies when nothing is bound` — assert `['Bearer ambient']`.
- `no identity anywhere emits no acting-user header` — client built with no `_actingUserToken`, assert `metadata.get(ACTING_USER_METADATA_KEY)` is empty. This is the rule-4 compatibility case.
- `withActingUser() does not mutate the receiver` — call `withActingUser('bound')`, then invoke the **original** coordinator and assert it still emits `['Bearer ambient']`.

- [ ] **Step 2: Add the bound field, the resolver, and `withActingUser`**

```typescript
    private _boundActingUser?: ActingUserToken;

    /**
     * Returns a coordinator bound to `token`, leaving this one untouched. The bound identity
     * outranks the client's ambient one; an explicit per-call token still outranks both.
     */
    withActingUser(token: ActingUserToken): EntityCoordinator<T> {
        const bound = new EntityCoordinator(this._cls, this._client);
        bound._boundActingUser = token;
        return bound;
    }

    private _identity(): ActingUserToken | undefined {
        return this._boundActingUser ?? this._client._actingUserToken;
    }
```

- [ ] **Step 3: Route the 8 ambient reads through the resolver**

Replace each `this._client._actingUserToken` with `this._identity()` — there are exactly 8 (PA2), not 16. Leave every `this._client._callCredentials` alone; those are the service's own credentials and are unrelated to acting-user identity.

- [ ] **Step 4: Run tests**

```bash
cd Iverson.Clients/TypeScript && npm test
```

- [ ] **Step 5: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| `_identity()` returns `this._client._actingUserToken` only | `withActingUser() binds an identity that wins over the client's ambient one` |
| `_identity()` returns `this._boundActingUser` only | `the client's ambient identity applies when nothing is bound` |
| `withActingUser` sets `this._boundActingUser` and returns `this` | `withActingUser() does not mutate the receiver` |

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/tests/core.test.ts
git commit -m "resolve the acting-user identity per call in the TypeScript coordinator"
```

---

### Task 2: Go ambient identity fallback

**Files:**
- Modify: `Iverson.Clients/Go/iverson/auth.go`
- Test: `Iverson.Clients/Go/iverson/auth_test.go`

Go gets no `WithActingUser` method. `WithActingUserToken(ctx, token)` (`auth.go:23`) is already the exported, idiomatic per-call override; adding a coordinator wrapper beside it would be a second way to do one thing. Only the ambient fallback is missing.

- [ ] **Step 1: Write the three fallback tests (they fail — the field does not exist)**

In `auth_test.go`, following its existing field-named struct-literal style (`:19`) and asserting on the returned metadata map:

- `TestGetRequestMetadata_CtxTokenWinsOverDefault` — credentials carry `DefaultActingUserToken: "ambient"`, ctx carries `"percall"`; assert `md[ActingUserMetadataKey] == "Bearer percall"`.
- `TestGetRequestMetadata_DefaultAppliesWhenCtxHasNone` — same credentials, bare ctx; assert `"Bearer ambient"`.
- `TestGetRequestMetadata_NoTokenAnywhereOmitsHeader` — no default, bare ctx; assert `ActingUserMetadataKey` is absent from the map. This is the rule-4 compatibility case.

These need a token endpoint for the client-credentials half; reuse the `httptest` server the existing `:11` test stands up.

- [ ] **Step 2: Add the field and the fallback**

```go
	// DefaultActingUserToken is the ambient acting-user identity, used when the call's
	// context carries none. A per-call WithActingUserToken always outranks it.
	DefaultActingUserToken string
```

Then in `GetRequestMetadata`, replace the single `if` at `:52-54` with the ordered resolution:

```go
	if actingUserToken, ok := ctx.Value(actingUserTokenKey{}).(string); ok && actingUserToken != "" {
		md[ActingUserMetadataKey] = "Bearer " + actingUserToken
	} else if c.DefaultActingUserToken != "" {
		md[ActingUserMetadataKey] = "Bearer " + c.DefaultActingUserToken
	}
```

- [ ] **Step 3: Run tests and vet**

```bash
cd Iverson.Clients/Go && go test ./... && go vet ./...
```

- [ ] **Step 4: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Drop the `else if` arm | `TestGetRequestMetadata_DefaultAppliesWhenCtxHasNone` |
| Make the `else if` an unconditional `if` that overwrites the ctx value | `TestGetRequestMetadata_CtxTokenWinsOverDefault` |
| Drop the `!= ""` guard on the default | `TestGetRequestMetadata_NoTokenAnywhereOmitsHeader` |

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Go/iverson/auth.go Iverson.Clients/Go/iverson/auth_test.go
git commit -m "fall back to an ambient acting-user token in the Go credentials"
```

---

### Task 3: Python identity relocation

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/test_auth.py`
- Test: `Iverson.Clients/Python/tests/test_entity_coordinator.py`

The step order here is load-bearing, not stylistic. Step 1 proves `metadata=` works on one call before fourteen more depend on it, discharging the spec's sequencing risk. Steps 1 and 2 are one indivisible change: Step 1's test asserts exactly one identity header, which cannot pass while the plugin is still composed, so Step 2 must land with it. A presence-only assertion in Step 1 would prove nothing — the plugin supplies the header on every call regardless of whether `metadata=` worked.

- [ ] **Step 1: Convert `get_schema` first, with its test**

Add to `tests/test_auth.py` (PA8 — this is where `get_schema` is already covered) a test asserting the call receives **exactly one** `x-acting-user-authorization` entry, not merely that one is present. With `_ActingUserAuthPlugin` still composed the count is two, so this test is what forces Step 2 to land alongside Step 1 — and once the plugin is gone, a passing count of one is proof that `metadata=` carried the identity itself. Then hold the ambient token on the client and pass it:

```python
    def get_schema(self, trace_id: str = "") -> list[mapping_pb.SchemaType]:
        """Return the catalog of registered types this identity may read."""
        response = self._mapping_stub.GetSchema(
            mapping_pb.GetSchemaRequest(trace_id=trace_id),
            metadata=self._acting_user_metadata(),
        )
        return list(response.types)
```

with one private helper on the client, which is also what Step 2's coordinator threading reuses:

```python
    def _acting_user_metadata(self) -> tuple[tuple[str, str], ...]:
        """Per-call metadata carrying the ambient acting-user identity, or empty when none."""
        if not self._acting_user_token:
            return ()
        return ((ACTING_USER_METADATA_KEY, f"Bearer {self._acting_user_token}"),)
```

`ACTING_USER_METADATA_KEY` already exists in `auth.py` and is what `_ActingUserAuthPlugin` uses (`auth.py:92`) — import it rather than restating the literal.

- [ ] **Step 2: Move the token off channel credentials**

Store `acting_user_token` on the client as `self._acting_user_token`, remove the `_ActingUserAuthPlugin` composition at `core.py:722`, and narrow the guard at `:713` from `if credentials is not None or acting_user_token is not None:` to `if credentials is not None:`. Removing the plugin breaks no other consumer (PA10) and leaves `_BearerTokenAuthPlugin` untouched.

Drop the now-unused `_ActingUserAuthPlugin` from the import at `:18`. Leave the class in `auth.py`: deleting it is not required by the spec's outcome, and `auth.py` is not otherwise in scope.

- [ ] **Step 3: Give the coordinator its optional token and `with_acting_user`**

```python
    def __init__(
        self,
        entity_class: type,
        channel: grpc.Channel,
        acting_user_token: str | None = None,
    ) -> None:
```

The third parameter must stay optional: four existing constructions pass two arguments (`tests/test_entity_coordinator.py:45,52,76,205`). Thread it from the client's factory at `core.py:756`:

```python
        return EntityCoordinator(entity_class, self._channel, self._acting_user_token)
```

Then the bound view and the resolver, mirroring Step 1's helper:

```python
    def with_acting_user(self, token: str) -> "EntityCoordinator[T]":
        """Return a coordinator bound to ``token``, leaving this one untouched."""
        bound = copy.copy(self)
        bound._acting_user_token = token
        return bound

    def _acting_user_metadata(self) -> tuple[tuple[str, str], ...]:
        if not self._acting_user_token:
            return ()
        return ((ACTING_USER_METADATA_KEY, f"Bearer {self._acting_user_token}"),)
```

`copy.copy` is a shallow copy, so the bound coordinator shares the receiver's four stub references — which is what makes the test convention work, since those tests overwrite `_mapping`/`_search` after construction and a rebuilt clone would not carry the mocks (PA22). The receiver is never written, so non-mutation is structural. Add `import copy` to `core.py`'s imports.

Add the four resolution-rule tests to `tests/test_entity_coordinator.py`, following its `MagicMock()`-on-the-private-stub convention (`:39-46`) and asserting on the `metadata=` keyword the stub actually received.

- [ ] **Step 4: Thread `metadata=` through the 14 coordinator stub call sites**

Fourteen, not ten (PA11): `core.py:503,517` (persistence `Post`/`Update`), `:529,542,558,571` (mapping `Delete`/`Get`/`Post`/`Update`), `:584,598` (retrieval `Get`/`GetMany`), `:617,626,631,637,642,648` (search `Search`/`SearchSimilar`/`SearchChunks`/`GroupBy`/`Aggregate`/`Pipeline`). Each gains `metadata=self._acting_user_metadata()`.

Verify mechanically rather than by count: no `self._mapping.`/`self._persistence.`/`self._retrieval.`/`self._search.` stub invocation may be left without a `metadata=` argument.

- [ ] **Step 5: Add the single-header test**

In `tests/test_auth.py`, assert that a **coordinator** call emits exactly one `x-acting-user-authorization` entry. Step 1 established the count on `get_schema`; this is the permanent regression guard on the coordinator path that Step 4 threads, where a re-added plugin would again produce two.

- [ ] **Step 6: Run tests**

```bash
cd Iverson.Clients/Python && python3 -m pytest -q
```

`python3`, not `python` — the latter is not on PATH here (PA18).

- [ ] **Step 7: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Re-add the `_ActingUserAuthPlugin` composition at `core.py:722` | the Step 5 single-header test |
| `get_schema` drops its `metadata=` argument | the Step 1 `get_schema` identity test |
| `_acting_user_metadata` returns `()` unconditionally | `the client's ambient identity applies when nothing is bound` |
| `with_acting_user` returns `self` | the non-mutation test |

- [ ] **Step 8: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/core.py \
        Iverson.Clients/Python/tests/test_auth.py \
        Iverson.Clients/Python/tests/test_entity_coordinator.py
git commit -m "carry the Python acting-user identity per call instead of on the channel"
```

---

### Task 4: Java identity resolution and sample

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java:78,94,111,124,145,208,319,331`
- Modify: `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java:81,99,103-104`
- Test (create): `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorIdentityResolutionTest.java`

- [ ] **Step 1: Write the four resolution-rule tests (they fail — `withActingUser` does not exist)**

New file, following `EntityCoordinatorTest`'s `@Mock` + `MockitoExtension` convention. Declare any `withOption` stub `lenient()`, exactly as `EntityCoordinatorTest.java:57-58` does and for the same reason: `MockitoExtension` defaults to `STRICT_STUBS` and the rule-4 case never exercises it.

Assert on the call option actually applied — `verify(stub).withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), eq("bound"))` for the bound case, and `verify(stub, never()).withOption(any(), any())` for the rule-4 case, mirroring `EntityCoordinatorTest.java:99`.

The four cases must cover more than the search stub: assert at least one per newly-routed family — persistence (`persist`), retrieval (`get`), and `delete`'s mapping path — or the five methods Step 4 fixes ship with no coverage.

- [ ] **Step 2: Add the ambient identity to `IversonClient`**

A package-private field named `actingUserToken` plus a constructor overload carrying it. The four existing public constructors (`:43,51,63,73`) keep working unchanged, and the package-private test seam at `:85` is untouched.

- [ ] **Step 3: Add the copy path and `withActingUser`**

The clone must tolerate a null `client`: the package-private constructor at `:55` sets `this.client = null` and is used by `EntityCoordinatorTest.java:68`, so a copy that dereferences `client` would break the existing search tests. Add a private constructor that carries `client`, `searchStub`, `entityType` and the bound token — held as `boundActingUserToken`, the name Step 4's helper reads — verbatim; `client` may be null; and have `withActingUser(token)` call it.

- [ ] **Step 4: Make identity resolution reach every stub family**

Identity attaches per *stub family* in Java, not per method (PA23), and the coordinator reaches its four stubs by four routes — so covering only `stubFor` and `mappingStubFor` would leave `persist`, `update`, `get`, `getMany` and `delete` with no identity at all. Because `withOption` is declared on `AbstractStub`, one generic helper covers every family:

```java
    /**
     * Attaches the resolved acting-user identity to {@code stub} as a call option (consumed by
     * {@link OAuth2ClientCredentials}). Resolution order: the caller's explicit token, then this
     * coordinator's bound identity, then the client's ambient one; none attaches nothing.
     */
    private <S extends AbstractStub<S>> S withIdentity(S stub, String explicitToken) {
        String token = explicitToken != null ? explicitToken
            : boundActingUserToken != null ? boundActingUserToken
            : (client != null ? client.actingUserToken : null);
        return token != null ? stub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, token) : stub;
    }
```

`EntityCoordinator.java` needs `import io.grpc.stub.AbstractStub;` for the helper's generic bound (PA24) — the existing `withOption` calls compile without it only because the concrete stubs inherit the method.

`stubFor` (`:319`) and `mappingStubFor` (`:331`) become one-line delegations to it, so their existing call sites are untouched. The null-client guard lives here once rather than per family. Then route the five bare-stub calls through it: `:78` and `:94` (`client.persistenceStub`), `:111` and `:124` (`client.retrievalStub`), and `:145` (`client.mappingStub`, which bypasses `mappingStubFor` today). The five existing token overloads keep working as the explicit per-call form.

- [ ] **Step 5: Route `search` through `stubFor`**

`:208` calls `searchStub.search(request)` directly, which is why the sample's search returns empty even with a valid identity. Change it to `stubFor(null).search(request)` so it picks up bound-or-ambient identity. `null` here means "no explicit per-call token", which is exactly what the token-less `search(QueryBuilder)` overload has.

- [ ] **Step 6: Switch the sample to ambient**

Pass `actingUserToken` into the `IversonClient` construction, then drop the trailing token argument from the three per-call sites (PA13): `:81` `postMapped(author, actingUserToken)`, `:99` `postMapped(article, actingUserToken)`, `:103-104` `getMapped(…, 1, actingUserToken)`. The `search` at `:120` needs no edit — Step 5 is what makes it carry identity.

- [ ] **Step 7: Run tests**

```bash
mvn -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 8: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| Route `:208` back to the bare `searchStub.search(request)` | a search-path assertion in the bound-identity test |
| `withActingUser` mutates and returns `this` | the non-mutation test |
| Both helpers consult only the explicit parameter, ignoring bound and ambient | `the ambient identity applies when nothing is bound` |
| Both helpers attach an identity even when none is configured | the rule-4 `verify(stub, never()).withOption(...)` test |
| Revert any one stub family to its bare stub (e.g. `client.persistenceStub.post` instead of `withIdentity(...)`) | that family's assertion in the resolution-rule test |

- [ ] **Step 9: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java \
        Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorIdentityResolutionTest.java \
        Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
git commit -m "resolve the acting-user identity for every Java coordinator operation"
```

---

### Task 5: .NET identity resolution and sample

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:75`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs`
- Test (create): `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`

**Interfaces:**
- Produces and consumes `ActingUserIdentity` entirely within this task; no other task references it.

- [ ] **Step 1: Add `ActingUserIdentity` and register it**

```csharp
namespace Iverson.Client.Core;

/// <summary>
/// The ambient acting-user identity, if one was configured on the client. A container-resolvable
/// type rather than a bare <c>Func&lt;Task&lt;string&gt;&gt;?</c>, because <c>EntityCoordinator&lt;T&gt;</c>
/// is registered open-generic and activated by reflection.
/// </summary>
public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null)
{
    public Func<Task<string>>? TokenProvider { get; } = tokenProvider;
}
```

Register it beside the coordinator in `ServiceCollectionExtensions.cs`, using the `actingUserTokenProvider` parameter already in that closure (`:33`):

```csharp
        services.AddSingleton(new ActingUserIdentity(actingUserTokenProvider));
```

`SchemaCatalogClient` is deliberately left alone (spec A27) — it already applies the provider itself and has its own tests constructing it with the raw delegate.

- [ ] **Step 2: Write the six tests (they fail — `WithActingUser` does not exist)**

Create `EntityCoordinatorIdentityResolutionTests.cs`, following `EntityCoordinatorMappedWriteTests.cs`'s conventions: `Substitute.For<…Client>()`, a hand-built `AsyncUnaryCall<T>`, `Arg.Do<Metadata>(h => captured = h)` to capture the emitted headers, and `TestCoordinatorFactory.Create<T>(...)`.

The four rule cases, asserting on `captured`:
- bound identity wins over ambient,
- ambient applies when nothing is bound,
- nothing configured anywhere emits no `x-acting-user-authorization` entry,
- `WithActingUser` does not mutate the receiver.

Plus the two merge cases the spec requires of .NET only:
- a supplied `Metadata` already carrying `x-acting-user-authorization` passes through untouched, even when an ambient identity exists,
- a supplied `Metadata` carrying only unrelated headers receives the resolved identity **in addition to** those headers.

`TestCoordinatorFactory.Create` needs no signature change (PA15) — see Step 3.

- [ ] **Step 3: Add the 8th constructor parameter, the resolver, and `WithActingUser`**

The parameter must carry a default, or `TestCoordinatorFactory.cs:30-38`'s seven-argument construction stops compiling (PA15):

```csharp
    ILogger<EntityCoordinator<T>> logger,
    ActingUserIdentity? identity = null)
```

Then the resolver and the bound copy. Primary-constructor parameters are in scope in the class body (PA14), so the copy can pass them straight through:

```csharp
    // Deliberately not `readonly`: WithActingUser assigns it through an object initializer on a
    // freshly constructed instance, which the compiler rejects on a readonly field. Nothing else
    // ever writes it, so the instance is still effectively immutable after construction.
    private Func<Task<string>>? _boundActingUser;

    /// <summary>
    /// Returns a coordinator bound to <paramref name="tokenProvider"/>, leaving this one
    /// untouched. The bound identity outranks the client's ambient one; explicit headers
    /// carrying an acting-user entry outrank both.
    /// </summary>
    public EntityCoordinator<T> WithActingUser(Func<Task<string>> tokenProvider) =>
        new(registry, assembler, mapping, persistence, retrieval, search, logger, identity)
        {
            _boundActingUser = tokenProvider,
        };

    private async Task<Metadata> ResolveHeadersAsync(Metadata? headers)
    {
        headers ??= new Metadata();
        if (headers.Get(ActingUserMetadata.MetadataKey) is not null)
            return headers;

        var provider = _boundActingUser ?? identity?.TokenProvider;
        if (provider is not null)
            headers.WithActingUser(await provider());

        return headers;
    }
```

The copy is what carries the bound token, so the receiver is never mutated. `ActingUserMetadata.MetadataKey` and the `WithActingUser` extension both already exist (`ActingUserMetadata.cs:7,9`).

- [ ] **Step 4: Apply the resolver at the 9 header-less call sites**

15 stub call sites total; 6 already pass headers (`:45,61,77,112,299,319`). The remaining 9 span 8 methods — `PipelineAsync` calls the stub twice (`:255,275`) — and each becomes `await ResolveHeadersAsync(null)` passed as the stub call's headers argument (PA16).

The 6 that already pass a caller-supplied `headers` change from `headers` to `await ResolveHeadersAsync(headers)`, which is what makes the merge semantic apply and what keeps the load test's explicit headers winning.

Four of the 9 sites are inside `async IAsyncEnumerable` iterator methods; awaiting there is legal and those methods already await internally (spec A28).

- [ ] **Step 5: Delete the sample's `headers` plumbing**

Every mapped call in `Program.cs` currently passes `headers`; with the coordinator resolving ambient identity from DI, all 16 arguments and the `headers` variable itself go. The sample already supplies `actingUserTokenProvider` (spec A5), so nothing replaces them. Its eight non-mapped calls — `GetAsync`, `GetManyAsync`, `SearchAsync`, `DeleteAsync` — now carry identity for the first time.

Verify mechanically: no `GetMappedAsync`/`PostMappedAsync`/`UpdateMappedAsync` call in the file may be left with a `headers` argument, and `headers` must not survive as an unused local.

- [ ] **Step 6: Build the sample and run the client tests**

```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Sample/Iverson.Client.Sample.csproj
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
```

Neither command exercises a real write or read against a server, and the sample has no test — so "the eight non-mapped calls now carry identity" is verified by compilation and by the resolver's own unit tests, not end-to-end. That gap is inherent to the samples having no coverage and is not closed by this task.

- [ ] **Step 7: Mutation-test**

| Mutation | Must be killed by |
|---|---|
| `ResolveHeadersAsync` returns `headers` unchanged | `the ambient identity applies when nothing is bound` |
| Drop the `headers.Get(...) is not null` early return | the explicit-identity-wins merge test |
| Replace the early return with `new Metadata()`, discarding supplied headers | the unrelated-headers merge test |
| Resolve `identity?.TokenProvider ?? _boundActingUser` (precedence inverted) | `bound identity wins over ambient` |
| `WithActingUser` assigns to the receiver and returns `this` | the non-mutation test |

- [ ] **Step 8: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs \
        Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
git commit -m "resolve the acting-user identity for every .NET coordinator operation"
```

---

## Tasks NOT in this plan

Inherited from the spec's "Out of scope" section. A new spec → new plan cycle is required to add any of these.

- **The server.** No change to how identity is read or authorization evaluated.
- **The write and mapped-CRUD contracts** established by `server-generated-ids`.
- **Client-side token validation** of any kind.
- **Go and Python samples.** Neither writes; both are schema-inspection and query-builder demos.
- **The conformance harness.** The `client-conformance-harness` branch consumes all five clients, and Python's identity mechanism moving out of channel credentials is a breaking change for whatever it does today. Those two branches need reconciling whichever lands second. Not examined here; pulling it into scope would widen this well past the finding.

## Known issues inherited from spec

- **Go's per-call override is spelled differently** from the other four: `WithActingUserToken(ctx, token)` rather than a bound coordinator. Accepted by Ben as the idiomatic Go form — the resolution rule is identical, only the spelling differs.
- **`GetAsync` and `GetMappedAsync` still log and return `null`** on an unauthorized read rather than surfacing the denial, so an authorization failure remains indistinguishable from an empty result at the call site. This design makes the reads *authorized*; it does not change .NET's log-and-return-null error convention, which the `server-generated-ids` Global Constraints deliberately preserve. A caller that wants to distinguish the two cases still cannot.
