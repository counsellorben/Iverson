# CSR Round 10 — Tooling Remediation (Sub-project E) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-04-csr-round10-tooling-design.md` (commit SHA: `f7bd7609`)

**Goal:** close CSR round-10 findings #9 (LoadTest's public default tenant-admin password), #20 (tool-side credential handling: agent CLI opt-in, TOTP cache modes, driver secrets on argv) and #24 (the per-call acting-user binding skips the plaintext guard in the .NET, Python and TypeScript SDKs).

**Architecture:**
- **LoadTest** requires an operator-chosen tenant-admin password and logs the admin in as soon as the password is set. It writes its TOTP cache owner-only.
- **The conformance harness** passes the four driver secrets in the child environment instead of on argv. Each driver's `optional` helper falls back to those variables, and a flag given on the command line still wins.
- **The agent CLI** opts in to insecure credentials only when every plaintext connection is loopback.
- **The .NET, Python and TypeScript SDKs** refuse a per-call acting-user binding on a plaintext client that has not opted in.

**Tech stack:**
- .NET 10 (LoadTest, ClientConformance, `Iverson.Client.Core`), xUnit 2.9 and FluentAssertions;
- Python 3.14 with grpcio and pytest;
- TypeScript with vitest and `tsc`;
- Go 1.22;
- Java 21 with Maven.

---

## Global Constraints

Copied from the spec:
- **One branch, one merge.** No server code, no proto change.
- **SDK and driver changes** are limited to what the spec names.
- **Security write-ups** (comments, test names, docs) stay at the level of conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored**, so commit docs files with `git add -f`.
- **Merge order:** sub-project A's branch (`csr10-server-authz`) changes comment lines in the same five driver files, in different hunks. Merge A before cutting this branch. A's hunks change exactly as many lines as they remove (36/36), so every line number below is also correct after A merges. A test merge of this plan's proven code with A's branch is conflict-free.

Plan-wide:
- **Branch:** `csr10-tooling`, in the worktree `/home/ben/repositories/Iverson/.worktrees/csr10-tooling`, cut from **local** `main`:

  ```bash
  git -C /home/ben/repositories/Iverson worktree add .worktrees/csr10-tooling -b csr10-tooling main
  ```

  Local `main` is ahead of `origin`, so the base must be named explicitly.
- **One `dotnet` process at a time.** Use absolute paths, because the shell's working directory resets between tool calls.
- **Never read or write `~/.cache/iverson`.** The only tests that write a TOTP cache (Task 1) point `HOME` at a temp directory, and assert that before writing. The one exception to never listing it is Task 7 Step 6's `find -newer`, which lists its entries' metadata (allowed by the user, 2026-10-04).
- **Never start, stop, remove or modify the user's `iversonserver` containers, volumes, networks or images.** Never print a value from `Iverson.Server/.env`.
- **Commit messages:** lowercase, imperative, no prefix, as in `git log --oneline`. End each with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Stage specific paths only, never `-A`, and never anything under `.superpowers/`.
- **TypeScript:** a fresh worktree has no `node_modules`. Run `npm ci` in `Iverson.Clients/TypeScript` once, before Task 3.

## File Structure

**Modify:**
- `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs`: the TOTP cache is created owner-only (Task 1).
- `Iverson.Server/Iverson.LoadTest/Program.cs`: the required tenant-admin password, the immediate mint, and the shared failure guidance (Task 2).
- `Iverson.Server/Iverson.LoadTest/README.md`: the tenant-admin variable row (Task 2).
- `Iverson.Server/deploy/kind/kind-config.yaml`: loopback `listenAddress` on both port mappings (Task 2).
- The five driver `optional` helpers: the environment fallback for the four secret flags (Task 3).
  - `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`
  - `Iverson.Clients/Python/conformance/driver.py` (Task 6 also edits this file, in another hunk)
  - `Iverson.Clients/TypeScript/conformance/driver.ts`
  - `Iverson.Clients/Go/conformance/main.go`
  - `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java`
- `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`: secrets move off argv and into the driver's environment (Task 4).
- `docs/runbooks/client-conformance-matrix.md`: the two required passwords and the `IVERSON_DRIVER_*` note (Task 4).
- `Iverson.Agents/Python/iverson_agent/__main__.py`: the loopback opt-in (Task 5).
- The SDK per-call guard (Task 6):
  - `Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs`, `ServiceCollectionExtensions.cs` and `EntityCoordinator.cs`;
  - `Iverson.Clients/Python/iverson_client/core.py`;
  - `Iverson.Clients/TypeScript/src/core.ts`.

**Test:**
- `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs` (Task 1)
- `Iverson.Clients/Python/tests/test_conformance_driver.py` and `Iverson.Clients/Go/conformance/main_test.go` (Task 3)
- `Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs` and `NamingRejectedScenarioTests.cs` (Task 4; the latter gains only a `[Collection]` attribute)
- `Iverson.Agents/Python/tests/test_main.py` (Task 5)
- Task 6:
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`
  - `Iverson.Clients/Python/tests/test_auth.py`
  - `Iverson.Clients/TypeScript/tests/core.test.ts`

**Scratchpad only, never committed:** the gRPC recorder (Task 3) and the `/proc` cmdline watcher (Task 7).

## Inherited from spec

`thorough-brainstorming` verified these at spec-write time, and two critical design reviews re-checked them. They are not re-verified here. The evidence for each is in the spec's `Verified assumptions` table, at the same row number.

1. Command-line arguments are readable by every local user; the environment is owner-only.
2. LoadTest's `RequireEnv` exists, and the acting-user passwords use it, gated by `needsTenantAndSchema`.
3. No script, runbook or CI job runs a LoadTest provisioning command.
4. `ActingUserTokenProvider.GetTokenAsync` runs the full `MintAsync` login, including TOTP enrolment, on first use.
5. .NET 10 creates a directory at 0700 and a file at 0600 with `Directory.CreateDirectory(string, UnixFileMode)` and `FileStreamOptions.UnixCreateMode`.
6. A loopback-bound published port in WSL2 is reachable from the Windows host.
7. An empty environment variable reaches the child as empty in all five driver languages, and `ProcessStartInfo.Environment` applies.
8. Each driver launcher passes its environment to the driver.
9. Every secret read in each driver goes through `Args.optional`.
10. Nothing else echoes driver arguments.
11. The agent always builds credentials, and its tests patch `IversonClient`.
12. Adding the optional flag breaks no SDK caller.
13. Go and Java need no change.
14. Each SDK has a test file covering its constructor guard.
15. E's files overlap sub-project A's branch only in comment hunks of the five driver files.
16. The doc sites exist as stated.
17. The Python driver's catalog client mirrors `IversonClient`'s attribute set, and a parity test pins it.
18. Exactly three harness tests pin the secret flags on argv.
19. LoadTest and the harness cache TOTP secrets under `UserProfile`, and enrolment overwrites the file.
20. Before `DirectSeeder`'s first Postgres open, only the immediate mint logs the tenant admin in, and the new `RequireEnv` runs before provisioning.
21. Only Python and Go have driver test homes, and omitting `--languages` runs all five.
22. No stack file holds a tenant-admin password.
23. The Python SDK's two guards each test only their own leg.
24. The immediate mint is compatible with the provider's later use.
25. Every driver helper treats an empty value as absent.
26. With the pre-write chmod and no post-write chmod, a missing `UnixCreateMode` is observable.
27. A hand run with a recording endpoint tests "the flag wins" in the .NET, TypeScript and Java drivers.

## Verified plan-level assumptions

Verified at plan-write time against `main` at `f7bd7609`.
- **Proof branch:** every task's code below was applied on the scratch branch `planproof-csr10e`, and its tests were run there, mutants included. Each code block in this plan is that proven code.
- **Proof commits:**

  | Task | Commit |
  |---|---|
  | T1 | `d8a1445f` |
  | T2 | `8512af2c` |
  | T3 | `c587b6f7` |
  | T4 | `9192c16b` |
  | T5 | `3097314e` |
  | T6 | `cd5d4438` |

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | Every **Modify** and **Test** path above exists | Each one was edited in the proof branch. The runbook is tracked (`git ls-files docs/runbooks/client-conformance-matrix.md`) but gitignored, hence `git add -f` |
| 2 | Signature | `AuthentikFlowExecutorClient` has a public test constructor `(AuthentikIdentityConfig, ILogger<…>, HttpMessageHandler)` | `AuthentikFlowExecutorClient.cs:57-65`; the existing tests use it through `Client(FakeHandler)` |
| 3 | Signature | A first login that enrols TOTP makes these requests, in this order: GET flow (the TOTP stage, with `config_url`); POST code (response unread); GET flow (`xak-flow-redirect`); GET authorize (302, with `code` in `Location`); POST token (JSON with `access_token` and `expires_in`) | `AuthentikFlowExecutorClient.cs:157-301`, `:397-408`. The Task 1 tests drive exactly this sequence: `Passed: 8` |
| 4 | Signature | `Environment.GetFolderPath(UserProfile)` follows an in-process `HOME` change | The Task 1 tests assert it before writing, and passed |
| 5 | Signature | `RequireEnv` and the new `TenantExistsFailure` are static local functions at the end of `Program.cs`'s top-level statements | `Program.cs:345`; the proof build succeeded |
| 6 | Signature | `RunProcessAsync` is private static, and it has exactly two call sites: build (`:175`) and exec (`:206`) | `grep -n "RunProcessAsync(" DriverRunner.cs` |
| 7 | Signature | `DriverRunner(repoRoot)` and `RunPhaseAsync(Phase, IReadOnlyCollection<string>, DriverContext, ct)` are reachable from the tests. The Python spec runs `python3 conformance/driver.py` from `Iverson.Clients/Python`, with no build step. `ProcessTimeout` and `KeysByLanguage` are reachable | `DriverRunner.cs:130-133, 154-158, 389`; `InternalsVisibleTo` (`Iverson.ClientConformance.csproj:18`); the Task 4 tests passed |
| 8 | Signature | Phase documents are web-cased JSON: `language`, `phase`, `steps[].name`, `ok` and `keys` | `DriverProtocol.cs:42, 57-63`; `JsonSerializerDefaults.Web` (`DriverRunner.cs:438`); the fake driver's document parsed |
| 9 | Signature | The `optional` helpers and their absent values: .NET `string?` (`Program.cs:1443`), Python `Optional[str]` (`driver.py:127`), TS `string \| undefined` (`driver.ts:124`), Go `string` (`main.go:201`), Java `String`/`null` (`Driver.java:1164`). `os` is imported in Python (`:14`) and Go (`:19`), and Java's `Map` is imported (`:48`) | The cited lines; all five builds and the Python and Go tests passed |
| 10 | Signature | `ActingUserIdentity` is a primary-constructor class, the Python `EntityCoordinator.__init__` takes `(entity_class, channel, acting_user_token=None)`, and the TS `IversonClient` constructor takes `(host, port, useTls, callCredentials, actingUserToken, allowInsecureCredentials)` | `ActingUserIdentity.cs:7`; `core.py:625-630`; `core.ts:866-873` |
| 11 | Signature | `main` in the agent has `tls` and `creds` in scope before both `IversonClient(...)` constructions | `__main__.py:67-68, 75, 79` |
| 12 | Command | `dotnet test <project>.csproj [--filter …]` for LoadTest.Tests, ClientConformance.Tests and Client.Core.Tests | Run in the proof: 121/121, 648/648, 79/79. Those were on pre-A `main`; after A's merge ClientConformance.Tests is 650/650, since A adds two tests there and none to the other suites |
| 13 | Command | The Python SDK suite is `python3 -m pytest`, run from `Iverson.Clients/Python`. That directory has no `.venv`, and the cwd's `iverson_client` is the one imported | Run: 241 passed; `iverson_client.__file__` resolved to the worktree |
| 14 | Command | The agent suite runs from the worktree's `Iverson.Agents/Python` with `/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python -m pytest`. That venv exists only in the main checkout; `iverson_agent` resolves from the cwd, and the venv's `iverson_client` is main's, which Task 5's tests don't exercise because they patch `IversonClient` | Run: 68 passed; `iverson_agent.__file__` resolved to the worktree |
| 15 | Command | TS: `npm test` (`tsc -p tsconfig.test.json`, then `vitest run`), after `npm ci`. Go: `go test ./conformance/` and `go test ./...`. Driver builds use `DriverRunner`'s own commands (`DriverRunner.cs:124-147`) | Run: TS 287 passed with type-checking clean; Go ok; all five drivers built |
| 16 | Command | The commit convention is lowercase imperative with no prefix | `git log --oneline -10` |
| 17 | Ordering | Task 3 needs nothing from Task 4. Task 4 needs Task 3, or a run between them would hand secrets to drivers that can't read them | The proof applied T3 before T4; each step's tests passed |
| 18 | Ordering | Tasks 3 and 6 both edit `driver.py`, in different hunks; Tasks 1, 2, 5 and 6 are otherwise independent | The proof commits applied in sequence without conflict |
| 19 | Code | The Unix-mode APIs compile in LoadTest inside `if/else` on `OperatingSystem.IsWindows()` with no CA1416 warning. The tests' `File.GetUnixFileMode`/`SetUnixFileMode` calls need `[UnsupportedOSPlatform("windows")]` to stay warning-free | Proof build: no CA1416 in LoadTest. The tests warned until the attribute was added |
| 20 | Code | The Python generic-handler recorder works with the installed grpcio. With the flag and the variable both set, the .NET, TS and Java drivers record `Bearer FLAG`; with only the variable, `Bearer ENV` | Proof hand runs (Task 3 Step 5): dotnet/ts/java with the flag → `auth='Bearer FLAG'`; with the variable only → `auth='Bearer ENV'` |
| 21 | Code | A Python `IversonClient(use_tls=True)` and a TS `IversonClient(..., true)` construct without connecting, so the TLS cases run offline | Task 6 tests passed with no server |
| 22 | Consumer | The `RequireEnv` change: both existing callers (`Program.cs:57, :61`) still compile, and `TokenBroker.RequireEnv` is a separate function | `git grep -n "RequireEnv("` |
| 23 | Consumer | Every caller of the per-call binding outside tests: `Iverson.Agents/Python/iverson_agent/session.py`; LoadTest's `DirectSeeder`, `WritePathRunner`, `ReadPathScenario`, `BenchmarkIngestScenario` and `BenchmarkQueryScenario`; the TS driver (`driver.ts:376, 724`, opt-in `true`); the Python driver (hand-built coordinators); .NET `SchemaCatalogClient` (its own `WithActingUser`, out of scope). None binds on a plaintext, un-opted client except where the constructor already raises: LoadTest's client opts in (`Program.cs:166`). The agent's computed opt-in is false for a plaintext connection only when that connection is non-loopback, and then the constructor raises first, because credentials are always present | `git grep -l -e with_acting_user -e "WithActingUser(" -e "withActingUser("`; every affected suite passed |
| 24 | Consumer | No tracked file other than the spec uses an `IVERSON_DRIVER_*` name | `git grep -l IVERSON_DRIVER_` → the spec only |
| 25 | Consumer | `DriverRunnerTests.Context()` gains `ServiceToken: "service-token"`, and every other test that uses `Context()` still passes | ClientConformance.Tests 650/650 after A's merge (648/648 before) |
| 26 | Live | LoadTest's tenant provisioning needs the `admin` scope (`TenantLifecycleGrpcService` → `RequireAuthorization("Operator")`, `Iverson.Api/Program.cs:742`; the admin scope arm of `OperatorAuthorizationPolicy`). Only `iverson-admin-automation` carries it (`compose-only/service-clients.yaml:122-136`). Its secret is `.env`'s `IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET` | The cited lines; the `.env` key names (values never read) |
| 27 | Live | The compose file has exactly two `    image: iverson-api` lines (`:449`, `:561`) and 19 `container_name:` lines, and publishes host ports on 127.0.0.1 (5432, 8080, 9000, …), so the isolated stack cannot run while the user's stack is up | `grep -n` of `docker-compose.yml`; `docker ps` was empty at plan-write time |
| 28 | Live | The `/proc` watcher flags secret flags, JWT-shaped arguments and the client-secret value on driver command lines (those carrying `--scenario`), prints only the PID and the pattern name, and records the languages it saw | Proof run against dummy processes: `HIT … pattern=secret-flag`, `jwt`, `client-secret-value`; `languages seen` listed each marker |
| 29 | Live | Task 7 reproduces as written, apart from the two corrections it now carries (the Step 2 health poll and the Step 6 control) | 2026-10-04 run of Steps 1–6 on `planproof-csr10e` merged with A, as the user chose (CIR-1 §3.2 B). Step 1 `grep -c` → `2`. Step 2 `READY` (3rd poll); the API was `healthy` via `docker inspect`, and `$DC ps` showed no health. Check 2: exit 134, counts `0` and `1`. Check 1: exit 134 (Postgres refused on port 1); the four lines in order; only `acting-user-totp-secret-compose-iverson-loadtest-tenant-admin.txt`; modes `700` and `600`. Check 3: `harness exit=0`, identity `ok` in all five languages, `hits: 0`, `languages seen` all five. Step 6 control: 17 containers, 9 volumes, `csr10tool_default`. After `down -v`, the container, volume and network diffs are empty; `iverson-api` kept its IDs; one `<none>` image added; `find` printed nothing |
| 30 | Code | Task 1's Windows branch behaves as `main`'s Windows path did. It was accepted on forced-branch equivalence and not run on a Windows host, by the user's decision (CIR-1 §3.3 A, 2026-10-04) | CIR-1 P28: forcing the Windows condition in both the plan's code and `main`'s gave identical directory, file, modes, content and file list |
| 31 | Ordering | No test whose result depends on its process finishing runs under Task 4's 2 s `ProcessTimeout` | CIR-2 P10/P11: 13 processes start per suite run (8 `dotnet build`, 3 NamingRejected fixtures, 2 `DriverRunnerTests` fakes); only NamingRejected NR2 and `DriverRunnerTests`' own end-to-end test fail when their driver is cut off, and the `dotnet build` members' tests pass even when timed out. With `DriverRunnerTests` and `NamingRejectedScenarioTests` in one `[Collection]`, no NamingRejected driver runs under the deadline: CIR-2 P12 (650 ×3, A merged), and a re-run on `planproof-csr10e` on 2026-10-04 (648 ×2, pre-A) |
| 32 | Live | `env.sh`'s `val` reads the gitignored `.env` through this shell's `grep` wrapper | CIR-1 P14; the 2026-10-04 live run authenticated checks 1–3 with values read through `val` |
| 33 | Live | Task 7's health poll and control counts rest on the compose file, and the counts were measured against A's tip | `iverson-api` has a `healthcheck:` at `docker-compose.yml:516` (inside its `:445-556` block). Only `reranker` (`:163`) and `tgi` (`:209`) are profiled, so 17 services and 9 volumes start (CIR-2 G4 lists all 19 and 11). `git rev-parse csr10-server-authz` → `85cb030e`, the tip rows 12, 25 and 29 were measured against |

## Tasks

### Task 1: The TOTP cache is created owner-only (spec §1c)

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:127-134` (`SaveCachedTotpSecret`)
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`

- [ ] **Step 1: Write the two tests**

In `AuthentikFlowExecutorClientTests.cs`, add `using System.Runtime.Versioning;` after `using System.Net;`. Then add these members at the end of the class, after `SetPasswordFromRecoveryLinkAsync_Throws_WhenTheRedirectsEndAnywhereButCompletion`. They stay in this class so that they run serially with the class's other tests while `HOME` is redirected.

```csharp
    // A first login that enrols TOTP: the enrolment stage, the code POST, completion, then the
    // authorize redirect and the token exchange. MintAsync caches the enrolment secret on the way.
    private static FakeHandler EnrolmentLogin() => new(
        Json("""{"component":"ak-stage-authenticator-totp","config_url":"otpauth://totp/iverson?secret=JBSWY3DPEHPK3PXP"}"""),
        Json("{}"),
        Json("""{"component":"xak-flow-redirect","to":"/"}"""),
        new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://localhost/placeholder-callback?code=c1&state=s1") },
        },
        Json("""{"access_token":"at","refresh_token":"rt","expires_in":300}"""));

    // Runs the test with HOME pointed at a fresh directory, so the TOTP cache is written there and
    // never to the real ~/.cache/iverson. Asserted before anything runs: if UserProfile did not
    // follow HOME, the test would write the developer's own cache.
    private static async Task WithTempHome(Func<string, Task> test)
    {
        var home = Directory.CreateTempSubdirectory("iverson-totp-home-").FullName;
        var previous = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Should().Be(home);
            await test(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previous);
            Directory.Delete(home, recursive: true);
        }
    }

    private static string CachePath(string home) =>
        Path.Combine(home, ".cache", "iverson", "acting-user-totp-secret-compose-iverson-loadtest-tenant-admin.txt");

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task MintAsync_CreatesTheTotpCacheFileOwnerOnly_InAnOwnerOnlyDirectory()
    {
        await WithTempHome(async home =>
        {
            await Client(EnrolmentLogin()).MintAsync();

            File.GetUnixFileMode(Path.Combine(home, ".cache", "iverson"))
                .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(CachePath(home)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.ReadAllText(CachePath(home)).Should().Be("JBSWY3DPEHPK3PXP\n");
        });
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task MintAsync_RestrictsAPreExistingLooserCacheFile_AndReplacesItsContent()
    {
        await WithTempHome(async home =>
        {
            Directory.CreateDirectory(Path.Combine(home, ".cache", "iverson"));
            File.WriteAllText(CachePath(home), "OLD\n");
            File.SetUnixFileMode(CachePath(home),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            await Client(EnrolmentLogin()).MintAsync();

            File.GetUnixFileMode(CachePath(home)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.ReadAllText(CachePath(home)).Should().Be("JBSWY3DPEHPK3PXP\n");
        });
    }
```

- [ ] **Step 2: Run the tests and watch the new-file test fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Server
dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj --filter "FullyQualifiedName~AuthentikFlowExecutorClientTests"
```

Expected: `MintAsync_CreatesTheTotpCacheFileOwnerOnly_InAnOwnerOnlyDirectory` fails on the directory mode, because today's directory picks up the umask's group and other bits. The pre-existing-file test passes, because today's post-write chmod restricts that file. Task 1's Step 4 mutants show it catches the change.

- [ ] **Step 3: Replace `SaveCachedTotpSecret`**

Replace the whole method (`AuthentikFlowExecutorClient.cs:127-134`):

```csharp
    private void SaveCachedTotpSecret(string secret)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(CachePath, secret + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogInformation("Cached new TOTP secret for future runs at {Path}", CachePath);
    }
```

with:

```csharp
    // The secret is a long-lived second factor, so neither the directory nor the file may ever
    // exist at a mode another local user can read. A create mode applies only to a file this call
    // creates, so a file left at a looser mode by an older build is restricted BEFORE the new
    // secret goes into it. Nothing changes the mode after the write: a test can then see the
    // create mode itself, which a chmod after the write would mask.
    private void SaveCachedTotpSecret(string secret)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CachePath, secret + "\n");
        }
        else
        {
            Directory.CreateDirectory(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(CachePath))
                File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var stream = new FileStream(CachePath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(secret + "\n");
        }
        logger.LogInformation("Cached new TOTP secret for future runs at {Path}", CachePath);
    }
```

The Windows branch is today's behaviour, minus the Unix-only chmod it never ran there.

- [ ] **Step 4: Run the tests, then the mutants**

Run Step 2's command. Expected: `Passed: 8`, with no CA1416 warning from either project.

Then check that each guard has a test that catches its removal, restoring the file after each:
1. Delete the `UnixCreateMode = …` line. The new-file test fails: the file comes out 0644 under umask 0022.
2. Make the pre-write chmod unreachable (`if (false && File.Exists(CachePath))`). The pre-existing-file test fails: the file stays 0644.
3. Replace the directory call with `Directory.CreateDirectory(CacheDir)`. The new-file test fails on the directory mode.

The proof run reproduced all three.

- [ ] **Step 5: Run the whole LoadTest suite**

```bash
dotnet test Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj
```

Expected: `Passed: 121`.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs
git commit -m "create the TOTP cache owner-only instead of restricting it after the write

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: The tenant-admin password and the immediate mint (spec §1a, §1b, §1d, §1e)

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs`, at `:66` (the password), `:130-144` (the recovery step's `catch` and the provider) and `:345-349` (`RequireEnv`)
- Modify: `Iverson.Server/deploy/kind/kind-config.yaml:12-17`
- Modify: `Iverson.Server/Iverson.LoadTest/README.md:171`

These changes are in top-level statements and config, which no test reaches. Per spec §5, live checks 1 and 2 (Task 7) cover the gating and the immediate mint.

- [ ] **Step 1: Require the tenant-admin password**

Replace `Program.cs:66`:

```csharp
var tenantAdminPassword = Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") ?? "dev-only-not-for-production-tenant-admin-password-0123456789";
```

with:

```csharp
// CSR round-10 finding #9: no default. A literal default would be the same public password on every
// tenant LoadTest creates, set through CreateTenant's recovery link below.
var tenantAdminPassword = needsTenantAndSchema
    ? RequireEnv("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD",
        "Missing required environment variable 'IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD' -- choose a password " +
        "for the LoadTest tenant's admin. LoadTest sets it on a tenant it creates, through CreateTenant's " +
        "recovery link, so it must meet Authentik's recovery password policy (at least 8 characters, " +
        "zxcvbn score >= 2).")
    : Environment.GetEnvironmentVariable("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD") ?? "";
```

- [ ] **Step 2: Give `RequireEnv` an optional message, and share the "tenant now exists" guidance**

Replace `Program.cs:345-349`:

```csharp
static string RequireEnv(string key) =>
    Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException(
        $"Missing required environment variable '{key}' -- the docker-compose stack's Authentik " +
        "dev-only passwords are now randomly generated per stack by scripts/generate-compose-secrets.sh; " +
        "read the value out of Iverson.Server/.env.");
```

with:

```csharp
static string RequireEnv(string key, string? message = null) =>
    Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException(message ??
        $"Missing required environment variable '{key}' -- the docker-compose stack's Authentik " +
        "dev-only passwords are now randomly generated per stack by scripts/generate-compose-secrets.sh; " +
        "read the value out of Iverson.Server/.env.");

// CreateTenant already succeeded, so the next run finds the tenant and skips the step that just
// failed. Say so now, or the operator only learns it from that run's failing logins.
static InvalidOperationException TenantExistsFailure(Exception ex, string tenantAdminUsername) => new(
    $"{ex.Message} The tenant now exists, so re-running LoadTest will not retry this step. " +
    "Set the tenant admin's password by hand, for example in " +
    "`docker exec -it iverson-authentik-worker ak shell`: " +
    $"u = User.objects.get(username=\"{tenantAdminUsername}\"); u.set_password(\"<password>\"); u.save()",
    ex);
```

- [ ] **Step 3: Log the tenant admin in as soon as the password is set**

Replace `Program.cs:130-144`:

```csharp
            catch (Exception ex)
            {
                // CreateTenant already succeeded, so the next run finds the tenant and skips this step.
                // Say so now, or the operator only learns it from that run's failing logins.
                throw new InvalidOperationException(
                    $"{ex.Message} The tenant now exists, so re-running LoadTest will not retry this step. " +
                    "Set the tenant admin's password by hand, for example in " +
                    "`docker exec -it iverson-authentik-worker ak shell`: " +
                    $"u = User.objects.get(username=\"{tenantAdminUsername}\"); u.set_password(\"<password>\"); u.save()",
                    ex);
            }
            Console.WriteLine("Set the tenant admin's password from CreateTenant's recovery link.");
        }
        tenantAdminTokenProvider = new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            tenantAdminIdentity, tenantAdminLoggerFactory.CreateLogger<AuthentikFlowExecutorClient>()));
```

with:

```csharp
            catch (Exception ex)
            {
                throw TenantExistsFailure(ex, tenantAdminUsername);
            }
            Console.WriteLine("Set the tenant admin's password from CreateTenant's recovery link.");
        }
        tenantAdminTokenProvider = new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            tenantAdminIdentity, tenantAdminLoggerFactory.CreateLogger<AuthentikFlowExecutorClient>()));
        if (adminRecoveryLink is not null)
        {
            // Log in now, while this run has just set the password: the first login completes the
            // tenant admin's TOTP enrolment, so the account is never left with a password and an
            // unclaimed second-factor enrolment until some later data-plane call.
            try
            {
                await tenantAdminTokenProvider.GetTokenAsync();
            }
            catch (Exception ex)
            {
                throw TenantExistsFailure(ex, tenantAdminUsername);
            }
            Console.WriteLine("Logged the tenant admin in, enrolling its TOTP device.");
        }
```

The surrounding `try`/`catch` (`:109`, `:147-152`) still prints "Tenant provisioning failed: …" and returns 1, for this failure as for the recovery step's.

- [ ] **Step 4: Bind kind's host ports to loopback**

In `kind-config.yaml`, replace:

```yaml
      - containerPort: 80
        hostPort: 8080
        protocol: TCP
      - containerPort: 443
        hostPort: 8443
        protocol: TCP
```

with:

```yaml
      # Bound to loopback, so the dev cluster's ingress is reachable only from this machine
      # (the default publishes it on every host interface).
      - containerPort: 80
        hostPort: 8080
        listenAddress: "127.0.0.1"
        protocol: TCP
      - containerPort: 443
        hostPort: 8443
        listenAddress: "127.0.0.1"
        protocol: TCP
```

- [ ] **Step 5: Update the README row**

Replace `README.md:171` in full with:

```markdown
| `IVERSON_LOADTEST_TENANT_ADMIN_USERNAME` / `_EMAIL` / `_PASSWORD` | the tenant-admin login LoadTest mints and uses as the data-plane gRPC channel's credential. `_USERNAME` and `_EMAIL` have dev-only defaults. `_PASSWORD` has no default and is required by `seed`, `write-path`, `read-path`, `all`, `benchmark-ingest` and `benchmark-query`: choose it yourself. When LoadTest creates the tenant, it sets `_PASSWORD` as that admin's password through CreateTenant's one-time recovery link and logs the admin in at once, enrolling its TOTP device, so the value must pass Authentik's recovery password policy (at least 8 characters, zxcvbn score ≥ 2). Changing it after the tenant exists has no effect. |
```

- [ ] **Step 6: Build, and check that nothing else is left behind**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Server
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
git -C /home/ben/repositories/Iverson/.worktrees/csr10-tooling grep -n "dev-only-not-for-production-tenant-admin" -- ':!docs'
python3 -c "import yaml; print(yaml.safe_load(open('deploy/kind/kind-config.yaml'))['nodes'][0]['extraPortMappings'])"
```

Expected:
- The build succeeds.
- The `grep` finds only the test-local constant, `Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs:17` (`:16` before Task 1 added its `using`).
- The YAML prints both mappings with `'listenAddress': '127.0.0.1'`.

- [ ] **Step 7: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/README.md Iverson.Server/deploy/kind/kind-config.yaml
git commit -m "require the LoadTest tenant-admin password, log the admin in at once, and bind kind to loopback

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: The drivers read the secret flags from the environment (spec §2b)

**Files:**
- Modify the five `optional` helpers:
  - .NET `Program.cs:1443-1444`
  - Python `driver.py:99` and `:127-129`
  - TypeScript `driver.ts:90-91` and `:124-127`
  - Go `main.go:201-203`
  - Java `Driver.java:1164-1167`
- Test: `Iverson.Clients/Python/tests/test_conformance_driver.py`, `Iverson.Clients/Go/conformance/main_test.go`
- Hand check (not committed): a recorder in the scratchpad, for .NET, TypeScript and Java

The rule, in every language: a flag that is present wins, even when empty. Otherwise, a secret flag reads its variable. Then the existing rule applies: empty means absent. No call site changes.

- [ ] **Step 1: Write the Python and Go tests**

In `tests/test_conformance_driver.py`, replace:

```python
import grpc

from conformance.driver import _DriverSchemaCatalogClient, entity_to_dict
```

with:

```python
import grpc
import pytest

from conformance.driver import Args, _DriverSchemaCatalogClient, entity_to_dict
```

and append to the end of the file:

```python


# ── Args.optional's environment fallback for the secret flags ──────────────────────────────────

_SECRET_FLAGS = [
    ("--client-secret", "IVERSON_DRIVER_CLIENT_SECRET"),
    ("--service-token", "IVERSON_DRIVER_SERVICE_TOKEN"),
    ("--acting-token", "IVERSON_DRIVER_ACTING_TOKEN"),
    ("--wrong-acting-token", "IVERSON_DRIVER_WRONG_ACTING_TOKEN"),
]


@pytest.mark.parametrize("flag,variable", _SECRET_FLAGS)
def test_optional_reads_a_secret_flags_variable_when_the_flag_is_absent(monkeypatch, flag, variable):
    monkeypatch.setenv(variable, "ENV")
    assert Args([]).optional(flag) == "ENV"


@pytest.mark.parametrize("flag,variable", _SECRET_FLAGS)
def test_optional_prefers_a_secret_flag_given_on_the_command_line(monkeypatch, flag, variable):
    monkeypatch.setenv(variable, "ENV")
    assert Args([flag, "FLAG"]).optional(flag) == "FLAG"


@pytest.mark.parametrize("flag,variable", _SECRET_FLAGS)
def test_optional_treats_an_unset_or_empty_secret_variable_as_absent(monkeypatch, flag, variable):
    monkeypatch.delenv(variable, raising=False)
    assert Args([]).optional(flag) is None
    monkeypatch.setenv(variable, "")
    assert Args([]).optional(flag) is None


def test_optional_never_reads_the_environment_for_a_non_secret_flag(monkeypatch):
    monkeypatch.setenv("IVERSON_DRIVER_CLIENT_ID", "ENV")
    assert Args([]).optional("--client-id") is None
```

Append to `conformance/main_test.go`:

```go

// The harness passes the secret flags in the environment rather than on the command line; a flag
// given on the command line still wins, so a driver run by hand keeps working.
func TestOptionalSecretFlagEnvironmentFallback(t *testing.T) {
	secretFlags := map[string]string{
		"--client-secret":      "IVERSON_DRIVER_CLIENT_SECRET",
		"--service-token":      "IVERSON_DRIVER_SERVICE_TOKEN",
		"--acting-token":       "IVERSON_DRIVER_ACTING_TOKEN",
		"--wrong-acting-token": "IVERSON_DRIVER_WRONG_ACTING_TOKEN",
	}
	for flag, variable := range secretFlags {
		t.Run(flag, func(t *testing.T) {
			t.Setenv(variable, "ENV")
			if got := parseArgs(nil).optional(flag); got != "ENV" {
				t.Errorf("flag absent: got %q, want %q", got, "ENV")
			}
			if got := parseArgs([]string{flag, "FLAG"}).optional(flag); got != "FLAG" {
				t.Errorf("flag present: got %q, want %q", got, "FLAG")
			}
			t.Setenv(variable, "")
			if got := parseArgs(nil).optional(flag); got != "" {
				t.Errorf("variable empty: got %q, want empty", got)
			}
		})
	}
}

func TestOptionalNeverReadsTheEnvironmentForANonSecretFlag(t *testing.T) {
	t.Setenv("IVERSON_DRIVER_CLIENT_ID", "ENV")
	if got := parseArgs(nil).optional("--client-id"); got != "" {
		t.Errorf("got %q, want empty", got)
	}
}
```

Run them and watch them fail:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Clients/Python && python3 -m pytest -q tests/test_conformance_driver.py
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Clients/Go && go test ./conformance/
```

Expected:
- **Python:** the four `…when_the_flag_is_absent` cases fail. The flag-wins, unset/empty and non-secret cases already pass today.
- **Go:** `TestOptionalSecretFlagEnvironmentFallback` fails on "flag absent".

- [ ] **Step 2: Add the fallback to all five helpers**

**.NET.** In `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs`, replace:

```csharp
        public string? Optional(string flag) =>
            _values.TryGetValue(flag, out var value) && value.Length > 0 ? value : null;
```

with:

```csharp
        // The secret flags the harness passes in the environment instead of on the command line,
        // which every local user can read. A flag given on the command line still wins, so a
        // driver run by hand keeps working.
        private static readonly Dictionary<string, string> SecretFlagVariables = new(StringComparer.Ordinal)
        {
            ["--client-secret"] = "IVERSON_DRIVER_CLIENT_SECRET",
            ["--service-token"] = "IVERSON_DRIVER_SERVICE_TOKEN",
            ["--acting-token"] = "IVERSON_DRIVER_ACTING_TOKEN",
            ["--wrong-acting-token"] = "IVERSON_DRIVER_WRONG_ACTING_TOKEN",
        };

        public string? Optional(string flag)
        {
            var value = _values.TryGetValue(flag, out var fromFlag) ? fromFlag
                : SecretFlagVariables.TryGetValue(flag, out var variable) ? Environment.GetEnvironmentVariable(variable) ?? string.Empty
                : string.Empty;
            return value.Length > 0 ? value : null;
        }
```

**Python.** In `Iverson.Clients/Python/conformance/driver.py`, insert immediately before `class Args:` (`:99`):

```python
# The secret flags the harness passes in the environment instead of on the command line, which
# every local user can read. A flag given on the command line still wins, so a driver run by hand
# keeps working.
_SECRET_FLAG_VARIABLES = {
    "--client-secret": "IVERSON_DRIVER_CLIENT_SECRET",
    "--service-token": "IVERSON_DRIVER_SERVICE_TOKEN",
    "--acting-token": "IVERSON_DRIVER_ACTING_TOKEN",
    "--wrong-acting-token": "IVERSON_DRIVER_WRONG_ACTING_TOKEN",
}


```

and replace:

```python
    def optional(self, flag: str) -> Optional[str]:
        value = self._values.get(flag, "")
        return value if value else None
```

with:

```python
    def optional(self, flag: str) -> Optional[str]:
        if flag in self._values:
            value = self._values[flag]
        else:
            value = os.environ.get(_SECRET_FLAG_VARIABLES[flag], "") if flag in _SECRET_FLAG_VARIABLES else ""
        return value if value else None
```

**TypeScript.** In `Iverson.Clients/TypeScript/conformance/driver.ts`, insert immediately before the line ``/** Minimal `--flag value` parser, mirroring the .NET/Python drivers' `Args`. */`` (`:90`):

```typescript
/** The secret flags the harness passes in the environment instead of on the command line, which
 *  every local user can read. A flag given on the command line still wins, so a driver run by hand
 *  keeps working. */
const SECRET_FLAG_VARIABLES = new Map<string, string>([
    ['--client-secret', 'IVERSON_DRIVER_CLIENT_SECRET'],
    ['--service-token', 'IVERSON_DRIVER_SERVICE_TOKEN'],
    ['--acting-token', 'IVERSON_DRIVER_ACTING_TOKEN'],
    ['--wrong-acting-token', 'IVERSON_DRIVER_WRONG_ACTING_TOKEN'],
]);

```

and replace:

```typescript
    optional(flag: string): string | undefined {
        const value = this.values.get(flag);
        return value ? value : undefined;
    }
```

with:

```typescript
    optional(flag: string): string | undefined {
        const variable = SECRET_FLAG_VARIABLES.get(flag);
        const value = this.values.has(flag)
            ? this.values.get(flag)
            : variable !== undefined ? process.env[variable] : undefined;
        return value ? value : undefined;
    }
```

**Go.** In `Iverson.Clients/Go/conformance/main.go`, replace:

```go
func (a args) optional(flag string) string {
	return a.values[flag]
}
```

with:

```go
// secretFlagVariables names the secret flags the harness passes in the environment instead of on
// the command line, which every local user can read. A flag given on the command line still wins,
// so a driver run by hand keeps working.
var secretFlagVariables = map[string]string{
	"--client-secret":      "IVERSON_DRIVER_CLIENT_SECRET",
	"--service-token":      "IVERSON_DRIVER_SERVICE_TOKEN",
	"--acting-token":       "IVERSON_DRIVER_ACTING_TOKEN",
	"--wrong-acting-token": "IVERSON_DRIVER_WRONG_ACTING_TOKEN",
}

func (a args) optional(flag string) string {
	if v, ok := a.values[flag]; ok {
		return v
	}
	if variable, ok := secretFlagVariables[flag]; ok {
		return os.Getenv(variable)
	}
	return ""
}
```

**Java.** In `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java`, inside `private static final class Args`, replace:

```java
        String optional(String flag) {
            String value = values.get(flag);
            return value == null || value.isEmpty() ? null : value;
        }
```

with:

```java
        // The secret flags the harness passes in the environment instead of on the command line,
        // which every local user can read. A flag given on the command line still wins, so a
        // driver run by hand keeps working.
        private static final Map<String, String> SECRET_FLAG_VARIABLES = Map.of(
            "--client-secret", "IVERSON_DRIVER_CLIENT_SECRET",
            "--service-token", "IVERSON_DRIVER_SERVICE_TOKEN",
            "--acting-token", "IVERSON_DRIVER_ACTING_TOKEN",
            "--wrong-acting-token", "IVERSON_DRIVER_WRONG_ACTING_TOKEN");

        String optional(String flag) {
            String value = values.containsKey(flag) ? values.get(flag)
                : SECRET_FLAG_VARIABLES.containsKey(flag) ? System.getenv(SECRET_FLAG_VARIABLES.get(flag))
                : null;
            return value == null || value.isEmpty() ? null : value;
        }
```

- [ ] **Step 3: Run the Python and Go tests, then their mutants**

Run Step 1's commands. Expected: Python `17 passed`, Go `ok`.

Mutants (restore each afterwards):
1. **The fallback removed** (Python: `value = ""` in the `else` branch; Go: `return ""` in place of `return os.Getenv(variable)`). Python: 4 failed. Go: `FAIL`.
2. **The variable outranks the flag** (Python: `if flag in self._values and flag not in _SECRET_FLAG_VARIABLES:`; Go: swap the two `if` blocks). Python: 4 failed. Go: `FAIL`.

- [ ] **Step 4: Build the .NET, TypeScript and Java drivers**

Use the harness's own build commands (`DriverRunner.cs:124-147`):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj
(cd Iverson.Clients/TypeScript && npx tsc -p tsconfig.conformance.json)
mvn -q -B -f Iverson.Clients/Java/pom.xml -pl conformance -am -DskipTests package
```

Expected: all three succeed, and `Iverson.Clients/Java/conformance/target/iverson-conformance-driver.jar` exists. (`npm ci` must have run once in `Iverson.Clients/TypeScript`; see Global Constraints.)

- [ ] **Step 5: Hand-check "the flag wins" in the .NET, TypeScript and Java drivers (spec §5)**

None of these three has a driver test project, and the harness never passes these flags, so this check has to be run by hand. Write the recorder to the session scratchpad (`$SP` below). Do not commit it.

`$SP/recorder.py`:

```python
"""Logs each gRPC call's authorization header, then fails the call. Prints its port first."""
from concurrent import futures

import grpc


class Recorder(grpc.GenericRpcHandler):
    def service(self, details):
        metadata = dict(details.invocation_metadata)
        print(f"{details.method} auth={metadata.get('authorization')!r}", flush=True)
        return grpc.unary_unary_rpc_method_handler(
            lambda request, context: context.abort(grpc.StatusCode.UNIMPLEMENTED, "recorder"))


server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
server.add_generic_rpc_handlers((Recorder(),))
port = server.add_insecure_port("127.0.0.1:0")
server.start()
print(f"PORT {port}", flush=True)
server.wait_for_termination()
```

Run everything in one block, because shell variables do not persist between tool calls:

```bash
SP=<session scratchpad>; BR=/home/ben/repositories/Iverson/.worktrees/csr10-tooling
python3 $SP/recorder.py >> $SP/rec.log 2>&1 & REC=$!
sleep 2; PORT=$(awk '/^PORT/{print $2}' $SP/rec.log)
COMMON="--scenario identity --type IdentityDoc --tenant t1 --grpc http://127.0.0.1:$PORT --client-id cid --token-endpoint http://127.0.0.1:1/application/o/token/ --acting-token ACT --owner-id o1 --id-prefix p-"
export IVERSON_DRIVER_SERVICE_TOKEN=ENV
: > $SP/rec.log; (cd $BR && dotnet run --no-build --project Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver -- $COMMON --phase register --service-token FLAG --out $SP/o1.json >/dev/null 2>&1); echo "dotnet exit=$?"; cat $SP/rec.log
: > $SP/rec.log; (cd $BR/Iverson.Clients/TypeScript && node dist-conformance/conformance/driver.js $COMMON --phase register --service-token FLAG --out $SP/o2.json >/dev/null 2>&1); echo "ts exit=$?"; cat $SP/rec.log
: > $SP/rec.log; (cd $BR && java -jar Iverson.Clients/Java/conformance/target/iverson-conformance-driver.jar $COMMON --phase write --keys '{}' --service-token FLAG --out $SP/o3.json >/dev/null 2>&1); echo "java exit=$?"; cat $SP/rec.log
: > $SP/rec.log; (cd $BR/Iverson.Clients/TypeScript && node dist-conformance/conformance/driver.js $COMMON --phase register --out $SP/o4.json >/dev/null 2>&1); echo "ts env-only exit=$?"; cat $SP/rec.log
kill $REC
```

Expected:
- Each run exits 0.
- **Flag and variable both set** (the first three runs): every recorded line reads `auth='Bearer FLAG'`. The .NET and TypeScript runs record `RegisterSchema`. Java records only `ObjectPersistenceService/Post`, because its identity `write` phase registers no schema.
- **Variable only** (the last run): `auth='Bearer ENV'`.

**Mutant check, for the task reviewer:** make the variable outrank a present flag in any one of the three helpers and rebuild it. That driver's first run then records `Bearer ENV`.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs Iverson.Clients/Python/conformance/driver.py Iverson.Clients/Python/tests/test_conformance_driver.py Iverson.Clients/TypeScript/conformance/driver.ts Iverson.Clients/Go/conformance/main.go Iverson.Clients/Go/conformance/main_test.go Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java
git commit -m "let the conformance drivers read their secrets from the environment

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: The harness passes secrets in the environment (spec §2a, plus the runbook)

**Files:**
- Modify: `Iverson.Server/Iverson.ClientConformance/DriverRunner.cs`, at `:25-26` (the `DriverContext` comment), `:206` (the exec call), `:319-335` (`BuildFlags`), before `:349` (the new `BuildEnvironment`) and `:378-390` (`RunProcessAsync`)
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs`
- Test: `Iverson.Server/Iverson.ClientConformance.Tests/NamingRejectedScenarioTests.cs:9` (a `[Collection]` attribute only)
- Modify: `docs/runbooks/client-conformance-matrix.md:39-40`

**Interfaces:**
- Consumes: Task 3's driver fallback. The drivers must read `IVERSON_DRIVER_*` before the harness stops sending the flags.
- Produces: `internal static Dictionary<string, string> DriverRunner.BuildEnvironment(DriverContext)`.

- [ ] **Step 1: Write the tests**

First, put the timeout test's class and `NamingRejectedScenarioTests` in one xUnit collection, so no test whose driver must finish runs while the timeout test holds `ProcessTimeout` at 2 s (row 31). Add `[Collection("driver-process-timeout")]` on the line above `public class DriverRunnerTests` (`DriverRunnerTests.cs:7`), and above `public class NamingRejectedScenarioTests` (`NamingRejectedScenarioTests.cs:9`). Both files already have `using Xunit;`.

Then, in `DriverRunnerTests.cs`:

1. In `Context()` (`:9-20`), add `ServiceToken: "service-token",` between `IdPrefix: "s1-",` and `WrongActingToken: "wrong-acting-token");`.
2. Replace `BuildFlags_IncludesAllRequiredBaseFlags`'s assertion, and the two tests after it (`:98-133`, from `flags.Should().Contain([` up to the end of `BuildFlags_WithNoWrongActingTokenConfigured_StillEmitsTheFlagWithAnEmptyValue`), with:

```csharp
        flags.Should().Contain(["--scenario", "s1", "--phase", "read", "--type", "Widget",
            "--tenant", "iverson-loadtest-dynamic", "--grpc", "http://localhost:5000",
            "--client-id", "client-id", "--token-endpoint", "http://localhost:9000/application/o/token/",
            "--owner-id", "owner-id", "--id-prefix", "s1-", "--out", "/tmp/out.json"]);
    }

    // Every local user can read a process's command line, so no secret may ride on it.
    [Theory]
    [InlineData("--client-secret", "client-secret")]
    [InlineData("--service-token", "service-token")]
    [InlineData("--acting-token", "acting-token")]
    [InlineData("--wrong-acting-token", "wrong-acting-token")]
    public void BuildFlags_CarriesNoSecretFlagOrValue(string flag, string value)
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(Phase.Read, "go", Context(), "/tmp/out.json");

        flags.Should().NotContain(flag);
        flags.Should().NotContain(value);
    }

    [Theory]
    [InlineData("IVERSON_DRIVER_CLIENT_SECRET", "client-secret")]
    [InlineData("IVERSON_DRIVER_SERVICE_TOKEN", "service-token")]
    [InlineData("IVERSON_DRIVER_ACTING_TOKEN", "acting-token")]
    [InlineData("IVERSON_DRIVER_WRONG_ACTING_TOKEN", "wrong-acting-token")]
    public void BuildEnvironment_CarriesEachSecret(string variable, string value) =>
        DriverRunner.BuildEnvironment(Context()).Should().Contain(variable, value);

    /// <summary>
    /// S8 identity's negative leg is the only thing that reads this value, but every driver
    /// invocation carries it: the environment is built once for all phases and all scenarios, and a
    /// driver that never needs it ignores it. It must be set even when empty, so a value the harness
    /// itself inherited never reaches the driver in its place.
    /// </summary>
    [Fact]
    public void BuildEnvironment_CarriesTheWrongActingTokenForTheIdentityScenariosNegativeLeg() =>
        DriverRunner.BuildEnvironment(Context())
            .Should().Contain("IVERSON_DRIVER_WRONG_ACTING_TOKEN", "wrong-acting-token");

    [Fact]
    public void BuildEnvironment_WithNoWrongActingTokenConfigured_StillSetsTheVariableEmpty() =>
        DriverRunner.BuildEnvironment(Context() with { WrongActingToken = string.Empty })
            .Should().Contain("IVERSON_DRIVER_WRONG_ACTING_TOKEN", string.Empty);

    // A stand-in for the Python driver: the harness runs `python3 conformance/driver.py` from
    // Iverson.Clients/Python under the repo root, with no build step. Under a temporary repo root
    // this script is what runs, so RunPhaseAsync is exercised end to end without a stack.
    private static string FakePythonDriverRepo(string script)
    {
        var root = Directory.CreateTempSubdirectory("iverson-conformance-fake-").FullName;
        var conformance = Path.Combine(root, "Iverson.Clients", "Python", "conformance");
        Directory.CreateDirectory(conformance);
        File.WriteAllText(Path.Combine(conformance, "driver.py"), script);
        return root;
    }

    [Fact]
    public async Task RunPhaseAsync_StartsTheDriverWithEachSecretInItsEnvironment()
    {
        // Reports each variable back as a step key, "<unset>" when the driver did not receive it.
        var root = FakePythonDriverRepo("""
            import json, os, sys
            out = sys.argv[sys.argv.index("--out") + 1]
            names = ["IVERSON_DRIVER_CLIENT_SECRET", "IVERSON_DRIVER_SERVICE_TOKEN",
                     "IVERSON_DRIVER_ACTING_TOKEN", "IVERSON_DRIVER_WRONG_ACTING_TOKEN"]
            keys = {name: os.environ.get(name, "<unset>") for name in names}
            with open(out, "w") as f:
                json.dump({"language": "python", "phase": "register",
                           "steps": [{"name": "env", "ok": True, "keys": keys}]}, f)
            """);
        try
        {
            var runner = new DriverRunner(root);

            var outcomes = await runner.RunPhaseAsync(Phase.Register, ["python"], Context());

            outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Success>();
            runner.KeysByLanguage["python"].Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["IVERSON_DRIVER_CLIENT_SECRET"] = "client-secret",
                ["IVERSON_DRIVER_SERVICE_TOKEN"] = "service-token",
                ["IVERSON_DRIVER_ACTING_TOKEN"] = "acting-token",
                ["IVERSON_DRIVER_WRONG_ACTING_TOKEN"] = "wrong-acting-token",
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The timeout message prints the driver's command line, so it must not carry a secret either.
    [Fact]
    public async Task RunPhaseAsync_TimeoutMessage_CarriesNoSecret()
    {
        var root = FakePythonDriverRepo("import time\ntime.sleep(60)\n");
        var previous = DriverRunner.ProcessTimeout;
        DriverRunner.ProcessTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var outcomes = await new DriverRunner(root).RunPhaseAsync(Phase.Register, ["python"], Context());

            var broken = outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Broken>().Subject;
            broken.Stderr.Should().Contain("timed out");
            foreach (var secret in new[] { "client-secret", "service-token", "acting-token", "wrong-acting-token" })
                broken.Stderr.Should().NotContain(secret);
        }
        finally
        {
            DriverRunner.ProcessTimeout = previous;
            Directory.Delete(root, recursive: true);
        }
    }
```

`ProcessTimeout` is static. The only other test class whose driver must finish, `NamingRejectedScenarioTests`, shares this class's xUnit collection (above), so it never runs while the timeout is 2 s. Other processes that can start inside the window are `dotnet build` runs, and their tests pass even when timed out. The value is restored in `finally`.

The suite does not compile yet, because `BuildEnvironment` does not exist. That is the expected red.

- [ ] **Step 2: Change `DriverRunner`**

1. **The `DriverContext` comment** (`:25-26`). Replace:

   ```csharp
       // other scenario — and emitted as an empty value rather than omitted, so a driver's
       // positional `--flag value` parser never mis-pairs the flags that follow it.
   ```

   with:

   ```csharp
       // other scenario — and still set, as an empty variable, so a value the harness itself
       // inherited can never reach the driver (see DriverRunner.BuildEnvironment).
   ```

2. **The exec call** (`:206`). Replace:

   ```csharp
               var execResult = await RunProcessAsync(ResolveCommand(spec.ExecCommand, cwd), execArgs, cwd, ct);
   ```

   with:

   ```csharp
               var execResult = await RunProcessAsync(
                   ResolveCommand(spec.ExecCommand, cwd), execArgs, cwd, ct, BuildEnvironment(context));
   ```

   The build call at `:175` is unchanged and passes no environment.

3. **`BuildFlags`** (`:319-335`). Replace:

   ```csharp
               "--client-id", context.ClientId ?? string.Empty,
               "--client-secret", context.ClientSecret ?? string.Empty,
               "--token-endpoint", context.TokenEndpoint ?? string.Empty,
               // A pre-minted service token, and the only one the drivers should ever use. The
               // client-credentials trio above is left in the contract for a driver run by hand,
               // but a driver that mints its own token cannot produce a usable one here: Authentik
               // stamps the JWT's `iss` from the request's Host header, so a token fetched from
               // localhost carries an issuer the API rejects outright (401), and none of the five
               // drivers passes a scope, so even an accepted token would lack `schema_admin` (403
               // on RegisterSchema). The orchestrator already mints this correctly once, with both
               // the Host header and the scope, so it hands the result over rather than having
               // five languages each re-derive Authentik's issuer semantics.
               "--service-token", context.ServiceToken,
               "--acting-token", context.ActingToken,
               // Always emitted, empty included — see DriverContext.WrongActingToken.
               "--wrong-acting-token", context.WrongActingToken,
               "--owner-id", context.OwnerId,
   ```

   with:

   ```csharp
               // The secret half of each credential travels in the environment instead — see
               // BuildEnvironment.
               "--client-id", context.ClientId ?? string.Empty,
               "--token-endpoint", context.TokenEndpoint ?? string.Empty,
               "--owner-id", context.OwnerId,
   ```

4. **`BuildEnvironment`.** Insert this immediately before the `/// <summary>` of `ResolveCommand` (the block that begins "Turns a driver-relative executable path into an absolute one"):

   ```csharp
       /// <summary>
       /// The secret values a driver needs, passed in its environment rather than on its command line:
       /// <c>/proc/&lt;pid&gt;/cmdline</c> is readable by every local user, <c>/proc/&lt;pid&gt;/environ</c>
       /// only by the owner. Each driver's <c>Args.optional</c> falls back to these variables for the
       /// matching flags, and a flag given on the command line still wins for a driver run by hand.
       /// </summary>
       internal static Dictionary<string, string> BuildEnvironment(DriverContext context) => new(StringComparer.Ordinal)
       {
           ["IVERSON_DRIVER_CLIENT_SECRET"] = context.ClientSecret ?? string.Empty,
           // A pre-minted service token, and the only one the drivers should ever use. The
           // client-credentials trio (--client-id, --token-endpoint and the secret above) is left in
           // the contract for a driver run by hand, but a driver that mints its own token cannot
           // produce a usable one here: Authentik stamps the JWT's `iss` from the request's Host
           // header, so a token fetched from localhost carries an issuer the API rejects outright
           // (401), and none of the five drivers passes a scope, so even an accepted token would
           // lack `schema_admin` (403 on RegisterSchema). The orchestrator already mints this
           // correctly once, with both the Host header and the scope, so it hands the result over
           // rather than having five languages each re-derive Authentik's issuer semantics.
           ["IVERSON_DRIVER_SERVICE_TOKEN"] = context.ServiceToken,
           ["IVERSON_DRIVER_ACTING_TOKEN"] = context.ActingToken,
           // Always set, empty included — see DriverContext.WrongActingToken.
           ["IVERSON_DRIVER_WRONG_ACTING_TOKEN"] = context.WrongActingToken,
       };

   ```

5. **`RunProcessAsync`** (`:378-390`). Replace:

   ```csharp
       private static async Task<ProcessOutcome> RunProcessAsync(
           string command, IReadOnlyList<string> args, string cwd, CancellationToken ct)
   ```

   with:

   ```csharp
       private static async Task<ProcessOutcome> RunProcessAsync(
           string command, IReadOnlyList<string> args, string cwd, CancellationToken ct,
           IReadOnlyDictionary<string, string>? environment = null)
   ```

   and, directly after:

   ```csharp
           foreach (var arg in args)
               psi.ArgumentList.Add(arg);
   ```

   insert:

   ```csharp
           if (environment is not null)
           {
               foreach (var (name, value) in environment)
                   psi.Environment[name] = value;
           }
   ```

- [ ] **Step 3: Run the suite, then the mutants**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Server
dotnet test Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
```

Expected: `Passed: 650` on a branch cut after A's merge (A adds two `IdentityScenarioTests`). In general, 10 more than the suite reported before this task (`648` on a pre-A `main`).

Mutants (restore each afterwards; filter `--filter "FullyQualifiedName~DriverRunnerTests"`):
1. **`RunProcessAsync` ignores the dictionary** (replace `psi.Environment[name] = value;` with `_ = value;`). `RunPhaseAsync_StartsTheDriverWithEachSecretInItsEnvironment` fails.
2. **The exec call passes no environment** (drop `, BuildEnvironment(context)`). The same test fails.
3. **`BuildFlags` re-emits `--service-token`.** `BuildFlags_CarriesNoSecretFlagOrValue(--service-token…)` and `RunPhaseAsync_TimeoutMessage_CarriesNoSecret` fail.
4. **`BuildEnvironment` drops a variable**, for example `IVERSON_DRIVER_WRONG_ACTING_TOKEN`. Its `BuildEnvironment_*` tests and the end-to-end test fail.

- [ ] **Step 4: Fix the runbook**

In `docs/runbooks/client-conformance-matrix.md`, replace:

```markdown
Every `IVERSON_ACTING_USER_*` variable already has a working compose default in `TokenBroker.cs`
and needs no export.
```

with:

````markdown
`IVERSON_ACTING_USER_BYPASS_PASSWORD` and `IVERSON_OTHER_TENANT_PASSWORD` have no default and are
required: `TokenBroker.cs` throws without them. The compose stack's Authentik passwords are
generated per stack, so read them out of `Iverson.Server/.env`:

```bash
export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(grep '^IVERSON_BYPASS_PASSWORD=' Iverson.Server/.env | cut -d= -f2)"
export IVERSON_OTHER_TENANT_PASSWORD="$(grep '^IVERSON_SMOKE_TEST_PASSWORD=' Iverson.Server/.env | cut -d= -f2)"
```

Every other `IVERSON_ACTING_USER_*` and `IVERSON_OTHER_TENANT_*` variable has a working compose
default in `TokenBroker.cs`.

The harness hands each driver its secrets (the client secret, the service token and the two
acting-user tokens) in the `IVERSON_DRIVER_*` environment variables rather than on its command
line, which every local user can read. A driver run by hand still accepts them as flags, and a
flag wins over the variable.
````

- [ ] **Step 5: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Server/Iverson.ClientConformance/DriverRunner.cs Iverson.Server/Iverson.ClientConformance.Tests/DriverRunnerTests.cs Iverson.Server/Iverson.ClientConformance.Tests/NamingRejectedScenarioTests.cs
git add -f docs/runbooks/client-conformance-matrix.md
git commit -m "pass conformance driver secrets in the environment instead of on the command line

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: The agent CLI opts in only for loopback plaintext (spec §3)

**Files:**
- Modify: `Iverson.Agents/Python/iverson_agent/__main__.py`, at `:6` (the imports), before `:39` (the new `_is_loopback`) and `:72-80` (the opt-in)
- Test: `Iverson.Agents/Python/tests/test_main.py`

- [ ] **Step 1: Write the tests**

In `tests/test_main.py`, insert immediately before `def _env(monkeypatch):`:

```python
@pytest.mark.parametrize("host,expected", [
    ("localhost", True), ("127.0.0.1", True), ("127.1.2.3", True), ("::1", True),
    ("example.com", False), ("10.0.0.5", False), (None, False),
])
def test_is_loopback(host, expected):
    assert cli._is_loopback(host) is expected


@pytest.mark.parametrize("grpc_url,token_endpoint,expected", [
    # The compose defaults: both legs plaintext, both on this machine.
    ("http://localhost:8080", "http://localhost:9000/application/o/token/", True),
    # A plaintext leg to another host keeps the SDK's refusal.
    ("http://iverson.example.com:8080", "http://localhost:9000/application/o/token/", False),
    ("http://localhost:8080", "http://idp.example.com/application/o/token/", False),
    ("https://iverson.example.com", "http://idp.example.com/application/o/token/", False),
    # A TLS leg's host does not matter.
    ("http://localhost:8080", "https://idp.example.com/application/o/token/", True),
    ("https://iverson.example.com", "http://127.0.0.1:9000/application/o/token/", True),
])
def test_insecure_opt_in_only_when_every_plaintext_leg_is_loopback(monkeypatch, grpc_url, token_endpoint, expected):
    client_cls, session_cls = _env(monkeypatch)
    monkeypatch.setenv("IVERSON_GRPC_URL", grpc_url)
    monkeypatch.setenv("IVERSON_TOKEN_ENDPOINT", token_endpoint)
    session_cls.return_value.run.return_value = AgentAnswer(
        text="x", citations=[], tool_calls=0, context_tokens=0)
    cli.main(["ask", "--entity", "tests.test_session:PolicyDoc", "--question", "q"])
    assert client_cls.call_args_list[0].kwargs["allow_insecure_credentials"] is expected
    session_cls.call_args.kwargs["schema_client_factory"]("other-jwt")
    assert client_cls.call_args.kwargs["allow_insecure_credentials"] is expected
```

Run them and watch them fail (`_is_loopback` does not exist yet):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Agents/Python
/home/ben/repositories/Iverson/Iverson.Agents/Python/.venv/bin/python -m pytest -q tests/test_main.py
```

- [ ] **Step 2: Add `_is_loopback` and the rule**

In `iverson_agent/__main__.py`:

1. Add `import ipaddress` between `import importlib` and `import json`.
2. Insert immediately before `def _entity(spec: str) -> type:`:

   ```python
   def _is_loopback(host: str | None) -> bool:
       """True for `localhost` and any loopback address (127.0.0.0/8, ::1); false for anything else."""
       if host == "localhost":
           return True
       try:
           return ipaddress.ip_address(host).is_loopback
       except ValueError:
           return False


   ```

3. Replace `:72-80`:

   ```python
       # IVERSON_GRPC_URL defaults to http://localhost:8080 — the compose stack's plaintext h2c
       # listener — so this CLI must explicitly defeat IversonClient's default guard against
       # attaching credentials to a plaintext channel. Inert (but harmless) when tls=True.
       with IversonClient(host, port, use_tls=tls, credentials=creds, allow_insecure_credentials=True) as iverson:
           session = AgentSession(
               client, iverson, _entity(args.entity), cfg,
               schema_client_factory=lambda t: IversonClient(host, port, use_tls=tls, credentials=creds,
                                                             acting_user_token=t, allow_insecure_credentials=True),
   ```

   with:

   ```python
       # IversonClient refuses to send credentials over plaintext without an opt-in, which covers two
       # legs: the gRPC channel (plaintext unless https://) and the token endpoint (plaintext unless
       # https). Opt in only when every plaintext leg stays on this machine — the compose stack's
       # defaults, http://localhost:8080 and a localhost token endpoint. A plaintext leg to any other
       # host keeps the SDK's refusal.
       token_url = urlsplit(creds.token_endpoint)
       allow_insecure = (tls or _is_loopback(host)) and (
           token_url.scheme == "https" or _is_loopback(token_url.hostname))
       with IversonClient(host, port, use_tls=tls, credentials=creds, allow_insecure_credentials=allow_insecure) as iverson:
           session = AgentSession(
               client, iverson, _entity(args.entity), cfg,
               schema_client_factory=lambda t: IversonClient(host, port, use_tls=tls, credentials=creds,
                                                             acting_user_token=t,
                                                             allow_insecure_credentials=allow_insecure),
   ```

- [ ] **Step 3: Run the tests, then the mutants**

Run Step 1's command, then the whole agent suite (`… -m pytest -q`). Expected: `18 passed`, then `68 passed`.

Mutants (restore each afterwards):
1. **The opt-in is always true** (`allow_insecure = True or (`). 3 failed.
2. **A TLS gRPC leg is ignored** (drop `tls or`). 1 failed: the remote-TLS, loopback-http case.
3. **`localhost` is not loopback.** 3 failed.

- [ ] **Step 4: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Agents/Python/iverson_agent/__main__.py Iverson.Agents/Python/tests/test_main.py
git commit -m "opt the agent CLI in to insecure credentials only when every plaintext leg is loopback

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: The SDKs refuse a per-call acting-user token on an un-opted plaintext client (spec §4)

**Files:**
- Modify .NET (`Iverson.Clients/DotNet/Iverson.Client.Core`):
  - `ActingUserIdentity.cs` (the whole class);
  - `ServiceCollectionExtensions.cs:121`;
  - `EntityCoordinator.cs:41-45`.
- Modify Python:
  - `Iverson.Clients/Python/iverson_client/core.py`, at `:625-630`, `:643-649`, `:929-930` and `:951-953`;
  - `Iverson.Clients/Python/conformance/driver.py`, at `:426-429` (the catalog client).
- Modify TypeScript: `Iverson.Clients/TypeScript/src/core.ts`, at `:671-675`, `:866` and `:908`.
- Test:
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs`
  - `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs`
  - `Iverson.Clients/Python/tests/test_auth.py`
  - `Iverson.Clients/TypeScript/tests/core.test.ts`

Each SDK's message mirrors its existing constructor message. Go and Java are unchanged (spec row 13).

- [ ] **Step 1: Write the tests**

**.NET.** Append to `ServiceCollectionExtensionsTests` (inside the class):

```csharp
    // WithActingUser (EntityCoordinator) refuses a per-call token when this bit is set; it must be
    // set exactly when the endpoint is plaintext and the caller did not opt in.
    [Theory]
    [InlineData("http://localhost:5000", false, true)]
    [InlineData("http://localhost:5000", true, false)]
    [InlineData("https://localhost:5000", false, false)]
    public void AddIversonClient_SetsRefusesPlaintextTokens_ForAPlaintextEndpointWithoutOptIn(
        string endpoint, bool optIn, bool expected)
    {
        var services = new ServiceCollection();
        services.AddIversonClient(grpcEndpoint: endpoint, allowInsecureChannelCallCredentials: optIn);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ActingUserIdentity>().RefusesPlaintextTokens.Should().Be(expected);
    }
```

Append to `EntityCoordinatorIdentityResolutionTests` (inside the class):

```csharp
    [Fact]
    public void WithActingUser_Throws_WhenTheClientRefusesPlaintextTokens()
    {
        var (sut, _) = CreateSut(new ActingUserIdentity(refusesPlaintextTokens: true));

        var act = () => sut.WithActingUser(() => Task.FromResult("bound-token"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*allowInsecureChannelCallCredentials*");
    }

    [Fact]
    public async Task WithActingUser_Binds_WhenTheClientDoesNotRefusePlaintextTokens()
    {
        var (sut, captured) = CreateSut(new ActingUserIdentity(refusesPlaintextTokens: false));

        await sut.WithActingUser(() => Task.FromResult("bound-token")).PostMappedAsync(NewEntity());

        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer bound-token");
    }
```

**Python.** Append to `tests/test_auth.py`:

```python


# A per-call acting-user token (with_acting_user) gets the same plaintext guard as the ambient
# one the constructor checks above.
def test_with_acting_user_raises_on_plaintext_without_opt_in():
    client = IversonClient(host="localhost", port=5000, use_tls=False)
    try:
        with pytest.raises(ValueError, match="allow_insecure_credentials"):
            client.coordinator(CoordSchemaEntity).with_acting_user("user-token-123")
    finally:
        client.close()


def test_with_acting_user_binds_on_plaintext_with_opt_in():
    client = IversonClient(host="localhost", port=5000, use_tls=False, allow_insecure_credentials=True)
    try:
        bound = client.coordinator(CoordSchemaEntity).with_acting_user("user-token-123")
        assert bound._acting_user_metadata()[0][1] == "Bearer user-token-123"
    finally:
        client.close()


def test_with_acting_user_binds_on_tls():
    client = IversonClient(host="localhost", port=5000, use_tls=True)
    try:
        bound = client.coordinator(CoordSchemaEntity).with_acting_user("user-token-123")
        assert bound._acting_user_metadata()[0][1] == "Bearer user-token-123"
    finally:
        client.close()
```

**TypeScript.** In `tests/core.test.ts`, insert immediately before the line `// ── EntityCoordinator — acting-user token threading ─────────────────────────`:

```typescript
// ── EntityCoordinator.withActingUser — plaintext guard ──────────────────────
// A per-call acting-user token gets the same plaintext guard as the ambient one the constructor
// checks above.

describe('EntityCoordinator.withActingUser — plaintext guard', () => {
    it('throws on a plaintext channel without the opt-in', () => {
        const client = new IversonClient('localhost', 5000, false);
        try {
            expect(() => new EntityCoordinator(TestEntity, client).withActingUser('tok-1'))
                .toThrow(/allowInsecureCredentials/);
        } finally {
            client.close();
        }
    });

    it('binds on a plaintext channel with the opt-in', () => {
        const client = new IversonClient('localhost', 5000, false, undefined, undefined, true);
        try {
            expect(new EntityCoordinator(TestEntity, client).withActingUser('tok-1')).toBeInstanceOf(EntityCoordinator);
        } finally {
            client.close();
        }
    });

    it('binds on a TLS channel', () => {
        const client = new IversonClient('localhost', 5000, true);
        try {
            expect(new EntityCoordinator(TestEntity, client).withActingUser('tok-1')).toBeInstanceOf(EntityCoordinator);
        } finally {
            client.close();
        }
    });
});

```

Expected red:
- **.NET:** compile errors (no `refusesPlaintextTokens` parameter or `RefusesPlaintextTokens` property yet).
- **Python:** the raise test fails.
- **TypeScript:** the throw test fails. Type-checking passes, because the tests read no new member.

- [ ] **Step 2: .NET**

1. **`ActingUserIdentity.cs`.** Replace the class:

   ```csharp
   public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null)
   {
       public Func<Task<string>>? TokenProvider { get; } = tokenProvider;
   }
   ```

   with:

   ```csharp
   public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null, bool refusesPlaintextTokens = false)
   {
       public Func<Task<string>>? TokenProvider { get; } = tokenProvider;

       /// <summary>
       /// True when the client's endpoint is plaintext and it was not opted in to sending credentials
       /// over it. <c>EntityCoordinator&lt;T&gt;.WithActingUser</c> then refuses a per-call token, as
       /// <c>AddIversonClient</c> refuses an ambient one.
       /// </summary>
       public bool RefusesPlaintextTokens { get; } = refusesPlaintextTokens;
   }
   ```

2. **`ServiceCollectionExtensions.cs:121`.** Replace:

   ```csharp
           services.AddSingleton(new ActingUserIdentity(actingUserTokenProvider));
   ```

   with:

   ```csharp
           services.AddSingleton(new ActingUserIdentity(
               actingUserTokenProvider,
               refusesPlaintextTokens: !allowInsecureChannelCallCredentials &&
                   string.Equals(new Uri(grpcEndpoint).Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)));
   ```

   This is the same plaintext test as the constructor guard at `:59-61`.

3. **`EntityCoordinator.cs:41-45`.** Replace:

   ```csharp
       public EntityCoordinator<T> WithActingUser(Func<Task<string>> tokenProvider) =>
           new(registry, assembler, mapping, persistence, retrieval, search, logger, identity)
           {
               _boundActingUser = tokenProvider,
           };
   ```

   with:

   ```csharp
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
           {
               _boundActingUser = tokenProvider,
           };
       }
   ```

- [ ] **Step 3: Python**

In `iverson_client/core.py`:

1. **`EntityCoordinator.__init__`.** Change the signature's tail from:

   ```python
           acting_user_token: str | None = None,
       ) -> None:
           meta = getattr(entity_class, "_iverson_meta", None)
   ```

   to:

   ```python
           acting_user_token: str | None = None,
           *,
           refuse_plaintext_token: bool = False,
       ) -> None:
           meta = getattr(entity_class, "_iverson_meta", None)
   ```

2. **The constructor body.** After `self._acting_user_token = acting_user_token` (`:643`), add:

   ```python
           self._refuse_plaintext_token = refuse_plaintext_token
   ```

3. **`with_acting_user`.** Insert directly after its docstring:

   ```python
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

4. **`IversonClient.__init__`.** After `self._acting_user_token = acting_user_token` (`:930`, the line after `self._mapping_stub = …`), add:

   ```python
           # Read by coordinator(): a per-call acting-user token gets the same plaintext guard as
           # the ambient one above.
           self._refuse_plaintext_token = not use_tls and not allow_insecure_credentials
   ```

5. **`coordinator()`.** Replace:

   ```python
           return EntityCoordinator(entity_class, self._channel, self._acting_user_token)
   ```

   with:

   ```python
           return EntityCoordinator(
               entity_class, self._channel, self._acting_user_token,
               refuse_plaintext_token=self._refuse_plaintext_token,
           )
   ```

In `conformance/driver.py`, `_DriverSchemaCatalogClient.__init__`: after `self._acting_user_token = None`, add:

```python
        # Never binds a per-call token: the driver's channel carries identity itself.
        self._refuse_plaintext_token = False
```

The parity test `test_driver_schema_catalog_client_reproduces_every_attribute_the_base_constructor_sets` fails without this line (spec row 17).

- [ ] **Step 4: TypeScript**

In `src/core.ts`:

1. **`withActingUser`.** Insert as its first statement:

   ```typescript
           // The per-call token travels as per-call metadata, as the ambient one does, so it needs the
           // same plaintext guard IversonClient's constructor applies to the ambient one.
           if (this._client._refusesPlaintextTokens) {
               throw new Error(
                   'Refusing to bind an acting-user token on a plaintext (useTls=false) channel ' +
                   'without an explicit allowInsecureCredentials=true opt-in on IversonClient. Pass ' +
                   'allowInsecureCredentials=true only for a known-local, non-TLS endpoint.',
               );
           }
   ```

2. **`IversonClient`.** After `readonly _actingUserToken?: ActingUserToken;`, add:

   ```typescript
       /** Read by EntityCoordinator.withActingUser: plaintext and not opted in. */
       readonly _refusesPlaintextTokens: boolean;
   ```

3. **The constructor.** After `this._actingUserToken = actingUserToken;`, add:

   ```typescript
           this._refusesPlaintextTokens = !useTls && !allowInsecureCredentials;
   ```

The tests' `makeClientLike` fakes lack the field, so they read as not refusing (spec §4).

- [ ] **Step 5: Run every SDK suite, then the mutants**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Clients/DotNet && dotnet test Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Clients/Python && python3 -m pytest -q
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling/Iverson.Clients/TypeScript && npm test
```

Expected: .NET `Passed: 79`; Python `241 passed`; TS `287 passed`, with type-checking clean.

Mutants (restore each afterwards):
- **.NET:**
  - The check removed (`if (false && identity?.RefusesPlaintextTokens == true)`): `WithActingUser_Throws…` fails.
  - The bit never set (`refusesPlaintextTokens: false && …`): the `http`, no-opt-in theory case fails.
- **Python:**
  - The check removed: 1 failed.
  - `coordinator()` passes `refuse_plaintext_token=False`: 1 failed.
  - The driver line deleted: the parity test fails.
- **TypeScript:**
  - The check removed: 1 failed.
  - The field ignores the opt-in (`= !useTls`): 1 failed.

- [ ] **Step 6: Rebuild the conformance drivers that compile against the changed SDKs**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
dotnet build Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj
dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj
(cd Iverson.Clients/TypeScript && npx tsc -p tsconfig.conformance.json)
```

Expected: all three succeed.

- [ ] **Step 7: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-tooling
git add Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/ServiceCollectionExtensionsTests.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorIdentityResolutionTests.cs Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/conformance/driver.py Iverson.Clients/Python/tests/test_auth.py Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/tests/core.test.ts
git commit -m "refuse a per-call acting-user token on a plaintext client that has not opted in

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 7: Live verification (spec §5 "Live check before merge")

This task makes no commits. It proves the spec's three live checks on the finished branch, against an isolated compose project. Its helper files live in the session scratchpad and are never committed.

**Names used below:**
- `LIVE` is `<session scratchpad>/live`.
- `BR` is `/home/ben/repositories/Iverson/.worktrees/csr10-tooling`.
- `MAIN` is `/home/ben/repositories/Iverson`. `.env` is gitignored, so it exists only at `$MAIN/Iverson.Server/.env`.

**Standing constraints for this task:**
- Never start, stop, remove or modify the user's `iversonserver` containers, volumes, networks or images. Snapshot all four before starting, and diff them after teardown.
- Never print a value from `.env`. Values reach programs only through exported variables, never through a command line.
- Every LoadTest and harness run uses the scratch `HOME`, `$LIVE/home`, with the toolchain caches pinned to the real home. `~/.cache/iverson` is never read or written. Step 1 records a marker, and Step 6 proves nothing under `~/.cache/iverson` changed after it. Step 6's `find -newer` lists that directory's entries' metadata; that is the one exception to never listing it, allowed by the user (2026-10-04).
- Shell variables do not persist between tool calls, so every block starts with `source $LIVE/env.sh`.

- [ ] **Step 1: Prepare, and check that the user's stack is down**

```bash
LIVE=<session scratchpad>/live; mkdir -p $LIVE/home
docker ps --format '{{.Names}}'
```

If the list is not empty, **stop and ask the user**, and never stop their containers yourself. The isolated stack publishes the same host ports (127.0.0.1:5432, 8080, 9000, …), so it cannot start next to theirs.

Write `$LIVE/env.sh`. It holds no secret, only how to read them:

```bash
MAIN=/home/ben/repositories/Iverson
BR=$MAIN/.worktrees/csr10-tooling
LIVE=<session scratchpad>/live
ENVF=$MAIN/Iverson.Server/.env
REAL_HOME=/home/ben
DC="docker compose -p csr10tool -f $LIVE/compose.yml --project-directory $BR/Iverson.Server --env-file $ENVF"
# Reads one .env value. Used only inside $(...) feeding an export, so no value reaches a command line.
val() { grep "^$1=" "$ENVF" | cut -d= -f2-; }
# Every LoadTest and harness run: the scratch HOME, with the toolchain caches pinned to the real home.
scratch_env() {
  export HOME=$LIVE/home
  export NUGET_PACKAGES=$REAL_HOME/.nuget/packages
  export PYTHONUSERBASE=$REAL_HOME/.local
  export GOMODCACHE=$REAL_HOME/go/pkg/mod
  export MAVEN_OPTS=-Dmaven.repo.local=$REAL_HOME/.m2/repository
  export npm_config_cache=$REAL_HOME/.npm
}
```

Then:

```bash
source <session scratchpad>/live/env.sh
touch $LIVE/start-marker
grep -v container_name $BR/Iverson.Server/docker-compose.yml \
  | sed 's/^    image: iverson-api$/    image: csr10tool-api/' > $LIVE/compose.yml
grep -c 'image: csr10tool-api' $LIVE/compose.yml
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

Expected: the `grep -c` prints `2`, for the api and the worker.

- [ ] **Step 2: Stand up the isolated stack**

The build and image pulls can exceed the Bash tool's 10-minute foreground limit, so run this block with `run_in_background`:

```bash
source <session scratchpad>/live/env.sh
$DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log
```

Once it reports, wait for readiness. Poll until `docker inspect -f '{{.State.Health.Status}}' csr10tool-iverson-api-1` prints `healthy` (up to 10 minutes). `$DC ps` does not show health on this machine's engine. Then wait until Authentik issues the admin-automation token, which proves its blueprints applied. The `printf` builtin keeps the secret off every command line:

```bash
source <session scratchpad>/live/env.sh
for i in $(seq 60); do
  code=$(printf 'data = "grant_type=client_credentials&client_id=dev-iverson-admin-automation-client-id&client_secret=%s&scope=admin"\n' "$(val IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET)" \
    | curl -s -o /dev/null -w '%{http_code}' -K - http://localhost:9000/application/o/token/)
  [ "$code" = 200 ] && echo READY && break; sleep 10
done
```

Expected: `READY`.

- [ ] **Step 3: Check 2. With the tenant-admin password unset, `seed` fails at startup**

```bash
source <session scratchpad>/live/env.sh
(cd $BR/Iverson.Server/Iverson.LoadTest && dotnet build -v q)
(
  scratch_env
  unset IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD
  export IVERSON_ACTING_USER_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)"
  cd $BR/Iverson.Server/Iverson.LoadTest && dotnet run --no-build -- seed
) > $LIVE/check2.out 2>&1; echo "exit=$?"
grep -c "Ensuring LoadTest tenant is provisioned" $LIVE/check2.out
grep -c "IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD' -- choose a password" $LIVE/check2.out
```

The build runs under the real `HOME`; only the `seed` run uses the scratch one.

Expected: a non-zero exit, then `0`, then `1`. Both acting-user passwords are set, so this message can only come from the tenant-admin gating (spec row 20).

- [ ] **Step 4: Check 1. A fresh `seed` logs the tenant admin in before any data-plane call**

```bash
source <session scratchpad>/live/env.sh
(
  scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-admin-automation-client-id
  export IVERSON_CLIENT_SECRET="$(val IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
  export IVERSON_CLIENT_SCOPE="admin schema_admin tenant_id_admin"
  export IVERSON_ACTING_USER_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)"
  export IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD="$(openssl rand -base64 24)"
  export IVERSON_POSTGRES_CS="Host=127.0.0.1;Port=1;Database=iverson;Username=iverson;Password=iverson"
  cd $BR/Iverson.Server/Iverson.LoadTest && dotnet run --no-build -- seed
) > $LIVE/check1.out 2>&1; echo "exit=$?"
grep -E "Set the tenant admin's password|Logged the tenant admin in|ready\.|Schemas registered" $LIVE/check1.out
ls -l $LIVE/home/.cache/iverson/
stat -c '%a %n' $LIVE/home/.cache/iverson $LIVE/home/.cache/iverson/*
```

The tenant-admin password is random and never stored. Port 1 is closed, so `DirectSeeder.RunAsync`'s first `pg.OpenAsync` (`Seeding/DirectSeeder.cs:37`) fails before any data-plane call. Provisioning uses `iverson-admin-automation`, because `CreateTenant` needs the `admin` scope (plan row 26).

Expected:
- A non-zero exit, from the Postgres failure.
- The four lines found, in order: the password was set; the admin was logged in; `Tenant 'iverson-loadtest-dynamic' ready.`; `Schemas registered.`
- `$LIVE/home/.cache/iverson/` holds exactly one file, `acting-user-totp-secret-compose-iverson-loadtest-tenant-admin.txt`. There is no `…-iverson-acting-user-smoke-test.txt` and no `…-iverson-loadtest-bypass-user.txt`.
- `stat` prints `700` for the directory and `600` for the file.

Schema registration authenticates with client credentials (spec row 20), so only the immediate mint can have created that file.

- [ ] **Step 5: Check 3. The identity scenario passes in all five languages, with no secret on any driver's command line**

Write `$LIVE/watch_cmdline.py`:

```python
"""While the harness runs, scans the command line of every conformance driver process for secrets.

A driver process is one whose command line carries `--scenario` (every driver exec does; the
harness's own `--scenarios` does not). Prints only the PID and the name of the pattern that
matched, never the matched text. Stops when the file named by WATCH_STOP_FILE exists.
"""
import os
import re
import time

SECRET_FLAG = re.compile(rb"(^|\x00)--(client-secret|service-token|acting-token|wrong-acting-token)\x00")
JWT = re.compile(rb"(^|\x00)eyJ[A-Za-z0-9_-]{10,}")
LANGUAGES = {
    b"Iverson.Client.Conformance.Driver": "dotnet",
    b"conformance/driver.py": "python",
    b"conformance/driver.js": "typescript",
    b"bin/conformance": "go",
    b"iverson-conformance-driver.jar": "java",
}
client_secret = os.environ["WATCH_CLIENT_SECRET"].encode()
stop_file = os.environ["WATCH_STOP_FILE"]
seen, hits = set(), set()
while not os.path.exists(stop_file):
    for pid in filter(str.isdigit, os.listdir("/proc")):
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                cmdline = f.read()
        except OSError:
            continue
        if b"\x00--scenario\x00" not in cmdline:
            continue
        seen.update(lang for marker, lang in LANGUAGES.items() if marker in cmdline)
        for name, matched in (("secret-flag", SECRET_FLAG.search(cmdline)),
                              ("jwt", JWT.search(cmdline)),
                              ("client-secret-value", client_secret in cmdline)):
            if matched and (pid, name) not in hits:
                hits.add((pid, name))
                print(f"HIT pid={pid} pattern={name}", flush=True)
    time.sleep(0.05)
print(f"languages seen: {sorted(seen)}")
print(f"hits: {len(hits)}")
```

Run the harness with the watcher alongside. The harness builds all five drivers, so run this block with `run_in_background`:

```bash
source <session scratchpad>/live/env.sh
rm -f $LIVE/watch.stop
( export WATCH_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)" WATCH_STOP_FILE=$LIVE/watch.stop
  python3 $LIVE/watch_cmdline.py > $LIVE/watch.out 2>&1 ) &
(
  scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id
  export IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/
  export IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)"
  export IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  cd $BR/Iverson.Server/Iverson.ClientConformance && dotnet run -- --scenarios identity
) > $LIVE/check3.out 2>&1; echo "harness exit=$?" >> $LIVE/check3.out
touch $LIVE/watch.stop; sleep 1
```

Expected:
- `check3.out` ends with `harness exit=0`, and its matrix shows the identity row `ok` for dotnet, python, typescript, go and java.
- `watch.out` shows `hits: 0` and `languages seen: ['dotnet', 'go', 'java', 'python', 'typescript']`.

If the matrix shows a failing cell, read that cell's stderr in `check3.out`. Then decide whether the failure is E's before blaming E: re-run the same cell from a `main`-built harness, built in a scratch worktree of `main`, against the same stack. A cell that also fails on `main` is not E's; record it, and say so in the outcome.

- [ ] **Step 6: Tear down, and confirm nothing else changed**

First, the control: before teardown, the same diffs must list the isolated stack, which proves they can fail.

```bash
source <session scratchpad>/live/env.sh
diff <(docker ps -a --format '{{.Names}}' | sort) $LIVE/before-containers.txt | grep -c '^< csr10tool-'
diff <(docker volume ls -q | sort) $LIVE/before-volumes.txt | grep -c '^< csr10tool_'
diff <(docker network ls --format '{{.Name}}' | sort) $LIVE/before-networks.txt
```

Expected: non-zero container and volume counts (17 and 9 in the 2026-10-04 run), and a network diff of `< csr10tool_default`.

Then tear down:

```bash
source <session scratchpad>/live/env.sh
$DC down -v
diff <(docker ps -a --format '{{.Names}}' | sort) $LIVE/before-containers.txt
diff <(docker volume ls -q | sort) $LIVE/before-volumes.txt
diff <(docker network ls --format '{{.Name}}' | sort) $LIVE/before-networks.txt
docker rmi csr10tool-api 2>/dev/null
diff <(docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort) $LIVE/before-images.txt
find /home/ben/.cache/iverson -newer $LIVE/start-marker 2>/dev/null
```

Expected:
- The container, volume and network diffs are empty.
- The images diff changes no pre-existing line, so `iverson-api:latest` keeps its ID. Any lines it adds are base or intermediate images the build pulled, and those may stay.
- `find` prints nothing: no file under `~/.cache/iverson` changed after Step 1.

- [ ] **Step 7: Record the outcome**

Record each check's result in the SDD ledger:
- **Check 2:** the exit code, plus both counts.
- **Check 1:** the four lines, the file list and the modes.
- **Check 3:** each language's identity cell, the watcher's `hits` and its `languages seen`.
- **Teardown:** the diffs.

A failure blocks the merge and goes back to the owning task:
- Check 2 → Task 2.
- Check 1 → Task 2, or Task 1 for the modes.
- Check 3 → Task 4 for a secret on a command line; Task 3 for a driver that cannot read its environment; Task 6 for a binding refused on an opted-in client.

## Known issues inherited from spec

- **TMA row F6 is not edited here.** `docs/security/tma.md` holds the user's uncommitted TMA refresh. The F6 correction, along with the others in the review's §7, belongs with committing that refresh. Decided by the user on 2026-10-04.
- **The SDK flag is not split** into separate plaintext-channel and plaintext-token-endpoint opt-ins. Once the agent CLI derives its opt-in from loopback endpoints, the combined flag is no longer misused.
- **A lost tenant-admin password is not recoverable by LoadTest.** If `IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD` changes after the tenant exists, the change has no effect, as today. The recovery-step guidance (`Program.cs:131-139`) explains how to reset it by hand.
