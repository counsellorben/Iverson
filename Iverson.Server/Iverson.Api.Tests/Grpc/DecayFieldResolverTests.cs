using Iverson.Api.Grpc;
using Iverson.Api.Schema;
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
    public void ComputeDecay_AgeZero_ReturnsOne()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_Age180Days_ReturnsOneHalf()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(-180).ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now);

        Assert.NotNull(result);
        Assert.Equal(0.5, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_Age360Days_ReturnsOneQuarter()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var stored = now.AddDays(-360).ToString("o");

        var result = DecayFieldResolver.ComputeDecay(stored, now);

        Assert.NotNull(result);
        Assert.Equal(0.25, result!.Value, precision: 9);
    }

    [Fact]
    public void ComputeDecay_NullValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay(null, DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeDecay_EmptyValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay(string.Empty, DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeDecay_UnparseableValue_ReturnsNull()
    {
        var result = DecayFieldResolver.ComputeDecay("not-a-timestamp", DateTimeOffset.UtcNow);

        Assert.Null(result);
    }
}
