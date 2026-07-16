# Critical Implementation Review: 2026-07-15-loadtest-acting-user-authorization-implementation-plan (Round 1)

**Plan:** `/home/ben/repositories/Iverson/docs/plans/2026-07-15-loadtest-acting-user-authorization-implementation-plan.md`
**Verified plan-level assumptions section:** present

⚠️ 1 commit since plan-write time (SHA `5725cf9`) — but that commit (`0ca8bb9`) is the plan document's own addition; no cited source file changed. §1 re-checked all 20 assumptions against current `HEAD` anyway.

## 0. Coverage enumeration

| # | Item | Disposition |
|---|---|---|
| 1 | Task 1 code block — `SchemaRegistrar.RegisterAllAsync` diff | ok — hand-traced parameter insertion order (`authorizationByTypeName` before `ct`, matching .NET convention); confirmed `AuthorizationRules` resolves via the file's existing `using` |
| 2 | Task 1 code block — 2 new xUnit tests | ok — traced both against the real `SchemaRegistrarTests.cs` fixture pattern (`Arg.Do<SchemaRequest>` capture, `AsyncUnaryCall` construction) already read in full; assertions (`.Single()`, `.Should().BeNull()`) match the test's own setup data |
| 3 | Task 1 command block — build/test/git-add paths | ok — traced CWD across both `cd` calls (`Iverson.Server` → `../Iverson.Clients/DotNet`); the two `../../Iverson.Server/...` git-add paths correctly resolve to repo-root-relative paths from that CWD |
| 4 | Task 2 code blocks — compose-only + kind blueprint YAML | ok — checked new `authentik_core.group`/`authentik_core.user` entries against the exact `attrs.groups: [!Find [...]]` shape confirmed live from Authentik's own `bootstrap.yaml` (inherited); indentation described in prose ("alongside `grant_types`") is unambiguous against the real file content read during design/planning |
| 5 | Task 2 command block — `helm template` + YAML-parse validation | ok — command syntax is standard `helm template --show-only` usage; no plan-specific risk |
| 6 | Task 3 code block — `AuthentikIdentityConfig`/`MintedToken`/class skeleton (Step 1) | ok — record field order traced against Task 5's construction call sites (both positional-arg orders match exactly) |
| 7 | Task 3 code block — TOTP (`Totp`) | ok — hand-verified the dynamic-truncation arithmetic (`(hash[offset] & 0x7F) << 24 \| ...`) is bit-for-bit equivalent to the Python reference's `unpack(">I", ...) & 0x7FFFFFFF`; big-endian counter byte order confirmed via the `BitConverter.IsLittleEndian` reversal |
| 8 | Task 3 code block — `Base32Decode` | ok — traced the bit-accumulator loop by hand against a known RFC 4648 test vector shape (5-bits-in, 8-bits-out); alphabet string matches RFC 4648's `A-Z2-7` |
| 9 | Task 3 code block — `GeneratePkce` | ok — verified against RFC 7636's S256 method definition (`BASE64URL(SHA256(ASCII(verifier)))`) directly, not just the Python mirror |
| 10 | Task 3 code block — `SendAsync` (Host-header forcing) | ok — consistent with the inherited, empirically-verified assumption that `HttpRequestMessage.Headers.Host` overrides the wire header |
| 11 | Task 3 code block — `DriveAuthenticationFlowAsync` stage switch | ok — traced all 6 `component` branches against the plan's own cited Python stage names; `continue`/`return` control flow matches the reference's loop-until-redirect shape |
| 12 | Task 3 code block — `SubmitTotpCodeAsync` | **→ §2.1** — declared `private async Task SubmitTotpCodeAsync(string flowUrl, string secret, ref int? lastCounter, ref int attempts)` — async methods cannot have `ref`/`out`/`in` parameters in C# (CS1988); confirmed by compiling an isolated repro (`async Task Foo(ref int x)`) — fails with exactly that error |
| 13 | Task 3 code block — `JsonBody`/`ParseTotpSecretFromConfigUrl` | ok — `System.Web.HttpUtility.ParseQueryString` usage already covered by plan-level assumption #20 (empirically verified); `JsonSerializer.Serialize(payload)` on an anonymous object produces the exact `{"uid_field":"..."}`/`{"password":"..."}`/`{"code":"..."}`/`{"selected_stage":"..."}` shapes the Python reference posts |
| 14 | Task 3 code block — `MintAsync`/`RefreshAsync`/`ParseTokenResponseAsync` | ok — no `ref`/`out` on any of these three; `client_secret` correctly omitted from both grant bodies (matches `client_type: public`, inherited assumption) |
| 15 | Task 3 command block | dropped — plain `dotnet build` + `git add`/`git commit` from `Iverson.Server`, paths already covered by row 3's methodology; note the build will fail because of finding §2.1 until fixed |
| 16 | Task 4 code block — `ActingUserTokenProvider`/`ActingUserIdentities` | ok — double-checked-locking shape matches `CachedClientCredentialsTokenProvider` (inherited pattern); `GetSubAsync`'s manual base64url JWT-payload decode is self-contained and doesn't depend on the broken Task 3 method — this file itself compiles independently of finding §2.1 |
| 17 | Task 5 code block — new env vars | ok — traced `actingUserBaseUrl`'s `IndexOf("/application/o/token/", ...)` slice against the existing, already-required `IVERSON_TOKEN_ENDPOINT` convention (fixed shape per the blueprint); no validation gap beyond what the rest of `Program.cs` already tolerates for other env vars |
| 18 | Task 5 code block — DI registration lambda | ok — confirmed `ILogger<AuthentikFlowExecutorClient>` resolves without a dedicated registration (generic `ILogger<T>` support from the existing `AddLogging` call); confirmed lazy construction claim (`AddSingleton` factory doesn't run until first `GetRequiredService<ActingUserIdentities>()`) |
| 19 | Task 5 code block — `BuildAuthorizationRules` + schema-registration call | ok — traced `RowPermission`/`FieldPermission` collection-initializer syntax against the exact working example the source spec itself cites (`RegisterSchemaAuthorizationIntegrationTests.cs:223-227`) |
| 20 | Task 6 step prose + code — `OwnerId` stamping, lazy `GetSubAsync` placement | ok — re-read `DirectSeeder.cs`'s three `Seed*Async` methods; confirmed the "after the skip-check, before the write loop" placement is achievable in all three without restructuring existing control flow; `i % 100 == 0` yields exactly 1% for all three targets (500/50,000, 100/10,000, 4,000/400,000) |
| 21 | Task 6 step prose — StarRocks/Postgres dual-pass `OwnerId` consistency note | ok — re-derived independently: both passes use the identical `i % 100 == 0` predicate over the identical `i` range, so "same stride, possibly different filler string" holds as stated |
| 22 | Task 7 code block — `EntityCoordinator.PersistAsync` diff | ok — positional-parameter insertion (`headers` before `ct`) matches plan-level assumption #10's consumer-impact analysis exactly |
| 23 | Task 7 code block — 2 new xUnit tests | ok — traced against `TestCoordinatorFactory.Create<T>(persistence: ...)` (already read in full); `Arg.Do<Metadata>` capture pattern mirrors `SchemaRegistrarTests.cs`'s established style |
| 24 | Task 7 code block — `WritePathRunner` diff (identity pick, headers, `OwnerId`, field-rejection catch) | ok — traced `identity == identities.Bypass` as reference equality on a `sealed class` (not a record) — correct semantics, confirmed against Task 4's `ActingUserTokenProvider` being declared `class` not `record`; `Interlocked.Increment(ref fieldRejections)` is a call-site `ref` argument to a non-async static BCL method, not a declaration-site `ref` parameter on an async method — does **not** trigger the CS1988 pattern found in row 12 |
| 25 | Task 7 command block — build/test/git-add paths | ok — traced CWD across `cd Iverson.Clients/DotNet` → `cd ../../Iverson.Server`; the two `../Iverson.Clients/DotNet/...` git-add paths correctly resolve from that CWD (mirrors row 3's check, different direction) |
| 26 | Task 8 code block — identity/headers insertion into 3 existing loops + new Author/Tag `GetMany` block | ok — `SampleKeys(Guid[] pool, ...)` signature reused unchanged for the new `ids` arrays; confirmed `identities`/`ct` are in scope at all 4 call sites (3 existing loops + new block) via the Step 1 constructor injection |
| 27 | Task 8 command block | ok — single `cd Iverson.Server` + relative paths, no multi-hop CWD risk |
| 28 | Cross-task contract — `OwnerId` naming (Task 1 → Tasks 6/7/8) | ok — C# property name `OwnerId` (Task 1) matches the Postgres quoted-identifier convention (`"OwnerId"`, PascalCase, consistent with `"Id"`/`"Name"`/`"Email"` already in `DirectSeeder.cs`) and StarRocks's backtick convention (`` `OwnerId` ``) |
| 29 | Cross-task contract — `authorizationByTypeName` (Task 1 → Task 5) | ok — parameter name, type (`IReadOnlyDictionary<string, AuthorizationRules>`), and position all match between declaration and call site |
| 30 | Cross-task contract — `ActingUserIdentities(Regular, Bypass)` positional construction (Task 4 → Task 5) | ok — Task 5's two `new ActingUserTokenProvider(...)` args are built from the *regular* username/password first, *bypass* second, matching the record's declared `(Regular, Bypass)` order |
| 31 | Cross-task contract — `GetTokenAsync`/`GetSubAsync` (Task 4 → Tasks 6, 7, 8) | ok — checked **each caller separately** (Task 6's one call, Task 7's two calls, Task 8's four call sites): every one has `ct` and `identities`/`identity` in scope at the point of call; no caller sources these from a different, stale shape |
| 32 | Cross-task contract — `ActingUserIdentities` constructor injection (Task 5 → Tasks 6, 7, 8) | ok — all 4 consuming constructors (`DirectSeeder`, `WritePathScenario`, `KindWritePathScenario`, `ReadPathScenario`) declare the parameter as exactly `ActingUserIdentities identities`, matching the DI-registered type |
| 33 | Rule-like content — random identity split (`PickRandom`, 50/50) | ok — over/under-inclusion doesn't apply here (not an eligibility predicate); no codebase constraint forces a different split ratio |
| 34 | Rule-like content — `~1%` `OwnerId` ownership stride | ok — both failure directions checked: over-inclusion (accidental UUID collision with the real `sub`) is a cryptographically negligible probability, not a mechanics defect; under-inclusion (every 100th row, not "approximately 1%") is exact, not approximate, for all three fixed target counts |

## 1. Verified-plan-assumptions cross-check

All 20 listed assumptions were re-checked against a fresh read of their cited evidence this round. All 20 still hold — no cited file has changed since plan-write time (confirmed by the drift check: the only commit since the recorded SHA is the plan document's own addition, touching no cited source file).

**Span check** — no uncovered dependency found. Every fact the plan's tasks rely on (file paths, signatures, proto shapes, test conventions, git-path arithmetic, DI wiring, the target-vocabulary cache-path mapping, the client_id/redirect_uri kind-target gap) traces to either a "Verified plan-level assumptions" row or an "Inherited from spec" item.

## 2. Literal-wrongness findings

### 2.1 `SubmitTotpCodeAsync` is declared `async` with `ref` parameters — does not compile (CS1988)

**Description:** Task 3, Step 4's code block declares:
```csharp
private async Task SubmitTotpCodeAsync(string flowUrl, string secret, ref int? lastCounter, ref int attempts)
```
C# does not allow `ref`, `out`, or `in` parameters on `async` methods — this is a hard language restriction (compiler error CS1988: "Async methods cannot have ref, in or out parameters"), not a style issue. `DriveAuthenticationFlowAsync` calls this method with `ref lastTotpCounter`/`ref totpAttempts` at two call sites (the `ak-stage-authenticator-validate` branch with existing device challenges, and the `ak-stage-authenticator-totp` enrollment branch). As written, `AuthentikFlowExecutorClient.cs` (Task 3) will not compile — which blocks every subsequent task's build, since Tasks 4-8 all reference types from this same file (`AuthentikFlowExecutorClient`, `AuthentikIdentityConfig`, `MintedToken`) either directly or transitively through `ActingUserTokenProvider`/`ActingUserIdentities`.

**Evidence:** Plan text at Task 3, Step 4's code block (`private async Task SubmitTotpCodeAsync(...)`); confirmed via an isolated compiled repro this round — a minimal `async Task Foo(ref int x)` method in a fresh net10.0 console project fails with `error CS1988: Async methods cannot have ref, in or out parameters`.

**Proposed fix:** Replace the two `ref` locals with a small mutable state object passed by ordinary reference (a class, not `ref` parameter) — this also more directly mirrors the Python reference implementation's own `TotpAttemptState` class (`self.last_counter`, `self.attempts`), which the plan's Step 4 prose already cites as the source of the replay-window logic being ported. E.g.:
```csharp
private sealed class TotpAttemptState
{
    public int? LastCounter;
    public int Attempts;
}
```
declared once in `DriveAuthenticationFlowAsync` (`var totpState = new TotpAttemptState();` replacing the two separate locals) and passed as an ordinary (non-`ref`) parameter to `SubmitTotpCodeAsync(string flowUrl, string secret, TotpAttemptState state)`, which mutates `state.LastCounter`/`state.Attempts` directly — no `ref`/`out` anywhere, and the two call sites in `DriveAuthenticationFlowAsync` simplify to `await SubmitTotpCodeAsync(flowUrl, cachedSecret, totpState);`.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §1 has no failed assumptions; §2 has one finding (build-blocking, affects every downstream task); §3 is empty. Fix §2.1 before `subagent-driven-development`; no forced decisions require the user's input first.
