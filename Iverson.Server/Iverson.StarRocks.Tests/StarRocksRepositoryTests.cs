using FluentAssertions;
using Iverson.StarRocks;
using NSubstitute;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksRepositoryTests
{
    [Fact]
    public void IStarRocksQueryExecutor_ExistsAsInterface()
    {
        var sut = Substitute.For<IEngagementStoreQueryExecutor>();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void IStarRocksEntityStore_ExistsAsInterface()
    {
        var sut = Substitute.For<IEngagementStoreEntityStore>();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void StarRocksRepository_ImplementsQueryAndEntityStoreRoles()
    {
        typeof(StarRocksRepository).Should().Implement<IEngagementStoreQueryExecutor>();
        typeof(StarRocksRepository).Should().Implement<IEngagementStoreEntityStore>();
    }

    [Fact]
    public void StarRocksTableSchema_StoresColumns()
    {
        var key  = new StarRocksColumnSchema("Id", "VARCHAR(36)", false);
        var cols = new List<StarRocksColumnSchema>
        {
            new("Name", "STRING", false),
            new("Bio",  "STRING", true)
        };
        var schema = new StarRocksTableSchema("authors", key, cols);

        schema.TableName.Should().Be("authors");
        schema.KeyColumn.Name.Should().Be("Id");
        schema.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void AggregationDescriptor_DefaultSizeIsTen()
    {
        var spec = new AggregationDescriptor("n", AggregationKind.Terms, "Name");
        spec.Size.Should().Be(10);
    }

    // ── PipelineAsync — Layer 2 (post-fetch) masking ────────────────────────────
    //
    // StarRocksRepository is sealed and QueryAsync<T> hits a real MySqlConnection (not virtual,
    // no injectable seam), so PipelineAsync itself can't be exercised without a live StarRocks
    // backend. MaskPipelineRows is extracted specifically so this row-stripping transformation —
    // the Step 5 "Layer 2 masking" safety net for implicit-passthrough/"select *" columns that
    // Build's tracked LastCols doesn't cover physically — has a unit-testable seam.

    [Fact]
    public void MaskPipelineRows_StripsKeysNotInLastCols_KeepsKeysThatAre()
    {
        var lastCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "Id", ["Title"] = "Title"
        };
        var row = new Dictionary<string, object> { ["Id"] = "1", ["Title"] = "T", ["Secret"] = "hidden" };

        var result = StarRocksRepository.MaskPipelineRows(new[] { (dynamic)row }, lastCols).ToList();

        result.Should().HaveCount(1);
        var dict = (IDictionary<string, object>)result[0];
        dict.Keys.Should().BeEquivalentTo(["Id", "Title"]);
        dict.Should().NotContainKey("Secret");
        dict["Title"].Should().Be("T");
    }

    [Fact]
    public void MaskPipelineRows_LastColsContainsEveryColumn_IsNoOp()
    {
        // The unrestricted/no-authz case: lastCols already contains every physical column, so
        // masking must not remove anything.
        var lastCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "Id", ["Title"] = "Title", ["Body"] = "Body"
        };
        var row = new Dictionary<string, object> { ["Id"] = "1", ["Title"] = "T", ["Body"] = "B" };

        var result = StarRocksRepository.MaskPipelineRows(new[] { (dynamic)row }, lastCols).ToList();

        var dict = (IDictionary<string, object>)result[0];
        dict.Keys.Should().BeEquivalentTo(["Id", "Title", "Body"]);
    }

    [Fact]
    public void MaskPipelineRows_MasksEveryRowInTheSet()
    {
        var lastCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Id"] = "Id" };
        var rows = new[]
        {
            (dynamic)new Dictionary<string, object> { ["Id"] = "1", ["Secret"] = "a" },
            (dynamic)new Dictionary<string, object> { ["Id"] = "2", ["Secret"] = "b" }
        };

        var result = StarRocksRepository.MaskPipelineRows(rows, lastCols).ToList();

        result.Should().HaveCount(2);
        foreach (var r in result)
        {
            var dict = (IDictionary<string, object>)r;
            dict.Keys.Should().BeEquivalentTo(["Id"]);
        }
    }
}
