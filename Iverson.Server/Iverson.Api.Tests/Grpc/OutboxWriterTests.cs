using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Sql;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class OutboxWriterTests
{
    private readonly IPostgresQueryExecutor _sql;
    private readonly IPostgresTransactionRunner _txRunner;
    private readonly OutboxWriter _sut;

    public OutboxWriterTests()
    {
        _sql = Substitute.For<IPostgresQueryExecutor>();
        _txRunner = Substitute.For<IPostgresTransactionRunner>();
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(Substitute.For<IDbTransactionContext>()));
        _sut = new OutboxWriter(_sql, _txRunner);
    }

    private static SchemaDescriptor MakeSchema() => new()
    {
        TypeName      = "Article",
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Title", "text", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = []
    };

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_RunsInsideOneTransaction()
    {
        var id = await _sut.UpsertAndEnqueueOutboxAsync(MakeSchema(), "Article", Guid.NewGuid().ToString(), "{}");

        id.Should().NotBe(Guid.Empty);
        await _txRunner.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }

    [Fact]
    public async Task DeleteOutboxRowIfPresentAsync_ExecutesDeleteByRowId()
    {
        var rowId = Guid.CreateVersion7();

        await _sut.DeleteOutboxRowIfPresentAsync(rowId);

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM") && s.Contains(ReconciliationSchema.TableName)),
            Arg.Any<object?>());
    }
}
