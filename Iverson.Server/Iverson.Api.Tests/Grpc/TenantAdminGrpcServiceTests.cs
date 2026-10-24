using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
    private readonly IIdpAdminClient _authentikAdminClient = Substitute.For<IIdpAdminClient>();
    private readonly ITenantStatusCache _tenantStatusCache = Substitute.For<ITenantStatusCache>();
    private readonly ITokenRevocationRepository _tokenRevocations = Substitute.For<ITokenRevocationRepository>();
    private readonly ITokenRevocationCache _tokenRevocationCache = Substitute.For<ITokenRevocationCache>();
    private readonly IInviteQuotaRepository _inviteQuota = Substitute.For<IInviteQuotaRepository>();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly Iverson.Api.Grpc.TenantAdminGrpcService _sut;

    // Uid deliberately differs from Id: tokens carry the uid as their sub, so revocation must be
    // keyed by it, not by the Authentik pk the RPCs take as user_id. Ids are Authentik pks: CSR
    // round 12 §2.1 refuses any user_id that is not a canonical positive decimal integer.
    private static readonly IdpUser CallerTenantListedUser = new("11", "alice", "alice@acme.example", "uid-alice");
    private static readonly IdpUserRecord CallerTenantUser = new("11", "alice", "alice@acme.example", "uid-alice", true, [], "acme");
    private static readonly IdpUserRecord OtherTenantUser = new("99", "mallory", "mallory@globex.example", "uid-mallory", true, [], "globex");

    public TenantAdminGrpcServiceTests()
    {
        _auditLog = new AuditLog(_auditLogger);
        _sut = new Iverson.Api.Grpc.TenantAdminGrpcService(
            _authentikAdminClient,
            _tenantStatusCache,
            _tokenRevocations,
            _tokenRevocationCache,
            _inviteQuota,
            _auditLog);
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("active"));
        // A slot is free unless a test says otherwise.
        _inviteQuota.TryReserveAsync(Arg.Any<string>(), Arg.Any<DateOnly>()).Returns(true);
        // Any other pk reads as Authentik's 404: GetUserAsync returns null.
        _authentikAdminClient.GetUserAsync("11").Returns(Task.FromResult<IdpUserRecord?>(CallerTenantUser));
        _authentikAdminClient.GetUserAsync("99").Returns(Task.FromResult<IdpUserRecord?>(OtherTenantUser));
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
            Email = "bob@acme.example"
        };
        _authentikAdminClient
            .CreateUserAsync(
                "bob",
                "bob@acme.example",
                "acme",
                Arg.Is<IReadOnlyList<string>>(g => g.Count == 0))
            .Returns(Task.FromResult(new CreateUserResult("11", "https://authentik.example/if/flow/iverson-recovery/?flow_token=abc")));

        var response = await _sut.InviteUser(request, ContextForCallerTenant());

        response.UserId.Should().Be("11");
        response.Username.Should().Be("bob");
        response.Email.Should().Be("bob@acme.example");
        response.RecoveryLink.Should().Be("https://authentik.example/if/flow/iverson-recovery/?flow_token=abc");
        await _authentikAdminClient.Received(1)
            .CreateUserAsync(
                "bob",
                "bob@acme.example",
                "acme",
                Arg.Is<IReadOnlyList<string>>(g => g.Count == 0));
    }

    [Fact]
    public async Task InviteUser_RecoveryLinkAbsent_ReturnsEmptyRecoveryLinkNotNull()
    {
        // IdpAdminClient returns a null RecoveryLink when Authentik's recovery response didn't
        // contain a link (logged as a warning there). The proto field is a plain string — never
        // null — so the handler must coalesce, not propagate null into the response.
        var request = new InviteUserRequest { Username = "bob", Email = "bob@acme.example" };
        _authentikAdminClient
            .CreateUserAsync("bob", "bob@acme.example", "acme", Arg.Any<IReadOnlyList<string>>())
            .Returns(Task.FromResult(new CreateUserResult("11", null)));

        var response = await _sut.InviteUser(request, ContextForCallerTenant());

        response.RecoveryLink.Should().Be(string.Empty);
    }

    [Fact]
    public async Task InviteUser_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotCreateUser()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));
        var request = new InviteUserRequest { Username = "bob", Email = "bob@acme.example" };

        var act = () => _sut.InviteUser(request, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        // An inactive tenant spends no slot of its daily invitation limit.
        await _inviteQuota.DidNotReceiveWithAnyArgs().TryReserveAsync(default!, default);
        await _authentikAdminClient.DidNotReceive()
            .CreateUserAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task InviteUser_NoTenantIdClaim_ThrowsPermissionDeniedAndDoesNotCreateUser()
    {
        var request = new InviteUserRequest { Username = "bob", Email = "bob@acme.example" };
        var context = TestServerCallContext.Create(user: ActingUserFixtures.PrincipalWithTenant("no-tenant-user", null));

        var act = () => _sut.InviteUser(request, context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive()
            .CreateUserAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>());
    }

    // CSR round 12 §2.1: 100 invitations per tenant per UTC day. The slot is reserved before
    // Authentik is called.
    [Fact]
    public async Task InviteUser_ReservesTodaysSlotForTheCallersTenant_BeforeCallingTheIdp()
    {
        var before = DateOnly.FromDateTime(DateTime.UtcNow);
        _authentikAdminClient.CreateUserAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromResult(new CreateUserResult("11", null)));

        await _sut.InviteUser(new InviteUserRequest { Username = "bob", Email = "bob@acme.example" }, ContextForCallerTenant());

        var after = DateOnly.FromDateTime(DateTime.UtcNow);
        Received.InOrder(() =>
        {
            _inviteQuota.TryReserveAsync("acme", Arg.Any<DateOnly>());
            _authentikAdminClient.CreateUserAsync("bob", "bob@acme.example", "acme", Arg.Any<IReadOnlyList<string>>());
        });
        var day = (DateOnly)_inviteQuota.ReceivedCalls().Single().GetArguments()[1]!;
        new[] { before, after }.Should().Contain(day, "the slot belongs to the current UTC day");
    }

    [Fact]
    public async Task InviteUser_DailyLimitReached_ThrowsResourceExhaustedAndDoesNotCreateUser()
    {
        _inviteQuota.TryReserveAsync("acme", Arg.Any<DateOnly>()).Returns(false);

        var act = () => _sut.InviteUser(
            new InviteUserRequest { Username = "bob", Email = "bob@acme.example" }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        ex.Which.Status.Detail.Should().Contain("daily invitation limit");
        await _authentikAdminClient.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!);
    }

    // Choice 6: a slot is spent even when Authentik then fails. Nothing gives it back.
    [Fact]
    public async Task InviteUser_IdpFails_TheReservedSlotStaysSpent()
    {
        _authentikAdminClient.CreateUserAsync(default!, default!, default!, default!)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("authentik is down"));

        var act = () => _sut.InviteUser(
            new InviteUserRequest { Username = "bob", Email = "bob@acme.example" }, ContextForCallerTenant());

        await act.Should().ThrowAsync<HttpRequestException>();
        _inviteQuota.ReceivedCalls().Should().ContainSingle()
            .Which.GetMethodInfo().Name.Should().Be(nameof(IInviteQuotaRepository.TryReserveAsync));
    }

    // ── ListUsers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_ActiveTenant_ReturnsUsersFromCallerTenantOnly()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantListedUser]));

        var response = await _sut.ListUsers(new ListUsersRequest(), ContextForCallerTenant());

        response.Users.Should().HaveCount(1);
        response.Users[0].UserId.Should().Be("11");
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
        await _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).DeactivateUserAsync("11");
    }

    // CSR round 12 §2.1: the target is read by its pk. Nothing lists the directory.
    [Fact]
    public async Task RemoveUser_ReadsTheTargetByPk_AndNeverListsTheDirectory()
    {
        await _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).GetUserAsync("11");
        await _authentikAdminClient.DidNotReceive().ListUsersByTenantAsync(Arg.Any<string>());
    }

    // CSR round 12 §2.1: the IdP writes take the pk of the record the tenant check read, not the
    // request's string.
    [Fact]
    public async Task RemoveUser_DeactivatesThePkOfTheRecordItChecked()
    {
        _authentikAdminClient.GetUserAsync("12")
            .Returns(Task.FromResult<IdpUserRecord?>(CallerTenantUser with { Id = "13" }));

        await _sut.RemoveUser(new RemoveUserRequest { UserId = "12" }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).DeactivateUserAsync("13");
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync("12");
    }

    // CSR round-10 #11: revoke first. If Authentik then fails, the user only has to log in again;
    // the reverse order could leave a deactivated user's tokens valid. CSR round 11 #13: revoke
    // again afterwards, for a token refreshed between the first revoke and the deactivation.
    [Fact]
    public async Task RemoveUser_RevokesTheUsersTokensBeforeAndAfterDeactivatingInAuthentik()
    {
        await _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _authentikAdminClient.DeactivateUserAsync("11");
            _tokenRevocations.RevokeAsync("uid-alice");
        });
    }

    // This process's revocation cache is invalidated after each revoke so it takes effect here at once.
    [Fact]
    public async Task RemoveUser_InvalidatesTheRevocationCacheAfterEachRevoke()
    {
        await _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
            _authentikAdminClient.DeactivateUserAsync("11");
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
        });
    }

    [Fact]
    public async Task RemoveUser_RevocationFails_ThrowsAndDoesNotDeactivate()
    {
        _tokenRevocations.RevokeAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    // Cross-tenant escalation guard: this is the single most important test in this task. A
    // tenant-admin of "acme" must not be able to deactivate a user who belongs to a different
    // tenant, even though user_id (unlike tenant_id) is entirely caller-supplied. The target
    // user_id here ("99") reads back from Authentik with tenant_id "globex" —
    // RequireUserInTenantAsync must reject this before DeactivateUserAsync is ever called.
    [Fact]
    public async Task RemoveUser_UserBelongsToDifferentTenant_ThrowsPermissionDeniedAndDoesNotDeactivate()
    {
        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = OtherTenantUser.Id }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    // Authentik's 404 (GetUserAsync's null) is "not in this tenant".
    [Fact]
    public async Task RemoveUser_UnknownUser_ThrowsPermissionDeniedAndDoesNotDeactivate()
    {
        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "12" }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    // A user whose record carries no tenant_id belongs to no tenant.
    [Fact]
    public async Task RemoveUser_UserWithNoTenant_ThrowsPermissionDeniedAndDoesNotDeactivate()
    {
        _authentikAdminClient.GetUserAsync("12")
            .Returns(Task.FromResult<IdpUserRecord?>(CallerTenantUser with { Id = "12", TenantId = null }));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "12" }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RemoveUser_SuspendedTenant_ThrowsPermissionDeniedBeforeCheckingUserOrDeactivating()
    {
        _tenantStatusCache.GetStatusAsync("acme").Returns(Task.FromResult<string?>("suspended"));

        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = "11" }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().GetUserAsync(Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().DeactivateUserAsync(Arg.Any<string>());
    }

    // CSR round 12 §2.1: a user_id that is not a canonical positive decimal integer (an Authentik
    // pk) is "not in this tenant" before any IdP request is made.
    public static TheoryData<string> NonCanonicalUserIds() => new()
    {
        "", "0", "00", "011", "-11", "+11", " 11", "11 ", "1 1", "11.0", "1e3", "0x1B", "abc",
        "11/", "11?x=1", "2147483648", "١١",
    };

    [Theory]
    [MemberData(nameof(NonCanonicalUserIds))]
    public async Task RemoveUser_NonCanonicalUserId_ThrowsPermissionDeniedWithNoIdpRequest(string userId)
    {
        var act = () => _sut.RemoveUser(new RemoveUserRequest { UserId = userId }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        _authentikAdminClient.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NonCanonicalUserIds))]
    public async Task SetTenantAdmin_NonCanonicalUserId_ThrowsPermissionDeniedWithNoIdpRequest(string userId)
    {
        var act = () => _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = userId, Grant = true }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        _authentikAdminClient.ReceivedCalls().Should().BeEmpty();
    }

    // ── SetTenantAdmin ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetTenantAdmin_GrantForUserInCallerTenant_AddsGroupAndReturnsUser()
    {
        var response = await _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = "11", Grant = true }, ContextForCallerTenant());

        response.UserId.Should().Be("11");
        response.Username.Should().Be("alice");
        await _authentikAdminClient.Received(1).AddGroupAsync("11", "tenant-admins");
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync(Arg.Any<string>(), Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().ListUsersByTenantAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task SetTenantAdmin_RevokeForUserInCallerTenant_RemovesGroup()
    {
        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "11", Grant = false }, ContextForCallerTenant());

        await _authentikAdminClient.Received(1).RemoveGroupAsync("11", "tenant-admins");
        await _authentikAdminClient.DidNotReceive().AddGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // CSR round 12 §2.1: both group writes take the checked record's pk.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetTenantAdmin_ChangesTheGroupOfThePkOfTheRecordItChecked(bool grant)
    {
        _authentikAdminClient.GetUserAsync("12")
            .Returns(Task.FromResult<IdpUserRecord?>(CallerTenantUser with { Id = "13" }));

        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "12", Grant = grant }, ContextForCallerTenant());

        if (grant)
            await _authentikAdminClient.Received(1).AddGroupAsync("13", "tenant-admins");
        else
            await _authentikAdminClient.Received(1).RemoveGroupAsync("13", "tenant-admins");
        await _authentikAdminClient.DidNotReceive().AddGroupAsync("12", Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync("12", Arg.Any<string>());
    }

    // A demoted admin's existing tokens still carry the tenant-admins group, and the TenantAdmin
    // policy reads groups from the caller's own token — so demotion revokes them, first. It
    // revokes again after removing the group, for a token refreshed in between.
    [Fact]
    public async Task SetTenantAdmin_Revoke_RevokesTheUsersTokensBeforeAndAfterRemovingTheGroup()
    {
        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "11", Grant = false }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _authentikAdminClient.RemoveGroupAsync("11", "tenant-admins");
            _tokenRevocations.RevokeAsync("uid-alice");
        });
    }

    [Fact]
    public async Task SetTenantAdmin_Revoke_InvalidatesTheRevocationCacheAfterEachRevoke()
    {
        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "11", Grant = false }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
            _authentikAdminClient.RemoveGroupAsync("11", "tenant-admins");
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
        });
    }

    [Fact]
    public async Task SetTenantAdmin_Revoke_RevocationFails_ThrowsAndDoesNotRemoveTheGroup()
    {
        _tokenRevocations.RevokeAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.SetTenantAdmin(
            new SetTenantAdminRequest { UserId = "11", Grant = false }, ContextForCallerTenant());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _authentikAdminClient.DidNotReceive().RemoveGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SetTenantAdmin_Grant_DoesNotRevoke()
    {
        await _sut.SetTenantAdmin(new SetTenantAdminRequest { UserId = "11", Grant = true }, ContextForCallerTenant());

        await _tokenRevocations.DidNotReceive().RevokeAsync(Arg.Any<string>());
    }

    // Same cross-tenant escalation guard as RemoveUser, for the promote/demote path: a
    // tenant-admin of "acme" must not be able to grant or revoke tenant-admin rights for a user
    // who belongs to a different tenant.
    [Fact]
    public async Task SetTenantAdmin_UserBelongsToDifferentTenant_ThrowsPermissionDeniedAndDoesNotChangeGroups()
    {
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
            new SetTenantAdminRequest { UserId = "11", Grant = true }, ContextForCallerTenant());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        await _authentikAdminClient.DidNotReceive().GetUserAsync(Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive().AddGroupAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}
