# CSR Round 11 — Clients, Tooling and CI Design (Sub-project D)

**Goal:** sub-project D of the round-11 remediation closes the client-SDK, developer-tooling and CI/supply-chain findings of `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`.

| Finding | Summary | Severity |
|---|---|---|
| #15 | LoadTest and the conformance harness send an Operator-level token and Authentik credentials to any http endpoint they are given | Low |
| #16 | The Java SDK's TLS constructors refuse credentials, so every authenticated Java client must pass the "insecure" opt-in | Low |
| #17 | The agent CLI prints tenant-authored document titles to the terminal unneutralized | Low |
| #19 | Tooling, CI and dependency hygiene (grouped): password on argv, CodeQL token, GitLab/CI drift, Go toolchain, Launcher image, no signing/SBOM/publish, operator charts outside Dependabot, branch protection, no Python lockfiles, legacy IdentityModel | Low |
| #22 | Two .NET per-user acting-token entry points skip round-10 #24's plaintext guard (Python has the same residual) | Info |
| #23 | Agent prompt-injection residual: model text echoed outside the escaped boundary | Info |

Sub-projects A, B and C merged on local `main` (C at `34656c26`). This is the last sub-project of round 11.

**Decisions (user, 2026-10-12):**
- **Scope:** all of #15, #16, #17, #22, #23 and **everything in #19**, in **one spec with phased tasks** (a D1 code / D2 supply-chain split was declined).
- **Publishing:** first-party images go to **GHCR from GitHub Actions**, signed keyless with cosign, with a signed SPDX SBOM attestation. Packages are made **public** (one-time manual step).
- **Branch protection:** add the security scans to the required checks; **keep the admin bypass** (`enforce_admins: false`) and no required reviews, so the maintainer's direct push to `main` keeps working (accepted residual).
- **Java guard (#16):** a **send-time** guard on the channel's security level (option A).
- **Operator charts:** pins move to a **Dependabot-tracked stub `Chart.yaml`** that Terraform reads.
- **Python lockfiles:** **pip-compile** with hashes.
- **Go:** the Go SDK moves to **Go 1.26** (go1.25 is out of upstream support; nine stdlib advisories have no 1.25 fix).
- **Scans red on `main` today:** **fix them, then require them** (govulncheck, the admin console image scan, both npm audits).
- **OWASP dependency-check (Java):** stays **optional** (it fails on Dependabot PRs, which get no NVD key).
- **kube-score `security-audit`:** stays **optional** (chart-wide lint, red on every recorded run).
- **`OAuth2ClientCredentials` flag (CDR-1 §3.1, option A):** its one `allowInsecureCredentials` flag covers both the token endpoint and the channel.
- **Ambient tokens on hand-built coordinators (CDR-1 §3.2, option A):** the .NET and Python coordinator constructors refuse an ambient token that would be sent over an unknown transport, as `SchemaCatalogClient` does.
- **In-cluster Host header (CDR-1 §3.3, option A):** verified by a kind live check before the runbook note ships.
- **Protection PUT body (CDR-1 §3.4, option C):** the user first applies the `checks`-only body to a throwaway public repo.
- **GitLab variable scope (CDR-1 §3.5, option A):** GitLab's documented variable semantics are accepted; `NVD_API_KEY` must be a Protected or schedule variable.

## 1. SDK credential guards (#16, #22)

**Java (#16).**
- A package-private `TransportGuardedCredentials` (a `CallCredentials`) wraps the caller's credentials together with the `allowInsecureCredentials` flag. In `applyRequestMetadata`, when `requestInfo.getSecurityLevel()` is not `PRIVACY_AND_INTEGRITY` and the flag is false, it calls `applier.fail(Status.UNAUTHENTICATED…)` with a non-procedural description (credentials need a TLS channel); otherwise it delegates with the original `RequestInfo` (the acting token is read from its call options).
- `IversonClient`'s two credential-attaching constructors (`(ManagedChannel, CallCredentials, boolean)` and `(ManagedChannel, CallCredentials, String, boolean)`) wrap non-null credentials once. Every other constructor and factory funnels into them, so every call path is covered.
- The host/port TLS constructors no longer throw; the raw-channel constructors no longer require the flag on a TLS channel. The deprecated two- and three-argument raw-channel overloads lose `@Deprecated`, and their javadoc is corrected.
- A raw-channel constructor given an acting-user token with **null** credentials stops throwing: with no `CallCredentials`, nothing carries the token.
- The `plaintext(…)` factories keep `requireInsecureCredentialsOptIn` at construction (fail fast; their transport is known).
- `OAuth2ClientCredentials.applyRequestMetadata` applies the same security-level check with its own `allowInsecureCredentials`, so it also refuses when used on a caller's own stubs outside `IversonClient`. That flag already allows a non-https token endpoint; it now also governs the channel, so an `OAuth2ClientCredentials` on a plaintext channel needs its own `allowInsecureCredentials=true` even when its token endpoint is https and `IversonClient` is opted in. The javadoc says so (user decision 2026-10-12, CDR-1 §3.1 A).

**.NET (#22): fail closed when the transport is unknown.**
- `ActingUserIdentity`'s `refusesPlaintextTokens` defaults to `true`. `AddIversonClient` already passes the computed value, so DI behaviour is unchanged.
- `EntityCoordinator<T>.WithActingUser` refuses unless `identity?.RefusesPlaintextTokens == false`. A hand-built coordinator with no identity can no longer bind a per-call token (stated in the XML docs).
- `EntityCoordinator<T>`'s constructor throws when the identity carries a token provider and `RefusesPlaintextTokens` is true, mirroring `SchemaCatalogClient` (user decision 2026-10-12, CDR-1 §3.2 A). DI is unaffected, because `AddIversonClient` throws first.
- `SchemaCatalogClient(mapping, ActingUserIdentity? identity = null)` replaces the `Func<Task<string>>?` parameter and throws at construction when the identity carries a token provider and refuses plaintext. `AddIversonClient` passes its singleton (in DI this state is unreachable, because `AddIversonClient` throws first).
- Moving the acting token into `CallCredentials` was rejected: grpc-dotnet drops call credentials on an `http://` channel without the opt-in and still sends the call (Verified assumption 3), which would turn a refusal into a call under the service identity.

**Python (#22).** The hand-built `EntityCoordinator`'s `refuse_plaintext_token` defaults to `True`, and `EntityCoordinator.__init__` raises when it is given an ambient `acting_user_token` while `refuse_plaintext_token` is true (CDR-1 §3.2 A). `IversonClient.coordinator()` keeps passing the computed value; its constructor already refuses an ambient token on plaintext without the opt-in.

## 2. Tooling (#15, #17, #23, #19 password on argv)

**LoadTest (#15).**
- One startup validation in `Program.cs`, before any credential-sending command (ahead of the provisioning block), over the gRPC URL, the token endpoint (if set) and the Authentik flow base URL. It uses `Uri.IsLoopback`.
- The gRPC URL must be loopback or `https://`. The token endpoint and the Authentik base URL must be `https://` or loopback. It first checks the token endpoint's `/application/o/token/` shape, so a malformed value fails with a clear message instead of an opaque `IndexOf` crash.
- `allowInsecureChannelCallCredentials` becomes "the gRPC URL is loopback" instead of `true`.
- The startup check is what protects the paths the SDK flag cannot see: the bare provisioning channel (`EnsureTenantProvisionedAsync`, admin-automation bearer as raw metadata) and the raw `Metadata.WithActingUser` sites in the read-path, benchmark-query and smoke-test commands.

**Conformance harness (#15).** Its channel hard-codes `UnsafeUseInsecureChannelCallCredentials = true` with `ChannelCredentials.Insecure`, so it can only speak plaintext. One startup check in `Program.cs`: the gRPC URL must be loopback, and the token endpoint and the Authentik base URL must be `https://` or loopback. The five child drivers receive the same URL and secrets, so the one check covers them.

**Agent CLI (#17).**
- `__main__.py` gains `_printable(text)`: it removes C0 controls except newline and tab, DEL, and C1 controls (`\x80`–`\x9f`).
- Applied to everything printed from tenant or model data: the answer text, each citation's key and title, and the flags.
- `main()` gains a top-level `except Exception` that prints `type(e).__name__` and a `_printable` message to stderr and exits non-zero, so tracebacks no longer carry server or model text raw.
- `retrieval.py`'s two `log.warning` calls that format server text with `%s` switch to `%r`.
- The evaluate path prints only `json.dumps` of numbers and is unchanged.

**Agent prompt echoes (#23).** `session.py` passes three echoes through the existing `retrieval._escape`: `No documents were found for: …` in `_page`, `No new documents were found for: …` and `Unknown tool …` in `_run_tool`.

**Password on argv (#19).**
- `mint_acting_user_token.py` drops `--password` and reads `IVERSON_ACTING_USER_PASSWORD` (LoadTest's existing name for the same smoke-test user) in both branches: compose requires it; kind uses it first, then the kind Secret.
- `pattern_leg.py` (the only code caller) passes the bypass user's password to the child as `env={**os.environ, "IVERSON_ACTING_USER_PASSWORD": …}`.
- Text updates: the script's help and error text, `pattern_leg.py`'s module docstring, and `docs/user-management-and-security.md:270-273` plus its CLI table's `--password` row (`:286-289`), which is replaced by a note that the password comes from `IVERSON_ACTING_USER_PASSWORD`.

**Runbook.** `docs/runbooks/kind-cluster-troubleshooting.md` §5.1's note on running LoadTest from an in-cluster debug pod says the targets must be `https://<release>-api:8080` and `https://<release>-authentik:8443` (trusting the chart CAs), that `IVERSON_TOKEN_ENDPOINT` must be set explicitly, because its default is loopback, and that `IVERSON_ACTING_USER_HOST_HEADER` must be `<release>-authentik:8443`, the issuer the API reads from its 8443 metadata (Verified assumption 29). The note's claim that Host headers match the in-cluster names naturally is dropped: LoadTest always sets the Host.

## 3. CI and dependency hygiene (#19)

**GitHub workflows.**
- `codeql.yml`'s checkout sets `persist-credentials: false`. The new publish workflow (§5) does too. The other workflows hold only `contents: read`.
- `image-scan.yml`'s `azure/setup-helm` gets `version: v3.15.0`.
- Unpinned installs in `dependency-scan.yml` are pinned: `pip-audit`, and the `pip install --upgrade setuptools` lines.
- `deploy-validate.yml` drops its `paths:` filter and adds `push: branches: [main]`, so its jobs report on every PR and on `main` (§6; Verified assumption 30). `security-audit` (not required) then also runs on `main` pushes, where it is red.

**GitLab (`.gitlab-ci.yml`).**
- Every `image:` is pinned as `name:tag@sha256:…` (keeping the tag for Dependabot). `alpine/helm:3.15.0` does not exist, so the three helm jobs move to `alpine/helm:3.15.4@sha256:105741fa…`.
- `govulncheck` is pinned to a release instead of `@latest`; `pip-audit` is pinned.
- The OWASP job, the only job that references `NVD_API_KEY`, gets a job-level `rules:` that excludes `merge_request_event` pipelines, and `NVD_API_KEY` must be defined in GitLab as a Protected variable (or a pipeline-schedule variable): a project-level variable reaches every job of an MR pipeline whatever the job rules say (Verified assumption 31). The runbook records this one-time setting.
- Go jobs use `golang:1.26@sha256:…` (§4).

**Python lockfiles.**
- `Iverson.Clients/Python` and `Iverson.Agents/Python` each gain a `requirements.in` (runtime and dev specifiers, mirroring `pyproject.toml`) and a `requirements.txt` compiled with `pip-compile --generate-hashes --allow-unsafe --output-file=requirements.txt requirements.in`.
- The `.in` file is what makes Dependabot treat the `.txt` as pip-compile output; `--allow-unsafe` keeps `setuptools` (needed by the client's `grpcio-tools`) in the hashed set.
- CI pip-audit jobs (GitHub and GitLab): `pip install --require-hashes -r requirements.txt`, then `pip install --no-deps .`, then audit with a separately installed, pinned `pip-audit`. The agent job puts the client on its path as the repo's venv does.

**.NET IdentityModel.** `Iverson.Client.Core` and `Iverson.Client.Conformance.Driver` move from `IdentityModel` 7.0.0 to `Duende.IdentityModel` 8.1.0 (Apache-2.0). Four files change `using IdentityModel.Client` to `using Duende.IdentityModel.Client`. LoadTest's assertion path already sets `PostBody`, which 8.x requires for client assertions.

**Launcher image.** `Iverson.Server/Iverson.Launcher/Dockerfile` and its `dependabot.yml` docker entry are deleted (nothing builds or runs the image; the project itself stays).

**Operator charts.**
- The five remote pins (CNPG `cloudnative-pg`, Strimzi `strimzi-kafka-operator`, the StarRocks operator chart `operator`, `cluster-autoscaler`, `aws-load-balancer-controller`) move to a dependencies-only stub `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml`, keeping each repository literal exactly (including Strimzi's trailing slash).
- `modules/operators/main.tf` builds a map from `yamldecode(file(…)).dependencies` and each `helm_release` reads `repository` and `version` from it. The local `pvc-storageclass-policy` chart is unchanged.
- `dependabot.yml`'s helm directories gain `…/modules/operators/versions`.
- `image-scan.yml`'s `tf_pin` reads the stub YAML instead of `main.tf`.
- `deploy/kind/setup.sh` and `setup.ps1`, which hard-code the same three operator versions, read them from the stub, so a Dependabot bump cannot desync kind.

## 4. Scans red on `main` today

- **govulncheck (Go SDK):** `Iverson.Clients/Go/go.mod` moves to `go 1.26.0` with `toolchain go1.26.9`, and `golang.org/x/net` to v0.60.0. `dependency-scan.yml` uses Go `1.26` and installs `govulncheck` after `setup-go`. Go SDK consumers need Go ≥ 1.26 (noted in the repository README's client list, `README.md:71`; `go.mod`'s `go 1.26.0` directive enforces it).
- **Admin console image scan (libtiff CVE-2026-4775):** the runtime base `nginx-unprivileged:1.30.5-alpine` moves from `sha256:15c994d1…` to `sha256:56b8272e…`, which carries `tiff 4.7.2-r0`. Compose's Authentik tls-proxy pins the same old digest and moves too.
- **npm audits (admin console and TS SDK):** `source-map-js` 1.2.1 → 1.2.2 in both lockfiles (`npm audit fix`, lockfile only).

## 5. Publishing and signing (#19)

**Workflow `.github/workflows/publish-images.yml`.**
- Triggers: push to `main`, tags `v*`, `workflow_dispatch`. Permissions per job: `contents: read`, `packages: write`, `id-token: write`. Checkout uses `persist-credentials: false`. Every action is SHA-pinned.
- **iverson-api and iverson-admin-ui** build on every trigger from the repo root (as `image-scan.yml` does) and push to `ghcr.io/counsellorben/iverson-api` and `…/iverson-admin-ui`, tagged with the commit SHA on `main` and with the version (`vX.Y.Z` → `X.Y.Z`) on tags; never `latest`.
- **Model images** (`iverson-tei-model`, `iverson-ollama-model`) build only on tags and `workflow_dispatch`. Their build args and tags are read from the chart render, as `deploy/kind/build-and-load-image.sh` does, so the pushed tag is exactly the content-derived tag the chart references (model, revision or digest, and the Dockerfile hash). Each builds from its own `model-image` directory.
- For each pushed image: `org.opencontainers.image.source` label; `cosign sign --yes <image>@<digest>` (keyless, GitHub OIDC; recorded in the public Rekor log); an SPDX-JSON SBOM of the digest from `anchore/sbom-action`, attached with `cosign attest --yes --type spdxjson`.

**Chart.**
- `values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml` set `api.image.repository` and `worker.image.repository` to `ghcr.io/counsellorben/iverson-api`, `adminUi.image.repository` to `ghcr.io/counsellorben/iverson-admin-ui`, and `global.modelImageRegistry` to `ghcr.io/counsellorben/` (trailing slash required).
- Image tags stay operator-supplied (`--set api.image.tag=…,worker.image.tag=…,adminUi.image.tag=…`), as the chart's comments require; the runbook says so. kind and compose keep their local names.

**Visibility.** New GHCR packages are private. After the first publish, each of the four packages is switched to public once in its GitHub package settings (no REST endpoint exists for user-owned package visibility). Until then cloud pulls fail with an authorization error (the chart has no `imagePullSecrets`).

## 6. Branch protection (#19)

- `Iverson.Server/deploy/scripts/apply-branch-protection.sh` makes one `gh api -X PUT repos/<owner/name>/branches/main/protection --input <body>` call, with `--repo owner/name` (default `counsellorben/Iverson`) and `--dry-run` printing the body.
- The body restates every current setting (`strict: false`, `enforce_admins: false`, `required_pull_request_reviews: null`, `restrictions: null`, and every boolean false as today), using `checks` with `app_id` 15368 (github-actions).
- Required checks: today's 11 (`nuget-audit`, `npm-audit-adminui`, `npm-audit-ts-sdk`, the six `Analyze (…)`, `dotnet-build-test`, `typescript-sdk-build-test`) plus `govulncheck`, `pip-audit-python-sdk`, `pip-audit-agents`, `first-party (iverson-api, Iverson.Server/Iverson.Api/Dockerfile)`, `first-party (iverson-admin-ui, Iverson.AdminUI/Dockerfile)`, `third-party`, `helm-lint`, `helm-template-kubeconform`, `terraform-validate (aws)`, `terraform-validate (azure)`, `terraform-validate (gcp)` (22 in all).
- Not required: `owasp-dependency-check-java`, `security-audit` (user decisions), and `admin-ui`'s build jobs (path-filtered build/test, not scans).
- The script runs only after every required check is green on `main`, and only when the user says so (it changes a live GitHub setting). Before the first real PUT on Iverson, the user runs it with `--repo` against a throwaway public repo they administer, to confirm GitHub accepts the `checks`-only body; if it is rejected, the body changes before Iverson's protection is touched (user decision 2026-10-12, CDR-1 §3.4 C). The runbook names the order.

## 7. Testing

- **Java:** in-process netty plaintext and TLS servers (as in Verified assumption 1): a TLS host/port client with credentials constructs without the flag and its call succeeds; a plaintext raw channel with credentials and no flag fails UNAUTHENTICATED and the server sees no call; a plaintext factory without the flag still throws at construction; `OAuth2ClientCredentials` used directly on a plaintext stub fails likewise, and under an opted-in `IversonClient.plaintext(…, true)` it still fails unless its own flag is true. The two construction-refusal tests in `IversonClientTransportSecurityTest` are rewritten.
- **.NET:** default-refusal tests for a hand-built `ActingUserIdentity`, `EntityCoordinator` (per-call binding and the ambient constructor refusal) and `SchemaCatalogClient`; the existing test sites that build them directly pass `refusesPlaintextTokens: false` (via `TestCoordinatorFactory` and the identity-resolution tests, including the ambient identities at `EntityCoordinatorIdentityResolutionTests.cs:67, :80, :103, :145`).
- **Python SDK:** default-refusal tests (per-call binding and the ambient constructor refusal); the coordinator tests that pass an ambient token (`test_entity_coordinator.py:134, :284, :296, :322, :335`) pass `refuse_plaintext_token=False`.
- **LoadTest and conformance:** unit tests of the startup validation (`localhost`, `127.0.0.1`, `[::1]` and `https://` accepted where allowed; `http://iverson-api:8080` and a malformed token endpoint refused).
- **Agent:** `_printable` with ESC, BEL, a C1 character, DEL and newline; the top-level handler's output; the three echoes escape `<` and `>`; the `%r` warnings. `test_missing_credentials_fail_before_any_connection` is rewritten to assert a non-zero return, stderr naming `KeyError` and `IVERSON_CLIENT_SECRET`, and that `IversonClient` is never constructed.
- **Mint script:** `pattern_leg`'s subprocess argv carries no password (stubbed `subprocess.run`); the script reads the env var in both branches.
- **CI and deps:** `actionlint` on edited and new workflows; GitLab YAML parse check; `terraform fmt`/`validate`/`tfsec` on the operators module and roots, plus a check that the `yamldecode` values equal today's literals; the image-scan pin reader and `setup.sh`'s reader run against the stub; hashed installs from both lockfiles plus the client (241) and agent (68) suites; .NET builds and suites after the Duende swap; `govulncheck` clean on Go 1.26; `npm audit --audit-level=high` clean in both packages; Helm renders and lint on all five profiles with the GHCR values; `apply-branch-protection.sh --dry-run` output matches the body in §6.
- **Kind live check (CDR-1 §3.3 A):** on the laptop kind profile, an in-cluster LoadTest token mint through `https://<release>-authentik:8443` with `IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:8443`, and the API accepting that token, confirm Verified assumption 29 before the runbook note ships.
- **Not exercised before push:** the publish workflow's first real run (push, signing, attestation), CodeQL with `persist-credentials: false`, the Dependabot behaviour on the stub chart and the `.in` files, and the image scan of the new admin console digest in CI. After the user pushes, the runbook's `cosign verify` and `cosign verify-attestation` commands are the acceptance check.

## 8. Rollout

1. Merge; the user pushes `main`.
2. `publish-images.yml` runs on the push; `workflow_dispatch` once for the model images.
3. The user makes the four GHCR packages public and runs the runbook's verification commands.
4. Once every check in §6 is green on `main`, the user runs `apply-branch-protection.sh --repo <throwaway>` once, then runs (or asks for) the run against Iverson.

The runbook is `docs/runbooks/csr11-clients-tooling-ci.md`: package visibility, image verification (`cosign verify` / `cosign verify-attestation --type spdxjson` with `--certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$'` and `--certificate-oidc-issuer https://token.actions.githubusercontent.com`), deploying with image tags, branch protection (throwaway repo first), the GitLab `NVD_API_KEY` setting, and `IVERSON_ACTING_USER_PASSWORD`.

## Known issues / accepted as out of scope

- **Admin bypass on `main`** (user decision 2026-10-12): `enforce_admins` stays false and no reviews are required; the maintainer's direct pushes skip the required checks.
- **OWASP dependency-check and kube-score `security-audit` stay optional** (user decisions 2026-10-12). kube-score reports 9–12 CRITICAL findings per profile (probes, ephemeral storage, read-only root FS, resources, PDBs, NetworkPolicy targets). With `deploy-validate` now running on `main` pushes, `security-audit` shows red there too.
- **No admission-time signature verification:** images are signed and attested, but clusters do not refuse unsigned images.
- **Go SDK consumers need Go ≥ 1.26.**
- **Hand-built .NET/Python coordinators** without an explicit identity or flag can no longer bind per-call acting tokens, or be constructed with an ambient one.
- **`OAuth2ClientCredentials` on plaintext** needs its own `allowInsecureCredentials=true` in addition to `IversonClient`'s opt-in (user decision 2026-10-12, CDR-1 §3.1 A).
- **Token endpoints that redirect:** LoadTest's and the harness's token clients follow redirects; the startup check sees only the configured URL.
- **First publish is unverified until pushed** (see §7).

## Verified assumptions

Evidence was gathered on 2026-10-12 at `34656c26`: scratch Java, .NET and Python probes; scratch builds and test runs of copied projects; read-only `gh api` GETs against `counsellorben/Iverson`; anonymous registry API reads (no image pulls); vendor documentation.

**Probes**
1. **grpc-java 1.84.0 exposes the transport at send time:** a `CallCredentials` saw `SecurityLevel` NONE on a netty plaintext channel and PRIVACY_AND_INTEGRITY on a TLS channel. A wrapper that calls `applier.fail(UNAUTHENTICATED)` on NONE produced a client-side failure; the server recorded zero calls and zero auth headers, and the inner credentials never ran. TLS calls succeeded.
2. **The Java acting token reaches the wire only through `OAuth2ClientCredentials.applyRequestMetadata`** (`OAuth2ClientCredentials.java:63-78`); the SDK has no client interceptor. Credentials attach only at `IversonClient.java:195-198` and `:232-235`; every other constructor funnels there. The conformance driver's `DualHeaderCredentials` is caller-supplied, so the wrapper covers it.
3. **grpc-dotnet 2.83.0 drops `CallCredentials` on `http://` without the opt-in and still sends the call:** a capture handler saw the request with no `authorization` header; with `UnsafeUseInsecureChannelCallCredentials` or on `https` the header was present.
4. **`AddIversonClient` already refuses an ambient token on plaintext** (`ServiceCollectionExtensions.cs:73-84`) and passes the computed flag (`:146-149`); its predicate is the `http` scheme.
5. **Construction sites:** .NET `ActingUserIdentity` only at `ServiceCollectionExtensions.cs:146` and tests; hand-built `EntityCoordinator<T>` in the conformance driver (`Program.cs:134`, `:917`, neither calls `WithActingUser`) and tests; `SchemaCatalogClient` at `:153`, the driver's `new SchemaCatalogClient(mapping)` (`Program.cs:332`) and tests. Python's `IversonClient.coordinator()` passes the flag explicitly (`core.py:968-971`); hand-built coordinators in the conformance driver and LoadTest scripts never call `with_acting_user`. The tests that break are enumerated (Java `IversonClientTransportSecurityTest:63-68, :101-106`; .NET identity-resolution, pipeline, match-pattern via `TestCoordinatorFactory`, `SchemaCatalogClientTests:52`; Python `test_entity_coordinator.py:284-288, :335-339`).
6. **LoadTest's credential-bearing endpoints** are the gRPC URL (`Program.cs:36`), the token endpoint (`:37`) and the Authentik base URL (`:77-79`). The provisioning channel (`:392-394`) and raw `Metadata.WithActingUser` sites (`ReadPathScenario.cs:66,115,230,298`, `BenchmarkQueryScenario.cs:275`, `Program.cs:270`) bypass the SDK flag. Every documented run (LoadTest README, `bench-env.sh`, `csr11-identity-plane.md` port-forward) is loopback.
7. **The conformance harness** hard-codes `UnsafeUseInsecureChannelCallCredentials = true` with `ChannelCredentials.Insecure` (`Program.cs:96-104`); its only documented run (`client-conformance-matrix.md`) is compose on localhost.
8. **`Uri.IsLoopback`** (net10.0) is true for `localhost` in any case, `127.0.0.1`, `127.1`, `[::1]` and `[::ffff:127.0.0.1]`, and false for `localhost.`, `0.0.0.0` and `foo.localhost`.
9. **Agent CLI terminal sinks:** prints at `__main__.py:100, 102, 104`; uncaught exceptions (no handler in `main()`) carrying `err.details()`, `ModelRefused` explanations and document keys; `log.warning` at `retrieval.py:77-78, 103-104` reaching stderr through the last-resort handler. The evaluate JSON carries numbers only.
10. **Agent prompt echoes:** `retrieval._escape` is importable (planner and evaluate already import it); the unescaped model text is the two "found for" echoes and `Unknown tool {name}`.
11. **The mint script** reads `--password` in both branches (`:470-478` compose, `:490` kind); `pattern_leg.py:669` is its only code caller; `docs/user-management-and-security.md:270-273` and its CLI table (`:286-289`) document the old flag.
12. **CodeQL** has one checkout and a plain `mvn` Java build with no SCM plugin; `image-scan.yml:48` is the only unversioned `setup-helm`; unpinned installs are `pip-audit` (`dependency-scan.yml:91,105`, `.gitlab-ci.yml:188,197`) and `setuptools` (`dependency-scan.yml:90,104`).
13. **GitLab images resolve to digests** (registry HEAD, no pull), except `alpine/helm:3.15.0`, which returns 404; `alpine/helm:3.15.4` is `sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2`. `NVD_API_KEY` appears only in the OWASP job (`.gitlab-ci.yml:177,180`).
14. **Go:** govulncheck v1.7.0 on go1.25.14 reports nine stdlib advisories (GO-2026-6617, 6613, 6612, 6611, 6610, 6608, 6607, 6605, 6603) with no 1.25 fix, plus four in `x/net` v0.58. A scratch copy on `go 1.26.0`, `toolchain go1.26.9` and `x/net` v0.60.0, with govulncheck built by go1.26.9, reported "No vulnerabilities found". `GOTOOLCHAIN=auto` downloads the toolchain line.
15. **Python locks:** pip-compile from each project compiled (agent 22 packages, client 9); with `--allow-unsafe` both install under `--require-hashes` in a fresh venv, `pip install --no-deps .` succeeds, and the suites pass (client 241, agent 68 with the client on a `.pth`). All pins support Python 3.11.
16. **Dependabot's pip-compile detection** (`PipCompileFileMatcher`, dependabot-core source) requires a `requirements*.in` in the directory; without one a hashed `requirements.txt` is treated as plain pins.
17. **Duende.IdentityModel 8.1.0** (Apache-2.0) builds as a drop-in for every IdentityModel symbol used (Core, Conformance.Driver, Sample, LoadTest, ClientConformance; Core.Tests 84/84, LoadTest.Tests 136/136). It sends a client secret as `Authorization: Basic` by default and throws on a client assertion with that style; the only assertion site (`AdminAutomationCredentials.cs:119-124`) sets `PostBody`.
18. **The Launcher Dockerfile** is referenced only by `dependabot.yml:39`; the project stays live (slnx, README, VS Code).
19. **Operator pins:** exactly five remote `helm_release` blocks in `modules/operators/main.tf` (cnpg 0.29.0, strimzi 1.1.0, `operator` 1.11.5, cluster-autoscaler 9.37.0, aws-load-balancer-controller 1.9.0). A stub `Chart.yaml` lints clean and `yamldecode` returned identical repository/version literals in a scratch Terraform 1.9.8 config. Besides `image-scan.yml`'s `tf_pin`, `kind/setup.sh:124-158` and `setup.ps1:61-89` hard-code three of the versions.
20. **Dependabot's helm ecosystem** reads `dependencies[]` from any YAML in the directory; `Chart.lock` and templates are optional (dependabot-core source; docs: "chart versions … stay up to date").
21. **Image builds:** api and admin-ui build from the repo root with no build args; the tei and ollama model images build from their `model-image` directories with `MODEL_ID`/`REVISION` and `MODEL`/`DIGEST`, and the chart tags them `<slug>-<rev12>-<dockerfile-hash12>` / `<model>-<digest12>-<dockerfile-hash12>` (`tei/templates/statefulset.yaml:47`, `ollama/templates/statefulset.yaml:49`).
22. **Chart image keys:** `api.image.repository`, `worker.image.repository` (independent of api), `adminUi.image.repository` with `*.image.tag` "0.1.0"; `global.modelImageRegistry` (needs a trailing `/`) is consumed only by the two model-image StatefulSets. No profile or ci-override sets an image value, and the chart has no `imagePullSecrets`.
23. **GHCR:** `GITHUB_TOKEN` with `packages: write` publishes and links a package to the workflow's repository; a user-owned package's first visibility is private and cannot be changed by REST; cosign keyless needs `id-token: write`; the certificate identity is `https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@refs/heads/main` (or `@refs/tags/…`) with issuer `https://token.actions.githubusercontent.com` (GitHub and Sigstore docs). Current action releases: docker/login-action v4.6.0, docker/build-push-action v7.4.0, docker/setup-buildx-action v4.4.1, sigstore/cosign-installer v4.1.2, anchore/sbom-action v0.24.3 (commit SHAs recorded in the scratch evidence; re-resolved at plan time).
24. **Branch protection today** (GET): 11 required checks (app 15368), `strict: false`, `enforce_admins: false`, no reviews, no restrictions, every other boolean false. The PUT body requires `required_status_checks`, `enforce_admins`, `required_pull_request_reviews` and `restrictions`; omitted optional booleans are restated explicitly.
25. **Check-run names** (origin/main `d710aadb` and PR run 38190008825): as listed in §6; the deploy-validate Terraform job reports per matrix entry (`terraform-validate (aws|azure|gcp)`) and the image-scan jobs report the long matrix form.
26. **State of the scans on `main`:** red — `govulncheck` (GO-2026-6617, `x/net`), `first-party (iverson-admin-ui, …)` (CVE-2026-4775, alpine `tiff` 4.7.1-r0, fixed 4.7.2-r0), `npm-audit-adminui` and `npm-audit-ts-sdk` (one high each); green — `nuget-audit`, both pip-audits, `first-party (iverson-api, …)`, `third-party`, `owasp-dependency-check-java` (but red on Dependabot PRs, which get no `NVD_API_KEY`). deploy-validate needs no secrets; its jobs take seconds; `security-audit` failed on 9 of 9 recorded runs and on every profile at HEAD.
27. **The red scans are fixable:** `npm audit fix --package-lock-only` in scratch copies moved `source-map-js` 1.2.1 → 1.2.2 and `npm audit --audit-level=high` came back clean for both packages. The `nginx-unprivileged:1.30.5-alpine` tag now resolves to `sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86`, whose apk database (read from the image layers via the registry API) has `tiff 4.7.2-r0`; the pinned `sha256:15c994d1…` has 4.7.1-r0. The old digest is pinned in `Iverson.AdminUI/Dockerfile:25` and `Iverson.Server/docker-compose.yml:426`.
28. **Nothing else depends on bare image names on cloud profiles:** `image-scan`'s third-party filter `(^|/)iverson-` also excludes the GHCR paths; no Terraform, test, kube-score, kubeconform or GitLab step assumes them.

Added after CDR-1 (2026-10-12), from evidence gathered in that round:

29. **The 8443 issuer for LoadTest (read tier; confirmed live by §7's kind check):** LoadTest sends `IVERSON_ACTING_USER_HOST_HEADER` (default `authentik-server:9000`, `LoadTest/Program.cs:43`) on every Authentik request; the 8443 tls-proxy passes Host through and sets https, so Authentik builds an https issuer for that Host (`charts/authentik/templates/configmap-tls-proxy.yaml:7-9`); the API fetches its metadata from `https://<release>-authentik:8443/…` and accepts that issuer besides the 9000 internal one (`charts/api/templates/deployment.yaml:199-205`, API `Program.cs:161-171`). So only Host `<release>-authentik:8443` yields an accepted issuer through 8443.
30. **`deploy-validate` runs unchanged under `push`:** it reads no secret or PR-only context (`command grep -n -E 'secrets\.|github\.(event|head_ref|base_ref)|pull_request' .github/workflows/deploy-validate.yml` → only the `pull_request:` trigger key at `:4`); the four sibling workflows already use `push: branches: [main]` and report on `main`.
31. **GitLab variable scope (vendor documentation; user-accepted):** a project-level CI/CD variable is available to every job of a pipeline, including MR pipelines, whatever a job's rules; a Protected variable is available only on protected branches and tags (docs.gitlab.com/ci/variables). `.gitlab-ci.yml:176-177` only re-exports `$NVD_API_KEY`.
32. **DI injects the identity:** MS DI passes the `ActingUserIdentity` singleton into `EntityCoordinator<T>`'s optional `identity` parameter; a DI-resolved coordinator binds on `https://` without the opt-in and on `http://localhost` with it, and refuses `http://localhost` without it (CDR-1 probe P-N1).
33. **The Java TLS host/port test is feasible:** the host/port constructor uses JVM default trust (`IversonClient.java:70`); a run with `-Djavax.net.ssl.trustStore` and a SAN-`localhost` certificate succeeded (CDR-1 probe P-J1 row 01).
34. **LoadTest's loopback flag across configurations:** loopback http gives the flag true and the token binds; a non-loopback `https://` gRPC URL is not refused by the SDK (non-http scheme); a non-loopback `http://` gRPC URL is refused at startup before the SDK is built (CDR-1 probes P-N1 DI, rows S1-X6 and S2-0.10).
35. **Go in CI:** without the `toolchain go1.26.9` line the module runs go1.26.0 and govulncheck reports 27 stdlib vulnerabilities; `setup-go` "1.26" resolves to 1.26.9; the pinned CodeQL (CLI 2.27.0) supports Go up to 1.27; the GitLab `golang:1.26` digest is Go 1.26.9 with `GOTOOLCHAIN=local` (CDR-1 rows S3-0.28, S3-0.9, S3-0.16).
36. **The new admin console base digest:** against `15c994d1…`, `56b8272e…` changes only `tiff` (4.7.1-r0 → 4.7.2-r0) and `tzdata`; `main`'s image scan of the old digest reported only the tiff HIGH (run 38189863563), so the new digest has no other HIGH/CRITICAL fixable finding today (CDR-1 row S3-0.29).
37. **The locks install on CI's Python:** both resolve for cp311 under `--require-hashes` (agent 22 packages, client 10 including `setuptools`) (CDR-1 row S3-0.17).
38. **Model-image tags are profile-independent:** renders of all five profiles give the same `iverson-tei-model` and `iverson-ollama-model` tags and build args, and each rendered Dockerfile hash equals `sha256sum` of the directory the workflow builds from (CDR-1 row S4-P3).
