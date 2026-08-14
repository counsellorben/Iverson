# Critical Implementation Review: 2026-08-12-acting-user-identity-parity-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson-serverids/docs/plans/2026-08-12-acting-user-identity-parity-implementation-plan.md
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `dc490215b1c0c38db18270a8539ff8d57cf62785`); cited file:line references re-checked under §1. The commit is the plan's own (`1051f48`), so no client code moved.

## 0. Coverage enumeration

**Task 1 — TypeScript**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — checked the "8 not 16" claim and the instruction to leave `_callCredentials` alone: `_client._actingUserToken` appears 8×, `_client._callCredentials` 8× separately, and the latter carries the service's own credentials, so excluding it is correct |
| Code blocks | `ok` — `withActingUser` constructs `new EntityCoordinator(this._cls, this._client)`; both are `private readonly` ctor params (`core.ts:511-512`) so they are in scope, and TS permits writing another instance's private field from inside the class. The clone re-reads stubs from `_client` (`core.ts:520-522`), so mocks injected via `makeClientLike` survive it |
| Commands | `ok` — `npm test` verified this session (176 passed, includes the `tsc` pass over `sample/`) |
| Wiring/integration text | `ok` — Step 3's replacement is confined to the 8 identity reads; `callUnary`'s signature (`core.ts:154-163`) already accepts the token per call, so no call-shape change is implied |

**Task 2 — Go**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — the "no `WithActingUser` for Go" rationale matches the codebase: `WithActingUserToken` is already exported at `auth.go:23` |
| Code blocks | `ok` — the `else if` replaces `auth.go:52-54` exactly; `ActingUserMetadataKey` (`:18`) and `actingUserTokenKey{}` (`:14`) both resolve. Adding a field to `OAuth2ClientCredentials` breaks no construction site: the only two are field-named literals (`auth_test.go:19,38`) |
| Commands | `ok` — `go test ./... && go vet ./...` verified clean this session |
| Wiring/integration text | `ok` — Step 1's "reuse the `httptest` server" is implementable: `GetRequestMetadata` calls `getToken` first and errors out without a token endpoint, so all three new tests need one, and `auth_test.go:11`'s test already stands one up |

**Task 3 — Python**

| Surface | Disposition |
|---|---|
| Step prose (step ordering / risk discharge) | `→ §2.2` |
| Code blocks (`with_acting_user`) | `→ §2.1` |
| Code blocks (`get_schema`, `_acting_user_metadata`) | `ok` — `ACTING_USER_METADATA_KEY` exists in `auth.py` and is what `_ActingUserAuthPlugin` uses (`auth.py:92`); the stubs are `channel.unary_unary` multicallables (`generated/object_mapping_pb2_grpc.py:64-68`) so `metadata=` is accepted; `grpcio>=1.81.1` (`pyproject.toml:11`) |
| Commands | `ok` — `python3 -m pytest -q` verified (180 passed); `python` is genuinely absent, as PA18 records |
| Wiring/integration text | `ok` — the guard narrowing at `core.py:713` and the import drop at `:18` are both consistent with `_ActingUserAuthPlugin` having exactly three references repo-wide; `_BearerTokenAuthPlugin` is composed independently (`:715-719`) so it survives |

**Task 4 — Java**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — checked the null-client hazard the plan names: the package-private ctor sets `this.client = null` (`EntityCoordinator.java:61`) and is used at `EntityCoordinatorTest.java:68`, so guarding the ambient read is genuinely required |
| Code blocks | `ok` — no literal code blocks in this task beyond prose-described shapes; the identifiers they name all resolve: `stubFor` (`:319`), `mappingStubFor` (`:329`), `OAuth2ClientCredentials.ACTING_USER_TOKEN`, `searchStub` field (`:39`), the four package-private client stubs (`IversonClient.java:35-38`) |
| Commands | `ok` — `mvn -f Iverson.Clients/Java/pom.xml test` verified (170 passed, BUILD SUCCESS) |
| Wiring/integration text | `ok` — Step 5's `stubFor(null)` is unambiguous (single-parameter method) and Step 4 makes `null` mean "no explicit per-call token", which is what the token-less `search(QueryBuilder)` overload has. Step 6's three sample edits match `Main.java:81,99,103-104`, and `:120`'s `search` needs no edit because Step 5 is what gives it identity |

**Task 5 — .NET**

| Surface | Disposition |
|---|---|
| Step prose | `ok` — the 15/6/9-across-8 arithmetic re-checked: headers already at `EntityCoordinator.cs:45,61,77,112,299,319`, and `PipelineAsync` accounts for the methods-vs-sites gap with two stub calls (`:255,275`) |
| Code blocks | `ok` — `ActingUserIdentity` is a new type with no collision (no such symbol today); `ActingUserMetadata.MetadataKey` and the `WithActingUser` extension both exist (`ActingUserMetadata.cs:7,9`); `Metadata.Get` returning a nullable entry matches the 5 existing test call sites; primary-ctor params are in scope in the body (`:25` uses `registry`, `:36` uses `logger`), so the sibling construction is legal. The non-`readonly` field plus object initializer is correct as written — a `readonly` field would have been rejected |
| Commands | `ok` — both `dotnet build …Sample.csproj` and `dotnet test …Core.Tests.csproj` verified this session (0 errors; 49 passed) |
| Wiring/integration text | `ok` — the defaulted 8th parameter keeps `TestCoordinatorFactory.cs:30-38`'s seven-argument construction compiling while DI still injects the registered singleton; the streaming sites' `(request, headers, cancellationToken: ct)` shape matches what `:299` already does |

**Cross-task interface contracts**

| Contract | Disposition |
|---|---|
| Task 5 Produces/Consumes `ActingUserIdentity` (only declared contract) | `ok` — created in Step 1 and consumed in Step 3 of the same task; no other task references the symbol, so there is no cross-task handoff to break |
| Tasks 1-5 file-set disjointness (the plan's no-ordering claim) | `ok` — each task's paths sit under a distinct client directory; no task reads an artifact another writes, and no persistence boundary is crossed anywhere in this plan |
| Task 3 Step 1 → Step 3 handoff of `_acting_user_metadata` / `self._acting_user_token` | `→ §2.2` — the field is introduced in Step 1 and re-described in Step 2, and the interaction with the still-composed plugin is what §2.2 is about |

**Rule-like content — the resolution rule, both failure directions**

| Row | Disposition |
|---|---|
| Over-inclusion: identity attached when none configured (rule-4 violation) | `ok` — every language's resolver guards on emptiness before attaching (`if not self._acting_user_token`, `!= ""`, `provider is not null`, `?? undefined`), and every task carries an explicit rule-4 test asserting the header is absent |
| Under-inclusion: identity missing when one is configured | `ok` — each task tests both the bound and ambient arms; Java additionally routes `:208` so the previously-unreachable `search` path is covered |
| Precedence mechanics (bound vs ambient) — not a calibrated value, so mandatory | `ok` — all five resolvers put bound first, and each task's mutation table includes an inverted-precedence mutation that the bound-wins test kills |
| Identity/exclusion: does the bound copy alias the receiver's state? | `→ §2.1` — this is where Python's clone mechanics fail; .NET copies the seven deps and sets the token on the new instance, TS re-reads from the shared client, Java carries fields explicitly, Go has no clone |
| Producers of "an acting-user identity" — one row per producer | `ok` — enumerated all five: `ActingUserMetadata.WithActingUser` (.NET), `ACTING_USER_TOKEN` call option (Java), `WithActingUserToken` ctx value (Go), `_ActingUserAuthPlugin` (Python, being relocated), `resolveActingUserMetadata` (TS). Each is accounted for by a task; the Python one is the only one whose producer changes |
| Rule 1 (per-call override) is unreachable in TypeScript and Python — no coordinator method accepts a token | `dropped` — the spec itself scopes those two clients to bound-plus-ambient (§4 and §6 describe only `withActingUser`/`with_acting_user`), so the plan matches the spec. Whether all five clients *should* expose a per-call parameter is a design question CDR already passed on, not literal wrongness in the plan |
| `ResolveHeadersAsync` mutates a caller-supplied `Metadata` in place | `dropped` — real aliasing, but no in-repo caller is affected: the load test builds a fresh `Metadata` per iteration and it already carries identity (early return), and the sample's `headers` are deleted by Step 5. No concrete failure path |

## 1. Verified-plan-assumptions cross-check

Fresh reads of all 21 cited evidence references: **PA1–PA21 all still hold.** Spot notes on the four that corrected the draft or the spec, since those are the ones most worth re-confirming:

- **PA2** (TypeScript has 8 identity sites, not 16) — reconfirmed: `_client._actingUserToken` 8×, `_client._callCredentials` 8×.
- **PA11** (14 Python stub call sites) — reconfirmed at all fourteen cited lines.
- **PA15** (the 8th .NET parameter needs a default) — reconfirmed: `TestCoordinatorFactory.cs:30-38` passes exactly seven positional arguments.
- **PA16** (15 sites, 6 headered, 9 across 8 methods) — reconfirmed, including `PipelineAsync`'s two stub calls.

**Span check — one uncovered dependency:**

- **Task 3's `with_acting_user` depends on the Python coordinator retaining its channel, and no assumption covers that.** PA11 covers the stub *call sites*; the spec's A17 covers the constructor's *signature* (`entity_class, channel`). Neither states what the constructor does with the channel afterwards. The plan's clone code sits exactly in that gap. Verified in-round and it fails — routed to §2.1.

No other uncovered dependency: every other fact the tasks rest on is covered by a PA row or by a spec assumption as scoped.

## 2. Literal-wrongness findings

### §2.1 — Python's `with_acting_user` references a `self._channel` that does not exist, and rebuilding stubs would defeat the task's own tests

**Description.** Task 3 Step 3 specifies:

```python
    def with_acting_user(self, token: str) -> "EntityCoordinator[T]":
        return EntityCoordinator(self._cls, self._channel, token)
```

The Python coordinator never stores the channel. `__init__` builds four stubs from it and lets it go, so `self._channel` is undefined and the method raises `AttributeError` on its first call — every Python bound-identity test in Step 3 fails immediately.

There is a second, deeper face to this, which is why the fix is not simply "store the channel." Python's coordinator tests inject mocks by **overwriting the stub attributes after construction** (`coordinator._search = MagicMock()`, `coordinator._mapping = MagicMock()`). A clone that reconstructs stubs from a channel would hand the bound coordinator *fresh real stubs*, not the test's mocks — so the four resolution-rule tests could not observe the bound coordinator's call at all, and would instead attempt a real RPC against `localhost:1`. Storing the channel makes the `AttributeError` go away while leaving the tests unable to verify the behaviour the task exists to add.

This is the one place the plan's "mirror TypeScript's model" instinct misleads. TypeScript's clone is safe for the opposite reason: it re-reads `_client._mappingClient` (`core.ts:520-522`) and TS tests inject at the *client* via `makeClientLike`, so a shared client carries the mocks into the clone. Python injects at the *coordinator*, so only a state-copying clone preserves them.

**Evidence.**
- `Iverson.Clients/Python/iverson_client/core.py:478-490` — `__init__(self, entity_class, channel)` assigns `_cls`, `_type_name`, `_key_field`, `_mapping`, `_persistence`, `_retrieval`, `_search`. No `self._channel`.
- `Iverson.Clients/Python/tests/test_entity_coordinator.py:44-46` — `coordinator = EntityCoordinator(CoordArticle, channel)` then `coordinator._search = MagicMock()`; `:52-53` does the same for `_mapping`.
- `Iverson.Clients/TypeScript/src/core.ts:520-522` — the contrasting TS case that makes reconstruction safe there.

**Proposed fix.** Make the Python clone copy state rather than reconstruct it, so the four stub references (mocked or real) carry over:

```python
    def with_acting_user(self, token: str) -> "EntityCoordinator[T]":
        """Return a coordinator bound to ``token``, leaving this one untouched."""
        bound = copy.copy(self)
        bound._acting_user_token = token
        return bound
```

`copy.copy` is a shallow copy, so the bound coordinator shares the same four stubs — which is what makes the test convention work and what keeps the receiver unmutated. Add `import copy` to `core.py`'s imports. The plan's non-mutation guarantee still holds structurally, and Step 7's `with_acting_user` returns `self` mutation remains killable by the non-mutation test.

If a copy-based clone is unwanted, the alternative is to store `self._channel` **and** copy the four stub attributes explicitly onto the new instance — same effect, more lines, same requirement that the stubs be carried rather than rebuilt.

### §2.2 — Task 3's Step 1 cannot prove what the plan says it proves, because the duplicate-header plugin is still composed

**Description.** The plan makes Step 1's ordering load-bearing and states its purpose explicitly: convert `get_schema` first, "proving `metadata=` works on one call before fourteen more depend on it," discharging the spec's sequencing risk (nothing in this repo passes `metadata=` to a Python stub).

But Step 2 is what removes `_ActingUserAuthPlugin` from the credential composition. At the end of Step 1 the plugin is still attached to every call, so the identity header is present on `get_schema` **whether or not the new `metadata=` argument did anything**. A Step-1 test that asserts the identity reached the call passes on the plugin's header alone. If the `metadata=` tuple shape is wrong, or the keyword is silently ignored, or `_acting_user_metadata` returns `()` because the token was stored under a different attribute name, Step 1 still goes green — and the mechanism the whole task sequence depends on ships unproven to the fourteen call sites in Step 4.

Worse in the same window: with both sources active, `get_schema` emits the header **twice**, which is precisely the corruption the design exists to prevent — the server joins the values and JWT validation fails. Nothing in Steps 1–4 would detect it, because the single-header test is Step 5. That state is transient within the task (the only commit is Step 8), so it does not land in git; the defect is that the proof step proves nothing, not that a broken commit is published.

**Evidence.**
- Plan Task 3 Step 1 — "Convert `get_schema` first, with its test", described as asserting "the call receives the identity as per-call metadata"; the risk-discharge rationale is stated in the task preamble and in Step 7's mutation table.
- Plan Task 3 Step 2 — "remove the `_ActingUserAuthPlugin` composition at `core.py:722`", i.e. after Step 1.
- `Iverson.Clients/Python/iverson_client/auth.py:90-93` — `_ActingUserAuthPlugin.__call__` returns the header unconditionally on every call, so its contribution is indistinguishable from the new one by a presence assertion.
- Spec §6 and the plan's own Problem-section reasoning — two identity headers corrupt the token rather than one overriding the other.

**Proposed fix.** Make Step 1's test count, not merely detect. Two ways, either sufficient:

- **Assert exactly one header in Step 1** rather than deferring that to Step 5 — with the plugin still composed the count is two, so the test fails until Step 2 lands, which correctly makes Step 1+2 a single indivisible change and still proves `metadata=` carried the identity once the plugin is gone. Step 5's test then becomes the permanent regression guard rather than the first count assertion.
- **Or swap Steps 1 and 2**, removing the plugin first so that `get_schema`'s only possible identity source is the new `metadata=` argument, making a presence assertion sufficient proof.

The first is the smaller edit and keeps the plan's stated ordering rationale intact.

## 3. Forced decisions

No forced decisions found. §2.1 offers two fix shapes but they are mechanically equivalent and the smaller one is unambiguous; §2.2's two options differ only in step ordering, and neither requires a product or codebase judgment the plan hasn't already made.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions, §2 has two findings, §3 is empty. Both findings are confined to Task 3 (Python); Tasks 1, 2, 4 and 5 came through the sweep clean.

The plan's verification work holds up: all 21 assumptions reconfirmed on fresh reads, including the four that corrected the draft's and the spec's counts. Both findings share a root cause worth naming — Task 3 is the one task that changes a *mechanism* rather than threading an existing one, and both defects come from reasoning about it by analogy to TypeScript (whose clone and test-injection points sit in different places) rather than from the Python code's own shape.
