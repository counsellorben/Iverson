using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class OutboxWriterTests
{
    private const string OutboxTableName = "IversonReconciliationQueue";

    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IRecordStoreTransactionRunner _txRunner;
    private readonly OutboxWriter _sut;

    public OutboxWriterTests()
    {
        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(Substitute.For<IDbTransactionContext>()));
        _sut = new OutboxWriter(OutboxTableName, _sql, _txRunner);
    }

    private static readonly TableSchema ArticleSchema = new(
        "articles",
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema> { new("Title", "text", false) });

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_RunsInsideOneTransaction()
    {
        var id = await _sut.UpsertAndEnqueueOutboxAsync(ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}");

        id.Should().NotBe(Guid.Empty);
        await _txRunner.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_NullTenantId_DoesNotIssueSetLocalRoleOrResetRole()
    {
        var tx = Substitute.For<IDbTransactionContext>();
        var calls = new List<string>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call => calls.Add(call.ArgAt<string>(0)));
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(tx));

        await _sut.UpsertAndEnqueueOutboxAsync(ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}");

        calls.Should().HaveCount(2); // upsert, then outbox insert — no role switch at all
        calls.Should().NotContain(s => s.Contains("ROLE"));
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_WithTenantId_SwitchesRoleForUpsert_ThenResetsBeforeOutboxInsert()
    {
        var tx = Substitute.For<IDbTransactionContext>();
        var calls = new List<string>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call => calls.Add(call.ArgAt<string>(0)));
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(tx));

        await _sut.UpsertAndEnqueueOutboxAsync(
            ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}", tenantId: "tenant-a");

        calls.Should().HaveCount(5);
        calls[0].Should().Contain("SET LOCAL ROLE iverson_runtime");
        calls[1].Should().Contain("set_config");
        calls[2].Should().Contain("INSERT INTO \"articles\"");
        calls[3].Should().Contain("RESET ROLE");
        calls[4].Should().Contain(OutboxTableName);
    }

    [Fact]
    public async Task DeleteOutboxRowIfPresentAsync_ExecutesDeleteByRowId()
    {
        var rowId = Guid.CreateVersion7();

        await _sut.DeleteOutboxRowIfPresentAsync(rowId);

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM") && s.Contains(OutboxTableName)),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task EnqueueDeleteOutboxRowAsync_InsertsRowWithEventTypeDeleted_AndStoredPayload()
    {
        var tx = Substitute.For<IDbTransactionContext>();
        var id = Guid.NewGuid();
        const string payload = """{"Id":"author-1","Name":"Alice"}""";

        string? capturedSql = null;
        object? capturedParams = null;
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call =>
          {
              capturedSql = call.ArgAt<string>(0);
              capturedParams = call.ArgAt<object?>(1);
          });

        await _sut.EnqueueDeleteOutboxRowAsync(tx, id, "Author", "author-1", payload);

        capturedSql.Should().NotBeNull();
        capturedSql!.Should().Contain("INSERT INTO").And.Contain(OutboxTableName).And.Contain("'Deleted'");

        capturedParams.Should().NotBeNull();
        var paramType = capturedParams!.GetType();
        paramType.GetProperty("Id")!.GetValue(capturedParams).Should().Be(id);
        paramType.GetProperty("TypeName")!.GetValue(capturedParams).Should().Be("Author");
        paramType.GetProperty("EntityKey")!.GetValue(capturedParams).Should().Be("author-1");
        paramType.GetProperty("Payload")!.GetValue(capturedParams).Should().Be(payload);
    }
}
