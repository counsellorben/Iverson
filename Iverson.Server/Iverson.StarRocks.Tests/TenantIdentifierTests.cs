using FluentAssertions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class TenantIdentifierTests
{
    // ── IsValid — Valid Cases ──────────────────────────────────────────────────

    [Theory]
    [InlineData("a")]
    [InlineData("z")]
    [InlineData("A")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("abc123")]
    [InlineData("ABC_DEF-GHI")]
    [InlineData("tenant_123")]
    [InlineData("my-tenant-id")]
    public void IsValid_AcceptsValidIds(string tenantId)
    {
        TenantIdentifier.IsValid(tenantId).Should().BeTrue();
    }

    [Fact]
    public void IsValid_AcceptsMinimumLength_1()
    {
        TenantIdentifier.IsValid("a").Should().BeTrue();
    }

    [Fact]
    public void IsValid_AcceptsMaximumLength_52()
    {
        var id52 = new string('a', 52);
        TenantIdentifier.IsValid(id52).Should().BeTrue();
    }

    // ── IsValid — Invalid Cases ────────────────────────────────────────────────

    [Fact]
    public void IsValid_RejectsEmpty()
    {
        TenantIdentifier.IsValid("").Should().BeFalse();
    }

    [Fact]
    public void IsValid_RejectsTooLong_53chars()
    {
        var id53 = new string('a', 53);
        TenantIdentifier.IsValid(id53).Should().BeFalse();
    }

    [Fact]
    public void IsValid_RejectsBacktick()
    {
        TenantIdentifier.IsValid("tenant`id").Should().BeFalse();
    }

    [Fact]
    public void IsValid_RejectsSemicolon()
    {
        TenantIdentifier.IsValid("tenant;id").Should().BeFalse();
    }

    [Fact]
    public void IsValid_RejectsSpace()
    {
        TenantIdentifier.IsValid("tenant id").Should().BeFalse();
    }

    [Fact]
    public void IsValid_RejectsSqlCommentSequence()
    {
        TenantIdentifier.IsValid("tenant--id").Should().BeFalse();
    }

    [Theory]
    [InlineData("!")]
    [InlineData("@")]
    [InlineData("#")]
    [InlineData("$")]
    [InlineData("%")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("*")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("+")]
    [InlineData("=")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("|")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("?")]
    [InlineData(".")]
    [InlineData(",")]
    public void IsValid_RejectsSpecialCharacters(string tenantId)
    {
        TenantIdentifier.IsValid(tenantId).Should().BeFalse();
    }

    // ── DatabaseName ──────────────────────────────────────────────────────────

    [Fact]
    public void DatabaseName_ProducesIversonTenantPrefix()
    {
        TenantIdentifier.DatabaseName("acme").Should().Be("iverson_tenant_acme");
    }

    [Fact]
    public void DatabaseName_PreservesUnderscoresAndHyphens()
    {
        TenantIdentifier.DatabaseName("acme_corp-2").Should().Be("iverson_tenant_acme_corp-2");
    }

    [Theory]
    [InlineData("a", "iverson_tenant_a")]
    [InlineData("abc123", "iverson_tenant_abc123")]
    [InlineData("prod_db", "iverson_tenant_prod_db")]
    public void DatabaseName_Formatting(string tenantId, string expected)
    {
        TenantIdentifier.DatabaseName(tenantId).Should().Be(expected);
    }

    // ── RoleName ───────────────────────────────────────────────────────────────

    // These were RE-POINTED, not added. The originals asserted the tenant id verbatim — one of
    // them, RoleName_PreservesUnderscoresAndHyphens, asserted `role_tenant_acme_corp-2`, which is
    // the EXACT name StarRocks rejects with "invalid role format". The suite was green over a
    // defect that made Search, Aggregate, GroupBy and Pipeline impossible for every hyphenated
    // tenant, because the assertion and the defect agreed with each other.

    [Fact]
    public void RoleName_ProducesRoleTenantPrefix()
    {
        TenantIdentifier.RoleName("acme").Should().StartWith("role_tenant_acme_");
    }

    [Fact]
    public void RoleName_HyphenatedTenant_CarriesNoHyphen()
    {
        // The whole point. StarRocks rejects a hyphen in a role name unconditionally, even
        // back-quoted, so this must hold for every tenant id the validator admits.
        TenantIdentifier.RoleName("acme_corp-2").Should().NotContain("-");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abc123")]
    [InlineData("prod_db")]
    [InlineData("tenant-bypass")]
    [InlineData("iverson-loadtest-dynamic")]
    public void RoleName_IsAValidStarRocksRoleName(string tenantId)
    {
        var role = TenantIdentifier.RoleName(tenantId);

        role.Should().MatchRegex("^[A-Za-z0-9_]+$", "StarRocks role names admit nothing else");
        role.Length.Should().BeLessThanOrEqualTo(64,
            "65 is rejected with 'invalid role format' — binary-searched against a live 4.1.1");
        role.Should().StartWith("role_tenant_");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("tenant-bypass")]
    [InlineData("iverson-loadtest-dynamic")]
    public void RoleName_KeepsTheTenantIdGreppable(string tenantId)
    {
        // The readable half exists only so SHOW ROLES can be searched. Losing it would not be a
        // correctness failure, which is exactly why it needs a test of its own.
        TenantIdentifier.RoleName(tenantId)
            .Should().Contain(tenantId.Replace('-', '_'));
    }

    [Fact]
    public void RoleName_IsStableAcrossCalls() =>
        TenantIdentifier.RoleName("acme-corp").Should().Be(TenantIdentifier.RoleName("acme-corp"));

    [Fact]
    public void RoleName_DistinguishesIdsThatSanitizeIdentically()
    {
        // THE SECURITY PROPERTY. `acme-corp` and `acme_corp` are two different tenants, and the
        // readable half of the role name collapses them onto the same string. If the whole name
        // collapsed too they would share a StarRocks role, and each could read the other's
        // database. The fingerprint is what stops that, and this is the test that says so.
        TenantIdentifier.RoleName("acme-corp").Should().NotBe(TenantIdentifier.RoleName("acme_corp"));
    }

    [Fact]
    public void RoleName_IsInjectiveOverAGeneratedCorpus()
    {
        // Every id the validator admits, generated over the characters that actually interact:
        // the two that sanitize onto each other, plus enough alphanumerics to vary length. A
        // single collision here is a cross-tenant read.
        var alphabet = new[] { "a", "b", "9", "_", "-" };
        var ids = new List<string>();

        foreach (var c1 in alphabet)
        foreach (var c2 in alphabet)
        foreach (var c3 in alphabet)
        foreach (var c4 in alphabet)
        {
            var id = $"t{c1}{c2}{c3}{c4}";
            if (TenantIdentifier.IsValid(id))
                ids.Add(id);
        }

        ids.Should().HaveCountGreaterThan(500, "the corpus must be big enough to be worth running");
        ids.Select(TenantIdentifier.RoleName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void RoleName_LongestPermittedTenantId_StillFits()
    {
        // 52 characters is the validator's ceiling. Alternating so it cannot contain "--".
        var longest = string.Concat(Enumerable.Repeat("a-", 26))[..52].TrimEnd('-');
        TenantIdentifier.IsValid(longest).Should().BeTrue("the corpus must exercise a REAL id");

        TenantIdentifier.RoleName(longest).Length.Should().BeLessThanOrEqualTo(64);
    }

    // ── Qualify ───────────────────────────────────────────────────────────────

    [Fact]
    public void Qualify_WithNullTenantDatabase_ReturnsBacktickQuotedTableName()
    {
        TenantIdentifier.Qualify(null, "articles").Should().Be("`articles`");
    }

    [Fact]
    public void Qualify_WithNullTenantDatabase_EscapesSpecialChars()
    {
        TenantIdentifier.Qualify(null, "user_articles").Should().Be("`user_articles`");
    }

    [Fact]
    public void Qualify_WithTenantDatabase_ReturnsQualifiedName()
    {
        TenantIdentifier.Qualify("iverson_tenant_acme", "articles")
            .Should().Be("`iverson_tenant_acme`.`articles`");
    }

    [Theory]
    [InlineData("db", "table", "`db`.`table`")]
    [InlineData("tenant_db", "user_table", "`tenant_db`.`user_table`")]
    [InlineData("prod-db", "items", "`prod-db`.`items`")]
    public void Qualify_WithDatabaseName_Formatting(string db, string table, string expected)
    {
        TenantIdentifier.Qualify(db, table).Should().Be(expected);
    }

    [Fact]
    public void Qualify_WithEmptyStringTenantDatabase_TreatsAsNonNull()
    {
        // Empty string is not null, so it should be treated as a database name
        TenantIdentifier.Qualify("", "articles").Should().Be("``.`articles`");
    }
}
