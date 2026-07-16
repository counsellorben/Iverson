# Critical Design Review: 2026-07-15-loadtest-acting-user-authorization-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-15-loadtest-acting-user-authorization-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

| # | Item | Disposition |
|---|---|---|
| 1 | Goal | ok — checked stated outcome ("load test of the row/field authorization system itself") against what §5's wiring actually delivers; see row 15 |
| 2 | Background: service token / acting-user token mechanism split | ok — re-confirmed `ActingUserMetadata.cs` and `ServiceCollectionExtensions.cs` citations by direct read |
| 3 | Background: `Authorization == null` → denied (fail-closed) | ok — re-confirmed live via fresh grep of `RowFieldAuthorizationEvaluatorTests.cs:41`, `ObjectRetrievalGrpcServiceTests.cs:226` |
| 4 | Background: `RegisterSchema` blind-replace constraint | ok — re-read `AuthorizationFieldMasking.cs`/evaluator directly this round; holds |
| 5 | §1 Two identities sharing `iverson-loadtest-human` client | ok — no contrary evidence found |
| 6 | §1 Random per-request identity split | dropped — spec doesn't specify a broken mechanism (e.g. non-thread-safe `Random`), .NET 10 has `Random.Shared`; this is an implementation-time choice, not a design-level defect |
| 7 | §2 New `OwnerId` field, no naming collision | ok — freshly re-read all three entity files; no `OwnerId` member exists today |
| 8 | §2 `AuthorizationRules` table — recurrence across 3 entities (same `OwnerField`/`RowPermissions` shape, field restriction tied to the bypass role) | ok — traced `Evaluate`'s `excluded`/`bypass` computation against all three entities' field lists; the field-restriction-shares-bypass-role design correctly lets the bypass identity through on both axes |
| 9 | §2 Seeding: ~1% `OwnerId` match, rest non-matching | ok — `DirectSeeder` bypasses gRPC/auth entirely (raw SQL), so this is a pure data-shape choice; over-inclusion (accidental sub collision) is negligible-probability, not a design defect |
| 10 | §2 Write-path: server force-sets `OwnerId` for owner-restricted identity on create | ok — re-read `AuthorizationFieldMasking.cs:41-48`; `existingRowJson is null` branch (Post/create path) unconditionally overwrites the payload field, confirms client doesn't need to set it |
| 11 | §2 Write-path: bypass identity must set `OwnerId` itself | ok — confirmed `EnforceWriteAuthorization` leaves `OwnerId` untouched when `OwnershipRequired == false` |
| 12 | §2 Restricted field always in write payload → owner-restricted writes rejected, tracked separately | ok — traced `RejectDisallowedFields` call order (after the owner-field force-set, with `exemptField: decision.OwnerFieldName`) against `WritePathRunner`'s existing `try`/`catch (RpcException)` structure; `postedKeys.Add` only runs on success, so the existing e2e-visibility probe already only samples successful writes — no change needed there |
| 13 | §2 `SchemaRegistrar.RegisterAllAsync` optional authorization-supplement parameter | ok — re-confirmed `BuildTypeDescriptor` is `private static` (`SchemaRegistrar.cs:45`); no existing extension point, addition is additive |
| 14 | §2 StarRocks column propagation via same `RegisterSchema` call | ok — no contrary evidence found on re-read |
| 15 | §2 Enforcement consistency across all 4 gRPC services, AND whether `read-path` actually exercises all 3 entities | **→ §3.1** — `ReadPathScenario.cs` (fresh full read) hardcodes `TypeName = "BenchmarkArticle"` in every one of its `GetMany`/`Search`/`Aggregate` calls; `BenchmarkAuthor`/`BenchmarkTag` are never queried by `read-path`, so their new `AuthorizationRules` (ownership filtering, `Email`/`Category` masking) are never exercised on the read side — only by `write-path`'s create-and-reject path |
| 16 | §2 "Known issues" claim: `ReadPathScenario`'s existing filter/aggregate fields don't overlap the restricted `Body` field, "worth checking, not a blocker" | ok — fresh full read of `ReadPathScenario.cs` confirms `searchFields` explicitly excludes `Body` (line 153, with its own pre-existing comment explaining why) and no clause/spec references `Body`; the spec's own caution here is unnecessary, not wrong — no residual risk found |
| 17 | §3 Native token provider: flow-executor/PKCE/TOTP mechanics | ok — consistent with the Python script's already-battle-tested mechanics per the spec's own citations |
| 18 | §3 TOTP secret cache path `{target}` token, "matching the Python script's convention" | **→ §2.1** — verified LoadTest's own `--target` values (`Program.cs:17`: `"containers"`/`"kind"`) diverge from the Python script's (`mint_acting_user_token.py:436`: `"compose"`/`"kind"`) for the compose case |
| 19 | §3 Host-header override actually achievable in .NET the way Python's `urllib` does it | ok — verified empirically: a compiled `HttpRequestMessage` probe with `req.Headers.Host` set was confirmed on the wire (raw socket capture showed `Host: authentik-server:9000` sent exactly as set); this dependency has no covering item in the spec's Verified Assumptions list, but holds — see §1 span check |
| 20 | §3 Refresh-token issuance requires `offline_access`; public client skips secret check | ok — re-confirmed directly against Authentik's own `authentik/providers/oauth2/views/token.py:195,760` this round (independent re-fetch, not reused from spec's citation alone) |
| 21 | §4 Blueprint group/user syntax (`attrs.groups: [!Find/!KeyOf ...]`) | ok — re-confirmed directly against Authentik's own `blueprints/system/bootstrap.yaml` this round |
| 22 | §4 `access_token_validity` field name/format | ok — re-confirmed directly against Authentik's own `providers/oauth2/models.py:274-288` this round |
| 23 | §5 `EntityCoordinator<T>.PersistAsync`/`ReadPathScenario` `Metadata? headers` threading | ok — additive optional-parameter change, non-breaking by C# semantics; no other repo caller of `PersistAsync` exists outside `Iverson.LoadTest` (see row 25) |
| 24 | §6 Configuration env vars and derived defaults | ok — no contrary evidence found |
| 25 | Verified assumptions §, item 14: no other repo code depends on unauthenticated access to the 3 entities | ok — re-ran the negative-claim grep myself this round (`BenchmarkArticle\|BenchmarkAuthor\|BenchmarkTag\|benchmark_articles\|...`); only historical planning docs and this design's own spec reference them outside `Iverson.LoadTest/` itself — confirmed one previously-unseen hit (`docs/tpc-h-candidates.md`) is prose-only, not live code |
| 26 | "Known issues" §: long-running invocations outlasting the 30-day refresh token | dropped — not realistic for a single LoadTest invocation, spec already scopes this out correctly |

## 1. Verified-assumptions cross-check

All 14 listed items were re-checked against their cited evidence this round (not re-litigated from scratch): all 14 still hold. Three were independently re-verified against fresh source fetches rather than trusted from the spec's own citation (items 11, 12, 13 — Authentik's `token.py`, `bootstrap.yaml`, `models.py`), and held identically.

**Span check** — design dependencies with no covering listed assumption:

- The whole native-minting mechanism (§3) depends on .NET's `HttpClient`/`HttpRequestMessage` actually being able to force a custom `Host` header per-request the way Python's `urllib` does — this is the single most load-bearing mechanical assumption in §3 (without it, minting fails with the exact 401 the Host-header workaround exists to prevent), and it isn't in the Verified Assumptions list. Verified in-round: a compiled `HttpRequestMessage` probe with `req.Headers.Host` set produced the expected header on the wire (raw socket capture). Holds — no finding, but the spec's Verified Assumptions list should include it given how central it is.
- ReadPathScenario's actual per-entity coverage (does it query all three authorization-configured entities, or just one?) — no listed assumption addresses this. Verified in-round with read evidence: it queries only `BenchmarkArticle`. This is not a "can't verify" gap — it's a verified, real coverage gap → surfaced as §3.1 below (not this round's `Verified assumptions` per se, since it's a scope question, not a fact-check).

## 2. Literal-wrongness findings

### 2.1 TOTP cache path uses a different `{target}` token than the Python script it's meant to interoperate with

**Description:** §3 states the native provider's TOTP secret cache path (`~/.cache/iverson/acting-user-totp-secret-{target}-{username}.txt`) is "matching the Python script's convention, so re-runs against the same environment don't re-enroll." The reused owner-restricted identity is `iverson-acting-user-smoke-test` — the exact same Authentik user the repo's documented workflow (`docs/user-management-and-security.md:228`: `python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose`) already enrolls a TOTP device for.

`{target}` is the natural token to source from LoadTest's own `--target` flag. But that flag's values (`Program.cs:17`: `flags.Target is not ("containers" or "kind")`) are `"containers"`/`"kind"` — not `"compose"`/`"kind"`, which is what `mint_acting_user_token.py`'s own `--target` choices are (`mint_acting_user_token.py:436`: `choices=["compose", "kind"]`), and what its cache-path function keys on (`totp_cache_path(target, username)`, `mint_acting_user_token.py:128`).

For the docker-compose case specifically, this means: any environment that has already run the documented `acting-user-smoke-test` workflow (server-side TOTP device enrolled for `iverson-acting-user-smoke-test`, secret cached at `...-compose-iverson-acting-user-smoke-test.txt`) will have LoadTest's native minting look for `...-containers-iverson-acting-user-smoke-test.txt` instead, find nothing, and hit exactly the dead end the Python script's own error message describes for this situation: an already-enrolled device with no locally cached secret, which Authentik "never re-exposes." The native flow cannot complete for this identity against docker-compose without either resetting the user's TOTP device server-side or manually copying/renaming the cache file — directly breaking the design's own asked-for behavior ("LoadTest natively mints... tokens" for `seed`/`write-path`/`read-path`/`all`) for the specific identity and target the design reuses on purpose. (For `--target kind`, both tools use the literal string `"kind"`, so no mismatch there.)

**Evidence:** `Iverson.Server/Iverson.LoadTest/Program.cs:17-20`; `Iverson.Server/deploy/scripts/mint_acting_user_token.py:128,436`; `docs/user-management-and-security.md:228`.

**Proposed fix:** map LoadTest's `--target` value to the Python script's target vocabulary before building the cache path (e.g. `"containers" → "compose"`), or use a cache-path token independent of either tool's `--target` spelling (e.g. keyed on the resolved Authentik base URL/host-header instead of a target name) so both tools converge on the same cache file for the same real environment regardless of each tool's own flag spelling.

## 3. Forced decisions

### 3.1 `read-path` never queries `BenchmarkAuthor`/`BenchmarkTag` — the design's stated goal is only half-delivered for 2 of 3 entities

**The choice:** `ReadPathScenario.cs` hardcodes `TypeName = "BenchmarkArticle"` in all three of its RPCs (`GetMany` at line 57, `Search` at line 165, `Aggregate` at line 232) — confirmed by a fresh full read; nothing in the design's §5 ("Wiring into scenarios") changes this. The design configures identical `AuthorizationRules` (ownership + field masking) on `BenchmarkAuthor` and `BenchmarkTag` (§2's table), and the Goal section states the intent is "turning LoadTest into a load test of the row/field authorization system itself" — but as specified, `read-path`'s ownership-filtering and field-masking behavior for those two entities is never exercised; only `write-path --type Author`/`--type Tag`'s create-and-reject-on-restricted-field path touches them at all, and that path never reads a row back.

**Why it's forced:** Closing the gap requires a real scope decision the spec hasn't made — adding `Get`/`GetMany`/`Search`/`Aggregate` coverage for two more entity types is itself new design surface (sampling keys for `BenchmarkAuthor`/`BenchmarkTag`, deciding what filter/aggregate profiles make sense for them, since neither currently has `IversonSearchKey` fields the way `BenchmarkArticle` does) — not a one-line mechanical fix, and not something this reviewer can pick on the spec's behalf.

**The options:**
1. Extend `ReadPathScenario` to also run `Get`/`GetMany` (and optionally `Search`/`Aggregate`, schema permitting) against `BenchmarkAuthor` and `BenchmarkTag`, so all three entities' ownership filtering and field masking are actually read-tested under load.
2. Accept that `BenchmarkAuthor`/`BenchmarkTag`'s authorization is exercised only via `write-path`'s field-rejection path, and narrow the Goal section's language accordingly (it currently reads as covering "the row/field authorization system" broadly, not "the write-path subset of it for 2 of 3 entities").

## 5. Recommendation

🛑 **Surface forced decisions to user** — §2 has one literal-wrongness finding (must be fixed regardless) and §3 has one forced decision requiring the user's input before the spec can be considered final.
