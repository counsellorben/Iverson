# Dafny Plan Verification: 2026-10-12-csr-round11-clients-tooling-ci-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md
**Source spec:** docs/specs/2026-10-12-csr-round11-clients-tooling-ci-design.md
**Plan SHA:** 7d4669e5896c4df448b2d45c9a4abbf67926b688 | **Replay base:** 7d4669e5896c4df448b2d45c9a4abbf67926b688 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1 - 64 bit
**Dafny files:** docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/

Round 1 is `my-skills/dafny-plan-verification/docs/criticalreviews/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan-formal-verification-1.md` (plan `e45a582c`, which became `70e67d1c` when Iverson `main` was rewritten on 2026-10-15). It ran there as the skill's own validation run, so its files live in that repository. This round runs on the plan after UIP-2 (`7d4669e5`). UIP-2 changed one cell of Verified row 25, and no code block. Its files live here, next to the plan, and are numbered after round 1.

## 0. Coverage enumeration

### Scanned blocks

The plan's code blocks are the same as in round 1: `git diff e45a582c 7d4669e5 -- <plan>` changes only Verified row 25 (a test count). The classification therefore matches round 1's.

Candidates (5):
- `CredentialEndpoints.Check`: T3 S3, `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs`, members `Check`, `AuthentikBaseUrl`, `IsLoopback` and `IsHttpsOrLoopback` (C#).
- `TransportGuardedCredentials.refusedWithoutTls` (Java), from two steps:
  - T1 S3, `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java`, members `applyRequestMetadata` and `refusedWithoutTls`;
  - T1 S5, `OAuth2ClientCredentials.java`, the guard in `applyRequestMetadata`.
- `ActingUserIdentity.RequireSendableAmbientToken` (C#), from three blocks:
  - T2 S3, `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs`, member `RequireSendableAmbientToken`;
  - T2 S3, `EntityCoordinator.cs`, the `_identity` initializer and the guard in `WithActingUser`;
  - T2 S7, `SchemaCatalogClient.cs`, the `_actingUserTokenProvider` initializer.
- `EntityCoordinator.__init__` (Python): T2 S11, `Iverson.Clients/Python/iverson_client/core.py`, the token guard in `EntityCoordinator.__init__` and the guard in `with_acting_user`.
- `_printable` (Python): T4 S3, `Iverson.Agents/Python/iverson_agent/__main__.py`, `_printable` and `_TERMINAL_CONTROLS`.

Excluded (run, check and commit blocks are not code the plan produces, so they are not listed):

**Tests**
- T1 S1 `IversonClientTransportSecurityTest.java`.
- T2 S1 `EntityCoordinatorIdentityResolutionTests.cs` and `TestCoordinatorFactory.cs`.
- T2 S5 `SchemaCatalogClientTests.cs` and `ServiceCollectionExtensionsTests.cs`.
- T2 S9 `tests/test_entity_coordinator.py`.
- T3 S1 `CredentialEndpointsTests.cs` and `HarnessEndpointCheckTests.cs`.
- T4 S1 `tests/test_main.py`, `tests/test_session.py` and `tests/test_retrieval.py`.
- T5 S1 `test_mint_acting_user_token.py` and `test_pattern_leg.py`.
- T10 S1 the digest checker (a scratch check, not repo code).
- T13 S1 `check.py` (a scratch check, not repo code).

**Glue**
- T1 S4 `IversonClient.java`: 5 edits (the javadoc, the `transportGuarded` wrap, and the wrap call in both constructors).
- T1 S5 `OAuth2ClientCredentials.java`: the class and constructor javadoc. Its `applyRequestMetadata` guard is a candidate above.
- T2 S3 `EntityCoordinator.cs`: the `provider` line (`_identity?.TokenProvider`).
- T2 S7 `ServiceCollectionExtensions.cs` (DI wiring).
- T2 S11 `core.py` docstrings.
- T3 S4 `Iverson.LoadTest/Program.cs`: the startup check call and the `IsLoopback` flag.
- T3 S5 `Iverson.ClientConformance/Program.cs`: the `using` and the startup check call.
- T3 S5 `TokenBroker.cs` `DeriveAuthentikBaseUrl` (a pass-through).
- T4 S3 `__main__.py` `import re`.
- T4 S5 `session.py`: the import and the three `_escape` echoes.
- T6 S1 `CachedClientCredentialsTokenProvider.cs`, `Auth.cs`, `AdminAutomationCredentials.cs` and `TokenBroker.cs` (the `using` swap).

**I/O-bound**
- T2 S7 `SchemaCatalogClient.cs` `GetSchemaAsync`.
- T4 S3 `__main__.py`: the top-level handler in `main`/`_run` and the three prints.
- T4 S4 `retrieval.py`: the two `log.warning` formats.
- T5 S3 `mint_acting_user_token.py`: the docstring, the constant, the argument, and the password reads from the environment.
- T5 S4 `pattern_leg.py`: the docstring and the `subprocess.run` env.
- T11 S5 `Iverson.Server/deploy/kind/setup.sh` and `setup.ps1` (operator versions read from the stub).
- T13 S2 `Iverson.Server/deploy/scripts/apply-branch-protection.sh` (GitHub API calls).

**Config / declarative**
- T3 S8 `docs/runbooks/kind-cluster-troubleshooting.md`.
- T5 S6 `docs/user-management-and-security.md`.
- T6 S1 `Iverson.Client.Core.csproj` and `Iverson.Client.Conformance.Driver.csproj`.
- T7 S2 `Iverson.Clients/Python/requirements.in` and `Iverson.Agents/Python/requirements.in`.
- T7 S3 the two `requirements.txt` locks.
- T7 S5 `.github/workflows/dependency-scan.yml` and `.gitlab-ci.yml` (the pip-audit jobs).
- T8 S1 `Iverson.Clients/Go/go.mod` and `go.sum`.
- T8 S3 `.github/workflows/dependency-scan.yml`, `.gitlab-ci.yml` (govulncheck) and `README.md`.
- T9 S1 `Iverson.AdminUI/package-lock.json`, `Iverson.Clients/TypeScript/package-lock.json`, `Iverson.AdminUI/Dockerfile` and `Iverson.Server/docker-compose.yml`.
- T10 S2 `.github/workflows/codeql.yml`, `image-scan.yml` and `deploy-validate.yml`.
- T10 S3 `.gitlab-ci.yml` (the pins and the OWASP `rules:`).
- T10 S4 `Iverson.Server/Iverson.Launcher/Dockerfile` (deleted) and `.github/dependabot.yml`.
- T11 S2 `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml`.
- T11 S3 `Iverson.Server/deploy/terraform/modules/operators/main.tf`.
- T11 S4 `.github/dependabot.yml` and `.github/workflows/image-scan.yml`.
- T12 S2 `.github/workflows/publish-images.yml`.
- T12 S4 `Iverson.Server/deploy/helm/iverson/values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml`.
- T14 S1 `docs/runbooks/csr11-clients-tooling-ci.md`.

### Replay

The plan was replayed through Task 4, the highest task that names a candidate's file. The base was `7d4669e5`, the plan's commit, and `main` was at the same commit, so no choice was needed. No step failed: 45 replace, 4 create and 3 rewrite steps applied, and every other step was skipped by class. Each candidate's file is the target of an applied step.

### Approved trusted base (axioms)

These axioms are used by `CredentialEndpoints.Check` only. They are uninterpreted functions of the string, with no contract, and stand for `Uri.TryCreate(s, UriKind.Absolute, out u)`, `u.Scheme` and `u.IsLoopback`:

```dafny
function UriIsAbsolute(s: CsString): bool

function UriScheme(s: CsString): CsString

function UriIsLoopback(s: CsString): bool
```

`dafny audit` lists `UriScheme` once, as "Compiled declaration has no body and has an ensures clause". The ensures is implicit: it comes from the subset-type return `CsString`. `UriScheme` is an approved axiom, so G1 passes. The other four candidates have no axioms.

### Correspondence tables

Each model was transliterated fresh from the replayed worktree, without reading round 1's files.

**`CredentialEndpoints.Check`.** The source file is `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs`, introduced by T3 S3.

| Source lines | Dafny lines | Notes |
|---|---|---|
| 14 (`TokenEndpointPath`) | 78 | |
| 22 (`Check` signature) | 95 | `string?` becomes `Option<CsString>`. The return is `Outcome<Option<CsString>>`, because exceptions are outcomes. |
| 24–26 (shape check) | 97–99 | `Uri.TryCreate(…, Absolute, out _)` becomes `UriIsAbsolute`. `EndsWith(…, Ordinal)` is modelled at 44–47. |
| 27–28 (shape message) | 82–83, 100 | Deviation: the C# `$"…{TokenEndpointPath} " + "(…)."` is a constant expression, because its hole is a `const`, so it is written as one literal of the same value. Justified. |
| 30–34 (gRPC URL check and messages) | 84–87, 102–104 | `grpc.Scheme` becomes `UriScheme(grpcUrl)` and `grpc.IsLoopback` becomes `UriIsLoopback(grpcUrl)`. `Uri.UriSchemeHttps` is the const `"https"` (37). The `\|\|`/`&&` order and the `grpcMayUseTls` choice of message are kept. |
| 36–37 (token endpoint must be https or loopback) | 88–89, 106–107 | |
| 42–45 (a non-loopback gRPC URL needs an https token endpoint) | 90–91, 109–111 | `grpc` is the URI parsed at line 30, which is assigned whenever this line is reached. `token.Scheme` becomes `UriScheme(tokenEndpoint.value)`. |
| 47–49 (Authentik base URL) | 92–93, 113–116 | When `AuthentikBaseUrl` throws, `Check` is `Threw`. |
| 51 | 117 | `Returned(None)`. |
| 59–61 (`AuthentikBaseUrl`) | 120–125 | `IndexOf(…, Ordinal)` is modelled at 49–65 (the first match, or -1; an `Int32` result). `[..n]` is modelled at 67–72 and is `Threw(ArgumentOutOfRangeException)` when n < 0 or n > Length. |
| 63–64 (`IsLoopback`) | 127–130 | `Check` does not call it. |
| 66–67 (`IsHttpsOrLoopback`) | 132–135 | |
| (none) | 141–159 | Helper lemmas `IndexOfFromFindsSuffix` and `AuthentikBaseUrlReturnsWhenShapeOk`, both proved. |

Each message is a `seq<char>` const that is then typed as `CsString`, with its text unchanged.

**`TransportGuardedCredentials.refusedWithoutTls`.** Paths are relative to `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/`.

| Source lines | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| TransportGuardedCredentials.java:4 (`SecurityLevel`) | T1 S3 | 12 | The enum's three constants. A null `getSecurityLevel()` is not modelled, because the frozen signature takes a non-optional level. In Java a null compares unequal to `PRIVACY_AND_INTEGRITY`, so it takes the same refusal branch as `NONE`. Justified. |
| TransportGuardedCredentials.java:45 (`Status.UNAUTHENTICATED.withDescription`) | T1 S3 | 15–29 | `Status` is a code plus an optional description. |
| `MetadataApplier.fail` (:45) | T1 S3 | 34, 43–47, 64–70 | Applier calls are recorded as events. `Failed` means exactly one `fail`. `Delegated` means the guard did not touch the applier. `Anomalous` covers any other history; it is unreachable and keeps the mapping total. |
| TransportGuardedCredentials.java:40–48 (`refusedWithoutTls`) | T1 S3 | 53–61 | `requestInfo` is reduced to its security level, the only thing the method reads from it. Branch order is kept. |
| TransportGuardedCredentials.java:27–34 (`applyRequestMetadata`) | T1 S3 | 75–83 | `inner.applyRequestMetadata` is `Delegated`. |
| OAuth2ClientCredentials.java:79–83 (the guard and the early `return`) | T1 S5 | 88–97 | The `executor.execute` token-fetch lambda is `Delegated`, because no property is about it. |

**`ActingUserIdentity.RequireSendableAmbientToken`.** Paths are relative to `Iverson.Clients/DotNet/Iverson.Client.Core/`.

| Source lines | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| (support types) | — | 6–12 | `Option<T>` stands for a nullable reference. `Outcome<T>` is `Returned(value)` or `Threw`. `type TokenProvider(==)` is an opaque stand-in for `Func<Task<string>>`; it is a type declaration, not a contract, and is never invoked. |
| ActingUserIdentity.cs:8–21 | T2 S3 | 16–20 | `hasTokenProvider` is `tokenProvider.Some?`. The constructor default (`true`) is not modelled, because every property quantifies over both values. |
| ActingUserIdentity.cs:28–37 (`RequireSendableAmbientToken`) | T2 S3 | 24–30 | The property pattern `{ TokenProvider: not null, RefusesPlaintextTokens: true }` becomes the `if`; its throw becomes `Threw`. |
| EntityCoordinator.cs:31 (`_identity` initializer) | T2 S3 | 35–38 | `_descriptor = registry.Get<T>()` at :26 runs earlier and can throw for an unregistered `T`. It is outside the candidate and is not modelled, so P1 is about the identity initializer, not the whole constructor. Justified. |
| EntityCoordinator.cs:50–72 (`WithActingUser`) | T2 S3 | 44–55 | The guard `_identity?.RefusesPlaintextTokens != false` uses a lifted `bool?`, modelled as `Option<bool>`; a null identity gives `null != false`, which is true, so it throws. `new(…, _identity)` re-runs the `_identity` initializer. The `tokenProvider` argument is used only in the `_boundActingUser` assignment, which cannot throw, so it is not modelled. Justified. |
| SchemaCatalogClient.cs:22–23 (`_actingUserTokenProvider` initializer) | T2 S7 | 60–66 | |

**`EntityCoordinator.__init__`.** The source file is `Iverson.Clients/Python/iverson_client/core.py`, introduced by T2 S11.

| Source lines | Dafny lines | Notes |
|---|---|---|
| 638 (`acting_user_token: str \| None`) | 10–14 | `str` is `seq<CodePoint>`; `None` is `Option`. |
| 644–652 (the `__init__` token guard) | 26–33, `InitGuard` | Same condition, same order. The raise is `Threw`. |
| 634–666 (all of `__init__`) | 38–47, `Init` | The `_iverson_meta` check at 653–657, which runs after the guard, is the input `hasIversonMeta`. P1 is stated over `InitGuard`, the guard alone. |
| 676–684 (the `with_acting_user` guard) | 50–56, `WithActingUserGuard` | |
| 668–687 (all of `with_acting_user`) | 60–65 | `copy.copy` followed by setting the token is a record update. |

**`_printable`.** The source file is `Iverson.Agents/Python/iverson_agent/__main__.py`, introduced by T4 S3.

| Source lines | Dafny lines | Notes |
|---|---|---|
| 51–52 (`_TERMINAL_CONTROLS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")`) | 12–15, `TerminalControlsMatches` | The class is written as its three ranges. The subagent checked the real regex with `fullmatch` against every non-surrogate code point: 0 mismatches. |
| 60 (`re.Pattern.sub` semantics) | 21–26, `TerminalControlsSub` | A one-character class never matches empty, so `sub` is a left-to-right scan that replaces each matching character. |
| 55–60 (`_printable`) | 30–33, `Printable` | |
| (none) | 38–58 | Helper lemmas `SubAppend` and `PrintableNoControls`, both proved. |

### Gates

Every candidate passed G0–G4 on its first return; there were no bounces.
- **G0.** Every obligation's verifier result matches the subagent's report, and there is no verifier error. The three C# and Java files ran with `--unicode-char false --allow-deprecation`.
- **G1.** The audit lists only the approved axioms, which match their frozen text. There are no forbidden constructs.
- **G2.** There is no contradictory-assumptions warning.
- **G3.** Every property lemma's `requires`/`ensures` matches the frozen text.
- **G4.** No transliterated member has a `requires`. The only `requires` outside the frozen lemmas are on the subagents' own proved helpers (`IndexOfFrom`, `IndexOfFromFindsSuffix` and `AuthentikBaseUrlReturnsWhenShapeOk`). I checked every deviation in the tables above against the worktree file, and each is justified.

G5 used single-line mutations of the transliterated code, each kept as `c/<C>/gates/g5/<L>.<k>.dfy`. Every obligation was killed by its first mutation:
- `CredentialEndpoints.Check`:
  - P1: drop `&& EndsWithOrdinal(…)` from the shape check (line 99).
  - P2: drop `grpcMayUseTls &&` from the gRPC check (103).
  - P3: disable the token-endpoint transport branch (106).
  - P4: disable the Authentik-base branch (116).
  - P5: return the transport message from the shape branch (100).
  - P6: disable the non-loopback branch (109).
- `TransportGuardedCredentials.refusedWithoutTls`:
  - P1: `==` → `!=` on the security level (55).
  - P2: `||` → `&&` (55).
- `ActingUserIdentity.RequireSendableAmbientToken`:
  - P1: `refusesPlaintextTokens == true` → `== false` (26).
  - P2: `lifted != Some(false)` → `lifted == Some(true)` (49).
  - P3: drop `tokenProvider.Some? &&` (26).
- `EntityCoordinator.__init__`:
  - P1: negate the `InitGuard` condition (29).
  - P2: negate the `WithActingUserGuard` condition (52).
- `_printable`:
  - P1: drop `|| (0x7f <= c <= 0x9f)` from `TerminalControlsMatches` (14).
  - P2: drop the kept character in the scan's copy branch (25).
  - P3: move the kept character after the recursive call (25). This fails on the helper row `SubAppend (correctness)`, which P3's proof calls.

### Per obligation

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `CredentialEndpoints.Check` | P1_NeverThrows | spec :56 ("…instead of an opaque `IndexOf` crash") | proved (G5 kill row: `P1_NeverThrows (correctness)`) |
| | P2_AcceptedGrpcUrlIsLoopbackOrHttps | spec :56 ("The gRPC URL must be loopback or `https://`.") | proved (G5 kill row: `P2_AcceptedGrpcUrlIsLoopbackOrHttps (correctness)`) |
| | P3_AcceptedTokenEndpointIsHttpsOrLoopback | spec :56 ("The token endpoint and the Authentik base URL must be `https://` or loopback.") | proved (G5 kill row: `P3_AcceptedTokenEndpointIsHttpsOrLoopback (correctness)`) |
| | P4_AcceptedAuthentikBaseIsHttpsOrLoopback | spec :56 (as P3) | proved (G5 kill row: `P4_AcceptedAuthentikBaseIsHttpsOrLoopback (correctness)`) |
| | P5_MalformedTokenEndpointIsNamedFirst | spec :56 ("It first checks the token endpoint's `/application/o/token/` shape") | proved (G5 kill row: `P5_MalformedTokenEndpointIsNamedFirst (correctness)`) |
| | P6_NonLoopbackGrpcNeedsHttpsTokenEndpoint | plan :1943-1945 (CIR-1 §3.1 C) | proved (G5 kill row: `P6_NonLoopbackGrpcNeedsHttpsTokenEndpoint (correctness)`) |
| `TransportGuardedCredentials.refusedWithoutTls` | P1_RefusesWithoutTlsOrOptIn | spec :36, :41 | proved (G5 kill row: `P1_RefusesWithoutTlsOrOptIn (correctness)`) |
| | P2_DelegatesOverTlsOrWithOptIn | spec :36, :41 | proved (G5 kill row: `P2_DelegatesOverTlsOrWithOptIn (correctness)`) |
| `ActingUserIdentity.RequireSendableAmbientToken` | P1_CoordinatorRefusesAnUnsendableAmbientToken | spec :46 | proved (G5 kill row: `P1_CoordinatorRefusesAnUnsendableAmbientToken (correctness)`) |
| | P2_WithActingUserNeedsAKnownTransport | spec :45 | proved (G5 kill row: `P2_WithActingUserNeedsAKnownTransport (correctness)`) |
| | P3_SchemaCatalogClientRefusesLikeTheCoordinator | spec :47 | proved (G5 kill row: `P3_SchemaCatalogClientRefusesLikeTheCoordinator (correctness)`) |
| `EntityCoordinator.__init__` | P1_InitRefusesAnAmbientTokenWhileRefusing | spec :50 | proved (G5 kill row: `P1_InitRefusesAnAmbientTokenWhileRefusing (correctness)`) |
| | P2_WithActingUserRefusesWhileRefusing | spec :50; spec :217 (VA 39) | proved (G5 kill row: `P2_WithActingUserRefusesWhileRefusing (correctness)`) |
| `_printable` | P1_RemovesTerminalControls | spec :63 | proved (G5 kill row: `P1_RemovesTerminalControls (correctness)`) |
| | P2_KeepsEveryOtherCharacter | spec :63 | proved (G5 kill row: `P2_KeepsEveryOtherCharacter (correctness)`) |
| | P3_WorksCharacterByCharacter | spec :63 | proved (G5 kill row: `SubAppend (correctness)`) |

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

Round 1 (in my-skills) had no §2 or §3 items. The properties and axioms are the same in this round, and this round records each property's citation in the `.dfy` files, which round 1's files did not carry.

## 5. Recommendation

✅ Approve as-is

All 16 obligations are proved, and G5 kills each one (P3 of `_printable` on its helper `SubAppend`'s row). §2 and §3 are empty.
