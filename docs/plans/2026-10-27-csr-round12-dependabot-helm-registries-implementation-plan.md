# CSR Round 12 Supply Chain Amendment: Dependabot Helm Registries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-27-csr-round12-dependabot-helm-registries-design.md` (commit SHA: `757bfd39`; critical design review round 4 ✅; design verification: no candidates)

**Goal:** the operators-stub and kind-stub `helm` Dependabot jobs reach their chart repositories and open bump pull requests, with `.github/dependabot.yml` valid.

**Architecture:** `.github/dependabot.yml` regains a top-level `registries:` block of eight `helm-registry` entries, each with a dummy `username: x` / `password: x` (GitHub rejects one without credentials), including `cloudnative-pg.io`, the redirect target, and no entry for `docs.tigera.io`, which refuses basic auth and is reachable undeclared. The three `helm` entries regain `registries: "*"`. The supply-chain runbook's rollout check and the parent plan's assumption 27 are corrected to match.

**Tech stack:** GitHub Dependabot (`version: 2` config, `helm-registry` registries), `gh` CLI, Python 3 with PyYAML and jsonschema for the local checks.

---

## File Structure

- Modify: `.github/dependabot.yml`: the `registries:` block and its comment (replacing `8b273b52`'s interim comment, lines 2-4), and `registries: "*"` on the three `helm` entries (Task 1).
- Modify: `docs/runbooks/csr12-supply-chain.md:35-38`: rollout step 2 item 3 (Task 2).
- Modify: `docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md:137,204`: assumption 27 and plan row 27 marked corrected (Task 2).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and four `critical-design-review` rounds at spec-write time and are NOT re-verified here. Trusted as ground truth (the spec's Verified assumptions, verbatim):

Evidence gathered 2026-10-27. Probe commits are in the private throwaway repository `counsellorben/dependabot-helm-probe`. The run ids are its Dependabot update jobs, and the quoted lines come from their logs, because the repository is deleted after this spec.

1. **An anonymous `helm-registry` is invalid on GitHub, though the public schema accepts it.**
   - Iverson `main` @ `dbb1ffe1`: the `dependabot` check suite 109852522367 failed, once per registry.
   - Probe `c5b76d1` (the same form) failed the same way.
   - The SchemaStore `dependabot-2.0.json` validates Iverson's pre-fix file with 0 errors.
   - The docs say "Declare a registry … even if it allows anonymous access".
2. **Where the verdict is.** `gh api repos/<o>/<r>/commits/<sha>/check-suites --jq '.check_suites[] | select(.app.slug=="dependabot")'`, then `gh api repos/<o>/<r>/check-suites/<id>/check-runs --jq '.check_runs[].output.summary'`. The **Insights → Dependency graph → Dependabot** page (`/network/updates`) returned 404. A push whose range changes `.github/dependabot.yml` gets one `dependabot` suite, on its head commit. The suite holds one check run, named `.github/dependabot.yml` and titled `Dependabot config file validation` when it passes. Single-commit pushes: suites 109889628759 on `8b273b52` and 109939808298 on `d616995d`. Multi-commit pushes: 109852522367 on `dbb1ffe1`, whose push brought the change in through merge `f4515519`; neither `f4515519` nor the changing commit `e9e858d5` has one. 105420512301, 101744928435 and 94568232718 are likewise on the push heads `f1e80a80`, `1f37f1be` and `5c37290f`. A PR merge commit gets the suite too (`dependabot/dependabot-core` #15985, #16015, #13515, #13151); a merge that does not change the file, such as `21aaf096`, carries only `github-actions` suites. The update jobs are separate Actions runs (`gh run list --event dynamic`, titled `helm in <directory> - Update #…`), and their logs carry the `egress not allowlisted` lines and the per-chart outcomes (runs 40560916404, 40560914422 and 40560912616 on `d616995d`).
3. **Credential forms**, each a probe push read through VA 2's commands:

   | Form | Validation | Job |
   |---|---|---|
   | `username` only (`4130587`) | invalid | — |
   | `token: ${{secrets.HELM_DUMMY}}` (`000ada4`) | invalid | — |
   | `username` + `password: ${{secrets.HELM_DUMMY}}` (`73a2fb5`) | valid | run 40545951643: `422 … "Secret Not Found","detail":"HELM_DUMMY"` |
   | literal `username: x` + `password: x` (`4edf2a4`, `f92da96`) | valid ("All changes look good") | runs, log `authenticating helm registry request` |

4. **Every registry host answers the dummy pair with 200.** Runs 40546305100 and 40546305887 (`f92da96`) record these `index.yaml` responses:

   | Host | Response |
   |---|---|
   | `aws.github.io` | 200 ×8 |
   | `cloudnative-pg.io` | 200 ×10 |
   | `kubernetes-sigs.github.io` | 200 ×2 |
   | `kubernetes.github.io` | 200 ×30 |
   | `sigstore.github.io` | 200 ×22 |
   | `starrocks.github.io` | 200 ×9 |
   | `strimzi.io` | 200 ×2 |
   | `cloudnative-pg.github.io` | 301 ×10 |
5. **cloudnative-pg needs its redirect host.**
   - `curl -L` on `https://cloudnative-pg.github.io/charts/index.yaml` makes one redirect, to `https://cloudnative-pg.io/charts/index.yaml`.
   - Run 40544929702 (`4edf2a4`, no `cloudnative-pg.io` registry) logged `egress not allowlisted cloudnative-pg.io` → `403`, and no cloudnative-pg PR was opened, while metrics-server's PR was.
   - With the extra registry (`f92da96`), the job proposed 0.29.1.
   - The other seven URLs do not redirect.
6. **`docs.tigera.io` refuses any basic auth.**
   - `curl` without auth returns 200.
   - `curl -u x:x` returns 403 `<Code>InvalidArgument</Code><Message>Unsupported Authorization Type</Message>`, served by Netlify in front of S3 (`x-amz-request-id`).
   - In-job (run 40546305887): `400 https://docs.tigera.io:443/calico/charts/index.yaml`, then "Error parsing Helm index".
   - The chart files are `github.com/projectcalico/calico` release assets (`tigera-operator-v3.33.0.tgz`).
7. *Withdrawn by CDR-1 §3.1, option B:* it verified the `ci/pins.yaml` `tigera-operator` row, which the design no longer has.
8. *Withdrawn by CDR-1 §3.1, option B:* it ran `ci/test_pins.py` with that row.
9. **The config as first proposed (with the kind entry's `tigera-operator` ignore, since dropped by CDR-1 §3.1, option B), on copies of all three Iverson helm directories** (`c428e40`, from `git archive` of `8b273b52`), passes validation ("All changes look good"). Runs:

   | Run | Entry | Result |
   |---|---|---|
   | 40550675263 | kind | success; `All versions of tigera-operator ignored, no update allowed`; 0 requests to `docs.tigera.io`; metrics-server 3.9.0 → 3.14.0 |
   | 40550674177 | operators | success; PRs for cloudnative-pg 0.29.1, operator 1.11.8, strimzi-kafka-operator 1.3.0, aws-load-balancer-controller 1.9.2 (minor and major ignored, as configured); cluster-autoscaler 9.59.0 and policy-controller 0.10.8 already newest |
   | 40550674103 | umbrella | success; the same image bumps and the same 60 `file://` errors as Iverson's own umbrella run 40543038683 without registries (16:39, after `8b273b52`) |

   The `ignore` rules for `iverson-api`, `iverson-admin-ui` and `registry.k8s.io/kubectl` held in both runs.
10. **Push protection does not scan for generic passwords.** Iverson's `secret_scanning_non_provider_patterns` is `disabled`, so `password: x` is not a push-protection pattern.
11. **Nothing else describes the anonymous form.**
    - `command grep -rn -i 'helm-registry|anonymous|egress not allowlisted|registries'` over `docs/runbooks`, `ci`, `.github` and both stubs finds only `dependabot.yml`, plus `image-scan.yml:101` (an unrelated Docker Hub remark).
    - The runbook's step 2 item 3 (`csr12-supply-chain.md:35-37`) is the only dependent text (§2.3).
12. **The stubs pin what the rollout expects.** The operators stub pins `cloudnative-pg 0.29.0`, `strimzi-kafka-operator 1.1.0` and `operator 1.11.5`, unchanged on `origin/main` @ `21aaf096`. The kind stub there pins `tigera-operator v3.33.0` and `metrics-server 3.14.0` (#233 and #236 merged 2026-10-27).
13. **Some hosts are reachable undeclared, and a declared registry's credentials go only to its own host.**
    - At `8b273b52` (no `registries:`), Iverson's kind run 40543039603 logged `200 https://docs.tigera.io` ×3 and `200 https://kubernetes-sigs.github.io` ×2, with no `egress not allowlisted` or `authenticating` line, and opened #233 and #236. In the same commit's operators run 40543040473, six hosts were blocked (`egress not allowlisted`, 10 each): `aws.github.io`, `cloudnative-pg.github.io`, `kubernetes.github.io`, `sigstore.github.io`, `starrocks.github.io`, `strimzi.io`.
    - Probe `a9fe1d1` (the eight registries and no `ignore`): validation "All changes look good"; kind run 40557957369 (`success`): `GET https://docs.tigera.io…/index.yaml` → `200` with no `authenticating helm registry request (host: docs.tigera.io)` line (the only one is for `kubernetes-sigs.github.io`), `Found latest version v3.33.0 for tigera-operator`, and the bump PR opened.
14. **The dummy password's masking hides no runbook marker.** With `password: x` declared, GitHub masks every `x` in the update-job logs: probe run 40546305100 prints `pro***y` 229 times, and `inde***.yaml`. None of the runbook's log markers (`egress not allowlisted`, `Submitting … pull request for creation`, `No update possible for …`) and none of the nine registry and chart hosts contains an `x`.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-10-27) in a scratch worktree of local `main` @ `757bfd39`, which contains `origin/main` @ `d616995d` plus five spec commits.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `.github/dependabot.yml` lines 2-4 hold `8b273b52`'s interim comment and line 5 is `updates:` | `sed -n 1,5p` of the file |
| 2 | File path | The three `helm` entries carry the anchors Task 1's patch edits: the umbrella's `directories:` list then `    schedule:`, the operators stub's `directory:` then `    schedule:`, the kind stub's `directory:` then `    schedule:`, and the comment `(CSR round 12 §4.1: its own entry).` | Each replacement asserted exactly one match while the patch was built |
| 3 | Code | Task 1's patch applies to HEAD and to `origin/main` | `git apply --check` on both: yes |
| 4 | Code | The patched file parses, gives 0 errors on the SchemaStore `dependabot-2.0` schema, has 8 registries (each `helm-registry` with `url`, `username: x`, `password: x`), none with a `docs.tigera.io` URL, and `registries: "*"` on exactly the three `helm` entries, with no `ignore` on the kind entry | PyYAML + jsonschema 4.19.2 run: `0` errors; `8 True tigera: False`; `['*', '*', '*']`; kind ignore `[None]`; no other entry has `registries` |
| 5 | Code (sibling set) | The patched file's `registries` map and its three `helm` entries (directory or directories, `registries`, `ignore`) equal the GitHub-validated probe `a9fe1d1` (spec VA 13) | Field-for-field comparison with `a9fe1d1`'s `.github/dependabot.yml` from the probe repository: registries equal `True`, helm entries equal `True` |
| 6 | Code | The patch removes only the interim comment and the old operators comment; every other change is an added comment line, a registries line or a `registries: "*"` line | Diff of `origin/main`'s file against the patched one, filtered to anything else: only those four removed lines |
| 7 | File path | Runbook step 2 item 3 is `docs/runbooks/csr12-supply-chain.md:35-38` with the **Insights** text Task 2 replaces, and item 4 (**Pin freshness**) starts at line 39 | `sed -n 35,39p` |
| 8 | File path | The parent plan's assumption 27 (line 137) and row 27 (line 204) hold the text Task 2 marks | `command grep -n` for `^27\. \*\*Declaring` and `^\| 27 \|` |
| 9 | Code | Task 2's patch applies to HEAD | `git apply --check`: yes |
| 10 | Command | Each command the new runbook item 3 gives runs read-only against Iverson and prints what the text says it reads | Run as written, placeholders filled from the `d616995d` push: suite `109939808298`; `.github/dependabot.yml success Dependabot config file validation`; the six `helm in` runs; the operators run's six hosts ×10 `egress not allowlisted`; the `dependabot/helm/` PRs including #233 and #236 |
| 11 | Command | The three files are tracked, so `git add <path>` stages them without `-f`; commit subjects are lowercase imperative with no prefix | `git ls-files` lists 3/3; `git log --format=%s -12 -- .github/dependabot.yml docs/runbooks` |
| 12 | Ordering | Task 2 does not depend on Task 1's file content (documentation only); R1 needs both on `main` | Task 2's patch touches no file Task 1 touches |
| 13 | Consumer impact | Nothing in the repository reads the `dependabot.yml` text Task 1 changes, and nothing parses runbook item 3 or the parent plan's assumption 27 and row 27; the five files that name the runbook (`dependabot.yml:45`, `deploy-validate.yml:58`, `apply-tag-ruleset.sh:12`, `bootstrap/aws/main.tf:70`, `ci/pins.py:249`) point at other sections in comments or messages | `git grep -l 'dependabot\.yml'` outside `docs/` and the file: none; `git grep` for the runbook and plan names: those five lines |
| 14 | Consumer impact | Task 1's hunks do not touch `d616995d`'s entries (the `/ci/gitlab-images` docker entry, the Java `ignore`, the Admin UI `opentelemetry` group) | The patch's hunks are at lines 1-7, 108, 116 and 130 of the file; those entries sit between lines 37 and 105 |

## Tasks

### Task 1: The helm registries in `.github/dependabot.yml`

**Files:**
- Modify: `.github/dependabot.yml:1-7,108-113,116-124,130-134`

- [ ] **Step 1: Apply the patch** (from the repository root).

```bash
git apply <<'EOF_PATCH'
diff --git a/.github/dependabot.yml b/.github/dependabot.yml
index cfff9cb2..9f349a42 100644
--- a/.github/dependabot.yml
+++ b/.github/dependabot.yml
@@ -1,7 +1,51 @@
 version: 2
-# CSR round 12 §4.1 declared one anonymous helm-registry per chart repository here, so the helm jobs could
-# reach them; GitHub's validator rejects a helm-registry without credentials and then refuses the whole file
-# (2026-10-27). Until a form it accepts is found, the helm entries below fail on "egress not allowlisted".
+# CSR round 12 §4.1 (amended 2026-10-27, docs/specs/2026-10-27-csr-round12-dependabot-helm-registries-design.md):
+# an update job reaches only allowlisted hosts, and declaring a registry allowlists its host (some hosts,
+# docs.tigera.io and kubernetes-sigs.github.io among them, are reachable without one). GitHub rejects a
+# helm-registry without credentials, so each carries a dummy username and password; these chart repositories
+# are public and ignore them. cloudnative-pg.github.io redirects to cloudnative-pg.io, which needs its own entry.
+# docs.tigera.io refuses basic auth, so it has no entry; the kind job reaches it undeclared.
+registries:
+  cloudnative-pg:
+    type: helm-registry
+    url: https://cloudnative-pg.github.io/charts
+    username: x
+    password: x
+  cloudnative-pg-io:
+    type: helm-registry
+    url: https://cloudnative-pg.io/charts
+    username: x
+    password: x
+  strimzi:
+    type: helm-registry
+    url: https://strimzi.io/charts/
+    username: x
+    password: x
+  starrocks:
+    type: helm-registry
+    url: https://starrocks.github.io/starrocks-kubernetes-operator
+    username: x
+    password: x
+  autoscaler:
+    type: helm-registry
+    url: https://kubernetes.github.io/autoscaler
+    username: x
+    password: x
+  eks-charts:
+    type: helm-registry
+    url: https://aws.github.io/eks-charts
+    username: x
+    password: x
+  sigstore:
+    type: helm-registry
+    url: https://sigstore.github.io/helm-charts
+    username: x
+    password: x
+  metrics-server:
+    type: helm-registry
+    url: https://kubernetes-sigs.github.io/metrics-server/
+    username: x
+    password: x
 updates:
   - package-ecosystem: "nuget"
     directory: "/"
@@ -108,6 +152,7 @@ updates:
     directories:
       - "/Iverson.Server/deploy/helm/iverson"
       - "/Iverson.Server/deploy/helm/iverson/charts/*"
+    registries: "*"
     schedule:
       interval: "weekly"
     # CSR round 12 §4.1: the umbrella's own images (built locally, not on Docker Hub) and the kubectl image
@@ -116,9 +161,10 @@ updates:
       - dependency-name: "iverson-api"
       - dependency-name: "iverson-admin-ui"
       - dependency-name: "registry.k8s.io/kubectl"
-  # The operator charts the cloud Terraform installs (CSR round 12 §4.1: its own entry).
+  # The operator charts the cloud Terraform installs (CSR round 12 §4.1: its own entry, with the registries).
   - package-ecosystem: "helm"
     directory: "/Iverson.Server/deploy/terraform/modules/operators/versions"
+    registries: "*"
     schedule:
       interval: "weekly"
     # These two move in lockstep with other files (see the coupling notes in the stub's Chart.yaml): minor/major bumps are done by hand.
@@ -130,5 +176,6 @@ updates:
   # CSR round 12 §4.2: the charts only kind installs.
   - package-ecosystem: "helm"
     directory: "/Iverson.Server/deploy/kind/versions"
+    registries: "*"
     schedule:
       interval: "weekly"
EOF_PATCH
```

- [ ] **Step 2: Check the file** against the spec's §2.1. Dependabot validates the file only on GitHub (R1); these local checks confirm what this commit can prove.

```bash
python3 - <<'PY'
import json, urllib.request, yaml, jsonschema
d = yaml.safe_load(open('.github/dependabot.yml'))
schema = json.load(urllib.request.urlopen('https://json.schemastore.org/dependabot-2.0.json'))
print('schema errors:', len(list(jsonschema.Draft7Validator(schema).iter_errors(d))))
r = d['registries']
print('registries:', len(r), all(v['type'] == 'helm-registry' and v.get('url') and v.get('username') == 'x' and v.get('password') == 'x' for v in r.values()))
print('tigera entry:', any('tigera' in v['url'] for v in r.values()))
helm = [u for u in d['updates'] if u['package-ecosystem'] == 'helm']
print('helm registries:', [u.get('registries') for u in helm])
print('kind ignore:', [u.get('ignore') for u in helm if u.get('directory', '').endswith('/kind/versions')])
print('others with registries:', [u['package-ecosystem'] for u in d['updates'] if 'registries' in u and u['package-ecosystem'] != 'helm'])
PY
```

Expected: `schema errors: 0`; `registries: 8 True`; `tigera entry: False`; `helm registries: ['*', '*', '*']`; `kind ignore: [None]`; `others with registries: []`.

- [ ] **Step 3: Commit.**

```bash
git add .github/dependabot.yml
git commit -m "give dependabot's helm registries the dummy credentials github requires"
```

### Task 2: The runbook's helm check and the parent plan's assumption 27

**Files:**
- Modify: `docs/runbooks/csr12-supply-chain.md:35-38`
- Modify: `docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md:137,204`

- [ ] **Step 1: Apply the patch** (from the repository root).

```bash
git apply <<'EOF_PATCH'
diff --git a/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md b/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
index 4b1c44f6..335618b6 100644
--- a/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
+++ b/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
@@ -134,7 +134,7 @@ Evidence gathered 2026-10-26 against `main` @ `af4ec5e9` (`b50c6df6` for images)
 
 **Pinning**
 26. **Why the helm job fails.** Dependabot run 39691998646 (2026-10-21, 4 of 4 runs failed): `egress not allowlisted` for `aws.github.io`, `cloudnative-pg.github.io`, `kubernetes.github.io`, `starrocks.github.io`, `strimzi.io` (10 each) → `index.yaml : 403 Forbidden`; 60 `file://` subchart errors; `private_source_authentication_failure` for `iverson-api`/`iverson-admin-ui`; and `registry.k8s.io/kubectl` (`charts/authentik/values.yaml:12`): `GET https://registry.k8s.io:443/v2/registry.k8s.io/kubectl/tags/list` → 404 (a wrong path; `/v2/kubectl/tags/list` lists `v1.37.1`), `Error processing registry.k8s.io/kubectl (DockerRegistry2::NotFound)`, `unknown_error` in the run's error table (CDR-2 §2.2). Stub pins vs latest: cnpg 0.29.0/0.29.1, Strimzi 1.1.0/1.3.0, StarRocks operator 1.11.5/1.11.8. No Dependabot PR has ever touched a `Chart.yaml`.
-27. **Declaring a registry allowlists it:** "Update jobs can connect only to hosts on an egress allowlist"; "Declare a registry under the top-level `registries` key even if it allows anonymous access"; `helm-registry` takes `url` (+ optional basic auth) (Dependabot private-registries docs).
+27. **Declaring a registry allowlists it:** "Update jobs can connect only to hosts on an egress allowlist"; "Declare a registry under the top-level `registries` key even if it allows anonymous access"; `helm-registry` takes `url` (+ optional basic auth) (Dependabot private-registries docs). **Corrected 2026-10-27:** GitHub's validator rejects a `helm-registry` without a username and password; see `docs/specs/2026-10-27-csr-round12-dependabot-helm-registries-design.md`.
 28. **Setup helpers.** `setup.sh:28-35` `operator_version` and `setup.ps1:25-31` `Get-OperatorVersion` read `helm dependency list` of the operators stub; Calico `--repo https://docs.tigera.io/calico/charts --version v3.32.2` (`setup.sh:157-159`, `:176`; `setup.ps1:167`, `:184`), metrics-server `https://kubernetes-sigs.github.io/metrics-server/` `3.9.0`.
 29. **Istio has no index entry.** `istio-release.storage.googleapis.com/charts` `base`: no 1.31.2, newest `1.31.0-rc.0`; `vendor-istio-charts.sh:3,13-14` vendors from the GitHub release tarball; GitHub's latest is 1.31.3 (2026-10-22).
 30. **GitLab images:** 7 distinct `image:` values, all `@sha256:` pinned, no `services:` (`.gitlab-ci.yml:37…242`).
@@ -201,7 +201,7 @@ Newly introduced by this plan and verified at plan-write time (2026-10-26), most
 | 24 | Command | `terraform init -backend=false -lockfile=readonly` and `validate` pass for `aws`, `azure`, `gcp` and `bootstrap/aws`; `terraform fmt -check -recursive` passes; tfsec v1.28.6 reports 0 findings on `main` and on the branch. | Replay. |
 | 25 | Code | CNPG chart 0.29.0 with `image.tag: "1.30.0@sha256:a2701eb9…"` renders that string into the operator container and `OPERATOR_IMAGE_NAME`; `helm show chart … --version 0.29.0` prints `appVersion: 1.30.0`. | `helm template cnpg cloudnative-pg --version 0.29.0 -f …` lines 20606-20614. |
 | 26 | Code | The postgres `imageName` renders in all five overlays and every overlay lints; `image-scan.yml`'s listing step then lists both CNPG images by digest and nothing else changes. | Replay: `helm lint` × 5; `diff images-main.txt images-wt.txt`: only the two CNPG lines. |
-| 27 | Code | Dependabot: top-level `registries` with `type: helm-registry` + `url`; per-update `registries` (a list or `"*"`); the `helm` ecosystem uses `helm-registry` (HTTP, not OCI). | Dependabot options and private-registries docs. |
+| 27 | Code | Dependabot: top-level `registries` with `type: helm-registry` + `url`; per-update `registries` (a list or `"*"`); the `helm` ecosystem uses `helm-registry` (HTTP, not OCI). Corrected 2026-10-27: each `helm-registry` also needs `username` and `password` (`docs/specs/2026-10-27-csr-round12-dependabot-helm-registries-design.md`). | Dependabot options and private-registries docs. |
 | 28 | Code | `helm dependency list` on the kind stub prints `tigera-operator v3.32.2` and `metrics-server 3.9.0`; the setup helpers, cut out of the scripts, return each pinned version and fail naming the stub for an unpinned chart (bash; PowerShell 7.5.13); `main`'s helpers fail the same tests. | `Iverson.Server/deploy/kind/test_chart_pins.py`: 5 passed with pwsh, 3 passed + 2 skipped without; 5 failed against `main`. |
 | 29 | Command | PowerShell 7.5.13 for Linux x64 (`powershell-7.5.13-linux-x64.tar.gz`, SHA-256 `e9256bfc6ac98fcbec97270e8a986577a7346a587837cc80d30595eef3c2546b`) runs here. | Downloaded, checked against the release's `hashes.sha256`, `$PSVersionTable` → 7.5.13. |
 | 30 | Code | `aws_iam_openid_connect_provider` validates without `thumbprint_list` under aws provider 5.100.0. | `terraform validate` in `bootstrap/aws`. |
diff --git a/docs/runbooks/csr12-supply-chain.md b/docs/runbooks/csr12-supply-chain.md
index ab0a250c..4b30a0a9 100644
--- a/docs/runbooks/csr12-supply-chain.md
+++ b/docs/runbooks/csr12-supply-chain.md
@@ -32,10 +32,31 @@ itself.
    ```
 
    Both jobs read `success`. A pull request's run then shows `nvd-update` skipped and the scan passing.
-3. **Dependabot's helm jobs.** In the repository's **Insights**, then **Dependency graph**, then
-   **Dependabot**, the three `helm` entries' last runs succeed (they failed on every run before, `egress not
-   allowlisted`), and open the outstanding stub bumps (cloudnative-pg, Strimzi and the StarRocks operator
-   at the time of writing).
+3. **Dependabot's helm jobs** (amended 2026-10-27,
+   `docs/specs/2026-10-27-csr-round12-dependabot-helm-registries-design.md`). First the config verdict:
+   GitHub puts the `dependabot` check suite on the head commit of the push that brings the
+   `.github/dependabot.yml` change to `main`, not on the commit that changed the file. Its one check run is
+   titled `Dependabot config file validation` when it passes:
+
+   ```bash
+   gh api repos/counsellorben/Iverson/commits/<push head sha>/check-suites --jq '.check_suites[] | select(.app.slug=="dependabot") | .id'
+   gh api repos/counsellorben/Iverson/check-suites/<suite id>/check-runs --jq '.check_runs[] | "\(.name) \(.conclusion) \(.output.title)"'
+   ```
+
+   Then the job verdicts, from the three helm update runs that follow that push (the **Insights** page
+   returns 404):
+
+   ```bash
+   gh run list --repo counsellorben/Iverson --event dynamic --limit 40 --json databaseId,displayTitle,conclusion,createdAt --jq '.[] | select(.displayTitle | startswith("helm in"))'
+   gh run view <run id> --repo counsellorben/Iverson --log | grep -o 'egress not allowlisted [^ ]*' | sort | uniq -c
+   gh pr list --repo counsellorben/Iverson --state all --limit 1000 --json number,state,headRefName --jq '.[] | select(.headRefName | startswith("dependabot/helm/"))'
+   ```
+
+   No run logs `egress not allowlisted`, except the umbrella run's 60 lines that name `charts` (its
+   `file://` subcharts). The operators run logs `Submitting … pull request for creation` for the stub bumps
+   still outstanding (cloudnative-pg, Strimzi, the StarRocks operator and aws-load-balancer-controller at
+   the time of writing), and the kind run logs `No update possible for tigera-operator …` and
+   `… metrics-server …` while both are current.
 4. **Pin freshness.** Run it once by hand and review the issues it opens (§4):
 
    ```bash
EOF_PATCH
```

- [ ] **Step 2: Run the runbook's new commands** as written, read-only, with the placeholders filled from the `d616995d` push (the last push that changed `.github/dependabot.yml`):

```bash
H=$(git rev-parse d616995d)
S=$(gh api repos/counsellorben/Iverson/commits/$H/check-suites --jq '.check_suites[] | select(.app.slug=="dependabot") | .id')
echo "suite $S"
gh api repos/counsellorben/Iverson/check-suites/$S/check-runs --jq '.check_runs[] | "\(.name) \(.conclusion) \(.output.title)"'
gh run list --repo counsellorben/Iverson --event dynamic --limit 40 --json databaseId,displayTitle,conclusion,createdAt --jq '.[] | select(.displayTitle | startswith("helm in"))' | head -3
gh run view 40560914422 --repo counsellorben/Iverson --log | grep -o 'egress not allowlisted [^ ]*' | sort | uniq -c
gh pr list --repo counsellorben/Iverson --state all --limit 1000 --json number,state,headRefName --jq '.[] | select(.headRefName | startswith("dependabot/helm/"))' | grep -c dependabot/helm/
```

Expected: `suite 109939808298`; `.github/dependabot.yml success Dependabot config file validation`; three `helm in` rows; six `10 egress not allowlisted <host>` lines (the operators run before this plan); a count of at least 19.

- [ ] **Step 3: Commit.**

```bash
git add docs/runbooks/csr12-supply-chain.md docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md
git commit -m "point the supply chain runbook's helm check at the dependabot runs and correct plan row 27"
```

### R1: Rollout (after the branch merges; each step confirmed by the user)

- [ ] **Step 1: Push `main`.** Only on the user's go-ahead; never force.

- [ ] **Step 2: The config verdict,** on the head commit of that push (runbook step 2 item 3, first block). Expected: `.github/dependabot.yml success Dependabot config file validation`. If it fails, the summary names the property at fault; restore the file with `git revert` of Task 1's commit and push, as `8b273b52` did.

- [ ] **Step 3: The job verdicts,** once the three `helm in` runs after the push complete (runbook step 2 item 3, second block). Expected, per the spec's §3:
  - no `egress not allowlisted` line in the operators or kind run, and none in the umbrella's except the 60 that name `charts`;
  - the operators run opens the cloudnative-pg, Strimzi and StarRocks operator bumps, plus aws-load-balancer-controller 1.9.2;
  - the kind run logs `No update possible for tigera-operator v3.33.0` and `… metrics-server 3.14.0` and opens no pull request.

## Known issues inherited from spec

1. **The dummy credentials are sent** as basic auth to seven public chart hosts on every helm job. They are in the file, carry no access, and the hosts answer 200 (VA 4).
2. **`docs.tigera.io` is reached undeclared.** Its index host refuses any `Authorization` header and GitHub refuses a registry without one, so it cannot be declared; the kind job reaches it without a declaration today (VA 13), for reasons GitHub does not document. If that stops, its lookups log `egress not allowlisted docs.tigera.io`, the job still reports `success` (item 3), and Calico bumps stop silently. Accepted by the user (counsellorben), 2026-10-27 (CDR-1 §3.1, option B).
3. **A host that starts redirecting** (as `cloudnative-pg.github.io` does) silently drops its charts out of the helm job (`egress not allowlisted <host>` in the job log) until it gets a registry entry. The job still reports success (VA 5).
