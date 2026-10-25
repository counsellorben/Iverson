# Dafny Code Verification: 2026-10-21-csr-round12-platform-implementation-plan (Round 1)

**Implementation plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md
**Plan-stage review:** /home/ben/repositories/Iverson/docs/criticalreviews/2026-10-21-csr-round12-platform-implementation-plan-formal-verification-2.md
**Code ref:** 8ef849174092218f507041e82b8185facafb1937 | **Diff base:** 248d9408de30fc4878127f804939cd872fa828ce | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-21-csr-round12-platform-implementation-plan/

## 0. Coverage

### Inputs and method

- Code ref `8ef84917`: branch `csr12-platform` after SDD Tasks 1–27 and the final-review fix passes (`b9b8bb23`, `8ef84917`).
- Diff base `248d9408`: where the branch left `main`, the DPV-2 commit. `dcv.py inputs` reported two bases. `rule_base` was `8ef84917`, because fix pass 2 edited the plan's Task 27 Expected text on the branch. `head_base` was `248d9408`. The user chose `248d9408`; `8ef84917` would have diffed the branch against itself.
- 37 candidates and 121 obligations, all from DPV-2. None was ignored.
- One prover subagent per candidate, each starting from its committed plan-stage model, with the code-stage brief substitutions. They ran in waves of 8–10 rather than all at once, because of host capacity (4 CPUs, about 8 GB free; every `dafny` call ran under `systemd-run --user --scope -p MemoryMax`).
- G0–G4 were run as the plan-verification skill's Step 9 blocks.
  - One bounce: `setup.sh.guards` added `{:axiom}` to the approved `SortVFirst` and failed G1 (`axiom text differs: SortVFirst`). The prover removed it, and the model passed G0–G4.
  - Every other candidate passed G0–G4 on its first return.
- G5: single-line mutations of the transliterated code only, never a lemma or an approved axiom.
  - A mechanical sweep (`==`/`!=`, `&&`/`||`, `true`/`false`, `<`/`<=`, `>`/`>=`, `then` → `then !`, ±1 → ±2) killed 113 of the 118 proved obligations.
  - Hand-picked branch or return swaps killed the other 5.
  - Each kill is kept as its own file under the run's `gates/g5/`, and each is a one-line `diff`.
- G4 (the controller's own judgement):
  - 36 models equal their starting models apart from the four header lines. Three of those differ in comments too: `admission-check.classify` (line-number comments), `blueprints-configmap.orchestrator-tokenreview` and `secret-service-clients.console-admins`.
  - The only transliteration change is `admission-policy.validations`. The controller checked it against `admission-policy.yaml` @ `8ef84917` lines 31, 42, 63–66, 69–73 and 110–114:
    - CEL's `a || b ? x : y` binds as `(a || b) ? x : y`;
    - `split(':')` is modelled as Go `strings.Split`;
    - the rule (b) guard is `(pods && sub in [exec, attach, portforward] && connectPod) || proxyTarget`.
  - Every `requires` outside the lemmas is the starting model's own, forced by a declared type or an index range, apart from the new functions, which add none.

### Per candidate

#### `TenantAdminGrpcService.RequireUserInTenantAsync`

Source: Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs RequireUserInTenantAsync, UserNotInTenant, IsCanonicalUserPk (123–141)

The prover's return (member status, correspondence table, statuses):

##### DCV return: TenantAdminGrpcService.RequireUserInTenantAsync

Only change from start.dfy: header line 3 "Replay base" -> "Code ref: 8ef84917...". Dafny: 25 verified, 0 errors. (The two bodyless-axiom ensures warnings, which the starting model also carries, make the CLI exit 2 without --allow-warnings; axioms were kept line for line as approved, no {:axiom} added.)

##### Correspondence table (wt/Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs, T1 S3)
| Source lines | Dafny | Note |
| 123-131 RequireUserInTenantAsync | RequireUserInTenant | same branch order; throw -> Refused(NoCall) / Refused(OneGetUserCall); GetUserAsync exception -> GuardThrew; null or TenantId != tenantId -> Refused; else Accepted(user) |
| 133-134 UserNotInTenant | comment / Refused | PermissionDenied modelled as Refused |
| 138-141 IsCanonicalUserPk | IsCanonicalUserPk | int.TryParse(NumberStyles.None, Invariant) via IntTryParseNone axiom; pk>0; pk.ToString == userId via Int32ToString. No deviation. |

##### Members
- RequireUserInTenantAsync: found, :123-131, changed: no
- UserNotInTenant: found, :133-134, changed: no
- IsCanonicalUserPk: found, :138-141, changed: no

##### Statuses
{"P1_NonCanonicalRefusedWithoutCall": "proved", "P2_MismatchRefused": "proved", "P3_MutationsUseCheckedPk": "proved", "P4_CanonicalPkReachesTheIdp": "proved"}

No failed or unproved obligations; no witnesses.

#### `IdpAdminClient.TenantReads`

Source: Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs ListUsersByTenantAsync (148–206), GetUserAsync (214–238), ReadPk (376–382), TenantIdOf (385–390)

The prover's return (member status, correspondence table, statuses):

##### IdpAdminClient.TenantReads return

Verifier: 32 verified, 0 errors (no repair rounds). Body identical to start.dfy; only the third header line changed ("Replay base" -> "Code ref: 8ef84917...").

##### Members (IdpAdminClient.cs in the worktree at 8ef849174092218f507041e82b8185facafb1937; all introduced by T1 S3)
- ListUsersByTenantAsync: found, 148-206; changed: no
- GetUserAsync: found, 214-238; changed: no
- ReadPk: found, 376-382; changed: no
- TenantIdOf: found, 385-390; changed: no

##### Correspondence (C# line -> Dafny function, comment-tagged line by line in the .dfy)
- 152-155 filter/first path -> ListUsersByTenant
- 157-205 while loop, 159-164 request/parse -> Loop
- 166-176 foreach, 168 tenant check, 172-175 IdpUser -> Collect
- 186-202 pagination/next/GetInt32/path -> Loop (nested matches)
- 214-238 -> GetUser
- 376-382 -> ReadPk; 385-390 -> TenantIdOf, TenantIdOfThrows
No deviations; line numbers in the start model's comments match the built file.

##### Statuses
{"P1_EveryPageCarriesTheFilter":"proved","P2_OnlyTheTenantsUsersReturned":"proved","P3_NotFoundIsNull":"proved","P4_ReturnedRecordHasTheRequestedPk":"proved","P5_OtherFailureThrows":"proved"}

No failed or unproved obligations; no witnesses.

#### `TenantAdminGrpcService.InviteUser`

Source: Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs InviteUser (29–53), RequireActiveTenantAsync (106–114)

The prover's return (member status, correspondence table, statuses):

(dafny verify: 12 verified, 0 errors)

Correspondence table (TenantAdminGrpcService.cs, T3 S3):
- 31 `await RequireActiveTenantAsync` -> InviteUser `var t := RequireActiveTenant(env,c)`; None = PermissionDenied, effects []
- 34 `TryReserveAsync(tenantId, DateOnly.FromDateTime(DateTime.UtcNow))` -> `UtcToday(env)`, `Reserve` effect, `TryReserve` axiom
- 35-36 ResourceExhausted throw -> InviteResult(ResourceExhausted,[reserve])
- 40-44 CreateUserAsync -> CreateUser effect; throw -> InviteThrew (CreateUserThrows axiom)
- 45-52 audit/return -> Ok (audit ignored)
- 106-114 RequireActiveTenantAsync -> RequireActiveTenant: null claim 108-109, status null/suspended/deleted 111-112, return 113
Deviations: none; the code as built matches the starting model. Only the header changed (Code ref replaces Replay base).

Members: InviteUser found 29-53, changed: no. RequireActiveTenantAsync found 106-114, changed: no.

{"P1_LimitReachedRefusesWithoutCallingTheIdp":"proved","P2_ReservesTheCallersTenantTodayFirst":"proved","P3_InactiveTenantReservesAndCreatesNothing":"proved"}
No failed or unproved obligations; 0 repair rounds.

#### `InviteQuotaRepository.TryReserveAsync`

Source: Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs TryReserveAsync (27–37)

The prover's return (member status, correspondence table, statuses):

##### Return: InviteQuotaRepository.TryReserveAsync

Verify: 11 verified, 0 errors (exit 0). No axioms; no warnings.

##### Correspondence table (wt = worktree, introduced by T3 S3)
| Worktree range | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs:27-37 (TryReserveAsync, `is not null`) | Reserve (l.66-71), QuerySingleOrDefault (l.54-57) | `QuerySingleOrDefaultAsync<int?>(...) is not null` -> `.Some?`; thrown statement folded into returned=false, row unchanged |
| :30-35 SQL upsert (INSERT ... ON CONFLICT DO UPDATE ... WHERE Count < 100 RETURNING) | UpsertStatement (l.34-52) | NoRow -> Row(1); Count<100 -> +1 (int4 overflow Raised, dead path); else no row returned |
| :37 parameters `new { TenantId, Day }` | per-key model state s | model is per (TenantId, Day) key |
Deviations: none; the built code equals the starting model's transliteration. Header: "Replay base" line replaced by "Code ref" line.

##### Per-member
- TryReserveAsync: found, Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs:27-37; changed from starting model: no.

##### Statuses
{"P1_AtMostHundredReserved": "proved", "P2_HundredFirstRefused": "proved", "P3_CountMonotone": "proved"}

No failed or unproved obligations; no repair rounds used.

#### `RateLimitInterceptor.Enforce`

Source: Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs AdminServices, _limiter, _adminLimiter (13–37), Enforce (58–74)

The prover's return (member status, correspondence table, statuses):

##### RateLimitInterceptor.Enforce return


Dafny: 17 verified, 0 errors (exit non-zero only for the expected bodyless-axiom warning). Only edit to start.dfy: header line 3 "Replay base" -> "Code ref: 8ef84917...".

##### Correspondence table (worktree Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs, introduced T2 S4)
| C# lines | Dafny |
|---|---|
| 13-17 AdminServices (descriptor FullNames; proto `package iverson;` + service names confirmed in tenant_admin.proto:3,7 / tenant_lifecycle.proto:3,7) | const AdminServices, TenantAdminService, TenantLifecycleService |
| 19-27 _limiter, PermitLimit 50_000 | GeneralPermitLimit, AcquireGeneral |
| 29-37 _adminLimiter, PermitLimit 30, key (Service, Subject) | AdminPermitLimit, AcquireAdmin |
| 60 subject ?? "unknown" | Enforce `subject` |
| 63-64 matched endpoint service | Enforce `matched` (c.routed) |
| 65 FirstOrDefault | FirstOrDefault |
| 66-68 lease selection | Enforce `lease` |
| 69-73 reject/throw | Enforce acquired=false |
Deviations: window/segments/queue and lease disposal abstracted by the approved axiom; LogWarning side-effect-free.

##### Members
- AdminServices, _limiter, _adminLimiter: found 13-37, changed no
- Enforce: found 58-74, changed no

##### Statuses
{"P1_ThirtyFirstRefusedOthersPass":"proved","P2_SeparateBuckets":"proved","P3_AnotherCallersBucketUntouched":"proved","P4_OtherRpcsNeverSpendTheAdminBucket":"proved"}

#### `OperatorAuthorizationPolicy.IsSatisfiedBy`

Source: Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs IsSatisfiedBy (14–25); with SchemaAdminAuthorizationPolicy.cs IsSatisfiedBy (11–18), ConsoleAdminAuthorizationPolicy.cs IsSatisfiedBy (10–12), OperatorSubjects.cs FromConfiguration (21–25), and the unchanged TenantAdminAuthorizationPolicy.IsSatisfiedBy

The prover's return (member status, correspondence table, statuses):

##### Return

Verifier: 30 verified, 0 errors (no repair rounds needed).

##### Correspondence table (worktree paths under Iverson.Server/Iverson.Api/, all introduced by T4 S3)
| Worktree | Dafny | Note |
|---|---|---|
| OperatorAuthorizationPolicy.cs 14-25 | IsOperator | same branch order; no deviation |
| SchemaAdminAuthorizationPolicy.cs 11-18 (built: signature at 12, body to 18) | IsSchemaAdmin | no deviation |
| ConsoleAdminAuthorizationPolicy.cs 10-12 | IsConsoleAdmin | no deviation |
| OperatorSubjects.cs 21-25 | FromConfiguration | HashSet(Ordinal) -> set; `{Length: > 0}` -> |fb.value| > 0; no deviation |
| TenantAdminAuthorizationPolicy.cs 5 | IsTenantAdmin | unchanged |

##### Members
- OperatorAuthorizationPolicy.IsSatisfiedBy: found, lines 14-25; changed from starting model: no
- SchemaAdminAuthorizationPolicy.IsSatisfiedBy: found, lines 12-18; changed: no
- ConsoleAdminAuthorizationPolicy.IsSatisfiedBy: found, lines 10-12; changed: no
- OperatorSubjects.FromConfiguration: found, lines 21-25; changed: no
- TenantAdminAuthorizationPolicy.IsSatisfiedBy: found, line 5; changed: no

##### Statuses
{"P1_OperatorImpliesSchemaAdmin":"proved","P2_GroupsIrrelevantWithoutFallback":"proved","P3_AdminScopeIsOperator":"proved","P4_OperatorImpliesConsoleAdmin":"proved","P5_FallbackGroupIsSchemaAdmin":"proved","P6_ListedSubjectIsOperator":"proved","P7_ConsoleAdminIsOperatorOrTenantAdmin":"proved","P8_EmptyFallbackIsUnset":"proved"}

No failed or unproved obligations; no witnesses.

#### `TokenRevocationCache.IsRevokedAsync`

Source: Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs IsRevokedAsync (35–42)

The prover's return (member status, correspondence table, statuses):

Correspondence (T5 S3; Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs):
- 26 Skew = 5 s -> SkewTicks (dfy const)
- 33 Snapshot record -> datatype Snapshot (LoadedAt omitted; unread)
- 35-37 signature + CurrentSnapshotAsync -> IsRevoked params (s is the snapshot)
- 40 TryGetValue -> `sub !in s.RevokedAt then Revoked(false)`
- 41 issuedAt is null -> Revoked(true); `issuedAt <= revokedAt + Skew` -> AddTimeSpan (throws past MaxValue -> RevokedThrew) then compare
No deviations; the built code matches the starting model.

Member IsRevokedAsync: found, TokenRevocationCache.cs:35-42; changed from starting model: no (only header line 3 updated to Code ref).

{"P1_WithinFiveSecondsRevoked":"proved","P2_Boundary":"proved","P3_NoRowNotRevoked":"proved"}

No failed or unproved obligations. dafny: 9 verified, 0 errors.

#### `StreamRevalidationInterceptor.RecheckAsync`

Source: Iverson.Server/Iverson.Api/Grpc/StreamRevalidationInterceptor.cs RecheckAsync (45–55), RevalidatingServerStreamWriter.WriteAsync ×2 (75–85); with Tenancy/TokenRevocationCheck.cs IsRevokedAsync (13–24)

The prover's return (member status, correspondence table, statuses):

##### Return: StreamRevalidationInterceptor.RecheckAsync

Verifier: 20 verified, 0 errors (exit 2 only from the bodyless-axiom warnings).

##### Correspondence table (worktree paths under Iverson.Server/Iverson.Api/; all T5 S19)
| Worktree file:line | Dafny lines | Note |
|---|---|---|
| Tenancy/TokenRevocationCheck.cs 13-24 IsRevokedAsync | 72-84 (FindFirst sub 16 -> 74; null sub 17-18 -> 75; long.TryParse 20 -> 77; FromUnixTimeSeconds 21 -> 79-80; cache call 23 -> 81/83) | No deviation. The synchronous throw from FromUnixTimeSeconds is CheckThrew, same as start. |
| Grpc/StreamRevalidationInterceptor.cs 45-55 RecheckAsync | 94-114 (47 -> 97-99; 48 -> 102-104; 49 -> 99/104; 51 -> 107; 52-53 -> 109-111; 54 -> 112; 55 -> 113) | No deviation. `is null or "suspended" or "deleted"` binds to the await result only, as modelled. |
| Grpc/StreamRevalidationInterceptor.cs 75-79 WriteAsync(T) | 122-134 (else branch 129-133) | No deviation. |
| Grpc/StreamRevalidationInterceptor.cs 81-85 WriteAsync(T, ct) | 122-134 (then branch 124-128) | No deviation. |

##### Members
- RecheckAsync: found, Grpc/StreamRevalidationInterceptor.cs:45-55; changed from starting model: no
- RevalidatingServerStreamWriter.WriteAsync x2: found, Grpc/StreamRevalidationInterceptor.cs:75-79 and 81-85; changed: no
- TokenRevocationCheck.IsRevokedAsync: found, Tenancy/TokenRevocationCheck.cs:13-24; changed: no

Only edit to start.dfy: the "Replay base" header line became "Code ref: 8ef849174092218f507041e82b8185facafb1937".

##### Statuses
{"P1_NoWriteAfterRevocationOrSuspension": "proved", "P2_RevokedEndsUnauthenticated": "proved", "P3_InactiveTenantEndsPermissionDenied": "proved", "P4_BothWriteFormsRecheckFirst": "proved"}

No failed or unproved obligations; 0 repair rounds used.

#### `TenantStatusCache.InvalidateAll`

Source: Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs KeyFor, GetStatusAsync, Invalidate, InvalidateAll (36–52)

The prover's return (member status, correspondence table, statuses):

##### TenantStatusCache.InvalidateAll return

Verify result: 49 verified, 0 errors.

##### Correspondence table (worktree file Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs, all introduced by T5 S7)
| C# lines | Dafny | Note |
|---|---|---|
| 36 KeyFor | KeyFor (+ LongToString/NatToString/DigitChar) | long modelled as int (2^63 increments unreachable); TooLong outcome for >CLR string length; same as starting model |
| 40-42 | GetStatusBegin (Key/Hit/Miss/Threw) | unchanged |
| 44-47 | GetStatusFinish (status = repository result as parameter; cache.Set at 46) | unchanged |
| 50 Invalidate | CacheInvalidate | unchanged |
| 52 InvalidateAll | CacheInvalidateAll | unchanged |

Built code is identical in behaviour to the starting model; only the header changed (Replay base line replaced by Code ref).

##### Members
- KeyFor: found, lines 36; changed: no
- GetStatusAsync: found, lines 38-48; changed: no
- Invalidate: found, line 50; changed: no
- InvalidateAll: found, line 52; changed: no

##### Statuses
{"P1_ReadBeforeInvalidateAllNeverServedAfter": "proved", "P2_FirstReadAfterInvalidateAllGoesToTheRepository": "proved"}

No failures, no unproved obligations; no repair rounds used.

#### `AuthInvalidationListener.ExecuteAsync`

Source: Iverson.Server/Iverson.Api/Tenancy/AuthInvalidationListener.cs ExecuteAsync, InvalidateCaches (43–97)

The prover's return (member status, correspondence table, statuses):

##### AuthInvalidationListener.ExecuteAsync return

Verify: 47 verified, 0 errors.

Correspondence (worktree AuthInvalidationListener.cs, T5 S15; line numbers identical to the start model's comments):
- 40-41 FirstRetryDelay/MaxRetryDelay -> consts FirstRetryDelay/MaxRetryDelay (ticks, Int64). No deviation.
- 43-46 ExecuteAsync entry, retryDelay = First -> Run. No deviation.
- 47-86 loop -> Loop/TryBlock; 53-55 -> Stopped/OpenOrListenFails/Listen; 58 -> InvalidateBoth; 59 -> rd := FirstRetryDelay; 61-62 + 52 handler -> RepeatAction(InvalidateBoth, n); 64-67 -> returned; 68-75 -> fall through; 77-84 -> Delay/Return; 85 -> NextRetryDelay (wrapping long mul, Math.Min). No deviation.
- 93-97 InvalidateCaches -> InvalidateBoth. No deviation.
Only the file header changed (Code ref replaces Replay base).

Members: ExecuteAsync found 43-87, changed no; InvalidateCaches found 93-97, changed no.

{"P1_ListeningWriteInvalidates":"proved","P2_NoWriteMissedAcrossReconnect":"proved","P3_BackoffFromOneToThirtySeconds":"proved"}
No failed or unproved obligations. Approved axioms: none.

#### `EngagementHealthChecker.CheckHealthAsync`

Source: Iverson.Server/Iverson.StarRocks/EngagementHealthChecker.cs CheckHealthAsync (12–30), ClassifyConnectionException (42–45), CreateBackendProbe (60–61), AnyBackendReadyAsync (73–83)

The prover's return (member status, correspondence table, statuses):

##### EngagementHealthChecker.CheckHealthAsync return

(start.dfy with only the header changed: line 3 "Replay base" -> "Code ref: 8ef849174092218f507041e82b8185facafb1937"; body, axiom, lemmas untouched.)

##### Correspondence table (worktree EngagementHealthChecker.cs, all introduced by T15 S4)
| cs lines | Dafny lines | note |
|---|---|---|
| 12-30 CheckHealthAsync (open 17, SELECT 1 20, probe 22-24, catch 26-28) | 79-88 CheckHealth | none; `await using` disposal not modelled (no outcome effect) |
| 42-45 ClassifyConnectionException | 47-52 | none |
| 56-61 CreateBackendProbe / BackendProbeSql / timeout | 54-57 | none; timeout appears as ProbeFails(CommandTimeout) in the environment |
| 73-83 AnyBackendReadyAsync (rows 78, loop 79-80, return 82) | 59-76 CountLoop, AnyBackendReady | deviation: `CancellationToken ct = default` is not modelled (no effect on the result; the starting model already omitted it); `rows++` modelled as wrapping Inc32 (unchecked) |

##### Members
- CheckHealthAsync: found, EngagementHealthChecker.cs:12-30, changed: no
- ClassifyConnectionException: found, :42-45, changed: no
- CreateBackendProbe: found, :60-61, changed: no
- AnyBackendReadyAsync: found, :73-83, changed: no

##### Statuses
{"P3_Counterexample": "proved", "P1_AuthPendingOnlyFromRefusedLogin": "proved", "P2_RowsMeanHealthy": "proved", "P3_HealthyIsExactlyTwoRows": "unproved"}

Verifier: 17 verified, 1 error (exit 4; the bodyless-axiom warning is expected).

##### Unproved: P3_HealthyIsExactlyTwoRows (the accepted P3)
Last Dafny error: `EngagementHealthChecker.CheckHealthAsync.dfy(161,21): Error: a postcondition could not be proved on this return path` (postcondition at line 158).
Cause: the property is false in the model as in the code. `rows` is a C# int incremented unchecked, so 4,294,967,298 rows wrap it to 2; P3_Counterexample (proved) shows CheckHealth(HealthEnv(StepOk, StepOk, Rows(0x1_0000_0002))) == Healthy while the probe is not Rows(2). Repair rounds tried: 0 (no repair can prove a refuted property; the frozen text may not change). No witness test was written: it would need a reader yielding 2^32+2 rows, infeasible against the real code, and the plan-stage status was unproved and accepted by the user.

#### `HarnessChannel.Create`

Source: Iverson.Server/Iverson.ClientConformance/HarnessChannel.cs CheckEndpoints (20–21), Create (28–37); with the unchanged Iverson.LoadTest/Auth/CredentialEndpoints.cs Check (24–53)

The prover's return (member status, correspondence table, statuses):

##### HarnessChannel.Create return

Only the "Replay base" line became the "Code ref: 8ef84917..." line; the transliteration matches the built code with no other change. Dafny: 32 verified, 0 errors (one warning: the approved bodyless StartsWithOrdinalIgnoreCase axiom lacks {:axiom}, kept verbatim as given).

##### Correspondence (all introduced by T23 S3; the CredentialEndpoints.cs file is unchanged)
| Worktree (Iverson.Server/...) | Dafny |
|---|---|
| Iverson.ClientConformance/HarnessChannel.cs:20-21 CheckEndpoints | CheckEndpoints (Check(.., true)) |
| HarnessChannel.cs:28-37 Create | Create; callCredentials not modelled (passed through; start model already omitted it); tls via StartsWithOrdinalIgnoreCase(grpcUrl,"https://"); UnsafeUseInsecureChannelCallCredentials = !tls, Credentials = SecureSsl/Insecure |
| Iverson.LoadTest/Auth/CredentialEndpoints.cs:24-28 | Check branch 1 |
| :30-34 | Check branch 2 |
| :36-37 | Check branch 3 |
| :42-45 | Check branch 4 |
| :47-49 | Check final branch (AuthentikBaseUrl :59-61, IsHttpsOrLoopback :66-67) |
| :51 | None |
Deviation: AuthentikBaseUrl's range throw is proved unreachable (helper lemmas) rather than modelled as an outcome in Check.

##### Members
- CheckEndpoints: found, HarnessChannel.cs:20-21, changed: no
- Create: found, HarnessChannel.cs:28-37, changed: no
- CredentialEndpoints.Check: found, Iverson.LoadTest/Auth/CredentialEndpoints.cs:22-52, changed: no

##### Statuses
{"P1_TheSchemePicksTheTransport": "proved", "P2_CredentialsGoOnlyToHttpsOrLoopback": "proved"}

No failed or unproved obligations; 0 repair rounds.

#### `Auth.BuildInvoker`

Source: Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs BuildInvoker (24–74)

The prover's return (member status, correspondence table, statuses):

##### Auth.BuildInvoker return

Verifier: "9 verified, 0 errors" (exit 2 only from the expected bodyless-axiom warning).

##### Correspondence table (Auth.cs, T23 S4)
| Auth.cs | Dafny | Note |
|---|---|---|
| 35 `tls = grpcUrl.StartsWith("https://", OrdinalIgnoreCase)` | `var tls := StartsWithOrdinalIgnoreCase(grpcUrl, HttpsPrefix)` | approved axiom |
| 36 transport | `var transport := if tls then SecureSsl else Insecure` | |
| 37-42 UnsafeUseInsecureChannelCallCredentials = !tls | `var unsafeInsecure := !tls` | |
| 50-58 serviceToken branch | `InvokerPlan(Some(transport), unsafeInsecure)` | closure bodies not modelled |
| 59-71 trio branch | `InvokerPlan(Some(transport), unsafeInsecure)` | |
| fall-through (no Credentials) | `InvokerPlan(None, unsafeInsecure)` | |
| 73-74 ForAddress / Intercept | not modelled | Deviation: `actingToken` parameter dropped (only feeds ActingUserInterceptor); carried by the starting model already, not a signature change. `serviceToken = null` default written as a plain parameter. ForAddress throwing on a malformed URL is not modelled (as in starting model). |

##### Member status
- Auth.BuildInvoker: found, Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs:24-74; changed from starting model: no.

##### Statuses
{"P1_TheSchemePicksTheTransport": "proved"}

No failed or unproved obligations; 0 repair rounds.

#### `conformance.grpcUsesTLS`

Source: Iverson.Clients/Go/conformance/main.go transportOption, grpcUsesTLS (248–259)

The prover's return (member status, correspondence table, statuses):

(header's "Replay base" line replaced by the required "Code ref" line; otherwise identical to start.dfy.)

Correspondence table (worktree Iverson.Clients/Go/conformance/main.go, T23 S4):
- 248-253 transportOption -> Dafny function transportOption (if grpcUsesTLS then TlsCreds else InsecureCreds); the two grpc.DialOption values are modelled as DialTransport constructors. No deviation.
- 256-258 grpcUsesTLS -> Dafny function grpcUsesTLS = StringsHasPrefix(StringsToLower(addr), HttpsBytes). strings.HasPrefix modelled as len>=n && s[:n]==prefix; strings.ToLower is the approved axiom. No deviation.

Members: transportOption found 248-253, changed no; grpcUsesTLS found 256-258, changed no.

Statuses: {"P1_TlsExactlyForAnHttpsScheme": "proved", "P2_DialOptionFollowsTheScheme": "proved"}
Dafny: 12 verified, 0 errors (exit non-zero only from the expected bodyless-axiom warning). 0 repair rounds. No witnesses, no unproved.

#### `driver.parse_grpc_address`

Source: Iverson.Clients/Python/conformance/driver.py parse_grpc_address (342–346), build_driver_channel (349–391)

The prover's return (member status, correspondence table, statuses):

##### Return: driver.parse_grpc_address


Verifier: 16 verified, 0 errors (only the expected bodyless-axiom warning, exit 2).

##### Correspondence table (worktree 8ef84917, T23 S4)
| driver.py | Dafny | Note |
|---|---|---|
| 342-346 parse_grpc_address (345 urlsplit with "//" prefix; 346 hostname/port/scheme) | ParseGrpcAddress | no deviation; ParseRaised models port ValueError |
| 369-381 call_creds building | CallCredsCount | no deviation |
| 386-391 channel choice | BuildDriverChannel | no deviation |

Only the header changed from the starting model (Replay base line replaced by Code ref line).

##### Members
- parse_grpc_address: found, driver.py:342-346, changed: no
- build_driver_channel: found, driver.py:349-391, changed: no

##### Statuses
{"P1_BareHostPortIsPlaintext": "proved", "P2_HttpsSchemeIsTls": "proved", "P3_ChannelTransportFollowsUseTls": "proved"}

No failures, no unproved obligations.

#### `driver.parseGrpcAddress`

Source: Iverson.Clients/TypeScript/conformance/driver.ts parseGrpcAddress (240–252)

The prover's return (member status, correspondence table, statuses):

(Only the header changed: "Replay base" line replaced by "Code ref: 8ef8491...". Transliteration unchanged from start.dfy.)

Correspondence table (driver.ts, T23 S4; all lines at 8ef84917):
- 240 signature -> func ParseGrpcAddress (dfy ~44); return object/throw -> datatype ParsedAddr | Throws
- 241 withScheme (includes '://' else http://+addr) -> RangeError guard + var withScheme
- 242-247 try new URL / catch throw -> UrlParse None -> Throws
- 248 !url.hostname -> |url.hostname|==0 -> Throws
- 249 port ternary -> NumberFromString(url.port) / "443" / "80"
- 250 !isInteger || <=0 -> !NumberIsPositiveInteger -> Throws
- 251 return {host, port, useTls} -> ParsedAddr(...)
Deviations: none (RangeError on oversized concatenation modelled as Throws, as in start model).

Member parseGrpcAddress: found, driver.ts:240-252, changed from starting model: no.

{"P1_TlsExactlyForAnHttpsUrl": "proved"}
Dafny: 9 verified, 0 errors (exit non-zero only from the approved bodyless-axiom warning).

#### `Driver.channelTransport`

Source: Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java the scheme test and channel builder (180–186)

The prover's return (member status, correspondence table, statuses):

##### Driver.channelTransport return


Verify: 9 verified, 0 errors (exit 2 only from the expected bodyless-axiom warning).

##### Correspondence table
| Worktree range (T23 S4) | Dafny | Note |
|---|---|---|
| Driver.java:181 `if ("https".equalsIgnoreCase(grpcUri.getScheme()))` | ChannelTransport, `isHttps` match / `if isHttps` | null scheme -> None -> false (String.equalsIgnoreCase(null) is false). No deviation. |
| Driver.java:182 `channelBuilder.useTransportSecurity()` | `TransportSecurity` branch | builder modelled as resulting transport |
| Driver.java:183-184 `else { channelBuilder.usePlaintext(); }` | `Plaintext` branch | |
| Driver.java:180, 186 builder creation / `.intercept(capture).build()` | not modelled | transport-neutral |

##### Members
- Driver.java scheme test and channel builder (180-186): found, Driver.java:180-186; changed from starting model: no (only the header's Code ref line replaced the Replay base line).

##### Statuses
{"P1_TlsExactlyForAnHttpsScheme": "proved"}

#### `roles.RequireRole`

Source: Iverson.AdminUI/src/auth/roles.tsx RolesProvider state (40–52), hasRole (55–61), RequireRole (74–112)

The prover's return (member status, correspondence table, statuses):

##### roles.RequireRole return


##### Correspondence table (all T6 S4; Iverson.AdminUI/src/auth/roles.tsx at 8ef84917)
| Worktree lines | Dafny | Note |
|---|---|---|
| 40-52 (RolesProvider state; 44-49 the ternary) | ProviderState | usePolledResource data/failure as Option; no deviation |
| 55-61 hasRole | HasRole | no deviation |
| 63-67 DENIED | DeniedOperator/TenantAdmin/Either, DeniedMessage | strings as CsString with length asserts |
| 74-112 RequireRole (83 checking, 91 unavailable, 101 denied, 111 children) | RequireRoleRender | JSX modelled as Rendered variant; same branch order |

##### Members
- RolesProvider state: found roles.tsx:40-52, changed: no
- hasRole: found roles.tsx:55-61, changed: no
- RequireRole: found roles.tsx:74-112, changed: no

##### Statuses
{"P1_NoRoleBeforeTheAnswer":"proved","P2_RolesAreTheServersAnswer":"proved","P3_GuardRendersByState":"proved","P4_ProviderState":"proved"}

Dafny: 11 verified, 0 errors. No witnesses, no unproved obligations. Repair rounds: 0. Header's "Replay base" line replaced by the required "Code ref" line.

#### `blueprints-configmap.orchestrator-tokenreview`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml the iverson-admin-orchestrator-tokenreview expression (141–168)

The prover's return (member status, correspondence table, statuses):

##### Return: blueprints-configmap.orchestrator-tokenreview

Only change from start.dfy: header line 3 is now the Code ref line. Verify: 19 verified, 0 errors.

##### Correspondence (T8 S3, worktree blueprints-configmap.yaml -> Dafny TryBody/Pin)
- 147 try / 167-168 except Exception: return False -> Pin (Exc => false)
- 148-149 oauth_jwt falsy -> False -> jwt/Truthy branch, Ok(false)
- 150 POST["client_assertion"] (KeyError) -> formAssertion.None? => Exc
- 151-159 requests.post(timeout=2) raising -> PostRaised => Exc (json= body irrelevant)
- 160-161 status outside 2xx -> False -> Ok(false)
- 162 response.json().get("status") or {} -> json.None? => Exc; DictGet; OrEmpty
- 164 status.get("authenticated") is True -> a != PyBool(true) => Ok(false)
- 165 (status.get("user") or {}).get("username") == "system:serviceaccount:NS:REL-api" -> DictGet/OrEmpty, n == PyStr(expected)
No deviations.

##### Member
iverson-admin-orchestrator-tokenreview expression: found, blueprints-configmap.yaml:147-168 (expression body; block 141-168 region); changed from starting model: no.

##### Statuses
{"P1_SuccessAdmitted":"proved","P2_ErrorsRefused":"proved","P3_WrongIdentityRefused":"proved","P4_NoFederatedJwtRefused":"proved","P5_MissingAssertionRefused":"proved"}

No failed or unproved obligations; no witnesses.

#### `blueprints-configmap.orchestrator-workload`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml the iverson-admin-orchestrator-workload expression (100–118)

The prover's return (member status, correspondence table, statuses):

Correspondence (blueprints-configmap.yaml, T8 S3; built lines, expression block at 101-118):
- 102 jwt = ... or {} -> WorkloadExpr `jwt`; non-dict truthy jwt -> Raised at `.get` (the `!jwt.PyDict?` branch)
- 103-107 aud/auds/iat/exp/k8s -> `aud`, `auds`, `iat`, `exp`, `k8s` lets
- 108 ak_user_by -> AkUserBy (approved axiom)
- 110 iss == "{{ $issuer }}" -> StrEq(DictGet(IssKey), issuer)
- 111 sub == system:serviceaccount:NS:REL-api -> ExpectedSub(ns, rel)
- 112 audience in auds, 113 isinstance int (bool counts) -> ListHasStr, IsInt
- 114 exp - iat <= 600 -> IntVal difference
- 115 "pod" in k8s -> PyContainsStr (TypeError -> Raised)
- 116 user is not None -> user.None?
- 117 ak_is_group_member -> AkIsGroupMember (approved axiom)
Same branch order as the starting model. No deviations; the built code equals the plan-stage transliteration. Only change to the file: line 3 is "Code ref: 8ef849174092218f507041e82b8185facafb1937".

Member: the iverson-admin-orchestrator-workload expression - found, blueprints-configmap.yaml:101-118 (expression body 102-118); changed from starting model: no.

Statuses: {"P1_AnotherIssuerRefused": "proved"}
dafny verify: 10 verified, 0 errors. No witnesses; nothing unproved; 0 repair rounds.

#### `secret-service-clients.admin-automation-workload`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml the iverson-admin-automation-workload expression (775–787)

The prover's return (member status, correspondence table, statuses):

(Header replaced by the four required lines; body is the starting model unchanged. Verified: 9 verified, 0 errors.)

Correspondence table (secret-service-clients.yaml in worktree, introduced by T8 S3; line numbers are the file's):
| yaml lines | Dafny |
| 775 `expression: |` | AdminAutomationExpr |
| 776 jwt = ... or {} | `var jwt := ... OrEmptyDict` |
| 777 aud = jwt.get("aud") | `aud := DictGet(jwt.m, AudKey)`; non-dict -> Raised |
| 778 auds = ... | `auds := if aud.PyList? ...` |
| 779-780 iat, exp | `iat`, `exp` DictGet |
| 782 iss == "{{ $issuer }}" | `StrEq(DictGet(.., IssKey), issuer)` |
| 783 sub == system:serviceaccount:ns:rel-admin-automation | `ExpectedSub(ns, rel)` |
| 784 audience in auds | `ListHasStr(auds, AdminAutomationAudience)` |
| 785 isinstance int (bool counts) | `IsInt` |
| 786 exp - iat <= 600 | `IntVal(exp) - IntVal(iat) <= 600` |
Deviations: none; the built code equals the plan-stage model (iss compare against rendered issuer already present). Short-circuit order kept.

Member: the iverson-admin-automation-workload expression: found, secret-service-clients.yaml:776-787 (775 is the `expression: |` line); changed from starting model: no.

Statuses: {"P1_AnotherIssuerRefused": "proved"}

No failed or unproved obligations.

#### `secret-service-clients.console-admins`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml the iverson-console-admins expression (352–358)

The prover's return (member status, correspondence table, statuses):

Correspondence table
| Worktree range (T9 S3) | Dafny | Note |
| secret-service-clients.yaml:352-358 (policy block; expression at 357-358) | const TenantAdminsGroup; function ConsoleAdmins | No deviation. Built return line is 358 (was 357 in the plan); text identical. Python or short-circuits on bool operands = Dafny ||; "in list" = sequence membership. |
| :355 name iverson-console-admins | (identifier only) | |

Members: iverson-console-admins expression: found, secret-service-clients.yaml:352-358; changed from starting model: no (only header line 3 and comment line numbers).

{"P1_TenantAdminAdmitted":"proved","P2_ListedOperatorAdmitted":"proved","P3_EveryoneElseRefused":"proved"}

No failures, nothing unproved. dafny: 5 verified, 0 errors.

#### `compose-service-clients.console-admins`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml the iverson-console-admins expression (194–199)

The prover's return (member status, correspondence table, statuses):

Verify: 5 verified, 0 errors.

Correspondence: service-clients.yaml:199 (T9 S3; expression block 194-199, policy id console-admins line 194, name line 196) -> ComposeConsoleAdmins (Dafny, `||` of two AkIsGroupMember calls with literals "tenant-admins", "operators"). No deviations. Header line 3 changed to the Code ref line; body unchanged from start.dfy.

Member: iverson-console-admins expression: found, service-clients.yaml:194-199 (return at 199); changed from starting model: no.

Statuses: {"P1_ComposeAdmitsTheTwoGroups": "proved"}
No failed or unproved obligations.

#### `recovery-flow.login-throttle`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml the iverson-login-throttle expression (71–79)

The prover's return (member status, correspondence table, statuses):

##### Return: recovery-flow.login-throttle

(identical to start.dfy except the third header line, now `// Code ref: 8ef849174092218f507041e82b8185facafb1937`.)

##### Correspondence table (recovery-flow.yaml, T9 S3, as built at 8ef84917)
| Worktree lines | Dafny |
|---|---|
| 74 `ip = str(ak_client_ip)` | Throttle `ip := clientIp` |
| 75 `user = request.user` | PendingUser parameter |
| 76 identifiers list comprehension (truthy username, email) | Identifiers, Truthy |
| 77 pair filter(ip, identifier__in).aggregate(Sum) or 0 | FilterPair, SqlSum, OrZero |
| 78 address filter(ip, score__lt=0).aggregate(Sum) or 0 | FilterNeg, SqlSum, OrZero, AddressAggregate |
| 79 `pair <= -5 or address <= -50` | Throttle result |
| 72-73 imports | none (library boundary) |
No deviations from the starting model.

##### Members
- iverson-login-throttle expression (71-79): found, recovery-flow.yaml:71-79; changed from starting model: no.

##### Statuses
{"P1_KnownWrongPasswordsFromZero":"proved","P2_ThreeWrongCodesReachMinusFive":"proved","P3_SuccessesFirstOffset":"proved","P4_InterleavedNoFixedBound":"proved","P5_UnknownThirdPlanRefused":"proved","P6_AddressCapNeedsTenIdentifiers":"proved","P7_EmailRowsCount":"proved","P8_OtherAddressesNeverCount":"proved"}

dafny verify: 45 verified, 0 errors. Only a warning (bodyless approved axiom ClampedAdd, as in the start model; {:axiom} not added) which makes the CLI exit non-zero without --allow-warnings.
No failed or unproved obligations; no witnesses. Repair rounds: 0.

#### `deployment-server.wait-for-migration`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml the wait-for-migration init container's python -c program (64–121)

The prover's return (member status, correspondence table, statuses):

Only the "Code ref" header line changed from start.dfy; the transliteration is identical (the built program matches the starting model line for line, same line numbers).

Correspondence (deployment-server.yaml, T7 S3, as built; Dafny unchanged from start.dfy):
- 79, 100, 116, 118 authentik_version / build_hash -> types Version, Build (approved library boundary)
- 84-88 LOCK_1000, 104-106 -> DbState.lock1000; Migrated lock branch
- 90-93 migrated() connect + SET search_path -> comment on Migrated
- 94-96 -> !db.tableExists then false
- 97-103 -> (v,b) !in db.rows then false
- 107-111 -> else !db.unapplied (migration_plan empty)
- 113-121 while True loop -> WaitFrom / Wait (Raised = except branch, keeps waiting)
No deviations.

Member status: wait-for-migration python -c program (64-121): found, deployment-server.yaml:64-121, changed: no.

Statuses: {"P1_MigratedExactly":"proved","P2_WaitEndsAtTheFirstMigratedAnswer":"proved","P3_WaitObservesTheEndOfThePass":"proved"}
(dafny: 9 verified, 0 errors; exit 2 only from the expected bodyless-axiom warning.)
No failures, no unproved obligations.

#### `admission-policy.validations`

Source: Iverson.Server/deploy/helm/iverson/templates/admission-policy.yaml variables and validations (43–126), with matchConstraints (35–42) and the binding (128–138)

The prover's return (member status, correspondence table, statuses):

##### admission-policy.validations: code-stage return

##### 1. Model


Result: `Dafny program verifier finished with 78 verified, 0 errors`, with no contradictory-assumption warnings, under `--verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction` and MemoryMax=1500M. All nine obligations proved on the first run, with no repair rounds. The model has no `assume`, `expect`, `{:axiom}`, `{:verify false}`, `{:induction}`, `include` or modules. The four header lines are exactly as the brief gives them, with `Code ref: 8ef849174092218f507041e82b8185facafb1937`. The frozen property text is unchanged.

##### 2. Correspondence table

The worktree file is `Iverson.Server/deploy/helm/iverson/templates/admission-policy.yaml` at 8ef84917. It was introduced by T11 S3 (d23ce136) and changed by the final-review fixes b9b8bb23 and 8ef84917. A longer header comment and the `$connectPrefixes` helper (:31) push every line number down from the plan's. The `// Source:` line keeps the plan's ranges, as the brief requires; the built ranges are given below.

| Worktree lines | Introduced by | Dafny lines | Notes / deviations |
|---|---|---|---|
| :31 `$connectPrefixes` (Helm) | T11 S3; factored out in b9b8bb23 | 235–239 `ConnectPrefixes` | Helm renders the 4-prefix list once and reuses it in connectPod and proxyTarget. Same value as before. |
| :38 `failurePolicy: Fail`, :152 `validationActions: [Deny]` | T11 S3 | 437–441 `Admits` (Validation 427–435) | An error or a false result denies (unchanged). |
| :40–46 resourceRules | T11 S3; :42 changed in b9b8bb23 | 178–197 `MatchesRule` | **Changed:** the CONNECT rule (:42) now also matches `pods/proxy` and `services/proxy` (new constants `ProxySub`, `ServicesResource` at 57–58). `apiVersions ["v1"]` is not modelled (as before). |
| :153–156 binding namespaceSelector | T11 S3 | 199–203 `MatchesPolicy` | Unchanged. |
| :48–49 protected | T11 S3 | 209–213 `VProtected` | Unchanged. |
| :50–51 operator | T11 S3 | 215–220 `VOperator` | Unchanged. |
| :52–53 node | T11 S3 | 222–227 `VNode` | Unchanged. |
| :54–55 res, :56–57 sub | T11 S3 | 229–233 | Unchanged. `r.sub` holds the value of `has(...) ? ... : ''`. |
| :59–60 connectPod | T11 S3 | 241–245 `VConnectPod` | Unchanged. |
| :63–66 proxyName | b9b8bb23; condition widened in 8ef84917 | 247–255 `VProxyName` | **New.** `res != 'pods' \|\| sub != 'proxy' ? '' : (split(':').size() == 3 ? [1] : [0])`. `split` is modelled as `SplitOn` (125–136), which follows cel-go ext.Strings / Go `strings.Split` for a one-code-point separator: `""` gives `[""]`, and every occurrence splits. Its helper contract `|result| >= 1` is proved. A concrete run matched Go: `"h:x:8"` gave `[h,x,8]`, `":"` gave `["",""]`, `"::x:"` gave `["","","x",""]`. `size()` is modelled as `SizeEq` (109–115) and `[i]` as `IndexS` (117–123), where an out-of-range index or a list error gives an error. |
| :69–73 proxyTarget | b9b8bb23; reworked in 8ef84917 | 257–263 `VProxyTarget` | **New.** `sub == 'proxy' && (res == 'services' \|\| (res == 'pods' && prefixes.exists(p, proxyName.startsWith(p))))`. The `exists` is modelled as `ExistsStartsWithS` (138–144): an error in proxyName errors every element of the non-empty list, so the result is an error. |
| :75–79 podSa | T11 S3 | 265–278 | Unchanged. |
| :81–87 newTplSa, :88–94 oldTplSa | T11 S3 | 280–314 | Unchanged. |
| :95–100 imagesChanged | T11 S3 | 316–342 | Unchanged. |
| :105–108 (a) | T11 S3 | 351–360 `GuardA`/`ValA` | Unchanged. |
| :110–114 (b) | T11 S3; changed in b9b8bb23 | 362–372 `GuardB`/`ValB` | **Changed:** the guard is now `(pods && sub in [exec,attach,portforward] && connectPod) \|\| proxyTarget`. It refuses a non-operator's pods/proxy to a gated pod name and every non-operator services/proxy in the namespace. |
| :116–124 (c) | T11 S3 | 374–392 | Unchanged. |
| :126–135 (d) | T11 S3 | 394–403 | Unchanged. |
| :137–140 (e) | T11 S3 | 405–414 | Unchanged. |
| :142–144 (f) | T11 S3 | 416–425 (f at 421–425) | Unchanged. |
| messageExpressions (:108, 114, 124, 135, 140, 144) | T11 S3 / b9b8bb23 | none | Not modelled (as before): a messageExpression does not change the admit/deny decision. |

The helper lemmas (535–599) and the proofs of P1–P9 (603–795) are unchanged from the starting model. They needed no new steps, because CEL's error-absorbing `\|\|` admits an operator through (b) whatever proxyTarget evaluates to, and `sub == 'proxy'` is false for every request the other properties name.

##### 3. Per member

| Member (as named on `// Source:`) | Status | Worktree file:line | Code changed from starting model |
|---|---|---|---|
| variables and validations (43–126) | found | admission-policy.yaml:47–144 | yes: proxyName (:63–66), proxyTarget (:69–73) and rule (b) guard (:111–112) |
| matchConstraints (35–42) | found | admission-policy.yaml:39–46 | yes: :42 adds `pods/proxy`, `services/proxy` |
| the binding (128–138) | found | admission-policy.yaml:146–156 | no |

##### 4. Statuses

```json
{"P1_OperatorsAdmitted": "proved", "P2_ClusterAdminDenied": "proved", "P3_MetadataOnlyPodUpdateAdmitted": "proved", "P4_KubeletMintsTokens": "proved", "P5_ScaleAdmitted": "proved", "P6_NoProtectedPodOrTemplateForNonOperators": "proved", "P7_MisnamedProtectedPodRefusedForAll": "proved", "P8_AdmittedProtectedPodIsGated": "proved", "P9_ApiReplicaSetPodsAdmitted": "proved"}
```

There are no failed or unproved obligations, so there are no witnesses.

##### 5. Sanity check on the new paths

These lemmas ran in a deleted scratch copy and are not part of the deliverable:
- a non-operator's `services/proxy` to any name is refused (proved);
- an operator's is admitted (proved, via P1);
- a non-operator's `pods/proxy` to `http:<rel>-api-x:80` is refused. This was proved under a scratch `assume` that fixes the split result, so the new rule (b) branches are reachable in the model.

#### `configmap-tls-proxy.public-locations`

Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml the :9080 server block's locations (72–115)

The prover's return (member status, correspondence table, statuses):

##### Return: configmap-tls-proxy.public-locations


Verify result: 53 verified, 0 errors. The only diagnostic is the bodyless-ensures warning on the approved axiom FlowRegexMatches, which is given verbatim.

##### Member status
- The :9080 server block's locations: found, configmap-tls-proxy.yaml:72-115 (T8 S3). Changed from starting model: no (only the header's third line changed from "Replay base" to "Code ref").

##### Correspondence table
| Worktree lines | Dafny | Note |
|---|---|---|
| yaml:89 regex location (if/flow|executor, five slugs, \z) | FlowRegexMatches axiom, Route phase 3 | no deviation |
| yaml:90-92 exact flows locations | Route exact branches | none |
| yaml:93-94 /static/, /media/ | LongestPrefixOutcome | none |
| yaml:95-104 = /ws/client/ | Route WsClient | none |
| yaml:105-111 OAuth exact locations | Route OAuth* branches | none |
| yaml:112 = /-/health/live/ | Route HealthLive | none |
| yaml:113 = / return 302 | Route Root -> Redirect | none |
| yaml:114 location / return 404 | LongestPrefixOutcome fallthrough NotFound | none |
| yaml:73-88 listen/ssl/headers | not modelled (no routing effect) | none |

##### Statuses
{"P1_RootRedirects":"proved","P2_SetupNotForwarded":"proved","P3_InterfacesRefused":"proved","P4_AllowedFlowsForwarded":"proved","P5_OtherApiRefused":"proved","P6_WebSocketAndLiveForwarded":"proved"}

No failed or unproved obligations; no witnesses. Repair rounds: 0.

#### `job-create-user.create-user`

Source: Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml the create-user container's sh script (62–111)

The prover's return (member status, correspondence table, statuses):

(identical to start.dfy except line 3, now the "Code ref: 8ef8491..." line in place of "Replay base".)

Correspondence table (YAML = Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml, as built; unchanged vs. plan-stage model; no deviations)
- 62-65 set -e / FE_HOST / FE_REPLICAS / BE_REPLICAS (T15 S4 4c, T19 S3 3c) -> JobCalls (dfy 111-114)
- 71-76 TLS, fe() (T15 S4 4c) -> Fe (99-102); TLS flags are not modelled (mysql is the approved axiom)
- 77-87 until probe loop (ROOT_PW then no pw) -> ProbeLoop (117-133)
- 93-94 fe SET PASSWORD; ROOT_HAS_PW=1 (T19 S3 3c) -> SetPasswordStep (136-144)
- 95-98 until BE/FE count loop (T19 S3 3d) -> WaitLoop (148-160), ViewAfterSet (105-108)
- 99-111 batch fe -e (T15 S4 4c, T19 S3 3e) -> BatchStep (163-166), BatchLines/consts (66-92); lines 100-110 map to elements 0-10
Lines 112-164 (outside the 62-111 range) not modelled.

Members: create-user sh script: found, YAML:62-111, changed: no

Statuses:
{"P1_JobSetsPasswordOnFe0":"proved","P2_BatchOnlyAfterEveryNodeListed":"proved","P3_RevokeOperateAfterCreateUser":"proved","P4_PasswordAfterSetPassword":"proved"}

Dafny: "44 verified, 0 errors". Note: the command exits 2 with "Compilation failed because warnings were found" because the three approved MysqlRun ensures clauses are bodyless-function warnings (suppressible only by {:axiom}, not added since axioms were to be copied line for line); same as the starting model.
No failed or unproved obligations; 0 repair rounds.

#### `setup.sh.guards`

Source: Iverson.Server/deploy/kind/setup.sh the kind version gate (4–11), the --only parser (44–52), ambient_enroll (106–115), the --only dispatch (136–140)

The prover's return (member status, correspondence table, statuses):

(identical to start.dfy except header line 3 "Code ref: 8ef8491..."; SortVFirst is verbatim the approved text, no {:axiom}). Verify: 71 verified, 0 errors; the bodyless-function warning makes dafny exit 2, as accepted by the coordinator.

Correspondence table (worktree setup.sh -> Dafny; introducing step; no deviations from the start model):
- 4-11 kind gate (T16 S3 3c): KIND_MIN/KindGate/KindVersionLine, dfy ~lines 196-225. Line 7 awk $2 -> StripTrailingNL(AwkPrint2); line 8 sort -V|head -n1 -> SortVFirst axiom with CutAtFirst(.,DASH) for ${%%-*}; 9-10 -> Refused; 11 -> Passed.
- 44-52 --only parser (T14 S4 4d): PHASES, Phases, ParseAndDispatch (dfy ~230-300). 46 $#>0; 47 the four-way refuse test (HasSpaceClass, Occurs(Pad(PHASES),Pad($2))) -> UsageExit2 (48-49); 51 ONLY="$2" -> DispatchOnly(args[1]).
- 106-115 ambient_enroll (T17 S5 5a-5d): Enroll (dfy ~305-325). 108 policies; 109 ports=" ... "; 110 test via StripShortestStarLit; 111-112 -> false; 114 label -> true.
- 136-140 dispatch (T20 S3 3d-3e): DispatchOnly + CallFunction (phase_ names at 54, 83, 117, 122); 138 ${ONLY//-/_} -> ReplaceAll; 139 exit 0; absent function -> OtherExit(127).

Members:
- kind version gate 4-11: found, setup.sh:4-11, changed: no
- --only parser 44-52: found, setup.sh:44-52, changed: no
- ambient_enroll 106-115: found, setup.sh:106-115, changed: no
- --only dispatch 136-140: found, setup.sh:137-140 (136 blank), changed: no

Statuses: {"P1_KindGate": "proved", "P2_OnlyRunsOnePhase": "proved", "P3_EnrolOnlyWith15008": "proved"}
No failed or unproved obligations; 0 repair rounds.

#### `setup.ps1.guards`

Source: Iverson.Server/deploy/kind/setup.ps1 the kind version gate (3–9), the --only parser (43–51), Add-AmbientLabels (96–110), the dispatch (144–152)

The prover's return (member status, correspondence table, statuses):

##### setup.ps1.guards return

Verifier: 69 verified, 0 errors (only the two expected bodyless-axiom warnings).

##### Correspondence table (worktree Iverson.Server/deploy/kind/setup.ps1 @8ef84917 -> Dafny)
| setup.ps1 | task/step | Dafny | note |
|---|---|---|---|
| 3-9 kind version gate | T16 S3 (3c) | KindGatePs (l.206-223) | no deviation; throws are outcomes |
| 43-51 --only parser | T14 S4 (4d), T17 S5 | ParseAndDispatchPs (l.230-244) | no deviation; -or short-circuit, -notin as ContainsPs |
| 96-110 Add-AmbientLabels | T20 S3 (3f-3i), T17 S5 (5e-5h) | EnrollPs (l.269-279) | no deviation |
| 144-152 dispatch | T14 S4 | DispatchOnlyPs (l.247-259) | no deviation; NoArmMatchedExit0 added (no default arm) |

##### Members
- kind version gate: found, setup.ps1:3-9, changed: no
- --only parser: found, setup.ps1:43-51, changed: no
- Add-AmbientLabels: found, setup.ps1:96-110, changed: no
- dispatch: found, setup.ps1:144-152, changed: no

##### Statuses
{"P1_KindGate": "proved", "P2_OnlyRunsOnePhase": "proved", "P3_EnrolOnlyWith15008": "proved"}

No failed or unproved obligations. 0 repair rounds. Only change from start.dfy: line 3 now "// Code ref: 8ef849174092218f507041e82b8185facafb1937".

#### `generate-compose-secrets.renew`

Source: scripts/generate-compose-secrets.sh LEAVES, the renew decision and the regenerate branch (53–89)

The prover's return (member status, correspondence table, statuses):

##### generate-compose-secrets.renew return

Verify: 34 verified, 0 errors.

##### Correspondence table (worktree scripts/generate-compose-secrets.sh, all introduced by T22 S3; unchanged from start model)
| Worktree lines | Dafny | Note |
|---|---|---|
| 52-59 LEAVES | consts Stem*/Rest*, LEAVES | no deviation |
| 60-61 renew=no; ca.crt missing | Renew | no deviation |
| 62-68 loop, read -r stem _ (63), tests 64-65, renew=yes (66) | ReadStem, RenewLoop | openssl -checkend modelled as Fs.expiring (no axiom) |
| 69-89 regenerate branch (writes at 76-77, 80-84) | LeafWrites, Regenerate, Writes, TlsNames, Written | chmod/echo/mkdir/trap write no file; openssl failures exit 1, not modelled |

Members: renew decision (60-69) found, changed no; regenerate branch (69-89) found, changed no; LEAVES (53-59) found, changed no. Only the header differs (Code ref line replaces Replay base).

##### Statuses
{"P1_RenewWhenAnyMissingOrExpiring": "proved", "P2_RenewRewritesEverythingButTheCaKey": "proved"}

No failed or unproved obligations.

#### `fe-tls-entrypoint.settings`

Source: Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh the append-each-setting-once loop (17–24)

The prover's return (member status, correspondence table, statuses):

##### Return: fe-tls-entrypoint.settings

(dafny verify: 60 verified, 0 errors; no repair rounds used)

##### Member status
- Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh append-each-setting-once loop: found, lines 17-24 (T22 S4). Changed from starting model: no (only the header's "Replay base" line became "Code ref").

##### Correspondence table (worktree wt/Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh, T22 S4)
| Script lines | Dafny | Note |
| 12, 14 (keystore, password) | KEYSTORE, PASSWORD consts (25-27) | identical bytes |
| 17 conf= | parameter `conf` of Step/Loop/Append | file content modelled as seq<Byte> |
| 18 word list | Settings (159-162), LIT_* consts | no deviation |
| 19 key=${setting%% =*} | StripLongestSuffixSpEqStar (90-96), Step 173 | no deviation |
| 20 grep -aEq | GrepAEq/EreMatch/MatchAt (66-106), Step 174 | -a: lines split at newline only |
| 21 [ -s ] && [ tail -c1 | wc -l -eq 0 ] ; echo >> | Line21 (165-168), TestS, TailC1, WcL, CommandSubst, TestEqZero | no deviation |
| 22 echo "$setting" >> | Step 176 (c1 + setting + [NL]) | |
| 23-24 fi/done | Step else-branch 177, Loop 181-185 | |
| 25 exec | not modelled (outside candidate) | |

##### Statuses
{"P1_EachSettingOnce": "proved", "P2_ExistingLinesKept": "proved"}

No failed or unproved obligations.

#### `authentik-probes.helpers`

Source: Iverson.Server/deploy/scripts/authentik-probes.sh edge_client_from_xff, ip_is, is_loopback, is_console, setup_shows_no_prompt (60–92), the option parser (216–227)

The prover's return (member status, correspondence table, statuses):

##### authentik-probes.helpers return

Verify: 47 verified, 0 errors (no contradictory-assumption warnings). Only line 3 differs from start.dfy (Replay base -> Code ref 8ef84917...).

##### Correspondence table (all introduced by T25 S3; file Iverson.Server/deploy/scripts/authentik-probes.sh)
| Worktree lines | Dafny lines | Note |
|---|---|---|
| 60-66 edge_client_from_xff | 126-137 EdgeClient | no deviation; bash intmax_t arithmetic via Wrap64, `read -a` via ReadArrayComma |
| 76 ip_is | 141 IpIs | none |
| 78-83 is_loopback | 153 IsLoopback | case globs as prefix/equality |
| 86 is_console | 160 IsConsole | CONSOLE_URL modeled as a parameter |
| 90-92 setup_shows_no_prompt | 172 SetupShowsNoPrompt | `*initial-setup*` as substring |
| 42-43, 220-227 option parser | 179-199 OptionsOutcome/OptionsLoop/Options | lines 216-218 (`return 0` when sourced) are the sourcing guard, not modeled; model is the executed script; usage side effect omitted |

##### Members
- edge_client_from_xff: found 60-66; changed from start: no
- ip_is: found 76; no
- is_loopback: found 78-83; no
- is_console: found 86; no
- setup_shows_no_prompt: found 90-92; no
- option parser: found 216-227 (loop 220-227); no

##### Statuses
{"P1_EdgeClient":"proved","P2_IpIs":"proved","P3_IsLoopback":"proved","P4_IsConsole":"proved","P5_SetupShowsNoPrompt":"proved","P6_Options":"proved"}

No failed or unproved obligations; no witnesses. Repair rounds: 0.

#### `admission-check.classify`

Source: Iverson.Server/deploy/scripts/admission-check.sh classify (52–60), the option parser (186–197)

The prover's return (member status, correspondence table, statuses):

(dafny: 19 verified, 0 errors; exit 2 only from the expected bodyless-axiom warning)

Correspondence table (worktree admission-check.sh at 8ef84917 -> Dafny)
- 55-63 classify (T25 S3 as introduced; lines shifted from 52-60 by earlier edits) -> Classify, dfy 52-61. 56 -> rc==0 branch; 58 -> GrepQF(out, policy); 60-61 -> ErrorWord. No deviation.
- 219-221 sourced-guard (return 0) -> not modelled (not taken when run as a script); noted in the comment above Options.
- 223-230 option parser (while/case) -> Options/OptionsLoop, dfy 68-96. 225 --release, 226 --namespace, 227 -h|--help (usage; exit 0), 228 * (exit 2). No deviation.
- Defaults RELEASE=iverson / NS=iverson at lines 41/42 -> DefaultRelease/DefaultNs.
- Final-review additions (proxy_outcome, expect_proxy, N10/O11) are not part of the candidate; only shifted line numbers.

Per member
- classify (52-60 in plan; built 55-63): found; changed from starting model: no (logic identical, only line comments updated)
- option parser (186-197 in plan; built 223-230, guard 219-221): found; changed: no (comments only)

Statuses
{"P1_Classify": "proved", "P2_Options": "proved"}

No failed or unproved obligations.

#### `mesh-check.mtls_seen`

Source: Iverson.Server/deploy/scripts/mesh-check.sh mtls_seen (70–76), the option parser (107–118)

The prover's return (member status, correspondence table, statuses):

##### mesh-check.mtls_seen return


##### Correspondence table
| Worktree range (task/step) | Dafny | Note |
|---|---|---|
| mesh-check.sh 70-76 mtls_seen (T25 S3) | MtlsSeen (predicate) | lines 71-75 map to s1..s4 filters + GrepQF; identical to the starting model, no deviation |
| 107-109 sourced guard (T25 S3) | comment above Options | returns before the parser when sourced; the command path always reaches 111 |
| 36-37 RELEASE/NS defaults | Options -> OptionsLoop(args, DefaultName, DefaultName) | unchanged |
| 111-118 option parser (T25 S3) | OptionsLoop | 113 --release, 114 --namespace (need a value else usage; exit 2), 115 -h/--help exit 0, 116 default exit 2; usage only writes stderr, so not modelled |

##### Members
- mtls_seen: found, mesh-check.sh:70-76, changed from starting model: no
- option parser: found, mesh-check.sh:107-118 (loop at 111-118), changed: no

##### Statuses
{"P1_MtlsSeen": "proved", "P2_Options": "proved"}

dafny verify: 32 verified, 0 errors. The only diagnostic is the warning that the approved bodyless axiom GrepQF has no {:axiom} attribute (the axiom text is frozen); with that warning dafny exits 2 unless --allow-warnings.

No witnesses; nothing unproved. Repair rounds: 0.

#### `mesh-check.UNENROLLED_PY`

Source: Iverson.Server/deploy/scripts/mesh-check.sh UNENROLLED_PY (59–66)

The prover's return (member status, correspondence table, statuses):

##### mesh-check.UNENROLLED_PY return

Verify: 16 verified, 0 errors.

Correspondence (T25 S3; all rows no deviation from the starting model):
- mesh-check.sh:61 -> Comprehension (filter phase == "Running")
- mesh-check.sh:62 -> Unenrolled, `out := [Decimal(|pods|)]`
- mesh-check.sh:63-65 -> ForLoop (redirection != "enabled" prints name)
- mesh-check.sh:59-60,66 -> shell quoting / import; not modelled
Note: Pod reduced to fields read (name, phase, redirection); missing-key KeyError paths (metadata.name, status) not modelled, as in the starting model.

Member UNENROLLED_PY: found, mesh-check.sh:59-66; changed from starting model: no.

Statuses: {"P1_RunningCountThenUnenrolled": "proved"}

#### `kind-checks.alive-wait`

Source: $LIVE/kind-checks.sh alive() (its embedded Python) and the at-most-60 wait loop (not found: kept from the starting model)

The prover's return (member status, correspondence table, statuses):

Correspondence table: none; the member is not found, so the starting model is kept unchanged (only header lines: Replay base line replaced by Code ref, Source line names the status).
Members: $LIVE/kind-checks.sh alive() and the wait loop: not found (no kind-checks.sh in the worktree at 8ef84917; only the docs/formal .dfy of the same name exists). Changed from starting model: no.
Statuses: {"P1_AliveColumn": "proved", "P2_WaitBounded": "proved"}
(dafny verify: 36 verified, 0 errors.) No failures, no unproved.

### Per obligation

| Candidate | Obligation | Citation | Status | Evidence |
|---|---|---|---|---|
| `TenantAdminGrpcService.RequireUserInTenantAsync` | P1_NonCanonicalRefusedWithoutCall | design TenantUserGuard.P1_NonCanonicalRefusedWithoutCall: "`RequireUserInTenantAsync` first requires `user_id` to be a canonical positive decimal integer; any other value is "not in this tenant" (`PermissionDenied`) with no IdP call." (§2.1 line 42) | proved | G5: line 39 `\|s\| > 0 && AllDigits(s) && s[0] != '0'` → `\|s\| >= 0 && AllDigits(s) && s[0] != '0'`; row `helper IsCanonicalPositiveDecimal` (gates/g5/P1_NonCanonicalRefusedWithoutCall.13.dfy) |
|  | P2_MismatchRefused | design TenantUserGuard.P2_MismatchRefused: "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (line 42); "uses `GetUserAsync` and compares `tenant_id`" (line 41); "404 maps to "not in this tenant"" (line 40) | proved | G5: line 130 `else if user.Null? \|\| user.record.TenantId != Some(tenantId) then` → `else if user.Null? \|\| user.record.TenantId == Some(tenantId) then`; row `P2_MismatchRefused (correctness)` (gates/g5/P2_MismatchRefused.38.dfy) |
|  | P3_MutationsUseCheckedPk | design TenantUserGuard.P3_MutationsUseCheckedPk: "RemoveUser and SetTenantAdmin pass the checked record's pk, not the raw request string, to `DeactivateUserAsync`, `AddGroupAsync` and `RemoveGroupAsync`." (line 42) | proved | G5: line 130 `else if user.Null? \|\| user.record.TenantId != Some(tenantId) then` → `else if user.Null? \|\| user.record.TenantId == Some(tenantId) then`; row `P3_MutationsUseCheckedPk (correctness)` (gates/g5/P3_MutationsUseCheckedPk.38.dfy) |
|  | P4_CanonicalPkReachesTheIdp | "`RequireUserInTenantAsync` (RemoveUser, SetTenantAdmin) uses `GetUserAsync` and compares `tenant_id`" (§2.1 line 41); test `RemoveUser_ReadsTheTargetByPk_AndNeverListsTheDirectory` (T1 S1 1f), generalised | proved | G5: line 33 `predicate IsDigit(c: char) { '0' <= c <= '9' }` → `predicate IsDigit(c: char) { '0' < c <= '9' }`; row `helper IntTryParseNone` (gates/g5/P4_CanonicalPkReachesTheIdp.1.dfy) |
| `IdpAdminClient.TenantReads` | P1_EveryPageCarriesTheFilter | "`ListUsersByTenantAsync` requests `GET /api/v3/core/users/?attributes=<JSON {"tenant_id": t}>` on **every** page." (§2.1 line 38); test `ListUsersByTenantAsync_AsksAuthentikForTheTenantsUsersOnly` (T1 S1 1b), generalised | proved | G5: line 238 `else if fuel == 0 then Unfinished(paths)` → `else if fuel != 0 then Unfinished(paths)`; row `helper LoopInvariants` (gates/g5/P1_EveryPageCarriesTheFilter.69.dfy) |
|  | P2_OnlyTheTenantsUsersReturned | "The client keeps its own check that each returned user's `attributes.tenant_id` equals the tenant, because Authentik returns the unfiltered list when filtering raises a `ValueError`" (§2.1 line 39); test `ListUsersByTenantAsync_DropsAUserOfAnotherTenant_EvenWhenAuthentikReturnsIt` | proved | G5: line 238 `else if fuel == 0 then Unfinished(paths)` → `else if fuel != 0 then Unfinished(paths)`; row `helper LoopInvariants` (gates/g5/P2_OnlyTheTenantsUsersReturned.69.dfy) |
|  | P3_NotFoundIsNull | "404 maps to "not in this tenant"." (§2.1 line 40); test `GetUserAsync_NotFound_ReturnsNull` | proved | G5: line 288 `else if ans.status == 404 then Null                                   // 218-219` → `else if ans.status != 404 then Null                                   // 218-219`; row `P3_NotFoundIsNull (correctness)` (gates/g5/P3_NotFoundIsNull.84.dfy) |
|  | P4_ReturnedRecordHasTheRequestedPk | "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (§2.1 line 42); test `GetUserAsync_ReturnedPkDiffersFromTheRequestedOne_ReturnsNull` | proved | G5: line 296 `if pk != Some(userId) then Null                                   // 227-228` → `if pk == Some(userId) then Null                                   // 227-228`; row `P4_ReturnedRecordHasTheRequestedPk (correctness)` (gates/g5/P4_ReturnedRecordHasTheRequestedPk.90.dfy) |
|  | P5_OtherFailureThrows | test `GetUserAsync_OtherFailure_ThrowsHttpRequestException` (T1 S1 1e), generalised | proved | G5: line 288 `else if ans.status == 404 then Null                                   // 218-219` → `else if ans.status != 404 then Null                                   // 218-219`; row `P5_OtherFailureThrows (correctness)` (gates/g5/P5_OtherFailureThrows.84.dfy) |
| `TenantAdminGrpcService.InviteUser` | P1_LimitReachedRefusesWithoutCallingTheIdp | "No row returned means over quota: `ResourceExhausted` ("the tenant's daily invitation limit is reached")." (§2.1 line 51); test `InviteUser_DailyLimitReached_ThrowsResourceExhaustedAndDoesNotCreateUser` | proved | G5: line 34 `s.Some? && s.value != Suspended && s.value != Deleted` → `s.Some? && s.value == Suspended && s.value != Deleted`; row `P1_LimitReachedRefusesWithoutCallingTheIdp (correctness)` (gates/g5/P1_LimitReachedRefusesWithoutCallingTheIdp.3.dfy) |
|  | P2_ReservesTheCallersTenantTodayFirst | "The api reserves a slot before calling Authentik" (§2.1 line 51); test `InviteUser_ReservesTodaysSlotForTheCallersTenant_BeforeCallingTheIdp` | proved | G5: `InviteResult(Ok, [reserve, CreateUser(tenantId)])` → `InviteResult(Ok, [CreateUser(tenantId), reserve])`; row `P2_ReservesTheCallersTenantTodayFirst (correctness)` (gates/g5/P2_ReservesTheCallersTenantTodayFirst.1.dfy) |
|  | P3_InactiveTenantReservesAndCreatesNothing | tests `InviteUser_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotCreateUser` and `InviteUser_NoTenantIdClaim_ThrowsPermissionDeniedAndDoesNotCreateUser`, generalised | proved | G5: line 34 `s.Some? && s.value != Suspended && s.value != Deleted` → `s.Some? && s.value == Suspended && s.value != Deleted`; row `P3_InactiveTenantReservesAndCreatesNothing (correctness)` (gates/g5/P3_InactiveTenantReservesAndCreatesNothing.3.dfy) |
| `InviteQuotaRepository.TryReserveAsync` | P1_AtMostHundredReserved | design InviteQuota.P1_AtMostHundredReserved: "`InviteUser` is limited to **100 per tenant per UTC day**." (§2.1 line 50) | proved | G5: line 85 `if n == 0 then 0` → `if n != 0 then 0`; row `helper ReservedCountIsMin` (gates/g5/P1_AtMostHundredReserved.18.dfy) |
|  | P2_HundredFirstRefused | design InviteQuota.P2_HundredFirstRefused: "the 101st invite of a tenant's day is refused across two api instances sharing Postgres" (§6 line 490) | proved | G5: line 42 `if c < 100 then` → `if c <= 100 then`; row `P2_HundredFirstRefused (correctness)` (gates/g5/P2_HundredFirstRefused.3.dfy) |
|  | P3_CountMonotone | design InviteQuota.P3_CountMonotone: "A reserved slot is not returned if the IdP call then fails." (§2.1 line 51) | proved | G5: line 45 `else Ran(Some((c as int + 1) as int32), Row((c as int + 1) as int32))` → `else Ran(Some((c as int + 1) as int32), Row((c as int + 2) as int32))`; row `helper CountAfterShape` (gates/g5/P3_CountMonotone.9.dfy) |
| `RateLimitInterceptor.Enforce` | P1_ThirtyFirstRefusedOthersPass | design RateLimitPartition.P1_ThirtyFirstRefusedOthersPass: "the 31st TenantAdmin call in a minute is refused while other RPCs pass" (§6 line 490) | proved | G5: line 52 `if \|services\| == 0 then None` → `if \|services\| != 0 then None`; row `helper FirstOrDefaultAdmin` (gates/g5/P1_ThirtyFirstRefusedOthersPass.9.dfy) |
|  | P2_SeparateBuckets | design RateLimitPartition.P2_SeparateBuckets: "partitioned by `(service, sub)`, so each of the two services has its own 30/min bucket per caller" (§2.1 line 45) | proved | G5: line 52 `if \|services\| == 0 then None` → `if \|services\| != 0 then None`; row `helper FirstOrDefaultLifecycle` (gates/g5/P2_SeparateBuckets.9.dfy) |
|  | P3_AnotherCallersBucketUntouched | test `One_callers_spent_TenantAdmin_budget_leaves_another_callers_untouched` (T2 S1 1a), generalised | proved | G5: line 52 `if \|services\| == 0 then None` → `if \|services\| != 0 then None`; row `helper FirstOrDefaultAdmin` (gates/g5/P3_AnotherCallersBucketUntouched.9.dfy) |
|  | P4_OtherRpcsNeverSpendTheAdminBucket | "`RateLimitInterceptor` keeps its 50,000/min per-`sub` limiter for every RPC except the `TenantAdminGrpcService` and `TenantLifecycleGrpcService` methods. Those go to a second limiter" (§2.1 line 45) | proved | G5: line 52 `if \|services\| == 0 then None` → `if \|services\| != 0 then None`; row `helper FirstOrDefaultOther` (gates/g5/P4_OtherRpcsNeverSpendTheAdminBucket.9.dfy) |
| `OperatorAuthorizationPolicy.IsSatisfiedBy` | P1_OperatorImpliesSchemaAdmin | design RoleFunctions.P1_OperatorImpliesSchemaAdmin: "`SchemaAdminAuthorizationPolicy`: `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`, which keeps "Operator implies SchemaAdmin"" (§2.4 line 111) | proved | G5: line 99 `if sub.Some? && sub.value in os.Subjects then` → `if sub.Some? \|\| sub.value in os.Subjects then`; row `helper IsOperator` (gates/g5/P1_OperatorImpliesSchemaAdmin.48.dfy) |
|  | P2_GroupsIrrelevantWithoutFallback | design RoleFunctions.P2_GroupsIrrelevantWithoutFallback: "The `groups` claim no longer yields Operator anywhere on Helm." (§2.4 line 114) with "API setting `Authorization:OperatorGroupFallback` (unset by default)" (line 119) | proved | G5: line 28 `if \|p\| == 0 then None` → `if \|p\| != 0 then None`; row `helper FindFirst` (gates/g5/P2_GroupsIrrelevantWithoutFallback.5.dfy) |
|  | P3_AdminScopeIsOperator | design RoleFunctions.P3_AdminScopeIsOperator: "Admin-automation keeps Operator through its `admin` scope." (§2.4 line 106) | proved | G5: line 28 `if \|p\| == 0 then None` → `if \|p\| != 0 then None`; row `helper FindFirst` (gates/g5/P3_AdminScopeIsOperator.5.dfy) |
|  | P4_OperatorImpliesConsoleAdmin | design RoleFunctions.P4_OperatorImpliesConsoleAdmin: "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69) | proved | G5: line 99 `if sub.Some? && sub.value in os.Subjects then` → `if sub.Some? \|\| sub.value in os.Subjects then`; row `helper IsOperator` (gates/g5/P4_OperatorImpliesConsoleAdmin.48.dfy) |
|  | P5_FallbackGroupIsSchemaAdmin | design RoleFunctions.P5_FallbackGroupIsSchemaAdmin: "(the list, the `admin` scope, or the compose fallback)" (§2.4 line 111) | proved | G5: line 44 `exists c :: c in p && c.ctype == t && c.value == v` → `exists c :: c in p && c.ctype != t && c.value == v`; row `helper GroupsAnyIffHasClaim` (gates/g5/P5_FallbackGroupIsSchemaAdmin.19.dfy) |
|  | P6_ListedSubjectIsOperator | "`OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` is true when the principal's `sub` is on the list or its `scope` contains `admin`." (§2.4 line 108); test `IsSatisfiedBy_SubjectOnTheList_ReturnsTrue` | proved | G5: line 28 `if \|p\| == 0 then None` → `if \|p\| != 0 then None`; row `helper FindFirst` (gates/g5/P6_ListedSubjectIsOperator.5.dfy) |
|  | P7_ConsoleAdminIsOperatorOrTenantAdmin | "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69); test `IsSatisfiedBy_SignedInUserWithNeitherRole_ReturnsFalse` | proved | G5: line 44 `exists c :: c in p && c.ctype == t && c.value == v` → `exists c :: c in p && c.ctype != t && c.value == v`; row `helper GroupsSelectIffHasClaim` (gates/g5/P7_ConsoleAdminIsOperatorOrTenantAdmin.19.dfy) |
|  | P8_EmptyFallbackIsUnset | "API setting `Authorization:OperatorGroupFallback` (unset by default)" (§2.4 line 119); test `FromConfiguration_NothingSet_IsAnEmptyListAndNoFallback` (`null`, `""`) | proved | G5: line 141 `if fb.Some? && \|fb.value\| > 0 then Some(fb.value) else None)` → `if fb.Some? \|\| \|fb.value\| > 0 then Some(fb.value) else None)`; row `P8_EmptyFallbackIsUnset (correctness)` (gates/g5/P8_EmptyFallbackIsUnset.68.dfy) |
| `TokenRevocationCache.IsRevokedAsync` | P1_WithinFiveSecondsRevoked | design StreamRecheck.P1_WithinFiveSecondsRevoked: "A token is revoked when `iat <= revoked_at + 5 s`. … A fresh login within 5 s of a revocation is therefore refused" (§2.3 line 90) | proved | G5: line 20 `if d as int + spanTicks <= MaxTicks then AddOk((d as int + spanTicks) as Ticks) else AddThrew` → `if d as int + spanTicks < MaxTicks then AddOk((d as int + spanTicks) as Ticks) else AddThrew`; row `P1_WithinFiveSecondsRevoked (correctness)` (gates/g5/P1_WithinFiveSecondsRevoked.1.dfy) |
|  | P2_Boundary | design StreamRecheck.P2_Boundary: "`TokenRevocationCacheTests.cs:50-52` moves from +1 s to +6 s, and a +5 s boundary test is added" (§2.3 line 90) | proved | G5: line 45 `case AddOk(limit) => Revoked(issuedAt.value <= limit)` → `case AddOk(limit) => Revoked(issuedAt.value < limit)`; row `P2_Boundary (correctness)` (gates/g5/P2_Boundary.9.dfy) |
|  | P3_NoRowNotRevoked | "A token is revoked when `iat <= revoked_at + 5 s`." (§2.3 line 90) | proved | G5: line 36 `if sub !in s.RevokedAt then Revoked(false)` → `if sub !in s.RevokedAt then Revoked(true)`; row `P3_NoRowNotRevoked (correctness)` (gates/g5/P3_NoRowNotRevoked.5.dfy) |
| `StreamRevalidationInterceptor.RecheckAsync` | P1_NoWriteAfterRevocationOrSuspension | design StreamRecheck.P3_NoWriteAfterRevocationOrSuspension: "Before each message it re-checks … the revocation of the primary principal's `sub` and of the acting user's `sub`; the status of the effective tenant. A revoked principal ends the stream with `Unauthenticated`. A suspended or deleted tenant ends it with `PermissionDenied`" (§2.3 lines 93–96) | proved | G5: line 26 `if \|p\| == 0 then None` → `if \|p\| != 0 then None`; row `helper FindFirst` (gates/g5/P1_NoWriteAfterRevocationOrSuspension.5.dfy) |
|  | P2_RevokedEndsUnauthenticated | "A revoked principal ends the stream with `Unauthenticated`." (§2.3 line 96); test `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage` | proved | G5: line 62 `if seconds < UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` → `if seconds <= UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew`; row `helper CheckAgreesWithRevoked` (gates/g5/P2_RevokedEndsUnauthenticated.10.dfy) |
|  | P3_InactiveTenantEndsPermissionDenied | "A suspended or deleted tenant ends it with `PermissionDenied` "Tenant '…' is not active.", as the unary paths do." (§2.3 line 96); test `ATenantNoLongerActive_EndsTheStreamPermissionDenied_AtItsNextMessage` | proved | G5: line 26 `if \|p\| == 0 then None` → `if \|p\| != 0 then None`; row `helper FindFirst` (gates/g5/P3_InactiveTenantEndsPermissionDenied.5.dfy) |
|  | P4_BothWriteFormsRecheckFirst | "The wrapper implements both `WriteAsync(T)` and `WriteAsync(T, CancellationToken)` and passes `WriteOptions` through" (§2.3 line 97); tests `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage` (both forms) and `NothingChanged_BothWriteFormsWriteThrough_PassingTheTokenAndTheWriteOptions` | proved | G5: line 110 `&& (var st := TenantStatus(env, tenantId.value);` → `\|\| (var st := TenantStatus(env, tenantId.value);`; row `helper Recheck` (gates/g5/P4_BothWriteFormsRecheckFirst.26.dfy) |
| `TenantStatusCache.InvalidateAll` | P1_ReadBeforeInvalidateAllNeverServedAfter | test `InvalidateAll_DuringARepositoryRead_TheReadsResultIsNotServedAfterwards` (T5 S5), generalised; "On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 line 85) | proved | G5: line 116 `s.(generation := s.generation + 1)` → `s.(generation := s.generation + 2)`; row `helper GenStep` (gates/g5/P1_ReadBeforeInvalidateAllNeverServedAfter.18.dfy) |
|  | P2_FirstReadAfterInvalidateAllGoesToTheRepository | test `InvalidateAll_EveryTenantReadsTheRepositoryAgain` (T5 S5), generalised | proved | G5: line 116 `s.(generation := s.generation + 1)` → `s.(generation := s.generation + 2)`; row `helper GenStep` (gates/g5/P2_FirstReadAfterInvalidateAllGoesToTheRepository.18.dfy) |
| `AuthInvalidationListener.ExecuteAsync` | P1_ListeningWriteInvalidates | design CacheInvalidation.P1_ListeningWriteInvalidates: "Every revocation write and every tenant-status write issues `pg_notify` in the same transaction as the write … On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 lines 84–85) | proved | G5: line 51 `if n == 0 then [] else [a] + RepeatAction(a, n - 1)` → `if n != 0 then [] else [a] + RepeatAction(a, n - 1)`; row `helper RepeatAction` (gates/g5/P1_ListeningWriteInvalidates.5.dfy) |
|  | P2_NoWriteMissedAcrossReconnect | design CacheInvalidation.P2_NoWriteMissedAcrossReconnect: "On every connect and reconnect it issues `LISTEN` first, then invalidates both caches. A notification sent while it was disconnected is not replayed." (§2.3 line 86) | proved | G5: line 153 `if \|s\| > 0 && s[\|s\| - 1] == Return then s[..\|s\| - 1] else s` → `if \|s\| > 0 && s[\|s\| - 1] != Return then s[..\|s\| - 1] else s`; row `helper ListenSession` (gates/g5/P2_NoWriteMissedAcrossReconnect.54.dfy) |
|  | P3_BackoffFromOneToThirtySeconds | "Reconnects back off from 1 s to 30 s." (§2.3 line 86) | proved | G5: line 61 `if n == 0 then 1 else 2 * Pow2(n - 1)` → `if n != 0 then 1 else 2 * Pow2(n - 1)`; row `helper RdAfter` (gates/g5/P3_BackoffFromOneToThirtySeconds.10.dfy) |
| `EngagementHealthChecker.CheckHealthAsync` | P3_Counterexample | (none in the frozen text) | proved | G5: line 75 `Ok(rows' == 2)                               // 82` → `Ok(rows' != 2)                               // 82`; row `P3_Counterexample (correctness)` (gates/g5/P3_Counterexample.14.dfy) |
|  | P1_AuthPendingOnlyFromRefusedLogin | design ReadinessMapping.P1_AuthPendingOnlyFromRefusedLogin: "With no grant needed, `AuthPending` now arises only from a refused login." (§3.5 line 317) (the precondition is VA45's fact that the probe needs no grant, so after a login no statement fails with the login error) | proved | G5: line 24 `env.selectOne == StepFails(AccessDenied) \|\| env.probe == ProbeFails(AccessDenied)` → `env.selectOne != StepFails(AccessDenied) \|\| env.probe == ProbeFails(AccessDenied)`; row `P1_AuthPendingOnlyFromRefusedLogin (correctness)` (gates/g5/P1_AuthPendingOnlyFromRefusedLogin.1.dfy) |
|  | P2_RowsMeanHealthy | design ReadinessMapping.P2_RowsMeanHealthy: "Two rows mean Healthy. The query needs no grant, returns rows with a backend alive, and fails in about 0.4 s ("Backend node not found") with none (Verified assumption 45)." (§3.5 line 316) | proved | G5: line 36 `if x == 0x7fff_ffff then -0x8000_0000 else x + 1` → `if x != 0x7fff_ffff then -0x8000_0000 else x + 1`; row `P2_RowsMeanHealthy (correctness)` (gates/g5/P2_RowsMeanHealthy.4.dfy) |
|  | P3_HealthyIsExactlyTwoRows | "Two rows mean Healthy." (§3.5 line 316) | unproved | refuted in the model: `rows` is an unchecked C# int, so 2^32+2 rows wrap to 2 and give Healthy (P3_Counterexample proves it); accepted unproved at plan stage (DPV-1/DPV-2); no witness (needs a reader yielding 2^32+2 rows) |
| `HarnessChannel.Create` | P1_TheSchemePicksTheTransport | "The harness (`ClientConformance/Program.cs:111-114`) and the five drivers (…) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446); test `Create_BuildsAChannelWhoseTransportMatchesTheScheme` (`https://`, `HTTPS://`, `http://`) | proved | G5: line 20 `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128`; row `helper IsAscii` (gates/g5/P1_TheSchemePicksTheTransport.3.dfy) |
|  | P2_CredentialsGoOnlyToHttpsOrLoopback | test `CheckEndpoints_RefusesAPlaintextNetworkGrpcUrlOrTokenEndpoint` ("IVERSON_GRPC_URL must be https:// or a loopback address"), generalised | proved | G5: line 122 `\|\| !(UriParse(grpcUrl).value.isLoopback \|\| (grpcMayUseTls && UriParse(grpcUrl).value.scheme == UriSchemeHttps))` → `\|\| !(UriParse(grpcUrl).value.isLoopback \|\| (grpcMayUseTls && UriParse(grpcUrl).value.scheme != UriSchemeHttps))`; row `P2_CredentialsGoOnlyToHttpsOrLoopback (correctness)` (gates/g5/P2_CredentialsGoOnlyToHttpsOrLoopback.42.dfy) |
| `Auth.BuildInvoker` | P1_TheSchemePicksTheTransport | "… the five drivers (`Auth.cs:52, 65`; …) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 13 `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128`; row `helper IsAscii` (gates/g5/P1_TheSchemePicksTheTransport.5.dfy) |
| `conformance.grpcUsesTLS` | P1_TlsExactlyForAnHttpsScheme | test `TestGrpcUsesTLSFollowsTheScheme` (T23 S1 1d: `https://`, `HTTPS://` true; `http://`, bare `host:port` false), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 12 `forall i :: 0 <= i < \|s\| ==> s[i] < 128` → `forall i :: 0 <= i <= \|s\| ==> s[i] < 128`; row `helper IsAscii` (gates/g5/P1_TlsExactlyForAnHttpsScheme.5.dfy) |
|  | P2_DialOptionFollowsTheScheme | as P1 | proved | G5: transportOption's `then TlsCreds` → `then InsecureCreds`; row `P2_DialOptionFollowsTheScheme (correctness)` (gates/g5/P2_DialOptionFollowsTheScheme.1.dfy) |
| `driver.parse_grpc_address` | P1_BareHostPortIsPlaintext | test `test_parse_grpc_address_takes_tls_from_the_scheme` (`localhost:8080` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 49 `var host := if parsed.hostname.Some? && \|parsed.hostname.value\| > 0 then parsed.hostname.value else Localhost;` → `var host := if parsed.hostname.Some? \|\| \|parsed.hostname.value\| > 0 then parsed.hostname.value else Localhost;`; row `helper ParseGrpcAddress` (gates/g5/P1_BareHostPortIsPlaintext.30.dfy) |
|  | P2_HttpsSchemeIsTls | test `test_parse_grpc_address_takes_tls_from_the_scheme` (`https://…` → `True`, `http://…` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 49 `var host := if parsed.hostname.Some? && \|parsed.hostname.value\| > 0 then parsed.hostname.value else Localhost;` → `var host := if parsed.hostname.Some? \|\| \|parsed.hostname.value\| > 0 then parsed.hostname.value else Localhost;`; row `helper ParseGrpcAddress` (gates/g5/P2_HttpsSchemeIsTls.30.dfy) |
|  | P3_ChannelTransportFollowsUseTls | test `test_build_driver_channel_takes_its_transport_from_use_tls` (`(True, acting) → ssl`, `(False, acting) → local`, `(True, None) → ssl`, `(False, None) → []`), generalised | proved | G5: line 75 `if callCreds == 0 then                                   // 386` → `if callCreds != 0 then                                   // 386`; row `P3_ChannelTransportFollowsUseTls (correctness)` (gates/g5/P3_ChannelTransportFollowsUseTls.58.dfy) |
| `driver.parseGrpcAddress` | P1_TlsExactlyForAnHttpsUrl | "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 34 `if \|sub\| <= \|s\| && s[..\|sub\|] == sub then true` → `if \|sub\| <= \|s\| \|\| s[..\|sub\|] == sub then true`; row `helper Contains` (gates/g5/P1_TlsExactlyForAnHttpsUrl.6.dfy) |
| `Driver.channelTransport` | P1_TlsExactlyForAnHttpsScheme | "… `Driver.java:177` … hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved | G5: line 15 `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128`; row `helper IsAscii` (gates/g5/P1_TlsExactlyForAnHttpsScheme.3.dfy) |
| `roles.RequireRole` | P1_NoRoleBeforeTheAnswer | tests "shows progress, not the page and not a refusal, while the roles are being read" and "says the roles could not be read when the request failed, rather than refusing" (T6 S2 2a), generalised; "replaced by a role guard over that response" (§2.2 line 78) | proved | G5: line 46 `if !state.Ready? then` → `if !state.Ready? then !`; row `P1_NoRoleBeforeTheAnswer (correctness)` (gates/g5/P1_NoRoleBeforeTheAnswer.7.dfy) |
|  | P2_RolesAreTheServersAnswer | tests "refuses an operator a tenant-admin page: the two roles answer different questions" and "admits an operator and a tenant admin alike to a page for either role"; "The landing page requires either role" (§2.2 line 79) | proved | G5: line 50 `case Either => state.roles.operator \|\| state.roles.tenantAdmin` → `case Either => state.roles.operator && state.roles.tenantAdmin`; row `P2_RolesAreTheServersAnswer (correctness)` (gates/g5/P2_RolesAreTheServersAnswer.9.dfy) |
|  | P3_GuardRendersByState | the `RequireRole` tests (T6 S2 2a), generalised | proved | G5: RequireRoleRender's `then ReadError` → `then Children`; row `P3_GuardRendersByState (correctness)` (gates/g5/P3_GuardRendersByState.1.dfy) |
|  | P4_ProviderState | tests "reads the roles once with the session's token and publishes them", "publishes 'checking' and reads nothing before the session has a token", "publishes 'unavailable' when the roles request fails"; "the last good answer is kept while a retry runs" is the code's own comment and not cited | proved | G5: ProviderState's `then Ready(data.value)` → `then Checking`; row `P4_ProviderState (correctness)` (gates/g5/P4_ProviderState.1.dfy) |
| `blueprints-configmap.orchestrator-tokenreview` | P1_SuccessAdmitted | design TokenReviewPin.P1_SuccessAdmitted: "It refuses (`invalid_grant`) on any exception, on any response status outside 2xx (a successful review answers 201)" (§2.6 line 175) | proved | G5: line 99 `if a != PyBool(true) then Ok(false)` → `if a == PyBool(true) then Ok(false)`; row `P1_SuccessAdmitted (correctness)` (gates/g5/P1_SuccessAdmitted.44.dfy) |
|  | P2_ErrorsRefused | design TokenReviewPin.P2_ErrorsRefused: same sentence (§2.6 line 175) | proved | G5: line 82 `if !Truthy(jwt) then Ok(false)` → `if !Truthy(jwt) then Ok(true)`; row `P2_ErrorsRefused (correctness)` (gates/g5/P2_ErrorsRefused.35.dfy) |
|  | P3_WrongIdentityRefused | design TokenReviewPin.P3_WrongIdentityRefused: "It requires `status.authenticated` true and `status.user.username` = `system:serviceaccount:<ns>:<rel>-api`." (§2.6 line 175) | proved | G5: line 99 `if a != PyBool(true) then Ok(false)` → `if a != PyBool(true) then Ok(true)`; row `helper PinReadsIdentity` (gates/g5/P3_WrongIdentityRefused.46.dfy) |
|  | P4_NoFederatedJwtRefused | "It keys on `context["oauth_jwt"]` and the request, not `request.user`, which is anonymous on a first exchange." (§2.6 line 176); test `TokenReviewExpression_ReviewsTheRawAssertion_AndRefusesEveryFailure` (`if not request.context.get("oauth_jwt"):`) | proved | G5: line 44 `case PyNone => false` → `case PyNone => true`; row `P4_NoFederatedJwtRefused (correctness)` (gates/g5/P4_NoFederatedJwtRefused.17.dfy) |
|  | P5_MissingAssertionRefused | "The raw assertion is read from the token request's form body." (§2.6 line 177) with "It refuses (`invalid_grant`) on any exception" (line 175); test `TokenReviewExpression_…` ("every step, the form read included, is inside the try") | proved | G5: line 82 `if !Truthy(jwt) then Ok(false)` → `if !Truthy(jwt) then Ok(true)`; row `P5_MissingAssertionRefused (correctness)` (gates/g5/P5_MissingAssertionRefused.35.dfy) |
| `blueprints-configmap.orchestrator-workload` | P1_AnotherIssuerRefused | "Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`." (§2.6 line 181) | proved | G5: line 76 `v == PyStr(s)` → `v != PyStr(s)`; row `helper IssuerMismatchRefusesEveryRelease` (gates/g5/P1_AnotherIssuerRefused.29.dfy) |
| `secret-service-clients.admin-automation-workload` | P1_AnotherIssuerRefused | "Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`." (§2.6 line 181); test `AdminAutomationPin_ComparesTheIssuer` (T8 S1 1a) | proved | G5: line 70 `v == PyStr(s)` → `v != PyStr(s)`; row `helper IssuerMismatchRefusesEveryRelease` (gates/g5/P1_AnotherIssuerRefused.29.dfy) |
| `secret-service-clients.console-admins` | P1_TenantAdminAdmitted | "It admits members of `tenant-admins` and subjects on the operator list (§2.4); everyone else gets Authentik's "Permission denied" at authorize." (§2.2 line 66) | proved | G5: line 32 `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) in subjects` → `AkIsGroupMember(env, user, TenantAdminsGroup) && UserUid(user) in subjects`; row `P1_TenantAdminAdmitted (correctness)` (gates/g5/P1_TenantAdminAdmitted.6.dfy) |
|  | P2_ListedOperatorAdmitted | "The expression is `ak_is_group_member(request.user, name="tenant-admins") or request.user.uid in [<list>]`." (§2.4 line 116) | proved | G5: line 32 `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) in subjects` → `AkIsGroupMember(env, user, TenantAdminsGroup) && UserUid(user) in subjects`; row `P2_ListedOperatorAdmitted (correctness)` (gates/g5/P2_ListedOperatorAdmitted.6.dfy) |
|  | P3_EveryoneElseRefused | "everyone else gets Authentik's "Permission denied" at authorize" (§2.2 line 66) | proved | G5: `\|\| UserUid(user) in subjects` → `\|\| UserUid(user) !in subjects` (ConsoleAdmins); row `P3_EveryoneElseRefused (correctness)` (gates/g5/P3_EveryoneElseRefused.1.dfy) |
| `compose-service-clients.console-admins` | P1_ComposeAdmitsTheTwoGroups | "The `operators` group and the bypass user's membership stay in `compose-only/service-clients.yaml`, together with a compose copy of the console sign-in policy that reads that group." (§2.4 line 120) | proved | G5: line 26 `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| AkIsGroupMember(env, user, OperatorsGroup)` → `AkIsGroupMember(env, user, TenantAdminsGroup) && AkIsGroupMember(env, user, OperatorsGroup)`; row `P1_ComposeAdmitsTheTwoGroups (correctness)` (gates/g5/P1_ComposeAdmitsTheTwoGroups.5.dfy) |
| `recovery-flow.login-throttle` | P1_KnownWrongPasswordsFromZero | design LoginThrottle.P1_KnownWrongPasswordsFromZero: "after 5 failed attempts for a known username whose pair starts at 0 (wrong passwords;" (§2.5 line 158) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper OneRowThrottle` (gates/g5/P1_KnownWrongPasswordsFromZero.10.dfy) |
|  | P2_ThreeWrongCodesReachMinusFive | design LoginThrottle.P2_ThreeWrongCodesReachMinusFive: "a wrong MFA code after a correct password lowers the row by 2, since the validation stage and its login-failure signal each score −1, so three wrong codes reach −5" (§2.5 line 158) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper OneRowThrottle` (gates/g5/P2_ThreeWrongCodesReachMinusFive.10.dfy) |
|  | P3_SuccessesFirstOffset | design LoginThrottle.P3_SuccessesFirstOffset: "after 5 wrong passwords plus one more for each successful login from that address in the row's hour that found the row below +5: up to 10 when those logins come before the failures" (§2.5 line 158) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper OneRowThrottle` (gates/g5/P3_SuccessesFirstOffset.10.dfy) |
|  | P4_InterleavedNoFixedBound | design LoginThrottle.P4_InterleavedNoFixedBound: "and more when the owner signs in between them" (§2.5 line 158); "and with no fixed bound when the owner keeps signing in between them" (§7 line 538) | proved | G5: line 143 `if \|s\| == 0 then 0 else (if s[0] == a then 1 else 0) + CountOf(a, s[1..])` → `if \|s\| != 0 then 0 else (if s[0] == a then 1 else 0) + CountOf(a, s[1..])`; row `helper CountOf` (gates/g5/P4_InterleavedNoFixedBound.90.dfy) |
|  | P5_UnknownThirdPlanRefused | design LoginThrottle.P5_UnknownThirdPlanRefused: "An unknown one scores −2 per attempt (identification and login both fail), and its identification's −1 is written before order 12 runs, so its 3rd plan is refused after 2 failed attempts" (§2.5 line 158) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper OneRowThrottle` (gates/g5/P5_UnknownThirdPlanRefused.10.dfy) |
|  | P6_AddressCapNeedsTenIdentifiers | design LoginThrottle.P9_AddressCapNeedsTenIdentifiers: "The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers." (§2.5 line 159) | proved | G5: line 50 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper NegTotalBound` (gates/g5/P6_AddressCapNeedsTenIdentifiers.25.dfy) |
|  | P7_EmailRowsCount | "**pair:** the rows for the client address whose identifier is the pending user's username or email have a summed score ≤ −5" (§2.5 line 148) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper PairTotalAtMost` (gates/g5/P7_EmailRowsCount.10.dfy) |
|  | P8_OtherAddressesNeverCount | "the rows for the client address …" (§2.5 line 148); "the sum of all negative row scores for the client address" (line 149) | proved | G5: line 40 `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []`; row `helper NoRowsAtIp` (gates/g5/P8_OtherAddressesNeverCount.10.dfy) |
| `deployment-server.wait-for-migration` | P1_MigratedExactly | plan check `t7_wait_probe.sh` (T7 S1): "A first wait must keep waiting with no version table, with only an older version's row, with this image's row while lock 1000 is held, and with the lock released but none of this image's migrations applied (what a migrator killed after writing its row leaves); it finishes once a real migrator, `python -m lifecycle.migrate`, completes", generalised | proved | G5: line 54 `if !db.tableExists then false` → `if !db.tableExists then true`; row `P1_MigratedExactly (correctness)` (gates/g5/P1_MigratedExactly.1.dfy) |
|  | P2_WaitEndsAtTheFirstMigratedAnswer | as P1; "Servers wait for the migration and never migrate." (§2.1 line 59) | proved | G5: line 79 `if i >= \|probes\| then StillWaiting` → `if i > \|probes\| then StillWaiting`; row `helper WaitFromFinished` (gates/g5/P2_WaitEndsAtTheFirstMigratedAnswer.9.dfy) |
|  | P3_WaitObservesTheEndOfThePass | "Neither the migrator's advisory lock nor the `authentik_version_history` marker shows completion on its own … The plan picks a wait that observes the end of the pass." (§2.1 line 59), for an image that brings Django migrations the database lacks | proved | G5: line 54 `if !db.tableExists then false` → `if !db.tableExists then true`; row `P3_WaitObservesTheEndOfThePass (correctness)` (gates/g5/P3_WaitObservesTheEndOfThePass.1.dfy) |
| `admission-policy.validations` | P1_OperatorsAdmitted | design AdmissionPolicy.P1_OperatorsAdmitted: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else; rule (c)'s name clause binds the operators group too." (§3.1 line 233) | proved | G5: line 96 `if a == CTrue \|\| b == CTrue then CTrue` → `if a != CTrue \|\| b == CTrue then CTrue`; row `P1_OperatorsAdmitted (correctness)` (gates/g5/P1_OperatorsAdmitted.94.dfy) |
|  | P2_ClusterAdminDenied | design AdmissionPolicy.P2_ClusterAdminDenied: "Cluster-admin is denied too, by design (Verified assumption 37)" (§3.1 line 233) | proved | G5: line 96 `if a == CTrue \|\| b == CTrue then CTrue` → `if a != CTrue \|\| b == CTrue then CTrue`; row `P2_ClusterAdminDenied (correctness)` (gates/g5/P2_ClusterAdminDenied.94.dfy) |
|  | P3_MetadataOnlyPodUpdateAdmitted | design AdmissionPolicy.P3_MetadataOnlyPodUpdateAdmitted: "Metadata-only pod updates are not matched (Verified assumption 77)." (§3.1 line 239) | proved | G5: line 89 `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse`; row `P3_MetadataOnlyPodUpdateAdmitted (correctness)` (gates/g5/P3_MetadataOnlyPodUpdateAdmitted.86.dfy) |
|  | P4_KubeletMintsTokens | design AdmissionPolicy.P4_KubeletMintsTokens: "The kubelet (`system:nodes`) is also admitted, so projected tokens keep mounting." (§3.1 line 234) | proved | G5: line 89 `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse`; row `P4_KubeletMintsTokens (correctness)` (gates/g5/P4_KubeletMintsTokens.86.dfy) |
|  | P5_ScaleAdmitted | design AdmissionPolicy.P5_ScaleAdmitted: "creates or updates (other than through the `scale` subresource) a workload whose pod template uses either account" (§3.1 line 243) | proved | G5: line 182 `(r.group == CoreGroup && r.op == CreateOp && r.resource == ServiceAccountsResource && r.sub == TokenSub)` → `(r.group == CoreGroup && r.op == CreateOp && r.resource == ServiceAccountsResource && r.sub != TokenSub)`; row `P5_ScaleAdmitted (correctness)` (gates/g5/P5_ScaleAdmitted.137.dfy) |
|  | P6_NoProtectedPodOrTemplateForNonOperators | design AdmissionPolicy.P6_NoProtectedPodOrTemplateForNonOperators: "Without (d), anyone who can create a Job could run a pod as `<rel>-admin-automation` through the job controller, which (c) admits." (§3.1 line 237) | proved | G5: line 96 `if a == CTrue \|\| b == CTrue then CTrue` → `if a != CTrue \|\| b == CTrue then CTrue`; row `P6_NoProtectedPodOrTemplateForNonOperators (correctness)` (gates/g5/P6_NoProtectedPodOrTemplateForNonOperators.94.dfy) |
|  | P7_MisnamedProtectedPodRefusedForAll | design AdmissionPolicy.P7_MisnamedProtectedPodRefusedForAll: "Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`" (§3.1 line 236) | proved | G5: line 90 `else if a == CTrue && b == CTrue then CTrue` → `else if a == CTrue \|\| b == CTrue then CTrue`; row `P7_MisnamedProtectedPodRefusedForAll (correctness)` (gates/g5/P7_MisnamedProtectedPodRefusedForAll.92.dfy) |
|  | P8_AdmittedProtectedPodIsGated | design AdmissionPolicy.P8_AdmittedProtectedPodIsGated: "so (b), (e) and (f) match every pod that holds a protected token" (§3.1 line 236) | proved | G5: line 90 `else if a == CTrue && b == CTrue then CTrue` → `else if a == CTrue \|\| b == CTrue then CTrue`; row `P8_AdmittedProtectedPodIsGated (correctness)` (gates/g5/P8_AdmittedProtectedPodIsGated.92.dfy) |
|  | P9_ApiReplicaSetPodsAdmitted | design AdmissionPolicy.P9_ApiReplicaSetPodsAdmitted: "the api's ReplicaSet pods (`<rel>-api-<hash>-<suffix>`) already do (Verified assumption 96)" (§3.1 line 236) | proved | G5: line 89 `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse`; row `P9_ApiReplicaSetPodsAdmitted (correctness)` (gates/g5/P9_ApiReplicaSetPodsAdmitted.86.dfy) |
| `configmap-tls-proxy.public-locations` | P1_RootRedirects | design AllowList9080.P1_RootRedirects: "**`= /` redirects (302) to the console URL** instead of forwarding to Authentik's root." (§2.7 line 216) | proved | G5: line 116 `if uri == FlowsDefaultAuthentication then Forward          // yaml:90` → `if uri != FlowsDefaultAuthentication then Forward          // yaml:90`; row `P1_RootRedirects (correctness)` (gates/g5/P1_RootRedirects.76.dfy) |
|  | P2_SetupNotForwarded | design AllowList9080.P2_SetupNotForwarded: "`:9080` forwards neither `/setup` nor the flow's slug" (§2.6 line 192); "**Refused:** `initial-setup`" (§2.7 line 217) | proved | G5: line 100 `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p`; row `P2_SetupNotForwarded (correctness)` (gates/g5/P2_SetupNotForwarded.66.dfy) |
|  | P3_InterfacesRefused | design AllowList9080.P3_InterfacesRefused: "**Refused:** … `/if/user/`, `/if/admin/` and the rest of `/api/`." (§2.7 line 217) | proved | G5: line 100 `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p`; row `P3_InterfacesRefused (correctness)` (gates/g5/P3_InterfacesRefused.66.dfy) |
|  | P4_AllowedFlowsForwarded | design AllowList9080.P4_AllowedFlowsForwarded: "`/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly: `default-authentication-flow`, `iverson-recovery`; `default-provider-authorization-implicit-consent` …; `default-invalidation-flow` …; `default-provider-invalidation-flow`" (§2.7 lines 204–208) | proved | G5: line 128 `else if uri == Root then Redirect                          // yaml:113  return 302 <console>` → `else if uri != Root then Redirect                          // yaml:113  return 302 <console>`; row `P4_AllowedFlowsForwarded (correctness)` (gates/g5/P4_AllowedFlowsForwarded.100.dfy) |
|  | P5_OtherApiRefused | design AllowList9080.P5_OtherApiRefused: "the rest of `/api/`" refused (§2.7 line 217) | proved | G5: line 100 `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p`; row `P5_OtherApiRefused (correctness)` (gates/g5/P5_OtherApiRefused.66.dfy) |
|  | P6_WebSocketAndLiveForwarded | "`/ws/client/`, with WebSocket upgrade headers." (§2.7 line 211); "`= /-/health/live/`, for the GCP health check." (line 213); plan check `check_t8.py` (T8 S1: `/ws/client/` and `/-/health/live/` in `allowed`, `/-/health/ready/` in `refused`) | proved | G5: line 100 `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p`; row `P6_WebSocketAndLiveForwarded (correctness)` (gates/g5/P6_WebSocketAndLiveForwarded.66.dfy) |
| `job-create-user.create-user` | P1_JobSetsPasswordOnFe0 | design StarRocksBootstrap.P2_JobSetsPasswordOnFe0: "the create-user Job sets root's password as soon as FE-0 answers, no longer waiting for the BE count first" (§4.3 line 375) | proved | G5: line 175 `exists i :: 0 <= i && i + 1 < \|calls\| && calls[i].1 == SelectOne && calls[i + 1].1 != SelectOne` → `exists i :: 0 <= i && i + 1 <= \|calls\| && calls[i].1 == SelectOne && calls[i + 1].1 != SelectOne`; row `helper FeAnswered` (gates/g5/P1_JobSetsPasswordOnFe0.116.dfy) |
|  | P2_BatchOnlyAfterEveryNodeListed | "It then waits for `BE_REPLICAS` backends and every FE before the rest of the batch." (§4.3 line 375); plan check `t19_job.sh` (T19 S1: `show-backends` and `show-frontends` before `create-user`), generalised | proved | G5: line 199 `&& calls[j].1 == ShowBackends && \|world(n + j).backendLines\| >= beReplicas` → `&& calls[j].1 != ShowBackends && \|world(n + j).backendLines\| >= beReplicas`; row `P2_BatchOnlyAfterEveryNodeListed (correctness)` (gates/g5/P2_BatchOnlyAfterEveryNodeListed.138.dfy) |
|  | P3_RevokeOperateAfterCreateUser | "`GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%'` is dropped … the job and compose's `starrocks-init` also run `REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%'` beside the existing role revoke, after `CREATE USER IF NOT EXISTS`" (§3.5 line 319); plan check (T15 S5: `"GRANT OPERATE" not in text`) | proved | G5: line 52 `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| \|\| s[..\|p\|] == p`; row `helper IsPrefix` (gates/g5/P3_RevokeOperateAfterCreateUser.10.dfy) |
|  | P4_PasswordAfterSetPassword | "Because the FE and BE then log in with root's password from their first start, the create-user Job sets root's password as soon as FE-0 answers" (§4.3 line 375); plan check `t19_job.sh` (`[ "$(command grep -c "^pw" $D/log)" -ge 3 ]`, state `r` after both runs) | proved | G5: line 220 `forall i, j :: 0 <= i < j < \|calls\| && calls[i].1 == SetPassword ==> calls[j].0 == Some(rootPw)` → `forall i, j :: 0 <= i < j < \|calls\| && calls[i].1 == SetPassword ==> calls[j].0 != Some(rootPw)`; row `P4_PasswordAfterSetPassword (correctness)` (gates/g5/P4_PasswordAfterSetPassword.173.dfy) |
| `setup.sh.guards` | P1_KindGate | "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330); plan check `t16_kind.sh` (T16 S1: "kind v0.24.0 must be refused before any helm or kubectl call; kind v0.33.0 must get past the check"), generalised | proved | G5: line 95 `if \|s\| > 0 && s[\|s\| - 1] == NL then StripTrailingNL(s[..\|s\| - 1]) else s` → `if \|s\| > 0 && s[\|s\| - 1] != NL then StripTrailingNL(s[..\|s\| - 1]) else s`; row `helper KindVersionIsV` (gates/g5/P1_KindGate.75.dfy) |
|  | P2_OnlyRunsOnePhase | **Expected:** (T14 S4) "`usage: Iverson.Server/deploy/kind/setup.sh [--only <phase>]   (phases: …)` then `exit=2`" for `--only no-such-phase`, generalised; Task 27 Interfaces "runs a single phase with `--only <phase>`" | proved | G5: line 38 `if i == \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` → `if i != \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)`; row `helper PadOccursIffToken` (gates/g5/P2_OnlyRunsOnePhase.12.dfy) |
|  | P3_EnrolOnlyWith15008 | "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); plan check `t17_enroll.sh` (T17 S1: fresh → label; `8080 15008 8081 15008` → label; `8080 8081 5432` → none; `150080 8081` → none), generalised | proved | G5: line 38 `if i == \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` → `if i != \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)`; row `helper PadOccursIffToken` (gates/g5/P3_EnrolOnlyWith15008.12.dfy) |
| `setup.ps1.guards` | P1_KindGate | "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330) | proved | G5: line 36 `c == '\t' \|\| c == '\n' \|\| c == '\u000B' \|\| c == '\u000C' \|\| c == '\r' \|\| c == ' '` → `c != '\t' \|\| c == '\n' \|\| c == '\u000B' \|\| c == '\u000C' \|\| c == '\r' \|\| c == ' '`; row `P1_KindGate (correctness)` (gates/g5/P1_KindGate.16.dfy) |
|  | P2_OnlyRunsOnePhase | **Expected:** (T14 S4) "… then `exit=2` … `setup.ps1` … mirrors the bash block line for line." | proved | G5: line 105 `CaseFold(x) == CaseFold(y)` → `CaseFold(x) != CaseFold(y)`; row `helper OnlyFlagFold` (gates/g5/P2_OnlyRunsOnePhase.80.dfy) |
|  | P3_EnrolOnlyWith15008 | "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); **Expected:** (T14 S4) "mirrors the bash block line for line" | proved | G5: line 105 `CaseFold(x) == CaseFold(y)` → `CaseFold(x) != CaseFold(y)`; row `helper ContainsPlain` (gates/g5/P3_EnrolOnlyWith15008.80.dfy) |
| `generate-compose-secrets.renew` | P1_RenewWhenAnyMissingOrExpiring | "All are rewritten together whenever any is missing or near expiry" (§4.6 line 438); plan check `t22_gen.sh` (T22 S1: "a re-run keeps the files", "a missing leaf key rewrites every file", "a leaf near expiry rewrites every file"), generalised | proved | G5: line 100 `if leaves == [] then [] else [ReadStem(leaves[0])] + StemsOf(leaves[1..])` → `if leaves != [] then [] else [ReadStem(leaves[0])] + StemsOf(leaves[1..])`; row `helper RenewLoopSpec` (gates/g5/P1_RenewWhenAnyMissingOrExpiring.83.dfy) |
|  | P2_RenewRewritesEverythingButTheCaKey | "All are rewritten together …, and the CA key is still deleted afterwards" (§4.6 line 438); plan check `t22_gen.sh` ("still no CA key") | proved | G5: line 80 `predicate IsIfs(b: Byte) { b == 32 \|\| b == 9 \|\| b == 10 }` → `predicate IsIfs(b: Byte) { b != 32 \|\| b == 9 \|\| b == 10 }`; row `helper LeavesValue` (gates/g5/P2_RenewRewritesEverythingButTheCaKey.54.dfy) |
| `fe-tls-entrypoint.settings` | P1_EachSettingOnce | plan check `t22_fe.sh` (T22 S1: two runs, then "fe.conf has '<line>' once" for each setting and "fe.conf has four lines"), generalised | proved | G5: line 184 `if \|settings\| == 0 then conf else Loop(Step(conf, settings[0]), settings[1..])` → `if \|settings\| != 0 then conf else Loop(Step(conf, settings[0]), settings[1..])`; row `helper LoopSets` (gates/g5/P1_EachSettingOnce.182.dfy) |
|  | P2_ExistingLinesKept | plan check `t22_fe.sh` ("the existing line is kept whole", from a file with no trailing newline "as an edited file may have") | proved | G5: line 61 `if i == \|c\| then [c] else [c[..i]] + Lines(c[i + 1..])` → `if i != \|c\| then [c] else [c[..i]] + Lines(c[i + 1..])`; row `helper Lines` (gates/g5/P2_ExistingLinesKept.44.dfy) |
| `authentik-probes.helpers` | P1_EdgeClient | tests (T25 S1 `test_probes.sh`): "edge client, hops 1, is the right-most entry", "hops 2, is the second from the right", "of an empty header is none", "beyond the header is none", generalised | proved | G5: line 60 `if \|e\| == 0 then []` → `if \|e\| != 0 then []`; row `helper StripSpaces` (gates/g5/P1_EdgeClient.54.dfy) |
|  | P2_IpIs | tests "a plain address matches", "an IPv4-mapped address matches", "another address does not match" | proved | G5: line 143 `a == b \|\| a == MappedPrefix + b` → `a != b \|\| a == MappedPrefix + b`; row `P2_IpIs (correctness)` (gates/g5/P2_IpIs.120.dfy) |
|  | P3_IsLoopback | tests "the tls-proxy bind address is loopback", "the port-forward address is loopback", "a pod address is not loopback" | proved | G5: line 42 `\|p\| <= \|x\| && x[..\|p\|] == p` → `\|p\| <= \|x\| && x[..\|p\|] != p`; row `P3_IsLoopback (correctness)` (gates/g5/P3_IsLoopback.31.dfy) |
|  | P4_IsConsole | tests "the console URL without its slash is the console", "the login page is not the console", "an empty Location is not the console"; "`= /` redirects (302) to the console URL" (§2.7 line 216) | proved | G5: line 68 `if \|x\| > 0 && x[\|x\| - 1] == SlashByte then x[..\|x\| - 1] else x` → `if \|x\| > 0 \|\| x[\|x\| - 1] == SlashByte then x[..\|x\| - 1] else x`; row `helper StripOneSlash` (gates/g5/P4_IsConsole.63.dfy) |
|  | P5_SetupShowsNoPrompt | tests "/setup failing shows no prompt", "redirecting to / shows no prompt", "redirecting to the setup flow shows a prompt", "answering 200 shows a prompt"; "on the in-cluster route, `/setup` and the `initial-setup` executor show no prompt" (§6 line 517) | proved | G5: line 53 `exists i {:trigger OccursAt(s, sub, i)} :: 0 <= i <= \|s\| && OccursAt(s, sub, i)` → `exists i {:trigger OccursAt(s, sub, i)} :: 0 < i <= \|s\| && OccursAt(s, sub, i)`; row `helper GlobIsContains` (gates/g5/P5_SetupShowsNoPrompt.48.dfy) |
|  | P6_Options | tests "<script>: --help exits 0", "<script>: an unknown option exits 2" (T25 S1) | proved | G5: line 185 `if \|args\| == 0 then Parsed(release, ns)` → `if \|args\| != 0 then Parsed(release, ns)`; row `P6_Options (correctness)` (gates/g5/P6_Options.167.dfy) |
| `admission-check.classify` | P1_Classify | tests (T25 S1): "success is admitted", "the policy named in the error is refused", "an RBAC refusal is an error", "another policy is an error", generalised; "a non-operator refused … the operators group admitted" (§6 line 519) | proved | G5: line 49 `if rc == 0 then                      // 59: if [ "$1" -eq 0 ]; then` → `if rc != 0 then                      // 59: if [ "$1" -eq 0 ]; then`; row `P1_Classify (correctness)` (gates/g5/P1_Classify.42.dfy) |
|  | P2_Options | tests "admission-check: --help exits 0", "admission-check: an unknown option exits 2" | proved | G5: line 72 `if \|args\| == 0 then                                   // 223: while [ $# -gt 0 ]; do` → `if \|args\| != 0 then                                   // 223: while [ $# -gt 0 ]; do`; row `P2_Options (correctness)` (gates/g5/P2_Options.55.dfy) |
| `mesh-check.mtls_seen` | P1_MtlsSeen | tests "FE -> BE mutual TLS reported by the destination", "a source-side report does not count", "a plaintext connection does not count" (T25 S1), generalised; "ztunnel reporting mTLS for FE ↔ BE" (§6 line 518) | proved | G5: line 14 `forall i :: 0 <= i < \|s\| ==> s[i] as int < 256` → `forall i :: 0 <= i <= \|s\| ==> s[i] as int < 256`; row `helper MutualTlsShape` (gates/g5/P1_MtlsSeen.3.dfy) |
|  | P2_Options | tests "mesh-check: --help exits 0", "mesh-check: an unknown option exits 2" | proved | G5: line 108 `if \|args\| == 0 then Parsed(release, ns)                                     // line 111: while [ $# -gt 0 ]` → `if \|args\| != 0 then Parsed(release, ns)                                     // line 111: while [ $# -gt 0 ]`; row `P2_Options (correctness)` (gates/g5/P2_Options.91.dfy) |
| `mesh-check.UNENROLLED_PY` | P1_RunningCountThenUnenrolled | test "running pods counted and the unenrolled one named" (T25 S1), generalised; "every release pod enrolled" (§6 line 518) | proved | G5: line 36 `if n < 10 then [Digit(n)] else Decimal(n / 10) + [Digit(n % 10)]` → `if n <= 10 then [Digit(n)] else Decimal(n / 10) + [Digit(n % 10)]`; row `helper Decimal` (gates/g5/P1_RunningCountThenUnenrolled.7.dfy) |
| `kind-checks.alive-wait` | P1_AliveColumn | **Expected:** (T27 S14) "`local K2 frontends alive: true backends alive: true` (one FE and one BE on the local profile; every row `true`)" | removed or renamed | member not found at the code ref (closure: ForLoopFacts, P1_AliveColumn, P2_WaitBounded) |
|  | P2_WaitBounded | **Expected:** (T27 S14) "printed after the script's wait of at most 5 minutes (the hook releases Helm once the BE is listed, which is before its process starts); a `false` after the wait is a failure" | removed or renamed | member not found at the code ref (closure: ForLoopFacts, P1_AliveColumn, P2_WaitBounded) |

Totals:
- 118 proved, each passing G5;
- 1 unproved: `EngagementHealthChecker.CheckHealthAsync` P3_HealthyIsExactlyTwoRows, accepted unproved at the plan stage;
- 2 removed or renamed: `kind-checks.alive-wait` P1 and P2.

### Ignored candidates

None.

### Notes

- **`kind-checks.alive-wait`.** Its source is `$LIVE/kind-checks.sh`, the scratch script that Task 27 Step 14 writes from the plan's `T27KIND` heredoc. It is not a repository file, so nothing matches it at `8ef84917`. Its model is the starting model, kept. `dcv.py closure` from seeds `AliveProgram`, `Alive`, `ForLoop` and `WaitLoop` gives `ForLoopFacts`, `P1_AliveColumn` and `P2_WaitBounded`. P1 and P2 are therefore *removed or renamed*. During SDD the live run extracted the script verbatim from the brief, which is the plan text DPV-2 proved.
- **`admission-policy.validations`.** The model now carries the final-review proxy logic (`proxyName`, `proxyTarget`, the CONNECT list with `pods/proxy` and `services/proxy`, and the new rule (b) guard), and P1–P9 still prove. No frozen property states that proxying is refused. The new refusal is transliterated and resolves inside a proved model, but no lemma asserts it. Its evidence is outside this review: the throwaway-kind live checks (31/31, then 38/38) and the 54 cel-go simulation cases in the SDD fix report.

### Uncovered

- Iverson.Server/deploy/scripts/admission-check.sh:106-137 — logic-bearing (shell) — `proxy_outcome` (three API-server proxy GETs, counts refused/passed) and `expect_proxy`; final-review fix b9b8bb23/8ef84917
- Iverson.Server/deploy/scripts/admission-check.sh:313 — logic-bearing (shell) — N10 check: non-operator proxy is refused; final-review fix b9b8bb23/8ef84917
- Iverson.Server/deploy/scripts/admission-check.sh:337 — logic-bearing (shell) — O11 check: operators group's proxy is admitted; final-review fix b9b8bb23/8ef84917

The scan also listed the admission-policy proxy hunks (`admission-policy.yaml:42`, `:60-73`, `:109-114`). Step 9 dropped them because `admission-policy.validations`' correspondence table maps them to model declarations that are neither a kept seed nor in a closure; they were re-proved. Per the scan, every other logic-bearing hunk matches a plan code block. The full classification of the 1232 hunks is in the run's `hunk-classes.md`.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes. §2 and §3 are empty, and there are no code fixes. The label is ⚠️ only because three obligations are not proved:
- `EngagementHealthChecker.CheckHealthAsync` P3_HealthyIsExactlyTwoRows is unproved, which the user accepted at DPV-1 and DPV-2;
- `kind-checks.alive-wait` P1_AliveColumn and P2_WaitBounded are *removed or renamed*, because their source is a scratch script outside the repository.
