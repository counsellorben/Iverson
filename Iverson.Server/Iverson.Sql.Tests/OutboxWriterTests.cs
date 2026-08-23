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

    [Fact]
    public async Task EnqueueUpdateOutboxRowAsync_InsertsRowWithEventTypeUpdated_AndStoredPayload_UsingCallerSuppliedId()
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

        await _sut.EnqueueUpdateOutboxRowAsync(tx, id, "Author", "author-1", payload);

        capturedSql.Should().NotBeNull();
        capturedSql!.Should().Contain("INSERT INTO").And.Contain(OutboxTableName).And.Contain("'Updated'");

        capturedParams.Should().NotBeNull();
        var paramType = capturedParams!.GetType();
        paramType.GetProperty("Id")!.GetValue(capturedParams).Should().Be(id);
        paramType.GetProperty("TypeName")!.GetValue(capturedParams).Should().Be("Author");
        paramType.GetProperty("EntityKey")!.GetValue(capturedParams).Should().Be("author-1");
        paramType.GetProperty("Payload")!.GetValue(capturedParams).Should().Be(payload);
    }

    // ── Server-owned tenant column injection ──────────────────────────────────
    //
    // UpsertAndEnqueueOutboxAsync is the ONE chokepoint all four writers reach
    // (ObjectMappingGrpcService.Post/Update, ObjectPersistenceGrpcService.Post/Update). The
    // upsert runs `json_populate_record(null::"table", @Json::json)` with an ON CONFLICT update
    // set covering every column, so a payload arriving without the tenant column writes NULL over
    // a valid tenant id. Injecting here rather than at each caller is what no future caller can
    // bypass.

    private const string TenantCol = "__TenantId";

    private static readonly TableSchema TenantArticleSchema = new(
        "articles",
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema> { new("Title", "text", false), new(TenantCol, "text", false) },
        TenantCol);

    /// <summary>
    /// Runs an upsert against a recording transaction and returns the JSON actually handed to
    /// <c>json_populate_record</c> — i.e. exactly what Postgres will write.
    /// </summary>
    private async Task<string> CaptureUpsertJsonAsync(
        TableSchema schema, string payloadJson, string? tenantId)
    {
        var tx = Substitute.For<IDbTransactionContext>();
        string? captured = null;
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call =>
          {
              if (!call.ArgAt<string>(0).Contains("json_populate_record")) return;
              var param = call.ArgAt<object?>(1);
              captured = (string?)param!.GetType().GetProperty("Json")!.GetValue(param);
          });
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(tx));

        await _sut.UpsertAndEnqueueOutboxAsync(
            schema, "Article", Guid.NewGuid().ToString(), payloadJson, tenantId);

        captured.Should().NotBeNull("the upsert statement must have been executed");
        return captured!;
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_PayloadMissingTheTenantColumn_InjectsIt()
    {
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema, """{"Id":"a","Title":"Hello"}""", "tenant-a");

        json.Should().Contain($"\"{TenantCol}\":\"tenant-a\"");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_InjectsTheTenantColumnInExactCanonicalCasing()
    {
        // json_populate_record matches column names CASE-SENSITIVELY. A key that survives the
        // round-trip as "__tenantId" or "__TENANTID" is silently discarded by Postgres and the
        // column is written NULL — which is exactly the failure this injection exists to prevent.
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema, """{"Id":"a","Title":"Hello"}""", "tenant-a");

        json.Should().Contain($"\"{TenantCol}\":");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_PreservesTheCasingOfEveryOtherKey()
    {
        // The injection is a JSON round-trip; if it lower-cased or otherwise renamed keys,
        // json_populate_record would drop every column, not just the tenant one.
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema, """{"Id":"a","Title":"Hello"}""", "tenant-a");

        json.Should().Contain("\"Id\":\"a\"");
        json.Should().Contain("\"Title\":\"Hello\"");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_ClientSuppliedTenantValue_IsOverwritten()
    {
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema,
            $$"""{"Id":"a","{{TenantCol}}":"attacker-tenant"}""",
            "tenant-a");

        json.Should().NotContain("attacker-tenant");
        json.Should().Contain($"\"{TenantCol}\":\"tenant-a\"");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_CaseVariantTenantKey_IsRemovedNotDuplicated()
    {
        // Two keys differing only by case both survive a naive Set: Postgres would then see a
        // duplicate-ish object and the canonical one is not guaranteed to win.
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema,
            """{"Id":"a","__tenantid":"attacker-tenant"}""",
            "tenant-a");

        json.Should().NotContain("attacker-tenant");
        json.Should().NotContain("__tenantid");
        json.Should().Contain($"\"{TenantCol}\":\"tenant-a\"");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_NullTenantId_LeavesThePayloadUntouched()
    {
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema, """{"Id":"a","Title":"Hello"}""", tenantId: null);

        json.Should().Be("""{"Id":"a","Title":"Hello"}""");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_SchemaWithoutATenantColumn_LeavesThePayloadUntouched()
    {
        var json = await CaptureUpsertJsonAsync(
            ArticleSchema, """{"Id":"a","Title":"Hello"}""", "tenant-a");

        json.Should().Be("""{"Id":"a","Title":"Hello"}""");
    }
}
