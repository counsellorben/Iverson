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

    /// <summary>
    /// Blueprint path (relative to Authentik's blueprints dir) of the service-clients blueprint
    /// that provisions the scoped <c>iverson-admin-orchestrator</c> role, its InitialPermissions
    /// object-scoping entry, the orchestrator user and its API token — the subject of CSR round-3
    /// finding #1. The bind mount puts the repo's <c>blueprints/</c> at <c>/blueprints/custom</c>,
    /// so <c>compose-only/service-clients.yaml</c> lands here.
    /// </summary>
    private const string ServiceClientsBlueprintPath = "custom/compose-only/service-clients.yaml";
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

    /// <summary>
    /// The API token of the LEAST-PRIVILEGED <c>iverson-admin-orchestrator</c> service identity —
    /// the credential Iverson.Api actually runs with (<c>Authentik__AdminToken</c>). Its literal
    /// value is fixed by the shipped <c>compose-only/service-clients.yaml</c> blueprint, so a test
    /// using it is exercising the real, shipped RBAC role rather than a reconstruction of it. Use
    /// this — not <see cref="AdminToken"/> — for anything asserting what the orchestrator may and
    /// may not do; the bootstrap token is a superuser and would pass every check vacuously.
    /// </summary>
    public const string OrchestratorToken = "dev-only-not-for-production-admin-orchestrator-token";

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
        await EnsureServiceClientsBlueprintAppliedAsync();
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
        if (!File.Exists(Path.Combine(blueprintsDir, "compose-only", "service-clients.yaml")))
            throw new FileNotFoundException(
                $"'{blueprintsDir}' does not contain compose-only/service-clients.yaml — the blueprint " +
                "carrying the scoped iverson-admin-orchestrator role (CSR round-3 finding #1) may have been moved.");

        return blueprintsDir;
    }

    /// <summary>
    /// Applies <c>compose-only/service-clients.yaml</c> and waits for the scoped orchestrator
    /// identity it provisions to be usable.
    /// <para>
    /// Authentik's own discovery DOES pick this file up (its blueprint loader rglobs the mounted
    /// directory, subdirectories included), but on a cold start it reliably FAILS: discovery
    /// enqueues this blueprint in the same batch as Authentik's stock ones, and its
    /// <c>!Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]</c>
    /// references resolve to null while that default flow is still being created — observed
    /// directly, the instance sits at <c>status: error</c> with
    /// <c>{'authorization_flow': ['This field may not be null.']}</c>. Authentik only retries it on
    /// the next scheduled discovery pass, which is far longer than a test run. So rather than wait
    /// on the worker, this drives the SYNCHRONOUS import endpoint
    /// (<c>POST /api/v3/managed/blueprints/import/</c>, which validates and applies inline and
    /// returns the result) against the very same on-disk file, by path. It is the shipped
    /// blueprint that gets applied either way — only the trigger differs.
    /// </para>
    /// </summary>
    private async Task EnsureServiceClientsBlueprintAppliedAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BootstrapToken);

        var deadline = DateTime.UtcNow.AddMinutes(3);
        string? lastImportBody = null;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(ServiceClientsBlueprintPath), "path" }
                };
                using var importResponse = await client.PostAsync("/api/v3/managed/blueprints/import/", form);
                lastImportBody = await importResponse.Content.ReadAsStringAsync();

                if (importResponse.IsSuccessStatusCode && await OrchestratorIdentityIsUsableAsync(client))
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex; // server still warming up, or a transient apply race — keep retrying
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        throw new TimeoutException(
            "The scoped iverson-admin-orchestrator identity did not become usable within 3 minutes. " +
            $"Last import response: {Truncate(lastImportBody)}. Last error: {lastError}");
    }

    /// <summary>
    /// Functional readiness probe for the orchestrator identity: the role must exist AND its token
    /// must authenticate as a NON-superuser. The superuser check is not incidental — the previous
    /// version of this blueprint made the orchestrator's group <c>is_superuser: true</c>, which
    /// would let every "the orchestrator is refused X" assertion pass for the wrong reason (it
    /// would in fact be allowed everything). Failing readiness here is better than a green suite
    /// asserting nothing.
    /// </summary>
    private static async Task<bool> OrchestratorIdentityIsUsableAsync(HttpClient bootstrapClient)
    {
        using var roleResponse = await bootstrapClient.GetAsync(
            "/api/v3/rbac/roles/?name=iverson-admin-orchestrator");
        if (!roleResponse.IsSuccessStatusCode)
            return false;

        using var roleDoc = await JsonDocument.ParseAsync(await roleResponse.Content.ReadAsStreamAsync());
        if (roleDoc.RootElement.GetProperty("results").GetArrayLength() == 0)
            return false;

        using var orchestratorClient = new HttpClient { BaseAddress = bootstrapClient.BaseAddress };
        orchestratorClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OrchestratorToken);

        using var meResponse = await orchestratorClient.GetAsync("/api/v3/core/users/me/");
        if (!meResponse.IsSuccessStatusCode)
            return false;

        using var meDoc = await JsonDocument.ParseAsync(await meResponse.Content.ReadAsStreamAsync());
        var me = meDoc.RootElement.GetProperty("user");
        return me.GetProperty("username").GetString() == "iverson-admin-orchestrator"
               && !me.GetProperty("is_superuser").GetBoolean();
    }

    /// <summary>
    /// Enrols a confirmed TOTP device on an existing user, for the CSR round-3 finding #2
    /// assertion that an account WITH a second factor cannot complete the recovery flow without
    /// presenting it.
    /// <para>
    /// This goes through <c>ak shell</c> rather than the REST API on purpose, and it is the one
    /// place this fixture reaches past Authentik's public API: Authentik 2026.5.3 exposes no way
    /// to enrol a device ON BEHALF OF another user. <c>TOTPDeviceSerializer.user</c> is
    /// <c>read_only</c> (so neither the admin device endpoints nor a blueprint entry can set it),
    /// and <c>POST /api/v3/authenticators/admin/totp/</c> answers 405 in any case — checked
    /// against the running image, not assumed. The only API-shaped alternative would be to drive
    /// the whole interactive TOTP-setup flow, which tests the setup flow rather than the thing
    /// under test here.
    /// </para>
    /// </summary>
    public async Task EnrollTotpDeviceAsync(string username)
    {
        var script =
            "from authentik.core.models import User\n" +
            "from authentik.stages.authenticator_totp.models import TOTPDevice\n" +
            $"u = User.objects.get(username='{username}')\n" +
            "d, _ = TOTPDevice.objects.get_or_create(user=u, name='csr3-test-totp', defaults={'confirmed': True})\n" +
            "d.confirmed = True\n" +
            "d.save()\n" +
            "print('ENROLLED', d.pk)\n";

        var result = await _authentikServer.ExecAsync(["ak", "shell", "-c", script]);
        if (result.ExitCode != 0 || !result.Stdout.Contains("ENROLLED"))
            throw new InvalidOperationException(
                $"Failed to enrol a TOTP device for '{username}' (exit {result.ExitCode}).\n" +
                $"stdout: {Truncate(result.Stdout)}\nstderr: {Truncate(result.Stderr)}");
    }

    private static string Truncate(string? value) =>
        value is null ? "<none>" : value.Length <= 2000 ? value : value[..2000] + "…";

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
