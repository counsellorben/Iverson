# Dafny Plan Verification: 2026-10-26-csr-round12-supply-chain-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
**Source spec:** docs/specs/2026-10-26-csr-round12-supply-chain-design.md
**Plan SHA:** 680eb9cf5a6dc2d3dd6374446822969441bfed0f | **Replay base:** 680eb9cf5a6dc2d3dd6374446822969441bfed0f | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/

## 0. Coverage enumeration

**Replay.**
- **Same failure as round 1:** `replay.py apply --through-task 14` exited 3 with round 1's failure.
  - It classed every patch step as `local-env` and skipped it.
  - Task 1 Step 3's podman builds then failed inside bwrap ("Failed to obtain podman configuration").
  - The worktree was left at the bare base.
- **What replaced it:** with the user's approval, the worktree was built by hand at `680eb9cf`. T1–T14's patches were cut from the plan text and applied in order with `git apply --check` and then `git apply`; all 14 applied.
- **Candidate files' md5s:** five are identical to round 1's (`DriverRunner.cs` `674ab559…`, `publish-images.yml` `378a89be…`, `apply-tag-ruleset.sh` `e8d674ab…`, `mint_acting_user_token.py` `c1c6f0b1…`, `teacher_rerank.py` `711313b3…`). `ci/pins.py` is `3265a092…`, with round 1's §3.1 option A applied.
- **What was not run:** the plan's generator steps. No candidate reads their output.
- **No §2 finding:** the tool failure is not a defect in the plan.
- **Candidate list:** the user approved round 1's list unchanged. The frozen properties and axioms are byte-identical to round 1's (`diff -r`). Every model is a fresh transliteration by a new prover.

**Scanned blocks.**

| Block | Verdict |
|---|---|
| T11 S1 `ci/pins.py` `norm`, `found`, `consistency` | candidate `Pins.Consistency` |
| T11 S1 `ci/pins.py` `freshness` | candidate `Pins.Freshness` |
| T11 S1 `ci/pins.py` `newest` (`dockerhub-tag`, `maven-central`), `version_key` | candidate `Pins.NewestInSeries` |
| T11 S1 `ci/pins.py` `_scalar`, `load_table` | candidate `Pins.LoadTable` |
| T14 S1 `DriverRunner.cs` `IsAllowed`, `BuildStepEnvironment`, `DriverRunEnvironment` (+ existing `BuildEnvironment`) | candidate `DriverRunner.Environment` |
| T13 S1 `mint_acting_user_token.py` and `teacher_rerank.py`: `is_https_or_loopback` and the `main` guards | candidate `CredentialEgress` |
| T5 S1 `publish-images.yml`: the lookup step's `run:` block and the verify step's `case $BIND` | candidate `PublishResiduals` |
| T6 S1 `apply-tag-ruleset.sh` | candidate `TagRuleset` |
| T1 `Directory.Build.props`, four Dockerfiles, `dependency-scan.yml`, `dotnet-build.yml`, `.gitlab-ci.yml` | excluded: config |
| T2 `codeql.yml`, `.mvn/maven.config` | excluded: config |
| T2 `DriverRunner.cs` (the Java spec, the build call through `ResolveCommand`) | excluded: glue |
| T2 `DriverRunnerTests.cs` | excluded: tests |
| T3 `dependabot.yml`, `dependency-scan.yml`; T4 `.gitlab-ci.yml` (one-line `test -z` guards) | excluded: config |
| T4 and T5 runbook sections | excluded: prose |
| T6 the request body (heredoc JSON) | excluded: config |
| T7 workflows, Terraform, the `image-policies` and `operator-namespace-policies` charts | excluded: config |
| T8 `image-scan.yml`, the postgres chart, Terraform, `cloudnative-pg-values.yaml` | excluded: config |
| T9 `dependabot.yml`, `versions/Chart.yaml`, `ci/gitlab-images/Dockerfile` | excluded: config |
| T9 `setup.sh`/`setup.ps1` `operator_version` (the `awk` rule is unchanged; only its directory becomes a parameter) | excluded: glue |
| T9 `test_chart_pins.py` | excluded: tests |
| T10 `bootstrap/aws/main.tf` | excluded: config |
| T11 `pins.yaml`, `pin-freshness.yml`, one-line workflow edits | excluded: config |
| T11 `pins.py` `http_get`, `run`, `stub_chart`, `chart_app_version`, `eks_kubernetes_version`, `open_issue`, the other `newest` branches, `main` | excluded: I/O-bound |
| T11 `test_pins.py` | excluded: tests |
| T12 `TokenBroker.cs`, `AdminAutomationCredentials.cs` (handler redirect behaviour; error message) | excluded: I/O-bound and glue |
| T12 tests | excluded: tests |
| T13 `run_measurement_batch.sh` (a removed flag) | excluded: glue |
| T13 tests | excluded: tests |
| T14 `RunProcessAsync` (`Environment.Clear()` and the copy) | excluded: I/O-bound and glue |
| T14 `DriverRunnerTests.cs` | excluded: tests |
| T15 `Program.cs`, `Main.java` | excluded: glue |
| T16 runbooks | excluded: prose |

**Approved trusted base.**
- **Uninterpreted declarations, no contract:**
  - `Pins.Consistency`: `ReadFile`, `FindAll`.
  - `Pins.Freshness`: `Kind`, `FoundFirst`, `NewestOf`, `OpenIssue`.
  - `Pins.NewestInSeries`: `DockerInSeries`, `MavenInSeries`, `VersionKey`.
  - `Pins.LoadTable`: `Lines`, `Classify`, `GroupCount`.
  - `CredentialEgress`: `UrlSplit`, `IpLoopback`.
  - `PublishResiduals`: `Inspect`, `NotFoundText`, `DeniedText`, `PackagesApi`, `DigestOf`, `IsSha256Digest`, `IsCommitSha`, `IsXYZ`, `CosignAccepts`.
  - `TagRuleset`: `RepoShape`.
- **Contracted axioms:**
  - `PublishResiduals.ShaFlagBinds`: cosign's `--certificate-github-workflow-sha` binding. Evidence: VA 12, and CIR-3 B-REL, where the release flag set with an all-zero SHA exits 1 with `expected GithubWorkflowSHA`.
  - `TagRuleset.DefaultRepoShape`: the default repository matches the `--repo` ERE. Evidence: Task 6 Step 2's default-repository runs reach the API.
- **Library boundaries not modelled** (approved):
  - In `pins.py`, exceptions outside the caught set and a failing `gh` call.
  - In the Bash scripts, a failing `gh`/`curl` call under `set -e`, `sleep`, and log output.
  - `urlsplit` raising `ValueError`, which the `UrlSplit` axiom cannot express. Such a URL ends either script with a traceback before any request is sent, so no credential is sent.
- **Strings:** Python uses `seq<CodePoint>`; C# uses `seq<char>` under `--unicode-char false`; Bash uses `seq<Byte>`. Bash is not in the skill's language list.
- **Witnesses:** Bash has no confirmed witness runner. No Bash obligation failed.

**Correspondence tables** (worktree `file:line`; every range was introduced by the task named for its candidate).

`Pins.Consistency` (T11 S1)
| Worktree | Dafny | Note |
|---|---|---|
| ci/pins.py:89-90 `norm` | `Norm` | `startswith("v")` is `\|v\| > 0 && v[0] == 118`. |
| ci/pins.py:93-105 `found` | `Found`, `FoundFrom`, `Pairs` | The loop is tail recursion with the branch order kept. `PinError` is an outcome. A `UnicodeDecodeError` from `read()` is outside `ReadFile`. |
| ci/pins.py:108-121 `consistency` | `RowErrors`, `Consistency`, `ConsistencyLoop`, `NormSet`, `Listing` | The `print` (now `values[0][1] if values else '(no locations)'`, which cannot raise) is not modelled. A `UnicodeDecodeError` from `read()` is not `OSError` and is outside `ReadFile`, as in round 1. |

`Pins.Freshness` (T11 S1)
| Worktree | Dafny | Note |
|---|---|---|
| ci/pins.py:36, :218, :244 | `IssuePrefix`, `Title` | |
| ci/pins.py:225-259 `freshness` | `RowStep` (one iteration), `FreshnessLoop`, `Freshness` | The branch order is kept: dependabot, then the EKS skip, then `found`, then `newest`, then `open_issue`, then stale or current. The result is (the `gh` actions, the error count). Prints and issue bodies are not modelled. |

`Pins.NewestInSeries` (T11 S1)
| Worktree | Dafny | Note |
|---|---|---|
| ci/pins.py:174-184 `dockerhub-tag` | `DockerPageTags`, `DockerTags`, `DockerNewest` | The page loop is recursion over `pages`. `PinError` is `None`. The `kind` dispatch and argument unpacking are not modelled. |
| ci/pins.py:201-209 `maven-central` | `MavenReleases`, `MavenNewest` | `v.text or ""` is the input. A `None` text is kept as `""`, which the series regex cannot match. |
| ci/pins.py:184, :209 `max(..., key=version_key)` | `MaxFrom`, `MaxByKey`, `KeyLess` | The first maximal element wins. Python's tuple `<` is lexicographic, with a proper prefix less. |

`Pins.LoadTable` (T11 S1)
| Worktree | Dafny | Note |
|---|---|---|
| ci/pins.py:45-51 `_scalar` | `Strip`, `Scalar`, `ReplaceDoubledQuotes` | `.strip()` uses Python's `isspace` set, which the prover checked against python3 over every code point. |
| ci/pins.py:57-74 the line loop | `ParseLines` | The aliased `row`/`location` dicts are modelled as the last row and the last location. `ParseLines`'s two `requires` are that helper's state invariants. |
| ci/pins.py:75-86 the checks | `Names`, `Count`, `CheckRows`, `CheckLocations` | Same check order. `re.error` is `Crash`. |

`DriverRunner.Environment` (T14 S1; `BuildEnvironment` pre-existing)
| Worktree | Dafny | Note |
|---|---|---|
| DriverRunner.cs:350-364 `BuildEnvironment` | `BuildEnvironment` | A 4-key map. `?? string.Empty` is modelled. |
| DriverRunner.cs:372-385 lists, `IsAllowed` | `Exact`, `AllowedPrefixes`, `CaTrust`, `AnyPrefix`, `IsAllowed` | |
| DriverRunner.cs:388-397 `BuildStepEnvironment` | map comprehension | Keys are distinct, so order cannot matter. Non-string entries are dropped at the boundary. |
| DriverRunner.cs:403-414 `DriverRunEnvironment` | `CopyCaTrust`, then right-biased `+ BuildEnvironment(context)` | The `IDictionary` indexer returns null for a missing key, so it is modelled as `name in parent`. |

`CredentialEgress` (T13 S1)
| Worktree | Dafny | Note |
|---|---|---|
| mint_acting_user_token.py:101-111, teacher_rerank.py:608-619 `is_https_or_loopback` | `MintIsHttpsOrLoopback`, `TeacherIsHttpsOrLoopback` | Both `None` and `""` are falsy. `except ValueError` is `IpLoopback == None`. |
| mint_acting_user_token.py:494-496 | `MintGuard` | `return 2` is `MintRefuse`. |
| teacher_rerank.py:626-629 | `TeacherKey` | `env or None`, then the refusal. |

`PublishResiduals` (T5 S1)
| Worktree | Dafny | Note |
|---|---|---|
| publish-images.yml:128-151 the try loop | `Loop` | Recursion over `try` 1..3. `requires 1 <= try <= 4` is a helper contract. |
| publish-images.yml:152-171 `case $state` and `case $status` | `Lookup` | The arm order is kept. A python3 digest-read failure under `set -e` gives the same `LookupFail`. curl's exit status is not modelled: a non-zero curl exit with a `404` body (for example exit 18) fails the step where the model builds. That errs toward not moving a tag, and only a transport failure reaches it. |
| publish-images.yml:197-210 `case $BIND` | `CertFlags` | release: `[--certificate-identity, WORKFLOW@refs/tags/vTAG, --certificate-github-workflow-sha, GITHUB_SHA]`. |

`TagRuleset` (T6 S1)
| Worktree | Dafny | Note |
|---|---|---|
| apply-tag-ruleset.sh:19-49 arguments, repo check | `ParseArgs`, `TagRuleset` | Same `case` order. `*..*` is `Contains` (byte matching). |
| :74-92 dry run, list, count, create or stop | `TagRuleset` | `grep -c .` is `NonEmptyLines`, and the prover checked under bash that the `[[ … -le 1 ]]` test survives `set -e`. |
| :95-105 read-back, check, PUT | `ReadBack` | |

**Obligations.**

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `Pins.Consistency` | P1_RowPassesIffEveryLocationMatchesAndAllAgree | design PinChecks.P1 (spec line 156); `test_consistency_fails_on_a_second_match_in_one_file_that_differs`, `…_when_a_location_matches_nothing` | proved (G5: `\|NormSet\| > 1`→`> 2`) |
| | P2_LeadingVIgnored | plan T11 S1 "a leading `v` is ignored"; `test_consistency_passes_…_ignoring_a_leading_v` | proved (G5: `Norm` `version[1..]`→`version`) |
| | P3_TableFailsIffSomeRowFails | spec line 156; `test_the_repositorys_table_is_consistent` | proved (G5: `ConsistencyLoop` drops `RowErrors`, helper LoopNoErrors) |
| `Pins.Freshness` | P1_DependabotRowsUntouched | design PinChecks.P2 (spec line 157) | proved (G5: `kind ==`→`!=` KindDependabot) |
| | P2_StaleRowOpensOrUpdatesItsIssue | design PinChecks.P3 (spec line 157); two stale-row tests | proved (G5: `[Create(title)]`→`[]`) |
| | P3_CurrentRowClosesItsIssue | design PinChecks.P3; two current-row tests | proved (G5: `[Close(…)]`→`[]`) |
| | P4_NoRoleLeavesEksUntouched | design PinChecks.P5 (spec line 151) | proved (G5: `!role`→`role`) |
| | P5_NoRoleStillChecksOthers | design PinChecks.P6 (spec line 151) | proved (G5: dependabot guard `&& role`) |
| | P6_RowsInTableOrder | spec line 157; plan T11 S1 | proved (G5: error increment 1→0, helper LoopAccumulates) |
| `Pins.NewestInSeries` | P1_ResultIsListedAndInSeries | design PinChecks.P4 (spec lines 155, 157) | proved (G5: in-series filter `if true`, helper DockerPageTagsMem) |
| | P2_NothingNewerInSeries | kindest/node and maven-central tests | proved (G5: the max fold keeps `best`, helper MaxByKeyFromProps) |
| | P3_EmptySeriesIsAnError | `pytest.raises(… "in the 4 series")` | proved (G5: `\|tags\| == 0`→`<= 1`) |
| `Pins.LoadTable` | P1_RowsAreComplete | `test_the_table_refuses_a_malformed_row` | proved (G5: source check dropped; helper CheckRowsSound, called through ScanSound and ValidateSound) |
| | P2_NamesDistinct | same test, "used twice" | proved (G5: `Count > 1`→`> 2`; same helper chain) |
| | P3_UnknownLineRefused | same test, "not in the table's format" | proved (G5: the final `else` continues, helper ScanRefusesOther) |
| `DriverRunner.Environment` | P1_BuildStepsGetNoHarnessSecret | design HarnessEnv.P1 (spec line 184) | proved (G5: `IsAllowed` filter dropped) |
| | P2_DriverRunGetsNoHarnessSecret | design HarnessEnv.P2; test `DriverRunEnvironment_…_AndNoHarnessSecret` | proved (G5: same line) |
| | P3_DriverRunGetsItsContext | design HarnessEnv.P3 | proved (G5: `AddAll(…, BuildEnvironment(context))` dropped) |
| | P4_DriverRunGetsCaTrust | design HarnessEnv.P4 | proved (G5: `AddCaTrust` skipped) |
| | P5_BuildStepsKeepExactAllowlist | design HarnessEnv.P5 | proved (G5: `name in Exact`→`false`) |
| | P6_BuildStepsOnlyAllowlisted | test `BuildStepEnvironment_IsTheAllowlistOnly`; spec line 184 | proved (G5: `IsAllowed` filter dropped) |
| `CredentialEgress` | P1_MintRefusesPlainRemote | design ToolingCredentialEgress.P1 (spec line 182) | proved (G5: guard `!Mint…`→`Mint…`) |
| | P2_MintAllowsHttpsOrLoopback | design ToolingCredentialEgress.P2 | proved (G5: same line) |
| | P3_BothCopiesAgree | plan T13 S1 | proved (G5: mint `case None => false`→`true`) |
| | P4_TeacherKeyOnlyFromEnvToSafeUrl | design ToolingCredentialEgress.P3 (spec line 183) | proved (G5: teacher guard `!Teacher…`→`Teacher…`) |
| | P5_TeacherRefusesKeyToPlainRemote | `test_main_refuses_to_send_the_key_to_a_plain_remote_base_url` | proved (G5: same line) |
| `PublishResiduals` | P1_DeniedThriceExistingPackageFails | design PublishResiduals.P1 (spec line 64); Expected `api=200 rc=1` | proved (G5: 200 arm `LookupFail`→`LookupBuild`) |
| | P2_DeniedThriceAbsentPackageBuilds | design PublishResiduals.P2; Expected `api=404 rc=0` | proved (G5: 404 arm `LookupBuild`→`LookupFail`) |
| | P3_BuildOnlyWhenTagAbsent | design PublishResiduals.P3 (Goals, spec line 26) | proved (G5: `*` arm `LookupFail`→`LookupBuild`) |
| | P4_OtherApiAnswerFails | design PublishResiduals.P4; Expected `api=500`/`api=000 rc=1` | proved (G5: same line) |
| | P5_ReleaseBindsCommit | design PublishResiduals.P5 (spec line 65) | proved (G5: release SHA `githubSha`→`tag`) |
| `TagRuleset` | P1_PutOnlyAfterAdminReadBack | design RolloutOrder.P2 (spec line 57) | proved (G5: admin-actor test dropped) |
| | P2_NoActivateNoPut | Expected `[none ]`, `[disabled-ok ]` (plan T6 S2) | proved (G5: the no-activate run appends `Put`) |
| | P3_AbsentRulesetCreatedOnce | Expected `[none ] rc=0 put=0 post=1` | proved (G5: `+ [Post]` dropped) |
| | P4_ExistingRulesetLeftUnchanged | Expected `[disabled-ok ] rc=0 put=0 post=0` | proved (G5: `+ [Post]` always) |
| | P5_ActivateOutcomes | the three `--activate` Expected lines | proved (G5: the empty-id stop `if false`) |

39 obligations, 39 proved, each one killed by a single-line mutation (G5). All eight candidates passed G0–G4.

- Design-only: `PublishResiduals.P6_RunbookAsStrictAsWorkflow`: the runbook commands are Markdown prose (T5, T16).
- Design-only: `AdmissionDecision.P1`–`P7`: the rule is Helm `ClusterImagePolicy` config (T7), decided by policy-controller.
- Design-only: `NvdKeyExposure.P1`–`P5`: the rule is CI workflow config (T3, T4).
- Design-only: `CnpgRolloutStability.P1`–`P3`: Helm values and Terraform config (T8); the rollout is CNPG's behaviour.
- Design-only: `ToolingCredentialEgress.P4_RedirectIsOneRequestAndFailure`: the handler's redirect behaviour is library code (T12; T12's 307 tests cover it).
- Design-only: `RolloutOrder.P1`, `P3`, `P4`, `P5`: operator rollout steps (runbook and R1–R4 prose).

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- Round 1 §3.1 (`consistency` raised `IndexError` on a row with no locations): resolved.
  - The user picked option A, applied in `680eb9cf`, which also added plan-level row 62.
  - The fresh round-2 model of the post-fix `consistency` proves P1_RowPassesIffEveryLocationMatchesAndAllAgree as frozen, and G5 kills it.

## 5. Recommendation

✅ Approve as-is: every obligation (39 of 39) is proved and passed G5, and §2 and §3 are empty.
