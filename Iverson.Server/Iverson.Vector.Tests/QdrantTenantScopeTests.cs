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

    private static IntelligenceTenantScope CreateScope() => new(ApiKey);

    // ── ResolveCollectionName ────────────────────────────────────────────────

    // Fingerprints below are the low 128 bits of SHA-256 over the literal tenant/sentinel text,
    // rendered as 25 base-36 digits — the exact construction IntelligenceTenantScope.Fingerprint
    // implements (mirroring Iverson.StarRocks.TenantIdentifier.Fingerprint). Computed independently
    // in Python against the documented algorithm, not read back out of the SUT.
    private const string Tenant42Fingerprint = "emo5tahabce26iljnao1rps5c";
    private const string NoTenantFingerprint = "74zc32639tuhty7qvmca0gtmn";

    [Fact]
    public void ResolveCollectionName_MainCollection_WithTenant_AppendsSanitizedTenantAndFingerprint()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", "tenant-42", isChunks: false);

        // "-" is not carried into the tenant segment (folded to "_" by Sanitize); the fingerprint
        // is what actually disambiguates, not this readable half.
        name.Should().Be($"players_tenant_42_{Tenant42Fingerprint}");
    }

    [Fact]
    public void ResolveCollectionName_ChunksCollection_WithTenant_AppendsChunksSanitizedTenantAndFingerprint()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", "tenant-42", isChunks: true);

        name.Should().Be($"players_chunks_tenant_42_{Tenant42Fingerprint}");
    }

    [Fact]
    public void ResolveCollectionName_MainCollection_NullTenant_UsesSanitizedSentinelAndFingerprint()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", null, isChunks: false);

        // The sentinel's own hyphens fold to "_" under Sanitize, same as any other tenant id would.
        name.Should().Be($"players___no_tenant_claim___{NoTenantFingerprint}");
    }

    [Fact]
    public void ResolveCollectionName_ChunksCollection_NullTenant_UsesSanitizedSentinelAndFingerprint()
    {
        var scope = CreateScope();

        var name = scope.ResolveCollectionName("players", null, isChunks: true);

        name.Should().Be($"players_chunks___no_tenant_claim___{NoTenantFingerprint}");
    }

    [Fact]
    public void ResolveCollectionName_DifferentTenantIds_NeverCollideEvenAcrossIsChunks()
    {
        var scope = CreateScope();

        // Before the fix: ResolveCollectionName("players", "chunks_a", isChunks: false) and
        // ResolveCollectionName("players", "a", isChunks: true) were BOTH "players_chunks_a" —
        // tenant "chunks_a"'s object collection aliased tenant "a"'s chunks collection, byte for
        // byte. This is the regression test for finding #9: the fingerprint (over the ORIGINAL,
        // unsanitized tenant id) must disambiguate them even though the readable text collides.
        var aliasingObjectCollection = scope.ResolveCollectionName("players", "chunks_a", isChunks: false);
        var aliasingChunksCollection = scope.ResolveCollectionName("players", "a", isChunks: true);

        aliasingObjectCollection.Should().NotBe(aliasingChunksCollection);

        // Confirms the readable halves really would have collided pre-fix (so this test would have
        // caught the original bug, not just exercised an unrelated code path).
        aliasingObjectCollection.Should().StartWith("players_chunks_a_");
        aliasingChunksCollection.Should().StartWith("players_chunks_a_");
    }

    [Theory]
    [InlineData("tenant-a", "tenant-b")]
    [InlineData("chunks_a", "a")]
    [InlineData("a_b", "a-b")]
    public void ResolveCollectionName_DistinctTenantIds_ProduceDistinctCollectionNames(string tenantA, string tenantB)
    {
        var scope = CreateScope();

        var nameA = scope.ResolveCollectionName("players", tenantA, isChunks: false);
        var nameB = scope.ResolveCollectionName("players", tenantB, isChunks: false);

        nameA.Should().NotBe(nameB);
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
