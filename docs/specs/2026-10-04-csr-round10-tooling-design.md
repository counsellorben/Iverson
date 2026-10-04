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
- In `Iverson.Server/Iverson.LoadTest/Program.cs:66`, `tenantAdminPassword` becomes `needsTenantAndSchema ? RequireEnv("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD", <message>) : Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") ?? ""`. That is the shape of the two acting-user passwords at `:56-62`.
- `RequireEnv` (`:345-349`) takes an optional message. This key's message says three things:
  - the operator chooses this password;
  - LoadTest sets it on a tenant it creates;
  - it must meet Authentik's recovery password policy: at least 8 characters, zxcvbn score ≥ 2 (`README.md:171`).

  The other keys keep the existing message, which points at `.env`. This password is in neither `.env` nor `generate-compose-secrets.sh`.
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
- When the file already exists, `File.SetUnixFileMode(0600)` runs **before** the write, and nothing changes the mode after the write. A new file therefore gets 0600 only from `UnixCreateMode`, which a test can observe. A pre-existing looser file is restricted before it receives the new secret.
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
- `main` opts in exactly when every plaintext leg is loopback. It computes `allow_insecure = (tls or _is_loopback(host)) and (token_url.scheme == "https" or _is_loopback(token_url.hostname))`, where `token_url = urlsplit(creds.token_endpoint)` and `tls` comes from `_endpoint()`. It passes that to both `IversonClient(...)` constructions (`:75`, `:79`) in place of the literal `True`. The CLI always builds credentials (`_credentials()`, `:21-24`), so the token endpoint is always part of the decision.
- The comment at `:71-73` is rewritten to describe the loopback rule.
- **Effect:**
  - The compose default (`http://localhost:8080`, token endpoint on `localhost`) keeps working.
  - A plaintext gRPC URL or an `http` token endpoint on a non-loopback host now gets the SDK's existing `ValueError` (`iverson_client/core.py:877-901`).
  - A TLS leg's host does not matter.
- The review's "split the SDK flag" suggestion is out of scope.

## 4. SDK per-call acting-user guard (closes #24)

Each client's constructor already refuses an acting-user token on a plaintext channel without the opt-in. The per-call binding now applies the same rule, using a bit the client computes once: plaintext && no opt-in.

| SDK | Where the bit lives | Who sets it | What checks it |
|---|---|---|---|
| .NET (`Iverson.Client.Core`) | `ActingUserIdentity` gains `bool refusesPlaintextTokens = false` and a `RefusesPlaintextTokens` property | `AddIversonClient` (`ServiceCollectionExtensions.cs:121`): plaintext endpoint (the `Uri.Scheme == http` test at `:59-61`) && `!allowInsecureChannelCallCredentials` | `EntityCoordinator.WithActingUser` (`EntityCoordinator.cs:41-45`) throws `InvalidOperationException` when `identity?.RefusesPlaintextTokens` is true |
| Python (`iverson_client/core.py`) | `IversonClient` stores `_refuse_plaintext_token = not use_tls and not allow_insecure_credentials` | `coordinator()` (`:951-953`) passes it to `EntityCoordinator` as a new keyword-only argument, `refuse_plaintext_token: bool = False` | `with_acting_user` (`:645-649`) raises `ValueError` when it is true |
| TypeScript (`src/core.ts`) | `IversonClient` stores a read-only `_refusesPlaintextTokens = !useTls && !allowInsecureCredentials` | its constructor (`:869-908`) | `withActingUser` (`:671-675`) throws an `Error` when `this._client._refusesPlaintextTokens` is true |

- **Messages:** each mirrors that SDK's existing constructor message: .NET `:63-68`, Python `:883-889`, TS `:890-894`.
- **Hand-built coordinators:** a .NET or Python coordinator built without the flag defaults to not refusing. That covers the conformance drivers and existing tests, whose builders own their channels.
- **Python driver catalog client:**
  - `_DriverSchemaCatalogClient.__init__` (`conformance/driver.py:426-429`) hand-reproduces the attributes `IversonClient.__init__` sets, and `test_driver_schema_catalog_client_reproduces_every_attribute_the_base_constructor_sets` (`tests/test_conformance_driver.py:101`) pins that.
  - It therefore also sets `_refuse_plaintext_token = False`. Its channel carries identity itself, and the driver uses it only for `get_schema` (`driver.py:556`).
- **TS test fakes:** fake clients built with `makeClientLike` lack the field, so they read as not refusing.
- **Unaffected callers:**
  - LoadTest's `WithActingUser` callers (`DirectSeeder.cs:121, 180, 258`; `WritePathRunner.cs:103, 113, 129`) run on a client built with `allowInsecureChannelCallCredentials: true` (`Program.cs:~168`).
  - Go and Java are unchanged. Their per-call token is sent only inside a per-RPC credential that already refuses plaintext without the opt-in: Go `OAuth2ClientCredentials`, `auth.go:31-56`; Java `OAuth2ClientCredentials.applyRequestMetadata`, `:64-77`, which a plaintext Java client can't hold without opting in.

## 5. Testing, docs and verification

**Mutation testing.** Task reviews mutation-test each new guard, and each must have a test that fails when the guard is removed or inverted:
- the `RequireEnv` gating;
- the immediate-mint call;
- the 0700 and 0600 create modes, and the pre-write chmod of an existing file;
- `BuildFlags` not emitting the four flags, and the start info carrying the four variables;
- each driver helper's environment fallback;
- `_is_loopback` and the computed opt-in;
- each SDK's binding check.

**Tests per area:**
- **LoadTest** (`Iverson.LoadTest.Tests`):
  - the TOTP cache file is created 0600 in a 0700 directory, using a temp `HOME` (`UserProfile`);
  - a pre-existing 0644 cache file ends at 0600 with the new content;
  - the password gating and the immediate mint live in `Program.cs`'s top-level statements, which no test in `Iverson.LoadTest.Tests` invokes. Live check 1 covers the mint, and live check 2 covers the gating.
- **ClientConformance** (`Iverson.ClientConformance.Tests/DriverRunnerTests.cs`):
  - three tests are re-pinned:
    - `BuildFlags_IncludesAllRequiredBaseFlags`;
    - `BuildFlags_CarriesTheWrongActingTokenForTheIdentityScenariosNegativeLeg` (`:111-119`) moves to the driver start environment, where `IVERSON_DRIVER_WRONG_ACTING_TOKEN` carries the token;
    - `BuildFlags_WithNoWrongActingTokenConfigured_StillEmitsTheFlagWithAnEmptyValue` (`:121-133`) moves there too: the variable is present and empty when `WrongActingToken` is empty;
  - new tests: none of the four flags is emitted, the environment carries the four variables, and the timeout message contains no secret value.
- **Drivers:** each helper reads the variable when the flag is missing, and the flag wins when present. Use each driver's existing test setup where it has one (Python and Go). .NET, TypeScript and Java have no driver test project.
  - **The fallback:** live check 3 covers it. Without the fallback, these drivers send no service credential (`driver.ts:363-371`; `DualHeaderCredentials.java:76-80`; .NET `Auth.cs:44-55`), and the identity scenario fails.
  - **"The flag wins":** check 3 cannot cover this half, because the harness passes no flag. Instead, run each of the three built drivers once by hand against a local gRPC endpoint that records the `authorization` metadata, with `--service-token` and `IVERSON_DRIVER_SERVICE_TOKEN` set to different values. The recorded header must carry the flag's value.
    - .NET and TypeScript use `--scenario identity --phase register`.
    - Java's identity scenario has no register phase, so it uses `--phase write --keys '{}'`.

    This needs no stack.
- **Agent** (`Iverson.Agents/Python/tests/test_main.py`):
  - `_is_loopback` over `localhost`, `127.0.0.1`, `127.1.2.3`, `::1`, `example.com`, `10.0.0.5` and `None`;
  - the opt-in passed to the patched `IversonClient`:
    - true for the compose defaults;
    - false whenever a plaintext leg is remote;
    - true for a loopback h2c gRPC URL with a remote `https` token endpoint;
    - true for a remote TLS gRPC URL with a loopback `http` token endpoint.
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
- teardown with `down -v`;
- one scratch `HOME` for every LoadTest and harness run (checks 1–3), so no run writes the real `~/.cache/iverson`:
  - On a fresh stack, the first login of each Authentik user overwrites that user's cached TOTP secret.
  - The harness's `TokenBroker` logs in the bypass user and the other-tenant user through the same cache.

  Toolchain caches stay pinned to the real home:
  - `NUGET_PACKAGES=<real-home>/.nuget/packages`;
  - `PYTHONUSERBASE=<real-home>/.local`;
  - `GOMODCACHE=<real-home>/go/pkg/mod`;
  - `MAVEN_OPTS=-Dmaven.repo.local=<real-home>/.m2/repository`;
  - `npm_config_cache=<real-home>/.npm`.

Checks:
1. Set `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD`, and point `IVERSON_POSTGRES_CS` at a closed port. `seed` then:
   1. provisions the tenant;
   2. registers schemas;
   3. fails at `DirectSeeder.RunAsync`'s first `pg.OpenAsync` (`Seeding/DirectSeeder.cs:37`), before any data-plane call.

   The scratch `HOME` must then hold `acting-user-totp-secret-compose-iverson-loadtest-tenant-admin.txt`, and no regular or bypass user's file. Schema registration authenticates with client credentials, so only the immediate mint can create that file.
2. Set both acting-user passwords, and leave `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` unset. `seed` exits non-zero before printing "Ensuring LoadTest tenant is provisioned..." (`Program.cs:108`), with §1a's message for this key.
3. `dotnet run -- --scenarios identity` passes. This covers all five languages, since `--languages` is omitted. While a driver runs, `/proc/<driver pid>/cmdline` shows none of the four secrets.

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
| 17 | The Python driver's catalog client mirrors `IversonClient`'s attribute set, and a parity test pins it | `conformance/driver.py:426-429` sets `_channel`, `_mapping_stub` and `_acting_user_token`. The test is at `tests/test_conformance_driver.py:101`. The client's only use is `get_schema` (`driver.py:556`). CDR-1 P1: adding the attribute to the base alone fails the test |
| 18 | Exactly three harness tests pin the secret flags on argv | `grep` of `Iverson.ClientConformance.Tests` for the four flags: `DriverRunnerTests.cs:100, 118, 129` assert argv; the other hits are `DriverContext` values. CDR-1 P2: `Failed: 3`, exactly these |
| 19 | LoadTest and the harness cache TOTP secrets under `UserProfile`, and enrolment overwrites the file | `AuthentikFlowExecutorClient.cs:117-121` (`CacheDir`), `:127-130` (`File.WriteAllText`), `:223-227` (enrolment). The harness uses it at `TokenBroker.cs:59-89`, with target `compose` and the default usernames. Toolchain caches on this machine: Python `grpc` in `~/.local`, `GOMODCACHE` `~/go/pkg/mod`, `~/.m2/repository`, `~/.nuget/packages` |
| 20 | Before `DirectSeeder`'s first Postgres open, only the immediate mint logs the tenant admin in, and the new `RequireEnv` runs before provisioning | `SchemaRegistrar` uses only the mapping client, which carries client credentials (`ServiceCollectionExtensions.cs:96`). The tenant-admin provider is attached only to persistence, retrieval and search. `DirectSeeder.cs:37` opens Postgres first, and the regular and bypass providers are first used at `:121`. The default username is `iverson-loadtest-tenant-admin` (`Program.cs:64`) with target `compose` (`:67`). `:56-66` run before `:108`. CDR-1 P14 and P15 |
| 21 | Only Python and Go have driver test homes, and omitting `--languages` runs all five | `Go/conformance/main_test.go` and `Python/tests/test_conformance_driver.py`. `flags.Languages ?? allLanguages` (`ClientConformance/Program.cs:61`). Without the fallback, TS attaches no service credential (`driver.ts:363-371`), Java's `hasServiceCredentials()` is false (`DualHeaderCredentials.java:76-80`), and .NET attaches neither the service token nor the client-id/secret trio (`Auth.cs:44-55`) |
| 22 | No stack file holds a tenant-admin password | The 11 `.env` key names and `scripts/generate-compose-secrets.sh` have no tenant-admin key (0 matches) |
| 23 | The Python SDK's two guards each test only their own leg | `core.py:877-901`: the channel guard fires only when `not use_tls`, the token guard only when the endpoint's scheme is not `https`. CDR-1 P11: across 16 combinations, the §3 rule refuses exactly the 7 with a non-loopback plaintext leg |
| 24 | The immediate mint is compatible with the provider's later use | `ActingUserTokenProvider.cs:10-40`: it returns the cached token until expiry, then refreshes, or re-mints using the cached TOTP secret. CDR-1 P16: the first data-plane use was served from the cache |
| 25 | Every driver helper treats an empty value as absent | .NET `Program.cs:1443-1444`, Python `driver.py:127-129`, TS `driver.ts:124-126` and Java `Driver.java:1164-1167` return null, `None` or `undefined` for `""`. Go `main.go:201-203` returns `""`, and its callers test `!= ""` |
| 26 | With the pre-write chmod and no post-write chmod, a missing `UnixCreateMode` is observable | net10.0 probe, umask 0022. The fix gives a new file 0600, and a pre-existing 0644 file ends at 0600 with the new content. Without `UnixCreateMode`, a new file is 0644. CDR-2 P1 shows the same through `MintAsync`: today's spec without the create mode still passes, and the fix without it fails the new-file test |
| 27 | A hand run with a recording endpoint tests "the flag wins" in the .NET, TS and Java drivers | .NET and TS have an identity `register` phase (`driver.ts:629`). Java's identity scenario has only `write` and `read` (`Driver.java:228-236`); any other phase exits 2. CDR-2 P9: with the flag and the variable set to different values, the correct helper recorded `Bearer FLAG` and a variable-first mutant recorded `Bearer ENV`, in all five drivers |

## Known issues / accepted as out of scope

- **TMA row F6 is not edited here.** `docs/security/tma.md` holds the user's uncommitted TMA refresh. The F6 correction, along with the others in the review's §7, belongs with committing that refresh. Decided by the user on 2026-10-04.
- **The SDK flag is not split** into separate plaintext-channel and plaintext-token-endpoint opt-ins. Once the agent CLI derives its opt-in from loopback endpoints, the combined flag is no longer misused.
- **A lost tenant-admin password is not recoverable by LoadTest.** If `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` changes after the tenant exists, the change has no effect, as today. The recovery-step guidance (`Program.cs:131-139`) explains how to reset it by hand.
