using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.3, per descriptor in batch order: the stored owner, the adoption check, the DDL
/// and the conditional registry write; then the re-render enqueue.
/// </summary>
public class SchemaRegistrationApplierTests
{
    private readonly ISchemaRegistryRepository _registry = Substitute.For<ISchemaRegistryRepository>();
    private readonly IExistingTableInspector _tables = Substitute.For<IExistingTableInspector>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IDocumentRerenderQueueRepository _rerenderQueue = Substitute.For<IDocumentRerenderQueueRepository>();
    private readonly SchemaRegistrationApplier _sut;

    public SchemaRegistrationApplierTests()
    {
        _registry.GetOwnersAsync(Arg.Any<string>()).Returns(new List<string?>());
        _registry.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _tables.DescribeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((ExistingTable?)null);
        _sut = new SchemaRegistrationApplier(_registry, _tables, _schemaManager, _rerenderQueue);
    }

    private Task Apply(params SchemaDescriptor[] descriptors) =>
        _sut.ExecuteAsync(descriptors, [], CancellationToken.None);

    private async Task AssertNothingAppliedAsync()
    {
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
        await _registry.DidNotReceive().UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnotherTenantOwnsTheStoredRow_IsPermissionDenied_AndNothingIsApplied()
    {
        _registry.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-b" });

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndNoTable_AppliesTheDdlThenWritesTheOwnerStampedRow()
    {
        await Apply(Descriptor("Probe", "tenant-a"));

        Received.InOrder(() =>
        {
            _schemaManager.ApplySchemaAsync(Arg.Is<TableSchema>(t => t.TableName == "probes"), Arg.Is(SchemaDriftPolicy.Throw));
            _registry.UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
        });
        var written = (string)_registry.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ISchemaRegistryRepository.UpsertIfOwnerAsync))
            .GetArguments()[1]!;
        JsonSerializer.Deserialize<SchemaDescriptor>(written, SchemaRegistry.JsonOptions)!
            .OwnerTenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingRelationThatIsNotATable_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(false, true));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain("'probes'").And.Contain("is not an ordinary table");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableWithoutTheTenantColumn_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, false));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain($"has no {SchemaDescriptor.TenantColumnName} column");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableHoldingAnotherTenantsRows_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));
        _tables.HasRowsOfAnotherTenantAsync("probes", SchemaDescriptor.TenantColumnName, "tenant-a").Returns(true);

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain("holds rows of another tenant");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableHoldingOnlyTheOwnersRows_IsAdopted()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));
        _tables.HasRowsOfAnotherTenantAsync("probes", SchemaDescriptor.TenantColumnName, "tenant-a").Returns(false);

        await Apply(Descriptor("Probe", "tenant-a"));

        await _schemaManager.Received(1).ApplySchemaAsync(Arg.Any<TableSchema>(), SchemaDriftPolicy.Throw);
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
    }

    [Fact]
    public async Task AnUnownedType_AdoptsAnExistingTenantScopedTable_WithoutTheForeignRowQuery()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));

        await Apply(Descriptor("Probe", owner: null));

        await _tables.DidNotReceive().HasRowsOfAnotherTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is((string?)null));
    }

    [Fact]
    public async Task AStoredRowOfTheSameOwner_IsReRegisteredWithoutTheAdoptionCheck()
    {
        _registry.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-a" });
        _registry.ExistsAsync("Probe").Returns(true);

        await Apply(Descriptor("Probe", "tenant-a"));

        await _tables.DidNotReceive().DescribeAsync(Arg.Any<string>(), Arg.Any<string>());
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
    }

    [Fact]
    public async Task Drift_IsFailedPrecondition_AndTheRegistryIsNotWritten()
    {
        _schemaManager.ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>())
            .ThrowsAsync(new SchemaDriftException("probes", "Title", "integer", "TEXT"));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Which.Status.Detail.Should().Contain("Migrate the column by hand");
        await _registry.DidNotReceive().UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task TheConditionalWriteMatchingNoRow_IsPermissionDenied()
    {
        _registry.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(false);

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("registered to another tenant");
    }

    [Fact]
    public async Task ChangedTemplates_AreEnqueuedAfterEveryDescriptorIsWritten()
    {
        await _sut.ExecuteAsync(
            [Descriptor("First", "tenant-a"), Descriptor("Second", "tenant-a")], ["First"], CancellationToken.None);

        Received.InOrder(() =>
        {
            _registry.UpsertIfOwnerAsync(Arg.Is("First"), Arg.Any<string>(), Arg.Any<string?>());
            _registry.UpsertIfOwnerAsync(Arg.Is("Second"), Arg.Any<string>(), Arg.Any<string?>());
            _rerenderQueue.EnqueueTypeAsync("First");
        });
        await _rerenderQueue.Received(1).EnqueueTypeAsync(Arg.Any<string>());
    }
}
