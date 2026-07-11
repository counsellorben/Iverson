using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectRetrievalGrpcServiceTests
{
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly SchemaRegistry _registry;
    private readonly ObjectRetrievalGrpcService _sut;

    private static readonly string AuthorId   = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorId2  = "11111111-0000-0000-0000-000000000002";
    private static readonly string AuthorJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer"}""";

    public ObjectRetrievalGrpcServiceTests()
    {
        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _entities = Substitute.For<IEntityRepository>();

        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new ObjectRetrievalGrpcService(_entities, _registry,
            NullLogger<ObjectRetrievalGrpcService>.Instance);
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WhenEntityExists_ReturnsFoundWithParsedData()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId, TraceId = "t1" },
            TestServerCallContext.Create());

        response.Found.Should().BeTrue();
        response.TraceId.Should().Be("t1");
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WhenEntityNotFound_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenSchemaNotRegistered_ReturnsNotFound()
    {
        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        await _entities.DidNotReceive().FetchByKeyAsync(
            Arg.Any<TableSchema>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Get_QueriesCorrectTable_UsingKeyColumnAndTypeName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(AuthorJson);

        await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        await _entities.Received(1).FetchByKeyAsync(
            Arg.Is<TableSchema>(s => s.TableName == "authors" && s.KeyColumn.Name == "Id"),
            AuthorId);
    }

    [Fact]
    public async Task Get_PreservesTraceId_InResponse()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId, TraceId = "trace-xyz" },
            TestServerCallContext.Create());

        response.TraceId.Should().Be("trace-xyz");
    }

    // ── GetMany ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMany_StreamsFoundResponseForEachKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeTrue());
    }

    [Fact]
    public async Task GetMany_WhenEntityMissing_StreamsNotFoundForThatKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) }); // AuthorId2 absent

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written[0].Found.Should().BeTrue();
        stream.Written[1].Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetMany_WhenSchemaNotRegistered_StreamsNotFoundForAllKeys()
    {
        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Ghost", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeFalse());
        await _entities.DidNotReceive().FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task GetMany_PreservesTraceId_InEachResponse()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId }, TraceId = "trace-abc" },
            stream, TestServerCallContext.Create());

        stream.Written[0].TraceId.Should().Be("trace-abc");
    }

    [Fact]
    public async Task GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(Array.Empty<KeyedRow>());

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(),
            Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 2));
    }

    // ── stream helper ────────────────────────────────────────────────────────

    private static FakeStream<T> MakeStream<T>() => new();

    private sealed class FakeStream<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(T message, CancellationToken cancellationToken)
            { Written.Add(message); return Task.CompletedTask; }
    }
}
