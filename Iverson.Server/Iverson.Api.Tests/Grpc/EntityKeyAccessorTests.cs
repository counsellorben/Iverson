using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class EntityKeyAccessorTests
{
    private readonly EntityKeyAccessor _sut = new();

    [Fact]
    public void ExtractKey_FindsCanonicalCasing()
    {
        var payload = new Struct();
        payload.Fields["Id"] = Value.ForString("abc-123");

        _sut.ExtractKey(payload, "Id").Should().Be("abc-123");
    }

    [Fact]
    public void ExtractKey_FindsCamelCaseFallback()
    {
        var payload = new Struct();
        payload.Fields["id"] = Value.ForString("abc-123");

        _sut.ExtractKey(payload, "Id").Should().Be("abc-123");
    }

    [Fact]
    public void ExtractKey_ReturnsEmpty_WhenNotPresent()
    {
        var payload = new Struct();

        _sut.ExtractKey(payload, "Id").Should().BeEmpty();
    }

    [Fact]
    public void SetKey_OverwritesExistingCandidateField()
    {
        var payload = new Struct();
        payload.Fields["id"] = Value.ForString("old");

        _sut.SetKey(payload, "Id", "new-key");

        payload.Fields["id"].StringValue.Should().Be("new-key");
        payload.Fields.ContainsKey("Id").Should().BeFalse();
    }

    [Fact]
    public void SetKey_AddsCanonicalField_WhenNeitherCandidatePresent()
    {
        var payload = new Struct();

        _sut.SetKey(payload, "Id", "new-key");

        payload.Fields["Id"].StringValue.Should().Be("new-key");
    }

    [Theory]
    [InlineData(null)]                                      // field absent entirely
    [InlineData("")]                                        // empty string
    [InlineData("00000000-0000-0000-0000-000000000000")]    // .NET/Java unset Guid/UUID
    public void AssignNewKey_StampsFreshKey_WhenKeyNotSupplied(string? supplied)
    {
        var payload = new Struct();
        if (supplied is not null)
            payload.Fields["Id"] = Value.ForString(supplied);

        var assigned = _sut.AssignNewKey(payload, "Id");

        Guid.TryParse(assigned, out _).Should().BeTrue();
        _sut.ExtractKey(payload, "Id").Should().Be(assigned);
    }

    [Fact]
    public void AssignNewKey_StampsFreshKey_WhenKeyIsJsonNull()
    {
        var payload = new Struct();
        payload.Fields["Id"] = Value.ForNull();

        var assigned = _sut.AssignNewKey(payload, "Id");

        Guid.TryParse(assigned, out _).Should().BeTrue();
        _sut.ExtractKey(payload, "Id").Should().Be(assigned);
    }

    [Fact]
    public void AssignNewKey_Throws_WhenClientSuppliedKey()
    {
        var payload = new Struct();
        payload.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());

        var act = () => _sut.AssignNewKey(payload, "Id");

        var ex = act.Should().Throw<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("server-generated");
    }

    [Fact]
    public void AssignNewKey_Throws_WhenSuppliedKeyIsNotAGuid()
    {
        var payload = new Struct();
        payload.Fields["Id"] = Value.ForString("client-chosen");

        var act = () => _sut.AssignNewKey(payload, "Id");

        act.Should().Throw<RpcException>()
           .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
