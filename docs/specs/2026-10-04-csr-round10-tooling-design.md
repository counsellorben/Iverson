# CSR Round 10 — Tooling Remediation Design (Sub-project E)

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`)

**Goal:** fix the tooling findings from CSR round 10:
- **#9:** LoadTest gives the tenant admin a public default password. This is the review's hard gate.
- **#20:** tool-side credential handling, in three parts:
  1. the agent CLI always opts in to insecure credentials;
  2. the TOTP cache is written before its mode is restricted;
  3. the conformance harness passes secrets on child-process argv.
- **#24:** per-call acting-user bindings skip the plaintext-channel guard in the .NET, Python and TypeScript SDKs.

The other round-10 findings belong to other sub-projects. Sub-project A, server authorization, is on branch `csr10-server-authz`.

**Global constraints**
- **One branch, one merge.** No server code, no proto change.
- **SDK and driver changes** are limited to what this spec names.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **Merge order:** sub-project A's branch changes comment lines in the same five driver files this work changes, in different hunks. Merge A before cutting this branch, or expect a trivial merge.

---

## 1. LoadTest (closes #9; #20 part 2)

**1a. The tenant-admin password comes from the environment.**
- In `Iverson.Server/Iverson.LoadTest/Program.cs:66`, `tenantAdminPassword` becomes `needsTenantAndSchema ? RequireEnv("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") : Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") ?? ""`. That is the shape of the two acting-user passwords at `:56-62`.
- The literal default `dev-only-not-for-production-tenant-admin-password-0123456789` is removed from `Program.cs`. The same string in `Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs:16` is a test-local constant and stays.
- `needsTenantAndSchema` is `seed`, `write-path`, `read-path`, `all`, `benchmark-ingest` and `benchmark-query` (`:34-35`). Those commands fail at startup through `RequireEnv` (`:345`) when the variable is unset. Passwordless commands are unaffected.

**1b. Enrol TOTP immediately after setting the password.**
- When this run set the password from CreateTenant's recovery link (`adminRecoveryLink is not null`), LoadTest calls `await tenantAdminTokenProvider.GetTokenAsync()` straight after constructing the provider (`:142-143`). `ActingUserTokenProvider.GetTokenAsync` runs `MintAsync` on first use, which completes the TOTP enrolment stage.
- When the tenant already exists, the token is still minted lazily, as today.
- If the first login fails, provisioning fails with the same guidance the recovery step gives at `:131-139`: the tenant now exists, so re-running will not retry this step.

**1c. The TOTP cache is created private (#20 part 2).**
- `AuthentikFlowExecutorClient.SaveCachedTotpSecret` (`Auth/AuthentikFlowExecutorClient.cs:127-134`):
  - creates `CacheDir` with `Directory.CreateDirectory(CacheDir, UserRead | UserWrite | UserExecute)`;
  - writes the secret through a `FileStream` with `FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UserRead | UserWrite }`, so the file never exists at a looser mode.
- The existing `File.SetUnixFileMode(0600)` after the write stays. A create mode applies only to new files, so this covers a pre-existing looser file.
- Every Unix-only call sits behind the existing `!OperatingSystem.IsWindows()` check. These APIs raise CA1416 otherwise. On Windows, behaviour is unchanged.
- This matches the Python sibling `deploy/scripts/mint_acting_user_token.py:143-145` (`os.open(..., 0o600)`).

**1d. kind host ports bind to loopback.** In `Iverson.Server/deploy/kind/kind-config.yaml`, both `extraPortMappings` entries (`containerPort` 80 → 8080, and 443 → 8443) gain `listenAddress: "127.0.0.1"`.

**1e. Docs.** In `Iverson.Server/Iverson.LoadTest/README.md:171`, the `IVERSON_LOADTEST_TENANT_ADMIN_USERNAME / _EMAIL / _PASSWORD` row changes:
- `_PASSWORD` is now required for the provisioning commands, with no default.
- The recovery-link password-policy note is kept.

## 2. Conformance-driver credentials (closes #20 part 3)

**2a. Harness** (`Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`):
- `BuildFlags` (`:303-343`) stops emitting `--client-secret`, `--service-token`, `--acting-token` and `--wrong-acting-token`.
- The driver process start passes those values in the child environment (`ProcessStartInfo.Environment`):

  | Variable | Value |
  |---|---|
  | `IVERSON_DRIVER_CLIENT_SECRET` | `context.ClientSecret ?? ""` |
  | `IVERSON_DRIVER_SERVICE_TOKEN` | `context.ServiceToken` |
  | `IVERSON_DRIVER_ACTING_TOKEN` | `context.ActingToken` |
  | `IVERSON_DRIVER_WRONG_ACTING_TOKEN` | `context.WrongActingToken` |

  - The wrong-acting token keeps its "always present, empty included" contract as an empty variable.
  - `RunProcessAsync` (`:378`) takes an optional environment dictionary. The driver run passes it; build steps don't.
- `--client-id`, `--token-endpoint` and every other non-secret flag stay on argv.
- The timeout message (`:423`) still prints the command and its arguments, which no longer contain any secret.

**2b. Drivers.** Each driver reads flags through a single `Args.optional(flag)` helper:

| Language | Helper |
|---|---|
| .NET | `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver` `Args.Optional` |
| Python | `Python/conformance/driver.py` `Args.optional`, `:127` |
| TypeScript | `TypeScript/conformance/driver.ts` `Args`, `:91` |
| Go | `Go/conformance/main.go` `optional` |
| Java | `Java/conformance/src/main/java/io/iverson/conformance/Driver.java` `Args.optional`, `:1164` |

- Each helper gains a fixed map from the four flag names to the four variables. When the flag is absent, it returns the variable's value, or the language's absent value if the variable is unset. When the flag is present, the flag wins, so a driver run by hand still works.
- No call site changes. Every secret read goes through the helper:
  - .NET `Program.cs:110-113, 912-915`;
  - Python `driver.py:484-488, 957`;
  - TypeScript `driver.ts:352-356, 725`;
  - Go `main.go:485-489, 871`;
  - Java `Driver.java:171-174, 659-662`.

**Effect:** while a run is in progress, `/proc/<pid>/cmdline` (mode 0444) no longer carries these secrets. `/proc/<pid>/environ` is mode 0400, readable by the owner only.

## 3. Agent CLI opt-in (closes #20 part 1)

In `Iverson.Agents/Python/iverson_agent/__main__.py`:
- Add `_is_loopback(host: str | None) -> bool`. It is true for `localhost` and for any address that `ipaddress.ip_address` parses as loopback (`127.0.0.0/8`, `::1`). It is false otherwise, including `None` and unparseable names.
- `main` computes `allow_insecure = _is_loopback(host) and _is_loopback(urlsplit(creds.token_endpoint).hostname)`. It passes that to both `IversonClient(...)` constructions (`:75`, `:79`) in place of the literal `True`. The CLI always builds credentials (`_credentials()`, `:21-24`), so the token endpoint is always part of the decision.
- The comment at `:71-73` is rewritten to describe the loopback rule.
- **Effect:** the compose default (`http://localhost:8080`, token endpoint on `localhost`) keeps working. Any non-loopback plaintext gRPC URL or token endpoint now gets the SDK's existing `ValueError` (`iverson_client/core.py:877-901`).
- The review's "split the SDK flag" suggestion is out of scope.

## 4. SDK per-call acting-user guard (closes #24)

Each client's constructor already refuses an acting-user token on a plaintext channel without the opt-in. The per-call binding now applies the same rule, using a bit the client computes once: plaintext && no opt-in.

| SDK | Where the bit lives | Who sets it | What checks it |
|---|---|---|---|
| .NET (`Iverson.Client.Core`) | `ActingUserIdentity` gains `bool refusesPlaintextTokens = false` and a `RefusesPlaintextTokens` property | `AddIversonClient` (`ServiceCollectionExtensions.cs:121`): plaintext endpoint (the `Uri.Scheme == http` test at `:59-61`) && `!allowInsecureChannelCallCredentials` | `EntityCoordinator.WithActingUser` (`EntityCoordinator.cs:41-45`) throws `InvalidOperationException` when `identity?.RefusesPlaintextTokens` is true |
| Python (`iverson_client/core.py`) | `IversonClient` stores `not use_tls and not allow_insecure_credentials` | `coordinator()` (`:951-953`) passes it to `EntityCoordinator` as a new keyword-only argument, `refuse_plaintext_token: bool = False` | `with_acting_user` (`:645-649`) raises `ValueError` when it is true |
| TypeScript (`src/core.ts`) | `IversonClient` stores a read-only `_refusesPlaintextTokens = !useTls && !allowInsecureCredentials` | its constructor (`:869-908`) | `withActingUser` (`:671-675`) throws an `Error` when `this._client._refusesPlaintextTokens` is true |

- **Messages:** each mirrors that SDK's existing constructor message: .NET `:63-68`, Python `:883-889`, TS `:890-894`.
- **Hand-built coordinators:** a .NET or Python coordinator built without the flag defaults to not refusing. That covers the conformance drivers and existing tests, whose builders own their channels.
- **TS test fakes:** fake clients built with `makeClientLike` lack the field, so they read as not refusing.
- **Unaffected callers:**
  - LoadTest's `WithActingUser` callers (`DirectSeeder.cs:121, 180, 258`; `WritePathRunner.cs:103, 113, 129`) run on a client built with `allowInsecureChannelCallCredentials: true` (`Program.cs:~168`).
  - Go and Java are unchanged. Their per-call token is sent only inside a per-RPC credential that already refuses plaintext without the opt-in: Go `OAuth2ClientCredentials`, `auth.go:31-56`; Java `OAuth2ClientCredentials.applyRequestMetadata`, `:64-77`, which a plaintext Java client can't hold without opting in.

## 5. Testing, docs and verification

**Mutation testing.** Task reviews mutation-test each new guard, and each must have a test that fails when the guard is removed or inverted:
- the `RequireEnv` gating;
- the immediate-mint call;
- the 0700 and 0600 create modes;
- `BuildFlags` not emitting the four flags, and the start info carrying the four variables;
- each driver helper's environment fallback;
- `_is_loopback` and the computed opt-in;
- each SDK's binding check.

**Tests per area:**
- **LoadTest** (`Iverson.LoadTest.Tests`):
  - the TOTP cache file is created 0600 in a 0700 directory, using a temp `HOME` (`UserProfile`);
  - the password gating, if `Program`'s top-level flow is testable as it stands; otherwise the live check covers it.
- **ClientConformance** (`Iverson.ClientConformance.Tests/DriverRunnerTests.cs`):
  - `BuildFlags_IncludesAllRequiredBaseFlags` is re-pinned;
  - new tests: none of the four flags is emitted, the environment carries the four variables, and the timeout message contains no secret value.
- **Drivers:** each helper reads the variable when the flag is missing, and the flag wins when present. Use each driver's existing test setup where it has one; otherwise rely on a driver build plus the live check.
- **Agent** (`Iverson.Agents/Python/tests/test_main.py`):
  - `_is_loopback` over `localhost`, `127.0.0.1`, `127.1.2.3`, `::1`, `example.com`, `10.0.0.5` and `None`;
  - the opt-in passed to the patched `IversonClient` is true for the compose defaults and false when either endpoint is remote.
- **SDKs:**
  - .NET `Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs` (or the `EntityCoordinator` identity tests): binding throws on plaintext without the opt-in, and binds with the opt-in and on TLS.
  - Python `tests/test_auth.py` / `tests/test_entity_coordinator.py`: the same three cases.
  - TS `tests/core.test.ts`: the same three cases.
  - Every existing SDK suite stays green. Run the Python suites as `.venv/bin/python -m pytest`, since the pytest shebang is stale.

**Docs** (`docs/runbooks/client-conformance-matrix.md:39-40`):
- Replace "Every `IVERSON_ACTING_USER_*` variable already has a working compose default … and needs no export" with the true statement. `IVERSON_ACTING_USER_BYPASS_PASSWORD` and `IVERSON_OTHER_TENANT_PASSWORD` are required, read from `.env`'s `IVERSON_BYPASS_PASSWORD` and `IVERSON_SMOKE_TEST_PASSWORD`, as the harness help at `Iverson.ClientConformance/Program.cs:330-334` says.
- Note that drivers now receive credentials through `IVERSON_DRIVER_*` environment variables.

**Live check before merge.** This runs on an isolated compose project:
- a copy of `docker-compose.yml` with every `container_name:` line stripped;
- its own project name, and an image tag other than `iverson-api`;
- snapshots of containers, volumes, networks and images before, and diffs after;
- secrets read from `.env` without printing them;
- teardown with `down -v`.

Checks:
1. With `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` set and a scratch `HOME`, `seed` provisions the tenant and the tenant admin's first login enrols TOTP during provisioning. A cache file appears under the scratch `HOME`.
2. With the variable unset, `seed` fails at startup with `RequireEnv`'s message.
3. `dotnet run -- --languages dotnet,python --scenarios identity` passes. While a driver runs, `/proc/<driver pid>/cmdline` shows none of the four secrets.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | Command-line arguments are readable by every local user; the environment is owner-only | Probe: a child started with `--client-secret S3CRET` and the same value in its environment. `/proc/<pid>/cmdline` is mode 0444 and shows `--client-secret S3CRET`; `/proc/<pid>/environ` is 0400, readable by the owner |
| 2 | LoadTest's `RequireEnv` exists, and the acting-user passwords use it gated by `needsTenantAndSchema` | `Program.cs:345` (`static string RequireEnv`), `:34-35`, `:56-62` |
| 3 | No script, runbook or CI job runs a LoadTest provisioning command | `grep` over `scripts/`, `Iverson.LoadTest/scripts`, `docs/runbooks`, `.github/workflows`: `stack.py` drives docker only; no workflow references LoadTest. The README documents manual `dotnet run -- …` |
| 4 | `ActingUserTokenProvider.GetTokenAsync` runs the full `MintAsync` login, including TOTP enrolment, on first use | `Auth/ActingUserTokenProvider.cs:10-36`; the enrolment branch was observed in CSR-10 sub-project A's live check |
| 5 | .NET 10 creates a directory at 0700 and a file at 0600 with `Directory.CreateDirectory(string, UnixFileMode)` and `FileStreamOptions.UnixCreateMode` | Probe (net10.0 console): the resulting modes were `UserExecute, UserWrite, UserRead` (directory) and `UserWrite, UserRead` (file). The calls raise CA1416, hence the `IsWindows` guard |
| 6 | A loopback-bound published port in WSL2 is reachable from the Windows host | Probe: a throwaway container published on `127.0.0.1:18080`. Windows `Test-NetConnection 127.0.0.1 -Port 18080` gave `True`, and `curl.exe` connected (empty reply from a non-HTTP service). Docker's port publish with a host IP is the mechanism kind's `listenAddress` uses. The container was removed |
| 7 | An empty environment variable reaches the child as empty in all five driver languages, and `ProcessStartInfo.Environment` applies | Probe: `XE=""` reads as `''` in Python, `""` in Node, `"" true` in Go (`LookupEnv`), `[]` in Java (empty, not null), and `''` in .NET. A .NET `ProcessStartInfo.Environment["CHILD_E"]=""` child saw `''`. `DriverRunner.cs:387` has `UseShellExecute = false` |
| 8 | Each driver launcher passes its environment to the driver | `DriverRunner.cs:124-147`: `dotnet run --no-build`, `python3`, `node`, the built Go binary and `java -jar` all inherit the parent environment by default |
| 9 | Every secret read in each driver goes through `Args.optional` | `grep` for the four flag names: only the call sites listed in §2b, plus comments |
| 10 | Nothing else echoes driver arguments | `grep` for `Join(' ', args)` and argument logging in `Iverson.ClientConformance`: only `DriverRunner.cs:423`, the timeout message |
| 11 | The agent always builds credentials, and its tests patch `IversonClient` | `__main__.py:21-24, 67`; `tests/test_main.py:37-47` (`monkeypatch.setattr(cli, "IversonClient", ...)`) |
| 12 | Adding the optional flag breaks no SDK caller | .NET `new ActingUserIdentity(` appears only at `ServiceCollectionExtensions.cs:121` and in tests using the default. Python `EntityCoordinator(` callers pass positional args (`core.py:953`, the driver, the tests). TS tests build coordinators on `makeClientLike` fakes. LoadTest's `WithActingUser` client opts in |
| 13 | Go and Java need no change | Go: the per-call token rides in `OAuth2ClientCredentials` (`auth.go:31-56`), whose transport-security requirement grpc-go enforces. Java: the per-call token is attached only in `OAuth2ClientCredentials.applyRequestMetadata` (`:64-77`), and `IversonClient.plaintext` refuses that credential without the opt-in |
| 14 | Each SDK has a test file covering its constructor guard | .NET `ServiceCollectionExtensionsTests.cs`; Python `tests/test_auth.py`; TS `tests/core.test.ts:170-180` |
| 15 | E's files overlap sub-project A's branch only in comment hunks of the five driver files | `git diff --name-only 3cb035f4 csr10-server-authz` intersected with E's files gives the five driver files only; A's hunks there are comment-only |
| 16 | Doc sites exist as stated | `Iverson.LoadTest/README.md:171`; `docs/runbooks/client-conformance-matrix.md:39-40` |

## Known issues / accepted as out of scope

- **TMA row F6 is not edited here.** `docs/security/tma.md` holds the user's uncommitted TMA refresh. The F6 correction, along with the others in the review's §7, belongs with committing that refresh. Decided by the user on 2026-10-04.
- **The SDK flag is not split** into separate plaintext-channel and plaintext-token-endpoint opt-ins. Once the agent CLI derives its opt-in from loopback endpoints, the combined flag is no longer misused.
- **A lost tenant-admin password is not recoverable by LoadTest.** If `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` changes after the tenant exists, the change has no effect, as today. The recovery-step guidance (`Program.cs:131-139`) explains how to reset it by hand.
