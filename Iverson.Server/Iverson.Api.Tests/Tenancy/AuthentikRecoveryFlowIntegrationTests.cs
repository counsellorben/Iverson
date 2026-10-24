using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
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
/// heavier than the rest of the suite (a network and 4 containers: Postgres + authentik-
/// migrate + authentik-server + authentik-worker) and gated behind <see cref="ContainerCollection"/>
/// like every other container-backed test class here, so it never runs concurrently with them.
///
/// Uses <see cref="IClassFixture{TFixture}"/>, NOT per-test IAsyncLifetime on this class: xunit
/// creates a fresh instance of the TEST CLASS per [Fact], so an IAsyncLifetime fixture built in
/// the class itself gets rebuilt (and its whole 4-container stack re-created) once per [Fact] —
/// observed directly running this file with 2 [Fact]s: two full Authentik stacks came up
/// concurrently, contended for this box's 4 cores, and both blew the migrate-completion timeout.
/// IClassFixture shares ONE fixture instance across every [Fact] in the class instead. That is
/// also why the round-3 findings' facts live in THIS class rather than a new one: a second class
/// would get its own IClassFixture instance and therefore its own 4-container Authentik stack.
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
    // Must equal the user the shipped blueprint's pin and mapping expressions name (both are read from
    // the template, so they name this user, not a test copy). The fixture is shared across the class, so
    // a second fact that pre-created this user would collide with this one.
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
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();
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

        // Wrong issuer, wrong subject, wrong audience, arrival through the public listener, no pod binding, or a lifetime over 600 s: refused, nobody created.
        var usersAfter = await UserCountAsync(admin);
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, FederatedAudience, issuer: "https://issuer.invalid")))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, "system:serviceaccount:iverson-test:other", FederatedAudience)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, "somebody-else")))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, assertion, publicIngressMarker: true))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, FederatedAudience, podBound: false)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await ExchangeAsync(clientId, Assertion(rsa, kid, FederatedSubject, FederatedAudience, lifetimeSeconds: 3600)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await UserCountAsync(admin)).Should().Be(usersAfter);
    }

    /// <summary>
    /// Creates the federation objects and returns the provider's client id. The application runs in `all` mode
    /// with the shipped pin and marker bound in that order, or with <paramref name="policies"/> in their place;
    /// the source maps every assertion to the shipped username, or to <paramref name="usernameExpression"/>'s.
    /// </summary>
    private static async Task<string> CreateFederationObjectsAsync(
        HttpClient admin, RSA rsa, string kid, string? usernameExpression = null, string[]? policies = null)
    {
        var key = rsa.ExportParameters(false);
        var mapping = await PostAsync(admin, "/api/v3/propertymappings/source/oauth/",
            new { name = $"{kid} username", expression = usernameExpression ?? AuthentikOrchestratorFederationBlueprintTests.UsernameMappingExpression() });
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
        // The expressions are the SHIPPED ones (orchestrator-federation.yaml), not a copy, so this
        // fact fails if the template's pin, marker or mapping stops behaving as tested.
        policies ??= new[]
        {
            AuthentikOrchestratorFederationBlueprintTests.PinPolicyExpression(FederatedSubject),
            AuthentikOrchestratorFederationBlueprintTests.InClusterOnlyExpression(),
        };
        for (var order = 0; order < policies.Length; order++)
        {
            var policy = await PostAsync(admin, "/api/v3/policies/expression/",
                new { name = $"{kid} policy {order}", expression = policies[order] });
            await PostAsync(admin, "/api/v3/policies/bindings/",
                new { policy = policy.GetProperty("pk"), target = app.GetProperty("pk"), order, enabled = true });
        }
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
        // A success body carries an access token, so a body that is not JSON is reported by status only.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (JsonException)
        {
            Assert.Fail($"The token endpoint answered HTTP {(int)response.StatusCode} with a body that is not JSON.");
            throw;
        }
        using (doc)
            return (response.StatusCode, doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null);
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

    /// <summary>
    /// An RS256 JWT shaped like a kubelet-projected service-account token: pod-bound (a
    /// <c>kubernetes.io.pod</c> claim) with a 600 s life, unless told otherwise.
    /// </summary>
    private static string Assertion(
        RSA rsa, string kid, string subject, string audience, bool podBound = true, int lifetimeSeconds = 600,
        string issuer = AuthentikOrchestratorFederationBlueprintTests.FixtureIssuer)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid }));
        var claims = new Dictionary<string, object>
        {
            ["iss"] = issuer, ["sub"] = subject, ["aud"] = new[] { audience },
            ["iat"] = now, ["exp"] = now + lifetimeSeconds,
        };
        var kubernetes = new Dictionary<string, object>
        {
            ["namespace"] = "iverson-test",
            ["serviceaccount"] = new { name = "iverson-api", uid = "00000000-0000-0000-0000-000000000001" },
        };
        if (podBound)
            kubernetes["pod"] = new { name = "iverson-api-0", uid = "00000000-0000-0000-0000-000000000002" };
        claims["kubernetes.io"] = kubernetes;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
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
    /// The privilege-escalation step the finding is really about. On 2026.5.3 Authentik's
    /// <c>GroupViewSet.add_user</c> had no superuser-group check of its own, so a token holding the
    /// global <c>add_user_to_group</c> permission could add any account to <c>authentik Admins</c>
    /// and become superuser (2026.8.4 refuses a superuser group there without
    /// <c>enable_group_superuser</c>). Both routes to that must fail — the group action because the
    /// role holds the permission on <c>tenant-admins</c> only, and the user-groups PATCH because
    /// Authentik refuses to add members to a superuser group without <c>enable_group_superuser</c>.
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
            "the role holds add_user_to_group on tenant-admins only, never globally");

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

    // ───────────────────────── CSR round 12 §2.1: tenant directory reads ──────────────────────

    /// <summary>
    /// ListUsers and DeleteTenant read only the caller's tenant: Authentik filters by
    /// <c>attributes.tenant_id</c> for the orchestrator's own token, and the client keeps its own
    /// check on what comes back.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_ListUsersByTenant_ReadsOnlyThatTenantsUsers()
    {
        var tenant = Unique("csr12-list");
        var usernames = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var username = Unique("csr12-in");
            usernames.Add(username);
            await _orchestrator.CreateUserAsync(username, $"{username}@iverson-test.invalid", tenant, groups: []);
        }
        var outsider = Unique("csr12-out");
        await _orchestrator.CreateUserAsync(outsider, $"{outsider}@iverson-test.invalid", Unique("csr12-other"), groups: []);

        var users = (await _orchestrator.ListUsersByTenantAsync(tenant)).ToList();

        users.Select(u => u.Username).Should().BeEquivalentTo(usernames);
        users.Should().OnlyContain(u => !string.IsNullOrEmpty(u.Uid));

        // Authentik applied the filter itself: the count is the tenant's, not the directory's.
        using var orchestrator = RawClient(AuthentikContainerFixture.OrchestratorToken);
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string> { ["tenant_id"] = tenant }));
        using var page = JsonDocument.Parse(await orchestrator.GetStringAsync($"/api/v3/core/users/?attributes={filter}&page_size=1"));
        page.RootElement.GetProperty("pagination").GetProperty("count").GetInt32().Should().Be(3);
    }

    /// <summary>RemoveUser's and SetTenantAdmin's tenant check reads one user by pk, with the orchestrator's token.</summary>
    [Fact]
    public async Task OrchestratorToken_GetUserAsync_ReadsOneUserByPk_AndAnUnknownPkIsNull()
    {
        var tenant = Unique("csr12-get");
        var username = Unique("csr12-one");
        var created = await _orchestrator.CreateUserAsync(username, $"{username}@iverson-test.invalid", tenant, groups: []);

        var user = await _orchestrator.GetUserAsync(created.UserId);

        user.Should().NotBeNull();
        user!.Id.Should().Be(created.UserId);
        user.Username.Should().Be(username);
        user.TenantId.Should().Be(tenant);
        user.IsActive.Should().BeTrue();
        user.Uid.Should().NotBeNullOrEmpty();
        (await _orchestrator.GetUserAsync("2147483647")).Should().BeNull();
    }

    // ───────────────────────── CSR round-3 finding #2: MFA on the recovery flow ─────────────────

    /// <summary>
    /// CSR round-11 #5: an account with no second factor enrols TOTP inside its recovery flow, after
    /// choosing a password and before the password is written, so the factor belongs to whoever
    /// completes the link rather than to whoever signs in first. Asserted behaviourally: the flow
    /// stops at TOTP setup with no session, and only a valid code reaches a real authenticated
    /// session.
    /// </summary>
    [Fact]
    public async Task RecoveryFlow_UserWithNoEnrolledFactor_EnrolsTotpBeforeSigningIn()
    {
        var username = Unique("csr11-nomfa");
        var created = await _orchestrator.CreateUserAsync(
            username, $"{username}@iverson-test.invalid", "csr11-tenant", groups: []);

        using var flow = new RecoveryFlowSession(_authentik.BaseUrl, created.RecoveryLink!);

        (await flow.StartAsync()).Should().Be("ak-stage-prompt");
        (await flow.SubmitPasswordAsync("Correct-Horse-Battery-9")).Should().Be("ak-stage-authenticator-totp",
            "an account with no enrolled factor must enrol one before the reset completes");
        (await flow.AuthenticatedUsernameAsync()).Should().BeNull("enrolment has not finished");

        (await flow.SubmitTotpSetupCodeAsync(Totp(flow.TotpSetupSecret()))).Should().Be("xak-flow-redirect");
        (await flow.AuthenticatedUsernameAsync()).Should().Be(username);
    }

    /// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s), the code Authentik's TOTP stages accept.</summary>
    private static string Totp(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in base32Secret.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                key.Add((byte)(buffer >> bits));
            }
        }
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key.ToArray(), counter);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3]) % 1_000_000;
        return code.ToString("D6");
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

    // ───────────────────────── CSR round 12: the login and admin planes on 2026.8.4 ─────────────────
    //
    // Every request in this class reaches Authentik from one client address, so the throttle facts
    // delete every reputation row before and after they run.

    private const string GoodPassword = "Correct-Horse-Battery-9";
    private const string Throttled = "Too many failed attempts. Try again later.";

    /// <summary>A user with a password, created with the superuser token; optionally in <paramref name="groupPks"/>.</summary>
    private async Task<(string Pk, string Username)> CreateUserWithPasswordAsync(string prefix, params string[] groupPks)
    {
        using var admin = RawClient(_authentik.AdminToken);
        var username = Unique(prefix);
        var user = await PostAsync(admin, "/api/v3/core/users/",
            new { username, name = username, email = $"{username}@iverson-test.invalid", is_active = true, groups = groupPks });
        var pk = user.GetProperty("pk").GetRawText();
        using var response = await admin.PostAsync($"/api/v3/core/users/{pk}/set_password/",
            Json(JsonSerializer.Serialize(new { password = GoodPassword })));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (pk, username);
    }

    private async Task<string> RecoveryLinkAsync(string userPk)
    {
        using var admin = RawClient(_authentik.AdminToken);
        using var response = await admin.PostAsync($"/api/v3/core/users/{userPk}/recovery/", Json("{}"));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("link").GetString()!;
    }

    private async Task ClearReputationAsync()
    {
        using var admin = RawClient(_authentik.AdminToken);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/api/v3/policies/reputation/scores/?page_size=1000"));
        foreach (var row in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            using var response = await admin.DeleteAsync($"/api/v3/policies/reputation/scores/{row.GetProperty("pk").GetString()}/");
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    private async Task<long> ScoreAsync(string identifier)
    {
        using var admin = RawClient(_authentik.AdminToken);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync(
            $"/api/v3/policies/reputation/scores/?identifier={Uri.EscapeDataString(identifier)}"));
        return doc.RootElement.GetProperty("results").EnumerateArray().Sum(row => row.GetProperty("score").GetInt64());
    }

    /// <summary>One login plan up to the stage after identification; returns that stage's component.</summary>
    private async Task<(LoginFlowSession Plan, string? Component)> IdentifyAsync(string username, bool publicListener = false)
    {
        var plan = new LoginFlowSession(_authentik.BaseUrl, publicListener);
        (await plan.StartAsync()).Should().Be("ak-stage-identification");
        return (plan, await plan.IdentifyAsync(username));
    }

    /// <summary>
    /// CSR round 12 §2.5, live with the shipped reputation.yaml. A known username's rows for this address
    /// fall by one per wrong password; at −5 the next plan is refused at order 12, before any password is
    /// asked, and so is a recovery link for the account minted afterwards (order 0 of iverson-recovery).
    /// </summary>
    [Fact]
    public async Task LoginThrottle_RefusesANewPlanAndARecoveryLink_OnceThePairReachesMinusFive()
    {
        await ClearReputationAsync();
        try
        {
            var (pk, username) = await CreateUserWithPasswordAsync("csr12-pair");
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var (plan, component) = await IdentifyAsync(username);
                using (plan)
                {
                    component.Should().Be("ak-stage-password", $"plan {attempt} starts with the pair above −5");
                    (await plan.SubmitPasswordAsync("wrong-password")).Should().Be("ak-stage-password");
                }
            }
            (await ScoreAsync(username)).Should().Be(-5);

            var (refused, refusedComponent) = await IdentifyAsync(username);
            using (refused)
            {
                refusedComponent.Should().Be("ak-stage-access-denied");
                refused.ErrorMessage().Should().Be(Throttled);
            }

            using var recovery = new RecoveryFlowSession(_authentik.BaseUrl, await RecoveryLinkAsync(pk));
            (await recovery.StartAsync()).Should().Be("ak-stage-access-denied", "the throttle runs at order 0 of iverson-recovery");
            recovery.ErrorMessage().Should().Be(Throttled);
        }
        finally
        {
            await ClearReputationAsync();
        }
    }

    /// <summary>
    /// CSR round 12 §2.5. An unknown username scores at identification and at the password, so its third plan
    /// is refused after two failed attempts. Ten such identifiers bring this address's failures to −50, which
    /// refuses every new plan from it, a known account with no failures of its own included; at nine (−45)
    /// that account is still asked for its password.
    /// </summary>
    [Fact]
    public async Task LoginThrottle_RefusesEveryNewPlan_FromAnAddressWhoseFailuresReachMinusFifty()
    {
        await ClearReputationAsync();
        try
        {
            var (_, known) = await CreateUserWithPasswordAsync("csr12-addr");
            for (var identifier = 1; identifier <= 10; identifier++)
            {
                if (identifier == 10)
                {
                    var (control, controlComponent) = await IdentifyAsync(known);
                    using (control)
                        controlComponent.Should().Be("ak-stage-password", "nine identifiers at −5 sum to −45, above the cap");
                }

                var unknown = Unique("csr12-nobody");
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var (plan, component) = await IdentifyAsync(unknown);
                    using (plan)
                    {
                        component.Should().Be("ak-stage-password");
                        (await plan.SubmitPasswordAsync("wrong-password")).Should().Be("ak-stage-password");
                    }
                }
                var (third, thirdComponent) = await IdentifyAsync(unknown);
                using (third)
                    thirdComponent.Should().Be("ak-stage-access-denied", "an unknown username's third plan starts at −5");
                (await ScoreAsync(unknown)).Should().Be(-5);
            }

            var (refused, refusedComponent) = await IdentifyAsync(known);
            using (refused)
            {
                refusedComponent.Should().Be("ak-stage-access-denied", "ten identifiers at −5 sum to −50");
                refused.ErrorMessage().Should().Be(Throttled);
            }
        }
        finally
        {
            await ClearReputationAsync();
        }
    }

    /// <summary>
    /// CSR round 12 §2.7 (Verified assumption 33, re-run in the suite): with the shipped superusers.yaml, which
    /// the fixture applies, a superuser is refused on the public listener after identification, before any
    /// password is asked, and a recovery link for one is refused there. Without the listener's marker the same
    /// account is asked for its password and its link opens.
    /// </summary>
    [Fact]
    public async Task ShippedSuperusersBlueprint_RefusesASuperuser_OnlyOnThePublicListener()
    {
        var (pk, username) = await CreateUserWithPasswordAsync("csr12-super", await GroupPkAsync("authentik Admins"));

        var (publicPlan, publicComponent) = await IdentifyAsync(username, publicListener: true);
        using (publicPlan)
        {
            publicComponent.Should().Be("ak-stage-access-denied");
            publicPlan.ErrorMessage().Should().Be("Administrators sign in through the in-cluster route.");
        }
        var (inCluster, inClusterComponent) = await IdentifyAsync(username);
        using (inCluster)
            inClusterComponent.Should().Be("ak-stage-password");

        using (var publicRecovery = new RecoveryFlowSession(_authentik.BaseUrl, await RecoveryLinkAsync(pk), publicListener: true))
            (await publicRecovery.StartAsync()).Should().Be("ak-stage-access-denied");
        using var inClusterRecovery = new RecoveryFlowSession(_authentik.BaseUrl, await RecoveryLinkAsync(pk));
        (await inClusterRecovery.StartAsync()).Should().Be("ak-stage-prompt");
    }

    /// <summary>
    /// CSR round 12 §2.5 (Verified assumption 19 on 2026.8.4): with mfa-enforcement.yaml as Helm renders it where
    /// no test identities are provisioned (not_configured_action deny), a user with no device is refused after its
    /// password, and a user with a TOTP device is challenged. Helm's own render is checked by the chart's render
    /// checks; this applies the same substitution, then re-applies the shipped file for the facts that follow.
    /// </summary>
    [Fact]
    public async Task MfaDeny_RefusesAUserWithNoDevice_AndChallengesAUserWithOne()
    {
        var shipped = await File.ReadAllTextAsync(Path.Combine(
            AuthentikOrchestratorRoleBlueprintTests.AuthentikChartDirectory(), "blueprints", "mfa-enforcement.yaml"));
        var deny = shipped.Replace("not_configured_action: configure", "not_configured_action: deny");
        deny.Should().NotBe(shipped, "templates/blueprints-configmap.yaml rewrites exactly this value");
        await _authentik.ImportBlueprintAsync(deny);
        try
        {
            var (_, bare) = await CreateUserWithPasswordAsync("csr12-nodevice");
            var (refused, _) = await IdentifyAsync(bare);
            using (refused)
            {
                (await refused.SubmitPasswordAsync(GoodPassword)).Should().Be("ak-stage-access-denied");
                (await refused.AuthenticatedUsernameAsync()).Should().BeNull();
            }

            var (_, enrolled) = await CreateUserWithPasswordAsync("csr12-device");
            await _authentik.EnrollTotpDeviceAsync(enrolled);
            var (challenged, _) = await IdentifyAsync(enrolled);
            using (challenged)
            {
                (await challenged.SubmitPasswordAsync(GoodPassword)).Should().Be("ak-stage-authenticator-validate");
                (await challenged.AuthenticatedUsernameAsync()).Should().BeNull();
            }
        }
        finally
        {
            await _authentik.ImportBlueprintAsync(shipped);
        }
    }

    /// <summary>
    /// CSR round 12 §2.4 (Verified assumptions 28 and 73): the orchestrator role holds add_user_to_group and
    /// remove_user_from_group on the tenant-admins group object only, through that entry's permissions in
    /// compose-only/service-clients.yaml, so the group endpoints work there and are refused on every other group.
    /// </summary>
    [Fact]
    public async Task OrchestratorToken_AddsAndRemovesMembers_OfTenantAdminsOnly()
    {
        var username = Unique("csr12-member");
        var created = await _orchestrator.CreateUserAsync(username, $"{username}@iverson-test.invalid", "csr12-tenant", groups: []);
        using var orchestrator = RawClient(AuthentikContainerFixture.OrchestratorToken);
        var body = $$"""{"pk":{{created.UserId}}}""";

        var tenantAdminsPk = await GroupPkAsync("tenant-admins");
        using (var added = await orchestrator.PostAsync($"/api/v3/core/groups/{tenantAdminsPk}/add_user/", Json(body)))
            added.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GroupPksOfUserAsync(created.UserId)).Should().Contain(tenantAdminsPk);
        using (var removed = await orchestrator.PostAsync($"/api/v3/core/groups/{tenantAdminsPk}/remove_user/", Json(body)))
            removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GroupPksOfUserAsync(created.UserId)).Should().NotContain(tenantAdminsPk);

        foreach (var other in new[] { "operators", "authentik Admins", "iverson-admin-orchestrators" })
        {
            var otherPk = await GroupPkAsync(other);
            using var add = await orchestrator.PostAsync($"/api/v3/core/groups/{otherPk}/add_user/", Json(body));
            add.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"the role holds the group verbs on tenant-admins only, not on {other}");
            using var remove = await orchestrator.PostAsync($"/api/v3/core/groups/{otherPk}/remove_user/", Json(body));
            remove.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"the role holds the group verbs on tenant-admins only, not on {other}");
        }
    }

    /// <summary>
    /// CSR round 12 §2.2, §2.4: the console sign-in policy, evaluated live. The compose copy, bound to iverson-api
    /// in compose-only/service-clients.yaml (which this fixture applies), admits tenant-admins and operators
    /// members. The Helm expression (templates/secret-service-clients.yaml) admits tenant-admins members and the
    /// subjects on the operator list, and not an operators member. Everyone else is refused by both.
    /// </summary>
    [Fact]
    public async Task ConsoleSignInPolicy_AdmitsOnlyTenantAdminsAndOperators()
    {
        using var admin = RawClient(_authentik.AdminToken);
        var (tenantAdmin, _) = await CreateUserWithPasswordAsync("csr12-ta", await GroupPkAsync("tenant-admins"));
        var (groupOperator, _) = await CreateUserWithPasswordAsync("csr12-op", await GroupPkAsync("operators"));
        var (listed, _) = await CreateUserWithPasswordAsync("csr12-listed");
        var (ordinary, _) = await CreateUserWithPasswordAsync("csr12-plain");

        var app = await GetAsync(admin, "/api/v3/core/applications/iverson-api/");
        using (var bindings = JsonDocument.Parse(await admin.GetStringAsync(
                   $"/api/v3/policies/bindings/?target={app.GetProperty("pk").GetString()}")))
        {
            bindings.RootElement.GetProperty("results").EnumerateArray()
                .Select(b => b.GetProperty("policy_obj").GetProperty("name").GetString())
                .Should().Equal("iverson-console-admins");
        }
        var compose = (await FirstAsync(admin, "/api/v3/policies/expression/?name=iverson-console-admins", "pk")).GetString()!;
        (await PassesAsync(admin, compose, tenantAdmin)).Should().BeTrue();
        (await PassesAsync(admin, compose, groupOperator)).Should().BeTrue();
        (await PassesAsync(admin, compose, listed)).Should().BeFalse();
        (await PassesAsync(admin, compose, ordinary)).Should().BeFalse();

        var uid = (await GetAsync(admin, $"/api/v3/core/users/{listed}/")).GetProperty("uid").GetString()!;
        var helm = (await PostAsync(admin, "/api/v3/policies/expression/",
            new { name = Unique("csr12-console-helm"), expression = HelmConsoleSignInExpression(uid) })).GetProperty("pk").GetString()!;
        (await PassesAsync(admin, helm, tenantAdmin)).Should().BeTrue();
        (await PassesAsync(admin, helm, listed)).Should().BeTrue();
        (await PassesAsync(admin, helm, groupOperator)).Should().BeFalse("on Helm no group yields Operator");
        (await PassesAsync(admin, helm, ordinary)).Should().BeFalse();
    }

    /// <summary>The Helm console sign-in expression, with the rendered operator list replaced by one subject.</summary>
    private static string HelmConsoleSignInExpression(string subject)
    {
        const string list = "{{ .Values.global.operatorSubjects | default (list) | toJson }}";
        var lines = File.ReadAllLines(Path.Combine(
            AuthentikOrchestratorRoleBlueprintTests.AuthentikChartDirectory(), "templates", "secret-service-clients.yaml"));
        var policy = Array.FindIndex(lines, line => line.Trim() == "name: iverson-console-admins");
        policy.Should().BeGreaterThanOrEqualTo(0, "the service-clients blueprint must carry the console sign-in policy");
        var expression = lines[Array.FindIndex(lines, policy, line => line.Trim() == "expression: |") + 1].Trim();
        expression.Should().Contain(list, "the Helm expression reads the release's operator list");
        return expression.Replace(list, JsonSerializer.Serialize(new[] { subject }));
    }

    private static async Task<JsonElement> GetAsync(HttpClient admin, string path)
    {
        using var doc = JsonDocument.Parse(await admin.GetStringAsync(path));
        return doc.RootElement.Clone();
    }

    private static async Task<bool> PassesAsync(HttpClient admin, string policyPk, string userPk)
    {
        using var response = await admin.PostAsync($"/api/v3/policies/all/{policyPk}/test/", Json($$"""{"user":{{userPk}}}"""));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("passing").GetBoolean();
    }

    /// <summary>
    /// CSR round 12 §2.6. The TokenReview policy reads the raw assertion from the token request's form body:
    /// a policy admitting exactly the presented assertion admits the exchange. The shipped review refuses when
    /// it gets no answer, which is this fixture's case (no Kubernetes API behind its relay address); the review
    /// that succeeds is exercised in the kind run.
    /// </summary>
    [Fact]
    public async Task OrchestratorTokenReview_ReadsTheRawAssertion_AndRefusesWithoutAReview()
    {
        using var admin = RawClient(_authentik.AdminToken);
        using var rsa = RSA.Create(2048);

        var seenKid = "tr-" + Guid.NewGuid().ToString("N")[..8];
        var assertion = Assertion(rsa, seenKid, FederatedSubject, FederatedAudience);
        var seen = await CreateFederationObjectsAsync(admin, rsa, seenKid,
            usernameExpression: $"return {{\"username\": \"{seenKid}-probe\"}}",
            policies: [$"return request.http_request.POST.get(\"client_assertion\") == \"{assertion}\""]);
        (await ExchangeAsync(seen, assertion)).Should().Be((HttpStatusCode.OK, (string?)null));

        var reviewKid = "tr-" + Guid.NewGuid().ToString("N")[..8];
        var review = await CreateFederationObjectsAsync(admin, rsa, reviewKid,
            usernameExpression: $"return {{\"username\": \"{reviewKid}-probe\"}}",
            policies: [AuthentikOrchestratorFederationBlueprintTests.TokenReviewPolicyExpression(FederatedSubject)]);
        var usersBefore = await UserCountAsync(admin);
        (await ExchangeAsync(review, Assertion(rsa, reviewKid, FederatedSubject, FederatedAudience)))
            .Should().Be((HttpStatusCode.BadRequest, "invalid_grant"));
        (await UserCountAsync(admin)).Should().Be(usersBefore);
    }

    /// <summary>
    /// Minimal driver for Authentik's flow-executor API — enough to walk a flow the way a browser does.
    /// Cookies are the flow plan's only carrier, so one session per plan. With <c>publicListener</c>, every
    /// request carries the marker the tls-proxy's public listener (:9080) sets.
    /// </summary>
    private abstract class FlowSession : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _executorUrl;

        public string LastChallengeBody { get; private set; } = "";

        protected FlowSession(string baseUrl, string executorUrl, bool publicListener)
        {
            _client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            {
                BaseAddress = new Uri(baseUrl)
            };
            if (publicListener)
                _client.DefaultRequestHeaders.Add("X-Iverson-Public-Ingress", "1");
            _executorUrl = executorUrl;
        }

        public Task<string?> StartAsync() => ReadComponentAsync(_client.GetAsync(_executorUrl));

        protected Task<string?> SubmitAsync(string json) => ReadComponentAsync(_client.PostAsync(_executorUrl, Json(json)));

        /// <summary>The last challenge's <c>error_message</c> (an access-denied stage's reason), or null.</summary>
        public string? ErrorMessage()
        {
            using var doc = JsonDocument.Parse(LastChallengeBody);
            return doc.RootElement.TryGetProperty("error_message", out var message) ? message.GetString() : null;
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

    /// <summary>Walks a recovery link: the <c>iverson-recovery</c> flow with the link's flow token.</summary>
    private sealed class RecoveryFlowSession(string baseUrl, string recoveryLink, bool publicListener = false)
        : FlowSession(baseUrl, "/api/v3/flows/executor/iverson-recovery/?query=" +
                               Uri.EscapeDataString($"flow_token={FlowToken(recoveryLink)}"), publicListener)
    {
        private static string FlowToken(string recoveryLink)
        {
            var flowToken = new Uri(recoveryLink).Query.TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] == "flow_token")
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault();
            flowToken.Should().NotBeNullOrEmpty("the recovery link must carry a flow_token");
            return flowToken!;
        }

        public Task<string?> SubmitPasswordAsync(string password) => SubmitAsync(
            $$"""{"component":"ak-stage-prompt","password":"{{password}}","password_repeat":"{{password}}"}""");

        public Task<string?> SubmitTotpCodeAsync(string code) => SubmitAsync(
            $$"""{"component":"ak-stage-authenticator-validate","code":"{{code}}"}""");

        public Task<string?> SubmitTotpSetupCodeAsync(string code) => SubmitAsync(
            $$"""{"component":"ak-stage-authenticator-totp","code":"{{code}}"}""");

        /// <summary>The base32 secret in the last challenge's config_url (the TOTP setup stage).</summary>
        public string TotpSetupSecret()
        {
            using var doc = JsonDocument.Parse(LastChallengeBody);
            var configUrl = new Uri(doc.RootElement.GetProperty("config_url").GetString()!);
            return HttpUtility.ParseQueryString(configUrl.Query)["secret"]!;
        }

        /// <summary>Device classes the last challenge offered (empty when it wasn't an MFA challenge).</summary>
        public string[] OfferedDeviceClasses()
        {
            using var doc = JsonDocument.Parse(LastChallengeBody);
            return doc.RootElement.TryGetProperty("device_challenges", out var challenges) &&
                   challenges.ValueKind == JsonValueKind.Array
                ? [.. challenges.EnumerateArray().Select(c => c.GetProperty("device_class").GetString()!)]
                : [];
        }
    }

    /// <summary>Walks one plan of the login flow, <c>default-authentication-flow</c>.</summary>
    private sealed class LoginFlowSession(string baseUrl, bool publicListener = false)
        : FlowSession(baseUrl, "/api/v3/flows/executor/default-authentication-flow/?query=", publicListener)
    {
        public Task<string?> IdentifyAsync(string username) => SubmitAsync(
            $$"""{"component":"ak-stage-identification","uid_field":"{{username}}"}""");

        public Task<string?> SubmitPasswordAsync(string password) => SubmitAsync(
            $$"""{"component":"ak-stage-password","password":"{{password}}"}""");
    }
}
