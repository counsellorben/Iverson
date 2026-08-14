# Critical Implementation Review: 2026-08-12-acting-user-identity-parity-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson-serverids/docs/plans/2026-08-12-acting-user-identity-parity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA `dc490215b1c0c38db18270a8539ff8d57cf62785`); cited file:line references re-checked under §1. All three are the plan's own writing and revision (`1051f48`, `d4bc82b`) plus the round-1 review; no client code moved.

## 0. Coverage enumeration

Re-derived independently of round 1. Round 1 concentrated on Task 3, so Tasks 1, 2, 4 and 5 got the deeper read this round.

**Task 1 — TypeScript**

| Surface | Disposition |
|---|---|
| Step prose (the "8 sites" instruction) | `ok` — enumerated the coordinator's own methods and their identity args: `persist`, `update`, `delete`, `getMapped`, `postMapped`, `updateMapped`, `get`, `getMany` each pass `this._client._actingUserToken` exactly once. 8 methods, 8 sites, no method left out |
| Step prose (class-boundary question the count raises) | `ok` — checked whether the search family is a coordinator method that the 8 sites miss: it is not. `EntityCoordinator` spans `core.ts:503-696`; `getSchema`, `searchChunks`, `groupBy`, `aggregate`, `pipeline` are `IversonClient` methods (`:703-843`) reading the client's own `_actingUserToken` (`:709,722`). They are ambient by construction and outside the coordinator the plan changes |
| Code blocks | `ok` — `withActingUser` writes `bound._boundActingUser` on another instance of the same class, which TypeScript permits; `_cls` and `_client` are `private readonly` ctor params (`:511-512`) so both are in scope; the clone re-reads stubs from `_client` (`:520-522`) so `makeClientLike` mocks survive |
| Commands | `ok` — `npm test` verified this session (176 passed, includes the `tsc` pass over `sample/`) |
| Wiring text | `ok` — the instruction to leave `_client._callCredentials` alone is right: those are the service's own credentials, and `callUnary` takes them as a separate parameter from the acting-user token (`:162-163`) |

**Task 2 — Go**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — checked the coverage question that sank Java: Go's fallback lives in `GetRequestMetadata` on the credentials object, which grpc-go invokes for **every** call on the channel, so all 14 coordinator methods inherit it with no per-method work |
| Code blocks | `ok` — the `else if` replaces `auth.go:52-54` exactly; `ActingUserMetadataKey` (`:18`) and `actingUserTokenKey{}` (`:14`) resolve; the only two construction sites are field-named literals (`auth_test.go:19,38`) so a new field breaks neither |
| Commands | `ok` — `go test ./... && go vet ./...` verified clean this session |
| Wiring text | `ok` — `getToken` runs before the acting-user branch and errors out without a token endpoint, so all three new tests need the `httptest` server `auth_test.go:11` already stands up; the plan says to reuse it |

**Task 3 — Python**

| Surface | Disposition |
|---|---|
| Step prose (step ordering / indivisibility) | `ok` — the current text makes Steps 1-2 indivisible and explains why a presence-only assertion proves nothing; the count assertion is what forces it |
| Code blocks (`with_acting_user`) | `ok` — `copy.copy(self)` shares the receiver's four stub references, which is what the post-construction mock injection at `tests/test_entity_coordinator.py:44-46,52-53` requires; the receiver is never written |
| Code blocks (`get_schema`, `_acting_user_metadata`, client factory) | `ok` — re-checked the surviving `self._channel` reference at plan line 269: that is `IversonClient.coordinator`, and the **client** does store `_channel` (`core.py:736-740`), so it is correct. `ACTING_USER_METADATA_KEY` exists (`auth.py`, used at `:92`); stubs are `channel.unary_unary` multicallables (`generated/object_mapping_pb2_grpc.py:64-68`) so `metadata=` is accepted |
| Commands | `ok` — `python3 -m pytest -q` verified (180 passed) |
| Wiring text | `ok` — 14 stub call sites re-counted at the cited lines; `_ActingUserAuthPlugin` still has exactly three references repo-wide, so removing the composition is safe and `_BearerTokenAuthPlugin` survives independently (`core.py:715-719`) |

**Task 4 — Java**

| Surface | Disposition |
|---|---|
| Step prose (Step 4, "both existing identity helpers") | `→ §2.1` |
| Step prose (Step 3, null-client copy path) | `ok` — the package-private ctor sets `this.client = null` (`:61`) and is used at `EntityCoordinatorTest.java:68`; a copy carrying `client`, `searchStub`, `entityType` and the token handles it, and `typeName` derives from `entityType` |
| Step prose (Step 5, `search` routing) | `ok` — `:208` genuinely calls `searchStub.search(request)` directly, and `stubFor(null)` is unambiguous on a single-parameter method |
| Code blocks | `ok` — no literal blocks beyond prose-described shapes; every identifier resolves: `stubFor` (`:319`), `mappingStubFor` (`:331`), `OAuth2ClientCredentials.ACTING_USER_TOKEN`, `searchStub` (`:39`), the four package-private client stubs (`IversonClient.java:35-38`) |
| Commands | `ok` — `mvn -f Iverson.Clients/Java/pom.xml test` verified (170 passed, BUILD SUCCESS) |
| Wiring text (sample) | `ok` — the three per-call token sites match `Main.java:81,99,103-104`, and `:120`'s `search` is reached by Step 5 rather than needing its own edit |

**Task 5 — .NET**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — asked the Java question of .NET and it comes out clean: the plan changes **all 15** stub call sites, not a subset, so no coordinator method is left without a resolution path. Headers already at `:45,61,77,112,299,319`; the 9 remaining span 8 methods because `PipelineAsync` calls its stub twice (`:255,275`) |
| Code blocks | `ok` — `ActingUserIdentity` is a new symbol with no existing collision; `ActingUserMetadata.MetadataKey` and the `WithActingUser` extension exist (`ActingUserMetadata.cs:7,9`); primary-ctor params are in scope in the body (`:25` uses `registry`, `:36` uses `logger`) so the sibling construction is legal; the field is correctly non-`readonly` for the object-initializer write |
| Commands | `ok` — `dotnet build …Sample.csproj` and `dotnet test …Core.Tests.csproj` both verified this session (0 errors; 49 passed) |
| Wiring text | `ok` — the defaulted 8th parameter keeps `TestCoordinatorFactory.cs:30-38`'s seven-argument call compiling; the streaming sites' `(request, headers, cancellationToken: ct)` shape matches what `:299` already does |

**Cross-task interface contracts**

| Contract | Disposition |
|---|---|
| Task 5's `ActingUserIdentity` (the only declared Produces/Consumes) | `ok` — created in Step 1, consumed in Step 3, same task; no other task names the symbol |
| Task 1-5 file-set disjointness (the no-ordering claim) | `ok` — each task's paths sit under a distinct client directory; no task reads an artifact another writes, and no persistence boundary is crossed anywhere in this plan |
| Task 3 Step 1 → Step 4 handoff of `_acting_user_metadata` | `ok` — Step 1 defines the helper on the client and Step 3 defines the coordinator's; Step 4's 14 sites call the coordinator's, which Step 3 introduces before Step 4 runs |

**Rule-like content — the resolution rule, both failure directions**

| Row | Disposition |
|---|---|
| Under-inclusion: a coordinator method that reaches no resolution path at all | `→ §2.1` — this is the direction that fails, and it fails only in Java |
| Over-inclusion: identity attached when nothing is configured (rule-4) | `ok` — each language guards on emptiness (`!= ""`, `if not self._acting_user_token`, `provider is not null`, `?? undefined`) and each task carries an explicit rule-4 absence test |
| Precedence mechanics (bound before ambient) — mechanics, not a calibrated value | `ok` — all five resolvers order bound first, and every task's mutation table includes an inverted-precedence mutation killed by the bound-wins test |
| Identity/exclusion: does a bound clone alias the receiver's state? | `ok` — .NET copies the seven deps and sets the token on the new instance; TypeScript re-reads from the shared client; Python shallow-copies (so stubs are shared, state is not); Java's copy carries fields explicitly; Go has no clone |
| Producers of "an acting-user identity" — one row per producer | `ok` — all five enumerated: `ActingUserMetadata.WithActingUser` (.NET), the `ACTING_USER_TOKEN` call option (Java), `WithActingUserToken` ctx value (Go), `_ActingUserAuthPlugin` (Python, relocated by Task 3), `resolveActingUserMetadata` (TS). Each belongs to a task |
| Consumers of the identity **per stub family** — one row per family, since a family is what a helper covers | `→ §2.1` — Java has four stub families and the plan's helpers cover two |
| Rule 1 (per-call override) unreachable in TypeScript and Python | `dropped` — round 1 settled this; the spec scopes those two clients to bound-plus-ambient. Not re-raised |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all 22 cited references: **PA1–PA22 all still hold.** PA22 (added after round 1) reconfirmed at `core.py:478-490` — the constructor assigns seven attributes and no `self._channel` — and at `tests/test_entity_coordinator.py:44-46,52-53`, where mocks are written onto `_search`/`_mapping` after construction.

**Span check — one uncovered dependency:**

- **Task 4 depends on which stub each Java coordinator method invokes, and no assumption covers that mapping.** PA12 covers the *visibility* of `IversonClient`'s stub fields; the spec's A9 covers where `withOption` currently appears. Neither states which of the four stubs each of the fourteen coordinator methods actually calls — and that mapping is exactly what determines whether the plan's two-helper approach reaches every method. Verified in-round and it does not. Routed to §2.1.

No other uncovered dependency: every other fact the tasks rest on is covered by a PA row or a spec assumption as scoped.

## 2. Literal-wrongness findings

### §2.1 — Task 4 leaves five Java coordinator methods with no acting-user identity, including four the spec names as broken

**Description.** Design §3 states that `stubFor` and `mappingStubFor` "are the only places identity attaches… so the resolution rule has exactly two implementation sites," and the plan's Task 4 Step 4 builds on exactly that: make those two helpers resolve bound → ambient, and route `search:208` through `stubFor`. The sentence is true as a description of where identity attaches *today* — and that is precisely why it is the wrong basis for the work. Five coordinator methods never touch either helper, because they invoke two other stubs directly:

- `persist` → `client.persistenceStub.post(request)`
- `update` → `client.persistenceStub.update(request)`
- `get` → `client.retrievalStub.get(request)`
- `getMany` → `client.retrievalStub.getMany(request)`
- `delete` → `client.mappingStub.delete(request)` — the bare field, not `mappingStubFor`

After Task 4 as written, Java's mapped trio and search family carry identity and those five do not. Four of them — `get`, `getMany`, `delete`, `persist`, `update` — are named in the spec's own Problem section as the methods that "have none," alongside `search`. The plan fixes `search` and leaves the other five, so the spec's stated outcome (every coordinator operation can carry an identity) is not delivered for Java. Concretely: after this plan ships, a Java caller with a valid bound or ambient identity still gets `PermissionDenied` on `persist`/`update`/`delete` and an empty result on `get`/`getMany`, which is the same silent-read failure the whole change exists to remove.

This is also why Task 4's test list would not catch it. The four resolution-rule tests assert on the *search* stub's `withOption`, so they pass while the persistence and retrieval stubs remain unrouted.

**Evidence.**
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java:78` — `client.persistenceStub.post(request)`; `:94` — `client.persistenceStub.update(request)`.
- `:111` — `client.retrievalStub.get(request)`; `:124` — `client.retrievalStub.getMany(request)`.
- `:145` — `client.mappingStub.delete(request)`, bypassing `mappingStubFor` (`:331`) which sits in the same file.
- `:319,331` — the two helpers the plan's Step 4 covers, wrapping `searchStub` and `client.mappingStub` respectively.
- Spec Problem section — "In Java, 5 search-family methods have token overloads while `get`, `getMany`, `delete`, `persist`, `update` and `search` have none."
- Plan Task 4 File Structure — cites `EntityCoordinator.java:208,319,329` only; `:78`, `:94`, `:111`, `:124` and `:145` appear nowhere in the task.

**Proposed fix.** Extend Task 4 Step 4 from two helpers to all four stub families, and add the five call sites to the task's file list. Because `withOption` is declared on `AbstractStub`, one generic helper covers every family rather than writing four near-identical ones:

```java
    /**
     * Attaches the resolved acting-user identity to {@code stub} as a call option (consumed by
     * {@link OAuth2ClientCredentials}). Resolution order: the caller's explicit token, then this
     * coordinator's bound identity, then the client's ambient one; none attaches nothing.
     */
    private <S extends AbstractStub<S>> S withIdentity(S stub, String explicitToken) {
        String token = explicitToken != null ? explicitToken
            : boundActingUserToken != null ? boundActingUserToken
            : (client != null ? client.actingUserToken() : null);
        return token != null ? stub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, token) : stub;
    }
```

`stubFor` and `mappingStubFor` then become one-line delegations to it, preserving their existing call sites, and the five bare-stub calls become `withIdentity(client.persistenceStub, null).post(request)` and so on. The null-client guard the plan already requires for the ambient read lives in this one helper rather than being repeated per family.

Task 4's test list needs the corresponding widening: the four resolution-rule tests currently assert against the search stub only, so add at least one assertion per newly-routed stub family — persistence, retrieval, and `delete`'s mapping path — or the five fixed methods ship with no coverage. Step 8's mutation table should gain a row for reverting any one family to its bare stub.

## 3. Forced decisions

No forced decisions found. §2.1's fix has one obvious shape given `withOption`'s declaration on `AbstractStub`, and choosing one generic helper over four per-family helpers is a mechanical preference rather than a decision the codebase forces.

## 4. Previously addressed

- **Round 1 §2.1** (Python's `with_acting_user` referencing a nonexistent `self._channel`, and the rebuilt-stubs hazard) — resolved. The plan now uses `copy.copy(self)`, and the justification prose explains why sharing stub references is required by the test convention. PA22 records the underlying fact.
- **Round 1 §2.2** (Step 1 could not prove `metadata=` worked, because the plugin still supplied the header) — resolved. Step 1's test now asserts exactly one header, the preamble states Steps 1-2 are indivisible, and Step 5 was re-scoped to the coordinator path as the permanent guard.
- **Round 1 span check** (the Python channel dependency) — resolved by PA22.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions, §2 has one finding, §3 is empty. Tasks 1, 2, 3 and 5 came through this round's sweep clean.

The finding is a coverage gap rather than a broken mechanism: Java's resolution logic as planned is correct, it simply is not reached by five of the coordinator's fourteen methods. It surfaced from asking, per stub family, "which methods route through a helper the plan touches" — the same question .NET passes (all 15 call sites change), Python passes (all 14 plus `get_schema`), Go passes structurally (credentials-level, so every call inherits it), and TypeScript passes (8 sites for 8 coordinator methods, with the search family living on the client instead). Java is the only client where identity attaches per stub family rather than per call, which is what let a two-helper plan look complete.
