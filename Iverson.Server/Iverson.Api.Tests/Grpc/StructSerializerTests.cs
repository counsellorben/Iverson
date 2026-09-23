using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR #15: <see cref="StructSerializer"/> folds every payload key through <c>UpperFirst</c>
/// before serializing, so two distinct client-supplied keys that fold to the same name after
/// case-normalization (e.g. "name" and "Name", or "id" and "Id") must be rejected with a typed
/// InvalidArgument rather than silently overwriting one another — the prior implementation's
/// plain <c>ToDictionary</c> either threw an unhandled "duplicate key" ArgumentException (a bare
/// Unknown gRPC status) or, for the recursive Struct-field case, was never even reachable via a
/// caller that inspected it.
/// </summary>
public class StructSerializerTests
{
    [Fact]
    public void SerializePayload_NoCollision_RoundTripsAllFields()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["name"]   = Value.ForString("Widget"),
                ["price"]  = Value.ForNumber(9.99),
                ["active"] = Value.ForBool(true),
            }
        };

        var json = StructSerializer.SerializePayload(payload);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Name").GetString().Should().Be("Widget");
        doc.RootElement.GetProperty("Price").GetDouble().Should().Be(9.99);
        doc.RootElement.GetProperty("Active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SerializePayload_TopLevelKeysCollideAfterFolding_ThrowsInvalidArgument()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["name"] = Value.ForString("lowercase-wins-the-race"),
                ["Name"] = Value.ForString("already-canonical"),
            }
        };

        var act = () => StructSerializer.SerializePayload(payload);

        var ex = act.Should().Throw<RpcException>().Which;
        ex.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("name");
        ex.Status.Detail.Should().Contain("Name");
    }

    // Both orderings are asserted (this test and the one above) because MapField/Dictionary
    // enumeration order determines which key is reported as "already seen" — a fold that only
    // detects the collision one direction would be an off-by-one in the guard, not a real fix.
    [Fact]
    public void SerializePayload_TopLevelKeysCollideAfterFolding_ReverseOrder_ThrowsInvalidArgument()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["Id"] = Value.ForString("canonical-first"),
                ["id"] = Value.ForString("lowercase-second"),
            }
        };

        var act = () => StructSerializer.SerializePayload(payload);

        act.Should().Throw<RpcException>()
            .Which.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // The recursive branch: ProtoValueToObject folds a nested Struct's fields through the exact
    // same FoldKeys helper, so a collision buried inside a StructValue-typed field must be caught
    // too, not just at the payload's own top level.
    [Fact]
    public void SerializePayload_NestedStructKeysCollideAfterFolding_ThrowsInvalidArgument()
    {
        var nested = new Struct
        {
            Fields =
            {
                ["city"] = Value.ForString("Springfield"),
                ["City"] = Value.ForString("also-springfield"),
            }
        };
        var payload = new Struct
        {
            Fields = { ["address"] = Value.ForStruct(nested) }
        };

        var act = () => StructSerializer.SerializePayload(payload);

        var ex = act.Should().Throw<RpcException>().Which;
        ex.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("city");
        ex.Status.Detail.Should().Contain("City");
    }

    [Fact]
    public void SerializePayload_NestedStructWithoutCollision_RoundTrips()
    {
        var nested = new Struct
        {
            Fields = { ["city"] = Value.ForString("Springfield"), ["zip"] = Value.ForString("00000") }
        };
        var payload = new Struct
        {
            Fields = { ["address"] = Value.ForStruct(nested) }
        };

        var json = StructSerializer.SerializePayload(payload);

        using var doc = JsonDocument.Parse(json);
        var address = doc.RootElement.GetProperty("Address");
        address.GetProperty("City").GetString().Should().Be("Springfield");
        address.GetProperty("Zip").GetString().Should().Be("00000");
    }

    [Fact]
    public void SerializePayload_ListValue_RoundTrips()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["tags"] = Value.ForList(Value.ForString("a"), Value.ForString("b")),
            }
        };

        var json = StructSerializer.SerializePayload(payload);

        using var doc = JsonDocument.Parse(json);
        var tags = doc.RootElement.GetProperty("Tags");
        tags.GetArrayLength().Should().Be(2);
        tags[0].GetString().Should().Be("a");
        tags[1].GetString().Should().Be("b");
    }

    [Fact]
    public void SerializePayload_NullValue_RoundTripsAsJsonNull()
    {
        var payload = new Struct
        {
            Fields = { ["deletedAt"] = Value.ForNull() }
        };

        var json = StructSerializer.SerializePayload(payload);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("DeletedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("id", "Id")]
    [InlineData("tenantId", "TenantId")]
    [InlineData("Name", "Name")]
    public void UpperFirst_ProducesExpectedCasing(string input, string expected) =>
        StructSerializer.UpperFirst(input).Should().Be(expected);
}
