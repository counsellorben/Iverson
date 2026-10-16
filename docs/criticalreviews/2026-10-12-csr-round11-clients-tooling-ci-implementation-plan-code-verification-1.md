# Dafny Code Verification: 2026-10-12-csr-round11-clients-tooling-ci-implementation-plan (Round 1)

**Implementation plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md
**Plan-stage review:** /home/ben/repositories/Iverson/docs/criticalreviews/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan-formal-verification-2.md
**Code ref:** 50796aedc1e8d3a08792db01c606f0505cae9a86 | **Diff base:** 7a6c1079f37edee052041b677727582e55ac7385 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1 - 64 bit
**Dafny files:** docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/

The code ref is the head of branch `csr11-clients-tooling-ci`. That is the SDD build of the plan (Tasks 1–14), plus a final-review fix wave (`60bff5f0`) and the chart audience fix the user chose after Task 15 (`50796aed`).

**Why the diff base is the branch point.** The plan's last commit `7d4669e5` and the branch point `7a6c1079` differ by one commit: `7a6c1079` itself. That commit is the plan-stage formal verification 2 (one review and five `.dfy` files, no code). The user chose the branch point, so the diff covers exactly the branch's 16 commits.

## 0. Coverage

### Members

All 13 members named on the five `// Source:` lines were found at the code ref. None changed from the plan-stage model, so no member is `not found`, `signature changed` or `kept by cascade`.

Each model is the committed plan-stage model with these edits only:
- Header line 3 is now `// Code ref: …` instead of `// Replay base: …`.
- In `_printable` and `ActingUserIdentity.RequireSendableAmbientToken`, two source comments gained worktree line numbers.
- No transliterated line changed. A diff against `start.dfy` outside line 3 shows comment lines only.

| Candidate | Member | Found at | Changed |
|---|---|---|---|
| `CredentialEndpoints.Check` | `Check` | `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs:22-52` | no |
| | `AuthentikBaseUrl` | `CredentialEndpoints.cs:59-61` | no |
| | `IsLoopback` | `CredentialEndpoints.cs:63-64` | no |
| | `IsHttpsOrLoopback` | `CredentialEndpoints.cs:66-67` | no |
| `TransportGuardedCredentials.refusedWithoutTls` | `TransportGuardedCredentials.applyRequestMetadata` | `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java:27-34` | no |
| | `TransportGuardedCredentials.refusedWithoutTls` | `TransportGuardedCredentials.java:40-48` | no |
| | the guard in `OAuth2ClientCredentials.applyRequestMetadata` | `OAuth2ClientCredentials.java:78-83` | no |
| `ActingUserIdentity.RequireSendableAmbientToken` | `ActingUserIdentity.RequireSendableAmbientToken` | `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs:28-37` | no |
| | `EntityCoordinator<T>`'s `_identity` initializer | `EntityCoordinator.cs:31` | no |
| | `EntityCoordinator<T>.WithActingUser`'s guard | `EntityCoordinator.cs:55-65` (member `:50-72`) | no |
| | `SchemaCatalogClient`'s `_actingUserTokenProvider` initializer | `SchemaCatalogClient.cs:22-23` | no |
| `EntityCoordinator.__init__` | `__init__`'s `acting_user_token` guard | `Iverson.Clients/Python/iverson_client/core.py:644-652` | no |
| | `with_acting_user`'s guard | `core.py:676-684` | no |
| `_printable` | `_printable` | `Iverson.Agents/Python/iverson_agent/__main__.py:55-60` | no |
| | `_TERMINAL_CONTROLS` | `__main__.py:52` | no |

### Correspondence tables

**`CredentialEndpoints.Check`** (`Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs`, T3 S3).
- The subagent also diffed the plan's T3 S3 code block (plan :1915-1973) against worktree lines 12-68. Only the fence and doc-comment lines at the window edges differ.

| Source lines | Dafny lines | Notes |
|---|---|---|
| 14 `TokenEndpointPath` | 78 | |
| 22 `Check` signature | 95 | `string?` → `Option<CsString>`; the return is `Outcome<Option<CsString>>`. |
| 24-28 shape guard | 97-100, message 82-83 | `Uri.TryCreate` → `UriIsAbsolute` (axiom); `EndsWith(Ordinal)` is modelled at 44-47; the message is folded into one literal, because its hole is a `const`. |
| 30-34 gRPC guard | 102-104, messages 84-87 | `grpc.IsLoopback` → `UriIsLoopback(grpcUrl)`, `grpc.Scheme` → `UriScheme(grpcUrl)`, `Uri.UriSchemeHttps` → the const at 37. |
| 36-37 token transport guard | 106-107, message 88-89 | |
| 42-45 a non-loopback gRPC URL needs an https token endpoint | 109-111, message 90-91 | |
| 47-49 Authentik base guard | 113-117, message 92-93 | A throw from `AuthentikBaseUrl` → `Threw`. |
| 51 `return null` | 117 | `Returned(None)` |
| 59-61 `AuthentikBaseUrl` | 120-125 | `IndexOf(Ordinal)` is modelled at 50-65; `[..n]` → `SliceTo` at 68-72, which is `Threw` when n < 0 or n > Length. |
| 63-64 `IsLoopback` | 127-130 | |
| 66-67 `IsHttpsOrLoopback` | 132-135 | |
| (none) | 141-159 | Helper lemmas `IndexOfFromFindsSuffix` and `AuthentikBaseUrlReturnsWhenShapeOk`, proved. |

**`TransportGuardedCredentials.refusedWithoutTls`.** Paths are under `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/`.

| Source lines | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| TransportGuardedCredentials.java:40-48 `refusedWithoutTls` | T1 S3 | 53-61 | `requestInfo` is reduced to `getSecurityLevel()`; the `\|\|` order and the early `return false` are kept; the `fail` is a single event carrying the identical message. A null level is not modelled; Java refuses it like `NONE`. |
| TransportGuardedCredentials.java:27-34 `applyRequestMetadata` | T1 S3 | 75-83 | `inner.applyRequestMetadata` → `Delegated`. |
| OAuth2ClientCredentials.java:78-83 the guard | T1 S5 | 88-97 | `if (refused) return;` → the failure outcome; `executor.execute(…)` → `Delegated`. |
| io.grpc `SecurityLevel`, `Status`, `withDescription` | — | 11-46 | Modelled, not axiomatised. |

**`ActingUserIdentity.RequireSendableAmbientToken`.** Paths are under `Iverson.Clients/DotNet/Iverson.Client.Core/`.

| Source lines | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| ActingUserIdentity.cs:8-21 | T2 S3 | 11-20 | `Func<Task<string>>` is the opaque `type TokenProvider(==)`; a nullable reference → `Option`; the constructor defaults are not modelled, because the properties quantify over both values. |
| ActingUserIdentity.cs:28-37 | T2 S3 | 22-30 | The property pattern becomes `Some? && tokenProvider.Some? && refusesPlaintextTokens == true`; the throw → `Threw`. |
| EntityCoordinator.cs:31 | T2 S3 | 32-38 | The earlier `_descriptor = registry.Get<T>()` (:26) is outside the candidate and not modelled. |
| EntityCoordinator.cs:50-72 (guard :55-65, construction :68-71) | T2 S3 | 40-55 | The lifted `bool?` is `Option<bool>` compared with `Some(false)`, so a null identity throws. The construction re-runs the `_identity` initializer. The `tokenProvider` argument is not observed. |
| SchemaCatalogClient.cs:22-23 | T2 S7 | 57-66 | |

**`EntityCoordinator.__init__`** (`Iverson.Clients/Python/iverson_client/core.py`, T2 S11).

| Source lines | Dafny lines | Notes |
|---|---|---|
| 638, 640 the parameters | 14, 27, 38 | `str` → `seq<CodePoint>`, `None` → `Option`; defaults are not modelled (plain parameters). |
| 644-652 the token guard | 26-33 `InitGuard` | `raise` → `Threw`. |
| 653-657 the `_iverson_meta` check | 35-47 `Init` | The reflection call is the input `hasIversonMeta`; the token guard comes first. |
| 658-666 the field assignments | 46 | The stubs, `_cls`, `_type_name` and `_key_field` are elided. |
| 676-684 the `with_acting_user` guard | 49-56 `WithActingUserGuard` | |
| 685-687 `copy.copy`, rebind | 60-65 | A record update. |

**`_printable`** (`Iverson.Agents/Python/iverson_agent/__main__.py`, T4 S3).

| Source lines | Dafny lines | Notes |
|---|---|---|
| 52 `_TERMINAL_CONTROLS` | 9-15 `TerminalControlsMatches` | The three ranges, with no flags. |
| `re.Pattern.sub` used at 60 | 17-26 `TerminalControlsSub` | Modelled as a left-to-right scan, one character per match. |
| 55-60 `_printable` | 28-33 `Printable` | |
| (none) | 38-58 | Helpers `SubAppend` and `PrintableNoControls`, proved. |

### Gates

Every candidate passed G0–G4 on its first return, with no bounces.
- **G0:** each status matches the verifier, with 0 errors. The verified counts are 30 / 3 / 9 / 3 / 10, in the candidate order above.
- **G1:** the audit names only the approved `Uri*` axioms, which match their frozen text. No forbidden construct.
- **G2:** no contradictory-assumptions warning.
- **G3:** the property text is exact.
- **G4:** no transliterated member has a `requires`. The kept text is unchanged from the plan-stage model, and every deviation was checked against the built file.

No member was kept, so Step 7 has no seeds and no obligation is relabelled.

### Per obligation

G5 used the same single-line mutations as the plan stage, each kept as `c/<C>/gates/g5/<L>.1.dfy`. Each obligation's first mutation killed it.

| Candidate | Obligation | Status | G5 kill row |
|---|---|---|---|
| `CredentialEndpoints.Check` | P1_NeverThrows | proved | `P1_NeverThrows (correctness)` (drop `EndsWithOrdinal`, line 99) |
| | P2_AcceptedGrpcUrlIsLoopbackOrHttps | proved | own row (drop `grpcMayUseTls &&`, 103) |
| | P3_AcceptedTokenEndpointIsHttpsOrLoopback | proved | own row (disable the transport branch, 106) |
| | P4_AcceptedAuthentikBaseIsHttpsOrLoopback | proved | own row (disable the base branch, 116) |
| | P5_MalformedTokenEndpointIsNamedFirst | proved | own row (the shape branch returns the transport message, 100) |
| | P6_NonLoopbackGrpcNeedsHttpsTokenEndpoint | proved | own row (disable the non-loopback branch, 109) |
| `TransportGuardedCredentials.refusedWithoutTls` | P1_RefusesWithoutTlsOrOptIn | proved | own row (`==` → `!=`, 55) |
| | P2_DelegatesOverTlsOrWithOptIn | proved | own row (`\|\|` → `&&`, 55) |
| `ActingUserIdentity.RequireSendableAmbientToken` | P1_CoordinatorRefusesAnUnsendableAmbientToken | proved | own row (`== true` → `== false`, 26) |
| | P2_WithActingUserNeedsAKnownTransport | proved | own row (`!= Some(false)` → `== Some(true)`, 49) |
| | P3_SchemaCatalogClientRefusesLikeTheCoordinator | proved | own row (drop `tokenProvider.Some? &&`, 26) |
| `EntityCoordinator.__init__` | P1_InitRefusesAnAmbientTokenWhileRefusing | proved | own row (negate `InitGuard`, 29) |
| | P2_WithActingUserRefusesWhileRefusing | proved | own row (negate `WithActingUserGuard`, 52) |
| `_printable` | P1_RemovesTerminalControls | proved | own row (drop the C1/DEL range, 14) |
| | P2_KeepsEveryOtherCharacter | proved | own row (drop the kept character, 25) |
| | P3_WorksCharacterByCharacter | proved | `SubAppend (correctness)`, the helper P3's proof calls (kept character moved after the recursion, 25) |

There are no failed obligations, so there are no witnesses.

### Ignored candidates

None. Every plan-stage obligation was proved.

### Uncovered

- `Iverson.Server/deploy/scripts/mint_acting_user_token.py:500-506` is logic-bearing: the kind branch's password-source selection. It uses the env var if non-empty, else the smoke-test Secret, and now prints a stderr line naming the source.
  - It comes from the final-review fix wave `60bff5f0`, not from a plan code block, and no candidate models it.
  - Its behaviour is covered by the two tests that commit added to `test_mint_acting_user_token.py`.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ Approve as-is

All 16 obligations are proved against the code as built, and G5 kills each one (`_printable` P3 on its helper `SubAppend`'s row). No obligation is removed or renamed, and §2 and §3 are empty. The one uncovered entry does not change the label.
