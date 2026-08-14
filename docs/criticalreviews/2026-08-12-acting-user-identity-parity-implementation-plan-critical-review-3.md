# Critical Implementation Review: 2026-08-12-acting-user-identity-parity-implementation-plan (Round 3)

**Plan:** /home/ben/repositories/Iverson-serverids/docs/plans/2026-08-12-acting-user-identity-parity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 4 commits since plan-write time (SHA `dc490215b1c0c38db18270a8539ff8d57cf62785`); cited file:line references re-checked under §1. All four are the plan's own writing and its two revision rounds; no client code moved.

## 0. Coverage enumeration

Re-derived independently. Rounds 1 and 2 landed on Task 3 and Task 4 respectively, so this round gave Tasks 1, 2 and 5 a full pass and treated the round-2 fix as a single new surface to check at identifier level.

**Task 1 — TypeScript**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — the 8-site instruction matches the coordinator's 8 methods one-for-one (`persist`, `update`, `delete`, `getMapped`, `postMapped`, `updateMapped`, `get`, `getMany`), and the search family genuinely lives on `IversonClient` (`core.ts:703-843`), outside what Task 1 touches |
| Code blocks | `ok` — identifier-level check: `ActingUserToken` (`core.ts:135`), `_cls`/`_client` as `private readonly` ctor params (`:511-512`), `_boundActingUser` newly introduced and used only within the block. Types line up: `withActingUser(token: ActingUserToken)` feeds a field of the same type, which `callUnary`'s 4th parameter accepts (`:163`) |
| Commands | `ok` — `npm test` verified this session (176 passed) |
| Wiring text | `ok` — leaving `_callCredentials` alone is correct; `callUnary` takes credentials and acting-user token as separate parameters (`:162-163`) |

**Task 2 — Go**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — credentials-level fallback reaches every method without per-method work, since grpc-go calls `GetRequestMetadata` for every RPC on the channel |
| Code blocks | `ok` — identifier-level: `ActingUserMetadataKey` (`auth.go:18`), `actingUserTokenKey{}` (`:14`), `DefaultActingUserToken` newly introduced; the `else if` slots into `:52-54` without touching the client-credentials half above it |
| Commands | `ok` — `go test ./... && go vet ./...` verified clean this session |
| Wiring text | `ok` — all three new tests need the token endpoint because `getToken` runs first and errors out; the plan says to reuse `auth_test.go:11`'s `httptest` server |

**Task 3 — Python**

| Surface | Disposition |
|---|---|
| Step prose (ordering, indivisibility) | `ok` — Steps 1-2 are stated as one change, with the reason a presence-only assertion proves nothing |
| Code blocks (`with_acting_user`, `_acting_user_metadata`) | `ok` — `copy.copy` shares the four stub references the post-construction mock injection requires (`tests/test_entity_coordinator.py:44-46,52-53`); `ACTING_USER_METADATA_KEY` exists in `auth.py` (used at `:92`) |
| Code blocks (`__init__` signature fragment) | `dropped` — the block shows the signature and stops at `-> None:`, and `_acting_user_metadata` reads `self._acting_user_token`, which nothing in the plan explicitly assigns. But the block is a signature fragment by construction rather than a full body, and adding a constructor parameter implies storing it. Omitting an obvious body line is elision, not a stated-wrong instruction — unlike §2.1, where the plan states a member that its own producing step does not create |
| Commands | `ok` — `python3 -m pytest -q` verified (180 passed); `python` genuinely absent |
| Wiring text | `ok` — 14 call sites re-counted at the cited lines; the surviving `self._channel` at plan line 269 is the **client's** factory and the client does store `_channel` (`core.py:736-740`) |

**Task 4 — Java**

| Surface | Disposition |
|---|---|
| Step 4 code block (new this round) — `client.actingUserToken()` | `→ §2.1` |
| Step 4 code block (new this round) — `AbstractStub` | `→ §2.1` (second defect in the same block; found by finishing the block's identifiers rather than stopping at the first) |
| Step 4 code block — remaining identifiers | `ok` — `OAuth2ClientCredentials.ACTING_USER_TOKEN` exists and is already used at `:321,333`; `withOption` is inherited from `AbstractStub` via `AbstractBlockingStub`, proven by the existing calls; the generic bound `<S extends AbstractStub<S>>` is satisfiable because each generated blocking stub extends `AbstractBlockingStub<Self>` |
| Step 4 prose (the five bare-stub sites) | `ok` — re-verified each: `:78,94` persistence, `:111,124` retrieval, `:145` bare mapping. PA23 now records the mapping |
| Step 2 / Step 3 prose | `→ §2.1` for the member-kind contract; otherwise `ok` — the null-client hazard at `:61` and its use at `EntityCoordinatorTest.java:68` are correctly described |
| Step 5 prose | `ok` — `:208` calls `searchStub.search(request)` directly; `stubFor(null)` is unambiguous |
| Commands | `ok` — `mvn -f …/pom.xml test` verified (170 passed) |
| Wiring text (sample) | `ok` — three token sites at `Main.java:81,99,103-104`; `:120`'s `search` reached via Step 5 |

**Task 5 — .NET**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — all 15 call sites change, so no method is left unrouted; the 9/8 split is explained by `PipelineAsync`'s two stub calls (`:255,275`) |
| Code blocks | `ok` — identifier-level: `ActingUserIdentity.TokenProvider` is defined in Step 1 and consumed in Step 3 of the same task, names matching; `ActingUserMetadata.MetadataKey` and the `WithActingUser` extension exist (`ActingUserMetadata.cs:7,9`); `Metadata.Get` returns a nullable entry as the `is not null` test requires; primary-ctor params are in scope for the sibling construction (`:25,36`); the field is non-`readonly`, which the object-initializer write requires |
| Commands | `ok` — sample build and `dotnet test` both verified this session (0 errors; 49 passed) |
| Wiring text | `ok` — the defaulted 8th parameter keeps `TestCoordinatorFactory.cs:30-38` compiling; streaming sites match `:299`'s existing `(request, headers, cancellationToken: ct)` shape |

**Cross-task and cross-step contracts**

| Contract | Disposition |
|---|---|
| Java Step 2 (produces the client's ambient identity) → Step 4 (consumes it) | `→ §2.1` — the producing step makes a field, the consuming code calls a method |
| Java Step 3 (produces the bound-token field) → Step 4 (consumes `boundActingUserToken`) | `ok` — Step 3 does not name the field, but Step 4 does, and nothing contradicts it; underspecification an implementer resolves by reading both, not a conflict. Folded into §2.1's fix as a naming note rather than raised separately |
| Task 5's `ActingUserIdentity` Produces/Consumes | `ok` — created Step 1, consumed Step 3, same task, names match |
| Task 1-5 file-set disjointness | `ok` — distinct client directories; no shared artifact, no persistence boundary anywhere in this plan |
| Python Step 1 (client helper) → Step 3 (coordinator helper) → Step 4 (14 sites) | `ok` — Step 3 defines the coordinator's `_acting_user_metadata` before Step 4's sites call it |

**Rule-like content — both failure directions**

| Row | Disposition |
|---|---|
| Under-inclusion: a coordinator method reaching no resolution path | `ok` this round — Java's gap was round 2's finding and the plan now routes all four stub families; .NET covers 15/15, Python 14 plus `get_schema`, Go structurally, TypeScript 8/8 |
| Over-inclusion: identity attached when nothing configured (rule 4) | `ok` — every resolver guards on emptiness, and the new Java helper returns `stub` unmodified when `token == null`, which is the rule-4 path for all four families at once |
| Precedence mechanics (explicit → bound → ambient) | `ok` — the Java helper encodes the order in one ternary chain; .NET, Python and TypeScript order bound before ambient; each task has an inverted-precedence mutation |
| Identity/exclusion: bound clone aliasing the receiver | `ok` — .NET copies deps and sets the token on the new instance; TypeScript re-reads from the shared client; Python shallow-copies; Java's private ctor carries fields explicitly; Go has no clone |
| Producers of the identity header — one row per producer | `ok` — five enumerated, each owned by a task; Python's is the only one relocated |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all 23 cited references: **PA1–PA23 all still hold.** PA23 (added after round 2) reconfirmed at every cited line — `EntityCoordinator.java:78,94` (`client.persistenceStub`), `:111,124` (`client.retrievalStub`), `:145` (bare `client.mappingStub`), `:208` (bare `searchStub`), against the helpers at `:319` and `:331`.

**Span check — one uncovered dependency:**

- **Task 4 Step 4's helper depends on `io.grpc.stub.AbstractStub` being importable into `EntityCoordinator.java`, and no assumption covers the file's import set.** PA20's sibling sweep enumerates identifiers the plan *names* and where they resolve, but it predates the round-2 code block and does not cover its imports. Verified in-round: the file imports only `io.grpc.StatusRuntimeException` from the grpc namespace (`EntityCoordinator.java:3`), so the type is not currently in scope. Routed to §2.1.

No other uncovered dependency.

## 2. Literal-wrongness findings

### §2.1 — Task 4's `withIdentity` code block does not compile as written: it calls a method the plan's own Step 2 does not create, and uses a type the file does not import

**Description.** Two independent defects in the block round 2 added.

**First**, the resolution chain reads the client's ambient identity as `client.actingUserToken()` — a method call. Step 2, which produces that identity, specifies "a package-private **field** plus a constructor overload carrying it." A field is not invocable, so the expression does not compile. This is not a stylistic preference about accessors: the coordinator reaches every other client member by direct field access (`client.persistenceStub`, `client.retrievalStub`, `client.mappingStub`, `client.searchStub`), so a field is the right shape and the *consumer* is what's wrong, not the producer.

**Second**, the helper's generic bound names `AbstractStub`, which is not imported. `EntityCoordinator.java` imports exactly one grpc type, `io.grpc.StatusRuntimeException` (`:3`). `withOption` works today only because it is inherited by the concrete generated stubs — naming the base type explicitly is new to this block and requires the import.

Either defect alone fails the Java build at Task 4, which means Step 7's `mvn test` cannot pass and the task cannot complete. The second is the more instructive one: it was sitting behind the first in the same five-line block, and is exactly the class of thing a scan that stops at its first hit does not reach.

**Evidence.**
- Plan Task 4 Step 2 — "A package-private field plus a constructor overload carrying it."
- Plan Task 4 Step 4 code block — `: (client != null ? client.actingUserToken() : null);`
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java:78,94,111,124,145` — every existing client-member access is a direct field read, no accessor methods anywhere.
- `EntityCoordinator.java:3` — the file's only `io.grpc.*` import is `StatusRuntimeException`; there is no `io.grpc.stub.AbstractStub`.
- `:321,333` — the existing `withOption` calls, which compile because `ObjectSearchServiceBlockingStub` / `ObjectMappingServiceBlockingStub` inherit it, without the base type ever being named.

**Proposed fix.** Two edits to Task 4, both mechanical:

- In the Step 4 code block, change `client.actingUserToken()` to `client.actingUserToken` — matching the field Step 2 creates and the direct-field-access convention the file already uses. While there, have Step 2 name the field `actingUserToken` explicitly and Step 3 name the coordinator's bound field `boundActingUserToken`, so the producing steps and this consuming block agree on both identifiers rather than leaving the reader to infer them.
- Add a line to Step 4 stating that `EntityCoordinator.java` needs `import io.grpc.stub.AbstractStub;` for the helper's generic bound — the same way Task 3 Step 3 already calls out `import copy` and Task 1 Step 1 calls out importing `ACTING_USER_METADATA_KEY` from `../src/auth.js`.

## 3. Forced decisions

No forced decisions found. Both defects have one correct resolution each, determined by the file's existing conventions rather than by a judgment the plan has to make.

## 4. Previously addressed

- **Round 1 §2.1** (Python's `with_acting_user` referencing a nonexistent `self._channel`, and the rebuilt-stub hazard) — resolved; `copy.copy(self)` shares the stub references the tests require, and PA22 records why.
- **Round 1 §2.2** (Step 1 could not prove `metadata=` worked while the plugin still supplied the header) — resolved; Step 1 now asserts exactly one header and the preamble states Steps 1-2 are indivisible.
- **Round 2 §2.1** (five Java methods reaching no resolution path) — resolved; Step 4 now routes all four stub families through one helper, the Files list carries the five call sites, Step 1 requires a per-family assertion, and Step 8 gained the bare-stub mutation. PA23 records the mapping.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions, §2 has one finding (two defects, one code block), §3 is empty. Tasks 1, 2, 3 and 5 came through this round clean.

Both defects live in text added by the previous round's fix, which is the pattern worth noting across three rounds now: each round's finding has been in the area the previous round most recently touched or reasoned about, and the fix for a coverage gap introduced a compile error of its own. The plan's substance is converging — the resolution rule itself, its precedence, and its per-client reach all check out this round — but new plan text has not been getting the identifier-level read that pre-existing text gets.
