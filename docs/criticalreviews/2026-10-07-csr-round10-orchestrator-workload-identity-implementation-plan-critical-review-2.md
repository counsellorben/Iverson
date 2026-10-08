# Critical Implementation Review: 2026-10-07-csr-round10-orchestrator-workload-identity-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-07-csr-round10-orchestrator-workload-identity-implementation-plan.md
**Artifact HEAD at review:** f0b6937397e0e0aa378552da48d00635f2fe3aa0
**Verified plan-level assumptions section:** present

⚠️ 3 commits since plan-write time (SHA 59782440); cited file:line references re-checked under §1. The three commits are the plan (`15f8aa93`), CIR-1 (`958a6f4f`) and CIR-1's fixes (`f0b69373`). `git diff --stat 59782440 HEAD -- Iverson.Server Iverson.Clients` is empty, so no code or chart file the plan touches has moved.

The prior review is CIR-1 (anchor `15f8aa93…`). The §0 sweep below was built before CIR-1 was read in detail. CIR-1 was then read for the never-re-raise rule and for row family (b).

## 0. Coverage enumeration

### Probe key

All probes used throwaway resources under `scratchpad/cir-c2-2/`:
- two detached worktrees at `f0b69373`, removed with `git worktree remove` (`git worktree list | command grep -c cir-c2-2` gives `0`);
- `git archive` chart copies;
- the Testcontainers fixture's own containers, which Ryuk removed. Afterwards `docker ps -a --filter label=org.testcontainers=true` listed only three containers exited 9 days or more ago. The one Testcontainers network present was created 2026-09-23.

No kind cluster or compose project was started. Nothing belonging to the user (`iversonserver`, `rfdt-*`, `tei_models`) was touched, no `.env` file was read, and the main checkout's `git status --short` is the same as at session start.

- **Q0. Anchor and amendment detection.**
  - `git log -1 -- <plan>` = `f0b69373…`, and `git status --porcelain -- <plan>` is empty, so the header uses the SHA form.
  - Content-identity check against CIR-1's anchor: `git rev-parse 15f8aa93:<plan>` = `d993e59c…`, but `git hash-object <plan>` = `c1612b6e…`. They differ, so the amendment set is `git diff 15f8aa93 -- <plan>`: **18 hunks**.
  - Forward window `git log 15f8aa93..HEAD -- <plan>`: one commit, `f0b69373 applied 5 fixes from …-critical-review-1.md to …`. That is the update skill's in-band shape.
  - Reverse window: `git merge-base --is-ancestor 15f8aa93 HEAD` succeeds, and `git log HEAD..15f8aa93 -- <plan>` is empty.
  - Result: no out-of-band amendments. All 18 hunks are reviewed at fix-equivalent rigor in family (c).
- **Q1. The embedded code equals the proofs.** Every fenced block was extracted from the plan (38 blocks) and compared:
  - The handler (`:391`) is byte-equal to `c2/proto/Handler.cs` (md5 `c8ad9a1c…`).
  - The handler tests (`:192`) are byte-equal to `c2/code/AuthentikOrchestratorTokenHandlerTests.cs` (`efddc9e5…`).
  - `t4_edits.py` (`:996`, `0884b482…`) and `check_t4.py` (`:1343`, `3b3da1ec…`) are byte-equal to their `c2/` copies.
  - The T3 block (`:773`) equals `c2/code/IntegrationFact.cs.txt` except for one leading blank line in the file. `t1_t3_apply.py` strips that line with `.lstrip("\n")`.
  - All 12 T2 snippets (`:566`–`:735`) occur verbatim in `c2/t1_t3_apply.py`: a substring check gave `True` ×12.
- **Q2. T1 and T2 run.** In a detached worktree at `f0b69373` with `t1_t3_apply.py` applied, T2 Step 5's command (its `cd` target redirected to the worktree) gave `Passed!  - Failed: 0, Passed: 42`. None of the build's warnings is in a file T1–T3 creates or edits. The only warnings are pre-existing CS0618 and CS8604 warnings in other files.
- **Q3. T3 run, with CIR-1's fix as applied.** In the same worktree, T3 Step 3's filter (with `--no-build -v n`) ran 10 tests: 10 `Passed` lines and 0 `Failed` lines. They include `OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists [20 s]`. So every POST in the fixed `PostAsync` helper was accepted by the live Authentik 2026.5.3.
- **Q4. T4 Steps 3–6 as written, each in an `env -i HOME=… PATH=… bash --noprofile --norc` shell.** The `source` line pointed at a scratch `env.sh` whose `SCR`/`WT` named a scratch dir and a second throwaway worktree. In S3, the `SCR=`/`WT=` lines were rewritten the same way. S6's `git commit` became `git commit --dry-run`. Nothing else was changed.
  - S3 printed `checker fails on base (expected)` ×5.
  - S4 printed `t4 edits applied`.
  - S5, for every overlay: `[<overlay>] PASS`; kubeconform `Invalid: 0, Errors: 0`; `other-container criticals changed: 0  relay: 5`; `1 chart(s) linted, 0 chart(s) failed`. `git status --short` listed exactly the six chart files.
  - S6 (dry run) listed exactly those six files under "Changes to be committed", exit 0.
- **Q5. `AuthentikOrchestratorRoleBlueprintTests` against the edited chart:** `Passed: 6`.
- **Q6. CI's kube-score gate, run as `deploy-validate.yml` runs it.** The job is `security-audit`. It runs `kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy --ignore-test container-security-context-user-group-id - < rendered.yaml` under `set -e`, with no `-n`. This used local kube-score 1.19.0, the CI's pinned version.

  | Overlay | Base exit | Base criticals | Edited exit | Edited criticals |
  |---|---|---|---|---|
  | local | 1 | 12 | 1 | 13 |
  | laptop | 1 | 10 | 1 | 11 |
  | aws | 1 | 13 | 1 | 14 |
  | azure | 1 | 13 | 1 | 14 |
  | gcp | 1 | 13 | 1 | 14 |

  The laptop diff adds one `[CRITICAL] Container Resources` (`jwks-relay -> CPU limit is not set`) on `deployment-worker.yaml`, plus the relay's ephemeral-storage lines under a critical class the worker Deployment already has.
- **Q7. CI's kubeconform gate, run as CI runs it:** `kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas` on the edited renders gave `Invalid: 0, Errors: 0` ×5. Adding `-strict` on laptop also gave `Invalid: 0, Errors: 0`, so the 1.30 schema knows the native sidecar's `restartPolicy`.
- **Q8. T0's `env.sh` heredoc, executed.**
  - The `cat > … <<'X'` command was extracted from the plan and its output redirected to a scratch path. `cat -A` shows three lines: `SCR=/tmp/…/c2-sdd$`, `WT=/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity$` and `LIVE=$SCR/live$`. `$SCR` is left literal by the quoted delimiter.
  - Sourced in `env -i bash`, it gives `LIVE=/tmp/…/c2-sdd/live`, and the cwd is `/home/ben/repositories/Iverson`.
  - Line 1 of each of the 8 sourcing blocks (T4 S4/S5/S6 and T6 S3–S7) was run in `env -i bash`, with its path redirected to that copy. Each gave `empty=[]` for `SCR WT LIVE`.
  - Negative control: with the path pointing at a file that does not exist, all three were EMPTY in all 8 blocks.
- **Q9. Commit blocks.** Each block's own `cd`, redirected to the throwaway worktree, was followed by `git add --dry-run <the block's paths>` in `env -i bash`:
  - T1 S5 printed `add '…AuthentikOrchestratorTokenHandlerTests.cs'` and `add '…AuthentikOrchestratorTokenHandler.cs'`;
  - T2 S6 printed three `add` lines;
  - T3 S5 printed one;
  - T5 S2 printed no `fatal: pathspec` (the runbook was unmodified there).

  T4 S6 is in Q4.
- **Q10. Syntax:** `bash -n` on all 20 bash blocks reported no error.
- **Q11. NetworkPolicies selecting the worker pod (`app: iverson-authentik-worker`), laptop render.** The base has `iverson-default-deny` (Ingress, Egress) and `iverson-authentik-egress` (3 rules). The edited render adds `iverson-authentik-worker-apiserver-egress` (1 rule). The new policy is additive under an existing default-deny.
- **Q12. Workloads.** `Authentik__*` env appears only on `Deployment/iverson-api`, container `api`, on laptop and aws, base and edited. The only other workload on the API image is `Deployment/iverson-worker`, with `WorkloadRole=worker`. The edited `api` pod runs `runAsUser 1000`, `fsGroup 1000`, with no init containers and one container.
- **Q13. The harness.**
  - Every Bash call here starts with the cwd at `/home/ben/repositories/Iverson`.
  - A `sleep 90 &` started in one call was alive in the next (`ps -p` showed `sleep`) and was then killed by PID.
  - `kind get clusters` with no `KIND_EXPERIMENTAL_PROVIDER` prints `enabling experimental podman provider`.
  - `systemd-run --user --scope -q … pwd` prints the caller's cwd (`/tmp` after `cd /tmp`).
- **Q14. Searches.** Each search is shown in the row that relies on it.

### (a) Fix neighbourhoods: CIR-1's fix sites and the plan's restatements of them

| # | Site | Disposition |
|---|---|---|
| A1 | §2.1 fix site: T3 `PostAsync` helper (`:918-924`) now sends `Json(JsonSerializer.Serialize(body))` | ok [compat]: Q3 ran the class with this helper against the live fixture: 10 passed, 0 failed, and the federation fact passed in 20 s. Q1: the block equals the proof file `t1_t3_apply.py` injects |
| A2 | §2.1 fix site: T3 Step 3 note (`:959`) | ok [compat]: its claim that a chunked body arrives empty and every field is reported missing matches `IdpAdminClient.cs:339-347` as read at HEAD ("Authentik's ASGI server silently DISCARDS a chunked request body … Every request body in this class must therefore go through here"). The length-delimited form it prescribes is the one Q3 ran green |
| A3 | §2.1 restatement: Verified row 32 | ok [compat]: same evidence as A1 and A2. The row's "class passed 10/10" is reproduced by Q3 |
| A4 | §2.1 restatement: does any other plan text still prescribe a chunked body? | ok [negative]. Search: `command grep -n 'PostAsJsonAsync\|JsonContent' <plan>`. Result: 2 hits, `:156` (row 32) and `:959` (the Step 3 note), both naming it as the thing *not* to use. No code block contains it |
| A5 | §2.2 fix site: Global "Shell state" rule (`:47`) | ok [compat]: the rule's mechanism, executed. Q8 shows the sourced file sets `SCR`/`WT`/`LIVE` in a clean shell, with a negative control that comes out EMPTY. Q13 shows the cwd reset the rule describes. The rule's wording against all 32 rows is in R7 |
| A6 | §2.2 fix site: T0 Step 1 writes `env.sh` (`:172-176`) | ok [compat]: Q8 executed the plan's heredoc verbatim (only the output path redirected). The output content is exact, and `LIVE` expands at source time |
| A7 | §2.2 fix sites: 8 `source` lines (T4 S4/S5/S6, T6 S3–S7) | ok [totality]: population = every block whose first line is `source …/c2-sdd/env.sh`. `head -1` over the 20 extracted bash blocks finds exactly 8: b27, b28, b29, b34, b35, b36, b37, b38. All 8 gave `empty=[]` (Q8). Q4 executed the T4 three end-to-end |
| A8 | §2.2 fix sites: `cd` added to T1 S5, T2 S6, T3 S5, T5 S2 (commits) and T2 S5 (tests) | ok [compat]: Q9 for the four commits. For T2 S5, Q2 ran its exact `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter …` from `<worktree>/Iverson.Server`, which is that `cd`'s target shape |
| A9 | §2.2 restatement: Verified row 33 | ok [compat]: "variables from an earlier block are unset and the cwd is the main checkout". Q13 shows the cwd. Q8's negative control shows variables absent without the source. The row's P16 clause covers the inline-line form, and Q8 now covers the `env.sh` form the fix actually chose |
| A10 | §2.2 restatement: the Global rule's "Eight blocks" (fix description) | ok [totality]: 8 counted in A7 |
| A11 | CIR-1 span-check restatements: Verified rows 34, 35, 36 (added in the same hunk) | ok [compat] for all three. Row 36: a read of this round's edited laptop render (the Q6 render) shows `Authentik__OrchestratorAssertionPath` = `/var/run/secrets/iverson/authentik-orchestrator/token` = mountPath + `/` + projected `path` (`equal: True`). Rows 34 and 35: CIR-1 P9 executed both against a real 2026.5.3, and the inputs P9 rendered are unchanged this round. `t4_edits.py` md5 is `0884b482…`, equal to the P9-era `c2/t4_edits.py`. `git diff --stat 15f8aa93 HEAD -- Iverson.Server` is empty, so the chart P9 rendered is byte-for-byte the chart T4 edits now. Each row's text matches CIR-1's P9 bullets |

### (b) Fix texts that intersect later discoveries

| # | Intersection | Disposition |
|---|---|---|
| B1 | CIR-1 §2.2's fix text said "T4 S3 and T6 S2, which already define their variables first, run correctly". The applied Global rule says "Blocks that use `$SCR`, `$WT` or `$LIVE` start with `source …`" | dropped. The rule's wording does not match 2 members (T4 S3 and T6 S2 set the variables inline and do not source). The values they set are identical to `env.sh` (`SCR=…/c2-sdd`, `WT=…/csr10-orchestrator-workload-identity`, `LIVE=$SCR/live`; compare plan `:1445-1446` and `:1563-1564` with Q8's `cat -A`), and Q4 ran T4 S3 green in a clean shell. Nothing breaks at execution time |
| B2 | CIR-1 P11 (a background `&` process survives into the next call) against the Global rule's "no variable or working directory carries over" | ok [compat]: Q13 reproduces the survival. The rule names only variables and the cwd, so it doesn't contradict this. T6 S5 starts port-forwards that T6 S6 uses and T6 S7 kills by PID file |
| B3 | Verified row 22 and T4 S5's note ("The relay's 5 criticals are known and accepted") against this round's discovery that CI runs kube-score as a gate (`deploy-validate.yml` `security-audit`, under `set -e`) | dropped. The gate already exits 1 on every overlay at `f0b69373` (Q6: base `exit=1` ×5). The edit adds one critical class on the worker Deployment but cannot turn a passing job into a failing one. The two ignored tests (image-pull-policy, user-group-id) are 2 of the relay's 5, and the remaining 3 match row 22's list |
| B4 | Spec CDR-1 and CDR-2 probe results against the two fix topics | ok [negative]. Search: `command grep -n -i 'chunk\|PostAsJson\|JsonContent\|length-delimited\|env\.sh\|fresh shell\|cwd'` over both `…-design-critical-review-*.md`. Result: 0 hits, so there is no shared probe evidence to reconcile |
| B5 | The §2.1 fix against Verified row 8 (T1–T3 code compiles), measured at `59782440` with the pre-fix helper | ok [compat]: Q2 built the post-fix T3 code (in the same test assembly as the 42-test run) with no new warnings |

### (c) Amendment hunks: `git diff 15f8aa93 -- <plan>`, 18 hunks, all in-band (Q0)

| Hunk (new-file line) | Content | Disposition |
|---|---|---|
| H1 `@@ +47` | Global "Shell state" rule | → A5, and R7 for the population |
| H2 `@@ +156-160` | Verified rows 32–36 | → A3, A9, A11 |
| H3 `@@ +172-176` | T0 writes `env.sh` | → A6 |
| H4 `@@ +544` | T1 S5 `cd` | → A8 (Q9) |
| H5 `@@ +743` | T2 S5 `cd` | → A8 (Q2) |
| H6 `@@ +752` | T2 S6 `cd` | → A8 (Q9) |
| H7 `@@ +920` | T3 `PostAsync` body | → A1 |
| H8 `@@ +959` | T3 S3 note | → A2 |
| H9 `@@ +969` | T3 S5 `cd` | → A8 (Q9) |
| H10–H12 `@@ +1459, +1468, +1493` | T4 S4/S5/S6 `source` | → A7 (Q4 executed all three) |
| H13 `@@ +1537` | T5 S2 `cd` | → A8 (Q9) |
| H14–H18 `@@ +1574, +1593, +1615, +1653, +1683` | T6 S3–S7 `source` | → A7 (Q8). The rest of each block's content is unchanged from `15f8aa93`; see T6 rows |

### Tasks × surfaces (fresh sweep)

| # | Task / surface | Disposition |
|---|---|---|
| T0 | Step 1: worktree, scratch dir, `env.sh` | ok [compat] for `env.sh` (A6). The `git worktree add … main` from the main checkout is **not load-bearing**: it is a standard command, and Expected is checked by eye |
| T1a | Test code | ok [compat]: Q2 (the 9 handler tests are inside the 42). Q1: equal to the proof file |
| T1b | Handler, static | ok [compat]: Q2 builds it with no warning in the file |
| T1c | Handler, dynamic: the exchange host comes from the request URI | ok [negative]. Search: every request site in `IdpAdminClient.cs` (`command grep -n 'GetAsync\|PostAsync\|SendAsync\|new Uri\|HttpMethod'`). Result: all are relative literals, at `:71`, `:107-109`, `:148` (paging builds `"/api/v3/core/users/?page={n}"`, `:197`), `:275`, `:307`, `:317` and `:325`. `new Uri` appears only at `:368`, in `PublicLink`, which rewrites a returned link and sends nothing. So `new Uri(requestUri, "/application/o/token/")` always targets `Authentik:BaseUrl`'s authority, and the assertion never goes to another host |
| T1d | Handler, dynamic: concurrency, lifetime, timeout | dropped. These are CIR-1's T1c–T1e, unchanged code (Q1) |
| T1e | Steps 2/4: commands and Expected | ok, not load-bearing. The `cd` is absolute (R7). `Passed: 9` = 42 − 33 |
| T1f | Step 5: commit | ok [compat] (A8, Q9) |
| T2a | Guard code | ok [compat]: Q2 |
| T2b | `Program.cs` wiring, and the other readers of `Authentik:*` config | ok [negative]. Search: `command grep -rn 'AdminToken\|Authentik:'` over `Iverson.Api` (excluding tests and bin/obj). Result: `Program.cs:178,193` (CA path), `:385-395` (the named client, the one T2 edits), `IdpAdminClient.cs:356,363` (PublicBaseUrl) and `AuthentikTrust.cs:73-90`. Nothing else switches behaviour on `Authentik:AdminToken`, so federated mode leaves no other code path without its credential |
| T2c | Steps 1, 3 and 4: edit anchors | ok [compat]: `t1_t3_apply.py` asserts `count == 1` per anchor and applied at `f0b69373` (Q2). `AuthentikTrustTests.cs` at HEAD has `IdpAdminClient_ReachesAuthentikThroughTheCaTrustingHandler` at `:258`, with the class closing at `:271`, so it is the last member, as Step 1(c) assumes |
| T2d | Step 2 Expected (4 named failures) | ok [compat]: CIR-1 P3 on byte-identical code (Q1). Not re-run |
| T2e | Step 5: command and Expected `42` | ok [compat]: Q2 |
| T2f | Step 6: commit | ok [compat] (Q9) |
| T2g | Consumer impact: non-Development API hosts | ok [negative]. Search: `command grep -rn 'DOTNET_ENVIRONMENT\|ASPNETCORE_ENVIRONMENT'` over `.github` and `Iverson.Server` (yml, yaml, json, cs, runsettings, props, sh). Result: `docker-compose.yml:492,616` (`Production`; `:492` is the API, which sets `Authentik__AdminToken`, so static mode; `:616` is the worker role, which skips) and `launchSettings.json:10,19` (`Development`). Q12: on Helm, only `iverson-api` runs the api role |
| T3a | Code: `PostAsync` helper | ok [compat] (A1) |
| T3b | Code: the nine POST payloads and the three refused exchanges | ok [compat]: Q3, the fact passed |
| T3c | Step 3: command and Expected | ok [compat]: Q3 (10 passed, 0 failed) |
| T3d | Step 4: mutation | ok [compat]: CIR-1 P7 ran with the fix applied, on code byte-identical to now (Q1). Not re-run |
| T3e | The shared fixture's other 9 facts | ok [compat]: Q3 |
| T3f | Step 5: commit | ok [compat] (Q9) |
| T4a | Step 1: `t4_edits.py` anchors at HEAD | ok [compat]: Q4 S4 printed `t4 edits applied` in a fresh worktree at `f0b69373` |
| T4b | Step 2: checker | ok [compat]: Q4 S3 (fails ×5 on the base) and S5 (PASS ×5) |
| T4c | Steps 3–6 as executed | ok [compat]: Q4, with all four Expected lines matched and `git status` showing six files |
| T4d | Runtime: the rendered blueprints apply, and the policy pins | ok [compat]: CIR-1 P9 on byte-identical inputs (A11) |
| T4e | Token path chain | ok [compat] (A11, row 36) |
| T4f | NetworkPolicy: worker-only, additive | ok [compat]: Q11. Q4's checker section 6 asserts the selector and CIDRs ×5 |
| T4g | Consumer: no other workload loses an Authentik credential | ok [compat]: Q12. Base and edited renders both carry `Authentik__*` env only on `iverson-api/api` |
| T4h | Consumer: CI deploy gates | ok [compat] for kubeconform (Q7, CI's 1.30.0 schemas, also `-strict`) and helm lint (Q4 S5). kube-score → B3, dropped (no pass→fail transition) |
| T4i | Consumer: image-scan | ok [compat]: the List step's `sed` over the edited AWS render prints `registry.k8s.io/kubectl:v1.37.1`. The third-party scan is report-only (`image-scan.yml` `--exit-code 0`), so a finding in the relay image cannot fail it |
| T5a | Runbook §9 content | ok [existence]: `command grep -n '^## '` on the runbook gives §1–§8, with the last at `201:## 8. EKS`. §4 at `:108` is "First-deploy gate (every cloud)", the egress section the §9 text cites. The command and its paths are those of T6 S4 (row 34) |
| T5b | Step 2: commit | ok [compat] (Q9). `git ls-files` lists the runbook as tracked, so `git add -f` stages the edit |
| T6a | Step 1: suite loop | ok [totality]: `ls -d *.Tests` in `Iverson.Server` gives exactly the loop's 9 names, and each `$p/$p.csproj` exists (`ok` ×9) |
| T6b | Step 2: prepare | ok, not load-bearing: self-contained (R7) |
| T6c | Step 3: install `main` | ok [compat]: `env.sh`, then `cd $WT` (A7). The kind commands are row 26. Q13 shows kind auto-selects podman for the later `kind load`, which build-and-load-image.sh runs without the env var. Content as CIR-1 T6c, unchanged |
| T6d | Step 3 prose: "Run the slow steps with `run_in_background`" | dropped. Running the whole block as one background call keeps its own `source` and `cd`, so the instruction can be followed without splitting it. Splitting is the executor's choice, not something the plan prescribes |
| T6e | Step 4: upgrade and runbook | ok [compat]: A7, plus CIR-1 P9 and P12 for the content. The `items[0]` jsonpath and the NOT READY awk are **not load-bearing** (verification only; a terminating old pod would show up as a mismatch to re-run, and would not corrupt state) |
| T6f | Step 5: live checks | ok [compat]: A7 and B2 (port-forwards survive into S6). The content is CIR-1 P9 and P12 |
| T6g | Step 6: `CreateTenant` console | ok [compat]: A7. CIR-1 P14 built it. Q13: `systemd-run --scope` keeps `$LIVE/createtenant` as the cwd for `dotnet build` |
| T6h | Step 7: teardown and diff | ok [compat]: A7. `kind delete` with no env reaches the podman cluster (Q13) |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | T1's constructor → T2 `Program.cs` and T3 (two call sites) | ok [compat]: Q2 and Q3 compile and run both |
| C2 | T2's config keys → T4's env `Authentik__OrchestratorAssertionPath` / `…ClientId` | ok [compat]: Q4 checker section 2 ×5 |
| C3 | T4's projected volume → T4's env path | ok [compat] (A11) |
| C4 | T0's `env.sh` (persisted file) → the 8 sourcing blocks in T4 and T6 (**crosses a persistence boundary**) | ok [compat]: Q8 executed the producer (T0's heredoc) and each consumer's line 1 against the produced file |
| C5 | T4 S1/S2 write `$SCR/t4_edits.py` and `$SCR/check_t4.py` → T4 S3–S5 read them (**crosses a persistence boundary**) | ok [compat]: Q4 wrote both files from the plan's blocks to a scratch `$SCR` and ran S3–S5 against them |
| C6 | T4's blueprint paths → the T5 runbook and T6 S4 `ak apply_blueprint` | ok [compat]: CIR-1 P9, inputs unchanged (A11) |
| C7 | T4's pinned `sub`, `client_id` and audience → T6 S5 `exchange()` and `create token` | ok [compat]: CIR-1 P9, inputs unchanged |
| C8 | T6 S2's `$LIVE/before-*.txt` → T6 S7's diffs (**crosses a persistence boundary**) | ok [compat]: S2 sets `LIVE=$SCR/live` inline and S7 sources `env.sh`. Both resolve to `/tmp/…/c2-sdd/live` (Q8 prints `LIVE=<scr>/c2-sdd/live`; S2's `:1563` sets the same string) |
| C9 | T6 S5's port-forward PIDs → T6 S7 `kill` | ok [compat]: Q13 (the process survives, and PID-file kill works) |

### Rule-like content

| # | Rule | Over-inclusion | Under-inclusion |
|---|---|---|---|
| R1 | Guard: exactly one credential mode outside Development | ok [compat]: both modes set → throws (`Guard_RefusesBothCredentialModes`, Q2) | ok [compat]: neither set → throws; federated without a client id → throws; static-only or full federated → passes (Q2) |
| R2 | `Program.cs` attaches the handler when `AssertionPath is { Length: > 0 }` | dropped: CIR-1 R2, code unchanged (Q1) | ok [compat]: the federated theory row attaches it (Q2) |
| R3 | Freshness `now < ExpiresAt − 60 s` | ok [compat]: re-mints at 240 s (Q2; CIR-1 P4 mutation on identical code) | ok [compat]: reuses the token at 239 s (Q2) |
| R4 | Workload policy: `sub`, `aud` (list or string), user and group gate | ok [compat]: wrong `sub`, wrong `aud` and the marker → `invalid_grant` (Q3) | ok [compat]: the pinned assertion mints (Q3). The string `aud` was minted in CIR-1 P9 (unchanged policy text, A11) |
| R5 | Every body sent to Authentik is length-delimited. Population: the T3 `PostAsync` helper (9 sites); T3 `ExchangeAsync`; the T1 exchange; `IdpAdminClient` via `JsonBody`; T6 S5 `exchange()` and the T6 S6 token `curl -d`; T6 S6 gRPC (n/a: HTTP/2 to the API, not to Authentik) | ok [compat]: no site sends a chunked body. The first four were exercised by Q3; the two `curl -d` sites by CIR-1 P9 (unchanged text) | ok [compat]: no site is missed. A4's search finds no `PostAsJsonAsync`/`JsonContent` in any plan code block |
| R7 | The amended Global "Shell state" rule over every plan step with a command or file edit (matrix below) | ok (each cell below) | ok (each cell below) |
| R8 | T4 S5's acceptance rule for kube-score ("other-container criticals changed: 0, relay: 5") against CI's kube-score gate | dropped: the plan's rule accepts nothing CI would newly reject. CI exits 1 before and after (Q6), so there is no pass→fail transition | n/a: the plan's rule cannot reject a render that CI accepts, because CI accepts none (Q6: base exit 1 ×5) |

**R7 matrix: the population is every plan step that carries a command block or a file edit, 32 rows.**

The rows are 20 bash blocks, 2 steps that re-run an earlier block by reference, and 10 non-runnable edits. "Under" means the block uses a variable or relative path it does not establish itself, after its own `source`/`cd`. "Over" means the block's `source`/`cd` moves resolution somewhere wrong: a relative path that no longer resolves, or a value different from the one the block intends. Search target per row: every `$`-variable and every relative path in the block, from the Q-extract listing ("vars / assigns / cds" per block).

| Step (plan line) | Variables and relative paths → where set | Under | Over |
|---|---|---|---|
| T0 S1 (`:168`) | No `$` reads (the heredoc is quoted). `.worktrees/…` and `main` resolve against `cd /home/ben/repositories/Iverson` | ok [negative], **not load-bearing** | dropped: it `cd`s to the main checkout although the Location rule says never to. That is required, because the worktree does not exist yet, and nothing else runs from there |
| T1 S1 | Non-runnable (Write) | n/a: not a shell block | n/a: not a shell block |
| T1 S2 (`:382`) | `Iverson.Api.Tests/…csproj` → `cd <wt>/Iverson.Server` | ok [negative] | ok [compat]: Q2 ran this path from that directory |
| T1 S3 | Non-runnable (Write) | n/a: not a shell block | n/a: not a shell block |
| T1 S4 (by reference to S2) | Same as T1 S2 | ok [negative] | ok [compat] (Q2) |
| T1 S5 (`:543`) | Repo-root-relative paths → `cd <wt>` | ok [compat] (Q9) | ok [compat]: Q9 printed `add '…'` for both paths |
| T2 S1, S3, S4 | Non-runnable (Edit ×7) | n/a: not a shell block | n/a: not a shell block |
| T2 S2 (`:647`) | As T1 S2 | ok [negative] | ok [compat] (Q2) |
| T2 S5 (`:742`) | As T1 S2 | ok [negative] | ok [compat] (Q2) |
| T2 S6 (`:751`) | → `cd <wt>` | ok [compat] (Q9) | ok [compat] (Q9: three `add` lines) |
| T3 S1, S2 | Non-runnable (Edit) | n/a: not a shell block | n/a: not a shell block |
| T3 S3 (`:952`) | As T1 S2 | ok [negative] | ok [compat] (Q3) |
| T3 S4 (by reference to S3) | As T3 S3 | ok [negative] | ok [compat] (Q3) |
| T3 S5 (`:968`) | → `cd <wt>` | ok [compat] (Q9) | ok [compat] (Q9) |
| T4 S1, S2 | Non-runnable (Write to `$SCR/…`; `$SCR` is spelled out absolute at `:46`) | n/a: not a shell block | n/a: not a shell block |
| T4 S3 (`:1444`) | `SCR` and `WT` set inline on lines 1–2; `v` and `o` from the loop; `.`, `$v.yaml` → `cd $WT/…/helm/iverson` | ok [compat]: Q4 executed it in `env -i` | dropped: it sets the variables inline instead of sourcing (B1). The values are identical, and Q4 ran it green |
| T4 S4 (`:1458`) | `$SCR`, `$WT` ← `env.sh` | ok [compat] (Q4, Q8) | ok [compat]: no `cd`. Both paths are absolute after the source (Q4 `t4 edits applied`) |
| T4 S5 (`:1467`) | `$SCR`, `$WT` ← `env.sh`; `.`, `$v.yaml` → `cd $WT/…/helm/iverson` | ok [compat] (Q4) | ok [compat]: Q4, all four Expected lines ×5 |
| T4 S6 (`:1492`) | `$WT` ← `env.sh`; `$C/…` → `cd $WT` | ok [compat] (Q4) | ok [compat]: Q4, exactly 6 files staged |
| T5 S1 | Non-runnable (Edit). Its inner blocks are runbook text | n/a: not a shell block | n/a: not a shell block |
| T5 S2 (`:1536`) | `docs/runbooks/…` → `cd <wt>` | ok [compat] (Q9) | ok [compat]: Q9, no `fatal: pathspec` |
| T6 S1 (`:1550`) | `p` from the loop; `$p/$p.csproj` → `cd <wt>/Iverson.Server` | ok [negative] | ok [totality]: T6a, all 9 csproj paths exist from that directory |
| T6 S2 (`:1562`) | `SCR`, `LIVE`, `WT` set inline; outputs `$LIVE/…` absolute | ok [negative] | dropped: inline, not sourced (B1). The values are identical (C8) |
| T6 S3 (`:1573`) | `$WT`, `$LIVE` ← `env.sh`; `Iverson.Server/…` → `cd $WT`; `.` → `cd $LIVE/mainchart/…` | ok [compat] (Q8: `empty=[]`) | ok [compat]: each relative path is read after the `cd` it needs. `Iverson.Server/deploy/kind/*` exist under the worktree (`ls`: `build-and-load-image.sh`, `kind-config.yaml`, `setup.sh`), and `git archive main Iverson.Server/deploy/helm/iverson` extracts that prefix |
| T6 S4 (`:1592`) | `$WT`, `$LIVE` ← `env.sh`; `.` and `values-laptop.yaml` → `cd $WT/…/helm/iverson` | ok [compat] (Q8) | ok [compat]: `values-laptop.yaml` exists in that directory (Q4 rendered it) |
| T6 S5 (`:1614`) | `$LIVE` ← `env.sh`; no relative paths | ok [compat] (Q8) | ok [negative]: no `cd`, and every path is `$LIVE/…` |
| T6 S6 (`:1652`) | `$LIVE` ← `env.sh`; `createtenant.csproj` and `Program.cs` → `cd $LIVE/createtenant`; `$CID`/`$CSEC` exported in the same subshell | ok [compat] (Q8) | ok [compat]: `systemd-run --scope` keeps that cwd (Q13), and the `dotnet $LIVE/…/createtenant.dll` path is absolute |
| T6 S7 (`:1682`) | `$LIVE` ← `env.sh`; PID files from S5 | ok [compat] (Q8, Q13) | ok [negative]: no `cd`, and every path is absolute |

That is 20 bash blocks plus 2 by-reference rows, and 10 non-runnable edits (T1 S1, T1 S3, T2 S1/S3/S4 counted as 3, T3 S1/S2 as 2, T4 S1/S2 as 2, T5 S1). Every cell is dispositioned.

## 1. Verified-plan-assumptions cross-check

1. Still holds. `ls` of both new paths in the main checkout gives "No such file or directory".
2. Still holds. The runbook's last `## ` heading is `201:## 8. EKS`.
3. Still holds. `t4_edits.py` found each anchor exactly once in all six files at `f0b69373` (Q4 S4).
4. Still holds (Q2: `FirstCall_ExchangesTheAssertion_ThenSendsTheAccessToken` asserts the absolute token URI).
5. Still holds (Q2).
6. Still holds (Q2: the wiring theory's `handlers[^1]` is a `SocketsHttpHandler`).
7. Still holds (Q2).
8. Still holds. Q2 gave 42/42, and it now also compiles the post-fix T3 code (B5).
9. Still holds. The mutations (CIR-1 P3, P4) were run on code byte-identical to the plan's (Q1).
10. Still holds (Q3: the live Authentik accepted the C#-signed assertion against the C#-built JWKS).
11. Still holds (Q3: every route in the row was called and accepted).
12. Still holds (Q3).
13. Still holds (Q3).
14. Still holds (Q3).
15. Still holds (Q3: `iverson-admin-orchestrators` and `tenant-admins` resolved).
16. Still holds. `command grep -rn 'UseEnvironment\|EnvironmentName' Iverson.Api.Tests` finds only `AuthentikTrustTests.cs:177`. `Program.cs:466` is the only `ValidateStartup` call. See also T2g.
17. Still holds (Q5: 6/6 against the edited chart).
18. Still holds. On HEAD, `command grep -rn adminOrchestrator Iverson.Server/deploy` hits only `secret-service-clients.yaml:95-108,199,209,663,673`. On the edited chart, a per-file `command grep -rc` over `charts/authentik/templates/` gives 0 in every file.
19. Still holds. `command grep -rni --exclude='.env*' 'AdminToken\|orchestrator token\|orchestrator-token' docs/runbooks Iverson.Server/deploy/kind | wc -l` gives `0`.
20. Still holds (Q4).
21. Still holds (Q4). It also holds at CI's `-kubernetes-version 1.30.0` (Q7).
22. Still holds. Q4 gave `other-container criticals changed: 0  relay: 5` ×5. See B3 for the CI gate.
23. Still holds (T4i).
24. Still holds (Q4 checker section 6, ×5).
25. Still holds (T6a).
26. Still holds. The three kind files exist, and kind auto-selects podman (Q13).
27. Still holds. `git archive HEAD Iverson.Server/deploy/helm/iverson | tar -x` followed by `helm dependency build` exited 0 and rendered ×5 (Q6 base renders).
28. Still holds. `Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj` exists. `Iverson.Clients/Common/Proto/tenant_lifecycle.proto` `CreateTenantRequest` has fields 1–4: `tenant_id`, `display_name`, `admin_username`, `admin_email`.
29. Still holds. `kubectl create token --help` lists `--audience=[]` and `--duration=0s`.
30. Still holds. The `git log --format=%s -6 main` subjects begin lowercase.
31. Still holds (C1; Q4's `git status` shows T4 touches only chart files).
32. Still holds (A1–A3; Q3).
33. Still holds (A9; Q8, Q13).
34. Still holds (A11: CIR-1 P9, with inputs byte-identical this round).
35. Still holds (A11, same basis).
36. Still holds (A11: `equal: True` on the edited laptop render).

**Span check:**
- **CI's deploy gates under the edited chart** (`deploy-validate.yml`: kubeconform at `-kubernetes-version 1.30.0`, and kube-score with three ignored tests). Rows 21 and 22 ran kubeconform on default schemas and a kube-score diff, not the CI invocations. Verified in-round: Q7 (kubeconform clean ×5, also `-strict`) and Q6 (kube-score exit 1 before and after, so no transition).
- **The `env.sh` form of the shell-state fix.** Row 33's P16 evidence covers the inline line, not the file T0 writes. Verified in-round (Q8).
- **Background processes surviving across blocks** (T6 S5 → S6 → S7). No row covers it. Verified in-round (Q13).
- **Only the api workload carries Authentik credentials, and only it runs the api role on Helm.** VA33 covers the Secrets, not the env. Verified in-round (Q12).

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- CIR-1 §2.1 (T3's helper sent chunked bodies that Authentik discards) is resolved. T3's `PostAsync` now sends `Json(JsonSerializer.Serialize(body))`, Step 3's note explains the trap, and the class passes 10/10 against the live fixture (Q3).
- CIR-1 §2.2 (command blocks relied on shell state from earlier blocks) is resolved. T0 writes `env.sh`, the 8 variable-using blocks source it, and the 4 commit blocks and T2 S5 `cd` first. All 22 runnable rows resolve their variables and paths in a fresh shell (R7, Q4, Q8, Q9).

## 5. Recommendation

✅ **Approve as-is.** §1 has no failed assumptions, and §2 and §3 are empty. Both CIR-1 fixes behave as the plan now states: the T3 fact passed against the live Authentik, and the T4 blocks ran end-to-end from clean shells. The amendment set is exactly CIR-1's in-band fix commit (18 hunks), and every hunk is dispositioned above.

Slot audit: pass (independent audit)
