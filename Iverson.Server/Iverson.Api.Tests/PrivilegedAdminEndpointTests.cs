using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;
using RpcStatusCode = Grpc.Core.StatusCode;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §5.2: <c>/admin/reconcile/{type}</c> and <c>/admin/dlq/{id}/replay</c> queue their
/// work for the worker and answer from the request's outcome: 200 when it finished, 404 when the
/// type or row is gone, 202 with the request id while it is still running. Through the real
/// <c>Program.cs</c> pipeline, with the request queue faked so each test decides what the worker
/// recorded, and with the waits shortened.
/// </summary>
public class PrivilegedAdminEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string ActiveTenant = "tenant_active";

    private readonly AuthTestWebApplicationFactory _baseFactory;
    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly IDlqRepository _dlq = Substitute.For<IDlqRepository>();
    private (Guid Id, string Kind, string Payload, TimeSpan? ExpiresAfter)? _inserted;

    public PrivilegedAdminEndpointTests(AuthTestWebApplicationFactory factory)
    {
        _baseFactory = factory;
        _requests.InsertAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _inserted = ((Guid)ci[0], (string)ci[1], (string)ci[2], (TimeSpan?)ci[4]));
    }

    private HttpClient CreateClient()
    {
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync(ActiveTenant).Returns(Task.FromResult<string?>("active"));

        return _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPrivilegedRequestRepository>();
            services.AddSingleton(_requests);
            services.RemoveAll<PrivilegedRequestWaits>();
            services.AddSingleton(new PrivilegedRequestWaits
            {
                Reconcile    = TimeSpan.FromMilliseconds(300),
                Replay       = TimeSpan.FromMilliseconds(300),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });
            services.RemoveAll<IDlqRepository>();
            services.AddSingleton(_dlq);
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton(tenantStatusCache);
        })).CreateClient();
    }

    private static HttpRequestMessage OperatorPost(string path)
    {
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", ActiveTenant), new Claim("groups", "operators")]);

        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");
        return request;
    }

    private void WorkerRecords(string status, PrivilegedRequestResult? result = null) =>
        _requests.GetAsync(Arg.Any<Guid>()).Returns(
            new PrivilegedRequestRow(Guid.Empty, "any", "{}", status, result?.ToJson()));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ── /admin/reconcile/{typeName} ─────────────────────────────────────────

    [Fact]
    public async Task Reconcile_TheWorkerFinished_Returns200WithTheCount()
    {
        WorkerRecords(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok(7));

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("reconciledCount").GetInt32().Should().Be(7);
        body.GetProperty("typeName").GetString().Should().Be("Widget");
        _inserted!.Value.Kind.Should().Be(PrivilegedRequestKinds.ReconcileType);
        JsonSerializer.Deserialize<ReconcileTypePayload>(_inserted.Value.Payload, SchemaRegistry.JsonOptions)!
            .TypeName.Should().Be("Widget");
        _inserted.Value.ExpiresAfter.Should().BeNull("a reconcile is idempotent, so it never expires");
    }

    [Fact]
    public async Task Reconcile_AnUnknownType_Returns404()
    {
        WorkerRecords(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(RpcStatusCode.NotFound, "No schema registered for 'Widget'"));

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("No schema registered for 'Widget'");
    }

    [Fact]
    public async Task Reconcile_StillRunningAfterTheWait_Returns202WithTheRequestId()
    {
        WorkerRecords(PrivilegedRequestStatus.Running);

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await BodyAsync(response)).GetProperty("requestId").GetGuid().Should().Be(_inserted!.Value.Id);
    }

    [Fact]
    public async Task Reconcile_AnyOtherWorkerFailure_Returns500WithTheRecordedMessage()
    {
        WorkerRecords(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(RpcStatusCode.Internal, "reconcile failed"));

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("reconcile failed");
    }

    // ── /admin/dlq/{id}/replay ──────────────────────────────────────────────

    [Fact]
    public async Task Replay_TheWorkerReplayed_Returns200_AndTheApiNeverReadsTheMessage()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok());

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("replayed").GetBoolean().Should().BeTrue();
        body.GetProperty("topic").GetString().Should().Be("iverson.entity-events");
        _inserted!.Value.Kind.Should().Be(PrivilegedRequestKinds.ReplayDlq);
        JsonSerializer.Deserialize<ReplayDlqPayload>(_inserted.Value.Payload, SchemaRegistry.JsonOptions)!
            .Id.Should().Be(id);
        await _dlq.DidNotReceive().GetUnreplayedByIdAsync(Arg.Any<Guid>());
        await _dlq.DidNotReceive().MarkReplayedAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Replay_TheWorkerFoundTheRowAlreadyReplayed_Returns404()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(RpcStatusCode.NotFound, $"No unreplayed DLQ row with id '{id}'"));

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_StillPendingAfterTheWait_Returns202WithTheRequestId()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Pending);

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await BodyAsync(response)).GetProperty("requestId").GetGuid().Should().Be(_inserted!.Value.Id);
    }

    [Fact]
    public async Task Replay_ARowOfAnotherTenant_Returns403_AndNothingIsQueued()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", "tenant_other"));

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _requests.DidNotReceive().InsertAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>());
    }
}
