using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// Brings up a real, blueprint-equipped Authentik instance (Postgres + Redis + the
/// authentik-migrate/-server/-worker trio, mirroring <c>docker-compose.yml</c>'s own sequencing
/// and its documented deadlock-avoidance) for CSR round-2 finding #4's regression coverage.
///
/// WHY THIS FIXTURE EXISTS. The live check performed during the CSR round-2 remediation
/// (2026-09-11) found that the #4 fix as first committed (<c>94a05ae</c>) was a functional
/// REGRESSION: <c>IdpAdminClient.CreateUserAsync</c> called Authentik's <c>/recovery/</c>
/// endpoint, but Authentik ships no recovery flow and none was provisioned, so every real
/// onboarding call failed with <c>{"non_field_errors":"No recovery flow set."}</c>. The entire
/// mocked unit suite (<see cref="AuthentikAdminClientTests"/> included) was green throughout,
/// because it mocks the HTTP layer and so has no way to know whether a real Authentik instance
/// actually has the flow the code depends on. This fixture closes that blind spot by running the
/// real dependency, applying the real blueprint (<c>deploy/helm/iverson/charts/authentik/
/// blueprints/recovery-flow.yaml</c>) this platform ships, and proving the onboarding call
/// succeeds against it — see <see cref="AuthentikRecoveryFlowIntegrationTests"/>.
///
/// Sequencing mirrors compose exactly (see its own extensive comment on the deadlock this
/// avoids): ONE process (authentik-migrate) runs the schema migration to completion BEFORE
/// authentik-server/-worker start with <c>AUTHENTIK_SKIP_MIGRATIONS=true</c>, so no two Authentik
/// processes ever race to migrate the same fresh database.
///
/// Blueprint reconciliation (picking up <c>recovery-flow.yaml</c> from the bind-mounted
/// <c>/blueprints/custom</c> directory) runs on the WORKER process, not the server — verified
/// 2026-09-11 against the kind laptop bring-up, whose worker pod logs showed the blueprint
/// "Applying"/"successful" events; the server pod's logs showed none. Both must run.
/// </summary>
public sealed class AuthentikContainerFixture : IAsyncLifetime
{
    private const string AuthentikImage = "ghcr.io/goauthentik/server:2026.5.3"; // pinned to match docker-compose.yml / the Helm chart's default
    private const string AuthentikDbName = "authentik";
    private const string AuthentikDbUser = "authentik";
    private const string AuthentikDbPassword = "authentik";
    private const string SecretKey = "test-only-not-for-production-use-0123456789abcdef";
    private const string BootstrapToken = "test-only-bootstrap-token-0123456789abcdef";
    private const string BootstrapEmail = "admin@iverson-test.invalid";
    private const string BootstrapPassword = "test-only-bootstrap-password-0123456789";
    private const int AuthentikPort = 9000;

    private readonly INetwork _network = new NetworkBuilder().Build();

    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _redis;

    public AuthentikContainerFixture()
    {
        // Network aliases below ("postgres"/"redis") are what every Authentik env var
        // (AUTHENTIK_POSTGRESQL__HOST / AUTHENTIK_REDIS__HOST) points at — matching
        // docker-compose.yml's own service-name-as-hostname convention exactly. WithNetwork(...)
        // is a BUILDER method, unavailable once .Build() has produced the container, so both
        // must be applied here rather than in InitializeAsync.
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase(AuthentikDbName)
            .WithUsername(AuthentikDbUser)
            .WithPassword(AuthentikDbPassword)
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .Build();

        _redis = new ContainerBuilder()
            .WithImage("redis:7.4-alpine")
            .WithCommand("redis-server", "--save", "")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
    }

    private IContainer _authentikServer = null!;
    private IContainer _authentikWorker = null!;

    /// <summary>Base URL of the running Authentik instance, reachable from the test host.</summary>
    public string BaseUrl { get; private set; } = null!;

    /// <summary>The API bootstrap token (superuser-equivalent), for constructing an <see cref="Iverson.Api.Tenancy.IdpAdminClient"/> against this instance.</summary>
    public string AdminToken => BootstrapToken;

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await RunMigrationToCompletionAsync();

        var blueprintsDir = BlueprintsDirectory();

        _authentikServer = BuildAuthentikContainer("server", exposePort: true, blueprintsDir);
        _authentikWorker = BuildAuthentikContainer("worker", exposePort: false, blueprintsDir);
        await Task.WhenAll(_authentikServer.StartAsync(), _authentikWorker.StartAsync());

        BaseUrl = $"http://{_authentikServer.Hostname}:{_authentikServer.GetMappedPublicPort(AuthentikPort)}";

        await WaitForRecoveryFlowBlueprintAsync();
    }

    public async Task DisposeAsync()
    {
        // Reverse start order; each disposal is independently best-effort so one failure doesn't
        // orphan the rest (mirrors the "each class keeps its own IClassFixture" isolation
        // ContainerCollection documents — this fixture's containers are its own to clean up).
        await SafeDisposeAsync(_authentikWorker);
        await SafeDisposeAsync(_authentikServer);
        await SafeDisposeAsync(_redis);
        await SafeDisposeAsync(_postgres);
        await _network.DeleteAsync();
    }

    private static async Task SafeDisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is not null)
            await resource.DisposeAsync();
    }

    /// <summary>
    /// Runs <c>python -m lifecycle.migrate</c> to completion in its OWN container, then asserts
    /// exit code 0, before any authentik-server/-worker container starts. See class remarks:
    /// this is the deadlock docker-compose.yml's own comment documents avoiding.
    /// </summary>
    private async Task RunMigrationToCompletionAsync()
    {
        var migrate = new ContainerBuilder()
            .WithImage(AuthentikImage)
            .WithNetwork(_network)
            .WithEntrypoint("python", "-m", "lifecycle.migrate")
            .WithEnvironment("AUTHENTIK_SECRET_KEY", SecretKey)
            .WithEnvironment("AUTHENTIK_REDIS__HOST", "redis")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgres")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", AuthentikDbName)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", AuthentikDbUser)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", AuthentikDbPassword)
            .WithWaitStrategy(Wait.ForUnixContainer()) // no readiness wait; we poll for exit below
            .Build();

        await migrate.StartAsync();

        // Testcontainers has no built-in "wait for exit" strategy (its wait strategies target a
        // long-running container becoming ready, not a one-shot batch job finishing). The first
        // attempt at this polled `migrate.State != TestcontainersStates.Exited` instead of the
        // exit code directly — under this suite's rootless-Podman backend, `.State` never
        // observably flipped to Exited even though the container's own logs proved it exited
        // cleanly in ~2 minutes (a real run's captured log ran waiting-to-acquire-lock through
        // releasing-database-lock in 2m09s while the `.State`-based loop still timed out at 6
        // minutes). Polling GetExitCodeAsync() directly — it throws while the container is still
        // running, and returns once it has actually exited — sidesteps whatever staleness
        // `.State` has here.
        long? exitCode = null;
        Exception? lastPollError = null;
        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (exitCode is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                var timeoutLogs = await migrate.GetLogsAsync();
                await migrate.DisposeAsync(); // don't orphan the container on our own timeout path
                throw new TimeoutException(
                    $"authentik-migrate did not exit within 6 minutes (last poll error: {lastPollError}). " +
                    $"Logs:\n{timeoutLogs.Stdout}\n{timeoutLogs.Stderr}");
            }

            try
            {
                exitCode = await migrate.GetExitCodeAsync();
            }
            catch (Exception ex)
            {
                lastPollError = ex; // expected while the container is still running; keep polling
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        if (exitCode != 0)
        {
            var logs = await migrate.GetLogsAsync();
            throw new InvalidOperationException(
                $"authentik-migrate exited with code {exitCode}. Logs:\n{logs.Stdout}\n{logs.Stderr}");
        }

        await migrate.DisposeAsync();
    }

    private IContainer BuildAuthentikContainer(string command, bool exposePort, string blueprintsDir)
    {
        var builder = new ContainerBuilder()
            .WithImage(AuthentikImage)
            .WithNetwork(_network)
            .WithCommand(command)
            .WithEnvironment("AUTHENTIK_SECRET_KEY", SecretKey)
            .WithEnvironment("AUTHENTIK_REDIS__HOST", "redis")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgres")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", AuthentikDbName)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", AuthentikDbUser)
            .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", AuthentikDbPassword)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", BootstrapEmail)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", BootstrapPassword)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", BootstrapToken)
            .WithEnvironment("AUTHENTIK_SKIP_MIGRATIONS", "true") // authentik-migrate already owns migrations
            .WithBindMount(blueprintsDir, "/blueprints/custom", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("ak", "healthcheck"));

        if (exposePort)
            builder = builder.WithPortBinding(AuthentikPort, true);

        return builder.Build();
    }

    /// <summary>
    /// Locates this platform's real recovery-flow blueprint directory from the test source
    /// file's own path — robust regardless of build output layout, and always the actual
    /// shipped blueprint (not a copy), so this test fails if that file is ever broken or moved.
    /// </summary>
    private static string BlueprintsDirectory([CallerFilePath] string thisFilePath = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(thisFilePath))!; // .../Iverson.Api.Tests
        var serverDir = Path.GetDirectoryName(testsDir)!; // .../Iverson.Server
        var blueprintsDir = Path.Combine(serverDir, "deploy", "helm", "iverson", "charts", "authentik", "blueprints");

        if (!Directory.Exists(blueprintsDir))
            throw new DirectoryNotFoundException(
                $"Expected Authentik blueprints directory at '{blueprintsDir}' — repo layout may have changed.");
        if (!File.Exists(Path.Combine(blueprintsDir, "recovery-flow.yaml")))
            throw new FileNotFoundException(
                $"'{blueprintsDir}' does not contain recovery-flow.yaml — the CSR round-2 finding #4 blueprint fix may have been reverted or moved.");

        return blueprintsDir;
    }

    /// <summary>
    /// Blueprint application by the worker is asynchronous relative to container startup — poll
    /// for its actual, functional effect (a probe user's recovery link succeeding) rather than
    /// any specific log line or internal API shape, so this doesn't couple to Authentik
    /// internals beyond what <see cref="Iverson.Api.Tenancy.IdpAdminClient"/> itself already
    /// depends on. This is the exact failure mode the 2026-09-11 live check caught: without it,
    /// this call returns <c>{"non_field_errors":"No recovery flow set."}</c> instead of a link.
    /// </summary>
    private async Task WaitForRecoveryFlowBlueprintAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BootstrapToken);

        var probeBody = new StringContent(
            JsonSerializer.Serialize(new
            {
                username = "readiness-probe",
                email = "readiness-probe@iverson-test.invalid",
                name = "readiness-probe",
                is_active = true
            }),
            Encoding.UTF8,
            "application/json");

        string? probeUserId = null;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (probeUserId is null)
                {
                    using var createResponse = await client.PostAsync("/api/v3/core/users/", probeBody);
                    if (createResponse.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
                        var pk = doc.RootElement.GetProperty("pk");
                        probeUserId = pk.ValueKind == JsonValueKind.Number ? pk.GetRawText() : pk.GetString();
                    }
                }

                if (probeUserId is not null)
                {
                    using var recoveryResponse = await client.PostAsync(
                        $"/api/v3/core/users/{probeUserId}/recovery/",
                        new StringContent("{}", Encoding.UTF8, "application/json"));

                    if (recoveryResponse.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await recoveryResponse.Content.ReadAsStreamAsync());
                        if (doc.RootElement.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
                            return; // recovery flow is live and provisioned — fixture is ready
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex; // server/worker still warming up — keep retrying
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            "Authentik's recovery flow did not become available within 2 minutes " +
            "(the exact CSR round-2 finding #4 regression this fixture exists to catch). " +
            $"Last error: {lastError}");
    }
}
