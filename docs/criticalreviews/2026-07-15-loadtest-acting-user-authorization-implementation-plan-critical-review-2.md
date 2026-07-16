# Critical Implementation Review: 2026-07-15-loadtest-acting-user-authorization-implementation-plan (Round 2)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-07-15-loadtest-acting-user-authorization-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA `5725cf9`) — both are the plan document's own history (`0ca8bb9` creation, `1e421cf` round-1's fix), touching no cited source file. §1 re-checked all 20 assumptions against current `HEAD` anyway.

## 0. Coverage enumeration

Rebuilt fresh this round (per the skill's iterative-review discipline) before reading round 1's findings in detail — not a diff-only pass.

| # | Item | Disposition |
|---|---|---|
| 1 | Task 1 (schema/entity, `SchemaRegistrar` supplement) — code, tests, commands | ok — unchanged since round 1; fresh re-read found nothing new; parameter order, test assertions, and git-path arithmetic all still correct |
| 2 | Task 2 (Authentik blueprint) — compose/kind YAML, Secret template, validation command | ok — unchanged since round 1; fresh re-read found nothing new |
| 3 | Task 3, Step 1 — `AuthentikIdentityConfig`/`MintedToken`/class skeleton | ok — unchanged since round 1 |
| 4 | Task 3, Step 1 — `Totp` | ok — unchanged; re-verified the dynamic-truncation bit arithmetic once more, still correct |
| 5 | Task 3, Step 1 — `Base32Decode`/`GeneratePkce` | ok — unchanged |
| 6 | Task 3, Step 2 — TOTP cache read/write | ok — unchanged |
| 7 | Task 3, Step 3 — `SendAsync` (Host-header forcing) | ok — unchanged |
| 8 | Task 3, Step 4 — `DriveAuthenticationFlowAsync` stage switch | ok — unchanged control flow (the `totpState` variable swap doesn't alter the switch's branching), re-traced against the Python reference's stage names again fresh |
| 9 | **Task 3, Step 4 — `TotpAttemptState` class (NEW this round, round-1's fix)** | ok — declared `private sealed class TotpAttemptState { public int? LastCounter; public int Attempts; }`, nested inside `AuthentikFlowExecutorClient` alongside its sibling private members (`Totp`, `Base32Decode`, etc.) — valid C#, a `private` type modifier is legal (indeed required) on a nested type |
| 10 | **Task 3, Step 4 — `SubmitTotpCodeAsync`'s corrected signature (round-1's fix)** | ok — now `private async Task SubmitTotpCodeAsync(string flowUrl, string secret, TotpAttemptState state)`, no `ref`/`out`/`in` anywhere; independently re-verified by compiling an isolated repro of the exact fixed shape (a class with mutable public fields passed as an ordinary parameter to an `async` method, mutated inside, observed by the caller after `await`) — compiled and ran correctly, printing the mutated field values |
| 11 | Task 3, Step 4 — both call sites (`await SubmitTotpCodeAsync(flowUrl, cachedSecret, totpState);` / `..., secret, totpState);`) | ok — both pass the single shared `totpState` instance declared once at the top of `DriveAuthenticationFlowAsync` (line 441), preserving the intended "state persists across loop iterations" semantics the original `ref`-based version had |
| 12 | Task 3, Steps 5-6 — `MintAsync`/`RefreshAsync`/`ParseTokenResponseAsync`/build commands | ok — unchanged since round 1 |
| 13 | Task 4 — `ActingUserTokenProvider`/`ActingUserIdentities` | ok — unchanged; re-traced the double-checked-locking path fresh for a race: two concurrent callers on an expired token both fail the outer check, both await the semaphore, the winner mints/refreshes and releases, the loser re-checks inside the lock and sees the now-fresh token — correct, no double-mint |
| 14 | Task 5 — env vars, DI registration, schema-registration call | ok — unchanged since round 1 |
| 15 | Task 6 — `OwnerId` stamping, lazy `GetSubAsync` placement | ok — unchanged since round 1 |
| 16 | Task 7 — `EntityCoordinator.PersistAsync` diff, 2 tests | ok — unchanged since round 1 |
| 17 | Task 7 — `WritePathScenario`/`KindWritePathScenario`/`WritePathRunner` diff | ok — unchanged code; re-read fresh with a dynamic-mode focus this round (see row 21) |
| 18 | Task 8 — `ReadPathScenario` diff (3 existing loops + new Author/Tag block) | ok — unchanged since round 1 |
| 19 | Cross-task contracts (`OwnerId` naming, `authorizationByTypeName`, `ActingUserIdentities` positional construction, `GetTokenAsync`/`GetSubAsync` per-call-site sourcing, DI constructor injection ×4) | ok — none of the producing/consuming code changed since round 1; re-confirmed round 1's per-call-site check is still accurate for all listed callers |
| 20 | Rule-like content (`PickRandom` 50/50, `~1%` `OwnerId` stride) | ok — unchanged, both failure directions still hold as analyzed in round 1 |
| 21 | **Dynamic mode — exception propagation when `ActingUserTokenProvider.GetTokenAsync` throws on first mint failure (new analysis this round)** | dropped — traced the full chain: `AuthentikFlowExecutorClient`'s failure paths throw `InvalidOperationException`, not `RpcException`; `WritePathRunner`'s per-iteration `try` only catches `RpcException` (two clauses), so an `InvalidOperationException` from a failed first mint would propagate out of that task-slot's loop, and `Task.WhenAll` would eventually re-throw it uncaught out of `Program.cs`, crashing the process before any report/results are written. This only triggers when minting **genuinely fails** (bad credentials, Authentik unreachable, TOTP misconfiguration) — a setup/environment failure, not a normal operational hazard on the happy path the spec describes. Consistent with the codebase's existing convention (this file already only ever caught `RpcException`, nothing else); the plan doesn't change that philosophy, it just adds one more failure source subject to it. Fails literal-wrongness: the spec's stated outcome (native minting + attachment working) is unaffected on the normal path; a genuine setup failure crashing loudly is defensible fail-fast behavior, not a silently-wrong result |
| 22 | Dynamic mode — `ActingUserTokenProvider.GetTokenAsync`'s refresh-then-fallback `catch (Exception)` swallowing a real `OperationCanceledException` | dropped — would fall back to a full mint instead of honoring cancellation, but `ct` traces back to `CancellationToken.None` everywhere in this app (no `Console.CancelKeyPress`/`CancellationTokenSource` wiring exists anywhere in `Program.cs`) — never actually cancels in practice, so this has no concrete failure path against the spec's outcome |

## 1. Verified-plan-assumptions cross-check

All 20 listed assumptions were re-checked against a fresh read of their cited evidence this round. All 20 still hold — no cited file has changed since plan-write time (both commits since the recorded SHA are the plan document's own history).

**Span check** — no uncovered dependency found, same as round 1. The round-1 fix didn't introduce any new codebase-fact dependency that isn't already covered by an existing assumption row — it's a self-contained C#-language-rule fix, not a new external fact to verify.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (`SubmitTotpCodeAsync` declared `async` with `ref` parameters — CS1988) — resolved. Replaced with a `TotpAttemptState` class passed as an ordinary parameter. Re-verified this round both structurally (correctly nested, both call sites pass the shared instance) and by independently compiling the exact fixed pattern in isolation — it compiles and behaves correctly.

## 5. Recommendation

✅ **Approve as-is** — §1 has no failed assumptions; §2 and §3 are both empty. Plan is ready for `subagent-driven-development`.
