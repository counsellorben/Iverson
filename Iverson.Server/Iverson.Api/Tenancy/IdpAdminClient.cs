using System.Text.Json;

namespace Iverson.Api.Tenancy;

/// <summary>
/// Wraps Authentik's REST Core API (https://docs.goauthentik.io/developer-docs/api/) so the
/// rest of the codebase never talks HTTP/JSON to the IdP directly. Follows the same
/// IHttpClientFactory + named-client convention as Iverson.Embeddings.EmbeddingService.
///
/// CAVEAT (carried over from design/plan review): the exact JSON field names used below
/// (attributes, groups, set_password, is_active, the group/user pagination envelope shape,
/// and the group add_user/remove_user endpoints) are grounded in Authentik's documented DRF
/// conventions and public API docs, but have NOT been verified against a live instance or the
/// /api/v3/schema/ OpenAPI document. Re-verify against a running Authentik before production use.
/// </summary>
public sealed class IdpAdminClient(IHttpClientFactory httpClientFactory) : IIdpAdminClient
{
    public const string HttpClientName = "iverson.authentik";

    public async Task<string> CreateUserAsync(
        string username,
        string email,
        string password,
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

        using var setPasswordResponse = await client.PostAsync(
            $"/api/v3/core/users/{userId}/set_password/",
            JsonBody(new { password }));
        await EnsureSuccessWithBodyAsync(setPasswordResponse, "set password");

        return userId;
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
            // of 0 (not null) signals no further pages. Not verified against a live instance —
            // see class-level remarks.
            path = root.TryGetProperty("pagination", out var pagination) &&
                   pagination.TryGetProperty("next", out var next) &&
                   next.ValueKind == JsonValueKind.Number &&
                   next.GetInt32() > 0
                ? $"/api/v3/core/users/?page={next.GetInt32()}"
                : null;
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
