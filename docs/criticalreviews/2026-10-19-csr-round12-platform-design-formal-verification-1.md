# Formal Verification: 2026-10-19-csr-round12-platform-design (Round 1)

**Spec:** /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md
**Spec SHA:** 19d74cfb95c4ccb8ceca9e939494828f3359c3a5 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-19-csr-round12-platform-design/

## 0. Coverage

### Rules scanned

**Candidates (13):**

| Candidate | Rule | Spec lines |
|---|---|---|
| `LoginThrottle` | pair and address throttle, clamp, password-stage cancel | §2.5, 146–160 |
| `TenantUserGuard` | `RequireUserInTenantAsync`: canonical id, `GetUserAsync`, pk and tenant compare | §2.1, 40–42 |
| `InviteQuota` | 100 per tenant per UTC day upsert, no refund | §2.1, 49–55; §6 488 |
| `RoleFunctions` | Operator / SchemaAdmin / ConsoleAdmin functions and the compose fallback | §2.2 69; §2.4 104–119 |
| `AdmissionPolicy` | VAP rules (a)–(f) and their exemptions | §3.1, 228–243 |
| `TokenReviewPin` | orchestrator pin's TokenReview decision | §2.6, 171–178 |
| `InitialSetupClosure` | `initial-setup` flow closure (engine mode plus always-false binding) | §2.6, 188–192 |
| `StreamRecheck` | 5 s skew rule and the per-message stream re-check | §2.3, 90–97; §6 490 |
| `CacheInvalidation` | LISTEN/NOTIFY writers, listener, reconnect invalidation | §2.3, 83–88; §6 490 |
| `StarRocksBootstrap` | create-user Job order and `MYSQL_PWD` joins | §4.3 #3, 374 |
| `AllowList9080` | the `:9080` forward/redirect/refuse decision | §2.7, 203–217; §2.6 192 |
| `RateLimitPartition` | per-`sub` limiter and the `(service, sub)` admin limiter | §2.1, 44–47; §6 488 |
| `ReadinessMapping` | `Healthy` / `AuthPending` / `Unhealthy` from the readiness probe | §3.5, 315–316 |

**Excluded:**
- **Facts about existing systems** (Verified assumptions and CDR cover these): §2.1 IdP request shapes and Authentik's `ValueError` fallback; §2.4 `sub` derivation, Authentik importer behaviour; §2.5 MFA stage options, recovery-link plans, port-forward addresses; §2.6 relay, RBAC, `iss` values, Setup flag; §3.6 versions; §4.1 Istio install, tolerations, peer-authentication semantics; §4.2 HBONE and ztunnel semantics; §4.3 #1, #2, #4 (CNPG behaviour); §4.4 StarRocks TLS, HSTS; §4.5 edges; §4.6 compose TLS and trust paths.
- **Config or declarative:** §2.1 grants and table DDL; §2.2 Jaeger args; §2.4 group-membership blueprint entries; §3.2 Kafka peers; §3.3 listener moves; §3.4 namespace allow tables, GKE firewall, AKS values; §4.2 NetworkPolicy and `AuthorizationPolicy` mirrors (declarative rules over ztunnel's semantics); §5 order and runbook lists; §6 test lists.
- **Prose** (citation sources only): Decisions, §1 Goals, §7, §8, Choices.

### Approved environment axioms

| Candidate | Axiom | Citation |
|---|---|---|
| `LoginThrottle` | `ClampedAdd(score, delta)` = `score + delta` clamped to [−5, 5] | VA22: "scores clamp at the tenant's −5/+5 (`tenants/models.py:34-35`)"; "an unknown username scores −2 per attempt" |
| | `PasswordStageCancels(planStart, current)` ⟺ `planStart − current ≥ 5` | VA65: a plan from row 0 "was cancelled at the 5th wrong password by the password stage's `failed_attempts_before_cancel` (5)"; plans from −3 and −4 "accepted 12 wrong passwords without cancelling"; spec line 158 "counted as the score drop since the plan started" |
| `InitialSetupClosure` | `FlowAdmitted(mode, bindings)` ⟺ `any`: some binding passes; `all`: every binding passes | VA62: mode `all` plus a `return False` flow-level binding → denied; "the binding alone, in `any` mode, left the prompt reachable" |
| `CacheInvalidation` | `Delivered(t, i)` ⟺ `ListeningAt(t, i)` for a write at `i` | VA14: "LISTEN takes effect at transaction commit"; "a no-grant login LISTENed and received a NOTIFY; a NOTIFY with no listener was not delivered to a later LISTEN"; registrations end with the session |
| `StarRocksBootstrap` | `JoinSucceeds(rootPasswordSet)` ⟺ `rootPasswordSet` | VA64: BE refused (62 × ERROR 1045) "exited 1 at 61 s, unregistered; after SET PASSWORD a restart registered it, Alive: true"; follower "exited 1 at 120 s without ADD FOLLOWER; a restart after the password joined it"; "FE-0 on a fresh install never logs in" |
| `ReadinessMapping` | `QueryOutcome(backendAlive)` = `Rows(2)` if alive, else `QueryError(1064)` | VA45: "as a login with no grants: `SELECT * FROM TABLE(generate_series(1,2))` returned two rows with a backend up (0.5 s) and ERROR 1064 Backend node not found with none (0.4 s)" |

`InviteQuota` declares no axiom; its serialization reading rests on a scan probe run during the scan (`postgres:18.6@sha256:74935e72…`, the spec's upsert statement): with the row at 99, 20 concurrent upserts returned exactly one row (`100`); on a fresh key, 150 concurrent upserts returned exactly 100 rows, no errors, final `"Count"` 100.

The Dafny run prints "ensures clause is part of a bodyless function" for each axiom, which the approved text keeps body-less; verification itself reports 0 errors and no contradictory-assumption warnings (G2).

### Correspondence tables

The controller checked each row against the spec lines it cites (G4). Lemma `requires` clauses are the frozen properties' own, plus well-formedness preconditions (sequence indices, datatype destructors) and preconditions of proof-repair helper lemmas; none restricts a modelled rule beyond the cited text.

**`LoginThrottle`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 147–149 | `RefusesNewPlan` | true when refused: pair `pairScore ≤ −5` first, then address `addressNegativeSum ≤ −50`; the pair is one summed score |
| 158 | `RefusesNewPlan` applied once per plan | "runs once per login plan, when identification completes"; evaluated on the score after the completed failures |
| 158; VA22 | `ScoreAfterFailures`, `PairScoreAfter` | each failure is `ClampedAdd(prev, −perAttempt)`, `perAttempt` 1 (known) or 2 (unknown), from 0 |
| 158; VA65 | axiom `PasswordStageCancels` | used as approved |
| 149, 159 | `AddressNegativeSum`, `CountNegative` | sum of negative row scores; rows unique on (identifier, ip) (VA22); identifiers with failures |
| — | `ScoreAfterFailuresExact`, `ScoreAfterFailuresBounds`, `NegativeSumLowerBound` | proof-repair helpers, proved |

**`TenantUserGuard`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 40, 42 | `UserRecord`, `Directory`, `LookupResult` | `GET /api/v3/core/users/{pk}/` as a map from the requested segment to the returned user; absence is the 404 |
| 42 | `IsCanonicalPositiveDecimal` | non-empty ASCII digits, first digit not `0` |
| 40, 42 | `GetUser` | 404 → not in tenant; returned pk ≠ requested → not in tenant; else the record |
| 41–42 | `RequireUserInTenant` | spec order: canonical check (`Refused(NoCall)`), `GetUserAsync`, `tenant_id` compare, `Accepted(record)` |
| 42 | `MutationTargetPk` | the checked record's pk goes to the mutations |

**`InviteQuota`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 50 | `Limit := 100` | "100 per tenant per UTC day" |
| 51 | `QuotaRow`, `Reserve`, `ReturnsRow` | the upsert's three cases in statement order: insert 1; `Count < 100` → +1 with a row; else no row (`ResourceExhausted`) |
| 51 | `InviteAttempt` | the IdP outcome is an input that changes nothing: no refund |
| 50, 55 | `CountAfter`, `ReservedCount` | one (TenantId, Day) key from `NoRow`; a new UTC day is a new key |
| §6 488 | `CountAfter` | concurrent attempts as one serial sequence (the scan probe above) |
| — | `CountAfterInvariant` | proof-repair helper: `StoredCount = min(n, 100) = ReservedCount(n)` |

**`RoleFunctions`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 104, 119 | `Principal`, `Config` | `operatorSubjects: seq<string>`; fallback unset = `None` |
| 108, 111, 119 | `IsOperator` | the list, the `admin` scope, or the compose fallback, in line 111's order |
| 111 | `IsSchemaAdmin` | `IsOperator` or scope `schema_admin` |
| 66, 116 | `IsTenantAdmin` | `"tenant-admins" in groups`; no property depends on the body |
| 69 | `IsConsoleAdmin` | Operator or TenantAdmin |

**`AdmissionPolicy`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 228–229 | `Admits` | one policy, `failurePolicy: Fail`; a failed validation is `!Admits` |
| 230 | `InReleaseNs`, `SA(ns, name)`, `PodRef(ns, name)` | namespace-qualified identities |
| 231, 233 | `Operator`, `Admits` | `groups ∩ operatorGroups ≠ {}`; `Admits = !Matched ∨ Operator ∨ ExemptRequester`; nothing keyed on `system:masters` |
| 234 | `TokenCreate(sa)` | (a) for the two protected accounts; `system:nodes` exempt |
| 235 | `PodConnect(kind, pod)` | (b) prefix match on the four pod-name prefixes; no exemption |
| 236 | `PodCreate(sa)` | (c) protected `serviceAccountName`; four kube-system controllers exempt |
| 237 | `WorkloadWrite(verb, wkind, oldSa, newSa)` | (d) the six kinds, old or new template account; five kube-system controllers exempt |
| 238 | `EphemeralUpdate(pod)` | (e) protected pod names; no exemption |
| 239 | `PodUpdate(pod, oldImages, newImages)`, `ImagesDiffer` | (f) protected pod and an image change; no exemption |
| 243 | `ScaleUpdate(wkind)` | no rule matches it |

**`TokenReviewPin`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 175 | `ReviewResponse` | `Exception` or `Http(status, authenticated, username)`; body fields optional |
| 175 | `ExpectedUsername` | `system:serviceaccount:<ns>:<rel>-api` |
| 175 | `PinAdmits` | spec order: exception, status outside 2xx, `authenticated` not true, username differs → refuse; else admit. The kept claim checks are outside the frozen signature and not modelled |

**`InitialSetupClosure`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 190 | `EngineMode`, `DefaultEngineMode() = Any` | "default `any` engine mode" |
| 190–191 | `ClosureBindings(stock) = [stock, ClosurePolicyResult()]` | stock binding, then the closure's flow-level binding |
| 191 | `ClosurePolicyResult() = false`, `ClosureEngineMode() = All` | "a policy that always returns false"; "sets `policy_engine_mode: all`" |
| 191 | `ClosureIsAllWithFalse` | helper: the modelled closure is exactly P1's configuration |

**`StreamRecheck`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 90 | `IsRevoked` | whole seconds on one time line; `iat ≤ revoked_at + 5`; no row → not revoked |
| 92–95 | `StreamState` | the cache snapshot taken before each message: both subs' (iat, revoked_at) and the tenant status |
| 94–96 | `BeforeWrite` | spec order: either sub revoked → `Unauthenticated`; tenant suspended or deleted → `PermissionDenied`; else write |

**`CacheInvalidation`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 84 | `Writer`, `Table`, `NotifiesInSameTransaction` | the eight writers in the spec's order; each notifies in its write's transaction |
| 85 | `ListenStateAfter`, `ValidTrace` | one listener connection; Connect only while disconnected, Disconnect only while connected; starts disconnected |
| VA14 | axiom `Delivered` | delivered iff listening at the write's commit |
| 85 | `InvalidatesAt` (Write) | every delivered notification invalidates both caches; the payload is ignored |
| 86 | `InvalidatesAt` (Connect) | LISTEN, then invalidate both caches |
| 86–87 | `InvalidatesAt` (Disconnect) | nothing replayed, nothing invalidated |
| 85 | `InvalidatedAt` | both caches |

**`StarRocksBootstrap`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 374 | `PodId = FE(i) \| BE(i)`, `InstallState` | FE-0 is `FE(0)`; followers `FE(i>0)` |
| 374; VA64 | `StartPod` | FE-0 always comes up and joins as leader; any other pod joins iff `JoinSucceeds(rootPasswordSet)`, else state unchanged (exit 1, unregistered) |
| 374 | `JobStep` | new order: wait for FE-0 → SET PASSWORD → wait for `BE_REPLICAS` and every FE → rest of batch |
| 374 (`job-create-user.yaml:87-96`) | `OldJobStep`, `RunOld` | old order: wait for FE → wait for `BE_REPLICAS` → SET PASSWORD; any interleaving of starts and Job steps |
| — | `OldStuck`, `NoBeMeansZeroCount`, `ApplyOldPreservesStuck`, `RunOldPreservesStuck` | proof-repair helpers, proved by explicit induction |

**`AllowList9080`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 203 | `Outcome`, `RouteReq` | forward when an allow-list location matches, else 404 |
| 203–213 | `Loc = Exact \| Prefix \| WsPrefix`, `Matches` | entries written with `=` are exact; every other entry is an nginx prefix location |
| 204–208 | `AllowedSlugs`, `FlowLocs` | `/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly the five slugs, in spec order |
| 209–213 | `OtherLocs` | the flow shortcuts, `/static/`, `/media/`, `/ws/client/` (only with upgrade headers), the OIDC endpoints under `/application/o/`, `= /-/health/live/` |
| 216 | `RouteReq` | `= /` → `Redirect`, after the allow-list (no entry matches `/`) |
| 217; §2.6 192 | `RouteReq` fall-through | the refused list is everything not matched |
| — | `Route(path) = RouteReq(path, true)` | properties take the most permissive headers, so "not forwarded" holds for any headers |
| — | `Clash`, `DiffAt`, `NoClashNoMatch`, `NoClash_*`, `FlowEntries`, `LongSlugHead` | proof-repair helpers, proved |

**`RateLimitPartition`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 45 | `Service`, `Call` | the two admin services, every other RPC as `Other`; a call is (sub, service) |
| 45 | `GeneralLimit = 50000`, `AdminLimit = 30` | the two limits |
| 45 | `CountFor`, `GeneralCount` | admin bucket keyed `(service, sub)`; the general bucket counts only that sub's non-admin calls |
| 45 | `Allowed` | spec order: general limiter for non-admin RPCs, the second limiter for admin RPCs; refused when the bucket is full |
| 45; §6 488 | `ValidWindow` | the admitted calls of the current minute, each admitted by `Allowed` |

**`ReadinessMapping`**

| Spec lines | Dafny | Modelling choice |
|---|---|---|
| 316 | `Status` | the kept three statuses |
| 315–316 | `ProbeOutcome` | `LoginRefused`, `ConnectFailed`, `Rows(count)`, `QueryError(code)`, `QueryTimedOut` |
| 315–316 | `Classify` | spec order: two rows → `Healthy`; refused login → `AuthPending`; otherwise `Unhealthy` (the kept mapping) |

### Obligations

G5 names the csv row that killed each obligation; "own" is the obligation's own row.

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `LoginThrottle` | P1_KnownUserRefusedFromFifth | §2.5 line 158 | proved (G5: own) |
| | P2_UnknownUserRefusedFromThird | §2.5 line 158 | proved (G5: own); see §3 item 1 |
| | P3_PlanFromZeroCancelledAtFifth | §2.5 line 158 | proved (G5: `ScoreAfterFailuresExact`, which its proof calls) |
| | P4_PlanFromNegativeNeverCancelled | §2.5 line 158 | proved (G5: `ScoreAfterFailuresBounds`, which its proof calls) |
| | P5_AddressCapNeedsTenIdentifiers | §2.5 line 159 | proved (G5: `NegativeSumLowerBound`, which its proof calls) |
| `TenantUserGuard` | P1_NonCanonicalRefusedWithoutCall | §2.1 line 42 | proved (G5: own) |
| | P2_MismatchRefused | §2.1 lines 40–42 | proved (G5: own) |
| | P3_MutationsUseCheckedPk | §2.1 line 42 | proved (G5: own) |
| `InviteQuota` | P1_AtMostHundredReserved | §2.1 line 50 | proved (G5: `CountAfterInvariant`, which its proof calls) |
| | P2_HundredFirstRefused | §6 line 488 | proved (G5: own) |
| | P3_CountMonotone | §2.1 line 51 | proved (G5: `CountAfterInvariant`, which its proof calls) |
| `RoleFunctions` | P1_OperatorImpliesSchemaAdmin | §2.4 line 111 | proved (G5: own) |
| | P2_GroupsIrrelevantWithoutFallback | §2.4 lines 114, 119 | proved (G5: own) |
| | P3_AdminScopeIsOperator | §2.4 line 106 | proved (G5: own) |
| | P4_OperatorImpliesConsoleAdmin | §2.2 line 69 | proved (G5: own) |
| | P5_FallbackGroupIsSchemaAdmin | §2.4 line 111 | proved (G5: own) |
| `AdmissionPolicy` | P1_OperatorsAdmitted | §3.1 line 233 | proved (G5: own) |
| | P2_ClusterAdminDenied | §3.1 line 233 | proved (G5: own) |
| | P3_MetadataOnlyPodUpdateAdmitted | §3.1 line 239 | proved (G5: own) |
| | P4_KubeletMintsTokens | §3.1 line 234 | proved (G5: own) |
| | P5_ScaleAdmitted | §3.1 line 243 | proved (G5: own) |
| | P6_NoProtectedPodOrTemplateForNonOperators | §3.1 line 237 | proved (G5: own) |
| `TokenReviewPin` | P1_SuccessAdmitted | §2.6 line 175 | proved (G5: own) |
| | P2_ErrorsRefused | §2.6 line 175 | proved (G5: own) |
| | P3_WrongIdentityRefused | §2.6 line 175 | proved (G5: own) |
| `InitialSetupClosure` | P1_ClosureDenies | §2.6 line 191 | proved but unfalsifiable |
| | P2_AnyModeStockAdmits | §2.6 line 190 | proved but unfalsifiable |
| `StreamRecheck` | P1_WithinFiveSecondsRevoked | §2.3 line 90 | proved (G5: own) |
| | P2_Boundary | §2.3 line 90 | proved (G5: own) |
| | P3_NoWriteAfterRevocationOrSuspension | §2.3 lines 93–96 | proved (G5: own) |
| `CacheInvalidation` | P1_ListeningWriteInvalidates | §2.3 lines 84–85 | proved (G5: own) |
| | P2_NoWriteMissedAcrossReconnect | §2.3 line 86 | proved (G5: own) |
| `StarRocksBootstrap` | P1_StartAfterPasswordJoins | §4.3 line 374 | proved (G5: own) |
| | P2_JobSetsPasswordOnFe0 | §4.3 line 374 | proved (G5: own) |
| | P3_OldOrderDeadlocks | §4.3 line 374 | proved (G5: `ApplyOldPreservesStuck`, which its proof calls through `RunOldPreservesStuck`) |
| `AllowList9080` | P1_RootRedirects | §2.7 line 216 | proved (G5: own) |
| | P2_SetupNotForwarded | §2.6 line 192; §2.7 line 217 | proved (G5: `NoClash_Setup`, `NoClash_IfFlowInitialSetup`, `NoClash_ApiInitialSetup`, which its proof calls) |
| | P3_InterfacesRefused | §2.7 line 217 | proved (G5: `NoClash_IfUser`, `NoClash_IfAdmin`, which its proof calls) |
| | P4_AllowedFlowsForwarded | §2.7 lines 204–208 | proved (G5: own) |
| | P5_OtherApiRefused | §2.7 line 217 | proved (G5: `NoClash_Api`, which its proof calls) |
| `RateLimitPartition` | P1_ThirtyFirstRefusedOthersPass | §6 line 488 | proved (G5: own) |
| | P2_SeparateBuckets | §2.1 line 45 | proved (G5: own) |
| `ReadinessMapping` | P1_AuthPendingOnlyFromRefusedLogin | §3.5 line 316 | proved (G5: own) |
| | P2_RowsMeanHealthy | §3.5 line 315 | proved (G5: own) |

**Why `InitialSetupClosure` P1 and P2 are unfalsifiable.** Both properties, as approved, state the configuration in literals (`FlowAdmitted(All, [stock, false])`, `FlowAdmitted(Any, [true, false])`) and read only the approved axiom. No model line feeds them, and G5 never mutates an axiom. Four mutations of the model (the closure policy's result, the closure's mode, the default mode, the closure's binding list) were each caught only by the link lemma `ClosureIsAllWithFalse`, which neither property's proof calls. The proofs show what VA62 implies for those two configurations; they do not check the spec's rule text against it.

### Ambiguities that did not reach §3

- **`LoginThrottle`, one pair row or two (line 148).** "the rows … whose identifier is the pending user's username or email have a summed score". The model uses one summed score clamped at −5. With username and email rows summed, failures split across both still reach −5 after 5 in total, so P1 holds; the sum can only go below −5, which no property bounds.
- **`LoginThrottle`, identifiers vs accounts (line 159).** P5 counts identifiers (rows), as its citation does. The follow-on "Failures concentrated on fewer accounts are stopped first by the pair rule" is not modelled: one account has up to two identifiers.
- **`TenantUserGuard`, what a pk mismatch yields (line 42).** The model maps it to "not in this tenant", as for the 404. Read as an exception, the RPC fails with an internal error instead of `PermissionDenied`. Either way no mutation runs, and no property states the status code for this case.
- **`TenantUserGuard`, IdP responses other than 200 and 404.** Not stated in §2.1; the model has only `Found` and `NotFound`.
- **`TenantUserGuard`, the bound on a canonical integer.** An over-range digit string passes the canonical check and costs one IdP call (404 or error) rather than none. No property is affected.
- **`InviteQuota`, concurrency (§6 line 488).** The model serializes the two instances' upserts. The scan probe above supports this: 150 concurrent upserts on a fresh key returned exactly 100 rows.
- **`InviteQuota`, no refund (line 51).** The model reads it as "the count never decreases". As the spec accepts, an IdP outage can use up the day's quota with no invite sent.
- **`RoleFunctions`, where the fallback sits (lines 108, 111, 119).** The model puts it inside the one Operator function, as lines 111 and 119 say. Read as SchemaAdmin-only, P5 still holds.
- **`AdmissionPolicy`, per-rule exemptions (lines 234–237).** The model keeps each "Also admitted" list to its own rule. A combined list weakens P2's and P6's preconditions, and all six properties hold either way.
- **`AdmissionPolicy`, completeness of the (d) controller list (line 237).** `statefulset-controller` and `daemon-set-controller` appear in (c) but not in (d). Under the model, a template write to a protected StatefulSet or DaemonSet by either is denied. Their status writes go through a subresource (d) does not match. No property depends on this.
- **`AdmissionPolicy`, namespace scoping (line 230).** Identities are namespace-qualified, so a same-named account elsewhere is a different, unprotected account.
- **`TokenReviewPin`, 2xx vs 201 (line 175).** "outside 2xx" admits any 2xx. Reading it as 201-only refuses a correct 200 or 202, and the properties hold under both.
- **`TokenReviewPin`, claim checks.** P1 shows only that the TokenReview half admits a correct 201. Whether the whole pin admits also depends on the kept claim checks, which the model leaves out.
- **`InitialSetupClosure`, line 190's subject.** Read literally, it describes the stock flow alone, `[stock]`. P2 states it with the closure's binding added in `any` mode, as VA62 records. Both readings agree that `any` mode with a passing stock binding admits the flow.
- **`StreamRecheck`, sub-second `revoked_at` (line 90).** With `revoked_at` sub-second and `iat` whole seconds, the refused window is just under 6 s, not 5. Neither property breaks.
- **`StreamRecheck`, the acting user's `iat`, a stream with no acting user, and the precedence of revoked over suspended (lines 94–96).** P3 requires only "no write", so it holds under every reading.
- **`StreamRecheck`, a deleted tenant as a cache miss (line 96).** The model treats deleted as a status value. A miss (`null`) is also covered by the text: the unary paths treat `null`, `"suspended"` and `"deleted"` alike (`Program.cs:692, 724`), and line 96 says the stream behaves "as the unary paths do".
- **`CacheInvalidation`, LISTEN then invalidate as one step (line 86).** With the two as separate steps in the spec's order, a write between them is delivered and also covered by the invalidation, so P2 holds. The reverse order would lose writes, and the spec states LISTEN first.
- **`CacheInvalidation`, commit-time listening (VA14, line 84).** Delivery is decided by the listening state at the write's commit.
- **`StarRocksBootstrap`, P3's counterfactual (line 374).** P3 shows that the old wait deadlocks with the pods' new `MYSQL_PWD`, which is what the citation's "existed only because the BE logged in without one" needs. Under the old pod env, without a password, BEs join and nothing deadlocks.
- **`StarRocksBootstrap`, whether a follower or BE also needs FE-0 up to join.** The model follows line 374, where the password is the only condition. The Job sets the password only after FE-0 answers, so every reachable state agrees.
- **`AllowList9080`, prefix or exact for entries without `=` (lines 203–213).** The model uses nginx prefix locations, since the spec marks exact matches with `=`. All five properties hold if every entry is exact.
- **`AllowList9080`, the base of `/token/`, `/userinfo/`, `/revoke/` and `/.well-known/openid-configuration` (line 212).** The model puts them under `/application/o/` and `/application/o/iverson-api/`, Authentik's OIDC paths. Root-level readings affect no property.
- **`AllowList9080`, `/ws/client/` "with WebSocket upgrade headers" (line 211).** Header-conditional in the model; the properties take headers present, so both readings agree.
- **`RateLimitPartition`, whether admin calls also count against the 50,000 (line 45).** "except … Those go to a second limiter" excludes them. Both properties hold under either reading, since each bounds the general count.
- **`RateLimitPartition`, window kind; refused calls counted; calls with no `sub`.** These are not stated for the second limiter, and none changes P1 or P2.
- **`ReadinessMapping`, "Two rows" (line 315).** The model reads exactly two. With "any rows", both properties still hold, because the axiom only produces `Rows(2)`.
- **`ReadinessMapping`, a refused login vs any 1045 (line 316).** The kept `ClassifyConnectionException` maps an AccessDenied (1045) from any stage of the probe to `AuthPending`, not only from the login. Under that reading, P1 fails only for a query-stage 1045. VA45 records that the query needs no grant, so the query stage cannot be denied for privileges. The two readings agree on every outcome VA45 allows.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 1. When an unknown username's identification failure is scored (`LoginThrottle` P2, §2.5 line 158)

**The choice.** Line 158 says the pair rule refuses new login plans "after 5 failed attempts for a known username, or 3 for an unknown one (it scores −2 per attempt: identification and login both fail)". P2, as approved, reads this as: the pair is checked at order 12 on the score left by the completed attempts, so the 4th plan is the first one refused. The pinned Authentik source reads differently:
- `authentik/stages/identification/stage.py:198` in 2026.8.4 (`VERSION = "2026.8.4"`, `authentik/__init__.py:6`) sends `identification_failed` while validating the identification stage, before it continues with the pretend user (`:210-212`).
- `authentik/policies/reputation/signals.py:58-61` handles that signal with `update_score(request, uid_field, -1)`.

So the −1 for a plan's own identification is written before the deny stage at order 12 evaluates the pair. After 2 failed attempts the pair is at −4. The 3rd plan's identification takes it to −5, and the deny stage refuses that plan before its password prompt. An unknown username gets 2 complete failed attempts, not 3.

**Why it is forced.** The two readings disagree on P2, and on the count the spec states. A controller check of the gated model plus this order (`ReadingB_ThirdPlanRefused`: `RefusesNewPlan(ClampedAdd(PairScoreAfter(2, 2), -1), 0)`; `ReadingB_SecondPlanAdmitted`) verified with "23 verified, 0 errors". P2 as frozen states `n >= 3`, so under the source's order it fails at `n = 2`. A test written from line 158 would expect the 3rd plan to reach the password prompt.

What backs the source's order: VA21's live run, where an unknown username "yields an unsaved pending user and the flow continues", and VA65's log, where the policy was "evaluated once per plan, when identification completed". This is a source read of 2026.8.4, not a live count of refused plans. Known usernames are unaffected: their identification succeeds and scores nothing.

**Options.**
- **(a) Restate line 158 to the source's order.** Replace `after 5 failed attempts for a known username, or 3 for an unknown one (it scores −2 per attempt: identification and login both fail)` with `after 5 failed attempts for a known username; for an unknown one the 3rd plan is refused, after 2 failed attempts (it scores −2 per attempt: identification and login both fail, and identification's −1 is written before the deny stage runs, \`stages/identification/stage.py:198\`)`. Add a Verified-assumptions row for the source read, and have the plan's live test assert the 3rd plan refused. P2 is re-frozen next round.
  Evidence: not model-checked: edits the cited text of P2. The controller's reading-B check above proves the restated count against the gated model.
- **(b) Verify before planning.** Leave line 158 as written and probe it live first: a throwaway 2026.8.4 stack with the deny stage at order 12, an unknown username, failed attempts until a plan is refused. Then restate the count to the observed one.
  Evidence: not model-checked: no spec change.
- **(c) Accept the text as an approximation.** Leave line 158 as written and record in §7 that the unknown-user count is approximate (2 or 3). The plan's tests then must not assert the exact count.
  Evidence: not model-checked: edits no modelled rule text.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3 has one item, the unknown-username count in `LoginThrottle` P2.

Also, two obligations are `proved but unfalsifiable` (`InitialSetupClosure` P1 and P2). They read only the VA62 axiom, so this round checks nothing about the spec's closure text beyond what VA62 records.

All other 42 obligations are proved and killed by a G5 mutation. §2 is empty.
