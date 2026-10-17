using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// CSR round 12 #8. A popularity signal counts CHILD rows onto the PARENT's Qdrant point, so it may be
// computed and ranked on only when every caller who can read the parent could read each counted
// child row. The cases follow RowFieldAuthorizationEvaluator's read semantics:
//  * a role reads every row in the tenant when a RowPermission grants it CanReadAll;
//  * an owner-scoped type is read by any caller, who sees their own rows and needs no role;
//  * a FieldPermission with a non-empty ReadableRoles hides that field from every other caller.
public class PopularitySignalEligibilityTests
{
    private static readonly RelationDescriptor Comments =
        new("Comments", RelationKind.OneToMany, "Comment", "ArticleId");

    private static AuthorizationRules ReadableBy(params string[] roles) =>
        new(null, roles.Select(role => new RowPermission(role, true, false, false)).ToList(), []);

    private static SchemaDescriptor Article(AuthorizationRules? authorization) => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "TEXT", false)],
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields    = [],
        Relations      = [Comments],
        Authorization  = authorization,
        TenantColumn   = SchemaDescriptor.TenantColumnName
    };

    // PostedAt is the child's [IversonPopularitySignal] column, which the recency series reads.
    private static SchemaDescriptor Comment(AuthorizationRules? authorization) => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Body", "TEXT", false),
            new ColumnDescriptor("ArticleId", "UUID", true),
            new ColumnDescriptor("PostedAt", "TIMESTAMPTZ", false)
        ],
        FkColumns              = [],
        VectorFields           = [],
        ChunkFields            = [],
        Relations              = [],
        Authorization          = authorization,
        TenantColumn           = SchemaDescriptor.TenantColumnName,
        PopularitySignalColumn = "PostedAt"
    };

    private static async Task<SchemaRegistry> RegistryWith(params SchemaDescriptor[] schemas)
    {
        var registry = new SchemaRegistry(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance);
        foreach (var schema in schemas)
            await registry.RegisterAsync(schema);
        return registry;
    }

    // Nobody can read a parent with no Authorization, so its ranking reveals nothing to anyone.
    [Fact]
    public void IsEligible_ParentWithNoAuthorization_ReturnsTrue() =>
        PopularitySignalEligibility.IsEligible(Article(null), Comments, Comment(null)).Should().BeTrue();

    [Fact]
    public void IsEligible_OwnerScopedParent_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers") with { OwnerField = "AuthorId" }), Comments, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    [Fact]
    public void IsEligible_ChildWithNoAuthorization_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(null))
            .Should().BeFalse();

    [Fact]
    public void IsEligible_EveryParentReaderReadsEveryChildRow_ReturnsTrue() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers")), Comments, Comment(ReadableBy("readers", "staff")))
            .Should().BeTrue();

    [Fact]
    public void IsEligible_AParentReaderThatCannotReadEveryChildRow_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers", "staff")), Comments, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    // Roles compare ordinally, as the evaluator matches them to the caller's `groups` claim.
    [Fact]
    public void IsEligible_ChildRoleDifferingOnlyByCase_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(ReadableBy("Readers")))
            .Should().BeFalse();

    // A role without CanReadAll on the parent does not read it, so the child need not cover it.
    [Fact]
    public void IsEligible_ParentRoleWithoutCanReadAll_IsNotAParentReader_ReturnsTrue()
    {
        var parentRules = new AuthorizationRules(
            null, [new RowPermission("readers", true, false, false), new RowPermission("writers", false, true, true)], []);

        PopularitySignalEligibility.IsEligible(Article(parentRules), Comments, Comment(ReadableBy("readers")))
            .Should().BeTrue();
    }

    // Nor does a child role without CanReadAll read the child's rows.
    [Fact]
    public void IsEligible_ChildRoleWithoutCanReadAll_DoesNotCoverAParentReader_ReturnsFalse()
    {
        var childRules = new AuthorizationRules(null, [new RowPermission("readers", false, true, true)], []);

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(childRules))
            .Should().BeFalse();
    }

    // The count is keyed on the foreign key and the recency series on the signal column, so each
    // must be readable to every parent reader. Field names compare case-insensitively; an empty
    // ReadableRoles restricts nothing; a permission on any other field is irrelevant.
    [Theory]
    [InlineData("ArticleId", "editors", false)]
    [InlineData("articleid", "editors", false)]
    [InlineData("PostedAt", "editors", false)]
    [InlineData("ArticleId", "readers,editors", true)]
    [InlineData("ArticleId", "", true)]
    [InlineData("Body", "editors", true)]
    public void IsEligible_ChildFieldPermission_DecidesByTheForeignKeyAndTheSignalColumn(
        string fieldName, string readableRoles, bool expected)
    {
        var roles = readableRoles.Length == 0 ? [] : readableRoles.Split(',').ToList();
        var childRules = new AuthorizationRules(
            null,
            [new RowPermission("readers", true, false, false)],
            [new FieldPermission(fieldName, roles, ["editors"])]);

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(childRules))
            .Should().Be(expected);
    }

    // Only a OneToMany relation has child rows to count.
    [Fact]
    public void IsEligible_RelationNotOneToMany_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers")), Comments with { Kind = RelationKind.ManyToOne }, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    [Fact]
    public async Task IsEligible_ChildNotRegistered_ReturnsFalse()
    {
        var registry = await RegistryWith(Article(ReadableBy("readers")));

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeFalse();
    }

    [Fact]
    public async Task IsEligible_ChildResolvedThroughTheRegistry_ReturnsTrue()
    {
        var registry = await RegistryWith(Article(ReadableBy("readers")), Comment(ReadableBy("readers")));

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeTrue();
    }

    // Spec §3.1: "A missing child or a relation that isn't OneToMany makes the signal ineligible"
    // holds even for a parent with no Authorization.
    [Fact]
    public async Task IsEligible_ParentWithNoAuthorizationButNoRegisteredChild_ReturnsFalse()
    {
        var registry = await RegistryWith(Article(null));

        PopularitySignalEligibility.IsEligible(Article(null), Comments, registry).Should().BeFalse();
    }
}
