using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.2: the api queues the registration, waits for the claim, then for the result,
/// and reloads its registry on success. The waits are shortened; their production values are
/// <see cref="PrivilegedRequestWaits"/>' defaults.
/// </summary>
public class QueuedSchemaRegistrationExecutorTests
{
    private static readonly PrivilegedRequestWaits ShortWaits = new()
    {
        RegisterClaim = TimeSpan.FromMilliseconds(300),
        RegisterRun   = TimeSpan.FromMilliseconds(300),
        PollInterval  = TimeSpan.FromMilliseconds(10)
    };

    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly ISchemaRegistryRepository _registryRepository = Substitute.For<ISchemaRegistryRepository>();
    private readonly QueuedSchemaRegistrationExecutor _sut;

    public QueuedSchemaRegistrationExecutorTests()
    {
        _registryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string, string)>());
        _sut = new QueuedSchemaRegistrationExecutor(
            new PrivilegedRequestClient(_requests, ShortWaits),
            new SchemaRegistry(_registryRepository, NullLogger<SchemaRegistry>.Instance));
    }

    private Task Execute() =>
        _sut.ExecuteAsync([Descriptor("Probe", "tenant-a")], ["Probe"], CancellationToken.None);

    [Fact]
    public async Task Succeeded_ReloadsTheRegistryBeforeAnswering()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(
            StatusRow(PrivilegedRequestStatus.Running), StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok()));

        await Execute();

        await _registryRepository.Received(1).LoadAllAsync();
        await _requests.Received(1).InsertAsync(
            Arg.Any<Guid>(),
            Arg.Is(PrivilegedRequestKinds.RegisterSchema),
            Arg.Any<string>(),
            Arg.Is<string?>("tenant-a"),
            Arg.Is<TimeSpan?>(ShortWaits.RegisterClaim));
    }

    [Fact]
    public async Task ThePayload_IsTheOwnerStampedDescriptorsAndTheChangedTemplates()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok()));

        await Execute();

        var payloadJson = (string)_requests.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPrivilegedRequestRepository.InsertAsync))
            .GetArguments()[2]!;
        var payload = JsonSerializer.Deserialize<RegisterSchemaPayload>(payloadJson, SchemaRegistry.JsonOptions)!;
        payload.Descriptors.Should().BeEquivalentTo(new[] { Descriptor("Probe", "tenant-a") });
        payload.ChangedTemplateTypes.Should().Equal("Probe");
    }

    [Fact]
    public async Task Failed_AnswersWithTheStoredStatusAndMessage()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(StatusCode.PermissionDenied, "Type 'Probe' is registered to another tenant and cannot be re-registered here.")));

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
        await _registryRepository.DidNotReceive().LoadAllAsync();
    }

    [Fact]
    public async Task NeverClaimed_ExpiresTheRequest_AndAnswersUnavailable()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Pending));
        _requests.ExpireIfPendingAsync(Arg.Any<Guid>()).Returns(true);

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unavailable);
        ex.Which.Status.Detail.Should().Contain("did not pick the registration up");
        await _requests.Received(1).ExpireIfPendingAsync(Arg.Any<Guid>());
        await _registryRepository.DidNotReceive().LoadAllAsync();
    }

    [Fact]
    public async Task ClaimedJustAsTheApiGaveUp_IsWaitedOnRatherThanExpired()
    {
        // The expiry is conditional on 'pending': when the worker's claim got there first, it
        // matches no row, and the api waits on the claimed request instead.
        var claimed = false;
        _requests.ExpireIfPendingAsync(Arg.Any<Guid>()).Returns(false).AndDoes(_ => claimed = true);
        _requests.GetAsync(Arg.Any<Guid>()).Returns(_ => claimed
            ? StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok())
            : StatusRow(PrivilegedRequestStatus.Pending));

        await Execute();

        await _registryRepository.Received(1).LoadAllAsync();
    }

    [Fact]
    public async Task ClaimedButUnfinishedAfterTheRunWait_AnswersDeadlineExceeded()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Running));

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);
        ex.Which.Status.Detail.Should().Contain("registration is still being applied");
        await _requests.DidNotReceive().ExpireIfPendingAsync(Arg.Any<Guid>());
    }
}
