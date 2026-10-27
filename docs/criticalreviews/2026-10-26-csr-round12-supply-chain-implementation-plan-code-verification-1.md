# Dafny Code Verification: 2026-10-26-csr-round12-supply-chain-implementation-plan (Round 1)

**Implementation plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
**Plan-stage review:** /home/ben/repositories/Iverson/docs/criticalreviews/2026-10-26-csr-round12-supply-chain-implementation-plan-formal-verification-2.md
**Code ref:** d0eab48b734ab0c7023d50d11a58dd51167028fa | **Diff base:** b02124482b317997c0f418385aa762025eb0caba | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/

## 0. Coverage

**Diff base.** `dcv.py inputs` reported two bases: `rule_base` 680eb9cf (the plan's last commit) and `head_base` b0212448 (where `csr12-supply-chain` left `main`). The only commit between them, `b0212448`, adds DPV round 2's review and `.dfy` files, with no code. The user chose `b0212448`.

**Members** (code ref `d0eab48b`; every member found; none `not found`, `signature changed` or `kept by cascade`):

| Candidate | Member (worktree file:line, introduced by) | Changed from the plan-stage model |
|---|---|---|
| `Pins.Consistency` | `ci/pins.py` `norm` 89-90, `found` 93-105, `consistency` 108-121 (T11 S1) | no |
| `Pins.Freshness` | `ci/pins.py` `freshness` 225-259, `norm` 89-90 (T11 S1) | no |
| `Pins.NewestInSeries` | `ci/pins.py` `newest` `dockerhub-tag` 174-184, `maven-central` 201-209, `version_key` 138-139 (T11 S1) | no |
| `Pins.LoadTable` | `ci/pins.py` `_scalar` 45-51, `load_table` 54-86 (T11 S1) | no |
| `DriverRunner.Environment` | `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`: `BuildEnvironment` 350-366 (pre-existing), `IsAllowed` 392-395, `BuildStepEnvironment` 398-407, `DriverRunEnvironment` 413-425 (T14 S1, then final-review fix 622a6476) | `IsAllowed`: yes (the new `DeniedPrefixes = ["GOOGLE_"]` is checked first; `UseSharedCompilation` was added to `AllowedNames` and `MSBUILD` to `AllowedPrefixes`); the others: no |
| `CredentialEgress` | `mint_acting_user_token.py` `is_https_or_loopback` 101-112 and its `main` guard 494-497; `teacher_rerank.py` `is_https_or_loopback` 619-630 and its `main` key lines 637-640 (T13 S1) | no (only the line numbers moved) |
| `PublishResiduals` | `.github/workflows/publish-images.yml`: the lookup `run:` block 128-171 and the verify step's `case $BIND` 197-210 (T5 S1) | no |
| `TagRuleset` | `Iverson.Server/deploy/scripts/apply-tag-ruleset.sh` 1-106 (T6 S1, then final-review fix f62dc20e) | yes: line 101's second test is now the exact tail `*" bypass_actors=RepositoryRole:5:always"`. The model's `EndsWith(readback, AdminTail)` (`AdminTail = " " + AdminRoleActor`) replaces `Contains(readback, AdminRoleActor)`. The helper `TailImpliesActor` proves the tail implies containment. |

**Correspondence.**
- Every candidate other than `DriverRunner.Environment` and `TagRuleset` keeps the plan-stage transliteration line for line, so its correspondence table is the plan-stage review's.
- `DriverRunner.Environment`: `AllowedNames` 372-376 → `Exact` (+`UseSharedCompilation`); `AllowedPrefixes` 378-383 → `Prefixes` (+`MSBUILD`); `DeniedPrefixes` 385 → `DeniedPrefixes`; `IsAllowed` 392-395 → `!AnyStartsWith(DeniedPrefixes, name) && (name in Exact || AnyStartsWith(Prefixes, name))`, in the code's order. The new helper `ExactAllowed` proves every exact name passes the denial.
- `TagRuleset`: line 101 → the `ReadBack` check described above; every other line maps as at plan stage.
- G4: neither model has a `requires` beyond its helpers' own state invariants, and each deviation is the code's own change.

**Obligations.**

| Candidate | Obligation | Status |
|---|---|---|
| `Pins.Consistency` | P1_RowPassesIffEveryLocationMatchesAndAllAgree | proved (G5: `\|NormSet\| > 1`→`> 2`) |
| | P2_LeadingVIgnored | proved (G5: `Norm` keeps the `v`) |
| | P3_TableFailsIffSomeRowFails | proved (G5: the loop drops `RowErrors`, helper LoopNoErrors) |
| `Pins.Freshness` | P1_DependabotRowsUntouched | proved (G5: `kind ==`→`!=` KindDependabot) |
| | P2_StaleRowOpensOrUpdatesItsIssue | proved (G5: `[Create(title)]`→`[]`) |
| | P3_CurrentRowClosesItsIssue | proved (G5: `[Close(…)]`→`[]`) |
| | P4_NoRoleLeavesEksUntouched | proved (G5: `!role`→`role`) |
| | P5_NoRoleStillChecksOthers | proved (G5: the dependabot guard gets `&& role`) |
| | P6_RowsInTableOrder | proved (G5: error increment 1→0, helper LoopAccumulates) |
| `Pins.NewestInSeries` | P1_ResultIsListedAndInSeries | proved (G5: series filter `if true`, helper DockerPageTagsMem) |
| | P2_NothingNewerInSeries | proved (G5: the max fold keeps `best`, helper MaxByKeyFromProps) |
| | P3_EmptySeriesIsAnError | proved (G5: `\|tags\| == 0`→`<= 1`) |
| `Pins.LoadTable` | P1_RowsAreComplete | proved (G5: source check dropped, helper CheckRowsSound) |
| | P2_NamesDistinct | proved (G5: `Count > 1`→`> 2`, helper CheckRowsSound) |
| | P3_UnknownLineRefused | proved (G5: the final `else` continues, helper ScanRefusesOther) |
| `DriverRunner.Environment` | P1_BuildStepsGetNoHarnessSecret | proved (G5: `IsAllowed` filter dropped) |
| | P2_DriverRunGetsNoHarnessSecret | proved (G5: same line) |
| | P3_DriverRunGetsItsContext | proved (G5: `AddAll(…, BuildEnvironment(context))` dropped) |
| | P4_DriverRunGetsCaTrust | proved (G5: `AddCaTrust` skipped) |
| | P5_BuildStepsKeepExactAllowlist | proved (G5: `name in Exact`→`false`, helper ExactAllowed) |
| | P6_BuildStepsOnlyAllowlisted | proved (G5: `IsAllowed` filter dropped) |
| `CredentialEgress` | P1_MintRefusesPlainRemote | proved (G5: guard `!Mint…`→`Mint…`) |
| | P2_MintAllowsHttpsOrLoopback | proved (G5: same line) |
| | P3_BothCopiesAgree | proved (G5: mint `case None => false`→`true`) |
| | P4_TeacherKeyOnlyFromEnvToSafeUrl | proved (G5: teacher guard `!Teacher…`→`Teacher…`) |
| | P5_TeacherRefusesKeyToPlainRemote | proved (G5: same line) |
| `PublishResiduals` | P1_DeniedThriceExistingPackageFails | proved (G5: 200 arm → `LookupBuild`) |
| | P2_DeniedThriceAbsentPackageBuilds | proved (G5: 404 arm → `LookupFail`) |
| | P3_BuildOnlyWhenTagAbsent | proved (G5: `*` arm → `LookupBuild`) |
| | P4_OtherApiAnswerFails | proved (G5: same line) |
| | P5_ReleaseBindsCommit | proved (G5: release SHA `githubSha`→`tag`) |
| `TagRuleset` | P1_PutOnlyAfterAdminReadBack | proved (G5: the admin-actor test dropped) |
| | P2_NoActivateNoPut | proved (G5: the no-activate run appends `Put`) |
| | P3_AbsentRulesetCreatedOnce | proved (G5: `+ [Post]` dropped) |
| | P4_ExistingRulesetLeftUnchanged | proved (G5: `+ [Post]` always) |
| | P5_ActivateOutcomes | unconfirmed (runner has no confirmed form) — see below |

`TagRuleset.P5_ActivateOutcomes`'s first ensures fails in the model.
- **What fails.** The clause says a read-back that merely contains `current_user_can_bypass=always ` and `bypass_actors=RepositoryRole:5:always` activates. The built line 101 deliberately requires the admin actor to be the read-back's whole tail. That is the final whole-branch review's fix M6-1 (commit f62dc20e), made so that a ruleset with extra bypass actors is not activated.
- **The witness.** A Bash script with a fake `gh` first on PATH, run on the built script. With the read-back `enforcement=disabled current_user_can_bypass=always bypass_actors=RepositoryRole:5:always,Team:9:always`, `--activate` exits 1 with no PUT (`DPV-VIOLATION rc=1 put=0`). The control read-back, with the admin actor alone, activates (`ok`). That control is the plan's cited Expected case, `[disabled-ok --activate] rc=0 put=1 post=0`.
- **Why it is unconfirmed.** Bash has no confirmed witness runner, so the witness is UNCONFIRMED by the runner rule.
- **What this means.** The frozen clause generalises the Expected line further than the built gate now allows. The second and third ensures, and P1–P4, verify.

Ignored candidates: none.

Uncovered:
- Iverson.Clients/Java/mvnw:1-338 — logic-bearing (shell: distribution download, checksum check, Java launch); generated by the maven-wrapper-plugin 3.3.4 command in plan T2 S1, not a plan code block
- Iverson.Clients/Java/mvnw.cmd:1-206 — logic-bearing (the Windows counterpart); generated by the same T2 S1 command
- Iverson.Clients/Java/.mvn/wrapper/MavenWrapperDownloader.java:1-96 — logic-bearing (download with optional credentials); generated by the same T2 S1 command
- Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py:351-361 — logic-bearing (the `_NoRedirect` handler and `_OPENER`: a redirect becomes an HTTPError); final-review fix 4d6b9a1b, not a plan code block
- Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py:383-384 — logic-bearing (`call_teacher` opens through `_OPENER`); final-review fix 4d6b9a1b
- Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py:409-409 — logic-bearing (`call_teacher`'s request through `_OPENER`); final-review fix 4d6b9a1b

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes. §2 and §3 are empty and 38 of 39 obligations are proved and passed G5. The exception is `TagRuleset.P5_ActivateOutcomes`, which is unconfirmed: its first clause no longer matches the stricter, intended exact-tail gate. Accepting that status is the user's call.
