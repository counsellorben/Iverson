# CSR round 12, supply chain amendment: Dependabot helm registries — design

**Amends:** `docs/specs/2026-10-26-csr-round12-supply-chain-design.md` (the parent) §4.1 (its registries bullet) and VA 27. The parent carries a pointer to this spec at each corrected line.
**Trigger:** rollout step 2's helm check (parent §6 step 2). GitHub's `dependabot` check on `main` @ `dbb1ffe1` failed with `The property '#/registries/<name>/' of type object did not match one or more of the required schemas`, once for each of the eight anonymous `helm-registry` entries. An invalid file is refused whole.
**Base:** `main` @ `8b273b52`. That interim commit removed the `registries:` block and the three `registries: "*"` lines, so the file is valid again (its `dependabot` check passes). At `8b273b52` all three helm jobs report `success`, but the operators job logs `egress not allowlisted` for its six chart hosts (10 each) and opens no pull request (run 40543040473), while the kind job, with no registry declared, reached `docs.tigera.io` and `kubernetes-sigs.github.io` and opened #233 (`tigera-operator` v3.33.0) and #236 (`metrics-server` 3.14.0) (run 40543039603), both since merged (VA 13).

## Decisions (user, 2026-10-27)

1. **Interim fix first** (`8b273b52`, pushed), then the real fix goes through a spec.
2. **Each registry carries a literal dummy username and password** (`x`/`x`), not a secret: probed below. A secret-backed password would need a Dependabot secret again, and a job fails when the secret is absent (VA 3).
3. **Dependabot tracks `tigera-operator` with no registry for its host** (CDR-1 §3.1, option B). `docs.tigera.io` refuses any basic auth (VA 6) and an anonymous registry is invalid (VA 1), so it cannot be declared; the kind job reaches it undeclared (VA 13). Its first bump, #233 (v3.33.0), is merged.
4. **This amendment is its own spec,** so its review covers this change only; the parent gets pointer corrections.

## 1. Goals and non-goals

**Goal:** the parent §4.1's intent, which never worked: the operators stub and kind stub `helm` jobs reach their chart repositories and open bump pull requests, with `.github/dependabot.yml` valid.

**Non-goals:**
- Bumping any chart. The rollout check only confirms the pull requests open. The parent's §6 step 2 names cnpg 0.29.1, Strimzi 1.3.0 and StarRocks 1.11.8.
- The umbrella job's 60 `could not find protocol handler for: file` errors for its `file://` subcharts. They occur with and without the registries (VA 9) and were already in the parent's VA 26.

## 2. Design

### 2.1 `.github/dependabot.yml`

The top-level `registries:` block returns with eight `helm-registry` entries. Each has `type: helm-registry`, `url`, `username: x` and `password: x`:

| Name | `url` |
|---|---|
| `cloudnative-pg` | `https://cloudnative-pg.github.io/charts` |
| `cloudnative-pg-io` | `https://cloudnative-pg.io/charts` (the target of `cloudnative-pg.github.io`'s 301, VA 5) |
| `strimzi` | `https://strimzi.io/charts/` |
| `starrocks` | `https://starrocks.github.io/starrocks-kubernetes-operator` |
| `autoscaler` | `https://kubernetes.github.io/autoscaler` |
| `eks-charts` | `https://aws.github.io/eks-charts` |
| `sigstore` | `https://sigstore.github.io/helm-charts` |
| `metrics-server` | `https://kubernetes-sigs.github.io/metrics-server/` |

There is no Calico (`docs.tigera.io`) entry. The comment above the block replaces `8b273b52`'s interim comment and says:
- a registry declaration allowlists its host (some hosts, `docs.tigera.io` and `kubernetes-sigs.github.io` among them, are reachable without one, VA 13);
- GitHub rejects a `helm-registry` without credentials, and these repositories are public and ignore the dummy pair;
- `cloudnative-pg.github.io` redirects to a host that needs its own entry;
- `docs.tigera.io` refuses basic auth, so it has no entry; the kind job reaches it undeclared.

The stub's `cloudnative-pg` repository URL stays `cloudnative-pg.github.io`. Terraform installs from that field (`terraform/modules/operators/main.tf:12,148`), so the redirect target gets a registry entry instead.

The three `helm` entries (the umbrella, the operators stub and the kind stub) regain `registries: "*"`, as the parent §4.1 had them. The operators entry's comment again reads "its own entry, with the registries". The kind entry gets nothing else: no `ignore`, so Dependabot looks up `tigera-operator` at `docs.tigera.io` without credentials (VA 13).

Every other entry is unchanged.

### 2.2 `ci/pins.yaml`

No change (CDR-1 §3.1, option B): `tigera-operator` stays a Dependabot dependency, and its version stays in the kind stub, where `setup.sh:41` and `setup.ps1:40` read it.

### 2.3 Dependents

- **`docs/runbooks/csr12-supply-chain.md`, rollout step 2 item 3:**
  - it states §3's rollout checks: no `egress not allowlisted` line except the umbrella's `charts` ones, and the stub bumps opened or current;
  - it reads the config verdict with VA 2's commands, on the commit that changed `.github/dependabot.yml` (the `dependabot` suite exists only on such a commit), instead of the **Insights** path, which returned 404 for the user;
  - it reads the job verdicts from the three helm update runs that follow that push: `gh run list --repo counsellorben/Iverson --event dynamic --limit 40 --json databaseId,displayTitle,conclusion,createdAt --jq '.[] | select(.displayTitle | startswith("helm in"))'`; for each run, `gh run view <id> --repo counsellorben/Iverson --log | grep -o 'egress not allowlisted [^ ]*' | sort | uniq -c`; the operators run's `Submitting … pull request for creation` lines and the kind run's `No update possible for tigera-operator v3.33.0` and `… metrics-server 3.14.0`; and the opened PRs, `gh pr list --repo counsellorben/Iverson --state all --json number,state,headRefName --jq '.[] | select(.headRefName | startswith("dependabot/helm/"))'`.
- **The parent spec,** each correction ending with a pointer to this spec:
  - §4.1's registries bullet (line 135);
  - VA 27.
- **The parent plan's row 27** repeats VA 27. It is corrected when this amendment's plan is written.

## 3. Testing and verification

- **Config:** GitHub's `dependabot` check on the pushed commit passes; the throwaway repository has no stand-in for this. Read it with VA 2's commands. A local schema check does not count: the public schema accepts the rejected form (VA 1).
- **Rollout, on `main`:**
  - no `egress not allowlisted` line in the operators or kind job log, and none in the umbrella's except the 60 that name `charts` (its `file://` subcharts);
  - the operators job opens the cnpg, Strimzi and StarRocks bumps, plus aws-load-balancer-controller 1.9.2 (patch only, under the existing ignore rule);
  - the kind job finds `tigera-operator` v3.33.0 and `metrics-server` 3.14.0 current (#233 and #236 are merged) and opens no pull request.

## 4. Known issues / accepted as out of scope

1. **The dummy credentials are sent** as basic auth to seven public chart hosts on every helm job. They are in the file, carry no access, and the hosts answer 200 (VA 4).
2. **`docs.tigera.io` is reached undeclared.** Its index host refuses any `Authorization` header and GitHub refuses a registry without one, so it cannot be declared; the kind job reaches it without a declaration today (VA 13), for reasons GitHub does not document. If that stops, its lookups log `egress not allowlisted docs.tigera.io`, the job still reports `success` (item 3), and Calico bumps stop silently. Accepted by the user (counsellorben), 2026-10-27 (CDR-1 §3.1, option B).
3. **A host that starts redirecting** (as `cloudnative-pg.github.io` does) silently drops its charts out of the helm job (`egress not allowlisted <host>` in the job log) until it gets a registry entry. The job still reports success (VA 5).

## Verified assumptions

Evidence gathered 2026-10-27. Probe commits are in the private throwaway repository `counsellorben/dependabot-helm-probe`. The run ids are its Dependabot update jobs, and the quoted lines come from their logs, because the repository is deleted after this spec.

1. **An anonymous `helm-registry` is invalid on GitHub, though the public schema accepts it.**
   - Iverson `main` @ `dbb1ffe1`: the `dependabot` check suite 109852522367 failed, once per registry.
   - Probe `c5b76d1` (the same form) failed the same way.
   - The SchemaStore `dependabot-2.0.json` validates Iverson's pre-fix file with 0 errors.
   - The docs say "Declare a registry … even if it allows anonymous access".
2. **Where the verdict is.** `gh api repos/<o>/<r>/commits/<sha>/check-suites --jq '.check_suites[] | select(.app.slug=="dependabot")'`, then `gh api repos/<o>/<r>/check-suites/<id>/check-runs --jq '.check_runs[].output.summary'`. The **Insights → Dependency graph → Dependabot** page (`/network/updates`) returned 404. The `dependabot` suite exists only on a commit that changes `.github/dependabot.yml` and holds one check run, `Dependabot config file validation` (suites 109889628759 on `8b273b52` and 109939808298 on `d616995d`); a merge such as `21aaf096` carries only `github-actions` suites. The update jobs are separate Actions runs (`gh run list --event dynamic`, titled `helm in <directory> - Update #…`), and their logs carry the `egress not allowlisted` lines and the per-chart outcomes (runs 40560916404, 40560914422 and 40560912616 on `d616995d`).
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
