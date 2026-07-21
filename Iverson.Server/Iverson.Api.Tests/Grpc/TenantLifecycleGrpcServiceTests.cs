using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class TenantLifecycleGrpcServiceTests
{
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IAuthentikAdminClient _authentikAdminClient = Substitute.For<IAuthentikAdminClient>();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly Iverson.Api.Grpc.TenantLifecycleGrpcService _sut;

    private static readonly TenantRow ExistingTenant =
        new("acme", "Acme Corp", "active", DateTimeOffset.UtcNow);

    public TenantLifecycleGrpcServiceTests()
    {
        _auditLog = new AuditLog(_auditLogger);
        _sut = new Iverson.Api.Grpc.TenantLifecycleGrpcService(_tenantRepository, _authentikAdminClient, _auditLog);
    }

    private static TestServerCallContext ContextWithUser() =>
        TestServerCallContext.Create(user: ActingUserFixtures.Principal("test-operator"));

    // ── CreateTenant ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTenant_ValidRequest_InsertsTenantCreatesAuthentikUserAndReturnsTenant()
    {
        var request = new CreateTenantRequest
        {
            TenantId = "acme",
            DisplayName = "Acme Corp",
            AdminUsername = "acme-admin",
            AdminEmail = "admin@acme.example",
            AdminInitialPassword = "correct-horse-battery-staple"
        };

        var response = await _sut.CreateTenant(request, ContextWithUser());

        response.TenantId.Should().Be("acme");
        response.DisplayName.Should().Be("Acme Corp");
        response.Status.Should().Be("active");

        await _tenantRepository.Received(1).InsertAsync("acme", "Acme Corp", "active");
        await _authentikAdminClient.Received(1).CreateUserAsync(
            "acme-admin", "admin@acme.example", "correct-horse-battery-staple", "acme",
            Arg.Is<IReadOnlyList<string>>(g => g.Contains("tenant-admins")));
        await _tenantRepository.DidNotReceive().DeleteAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CreateTenant_InvalidTenantId_ThrowsInvalidArgumentAndTouchesNoDependency()
    {
        var request = new CreateTenantRequest
        {
            TenantId = "not a valid id; DROP TABLE--",
            DisplayName = "Bad Tenant",
            AdminUsername = "bad-admin",
            AdminEmail = "admin@bad.example",
            AdminInitialPassword = "pw"
        };

        var act = () => _sut.CreateTenant(request, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        await _tenantRepository.DidNotReceive().InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().CreateUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task CreateTenant_AuthentikCreateUserFails_CompensatingDeletesTenantAndRethrows()
    {
        var request = new CreateTenantRequest
        {
            TenantId = "acme",
            DisplayName = "Acme Corp",
            AdminUsername = "acme-admin",
            AdminEmail = "admin@acme.example",
            AdminInitialPassword = "correct-horse-battery-staple"
        };
        var authentikFailure = new InvalidOperationException("Authentik is unreachable");
        _authentikAdminClient
            .CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>())
            .Returns<Task<string>>(_ => throw authentikFailure);

        var act = () => _sut.CreateTenant(request, ContextWithUser());

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Should().BeSameAs(authentikFailure);

        await _tenantRepository.Received(1).InsertAsync("acme", "Acme Corp", "active");
        await _tenantRepository.Received(1).DeleteAsync("acme");
        // The audit log must not record a "CreateTenant" success for a tenant that was rolled back.
        _auditLogger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── ListTenants ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTenants_ReturnsAllTenantsFromRepository()
    {
        var tenants = new[]
        {
            new TenantRow("acme", "Acme Corp", "active", DateTimeOffset.UtcNow),
            new TenantRow("globex", "Globex", "suspended", DateTimeOffset.UtcNow)
        };
        _tenantRepository.ListAsync().Returns(Task.FromResult<IEnumerable<TenantRow>>(tenants));

        var response = await _sut.ListTenants(new ListTenantsRequest(), TestServerCallContext.Create());

        response.Tenants.Should().HaveCount(2);
        response.Tenants.Should().Contain(t => t.TenantId == "acme" && t.Status == "active");
        response.Tenants.Should().Contain(t => t.TenantId == "globex" && t.Status == "suspended");
    }

    // ── SuspendTenant ────────────────────────────────────────────────────────

    [Fact]
    public async Task SuspendTenant_ExistingTenant_UpdatesStatusAndReturnsSuspendedTenant()
    {
        _tenantRepository.GetAsync("acme").Returns(Task.FromResult<TenantRow?>(ExistingTenant));

        var response = await _sut.SuspendTenant(new SuspendTenantRequest { TenantId = "acme" }, ContextWithUser());

        response.TenantId.Should().Be("acme");
        response.Status.Should().Be("suspended");
        await _tenantRepository.Received(1).UpdateStatusAsync("acme", "suspended");
    }

    [Fact]
    public async Task SuspendTenant_UnknownTenant_ThrowsNotFoundAndDoesNotUpdate()
    {
        _tenantRepository.GetAsync("ghost").Returns(Task.FromResult<TenantRow?>(null));

        var act = () => _sut.SuspendTenant(new SuspendTenantRequest { TenantId = "ghost" }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        await _tenantRepository.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ── ReactivateTenant ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReactivateTenant_ExistingTenant_UpdatesStatusAndReturnsActiveTenant()
    {
        var suspended = ExistingTenant with { Status = "suspended" };
        _tenantRepository.GetAsync("acme").Returns(Task.FromResult<TenantRow?>(suspended));

        var response = await _sut.ReactivateTenant(new ReactivateTenantRequest { TenantId = "acme" }, ContextWithUser());

        response.TenantId.Should().Be("acme");
        response.Status.Should().Be("active");
        await _tenantRepository.Received(1).UpdateStatusAsync("acme", "active");
    }

    [Fact]
    public async Task ReactivateTenant_UnknownTenant_ThrowsNotFoundAndDoesNotUpdate()
    {
        _tenantRepository.GetAsync("ghost").Returns(Task.FromResult<TenantRow?>(null));

        var act = () => _sut.ReactivateTenant(new ReactivateTenantRequest { TenantId = "ghost" }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        await _tenantRepository.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ── DeleteTenant ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTenant_ExistingTenant_MarksDeletedAndDeactivatesAuthentikUsers()
    {
        _tenantRepository.GetAsync("acme").Returns(Task.FromResult<TenantRow?>(ExistingTenant));

        await _sut.DeleteTenant(new DeleteTenantRequest { TenantId = "acme" }, ContextWithUser());

        await _tenantRepository.Received(1).UpdateStatusAsync("acme", "deleted");
        await _authentikAdminClient.Received(1).DeactivateAllUsersInTenantAsync("acme");
    }

    [Fact]
    public async Task DeleteTenant_UnknownTenant_ThrowsNotFoundAndTouchesNoDependency()
    {
        _tenantRepository.GetAsync("ghost").Returns(Task.FromResult<TenantRow?>(null));

        var act = () => _sut.DeleteTenant(new DeleteTenantRequest { TenantId = "ghost" }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        await _tenantRepository.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().DeactivateAllUsersInTenantAsync(Arg.Any<string>());
    }
}
