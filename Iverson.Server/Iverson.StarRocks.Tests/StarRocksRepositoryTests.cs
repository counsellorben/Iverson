using Dapper;
using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class EngagementRepositoryTests
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
    public void EngagementRepository_ImplementsQueryAndEntityStoreRoles()
    {
        typeof(EngagementRepository).Should().Implement<IEngagementStoreQueryExecutor>();
        typeof(EngagementRepository).Should().Implement<IEngagementStoreEntityStore>();
    }

    [Fact]
    public void EngagementTableSchema_StoresColumns()
    {
        var key  = new EngagementColumnSchema("Id", "VARCHAR(36)", false);
        var cols = new List<EngagementColumnSchema>
        {
            new("Name", "STRING", false),
            new("Bio",  "STRING", true)
        };
        var schema = new EngagementTableSchema("authors", key, cols);

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
    // EngagementRepository is sealed and QueryAsync<T> hits a real MySqlConnection (not virtual,
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

        var result = EngagementRepository.MaskPipelineRows(new[] { (dynamic)row }, lastCols).ToList();

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

        var result = EngagementRepository.MaskPipelineRows(new[] { (dynamic)row }, lastCols).ToList();

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

        var result = EngagementRepository.MaskPipelineRows(rows, lastCols).ToList();

        result.Should().HaveCount(2);
        foreach (var r in result)
        {
            var dict = (IDictionary<string, object>)r;
            dict.Keys.Should().BeEquivalentTo(["Id"]);
        }
    }

    // ── the cold-start gate honours a streaming read's token ─────────────────────

    [Fact]
    public async Task A_streaming_reads_token_cuts_only_its_own_wait_at_the_cold_start_gate_short()
    {
        // Port 1 refuses at once, so the gate keeps polling until BackendReadyTimeout; no StarRocks is needed.
        var repo = new EngagementRepository("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1",
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(3) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // The cancelled read starts first, so it is the one that starts the gate's shared wait.
        var cancelled = ReadAllAsync(repo.StreamTenantScopedAsync("sr.test", "t1", "SELECT 1", new DynamicParameters(), _ => false, cts.Token));
        var other = ReadAllAsync(repo.StreamTenantScopedAsync("sr.test", "t1", "SELECT 1", new DynamicParameters(), _ => false, default));

        var act = () => cancelled.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The shared wait belongs to every caller: the other read still gets the gate's own verdict.
        var rest = () => other.WaitAsync(TimeSpan.FromSeconds(15));
        await rest.Should().ThrowAsync<EngagementNotReadyException>();

        static async Task<int> ReadAllAsync(IAsyncEnumerable<IDictionary<string, object?>> rows)
        {
            var n = 0;
            await foreach (var _ in rows) n++;
            return n;
        }
    }
}
