using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// TenantAdminGrpcService is the most security-sensitive service in this plan: it is a delegated
// PER-TENANT admin surface, so every RPC must (a) resolve the caller's own tenant strictly from
// the PRIMARY authenticated principal — never IActingUserAccessor, which this service does not
// even take as a dependency, so there is nothing to accidentally fall back to — and (b) refuse
// to act on any user_id that doesn't belong to that tenant. These tests exercise both guarantees
// directly, plus the inline suspended-tenant check the service performs itself (rather than
// relying on ActingUserInterceptor, whose check never fires here since it's gated on an
// acting-user header this service's caller never sends).
public class TenantAdminGrpcServiceTests
{
    private readonly IAuthentikAdminClient _authentikAdminClient = Substitute.For<IAuthentikAdminClient>();
    private readonly ITenantStatusCache _tenantStatusCache = Substitute.For<ITenantStatusCache>();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly Iverson.Api.Grpc.TenantAdminGrpcService _sut;

    private static readonly AuthentikUser CallerTenantUser = new("user-1", "alice", "alice@acme.example");
    private static readonly AuthentikUser OtherTenantUser = new("user-99", "mallory", "mallory@globex.example");

    public TenantAdminGrpcServiceTests()
    {
        _auditLog = new AuditLog(_auditLogger);
        _sut = new Iverson.Api.Grpc.TenantAdminGrpcService(_authentikAdminClient, _tenantStatusCache, _auditLog);
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("active"));
    }

    // The caller's tenant is carried on the PRIMARY principal's "tenant_id" claim. No acting-user
    // header/accessor is involved anywhere in these tests — proving RequireActiveTenantAsync
    // reads the right source, since there is no other source it could plausibly read from.
    private static TestServerCallContext ContextForCallerTenant() =>
        TestServerCallContext.Create(user: ActingUserFixtures.PrincipalWithTenant("test-tenant-admin", "acme"));

    // ── InviteUser ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InviteUser_ActiveTenant_CreatesAuthentikUserScopedToCallerTenantAndReturnsTenantUser()
    {
        var request = new InviteUserRequest
        {
            Username = "bob",
            Email = "bob@acme.example",
            InitialPassword = "correct-horse-battery-staple"
        };
        _authentikAdminClient.CreateUserAsync(
            "bob", "bob@acme.example", "correct-horse-battery-staple", "acme",
            Arg.Is<IReadOnlyList<string>>(g => g.Count == 0)).Returns(Task.FromResult("user-1"));

        var response = await _sut.InviteUser(request, ContextForCallerTenant());

        response.UserId.Should().Be("user-1");
        response.Username.Should().Be("bob");
        response.Email.Should().Be("bob@acme.example");
        await _authentikAdminClient.Received(1).CreateUserAsync(
            "bob", "bob@acme.example", "correct-horse-battery-staple", "acme",
            Arg.Is<IReadOnlyList<string>>(g => g.Count == 0));
    }

    [Fact]
    public async Task InviteUser_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotCreateUser()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));
        var request = new InviteUserRequest { Username = "bob", Email = "bob@acme.example", InitialPassword = "pw" };

        var act = () => _sut.InviteUser(request, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().CreateUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task InviteUser_NoTenantIdClaim_ThrowsPermissionDeniedAndDoesNotCreateUser()
    {
        var request = new InviteUserRequest { Username = "bob", Email = "bob@acme.example", InitialPassword = "pw" };
        var context = TestServerCallContext.Create(user: ActingUserFixtures.PrincipalWithTenant("no-tenant-user", null));

        var act = () => _sut.InviteUser(request, context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().CreateUserAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
    }

    // ── ListUsers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_ActiveTenant_ReturnsUsersFromCallerTenantOnly()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        var response = await _sut.ListUsers(new ListUsersRequest(), ContextForCallerTenant());

        response.Users.Should().HaveCount(1);
        response.Users[0].UserId.Should().Be("user-1");
        response.Users[0].Username.Should().Be("alice");
        await _authentikAdminClient.Received(1).ListUsersByTenantAsync("acme");
    }

    [Fact]
    public async Task ListUsers_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotQueryAuthentik()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));

        var act = () => _sut.ListUsers(new ListUsersRequest(), ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().ListUsersByTenantAsync(Arg.Any<string>());
    }

    // ── RemoveUser ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveUser_UserBelongsToCallerTenant_Deactivates()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        await _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).DeactivateUserAsync("user-1");
    }

    // Cross-tenant escalation guard: this is the single most important test in this task. A
    // tenant-admin of "acme" must not be able to deactivate a user who belongs to a different
    // tenant, even though user_id (unlike tenant_id) is entirely caller-supplied. The target
    // user_id here ("user-99") is deliberately NOT present in ListUsersByTenantAsync("acme")'s
    // result set (it "belongs" to some other tenant, e.g. globex) — RequireUserInTenantAsync
    // must reject this before DeactivateUserAsync is ever called.
    [Fact]
    public async Task RemoveUser_UserBelongsToDifferentTenant_ThrowsPermissionDeniedAndDoesNotDeactivate()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = OtherTenantUser.Id }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RemoveUser_SuspendedTenant_ThrowsPermissionDeniedBeforeCheckingUserOrDeactivating()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().ListUsersByTenantAsync(Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    // ── SetTenantAdmin ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetTenantAdmin_GrantForUserInCallerTenant_AddsGroupAndReturnsUser()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        var response = await _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = "user-1", Grant = true }, ContextForCallerTenant());

        response.UserId.Should().Be("user-1");
        await _authentikAdminClient.Received(1).AddGroupAsync("user-1", "tenant-admins");
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetTenantAdmin_RevokeForUserInCallerTenant_RemovesGroup()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "user-1", Grant = false }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).RemoveGroupAsync("user-1", "tenant-admins");
        await _authentikAdminClient.DidNotReceive().AddGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // Same cross-tenant escalation guard as RemoveUser, for the promote/demote path: a
    // tenant-admin of "acme" must not be able to grant or revoke tenant-admin rights for a user
    // who belongs to a different tenant.
    [Fact]
    public async Task SetTenantAdmin_UserBelongsToDifferentTenant_ThrowsPermissionDeniedAndDoesNotChangeGroups()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([CallerTenantUser]));

        var act = () => _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = OtherTenantUser.Id, Grant = true }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().AddGroupAsync(Arg.Any<string>(), Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetTenantAdmin_SuspendedTenant_ThrowsPermissionDeniedBeforeCheckingUserOrChangingGroups()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));

        var act = () => _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = "user-1", Grant = true }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().ListUsersByTenantAsync(Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().AddGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}
