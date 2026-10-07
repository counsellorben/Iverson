# CSR Round 10 Sub-project C2: Orchestrator Workload Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-06-csr-round10-orchestrator-workload-identity-design.md` (commit SHA: `59782440`)

**Goal:** on every Helm profile, the API reaches Authentik's admin API with a short-lived access token minted from its own Kubernetes service-account token, and the static orchestrator token and password are removed. Compose keeps the static token.

**Architecture:**
- **API:** a new `DelegatingHandler` on the `iverson.authentik` client exchanges the projected token for a 5-minute access token (`client_credentials` with a `jwt-bearer` assertion). It caches the token until 60 seconds before expiry.
- **Authentik:** a templated `orchestrator-federation.yaml` blueprint trusts the cluster's service-account keys. A loopback-only `kubectl proxy` relay, running as a native sidecar in the Authentik worker, serves those keys. A policy pins the API's `sub` and audience, and requires the pre-created workload user.
- **Static credential:** `service-clients.yaml` pre-creates that user and removes the old user and token.

**Tech stack:**
- .NET 10 (`Iverson.Api`, xUnit 2.9.3, FluentAssertions 8.11, NSubstitute 5.3);
- Helm 3, with the umbrella chart at `Iverson.Server/deploy/helm/iverson`;
- Authentik 2026.5.3;
- `registry.k8s.io/kubectl:v1.37.1`;
- kind with podman, for the live check.

---

## Global Constraints

Copied from the spec, plus the session rules every task must hold to.

**From the spec:**
- **One branch, one merge.** No proto or SDK change.
- **Cloud profiles get static verification only:** render, kubeconform and the guards. Behaviour is verified live on a throwaway kind cluster.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored.** Commit docs files with `git add -f`.
- **The user's running compose stack is off-limits.** Never start, stop or modify its `iversonserver` containers, volumes, networks or images (`iverson-api:latest`, `iverson-api:0.1.0`, `localhost/iverson-api:0.1.0`), or another session's `rfdt-qdrant`/`rfdt-tei` containers and `tei_models` volume. Never run `docker compose up` against `Iverson.Server/docker-compose.yml`. Live checks use throwaway resources, which they delete afterwards.
- **Memory.** Run one heavy process at a time, because a WSL OOM kills the whole VM. Wrap builds and test runs in `systemd-run --user --scope -q -p MemoryMax=…`. Stage kind loads under `/var/tmp`, never `/tmp`.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity` on branch `csr10-orchestrator-workload-identity`. Use absolute paths, and never `cd` to the main checkout.
- **Commands.**
  - Use `command grep`, because plain `grep` is a gitignore-aware ugrep that skips `docs/`.
  - Every repo-wide grep excludes `.env*`, and nobody reads `Iverson.Server/.env`.
  - The docker CLI is podman.
  - Kill processes by PID, never `pkill -f`.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative and end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c2-sdd` (create it), never in the repo.

## File Structure

**Create:**
- `Iverson.Server/Iverson.Api/Tenancy/AuthentikOrchestratorTokenHandler.cs`: the federated token handler. It exchanges the projected token, caches the access token, and reports failures naming the step.
- `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorTokenHandlerTests.cs`: unit tests for the handler.

**Modify:**
- `Iverson.Server/Iverson.Api/Program.cs` (`:384-396`): attach the handler in federated mode.
- `Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs` (`ValidateStartup`): enforce exactly one credential mode.
- `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`: give `GoodConfig` a mode, and add the guard and wiring tests.
- `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs`: one federation `[Fact]` against the live fixture.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml`: `jwksRelay.image` and `resources.jwksRelay`.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml`: the relay sidecar and its two volumes.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml`: the templated `orchestrator-federation.yaml` key.
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml`:
  - drop the two orchestrator credentials and their Secrets;
  - add the workload user;
  - add `state: absent` entries for the old user and token.
- `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml`: the worker-only API-server egress policy, and the guard message.
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`: federated env, plus the projected token volume and mount.
- `docs/runbooks/csr10-infrastructure-cutover.md`: a new §9 for the C2 cutover.

## Inherited from spec

The spec's 45 verified assumptions (`docs/specs/2026-10-06-csr-round10-orchestrator-workload-identity-design.md`, "Verified assumptions") are trusted as ground truth and not re-verified here. Each spec row carries its evidence:

1. The named client sets its bearer from `Authentik:AdminToken`, and a handler can be attached.
2. The startup guard runs only for the API role outside Development.
3. Only `IdpAdminClient` uses the named client.
4. 8443 forwards `/application/o/token/`.
5. The exchange returns `access_token` and `expires_in`.
6. The access token carries exactly the orchestrator role's rights.
7. An invalid bearer gets 403, not 401.
8. The fixture holds an admin credential.
9. A blueprint can create the source with an `oidc_jwks_url`, which is fetched on save.
10. The blueprint runs in the worker.
11. A source mapping sets the username.
12. The pre-created user keeps its group and is never deleted.
13. Policies see `oauth_jwt` before any user exists, and `aud`/`iss` aren't checked.
14. `policy_engine_mode: all` is valid.
15. The public listener marks token requests.
16. Federated users get the role's per-user grants.
17. `state: absent` keeps the grants on earlier users.
18. A user with no password gets an unusable one.
19. The provider attrs are valid, and no client secret is needed.
20. The wrong or API audience is refused.
21. The JWKS refresh runs every 3 hours, in the worker.
22. Any service account can read the JWKS, and anonymous reads are refused.
23. The audience token works with automount off, and the API server rejects it.
24. The relay runs non-root with a read-only root filesystem.
25. The kubectl image exists.
26. An exec startup probe gates the main container, and a TCP probe can't reach loopback.
27. One invalid entry fails the whole blueprint, with an hourly retry.
28. D's egress helper is reusable.
29. The worker label is `app: <release>-authentik-worker`.
30. The API service account is `<release>-api`.
31. Dependabot and image-scan see nested and rendered images.
32. The API already reaches Authentik on 8443.
33. Nothing else reads the two Secrets.
34. LoadTest and conformance don't use the token.
35. Native sidecars are on in every target version.
36. Managed clusters rotate their keys.
37. Both Secrets carry `resource-policy: keep`.
38. The user-gated policy refuses early exchanges.
39. Compose and the fixture load every file under `blueprints/`.
40. The old worker can take an apply mid-rollout, and `ak apply_blueprint` re-applies.
41. The relay works in the worker's pod context with a newer kubectl.
42. The `/ready/` probe decouples the start, and `--www` is unfiltered.
43. The relay survives token rotation.
44. The old worker sees new blueprint content.
45. The refresh uses `oidc_jwks_url` alone.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | The handler and its test file are new | `ls` of both paths: "No such file or directory" |
| 2 | File path | The runbook's last section is `## 8. EKS`, so C2 becomes §9 | `command grep -n '^## ' docs/runbooks/csr10-infrastructure-cutover.md`: `201:## 8. EKS` |
| 3 | File path | Every chart file T4 modifies exists | Each was read at plan time: `charts/authentik/{values.yaml,templates/deployment-worker.yaml,templates/blueprints-configmap.yaml,templates/secret-service-clients.yaml}`, `templates/networkpolicies.yaml`, `charts/api/templates/deployment.yaml` |
| 4 | Signature | `HttpClient` hands the pipeline an absolute `RequestUri`, so the handler derives the token URI from it | Prototype (scratch console, net10, `Microsoft.Extensions.Http`): the token request went to `https://iverson-authentik:8443/application/o/token/` from a relative `GetAsync` on a client with `BaseAddress` |
| 5 | Signature | A `DelegatingHandler` may call `base.SendAsync` twice in one `SendAsync` (exchange, then the call) | Same prototype: 2 requests recorded in order (token, then API), and the API call carried `Bearer at-1` |
| 6 | Signature | `AddHttpMessageHandler(...)` sits above `ConfigurePrimaryHttpMessageHandler`'s handler | Prototype wired both and both requests reached the primary fake. The T2 wiring test asserts the chain ends in `SocketsHttpHandler` |
| 7 | Signature | Tests can subclass `TimeProvider` and override `GetUtcNow` | Prototype `FakeClock : TimeProvider`: cached at 239 s, re-mint at 240 s |
| 8 | Code validity | The T1–T3 test code compiles and runs on the repo's packages (xUnit 2.9.3, FluentAssertions 8.11) | A scratch worktree at `59782440` with T1–T3 applied: `dotnet build Iverson.Api.Tests` succeeded with no new warnings, and `dotnet test --filter "FullyQualifiedName~AuthentikOrchestratorTokenHandlerTests\|FullyQualifiedName~Tenancy.AuthentikTrustTests"` gave **Passed 42/42** |
| 9 | Code validity | The new tests can fail | Mutations: margin set to 0 failed `CachedToken_IsReused_UntilTheRefreshMargin`; the mode check removed failed `Guard_RefusesBothCredentialModes` and `Guard_RefusesNeitherCredentialMode` |
| 10 | Code validity | An RS256 assertion signed with `System.Security.Cryptography`, plus a JWKS built from `ExportParameters`, validate together | Prototype: `JsonWebTokenHandler.ValidateTokenAsync` against `JsonWebKeySet(jwks)` returned `IsValid` |
| 11 | Signature | The admin-API routes T3 uses exist in 2026.5.3 | `api_urlpatterns` in the image: `propertymappings/source/oauth`, `sources/oauth`, `providers/oauth2`, `propertymappings/provider/scope`, `core/applications`, `policies/expression`, `policies/bindings`, `flows/instances`, `crypto/certificatekeypairs`, `core/groups`, `core/users` |
| 12 | Signature | Creating a source through the API with static `oidc_jwks`, blank URLs and no `oidc_jwks_url` triggers no fetch | `sources/oauth/api/source.py:60-130`: fetches only when a well-known or JWKS URL is non-blank; the URL fields have `allow_blank`; `user_property_mappings`, `slug` and `enabled` are in `core/api/sources.py:44-49` |
| 13 | Signature | The provider serializer takes T3's fields | `providers/oauth2/api/providers.py:64-86`: `authorization_flow`, `client_type`, `grant_types`, `client_id`, `access_token_validity`, `signing_key`, `redirect_uris`, `property_mappings`, `issuer_mode`, `jwt_federation_sources` |
| 14 | Signature | The application serializer accepts `policy_engine_mode: "all"`; bindings take `policy`/`target`/`order`; expression policies take `expression` | `core/api/applications.py:121`; `policies/models.py:20-24` (`MODE_ALL = "all"`); `policies/api/bindings.py:70-79`; `policies/expression/api.py:22` |
| 15 | Consumer | The fixture's compose-only blueprint has `iverson-admin-orchestrators` (with the role) and `tenant-admins` | Spec VA39/CDR-1: `compose-only/service-clients.yaml:342-373`; the existing tests resolve `tenant-admins` through `GroupPkAsync` |
| 16 | Consumer | Every `ValidateStartup` caller outside Development is in `AuthentikTrustTests` | `Program.cs:466` is the only production call. `command grep 'UseEnvironment\|EnvironmentName' Iverson.Api.Tests` finds only `AuthentikTrustTests.cs:177`, and `AuthTestWebApplicationFactory` sets no environment (WebApplicationFactory's default is Development). Compose's API sets `Authentik__AdminToken`, so it passes as static |
| 17 | Consumer | No test asserts on the Helm blueprint's orchestrator user or token entries | `AuthentikOrchestratorRoleBlueprintTests.cs` checks only the role's permissions, InitialPermissions and `is_superuser: false`; T4 keeps all three |
| 18 | Consumer | `$adminOrchestratorPassword`/`$adminOrchestratorTokenKey` are used only at the sites T4 deletes | `command grep` hits only at `secret-service-clients.yaml:96-108` (computation), `:199`, `:209` (Secrets) and `:663`, `:673` (blueprint entries) |
| 19 | Consumer | No doc or runbook says the API uses the static token | `command grep -i 'AdminToken\|orchestrator token\|orchestrator-token' docs/runbooks Iverson.Server/deploy/kind`: no hits |
| 20 | Code validity | T4's edits render on all five overlays, and the assertions fail on the unmodified chart | Scratch copy of the chart at `59782440` plus `t4_edits.py`: `helm template` ×5 rendered; `check_t4.py` **PASS ×5** on the edited chart and **FAIL ×5** on the base; `helm lint` passed |
| 21 | Code validity | kubeconform accepts the native sidecar | Same renders: `kubeconform -ignore-missing-schemas`, **Invalid 0, Errors 0** on all five |
| 22 | Code validity | kube-score: criticals on every other container unchanged, and the relay adds exactly the 5 known classes | `kube-score score --output-format ci` diffed base vs new: 0 changed lines outside `(jwks-relay)` ×5. The relay has 5: CPU limit unset (spec sets none), ephemeral request unset, ephemeral limit unset, ImagePullPolicy not Always, low group ID. The last three already appear on every container in the base, `tls-proxy` and `worker` included |
| 23 | Consumer | image-scan's List step picks up the relay image from the render | `sed -n 's/^ *image: "\{0,1\}\([^" ]*\)"\{0,1\}$/\1/p'` on the AWS render prints `registry.k8s.io/kubectl:v1.37.1` |
| 24 | Consumer | Every overlay sets `networkPolicy.apiServerCidrs`, so the new policy renders | `values-laptop.yaml:20` plus D's overlays; `check_t4.py` asserted each overlay's CIDRs on the new policy |
| 25 | Command | The 9-project suite loop and filters are valid | C1 plan Task 6 Step 1 (same loop, passed 3797/0 on C1's merge); this plan's filter ran in row 8 |
| 26 | Command | The kind commands are current | C1 plan Task 6 Step 9 and D plan Task 10 Step 5: `KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml`, `setup.sh`, `build-and-load-image.sh <tag> iverson [--model-images --values <file>]`. `kind-config.yaml` exists |
| 27 | Command | `main`'s chart installs from `git archive` after `helm dependency build` | Row 20 rendered the `git archive 59782440` copy (equal to `main` at plan time) after `helm dependency build` |
| 28 | Command | `CreateTenant` can be called from a scratch console referencing `Iverson.Client.Core` | C1 plan Task 6 Step 12 (passed live). `Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj` exists, and `tenant_lifecycle.proto:15-19` still has `tenant_id`, `display_name`, `admin_username` and `admin_email` |
| 29 | Command | `kubectl create token <sa> --audience …` mints a projected-shape token | The spec's brainstorming probe used it on kind v1.31 for every assertion |
| 30 | Command | Commit messages are lowercase imperative | `git log --oneline -8` on `main` |
| 31 | Ordering | T2 and T3 use T1's class; T4 and T5 are independent of T1–T3; T6 runs last | T2's `Program.cs` and the T3 test construct `AuthentikOrchestratorTokenHandler` (T1). T4 touches only chart files, T5 only the runbook |

## Tasks

### Task 0: Branch

- [ ] **Step 1: Create the worktree.**

```bash
cd /home/ben/repositories/Iverson
git worktree add -b csr10-orchestrator-workload-identity .worktrees/csr10-orchestrator-workload-identity main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c2-sdd
```

Expected: the worktree is on the new branch, and HEAD equals `main`.

### Task 1: The federated token handler

**Files:**
- Create: `Iverson.Server/Iverson.Api/Tenancy/AuthentikOrchestratorTokenHandler.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorTokenHandlerTests.cs`

**Interfaces:**
- Produces: `internal sealed class AuthentikOrchestratorTokenHandler(string assertionPath, string clientId, TimeProvider timeProvider) : DelegatingHandler`, used by T2 (`Program.cs`) and T3 (integration test).

- [ ] **Step 1: Write the tests.** Create `AuthentikOrchestratorTokenHandlerTests.cs` with exactly this content:

```csharp
using System.Net;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round-10 #4 (C2): the federated Authentik credential. The handler exchanges the projected
/// service-account token for a short-lived access token, caches it until a minute before expiry,
/// and fails with a message that names the step and carries no credential.
/// </summary>
public sealed class AuthentikOrchestratorTokenHandlerTests : IDisposable
{
    private const string TokenPath = "/application/o/token/";
    private readonly string _dir = Directory.CreateTempSubdirectory("orchestrator-token-").FullName;
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-10-07T00:00:00Z"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Mirrors AuthentikAdminClientTests' FakeHttpMessageHandler: records every request and answers
    /// the token endpoint with a numbered access token (or the configured failure).
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public sealed record Seen(string Path, string Uri, string Body, string? Authorization);

        private int _minted;
        public List<Seen> Requests { get; } = [];
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public string? TokenBody { get; init; }
        public bool ThrowOnToken { get; init; }
        public TimeSpan Delay { get; init; }

        public int Mints => Requests.Count(r => r.Path == TokenPath);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new Seen(request.RequestUri!.AbsolutePath, request.RequestUri.ToString(), body, request.Headers.Authorization?.ToString()));

            if (request.RequestUri.AbsolutePath != TokenPath)
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (ThrowOnToken)
                throw new HttpRequestException("connection refused");
            if (TokenStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"The provided authorization grant is invalid\"}"),
                };
            var n = Interlocked.Increment(ref _minted);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TokenBody ?? $"{{\"access_token\":\"access-token-{n}\",\"token_type\":\"Bearer\",\"expires_in\":300,\"id_token\":\"id-token\"}}"),
            };
        }
    }

    private string WriteAssertion(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private HttpClient Client(string assertionPath, FakeHttpMessageHandler inner) =>
        new(new AuthentikOrchestratorTokenHandler(assertionPath, "iverson-admin-orchestrator", _clock) { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://iverson-authentik:8443"),
        };

    [Fact]
    public async Task FirstCall_ExchangesTheAssertion_ThenSendsTheAccessToken()
    {
        var inner = new FakeHttpMessageHandler();
        using var client = Client(WriteAssertion("projected-sa-token\n"), inner);

        await client.GetAsync("/api/v3/core/users/");

        inner.Requests.Should().HaveCount(2);
        var exchange = inner.Requests[0];
        exchange.Uri.Should().Be("https://iverson-authentik:8443/application/o/token/");
        exchange.Body.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=iverson-admin-orchestrator")
            .And.Contain("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer")
            .And.Contain("client_assertion=projected-sa-token&")
            .And.Contain("scope=goauthentik.io%2Fapi");
        inner.Requests[1].Path.Should().Be("/api/v3/core/users/");
        inner.Requests[1].Authorization.Should().Be("Bearer access-token-1");
    }

    [Fact]
    public async Task CachedToken_IsReused_UntilTheRefreshMargin()
    {
        var inner = new FakeHttpMessageHandler();
        using var client = Client(WriteAssertion("projected-sa-token"), inner);

        await client.GetAsync("/a");
        _clock.Now += TimeSpan.FromSeconds(239); // expires_in 300, margin 60: still fresh
        await client.GetAsync("/b");
        inner.Mints.Should().Be(1);

        _clock.Now += TimeSpan.FromSeconds(1);   // inside the margin
        await client.GetAsync("/c");
        inner.Mints.Should().Be(2);
        inner.Requests[^1].Authorization.Should().Be("Bearer access-token-2");
    }

    [Fact]
    public async Task EveryMint_ReReadsTheAssertionFile()
    {
        var inner = new FakeHttpMessageHandler();
        var path = WriteAssertion("first-assertion");
        using var client = Client(path, inner);

        await client.GetAsync("/a");
        File.WriteAllText(path, "rotated-assertion");
        _clock.Now += TimeSpan.FromSeconds(300);
        await client.GetAsync("/b");

        inner.Requests.Where(r => r.Path == TokenPath).Select(r => r.Body)
            .Should().SatisfyRespectively(
                first => first.Should().Contain("client_assertion=first-assertion"),
                second => second.Should().Contain("client_assertion=rotated-assertion"));
    }

    [Fact]
    public async Task ConcurrentCalls_MintOnce()
    {
        var inner = new FakeHttpMessageHandler { Delay = TimeSpan.FromMilliseconds(50) };
        using var client = Client(WriteAssertion("projected-sa-token"), inner);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/a")));

        inner.Mints.Should().Be(1);
    }

    public static TheoryData<string, string> Failures() => new()
    {
        { "missing-file", "could not be read" },
        { "empty-file", "is empty" },
        { "http-400", "HTTP 400 invalid_grant" },
        { "network", "Authentik token exchange failed: connection refused" },
        { "no-access-token", "returned no access_token" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failure_NamesTheStep_AndCarriesNoCredential(string failure, string expected)
    {
        const string assertion = "assertion-that-must-not-leak";
        var path = failure switch
        {
            "missing-file" => Path.Combine(_dir, "absent"),
            "empty-file" => WriteAssertion("  \n"),
            _ => WriteAssertion(assertion),
        };
        var inner = failure switch
        {
            "http-400" => new FakeHttpMessageHandler { TokenStatus = HttpStatusCode.BadRequest },
            "network" => new FakeHttpMessageHandler { ThrowOnToken = true },
            "no-access-token" => new FakeHttpMessageHandler { TokenBody = "{\"token_type\":\"Bearer\",\"id_token\":\"id-token\"}" },
            _ => new FakeHttpMessageHandler(),
        };
        using var client = Client(path, inner);

        var thrown = await FluentActions.Awaiting(() => client.GetAsync("/a")).Should().ThrowAsync<InvalidOperationException>();

        thrown.Which.Message.Should().Contain(expected)
            .And.NotContain(assertion)
            .And.NotContain("id-token")
            .And.NotContain("access-token");
        inner.Requests.Should().NotContain(r => r.Path != TokenPath, "no admin call is sent without a token");
    }
}
```

- [ ] **Step 2: Run them and see them fail.** They don't compile yet, because the handler doesn't exist.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikOrchestratorTokenHandlerTests" 2>&1 | command grep -E 'error CS|Passed!|Failed!' | head -3
```

Expected: `error CS0246` naming `AuthentikOrchestratorTokenHandler`.

- [ ] **Step 3: Write the handler.** Create `AuthentikOrchestratorTokenHandler.cs` with exactly this content:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #4 (C2): authenticates the Authentik admin client with the API's own Kubernetes
/// service-account token instead of a long-lived Authentik API token. The kubelet-projected token at
/// the assertion path is exchanged at Authentik's token endpoint (client_credentials with a
/// jwt-bearer client assertion) for a short-lived access token, which is cached until
/// <see cref="RefreshMargin"/> before it expires. The exchange goes through this handler's own inner
/// handler, so it uses the same CA-trusting hop as the admin calls.
/// </summary>
internal sealed class AuthentikOrchestratorTokenHandler(string assertionPath, string clientId, TimeProvider timeProvider)
    : DelegatingHandler
{
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _mint = new(1, 1);
    private CachedToken? _cached;

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(request.RequestUri!, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsFresh(CachedToken? cached) =>
        cached is not null && timeProvider.GetUtcNow() < cached.ExpiresAt - RefreshMargin;

    private async Task<string> GetAccessTokenAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (IsFresh(cached))
            return cached!.Value;

        await _mint.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (IsFresh(cached))
                return cached!.Value;

            var assertion = await ReadAssertionAsync(cancellationToken);
            using var exchange = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri, "/application/o/token/"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = assertion,
                    ["scope"] = "goauthentik.io/api",
                }),
            };

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(exchange, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new InvalidOperationException($"Authentik token exchange failed: {ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Authentik token exchange failed: HTTP {(int)response.StatusCode} {ErrorCode(body)}".TrimEnd() + ".");

                using var doc = ParseOrNull(body);
                if (doc is null
                    || !doc.RootElement.TryGetProperty("access_token", out var accessToken)
                    || accessToken.ValueKind != JsonValueKind.String
                    || accessToken.GetString() is not { Length: > 0 } value)
                    throw new InvalidOperationException("Authentik token exchange returned no access_token.");

                var lifetime = doc.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.Zero;
                _cached = new CachedToken(value, timeProvider.GetUtcNow() + lifetime);
                return value;
            }
        }
        finally
        {
            _mint.Release();
        }
    }

    private async Task<string> ReadAssertionAsync(CancellationToken cancellationToken)
    {
        string assertion;
        try
        {
            assertion = await File.ReadAllTextAsync(assertionPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Authentik orchestrator assertion file '{assertionPath}' could not be read.", ex);
        }

        assertion = assertion.Trim();
        if (assertion.Length == 0)
            throw new InvalidOperationException($"Authentik orchestrator assertion file '{assertionPath}' is empty.");
        return assertion;
    }

    // Only Authentik's error code (for example invalid_grant) reaches the message; never the body.
    private static string ErrorCode(string body)
    {
        using var doc = ParseOrNull(body);
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()!
            : "";
    }

    private static JsonDocument? ParseOrNull(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _mint.Dispose();
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 4: Run the tests.** Use the same command as Step 2.

Expected: `Passed!  - Failed:     0, Passed:     9`.

- [ ] **Step 5: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Tenancy/AuthentikOrchestratorTokenHandler.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorTokenHandlerTests.cs
git commit -m "add the federated authentik orchestrator token handler

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Configuration wiring and the startup guard

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs:387-396`
- Modify: `Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs` (`ValidateStartup` and its doc comment)
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs`

**Interfaces:**
- Consumes: T1's `AuthentikOrchestratorTokenHandler`.
- Produces: the configuration keys `Authentik:OrchestratorAssertionPath` and `Authentik:OrchestratorClientId`, which T4's Helm env sets.

- [ ] **Step 1: Write the tests.** Make three edits in `AuthentikTrustTests.cs`.

  **(a)** `GoodConfig` needs a credential mode, because the guard now requires one. Every existing guard test builds on it. Replace

```csharp
            ["Authentik:CaCertificatePath"] = WriteCaPem("guard-ca.crt"),
        };
```

  with

```csharp
            ["Authentik:CaCertificatePath"] = WriteCaPem("guard-ca.crt"),
            ["Authentik:AdminToken"] = "static-orchestrator-token",
        };
```

  **(b)** Insert this block directly above the `[Fact]` attribute of `Guard_SkipsDevelopment`, so `Guard_SkipsDevelopment` keeps its own `[Fact]`:

```csharp
    [Fact]
    public void Guard_RefusesBothCredentialModes() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v =>
            {
                v["Authentik:OrchestratorAssertionPath"] = "/var/run/secrets/iverson/authentik-orchestrator/token";
                v["Authentik:OrchestratorClientId"] = "iverson-admin-orchestrator";
            }), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Authentik:AdminToken*Authentik:OrchestratorAssertionPath*");

    [Fact]
    public void Guard_RefusesNeitherCredentialMode() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v => v["Authentik:AdminToken"] = null), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Authentik:AdminToken*Authentik:OrchestratorAssertionPath*");

    [Fact]
    public void Guard_RefusesFederatedModeWithoutAClientId() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v =>
            {
                v["Authentik:AdminToken"] = null;
                v["Authentik:OrchestratorAssertionPath"] = "/var/run/secrets/iverson/authentik-orchestrator/token";
            }), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Authentik:OrchestratorClientId*");

    [Fact]
    public void Guard_PassesTheFederatedMode() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v =>
            {
                v["Authentik:AdminToken"] = null;
                v["Authentik:OrchestratorAssertionPath"] = "/var/run/secrets/iverson/authentik-orchestrator/token";
                v["Authentik:OrchestratorClientId"] = "iverson-admin-orchestrator";
            }), "api", Env("Production"))).Should().NotThrow();

```

  **(c)** Append this theory as the class's last member, after `IdpAdminClient_ReachesAuthentikThroughTheCaTrustingHandler`:

```csharp

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdpAdminClient_UsesTheOrchestratorTokenHandler_OnlyInFederatedMode(bool federated)
    {
        using var factory = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Authentik:BaseUrl", "https://iverson-authentik:8443");
            if (federated)
            {
                b.UseSetting("Authentik:OrchestratorAssertionPath", "/var/run/secrets/iverson/authentik-orchestrator/token");
                b.UseSetting("Authentik:OrchestratorClientId", "iverson-admin-orchestrator");
            }
        });

        var handlers = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(IdpAdminClient.HttpClientName);
             h is not null; h = (h as DelegatingHandler)?.InnerHandler)
            handlers.Add(h);

        handlers.OfType<AuthentikOrchestratorTokenHandler>().Should().HaveCount(federated ? 1 : 0);
        handlers[^1].Should().BeOfType<SocketsHttpHandler>("the exchange and the admin calls share the CA-trusting primary handler");
    }
```

- [ ] **Step 2: Run them and see them fail.**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Tenancy.AuthentikTrustTests" 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

Expected failures:
- `Guard_RefusesBothCredentialModes`;
- `Guard_RefusesNeitherCredentialMode`;
- `Guard_RefusesFederatedModeWithoutAClientId`;
- `IdpAdminClient_UsesTheOrchestratorTokenHandler_OnlyInFederatedMode(federated: True)`.

Every other test passes.

- [ ] **Step 3: Write the guard.** Make two edits in `AuthentikTrust.cs`.

  **(a)** In `ValidateStartup`'s doc comment, replace

```csharp
    /// Outside Development the api role refuses to start unless every Authentik hop is https and the
    /// CA is readable.
```

  with

```csharp
    /// Outside Development the api role refuses to start unless every Authentik hop is https, the
    /// CA is readable, and exactly one admin credential mode is configured: the static
    /// <c>Authentik:AdminToken</c> (compose) or the federated <c>Authentik:OrchestratorAssertionPath</c>
    /// with <c>Authentik:OrchestratorClientId</c> (Helm, CSR round-10 #4).
```

  **(b)** At the end of `ValidateStartup`, replace

```csharp
            throw new InvalidOperationException($"Authentik:CaCertificatePath '{caPath}' is not a readable PEM certificate.", ex);
        }
    }
```

  with

```csharp
            throw new InvalidOperationException($"Authentik:CaCertificatePath '{caPath}' is not a readable PEM certificate.", ex);
        }

        var hasStaticToken = !string.IsNullOrWhiteSpace(cfg["Authentik:AdminToken"]);
        var hasAssertion = !string.IsNullOrWhiteSpace(cfg["Authentik:OrchestratorAssertionPath"]);
        if (hasStaticToken == hasAssertion)
            throw new InvalidOperationException(
                "Exactly one of Authentik:AdminToken and Authentik:OrchestratorAssertionPath must be set outside Development.");
        if (hasAssertion && string.IsNullOrWhiteSpace(cfg["Authentik:OrchestratorClientId"]))
            throw new InvalidOperationException("Authentik:OrchestratorClientId is required with Authentik:OrchestratorAssertionPath.");
    }
```

- [ ] **Step 4: Wire the handler.** In `Program.cs`, make two edits.

  **(a)** Replace

```csharp
builder.Services.AddHttpClient(Iverson.Api.Tenancy.IdpAdminClient.HttpClientName, client =>
{
```

  with

```csharp
var authentikAdminClient = builder.Services.AddHttpClient(Iverson.Api.Tenancy.IdpAdminClient.HttpClientName, client =>
{
```

  **(b)** Replace

```csharp
}).ConfigurePrimaryHttpMessageHandler(() => Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]));
builder.Services.AddSingleton<Iverson.Api.Tenancy.IIdpAdminClient, Iverson.Api.Tenancy.IdpAdminClient>();
```

  with

```csharp
}).ConfigurePrimaryHttpMessageHandler(() => Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]));
// CSR round-10 #4 (C2): on Helm there is no static Authentik credential. The API exchanges its own
// projected service-account token for a short-lived access token instead; the startup guard makes the
// two modes mutually exclusive.
if (cfg["Authentik:OrchestratorAssertionPath"] is { Length: > 0 } orchestratorAssertionPath)
    authentikAdminClient.AddHttpMessageHandler(() => new Iverson.Api.Tenancy.AuthentikOrchestratorTokenHandler(
        orchestratorAssertionPath, cfg["Authentik:OrchestratorClientId"]!, TimeProvider.System));
builder.Services.AddSingleton<Iverson.Api.Tenancy.IIdpAdminClient, Iverson.Api.Tenancy.IdpAdminClient>();
```

  The existing static-header line, which sets `Authorization` from `Authentik:AdminToken` when it is non-empty, stays unchanged.

- [ ] **Step 5: Run the tests.** Use the same command as Step 2, then run the handler tests again.

```bash
systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikOrchestratorTokenHandlerTests|FullyQualifiedName~Tenancy.AuthentikTrustTests" 2>&1 | command grep -E '^(Passed|Failed)!'
```

Expected: `Passed!  - Failed:     0, Passed:    42`. The full suites run in Task 6. A `Category!=Integration` filter is not used: one of the ten container-backed classes carries no `Integration` trait.

- [ ] **Step 6: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Tenancy/AuthentikTrust.cs Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs
git commit -m "wire the federated orchestrator credential and require exactly one mode

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: Integration test against a live Authentik

**Files:**
- Modify (test): `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs`

This test goes in the existing class, not a new one. The class's remarks explain why: each class gets its own `IClassFixture`, and so its own 5-container Authentik stack.

**Interfaces:**
- Consumes: T1's handler, `AuthentikContainerFixture.BaseUrl`/`AdminToken`, the class's `RawClient(token)`, and `IdpAdminClient.CreateUserAsync`.

- [ ] **Step 1: Add the using.** After `using System.Net.Http.Json;`, add `using System.Security.Cryptography;`.

- [ ] **Step 2: Add the fact and its helpers.** Insert this block immediately before `    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");`:

```csharp
    // ── CSR round-10 #4 (C2): workload-identity federation ─────────────────────────────────────
    //
    // The Helm profiles authenticate the API with its Kubernetes service-account token instead of a
    // static Authentik token. This builds the same objects orchestrator-federation.yaml declares
    // (static JWKS in place of the relay URL; names suffixed so the shared fixture stays clean),
    // then drives the real AuthentikOrchestratorTokenHandler against this live Authentik.

    private const string FederatedSubject = "system:serviceaccount:iverson-test:iverson-api";
    private const string FederatedAudience = "iverson-authentik-orchestrator";
    private const string WorkloadUsername = "iverson-admin-orchestrator-workload";

    [Fact]
    public async Task OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists()
    {
        using var rsa = RSA.Create(2048);
        var kid = "c2-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = RawClient(_authentik.AdminToken);
        var clientId = await CreateFederationObjectsAsync(admin, rsa, kid);
        var assertion = Assertion(rsa, kid, FederatedSubject, FederatedAudience);

        // Before the workload user exists the policy refuses, and the exchange creates nobody.
        var usersBefore = await UserCountAsync(admin);
        (await ExchangeAsync(clientId, assertion)).Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await UserCountAsync(admin)).Should().Be(usersBefore);

        // Pre-create the user in the orchestrators group, as service-clients.yaml does.
        var groupPk = await FirstAsync(admin, "/api/v3/core/groups/?name=iverson-admin-orchestrators", "pk");
        await PostAsync(admin, "/api/v3/core/users/",
            new { username = WorkloadUsername, name = "Iverson Admin Orchestrator (workload identity)", is_active = true, groups = new[] { groupPk } });

        // Through the real handler: exactly the orchestrator role's rights.
        var assertionFile = Path.Combine(Path.GetTempPath(), $"{kid}-assertion");
        await File.WriteAllTextAsync(assertionFile, assertion);
        try
        {
            var services = new ServiceCollection();
            services.AddHttpClient(IdpAdminClient.HttpClientName, client => client.BaseAddress = new Uri(_authentik.BaseUrl))
                .AddHttpMessageHandler(() => new AuthentikOrchestratorTokenHandler(assertionFile, clientId, TimeProvider.System));
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
            var federated = new IdpAdminClient(factory, NullLogger<IdpAdminClient>.Instance, new ConfigurationBuilder().Build());

            var created = await federated.CreateUserAsync($"{kid}-tenant-admin", $"{kid}@example.invalid", $"tenant-{kid}", ["tenant-admins"]);
            created.RecoveryLink.Should().NotBeNullOrEmpty("POST users and its recovery link both succeed with the federated token");

            using var raw = factory.CreateClient(IdpAdminClient.HttpClientName);
            (await raw.GetAsync("/api/v3/rbac/roles/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            File.Delete(assertionFile);
        }

        // Wrong subject, wrong audience, or arrival through the public listener: refused, nobody created.
        var usersAfter = await UserCountAsync(admin);
        (await ExchangeAsync(clientId, Assertion(rsa, kid, "system:serviceaccount:iverson-test:other", FederatedAudience)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, "somebody-else")))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, assertion, publicIngressMarker: true))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await UserCountAsync(admin)).Should().Be(usersAfter);
    }

    /// <summary>Creates the federation objects and returns the provider's client id.</summary>
    private static async Task<string> CreateFederationObjectsAsync(HttpClient admin, RSA rsa, string kid)
    {
        var key = rsa.ExportParameters(false);
        var mapping = await PostAsync(admin, "/api/v3/propertymappings/source/oauth/",
            new { name = $"{kid} username", expression = $"return {{\"username\": \"{WorkloadUsername}\"}}" });
        var source = await PostAsync(admin, "/api/v3/sources/oauth/", new
        {
            name = kid, slug = kid, provider_type = "openidconnect", enabled = true,
            consumer_key = kid, consumer_secret = "unused",
            authorization_url = "", access_token_url = "", profile_url = "",
            oidc_jwks = new { keys = new[] { new { kty = "RSA", alg = "RS256", use = "sig", kid, n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!) } } },
            user_property_mappings = new[] { mapping.GetProperty("pk") },
        });
        var provider = await PostAsync(admin, "/api/v3/providers/oauth2/", new
        {
            name = kid,
            authorization_flow = await FirstAsync(admin, "/api/v3/flows/instances/?slug=default-provider-authorization-implicit-consent", "pk"),
            invalidation_flow = await FirstAsync(admin, "/api/v3/flows/instances/?slug=default-provider-invalidation-flow", "pk"),
            client_type = "confidential", client_id = kid, redirect_uris = Array.Empty<object>(),
            grant_types = new[] { "client_credentials" },
            signing_key = await FirstAsync(admin, "/api/v3/crypto/certificatekeypairs/?name=authentik%20Self-signed%20Certificate", "pk"),
            issuer_mode = "global", access_token_validity = "minutes=5",
            property_mappings = new[] { await FirstAsync(admin, "/api/v3/propertymappings/provider/scope/?scope_name=goauthentik.io/api", "pk") },
            jwt_federation_sources = new[] { source.GetProperty("pk") },
        });
        var app = await PostAsync(admin, "/api/v3/core/applications/",
            new { name = kid, slug = kid, provider = provider.GetProperty("pk"), policy_engine_mode = "all" });
        var pin = await PostAsync(admin, "/api/v3/policies/expression/", new
        {
            name = $"{kid} workload",
            expression = $$"""
                jwt = request.context.get("oauth_jwt") or {}
                aud = jwt.get("aud")
                auds = aud if isinstance(aud, list) else [aud]
                user = ak_user_by(username="{{WorkloadUsername}}")
                return (
                    jwt.get("sub") == "{{FederatedSubject}}"
                    and "{{FederatedAudience}}" in auds
                    and user is not None
                    and ak_is_group_member(user, name="iverson-admin-orchestrators")
                )
                """,
        });
        var marker = await PostAsync(admin, "/api/v3/policies/expression/", new
        {
            name = $"{kid} in-cluster-only",
            expression = "return \"HTTP_X_IVERSON_PUBLIC_INGRESS\" not in request.http_request.META",
        });
        await PostAsync(admin, "/api/v3/policies/bindings/", new { policy = pin.GetProperty("pk"), target = app.GetProperty("pk"), order = 0, enabled = true });
        await PostAsync(admin, "/api/v3/policies/bindings/", new { policy = marker.GetProperty("pk"), target = app.GetProperty("pk"), order = 1, enabled = true });
        return kid;
    }

    private async Task<(HttpStatusCode Status, string? Error)> ExchangeAsync(string clientId, string assertion, bool publicIngressMarker = false)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_authentik.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/application/o/token/")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
                ["scope"] = "goauthentik.io/api",
            }),
        };
        if (publicIngressMarker)
            request.Headers.Add("X-Iverson-Public-Ingress", "1");
        using var response = await client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    private static async Task<int> UserCountAsync(HttpClient admin)
    {
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/v3/core/users/?page_size=1"));
        return doc.RootElement.GetProperty("pagination").GetProperty("count").GetInt32();
    }

    private static async Task<JsonElement> PostAsync(HttpClient admin, string path, object body)
    {
        using var response = await admin.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"POST {path} returned {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> FirstAsync(HttpClient admin, string pathAndQuery, string property)
    {
        using var doc = JsonDocument.Parse(await admin.GetStringAsync(pathAndQuery));
        var results = doc.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, $"GET {pathAndQuery} must find the object");
        return results[0].GetProperty(property).Clone();
    }

    /// <summary>An RS256 JWT shaped like a kubelet-projected service-account token.</summary>
    private static string Assertion(RSA rsa, string kid, string subject, string audience)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "https://kubernetes.default.svc.cluster.local", sub = subject, aud = new[] { audience }, iat = now, exp = now + 600,
        }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
```

- [ ] **Step 3: Run it.** It's heavy: 5 containers. Check `free -g` first, and run nothing else.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server
systemd-run --user --scope -q -p MemoryMax=7G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~AuthentikRecoveryFlowIntegrationTests" 2>&1 | command grep -E '^\s+Failed |^(Passed|Failed)!'
```

Expected: `Passed!`, 0 failed. That covers the existing facts plus `OrchestratorFederation_MintsOnlyForThePinnedWorkload_OnceTheUserExists`.

If the new fact fails at a `POST … returned` assertion, the message carries Authentik's validation error. Fix the payload field it names; the routes and fields themselves were verified (plan rows 11–14).

- [ ] **Step 4: Prove the fact can fail.**
  1. Change the pin policy's `jwt.get("sub") == "{{FederatedSubject}}"` to compare against `"x"`.
  2. Re-run. Expected: `CreateUserAsync` fails, because the exchange is refused.
  3. Revert, and re-run to green.

- [ ] **Step 5: Commit.**

```bash
git add Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs
git commit -m "cover the orchestrator federation against a live authentik

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Helm wiring

**Files (modify):**
- `charts/authentik/values.yaml`
- `charts/authentik/templates/deployment-worker.yaml`
- `charts/authentik/templates/blueprints-configmap.yaml`
- `charts/authentik/templates/secret-service-clients.yaml`
- `templates/networkpolicies.yaml`
- `charts/api/templates/deployment.yaml`

All paths are under `Iverson.Server/deploy/helm/iverson/`.

**Interfaces:**
- Consumes: T2's configuration keys.
- Produces: the blueprint paths `custom/orchestrator-federation.yaml` and `custom/service-clients/service-clients.yaml`, and the Secret names T5 and T6 reference.

The edits are exact old→new replacements, applied by a script that asserts each old text occurs exactly once. The plan proved the script and the checker on a scratch copy of the chart (plan rows 20–24).

- [ ] **Step 1: Write `$SCR/t4_edits.py`** with exactly this content:

```python
"""Apply the C2 plan's Task 4 Helm edits to a chart root (argv[1] = path to .../helm/iverson)."""
import sys, pathlib

root = pathlib.Path(sys.argv[1])

def edit(rel, old, new, count=1):
    p = root / rel
    s = p.read_text()
    assert s.count(old) == count, (rel, old[:70], s.count(old))
    p.write_text(s.replace(old, new))

# --- values.yaml: relay image + resources -------------------------------------------------------
edit("charts/authentik/values.yaml",
     'revokeCrossDb:\n  image: "postgres:16.15"\n',
     'revokeCrossDb:\n  image: "postgres:16.15"\n'
     '# CSR round-10 #4 (C2): the worker\'s JWKS relay. A nested key named `image`, so Dependabot\'s helm\n'
     '# updater tracks it.\n'
     'jwksRelay:\n  image: "registry.k8s.io/kubectl:v1.37.1"\n')
edit("charts/authentik/values.yaml",
     '  tlsProxy:\n    requests: { cpu: "10m", memory: "16Mi" }\n    limits: { cpu: "100m", memory: "64Mi" }\n',
     '  tlsProxy:\n    requests: { cpu: "10m", memory: "16Mi" }\n    limits: { cpu: "100m", memory: "64Mi" }\n'
     '  jwksRelay:\n    requests: { cpu: "10m", memory: "32Mi" }\n    limits: { memory: "64Mi" }\n')

# --- deployment-worker.yaml: native sidecar + volumes --------------------------------------------
edit("charts/authentik/templates/deployment-worker.yaml",
     '        seccompProfile: { type: RuntimeDefault }\n      containers:\n',
     '''        seccompProfile: { type: RuntimeDefault }
      # CSR round-10 #4 (C2): serves the cluster's service-account signing keys (JWKS) on loopback, for
      # the iverson-k8s-workload source in orchestrator-federation.yaml. A native sidecar, so it is
      # listening before the worker applies blueprints. It authenticates with its own projected token
      # (the default issuer-discovery binding lets any service account read the JWKS) and answers only
      # the JWKS path. The startup probe reads /ready/, which kubectl proxy serves from the empty
      # jwks-relay-www volume, so a refusing or unreachable API server delays only federation, never
      # the worker's start. That volume must stay empty: --www is not filtered by --accept-paths.
      initContainers:
        - name: jwks-relay
          image: {{ .Values.jwksRelay.image | quote }}
          restartPolicy: Always
          args:
            - proxy
            - --address=127.0.0.1
            - --port=8001
            - --accept-paths=^/openid/v1/jwks$
            - --reject-methods=^(POST|PUT|PATCH|DELETE)$
            - --www=/www
            - --www-prefix=/ready/
          startupProbe:
            exec: { command: ["kubectl", "--server=http://127.0.0.1:8001", "get", "--raw", "/ready/"] }
            periodSeconds: 1
            failureThreshold: 30
          securityContext:
            runAsNonRoot: true
            runAsUser: 65532
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          resources:
            requests:
              cpu: {{ .Values.resources.jwksRelay.requests.cpu | quote }}
              memory: {{ .Values.resources.jwksRelay.requests.memory | quote }}
            limits:
              memory: {{ .Values.resources.jwksRelay.limits.memory | quote }}
          volumeMounts:
            - name: jwks-relay-credential
              mountPath: /var/run/secrets/kubernetes.io/serviceaccount
              readOnly: true
            - name: jwks-relay-www
              mountPath: /www
              readOnly: true
      containers:
''')
edit("charts/authentik/templates/deployment-worker.yaml",
     '''        - name: blueprints-service-clients
          secret:
            secretName: {{ .Release.Name }}-authentik-blueprints-service-clients
''',
     '''        - name: blueprints-service-clients
          secret:
            secretName: {{ .Release.Name }}-authentik-blueprints-service-clients
        - name: jwks-relay-credential
          projected:
            sources:
              - serviceAccountToken: { path: token, expirationSeconds: 3600 }
              - configMap: { name: kube-root-ca.crt, items: [{ key: ca.crt, path: ca.crt }] }
              - downwardAPI: { items: [{ path: namespace, fieldRef: { fieldPath: metadata.namespace } }] }
        - name: jwks-relay-www
          emptyDir: {}
''')

# --- blueprints-configmap.yaml: the federation blueprint ------------------------------------------
edit("charts/authentik/templates/blueprints-configmap.yaml",
     "{{ $content | indent 4 }}\n{{- end }}\n",
     '''{{ $content | indent 4 }}
{{- end }}
  {{- /* CSR round-10 #4 (C2): rendered here, not kept under blueprints/, because compose and the
     Api.Tests fixture mount that directory (where nothing serves the JWKS) and the pinned subject
     needs the release namespace and name. A file of its own because one failing entry stops a
     whole blueprint: if the relay cannot fetch the JWKS, only federation waits for the hourly
     re-apply. */}}
  orchestrator-federation.yaml: |
    version: 1
    metadata:
      name: Iverson - orchestrator workload identity
    entries:
      # Every accepted assertion maps to the one pre-created user (service-clients.yaml). Runs only
      # after both policies below have passed.
      - id: workload-username
        model: authentik_sources_oauth.oauthsourcepropertymapping
        identifiers:
          name: "Iverson: k8s workload username"
        attrs:
          expression: 'return {"username": "iverson-admin-orchestrator-workload"}'
      # Trust anchor only: the cluster's service-account keys, through the worker's JWKS relay. No
      # login or enrolment flow.
      - id: k8s-workload-source
        model: authentik_sources_oauth.oauthsource
        identifiers:
          slug: iverson-k8s-workload
        attrs:
          name: iverson-k8s-workload
          provider_type: openidconnect
          enabled: true
          consumer_key: iverson-k8s-workload
          consumer_secret: unused
          authorization_url: ""
          access_token_url: ""
          profile_url: ""
          oidc_jwks_url: http://127.0.0.1:8001/openid/v1/jwks
          user_property_mappings:
            - !KeyOf workload-username
      - model: authentik_providers_oauth2.oauth2provider
        identifiers:
          name: iverson-admin-orchestrator
        attrs:
          authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
          invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
          client_type: confidential
          client_id: iverson-admin-orchestrator
          redirect_uris: []
          grant_types: ["client_credentials"]
          signing_key: !Find [authentik_crypto.certificatekeypair, [name, "authentik Self-signed Certificate"]]
          issuer_mode: global
          access_token_validity: "minutes=5"
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, goauthentik.io/api]]
          jwt_federation_sources:
            - !KeyOf k8s-workload-source
      # all, not the default any: both bindings must pass.
      - id: orchestrator-app
        model: authentik_core.application
        identifiers:
          slug: iverson-admin-orchestrator
        attrs:
          name: iverson-admin-orchestrator
          provider: !Find [authentik_providers_oauth2.oauth2provider, [name, iverson-admin-orchestrator]]
          policy_engine_mode: all
      # Authentik checks only a federated JWT's signature and expiry, so the subject and audience are
      # pinned here. The user check refuses every exchange until service-clients.yaml has pre-created
      # the user, so no exchange can create it (one created that way has an expiry and no group).
      - id: orchestrator-workload
        model: authentik_policies_expression.expressionpolicy
        identifiers:
          name: iverson-admin-orchestrator-workload
        attrs:
          expression: |
            jwt = request.context.get("oauth_jwt") or {}
            aud = jwt.get("aud")
            auds = aud if isinstance(aud, list) else [aud]
            user = ak_user_by(username="iverson-admin-orchestrator-workload")
            return (
                jwt.get("sub") == "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-api"
                and "iverson-authentik-orchestrator" in auds
                and user is not None
                and ak_is_group_member(user, name="iverson-admin-orchestrators")
            )
      # Refuses exchanges that came through the public listener. The same rule as the
      # admin-automation policy, kept as its own object so this blueprint does not depend on
      # service-clients.yaml's apply order.
      - id: orchestrator-in-cluster-only
        model: authentik_policies_expression.expressionpolicy
        identifiers:
          name: iverson-admin-orchestrator-in-cluster-only
        attrs:
          expression: |
            return "HTTP_X_IVERSON_PUBLIC_INGRESS" not in request.http_request.META
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf orchestrator-workload
          target: !KeyOf orchestrator-app
          order: 0
      - model: authentik_policies.policybinding
        identifiers:
          policy: !KeyOf orchestrator-in-cluster-only
          target: !KeyOf orchestrator-app
          order: 1
''')

# --- secret-service-clients.yaml ------------------------------------------------------------------
ssc = "charts/authentik/templates/secret-service-clients.yaml"
edit(ssc, '''{{- $adminOrchestratorUserExisting := lookup "v1" "Secret" $ns (printf "%s-authentik-admin-orchestrator-user" $rel) }}
{{- $adminOrchestratorPassword := "" }}
{{- if $adminOrchestratorUserExisting }}
{{- $adminOrchestratorPassword = index $adminOrchestratorUserExisting.data "password" | b64dec }}
{{- else }}
{{- $adminOrchestratorPassword = randAlphaNum 32 }}
{{- end }}

{{- $adminOrchestratorTokenExisting := lookup "v1" "Secret" $ns (printf "%s-authentik-admin-orchestrator-token" $rel) }}
{{- $adminOrchestratorTokenKey := "" }}
{{- if $adminOrchestratorTokenExisting }}
{{- $adminOrchestratorTokenKey = index $adminOrchestratorTokenExisting.data "token-key" | b64dec }}
{{- else }}
{{- $adminOrchestratorTokenKey = randAlphaNum 50 }}
{{- end }}

''', '')
edit(ssc, '''---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-admin-orchestrator-user
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
  password: {{ $adminOrchestratorPassword | b64enc }}
---
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-admin-orchestrator-token
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
  token-key: {{ $adminOrchestratorTokenKey | b64enc }}
''', '')
edit(ssc, "# This blueprint carries credential material -- the iverson-admin-orchestrator API token, OAuth2\n# client secrets, and user passwords -- so",
          "# This blueprint carries credential material -- OAuth2 client secrets and user passwords -- so")
i_old_start = '''      - model: authentik_core.user
        identifiers:
          username: iverson-admin-orchestrator
        attrs:'''
p = root / ssc
s = p.read_text()
a = s.index(i_old_start)
b = s.index("          expiring: false\n", a) + len("          expiring: false\n")
new_tail = '''      # CSR round-10 #4 (C2): the API authenticates with its Kubernetes service-account token, which
      # orchestrator-federation.yaml maps to this user. Pre-created here, in the blueprint that never
      # depends on the JWKS relay: a user the token exchange created would get an expiry and no group.
      # No password, so Authentik stores an unusable one. NOT is_superuser: the
      # iverson-admin-orchestrators group binds the scoped role above.
      - model: authentik_core.user
        identifiers:
          username: iverson-admin-orchestrator-workload
        attrs:
          username: iverson-admin-orchestrator-workload
          name: "Iverson Admin Orchestrator (workload identity)"
          email: iverson-admin-orchestrator@example.invalid
          is_active: true
          groups:
            - !Find [authentik_core.group, [name, iverson-admin-orchestrators]]
      # The former static, never-expiring orchestrator token and its password-bearing user. Their
      # Secrets carry resource-policy: keep, so the cutover runbook deletes them after the upgrade.
      - model: authentik_core.token
        state: absent
        identifiers:
          identifier: iverson-admin-orchestrator-token
      - model: authentik_core.user
        state: absent
        identifiers:
          username: iverson-admin-orchestrator
'''
assert s.count(i_old_start) == 1
p.write_text(s[:a] + new_tail + s[b:])

# --- networkpolicies.yaml: worker-only API-server egress ------------------------------------------
edit("templates/networkpolicies.yaml",
     '''    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-redis } } }]
      ports: [{ protocol: TCP, port: 6379 }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-redis-ingress
''',
     '''    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-redis } } }]
      ports: [{ protocol: TCP, port: 6379 }]
---
# CSR round-10 #4 (C2): the worker's JWKS relay reads the cluster's service-account keys from the API
# server. The server pods get no API-server egress.
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-authentik-worker-apiserver-egress
spec:
  podSelector:
    matchLabels: { app: {{ .Release.Name }}-authentik-worker }
  policyTypes: ["Egress"]
  egress:
    {{- include "iverson.apiServerEgress" . | nindent 4 }}
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-redis-ingress
''')
edit("templates/networkpolicies.yaml",
     "it scopes the Postgres and Kafka API-server egress rules",
     "it scopes the Postgres, Kafka and Authentik-worker API-server egress rules")

# --- api deployment.yaml ------------------------------------------------------------------------
api = "charts/api/templates/deployment.yaml"
edit(api, '''            - name: Authentik__AdminToken
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-admin-orchestrator-token, key: token-key }
''', '''            # CSR round-10 #4 (C2): no static Authentik credential. The API exchanges the projected
            # service-account token below for a short-lived access token (AuthentikOrchestratorTokenHandler).
            - name: Authentik__OrchestratorAssertionPath
              value: "/var/run/secrets/iverson/authentik-orchestrator/token"
            - name: Authentik__OrchestratorClientId
              value: "iverson-admin-orchestrator"
''')
edit(api, '''            - name: authentik-ca
              mountPath: /etc/iverson/authentik-ca
              readOnly: true
''', '''            - name: authentik-ca
              mountPath: /etc/iverson/authentik-ca
              readOnly: true
            - name: authentik-orchestrator-token
              mountPath: /var/run/secrets/iverson/authentik-orchestrator
              readOnly: true
''')
edit(api, '''        - name: tmp
          emptyDir: {}''', '''        # The pod's only Kubernetes credential: audience-scoped, so the API server refuses it.
        - name: authentik-orchestrator-token
          projected:
            sources:
              - serviceAccountToken: { path: token, audience: iverson-authentik-orchestrator, expirationSeconds: 600 }
        - name: tmp
          emptyDir: {}''')
print("t4 edits applied")
```

- [ ] **Step 2: Write `$SCR/check_t4.py`** with exactly this content:

```python
"""C2 Task 4 render assertions. Usage: check_t4.py <overlay-name> < rendered.yaml"""
import sys, yaml

name = sys.argv[1]
docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
fails = []
def need(cond, msg):
    if not cond:
        fails.append(msg)

class Tagged(yaml.SafeLoader):
    pass
Tagged.add_multi_constructor("!", lambda loader, suffix, node: f"!{suffix}")

API_CIDRS = {"values-aws": ["10.0.0.0/20", "10.0.16.0/20", "10.0.128.0/20", "10.0.144.0/20", "10.100.0.1/32", "172.20.0.1/32"],
             "values-azure": ["10.1.17.0/28"], "values-gcp": ["172.16.0.0/28"],
             "values-laptop": ["172.18.0.0/16", "10.89.0.0/16"], "values-local": ["172.18.0.0/16", "10.89.0.0/16"]}[name]

# 1. No orchestrator Secrets, and nothing reads them.
need(("Secret", "iverson-authentik-admin-orchestrator-token") not in by, "token Secret still rendered")
need(("Secret", "iverson-authentik-admin-orchestrator-user") not in by, "user Secret still rendered")
raw = yaml.safe_dump_all(docs)
need("iverson-authentik-admin-orchestrator-token" not in raw and "iverson-authentik-admin-orchestrator-user" not in raw, "a manifest still names an orchestrator Secret")

# 2. API Deployment: federated env, projected token, no AdminToken.
api = by[("Deployment", "iverson-api")]["spec"]["template"]["spec"]
c = next(x for x in api["containers"] if x["name"] == "api")
env = {e["name"]: e.get("value") for e in c["env"]}
need("Authentik__AdminToken" not in env, "API still has Authentik__AdminToken")
need(env.get("Authentik__OrchestratorAssertionPath") == "/var/run/secrets/iverson/authentik-orchestrator/token", "assertion path env")
need(env.get("Authentik__OrchestratorClientId") == "iverson-admin-orchestrator", "client id env")
vol = next((v for v in api["volumes"] if v["name"] == "authentik-orchestrator-token"), None)
need(vol is not None and vol["projected"]["sources"] == [{"serviceAccountToken": {"path": "token", "audience": "iverson-authentik-orchestrator", "expirationSeconds": 600}}], "API projected token volume")
need(any(m["name"] == "authentik-orchestrator-token" and m["mountPath"] == "/var/run/secrets/iverson/authentik-orchestrator" and m.get("readOnly") for m in c["volumeMounts"]), "API token mount")
need(api.get("automountServiceAccountToken") is False, "API automount must stay false")

# 3. Worker: native-sidecar relay.
w = by[("Deployment", "iverson-authentik-worker")]["spec"]["template"]["spec"]
relay = next((x for x in w.get("initContainers", []) if x["name"] == "jwks-relay"), None)
need(relay is not None, "no jwks-relay initContainer")
if relay:
    need(relay["image"] == "registry.k8s.io/kubectl:v1.37.1", "relay image")
    need(relay.get("restartPolicy") == "Always", "relay must be a native sidecar")
    need(relay["args"] == ["proxy", "--address=127.0.0.1", "--port=8001", "--accept-paths=^/openid/v1/jwks$",
                           "--reject-methods=^(POST|PUT|PATCH|DELETE)$", "--www=/www", "--www-prefix=/ready/"], "relay args")
    need(relay["startupProbe"]["exec"]["command"] == ["kubectl", "--server=http://127.0.0.1:8001", "get", "--raw", "/ready/"], "startup probe")
    sc = relay["securityContext"]
    need(sc.get("runAsNonRoot") and sc.get("runAsUser") == 65532 and sc.get("readOnlyRootFilesystem") and sc.get("allowPrivilegeEscalation") is False and sc["capabilities"]["drop"] == ["ALL"], "relay securityContext")
    mounts = {m["name"]: m for m in relay["volumeMounts"]}
    need(mounts.get("jwks-relay-credential", {}).get("mountPath") == "/var/run/secrets/kubernetes.io/serviceaccount", "relay credential mount")
    need(mounts.get("jwks-relay-www", {}).get("mountPath") == "/www" and mounts["jwks-relay-www"].get("readOnly"), "relay www mount")
vols = {v["name"]: v for v in w["volumes"]}
need(vols.get("jwks-relay-www") == {"name": "jwks-relay-www", "emptyDir": {}}, "www must be an empty emptyDir")
src = vols.get("jwks-relay-credential", {}).get("projected", {}).get("sources", [])
need(any("serviceAccountToken" in s and "audience" not in s["serviceAccountToken"] for s in src), "relay token has the default API audience")
need(w.get("automountServiceAccountToken") is False, "worker automount must stay false")

# 4. Federation blueprint (ConfigMap) with the rendered subject.
fed = yaml.load(by[("ConfigMap", "iverson-authentik-blueprints")]["data"]["orchestrator-federation.yaml"], Loader=Tagged)
ent = fed["entries"]
models = [e["model"] for e in ent]
need(models == ["authentik_sources_oauth.oauthsourcepropertymapping", "authentik_sources_oauth.oauthsource",
                "authentik_providers_oauth2.oauth2provider", "authentik_core.application",
                "authentik_policies_expression.expressionpolicy", "authentik_policies_expression.expressionpolicy",
                "authentik_policies.policybinding", "authentik_policies.policybinding"], f"federation models {models}")
pin = next(e for e in ent if e.get("id") == "orchestrator-workload")["attrs"]["expression"]
need('"system:serviceaccount:iverson:iverson-api"' in pin, "pinned sub not rendered")
need('ak_is_group_member(user, name="iverson-admin-orchestrators")' in pin, "user gate missing")
src_e = next(e for e in ent if e.get("id") == "k8s-workload-source")["attrs"]
need(src_e["oidc_jwks_url"] == "http://127.0.0.1:8001/openid/v1/jwks", "source JWKS URL")
app = next(e for e in ent if e.get("id") == "orchestrator-app")["attrs"]
need(app["policy_engine_mode"] == "all", "policy_engine_mode")

# 5. service-clients.yaml: workload user, absent entries, no static credential.
sc_bp = yaml.load(by[("Secret", "iverson-authentik-blueprints-service-clients")]["stringData"]["service-clients.yaml"], Loader=Tagged)
users = [e for e in sc_bp["entries"] if e["model"] == "authentik_core.user"]
wl = [e for e in users if e["identifiers"].get("username") == "iverson-admin-orchestrator-workload"]
need(len(wl) == 1 and "password" not in wl[0]["attrs"], "workload user missing or carries a password")
need(any(e["identifiers"].get("username") == "iverson-admin-orchestrator" and e.get("state") == "absent" for e in users), "old user not absent")
tok = [e for e in sc_bp["entries"] if e["model"] == "authentik_core.token" and e["identifiers"].get("identifier") == "iverson-admin-orchestrator-token"]
need(len(tok) == 1 and tok[0].get("state") == "absent" and "attrs" not in tok[0], "old token not absent")
idx = {id(e): i for i, e in enumerate(sc_bp["entries"])}
need(tok and idx[id(tok[0])] < idx[id(next(e for e in users if e["identifiers"].get("username") == "iverson-admin-orchestrator"))], "token must be removed before its user")

# 6. Worker-only API-server egress.
np = by.get(("NetworkPolicy", "iverson-authentik-worker-apiserver-egress"))
need(np is not None, "worker API-server egress policy missing")
if np:
    need(np["spec"]["podSelector"] == {"matchLabels": {"app": "iverson-authentik-worker"}}, "policy selects only the worker")
    rules = np["spec"]["egress"]
    need(len(rules) == 1 and [p["ipBlock"]["cidr"] for p in rules[0]["to"]] == API_CIDRS
         and sorted(p["port"] for p in rules[0]["ports"]) == [443, 6443], f"API CIDRs {rules}")

print(f"[{name}] {'PASS' if not fails else 'FAIL'}", *fails, sep="\n  ")
sys.exit(bool(fails))
```

- [ ] **Step 3: Baseline.** Render `main`'s chart and record kube-score criticals, then confirm the checker fails on it.

```bash
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c2-sdd
WT=/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/base-$v.yaml
  kube-score score --output-format ci $SCR/base-$v.yaml 2>/dev/null | command grep '^\[CRITICAL\]' | sort > $SCR/base-$v.ks
  python3 $SCR/check_t4.py $v < $SCR/base-$v.yaml > /dev/null && echo "$v: checker PASSED on base (wrong)" || echo "$v: checker fails on base (expected)"; done
```

Expected: `checker fails on base (expected)` ×5.

- [ ] **Step 4: Apply the edits.**

```bash
python3 $SCR/t4_edits.py $WT/Iverson.Server/deploy/helm/iverson
```

Expected: `t4 edits applied`.

- [ ] **Step 5: Verify.**

```bash
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/new-$v.yaml
  python3 $SCR/check_t4.py $v < $SCR/new-$v.yaml
  kubeconform -summary -ignore-missing-schemas -skip CustomResourceDefinition $SCR/new-$v.yaml | tail -1
  kube-score score --output-format ci $SCR/new-$v.yaml 2>/dev/null | command grep '^\[CRITICAL\]' | sort > $SCR/new-$v.ks
  echo "  other-container criticals changed: $(diff <(command grep -v '(jwks-relay)' $SCR/base-$v.ks) <(command grep -v '(jwks-relay)' $SCR/new-$v.ks) | command grep -c '^[<>]')  relay: $(command grep -c '(jwks-relay)' $SCR/new-$v.ks)"
  helm lint . $o | tail -1; done
git -C $WT status --short
```

Expected, for each of the five overlays:
- `[<overlay>] PASS`;
- kubeconform `Invalid: 0, Errors: 0`;
- `other-container criticals changed: 0  relay: 5`;
- `1 chart(s) linted, 0 chart(s) failed`.

`git status` shows only the six chart files.

The relay's 5 criticals are known and accepted (plan row 22). Three of them already appear on every container in the chart. The CPU limit and ephemeral-storage request follow from the spec's resources (§2c).

- [ ] **Step 6: Commit.**

```bash
cd $WT && C=Iverson.Server/deploy/helm/iverson
git add $C/charts/authentik/values.yaml $C/charts/authentik/templates/deployment-worker.yaml $C/charts/authentik/templates/blueprints-configmap.yaml \
  $C/charts/authentik/templates/secret-service-clients.yaml $C/templates/networkpolicies.yaml $C/charts/api/templates/deployment.yaml
git commit -m "authenticate the api to authentik with its service-account token on helm

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 5: Runbook

**Files:**
- Modify: `docs/runbooks/csr10-infrastructure-cutover.md` (append §9)

- [ ] **Step 1: Append §9.** Add this at the end of the file, after §8:

```markdown

## 9. Orchestrator workload identity (C2)

The API now authenticates to Authentik's admin API with its own Kubernetes service-account token, exchanged for a 5-minute access token, and the static orchestrator token is removed. A single `helm upgrade` switches it over. Until both blueprints below have applied in the new Authentik worker, `CreateTenant` and `InviteUser` can fail; no data is affected. If the old worker (which has no JWKS relay) took the apply, that can last until the next hourly blueprint discovery, which step 2 shortcuts.

1. Do not provision tenants while the upgrade rolls out.
2. After the rollout, apply both blueprints in the new worker container:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/service-clients/service-clients.yaml custom/orchestrator-federation.yaml
   ```

   It exits non-zero if either blueprint is invalid. A failure that names `oidc_jwks_url` means the relay could not read the cluster's keys; check the `jwks-relay` container's log and the worker's API-server egress (§4).
3. Delete the two orchestrator Secrets. They carry `helm.sh/resource-policy: keep`, so the upgrade leaves them behind:

   ```bash
   kubectl -n <ns> delete secret <release>-authentik-admin-orchestrator-token <release>-authentik-admin-orchestrator-user
   ```

4. Confirm with one `InviteUser` (or `CreateTenant`) that a recovery link comes back.
5. Confirm both Secrets are gone: `kubectl -n <ns> get secret <release>-authentik-admin-orchestrator-token <release>-authentik-admin-orchestrator-user` reports `NotFound` for each.
```

- [ ] **Step 2: Commit.**

```bash
git add -f docs/runbooks/csr10-infrastructure-cutover.md
git commit -m "add the c2 orchestrator workload identity cutover to the runbook

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: Verification

No commits. The live part is heavy: one heavy process at a time, with `free -g` checked first.

- [ ] **Step 1: Full suites.**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p/$p.csproj 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
```

Expected: every line reads `Passed!`, with 0 failed.

- [ ] **Step 2: Prepare.**
  - Check the user's stack is down: `docker ps --format '{{.Names}}'`. If the list is not empty, **stop and ask the user**. Never stop their containers yourself: kind's ingress binds host port 8080.
  - Record their state:

```bash
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/c2-sdd; LIVE=$SCR/live; mkdir -p $LIVE
WT=/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

- [ ] **Step 3: Install `main`'s chart with the branch's API image.** The branch image runs the static mode that `main`'s chart configures. Ollama is off, because `CreateTenant` needs no model; TEI stays on, as in D's live check. Run the slow steps with `run_in_background`.

```bash
cd $WT
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr10c2 iverson
python3 -c "import yaml; d=yaml.safe_load(open('Iverson.Server/deploy/helm/iverson/values-laptop.yaml')); d.setdefault('ollama',{})['enabled']=False; yaml.safe_dump(d, open('$LIVE/laptop-no-ollama.yaml','w'))"
bash Iverson.Server/deploy/kind/build-and-load-image.sh csr10c2 iverson --model-images --values $LIVE/laptop-no-ollama.yaml
mkdir -p $LIVE/mainchart && git archive main Iverson.Server/deploy/helm/iverson | tar -x -C $LIVE/mainchart
cd $LIVE/mainchart/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10c2 --set worker.image.tag=csr10c2 --wait --timeout 30m
kubectl -n iverson get secret iverson-authentik-admin-orchestrator-token iverson-authentik-admin-orchestrator-user -o name
```

Expected: every pod is Running and ready. Both Secrets are listed, which is the pre-upgrade state.

- [ ] **Step 4: Upgrade to the branch chart and run the runbook.**

```bash
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade iverson . -n iverson -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr10c2 --set worker.image.tag=csr10c2 --wait --timeout 30m
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
kubectl -n iverson get pod -l app=iverson-authentik-worker -o jsonpath='{range .items[0].status.initContainerStatuses[*]}{.name} ready={.ready} restarts={.restartCount}{"\n"}{end}'
kubectl -n iverson get secret iverson-authentik-admin-orchestrator-token iverson-authentik-admin-orchestrator-user -o name
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- \
  ak apply_blueprint custom/service-clients/service-clients.yaml custom/orchestrator-federation.yaml > $LIVE/apply.log 2>&1; echo "apply exit=$?"
kubectl -n iverson delete secret iverson-authentik-admin-orchestrator-token iverson-authentik-admin-orchestrator-user
```

Expected:
- no `NOT READY` lines;
- `jwks-relay ready=true restarts=0`;
- both Secrets still listed after the upgrade, showing the runbook's deletion step is needed;
- `apply exit=0`;
- both Secrets deleted.

- [ ] **Step 5: Live checks.**

```bash
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $LIVE/pf-ak.log 2>&1 & echo $! > $LIVE/pf-ak.pid
kubectl -n iverson port-forward svc/iverson-api 18080:8080 > $LIVE/pf-api.log 2>&1 & echo $! > $LIVE/pf-api.pid
sleep 3
echo "cluster kids: $(kubectl get --raw /openid/v1/jwks | python3 -c 'import sys,json; print(sorted(k["kid"] for k in json.load(sys.stdin)["keys"]))')"
echo "relay kids:   $(kubectl -n iverson exec deploy/iverson-authentik-worker -c jwks-relay -- kubectl --server=http://127.0.0.1:8001 get --raw /openid/v1/jwks | python3 -c 'import sys,json; print(sorted(k["kid"] for k in json.load(sys.stdin)["keys"]))')"
echo "relay other path: $(kubectl -n iverson exec deploy/iverson-authentik-worker -c jwks-relay -- kubectl --server=http://127.0.0.1:8001 get --raw /api 2>&1 | head -c 60)"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "
from authentik.sources.oauth.models import OAuthSource
from authentik.core.models import User, Token
print('source kids:', sorted(k['kid'] for k in OAuthSource.objects.get(slug='iverson-k8s-workload').oidc_jwks['keys']))
print('old user:', User.objects.filter(username='iverson-admin-orchestrator').count(), 'old token:', Token.objects.filter(identifier='iverson-admin-orchestrator-token').count())
w = User.objects.get(username='iverson-admin-orchestrator-workload')
print('workload groups:', [g.name for g in w.groups.all()], 'usable password:', w.has_usable_password())" 2>/dev/null | tail -3
exchange() { curl -s -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials \
  -d client_id=iverson-admin-orchestrator -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer \
  --data-urlencode client_assertion="$1" -d scope=goauthentik.io/api | python3 -c 'import sys,json; d=json.load(sys.stdin); print("MINTED" if "access_token" in d else "REFUSED "+d.get("error",""))'; }
kubectl -n iverson create sa c2-other >/dev/null
echo "api sa:   $(exchange "$(kubectl -n iverson create token iverson-api --audience iverson-authentik-orchestrator --duration 10m)")"
echo "other sa: $(exchange "$(kubectl -n iverson create token c2-other --audience iverson-authentik-orchestrator --duration 10m)")"
kubectl -n iverson get deploy iverson-api -o json | python3 -c "import sys,json; c=json.load(sys.stdin)['spec']['template']['spec']['containers'][0]; print('api env AdminToken:', any(e['name']=='Authentik__AdminToken' for e in c['env']))"
kubectl -n iverson get secret iverson-authentik-admin-orchestrator-token iverson-authentik-admin-orchestrator-user 2>&1 | command grep -c NotFound
```

Expected:
- `cluster kids` equals `relay kids`;
- `relay other path` shows a `Forbidden` or 403 error;
- `source kids` equals `cluster kids`;
- `old user: 0 old token: 0`;
- `workload groups: ['iverson-admin-orchestrators'] usable password: False`;
- `api sa:   MINTED`;
- `other sa: REFUSED invalid_grant`;
- `api env AdminToken: False`;
- `2`, for both Secrets `NotFound`.

- [ ] **Step 6: `CreateTenant` through the federated credential.** Build C1's scratch console, pointing it at this worktree:

```bash
mkdir -p $LIVE/createtenant && cd $LIVE/createtenant
cat > createtenant.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr10-orchestrator-workload-identity/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
X
cat > Program.cs <<'X'
// args: grpcUrl tenantId ; env TOKEN (admin-automation access token). Prints only the link's host and path.
using Grpc.Core; using Grpc.Net.Client; using Iverson.Client.Contracts;
using var channel = GrpcChannel.ForAddress(args[0]);
var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
var tenant = await client.CreateTenantAsync(new CreateTenantRequest { TenantId = args[1], DisplayName = "C2 live check",
    AdminUsername = args[1] + "-admin", AdminEmail = args[1] + "-admin@iverson.local" },
    new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } });
var link = new Uri(tenant.AdminRecoveryLink);
Console.WriteLine($"link host={link.Scheme}://{link.Authority} path={link.AbsolutePath}");
X
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release 2>&1 | command grep -E "error|Build succeeded" | head -3
( export CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
  export CSEC="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-secret}' | base64 -d)"
  export TOKEN="$(curl -s -H 'Host: iverson-authentik:9000' -d grant_type=client_credentials -d client_id="$CID" --data-urlencode client_secret="$CSEC" \
    -d scope="admin schema_admin tenant_id_admin" http://127.0.0.1:19000/application/o/token/ | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
  dotnet $LIVE/createtenant/bin/Release/net10.0/createtenant.dll http://127.0.0.1:18080 c2-live-check )
```

Expected: `link host=http://authentik.iverson.local path=/if/flow/iverson-recovery/`.

- [ ] **Step 7: Tear kind down and diff the user's state.**

```bash
kill $(cat $LIVE/pf-ak.pid) $(cat $LIVE/pf-api.pid) 2>/dev/null
kind delete cluster --name iverson; kind get clusters
docker rmi docker.io/library/iverson-api:csr10c2 localhost/iverson-api:csr10c2 >/dev/null 2>&1; true
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt - | head && echo no-image-removed-or-repointed
```

Expected:
- `No kind clusters found`;
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`;
- `no-image-removed-or-repointed`.

Newly pulled base images (the kind node, Calico, `registry.k8s.io/kubectl`, the TEI base) are acceptable; list them. Remove any `*:csr10c2` or `iverson-tei-model:*` image this task built.

## Known issues inherited from spec

- **New signing keys on managed clusters (unverified).** Whether EKS, AKS and GKE publish a new signing key before they start signing with it can't be checked without a cloud account. If a cluster signs with a key Authentik hasn't fetched yet, exchanges fail with `invalid_grant` until the next 3-hourly refresh, and tenant provisioning fails during that window.
- **Relay or its API server unavailable while the blueprint applies, including at worker start.** Federation waits for the hourly re-apply. `service-clients.yaml` and the removal of the static credential are unaffected.
- **Rollout window.** Tenant provisioning can fail during the upgrade until both blueprints have applied in the new worker, which can wait for the next hourly discovery unless the runbook's `ak apply_blueprint` step runs (§3c).
- **Managed API servers' JWKS (unverified).** Whether the EKS, AKS and GKE API servers serve `/openid/v1/jwks` with the keys that sign projected tokens can't be checked without a cloud account; cloud profiles get static verification only.
- **Compose keeps the static, never-expiring token** (user decision).
- **The orchestrator role's global `add_user`** can still create a user directly into a non-superuser group. That is an Authentik permission-model limit that C1 recorded and left as is. It is unchanged here.
- **One new third-party image,** `registry.k8s.io/kubectl`, pinned and tracked by Dependabot.
- **The Authentik admin UI's source form** (server process) can't re-validate this source, because the relay runs only in the worker pod. The source is managed by blueprint, so nothing uses that form.
