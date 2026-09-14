using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Iverson.Vector;

public sealed class IntelligenceTenantScope(string apiKey)
{
    private const string NoTenantSentinel = "__no-tenant-claim__";

    /// <summary>Base-36 digits needed for 128 bits: ceil(128 / log2(36)) = 25 — the same constant,
    /// for the same reason, as <c>Iverson.StarRocks.TenantIdentifier.HashDigits</c>.</summary>
    private const int HashDigits = 25;

    /// <summary>
    /// The physical Qdrant collection for a (base, tenant, isChunks) triple:
    /// <c>{baseName}{suffix}_{sanitizedTenant}_{fingerprint}</c>.
    ///
    /// <para><b>Why the old <c>{baseName}{suffix}_{tenantId}</c> shape was unsafe.</b> Plain
    /// concatenation with "_" is ambiguous whenever the tenant id itself contains "_": tenant
    /// <c>"chunks_a"</c>'s object collection (<c>"{base}_chunks_a"</c>) was byte-identical to
    /// tenant <c>"a"</c>'s chunks collection (<c>"{base}_chunks_a"</c>) — a cross-tenant aliasing
    /// in the one isolation layer that is purely name-based. Tenant ids are validated elsewhere
    /// (<c>Iverson.StarRocks.TenantIdentifier.IsValid</c>, pattern <c>[A-Za-z0-9_-]{1,52}</c>) but
    /// that pattern permits "_", so no choice of separator removes the ambiguity by itself — the
    /// tenant segment has to be collision-safe on its own.</para>
    ///
    /// <para><b>Same construction as <c>Iverson.StarRocks.TenantIdentifier.RoleName</c></b>
    /// (deliberately reused rather than inventing a second scheme): a lossy, greppable "readable"
    /// half (every character a physical name shouldn't rely on for identity folded to <c>_</c>)
    /// plus the low 128 bits of SHA-256 over the ORIGINAL (unsanitized) tenant id, rendered as 25
    /// base-36 digits. The hash is always over the original id, never the sanitized one — sanitizing
    /// is exactly what collapses <c>"chunks_a"</c> and <c>"a"</c>-with-a-"chunks_"-prefix onto
    /// overlapping text, so undoing that collapse requires hashing the pre-collapse string. This
    /// makes a collision unreachable even for an attacker-chosen tenant id. Unlike <c>RoleName</c>,
    /// this does not need to budget/truncate the readable half: Qdrant collection names carry no
    /// StarRocks-style 64-character cap.</para>
    /// </summary>
    public string ResolveCollectionName(string baseName, string? tenantId, bool isChunks)
    {
        var suffix = isChunks ? "_chunks" : "";
        var qualifier = tenantId ?? NoTenantSentinel;
        return $"{baseName}{suffix}_{Sanitize(qualifier)}_{Fingerprint(qualifier)}";
    }

    /// <summary>Every character the tenant segment shouldn't rely on for identity becomes <c>_</c>.
    /// Lossy and deliberately so — <see cref="Fingerprint"/> is what keeps distinct tenants
    /// distinct; this exists only so the physical collection name stays greppable back to its
    /// tenant. Mirrors <c>Iverson.StarRocks.TenantIdentifier.Sanitize</c>.</summary>
    private static string Sanitize(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>The low 128 bits of SHA-256 over the ORIGINAL value, as exactly
    /// <see cref="HashDigits"/> base-36 digits. Byte-for-byte the same construction as
    /// <c>Iverson.StarRocks.TenantIdentifier.Fingerprint</c> (duplicated rather than referenced:
    /// <c>Iverson.Vector</c> does not, and should not, take a project reference on
    /// <c>Iverson.StarRocks</c> just for this).</summary>
    private static string Fingerprint(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        UInt128 accumulator = 0;
        for (var i = 0; i < 16; i++)
            accumulator = (accumulator << 8) | digest[i];

        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var buffer = new char[HashDigits];
        for (var i = HashDigits - 1; i >= 0; i--)
        {
            buffer[i] = Digits[(int)(accumulator % 36)];
            accumulator /= 36;
        }

        return new string(buffer);
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
