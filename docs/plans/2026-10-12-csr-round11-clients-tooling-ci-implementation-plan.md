# CSR Round 11 — Clients, Tooling and CI (Sub-project D) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-12-csr-round11-clients-tooling-ci-design.md` (commit SHA: `1b108e37`)

**Goal:** close the client-SDK, developer-tooling and CI/supply-chain findings #15, #16, #17, #19, #22 and #23 of CSR round 11.

**Architecture:**
- **SDK guards.** The Java SDK checks the channel's security level when it sends credentials, so TLS clients need no "insecure" flag. The .NET and Python SDKs refuse acting tokens when they cannot know the transport.
- **Tooling.** LoadTest and the conformance harness refuse credential-bearing endpoints that are neither loopback nor https; the agent CLI neutralises terminal control characters and escapes the last model echoes; the mint script takes its password from the environment.
- **Dependencies and CI.** Duende.IdentityModel, hashed Python lockfiles, Go 1.26, the three red scans fixed, pinned CI tooling, operator chart pins Dependabot can update, and the Launcher image removed.
- **Supply chain.** First-party images are published to GHCR, signed keyless with cosign and given a signed SPDX SBOM attestation; branch protection requires the security scans, applied by a script after a throwaway-repo trial.

**Tech stack:** .NET 10 with Grpc.Net.Client 2.83.0 and Duende.IdentityModel 8.1.0; Java 21 with grpc-java 1.84.0 (Maven); Python 3.11 (CI) with pip-tools; Go 1.26.9; Node 22; Helm v3.15.0; Terraform with azurerm/aws/google providers; GitHub Actions (cosign, anchore/sbom-action, docker/build-push-action); GitLab CI.

---

## Global Constraints

Copied from the spec, plus the session rules every task must hold to.

**From the spec:**
- **Fail closed when the transport is unknown** (.NET, Python); the Java guard decides at send time from the channel's security level.
- **`OAuth2ClientCredentials`' one `allowInsecureCredentials` flag** covers both the token endpoint and the channel (user decision 2026-10-12, CDR-1 §3.1 A).
- **Hand-built coordinators refuse an ambient token over an unknown transport** (user decision 2026-10-12, CDR-1 §3.2 A).
- **Admin bypass stays:** branch protection keeps `enforce_admins: false` and no required reviews (user decision 2026-10-12).
- **OWASP dependency-check and kube-score `security-audit` stay optional** (user decisions 2026-10-12).
- **The protection script is never run against a live repository by the plan.** The user runs it, first against a throwaway public repo (CDR-1 §3.4 C).
- **The publish workflow is never run by the plan.** It runs on the user's push; its first run is the acceptance check.
- **Security write-ups** (comments, test names, docs, error messages) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit docs files with `git add -f`.
- **The user's running stack is off-limits.**
  - Never start, stop or modify its `iversonserver` containers, volumes, networks or images (`iverson-api:latest`, `iverson-api:0.1.0`, `localhost/iverson-api:0.1.0`), the stopped `confident_cohen`, `interesting_kalam` and `beautiful_panini`, or another session's `rfdt-*` containers and `tei_models` volume.
  - Never run `docker compose up` against `Iverson.Server/docker-compose.yml`.
  - Live checks use throwaway resources named `csr11d-*`, which they delete afterwards.
- **Memory.** Run one heavy process at a time, because a WSL OOM kills the whole VM. Wrap builds, test runs and probe containers in `systemd-run --user --scope -q -p MemoryMax=…` or `--memory`. Stage kind image loads under `/var/tmp`, never `/tmp`. `/tmp` is a 4.9G tmpfs shared with everything else: delete build outputs you create under `$SCR` when a task ends.
- **`docs/security/tma.md` is not edited.** The user's uncommitted TMA refresh holds it.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr11-clients-tooling-ci` on branch `csr11-clients-tooling-ci`. Use absolute paths, and never `cd` to the main checkout.
- **Commands.**
  - Use `command grep`, because plain `grep` is a gitignore-aware ugrep that skips `docs/`.
  - Every repo-wide grep excludes `.env*`. Nobody reads `Iverson.Server/.env` or the worktree's `.env`.
  - The docker CLI is podman: containers get no bridge IP, so publish on `127.0.0.1` or use a podman network with aliases.
  - Kill processes by PID, never with `pkill -f`.
  - Never use `git stash`.
  - dotnet builds and tests pass `-p:UseSharedCompilation=false`, then run `dotnet build-server shutdown`.
  - `gh api` is used read-only (GET) except where a step says otherwise; no step in this plan writes to GitHub.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content. Render cloud profiles with their `*.ci-override.yaml`, as CI does. The release is `iverson` in namespace `iverson`.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative and end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. A subagent puts exactly this trailer, never its own model name.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd`, never in the repo.
- **Shell state.** Each bash block runs in a fresh shell, and no variable or working directory carries over. Every block starts with `source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh`, which Task 0 writes. It defines `$SCR`, `$WT`, `$CH`, `$LIVE` and `$TRAILER`.

## File Structure

**Create:**
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java`: the send-time guard that wraps caller credentials (Task 1).
- `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs`: the endpoint checks shared by LoadTest and the conformance harness (Task 3), with tests `Iverson.Server/Iverson.LoadTest.Tests/Auth/CredentialEndpointsTests.cs` and `Iverson.Server/Iverson.ClientConformance.Tests/HarnessEndpointCheckTests.cs`.
- `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py`: the mint script's env-var tests (Task 5).
- `Iverson.Clients/Python/requirements.{in,txt}`, `Iverson.Agents/Python/requirements.{in,txt}`: pip-compile inputs and hashed locks (Task 7).
- `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml`: the operator chart pins Dependabot updates (Task 11).
- `.github/workflows/publish-images.yml`: GHCR publish, cosign signing and SBOM attestation (Task 12).
- `Iverson.Server/deploy/scripts/apply-branch-protection.sh`: the branch-protection PUT (Task 13).
- `docs/runbooks/csr11-clients-tooling-ci.md`: the operator runbook (Task 14).

**Modify:**
- Java client: `IversonClient.java`, `OAuth2ClientCredentials.java` and the transport-security test (Task 1).
- .NET client: `ActingUserIdentity.cs`, `EntityCoordinator.cs`, `SchemaCatalogClient.cs`, `ServiceCollectionExtensions.cs` and their tests (Task 2); the two IdentityModel package references and four `using` lines (Task 6).
- Python client: `iverson_client/core.py` and `tests/test_entity_coordinator.py` (Task 2).
- LoadTest and conformance harness: `Program.cs` files and `TokenBroker.cs` (Task 3; Task 6 changes a `using` in `TokenBroker.cs` and `AdminAutomationCredentials.cs`).
- Agent: `__main__.py`, `retrieval.py`, `session.py` and their tests (Task 4).
- Scripts: `mint_acting_user_token.py`, `pattern_leg.py` and its test (Task 5).
- Go client: `go.mod`, `go.sum` (Task 8).
- Lockfiles and images: `Iverson.AdminUI/package-lock.json`, `Iverson.Clients/TypeScript/package-lock.json`, `Iverson.AdminUI/Dockerfile`, `Iverson.Server/docker-compose.yml` (Task 9).
- CI: `.github/workflows/dependency-scan.yml` (Tasks 7, 8), `.gitlab-ci.yml` (Tasks 7, 8, 10), `codeql.yml` and `deploy-validate.yml` (Task 10), `image-scan.yml` (Tasks 10, 11), `.github/dependabot.yml` (Tasks 10, 11).
- Terraform and kind: `modules/operators/main.tf`, `deploy/kind/setup.sh`, `setup.ps1` (Task 11).
- Chart: `values-aws.yaml`, `values-gcp.yaml`, `values-azure.yaml` (Task 12).
- Docs: `README.md` (Task 8), `docs/runbooks/kind-cluster-troubleshooting.md` (Task 3), `docs/user-management-and-security.md` (Task 5).

**Delete:**
- `Iverson.Server/Iverson.Launcher/Dockerfile` (Task 10).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and two design-review rounds, and are NOT re-verified here. They are trusted as ground truth; the spec holds each item's evidence. They are quoted in short form.

- **VA 1.** grpc-java 1.84.0 exposes the transport at send time
- **VA 2.** The Java acting token reaches the wire only through `OAuth2ClientCredentials.applyRequestMetadata`
- **VA 3.** grpc-dotnet 2.83.0 drops `CallCredentials` on `http://` without the opt-in and still sends the call
- **VA 4.** `AddIversonClient` already refuses an ambient token on plaintext
- **VA 5.** Construction sites
- **VA 6.** LoadTest's credential-bearing endpoints
- **VA 7.** The conformance harness
- **VA 8.** `Uri.IsLoopback`
- **VA 9.** Agent CLI terminal sinks
- **VA 10.** Agent prompt echoes
- **VA 11.** The mint script
- **VA 12.** CodeQL
- **VA 13.** GitLab images resolve to digests
- **VA 14.** Go
- **VA 15.** Python locks
- **VA 16.** Dependabot's pip-compile detection
- **VA 17.** Duende.IdentityModel 8.1.0
- **VA 18.** The Launcher Dockerfile
- **VA 19.** Operator pins
- **VA 20.** Dependabot's helm ecosystem
- **VA 21.** Image builds
- **VA 22.** Chart image keys
- **VA 23.** GHCR
- **VA 24.** Branch protection today
- **VA 25.** Check-run names
- **VA 26.** State of the scans on `main`
- **VA 27.** The red scans are fixable
- **VA 28.** Nothing else depends on bare image names on cloud profiles
- **VA 29.** The 8443 issuer for LoadTest (read tier; confirmed live by §7's kind check)
- **VA 30.** `deploy-validate` runs unchanged under `push`
- **VA 31.** GitLab variable scope (vendor documentation; user-accepted)
- **VA 32.** DI injects the identity
- **VA 33.** The Java TLS host/port test is feasible
- **VA 34.** LoadTest's loopback flag across configurations
- **VA 35.** Go in CI
- **VA 36.** The new admin console base digest
- **VA 37.** The locks install on CI's Python
- **VA 38.** Model-image tags are profile-independent
- **VA 39.** Coordinator constructors
- **VA 40.** The in-cluster LoadTest run
- **VA 41.** The agent handler
- **VA 42.** CI tooling changes
- **VA 43.** Dependencies
- **VA 44.** deploy-validate and new files
- **VA 45.** Repository settings

## Verified plan-level assumptions

These were introduced by this plan and verified at plan-write time (2026-10-12, at `1b108e37`). Six drafters each wrote a slice of the tasks, applied its edits to a scratch worktree and ran its bash blocks verbatim; every **Expected:** is observed output. Tasks 1–14 were then **replayed together**: every edit applied and every block run, in order, on one fresh tree. The replay found no anchor failure and no mismatch, made 14 commits with the trailer, and produced the two Task 11 baseline fixes (the last row). The kind live check in Task 15 Steps 5–8 and the container-backed suites were not run at plan time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java` does not exist; the Java files Task 1 modifies (`IversonClient.java`, `OAuth2ClientCredentials.java`, `IversonClientTransportSecurityTest.java`) do | `git ls-tree 1b108e37 Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/` lists no `TransportGuardedCredentials.java`; the drafting replay's applier asserted create-must-not-exist and rewrite/replace-must-exist (`step 3: create …`, `step 1: rewrite …`) on a fresh worktree at `1b108e37` |
| 2 | Path | Every .NET and Python file Task 2 modifies exists at the cited path: `Iverson.Client.Core/{ActingUserIdentity,EntityCoordinator,SchemaCatalogClient,ServiceCollectionExtensions}.cs`, `Iverson.Client.Core.Tests/{EntityCoordinatorIdentityResolutionTests,TestCoordinatorFactory,SchemaCatalogClientTests,ServiceCollectionExtensionsTests}.cs`, `Iverson.Clients/Python/iverson_client/core.py`, `Iverson.Clients/Python/tests/test_entity_coordinator.py` | Fresh-worktree replay: every replace asserted its anchor count (1, or 4 for the two "all 4 occurrences" edits) and the commit step listed exactly these ten paths as `M ` |
| 3 | Signature | The Java edit anchors match the source: the 2-arg raw ctor is `@Deprecated` at `IversonClient.java:169-170`, the 3-arg `(ManagedChannel, CallCredentials, boolean)` at `:192` calls `requireInsecureCredentialsOptIn(credentials, null, …)` at `:193`, the deprecated `(ManagedChannel, CallCredentials, String)` at `:212-213`, the 4-arg at `:228-230`; `OAuth2ClientCredentials.applyRequestMetadata` opens `executor.execute(() -> {` at `:64-65` and reads the acting token from `requestInfo.getCallOptions()` at `:69` | Read of both files at `1b108e37`; replay applied all 9 Java edits exactly once |
| 4 | Signature | grpc-java 1.84.0's `CallCredentials` needs only `applyRequestMetadata` overridden (no abstract `thisUsesUnstableApi`), `RequestInfo.getSecurityLevel()` returns `io.grpc.SecurityLevel`, and `MetadataApplier.fail(Status)` surfaces the status's description unchanged to the blocking caller | `OAuth2ClientCredentials` overrides only `applyRequestMetadata` (`:63`); replay: `TransportGuardedCredentials` compiled, and the refusal reached `StatusRuntimeException.getStatus().getDescription()` verbatim (tests `assertRefusedWithoutCall` pass, 17/17) |
| 5 | Code | The Java test's in-process TLS server and JVM-default trust work at test time with no committed key material: keytool from `java.home/bin` makes an EC `SAN=dns:localhost` PKCS12, netty-shaded serves it from a `KeyManagerFactory` via `GrpcSslContexts.configure(SslContextBuilder.forServer(kmf))`, and setting `javax.net.ssl.trustStore{,Password,Type}` in `@BeforeAll` (restored in `@AfterAll`) is honoured by the host/port constructor's default-trust channel | `/home/ben/sdk/java21/bin/keytool` exists (JDK 21.0.5 Temurin); `grpc-netty-shaded` is a compile-scope dep (`client/pom.xml:22`); replay: both `tlsHostPortConstructor_*` tests pass and the server recorded `Bearer service-token \| Bearer per-call-token` / `\| Bearer ambient-token`; `git grep -l 'BEGIN .*PRIVATE KEY'` → no committed keys in the repo, so none are added |
| 6 | Code | JUnit 6.1.3 accepts `@TempDir Path` as a parameter of a static `@BeforeAll`, and `com.sun.net.httpserver.HttpServer` is usable from the client's tests | `OAuth2ClientCredentialsTest.java:3` already imports `com.sun.net.httpserver.HttpServer`; replay: the class runs 17 tests with the `@BeforeAll` setup |
| 7 | Code | The two `oauth2Credentials_…_fail…` tests fail without the new check (the description, not just the code, distinguishes the guard from a failed token fetch) | Replay Step 2 at `1b108e37`: both fail with `expected: <call credentials are sent only over TLS; …> but was: <null>` (the old path fails UNAUTHENTICATED only because `https://localhost:1` refuses the connection) |
| 8 | Code | The Java tests are falsifiable for the wrap in each credential-attaching ctor | Mutation (drafting): removing `transportGuarded(...)` from the 3-arg raw ctor → `managedChannelConstructor_plaintext_withCredentials_failsEveryCallWithoutOptIn` fails ("Expected io.grpc.StatusRuntimeException to be thrown"), 1 of 17; the 4-arg ctor is covered by the five `IllegalArgument` reds in Step 2 and the `…AndActingUserToken…` send-time test |
| 9 | Command | `mvn -o -B -pl client test -Dtest=…` and `mvn -o -B verify` run offline from `Iverson.Clients/Java` under `systemd-run --user --scope -q -p MemoryMax=3G`, from a fresh worktree with no `target/` | Baseline at `1b108e37`: `Tests run: 220, Failures: 0` BUILD SUCCESS in 41 s; replay on a fresh worktree: `Tests run: 227` and Reactor SUCCESS for client, sample and conformance (Maven 3.9.9, JDK 21.0.5) |
| 10 | Command | `dotnet test Iverson.Client.Core.Tests -p:UseSharedCompilation=false` and `dotnet build Iverson.Client.slnx --no-incremental -p:UseSharedCompilation=false` run from `Iverson.Clients/DotNet` under a 4G scope | Baseline: `Passed: 84, Total: 84`; replay: 89 after Step 3, 91 after Step 7; slnx `0 Error(s)`, `1 Warning(s)` (pre-existing `Iverson.Client.Sample/Program.cs(41,40) CS8619`, a line Task 2 does not touch) |
| 11 | Command | The Python suites run from the main checkout's agent venv (`/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python`, Python 3.14.4, grpcio 1.83.1, pytest 9.1.1), which is gitignored (`.gitignore:88`) and so absent from any worktree; its `iverson_client.pth` points at the **main checkout's** client, and `PYTHONPATH=$WT/Iverson.Clients/Python` takes precedence | `cat .venv/lib/python3.14/site-packages/iverson_client.pth` → `/home/ben/repositories/Iverson/Iverson.Clients/Python`; with PYTHONPATH set, `import iverson_client` from `$WT/Iverson.Agents/Python` prints `$WT/Iverson.Clients/Python/iverson_client/__init__.py`; baseline 241 client / 68 agent passed; after Task 2, 244 / 68 |
| 12 | Code | C# primary-constructor validation via a field initializer compiles without a new warning once `EntityCoordinator<T>`'s `identity` parameter is read only by that initializer (no CS9124 capture-and-initialize) | Replay Step 4 grep for `warning CS|error CS` over the Core.Tests build: no lines; slnx build: only the pre-existing Sample CS8619 |
| 13 | Code | `ActingUserMetadata.MetadataKey` is a public const usable from the DI test, and the existing `RecordingHandler`/`ConfigureAll<HttpClientFactoryOptions>` pattern can record a non-authorization header with an added optional parameter | `ActingUserMetadata.cs:7` `public const string MetadataKey = "x-acting-user-authorization"`; `ServiceCollectionExtensionsTests.cs:236` (pattern), `:252` (handler); replay: the new DI test passes, and the four existing callers of `RecordingHandler(header => …)` still compile and pass |
| 14 | Code | The DI test is falsifiable for the `SchemaCatalogClient` wiring | Mutation (drafting): passing `null` instead of `sp.GetRequiredService<ActingUserIdentity>()` at `ServiceCollectionExtensions.cs:155` → `AddIversonClient_SchemaCatalogClient_SendsTheAmbientActingUserToken` fails (1 of 4 in `~SchemaCatalogClient`) |
| 15 | Ordering | Tasks 1 and 2 introduce no symbol another task consumes and consume none; they touch no file shared with other tasks (the Duende swap edits `CachedClientCredentialsTokenProvider.cs` and the csproj, not Task 2's files) | `git grep -l 'using IdentityModel'` → `Conformance.Driver/Auth.cs`, `Core/CachedClientCredentialsTokenProvider.cs`, `ClientConformance/TokenBroker.cs`, `LoadTest/Auth/AdminAutomationCredentials.cs`; none in Task 1/2's file lists |
| 16 | Consumer | Java: no in-repo caller breaks. The conformance driver passes `true` on its plaintext channel (`Driver.java:186`, `:663`); the sample passes `true` to `plaintext(…)` and to its `OAuth2ClientCredentials` (`Main.java:53-61`); test seams use the package-private stub ctors; no Markdown documents the Java opt-in | `git grep -n 'new IversonClient(\|IversonClient.plaintext('` → only those sites and tests; `git grep` of `Iverson.Clients/**/*.md` and `README.md` for `insecure\|opt-in\|plaintext` → none for Java; replay `mvn verify`: sample and conformance compile, 227 tests pass |
| 17 | Consumer | .NET: the only non-test constructions are the conformance driver's `Coordinator<T>()` (`Program.cs:134-136`) and `wrongCoordinator` (`:917-926`), with no identity and no `WithActingUser`, and `new SchemaCatalogClient(mapping)` (`:332`), which still compiles; LoadTest resolves coordinators from DI (`BenchmarkIngestScenario.cs:27`, `KindWritePathScenario.cs:18-20`), so its `WithActingUser` calls get the computed flag | `git grep -n 'SchemaCatalogClient(\|new ActingUserIdentity\|new EntityCoordinator<' -- '*.cs'` outside Core.Tests → only those sites plus `ServiceCollectionExtensions.cs:146,153`; slnx build passes |
| 18 | Consumer | Python: hand-built coordinators outside the tests pass no token and never call `with_acting_user` (`conformance/driver.py:519`, `:998`; `LoadTest/scripts/pattern_leg.py:662`, `dialogue_pool.py:305`, `dialogue_patterns.py:893`); the agent uses `IversonClient.coordinator()` (`session.py:132`) and its tests mock the coordinator; `test_auth.py:467-490` (client-built coordinators) keeps matching `allow_insecure_credentials` in the new message | `git grep -n 'with_acting_user\|EntityCoordinator('` over `Iverson.Clients/Python`, `Iverson.Agents`, `Iverson.Server/Iverson.LoadTest/scripts`; replay: client 244 passed, agent 68 passed |
| 19 | Path | `Iverson.Server/Iverson.LoadTest/Auth/` exists and holds LoadTest's auth helpers in namespace `Iverson.LoadTest.Auth`; `CredentialEndpoints.cs` is a new file there (Task 3) | `ls Iverson.LoadTest/Auth` → `ActingUserTokenProvider.cs AdminAutomationCredentials.cs AuthentikFlowExecutorClient.cs RecoveryPasswordPolicy.cs`; `RecoveryPasswordPolicy.cs:1` `namespace Iverson.LoadTest.Auth;`; `git grep CredentialEndpoints 1b108e37` → no hits |
| 20 | Path | The test projects are `Iverson.Server/Iverson.LoadTest.Tests` (tests under `Auth/`, namespace `Iverson.LoadTest.Tests.Auth`) and `Iverson.Server/Iverson.ClientConformance.Tests` (flat, namespace `Iverson.ClientConformance.Tests`), both xunit 2.9.3 + FluentAssertions 8.11.0 | `Iverson.LoadTest.Tests/Auth/RecoveryPasswordPolicyTests.cs:5` `namespace Iverson.LoadTest.Tests.Auth;`; `Iverson.ClientConformance.Tests/SchemaProbeTests.cs:6` `namespace Iverson.ClientConformance.Tests;`; both csprojs `:10` xunit 2.9.3, `:15` FluentAssertions 8.11.0 |
| 21 | Signature | The harness can call a public static class in `Iverson.LoadTest`: `Iverson.ClientConformance.csproj:10` references `../Iverson.LoadTest/Iverson.LoadTest.csproj`, and `Iverson.ClientConformance.Tests.csproj:22` references the harness, so the test project sees it transitively | csproj lines cited; Task 3 Step 6 build of `Iverson.ClientConformance.Tests` → `0 Error(s)` and `HarnessEndpointCheckTests` 7/7 |
| 22 | Signature | LoadTest `Program.cs` already has `using Iverson.LoadTest.Auth;` (`:8`) and `TokenBroker.cs` already has it (`:6`); the harness `Program.cs` does not (its usings are `:1-6`), so Task 3 adds it there | `git show 1b108e37:…` grep output: LoadTest `Program.cs:8`, `TokenBroker.cs:6`; harness `Program.cs:1-6` lists Grpc.Core, Grpc.Net.Client, Iverson.Client.Contracts, Iverson.ClientConformance, Iverson.ClientConformance.Scenarios, Microsoft.Extensions.Logging |
| 23 | Code | The replaced derivation (`Program.cs:77-79`, `TokenBroker.cs:96-98`) is the only place the token endpoint is sliced, and the check runs before both: LoadTest's check is at the old `:77` (before `AdminAutomationCredentials.FromEnvironment` `:111` and the provisioning block `:117`); the harness's precedes `DeriveAuthentikBaseUrl` (`Program.cs:20`) | `git grep -n 'IndexOf("/application/o/token/"' 1b108e37` → `LoadTest/Program.cs:78`, `ClientConformance/TokenBroker.cs:97`; Task 3 Step 7: `IVERSON_TOKEN_ENDPOINT=localhost:9000` gives the refusal message, exit 1, instead of a range exception |
| 24 | Code | `CredentialEndpoints.Check` accepts `localhost`/`LOCALHOST`/`127.0.0.1`/`[::1]` (any scheme for the gRPC URL, http for the token endpoint) and `https://`, and refuses `http://iverson-api:8080`, `http://0.0.0.0:8080`, an unparsable URL, and a token endpoint not ending in `/application/o/token/`, and (CIR-1 §3.1 C) an `http://` token endpoint with a non-loopback `https://` gRPC URL; with `grpcMayUseTls: false` it refuses `https://iverson-api:8080` | Task 3 Step 6: `CredentialEndpointsTests` 21/21 and `HarnessEndpointCheckTests` 7/7; red first at Step 2 (`error CS0103: The name 'CredentialEndpoints' does not exist in the current context`) |
| 25 | Command | `dotnet build <proj> -p:UseSharedCompilation=false`, `dotnet test <proj> --no-build [--filter 'FullyQualifiedName~X']` run from `Iverson.Server` under `flock $SCR/heavy.lock` + `systemd-run --user --scope -q -p MemoryMax=6G` | Ran at plan time: `0 Error(s)` for both; `Passed: 156` (LoadTest.Tests), `Passed: 657` (ClientConformance.Tests); filters `~Auth.CredentialEndpointsTests` → 20, `~HarnessEndpointCheckTests` → 7 (exactly the new cases, so the substrings match nothing else) |
| 26 | Command | The built binaries are `Iverson.LoadTest/bin/Debug/net10.0/Iverson.LoadTest.dll` and `Iverson.ClientConformance/bin/Debug/net10.0/Iverson.ClientConformance.dll` after building the test projects, and run under `env -i HOME=… $(command -v dotnet) <dll>` (the inherited PATH contains spaces from WSL, so `env -i PATH=$PATH` breaks) | `ls` after the Step 6 build lists both dlls; Task 3 Step 7 outputs recorded (exit 0 usage line; four `exit=1` refusals) |
| 27 | Consumer | Changing `allowInsecureChannelCallCredentials` from `true` to `IsLoopback(grpcUrl)` keeps every documented run working: all documented LoadTest/harness gRPC URLs and token endpoints are loopback and end in `/application/o/token/` | `git grep -h -o 'IVERSON_TOKEN_ENDPOINT[=:][^ ]*' 1b108e37` → every value `http://localhost:9000/application/o/token/` (plus the agent's own test value `https://idp/token`, which this task does not touch); spec verified assumptions 6, 7, 34 |
| 28 | Consumer | `TokenBroker.DeriveAuthentikBaseUrl` keeps its signature and result; its callers (`Program.cs:20`, `TokenBroker.cs:61`) are unaffected | `git grep -n DeriveAuthentikBaseUrl 1b108e37` → those two callers plus the definition; the delegate is the same expression, moved; `ClientConformance.Tests` 657/657 |
| 29 | Ordering | Task 3's `TokenBroker.cs` edit (the `DeriveAuthentikBaseUrl` block, `:89-98`) is disjoint from the Duende.IdentityModel task's `using IdentityModel.Client;` (`:3`), so either order applies; no other task touches the LoadTest or harness `Program.cs` | `git show 1b108e37:Iverson.Server/Iverson.ClientConformance/TokenBroker.cs` lines 3 and 89-98; brief's shared-files list names no other task on these files |
| 30 | Path | `docs/runbooks/kind-cluster-troubleshooting.md` §5.1 ends with the paragraph beginning `is broken. For a gRPC client under test (e.g. \`Iverson.LoadTest\`)` and containing "names naturally"; it is tracked, and Task 3 is the only task editing it | `sed -n 309,317p` at `1b108e37`; `git ls-files docs/runbooks/kind-cluster-troubleshooting.md` → listed; brief "Shared files" |
| 31 | Code | The chart CA Secrets the runbook names exist with key `ca.crt`: `<release>-api-internal-tls` (`charts/api/templates/secret-internal-tls.yaml:8`, SANs `<release>-api[.<ns>.svc[.cluster.local]]`) and `<release>-authentik-internal-tls` (`charts/authentik/templates/secret-internal-tls.yaml:6`, SANs `<release>-authentik…`) | Read of both templates at `1b108e37` (`genSignedCert $svc … (list $svc …)`, `ca.crt: {{ $ca.Cert | b64enc }}`) |
| 32 | Path | The agent has no checked-in venv in the worktree; its convention is a `--without-pip` venv with deps installed by the system pip and a `.pth` naming `Iverson.Clients/Python` (reasoning-agent plan Task 1 Step 1); system `python3` is 3.14.4 with pip 26.1.2 | `ls -a /home/ben/repositories/Iverson/Iverson.Agents/Python` → `.venv` (gitignored, not in worktrees); `.venv/lib/python3.14/site-packages/*.pth` = `/home/ben/repositories/Iverson/Iverson.Clients/Python`; `python3 -m pip --version` → `pip 26.1.2 … (python 3.14)` |
| 33 | Command | `$SCR/agent-venv/bin/python -m pytest -q` from `Iverson.Agents/Python` runs the agent suite (68 passed at `1b108e37`; pytest's prepend import mode keeps the agent's `tests` package ahead of the client's, per `tests/conftest.py`) | Ran at plan time: baseline `68 passed`; red `6 failed, 67 passed`; green `73 passed` |
| 34 | Code | `_escape` is importable from `iverson_agent.retrieval` (`retrieval.py:168`), as `planner.py:6` and `evaluate.py:12` already do; session.py's import tuple (`:14-16`) gains it | `git show 1b108e37:…/retrieval.py` grep `:168 def _escape`; planner/evaluate import lines |
| 35 | Code | The `%r` warnings print control characters as escapes: with `err.details()` returning `"bad\x1b[2Jfilter"`, both formatted records contain `bad\\x1b[2Jfilter` and no ESC | `test_retrieval_warnings_quote_server_text` red with `%s`, green with `%r` (Task 4 Steps 2 and 6) |
| 36 | Consumer | Moving `main`'s body into `_run` keeps every `test_main.py` call site working (they call `cli.main([...])` and patch module globals `cli.IversonClient`, `cli.AgentSession`, `cli.anthropic`, `cli.judge_grounding`, which `_run` resolves at call time); nothing outside `Iverson.Agents` imports `iverson_agent` | `git grep -n iverson_agent 1b108e37 -- ':!Iverson.Agents' ':!docs'` → no hits; `test_main.py:62,87,112,122` call `cli.main`; Task 4 Step 6 `73 passed` |
| 37 | Consumer | The three escaped echoes change only text the model sees; the existing session tests that assert them (`test_empty_retrieval_tells_the_model_explicitly` `"No documents were found for: q"`, `test_search_more_with_no_new_documents…` `"…for: q2"`, `test_expand_document_out_of_range_and_unknown_tool…` `"Unknown tool frobnicate."`) use bracket-free text and still pass | `test_session.py:156, :243, :256`; Task 4 Step 6 `73 passed` |
| 38 | Code | argparse exits are `SystemExit`, a `BaseException`, so the new `except Exception` in `main` does not swallow `--help` or a usage error | Python data model; `test_entity_resolves_a_module_class_spec…` and the argparse-driven tests pass (73 passed) |
| 39 | Path | `Iverson.Server/deploy/scripts/` holds only `mint_acting_user_token.py` and nothing consumes the directory (no Dockerfile COPY, chart or workflow), so a sibling `test_mint_acting_user_token.py` is inert outside pytest; `__pycache__/` is gitignored (`.gitignore:82`) | `git ls-tree --name-only 1b108e37 Iverson.Server/deploy/scripts/` → the one file; `git grep 'deploy/scripts' 1b108e37 -- ':!docs'` → only the mint script's own name; `git check-ignore -v …/__pycache__/x.pyc` → `.gitignore:82` |
| 40 | Command | `python3 -m pytest <test files> -q` with the system interpreter is the LoadTest scripts' test convention (`test_pattern_leg.py:1-4` docstring) and also runs the new mint-script test; no CI job runs these tests | `test_pattern_leg.py` docstring "Run with: python3 -m pytest …"; `command grep -rn pytest .github/workflows .gitlab-ci.yml` → no hits; ran: baseline 58 passed, red `5 failed, 59 passed`, green `64 passed` |
| 41 | Signature | `mint_acting_user_token.main()` reads `sys.argv` via `parse_args()`, calls `run(args, base_url, host_header, client_id, password, redirect_uri)`, and on kind wraps the flow in `PortForward(namespace, service, local_port)` and reads Secrets through `kubectl_get_secret_value(namespace, secret_name, key)`; `log()` writes to stderr | `mint_acting_user_token.py:81-82` (`log`), `:375` (`kubectl_get_secret_value`), `:392-393` (`PortForward.__init__`), `:440-494` (`parse_args`/`main`), `:503` (`run`) |
| 42 | Signature | `pattern_leg.mint_acting_user_token(username)` (`:665`) reads `BYPASS_PASSWORD_ENV = "IVERSON_ACTING_USER_BYPASS_PASSWORD"` (`:130`) and calls `subprocess.run([...,"--password", password], capture_output=True, text=True)` (`:668-670`); `os` and `subprocess` are imported (`:88`, `:90`); `test_pattern_leg.py` imports `os` (`:11`) | `git show 1b108e37:…/pattern_leg.py` grep results cited |
| 43 | Consumer | `pattern_leg.mint_acting_user_token`'s signature is unchanged, so its other callers keep working: `pattern_leg.py:701`, `dialogue_pool.py:302`, `dialogue_patterns.py:890`; it is the only code that runs the mint script | `git grep -n mint_acting_user_token 1b108e37 -- '*.py'` → those call sites plus docstrings |
| 44 | Consumer | Removing `--password` breaks no other invocation: the only documented one is `docs/user-management-and-security.md:273`, which Task 5 rewrites; no runbook, README or script passes it | `command grep -rn -e '--password' docs/runbooks docs/*.md Iverson.Server/*.md Iverson.Server/Iverson.LoadTest/README.md README.md` → only `docs/user-management-and-security.md:270, :273, :286` |
| 45 | Code | `IVERSON_ACTING_USER_PASSWORD` is LoadTest's existing name for the smoke-test user's password | `Iverson.LoadTest/Program.cs:53-54` `RequireEnv("IVERSON_ACTING_USER_PASSWORD")` |
| 46 | Path | The only legacy `IdentityModel` references are two `PackageReference`s (Client.Core, Conformance.Driver) and four `using IdentityModel.Client;` lines (Client.Core `CachedClientCredentialsTokenProvider.cs`, driver `Auth.cs`, LoadTest `Auth/AdminAutomationCredentials.cs`, ClientConformance `TokenBroker.cs`); no `Directory.Packages.props`, `packages.lock.json` or `NuGet.config` pins it | At `1b108e37`: `command grep -rn -I --exclude-dir={node_modules,bin,obj} IdentityModel .` minus `Microsoft./System.IdentityModel` and docs/ → exactly those 6 lines plus a test reason string (`AdminAutomationCredentialsTests.cs:82`); `find . -iname nuget.config -o -name packages.lock.json -o -name Directory.Packages.props` → none. Task 6 Step 1 replay: each anchor matched once, `6 files changed, 6 insertions(+), 6 deletions(-)` |
| 47 | Consumer | LoadTest, ClientConformance and the Sample get the package only transitively through `ProjectReference` to Client.Core, so they need no csproj edit and build after the swap | `Iverson.LoadTest.csproj:10`, `Iverson.ClientConformance.csproj:11`, Sample csproj → ProjectReference Client.Core; no `IdentityModel` PackageReference. Replay: driver, Sample, ClientConformance builds `0 Error(s)`; Sample's `1 Warning(s)` is the existing CS8619 at `Program.cs:41` (`Task.FromResult(actingUserToken)`), unrelated; `dotnet list package --include-transitive` shows `> Duende.IdentityModel 8.1.0` for Core and ClientConformance |
| 48 | Command | `dotnet test` on Client.Core.Tests, LoadTest.Tests and ClientConformance.Tests runs with no containers and passes after the swap | Replay (flock + `systemd-run MemoryMax=6G`, `-p:UseSharedCompilation=false`): `Passed! Failed: 0, Passed: 84` / `136` / `650` (ClientConformance.Tests is 650, not the spec's unlisted count) |
| 49 | Code | The local Duende 8.1.0 package resolves (already in the NuGet cache) and the `Duende.IdentityModel.Client` namespace covers every symbol used | `~/.nuget/packages/duende.identitymodel/8.1.0` exists; builds above compile all four files |
| 50 | Command | pip-tools 7.6.2 on pip 26.2.1 (current PyPI releases) installs into a `--without-pip` Python 3.14 venv bootstrapped by `get-pip.py` (system python3.14 has no ensurepip) | `python3 -m ensurepip` → `No module named ensurepip`; PyPI JSON: pip-tools 7.6.2, pip 26.2.1, pip-audit 2.10.1, setuptools 84.0.0 are latest; replay Step 1 → `pip 26.2.1`, `pip-compile, version 7.6.2` |
| 51 | Code | The spec's exact pip-compile command run in each directory writes a header with `--output-file=requirements.txt` and locks client 10 packages (incl. `setuptools==84.0.0`) and agent 22 | Replay Step 3 (twice, identical): header line 5 `pip-compile --allow-unsafe --generate-hashes --output-file=requirements.txt requirements.in`; client 10 / agent 22 (only drift vs spec-time probe: `truststore` 0.10.4 → 0.10.5). pip-compile prints a `--strip-extras` deprecation warning, harmless (no extras requested) |
| 52 | Code | The 3.14-compiled locks are complete and installable for Python 3.11: every pin has a 3.11 wheel and hash, and no dependency whose marker is true on 3.11 is missing | `pip install --dry-run --ignore-installed --python-version 3.11 --only-binary=:all: --require-hashes -r …` succeeds for both; marker check over the `--report` metadata evaluated with python_version 3.11 → `missing on 3.11: none`. Both checks mutation-tested: `--python-version 3.9` fails on `annotated-types==0.8.0`; marker env 3.10 reports `pytest -> exceptiongroup`, `pytest -> tomli`, `anyio -> exceptiongroup` |
| 53 | Command | The CI sequence (hashed install, `pip install --no-deps .`, `.pth` for the agent) passes both suites from the hashed envs | Replay Step 4: `pip check` → `No broken requirements found.` (both); client `241 passed`, agent `68 passed` (Python 3.14 venvs) |
| 54 | Code | pip-audit 2.10.1 installed in its own venv audits another interpreter when `PIPAPI_PYTHON_LOCATION` names it, and fails on a vulnerable package there | Replay: audits of the client/agent venvs list `iverson-client`/`iverson-agent` as "not found on PyPI" (packages present only in the target venv) and exit 0; same command against a scratch venv with pip 24.0 + setuptools 79.0.1 → `Found 14 known vulnerabilities in 2 packages`, exit 1 |
| 55 | Path | The four new Python files are not gitignored; `pip install .`'s `build/` and `*.egg-info/` are | `git check-ignore -v Iverson.Clients/Python/requirements.txt Iverson.Agents/Python/requirements.in` → none; `.gitignore:84 build/`, `:85 *.egg-info/`; replay `git status --short` after installs lists only the four `??` files |
| 56 | Consumer | The pip-audit job edits change no job name (the required-check names `pip-audit-python-sdk`, `pip-audit-agents` in spec §6 stay), and no other workflow, script or Dockerfile installs from these directories | YAML parse after edit: job keys unchanged in both CI files; `command grep -rn "Iverson.Agents/Python\|Iverson.Clients/Python" .github .gitlab-ci.yml` → only these jobs and `dependabot.yml`'s two pip entries |
| 57 | Consumer | GitHub's setuptools line still matters under the env audit: Python 3.11's bundled setuptools is 79.0.1, which has advisories; 84.0.0 has none | `Lib/ensurepip/__init__.py` at cpython `v3.11.17`: `_SETUPTOOLS_VERSION = "79.0.1"`, `_PIP_VERSION = "24.0"`; OSV query: setuptools 79.0.1 → GHSA-h35f-9h28-mq5c, PYSEC-2026-3447; setuptools 84.0.0 → none; pip 24.0 → 12 advisories |
| 58 | Consumer | GitLab's `python:3.11` image (digest `50cfccc7…`, Python 3.11.17) bundles pip 24.0 and setuptools 79.0.1, both with published advisories, so an interpreter audit there fails unless the job first runs `pip install pip==26.2.1 setuptools==84.0.0` (added to both GitLab jobs, user decision 2026-10-12), after which it is clean | Registry config of `library/python:3.11` amd64: `PYTHON_VERSION=3.11.17`, build layer `pip3 install 'setuptools==79.0.1' 'wheel==0.46.3'`, no pip upgrade; cpython `v3.11.17` `Lib/ensurepip/__init__.py`: `_PIP_VERSION = "24.0"`, `_SETUPTOOLS_VERSION = "79.0.1"`; OSV: pip 24.0 → 12 advisories (GHSA-4xh5-x5gv-qwph …), setuptools 79.0.1 → GHSA-h35f-9h28-mq5c, PYSEC-2026-3447, pip 26.2.1 / setuptools 84.0.0 → none. Scratch venv with pip 24.0 + setuptools 79.0.1: pip-audit 2.10.1 via `PIPAPI_PYTHON_LOCATION` → `Found 14 known vulnerabilities in 2 packages` (exit 1); after `pip install pip==26.2.1 setuptools==84.0.0` in it → `No known vulnerabilities found` (exit 0). Fresh-worktree replay of Task 7 Step 5: both GitLab scripts start `['cd …', 'pip install pip==26.2.1 setuptools==84.0.0']` |
| 59 | Command | actionlint v1.7.12 (latest release) runs from its checksum-pinned release tarball and rejects a malformed workflow | GitHub releases API latest `v1.7.12`; checksums file: `8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  actionlint_1.7.12_linux_amd64.tar.gz`; `-color=never` is invalid (bool flag), `-no-color` is used; replay `actionlint clean`; a `runs-onn` mutation → exit 1 with `unexpected key "runs-onn"` |
| 60 | Command | With local go1.22 and `GOTOOLCHAIN=auto`, `go mod edit -go=1.26.0 -toolchain=go1.26.9` + `go get golang.org/x/net@v0.60.0` + `go mod tidy` give exactly x/net v0.60.0, x/sys v0.48.0, x/text v0.42.0, and change only go.mod/go.sum | Replay (twice): `go: upgraded … x/net v0.58.0 => v0.60.0`, `x/sys v0.47.0 => v0.48.0`, `x/text v0.41.0 => v0.42.0`; `2 files changed, 12 insertions(+), 10 deletions(-)`; caches under `$SCR/go` with `GOFLAGS=-modcacherw` |
| 61 | Command | On the bumped module the declared toolchain builds, vets and tests clean and govulncheck v1.7.0 finds nothing | Replay: `go version go1.26.9 linux/amd64`; `ok` for `conformance`, `iverson`, `iverson_test`; `No vulnerabilities found.` (govulncheck latest is v1.9.0, released 2026-10-11; v1.7.0 kept to match GitHub's existing pin and spec VA14) |
| 62 | Path | `golang:1.26` resolves to manifest list `sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89` (same as `1.26.9`), Go 1.26.9 with `GOTOOLCHAIN=local` | Registry HEAD (index Accept) for `1.26` and `1.26.9` → that digest; amd64 config Env `GOLANG_VERSION=1.26.9`, `GOTOOLCHAIN=local` (matches CDR-1 S3-0.16) |
| 63 | Consumer | The only other `go` consumer of the Go module is ClientConformance's `DriverRunner` (`go build -o bin/conformance ./conformance`, `DriverRunner.cs:141-142`), which uses the caller's `go`; with the default `GOTOOLCHAIN=auto` it downloads go1.26.9 as it already downloads 1.25.0 today. `scripts/generate_protos.sh` pins go1.22 but only runs `protoc` plugins, never `go build` | `command grep -rn 'go build\|go run' Iverson.Server/Iverson.ClientConformance` → `DriverRunner.cs:141-142`; `generate_protos.sh` read in full |
| 64 | Path | `README.md:71` is the single-line clients list ending `[TypeScript](Iverson.Clients/TypeScript/).` (anchor unique) | Replay: anchor count 1; no other README/docs line states a Go version (`command grep -rni 'go 1\.2[0-9]\|go1\.2[0-9]'` outside docs history → go.mod, CI, generate_protos.sh only) |
| 65 | Code | `govulncheck` in GitHub already installs after `setup-go` at v1.7.0, so Task 8's GitHub edit is `go-version: "1.26"` plus `check-latest: true`: setup-go exports `GOTOOLCHAIN=local` (so `go.mod`'s `toolchain` line is ignored) and without `check-latest` takes the runner image's cached 1.26 patch | `dependency-scan.yml:58-62` at `1b108e37`; setup-go `action.yml` (`check-latest`, default false), `src/installer.ts:95,127` and `src/main.ts:190` at `b7ad1dad`; the `actions/go-versions` manifest resolves "1.26" to 1.26.9; runner image `ubuntu24/20261004.327` caches 1.26.8 and `ubuntu24/20261012.334` (2026-10-13, rolling out) moves it to 1.26.9; govulncheck v1.7.0 on the bumped module: go1.26.8 → 9 stdlib advisories, exit 3; go1.26.9 → `No vulnerabilities found.` (CIR-1 §2.1) |
| 66 | Code | Replacing the three `source-map-js` lines in each lockfile equals `npm audit fix --package-lock-only` minus npm 10.9.2's `libc` deletions | Scratch copies at `1b108e37`: npm's AdminUI output is byte-identical to the edited file; npm's TS output is `3 33` (drops 10 `"libc"` arrays written by a newer npm) vs the edit's `3 3`; `npm view source-map-js@1.2.2 dist.integrity` = `sha512-KGj/8Y43…NP3Vw==`; each lock has exactly one `node_modules/source-map-js` entry and dependents' ranges are `^1.2.1` |
| 67 | Command | After the bump both packages `npm ci`, audit clean at high, build and test | Replay: both `found 0 vulnerabilities` (`npm ci` and `npm audit --audit-level=high`), `npm ls` → only `source-map-js@1.2.2`; AdminUI `✓ built`, 22 files / 226 tests passed; TS `tsc`, 10 files / 287 tests passed. One earlier AdminUI full run at load ≈10 timed out `router.test.tsx` "renders the landing page at / when authenticated" (5000 ms); passed alone ×2 and in the full suite ×3 |
| 68 | Path | The old nginx digest `15c994d1…` appears outside `docs/` only at `Iverson.AdminUI/Dockerfile:25` and `Iverson.Server/docker-compose.yml:426`; the chart's tls-proxy (`charts/authentik/values.yaml:6`) is tag-only | `command grep -rn --exclude-dir={node_modules,docs,.git} 15c994d1 .` → those two lines; after Task 9 → none |
| 69 | Path | `1.30.5-alpine` currently resolves to the OCI index `sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86`, which also resolves by digest | Registry HEAD (index Accept, anonymous token, no pull) → `content-type: application/vnd.oci.image.index.v1+json`, that digest, for both refs |
| 70 | Consumer | The digest swap keeps compose valid YAML and changes only the `authentik-tls-proxy` image string; image-scan builds `Iverson.AdminUI/Dockerfile` from the repo root unchanged | Replay: `yaml.safe_load` → `services.authentik-tls-proxy.image` is the new ref; `git diff --numstat` `1 1` compose, `3 2` Dockerfile |
| 71 | Ordering | Tasks 6–9 are mutually independent and anchor on text, so T7/T8 apply in either order on the shared `dependency-scan.yml` and `.gitlab-ci.yml` (T7: pip-audit jobs; T8: govulncheck job); T10 must leave `golang:1.26@sha256:d772…` and only pin the two `python:3.11` lines | Replay applied T7 then T8 on a fresh tree: all anchors matched once; T8's anchors (`go-version: "1.25"` block, `image: golang:1.25` block, README line) are disjoint from T7's (pip-audit job bodies) |
| 72 | Path | `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml` does not exist yet and is not gitignored; every file Tasks 10–11 modify or delete exists | `git ls-tree -r --name-only 1b108e37 -- …/modules/operators .github Iverson.Server/Iverson.Launcher` lists `main.tf`, the `pvc-storageclass-policy` chart, the six workflows, `dependabot.yml` and the Launcher `Dockerfile`, `.csproj` and `Program.cs`, but no `versions/`; `git check-ignore -v …/versions/Chart.yaml` → rc 1 (not ignored). The drafting replays asserted create-must-not-exist and replace-exactly-once for every anchor |
| 73 | Path | The Launcher `Dockerfile` has no live reference besides `dependabot.yml:39`; the project stays | `command grep -rn -I --exclude-dir=.git --exclude-dir=docs -e 'Launcher/Dockerfile' -e 'Iverson.Launcher"' .` after Task 10 Step 4 → no match; `ls Iverson.Server/Iverson.Launcher` → `Iverson.Launcher.csproj Program.cs`. The remaining hits are in `docs/` history (round-4 and round-10 specs and plans), which are not edited |
| 74 | Command | actionlint v1.7.12 (checksum `8aca8db9…` from the release's `actionlint_1.7.12_checksums.txt`) runs clean on all six workflows at `1b108e37` and after each task | `actionlint -no-color .github/workflows/*.yml` → rc 0 at `1b108e37`, after Task 10, and on `image-scan.yml` after Task 11. shellcheck is not installed, so `run:` bodies get expression checks only |
| 75 | Command | The registry API resolves every GitLab image tag without a pull; `alpine/helm:3.15.0` is 404 | `digest.py` (anonymous `HEAD /v2/<repo>/manifests/<tag>`, Docker Hub token from auth.docker.io, MCR anonymous) on 2026-10-12: alpine/helm 3.15.0 → 404; 3.15.4 → `105741fa…`; hashicorp/terraform:1.7.5 → `386b7bff…`; mcr dotnet/sdk:10.0 → `e70cdb7f…` (the digest `Launcher/Dockerfile:1` already pins); node:22 → `0e5f9065…`; maven:3.9-eclipse-temurin-21 → `99e61abc…`; python:3.11 → `50cfccc7…`; golang:1.26 → `d7722066…` (equal to Task 8's pin). A `HEAD` by each pinned digest → 200 |
| 76 | Code | Every `image:` in `.gitlab-ci.yml` sits at two-space indent with the exact strings Task 10's anchors expect: 3× `alpine/helm:3.15.0`, 1× `hashicorp/terraform:1.7.5`, 2× `mcr.microsoft.com/dotnet/sdk:10.0`, 3× `node:22`, 1× `maven:3.9-eclipse-temurin-21`, 2× `python:3.11` (Task 7 changes only the pip-audit `script:`), and `golang:1.25` (Task 8 replaces it) | `command grep -n 'image:' .gitlab-ci.yml` at `1b108e37`: lines 34, 51, 78, 116, 138, 147, 156, 165, 174, 184, 193, 202, 212. D3's Task 7 draft edits only the two `script:` blocks and states the jobs "keep `image: python:3.11`"; its Task 8 replaces `image: golang:1.25` with a comment line plus `golang:1.26@sha256:d7722066…`. A replay on a simulated T7/T8 tree (T8's GitLab text verbatim) applied every Task 10 anchor exactly once |
| 77 | Code | A job-level `rules:` replaces the rules a job inherits through `extends:`, so the OWASP job can drop `extends: .rules-dependency-scan` | GitLab merges `extends` hashes deeply but replaces arrays. Task 10 removes `extends:` anyway, so the job's only rules are its own. `gl_rules.py` (Step 5) evaluates the workflow rules plus each job's rules for MR, `main` push and schedule: `owasp-dependency-check-java {'merge_request_event': 'True -> False'}`, `12 of 13 jobs unchanged`, `now runs on: ['schedule']` |
| 78 | Consumer | The OWASP job's triggers before Task 10 were MR and schedule only, never a `main` push | `.gitlab-ci.yml:27-30` `.rules-dependency-scan` = MR + schedule; `owasp-dependency-check-java` (`:172-180`) `extends` it. So after the change it runs only in scheduled pipelines (see notes.md) |
| 79 | Consumer | `deploy-validate.yml` behaves identically under `push` (spec VA30, re-proved on the edited file) | Task 10 Step 6: `grep -n -E 'secrets\.|github\.(event|head_ref|base_ref)|pull_request|if:|needs:'` → only `7:  pull_request: {}`; the only expression is `${{ matrix.cloud }}` (×2); `permissions: {'contents': 'read'}`; jobs `helm-lint, helm-template-kubeconform, security-audit, terraform-validate` |
| 80 | Consumer | `codeql.yml`'s steps after checkout need no persisted git credential | `codeql.yml:38-77`: setup-dotnet, setup-java, `codeql-action/init`, the `mvn -B -f Iverson.Clients/Java/pom.xml … install` build (no SCM plugin, spec VA12), `codeql-action/analyze` (uploads with its own token input). No `git push`/`git fetch` step |
| 81 | Code | `image-scan.yml` has exactly one `azure/setup-helm` (`:48`) and the third-party job is the only Helm user there; `deploy-validate.yml` already pins `version: v3.15.0` in the same `with:` form | `command grep -n 'setup-helm' .github/workflows/*.yml` → `deploy-validate.yml:21,50,94` (each followed by `version: v3.15.0`) and `image-scan.yml:48` (no `with:`) |
| 82 | Code | Dependabot's helm updater rewrites a stub pin only if `- name: <chart>` precedes that entry's `version:` line, and it writes the new version unquoted, so the stub uses name→version→repository order and bare versions | dependabot-core `helm/lib/dependabot/helm/file_updater/chart_updater.rb` `replace_next_entry_version`: pattern `(\s+-\s+name:\s+["']?NAME["']?.*?\n\s+version:\s+)["']?OLD["']?` (Ruby `/mx`), replaced with `match[1] + new_version` (quotes dropped; `yaml_safe_value` quotes only whitespace or indicator characters). The same pattern, run in Python with DOTALL against the stub, matches all five entries (`'… operator\n    version: 1.11.5'` and so on), and `yaml.safe_load` reads every version as `str` |
| 83 | Command | `helm dependency list <dir>` prints `NAME VERSION REPOSITORY STATUS` as whitespace-separated columns with no network and no `charts/` dir, identically on Helm 3.15.0 (CI after Task 10), 3.16.4 (local) and 4.3.0 | `cat -A` of the output on all three binaries (3.15.0 and 4.3.0 fetched with their `.sha256sum`): the same `NAME␣…\tVERSION\tREPOSITORY…\tSTATUS␣` header and rows ending `\tmissing`; rc 0. `helm lint` of the stub → `1 chart(s) linted, 0 chart(s) failed` (one WARNING that the dependencies are not in `charts/`) |
| 84 | Code | `yamldecode(file("${path.module}/versions/Chart.yaml")).dependencies` gives exactly today's five repository and version literals | Task 11 Step 6, in `terraform console` (Terraform 1.9.8) of the initialised module: `decoded == main.tf literals at HEAD: True 5`, against a JSON of the five `helm_release` blocks parsed from `git show HEAD:…/main.tf` (Step 1) |
| 85 | Command | `terraform fmt -check -recursive`, then `init -backend=false` and `validate` for `modules/operators` and the aws/azure/gcp roots (`-lockfile=readonly`), plus `tfsec` over the tree, pass after Task 11 | Task 11 Step 6: `fmt rc=0`, four `Success! The configuration is valid.`, `No problems detected!` (tfsec 1.28.6; it was also clean at `1b108e37`). The roots reach the module through `source = "../modules/operators"` (`aws/main.tf:59`, `azure/main.tf:102`, `gcp/main.tf:63`) |
| 86 | Consumer | The five remote `helm_release` blocks in `main.tf` are the only Terraform readers of the pins; `pvc_storageclass_policy` (local chart) is untouched | `command grep -rn -E '0\.29\.0|strimzi-kafka-operator|1\.11\.5|9\.37\.0|operators/main\.tf'` outside `docs/` → only `main.tf`, `kind/setup.sh`, `kind/setup.ps1`, `image-scan.yml:56,91` and the `pvc-storageclass-policy/values.yaml` comment (which names `main.tf` for the policy chart, unaffected). After Task 11: 10 `local.operator_charts[` references; no `repository`/`version` string literal left in `main.tf` |
| 87 | Consumer | `image-scan.yml`'s third-party image list is unchanged by the switch from `tf_pin` to `chart_pin` | Task 11 Step 7: the `List the chart's third-party images` run script, extracted with PyYAML from `HEAD`'s workflow (against a `git archive HEAD` export) and from the edited workflow (against the worktree), run with `bash --noprofile --norc -eo pipefail`: `before rc=0`, `after rc=0`, 16 images, `image lists identical` (cnpg 1.30.0, postgresql 18.4-system-trixie, strimzi operator 1.1.0, kafka 1.1.0-kafka-4.3.0). Failure path: `render_operator no-such-chart` → `no no-such-chart pin in …/versions/Chart.yaml`, rc 1 |
| 88 | Consumer | The stale `main.tf` comment's `data.http.lb_controller_policy` fetch no longer exists; the controller IAM policy is a committed file | `modules/cluster-aws/main.tf:369-385`: the round-2 fix replaced the fetch with `file("${path.module}/files/aws-load-balancer-controller-iam-policy-v2.9.0.json")`; `modules/cluster-aws/variables.tf:11-14` `kubernetes_version` default `"1.30"`. The stub's comments cite those |
| 89 | Code | `setup.sh`'s reader resolves the stub from the script's own directory, and a missing pin stops the script before it touches the cluster | The repo convention for that path is `build-and-load-image.sh:68` `"$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"`. Task 11 Step 8, which runs the extracted block from a scratch copy with the same relative layout under `bash -euo pipefail`, prints `sh: 0.29.0 1.1.0 1.11.5`. With the `operator` entry renamed it prints `no operator pin in …/Chart.yaml`, rc 1. `bash -n setup.sh` → 0. The block sits before the first `kubectl`/`helm upgrade` (it is inserted above the Calico step, `setup.sh:15` at HEAD) |
| 90 | Code | `setup.ps1`'s reader works under PowerShell 7 and parses; a missing pin throws | Task 11 Step 8 with pwsh 7.6.6 (release tarball, sha256 `ddbc4a2d…`): `setup.ps1 parse errors: 0` (Parser::ParseFile on the whole edited script), `ps1: 0.29.0 1.1.0 1.11.5`, renamed entry → `ps1 throws: no operator pin in …/Chart.yaml`, rc 1. Path idiom matches `build-and-load-image.ps1:36` (`Join-Path $PSScriptRoot "../../.."`). Windows PowerShell 5.1 is not exercised |
| 91 | Ordering | Task 10 and Task 11 edit disjoint regions of `image-scan.yml` and `dependabot.yml`, and Task 11 needs Task 10's Helm pin only for CI parity | Task 10 anchors on the `setup-helm` `uses:` line (`image-scan.yml:48`) and the docker entry's Launcher line (`dependabot.yml:39`); Task 11 anchors on the `tf_pin`/`render_operator` block (`image-scan.yml:54-66`) and the helm entry's `charts/*` line (`dependabot.yml:64`). The full in-order replay (simulated T7/T8 → T10 → T11) applied every anchor exactly once, with Task 11's actionlint rc 0 |
| 92 | Ordering | Task 10 must follow Tasks 7 and 8 in `.gitlab-ci.yml` | Task 10 pins `image: python:3.11` (left by Task 7) and asserts that `govulncheck`'s image is already pinned (Task 8). If run before Task 8, Step 5's `gl_images.py` lists `govulncheck` as unpinned. The control run on the pre-Task-10 file lists exactly the 12 jobs Task 10 pins |
| 93 | Path | `.github/workflows/publish-images.yml`, `Iverson.Server/deploy/scripts/apply-branch-protection.sh` and `docs/runbooks/csr11-clients-tooling-ci.md` do not exist at `1b108e37`, and none is gitignored (`git add -f` on the runbook is harmless) | `ls` of the three paths at `1b108e37`: absent. `git check-ignore -v --no-index docs/runbooks/csr11-clients-tooling-ci.md` → exit 1 (`.gitignore:46-51` ignores `docs/{superpowers,criticalreviews,performance,plans,reviews,specs}` only; `git ls-files docs/runbooks` lists tracked runbooks). Replay: the three Task commits show `create mode` for each. |
| 94 | Path | `Iverson.Server/deploy/scripts/` exists and already holds an executable script, so the new 755 script follows its neighbour | `git ls-files -s Iverson.Server/deploy/scripts/mint_acting_user_token.py` → `100755`. Task 13's commit shows `create mode 100755 …/apply-branch-protection.sh`. |
| 95 | Path | `pattern_leg.py`, named in the runbook, is `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` | `git ls-files \| command grep pattern_leg.py$` → that path (plus `test_pattern_leg.py`). |
| 96 | Code | Each of Task 12's four anchors (`  externalScheme: "https"\n`, `\napi:\n  ingress:\n`, `\nworker:\n  replicas: 2\n`, `\nadminUi:\n  ingress:\n`) occurs exactly once in each of `values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml`, and no profile sets an image key today | `command grep -n` at `1b108e37`: aws `:17`, `:93-94`, `:131-132`, `:180-181`; gcp `:16`, `:91-92`, `:120-121`, `:158-159`; azure `:14`, `:89-90`, `:123-124`, `:163-164`. The applier asserts count == 1 and printed `edited` for all three on two clean replays. |
| 97 | Code | The pinned action commits are the current releases' commits: checkout v7.0.1 `3d3c42e5…`, setup-helm v5.0.1 `9bc31f4e…`, login-action v4.6.0 `dbcb8138…`, setup-buildx-action v4.4.1 `f87e5991…`, build-push-action v7.4.0 `c3c9e263…`, cosign-installer v4.1.2 `6f9f1778…`, sbom-action v0.24.3 `66cbf4bc…` (annotated tag object `fffec3d8…` dereferenced) | `gh api repos/<r>/releases/latest` + `git/ref/tags/<tag>` (+ `git/tags/<sha>` for anchore) on 2026-10-12; Task 12 Step 3 re-resolves each version comment and printed `commit matches` ×7. Same versions as spec Verified assumption 23. |
| 98 | Code | `sigstore/cosign-installer` v4.1.2 installs cosign v3.0.6 by default and checks that bootstrap binary against SHA-256s inside the action (linux-amd64 `c956e5df…`); pinning `cosign-release: v3.0.6` therefore installs a hash-checked binary. The latest cosign is v3.1.3 | `action.yml@6f9f1778`: `cosign-release` default `'v3.0.6'`, `bootstrap_version='v3.0.6'`, `bootstrap_linux_amd64_sha="c956e5df…"`; `gh api repos/sigstore/cosign/releases/latest` → v3.1.3 (2026-08-06); release asset `cosign-linux-amd64` digest `sha256:c956e5df…` (GitHub API `.digest`). |
| 99 | Code | The action inputs/outputs the workflow uses exist at the pinned commits: build-push-action `context file build-args tags labels push` and output `digest`; login-action `registry username password`; sbom-action `image registry-username registry-password format output-file upload-artifact upload-release-assets` (default syft v1.54.0 fixed by the commit) | `gh api repos/<r>/contents/action.yml?ref=<sha>` input/output keys; sbom-action `src/SyftVersion.ts@66cbf4bc`: `VERSION = "v1.54.0"`. |
| 100 | Code | cosign v3.0.6 has the flags the workflow and runbook use: `sign -y/--yes`, `attest --yes --type --predicate` with predicate type `spdxjson`, and `verify`/`verify-attestation` `--certificate-identity-regexp`, `--certificate-oidc-issuer`, `verify-attestation --type` | `cosign <cmd> --help` of the hash-checked v3.0.6 binary: `-y, --yes=false:`, `--type='custom':` ("…\|spdx\|spdxjson\|cyclonedx…"), `--predicate=`, `--certificate-identity-regexp='':`, `--certificate-oidc-issuer='':`. Task 12 Step 3 and Task 14 Step 2 print `ok` for each. |
| 101 | Command | `actionlint` 1.7.12 and `shellcheck` 0.11.0 download, hash-check against their GitHub release digests, and lint `publish-images.yml` clean; actionlint really runs shellcheck on `run:` scripts | Release digests via `gh api …/releases/tags/<t>` `.assets[].digest`: actionlint linux_amd64 `8aca8db9…`, shellcheck `8c3be12b…` (= `sha256sum -c` OK). `actionlint -shellcheck …` → clean. Mutation (unquoted `$REF`) → `SC2086:info … [shellcheck]`. D4's draft uses the same `$SCR/tools/actionlint` 1.7.12. |
| 102 | Command | The workflow's own `images` script, run locally as the runner would, emits 4 entries on a `v1.2.3` tag push and a manual `main` run, 2 on a `main` push, and refuses a manual run on another branch and a `v1.2` tag; its model entries equal `build-and-load-image.sh --model-images`'s tags and build args | Task 12 Step 5 output (two clean replays): `exit=0` ×3, `exit=1` with `::error::` ×2; `model images match the kind builder: True` (kind builder run with `docker`/`kind` stubs printing their args; no build). |
| 103 | Code | A cloud profile renders only with its CI override (the Authentik edge WAF/policy guard), so the workflow renders `values-aws.yaml` + `values-aws.ci-override.yaml`; with Task 12's values that render names the model images under `ghcr.io/counsellorben/`, and all three cloud profiles render identical model refs | `helm template iverson . -n iverson -f values-aws.yaml` at `1b108e37` → `Error: … authentik.ingress.wafAclArn must be set with ingress className "alb"` (gcp `securityPolicy`, azure `wafPolicyId` likewise); Task 12 Step 6 lists the same two GHCR model refs for aws, azure and gcp. |
| 104 | Command | `helm lint` and `kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas` (CI's invocation, `deploy-validate.yml:28-38, :64-77`) pass on all five profiles after Task 12 | Task 12 Step 6: `template=0`, `0 chart(s) failed`, Valid 92/69/98/98/98, `Invalid: 0, Errors: 0` (= spec Verified assumption 44). Local helm v3.16.4, kubeconform v0.8.0. |
| 105 | Consumer | The new repository values reach the right workloads: api and worker Deployments use `ghcr.io/counsellorben/iverson-api`, admin-ui `…/iverson-admin-ui`, the tei/ollama StatefulSets `ghcr.io/counsellorben/iverson-{tei,ollama}-model`; `values-local`/`values-laptop` keep bare names, so kind and compose (and `build-and-load-image.sh`, whose default `--values` is `values-laptop.yaml`) are unaffected | Task 12 Step 6 per-workload image map; `build-and-load-image.sh:41` `VALUES_REL=…/values-laptop.yaml`. |
| 106 | Consumer | `image-scan.yml`'s third-party filter `grep -v -E '(^\|/)iverson-'` still drops every first-party image after the GHCR rename | Task 12 Step 6: the filter over the three cloud renders lets through `0` lines containing `iverson-` or `counsellorben` (`ghcr.io/goauthentik/server:2026.5.3` stays, as before). |
| 107 | Consumer | `--set api.image.tag=…,worker.image.tag=…,adminUi.image.tag=…` (the runbook's deploy line) sets the three tags without touching the model images | Task 14 Step 2 render: `iverson-admin-ui:1.2.3` ×1, `iverson-api:1.2.3` ×2 (api + worker), model images keep their chart tags. |
| 108 | Command | Today's live protection (read-only GET) is: 11 checks, all `app_id` 15368, `strict: false`, `enforce_admins: false`, no `required_pull_request_reviews`/`restrictions` keys, and `required_linear_history`, `allow_force_pushes`, `allow_deletions`, `block_creations`, `required_conversation_resolution`, `lock_branch`, `allow_fork_syncing` all false; `required_signatures` false | `gh api repos/counsellorben/Iverson/branches/main/protection` on 2026-10-12; Task 13 Step 3 prints `today's checks kept: True … flags same: True … GET-only keys: ['required_signatures']`. |
| 109 | Code | The PUT body's keys are exactly the endpoint's 11 request properties, the four required ones included; `required_signatures` is not a PUT property (own endpoint) | GitHub's OpenAPI (`github/rest-api-description` main, `paths["/repos/{owner}/{repo}/branches/{branch}/protection"].put`): required `[required_status_checks, enforce_admins, required_pull_request_reviews, restrictions]`; properties = those four + the seven booleans the script sends. See notes.md for `contexts`. |
| 110 | Code | The 22 names are real check-run names: 15 appear on `main` (`d710aadb`) with app 15368; `helm-lint`, `helm-template-kubeconform` and `terraform-validate (aws\|azure\|gcp)` appear in PR run 38190008825 (deploy-validate is PR-only today); the matrix forms match `image-scan.yml`'s `first-party` include and `deploy-validate.yml`'s `cloud: [aws, azure, gcp]` | `gh api repos/counsellorben/Iverson/commits/<sha>/check-runs`; `gh api …/actions/runs/38190008825/jobs`; `deploy-validate.yml:145-146`. |
| 111 | Command | `apply-branch-protection.sh --dry-run` makes no `gh` call and prints a body equal to spec §6; `--repo` changes only the target; malformed `--repo` and unknown flags exit 2; shellcheck is clean; the check is falsifiable | Task 13 Step 3 output (`gh called: False`, `equal to spec §6: True`, exits 2); Step 1 shows the check failing before the script exists; deleting one check → `equal to spec §6: False`. |
| 112 | Command | The `gh` subcommands and flags the runbook names exist in gh 2.95.0: `repo create --public --add-readme` (repo created on the account's default branch), `repo delete <repo> --yes`, `auth refresh -s`, `workflow run --ref`, `api --paginate --jq`; `commits/main/check-runs` accepts a branch name | `gh <cmd> --help` greps (Task 14 Step 2 `gh flags: 1 1 1 1 2`); `gh repo create --help` line 20 (default branch); `gh api 'repos/counsellorben/Iverson/commits/main/check-runs?per_page=100' --paginate` returned the 21 runs. |
| 113 | Code | The runbook's certificate-identity regexp accepts the identities the workflow signs with (`…/publish-images.yml@refs/heads/main`, `@refs/tags/v1.2.3`) and refuses another branch, a PR ref, `main-x` and another workflow | Task 14 Step 2 (Python `re.fullmatch`; the pattern uses only RE2-compatible syntax). |
| 114 | Ordering | Task 12 depends on no other task; Task 13 uses only Task 12's `$SCR/tools/shellcheck`; Task 14 uses only Tasks 12–13's files and names, and its check does not test Task 5's or Task 10's results | Brief "Shared files": `values-{aws,gcp,azure}.yaml` are T12-only; no other task creates the three new files; Task 14 Step 2 reads only Task 12/13 files, the mint script's existing `--target`, and the chart. |
| 115 | Command | CI's .NET gate is `dotnet build Iverson.slnx` then `dotnet test Iverson.slnx --filter "Category!=Integration"`, from the repo root | `.github/workflows/dotnet-build.yml:19-20` (`- run: dotnet build Iverson.slnx`, `- run: dotnet test Iverson.slnx --filter "Category!=Integration"`) |
| 116 | Command | `dotnet test Iverson.slnx` alone does not build `Iverson.Client.Sample`, `Iverson.Client.Conformance.Driver` or `Iverson.Launcher`, so Step 1 runs the build first, and runs it `--no-incremental` so every project's warnings print | Plan-time solution test log: the built projects are only the test projects and their references (no `Iverson.Client.Sample ->` line); the Sample's CS8619 appears only in the `dotnet build Iverson.slnx --no-incremental` log |
| 117 | Command | Step 1's .NET block, run on a tree holding the .NET edits of Tasks 2, 3 and 6 (applied from the drafts), prints `build-exit=0`, `22 Warning(s)`, `0 Error(s)` and the 11 per-assembly lines in Step 1's Expected | Plan-time run of the block (env path swapped). Output: Client.Core.Tests 91, LoadTest.Tests 157, ClientConformance.Tests 657, Api.Tests 1511, Client.Search.Tests 125, Embeddings.Tests 60, Events.Tests 41, Patterns.Tests 289, Sql.Tests 86, StarRocks.Tests 635, Vector.Tests 112, all `failed=0` |
| 118 | Consumer | The branch adds no compiler warning: the final build's 22 warning sites are exactly `1b108e37`'s | Baseline `dotnet build Iverson.slnx --no-incremental` at `1b108e37`: `22 Warning(s)`. The deduplicated `file(line,col): warning CSxxxx` sites from the final-state build equal the baseline set (diff differs only in sort order) |
| 119 | Consumer | Every non-Integration .NET test runs without Docker, so the CI-command run starts no container | Plan-time solution test with `DOCKER_HOST=unix:///nonexistent/docker.sock`: all 11 assemblies pass (one timing test aside, next row) |
| 120 | Code | `StarRocksReadinessGateTests.A_shared_wait_its_only_waiter_abandoned_is_still_observed_when_it_fails` is timing-sensitive under host load and unrelated to the branch | It failed once in the baseline solution run (`Expected HasFaulted(wait) to be True … found False`, load average ≈10). Then the class alone passed 5/5 twice, and the assembly re-run passed 635/635 |
| 121 | Command | `dotnet test <proj> --no-build --filter "Category=Integration"` selects exactly the container-backed tests: Api.Tests 46, Patterns.Tests 202, Sql.Tests 63, StarRocks.Tests 66, Vector.Tests 71 | `--list-tests` with and without the filter: 1557/46, 491/202, 149/63, 701/66, 183/71. In each case all − filtered equals the non-Integration count the solution run passed (1511, 289, 86, 635, 112). Embeddings.Tests and Events.Tests list 0 Integration tests |
| 122 | Path | The five Testcontainers projects are Api.Tests, Patterns.Tests, Sql.Tests, StarRocks.Tests and Vector.Tests | `command grep -rln Testcontainers --include=*.csproj Iverson.Server Iverson.Clients` lists exactly those five `.csproj` files |
| 123 | Command | The Python block, run from the main checkout's agent venv with `PYTHONPATH=$WT/Iverson.Clients/Python`, imports the worktree's client in both directories and the worktree's agent, and passes 244 (client) and 73 (agent) | Plan-time run on `1b108e37` plus Task 2's Python edits (Steps 9 and 11) and Task 4's edits: `…imports $WT/Iverson.Clients/Python/iverson_client/__init__.py …`, `244 passed`, `…$WT/Iverson.Agents/Python/iverson_agent/__init__.py`, `73 passed` |
| 124 | Path | The main checkout's agent venv's only path hook is `iverson_client.pth` → the main checkout's client; `iverson_agent` comes from the working directory | `ls …/.venv/lib/python3*/site-packages` shows `iverson_client.pth` only (content `/home/ben/repositories/Iverson/Iverson.Clients/Python`); Python 3.14.4 |
| 125 | Command | Task 10's scratch scripts compare `main`'s `.gitlab-ci.yml` with the final one as Step 2's GitLab block expects: 13 unpinned on `main`; 7 pinned references and none unpinned on the final file; every pinned digest 200; only the OWASP job's triggers change | Plan-time run on a tree holding the CI edits of Tasks 7 (Step 5), 8 (Step 3) and 10 (Steps 2–4), with `git merge-base main HEAD` = `1b108e37`: output as in Step 2's Expected (`0 distinct pinned references; unpinned jobs: [13 names]`, `7 distinct pinned references; unpinned jobs: none`, seven `pinned … 200 (tag still points here)`, `12 of 13 jobs unchanged`) |
| 126 | Command | Task 10 Step 5's first block passes on the final workflow set, including `publish-images.yml` | Plan-time run after applying Tasks 7, 8, 10, 11 (Steps 2–5) and 12 (Steps 2 and 4): `actionlint rc=0` |
| 127 | Ordering | The Step 2 re-runs find their inputs still in `$SCR` | `$SCR/ci/{digest,gl_images,gl_rules}.py`: T10 Steps 1 and 5 create them, and no task deletes `$SCR/ci`. `$SCR/ops/before.json`: T11 Step 1 writes it, and only `$SCR/ops/head` is removed. `$SCR/protection/check.py` and the stub: T13 Step 1. `$SCR/tools/shellcheck`: T12 Step 1. `get-pip.py`: re-fetched by T7 Step 1. `cosign`: re-fetched by T12 Step 1. (`command grep -n 'rm -rf\|rm -f'` over the five drafts.) |
| 128 | Ordering | Re-running Task 7 Step 3 or Task 10 Step 5's second and third blocks on the final tree would give wrong results; re-running Task 11 Step 1 is redundant | T7 Step 3 runs `pip-compile … --output-file=requirements.txt` (rewrites the lock with the newest releases). T11 Step 1 reads `git show main:…/main.tf`, so its output is already in `$SCR/ops/before.json` and a re-run rewrites the same file. T10 Step 5 block 2 does `git show HEAD:.gitlab-ci.yml > gitlab-before.yml`, which on the final tree is already pinned |
| 129 | Ordering | The branch holds 14 commits, one per task, Tasks 1–14 | The commit steps in the drafts are T1 S7, T2 S13, T3 S9, T4 S7, T5 S7, T6 S3, T7 S7, T8 S4, T9 S4, T10 S7, T11 S9, T12 S7, T13 S4 and T14 S3, each with one `git commit`. Task 0 commits nothing |
| 130 | Command | `git log --format='%(trailers:key=Co-Authored-By,valueonly,separator=; )' … \| sort \| uniq -c` prints one line per distinct trailer set | Run on `main`'s last 5 commits: `      5 Claude Opus 5.5 (1M context) <noreply@anthropic.com>` |
| 131 | Command | LoadTest publishes framework-dependent for linux-x64 at 41M, needs only `Microsoft.NETCore.App` 10.0.0, and carries `Duende.IdentityModel.dll` on the final .NET state | Plan-time `dotnet publish Iverson.Server/Iverson.LoadTest -c Release -r linux-x64 --self-contained false -o $LIVE/lt` on a tree with Tasks 2, 3 and 6 applied: `41M`, runtimeconfig `{'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}`, `Duende.IdentityModel.dll` |
| 132 | Path | The branch's `iverson-api` image can host the LoadTest run: its base is `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04, `USER ubuntu`), which has `sh`, `tar`, `sleep` and the .NET runtime, and `dotnet` is on PATH | `Iverson.Server/Iverson.Api/Dockerfile` (`FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2227…`, `USER ubuntu`, `ENTRYPOINT ["dotnet", …]`). Read-only image mount (`podman unshare podman image mount`) of the local `aspnet:10.0`: `/usr/bin/tar`, `/usr/bin/sleep`, `/usr/bin/sh -> dash`, `ubuntu:x:1000:…`, `Ubuntu 24.04.5 LTS`, `DOTNET_VERSION=10.0.12`. No container run |
| 133 | Command | `build-and-load-image.sh csr11d iverson` loads the app image as `docker.io/library/iverson-api:csr11d`, the pod's image reference | `deploy/kind/build-and-load-image.sh` header: "Both modes re-tag with the fully qualified docker.io/library/... reference before loading", positional `[tag] [cluster-name]`. Sub-project C's Task 9 removes `docker.io/library/iverson-api:csr11c localhost/iverson-api:csr11c` after the same call |
| 134 | Path | The live check's selectors and ports match the laptop render: pods `app: iverson-api` (container `api`) and `app: iverson-authentik-server` (containers `server`, `tls-proxy`); Service `iverson-api` 8080→8080 and `iverson-authentik` 8443 (targetPort unset = 8443); kube-dns egress shape copied from `iverson-api-egress` | `helm template iverson . -n iverson -f values-laptop.yaml` at `1b108e37` (Task 12 changes only the cloud values). The kubeconform `-strict` run on `$LIVE/csr11d.yaml` gives `Valid: 4, Invalid: 0`. A selector cross-check maps `csr11d-api-ingress` → `iverson-api` and `csr11d-authentik-ingress` → `iverson-authentik-server` |
| 135 | Code | The debug pod has no path to the API or Authentik without the allowances: `iverson-default-deny` selects every pod for Ingress and Egress, `iverson-api-ingress` admits 8080 only from ingress-nginx and 172.18.0.0/16, and `iverson-authentik-ingress` admits 8443 only from `app: iverson-api` | Laptop render (23 NetworkPolicies) |
| 136 | Code | The namespace enforces PSA `baseline`, which the pod spec (non-root image user, no privileged settings) satisfies | `deploy/kind/setup.sh:56` `kubectl label namespace iverson pod-security.kubernetes.io/enforce=baseline` |
| 137 | Path | The kind Secrets the run reads are `iverson-authentik-admin-automation-client` (`client-id`), `iverson-authentik-loadtest-human-client` (`client-id`), `iverson-authentik-smoke-test-user` and `iverson-authentik-bypass-user` (`password`), `iverson-api-internal-tls` and `iverson-authentik-internal-tls` (`ca.crt`) | `charts/authentik/templates/secret-service-clients.yaml:146,156,166` and the `client-id` keys; `csr11-identity-plane.md` §4 (admin-automation client-id); the laptop render lists `ca.crt`, `tls.crt` and `tls.key` for both internal-tls Secrets |
| 138 | Path | The 8080 and 8443 certificates carry the in-cluster names the run uses | Laptop render, `openssl x509 -ext subjectAltName`: `DNS:iverson-api, DNS:iverson-api.iverson.svc, …` and `DNS:iverson-authentik, DNS:iverson-authentik.iverson.svc, …` |
| 139 | Code | On laptop, the smoke-test and bypass users' tenants (`tenant_smoke_test`, `tenant_bypass`) are seeded `active`, so `ActingUserInterceptor` logs their accepted Posts | `values-laptop.yaml:39` `provisionTestIdentities: true` → `charts/api/templates/deployment.yaml:106-107` `Tenancy__SeedLegacyTenants`; API `Program.cs:754-758` seeds the five legacy tenants `active`; the users' `tenant_id` attributes are at `secret-service-clients.yaml:468,483` |
| 140 | Code | `ActingUserInterceptor` logs `service {sub} acting as user {sub} called {Method}` at Information, only after the ActingUser scheme succeeds and the tenant is active; a failed token throws `Unauthenticated` first | `Iverson.Api/Grpc/ActingUserInterceptor.cs:36-58`; `appsettings.json` `LogLevel.Default: Information`; the console logger is the default simple formatter (no `AddJsonConsole` in Program.cs or the chart) |
| 141 | Code | Each `write-path` Post carries an acting-user token minted through the configured base URL with the configured Host, and the RPC is `/iverson.ObjectPersistenceService/Post` | `WritePathRunner.cs:77-127` (`identities.PickRandom()`, `.WithActingUser(() => identity.GetTokenAsync(ct)).PersistAsync`); `Program.cs:186-198` (identities built with `actingUserBaseUrl`, `actingUserHostHeader`); `object_persistence.proto:3,8-9` (`package iverson`, `rpc Post`); `EntityCoordinator.cs:171` `persistence.PostAsync` |
| 142 | Code | With `--type Tag`, write-path needs no Postgres before the post wave, and `IVERSON_POSTGRES_CS`/`IVERSON_KAFKA_BOOTSTRAP` on port 1 are touched only after it (the e2e probe when a key was posted, else the Kafka lag probe) | `WritePathRunner.cs:42-56` (authors load only for Article), `:164-207` (post-wave lines, then the e2e probe over `postedKeys`, `:178`), `:213` (Kafka lag) |
| 143 | Code | LoadTest's tenant provisioning prints, in order, `Ensuring LoadTest tenant is provisioned...`, `Set the tenant admin's password from CreateTenant's recovery link.`, `Logged the tenant admin in.`, `Tenant '<id>' ready.`, then `Schemas registered.` | `Iverson.LoadTest/Program.cs:117,143,160,162,221` at `1b108e37`. Task 3 changes only the base-URL derivation and the SDK flag |
| 144 | Code | With the default Host (`authentik-server:9000`), the first request (the admin-automation token request to the 8443 endpoint) fails TLS and LoadTest exits 1 with `Tenant provisioning failed: Failed to acquire admin-automation token: The SSL connection could not be established, see inner exception.`. With Host `iverson-authentik:8443`, TLS passes against a two-CA `SSL_CERT_FILE` | Plan-time run of the final-state LoadTest build (Tasks 2, 3 and 6 applied) against a loopback Python TLS server with a certificate for `iverson-authentik`/`iverson-authentik.iverson.svc`, signed by a CA placed second in the bundle. Default Host: exit 1, that message, the server logged `SNI: authentik-server`. Host `iverson-authentik:8443`: the server logged `SNI: iverson-authentik` and `request Host: iverson-authentik:8443 /application/o/token/`, and LoadTest reported the server's 401 (`… token: Unauthorized`). The same certificate with only the other CA in the bundle failed TLS |
| 145 | Code | LoadTest's startup check (Task 3) admits the in-cluster values `IVERSON_GRPC_URL=https://iverson-api:8080` and an https token endpoint | Same plan-time run: LoadTest got past startup to `Ensuring LoadTest tenant is provisioned...` with `IVERSON_GRPC_URL=https://iverson-api:8080` |
| 146 | Code | The env-file generator writes 17 `export NAME='…'` lines that dash sources correctly, including values with `$` and `'`, and refuses an empty value | Plan-time run with a fake `sec` returning `fake-…$x'q`: `env file exit=0 lines=17`, mode `600`, `dash -c '. file; echo …'` printed the values intact. With one value empty: `empty: ['IVERSON_ACTING_USER_BYPASS_PASSWORD']` and exit 1 |
| 147 | Code | The NetworkPolicy dump helper turns `kubectl get networkpolicy -o json` into a name→spec map that `diff` can compare | Run on a `List` built from the laptop render's 23 policies: `23 policies`, sorted JSON |
| 148 | Command | `--target containers` (the default) needs no Kafka security settings, and `write-path` resolves `WritePathScenario` | `Program.cs:97-104` (the Kafka check only under `--target kind`, message at `:100`), `:231-233`, `CommandFlags` `Target` default `"containers"` (`Program.cs:505`) |
| 149 | Command | The tool versions the kind steps use exist locally: kind v0.24.0 (podman provider), kubectl v1.36.2, helm v3.16.4, kubeconform v0.8.0; no kind cluster exists now | `kind version`, `kubectl version --client`, `helm version --short`, `kubeconform -v`; `KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters` → `No kind clusters found.` |
| 150 | Consumer | Task 15 changes no tracked file. Its only outputs under `$WT` are gitignored build directories (`bin/`, `obj/`, `node_modules/`, `dist/`, `build/`, `*.egg-info/`), so `git status --porcelain` stays empty | Plan-time: after the solution build, test run and publish in the scratch worktree, the only `git status` entries were the drafted edits I applied; there were no build outputs. Tasks 7 and 9 record the same for pip and npm |
| 151 | Ordering | Applying Tasks 1–14 in numeric order on one tree from `1b108e37` gives no anchor failure and no output mismatch, and every check re-run on the final tree keeps its outcome, including the shared files `.gitlab-ci.yml`, `dependency-scan.yml`, `dependabot.yml` and `image-scan.yml` | Combined replay (scratch `csr11-d-plan/replay/replay-report.md`): 14 tasks applied, every block matched apart from timings and the suite counts each task notes; 14 commits, each with the exact trailer; the final-tree re-runs (GitLab parse and pins, actionlint, Terraform, the image-scan reader, helm lint/kubeconform, the protection dry-run, the Python suites at 244/73) all kept their outcome |
| 152 | Command | Task 11's baselines compare against `main`, not `HEAD`, so its decoded-pin and image-list checks stay meaningful when re-run after its commit | Replay: with `HEAD`, a post-commit re-run of Step 1 printed `0 {}` and Step 6 `False 5`, and Step 7 compared the new workflow with itself; with `main` the final tree printed `True 5`, `16` and `image lists identical` |
| 153 | Path | Task 0's branch name and worktree path are free and the path is gitignored, and its first command prints nothing on a clean main checkout | `git branch --list csr11-clients-tooling-ci` → empty; the path is absent; `.gitignore:79` ignores `.worktrees/`; the status filter printed nothing (CIR-1 S1-T0.2, S1-T0.3) |
| 154 | Code | Task 1's tests, which set JVM-wide trust-store properties, pass in any order within the surefire JVM | `mvn -o -B -pl client test` with `-Dsurefire.runOrder=reversealphabetical` and `=random` → 227/0 each (CIR-1 S1-T1.10) |
| 155 | Consumer | After Tasks 2 and 3, every LoadTest `WithActingUser` caller gets a non-refusing identity, and Python's new `ValueError` cannot fire through `IversonClient.coordinator()` | The `WithActingUser` callers (`DirectSeeder.cs:121,180,258`, `WritePathRunner.cs:103,113,129`, `BenchmarkIngestScenario.cs:194`) use coordinators that DI injects (into DirectSeeder, BenchmarkIngestScenario, and WritePathScenario/KindWritePathScenario for WritePathRunner), so they get the DI identity under `IsLoopback(grpcUrl)`; `core.py:916-918` refuses an ambient token on plaintext before `:971` builds the coordinator; Python suite 244/0 (CIR-1 S1-X2.2–X2.4, S1-T2.14) |
| 156 | Code | Both programs reach the startup check: the harness with no arguments, LoadTest with `--help` before any `RequireEnv` | Harness `Program.cs:361-366` sets `ShowHelp` only for `--help`/`-h`, and `:10` is its only branch; LoadTest `Program.cs:33-34` `needsTenantAndSchema` is `seed`/`write-path`/`read-path`/`all`/`benchmark-ingest`/`benchmark-query`; Task 3 Step 7 printed the refusals (CIR-1 span check) |
| 157 | Command | The pip and audit environments behave as the tasks assume: the agent venv passes with pip 26.1.2; the GitHub runner's pip 26.2.1 and the GitLab image's `wheel 0.46.3` audit clean; `--require-hashes` installs over an unhashed `setuptools==84.0.0`; the agent's `pip install --no-deps .` works without a `[build-system]` table | Task 4 Steps 2 and 6 outputs; run 38189863559 log (`pip … (26.2.1)`); OSV `pip 26.2.1` and `wheel 0.46.3` → none; a CPython 3.11.17 run shaped like the GitLab image installed both locks and `iverson-agent==0.1.0` (CIR-1 S3-7.11) |
| 158 | Command | CI's Helm v3.15.0 and Terraform 1.7.5 give the same results as the plan-time versions for Task 11 | Helm 3.15.0 ran the whole third-party image-list script: rc 0, list identical to 3.16.4's; Terraform 1.7.5 `fmt -check -recursive` rc 0 and the decode check `True 5` (CIR-1 S4-X.6, S4-X.15) |
| 159 | Consumer | `setup.sh` reads the stub when Task 15 Step 5 runs it by a relative path; no other consumer uses the Launcher Dockerfile; Task 12's inline copy of the third-party filter matches image-scan after Tasks 10–11 | `setup.sh`'s reader run from `$WT`, `/` and its own directory reads all three pins; a case-insensitive `launcher` sweep outside `docs/` finds no other user; the filter line is unchanged in Tasks 10+11's diff (CIR-1 S4-11.12, S4-10.8, S4-X.16) |
| 160 | Consumer | Task 15's live check: LoadTest registers unowned schemas, so the smoke-test and bypass users' Posts resolve them; kind selects podman without `KIND_EXPERIMENTAL_PROVIDER` in `build-and-load-image.sh`; the `kind` podman network predates Step 4's snapshot | Post resolves schemas through `TryGetVisible` since `02abddb3` and LoadTest registers without an acting-user header (CIR-1 S5-0.59); the same `build-and-load-image.sh` lines ran live in sub-projects B and C2 (identity-plane plan :2762-2764, orchestrator plan :1578-1580); `docker network ls` lists `kind` today |

## Tasks

Order: Task 0 first. Tasks 1–6 are independent of each other. Tasks 7, 8 and 10 all edit `.gitlab-ci.yml`, and Tasks 7 and 8 both edit `.github/workflows/dependency-scan.yml`, so they run in numeric order and anchor their edits on text, not line numbers. Tasks 10 and 11 both edit `.github/dependabot.yml` and `.github/workflows/image-scan.yml`. Task 14 documents what Tasks 10–13 produce. Task 15 verifies the result.

### Task 0: Branch

**Files:** none in the repo.

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson && git status --porcelain -- . ':!docs/security/tma.md' ':!22' ':!fsc' ':!sego' | head
git -C /home/ben/repositories/Iverson worktree add -b csr11-clients-tooling-ci /home/ben/repositories/Iverson/.worktrees/csr11-clients-tooling-ci main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh <<'X'
export SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd
export WT=/home/ben/repositories/Iverson/.worktrees/csr11-clients-tooling-ci
export CH=$WT/Iverson.Server/deploy/helm/iverson
export LIVE=$SCR/live
export TRAILER='Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>'
X
git -C /home/ben/repositories/Iverson/.worktrees/csr11-clients-tooling-ci log -1 --format=%H
```

Expected:
- The first command prints nothing, so the main checkout has no other uncommitted work.
- The `git worktree add` succeeds.
- The last line prints `main`'s HEAD, which is the commit that holds this plan.

### Task 1: Java SDK send-time credential guard (#16)

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java` (the four raw-channel constructors and the guard's javadoc)
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/IversonClientTransportSecurityTest.java` (rewritten)

The send-time check reads `RequestInfo.getSecurityLevel()`: `PRIVACY_AND_INTEGRITY` on a TLS channel, `NONE` on plaintext (spec verified assumption 1). `IversonClient` attaches credentials only in its two credential-attaching constructors, and every other constructor and factory funnels into them (spec verified assumption 2), so wrapping there covers every call path. The `plaintext(…)` factories keep their construction-time refusal. `OAuth2ClientCredentials` calls the same static check with its own flag. In-repo callers are unaffected: the conformance driver passes `true` on its plaintext channel (`Driver.java:186`, `:663`), and the sample passes `true` both to `plaintext(…)` and to its `OAuth2ClientCredentials` (`Main.java:53-61`).

- [ ] **Step 1: Write the failing tests.** Replace the whole of `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/IversonClientTransportSecurityTest.java` with:

```java
package io.iverson.client.core;

import com.sun.net.httpserver.HttpServer;
import io.grpc.CallCredentials;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import io.grpc.Metadata;
import io.grpc.Server;
import io.grpc.ServerCall;
import io.grpc.ServerCallHandler;
import io.grpc.ServerInterceptor;
import io.grpc.ServerInterceptors;
import io.grpc.Status;
import io.grpc.StatusRuntimeException;
import io.grpc.netty.shaded.io.grpc.netty.GrpcSslContexts;
import io.grpc.netty.shaded.io.grpc.netty.NettyServerBuilder;
import io.grpc.netty.shaded.io.netty.handler.ssl.SslContextBuilder;
import io.grpc.stub.StreamObserver;
import iverson.ObjectMapping.GetSchemaRequest;
import iverson.ObjectMapping.GetSchemaResponse;
import iverson.ObjectMappingServiceGrpc;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import javax.net.ssl.KeyManagerFactory;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * The Java SDK's transport guard for call credentials. CSR finding #10 added a construction-time
 * opt-in ({@code allowInsecureCredentials=true}) for plaintext channels; CSR round-11 #16 moves the
 * check for caller-built channels to send time, where the transport's security level is known: on
 * a TLS channel credentials need no opt-in, and on a plaintext channel without the opt-in every call
 * fails {@code UNAUTHENTICATED} before any credential runs. The {@code plaintext(…)} factories,
 * whose transport is known, still refuse at construction.
 *
 * <p>The send-time tests make real calls to two netty servers on localhost, one plaintext and one
 * TLS. The TLS certificate is generated per run with the JDK's keytool and installed as the JVM's
 * default trust store, which the host/port constructors use; the previous trust-store properties
 * are restored afterwards.
 */
class IversonClientTransportSecurityTest {

    private static final Metadata.Key<String> AUTHORIZATION =
        Metadata.Key.of("authorization", Metadata.ASCII_STRING_MARSHALLER);
    private static final Metadata.Key<String> ACTING_USER =
        Metadata.Key.of(OAuth2ClientCredentials.ACTING_USER_METADATA_KEY, Metadata.ASCII_STRING_MARSHALLER);
    private static final String REFUSAL =
        "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false";
    private static final String TRUST_STORE = "javax.net.ssl.trustStore";
    private static final String TRUST_STORE_PASSWORD = "javax.net.ssl.trustStorePassword";
    private static final String TRUST_STORE_TYPE = "javax.net.ssl.trustStoreType";

    private static final class NoopCallCredentials extends CallCredentials {
        @Override
        public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
            applier.apply(new Metadata());
        }
    }

    /**
     * Sends a fixed service token, plus the acting-user token from the call options as
     * {@link OAuth2ClientCredentials} does, and counts how often it runs.
     */
    private static final class BearerCredentials extends CallCredentials {
        final AtomicInteger applied = new AtomicInteger();

        @Override
        public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
            applied.incrementAndGet();
            Metadata headers = new Metadata();
            headers.put(AUTHORIZATION, "Bearer service-token");
            String acting = requestInfo.getCallOptions().getOption(OAuth2ClientCredentials.ACTING_USER_TOKEN);
            if (acting != null) {
                headers.put(ACTING_USER, "Bearer " + acting);
            }
            applier.apply(headers);
        }
    }

    /** Answers GetSchema with an empty catalog. */
    private static final class MappingService extends ObjectMappingServiceGrpc.ObjectMappingServiceImplBase {
        @Override
        public void getSchema(GetSchemaRequest request, StreamObserver<GetSchemaResponse> responseObserver) {
            responseObserver.onNext(GetSchemaResponse.getDefaultInstance());
            responseObserver.onCompleted();
        }
    }

    /** Every call either server receives, as "authorization | x-acting-user-authorization". */
    private static final List<String> received = new CopyOnWriteArrayList<>();

    private static final ServerInterceptor RECORDER = new ServerInterceptor() {
        @Override
        public <ReqT, RespT> ServerCall.Listener<ReqT> interceptCall(
                ServerCall<ReqT, RespT> call, Metadata headers, ServerCallHandler<ReqT, RespT> next) {
            received.add(headers.get(AUTHORIZATION) + " | " + headers.get(ACTING_USER));
            return next.startCall(call, headers);
        }
    };

    private static Server plaintextServer;
    private static Server tlsServer;
    private static String savedTrustStore;
    private static String savedTrustStorePassword;
    private static String savedTrustStoreType;

    private ManagedChannel channel;

    @BeforeAll
    static void startServers(@TempDir Path dir) throws Exception {
        char[] password = "transport-test".toCharArray();
        Path serverStore = dir.resolve("server.p12");
        Process keytool = new ProcessBuilder(
                Path.of(System.getProperty("java.home"), "bin", "keytool").toString(),
                "-genkeypair", "-alias", "localhost", "-keyalg", "EC", "-groupname", "secp256r1",
                "-dname", "CN=localhost", "-ext", "SAN=dns:localhost", "-validity", "1",
                "-storetype", "PKCS12", "-keystore", serverStore.toString(),
                "-storepass", new String(password))
            .redirectErrorStream(true).start();
        String keytoolOutput = new String(keytool.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
        assertEquals(0, keytool.waitFor(), keytoolOutput);

        KeyStore keys = KeyStore.getInstance("PKCS12");
        try (InputStream in = Files.newInputStream(serverStore)) {
            keys.load(in, password);
        }
        KeyStore trust = KeyStore.getInstance("PKCS12");
        trust.load(null, null);
        trust.setCertificateEntry("localhost", keys.getCertificate("localhost"));
        Path trustStore = dir.resolve("trust.p12");
        try (OutputStream out = Files.newOutputStream(trustStore)) {
            trust.store(out, password);
        }
        savedTrustStore = System.getProperty(TRUST_STORE);
        savedTrustStorePassword = System.getProperty(TRUST_STORE_PASSWORD);
        savedTrustStoreType = System.getProperty(TRUST_STORE_TYPE);
        System.setProperty(TRUST_STORE, trustStore.toString());
        System.setProperty(TRUST_STORE_PASSWORD, new String(password));
        System.setProperty(TRUST_STORE_TYPE, "PKCS12");

        KeyManagerFactory keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        keyManagers.init(keys, password);
        var service = ServerInterceptors.intercept(new MappingService(), RECORDER);
        plaintextServer = NettyServerBuilder.forAddress(new InetSocketAddress("localhost", 0))
            .addService(service).build().start();
        tlsServer = NettyServerBuilder.forAddress(new InetSocketAddress("localhost", 0))
            .sslContext(GrpcSslContexts.configure(SslContextBuilder.forServer(keyManagers)).build())
            .addService(service).build().start();
    }

    @AfterAll
    static void stopServers() {
        plaintextServer.shutdownNow();
        tlsServer.shutdownNow();
        restore(TRUST_STORE, savedTrustStore);
        restore(TRUST_STORE_PASSWORD, savedTrustStorePassword);
        restore(TRUST_STORE_TYPE, savedTrustStoreType);
    }

    private static void restore(String key, String value) {
        if (value == null) {
            System.clearProperty(key);
        } else {
            System.setProperty(key, value);
        }
    }

    @BeforeEach
    void clearRecord() {
        received.clear();
    }

    @AfterEach
    void tearDown() {
        if (channel != null) {
            channel.shutdownNow();
        }
    }

    private ManagedChannel plaintextChannel() {
        channel = ManagedChannelBuilder.forAddress("localhost", plaintextServer.getPort()).usePlaintext().build();
        return channel;
    }

    /** The call fails UNAUTHENTICATED with the guard's own description, and no server sees it. */
    private static void assertRefusedWithoutCall(Runnable call) {
        StatusRuntimeException e = assertThrows(StatusRuntimeException.class, call::run);
        assertEquals(Status.Code.UNAUTHENTICATED, e.getStatus().getCode());
        assertEquals(REFUSAL, e.getStatus().getDescription());
        assertEquals(List.of(), received, "the server must see no call");
    }

    // ── plaintext(…) factories: the transport is known, so they refuse at construction ──

    @Test
    void plaintextFactory_withCredentials_throwsWithoutOptIn() {
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, new NoopCallCredentials(), false));
    }

    @Test
    void plaintextFactory_withCredentials_succeedsWithOptIn() throws Exception {
        try (IversonClient client =
                 IversonClient.plaintext("localhost", 5000, new NoopCallCredentials(), true)) {
            assertDoesNotThrow(() -> {});
        }
    }

    @Test
    void plaintextFactory_deprecatedOverload_withCredentials_alwaysThrows() {
        // The pre-remediation 3-arg overload has no way to opt in, so it must always refuse
        // non-null credentials rather than silently defaulting to the old insecure behavior.
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, new NoopCallCredentials()));
    }

    @Test
    void plaintextFactory_withNullCredentials_neverThrows() {
        assertDoesNotThrow(() -> IversonClient.plaintext("localhost", 5000, null, false).close());
    }

    // ── CSR round-3 finding #4: acting-user token bypassed the plaintext-credential guard ──
    // The acting-user token travels via a CallOptions key consumed by
    // OAuth2ClientCredentials#applyRequestMetadata, not via CallCredentials identity itself, so
    // the original credentials != null check alone missed it: a caller supplying only an
    // acting-user token (null service credentials) over a plaintext channel passed silently.

    @Test
    void plaintextFactory_withActingUserTokenOnly_throwsWithoutOptIn() {
        assertThrows(IllegalArgumentException.class, () ->
            IversonClient.plaintext("localhost", 5000, null, "acting-user-token", false));
    }

    @Test
    void plaintextFactory_withActingUserTokenOnly_succeedsWithOptIn() throws Exception {
        try (IversonClient client =
                 IversonClient.plaintext("localhost", 5000, null, "acting-user-token", true)) {
            assertDoesNotThrow(() -> {});
        }
    }

    // ── CSR round-11 #16: a TLS channel needs no opt-in ──

    @Test
    void tlsHostPortConstructor_withCredentials_needsNoOptIn_andCallSucceeds() throws Exception {
        try (IversonClient client = new IversonClient("localhost", tlsServer.getPort(), new BearerCredentials())) {
            assertEquals(List.of(), client.getSchema("trace", "per-call-token"));
        }
        assertEquals(List.of("Bearer service-token | Bearer per-call-token"), received);
    }

    @Test
    void tlsHostPortConstructor_withCredentialsAndActingUserToken_needsNoOptIn_andCallSucceeds() throws Exception {
        try (IversonClient client = new IversonClient(
                "localhost", tlsServer.getPort(), new BearerCredentials(), "ambient-token")) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("Bearer service-token | Bearer ambient-token"), received);
    }

    // ── CSR round-11 #16: a caller-built plaintext channel refuses at send time ──

    @Test
    void managedChannelConstructor_plaintext_withCredentials_failsEveryCallWithoutOptIn() throws Exception {
        BearerCredentials credentials = new BearerCredentials();
        try (IversonClient client = new IversonClient(plaintextChannel(), credentials, false)) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", "per-call-token"));
        }
        assertEquals(0, credentials.applied.get(), "the credentials must never run");
    }

    @Test
    void managedChannelConstructor_plaintext_withCredentialsAndActingUserToken_failsEveryCallWithoutOptIn()
            throws Exception {
        BearerCredentials credentials = new BearerCredentials();
        try (IversonClient client = new IversonClient(plaintextChannel(), credentials, "ambient-token")) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", null));
        }
        assertEquals(0, credentials.applied.get(), "the credentials must never run");
    }

    @Test
    void managedChannelConstructor_plaintext_withCredentials_sendsThemWithOptIn() throws Exception {
        try (IversonClient client =
                 new IversonClient(plaintextChannel(), new BearerCredentials(), "ambient-token", true)) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("Bearer service-token | Bearer ambient-token"), received);
    }

    @Test
    void managedChannelConstructor_withCredentials_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, new NoopCallCredentials(), true).close());
    }

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_constructs_andNothingSendsTheToken() throws Exception {
        // With no CallCredentials nothing carries the acting-user token, so there is nothing to refuse.
        try (IversonClient client = new IversonClient(plaintextChannel(), null, "ambient-token", false)) {
            assertEquals(List.of(), client.getSchema("trace", null));
        }
        assertEquals(List.of("null | null"), received);
    }

    @Test
    void managedChannelConstructor_withActingUserTokenOnly_succeedsWithOptIn() {
        channel = ManagedChannelBuilder.forAddress("localhost", 5000).usePlaintext().build();
        assertDoesNotThrow(() -> new IversonClient(channel, null, "acting-user-token", true).close());
    }

    // ── OAuth2ClientCredentials applies the same check with its own allowInsecureCredentials ──

    @Test
    void oauth2Credentials_onOwnPlaintextStub_failWithoutTheirOwnOptIn() {
        var credentials = new OAuth2ClientCredentials("client-id", "client-secret", "https://localhost:1/token");
        var stub = ObjectMappingServiceGrpc.newBlockingStub(plaintextChannel()).withCallCredentials(credentials);
        assertRefusedWithoutCall(() -> stub.getSchema(GetSchemaRequest.getDefaultInstance()));
    }

    @Test
    void oauth2Credentials_underOptedInPlaintextFactory_stillFailWithoutTheirOwnOptIn() throws Exception {
        var credentials = new OAuth2ClientCredentials("client-id", "client-secret", "https://localhost:1/token");
        try (IversonClient client =
                 IversonClient.plaintext("localhost", plaintextServer.getPort(), credentials, true)) {
            assertRefusedWithoutCall(() -> client.getSchema("trace", "per-call-token"));
        }
    }

    @Test
    void oauth2Credentials_underOptedInPlaintextFactory_sendWithTheirOwnOptIn() throws Exception {
        HttpServer tokenEndpoint = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        tokenEndpoint.createContext("/token", exchange -> {
            byte[] body = "{\"access_token\":\"oauth2-token\",\"expires_in\":3600}".getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
        });
        tokenEndpoint.start();
        try {
            var credentials = new OAuth2ClientCredentials("client-id", "client-secret",
                "http://127.0.0.1:" + tokenEndpoint.getAddress().getPort() + "/token", null, true);
            try (IversonClient client =
                     IversonClient.plaintext("localhost", plaintextServer.getPort(), credentials, true)) {
                assertEquals(List.of(), client.getSchema("trace", "per-call-token"));
            }
        } finally {
            tokenEndpoint.stop(0);
        }
        assertEquals(List.of("Bearer oauth2-token | Bearer per-call-token"), received);
    }
}
```

The two construction-refusal tests (`managedChannelConstructor_withCredentials_throwsWithoutOptIn`, `managedChannelConstructor_withActingUserTokenOnly_throwsWithoutOptIn`) are replaced by the send-time tests. The two `oauth2Credentials_…_fail…` tests assert the guard's own description, because without the check the call also fails `UNAUTHENTICATED`, but only because the token fetch cannot connect to `https://localhost:1`.

- [ ] **Step 2: Run the tests and watch them fail.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Java
systemd-run --user --scope -q -p MemoryMax=3G mvn -o -B -pl client test -Dtest=IversonClientTransportSecurityTest 2>&1 | command grep -E '^\[ERROR\]   IversonClient|Tests run:.*Fail' | sed -E 's/(» [A-Za-z]+).*/\1/; s/(expected: <[^>]*>) but was: <(.{0,40}).*/\1 but was <\2…/' | sort -u
```

Expected (times vary): 7 of the 17 fail. The five send-time tests fail because the constructor still refuses (`IllegalArgument`), and the two `OAuth2ClientCredentials` tests fail on the description (the call fails only because the token fetch cannot connect):

```text
[ERROR]   IversonClientTransportSecurityTest.managedChannelConstructor_plaintext_withCredentialsAndActingUserToken_failsEveryCallWithoutOptIn:294 » IllegalArgument
[ERROR]   IversonClientTransportSecurityTest.managedChannelConstructor_plaintext_withCredentials_failsEveryCallWithoutOptIn:284 » IllegalArgument
[ERROR]   IversonClientTransportSecurityTest.managedChannelConstructor_withActingUserTokenOnly_constructs_andNothingSendsTheToken:318 » IllegalArgument
[ERROR]   IversonClientTransportSecurityTest.oauth2Credentials_onOwnPlaintextStub_failWithoutTheirOwnOptIn:336->assertRefusedWithoutCall:207 expected: <call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false> but was <null>…
[ERROR]   IversonClientTransportSecurityTest.oauth2Credentials_underOptedInPlaintextFactory_stillFailWithoutTheirOwnOptIn:344->assertRefusedWithoutCall:207 expected: <call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false> but was <null>…
[ERROR]   IversonClientTransportSecurityTest.tlsHostPortConstructor_withCredentialsAndActingUserToken_needsNoOptIn_andCallSucceeds:273 » IllegalArgument
[ERROR]   IversonClientTransportSecurityTest.tlsHostPortConstructor_withCredentials_needsNoOptIn_andCallSucceeds:264 » IllegalArgument
[ERROR] Tests run: 17, Failures: 2, Errors: 5, Skipped: 0
[ERROR] Tests run: 17, Failures: 2, Errors: 5, Skipped: 0, Time elapsed: 10.11 s <<< FAILURE! -- in io.iverson.client.core.IversonClientTransportSecurityTest
```

- [ ] **Step 3: The guard.** Create `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java` with exactly:

```java
package io.iverson.client.core;

import io.grpc.CallCredentials;
import io.grpc.SecurityLevel;
import io.grpc.Status;

import java.util.concurrent.Executor;

/**
 * Wraps the {@link CallCredentials} that {@link IversonClient} attaches, so that they are sent
 * only over TLS (CSR round-11 #16). grpc-java attaches call credentials to any channel, and an
 * already-built {@link io.grpc.ManagedChannel} does not say whether it is TLS; the transport's
 * security level is known only when a call is sent. On a channel without privacy and integrity,
 * a call fails {@code UNAUTHENTICATED} before the wrapped credentials run, unless the caller set
 * {@code allowInsecureCredentials} for a known-local, non-TLS endpoint.
 */
final class TransportGuardedCredentials extends CallCredentials {

    private final CallCredentials inner;
    private final boolean allowInsecureCredentials;

    TransportGuardedCredentials(CallCredentials inner, boolean allowInsecureCredentials) {
        this.inner = inner;
        this.allowInsecureCredentials = allowInsecureCredentials;
    }

    @Override
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        if (!refusedWithoutTls(requestInfo, allowInsecureCredentials, applier)) {
            // The original RequestInfo: OAuth2ClientCredentials reads the acting-user token from
            // its call options.
            inner.applyRequestMetadata(requestInfo, executor, applier);
        }
    }

    /**
     * The send-time check, shared with {@link OAuth2ClientCredentials}: when the call's transport
     * is not TLS and {@code allowInsecureCredentials} is false, fails the call and returns true.
     */
    static boolean refusedWithoutTls(
            RequestInfo requestInfo, boolean allowInsecureCredentials, MetadataApplier applier) {
        if (allowInsecureCredentials || requestInfo.getSecurityLevel() == SecurityLevel.PRIVACY_AND_INTEGRITY) {
            return false;
        }
        applier.fail(Status.UNAUTHENTICATED.withDescription(
            "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false"));
        return true;
    }
}
```

- [ ] **Step 4: Wrap the credentials in `IversonClient`.** Five edits.

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, replace:

```java
    /**
     * Equivalent to {@link #IversonClient(ManagedChannel, CallCredentials, boolean)} with
     * {@code allowInsecureCredentials=false} — since that always refuses non-null credentials,
     * this overload only remains usable when {@code credentials} is null.
     *
     * @deprecated call {@link #IversonClient(ManagedChannel, CallCredentials, boolean)} and pass
     *     {@code allowInsecureCredentials=true} explicitly when {@code channel} is a known-local,
     *     non-TLS channel.
     */
    @Deprecated
    public IversonClient(ManagedChannel channel, CallCredentials credentials) {
```

with:

```java
    /**
     * Equivalent to {@link #IversonClient(ManagedChannel, CallCredentials, boolean)} with
     * {@code allowInsecureCredentials=false}: the credentials are sent only when {@code channel}
     * is TLS, and on a channel without TLS every call fails {@code UNAUTHENTICATED}.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials) {
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, replace:

```java
    /**
     * Creates a client using an already-configured channel, attaching the given call
     * credentials to every stub. grpc-java attaches {@link CallCredentials} to any channel with
     * no guard of its own (unlike grpc-dotnet, which refuses call credentials on a plaintext
     * channel unless explicitly told otherwise) — and this constructor is handed an opaque,
     * already-built {@link ManagedChannel}, so it has no reliable way to detect whether that
     * channel is actually TLS-protected. It therefore requires the explicit opt-in below
     * whenever {@code credentials} is non-null, regardless of the channel's real transport: a
     * caller building a genuinely-TLS channel and wiring up interceptors externally (the
     * documented use case for this constructor) must still pass {@code true} here to confirm
     * they intend credentials to be attached, rather than the library silently assuming safety.
     *
     * @param allowInsecureCredentials explicit, named opt-in required to attach non-null
     *     {@code credentials} to {@code channel}. Defaults to false via the deprecated 2-arg
     *     overload.
     * @throws IllegalArgumentException if {@code credentials} is non-null and
     *     {@code allowInsecureCredentials} is false.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, boolean allowInsecureCredentials) {
        requireInsecureCredentialsOptIn(credentials, null, allowInsecureCredentials);
        this.channel         = channel;
```

with:

```java
    /**
     * Creates a client using an already-configured channel, attaching the given call
     * credentials to every stub. grpc-java attaches {@link CallCredentials} to any channel with
     * no guard of its own (unlike grpc-dotnet, which refuses call credentials on a plaintext
     * channel unless explicitly told otherwise), and an already-built {@link ManagedChannel}
     * does not say whether it is TLS. So the check happens when each call is sent, where the
     * transport is known (CSR round-11 #16): on a TLS channel the credentials are sent; on a
     * channel without TLS the call fails {@code UNAUTHENTICATED} before the credentials run,
     * unless {@code allowInsecureCredentials} is true.
     *
     * @param allowInsecureCredentials explicit, named opt-in to send non-null {@code credentials}
     *     over a channel without TLS; pass {@code true} only for a known-local, non-TLS endpoint.
     *     A TLS channel needs no opt-in.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, boolean allowInsecureCredentials) {
        credentials = transportGuarded(credentials, allowInsecureCredentials);
        this.channel         = channel;
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, replace:

```java
    /**
     * Equivalent to {@link #IversonClient(ManagedChannel, CallCredentials, String, boolean)}
     * with {@code allowInsecureCredentials=false} — since that always refuses a non-null
     * {@code credentials} or {@code actingUserToken}, this overload only remains usable when
     * both are null.
     *
     * @deprecated call {@link #IversonClient(ManagedChannel, CallCredentials, String, boolean)}
     *     and pass {@code allowInsecureCredentials=true} explicitly when {@code channel} is a
     *     known-local, non-TLS channel.
     */
    @Deprecated
    public IversonClient(ManagedChannel channel, CallCredentials credentials, String actingUserToken) {
```

with:

```java
    /**
     * Equivalent to {@link #IversonClient(ManagedChannel, CallCredentials, String, boolean)}
     * with {@code allowInsecureCredentials=false}: the credentials, and the acting-user token
     * they carry, are sent only when {@code channel} is TLS, and on a channel without TLS every
     * call fails {@code UNAUTHENTICATED}.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, String actingUserToken) {
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, replace:

```java
     * this ambient one).
     *
     * @param allowInsecureCredentials see
     *     {@link #IversonClient(ManagedChannel, CallCredentials, boolean)}.
     * @throws IllegalArgumentException if {@code credentials} or {@code actingUserToken} is
     *     non-null and {@code allowInsecureCredentials} is false.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, String actingUserToken,
                          boolean allowInsecureCredentials) {
        requireInsecureCredentialsOptIn(credentials, actingUserToken, allowInsecureCredentials);
        this.channel         = channel;
```

with:

```java
     * this ambient one).
     *
     * <p>The acting-user token is sent by the credentials ({@link OAuth2ClientCredentials} reads
     * it from the call options), so it is subject to the same send-time transport check; with
     * null {@code credentials} nothing sends it.
     *
     * @param allowInsecureCredentials see
     *     {@link #IversonClient(ManagedChannel, CallCredentials, boolean)}.
     */
    public IversonClient(ManagedChannel channel, CallCredentials credentials, String actingUserToken,
                          boolean allowInsecureCredentials) {
        credentials = transportGuarded(credentials, allowInsecureCredentials);
        this.channel         = channel;
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`, replace:

```java
    /**
     * The construction-time guard closing CSR finding #10 for the Java SDK: refuses to combine
     * non-null {@link CallCredentials} <em>or</em> a non-null {@code actingUserToken} with a
     * plaintext channel unless the caller has explicitly set
     * {@code allowInsecureCredentials=true}. Mirrors the .NET reference
```

with:

```java
    /**
     * Wraps non-null credentials in the send-time transport check (CSR round-11 #16). Called
     * once, by the two credential-attaching constructors, into which every other constructor and
     * factory funnels.
     */
    private static CallCredentials transportGuarded(CallCredentials credentials, boolean allowInsecureCredentials) {
        return credentials == null ? null : new TransportGuardedCredentials(credentials, allowInsecureCredentials);
    }

    /**
     * The construction-time guard closing CSR finding #10 for the Java SDK, kept for the
     * {@code plaintext(…)} factories, whose transport is known to be plaintext, so they fail fast
     * at construction (caller-built channels are checked at send time instead, by
     * {@link TransportGuardedCredentials}): refuses to combine
     * non-null {@link CallCredentials} <em>or</em> a non-null {@code actingUserToken} with a
     * plaintext channel unless the caller has explicitly set
     * {@code allowInsecureCredentials=true}. Mirrors the .NET reference
```

- [ ] **Step 5: The same check in `OAuth2ClientCredentials`.** Three edits.

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`, replace:

```java
/**
 * Attaches an OAuth2 client-credentials Bearer token to every gRPC call, caching the
 * token in memory and refreshing it 60 seconds before expiry.
 */
```

with:

```java
/**
 * Attaches an OAuth2 client-credentials Bearer token to every gRPC call, caching the
 * token in memory and refreshing it 60 seconds before expiry.
 *
 * <p>The token, and any acting-user token, are sent only over TLS (CSR round-11 #16): on a
 * channel without TLS every call fails {@code UNAUTHENTICATED} before a token is fetched, unless
 * this instance's own {@code allowInsecureCredentials} is true. That one flag also allows a
 * non-https token endpoint. So an instance used on a plaintext channel needs
 * {@code allowInsecureCredentials=true} even when its token endpoint is https and the
 * {@link IversonClient} it is given to was opted in too; the check also covers an instance
 * attached directly to the caller's own stubs.
 */
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`, replace:

```java
    public OAuth2ClientCredentials(
            String clientId, String clientSecret, String tokenEndpoint, String scope,
            boolean allowInsecureCredentials) {
```

with:

```java
    /**
     * @param allowInsecureCredentials explicit, named opt-in, for a known-local, non-TLS
     *     endpoint only, to fetch the token from a non-https {@code tokenEndpoint} and to send the
     *     tokens over a channel without TLS. The shorter constructors pass false.
     * @throws IllegalArgumentException if {@code tokenEndpoint} is not https and
     *     {@code allowInsecureCredentials} is false.
     */
    public OAuth2ClientCredentials(
            String clientId, String clientSecret, String tokenEndpoint, String scope,
            boolean allowInsecureCredentials) {
```

In `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java`, replace:

```java
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        executor.execute(() -> {
```

with:

```java
    public void applyRequestMetadata(RequestInfo requestInfo, Executor executor, MetadataApplier applier) {
        if (TransportGuardedCredentials.refusedWithoutTls(requestInfo, allowInsecureCredentials, applier)) {
            return;
        }
        executor.execute(() -> {
```

- [ ] **Step 6: Run the module's tests, then build the whole Java reactor.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Java
systemd-run --user --scope -q -p MemoryMax=3G mvn -o -B -pl client test -Dtest=IversonClientTransportSecurityTest 2>&1 | command grep -E 'Tests run:|BUILD'
systemd-run --user --scope -q -p MemoryMax=3G mvn -o -B verify 2>&1 | command grep -E 'Tests run: [0-9]+, F[^-]*$|Reactor Summary|SUCCESS \[|FAILURE|BUILD'
```

Expected (times vary): the class passes 17/17; the reactor passes 227 client tests (220 before, minus the 10 old tests in this class, plus its 17), and the sample and the conformance driver, which construct `IversonClient` with credentials, compile:

```text
[INFO] Tests run: 17, Failures: 0, Errors: 0, Skipped: 0, Time elapsed: 13.84 s -- in io.iverson.client.core.IversonClientTransportSecurityTest
[INFO] Tests run: 17, Failures: 0, Errors: 0, Skipped: 0
[INFO] BUILD SUCCESS
[INFO] Tests run: 227, Failures: 0, Errors: 0, Skipped: 0
[INFO] Reactor Summary for Iverson Java Client 1.0.0-SNAPSHOT:
[INFO] Iverson Java Client ................................ SUCCESS [  0.009 s]
[INFO] Iverson Client — Core .............................. SUCCESS [01:25 min]
[INFO] Iverson Sample Application ......................... SUCCESS [  4.254 s]
[INFO] Iverson Conformance Driver ......................... SUCCESS [  9.927 s]
[INFO] BUILD SUCCESS
```

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/IversonClientTransportSecurityTest.java
git status --short Iverson.Clients/Java
git commit -m "check the java sdk's call credentials against the channel's transport at send time

$TRAILER"
```

Expected: the status lists exactly the four paths, staged (`A ` for `TransportGuardedCredentials.java`, `M ` for the other three); the commit reports `4 files changed`.

### Task 2: .NET and Python acting tokens fail closed on an unknown transport (#22)

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs`, `EntityCoordinator.cs`, `SchemaCatalogClient.cs`, `ServiceCollectionExtensions.cs` (all under `Iverson.Clients/DotNet/Iverson.Client.Core/`)
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, `TestCoordinatorFactory.cs`, `SchemaCatalogClientTests.cs`, `ServiceCollectionExtensionsTests.cs` (all under `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/`)
- Test: `Iverson.Clients/Python/tests/test_entity_coordinator.py`

The acting-user token travels as raw metadata in both SDKs, so no channel option guards it, and grpc-dotnet would drop call credentials on `http://` and still send the call (spec verified assumption 3). The fix is therefore the identity's own flag, failing closed when nobody computed it. `AddIversonClient` and Python's `IversonClient.coordinator()` already pass the computed value (spec verified assumptions 4 and 5), and DI injects the `ActingUserIdentity` singleton into the coordinator (verified assumption 32).

Which hand-built constructions the change reaches (spec verified assumptions 5 and 39):
- **.NET.** The conformance driver's `Coordinator<T>()` (`Program.cs:134-136`) and `wrongCoordinator` (`:917-926`) pass no identity and never call `WithActingUser`, and its `new SchemaCatalogClient(mapping)` (`:332`) still compiles. LoadTest resolves its coordinators from DI.
- **Python.** The conformance driver and the LoadTest scripts build coordinators with no token and never call `with_acting_user`. The agent uses `IversonClient.coordinator()` (`session.py:132`).
- **Tests.** Only the test sites below construct these types with a token or bind per call.

.NET keeps its primary constructors. `EntityCoordinator<T>`'s `identity` parameter moves into a validated `_identity` field, so the constructor can throw from a field initializer and the parameter is no longer captured. `WithActingUser` re-runs the constructor with the same identity (`EntityCoordinator.cs:56`, verified assumption 39), and it has already refused unless `RefusesPlaintextTokens` is false, so the re-run never throws.

The tests run from the main checkout's agent venv (Python 3.14, grpcio 1.83.1, pytest 9.1.1), which nothing else in the worktree provides. Its `iverson_client.pth` points at the **main checkout's** client (`/home/ben/repositories/Iverson/Iverson.Clients/Python`). So every Python run below sets `PYTHONPATH=$WT/Iverson.Clients/Python`, which Python puts ahead of `.pth` entries, so that both suites import the worktree's client. `-p no:cacheprovider` keeps `.pytest_cache` out of the worktree.

- [ ] **Step 1: .NET: the failing identity and coordinator tests.** Six edits. Where a test binds a per-call token, or carries an ambient one, its identity now says `refusesPlaintextTokens: false`.

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, replace:

```csharp
    private const string MetadataKey = ActingUserMetadata.MetadataKey;
```

with:

```csharp
    private const string MetadataKey = ActingUserMetadata.MetadataKey;

    // An identity whose transport is known to be TLS or opted in, with no ambient token: what
    // AddIversonClient registers for such an endpoint when no actingUserTokenProvider is given.
    private static readonly ActingUserIdentity KnownTransport = new(refusesPlaintextTokens: false);
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, replace all 4 occurrences of:

```csharp
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
```

with:

```csharp
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"), refusesPlaintextTokens: false);
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, replace:

```csharp
        var (sut, captured) = CreateSut();
        var bound = sut.WithActingUser(() => Task.FromResult("bound-token"));
```

with:

```csharp
        var (sut, captured) = CreateSut(KnownTransport);
        var bound = sut.WithActingUser(() => Task.FromResult("bound-token"));
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, replace:

```csharp
        var (aliceCoordinator, aliceCaptured) = CreateSut();
        var (bobCoordinator, bobCaptured) = CreateSut();
```

with:

```csharp
        var (aliceCoordinator, aliceCaptured) = CreateSut(KnownTransport);
        var (bobCoordinator, bobCaptured) = CreateSut(KnownTransport);
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`, replace:

```csharp
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer bound-token");
    }
}
```

with:

```csharp
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer bound-token");
    }

    // ---- CSR round-11 #22: a hand-built identity or coordinator fails closed ------------------------

    [Fact]
    public void ActingUserIdentity_RefusesPlaintextTokens_UnlessToldOtherwise()
    {
        new ActingUserIdentity().RefusesPlaintextTokens.Should().BeTrue();
        new ActingUserIdentity(() => Task.FromResult("ambient-token")).RefusesPlaintextTokens.Should().BeTrue();
        new ActingUserIdentity(refusesPlaintextTokens: false).RefusesPlaintextTokens.Should().BeFalse();
    }

    [Fact]
    public void WithActingUser_Throws_WithoutAnIdentity()
    {
        var (sut, _) = CreateSut(identity: null);

        var act = () => sut.WithActingUser(() => Task.FromResult("bound-token"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*transport is unknown*");
    }

    [Fact]
    public void WithActingUser_Throws_WithADefaultIdentity()
    {
        var (sut, _) = CreateSut(new ActingUserIdentity());

        var act = () => sut.WithActingUser(() => Task.FromResult("bound-token"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*transport is unknown*");
    }

    [Fact]
    public void Constructor_Throws_ForAnAmbientTokenWhoseIdentityRefusesPlaintextTokens()
    {
        var act = () => CreateSut(new ActingUserIdentity(() => Task.FromResult("ambient-token")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*refusesPlaintextTokens*");
    }

    [Fact]
    public async Task Constructor_AcceptsAnIdentityWithoutAnAmbientToken_AndSendsNoHeader()
    {
        var (sut, captured) = CreateSut(new ActingUserIdentity());

        await sut.PostMappedAsync(NewEntity());

        captured()!.Get(MetadataKey).Should().BeNull();
    }
}
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/TestCoordinatorFactory.cs`, replace:

```csharp
            search ?? Substitute.For<ObjectSearchService.ObjectSearchServiceClient>(),
            NullLogger<EntityCoordinator<T>>.Instance);
```

with:

```csharp
            search ?? Substitute.For<ObjectSearchService.ObjectSearchServiceClient>(),
            NullLogger<EntityCoordinator<T>>.Instance,
            // The pipeline and match-pattern tests bind per-call tokens; their transport is known.
            new ActingUserIdentity(refusesPlaintextTokens: false));
```

- [ ] **Step 2: Run them and watch them fail.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests -p:UseSharedCompilation=false 2>&1 | command grep -E '^\s+Failed Iverson|^(Passed|Failed)!' | sed -E 's/ \[[0-9]+ ms\]//'; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: four of the new tests fail (the order of the `Failed` lines and the duration may differ); the fifth new test and every existing test pass:

```text
  Failed Iverson.Client.Core.Tests.EntityCoordinatorIdentityResolutionTests.ActingUserIdentity_RefusesPlaintextTokens_UnlessToldOtherwise
  Failed Iverson.Client.Core.Tests.EntityCoordinatorIdentityResolutionTests.WithActingUser_Throws_WithADefaultIdentity
  Failed Iverson.Client.Core.Tests.EntityCoordinatorIdentityResolutionTests.WithActingUser_Throws_WithoutAnIdentity
  Failed Iverson.Client.Core.Tests.EntityCoordinatorIdentityResolutionTests.Constructor_Throws_ForAnAmbientTokenWhoseIdentityRefusesPlaintextTokens
Failed!  - Failed:     4, Passed:    85, Skipped:     0, Total:    89, Duration: 946 ms - Iverson.Client.Core.Tests.dll (net10.0)
```

- [ ] **Step 3: .NET: the identity defaults to refusing, and the coordinator refuses.** Four edits.

Replace the whole of `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs` with:

```csharp
namespace Iverson.Client.Core;

/// <summary>
/// The ambient acting-user identity, if one was configured on the client. A container-resolvable
/// type rather than a bare <c>Func&lt;Task&lt;string&gt;&gt;?</c>, because <c>EntityCoordinator&lt;T&gt;</c>
/// is registered open-generic and activated by reflection.
/// </summary>
public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null, bool refusesPlaintextTokens = true)
{
    public Func<Task<string>>? TokenProvider { get; } = tokenProvider;

    /// <summary>
    /// True when the client's endpoint is plaintext and it was not opted in to sending credentials
    /// over it, or when the transport is unknown. <c>AddIversonClient</c> computes it from the
    /// endpoint. An identity built by hand defaults to true (CSR round-11 #22: fail closed), so it
    /// says <c>false</c> only for a TLS endpoint or a known-local, opted-in one. While it is true,
    /// <c>EntityCoordinator&lt;T&gt;.WithActingUser</c> refuses a per-call token, and the
    /// <c>EntityCoordinator&lt;T&gt;</c> and <c>SchemaCatalogClient</c> constructors refuse an identity
    /// that carries a <see cref="TokenProvider"/>, as <c>AddIversonClient</c> refuses an ambient one.
    /// </summary>
    public bool RefusesPlaintextTokens { get; } = refusesPlaintextTokens;

    /// <summary>
    /// Returns <paramref name="identity"/> unchanged, or throws when it carries an ambient token
    /// provider while it refuses plaintext tokens. Called by the <c>EntityCoordinator&lt;T&gt;</c> and
    /// <c>SchemaCatalogClient</c> constructors.
    /// </summary>
    internal static ActingUserIdentity? RequireSendableAmbientToken(ActingUserIdentity? identity) =>
        identity is { TokenProvider: not null, RefusesPlaintextTokens: true }
            ? throw new InvalidOperationException(
                "Refusing an ambient acting-user token provider whose ActingUserIdentity refuses " +
                "plaintext tokens: the endpoint is plaintext without an explicit " +
                "allowInsecureChannelCallCredentials=true opt-in, or the transport is unknown (an " +
                "ActingUserIdentity built by hand defaults to refusesPlaintextTokens: true). The token " +
                "travels as raw Metadata, which no channel option guards. Pass " +
                "refusesPlaintextTokens: false only for a TLS endpoint or a known-local, non-TLS one.")
            : identity;
}
```

In `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`, replace:

```csharp
    private readonly EntityDescriptor _descriptor = registry.Get<T>();
```

with:

```csharp
    private readonly EntityDescriptor _descriptor = registry.Get<T>();

    // CSR round-11 #22: an ambient token on an identity that refuses plaintext tokens is refused
    // here, as SchemaCatalogClient does. In DI this never throws: AddIversonClient refuses the same
    // configuration first. Every other member reads _identity, never the parameter.
    private readonly ActingUserIdentity? _identity = ActingUserIdentity.RequireSendableAmbientToken(identity);
```

In `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`, replace:

```csharp
    /// per-call override; the Metadata bag passed to individual calls is not an identity channel.
    /// </summary>
    public EntityCoordinator<T> WithActingUser(Func<Task<string>> tokenProvider)
    {
        // The per-call token travels as raw Metadata, as the ambient one does, so it needs the same
        // plaintext guard AddIversonClient applies to the ambient one.
        if (identity?.RefusesPlaintextTokens == true)
        {
            throw new InvalidOperationException(
                "Refusing to bind an acting-user token on a plaintext (h2c) endpoint without an " +
                "explicit allowInsecureChannelCallCredentials=true opt-in. The acting-user identity " +
                "travels as raw Metadata, not CallCredentials, so grpc-dotnet's own " +
                "UnsafeUseInsecureChannelCallCredentials guard cannot see it — the acting-user Bearer " +
                "token would otherwise be sent in the clear. Pass allowInsecureChannelCallCredentials: " +
                "true to AddIversonClient only for a known-local, non-TLS endpoint.");
        }

        return new(registry, assembler, mapping, persistence, retrieval, search, logger, identity)
```

with:

```csharp
    /// per-call override; the Metadata bag passed to individual calls is not an identity channel.
    /// Refused unless the coordinator's <see cref="ActingUserIdentity"/> says the transport is TLS or
    /// opted in (<see cref="ActingUserIdentity.RefusesPlaintextTokens"/> false). A coordinator built
    /// by hand with no identity, or with an identity left at its default, cannot bind a per-call
    /// token (CSR round-11 #22); one resolved through <c>AddIversonClient</c> gets the computed value.
    /// </summary>
    public EntityCoordinator<T> WithActingUser(Func<Task<string>> tokenProvider)
    {
        // The per-call token travels as raw Metadata, as the ambient one does, so it needs the same
        // plaintext guard AddIversonClient applies to the ambient one. An unknown transport (no
        // identity, or one that did not say) fails closed.
        if (_identity?.RefusesPlaintextTokens != false)
        {
            throw new InvalidOperationException(
                "Refusing to bind an acting-user token: this coordinator's endpoint is plaintext " +
                "without an explicit allowInsecureChannelCallCredentials=true opt-in, or its transport " +
                "is unknown (no ActingUserIdentity, or one left at refusesPlaintextTokens: true). The " +
                "acting-user identity travels as raw Metadata, not CallCredentials, so grpc-dotnet's own " +
                "UnsafeUseInsecureChannelCallCredentials guard cannot see it — the acting-user Bearer " +
                "token would otherwise be sent in the clear. Resolve the coordinator through " +
                "AddIversonClient, which computes the flag from the endpoint; pass " +
                "allowInsecureChannelCallCredentials: true there only for a known-local, non-TLS endpoint.");
        }

        return new(registry, assembler, mapping, persistence, retrieval, search, logger, _identity)
```

In `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`, replace:

```csharp
        var provider = _boundActingUser ?? identity?.TokenProvider;
```

with:

```csharp
        var provider = _boundActingUser ?? _identity?.TokenProvider;
```

- [ ] **Step 4: Run them and watch them pass.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests -p:UseSharedCompilation=false 2>&1 | command grep -E 'warning CS|error CS|^\s+Failed Iverson|^(Passed|Failed)!' | sed -E 's/ \[[0-9]+ ms\]//' | sort -u; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: no compiler warning or error lines, and (duration varies):

```text
Passed!  - Failed:     0, Passed:    89, Skipped:     0, Total:    89, Duration: 5 s - Iverson.Client.Core.Tests.dll (net10.0)
```

- [ ] **Step 5: .NET: the failing catalog-client tests.** Three edits.

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs`, replace:

```csharp
        var client = new SchemaCatalogClient(mapping, () => Task.FromResult("test-token"));

        await client.GetSchemaAsync();

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }
}
```

with:

```csharp
        var client = new SchemaCatalogClient(
            mapping, new ActingUserIdentity(() => Task.FromResult("test-token"), refusesPlaintextTokens: false));

        await client.GetSchemaAsync();

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }

    // CSR round-11 #22: an identity built by hand refuses plaintext tokens unless told otherwise, so
    // an ambient token on it is refused at construction.
    [Fact]
    public void Constructor_Throws_ForAnAmbientTokenWhoseIdentityRefusesPlaintextTokens()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();

        var act = () => new SchemaCatalogClient(mapping, new ActingUserIdentity(() => Task.FromResult("test-token")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*refusesPlaintextTokens*");
    }
}
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`, replace:

```csharp
    // ---- CSR round-11 #9: the service-token provider ------------------------------------------------
```

with:

```csharp
    // CSR round-11 #22: AddIversonClient hands SchemaCatalogClient its ActingUserIdentity singleton,
    // so the catalog call carries the ambient acting-user token.
    [Fact]
    public async Task AddIversonClient_SchemaCatalogClient_SendsTheAmbientActingUserToken()
    {
        string? seen = null;
        var services = new ServiceCollection();
        services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
            b.PrimaryHandler = new RecordingHandler(header => seen = header, ActingUserMetadata.MetadataKey)));
        using var provider = services.BuildServiceProvider();

        try { await provider.GetRequiredService<SchemaCatalogClient>().GetSchemaAsync(); } catch (RpcException) { }

        seen.Should().Be("Bearer acting-user-token");
    }

    // ---- CSR round-11 #9: the service-token provider ------------------------------------------------
```

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`, replace:

```csharp
    private sealed class RecordingHandler(Action<string?> record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            record(request.Headers.TryGetValues("authorization", out var values) ? values.Single() : null);
```

with:

```csharp
    private sealed class RecordingHandler(Action<string?> record, string header = "authorization") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            record(request.Headers.TryGetValues(header, out var values) ? values.Single() : null);
```

- [ ] **Step 6: Run them and watch them fail.** The changed constructor call does not compile yet.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests -p:UseSharedCompilation=false 2>&1 | command grep -E 'error CS' | sed -E 's#^/.*/(Iverson\.Client\.Core\.Tests/[A-Za-z]+\.cs)#\1#; s/ \[.*$//' | sort -u; dotnet build-server shutdown >/dev/null 2>&1
```

Expected:

```text
Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs(53,22): error CS1503: Argument 2: cannot convert from 'Iverson.Client.Core.ActingUserIdentity' to 'System.Func<System.Threading.Tasks.Task<string>>?'
Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs(68,58): error CS1503: Argument 2: cannot convert from 'Iverson.Client.Core.ActingUserIdentity' to 'System.Func<System.Threading.Tasks.Task<string>>?'
```

- [ ] **Step 7: .NET: `SchemaCatalogClient` takes the identity, and DI passes its singleton.** Two edits.

Replace the whole of `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaCatalogClient.cs` with:

```csharp
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.Client.Core;

/// <summary>
/// Retrieves the tenant's authorized schema catalog for agent consumption. Binds the acting
/// user at construction and applies it internally, so the call surface stays entity- and
/// identity-free.
/// </summary>
/// <param name="mapping">The schema-registration client.</param>
/// <param name="identity">
/// The client's acting-user identity; its <see cref="ActingUserIdentity.TokenProvider"/>, if any, is
/// sent on every call. <c>AddIversonClient</c> passes its singleton. An identity that carries a token
/// provider while <see cref="ActingUserIdentity.RefusesPlaintextTokens"/> is true (the default for an
/// identity built by hand) is refused at construction (CSR round-11 #22).
/// </param>
public sealed class SchemaCatalogClient(
    ObjectMappingService.ObjectMappingServiceClient mapping,
    ActingUserIdentity? identity = null)
{
    private readonly Func<Task<string>>? _actingUserTokenProvider =
        ActingUserIdentity.RequireSendableAmbientToken(identity)?.TokenProvider;

    public async Task<IReadOnlyList<SchemaType>> GetSchemaAsync(
        string traceId = "", CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (_actingUserTokenProvider is not null)
            headers.WithActingUser(await _actingUserTokenProvider());

        var response = await mapping.GetSchemaAsync(
            new GetSchemaRequest { TraceId = traceId }, headers, cancellationToken: ct);

        return response.Types_;
    }
}
```

In `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`, replace:

```csharp
        services.AddSingleton(sp => new SchemaCatalogClient(
            sp.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>(),
            actingUserTokenProvider));
```

with:

```csharp
        services.AddSingleton(sp => new SchemaCatalogClient(
            sp.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>(),
            sp.GetRequiredService<ActingUserIdentity>()));
```

- [ ] **Step 8: Run the .NET suite and build the client solution.** The solution build covers the conformance driver and the sample, which construct `EntityCoordinator<T>` and `SchemaCatalogClient` by hand or through DI.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/DotNet
systemd-run --user --scope -q -p MemoryMax=4G dotnet test Iverson.Client.Core.Tests -p:UseSharedCompilation=false 2>&1 | command grep -E '^\s+Failed Iverson|^(Passed|Failed)!' | sed -E 's/, Duration.*//'; dotnet build-server shutdown >/dev/null 2>&1
systemd-run --user --scope -q -p MemoryMax=4G dotnet build Iverson.Client.slnx --no-incremental -p:UseSharedCompilation=false 2>&1 | command grep -E 'warning CS|error CS|Warning\(s\)|Error\(s\)' | sed -E 's#^/.*/(Iverson\.Client\.[A-Za-z.]+/[A-Za-z]+\.cs)#\1#; s/ \[.*$//' | sort -u; dotnet build-server shutdown >/dev/null 2>&1
```

Expected: 91 tests pass (84 before this task, plus five identity and coordinator tests, one catalog-client test and one DI test). The solution builds with no error; its one warning is the sample's existing nullability warning on a line this task does not touch:

```text
Passed!  - Failed:     0, Passed:    91, Skipped:     0, Total:    91
    0 Error(s)
    1 Warning(s)
Iverson.Client.Sample/Program.cs(41,40): warning CS8619: Nullability of reference types in value of type 'Task<string?>' doesn't match target type 'Task<string>'.
```

- [ ] **Step 9: Python: the failing tests.** Three edits.

In `Iverson.Clients/Python/tests/test_entity_coordinator.py`, replace all 4 occurrences of:

```python
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token")
```

with:

```python
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token", refuse_plaintext_token=False)
```

In `Iverson.Clients/Python/tests/test_entity_coordinator.py`, replace:

```python
        coordinator = EntityCoordinator(CoordArticle, channel, "")
```

with:

```python
        coordinator = EntityCoordinator(CoordArticle, channel, "", refuse_plaintext_token=False)
```

In `Iverson.Clients/Python/tests/test_entity_coordinator.py`, replace:

```python
        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer ambient-token"),)


# ── Related-object hydration on the read path ────────────────────────────────────
```

with:

```python
        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer ambient-token"),)

    # CSR round-11 #22: a coordinator built directly cannot see its channel's transport, so it
    # refuses acting-user tokens unless told otherwise.

    def test_a_hand_built_coordinator_refuses_a_per_call_token_by_default(self):
        import pytest

        coordinator = EntityCoordinator(CoordArticle, grpc.insecure_channel("localhost:1"))

        with pytest.raises(ValueError, match="refuse_plaintext_token"):
            coordinator.with_acting_user("bound-token")

    def test_a_hand_built_coordinator_refuses_an_ambient_token_by_default(self):
        import pytest

        with pytest.raises(ValueError, match="refuse_plaintext_token"):
            EntityCoordinator(CoordArticle, grpc.insecure_channel("localhost:1"), "ambient-token")

    def test_a_hand_built_coordinator_told_the_transport_is_known_binds_a_per_call_token(self):
        coordinator = EntityCoordinator(
            CoordArticle, grpc.insecure_channel("localhost:1"), refuse_plaintext_token=False
        )

        bound = coordinator.with_acting_user("bound-token")

        assert bound._acting_user_metadata() == (("x-acting-user-authorization", "Bearer bound-token"),)


# ── Related-object hydration on the read path ────────────────────────────────────
```

- [ ] **Step 10: Run them and watch them fail.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Python
PYTHONPATH=$WT/Iverson.Clients/Python /home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python -m pytest -q -p no:cacheprovider tests/test_entity_coordinator.py 2>&1 | command grep -E '^FAILED|passed|failed'
```

Expected (times vary): the two default-refusal tests fail, and the third new test passes (refusal is already off when it is passed explicitly):

```text
FAILED tests/test_entity_coordinator.py::TestEntityCoordinatorActingUserIdentity::test_a_hand_built_coordinator_refuses_a_per_call_token_by_default
FAILED tests/test_entity_coordinator.py::TestEntityCoordinatorActingUserIdentity::test_a_hand_built_coordinator_refuses_an_ambient_token_by_default
2 failed, 29 passed in 0.33s
```

- [ ] **Step 11: Python: the hand-built coordinator defaults to refusing.** Two edits.

In `Iverson.Clients/Python/iverson_client/core.py`, replace:

```python
    Args:
        entity_class: the ``@iverson_entity``-decorated class.
        channel: an open ``grpc.Channel``.
    """

    def __init__(
        self,
        entity_class: type,
        channel: grpc.Channel,
        acting_user_token: str | None = None,
        *,
        refuse_plaintext_token: bool = False,
    ) -> None:
        meta = getattr(entity_class, "_iverson_meta", None)
```

with:

```python
    Args:
        entity_class: the ``@iverson_entity``-decorated class.
        channel: an open ``grpc.Channel``.
        acting_user_token: optional ambient acting-user token, sent on every call as
            ``x-acting-user-authorization`` metadata.
        refuse_plaintext_token: whether acting-user tokens must be refused because the channel
            is plaintext without an opt-in, or its transport is unknown. Defaults to ``True``
            (CSR round-11 #22: fail closed), because a coordinator built directly cannot see its
            channel's transport: it then raises ``ValueError`` at construction when given an
            ``acting_user_token``, and from ``with_acting_user``. ``IversonClient.coordinator()``
            passes the value computed from ``use_tls`` and ``allow_insecure_credentials``. Pass
            ``False`` only for a TLS channel or a known-local, non-TLS endpoint.
    """

    def __init__(
        self,
        entity_class: type,
        channel: grpc.Channel,
        acting_user_token: str | None = None,
        *,
        refuse_plaintext_token: bool = True,
    ) -> None:
        # The ambient token travels as per-call metadata, which no channel option guards, so it is
        # refused here, as IversonClient's constructor refuses one on plaintext without the opt-in.
        if acting_user_token is not None and refuse_plaintext_token:
            raise ValueError(
                "Refusing an ambient acting-user token on a coordinator whose channel is plaintext "
                "without an explicit allow_insecure_credentials=True opt-in, or whose transport is "
                "unknown (a coordinator built directly defaults to refuse_plaintext_token=True): the "
                "token would otherwise risk being sent in the clear. Use IversonClient.coordinator(), "
                "or pass refuse_plaintext_token=False only for a TLS channel or a known-local, "
                "non-TLS endpoint."
            )
        meta = getattr(entity_class, "_iverson_meta", None)
```

In `Iverson.Clients/Python/iverson_client/core.py`, replace:

```python
        """Return a coordinator bound to ``token``, leaving this one untouched."""
        # The per-call token travels as per-call metadata, as the ambient one does, so it needs
        # the same plaintext guard IversonClient's constructor applies to the ambient one.
        if self._refuse_plaintext_token:
            raise ValueError(
                "Refusing to bind an acting-user token on a plaintext (use_tls=False) channel "
                "without an explicit allow_insecure_credentials=True opt-in on IversonClient: "
                "the acting-user token would otherwise be sent in the clear. Pass "
                "allow_insecure_credentials=True only for a known-local, non-TLS endpoint."
            )
```

with:

```python
        """Return a coordinator bound to ``token``, leaving this one untouched.

        Raises ``ValueError`` while ``refuse_plaintext_token`` is true, which it is by default for
        a coordinator built directly.
        """
        # The per-call token travels as per-call metadata, as the ambient one does, so it needs
        # the same plaintext guard IversonClient's constructor applies to the ambient one.
        if self._refuse_plaintext_token:
            raise ValueError(
                "Refusing to bind an acting-user token on a coordinator whose channel is plaintext "
                "(use_tls=False) without an explicit allow_insecure_credentials=True opt-in on "
                "IversonClient, or whose transport is unknown (a coordinator built directly "
                "defaults to refuse_plaintext_token=True): the acting-user token would otherwise "
                "risk being sent in the clear. Pass allow_insecure_credentials=True only for a "
                "known-local, non-TLS endpoint."
            )
```

- [ ] **Step 12: Run the client suite and the agent suite.** The agent suite imports the worktree's client through `PYTHONPATH`, as the first command shows.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; PY=/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python
cd $WT/Iverson.Clients/Python && PYTHONPATH=$WT/Iverson.Clients/Python $PY -m pytest -q -p no:cacheprovider 2>&1 | tail -1
cd $WT/Iverson.Agents/Python && PYTHONPATH=$WT/Iverson.Clients/Python $PY -c "import iverson_client; print(iverson_client.__file__.replace('$WT', '\$WT'))"
PYTHONPATH=$WT/Iverson.Clients/Python $PY -m pytest -q -p no:cacheprovider 2>&1 | tail -1
```

Expected (times vary): the client suite passes 244 (241 before this task, plus three), the agent suite imports the worktree's client, and the agent suite passes 68:

```text
244 passed in 0.63s
$WT/Iverson.Clients/Python/iverson_client/__init__.py
68 passed in 1.46s
```

- [ ] **Step 13: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
D=Iverson.Clients/DotNet
git add $D/Iverson.Client.Core/ActingUserIdentity.cs $D/Iverson.Client.Core/EntityCoordinator.cs $D/Iverson.Client.Core/SchemaCatalogClient.cs $D/Iverson.Client.Core/ServiceCollectionExtensions.cs $D/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs $D/Iverson.Client.Core.Tests/TestCoordinatorFactory.cs $D/Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs $D/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_entity_coordinator.py
git status --short Iverson.Clients Iverson.Agents
git commit -m "refuse acting-user tokens on hand-built .net and python coordinators when the transport is unknown

$TRAILER"
```

Expected: the status lists exactly the ten paths, each `M ` (staged); the commit reports `10 files changed`.

### Task 3: LoadTest and conformance harness endpoint checks (#15)

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs`
- Create: `Iverson.Server/Iverson.LoadTest.Tests/Auth/CredentialEndpointsTests.cs`
- Create: `Iverson.Server/Iverson.ClientConformance.Tests/HarnessEndpointCheckTests.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` (the Authentik base URL derivation and the `AddIversonClient` flag)
- Modify: `Iverson.Server/Iverson.ClientConformance/Program.cs` (a `using` and the startup check), `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs` (`DeriveAuthentikBaseUrl` only)
- Modify: `docs/runbooks/kind-cluster-troubleshooting.md` (§5.1's closing paragraph)

**Interfaces:**
- Produces `Iverson.LoadTest.Auth.CredentialEndpoints` (public, static): `Check(string grpcUrl, string? tokenEndpoint, bool grpcMayUseTls) → string?` (the refusal message, or null), `AuthentikBaseUrl(string? tokenEndpoint) → string`, `IsLoopback(string url) → bool`, and the constant `TokenEndpointPath`. LoadTest calls `Check` with `grpcMayUseTls: true`; the harness, which already references `Iverson.LoadTest`, calls it with `false`, because its own channel is plaintext.
- `TokenBroker.cs` is also edited by the Duende.IdentityModel task (its `using IdentityModel.Client` line only). This task edits only the `DeriveAuthentikBaseUrl` block, so either order applies cleanly.

One check covers both programs. It uses `Uri.IsLoopback` (spec verified assumption 8). The token endpoint's shape is checked first, so a malformed value gets a message instead of the `IndexOf` range exception the old derivation threw. Then the gRPC URL is checked (loopback, or https for LoadTest), then the token endpoint and the Authentik base URL (https or loopback). Last, for LoadTest only, a non-loopback `https://` gRPC URL requires an `https://` token endpoint (user decision 2026-10-12, CIR-1 §3.1 C): with that gRPC URL the client library is not opted in to plaintext credentials and refuses a plaintext token endpoint when the client is registered, so the startup check refuses the pairing first. This narrows spec §2's "https or loopback" rule for that one pairing. LoadTest runs the check unconditionally, before the provisioning block and before `AdminAutomationCredentials` is built. The SDK's own flag cannot see two paths, and the check covers both: the bare provisioning channel and the raw `Metadata.WithActingUser` sites (verified assumption 6). The harness runs it before it derives the base URL and before preflight; the five drivers receive the same URL and secrets (verified assumption 7).

- [ ] **Step 1: Write the failing tests.**

Create `Iverson.Server/Iverson.LoadTest.Tests/Auth/CredentialEndpointsTests.cs` with exactly:

```cs
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

// CSR round-11 #15: LoadTest sends credentials to its gRPC URL only when it is loopback or https,
// and to the token endpoint and the Authentik base URL only when they are https or loopback; with a
// non-loopback https gRPC URL the token endpoint must be https.
public class CredentialEndpointsTests
{
    [Theory]
    [InlineData("http://localhost:8080", null)]
    [InlineData("http://LOCALHOST:8080", "http://localhost:9000/application/o/token/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:9000/application/o/token/")]
    [InlineData("http://[::1]:8080", "http://[::1]:9000/application/o/token/")]
    [InlineData("https://iverson-api:8080", "https://iverson-authentik:8443/application/o/token/")]
    [InlineData("http://localhost:8080", "https://idp.example.com/application/o/token/")]
    public void Check_AcceptsLoopbackOrHttpsEndpoints(string grpcUrl, string? tokenEndpoint) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true).Should().BeNull();

    [Theory]
    [InlineData("http://iverson-api:8080", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("http://0.0.0.0:8080", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("not a url", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "http://iverson-authentik:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address")]
    [InlineData("https://iverson-api:8080", "http://localhost:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address")]
    [InlineData("http://localhost:8080", "https://iverson-authentik:8443/token", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    [InlineData("http://localhost:8080", "localhost:9000", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    // The shape is checked first: a malformed token endpoint is named even when the gRPC URL is also refused.
    [InlineData("http://iverson-api:8080", "not a url", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    public void Check_RefusesAPlaintextNetworkEndpointOrAMalformedTokenEndpoint(
        string grpcUrl, string? tokenEndpoint, string expected) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true).Should().StartWith(expected);

    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://iverson-api:8080", false)]
    [InlineData("http://iverson-api:8080", false)]
    public void IsLoopback_IsTrueOnlyForALoopbackHost(string url, bool expected) =>
        CredentialEndpoints.IsLoopback(url).Should().Be(expected);

    [Theory]
    [InlineData(null, "http://localhost:9000")]
    [InlineData("https://iverson-authentik:8443/application/o/token/", "https://iverson-authentik:8443")]
    public void AuthentikBaseUrl_IsTheTokenEndpointWithoutItsPath(string? tokenEndpoint, string expected) =>
        CredentialEndpoints.AuthentikBaseUrl(tokenEndpoint).Should().Be(expected);
}
```

Create `Iverson.Server/Iverson.ClientConformance.Tests/HarnessEndpointCheckTests.cs` with exactly:

```cs
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// CSR round-11 #15: the harness's own channel is plaintext (<c>ChannelCredentials.Insecure</c>
/// with <c>UnsafeUseInsecureChannelCallCredentials</c>) and carries the service and acting-user
/// tokens, so Program.cs refuses a gRPC URL that is not loopback, https or not. The token endpoint
/// and the Authentik base URL must be https or loopback, as in LoadTest.
/// </summary>
public class HarnessEndpointCheckTests
{
    [Theory]
    [InlineData("http://localhost:8080", null)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:9000/application/o/token/")]
    [InlineData("http://[::1]:8080", "https://idp.example.com/application/o/token/")]
    public void Check_AcceptsALoopbackGrpcUrl(string grpcUrl, string? tokenEndpoint) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: false).Should().BeNull();

    [Theory]
    [InlineData("http://iverson-api:8080", null, "IVERSON_GRPC_URL must be a loopback address")]
    [InlineData("https://iverson-api:8080", null, "IVERSON_GRPC_URL must be a loopback address")]
    [InlineData("http://localhost:8080", "http://iverson-authentik:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "localhost:9000", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    public void Check_RefusesANonLoopbackGrpcUrlOrAPlaintextNetworkTokenEndpoint(
        string grpcUrl, string? tokenEndpoint, string expected) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: false).Should().StartWith(expected);
}
```

- [ ] **Step 2: Watch them fail.** Only LoadTest.Tests is built here; the harness tests fail on the same missing type, and building the harness tests also builds `Iverson.Api` (about four minutes).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Server
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.LoadTest.Tests -p:UseSharedCompilation=false 2>&1 | command grep -oE 'error CS[0-9]+: [^[]*' | sort -u; dotnet build-server shutdown >/dev/null 2>&1"
```

Expected:

```text
error CS0103: The name 'CredentialEndpoints' does not exist in the current context 
```

- [ ] **Step 3: The shared check.** Create `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs` with exactly:

```cs
namespace Iverson.LoadTest.Auth;

/// <summary>
/// CSR round-11 #15: where LoadTest and the client conformance harness may send credentials. Both
/// send an Operator-level service token and Authentik user passwords to three endpoints: the gRPC
/// URL, the token endpoint and the Authentik flow base URL. A loopback address never leaves the
/// machine; any other address needs TLS. Some of their credential-bearing calls bypass the SDK's
/// transport check (LoadTest's provisioning channel and raw acting-user metadata, the harness's own
/// channel), so this startup check is what keeps credentials off a plaintext network hop. It sees
/// only the configured URLs: a token endpoint that redirects is followed.
/// </summary>
public static class CredentialEndpoints
{
    public const string TokenEndpointPath = "/application/o/token/";

    /// <summary>
    /// Why these endpoints may not carry credentials, or null when they may. The token endpoint's
    /// shape is checked first, then the gRPC URL (loopback, or https when
    /// <paramref name="grpcMayUseTls"/>), then the token endpoint and the Authentik base URL
    /// (https or loopback), and last, with a non-loopback https gRPC URL, an https token endpoint.
    /// </summary>
    public static string? Check(string grpcUrl, string? tokenEndpoint, bool grpcMayUseTls)
    {
        if (tokenEndpoint is not null
            && !(Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out _)
                 && tokenEndpoint.EndsWith(TokenEndpointPath, StringComparison.Ordinal)))
            return $"IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in {TokenEndpointPath} " +
                   "(an Authentik token endpoint).";

        if (!Uri.TryCreate(grpcUrl, UriKind.Absolute, out var grpc)
            || !(grpc.IsLoopback || (grpcMayUseTls && grpc.Scheme == Uri.UriSchemeHttps)))
            return grpcMayUseTls
                ? "IVERSON_GRPC_URL must be https:// or a loopback address: the service and acting-user tokens are sent on this channel."
                : "IVERSON_GRPC_URL must be a loopback address: this harness's channel is plaintext and carries the service and acting-user tokens.";

        if (tokenEndpoint is not null && !IsHttpsOrLoopback(tokenEndpoint))
            return "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address: the client secret or assertion is sent to it.";

        // CIR-1 §3.1 C (user decision 2026-10-12): with a non-loopback https:// gRPC URL the client
        // library is not opted in to plaintext credentials, so it refuses a plaintext token endpoint
        // when the client is registered; refuse that pairing here, before any credential is sent.
        if (grpcMayUseTls && !grpc.IsLoopback && tokenEndpoint is not null
            && !(Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var token) && token.Scheme == Uri.UriSchemeHttps))
            return "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address: " +
                   "the client library sends client credentials only to an https token endpoint on such a channel.";

        if (!IsHttpsOrLoopback(AuthentikBaseUrl(tokenEndpoint)))
            return "The Authentik base URL (IVERSON_TOKEN_ENDPOINT without its path) must be https:// or a " +
                   "loopback address: user passwords and TOTP codes are sent to it.";

        return null;
    }

    /// <summary>
    /// The Authentik flow base URL: the token endpoint up to its <see cref="TokenEndpointPath"/>,
    /// or the compose default when none is set. Call it only after <see cref="Check"/> accepted the
    /// token endpoint.
    /// </summary>
    public static string AuthentikBaseUrl(string? tokenEndpoint) => tokenEndpoint is not null
        ? tokenEndpoint[..tokenEndpoint.IndexOf(TokenEndpointPath, StringComparison.Ordinal)]
        : "http://localhost:9000";

    public static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static bool IsHttpsOrLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
}
```

- [ ] **Step 4: LoadTest runs the check at startup and opts in to plaintext only on loopback.**

In `Iverson.Server/Iverson.LoadTest/Program.cs`, replace:

```cs
var actingUserBaseUrl = tokenEndpoint is not null
    ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
    : "http://localhost:9000";
```

with:

```cs
// CSR round-11 #15: checked before any command sends a credential, including the provisioning
// channel and the raw acting-user metadata, which the SDK's transport check does not see.
if (CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true) is { } endpointRefusal)
{
    Console.Error.WriteLine(endpointRefusal);
    return 1;
}
var actingUserBaseUrl = CredentialEndpoints.AuthentikBaseUrl(tokenEndpoint);
```

In `Iverson.Server/Iverson.LoadTest/Program.cs`, replace:

```cs
        allowInsecureChannelCallCredentials: true,
```

with:

```cs
        // CSR round-11 #15: plaintext only to a loopback gRPC URL; the startup check has already
        // refused any other http:// URL.
        allowInsecureChannelCallCredentials: CredentialEndpoints.IsLoopback(grpcUrl),
```

Program.cs already has `using Iverson.LoadTest.Auth;`. With a loopback `http://` URL the flag is true, so the ambient tenant-admin token still binds. A non-loopback `https://` URL is not refused by the SDK's channel check, whose predicate is the `http` scheme; the SDK's token-endpoint check would refuse a plaintext token endpoint with it, a pairing the startup check now refuses first (CIR-1 §3.1 C). A non-loopback `http://` URL never reaches the SDK (verified assumption 34).

- [ ] **Step 5: The harness runs the check before preflight and shares the derivation.**

In `Iverson.Server/Iverson.ClientConformance/Program.cs`, replace:

```cs
using Iverson.ClientConformance.Scenarios;
```

with:

```cs
using Iverson.ClientConformance.Scenarios;
using Iverson.LoadTest.Auth;
```

In `Iverson.Server/Iverson.ClientConformance/Program.cs`, replace:

```cs
// Single source of truth for the Authentik base URL — TokenBroker derives it the same way, so
```

with:

```cs
// CSR round-11 #15: the harness's channel below is plaintext and carries both tokens, and the five
// drivers receive the same URL and secrets, so nothing runs unless the gRPC URL is loopback and
// the token endpoint and the Authentik base URL are https or loopback. Checked before the base URL
// is derived, so a malformed token endpoint is named instead of failing inside the derivation.
if (CredentialEndpoints.Check(grpcUrl, Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT"), grpcMayUseTls: false) is { } endpointRefusal)
{
    Console.Error.WriteLine(endpointRefusal);
    return 1;
}

// Single source of truth for the Authentik base URL — TokenBroker derives it the same way, so
```

In `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs`, replace:

```cs
    /// <summary>
    /// Derives the Authentik base URL the same way <c>Iverson.LoadTest/Program.cs:48-51</c> does:
    /// strip the token endpoint down to its origin when <c>IVERSON_TOKEN_ENDPOINT</c> is set,
    /// otherwise fall back to the compose default. This is the single source of truth for the
    /// value — <see cref="Program"/> computes it once here and feeds the same string to both
    /// <see cref="TokenBroker"/> and <see cref="Preflight"/> so the two never diverge.
    /// </summary>
    public static string DeriveAuthentikBaseUrl(string? tokenEndpoint) => tokenEndpoint is not null
        ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
        : "http://localhost:9000";
```

with:

```cs
    /// <summary>
    /// Derives the Authentik base URL with LoadTest's <see cref="CredentialEndpoints.AuthentikBaseUrl"/>,
    /// the derivation Program.cs's startup check accepted: strip the token endpoint down to its
    /// origin when <c>IVERSON_TOKEN_ENDPOINT</c> is set, otherwise fall back to the compose default.
    /// This is the single source of truth for the value — <see cref="Program"/> computes it once
    /// here and feeds the same string to both <see cref="TokenBroker"/> and <see cref="Preflight"/>
    /// so the two never diverge.
    /// </summary>
    public static string DeriveAuthentikBaseUrl(string? tokenEndpoint) =>
        CredentialEndpoints.AuthentikBaseUrl(tokenEndpoint);
```

`TokenBroker.cs` already has `using Iverson.LoadTest.Auth;`. The derivation the check accepts is now the one TokenBroker and Preflight use, so the two cannot drift.

- [ ] **Step 6: Build and run both suites.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Server
for p in Iverson.LoadTest.Tests Iverson.ClientConformance.Tests; do
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet build $p -p:UseSharedCompilation=false 2>&1 | command grep -E ' Error\(s\)|error CS' | sort -u; dotnet build-server shutdown >/dev/null 2>&1"
done
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.LoadTest.Tests --no-build --filter 'FullyQualifiedName~Auth.CredentialEndpointsTests' 2>&1 | tail -1; systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.ClientConformance.Tests --no-build --filter 'FullyQualifiedName~HarnessEndpointCheckTests' 2>&1 | tail -1; dotnet build-server shutdown >/dev/null 2>&1"
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.LoadTest.Tests --no-build 2>&1 | tail -1; systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.ClientConformance.Tests --no-build 2>&1 | tail -1; dotnet build-server shutdown >/dev/null 2>&1"
```

Expected (durations vary):

```text
    0 Error(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 216 ms - Iverson.LoadTest.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 66 ms - Iverson.ClientConformance.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   157, Skipped:     0, Total:   157, Duration: 2 s - Iverson.LoadTest.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   657, Skipped:     0, Total:   657, Duration: 9 s - Iverson.ClientConformance.Tests.dll (net10.0)
```

At `1b108e37` the full suites hold 136 and 650 tests; this task adds 21 and 7. If an earlier task in this plan changed either suite, the totals differ. What matters is `Failed: 0`, the 21 and the 7.

- [ ] **Step 7: Both programs refuse at startup, before any network call.** This runs the built binaries with a clean environment. LoadTest's `--help` exercises the unconditional check without contacting anything. Only refusal cases are run against the harness, because an accepted configuration would go on to preflight against localhost.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Server
DN=$(command -v dotnet); LT=Iverson.LoadTest/bin/Debug/net10.0/Iverson.LoadTest.dll; CC=Iverson.ClientConformance/bin/Debug/net10.0/Iverson.ClientConformance.dll
run() { env -i HOME="$HOME" DOTNET_CLI_TELEMETRY_OPTOUT=1 "$@" > $SCR/t3-run.txt 2>&1; rc=$?; echo "exit=$rc $(head -1 $SCR/t3-run.txt)"; }
run $DN $LT --help
run IVERSON_GRPC_URL=http://iverson-api:8080 $DN $LT --help
run IVERSON_TOKEN_ENDPOINT=http://iverson-authentik:9000/application/o/token/ $DN $LT --help
run IVERSON_GRPC_URL=https://iverson-api:8080 $DN $CC
run IVERSON_TOKEN_ENDPOINT=localhost:9000 $DN $CC
```

Expected:

```text
exit=0 Usage: dotnet run -- <command> [options]
exit=1 IVERSON_GRPC_URL must be https:// or a loopback address: the service and acting-user tokens are sent on this channel.
exit=1 IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address: the client secret or assertion is sent to it.
exit=1 IVERSON_GRPC_URL must be a loopback address: this harness's channel is plaintext and carries the service and acting-user tokens.
exit=1 IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/ (an Authentik token endpoint).
```

- [ ] **Step 8: The runbook note for an in-cluster LoadTest run.** Under the spec's CDR-1 §3.3 decision, the kind live check in spec §7 confirms this text before it ships; that check belongs to the plan's verification task. The note keeps `Host` advice for the gRPC-less curl case above it. It drops the claim that an in-cluster run makes Host headers "match naturally": LoadTest always sets the Host (verified assumptions 29 and 40).

In `docs/runbooks/kind-cluster-troubleshooting.md`, replace:

````markdown
is broken. For a gRPC client under test (e.g. `Iverson.LoadTest`) that can't have its Host header
overridden this way, run it **inside the cluster** (a throwaway debug pod, see 2.6 above for the
NetworkPolicy allowances it'll need) so its DNS resolution and Host headers match the real in-cluster
names naturally, rather than fighting port-forward hostname mismatches.
````

with:

````markdown
is broken.

**Running `Iverson.LoadTest` inside the cluster** (a throwaway debug pod; see 2.6 above for the
NetworkPolicy allowances it needs): LoadTest refuses to send credentials to an `http://` endpoint
that is not loopback (CSR round-11 #15), so point it at the TLS listeners and trust the chart CAs.

- `IVERSON_GRPC_URL=https://<release>-api:8080`. The API serves 8080 over TLS from the
  `<release>-api-internal-tls` Secret.
- `IVERSON_TOKEN_ENDPOINT=https://<release>-authentik:8443/application/o/token/`, set explicitly:
  its default is loopback, and LoadTest derives the Authentik base URL from it. Authentik's
  tls-proxy serves 8443 from the `<release>-authentik-internal-tls` Secret.
- `IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:8443`. LoadTest sets this Host on every
  Authentik request (default `authentik-server:9000`), so the in-cluster name does not reach
  Authentik by itself. Authentik stamps the token's `iss` from it, and the API accepts the issuer
  it reads from its own 8443 metadata, `https://<release>-authentik:8443/…`. .NET also checks the
  8443 certificate against this Host. With any other Host, one of the two checks fails.
- Trust both chart CAs: collect them in one file in the pod and point `SSL_CERT_FILE` at it.
  ```bash
  for s in <release>-api-internal-tls <release>-authentik-internal-tls; do
    kubectl get secret -n <namespace> "$s" -o jsonpath='{.data.ca\.crt}' | base64 -d
  done > chart-cas.pem   # copy into the pod, then: export SSL_CERT_FILE=/path/to/chart-cas.pem
  ```
````

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
command grep -c 'naturally' docs/runbooks/kind-cluster-troubleshooting.md
command grep -n 'IVERSON_ACTING_USER_HOST_HEADER=<release>-authentik:8443\|https://<release>-api:8080\|https://<release>-authentik:8443/application/o/token/' docs/runbooks/kind-cluster-troubleshooting.md | cut -c1-80
```

Expected: `0`, then three lines (the gRPC URL, the token endpoint and the Host header bullets, in that order).

- [ ] **Step 9: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs Iverson.Server/Iverson.LoadTest.Tests/Auth/CredentialEndpointsTests.cs Iverson.Server/Iverson.ClientConformance.Tests/HarnessEndpointCheckTests.cs Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.ClientConformance/Program.cs Iverson.Server/Iverson.ClientConformance/TokenBroker.cs
git add -f docs/runbooks/kind-cluster-troubleshooting.md
git status --short | command grep -v '^[MA] ' || true
git commit -q -m "refuse plaintext network endpoints in loadtest and the conformance harness

$TRAILER" && git show --stat --format=%s HEAD | tail -n +1
```

Expected: the status filter prints nothing; then the subject `refuse plaintext network endpoints in loadtest and the conformance harness` and `7 files changed, 200 insertions(+), 16 deletions(-)`.

### Task 4: Agent CLI output and prompt echoes (#17, #23)

**Files:**
- Modify: `Iverson.Agents/Python/iverson_agent/__main__.py` (`_printable`, the top-level handler, the three prints)
- Modify: `Iverson.Agents/Python/iverson_agent/retrieval.py` (two `log.warning` format strings)
- Modify: `Iverson.Agents/Python/iverson_agent/session.py` (an import and three echoes)
- Test: `Iverson.Agents/Python/tests/test_main.py`, `tests/test_session.py`, `tests/test_retrieval.py`

The agent imports `iverson_client` by path through a `.pth` file, not by installing it (`pyproject.toml`'s comment; the reasoning-agent plan's Task 1). The worktree has no `.venv`, so this task builds one in `$SCR/agent-venv` the same way. Its `.pth` points at the worktree's client, so the suite runs against this branch's SDK. The evaluate path prints only `json.dumps` of numbers and is unchanged (spec verified assumption 9).

- [ ] **Step 1: Write the failing tests.**

In `Iverson.Agents/Python/tests/test_main.py`, replace:

```python
def test_missing_credentials_fail_before_any_connection(monkeypatch):
    client_cls, _ = _env(monkeypatch)
    monkeypatch.delenv("IVERSON_CLIENT_SECRET")
    with pytest.raises(KeyError, match="IVERSON_CLIENT_SECRET"):
        cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    client_cls.assert_not_called()
```

with:

```python
def test_missing_credentials_fail_before_any_connection(monkeypatch, capsys):
    client_cls, _ = _env(monkeypatch)
    monkeypatch.delenv("IVERSON_CLIENT_SECRET")
    rc = cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    err = capsys.readouterr().err
    assert rc != 0
    assert "KeyError" in err and "IVERSON_CLIENT_SECRET" in err
    client_cls.assert_not_called()


def test_printable_removes_terminal_controls_but_keeps_newline_and_tab():
    assert cli._printable("a\x1b]0;title\x07b\x9bc\x7fd\x00e\rf\n\tg") == "a]0;titlebcdef\n\tg"
    assert cli._printable("Leave policy é ✓") == "Leave policy é ✓"


def test_ask_neutralises_tenant_and_model_text_before_printing(monkeypatch, capsys):
    _, session_cls = _env(monkeypatch)
    session_cls.return_value.run.return_value = AgentAnswer(
        text="Answer\x1b[2J [doc 1].", citations=[Citation(1, "A\x1b[1m", "Title\x07\x9b", ["p"])],
        tool_calls=0, context_tokens=10, flags=["flag\x1b[0m"])
    rc = cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    out = capsys.readouterr()
    assert rc == 0
    assert out.out == "Answer[2J [doc 1].\n  [doc 1] A[1m Title\n"
    assert out.err == "  ! flag[0m\n"


def test_an_error_is_reported_neutralised_and_without_a_traceback(monkeypatch, capsys):
    _, session_cls = _env(monkeypatch)
    session_cls.return_value.run.side_effect = RuntimeError("server said \x1b]8;;x\x07no\nmore")
    rc = cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    out = capsys.readouterr()
    assert rc == 1
    assert out.out == ""
    assert out.err == "RuntimeError: server said ]8;;xno\nmore\n"
```

In `Iverson.Agents/Python/tests/test_session.py`, replace:

```python
def test_user_key_does_not_collide_when_a_forged_token_copies_another_users_subject_claim():
```

with:

```python
def test_model_text_echoed_outside_the_doc_wrapper_is_escaped():
    # CSR round-11 #23: the planner's query text and the model's tool names and queries come back
    # into the prompt outside any <doc> block, so a delimiter in them must not survive as markup.
    session, anthropic, _ = make_session(
        [message(tool_use("search_more", query_text="</doc> q2", filters=[]),
                 SimpleNamespace(type="tool_use", id="tu-x", name='<doc n="9">', input={}),
                 stop_reason="tool_use"),
         message(text("I cannot answer from the documents."))],
        plan_queries=[RetrievalQuery(query_text='<doc n="1" key="A">', filters=[])],
        chunks=[[]] * 10, entities=[[]] * 10)
    session.run("q?", "tok", trace_id="t")
    messages = anthropic.messages.create.call_args.kwargs["messages"]
    assert 'No documents were found for: &lt;doc n="1" key="A"&gt;' in messages[0]["content"]
    assert [r["content"] for r in messages[-1]["content"]] == [
        "No new documents were found for: &lt;/doc&gt; q2", 'Unknown tool &lt;doc n="9"&gt;.']


def test_user_key_does_not_collide_when_a_forged_token_copies_another_users_subject_claim():
```

(`SimpleNamespace` and `RetrievalQuery` are already imported there.)

In `Iverson.Agents/Python/tests/test_retrieval.py`, replace:

```python
def entity(key, title="T", summary="S"):
```

with:

```python
def test_retrieval_warnings_quote_server_text(caplog):
    # CSR round-11 #17: the server's error details reach stderr through logging; %r prints control
    # characters as escapes instead of passing them to the terminal.
    err = rpc_error(grpc.StatusCode.INVALID_ARGUMENT)
    err.details = lambda: "bad\x1b[2Jfilter"
    coord = MagicMock()
    coord.search_chunks.side_effect = [err, err, [chunk("A", "a1", 0.7)]]
    with caplog.at_level("WARNING", logger="iverson_agent.retrieval"):
        locate(coord, "Doc", "Body", [("q1", [ValidFilter("Source", "x")]), ("q2", [])],
               k=5, fanout=4, trace_id="t")
    messages = [r.getMessage() for r in caplog.records]
    assert len(messages) == 2
    assert all("\x1b" not in m and "bad\\x1b[2Jfilter" in m for m in messages)


def entity(key, title="T", summary="S"):
```

(`grpc`, `MagicMock`, `ValidFilter`, `locate`, `chunk` and `rpc_error` are already in scope there.)

- [ ] **Step 2: Build the scratch venv and watch the tests fail.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
if [ ! -x $SCR/agent-venv/bin/python ]; then
  python3 -m venv --without-pip $SCR/agent-venv
  systemd-run --user --scope -q -p MemoryMax=2G python3 -m pip --python $SCR/agent-venv/bin/python install -q "anthropic>=1.0,<2" "pydantic>=2,<3" "grpcio>=1.81.1" "protobuf>=5.29.0" "pytest>=8"
fi
echo $WT/Iverson.Clients/Python > $(ls -d $SCR/agent-venv/lib/python3*/site-packages)/iverson_client.pth
cd $WT/Iverson.Agents/Python && $SCR/agent-venv/bin/python -m pytest -q 2>&1 | command grep -E '^FAILED|passed|failed' | sed 's/ - .*//; s/ in [0-9.]*s.*//'
```

Expected:

```text
FAILED tests/test_main.py::test_missing_credentials_fail_before_any_connection
FAILED tests/test_main.py::test_printable_removes_terminal_controls_but_keeps_newline_and_tab
FAILED tests/test_main.py::test_ask_neutralises_tenant_and_model_text_before_printing
FAILED tests/test_main.py::test_an_error_is_reported_neutralised_and_without_a_traceback
FAILED tests/test_retrieval.py::test_retrieval_warnings_quote_server_text
FAILED tests/test_session.py::test_model_text_echoed_outside_the_doc_wrapper_is_escaped
6 failed, 67 passed
```

- [ ] **Step 3: `_printable` and the top-level handler.**

In `Iverson.Agents/Python/iverson_agent/__main__.py`, replace:

```python
import os
import sys
from pathlib import Path
```

with:

```python
import os
import re
import sys
from pathlib import Path
```

In `Iverson.Agents/Python/iverson_agent/__main__.py`, replace:

```python
def _entity(spec: str) -> type:
```

with:

```python
# C0 controls except tab (\x09) and newline (\x0a), DEL (\x7f), and C1 controls (\x80-\x9f).
_TERMINAL_CONTROLS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")


def _printable(text: str) -> str:
    """`text` with the characters a terminal acts on removed (CSR round-11 #17).

    Answers, document keys and titles, flags and error messages carry tenant-authored or model
    text; printed raw, an escape or control sequence in them would drive the operator's terminal."""
    return _TERMINAL_CONTROLS.sub("", text)


def _entity(spec: str) -> type:
```

In `Iverson.Agents/Python/iverson_agent/__main__.py`, replace:

```python
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="iverson_agent")
```

with:

```python
def main(argv: list[str] | None = None) -> int:
    """Runs the CLI. Any error ends it with one `Type: message` report on stderr and exit status 1
    instead of a traceback, because tracebacks would carry server and model text raw
    (CSR round-11 #17). argparse's own exits are SystemExit and pass through unchanged."""
    try:
        return _run(argv)
    except Exception as err:  # noqa: BLE001 -- top-level CLI error reporting
        print(f"{type(err).__name__}: {_printable(str(err))}", file=sys.stderr)
        return 1


def _run(argv: list[str] | None) -> int:
    ap = argparse.ArgumentParser(prog="iverson_agent")
```

The old body of `main` becomes `_run`'s body unchanged, so the tests that patch `cli.IversonClient`, `cli.AgentSession` and `cli.anthropic` still reach it.

In `Iverson.Agents/Python/iverson_agent/__main__.py`, replace:

```python
            print(answer.text)
            for c in answer.citations:
                print(f"  [doc {c.doc_number}] {c.key} {c.title or ''}")
            for f in answer.flags:
                print(f"  ! {f}", file=sys.stderr)
```

with:

```python
            print(_printable(answer.text))
            for c in answer.citations:
                print(f"  [doc {c.doc_number}] {_printable(c.key)} {_printable(c.title or '')}")
            for f in answer.flags:
                print(f"  ! {_printable(f)}", file=sys.stderr)
```

- [ ] **Step 4: `%r` for server text in the two retrieval warnings.**

In `Iverson.Agents/Python/iverson_agent/retrieval.py`, replace:

```python
                log.warning("[locate] trace=%s InvalidArgument with filters; retrying unfiltered: %s",
```

with:

```python
                # %r, not %s: the details are server text and reach the terminal (CSR round-11 #17).
                log.warning("[locate] trace=%s InvalidArgument with filters; retrying unfiltered: %r",
```

In `Iverson.Agents/Python/iverson_agent/retrieval.py`, replace:

```python
            log.warning("[locate] trace=%s skipping query %r after unfiltered retry: %s",
```

with:

```python
            log.warning("[locate] trace=%s skipping query %r after unfiltered retry: %r",
```

- [ ] **Step 5: Escape the three prompt echoes.**

In `Iverson.Agents/Python/iverson_agent/session.py`, replace:

```python
    DocumentContext, RankedParent, _render_one, assemble, estimate_tokens, locate, render_context,
```

with:

```python
    DocumentContext, RankedParent, _escape, _render_one, assemble, estimate_tokens, locate, render_context,
```

In `Iverson.Agents/Python/iverson_agent/session.py`, replace:

```python
            parts.append(f"No documents were found for: {q}")
```

with:

```python
            # CSR round-11 #23: model text outside the <doc> wrapper is escaped like the text inside it.
            parts.append(f"No documents were found for: {_escape(q)}")
```

In `Iverson.Agents/Python/iverson_agent/session.py`, replace:

```python
                return f"No new documents were found for: {inp['query_text']}"
```

with:

```python
                return f"No new documents were found for: {_escape(inp['query_text'])}"
```

In `Iverson.Agents/Python/iverson_agent/session.py`, replace:

```python
        return f"Unknown tool {name}."
```

with:

```python
        return f"Unknown tool {_escape(name)}."
```

- [ ] **Step 6: The agent suite passes.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT/Iverson.Agents/Python && $SCR/agent-venv/bin/python -m pytest -q 2>&1 | tail -1 | sed 's/ in [0-9.]*s.*//'
```

Expected: `73 passed` (68 at `1b108e37`, plus the 5 new tests; the rewritten one replaces its old form).

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Agents/Python/iverson_agent/__main__.py Iverson.Agents/Python/iverson_agent/retrieval.py Iverson.Agents/Python/iverson_agent/session.py Iverson.Agents/Python/tests/test_main.py Iverson.Agents/Python/tests/test_session.py Iverson.Agents/Python/tests/test_retrieval.py
git status --short | command grep -v '^[MA] ' || true
git commit -q -m "neutralise the agent cli's terminal output and escape its prompt echoes

$TRAILER" && git show --stat --format=%s HEAD | tail -n +1
```

Expected: the status filter prints nothing; then the subject `neutralise the agent cli's terminal output and escape its prompt echoes` and `6 files changed, 99 insertions(+), 12 deletions(-)`.

### Task 5: Password off argv (#19)

**Files:**
- Create: `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py`
- Modify: `Iverson.Server/deploy/scripts/mint_acting_user_token.py` (module docstring, a constant, the `--password` argument, both password reads)
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` (module docstring, `mint_acting_user_token`)
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py`
- Modify: `docs/user-management-and-security.md` (the compose example and the CLI table's `--password` row)

The script reads `IVERSON_ACTING_USER_PASSWORD`, LoadTest's existing name for the same smoke-test user, in both branches. compose requires it. kind uses it first, then the kind Secret. `pattern_leg.mint_acting_user_token` is the only code caller (spec verified assumption 11). `dialogue_pool.py` and `dialogue_patterns.py` call it with the same one-argument signature, which does not change. The new mint-script test sits beside the script, as the LoadTest scripts' `test_*.py` files do, and runs under the same command.

- [ ] **Step 1: Write the failing tests.**

Create `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py` with exactly:

```python
"""pytest suite for mint_acting_user_token.py's password source (CSR round-11 #19). Run with:

    python3 -m pytest Iverson.Server/deploy/scripts/test_mint_acting_user_token.py -q

No network and no cluster: the flow (`run`), the kubectl port-forward and the Secret lookup are
fakes. Only main()'s choice of password is under test."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mint_acting_user_token as mint  # noqa: E402

ENV = "IVERSON_ACTING_USER_PASSWORD"


@pytest.fixture
def fakes(monkeypatch):
    """Records the password main() hands to the flow and every Secret it reads."""
    seen = {"secrets": []}

    class NoPortForward:
        def __init__(self, *args):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def fake_secret(namespace, secret_name, key):
        seen["secrets"].append(secret_name)
        return f"from-{secret_name}"

    def fake_run(args, base_url, host_header, client_id, password, redirect_uri):
        seen["password"] = password
        return 0

    monkeypatch.setattr(mint, "PortForward", NoPortForward)
    monkeypatch.setattr(mint, "kubectl_get_secret_value", fake_secret)
    monkeypatch.setattr(mint, "run", fake_run)
    return seen


def main_with(monkeypatch, *argv):
    monkeypatch.setattr(sys, "argv", ["mint_acting_user_token.py", *argv])
    return mint.main()


def test_compose_reads_the_password_from_the_environment(monkeypatch, fakes):
    monkeypatch.setenv(ENV, "env-pw")
    assert main_with(monkeypatch, "--target", "compose") == 0
    assert fakes["password"] == "env-pw"


def test_compose_without_the_environment_variable_fails_naming_it(monkeypatch, fakes, capsys):
    monkeypatch.delenv(ENV, raising=False)
    assert main_with(monkeypatch, "--target", "compose") == 1
    assert "password" not in fakes
    assert ENV in capsys.readouterr().err


def test_kind_prefers_the_environment_over_the_secret(monkeypatch, fakes):
    monkeypatch.setenv(ENV, "env-pw")
    assert main_with(monkeypatch, "--target", "kind") == 0
    assert fakes["password"] == "env-pw"
    assert "iverson-authentik-smoke-test-user" not in fakes["secrets"]


def test_kind_without_the_environment_variable_reads_the_secret(monkeypatch, fakes):
    monkeypatch.delenv(ENV, raising=False)
    assert main_with(monkeypatch, "--target", "kind") == 0
    assert fakes["password"] == "from-iverson-authentik-smoke-test-user"


def test_there_is_no_password_flag(monkeypatch, fakes):
    with pytest.raises(SystemExit) as exit_info:
        main_with(monkeypatch, "--target", "compose", "--password", "argv-pw")
    assert exit_info.value.code == 2
    assert "password" not in fakes
```

In `Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py`, replace:

```python
# ── /build ──────────────────────────────────────────────────────────────────────────────
```

with:

```python
# ── mint_acting_user_token: the password reaches the child through its environment ─────────────

def test_mint_acting_user_token_passes_the_password_in_the_childs_environment_not_its_argv(monkeypatch):
    # CSR round-11 #19: a command line is visible to every local user; a process environment is not.
    seen = {}

    class Completed:
        returncode, stdout, stderr = 0, "user-tok\n", ""

    def fake_run(argv, **kwargs):
        seen["argv"], seen["kwargs"] = argv, kwargs
        return Completed()

    monkeypatch.setenv(pl.BYPASS_PASSWORD_ENV, "bypass-pw")
    monkeypatch.setenv("IVERSON_ACTING_USER_PASSWORD", "smoke-test-pw")
    monkeypatch.setattr(pl.subprocess, "run", fake_run)
    assert pl.mint_acting_user_token("iverson-loadtest-bypass-user") == "user-tok"
    assert not any("pw" in a for a in seen["argv"]) and "--password" not in seen["argv"]
    assert seen["argv"][1:] == [pl.MINT_SCRIPT, "--target", "compose", "--username", "iverson-loadtest-bypass-user"]
    env = seen["kwargs"]["env"]
    assert env["IVERSON_ACTING_USER_PASSWORD"] == "bypass-pw"      # the bypass user's, not the smoke-test user's
    assert env["PATH"] == os.environ["PATH"]                        # the rest of the environment is inherited


# ── /build ──────────────────────────────────────────────────────────────────────────────
```

- [ ] **Step 2: Watch them fail.** The kind branch already falls back to the Secret, so that one test passes before the change.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 -m pytest Iverson.Server/deploy/scripts/test_mint_acting_user_token.py Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q 2>&1 | command grep -E '^FAILED|passed|failed' | sed 's#Iverson.Server/##; s/ - .*//; s/ in [0-9.]*s.*//'
```

Expected:

```text
FAILED deploy/scripts/test_mint_acting_user_token.py::test_compose_reads_the_password_from_the_environment
FAILED deploy/scripts/test_mint_acting_user_token.py::test_compose_without_the_environment_variable_fails_naming_it
FAILED deploy/scripts/test_mint_acting_user_token.py::test_kind_prefers_the_environment_over_the_secret
FAILED deploy/scripts/test_mint_acting_user_token.py::test_there_is_no_password_flag
FAILED Iverson.LoadTest/scripts/test_pattern_leg.py::test_mint_acting_user_token_passes_the_password_in_the_childs_environment_not_its_argv
5 failed, 59 passed
```

- [ ] **Step 3: The mint script reads the password from the environment.**

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace:

```python
    python3 deploy/scripts/mint_acting_user_token.py --target compose \
        > /tmp/acting-user-token.txt
```

with:

```python
    IVERSON_ACTING_USER_PASSWORD=... \
        python3 deploy/scripts/mint_acting_user_token.py --target compose \
        > /tmp/acting-user-token.txt
```

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace:

```python
           `<release>-authentik:9000` (the in-cluster DNS name the API's
           OIDC `Authority` is actually configured with). client_id and
           the smoke-test user's password are read from the Secrets Task 5
           provisions (`<release>-authentik-loadtest-human-client`,
           `<release>-authentik-smoke-test-user`) unless overridden.
```

with:

```python
           `<release>-authentik:9000` (the in-cluster DNS name the API's
           OIDC `Authority` is actually configured with). client_id is read
           from the `<release>-authentik-loadtest-human-client` Secret
           unless --client-id overrides it.

The user's password comes from the environment variable
IVERSON_ACTING_USER_PASSWORD (LoadTest's name for the same smoke-test
user), never from the command line, which any local user can read from the
process list. compose requires it: Authentik's dev-only smoke-test password
is randomly generated per docker-compose stack by
scripts/generate-compose-secrets.sh (IVERSON_SMOKE_TEST_PASSWORD in
Iverson.Server/.env). kind uses it when set, and otherwise reads the
`<release>-authentik-smoke-test-user` Secret.
```

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace:

```python
CACHE_DIR = os.path.expanduser("~/.cache/iverson")
```

with:

```python
CACHE_DIR = os.path.expanduser("~/.cache/iverson")
# CSR round-11 #19: the password is read from the environment, never from argv.
PASSWORD_ENV = "IVERSON_ACTING_USER_PASSWORD"
```

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace (this removes the `--password` argument; the docstring above is the `--help` description):

```python
    p.add_argument("--password", default=None, help="Required for --target compose (Authentik's dev-only smoke-test password is randomly generated per docker-compose stack by scripts/generate-compose-secrets.sh -- read IVERSON_SMOKE_TEST_PASSWORD from Iverson.Server/.env); read from the kind Secret otherwise")
    p.add_argument("--client-id", default=None, help="Defaults per-target (fixed dev value for compose, read from the kind Secret otherwise)")
```

with:

```python
    p.add_argument("--client-id", default=None, help="Defaults per-target (fixed dev value for compose, read from the kind Secret otherwise)")
```

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace:

```python
        if args.password is None:
            log(
                "ERROR: --password is required for --target compose. Authentik's dev-only "
                "smoke-test password is randomly generated per docker-compose stack by "
                "scripts/generate-compose-secrets.sh -- read IVERSON_SMOKE_TEST_PASSWORD out of "
                "Iverson.Server/.env and pass it explicitly."
            )
            return 1
        password = args.password
```

with:

```python
        password = os.environ.get(PASSWORD_ENV)
        if not password:
            log(
                f"ERROR: {PASSWORD_ENV} must be set for --target compose. Authentik's dev-only "
                "smoke-test password is randomly generated per docker-compose stack by "
                "scripts/generate-compose-secrets.sh -- export IVERSON_SMOKE_TEST_PASSWORD's value "
                f"from Iverson.Server/.env as {PASSWORD_ENV}."
            )
            return 1
```

In `Iverson.Server/deploy/scripts/mint_acting_user_token.py`, replace:

```python
        password = args.password or kubectl_get_secret_value(
```

with:

```python
        password = os.environ.get(PASSWORD_ENV) or kubectl_get_secret_value(
```

- [ ] **Step 4: `pattern_leg` hands the password to the child in its environment.**

In `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py`, replace:

```python
runs deploy/scripts/mint_acting_user_token.py --target compose --username
iverson-loadtest-bypass-user --password $IVERSON_ACTING_USER_BYPASS_PASSWORD as a subprocess, and
re-mints once the token is TOKEN_REFRESH_SECONDS (90 minutes) old. Both tokens live in one
```

with:

```python
runs deploy/scripts/mint_acting_user_token.py --target compose --username
iverson-loadtest-bypass-user as a subprocess, and re-mints once the token is TOKEN_REFRESH_SECONDS
(90 minutes) old. The child gets $IVERSON_ACTING_USER_BYPASS_PASSWORD as its
IVERSON_ACTING_USER_PASSWORD, in its environment rather than on its command line, which any local
user can read from the process list (CSR round-11 #19). Both tokens live in one
```

In `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py`, replace:

```python
    result = subprocess.run(
        [sys.executable, MINT_SCRIPT, "--target", "compose", "--username", username, "--password", password],
        capture_output=True, text=True)
```

with:

```python
    result = subprocess.run(
        [sys.executable, MINT_SCRIPT, "--target", "compose", "--username", username],
        env={**os.environ, "IVERSON_ACTING_USER_PASSWORD": password},
        capture_output=True, text=True)
```

- [ ] **Step 5: The tests pass and the CLI behaves.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 -m pytest Iverson.Server/deploy/scripts/test_mint_acting_user_token.py Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py -q 2>&1 | tail -1 | sed 's/ in [0-9.]*s.*//'
M=Iverson.Server/deploy/scripts/mint_acting_user_token.py
python3 $M --help | command grep -c IVERSON_ACTING_USER_PASSWORD
python3 $M --target compose --password x > $SCR/t5-run.txt 2>&1; echo "exit=$? $(tail -1 $SCR/t5-run.txt)"
env -u IVERSON_ACTING_USER_PASSWORD python3 $M --target compose > $SCR/t5-run.txt 2>&1; echo "exit=$? $(cut -c1-80 $SCR/t5-run.txt)"
command grep -n -e '--password' $M Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py || echo "no --password left"
```

Expected:

```text
64 passed
2
exit=2 mint_acting_user_token.py: error: unrecognized arguments: --password x
exit=1 ERROR: IVERSON_ACTING_USER_PASSWORD must be set for --target compose. Authentik'
no --password left
```

The compose run without the variable stops before any request.

- [ ] **Step 6: The user-management doc.**

In `docs/user-management-and-security.md`, replace:

```bash
# --password is required for --target compose: Authentik's dev-only smoke-test password is
# randomly generated per docker-compose stack by scripts/generate-compose-secrets.sh (CSR round-4
# finding #8) — read it out of Iverson.Server/.env's IVERSON_SMOKE_TEST_PASSWORD.
python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose --password "$(grep IVERSON_SMOKE_TEST_PASSWORD Iverson.Server/.env | cut -d= -f2)"
```

with:

```bash
# The password comes from IVERSON_ACTING_USER_PASSWORD, never from the command line (CSR round-11
# #19). --target compose requires it: Authentik's dev-only smoke-test password is randomly
# generated per docker-compose stack by scripts/generate-compose-secrets.sh (CSR round-4 finding
# #8) — read it out of Iverson.Server/.env's IVERSON_SMOKE_TEST_PASSWORD.
IVERSON_ACTING_USER_PASSWORD="$(grep IVERSON_SMOKE_TEST_PASSWORD Iverson.Server/.env | cut -d= -f2)" \
  python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose
```

In `docs/user-management-and-security.md`, replace:

```text
--password                required for --target compose (Authentik's dev-only smoke-test password
                          is randomly generated per stack by scripts/generate-compose-secrets.sh;
                          read IVERSON_SMOKE_TEST_PASSWORD from Iverson.Server/.env); for
                          --target kind, default: read from the kind Secret
```

with:

```text
(no --password flag)      the password comes from the IVERSON_ACTING_USER_PASSWORD environment
                          variable: required for --target compose (read IVERSON_SMOKE_TEST_PASSWORD
                          from Iverson.Server/.env); for --target kind, used when set, otherwise
                          read from the kind Secret
```

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
command grep -n -e '--password' docs/user-management-and-security.md
```

Expected: one line, `288:(no --password flag)      the password comes from the IVERSON_ACTING_USER_PASSWORD environment`.

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/scripts/test_mint_acting_user_token.py Iverson.Server/deploy/scripts/mint_acting_user_token.py Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py
git add -f docs/user-management-and-security.md
git status --short | command grep -v '^[MA] ' || true
git commit -q -m "read the acting-user password from the environment instead of argv

$TRAILER" && git show --stat --format=%s HEAD | tail -n +1
```

Expected: the status filter prints nothing; then the subject `read the acting-user password from the environment instead of argv` and `5 files changed, 144 insertions(+), 23 deletions(-)`.

### Task 6: Duende.IdentityModel 8.1.0 (#19, spec §3 ".NET IdentityModel")

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs`

LoadTest, ClientConformance and the Sample get the package through their `ProjectReference` to `Iverson.Client.Core`, so only the two projects that reference it directly change. Each edit replaces one whole line, so it applies whatever Tasks 2–5 changed elsewhere in these files. LoadTest's client-assertion request already sets `ClientCredentialStyle.PostBody`, which 8.x requires (spec verified assumption 17), so no code changes beyond the `using` lines.

- [ ] **Step 1: Swap the package and the namespace.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 - <<'PYEOF'
def sub(path, old, new):
    # bytes in, bytes out: keeps the Core csproj's UTF-8 BOM and every line ending as they are
    s = open(path, "rb").read().decode("utf-8")
    assert s.count(old) == 1, (path, old, s.count(old))
    open(path, "wb").write(s.replace(old, new).encode("utf-8"))

for p in ("Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj",
          "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj"):
    sub(p, '<PackageReference Include="IdentityModel" Version="7.0.0" />',
           '<PackageReference Include="Duende.IdentityModel" Version="8.1.0" />')
for p in ("Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs",
          "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs",
          "Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs",
          "Iverson.Server/Iverson.ClientConformance/TokenBroker.cs"):
    sub(p, "using IdentityModel.Client;\n", "using Duende.IdentityModel.Client;\n")
print("swapped")
PYEOF
git diff --stat
command grep -rn --include=*.cs --include=*.csproj -E '(^|[^.])\bIdentityModel\b' Iverson.Clients Iverson.Server | command grep -v -E 'Microsoft\.IdentityModel|System\.IdentityModel|Duende\.IdentityModel' || echo "no legacy IdentityModel reference"
```

Expected: `swapped`; the stat lists the six files, each `2 +-` (one line out, one in); the grep prints only `Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs:82`, a test assertion's reason string ("IdentityModel's default sends the secret as HTTP Basic, as today"), which stays true of `Duende.IdentityModel` and is left as is.

- [ ] **Step 2: Build and test the affected .NET projects.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
for p in Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver Iverson.Clients/DotNet/Iverson.Client.Sample Iverson.Server/Iverson.ClientConformance; do
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet build $p -p:UseSharedCompilation=false 2>&1 | grep -E 'Warn|Error\(s\)|error' | tail -3; dotnet build-server shutdown >/dev/null 2>&1"
done
for t in Iverson.Clients/DotNet/Iverson.Client.Core.Tests Iverson.Server/Iverson.LoadTest.Tests Iverson.Server/Iverson.ClientConformance.Tests; do
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=6G dotnet test $t -p:UseSharedCompilation=false 2>&1 | grep -E '^(Passed|Failed)!|error' | tail -3; dotnet build-server shutdown >/dev/null 2>&1"
done
for p in Iverson.Clients/DotNet/Iverson.Client.Core Iverson.Server/Iverson.ClientConformance; do
  dotnet list $p package --include-transitive 2>/dev/null | command grep -i identitymodel | command grep -v -E 'Microsoft\.|System\.'
done
```

Expected (each build): `0 Error(s)`, with `0 Warning(s)` for the driver and ClientConformance and `1 Warning(s)` for the Sample: its existing CS8619 at `Program.cs:41` (`Task.FromResult(actingUserToken)` is a `Task<string?>`), which predates this task and has nothing to do with IdentityModel. The three test runs: `Passed!  - Failed: 0, Passed: <n>, Skipped: 0` for `Iverson.Client.Core.Tests.dll`, `Iverson.LoadTest.Tests.dll` and `Iverson.ClientConformance.Tests.dll`. At `1b108e37` with only this task applied, `<n>` is 84, 136 and 650. Tasks 2–3 add tests to these suites, so only `Failed: 0` gates. The package listing prints `> Duende.IdentityModel      8.1.0      8.1.0` for Client.Core (top-level) and `> Duende.IdentityModel      8.1.0` under the transitive packages of ClientConformance, and no line for the legacy `IdentityModel`.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj Iverson.Clients/DotNet/Iverson.Client.Core/CachedClientCredentialsTokenProvider.cs Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs Iverson.Server/Iverson.ClientConformance/TokenBroker.cs
git status --short | command grep -v '^M  ' || true
git commit -q -m "move the .net clients from IdentityModel to Duende.IdentityModel

$TRAILER" && git show --stat --format=%s HEAD | tail -2
```

Expected: the status filter prints nothing; the commit summary reads `6 files changed, 6 insertions(+), 6 deletions(-)`.

### Task 7: Python lockfiles and the pip-audit jobs (#19, spec §3 "Python lockfiles", "GitHub workflows", "GitLab")

**Files:**
- Create: `Iverson.Clients/Python/requirements.in`, `Iverson.Clients/Python/requirements.txt`
- Create: `Iverson.Agents/Python/requirements.in`, `Iverson.Agents/Python/requirements.txt`
- Modify: `.github/workflows/dependency-scan.yml` (the `pip-audit-python-sdk` and `pip-audit-agents` jobs only; Task 8 edits the `govulncheck` job)
- Modify: `.gitlab-ci.yml` (the `script:` of `pip-audit-python-sdk` and `pip-audit-agents` only; Task 8 edits `govulncheck`, and Task 10 pins every other `image:`, including these two jobs' `python:3.11`)

**Interfaces:**
- Produces the two hashed locks. Dependabot's pip ecosystem already covers both directories (`.github/dependabot.yml`); the `requirements.in` beside each lock is what makes it treat the `.txt` as pip-compile output (spec verified assumption 16).
- Produces the scratch tool venv `$SCR/piptools` (pip 26.2.1, pip-tools 7.6.2) and `$SCR/get-pip.py`. Step 6 deletes them; nothing later uses them.

The locks are compiled by pip-tools **7.6.2** on pip **26.2.1** (the current releases). The session's only interpreter is the system Python 3.14, which has no `ensurepip`, so the tool venv is created `--without-pip` and bootstrapped with `get-pip.py`. pip-compile resolves for the interpreter it runs on; Step 3 checks that the 3.14-compiled locks are complete and installable for CI's Python 3.11 (spec verified assumptions 15 and 37).

How the jobs audit: the job's interpreter gets the hashed lock and then the package itself with `--no-deps`, and `pip-audit` runs from its own venv with `PIPAPI_PYTHON_LOCATION` pointing at that interpreter, so it audits the installed lock (plus the interpreter's own `pip`/`setuptools`) and not its own dependencies. The GitHub jobs keep their `setuptools` line, now pinned to the version the client lock carries: Python 3.11's bundled setuptools is 79.0.1 (`Lib/ensurepip/__init__.py` at `v3.11.17`), which has two published advisories (OSV), and the audit would report it in the agent job, whose lock has no setuptools. The GitLab `python:3.11` image (Python 3.11.17) carries pip 24.0 and setuptools 79.0.1, both with published advisories, which an interpreter audit reports (today's GitLab jobs audit the same interpreter, so they are red already). Both GitLab jobs therefore start with `pip install pip==26.2.1 setuptools==84.0.0` (user decision 2026-10-12), the current releases, which have none.

- [ ] **Step 1: The pip-compile tool venv.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
curl -fsSLo $SCR/get-pip.py https://bootstrap.pypa.io/get-pip.py
python3 -m venv --without-pip $SCR/piptools
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=2G sh -c '$SCR/piptools/bin/python $SCR/get-pip.py -q pip==26.2.1 && $SCR/piptools/bin/pip install -q pip-tools==7.6.2'"
$SCR/piptools/bin/pip --version | cut -d' ' -f1-2; $SCR/piptools/bin/pip-compile --version
```

Expected: `pip 26.2.1` and `pip-compile, version 7.6.2`.

- [ ] **Step 2: The two `requirements.in` files.**

Create `Iverson.Clients/Python/requirements.in`:

```text
# Inputs for requirements.txt, the hashed lock the CI pip-audit jobs install with --require-hashes.
# Mirrors pyproject.toml: [project].dependencies, then the dev extra; keep the two in step.
# Regenerate from this directory with:
#   pip-compile --generate-hashes --allow-unsafe --output-file=requirements.txt requirements.in
# --allow-unsafe keeps setuptools, which grpcio-tools needs, in the hashed set.
grpcio>=1.81.1
protobuf>=5.29.0
pytest>=8.0
grpcio-tools>=1.81.1
```

Create `Iverson.Agents/Python/requirements.in`:

```text
# Inputs for requirements.txt, the hashed lock the CI pip-audit jobs install with --require-hashes.
# Mirrors pyproject.toml: [project].dependencies, then the dev extra; keep the two in step.
# iverson_client is imported by path, as pyproject.toml explains, so it is not listed here.
# Regenerate from this directory with:
#   pip-compile --generate-hashes --allow-unsafe --output-file=requirements.txt requirements.in
anthropic>=1.0,<2
pydantic>=2,<3
grpcio>=1.81.1
protobuf>=5.29.0
pytest>=8
```

- [ ] **Step 3: Compile the locks and check them against Python 3.11.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
for d in Iverson.Clients/Python Iverson.Agents/Python; do
  (cd $WT/$d && flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=2G $SCR/piptools/bin/pip-compile -q --generate-hashes --allow-unsafe --output-file=requirements.txt requirements.in")
  echo "$d: $(sed -n 2p $WT/$d/requirements.txt) | $(sed -n 5p $WT/$d/requirements.txt)"
  echo "  $(command grep -E '^[a-z]' $WT/$d/requirements.txt | cut -d' ' -f1 | tr '\n' ' ')"
  # 3.11 view of the lock: wheels and Requires-Python as CI's interpreter sees them, every hash checked
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=2G $SCR/piptools/bin/pip install -q --dry-run --ignore-installed --python-version 3.11 --only-binary=:all: --require-hashes -r $WT/$d/requirements.txt --report $SCR/py311-${d%%/*}.json"
done
# Completeness for 3.11: every dependency a locked package declares, with its markers evaluated for
# Python 3.11 on Linux, is itself in the lock (pip-compile on 3.14 omits dependencies whose markers are false on 3.14).
$SCR/piptools/bin/python - "$SCR" <<'PYEOF'
import json, sys
from packaging.requirements import Requirement
from packaging.utils import canonicalize_name as cn
env = {"python_version": "3.11", "python_full_version": "3.11.14", "sys_platform": "linux",
       "platform_system": "Linux", "os_name": "posix", "implementation_name": "cpython",
       "platform_python_implementation": "CPython", "platform_machine": "x86_64", "extra": ""}
for name in ("Iverson.Clients", "Iverson.Agents"):
    items = json.load(open(f"{sys.argv[1]}/py311-{name}.json"))["install"]
    locked = {cn(i["metadata"]["name"]) for i in items}
    missing = {f'{i["metadata"]["name"]} -> {r.name}'
               for i in items for r in map(Requirement, i["metadata"].get("requires_dist") or [])
               if (r.marker is None or r.marker.evaluate(env)) and cn(r.name) not in locked}
    print(name, len(locked), "packages, 3.11 wheels for all; missing on 3.11:", sorted(missing) or "none")
PYEOF
```

Expected (versions are today's releases; a later run may resolve newer ones, which is fine as long as the checks hold):

```text
Iverson.Clients/Python: # This file is autogenerated by pip-compile with Python 3.14 | #    pip-compile --allow-unsafe --generate-hashes --output-file=requirements.txt requirements.in
  grpcio==1.84.0 grpcio-tools==1.84.0 iniconfig==2.3.1 packaging==26.3 pluggy==1.6.0 protobuf==7.36.2 pygments==2.21.0 pytest==9.1.1 typing-extensions==4.16.0 setuptools==84.0.0
Iverson.Agents/Python: # This file is autogenerated by pip-compile with Python 3.14 | #    pip-compile --allow-unsafe --generate-hashes --output-file=requirements.txt requirements.in
  annotated-types==0.8.0 anthropic==1.13.0 anyio==4.15.1 docstring-parser==0.18.0 grpcio==1.84.0 h11==0.16.0 httpcore2==2.13.1 httpx2==2.13.1 idna==3.20 iniconfig==2.3.1 jiter==0.17.0 packaging==26.3 pluggy==1.6.0 protobuf==7.36.2 pydantic==2.14.1 pydantic-core==2.50.1 pygments==2.21.0 pytest==9.1.1 sniffio==1.3.1 truststore==0.10.5 typing-extensions==4.16.0 typing-inspection==0.4.4
Iverson.Clients 10 packages, 3.11 wheels for all; missing on 3.11: none
Iverson.Agents 22 packages, 3.11 wheels for all; missing on 3.11: none
```

Each pip-compile run also prints `WARNING: --strip-extras is becoming the default in version 8.0.0. …`; the spec's command is kept as is (no requirement here carries an extra, so both behaviours give the same lock). The dry run fails, rather than printing, if any pin lacks a 3.11-compatible file or a hash; both checks were mutation-tested at plan time (the same dry run with `--python-version 3.9` fails on `annotated-types==0.8.0`, and the marker check with `3.10` reports `pytest -> exceptiongroup`, `pytest -> tomli` and `anyio -> exceptiongroup`). The header's `--output-file=requirements.txt` is what Dependabot keys on.

- [ ] **Step 4: Install each lock with hashes and run the suites.**

The same sequence CI runs (hashed lock, then the package with `--no-deps`; the agent's venv gets the client on its path through a `.pth`, as the repo's `.venv` does), on the 3.14 interpreter, followed by the audit exactly as the jobs run it.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
python3 -m venv --without-pip $SCR/pip-audit
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=2G sh -c '$SCR/pip-audit/bin/python $SCR/get-pip.py -q pip==26.2.1 && $SCR/pip-audit/bin/pip install -q pip-audit==2.10.1'"
for d in Iverson.Clients/Python Iverson.Agents/Python; do
  v=$SCR/venv-${d%%/*}
  python3 -m venv --without-pip $v
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=3G sh -c 'cd $WT/$d && $v/bin/python $SCR/get-pip.py -q pip==26.2.1 && $v/bin/pip install -q --require-hashes -r requirements.txt && $v/bin/pip install -q --no-deps .'"
  [ $d = Iverson.Agents/Python ] && echo "$WT/Iverson.Clients/Python" > "$($v/bin/python -c 'import sysconfig; print(sysconfig.get_path("purelib"))')/iverson_client.pth"
  echo "$d: $($v/bin/pip check)"
  (cd $WT/$d && flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=3G $v/bin/python -m pytest -q -p no:cacheprovider" 2>&1 | tail -1)
  PIPAPI_PYTHON_LOCATION=$v/bin/python $SCR/pip-audit/bin/pip-audit 2>&1; echo "audit-exit=$?"
done
git -C $WT status --short
```

Expected:

```text
Iverson.Clients/Python: No broken requirements found.
241 passed in <t>s
No known vulnerabilities found
Name           Skip Reason
-------------- -----------------------------------------------------------------------------
iverson-client Dependency not found on PyPI and could not be audited: iverson-client (0.1.0)
audit-exit=0
Iverson.Agents/Python: No broken requirements found.
68 passed in <t>s
No known vulnerabilities found
Name           Skip Reason
-------------- -----------------------------------------------------------------------------
iverson-agent  Dependency not found on PyPI and could not be audited: iverson-agent (0.1.0)
iverson-client Dependency not found on PyPI and could not be audited: iverson-client (0.1.0)
audit-exit=0
?? Iverson.Agents/Python/requirements.in
?? Iverson.Agents/Python/requirements.txt
?? Iverson.Clients/Python/requirements.in
?? Iverson.Clients/Python/requirements.txt
```

The skip lines are the projects' own packages, which are not on PyPI; they show the audit read the venv `PIPAPI_PYTHON_LOCATION` names, not pip-audit's own. (`iverson-client` appears in the agent's list only here, because the client's `pip install .` just left `iverson_client.egg-info` in the directory the `.pth` adds; in CI the agent job builds no client, so that line is absent.) At plan time the same audit pointed at a venv holding pip 24.0 and setuptools 79.0.1 reported `Found 14 known vulnerabilities in 2 packages` and exited 1, so a vulnerable package in the audited interpreter does fail the job. The `build/` and `*.egg-info/` directories that `pip install .` leaves are gitignored. The suite counts are those at `1b108e37`; Tasks 2 and 4 add Python tests before this task, so only `0 failed` (no `failed` in the summary) gates.

- [ ] **Step 5: The CI jobs.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old[:80], s.count(old))
    open(path, "w").write(s.replace(old, new))

INSTALL_GH = '''      - run: pip install setuptools==84.0.0
      # requirements.txt is the hashed pip-compile lock of requirements.in and holds every dependency, hence --no-deps.
      - run: pip install --require-hashes -r requirements.txt
      - run: pip install --no-deps .
'''
AUDIT_GH = '''      # pip-audit runs from its own venv, so its own dependencies are not what it audits;
      # PIPAPI_PYTHON_LOCATION points it at the interpreter that holds the lock.
      - run: python -m venv "$RUNNER_TEMP/pip-audit" && "$RUNNER_TEMP/pip-audit/bin/pip" install pip-audit==2.10.1
      - run: PIPAPI_PYTHON_LOCATION="$(command -v python)" "$RUNNER_TEMP/pip-audit/bin/pip-audit"
'''
PTH_GH = '''      # The agent imports iverson_client by path (pyproject.toml), as the repo's own venv does.
      - run: echo "$GITHUB_WORKSPACE/Iverson.Clients/Python" > "$(python -c 'import sysconfig; print(sysconfig.get_path("purelib"))')/iverson_client.pth"
'''
OLD_GH = '''      - run: pip install --upgrade setuptools
      - run: pip install .[dev] pip-audit
      - run: pip-audit
'''
gh = ".github/workflows/dependency-scan.yml"
sub(gh, '''        working-directory: Iverson.Clients/Python
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97 # v7.0.0
        with:
          python-version: "3.11"
''' + OLD_GH, '''        working-directory: Iverson.Clients/Python
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97 # v7.0.0
        with:
          python-version: "3.11"
''' + INSTALL_GH + AUDIT_GH)
sub(gh, '''        working-directory: Iverson.Agents/Python
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97 # v7.0.0
        with:
          python-version: "3.11"
''' + OLD_GH, '''        working-directory: Iverson.Agents/Python
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97 # v7.0.0
        with:
          python-version: "3.11"
''' + INSTALL_GH + PTH_GH + AUDIT_GH)

INSTALL_GL = '''    # the image's own pip 24.0 and setuptools 79.0.1 have published advisories, and the audit reads this interpreter
    - pip install pip==26.2.1 setuptools==84.0.0
    - pip install --require-hashes -r requirements.txt
    - pip install --no-deps .
'''
AUDIT_GL = '''    # pip-audit runs from its own venv and audits the interpreter that holds the lock
    - python -m venv /tmp/pip-audit && /tmp/pip-audit/bin/pip install pip-audit==2.10.1
    - PIPAPI_PYTHON_LOCATION="$(command -v python)" /tmp/pip-audit/bin/pip-audit
'''
gl = ".gitlab-ci.yml"
sub(gl, '''    - cd Iverson.Clients/Python
    - pip install .[dev] pip-audit
    - pip-audit
''', '''    - cd Iverson.Clients/Python
''' + INSTALL_GL + AUDIT_GL)
sub(gl, '''    - cd Iverson.Agents/Python
    - pip install .[dev] pip-audit
    - pip-audit
''', '''    - cd Iverson.Agents/Python
''' + INSTALL_GL + '''    # the agent imports iverson_client by path (pyproject.toml), as the repo's own venv does
    - echo "$CI_PROJECT_DIR/Iverson.Clients/Python" > "$(python -c 'import sysconfig; print(sysconfig.get_path("purelib"))')/iverson_client.pth"
''' + AUDIT_GL)
print("ci ok")
PYEOF
python3 - <<'PYEOF'
import yaml
gh = yaml.safe_load(open(".github/workflows/dependency-scan.yml"))
for j in ("pip-audit-python-sdk", "pip-audit-agents"):
    print(j, [s.get("run", s.get("uses", "")).split("@")[0] for s in gh["jobs"][j]["steps"]])
gl = yaml.safe_load(open(".gitlab-ci.yml"))
for j in ("pip-audit-python-sdk", "pip-audit-agents"):
    print(j, gl[j]["image"], gl[j]["script"][:2])
PYEOF
command grep -n -E 'pip install|pip-audit' .github/workflows/dependency-scan.yml .gitlab-ci.yml | command grep -v -E '==|--require-hashes|--no-deps|PIPAPI|pip-audit-(python-sdk|agents):|# ' || echo "no unpinned pip install left"
```

Expected: `ci ok`, then each GitHub job's steps end `pip install setuptools==84.0.0`, `pip install --require-hashes -r requirements.txt`, `pip install --no-deps .`, (agent only) the `.pth` echo, the pip-audit venv, the audit; the GitLab jobs keep `image: python:3.11` (Task 10 pins it) and each script starts `['cd Iverson.Clients/Python', 'pip install pip==26.2.1 setuptools==84.0.0']` (client) or `['cd Iverson.Agents/Python', 'pip install pip==26.2.1 setuptools==84.0.0']` (agent); `no unpinned pip install left`.

- [ ] **Step 6: Lint the workflow and clean up.**

`actionlint` is not installed in this session, so the step fetches the v1.7.12 release binary into `$SCR/bin` (checksum pinned) unless an earlier task already did.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
[ -x $SCR/bin/actionlint ] || (mkdir -p $SCR/bin/dl && cd $SCR/bin/dl && curl -fsSLO https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz \
  && echo "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  actionlint_1.7.12_linux_amd64.tar.gz" | sha256sum -c --quiet - \
  && tar xzf actionlint_1.7.12_linux_amd64.tar.gz -C $SCR/bin actionlint)
$SCR/bin/actionlint -version | head -1
$SCR/bin/actionlint -no-color .github/workflows/dependency-scan.yml && echo "actionlint clean"
chmod -R u+w $SCR/piptools $SCR/pip-audit $SCR/venv-Iverson.Clients $SCR/venv-Iverson.Agents; rm -rf $SCR/piptools $SCR/pip-audit $SCR/venv-Iverson.Clients $SCR/venv-Iverson.Agents $SCR/get-pip.py $SCR/py311-*.json
```

Expected: `1.7.12`, then `actionlint clean`.

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Clients/Python/requirements.in Iverson.Clients/Python/requirements.txt Iverson.Agents/Python/requirements.in Iverson.Agents/Python/requirements.txt .github/workflows/dependency-scan.yml .gitlab-ci.yml
git status --short | command grep -v -E '^(A |M )' || true
git commit -q -m "lock the python client and agent with hashed pip-compile requirements and pin the pip-audit jobs

$TRAILER" && git show --stat --format=%s HEAD | tail -1
```

Expected: the status filter prints nothing; `6 files changed, 654 insertions(+), 10 deletions(-)` (the two locks are most of it; their size moves with the resolved versions).

### Task 8: Go SDK on Go 1.26 (#19, spec §4 "govulncheck")

**Files:**
- Modify: `Iverson.Clients/Go/go.mod`, `Iverson.Clients/Go/go.sum`
- Modify: `.github/workflows/dependency-scan.yml` (the `govulncheck` job only; Task 7 edits the pip-audit jobs)
- Modify: `.gitlab-ci.yml` (the `govulncheck` job only; Task 7 edits the pip-audit scripts and Task 10 pins the other images)
- Modify: `README.md` (the client-libraries line)

**Interfaces:**
- Produces `golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89` on the GitLab `govulncheck` job. Task 10 pins every other `image:` and leaves this one as it is.

The GitHub job already installs `govulncheck@v1.7.0` after `setup-go`; its Go version moves to `1.26` with `check-latest: true`, because setup-go sets `GOTOOLCHAIN=local` (so `go.mod`'s `toolchain` line is ignored) and otherwise uses the runner image's cached 1.26 patch, which can be older than 1.26.9 and still carry the stdlib advisories; GitLab moves from `@latest` to the same v1.7.0. The local `go` is 1.22 with `GOTOOLCHAIN=auto`, so it downloads and runs the toolchain `go.mod` names. Every Go command here keeps its module and build caches and the downloaded toolchain under `$SCR/go`, and Step 4 deletes them.

- [ ] **Step 1: Move the module to Go 1.26 and `golang.org/x/net` v0.60.0.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Go
export GOPATH=$SCR/go GOMODCACHE=$SCR/go/mod GOCACHE=$SCR/go/cache GOFLAGS=-modcacherw
GOTOOLCHAIN=go1.26.9 go mod edit -go=1.26.0 -toolchain=go1.26.9
GOTOOLCHAIN=auto go get golang.org/x/net@v0.60.0
GOTOOLCHAIN=auto go mod tidy
cat go.mod; git diff --stat -- .
```

Expected: `go: downloading go1.26.9 (linux/amd64)`, then `go: upgraded golang.org/x/net v0.58.0 => v0.60.0`, `go: upgraded golang.org/x/sys v0.47.0 => v0.48.0` and `go: upgraded golang.org/x/text v0.41.0 => v0.42.0` (plus `go: downloading …` lines), and `go.mod` reads:

```text
module github.com/iverson/clients/go

go 1.26.0

toolchain go1.26.9

require (
	google.golang.org/grpc v1.84.0
	google.golang.org/protobuf v1.36.12
)

require (
	golang.org/x/net v0.60.0 // indirect
	golang.org/x/sys v0.48.0 // indirect
	golang.org/x/text v0.42.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20260706201446-f0a921348800 // indirect
)
```

The stat reads `2 files changed, 12 insertions(+), 10 deletions(-)` (`go.mod` and `go.sum` only; `go mod tidy` changes nothing beyond `go get`).

- [ ] **Step 2: Build, vet, test and scan on the declared toolchain.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Go
export GOPATH=$SCR/go GOMODCACHE=$SCR/go/mod GOCACHE=$SCR/go/cache GOFLAGS=-modcacherw GOTOOLCHAIN=auto GOBIN=$SCR/go/bin
go version
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=3G sh -c 'go build ./... && go vet ./... && go test ./... 2>&1 | grep -v no.test.files'"
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=3G go install golang.org/x/vuln/cmd/govulncheck@v1.7.0"
$SCR/go/bin/govulncheck ./... | tail -2
```

Expected: `go version go1.26.9 linux/amd64` (the toolchain line selects it here; on GitHub `check-latest: true` selects the newest 1.26 release, and the GitLab image is 1.26.9); `go build` and `go vet` print nothing; `go test` prints `ok` for `github.com/iverson/clients/go/conformance`, `github.com/iverson/clients/go/iverson` and `github.com/iverson/clients/go/iverson_test` and no `FAIL`; `go install` prints `go: golang.org/x/vuln@v1.7.0 requires go >= 1.25.0; switching to go1.26.9` (an `@version` install ignores the module's `go.mod`, so the local 1.22 switches up) and some `go: downloading` lines; govulncheck ends with `No vulnerabilities found.` The same govulncheck run on `main`'s `go.mod` reports the stdlib and `x/net` advisories (spec verified assumptions 14 and 35).

- [ ] **Step 3: CI and README.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old[:80], s.count(old))
    open(path, "w").write(s.replace(old, new))

sub(".github/workflows/dependency-scan.yml", '''      - uses: actions/setup-go@b7ad1dad31e06c5925ef5d2fc7ad053ef454303e # v7.0.0
        with:
          go-version: "1.25"
''', '''      - uses: actions/setup-go@b7ad1dad31e06c5925ef5d2fc7ad053ef454303e # v7.0.0
        with:
          go-version: "1.26"
          # the runner image can cache an older 1.26 patch; resolve "1.26" to the newest release instead
          check-latest: true
''')
sub(".gitlab-ci.yml", '''  image: golang:1.25
  extends: .rules-dependency-scan
  script:
    - cd Iverson.Clients/Go
    - go install golang.org/x/vuln/cmd/govulncheck@latest
''', '''  # Go 1.26.9 with GOTOOLCHAIN=local, which meets go.mod's `toolchain go1.26.9` without a download
  image: golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89
  extends: .rules-dependency-scan
  script:
    - cd Iverson.Clients/Go
    - go install golang.org/x/vuln/cmd/govulncheck@v1.7.0
''')
sub("README.md", "[Go](Iverson.Clients/Go/), [TypeScript](Iverson.Clients/TypeScript/).\n",
    "[Go](Iverson.Clients/Go/), [TypeScript](Iverson.Clients/TypeScript/). "
    "The Go client needs Go 1.26 or later (its `go.mod` declares `go 1.26.0`).\n")
print("ci ok")
PYEOF
python3 -c "import yaml; j = yaml.safe_load(open('.gitlab-ci.yml'))['govulncheck']; print(j['image'], j['script'])"
[ -x $SCR/bin/actionlint ] || (mkdir -p $SCR/bin/dl && cd $SCR/bin/dl && curl -fsSLO https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz \
  && echo "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  actionlint_1.7.12_linux_amd64.tar.gz" | sha256sum -c --quiet - \
  && tar xzf actionlint_1.7.12_linux_amd64.tar.gz -C $SCR/bin actionlint)
$SCR/bin/actionlint -no-color .github/workflows/dependency-scan.yml && echo "actionlint clean"
command grep -n -E 'go-version|golang:|govulncheck@' .github/workflows/dependency-scan.yml .gitlab-ci.yml
```

Expected: `ci ok`; `golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89 ['cd Iverson.Clients/Go', 'go install golang.org/x/vuln/cmd/govulncheck@v1.7.0', 'govulncheck ./...']`; `actionlint clean`; the grep shows `go-version: "1.26"`, the pinned `golang:1.26@sha256:d772…` and `govulncheck@v1.7.0` in both files, and nothing for 1.25 or `@latest`.

- [ ] **Step 4: Remove the Go caches and commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
chmod -R u+w $SCR/go && rm -rf $SCR/go
git add Iverson.Clients/Go/go.mod Iverson.Clients/Go/go.sum .github/workflows/dependency-scan.yml .gitlab-ci.yml README.md
git status --short | command grep -v '^M  ' || true
git commit -q -m "move the go sdk to go 1.26 and x/net v0.60.0 and pin govulncheck on gitlab

$TRAILER" && git show --stat --format=%s HEAD | tail -1
```

Expected: the status filter prints nothing; `5 files changed, 19 insertions(+), 14 deletions(-)`.

### Task 9: The admin console base image and the npm audits (#19, spec §4)

**Files:**
- Modify: `Iverson.AdminUI/package-lock.json`, `Iverson.Clients/TypeScript/package-lock.json`
- Modify: `Iverson.AdminUI/Dockerfile` (the runtime `FROM` and its dated comment)
- Modify: `Iverson.Server/docker-compose.yml` (the `authentik-tls-proxy` image)

`npm audit fix --package-lock-only` moves `source-map-js` from 1.2.1 to 1.2.2 in both lockfiles (spec verified assumption 27), but the session's npm 10.9.2 also deletes the ten `"libc"` arrays a newer npm wrote into the TypeScript lockfile. The step therefore makes the same three-line change to each `node_modules/source-map-js` entry directly, with the version, tarball URL and `sha512` integrity the registry publishes for 1.2.2, and Step 2 proves it with `npm ci` (which checks the integrity) and `npm audit`. The `"source-map-js": "^1.2.1"` ranges in the dependents' entries already admit 1.2.2 and stay.

The new runtime digest is the `1.30.5-alpine` manifest list that carries `tiff 4.7.2-r0` (spec verified assumptions 27 and 36). No image is built or pulled here; Step 3 checks the digest against the registry API.

- [ ] **Step 1: Bump `source-map-js` and the nginx digest.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
python3 - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old[:80], s.count(old))
    open(path, "w").write(s.replace(old, new))

for p in ("Iverson.AdminUI/package-lock.json", "Iverson.Clients/TypeScript/package-lock.json"):
    sub(p, '''    "node_modules/source-map-js": {
      "version": "1.2.1",
      "resolved": "https://registry.npmjs.org/source-map-js/-/source-map-js-1.2.1.tgz",
      "integrity": "sha512-UXWMKhLOwVKb728IUtQPXxfYU+usdybtUrK/8uGE8CQMvrhOpwvzDBwj0QhSL7MQc7vIsISBG8VQ8+IDQxpfQA==",
''', '''    "node_modules/source-map-js": {
      "version": "1.2.2",
      "resolved": "https://registry.npmjs.org/source-map-js/-/source-map-js-1.2.2.tgz",
      "integrity": "sha512-KGj/8Y43x35aZVDtt+J4mK1hoLGHULMYfSkODJNQjNDC3oW1PqPoxMwo0pLUsWM/UEGzON/NxeHywEfNXNP3Vw==",
''')

OLD = "nginx-unprivileged:1.30.5-alpine@sha256:15c994d10d6d78658721c3bcafff14cb281fba2a4bdf9d5ba92c416a472516e3"
NEW = "nginx-unprivileged:1.30.5-alpine@sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86"
sub("Iverson.AdminUI/Dockerfile", '''# platform-specific vs. manifest-list digests as the node stage above. Manifest-list
# digest for docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine as of 2026-10-06.
FROM docker.io/nginxinc/''' + OLD, '''# platform-specific vs. manifest-list digests as the node stage above. Manifest-list
# digest for docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine as of 2026-10-12 (CSR round-11:
# the rebuild that carries tiff 4.7.2-r0, the fix for CVE-2026-4775).
FROM docker.io/nginxinc/''' + NEW)
sub("Iverson.Server/docker-compose.yml", "    image: docker.io/nginxinc/" + OLD + "\n",
    "    image: docker.io/nginxinc/" + NEW + "\n")
print("edits ok")
PYEOF
git diff --numstat
python3 -c "import yaml; print(yaml.safe_load(open('Iverson.Server/docker-compose.yml'))['services']['authentik-tls-proxy']['image'])"
command grep -rn --exclude-dir=node_modules --exclude-dir=docs --exclude-dir=.git --exclude='.env*' 15c994d1 . || echo "old digest gone"
```

Expected: `edits ok`; numstat `3 2` for the Dockerfile, `3 3` for each lockfile, `1 1` for the compose file; the compose file still parses and its `authentik-tls-proxy` image reads `docker.io/nginxinc/nginx-unprivileged:1.30.5-alpine@sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86`; `old digest gone` (the old digest survives only in `docs/` history: specs, plans and reviews). At plan time the admin console lockfile this step writes was byte-identical to the one `npm audit fix --package-lock-only` produced from the same base; in the TypeScript lockfile npm's output differed only by the ten dropped `"libc"` arrays (`3 33` instead of `3 3`).

- [ ] **Step 2: Both packages install, audit clean at `high`, build and test.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
for d in Iverson.AdminUI Iverson.Clients/TypeScript; do
  echo "== $d"
  flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=4G sh -c 'cd $WT/$d && npm ci --no-fund 2>&1 | tail -1 && npm ls source-map-js 2>&1 | grep -o \"source-map-js@[0-9.]*\" | sort -u && npm audit --audit-level=high; echo audit-exit=\$? && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep -E \"Test Files|Tests \"'"
done
git -C $WT status --short
```

Expected:

```text
== Iverson.AdminUI
found 0 vulnerabilities
source-map-js@1.2.2
found 0 vulnerabilities
audit-exit=0
- Adjust chunk size limit for this warning via build.chunkSizeWarningLimit.
✓ built in <t>s
 Test Files  22 passed (22)
      Tests  226 passed (226)
== Iverson.Clients/TypeScript
found 0 vulnerabilities
source-map-js@1.2.2
found 0 vulnerabilities
audit-exit=0
> tsc

 Test Files  10 passed (10)
      Tests  287 passed (287)
 M Iverson.AdminUI/Dockerfile
 M Iverson.AdminUI/package-lock.json
 M Iverson.Clients/TypeScript/package-lock.json
 M Iverson.Server/docker-compose.yml
```

The first `found 0 vulnerabilities` is `npm ci`'s own summary, the second is `npm audit --audit-level=high` (at `main` both packages report one high, in `source-map-js`; spec verified assumption 26). The chunk-size line is Vite's existing advisory warning. `node_modules/` and `dist/` are gitignored. At plan time one full admin console run, on a host at load average ≈10, failed `src/router.test.tsx > router > renders the landing page at / when authenticated, without redirecting away` with `Test timed out in 5000ms`; that file passed alone twice, and the full suite passed in two further runs. This task changes only `source-map-js`, a build-time source-map dependency, so if that timeout recurs, rerun the suite once on a quieter host before treating it as a failure.

- [ ] **Step 3: The new digest is the live `1.30.5-alpine` manifest list (registry API, no pull).**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
T=$(curl -fsS "https://auth.docker.io/token?service=registry.docker.io&scope=repository:nginxinc/nginx-unprivileged:pull" | python3 -c 'import sys, json; print(json.load(sys.stdin)["token"])')
for ref in 1.30.5-alpine sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86; do
  echo "$ref -> $(curl -fsSI -H "Authorization: Bearer $T" -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' https://registry-1.docker.io/v2/nginxinc/nginx-unprivileged/manifests/$ref | command grep -i -E '^(docker-content-digest|content-type):' | tr -d '\r' | sort | tr '\n' ' ')"
done
```

Expected: both lines show `content-type: application/vnd.oci.image.index.v1+json docker-content-digest: sha256:56b8272e43a0525f67aed68d1e0794a712d10e3e45a563e118e9a77c817e6f86`, so the pin is a multi-platform index, as the Dockerfile comment requires. (If upstream has since re-tagged `1.30.5-alpine`, only the first line's digest differs; the pinned digest is what CI builds.)

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.AdminUI/package-lock.json Iverson.Clients/TypeScript/package-lock.json Iverson.AdminUI/Dockerfile Iverson.Server/docker-compose.yml
git status --short | command grep -v '^M  ' || true
git commit -q -m "move the admin console base to the patched nginx digest and source-map-js to 1.2.2

$TRAILER" && git show --stat --format=%s HEAD | tail -1
```

Expected: the status filter prints nothing; `4 files changed, 10 insertions(+), 9 deletions(-)`.

### Task 10: CI hygiene (#19: CodeQL token, GitLab drift, deploy-validate triggers, Launcher image)

**Files:**
- Modify: `.github/workflows/codeql.yml` (checkout `persist-credentials: false`)
- Modify: `.github/workflows/image-scan.yml` (the third-party job's `azure/setup-helm` gets `version: v3.15.0`)
- Modify: `.github/workflows/deploy-validate.yml` (triggers: every PR and pushes to `main`, no `paths:`)
- Modify: `.gitlab-ci.yml` (digest pins for every image except `govulncheck`'s, which Task 8 pins; `alpine/helm:3.15.4`; the OWASP job's `rules:`)
- Modify: `.github/dependabot.yml` (the docker entry loses `/Iverson.Server/Iverson.Launcher`)
- Delete: `Iverson.Server/Iverson.Launcher/Dockerfile`

**Interfaces:**
- Consumes (Tasks 7 and 8): Task 7 rewrote the `script:` of `pip-audit-python-sdk` and `pip-audit-agents` but left their `image: python:3.11` lines alone; this task pins those two lines and nothing else in those jobs. Task 8 already pinned `govulncheck`'s image (`golang:1.26@sha256:d7722066…`), and this task does not touch that job. Step 5 asserts that **every** job image in the file is pinned, so it also checks Task 8's.
- Produces: `image-scan.yml`'s third-party job runs Helm v3.15.0, so Task 11's chart-pin reader runs under that Helm. Task 11 edits the same job's `run:` script, in a region disjoint from this task's edit. Task 11 also adds a helm directory to `dependabot.yml`, in a different entry from the docker entry this task edits.
- Produces: `deploy-validate.yml` reports `helm-lint`, `helm-template-kubeconform`, `terraform-validate (aws|azure|gcp)` and `security-audit` on every PR and on `main`. The branch-protection task (§6) requires all of these except `security-audit`.

**Design notes.**
- **Digests.** Each digest is the registry's index digest for the tag, resolved on 2026-10-12 with an anonymous registry `HEAD` (no pull; Step 1 records the same lookup). Tags move, so a later lookup can return a newer digest. The plan pins the recorded digests, and Step 5 proves that each pinned digest exists in its registry.
- **`alpine/helm:3.15.0` does not exist** (registry 404, spec VA13), so the three GitLab Helm jobs cannot pull their image today. They move to `3.15.4`. GitHub keeps Helm v3.15.0 through `setup-helm`.
- **OWASP rules.** Today the job inherits `.rules-dependency-scan` (merge requests and schedules). It never ran on a `main` push, because that template has no push rule. A job-level `rules:` replaces the inherited ones, so this task drops `extends:` and states both rules explicitly: `merge_request_event` → `when: never`, then `schedule`. The job now runs only in scheduled pipelines. Job rules decide which jobs run, not which variables a pipeline receives (spec VA31). Keeping `NVD_API_KEY` out of MR pipelines therefore still needs the Protected-variable setting that the runbook records (the runbook task, spec §8).
- **deploy-validate under `push`** (spec VA30): the workflow reads no secret and no PR-only context. Step 6 re-proves this on the edited file, so the same jobs run unchanged on a `main` push.

- [ ] **Step 1: Write the digest checker and record today's resolutions.**

Create `$SCR/ci/digest.py` (scratch, not in the repo):

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
mkdir -p $SCR/ci
cat > $SCR/ci/digest.py <<'PY'
# digest.py <image:tag[@sha256:…]>…: the registry's current digest for each tag (anonymous HEAD on the
# manifest, no pull) and, when the reference carries a digest, whether that digest exists there.
import json, sys, urllib.error, urllib.request
ACCEPT = ", ".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json"])
def split(ref):
    ref, _, digest = ref.partition("@")
    name, _, tag = ref.rpartition(":")
    first = name.split("/")[0]
    if "." in first:
        host, repo = first, name[len(first) + 1:]
    else:
        host, repo = "registry-1.docker.io", name if "/" in name else "library/" + name
    return host, repo, tag, digest
def head(host, repo, ref):
    hdrs = {"Accept": ACCEPT}
    if host == "registry-1.docker.io":
        tok = json.load(urllib.request.urlopen(
            f"https://auth.docker.io/token?service=registry.docker.io&scope=repository:{repo}:pull"))["token"]
        hdrs["Authorization"] = f"Bearer {tok}"
    try:
        r = urllib.request.urlopen(urllib.request.Request(
            f"https://{host}/v2/{repo}/manifests/{ref}", method="HEAD", headers=hdrs))
        return r.status, r.headers.get("Docker-Content-Digest")
    except urllib.error.HTTPError as e:
        return e.code, None
failed = False
for ref in sys.argv[1:]:
    host, repo, tag, digest = split(ref)
    st, cur = head(host, repo, tag)
    line = f"{ref.split('@')[0]}: tag {st} {cur}"
    if digest:
        st2, _ = head(host, repo, digest)
        failed |= st2 != 200
        line += f" | pinned {digest[:19]}… {st2} ({'tag still points here' if cur == digest else 'tag has moved since'})"
    print(line)
sys.exit(1 if failed else 0)
PY
python3 -I $SCR/ci/digest.py alpine/helm:3.15.0 alpine/helm:3.15.4 hashicorp/terraform:1.7.5 \
  mcr.microsoft.com/dotnet/sdk:10.0 node:22 maven:3.9-eclipse-temurin-21 python:3.11
```

Expected (2026-10-12; a later run can show a newer digest for a moving tag such as `node:22`, which is expected):

```text
alpine/helm:3.15.0: tag 404 None
alpine/helm:3.15.4: tag 200 sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2
hashicorp/terraform:1.7.5: tag 200 sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e
mcr.microsoft.com/dotnet/sdk:10.0: tag 200 sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
node:22: tag 200 sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8
maven:3.9-eclipse-temurin-21: tag 200 sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
python:3.11: tag 200 sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f
```

- [ ] **Step 2: GitHub workflows.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
python3 -I - <<'PYEOF'
def sub(path, old, new, count=1):
    s = open(path).read()
    assert s.count(old) == count, (path, old, s.count(old))
    open(path, "w").write(s.replace(old, new))

sub(".github/workflows/codeql.yml", '''      - name: Checkout repository
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
''', '''      - name: Checkout repository
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          # CSR round-11 #19: no later step pushes or fetches, so the job token is not left in .git/config.
          persist-credentials: false
''')

sub(".github/workflows/image-scan.yml", '''        uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
''', '''        uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
        with:
          version: v3.15.0
''')

sub(".github/workflows/deploy-validate.yml", '''on:
  pull_request:
    paths:
      - "Iverson.Server/deploy/**"
''', '''# CSR round-11 #19: every pull request and every push to main, with no paths filter. Branch
# protection requires these checks, and a required check whose workflow a paths filter skips never
# reports, which leaves the pull request blocked.
on:
  pull_request: {}
  push:
    branches: [main]
''')
PYEOF
git diff --stat -- .github/workflows
```

Expected: `codeql.yml | 3 +++`, `deploy-validate.yml | 9 ++++++---`, `image-scan.yml | 2 ++`, `3 files changed, 11 insertions(+), 3 deletions(-)`.

- [ ] **Step 3: GitLab pins and the OWASP job's rules.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
python3 -I - <<'PYEOF'
p = ".gitlab-ci.yml"
s = open(p).read()
def sub(old, new, count=1):
    global s
    assert s.count(old) == count, (old, s.count(old))
    s = s.replace(old, new)

# Index digests resolved 2026-10-12 (Step 1). alpine/helm:3.15.0 does not exist; 3.15.4 does.
sub("  image: alpine/helm:3.15.0\n",
    "  image: alpine/helm:3.15.4@sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2\n", 3)
sub("  image: hashicorp/terraform:1.7.5\n",
    "  image: hashicorp/terraform:1.7.5@sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e\n")
sub("  image: mcr.microsoft.com/dotnet/sdk:10.0\n",
    "  image: mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317\n", 2)
sub("  image: node:22\n",
    "  image: node:22@sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8\n", 3)
# the two pip-audit jobs (Task 7 changed only their scripts)
sub("  image: python:3.11\n",
    "  image: python:3.11@sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f\n", 2)

sub('''helm-lint:
  stage: validate
''', '''# CSR round-11 #19: every job image is pinned as name:tag@sha256:<index digest>. The tag keeps the
# reference readable; the digest is what is pulled. A bump changes both together.
helm-lint:
  stage: validate
''')

sub('''owasp-dependency-check-java:
  stage: dependency-scan
  image: maven:3.9-eclipse-temurin-21
  extends: .rules-dependency-scan
''', '''owasp-dependency-check-java:
  stage: dependency-scan
  image: maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
  # CSR round-11 #19: the only job that reads NVD_API_KEY, so it never runs in a merge-request
  # pipeline (these rules replace .rules-dependency-scan's, which also admit merge requests). Job
  # rules decide which jobs run, not which variables a pipeline receives: NVD_API_KEY must also be a
  # Protected (or pipeline-schedule) variable (docs/runbooks/csr11-clients-tooling-ci.md).
  rules:
    - if: '$CI_PIPELINE_SOURCE == "merge_request_event"'
      when: never
    - if: '$CI_PIPELINE_SOURCE == "schedule"'
''')
open(p, "w").write(s)
PYEOF
git diff --stat -- .gitlab-ci.yml
```

Expected: `.gitlab-ci.yml | 35 ++++++++++++++++++++++-------------`, `1 file changed, 22 insertions(+), 13 deletions(-)`.

- [ ] **Step 4: Delete the Launcher image and its Dependabot entry.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
python3 -I - <<'PYEOF'
p = ".github/dependabot.yml"
s = open(p).read()
old = '''      - "/Iverson.Server/Iverson.Launcher"
'''
assert s.count(old) == 1
open(p, "w").write(s.replace(old, ""))
PYEOF
git rm -q Iverson.Server/Iverson.Launcher/Dockerfile
command grep -rn -I --exclude='.env*' --exclude-dir=.git --exclude-dir=docs -e 'Launcher/Dockerfile' -e 'Iverson.Launcher"' . ; echo "references left: $?"
ls Iverson.Server/Iverson.Launcher
```

Expected: no grep match (`references left: 1`, grep's no-match status); the project directory still lists `Iverson.Launcher.csproj` and `Program.cs` (spec §3: the project stays). The only remaining mentions are in `docs/` history (specs, plans, reviews), which are not edited.

- [ ] **Step 5: Validate the workflows and the GitLab file.**

The control and the rules diff in this step compare with `HEAD`, so they are meaningful only before this task's commit; Task 15 does not re-run them.

actionlint is fetched as a pinned, checksum-verified release binary into scratch:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
mkdir -p $SCR/tools/actionlint-dl && cd $SCR/tools/actionlint-dl
[ -x $SCR/tools/actionlint ] || {
  curl -fsSLO https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz
  echo "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  actionlint_1.7.12_linux_amd64.tar.gz" | sha256sum -c -
  tar xzf actionlint_1.7.12_linux_amd64.tar.gz -C $SCR/tools actionlint; }
cd $WT && $SCR/tools/actionlint -no-color .github/workflows/*.yml; echo "actionlint rc=$?"
```

Expected: `actionlint_1.7.12_linux_amd64.tar.gz: OK` on the first run, then only `actionlint rc=0`. (actionlint runs shellcheck on `run:` scripts only when `shellcheck` is on `PATH`; it is not installed here, so the `run:` bodies are checked for expression syntax only.)

Then parse `.gitlab-ci.yml`, assert that every job image is digest-pinned, and check that each pinned digest exists in its registry:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cat > $SCR/ci/gl_images.py <<'PY'
# gl_images.py <.gitlab-ci.yml> <refs-out>: parses the file, prints each job's image, fails unless every
# one is name:tag@sha256:<64 hex>, and writes the distinct references for digest.py.
import re, sys, yaml
doc = yaml.safe_load(open(sys.argv[1]))
pinned = re.compile(r"^[^\s@]+:[^\s@/]+@sha256:[0-9a-f]{64}$")
bad, refs = [], set()
for name, job in doc.items():
    if not isinstance(job, dict) or "image" not in job:
        continue
    img = job["image"]["name"] if isinstance(job["image"], dict) else job["image"]
    print(f"{name}: {img[:img.index('@') + 19] + '…' if '@' in img else img}")
    if pinned.match(img): refs.add(img)
    else: bad.append(name)
print(f"{len(refs)} distinct pinned references; unpinned jobs: {bad or 'none'}")
open(sys.argv[2], "w").write("\n".join(sorted(refs)) + "\n")
sys.exit(1 if bad else 0)
PY
cd $WT && git show HEAD:.gitlab-ci.yml > $SCR/ci/gitlab-before.yml
echo "== control: the file before this task"; python3 -I $SCR/ci/gl_images.py $SCR/ci/gitlab-before.yml /dev/null | tail -1
echo "== after"; python3 -I $SCR/ci/gl_images.py .gitlab-ci.yml $SCR/ci/refs.txt && python3 -I $SCR/ci/digest.py $(cat $SCR/ci/refs.txt)
```

Expected: the control lists the twelve jobs this task pins as unpinned, which shows that the check can fail. After the edit, the file parses, every job is pinned, and every pinned digest returns `200`:

```text
== control: the file before this task
1 distinct pinned references; unpinned jobs: ['helm-lint', 'helm-template-kubeconform', 'security-audit', 'terraform-validate', 'nuget-audit', 'npm-audit-adminui', 'npm-audit-ts-sdk', 'owasp-dependency-check-java', 'pip-audit-python-sdk', 'pip-audit-agents', 'dotnet-build-test', 'typescript-sdk-build-test']
== after
helm-lint: alpine/helm:3.15.4@sha256:105741fa662…
helm-template-kubeconform: alpine/helm:3.15.4@sha256:105741fa662…
security-audit: alpine/helm:3.15.4@sha256:105741fa662…
terraform-validate: hashicorp/terraform:1.7.5@sha256:386b7bff108…
nuget-audit: mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b…
npm-audit-adminui: node:22@sha256:0e5f9065736…
npm-audit-ts-sdk: node:22@sha256:0e5f9065736…
govulncheck: golang:1.26@sha256:d7722066f0b…
owasp-dependency-check-java: maven:3.9-eclipse-temurin-21@sha256:99e61abcff9…
pip-audit-python-sdk: python:3.11@sha256:50cfccc707e…
pip-audit-agents: python:3.11@sha256:50cfccc707e…
dotnet-build-test: mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b…
typescript-sdk-build-test: node:22@sha256:0e5f9065736…
7 distinct pinned references; unpinned jobs: none
alpine/helm:3.15.4: tag 200 sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2 | pinned sha256:105741fa6621… 200 (tag still points here)
golang:1.26: tag 200 sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89 | pinned sha256:d7722066f0b6… 200 (tag still points here)
hashicorp/terraform:1.7.5: tag 200 sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e | pinned sha256:386b7bff108f… 200 (tag still points here)
maven:3.9-eclipse-temurin-21: tag 200 sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320 | pinned sha256:99e61abcff91… 200 (tag still points here)
mcr.microsoft.com/dotnet/sdk:10.0: tag 200 sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 | pinned sha256:e70cdb7f80b0… 200 (tag still points here)
node:22: tag 200 sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8 | pinned sha256:0e5f90657369… 200 (tag still points here)
python:3.11: tag 200 sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f | pinned sha256:50cfccc707e8… 200 (tag still points here)
```

The `golang:1.26` digest is Task 8's pin. A moving tag such as `node:22` can show `(tag has moved since)` instead of `(tag still points here)`. That is fine: the requirement is `200` on every pinned digest. If `govulncheck` is listed under `unpinned jobs:`, Task 8 has not pinned it. Stop and report; do not pin it here.

Then show that only the OWASP job's triggers change. The script evaluates the rule forms this file uses for the three pipeline sources the workflow admits:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cat > $SCR/ci/gl_rules.py <<'PY'
# gl_rules.py <before.yml> <after.yml>: which jobs run for each pipeline source the workflow admits, before
# and after. Evaluates only the forms this file uses: `if:` equality tests joined by &&, `changes:`
# (taken as matched, i.e. the most permissive case) and `when: never`. Job rules replace extended ones.
import re, sys, yaml
SOURCES = {"merge_request_event": {"CI_PIPELINE_SOURCE": "merge_request_event", "CI_COMMIT_BRANCH": ""},
           "push main": {"CI_PIPELINE_SOURCE": "push", "CI_COMMIT_BRANCH": "main"},
           "schedule": {"CI_PIPELINE_SOURCE": "schedule", "CI_COMMIT_BRANCH": "main"}}
def test(expr, env):
    parts = [re.fullmatch(r'\s*\$(\w+) == "([^"]*)"\s*', p) for p in expr.split("&&")]
    assert all(parts), expr
    return all(env[m[1]] == m[2] for m in parts)
def runs(rules, env):
    for r in rules:
        if "if" not in r or test(r["if"], env):
            return r.get("when", "on_success") != "never"
    return False
def table(path):
    doc = yaml.safe_load(open(path))
    jobs = {}
    for name, job in doc.items():
        if name.startswith(".") or not isinstance(job, dict) or "stage" not in job:
            continue
        rules = job["rules"] if "rules" in job else doc[job["extends"]]["rules"]
        jobs[name] = {s: runs(doc["workflow"]["rules"], e) and runs(rules, e) for s, e in SOURCES.items()}
    return jobs
before, after = table(sys.argv[1]), table(sys.argv[2])
assert sorted(before) == sorted(after), "job set changed"
for name in after:
    if before[name] != after[name]:
        print(name, {s: f"{before[name][s]} -> {after[name][s]}" for s in SOURCES if before[name][s] != after[name][s]})
print(f"{sum(before[n] == after[n] for n in after)} of {len(after)} jobs unchanged")
print("owasp-dependency-check-java now runs on:", [s for s, v in after["owasp-dependency-check-java"].items() if v])
PY
cd $WT && python3 -I $SCR/ci/gl_rules.py $SCR/ci/gitlab-before.yml .gitlab-ci.yml
```

Expected:

```text
owasp-dependency-check-java {'merge_request_event': 'True -> False'}
12 of 13 jobs unchanged
owasp-dependency-check-java now runs on: ['schedule']
```

- [ ] **Step 6: Show that deploy-validate's jobs run unchanged on a `main` push (spec VA30).**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
f=.github/workflows/deploy-validate.yml
echo "== PR-only or secret contexts, and job/step conditions:"
command grep -n -E 'secrets\.|github\.(event|head_ref|base_ref)|pull_request|if:|needs:' $f
echo "== every expression in the file:"; command grep -o -E '\$\{\{[^}]*\}\}' $f | sort | uniq -c
python3 -I -c '
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
print("on:", d[True]); print("permissions:", d["permissions"]); print("jobs:", sorted(d["jobs"]))' $f
```

Expected:

```text
== PR-only or secret contexts, and job/step conditions:
7:  pull_request: {}
== every expression in the file:
      2 ${{ matrix.cloud }}
on: {'pull_request': {}, 'push': {'branches': ['main']}}
permissions: {'contents': 'read'}
jobs: ['helm-lint', 'helm-template-kubeconform', 'security-audit', 'terraform-validate']
```

The only match is the trigger key itself. No job or step has an `if:`, nothing reads `secrets`, `github.event` or the PR refs, and the only expression is the matrix value. A `push` run therefore executes exactly the steps a PR run does, with the same read-only token. (PyYAML reads the `on` key as boolean `True`, hence `d[True]`.)

- [ ] **Step 7: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
# The Dockerfile's deletion is already staged by Step 4's `git rm`.
git add .github/workflows/codeql.yml .github/workflows/image-scan.yml .github/workflows/deploy-validate.yml .gitlab-ci.yml .github/dependabot.yml &&
git status --short -- .github .gitlab-ci.yml Iverson.Server/Iverson.Launcher &&
git commit -q -m "pin ci images and helm, run deploy-validate on every pr and main, and drop the launcher image

$TRAILER" && git log -1 --format='%s' && git show --stat --format= HEAD | tail -1
```

Expected: the status lists `M  ` (staged) for the five edited files and `D  Iverson.Server/Iverson.Launcher/Dockerfile`; the commit prints its subject and `6 files changed, 33 insertions(+), 49 deletions(-)`.

### Task 11: Operator chart pins in a Dependabot-tracked stub (#19, spec §3 "Operator charts")

**Files:**
- Create: `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml`
- Modify: `Iverson.Server/deploy/terraform/modules/operators/main.tf` (a `locals` map from the stub; the five remote `helm_release` blocks read `repository` and `version` from it)
- Modify: `.github/dependabot.yml` (the helm entry gains the stub's directory)
- Modify: `.github/workflows/image-scan.yml` (`tf_pin` becomes `chart_pin`, which reads the stub)
- Modify: `Iverson.Server/deploy/kind/setup.sh`, `Iverson.Server/deploy/kind/setup.ps1` (the CNPG, Strimzi and StarRocks operator versions come from the stub)

**Interfaces:**
- Consumes (Task 10): `image-scan.yml`'s third-party job already runs `azure/setup-helm` with `version: v3.15.0`, so `chart_pin`'s `helm dependency list` runs under Helm 3.15. Its output format is the same on 3.15.0, 3.16.4 and 4.3.0 (checked while drafting, see the plan-level assumptions). Task 10 also edited `dependabot.yml`'s docker entry; this task edits the helm entry.
- Produces: the stub `versions/Chart.yaml`, read by four consumers: Terraform (`yamldecode`) and `image-scan.yml`, `setup.sh` and `setup.ps1` (all three through `helm dependency list`).

**Design notes.**
- **Stub format.** Each entry lists `name`, then `version`, then `repository`, with bare (unquoted) versions. Dependabot's helm updater rewrites a pin by matching `- name: <chart>` followed by the next `version:` line, and it writes the new version unquoted (dependabot-core `helm/lib/dependabot/helm/file_updater/chart_updater.rb`, `replace_next_entry_version`). Strimzi's repository keeps its trailing slash (spec §3).
- **Coupled pins.** Two pins must move with something outside the stub: `cluster-autoscaler` with cluster-aws's EKS `kubernetes_version`, and `aws-load-balancer-controller` with the IAM policy file cluster-aws commits (`modules/cluster-aws/files/aws-load-balancer-controller-iam-policy-v2.9.0.json`). Their `main.tf` comments explaining this move into the stub, next to the version a Dependabot PR changes. The old `main.tf` comment's reference to a `data.http.lb_controller_policy` fetch was stale (round 2 replaced the fetch with the committed file, `cluster-aws/main.tf:369-385`), so it is corrected rather than copied.
- **One reader for the three scripts.** The workflow and both kind scripts need the pins but have no YAML parser in common, so all three read the stub through `helm dependency list <dir>`. Helm is present in all three places, and it parses the YAML itself. Its output is a `NAME VERSION REPOSITORY STATUS` table. The kind scripts read the versions before touching the cluster, so a missing pin stops them at the start.
- **`pvc-storageclass-policy`** (local chart) is unchanged.

- [ ] **Step 1: Record today's pins from `main.tf`.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
mkdir -p $SCR/ops
cd $WT && git show main:Iverson.Server/deploy/terraform/modules/operators/main.tf > $SCR/ops/main-before.tf
python3 -I - $SCR/ops/main-before.tf $SCR/ops/before.json <<'PY'
import json, re, sys
src = open(sys.argv[1]).read()
pins = {}
for block in re.findall(r'^resource "helm_release" "\w+" \{\n(.*?)^\}', src, re.M | re.S):
    attrs = dict(re.findall(r'^  (repository|chart|version)\s*=\s*"([^"]*)"', block, re.M))
    if "repository" in attrs:
        pins[attrs["chart"]] = {"repository": attrs["repository"], "version": attrs["version"]}
json.dump(pins, open(sys.argv[2], "w"), sort_keys=True)
print(len(pins), json.dumps(pins, sort_keys=True))
PY
```

Expected:

```text
5 {"aws-load-balancer-controller": {"repository": "https://aws.github.io/eks-charts", "version": "1.9.0"}, "cloudnative-pg": {"repository": "https://cloudnative-pg.github.io/charts", "version": "0.29.0"}, "cluster-autoscaler": {"repository": "https://kubernetes.github.io/autoscaler", "version": "9.37.0"}, "operator": {"repository": "https://starrocks.github.io/starrocks-kubernetes-operator", "version": "1.11.5"}, "strimzi-kafka-operator": {"repository": "https://strimzi.io/charts/", "version": "1.1.0"}}
```

- [ ] **Step 2: Create the stub `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml`.**

```yaml
# CSR round-11 #19: the remote operator charts modules/operators installs, pinned here so Dependabot's
# helm ecosystem proposes their bumps. A dependencies-only stub: nothing renders, packages or deploys
# this chart. Read by modules/operators/main.tf (yamldecode), deploy/kind/setup.sh and setup.ps1, and
# .github/workflows/image-scan.yml (helm dependency list). Keep each entry's order (name, version,
# repository) and the versions unquoted, the form Dependabot rewrites.
apiVersion: v2
name: iverson-operators
description: Version pins for the operator charts modules/operators installs (not deployed)
type: application
version: 0.1.0
dependencies:
  - name: cloudnative-pg
    version: 0.29.0
    repository: https://cloudnative-pg.github.io/charts
  - name: strimzi-kafka-operator
    version: 1.1.0
    repository: https://strimzi.io/charts/
  # The StarRocks operator's chart is published as "operator" in this repository's index.yaml.
  - name: operator
    version: 1.11.5
    repository: https://starrocks.github.io/starrocks-kubernetes-operator
  # AWS only. Its appVersion must match cluster-aws's EKS kubernetes_version (default "1.30");
  # 9.37.0 has appVersion 1.30.0. Bump the two together.
  - name: cluster-autoscaler
    version: 9.37.0
    repository: https://kubernetes.github.io/autoscaler
  # AWS only. 1.9.0 has appVersion v2.9.0, matching the controller IAM policy cluster-aws commits
  # (files/aws-load-balancer-controller-iam-policy-v2.9.0.json). Bump the two together.
  - name: aws-load-balancer-controller
    version: 1.9.0
    repository: https://aws.github.io/eks-charts
```

- [ ] **Step 3: `main.tf` reads the stub.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT/Iverson.Server/deploy/terraform/modules/operators
python3 -I - <<'PYEOF'
p = "main.tf"
s = open(p).read()
def sub(old, new):
    global s
    assert s.count(old) == 1, old
    s = s.replace(old, new)

sub('''# Cloud-profile equivalent of the kind setup scripts' namespace step in the
''', '''# CSR round-11 #19: the remote charts' repositories and versions come from versions/Chart.yaml, a
# dependencies-only stub that Dependabot's helm ecosystem keeps current. deploy/kind/setup.sh,
# setup.ps1 and .github/workflows/image-scan.yml read the same file.
locals {
  operator_charts = { for d in yamldecode(file("${path.module}/versions/Chart.yaml")).dependencies : d.name => d }
}

# Cloud-profile equivalent of the kind setup scripts' namespace step in the
''')
sub('''  name       = "cnpg"
  repository = "https://cloudnative-pg.github.io/charts"
  chart      = "cloudnative-pg"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Verified current stable as of this fix pass.
  version          = "0.29.0"
''', '''  name             = "cnpg"
  repository       = local.operator_charts["cloudnative-pg"].repository
  chart            = "cloudnative-pg"
  version          = local.operator_charts["cloudnative-pg"].version
''')
sub('''  name       = "strimzi"
  repository = "https://strimzi.io/charts/"
  chart      = "strimzi-kafka-operator"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Verified current stable as of this fix pass.
  #
  # No featureGates set: KafkaNodePools and UseKRaft graduated to GA in this
  # pinned operator version and their feature-gate flags were removed
  # entirely — setting them crashes the operator with "Unknown feature gate".
  # Both behaviors are already the default.
  version          = "1.1.0"
''', '''  name       = "strimzi"
  repository = local.operator_charts["strimzi-kafka-operator"].repository
  chart      = "strimzi-kafka-operator"
  # No featureGates set: KafkaNodePools and UseKRaft graduated to GA in this
  # pinned operator version and their feature-gate flags were removed
  # entirely — setting them crashes the operator with "Unknown feature gate".
  # Both behaviors are already the default.
  version          = local.operator_charts["strimzi-kafka-operator"].version
''')
sub('''  repository = "https://starrocks.github.io/starrocks-kubernetes-operator"
  # The chart is published under the key "operator" in this repo's
  # index.yaml, not "kube-starrocks-operator" — confirmed live against
  # https://starrocks.github.io/starrocks-kubernetes-operator/index.yaml.
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time.
  chart            = "operator"
  version          = "1.11.5"
''', '''  repository = local.operator_charts["operator"].repository
  # The chart is published under the key "operator" in this repo's
  # index.yaml, not "kube-starrocks-operator" — confirmed live against
  # https://starrocks.github.io/starrocks-kubernetes-operator/index.yaml.
  chart            = "operator"
  version          = local.operator_charts["operator"].version
''')
sub('''  repository = "https://kubernetes.github.io/autoscaler"
  chart      = "cluster-autoscaler"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Chosen to match this module's pinned EKS
  # kubernetes_version ("1.30"): chart 9.37.0 has appVersion 1.30.0.
  version   = "9.37.0"
''', '''  repository = local.operator_charts["cluster-autoscaler"].repository
  chart      = "cluster-autoscaler"
  # The version must match the EKS kubernetes_version (see the note in versions/Chart.yaml).
  version   = local.operator_charts["cluster-autoscaler"].version
''')
sub('''  repository = "https://aws.github.io/eks-charts"
  chart      = "aws-load-balancer-controller"
  # Pinned deliberately — bump by hand, don't let this float to whatever is
  # newest at apply time. Chart 1.9.0 has appVersion v2.9.0, matching the
  # aws-load-balancer-controller IAM policy version already pinned in
  # cluster-aws's data.http.lb_controller_policy fetch (verified against
  # https://aws.github.io/eks-charts/index.yaml).
  version   = "1.9.0"
''', '''  repository = local.operator_charts["aws-load-balancer-controller"].repository
  chart      = "aws-load-balancer-controller"
  # The version must match cluster-aws's committed IAM policy (see the note in versions/Chart.yaml).
  version   = local.operator_charts["aws-load-balancer-controller"].version
''')
open(p, "w").write(s)
PYEOF
terraform fmt -check -diff main.tf; echo "fmt rc=$?"
command grep -c 'local.operator_charts\[' main.tf; command grep -n -E '^\s*(repository|version)\s*=\s*"' main.tf; echo "string pins left: $?"
```

Expected: `fmt rc=0` with no diff; `10` references to `local.operator_charts[`; no `repository`/`version` string literal left (`string pins left: 1`). The `terraform {}` block's provider constraints use `version = "~> …"` inside `{ … }` on one line, so the line-anchored grep does not see them.

- [ ] **Step 4: Dependabot and the image scan read the stub.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
python3 -I - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old)
    open(path, "w").write(s.replace(old, new))

sub(".github/dependabot.yml", '''      - "/Iverson.Server/deploy/helm/iverson/charts/*"
''', '''      - "/Iverson.Server/deploy/helm/iverson/charts/*"
      - "/Iverson.Server/deploy/terraform/modules/operators/versions"
''')

sub(".github/workflows/image-scan.yml", '''          # The CNPG and Strimzi operators are installed by Terraform; read their chart pins from
          # there, so this list follows a version bump without a second edit.
          TF=Iverson.Server/deploy/terraform/modules/operators/main.tf
          tf_pin() {   # $1 = chart name, $2 = attribute (repository or version)
            awk -v chart="$1" -v attr="$2" '
              /^(resource|data|module|locals|variable|output) / { delete a }
              /^ *[a-z_]+ *= *"/ { k = $1; v = $0; sub(/^[^"]*"/, "", v); sub(/".*$/, "", v); a[k] = v }
              /^}/ { if (a["chart"] == chart) { print a[attr]; exit } }' "$TF"
          }
          render_operator() {   # $1 = chart name; prints the rendered manifests
            local repo version
            repo=$(tf_pin "$1" repository); version=$(tf_pin "$1" version)
            [ -n "$repo" ] && [ -n "$version" ] || { echo "no $1 pin in $TF" >&2; return 1; }
''', '''          # The CNPG and Strimzi operators are installed by Terraform from the pins in the operators
          # module's stub Chart.yaml (Dependabot-tracked); read the same pins, so this list follows a
          # version bump without a second edit.
          PINS=Iverson.Server/deploy/terraform/modules/operators/versions
          chart_pin() {   # $1 = chart name, $2 = column of `helm dependency list` (2 = version, 3 = repository)
            helm dependency list "$PINS" | awk -v chart="$1" -v col="$2" '$1 == chart && !n++ { print $col }'
          }
          render_operator() {   # $1 = chart name; prints the rendered manifests
            local repo version
            repo=$(chart_pin "$1" 3); version=$(chart_pin "$1" 2)
            [ -n "$repo" ] && [ -n "$version" ] || { echo "no $1 pin in $PINS/Chart.yaml" >&2; return 1; }
''')
PYEOF
git diff --stat -- .github
```

Expected: `.github/dependabot.yml | 1 +`, `.github/workflows/image-scan.yml | 18 ++++++++----------`, `2 files changed, 9 insertions(+), 10 deletions(-)`.

- [ ] **Step 5: The kind scripts read the stub.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT/Iverson.Server/deploy/kind
python3 -I - <<'PYEOF'
def sub(path, old, new):
    s = open(path).read()
    assert s.count(old) == 1, (path, old)
    open(path, "w").write(s.replace(old, new))

# ---- setup.sh
sub("setup.sh", '''echo "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
''', '''# CSR round-11 #19: the CNPG, Strimzi and StarRocks operator chart versions come from the stub
# Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
# Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
# read before anything touches the cluster, so a missing pin stops the script here.
OPERATOR_PINS="$(cd "$(dirname "${BASH_SOURCE[0]}")/../terraform/modules/operators/versions" && pwd)"
operator_version() {   # $1 = chart name in $OPERATOR_PINS/Chart.yaml; prints its version
  helm dependency list "$OPERATOR_PINS" | awk -v chart="$1" '$1 == chart && !n++ { print $2 } END { exit !n }' ||
    { echo "no $1 pin in $OPERATOR_PINS/Chart.yaml" >&2; return 1; }
}
CNPG_VERSION=$(operator_version cloudnative-pg)
STRIMZI_VERSION=$(operator_version strimzi-kafka-operator)
STARROCKS_OPERATOR_VERSION=$(operator_version operator)

echo "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
''')
sub("setup.sh", '''# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's cloudnative_pg helm_release, so kind and the
# cloud Terraform installs run the identical operator build.
helm upgrade --install cnpg cloudnative-pg \\
  --repo https://cloudnative-pg.github.io/charts \\
  --version 0.29.0 \\
''', '''# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# cloudnative_pg helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install cnpg cloudnative-pg \\
  --repo https://cloudnative-pg.github.io/charts \\
  --version "$CNPG_VERSION" \\
''')
sub("setup.sh", '''# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's strimzi helm_release, so kind and the cloud
# Terraform installs run the identical operator build.
helm upgrade --install strimzi strimzi-kafka-operator \\
  --repo https://strimzi.io/charts/ \\
  --version 1.1.0 \\
''', '''# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# strimzi helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install strimzi strimzi-kafka-operator \\
  --repo https://strimzi.io/charts/ \\
  --version "$STRIMZI_VERSION" \\
''')
sub("setup.sh", '''# CSR round-2 finding #12: pinned to the same version as
# modules/operators/main.tf's starrocks_operator helm_release, so kind and
# the cloud Terraform installs run the identical operator build.
helm upgrade --install starrocks-operator operator \\
  --repo https://starrocks.github.io/starrocks-kubernetes-operator \\
  --version 1.11.5 \\
''', '''# CSR round-2 finding #12: the same version as modules/operators/main.tf's
# starrocks_operator helm_release (both read versions/Chart.yaml, see the top).
helm upgrade --install starrocks-operator operator \\
  --repo https://starrocks.github.io/starrocks-kubernetes-operator \\
  --version "$STARROCKS_OPERATOR_VERSION" \\
''')

# ---- setup.ps1
sub("setup.ps1", '''Write-Host "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
''', '''# CSR round-11 #19: the CNPG, Strimzi and StarRocks operator chart versions come from the stub
# Chart.yaml the cloud Terraform installs read (terraform/modules/operators/versions), which
# Dependabot keeps current, so kind and the cloud installs run the same operator builds. They are
# read before anything touches the cluster, so a missing pin stops the script here.
$OperatorPins = Join-Path $PSScriptRoot "../terraform/modules/operators/versions"
function Get-OperatorVersion([string]$Chart) {
  foreach ($line in (helm dependency list $OperatorPins)) {
    $cols = "$line".Trim() -split '\\s+'
    if ($cols[0] -eq $Chart) { return $cols[1] }
  }
  throw "no $Chart pin in $(Join-Path $OperatorPins 'Chart.yaml')"
}
$CnpgVersion = Get-OperatorVersion "cloudnative-pg"
$StrimziVersion = Get-OperatorVersion "strimzi-kafka-operator"
$StarRocksOperatorVersion = Get-OperatorVersion "operator"

Write-Host "Installing Calico (kind's default CNI, kindnet, does not enforce NetworkPolicy)..."
''')
sub("setup.ps1", '''  --repo https://cloudnative-pg.github.io/charts `
  --version 0.29.0 `
''', '''  --repo https://cloudnative-pg.github.io/charts `
  --version $CnpgVersion `
''')
sub("setup.ps1", '''  --repo https://strimzi.io/charts/ `
  --version 1.1.0 `
''', '''  --repo https://strimzi.io/charts/ `
  --version $StrimziVersion `
''')
sub("setup.ps1", '''  --repo https://starrocks.github.io/starrocks-kubernetes-operator `
  --version 1.11.5 `
''', '''  --repo https://starrocks.github.io/starrocks-kubernetes-operator `
  --version $StarRocksOperatorVersion `
''')
PYEOF
bash -n setup.sh; echo "bash -n rc=$?"
command grep -n -E -- '--version (0\.29\.0|1\.1\.0|1\.11\.5)' setup.sh setup.ps1; echo "literal operator versions left: $?"
git diff --stat -- .
```

Expected: `bash -n rc=0`; `literal operator versions left: 1` (none); `setup.ps1 | 22 +++++++++++++++++++---`, `setup.sh | 34 ++++++++++++++++++++++------------`, `2 files changed, 41 insertions(+), 15 deletions(-)`.

- [ ] **Step 6: Terraform — fmt, validate the module and the three roots, tfsec, and the decoded values.**

Terraform runs on a copy in scratch, so `init` writes nothing into the worktree. Each root's providers are deleted after its validate, so the tmpfs holds one root's providers at a time:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT/Iverson.Server/deploy/terraform && terraform fmt -check -recursive . ; echo "fmt rc=$?"
rm -rf $SCR/tf && mkdir -p $SCR/tf && cp -r $WT/Iverson.Server/deploy/terraform $SCR/tf/
export TF_PLUGIN_CACHE_DIR=$SCR/tf/plugin-cache
for d in modules/operators aws azure gcp; do
  mkdir -p $TF_PLUGIN_CACHE_DIR
  lock=; [ $d != modules/operators ] && lock=-lockfile=readonly
  echo "== $d: $(systemd-run --user --scope -q -p MemoryMax=3G bash -c "cd $SCR/tf/terraform/$d && terraform init -backend=false -input=false $lock >/dev/null && terraform validate -no-color" | head -1)"
  if [ $d = modules/operators ]; then
    (cd $SCR/tf/terraform/$d && echo 'jsonencode({ for k, v in local.operator_charts : k => { repository = v.repository, version = v.version } })' \
      | terraform console -var cloud=aws -var cluster_name=x -var 'storage_class_config={provisioner="x",parameters={}}' \
      | python3 -I -c 'import json, sys; got = json.loads(json.loads(sys.stdin.read())); want = json.load(open(sys.argv[1])); print("decoded == main.tf literals on main:", got == want, len(got))' $SCR/ops/before.json)
  fi
  rm -rf $SCR/tf/terraform/$d/.terraform $TF_PLUGIN_CACHE_DIR
done
tfsec $SCR/tf/terraform --no-colour 2>&1 | tail -2
rm -rf $SCR/tf
```

Expected:

```text
fmt rc=0
== modules/operators: Success! The configuration is valid.
decoded == main.tf literals on main: True 5
== aws: Success! The configuration is valid.
== azure: Success! The configuration is valid.
== gcp: Success! The configuration is valid.
No problems detected!

```

The step downloads each root's providers and takes about two and a half minutes.

(`terraform console` prints the `jsonencode` result as a quoted HCL string, hence the double `json.loads`. The aws, azure and gcp roots all call `../modules/operators`, so validating them proves `file("${path.module}/versions/Chart.yaml")` resolves from each root.)

- [ ] **Step 7: The stub lints, and the image scan's list is unchanged.**

actionlint (fetched by Task 10 Step 5 into `$SCR/tools`) re-checks the edited workflow. The `List the chart's third-party images` step runs from the edited workflow against the worktree, and from `main`'s workflow (which still reads `main.tf`) against a `main` export. The two image lists must be identical. The step fetches the operator charts with `helm template --repo` and reads CNPG's source on GitHub, so it needs network access:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT && $SCR/tools/actionlint -no-color .github/workflows/image-scan.yml; echo "actionlint rc=$?"
helm lint $WT/Iverson.Server/deploy/terraform/modules/operators/versions | tail -1
helm dependency list $WT/Iverson.Server/deploy/terraform/modules/operators/versions
cat > $SCR/ops/step.py <<'PY'
# step.py <workflow> <out>: writes the third-party job's "List the chart's third-party images" run script.
import sys, yaml
steps = yaml.safe_load(open(sys.argv[1]))["jobs"]["third-party"]["steps"]
open(sys.argv[2], "w").write(next(s["run"] for s in steps if s.get("name") == "List the chart's third-party images"))
PY
rm -rf $SCR/ops/head && mkdir -p $SCR/ops/head
git -C $WT archive main Iverson.Server/deploy .github/workflows/image-scan.yml | tar -x -C $SCR/ops/head
python3 -I $SCR/ops/step.py $SCR/ops/head/.github/workflows/image-scan.yml $SCR/ops/list-before.sh
python3 -I $SCR/ops/step.py $WT/.github/workflows/image-scan.yml $SCR/ops/list-after.sh
(cd $SCR/ops/head && bash --noprofile --norc -eo pipefail $SCR/ops/list-before.sh >/dev/null 2>$SCR/ops/before.err; echo "before rc=$?"; mv images.txt $SCR/ops/images-before.txt)
(cd $WT && bash --noprofile --norc -eo pipefail $SCR/ops/list-after.sh >/dev/null 2>$SCR/ops/after.err; echo "after rc=$?"; mv images.txt $SCR/ops/images-after.txt)
wc -l < $SCR/ops/images-after.txt; diff $SCR/ops/images-before.txt $SCR/ops/images-after.txt && echo "image lists identical"
command grep -E 'cloudnative-pg|strimzi' $SCR/ops/images-after.txt
git -C $WT status --short --ignored -- images.txt
```

Expected:

```text
actionlint rc=0
1 chart(s) linted, 0 chart(s) failed
NAME                        	VERSION	REPOSITORY                                               	STATUS 
cloudnative-pg              	0.29.0 	https://cloudnative-pg.github.io/charts                  	missing
strimzi-kafka-operator      	1.1.0  	https://strimzi.io/charts/                               	missing
operator                    	1.11.5 	https://starrocks.github.io/starrocks-kubernetes-operator	missing
cluster-autoscaler          	9.37.0 	https://kubernetes.github.io/autoscaler                  	missing
aws-load-balancer-controller	1.9.0  	https://aws.github.io/eks-charts                         	missing

before rc=0
after rc=0
16
image lists identical
ghcr.io/cloudnative-pg/cloudnative-pg:1.30.0
ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie
quay.io/strimzi/kafka:1.1.0-kafka-4.3.0
quay.io/strimzi/operator:1.1.0
```

(`missing` only means the stub has no `charts/` directory, which nothing needs. The last command prints nothing, because both `images.txt` files were moved out. If an earlier task in this plan changed the chart's images, the count differs; the requirement is `image lists identical`.)

Then the reader's failure path. A chart that is not in the stub stops `render_operator`:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT && sed -n '/^PINS=/,/^}$/p;/^render_operator()/,/^}$/p' <(sed 's/^          //' $SCR/ops/list-after.sh) > $SCR/ops/fns.sh
bash --noprofile --norc -eo pipefail -c "source $SCR/ops/fns.sh; chart_pin strimzi-kafka-operator 3; chart_pin operator 2; render_operator no-such-chart; echo unreachable"; echo "rc=$?"
```

Expected:

```text
https://strimzi.io/charts/
1.11.5
no no-such-chart pin in Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml
rc=1
```

- [ ] **Step 8: The kind scripts' readers.**

Run only the reader blocks, never the scripts: `setup.sh` and `setup.ps1` install into the current kube context. PowerShell is fetched as a pinned, checksum-verified release into scratch, and deleted afterwards:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
K=$WT/Iverson.Server/deploy/kind
# setup.sh's reader, run from a scratch copy beside a copy of the stub (same relative layout)
rm -rf $SCR/kind && mkdir -p $SCR/kind/deploy/kind $SCR/kind/deploy/terraform/modules/operators
cp -r $WT/Iverson.Server/deploy/terraform/modules/operators/versions $SCR/kind/deploy/terraform/modules/operators/
sed -n '/^OPERATOR_PINS=/,/^STARROCKS_OPERATOR_VERSION=/p' $K/setup.sh > $SCR/kind/deploy/kind/reader.sh
cd / && bash -euo pipefail -c "source $SCR/kind/deploy/kind/reader.sh; echo \"sh: \$CNPG_VERSION \$STRIMZI_VERSION \$STARROCKS_OPERATOR_VERSION\""
sed -i 's/^  - name: operator$/  - name: renamed-operator/' $SCR/kind/deploy/terraform/modules/operators/versions/Chart.yaml
bash -euo pipefail -c "source $SCR/kind/deploy/kind/reader.sh; echo unreachable"; echo "sh missing pin rc=$?"
cp $WT/Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml $SCR/kind/deploy/terraform/modules/operators/versions/
# setup.ps1's reader
mkdir -p $SCR/tools/pwsh-dl && cd $SCR/tools/pwsh-dl
curl -fsSLO https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/powershell-7.6.6-linux-x64.tar.gz
echo "ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc  powershell-7.6.6-linux-x64.tar.gz" | sha256sum -c -
mkdir -p $SCR/tools/pwsh && tar xzf powershell-7.6.6-linux-x64.tar.gz -C $SCR/tools/pwsh && rm -rf $SCR/tools/pwsh-dl && chmod +x $SCR/tools/pwsh/pwsh
sed -n '/^\$OperatorPins = /,/^\$StarRocksOperatorVersion = /p' $K/setup.ps1 > $SCR/kind/deploy/kind/reader.ps1
cat > $SCR/kind/check.ps1 <<'PS'
param([string]$Setup, [string]$Reader)
$ErrorActionPreference = "Stop"
$errs = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($Setup, [ref]$null, [ref]$errs)
"setup.ps1 parse errors: $($errs.Count)"
try { . $Reader; "ps1: $CnpgVersion $StrimziVersion $StarRocksOperatorVersion" }
catch { "ps1 throws: $($_.Exception.Message)"; exit 1 }
PS
NO_COLOR=1 $SCR/tools/pwsh/pwsh -NoProfile -NonInteractive -File $SCR/kind/check.ps1 $K/setup.ps1 $SCR/kind/deploy/kind/reader.ps1
sed -i 's/^  - name: operator$/  - name: renamed-operator/' $SCR/kind/deploy/terraform/modules/operators/versions/Chart.yaml
NO_COLOR=1 $SCR/tools/pwsh/pwsh -NoProfile -NonInteractive -File $SCR/kind/check.ps1 $K/setup.ps1 $SCR/kind/deploy/kind/reader.ps1 | tail -1; echo "ps1 missing pin rc=${PIPESTATUS[0]}"
rm -rf $SCR/tools/pwsh $SCR/kind
```

Expected:

```text
sh: 0.29.0 1.1.0 1.11.5
no operator pin in $SCR/kind/deploy/terraform/modules/operators/versions/Chart.yaml
sh missing pin rc=1
powershell-7.6.6-linux-x64.tar.gz: OK
setup.ps1 parse errors: 0
ps1: 0.29.0 1.1.0 1.11.5
ps1 throws: no operator pin in $SCR/kind/deploy/kind/../terraform/modules/operators/versions/Chart.yaml
ps1 missing pin rc=1
```

(`$SCR` is printed expanded.) `$PSScriptRoot` in the dot-sourced reader is the scratch copy's directory, which mirrors `deploy/kind`'s position relative to `deploy/terraform`. Renaming the `operator` entry shows that both readers stop with a named error instead of passing an empty `--version`, which would make `helm` install the newest chart. Windows PowerShell 5.1 is not exercised. The reader uses only `Join-Path`, `foreach`, `-split`, `-eq`, `return` and `throw`, which 5.1 supports, and `helm dependency list` prints the same table on Windows.

- [ ] **Step 9: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT
git add Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml Iverson.Server/deploy/terraform/modules/operators/main.tf \
  .github/dependabot.yml .github/workflows/image-scan.yml Iverson.Server/deploy/kind/setup.sh Iverson.Server/deploy/kind/setup.ps1 &&
git status --short -- .github Iverson.Server/deploy &&
git commit -q -m "move the operator chart pins to a dependabot-tracked stub chart

$TRAILER" && git log -1 --format='%s' && git show --stat --format= HEAD | tail -1
```

Expected: the status shows `A  ` for the stub and `M  ` for the five edited files, with nothing else under `.github` or `Iverson.Server/deploy` (the `charts/*.tgz` that Step 7's `helm dependency build` wrote are gitignored); the commit prints its subject and `6 files changed, 102 insertions(+), 52 deletions(-)`.

### Task 12: Image publishing and signing (#19, spec §5)

**Files:**
- Create: `.github/workflows/publish-images.yml`
- Modify: `Iverson.Server/deploy/helm/iverson/values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml` (the same four edits in each)

**Interfaces:**
- Produces the workflow `.github/workflows/publish-images.yml`, its job `publish (<image name>)` and the four GHCR packages `ghcr.io/counsellorben/iverson-api`, `iverson-admin-ui`, `iverson-tei-model` and `iverson-ollama-model`, which Task 14's runbook names. The signing identity is `https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@refs/heads/main` (or `@refs/tags/vX.Y.Z`), issuer `https://token.actions.githubusercontent.com`.
- Produces the cloud-profile values `api.image.repository`, `worker.image.repository`, `adminUi.image.repository` and `global.modelImageRegistry`, which Task 14's runbook names.
- Produces the pinned tools `$SCR/tools/actionlint` (1.7.12) and `$SCR/tools/shellcheck` (0.11.0), which Task 13 reuses. Task 10 may already have installed the same `actionlint`; the install step skips a tool that is present.
- No other task edits these four files. The model images' tags are read from the AWS render, so this task depends on nothing another task changes in the chart.

The workflow has two jobs. `images` lists what the run publishes: the two app images always, and the two model images on a `v` tag or a manual run. It reads the model images' references and build args from the chart render with the awk and sed of `deploy/kind/build-and-load-image.sh`. It renders `values-aws.yaml` with its CI override, because a cloud profile renders only with its override, and this profile's `global.modelImageRegistry` is the registry the workflow pushes to (it checks the prefix). Every profile renders the same model tags (spec Verified assumption 38). `publish` runs once per listed image: it builds, pushes, signs the digest, generates the SBOM of the digest and attests it. The `images` job holds only `contents: read`, because it neither pushes nor signs. Step outputs reach `run:` scripts through `env:`, never through `${{ }}` inside the script.

- [ ] **Step 1: Pinned tools.** `actionlint` and `shellcheck` lint the workflow. `cosign` is the release the workflow installs, and it is used here only to read its `--help`. Each is checked against its release SHA-256 (the cosign one is the bootstrap digest inside `sigstore/cosign-installer` v4.1.2).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; T=$SCR/tools; mkdir -p $T/dl && cd $T/dl
[ -x $T/actionlint ] || { curl -fsSLO https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz \
  && echo '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  actionlint_1.7.12_linux_amd64.tar.gz' | sha256sum -c \
  && tar -xzf actionlint_1.7.12_linux_amd64.tar.gz -C $T actionlint; }
[ -x $T/shellcheck ] || { curl -fsSLO https://github.com/koalaman/shellcheck/releases/download/v0.11.0/shellcheck-v0.11.0.linux.x86_64.tar.xz \
  && echo '8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198  shellcheck-v0.11.0.linux.x86_64.tar.xz' | sha256sum -c \
  && tar -xJf shellcheck-v0.11.0.linux.x86_64.tar.xz -C $T --strip-components=1 shellcheck-v0.11.0/shellcheck; }
[ -x $T/cosign ] || { curl -fsSLo cosign-linux-amd64 https://github.com/sigstore/cosign/releases/download/v3.0.6/cosign-linux-amd64 \
  && echo 'c956e5dfcac53d52bcf058360d579472f0c1d2d9b69f55209e256fe7783f4c74  cosign-linux-amd64' | sha256sum -c \
  && install -m 755 cosign-linux-amd64 $T/cosign && rm cosign-linux-amd64; }
rm -f $T/dl/actionlint_1.7.12_linux_amd64.tar.gz $T/dl/shellcheck-v0.11.0.linux.x86_64.tar.xz
echo "actionlint $($T/actionlint -version | head -1) | shellcheck $($T/shellcheck --version | sed -n 's/^version: //p') | cosign $($T/cosign version 2>&1 | sed -n 's/^GitVersion: *//p')"
```

Expected:

```text
actionlint_1.7.12_linux_amd64.tar.gz: OK
shellcheck-v0.11.0.linux.x86_64.tar.xz: OK
cosign-linux-amd64: OK
actionlint 1.7.12 | shellcheck 0.11.0 | cosign v3.0.6
```

A tool already present (Task 10 installs the same `actionlint`) prints no `OK` line.

- [ ] **Step 2: Create `.github/workflows/publish-images.yml`** with exactly this content:

```yaml
name: Publish Images

# CSR round-11 #19. Publishes the first-party images to GHCR, signs each pushed digest keyless with
# cosign (GitHub OIDC; the signature is recorded in the public Rekor log) and attaches a signed
# SPDX-JSON SBOM attestation. iverson-api and iverson-admin-ui publish on every run, tagged with the
# commit SHA on main and X.Y.Z on a vX.Y.Z tag, never "latest". The model images publish only on tags
# and manual runs, under the content-derived tags the chart references. Verifying a published image:
# docs/runbooks/csr11-clients-tooling-ci.md.

on:
  push:
    branches: [main]
    tags: ['v*']
  workflow_dispatch: {}

jobs:
  images:
    # The publish job's matrix: one entry per image this run publishes.
    runs-on: ubuntu-latest
    permissions:
      contents: read
    outputs:
      include: ${{ steps.list.outputs.include }}
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
      - name: Set up Helm
        uses: azure/setup-helm@9bc31f4ebc9c6b171d7bfbaa5d006ae7abdb4310 # v5.0.1
        with:
          version: v3.15.0
      - name: List the images
        id: list
        run: |
          set -euo pipefail
          fail() { echo "::error::$*"; exit 1; }
          case "$GITHUB_REF" in
            refs/heads/main) tag=$GITHUB_SHA ;;
            refs/tags/v*)
              tag=${GITHUB_REF#refs/tags/v}
              [[ $tag =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "tag ${GITHUB_REF#refs/tags/} is not vX.Y.Z" ;;
            *) fail "images publish from main or a vX.Y.Z tag, not $GITHUB_REF" ;;
          esac
          reg=ghcr.io/counsellorben
          row() {   # name image tag context dockerfile [build-arg ...]: one matrix entry as JSON
            python3 -c 'import json, sys; n, i, t, c, f, *a = sys.argv[1:]; print(json.dumps({"name": n, "image": i, "tag": t, "context": c, "dockerfile": f, "build_args": "\n".join(a)}))' "$@"
          }
          rows="$RUNNER_TEMP/images.jsonl"
          {
            row iverson-api "$reg/iverson-api" "$tag" . Iverson.Server/Iverson.Api/Dockerfile
            row iverson-admin-ui "$reg/iverson-admin-ui" "$tag" . Iverson.AdminUI/Dockerfile
            if [ "$GITHUB_EVENT_NAME" = workflow_dispatch ] || [[ $GITHUB_REF == refs/tags/v* ]]; then
              # The model images' references and build args come from the chart render, as
              # deploy/kind/build-and-load-image.sh reads them, so each pushed tag is the
              # content-derived tag the chart pulls. The AWS profile sets global.modelImageRegistry
              # to this registry; its CI override only fills in ingress placeholders the render
              # requires. Every profile renders the same model tags.
              C=Iverson.Server/deploy/helm/iverson
              helm dependency build "$C" >/dev/null
              render=$(helm template iverson "$C" -f "$C/values-aws.yaml" -f "$C/values-aws.ci-override.yaml")
              tei=$(printf '%s\n' "$render" | awk '
                /image: "[^"]*iverson-tei-model:/ { match($0, /"[^"]+"/); img = substr($0, RSTART+1, RLENGTH-2) }
                /args: \["--model-id"/ { split($0, a, "\""); print img "|" a[4] "|" a[8] }')
              [ -n "$tei" ] || fail "the chart renders no iverson-tei-model image"
              while IFS='|' read -r image model_id revision; do
                [[ $image == "$reg"/* ]] || fail "the chart's model image $image is not under $reg/"
                row iverson-tei-model "${image%:*}" "${image##*:}" "$C/charts/tei/model-image" "$C/charts/tei/model-image/Dockerfile" \
                  "MODEL_ID=$model_id" "REVISION=$revision"
              done <<< "$tei"
              image=$(printf '%s\n' "$render" | sed -n 's/.*image: "\([^"]*iverson-ollama-model:[^"]*\)".*/\1/p' | head -1)
              model=$(printf '%s\n' "$render" | sed -n 's/.*iverson.io\/model: "\(.*\)"/\1/p' | head -1)
              digest=$(printf '%s\n' "$render" | sed -n 's/.*iverson.io\/model-digest: "\(.*\)"/\1/p' | head -1)
              if [ -z "$image" ] || [ -z "$model" ] || [ -z "$digest" ]; then fail "the chart renders no iverson-ollama-model image"; fi
              [[ $image == "$reg"/* ]] || fail "the chart's model image $image is not under $reg/"
              row iverson-ollama-model "${image%:*}" "${image##*:}" "$C/charts/ollama/model-image" "$C/charts/ollama/model-image/Dockerfile" \
                "MODEL=$model" "DIGEST=$digest"
            fi
          } > "$rows"
          cat "$rows"
          echo "include=$(python3 -c 'import json, sys; print(json.dumps([json.loads(l) for l in sys.stdin]))' < "$rows")" >> "$GITHUB_OUTPUT"

  publish:
    name: publish (${{ matrix.name }})
    needs: images
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
      id-token: write
    strategy:
      fail-fast: false
      matrix:
        include: ${{ fromJSON(needs.images.outputs.include) }}
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
      - name: Log in to GHCR
        uses: docker/login-action@dbcb813823bdd20940b903addbd779551569679f # v4.6.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/setup-buildx-action@f87e5991a6d7451dcb8d9637bfbc97413f497069 # v4.4.1
      - name: Build and push
        id: build
        uses: docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc # v7.4.0
        with:
          context: ${{ matrix.context }}
          file: ${{ matrix.dockerfile }}
          build-args: ${{ matrix.build_args }}
          tags: ${{ matrix.image }}:${{ matrix.tag }}
          labels: org.opencontainers.image.source=${{ github.server_url }}/${{ github.repository }}
          push: true
      - uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2
        with:
          # The installer's own bootstrap release, which it checks against a SHA-256 in the pinned
          # action.
          cosign-release: v3.0.6
      - name: Sign
        env:
          REF: ${{ matrix.image }}@${{ steps.build.outputs.digest }}
        run: cosign sign --yes "$REF"
      - name: SBOM
        uses: anchore/sbom-action@66cbf4bc1f1c0d2edc94016e65bc221b6bb0ad6c # v0.24.3
        with:
          image: ${{ matrix.image }}@${{ steps.build.outputs.digest }}
          registry-username: ${{ github.actor }}
          registry-password: ${{ secrets.GITHUB_TOKEN }}
          format: spdx-json
          output-file: ${{ runner.temp }}/sbom.spdx.json
          upload-artifact: false
          upload-release-assets: false
      - name: Attest the SBOM
        env:
          REF: ${{ matrix.image }}@${{ steps.build.outputs.digest }}
          SBOM: ${{ runner.temp }}/sbom.spdx.json
        run: cosign attest --yes --type spdxjson --predicate "$SBOM" "$REF"
```

- [ ] **Step 3: Lint the workflow and check its pins.** The Python check reads the workflow's triggers, job permissions and checkouts, and re-resolves each action's version comment to its commit through read-only `gh api` GETs, dereferencing annotated tags. The last line checks the cosign flags the workflow uses against the pinned release.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; T=$SCR/tools; cd $WT
$T/actionlint -shellcheck $T/shellcheck .github/workflows/publish-images.yml && echo "actionlint: clean"
python3 - .github/workflows/publish-images.yml <<'PY'
import re, subprocess, sys, yaml
src = open(sys.argv[1]).read(); w = yaml.safe_load(src)
on = w[True]   # PyYAML reads the key `on` as True
print("triggers:", sorted(on), "| push:", on["push"])
for name, job in w["jobs"].items():
    co = [s for s in job["steps"] if s.get("uses", "").startswith("actions/checkout@")]
    print(f"job {name}: permissions {job['permissions']} | checkouts {len(co)}, persist-credentials false: {all(s['with']['persist-credentials'] is False for s in co)}")
pins = re.findall(r"uses: ([\w./-]+)@(\S+) # (v[\w.]+)", src)
print("uses lines:", len(re.findall(r"uses:", src)), "| pinned with a version comment:", len(pins), "| all 40-hex:", all(re.fullmatch(r"[0-9a-f]{40}", s) for _, s, _ in pins))
def ref(path): return subprocess.run(["gh", "api", path, "--jq", '.object.type + " " + .object.sha'], capture_output=True, text=True, check=True).stdout.split()
for repo, sha, tag in sorted(set(pins)):
    t, s = ref(f"repos/{repo}/git/ref/tags/{tag}")
    if t == "tag": t, s = ref(f"repos/{repo}/git/tags/{s}")   # annotated tag: dereference to its commit
    print(f"  {repo} {tag}: {'commit matches' if (t, s) == ('commit', sha) else 'MISMATCH ' + t + ' ' + s}")
print("cosign-release:", re.findall(r"cosign-release: (\S+)", src))
PY
echo "cosign flags: $(for f in 'sign --yes=' 'attest --yes=' 'attest --type=' 'attest --predicate='; do set -- $f; $T/cosign $1 --help | command grep -q -- "^ *-*[a-z]*,* *$2" && echo "$f ok" || echo "$f MISSING"; done | tr '\n' ';') $($T/cosign attest --help | command grep -q 'spdxjson' && echo 'attest --type spdxjson ok')"
```

Expected:

```text
actionlint: clean
triggers: ['push', 'workflow_dispatch'] | push: {'branches': ['main'], 'tags': ['v*']}
job images: permissions {'contents': 'read'} | checkouts 1, persist-credentials false: True
job publish: permissions {'contents': 'read', 'packages': 'write', 'id-token': 'write'} | checkouts 1, persist-credentials false: True
uses lines: 8 | pinned with a version comment: 8 | all 40-hex: True
  actions/checkout v7.0.1: commit matches
  anchore/sbom-action v0.24.3: commit matches
  azure/setup-helm v5.0.1: commit matches
  docker/build-push-action v7.4.0: commit matches
  docker/login-action v4.6.0: commit matches
  docker/setup-buildx-action v4.4.1: commit matches
  sigstore/cosign-installer v4.1.2: commit matches
cosign-release: ['v3.0.6']
cosign flags: sign --yes= ok;attest --yes= ok;attest --type= ok;attest --predicate= ok; attest --type spdxjson ok
```

- [ ] **Step 4: Point the cloud profiles at GHCR.** The same four edits go into `values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml`; each anchor occurs once in each file, and the applier asserts it. Only `repository` is set, so the chart's default `tag` and `pullPolicy` still merge in, and the tag stays operator-supplied (spec §5).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $CH && python3 - <<'PY'
import pathlib
edits = [
    ('  externalScheme: "https"\n',
     '  externalScheme: "https"\n'
     '  # CSR round-11 #19: the model images are published to GHCR by\n'
     '  # .github/workflows/publish-images.yml (docs/runbooks/csr11-clients-tooling-ci.md).\n'
     '  modelImageRegistry: "ghcr.io/counsellorben/"\n'),
    ('\napi:\n  ingress:\n',
     '\napi:\n'
     '  # CSR round-11 #19: published to GHCR by .github/workflows/publish-images.yml. Set image.tag on\n'
     '  # every deploy (docs/runbooks/csr11-clients-tooling-ci.md).\n'
     '  image:\n'
     '    repository: "ghcr.io/counsellorben/iverson-api"\n'
     '  ingress:\n'),
    ('\nworker:\n  replicas: 2\n',
     '\nworker:\n'
     '  image:\n'
     '    repository: "ghcr.io/counsellorben/iverson-api"   # the api image (api.image above)\n'
     '  replicas: 2\n'),
    ('\nadminUi:\n  ingress:\n',
     '\nadminUi:\n'
     '  image:\n'
     '    repository: "ghcr.io/counsellorben/iverson-admin-ui"   # published with the api image\n'
     '  ingress:\n'),
]
for name in ("values-aws.yaml", "values-gcp.yaml", "values-azure.yaml"):
    p = pathlib.Path(name)
    s = p.read_text()
    for old, new in edits:
        assert s.count(old) == 1, (name, old)
        s = s.replace(old, new)
    p.write_text(s)
    print(name, "edited")
PY
```

Expected:

```text
values-aws.yaml edited
values-gcp.yaml edited
values-azure.yaml edited
```

- [ ] **Step 5: Dry-run the workflow's image list against the chart.** `list.sh` extracts the `images` job's own `run:` script from the workflow and runs it as the runner would (`bash --noprofile --norc -eo pipefail`), with the `GITHUB_*` and `RUNNER_TEMP` variables of a given ref and event. Five cases: a tag push, a `main` push, a manual run on `main`, a manual run on another branch (refused) and a tag that is not `vX.Y.Z` (refused). Then `deploy/kind/build-and-load-image.sh --model-images` runs with `docker` and `kind` stubbed to print their arguments, and its `docker build` tags and build args are compared with the workflow's model entries (same tag, registry prefix aside, and same build args). Nothing is built or pushed.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; mkdir -p $SCR/publish/stub
cat > $SCR/publish/list.sh <<'SH'
#!/usr/bin/env bash
# list.sh <GITHUB_REF> <GITHUB_EVENT_NAME>: runs publish-images.yml's own "List the images" script
# against the worktree as the runner would; prints its exit code, any ::error:: and the matrix.
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
python3 -c 'import sys, yaml; w = yaml.safe_load(open(sys.argv[1])); print(next(s["run"] for s in w["jobs"]["images"]["steps"] if s.get("id") == "list"))' \
  $WT/.github/workflows/publish-images.yml > $SCR/publish/list-step.sh
D=$(mktemp -d $SCR/publish/run.XXXX); : > $D/output
(cd $WT && GITHUB_REF=$1 GITHUB_EVENT_NAME=$2 GITHUB_SHA=0123456789abcdef0123456789abcdef01234567 RUNNER_TEMP=$D GITHUB_OUTPUT=$D/output \
  bash --noprofile --norc -eo pipefail $SCR/publish/list-step.sh > $D/stdout 2>&1); echo "exit=$?"
command grep '::error::' $D/stdout
python3 -c 'import json, sys
line = open(sys.argv[1]).read().strip()
if line:
    k, v = line.split("=", 1); assert k == "include"
    for e in json.loads(v): print(e["name"], e["image"] + ":" + e["tag"], e["context"], e["dockerfile"], e["build_args"].replace("\n", " "))' $D/output
rm -rf $D
SH
chmod +x $SCR/publish/list.sh
for c in "refs/tags/v1.2.3 push" "refs/heads/main push" "refs/heads/main workflow_dispatch" "refs/heads/feature workflow_dispatch" "refs/tags/v1.2 push"; do echo "== $c"; $SCR/publish/list.sh $c; done | tee $SCR/publish/list.out
printf '#!/bin/sh\necho "STUB $(basename "$0") $*"\n' > $SCR/publish/stub/docker; cp $SCR/publish/stub/docker $SCR/publish/stub/kind; chmod +x $SCR/publish/stub/docker $SCR/publish/stub/kind
PATH=$SCR/publish/stub:$PATH bash $WT/Iverson.Server/deploy/kind/build-and-load-image.sh 0.1.0 csr11d-none --model-images > $SCR/publish/kind.out 2>&1
python3 - $SCR/publish/list.out $SCR/publish/kind.out <<'PY'
import re, sys
lines = open(sys.argv[1]).read().split("== ")[1].splitlines()[1:]   # the tag-push case
wf = sorted((f.split(" ")[1].removeprefix("ghcr.io/counsellorben/"), tuple(f.split(" ")[4:])) for f in lines if "-model " in f)
kind = sorted((m.group(1), tuple(re.findall(r"--build-arg (\S+)", m.group(0))))
              for m in re.finditer(r"STUB docker build --memory 3g -t (\S+)( --build-arg \S+)+", open(sys.argv[2]).read()))
print("kind builder:", kind)
print("model images match the kind builder:", wf == kind and len(wf) == 2)
PY
```

Expected:

```text
== refs/tags/v1.2.3 push
exit=0
iverson-api ghcr.io/counsellorben/iverson-api:1.2.3 . Iverson.Server/Iverson.Api/Dockerfile 
iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:1.2.3 . Iverson.AdminUI/Dockerfile 
iverson-tei-model ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b Iverson.Server/deploy/helm/iverson/charts/tei/model-image Iverson.Server/deploy/helm/iverson/charts/tei/model-image/Dockerfile MODEL_ID=BAAI/bge-base-en-v1.5 REVISION=a5beb1e3e68b9ab74eb54cfd186867f64f240e1a
iverson-ollama-model ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1 Iverson.Server/deploy/helm/iverson/charts/ollama/model-image Iverson.Server/deploy/helm/iverson/charts/ollama/model-image/Dockerfile MODEL=qwen2.5:3b DIGEST=357c53fb659c5076de1d65ccb0b397446227b71a42be9d1603d46168015c9e4b
== refs/heads/main push
exit=0
iverson-api ghcr.io/counsellorben/iverson-api:0123456789abcdef0123456789abcdef01234567 . Iverson.Server/Iverson.Api/Dockerfile 
iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:0123456789abcdef0123456789abcdef01234567 . Iverson.AdminUI/Dockerfile 
== refs/heads/main workflow_dispatch
exit=0
iverson-api ghcr.io/counsellorben/iverson-api:0123456789abcdef0123456789abcdef01234567 . Iverson.Server/Iverson.Api/Dockerfile 
iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:0123456789abcdef0123456789abcdef01234567 . Iverson.AdminUI/Dockerfile 
iverson-tei-model ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b Iverson.Server/deploy/helm/iverson/charts/tei/model-image Iverson.Server/deploy/helm/iverson/charts/tei/model-image/Dockerfile MODEL_ID=BAAI/bge-base-en-v1.5 REVISION=a5beb1e3e68b9ab74eb54cfd186867f64f240e1a
iverson-ollama-model ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1 Iverson.Server/deploy/helm/iverson/charts/ollama/model-image Iverson.Server/deploy/helm/iverson/charts/ollama/model-image/Dockerfile MODEL=qwen2.5:3b DIGEST=357c53fb659c5076de1d65ccb0b397446227b71a42be9d1603d46168015c9e4b
== refs/heads/feature workflow_dispatch
exit=1
::error::images publish from main or a vX.Y.Z tag, not refs/heads/feature
== refs/tags/v1.2 push
exit=1
::error::tag v1.2 is not vX.Y.Z
kind builder: [('iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1', ('MODEL=qwen2.5:3b', 'DIGEST=357c53fb659c5076de1d65ccb0b397446227b71a42be9d1603d46168015c9e4b')), ('iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b', ('MODEL_ID=BAAI/bge-base-en-v1.5', 'REVISION=a5beb1e3e68b9ab74eb54cfd186867f64f240e1a'))]
model images match the kind builder: True
```

- [ ] **Step 6: Render, lint and validate every profile; check the image-scan filter.** The three cloud profiles render with their CI overrides, as CI does; `values-local` and `values-laptop` must still render the bare local names. The last line runs `image-scan.yml`'s third-party image filter over the three cloud renders and counts any first-party image it lets through.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; mkdir -p $SCR/publish/r; cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/publish/r/$v.yaml; e=$?
  echo "$v template=$e lint=$(helm lint . $o 2>&1 | tail -1) | $(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $SCR/publish/r/$v.yaml 2>&1 | tail -1 | sed 's/.*- //')"
  python3 -c 'import sys, yaml
for d in yaml.safe_load_all(open(sys.argv[1])):
    if d and d["kind"] in ("Deployment", "StatefulSet"):
        for c in d["spec"]["template"]["spec"]["containers"]:
            if "iverson-" in c["image"]: print("   ", d["kind"] + "/" + d["metadata"]["name"], c["image"])' $SCR/publish/r/$v.yaml; done
echo "first-party images through the third-party filter: $(for v in values-aws values-azure values-gcp; do cat $SCR/publish/r/$v.yaml; done | sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p' | command grep -v -E '(^|/)iverson-' | command grep -c -E 'iverson-|counsellorben')"
```

Expected:

```text
values-local template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 92, Invalid: 0, Errors: 0, Skipped: 6
    Deployment/iverson-admin-ui iverson-admin-ui:0.1.0
    Deployment/iverson-api iverson-api:0.1.0
    Deployment/iverson-worker iverson-api:0.1.0
    StatefulSet/iverson-ollama iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
    StatefulSet/iverson-tei-bge-base iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
values-laptop template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 69, Invalid: 0, Errors: 0, Skipped: 5
    Deployment/iverson-api iverson-api:0.1.0
    Deployment/iverson-worker iverson-api:0.1.0
    StatefulSet/iverson-ollama iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
    StatefulSet/iverson-tei-bge-base iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
values-aws template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 98, Invalid: 0, Errors: 0, Skipped: 6
    Deployment/iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:0.1.0
    Deployment/iverson-api ghcr.io/counsellorben/iverson-api:0.1.0
    Deployment/iverson-worker ghcr.io/counsellorben/iverson-api:0.1.0
    StatefulSet/iverson-ollama ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
    StatefulSet/iverson-tei-bge-base ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
values-azure template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 98, Invalid: 0, Errors: 0, Skipped: 6
    Deployment/iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:0.1.0
    Deployment/iverson-api ghcr.io/counsellorben/iverson-api:0.1.0
    Deployment/iverson-worker ghcr.io/counsellorben/iverson-api:0.1.0
    StatefulSet/iverson-ollama ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
    StatefulSet/iverson-tei-bge-base ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
values-gcp template=0 lint=1 chart(s) linted, 0 chart(s) failed | Valid: 98, Invalid: 0, Errors: 0, Skipped: 7
    Deployment/iverson-admin-ui ghcr.io/counsellorben/iverson-admin-ui:0.1.0
    Deployment/iverson-api ghcr.io/counsellorben/iverson-api:0.1.0
    Deployment/iverson-worker ghcr.io/counsellorben/iverson-api:0.1.0
    StatefulSet/iverson-ollama ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
    StatefulSet/iverson-tei-bge-base ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
first-party images through the third-party filter: 0
```

(The `Valid` counts are those at the spec's commit, Verified assumption 44. If an earlier task changed the chart's object count, they differ; what matters is `template=0`, `0 chart(s) failed`, `Invalid: 0, Errors: 0`, the GHCR names on the cloud profiles and the bare names on `values-local` and `values-laptop`.)

- [ ] **Step 7: Clean up and commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; rm -f $SCR/tools/cosign; rm -rf $SCR/publish/r; cd $WT
git add .github/workflows/publish-images.yml Iverson.Server/deploy/helm/iverson/values-aws.yaml Iverson.Server/deploy/helm/iverson/values-gcp.yaml Iverson.Server/deploy/helm/iverson/values-azure.yaml
git commit -m "publish signed first-party images to ghcr

$TRAILER"
git show --stat --format=%s HEAD | head -6
```

Expected:

The commit succeeds, and the stat lists exactly four files:

```text
publish signed first-party images to ghcr

 .github/workflows/publish-images.yml               | 138 +++++++++++++++++++++
 Iverson.Server/deploy/helm/iverson/values-aws.yaml |  11 ++
 .../deploy/helm/iverson/values-azure.yaml          |  11 ++
 Iverson.Server/deploy/helm/iverson/values-gcp.yaml |  11 ++
```

### Task 13: Branch-protection script (#19, spec §6)

**Files:**
- Create: `Iverson.Server/deploy/scripts/apply-branch-protection.sh` (mode 755)

**Interfaces:**
- Consumes `$SCR/tools/shellcheck` from Task 12 Step 1.
- Produces `Iverson.Server/deploy/scripts/apply-branch-protection.sh` with `--repo owner/name` (default `counsellorben/Iverson`) and `--dry-run`, which Task 14's runbook names.
- The 22 check names are the check-run names of jobs in `dependency-scan.yml`, `codeql.yml`, `dotnet-build.yml`, `image-scan.yml` and `deploy-validate.yml` (spec Verified assumption 25). No task in this plan renames any of those jobs or their matrix values.

This task never runs the script without `--dry-run`. The check below puts a `gh` stub first on `PATH`, which records any call, so a dry run that reached the API would show. The script's real run is the user's, per the runbook (Task 14).

- [ ] **Step 1: Write the check, and see it fail.** `check.py` runs the script with `--dry-run`, parses the printed body and compares it with spec §6: exactly the 22 checks, each with `app_id` 15368 and no `contexts` key, `strict` false, `enforce_admins` false, `required_pull_request_reviews` and `restrictions` null, every other option false. It also checks `--repo` and that bad arguments exit 2. With a GET of today's protection (read-only) as its second argument, it checks that the body keeps today's 11 checks and restates every setting today's GET reports.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; mkdir -p $SCR/protection/stub
printf '#!/bin/sh\necho "gh was called: $*" >&2\ntouch "%s/protection/gh-called"\nexit 99\n' "$SCR" > $SCR/protection/stub/gh; chmod +x $SCR/protection/stub/gh
cat > $SCR/protection/check.py <<'PY'
# check.py <script> [today.json]: runs apply-branch-protection.sh --dry-run (and bad invocations)
# with a `gh` stub first on PATH that records any call, and compares the body with spec §6.
import json, os, subprocess, sys
script, scr = sys.argv[1], os.environ["SCR"]
env = dict(os.environ, PATH=f"{scr}/protection/stub:" + os.environ["PATH"])
marker = f"{scr}/protection/gh-called"
if os.path.exists(marker): os.remove(marker)
def run(*args): return subprocess.run(["bash", script, *args], capture_output=True, text=True, env=env)
EXPECTED = {
    "nuget-audit", "npm-audit-adminui", "npm-audit-ts-sdk",
    "Analyze (actions)", "Analyze (csharp)", "Analyze (go)", "Analyze (java-kotlin)",
    "Analyze (javascript-typescript)", "Analyze (python)",
    "dotnet-build-test", "typescript-sdk-build-test",
    "govulncheck", "pip-audit-python-sdk", "pip-audit-agents",
    "first-party (iverson-api, Iverson.Server/Iverson.Api/Dockerfile)",
    "first-party (iverson-admin-ui, Iverson.AdminUI/Dockerfile)", "third-party",
    "helm-lint", "helm-template-kubeconform",
    "terraform-validate (aws)", "terraform-validate (azure)", "terraform-validate (gcp)",
}
r = run("--dry-run")
print("dry-run exit", r.returncode, "| stderr:", r.stderr.strip())
body = json.loads(r.stdout)
rsc = body["required_status_checks"]
names = [c["context"] for c in rsc["checks"]]
print("checks", len(names), "| distinct", len(set(names)), "| equal to spec §6:", set(names) == EXPECTED,
      "| app_ids", sorted({c["app_id"] for c in rsc["checks"]}), "| contexts key:", "contexts" in rsc)
print("strict", rsc["strict"], "| enforce_admins", body["enforce_admins"],
      "| reviews", body["required_pull_request_reviews"], "| restrictions", body["restrictions"])
flags = {k: v for k, v in body.items() if k not in ("required_status_checks", "enforce_admins", "required_pull_request_reviews", "restrictions")}
print("other settings", flags)
r2 = run("--repo", "someone/trial", "--dry-run")
print("--repo someone/trial --dry-run: exit", r2.returncode, "| stderr:", r2.stderr.strip(), "| same body:", json.loads(r2.stdout) == body)
for bad in (["--repo", "not a repo", "--dry-run"], ["--repo", "a/b/../c", "--dry-run"], ["--bogus"], ["--repo"]):
    rb = run(*bad); print(bad, "exit", rb.returncode, "|", rb.stderr.strip().splitlines()[-1] if rb.stderr.strip() else "")
print("gh called:", os.path.exists(marker))
if len(sys.argv) > 2:   # today.json: a GET of the live protection, to show every setting is restated
    t = json.load(open(sys.argv[2]))
    print("today's checks kept:", {c["context"] for c in t["required_status_checks"]["checks"]} <= set(names),
          "| strict same:", t["required_status_checks"]["strict"] == rsc["strict"],
          "| enforce_admins same:", t["enforce_admins"]["enabled"] == body["enforce_admins"],
          "| reviews/restrictions absent today:", "required_pull_request_reviews" not in t and "restrictions" not in t,
          "| flags same:", all(t[k]["enabled"] == v for k, v in flags.items()),
          "| GET-only keys:", sorted(set(t) - set(body) - {"url"}))
PY
python3 $SCR/protection/check.py $WT/Iverson.Server/deploy/scripts/apply-branch-protection.sh 2>&1 | sed -n '1p;$p' | sed "s#$WT/##"
```

Expected (the script does not exist yet):

```text
dry-run exit 127 | stderr: bash: Iverson.Server/deploy/scripts/apply-branch-protection.sh: No such file or directory
json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)
```

- [ ] **Step 2: Create `Iverson.Server/deploy/scripts/apply-branch-protection.sh`** with exactly this content:

```bash
#!/usr/bin/env bash
set -euo pipefail

# CSR round-11 #19: sets main's branch protection so the security scans are required checks.
# The body restates every other setting as it was on 2026-10-12: no strict up-to-date rule, admin
# bypass kept (enforce_admins false), no required reviews, no push restrictions, every other
# option off. required_signatures has its own endpoint, so this PUT leaves it unchanged (off).
# Each check is pinned to the GitHub Actions app (app_id 15368), so only a check run from Actions
# satisfies it.
#
# Run it only once every required check below is green on main: a required check that is red or
# missing blocks every pull request. Try it first on a throwaway public repository you administer,
# with --repo (docs/runbooks/csr11-clients-tooling-ci.md).
#
# Usage: apply-branch-protection.sh [--repo owner/name] [--dry-run]
#   --repo     defaults to counsellorben/Iverson
#   --dry-run  prints the request body and makes no API call

usage() { echo "usage: ${0##*/} [--repo owner/name] [--dry-run]" >&2; }

repo=counsellorben/Iverson
dry_run=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      [[ $# -ge 2 ]] || { usage; exit 2; }
      repo=$2
      shift 2
      ;;
    --dry-run)
      dry_run=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done
[[ $repo =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ && $repo != *..* ]] || { echo "--repo must be owner/name, not '$repo'" >&2; exit 2; }

# The check-run names exactly as GitHub reports them (matrix jobs carry their matrix values).
checks=(
  "nuget-audit"
  "npm-audit-adminui"
  "npm-audit-ts-sdk"
  "Analyze (actions)"
  "Analyze (csharp)"
  "Analyze (go)"
  "Analyze (java-kotlin)"
  "Analyze (javascript-typescript)"
  "Analyze (python)"
  "dotnet-build-test"
  "typescript-sdk-build-test"
  "govulncheck"
  "pip-audit-python-sdk"
  "pip-audit-agents"
  "first-party (iverson-api, Iverson.Server/Iverson.Api/Dockerfile)"
  "first-party (iverson-admin-ui, Iverson.AdminUI/Dockerfile)"
  "third-party"
  "helm-lint"
  "helm-template-kubeconform"
  "terraform-validate (aws)"
  "terraform-validate (azure)"
  "terraform-validate (gcp)"
)

# The names hold no quote or backslash, so they go into the JSON as they are.
entries=""
for c in "${checks[@]}"; do
  entries+="${entries:+,
}      { \"context\": \"${c}\", \"app_id\": 15368 }"
done

body=$(cat <<JSON
{
  "required_status_checks": {
    "strict": false,
    "checks": [
${entries}
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "required_linear_history": false,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "block_creations": false,
  "required_conversation_resolution": false,
  "lock_branch": false,
  "allow_fork_syncing": false
}
JSON
)

if [[ $dry_run = 1 ]]; then
  echo "PUT repos/${repo}/branches/main/protection (dry run, nothing sent)" >&2
  printf '%s\n' "$body"
  exit 0
fi

printf '%s\n' "$body" | gh api -X PUT "repos/${repo}/branches/main/protection" --input -
```

- [ ] **Step 3: Run the check against the script and today's protection.** The GET is read-only. `required_signatures` is the one setting the GET reports that the PUT body cannot carry (it has its own endpoint, and the script's header says the PUT leaves it unchanged). The last two lines are a mutation check: with one required check deleted from a copy of the script, `check.py` must report a mismatch.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; S=$WT/Iverson.Server/deploy/scripts/apply-branch-protection.sh; chmod 755 $S
gh api repos/counsellorben/Iverson/branches/main/protection > $SCR/protection/today.json
python3 $SCR/protection/check.py $S $SCR/protection/today.json
$SCR/tools/shellcheck $S && echo "shellcheck: clean"
sed '/"terraform-validate (gcp)"/d' $S > $SCR/protection/mut.sh
python3 $SCR/protection/check.py $SCR/protection/mut.sh | sed -n 2p
```

Expected:

```text
dry-run exit 0 | stderr: PUT repos/counsellorben/Iverson/branches/main/protection (dry run, nothing sent)
checks 22 | distinct 22 | equal to spec §6: True | app_ids [15368] | contexts key: False
strict False | enforce_admins False | reviews None | restrictions None
other settings {'required_linear_history': False, 'allow_force_pushes': False, 'allow_deletions': False, 'block_creations': False, 'required_conversation_resolution': False, 'lock_branch': False, 'allow_fork_syncing': False}
--repo someone/trial --dry-run: exit 0 | stderr: PUT repos/someone/trial/branches/main/protection (dry run, nothing sent) | same body: True
['--repo', 'not a repo', '--dry-run'] exit 2 | --repo must be owner/name, not 'not a repo'
['--repo', 'a/b/../c', '--dry-run'] exit 2 | --repo must be owner/name, not 'a/b/../c'
['--bogus'] exit 2 | usage: apply-branch-protection.sh [--repo owner/name] [--dry-run]
['--repo'] exit 2 | usage: apply-branch-protection.sh [--repo owner/name] [--dry-run]
gh called: False
today's checks kept: True | strict same: True | enforce_admins same: True | reviews/restrictions absent today: True | flags same: True | GET-only keys: ['required_signatures']
shellcheck: clean
checks 21 | distinct 21 | equal to spec §6: False | app_ids [15368] | contexts key: False
```

If today's GET no longer reports 11 checks, `strict` false and every option off, the live setting changed since the spec: stop and report it, because the body would then not restate it.

- [ ] **Step 4: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add Iverson.Server/deploy/scripts/apply-branch-protection.sh
git commit -m "add a script that requires the security scans on main

$TRAILER"
git show --stat --summary --format=%s HEAD | tail -2
```

Expected:

The commit succeeds, and the summary ends:

```text
 1 file changed, 107 insertions(+)
 create mode 100755 Iverson.Server/deploy/scripts/apply-branch-protection.sh
```

### Task 14: Operator runbook (spec §8)

**Files:**
- Create: `docs/runbooks/csr11-clients-tooling-ci.md`

**Interfaces:**
- Consumes the names Tasks 12 and 13 introduce: `.github/workflows/publish-images.yml`, the four GHCR packages, the cloud-profile values `api.image.repository`, `worker.image.repository`, `adminUi.image.repository` and `global.modelImageRegistry`, and `apply-branch-protection.sh` with `--repo` and `--dry-run`.
- Documents, from the spec, what Task 5 does to `mint_acting_user_token.py` (`--password` removed; `IVERSON_ACTING_USER_PASSWORD` read in both targets) and what Task 10 does to the GitLab OWASP job (its `rules:` exclude `merge_request_event`) and to `deploy-validate.yml` (it runs on `main` pushes). Step 2's check does not test those three, because they are other tasks' results; Task 15 verifies them.

- [ ] **Step 1: Create `docs/runbooks/csr11-clients-tooling-ci.md`** with exactly this content:

````markdown
# CSR Round-11 Clients, Tooling and CI — Operator Runbook

**Why:** first-party images are now published to GHCR by GitHub Actions. Each pushed image is signed
keyless with cosign and carries a signed SPDX SBOM attestation, and the cloud profiles pull from
there. Branch protection on `main` requires the security scans. On GitLab, the NVD API key reaches
only protected pipelines. The acting-user mint script takes its password from the environment, not
the command line. kind and compose keep their locally built images.

The order after the merge:

1. Push `main`. `.github/workflows/publish-images.yml` publishes `iverson-api` and
   `iverson-admin-ui`. Run the workflow once by hand for the model images (§3).
2. Make the four packages public (§1), then verify them (§2).
3. Once every required check is green on `main`, apply branch protection: on a throwaway
   repository first, then on Iverson (§4).

## 1. Package visibility (one time)

GHCR creates every new package private, and GitHub has no REST endpoint that changes the
visibility of a user-owned package. Until a package is public, a cloud cluster's pull of it fails
with an authorization error, because the chart sets no `imagePullSecrets`.

After the first publish of each, make these four packages public: `iverson-api`,
`iverson-admin-ui`, `iverson-tei-model` and `iverson-ollama-model`. The model packages exist only
after the first tag or manual run. For each package, open your GitHub profile, then **Packages**,
then the package, then **Package settings**. Under **Danger Zone**, choose **Change visibility**,
then **Public**, and confirm with the package name.

The signatures and SBOM attestations are stored in the same package, so they become public with
it. The `org.opencontainers.image.source` label links each package to the Iverson repository.

## 2. Verifying an image

Use cosign 3.x, the version the workflow signs with. Both commands must succeed for every image
you deploy, from any machine with no GHCR login. A failure on a package that is still private is
an authorization error, not a verification failure.

```bash
cosign verify ghcr.io/counsellorben/iverson-api:<tag> \
  --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com

cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:<tag> \
  --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Run the same pair for `iverson-admin-ui:<tag>` and for the two model images under the tags the
chart renders:

```bash
helm dependency build Iverson.Server/deploy/helm/iverson
helm template iverson Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  -f Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml | sed -n 's/.*image: "\(ghcr\.io[^"]*-model:[^"]*\)".*/\1/p'
```

A pass means two things. The digest the tag names was signed by `publish-images.yml` running on
`main` or on a `v` tag of `counsellorben/Iverson`. The SBOM attestation for that digest came from
the same workflow. Signing writes an entry to the public Rekor transparency log, which records
the repository, workflow and ref.

The clusters do not check signatures at admission. Verification is the operator's check before a
deploy.

The publish workflow's first real run is unexercised before the push. Its acceptance check is
this section: run it against the `main` SHA tags after the first push, and against the model
images after the first manual run.

## 3. Deploying with image tags

`values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml` point `api.image.repository` and
`worker.image.repository` at `ghcr.io/counsellorben/iverson-api`, and `adminUi.image.repository` at
`ghcr.io/counsellorben/iverson-admin-ui`. They also set `global.modelImageRegistry` to
`ghcr.io/counsellorben/`.

The image tags stay operator-supplied. Set all three on every deploy, to the full commit SHA of a
`main` build or to `X.Y.Z` for a `vX.Y.Z` tag. The chart's default tag `0.1.0` is never published,
and the workflow never publishes `latest`.

```bash
helm upgrade --install iverson Iverson.Server/deploy/helm/iverson -n iverson \
  -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  --set api.image.tag=<tag>,worker.image.tag=<tag>,adminUi.image.tag=<tag>
```

The chart computes the model images' tags itself, from the model pins and each model Dockerfile.
Those images publish only on a `v` tag or a manual run, so after changing a model pin or a model
Dockerfile, run the workflow on that commit before deploying it:

```bash
gh workflow run publish-images.yml --ref main
```

A manual run also republishes `iverson-api` and `iverson-admin-ui` under the commit SHA. The
workflow refuses any ref other than `main` or a `vX.Y.Z` tag.

## 4. Branch protection

`Iverson.Server/deploy/scripts/apply-branch-protection.sh` replaces `main`'s protection in one
call. It adds the security scans to the eleven existing required checks, 22 in all, and restates
every other setting as it was. The admin bypass stays (`enforce_admins` false), and no reviews are
required. A maintainer's direct push to `main` therefore still skips the required checks (accepted).
`owasp-dependency-check-java` and kube-score's `security-audit` are not required.

Run it only when every required check is green on `main`'s latest commit. Once a check is
required, a pull request whose run of it is red or missing cannot merge. The `deploy-validate`
jobs report on `main` from this change on. The script lists the 22 checks; read their state with:

```bash
gh api 'repos/counsellorben/Iverson/commits/main/check-runs?per_page=100' --paginate \
  --jq '.check_runs[] | "\(.conclusion) \(.name)"' | sort
```

`--dry-run` prints the request body and sends nothing. Save today's protection before changing it,
so it can be restored:

```bash
Iverson.Server/deploy/scripts/apply-branch-protection.sh --dry-run
gh api repos/counsellorben/Iverson/branches/main/protection > protection-before.json
```

Before the first change to Iverson, apply the body to a throwaway public repository you
administer, to confirm GitHub accepts it. The repository needs an initial commit so `main`
exists. `--add-readme` makes one on your account's default branch, which must be `main`.

```bash
gh repo create <you>/protection-trial --public --add-readme
Iverson.Server/deploy/scripts/apply-branch-protection.sh --repo <you>/protection-trial
gh api repos/<you>/protection-trial/branches/main/protection --jq '.required_status_checks.checks | length'
```

The last command prints `22`. If GitHub rejects the body, stop: the body changes before Iverson's
protection is touched. Delete the throwaway repository afterwards from its **Settings** page, or
with `gh repo delete <you>/protection-trial --yes` (this needs the `delete_repo` scope:
`gh auth refresh -s delete_repo`).

Then apply it to Iverson, the script's default repository, and read it back:

```bash
Iverson.Server/deploy/scripts/apply-branch-protection.sh
gh api repos/counsellorben/Iverson/branches/main/protection --jq '.required_status_checks.checks | length'
```

## 5. GitLab: `NVD_API_KEY`

The OWASP dependency-check job is the only GitLab job that uses `NVD_API_KEY`, and it no longer runs
in merge-request pipelines. Job rules do not limit a project-level CI/CD variable: such a variable
reaches every job of every pipeline, merge-request pipelines included. So define `NVD_API_KEY` in
one of two ways:

- as a **Protected** variable (project **Settings**, then **CI/CD**, then **Variables**; select
  **Protect variable**, and mask it), which only pipelines on protected branches and tags receive;
- or as a variable of a pipeline schedule (**Build**, then **Pipeline schedules**).

Remove any unprotected project-level `NVD_API_KEY`. GitLab's variable semantics are described at
https://docs.gitlab.com/ci/variables/.

## 6. `IVERSON_ACTING_USER_PASSWORD`

`Iverson.Server/deploy/scripts/mint_acting_user_token.py` no longer takes `--password`, because a
password on the command line is visible to other local users in the process list. It reads
`IVERSON_ACTING_USER_PASSWORD`, the variable LoadTest already uses for the same smoke-test user.

- `--target compose` requires it. Set it to the stack's smoke-test password, which is
  `IVERSON_SMOKE_TEST_PASSWORD` in `Iverson.Server/.env`.
- `--target kind` uses it when it is set. Otherwise the script reads the password from the
  `<release>-authentik-smoke-test-user` Secret, as before.

```bash
read -rs IVERSON_ACTING_USER_PASSWORD && export IVERSON_ACTING_USER_PASSWORD
python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose
```

`Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` passes the password to the script through this variable itself.
````

- [ ] **Step 2: Check every path, flag and value the runbook names.** Paths must exist in the worktree (`Iverson.Server/.env` is excluded: it is the compose stack's untracked secret file, and nobody reads it). The flags are read from the script, the mint script's `--help`, `gh`'s help and the pinned cosign release's help. The certificate-identity regexp is tested against the identities the workflow's signatures carry and against identities it must refuse. The Helm values are checked by a render with the runbook's `--set`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; T=$SCR/tools; cd $WT; RB=docs/runbooks/csr11-clients-tooling-ci.md
[ -x $T/cosign ] || { mkdir -p $T/dl && curl -fsSLo $T/dl/cosign-linux-amd64 https://github.com/sigstore/cosign/releases/download/v3.0.6/cosign-linux-amd64 \
  && (cd $T/dl && echo 'c956e5dfcac53d52bcf058360d579472f0c1d2d9b69f55209e256fe7783f4c74  cosign-linux-amd64' | sha256sum -c) \
  && install -m 755 $T/dl/cosign-linux-amd64 $T/cosign && rm $T/dl/cosign-linux-amd64; }
python3 - $RB <<'PY'
import os, re, sys
text = open(sys.argv[1]).read()
paths = sorted(set(re.findall(r"(?<![\\\w.])((?:\.github|Iverson\.[A-Za-z]+|docs)/[\w./-]*\w)", text)) - {"Iverson.Server/.env"})
missing = [p for p in paths if not os.path.exists(p)]
print("paths named:", len(paths), "| missing:", missing)
rx = re.findall(r"--certificate-identity-regexp '([^']+)'", text)
print("identity regexps:", len(rx), "| identical:", len(set(rx)) == 1,
      "| issuer flags:", text.count("--certificate-oidc-issuer https://token.actions.githubusercontent.com"))
r = re.compile(rx[0]); base = "https://github.com/counsellorben/Iverson/.github/workflows/publish-images.yml@"
for ref in ("refs/heads/main", "refs/tags/v1.2.3", "refs/heads/feature", "refs/pull/1/merge", "refs/heads/main-x"):
    print(f"  {ref}: {'match' if r.fullmatch(base + ref) else 'no match'}")
print("  image-scan.yml@refs/heads/main:", "match" if r.fullmatch(base.replace("publish-images", "image-scan") + "refs/heads/main") else "no match")
wf = open(".github/workflows/publish-images.yml").read()
print("packages named in the runbook and published by the workflow:", [p for p in ("iverson-api", "iverson-admin-ui", "iverson-tei-model", "iverson-ollama-model") if f"`{p}`" in text and f"row {p} " in wf])
PY
S=Iverson.Server/deploy/scripts/apply-branch-protection.sh
echo "script flags: $(for f in --repo --dry-run; do command grep -q -- "^    $f)" $S && echo -n "$f ok "; done)"
echo "mint script: $(python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --help | command grep -o -- '--target {compose,kind}' | head -1)"
echo "gh flags: $(gh repo create --help | command grep -c -- '--add-readme') $(gh repo delete --help | command grep -c -- '^ *--yes') $(gh auth refresh --help | command grep -c -- '-s, --scopes') $(gh workflow run --help | command grep -c -- '-r, --ref') $(gh api --help | command grep -c -E -- '^ *--paginate|-q, --jq')"
echo "cosign flags: $(for c in verify verify-attestation; do for f in --certificate-identity-regexp= --certificate-oidc-issuer=; do $T/cosign $c --help | command grep -q -- "^ *$f" && echo -n "$c $f ok; "; done; done)$($T/cosign verify-attestation --help | command grep -q -- '^ *--type=' && echo 'verify-attestation --type= ok')"
cd $CH && helm dependency build . >/dev/null && helm template iverson . -n iverson -f values-aws.yaml -f values-aws.ci-override.yaml \
  --set api.image.tag=1.2.3,worker.image.tag=1.2.3,adminUi.image.tag=1.2.3 | sed -n 's/^ *image: "\(ghcr.io\/counsellorben[^"]*\)"$/\1/p' | sort | uniq -c
cd $WT && helm template iverson Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  -f Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml | sed -n 's/.*image: "\(ghcr\.io[^"]*-model:[^"]*\)".*/\1/p'
rm -f $T/cosign
```

Expected:

```text
cosign-linux-amd64: OK
paths named: 7 | missing: []
identity regexps: 2 | identical: True | issuer flags: 2
  refs/heads/main: match
  refs/tags/v1.2.3: match
  refs/heads/feature: no match
  refs/pull/1/merge: no match
  refs/heads/main-x: no match
  image-scan.yml@refs/heads/main: no match
packages named in the runbook and published by the workflow: ['iverson-api', 'iverson-admin-ui', 'iverson-tei-model', 'iverson-ollama-model']
script flags: --repo ok --dry-run ok 
mint script: --target {compose,kind}
gh flags: 1 1 1 1 2
cosign flags: verify --certificate-identity-regexp= ok; verify --certificate-oidc-issuer= ok; verify-attestation --certificate-identity-regexp= ok; verify-attestation --certificate-oidc-issuer= ok; verify-attestation --type= ok
      1 ghcr.io/counsellorben/iverson-admin-ui:1.2.3
      2 ghcr.io/counsellorben/iverson-api:1.2.3
      1 ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
      1 ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
ghcr.io/counsellorben/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
ghcr.io/counsellorben/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b
```

The `cosign-linux-amd64: OK` line appears because Task 12 removed its copy. `iverson-api` counts 2: the api and worker Deployments.

- [ ] **Step 3: Commit.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git add -f docs/runbooks/csr11-clients-tooling-ci.md
git commit -m "document publishing, verification and branch protection for operators

$TRAILER"
git show --stat --format=%s HEAD | head -3
```

Expected:

The commit succeeds:

```text
document publishing, verification and branch protection for operators

 docs/runbooks/csr11-clients-tooling-ci.md | 174 ++++++++++++++++++++++++++++++
```

### Task 15: Verification

No commits. The task runs the full suites, re-runs the earlier tasks' checks on the final tree, renders every profile, and then runs spec §7's kind live check (CDR-1 §3.3 A): an in-cluster LoadTest run that mints its tokens through `https://iverson-authentik:8443` with `IVERSON_ACTING_USER_HOST_HEADER=iverson-authentik:8443`, and the API accepting them. That check confirms spec Verified assumption 29 and the runbook note Task 3 Step 8 writes.

Run one heavy process at a time and check `free -g` first. The container-backed suites (Step 1) and the kind cluster (Steps 5–7) never run together.

- [ ] **Step 1: Full suites.**

The .NET solution, as `.github/workflows/dotnet-build.yml` runs it: `dotnet build Iverson.slnx`, then `dotnet test Iverson.slnx --filter "Category!=Integration"`. The build is `--no-incremental`, so every project's warnings print, including projects earlier tasks already built.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
free -g | sed -n 2p
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=7G dotnet build Iverson.slnx --no-incremental -p:UseSharedCompilation=false > $SCR/t15-build.log 2>&1; echo build-exit=\$?; dotnet build-server shutdown >/dev/null 2>&1"
command grep -E '^ +[0-9]+ (Warning|Error)\(s\)' $SCR/t15-build.log
command grep -oE '[A-Za-z0-9.]+/[A-Za-z0-9./]+\.cs\([0-9,]+\): (warning|error) [A-Z]+[0-9]+' $SCR/t15-build.log | sort -u | sed -E 's#^.*/(Iverson\.[A-Za-z.]+)/.*: #\1: #' | sort | uniq -c
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=7G dotnet test Iverson.slnx --filter 'Category!=Integration' -p:UseSharedCompilation=false > $SCR/t15-test.log 2>&1; echo test-exit=\$?; dotnet build-server shutdown >/dev/null 2>&1"
command grep -E '^(Passed|Failed)!' $SCR/t15-test.log | sed -E 's/^(Passed|Failed)! +- Failed: +([0-9]+), Passed: +([0-9]+), Skipped: +([0-9]+), Total: +([0-9]+).* - (Iverson[A-Za-z.]+)\.dll.*/\6 \1 failed=\2 passed=\3 skipped=\4/' | sort
```

Expected (the build takes about six minutes and the tests about five):

```text
build-exit=0
    22 Warning(s)
    0 Error(s)
     11 Iverson.Api.Tests: warning CS0618
      1 Iverson.Client.Sample: warning CS8619
      1 Iverson.Patterns.Tests: warning CS0618
      4 Iverson.Sql.Tests: warning CS0618
      1 Iverson.StarRocks.Tests: warning CS0618
      2 Iverson.StarRocks: warning CS8604
      2 Iverson.Vector.Tests: warning CS0618
test-exit=0
Iverson.Api.Tests Passed failed=0 passed=1511 skipped=0
Iverson.Client.Core.Tests Passed failed=0 passed=91 skipped=0
Iverson.Client.Search.Tests Passed failed=0 passed=125 skipped=0
Iverson.ClientConformance.Tests Passed failed=0 passed=657 skipped=0
Iverson.Embeddings.Tests Passed failed=0 passed=60 skipped=0
Iverson.Events.Tests Passed failed=0 passed=41 skipped=0
Iverson.LoadTest.Tests Passed failed=0 passed=157 skipped=0
Iverson.Patterns.Tests Passed failed=0 passed=289 skipped=0
Iverson.Sql.Tests Passed failed=0 passed=86 skipped=0
Iverson.StarRocks.Tests Passed failed=0 passed=635 skipped=0
Iverson.Vector.Tests Passed failed=0 passed=112 skipped=0
```

- The 22 warnings are the same sites as on `main`; none is in a file this branch touches. The Sample's CS8619 predates the branch (Task 2 Step 8, Task 6 Step 2).
- Three suites grow: Client.Core.Tests 84 → 91 (Task 2), LoadTest.Tests 136 → 157 and ClientConformance.Tests 650 → 657 (Task 3). The other eight match `main`.
- `Iverson.StarRocks.Tests.StarRocksReadinessGateTests.A_shared_wait_its_only_waiter_abandoned_is_still_observed_when_it_fails` failed once at plan time, on `main`'s code, while the host's load average was about 10. It passed alone twice and in a re-run of the whole assembly. It is a timing test the branch does not touch. If it fails, re-run that assembly once on a quieter host. If it fails again, run it on `main` before treating it as a branch failure.

The container-backed suites, one at a time, the way sub-project C's verification ran them. They start their own throwaway Testcontainers and never touch the user's containers. Only the `Integration` category runs here, because the solution run above already ran every other test in these projects. The `--no-build` reuses that run's build.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Server
for p in Iverson.Api.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p --no-build --filter 'Category=Integration' 2>&1" | command grep -E '^(Passed|Failed)!' | sed -E 's/, Duration.*//')"
done
dotnet build-server shutdown >/dev/null 2>&1
```

Expected: every line reads `Passed!  - Failed:     0`, with Api.Tests 46, Patterns.Tests 202, Sql.Tests 63, StarRocks.Tests 66 and Vector.Tests 71 passed. These counts come from `dotnet test <project> --no-build --list-tests --filter "Category=Integration"` at plan time. They were not run then, because the drafting session runs no containers. For each project, the listed total minus the integration count equals the non-integration count the solution run above passed (1557 − 46 = 1511, 491 − 202 = 289, 149 − 63 = 86, 701 − 66 = 635, 183 − 71 = 112), so the listing enumerates the same cases a run does. None of these projects changes on this branch.

Java, the reactor build Task 1 Step 6 ran (offline, so it reuses Task 1's dependency cache):

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT/Iverson.Clients/Java
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=3G mvn -o -B verify 2>&1" | command grep -E 'Tests run: [0-9]+, F[^-]*$|Reactor Summary|SUCCESS \[|FAILURE|BUILD'
```

Expected (times vary):

```text
[INFO] Tests run: 227, Failures: 0, Errors: 0, Skipped: 0
[INFO] Reactor Summary for Iverson Java Client 1.0.0-SNAPSHOT:
[INFO] Iverson Java Client ................................ SUCCESS [  0.009 s]
[INFO] Iverson Client — Core .............................. SUCCESS [01:25 min]
[INFO] Iverson Sample Application ......................... SUCCESS [  4.254 s]
[INFO] Iverson Conformance Driver ......................... SUCCESS [  9.927 s]
[INFO] BUILD SUCCESS
```

Python: the client suite and the agent suite, from the main checkout's agent venv with `PYTHONPATH` on the worktree's client, as Task 2 Step 12 runs them. The venv's `iverson_client.pth` points at the main checkout's client, so without `PYTHONPATH` both suites would silently test `main`'s client. The first line in each directory proves which copies are imported. The hashed-lock installs, with the same two suites, run again in Step 2 (Task 7 Step 4).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; PY=/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python
for d in Iverson.Clients/Python Iverson.Agents/Python; do
  (cd $WT/$d && PYTHONPATH=$WT/Iverson.Clients/Python $PY -c "import iverson_client, os; m = __import__('iverson_agent') if os.path.isdir('iverson_agent') else iverson_client; print('$d imports', iverson_client.__file__.replace('$WT', '\$WT'), m.__file__.replace('$WT', '\$WT'))"
   PYTHONPATH=$WT/Iverson.Clients/Python $PY -m pytest -q -p no:cacheprovider 2>&1 | tail -1 | sed 's/ in [0-9.]*s.*//')
done
```

Expected:

```text
Iverson.Clients/Python imports $WT/Iverson.Clients/Python/iverson_client/__init__.py $WT/Iverson.Clients/Python/iverson_client/__init__.py
244 passed
Iverson.Agents/Python imports $WT/Iverson.Clients/Python/iverson_client/__init__.py $WT/Iverson.Agents/Python/iverson_agent/__init__.py
73 passed
```

The counts are 244 = 241 + 3 (Task 2) and 73 = 68 + 5 (Task 4). Task 4 counted its 73 in `$SCR/agent-venv`. At plan time this block, run on a tree holding the edits from Tasks 2 and 4, gave 73 in the main checkout's venv too.

Go: run Task 8 Step 2's block unchanged. Expected: what Task 8 Step 2 expects. That is `go version go1.26.9 linux/amd64`; `go build` and `go vet` print nothing; `ok` for the three packages and no `FAIL`; and govulncheck ends with `No vulnerabilities found.` Then remove the caches it wrote:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
chmod -R u+w $SCR/go && rm -rf $SCR/go; ls -d $SCR/go 2>/dev/null || echo "go caches removed"
```

Expected: `go caches removed`.

TypeScript SDK and admin console: run Task 9 Step 2's block unchanged. Expected: what Task 9 Step 2 expects, which is `source-map-js@1.2.2`, `audit-exit=0`, a build, and `Tests  226 passed (226)` (admin console) and `Tests  287 passed (287)` (TypeScript SDK). One difference: its closing `git -C $WT status --short` now prints nothing, because Task 9 committed the four files it lists. Task 9 records one load-related timeout of `src/router.test.tsx`; if it recurs, rerun the suite once on a quieter host before treating it as a failure.

- [ ] **Step 2: Re-run the tasks' checks on the final tree.**

Run these blocks again, unchanged and in this order. Each must print what its own step expects, except for the differences noted here.
1. **Task 12 Step 1** (the pinned `actionlint`, `shellcheck` and `cosign` in `$SCR/tools`). Only `cosign-linux-amd64: OK` and the version line print, because Task 12 Step 7 removed `cosign` and the other two are still present.
2. **Task 10 Step 5's first block** (actionlint over every workflow, including Task 12's `publish-images.yml`). It prints only `actionlint rc=0`.
3. **This step's GitLab block below.** It replaces Task 10 Step 5's second and third blocks.
4. **Task 10 Step 6** (deploy-validate runs unchanged on a `main` push).
5. **Task 7 Steps 1, 4 and 6**, in that order. Step 1 fetches the `get-pip.py` that Step 4 needs and Step 6 deletes. The differences:
   - Step 4's suites print `244 passed` and `73 passed`.
   - Its closing `git -C $WT status --short` prints nothing, because Task 7 Step 7 committed the four lock files.
   - Its audit output is otherwise as Task 7 expects: `No known vulnerabilities found` and `audit-exit=0` for both, with only the projects' own packages skipped.
6. **Task 9 Step 3** (the nginx digest is the live `1.30.5-alpine` index).
7. **Task 11 Steps 6, 7 and 8** (Terraform fmt, validate, tfsec and the decoded pins; the image-scan pin reader; the kind scripts' readers). Step 7 compares against `main`'s export, which still reads `main.tf`, so its output gates as Task 11 expects:
   - `image lists identical` and `after rc=0`;
   - `16`;
   - the four CNPG and Strimzi images;
   - the failure-path block's `no no-such-chart pin …` and `rc=1`.

   No later task edits the stub, `image-scan.yml`'s reader, or the operator pins.
8. **Task 12 Steps 3 and 5** (the workflow's triggers, permissions and pins, through read-only `gh api` GETs; the image list dry-run).
9. **Task 13 Step 3** (the protection script's `--dry-run` body against spec §6, and today's protection by a read-only GET).
10. **Task 14 Step 2** (every path, flag and value the runbook names). It prints no `cosign-linux-amd64: OK` line, because item 1 left `cosign` in place, and it removes `cosign` at its end.
11. **Task 3 Step 7** (LoadTest and the harness refuse at startup; it runs the Debug binaries Step 1 built), and **Task 3 Step 8's bash block** (the runbook note).
12. **Task 5 Step 5** (the mint-script and `pattern_leg` tests, and the CLI with no `--password`), and **Task 5 Step 6's bash block** (the user-management doc).

**Do not re-run** these:
- **Edit steps.** Their asserts require the pre-edit text, so they fail or double-apply on the final tree:
  - Task 1 Steps 1 and 3–5;
  - Task 2 Steps 1, 3, 5, 7, 9 and 11;
  - Task 3 Steps 1 and 3–5;
  - Task 4 Steps 1 and 3–5;
  - Task 5 Steps 1, 3 and 4;
  - Task 6 Step 1;
  - Task 7 Steps 2 and 5;
  - Task 8 Steps 1 and 3;
  - Task 9 Step 1;
  - Task 10 Steps 2–4;
  - Task 11 Steps 2–5;
  - Task 12 Steps 2 and 4;
  - Task 13 Step 2;
  - Task 14 Step 1.

  The text edits in Task 3 Step 8 and Task 5 Step 6 are applied by hand, so only those steps' bash blocks are re-run.
- **Red-test steps.** On the final tree they no longer fail:
  - Task 1 Step 2;
  - Task 2 Steps 2, 6 and 10;
  - Task 3 Step 2;
  - Task 4 Step 2;
  - Task 5 Step 2;
  - Task 13 Step 1. It also writes the `check.py` and the `gh` stub that Task 13 Step 3 uses; both are still in `$SCR/protection`.
- **Baselines.** Task 10 Step 1 records the registry resolutions. Task 11 Step 1 writes `$SCR/ops/before.json` from `main`'s `main.tf`; the file is still in `$SCR/ops` from Task 11's run, so Step 6 needs no re-run of it.
- **Task 7 Step 3.** It recompiles the locks, and pip-compile resolves the newest releases, so a re-run can rewrite the committed `requirements.txt` files.
- **Task 10 Step 5's second and third blocks.** Their "before" file is `HEAD`'s, which on the final tree already holds Task 10's change. The GitLab block below compares against `main` instead.
- **Suite runs Step 1 replaces:** Task 1 Step 6, Task 2 Steps 4, 8 and 12, Task 3 Step 6, Task 4 Step 6, Task 6 Step 2, Task 8 Step 2 and Task 9 Step 2. Step 1 ran the last two as they are.
- **Task 12 Step 6.** Step 3 runs it.
- **Commit steps:** Task 1 Step 7, Task 2 Step 13, Task 3 Step 9, Task 4 Step 7, Task 5 Step 7, Task 6 Step 3, Task 7 Step 7, Task 8 Step 4, Task 9 Step 4, Task 10 Step 7, Task 11 Step 9, Task 12 Step 7, Task 13 Step 4 and Task 14 Step 3.

The GitLab block. It parses `main`'s and the final `.gitlab-ci.yml`, checks that every job image on the final file is digest-pinned and that each pinned digest exists in its registry, and confirms that only the OWASP job's triggers changed. It reuses Task 10's scratch scripts in `$SCR/ci`. The registry checks are anonymous `HEAD` requests, with no pull.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git show "$(git merge-base main HEAD)":.gitlab-ci.yml > $SCR/ci/gitlab-main.yml
echo "== control: main's file"; python3 -I $SCR/ci/gl_images.py $SCR/ci/gitlab-main.yml /dev/null | tail -1
echo "== final"; python3 -I $SCR/ci/gl_images.py .gitlab-ci.yml $SCR/ci/refs.txt | tail -1
python3 -I $SCR/ci/digest.py $(cat $SCR/ci/refs.txt) > $SCR/ci/final-digests.txt; echo "digest exit=$? | pinned digests answering 200: $(command grep -c '| pinned sha256:[0-9a-f]*… 200 (' $SCR/ci/final-digests.txt)"
python3 -I $SCR/ci/gl_rules.py $SCR/ci/gitlab-main.yml .gitlab-ci.yml
```

Expected:

```text
== control: main's file
0 distinct pinned references; unpinned jobs: ['helm-lint', 'helm-template-kubeconform', 'security-audit', 'terraform-validate', 'nuget-audit', 'npm-audit-adminui', 'npm-audit-ts-sdk', 'govulncheck', 'owasp-dependency-check-java', 'pip-audit-python-sdk', 'pip-audit-agents', 'dotnet-build-test', 'typescript-sdk-build-test']
== final
7 distinct pinned references; unpinned jobs: none
digest exit=0 | pinned digests answering 200: 7
owasp-dependency-check-java {'merge_request_event': 'True -> False'}
12 of 13 jobs unchanged
owasp-dependency-check-java now runs on: ['schedule']
```

The control shows that the pin check can fail: all 13 jobs are unpinned on `main`, `govulncheck` included, because Task 8 pinned it. The final file parses, and Task 8's `golang:1.26@sha256:d772…` is one of its seven references. Across the branch, Tasks 7 and 8 changed only scripts and images, so the rule comparison against `main` gives Task 10's result.

- [ ] **Step 3: Final renders and the branch's commits.**

Run Task 12 Step 6's block unchanged. It runs `helm dependency build`, then renders all five profiles, with the three cloud profiles on their `*.ci-override.yaml` as CI does. It lints each profile, validates each render with kubeconform, lists each profile's first-party images, and runs the image scan's third-party filter. Expected: exactly Task 12 Step 6's output:
- every profile `template=0`, `1 chart(s) linted, 0 chart(s) failed` and `Invalid: 0, Errors: 0`, with Valid 92/69/98/98/98 for local/laptop/aws/azure/gcp;
- bare `iverson-*` names on `values-local` and `values-laptop`;
- `ghcr.io/counsellorben/…` names on the three cloud profiles;
- `first-party images through the third-party filter: 0`.

Then the branch's commits:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; cd $WT
git log --format='%h %s' main..HEAD
echo "commits: $(git rev-list --count main..HEAD)"
git log --format='%(trailers:key=Co-Authored-By,valueonly,separator=; )' main..HEAD | sort | uniq -c
git status --porcelain
```

Expected:
- Fourteen commits, one per task from Task 1 to Task 14. Newest first, the subjects are:
  - `document publishing, verification and branch protection for operators`
  - `add a script that requires the security scans on main`
  - `publish signed first-party images to ghcr`
  - `move the operator chart pins to a dependabot-tracked stub chart`
  - `pin ci images and helm, run deploy-validate on every pr and main, and drop the launcher image`
  - `move the admin console base to the patched nginx digest and source-map-js to 1.2.2`
  - `move the go sdk to go 1.26 and x/net v0.60.0 and pin govulncheck on gitlab`
  - `lock the python client and agent with hashed pip-compile requirements and pin the pip-audit jobs`
  - `move the .net clients from IdentityModel to Duende.IdentityModel`
  - `read the acting-user password from the environment instead of argv`
  - `neutralise the agent cli's terminal output and escape its prompt echoes`
  - `refuse plaintext network endpoints in loadtest and the conformance harness`
  - `refuse acting-user tokens on hand-built .net and python coordinators when the transport is unknown`
  - `check the java sdk's call credentials against the channel's transport at send time`
- `commits: 14`.
- One trailer line, `     14 Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Any other line means a commit carries a different or extra trailer: stop and report it.
- `git status --porcelain` prints nothing.

- [ ] **Step 4: Record the user's state, and build the LoadTest the pod runs.**
  - Check that the user's stack is down: `docker ps --format '{{.Names}}'`. If the list is not empty, **stop and ask the user**. Never stop their containers yourself: compose binds host port 9000, and kind binds 8080 and 8443.
  - Record their state, and publish LoadTest from the branch. The publish is framework-dependent for `linux-x64`. The pod runs it on the .NET runtime inside the branch's own `iverson-api` image, which Step 5 builds and loads into kind (`mcr.microsoft.com/dotnet/aspnet:10.0`, Ubuntu 24.04, with `sh`, `tar` and `sleep`). No other image is needed.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; mkdir -p $LIVE
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
cd $WT && rm -rf $LIVE/lt
flock -o $SCR/heavy.lock -c "systemd-run --user --scope -q -p MemoryMax=4G dotnet publish Iverson.Server/Iverson.LoadTest -c Release -r linux-x64 --self-contained false -o $LIVE/lt -p:UseSharedCompilation=false 2>&1 | command grep -E ' error | warning |Iverson.LoadTest -> ' | sed 's#$LIVE#\$LIVE#; s#$WT#\$WT#'; dotnet build-server shutdown >/dev/null 2>&1"
du -sh $LIVE/lt | sed "s#$LIVE#\$LIVE#"; python3 -c "import json; print(json.load(open('$LIVE/lt/Iverson.LoadTest.runtimeconfig.json'))['runtimeOptions']['framework'])"; ls $LIVE/lt | command grep -i identitymodel
```

Expected:

```text
  Iverson.LoadTest -> $WT/Iverson.Server/Iverson.LoadTest/bin/Release/net10.0/linux-x64/Iverson.LoadTest.dll
  Iverson.LoadTest -> $LIVE/lt/
41M	$LIVE/lt
{'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}
Duende.IdentityModel.dll
```

The publish needs only `Microsoft.NETCore.App` 10.0, which the `aspnet:10.0` base carries. `Duende.IdentityModel.dll` shows that Task 6's swap is in the build. `bin/` and `obj/` are gitignored.

- [ ] **Step 5: Kind: a fresh install of the branch chart.**

No install exists to upgrade, so this is a fresh install of the laptop profile, the same install sub-project C's verification ran. Ollama is off, because nothing here needs a model. `setup.sh` now reads the operator versions from Task 11's stub, so its run also exercises that reader against a real cluster. Run the slow commands with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
cd $WT; free -g | sed -n 2p
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh; echo "setup exit=$?"
helm list -A -o json | python3 -c 'import json, sys; print({r["name"]: r["chart"] for r in json.load(sys.stdin) if r["name"] in ("cnpg", "strimzi", "starrocks-operator")})'
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11d iverson
python3 -c "import yaml; d=yaml.safe_load(open('Iverson.Server/deploy/helm/iverson/values-laptop.yaml')); d.setdefault('ollama',{})['enabled']=False; yaml.safe_dump(d, open('$LIVE/laptop-no-ollama.yaml','w'))"
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr11d iverson --model-images --values $LIVE/laptop-no-ollama.yaml
cd $CH && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson --create-namespace -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr11d --set worker.image.tag=csr11d --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
ok=no; for i in $(seq 1 30); do [ "$(kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; from authentik.stages.authenticator_totp.models import AuthenticatorTOTPStage; print(Flow.objects.filter(slug='default-authentication-flow').count(), AuthenticatorTOTPStage.objects.filter(name='default-authenticator-totp-setup').count(), Flow.objects.filter(slug='initial-setup').count())" 2>/dev/null | tail -1)" = "1 1 1" ] && { ok=yes; break; }; sleep 10; done; echo "stock objects present: $ok"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/apply1.log 2>&1; echo "apply static exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml > $LIVE/apply2.log 2>&1; echo "apply templated exit=$?"
```

Expected (not replayed at plan time; this is the install sub-project C's verification ran):
- `setup exit=0`, and then `{'cnpg': 'cloudnative-pg-0.29.0', 'strimzi': 'strimzi-kafka-operator-1.1.0', 'starrocks-operator': 'operator-1.11.5'}`. These are the stub's pins (Task 11), installed by `setup.sh`.
- No `NOT READY` lines. Every probe is passing, including the HTTPS probes of the API and the tls-proxy.
- `stock objects present: yes`.
- `apply static exit=0` and `apply templated exit=0`. A worker at the laptop limits can be killed for memory (exit 137). Applying a blueprint is idempotent, so re-run that command (`docs/runbooks/csr11-identity-plane.md` §2).

- [ ] **Step 6: Kind: LoadTest in a debug pod mints through 8443, and the API accepts its tokens.**

This step follows the runbook note Task 3 Step 8 wrote (`docs/runbooks/kind-cluster-troubleshooting.md` §5.1):
- `IVERSON_GRPC_URL=https://iverson-api:8080`;
- `IVERSON_TOKEN_ENDPOINT=https://iverson-authentik:8443/application/o/token/`;
- `IVERSON_ACTING_USER_HOST_HEADER=iverson-authentik:8443`;
- `SSL_CERT_FILE` on one file holding both chart CAs, built with the note's own loop.

The NetworkPolicy allowances follow §2.6: the pod gets a distinct `app:` label, an egress policy for DNS and the two destination ports, and matching ingress allowances on the API and the Authentik server. Each allowance is its own `csr11d-*` policy beside the chart's. NetworkPolicies are additive, so this needs no edit to a Helm-owned object, and deleting the allowances restores the chart's set exactly. The step diffs that set before and after.

Identities come from the kind Secrets through `kubectl`, never from a `.env` file:
- **admin automation:** the client id, plus a ServiceAccount token minted for 10 minutes, as `docs/runbooks/csr11-identity-plane.md` §4 does;
- **the `loadtest-human` OAuth client:** its id;
- **the smoke-test and bypass users:** their passwords.

The tenant admin's password is random and never stored. Secrets reach the pod through stdin into files under its `emptyDir`, never on a command line. The env file on the host is deleted once it is copied.

The command is `write-path --type Tag --count 4 --concurrency 1`, run with `--target containers`, the default, which needs no Kafka settings:
- It provisions tenant `csr11d-live-check` through `CreateTenant`, with an admin-automation token minted at the 8443 token endpoint.
- It sets the tenant admin's password through the recovery link, logs the admin in through 8443, and registers the schemas.
- It posts four Tags. Each Post carries an acting-user token, minted through 8443 for the smoke-test or the bypass user (LoadTest picks one at random for each Post).
- `Tag` needs no seeded data. Postgres and Kafka point at the closed port 1 in the pod, so nothing after the post wave leaves the pod.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh; umask 077
cat > $LIVE/csr11d.yaml <<'X'
# CSR round-11 sub-project D live check: a throwaway LoadTest pod and the runbook §2.6 allowances it
# needs, as separate policies beside the chart's own, so removing them restores the chart's policy set.
apiVersion: v1
kind: Pod
metadata:
  name: csr11d-loadtest
  namespace: iverson
  labels:
    app: csr11d-loadtest
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  containers:
    - name: loadtest
      image: docker.io/library/iverson-api:csr11d
      imagePullPolicy: Never
      command: ["sleep", "7200"]
      env:
        - name: HOME
          value: /work/home
        - name: DOTNET_CLI_TELEMETRY_OPTOUT
          value: "1"
      resources:
        requests:
          cpu: 100m
          memory: 256Mi
        limits:
          memory: 1Gi
      volumeMounts:
        - name: work
          mountPath: /work
  volumes:
    - name: work
      emptyDir: {}
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: csr11d-loadtest-egress
  namespace: iverson
spec:
  podSelector:
    matchLabels:
      app: csr11d-loadtest
  policyTypes: [Egress]
  egress:
    - to:
        - namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: kube-system
          podSelector:
            matchLabels:
              k8s-app: kube-dns
      ports:
        - {protocol: UDP, port: 53}
        - {protocol: TCP, port: 53}
    - to:
        - podSelector:
            matchLabels:
              app: iverson-api
      ports:
        - {protocol: TCP, port: 8080}
    - to:
        - podSelector:
            matchLabels:
              app: iverson-authentik-server
      ports:
        - {protocol: TCP, port: 8443}
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: csr11d-api-ingress
  namespace: iverson
spec:
  podSelector:
    matchLabels:
      app: iverson-api
  policyTypes: [Ingress]
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: csr11d-loadtest
      ports:
        - {protocol: TCP, port: 8080}
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: csr11d-authentik-ingress
  namespace: iverson
spec:
  podSelector:
    matchLabels:
      app: iverson-authentik-server
  policyTypes: [Ingress]
  ingress:
    - from:
        - podSelector:
            matchLabels:
              app: csr11d-loadtest
      ports:
        - {protocol: TCP, port: 8443}
X
kubectl -n iverson get networkpolicy -o json | python3 -I -c 'import json, sys; print(json.dumps({i["metadata"]["name"]: i["spec"] for i in json.load(sys.stdin)["items"]}, indent=1, sort_keys=True))' > $LIVE/np-before.json
kubectl apply -f $LIVE/csr11d.yaml
kubectl -n iverson wait --for=condition=Ready pod/csr11d-loadtest --timeout=180s
# The runbook note's CA recipe, then the LoadTest build and the bundle, into the pod.
for s in iverson-api-internal-tls iverson-authentik-internal-tls; do
  kubectl get secret -n iverson "$s" -o jsonpath='{.data.ca\.crt}' | base64 -d
done > $LIVE/chart-cas.pem
echo "CAs in bundle: $(command grep -c 'BEGIN CERTIFICATE' $LIVE/chart-cas.pem)"
tar -C $LIVE/lt -cf - . | kubectl -n iverson exec -i csr11d-loadtest -- sh -c 'mkdir -p /work/lt /work/home && tar -C /work/lt -xf -'
kubectl -n iverson exec -i csr11d-loadtest -- sh -c 'cat > /work/chart-cas.pem' < $LIVE/chart-cas.pem
# The env file: the note's endpoints, and identities from the kind Secrets. Every value must be non-empty.
( sec() { kubectl -n iverson get secret "$1" -o jsonpath="{.data.$2}" | base64 -d; }
  export IVERSON_GRPC_URL=https://iverson-api:8080
  export IVERSON_TOKEN_ENDPOINT=https://iverson-authentik:8443/application/o/token/
  export IVERSON_ACTING_USER_HOST_HEADER=iverson-authentik:8443
  export SSL_CERT_FILE=/work/chart-cas.pem
  export IVERSON_CLIENT_ID="$(sec iverson-authentik-admin-automation-client client-id)"
  export IVERSON_CLIENT_ASSERTION_FILE=/work/sa-token
  export IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
  export IVERSON_ACTING_USER_CLIENT_ID="$(sec iverson-authentik-loadtest-human-client client-id)"
  export IVERSON_ACTING_USER_REDIRECT_URI=https://iverson.local/placeholder-callback
  export IVERSON_ACTING_USER_PASSWORD="$(sec iverson-authentik-smoke-test-user password)"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(sec iverson-authentik-bypass-user password)"
  export IVERSON_LOADTEST_TENANT_ID=csr11d-live-check
  export IVERSON_LOADTEST_TENANT_ADMIN_USERNAME=csr11d-admin
  export IVERSON_LOADTEST_TENANT_ADMIN_EMAIL=csr11d-admin@iverson.local
  export IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD="$(openssl rand -base64 24)aA1!"
  export IVERSON_POSTGRES_CS="Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"
  export IVERSON_KAFKA_BOOTSTRAP=127.0.0.1:1
  python3 -I - > $LIVE/csr11d.env <<'PY'
import os, shlex
names = ["IVERSON_GRPC_URL", "IVERSON_TOKEN_ENDPOINT", "IVERSON_ACTING_USER_HOST_HEADER", "SSL_CERT_FILE",
         "IVERSON_CLIENT_ID", "IVERSON_CLIENT_ASSERTION_FILE", "IVERSON_CLIENT_SCOPE",
         "IVERSON_ACTING_USER_CLIENT_ID", "IVERSON_ACTING_USER_REDIRECT_URI",
         "IVERSON_ACTING_USER_PASSWORD", "IVERSON_ACTING_USER_BYPASS_PASSWORD",
         "IVERSON_LOADTEST_TENANT_ID", "IVERSON_LOADTEST_TENANT_ADMIN_USERNAME",
         "IVERSON_LOADTEST_TENANT_ADMIN_EMAIL", "IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD",
         "IVERSON_POSTGRES_CS", "IVERSON_KAFKA_BOOTSTRAP"]
missing = [n for n in names if not os.environ.get(n)]
if missing:
    raise SystemExit(f"empty: {missing}")
print("\n".join(f"export {n}={shlex.quote(os.environ[n])}" for n in names))
PY
)
echo "env file exit=$? lines=$(wc -l < $LIVE/csr11d.env)"
kubectl -n iverson exec -i csr11d-loadtest -- sh -c 'umask 077; cat > /work/csr11d.env' < $LIVE/csr11d.env && rm -f $LIVE/csr11d.env
kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m \
  | kubectl -n iverson exec -i csr11d-loadtest -- sh -c 'umask 077; cat > /work/sa-token'
kubectl -n iverson exec csr11d-loadtest -- sh -c 'cd /work/lt && . /work/csr11d.env && exec dotnet Iverson.LoadTest.dll write-path --type Tag --count 4 --concurrency 1' > $LIVE/lt-positive.out 2>&1; echo "loadtest exit=$?"
command grep -E "provisioned|recovery link|Logged the tenant admin in|ready\.|Schemas registered|Post wave complete|field-rejections|rejected:|Post failed|Probing|Kafka consumer lag|Unhandled" $LIVE/lt-positive.out
echo "Unauthenticated in LoadTest output: $(command grep -c Unauthenticated $LIVE/lt-positive.out)"
echo "acting-user Posts the API accepted: $(kubectl -n iverson logs deploy/iverson-api -c api --since=30m | command grep -c 'acting as user .* called /iverson.ObjectPersistenceService/Post')"
```

Expected (not replayed at plan time; see the notes after this list for what was):
- `kubectl apply` prints `pod/csr11d-loadtest created` and three `networkpolicy.networking.k8s.io/csr11d-… created` lines. The wait prints `pod/csr11d-loadtest condition met`.
- `CAs in bundle: 2`, `env file exit=0 lines=17`.
- These lines, in this order:

  ```text
  Ensuring LoadTest tenant is provisioned...
  Set the tenant admin's password from CreateTenant's recovery link.
  Logged the tenant admin in.
  Tenant 'csr11d-live-check' ready.
  Schemas registered.
  [write-path] Post wave complete — 4 records in <t>s
  [write-path] Expected field-rejections (owner-restricted identity writing a bypass-only field): <n>
  ```

  Here `<n>` is the number of Posts LoadTest gave the smoke-test user. When `<n>` > 0, a `[write-path]   rejected: <detail>` line follows: the API's field-permission refusal of `Category`. No `Post failed` line appears.
- The tail depends on the random picks and is not gated:
  - If any Post went to the bypass user, `[write-path] Probing end-to-end visibility for <4 − n> keys...` follows, then an unhandled Npgsql connection error (Postgres is port 1), and a non-zero `loadtest exit`.
  - Otherwise `[write-path] Sampling Kafka consumer lag...` follows, the probe gives up on port 1, and `loadtest exit=0`.
- `Unauthenticated in LoadTest output: 0`.
- `acting-user Posts the API accepted: 4`.

What the gating lines prove:
- **`Tenant 'csr11d-live-check' ready.` and `Schemas registered.`** The API's default JwtBearer scheme accepted an Operator token minted at `https://iverson-authentik:8443/application/o/token/` with Host `iverson-authentik:8443`.
- **`Logged the tenant admin in.`** LoadTest drove the flow executor, the authorize endpoint and the token endpoint through 8443. Each TLS handshake was verified against the chart CA, under the name .NET derives from the Host header.
- **The four interceptor lines.** `ActingUserInterceptor` logs `service … acting as user … called …` only after the `ActingUser` scheme authenticates the token and the user's tenant is active. So the API accepted every acting-user token LoadTest minted through 8443.

Together these confirm spec Verified assumption 29 for both JwtBearer schemes. A `Post failed` line with a status other than `Unauthenticated` would not contradict that check, but it is unexpected (the same run passes on compose): report it.

What was checked at plan time:
- **Locally, with this branch's LoadTest build** (Tasks 2, 3 and 6 applied), against a loopback TLS stand-in whose certificate carries the 8443 certificate's SANs, with a two-CA `SSL_CERT_FILE` and the right CA second:
  - LoadTest's startup check accepts `https://iverson-api:8080`;
  - Host `iverson-authentik:8443` passes TLS, and the stand-in saw SNI `iverson-authentik` and that Host;
  - the default Host fails TLS (Step 7's control).
- **Against a `values-laptop.yaml` render:** the manifest is valid under kubeconform `-strict`, and its selectors match the chart's pod labels (`app: iverson-api`, `app: iverson-authentik-server`) and the services' ports (8080 and 8443, the same as their target ports).

- [ ] **Step 7: The negative control, then remove the throwaway resources.**

This step is the same pod, env and token, with `IVERSON_ACTING_USER_HOST_HEADER` unset, so LoadTest sends its old default Host, `authentik-server:9000` (spec Verified assumption 40). .NET takes the TLS name from the Host header, and that name is not on the 8443 certificate. So the first request, the admin-automation token request, fails TLS before anything is sent. The control also uses a fresh tenant id, though the run never reaches `CreateTenant`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
kubectl -n iverson exec csr11d-loadtest -- sh -c 'cd /work/lt && . /work/csr11d.env && unset IVERSON_ACTING_USER_HOST_HEADER && export IVERSON_LOADTEST_TENANT_ID=csr11d-negative && exec dotnet Iverson.LoadTest.dll write-path --type Tag --count 4 --concurrency 1' > $LIVE/lt-negative.out 2>&1; echo "loadtest exit=$?"
cat $LIVE/lt-negative.out
kubectl delete -f $LIVE/csr11d.yaml --wait=true
kubectl -n iverson get pod,networkpolicy -o name | command grep csr11d || echo "no csr11d resources left"
kubectl -n iverson get networkpolicy -o json | python3 -I -c 'import json, sys; print(json.dumps({i["metadata"]["name"]: i["spec"] for i in json.load(sys.stdin)["items"]}, indent=1, sort_keys=True))' > $LIVE/np-after.json
diff $LIVE/np-before.json $LIVE/np-after.json && echo "chart policies unchanged"
rm -f $LIVE/chart-cas.pem
```

Expected:
- `loadtest exit=1`, and the output:

  ```text
  Ensuring LoadTest tenant is provisioned...
  Tenant provisioning failed: Failed to acquire admin-automation token: The SSL connection could not be established, see inner exception.
  Is the Iverson API running, and does IVERSON_CLIENT_SCOPE include 'admin schema_admin'?
  command terminated with exit code 1
  ```

  The first three lines are the output of this branch's LoadTest build at plan time, against the loopback TLS stand-in (SNI `authentik-server`). The kubectl line was not replayed.
- The deletions print `pod "csr11d-loadtest" deleted` and the three `networkpolicy.networking.k8s.io "csr11d-…" deleted` lines.
- `no csr11d resources left`.
- `chart policies unchanged`.

The positive run used the same CA bundle, so this failure comes from the Host header alone. That is why the runbook note names `<release>-authentik:8443`.

- [ ] **Step 8: Tear down, and diff the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-d-sdd/env.sh
KIND_EXPERIMENTAL_PROVIDER=podman kind delete cluster --name iverson; KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters
docker rmi docker.io/library/iverson-api:csr11d localhost/iverson-api:csr11d >/dev/null 2>&1; true
docker images --format '{{.Repository}}:{{.Tag}}' | command grep -E ':csr11d$|iverson-tei-model:'
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
out="$(docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt -)"; [ -z "$out" ] && echo no-image-removed-or-repointed || printf 'CHANGED:\n%s\n' "$out"
echo "new images:"; docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -13 $LIVE/before-images.txt -
rm -rf $LIVE/lt
git -C $WT status --porcelain
```

Expected:
- `No kind clusters found`.
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`.
- `no-image-removed-or-repointed`. A `CHANGED:` block instead lists a pre-existing image that was removed or re-tagged: stop and report it.
- An empty `git status`.

Then remove whatever this task built:
- any `iverson-tei-model:*` image the grep lists;
- any untagged `<none>` image under "new images" that was created during this task's build window (check with `docker image inspect <id> --format '{{.Created}}'`).

Newly pulled base images (the kind node, Calico, the TEI and .NET bases) are acceptable; list them.

## Known issues inherited from spec

These exist in the implementation by design; they were accepted by the user during brainstorming and the design reviews. Inherited verbatim.

- **Admin bypass on `main`** (user decision 2026-10-12): `enforce_admins` stays false and no reviews are required; the maintainer's direct pushes skip the required checks.
- **OWASP dependency-check and kube-score `security-audit` stay optional** (user decisions 2026-10-12). kube-score reports 9–12 CRITICAL findings per profile (probes, ephemeral storage, read-only root FS, resources, PDBs, NetworkPolicy targets). With `deploy-validate` now running on `main` pushes, `security-audit` shows red there too.
- **No admission-time signature verification:** images are signed and attested, but clusters do not refuse unsigned images.
- **Go SDK consumers need Go ≥ 1.26.**
- **Hand-built .NET/Python coordinators** without an explicit identity or flag can no longer bind per-call acting tokens, or be constructed with an ambient one.
- **`OAuth2ClientCredentials` on plaintext** needs its own `allowInsecureCredentials=true` in addition to `IversonClient`'s opt-in (user decision 2026-10-12, CDR-1 §3.1 A).
- **Token endpoints that redirect:** LoadTest's and the harness's token clients follow redirects; the startup check sees only the configured URL.
- **First publish is unverified until pushed** (see §7).

