using System.Globalization;
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
/// production use. (The <c>attributes</c> filter, the per-pk read and the group add_user/
/// remove_user actions — see <see cref="ListUsersByTenantAsync"/>, <see cref="GetUserAsync"/> and
/// <see cref="AddGroupAsync"/>, CSR round 12 §2.1 and §2.4 — ARE exercised against a live
/// Authentik by Iverson.Api.Tests' AuthentikRecoveryFlowIntegrationTests.)
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
public sealed class IdpAdminClient(IHttpClientFactory httpClientFactory, ILogger<IdpAdminClient> logger, IConfiguration configuration) : IIdpAdminClient
{
    public const string HttpClientName = "iverson.authentik";

    // CSR round-10 #16: an invite link is a bearer credential until first use; 15 minutes, not the
    // tenant default of 30.
    internal const string RecoveryLinkDuration = "minutes=15";

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
            JsonBody(new { token_duration = RecoveryLinkDuration }));
        await EnsureSuccessWithBodyAsync(recoveryResponse, "create recovery link");

        await using var recoveryStream = await recoveryResponse.Content.ReadAsStreamAsync();
        using var recoveryDoc = await JsonDocument.ParseAsync(recoveryStream);

        if (recoveryDoc.RootElement.TryGetProperty("link", out var linkProp) &&
            linkProp.ValueKind == JsonValueKind.String)
        {
            var link = PublicLink(linkProp.GetString()!);
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

    /// <summary>
    /// The tenant's users (CSR round 12 §2.1). Every page request carries the
    /// <c>attributes</c> filter <c>{"tenant_id": tenantId}</c>, the first one included, so Authentik
    /// reads only the tenant's rows: the next page is a fresh query, and one built without the
    /// filter would page through the whole directory. Each returned user's own <c>tenant_id</c>
    /// is still checked here, because Authentik answers with the unfiltered list when filtering
    /// raises a <c>ValueError</c>.
    /// </summary>
    public async Task<IEnumerable<IdpUser>> ListUsersByTenantAsync(string tenantId)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        var filter = Uri.EscapeDataString(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["tenant_id"] = tenantId }));
        var matches = new List<IdpUser>();
        string? path = $"/api/v3/core/users/?attributes={filter}";

        while (path is not null)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            foreach (var user in root.GetProperty("results").EnumerateArray())
            {
                if (TenantIdOf(user) != tenantId)
                    continue;

                matches.Add(new IdpUser(
                    ReadPk(user),
                    user.GetProperty("username").GetString()!,
                    user.GetProperty("email").GetString()!,
                    user.GetProperty("uid").GetString()!));
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

            path = nextPage > 0 ? $"/api/v3/core/users/?attributes={filter}&page={nextPage}" : null;
        }

        return matches;
    }

    /// <summary>
    /// One user by Authentik pk (CSR round 12 §2.1), for RemoveUser's and SetTenantAdmin's tenant
    /// check, which used to list the whole directory. Null when Authentik answers 404, and null
    /// when the user it returns carries another pk than the one asked for: either way there is no
    /// record of the requested user to check.
    /// </summary>
    public async Task<IdpUserRecord?> GetUserAsync(string userId)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync($"/api/v3/core/users/{userId}/");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessWithBodyAsync(response, "read user");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var user = doc.RootElement;

        var pk = ReadPk(user);
        if (pk != userId)
            return null;

        return new IdpUserRecord(
            pk,
            user.GetProperty("username").GetString()!,
            user.GetProperty("email").GetString()!,
            user.GetProperty("uid").GetString()!,
            user.GetProperty("is_active").GetBoolean(),
            [.. user.GetProperty("groups").EnumerateArray().Select(group => group.GetString()!)],
            TenantIdOf(user));
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
    /// CSR round 12 §2.4: membership is written through the group's own actions,
    /// <c>POST /api/v3/core/groups/{pk}/add_user/</c> and <c>/remove_user/</c> with the body
    /// <c>{"pk": user}</c>. These honour the object-level <c>add_user_to_group</c> and
    /// <c>remove_user_from_group</c> permissions, which the orchestrator role holds on the
    /// <c>tenant-admins</c> group only (the blueprint entry's <c>permissions:</c> field), and
    /// Authentik 2026.8 refuses add_user for a superuser group. The user PATCH of <c>groups</c> this
    /// replaces checked no permission on the group, so it could put a user the orchestrator
    /// created into any non-superuser group. Both actions are idempotent. The caller passes a pk it
    /// has already checked against its own tenant (<c>TenantAdminGrpcService</c>).
    /// </summary>
    public async Task AddGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);
        await PostGroupMembershipAsync(client, groupPk, "add_user", userId, "add user to group");
    }

    /// <inheritdoc cref="AddGroupAsync"/>
    public async Task RemoveGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);
        await PostGroupMembershipAsync(client, groupPk, "remove_user", userId, "remove user from group");
    }

    private static async Task PostGroupMembershipAsync(
        HttpClient client,
        string groupPk,
        string action,
        string userId,
        string operation)
    {
        using var response = await client.PostAsync(
            $"/api/v3/core/groups/{groupPk}/{action}/",
            JsonBody(new { pk = int.Parse(userId, NumberStyles.None, CultureInfo.InvariantCulture) }));
        await EnsureSuccessWithBodyAsync(response, operation);
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
        using var response = await client.GetAsync($"/api/v3/core/groups/?name={Uri.EscapeDataString(groupName)}&include_users=false");
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

    // Authentik builds the link from the host this client called, which is the in-cluster address an
    // invited user cannot open. Authentik:PublicBaseUrl is the host they can; without it (Development,
    // tests) the link is returned as Authentik built it. A link that is not an absolute http(s) URL
    // is also returned as is: the user already exists, so a throw here would lose the link.
    // Only the leading scheme://authority is replaced; path, query and fragment are kept byte for
    // byte, because re-serialising through Uri can re-normalise percent-escapes in a bearer token.
    private string PublicLink(string link)
    {
        if (configuration["Authentik:PublicBaseUrl"] is not { Length: > 0 } publicBaseUrl)
            return link;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return link;
        var publicBase = new Uri(publicBaseUrl);
        // Uri also accepts forms without a literal "://" (backslashes, for one); splicing those by
        // offset would be wrong, so they are returned as Authentik built them.
        var separator = link.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
            return link;
        var pathStart = link.IndexOfAny(['/', '?', '#'], separator + 3);
        return publicBase.GetLeftPart(UriPartial.Authority) + (pathStart < 0 ? "/" : link[pathStart..]);
    }

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

    // A user's attributes.tenant_id, or null when it has none or it is not a string.
    private static string? TenantIdOf(JsonElement user) =>
        user.TryGetProperty("attributes", out var attrs) &&
        attrs.TryGetProperty("tenant_id", out var tid) &&
        tid.ValueKind == JsonValueKind.String
            ? tid.GetString()
            : null;
}
