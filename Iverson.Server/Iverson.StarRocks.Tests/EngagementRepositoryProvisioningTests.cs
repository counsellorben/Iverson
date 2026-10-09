using System.Reflection;
using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 11 §3: tenant provisioning runs under its own login. On the first upgrade the
/// post-upgrade hook creates that login after the upgraded worker is already serving, so a refusal as
/// AccessDenied while opening it surfaces as <see cref="EngagementNotReadyException"/>, which the
/// projection consumers treat as transient. Every other failure keeps its own exception. A repository
/// given no provisioner connection string refuses to provision at all. No server is needed.
/// </summary>
public class EngagementRepositoryProvisioningTests
{
    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant to this
    // assembly; built via reflection against the exact overload (StarRocksResiliencePipelineFactoryTests).
    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    [Fact]
    public async Task AccessDenied_OnOpen_SurfacesAsNotReady()
    {
        var original = CreateMySqlException(MySqlErrorCode.AccessDenied,
            "Access denied for user 'iverson_provisioner'@'10.0.0.1' (using password: YES)");

        var act = () => EngagementRepository.OpenProvisionerConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeSameAs(original);
    }

    public static TheoryData<string> UnchangedCases() => new(Unchanged.Keys);

    private static readonly Dictionary<string, Func<Exception>> Unchanged = new()
    {
        ["library-transient MySqlException"] = () => CreateMySqlException(MySqlErrorCode.UnableToConnectToHost,
            "Unable to connect to any of the specified MySQL hosts."),
        ["statement-level privilege error"]  = () => CreateMySqlException((MySqlErrorCode)5203,
            "Access denied; you need (at least one of) the CREATE DATABASE privilege(s) on CATALOG default_catalog for this operation."),
        ["parse error"]                      = () => CreateMySqlException(MySqlErrorCode.ParseError,
            "You have an error in your SQL syntax"),
        ["not a MySqlException"]             = () => new InvalidOperationException("boom"),
    };

    [Theory]
    [MemberData(nameof(UnchangedCases))]
    public async Task OtherFailure_OnOpen_IsRethrownUnchanged(string name)
    {
        var original = Unchanged[name]();

        var act = () => EngagementRepository.OpenProvisionerConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(original, name);
    }

    [Fact]
    public async Task Open_Succeeds_DoesNotThrow()
    {
        var opened = false;

        await EngagementRepository.OpenProvisionerConnectionAsync(() => { opened = true; return Task.CompletedTask; });

        opened.Should().BeTrue();
    }

    [Fact]
    public async Task NoProvisionerConnectionString_RefusesProvisioningBeforeConnecting()
    {
        // Port 1 refuses at once. A refusal that came after the readiness gate instead would wait out the
        // gate's 2 s timeout and answer EngagementNotReadyException.
        var repo = new EngagementRepository("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1",
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) });
        var schema = new EngagementTableSchema("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false), []);

        var act = () => repo.EnsureTenantProvisionedAsync("t1", schema);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioner*");
    }
}
