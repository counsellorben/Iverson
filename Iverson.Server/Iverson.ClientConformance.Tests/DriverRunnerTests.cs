using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class DriverRunnerTests
{
    private static DriverContext Context() => new(
        Scenario: "s1",
        Type: "Widget",
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s1-",
        WrongActingToken: "wrong-acting-token");

    [Fact]
    public void MergeKeys_QualifiesKeysByLanguage_SoSameLogicalNameFromTwoLanguagesDoNotCollide()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var pythonDoc = new PhaseDocument("python", "write",
        [
            new StepResult("create-primary", true, Keys: new Dictionary<string, string> { ["primary"] = "11111111-1111-1111-1111-111111111111" }),
        ]);
        var goDoc = new PhaseDocument("go", "write",
        [
            new StepResult("create-primary", true, Keys: new Dictionary<string, string> { ["primary"] = "22222222-2222-2222-2222-222222222222" }),
        ]);

        runner.MergeKeys("python", pythonDoc);
        runner.MergeKeys("go", goDoc);

        runner.KeysByLanguage.Should().ContainKey("python");
        runner.KeysByLanguage.Should().ContainKey("go");
        runner.KeysByLanguage["python"]["primary"].Should().Be("11111111-1111-1111-1111-111111111111");
        runner.KeysByLanguage["go"]["primary"].Should().Be("22222222-2222-2222-2222-222222222222");
        // Same logical name, different languages, different keys: qualifying by language is what
        // keeps these from colliding into a single flat "primary" -> key mapping.
        runner.KeysByLanguage["python"]["primary"].Should().NotBe(runner.KeysByLanguage["go"]["primary"]);
    }

    [Fact]
    public void MergeKeys_StepWithoutKeys_DoesNotCreateLanguageEntry()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");
        var doc = new PhaseDocument("java", "read", [new StepResult("read-primary", true)]);

        runner.MergeKeys("java", doc);

        runner.KeysByLanguage.Should().NotContainKey("java");
    }

    [Fact]
    public void BuildFlags_OnRegisterPhase_OmitsKeysFlag()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(Phase.Register, "python", Context(), "/tmp/out.json");

        flags.Should().NotContain("--keys");
    }

    [Fact]
    public void BuildFlags_OnWritePhase_IncludesLanguageQualifiedKeysJson()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");
        var doc = new PhaseDocument("python", "write",
        [
            new StepResult("create-primary", true, Keys: new Dictionary<string, string> { ["primary"] = "11111111-1111-1111-1111-111111111111" }),
        ]);
        runner.MergeKeys("python", doc);

        var flags = runner.BuildFlags(Phase.Write, "python", Context(), "/tmp/out.json");

        var keysIndex = flags.IndexOf("--keys");
        keysIndex.Should().BeGreaterThanOrEqualTo(0);
        var keysJson = flags[keysIndex + 1];

        // The brief's exact shape: {"<language>": {"<logical name>": "<uuid>"}}.
        using var parsed = JsonDocument.Parse(keysJson);
        parsed.RootElement.GetProperty("python").GetProperty("primary").GetString()
            .Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void BuildFlags_IncludesAllRequiredBaseFlags()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(Phase.Read, "go", Context(), "/tmp/out.json");

        flags.Should().Contain(["--scenario", "s1", "--phase", "read", "--type", "Widget",
            "--tenant", "iverson-loadtest-dynamic", "--grpc", "http://localhost:5000",
            "--acting-token", "acting-token", "--owner-id", "owner-id", "--id-prefix", "s1-",
            "--out", "/tmp/out.json"]);
    }

    /// <summary>
    /// S8 identity's negative leg is the only thing that reads this flag, but every driver
    /// invocation carries it: the flag set is built once for all phases and all scenarios, and a
    /// driver that never needs it ignores it. It must be emitted even when empty (the harness
    /// always emits `--flag value` pairs) so a driver's positional parser never mis-pairs the
    /// flags that follow.
    /// </summary>
    [Fact]
    public void BuildFlags_CarriesTheWrongActingTokenForTheIdentityScenariosNegativeLeg()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(Phase.Read, "go", Context(), "/tmp/out.json");

        flags.Should().Contain(["--wrong-acting-token", "wrong-acting-token"]);
    }

    [Fact]
    public void BuildFlags_WithNoWrongActingTokenConfigured_StillEmitsTheFlagWithAnEmptyValue()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(
            Phase.Read, "go", Context() with { WrongActingToken = string.Empty }, "/tmp/out.json");

        var index = flags.IndexOf("--wrong-acting-token");
        index.Should().BeGreaterThanOrEqualTo(0);
        flags[index + 1].Should().BeEmpty();
    }

    // The five client libraries disagree on endpoint syntax (.NET/Java need the scheme, Go/
    // TypeScript need it gone), so --grpc must always leave here in one canonical
    // scheme://host:port form. These pin that form.
    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("localhost:8080", "http://localhost:8080")]
    [InlineData("http://localhost", "http://localhost:80")]
    [InlineData("https://iverson.example.com", "https://iverson.example.com:443")]
    [InlineData("  http://iverson:5000  ", "http://iverson:5000")]
    public void NormalizeGrpcUrl_ProducesSchemeHostAndExplicitPort(string input, string expected) =>
        DriverRunner.NormalizeGrpcUrl(input).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://")]
    public void NormalizeGrpcUrl_OnUnusableValue_Throws(string input) =>
        FluentActions.Invoking(() => DriverRunner.NormalizeGrpcUrl(input))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void BuildFlags_NormalizesASchemelessGrpcEndpoint()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(
            Phase.Read, "go", Context() with { GrpcUrl = "localhost:8080" }, "/tmp/out.json");

        flags[flags.IndexOf("--grpc") + 1].Should().Be("http://localhost:8080");
    }
}
