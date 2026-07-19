using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Iverson.Vector;

public sealed class QdrantTenantScope(string apiKey)
{
    private const string NoTenantSentinel = "__no-tenant-claim__";

    public string ResolveCollectionName(string baseName, string? tenantId, bool isChunks)
    {
        var suffix = isChunks ? "_chunks" : "";
        var qualifier = tenantId ?? NoTenantSentinel;
        return $"{baseName}{suffix}_{qualifier}";
    }

    public string MintScopedApiKey(string collectionName, bool readOnly)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apiKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var payload = new JwtPayload
        {
            ["exp"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
            ["access"] = new[]
            {
                new Dictionary<string, string> { ["collection"] = collectionName, ["access"] = readOnly ? "r" : "rw" }
            }
        };
        var header = new JwtHeader(credentials);
        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
