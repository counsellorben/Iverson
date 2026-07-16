# Critical Design Review: 2026-07-15-loadtest-acting-user-authorization-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-15-loadtest-acting-user-authorization-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

| # | Item | Disposition |
|---|---|---|
| 1 | Goal | ok — re-checked stated outcome against the now-expanded §5 read coverage; no longer half-delivered (see row 12) |
| 2 | Background (all 4 bullets, unchanged since round 1) | ok — spot-re-read; no contrary evidence, consistent with round 1's ground-truthing |
| 3 | §1 Two identities, `RowPermission` bypass grant | ok — no contrary evidence |
| 4 | §1 Role-name consistency across all 4 places `"iverson-loadtest-bypass"` appears (§1's `RowPermission`, §2's table `RowPermissions`/`FieldPermissions`, §4's blueprint `identifiers.name`) | ok — read all four occurrences side by side; identical literal string in every location, no typo/mismatch |
| 5 | §2 New `OwnerId` field / recurrence across 3 entities | ok — unchanged since round 1, re-read entity files fresh, still no collision |
| 6 | §2 Field-restriction-shares-bypass-role mechanics, re-traced for the WRITE side specifically (round 1 traced read-side masking; this round hand-traced the write-side `excluded`/`RejectDisallowedFields` path for the bypass identity) | ok — hand-traced `Evaluate`: bypass identity's groups contain `"iverson-loadtest-bypass"`, so the sole `FieldPermission` never enters `excluded`, `excluded.Count == 0`, `AllowedFields` stays `null`, `RejectDisallowedFields` returns immediately (`allowedFields is null` early exit) — bypass-identity writes (including the client-set `OwnerId`) pass through completely unrestricted, confirming §2's claim ("writes as the bypass identity succeed normally") holds at the mechanism level, not just as an assertion |
| 7 | §2 Seeding / write-path force-set behavior | ok — unchanged since round 1, no new evidence to re-check |
| 8 | §2 `SchemaRegistrar` supplement, StarRocks propagation, 4-service enforcement consistency | ok — unchanged since round 1 |
| 9 | §3 Flow-executor/PKCE/TOTP mechanics (unchanged) | ok — no contrary evidence |
| 10 | §3 TOTP cache path `{target}` mapping (round 1's fix, `"containers"→"compose"`, `"kind"→"kind"`) | ok — re-verified the mapping is exhaustive against `Program.cs:17`'s validation gate (only `"containers"`/`"kind"` can ever reach this code — no third value to leave unmapped), and that both tokens in the cache filename (`{target}` now mapped, `{username}` already shared via the reused `iverson-acting-user-smoke-test` identity) converge to the exact same path the Python script already uses — the fix is complete, not partial |
| 11 | §3 Host-header override in .NET (now assumption #15) | ok — ground truth per Verified Assumptions, not re-litigated |
| 12 | §5 `ReadPathScenario` extended to `BenchmarkAuthor`/`BenchmarkTag` (round 1's forced-decision resolution) | ok — checked the new bullet against `GetMany`'s existing generic `RetrievalManyRequest{TypeName, Keys}` shape (no schema-specific special-casing to trip over), against `ReadPathScenario`'s single-output-file report structure (purely additive, no structural conflict), and against the ownership-filtering path already verified for `BenchmarkArticle`'s own `GetMany` (same `IEntityRepository`/Postgres path, no new untested mechanism) — the extension is buildable as specified, deferring only the `Search`/`Aggregate` profile *shape* to the implementation plan, which is an appropriate altitude for a design doc, not a residual gap |
| 13 | §6 Configuration — `IVERSON_ACTING_USER_HOST_HEADER`'s kind-target override has no auto-computed default (unlike the Python script's `--release`-derived one) | ok — checked against existing convention: `IVERSON_CLIENT_ID`/`IVERSON_CLIENT_SECRET`/`IVERSON_TOKEN_ENDPOINT` already require the operator to export per-target with no LoadTest-side computation (`Program.cs` reads them with no default at all) — consistent with, not a deviation from, established practice |
| 14 | Verified assumptions § (15 items) | ok — all previously-listed items are ground truth per the skill's rules; item 15 (new this round) not re-litigated either, per the same rule |
| 15 | Known issues § (3 bullets, bullet 2 updated in round 1) | ok — re-read; bullet 2's deferral to the implementation plan is consistent with row 12's disposition, no contradiction |

## 1. Verified-assumptions cross-check

All 15 listed items (14 from round 1, plus the round-1-added item 15 on .NET's `Host` header override) still hold under a fresh read of their cited evidence. None re-litigated from scratch — per the skill's rule, listed items are ground truth once verified in a prior round unless the cited evidence changed, and it hasn't.

**Span check** — no new uncovered dependency found. Both span-check items surfaced in round 1 (the `Host` header mechanism, and `ReadPathScenario`'s per-entity coverage) are now closed: the first was folded into the Verified Assumptions list as item 15; the second was resolved via the round 1 §3.1 forced decision and is now reflected in §5. This round's fresh sweep (§0 above) did not surface any further dependency lacking a covering assumption.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (TOTP cache path `{target}` vocabulary mismatch, `"containers"` vs `"compose"`) — resolved. §3 now maps LoadTest's `--target` value to the Python script's vocabulary before building the cache path; re-verified this round that the mapping is exhaustive and that both tokens in the resulting filename converge with the Python script's existing cache file for the reused identity.
- **Round 1 §3.1** (`read-path` never queried `BenchmarkAuthor`/`BenchmarkTag`) — resolved. User picked "extend read-path to all three entities" (not narrow the goal); §5 now describes the extension, with `Search`/`Aggregate` profile specifics for the two new entities explicitly deferred to the implementation plan. Re-verified this round that the extension is mechanically buildable against `GetMany`'s existing generic request shape and the already-verified ownership-filtering path.
- **Round 1 §1 span check** (Host-header override mechanism uncovered by any listed assumption) — resolved. Added as Verified Assumptions item 15.

## 5. Recommendation

✅ **Approve as-is** — §2 and §3 are both empty. Spec is ready for implementation planning.
