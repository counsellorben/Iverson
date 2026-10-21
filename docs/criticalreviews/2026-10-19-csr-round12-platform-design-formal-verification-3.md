# Formal Verification: 2026-10-19-csr-round12-platform-design (Round 3)

**Spec:** /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md
**Spec SHA:** a11e848d722d05cc808840ae796e6afdb2101aa2 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-19-csr-round12-platform-design/

## 0. Coverage

### Rules scanned

**Candidates (13).** The same 13 rules as rounds 1 and 2. The only spec change since round 2 (`a782ac76`) is line 233, in `AdmissionPolicy`'s range, which round 2's §2 item fixed (`a11e848d`). `AdmissionPolicy` was re-frozen, with P1 restated to the corrected sentence, and re-modelled. The other twelve candidates' text and line numbers are unchanged since round 2; with the user's approval they were re-gated on round 2's models (non-comment text identical; header SHA and round updated), through G0–G3 and a replay of round 2's G5 mutations.

| Candidate | Rule | Spec lines | This round |
|---|---|---|---|
| `LoginThrottle` | pair and address throttle, clamp, success offset, MFA −2, unknown-username order, password-stage cancel | §2.5, 146–160; §7 538 | re-gated (round 2 model) |
| `AdmissionPolicy` | VAP rules (a)–(f), their exemptions and rule (c)'s name clause | §3.1, 228–243 | P1 re-frozen; re-modelled |
| `TenantUserGuard` | `RequireUserInTenantAsync` | §2.1, 40–42 | re-gated (round 1 model, via round 2) |
| `InviteQuota` | 100 per tenant per UTC day upsert, no refund | §2.1, 49–55; §6 490 | re-gated (round 1 model, via round 2) |
| `RoleFunctions` | Operator / SchemaAdmin / ConsoleAdmin and the compose fallback | §2.2 69; §2.4 104–119 | re-gated (round 1 model, via round 2) |
| `TokenReviewPin` | orchestrator pin's TokenReview decision | §2.6, 171–178 | re-gated (round 1 model, via round 2) |
| `InitialSetupClosure` | `initial-setup` closure | §2.6, 188–192 | re-gated (round 1 model, via round 2) |
| `StreamRecheck` | 5 s skew and per-message stream re-check | §2.3, 90–97; §6 492 | re-gated (round 1 model, via round 2) |
| `CacheInvalidation` | LISTEN/NOTIFY invalidation | §2.3, 83–88; §6 492 | re-gated (round 1 model, via round 2) |
| `StarRocksBootstrap` | create-user Job order and `MYSQL_PWD` joins | §4.3 #3, 375 | re-gated (round 1 model, via round 2) |
| `AllowList9080` | the `:9080` decision | §2.7, 203–217; §2.6 192 | re-gated (round 1 model, via round 2) |
| `RateLimitPartition` | per-`sub` and `(service, sub)` limiters | §2.1, 44–47; §6 490 | re-gated (round 1 model, via round 2) |
| `ReadinessMapping` | readiness probe mapping | §3.5, 316–317 | re-gated (round 1 model, via round 2) |

**Excluded:** as rounds 1 and 2: the text added since: §2.5 line 154's link lifetimes, line 160's row expiry and cleanup, and line 165's port-forward address (facts about Authentik, covered by VA93, VA94 and VA23); §3.1's tooling list and group plumbing (config); §6 line 495's fresh-install convergence check and §6's other new tests (test procedures); §7's new entries (prose).

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

**`AdmissionPolicy`** (round 3 model):

| Spec lines | Dafny lines | Spec text behind the modelling choice |
|---|---|---|
| 228 | 160–181 (`Admits`) | "one `ValidatingAdmissionPolicy` and one binding"; one policy, decision `Admits` |
| 229 | 157–160 | "`failurePolicy: Fail`, `validationActions: [Deny]`": a matched request its rule does not admit is denied |
| 230 | 77–90 (`RequestNs`), 138 | "bound with a `namespaceSelector` on the release namespace": a request outside `cfg.releaseNs` is not matched |
| 231 | 15, 22–26 (`RendersOk`) | "`admission.operatorGroups` (non-empty, render-checked)" |
| 233 | 98–101 (`IsOperator`), 162 | "admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else"; no cluster-admin exemption |
| 233 / 236 (name clause) | 171–172 | "rule (c)'s name clause binds the operators group too" |
| 234 (a) | 46, 82, 128, 141, 166 | token CREATE for `SA(releaseNs, <rel>-api / <rel>-admin-automation)` (17–19); admitted for an operator or `"system:nodes" in groups` |
| 235 (b) | 48, 105–113, 143, 168 | the four `<rel>-…-*` globs as name prefixes in the release namespace; operators only |
| 236 (c) | 50, 116–117, 129, 145, 171–172 | Pod CREATE whose `SA(pod.ns, saName)` is protected; admitted when operator or one of the four kube-system controllers AND the name starts with `<rel>-api-` |
| 237 (d) | 54, 64–74, 120–121, 130, 147–148, 174 | CREATE/UPDATE of the six workload kinds whose old or new template account is protected; operators or the five controllers |
| 238 (e) | 58, 152, 177 | `pods/ephemeralcontainers` UPDATE on (b)'s names; operators only |
| 239 (f) | 59–61, 154, 179 | main-resource pod UPDATE on (b)'s names, matched only when images differ; operators only |
| 242–243 | 56, 86, 150, 175 | "other than through the `scale` subresource": `ScaleUpdate` never matched |
| (outside the rule) | 63, 155, 180 | every other operation unmatched and admitted |

**`LoginThrottle`:** the table above is round 2's model, carried unchanged.

**The eleven candidates carried since round 1:** round 1's tables (`…-formal-verification-1.md`, §0 "Correspondence tables") apply unchanged, with these spec line numbers moved: §6 488 → 490 and 490 → 492; §4.3 374 → 375; §3.5 315 → 316 and 316 → 317.

### Gates

- **G0–G3:** pass for all 13 candidates. G0's verifier results match each `status.json`; G1/G3 found every approved axiom and frozen property text unchanged and no other trusted declaration; G2 reported no contradictory assumptions.
- **G4:** every `requires` in `AdmissionPolicy` is frozen property text, and its model functions carry none. The controller checked the correspondence rows above against lines 228–243: `Matched` applies the namespace selector (230) and rules (a)–(f) in order; `Admits` admits operators on every rule (233), the kubelet on (a) (234), the four controllers on (c) and the five on (d) (236, 237), and applies (c)'s `<rel>-api-` name clause to every requester (233, 236). `LoginThrottle` and the eleven earlier candidates carry round 2's models, whose non-comment text passed G4 against the same rule text.

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
| `AdmissionPolicy` | P1_OperatorsAdmitted | §3.1 line 233 | proved (G5: own) |
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
- **`AdmissionPolicy`, (c)'s name clause and operators (line 236).** Line 233 now states that the clause binds the operators group too, so the model's reading (operators and controllers both bound) is the text's.
- **`AdmissionPolicy`, (d) on CREATE (line 237).** "pod template (old or new)": a CREATE has no old object, so the model takes the new template's account for both; the other reading would match requests that cannot occur.
- **`AdmissionPolicy`, the kubelet (line 234).** Read as membership of the `system:nodes` group; Kubernetes' kubelet usernames are `system:node:<name>`, so the username reading admits no one.
- **`AdmissionPolicy`, the globs (line 235).** Read as string prefixes in the release namespace. A pod named exactly `<rel>-api` is not gated by (b), and (c)'s name clause gives no protected-account pod that name.
- **`AdmissionPolicy`, "differs" in (f) (line 239).** One sequence of container then init-container images; a per-container comparison agrees, since a pod's container lists cannot change on update.
- **`AdmissionPolicy`, the pod name rule (c) checks (line 236).** The final name after `generateName`, as VA96 records ("the generated name is visible to the policy").
- **`AdmissionPolicy`, namespace scoping (line 230).** As round 1.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 2 §2 item 1 (line 233 admitted operators on every rule, against line 236's name clause).** Fixed in `a11e848d`: line 233 now ends "…and denies everyone else; rule (c)'s name clause binds the operators group too." P1_OperatorsAdmitted, restated to that sentence, is proved and killed by its own mutation.
- **Round 1 §3 item 1 (an unknown username's identification scoring).** Still resolved: `LoginThrottle` P5_UnknownThirdPlanRefused is proved (round 2's model, re-gated).

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**, with §2 and §3 empty. The only obligations not `proved` are `InitialSetupClosure` P1_ClosureDenies and P2_AnyModeStockAdmits, `proved but unfalsifiable` as in rounds 1 and 2: they state the closure's configuration in literals and read only the VA62 axiom, so they check what VA62 implies for those two configurations, not the spec's closure text.

The other 50 obligations are proved and killed by a G5 mutation, including `AdmissionPolicy` P1 restated to line 233's corrected sentence and the name-clause properties P7–P9.
