using System.Text.Json;

namespace Iverson.Api.Tenancy;

/// <summary>
/// Wraps Authentik's REST Core API (https://docs.goauthentik.io/developer-docs/api/) so the
/// rest of the codebase never talks HTTP/JSON to the IdP directly. Follows the same
/// IHttpClientFactory + named-client convention as Iverson.Embeddings.EmbeddingService.
///
/// CAVEAT (carried over from design/plan review): the exact JSON field names used below
/// (attributes, groups, is_active, and the group add_user/remove_user endpoints) are
/// grounded in Authentik's documented DRF conventions and public API docs, but have NOT
/// been verified against a live instance or the /api/v3/schema/ OpenAPI document.
/// Re-verify against a running Authentik before production use.
///
/// CSR finding #4 remediation: CreateUserAsync no longer posts a caller-supplied password to
/// Authentik's set_password endpoint. Instead it POSTs /api/v3/core/users/{id}/recovery/,
/// which Authentik's docs describe as returning a one-time recovery link
/// (<c>{"link": "..."}</c>) the user follows to set their own password directly against
/// Authentik. That specific endpoint shape is, like the set_password endpoint it replaces,
/// UNVERIFIED against a live instance or the OpenAPI schema — re-verify it too before
/// production use.
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

    public async Task<string> CreateUserAsync(
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

        await TriggerPasswordRecoveryAsync(client, userId);

        return userId;
    }

    /// <summary>
    /// CSR finding #4 remediation: the platform must never transmit a user's password. Rather
    /// than POSTing one to Authentik's set_password endpoint, this triggers Authentik's own
    /// recovery flow — POST /api/v3/core/users/{id}/recovery/ — which Authentik's docs describe
    /// as minting a one-time link the user follows to set their own password directly against
    /// Authentik, never through this platform. See class remarks: this endpoint's shape is
    /// unverified against a live instance.
    ///
    /// The link is surfaced via a log line, not the gRPC response: neither InviteUser's
    /// TenantUser nor CreateTenant's Tenant response message (tenant_admin.proto /
    /// tenant_lifecycle.proto) has a field for it, and adding one ripples into all five SDKs —
    /// out of scope for this change and tracked as a follow-up. Until that lands, an inviting
    /// admin retrieves the link from server logs.
    /// </summary>
    private async Task TriggerPasswordRecoveryAsync(HttpClient client, string userId)
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
            logger.LogInformation(
                "[IdpAdminClient] recovery link created for new user {UserId}: {RecoveryLink}",
                userId, linkProp.GetString());
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

    public async Task AddGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);

        // NOTE: authentik_core.group's add_user/remove_user actions are this class's own
        // extrapolation from general Authentik API conventions (mirroring the Django-admin-style
        // bulk membership actions Authentik exposes) — not explicitly named in the task brief and
        // not verified against a live instance or OpenAPI schema.
        using var response = await client.PostAsync(
            $"/api/v3/core/groups/{groupPk}/add_user/",
            JsonBody(new { pk = UserPkJsonValue(userId) }));
        await EnsureSuccessWithBodyAsync(response, "add user to group");
    }

    public async Task RemoveGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);

        using var response = await client.PostAsync(
            $"/api/v3/core/groups/{groupPk}/remove_user/",
            JsonBody(new { pk = UserPkJsonValue(userId) }));
        await EnsureSuccessWithBodyAsync(response, "remove user from group");
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

    // Authentik user pks are integers; group pks are UUIDs. Both are carried through this class
    // as opaque strings (matching IAuthentikAdminClient's string-typed ids), so when a user pk
    // needs to go back into a request body we re-emit it as a JSON number if it parses as one,
    // to match the integer type Authentik's user model actually uses.
    private static object UserPkJsonValue(string userId) =>
        int.TryParse(userId, out var numeric) ? numeric : userId;

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
