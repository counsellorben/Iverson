using FluentAssertions;
using Iverson.Api.Schema;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class StoreTargetingTests
{
    private static SchemaDescriptor MakeSchema(RelationKind kind) => new()
    {
        TypeName      = "Article",
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [],
        FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [new RelationDescriptor("Author", kind, "Author", "AuthorId")]
    };

    [Theory]
    [InlineData(RelationKind.ManyToOne, true)]
    [InlineData(RelationKind.OneToOne, true)]
    [InlineData(RelationKind.OneToMany, false)]
    public void IsEngagementEligible_MatchesExpectedPerKind(RelationKind kind, bool expected)
    {
        StoreTargeting.IsEngagementEligible(MakeSchema(kind)).Should().Be(expected);
    }

    [Fact]
    public void IsEngagementEligible_ManyToMany_TrueWhenForeignKeyColumnPresent()
    {
        StoreTargeting.IsEngagementEligible(MakeSchema(RelationKind.ManyToMany)).Should().BeTrue();
    }
}
