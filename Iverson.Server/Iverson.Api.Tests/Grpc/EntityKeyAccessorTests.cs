using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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
}
