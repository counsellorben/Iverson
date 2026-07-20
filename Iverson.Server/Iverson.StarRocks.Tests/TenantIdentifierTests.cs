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

    [Fact]
    public void RoleName_ProducesRoleTenantPrefix()
    {
        TenantIdentifier.RoleName("acme").Should().Be("role_tenant_acme");
    }

    [Fact]
    public void RoleName_PreservesUnderscoresAndHyphens()
    {
        TenantIdentifier.RoleName("acme_corp-2").Should().Be("role_tenant_acme_corp-2");
    }

    [Theory]
    [InlineData("a", "role_tenant_a")]
    [InlineData("abc123", "role_tenant_abc123")]
    [InlineData("prod_db", "role_tenant_prod_db")]
    public void RoleName_Formatting(string tenantId, string expected)
    {
        TenantIdentifier.RoleName(tenantId).Should().Be(expected);
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
