using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 12 §6.4: the api never enters BYPASSRLS. GetMany and Mapping.Delete used to pick
/// <c>iverson_maintenance</c> for a decision with no tenant column. The real evaluator never
/// produces such a decision when it allows the call: every loaded descriptor has a tenant column,
/// and its only non-denied return carries it. So a stub evaluator stands in here, and the call must
/// be refused with Internal: GetMany before its read, and Delete after its row lookup but before
/// its transaction.
/// </summary>
public class TenantlessDecisionRefusalTests
{
    private static readonly string AuthorId   = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"test-tenant"}""";

    // Allowed, with no owner scoping, no field restriction and no tenant boundary.
    private static readonly AuthorizationDecision TenantlessDecision = new(false, false, null, null, null, null, null);

    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IRecordStoreTransactionRunner _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
    private readonly IRowFieldAuthorizationEvaluator _evaluator = Substitute.For<IRowFieldAuthorizationEvaluator>();
    private readonly IActingUserAccessor _actingUser = new ActingUserAccessor
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly AuditLog _auditLog = new(NullLogger<AuditLog>.Instance);
    private readonly SchemaRegistry _registry = new(
        new SchemaRegistryRepository(Substitute.For<IRecordStoreQueryExecutor>()),
        NullLogger<SchemaRegistry>.Instance);

    public TenantlessDecisionRefusalTests()
    {
        _evaluator
            .Evaluate(Arg.Any<SchemaDescriptor>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<AuthorizationAction>())
            .Returns(TenantlessDecision);
    }

    [Fact]
    public async Task GetMany_WithAnAllowedDecisionThatHasNoTenantColumn_IsRefusedBeforeTheRead()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var sut = new ObjectRetrievalGrpcService(
            _entities, _registry, NullLogger<ObjectRetrievalGrpcService>.Instance, _actingUser, _evaluator, _auditLog);

        var act = () => sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            Substitute.For<IServerStreamWriter<RetrievalResponse>>(),
            TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Internal);
        await _entities.DidNotReceiveWithAnyArgs().FetchManyByKeysAsync(default!, default!, default);
    }

    [Fact]
    public async Task Delete_WithAnAllowedDecisionThatHasNoTenantColumn_IsRefusedBeforeTheTransaction()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(AuthorJson);
        var sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            Substitute.For<IOutboxPublisher>(),
            _registry,
            Substitute.For<IRelationValidator>(),
            Substitute.For<IPayloadSizeValidator>(),
            Substitute.For<IEntityKeyAccessor>(),
            Substitute.For<IOutboxWriter>(),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUser,
            _evaluator,
            Substitute.For<IEntityRelationResolver>(),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            _auditLog,
            EngagementQueryLimitOptions.Default);

        var act = () => sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId }, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Internal);
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
        await _entities.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default!, default);
    }
}
