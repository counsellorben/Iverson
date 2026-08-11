# Critical Design Review: 2026-08-10-server-generated-ids-and-mapped-crud-parity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | ok — the divergence claim re-read against `ObjectPersistenceGrpcService.cs:47-49` (unconditional mint) and `ObjectMappingGrpcService.cs:300-305` (extract-then-conditional-mint); both match the spec's description |
| "It is already causing damage" | ok — `Program.cs:64-66` persists tags with client IDs, `:74-84` posts articles with `TagIds` referencing them; `RelationValidator.cs:79` resolves the FK column only, no row lookup |
| §1 Server contract | ok — `AssignNewKey` composes `ExtractKey`/`SetKey` (both present, `EntityKeyAccessor.cs:13,21`); predicate matches `Update`'s existing one at `ObjectPersistenceGrpcService.cs:99-101`; `Grpc.AspNetCore` 2.80.0 makes `RpcException` available |
| §1 error message "Omit it on create" | dropped — .NET/Java/Go always serialize the key field so it cannot literally be omitted, but leaving it at its default (all-zeroes `Guid` / `""`) satisfies the predicate, so the asked-for behavior does not fail. Misleading wording, not literal wrongness |
| §2 Client parity surface | → §2.1 |
| §2 Go interface widening | ok — `coordinator.go:29-32` types `deps.mapping` as Delete-only `MappingDeleteClient`, adapter at `:694`; the spec already carries this and scopes the break to custom-`deps` construction, which is test-only |
| §3 Sample-program correction | → §2.1 (its `PostMappedAsync` calls sit on the same broken path) |
| §4 Callers that break | ok — `WritePathRunner.cs:99,111,122` and `DirectSeeder.cs:116,178,254` confirmed setting `Id` then calling `PersistAsync`; both absorb `InvalidArgument` (`WritePathRunner.cs:145-148`, `DirectSeeder.cs:104-109`), so the silent-failure claim holds. `DirectSeeder`'s `COPY` half writes via Npgsql, never gRPC |
| §5 Testing | ok — the four "not supplied" cases each trace to a real code path; `IversonClient`'s package-private mapping-stub constructor (`IversonClient.java:86`) gives Java the mock seam the section assumes |
| Out of scope | ok — four exclusions, none of which the design's stated outcome depends on |
| Verified assumptions | see §1 |

### Rules and operands

| Row | Disposition |
|---|---|
| "Not supplied" predicate — over-inclusion (rejects a legitimate create) | ok — `""`, whitespace, absent field and all-zeroes `Guid` all pass. Checked each client's unset-key serialization: .NET/Java emit all-zeroes, Go emits `""`, Python/TS omit or emit null; `ExtractKey` returns `""` for `NullValue` since it reads `v.StringValue` |
| "Not supplied" predicate — under-inclusion (accepts a supplied key) | ok — any other non-empty string throws. Non-string key kinds would read as `""` and slip through, but all five clients serialize keys as strings, so no such input exists |
| Identity/dedup mechanics — `UpsertAndEnqueueOutboxAsync` upserts by key | dropped — forcing a fresh key on every `Post` means a retried create writes a second row rather than upserting. Checked for an actual retry path: no `ServiceConfig`, `RetryPolicy`, `MaxAttempts` or `WithDefaultServiceConfig` anywhere in the five clients. With no retry mechanism in existence, this is speculation about a future |
| Gate ordering rule (auth → relations → key) | ok — neither `EnforceWriteAuthorization` (`existingRowJson: null` on create) nor `ValidateAndNormalizeRelations` reads the key column, so assignment can follow both |
| Eligibility: producers of an entity-create through gRPC | ok — the only two `SetKey` call sites are the two `Post` methods; every other `CreateVersion7` mints an outbox or DLQ row id (`OutboxWriter.cs:39`, `DlqRepository.cs:19`, `EnrichmentConsumer.cs:160`, `ObjectMappingGrpcService.cs:425`) |

### Data-flow arrows

| Row | Disposition |
|---|---|
| assigned key → `PersistResponse.Key` → caller's next-write FK | ok — `object_persistence.proto:21` carries `key`; all five `persist` surfaces return it |
| assigned key → `SetKey` stamps `request.Payload` → `MappingResponse.Data` → client struct→entity | ok — `Post` returns `request.Payload` after stamping; each client has a struct→entity converter (Python `_from_struct`, TS `payloadToEntity:468`, Go `structToEntity:551`, Java `StructConverter.fromStruct:58`) |
| client `getMapped(depth)` → `MappingGetRequest` | ok — `object_mapping.proto:165-170` carries `type_name`, `key`, `depth`, `trace_id`; the "no proto change" claim holds |
| client `postMapped`/`updateMapped` → `MappingWriteRequest` | ok on message shape — `object_mapping.proto:172-176` carries `type_name`, `payload`, `trace_id` |
| **acting-user identity → mapping channel → server write-authorization gate** | **→ §2.1 / §3.1** — crosses a credential boundary the spec never traces |

## 1. Verified-assumptions cross-check

A1–A19 all still hold under a fresh read; the three the spec already marks (A11 failed, A16/A17 changed) are recorded accurately and are not re-litigated here. Spot-confirmed the cited evidence for A2 (`EntityKeyAccessor.cs:13-19`), A5 (`SetKey` call sites), A6 (`object_persistence.proto:21`, `object_mapping.proto:186`), A9 (`object_mapping.proto:10-12`), A15 (`RelationValidator.cs:79`) and A18 (no retry policy in any client).

**Span check — one uncovered dependency:**

- **The mapped write path requires an acting-user identity, and no assumption covers whether the mapping channel carries one.** A8 verifies only that each client's coordinator can *reach* a mapping stub — reachability, not credentialing. The design depends on the stronger fact that a mapped `Post`/`Update` arrives at the server with `x-acting-user-authorization` set. Verified in-round; it is false for two of five clients. See §2.1.

## 2. Literal-wrongness findings

### 2.1 — The mapped write path is unauthenticated-as-user in .NET and Java, so every new mapped write is denied

**Description.** The server's write gate denies unconditionally when no acting user is present. `RowFieldAuthorizationEvaluator.Evaluate` returns `Denied: true` when `actingUser is null` (`RowFieldAuthorizationEvaluator.cs:14-15`; `Denied` is the first positional field of the record, `IRowFieldAuthorizationEvaluator.cs:16-17`), and `AuthorizationFieldMasking.EnforceWriteAuthorization` turns that into `RpcException(PermissionDenied)` (`AuthorizationFieldMasking.cs:41-46`). The acting user is populated only from the `x-acting-user-authorization` metadata header (`ActingUserInterceptor.cs:12,35-37,54`); absent that header, the interceptor returns early and `IActingUserAccessor.ActingUser` stays null — the service's own client-credentials token does not identify an acting user.

Three of the five clients attach that header to every call, so their new mapped methods inherit it: Python via `metadata_call_credentials(_ActingUserAuthPlugin(...))` on the channel (`core.py:662-664`), Go via context metadata (`auth.go:52-53`), TypeScript via `resolveActingUserMetadata` inside `callUnary` (`core.ts:142-161`).

The other two do not:

- **Java.** `EntityCoordinator`'s data-plane methods use the bare stubs — `client.persistenceStub` (`EntityCoordinator.java:78,93`), `client.retrievalStub` (`:106`), `client.mappingStub` (`:137`). Only the search family takes an `actingUserToken` parameter and routes through `stubFor` (`:269-271`). `OAuth2ClientCredentials` emits the acting-user header only when the per-call `ACTING_USER_TOKEN` option is set (`OAuth2ClientCredentials.java:56-59`), and these call sites never set it.
- **.NET.** `EntityCoordinator`'s mapped methods take no headers parameter at all (`EntityCoordinator.cs:33,50,66`), unlike `PersistAsync`, which does (`:101`). They go over the injected `ObjectMappingServiceClient`, and `ServiceCollectionExtensions` attaches the *schema-registration* credentials to that channel — `dataPlaneTokenProvider` is attached only to the persistence, retrieval and search builders (`:59-73`), and `actingUserTokenProvider` is handed only to `SchemaCatalogClient` (`:79-81`).

So the spec's §2 instruction — "Each body is the language's existing `persist`/`get` body with the stub and request type swapped" — prescribes, for Java, three methods that cannot succeed. And §3's corrected sample sits on `PostMappedAsync`, which has the same gap in .NET.

**Why this is literal wrongness rather than a pre-existing limitation.** §2's stated outcome is that all five clients gain a working relation-resolving write, and §3's outcome is a sample that runs. Both fail as written: Java's three new methods return `PermissionDenied` for every type, and the rewritten sample cannot complete its first `PostMappedAsync`. The bug living in the client's credential wiring rather than in the new method bodies does not move it out of scope — the design calls that wiring.

Note this is independent of the ID rule: it is why the mapped path is currently exercised only where authorization happens to be inert, which is plausibly why the divergence survived this long.

**Proposed fix.** Extend §2 to state the credential requirement explicitly — that a mapped write must carry the acting-user identity in every client — and add it to the assumptions table so it cannot be lost again. Java's three new methods should take the trailing `actingUserToken` parameter its search family already uses and route through a mapping-stub equivalent of `stubFor`, rather than copying `persist`'s uncredentialed body. For .NET, see §3.1: the mechanism is a genuine choice, not a mechanical fix.

## 3. Forced decisions

### 3.1 — How .NET's mapped methods obtain an acting-user identity

**The choice.** Whether to give .NET's mapped methods a per-call headers parameter, or to attach an acting-user token provider to the shared mapping channel.

**Why it's forced.** The `ObjectMappingServiceClient` is a single registration shared by three consumers: `SchemaRegistrar` (`ServiceCollectionExtensions.cs:80`), `SchemaCatalogClient` (`:79-81`) and `EntityCoordinator` (`EntityCoordinator.cs:17`). The DI code deliberately credentials it differently from the data-plane clients, and the comment at `:22` states the intent — a different token "(e.g. a human/acting-user login) for data-plane calls than for schema registration." Attaching the data-plane or acting-user provider to `mappingBuilder` would therefore change the identity under which schema registration runs, which is a separate contract. The design cannot have working mapped writes in .NET without picking one of these, and the spec picks neither.

**The options.**

- **(a)** Add `Metadata? headers = null` to `GetMappedAsync`/`PostMappedAsync`/`UpdateMappedAsync`, mirroring `PersistAsync`'s existing signature (`EntityCoordinator.cs:101`). Callers pass `new Metadata().WithActingUser(token)`. Smallest change; leaves schema registration's identity untouched; puts the burden on every caller, and the sample must thread a token through each mapped write.
- **(b)** Register a second, separately-credentialed `ObjectMappingServiceClient` for data-plane use, leaving the existing registration for `SchemaRegistrar`. Callers get the acting user automatically; costs a second channel and a named-client registration.
- **(c)** Attach `actingUserTokenProvider` to `mappingBuilder` and accept that schema registration then runs as the acting user. Simplest wiring; contradicts the stated intent at `ServiceCollectionExtensions.cs:22` and changes who registers schemas.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty. The credential question in §3.1 must be settled before planning, and §2.1's Java half should be folded into the spec at the same time.
