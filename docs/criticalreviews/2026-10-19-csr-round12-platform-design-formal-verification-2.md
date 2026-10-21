# Formal Verification: 2026-10-19-csr-round12-platform-design (Round 2)

**Spec:** /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md
**Spec SHA:** a782ac7674fde9185ad55bb1a986df3008adf050 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-19-csr-round12-platform-design/

## 0. Coverage

### Rules scanned

**Candidates (13).** The same 13 rules as round 1. Since round 1's spec (`19d74cfb`), the rule text of two changed and was re-frozen and re-modelled; the other eleven are byte-identical (a block comparison of each candidate's cited lines between `19d74cfb` and `a782ac76`; lines after 282 moved by +1 or +2) and their axioms' Verified-assumption rows (VA14, VA45, VA62, VA64) are unchanged. With the user's approval, those eleven were re-gated on round 1's models rather than re-modelled: each model's non-comment text is identical to round 1's (a comment-stripped diff gives 0 lines), with the header's spec SHA, round and the cited line numbers updated, and each went through G0–G3 and a replay of round 1's G5 mutations.

| Candidate | Rule | Spec lines | This round |
|---|---|---|---|
| `LoginThrottle` | pair and address throttle, clamp, success offset, MFA −2, unknown-username order, password-stage cancel | §2.5, 146–160; §7 538 | re-frozen and re-modelled |
| `AdmissionPolicy` | VAP rules (a)–(f), their exemptions and rule (c)'s name clause | §3.1, 228–243 | re-frozen and re-modelled |
| `TenantUserGuard` | `RequireUserInTenantAsync` | §2.1, 40–42 | re-gated |
| `InviteQuota` | 100 per tenant per UTC day upsert, no refund | §2.1, 49–55; §6 490 | re-gated |
| `RoleFunctions` | Operator / SchemaAdmin / ConsoleAdmin and the compose fallback | §2.2 69; §2.4 104–119 | re-gated |
| `TokenReviewPin` | orchestrator pin's TokenReview decision | §2.6, 171–178 | re-gated |
| `InitialSetupClosure` | `initial-setup` closure | §2.6, 188–192 | re-gated |
| `StreamRecheck` | 5 s skew and per-message stream re-check | §2.3, 90–97; §6 492 | re-gated |
| `CacheInvalidation` | LISTEN/NOTIFY invalidation | §2.3, 83–88; §6 492 | re-gated |
| `StarRocksBootstrap` | create-user Job order and `MYSQL_PWD` joins | §4.3 #3, 375 | re-gated |
| `AllowList9080` | the `:9080` decision | §2.7, 203–217; §2.6 192 | re-gated |
| `RateLimitPartition` | per-`sub` and `(service, sub)` limiters | §2.1, 44–47; §6 490 | re-gated |
| `ReadinessMapping` | readiness probe mapping | §3.5, 316–317 | re-gated |

**Excluded:** as round 1, plus the text added since: §2.5 line 154's link lifetimes, line 160's row expiry and cleanup, and line 165's port-forward address (facts about Authentik, covered by VA93, VA94 and VA23); §3.1's tooling list and group plumbing (config); §6 line 495's fresh-install convergence check and §6's other new tests (test procedures); §7's new entries (prose).

### Approved environment axioms

| Candidate | Axiom | Citation |
|---|---|---|
| `LoginThrottle` | `ClampedAdd(score, delta)` = `score + delta` clamped to [−5, 5] | VA22: "scores clamp at the tenant's −5/+5 (`tenants/models.py:34-35`)"; VA93: successful logins left the row at +1, +2 and +3 |
| | `PasswordStageCancels(promptScore, current)` ⟺ `promptScore − current ≥ 5` | VA65: a plan from row 0 "was cancelled at the 5th wrong password …"; plans from −3 and −4 "accepted 12 wrong passwords without cancelling"; VA91: an unknown identifier's row read −1 after identification and the plan was not cancelled through 12 wrong passwords; line 158: "counted as the drop in the username's score since the password stage was first shown in that plan" |
| `InitialSetupClosure`, `CacheInvalidation`, `StarRocksBootstrap`, `ReadinessMapping` | as round 1 | VA62, VA14, VA64, VA45, unchanged |

### Correspondence tables

**`LoginThrottle`** (model lines in `docs/formal/…/LoginThrottle.dfy` before the citation lines were inserted):

| Spec lines | Dafny lines | Spec text behind the choice |
|---|---|---|
| 147–149 | 31–36 `RefusesNewPlan` | "returns **true when the attempt must be refused**"; pair case first ("summed score ≤ −5", 148), then address ("sum of all negative row scores … ≤ −50", 149) |
| 149 | 39–45 `AddressNegativeSum` | "the sum of all negative row scores for the client address" |
| 158 | 63 `Attempt` | plan kinds in 158's order: success +1; "5 failed attempts … (wrong passwords;"; "a wrong MFA code after a correct password" |
| 158 | 66–76 `Step` | success +1 clamped at +5 ("found the row below +5"; VA22/VA93 via `ClampedAdd`); wrong password −1; wrong MFA code −1 twice ("the validation stage and its login-failure signal each score −1") |
| 158 | 79–83 `PairAfter`, 86–90 `Repeat`, 93–97 `CountOf` | plans applied in order to one (address, identifier) row: "Each (address, identifier) row is clamped at −5 and +5" |
| 158 | 103–108 `Admissible` | "The deny stage runs once per login plan, when identification completes": every plan, successes included, passes `RefusesNewPlan` on its order-12 score; the address sum is the single row's negative part |
| 158 | 113–119 `ScoreAfterFailures` | "Password retries inside a plan that has passed order 12": repeated −1 steps, clamped at −5 |
| 158 | 124–129 `UnknownPlanStep`, 132–136 `UnknownPairAfterPlans`, 141–145 `UnknownPairAtOrder12` | "identification and login both fail" (−2 per plan); "its identification's −1 is written before order 12 runs" (k-th plan judged on k−1 full plans then −1); a plan refused at order 12 scores only identification's −1 |
| 158 | 149–152 `UnknownPromptScore` | "identification's −1 is already in the stage's starting score": `ClampedAdd(p0, −1)` |
| 158 (cancel) | axiom `PasswordStageCancels`, 15–16 | "counted as the drop in the username's score since the password stage was first shown in that plan" |
| 159 | 48–54 `CountNegative`, P9 | "each floored at −5, so it is reached only once failures span at least 10 identifiers" |
| 160 | not modelled | expiry and cleanup excluded; every attempt assumed inside the row's hour, as 158's "in the row's hour" |
| 538 (§7) | P4, helper `Interleaved` at 258 | "with no fixed bound when the owner keeps signing in between them"; witness N rounds of [SuccessfulLogin, WrongPasswordPlan] (0 → 1 → 0) |

**`AdmissionPolicy`:**

| Spec lines | Dafny lines | Spec text behind the modelling choice |
|---|---|---|
| 230 | 14 (`SA` namespace-qualified), 18 (`PodRef`), 52–57 (`releaseNs`, `protectedAccounts`), 80–83 (`InReleaseNs`) | "bound with a `namespaceSelector` on the release namespace" |
| 231 | 52 (`operatorGroups`), 124–127 (`Operator`) | "names the group(s) that count as operators" (the non-empty render check is not modelled; no property depends on it) |
| 233 | 124–127 (`Operator`), 157–160 (`Admits`) | "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else."; no cluster-admin exemption |
| 234 (a) | 35 (`TokenCreate`), 86–89 (`ProtectedSA`), 55–56, 111, 133 (kubelet: `"system:nodes" in r.groups`) | "`CREATE` on `serviceaccounts/token` for `<rel>-api` and `<rel>-admin-automation`. The kubelet (`system:nodes`) is also admitted" |
| 235 (b) | 24, 36 (`PodConnect`), 93–100 (`ProtectedPod`, four prefixes), 112, 134 | "pods named `<rel>-api-*`, `<rel>-worker-*`, `<rel>-authentik-*` and `<rel>-postgres-*` … It matches the pod name" |
| 236 (c) | 37 (`PodCreate(pod, saName)`), 113 (match on `SA(pod.ns, saName)`), 65–69 and 135 (four controllers), 146–150 and 159 (`NameClause`, every requester) | "Pod `CREATE` with `serviceAccountName` … Also admitted: …replicaset-, statefulset-, job-, daemon-set-controller. Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`" |
| 237 (d) | 27, 30, 38–39 (`WorkloadWrite` old/new template account), 114–115, 72–77 and 136 (five controllers) | "`CREATE` and `UPDATE` of Deployments, … whose pod template (old or new) uses either of those service accounts. Also admitted: …" |
| 238 (e) | 40 (`EphemeralUpdate`), 116, 137 | "`UPDATE` on `pods/ephemeralcontainers` for the pod names in (b)" |
| 239 (f) | 21 (`PodImages`), 41–42 (`PodUpdate`), 103–106 (`ImagesDiffer`), 117, 138 | "when any `spec.containers[*].image` or `spec.initContainers[*].image` differs … Metadata-only pod updates are not matched" |
| 242–243 | 43 and 118 (`ScaleUpdate` not matched), 44 and 119 (`OtherOp`) | "creates or updates (other than through the `scale` subresource) a workload…"; 242's "`kubectl run --overrides` … (under a name starting `<rel>-api-`, rule (c)) … need the operators group" supports the name clause binding operators |
| (helpers) | 167–171 (`PrefixOfConcat`), 175–192 (`OperatorMisnamedPodRefused`, P1's concrete counterexample, proved) | — |

**The eleven re-gated candidates:** round 1's tables (`…-formal-verification-1.md`, §0 "Correspondence tables") apply unchanged, with these spec line numbers moved: §6 488 → 490 and 490 → 492; §4.3 374 → 375; §3.5 315 → 316 and 316 → 317.

### Gates

- **G0–G3:** pass for all 13 candidates. G0's verifier results match each `status.json`; G1/G3 found every approved axiom and frozen property text unchanged and no other trusted declaration; G2 reported no contradictory assumptions.
- **G4:** `LoginThrottle`'s model functions carry no `requires`; the six `requires` outside the frozen properties are on proved helper lemmas (`ScoreAfterFailuresExact`, `ScoreAfterFailuresBounds`, `NegativeSumLowerBound`, `RepeatWrongPassword`, `RepeatWrongCode`, `RepeatSuccess`). Every `requires` in `AdmissionPolicy` is frozen property text. The controller checked each correspondence row above against the spec lines it cites: `Step`'s three cases, `Admissible`, `UnknownPlanStep` and `UnknownPairAtOrder12` follow line 158; `Matched`, `ExemptRequester`, `NameClause` and `Admits` follow lines 233–239, with the name clause applied to every requester as line 236 says. The re-gated candidates' non-comment text is round 1's, which passed G4 against the same rule text.

### Obligations

G5 names the csv row that killed each obligation; "own" is the obligation's own row.

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `LoginThrottle` | P1_KnownWrongPasswordsFromZero | §2.5 line 158 | proved (G5: `RepeatWrongPassword`, which its proof calls) |
| | P2_ThreeWrongCodesReachMinusFive | §2.5 line 158 | proved (G5: `RepeatWrongCode`, which its proof calls) |
| | P3_SuccessesFirstOffset | §2.5 line 158 | proved (G5: `RepeatSuccess`, which its proof calls) |
| | P4_InterleavedNoFixedBound | §2.5 line 158; §7 line 538 | proved (G5: `InterleavedFacts`, which its proof calls) |
| | P5_UnknownThirdPlanRefused | §2.5 line 158 | proved (G5: own) |
| | P6_PlanFromZeroCancelledAtFifth | §2.5 line 158 | proved (G5: `ScoreAfterFailuresExact`, which its proof calls) |
| | P7_PlanFromNegativeNeverCancelled | §2.5 line 158 | proved (G5: `ScoreAfterFailuresBounds`, which its proof calls) |
| | P8_UnknownNeverCancelled | §2.5 line 158 | proved (G5: own) |
| | P9_AddressCapNeedsTenIdentifiers | §2.5 line 159 | proved (G5: `NegativeSumLowerBound`, which its proof calls) |
| `AdmissionPolicy` | P1_OperatorsAdmitted | §3.1 line 233 | failed: CONFIRMED (§2 item 1) |
| | P2_ClusterAdminDenied | §3.1 line 233 | proved (G5: own) |
| | P3_MetadataOnlyPodUpdateAdmitted | §3.1 line 239 | proved (G5: own) |
| | P4_KubeletMintsTokens | §3.1 line 234 | proved (G5: own) |
| | P5_ScaleAdmitted | §3.1 line 243 | proved (G5: own) |
| | P6_NoProtectedPodOrTemplateForNonOperators | §3.1 line 237 | proved (G5: own) |
| | P7_MisnamedProtectedPodRefusedForAll | §3.1 line 236 | proved (G5: own) |
| | P8_AdmittedProtectedPodIsGated | §3.1 line 236 | proved (G5: own) |
| | P9_ApiReplicaSetPodsAdmitted | §3.1 line 236 | proved (G5: own) |
| `TenantUserGuard` | P1_NonCanonicalRefusedWithoutCall | §2.1 line 42 | proved (G5: own) |
| | P2_MismatchRefused | §2.1 lines 40–42 | proved (G5: own) |
| | P3_MutationsUseCheckedPk | §2.1 line 42 | proved (G5: own) |
| `InviteQuota` | P1_AtMostHundredReserved | §2.1 line 50 | proved (G5: `CountAfterInvariant`, which its proof calls) |
| | P2_HundredFirstRefused | §6 line 490 | proved (G5: own) |
| | P3_CountMonotone | §2.1 line 51 | proved (G5: `CountAfterInvariant`, which its proof calls) |
| `RoleFunctions` | P1_OperatorImpliesSchemaAdmin | §2.4 line 111 | proved (G5: own) |
| | P2_GroupsIrrelevantWithoutFallback | §2.4 lines 114, 119 | proved (G5: own) |
| | P3_AdminScopeIsOperator | §2.4 line 106 | proved (G5: own) |
| | P4_OperatorImpliesConsoleAdmin | §2.2 line 69 | proved (G5: own) |
| | P5_FallbackGroupIsSchemaAdmin | §2.4 line 111 | proved (G5: own) |
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
| `StarRocksBootstrap` | P1_StartAfterPasswordJoins | §4.3 line 375 | proved (G5: own) |
| | P2_JobSetsPasswordOnFe0 | §4.3 line 375 | proved (G5: own) |
| | P3_OldOrderDeadlocks | §4.3 line 375 | proved (G5: `ApplyOldPreservesStuck`, which its proof calls through `RunOldPreservesStuck`) |
| `AllowList9080` | P1_RootRedirects | §2.7 line 216 | proved (G5: own) |
| | P2_SetupNotForwarded | §2.6 line 192; §2.7 line 217 | proved (G5: `NoClash_Setup`, `NoClash_IfFlowInitialSetup`, `NoClash_ApiInitialSetup`, which its proof calls) |
| | P3_InterfacesRefused | §2.7 line 217 | proved (G5: `NoClash_IfUser`, `NoClash_IfAdmin`, which its proof calls) |
| | P4_AllowedFlowsForwarded | §2.7 lines 204–208 | proved (G5: own) |
| | P5_OtherApiRefused | §2.7 line 217 | proved (G5: `NoClash_Api`, which its proof calls) |
| `RateLimitPartition` | P1_ThirtyFirstRefusedOthersPass | §6 line 490 | proved (G5: own) |
| | P2_SeparateBuckets | §2.1 line 45 | proved (G5: own) |
| `ReadinessMapping` | P1_AuthPendingOnlyFromRefusedLogin | §3.5 line 317 | proved (G5: own) |
| | P2_RowsMeanHealthy | §3.5 line 316 | proved (G5: own) |

**Why `InitialSetupClosure` P1 and P2 are still unfalsifiable.** As in round 1: both properties state the configuration in literals and read only the VA62 axiom. Round 1's mutations, replayed on the unchanged model, are again caught only by the link lemma `ClosureIsAllWithFalse`, which neither property's proof calls.

### Ambiguities that did not reach §3

- **The eleven re-gated candidates:** round 1's ambiguities (`…-formal-verification-1.md`, §0) apply unchanged; their rule text is unchanged.
- **`LoginThrottle`, a wrong MFA code (line 158).** "the validation stage and its login-failure signal each score −1": two clamped −1 steps or one clamped −2. The model uses two steps; the readings give the same score from every row in −5..+5.
- **`LoginThrottle`, an unknown username's plan refused at order 12 (line 158).** Whether it still scores the login failure. The model scores only identification's −1, since the refusal comes before the login stage; P5 does not depend on it, because the row is already at −5.
- **`LoginThrottle`, other identifiers at the address in P4.** The model puts only the pair's row at the address. P4 is an existence claim, so one such scenario discharges it.
- **`LoginThrottle`, attempts outside the row's hour (lines 158, 160).** The model keeps every attempt inside the row's hour, as P3's citation does ("in the row's hour"); a row that expires mid-sequence restarts at 0 and falls outside that citation.
- **`LoginThrottle`, `UnknownPairAtOrder12(0)`.** "its 3rd plan" counts from 1; k = 0 names no plan and returns 0. P5's `requires k >= 1` is frozen text.
- **`AdmissionPolicy`, the pod name rule (c) checks (line 236).** The model reads the final name after `generateName`, which VA96 records ("the generated name is visible to the policy"; a ReplicaSet pod `iverson-api-65948db4fd-dvxrx` admitted). The other reading (the client's object name) is contradicted by that record.
- **`AdmissionPolicy`, the kubelet (line 234).** "The kubelet (`system:nodes`)" is read as group membership; Kubernetes' kubelet usernames are `system:node:<name>`, so no requester has the username `system:nodes`, and the username reading admits no one.
- **`AdmissionPolicy`, namespace scoping (line 230).** As round 1: identities are namespace-qualified by the binding's `namespaceSelector`.

## 2. Literal-wrongness findings

### 1. Line 233 admits operators on every rule, but line 236's name clause refuses them a misnamed protected-account pod (`AdmissionPolicy` P1_OperatorsAdmitted, §3.1 line 233)

**The rule and the property.** P1_OperatorsAdmitted: a requester whose groups intersect `admission.operatorGroups` is admitted. Citation: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else." (§3.1 line 233). Round 1 proved it; round 9's name clause, added to rule (c) at line 236, now refuses the operators group too: "Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`".

**The scenario.** Release `iverson` in namespace `iverson`, `admission.operatorGroups = {iverson-operators}`. `alice`, in `iverson-operators`, sends a Pod `CREATE` for a bare pod `steal` in `iverson` with `serviceAccountName: iverson-api`. Line 233 admits her; line 236 refuses the pod, because `steal` does not start with `iverson-api-`. The model's helper `OperatorMisnamedPodRefused` proves the refusal for exactly this request, and P1 fails on it.

**Hand trace** (`ddv-run/c/AdmissionPolicy/trace.md`): line 230's namespace selector applies; rule (c) matches (the account is `<rel>-api`); line 233 admits an operator; line 236 refuses a protected-account pod not named `<rel>-api-…`, "the operators group … included". The two sentences give opposite outcomes for one request. Line 242 ("`kubectl run --overrides` with those accounts (under a name starting `<rel>-api-`, rule (c))") and VA96 (`kubectl debug --copy-to=dbg-copy` denied and `--copy-to=iverson-api-dbg` admitted under a clause on every requester) agree with line 236, and goal #11/#15's "copy it from a pod" rests on it (line 236: "so (b), (e) and (f) match every pod that holds a protected token", P8 proved). No existing system is involved, so the hand trace confirms the failure.

**Proposed spec fix.** In §3.1 line 233, find:

```text
Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else.
```

replace with:

```text
Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else; rule (c)'s name clause binds the operators group too.
```

P1 is then restated next round to the carved-out sentence (operators admitted except for a protected-account pod named outside `<rel>-api-`, which P7 covers).

Evidence: not model-checked: edits the cited text of P1_OperatorsAdmitted.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §3 item 1 (when an unknown username's identification failure is scored, `LoginThrottle` P2).** The user picked (b), verify before planning. The live count (VA90) found the 3rd plan refused after 2 failed attempts, and line 158 now states it. This round's P5_UnknownThirdPlanRefused, re-frozen to that text, is proved and killed by its own mutation.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes.** §2 has one item: line 233's unconditional operator admission contradicts rule (c)'s name clause at line 236, which binds the operators group too. The fix is one clause at line 233.

Also, two obligations are `proved but unfalsifiable` (`InitialSetupClosure` P1 and P2), as in round 1: they read only the VA62 axiom.

The other 50 obligations are proved and killed by a G5 mutation, including all nine of `LoginThrottle`'s re-frozen properties and `AdmissionPolicy`'s new name-clause properties P7–P9. §3 is empty.
