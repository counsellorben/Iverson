using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class PopularitySignalOptionsTests
{
    // Minimal SchemaDescriptor builder, mirroring SchemaFixtures.cs's explicit-record convention
    // but parameterized on exactly the fields ValidateAtStartup's four checks branch on: vector/
    // chunk fields (HasVectorOrChunkFields), relations (the OneToMany lookup), and TypeName (the
    // registry key). Everything else is irrelevant to the validator under test.
    private static SchemaDescriptor MinimalSchema(
        string typeName,
        IReadOnlyList<VectorDescriptor>? vectorFields = null,
        IReadOnlyList<ChunkDescriptor>? chunkFields = null,
        IReadOnlyList<RelationDescriptor>? relations = null) => new()
    {
        TypeName      = typeName,
        TableName     = typeName.ToLowerInvariant() + "s",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [],
        FkColumns     = [],
        VectorFields  = vectorFields ?? [],
        ChunkFields   = chunkFields ?? [],
        Relations     = relations ?? [],
        TenantColumn  = "TenantId"
    };

    private static async Task<SchemaRegistry> RegistryWith(params SchemaDescriptor[] schemas)
    {
        var repository = Substitute.For<ISchemaRegistryRepository>();
        var registry = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        foreach (var schema in schemas)
            await registry.RegisterAsync(schema);
        return registry;
    }

    private static PopularitySignalOptions OptionsWith(string parentType, string relation) => new()
    {
        Signals = [new PopularitySignalEntry(parentType, relation)]
    };

    [Fact]
    public async Task ValidateAtStartup_SignalsNonEmpty_EngagementDisabled_Throws()
    {
        // Article has vector fields and a OneToMany relation, so this would otherwise pass —
        // isolating the assertion to the engagement-disabled branch specifically.
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId")]);
        var userArticle = MinimalSchema(
            "UserArticle",
            relations:
            [
                new RelationDescriptor("User", RelationKind.ManyToOne, "User", "UserId"),
                new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")
            ]);
        var registry = await RegistryWith(article, userArticle);
        var options = OptionsWith("Article", "UserArticles");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Engagement*Enabled*");
    }

    [Fact]
    public async Task ValidateAtStartup_UnregisteredParentType_Throws()
    {
        var registry = await RegistryWith();
        var options = OptionsWith("NoSuchType", "SomeRelation");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchType*not a registered schema*");
    }

    [Fact]
    public async Task ValidateAtStartup_ParentTypeHasNoVectorOrChunkFields_Throws()
    {
        var author = MinimalSchema("Author");
        var registry = await RegistryWith(author);
        var options = OptionsWith("Author", "SomeRelation");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Author*no vector or chunk fields*");
    }

    [Fact]
    public async Task ValidateAtStartup_UnknownRelation_Throws()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")]);
        var registry = await RegistryWith(article);
        var options = OptionsWith("Article", "NotARelation");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*NotARelation*is not a relation on*Article*");
    }

    [Fact]
    public async Task ValidateAtStartup_RelationNotOneToMany_Throws()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")]);
        var registry = await RegistryWith(article);
        var options = OptionsWith("Article", "Author");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Author*ManyToOne*not OneToMany*");
    }

    [Fact]
    public async Task ValidateAtStartup_RelatedTypeNotRegistered_Throws()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId")]);
        // UserArticle is deliberately NOT registered.
        var registry = await RegistryWith(article);
        var options = OptionsWith("Article", "UserArticles");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UserArticle*not a registered schema*");
    }

    [Fact]
    public async Task ValidateAtStartup_ChildNotEngagementEligible_Throws()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId")]);
        // UserArticle declares a OneToMany relation of its own, so it is not Engagement-eligible.
        var userArticle = MinimalSchema(
            "UserArticle",
            relations: [new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "UserArticleId")]);
        var registry = await RegistryWith(article, userArticle);
        var options = OptionsWith("Article", "UserArticles");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UserArticle*not StarRocks-eligible*");
    }

    [Fact]
    public async Task ValidateAtStartup_WellFormedSignal_DoesNotThrow()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId")]);
        var userArticle = MinimalSchema(
            "UserArticle",
            relations:
            [
                new RelationDescriptor("User", RelationKind.ManyToOne, "User", "UserId"),
                new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")
            ]);
        var registry = await RegistryWith(article, userArticle);
        var options = OptionsWith("Article", "UserArticles");

        var act = () => PopularitySignalValidator.ValidateAtStartup(options, registry, engagementEnabled: true);

        act.Should().NotThrow();
    }
}
