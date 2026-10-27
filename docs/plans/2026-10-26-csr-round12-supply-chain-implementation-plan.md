# CSR Round 12 Sub-project 3: Supply Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-26-csr-round12-supply-chain-design.md` (commit SHA: `f6f5db2c`; design verification round 1 ✅ at `658c2f20`)

**Goal:** close round-12 #10 (the NVD key in Dependabot pull-request build code) and every bullet of #17 (TMA F7), as the spec defines them.

**Architecture:**
- **Locks and the one Maven (Tasks 1–2):** .NET restores against committed `packages.lock.json` files in locked mode; every Java build runs Maven 3.9.16 through a source-type Maven Wrapper, and every artifact it resolves must match a recorded SHA-256.
- **CI and publishing (Tasks 3–6):** the NVD key moves to a job that runs no repository code (GitHub and GitLab); the scan runs keyless from its cache; the publish workflow stops treating repeated registry denials as "not published" when the package exists, binds release signatures to their commit, and the runbook verifies as strictly; a script creates the admin-only release-tag ruleset.
- **Cluster admission (Tasks 7–8):** on the clouds, Sigstore policy-controller admits a first-party image only with the publish workflow's SPDX attestation naming that image; the CNPG images are pinned by digest so the engine's `tag@digest` rewrite rolls nothing.
- **Pins (Tasks 9–11):** Dependabot reaches the chart repositories, the kind and GitLab image pins get Dependabot stubs, an AWS role lets the EKS add-on pins be checked, and one table (`ci/pins.yaml`) drives a consistency check on every pull request and a weekly freshness check.
- **Tooling (Tasks 12–15):** token clients refuse redirects, credential scripts refuse plaintext remote URLs, the conformance harness gives builds and drivers an allowlisted environment, the samples use TLS.
- **Runbook and proof (Tasks 16–17), then rollout (R1–R4, after the merge, each confirmed by the user).**

**Tech stack:** .NET 10 (xUnit 2.9, FluentAssertions, Duende.IdentityModel 8.1.0), Java 21 with Maven 3.9.16 through Maven Wrapper 3.3.4 (`type=source`), `dependency-check-maven` 13.0.0, Python 3 (stdlib; pytest 9 for tests), GitHub Actions and GitLab CI, Helm 3 (`azure/setup-helm` v3.15.4), Sigstore policy-controller chart 0.10.8 with engine v0.15.1, CloudNativePG chart 0.29.0 (operator 1.30.0), Terraform (aws provider 5.100.0 in `bootstrap/aws`), kind v0.33.0 on podman.

---

## Global Constraints

**From the spec:**
- **One spec, one branch** (Decision 1). Tasks run in the order written.
- **#10, N2** (Decision 5): the NVD key is only in a job that runs no project code; every scan runs keyless from a cache.
- **Pinned versions:** Maven 3.9.16 (`distributionSha256Sum=5af3b743dd8b876b5c45da33b676251e5f1687712644abb4ee519ca56e1d89ce`), Maven Wrapper 3.3.4 `type=source` (`wrapperSha256Sum=4e2fbf6554bc8a4702cdfdd3bef464f423393d784ddbb037216320ce55d5e4e1`), `dependency-check-maven` 13.0.0, policy-controller chart 0.10.8 with engine `v0.15.1`, CNPG PostgreSQL `ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie@sha256:42708a75345b7a48fdd9257b071830783a97fd228529196b6313187a7198e185`, CNPG operator `image.tag: "1.30.0@sha256:a2701eb97cdd2a34b1fdb2cb51987f544b706e40bec72ae7146cd8580efefebb"`, `azure/setup-helm` inputs `v3.15.4`.
- **The admission engine is on the clouds only;** kind, local and compose are not verified (§8.2). Cloud profiles are checked by render and static checks; the engine is exercised live on a throwaway kind cluster (Task 17).
- **Live GitHub, GitLab and AWS changes are not part of the branch:** the branch adds the scripts and workflows; R1–R4 make the changes after the merge, each confirmed by the user at the step.
- **Never edit `docs/security/tma.md`** (spec §9).
- **Security write-ups** (comments, test names, docs, scripts) describe conditions and behaviour, never step-by-step exploitation. Scripts never print a token.
- **Most of `docs/` is gitignored** (`docs/plans/`, `docs/specs/`, `docs/criticalreviews/`, `docs/reviews/`, `docs/performance/`, `docs/superpowers/`; `.gitignore:46-51`): commit files there with `git add -f`. `docs/runbooks/` is tracked normally.
- **The user's running stacks are off-limits.** Never start, stop or modify the `iversonserver` compose project's containers, volumes, networks or images, the `iverson-*` containers, another session's `rfdt-*` containers or the `tei_models` volume. Live checks use throwaway names (`csr12s-*`), take before/after snapshots, and delete what they create.
- **Memory.** One heavy process at a time: a WSL OOM kills the whole VM. Wrap builds, test runs, image builds and Maven in `systemd-run --user --scope -q -p MemoryMax=<n>G`; export `MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false` (env.sh does).

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr12-supply-chain` on branch `csr12-supply-chain` (Task 0). Use absolute paths, and never `cd` to the main checkout.
- **Patches.** Each task's code is a patch that was replayed green in a scratch worktree at `658c2f20` (2026-10-26). Apply it with `git apply` from the worktree root as written; it must apply cleanly. If `git apply --check` fails, stop and report: never hand-merge a patch.
- **Generated files** (the 27 `packages.lock.json`, the Maven Wrapper scripts and properties, the checksum record) are made by the task's own commands, never typed in.
- **Commands.**
  - Use `command grep` (plain `grep` is a gitignore-aware ugrep that skips `docs/`), or `git grep`.
  - Every repo-wide grep excludes `.env*`.
  - The docker CLI is podman; refer to Docker Hub images fully qualified (`docker.io/library/maven:…`).
  - Kill processes by PID, never with `pkill -f`.
  - Downloading and running a tool that is not already installed needs the user's permission (the session's permission rules refused an unvetted binary once); the tools this plan downloads are named in the task, with their SHA-256.
- **Helm.** Run `helm dependency build Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint` of the umbrella chart.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative with no prefix, and end with exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Do not use your own model name in the trailer.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd` (Task 0 creates it), never in the repo.
- **Shell state.** Each bash block runs in a fresh shell. Blocks that use `$SCR` or `$WT` start with `source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh`.
- **Test filters.** `--filter "FullyQualifiedName~X"` is a substring match; this plan writes `~.ClassName.` with dots on both sides.

---

## File Structure

**Create:**
- `Directory.Build.props` — `RestorePackagesWithLockFile` for every project (Task 1).
- 27 × `**/packages.lock.json` — generated (Task 1).
- `Iverson.Clients/Java/mvnw`, `mvnw.cmd`, `.mvn/wrapper/MavenWrapperDownloader.java`, `.mvn/wrapper/maven-wrapper.properties` — the source-type Maven Wrapper, generated (Task 2).
- `Iverson.Clients/Java/.mvn/maven.config` — trusted-checksum enforcement (Task 2).
- `Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256` — the record, generated (Task 2).
- `Iverson.Server/deploy/scripts/apply-tag-ruleset.sh` — the release-tag ruleset (Task 6).
- `Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/{Chart.yaml,values.yaml,templates/clusterimagepolicies.yaml}` — the four ClusterImagePolicies (Task 7).
- `Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml` — the CNPG operator digest pin (Task 8).
- `Iverson.Server/deploy/kind/versions/Chart.yaml` — the kind-only chart pins (Task 9).
- `Iverson.Server/deploy/kind/test_chart_pins.py` — the setup helpers' tests (Task 9).
- `ci/gitlab-images/Dockerfile` — the GitLab job images for Dependabot (Task 9).
- `ci/pins.yaml`, `ci/pins.py`, `ci/test_pins.py` — the pin table and its checks (Task 11).
- `.github/workflows/pin-freshness.yml` — the weekly freshness check (Task 11).
- `Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs` (Task 12).
- `docs/runbooks/csr12-supply-chain.md` (Task 16).

**Modify:**
- `.github/workflows/dependency-scan.yml` — locked NuGet restore (1); `nvd-update` + keyless scan (3).
- `.github/workflows/dotnet-build.yml` — locked restore, `build --no-restore` (1).
- `.github/workflows/codeql.yml` — Java build through `mvnw` (2).
- `.github/workflows/publish-images.yml` — denial rule, release commit binding (5); `setup-helm` v3.15.4 (11).
- `.github/workflows/deploy-validate.yml` — image-policies lint, identity agreement, cosign-system render (7); pin consistency, `setup-helm` v3.15.4 (11).
- `.github/workflows/image-scan.yml` — CNPG images from the pinned renders (8); `setup-helm` v3.15.4 (11).
- `.github/dependabot.yml` — Maven cooldown (3); registries, split helm entries, kind stub, GitLab images (9).
- `.gitlab-ci.yml` — locked restores (1); `nvd-update` + keyless scan (4); image-policies lint, cosign-system render (7).
- `Iverson.Server/Iverson.{Api,Events,Sql,Vector}/Dockerfile` — lock files beside each `.csproj`, locked restore (1).
- `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs` — Java build through `mvnw` (2); allowlisted environments (14). Tests: `Iverson.ClientConformance.Tests/DriverRunnerTests.cs`.
- `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs`, `Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs` — no redirects (12). Tests: `Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs`.
- `docs/runbooks/csr11-clients-tooling-ci.md` — §5 (4), §2 (5), §7 (16).
- `Iverson.Server/deploy/terraform/modules/operators/main.tf` — engine, policies, namespace label (7); CNPG values file (8). `…/operators/versions/Chart.yaml` — `policy-controller` entry (7). `…/charts/operator-namespace-policies/{values.yaml,templates/networkpolicies.yaml}` — `cosignSystem` (7). `…/modules/cluster-gcp/main.tf` — webhook port 8443 (7).
- `Iverson.Server/deploy/helm/iverson/charts/postgres/{values.yaml,templates/cluster.yaml}` — `imageName` (8).
- `Iverson.Server/deploy/kind/setup.sh`, `setup.ps1` — stub-directory parameter, Calico and metrics-server from the kind stub (9).
- `Iverson.Server/deploy/terraform/bootstrap/aws/main.tf` — OIDC provider and pin-freshness role (10).
- `Iverson.Server/deploy/scripts/mint_acting_user_token.py` + its test; `Iverson.Server/Iverson.LoadTest/scripts/{teacher_rerank.py,test_teacher_rerank.py,run_measurement_batch.sh}` (13).
- `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs`, `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java` (15).

---

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` (and the CDR and DDV rounds) at spec-write time and are NOT re-verified here. Trusted as ground truth (spec "Verified assumptions", verbatim):

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
53. **The GitLab key change is a rollout step.** §6 had no GitLab step (CDR-6 §2.1). The owasp job's absence line, as §2.1 writes it, exits 0 with `NVD_API_KEY` unset and 1 when it is set or set empty (`sh` and `bash`, CDR-6 R19). A variable scoped to `nvd-update` reaches only a job whose environment is `nvd-update` (VA 51), and no job declares one before this branch (`grep -c 'environment:' .gitlab-ci.yml` → 0), so the redefinition must follow the merge. `.gitlab-ci.yml:21-26` calls the schedule trigger dormant until a GitLab Pipeline Schedule is provisioned out-of-band; the repository's only remote is GitHub. Whether a GitLab project runs this pipeline, and whether a group-level `NVD_API_KEY` exists, are not visible from the repository; the step is conditioned on the first and deletes any definition of the second.
54. **The publish job's token can tell absent from existing.** A temporary branch `probe-packages-token` (pushed from `origin/main` with one `on: push` workflow, `permissions: packages: read`; deleted after) ran `curl` with this repository's `GITHUB_TOKEN` against `GET /users/counsellorben/packages/container/<name>`: `iverson-api: 200`, `iverson-ollama-model: 404` (run 40393306445, 2026-10-26). GitHub's docs conflict on this (the installation-token endpoint list includes the endpoint; the Packages REST page names classic tokens), so the run settles it (CDR-7 §3.1, option A).

---

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-10-26), mostly by replaying the plan's own patches in a scratch worktree at `658c2f20`:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Command | The worktree and branch can be created: no `csr12-supply-chain` branch exists, `.worktrees/` is ignored. | `git branch --list 'csr12-supply*'` empty; `.gitignore:79` `.worktrees/`. |
| 2 | Path | No `Directory.Build.props` or `Directory.Packages.props` exists anywhere, so a root one applies to all 27 projects. | `find . -name 'Directory.Build.*'` and `Directory.Packages.props`: none. |
| 3 | Code | The root props file generates 27 lock files; `dotnet restore Iverson.slnx --locked-mode`, then `dotnet build Iverson.slnx --no-restore`, then `dotnet test --no-build --filter "Category!=Integration"` pass. | Replay: 27 `packages.lock.json`; locked restore rc 0; build 0 errors; 11 test projects, 4,070 tests, 0 failed. |
| 4 | Code | An XML comment may not contain `--`: a props file whose comment said `--locked-mode` failed every project with MSB4024. | Replay (first attempt): `error MSB4024 … Directory.Build.props could not be loaded`; the plan's comment has no `--`. |
| 5 | Code | The api image restores in locked mode with its 8 lock files copied beside their `.csproj` and publishes `--no-restore`; Events and Sql build; the root props file is not needed in an image (NuGet uses a lock file present beside the project). | Replay: `podman build` api rc 0, Events rc 0, Sql rc 0; NuGet docs: "Once a project has packages.lock.json file in its root directory, the lock file is always used with restore even if the property RestorePackagesWithLockFile is not set." |
| 6 | Code | The api image build fails `NU1004` when a `PackageReference` version changes without its lock file. | Replay: `OpenTelemetry.Extensions.Hosting` 1.20.0 → 1.15.0: `error NU1004: The package reference … version has changed`. |
| 7 | Consumer | The Vector image does not build on `main` (its Dockerfile copies no referenced project: 18 × CS0234/CS0246); its locked restore step passes. Out of scope; Task 1 checks only its restore step. | Replay: `main`'s Dockerfile rc 1 with 18 `error CS`; with lock files, STEP 4 `dotnet restore … --locked-mode` `Restored …`, then the same compile errors. |
| 8 | Consumer | The .NET restore and build sites are exactly `dependency-scan.yml:27`, `dotnet-build.yml:19-20`, `.gitlab-ci.yml:149,237-238` and the four Dockerfiles; codeql's C# autobuild and the harness's .NET driver build keep their implicit (unlocked) restore. | `git grep -E 'dotnet (restore|build|publish|test|run|pack)'`; `codeql.yml:27-28` `csharp` `autobuild`. |
| 9 | Command | `mvn -N org.apache.maven.plugins:maven-wrapper-plugin:3.3.4:wrapper -Dtype=source -Dmaven=3.9.16` writes `mvnw` (mode 100755, "version 3.3.4"), `mvnw.cmd`, `.mvn/wrapper/MavenWrapperDownloader.java` and `maven-wrapper.properties` with `wrapperVersion=3.3.4`, `distributionType=source`, `distributionUrl=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.16/apache-maven-3.9.16-bin.zip` and `wrapperUrl=…/maven-wrapper-3.3.4.jar`; the two SHA-256 lines are appended. | Replay; `git ls-files -s` mode `100755` for `mvnw`. |
| 10 | Code | Under the wrapper (Maven 3.9.16) the record writes `.mvn/checksums/checksums-central.sha256`: 463 entries after `clean install` (227 tests), 647 after `dependency-check-maven:13.0.0:check -DautoUpdate=false` (`NoDataException` expected), starting from no file. | Replay `rec1.log`/`rec2.log`: `Saving 463 checksums`, `Saving 647 checksums`. |
| 11 | Code | From empty local repositories against the 647 record: codeql's `Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install` from the root, the owasp `install -DskipTests` and `check -DautoUpdate=false` (stops at `NoDataException`), the harness's `-pl conformance -am -DskipTests package` from the root, `./mvnw -B test` (227 pass) and the GitLab form (`install -DskipTests`, then `check`, `MAVEN_CONFIG` unset) all resolve; a `check` with no `install` fails on `iverson-sample` (`Could not find artifact io.iverson:iverson-client:jar:1.0.0-SNAPSHOT`) once the first module's check passes (shown with `-DfailOnError=false`). | Replay `v-codeql.log` … `gl-check.log`: rc 0, or `NoDataException` with no `Missing from summaryFile`. |
| 12 | Code | `bin/mvn` run from a directory holding only `.mvn/` reads its `maven.config`: `update-only` resolves against the record (reaches the NVD retries), and a tampered `dependency-check-core` entry fails `trusted checksum mismatch`; its default data directory is `<local repo>/org/owasp/dependency-check-data/11.0`, inside the cached path. | Replay `v-update.log`, `v-update2.log`; `ls m2-nvd/org/owasp/dependency-check-data` → `11.0`. |
| 13 | Code | The pinned GitLab `maven` image has `curl`, `wget`, `sha256sum`, `jar` and `bash` but no `unzip` or `python3`; `jar xf` unpacks the Maven zip, and `sh <dir>/bin/mvn` runs it (jar drops exec bits). The GitLab `nvd-update` script, run with `sh` in that image, checks the SHA-256, unpacks, and reaches the NVD stage. | `podman run docker.io/library/maven:3.9-eclipse-temurin-21@sha256:99e61abc…`; replay `gl-nvd.log`: `/tmp/maven.zip: OK`, `Maven Stub Project (No POM)`, NVD retries. |
| 14 | Code | `actions/checkout` at `3d3c42e5…` (v7.0.1) has the inputs `sparse-checkout` and `sparse-checkout-cone-mode`. | Its `action.yml` lines 65, 70. |
| 15 | Code | The GitHub `nvd-update` install step runs as written with `RUNNER_TEMP`/`GITHUB_ENV` set: the SHA-256 check passes, `MVN=<RUNNER_TEMP>/maven/apache-maven-3.9.16/bin/mvn` is exported, `.mvn` is copied, and `sh "$MVN" … update-only` reaches the NVD stage. | Replay `gh-update.log`. Workflow syntax beyond YAML parsing is not linted locally (actionlint was refused by the session's permission rules); the branch's PR run exercises the workflows (spec §7). |
| 16 | Code | The publish lookup step, with three registry denials, builds on a packages-API 404 and fails the run on 200, 500 and a network failure; `main`'s step builds in all four cases. | `$SCR/t5_lookup.sh` (Task 5 Step 2) against the replay and against `main`. |
| 17 | Code | The publish job's token (`packages: write`) can read `GET /users/counsellorben/packages/container/<name>`. UNVERIFIED in docs (VA 54 proved `packages: read`); a wrong guess answers neither 200 nor 404, which fails the run (fail closed), and the rule runs only after three denials. | GitHub docs list only `read|write|none`; accepted by the user 2026-10-26. |
| 18 | Code | `cosign verify-attestation` prints JSON lines with `payload` (base64 in-toto statement), `payloadType`, `signatures`; `.payload | @base64d | fromjson | .subject[0].name` is `ghcr.io/counsellorben/iverson-api` for the api image. | cosign v3.0.6 against `iverson-api:b50c6df6…`, decoded with Python. |
| 19 | Code | Rulesets REST: POST body (`target: tag`, `enforcement`, `bypass_actors[]`, `conditions.ref_name.include`, rules `creation`, `update` with `update_allows_fetch_and_merge`, `deletion`); GET returns `current_user_can_bypass` (`always`, `pull_requests_only`, `never`, `exempt`); PUT updates `enforcement` alone. The admin role's `actor_id` is NOT documented: the script sends 5 and `--activate` requires the read-back `RepositoryRole:5:always` and `current_user_can_bypass=always`, and the runbook adds the UI check. | docs.github.com "REST API endpoints for rules"; search found no official id; the script's branches against a fake `gh` (Task 6 Step 2). |
| 20 | Code | Chart `policy-controller` 0.10.8: `webhook.image.version` is printed after `repository:` (or `@` for a bare `sha256:`), so `v0.15.1@sha256:0492bb26…` renders `ghcr.io/sigstore/policy-controller/policy-controller:v0.15.1@sha256:0492bb264fb1d9bdc8e3f343ef542cc85b7dd7c7fd8d9524b453c2bd31a1d128` (v0.15.1's index digest, as probe 1 recorded); `webhook.resources.limits.cpu`, `webhook.configData` are values; the webhook pod is labelled `control-plane: policy-controller-webhook` (release `policy-controller`) and listens on 8443 and 9090; its only other image (`cgr.dev/chainguard/kubectl:latest-dev`) runs in a `post-delete` hook. | `helm pull … --version 0.10.8`; `_helpers.tpl:114-147`; rendered Deployment; anonymous GHCR HEAD of `v0.15.1`; `cleanup-leases.yaml:10` `helm.sh/hook: post-delete`. |
| 21 | Code | The `image-policies` chart renders four ClusterImagePolicies; the api one is identical (parsed YAML) to the policy probe 2 admitted and refused with. | `helm template` vs `sc-probe2/cip-api.yaml`: equal. |
| 22 | Code | kubeconform has no usable schema for ClusterImagePolicy: datreeio/CRDs-catalog's `clusterimagepolicy_v1beta1.json` (at `3eb90d47`) rejects `signatureFormat`. A strict schema built from the chart's own CRD (v1beta1) validates the four policies and rejects a misspelled field; CI runs `helm lint` instead. | kubeconform: `additional properties 'signatureFormat' not allowed` (datree); Task 7 Step 3: 4 valid, `signatureFormatX` invalid. |
| 23 | Code | `operator-namespace-policies` with `cosignSystem=true` (and `kind=false`) renders 18 NetworkPolicies, 3 in `cosign-system`, all kubeconform-valid; kind's render is byte-identical to `main`'s. | Replay renders; `cmp` of the kind renders. |
| 24 | Command | `terraform init -backend=false -lockfile=readonly` and `validate` pass for `aws`, `azure`, `gcp` and `bootstrap/aws`; `terraform fmt -check -recursive` passes; tfsec v1.28.6 reports 0 findings on `main` and on the branch. | Replay. |
| 25 | Code | CNPG chart 0.29.0 with `image.tag: "1.30.0@sha256:a2701eb9…"` renders that string into the operator container and `OPERATOR_IMAGE_NAME`; `helm show chart … --version 0.29.0` prints `appVersion: 1.30.0`. | `helm template cnpg cloudnative-pg --version 0.29.0 -f …` lines 20606-20614. |
| 26 | Code | The postgres `imageName` renders in all five overlays and every overlay lints; `image-scan.yml`'s listing step then lists both CNPG images by digest and nothing else changes. | Replay: `helm lint` × 5; `diff images-main.txt images-wt.txt`: only the two CNPG lines. |
| 27 | Code | Dependabot: top-level `registries` with `type: helm-registry` + `url`; per-update `registries` (a list or `"*"`); the `helm` ecosystem uses `helm-registry` (HTTP, not OCI). | Dependabot options and private-registries docs. |
| 28 | Code | `helm dependency list` on the kind stub prints `tigera-operator v3.32.2` and `metrics-server 3.9.0`; the setup helpers, cut out of the scripts, return each pinned version and fail naming the stub for an unpinned chart (bash; PowerShell 7.5.13); `main`'s helpers fail the same tests. | `Iverson.Server/deploy/kind/test_chart_pins.py`: 5 passed with pwsh, 3 passed + 2 skipped without; 5 failed against `main`. |
| 29 | Command | PowerShell 7.5.13 for Linux x64 (`powershell-7.5.13-linux-x64.tar.gz`, SHA-256 `e9256bfc6ac98fcbec97270e8a986577a7346a587837cc80d30595eef3c2546b`) runs here. | Downloaded, checked against the release's `hashes.sha256`, `$PSVersionTable` → 7.5.13. |
| 30 | Code | `aws_iam_openid_connect_provider` validates without `thumbprint_list` under aws provider 5.100.0. | `terraform validate` in `bootstrap/aws`. |
| 31 | Code | `pins.py`'s sources against the live endpoints: GitHub `releases/latest` `tag_name`; PyPI `info.version`; Docker Hub `results[].name` with a full-URL `next`; proxy.golang.org `Version`; `maven-metadata.xml` `<version>` list (its `<latest>` is a pre-release, `4.0.0-rc-7`, so the series filter excludes pre-releases); `helm show chart` `appVersion`; `versions.go` `DefaultImageName`. All 21 non-EKS, non-Dependabot rows read; 12 are behind today (istio, govulncheck, kubeconform, kube-score, tfsec, trivy, cosign, helm, terraform, pip, setuptools). | Live run of `pins.newest` per row (Task 11 Step 4). |
| 32 | Code | `describe-addon-versions` output: `addons[].addonVersions[].addonVersion` with `compatibilities[].clusterVersion` (VA 32); the EKS Kubernetes version is `cluster-aws/variables.tf` `kubernetes_version` default `"1.35"`. | AWS CLI reference; `variables.tf:11-14`. |
| 33 | Code | gh CLI 2.95: `gh issue list --state open --search … --json number,title --limit`, `gh issue create --title --body`, `gh issue edit <n> --body`, `gh issue close <n> --comment`; `gh secret delete --app dependabot`; `gh variable set --body`; `gh api -f 'a[b][c]=v'` nests fields. | cli.github.com manual; local `--help`. |
| 34 | Code | `aws-actions/configure-aws-credentials` v6.3.0 is commit `e1253824e5c10ff9df46874f81ed3ec929e19cfd` with inputs `role-to-assume` and `aws-region`; `vars` is available in a step `if:`. | `api.github.com` tag ref; its `action.yml`; GitHub contexts table. |
| 35 | Code | GitHub's `ubuntu-latest` image has the AWS CLI, GitHub CLI, curl, unzip and Python 3 (the README maps `ubuntu-latest` to 24.04; VA 49's runs showed 26.04; the 26.04 README lists AWS CLI 2.37.13 and GitHub CLI 2.102.0). `pin-freshness.yml` installs Helm itself. | actions/runner-images README and Ubuntu 26.04 README. |
| 36 | Code | Every pin-table location matches at the branch tip and the 31 rows agree; each location matches as often as a repository-wide grep finds the version (helm: 3 in `deploy-validate.yml`, 3 in `.gitlab-ci.yml`; pip, setuptools and pip-audit: 2 per file; kind: the variables and the scripts' header comments). | `python3 ci/pins.py consistency`; per-location counts vs `git grep -c`. |
| 37 | Code | Duende.IdentityModel 8.1.0's `TokenResponse` (a `ProtocolResponse`) has `HttpResponse`; for a 307 `IsError` is true and `HttpResponse.Headers.Location` is the redirect target. | Package XML docs; the Task 12 tests pass against a loopback `HttpListener`. |
| 38 | Consumer | `TokenBroker.MintClientCredentialsTokenAsync` has one caller (`TokenBroker.cs:112`) and `Iverson.ClientConformance` grants `InternalsVisibleTo` to its tests; `AdminAutomationCredentials`'s constructor callers pass a handler only in tests. | `git grep`; `Iverson.ClientConformance.csproj:18`. |
| 39 | Consumer | `--api-key` is passed only by `run_measurement_batch.sh:61` on `main` (VA 44); `api_key` appears only in `teacher_rerank.py` and its tests. | `git grep -E -- '--api-key|api_key'`. |
| 40 | Code | `ProcessStartInfo.Environment.Clear()` followed by adds gives the child exactly those variables: a fake driver sees an allowlisted probe variable and not a non-allowlisted one. | `RunPhaseAsync_StartsTheDriverWithOnlyTheAllowlistedEnvironment` passes; with `Clear()` removed it fails. |
| 41 | Consumer | Platform's live-run scratch environment (`HOME`, `NUGET_PACKAGES`, `PYTHONUSERBASE`, `GOMODCACHE`, `MAVEN_OPTS`, `npm_config_cache`) and the CA variables (`SSL_CERT_FILE`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH`, `NODE_EXTRA_CA_CERTS`, `JAVA_TOOL_OPTIONS`) all pass the allowlist (`NODE_EXTRA_CA_CERTS` by the `NODE_` prefix). | Platform plan Task 27 Steps 2 and 7; `IsAllowed` cases. |
| 42 | Consumer | With `mvnw` committed, the harness's Java build no longer reports "mvn not found" as a skip: a host without Java gets a broken Java row. | `DriverRunner.cs` `ToolMissing` arises only from `Process.Start`; `mvnw` exists. |
| 43 | Code | The .NET sample builds without `allowInsecureChannelCallCredentials` (its only warning, CS8619, is `main`'s too); the Java sample compiles with the TLS constructor. | Replay builds. |
| 44 | Command | kind v0.33.0 (`kind-linux-amd64`, SHA-256 `aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d`) runs here; the published api and admin-ui images and their attestations are readable anonymously. | Downloaded and checked; cosign `verify-attestation` without a login (row 18). |
| 45 | Order | Tasks run in order with no forward dependency: the wrapper and record (2) precede every workflow that runs `mvnw` (3, 4); every pin location exists before the pin table (11): the wrapper (2), the update-only version (3, 4), the engine pin (7), the CNPG pins (8), the kind and GitLab stubs and the setup comments (9), `pin-freshness.yml` (11 itself); the runbook (16) names what 2–11 built. No later task changes a pom, so the record stays complete. | Per-task replay in this order; Task 11's consistency passes only after 2–10. |
| 46 | Convention | Commit subjects are lowercase imperative with no prefix; docs under `docs/plans` and `docs/specs` need `git add -f`; `docs/runbooks` is tracked. | `git log --format=%s -15`; `.gitignore:46-51`. |
| 47 | Code | CNPG v1.30.0's webhook accepts `imageName: ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie@sha256:42708a75…` (a `tag@digest` reference). Source read only: the run-tier probe (a `go test` of `validateImageName`) was refused by the session's permission rules; the run is Task 17 Step 6 (user decision 2026-10-26, CIR-1 §3.2 A). | CNPG v1.30.0 `internal/webhook/v1/cluster_webhook.go:771-797` refuses only `latest`, an empty tag or an unparsable tag; machinery v0.5.0 `pkg/image/reference/imagename.go` strips the digest before the tag (`imagename_test.go:44-45`: `New("postgres:13@sha256:…").Tag == "13"`). |
| 48 | Code | Lock files written by the local SDK (10.0.113) restore in locked mode under CI's `10.0.x` and the image's SDK (10.0.401): no project uses an SDK feature that adds SDK-versioned implicit packages, and a changed `PackageReference` still fails `NU1004` in the image. | CIR-2 A-SDK: `git grep` for AOT/trim/RID/self-contained/central-package properties finds none besides `Sdk="Microsoft.NET.Sdk.Web"`, no `global.json`, no SDK-versioned implicit packages in the 27 lock files; A-P12: the Api restore stage in the pinned SDK image, `--locked-mode` rc 0, the changed version `error NU1004`. |
| 49 | Code | The checksum record stays valid as Maven Central publishes new versions: no build step resolves a version from Central's current metadata (no ranges, no prefix lookups). | CIR-2 A-RANGE: after the record, only `maven-metadata-local.xml` (the reactor's own installs) exists in the local repository; no `maven-metadata-central.xml`. |
| 50 | Code | The record holds `linux-x86_64` protoc binaries, so every Java caller runs on x86_64 Linux: GitHub's `ubuntu-latest` jobs, GitLab's `maven` image, the harness host. | CIR-2 A-P4 (`uname -m` in the pinned `maven` image: `x86_64`); `codeql.yml:14`, `dependency-scan.yml:73` `runs-on: ubuntu-latest`; A-S4 `harness rc=0` here. |
| 51 | Code | Task 1 Step 3's amended counter reads the locked restore step (its own `Restored` line or its cache line), and its cleanup removes only dangling images created during the step. | CIR-2 A-CNT: the counter over round 1's real podman logs gives `new=1` for cached and uncached builds (old counter: 2 uncached); the cleanup is a before/after `comm -13` of dangling IDs. |
| 52 | Consumer | Dependabot's helm job reads Task 8's `modules/operators/versions/cloudnative-pg-values.yaml` (it takes every `*.yaml` in the stub directory) and extracts no dependency from it. | CIR-2 shard C, T8 Dependabot row: the values file has no `dependencies:` or image `repository:`/`tag:` pair Dependabot's helm updater parses. |
| 53 | Code | The ClusterImagePolicy CRD's conversion webhook (v1beta1 stored as v1alpha1) is the same Service and port as admission, so the `cosign-system` NetworkPolicy's 8443 ingress covers it. | CIR-2 shard C, T7 CRD-storage row; VA 46 (Service 443 → 8443). |
| 54 | Code | Each of the three identity copies (the runbook's, `publish-images.yml`'s `IDENTITY`, the chart's `subjectRegExp`), changed alone, fails deploy-validate's identity check. | CIR-2 C-P-idmut: one copy changed (`tags/v` → `tag/v`) per run, each run rc 1. |
| 55 | Consumer | Task 17 Step 6 reads Task 8's exact `imageName` string from `charts/postgres/values.yaml`. | CIR-2 C-P-img17: Step 6's `IMG` sed against Task 8's values prints the pinned `…:18.4-system-trixie@sha256:42708a75…`. |
| 56 | Command | R4's imports take these IDs: `aws_s3_bucket`, `aws_s3_bucket_versioning`, `aws_s3_bucket_server_side_encryption_configuration` and `aws_s3_bucket_public_access_block` the bucket name; `aws_dynamodb_table` the table name; `aws_iam_openid_connect_provider` the ARN. | CIR-2 D-TFDOC: provider v5.100.0 `website/docs/r/*.html.markdown` import sections (rechecked at UIP-1). |
| 57 | Code | An imported state bucket does not by itself make R4's `terraform plan` show a change to it (the module sets no `force_destroy`). | CIR-2 D-TFPLAN: `terraform plan` over a state holding only `aws_s3_bucket.state` shows no change to that resource. |
| 58 | Command | The runbook's wrapper regeneration (`mvn -N …maven-wrapper-plugin:<v>:wrapper -Dtype=source -Dmaven=<v> -Daether.artifactResolver.postProcessor.trustedChecksums=false`) succeeds with the record and `.mvn/maven.config` present, and writes the properties without the two SHA-256 lines. | CIR-2 D-REGEN: rc 0; the properties come out as 4 lines. |
| 59 | Code | Maven Central publishes a `.sha512` beside the Maven zip and only a `.sha1` (and `.asc`) beside the wrapper jar; the 3.3.4 jar's SHA-1 equals its `.sha1`, and its SHA-256 is the plan's `wrapperSha256Sum`. | CIR-2 D-SHA; rechecked at UIP-2: jar `.sha512` 404, `.sha1` 200 and matching, zip `.sha512` 200, jar SHA-256 `4e2fbf65…`. |
| 60 | Consumer | `pin-freshness.yml` can open issues (the repository has Issues enabled) and `gh` finds the repository from the checkout's `origin` remote with `GH_TOKEN` set, `persist-credentials: false` notwithstanding. | CIR-2 shard D S5: `has_issues: True`; the step sets `GH_TOKEN`, and the checkout keeps `origin`. |
| 61 | Consumer | Task 17 Step 5's images exist for `origin/main` at run time (every push to `main` publishes), and Step 7's empty worktree status holds after the harness and builds (all their outputs are ignored). | CIR-2 D-GHCR (`publish-images.yml:13-17`, no `paths:`); D-IGN: `git check-ignore` on all 10 output paths → ignored. |
| 62 | Code | `pins.py consistency` passes a row with no locations instead of raising `IndexError` on `values[0]`; `load_table` already refuses such a row before `main` calls `consistency`. | DPV-1 §3.1 A (user pick 2026-10-27): the fix re-proved in the model (`fix/Pins.Consistency.dfy`, 36 verified, 0 errors); on the replay, the witness `consistency([{name, source, locations: []}])` passes with the edit (before it: `IndexError('list index out of range')`). |

---

## Tasks

### Task 0: Branch

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson
git worktree add -b csr12-supply-chain .worktrees/csr12-supply-chain main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh <<'T0ENV'
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd
WT=/home/ben/repositories/Iverson/.worktrees/csr12-supply-chain
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
export TF_PLUGIN_CACHE_DIR=$HOME/.cache/csr12p-terraform/plugins
T0ENV
mkdir -p $HOME/.cache/csr12p-terraform/plugins
git -C .worktrees/csr12-supply-chain log --oneline -1
```

Expected: the worktree is on `csr12-supply-chain`, and HEAD equals `main` (`658c2f20` or later; if later, the patches below must still apply).

---

### Task 1: .NET lock files and locked restores

**Spec:** §4.6. VA 35, 36.

**Files:**
- Create: `Directory.Build.props`; 27 × `packages.lock.json` (generated).
- Modify: `Iverson.Server/Iverson.{Api,Events,Sql,Vector}/Dockerfile`, `.github/workflows/dependency-scan.yml`, `.github/workflows/dotnet-build.yml`, `.gitlab-ci.yml`.

- [ ] **Step 1: Apply the patch** (the props file, the four Dockerfiles, the CI restore sites). The props file's comment must not contain two hyphens in a row (XML forbids them in a comment, and MSBuild then fails every project with MSB4024).

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t1.patch <<'T1_PATCH'
diff --git a/.github/workflows/dependency-scan.yml b/.github/workflows/dependency-scan.yml
index 9869f5ae..bce8617f 100644
--- a/.github/workflows/dependency-scan.yml
+++ b/.github/workflows/dependency-scan.yml
@@ -24,7 +24,8 @@ jobs:
       - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
         with:
           dotnet-version: "10.0.x"
-      - run: dotnet restore Iverson.slnx
+      # CSR round 12 §4.6: locked to the committed packages.lock.json files.
+      - run: dotnet restore Iverson.slnx --locked-mode
       - run: dotnet list Iverson.slnx package --vulnerable --include-transitive 2>&1 | tee scan.log
       - run: grep -q "has the following vulnerable packages" scan.log && exit 1 || exit 0
 
diff --git a/.github/workflows/dotnet-build.yml b/.github/workflows/dotnet-build.yml
index a27b35ab..e0957538 100644
--- a/.github/workflows/dotnet-build.yml
+++ b/.github/workflows/dotnet-build.yml
@@ -16,7 +16,10 @@ jobs:
       - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
         with:
           dotnet-version: "10.0.x"
-      - run: dotnet build Iverson.slnx
+      # CSR round 12 §4.6: restore locked to the committed packages.lock.json files, then build without
+      # a second, unlocked restore.
+      - run: dotnet restore Iverson.slnx --locked-mode
+      - run: dotnet build Iverson.slnx --no-restore
       - run: dotnet test Iverson.slnx --filter "Category!=Integration"
 
   typescript-sdk-build-test:
diff --git a/.gitlab-ci.yml b/.gitlab-ci.yml
index aacc332e..4b69dd2c 100644
--- a/.gitlab-ci.yml
+++ b/.gitlab-ci.yml
@@ -146,7 +146,8 @@ nuget-audit:
   image: mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
   extends: .rules-dependency-scan
   script:
-    - dotnet restore Iverson.slnx
+    # CSR round 12 §4.6: locked to the committed packages.lock.json files.
+    - dotnet restore Iverson.slnx --locked-mode
     - dotnet list Iverson.slnx package --vulnerable --include-transitive 2>&1 | tee scan.log
     - grep -q "has the following vulnerable packages" scan.log && exit 1 || exit 0
 
@@ -234,7 +235,10 @@ dotnet-build-test:
     - if: '$CI_PIPELINE_SOURCE == "merge_request_event"'
     - if: '$CI_PIPELINE_SOURCE == "push" && $CI_COMMIT_BRANCH == "main"'
   script:
-    - dotnet build Iverson.slnx
+    # CSR round 12 §4.6: restore locked to the committed packages.lock.json files, then build without
+    # a second, unlocked restore.
+    - dotnet restore Iverson.slnx --locked-mode
+    - dotnet build Iverson.slnx --no-restore
     - dotnet test Iverson.slnx --filter "Category!=Integration"
 
 typescript-sdk-build-test:
diff --git a/Directory.Build.props b/Directory.Build.props
new file mode 100644
index 00000000..63138e12
--- /dev/null
+++ b/Directory.Build.props
@@ -0,0 +1,8 @@
+<Project>
+  <!-- CSR round 12 §4.6: every restore writes packages.lock.json beside its project and honours it.
+       CI and the Dockerfiles restore in locked mode, so a resolution that differs from the committed
+       lock file fails. -->
+  <PropertyGroup>
+    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
+  </PropertyGroup>
+</Project>
diff --git a/Iverson.Server/Iverson.Api/Dockerfile b/Iverson.Server/Iverson.Api/Dockerfile
index 52236453..f1945b03 100644
--- a/Iverson.Server/Iverson.Api/Dockerfile
+++ b/Iverson.Server/Iverson.Api/Dockerfile
@@ -10,15 +10,17 @@
 FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
 WORKDIR /src
 
-COPY ["Iverson.Server/Iverson.Api/Iverson.Api.csproj",                                               "Iverson.Server/Iverson.Api/"]
-COPY ["Iverson.Server/Iverson.Sql/Iverson.Sql.csproj",                                               "Iverson.Server/Iverson.Sql/"]
-COPY ["Iverson.Server/Iverson.StarRocks/Iverson.StarRocks.csproj",                                   "Iverson.Server/Iverson.StarRocks/"]
-COPY ["Iverson.Server/Iverson.Embeddings/Iverson.Embeddings.csproj",                                 "Iverson.Server/Iverson.Embeddings/"]
-COPY ["Iverson.Server/Iverson.Vector/Iverson.Vector.csproj",                                         "Iverson.Server/Iverson.Vector/"]
-COPY ["Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj",                                     "Iverson.Server/Iverson.Patterns/"]
-COPY ["Iverson.Server/Iverson.Events/Iverson.Events.csproj",                                         "Iverson.Server/Iverson.Events/"]
-COPY ["Iverson.Clients/DotNet/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj",             "Iverson.Clients/DotNet/Iverson.Client.Contracts/"]
-RUN dotnet restore "Iverson.Server/Iverson.Api/Iverson.Api.csproj"
+# CSR round 12 §4.6: each project's packages.lock.json goes beside its .csproj, so the locked restore
+# below checks the resolution against the committed lock file instead of re-resolving it.
+COPY ["Iverson.Server/Iverson.Api/Iverson.Api.csproj", "Iverson.Server/Iverson.Api/packages.lock.json", "Iverson.Server/Iverson.Api/"]
+COPY ["Iverson.Server/Iverson.Sql/Iverson.Sql.csproj", "Iverson.Server/Iverson.Sql/packages.lock.json", "Iverson.Server/Iverson.Sql/"]
+COPY ["Iverson.Server/Iverson.StarRocks/Iverson.StarRocks.csproj", "Iverson.Server/Iverson.StarRocks/packages.lock.json", "Iverson.Server/Iverson.StarRocks/"]
+COPY ["Iverson.Server/Iverson.Embeddings/Iverson.Embeddings.csproj", "Iverson.Server/Iverson.Embeddings/packages.lock.json", "Iverson.Server/Iverson.Embeddings/"]
+COPY ["Iverson.Server/Iverson.Vector/Iverson.Vector.csproj", "Iverson.Server/Iverson.Vector/packages.lock.json", "Iverson.Server/Iverson.Vector/"]
+COPY ["Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj", "Iverson.Server/Iverson.Patterns/packages.lock.json", "Iverson.Server/Iverson.Patterns/"]
+COPY ["Iverson.Server/Iverson.Events/Iverson.Events.csproj", "Iverson.Server/Iverson.Events/packages.lock.json", "Iverson.Server/Iverson.Events/"]
+COPY ["Iverson.Clients/DotNet/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj", "Iverson.Clients/DotNet/Iverson.Client.Contracts/packages.lock.json", "Iverson.Clients/DotNet/Iverson.Client.Contracts/"]
+RUN dotnet restore "Iverson.Server/Iverson.Api/Iverson.Api.csproj" --locked-mode
 
 # Explicit copies (CSR round-10 #19): only the projects the publish needs reach the build stage.
 COPY Iverson.Server/Iverson.Api/ Iverson.Server/Iverson.Api/
diff --git a/Iverson.Server/Iverson.Events/Dockerfile b/Iverson.Server/Iverson.Events/Dockerfile
index d65f456f..05f9e9db 100644
--- a/Iverson.Server/Iverson.Events/Dockerfile
+++ b/Iverson.Server/Iverson.Events/Dockerfile
@@ -3,8 +3,9 @@
 FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
 WORKDIR /src
 
-COPY ["Iverson.Events/Iverson.Events.csproj", "Iverson.Events/"]
-RUN dotnet restore "Iverson.Events/Iverson.Events.csproj"
+# CSR round 12 §4.6: the lock file goes beside the .csproj for the locked restore.
+COPY ["Iverson.Events/Iverson.Events.csproj", "Iverson.Events/packages.lock.json", "Iverson.Events/"]
+RUN dotnet restore "Iverson.Events/Iverson.Events.csproj" --locked-mode
 
 COPY Iverson.Events/ Iverson.Events/
 
diff --git a/Iverson.Server/Iverson.Sql/Dockerfile b/Iverson.Server/Iverson.Sql/Dockerfile
index 76f743f5..8173fdec 100644
--- a/Iverson.Server/Iverson.Sql/Dockerfile
+++ b/Iverson.Server/Iverson.Sql/Dockerfile
@@ -3,8 +3,9 @@
 FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
 WORKDIR /src
 
-COPY ["Iverson.Sql/Iverson.Sql.csproj", "Iverson.Sql/"]
-RUN dotnet restore "Iverson.Sql/Iverson.Sql.csproj"
+# CSR round 12 §4.6: the lock file goes beside the .csproj for the locked restore.
+COPY ["Iverson.Sql/Iverson.Sql.csproj", "Iverson.Sql/packages.lock.json", "Iverson.Sql/"]
+RUN dotnet restore "Iverson.Sql/Iverson.Sql.csproj" --locked-mode
 
 COPY Iverson.Sql/ Iverson.Sql/
 
diff --git a/Iverson.Server/Iverson.Vector/Dockerfile b/Iverson.Server/Iverson.Vector/Dockerfile
index 2edb8b9b..e597d66f 100644
--- a/Iverson.Server/Iverson.Vector/Dockerfile
+++ b/Iverson.Server/Iverson.Vector/Dockerfile
@@ -3,8 +3,9 @@
 FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
 WORKDIR /src
 
-COPY ["Iverson.Vector/Iverson.Vector.csproj", "Iverson.Vector/"]
-RUN dotnet restore "Iverson.Vector/Iverson.Vector.csproj"
+# CSR round 12 §4.6: the lock file goes beside the .csproj for the locked restore.
+COPY ["Iverson.Vector/Iverson.Vector.csproj", "Iverson.Vector/packages.lock.json", "Iverson.Vector/"]
+RUN dotnet restore "Iverson.Vector/Iverson.Vector.csproj" --locked-mode
 
 COPY Iverson.Vector/ Iverson.Vector/
 
T1_PATCH
cd $WT && git apply --check $SCR/t1.patch && git apply $SCR/t1.patch && git status --short
````

- [ ] **Step 2: Generate the lock files and check a locked restore and build.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
systemd-run --user --scope -q -p MemoryMax=6G dotnet restore Iverson.slnx > $SCR/t1-restore.log 2>&1; echo "restore rc=$?"
git ls-files -o --exclude-standard | command grep -c 'packages.lock.json$'
systemd-run --user --scope -q -p MemoryMax=6G dotnet restore Iverson.slnx --locked-mode > $SCR/t1-locked.log 2>&1; echo "locked rc=$?"
systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.slnx --no-restore > $SCR/t1-build.log 2>&1; echo "build rc=$?"; command grep -E '^ +[0-9]+ Error' $SCR/t1-build.log
```

Expected: `restore rc=0`, `27`, `locked rc=0`, `build rc=0`, `0 Error(s)`.

- [ ] **Step 3: The images restore in locked mode, and a changed version is refused.** Events and Sql build fully; Vector's restore step must pass, and its later compile fails on `main` already (18 errors: its Dockerfile copies none of the projects it references), which is out of scope.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
podman images -q --no-trunc -f dangling=true | sort > $SCR/t1-dangling-before.txt
systemd-run --user --scope -q -p MemoryMax=6G podman build -q -f Iverson.Server/Iverson.Api/Dockerfile -t localhost/csr12s-api:probe . > $SCR/t1-api.log 2>&1; echo "api rc=$?"
for p in Events Sql Vector; do l=$(echo $p | tr A-Z a-z)
  systemd-run --user --scope -q -p MemoryMax=6G podman build -f Iverson.Server/Iverson.$p/Dockerfile -t localhost/csr12s-$l:probe Iverson.Server > $SCR/t1-$l.log 2>&1
  echo "$p rc=$? restore-step: $(command grep -A3 -- 'restore .*--locked-mode' $SCR/t1-$l.log | command grep -c -E "Restored /src/Iverson.$p/Iverson.$p.csproj|Using cache")"; done
F=Iverson.Server/Iverson.Api/Iverson.Api.csproj; cp $F $SCR/api.csproj.bak
sed -i 's/Include="OpenTelemetry.Extensions.Hosting" Version="1.20.0"/Include="OpenTelemetry.Extensions.Hosting" Version="1.15.0"/' $F
systemd-run --user --scope -q -p MemoryMax=6G podman build -q -f Iverson.Server/Iverson.Api/Dockerfile -t localhost/csr12s-api-neg:probe . > $SCR/t1-neg.log 2>&1; echo "changed version rc=$?"; command grep -m1 -o 'error NU1004' $SCR/t1-neg.log
cp $SCR/api.csproj.bak $F; git diff --quiet -- $F && echo "csproj restored"
podman rmi localhost/csr12s-api:probe localhost/csr12s-events:probe localhost/csr12s-sql:probe >/dev/null 2>&1; comm -13 $SCR/t1-dangling-before.txt <(podman images -q --no-trunc -f dangling=true | sort) | xargs -r podman rmi >/dev/null 2>&1; true
```

Expected: `api rc=0`; `Events rc=0 restore-step: 1`; `Sql rc=0 restore-step: 1`; `Vector rc=1 restore-step: 1`; `changed version rc=1`, `error NU1004`; `csproj restored`. (The `Category!=Integration` suites run in Task 17 Step 1.)

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Directory.Build.props $(git ls-files -o --exclude-standard | command grep 'packages.lock.json$') \
  Iverson.Server/Iverson.Api/Dockerfile Iverson.Server/Iverson.Events/Dockerfile Iverson.Server/Iverson.Sql/Dockerfile Iverson.Server/Iverson.Vector/Dockerfile \
  .github/workflows/dependency-scan.yml .github/workflows/dotnet-build.yml .gitlab-ci.yml
git commit -q -m "lock the .NET dependencies and restore them in locked mode

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat HEAD | tail -1
```

Expected: `35 files changed`.

---

### Task 2: One pinned Maven and trusted checksums for Java

**Spec:** §4.7, Decision 6. VA 37, 38, 39, 49, 50.

**Files:**
- Create (generated): `Iverson.Clients/Java/mvnw`, `mvnw.cmd`, `.mvn/wrapper/MavenWrapperDownloader.java`, `.mvn/wrapper/maven-wrapper.properties`, `.mvn/checksums/checksums-central.sha256`.
- Create: `Iverson.Clients/Java/.mvn/maven.config`.
- Modify: `.github/workflows/codeql.yml`, `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`. Test: `Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs`.
- Not modified: `Iverson.Clients/Java/.gitignore` already ignores `.mvn/wrapper/maven-wrapper.jar` through its `*.jar` line (`git check-ignore -v` → `.gitignore:3:*.jar`).

**Interfaces:** Produces `Iverson.Clients/Java/mvnw` and `.mvn/` (used by Tasks 3, 4, 11 and the harness).

- [ ] **Step 1: Generate the source-type wrapper and add the two SHA-256 lines.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Clients/Java
systemd-run --user --scope -q -p MemoryMax=3G mvn -B -q -N org.apache.maven.plugins:maven-wrapper-plugin:3.3.4:wrapper -Dtype=source -Dmaven=3.9.16 -Dmaven.repo.local=$SCR/m2-wrapgen
cat >> .mvn/wrapper/maven-wrapper.properties <<'T2PROPS'
distributionSha256Sum=5af3b743dd8b876b5c45da33b676251e5f1687712644abb4ee519ca56e1d89ce
wrapperSha256Sum=4e2fbf6554bc8a4702cdfdd3bef464f423393d784ddbb037216320ce55d5e4e1
T2PROPS
cat .mvn/wrapper/maven-wrapper.properties; ls -l mvnw mvnw.cmd .mvn/wrapper/MavenWrapperDownloader.java
./mvnw -v 2>&1 | head -1
```

Expected: the properties read `wrapperVersion=3.3.4`, `distributionType=source`, `distributionUrl=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.16/apache-maven-3.9.16-bin.zip`, `wrapperUrl=…/maven-wrapper-3.3.4.jar` and the two sums; `mvnw` is executable; `Apache Maven 3.9.16 (2bdd9fdd…)`.

- [ ] **Step 2: Apply the patch** (`.mvn/maven.config`, codeql's Java build, the harness's Java build command and its test).

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t2.patch <<'T2_PATCH'
diff --git a/.github/workflows/codeql.yml b/.github/workflows/codeql.yml
index 0ea9b9ae..8633cb17 100644
--- a/.github/workflows/codeql.yml
+++ b/.github/workflows/codeql.yml
@@ -72,7 +72,9 @@ jobs:
       # before descending into it.
       - name: Build Java client (manual build mode)
         if: matrix.language == 'java-kotlin'
-        run: mvn -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install
+        # CSR round 12 §4.7: through the repository's Maven Wrapper, the one Maven the trusted-checksum
+        # record was written under.
+        run: Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install
 
       - name: Perform CodeQL Analysis
         uses: github/codeql-action/analyze@b96794f015dfd88f77b49b1c93e0fa7110f94c63 # v4.38.0
diff --git a/Iverson.Clients/Java/.mvn/maven.config b/Iverson.Clients/Java/.mvn/maven.config
new file mode 100644
index 00000000..8f5900e9
--- /dev/null
+++ b/Iverson.Clients/Java/.mvn/maven.config
@@ -0,0 +1,5 @@
+-Daether.artifactResolver.postProcessor.trustedChecksums=true
+-Daether.artifactResolver.postProcessor.trustedChecksums.checksumAlgorithms=SHA-256
+-Daether.trustedChecksumsSource.summaryFile=true
+-Daether.trustedChecksumsSource.summaryFile.basedir=${maven.multiModuleProjectDirectory}/.mvn/checksums
+-Daether.artifactResolver.postProcessor.trustedChecksums.failIfMissing=true
diff --git a/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs b/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
index dc06a3eb..40fcbf8d 100644
--- a/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
+++ b/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
@@ -190,6 +190,31 @@ public class DriverRunnerTests
         }
     }
 
+    // CSR round 12 §4.7: the Java build runs the repository's own Maven Wrapper (the one Maven the
+    // trusted-checksum record was written under), resolved against the repository root, never an `mvn`
+    // found on PATH.
+    [Fact]
+    public async Task RunPhaseAsync_BuildsTheJavaDriverWithTheRepositorysMavenWrapper()
+    {
+        var root = Directory.CreateTempSubdirectory("iverson-conformance-fake-").FullName;
+        var wrapper = Path.Combine(root, "Iverson.Clients", "Java", "mvnw");
+        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
+        File.WriteAllText(wrapper, "#!/bin/sh\necho \"repository wrapper ran: $*\" >&2\nexit 3\n");
+        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
+        try
+        {
+            var outcomes = await new DriverRunner(root).RunPhaseAsync(Phase.Register, ["java"], Context());
+
+            var broken = outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Broken>().Subject;
+            broken.ExitCode.Should().Be(3);
+            broken.Stderr.Should().Contain("repository wrapper ran: -B -f Iverson.Clients/Java/pom.xml");
+        }
+        finally
+        {
+            Directory.Delete(root, recursive: true);
+        }
+    }
+
     // The timeout message prints the driver's command line, so it must not carry a secret either.
     [Fact]
     public async Task RunPhaseAsync_TimeoutMessage_CarriesNoSecret()
diff --git a/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs b/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
index 4b7d0216..50c65489 100644
--- a/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
+++ b/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
@@ -143,7 +143,9 @@ public sealed class DriverRunner : IDriverRunner
             "bin/conformance", []),
         new DriverSpec(
             "java", null,
-            "mvn", ["-B", "-f", "Iverson.Clients/Java/pom.xml", "-pl", "conformance", "-am", "-DskipTests", "package"],
+            // CSR round 12 §4.7: the repository's Maven Wrapper, which runs the one Maven the trusted-checksum
+            // record was written under (Iverson.Clients/Java/.mvn).
+            "Iverson.Clients/Java/mvnw", ["-B", "-f", "Iverson.Clients/Java/pom.xml", "-pl", "conformance", "-am", "-DskipTests", "package"],
             "java", ["-jar", "Iverson.Clients/Java/conformance/target/iverson-conformance-driver.jar"]),
     ];
 
@@ -172,8 +174,9 @@ public sealed class DriverRunner : IDriverRunner
 
             if (spec.BuildCommand is not null && !_builtLanguages.Contains(spec.Language))
             {
+                var buildCwd = ResolveCwd(spec.Cwd);
                 var buildResult = await RunProcessAsync(
-                    spec.BuildCommand, spec.BuildArgs!, ResolveCwd(spec.Cwd), ct);
+                    ResolveCommand(spec.BuildCommand, buildCwd), spec.BuildArgs!, buildCwd, ct);
 
                 if (buildResult is ProcessOutcome.ToolMissing missing)
                 {
T2_PATCH
cd $WT && git apply --check $SCR/t2.patch && git apply $SCR/t2.patch && git status --short
````

- [ ] **Step 3: Record the checksums under the wrapper.** The tests run in the first command, so the test runner's plugins are recorded; the second ends in `NoDataException` (no NVD database), as expected.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Clients/Java
R=-Dmaven.repo.local=$SCR/m2-rec; REC=-Daether.artifactResolver.postProcessor.trustedChecksums.record=true
systemd-run --user --scope -q -p MemoryMax=4G ./mvnw -B $R $REC clean install > $SCR/t2-rec1.log 2>&1; echo "install rc=$?"
command grep -E 'Tests run: [0-9]+, Failures' $SCR/t2-rec1.log | tail -1; command grep -o 'Saving [0-9]* checksums' $SCR/t2-rec1.log
systemd-run --user --scope -q -p MemoryMax=4G ./mvnw -B $R $REC org.owasp:dependency-check-maven:13.0.0:check -DautoUpdate=false > $SCR/t2-rec2.log 2>&1; echo "check rc=$?"
command grep -m1 -o 'NoDataException[^.]*' $SCR/t2-rec2.log; command grep -o 'Saving [0-9]* checksums' $SCR/t2-rec2.log
rm .mvn/checksums/checksums-central.sha256.bak; ls .mvn/checksums
```

Expected: `install rc=0`, `Tests run: 227, Failures: 0, …`, `Saving 463 checksums` (about); `check rc=1`, `NoDataException: Autoupdate is disabled and the database does not exist`, `Saving 647 checksums` (about; the exact counts follow Maven Central's metadata on the day); `checksums-central.sha256` alone (the second run backs up the first record as `….sha256.bak`, which is not committed).

- [ ] **Step 4: Every caller builds from an empty local repository against the record, and tampering is refused.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
J=$WT/Iverson.Clients/Java
run() { local name=$1 dir=$2; shift 2; rm -rf $SCR/m2-$name
  ( cd "$dir" && systemd-run --user --scope -q -p MemoryMax=4G "$@" -Dmaven.repo.local=$SCR/m2-$name > $SCR/t2-$name.log 2>&1; echo "$name rc=$?" )
  command grep -m2 -E -o 'Missing from summaryFile[^]]*|trusted checksum mismatch|NoDataException[^.]*|Tests run: [0-9]+, Failures: [0-9]+' $SCR/t2-$name.log | tail -2; }
run codeql $WT Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -DskipTests clean install
run harness $WT Iverson.Clients/Java/mvnw -B -f Iverson.Clients/Java/pom.xml -pl conformance -am -DskipTests package
run test $J ./mvnw -B test; command grep -E 'Tests run: [0-9]+, Failures' $SCR/t2-test.log | tail -1
run gitlab-check $J env -u MAVEN_CONFIG ./mvnw -B org.owasp:dependency-check-maven:check -DfailBuildOnCVSS=7 -DautoUpdate=false -DdataDirectory=$SCR/empty-nvd
# Tampering: a changed record entry, an unrecorded version, a changed distributionSha256Sum, a changed wrapperSha256Sum.
cp $J/.mvn/checksums/checksums-central.sha256 $SCR/rec.bak; cp $J/.mvn/wrapper/maven-wrapper.properties $SCR/props.bak; cp $J/pom.xml $SCR/pom.bak
sed -i -E 's#^[0-9a-f]{64}(  com/google/code/gson/gson/)#0000000000000000000000000000000000000000000000000000000000000000\1#' $J/.mvn/checksums/checksums-central.sha256
command grep -c '^0\{64\}  com/google/code/gson/gson/' $J/.mvn/checksums/checksums-central.sha256
run tampered-entry $J ./mvnw -B -DskipTests package; cp $SCR/rec.bak $J/.mvn/checksums/checksums-central.sha256
command grep -n '<grpc.version>' $J/pom.xml
sed -i -E 's#<grpc.version>[^<]+</grpc.version>#<grpc.version>1.84.0</grpc.version>#' $J/pom.xml
run unrecorded $J ./mvnw -B -DskipTests package; cp $SCR/pom.bak $J/pom.xml
sed -i 's/^distributionSha256Sum=5/distributionSha256Sum=6/' $J/.mvn/wrapper/maven-wrapper.properties
( cd $J && MAVEN_USER_HOME=$SCR/mvnhome-tamper1 ./mvnw -v > $SCR/t2-dist.log 2>&1; echo "tampered distribution rc=$?"; command grep -m1 -i -o 'Failed to validate Maven distribution SHA-256[^,]*' $SCR/t2-dist.log )
cp $SCR/props.bak $J/.mvn/wrapper/maven-wrapper.properties
sed -i 's/^wrapperSha256Sum=4/wrapperSha256Sum=5/' $J/.mvn/wrapper/maven-wrapper.properties
( cd $J && MAVEN_USER_HOME=$SCR/mvnhome-tamper2 ./mvnw -v > $SCR/t2-wrap.log 2>&1; echo "tampered wrapper rc=$?"; command grep -m1 -i -o '[^.]*wrapperSha256Sum[^.]*' $SCR/t2-wrap.log )
cp $SCR/props.bak $J/.mvn/wrapper/maven-wrapper.properties
git -C $WT status --porcelain -- Iverson.Clients/Java/pom.xml Iverson.Clients/Java/.mvn/checksums Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties
rm -rf $SCR/mvnhome-tamper1 $SCR/mvnhome-tamper2
```

Expected:
- `codeql rc=0`, `harness rc=0`, `test rc=0` (then two per-class `Tests run:` lines, then the summary `Tests run: 227, Failures: 0, Errors: 0, Skipped: 0`), `gitlab-check rc=1` with `NoDataException…` and no `Missing from summaryFile`.
- The tamper line prints `1` (or more); `tampered-entry rc=1` with `trusted checksum mismatch`; `unrecorded rc=1` with `Missing from summaryFile`.
- `tampered distribution rc=1` naming the SHA-256 failure, and `tampered wrapper rc=1` naming `wrapperSha256Sum` (the exact wording is the wrapper's; any refusal that names the sum counts).
- The status command prints only `?? …` lines for the new `.mvn` files (no `M`): every tampered file is restored.

- [ ] **Step 5: The harness test.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "FullyQualifiedName~.DriverRunnerTests." 2>&1 | command grep -E '^(Passed|Failed)!'
```

Expected: `Passed!  - Failed:     0, Passed:    27`. (Mutation check at plan time: `"mvn"` in place of `"Iverson.Clients/Java/mvnw"` fails `RunPhaseAsync_BuildsTheJavaDriverWithTheRepositorysMavenWrapper`.)

- [ ] **Step 6: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Clients/Java/mvnw Iverson.Clients/Java/mvnw.cmd Iverson.Clients/Java/.mvn/wrapper/MavenWrapperDownloader.java \
  Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties Iverson.Clients/Java/.mvn/maven.config \
  Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256 .github/workflows/codeql.yml \
  Iverson.Server/Iverson.ClientConformance/DriverRunner.cs Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
git ls-files -s Iverson.Clients/Java/mvnw | cut -c1-6
git commit -q -m "build java through one pinned maven and verify every artifact against recorded checksums

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat HEAD | tail -1
```

Expected: `100755`; `9 files changed`.

---

### Task 3: The NVD key on GitHub (nvd-update and a keyless scan)

**Spec:** §2.1 (GitHub), §4.5 (the plugin version's three copies). VA 1–6.

**Files:** Modify `.github/workflows/dependency-scan.yml`, `.github/dependabot.yml`.

**Interfaces:** Consumes Task 2's `.mvn/` (maven.config, the record, the wrapper properties) and `mvnw`.

- [ ] **Step 1: Apply the patch.** `nvd-update` runs on push and schedule only, checks out `Iverson.Clients/Java/.mvn/` alone, installs the pinned Maven from the wrapper's `distributionUrl` after its SHA-256 check (unpacked with `jar`, the same as GitLab, whose image has no `unzip`), and runs `update-only` from a directory holding only `.mvn/`. The scan needs `nvd-update` but continues when it is skipped (pull requests), and holds no key.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t3.patch <<'T3_PATCH'
diff --git a/.github/dependabot.yml b/.github/dependabot.yml
index b0acc532..ee003e44 100644
--- a/.github/dependabot.yml
+++ b/.github/dependabot.yml
@@ -32,6 +32,10 @@ updates:
     directory: "/Iverson.Clients/Java"
     schedule:
       interval: "weekly"
+    # CSR round 12 §2.1: version updates wait a week after a release (security updates are not delayed).
+    # Each Maven PR needs the trusted-checksum re-record before it goes green (docs/runbooks/csr12-supply-chain.md).
+    cooldown:
+      default-days: 7
   - package-ecosystem: "pip"
     directory: "/Iverson.Clients/Python"
     schedule:
diff --git a/.github/workflows/dependency-scan.yml b/.github/workflows/dependency-scan.yml
index bce8617f..30e83e46 100644
--- a/.github/workflows/dependency-scan.yml
+++ b/.github/workflows/dependency-scan.yml
@@ -70,39 +70,87 @@ jobs:
       - run: go install golang.org/x/vuln/cmd/govulncheck@v1.7.0
       - run: govulncheck ./...
 
-  owasp-dependency-check-java:
+  # CSR round 12 §2.1 (#10): the only job that holds NVD_API_KEY, and it runs no repository code. Its
+  # checkout holds only Iverson.Clients/Java/.mvn/, which it copies into an otherwise empty directory, so
+  # no pom.xml is present and no project code or build extension can load. It installs the pinned Maven
+  # itself, refused unless its SHA-256 equals the wrapper's distributionSha256Sum, rather than through
+  # mvnw. The .mvn/maven.config it runs beside makes the plugin's own resolution checksum-verified.
+  # Pull requests (Dependabot's included) never run it; they scan keyless from main's cache.
+  nvd-update:
+    if: github.event_name != 'pull_request'
     runs-on: ubuntu-latest
     # A cold NVD download with an API key has taken up to 40 minutes; without a bound a throttled
     # download holds its runner for the 6-hour default.
     timeout-minutes: 60
-    defaults:
-      run:
-        working-directory: Iverson.Clients/Java
     steps:
       - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
+        with:
+          persist-credentials: false
+          sparse-checkout: Iverson.Clients/Java/.mvn/
+          sparse-checkout-cone-mode: false
       - uses: actions/setup-java@de7274f081f381c8f8158605e0321c36c376e2e6 # v6.0.1
         with:
           java-version: "21"
           distribution: "temurin"
-      # The NVD database lives under the local Maven repository. Restore the newest copy so the check
-      # fetches only what changed since; PRs (Dependabot's included) can read main's caches.
+      # The NVD database lives under the local Maven repository. Restore the newest copy so the update
+      # fetches only what changed since.
       - uses: actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0
         with:
           path: ~/.m2/repository/org/owasp/dependency-check-data
           key: nvd-${{ github.run_id }}
           restore-keys: nvd-
-      - run: mvn install -DskipTests
-      - run: mvn org.owasp:dependency-check-maven:check -DfailBuildOnCVSS=7 -DnvdApiKeyEnvironmentVariable=NVD_API_KEY
+      - name: Install the pinned Maven beside a copy of .mvn/ only
+        run: |
+          set -euo pipefail
+          props=Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties
+          url=$(sed -n 's/^distributionUrl=//p' "$props")
+          sum=$(sed -n 's/^distributionSha256Sum=//p' "$props")
+          [[ $url =~ ^https://repo\.maven\.apache\.org/maven2/org/apache/maven/apache-maven/[0-9.]+/apache-maven-[0-9.]+-bin\.zip$ && $sum =~ ^[0-9a-f]{64}$ ]] ||
+            { echo "::error::$props names no Maven Central distribution and SHA-256"; exit 1; }
+          curl -fsSL -o "$RUNNER_TEMP/maven.zip" "$url"
+          echo "$sum  $RUNNER_TEMP/maven.zip" | sha256sum -c -
+          # jar, not unzip: the GitLab job's image has no unzip, and both jobs install Maven the same way.
+          mkdir "$RUNNER_TEMP/maven" && (cd "$RUNNER_TEMP/maven" && jar xf ../maven.zip)
+          echo "MVN=$RUNNER_TEMP/maven/$(basename "$url" -bin.zip)/bin/mvn" >> "$GITHUB_ENV"
+          mkdir "$RUNNER_TEMP/nvd" && cp -R Iverson.Clients/Java/.mvn "$RUNNER_TEMP/nvd/"
+      - name: Update the NVD database
+        working-directory: ${{ runner.temp }}/nvd
         env:
           NVD_API_KEY: ${{ secrets.NVD_API_KEY }}
-      # Only main saves, so PR runs do not fill the repository's cache quota with copies of the database.
-      # It saves even when the check fails on a finding, because the updated database is still good.
-      - if: always() && github.ref == 'refs/heads/main' && hashFiles('Iverson.Clients/Java/target/dependency-check-report.html') != ''
-        uses: actions/cache/save@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0
+        # The plugin version is the pom's; ci/pins.py consistency keeps the three copies equal.
+        run: sh "$MVN" -B org.owasp:dependency-check-maven:13.0.0:update-only -DnvdApiKeyEnvironmentVariable=NVD_API_KEY
+      - uses: actions/cache/save@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0
         with:
           path: ~/.m2/repository/org/owasp/dependency-check-data
           key: nvd-${{ github.run_id }}
 
+  # Keyless (CSR round 12 §2.1): it builds the project and scans against the database nvd-update keeps,
+  # never updating it, so it holds no key on any event. On pull requests nvd-update is skipped, and a
+  # skipped `needs` job skips its dependants unless their `if` continues, which this one does. A missing
+  # database fails the check loudly (NoDataException).
+  owasp-dependency-check-java:
+    needs: nvd-update
+    if: ${{ !cancelled() && (needs.nvd-update.result == 'success' || needs.nvd-update.result == 'skipped') }}
+    runs-on: ubuntu-latest
+    timeout-minutes: 60
+    defaults:
+      run:
+        working-directory: Iverson.Clients/Java
+    steps:
+      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
+      - uses: actions/setup-java@de7274f081f381c8f8158605e0321c36c376e2e6 # v6.0.1
+        with:
+          java-version: "21"
+          distribution: "temurin"
+      # PRs (Dependabot's included) can read main's caches.
+      - uses: actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0
+        with:
+          path: ~/.m2/repository/org/owasp/dependency-check-data
+          key: nvd-${{ github.run_id }}
+          restore-keys: nvd-
+      - run: ./mvnw -B install -DskipTests
+      - run: ./mvnw -B org.owasp:dependency-check-maven:check -DfailBuildOnCVSS=7 -DautoUpdate=false
+
   pip-audit-python-sdk:
     runs-on: ubuntu-latest
     defaults:
T3_PATCH
cd $WT && git apply --check $SCR/t3.patch && git apply $SCR/t3.patch && git status --short
````

- [ ] **Step 2: Run the install step and the update goal as the runner would.** `MVN` comes from the step; the dead NVD endpoint stops the goal right after resolution.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/dependency-scan.yml')); j=d['jobs']; print(list(j)); print(j['nvd-update']['if']); print(j['owasp-dependency-check-java']['needs'], j['owasp-dependency-check-java']['if']); print([s.get('env') for s in j['owasp-dependency-check-java']['steps'] if s.get('env')])"
python3 -c "import yaml; print(yaml.safe_load(open('.github/workflows/dependency-scan.yml'))['jobs']['nvd-update']['steps'][3]['run'])" > $SCR/t3-step.sh
export RUNNER_TEMP=$SCR/t3-rt GITHUB_ENV=$SCR/t3-rt.env; rm -rf $RUNNER_TEMP $GITHUB_ENV; mkdir -p $RUNNER_TEMP
bash -e $SCR/t3-step.sh && cat $GITHUB_ENV && ls $RUNNER_TEMP/nvd/.mvn
MVN=$(sed -n 's/^MVN=//p' $GITHUB_ENV); cd $RUNNER_TEMP/nvd; rm -rf $SCR/m2-t3
timeout 300 sh "$MVN" -B -Dmaven.repo.local=$SCR/m2-t3 org.owasp:dependency-check-maven:13.0.0:update-only -DnvdApiKeyEnvironmentVariable=NVD_API_KEY -DnvdApiEndpoint=http://127.0.0.1:1/ > $SCR/t3-update.log 2>&1
command grep -m2 -E 'Stub Project|NVD API request failures|Missing from summaryFile|mismatch' $SCR/t3-update.log
```

Expected: the job list has `nvd-update` before `owasp-dependency-check-java`; `github.event_name != 'pull_request'`; `nvd-update` and `${{ !cancelled() && (needs.nvd-update.result == 'success' || needs.nvd-update.result == 'skipped') }}`; `[]` (no step of the scan has an `env`); `…/maven.zip: OK`; `MVN=…/apache-maven-3.9.16/bin/mvn`; `checksums maven.config wrapper`; `Building Maven Stub Project (No POM) 1` and an NVD retry line, with no checksum error.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add .github/workflows/dependency-scan.yml .github/dependabot.yml
git commit -q -m "keep the nvd key in a job that runs no repository code and scan keyless from its cache

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The NVD key on GitLab, and runbook §5

**Spec:** §2.1 (GitLab, runbook §5). VA 47, 50, 51, 53.

**Files:** Modify `.gitlab-ci.yml`, `docs/runbooks/csr11-clients-tooling-ci.md` (§5).

- [ ] **Step 1: Apply the patch.** `nvd-update` (schedule only, `environment: { name: nvd-update, action: prepare }`, cache push) and the keyless scan (`needs: [nvd-update]`, cache pull, the absence check as its first line, `unset MAVEN_CONFIG`, `./mvnw -B install -DskipTests`, then the keyless `./mvnw` check, as on GitHub). Runbook §5 now has one definition of the key.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t4.patch <<'T4_PATCH'
diff --git a/.gitlab-ci.yml b/.gitlab-ci.yml
index 4b69dd2c..1169f4ca 100644
--- a/.gitlab-ci.yml
+++ b/.gitlab-ci.yml
@@ -179,22 +179,68 @@ govulncheck:
     - go install golang.org/x/vuln/cmd/govulncheck@v1.7.0
     - govulncheck ./...
 
+# CSR round 12 §2.1 (#10): the only job that receives NVD_API_KEY, and it runs no repository code. The
+# key is a project variable scoped to the nvd-update environment (docs/runbooks/csr11-clients-tooling-ci.md
+# §5); `action: prepare` gives this job that environment's variables without creating a deployment. It
+# installs the pinned Maven itself, refused unless its SHA-256 equals the wrapper's distributionSha256Sum,
+# and runs it from a directory holding only a copy of Iverson.Clients/Java/.mvn/, whose maven.config makes
+# the plugin's own resolution checksum-verified. It pushes the database to the cache the scan pulls.
+nvd-update:
+  stage: dependency-scan
+  image: maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
+  rules:
+    - if: '$CI_PIPELINE_SOURCE == "schedule"'
+  environment:
+    name: nvd-update
+    action: prepare
+  cache:
+    key: nvd-data
+    paths: [.nvd-data]
+    policy: push
+  script:
+    - |
+      set -eu
+      props=Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties
+      url=$(sed -n 's/^distributionUrl=//p' "$props")
+      sum=$(sed -n 's/^distributionSha256Sum=//p' "$props")
+      case "$url" in
+        https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/*-bin.zip) ;;
+        *) echo "$props names no Maven Central distribution"; exit 1 ;;
+      esac
+      curl -fsSL -o /tmp/maven.zip "$url"
+      echo "$sum  /tmp/maven.zip" | sha256sum -c -
+      # jar, not unzip: this image has no unzip.
+      mkdir /tmp/maven && (cd /tmp/maven && jar xf /tmp/maven.zip)
+      mkdir /tmp/nvd && cp -R Iverson.Clients/Java/.mvn /tmp/nvd/
+      cd /tmp/nvd
+      # The plugin version is the pom's; ci/pins.py consistency keeps the three copies equal.
+      sh "/tmp/maven/$(basename "$url" -bin.zip)/bin/mvn" -B org.owasp:dependency-check-maven:13.0.0:update-only \
+        -DnvdApiKeyEnvironmentVariable=NVD_API_KEY -DdataDirectory="$CI_PROJECT_DIR/.nvd-data"
+
+# Keyless (CSR round 12 §2.1): it scans against the database nvd-update pushed, never updating it. It
+# never runs in a merge-request pipeline (these rules replace .rules-dependency-scan's, which also admit
+# merge requests). Its first line fails the job if NVD_API_KEY reached it at all, which proves the key's
+# environment scoping on every scheduled run. A missing database fails the check loudly (NoDataException).
 owasp-dependency-check-java:
   stage: dependency-scan
   image: maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
-  # CSR round-11 #19: the only job that reads NVD_API_KEY, so it never runs in a merge-request
-  # pipeline (these rules replace .rules-dependency-scan's, which also admit merge requests). Job
-  # rules decide which jobs run, not which variables a pipeline receives: NVD_API_KEY must also be a
-  # Protected (or pipeline-schedule) variable (docs/runbooks/csr11-clients-tooling-ci.md).
   rules:
     - if: '$CI_PIPELINE_SOURCE == "merge_request_event"'
       when: never
     - if: '$CI_PIPELINE_SOURCE == "schedule"'
-  variables:
-    NVD_API_KEY: $NVD_API_KEY
+  needs: [nvd-update]
+  cache:
+    key: nvd-data
+    paths: [.nvd-data]
+    policy: pull
   script:
+    - test -z "${NVD_API_KEY+x}" || { echo "NVD_API_KEY must be scoped to the nvd-update environment"; exit 1; }
+    # The image sets MAVEN_CONFIG=/root/.m2, which mvnw would pass to Maven as arguments; only the image's
+    # entrypoint unsets it, and not every executor runs entrypoints.
+    - unset MAVEN_CONFIG
     - cd Iverson.Clients/Java
-    - mvn org.owasp:dependency-check-maven:check -DfailBuildOnCVSS=7 -DnvdApiKeyEnvironmentVariable=NVD_API_KEY
+    - ./mvnw -B install -DskipTests
+    - ./mvnw -B org.owasp:dependency-check-maven:check -DfailBuildOnCVSS=7 -DautoUpdate=false -DdataDirectory="$CI_PROJECT_DIR/.nvd-data"
 
 pip-audit-python-sdk:
   stage: dependency-scan
diff --git a/docs/runbooks/csr11-clients-tooling-ci.md b/docs/runbooks/csr11-clients-tooling-ci.md
index 954a438c..4e123d4f 100644
--- a/docs/runbooks/csr11-clients-tooling-ci.md
+++ b/docs/runbooks/csr11-clients-tooling-ci.md
@@ -175,17 +175,23 @@ gh api repos/counsellorben/Iverson/branches/main/protection --jq '.required_stat
 
 ## 5. GitLab: `NVD_API_KEY`
 
-The OWASP dependency-check job is the only GitLab job that uses `NVD_API_KEY`, and it no longer runs
-in merge-request pipelines. Job rules do not limit a project-level CI/CD variable: such a variable
-reaches every job of every pipeline, merge-request pipelines included. So define `NVD_API_KEY` in
-one of two ways:
-
-- as a **Protected** variable (project **Settings**, then **CI/CD**, then **Variables**; select
-  **Protect variable**, and mask it), which only pipelines on protected branches and tags receive;
-- or as a variable of a pipeline schedule (**Build**, then **Pipeline schedules**).
-
-Remove any unprotected project-level `NVD_API_KEY`. GitLab's variable semantics are described at
-https://docs.gitlab.com/ci/variables/.
+Only the `nvd-update` job reads `NVD_API_KEY` (CSR round 12 §2.1). It runs only in scheduled
+pipelines, runs no repository code, and receives the key through its `nvd-update` environment. The
+scan job, `owasp-dependency-check-java`, runs keyless from the database `nvd-update` caches. Its first
+line fails the job if the key reached it.
+
+Define the key once, as a project variable `NVD_API_KEY` (project **Settings**, then **CI/CD**, then
+**Variables**) with **Protect variable** and **Mask variable** selected and **Environment scope** set
+to `nvd-update`.
+
+Delete every other definition of `NVD_API_KEY`: an unscoped or differently scoped project variable, a
+group-level variable, and a pipeline-schedule variable. A pipeline-schedule variable reaches every job
+of its pipeline, so it is not an option. The scoped variable reaches only a job whose environment is
+`nvd-update`, which no job had before CSR round 12, so define it after that change is on `main`.
+GitLab's variable semantics are described at https://docs.gitlab.com/ci/variables/.
+
+The first scheduled pipeline afterwards is the check: `owasp-dependency-check-java` passes its first
+line and finds the database (`docs/runbooks/csr12-supply-chain.md`, rollout step 2).
 
 ## 6. `IVERSON_ACTING_USER_PASSWORD`
 
T4_PATCH
cd $WT && git apply --check $SCR/t4.patch && git apply $SCR/t4.patch && git status --short
````

- [ ] **Step 2: Run `nvd-update`'s script with `sh` in the job's image.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
python3 -c "
import yaml; d=yaml.safe_load(open('.gitlab-ci.yml'))
for j in ('nvd-update','owasp-dependency-check-java'): print(j, d[j]['rules'], d[j].get('needs'), d[j]['cache'], d[j].get('environment'), d[j].get('variables'))
print(d['owasp-dependency-check-java']['script'][0])"
python3 -c "import yaml; print(yaml.safe_load(open('.gitlab-ci.yml'))['nvd-update']['script'][0])" > $SCR/t4-nvd.sh
podman image exists docker.io/library/maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320 && T4_HAD_MAVEN=1
timeout 900 podman run --rm -v $WT:/builds/iv:ro,z -v $SCR/t4-nvd.sh:/gl.sh:ro,z -e CI_PROJECT_DIR=/tmp/proj \
  -e MAVEN_OPTS=-DnvdApiEndpoint=http://127.0.0.1:1/ -w /builds/iv --entrypoint sh \
  docker.io/library/maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320 \
  -c 'mkdir -p /tmp/proj; timeout 240 sh /gl.sh' > $SCR/t4-nvd.log 2>&1
command grep -m3 -E 'maven.zip: OK|Stub Project|NVD API request failures|Missing from summaryFile|mismatch' $SCR/t4-nvd.log
[ -n "$T4_HAD_MAVEN" ] || podman rmi docker.io/library/maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320 >/dev/null 2>&1; true
```

Expected: `nvd-update [{'if': '$CI_PIPELINE_SOURCE == "schedule"'}] None {'key': 'nvd-data', 'paths': ['.nvd-data'], 'policy': 'push'} {'name': 'nvd-update', 'action': 'prepare'} None`; the scan's rules (merge requests never, schedules), `['nvd-update']`, `policy: pull`, no environment, no variables; its first script line is the absence check; then `/tmp/maven.zip: OK`, `Building Maven Stub Project (No POM) 1`, an NVD retry line, and no checksum error.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add .gitlab-ci.yml docs/runbooks/csr11-clients-tooling-ci.md
git commit -q -m "scope the gitlab nvd key to a job that runs no repository code and scan keyless

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Publish residuals, and runbook §2

**Spec:** §2.3 items 1–4. VA 11, 12, 13, 54.

**Files:** Modify `.github/workflows/publish-images.yml`, `docs/runbooks/csr11-clients-tooling-ci.md` (§2).

- [ ] **Step 1: Apply the patch.** After the third denial the lookup asks `GET /users/counsellorben/packages/container/<name>` with the job's token: 404 builds, anything else fails. `bind=release` adds `--certificate-github-workflow-sha "$GITHUB_SHA"`. Runbook §2 gets a command pair per tag kind (release tags bound to `git rev-list -n1 vX.Y.Z`), the subject-name command, and the clouds' admission check in place of "The clusters do not check signatures at admission".

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t5.patch <<'T5_PATCH'
diff --git a/.github/workflows/publish-images.yml b/.github/workflows/publish-images.yml
index bcf04c2e..3ec06753 100644
--- a/.github/workflows/publish-images.yml
+++ b/.github/workflows/publish-images.yml
@@ -123,6 +123,8 @@ jobs:
         id: published
         env:
           REF: ${{ matrix.image }}:${{ matrix.tag }}
+          NAME: ${{ matrix.name }}
+          GH_TOKEN: ${{ github.token }}
         run: |
           set -euo pipefail
           # A published tag never moves. Only the registry's "not found" or "denied" answers count
@@ -137,7 +139,7 @@ jobs:
                  grep -Eq 'manifest unknown|name unknown|MANIFEST_UNKNOWN|NAME_UNKNOWN' "$err"; then
               state=absent; break
             # GHCR may answer a lookup in a package that does not exist yet with denied/unauthorized;
-            # after retries it counts as absent, since a real permission problem still fails at the push.
+            # after retries the packages API decides whether the package exists (below).
             elif grep -Eq 'denied:|unauthorized:|401 Unauthorized|403 Forbidden' "$err"; then
               state=denied
               if [ "$try" -lt 3 ]; then sleep 5; fi
@@ -156,7 +158,16 @@ jobs:
             absent) echo "$REF is not published yet; building it" ;;
             denied)
               cat "$err" >&2
-              echo "$REF is not visible to this run (the registry denied the lookup $try times); treating it as not published and building it" ;;
+              # CSR round 12 §2.3: GHCR denies lookups in a package that does not exist yet, so only a
+              # package the packages API does not know counts as unpublished. If the package exists, the
+              # denial is a real failure, and building could move a tag that may already exist.
+              status=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $GH_TOKEN" \
+                -H 'Accept: application/vnd.github+json' "https://api.github.com/users/counsellorben/packages/container/$NAME")
+              case $status in
+                404) echo "$REF is not visible to this run (the registry denied the lookup $try times) and the package $NAME does not exist yet; building it" ;;
+                200) echo "::error::the registry denied the lookup of $REF $try times, but the package $NAME exists, so this run cannot tell whether the tag is published; not building"; exit 1 ;;
+                *) echo "::error::the registry denied the lookup of $REF $try times and the packages API answered $status for $NAME; not building"; exit 1 ;;
+              esac ;;
           esac
       - uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2
         if: steps.published.outputs.digest != ''
@@ -189,7 +200,8 @@ jobs:
               cert_flags=(--certificate-identity-regexp "$IDENTITY" --certificate-github-workflow-sha "$TAG") ;;
             release)
               [[ $TAG =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "::error::$TAG is not X.Y.Z"; exit 1; }
-              cert_flags=(--certificate-identity "$WORKFLOW@refs/tags/v$TAG") ;;
+              # CSR round 12 §2.3: bound to the tagged commit too (on a tag run GITHUB_SHA is that commit).
+              cert_flags=(--certificate-identity "$WORKFLOW@refs/tags/v$TAG" --certificate-github-workflow-sha "$GITHUB_SHA") ;;
             content)
               # Accepted residual: any digest this workflow signed under a content-derived tag was
               # built from the same model inputs, so no commit or release binding is required.
diff --git a/docs/runbooks/csr11-clients-tooling-ci.md b/docs/runbooks/csr11-clients-tooling-ci.md
index 4e123d4f..a82dc830 100644
--- a/docs/runbooks/csr11-clients-tooling-ci.md
+++ b/docs/runbooks/csr11-clients-tooling-ci.md
@@ -31,21 +31,53 @@ it. The `org.opencontainers.image.source` label links each package to the Iverso
 
 ## 2. Verifying an image
 
-Use cosign 3.x, the version the workflow signs with. Both commands must succeed for every image
-you deploy, from any machine with no GHCR login. A failure on a package that is still private is
-an authorization error, not a verification failure.
+Use cosign 3.x, the version the workflow signs with. Both commands of a pair must succeed for every
+image you deploy, from any machine with no GHCR login. A failure on a package that is still private
+is an authorization error, not a verification failure. Verify as strictly as the workflow does
+(CSR round 12 §2.3): pick the pair for the tag's kind.
+
+A commit tag (`main`'s 40-hex SHA) must have been signed by the run for that commit:
 
 ```bash
-cosign verify ghcr.io/counsellorben/iverson-api:<tag> \
+cosign verify ghcr.io/counsellorben/iverson-api:<sha> \
   --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
+  --certificate-github-workflow-sha <sha> \
   --certificate-oidc-issuer https://token.actions.githubusercontent.com
 
-cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:<tag> \
+cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:<sha> \
   --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
+  --certificate-github-workflow-sha <sha> \
+  --certificate-oidc-issuer https://token.actions.githubusercontent.com
+```
+
+A release tag (`X.Y.Z`) must have been signed by the run on `refs/tags/vX.Y.Z`, at the commit that
+tag names (run from a clone with the tags fetched):
+
+```bash
+cosign verify ghcr.io/counsellorben/iverson-api:X.Y.Z \
+  --certificate-identity https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@refs/tags/vX.Y.Z \
+  --certificate-github-workflow-sha "$(git rev-list -n1 vX.Y.Z)" \
   --certificate-oidc-issuer https://token.actions.githubusercontent.com
+
+cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:X.Y.Z \
+  --certificate-identity https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@refs/tags/vX.Y.Z \
+  --certificate-github-workflow-sha "$(git rev-list -n1 vX.Y.Z)" \
+  --certificate-oidc-issuer https://token.actions.githubusercontent.com
+```
+
+The attestation names the image it was made for. Print that name and check that it is the image
+you verified (`ghcr.io/counsellorben/iverson-api` here), so another first-party image's digest
+pushed under this name is caught:
+
+```bash
+cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:<tag> \
+  --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
+  --certificate-oidc-issuer https://token.actions.githubusercontent.com 2>/dev/null \
+  | jq -r '.payload | @base64d | fromjson | .subject[0].name'
 ```
 
-Run the same pair for `iverson-admin-ui:<tag>` and for the two model images under the tags the
+Run the same commands for `iverson-admin-ui:<tag>`. The two model images keep the general identity
+(the commands without `--certificate-github-workflow-sha`, accepted in round 11), under the tags the
 chart renders:
 
 ```bash
@@ -59,7 +91,10 @@ A pass means two things. The digest the tag names was signed by `publish-images.
 the same workflow. Signing writes an entry to the public Rekor transparency log, which records
 the repository, workflow and ref.
 
-The clusters do not check signatures at admission. Verification is the operator's check before a
+The cloud clusters (AWS, Azure, GCP) also check the attestation at admission: their `iverson`
+namespace admits a first-party image only when this workflow attested that digest under that image's
+own name (CSR round 12 §3; `docs/runbooks/csr12-supply-chain.md`). kind, local and compose do not
+check (they run unsigned local builds), so there these commands are the operator's check before a
 deploy.
 
 Each run builds and pushes an image only when its tag does not exist yet. A separate
@@ -71,7 +106,7 @@ of both; change them together). For `iverson-api` and `iverson-admin-ui` the sig
 belong to the tag's own build: on a commit-SHA tag the commands add
 `--certificate-github-workflow-sha <tag>`, and on an `X.Y.Z` tag they require exactly
 `--certificate-identity https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@refs/tags/vX.Y.Z`
-in place of the regexp. The model images keep the general identity: their tags are derived from
+in place of the regexp, with `--certificate-github-workflow-sha` set to the tagged commit. The model images keep the general identity: their tags are derived from
 the model inputs, so any digest this workflow signed under such a tag was built from the same
 inputs (an accepted residual). If both commands pass, the image was published by this workflow:
 the log carries a notice and nothing else happens for it. If either fails, the job fails with an
T5_PATCH
cd $WT && git apply --check $SCR/t5.patch && git apply $SCR/t5.patch && git status --short
````

- [ ] **Step 2: Check the lookup step against fake registry and API answers.** The script runs the step's own text with `docker`, `curl` and `sleep` faked: three denials, then each packages-API answer.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t5_lookup.sh <<'T5LOOKUP'
#!/usr/bin/env bash
# Runs publish-images.yml's "Check whether the tag is published" step against fake docker/curl/sleep:
# three registry denials, then each packages-API answer in turn. Prints one line per case.
set -u
W=$1; T=$(mktemp -d)
python3 -c "import yaml,sys; s=[x for x in yaml.safe_load(open(sys.argv[1]))['jobs']['publish']['steps'] if x.get('id')=='published'][0]; print(s['run'])" "$W/.github/workflows/publish-images.yml" > $T/step.sh
mkdir $T/bin
printf '#!/bin/sh\necho "ERROR: denied: requested access to the resource is denied" >&2\nexit 1\n' > $T/bin/docker
printf '#!/bin/sh\nprintf %%s "$FAKE_STATUS"\n' > $T/bin/curl
printf '#!/bin/sh\nexit 0\n' > $T/bin/sleep
chmod +x $T/bin/*
for st in 404 200 500 000; do
  out=$(PATH=$T/bin:$PATH FAKE_STATUS=$st REF=ghcr.io/counsellorben/iverson-api:abc NAME=iverson-api GH_TOKEN=x RUNNER_TEMP=$T GITHUB_OUTPUT=$T/out bash $T/step.sh 2>/dev/null); rc=$?
  echo "api=$st rc=$rc $(printf '%s' "$out" | tail -1 | cut -c1-200)"
done
rm -rf $T
T5LOOKUP
bash $SCR/t5_lookup.sh $WT
bash $SCR/t5_lookup.sh /home/ben/repositories/Iverson | cut -c1-12
command grep -n 'certificate-github-workflow-sha "\$GITHUB_SHA"' $WT/.github/workflows/publish-images.yml
```

Expected: on the branch `api=404 rc=0 … does not exist yet; building it`, `api=200 rc=1`, `api=500 rc=1`, `api=000 rc=1`; on `main` all four `rc=0` (the old rule, the control); one line binding the release check to `$GITHUB_SHA`.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add .github/workflows/publish-images.yml docs/runbooks/csr11-clients-tooling-ci.md
git commit -q -m "build after repeated registry denials only for an absent package and bind release signatures to their commit

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The release-tag ruleset script (not run)

**Spec:** §2.2 (tag ruleset). VA 8.

**Files:** Create `Iverson.Server/deploy/scripts/apply-tag-ruleset.sh` (mode 755).

- [ ] **Step 1: Apply the patch.** The style and `--repo`/`--dry-run` flags of `apply-branch-protection.sh`; `--activate` refuses unless the read-back shows `bypass_actors=RepositoryRole:5:always` and `current_user_can_bypass=always` (GitHub does not document the admin role's id).

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t6.patch <<'T6_PATCH'
diff --git a/Iverson.Server/deploy/scripts/apply-tag-ruleset.sh b/Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
new file mode 100755
index 00000000..aba81d00
--- /dev/null
+++ b/Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
@@ -0,0 +1,105 @@
+#!/usr/bin/env bash
+set -euo pipefail
+
+# CSR round 12 §2.2: a tag ruleset so release tags (v*) can be created, moved or deleted only by the
+# repository admin role. Release signatures are bound to their tag (publish-images.yml), so whoever can
+# create a v* tag can have the workflow sign a release.
+#
+# GitHub does not document the numeric id of the built-in admin role, so the ruleset is created with
+# enforcement disabled, read back, and switched to active only with --activate, which refuses unless the
+# read-back shows the bypass actor and that the admin running this script can bypass it
+# (current_user_can_bypass = always). Check the bypass list in the repository's Settings, then Rules,
+# then Rulesets, too: it must read "Repository admin" (docs/runbooks/csr12-supply-chain.md).
+#
+# Usage: apply-tag-ruleset.sh [--repo owner/name] [--dry-run] [--activate]
+#   --repo      defaults to counsellorben/Iverson
+#   --dry-run   prints the request body and makes no API call
+#   --activate  switches the existing ruleset to active after checking its read-back
+
+usage() { echo "usage: ${0##*/} [--repo owner/name] [--dry-run] [--activate]" >&2; }
+
+repo=counsellorben/Iverson
+dry_run=0
+activate=0
+while [[ $# -gt 0 ]]; do
+  case "$1" in
+    --repo)
+      [[ $# -ge 2 ]] || { usage; exit 2; }
+      repo=$2
+      shift 2
+      ;;
+    --dry-run)
+      dry_run=1
+      shift
+      ;;
+    --activate)
+      activate=1
+      shift
+      ;;
+    -h|--help)
+      usage
+      exit 0
+      ;;
+    *)
+      usage
+      exit 2
+      ;;
+  esac
+done
+[[ $repo =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ && $repo != *..* ]] || { echo "--repo must be owner/name, not '$repo'" >&2; exit 2; }
+
+name=release-tags
+# actor_id 5 is the id commonly given for the admin role. GitHub does not document it, which is why the
+# ruleset starts disabled and --activate checks the read-back.
+body=$(cat <<JSON
+{
+  "name": "${name}",
+  "target": "tag",
+  "enforcement": "disabled",
+  "bypass_actors": [
+    { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" }
+  ],
+  "conditions": {
+    "ref_name": { "include": ["refs/tags/v*"], "exclude": [] }
+  },
+  "rules": [
+    { "type": "creation" },
+    { "type": "update", "parameters": { "update_allows_fetch_and_merge": false } },
+    { "type": "deletion" }
+  ]
+}
+JSON
+)
+
+if [[ $dry_run = 1 ]]; then
+  echo "POST repos/${repo}/rulesets (dry run, nothing sent; --activate later sends {\"enforcement\": \"active\"})" >&2
+  printf '%s\n' "$body"
+  exit 0
+fi
+
+# The id of the ruleset named $name, or nothing.
+id=$(gh api "repos/${repo}/rulesets?per_page=100" --paginate --jq ".[] | select(.name == \"${name}\") | .id")
+[[ $(printf '%s\n' "$id" | command grep -c .) -le 1 ]] || { echo "more than one ruleset is named ${name}: ${id//$'\n'/ }" >&2; exit 1; }
+
+if [[ $activate = 0 ]]; then
+  if [[ -z $id ]]; then
+    id=$(printf '%s\n' "$body" | gh api -X POST "repos/${repo}/rulesets" --input - --jq .id)
+    echo "created ruleset ${name} (id ${id}), enforcement disabled"
+  else
+    echo "ruleset ${name} exists (id ${id}); left unchanged"
+  fi
+else
+  [[ -n $id ]] || { echo "no ruleset named ${name}: run without --activate first" >&2; exit 1; }
+fi
+
+# The read-back: enforcement, the bypass actors, and whether the caller can bypass the ruleset.
+readback=$(gh api "repos/${repo}/rulesets/${id}" --jq '"enforcement=\(.enforcement) current_user_can_bypass=\(.current_user_can_bypass) bypass_actors=\([.bypass_actors[] | "\(.actor_type):\(.actor_id):\(.bypass_mode)"] | join(","))"')
+echo "read back: ${readback}"
+
+if [[ $activate = 1 ]]; then
+  [[ $readback == *"current_user_can_bypass=always "* && $readback == *"bypass_actors=RepositoryRole:5:always"* ]] || {
+    echo "not activating: the read-back must show bypass_actors=RepositoryRole:5:always and current_user_can_bypass=always for the admin running this script" >&2
+    exit 1
+  }
+  gh api -X PUT "repos/${repo}/rulesets/${id}" -f enforcement=active --jq '"enforcement=\(.enforcement)"'
+fi
T6_PATCH
cd $WT && git apply --check $SCR/t6.patch && git apply $SCR/t6.patch && git status --short
````

- [ ] **Step 2: Check the dry run and each branch against a fake `gh`.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
F=$WT/Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
bash -n $F && echo syntax-ok; test -x $F && echo executable
$F --dry-run 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["target"], d["enforcement"], d["bypass_actors"], d["conditions"], [r["type"] for r in d["rules"]], d["rules"][1]["parameters"])'
T=$(mktemp -d); cat > $T/gh <<'T6GH'
#!/bin/bash
# fake gh: the ruleset's state in $FAKE_STATE (none, disabled-ok, disabled-never)
echo "gh $*" >> "$FAKE_LOG"
case "$*" in
  *"rulesets?per_page=100"*) [[ $FAKE_STATE == none ]] || echo 77 ;;
  *"-X POST"*) cat > /dev/null; echo 77 ;;
  *"-X PUT"*) echo "enforcement=active" ;;
  *"rulesets/77"*) case $FAKE_STATE in
      disabled-never) echo "enforcement=disabled current_user_can_bypass=never bypass_actors=RepositoryRole:5:always" ;;
      *) echo "enforcement=disabled current_user_can_bypass=always bypass_actors=RepositoryRole:5:always" ;; esac ;;
esac
T6GH
chmod +x $T/gh
for c in "none:" "disabled-ok:" "disabled-ok:--activate" "disabled-never:--activate" "none:--activate"; do st=${c%%:*}; a=${c#*:}; : > $T/log
  out=$(PATH=$T:$PATH FAKE_STATE=$st FAKE_LOG=$T/log $F $a 2>&1); echo "[$st $a] rc=$? put=$(command grep -c 'X PUT' $T/log) post=$(command grep -c 'X POST' $T/log)"; done; rm -rf $T
```

Expected: `syntax-ok`, `executable`; `tag disabled [{'actor_id': 5, 'actor_type': 'RepositoryRole', 'bypass_mode': 'always'}] {'ref_name': {'include': ['refs/tags/v*'], 'exclude': []}} ['creation', 'update', 'deletion'] {'update_allows_fetch_and_merge': False}`; then `[none ] rc=0 put=0 post=1`, `[disabled-ok ] rc=0 put=0 post=0`, `[disabled-ok --activate] rc=0 put=1 post=0`, `[disabled-never --activate] rc=1 put=0 post=0`, `[none --activate] rc=1 put=0 post=0`.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
git ls-files -s Iverson.Server/deploy/scripts/apply-tag-ruleset.sh | cut -c1-6
git commit -q -m "add a script for the admin-only release tag ruleset

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Expected: `100755`.

---

### Task 7: Admission verification on the clouds

**Spec:** §3 (Install, Behaviour, the identity check of §7), Choices 3–5. VA 14–25, 46.

**Files:**
- Create: `Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/{Chart.yaml,values.yaml,templates/clusterimagepolicies.yaml}`.
- Modify: `…/modules/operators/main.tf`, `…/operators/versions/Chart.yaml`, `…/charts/operator-namespace-policies/{values.yaml,templates/networkpolicies.yaml}`, `…/modules/cluster-gcp/main.tf`, `.github/workflows/deploy-validate.yml`, `.gitlab-ci.yml`.

**Interfaces:** Produces the `policy-controller` stub entry and `main.tf`'s `v0.15.1@sha256:…` (pin table, Task 11); the `image-policies` values' `subjectRegExp` (the identity check).

- [ ] **Step 1: Apply the patch.** The namespace `cosign-system`, `helm_release.policy_controller` (chart from the stub, engine `v0.15.1@sha256:0492bb26…`, CPU limit 1000m, `no-match-policy: allow`), `helm_release.image_policies` after it, the `iverson` namespace label `policy.sigstore.dev/include=true`, `cosignSystem = true` for the operator-namespace chart (its default-deny plus the webhook's ingress on 8443 from the API server and egress on 443), GKE's firewall port 8443, and CI: `helm lint` of the new chart on both systems, the cloud operator-namespace renders with `cosignSystem=true`, and a deploy-validate step that fails unless the publish identity is the same in `publish-images.yml`, the runbook and the chart.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t7.patch <<'T7_PATCH'
diff --git a/.github/workflows/deploy-validate.yml b/.github/workflows/deploy-validate.yml
index 974fee56..f905be3b 100644
--- a/.github/workflows/deploy-validate.yml
+++ b/.github/workflows/deploy-validate.yml
@@ -49,6 +49,24 @@ jobs:
           C=Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies
           helm lint "$C" -f "$C/values-kind.yaml"
 
+      # CSR round 12 §3: the clouds' ClusterImagePolicies. kubeconform has no schema for their CRD, so the
+      # lint (which renders them) is the CI check; the live check is docs/runbooks/csr12-supply-chain.md's.
+      - name: helm lint the image-policies chart
+        run: helm lint Iverson.Server/deploy/terraform/modules/operators/charts/image-policies
+
+      # CSR round 12 §3: the publish workflow, the runbook's verification commands and the clusters'
+      # admission policies must trust the same signing identity.
+      - name: The publish identity is the same in the workflow, the runbook and the image policies
+        run: |
+          set -euo pipefail
+          wf=$(sed -n "s/^ *IDENTITY: '\(.*\)'$/\1/p" .github/workflows/publish-images.yml)
+          ip=$(sed -n "s/^ *subjectRegExp: '\(.*\)'$/\1/p" Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/values.yaml)
+          rb=$(sed -n "s/.*--certificate-identity-regexp '\([^']*\)'.*/\1/p" docs/runbooks/csr11-clients-tooling-ci.md | sort -u)
+          echo "workflow:      $wf"
+          echo "image-policies: $ip"
+          echo "runbook:       $rb"
+          [ -n "$wf" ] && [ "$ip" = "$wf" ] && [ "$rb" = "$wf" ] || { echo "::error::the three identities differ"; exit 1; }
+
   helm-template-kubeconform:
     runs-on: ubuntu-latest
     steps:
@@ -93,8 +111,8 @@ jobs:
             Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy \
             | kubeconform -kubernetes-version 1.35.0 -summary
 
-      # The operator-namespace policy chart renders NetworkPolicies, kind's set and the clouds' (kind=false)
-      # with the same addresses, and the CNPG operator's PeerAuthentication (CSR round 12 §4.1), an Istio
+      # The operator-namespace policy chart renders NetworkPolicies, kind's set and the clouds' (kind=false,
+      # with cosign-system) with the same addresses, and the CNPG operator's PeerAuthentication (CSR round 12 §4.1), an Istio
       # kind kubeconform has no schema for: that kind alone is skipped, so a missing schema for anything
       # else still fails.
       - name: helm template | kubeconform the operator-namespace policy chart
@@ -102,7 +120,7 @@ jobs:
           set -e
           C=Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies
           helm template operator-namespace-policies "$C" -f "$C/values-kind.yaml" | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
-          helm template operator-namespace-policies "$C" -f "$C/values-kind.yaml" --set kind=false --set cnpgWebhookPermissive=true | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
+          helm template operator-namespace-policies "$C" -f "$C/values-kind.yaml" --set kind=false --set cnpgWebhookPermissive=true --set cosignSystem=true | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
 
   security-audit:
     runs-on: ubuntu-latest
diff --git a/.gitlab-ci.yml b/.gitlab-ci.yml
index 1169f4ca..4cf605bb 100644
--- a/.gitlab-ci.yml
+++ b/.gitlab-ci.yml
@@ -49,6 +49,8 @@ helm-lint:
       done
     - helm lint Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy
     - helm lint Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies -f Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values-kind.yaml
+    # CSR round 12 §3: kubeconform has no schema for the ClusterImagePolicy CRD, so the lint is the check.
+    - helm lint Iverson.Server/deploy/terraform/modules/operators/charts/image-policies
 
 helm-template-kubeconform:
   stage: validate
@@ -76,10 +78,10 @@ helm-template-kubeconform:
     # a misspelled ValidatingAdmissionPolicy field. No -ignore-missing-schemas: this
     # chart renders only core kinds, so a missing schema means something is wrong.
     - helm template pvc-storageclass-policy Iverson.Server/deploy/terraform/modules/operators/charts/pvc-storageclass-policy | kubeconform -kubernetes-version 1.35.0 -summary
-    # The operator-namespace policy chart: kind's set, and the clouds' (kind=false) with the same addresses.
+    # The operator-namespace policy chart: kind's set, and the clouds' (kind=false, with cosign-system) with the same addresses.
     # Its CNPG PeerAuthentication (CSR round 12 §4.1) is an Istio kind with no schema here: only it is skipped.
     - helm template operator-namespace-policies Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies -f Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values-kind.yaml | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
-    - helm template operator-namespace-policies Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies -f Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values-kind.yaml --set kind=false --set cnpgWebhookPermissive=true | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
+    - helm template operator-namespace-policies Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies -f Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values-kind.yaml --set kind=false --set cnpgWebhookPermissive=true --set cosignSystem=true | kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication
 
 security-audit:
   stage: validate
diff --git a/Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf b/Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf
index 85082227..5cdc07ff 100644
--- a/Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf
+++ b/Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf
@@ -211,8 +211,8 @@ resource "google_container_cluster" "this" {
 }
 
 # CSR round 12 §3.4: the private cluster's control plane reaches the nodes only on the ports GKE's own
-# firewall rule opens, so the admission webhooks on other ports (CNPG 9443, istiod 15017) are
-# unreachable without this rule.
+# firewall rule opens, so the admission webhooks on other ports (CNPG 9443, istiod 15017 and, CSR round 12
+# §3, the policy-controller's 8443) are unreachable without this rule.
 resource "google_compute_firewall" "control_plane_webhooks" {
   name                    = "${var.cluster_name}-control-plane-webhooks"
   network                 = google_compute_network.this.name
@@ -222,7 +222,7 @@ resource "google_compute_firewall" "control_plane_webhooks" {
 
   allow {
     protocol = "tcp"
-    ports    = ["9443", "15017"]
+    ports    = ["9443", "15017", "8443"]
   }
 }
 
diff --git a/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/Chart.yaml b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/Chart.yaml
new file mode 100644
index 00000000..c6a77e7f
--- /dev/null
+++ b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/Chart.yaml
@@ -0,0 +1,6 @@
+apiVersion: v2
+name: image-policies
+description: Sigstore policy-controller ClusterImagePolicies for the first-party images (CSR round 12 §3)
+type: application
+version: 0.1.0
+appVersion: "1.0.0"
diff --git a/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/templates/clusterimagepolicies.yaml b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/templates/clusterimagepolicies.yaml
new file mode 100644
index 00000000..8c8afbfd
--- /dev/null
+++ b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/templates/clusterimagepolicies.yaml
@@ -0,0 +1,33 @@
+{{- /*
+CSR round 12 §3. Each image's glob is its own name followed by "*", and the four names share no prefix
+with each other, so no image matches another's policy. policy-controller v0.15.1 cannot verify the
+workflow's cosign v3 bundle signature, but it verifies the SPDX attestation, which the same workflow
+identity signs for the same digest (signatureFormat: bundle).
+*/}}
+{{- range .Values.images }}
+{{- $image := printf "%s/%s" $.Values.registry . }}
+---
+apiVersion: policy.sigstore.dev/v1beta1
+kind: ClusterImagePolicy
+metadata:
+  name: {{ . }}
+spec:
+  images:
+  - glob: {{ printf "%s*" $image | quote }}
+  authorities:
+  - signatureFormat: bundle
+    keyless:
+      url: https://fulcio.sigstore.dev
+      identities:
+      - issuer: {{ $.Values.identity.issuer }}
+        subjectRegExp: {{ $.Values.identity.subjectRegExp | squote }}
+    ctlog:
+      url: https://rekor.sigstore.dev
+    attestations:
+    - name: sbom
+      predicateType: https://spdx.dev/Document
+      policy:
+        type: cue
+        data: |
+          subject: [{name: {{ $image | quote }}}]
+{{- end }}
diff --git a/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/values.yaml b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/values.yaml
new file mode 100644
index 00000000..634cb5e3
--- /dev/null
+++ b/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies/values.yaml
@@ -0,0 +1,16 @@
+# CSR round 12 §3: one ClusterImagePolicy per first-party image. A pod in a namespace labelled
+# policy.sigstore.dev/include=true (the release namespace on the clouds) is admitted with one of these
+# images only if publish-images.yml attested that digest's SPDX SBOM under this identity, and the
+# attestation's in-toto subject names the same image. That subject check refuses another first-party
+# image's digest pushed under this name.
+registry: ghcr.io/counsellorben
+images:
+  - iverson-api
+  - iverson-admin-ui
+  - iverson-ollama-model
+  - iverson-tei-model
+identity:
+  issuer: https://token.actions.githubusercontent.com
+  # Identical to publish-images.yml's IDENTITY and to docs/runbooks/csr11-clients-tooling-ci.md §2;
+  # deploy-validate.yml checks that the three agree.
+  subjectRegExp: '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$'
diff --git a/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml b/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml
index ddd92152..33089742 100644
--- a/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml
+++ b/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml
@@ -15,6 +15,9 @@ creates istio-system and iverson-edge, before installing this chart.
 {{- if .Values.kind }}
 {{- $namespaces = concat $namespaces (list "tigera-operator" "iverson-edge") }}
 {{- end }}
+{{- if .Values.cosignSystem }}
+{{- $namespaces = append $namespaces "cosign-system" }}
+{{- end }}
 {{- $rel := .Values.release.name }}
 {{- $relNs := .Values.release.namespace }}
 {{- range $ns := $namespaces }}
@@ -176,3 +179,26 @@ spec:
           podSelector: { matchLabels: { app: istiod } }
       ports: [{ protocol: TCP, port: 15012 }, { protocol: TCP, port: 15008 }]
 {{- end }}
+{{- if .Values.cosignSystem }}
+---
+# CSR round 12 §3: the policy-controller webhook. The API server calls it on 8443 (its Service's 443); it
+# reads the attestations from GHCR and, at startup, Sigstore's TUF repository, both on 443. cosign-system
+# is not enrolled in the mesh, so there is no 15008 here.
+apiVersion: networking.k8s.io/v1
+kind: NetworkPolicy
+metadata:
+  name: policy-controller-webhook
+  {{- include "onp.metadata" (dict "ns" "cosign-system") | nindent 2 }}
+spec:
+  podSelector:
+    matchLabels: { control-plane: policy-controller-webhook }
+  policyTypes: ["Ingress", "Egress"]
+  ingress:
+    - from:
+        {{- range .Values.apiServerCidrs }}
+        - ipBlock: { cidr: {{ . }} }
+        {{- end }}
+      ports: [{ protocol: TCP, port: 8443 }]
+  egress:
+    - ports: [{ protocol: TCP, port: 443 }]
+{{- end }}
diff --git a/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml b/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml
index 49cea5b5..fac0abd4 100644
--- a/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml
+++ b/Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml
@@ -5,6 +5,9 @@ apiServerCidrs: []
 # kind only: also governs tigera-operator and iverson-edge, the namespace of the kind gateway, and
 # admits that gateway to istiod.
 kind: false
+# The clouds only (CSR round 12 §3): also governs cosign-system, the Sigstore policy-controller's namespace,
+# which is not enrolled in the mesh.
+cosignSystem: false
 # The release whose Postgres (CNPG) and Kafka (Strimzi) pods the operators reconcile, and whose
 # api, admin-ui and Authentik pods the kind gateway reaches.
 release:
diff --git a/Iverson.Server/deploy/terraform/modules/operators/main.tf b/Iverson.Server/deploy/terraform/modules/operators/main.tf
index f28e6c70..89e545b5 100644
--- a/Iverson.Server/deploy/terraform/modules/operators/main.tf
+++ b/Iverson.Server/deploy/terraform/modules/operators/main.tf
@@ -24,6 +24,8 @@ resource "kubernetes_namespace" "iverson" {
       # CSR round 12 §4.1: enrolled in the Istio ambient mesh from creation, so every release pod
       # starts enrolled; the chart renders its 15008 (HBONE) rules with its first install.
       "istio.io/dataplane-mode" = "ambient"
+      # CSR round 12 §3: the admission engine checks this namespace's images (helm_release.policy_controller).
+      "policy.sigstore.dev/include" = "true"
     }
   }
 }
@@ -304,8 +306,50 @@ resource "kubernetes_storage_class" "prometheus" {
   volume_binding_mode = "WaitForFirstConsumer"
 }
 
+# CSR round 12 §3: Sigstore policy-controller, the admission engine. Its webhooks select namespaces labelled
+# policy.sigstore.dev/include=true, which only kubernetes_namespace.iverson carries, and fail closed
+# (failurePolicy Fail). In that namespace a first-party image is admitted only with the attestation the
+# image_policies chart requires; any other image is admitted unverified (no-match-policy allow), and every
+# image is rewritten to tag@digest. cosign-system is not enrolled in the mesh: the cloud API server, outside
+# the mesh, must reach the webhook.
+resource "kubernetes_namespace" "cosign_system" {
+  metadata {
+    name = "cosign-system"
+  }
+}
+
+resource "helm_release" "policy_controller" {
+  name       = "policy-controller"
+  repository = local.operator_charts["policy-controller"].repository
+  chart      = "policy-controller"
+  version    = local.operator_charts["policy-controller"].version
+  namespace  = kubernetes_namespace.cosign_system.metadata[0].name
+
+  values = [yamlencode({
+    webhook = {
+      # The chart's default app (0.13.1) lags; v0.15.1 is the release the admission probes verified, pinned
+      # by its index digest (ci/pins.yaml tracks it).
+      image = { version = "v0.15.1@sha256:0492bb264fb1d9bdc8e3f343ef542cc85b7dd7c7fd8d9524b453c2bd31a1d128" }
+      # At the chart's 200m a verification took 5-10 s against the webhook's 10 s timeout; about 3 s here.
+      resources = { limits = { cpu = "1000m" } }
+      # Only first-party images need an attestation; the default (deny) refuses every other image.
+      configData = { "no-match-policy" = "allow" }
+    }
+  })]
+}
+
+# CSR round 12 §3: the ClusterImagePolicies (cluster-scoped, so outside the app chart's admission policy).
+# Their CRD comes with the policy-controller chart.
+resource "helm_release" "image_policies" {
+  name      = "image-policies"
+  chart     = "${path.module}/charts/image-policies"
+  namespace = kubernetes_namespace.cosign_system.metadata[0].name
+
+  depends_on = [helm_release.policy_controller]
+}
+
 # CSR round 12 §3.4 (#16): default-deny with explicit allows in cnpg-system, kafka, starrocks and
-# istio-system. The same chart serves kind (deploy/kind/setup.sh, values-kind.yaml). Each namespace must
+# istio-system (and, CSR round 12 §3, cosign-system). The same chart serves kind (deploy/kind/setup.sh, values-kind.yaml). Each namespace must
 # exist first: Terraform creates each of them above (cnpg-system, kafka and starrocks enrolled in the mesh).
 resource "helm_release" "operator_namespace_policies" {
   name      = "operator-namespace-policies"
@@ -316,10 +360,13 @@ resource "helm_release" "operator_namespace_policies" {
     # CSR round 12 §4.1: the CNPG operator's webhook port 9443 is PERMISSIVE on the clouds, whose API
     # server is outside the mesh (templates/peerauthentication-cnpg.yaml).
     cnpgWebhookPermissive = true
+    # CSR round 12 §3: the policy-controller's namespace.
+    cosignSystem = true
   })]
 
   depends_on = [
     kubernetes_namespace.istio_system,
+    kubernetes_namespace.cosign_system,
     helm_release.istio_base,
     helm_release.cloudnative_pg,
     helm_release.strimzi,
diff --git a/Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml b/Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml
index d575bdb2..ab444284 100644
--- a/Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml
+++ b/Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml
@@ -29,3 +29,8 @@ dependencies:
   - name: aws-load-balancer-controller
     version: 1.9.0
     repository: https://aws.github.io/eks-charts
+  # CSR round 12 §3: the admission engine. The chart's default app lags; main.tf sets the engine image
+  # (ci/pins.yaml tracks it).
+  - name: policy-controller
+    version: 0.10.8
+    repository: https://sigstore.github.io/helm-charts
T7_PATCH
cd $WT && git apply --check $SCR/t7.patch && git apply $SCR/t7.patch && git status --short
````

- [ ] **Step 2: Render and lint the charts.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
IP=Iverson.Server/deploy/terraform/modules/operators/charts/image-policies
O=Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies
helm lint $IP | tail -1; helm template image-policies $IP > $SCR/t7-ip.yaml
python3 -c "import yaml; print([d['metadata']['name'] for d in yaml.safe_load_all(open('$SCR/t7-ip.yaml')) if d])"
for v in "-f $O/values-kind.yaml" "-f $O/values-kind.yaml --set kind=false --set cnpgWebhookPermissive=true --set cosignSystem=true"; do
  helm template onp $O $v > $SCR/t7-onp.yaml
  echo "$(command grep -c '^kind: NetworkPolicy' $SCR/t7-onp.yaml) policies, cosign-system=$(command grep -c 'namespace: cosign-system' $SCR/t7-onp.yaml); $(kubeconform -kubernetes-version 1.35.0 -summary -skip PeerAuthentication $SCR/t7-onp.yaml | tail -1)"; done
helm template onp /home/ben/repositories/Iverson/$O -f /home/ben/repositories/Iverson/$O/values-kind.yaml > $SCR/t7-onp-main.yaml
helm template onp $O -f $O/values-kind.yaml | cmp - $SCR/t7-onp-main.yaml && echo "kind render unchanged"
```

Expected: `0 chart(s) failed`; `['iverson-api', 'iverson-admin-ui', 'iverson-ollama-model', 'iverson-tei-model']`; `20 policies, cosign-system=0; … Valid: 20, Invalid: 0` (kind); `18 policies, cosign-system=3; … Valid: 18, Invalid: 0` (clouds); `kind render unchanged`.

- [ ] **Step 3: Validate the policies against the engine's own CRD.** kubeconform has no usable schema for ClusterImagePolicy (the CRDs-catalog one lacks `signatureFormat`), so build a strict one from the chart's CRD in scratch. The api policy must also equal the one the admission probes used (spec §3's YAML).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $SCR && rm -rf t7-chart && mkdir t7-chart && helm pull policy-controller --repo https://sigstore.github.io/helm-charts --version 0.10.8 --untar -d t7-chart
helm template policy-controller t7-chart/policy-controller --namespace cosign-system \
  --set webhook.image.version=v0.15.1@sha256:0492bb264fb1d9bdc8e3f343ef542cc85b7dd7c7fd8d9524b453c2bd31a1d128 > $SCR/t7-pc.yaml
command grep -m1 'image: "ghcr.io/sigstore' $SCR/t7-pc.yaml; command grep -m1 'control-plane: policy-controller-webhook' $SCR/t7-pc.yaml
mkdir -p $SCR/t7-schema/policy.sigstore.dev
python3 - $SCR/t7-pc.yaml $SCR/t7-schema/policy.sigstore.dev/clusterimagepolicy_v1beta1.json <<'T7SCHEMA'
import sys, json, yaml
crd = next(d for d in yaml.safe_load_all(open(sys.argv[1])) if d and d.get('kind') == 'CustomResourceDefinition' and d['spec']['names']['kind'] == 'ClusterImagePolicy')
v = next(v for v in crd['spec']['versions'] if v['name'] == 'v1beta1')
def strict(n):
    if isinstance(n, dict):
        if n.get('type') == 'object' and 'properties' in n and 'additionalProperties' not in n and not n.get('x-kubernetes-preserve-unknown-fields'):
            n['additionalProperties'] = False
        for x in n.values(): strict(x)
    elif isinstance(n, list):
        for x in n: strict(x)
s = v['schema']['openAPIV3Schema']; strict(s)
for k in ('apiVersion', 'kind'): s['properties'].setdefault(k, {'type': 'string'})
s['properties'].setdefault('metadata', {'type': 'object'})
json.dump(s, open(sys.argv[2], 'w')); print('schema written')
T7SCHEMA
L="$SCR/t7-schema/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json"
kubeconform -strict -summary -schema-location "$L" $SCR/t7-ip.yaml | tail -1
sed 's/signatureFormat/signatureFormatX/' $SCR/t7-ip.yaml | kubeconform -strict -summary -schema-location "$L" - | tail -1
python3 - $SCR/t7-ip.yaml <<'T7PROBE'
import sys, yaml
api = [d for d in yaml.safe_load_all(open(sys.argv[1])) if d and d['metadata']['name'] == 'iverson-api'][0]
probe = yaml.safe_load('''apiVersion: policy.sigstore.dev/v1beta1
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
        subjectRegExp: '^https://github\\.com/counsellorben/Iverson/\\.github/workflows/publish-images\\.yml@refs/(heads/main|tags/v.+)$'
    ctlog:
      url: https://rekor.sigstore.dev
    attestations:
    - name: sbom
      predicateType: https://spdx.dev/Document
      policy:
        type: cue
        data: |
          subject: [{name: "ghcr.io/counsellorben/iverson-api"}]
''')
print("api policy identical to the probed one:", api == probe)
T7PROBE
```

Expected: the engine image `ghcr.io/sigstore/policy-controller/policy-controller:v0.15.1@sha256:0492bb26…`; the `control-plane: policy-controller-webhook` label (the operator-namespace chart's selector); `schema written`; `Valid: 4, Invalid: 0`; `Valid: 0, Invalid: 4` (all four policies carry the misspelled field); `api policy identical to the probed one: True`.

- [ ] **Step 4: The identity check, Terraform and tfsec.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
python3 -c "import yaml
for s in yaml.safe_load(open('.github/workflows/deploy-validate.yml'))['jobs']['helm-lint']['steps']:
    if s.get('name','').startswith('The publish identity'): print(s['run'])" > $SCR/t7-id.sh
bash $SCR/t7-id.sh; echo "identity rc=$?"
cp docs/runbooks/csr11-clients-tooling-ci.md $SCR/rb.bak; sed -i '0,/heads\/main|tags\/v/s//heads\/main|tag\/v/' docs/runbooks/csr11-clients-tooling-ci.md
bash $SCR/t7-id.sh > /dev/null; echo "one copy changed rc=$?"; cp $SCR/rb.bak docs/runbooks/csr11-clients-tooling-ci.md; git diff --quiet -- docs/runbooks && echo restored
terraform fmt -check -recursive Iverson.Server/deploy/terraform && echo fmt-ok
for c in aws azure gcp; do terraform -chdir=Iverson.Server/deploy/terraform/$c init -backend=false -lockfile=readonly > $SCR/t7-init-$c.log 2>&1; i=$?
  terraform -chdir=Iverson.Server/deploy/terraform/$c validate -no-color > $SCR/t7-val-$c.log 2>&1; echo "$c init=$i validate=$? $(head -1 $SCR/t7-val-$c.log)"; done
tfsec Iverson.Server/deploy/terraform/ --no-color --exclude-path .terraform > $SCR/t7-tfsec.log 2>&1; echo "tfsec rc=$?"; tail -8 $SCR/t7-tfsec.log | command grep -E 'critical|high|medium|low'
```

Expected: the three identities printed equal and `identity rc=0`; `one copy changed rc=1`; `restored`; `fmt-ok`; `aws/azure/gcp init=0 validate=0 Success! The configuration is valid.`; `tfsec rc=0` with 0 critical, high, medium and low.

- [ ] **Step 5: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/terraform/modules/operators/charts/image-policies Iverson.Server/deploy/terraform/modules/operators/main.tf \
  Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml \
  Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml \
  Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml \
  Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf .github/workflows/deploy-validate.yml .gitlab-ci.yml
git commit -q -m "verify first-party images at admission on the cloud clusters

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat HEAD | tail -1
```

Expected: `10 files changed`.

---

### Task 8: CNPG images pinned by digest

**Spec:** §3 "Operator-managed workloads". VA 45.

**Files:** Create `…/modules/operators/versions/cloudnative-pg-values.yaml`; modify `Iverson.Server/deploy/helm/iverson/charts/postgres/{values.yaml,templates/cluster.yaml}`, `…/modules/operators/main.tf`, `.github/workflows/image-scan.yml`.

**Interfaces:** Produces the two CNPG pin locations (Task 11).

- [ ] **Step 1: Apply the patch.** The postgres subchart's `Cluster` sets `imageName` from its values (the digest-pinned default image); `helm_release.cloudnative_pg` reads the new values file (`image.tag: "1.30.0@sha256:a2701eb9…"`); `image-scan.yml` lists the rendered `imageName` (its image sed also matches `imageName:`) and renders the operator with the same values file, instead of reading `versions.go`. kind keeps the chart's default operator image (its namespace is not labelled).

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t8.patch <<'T8_PATCH'
diff --git a/.github/workflows/image-scan.yml b/.github/workflows/image-scan.yml
index e62a5d7a..007dae6e 100644
--- a/.github/workflows/image-scan.yml
+++ b/.github/workflows/image-scan.yml
@@ -63,33 +63,27 @@ jobs:
             list=$(helm dependency list "$PINS") || { echo "helm dependency list failed for $PINS" >&2; return 2; }
             printf '%s\n' "$list" | awk -v chart="$1" -v col="$2" '$1 == chart && !n++ { print $col } END { exit !n }'
           }
-          render_operator() {   # $1 = chart name; prints the rendered manifests
+          render_operator() {   # $1 = chart name, then any extra helm template arguments; prints the rendered manifests
             local repo version
             # A missing pin (1) falls through to the empty check; a helm failure stops here.
             repo=$(chart_pin "$1" 3) || [ $? -eq 1 ] || return 1
             version=$(chart_pin "$1" 2) || [ $? -eq 1 ] || return 1
             [ -n "$repo" ] && [ -n "$version" ] || { echo "no $1 pin in $PINS/Chart.yaml" >&2; return 1; }
-            helm template op "$1" --repo "$repo" --version "$version"
+            helm template op "$1" --repo "$repo" --version "$version" "${@:2}"
           }
           {
             for v in values-aws values-azure values-gcp; do
               helm template iverson "$C" -f "$C/$v.yaml" -f "$C/$v.ci-override.yaml"
-            done | sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p' \
+            done | sed -n 's/^ *image\(Name\)\{0,1\}: "\{0,1\}\([^" ]*\)"\{0,1\}$/\2/p' \
                  | grep -v -E '(^|/)iverson-'
             # The model images' base references (fully qualified and digest-pinned).
             sed -n 's/^FROM \([^ ]*\)\( AS .*\)\{0,1\}$/\1/p' "$C"/charts/*/model-image/Dockerfile
 
-            # CNPG: the operator image (it also runs as an init container in every PostgreSQL pod)
-            # and, because the chart's Cluster sets no imageName, the operator's built-in default
-            # PostgreSQL image. That default is compiled into the operator, not rendered by its
-            # chart, so read it from the operator source at the chart's app version.
-            cnpg=$(render_operator cloudnative-pg)
-            cnpg_image=$(printf '%s\n' "$cnpg" | sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p' | head -1)
-            cnpg_version=${cnpg_image##*:}
-            test -n "$cnpg_version"
-            echo "$cnpg_image"
-            curl -fsSL "https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v$cnpg_version/pkg/versions/versions.go" \
-              | sed -n 's/^[[:space:]]*DefaultImageName = "\([^"]*\)"$/\1/p' | grep .
+            # CNPG: the operator image (it also runs as an init container in every PostgreSQL pod), with
+            # the digest-pinned tag Terraform installs it with (CSR round 12 §3). The PostgreSQL image is
+            # the chart Cluster's imageName, read from the renders above.
+            render_operator cloudnative-pg -f Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml \
+              | sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p' | head -1 | grep .
 
             # Strimzi: the operator image (it also runs the entity operator the Kafka CR enables) and
             # the Kafka image for the version the chart's Kafka CR pins.
diff --git a/Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml b/Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml
index a7ce45b9..99f34367 100644
--- a/Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml
+++ b/Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml
@@ -4,6 +4,7 @@ metadata:
   name: {{ .Release.Name }}-postgres
 spec:
   instances: {{ .Values.instances }}
+  imageName: {{ .Values.imageName | quote }}
   enableSuperuserAccess: false   # least privilege — nothing authenticates as `postgres`: the worker and the hooks use the owner `iverson`, the api uses iverson_api below
   storage:
     size: {{ .Values.storageSize }}
diff --git a/Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml b/Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml
index 02d90bad..ac2d3b3c 100644
--- a/Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml
+++ b/Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml
@@ -1,4 +1,9 @@
 instances: 2
+# CSR round 12 §3: CNPG 1.30.0's default PostgreSQL image (its DefaultImageName at the operators stub's chart
+# 0.29.0), pinned by its index digest. On the clouds the admission engine rewrites every image to tag@digest,
+# and CNPG rolls an instance whose image string differs from its target, so a tag-only target would roll the
+# instances on every reconcile; a tag@digest target is written back unchanged. ci/pins.yaml tracks it.
+imageName: "ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie@sha256:42708a75345b7a48fdd9257b071830783a97fd228529196b6313187a7198e185"
 storageSize: 1500Gi
 storageClassName: ""
 resources:
diff --git a/Iverson.Server/deploy/terraform/modules/operators/main.tf b/Iverson.Server/deploy/terraform/modules/operators/main.tf
index 89e545b5..51e34767 100644
--- a/Iverson.Server/deploy/terraform/modules/operators/main.tf
+++ b/Iverson.Server/deploy/terraform/modules/operators/main.tf
@@ -149,6 +149,8 @@ resource "helm_release" "cloudnative_pg" {
   chart      = "cloudnative-pg"
   version    = local.operator_charts["cloudnative-pg"].version
   namespace  = kubernetes_namespace.cnpg_system.metadata[0].name
+  # CSR round 12 §3: the operator image pinned by digest (see the file).
+  values = [file("${path.module}/versions/cloudnative-pg-values.yaml")]
 
   # CSR round 12 §4.1: the namespace is mesh-labelled, so the operator pods start after istio-cni and
   # ztunnel are ready and are enrolled from their first start (the same for strimzi and starrocks below).
diff --git a/Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml b/Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml
new file mode 100644
index 00000000..23776612
--- /dev/null
+++ b/Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml
@@ -0,0 +1,8 @@
+# CSR round 12 §3: the CNPG operator image, the chart's appVersion pinned by its index digest. CNPG compares
+# each instance's bootstrap-controller init container with OPERATOR_IMAGE_NAME, both rendered from this tag;
+# on the clouds the admission engine rewrites a tag-only image to tag@digest, which would differ from the
+# operator's own string and roll every instance. A tag@digest string is written back unchanged.
+# Read by modules/operators/main.tf (helm_release.cloudnative_pg) and .github/workflows/image-scan.yml.
+# A Dependabot bump of the chart in Chart.yaml leaves this tag behind: ci/pins.py freshness reports it.
+image:
+  tag: "1.30.0@sha256:a2701eb97cdd2a34b1fdb2cb51987f544b706e40bec72ae7146cd8580efefebb"
T8_PATCH
cd $WT && git apply --check $SCR/t8.patch && git apply $SCR/t8.patch && git status --short
````

- [ ] **Step 2: Render, and compare image-scan's list with `main`'s.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT; C=Iverson.Server/deploy/helm/iverson; helm dependency build $C > /dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do x=""; [ -f $C/$v.ci-override.yaml ] && x="-f $C/$v.ci-override.yaml"
  echo "$v: $(helm lint $C -f $C/$v.yaml $x 2>&1 | tail -1) imageName=$(helm template iverson $C -f $C/$v.yaml $x 2>/dev/null | command grep -c 'imageName: "ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie@sha256:42708a75')"; done
mkdir -p $SCR/t8-main; git -C /home/ben/repositories/Iverson worktree add --detach $SCR/t8-main main > /dev/null 2>&1 || true
for t in $WT $SCR/t8-main; do python3 -c "import yaml,sys; s=[x for x in yaml.safe_load(open(sys.argv[1]))['jobs']['third-party']['steps'] if x.get('name','').startswith('List')][0]; print(s['run'])" $t/.github/workflows/image-scan.yml > $SCR/t8-list.sh
  ( cd $t && bash -e $SCR/t8-list.sh > /dev/null 2>$SCR/t8-list.err; echo "list rc=$?"; mv images.txt $SCR/t8-images-$(basename $t).txt ); done
diff $SCR/t8-images-t8-main.txt $SCR/t8-images-csr12-supply-chain.txt
git -C /home/ben/repositories/Iverson worktree remove --force $SCR/t8-main
```

Expected: every overlay `0 chart(s) failed imageName=1`; `list rc=0` twice; the diff shows only the two CNPG lines, `:1.30.0` → `:1.30.0@sha256:a2701eb9…` and `:18.4-system-trixie` → `:18.4-system-trixie@sha256:42708a75…`. (`Iverson.Server/deploy/helm/iverson/charts/*.tgz` is gitignored.) That CNPG v1.30.0's webhook accepts the `tag@digest` `imageName` is checked live in Task 17 Step 6, which applies exactly this `Cluster` (plan-level row 47; CIR-1 §3.2 A); a refusal there comes back to this task.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add .github/workflows/image-scan.yml Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml \
  Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml Iverson.Server/deploy/terraform/modules/operators/main.tf \
  Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml
git commit -q -m "pin the cnpg postgresql and operator images by digest so the admission rewrite rolls nothing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Dependabot registries, the kind stub and the GitLab images stub

**Spec:** §4.1, §4.2, §4.3. VA 26–28, 30.

**Files:**
- Create: `Iverson.Server/deploy/kind/versions/Chart.yaml`, `Iverson.Server/deploy/kind/test_chart_pins.py`, `ci/gitlab-images/Dockerfile`.
- Modify: `.github/dependabot.yml`, `Iverson.Server/deploy/kind/setup.sh`, `Iverson.Server/deploy/kind/setup.ps1`.

- [ ] **Step 1: Apply the patch.** `dependabot.yml` declares one anonymous `helm-registry` per chart repository, gives the umbrella, the operators stub and the kind stub their own `helm` entries with `registries: "*"`, moves the stub's two `ignore` rules with it, ignores `iverson-api`, `iverson-admin-ui` and `registry.k8s.io/kubectl` in the umbrella entry, and adds `/ci/gitlab-images` to the `docker` entry. The setup helpers take the stub directory first; Calico and metrics-server come from the kind stub. (`policy-controller` is already in the operators stub from Task 7.)

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t9.patch <<'T9_PATCH'
diff --git a/.github/dependabot.yml b/.github/dependabot.yml
index ee003e44..154e1fbc 100644
--- a/.github/dependabot.yml
+++ b/.github/dependabot.yml
@@ -1,4 +1,32 @@
 version: 2
+# CSR round 12 §4.1: an update job reaches only allowlisted hosts, and declaring a registry here allowlists it,
+# even with anonymous access. Without these the helm jobs failed on every chart repository (403, "egress not
+# allowlisted"). One per chart repository a helm entry below reads.
+registries:
+  cloudnative-pg:
+    type: helm-registry
+    url: https://cloudnative-pg.github.io/charts
+  strimzi:
+    type: helm-registry
+    url: https://strimzi.io/charts/
+  starrocks:
+    type: helm-registry
+    url: https://starrocks.github.io/starrocks-kubernetes-operator
+  autoscaler:
+    type: helm-registry
+    url: https://kubernetes.github.io/autoscaler
+  eks-charts:
+    type: helm-registry
+    url: https://aws.github.io/eks-charts
+  sigstore:
+    type: helm-registry
+    url: https://sigstore.github.io/helm-charts
+  calico:
+    type: helm-registry
+    url: https://docs.tigera.io/calico/charts
+  metrics-server:
+    type: helm-registry
+    url: https://kubernetes-sigs.github.io/metrics-server/
 updates:
   - package-ecosystem: "nuget"
     directory: "/"
@@ -57,6 +85,8 @@ updates:
       - "/Iverson.Server/Iverson.Vector"
       - "/Iverson.Server/deploy/helm/iverson/charts/tei/model-image"
       - "/Iverson.Server/deploy/helm/iverson/charts/ollama/model-image"
+      # CSR round 12 §4.3: the GitLab job images (never built; see the file).
+      - "/ci/gitlab-images"
     schedule:
       interval: "weekly"
   - package-ecosystem: "docker-compose"
@@ -77,7 +107,19 @@ updates:
     directories:
       - "/Iverson.Server/deploy/helm/iverson"
       - "/Iverson.Server/deploy/helm/iverson/charts/*"
-      - "/Iverson.Server/deploy/terraform/modules/operators/versions"
+    registries: "*"
+    schedule:
+      interval: "weekly"
+    # CSR round 12 §4.1: the umbrella's own images (built locally, not on Docker Hub) and the kubectl image
+    # (looked up at a wrong path); ci/pins.yaml tracks kubectl instead.
+    ignore:
+      - dependency-name: "iverson-api"
+      - dependency-name: "iverson-admin-ui"
+      - dependency-name: "registry.k8s.io/kubectl"
+  # The operator charts the cloud Terraform installs (CSR round 12 §4.1: its own entry, with the registries).
+  - package-ecosystem: "helm"
+    directory: "/Iverson.Server/deploy/terraform/modules/operators/versions"
+    registries: "*"
     schedule:
       interval: "weekly"
     # These two move in lockstep with other files (see the coupling notes in the stub's Chart.yaml): minor/major bumps are done by hand.
@@ -86,3 +128,9 @@ updates:
         update-types: ["version-update:semver-minor", "version-update:semver-major"]
       - dependency-name: "aws-load-balancer-controller"
         update-types: ["version-update:semver-minor", "version-update:semver-major"]
+  # CSR round 12 §4.2: the charts only kind installs.
+  - package-ecosystem: "helm"
+    directory: "/Iverson.Server/deploy/kind/versions"
+    registries: "*"
+    schedule:
+      interval: "weekly"
diff --git a/Iverson.Server/deploy/kind/setup.ps1 b/Iverson.Server/deploy/kind/setup.ps1
index 42281553..7903298c 100644
--- a/Iverson.Server/deploy/kind/setup.ps1
+++ b/Iverson.Server/deploy/kind/setup.ps1
@@ -21,19 +21,24 @@ if ([version](($KindVersion.TrimStart('v') -split '-')[0]) -lt $KindMin) {
 # Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
 # Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
 # read before anything touches the cluster, so a missing pin stops the script here.
+# CSR round 12 §4.2: the charts only kind installs (Calico, metrics-server) are pinned the same way in
+# deploy/kind/versions.
 $OperatorPins = Join-Path $PSScriptRoot "../terraform/modules/operators/versions"
-function Get-OperatorVersion([string]$Chart) {
-  $list = helm dependency list $OperatorPins
-  if ($LASTEXITCODE -ne 0) { throw "helm dependency list failed for $OperatorPins" }
+$KindPins = Join-Path $PSScriptRoot "versions"
+function Get-OperatorVersion([string]$Pins, [string]$Chart) {
+  $list = helm dependency list $Pins
+  if ($LASTEXITCODE -ne 0) { throw "helm dependency list failed for $Pins" }
   foreach ($line in $list) {
     $cols = "$line".Trim() -split '\s+'
     if ($cols[0] -eq $Chart) { return $cols[1] }
   }
-  throw "no $Chart pin in $(Join-Path $OperatorPins 'Chart.yaml')"
+  throw "no $Chart pin in $(Join-Path $Pins 'Chart.yaml')"
 }
-$CnpgVersion = Get-OperatorVersion "cloudnative-pg"
-$StrimziVersion = Get-OperatorVersion "strimzi-kafka-operator"
-$StarRocksOperatorVersion = Get-OperatorVersion "operator"
+$CnpgVersion = Get-OperatorVersion $OperatorPins "cloudnative-pg"
+$StrimziVersion = Get-OperatorVersion $OperatorPins "strimzi-kafka-operator"
+$StarRocksOperatorVersion = Get-OperatorVersion $OperatorPins "operator"
+$CalicoVersion = Get-OperatorVersion $KindPins "tigera-operator"
+$MetricsServerVersion = Get-OperatorVersion $KindPins "metrics-server"
 
 # CSR round 12 §5: with no argument this script runs the whole fresh install. On an existing cluster the
 # upgrade runs the platform steps one at a time (docs/runbooks/csr12-platform.md §3): `setup.ps1 --only
@@ -164,7 +169,7 @@ kubectl create namespace tigera-operator --dry-run=client -o yaml | kubectl appl
 # disabled values) to add the CRs now that the CRDs exist.
 helm upgrade --install calico tigera-operator `
   --repo https://docs.tigera.io/calico/charts `
-  --version v3.32.2 `
+  --version $CalicoVersion `
   --namespace tigera-operator `
   --set installation.enabled=false `
   --set apiServer.enabled=false `
@@ -181,7 +186,7 @@ for ($i = 0; $i -lt 30; $i++) {
 kubectl wait --for=condition=Established crd/installations.operator.tigera.io --timeout=60s
 helm upgrade --install calico tigera-operator `
   --repo https://docs.tigera.io/calico/charts `
-  --version v3.32.2 `
+  --version $CalicoVersion `
   --namespace tigera-operator `
   --reset-values `
   --wait
@@ -232,7 +237,7 @@ Write-Host "Installing metrics-server..."
 # metrics-server will otherwise reject.
 helm upgrade --install metrics-server metrics-server `
   --repo https://kubernetes-sigs.github.io/metrics-server/ `
-  --version 3.9.0 `
+  --version $MetricsServerVersion `
   --namespace kube-system `
   --set 'args={--kubelet-insecure-tls}' `
   --wait
diff --git a/Iverson.Server/deploy/kind/setup.sh b/Iverson.Server/deploy/kind/setup.sh
index f85e2fb2..af1acdd0 100755
--- a/Iverson.Server/deploy/kind/setup.sh
+++ b/Iverson.Server/deploy/kind/setup.sh
@@ -24,18 +24,22 @@ fi
 # CSR round-11 #19: the CNPG, Strimzi and StarRocks operator chart versions come from the stub
 # Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
 # Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
-# read before anything touches the cluster, so a missing pin stops the script here.
+# read before anything touches the cluster, so a missing pin stops the script here. CSR round 12 §4.2: the
+# charts only kind installs (Calico, metrics-server) are pinned the same way in deploy/kind/versions.
 OPERATOR_PINS="$(cd "$(dirname "${BASH_SOURCE[0]}")/../terraform/modules/operators/versions" && pwd)"
-operator_version() {   # $1 = chart name in $OPERATOR_PINS/Chart.yaml; prints its version
+KIND_PINS="$(cd "$(dirname "${BASH_SOURCE[0]}")/versions" && pwd)"
+operator_version() {   # $1 = a stub chart directory, $2 = a chart name in its Chart.yaml; prints its version
   local list
-  list=$(helm dependency list "$OPERATOR_PINS") ||
-    { echo "helm dependency list failed for $OPERATOR_PINS" >&2; return 1; }
-  printf '%s\n' "$list" | awk -v chart="$1" '$1 == chart && !n++ { print $2 } END { exit !n }' ||
-    { echo "no $1 pin in $OPERATOR_PINS/Chart.yaml" >&2; return 1; }
+  list=$(helm dependency list "$1") ||
+    { echo "helm dependency list failed for $1" >&2; return 1; }
+  printf '%s\n' "$list" | awk -v chart="$2" '$1 == chart && !n++ { print $2 } END { exit !n }' ||
+    { echo "no $2 pin in $1/Chart.yaml" >&2; return 1; }
 }
-CNPG_VERSION=$(operator_version cloudnative-pg)
-STRIMZI_VERSION=$(operator_version strimzi-kafka-operator)
-STARROCKS_OPERATOR_VERSION=$(operator_version operator)
+CNPG_VERSION=$(operator_version "$OPERATOR_PINS" cloudnative-pg)
+STRIMZI_VERSION=$(operator_version "$OPERATOR_PINS" strimzi-kafka-operator)
+STARROCKS_OPERATOR_VERSION=$(operator_version "$OPERATOR_PINS" operator)
+CALICO_VERSION=$(operator_version "$KIND_PINS" tigera-operator)
+METRICS_SERVER_VERSION=$(operator_version "$KIND_PINS" metrics-server)
 
 # CSR round 12 §5: with no argument this script runs the whole fresh install. On an existing cluster the
 # upgrade runs the platform steps one at a time (docs/runbooks/csr12-platform.md §3): `setup.sh --only
@@ -152,11 +156,10 @@ kubectl create namespace tigera-operator --dry-run=client -o yaml | kubectl appl
 # disabled values) to add the CRs now that the CRDs exist.
 # CSR round-2 finding #12: pin every operator install to an explicit chart
 # version rather than letting it float to whatever the repo's index.yaml
-# currently calls "latest" — verified current stable as of this fix pass
-# (docs.tigera.io/calico/charts/index.yaml).
+# currently calls "latest"; the version is read from deploy/kind/versions (CSR round 12 §4.2).
 helm upgrade --install calico tigera-operator \
   --repo https://docs.tigera.io/calico/charts \
-  --version v3.32.2 \
+  --version "$CALICO_VERSION" \
   --namespace tigera-operator \
   --set installation.enabled=false \
   --set apiServer.enabled=false \
@@ -173,7 +176,7 @@ done
 kubectl wait --for=condition=Established crd/installations.operator.tigera.io --timeout=60s
 helm upgrade --install calico tigera-operator \
   --repo https://docs.tigera.io/calico/charts \
-  --version v3.32.2 \
+  --version "$CALICO_VERSION" \
   --namespace tigera-operator \
   --reset-values \
   --wait
@@ -238,11 +241,10 @@ echo "Installing metrics-server..."
 # `kubectl top` work, which the laptop profile's capacity numbers depend on.
 # --kubelet-insecure-tls is required on kind: kubelet serves a self-signed cert that
 # metrics-server will otherwise reject.
-# CSR round-2 finding #12: verified current stable as of this fix pass
-# (kubernetes-sigs.github.io/metrics-server/index.yaml).
+# CSR round-2 finding #12: an explicit chart version, read from deploy/kind/versions (CSR round 12 §4.2).
 helm upgrade --install metrics-server metrics-server \
   --repo https://kubernetes-sigs.github.io/metrics-server/ \
-  --version 3.9.0 \
+  --version "$METRICS_SERVER_VERSION" \
   --namespace kube-system \
   --set 'args={--kubelet-insecure-tls}' \
   --wait
diff --git a/Iverson.Server/deploy/kind/test_chart_pins.py b/Iverson.Server/deploy/kind/test_chart_pins.py
new file mode 100644
index 00000000..b35df244
--- /dev/null
+++ b/Iverson.Server/deploy/kind/test_chart_pins.py
@@ -0,0 +1,91 @@
+"""pytest suite for the stub-reading helpers of setup.sh (operator_version) and setup.ps1
+(Get-OperatorVersion), CSR round 12 §4.2. Run with:
+
+    python3 -m pytest Iverson.Server/deploy/kind/test_chart_pins.py -q
+
+Each test runs the helper's own text, cut out of the script, against a throwaway stub chart; helm must be
+on PATH. The PowerShell tests need pwsh on PATH and are skipped without it."""
+import os
+import re
+import shutil
+import subprocess
+
+import pytest
+
+HERE = os.path.dirname(os.path.abspath(__file__))
+
+STUB = """apiVersion: v2
+name: stub
+version: 0.1.0
+dependencies:
+  - name: tigera-operator
+    version: v3.32.2
+    repository: https://docs.tigera.io/calico/charts
+  - name: metrics-server
+    version: 3.9.0
+    repository: https://kubernetes-sigs.github.io/metrics-server/
+"""
+
+
+@pytest.fixture
+def stub(tmp_path):
+    (tmp_path / "Chart.yaml").write_text(STUB)
+    return str(tmp_path)
+
+
+def bash_helper():
+    text = open(os.path.join(HERE, "setup.sh")).read()
+    match = re.search(r"^operator_version\(\) \{.*?^\}\n", text, re.M | re.S)
+    assert match, "setup.sh defines no operator_version()"
+    return match.group(0)
+
+
+def run_bash(stub_dir, chart):
+    return subprocess.run(["bash", "-c", bash_helper() + 'operator_version "$1" "$2"', "-", stub_dir, chart],
+                          capture_output=True, text=True)
+
+
+def test_bash_prints_the_pinned_version_of_each_chart(stub):
+    assert run_bash(stub, "tigera-operator").stdout == "v3.32.2\n"
+    assert run_bash(stub, "metrics-server").stdout == "3.9.0\n"
+
+
+def test_bash_fails_naming_the_stub_for_a_chart_it_does_not_pin(stub):
+    result = run_bash(stub, "cert-manager")
+    assert result.returncode != 0
+    assert result.stdout == ""
+    assert f"no cert-manager pin in {stub}/Chart.yaml" in result.stderr
+
+
+def test_bash_reads_the_real_kind_stub():
+    result = run_bash(os.path.join(HERE, "versions"), "tigera-operator")
+    assert result.returncode == 0 and result.stdout.strip().startswith("v3.")
+
+
+needs_pwsh = pytest.mark.skipif(shutil.which("pwsh") is None, reason="pwsh is not on PATH")
+
+
+def pwsh_helper():
+    text = open(os.path.join(HERE, "setup.ps1")).read()
+    match = re.search(r"^function Get-OperatorVersion\(.*?^\}\n", text, re.M | re.S)
+    assert match, "setup.ps1 defines no Get-OperatorVersion"
+    return match.group(0)
+
+
+def run_pwsh(stub_dir, chart):
+    script = '$ErrorActionPreference = "Stop"\n' + pwsh_helper() + f"Get-OperatorVersion '{stub_dir}' '{chart}'\n"
+    return subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", script],
+                          capture_output=True, text=True)
+
+
+@needs_pwsh
+def test_pwsh_prints_the_pinned_version_of_each_chart(stub):
+    assert run_pwsh(stub, "tigera-operator").stdout.strip() == "v3.32.2"
+    assert run_pwsh(stub, "metrics-server").stdout.strip() == "3.9.0"
+
+
+@needs_pwsh
+def test_pwsh_fails_naming_the_stub_for_a_chart_it_does_not_pin(stub):
+    result = run_pwsh(stub, "cert-manager")
+    assert result.returncode != 0
+    assert "no cert-manager pin in" in result.stdout + result.stderr
diff --git a/Iverson.Server/deploy/kind/versions/Chart.yaml b/Iverson.Server/deploy/kind/versions/Chart.yaml
new file mode 100644
index 00000000..84dd535b
--- /dev/null
+++ b/Iverson.Server/deploy/kind/versions/Chart.yaml
@@ -0,0 +1,16 @@
+# CSR round 12 §4.2: the remote charts only kind installs (deploy/kind/setup.sh and setup.ps1: Calico, which
+# enforces NetworkPolicy on kind, and metrics-server), pinned here so Dependabot's helm ecosystem proposes
+# their bumps. A dependencies-only stub: nothing renders, packages or deploys this chart. Keep each entry's
+# order (name, version, repository) and the versions unquoted, the form Dependabot rewrites.
+apiVersion: v2
+name: iverson-kind
+description: Version pins for the charts deploy/kind/setup.sh and setup.ps1 install (not deployed)
+type: application
+version: 0.1.0
+dependencies:
+  - name: tigera-operator
+    version: v3.32.2
+    repository: https://docs.tigera.io/calico/charts
+  - name: metrics-server
+    version: 3.9.0
+    repository: https://kubernetes-sigs.github.io/metrics-server/
diff --git a/ci/gitlab-images/Dockerfile b/ci/gitlab-images/Dockerfile
new file mode 100644
index 00000000..5539946a
--- /dev/null
+++ b/ci/gitlab-images/Dockerfile
@@ -0,0 +1,10 @@
+# CSR round 12 §4.3: every image .gitlab-ci.yml runs, one FROM each, so Dependabot's docker ecosystem proposes
+# their bumps (tag and digest). Never built. ci/pins.py consistency (deploy-validate.yml) keeps .gitlab-ci.yml
+# equal to this file, so a Dependabot bump here stays red until the same pull request copies it there.
+FROM alpine/helm:3.15.4@sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2
+FROM hashicorp/terraform:1.7.5@sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e
+FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
+FROM node:22@sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8
+FROM golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89
+FROM maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
+FROM python:3.11@sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f
T9_PATCH
cd $WT && git apply --check $SCR/t9.patch && git apply $SCR/t9.patch && git status --short
````

- [ ] **Step 2: The helpers' tests, with and without PowerShell.** PowerShell 7.5.13 goes into scratch only (its release's SHA-256 checked); without `pwsh` the two PowerShell tests skip.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
helm dependency list Iverson.Server/deploy/kind/versions
bash -n Iverson.Server/deploy/kind/setup.sh && echo sh-syntax-ok
command grep -n -E 'v3\.32\.2|--version 3\.9\.0' Iverson.Server/deploy/kind/setup.sh Iverson.Server/deploy/kind/setup.ps1 || echo "no inline chart versions left"
python3 -m pytest Iverson.Server/deploy/kind/test_chart_pins.py -q -p no:cacheprovider 2>&1 | tail -1
mkdir -p $SCR/pwsh && cd $SCR/pwsh && [ -x pwsh ] || { curl -fsSLO https://github.com/PowerShell/PowerShell/releases/download/v7.5.13/powershell-7.5.13-linux-x64.tar.gz \
  && echo "e9256bfc6ac98fcbec97270e8a986577a7346a587837cc80d30595eef3c2546b  powershell-7.5.13-linux-x64.tar.gz" | sha256sum -c - \
  && tar xzf powershell-7.5.13-linux-x64.tar.gz && chmod +x pwsh; }
cd $WT && PATH=$SCR/pwsh:$PATH python3 -m pytest Iverson.Server/deploy/kind/test_chart_pins.py -q -p no:cacheprovider 2>&1 | tail -1
python3 -c "
import yaml; d=yaml.safe_load(open('.github/dependabot.yml'))
print(sorted(d['registries']), {r['type'] for r in d['registries'].values()})
for u in d['updates']:
    if u['package-ecosystem'] in ('helm', 'docker'): print(u['package-ecosystem'], u.get('directory') or u.get('directories'), u.get('registries'), [i['dependency-name'] for i in u.get('ignore', [])])"
command grep -c '^FROM ' ci/gitlab-images/Dockerfile; command grep -E '^\s+image: ' .gitlab-ci.yml | sed 's/^ *image: //' | sort -u | wc -l
```

Expected: the stub lists `tigera-operator v3.32.2` and `metrics-server 3.9.0`; `sh-syntax-ok`; `no inline chart versions left`; `3 passed, 2 skipped`; `…: OK`; `5 passed`; eight registries of type `helm-registry`; the umbrella entry (`registries: *`, ignores `iverson-api`, `iverson-admin-ui`, `registry.k8s.io/kubectl`), the operators-stub entry (`*`, ignores `cluster-autoscaler`, `aws-load-balancer-controller`), the kind-stub entry (`*`), and the docker entry ending in `/ci/gitlab-images`; `7` and `7`. (At plan time the same tests failed 5 of 5 against `main`'s scripts.)

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add .github/dependabot.yml ci/gitlab-images/Dockerfile Iverson.Server/deploy/kind/versions/Chart.yaml Iverson.Server/deploy/kind/setup.sh \
  Iverson.Server/deploy/kind/setup.ps1 Iverson.Server/deploy/kind/test_chart_pins.py
git commit -q -m "let dependabot reach the chart repositories and track the kind and gitlab pins

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: The pin-freshness AWS role

**Spec:** §4.4. VA 32, 33.

**Files:** Modify `Iverson.Server/deploy/terraform/bootstrap/aws/main.tf`.

- [ ] **Step 1: Apply the patch.** GitHub's OIDC provider, the role `iverson-pin-freshness` that only `repo:counsellorben/Iverson:ref:refs/heads/main` can assume, an inline policy allowing only `eks:DescribeAddonVersions`, and the output `pin_freshness_role_arn`.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t10.patch <<'T10_PATCH'
diff --git a/Iverson.Server/deploy/terraform/bootstrap/aws/main.tf b/Iverson.Server/deploy/terraform/bootstrap/aws/main.tf
index 1dc398c3..db69f8f0 100644
--- a/Iverson.Server/deploy/terraform/bootstrap/aws/main.tf
+++ b/Iverson.Server/deploy/terraform/bootstrap/aws/main.tf
@@ -63,5 +63,48 @@ resource "aws_dynamodb_table" "locks" {
   }
 }
 
+# CSR round 12 §4.4: the pin-freshness workflow (.github/workflows/pin-freshness.yml) reads which EKS add-on
+# versions are compatible with the cluster's Kubernetes version. It signs in with GitHub's OIDC token, as a role
+# only main's runs of this repository can assume, holding only eks:DescribeAddonVersions. An account has one
+# OIDC provider per URL: if it already has GitHub's, import it before the first apply
+# (docs/runbooks/csr12-supply-chain.md).
+resource "aws_iam_openid_connect_provider" "github" {
+  url            = "https://token.actions.githubusercontent.com"
+  client_id_list = ["sts.amazonaws.com"]
+}
+
+resource "aws_iam_role" "pin_freshness" {
+  name = "iverson-pin-freshness"
+  assume_role_policy = jsonencode({
+    Version = "2012-10-17"
+    Statement = [{
+      Effect    = "Allow"
+      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
+      Action    = "sts:AssumeRoleWithWebIdentity"
+      Condition = {
+        StringEquals = {
+          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
+          "token.actions.githubusercontent.com:sub" = "repo:counsellorben/Iverson:ref:refs/heads/main"
+        }
+      }
+    }]
+  })
+}
+
+resource "aws_iam_role_policy" "pin_freshness" {
+  name = "describe-addon-versions"
+  role = aws_iam_role.pin_freshness.id
+  policy = jsonencode({
+    Version = "2012-10-17"
+    Statement = [{
+      Effect   = "Allow"
+      Action   = "eks:DescribeAddonVersions"
+      Resource = "*"
+    }]
+  })
+}
+
 output "state_bucket_name" { value = aws_s3_bucket.state.bucket }
 output "lock_table_name" { value = aws_dynamodb_table.locks.name }
+# Set as the repository variable PIN_FRESHNESS_AWS_ROLE.
+output "pin_freshness_role_arn" { value = aws_iam_role.pin_freshness.arn }
T10_PATCH
cd $WT && git apply --check $SCR/t10.patch && git apply $SCR/t10.patch && git status --short
````

- [ ] **Step 2: Validate.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Server/deploy/terraform
terraform fmt -check -recursive . && echo fmt-ok
terraform -chdir=bootstrap/aws init -backend=false -lockfile=readonly > $SCR/t10-init.log 2>&1; echo "init rc=$?"; terraform -chdir=bootstrap/aws validate -no-color | head -1
cd $WT && tfsec Iverson.Server/deploy/terraform/ --no-color --exclude-path .terraform > $SCR/t10-tfsec.log 2>&1; echo "tfsec rc=$?"
```

Expected: `fmt-ok`, `init rc=0`, `Success! The configuration is valid.`, `tfsec rc=0`.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/terraform/bootstrap/aws/main.tf
git commit -q -m "add a read-only aws role for the eks add-on freshness check

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: The pin table and its two checks

**Spec:** §4.5, Decision 9, Choice 1. VA 29, 31, 32, 34, 52.

**Files:**
- Create: `ci/pins.yaml`, `ci/pins.py`, `ci/test_pins.py`, `.github/workflows/pin-freshness.yml`.
- Modify: `.github/workflows/deploy-validate.yml` (the consistency step; `setup-helm` inputs), `.github/workflows/image-scan.yml`, `.github/workflows/publish-images.yml` (`setup-helm` inputs `v3.15.0` → `v3.15.4`, so GitHub's helm matches GitLab's `alpine/helm:3.15.4`).

**Interfaces:** Consumes every pin location Tasks 2–10 created (Global row 45).

- [ ] **Step 1: Apply the patch.** `pins.py` (stdlib only) reads the table's own YAML subset; every match of every location's regex must agree (a leading `v` is ignored); `freshness` opens, edits or closes one `Pin behind: <name>` issue per row through `gh`, skips the EKS rows without `PIN_FRESHNESS_AWS_ROLE`, and never touches `dependabot` rows; network and process calls are injectable for the tests. The table has 31 rows: those the spec lists, with every location (including the setup scripts' comments that state the kind version and the kindest/node pin), and one row per GitLab image.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t11.patch <<'T11_PATCH'
diff --git a/.github/workflows/deploy-validate.yml b/.github/workflows/deploy-validate.yml
index f905be3b..183b8121 100644
--- a/.github/workflows/deploy-validate.yml
+++ b/.github/workflows/deploy-validate.yml
@@ -20,10 +20,15 @@ jobs:
     steps:
       - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
 
+      # CSR round 12 §4.5: every version pin written in more than one place (ci/pins.yaml) holds the same
+      # version everywhere.
+      - name: Pin table consistency
+        run: python3 ci/pins.py consistency
+
       - name: Set up Helm
         uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
         with:
-          version: v3.15.0
+          version: v3.15.4
 
       - name: helm dependency build
         run: helm dependency build Iverson.Server/deploy/helm/iverson
@@ -75,7 +80,7 @@ jobs:
       - name: Set up Helm
         uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
         with:
-          version: v3.15.0
+          version: v3.15.4
 
       - name: Install kubeconform
         run: |
@@ -130,7 +135,7 @@ jobs:
       - name: Set up Helm
         uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
         with:
-          version: v3.15.0
+          version: v3.15.4
 
       - name: Install kube-score
         run: |
diff --git a/.github/workflows/image-scan.yml b/.github/workflows/image-scan.yml
index 007dae6e..baab6194 100644
--- a/.github/workflows/image-scan.yml
+++ b/.github/workflows/image-scan.yml
@@ -47,7 +47,7 @@ jobs:
       - name: Set up Helm
         uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
         with:
-          version: v3.15.0
+          version: v3.15.4
       - name: List the chart's third-party images
         run: |
           set -eo pipefail
diff --git a/.github/workflows/pin-freshness.yml b/.github/workflows/pin-freshness.yml
new file mode 100644
index 00000000..f365d765
--- /dev/null
+++ b/.github/workflows/pin-freshness.yml
@@ -0,0 +1,41 @@
+name: Pin Freshness
+
+# CSR round 12 §4.5: for each pin in ci/pins.yaml that Dependabot does not raise, reads the newest version
+# from its source and opens or updates one issue "Pin behind: <name>" while the pin is behind, closing it
+# once the pin is current. It changes no code. The EKS add-on rows need the read-only AWS role from
+# Iverson.Server/deploy/terraform/bootstrap/aws (repository variable PIN_FRESHNESS_AWS_ROLE); without it
+# they report "skipped: no AWS role" and the other rows still run.
+on:
+  schedule:
+    - cron: '15 5 * * 1'
+  workflow_dispatch: {}
+
+permissions:
+  contents: read
+  issues: write
+  id-token: write
+
+jobs:
+  freshness:
+    runs-on: ubuntu-latest
+    steps:
+      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
+        with:
+          persist-credentials: false
+      # For the chart-app-version and cnpg-default rows (helm show chart).
+      - name: Set up Helm
+        uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
+        with:
+          version: v3.15.4
+      - name: Assume the pin-freshness AWS role
+        if: vars.PIN_FRESHNESS_AWS_ROLE != ''
+        uses: aws-actions/configure-aws-credentials@e1253824e5c10ff9df46874f81ed3ec929e19cfd # v6.3.0
+        with:
+          role-to-assume: ${{ vars.PIN_FRESHNESS_AWS_ROLE }}
+          aws-region: us-east-1
+      - name: Report stale pins
+        env:
+          GH_TOKEN: ${{ github.token }}
+          GITHUB_TOKEN: ${{ github.token }}
+          PIN_FRESHNESS_AWS_ROLE: ${{ vars.PIN_FRESHNESS_AWS_ROLE }}
+        run: python3 ci/pins.py freshness
diff --git a/.github/workflows/publish-images.yml b/.github/workflows/publish-images.yml
index 3ec06753..51079e88 100644
--- a/.github/workflows/publish-images.yml
+++ b/.github/workflows/publish-images.yml
@@ -31,7 +31,7 @@ jobs:
       - name: Set up Helm
         uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
         with:
-          version: v3.15.0
+          version: v3.15.4
       - name: List the images
         id: list
         run: |
diff --git a/ci/pins.py b/ci/pins.py
new file mode 100644
index 00000000..93d84ed2
--- /dev/null
+++ b/ci/pins.py
@@ -0,0 +1,280 @@
+#!/usr/bin/env python3
+"""The version pins outside Dependabot's reach (CSR round 12 §4.5), from ci/pins.yaml.
+
+    python3 ci/pins.py consistency   # every row's locations hold the same version; exit 1 on a mismatch
+    python3 ci/pins.py freshness     # one issue "Pin behind: <name>" per stale row (needs gh and GH_TOKEN)
+
+Each row of ci/pins.yaml names a pin, its source (where the newest version is read) and its locations: a file
+and a regular expression whose one group is the version. Every match of a location's expression in its file
+counts, and every match of every location must agree. A leading "v" is ignored when versions are compared.
+
+Sources:
+    github-release <owner/repo>                  the latest release's tag
+    pypi <package>                               the package's current version
+    dockerhub-tag <repo> <series>                the newest <series>.N tag (kindest/node's patch releases)
+    eks-addon <name>                             the newest add-on version compatible with the EKS cluster's
+                                                 Kubernetes version (needs the AWS role; skipped without it)
+    cnpg-default <chart>                         CNPG's DefaultImageName tag at the operators stub chart's appVersion
+    chart-app-version <chart>                    the operators stub chart's appVersion
+    maven-central <groupId:artifactId> <series>  the newest release in maven-metadata.xml within the series
+    go-proxy <module>                            the Version of proxy.golang.org's @latest
+    dependabot                                   Dependabot tracks it; only the consistency check reads the row
+
+Standard library only. Stdout carries one line per row; nothing here prints a token."""
+import json
+import os
+import re
+import subprocess
+import sys
+import urllib.request
+import xml.etree.ElementTree as ET
+
+ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
+TABLE = os.path.join(ROOT, "ci", "pins.yaml")
+OPERATOR_STUB = "Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml"
+EKS_VARIABLES = "Iverson.Server/deploy/terraform/modules/cluster-aws/variables.tf"
+ISSUE_PREFIX = "Pin behind: "
+
+
+class PinError(Exception):
+    pass
+
+
+# --- the table ---------------------------------------------------------------------------------------------
+
+def _scalar(value):
+    value = value.strip()
+    if value.startswith("'"):
+        if len(value) < 2 or not value.endswith("'"):
+            raise PinError(f"unterminated quoted value: {value}")
+        return value[1:-1].replace("''", "'")
+    return value
+
+
+def load_table(text):
+    """Reads the table's own YAML subset: a list of rows, each `- name:`, `  source:`, optional `  note:`,
+    and `  locations:` with `    - file:` / `      regex:` pairs. Values are plain or single-quoted."""
+    rows, row, location = [], None, None
+    for number, line in enumerate(text.splitlines(), 1):
+        if not line.strip() or line.lstrip().startswith("#"):
+            continue
+        if m := re.fullmatch(r"- name: (.+)", line):
+            row, location = {"name": _scalar(m[1]), "locations": []}, None
+            rows.append(row)
+        elif row is not None and (m := re.fullmatch(r"  (source|note): (.+)", line)):
+            row[m[1]] = _scalar(m[2])
+        elif row is not None and line == "  locations:":
+            pass
+        elif row is not None and (m := re.fullmatch(r"    - file: (.+)", line)):
+            location = {"file": _scalar(m[1])}
+            row["locations"].append(location)
+        elif location is not None and (m := re.fullmatch(r"      regex: (.+)", line)):
+            location["regex"] = _scalar(m[1])
+        else:
+            raise PinError(f"pins.yaml line {number} is not in the table's format: {line!r}")
+    names = [r["name"] for r in rows]
+    for r in rows:
+        if "source" not in r or not r["locations"]:
+            raise PinError(f"row {r['name']}: needs a source and at least one location")
+        if names.count(r["name"]) > 1:
+            raise PinError(f"row {r['name']}: the name is used twice")
+        for loc in r["locations"]:
+            if "regex" not in loc:
+                raise PinError(f"row {r['name']}: location {loc['file']} has no regex")
+            if re.compile(loc["regex"], re.M).groups != 1:
+                raise PinError(f"row {r['name']}: the regex for {loc['file']} must have exactly one group")
+    return rows
+
+
+def norm(version):
+    return version[1:] if version.startswith("v") else version
+
+
+def found(row, root):
+    """Every (file, version) the row's locations match, or a PinError naming a location that matches nothing."""
+    out = []
+    for loc in row["locations"]:
+        try:
+            text = open(os.path.join(root, loc["file"]), encoding="utf-8").read()
+        except OSError as e:
+            raise PinError(f"{row['name']}: cannot read {loc['file']}: {e.strerror}")
+        values = re.findall(loc["regex"], text, re.M)
+        if not values:
+            raise PinError(f"{row['name']}: {loc['file']} has no match for {loc['regex']}")
+        out += [(loc["file"], v) for v in values]
+    return out
+
+
+def consistency(rows, root):
+    errors = []
+    for row in rows:
+        try:
+            values = found(row, root)
+        except PinError as e:
+            errors.append(str(e))
+            continue
+        if len({norm(v) for _, v in values}) > 1:
+            listing = ", ".join(f"{f}={v}" for f, v in values)
+            errors.append(f"{row['name']}: the locations disagree: {listing}")
+        else:
+            print(f"ok {row['name']} {values[0][1] if values else '(no locations)'}")
+    return errors
+
+
+# --- sources -----------------------------------------------------------------------------------------------
+
+def http_get(url):
+    headers = {"User-Agent": "iverson-pins"}
+    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
+        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
+    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
+        return response.read().decode("utf-8")
+
+
+def run(argv):
+    return subprocess.run(argv, check=True, capture_output=True, text=True).stdout
+
+
+def version_key(version):
+    return tuple(int(n) for n in re.findall(r"\d+", version))
+
+
+def stub_chart(chart, root):
+    """The operators stub's (version, repository) for a chart."""
+    text = open(os.path.join(root, OPERATOR_STUB), encoding="utf-8").read()
+    m = re.search(rf"- name: {re.escape(chart)}\n\s+version: (\S+)\n\s+repository: (\S+)", text)
+    if not m:
+        raise PinError(f"no {chart} entry in {OPERATOR_STUB}")
+    return m[1], m[2]
+
+
+def chart_app_version(chart, root, run):
+    version, repository = stub_chart(chart, root)
+    m = re.search(r"^appVersion: \"?([^\"\s]+)\"?$", run(["helm", "show", "chart", chart, "--repo", repository, "--version", version]), re.M)
+    if not m:
+        raise PinError(f"helm show chart {chart} {version} prints no appVersion")
+    return m[1]
+
+
+def eks_kubernetes_version(root):
+    text = open(os.path.join(root, EKS_VARIABLES), encoding="utf-8").read()
+    m = re.search(r'variable "kubernetes_version" \{[^}]*default\s*=\s*"([^"]+)"', text)
+    if not m:
+        raise PinError(f"no kubernetes_version default in {EKS_VARIABLES}")
+    return m[1]
+
+
+def newest(source, root, http=http_get, run=run):
+    """The newest version the source offers, for comparison with the pinned one."""
+    kind, *args = source.split()
+    if kind == "github-release":
+        return json.loads(http(f"https://api.github.com/repos/{args[0]}/releases/latest"))["tag_name"]
+    if kind == "pypi":
+        return json.loads(http(f"https://pypi.org/pypi/{args[0]}/json"))["info"]["version"]
+    if kind == "dockerhub-tag":
+        repo, series = args
+        pattern = re.compile(re.escape(series) + r"\.(\d+)")
+        url, tags = f"https://hub.docker.com/v2/repositories/{repo}/tags?page_size=100", []
+        while url:
+            page = json.loads(http(url))
+            tags += [t["name"] for t in page["results"] if pattern.fullmatch(t["name"])]
+            url = page.get("next")
+        if not tags:
+            raise PinError(f"Docker Hub lists no {repo} tag in the {series} series")
+        return max(tags, key=version_key)
+    if kind == "eks-addon":
+        out = json.loads(run(["aws", "eks", "describe-addon-versions", "--addon-name", args[0],
+                              "--kubernetes-version", eks_kubernetes_version(root), "--output", "json"]))
+        versions = [v["addonVersion"] for a in out["addons"] for v in a["addonVersions"]]
+        if not versions:
+            raise PinError(f"AWS lists no {args[0]} version for this Kubernetes version")
+        return max(versions, key=version_key)
+    if kind == "chart-app-version":
+        return chart_app_version(args[0], root, run)
+    if kind == "cnpg-default":
+        app = chart_app_version(args[0], root, run)
+        text = http(f"https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v{norm(app)}/pkg/versions/versions.go")
+        m = re.search(r'^\s*DefaultImageName = "[^":]+:([^"]+)"$', text, re.M)
+        if not m:
+            raise PinError(f"CNPG {app}'s versions.go has no DefaultImageName")
+        return m[1]
+    if kind == "maven-central":
+        coordinates, series = args
+        group, artifact = coordinates.split(":")
+        root_el = ET.fromstring(http(f"https://repo1.maven.org/maven2/{group.replace('.', '/')}/{artifact}/maven-metadata.xml"))
+        releases = [v.text for v in root_el.iter("version")
+                    if re.fullmatch(re.escape(series) + r"(\.\d+)+", v.text or "")]
+        if not releases:
+            raise PinError(f"Maven Central lists no {coordinates} release in the {series} series")
+        return max(releases, key=version_key)
+    if kind == "go-proxy":
+        return json.loads(http(f"https://proxy.golang.org/{args[0]}/@latest"))["Version"]
+    raise PinError(f"unknown source {source!r}")
+
+
+# --- freshness ---------------------------------------------------------------------------------------------
+
+def open_issue(name, run):
+    title = ISSUE_PREFIX + name
+    issues = json.loads(run(["gh", "issue", "list", "--state", "open", "--search", f'"{title}" in:title',
+                             "--json", "number,title", "--limit", "100"]))
+    numbers = [i["number"] for i in issues if i["title"] == title]
+    return numbers[0] if numbers else None
+
+
+def freshness(rows, root, http=http_get, run=run, aws_role=None):
+    """Opens or updates one issue per stale row and closes it when the row is current. A row whose source
+    cannot be read, and an EKS row without the AWS role, leave its issue as it is. Returns the error count."""
+    errors = 0
+    for row in rows:
+        kind = row["source"].split()[0]
+        if kind == "dependabot":
+            continue
+        if kind == "eks-addon" and not aws_role:
+            print(f"skipped: no AWS role {row['name']}")
+            continue
+        try:
+            pinned = found(row, root)[0][1]
+            latest = newest(row["source"], root, http=http, run=run)
+        except (PinError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as e:
+            print(f"error {row['name']}: {e}")
+            errors += 1
+            continue
+        number = open_issue(row["name"], run)
+        title = ISSUE_PREFIX + row["name"]
+        if norm(pinned) != norm(latest):
+            body = (f"`{row['name']}` is pinned at `{pinned}`; its source (`{row['source']}`) offers `{latest}`.\n\n"
+                    "Locations (ci/pins.yaml):\n" + "".join(f"- `{l['file']}`\n" for l in row["locations"]) +
+                    "\nRaise every location together (`python3 ci/pins.py consistency` checks them), or record "
+                    "why the pin stays in the row's `note:`. docs/runbooks/csr12-supply-chain.md describes both.")
+            if number is None:
+                run(["gh", "issue", "create", "--title", title, "--body", body])
+            else:
+                run(["gh", "issue", "edit", str(number), "--body", body])
+            print(f"stale {row['name']} {pinned} -> {latest}")
+        else:
+            if number is not None:
+                run(["gh", "issue", "close", str(number), "--comment", f"Current: `{pinned}`."])
+            print(f"current {row['name']} {pinned}")
+    return errors
+
+
+def main(argv):
+    if len(argv) != 2 or argv[1] not in ("consistency", "freshness"):
+        print(__doc__.split("\n\n")[1], file=sys.stderr)
+        return 2
+    try:
+        rows = load_table(open(TABLE, encoding="utf-8").read())
+    except PinError as e:
+        print(f"error: {e}", file=sys.stderr)
+        return 1
+    if argv[1] == "consistency":
+        errors = consistency(rows, ROOT)
+        for e in errors:
+            print(f"error: {e}", file=sys.stderr)
+        return 1 if errors else 0
+    return 1 if freshness(rows, ROOT, aws_role=os.environ.get("PIN_FRESHNESS_AWS_ROLE")) else 0
+
+
+if __name__ == "__main__":
+    sys.exit(main(sys.argv))
diff --git a/ci/pins.yaml b/ci/pins.yaml
new file mode 100644
index 00000000..b97c458a
--- /dev/null
+++ b/ci/pins.yaml
@@ -0,0 +1,265 @@
+# CSR round 12 §4.5: every version pin Dependabot does not raise, with where its newest version is read
+# (`source`) and every place it is written (`locations`: a file and a regex whose one group is the version).
+# ci/pins.py reads this file: `consistency` (deploy-validate.yml, every pull request) fails when a row's
+# locations disagree, and `freshness` (pin-freshness.yml, weekly) opens one issue "Pin behind: <name>" per
+# stale row. Rows with source `dependabot` are pins Dependabot raises in one location; the consistency check
+# keeps their other locations equal. Format: the subset of YAML pins.py reads (plain or single-quoted values,
+# two-space indents, as below). A pin that must stay behind keeps its row and gains a `note:` saying why.
+
+- name: gateway-api
+  source: github-release kubernetes-sigs/gateway-api
+  locations:
+    - file: Iverson.Server/deploy/kind/setup.sh
+      regex: 'gateway-api/releases/download/(v[0-9.]+)/standard-install\.yaml'
+    - file: Iverson.Server/deploy/kind/setup.ps1
+      regex: 'gateway-api/releases/download/(v[0-9.]+)/standard-install\.yaml'
+
+- name: kind
+  source: github-release kubernetes-sigs/kind
+  locations:
+    - file: Iverson.Server/deploy/kind/setup.sh
+      regex: '^KIND_MIN=(v[0-9.]+)$'
+    - file: Iverson.Server/deploy/kind/setup.ps1
+      regex: '^\$KindMin = \[version\]"([0-9.]+)"$'
+    - file: Iverson.Server/deploy/kind/kind-config.yaml
+      regex: 'Run with kind (v[0-9.]+),'
+    - file: Iverson.Server/deploy/kind/setup.sh
+      regex: 'which was run with kind (v[0-9.]+);'
+    - file: Iverson.Server/deploy/kind/setup.ps1
+      regex: 'which was run with kind (v[0-9.]+);'
+
+- name: kindest-node
+  source: dockerhub-tag kindest/node v1.35
+  locations:
+    - file: Iverson.Server/deploy/kind/kind-config.yaml
+      regex: 'image: kindest/node:(v[0-9.]+)@sha256:'
+    - file: Iverson.Server/deploy/kind/setup.sh
+      regex: 'kind-config\.yaml pins kindest/node:(v[0-9.]+),'
+    - file: Iverson.Server/deploy/kind/setup.ps1
+      regex: 'kind-config\.yaml pins kindest/node:(v[0-9.]+),'
+
+- name: istio
+  source: github-release istio/istio
+  locations:
+    - file: Iverson.Server/deploy/terraform/modules/operators/charts/istio/vendor-istio-charts.sh
+      regex: '^VERSION=([0-9.]+)$'
+    - file: Iverson.Server/deploy/terraform/modules/operators/charts/istio/base/Chart.yaml
+      regex: '^version: ([0-9.]+)$'
+    - file: Iverson.Server/deploy/terraform/modules/operators/charts/istio/istio-cni/Chart.yaml
+      regex: '^version: ([0-9.]+)$'
+    - file: Iverson.Server/deploy/terraform/modules/operators/charts/istio/istio-discovery/Chart.yaml
+      regex: '^version: ([0-9.]+)$'
+    - file: Iverson.Server/deploy/terraform/modules/operators/charts/istio/ztunnel/Chart.yaml
+      regex: '^version: ([0-9.]+)$'
+
+- name: policy-controller
+  source: github-release sigstore/policy-controller
+  locations:
+    - file: Iverson.Server/deploy/terraform/modules/operators/main.tf
+      regex: 'image = \{ version = "(v[0-9.]+)@sha256:'
+
+- name: cnpg-postgresql
+  source: cnpg-default cloudnative-pg
+  locations:
+    - file: Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml
+      regex: '^imageName: "ghcr\.io/cloudnative-pg/postgresql:([^@"]+)@sha256:'
+
+- name: cnpg-operator
+  source: chart-app-version cloudnative-pg
+  locations:
+    - file: Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml
+      regex: '^  tag: "([0-9.]+)@sha256:'
+
+- name: maven
+  source: maven-central org.apache.maven:apache-maven 3.9
+  locations:
+    - file: Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties
+      regex: '^distributionUrl=.*/apache-maven-([0-9.]+)-bin\.zip$'
+
+- name: maven-wrapper
+  source: maven-central org.apache.maven.wrapper:maven-wrapper 3
+  locations:
+    - file: Iverson.Clients/Java/.mvn/wrapper/maven-wrapper.properties
+      regex: '^wrapperVersion=([0-9.]+)$'
+    - file: Iverson.Clients/Java/mvnw
+      regex: 'Apache Maven Wrapper startup batch script, version ([0-9.]+)$'
+    - file: Iverson.Clients/Java/mvnw.cmd
+      regex: 'Apache Maven Wrapper startup batch script, version ([0-9.]+)$'
+    - file: Iverson.Clients/Java/.mvn/wrapper/MavenWrapperDownloader.java
+      regex: 'WRAPPER_VERSION = "([0-9.]+)";'
+
+- name: vpc-cni
+  source: eks-addon vpc-cni
+  locations:
+    - file: Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf
+      regex: 'addon_name += "vpc-cni"(?:\n.*)*?\n *addon_version += "([^"]+)"'
+
+- name: aws-ebs-csi-driver
+  source: eks-addon aws-ebs-csi-driver
+  locations:
+    - file: Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf
+      regex: 'addon_name += "aws-ebs-csi-driver"(?:\n.*)*?\n *addon_version += "([^"]+)"'
+
+- name: govulncheck
+  source: go-proxy golang.org/x/vuln
+  locations:
+    - file: .github/workflows/dependency-scan.yml
+      regex: 'golang\.org/x/vuln/cmd/govulncheck@(v[0-9.]+)'
+    - file: .gitlab-ci.yml
+      regex: 'golang\.org/x/vuln/cmd/govulncheck@(v[0-9.]+)'
+
+- name: kubeconform
+  source: github-release yannh/kubeconform
+  locations:
+    - file: .github/workflows/deploy-validate.yml
+      regex: 'kubeconform/releases/download/(v[0-9.]+)/'
+    - file: .gitlab-ci.yml
+      regex: 'kubeconform/releases/download/(v[0-9.]+)/'
+
+- name: kube-score
+  source: github-release zegl/kube-score
+  locations:
+    - file: .github/workflows/deploy-validate.yml
+      regex: 'kube-score/releases/download/(v[0-9.]+)/'
+    - file: .gitlab-ci.yml
+      regex: 'kube-score/releases/download/(v[0-9.]+)/'
+
+- name: tfsec
+  source: github-release aquasecurity/tfsec
+  locations:
+    - file: .github/workflows/deploy-validate.yml
+      regex: 'tfsec/releases/download/(v[0-9.]+)/'
+    - file: .gitlab-ci.yml
+      regex: 'tfsec/releases/download/(v[0-9.]+)/'
+
+- name: trivy
+  source: github-release aquasecurity/trivy
+  locations:
+    - file: .github/workflows/image-scan.yml
+      regex: 'uses: aquasecurity/trivy-action@[0-9a-f]+ # v[0-9.]+\n *with:\n *image-ref: .*\n *version: (v[0-9.]+)$'
+    - file: .github/workflows/image-scan.yml
+      regex: 'docker\.io/aquasec/trivy:([0-9.]+)@sha256:'
+
+- name: cosign
+  source: github-release sigstore/cosign
+  locations:
+    - file: .github/workflows/publish-images.yml
+      regex: 'cosign-release: (v[0-9.]+)$'
+
+- name: helm
+  source: github-release helm/helm
+  locations:
+    - file: .github/workflows/deploy-validate.yml
+      regex: 'uses: azure/setup-helm@[0-9a-f]+ # v[0-9.]+\n *with:\n *version: (v[0-9.]+)$'
+    - file: .github/workflows/image-scan.yml
+      regex: 'uses: azure/setup-helm@[0-9a-f]+ # v[0-9.]+\n *with:\n *version: (v[0-9.]+)$'
+    - file: .github/workflows/publish-images.yml
+      regex: 'uses: azure/setup-helm@[0-9a-f]+ # v[0-9.]+\n *with:\n *version: (v[0-9.]+)$'
+    - file: .github/workflows/pin-freshness.yml
+      regex: 'uses: azure/setup-helm@[0-9a-f]+ # v[0-9.]+\n *with:\n *version: (v[0-9.]+)$'
+    - file: .gitlab-ci.yml
+      regex: 'image: alpine/helm:([0-9.]+)@sha256:'
+
+- name: terraform
+  source: github-release hashicorp/terraform
+  locations:
+    - file: .github/workflows/deploy-validate.yml
+      regex: 'terraform_version: "([0-9.]+)"'
+    - file: .gitlab-ci.yml
+      regex: 'image: hashicorp/terraform:([0-9.]+)@sha256:'
+
+- name: pip
+  source: pypi pip
+  locations:
+    - file: .github/workflows/dependency-scan.yml
+      regex: '\bpip==([0-9.]+)'
+    - file: .gitlab-ci.yml
+      regex: '\bpip==([0-9.]+)'
+
+- name: setuptools
+  source: pypi setuptools
+  locations:
+    - file: .github/workflows/dependency-scan.yml
+      regex: '\bsetuptools==([0-9.]+)'
+    - file: .gitlab-ci.yml
+      regex: '\bsetuptools==([0-9.]+)'
+
+- name: pip-audit
+  source: pypi pip-audit
+  locations:
+    - file: .github/workflows/dependency-scan.yml
+      regex: '\bpip-audit==([0-9.]+)'
+    - file: .gitlab-ci.yml
+      regex: '\bpip-audit==([0-9.]+)'
+
+- name: kubectl
+  source: github-release kubernetes/kubernetes
+  locations:
+    - file: Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml
+      regex: 'image: "registry\.k8s\.io/kubectl:(v[0-9.]+)"'
+
+- name: dependency-check-maven
+  source: dependabot
+  locations:
+    - file: Iverson.Clients/Java/pom.xml
+      regex: '<artifactId>dependency-check-maven</artifactId>\s*<version>([0-9.]+)</version>'
+    - file: .github/workflows/dependency-scan.yml
+      regex: 'org\.owasp:dependency-check-maven:([0-9.]+):update-only'
+    - file: .gitlab-ci.yml
+      regex: 'org\.owasp:dependency-check-maven:([0-9.]+):update-only'
+
+- name: gitlab-image-alpine-helm
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM alpine/helm:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: alpine/helm:(\S+)$'
+
+- name: gitlab-image-terraform
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM hashicorp/terraform:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: hashicorp/terraform:(\S+)$'
+
+- name: gitlab-image-dotnet-sdk
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM mcr\.microsoft\.com/dotnet/sdk:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: mcr\.microsoft\.com/dotnet/sdk:(\S+)$'
+
+- name: gitlab-image-node
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM node:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: node:(\S+)$'
+
+- name: gitlab-image-golang
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM golang:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: golang:(\S+)$'
+
+- name: gitlab-image-maven
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM maven:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: maven:(\S+)$'
+
+- name: gitlab-image-python
+  source: dependabot
+  locations:
+    - file: ci/gitlab-images/Dockerfile
+      regex: '^FROM python:(\S+)$'
+    - file: .gitlab-ci.yml
+      regex: '^ *image: python:(\S+)$'
diff --git a/ci/test_pins.py b/ci/test_pins.py
new file mode 100644
index 00000000..c6a455ab
--- /dev/null
+++ b/ci/test_pins.py
@@ -0,0 +1,280 @@
+"""pytest suite for ci/pins.py (CSR round 12 §4.5). Run with:
+
+    python3 -m pytest ci/test_pins.py -q
+
+No network: the sources read recorded responses (trimmed copies of the real ones, 2026-10-26), and gh, helm
+and aws are fakes that record their arguments."""
+import json
+import os
+import sys
+
+import pytest
+
+sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
+import pins  # noqa: E402
+
+ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
+
+
+def table(text):
+    return pins.load_table(text)
+
+
+def write(root, path, text):
+    full = os.path.join(root, path)
+    os.makedirs(os.path.dirname(full), exist_ok=True)
+    with open(full, "w") as f:
+        f.write(text)
+
+
+# --- the table and the consistency check ---------------------------------------------------------------------
+
+ROW = """- name: tool
+  source: github-release owner/tool
+  locations:
+    - file: a.yml
+      regex: 'tool@(v[0-9.]+)'
+    - file: b.yml
+      regex: 'tool:([0-9.]+)'
+"""
+
+
+def test_the_table_reads_rows_with_quoted_regexes():
+    rows = table("# comment\n\n" + ROW)
+    assert rows == [{"name": "tool", "source": "github-release owner/tool", "locations": [
+        {"file": "a.yml", "regex": "tool@(v[0-9.]+)"}, {"file": "b.yml", "regex": "tool:([0-9.]+)"}]}]
+
+
+@pytest.mark.parametrize("bad, message", [
+    ("- name: x\n  locations:\n    - file: a\n      regex: '(a)'\n", "needs a source"),
+    ("- name: x\n  source: dependabot\n  locations:\n    - file: a\n      regex: 'a'\n", "exactly one group"),
+    ("- name: x\n  source: dependabot\n  locations:\n    - file: a\n", "has no regex"),
+    ("- name: x\n  source: dependabot\n   oops: 1\n", "not in the table's format"),
+    (ROW + ROW, "used twice"),
+])
+def test_the_table_refuses_a_malformed_row(bad, message):
+    with pytest.raises(pins.PinError, match=message):
+        table(bad)
+
+
+def test_consistency_passes_when_every_match_agrees_ignoring_a_leading_v(tmp_path):
+    write(tmp_path, "a.yml", "run: tool@v1.2.3\nagain: tool@v1.2.3\n")
+    write(tmp_path, "b.yml", "image: tool:1.2.3\n")
+    assert pins.consistency(table(ROW), str(tmp_path)) == []
+
+
+def test_consistency_fails_on_a_second_match_in_one_file_that_differs(tmp_path):
+    write(tmp_path, "a.yml", "run: tool@v1.2.3\nagain: tool@v1.2.4\n")
+    write(tmp_path, "b.yml", "image: tool:1.2.3\n")
+    (error,) = pins.consistency(table(ROW), str(tmp_path))
+    assert "tool: the locations disagree" in error and "a.yml=v1.2.4" in error
+
+
+def test_consistency_fails_when_a_location_matches_nothing(tmp_path):
+    write(tmp_path, "a.yml", "run: tool@v1.2.3\n")
+    write(tmp_path, "b.yml", "image: other:1.2.3\n")
+    (error,) = pins.consistency(table(ROW), str(tmp_path))
+    assert "b.yml has no match" in error
+
+
+def test_the_repositorys_table_is_consistent():
+    rows = pins.load_table(open(pins.TABLE).read())
+    assert pins.consistency(rows, ROOT) == []
+
+
+@pytest.mark.parametrize("row, path, old, new", [
+    ("istio", "Iverson.Server/deploy/terraform/modules/operators/charts/istio/ztunnel/Chart.yaml", "version: 1.31.2", "version: 1.31.3"),
+    ("gitlab-image-node", ".gitlab-ci.yml", "image: node:22@sha256:0e5f", "image: node:22@sha256:1e5f"),
+    ("pip-audit", ".gitlab-ci.yml", "pip-audit==2.10.1", "pip-audit==2.10.0"),
+])
+def test_consistency_catches_a_one_location_change_in_the_repositorys_table(tmp_path, row, path, old, new):
+    rows = [r for r in pins.load_table(open(pins.TABLE).read()) if r["name"] == row]
+    for loc in rows[0]["locations"]:
+        write(tmp_path, loc["file"], open(os.path.join(ROOT, loc["file"])).read())
+    text = open(os.path.join(tmp_path, path)).read()
+    assert text.count(old) >= 1
+    write(tmp_path, path, text.replace(old, new, 1))
+    (error,) = pins.consistency(rows, str(tmp_path))
+    assert f"{row}: the locations disagree" in error
+
+
+# --- sources, against recorded responses ---------------------------------------------------------------------
+
+RECORDED = {
+    "https://api.github.com/repos/kubernetes-sigs/kind/releases/latest":
+        '{"tag_name": "v0.33.0", "name": "v0.33.0", "draft": false, "prerelease": false}',
+    "https://pypi.org/pypi/pip-audit/json": '{"info": {"name": "pip-audit", "version": "2.10.1"}}',
+    "https://hub.docker.com/v2/repositories/kindest/node/tags?page_size=100": json.dumps({
+        "count": 179, "next": "https://hub.docker.com/v2/repositories/kindest/node/tags?page=2&page_size=100",
+        "results": [{"name": n} for n in ["v1.37.0", "v1.36.4", "v1.35.8", "v1.34.11", "v1.37.0-rc.1", "v1.35.5"]]}),
+    "https://hub.docker.com/v2/repositories/kindest/node/tags?page=2&page_size=100": json.dumps({
+        # Page 2 is not recorded: its tags are chosen to test the series filter (a later patch, a pre-release
+        # numbered above it, and a tag whose prefix only looks like the series).
+        "count": 179, "next": None, "results": [{"name": n} for n in ["v1.35.10", "v1.35.11-rc.0", "v1.3.5"]]}),
+    "https://proxy.golang.org/golang.org/x/vuln/@latest":
+        '{"Version":"v1.9.0","Time":"2026-10-11T03:39:53Z","Origin":{"VCS":"git","Ref":"refs/tags/v1.9.0"}}',
+    "https://repo1.maven.org/maven2/org/apache/maven/apache-maven/maven-metadata.xml": """<?xml version="1.0" encoding="UTF-8"?>
+<metadata><groupId>org.apache.maven</groupId><artifactId>apache-maven</artifactId><versioning>
+<latest>4.0.0-rc-7</latest><release>4.0.0-rc-7</release><versions>
+<version>3.9.9</version><version>3.9.15</version><version>3.9.16</version><version>3.10.0</version>
+<version>4.0.0-beta-5</version><version>4.0.0-rc-7</version></versions></versioning></metadata>""",
+    "https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v1.30.0/pkg/versions/versions.go": """package versions
+
+const (
+\t// Version is the version of the operator
+\tVersion = "1.30.0"
+
+\t// DefaultImageName is the default image used by the operator to create pods
+\tDefaultImageName = "ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie"
+
+\t// DefaultOperatorImageName is the default operator image used by the controller in the pods running PostgreSQL
+\tDefaultOperatorImageName = "ghcr.io/cloudnative-pg/cloudnative-pg:1.30.0"
+)
+""",
+}
+
+HELM_SHOW_CHART = """apiVersion: v2
+appVersion: 1.30.0
+description: CloudNativePG Operator Helm Chart
+name: cloudnative-pg
+version: 0.29.0
+"""
+
+# The shape of `aws eks describe-addon-versions --output json` (AWS CLI reference).
+EKS = json.dumps({"addons": [{"addonName": "vpc-cni", "addonVersions": [
+    {"addonVersion": "v1.23.1-eksbuild.1", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": True}]},
+    {"addonVersion": "v1.23.10-eksbuild.2", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": False}]},
+    {"addonVersion": "v1.23.9-eksbuild.1", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": False}]}]}]})
+
+
+class Fakes:
+    """http() answers from RECORDED; run() answers helm, aws and gh and records every argv."""
+
+    def __init__(self, issues=()):
+        self.calls = []
+        self.issues = list(issues)
+
+    def http(self, url):
+        return RECORDED[url]
+
+    def run(self, argv):
+        self.calls.append(argv)
+        if argv[:3] == ["helm", "show", "chart"]:
+            assert argv[3:] == ["cloudnative-pg", "--repo", "https://cloudnative-pg.github.io/charts", "--version", "0.29.0"]
+            return HELM_SHOW_CHART
+        if argv[:3] == ["aws", "eks", "describe-addon-versions"]:
+            assert argv[argv.index("--kubernetes-version") + 1] == "1.35"
+            return EKS
+        if argv[:3] == ["gh", "issue", "list"]:
+            return json.dumps(self.issues)
+        return ""
+
+    def gh(self):
+        return [c for c in self.calls if c[0] == "gh" and c[2] != "list"]
+
+
+@pytest.mark.parametrize("source, expected", [
+    ("github-release kubernetes-sigs/kind", "v0.33.0"),
+    ("pypi pip-audit", "2.10.1"),
+    ("go-proxy golang.org/x/vuln", "v1.9.0"),
+    ("maven-central org.apache.maven:apache-maven 3.9", "3.9.16"),
+    ("chart-app-version cloudnative-pg", "1.30.0"),
+    ("cnpg-default cloudnative-pg", "18.4-system-trixie"),
+    ("eks-addon vpc-cni", "v1.23.10-eksbuild.2"),
+])
+def test_each_source_reads_the_newest_version(source, expected):
+    fakes = Fakes()
+    assert pins.newest(source, ROOT, http=fakes.http, run=fakes.run) == expected
+
+
+def test_the_kindest_node_series_keeps_the_newest_patch_of_its_own_minor_across_pages():
+    fakes = Fakes()
+    assert pins.newest("dockerhub-tag kindest/node v1.35", ROOT, http=fakes.http, run=fakes.run) == "v1.35.10"
+
+
+def test_maven_central_keeps_its_series_and_ignores_pre_releases():
+    fakes = Fakes()
+    newest = lambda series: pins.newest(f"maven-central org.apache.maven:apache-maven {series}", ROOT,
+                                        http=fakes.http, run=fakes.run)
+    assert newest("3.9") == "3.9.16"
+    assert newest("3") == "3.10.0"
+    with pytest.raises(pins.PinError, match="no org.apache.maven:apache-maven release in the 4 series"):
+        newest("4")
+
+
+# --- freshness and its issues --------------------------------------------------------------------------------
+
+FRESH_TABLE = """- name: kind
+  source: github-release kubernetes-sigs/kind
+  locations:
+    - file: kind.txt
+      regex: 'kind (v[0-9.]+)'
+- name: vpc-cni
+  source: eks-addon vpc-cni
+  locations:
+    - file: eks.txt
+      regex: 'vpc-cni ([^ ]+)$'
+- name: dependency-check-maven
+  source: dependabot
+  locations:
+    - file: pom.txt
+      regex: 'dc ([0-9.]+)'
+"""
+
+
+@pytest.fixture
+def fresh_root(tmp_path):
+    write(tmp_path, "pom.txt", "dc 13.0.0\n")
+    write(tmp_path, "eks.txt", "vpc-cni v1.23.1-eksbuild.1\n")
+    write(tmp_path, os.path.relpath(os.path.join(ROOT, pins.EKS_VARIABLES), ROOT),
+          open(os.path.join(ROOT, pins.EKS_VARIABLES)).read())
+    return tmp_path
+
+
+def run_freshness(root, kind_version, issues=(), aws_role=None):
+    write(root, "kind.txt", f"kind {kind_version}\n")
+    fakes = Fakes(issues)
+    errors = pins.freshness(table(FRESH_TABLE), str(root), http=fakes.http, run=fakes.run, aws_role=aws_role)
+    return errors, fakes
+
+
+def test_a_stale_row_without_an_issue_opens_one(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.32.0")
+    assert errors == 0
+    (call,) = fakes.gh()
+    assert call[:5] == ["gh", "issue", "create", "--title", "Pin behind: kind"]
+    assert "pinned at `v0.32.0`" in call[6] and "offers `v0.33.0`" in call[6]
+
+
+def test_a_stale_row_with_an_open_issue_updates_it(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.32.0", issues=[{"number": 7, "title": "Pin behind: kind"}])
+    (call,) = fakes.gh()
+    assert call[:5] == ["gh", "issue", "edit", "7", "--body"]
+
+
+def test_a_current_row_closes_its_open_issue(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.33.0", issues=[{"number": 7, "title": "Pin behind: kind"}])
+    (call,) = fakes.gh()
+    assert call[:4] == ["gh", "issue", "close", "7"]
+
+
+def test_a_current_row_with_no_issue_touches_nothing(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.33.0", issues=[{"number": 8, "title": "Pin behind: kind-extra"}])
+    assert fakes.gh() == []
+
+
+def test_without_the_aws_role_the_eks_rows_are_skipped_and_the_rest_run(fresh_root, capsys):
+    errors, fakes = run_freshness(fresh_root, "v0.32.0")
+    assert not any(c[0] == "aws" for c in fakes.calls)
+    assert [c[4] for c in fakes.gh()] == ["Pin behind: kind"]
+    assert "skipped: no AWS role vpc-cni" in capsys.readouterr().out
+
+
+def test_with_the_aws_role_the_eks_rows_are_checked(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.33.0", aws_role="arn:aws:iam::1:role/r")
+    assert [c[4] for c in fakes.gh()] == ["Pin behind: vpc-cni"]
+
+
+def test_dependabot_rows_never_reach_a_source_or_an_issue(fresh_root):
+    errors, fakes = run_freshness(fresh_root, "v0.33.0")
+    assert not any("dependency-check-maven" in " ".join(c) for c in fakes.calls)
T11_PATCH
cd $WT && git apply --check $SCR/t11.patch && git apply $SCR/t11.patch && git status --short
````

- [ ] **Step 2: Consistency and the tests.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
python3 ci/pins.py consistency > $SCR/t11-cons.log; echo "consistency rc=$?"; command grep -c '^ok ' $SCR/t11-cons.log
git grep -n 'v3\.15\.0' -- .github || echo "no setup-helm v3.15.0 left"
python3 -m pytest ci/test_pins.py -q -p no:cacheprovider 2>&1 | tail -1
python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/pin-freshness.yml')); print(d['permissions'], [s.get('if') for s in d['jobs']['freshness']['steps']])"
```

Expected: `consistency rc=0`, `31`; `no setup-helm v3.15.0 left`; `29 passed`; `{'contents': 'read', 'issues': 'write', 'id-token': 'write'}` and the AWS step's `vars.PIN_FRESHNESS_AWS_ROLE != ''`.

- [ ] **Step 3: The tests catch broken logic.** Each mutation is applied to a copy of `pins.py`, run, and reverted.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT; cp ci/pins.py $SCR/pins.bak
m() { python3 - "$1" "$2" <<'T11MUT'
import sys
s = open('ci/pins.py').read(); assert s.count(sys.argv[1]) == 1, sys.argv[1]; open('ci/pins.py', 'w').write(s.replace(sys.argv[1], sys.argv[2]))
T11MUT
  echo "[$3] $(python3 -m pytest ci/test_pins.py -q -p no:cacheprovider 2>&1 | tail -1)"; cp $SCR/pins.bak ci/pins.py; }
m 'return version[1:] if version.startswith("v") else version' 'return version' 'no v-normalising'
m 'if len({norm(v) for _, v in values}) > 1:' 'if len({norm(v) for _, v in values[:1]}) > 1:' 'compare first match only'
m 'if number is None:' 'if number is not None:' 'create and edit swapped'
m 'pattern = re.compile(re.escape(series) + r"\.(\d+)")' 'pattern = re.compile(re.escape(series) + r".*")' 'series filter loosened'
m 'if kind == "eks-addon" and not aws_role:' 'if kind == "eks-addon" and False:' 'no role skip'
cmp ci/pins.py $SCR/pins.bak && echo restored
```

Expected: every bracketed line reports at least `1 failed`; `restored`.

- [ ] **Step 4: Every source answers live** (reads only; `gh` is not called, no issue is touched).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT; python3 - <<'T11LIVE'
import sys; sys.path.insert(0, 'ci'); import pins
for r in pins.load_table(open('ci/pins.yaml').read()):
    if r['source'].split()[0] in ('dependabot', 'eks-addon'): continue
    pinned = pins.found(r, pins.ROOT)[0][1]
    try: latest = pins.newest(r['source'], pins.ROOT)
    except Exception as e: latest = f'ERROR {type(e).__name__}: {e}'
    print(f"{r['name']:22} pinned {pinned:24} newest {latest}{'' if pins.norm(pinned) == pins.norm(str(latest)) else '   <- stale'}")
T11LIVE
```

Expected: 21 lines, none `ERROR`. Some rows read `<- stale` (at plan time: istio, govulncheck, kubeconform, kube-score, tfsec, trivy, cosign, helm, terraform, pip, setuptools); that is the check working, not a failure.

- [ ] **Step 5: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add ci/pins.py ci/pins.yaml ci/test_pins.py .github/workflows/pin-freshness.yml .github/workflows/deploy-validate.yml \
  .github/workflows/image-scan.yml .github/workflows/publish-images.yml
git commit -q -m "track every version pin outside dependabot in one table with consistency and freshness checks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Token clients do not follow redirects

**Spec:** §5.1. VA 43.

**Files:** Modify `Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs`, `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs`, `Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs`; create `Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs`.

- [ ] **Step 1: Apply the patch.** Both clients build their `HttpClient` over `HttpClientHandler { AllowAutoRedirect = false }` (the pattern of `AuthentikFlowExecutorClient.cs:45-47`) unless a handler is passed; a failed token request names the redirect's `Location`. `TokenBroker.MintClientCredentialsTokenAsync` becomes `internal` with an optional handler. The tests use a real loopback `HttpListener` that answers 307: a scripted handler never follows a redirect, so only the real handler can show one request.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t12.patch <<'T12_PATCH'
diff --git a/Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs b/Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs
new file mode 100644
index 00000000..9bc07d2b
--- /dev/null
+++ b/Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs
@@ -0,0 +1,94 @@
+using System.Net;
+using System.Net.Sockets;
+using FluentAssertions;
+using Iverson.Client.Core;
+using Xunit;
+
+namespace Iverson.ClientConformance.Tests;
+
+// CSR round 12 §5.1: TokenBroker's client-credentials token request does not follow a redirect, so the
+// client secret goes only to the configured token endpoint; a 3xx is a failed request naming its Location.
+public class TokenBrokerTests
+{
+    // A loopback token endpoint that answers every request with a 307 to another path on itself and counts the
+    // requests, so a test sees whether the client's real handler followed the redirect; a scripted
+    // HttpMessageHandler never follows one.
+    private sealed class RedirectingEndpoint : IDisposable
+    {
+        private readonly HttpListener _listener = new();
+        private int _requests;
+
+        public RedirectingEndpoint()
+        {
+            var probe = new TcpListener(IPAddress.Loopback, 0);
+            probe.Start();
+            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
+            probe.Stop();
+            Url = $"http://127.0.0.1:{port}/application/o/token/";
+            Location = $"http://127.0.0.1:{port}/elsewhere/";
+            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
+            _listener.Start();
+            _ = Task.Run(ServeAsync);
+        }
+
+        public string Url { get; }
+        public string Location { get; }
+        public int Requests => Volatile.Read(ref _requests);
+
+        private async Task ServeAsync()
+        {
+            while (true)
+            {
+                HttpListenerContext context;
+                try { context = await _listener.GetContextAsync(); }
+                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
+                Interlocked.Increment(ref _requests);
+                context.Response.StatusCode = 307;
+                context.Response.RedirectLocation = Location;
+                context.Response.Close();
+            }
+        }
+
+        public void Dispose() => _listener.Close();
+    }
+
+    private sealed class Redirect(string location) : HttpMessageHandler
+    {
+        public int Requests { get; private set; }
+
+        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
+        {
+            Requests++;
+            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
+            response.Headers.Location = new Uri(location);
+            return Task.FromResult(response);
+        }
+    }
+
+    [Fact]
+    public async Task MintClientCredentialsToken_DoesNotFollowARedirect()
+    {
+        using var endpoint = new RedirectingEndpoint();
+
+        var act = () => TokenBroker.MintClientCredentialsTokenAsync(
+            new IversonClientCredentials("id", "s3cret", endpoint.Url), CancellationToken.None);
+
+        (await act.Should().ThrowAsync<InvalidOperationException>())
+            .WithMessage($"*redirected to {endpoint.Location}*");
+        endpoint.Requests.Should().Be(1);
+    }
+
+    [Fact]
+    public async Task MintClientCredentialsToken_WithAHandler_SendsThroughIt_AndNamesTheRedirectsLocation()
+    {
+        var handler = new Redirect("https://example.invalid/elsewhere/");
+
+        var act = () => TokenBroker.MintClientCredentialsTokenAsync(
+            new IversonClientCredentials("id", "s3cret", "https://localhost:9443/application/o/token/"),
+            CancellationToken.None, handler);
+
+        (await act.Should().ThrowAsync<InvalidOperationException>())
+            .WithMessage("*redirected to https://example.invalid/elsewhere/*");
+        handler.Requests.Should().Be(1);
+    }
+}
diff --git a/Iverson.Server/Iverson.ClientConformance/TokenBroker.cs b/Iverson.Server/Iverson.ClientConformance/TokenBroker.cs
index 45849091..8d22519e 100644
--- a/Iverson.Server/Iverson.ClientConformance/TokenBroker.cs
+++ b/Iverson.Server/Iverson.ClientConformance/TokenBroker.cs
@@ -174,9 +174,12 @@ public sealed class TokenBroker : IDisposable
         return document.RootElement.TryGetProperty(claim, out var value) ? value.GetString() : null;
     }
 
-    private static async Task<string> MintClientCredentialsTokenAsync(IversonClientCredentials creds, CancellationToken ct)
+    // CSR round 12 §5.1: a redirect is never followed, so the client secret goes only to the configured token
+    // endpoint; a 3xx is a failed token request. The handler parameter is for tests.
+    internal static async Task<string> MintClientCredentialsTokenAsync(
+        IversonClientCredentials creds, CancellationToken ct, HttpMessageHandler? handler = null)
     {
-        using var http = new HttpClient();
+        using var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
         var request = new ClientCredentialsTokenRequest
         {
             Address = creds.TokenEndpoint,
@@ -189,7 +192,11 @@ public sealed class TokenBroker : IDisposable
 
         var response = await http.RequestClientCredentialsTokenAsync(request, ct);
         if (response.IsError)
-            throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
+            throw new InvalidOperationException(
+                $"Failed to acquire admin-automation token: {response.Error}" +
+                (response.HttpResponse?.Headers.Location is { } location
+                    ? $" (the token endpoint redirected to {location}; token requests do not follow redirects)"
+                    : ""));
         return response.AccessToken!;
     }
 
diff --git a/Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs b/Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs
index 6f9c9e50..4959e61b 100644
--- a/Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs
+++ b/Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs
@@ -1,4 +1,5 @@
 using System.Net;
+using System.Net.Sockets;
 using System.Text;
 using System.Web;
 using FluentAssertions;
@@ -36,6 +37,63 @@ public class AdminAutomationCredentialsTests
         }
     }
 
+    // A loopback token endpoint that answers every request with a 307 to another path on itself and counts the
+    // requests, so a test sees whether the client's real handler followed the redirect; a scripted
+    // HttpMessageHandler never follows one.
+    private sealed class RedirectingEndpoint : IDisposable
+    {
+        private readonly HttpListener _listener = new();
+        private int _requests;
+
+        public RedirectingEndpoint()
+        {
+            var probe = new TcpListener(IPAddress.Loopback, 0);
+            probe.Start();
+            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
+            probe.Stop();
+            Url = $"http://127.0.0.1:{port}/application/o/token/";
+            Location = $"http://127.0.0.1:{port}/elsewhere/";
+            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
+            _listener.Start();
+            _ = Task.Run(ServeAsync);
+        }
+
+        public string Url { get; }
+        public string Location { get; }
+        public int Requests => Volatile.Read(ref _requests);
+
+        private async Task ServeAsync()
+        {
+            while (true)
+            {
+                HttpListenerContext context;
+                try { context = await _listener.GetContextAsync(); }
+                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
+                Interlocked.Increment(ref _requests);
+                context.Response.StatusCode = 307;
+                context.Response.RedirectLocation = Location;
+                context.Response.Close();
+            }
+        }
+
+        public void Dispose() => _listener.Close();
+    }
+
+    // CSR round 12 §5.1: the default handler does not follow a redirect, so the secret is sent once, to the
+    // configured endpoint, and the failure names where the redirect pointed.
+    [Fact]
+    public async Task GetTokenAsync_DoesNotFollowARedirect_AndNamesItsLocation()
+    {
+        using var endpoint = new RedirectingEndpoint();
+        using var creds = new AdminAutomationCredentials("id", endpoint.Url, "s3cret", null, null, null);
+
+        var act = () => creds.GetTokenAsync();
+
+        (await act.Should().ThrowAsync<InvalidOperationException>())
+            .WithMessage($"*redirected to {endpoint.Location}*");
+        endpoint.Requests.Should().Be(1);
+    }
+
     private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
         key => vars.FirstOrDefault(v => v.Key == key).Value;
 
diff --git a/Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs b/Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs
index 5bcf1ecf..a8016b86 100644
--- a/Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs
+++ b/Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs
@@ -36,7 +36,9 @@ public sealed class AdminAutomationCredentials : IDisposable
         AssertionFile = assertionFile;
         Scope = scope;
         HostHeader = hostHeader;
-        _http = handler is null ? new HttpClient() : new HttpClient(handler);
+        // CSR round 12 §5.1: a redirect is never followed, so the client credentials or assertion go only to the
+        // configured token endpoint; a 3xx is a failed token request (the pattern of AuthentikFlowExecutorClient).
+        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
     }
 
     public string ClientId { get; }
@@ -84,7 +86,11 @@ public sealed class AdminAutomationCredentials : IDisposable
             using var request = BuildRequest();
             var response = await _http.RequestClientCredentialsTokenAsync(request);
             if (response.IsError)
-                throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
+                throw new InvalidOperationException(
+                    $"Failed to acquire admin-automation token: {response.Error}" +
+                    (response.HttpResponse?.Headers.Location is { } location
+                        ? $" (the token endpoint redirected to {location}; token requests do not follow redirects)"
+                        : ""));
             var minted = new CachedToken(response.AccessToken!, DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - 60));
             Volatile.Write(ref _cached, minted);
             return minted.Value;
T12_PATCH
cd $WT && git apply --check $SCR/t12.patch && git apply $SCR/t12.patch && git status --short
````

- [ ] **Step 2: Run the tests, then check they fail with redirect-following clients.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Server
t() { systemd-run --user --scope -q -p MemoryMax=6G dotnet test $1 --filter "$2" 2>&1 | command grep -E '^(Passed|Failed)!' | cut -c1-70; }
t Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj "FullyQualifiedName~.AdminAutomationCredentialsTests."
t Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj "FullyQualifiedName~.TokenBrokerTests."
A=Iverson.LoadTest/Auth/AdminAutomationCredentials.cs; B=Iverson.ClientConformance/TokenBroker.cs; cp $A $SCR/a.bak; cp $B $SCR/b.bak
sed -i 's/new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })/(handler is null ? new HttpClient() : new HttpClient(handler))/' $A $B
t Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj "FullyQualifiedName~.AdminAutomationCredentialsTests.GetTokenAsync_DoesNotFollow"
t Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj "FullyQualifiedName~.TokenBrokerTests."
cp $SCR/a.bak $A; cp $SCR/b.bak $B; git diff --quiet -- $A $B || git diff --stat -- $A $B
```

Expected: `Passed! … Passed: 10` and `Passed! … Passed: 2`; with the mutation, `Failed! - Failed: 1, Passed: 0` and `Failed! - Failed: 1, Passed: 1` (the stub-handler test passes either way: it checks the message and the parameter); the last line prints the two files' patch-sized stat (the task's own edits), not a mutation.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs Iverson.Server/Iverson.ClientConformance/TokenBroker.cs \
  Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs Iverson.Server/Iverson.ClientConformance.Tests/TokenBrokerTests.cs
git commit -q -m "stop the token clients following redirects

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: The mint and teacher scripts keep credentials off plaintext remotes

**Spec:** §5.2, §5.3. VA 43, 44.

**Files:** Modify `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py`, `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`, `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`, `Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh`.

- [ ] **Step 1: Apply the patch.** Both scripts gain `is_https_or_loopback` (the rule of `CredentialEndpoints.IsHttpsOrLoopback`: https, `localhost`, or a loopback address). The mint script refuses a plain remote `--base-url` with exit 2 before any request or port-forward. `teacher_rerank.py` drops `--api-key`, reads the key only from `VLLM_API_KEY`, and refuses to start when the key is set and the base URL is neither https nor loopback; `run_measurement_batch.sh` stops passing the flag. The teacher tests clear `VLLM_API_KEY` for every test.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t13.patch <<'T13_PATCH'
diff --git a/Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh b/Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh
index e53aff65..2f4fc00f 100755
--- a/Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh
+++ b/Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh
@@ -27,8 +27,9 @@
 #                     re-run against the same seeds does not resume into pre-fix ledger entries
 #   CONCURRENCY    -- max concurrent teacher_rerank.py invocations per pass (default 6)
 #   BASE_URL       -- vLLM base URL (default http://127.0.0.1:8000)
-#   VLLM_API_KEY   -- if set, forwarded as --api-key (RunPod's vLLM template enforces this even
-#                     when --api-key never shows in `ps aux` -- confirmed live 2026-09-21)
+#   VLLM_API_KEY   -- if set, teacher_rerank.py reads it from the environment and sends it to BASE_URL
+#                     (RunPod's vLLM template enforces it -- confirmed live 2026-09-21); it is never
+#                     put on a command line (CSR round 12 §5.3)
 #   SEEDS          -- default "101 102 103 104 105 106 107 108"
 #   RUN_SUMMARIZER -- "0" to skip the summarizer step (default: run it)
 
@@ -56,11 +57,6 @@ for f in "$S/teacher_rerank.py" "$B/runs/rerank-a0prime.chunks.trec" "$B/beir/co
   fi
 done
 
-API_KEY_ARGS=()
-if [ -n "${VLLM_API_KEY:-}" ]; then
-  API_KEY_ARGS=(--api-key "$VLLM_API_KEY")
-fi
-
 echo "=== measurement batch: seeds [$SEEDS], model=$MODEL_ID vllm=$VLLM_VERSION sku=$INSTANCE_SKU ==="
 
 read -ra SEED_ARR <<< "$SEEDS"
@@ -78,7 +74,6 @@ for PASS in 1 2; do
         --base-url "$BASE_URL" --model "$MODEL_ID" \
         --seed "$SEED" --shuffle-seed "$SEED" --subsample 1 --subsample-seed "$SEED" \
         --vllm-version "$VLLM_VERSION" --quantisation mxfp4 --instance-type "$INSTANCE_SKU" \
-        "${API_KEY_ARGS[@]}" \
         --responses "$A/q$SEED.responses.jsonl" --out "$A/q$SEED.chunks.trec" \
         > "$A/q$SEED.pass$PASS.log" 2>&1 < /dev/null &
       PIDS+=("$!")
diff --git a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
index 75becc98..c8a00e5f 100644
--- a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
@@ -32,11 +32,13 @@ brief / the design spec's §9 for the exact invocation).
 """
 import argparse
 import hashlib
+import ipaddress
 import json
 import os
 import random
 import sys
 import urllib.error
+import urllib.parse
 import urllib.request
 from datetime import datetime, timezone
 
@@ -367,7 +369,8 @@ def call_teacher(base_url, model, prompt, seed, api_key=None):
     `api_key`, when given, is sent as `Authorization: Bearer <api_key>` -- some vLLM deployments
     (e.g. a RunPod template that launches `vllm serve --api-key` from a `VLLM_API_KEY` env var
     before the operator ever runs this script) require it; the header is simply omitted when
-    `api_key` is None, so unauthenticated servers are unaffected.
+    `api_key` is None, so unauthenticated servers are unaffected. main() takes it only from
+    VLLM_API_KEY and only for an https or loopback base URL (CSR round 12 §5.3).
 
     HTTP-level failures are caught, not left to crash the whole run (design doc §4): `HTTPError`
     (e.g. an xgrammar/Harmony FSM crash a `structured_outputs` request used to raise) records the
@@ -575,7 +578,6 @@ def build_arg_parser():
     ap.add_argument("--subsample-seed", type=int, default=None, help="seed for --subsample's selection")
     ap.add_argument("--vllm-version", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
     ap.add_argument("--quantisation", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
-    ap.add_argument("--api-key", default=None, help="sent as 'Authorization: Bearer <key>' if given; omitted otherwise (some vLLM deployments require it, e.g. RunPod's VLLM_API_KEY template)")
     ap.add_argument("--instance-type", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
     return ap
 
@@ -603,8 +605,28 @@ def validate_inputs_before_any_model_call(pool, pool_query_order, corpus, querie
         sys.exit("[teacher_rerank] refusing to start -- inputs are incomplete:\n" + "\n".join(lines))
 
 
+def is_https_or_loopback(url):
+    """CSR round 12 §5.3: the rule of CredentialEndpoints.IsHttpsOrLoopback (LoadTest): an absolute https
+    URL, or any scheme to localhost or a loopback address."""
+    parts = urllib.parse.urlsplit(url)
+    if not parts.scheme or not parts.hostname:
+        return False
+    if parts.scheme == "https" or parts.hostname == "localhost":
+        return True
+    try:
+        return ipaddress.ip_address(parts.hostname).is_loopback
+    except ValueError:
+        return False
+
+
 def main(argv=None):
     args = build_arg_parser().parse_args(argv)
+    # CSR round 12 §5.3: the vLLM key comes only from VLLM_API_KEY, never argv (every local user can read a
+    # process's command line), and is sent only to an https or loopback base URL.
+    args.api_key = os.environ.get("VLLM_API_KEY") or None
+    if args.api_key and not is_https_or_loopback(args.base_url):
+        sys.exit(f"[teacher_rerank] VLLM_API_KEY is set, but --base-url {args.base_url} is neither https:// nor a "
+                 "loopback address; the key is sent only to one that is")
     if (args.subsample is None) != (args.subsample_seed is None):
         sys.exit("--subsample and --subsample-seed must be given together")
 
diff --git a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
index e519f4ab..dea1d3d1 100644
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
@@ -29,6 +29,12 @@ sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
 import teacher_rerank as tr  # noqa: E402
 
 
+@pytest.fixture(autouse=True)
+def no_vllm_api_key(monkeypatch):
+    """main() reads VLLM_API_KEY (CSR round 12 §5.3); a value in the developer's shell must not leak in."""
+    monkeypatch.delenv("VLLM_API_KEY", raising=False)
+
+
 def write(path, text):
     with open(path, "w", encoding="utf-8") as f:
         f.write(text)
@@ -378,15 +384,24 @@ DOC_ID_IN_PROMPT = re.compile(r"^\[(\S+)\]", re.M)
 
 
 # --------------------------------------------------------------------------------------------
-# --api-key: some vLLM deployments require Authorization: Bearer <key> (e.g. a RunPod template
+# The vLLM key: some vLLM deployments require Authorization: Bearer <key> (e.g. a RunPod template
 # that launches `vllm serve --api-key` from VLLM_API_KEY before the operator runs this script).
-# The plumbing test proves args.api_key reaches call_teacher through main(), via ScriptedTeacher
-# recording what it received. The transport test proves call_teacher itself sends the header when
-# given a key and omits it when not, against a real HTTP server on loopback -- not a mock of
-# urllib, since a mock could assert the header was passed to the wrong urllib call.
+# CSR round 12 §5.3: main() takes it only from VLLM_API_KEY, never from argv, and sends it only
+# to an https or loopback --base-url. The plumbing tests prove the key reaches call_teacher through
+# main(), via ScriptedTeacher recording what it received. The transport test proves call_teacher
+# itself sends the header when given a key and omits it when not, against a real HTTP server on
+# loopback -- not a mock of urllib, since a mock could assert the header was passed to the wrong
+# urllib call.
 # --------------------------------------------------------------------------------------------
 
-def test_main_passes_api_key_through_to_call_teacher(tmp_path, monkeypatch):
+def with_base_url(argv, url):
+    argv = list(argv)
+    argv[argv.index("--base-url") + 1] = url
+    return argv
+
+
+@pytest.mark.parametrize("url", ["http://127.0.0.1:8000", "http://localhost:8000", "https://vllm.example.com"])
+def test_main_passes_the_environment_key_to_an_https_or_loopback_base_url(tmp_path, monkeypatch, url):
     run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
     responses_path = str(tmp_path / "responses.jsonl")
     out_path = str(tmp_path / "teacher.chunks.trec")
@@ -397,14 +412,40 @@ def test_main_passes_api_key_through_to_call_teacher(tmp_path, monkeypatch):
     ])
     monkeypatch.setattr(tr, "call_teacher", scripted)
 
-    argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
-    argv += ["--api-key", "sk-test-through-main"]
+    argv = with_base_url(base_argv(run_path, corpus_path, queries_path, responses_path, out_path), url)
+    monkeypatch.setenv("VLLM_API_KEY", "sk-test-through-main")
     monkeypatch.setattr(sys, "argv", ["teacher_rerank.py"] + argv)
     tr.main()
 
     assert scripted.api_key == "sk-test-through-main"
 
 
+def test_main_refuses_to_send_the_key_to_a_plain_remote_base_url(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
+    scripted = ScriptedTeacher([])
+    monkeypatch.setattr(tr, "call_teacher", scripted)
+    argv = with_base_url(base_argv(run_path, corpus_path, queries_path, str(tmp_path / "r.jsonl"),
+                                   str(tmp_path / "teacher.chunks.trec")), "http://vllm.example.com:8000")
+    monkeypatch.setenv("VLLM_API_KEY", "sk-test-refused")
+    monkeypatch.setattr(sys, "argv", ["teacher_rerank.py"] + argv)
+
+    with pytest.raises(SystemExit) as exit_info:
+        tr.main()
+
+    assert "neither https:// nor a loopback address" in str(exit_info.value.code)
+    assert "sk-test-refused" not in str(exit_info.value.code)
+    assert scripted.calls == 0
+
+
+def test_the_api_key_flag_is_gone(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
+    argv = base_argv(run_path, corpus_path, queries_path, str(tmp_path / "r.jsonl"), str(tmp_path / "t.chunks.trec"))
+    monkeypatch.setattr(sys, "argv", ["teacher_rerank.py"] + argv + ["--api-key", "sk-on-argv"])
+    with pytest.raises(SystemExit) as exit_info:
+        tr.main()
+    assert exit_info.value.code == 2
+
+
 def test_main_omits_api_key_when_not_given(tmp_path, monkeypatch):
     run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
     responses_path = str(tmp_path / "responses.jsonl")
diff --git a/Iverson.Server/deploy/scripts/mint_acting_user_token.py b/Iverson.Server/deploy/scripts/mint_acting_user_token.py
index bfa66959..c3ba1718 100755
--- a/Iverson.Server/deploy/scripts/mint_acting_user_token.py
+++ b/Iverson.Server/deploy/scripts/mint_acting_user_token.py
@@ -57,6 +57,7 @@ import argparse
 import base64
 import hashlib
 import hmac
+import ipaddress
 import json
 import os
 import socket
@@ -97,6 +98,20 @@ def log(msg):
     print(msg, file=sys.stderr, flush=True)
 
 
+def is_https_or_loopback(url: str) -> bool:
+    """CSR round 12 §5.2: the rule of CredentialEndpoints.IsHttpsOrLoopback (LoadTest): an absolute https
+    URL, or any scheme to localhost or a loopback address."""
+    parts = urllib.parse.urlsplit(url)
+    if not parts.scheme or not parts.hostname:
+        return False
+    if parts.scheme == "https" or parts.hostname == "localhost":
+        return True
+    try:
+        return ipaddress.ip_address(parts.hostname).is_loopback
+    except ValueError:
+        return False
+
+
 # --- TOTP (RFC 6238) --------------------------------------------------------
 # No third-party dependency (pyotp isn't guaranteed to be installed on every
 # dev machine this script runs on) -- HMAC-SHA1 TOTP is ~15 lines of stdlib.
@@ -474,6 +489,11 @@ def parse_args():
 
 def main() -> int:
     args = parse_args()
+    # CSR round 12 §5.2: the password, the TOTP code and the token travel to the base URL, so an override must be
+    # https or loopback. Checked before any request and before the kind port-forward starts.
+    if args.base_url is not None and not is_https_or_loopback(args.base_url):
+        log(f"ERROR: --base-url {args.base_url} must be https:// or a loopback address: the password and token are sent to it.")
+        return 2
 
     if args.target == "compose":
         base_url = args.base_url or DEFAULT_COMPOSE_BASE_URL
diff --git a/Iverson.Server/deploy/scripts/test_mint_acting_user_token.py b/Iverson.Server/deploy/scripts/test_mint_acting_user_token.py
index 95a2b073..7628560c 100644
--- a/Iverson.Server/deploy/scripts/test_mint_acting_user_token.py
+++ b/Iverson.Server/deploy/scripts/test_mint_acting_user_token.py
@@ -140,3 +140,26 @@ def test_there_is_no_password_flag(monkeypatch, fakes):
         main_with(monkeypatch, "--target", "compose", "--password", "argv-pw")
     assert exit_info.value.code == 2
     assert "password" not in fakes
+
+
+# CSR round 12 §5.2: --base-url must be https or a loopback host, checked before any request.
+@pytest.mark.parametrize("url", ["http://example.com", "http://10.0.0.5:9000", "http://localhost.example.com:9000",
+                                 "ftp://", "localhost:9000"])
+def test_a_plain_remote_base_url_is_refused_before_any_request(monkeypatch, fakes, capsys, url):
+    monkeypatch.setenv(ENV, "env-pw")
+    assert main_with(monkeypatch, "--target", "compose", "--base-url", url) == 2
+    assert "password" not in fakes
+    assert "must be https:// or a loopback address" in capsys.readouterr().err
+
+
+@pytest.mark.parametrize("url", ["https://auth.example.com", "http://localhost:9000", "http://127.0.0.1:9000",
+                                 "http://127.8.9.10", "http://[::1]:9000", "https://localhost:9443"])
+def test_an_https_or_loopback_base_url_is_used(monkeypatch, fakes, url):
+    monkeypatch.setenv(ENV, "env-pw")
+    assert main_with(monkeypatch, "--target", "compose", "--base-url", url) == 0
+    assert fakes["base_url"] == url
+
+
+def test_the_kind_target_refuses_a_plain_remote_base_url_before_its_port_forward(monkeypatch, fakes):
+    assert main_with(monkeypatch, "--target", "kind", "--base-url", "http://example.com") == 2
+    assert "port_forward" not in fakes and fakes["secrets"] == []
T13_PATCH
cd $WT && git apply --check $SCR/t13.patch && git apply $SCR/t13.patch && git status --short
````

- [ ] **Step 2: Run the tests and two mutations of each guard.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
M=Iverson.Server/deploy/scripts; L=Iverson.Server/Iverson.LoadTest/scripts
p() { python3 -m pytest $1 -q -p no:cacheprovider 2>&1 | tail -1; }
p $M/test_mint_acting_user_token.py; p $L/test_teacher_rerank.py
bash -n $L/run_measurement_batch.sh && echo sh-ok; git grep -n -- '--api-key' -- $L/run_measurement_batch.sh $L/teacher_rerank.py | command grep -v '^.*#' || echo "no --api-key flag left"
cp $M/mint_acting_user_token.py $SCR/m.bak
sed -i 's/    if args.base_url is not None and not is_https_or_loopback(args.base_url):/    if False:/' $M/mint_acting_user_token.py; echo "mint guard removed: $(p $M/test_mint_acting_user_token.py)"; cp $SCR/m.bak $M/mint_acting_user_token.py
sed -i 's/    if parts.scheme == "https" or parts.hostname == "localhost":/    if parts.scheme == "https" or "localhost" in parts.hostname:/' $M/mint_acting_user_token.py; echo "mint localhost substring: $(p $M/test_mint_acting_user_token.py)"; cp $SCR/m.bak $M/mint_acting_user_token.py
cp $L/teacher_rerank.py $SCR/t.bak
sed -i 's/    if args.api_key and not is_https_or_loopback(args.base_url):/    if False:/' $L/teacher_rerank.py; echo "teacher guard removed: $(p $L/test_teacher_rerank.py)"; cp $SCR/t.bak $L/teacher_rerank.py
git status --porcelain -- $M $L
```

Expected: `23 passed`; `48 passed`; `sh-ok`; one line, `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py:370:` (the docstring's `vllm serve --api-key`, the server's own flag), and no other `--api-key` line; the three mutations each `1 failed` or more (`6 failed`, `1 failed`, `1 failed` at plan time); the status lists only this task's five files as modified.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/scripts/mint_acting_user_token.py Iverson.Server/deploy/scripts/test_mint_acting_user_token.py \
  Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py \
  Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh
git commit -q -m "send tooling credentials only to https or loopback endpoints and take the vllm key from the environment

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: The harness gives builds and drivers an allowlisted environment

**Spec:** §5.4. VA 40, 48.

**Files:** Modify `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`, `Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs`.

- [ ] **Step 1: Apply the patch.** `RunProcessAsync` clears the child's environment and applies exactly the one it is given: build steps get `BuildStepEnvironment` (the allowlist: `PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `USER`, `SHELL`, `M2_HOME`, `VIRTUAL_ENV` and the prefixes `LC_`, `DOTNET_`, `NUGET_`, `JAVA_HOME`, `MAVEN_`, `GO`, `NODE_`, `NPM_CONFIG_`, `npm_config_`, `PYTHON`, `PIP_`); the driver run gets `DriverRunEnvironment` (that, plus `SSL_CERT_FILE`, `SSL_CERT_DIR`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH`, `JAVA_TOOL_OPTIONS`, plus `BuildEnvironment(context)`). The process test sets two uniquely named probe variables in the test process (never a real secret name, and never a CA variable, which the test process itself uses).

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t14.patch <<'T14_PATCH'
diff --git a/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs b/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
index 40fcbf8d..c40446e1 100644
--- a/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
+++ b/Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
@@ -1,3 +1,4 @@
+using System.Collections;
 using System.Text.Json;
 using FluentAssertions;
 using Xunit;
@@ -215,6 +216,95 @@ public class DriverRunnerTests
         }
     }
 
+    // CSR round 12 §5.4: build steps and drivers start from an empty environment plus an allowlist, so the
+    // harness's own credentials never reach a build tool or a driver.
+    private static readonly string[] HarnessSecrets =
+        ["IVERSON_CLIENT_SECRET", "IVERSON_CLIENT_ID", "IVERSON_TOKEN_ENDPOINT", "IVERSON_CLIENT_SCOPE", "IVERSON_ACTING_USER_HOST_HEADER"];
+
+    private static Hashtable Parent() => new()
+    {
+        ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["LC_ALL"] = "C.UTF-8", ["NUGET_PACKAGES"] = "/n",
+        ["JAVA_HOME"] = "/jdk", ["MAVEN_OPTS"] = "-Xmx1g", ["GOMODCACHE"] = "/g", ["npm_config_cache"] = "/npm",
+        ["NODE_EXTRA_CA_CERTS"] = "/ca.crt", ["PYTHONUSERBASE"] = "/py", ["SSL_CERT_FILE"] = "/bundle.pem",
+        ["JAVA_TOOL_OPTIONS"] = "-Djavax.net.ssl.trustStore=/ts.p12", ["IVERSON_CLIENT_SECRET"] = "harness-secret",
+        ["IVERSON_CLIENT_ID"] = "harness-id", ["IVERSON_TOKEN_ENDPOINT"] = "https://t/", ["IVERSON_CLIENT_SCOPE"] = "s",
+        ["IVERSON_ACTING_USER_HOST_HEADER"] = "h", ["AWS_SECRET_ACCESS_KEY"] = "aws",
+    };
+
+    [Theory]
+    [InlineData("PATH", true)]
+    [InlineData("LC_ALL", true)]
+    [InlineData("JAVA_HOME_21_X64", true)]
+    [InlineData("GOFLAGS", true)]
+    [InlineData("npm_config_cache", true)]
+    [InlineData("IVERSON_CLIENT_SECRET", false)]
+    [InlineData("SSL_CERT_FILE", false)]
+    [InlineData("JAVA_TOOL_OPTIONS", false)]
+    [InlineData("AWS_SECRET_ACCESS_KEY", false)]
+    [InlineData("path", false)]
+    public void IsAllowed_MatchesExactNamesAndPrefixesOnly(string name, bool allowed) =>
+        DriverRunner.IsAllowed(name).Should().Be(allowed);
+
+    [Fact]
+    public void BuildStepEnvironment_IsTheAllowlistOnly()
+    {
+        var environment = DriverRunner.BuildStepEnvironment(Parent());
+
+        environment.Keys.Should().BeEquivalentTo(
+            ["PATH", "HOME", "LC_ALL", "NUGET_PACKAGES", "JAVA_HOME", "MAVEN_OPTS", "GOMODCACHE", "npm_config_cache",
+             "NODE_EXTRA_CA_CERTS", "PYTHONUSERBASE"]);
+    }
+
+    [Fact]
+    public void DriverRunEnvironment_AddsTheCaTrustVariablesAndTheContext_AndNoHarnessSecret()
+    {
+        var environment = DriverRunner.DriverRunEnvironment(Parent(), Context());
+
+        environment.Should().Contain("SSL_CERT_FILE", "/bundle.pem")
+            .And.Contain("JAVA_TOOL_OPTIONS", "-Djavax.net.ssl.trustStore=/ts.p12")
+            .And.Contain("IVERSON_DRIVER_CLIENT_SECRET", "client-secret")
+            .And.Contain("IVERSON_DRIVER_ACTING_TOKEN", "acting-token")
+            .And.Contain("PATH", "/usr/bin");
+        environment.Keys.Should().NotContain(HarnessSecrets).And.NotContain("AWS_SECRET_ACCESS_KEY");
+    }
+
+    [Fact]
+    public async Task RunPhaseAsync_StartsTheDriverWithOnlyTheAllowlistedEnvironment()
+    {
+        // Reports two variables this test process sets: one under an allowlisted prefix, one not.
+        var root = FakePythonDriverRepo("""
+            import json, os, sys
+            out = sys.argv[sys.argv.index("--out") + 1]
+            names = ["PYTHON_CONFORMANCE_ALLOWED_PROBE", "IVERSON_CONFORMANCE_DENIED_PROBE", "IVERSON_DRIVER_ACTING_TOKEN"]
+            keys = {name: os.environ.get(name, "<unset>") for name in names}
+            with open(out, "w") as f:
+                json.dump({"language": "python", "phase": "register",
+                           "steps": [{"name": "env", "ok": True, "keys": keys}]}, f)
+            """);
+        Environment.SetEnvironmentVariable("PYTHON_CONFORMANCE_ALLOWED_PROBE", "allowed");
+        Environment.SetEnvironmentVariable("IVERSON_CONFORMANCE_DENIED_PROBE", "denied");
+        try
+        {
+            var runner = new DriverRunner(root);
+
+            var outcomes = await runner.RunPhaseAsync(Phase.Register, ["python"], Context());
+
+            outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Success>();
+            runner.KeysByLanguage["python"].Should().BeEquivalentTo(new Dictionary<string, string>
+            {
+                ["PYTHON_CONFORMANCE_ALLOWED_PROBE"] = "allowed",
+                ["IVERSON_CONFORMANCE_DENIED_PROBE"] = "<unset>",
+                ["IVERSON_DRIVER_ACTING_TOKEN"] = "acting-token",
+            });
+        }
+        finally
+        {
+            Environment.SetEnvironmentVariable("PYTHON_CONFORMANCE_ALLOWED_PROBE", null);
+            Environment.SetEnvironmentVariable("IVERSON_CONFORMANCE_DENIED_PROBE", null);
+            Directory.Delete(root, recursive: true);
+        }
+    }
+
     // The timeout message prints the driver's command line, so it must not carry a secret either.
     [Fact]
     public async Task RunPhaseAsync_TimeoutMessage_CarriesNoSecret()
diff --git a/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs b/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
index 50c65489..0b06f122 100644
--- a/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
+++ b/Iverson.Server/Iverson.ClientConformance/DriverRunner.cs
@@ -1,3 +1,4 @@
+using System.Collections;
 using System.ComponentModel;
 using System.Diagnostics;
 using System.Text.Json;
@@ -176,7 +177,8 @@ public sealed class DriverRunner : IDriverRunner
             {
                 var buildCwd = ResolveCwd(spec.Cwd);
                 var buildResult = await RunProcessAsync(
-                    ResolveCommand(spec.BuildCommand, buildCwd), spec.BuildArgs!, buildCwd, ct);
+                    ResolveCommand(spec.BuildCommand, buildCwd), spec.BuildArgs!, buildCwd, ct,
+                    BuildStepEnvironment(Environment.GetEnvironmentVariables()));
 
                 if (buildResult is ProcessOutcome.ToolMissing missing)
                 {
@@ -207,7 +209,8 @@ public sealed class DriverRunner : IDriverRunner
 
             var cwd = ResolveCwd(spec.Cwd);
             var execResult = await RunProcessAsync(
-                ResolveCommand(spec.ExecCommand, cwd), execArgs, cwd, ct, BuildEnvironment(context));
+                ResolveCommand(spec.ExecCommand, cwd), execArgs, cwd, ct,
+                DriverRunEnvironment(Environment.GetEnvironmentVariables(), context));
 
             if (execResult is ProcessOutcome.ToolMissing execMissing)
             {
@@ -362,6 +365,54 @@ public sealed class DriverRunner : IDriverRunner
         ["IVERSON_DRIVER_WRONG_ACTING_TOKEN"] = context.WrongActingToken,
     };
 
+    // CSR round 12 §5.4: every build and driver process starts from an empty environment plus these names and
+    // prefixes, so the harness's own credentials (IVERSON_CLIENT_SECRET and the other variables it reads) never
+    // reach a build tool, a package script or a driver. A variable a toolchain needs and this list lacks is added
+    // here, with a comment naming the toolchain.
+    private static readonly string[] AllowedNames =
+        ["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV"];
+
+    private static readonly string[] AllowedPrefixes =
+        ["LC_", "DOTNET_", "NUGET_", "JAVA_HOME", "MAVEN_", "GO", "NODE_", "NPM_CONFIG_", "npm_config_", "PYTHON", "PIP_"];
+
+    // The drivers verify the compose stack's TLS server through these (docs/runbooks/client-conformance-matrix.md):
+    // the driver run gets them, the build steps do not.
+    private static readonly string[] CaTrustNames =
+        ["SSL_CERT_FILE", "SSL_CERT_DIR", "GRPC_DEFAULT_SSL_ROOTS_FILE_PATH", "JAVA_TOOL_OPTIONS"];
+
+    internal static bool IsAllowed(string name) =>
+        AllowedNames.Contains(name, StringComparer.Ordinal)
+        || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
+
+    /// <summary>A build step's environment: the allowlisted variables of <paramref name="parent"/> and nothing else.</summary>
+    internal static Dictionary<string, string> BuildStepEnvironment(IDictionary parent)
+    {
+        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
+        foreach (DictionaryEntry entry in parent)
+        {
+            if (entry.Key is string name && entry.Value is string value && IsAllowed(name))
+                environment[name] = value;
+        }
+        return environment;
+    }
+
+    /// <summary>
+    /// A driver run's environment: a build step's, plus <paramref name="parent"/>'s CA-trust variables, plus
+    /// <see cref="BuildEnvironment"/>'s values for this <paramref name="context"/>.
+    /// </summary>
+    internal static Dictionary<string, string> DriverRunEnvironment(IDictionary parent, DriverContext context)
+    {
+        var environment = BuildStepEnvironment(parent);
+        foreach (var name in CaTrustNames)
+        {
+            if (parent[name] is string value)
+                environment[name] = value;
+        }
+        foreach (var (name, value) in BuildEnvironment(context))
+            environment[name] = value;
+        return environment;
+    }
+
     /// <summary>
     /// Turns a driver-relative executable path into an absolute one. <c>ProcessStartInfo</c>
     /// resolves a relative <c>FileName</c> against the CALLING process's current directory, not
@@ -393,7 +444,7 @@ public sealed class DriverRunner : IDriverRunner
 
     private static async Task<ProcessOutcome> RunProcessAsync(
         string command, IReadOnlyList<string> args, string cwd, CancellationToken ct,
-        IReadOnlyDictionary<string, string>? environment = null)
+        IReadOnlyDictionary<string, string> environment)
     {
         var psi = new ProcessStartInfo
         {
@@ -405,11 +456,10 @@ public sealed class DriverRunner : IDriverRunner
         };
         foreach (var arg in args)
             psi.ArgumentList.Add(arg);
-        if (environment is not null)
-        {
-            foreach (var (name, value) in environment)
-                psi.Environment[name] = value;
-        }
+        // CSR round 12 §5.4: exactly the given environment, nothing inherited (see AllowedNames).
+        psi.Environment.Clear();
+        foreach (var (name, value) in environment)
+            psi.Environment[name] = value;
 
         using var process = new Process { StartInfo = psi };
 
T14_PATCH
cd $WT && git apply --check $SCR/t14.patch && git apply $SCR/t14.patch && git status --short
````

- [ ] **Step 2: Run the tests and two mutations.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT/Iverson.Server; F=Iverson.ClientConformance/DriverRunner.cs; cp $F $SCR/dr.bak
t() { echo "[$1] $(systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj --filter "$2" 2>&1 | command grep -E '^(Passed|Failed)!' | cut -c1-60)"; cp $SCR/dr.bak $F; }
t whole-project "FullyQualifiedName~Iverson.ClientConformance.Tests."
sed -i 's/^        psi.Environment.Clear();$/        \/\/ cleared/' $F; t 'no Clear()' "FullyQualifiedName~.DriverRunnerTests."
sed -i 's/\["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV"\]/["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV", "SSL_CERT_FILE"]/' $F; t 'CA variable on build steps' "FullyQualifiedName~.DriverRunnerTests."
git diff --stat -- $F
```

Expected: `[whole-project] Passed! - Failed: 0, Passed: 677` (about); both mutations `Failed!`; the stat is the task's own edit. (The full conformance run on all five drivers with this environment is Task 17 Step 3.)

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Server/Iverson.ClientConformance/DriverRunner.cs Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs
git commit -q -m "give harness builds and drivers an allowlisted environment

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: The samples use TLS

**Spec:** §5.5. VA 41, 42.

**Files:** Modify `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs`, `Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java`.

- [ ] **Step 1: Apply the patch.** .NET drops `allowInsecureChannelCallCredentials: true` (its endpoint is `https://localhost:7142`). Java uses `new IversonClient(host, port, credentials, actingUserToken)` with host and port from `IVERSON_GRPC_HOST`/`IVERSON_GRPC_PORT` (default `localhost:8080`, compose's TLS listener) and `false` for the plaintext token-endpoint opt-in.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t15.patch <<'T15_PATCH'
diff --git a/Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs b/Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
index 3623e493..c01b47aa 100644
--- a/Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
+++ b/Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
@@ -39,7 +39,6 @@ var services = new ServiceCollection()
             tokenEndpoint!,
             Scope: "admin schema_admin"),
         actingUserTokenProvider: () => Task.FromResult(actingUserToken),
-        allowInsecureChannelCallCredentials: true,
         entityAssemblies: [typeof(Article).Assembly])
     .BuildServiceProvider();
 
diff --git a/Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java b/Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
index c52c1e46..c8fc4e7b 100644
--- a/Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
+++ b/Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
@@ -47,19 +47,21 @@ public class Main {
         }
 
         // ── Connect ────────────────────────────────────────────────────────────
-        // This sample deliberately targets a local plaintext (h2c) endpoint — the accepted
-        // dev/test model — so it must explicitly defeat IversonClient's default guard against
-        // attaching credentials to a plaintext channel.
-        try (IversonClient client = IversonClient.plaintext(
-                "localhost", 5000,
+        // TLS (CSR round 12 §5.5): the docker-compose API listens with TLS on localhost:8080. The JVM must
+        // trust compose's CA (JAVA_TOOL_OPTIONS, docs/runbooks/client-conformance-matrix.md);
+        // IVERSON_GRPC_HOST and IVERSON_GRPC_PORT point the sample at another server. The token endpoint
+        // must be https too: no plaintext opt-in.
+        String host = System.getenv().getOrDefault("IVERSON_GRPC_HOST", "localhost");
+        int port = Integer.parseInt(System.getenv().getOrDefault("IVERSON_GRPC_PORT", "8080"));
+        try (IversonClient client = new IversonClient(
+                host, port,
                 new OAuth2ClientCredentials(
                     clientId,
                     clientSecret,
                     tokenEndpoint,
                     "admin schema_admin",
-                    true),
-                actingUserToken,
-                true)) {
+                    false),
+                actingUserToken)) {
 
             // ── Register schemas ───────────────────────────────────────────────
             ObjectMapping.AuthorizationRules sampleRules = ObjectMapping.AuthorizationRules.newBuilder()
T15_PATCH
cd $WT && git apply --check $SCR/t15.patch && git apply $SCR/t15.patch && git status --short
````

- [ ] **Step 2: Build both samples.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Clients/DotNet/Iverson.Client.Sample/Iverson.Client.Sample.csproj 2>&1 | command grep -E '^ +[0-9]+ Error|warning' | sort -u | cut -c1-120
cd Iverson.Clients/Java && systemd-run --user --scope -q -p MemoryMax=4G ./mvnw -B -q -Dmaven.repo.local=$SCR/m2-rec -pl sample -am -DskipTests package > $SCR/t15.log 2>&1; echo "java sample rc=$?"
```

Expected: `0 Error(s)` (a `CS8619` warning, if printed, is `main`'s too); `java sample rc=0`.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs Iverson.Clients/Java/sample/src/main/java/io/iverson/sample/Main.java
git commit -q -m "connect the samples over tls

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: The supply-chain runbook

**Spec:** §6 "Runbook", §3 "Runbook", §4.7, §4.5. VA 24, 25.

**Files:** Create `docs/runbooks/csr12-supply-chain.md`; modify `docs/runbooks/csr11-clients-tooling-ci.md` (§7, which said Dependabot does not raise the GitLab pins).

- [ ] **Step 1: Apply the patch.** The runbook covers the steps after the merge (R1–R4 below run from it), reading an admission refusal and telling it from an engine or TUF outage, the TUF restart residual, the emergency lever, the CNPG pins, the Maven re-record (starting from no record file) and wrapper bumps, and answering a `Pin behind` issue.

````bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $SCR/t16.patch <<'T16_PATCH'
diff --git a/docs/runbooks/csr11-clients-tooling-ci.md b/docs/runbooks/csr11-clients-tooling-ci.md
index a82dc830..95b44356 100644
--- a/docs/runbooks/csr11-clients-tooling-ci.md
+++ b/docs/runbooks/csr11-clients-tooling-ci.md
@@ -248,9 +248,10 @@ python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose
 
 ## 7. GitLab image pins
 
-`.gitlab-ci.yml` pins every job image by digest. Dependabot does not read that file, so these pins
-are bumped by hand, and GitLab's tool versions (for example govulncheck's Go toolchain) can lag
-the GitHub workflows, which float.
+`.gitlab-ci.yml` pins every job image by digest. Dependabot does not read that file; it raises the
+same images in `ci/gitlab-images/Dockerfile` (CSR round 12 §4.3), and `ci/pins.py consistency` fails until
+each bump is copied into `.gitlab-ci.yml` (`csr12-supply-chain.md` §4). GitLab's tool versions (for example
+govulncheck's Go toolchain) can still lag the GitHub workflows, which float.
 
 ## 8. SDK behaviour changes
 
diff --git a/docs/runbooks/csr12-supply-chain.md b/docs/runbooks/csr12-supply-chain.md
new file mode 100644
index 00000000..611324b0
--- /dev/null
+++ b/docs/runbooks/csr12-supply-chain.md
@@ -0,0 +1,226 @@
+# CSR Round-12 Supply Chain — Operator Runbook
+
+**Why:** CSR round 12's supply-chain sub-project (`docs/specs/2026-10-26-csr-round12-supply-chain-design.md`)
+keeps the NVD API key out of every job that runs repository or dependency code, makes release tags
+admin-only, binds release signatures to their commit, has the cloud clusters check first-party images at
+admission, locks the .NET and Java resolutions to recorded hashes, and gives every version pin outside
+Dependabot a weekly "you are behind" signal. This runbook covers the steps after the merge, reading an
+admission refusal, the Maven checksum re-record, and the pin table. Image verification by hand is
+`csr11-clients-tooling-ci.md` §2; the GitLab key is its §5.
+
+## 1. After the merge
+
+Each step is a live change; make them in this order and confirm each before the next.
+
+### Step 2: the pipelines on `main`
+
+1. **GitLab, only if a GitLab project runs this pipeline.** Define `NVD_API_KEY` once, as
+   `csr11-clients-tooling-ci.md` §5 describes (Protected, Masked, Environment scope `nvd-update`), and
+   delete every other definition: an unscoped project variable, a group variable, a pipeline-schedule
+   variable. Before this branch no job declared `environment: nvd-update`, so a scoped variable reached
+   nothing; that is why this comes after the merge. The next scheduled pipeline is the check: `nvd-update`
+   pushes the cache, and `owasp-dependency-check-java` passes its first line (the key did not reach it) and
+   finds the database. If the scan fails with `NoDataException`, its cache pull did not see `nvd-update`'s
+   push: check the runner's cache configuration (spec §8.9).
+2. **GitHub.** On the first push to `main`, `dependency-scan.yml`'s `nvd-update` runs and saves a cache
+   (`nvd-<run id>`), then `owasp-dependency-check-java` runs keyless from it:
+
+   ```bash
+   gh run list --repo counsellorben/Iverson --workflow dependency-scan.yml --branch main --limit 1
+   gh run view --repo counsellorben/Iverson <run id> --json jobs --jq '.jobs[] | "\(.conclusion) \(.name)"'
+   ```
+
+   Both jobs read `success`. A pull request's run then shows `nvd-update` skipped and the scan passing.
+3. **Dependabot's helm jobs.** In the repository's **Insights**, then **Dependency graph**, then
+   **Dependabot**, the three `helm` entries' last runs succeed (they failed on every run before, `egress not
+   allowlisted`), and open the outstanding stub bumps (cloudnative-pg, Strimzi and the StarRocks operator
+   at the time of writing).
+4. **Pin freshness.** Run it once by hand and review the issues it opens (§4):
+
+   ```bash
+   gh workflow run pin-freshness.yml --repo counsellorben/Iverson
+   gh issue list --repo counsellorben/Iverson --search '"Pin behind:" in:title'
+   ```
+
+   Without the AWS role (step 4) its log reads `skipped: no AWS role` for `vpc-cni` and `aws-ebs-csi-driver`.
+
+### Step 3: the repository's settings
+
+1. **Delete the Dependabot secret `NVD_API_KEY`**, only after step 2's keyless scan passed. The Actions
+   secret of the same name stays (`nvd-update` reads it).
+
+   ```bash
+   gh secret delete NVD_API_KEY --repo counsellorben/Iverson --app dependabot
+   ```
+
+2. **Push protection:**
+
+   ```bash
+   gh api -X PATCH repos/counsellorben/Iverson -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled' \
+     --jq .security_and_analysis.secret_scanning_push_protection.status
+   ```
+
+   It prints `enabled`.
+3. **The tag ruleset** (`Iverson.Server/deploy/scripts/apply-tag-ruleset.sh`). GitHub does not document the
+   admin role's numeric id, so the script creates the ruleset disabled and switches it on only once its
+   read-back shows the bypass applies to you:
+
+   ```bash
+   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh --dry-run
+   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
+   ```
+
+   The second prints `read back: enforcement=disabled current_user_can_bypass=always
+   bypass_actors=RepositoryRole:5:always`. Open the repository's **Settings**, then **Rules**, then
+   **Rulesets**, then `release-tags`: the bypass list must read **Repository admin**, and the target must be
+   tags matching `v*`. If either is wrong, delete the ruleset there and stop; do not activate it. Otherwise:
+
+   ```bash
+   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh --activate
+   ```
+
+   It prints `enforcement=active`. From then on only the admin can create, move or delete a `v*` tag. If it
+   refuses because the read-back does not show `current_user_can_bypass=always` (GitHub does not document
+   that value for a disabled ruleset), leave the ruleset disabled and stop here: report the read-back.
+4. **Remove the second writer:**
+
+   ```bash
+   gh api -X DELETE repos/counsellorben/Iverson/collaborators/vadim82
+   gh api repos/counsellorben/Iverson/collaborators --jq '.[].login'
+   ```
+
+   The last command lists only `counsellorben`.
+5. **Branch protection, last,** once all 22 required checks are green on `main`
+   (`csr11-clients-tooling-ci.md` §4). At the time of writing the admin-ui image's scan is red on
+   `brotli-libs` until the upstream `nginx-unprivileged` image is rebuilt (spec §8.6).
+
+### Step 4: the AWS account (for the EKS freshness rows)
+
+`Iverson.Server/deploy/terraform/bootstrap/aws` gains GitHub's OIDC provider and the role
+`iverson-pin-freshness`, which only `main`'s runs can assume and which may only call
+`eks:DescribeAddonVersions`. The module also declares the Terraform state bucket `iverson-terraform-state`
+(with its versioning, encryption and public-access block) and the lock table `iverson-terraform-locks`, and no
+state file for the module is kept in the repository, so those must be adopted before the apply rather than
+created again. An account holds one OIDC provider per URL: if yours already has
+`token.actions.githubusercontent.com`, import it too.
+
+```bash
+cd Iverson.Server/deploy/terraform/bootstrap/aws && terraform init
+# Both must succeed: the bucket and the table are yours. If either fails (403 or 404 for the bucket), stop:
+# the name is taken by another account, and renaming it changes every cloud root's backend.
+aws s3api head-bucket --bucket iverson-terraform-state
+aws dynamodb describe-table --table-name iverson-terraform-locks --query Table.TableName --output text
+terraform import aws_s3_bucket.state iverson-terraform-state
+terraform import aws_s3_bucket_versioning.state iverson-terraform-state
+terraform import aws_s3_bucket_server_side_encryption_configuration.state iverson-terraform-state
+terraform import aws_s3_bucket_public_access_block.state iverson-terraform-state
+terraform import aws_dynamodb_table.locks iverson-terraform-locks
+aws iam list-open-id-connect-providers --query 'OpenIDConnectProviderList[].Arn' --output text
+# only if an ARN ending in /token.actions.githubusercontent.com is listed:
+terraform import aws_iam_openid_connect_provider.github <that ARN>
+terraform plan    # must add only the role, its policy and (unless imported) the OIDC provider, and change nothing else
+terraform apply
+gh variable set PIN_FRESHNESS_AWS_ROLE --repo counsellorben/Iverson --body "$(terraform output -raw pin_freshness_role_arn)"
+```
+
+If `terraform plan` would create, change or destroy anything else (the bucket's settings differ from the
+module, for example), stop and report it rather than apply.
+
+The next `pin-freshness` run checks the two add-ons.
+
+## 2. Admission verification on the clouds
+
+On AWS, Azure and GCP the `iverson` namespace carries `policy.sigstore.dev/include=true`, and Sigstore's
+policy-controller (namespace `cosign-system`) checks every pod and workload created or changed there. A
+first-party image (`iverson-api`, `iverson-admin-ui`, `iverson-ollama-model`, `iverson-tei-model`) is admitted
+only if `publish-images.yml` attested its digest's SPDX SBOM, and the attestation names that same image. Every
+other image is admitted unverified. Every image, by tag or not, is rewritten to `tag@digest`, so the stored
+pods and workload templates read `tag@digest`. kind, local and compose do not check.
+
+**Reading a refusal.** The API server returns the engine's reason:
+
+| Message contains | Meaning |
+|---|---|
+| `no matching attestations` … `expected SAN value to match regex …, got …` | not signed by this repository's publish workflow |
+| `no matching attestations with type https://cyclonedx.org/bom` (or another type) | the attestation is not an SPDX SBOM |
+| `subject.0.name: conflicting values "…/iverson-admin-ui" and "…/iverson-api"` | another first-party image's digest pushed under this name |
+| `spec.initContainers[0].image` | the init container's image failed, not the main one |
+| `must be an image digest` | the registry could not resolve the tag (for example a local, unpublished image) |
+| `failed calling webhook "policy.sigstore.dev"` … `connection refused` or `context deadline exceeded` | the engine is down or slow, not a verdict on the image |
+
+**An engine outage, not a refusal.** With the engine down, every create in `iverson` is refused (the webhooks
+fail closed) and other namespaces are unaffected:
+
+```bash
+kubectl -n cosign-system get pods
+kubectl -n cosign-system logs deploy/policy-controller-webhook --tail=50
+```
+
+**The TUF residual (spec §8.3).** The engine fetches Sigstore's TUF root (`tuf-repo-cdn.sigstore.dev`) when
+it starts. A running engine keeps verifying through a Sigstore outage, but an engine restarted while the TUF
+CDN is unreachable crash-loops (`Failed to initialize TUF client`), and first-party creates in `iverson` are
+refused until the CDN returns. Wait for it, then delete the engine's pods so they start again.
+
+**The emergency lever.** To admit pods while the engine cannot run, remove the namespace label. It is an
+operator act (namespace-scoped roles cannot edit a `Namespace`), and it switches verification off for the
+whole namespace; put it back as soon as the engine runs:
+
+```bash
+kubectl label namespace iverson policy.sigstore.dev/include-
+kubectl label namespace iverson policy.sigstore.dev/include=true    # afterwards
+```
+
+**The model images** have not been published yet: their policies apply from their first `v*` tag or manual
+publish run.
+
+**CNPG.** Because the engine rewrites every image, the PostgreSQL image (`charts/postgres` `imageName`) and
+the CNPG operator image (`modules/operators/versions/cloudnative-pg-values.yaml`) are pinned by digest; a
+tag-only target would roll the instances. A cluster that ran `main`'s chart before this change rolls its
+PostgreSQL instances once when the pinned `imageName` first applies (kind included). When Dependabot raises
+the cloudnative-pg chart, the `cnpg-operator` and `cnpg-postgresql` pin issues (§4) say which digests to
+take. Strimzi's and the StarRocks operator's workloads are not pinned (spec §8.8).
+
+## 3. Java: the trusted-checksum record
+
+Every Java build runs Maven 3.9.16 through `Iverson.Clients/Java/mvnw`, and every artifact it resolves
+must match `Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256`; an artifact that is missing from
+it fails with `Missing from summaryFile trusted checksum(s)`, and one that differs with `trusted checksum
+mismatch`. Dependabot does not update the record, so **each Maven Dependabot pull request needs a
+re-record** before it goes green. On its branch:
+
+```bash
+cd Iverson.Clients/Java
+rm .mvn/checksums/checksums-central.sha256
+./mvnw -B clean install -Daether.artifactResolver.postProcessor.trustedChecksums.record=true
+./mvnw -B org.owasp:dependency-check-maven:<the pom's version>:check -DautoUpdate=false \
+  -Daether.artifactResolver.postProcessor.trustedChecksums.record=true
+rm .mvn/checksums/checksums-central.sha256.bak   # the second run's backup of the first record
+git add .mvn/checksums/checksums-central.sha256
+```
+
+The tests run in the first command, so the test runner's own plugins are recorded; the second ends in
+`NoDataException` (no NVD database here), which is expected. Commit the record to the pull request.
+
+**A Maven or Maven Wrapper bump** (a `Pin behind: maven` or `maven-wrapper` issue) changes
+`.mvn/wrapper/maven-wrapper.properties`. For a wrapper bump, first regenerate the scripts and that file with the
+record's check off (the plugin is not in the record): `mvn -N org.apache.maven.plugins:maven-wrapper-plugin:<version>:wrapper -Dtype=source -Dmaven=<maven version> -Daether.artifactResolver.postProcessor.trustedChecksums=false`.
+It rewrites the file without its two SHA-256 lines. For a Maven bump, edit the version in `distributionUrl`.
+Then download the new zip (and, for a wrapper bump, jar) from Maven Central, take its SHA-256 (`sha256sum`),
+check the zip against the `.sha512` and the jar against the `.sha1` Maven Central publishes beside each (it publishes no SHA-512 for the jar), and write `distributionSha256Sum` and
+`wrapperSha256Sum` (after a regeneration, append both). Then re-record as above.
+
+## 4. The pin table
+
+`ci/pins.yaml` lists every version pin Dependabot does not raise: where its newest version is read
+(`source`) and every place it is written (`locations`).
+
+- **`Pin behind: <name>`** issues come from `pin-freshness.yml` (weekly). To answer one, raise every
+  location the issue lists in one pull request; `python3 ci/pins.py consistency` (deploy-validate's
+  "Pin table consistency" step) fails until they agree. If the pin must stay behind, add a `note:` line to
+  the row saying why. The next run opens a new issue while the row is behind, so a pin that stays behind
+  keeps an issue open as the reminder. The issue closes itself once the row is current.
+- **GitLab images.** Dependabot raises `ci/gitlab-images/Dockerfile`, never `.gitlab-ci.yml`. Its pull request
+  stays red on the consistency step until the same tag and digest are copied into every matching `image:`
+  line of `.gitlab-ci.yml`.
+- **A new pin** gets a row before it merges: a `version:` input belongs to the action it configures, so
+  identify the action first.
T16_PATCH
cd $WT && git apply --check $SCR/t16.patch && git apply $SCR/t16.patch && git status --short
````

- [ ] **Step 2: Check the commands it names and its cross-references.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
gh secret delete --help | command grep -c -- '--app'; gh variable set --help | command grep -c -- '--body'
for f in Iverson.Server/deploy/scripts/apply-tag-ruleset.sh .github/workflows/pin-freshness.yml ci/pins.yaml ci/gitlab-images/Dockerfile \
  Iverson.Server/deploy/terraform/modules/operators/versions/cloudnative-pg-values.yaml Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256; do test -e $f && echo "exists $f"; done
command grep -n 'pin_freshness_role_arn\|iverson-pin-freshness' Iverson.Server/deploy/terraform/bootstrap/aws/main.tf | head -3
command grep -c 'policy-controller-webhook' docs/runbooks/csr12-supply-chain.md
```

Expected: `1`, `1` (or more); six `exists` lines; the role name and the output in `main.tf`; `1` or more (the engine Deployment's name, `kubectl -n cosign-system logs deploy/policy-controller-webhook`).

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
git add docs/runbooks/csr12-supply-chain.md docs/runbooks/csr11-clients-tooling-ci.md
git commit -q -m "add the supply chain runbook

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 17: Full suites, the conformance run and the live admission probe

**Spec:** §7 (unit tests, lock files, harness, live kind probe). VA 14–25, 45.

**Files:** none (scratch under `$SCR/live`). Not replayed at plan time; the probe steps repeat the admission probes' own method (probe 2's registry copy, policies and controls).

Rules for this task:
- **One heavy process at a time.** Check `free -g` before each heavy step and stop and ask the user if `available` is below 7 GB. The compose run and the kind cluster never overlap.
- **The user's stacks are off-limits.** If `podman ps` lists a running container or `kind get clusters` lists a cluster at Step 2, **stop and ask the user**. Throwaway names only: compose project `csr12s`, kind cluster `csr12s-admission`, kubeconfig `$SCR/live/kubeconfig`.
- **Downloads:** kind v0.33.0 (SHA-256 `aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d`) into `/home/ben/.cache/csr12p-live/bin` if it is not already there (the Platform run put it there).
- **A failure is reported, not patched over.** Record the output, stop, and report it with the task that owns the behaviour.

- [ ] **Step 1: Full suites.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cd $WT
systemd-run --user --scope -q -p MemoryMax=8G bash -c 'dotnet restore Iverson.slnx --locked-mode > /dev/null && dotnet build Iverson.slnx --no-restore > /dev/null && dotnet test Iverson.slnx --no-build --filter "Category!=Integration"' 2>&1 | command grep -E '^(Passed|Failed)!'
python3 -m pytest ci/test_pins.py Iverson.Server/deploy/kind/test_chart_pins.py Iverson.Server/deploy/scripts Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q -p no:cacheprovider 2>&1 | tail -1
python3 ci/pins.py consistency > /dev/null && echo "pins consistent"
```

Expected: eleven `Passed!` lines with 0 failed (4,070 tests at plan time); pytest's last line `<n> passed, 2 skipped` (the PowerShell tests skip without `pwsh` on `PATH`); `pins consistent`.

- [ ] **Step 2: Record the user's state and prepare.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
mkdir -p $SCR/live /home/ben/.cache/csr12p-live/bin
[ -x /home/ben/.cache/csr12p-live/bin/kind ] || { curl -sSL -o /home/ben/.cache/csr12p-live/bin/kind https://github.com/kubernetes-sigs/kind/releases/download/v0.33.0/kind-linux-amd64 && chmod 755 /home/ben/.cache/csr12p-live/bin/kind; }
echo "aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d  /home/ben/.cache/csr12p-live/bin/kind" | sha256sum -c -
cat >> /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh <<'T17ENV'
LIVE=$SCR/live
export KUBECONFIG=$LIVE/kubeconfig KIND_EXPERIMENTAL_PROVIDER=podman PATH=/home/ben/.cache/csr12p-live/bin:$PATH
ENVF=$WT/Iverson.Server/.env
CA=$WT/Iverson.Server/deploy/compose-tls/ca.crt
DC="docker compose -p csr12s -f $LIVE/compose.yml --project-directory $WT/Iverson.Server --env-file $ENVF"
val() { command grep "^$1=" "$ENVF" | cut -d= -f2-; }
scratch_env() {
  export HOME=$LIVE/home NUGET_PACKAGES=/home/ben/.nuget/packages PYTHONUSERBASE=/home/ben/.local
  export GOMODCACHE=/home/ben/go/pkg/mod MAVEN_OPTS=-Dmaven.repo.local=/home/ben/.m2/repository npm_config_cache=/home/ben/.npm
}
T17ENV
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
mkdir -p $LIVE/home; kind version; podman ps --format '{{.Names}}'; kind get clusters
podman ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt; podman volume ls -q | sort > $LIVE/before-volumes.txt
podman network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt; podman images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

Expected: `…/kind: OK`; `kind v0.33.0 …`; no running container and `No kind clusters found.` (otherwise stop and ask).

- [ ] **Step 3: The full conformance harness over TLS with the allowlisted environment** (§5.4's proof), against an isolated compose project, the Platform plan's pattern (its Task 27 Steps 4, 7 and 8). `MAVEN_OPTS` and the other scratch variables reach the build steps through the allowlist; the CA variables reach only the drivers.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
umask 077
bash $WT/scripts/generate-compose-secrets.sh > $LIVE/secrets.log 2>&1; echo "secrets exit=$?"
command grep -v container_name $WT/Iverson.Server/docker-compose.yml | sed 's/^    image: iverson-api$/    image: csr12s-api/' > $LIVE/compose.yml
command grep -c 'image: csr12s-api' $LIVE/compose.yml; $DC config --quiet && echo compose-parses
cat /etc/ssl/certs/ca-certificates.crt $CA > $LIVE/ca-bundle.pem
keytool -importkeystore -noprompt -srckeystore /home/ben/sdk/java21/lib/security/cacerts -srcstorepass changeit \
  -destkeystore $LIVE/truststore.p12 -deststoretype PKCS12 -deststorepass changeit > /dev/null 2>&1
keytool -importcert -noprompt -alias csr12s-compose-ca -file $CA -keystore $LIVE/truststore.p12 -storetype PKCS12 -storepass changeit > /dev/null; echo "truststore exit=$?"
free -g | sed -n 2p
$DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log
```

Run the `up` with `run_in_background`; within 15 minutes `podman inspect -f '{{.State.Health.Status}}' csr12s-iverson-api-1` prints `healthy`. Then:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
( scratch_env; cd $WT/Iverson.Clients/TypeScript && systemd-run --user --scope -q -p MemoryMax=3G npm ci --no-fund --no-audit 2>&1 | tail -1 )
( scratch_env
  export IVERSON_GRPC_URL=https://localhost:8080 IVERSON_TOKEN_ENDPOINT=https://localhost:9443/application/o/token/
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)" IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  export SSL_CERT_FILE=$LIVE/ca-bundle.pem GRPC_DEFAULT_SSL_ROOTS_FILE_PATH=$LIVE/ca-bundle.pem NODE_EXTRA_CA_CERTS=$CA
  export JAVA_TOOL_OPTIONS="-Djavax.net.ssl.trustStore=$LIVE/truststore.p12 -Djavax.net.ssl.trustStorePassword=changeit -Djavax.net.ssl.trustStoreType=PKCS12"
  cd $WT/Iverson.Server/Iverson.ClientConformance && systemd-run --user --scope -q -p MemoryMax=4G dotnet run ) > $LIVE/conformance.out 2>&1; echo "harness exit=$?"
tail -25 $LIVE/conformance.out
$DC down -v > $LIVE/down.log 2>&1; echo "down exit=$?"
podman rmi csr12s-api localhost/csr12s-api > /dev/null 2>&1; true
rm -rf $WT/Iverson.Server/.env $WT/Iverson.Server/deploy/compose-tls
podman ps -a --format '{{.Names}}' | command grep -c '^csr12s-' | sed 's/^/csr12s containers left: /'
```

Expected: `secrets exit=0`; `2`; `compose-parses`; `truststore exit=0`; `added <n> packages …`; `harness exit=0` and every scenario row `ok` in all five language columns (the Java row builds through `mvnw` against the record); `down exit=0`; `csr12s containers left: 0`. A row that fails for a variable a toolchain needs and the allowlist lacks is fixed in `DriverRunner.cs` (Task 14's list, with a comment naming the toolchain) and recorded.

- [ ] **Step 4: A throwaway kind cluster with the engine, the policies and CNPG, as Terraform installs them.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
cat > $LIVE/kind.yaml <<'T17KIND'
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    image: kindest/node:v1.35.8@sha256:07b2536e30b803ed61d1677a79df6115f798ce64c80f9e22f6ed45afd09323c0
T17KIND
free -g | sed -n 2p
kind create cluster --name csr12s-admission --config $LIVE/kind.yaml > $LIVE/kind-create.log 2>&1; echo "kind create exit=$?"
podman update --memory 6500m csr12s-admission-control-plane > /dev/null && echo capped
O=$WT/Iverson.Server/deploy/terraform/modules/operators
helm upgrade --install policy-controller policy-controller --repo https://sigstore.github.io/helm-charts --version 0.10.8 \
  --namespace cosign-system --create-namespace \
  --set webhook.image.version=v0.15.1@sha256:0492bb264fb1d9bdc8e3f343ef542cc85b7dd7c7fd8d9524b453c2bd31a1d128 \
  --set-string webhook.resources.limits.cpu=1000m --set-string 'webhook.configData.no-match-policy=allow' --wait > $LIVE/pc.log 2>&1; echo "engine exit=$?"
helm upgrade --install image-policies $O/charts/image-policies --namespace cosign-system --wait > $LIVE/ip.log 2>&1; echo "policies exit=$?"
kubectl get clusterimagepolicies -o name
helm upgrade --install cnpg cloudnative-pg --repo https://cloudnative-pg.github.io/charts --version 0.29.0 \
  -f $O/versions/cloudnative-pg-values.yaml --namespace cnpg-system --create-namespace --wait > $LIVE/cnpg.log 2>&1; echo "cnpg exit=$?"
kubectl create namespace iverson; kubectl label namespace iverson policy.sigstore.dev/include=true
kubectl create namespace free
```

Run `kind create` and each `helm upgrade` with `run_in_background` and a Monitor on the closing line. Expected: `kind create exit=0`, `capped`, `engine exit=0`, `policies exit=0`, four `clusterimagepolicy.policy.sigstore.dev/iverson-…` lines, `cnpg exit=0`.

- [ ] **Step 5: The admission cases** (spec §3's behaviour table). `$API` and `$UI` are the published images at `main`'s current commit (both are public). The cross-image swap copies admin-ui and its attestations into an in-cluster registry under the api path (probe 2's method); the copy tool is a scratch script, never committed.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
SHA=$(git -C /home/ben/repositories/Iverson rev-parse origin/main); echo "main $SHA"
digest() { curl -fsSI -H 'Accept: application/vnd.oci.image.index.v1+json' -H "Authorization: Bearer $(curl -fsS "https://ghcr.io/token?scope=repository:counsellorben/$1:pull" | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')" "https://ghcr.io/v2/counsellorben/$1/manifests/$2" | tr -d '\r' | sed -n 's/^docker-content-digest: //Ip'; }
API_D=$(digest iverson-api $SHA); UI_D=$(digest iverson-admin-ui $SHA); echo "api $API_D ui $UI_D"
API=ghcr.io/counsellorben/iverson-api@$API_D; UI=ghcr.io/counsellorben/iverson-admin-ui@$UI_D
pod() { kubectl -n $1 run $2 --image=$3 --restart=Never --command -- sleep 3600 2>&1 | tail -2 | tr '\n' ' ' | cut -c1-2000; }
# A variant of the chart's api policy: a new name, a glob, and optionally another identity regexp.
cat > $LIVE/cip.py <<'T17CIP'
import subprocess, sys, yaml
chart, name, glob = sys.argv[1:4]
render = subprocess.run(["helm", "template", "x", chart], check=True, capture_output=True, text=True).stdout
policy = [d for d in yaml.safe_load_all(render) if d and d["metadata"]["name"] == "iverson-api"][0]
policy["metadata"]["name"] = name
policy["spec"]["images"] = [{"glob": glob}]
if len(sys.argv) > 4:
    policy["spec"]["authorities"][0]["keyless"]["identities"][0]["subjectRegExp"] = sys.argv[4]
print(yaml.safe_dump(policy))
T17CIP
IP=$WT/Iverson.Server/deploy/terraform/modules/operators/charts/image-policies
echo "A1 api by digest:      $(pod iverson a1 $API)"
echo "A2 api by tag:         $(pod iverson a2 ghcr.io/counsellorben/iverson-api:$SHA)"; kubectl -n iverson get pod a2 -o jsonpath='{.spec.containers[0].image}'; echo
echo "A3 third-party:        $(pod iverson a3 docker.io/library/busybox:1.37)"
echo "A4 unlabelled ns:      $(pod free a4 docker.io/library/busybox:1.37)"
# A5 the wrong identity: an extra policy on admin-ui that expects another workflow (every matching policy must pass).
python3 $LIVE/cip.py $IP csr12s-wrong-identity 'ghcr.io/counsellorben/iverson-admin-ui*' \
  '^https://github\.com/counsellorben/Iverson/\.github/workflows/other\.yml@refs/(heads/main|tags/v.+)$' | kubectl apply -f - > /dev/null
echo "A5 wrong identity:     $(pod iverson a5 $UI)"; kubectl delete clusterimagepolicy csr12s-wrong-identity > /dev/null
# A6 init container: a bad init container (an unattested copy, below) beside a good main container.
kubectl create namespace reg; kubectl -n reg create deployment registry --image=docker.io/library/registry:2 --port=5000
kubectl -n reg expose deployment registry --port=5000; kubectl -n reg rollout status deployment/registry --timeout=180s > /dev/null
kubectl -n reg port-forward svc/registry 15000:5000 > $LIVE/pf.log 2>&1 & PF=$!; sleep 3
cat > $LIVE/regcopy.py <<'T17COPY'
import json,sys,urllib.request,urllib.error
srcrepo=sys.argv[1]; dst=sys.argv[2]; dstrepo=sys.argv[3]; digest=sys.argv[4]; refs=len(sys.argv) < 6 or sys.argv[5] != "no-referrers"
fb="sha256-"+digest.split(":")[1]
tok=json.load(urllib.request.urlopen(f"https://ghcr.io/token?scope=repository:{srcrepo}:pull"))["token"]
ACC="application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.docker.distribution.manifest.list.v2+json"
def get(path):
    return urllib.request.urlopen(urllib.request.Request(f"https://ghcr.io/v2/{srcrepo}/{path}",headers={"Authorization":"Bearer "+tok,"Accept":ACC}))
seen=set()
def blob(d):
    if d in seen: return
    seen.add(d)
    try:
        urllib.request.urlopen(urllib.request.Request(f"http://{dst}/v2/{dstrepo}/blobs/{d}",method="HEAD")); return
    except urllib.error.HTTPError: pass
    data=get(f"blobs/{d}").read()
    loc=urllib.request.urlopen(urllib.request.Request(f"http://{dst}/v2/{dstrepo}/blobs/uploads/",method="POST",data=b"")).headers["Location"]
    if loc.startswith("/"): loc=f"http://{dst}"+loc
    loc+=("&" if "?" in loc else "?")+"digest="+d
    urllib.request.urlopen(urllib.request.Request(loc,method="PUT",data=data,headers={"Content-Type":"application/octet-stream"}))
def manifest(ref):
    r=get(f"manifests/{ref}"); body=r.read(); ct=r.headers["Content-Type"]; m=json.loads(body)
    if "manifests" in m:
        for c in m["manifests"]: manifest(c["digest"])
    else:
        if "config" in m: blob(m["config"]["digest"])
        for l in m.get("layers",[]): blob(l["digest"])
    urllib.request.urlopen(urllib.request.Request(f"http://{dst}/v2/{dstrepo}/manifests/{ref}",method="PUT",data=body,headers={"Content-Type":ct}))
manifest(digest)
if refs: manifest(fb)
print("copied", srcrepo, digest, "with referrers" if refs else "without referrers")
T17COPY
R=registry.reg.svc.cluster.local:5000
python3 $LIVE/regcopy.py counsellorben/iverson-admin-ui localhost:15000 counsellorben/iverson-api $UI_D
python3 $LIVE/regcopy.py counsellorben/iverson-api localhost:15000 counsellorben/iverson-api $API_D
python3 $LIVE/regcopy.py counsellorben/iverson-admin-ui localhost:15000 counsellorben/iverson-api-unsigned $UI_D no-referrers
kill $PF
# The api policy (same identity, same subject) for this registry's api path. Every case below is decided at
# admission, where the engine reads the registry itself, so the node never pulls from it.
python3 $LIVE/cip.py $IP csr12s-local-api "$R/counsellorben/iverson-api*" | kubectl apply -f - > /dev/null
echo "A6 swap (admin-ui under the api name): $(pod iverson a6 $R/counsellorben/iverson-api@$UI_D)"
echo "A7 real api copy (control):           $(pod iverson a7 $R/counsellorben/iverson-api@$API_D)"
kubectl -n iverson apply -f - <<T17INIT 2>&1 | tail -2 | cut -c1-2000
apiVersion: v1
kind: Pod
metadata: { name: a8 }
spec:
  initContainers: [{ name: init, image: "$R/counsellorben/iverson-api-unsigned@$UI_D", command: ["true"] }]
  containers: [{ name: main, image: "$API", command: ["sleep", "3600"] }]
T17INIT
```

Expected (the exact wording is the engine's; the substance must match):
- A1 `pod/a1 created`; A2 `pod/a2 created` and its stored image `ghcr.io/counsellorben/iverson-api:<sha>@sha256:…`; A3 `pod/a3 created`; A4 `pod/a4 created`.
- A5 refused: `… no matching attestations … expected SAN value to match regex "…other\.yml…", got "…publish-images.yml@refs/heads/main"`.
- The three copies print `copied …`; A6 refused with `subject.0.name: conflicting values "ghcr.io/counsellorben/iverson-admin-ui" and "ghcr.io/counsellorben/iverson-api"`; A7 `pod/a7 created`.
- A8 refused naming `spec.initContainers[0].image`.

- [ ] **Step 6: CNPG with the pinned images rolls nothing, `helm upgrade` over a rewritten Deployment works, and an engine outage refuses.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
IMG=$(sed -n 's/^imageName: "\(.*\)"$/\1/p' $WT/Iverson.Server/deploy/helm/iverson/charts/postgres/values.yaml); echo "$IMG"
kubectl -n iverson apply -f - <<T17PG
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata: { name: pg }
spec:
  instances: 1
  imageName: "$IMG"
  storage: { size: 1Gi }
T17PG
kubectl -n iverson wait cluster/pg --for=condition=Ready --timeout=600s
p0=$(kubectl -n iverson get pod pg-1 -o jsonpath='{.metadata.uid} {.spec.containers[0].image} {.spec.initContainers[0].image}'); echo "$p0"
for i in 1 2 3 4 5 6; do sleep 30; echo "t+$((i*30))s $(kubectl -n iverson get pod pg-1 -o jsonpath='{.metadata.uid}') $(kubectl -n iverson get cluster pg -o jsonpath='{.status.phase}')"; done
O=$WT/Iverson.Server/deploy/terraform/modules/operators
mkdir -p $LIVE/upchart/templates && cat > $LIVE/upchart/Chart.yaml <<'T17CHART'
apiVersion: v2
name: upchart
version: 0.1.0
T17CHART
printf 'apiVersion: apps/v1\nkind: Deployment\nmetadata: { name: up }\nspec:\n  replicas: 1\n  selector: { matchLabels: { app: up } }\n  template:\n    metadata: { labels: { app: up } }\n    spec: { containers: [{ name: main, image: "ghcr.io/counsellorben/iverson-api:%s", command: ["sleep", "3600"] }] }\n' "$(git -C /home/ben/repositories/Iverson rev-parse origin/main)" > $LIVE/upchart/templates/up.yaml
helm upgrade --install up $LIVE/upchart -n iverson --wait > $LIVE/up1.log 2>&1; echo "install exit=$?"; kubectl -n iverson get deploy up -o jsonpath='{.spec.template.spec.containers[0].image}'; echo
helm upgrade up $LIVE/upchart -n iverson --wait > $LIVE/up2.log 2>&1; echo "no-change upgrade exit=$?"; kubectl -n iverson get deploy up -o jsonpath='{.metadata.generation}'; echo
kubectl -n cosign-system scale deployment policy-controller-webhook --replicas=0; kubectl -n cosign-system wait --for=delete pod -l control-plane=policy-controller-webhook --timeout=120s > /dev/null
echo "engine down, iverson: $(kubectl -n iverson run d1 --image=docker.io/library/busybox:1.37 --restart=Never --command -- sleep 60 2>&1 | tail -1 | cut -c1-200)"
echo "engine down, free:    $(kubectl -n free run d2 --image=docker.io/library/busybox:1.37 --restart=Never --command -- sleep 60 2>&1 | tail -1)"
kubectl -n cosign-system scale deployment policy-controller-webhook --replicas=1
```

Expected:
- The pinned `imageName` (`…:18.4-system-trixie@sha256:42708a75…`); `condition met`; the pod's uid, its image (the same `tag@digest` string) and its init image (`ghcr.io/cloudnative-pg/cloudnative-pg:1.30.0@sha256:a2701eb9…`); six lines with the same uid and phase `Cluster in healthy state` (no rollout over three minutes). This is also row 47's check that CNPG's webhook accepts the `tag@digest` `imageName`: if the `apply` of the `Cluster` is refused, stop and report it to Task 8.
- `install exit=0` and the Deployment's stored image `…:<sha>@sha256:…`; `no-change upgrade exit=0` and generation `1`.
- `engine down, iverson: … failed calling webhook "policy.sigstore.dev" …` (refused); `engine down, free: pod/d2 created`.

- [ ] **Step 7: Tear down and compare with the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/aab9d0ce-9a20-4b83-9e6a-e2d3793a50aa/scratchpad/csr12s-sdd/env.sh
kind delete cluster --name csr12s-admission > /dev/null 2>&1; echo "kind delete exit=$?"
podman ps -a --format '{{.Names}}' | sort > $LIVE/after-containers.txt; podman volume ls -q | sort > $LIVE/after-volumes.txt
podman network ls --format '{{.Name}}' | sort > $LIVE/after-networks.txt
for k in containers volumes networks; do diff $LIVE/before-$k.txt $LIVE/after-$k.txt > /dev/null && echo "$k unchanged" || { echo "$k differ:"; diff $LIVE/before-$k.txt $LIVE/after-$k.txt; }; done
git -C $WT status --porcelain
```

Expected: `kind delete exit=0`; containers, volumes and networks unchanged; an empty worktree status (nothing to commit; the live record stays under `$SCR/live`).

---

## Rollout after the merge (R1–R4)

Not part of subagent-driven development: these are live changes to GitHub, GitLab and AWS, made after the branch is merged and pushed, **each confirmed by the user at the step**, in this order. The commands and checks are in `docs/runbooks/csr12-supply-chain.md` §1.

- [ ] **R1 (runbook step 2.1): GitLab `NVD_API_KEY`**, only if the user confirms a GitLab project runs this pipeline: the one scoped definition, every other definition deleted.
- [ ] **R2 (runbook steps 2.2–2.4): checks on `main`:** `nvd-update` saves a cache and the scan runs keyless (GitHub; GitLab's scheduled run passes the absence check); Dependabot's three `helm` jobs succeed and open the stub bumps; `pin-freshness` runs once by hand and its issues are reviewed. Spec §7 expects a pull-request run of the keyless path: if the branch merges without a pull request, the first pull request afterwards (a Dependabot one serves) must show `nvd-update` skipped and `owasp-dependency-check-java` passing.
- [ ] **R3 (runbook step 3): the repository's settings,** each confirmed: delete the Dependabot `NVD_API_KEY` (only after R2's keyless pass); push protection; the tag ruleset (`--dry-run`, create disabled, read back, the UI check, `--activate`; if the read-back does not show `current_user_can_bypass=always`, `--activate` refuses: leave the ruleset disabled and stop here, CIR-1 §3.1 A); remove `vadim82`; branch protection last, once all 22 checks are green.
- [ ] **R4 (runbook step 4): the AWS account:** `terraform init`; confirm the state bucket `iverson-terraform-state` and lock table `iverson-terraform-locks` are the user's (stop if not) and import them and the bucket's three settings, which `bootstrap/aws` declares but no state holds; import an existing GitHub OIDC provider if there is one; `terraform plan` must add only the three IAM resources; apply; set `PIN_FRESHNESS_AWS_ROLE` (CIR-1 §3.3 A).

---

## Tasks NOT in this plan

(From the spec's §9 "Out of scope", verbatim.)

- `docs/security/tma.md` (next TMA refresh).
- Four unmerged research branches (`student-distill`, `simple-jev-judge`, `teacher-lenient`, `rfdt-tenant`) call `teacher_rerank.py --api-key` or carry their own `--api-key` flags; each needs §5.3's change when it merges.
- The runtime-floating toolchains (`node-version: "22"`, `go-version: "1.26"` with `check-latest`, `dotnet-version: "10.0.x"`, `java-version: "21"`, `python-version: "3.11"`) float within their line by design and are not pins.

## Known issues inherited from spec

(From the spec's §8, verbatim. These exist in the implementation by design, accepted by the user.)

Accepted by the user (counsellorben), 2026-10-26:
1. **Admin bypass, no required reviews.** The user is the only writer and pushes to `main` directly; a stolen admin credential could disable any protection.
2. **kind, local and compose do not verify images** (unsigned local builds; tag-only local images would be refused, VA 22).
3. **TUF at engine start.** A policy-controller restart while `tuf-repo-cdn.sigstore.dev` is unreachable crash-loops the engine and refuses first-party pod creates until it returns. The startup fetch is unconditional in v0.15.1; a `TrustRoot` does not avoid it; an in-cluster mirror would (VA 24) and was declined.
4. **Maven Dependabot PRs need a checksum re-record** (one command, run through `./mvnw`) before they go green, and a Maven or wrapper bump needs a re-record and new `distributionSha256Sum`/`wrapperSha256Sum`; Dependabot does not update the file (VA 39).
5. **The EKS freshness rows stay inert** until the AWS role is applied.
6. **Branch protection waits** for the upstream `nginx-unprivileged` rebuild that carries `brotli-libs` 1.2.1 (CVE-2026-111497).
7. **Model-image content-tag binding** stays as accepted in round 11 (any digest the workflow signed under a content tag).
8. **Strimzi- and StarRocks-operator-managed workloads in `iverson`** are unverified against the engine's `tag@digest` rewrite (CDR-1 §3.1 and CDR-2 §3.1, option A pins only CNPG's PostgreSQL and operator images).
9. **The GitLab NVD cache hand-off is unverified until rollout.** Whether `owasp-dependency-check-java`'s cache pull sees `nvd-update`'s push depends on the runner's cache configuration, which is not in the repository; §6 step 2's GitLab run on `main` is the check (CDR-7 §3.2, option A). A miss fails that job loudly (`NoDataException`, VA 3), exposes no key, and nothing irreversible follows, because §6 step 3 deletes the GitHub Dependabot secret only after step 2's pass.

Found while planning, outside the spec's scope (accepted by the user 2026-10-26):
- `Iverson.Server/Iverson.Vector/Dockerfile` does not build on `main` (18 compile errors: it copies none of the projects Vector references); Task 1 checks only its locked restore.
- The policy-controller chart's leases-cleanup Job uses the floating `cgr.dev/chainguard/kubectl:latest-dev`; it runs only as a `post-delete` hook, on uninstall.
- GitHub does not document the admin role's ruleset `actor_id`; `apply-tag-ruleset.sh` sends 5 and gates activation on the read-back and the UI check.
- GitHub does not document what `current_user_can_bypass` reports for a disabled ruleset; if R3's read-back is not `always`, the ruleset stays disabled and R3 stops there (accepted by the user 2026-10-26, CIR-1 §3.1 A).
- Whether the publish job's `packages: write` token reads the packages endpoint is not documented; if it does not, the new rule fails the run after three denials (fail closed).
