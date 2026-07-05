using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Iverson.Client.Core;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class StructConverterToDictionaryTests
{
    [Fact]
    public void ToDictionary_MapsScalarKinds()
    {
        var s = new Struct
        {
            Fields =
            {
                ["name"]  = Value.ForString("Alice"),
                ["n"]     = Value.ForNumber(3),
                ["flag"]  = Value.ForBool(true),
                ["blank"] = Value.ForNull()
            }
        };

        var dict = StructConverter.ToDictionary(s);

        dict["name"].Should().Be("Alice");
        dict["n"].Should().Be(3.0);
        dict["flag"].Should().Be(true);
        dict["blank"].Should().BeNull();
    }

    [Fact]
    public void ToDictionary_MapsNestedListAndStruct()
    {
        var s = new Struct
        {
            Fields =
            {
                ["tags"]  = Value.ForList(Value.ForString("a"), Value.ForString("b")),
                ["inner"] = Value.ForStruct(new Struct { Fields = { ["x"] = Value.ForNumber(1) } })
            }
        };

        var dict = StructConverter.ToDictionary(s);

        dict["tags"].Should().BeEquivalentTo(new object?[] { "a", "b" });
        ((IReadOnlyDictionary<string, object?>)dict["inner"]!)["x"].Should().Be(1.0);
    }
}
