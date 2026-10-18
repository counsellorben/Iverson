using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.1: the worker reloads its registry, takes the request kind's lock, runs it, and
/// turns every outcome into a recorded result: a refusal keeps its status and message, an
/// unexpected exception becomes a generic Internal with the details in the log.
/// </summary>
public class PrivilegedRequestExecutorTests
{
    private readonly ISchemaRegistryRepository _registryRepository = Substitute.For<ISchemaRegistryRepository>();
    private readonly IExistingTableInspector _tables = Substitute.For<IExistingTableInspector>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IPrivilegedRequestLocks _locks = Substitute.For<IPrivilegedRequestLocks>();
    private readonly ILogger<PrivilegedRequestExecutor> _logger = Substitute.For<ILogger<PrivilegedRequestExecutor>>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IDlqRepository _dlq = Substitute.For<IDlqRepository>();
    private readonly IEventProducer _events = Substitute.For<IEventProducer>();
    private readonly SchemaRegistry _registry;
    private readonly PrivilegedRequestExecutor _sut;

    public PrivilegedRequestExecutorTests()
    {
        _registryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string, string)>());
        _registryRepository.GetOwnersAsync(Arg.Any<string>()).Returns(new List<string?>());
        _registryRepository.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _tables.DescribeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((ExistingTable?)null);
        _locks.AcquireRegistrationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        _locks.AcquireTypeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        _registry = new SchemaRegistry(_registryRepository, NullLogger<SchemaRegistry>.Instance);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<EntityAccess>())
            .Returns(Enumerable.Empty<KeyedTenantRow>());
        _sut = new PrivilegedRequestExecutor(
            _registry,
            new SchemaRegistrationApplier(_registryRepository, _tables, _schemaManager, Substitute.For<IDocumentRerenderQueueRepository>()),
            new ReconciliationService(
                _registry, _entities, Substitute.For<IReconciliationQueueRepository>(), _events,
                NullLogger<ReconciliationService>.Instance),
            _dlq,
            _events,
            _locks,
            _logger);
    }

    private static PrivilegedRequestRow ReconcileRow(string typeName) => new(
        Guid.NewGuid(), PrivilegedRequestKinds.ReconcileType,
        JsonSerializer.Serialize(new ReconcileTypePayload(typeName), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running, null);

    private static PrivilegedRequestRow ReplayRow(Guid dlqId) => new(
        Guid.NewGuid(), PrivilegedRequestKinds.ReplayDlq,
        JsonSerializer.Serialize(new ReplayDlqPayload(dlqId), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running, null);

    private void AuthorIsRegistered() =>
        _registryRepository.LoadAllAsync().Returns(
            [("Author", JsonSerializer.Serialize(Helpers.SchemaFixtures.AuthorSchema(), SchemaRegistry.JsonOptions))]);

    [Fact]
    public async Task ReconcileType_ReloadsTheRegistry_TakesOnlyTheTypeLock_AndAnswersTheCount()
    {
        AuthorIsRegistered();

        var result = await _sut.RunAsync(ReconcileRow("Author"), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok(0));
        await _locks.Received(1).AcquireTypeAsync(Arg.Is("Author"), Arg.Any<CancellationToken>());
        await _locks.DidNotReceive().AcquireRegistrationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileType_AnUnknownType_IsNotFound()
    {
        var result = await _sut.RunAsync(ReconcileRow("NoSuchType"), CancellationToken.None);

        result.Code.Should().Be(StatusCode.NotFound);
        result.Message.Should().Be("No schema registered for 'NoSuchType'");
    }

    [Fact]
    public async Task ReconcileType_StoppedByWorkerShutdown_IsUnavailable_NotAPartialCount()
    {
        // ReconcileTypeAsync ends its page loop when the token fires and returns the count so far.
        AuthorIsRegistered();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _sut.RunAsync(ReconcileRow("Author"), cts.Token);

        result.Code.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public async Task ReplayDlq_AnUnreplayedRow_IsProducedThenMarked_WithoutALock()
    {
        var dlqId = Guid.NewGuid();
        _dlq.GetUnreplayedByIdAsync(dlqId).Returns(new DlqReplayRow("iverson.entity-events", "key-1", "value-1", "tenant-a"));

        var result = await _sut.RunAsync(ReplayRow(dlqId), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok());
        Received.InOrder(() =>
        {
            _events.ProduceAsync("iverson.entity-events", "key-1", "value-1");
            _dlq.MarkReplayedAsync(dlqId);
        });
        await _locks.DidNotReceive().AcquireTypeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _locks.DidNotReceive().AcquireRegistrationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplayDlq_ARowAlreadyReplayed_IsNotFound_AndNothingIsProduced()
    {
        var dlqId = Guid.NewGuid();
        _dlq.GetUnreplayedByIdAsync(dlqId).Returns((DlqReplayRow?)null);

        var result = await _sut.RunAsync(ReplayRow(dlqId), CancellationToken.None);

        result.Code.Should().Be(StatusCode.NotFound);
        await _events.DidNotReceive().ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _dlq.DidNotReceive().MarkReplayedAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task RegisterSchema_ReloadsTheRegistry_TakesTheRegistrationLock_AndApplies()
    {
        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok());
        await _registryRepository.Received(1).LoadAllAsync();
        await _locks.Received(1).AcquireRegistrationAsync(Arg.Any<CancellationToken>());
        await _schemaManager.Received(1).ApplySchemaAsync(Arg.Is<TableSchema>(t => t.TableName == "probes"), SchemaDriftPolicy.Throw);
    }

    [Fact]
    public async Task ARefusal_IsRecordedWithItsStatusAndMessage()
    {
        _registryRepository.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-b" });

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Code.Should().Be(StatusCode.PermissionDenied);
        result.Message.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
    }

    [Fact]
    public async Task AnUnexpectedException_IsRecordedAsInternal_WithAGenericMessage_AndLogged()
    {
        _schemaManager.ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>())
            .ThrowsAsync(new InvalidOperationException("connection refused: secret detail"));

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Code.Should().Be(StatusCode.Internal);
        result.Message.Should().NotContain("secret detail");
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(e => e.Message == "connection refused: secret detail"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task AnUnknownKind_IsRecordedAsInternal()
    {
        var request = new PrivilegedRequestRow(Guid.NewGuid(), "Unheard", "{}", PrivilegedRequestStatus.Running, null);

        var result = await _sut.RunAsync(request, CancellationToken.None);

        result.Code.Should().Be(StatusCode.Internal);
    }

    [Fact]
    public async Task StoppedByWorkerShutdown_IsRecordedAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _locks.AcquireRegistrationAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), cts.Token);

        result.Code.Should().Be(StatusCode.Unavailable);
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
    }
}
