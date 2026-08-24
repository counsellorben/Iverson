using System.Text;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// Covers the two halves of the oversize-document fix: large text fields are projected into a
/// column that can actually hold them, and a value that still will not fit is refused at the write
/// call instead of dead-lettering on the Kafka projection after the write reported success.
/// </summary>
public class OversizeTextProjectionTests
{
    // ── the projection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToEngagementTableSchema_LargeTextField_GetsTheWideColumnType()
    {
        var schema = SchemaBuilder.ToEngagementTableSchema(SchemaFixtures.ArticleWithProjectionSchema());

        schema.Columns.Single(c => c.Name == "Body").SrType
            .Should().Be("VARCHAR(1048576)",
                "STRING is an alias for varchar(65533) and silently filters anything larger");
    }

    [Fact]
    public void ToEngagementTableSchema_OrdinaryTextField_KeepsTheStringAlias()
    {
        // Only large fields are widened. A sort key or a short attribute has no reason to declare a
        // megabyte, and widening everything would change every table's schema for no benefit.
        var schema = SchemaBuilder.ToEngagementTableSchema(SchemaFixtures.ArticleWithProjectionSchema());

        schema.Columns.Single(c => c.Name == "Category").SrType.Should().Be("STRING");
    }

    [Fact]
    public void ToEngagementTableSchema_NonTextLargeField_KeepsItsOwnType()
    {
        // Guard against retyping a large field that is not textual. Nothing produces this today,
        // which is exactly why it needs pinning: the widening is conditional on the mapped type
        // being STRING, and dropping that condition would turn an INT into a VARCHAR silently.
        var descriptor = SchemaFixtures.ArticleWithProjectionSchema() with
        {
            LargeFieldColumns = ["Body", "WordCount"]
        };

        var schema = SchemaBuilder.ToEngagementTableSchema(descriptor);

        schema.Columns.Single(c => c.Name == "WordCount").SrType.Should().NotContain("VARCHAR");
    }

    // ── the write guard ───────────────────────────────────────────────────────────────────────

    private static Struct PayloadWith(string field, string value)
    {
        var payload = new Struct();
        payload.Fields[field] = Value.ForString(value);
        return payload;
    }

    private static void Validate(Struct payload) =>
        new PayloadSizeValidator().ValidateTextColumnSizes(payload, SchemaFixtures.ArticleWithProjectionSchema());

    [Fact]
    public void Validate_LargeFieldUnderTheWideLimit_IsAccepted()
    {
        var act = () => Validate(PayloadWith("Body", new string('x', 900_000)));

        act.Should().NotThrow("900 KB fits VARCHAR(1048576), and used to dead-letter under STRING");
    }

    [Fact]
    public void Validate_LargeFieldOverTheWideLimit_IsRejectedAtTheWriteCall()
    {
        var act = () => Validate(PayloadWith("Body", new string('x', StarRocksLimits.MaxVarcharBytes + 1)));

        act.Should().Throw<RpcException>()
            .Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public void Validate_OrdinaryTextFieldOverTheStringAlias_IsRejected()
    {
        // A non-large text column still caps at the alias, so the guard must use the cap of the
        // column the value is ACTUALLY going to — not the widest one in the system.
        var act = () => Validate(PayloadWith("Category", new string('x', StarRocksLimits.StringAliasBytes + 1)));

        act.Should().Throw<RpcException>();
    }

    [Fact]
    public void Validate_MultiByteValueUnderTheCharacterCountButOverTheByteCount_IsRejected()
    {
        // StarRocks counts BYTES: four multi-byte characters do not fit a VARCHAR(4). A guard
        // measuring string.Length would accept this and let StarRocks drop it — reintroducing the
        // exact defect, for precisely the non-ASCII content most likely to sit near the limit.
        var justUnderInChars = new string('é', StarRocksLimits.StringAliasBytes - 100);
        Encoding.UTF8.GetByteCount(justUnderInChars)
            .Should().BeGreaterThan(StarRocksLimits.StringAliasBytes, "the fixture must actually be over in bytes");

        var act = () => Validate(PayloadWith("Category", justUnderInChars));

        act.Should().Throw<RpcException>();
    }

    [Fact]
    public void Validate_NonStringValue_IsIgnored()
    {
        var payload = new Struct();
        payload.Fields["WordCount"] = Value.ForNumber(42);

        var act = () => Validate(payload);

        act.Should().NotThrow();
    }
}
