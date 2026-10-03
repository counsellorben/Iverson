# LoadTest Tenant-Admin Password Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-02-loadtest-tenant-admin-password-design.md` (commit SHA: `789ae718fd6039eec4cff6662a1d0994e0a5ec99`)

**Goal:** When LoadTest creates its tenant, it follows CreateTenant's one-time `admin_recovery_link` to give the new tenant admin the configured password (`IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD`), so the data plane can log in on a fresh stack.

**Architecture:** `AuthentikFlowExecutorClient` gains `SetPasswordFromRecoveryLinkAsync`. It drives the `iverson-recovery` flow through the client's existing `SendAsync`: prompt GET, password POST, then GETs past Authentik's body-less 302s until a terminal 200. `Program.cs` returns the link from `EnsureTenantProvisionedAsync` and runs the method on a separate client instance before building the login client.

**Tech stack:** .NET 10 (`net10.0`), xunit 2.9.3, FluentAssertions 8.11.0, Authentik 2026.5.3 flow executor.

---

## Global Constraints

- **Work in a worktree branched from LOCAL `main`** (at or after `789ae718`), not `origin/main`. Local `main` holds the spec commits.
- **The recovery link is a bearer credential.** No log line or exception message may include the link or its `flow_token` (CSR round-3 finding #3; `IdpAdminClient.cs:92-99`). This includes the executor URL, which embeds the token.
- **Only the existing two files plus one new test file change in the repo.** The server, proto, blueprints, environment variables and the dev default password stay as they are (spec, "Nothing else changes").
- **The live check never touches the main compose stack.** The main stack is the `iversonserver` project, whose stopped containers have fixed `iverson-*` names. The check uses a copy of `docker-compose.yml` with its `container_name:` lines stripped, under its own project name, and tears it down with `down -v`.

## File Structure

- **Modify** `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs`: a test-only constructor, `SetPasswordFromRecoveryLinkAsync`, and the private helper `ReadComponentAsync`.
- **Create** `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`: the spec's five tests, over a scripted fake `HttpMessageHandler`.
- **Modify** `Iverson.Server/Iverson.LoadTest/Program.cs`: `EnsureTenantProvisionedAsync` returns the link, and the bootstrap block sets the password before building the login client.
- **Scratch, not committed:** a live-check console project outside the repo.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` at spec-write time, and through CDR rounds 1–2, and are NOT re-verified here. They are trusted as ground truth. Verbatim from the spec:

Probes P1–P5 ran 2026-10-02 against an isolated Authentik 2026.5.3. It was started from this repo's compose file and blueprints, under its own project name, with `container_name` lines stripped. Each probe followed the server's side through `IdpAdminClient`'s API calls, and LoadTest's side through `127.0.0.1:9000` with `Host: authentik-server:9000`, a cookie jar and the CSRF echo.

P1–P5 used Python urllib, whose default opener follows redirects. Rows 2, 18 and 19 instead rest on probe P6, a C# program that drives LoadTest's real `AuthentikFlowExecutorClient.SendAsync` by reflection (existing constructor, so no redirect following). It was run in CDR round 1 and re-run while applying that review.

| # | Assumption | Evidence |
|---|---|---|
| 1 | A recovery link is `/if/flow/<slug>/` with a `flow_token` query value, on the server's internal host | P1: `POST /api/v3/core/users/{pk}/recovery/` returned host `authentik-server:9000`, path `/if/flow/iverson-recovery/`, query keys `['flow_token']` |
| 2 | For a new user, one POST followed by GETs past the executor's self-redirects completes the recovery flow | P2: the GET to `/api/v3/flows/executor/iverson-recovery/?query=flow_token%3D…` returned `ak-stage-prompt` with fields `[password, password_repeat]`. P6, through the real `SendAsync`: the POST returned `302` with an empty body, then GET `302`, GET `302`, then GET `200 {"component": "xak-flow-redirect", "to": "/"}`, and a fresh login then reached `ak-stage-authenticator-validate`. Stopping after the POST left the password unset (login `PASSWORD-REJECTED`). The MFA stage skips for a user with no enrolled device (`recovery-flow.yaml` `not_configured_action: skip`). |
| 3 | The password really is set | P3: a fresh session's `default-authentication-flow` went identification → password → `ak-stage-authenticator-validate` |
| 4 | Another GET after completion is denied | P2: `ak-stage-access-denied`, "Flow does not apply to current user" |
| 5 | A policy rejection appears in the POST response, and the password stays unset | P4: with `short1`, the POST returned `ak-stage-prompt` with `response_errors.non_field_errors[0].string` = "Password needs to be 8 characters or longer.", and a later login rejected the password |
| 6 | The dev default password passes the recovery password policy | P2/P3 used `dev-only-not-for-production-tenant-admin-password-0123456789` |
| 7 | A blueprinted user would break CreateTenant | P5: `POST /api/v3/core/users/` with an existing username returned 400 `{"username":["This field must be unique."]}`. CreateTenant deletes the tenant row when user creation throws (`TenantLifecycleGrpcService.cs:42-46`) |
| 8 | LoadTest holds no Authentik admin credential | `Program.cs` reads no Authentik token environment variable; its Authentik use is the flow-executor clients (`:117-121`, `:148-157`) |
| 9 | CreateTenant returns the link | `TenantLifecycleGrpcService.cs:55` `AdminRecoveryLink = adminUserResult.RecoveryLink ?? string.Empty`; `tenant_lifecycle.proto:56` `string admin_recovery_link = 4` |
| 10 | Changing `EnsureTenantProvisionedAsync`'s return type is safe | `git grep` in `Iverson.LoadTest*`: one definition (`Program.cs:351`), one call (`:112`), and the only `CreateTenantAsync` call (`:368`) |
| 11 | `SendAsync` supplies the Host and Accept headers and the CSRF echo for any URL | `AuthentikFlowExecutorClient.cs:122-143`; `MintAsync` builds its executor URL from `identity.BaseUrl` (`:272`) |
| 12 | The login identity uses `tenantAdminPassword` with the shared base URL and Host header | `Program.cs:117-121` |
| 13 | The client is `IDisposable`, and the CSRF read is harmless with a fake handler | `:22` `: IDisposable`; `:138` reads the client's own `CookieContainer`, which stays empty when a fake handler is injected |
| 14 | A throw in the bootstrap exits 1 with the reason | `Program.cs:124-128` |
| 15 | The test project can host the new tests | `Iverson.LoadTest.Tests.csproj` references `Iverson.LoadTest` plus xunit 2.9.3 and FluentAssertions 8.11.0. `NullLogger<T>` arrives through `Microsoft.Extensions.Logging.Console` (LoadTest's package) and is already used by the other server test projects. The project is in `Iverson.slnx`. |
| 16 | kind ships the same recovery flow | `charts/authentik/templates/blueprints-configmap.yaml:6` globs `blueprints/*.yaml`, which includes `recovery-flow.yaml`, mounted at `/blueprints/custom` on the server and worker |
| 17 | DeleteTenant can't roll back a failed recovery | `TenantLifecycleGrpcService.cs:73-84`: status `deleted` plus `DeactivateAllUsersInTenantAsync`, with no row or user deletion |
| 18 | `SendAsync` doesn't follow redirects, and Authentik answers each completed recovery stage with a body-less `302` back to the same executor URL | `AuthentikFlowExecutorClient.cs:47` `AllowAutoRedirect = false`; `:25` `MaxFlowStages = 20`. P6: every `302` (after the POST and after the MFA-skip and user-write GETs) had `Content-Type: text/html`, an empty body that throws `JsonReaderException` when parsed, and `Location` equal to the request's executor path and query, so re-GETting the built URL is equivalent to following it |
| 19 | The terminal check never reports a false success | P6, one member per failure class: a too-short password → POST `200 ak-stage-prompt` with `response_errors`, login rejected; a reused token → GET prompt, POST `302`, GET `302`, GET `200 ak-stage-access-denied`, the second password never set; an invalid token → its first GET still returns the prompt, and its post-POST redirects end `ak-stage-access-denied` (CDR round 1, RP4). None ends in `xak-flow-redirect` |

## Verified plan-level assumptions

Every row was verified at plan-write time (2026-10-02) by applying both tasks' code, verbatim as written below, to a throwaway detached worktree of `789ae718`. That worktree has since been removed.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.LoadTest.Tests/Auth/` and the test file don't exist yet. The SDK-style test project compiles a new subfolder file with no `.csproj` change. | `ls …/Iverson.LoadTest.Tests/Auth` gave "No such file or directory". After the file was created, `dotnet test --filter FullyQualifiedName~AuthentikFlowExecutorClientTests` compiled and ran it (row 8). |
| 2 | Code validity | The test file fails red against the current client | Before implementation: `error CS1729: 'AuthentikFlowExecutorClient' does not contain a constructor that takes 3 arguments`, and `CS1061 … 'SetPasswordFromRecoveryLinkAsync'` at every call site |
| 3 | Code validity | `Uri.AbsoluteUri` keeps `%3D` escaped, so the expected executor URL matches | The happy-path test asserts `…/?query=flow_token%3Dtok123` against `RequestUri.AbsoluteUri` and passes (row 8) |
| 4 | Code validity | `SendAsync`'s Host header reaches a fake handler, and `HttpClient` over a fake handler doesn't follow redirects | The happy-path test asserts `Host == "authentik-server:9000"` and five requests, with each scripted 302 seen exactly once; it passes. CDR-2 established the same independently. |
| 5 | Code validity | FluentAssertions 8.11.0 supports `ThrowAsync<T>().WithMessage`, `AllSatisfy`, `ContainSingle().Which` and `HaveCount`; `NullLogger<T>` and `System.Linq` resolve | All 6 test cases compile and pass (row 8). Both projects have `<ImplicitUsings>enable</ImplicitUsings>`. |
| 6 | Code validity | `HttpStatusCode.Redirect` is the 302 that Authentik and the fake return, and `ReadComponentAsync` rejects anything other than 200 with `InvalidOperationException` | The happy path follows three `HttpStatusCode.Redirect` responses to completion. The live check (row 12) follows real 302s. |
| 7 | Function signature | The generated `Tenant` exposes `AdminRecoveryLink` (`string`) | `TenantLifecycleGrpcService.cs:55` sets it. Task 2's `return tenant.AdminRecoveryLink;` compiles (row 9). |
| 8 | Command | `dotnet test Iverson.Server/Iverson.LoadTest.Tests --filter FullyQualifiedName~AuthentikFlowExecutorClientTests` runs exactly the new tests | `Passed! - Failed: 0, Passed: 6` (5 tests, one a two-case Theory) |
| 9 | Command | `dotnet build Iverson.Server/Iverson.LoadTest` and `dotnet test Iverson.Server/Iverson.LoadTest.Tests` succeed after both tasks | Build: `0 Warning(s) 0 Error(s)`. Tests: `Passed! - Failed: 0, Passed: 119` (113 existing plus 6 new). |
| 10 | Consumer impact | The other consumers of `AuthentikFlowExecutorClient` still compile. They are `Program.cs:117/148/153` and `Iverson.ClientConformance/TokenBroker.cs:67/82` (`ClientConformance.csproj:10` references `Iverson.LoadTest`); all use the 2-argument constructor, which is unchanged and doesn't clash with the new 3-argument one. | `git grep 'new AuthentikFlowExecutorClient('` gives those 5 sites. `dotnet build Iverson.Server/Iverson.ClientConformance` gives `0 Error(s)`. |
| 11 | Consumer impact | `EnsureTenantProvisionedAsync` has one call site, so changing its return type to `Task<string?>` is safe | `git grep` in `Iverson.Server/Iverson.LoadTest*`: definition `Program.cs:351`, one call at `:112` (inherited row 10) |
| 12 | Code validity | The live-check program passes end to end: a passwordless `tenant-admins` user plus its recovery link, then `SetPasswordFromRecoveryLinkAsync`, then the real `MintAsync` on a separate client (password, TOTP enrolment, authorization-code token) | `LIVE CHECK PASS: live-check-20261003010326 logged in with the configured password (access token 1610 chars).` Run on an isolated Authentik (`-p planprobe`). |
| 13 | Code validity | The live check is falsifiable: without the recovery call, the same program fails with the original bug | With the `SetPasswordFromRecoveryLinkAsync` line removed: `Unhandled exception. System.InvalidOperationException: Authentication flow did not complete after 20 stages` |
| 14 | Code validity | The tests catch the regressions that matter | Three mutants, each restored afterwards: no redirect loop → 2 failed; the link's host used instead of `BaseUrl` → 1 failed; the prompt check skipped → 1 failed |
| 15 | Command | The live check's OAuth parameters match the compose blueprint | `service-clients.yaml`: provider `iverson-loadtest-human`, `client_id: "dev-iverson-loadtest-human-client-id"`, `redirect_uris` `strict` `http://localhost/placeholder-callback`, the same as `Program.cs:50-51`'s defaults |
| 16 | File path | `MintAsync` caches the TOTP secret at `~/.cache/iverson/acting-user-totp-secret-compose-<username>.txt` | `AuthentikFlowExecutorClient.cs:103-107` (`CacheDir`, `CachePath`). After the live check: `acting-user-totp-secret-compose-live-check-20261003010326.txt` existed, and it was deleted. |
| 17 | Ordering | Task 2 consumes Task 1's `SetPasswordFromRecoveryLinkAsync`, so Task 1 must land first. Task 1 consumes nothing from Task 2. | Task 2's `Program.cs` code calls the method. Task 1's files don't reference `Program.cs`. |
| 18 | Command | Commit messages are lowercase and imperative, with no prefix | `git log --format=%s -10`, for example `add the loadtest tenant-admin password design` |

## Tasks

### Task 1: `SetPasswordFromRecoveryLinkAsync`, test-first

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs` (after the existing constructor, `:51`; before `RefreshAsync`, `:304`)

**Interfaces:**
- Produces: `public Task SetPasswordFromRecoveryLinkAsync(string recoveryLink)` and `public AuthentikFlowExecutorClient(AuthentikIdentityConfig, ILogger<AuthentikFlowExecutorClient>, HttpMessageHandler)`.

- [ ] **Step 1: Write the tests**

Create `Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

public class AuthentikFlowExecutorClientTests
{
    private const string Link = "http://authentik-server:9000/if/flow/iverson-recovery/?flow_token=tok123";
    private const string ExecutorUrl =
        "http://localhost:9000/api/v3/flows/executor/iverson-recovery/?query=flow_token%3Dtok123";
    private const string Password = "dev-only-not-for-production-tenant-admin-password-0123456789";

    private sealed record Sent(HttpMethod Method, string Url, string? Host, string Body);

    // Answers each request with the next scripted response and records what was sent. HttpClient
    // itself never follows redirects (HttpClientHandler does), so this fake sees every 302 exactly
    // as the client's real AllowAutoRedirect = false handler returns it.
    private sealed class FakeHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Host, body));
            return responses[Requests.Count - 1];
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Authentik answers each completed stage with a body-less 302 back to the same executor URL.
    private static HttpResponseMessage Redirect() =>
        new(HttpStatusCode.Redirect)
        {
            Content = new StringContent("", Encoding.UTF8, "text/html"),
            Headers = { Location = new Uri("/api/v3/flows/executor/iverson-recovery/?query=flow_token%3Dtok123", UriKind.Relative) },
        };

    private static AuthentikFlowExecutorClient Client(FakeHandler handler) => new(
        new AuthentikIdentityConfig(
            "iverson-loadtest-tenant-admin", Password, "dev-iverson-loadtest-human-client-id",
            "http://localhost/placeholder-callback", "http://localhost:9000", "authentik-server:9000", "compose"),
        NullLogger<AuthentikFlowExecutorClient>.Instance,
        handler);

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_PostsThePassword_AndFollowsTheRedirectsToCompletion()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Redirect(), Redirect(), Redirect(),
            Json("""{"component":"xak-flow-redirect","to":"/"}"""));

        await Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        handler.Requests.Should().HaveCount(5, "nothing is sent after the xak-flow-redirect");
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Url.Should().Be(ExecutorUrl, "only the slug and flow_token come from the link, not its host");
        handler.Requests[0].Host.Should().Be("authentik-server:9000");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Url.Should().Be(ExecutorUrl);
        using (var body = JsonDocument.Parse(handler.Requests[1].Body))
        {
            body.RootElement.GetProperty("password").GetString().Should().Be(Password);
            body.RootElement.GetProperty("password_repeat").GetString().Should().Be(Password);
        }
        handler.Requests.Skip(2).Should().AllSatisfy(r =>
        {
            r.Method.Should().Be(HttpMethod.Get);
            r.Url.Should().Be(ExecutorUrl);
        });
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_WithAuthentiksError_WhenThePasswordIsRejected()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Json("""
                 {"component":"ak-stage-prompt","response_errors":{"non_field_errors":[
                   {"string":"Password needs to be 8 characters or longer.","code":"invalid"}]}}
                 """));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Password needs to be 8 characters or longer.*");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_AndSendsNoPost_WhenThePromptIsNotOffered()
    {
        var handler = new FakeHandler(Json("""{"component":"ak-stage-access-denied"}"""));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ak-stage-access-denied*");
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://authentik-server:9000/if/flow/iverson-recovery/")]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_AndSendsNothing_ForAMissingOrMalformedLink(string link)
    {
        var handler = new FakeHandler();

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(link);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_WhenTheRedirectsEndAnywhereButCompletion()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Redirect(), Redirect(),
            Json("""{"component":"ak-stage-access-denied"}"""));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ak-stage-access-denied*");
        handler.Requests.Should().HaveCount(4);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests --filter FullyQualifiedName~AuthentikFlowExecutorClientTests
```

Expected: the build fails with `CS1729` (no 3-argument constructor) and `CS1061` (no `SetPasswordFromRecoveryLinkAsync`).

- [ ] **Step 3: Add the test-only constructor**

In `AuthentikFlowExecutorClient.cs`, directly after the existing constructor (which ends at `:51`) and before the `// RFC 6238 TOTP` comment, insert:

```csharp
    // For tests: drives the client over a caller-supplied handler. HttpClient never follows
    // redirects itself (HttpClientHandler does), so the handler sees each 302 exactly as the
    // constructor above's AllowAutoRedirect = false handler returns it. The cookie container
    // stays empty, so SendAsync sends no CSRF header.
    public AuthentikFlowExecutorClient(
        AuthentikIdentityConfig identity,
        ILogger<AuthentikFlowExecutorClient> logger,
        HttpMessageHandler handler)
    {
        this.identity = identity;
        this.logger = logger;
        _http = new HttpClient(handler);
    }
```

- [ ] **Step 4: Add `SetPasswordFromRecoveryLinkAsync` and `ReadComponentAsync`**

Directly before `public async Task<MintedToken> RefreshAsync(` (`:304`), insert:

```csharp
    /// <summary>
    /// Sets <see cref="AuthentikIdentityConfig.Password"/> as the user's password by driving the
    /// one-time recovery link CreateTenant returns (Tenant.admin_recovery_link) through Authentik's
    /// flow executor. Only the link's flow slug and flow_token are used: its host is the API
    /// server's internal view of Authentik, so requests go to identity.BaseUrl with the configured
    /// Host header, like MintAsync's. The link is a bearer credential, so no message includes it.
    /// </summary>
    public async Task SetPasswordFromRecoveryLinkAsync(string recoveryLink)
    {
        if (string.IsNullOrEmpty(recoveryLink))
            throw new InvalidOperationException(
                $"CreateTenant returned no admin recovery link, so '{identity.Username}' has no password " +
                "and the data plane cannot log in.");
        if (!Uri.TryCreate(recoveryLink, UriKind.Absolute, out var link))
            throw new InvalidOperationException("The admin recovery link is malformed: it is not an absolute URL.");
        var slug = link.AbsolutePath.TrimEnd('/').Split('/')[^1];
        var token = HttpUtility.ParseQueryString(link.Query)["flow_token"];
        if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(token))
            throw new InvalidOperationException("The admin recovery link is malformed: it has no flow slug or no flow_token.");

        var flowUrl = $"{identity.BaseUrl}/api/v3/flows/executor/{slug}/?query=" +
                      Uri.EscapeDataString($"flow_token={token}");

        var prompt = await ReadComponentAsync(await SendAsync(HttpMethod.Get, flowUrl));
        if (prompt != "ak-stage-prompt")
            throw new InvalidOperationException($"The recovery flow did not offer the password prompt; it answered '{prompt}'.");

        var resp = await SendAsync(HttpMethod.Post, flowUrl,
            JsonBody(new { password = identity.Password, password_repeat = identity.Password }));

        // A 200 here is a rejection: Authentik re-renders the prompt with response_errors.
        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            using var rejected = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = rejected.RootElement;
            var errors = root.TryGetProperty("response_errors", out var re) && re.ValueKind == JsonValueKind.Object
                ? re.EnumerateObject()
                    .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray() : [])
                    .Select(e => e.TryGetProperty("string", out var text) ? text.GetString() : null)
                    .OfType<string>()
                    .ToList()
                : [];
            throw new InvalidOperationException(errors.Count > 0
                ? $"Authentik rejected the tenant admin's password: {string.Join(" ", errors)}"
                : $"The recovery flow answered the password with '{(root.TryGetProperty("component", out var c) ? c.GetString() : null)}' and no error; the password was not set.");
        }

        // Each completed stage (prompt, MFA check, user write) answers a body-less 302 back to the
        // same executor URL; this client doesn't follow redirects, so follow them here.
        for (var i = 0; i < MaxFlowStages && resp.StatusCode == System.Net.HttpStatusCode.Redirect; i++)
            resp = await SendAsync(HttpMethod.Get, flowUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"The recovery flow was still redirecting after {MaxFlowStages} requests.");

        var end = await ReadComponentAsync(resp);
        if (end != "xak-flow-redirect")
            throw new InvalidOperationException($"The recovery flow ended at '{end}', not 'xak-flow-redirect'; the password was not set.");
    }

    private static async Task<string?> ReadComponentAsync(HttpResponseMessage resp)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException($"The recovery flow answered HTTP {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("component", out var c) ? c.GetString() : null;
    }
```

- [ ] **Step 5: Run the new tests, then the whole project**

```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests --filter FullyQualifiedName~AuthentikFlowExecutorClientTests
dotnet test Iverson.Server/Iverson.LoadTest.Tests
```

Expected: `Passed: 6` for the filter, then `Failed: 0, Passed: 119` for the whole project.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs Iverson.Server/Iverson.LoadTest.Tests/Auth/AuthentikFlowExecutorClientTests.cs
git commit -m "set the loadtest tenant admin's password from a recovery link" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Wire it into the LoadTest bootstrap, and the live check

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs:111-121` (the bootstrap block) and `:351-375` (`EnsureTenantProvisionedAsync`)
- Scratch, not committed: a live-check console project outside the repo

**Interfaces:**
- Consumes: Task 1's `SetPasswordFromRecoveryLinkAsync`, used through the existing 2-argument constructor.

- [ ] **Step 1: Return the recovery link from `EnsureTenantProvisionedAsync`**

Replace the whole function (`Program.cs:351-375`) with:

```csharp
// Returns CreateTenant's one-time admin recovery link when this call creates the tenant, or null
// when the tenant already exists.
static async Task<string?> EnsureTenantProvisionedAsync(
    string grpcUrl, string adminToken, string tenantId, string displayName,
    string adminUsername, string adminEmail)
{
    using var channel = GrpcChannel.ForAddress(grpcUrl);
    var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
    var headers = new Metadata { { "authorization", $"Bearer {adminToken}" } };

    var existing = await client.ListTenantsAsync(new ListTenantsRequest(), headers);
    if (existing.Tenants.Any(t => t.TenantId == tenantId))
        return null;

    // No password param (CSR round-2 finding #4): onboarding is passwordless, so CreateTenant
    // creates the admin with no password and returns a one-time recovery link instead. The caller
    // sets the admin's password through that link
    // (AuthentikFlowExecutorClient.SetPasswordFromRecoveryLinkAsync).
    var tenant = await client.CreateTenantAsync(new CreateTenantRequest
    {
        TenantId      = tenantId,
        DisplayName   = displayName,
        AdminUsername = adminUsername,
        AdminEmail    = adminEmail,
    }, headers);
    return tenant.AdminRecoveryLink;
}
```

- [ ] **Step 2: Set the password in the bootstrap block**

Replace `Program.cs:111-121`, from `var adminToken = await MintClientCredentialsTokenAsync(clientCredentials);` through the `tenantAdminTokenProvider = …;` statement, with:

```csharp
        var adminToken = await MintClientCredentialsTokenAsync(clientCredentials);
        var adminRecoveryLink = await EnsureTenantProvisionedAsync(
            grpcUrl, adminToken, tenantProvisionId, "Iverson LoadTest (dynamic)",
            tenantAdminUsername, tenantAdminEmail);

        var tenantAdminLoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var tenantAdminIdentity = new AuthentikIdentityConfig(
            tenantAdminUsername, tenantAdminPassword, actingUserClientId, actingUserRedirectUri,
            actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget);
        if (adminRecoveryLink is not null)
        {
            // A separate client, so a separate cookie jar: the recovery flow ends by logging the
            // user in, and that session must not reach the login client below.
            using var recoveryClient = new AuthentikFlowExecutorClient(
                tenantAdminIdentity, tenantAdminLoggerFactory.CreateLogger<AuthentikFlowExecutorClient>());
            await recoveryClient.SetPasswordFromRecoveryLinkAsync(adminRecoveryLink);
            Console.WriteLine("Set the tenant admin's password from CreateTenant's recovery link.");
        }
        tenantAdminTokenProvider = new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            tenantAdminIdentity, tenantAdminLoggerFactory.CreateLogger<AuthentikFlowExecutorClient>()));
```

The `catch` at `:124-128` is unchanged. A throw from `SetPasswordFromRecoveryLinkAsync` reaches it and exits 1 with `Tenant provisioning failed: <message>`.

- [ ] **Step 3: Build and test**

```bash
dotnet build Iverson.Server/Iverson.LoadTest
dotnet build Iverson.Server/Iverson.ClientConformance
dotnet test Iverson.Server/Iverson.LoadTest.Tests
```

Expected: both builds report `0 Error(s)` (LoadTest also `0 Warning(s)`), and the tests report `Failed: 0, Passed: 119`.

- [ ] **Step 4: Live check against an isolated Authentik**

Run each command from an absolute path; the shell's working directory does not persist between calls. `<WORKTREE_ROOT>` is the absolute path of the worktree, and `<SCRATCH>` is any directory outside the repo.

1. **Start an isolated Authentik.** This never touches the main stack's containers or volumes:

   ```bash
   grep -v '^\s*container_name:' /home/ben/repositories/Iverson/Iverson.Server/docker-compose.yml > <SCRATCH>/live-check-compose.yml
   cd /home/ben/repositories/Iverson/Iverson.Server && docker compose -p tapwlive --project-directory /home/ben/repositories/Iverson/Iverson.Server -f <SCRATCH>/live-check-compose.yml up -d postgres redis authentik-migrate authentik-server authentik-worker
   ```

   The project directory is the main checkout on purpose. It holds the untracked `.env` with the compose secrets, and this plan changes no blueprint, so its blueprints are the same as the worktree's. Wait until both `GET http://127.0.0.1:9000/api/v3/core/applications/?slug=iverson-loadtest-human` and `GET http://127.0.0.1:9000/api/v3/flows/instances/?slug=iverson-recovery` report `pagination.count == 1`. Authenticate with `Authorization: Bearer $TOK`, where `TOK` is `AUTHENTIK_BOOTSTRAP_TOKEN` from `/home/ben/repositories/Iverson/Iverson.Server/.env`; read it into a variable and never print it. Expect roughly 1–2 minutes.

2. **Create the live-check project** in `<SCRATCH>/live-check/`, with `live-check.csproj`:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net10.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <ProjectReference Include="<WORKTREE_ROOT>/Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj" />
     </ItemGroup>
   </Project>
   ```

   and `Program.cs`:

   ```csharp
   using System.Net.Http.Headers;
   using System.Text;
   using System.Text.Json;
   using Iverson.LoadTest.Auth;
   using Microsoft.Extensions.Logging.Abstractions;

   const string AuthentikHost = "authentik-server:9000";
   const string Password = "dev-only-not-for-production-tenant-admin-password-0123456789";
   var user = $"live-check-{DateTime.UtcNow:yyyyMMddHHmmss}";

   using var admin = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9000") };
   admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("TOK"));
   admin.DefaultRequestHeaders.Host = AuthentikHost;

   async Task<JsonElement> PostJson(string path, object body)
   {
       var resp = await admin.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
       resp.EnsureSuccessStatusCode();
       return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
   }

   // What IdpAdminClient.CreateUserAsync does for CreateTenant: a passwordless tenant-admins user plus a recovery link.
   var groups = JsonDocument.Parse(await admin.GetStringAsync("/api/v3/core/groups/?name=tenant-admins")).RootElement;
   var groupPk = groups.GetProperty("results")[0].GetProperty("pk").GetString();
   var created = await PostJson("/api/v3/core/users/", new
   {
       username = user, email = $"{user}@iverson.local", name = user, is_active = true,
       attributes = new { tenant_id = "live-check" }, groups = new[] { groupPk },
   });
   var link = (await PostJson($"/api/v3/core/users/{created.GetProperty("pk").GetRawText()}/recovery/", new { }))
       .GetProperty("link").GetString()!;

   // LoadTest's compose defaults (Program.cs): base URL, Host header, loadtest-human OAuth client.
   var identity = new AuthentikIdentityConfig(
       user, Password, "dev-iverson-loadtest-human-client-id", "http://localhost/placeholder-callback",
       "http://localhost:9000", AuthentikHost, "compose");

   using (var recovery = new AuthentikFlowExecutorClient(identity, NullLogger<AuthentikFlowExecutorClient>.Instance))
       await recovery.SetPasswordFromRecoveryLinkAsync(link);

   using (var login = new AuthentikFlowExecutorClient(identity, NullLogger<AuthentikFlowExecutorClient>.Instance))
   {
       var token = await login.MintAsync();
       Console.WriteLine($"LIVE CHECK PASS: {user} logged in with the configured password (access token {token.AccessToken.Length} chars).");
   }
   ```

3. **Run it** with `TOK` set: `cd <SCRATCH>/live-check && TOK=$TOK dotnet run`.

   Expected: `LIVE CHECK PASS: live-check-<timestamp> logged in with the configured password (access token N chars).`

4. **Negative control.** Delete the line `    await recovery.SetPasswordFromRecoveryLinkAsync(link);` (leave its `using` statement with an empty `{ }` body) and run it again.

   Expected: `Unhandled exception. System.InvalidOperationException: Authentication flow did not complete after 20 stages`.

5. **Clean up.**

   ```bash
   cd /home/ben/repositories/Iverson/Iverson.Server && docker compose -p tapwlive --project-directory /home/ben/repositories/Iverson/Iverson.Server -f <SCRATCH>/live-check-compose.yml down -v
   rm -f ~/.cache/iverson/acting-user-totp-secret-compose-live-check-*.txt
   ```

   Confirm that `docker ps -a`, `docker volume ls` and `docker network ls` show nothing named `tapwlive`, and that the `iversonserver` project's containers are still present and `Exited`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "set the loadtest tenant admin's password from createtenant's recovery link" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

## Known issues inherited from spec

These are inherited verbatim. They exist in the implementation by design, accepted by the user during brainstorming.

Accepted by Ben under scope A, 2026-10-02:

- **A tenant that already exists without a password stays broken.** That covers tenants created before this change, and any whose recovery step failed after CreateTenant succeeded. LoadTest skips creation for them, so their data-plane logins fail on every call, as today.
  - **Manual fix:** set the user's password, for example `docker exec iverson-authentik-worker ak shell` then `User.objects.get(username="iverson-loadtest-tenant-admin").set_password(<password>)` and `.save()`, as done on 2026-09-28.
  - **Deleting and recreating the tenant is no remedy:** DeleteTenant only soft-deletes, marking the row `deleted` and deactivating the user, so a recreate collides on both.
- **After a recovery failure, the bootstrap's existing second error line can mislead.** It reads "Is the Iverson API running, and does IVERSON_CLIENT_SCOPE include 'admin schema_admin'?" and is printed for every provisioning failure. The first line carries the real reason.

