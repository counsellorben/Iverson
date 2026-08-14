# Critical Design Review: 2026-08-12-acting-user-identity-parity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson-serverids/docs/specs/2026-08-12-acting-user-identity-parity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

**Sections**

| Row | Disposition |
|---|---|
| Problem | `ok` — re-derived the 6-of-14 .NET split and Java's 5-with / 6-without overload asymmetry from the method lists; both hold |
| Design §1 (resolution rule) | `ok` — see the two rule rows below |
| Design §1 (non-mutation / concurrency) | `ok` — checked the stated concurrency requirement against the real caller: `WritePathRunner` runs `--concurrency 16` (default) with `identities.PickRandom` per iteration, so two bound coordinators must be independently usable; a returns-new-instance rule satisfies it |
| Design §2 (.NET ambient via DI) | `ok` — `AddTransient(typeof(EntityCoordinator<>))` is reflection-activated (`ServiceCollectionExtensions.cs:75`), so a container-registered `ActingUserIdentity` is resolvable as an 8th dependency |
| Design §2 (.NET 15 call sites, `ResolveHeadersAsync`) | `ok` — see the arrow row on async iterators |
| Design §2 (.NET merge semantic) | `ok` — `Metadata.Get(key)` exists and is used at 5 test sites, so "identity already present" is checkable |
| Design §2 (leave `SchemaCatalogClient` alone) | `ok` — confirmed it applies the provider itself at `SchemaCatalogClient.cs:19-20` and has its own test file, so not touching it costs nothing the design needs |
| Design §3 (Java `stubFor` / `mappingStubFor`) | `ok` — `withOption` occurs only at `EntityCoordinator.java:321,333`, so the rule has exactly two implementation sites as claimed |
| Design §3 (Java null-client clone) | `dropped` — the spec names the constraint ("must tolerate a null client"); *how* to construct the clone (a private copy constructor) is implementation detail and belongs to `critical-implementation-review`, not to design review |
| Design §3 (Java `search:208` bypass) | `ok` — verified `searchStub.search(request)` is called directly, so ambient alone would not reach it and the spec correctly requires routing it through the helper |
| Design §4 (TypeScript) | `ok` — `callUnary` takes a per-call `actingUserToken` (`core.ts:154-165`), 16 existing threading sites, `ActingUserToken = string \| (() => Promise<string>)` at `:135` |
| Design §5 (Go ambient only) | `ok` — all 14 coordinator methods take and forward `ctx`; `OAuth2ClientCredentials` is constructed as a field-named struct literal, so adding a default-token field breaks no construction site |
| Design §6 (Python moves off channel credentials) | `→ §2.1` |
| Design §6 (`metadata=` sequencing risk) | `ok` — the spec already flags the absence of in-repo precedent and requires proving one method first; nothing further to add |
| Design §7 (samples switch to ambient) | `ok` — counted the .NET sample's 16 `headers` arguments across both forms; ambient provider already supplied at `Program.cs:41` |
| Design §7 (load test unchanged) | `ok` — 6 coordinator writes pass `(entity, headers, ct)` and rule 1 keeps them winning; read-path uses raw stubs, so it never touches the changed methods |
| Design §8 (testing) | `ok` — the four rule cases plus .NET's two merge cases plus Python's single-header case cover every branch of the resolution rule; per-client mocking conventions exist (A25) |
| Out of scope | `ok` — the five exclusions are all genuinely outside the stated outcome; the conformance-harness collision is named rather than buried |
| Known issues | `ok` — both entries are accepted consequences, not unstated defects |
| Verified assumptions table | see §1 |

**Rules and operands**

| Row | Disposition |
|---|---|
| Resolution rule — **under-inclusion** (identity missing where the caller wanted one) | `ok` — rules 1-3 cover per-call, bound and ambient; the only path to "no identity" is rule 4, which requires nothing configured at any level |
| Resolution rule — **over-inclusion** (identity sent where the caller wanted none) | `dropped` — with an ambient identity configured there is no way to make a deliberately anonymous call. Nothing in the repo wants one (the load test's two identities are both real; the samples want identity on every call), and the spec never claims to support it. Speculation about a caller that does not exist |
| .NET merge rule — **both operands** | `ok` — checked the assumed-clean side too: a supplied `Metadata` carrying *only* non-identity headers is the case that would silently lose identity under a naive "if headers != null, use them as-is" reading. The spec's rule adds identity to it rather than passing it through, so both operands are handled |
| Identity/exclusion mechanics — does the rule conflate two identities? | `ok` — the bound identity is per-coordinator-instance and the ambient is per-client; `WithActingUser` returning a new instance is what keeps two concurrent identities distinct. No shared mutable slot exists to conflate them |
| Eligibility predicate — producers of "an acting-user identity" | `ok` — enumerated every producer of the header across the clients: .NET `ActingUserMetadata.WithActingUser` (`ActingUserMetadata.cs:9`), Java `OAuth2ClientCredentials.ACTING_USER_TOKEN` call option, Go `WithActingUserToken` ctx value (`auth.go:23`), Python `_ActingUserAuthPlugin` (`auth.py:82-93`), TypeScript `resolveActingUserMetadata` (`core.ts:146`). All five are named and accounted for by the design; the Python one is the one being relocated |

**Data-flow arrows**

| Row | Disposition |
|---|---|
| .NET DI closure → `EntityCoordinator<T>` (crosses a container boundary) | `ok` — the ambient provider currently lives only in the `AddIversonClient` closure (`:33,81`); the design's registered `ActingUserIdentity` is what carries it across the reflection-activation boundary. Required parameter exists on the consuming side |
| .NET `ResolveHeadersAsync` → the 15 stub calls, 4 of which are `async IAsyncEnumerable` iterators | `ok` — **uncovered by any assumption, verified in-round**: `GetManyAsync` is already `async IAsyncEnumerable<T>` and already awaits internally (`await foreach` at `EntityCoordinator.cs:171`), so adding an `await` before the stub call is trivially legal in the same method shape |
| Python client → coordinator factory (`core.py:756`) | `ok` — the token must cross this boundary as a third constructor argument; the consuming side is `EntityCoordinator.__init__`, and keeping the parameter optional preserves the four existing two-argument test constructions |
| Python client → `SchemaRegistrar` (`core.py:765`, passes `self._mapping_stub`) | `ok` — checked whether registration depends on the acting-user identity being removed from channel credentials: `RegisterSchema` is authorized by the `schema_admin` client-credentials scope, not by the acting user, so relocating the acting-user token does not affect it |
| Python client → `get_schema` (`core.py:758`, same stub, no metadata) | `→ §2.1` |
| Java `IversonClient` ambient → `EntityCoordinator` helpers | `ok` — `stubFor`/`mappingStubFor` read `client`/`searchStub` fields already present on the instance; the parameter the helper needs (a token) is what the ambient supplies |
| Sample → client construction → every coordinator call | `ok` — .NET's sample already supplies the provider, so the arrow is complete once the coordinator consumes it; Java's and TypeScript's require the construction-site change the spec specifies |

## 1. Verified-assumptions cross-check

All 27 listed assumptions reconfirmed under a fresh read of the cited evidence, with one detail correction:

- **A24 — holds in substance, one inaccurate detail.** The spec states that `Headers[key].ToString()` joins duplicate values "with `\", \"`". `StringValues.ToString()` joins with a comma and no space, so the corrupted token is `"A,Bearer B"` rather than `"A, Bearer B"`. I did not execute this (a scratch project's restore exceeded the time budget), so the separator is asserted from the documented behaviour rather than observed. The finding's mechanism and conclusion are unaffected either way: both forms retain the `"Bearer "` prefix, so `context.Token` is set to a corrupt string and JWT validation fails. Recommend correcting the separator in the spec's A24 row, or dropping the separator detail and keeping the conclusion.

The remaining 26 were spot-re-read at their cited evidence and match as written — including the three that corrected the draft design (A3's 15 call sites, A12's null-client constructor at `EntityCoordinator.java:61` used at `EntityCoordinatorTest.java:68`, and A27's separate test file).

**Span check — one uncovered dependency:**

- **Python's `get_schema` depends on channel-level acting-user injection, and no listed assumption covers it.** A16 verifies *that* the acting-user plugin attaches to every call; A17/A19/A20 cover the coordinator and the credential composition. Nothing states that a non-coordinator path also relies on that injection. The design's correctness for `get_schema` sits exactly in the gap between "the plugin attaches to every call" (verified) and "every consumer of that injection is accounted for" (never stated). Routed to §2.1.

- **`.NET` awaiting inside async-iterator methods** — also uncovered, verified in-round as `ok` (see the §0 arrow row); no action.

## 2. Literal-wrongness findings

### §2.1 — Relocating Python's acting-user token silently empties `get_schema`

**Description.** Design §6 removes the acting-user token from Python's channel call-credentials and has *coordinators* pass it per call instead. But `IversonClient.get_schema` is not a coordinator path: it calls the mapping stub directly and passes no metadata, relying entirely on the channel-level injection the design removes. After this change, `get_schema` sends no acting-user identity.

That is not a degradation to a partial result — it is an empty one. The server evaluates read authorization per schema and skips every denied entry, and the evaluator denies unconditionally when the acting user is null. So `client.get_schema()` returns `[]` for every caller, where today it returns the identity's authorized catalog. A method that works before the change returns empty after it, with no error raised — the same silent-null failure mode this spec exists to eliminate, reintroduced one method over.

**Evidence.**
- `Iverson.Clients/Python/iverson_client/core.py:758-763` — `get_schema` calls `self._mapping_stub.GetSchema(mapping_pb.GetSchemaRequest(trace_id=trace_id))` with no `metadata=` argument, so its only identity source is the channel credentials composed at `:713-723`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:79-80` — per-schema `_authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read)`, and denied schemas are skipped rather than erroring.
- `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — returns `Denied: true` when `actingUser is null`, before any role or tenant consideration.
- The spec's own "Out of scope" section does not cover this: it excludes the server, the write/mapped contracts, client-side validation, the Go and Python samples, and the conformance harness. `get_schema` is none of those.

**Proposed fix.** Extend Design §6 to cover every Python consumer of the relocated token, not only coordinators. Concretely: `IversonClient` holds the ambient token; `get_schema` passes it as `metadata=` on its own stub call exactly as the coordinator methods will; and the spec's §8 testing list gains a Python case asserting `get_schema` emits the identity header. This also removes the sequencing risk A18 flags for free — `get_schema` is a single-call method with an existing test surface, making it the natural place to prove `metadata=` works before the ten coordinator methods follow.

While making that edit, audit the remaining non-coordinator paths on the Python client for the same dependency. I checked `registrar()` (`core.py:765`) and it is unaffected, since `RegisterSchema` is authorized by the `schema_admin` client-credentials scope rather than the acting user — but that audit belongs in the spec rather than in this review, so that the next reader does not have to redo it.

## 3. Forced decisions

No forced decisions found. §2.1's fix has an obvious shape (treat `get_schema` like a coordinator call) and does not require choosing between materially different designs; the Python-versus-channel-credentials choice the spec had to make was already resolved as option (a) before the spec was written.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty. Fix §2.1 (and correct A24's separator detail) and the spec is ready for implementation planning.

The design's core is sound: the four-level resolution rule covers both failure directions, rule 4 genuinely preserves existing behaviour, the load test's continuing to work unedited is a real compatibility proof rather than an assertion, and the three corrections the spec's own verification produced (A3, A12, A27) are the kind of thing that usually surfaces during implementation instead. The single finding is a scope gap rather than a flaw in the model: the design reasoned about coordinators and missed that one non-coordinator method depends on the mechanism being relocated.
