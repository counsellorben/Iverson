# Row/Field Authorization: Null `OwnerValue` Fix

## Context

`RowFieldAuthorizationEvaluator.Evaluate` (Part 5a) sets `ownerValue = actingUser.FindFirst("sub")?.Value` whenever ownership is required (the caller isn't bypass-eligible and the schema has an `OwnerField` configured). `FindFirst("sub")` can return `null` if the acting user's `ClaimsPrincipal` lacks a `"sub"` claim, but nothing checks for this — `AuthorizationDecision.OwnershipRequired` can be `true` while `OwnerValue` is `null`.

Every consumer of `AuthorizationDecision` across Parts 5a–5d (Postgres write/read in `ObjectMappingGrpcService.cs`/`ObjectRetrievalGrpcService.cs`/`AuthorizationFieldMasking.cs`, StarRocks `Search` in `ObjectSearchGrpcService.cs`, Qdrant `SearchSimilar`/`SearchChunks` in the same file) dereferences `decision.OwnerValue!`/`decision.OwnerFieldName!` with the null-forgiving operator once `OwnershipRequired` is confirmed true, trusting this can never actually be null. This part closes that gap at its source.

Surfaced during the final whole-branch review of Part 5d (`docs/plans/2026-07-14-qdrant-authorization-enforcement-implementation-plan.md`), which characterized the gap as "fails closed." Empirical verification during this design (see below) found that characterization was wrong for two of the four affected surfaces — they crash, not fail closed — and found one surface with a genuine (narrow) authorization bypass rather than either.

## Goal

`RowFieldAuthorizationEvaluator.Evaluate` never returns `OwnershipRequired: true` with a null `OwnerValue`. A caller who would otherwise require ownership-based access, but whose token lacks a `"sub"` claim, gets the same `Denied` decision the evaluator already returns for "no `AuthorizationRules` configured" and "no acting-user identity" — no new decision shape, no call-site changes.

## Non-goals

- Changing `AuthorizationDecision`'s shape, or any of the ~12 existing call sites that consume it — the fix is entirely internal to `Evaluate`.
- Logging when this new denial path fires — matches the 3 existing `Denied` branches, all silent (user decision, confirmed during brainstorming).
- Rejecting missing-`sub` tokens at the authentication layer (`JwtBearerEvents`) instead of the authorization evaluator — rejected as an alternative (see "Approaches considered" in the brainstorming transcript); broader blast radius than the actual bug, since it would affect requests that never reach `RowFieldAuthorizationEvaluator` at all.
- Any change to `ActingUserInterceptor`'s own `?? "unknown"` logging fallback for a missing `sub` — unrelated surface, already independently defensive.

## Design

In `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`, the `else if (!string.IsNullOrEmpty(rules.OwnerField))` branch (the only place `ownershipRequired` is ever set to `true`) gains a `sub`-presence check before proceeding:

```csharp
else if (!string.IsNullOrEmpty(rules.OwnerField))
{
    var sub = actingUser.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub))
        return new AuthorizationDecision(true, false, null, null, null);

    ownershipRequired = true;
    ownerFieldName = rules.OwnerField;
    ownerValue = sub;
}
```

This is the exact literal `AuthorizationDecision(true, false, null, null, null)` already used verbatim by the other 3 `Denied` branches in this method (confirmed by direct read — it appears 3 times today). No new decision shape, no new field, no new exception type.

After this change, `OwnershipRequired: true` structurally guarantees `OwnerValue` is a non-null, non-empty string — every existing `decision.OwnerValue!` dereference across the ~12 call sites remains correct, now backed by a real guarantee instead of an unstated assumption.

## Why this matters (empirical findings from verification)

The prior review's "fails closed" characterization doesn't hold uniformly. Compiled and ran the actual call sites against real package versions in this repo:

- `Google.Protobuf.WellKnownTypes.Value.ForString(null)` **throws `ArgumentNullException`.** `AuthorizationFieldMasking.cs:47` (`EnforceWriteAuthorization`'s force-set-owner-field-on-Create path) calls this with `decision.OwnerValue!` — a Create request from a no-`sub` caller would crash with an unhandled exception, not fail gracefully.
- `Qdrant.Client.Grpc.Conditions.MatchKeyword("ownerId", null)` **also throws `ArgumentNullException`** — confirmed the same way. Part 5d's `SearchSimilar`/`SearchChunks` (`ObjectSearchGrpcService.cs:157,228`) would crash on this same input, not return an empty stream.
- The read-path comparisons in `ObjectMappingGrpcService.cs`/`ObjectRetrievalGrpcService.cs` (`StructFieldAccess.GetFieldString(...) != decision.OwnerValue`) use plain C# string comparison, where `null != null` evaluates to `false`. If a row's stored owner-field value is *also* null or missing (e.g., data predating `AuthorizationRules` on that schema, or a field genuinely absent from the JSON), a no-`sub` caller would be treated as the matching owner and granted access — a real, if narrow, authorization bypass, not a fail-closed default.
- Only the StarRocks SQL path (`WHERE ownerCol = @val` with a null `@val` parameter) genuinely fails closed, because SQL's three-valued `NULL` logic never equates two `NULL`s.

This is not purely theoretical: `Program.cs:99-104` already documents that `sub`-claim retrieval is fragile enough to require an explicit `MapInboundClaims = false` setting on both JWT bearer schemes (ASP.NET Core's default inbound-claim mapping otherwise silently renames `sub` to `ClaimTypes.NameIdentifier`), and `ActingUserInterceptor.cs`'s own logging already defends against a missing `sub` with a `?? "unknown"` fallback. The evaluator was the one place that hadn't inherited that same defensiveness.

## Verified assumptions

| # | Assumption | Verified |
|---|---|---|
| 1 | `RowFieldAuthorizationEvaluator.Evaluate`'s current structure: the `else if (!string.IsNullOrEmpty(rules.OwnerField))` branch is the only place `ownershipRequired` is set `true`, and the 3 sibling `Denied` branches return the exact literal `new AuthorizationDecision(true, false, null, null, null)` | Read `RowFieldAuthorizationEvaluator.cs` in full |
| 2 | `AuthorizationDecision`'s constructor order is `(bool Denied, bool OwnershipRequired, string? OwnerFieldName, string? OwnerValue, IReadOnlySet<string>? AllowedFields)` | Read `IRowFieldAuthorizationEvaluator.cs` |
| 3 | Every consumer of `decision.OwnerValue!`/`decision.OwnerFieldName!` gates the dereference behind `decision.OwnershipRequired` (or passes them through as plain nullable strings with no unsafe dereference) — so removing the only path to a null `OwnerValue` while `OwnershipRequired` is true is sufficient to close the gap everywhere, with zero call-site changes | Read all 12 usage sites: `ObjectMappingGrpcService.cs:156,324,441,477,511` (all `decision.OwnershipRequired && ...`), `ObjectRetrievalGrpcService.cs:42,98` (same pattern), `AuthorizationFieldMasking.cs:46-47,52-53` (same pattern), `ObjectSearchGrpcService.cs:157,228` (Qdrant, gated behind `if (decision.OwnershipRequired)`), `ObjectSearchGrpcService.cs:458,468` (StarRocks — passed through into `AuthorizationConstraint`, itself null-checked before use downstream in `StarRocksQueryBuilder`, unaffected either way) |
| 4 | `Value.ForString(null)` throws `ArgumentNullException` rather than accepting/coercing null | Compiled and ran a throwaway `net10.0` project referencing `Google.Protobuf` 3.31.0: `Value.ForString(null!)` → `ArgumentNullException: Value cannot be null. (Parameter 'value')` |
| 5 | `Conditions.MatchKeyword(field, null)` throws `ArgumentNullException` rather than building a silently-never-matching condition | Compiled and ran a throwaway `net10.0` project referencing `Qdrant.Client` 1.18.1: `Conditions.MatchKeyword("ownerId", null!)` → `ArgumentNullException: Value cannot be null. (Parameter 'value')` |
| 6 | C# `null != null` evaluates to `false` (so the Postgres read-path comparisons don't fail closed when the stored value is also null) | Confirmed via the same compiled probe: `(string?)null != (string?)null` → `False` |
| 7 | No existing test anywhere in the repo constructs a `ClaimsPrincipal`/`ClaimsIdentity` without a `"sub"` claim while relying on ownership-required behavior — so this fix cannot regress any currently-passing test | `grep -rln "new ClaimsPrincipal\|new ClaimsIdentity"` across `Iverson.Server` returns only `ActingUserFixtures.cs` and `RowFieldAuthorizationEvaluatorTests.cs`'s own helper, both of which unconditionally include a `"sub"` claim in every principal they construct |
| 8 | `RowFieldAuthorizationEvaluatorTests.cs` already exists with an established fixture pattern (`ActingUser(sub, groups)` helper, `SchemaWithAuthorization(rules)` helper) this fix's new test should match | Read the file in full — 21 existing tests, consistent `FluentAssertions`-style assertions on `result.Denied`/`OwnershipRequired`/`OwnerFieldName`/`OwnerValue`/`AllowedFields` |
| 9 | Bypass-role callers are entirely unaffected by this change regardless of whether their token has a `sub` claim, since the new check lives inside the `else if` branch only reached when `bypass` is `false` | Read `Evaluate`'s control flow: `if (bypass) { ownershipRequired = false; }` short-circuits before the new check's branch is ever reached |

## Testing plan

One new test in `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`, matching the file's existing style: an `AuthorizationRules` with a non-null `OwnerField` and no matching bypass role (forcing the ownership-required branch), evaluated against a `ClaimsPrincipal` built without a `"sub"` claim (e.g., only a `"groups"` claim) — asserts `Denied: true`, `OwnershipRequired: false`, `OwnerFieldName: null`, `OwnerValue: null`, `AllowedFields: null`, matching the exact assertion shape every other `Denied`-path test in this file already uses.

## Known issues / accepted as out of scope

- **Whether Authentik (or any future IdP swap) can actually issue a validly-signed token without a `sub` claim in production** is not verified by this design — `sub` is a required claim per the OIDC Core spec for ID tokens, but the `"ActingUser"` JWT bearer scheme validates a token supplied via a custom header, and nothing in this codebase enforces it's specifically an OIDC ID token rather than a bare access token. This fix is defense-in-depth regardless of whether the gap is reachable today with a correctly configured Authentik — same reasoning `MapInboundClaims = false` already applied to accepted as adequate for this initiative.
