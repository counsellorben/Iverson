# CSR round 12, supply chain amendment: Dependabot helm registries — design

**Amends:** `docs/specs/2026-10-26-csr-round12-supply-chain-design.md` (the parent) §4.1, §4.2 and §4.5. The parent carries a pointer to this spec at each corrected line.
**Trigger:** rollout step 2's helm check (parent §6 step 2). GitHub's `dependabot` check on `main` @ `dbb1ffe1` failed with `The property '#/registries/<name>/' of type object did not match one or more of the required schemas`, once for each of the eight anonymous `helm-registry` entries. An invalid file is refused whole.
**Base:** `main` @ `8b273b52`. That interim commit removed the `registries:` block and the three `registries: "*"` lines, so the file is valid again (its `dependabot` check passes) and the helm jobs fail on `egress not allowlisted` as they did before the parent.

## Decisions (user, 2026-10-27)

1. **Interim fix first** (`8b273b52`, pushed), then the real fix goes through a spec.
2. **Each registry carries a literal dummy username and password** (`x`/`x`), not a secret: probed below. A secret-backed password would need a Dependabot secret again, and a job fails when the secret is absent (VA 3).
3. **`tigera-operator` stays in the kind stub,** Dependabot ignores it, and `ci/pins.yaml` tracks it (option 1 of 3). Its chart repository refuses any basic auth (VA 6), and an anonymous registry is invalid (VA 1).
4. **This amendment is its own spec,** so its review covers this change only; the parent gets pointer corrections.

## 1. Goals and non-goals

**Goal:** the parent §4.1's intent, which never worked: the operators stub and kind stub `helm` jobs reach their chart repositories and open bump pull requests, with `.github/dependabot.yml` valid.

**Non-goals:**
- Bumping any chart. The rollout check only confirms the pull requests open. The parent's §6 step 2 names cnpg 0.29.1, Strimzi 1.3.0 and StarRocks 1.11.8, and the new `tigera-operator` row reports v3.33.0.
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
- a registry declaration is what allowlists a host;
- GitHub rejects a `helm-registry` without credentials, and these repositories are public and ignore the dummy pair;
- `cloudnative-pg.github.io` redirects to a host that needs its own entry;
- `docs.tigera.io` refuses basic auth, so `ci/pins.yaml` tracks `tigera-operator`.

The stub's `cloudnative-pg` repository URL stays `cloudnative-pg.github.io`. Terraform installs from that field (`terraform/modules/operators/main.tf:12,148`), so the redirect target gets a registry entry instead.

The three `helm` entries (the umbrella, the operators stub and the kind stub) regain `registries: "*"`, as the parent §4.1 had them. The operators entry's comment again reads "its own entry, with the registries". The kind entry gains:

```yaml
    # docs.tigera.io refuses the registry's basic auth (see registries above); ci/pins.yaml tracks tigera-operator.
    ignore:
      - dependency-name: "tigera-operator"
```

Every other entry is unchanged.

### 2.2 `ci/pins.yaml`

A new row, placed after `kindest-node` (the other kind rows) and preceded by a two-line comment giving the reason:

```yaml
- name: tigera-operator
  source: github-release projectcalico/calico
  locations:
    - file: Iverson.Server/deploy/kind/versions/Chart.yaml
      regex: '- name: tigera-operator\n    version: (v[0-9.]+)\n'
```

The regex spans the dependency's `name` and `version` lines, so it matches only `tigera-operator`'s version, not `metrics-server`'s (VA 7). `setup.sh` and `setup.ps1` keep reading the version from the stub (`setup.sh:41`, `setup.ps1:40`). The pin stays at v3.32.2.

### 2.3 Dependents

- **`Iverson.Server/deploy/kind/versions/Chart.yaml`:** its header comment (lines 1–4) says both charts are "pinned here so Dependabot's helm ecosystem proposes their bumps". It gains that `tigera-operator`'s bumps come from the `ci/pins.yaml` row instead.
- **`docs/runbooks/csr12-supply-chain.md`, rollout step 2 item 3:**
  - it states that the three helm jobs succeed and open the stub bumps, as now;
  - it reads the verdict from GitHub's `dependabot` check suite on `main`'s head commit, with the `gh api` commands of VA 2, instead of the **Insights** path, which returned 404 for the user;
  - it notes that a `Pin behind: tigera-operator` issue comes from pin freshness, not Dependabot.
- **The parent spec,** each correction ending with a pointer to this spec:
  - §4.1's registries bullet (line 135);
  - VA 27;
  - §4.2's "its own Dependabot `helm` entry with the registries";
  - §4.5's row list, which gains `tigera-operator`.
- **The parent plan's row 27** repeats VA 27. It is corrected when this amendment's plan is written.

## 3. Testing and verification

- **Config:** GitHub's `dependabot` check on the pushed commit passes; the throwaway repository has no stand-in for this. Read it with VA 2's commands. A local schema check does not count: the public schema accepts the rejected form (VA 1).
- **Pins:** `python3 ci/pins.py consistency` exits 0 and prints `ok tigera-operator v3.32.2`, and `python3 -m pytest -q ci/test_pins.py` passes (VA 7, 8).
- **Rollout, on `main`:**
  - the three helm jobs succeed;
  - the operators job opens the cnpg, Strimzi and StarRocks bumps, plus aws-load-balancer-controller 1.9.2 (patch only, under the existing ignore rule);
  - the kind job opens metrics-server's bump and logs `All versions of tigera-operator ignored`;
  - the next pin-freshness run opens `Pin behind: tigera-operator`.

## 4. Known issues / accepted as out of scope

1. **The dummy credentials are sent** as basic auth to seven public chart hosts on every helm job. They are in the file, carry no access, and the hosts answer 200 (VA 4).
2. **Dependabot cannot track Calico's chart.** Its index host refuses any `Authorization` header, and GitHub refuses a registry without one. The pin table tracks it weekly instead.
3. **A host that starts redirecting** (as `cloudnative-pg.github.io` does) silently drops its charts out of the helm job (`egress not allowlisted <host>` in the job log) until it gets a registry entry. The job still reports success (VA 5).

## Verified assumptions

Evidence gathered 2026-10-27. Probe commits are in the private throwaway repository `counsellorben/dependabot-helm-probe`. The run ids are its Dependabot update jobs, and the quoted lines come from their logs, because the repository is deleted after this spec.

1. **An anonymous `helm-registry` is invalid on GitHub, though the public schema accepts it.**
   - Iverson `main` @ `dbb1ffe1`: the `dependabot` check suite 109852522367 failed, once per registry.
   - Probe `c5b76d1` (the same form) failed the same way.
   - The SchemaStore `dependabot-2.0.json` validates Iverson's pre-fix file with 0 errors.
   - The docs say "Declare a registry … even if it allows anonymous access".
2. **Where the verdict is.** `gh api repos/<o>/<r>/commits/<sha>/check-suites --jq '.check_suites[] | select(.app.slug=="dependabot")'`, then `gh api repos/<o>/<r>/check-suites/<id>/check-runs --jq '.check_runs[].output.summary'`. The **Insights → Dependency graph → Dependabot** page (`/network/updates`) returned 404.
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
7. **The pin row works.**
   - Built in a scratch worktree of `8b273b52`.
   - `found()` (`ci/pins.py:93-105`, `re.findall(…, re.M)`) returns only `('…/kind/versions/Chart.yaml', 'v3.32.2')`.
   - `consistency` exits 0 and prints `ok tigera-operator v3.32.2`.
   - `newest('github-release projectcalico/calico', '.')` returns `v3.33.0`, which is stale against v3.32.2 once `norm` strips the `v`.
   - `releases/latest` is `v3.33.0` (2026-10-01), the newest chart version in the index.
8. **No test fixes the table's rows.** `python3 -m pytest -q ci/test_pins.py`: 29 passed with the row added.
9. **The exact config, on copies of all three Iverson helm directories** (`c428e40`, from `git archive` of `8b273b52`), passes validation ("All changes look good"). Runs:

   | Run | Entry | Result |
   |---|---|---|
   | 40550675263 | kind | success; `All versions of tigera-operator ignored, no update allowed`; 0 requests to `docs.tigera.io`; metrics-server 3.9.0 → 3.14.0 |
   | 40550674177 | operators | success; PRs for cloudnative-pg 0.29.1, operator 1.11.8, strimzi-kafka-operator 1.3.0, aws-load-balancer-controller 1.9.2 (minor and major ignored, as configured); cluster-autoscaler 9.59.0 and policy-controller 0.10.8 already newest |
   | 40550674103 | umbrella | success; the same image bumps and the same 60 `file://` errors as Iverson's own umbrella run 40543038683 without registries (16:39, after `8b273b52`) |

   The `ignore` rules for `iverson-api`, `iverson-admin-ui` and `registry.k8s.io/kubectl` held in both runs.
10. **Push protection does not scan for generic passwords.** Iverson's `secret_scanning_non_provider_patterns` is `disabled`, so `password: x` is not a push-protection pattern.
11. **Nothing else describes the anonymous form.**
    - `command grep -rn -i 'helm-registry|anonymous|egress not allowlisted|registries'` over `docs/runbooks`, `ci`, `.github` and both stubs finds only `dependabot.yml`, plus `image-scan.yml:101` (an unrelated Docker Hub remark).
    - The runbook's step 2 item 3 (`csr12-supply-chain.md:35-37`) and the kind stub's header comment are the only dependent texts (§2.3).
12. **The stub still pins the bumps the rollout expects:** `cloudnative-pg 0.29.0`, `strimzi-kafka-operator 1.1.0`, `operator 1.11.5`, `tigera-operator v3.32.2`, `metrics-server 3.9.0`.
