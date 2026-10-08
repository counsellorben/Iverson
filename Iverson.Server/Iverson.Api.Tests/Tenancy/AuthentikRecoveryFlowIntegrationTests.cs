using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// Live-Authentik coverage for the identity plane: CSR round-2 finding #4 (the recovery flow
/// exists and mints a working link) plus CSR round-3 findings #1 (the orchestrator role is scoped
/// by OBJECT, not just by verb) and #2 (the recovery flow consults MFA).
///
/// CSR round-2 finding #4 regression coverage: proves <see cref="IdpAdminClient.CreateUserAsync"/>
/// actually obtains a working recovery link from a REAL, blueprint-equipped Authentik instance —
/// not a mock. See <see cref="AuthentikContainerFixture"/>'s remarks for why this class exists:
/// the mocked <see cref="AuthentikAdminClientTests"/> suite stayed green through the entire
/// window where this call was silently broken against every real deployment, because a mock has
/// no way to know whether the real dependency (a provisioned recovery flow) exists.
///
/// This is deliberately the ONLY integration test in this assembly that runs a real Authentik —
/// heavier than the rest of the suite (5 containers: network + Postgres + Redis + authentik-
/// migrate + authentik-server + authentik-worker) and gated behind <see cref="ContainerCollection"/>
/// like every other container-backed test class here, so it never runs concurrently with them.
///
/// Uses <see cref="IClassFixture{TFixture}"/>, NOT per-test IAsyncLifetime on this class: xunit
/// creates a fresh instance of the TEST CLASS per [Fact], so an IAsyncLifetime fixture built in
/// the class itself gets rebuilt (and its whole 5-container stack re-created) once per [Fact] —
/// observed directly running this file with 2 [Fact]s: two full Authentik stacks came up
/// concurrently, contended for this box's 4 cores, and both blew the migrate-completion timeout.
/// IClassFixture shares ONE fixture instance across every [Fact] in the class instead. That is
/// also why the round-3 findings' facts live in THIS class rather than a new one: a second class
/// would get its own IClassFixture instance and therefore its own 5-container Authentik stack.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class AuthentikRecoveryFlowIntegrationTests : IClassFixture<AuthentikContainerFixture>
{
    private readonly AuthentikContainerFixture _authentik;

    /// <summary>Client bound to the superuser bootstrap token — for arranging test state.</summary>
    private readonly IdpAdminClient _sut;

    /// <summary>
    /// Client bound to the LEAST-PRIVILEGED orchestrator token the platform actually deploys with.
    /// Anything asserting what the orchestrator may or may not do must go through this one;
    /// <see cref="_sut"/> is a superuser and would pass every authorization check vacuously.
    /// </summary>
    private readonly IdpAdminClient _orchestrator;

    public AuthentikRecoveryFlowIntegrationTests(AuthentikContainerFixture authentik)
    {
        _authentik = authentik;
        _sut = BuildClient(authentik.BaseUrl, authentik.AdminToken);
        _orchestrator = BuildClient(authentik.BaseUrl, AuthentikContainerFixture.OrchestratorToken);
    }

    private static IdpAdminClient BuildClient(string baseUrl, string token)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(IdpAdminClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        });

        return new IdpAdminClient(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            NullLogger<IdpAdminClient>.Instance,
            new ConfigurationBuilder().Build());
    }

    private HttpClient RawClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_authentik.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

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
        using var response = await admin.PostAsync(path, Json(JsonSerializer.Serialize(body)));
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

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<string> GroupPkAsync(string name)
    {
        using var client = RawClient(_authentik.AdminToken);
        using var response = await client.GetAsync($"/api/v3/core/groups/?name={Uri.EscapeDataString(name)}");
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var results = doc.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, $"Authentik group '{name}' must exist");
        return results[0].GetProperty("pk").GetString()!;
    }

    private async Task<string> UserPkAsync(string username)
    {
        using var client = RawClient(_authentik.AdminToken);
        using var response = await client.GetAsync($"/api/v3/core/users/?username={Uri.EscapeDataString(username)}");
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var results = doc.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, $"Authentik user '{username}' must exist");
        var pk = results[0].GetProperty("pk");
        return pk.ValueKind == JsonValueKind.Number ? pk.GetRawText() : pk.GetString()!;
    }

    private async Task<string[]> GroupPksOfUserAsync(string userId)
    {
        using var client = RawClient(_authentik.AdminToken);
        using var response = await client.GetAsync($"/api/v3/core/users/{userId}/");
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return [.. doc.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!)];
    }

    /// <summary>Unique suffix so two facts in this class never contend for the same username.</summary>
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    [Fact]
    public async Task CreateUserAsync_AgainstRealAuthentikWithShippedBlueprint_ReturnsWorkingRecoveryLink()
    {
        var result = await _sut.CreateUserAsync(
            "csr4-regression-test-user",
            "csr4-regression-test-user@iverson-test.invalid",
            "csr4-regression-tenant",
            groups: []);

        result.UserId.Should().NotBeNullOrEmpty();

        // This is the exact assertion that would have failed against the 94a05ae regression:
        // Authentik's /recovery/ response there was {"non_field_errors":"No recovery flow set."},
        // which IdpAdminClient's TriggerPasswordRecoveryAsync treats as "no link" (logs a
        // warning, returns null) rather than throwing — so RecoveryLink being non-null here is
        // the one signal a real Authentik dependency was actually satisfied.
        result.RecoveryLink.Should().NotBeNullOrEmpty(
            "the platform's own recovery-flow.yaml blueprint must be applied and reachable; " +
            "a null link here means the same regression CSR round-2 finding #4's live check caught");
        result.RecoveryLink.Should().Contain(
            "/if/flow/iverson-recovery/",
            "the link must come from THIS platform's own recovery flow, not some other default");
    }

    [Fact]
    public async Task CreateUserAsync_TwoUsers_EachGetsADistinctRecoveryLink()
    {
        // Guards against a trivial-but-real failure mode: a cached/static link, or a handler bug
        // that returns the readiness probe's own link instead of freshly minting one per call.
        var first = await _sut.CreateUserAsync(
            "csr4-regression-user-a", "csr4-regression-user-a@iverson-test.invalid", "csr4-regression-tenant", []);
        var second = await _sut.CreateUserAsync(
            "csr4-regression-user-b", "csr4-regression-user-b@iverson-test.invalid", "csr4-regression-tenant", []);

        first.RecoveryLink.Should().NotBeNullOrEmpty();
        second.RecoveryLink.Should().NotBeNullOrEmpty();
        first.RecoveryLink.Should().NotBe(second.RecoveryLink);
    }

    // ───────────────────────── CSR round-3 finding #1: object-scoped orchestrator role ──────────

    /// <summary>
    /// The shipped role must hold EXACTLY the three global model permissions that cannot be
    /// object-scoped, and nothing else. This is the assertion that fails the moment anyone puts
    /// change_user, reset_user_password, change_group or either group-membership verb back on the
    /// global list — which is what the finding was.
    /// </summary>
    [Fact]
    public async Task OrchestratorRole_HoldsOnlyTheThreeGlobalPermissionsThatCannotBeObjectScoped()
    {
        using var admin = RawClient(_authentik.AdminToken);

        using var roleResponse = await admin.GetAsync("/api/v3/rbac/roles/?name=iverson-admin-orchestrator");
        roleResponse.EnsureSuccessStatusCode();
        using var roleDoc = await JsonDocument.ParseAsync(await roleResponse.Content.ReadAsStreamAsync());
        var roleUuid = roleDoc.RootElement.GetProperty("results")[0].GetProperty("pk").GetString();

        using var permResponse = await admin.GetAsync($"/api/v3/rbac/permissions/?role={roleUuid}&page_size=100");
        permResponse.EnsureSuccessStatusCode();
        using var permDoc = await JsonDocument.ParseAsync(await permResponse.Content.ReadAsStreamAsync());

        var granted = permDoc.RootElement.GetProperty("results").EnumerateArray()
            .Select(p => $"{p.GetProperty("app_label").GetString()}.{p.GetProperty("codename").GetString()}")
            .ToArray();

        granted.Should().BeEquivalentTo(
            ["authentik_core.add_user", "authentik_core.view_user", "authentik_core.view_group"],
            "a global grant applies to EVERY user/group in the IdP, so only verbs that genuinely " +
            "need the whole object space may be global (CSR round-3 finding #1)");
    }

    /// <summary>
    /// The object-scoping half: <c>reset_user_password</c> is not global, so the orchestrator can
    /// mint a recovery link only for accounts it created. Minting one for <c>akadmin</c> is the
    /// exact account-takeover step at the head of the report's worst chain.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_CanMintRecoveryLinkForItsOwnUser_ButNotForAnAccountItDidNotCreate()
    {
        var created = await _orchestrator.CreateUserAsync(
            Unique("csr3-own"), "csr3-own@iverson-test.invalid", "csr3-tenant", groups: []);

        created.RecoveryLink.Should().NotBeNullOrEmpty(
            "InitialPermissions must grant the role reset_user_password on the user it just created");

        var akadminPk = await UserPkAsync("akadmin");
        using var orchestrator = RawClient(AuthentikContainerFixture.OrchestratorToken);
        using var response = await orchestrator.PostAsync($"/api/v3/core/users/{akadminPk}/recovery/", Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a recovery link for a superuser account is a full account takeover; the orchestrator " +
            "must not be able to mint one for an account it did not create");
    }

    /// <summary>
    /// Same scoping, the other verb: PATCHing a user is how the orchestrator deactivates and
    /// re-groups accounts, and it must not reach accounts it did not create.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_CanPatchItsOwnUser_ButNotAnAccountItDidNotCreate()
    {
        var created = await _orchestrator.CreateUserAsync(
            Unique("csr3-patch"), "csr3-patch@iverson-test.invalid", "csr3-tenant", groups: []);

        using var orchestrator = RawClient(AuthentikContainerFixture.OrchestratorToken);

        using var ownRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{created.UserId}/")
        {
            Content = Json("""{"is_active":false}""")
        };
        using var ownResponse = await orchestrator.SendAsync(ownRequest);
        ownResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var akadminPk = await UserPkAsync("akadmin");
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{akadminPk}/")
        {
            Content = Json("""{"is_active":false}""")
        };
        using var foreignResponse = await orchestrator.SendAsync(foreignRequest);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "deactivating (or otherwise rewriting) an account it did not create is outside the " +
            "orchestrator's job and was the blast radius CSR round-3 finding #1 flagged");
    }

    /// <summary>
    /// The privilege-escalation step the finding is really about. Authentik's
    /// <c>GroupViewSet.add_user</c> has NO superuser-group check of its own, so a token holding the
    /// global <c>add_user_to_group</c> permission could add any account to <c>authentik Admins</c>
    /// and become superuser. Both routes to that must now fail — the group action because the
    /// permission is gone, and the user-groups PATCH because Authentik refuses to add members to a
    /// superuser group without <c>enable_group_superuser</c>.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_CannotAddAnyoneToAuthentikAdmins_ByEitherRoute()
    {
        var created = await _orchestrator.CreateUserAsync(
            Unique("csr3-escalate"), "csr3-escalate@iverson-test.invalid", "csr3-tenant", groups: []);
        var adminsPk = await GroupPkAsync("authentik Admins");

        using var orchestrator = RawClient(AuthentikContainerFixture.OrchestratorToken);

        using var groupActionResponse = await orchestrator.PostAsync(
            $"/api/v3/core/groups/{adminsPk}/add_user/", Json($$"""{"pk":{{created.UserId}}}"""));
        groupActionResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the global add_user_to_group permission that gates this endpoint has been removed");

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{created.UserId}/")
        {
            Content = Json($$"""{"groups":["{{adminsPk}}"]}""")
        };
        using var patchResponse = await orchestrator.SendAsync(patchRequest);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Authentik's own validate_groups refuses a new member of a superuser group without " +
            "enable_group_superuser, which this role does not hold");
        (await patchResponse.Content.ReadAsStringAsync()).Should().Contain("superuser group");
    }

    /// <summary>
    /// Least privilege is only a fix if the product still works under it. This drives the real
    /// <see cref="IdpAdminClient"/> membership calls — the ones
    /// <c>TenantAdminGrpcService.SetTenantAdmin</c> makes — through the scoped role end to end.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_CanStillAddAndRemoveTenantAdminsMembership()
    {
        var username = Unique("csr3-member");
        var created = await _orchestrator.CreateUserAsync(
            username, $"{username}@iverson-test.invalid", "csr3-tenant", groups: []);
        var tenantAdminsPk = await GroupPkAsync("tenant-admins");

        await _orchestrator.AddGroupAsync(created.UserId, "tenant-admins");
        (await GroupPksOfUserAsync(created.UserId)).Should().Contain(tenantAdminsPk);

        // Idempotent: a second add must not fail or duplicate.
        await _orchestrator.AddGroupAsync(created.UserId, "tenant-admins");
        (await GroupPksOfUserAsync(created.UserId)).Should().ContainSingle(pk => pk == tenantAdminsPk);

        await _orchestrator.RemoveGroupAsync(created.UserId, "tenant-admins");
        (await GroupPksOfUserAsync(created.UserId)).Should().NotContain(tenantAdminsPk);
    }

    // ───────────────────────── CSR round-3 finding #2: MFA on the recovery flow ─────────────────

    /// <summary>
    /// The onboarding half of finding #2's fix: <c>not_configured_action: skip</c> must leave a
    /// brand-new invitee — who by definition has no enrolled factor — able to complete the flow.
    /// This is the CSR round-2 finding #4 regression class all over again, so it is asserted
    /// behaviourally (the flow runs to a real authenticated session), not by reading config.
    /// </summary>
    [Fact]
    public async Task RecoveryFlow_UserWithNoEnrolledFactor_CompletesAndSignsTheUserIn()
    {
        var username = Unique("csr3-nomfa");
        var created = await _orchestrator.CreateUserAsync(
            username, $"{username}@iverson-test.invalid", "csr3-tenant", groups: []);

        using var flow = new RecoveryFlowSession(_authentik.BaseUrl, created.RecoveryLink!);

        (await flow.StartAsync()).Should().Be("ak-stage-prompt");
        var afterPassword = await flow.SubmitPasswordAsync("Correct-Horse-Battery-9");

        afterPassword.Should().Be("xak-flow-redirect",
            "with no enrolled factor the MFA stage must skip, letting onboarding finish");
        (await flow.AuthenticatedUsernameAsync()).Should().Be(username);
    }

    /// <summary>
    /// The security half of finding #2's fix. Before it, this flow went
    /// prompt → user_write → user_login with no authenticator stage at all, so a leaked recovery
    /// link completed to an authenticated session with the account's second factor never
    /// consulted — the MFA-bypass link in the report's worst chain.
    /// </summary>
    [Fact]
    public async Task RecoveryFlow_UserWithEnrolledTotp_IsChallengedAndCannotCompleteWithoutIt()
    {
        var username = Unique("csr3-totp");
        var created = await _orchestrator.CreateUserAsync(
            username, $"{username}@iverson-test.invalid", "csr3-tenant", groups: []);
        await _authentik.EnrollTotpDeviceAsync(username);

        using var flow = new RecoveryFlowSession(_authentik.BaseUrl, created.RecoveryLink!);

        (await flow.StartAsync()).Should().Be("ak-stage-prompt");
        var afterPassword = await flow.SubmitPasswordAsync("Correct-Horse-Battery-9");

        afterPassword.Should().Be("ak-stage-authenticator-validate",
            "an account with an enrolled factor must be challenged before the reset completes");
        flow.OfferedDeviceClasses().Should().Contain("totp",
            "the challenge must be for the factor the account actually enrolled");

        (await flow.AuthenticatedUsernameAsync()).Should().BeNull(
            "the flow must not have reached the user_login stage");

        var afterWrongCode = await flow.SubmitTotpCodeAsync("000000");
        afterWrongCode.Should().NotBe("xak-flow-redirect",
            "a wrong code must not let the flow fall through to completion");
        (await flow.AuthenticatedUsernameAsync()).Should().BeNull(
            "a failed second factor must leave the session unauthenticated");
    }

    /// <summary>
    /// Minimal driver for Authentik's flow-executor API — enough to walk the recovery flow the way
    /// a browser does. Cookies are the flow plan's only carrier, so one session per flow run.
    /// </summary>
    private sealed class RecoveryFlowSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _executorUrl;

        public string LastChallengeBody { get; private set; } = "";

        public RecoveryFlowSession(string baseUrl, string recoveryLink)
        {
            var flowToken = new Uri(recoveryLink).Query.TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] == "flow_token")
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault();
            flowToken.Should().NotBeNullOrEmpty("the recovery link must carry a flow_token");

            _client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            {
                BaseAddress = new Uri(baseUrl)
            };
            _executorUrl = "/api/v3/flows/executor/iverson-recovery/?query=" +
                           Uri.EscapeDataString($"flow_token={flowToken}");
        }

        public Task<string?> StartAsync() => ReadComponentAsync(_client.GetAsync(_executorUrl));

        public Task<string?> SubmitPasswordAsync(string password) => ReadComponentAsync(
            _client.PostAsync(_executorUrl, Json(
                $$"""{"component":"ak-stage-prompt","password":"{{password}}","password_repeat":"{{password}}"}""")));

        public Task<string?> SubmitTotpCodeAsync(string code) => ReadComponentAsync(
            _client.PostAsync(_executorUrl, Json(
                $$"""{"component":"ak-stage-authenticator-validate","code":"{{code}}"}""")));

        /// <summary>Device classes the last challenge offered (empty when it wasn't an MFA challenge).</summary>
        public string[] OfferedDeviceClasses()
        {
            using var doc = JsonDocument.Parse(LastChallengeBody);
            return doc.RootElement.TryGetProperty("device_challenges", out var challenges) &&
                   challenges.ValueKind == JsonValueKind.Array
                ? [.. challenges.EnumerateArray().Select(c => c.GetProperty("device_class").GetString()!)]
                : [];
        }

        /// <summary>
        /// The username this flow's SESSION is signed in as, or null if it never authenticated.
        /// Deliberately asks Authentik rather than trusting the flow's own reported component: the
        /// thing that matters is whether an attacker ends up with a session.
        /// </summary>
        public async Task<string?> AuthenticatedUsernameAsync()
        {
            using var response = await _client.GetAsync("/api/v3/core/users/me/");
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.TryGetProperty("user", out var user) &&
                   user.TryGetProperty("username", out var username) &&
                   username.GetString() != "AnonymousUser"
                ? username.GetString()
                : null;
        }

        private async Task<string?> ReadComponentAsync(Task<HttpResponseMessage> pending)
        {
            using var response = await pending;
            LastChallengeBody = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(LastChallengeBody);
            return doc.RootElement.TryGetProperty("component", out var component)
                ? component.GetString()
                : null;
        }

        public void Dispose() => _client.Dispose();
    }
}
