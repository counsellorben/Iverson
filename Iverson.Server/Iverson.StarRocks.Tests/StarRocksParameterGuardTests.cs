using Dapper;
using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The guard exists because StarRocks does NOT reject an unsubstituted placeholder — it reads
/// `@p0` as an unset user variable and returns zero rows. See
/// docs/runbooks/integration-test-flake-signatures.md, "Syntax error on '@'".
/// </summary>
public class StarRocksParameterGuardTests
{
    [Fact]
    public void EnsureAllPlaceholdersBound_WithEveryPlaceholderSupplied_DoesNotThrow()
    {
        var param = new DynamicParameters();
        param.Add("p0", "alice");
        param.Add("p1", 42);

        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "SELECT `Id` FROM `t` WHERE `Name` = @p0 AND `Age` > @p1", param);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAllPlaceholdersBound_WithAnUnboundPlaceholder_ThrowsNamingIt()
    {
        var param = new DynamicParameters();
        param.Add("p0", "alice");

        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "SELECT `Id` FROM `t` WHERE `Name` = @p0 AND `Age` > @p1", param);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*@p1*");
    }

    [Fact]
    public void EnsureAllPlaceholdersBound_WithNoParametersAtAll_ThrowsForAPlaceholderBearingStatement()
    {
        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "SELECT `Id` FROM `t` WHERE `Name` = @p0", null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*@p0*");
    }

    [Fact]
    public void EnsureAllPlaceholdersBound_WithAnonymousObjectParameters_MatchesByPropertyName()
    {
        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "DELETE FROM `t` WHERE `Id` = @key", new { key = "abc" });

        act.Should().NotThrow();
    }

    // ── the two real statements in this repo that legitimately contain '@' ──

    [Fact]
    public void EnsureAllPlaceholdersBound_WithASessionVariable_DoesNotTreatItAsAPlaceholder()
    {
        // `@@version_comment` is a session variable. A regex that merely looked for '@' followed
        // by word characters would flag "version_comment" and refuse a valid statement.
        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound("SELECT @@version_comment", null);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAllPlaceholdersBound_WithAGrantUserSpec_DoesNotTreatItAsAPlaceholder()
    {
        // EngagementRepository.EnsureTenantProvisionedAsync sends exactly this shape. The '@' is
        // followed by a quote, not an identifier character.
        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "GRANT `tenant_role` TO USER 'iverson_app'@'%'", null);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAllPlaceholdersBound_WithNoPlaceholdersAndNoParameters_DoesNotThrow()
    {
        var act = () => StarRocksParameterGuard.EnsureAllPlaceholdersBound(
            "CREATE DATABASE IF NOT EXISTS `iverson_t_abc`", null);

        act.Should().NotThrow();
    }
}
