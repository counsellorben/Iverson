using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class QdrantTenantScopeTests
{
    // HS256 requires a key of at least 256 bits (32 bytes); pad well past that minimum.
    private const string ApiKey = "test-signing-key-at-least-32-bytes-long";

    private static QdrantTenantScope CreateScope() => new(ApiKey);

    // ── ResolveCollectionName ────────────────────────────────────────────────

    [Fact]
    public void ResolveCollectionName_MainCollection_WithTenant_AppendsTenantId()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", "tenant-42", isChunks: false);

        name.Should().Be("players_tenant-42");
    }

    [Fact]
    public void ResolveCollectionName_ChunksCollection_WithTenant_AppendsChunksAndTenantId()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", "tenant-42", isChunks: true);

        name.Should().Be("players_chunks_tenant-42");
    }

    [Fact]
    public void ResolveCollectionName_MainCollection_NullTenant_UsesSentinel()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", null, isChunks: false);

        // The "_" separator in the format string plus the sentinel's own leading "__" yields
        // three underscores between the base name and the sentinel text.
        name.Should().Be("players___no-tenant-claim__");
    }

    [Fact]
    public void ResolveCollectionName_ChunksCollection_NullTenant_UsesSentinel()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", null, isChunks: true);

        name.Should().Be("players_chunks___no-tenant-claim__");
    }

    // ── MintScopedApiKey ─────────────────────────────────────────────────────

    [Fact]
    public void MintScopedApiKey_ExpClaim_IsApproximatelyThirtySecondsInFuture()
    {
        var scope = CreateScope();
        var before = DateTimeOffset.UtcNow;

        var token = scope.MintScopedApiKey("players_tenant-42", readOnly: false);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var expClaim = jwt.Payload["exp"];
        var exp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(expClaim));

        exp.Should().BeCloseTo(before.AddSeconds(30), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MintScopedApiKey_ReadWrite_AccessClaim_HasSingleEntryWithRwAccess()
    {
        var scope = CreateScope();

        var token = scope.MintScopedApiKey("players_tenant-42", readOnly: false);

        var jwt = new JwtSecurityToken(token);
        var access = Assert.IsType<JsonElement>(jwt.Payload["access"]);

        access.ValueKind.Should().Be(JsonValueKind.Array);
        access.GetArrayLength().Should().Be(1);
        var entry = access[0];
        entry.GetProperty("collection").GetString().Should().Be("players_tenant-42");
        entry.GetProperty("access").GetString().Should().Be("rw");
    }

    [Fact]
    public void MintScopedApiKey_ReadOnly_AccessClaim_HasSingleEntryWithRAccess()
    {
        var scope = CreateScope();

        var token = scope.MintScopedApiKey("players_tenant-42", readOnly: true);

        var jwt = new JwtSecurityToken(token);
        var access = Assert.IsType<JsonElement>(jwt.Payload["access"]);

        access.ValueKind.Should().Be(JsonValueKind.Array);
        access.GetArrayLength().Should().Be(1);
        var entry = access[0];
        entry.GetProperty("collection").GetString().Should().Be("players_tenant-42");
        entry.GetProperty("access").GetString().Should().Be("r");
    }

    [Fact]
    public void MintScopedApiKey_TokenSignature_ValidatesAgainstSameKey()
    {
        var scope = CreateScope();
        var token = scope.MintScopedApiKey("players_tenant-42", readOnly: false);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiKey)),
            ValidateIssuerSigningKey = true,
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void MintScopedApiKey_TokenSignature_FailsValidationWithWrongKey()
    {
        var scope = CreateScope();
        var token = scope.MintScopedApiKey("players_tenant-42", readOnly: false);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-key")),
            ValidateIssuerSigningKey = true,
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out _);

        act.Should().Throw<SecurityTokenSignatureKeyNotFoundException>();
    }
}
