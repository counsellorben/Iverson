# CSR round 12, sub-project 3: Supply chain — design

**Source review:** `docs/criticalreviews/2026-10-16-iverson-critical-security-review-12.md` (at `f7eeba79`) #10 and #17, and `docs/security/tma.md` §5.10 (tools), §5.12 and §6 F7 (read only; this sub-project does not edit the TMA).
**Findings in scope:** #10 (NVD key exposed to Dependabot PR build code) and #17 (TMA F7, supply-chain, CI and tooling hygiene, every bullet). Side fixes found while designing are marked "(side fix)".
**Base:** `main` @ `af4ec5e9` (sub-projects 1, Server, and 2, Platform, merged and pushed; the 2026-10-26 advisory jam fixed in `af4ec5e9`).
**Sibling sub-projects:** 1, Server (merged); 2, Platform (merged).

## Decisions (user, 2026-10-26)

1. Supply chain is **one spec and one branch**, covering #10 and every F7 item, including admission-time signature verification.
2. **`vadim82`'s write access is removed;** the user is the repository's only writer.
3. **Branch protection keeps the admin bypass and no required reviews** (direct pushes to `main` continue). Accepted residual, §8.
4. **Clusters verify first-party images at admission** with Sigstore policy-controller, checking the publish workflow's SPDX attestation (probe: §3, VA 14–25).
5. **#10: N2** — the NVD key lives only in a job that runs no project code; every scan runs keyless from a cache.
6. **Java gets Maven trusted checksums (J1); .NET gets `packages.lock.json`.** Every Java build runs one pinned Maven, 3.9.16, through a Maven Wrapper, and the record is written and verified under it (CDR-3 §3.1, option A, source-type wrapper).
7. **EKS add-ons: exact pins plus a scheduled check** (needs a read-only AWS role).
8. **The engine's TUF fetch at startup stays an accepted residual** (option a); no in-cluster TUF mirror.
9. **Every version pin outside Dependabot is tracked** by one table and one script (§4.5), the CLI tool pins in workflows included.
10. The 2026-10-26 CI jam (new npm/setuptools advisories) was fixed separately before this spec (`af4ec5e9`); the admin-ui image's `brotli-libs` waits for an upstream `nginx-unprivileged` rebuild.

## 1. Goals and non-goals

**Goals**
- The NVD API key is never in a job that executes repository or dependency build code, on any event, on GitHub or GitLab (#10).
- Repository settings match the security model: required security checks, release tags creatable only by the admin, push protection on, no second writer (F7 repository bullets).
- The publish workflow never moves a published tag, binds release signatures to their commit, and its runbook verifies as strictly as the workflow (F7 publish residuals).
- A cloud cluster admits a first-party image only if the publish workflow attested that digest under that image's own name (F7 deploy side).
- Every version pin either has an automatic "you are behind" signal or a stated reason (F7 "outside Dependabot").
- Java and .NET resolutions are locked to recorded hashes (F7 lockfiles).
- The five tooling residuals are closed (F7 tooling).

**Non-goals**
- Verifying images on kind, local or compose profiles (they run unsigned local builds).
- Verifying third-party images at admission.
- Editing `docs/security/tma.md` (the next TMA refresh records the closures).
- Required reviews or removing the admin bypass (§8).

## 2. CI, repository settings and publishing

### 2.1 The NVD key (#10, N2)

`.github/workflows/dependency-scan.yml`:
- **New job `nvd-update`**, `if: github.event_name != 'pull_request'` (push to `main`, the weekly schedule).
  - Sparse checkout of `Iverson.Clients/Java/.mvn/` only (`actions/checkout` `sparse-checkout`, `sparse-checkout-cone-mode: false`), copied into an otherwise empty directory, so no `pom.xml` is present and no project code or build extension can load.
  - Restores the NVD cache (same path and keys as today), installs the pinned Maven itself (it runs no repository code, so not `mvnw`): it downloads the `distributionUrl` named in the checked-out `.mvn/wrapper/maven-wrapper.properties`, refuses it unless its SHA-256 equals that file's `distributionSha256Sum`, unpacks it, and runs its `bin/mvn -B org.owasp:dependency-check-maven:<the pom's pinned version, 13.0.0 today>:update-only -DnvdApiKeyEnvironmentVariable=NVD_API_KEY` there with `NVD_API_KEY: ${{ secrets.NVD_API_KEY }}`, and saves the cache (`nvd-${{ github.run_id }}`). The `.mvn/maven.config` of §4.7 makes the plugin's own resolution checksum-verified.
- **`owasp-dependency-check-java`** runs on every event, `needs: nvd-update`, `if: ${{ !cancelled() && (needs.nvd-update.result == 'success' || needs.nvd-update.result == 'skipped') }}` (a skipped `needs` job otherwise skips its dependants, VA 4). It restores the cache, runs `./mvnw -B install -DskipTests`, then `./mvnw -B … check -DfailBuildOnCVSS=7 -DautoUpdate=false` with **no key**, and never saves the cache. A cache miss fails loudly (`NoDataException`, VA 3).
- The plugin version appears in `pom.xml` and both workflows; §4.5 keeps them equal.

`.gitlab-ci.yml`: the same split. `nvd-update` (schedule only; `environment: { name: nvd-update, action: prepare }`, so it alone receives the environment-scoped `NVD_API_KEY` and creates no deployment, VA 51; installs the pinned Maven the same way as GitHub's and runs it from a temp directory holding only a copy of `Iverson.Clients/Java/.mvn/`; pushes the DB to a GitLab `cache:` with `policy: push`) and `owasp-dependency-check-java` (schedule only as today, `needs: [nvd-update]`, `cache: policy: pull`, no `variables: NVD_API_KEY` entry, its first script line `test -z "${NVD_API_KEY+x}" || { echo "NVD_API_KEY must be scoped to the nvd-update environment"; exit 1; }` so §6 step 2's run proves the scoping, then `./mvnw` after `unset MAVEN_CONFIG`, `-DautoUpdate=false`, no key; the `maven` image sets `MAVEN_CONFIG=/root/.m2`, which `mvnw` passes to Maven as arguments, and only the image's entrypoint unsets it, which not every GitLab executor runs, VA 50). Both pass `-DdataDirectory=$CI_PROJECT_DIR/.nvd-data`, and both `cache:` entries name `.nvd-data` with one shared `key:` (GitLab caches only paths inside the project directory, VA 47). `docs/runbooks/csr11-clients-tooling-ci.md` §5 is updated: `NVD_API_KEY` is read only by `nvd-update`, and it has one definition, a project variable `NVD_API_KEY` that is **Protected**, **Masked** and has **Environment scope `nvd-update`**; the pipeline-schedule-variable option is removed, because a schedule variable reaches every job of the scheduled pipeline (VA 51).

GitHub settings: **delete the Dependabot secret `NVD_API_KEY`** (the Actions secret stays). `.github/dependabot.yml`: the Maven entry gains `cooldown: { default-days: 7 }` (security updates are not delayed, VA 5).

### 2.2 Repository settings

Each is a live GitHub change; the plan makes them as separate confirmed steps, in the order of §6.
- **Branch protection:** run `Iverson.Server/deploy/scripts/apply-branch-protection.sh` (22 required checks, all present on `main`, VA 7) once all 22 are green on `main`. Admin bypass and no reviews unchanged.
- **Tag ruleset:** new `Iverson.Server/deploy/scripts/apply-tag-ruleset.sh`, same style and `--repo`/`--dry-run` flags as the protection script. One ruleset, `target: tag`, `conditions.ref_name.include: ["refs/tags/v*"]`, rules `creation`, `update` (`update_allows_fetch_and_merge: false`), `deletion`, bypass actor the repository admin role (`actor_type: RepositoryRole`), `bypass_mode: always`. The script creates it `enforcement: disabled`, reads it back and prints the bypass actor, and switches it to `active` only with `--activate` once the read-back shows the admin role (the role's numeric id is not documented, VA 8).
- **Push protection:** `PATCH /repos/counsellorben/Iverson` `security_and_analysis.secret_scanning_push_protection.status = enabled` (VA 9). History has 0 secret-scanning alerts.
- **Collaborator:** `DELETE /repos/counsellorben/Iverson/collaborators/vadim82` (VA 10; they have no fork).

### 2.3 Publish residuals

`.github/workflows/publish-images.yml`:
1. **A tag never moves after repeated denials.** Today three `denied`/`unauthorized` lookups count as "not published" and the job builds (VA 11). They exist because GHCR denies lookups in a package that does not exist yet. New rule: after the third denial, query `GET /users/counsellorben/packages/container/<name>` with the job's token (`packages: read`). 404 → absent, build. 200 → the package exists, so the denial is a real failure: fail the run. Any other answer fails the run.
2. **Release signatures bind the commit.** The existing-tag check for `bind=release` adds `--certificate-github-workflow-sha "$GITHUB_SHA"` beside the exact `@refs/tags/v$TAG` identity (on a tag run `GITHUB_SHA` is the tagged commit).
3. **The runbook verifies like the workflow.** `docs/runbooks/csr11-clients-tooling-ci.md` §2: commit tags use `--certificate-github-workflow-sha <tag>`; release tags use `--certificate-identity <workflow>@refs/tags/vX.Y.Z` plus `--certificate-github-workflow-sha $(git rev-list -n1 vX.Y.Z)`; model images keep the content-tag regexp (accepted in round 11).
4. **The image name is bound.** The attestation's in-toto `subject[0].name` is the repository (`ghcr.io/counsellorben/iverson-api`, VA 13). The cluster checks it per image (§3); the runbook adds `cosign verify-attestation … | jq` that prints the subject name for the operator to compare.

## 3. Admission-time verification (cloud profiles)

**Install** (`Iverson.Server/deploy/terraform/modules/operators/main.tf`):
- `kubernetes_namespace.cosign_system` (`cosign-system`, not mesh-enrolled: the cloud API server is outside the mesh and must reach the webhook).
- `helm_release.policy_controller`: chart `policy-controller` from `https://sigstore.github.io/helm-charts`, version from the operators stub (`versions/Chart.yaml`, §4.1). Values: `webhook.image.version` = the app version the probes verified (`v0.15.1`; the chart's default app lags, VA 17; tracked by §4.5), `webhook.resources.limits.cpu = 1000m` (at the chart's 200m verification took 5–10 s against the 10 s webhook timeout, VA 20), `webhook.configData.no-match-policy = allow` (default `deny` would refuse third-party images in the namespace, VA 21).
- `kubernetes_namespace.iverson` gains `policy.sigstore.dev/include = "true"` (the webhooks' namespaceSelector, VA 21). No other namespace carries it.
- `helm_release.image_policies`: a new local chart `modules/operators/charts/image-policies`, installed after `policy_controller`, holding one `ClusterImagePolicy` per first-party image — `iverson-api`, `iverson-admin-ui`, `iverson-ollama-model`, `iverson-tei-model`:

```yaml
apiVersion: policy.sigstore.dev/v1beta1
kind: ClusterImagePolicy
metadata:
  name: iverson-api
spec:
  images:
  - glob: "ghcr.io/counsellorben/iverson-api*"
  authorities:
  - signatureFormat: bundle
    keyless:
      url: https://fulcio.sigstore.dev
      identities:
      - issuer: https://token.actions.githubusercontent.com
        subjectRegExp: '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$'
    ctlog:
      url: https://rekor.sigstore.dev
    attestations:
    - name: sbom
      predicateType: https://spdx.dev/Document
      policy:
        type: cue
        data: |
          subject: [{name: "ghcr.io/counsellorben/iverson-api"}]
```

The identity regexp is one value of the chart (shown inline above), documented as identical to `publish-images.yml`'s `IDENTITY` and the runbook's; §7 adds a check that the three agree.
- `cluster-gcp`'s `google_compute_firewall.control_plane_webhooks` adds `"8443"` (the policy-controller webhook) to its ports, and its comment names it; AWS and Azure need no rule (VA 46).
- `operator-namespace-policies` gains `cosign-system` on the clouds (a new value, like `kind:`): default-deny, ingress from `apiServerCidrs` to the webhook port, egress to DNS and to 443 (TUF CDN at startup, GHCR for the attestations).

**Behaviour (measured, VA 14–25):**

| Situation | Result |
|---|---|
| First-party image by digest, attested by the workflow, subject = its name | admitted |
| By tag | admitted; the stored pod and workload templates read `tag@digest` |
| Wrong identity, missing attestation, wrong predicate type | refused, the message names expected and actual |
| Another first-party image's digest under this name (cross-image swap) | refused: `subject.0.name: conflicting values` |
| Bad init container only | refused, naming `spec.initContainers[0].image` |
| Deployment, ReplicaSet, StatefulSet, DaemonSet, Job, CronJob | checked at the workload object, not only at pods |
| Third-party image in the namespace | rewritten to `tag@digest` and admitted unverified (`no-match-policy: allow`); refused if its registry cannot resolve the tag (VA 45) |
| Engine down | refused in the release namespace (failurePolicy Fail); other namespaces unaffected |
| Engine running, Sigstore (TUF, Rekor, Fulcio) unreachable | admitted (bundles verify offline) |
| Engine restarted while the TUF CDN is unreachable | the engine crash-loops; first-party creates are refused until TUF returns (§8) |

**Helm interaction (VA 23):** every `helm upgrade`, even with no change, patches each Deployment (Helm restores the tag, the engine rewrites it to `tag@digest`); no loop forms. A non-operator's no-change upgrade is therefore refused by the chart's own admission policy rule (d), as the Platform design intends.

**Operator-managed workloads (VA 45):** the rewrite reaches every image in the namespace, and CNPG rolls an instance whose stored image string differs from its target. So the postgres subchart's `Cluster` (`charts/postgres/templates/cluster.yaml`) sets `imageName: ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie@sha256:42708a75345b7a48fdd9257b071830783a97fd228529196b6313187a7198e185` (CNPG 1.30.0's default image, at chart 0.29.0, pinned by its digest), which the webhook writes back unchanged. `image-scan.yml:82-92` reads the rendered `Cluster`'s `imageName` instead of the operator source. CNPG also compares each instance's `bootstrap-controller` init-container image with its `OPERATOR_IMAGE_NAME`, both rendered from the chart's `image.tag` (CDR-2 §3.1, VA 45). So `helm_release.cloudnative_pg` reads a new values file, `modules/operators/versions/cloudnative-pg-values.yaml`, setting `image.tag: "1.30.0@sha256:a2701eb97cdd2a34b1fdb2cb51987f544b706e40bec72ae7146cd8580efefebb"` (the chart's appVersion, pinned by its digest); the operator's env and the init container then carry the `tag@digest` string the webhook writes back unchanged. `image-scan.yml`'s `render_operator cloudnative-pg` passes the same file. kind (namespace not labelled) keeps the chart default. Strimzi's and the StarRocks operator's workloads are an accepted residual (§8).

**Runbook:** a new section in the supply-chain runbook (§6): what a refusal looks like and how to read it; telling a refusal from an engine or TUF outage; the TUF restart residual; the emergency lever (remove the namespace label — an operator act, since namespace-scoped roles cannot edit `Namespace` objects, VA 25).

The model images have never been published (VA 18); their policies apply from their first `v*` or manual publish run.

## 4. Pinning and lockfiles

### 4.1 Dependabot for the chart stubs (side fix: they never ran)

The `helm` update job has failed on every run since the stub was added (4 of 4). Cause: Dependabot's proxy allows only allowlisted hosts, and every chart repository got `403` with `egress not allowlisted` (VA 26). A registry declared under `registries:` is allowlisted even with anonymous access (VA 27).
- `.github/dependabot.yml` gains a top-level `registries:` with one anonymous `helm-registry` per chart repository: `cloudnative-pg.github.io/charts`, `strimzi.io/charts/`, `starrocks.github.io/starrocks-kubernetes-operator`, `kubernetes.github.io/autoscaler`, `aws.github.io/eks-charts`, `sigstore.github.io/helm-charts`, `docs.tigera.io/calico/charts`, `kubernetes-sigs.github.io/metrics-server/`.
- The operators stub moves to its own `helm` entry with `registries: [...]`; the umbrella entry keeps its directories, gains the registries, and `ignore`s `iverson-api` and `iverson-admin-ui` (local image names it looked up on Docker Hub, `private_source_authentication_failure`, VA 26) and `registry.k8s.io/kubectl` (looked up at a wrong path, `unknown_error`, VA 26; tracked by §4.5 instead).
- The stub gains `policy-controller`.
- Proof is on `main` only (Dependabot reads the default branch's config): §6 step 2.

### 4.2 kind stub

New `Iverson.Server/deploy/kind/versions/Chart.yaml` lists `tigera-operator` and `metrics-server` with their repositories, its own Dependabot `helm` entry with the registries. `setup.sh`'s `operator_version` and `setup.ps1`'s `Get-OperatorVersion` take the stub directory as a parameter (VA 28); the inline `v3.32.2` (twice per script) and `3.9.0` go.

### 4.3 GitLab images

New `ci/gitlab-images/Dockerfile`: one `FROM <image>@<digest>` per distinct `.gitlab-ci.yml` image (7 today, all digest-pinned, VA 30), never built. Dependabot `docker` entry for `/ci/gitlab-images` (it updates digest-pinned `FROM` lines, e.g. #151, #196). §4.5's consistency check keeps `.gitlab-ci.yml` equal to it.

### 4.4 EKS add-ons

`vpc-cni` (`cluster-aws/main.tf:524`) and `aws-ebs-csi-driver` (`:550`) stay exact pins (VA 32). §4.5 checks them with `aws eks describe-addon-versions --addon-name <n> --kubernetes-version <var.kubernetes_version>`.
- `Iverson.Server/deploy/terraform/bootstrap/aws` gains `aws_iam_openid_connect_provider` for `token.actions.githubusercontent.com` (or an `import` of the account's existing one: one provider per URL per account) and a role trusting only `repo:counsellorben/Iverson:ref:refs/heads/main` for the pin-freshness workflow, with only `eks:DescribeAddonVersions` (VA 33). The role ARN is the repository variable `PIN_FRESHNESS_AWS_ROLE`; unset → the EKS rows report "skipped: no AWS role" and the rest run.

### 4.5 One pin table, two checks

New `ci/pins.yaml` and `ci/pins.py` (Python, stdlib only, with tests). Each row: a name, a **source** (`github-release <owner/repo>`, `pypi <package>`, `dockerhub-tag <repo> <series>`, `eks-addon <name>`, `cnpg-default <chart>` (CNPG's `DefaultImageName` at the stub chart's appVersion, the computation `image-scan.yml` performs today), `chart-app-version <chart>` (stale when the pinned tag differs from the stub chart's appVersion), `maven-central <groupId:artifactId> <series>` (the newest version in the artifact's `maven-metadata.xml` within the series), `go-proxy <module>` (the `Version` field of `https://proxy.golang.org/<module>/@latest`), or `dependabot` for rows Dependabot already tracks), and one or more **locations** (file + a regex whose group is the version).
- `pins.py consistency` (a `deploy-validate` step on every PR): every row's locations hold the same version. Fails on a mismatch.
- `pins.py freshness` (new `.github/workflows/pin-freshness.yml`, weekly `schedule` plus `workflow_dispatch`, `permissions: { contents: read, issues: write, id-token: write }`): for each non-`dependabot` row, the newest release (newest patch within the pinned series for `kindest/node`, compatible versions for EKS add-ons); opens or updates one issue `Pin behind: <name>` per stale row, closes it when current. It changes no code.

Rows (VA 29, 31, 34): Gateway API CRDs (`setup.sh:65`, `setup.ps1:62`), kind, `kindest/node` (`kind-config.yaml:15`), Istio (`vendor-istio-charts.sh:13` and the four vendored `Chart.yaml` versions; its chart repository has no 1.31.x, VA 29), the policy-controller image, the CNPG PostgreSQL image (`charts/postgres` `imageName`, source `cnpg-default cloudnative-pg`), the CNPG operator image (`cloudnative-pg-values.yaml` `image.tag`, source `chart-app-version cloudnative-pg`), Maven (`maven-wrapper.properties` `distributionUrl`, source `maven-central org.apache.maven:apache-maven 3.9`), the Maven Wrapper (`wrapperVersion`, source `maven-central org.apache.maven.wrapper:maven-wrapper 3`), the two EKS add-ons, govulncheck (source `go-proxy golang.org/x/vuln`, VA 52), kubeconform, kube-score, tfsec, trivy, cosign, helm (the five `azure/setup-helm` `version:` inputs and the `alpine/helm:<tag>` image at `.gitlab-ci.yml:37,55,86`; the branch sets the inputs to `v3.15.4` so both agree), terraform, pip, setuptools, pip-audit (each with all its workflow and `.gitlab-ci.yml` locations), `dependency-check-maven` (`pom.xml` + both workflows, source `dependabot`), and each GitLab image (stub + `.gitlab-ci.yml`, source `dependabot`), and `registry.k8s.io/kubectl` (`charts/authentik/values.yaml:12`, source `github-release kubernetes/kubernetes`; ignored by Dependabot, §4.1). The plan enumerates the exact locations; a `version:` input is identified from its action before it gets a row.

### 4.6 .NET lock files

New root `Directory.Build.props` with `RestorePackagesWithLockFile=true`; 27 `packages.lock.json` committed. Restores use `--locked-mode`: `dependency-scan.yml` (`nuget-audit`), `dotnet-build.yml` (an explicit locked restore before `build --no-restore`), `.gitlab-ci.yml:149` and `:237`, and the four Dockerfiles' `dotnet restore` (Api, Events, Sql, Vector), whose restore stage copies each copied project's `packages.lock.json` beside its `.csproj` (Api 8, the others 1) — without it `--locked-mode` has no lock to check. None restores for a runtime identifier (VA 35). Dependabot rewrites the lock files (VA 36).

### 4.7 Java trusted checksums

New `Iverson.Clients/Java/.mvn/maven.config`:
```
-Daether.artifactResolver.postProcessor.trustedChecksums=true
-Daether.artifactResolver.postProcessor.trustedChecksums.checksumAlgorithms=SHA-256
-Daether.trustedChecksumsSource.summaryFile=true
-Daether.trustedChecksumsSource.summaryFile.basedir=${maven.multiModuleProjectDirectory}/.mvn/checksums
-Daether.artifactResolver.postProcessor.trustedChecksums.failIfMissing=true
```
and the recorded `Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256`, covering the build and `dependency-check-maven`'s own resolution (VA 37). Every `mvn` run reads it, including `mvn -f Iverson.Clients/Java/pom.xml` from the repository root (`codeql.yml:75`, VA 38).

The record holds only what one Maven resolves (unpinned lifecycle plugins come from Maven's own bindings, and Maven 3.10 reads a differently named file, VA 49), so every Java build uses one pinned Maven: a Maven Wrapper 3.3.4 of `type=source` in `Iverson.Clients/Java` (`mvnw`, `mvnw.cmd`, `.mvn/wrapper/MavenWrapperDownloader.java`; no binary committed). `.mvn/wrapper/maven-wrapper.properties` sets `distributionUrl` to `apache-maven-3.9.16-bin.zip` on Maven Central, `distributionSha256Sum=5af3b743dd8b876b5c45da33b676251e5f1687712644abb4ee519ca56e1d89ce` and `wrapperSha256Sum=4e2fbf6554bc8a4702cdfdd3bef464f423393d784ddbb037216320ce55d5e4e1` (the `maven-wrapper-3.3.4.jar` the downloader fetches at first run); `Iverson.Clients/Java/.gitignore` lists `.mvn/wrapper/maven-wrapper.jar`. The callers: `codeql.yml:75` (`Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install`, from the root), the GitHub and GitLab `owasp-dependency-check-java` jobs, `DriverRunner.cs:146` (its java build command becomes `Iverson.Clients/Java/mvnw`, resolved to an absolute path the way `ResolveCommand` resolves exec commands), the re-record, and the runbooks' Java commands. The script-only wrapper type is not used: it accepts only a `-bin.zip` URL but fetches the `.tar.gz` on a host without `unzip`, and then fails its own SHA-256 check (VA 49). The runbook gives the re-record command (`./mvnw -B clean install` — the tests run, so surefire's JUnit Platform provider is recorded; `clean` because `codeql.yml:75` runs `clean install` — plus `org.owasp:dependency-check-maven:<ver>:check -DautoUpdate=false`, both with `-Daether.artifactResolver.postProcessor.trustedChecksums.record=true`; the check's `NoDataException` is expected there) as the step for each Maven Dependabot PR.

## 5. Tooling

1. **Token clients do not follow redirects.** `Iverson.LoadTest/Auth/AdminAutomationCredentials.cs:39` and `Iverson.ClientConformance/TokenBroker.cs:179` build their `HttpClient` over `HttpClientHandler { AllowAutoRedirect = false }` (the pattern of `AuthentikFlowExecutorClient.cs:45-47`); a 3xx is a failed token request whose error names the `Location`. `TokenBroker.MintClientCredentialsTokenAsync` gains an optional handler parameter for tests. Tests: a 307 yields one request and a failure.
2. **`mint_acting_user_token.py --base-url`** must be `https` or a loopback host (the rule of `CredentialEndpoints.IsHttpsOrLoopback`), checked before any request. Tests in `test_mint_acting_user_token.py`.
3. **`teacher_rerank.py`** drops `--api-key`; the key comes only from `VLLM_API_KEY` (the convention `run_measurement_batch.sh` already uses), and is sent only to an `https` or loopback `--base-url`. `run_measurement_batch.sh:61` stops passing the flag. Tests in `test_teacher_rerank.py`.
4. **Harness build tools get no secrets.** `DriverRunner.RunProcessAsync` (`:391-409`) starts every process from an empty environment plus an allowlist (`PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_*`, `USER`, `SHELL`, and the toolchain prefixes `DOTNET_*`, `NUGET_*`, `JAVA_HOME*`, `MAVEN_*`, `M2_HOME`, `GO*`, `NODE_*`, `NPM_CONFIG_*`/`npm_config_*`, `PYTHON*`, `PIP_*`, `VIRTUAL_ENV`); the driver run step adds `BuildEnvironment(context)` (VA 40) and the CA-trust variables `SSL_CERT_FILE`, `SSL_CERT_DIR`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` and `JAVA_TOOL_OPTIONS`, which the drivers need to verify the compose TLS server (VA 48). Build steps (`:175`) get the allowlist only. The full conformance run on every driver is the proof; a variable a toolchain needs and the allowlist lacks is added with a comment.
5. **Samples use TLS.** `.NET` `Iverson.Client.Sample/Program.cs:42` drops `allowInsecureChannelCallCredentials: true` (its endpoint is `https://localhost:7142`; the flag only matters for `http`, VA 41). Java `sample/.../Main.java` uses the TLS constructor `new IversonClient(host, port, credentials, actingUserToken)`, host and port from `IVERSON_GRPC_HOST`/`IVERSON_GRPC_PORT` (default `localhost:8080`, compose's TLS listener), and passes `false` for the plaintext-token-endpoint opt-in (side fix: it dials plaintext `localhost:5000`, which nothing serves, VA 42).

## 6. Rollout and runbooks

1. **The branch (SDD):** everything in §2.1 (workflows), §2.2's two scripts (not run), §2.3, §3, §4, §5 and the runbooks.
2. **After merge and push**, checked on `main`: the `helm` Dependabot jobs succeed and open the outstanding stub bumps (cnpg 0.29.1, Strimzi 1.3.0, StarRocks 1.11.8 at the time of writing); `nvd-update` saves a cache and `owasp-dependency-check-java` passes keyless; `pin-freshness` runs once (manual dispatch) and its issues are reviewed.
3. **Live GitHub settings,** each confirmed by the user at the step: delete the Dependabot `NVD_API_KEY` (only after step 2's keyless pass); push protection; tag ruleset (`--dry-run`, create disabled, read back, `--activate`); remove `vadim82`; branch protection last, once all 22 checks are green (today blocked on the admin-ui image's `brotli-libs`).
4. **The user's AWS account:** apply `bootstrap/aws`, set `PIN_FRESHNESS_AWS_ROLE`.

**Runbook:** new `docs/runbooks/csr12-supply-chain.md`: the rollout steps above; admission verification (§3); the Maven re-record step; the pin table and how to answer a `Pin behind` issue; the TUF restart residual. `csr11-clients-tooling-ci.md` §2 and §5 are updated (§2.1, §2.3).

## 7. Testing and verification

- **Unit tests:** §5's five fixes; `pins.py` (consistency mismatch, each source parser against recorded API responses, series filtering for `kindest/node`); the stub-reading helpers in both setup scripts.
- **Render and validate:** `helm template` + kubeconform for `image-policies` and `operator-namespace-policies` with `cosign-system`; `terraform validate` on all three clouds and `bootstrap/aws`; a check that the identity regexp is identical in `publish-images.yml`, the runbook and `image-policies` values.
- **Live kind probe** (throwaway cluster; the real `image-policies` chart; real published images; engine installed with the same values as Terraform): digest admitted; tag rewritten; wrong identity refused; cross-image swap refused (in-cluster registry copy with referrers, as VA 16); init container checked; a CNPG `Cluster` with the pinned `imageName` shows no instance rollout over several reconcile cycles; `helm upgrade` over the rewritten Deployment as operator; third-party image admitted; engine down → refused.
- **Lock files:** locked restore + full .NET suites + the api Docker build, and the api Docker build with a changed `PackageReference` version failing `NU1004`; Java fresh-repository build through `./mvnw` (Maven 3.9.16), a tampered `distributionSha256Sum` or `wrapperSha256Sum` refused by `mvnw`, a tampered entry refused, an unrecorded version bump refused with `Missing from summaryFile trusted checksum(s)`, the re-record command run once and then `./mvnw -B test` from a fresh repository passing.
- **Pins:** `pins.py consistency` fails on a deliberate mismatch for an Istio, a GitLab and a multi-workflow row.
- **CI:** the branch's PR run exercises the keyless `owasp-dependency-check-java` path (from `main`'s cache); `nvd-update` and the GitLab split are proven on `main` (§6 step 2).
- **Harness:** a full conformance run on all five drivers with the allowlisted environment.

## 8. Known issues / accepted as out of scope

Accepted by the user (counsellorben), 2026-10-26:
1. **Admin bypass, no required reviews.** The user is the only writer and pushes to `main` directly; a stolen admin credential could disable any protection.
2. **kind, local and compose do not verify images** (unsigned local builds; tag-only local images would be refused, VA 22).
3. **TUF at engine start.** A policy-controller restart while `tuf-repo-cdn.sigstore.dev` is unreachable crash-loops the engine and refuses first-party pod creates until it returns. The startup fetch is unconditional in v0.15.1; a `TrustRoot` does not avoid it; an in-cluster mirror would (VA 24) and was declined.
4. **Maven Dependabot PRs need a checksum re-record** (one command, run through `./mvnw`) before they go green, and a Maven or wrapper bump needs a re-record and new `distributionSha256Sum`/`wrapperSha256Sum`; Dependabot does not update the file (VA 39).
5. **The EKS freshness rows stay inert** until the AWS role is applied.
6. **Branch protection waits** for the upstream `nginx-unprivileged` rebuild that carries `brotli-libs` 1.2.1 (CVE-2026-111497).
7. **Model-image content-tag binding** stays as accepted in round 11 (any digest the workflow signed under a content tag).
8. **Strimzi- and StarRocks-operator-managed workloads in `iverson`** are unverified against the engine's `tag@digest` rewrite (CDR-1 §3.1 and CDR-2 §3.1, option A pins only CNPG's PostgreSQL and operator images).

## 9. Out of scope

- `docs/security/tma.md` (next TMA refresh).
- Four unmerged research branches (`student-distill`, `simple-jev-judge`, `teacher-lenient`, `rfdt-tenant`) call `teacher_rerank.py --api-key` or carry their own `--api-key` flags; each needs §5.3's change when it merges.
- The runtime-floating toolchains (`node-version: "22"`, `go-version: "1.26"` with `check-latest`, `dotnet-version: "10.0.x"`, `java-version: "21"`, `python-version: "3.11"`) float within their line by design and are not pins.

## Choices made while writing

1. **One pin table** serves both the freshness check and the multi-location consistency checks (GitLab images, Istio, the dependency-check version), instead of a separate check per case.
2. **`nvd-update` runs from a directory holding only `.mvn/`**, not an empty one, so the key-holding plugin resolution is checksum-verified (VA 37) while no `pom.xml` can load.
3. **The `ClusterImagePolicy` objects ship as a local chart installed by Terraform** (the `operator-namespace-policies` pattern), not in the app chart: cluster-scoped, outside the release namespace's admission policy (VA 25), and a single `terraform apply` still works.
4. **Glob per image (`…/iverson-api*`)**; the four names share no prefix with each other.
5. **The engine image is overridden to v0.15.1** because the chart's default app is older; the full policy (subject binding) was verified on v0.15.1.

## Verified assumptions

Evidence gathered 2026-10-26 against `main` @ `af4ec5e9` (`b50c6df6` for images). "Probe 1" and "probe 2" are throwaway kind clusters (kind v0.33.0, `kindest/node` default v1.37.0, policy-controller chart 0.10.8 with app 0.13.1 and v0.15.1, Kyverno 3.9.2); records in the session scratchpad (`sc-probe/report.md`, `sc-probe2/report.md`).

**#10**
1. **The OWASP job holds the key one step after `mvn install`.** `dependency-scan.yml:72-101`: restore → `mvn install -DskipTests` (:93) → `check … NVD_API_KEY: ${{ secrets.NVD_API_KEY }}` (:94-96) → save on `main` only.
2. **`update-only` needs no project.** `dependency-check-maven-13.0.0.jar` `plugin.xml`: `update-only` `requiresProject=false`; `check` `requiresProject=true`, `requiresDependencyResolution=compile+runtime`; both read `nvdApiKeyEnvironmentVariable` (`help:describe`). Maven ran the goal as "Maven Stub Project (No POM)".
3. **A missing DB fails loudly.** `check -DautoUpdate=false -DdataDirectory=<empty>` on the Java client: `NoDataException: Autoupdate is disabled and the database does not exist`, BUILD FAILURE.
4. **Skipped `needs` skip dependants** unless the dependant's `if` continues ("If a job fails or is skipped, all jobs that need it are skipped unless the jobs use a conditional expression that causes the job to continue", workflow syntax reference).
5. **Cooldown** supports Maven (`default-days` and semver keys) and "is only available for version updates, not security updates" (Dependabot options reference).
6. **The keyless delta is small.** NVD API without a key: 4,740 CVEs modified in 1 day, 23,194 in 7 (3 and 12 pages of 2,000). `owasp-dependency-check-java` is not among the 22 required checks.

**Repository**
7. **All 22 protection-script check names exist on `main`** (`comm` of the script's list against `af4ec5e9`'s check runs: none missing). Live protection today: 11 checks, `enforce_admins` false, no reviews; 0 rulesets; push protection disabled; secret scanning enabled with 0 alerts.
8. **Rulesets API:** `target: tag`, `conditions.ref_name.include`, rules `creation`/`update` (`update_allows_fetch_and_merge`)/`deletion`, bypass `actor_type: RepositoryRole`, `bypass_mode: always` (REST docs). The admin role's numeric id is not documented (search found none), hence create-disabled-then-read-back.
9. **Push protection** is `security_and_analysis.secret_scanning_push_protection.status` via `PATCH /repos/{o}/{r}`, admin permission (REST docs).
10. **Collaborators:** `DELETE /repos/{o}/{r}/collaborators/{u}` (REST docs). `vadim82` has `write`, no commits, no PRs; `forks_count` 0.

**Publishing**
11. **The denial path rebuilds.** `publish-images.yml:129-159`: three `denied:|unauthorized:|401|403` answers → `state=denied` → "treating it as not published and building it"; the comment gives the reason (GHCR denies lookups in a package that does not exist yet).
12. **cosign v3.0.6** has `--certificate-github-workflow-sha` (and `-name`, `-ref`, `-repository`, `-trigger`); the workflow already binds commit tags with it (`:189`); the runbook (`csr11-clients-tooling-ci.md:39-46`) uses only the general regexp.
13. **The attestation names the image.** `cosign verify-attestation --type spdxjson` on `iverson-api@sha256:4268c5ab…` and `iverson-admin-ui` at `b50c6df6`: in-toto `subject[0].name` = `ghcr.io/counsellorben/iverson-api` / `…/iverson-admin-ui`; the SPDX `name` is masked (`ghcr.io/*******/iverson-api`).

**Admission (probes)**
14. **cosign v3 bundles.** Signatures and attestations are OCI 1.1 referrers under the `sha256-<hex>` fallback tag (no `.sig` tag; GHCR's referrers API returns none). `cosign verify` and `verify-attestation` with the workflow identity pass.
15. **policy-controller verifies the attestation, not the v3 signature.** Probe 1: signature path `no signatures found` / `bundle support for image signatures is not yet implemented` (0.13.1 and v0.15.1); attestation with `signatureFormat: bundle` admitted, wrong identity and wrong predicate type refused. Kyverno's `ImageValidatingPolicy` verifies both but removes its webhooks on graceful stop (pods admitted unverified) and refuses on any Rekor outage; 4 pods, 400m/320Mi vs 100m/128Mi.
16. **Subject binding works.** Probe 2: the CUE policy sees the whole statement; `subject: [{name: "ghcr.io/counsellorben/iverson-api"}]` admits api; admin-ui plus its referrers copied into an in-cluster registry under the api path is refused with `subject.0.name: conflicting values "ghcr.io/counsellorben/iverson-admin-ui" and "ghcr.io/counsellorben/iverson-api"` after signature verification; a real api copy in the same registry is admitted.
17. **Chart 0.10.8 ships app 0.13.1;** upstream is v0.15.1 (probe 1).
18. **Model images are unpublished.** GHCR anonymous lookups of `iverson-ollama-model` and `iverson-tei-model`: `DENIED` (no package); the clouds reference them via `modelImageRegistry: "ghcr.io/counsellorben/"` (`values-aws.yaml:25`, azure `:23`, gcp `:26`).
19. **Init containers are checked** in either position; a pod with only a bad init container is refused naming `spec.initContainers[0].image` (probe 2).
20. **CPU limit.** At the chart's 200m limit verification took 5.7–10+ s and one create hit `context deadline exceeded` at the 10 s webhook timeout; at 1000m about 3 s (probe 2).
21. **Chart facts.** Webhooks select `policy.sigstore.dev/include=true`, all `failurePolicy: Fail`, cover Pods, Deployments, ReplicaSets, StatefulSets, DaemonSets, Jobs and CronJobs (unsigned Deployment, Job and CronJob denied at the workload); `no-match-policy` is ConfigMap `config-policy-controller`, default deny, set by `webhook.configData` (probe 2).
22. **Local images.** In an opted-in namespace tag-only local images are refused (`must be an image digest`) whatever the no-match policy (probe 1).
23. **Helm and the chart's admission policy.** The Deployment template is rewritten to `tag@digest`; no drift loop; operator no-change and annotation-change upgrades succeed; every upgrade patches the Deployment, so a non-operator's no-change upgrade is refused by rule (d) (probe 2, with the real `admission-policy.yaml` rendered from `values-aws.yaml`).
24. **Outages.** Running engine with TUF, Rekor and Fulcio blocked: admitted. Restart with TUF blocked: `Failed to initialize TUF client`, crash-loop, refused (probe 1). The startup fetch is unconditional even with a `TrustRoot` (`webhook/main.go:134`); `-disable-tuf` then fetches lazily; a key-based `TrustRoot` fails on v0.15.1 (`logId.keyId` encoding); `-tuf-mirror`/`-tuf-root` against an in-cluster static copy works with all three blocked; its `timestamp.json` expires about weekly (probe 2).
25. **Scope.** The chart's `admission-policy.yaml` matches only core `pods`, `serviceaccounts/token`, the CONNECT subresources and `apps`/`batch` workloads (`:39-46`), so cluster-scoped `ClusterImagePolicy` objects are outside it; the namespace label is on a `Namespace` object, which namespace-scoped roles cannot edit.

**Pinning**
26. **Why the helm job fails.** Dependabot run 39691998646 (2026-10-21, 4 of 4 runs failed): `egress not allowlisted` for `aws.github.io`, `cloudnative-pg.github.io`, `kubernetes.github.io`, `starrocks.github.io`, `strimzi.io` (10 each) → `index.yaml : 403 Forbidden`; 60 `file://` subchart errors; `private_source_authentication_failure` for `iverson-api`/`iverson-admin-ui`; and `registry.k8s.io/kubectl` (`charts/authentik/values.yaml:12`): `GET https://registry.k8s.io:443/v2/registry.k8s.io/kubectl/tags/list` → 404 (a wrong path; `/v2/kubectl/tags/list` lists `v1.37.1`), `Error processing registry.k8s.io/kubectl (DockerRegistry2::NotFound)`, `unknown_error` in the run's error table (CDR-2 §2.2). Stub pins vs latest: cnpg 0.29.0/0.29.1, Strimzi 1.1.0/1.3.0, StarRocks operator 1.11.5/1.11.8. No Dependabot PR has ever touched a `Chart.yaml`.
27. **Declaring a registry allowlists it:** "Update jobs can connect only to hosts on an egress allowlist"; "Declare a registry under the top-level `registries` key even if it allows anonymous access"; `helm-registry` takes `url` (+ optional basic auth) (Dependabot private-registries docs).
28. **Setup helpers.** `setup.sh:28-35` `operator_version` and `setup.ps1:25-31` `Get-OperatorVersion` read `helm dependency list` of the operators stub; Calico `--repo https://docs.tigera.io/calico/charts --version v3.32.2` (`setup.sh:157-159`, `:176`; `setup.ps1:167`, `:184`), metrics-server `https://kubernetes-sigs.github.io/metrics-server/` `3.9.0`.
29. **Istio has no index entry.** `istio-release.storage.googleapis.com/charts` `base`: no 1.31.2, newest `1.31.0-rc.0`; `vendor-istio-charts.sh:3,13-14` vendors from the GitHub release tarball; GitHub's latest is 1.31.3 (2026-10-22).
30. **GitLab images:** 7 distinct `image:` values, all `@sha256:` pinned, no `services:` (`.gitlab-ci.yml:37…242`).
31. **Release APIs answer unauthenticated:** `api.github.com/repos/{kubernetes-sigs/gateway-api,kubernetes-sigs/kind,istio/istio}/releases/latest` → v1.6.3, v0.33.0, 1.31.3; Docker Hub lists `kindest/node` tags (v1.37.0, v1.36.4, v1.35.8, …). Gateway API is pinned only in the kind scripts (`setup.sh:65`, `setup.ps1:62`).
32. **EKS add-ons:** `vpc-cni` `v1.23.1-eksbuild.1` (`cluster-aws/main.tf:524`), `aws-ebs-csi-driver` `v1.66.1-eksbuild.1` (`:550`); `describe-addon-versions --addon-name --kubernetes-version` returns `addons[].addonVersions[].addonVersion` with `compatibilities[].clusterVersion` (AWS CLI reference; the IAM action is confirmed when the role is applied).
33. **`bootstrap/aws`** holds only the state bucket and lock table (`main.tf:27-55`); no OIDC provider.
34. **Tool pins outside Dependabot** (`git grep`, `uses:` lines excluded): govulncheck `v1.7.0`; kubeconform `v0.6.7`, kube-score `v1.19.0`, tfsec `v1.28.6` (`deploy-validate.yml` and `.gitlab-ci.yml`); trivy `v0.75.0` (`image-scan.yml:35`); cosign `v3.0.6` (`publish-images.yml:166`, `:320`); helm `v3.15.0` (`azure/setup-helm` `version:` at `deploy-validate.yml:26,60,115`, `image-scan.yml:50`, `publish-images.yml:34`) against `alpine/helm:3.15.4` on GitLab (`.gitlab-ci.yml:37,55,86`); terraform `1.7.5`; pip, setuptools, pip-audit (both CI systems).

**Lock files**
35. **.NET restore sites:** `dependency-scan.yml:27`, `dotnet-build.yml:19-20`, `.gitlab-ci.yml:149,237-238`, and the Api (`:21`, publish `--no-restore` `:33`), Events, Sql and Vector Dockerfiles, whose restore stages copy only `.csproj` files (`Iverson.Api/Dockerfile:13-21`, the others `:6-7`; build context `Iverson.Server/` for the three); `--locked-mode` with no lock file restores and exits 0 (CDR-1 §2.2 probe); no runtime identifier anywhere; no `Directory.Build.props`; 27 projects.
36. **Locked restore works.** Throwaway worktree with a root `Directory.Build.props`: 27 lock files generated; `dotnet restore Iverson.slnx --locked-mode --force`, the api project alone, and `dotnet build --no-restore` pass; changing `OpenTelemetry.Extensions.Hosting` 1.20.0 → 1.15.0 without the lock fails `NU1004`. Dependabot's NuGet updater tracks and rewrites `packages.lock.json` (`dependabot-core` `ModifiedFilesTracker.cs:42`, `EndToEndTests.cs:1380-1396`).
37. **Trusted checksums enforce** (Maven 3.9.9, scratch copy of the Java client): `.mvn/maven.config` with `${maven.multiModuleProjectDirectory}` records `.mvn/checksums/checksums-central.sha256` (302 SHA-256 entries incl. `dependency-check-maven` and its Lucene/H2 dependencies); a fresh repository verifies; a tampered `dependency-check-core` entry fails `trusted checksum mismatch`. An unrecorded artifact is refused only with `trustedChecksums.failIfMissing=true` (default false): with the four lines alone an unrecorded `grpc.version` 1.84.0 builds (CDR-1 §2.1); with the fifth line an unrecorded version fails `Missing from summaryFile trusted checksum(s) [SHA-256]` and a recorded one builds (one-dependency probe, 2026-10-26). A `clean install` + `check` record (678 entries) passes codeql's `clean install` and the harness's `-pl conformance -am -DskipTests package` from a fresh repository. The record must run the tests: a `-DskipTests clean install` record (476 entries) leaves `mvn -B -pl client test` failing `surefire-junit-platform:pom:3.6.0 (absent): Missing from summaryFile`; a `mvn -B clean install` record (489 entries) lets it pass 227 tests from an empty repository (CDR-2 §2.1, reproduced 2026-10-26).
38. **`-f` from the root** reads the project's `.mvn/maven.config` (`maven.multiModuleProjectDirectory` = `…/Iverson.Clients/Java`). VA 37 and VA 38 were observed under Maven 3.9.9 only (VA 49).
39. **Dependabot has no Maven lockfile support** (`dependabot-core` code search: 0 results).

**Tooling**
40. **Build steps inherit secrets.** `DriverRunner.cs:175-176` runs builds with no explicit environment; `:206-207` adds `BuildEnvironment` (`:344-357`) for the driver; `RunProcessAsync` (`:391-409`) only adds to the inherited environment; the harness reads `IVERSON_CLIENT_SECRET`, `IVERSON_CLIENT_ID`, `IVERSON_TOKEN_ENDPOINT`, `IVERSON_CLIENT_SCOPE`, `IVERSON_ACTING_USER_HOST_HEADER`.
41. **.NET flag scope.** `ServiceCollectionExtensions.cs:74-75` refuses only an `http` endpoint without the opt-in; the sample's endpoint is `https://localhost:7142`.
42. **Java sample.** `Main.java:50-60` calls `IversonClient.plaintext("localhost", 5000, …, true)`; compose publishes only `127.0.0.1:8080` and `:8081` (TLS), the api launch profile `https://localhost:7142` and `http://localhost:5021`; `new IversonClient(host, port, credentials, token)` (`IversonClient.java:79-80`) builds a default (TLS) channel.
43. **Token clients.** `AdminAutomationCredentials` takes a handler and has `AdminAutomationCredentialsTests.cs`; `TokenBroker.MintClientCredentialsTokenAsync` (`:177-179`) is static with `new HttpClient()` and has no tests; `CredentialEndpoints.cs:22-52` defines the https-or-loopback rule.
44. **`--api-key` callers:** `run_measurement_batch.sh:61` (`API_KEY_ARGS=(--api-key "$VLLM_API_KEY")`) on `main`; the four research branches mention `--api-key` in 7–11 files each.
45. **The engine rewrites every image; CNPG compares strings.** policy-controller v0.15.1 `pkg/webhook/validator.go:1059-1093` (`resolvePodSpec`) resolves every container, init and ephemeral image on CREATE/UPDATE, policy or not, to `tag@digest`, and `:1234-1246` (`refOrFieldError`) refuses any image that is not a digest. CNPG decides rollouts by exact comparison of the pod's image with `cluster.Status.Image` (`internal/controller/cluster_upgrade.go:554-572`, `pkg/specs/pg_pods.go:69-82`). A `tag@digest` input parses as `name.Digest`, whose `String()` is the input (go-containerregistry v0.21.3 `pkg/name/digest.go:63-65`), and the webhook writes back the same string (`validator.go:1088-1090`) (CDR-1 §3.1, source reads). The chart sets no `imageName` (`cluster.yaml`); stub chart 0.29.0 → appVersion 1.30.0 → `DefaultImageName = ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie` (`pkg/versions/versions.go` at v1.30.0); that tag's index digest `sha256:42708a75345b7a48fdd9257b071830783a97fd228529196b6313187a7198e185` (anonymous GHCR HEAD, 2026-10-26). The no-loop outcome on a live CNPG cluster is §7's. CNPG v1.30.0 makes a second live-pod comparison: the `bootstrap-controller` init-container image against `OperatorImageName` (`checkPodSpecIsOutdated`, `internal/controller/cluster_upgrade.go:722-731`; legacy `checkPodBootstrapImage`, `:574-595`), unless `EnableInstanceManagerInplaceUpdates` (default false); the init image is `OperatorImageName` (`pkg/specs/containers.go:36-37`). Chart 0.29.0 renders both `OPERATOR_IMAGE_NAME` and the operator container from `{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}` (`templates/deployment.yaml:84-85`, `:99`; `values.yaml` `image.tag: ""`); `ghcr.io/cloudnative-pg/cloudnative-pg:1.30.0` index digest `sha256:a2701eb97cdd2a34b1fdb2cb51987f544b706e40bec72ae7146cd8580efefebb` (anonymous GHCR HEAD, 2026-10-26) (CDR-2 §3.1).
46. **The webhook port on the clouds.** Chart 0.10.8 `templates/webhook/deployment_webhook.yaml:110` `containerPort: 8443` (Service `443` → `https`); `cluster-gcp/main.tf:213-226` opens only `["9443", "15017"]` from the control plane ("reaches the nodes only on the ports GKE's own firewall rule opens"); AWS and Azure set no node security group or NSG that would block it (EKS: cluster-to-node traffic allowed; AKS: all traffic within the VNet allowed by default) (CDR-1 §2.3).
47. **GitLab cache paths.** `cache:paths`: "An array of paths relative to the project directory (`$CI_PROJECT_DIR`)"; "You can only use paths that are in the local working copy" (GitLab CI YAML reference). `dataDirectory` is a parameter of `update-only` and `check` (VA 2, VA 3). `.gitlab-ci.yml` sets no `cache:`, `MAVEN_OPTS` or `maven.repo.local`.
48. **The drivers' CA trust comes from inherited variables.** `docs/runbooks/client-conformance-matrix.md:35-43`: "The harness and every driver it starts inherit these": `SSL_CERT_FILE` (.NET, Go, Python urllib), `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` (Python grpcio), `NODE_EXTRA_CA_CERTS` (TypeScript), `JAVA_TOOL_OPTIONS` trust store (Java).
49. **The record is Maven-specific; the source wrapper pins Maven.** GitHub `ubuntu-latest` (ubuntu-26.04) runs Maven 3.9.15 and its 2026-10-26 `main` runs executed `install:3.1.4`, `resources:3.4.0`, `jar:3.5.0`, `surefire:3.5.4` (`gh run view 40309993144`/`40309993255 --log`), against 3.1.2, 3.3.1, 3.4.1, 3.2.5 in a 3.9.9 record; the poms pin none of the default-lifecycle plugins; GitLab's image runs 3.9.16; Maven 3.10.0 (Resolver 2.0.24) reads only `checksums-central-<hash>.sha256` and checks import BOMs. From empty repositories against the 691-entry 3.9.9 record: codeql's `clean install` and the owasp `install` fail under 3.9.15 (`maven-install-plugin:pom:3.1.4 … Missing from summaryFile`), `DriverRunner`'s build fails under 3.9.16, every run fails under 3.10.0; the same codeql form passes under 3.9.9 (CDR-3 §3.1, sweep 1). Wrapper probes (2026-10-26, a host without `unzip`): Maven Central lists 3.9.16 as the latest 3.9.x and 3.3.4 as the latest wrapper; the script-only `mvnw` rejects a `.tar.gz` `distributionUrl` ("must match *-bin.zip") and, with `unzip` absent, fetches the `.tar.gz` and fails `Failed to validate Maven distribution SHA-256` against the zip's sum; `type=source` and `type=bin` both run Maven 3.9.16 with the zip's `distributionSha256Sum` (`5af3b743dd8b876b5c45da33b676251e5f1687712644abb4ee519ca56e1d89ce`, whose SHA-512 matches Apache's) and refuse a tampered one; `type=source` with `wrapperSha256Sum=4e2fbf6554bc8a4702cdfdd3bef464f423393d784ddbb037216320ce55d5e4e1` (the jar's SHA-1 matches Central's) runs and refuses a tampered one; a `type=source` `Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install` from the repository root exits 0. `DriverRunner.cs:146` runs `"mvn"` with no working directory (the repository root); no `Iverson.ClientConformance.Tests` file asserts the command.
50. **The GitLab `maven` image's `MAVEN_CONFIG` breaks `mvnw`.** The pinned `maven:3.9-eclipse-temurin-21@sha256:99e61abc…` (amd64 config) sets `MAVEN_CONFIG=/root/.m2` with entrypoint `/usr/local/bin/mvn-entrypoint.sh`, which ends `unset MAVEN_CONFIG` / `exec "$@"`. Maven Wrapper 3.3.4's `mvnw` passes the variable as arguments (`MAVEN_CMD_LINE_ARGS="$MAVEN_CONFIG $*"` `:327`, `${WRAPPER_LAUNCHER} $MAVEN_CONFIG "$@"` `:338`); `bin/mvn` does not (`bin/mvn:216`). A source-type `mvnw` with `MAVEN_CONFIG=/root/.m2` exits 1 `Unknown lifecycle phase "/root/.m2"`, and with it unset exits 0 (2026-10-26); on the real client against a 3.9.16 record, the unset form resolves everything and stops at the expected `NoDataException` (CDR-4 §2.1, W3, W8). GitLab's Kubernetes executor does not run image entrypoints by default (`FF_KUBERNETES_HONOR_ENTRYPOINT`, default false), and `.gitlab-ci.yml`'s `alpine/helm` and `hashicorp/terraform` jobs already rely on entrypoints being ignored (no `entrypoint: [""]` override) (GitLab docs; not run on a runner).
51. **GitLab variables reach every job unless environment-scoped.** GitLab environments docs: "By default, all CI/CD variables are available to all jobs in a pipeline"; "Limit the environment scope of a CI/CD variable by defining which environments it can be available for"; `action: prepare` "effectively bypasses deployment creation" and "gives you access to environment-scoped variables" (Tier: Free, Premium, Ultimate). Runbook `csr11-clients-tooling-ci.md:176-188` allows a Protected or a pipeline-schedule `NVD_API_KEY`, both of which reach every job of their pipelines (the repository says so itself at `.gitlab-ci.yml:184-187` and runbook `:179-180`); `.gitlab-ci.yml:193` passes the key into the owasp job. No job declares `environment:` (`grep -c 'environment:' .gitlab-ci.yml` → 0) and no `rules:` references `NVD` (CDR-5 §2.1). Not run on a GitLab runner; the owasp job's absence check is that run.
52. **govulncheck has no GitHub Releases past v1.1.4.** `api.github.com/repos/golang/vuln/releases/latest` → `v1.1.4`; tags `v1.9.0`, `v1.8.0`, `v1.7.0`, …; `proxy.golang.org/golang.org/x/vuln/@latest` → `{"Version":"v1.9.0","Time":"2026-10-11T03:39:53Z",…}` (unauthenticated, 2026-10-26). The pin is `v1.7.0` at `dependency-scan.yml:69` and `.gitlab-ci.yml:178`, the only references outside docs; no `go.mod` requires the module, so Dependabot does not track it (CDR-5 §2.2).
