using System.Text.Json;

namespace Iverson.Api.Tenancy;

/// <summary>
/// Wraps Authentik's REST Core API (https://docs.goauthentik.io/developer-docs/api/) so the
/// rest of the codebase never talks HTTP/JSON to the IdP directly. Follows the same
/// IHttpClientFactory + named-client convention as Iverson.Embeddings.EmbeddingService.
///
/// CAVEAT (carried over from design/plan review): the exact JSON field names used below
/// (attributes, groups, is_active) are grounded in Authentik's documented DRF conventions
/// and public API docs, but have NOT been verified against a live instance or the
/// /api/v3/schema/ OpenAPI document. Re-verify against a running Authentik before
/// production use. (The group add_user/remove_user endpoints this caveat used to also
/// cover are gone — see <see cref="AddGroupAsync"/>, CSR round-3 finding #1; the
/// replacement groups-PATCH path IS exercised against a live Authentik by
/// Iverson.Api.Tests' AuthentikRecoveryFlowIntegrationTests.)
///
/// CSR finding #4 remediation: CreateUserAsync no longer posts a caller-supplied password to
/// Authentik's set_password endpoint. Instead it POSTs /api/v3/core/users/{id}/recovery/,
/// which Authentik's docs describe as returning a one-time recovery link
/// (<c>{"link": "..."}</c>) the user follows to set their own password directly against
/// Authentik. VERIFIED against a live instance (2026-09-11, isolated Authentik 2026.5.3 with the
/// `recovery-flow.yaml` blueprint applied): <c>/recovery/</c> returns <c>{"link":"..."}</c>. The
/// link is returned to the caller as <see cref="CreateUserResult.RecoveryLink"/> and surfaced in
/// the gRPC response (TenantUser.recovery_link / Tenant.admin_recovery_link) — see
/// <see cref="TriggerPasswordRecoveryAsync"/>.
///
/// The user-list pagination envelope IS now verified (2026-09-10, against the compose
/// Authentik at localhost:9000, image ghcr.io/goauthentik/server:2026.5.3):
/// <c>GET /api/v3/core/users/?page_size=2</c> returned
/// <c>{"pagination":{"next":2,"previous":0,"count":8,"current":1,"total_pages":4,
/// "start_index":1,"end_index":2},"results":[...]}</c>, and following with
/// <c>?page_size=2&amp;page=2</c> returned <c>"pagination":{"next":3,"previous":1,...}</c>.
/// "next" is a page NUMBER (not a URL), and is <c>0</c> — not null, not absent — once the
/// last page has been read; that matches this class's original inference exactly. See
/// <see cref="ListUsersByTenantAsync"/>, which now throws on any envelope shape it does not
/// recognise (missing "pagination", missing/non-numeric "next", or a negative "next") rather
/// than treating an unrecognised shape as end-of-list.
/// </summary>
public sealed class IdpAdminClient(IHttpClientFactory httpClientFactory, ILogger<IdpAdminClient> logger) : IIdpAdminClient
{
    public const string HttpClientName = "iverson.authentik";

    public async Task<CreateUserResult> CreateUserAsync(
        string username,
        string email,
        string tenantId,
        IReadOnlyList<string> groups)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        var groupPks = new List<string>(groups.Count);
        foreach (var groupName in groups)
            groupPks.Add(await ResolveGroupPkAsync(client, groupName));

        var createBody = new
        {
            username,
            email,
            name = username,
            is_active = true,
            attributes = new { tenant_id = tenantId },
            groups = groupPks
        };

        using var createResponse = await client.PostAsync("/api/v3/core/users/", JsonBody(createBody));
        await EnsureSuccessWithBodyAsync(createResponse, "create user");

        await using var createdStream = await createResponse.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var userId = ReadPk(createdDoc.RootElement);

        var recoveryLink = await TriggerPasswordRecoveryAsync(client, userId);

        return new CreateUserResult(userId, recoveryLink);
    }

    /// <summary>
    /// CSR finding #4 remediation: the platform must never transmit a user's password. Rather
    /// than POSTing one to Authentik's set_password endpoint, this triggers Authentik's own
    /// recovery flow — POST /api/v3/core/users/{id}/recovery/ — which Authentik's docs describe
    /// as minting a one-time link the user follows to set their own password directly against
    /// Authentik, never through this platform. See class remarks: this endpoint's shape is
    /// unverified against a live instance.
    ///
    /// The link is returned to the caller: TenantUser.recovery_link (tenant_admin.proto) and
    /// Tenant.admin_recovery_link (tenant_lifecycle.proto) carry it back through
    /// InviteUser/CreateTenant's gRPC response respectively (CSR round-2 finding #4 follow-up —
    /// this used to be log-only, requiring an inviting admin to read server logs).
    ///
    /// CSR round-3 finding #3 remediation: the link itself is NOT logged. It is a bearer
    /// credential — anyone holding it can set the account's password and, via the recovery flow,
    /// reach an authenticated session — so writing it to an Information-level log put a working
    /// account-takeover token into every log sink, retained for the life of the logs and readable
    /// by anyone with log access. Round 2's justification for keeping the log line ("it's the only
    /// record once the gRPC response has been read once") argues for re-issuing a fresh link on
    /// demand, not for retaining a live one in plaintext; the gRPC return path above is the
    /// supported way to get it, and a lost link is recoverable by inviting again.
    /// </summary>
    private async Task<string?> TriggerPasswordRecoveryAsync(HttpClient client, string userId)
    {
        using var recoveryResponse = await client.PostAsync(
            $"/api/v3/core/users/{userId}/recovery/",
            JsonBody(new { }));
        await EnsureSuccessWithBodyAsync(recoveryResponse, "create recovery link");

        await using var recoveryStream = await recoveryResponse.Content.ReadAsStreamAsync();
        using var recoveryDoc = await JsonDocument.ParseAsync(recoveryStream);

        if (recoveryDoc.RootElement.TryGetProperty("link", out var linkProp) &&
            linkProp.ValueKind == JsonValueKind.String)
        {
            var link = linkProp.GetString();
            logger.LogInformation(
                "[IdpAdminClient] recovery link created for new user {UserId} " +
                "(link returned to caller, not logged)",
                userId);
            return link;
        }
        else
        {
            // Not fatal: the user was already created successfully. But an admin has no other
            // way to learn about this short of reading Authentik's own logs/UI, so this needs
            // to be loud.
            logger.LogWarning(
                "[IdpAdminClient] recovery link response for new user {UserId} did not contain " +
                "a \"link\" string property; the user has no way to set a password until an " +
                "admin creates one manually in Authentik.",
                userId);
            return null;
        }
    }

    public async Task<IEnumerable<IdpUser>> ListUsersByTenantAsync(string tenantId)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        var matches = new List<IdpUser>();
        string? path = "/api/v3/core/users/";

        while (path is not null)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            foreach (var user in root.GetProperty("results").EnumerateArray())
            {
                var userTenantId =
                    user.TryGetProperty("attributes", out var attrs) &&
                    attrs.TryGetProperty("tenant_id", out var tid) &&
                    tid.ValueKind == JsonValueKind.String
                        ? tid.GetString()
                        : null;

                if (userTenantId != tenantId)
                    continue;

                matches.Add(new IdpUser(
                    ReadPk(user),
                    user.GetProperty("username").GetString()!,
                    user.GetProperty("email").GetString()!));
            }

            // Authentik's pagination envelope nests page metadata under "pagination"; a "next"
            // of 0 (not null, not absent) signals no further pages — verified against a live
            // instance, see class-level remarks. An envelope that does not match this shape is
            // not a "no more pages" signal: it means the response isn't what this client
            // expects, and silently stopping there would truncate the tenant's user list
            // without anyone noticing (the finding this replaces). So anything other than a
            // recognised "pagination.next" — missing "pagination", missing/non-numeric "next",
            // or a negative "next" — throws instead of ending the loop.
            if (!root.TryGetProperty("pagination", out var pagination))
                throw new InvalidOperationException(
                    "Authentik user-list response is missing the expected \"pagination\" envelope; " +
                    "refusing to silently truncate the tenant's user list.");

            if (!pagination.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException(
                    "Authentik pagination envelope's \"next\" field is missing or not a number; " +
                    "refusing to silently truncate the tenant's user list.");

            var nextPage = next.GetInt32();
            if (nextPage < 0)
                throw new InvalidOperationException(
                    $"Authentik pagination envelope's \"next\" field was negative ({nextPage}); " +
                    "refusing to silently truncate the tenant's user list.");

            path = nextPage > 0 ? $"/api/v3/core/users/?page={nextPage}" : null;
        }

        return matches;
    }

    public async Task DeactivateUserAsync(string userId)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        await PatchIsActiveAsync(client, userId, isActive: false);
    }

    public async Task DeactivateAllUsersInTenantAsync(string tenantId)
    {
        var users = await ListUsersByTenantAsync(tenantId);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        foreach (var user in users)
            await PatchIsActiveAsync(client, user.Id, isActive: false);
    }

    /// <summary>
    /// CSR round-3 finding #1 remediation. This used to POST
    /// <c>/api/v3/core/groups/{pk}/add_user/</c>, which Authentik gates on the GLOBAL
    /// <c>authentik_core.add_user_to_group</c> permission — and that permission means "add ANY
    /// user to ANY group". Authentik's <c>GroupViewSet.add_user</c> has no
    /// <c>enable_group_superuser</c> check (unlike its user serializer), so an orchestrator token
    /// holding it could add any account to <c>authentik Admins</c> and become superuser.
    /// Authentik 2026.5.3 cannot express a per-object grant of that permission in a blueprint
    /// (<c>RoleObjectPermission</c> is in the blueprint importer's <c>excluded_models()</c>), so
    /// the permission is dropped entirely and membership is written through the USER instead:
    /// <c>PATCH /api/v3/core/users/{id}/</c> with the new <c>groups</c> list. That path needs only
    /// <c>change_user</c>, which the blueprint object-scopes to users this service created, and
    /// Authentik's own <c>UserSerializer.validate_groups</c> independently refuses to add a member
    /// to a superuser group without <c>enable_group_superuser</c> (which this role does not hold).
    /// <para>
    /// Trade-off, accepted deliberately: <c>groups</c> is a whole-list write, so this is a
    /// read-modify-write where the old endpoint was atomic. The only caller is
    /// <c>TenantAdminGrpcService.SetTenantAdmin</c> — a rare, human-driven admin action on a
    /// single user — so the lost-update window is not worth holding a superuser-equivalent
    /// permission to close.
    /// </para>
    /// </summary>
    public async Task AddGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);
        await SetGroupMembershipAsync(client, userId, groupPk, shouldBeMember: true);
    }

    /// <inheritdoc cref="AddGroupAsync"/>
    public async Task RemoveGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);
        await SetGroupMembershipAsync(client, userId, groupPk, shouldBeMember: false);
    }

    /// <summary>
    /// Reads the user's current group list and PATCHes it back with <paramref name="groupPk"/>
    /// added or removed. See <see cref="AddGroupAsync"/> for why membership goes through the user
    /// rather than the group's own add_user/remove_user actions.
    /// <para>
    /// A user whose membership already matches the requested state is left alone rather than
    /// PATCHed with an identical list — this preserves the idempotence the group add_user/
    /// remove_user actions had (Django's <c>m2m.add</c>/<c>.remove</c> are both no-ops on a
    /// no-change call) without spending a write.
    /// </para>
    /// </summary>
    private static async Task SetGroupMembershipAsync(
        HttpClient client,
        string userId,
        string groupPk,
        bool shouldBeMember)
    {
        var operation = shouldBeMember ? "add user to group" : "remove user from group";

        using var readResponse = await client.GetAsync($"/api/v3/core/users/{userId}/");
        await EnsureSuccessWithBodyAsync(readResponse, operation);

        await using var stream = await readResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        // Refuse to guess. A missing/!array "groups" property would make the PATCH below
        // overwrite the user's real membership with a list reconstructed from nothing, silently
        // dropping every other group they belong to.
        if (!doc.RootElement.TryGetProperty("groups", out var groupsProp) ||
            groupsProp.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Authentik user {userId} response has no \"groups\" array; refusing to rewrite " +
                "group membership from an unrecognised payload.");
        }

        var groups = new List<string>();
        foreach (var group in groupsProp.EnumerateArray())
            groups.Add(group.ValueKind == JsonValueKind.Number ? group.GetRawText() : group.GetString()!);

        var isMember = groups.Contains(groupPk);
        if (isMember == shouldBeMember)
            return;

        if (shouldBeMember)
            groups.Add(groupPk);
        else
            groups.RemoveAll(group => group == groupPk);

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{userId}/")
        {
            Content = JsonBody(new { groups })
        };
        using var patchResponse = await client.SendAsync(request);
        await EnsureSuccessWithBodyAsync(patchResponse, operation);
    }

    private static async Task PatchIsActiveAsync(HttpClient client, string userId, bool isActive)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{userId}/")
        {
            Content = JsonBody(new { is_active = isActive })
        };
        using var response = await client.SendAsync(request);
        await EnsureSuccessWithBodyAsync(response, "patch is_active");
    }

    private static async Task<string> ResolveGroupPkAsync(HttpClient client, string groupName)
    {
        using var response = await client.GetAsync($"/api/v3/core/groups/?name={Uri.EscapeDataString(groupName)}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var results = doc.RootElement.GetProperty("results");

        if (results.GetArrayLength() == 0)
            throw new InvalidOperationException($"Authentik group '{groupName}' was not found.");

        return ReadPk(results[0]);
    }

    /// <summary>
    /// Serializes a request body to a length-delimited <see cref="StringContent"/>.
    /// <para>
    /// PostAsJsonAsync/JsonContent serialize lazily and so leave Content-Length unset, which makes
    /// HttpClient fall back to <c>Transfer-Encoding: chunked</c>. Authentik's ASGI server silently
    /// DISCARDS a chunked request body — DRF then sees an empty payload and rejects the call with
    /// "This field is required." for every required field, while the fields were in fact sent.
    /// Verified against the live instance: byte-identical JSON succeeds with Content-Length and
    /// fails chunked. Every request body in this class must therefore go through here.
    /// </para>
    /// </summary>
    private static StringContent JsonBody(object body) =>
        new(
            JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            System.Text.Encoding.UTF8,
            "application/json");

    /// <summary>
    /// EnsureSuccessStatusCode() discards the response body, and Authentik's DRF layer puts its
    /// per-field validation errors there — so a rejected request surfaced only as a bare
    /// "400 (Bad Request)" with nothing saying which field it disliked. Given this class's
    /// standing caveat that its JSON field names were never verified against a live instance,
    /// that body is the first thing anyone diagnosing a failure here needs.
    /// </summary>
    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Authentik {operation} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static string ReadPk(JsonElement element)
    {
        var pk = element.GetProperty("pk");
        return pk.ValueKind == JsonValueKind.Number
            ? pk.GetRawText()
            : pk.GetString()!;
    }
}
