using FluentAssertions;
using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

[Trait("Category", "Integration")]
[Collection(TrinoCollection.Name)]
public sealed class PortedTrinoCasesTests(TrinoContainerFixture fixture)
{
    public static TheoryData<OracleCase> Cases()
    {
        var data = new TheoryData<OracleCase>();
        foreach (var c in OracleCase.LoadPorted()) data.Add(c);
        return data;
    }

    [Fact]
    public void All_130_ported_cases_are_present() => OracleCase.LoadPorted().Should().HaveCount(130);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Agrees_with_Trino(OracleCase c) => OracleRunner.AssertAgreesAsync(fixture.Client, c);
}
