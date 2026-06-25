using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class SchemaRegistryTests
{
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _sut;

    public SchemaRegistryTests()
    {
        _sql = Substitute.For<IPostgresRepository>();
        // ExecuteAsync is used for EnsureMetadataTableAsync and SQL operations — default to no-op
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _sut = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotRegistered()
    {
        var result = _sut.Get("NonExistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_StoresDescriptor()
    {
        var schema = SchemaFixtures.AuthorSchema();

        await _sut.RegisterAsync(schema);

        _sut.Get("Author").Should().NotBeNull();
        _sut.Get("Author")!.TypeName.Should().Be("Author");
    }

    [Fact]
    public void IsRegistered_ReturnsFalse_BeforeRegistration()
    {
        _sut.IsRegistered("Author").Should().BeFalse();
    }

    [Fact]
    public async Task IsRegistered_ReturnsTrue_AfterRegistration()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());

        _sut.IsRegistered("Author").Should().BeTrue();
    }

    [Fact]
    public async Task UnregisterAsync_RemovesSchema()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());

        await _sut.UnregisterAsync("Author");

        _sut.IsRegistered("Author").Should().BeFalse();
        _sut.Get("Author").Should().BeNull();
    }

}
