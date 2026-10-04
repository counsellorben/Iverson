using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

[Collection("driver-process-timeout")]
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
        ServiceToken: "service-token",
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
            "--client-id", "client-id", "--token-endpoint", "http://localhost:9000/application/o/token/",
            "--owner-id", "owner-id", "--id-prefix", "s1-", "--out", "/tmp/out.json"]);
    }

    // Every local user can read a process's command line, so no secret may ride on it.
    [Theory]
    [InlineData("--client-secret", "client-secret")]
    [InlineData("--service-token", "service-token")]
    [InlineData("--acting-token", "acting-token")]
    [InlineData("--wrong-acting-token", "wrong-acting-token")]
    public void BuildFlags_CarriesNoSecretFlagOrValue(string flag, string value)
    {
        var runner = new DriverRunner(repoRoot: "/tmp");

        var flags = runner.BuildFlags(Phase.Read, "go", Context(), "/tmp/out.json");

        flags.Should().NotContain(flag);
        flags.Should().NotContain(value);
    }

    [Theory]
    [InlineData("IVERSON_DRIVER_CLIENT_SECRET", "client-secret")]
    [InlineData("IVERSON_DRIVER_SERVICE_TOKEN", "service-token")]
    [InlineData("IVERSON_DRIVER_ACTING_TOKEN", "acting-token")]
    [InlineData("IVERSON_DRIVER_WRONG_ACTING_TOKEN", "wrong-acting-token")]
    public void BuildEnvironment_CarriesEachSecret(string variable, string value) =>
        DriverRunner.BuildEnvironment(Context()).Should().Contain(variable, value);

    /// <summary>
    /// S8 identity's negative leg is the only thing that reads this value, but every driver
    /// invocation carries it: the environment is built once for all phases and all scenarios, and a
    /// driver that never needs it ignores it. It must be set even when empty, so a value the harness
    /// itself inherited never reaches the driver in its place.
    /// </summary>
    [Fact]
    public void BuildEnvironment_CarriesTheWrongActingTokenForTheIdentityScenariosNegativeLeg() =>
        DriverRunner.BuildEnvironment(Context())
            .Should().Contain("IVERSON_DRIVER_WRONG_ACTING_TOKEN", "wrong-acting-token");

    [Fact]
    public void BuildEnvironment_WithNoWrongActingTokenConfigured_StillSetsTheVariableEmpty() =>
        DriverRunner.BuildEnvironment(Context() with { WrongActingToken = string.Empty })
            .Should().Contain("IVERSON_DRIVER_WRONG_ACTING_TOKEN", string.Empty);

    // A stand-in for the Python driver: the harness runs `python3 conformance/driver.py` from
    // Iverson.Clients/Python under the repo root, with no build step. Under a temporary repo root
    // this script is what runs, so RunPhaseAsync is exercised end to end without a stack.
    private static string FakePythonDriverRepo(string script)
    {
        var root = Directory.CreateTempSubdirectory("iverson-conformance-fake-").FullName;
        var conformance = Path.Combine(root, "Iverson.Clients", "Python", "conformance");
        Directory.CreateDirectory(conformance);
        File.WriteAllText(Path.Combine(conformance, "driver.py"), script);
        return root;
    }

    [Fact]
    public async Task RunPhaseAsync_StartsTheDriverWithEachSecretInItsEnvironment()
    {
        // Reports each variable back as a step key, "<unset>" when the driver did not receive it.
        var root = FakePythonDriverRepo("""
            import json, os, sys
            out = sys.argv[sys.argv.index("--out") + 1]
            names = ["IVERSON_DRIVER_CLIENT_SECRET", "IVERSON_DRIVER_SERVICE_TOKEN",
                     "IVERSON_DRIVER_ACTING_TOKEN", "IVERSON_DRIVER_WRONG_ACTING_TOKEN"]
            keys = {name: os.environ.get(name, "<unset>") for name in names}
            with open(out, "w") as f:
                json.dump({"language": "python", "phase": "register",
                           "steps": [{"name": "env", "ok": True, "keys": keys}]}, f)
            """);
        try
        {
            var runner = new DriverRunner(root);

            var outcomes = await runner.RunPhaseAsync(Phase.Register, ["python"], Context());

            outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Success>();
            runner.KeysByLanguage["python"].Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["IVERSON_DRIVER_CLIENT_SECRET"] = "client-secret",
                ["IVERSON_DRIVER_SERVICE_TOKEN"] = "service-token",
                ["IVERSON_DRIVER_ACTING_TOKEN"] = "acting-token",
                ["IVERSON_DRIVER_WRONG_ACTING_TOKEN"] = "wrong-acting-token",
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The timeout message prints the driver's command line, so it must not carry a secret either.
    [Fact]
    public async Task RunPhaseAsync_TimeoutMessage_CarriesNoSecret()
    {
        var root = FakePythonDriverRepo("import time\ntime.sleep(60)\n");
        var previous = DriverRunner.ProcessTimeout;
        DriverRunner.ProcessTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var outcomes = await new DriverRunner(root).RunPhaseAsync(Phase.Register, ["python"], Context());

            var broken = outcomes.Should().ContainSingle().Which.Should().BeOfType<DriverPhaseOutcome.Broken>().Subject;
            broken.Stderr.Should().Contain("timed out");
            foreach (var secret in new[] { "client-secret", "service-token", "acting-token", "wrong-acting-token" })
                broken.Stderr.Should().NotContain(secret);
        }
        finally
        {
            DriverRunner.ProcessTimeout = previous;
            Directory.Delete(root, recursive: true);
        }
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
