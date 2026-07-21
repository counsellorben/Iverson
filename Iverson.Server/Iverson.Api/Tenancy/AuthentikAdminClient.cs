using System.Net.Http.Json;
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
public sealed class AuthentikAdminClient(IHttpClientFactory httpClientFactory) : IAuthentikAdminClient
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

        using var createResponse = await client.PostAsJsonAsync("/api/v3/core/users/", createBody);
        createResponse.EnsureSuccessStatusCode();

        await using var createdStream = await createResponse.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var userId = ReadPk(createdDoc.RootElement);

        using var setPasswordResponse = await client.PostAsJsonAsync(
            $"/api/v3/core/users/{userId}/set_password/",
            new { password });
        setPasswordResponse.EnsureSuccessStatusCode();

        return userId;
    }

    public async Task<IEnumerable<AuthentikUser>> ListUsersByTenantAsync(string tenantId)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        var matches = new List<AuthentikUser>();
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

                matches.Add(new AuthentikUser(
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
        using var response = await client.PostAsJsonAsync(
            $"/api/v3/core/groups/{groupPk}/add_user/",
            new { pk = UserPkJsonValue(userId) });
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveGroupAsync(string userId, string groupName)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var groupPk = await ResolveGroupPkAsync(client, groupName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v3/core/groups/{groupPk}/remove_user/",
            new { pk = UserPkJsonValue(userId) });
        response.EnsureSuccessStatusCode();
    }

    private static async Task PatchIsActiveAsync(HttpClient client, string userId, bool isActive)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v3/core/users/{userId}/")
        {
            Content = JsonContent.Create(new { is_active = isActive })
        };
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
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

    private static string ReadPk(JsonElement element)
    {
        var pk = element.GetProperty("pk");
        return pk.ValueKind == JsonValueKind.Number
            ? pk.GetRawText()
            : pk.GetString()!;
    }
}
