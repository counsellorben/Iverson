using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
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
        _sut = new PrivilegedRequestExecutor(
            _registry,
            new SchemaRegistrationApplier(_registryRepository, _tables, _schemaManager, Substitute.For<IDocumentRerenderQueueRepository>()),
            _locks,
            _logger);
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
