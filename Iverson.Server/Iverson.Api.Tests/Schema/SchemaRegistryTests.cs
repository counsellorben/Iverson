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
    private readonly ISchemaRegistryRepository _repository;
    private readonly SchemaRegistry _sut;

    public SchemaRegistryTests()
    {
        _repository = Substitute.For<ISchemaRegistryRepository>();
        _sut = new SchemaRegistry(_repository, NullLogger<SchemaRegistry>.Instance);
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

    [Fact]
    public async Task LoadAsync_RemovesSchemasNoLongerInPostgres()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _sut.RegisterAsync(SchemaFixtures.ArticleSchema());
        _sut.IsRegistered("Author").Should().BeTrue();
        _sut.IsRegistered("Article").Should().BeTrue();

        // Simulate "Article" having been unregistered by a different process: the next
        // LoadAsync's query only returns "Author" (matching what UnregisterAsync's DELETE
        // would leave behind in Postgres), even though this instance's in-memory copy still
        // has "Article" from the RegisterAsync call above.
        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)>
            {
                ("Author", System.Text.Json.JsonSerializer.Serialize(
                    SchemaFixtures.AuthorSchema(),
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }))
            });

        await _sut.LoadAsync();

        _sut.IsRegistered("Author").Should().BeTrue();
        _sut.IsRegistered("Article").Should().BeFalse();
    }

}
