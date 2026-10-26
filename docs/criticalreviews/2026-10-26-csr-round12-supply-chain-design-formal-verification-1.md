# Formal Verification: 2026-10-26-csr-round12-supply-chain-design (Round 1)

**Spec:** /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md
**Spec SHA:** f6f5db2cbea0425d938739f73a8d6e9147479682 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-26-csr-round12-supply-chain-design/

## 0. Coverage

**Rules scanned.**

| Rule (spec lines) | Verdict |
|---|---|
| §2.3 items 1–3, publish lookup and signature binding (63–66) | candidate `PublishResiduals` |
| §3 admission decision: policies, behaviour table (71–120) | candidate `AdmissionDecision` |
| §3 operator-managed workloads, CNPG rollout (124) | candidate `CnpgRolloutStability` |
| §2.1 NVD key split, GitHub and GitLab (42–51) | candidate `NvdKeyExposure` |
| §4.5 pin table checks, §4.4 EKS skip (151, 155–157) | candidate `PinChecks` |
| §5 item 4, harness environment allowlist (184) | candidate `HarnessEnv` |
| §5 items 1–3, token-client redirects, mint and teacher base URLs (181–183) | candidate `ToolingCredentialEgress` |
| §6 rollout order, §2.2 ruleset activation (57, 189–192) | candidate `RolloutOrder` |
| §2.1 sparse checkout, Maven install, cache paths, cooldown (44–51) | excluded: config / fact about an existing system (VA 2, 3, 47, 50) |
| §2.2 branch protection, push protection, collaborator (56, 58, 59) | excluded: config (live settings); the ruleset guard is `RolloutOrder` P2 |
| §2.3 item 4, image-name binding (67) | excluded: its rule is `AdmissionDecision` P1/P2 |
| §3 install list, network policy, firewall (71–105) | excluded: config |
| §3 Helm interaction (122) | excluded: fact about an existing system (VA 23) |
| §4.1–§4.4 Dependabot entries, stubs, GitLab image stub, EKS role (132–151) | excluded: config; the EKS skip rule is `PinChecks` P5/P6 |
| §4.6 .NET lock files (163) | excluded: config plus existing-tool behaviour (VA 35, 36) |
| §4.7 Maven checksums and wrapper (165–177) | excluded: config plus existing-tool behaviour (VA 37, 38, 49, 50) |
| §5 item 5, samples (185) | excluded: config |
| §6 runbook, §7, §8, §9 | prose (citation sources) |

**Approved environment axioms.**
- `PublishResiduals`: `predicate IdentityMatchesRegexp(identity: string)`, the workflow's general identity regexp (`IDENTITY`, spec lines 91 / 103), left abstract. Citation: VA 12 and CDR-8's cosign run (matching SHA passes, another SHA fails).
- `ToolingCredentialEgress`: `predicate IsLoopbackHost(host: string)`, the loopback test of `CredentialEndpoints.IsHttpsOrLoopback`, left abstract. Citation: VA 43 (`CredentialEndpoints.cs:22-52` defines the https-or-loopback rule) and CDR-8's `dotnet fsi` run of it.

**Correspondence tables** (Dafny line numbers refer to the gated model, before the `// Citation:` lines are inserted).

`PublishResiduals`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 64 | `ApiOutcome` | "404 → absent, build. 200 → the package exists, so the denial is a real failure: fail the run. Any other answer fails the run." |
| line 64; `publish-images.yml:128-159` | `LookupFrom`, `Lookup` (`requires \|answers\| == 3`) | "Today three `denied`/`unauthorized` lookups count as \"not published\""; "after the third denial, query …". Found → existing-tag check, not-found → build, any other registry error → fail, as in today's workflow (`:128-130` "any other failure … stops the run", `:144-147`), which the spec does not change |
| line 65 | `WorkflowAccepts` Release | "adds `--certificate-github-workflow-sha \"$GITHUB_SHA\"` beside the exact `@refs/tags/v$TAG` identity" |
| `publish-images.yml:189`, line 66 | `WorkflowAccepts` Commit / Content | existing commit binding (regexp plus `--certificate-github-workflow-sha`, VA 12); "model images keep the content-tag regexp" |
| line 66 | `RunbookAccepts` | "commit tags use `--certificate-github-workflow-sha <tag>`; release tags use `--certificate-identity <workflow>@refs/tags/vX.Y.Z` plus `--certificate-github-workflow-sha $(git rev-list -n1 vX.Y.Z)`" |

`AdmissionDecision`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 75 | `FirstParty` | "one `ClusterImagePolicy` per first-party image — `iverson-api`, `iverson-admin-ui`, `iverson-ollama-model`, `iverson-tei-model`" |
| line 84, Choices item 4 | `Matches` (prefix) | `glob: "ghcr.io/counsellorben/iverson-api*"`; "Glob per image" |
| lines 112, 117 | `DigestOf` | "By tag | admitted; the stored pod … read `tag@digest`"; "refused if its registry cannot resolve the tag" |
| lines 85–100, Goals 27 | `AttestedUnder` | identity and issuer (`identityOk`), `predicateType: https://spdx.dev/Document`, `subject: [{name: …}]` equal to the policy's own name |
| lines 111–117 | `ImageOk` | unresolvable → refused; every matching policy satisfied; no match → "admitted unverified (`no-match-policy: allow`)" |
| lines 115, 118 | `AllOk`, `Admit` | "Bad init container only | refused"; "Engine down | refused in the release namespace (failurePolicy Fail); other namespaces unaffected" |
| lines 116, 119–120 | not modelled | workload-object checks, Sigstore unreachable, TUF restart: no property cites them |

`CnpgRolloutStability`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| lines 117, 124; VA 45 | `Rewrite` | a tag-only reference gains `@digest`; "the `tag@digest` string the webhook writes back unchanged" |
| line 124; VA 45 | `NeedsRollout` | "CNPG rolls an instance whose stored image string differs from its target"; "compares each instance's `bootstrap-controller` init-container image with its `OPERATOR_IMAGE_NAME`" (OR of the two comparisons) |

`NvdKeyExposure`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| lines 43, 49 | `Runs` NvdUpdate | "`if: github.event_name != 'pull_request'`"; GitLab "`nvd-update` (schedule only" |
| line 46 | `Runs` scan, GitHub | "runs on every event, `needs: nvd-update`, `if: ${{ !cancelled() && (… == 'success' \|\| … == 'skipped') }}`" (`!cancelled()` not modelled) |
| line 49 | `Runs` scan, GitLab | "schedule only as today, `needs: [nvd-update]`" |
| lines 45, 46, 49 | `HasKey` | "with `NVD_API_KEY: ${{ secrets.NVD_API_KEY }}`" on `nvd-update` only; scan "with **no key**"; "it alone receives the environment-scoped `NVD_API_KEY`", "no `variables: NVD_API_KEY` entry" |
| lines 44–46 | `ExecutesProjectCode` | "no `pom.xml` is present and no project code or build extension can load", "it runs no repository code, so not `mvnw`"; the scan runs `./mvnw -B install` |
| line 49 | `GitLabScanProceeds` | first script line `test -z "${NVD_API_KEY+x}" \|\| { …; exit 1; }` |

`PinChecks`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 155 | `Source`, `Row` | sources `github-release` … `dependabot`; "one or more **locations**" (each modelled as the version it yields); `dockerhub-tag`/`maven-central` carry a series |
| line 156 | `Consistent` | "every row's locations hold the same version" |
| line 157 | `Newest`, `MaxOf`, `Le` | "the newest release (newest patch within the pinned series for `kindest/node`, compatible versions for EKS add-ons)"; `Le` only orders to pick the newest |
| lines 155, 157 | `Stale` | "per stale row"; "stale when the pinned tag differs" (line 155) |
| lines 151, 157 | `Active` | "for each non-`dependabot` row"; "unset → the EKS rows report \"skipped: no AWS role\" and the rest run" |
| line 157 | `Apply`, `Freshness` | "opens or updates one issue `Pin behind: <name>` per stale row, closes it when current"; a skipped row's issue is left as it was |

`HarnessEnv`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 184 | `Exact`, `Prefixes`, `Allowed` | "(`PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_*`, `USER`, `SHELL`, and the toolchain prefixes `DOTNET_*`, `NUGET_*`, `JAVA_HOME*`, `MAVEN_*`, `M2_HOME`, `GO*`, `NODE_*`, `NPM_CONFIG_*`/`npm_config_*`, `PYTHON*`, `PIP_*`, `VIRTUAL_ENV`)"; `X*` is the prefix `X` |
| line 184 | `BuildEnv` | "starts every process from an empty environment plus an allowlist"; "Build steps (`:175`) get the allowlist only" |
| line 184, VA 40, VA 48 | `CaTrustEnv`, `RunEnv` | "the driver run step adds `BuildEnvironment(context)` (VA 40) and the CA-trust variables `SSL_CERT_FILE`, `SSL_CERT_DIR`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` and `JAVA_TOOL_OPTIONS`" |

`ToolingCredentialEgress`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 182 | `HttpsOrLoopback`, `MintSends` | "must be `https` or a loopback host (the rule of `CredentialEndpoints.IsHttpsOrLoopback`), checked before any request" |
| line 183 | `TeacherKeySent` | "drops `--api-key`; the key comes only from `VLLM_API_KEY` … and is sent only to an `https` or loopback `--base-url`" |
| line 181 | `TokenRequest` | "`AllowAutoRedirect = false` … a 3xx is a failed token request"; "a 307 yields one request and a failure" |

`RolloutOrder`
| Spec | Dafny | Text behind the choice |
|---|---|---|
| line 190 | `Step2Part` | "first, where a GitLab project runs this pipeline (confirmed by the user at the step) …; this comes after the merge … Then, checked on `main`" |
| lines 191, 57 | `Step3Part` | listed order: delete the Dependabot `NVD_API_KEY` "(only after step 2's keyless pass)"; push protection; ruleset create disabled, read back, activate "once the read-back shows the admin role"; remove `vadim82`; "branch protection last, once all 22 checks are green" |
| line 192 | `Step4Part` | "apply `bootstrap/aws`" |
| line 189 | not modelled | step 1 (the branch) has no action |

**G4.** Two `requires` sit on model functions, both in `PublishResiduals`. `Lookup`'s `requires |answers| == 3` is the spec's three tries (line 64). `LookupFrom`'s `requires i <= |answers|` bounds the index of the recursion that `Lookup` enters at `i = 0`; it does not narrow the rule's inputs. Every other `requires` is on a property or helper lemma.

**G5.** The automatic driver mutated operators. Twelve obligations were killed by hand mutations of one line each (diff count 2): the eleven it left unkilled, and `RolloutOrder` P3, whose automatic "kill" mutated a line of its own lemma and was discarded.

**Obligations.**

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `PublishResiduals` | P1_DeniedThriceExistingPackageFails | line 64 "200 → … fail the run." | proved (G5: `LookupFrom` `==`→`!=`, helper AllDeniedLookup) |
| | P2_DeniedThriceAbsentPackageBuilds | line 64 "404 → absent, build." | proved (G5: same line, helper AllDeniedLookup) |
| | P3_BuildOnlyWhenTagAbsent | Goals line 26 | proved (G5: same line, helper LookupFromBuild) |
| | P4_OtherApiAnswerFails | line 64 "Any other answer fails the run." | proved (G5: same line, helper AllDeniedLookup) |
| | P5_ReleaseBindsCommit | Goals line 26 | proved (G5: Release `workflowSha ==`→`!=`) |
| | P6_RunbookAsStrictAsWorkflow | Goals line 26 | proved (G5: workflow Commit `workflowSha ==`→`!=`) |
| `AdmissionDecision` | P1_FirstPartyAdmittedOnlyIfAttestedUnderOwnName | Goals line 27 | proved (G5: `Matches` `&&`→`\|\|`, helper Matches) |
| | P2_CrossImageSwapRefused | line 114 | proved (G5: `Matches` prefix `==`→`!=`) |
| | P3_BadInitContainerRefuses | line 115 | proved (G5: `AllOk` `0 <= i`→`0 < i`, helper SingleOk) |
| | P4_ThirdPartyResolvableAdmitted | line 117 | proved (G5: `Matches` `&&`→`\|\|`, helper Matches) |
| | P5_UnresolvableRefused | line 117 | proved (G5: `case None => false`→`true`) |
| | P6_EngineDown | line 118 | proved (G5: `if !labelled then true`→`false`) |
| | P7_PolicyGlobsDisjoint | Choices item 4 | proved (G5: `Matches` `&&`→`\|\|`, helper DiffNotMatch) |
| `NvdKeyExposure` | P1_KeyNeverWithProjectCode | Goals line 24 | proved (G5: `HasKey` `==`→`!=`) |
| | P2_GitHubScanRunsOnPullRequests | lines 46, 203 | proved (G5: `pl == GitHub`→`!=`) |
| | P3_GitHubScanRunsWhenUpdateSucceeds | line 46 | proved (G5: same) |
| | P4_UpdateHoldsKeyWhenItRuns | lines 45, 49 | proved (G5: `HasKey` `==`→`!=`) |
| | P5_GitLabScanRefusesAKey | line 49 | proved (G5, hand: `!keyPresent`→`true`) |
| `PinChecks` | P1_ConsistencyIffLocationsEqual | line 156 | proved (G5: `Consistent` `==`→`!=`) |
| | P2_DependabotRowsUntouched | line 157 | proved (G5: `Active` `==`→`!=`) |
| | P3_IssueOpenIffStale | line 157 | proved (G5: `Stale` `&&`→`\|\|`, helper Stale) |
| | P4_SeriesNewestStaysInSeries | line 157 | proved (G5: `Newest` `==`→`!=`, helper Newest) |
| | P5_NoRoleLeavesEksUntouched | line 151 | proved (G5: `Active` `&&`→`\|\|`) |
| | P6_NoRoleStillChecksOthers | line 151 | proved (G5: `Freshness` `==`→`!=`, helper Key) |
| `HarnessEnv` | P1_BuildStepsGetNoHarnessSecret | line 184 | proved (G5, hand: `IVERSON_CLIENT_SECRET` added to `Exact`, helper NotAllowed) |
| | P2_DriverRunGetsNoHarnessSecret | line 184 | proved (G5, hand: same, helper NotAllowed) |
| | P3_DriverRunGetsItsContext | line 184 | proved (G5, hand: `+ ctx` dropped from `RunEnv`) |
| | P4_DriverRunGetsCaTrust | line 184 | proved (G5, hand: `CaTrustEnv` dropped from `RunEnv`) |
| | P5_BuildStepsKeepExactAllowlist | line 184 | proved (G5: `Allowed` `\|\|`→`&&`) |
| `ToolingCredentialEgress` | P1_MintRefusesPlainRemote | line 182 | proved (G5: `scheme ==`→`!=`) |
| | P2_MintAllowsHttpsOrLoopback | line 182 | proved (G5: same) |
| | P3_TeacherKeyOnlyFromEnvToSafeUrl | line 183 | proved (G5, hand: `else None`→`else envKey`) |
| | P4_RedirectIsOneRequestAndFailure | line 181 | proved (G5: 3xx `false`→`true`) |
| `CnpgRolloutStability` | P1_PinnedImagesNoRollout | line 124 | proved (G5: `storedPg !=`→`==`) |
| | P2_TagOnlyPostgresRollsOut | line 124 | proved (G5: same) |
| | P3_TagOnlyOperatorRollsOut | line 124 | proved (G5: `storedInit !=`→`==`) |
| `RolloutOrder` | P1_DependabotSecretOnlyAfterPass | line 191 | proved (G5, hand: `if step2Pass`→`if true`) |
| | P2_ActivateOnlyAfterAdminReadBack | line 57 | proved (G5, hand: `if adminShown`→`if true`) |
| | P3_BranchProtectionLastAndGreen | line 191 | proved (G5, hand: `if all22Green`→`if true`) |
| | P4_GitLabScopingBetweenMergeAndChecks | line 190 | proved (G5, hand: `ScopeGitLabKey` moved after `Step2Checks`) |
| | P5_NoGitLabStepWithoutProject | line 190 | proved (G5, hand: `if gitlabProject`→`if true`) |

41 obligations, 41 proved, every one falsifiable.

**Ambiguities that did not reach §3** (their readings agree on every property, or the spec's text settles them):
- `PublishResiduals`: a registry error other than found, not-found or denied. The spec's lines are silent on it, but today's workflow fails the run (`publish-images.yml:128-130`, `:144-147`), and the spec keeps that. P6 relies on "on a tag run `GITHUB_SHA` is the tagged commit" (line 65), which P6's parameters state.
- `AdmissionDecision`: an image matching several policies (all must hold, or the first wins) gives the same result, since P7 proves the four globs disjoint. A digest-pinned reference needs no resolution. Unresolvable references are refused for every image (P5 as approved).
- `CnpgRolloutStability`: "stored image string" is the string after the webhook, and `OPERATOR_IMAGE_NAME` is not rewritten. VA 45 settles both: the engine resolves container, init and ephemeral images (`resolvePodSpec`), not env values, and CNPG compares the stored pod image with `cluster.Status.Image` and the init image with `OperatorImageName`.
- `NvdKeyExposure`: "runs on every event" vs its `if:`. The `if:` governs; P2/P3 hold under both readings. A defined but empty key fails `test -z "${NVD_API_KEY+x}"`, as written.
- `PinChecks`: "stale" as differs from, or older than, the newest. P3 is stated over `Stale`, so both agree; line 155 supports "differs". A skipped EKS row's issue is left as it was: "closes it when current" does not apply to a row that was not checked. Row names are not stated as unique; P3 assumes distinct names, as approved.
- `HarnessEnv`: the allowlist is closed. The prover read an open "(…)", but that came from the elision in P5's citation; spec line 184 lists the names in full. `X*` is a case-sensitive prefix, so any name starting `GO` or `PYTHON` passes. "Get no secrets" is proved for VA 40's five harness secrets. A credential in a variable under an allowed prefix (for example `PIP_INDEX_URL`) passes by the rule as written; no property covers it.
- `ToolingCredentialEgress`: the scheme is compared exactly ("https"). A base URL that is neither https nor loopback gets no key under either reading (refuse the run, or omit the key).
- `RolloutOrder`: "branch protection last" is last within step 3, with step 4 after it. P3's list holds under both readings. Step 3's unguarded actions do not depend on step 2's pass. Activation is skipped, not halted, without the admin read-back.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ Approve as-is: all 41 obligations are proved and passed G5, and §2 and §3 are empty.
