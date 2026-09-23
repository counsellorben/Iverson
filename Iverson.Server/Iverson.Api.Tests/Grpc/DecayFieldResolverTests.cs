using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class DecayFieldResolverTests
{
    private static SchemaDescriptor MakeSchema(
        string typeName,
        IReadOnlyList<ColumnDescriptor> scalarColumns,
        HashSet<string> metadataColumns) => new()
    {
        TypeName      = typeName,
        TableName     = typeName.ToLowerInvariant(),
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = scalarColumns,
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        MetadataColumns = metadataColumns,
        TenantColumn  = SchemaDescriptor.TenantColumnName,
    };

    [Fact]
    public void ResolveDecayField_NoTimestampMetadataColumns_ReturnsNull()
    {
        var schema = MakeSchema(
            $"NoTimestamp_{Guid.NewGuid():N}",
            [new ColumnDescriptor("Title", "text", false)],
            metadataColumns: ["Title"]);

        var result = DecayFieldResolver.ResolveDecayField(schema, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDecayField_OneTimestampMetadataColumn_ReturnsCamelCaseKey()
    {
        var schema = MakeSchema(
            $"OneTimestamp_{Guid.NewGuid():N}",
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
            ],
            metadataColumns: ["Title", "PublishedAt"]);

        var result = DecayFieldResolver.ResolveDecayField(schema, NullLogger.Instance);

        Assert.Equal("publishedAt", result);
    }

    [Fact]
    public void ResolveDecayField_TwoTimestampMetadataColumns_ReturnsNullDeliberately()
    {
        var schema = MakeSchema(
            $"TwoTimestamps_{Guid.NewGuid():N}",
            [
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
                new ColumnDescriptor("UpdatedAt", "DATETIME", false),
            ],
            metadataColumns: ["PublishedAt", "UpdatedAt"]);

        var result = DecayFieldResolver.ResolveDecayField(schema, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDecayField_TimestampColumnNotDeclaredMetadata_IsNotSelected()
    {
        var schema = MakeSchema(
            $"UndeclaredTimestamp_{Guid.NewGuid():N}",
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
            ],
            metadataColumns: ["Title"]); // PublishedAt intentionally NOT declared metadata

        var result = DecayFieldResolver.ResolveDecayField(schema, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDecayField_ReRegisteredWithChangedTimestampColumns_ReturnsNewFieldNotCached()
    {
        var typeName = $"Reregistered_{Guid.NewGuid():N}";

        var originalSchema = MakeSchema(
            typeName,
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
            ],
            metadataColumns: ["Title", "PublishedAt"]);

        var originalResult = DecayFieldResolver.ResolveDecayField(originalSchema, NullLogger.Instance);
        Assert.Equal("publishedAt", originalResult);

        // Re-register the same type with a different timestamp metadata column — simulates a
        // live RegisterSchema RPC changing the schema's shape without a process restart.
        var updatedSchema = MakeSchema(
            typeName,
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("UpdatedAt", "DATETIME", false),
            ],
            metadataColumns: ["Title", "UpdatedAt"]);

        var updatedResult = DecayFieldResolver.ResolveDecayField(updatedSchema, NullLogger.Instance);

        Assert.Equal("updatedAt", updatedResult);
    }

    [Fact]
    public void ComputeDecay_AgeZero_ReturnsOne()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now, 180.0);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_Age180Days_ReturnsOneHalf()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(-180).ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now, 180.0);

        Assert.NotNull(result);
        Assert.Equal(0.5, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_Age360Days_ReturnsOneQuarter()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(-360).ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now, 180.0);

        Assert.NotNull(result);
        Assert.Equal(0.25, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_FutureTimestamp_ClampsToOne()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(180).ToString("o"); // 180 days in the future

        var result = DecayFieldResolver.ComputeDecay(stored, now, 180.0);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_NullValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay(null, DateTimeOffset.UtcNow, 180.0);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeDecay_EmptyValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay(string.Empty, DateTimeOffset.UtcNow, 180.0);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeDecay_UnparseableValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay("not-a-timestamp", DateTimeOffset.UtcNow, 180.0);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeDecay_NonDefaultHalfLife_DiffersFromDefault()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(-180).ToString("o");

        var defaultResult  = DecayFieldResolver.ComputeDecay(stored, now, 180.0);
        var nonDefault     = DecayFieldResolver.ComputeDecay(stored, now, 90.0);

        Assert.NotNull(defaultResult);
        Assert.NotNull(nonDefault);
        Assert.NotEqual(defaultResult!.Value, nonDefault!.Value);
        Assert.Equal(0.25, nonDefault!.Value, precision: 9);
    }

    [Fact]
    public void ComputeRecencySum_NullSeries_ReturnsZero()
    {
        var result = DecayFieldResolver.ComputeRecencySum(null, DateTimeOffset.UtcNow, 180.0);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeRecencySum_EmptySeries_ReturnsZero()
    {
        var result = DecayFieldResolver.ComputeRecencySum(string.Empty, DateTimeOffset.UtcNow, 180.0);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeRecencySum_SingleCurrentBucket_ReturnsItsCount()
    {
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var series = "2026-07:5";

        var result = DecayFieldResolver.ComputeRecencySum(series, now, 180.0);

        Assert.Equal(5.0, result, precision: 9);
    }

    [Fact]
    public void ComputeRecencySum_BucketOneHalfLifeOld_ReturnsHalfItsCount()
    {
        // The bucket's age is measured from its START (2026-01-01), not its midpoint, so
        // "one half-life old" means the bucket start is exactly halfLifeDays before now.
        var halfLifeDays = 180.0;
        var bucketStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = bucketStart.AddDays(halfLifeDays);
        var series = "2026-01:10";

        var result = DecayFieldResolver.ComputeRecencySum(series, now, halfLifeDays);

        Assert.Equal(5.0, result, precision: 9);
    }

    [Fact]
    public void ComputeRecencySum_FutureBucket_ClampsToFullCount()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var series = "2026-07:8"; // bucket start is months after "now"

        var result = DecayFieldResolver.ComputeRecencySum(series, now, 180.0);

        Assert.Equal(8.0, result, precision: 9);
    }

    [Fact]
    public void ComputeRecencySum_MultipleBuckets_SumsDecayedContributions()
    {
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var halfLifeDays = 180.0;
        var oldStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ageDays = (now - oldStart).TotalDays;
        var expected = 5.0 + 10.0 * Math.Pow(0.5, ageDays / halfLifeDays);
        var series = "2026-01:10;2026-07:5";

        var result = DecayFieldResolver.ComputeRecencySum(series, now, halfLifeDays);

        Assert.Equal(expected, result, precision: 9);
    }

    [Theory]
    [InlineData("bad-key:5")]        // unparseable yyyy-MM key
    [InlineData("2026-07:notanumber")] // unparseable count
    [InlineData("2026-07")]           // missing colon
    public void ComputeRecencySum_MalformedEntry_AbandonsWholeSeriesReturnsZero(string malformedSeries)
    {
        var result = DecayFieldResolver.ComputeRecencySum(malformedSeries, DateTimeOffset.UtcNow, 180.0);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeRecencySum_OneMalformedEntryAmongValidOnes_AbandonsWholeSeriesReturnsZero()
    {
        // A partial sum over the entries that DID parse would be a confidently wrong recency
        // signal — worse than no signal at all. The whole series must be abandoned.
        var series = "2026-01:10;2026-07:notanumber";

        var result = DecayFieldResolver.ComputeRecencySum(series, DateTimeOffset.UtcNow, 180.0);

        Assert.Equal(0.0, result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void AddDecayOptions_InvalidHalfLifeDays_Throws(string halfLifeDays)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Decay:HalfLifeDays"] = halfLifeDays,
            })
            .Build();

        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddDecayOptions(config));
    }
}
