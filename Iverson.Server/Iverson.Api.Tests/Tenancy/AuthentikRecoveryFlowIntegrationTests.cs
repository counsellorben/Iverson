using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tenancy;
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
            NullLogger<IdpAdminClient>.Instance);
    }

    private HttpClient RawClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_authentik.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

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
