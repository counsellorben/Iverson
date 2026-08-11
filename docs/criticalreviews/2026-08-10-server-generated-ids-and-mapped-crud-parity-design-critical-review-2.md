# Critical Design Review: 2026-08-10-server-generated-ids-and-mapped-crud-parity-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | ok — re-read `ObjectPersistenceGrpcService.cs:47-49` and `ObjectMappingGrpcService.cs:300-305`; the divergence description still matches the code |
| "It is already causing damage" | ok — `Program.cs:64-66` / `:74-84` still show the persist-then-post-mapped mix; `RelationValidator.cs:79` still resolves only the FK column |
| §1 Server contract | ok — `AssignNewKey` composes `ExtractKey`/`SetKey` (`EntityKeyAccessor.cs:13,21`); predicate matches `Update`'s at `ObjectPersistenceGrpcService.cs:99-101`; gate position unchanged |
| §2 signature table — Java column (new) | ok — the added `actingUserToken` parameter matches the search family's existing trailing-parameter convention (`EntityCoordinator.java:178,198,214`) |
| §2 signature table — Python/TS/Go columns | ok — unchanged from round 1; `MappingGetRequest.depth` is `int32` (`object_mapping.proto:168`), matching Go's `depth int32` |
| §2 credential paragraph (new) — Python claim | ok — `IversonClient.coordinator()` passes `self._channel` (`core.py:697`), built with `composite_channel_credentials` including `_ActingUserAuthPlugin` (`core.py:662-677`); every stub on that channel carries it |
| §2 credential paragraph (new) — TypeScript claim | ok — the mapping call site passes `this._client._actingUserToken` into `callUnary` (`core.ts:570-579`), which resolves it into metadata (`:142-161`) |
| §2 credential paragraph (new) — Go claim | ok — `OAuth2ClientCredentials.GetRequestMetadata` reads the token from `ctx` (`auth.go:45-53`) as `PerRPCCredentials` on the shared conn (`coordinator.go:74-88`); mapped calls take `ctx` like `Persist` does |
| §2 credential paragraph (new) — Java fix | ok — `OAuth2ClientCredentials` emits the header only on the per-call `ACTING_USER_TOKEN` option (`OAuth2ClientCredentials.java:56-59`); a mapping `stubFor` equivalent already exists privately at `IversonClient.java:122-126`, so the prescribed shape is implementable |
| §2 credential paragraph (new) — .NET fix | ok — `WithActingUser` is fluent, returning `Metadata` (`ActingUserMetadata.cs:9`), so `new Metadata().WithActingUser(token)` composes; generated mapping stubs accept a `Metadata` positional argument as `PersistAsync` already relies on (`EntityCoordinator.cs:101-113`) |
| §2 Go interface widening | ok — unchanged; `MappingDeleteClient` at `coordinator.go:29-32`, adapter at `:694` |
| §3 Sample-program correction | **→ §2.1** |
| §4 Callers that break | ok — `WritePathRunner.cs:99,111,122` and `DirectSeeder.cs:116,178,254` unchanged; both pass acting-user tokens via `WithActingUser`, so §2's new credential requirement does not add breakage here |
| §5 Testing | ok — Java's mock seam exists (`IversonClient.java:86`); the four "not supplied" cases each map to a real path |
| Out of scope | ok — four exclusions, none load-bearing for the stated outcome |
| Verified assumptions (A1–A20) | see §1 |

### Rules and operands

| Row | Disposition |
|---|---|
| "Not supplied" predicate — both directions | ok — unchanged from round 1; re-confirmed the all-zeroes/empty/null/absent cases against `ExtractKey`'s `v.StringValue` read |
| Credential-presence rule — under-inclusion (a client the spec says is fine but isn't) | ok — this is the round's main check. Tested all three "fine" clients at their actual mapping call sites rather than accepting the spec's assertion: Python `core.py:697`+`:662-677`, TypeScript `core.ts:570-579`, Go `auth.go:45-53`. All three genuinely attach it |
| Credential-presence rule — over-inclusion (a client the spec says is broken but isn't) | ok — Java's data-plane methods use bare stubs (`EntityCoordinator.java:78,93,106,137`); .NET's `mappingBuilder` receives only registration credentials (`ServiceCollectionExtensions.cs:52-56`), never `dataPlaneTokenProvider` or `actingUserTokenProvider` (`:59-81`). Both are correctly identified as broken |
| Eligibility: producers of an entity-create through gRPC | ok — unchanged; two `SetKey` call sites, all other `CreateVersion7` uses mint outbox/DLQ ids |
| Gate ordering rule | ok — unchanged; neither gate reads the key column on create |

### Data-flow arrows

| Row | Disposition |
|---|---|
| assigned key → `PersistResponse.Key` → next write's FK | ok — unchanged |
| assigned key → stamped payload → `MappingResponse.Data` → struct→entity | ok — unchanged; converters present in all four clients |
| **acting-user token → .NET mapped call's `headers` argument → server gate** | **→ §2.1** — the parameter now exists, but §3's caller has no token to put in it |
| acting-user token → Java mapped call's `actingUserToken` argument → server gate | ok — Java's callers in scope are tests, which construct their own tokens; no in-repo Java caller is broken by the added parameter |

## 1. Verified-assumptions cross-check

A1–A19 still hold under fresh reads; the three the spec marks (A11 failed, A16/A17 changed) remain accurate and are not re-litigated.

**A20 (new this round) reconfirmed.** Spot-checked all six of its evidence citations: the server's null-acting-user denial (`RowFieldAuthorizationEvaluator.cs:14-15`, `IRowFieldAuthorizationEvaluator.cs:16-17`), the header as sole source (`ActingUserInterceptor.cs:12,35-37,54`), and each of the five clients' attach-or-not status. All six match.

**Span check — no uncovered dependency.** The round-1 gap is now covered by A20. Re-derived the design's dependency list against the updated spec: server-side key assignment, response carriage, per-client stub reachability, per-client converter availability, per-client credential attachment, and the breaking-caller census each have a covering assumption.

## 2. Literal-wrongness findings

### 2.1 — §3's corrected sample has no acting-user token to pass, so it cannot run

**Description.** §2 now states that .NET's mapped methods gain `Metadata? headers = null` and that "callers pass `new Metadata().WithActingUser(token)`." §3's sample is such a caller, and it has no token to pass — nor any credentials at all.

The sample's DI is `AddIversonClient(grpcEndpoint: "https://localhost:7142", entityAssemblies: [typeof(Article).Assembly])` (`Program.cs:11-16`). The overload's `credentials` and `actingUserTokenProvider` parameters (`ServiceCollectionExtensions.cs:33`) are both left unset, so `AttachCredentials` is never invoked for any channel (`:52-73`) and no `x-acting-user-authorization` header is ever emitted. With no acting user, `RowFieldAuthorizationEvaluator.Evaluate` returns `Denied: true` (`:14-15`) and `EnforceWriteAuthorization` throws `PermissionDenied` (`AuthorizationFieldMasking.cs:41-46`).

§3's code block shows `articles.PostMappedAsync(new Article { … })` with no `headers` argument, so as written the corrected sample calls the new parameter's default and is denied on its first mapped write. The same is true of its `PersistAsync` calls.

**Evidence has changed since round 1, which is why this is surfaced rather than suppressed.** Round 1's §2.1 named the mechanism as the .NET mapping *channel* being credentialed for schema registration, and its fix was scoped to the client library. The §3.1 resolution moved the mechanism to a per-call parameter, which relocates the defect to the caller and makes `Program.cs:11-16` — never cited in round 1 — the operative evidence. The required fix is correspondingly different: it is now a change to the sample's DI, not to `EntityCoordinator`.

**Why this is literal wrongness.** §3 is a stated deliverable of the spec — a corrected sample that demonstrates the server-assigned-key round-trip. Without an acting-user identity the sample cannot complete a single write, so the section's outcome is impossible as specified. That the sample is equally broken *today* does not exempt it: the spec takes on its correction, and a correction that still cannot run has not corrected it.

**Proposed fix.** Extend §3 to state that the sample's DI must supply an acting-user identity — passing `credentials` and `actingUserTokenProvider` to `AddIversonClient` (`ServiceCollectionExtensions.cs:33`) — and thread the resulting token through each mapped write as `new Metadata().WithActingUser(token)`, matching what `Iverson.LoadTest` already does at `WritePathRunner.cs:80`. If supplying a real identity is judged out of scope for a sample, the alternative is to say so explicitly and note that the sample is demonstrative only and not runnable against an authorization-enabled server — but the spec cannot leave §3 claiming a working sample while §2 requires a token the sample has no way to obtain.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (mapped write path unauthenticated-as-user in .NET and Java) — resolved for the client-library surface. §2 now carries the credential requirement explicitly, Java's three methods take the trailing `actingUserToken`, and .NET's take `Metadata? headers`. The residue at the sample's call site is §2.1 above, on changed evidence.
- **Round 1 §3.1** (how .NET's mapped methods obtain an acting-user identity) — resolved as option (a), the per-call headers parameter; the shared `ObjectMappingServiceClient` keeps its schema-registration credentials, as the option required.
- **Round 1 §1 span gap** (no assumption covering acting-user identity on the mapped path) — resolved by A20.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty. Address §2.1's choice between giving the sample a real identity and documenting it as non-runnable, then the spec is ready for implementation planning.
