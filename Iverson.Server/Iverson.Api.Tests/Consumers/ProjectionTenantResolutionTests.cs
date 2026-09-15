using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class ProjectionTenantResolutionTests
{
    private const string Key = "11111111-1111-1111-1111-111111111111";

    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();

    // TenantColumn = "TenantId" (SchemaFixtures.cs:55-74).
    private readonly SchemaDescriptor _schema = SchemaFixtures.ArticleSchema();

    private void RowIs(string? rowJson) =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns(rowJson);

    private Task<AuthoritativeRow?> Fetch() =>
        ProjectionTenantResolution.FetchAuthoritativeRowAsync(_entities, _schema, Key, "[Test]");

    private static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── FetchAuthoritativeRowAsync ───────────────────────────────────────────

    [Fact]
    public async Task FetchAuthoritativeRow_RowGone_ReturnsNull()
    {
        RowIs(null);

        (await Fetch()).Should().BeNull();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_RowWithTenant_ReturnsTenantAndARowReadableAfterTheCall()
    {
        RowIs("""{"Title":"T","TenantId":"tenant-a"}""");

        var row = await Fetch();

        row.Should().NotBeNull();
        row!.TenantId.Should().Be("tenant-a");
        row.ReadString("Title").Should().Be("T");
    }

    [Fact]
    public async Task FetchAuthoritativeRow_TenantJsonNull_ThrowsPoison()
    {
        RowIs("""{"Title":"T","TenantId":null}""");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_TenantKeyAbsent_ThrowsPoison()
    {
        RowIs("""{"Title":"T"}""");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_CamelCaseOnlyTenantKey_Resolves()
    {
        RowIs("""{"tenantId":"tenant-a"}""");

        (await Fetch())!.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task FetchAuthoritativeRow_MalformedRowJson_ThrowsPoison()
    {
        RowIs("NOT_VALID_JSON{{{");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_RepositoryThrows_PropagatesUnwrapped()
    {
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Throws(new InvalidOperationException("connection reset"));

        await ((Func<Task>)Fetch).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_ReadsCrossTenant()
    {
        RowIs("""{"TenantId":"tenant-a"}""");

        await Fetch();

        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), Key, EntityAccess.CrossTenantMaintenance);
    }

    // ── TenantFromSnapshot ───────────────────────────────────────────────────

    [Fact]
    public void TenantFromSnapshot_TenantPresent_ReturnsIt()
    {
        ProjectionTenantResolution.TenantFromSnapshot("""{"TenantId":"tenant-a"}""", _schema, Key, "[Test]")
            .Should().Be("tenant-a");
    }

    [Fact]
    public void TenantFromSnapshot_TenantKeyAbsent_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("""{"Title":"T"}""", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void TenantFromSnapshot_TenantJsonNull_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("""{"TenantId":null}""", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void TenantFromSnapshot_MalformedJson_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("NOT_VALID_JSON{{{", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    // ── ReadString ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadString_ExactKeyWinsOverCamelCase()
    {
        ProjectionTenantResolution.ReadString(Element("""{"TenantId":"exact","tenantId":"camel"}"""), "TenantId")
            .Should().Be("exact");
    }

    [Fact]
    public void ReadString_JsonNull_ReturnsNull()
    {
        ProjectionTenantResolution.ReadString(Element("""{"TenantId":null}"""), "TenantId")
            .Should().BeNull();
    }

    [Fact]
    public void ReadString_Number_ReturnsRawText()
    {
        ProjectionTenantResolution.ReadString(Element("""{"Count":42}"""), "Count")
            .Should().Be("42");
    }
}
