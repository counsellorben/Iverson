using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        IReadOnlyList<RelationDescriptor>? relations = null,
        string? popularitySignalColumn = null) => new()
    {
        TypeName      = typeName,
        TableName     = typeName.ToLowerInvariant() + "s",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [],
        FkColumns     = [],
        VectorFields  = vectorFields ?? [],
        ChunkFields   = chunkFields ?? [],
        Relations     = relations ?? [],
        TenantColumn  = "TenantId",
        PopularitySignalColumn = popularitySignalColumn
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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: false, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Engagement*Enabled*");
    }

    // Schemas are registered at runtime via the RegisterSchema RPC served by this same process —
    // on a fresh deployment (or after a Postgres reset) an unregistered ParentType in config is
    // not a misconfiguration, it is an ordering the process cannot itself resolve by crashing.
    // ValidateAtStartup must skip the entry and log a warning rather than throw, matching the
    // runtime's own tolerance for this condition (PopularitySignalConsumer.DispatchAsync filters
    // on registry.Get(...) is not null; PopularitySignalReconciliationWorker.SweepSignalAsync
    // returns early).
    [Fact]
    public async Task ValidateAtStartup_UnregisteredParentType_SkipsAndWarns()
    {
        var registry = await RegistryWith();
        var options = OptionsWith("NoSuchType", "SomeRelation");
        var logger = Substitute.For<ILogger>();

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, logger);

        act.Should().NotThrow();
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("NoSuchType")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ValidateAtStartup_ParentTypeHasNoVectorOrChunkFields_Throws()
    {
        var author = MinimalSchema("Author");
        var registry = await RegistryWith(author);
        var options = OptionsWith("Author", "SomeRelation");

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

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

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

        act.Should().NotThrow();
    }

    // FIX 3: RecencyBoost > 0 with no marked column on the child is a silently inert recency
    // term — DATE_FORMAT over a null-valued histogram query never fires, so nothing ever throws
    // or logs today. ValidateAtStartup already fails loud on every other way this feature can be
    // silently inert; this is the equivalent warning for the recency term specifically. A warning,
    // not a throw, because the child schema can be re-registered with the marker later — matching
    // the soft path already used for an unregistered ParentType above.
    [Fact]
    public async Task ValidateAtStartup_RecencyBoostPositive_ChildHasNoMarker_Warns()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "ArticleId")]);
        var comment = MinimalSchema(
            "Comment",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")]);
        var registry = await RegistryWith(article, comment);
        var options = new PopularitySignalOptions
        {
            Signals = [new PopularitySignalEntry("Article", "Comments")],
            RecencyBoost = 0.5
        };
        var logger = Substitute.For<ILogger>();

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, logger);

        act.Should().NotThrow();
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Comment") && o.ToString()!.Contains("RecencyBoost")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ValidateAtStartup_RecencyBoostPositive_ChildHasMarker_DoesNotWarn()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "ArticleId")]);
        var comment = MinimalSchema(
            "Comment",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")],
            popularitySignalColumn: "PostedAt");
        var registry = await RegistryWith(article, comment);
        var options = new PopularitySignalOptions
        {
            Signals = [new PopularitySignalEntry("Article", "Comments")],
            RecencyBoost = 0.5
        };
        var logger = Substitute.For<ILogger>();

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, logger);

        act.Should().NotThrow();
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // FIX 1: a second entry naming the same ParentType writes every matching relation's count
    // (PopularitySignalConsumer.DispatchAsync builds a LIST of matches) but the read side
    // (ObjectSearchGrpcService.PopularityFor / RetrievePopularityOrDegradeAsync) resolves via
    // FirstOrDefault — so the second entry's count is computed and written, and then silently
    // never ranked on. That must be a loud startup failure, not a silent half-honoured config.
    [Fact]
    public async Task ValidateAtStartup_DuplicateParentType_Throws()
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations:
            [
                new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "ArticleId"),
                new RelationDescriptor("Likes", RelationKind.OneToMany, "Like", "ArticleId")
            ]);
        var comment = MinimalSchema(
            "Comment",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")]);
        var like = MinimalSchema(
            "Like",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")]);
        var registry = await RegistryWith(article, comment, like);
        var options = new PopularitySignalOptions
        {
            Signals =
            [
                new PopularitySignalEntry("Article", "Comments"),
                new PopularitySignalEntry("Article", "Likes")
            ]
        };

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Article*configured more than once*");
    }

    [Fact]
    public void AddPopularitySignalOptions_DefaultsAreCorrect()
    {
        var options = new PopularitySignalOptions();

        options.RecencyBoost.Should().Be(0.0);
        options.RecencyHalfLifeDays.Should().Be(180.0);
    }

    [Fact]
    public void AddPopularitySignalOptions_RecencyBoost_Negative_Throws()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "PopularitySignal:RecencyBoost", "-0.1" }
            })
            .Build();

        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RecencyBoost*finite and non-negative*");
    }

    [Fact]
    public void AddPopularitySignalOptions_RecencyBoost_NaN_Throws()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "PopularitySignal:RecencyBoost", "NaN" }
            })
            .Build();

        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RecencyBoost*finite and non-negative*");
    }

    [Fact]
    public void AddPopularitySignalOptions_RecencyHalfLifeDays_Zero_Throws()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "PopularitySignal:RecencyHalfLifeDays", "0" }
            })
            .Build();

        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RecencyHalfLifeDays*in (0, 300]*");
    }

    [Fact]
    public void AddPopularitySignalOptions_RecencyHalfLifeDays_ExceedsMax_Throws()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "PopularitySignal:RecencyHalfLifeDays", "300.1" }
            })
            .Build();

        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*RecencyHalfLifeDays*in (0, 300]*");
    }

    [Fact]
    public void AddPopularitySignalOptions_RecencyHalfLifeDays_AtMax_Succeeds()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "PopularitySignal:RecencyHalfLifeDays", "300.0" }
            })
            .Build();

        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().NotThrow();
    }
}
