# LoadTest Acting-User Authorization Design

**Date:** 2026-07-15
**Status:** Draft, verified, pending `critical-design-review`

## Goal

`Iverson.LoadTest` currently attaches an automatic service-account Bearer token to every gRPC call (via `CachedClientCredentialsTokenProvider`), but never attaches an acting-user (end-user identity) token — that mechanism exists (`Metadata.WithActingUser`, Part 4 of the identity initiative) but is exercised only by the one-off `acting-user-smoke-test` command, manually, via a pre-minted token from `deploy/scripts/mint_acting_user_token.py`.

This design makes `seed`/`write-path`/`read-path`/`all` natively mint and attach real acting-user tokens to every request, and configures real `AuthorizationRules` (row ownership + field restrictions) on the three benchmark entities so those tokens are actually enforced — turning LoadTest into a load test of the row/field authorization system itself, not just of raw throughput.

## Background: what's already true today

- Service token: fully automatic today, channel-level, via `CachedClientCredentialsTokenProvider` + `AddCallCredentials` (`Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs:44-52,61-74`).
- Acting-user token: a separate mechanism, `Metadata.WithActingUser(string token)` (`Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserMetadata.cs:9-13`), attached manually per-call. Only used today by `acting-user-smoke-test` (`Iverson.LoadTest/Program.cs:111-130`), reading a pre-minted token from `IVERSON_ACTING_USER_TOKEN`.
- Minting today is entirely manual and out-of-band: `deploy/scripts/mint_acting_user_token.py` drives Authentik's flow-executor (identification → password → TOTP enroll-or-solve) + PKCE authorization-code exchange for a single pre-provisioned user (`iverson-acting-user-smoke-test`), printing the token to stdout for a human to export.
- **Row/field authorization is real and already enforced on every live gRPC read/write path** (`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`, `ObjectPersistenceGrpcService`, `ObjectMappingGrpcService` — all call the same `RowFieldAuthorizationEvaluator.Evaluate`), but **a schema with no `AuthorizationRules` configured is denied outright, not left unrestricted** — this is deliberate, tested behavior (`RowFieldAuthorizationEvaluatorTests.cs:41` `Evaluate_NoAuthorizationRules_ReturnsDenied`; `ObjectRetrievalGrpcServiceTests.cs:226` `Get_WithNoAuthorizationRulesConfigured_ReturnsNotFound`).
- **Known pre-existing issue, not introduced by this design:** `BenchmarkArticle`/`BenchmarkAuthor`/`BenchmarkTag` have never had `AuthorizationRules` configured. Given the fail-closed behavior above, `read-path` (`Get`/`GetMany`/`Search`/`Aggregate`) is very likely already returning empty results against current `main` — the most recent recorded read-path benchmark run predates the row/field authorization initiative (Parts 5a-5d, 2026-07-13 onward) by weeks, so this has not been re-verified since. This design's schema changes (§2) resolve it as a side effect, since these entities will no longer have `Authorization == null`.
- No client-side attribute mechanism exists for declaring `AuthorizationRules` from a POCO — this is intentional (`docs/superpowers/specs/2026-07-13-row-field-authorization-foundation-design.md:32`, non-goal: *"schema registration remains a raw proto call"*). The one real example of the full round trip is `RegisterSchemaAuthorizationIntegrationTests.cs:190-260`, constructing `Iverson.Client.Contracts.TypeDescriptor.Authorization` directly.
- `RegisterSchema` is a blind replace, not a merge, at every layer (`SchemaBuilder.BuildDescriptor`, `SchemaRegistry.RegisterAsync`, `SchemaRegistryRepository.UpsertAsync` — the last is a JSONB full-column overwrite, not a partial update) — and `PostgresSchemaManager.ApplySchemaAsync` actively `DROP COLUMN`s anything missing from a re-registration's column list. So `Authorization` **must** be sent in the same `RegisterSchema` call as the complete `Properties`/`Relations` list, never a follow-up call.

## 1. Two Authentik acting-user identities

Both authenticate through the existing `iverson-loadtest-human` OAuth2 client (public, PKCE) — no new provider/application needed, just new `authentik_core.group`/`authentik_core.user` blueprint entries:

1. **Owner-restricted** — reuses the existing `iverson-acting-user-smoke-test` user (no group membership). Subject to ownership checks and field restrictions.
2. **Bypass** — new user `iverson-loadtest-bypass-user`, member of a new group `iverson-loadtest-bypass`. Granted `RowPermission{Role: "iverson-loadtest-bypass", CanReadAll: true, CanWriteAll: true, CanDeleteAll: true}` on all three entities, so it bypasses ownership entirely.

Each request in `write-path`'s post loop and each call in `read-path` independently picks one of the two identities at random and attaches that identity's token as `x-acting-user-authorization` metadata, alongside the existing automatic service-token metadata.

## 2. Entity & schema changes

**New field:** each of `BenchmarkArticle`, `BenchmarkAuthor`, `BenchmarkTag` gets a new `string OwnerId { get; set; } = ""` property, matching the existing style of the other string properties on these entities — holds the Authentik `sub` claim of whichever identity owns the row. No naming collision (confirmed: none of the three entities currently has any `OwnerId`-named member).

**`AuthorizationRules`** (identical shape across all three entities — deliberately checked for this recurrence):

| Entity | `OwnerField` | `RowPermissions` | `FieldPermissions` |
|---|---|---|---|
| `BenchmarkArticle` | `OwnerId` | `{Role: "iverson-loadtest-bypass", CanReadAll/CanWriteAll/CanDeleteAll: true}` | `Body` — `ReadableRoles`/`WritableRoles: ["iverson-loadtest-bypass"]` |
| `BenchmarkAuthor` | `OwnerId` | same | `Email` — same |
| `BenchmarkTag` | `OwnerId` | same | `Category` — same |

Restricting the field to the **same** role used for row-level bypass (rather than a third, unused role) means: bypass identity reads/writes the field normally; owner-restricted identity gets it masked on reads and rejected on writes (`AuthorizationFieldMasking.RejectDisallowedFields` throws `RpcException(StatusCode.InvalidArgument, "Field(s) not permitted for this caller: {field}")` — a status code cleanly distinguishable from the `PermissionDenied` used for ownership/authorization denial, confirmed at `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:94-95`).

**Seeding (`DirectSeeder`):** before seeding, LoadTest mints the owner-restricted identity's token and decodes its `sub` claim. ~1% of seeded rows per entity get `OwnerId` set to that `sub`; the rest get a random non-matching string. `DirectSeeder` writes directly to Postgres/StarRocks, bypassing gRPC and therefore authorization enforcement entirely — this is a data-shape decision, not an enforcement concern.

**Write-path:** for the owner-restricted identity, the server force-sets `OwnerId` to the caller's own `sub` automatically on create (`AuthorizationFieldMasking.EnforceWriteAuthorization`, `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:41-48` — `if (decision.OwnershipRequired) payload.Fields[decision.OwnerFieldName!] = ...`) — the client does not need to set it. For the bypass identity (`OwnershipRequired == false`), the server leaves `OwnerId` untouched, so `WritePathRunner` sets it explicitly to the bypass identity's own `sub` when that identity is the one performing the write, for a consistent "you own what you create" story across both identities.

The restricted field (`Body`/`Email`/`Category`) is always included in write payloads regardless of identity. Writes as the owner-restricted identity are expected to be rejected (`InvalidArgument`); these are tracked in a separate counter/histogram from ordinary write failures. Writes as the bypass identity succeed normally.

**Schema registration:** `SchemaRegistrar.RegisterAllAsync` gains an optional parameter (e.g. `IReadOnlyDictionary<string, Iverson.Client.Contracts.AuthorizationRules>? authorizationByTypeName = null`) — when a registered type's name has an entry, it's set on the `TypeDescriptor.Authorization` before the existing `RegisterSchemaAsync` call, so the reflection-built `Properties`/`Relations` and the new `Authorization` go out together in the one required call, satisfying the blind-replace constraint above. This is a small, additive change to shared `Iverson.Client.Core`, usable by any future caller, not just LoadTest.

**StarRocks column propagation:** confirmed the same `RegisterSchema` RPC drives both stores from the identical `ScalarColumns` list (`ObjectMappingGrpcService.cs:101` Postgres via `IRecordStoreSchemaManager`, `:105` StarRocks via `IEngagementStoreSchemaManager`) — `StarRocksSchemaManager.ApplyTableAsync` diffs `information_schema.columns` and runs `ALTER TABLE ... ADD COLUMN` for anything missing. No separate MV/migration step needed for the new `OwnerId` column. One operational note: if StarRocks is unreachable during registration, the whole RPC throws `Unavailable` rather than silently completing Postgres-only — registration must be retried, not partially trusted.

**Enforcement confirmed consistent across all four gRPC services** (not just Retrieval): `Search`/`Aggregate`/`GroupBy`/`Pipeline` deny silently (empty results, no throw) exactly like `Get`, using the same evaluator. StarRocks enforcement is real row-level SQL filtering, not just field masking — `StarRocksQueryBuilder.BuildSearch` injects `` `{ownerColumn}` = @__ownerVal `` into the WHERE clause for `OwnershipRequired` callers (`Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:61-66,75-80`). `ObjectPersistenceGrpcService.Post`/`Update` (what `EntityCoordinator<T>.PersistAsync`/`UpdateAsync` actually call) independently invoke the same shared `EnforceWriteAuthorization` gate as `ObjectMappingGrpcService.Post`/`Update`.

## 3. Native acting-user token provider (C#)

New code under `Iverson.LoadTest/Auth/`, replicating `mint_acting_user_token.py`'s logic natively rather than shelling out to Python:

- **`AuthentikFlowExecutorClient`** — drives the flow-executor JSON stage machine (identification → password → TOTP enroll-or-solve), generates PKCE verifier/challenge (`System.Security.Cryptography`, no new package), hits `/application/o/authorize/` (no-redirect-follow, extracts `code` from the `Location` header), exchanges the code at `/application/o/token/` requesting `scope=openid groups offline_access`. RFC 6238 TOTP is ~20 lines of `HMACSHA1`. Forces a configurable `Host` header on every request (same issuer-mismatch workaround the Python script uses, per `docs/runbooks/kind-cluster-troubleshooting.md` §5.1).
- **`ActingUserTokenProvider`** — one instance per identity. Caches `{access_token, refresh_token, expires_at}` in memory; `GetTokenAsync()` refreshes via `grant_type=refresh_token` (no TOTP) when the cached token nears expiry, falling back to the full flow only on first use or if the refresh token is rejected. TOTP secret persisted to `~/.cache/iverson/acting-user-totp-secret-{target}-{username}.txt` (0600), matching the Python script's convention, so re-runs against the same environment don't re-enroll.

Constructed once at `Program.cs` startup for commands that need it (`seed`, `write-path`, `read-path`, `all`). `acting-user-smoke-test` and `reset-starrocks` are untouched.

**Verified via Authentik's own source** (`goauthentik/authentik`, `authentik/providers/oauth2/`):
- A `refresh_token` is issued in the authorization_code exchange response precisely when `offline_access` is in the granted scope (`views/token.py:760`: `if SCOPE_OFFLINE_ACCESS in self.params.authorization_code.scope:`).
- Client-secret validation is skipped entirely for `client_type == public` (`views/token.py:195`) — the refresh grant works with just `client_id`, no secret, consistent with `iverson-loadtest-human`'s existing `client_type: public` config.
- `OAuth2Provider.access_token_validity`/`refresh_token_validity` are plain `TextField`s taking a `"hours=N"`/`"days=N"`-style duration string (`providers/oauth2/models.py:274-288`), defaulting to `hours=1`/`days=30`.

## 4. Authentik blueprint changes

In both `deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` and the equivalent kind ConfigMap template:
- New `authentik_core.group`, `identifiers: {name: iverson-loadtest-bypass}`.
- New `authentik_core.user`, `iverson-loadtest-bypass-user`, `attrs.groups: [!Find [authentik_core.group, [name, iverson-loadtest-bypass]]]` — this exact pattern (a list of `!Find`/`!KeyOf` references under `attrs.groups`) is confirmed directly from Authentik's own `blueprints/system/bootstrap.yaml`, and mirrors this repo's existing `attrs.property_mappings: [!Find [...]]` convention.
- `iverson-loadtest-human` provider's `access_token_validity` extended to a generous window (e.g. `hours=2`) — belt-and-suspenders alongside refresh-token support, since it's a dev/test-only credential.

## 5. Wiring into scenarios

- **`EntityCoordinator<T>.PersistAsync`** gains an optional `Metadata? headers = null` parameter, threaded to the underlying `persistence.PostAsync` call (every generated client method already supports this overload — confirmed via the generated `ObjectPersistenceGrpc.cs:134` signature). `WritePathRunner` picks an identity per iteration, builds `new Metadata().WithActingUser(token)`, passes it through, and sets `OwnerId` on bypass-identity writes (see §2).
- **`ReadPathScenario`** picks an identity per call and passes `new Metadata().WithActingUser(token)` directly to `retrieval.GetMany(req, headers)` / `search.Search(req, headers)` / `search.AggregateAsync(req, headers, cancellationToken: ct)` — no `EntityCoordinator` involved here (confirmed it injects the raw clients directly), so no further SDK change needed.

## 6. Configuration

New/changed env vars, following the existing `IVERSON_*` convention, all optional with dev-target defaults matching the blueprint's fixed values:
- `IVERSON_ACTING_USER_HOST_HEADER` (default `authentik-server:9000`, override for kind)
- `IVERSON_ACTING_USER_USERNAME` / `IVERSON_ACTING_USER_PASSWORD` (regular identity; default to the existing smoke-test user's values)
- `IVERSON_ACTING_USER_BYPASS_USERNAME` / `IVERSON_ACTING_USER_BYPASS_PASSWORD` (new bypass identity)

Not new env vars — derived from existing config: OAuth2 client id defaults to the fixed `dev-iverson-loadtest-human-client-id` (both identities share the client); the Authentik base URL is parsed from the existing `IVERSON_TOKEN_ENDPOINT` (already configured for the service token) by trimming its path.

The existing `acting-user-smoke-test` command and `IVERSON_ACTING_USER_TOKEN` env var are left untouched — a separate, minimal manual sanity check.

## Verified assumptions

All of the following were checked against the real codebase (or Authentik's own upstream source) during this design session, not assumed:

1. Row/field authorization enforcement is wired into all four live gRPC services (`ObjectRetrievalGrpcService`, `ObjectSearchGrpcService`, `ObjectPersistenceGrpcService`, `ObjectMappingGrpcService`), not just isolated tests.
2. A schema with `Authorization == null` is denied on every action (Read/Write/Delete) — deliberate, tested, and true for all three benchmark entities today, independent of this design.
3. `RegisterSchema` is a blind replace at every layer; `Authorization` must ship in the same call as the complete `Properties`/`Relations`.
4. `SchemaRegistrar.BuildTypeDescriptor` is private; no existing extension point supports layering `Authorization` on top of the attribute-driven build — confirmed via direct read of `SchemaRegistrar.cs`.
5. `EntityCoordinator<T>.PersistAsync` and `ReadPathScenario`'s direct client calls both have unused `Metadata? headers` parameters already available on the generated client methods — threading them through is additive, not a breaking change.
6. Write-side field rejection throws `RpcException(StatusCode.InvalidArgument, ...)`, cleanly distinguishable from `PermissionDenied` (ownership) — confirmed by reading `AuthorizationFieldMasking.cs` directly.
7. `RowPermission.Role`/groups-claim matching is case-sensitive (`ToHashSet()` with no comparer) — not a blocker, just requires consistent naming between the blueprint group and the schema's `RowPermission.Role`.
8. `StructConverter.ToStruct` reflects all public properties via `JsonSerializer.Serialize` — a new `OwnerId` property is picked up automatically, no wiring needed.
9. No existing `OwnerId`-named member on any of the three entities — confirmed by reading all three entity files directly.
10. StarRocks column propagation happens in the same `RegisterSchema` call via a second, StarRocks-specific `ISchemaManager` implementation (`ALTER TABLE ... ADD COLUMN` diffing) — no separate MV/migration step required.
11. Refresh-token issuance requires `offline_access` in the granted scope, and works for `client_type: public` without a secret — confirmed directly from Authentik's own `views/token.py` source.
12. Authentik blueprint syntax for group creation and user-to-group assignment (`attrs.groups: [!Find [...]]`) — confirmed directly from Authentik's own `blueprints/system/bootstrap.yaml`.
13. `OAuth2Provider.access_token_validity` is a `"hours=N"`-style `TextField` — confirmed from Authentik's own `providers/oauth2/models.py`.
14. No other code in the repo (outside `Iverson.LoadTest` itself) references `BenchmarkArticle`/`BenchmarkAuthor`/`BenchmarkTag` — nothing else depends on these entities being queryable without an acting-user token.

## Known issues / out of scope

- **Read-path is very likely already broken for these entities on current `main`, independent of this design** (see Background) — this design's schema changes fix it as a side effect; no separate remediation is planned per user direction (2026-07-15: "note it in the spec, let this design's changes fix it").
- `ReadPathScenario`'s existing filter/aggregate profiles were not audited for incidental overlap with the newly restricted fields (`Body`/`Email`/`Category`) — worth a quick check during implementation, not a design blocker.
- Long-running write-path/read-path invocations that outlast both the access token and the 30-day refresh token are out of scope (already a generous window; not a realistic concern for a single LoadTest invocation).
