using FluentAssertions;
using Iverson.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round-2 finding #4 regression coverage: proves <see cref="IdpAdminClient.CreateUserAsync"/>
/// actually obtains a working recovery link from a REAL, blueprint-equipped Authentik instance —
/// not a mock. See <see cref="AuthentikContainerFixture"/>'s remarks for why this class exists:
/// the mocked <see cref="AuthentikAdminClientTests"/> suite stayed green through the entire
/// window where this call was silently broken against every real deployment, because a mock has
/// no way to know whether the real dependency (a provisioned recovery flow) exists.
///
/// This is deliberately the ONLY integration test in this assembly that runs a real Authentik —
/// heavier than the rest of the suite (5 containers: network + Postgres + Redis + authentik-
/// migrate + authentik-server + authentik-worker) and gated behind <see cref="ContainerCollection"/>
/// like every other container-backed test class here, so it never runs concurrently with them.
///
/// Uses <see cref="IClassFixture{TFixture}"/>, NOT per-test IAsyncLifetime on this class: xunit
/// creates a fresh instance of the TEST CLASS per [Fact], so an IAsyncLifetime fixture built in
/// the class itself gets rebuilt (and its whole 5-container stack re-created) once per [Fact] —
/// observed directly running this file with 2 [Fact]s: two full Authentik stacks came up
/// concurrently, contended for this box's 4 cores, and both blew the migrate-completion timeout.
/// IClassFixture shares ONE fixture instance across every [Fact] in the class instead.
/// </summary>
[Collection(ContainerCollection.Name)]
public sealed class AuthentikRecoveryFlowIntegrationTests : IClassFixture<AuthentikContainerFixture>
{
    private readonly IdpAdminClient _sut;

    public AuthentikRecoveryFlowIntegrationTests(AuthentikContainerFixture authentik)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(IdpAdminClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(authentik.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authentik.AdminToken);
        });
        var provider = services.BuildServiceProvider();

        _sut = new IdpAdminClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<IdpAdminClient>.Instance);
    }

    [Fact]
    public async Task CreateUserAsync_AgainstRealAuthentikWithShippedBlueprint_ReturnsWorkingRecoveryLink()
    {
        var result = await _sut.CreateUserAsync(
            "csr4-regression-test-user",
            "csr4-regression-test-user@iverson-test.invalid",
            "csr4-regression-tenant",
            groups: []);

        result.UserId.Should().NotBeNullOrEmpty();

        // This is the exact assertion that would have failed against the 94a05ae regression:
        // Authentik's /recovery/ response there was {"non_field_errors":"No recovery flow set."},
        // which IdpAdminClient's TriggerPasswordRecoveryAsync treats as "no link" (logs a
        // warning, returns null) rather than throwing — so RecoveryLink being non-null here is
        // the one signal a real Authentik dependency was actually satisfied.
        result.RecoveryLink.Should().NotBeNullOrEmpty(
            "the platform's own recovery-flow.yaml blueprint must be applied and reachable; " +
            "a null link here means the same regression CSR round-2 finding #4's live check caught");
        result.RecoveryLink.Should().Contain(
            "/if/flow/iverson-recovery/",
            "the link must come from THIS platform's own recovery flow, not some other default");
    }

    [Fact]
    public async Task CreateUserAsync_TwoUsers_EachGetsADistinctRecoveryLink()
    {
        // Guards against a trivial-but-real failure mode: a cached/static link, or a handler bug
        // that returns the readiness probe's own link instead of freshly minting one per call.
        var first = await _sut.CreateUserAsync(
            "csr4-regression-user-a", "csr4-regression-user-a@iverson-test.invalid", "csr4-regression-tenant", []);
        var second = await _sut.CreateUserAsync(
            "csr4-regression-user-b", "csr4-regression-user-b@iverson-test.invalid", "csr4-regression-tenant", []);

        first.RecoveryLink.Should().NotBeNullOrEmpty();
        second.RecoveryLink.Should().NotBeNullOrEmpty();
        first.RecoveryLink.Should().NotBe(second.RecoveryLink);
    }
}
