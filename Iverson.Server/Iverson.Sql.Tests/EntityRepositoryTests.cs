using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class EntityRepositoryTests
{
    private static readonly TableSchema ArticleSchema = new(
        "articles",
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema> { new("Title", "text", false), new("Body", "text", false) });

    [Fact]
    public async Task FetchByKeyAsync_QuotesTableAndKeyColumn_BindsKeyAsUuidCast()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns("{\"Id\":\"k1\"}");
        var repo = new EntityRepository(sql);

        var result = await repo.FetchByKeyAsync(ArticleSchema, "k1");

        result.Should().Be("{\"Id\":\"k1\"}");
        await sql.Received(1).QuerySingleOrDefaultAsync<string>(
            Arg.Is<string>(s => s.Contains("\"articles\"") && s.Contains("\"Id\"") && s.Contains("::uuid")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task FetchManyByKeysAsync_BindsKeysAsGuidArray_NotStringArray()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<KeyedRow> { new("k1", "{}") });
        var repo = new EntityRepository(sql);
        var key = Guid.NewGuid().ToString();

        await repo.FetchManyByKeysAsync(ArticleSchema, [key]);

        var call = sql.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRecordStoreQueryExecutor.QueryAsync));
        var param = call.GetArguments()[1];
        var keysProp = param!.GetType().GetProperty("Keys")!.GetValue(param);
        keysProp.Should().BeOfType<Guid[]>();
    }

    [Fact]
    public async Task FetchByColumnAsync_FiltersByGivenColumn_NotTheKeyColumn()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(new List<string> { "{}" });
        var repo = new EntityRepository(sql);

        await repo.FetchByColumnAsync(ArticleSchema, "AuthorId", "a1");

        await sql.Received(1).QueryAsync<string>(
            Arg.Is<string>(s => s.Contains("\"AuthorId\"")), Arg.Any<object?>());
    }

    [Fact]
    public async Task FetchAllAsync_HasNoWhereClause()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(new List<string> { "{}" });
        var repo = new EntityRepository(sql);

        await repo.FetchAllAsync(ArticleSchema);

        await sql.Received(1).QueryAsync<string>(
            Arg.Is<string>(s => !s.Contains("WHERE")), Arg.Any<object?>());
    }

    [Fact]
    public async Task DeleteAsync_ExecutesOnTheGivenTransactionContext_NotTheInjectedExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EntityRepository(sql);

        await repo.DeleteAsync(tx, ArticleSchema, "k1");

        await tx.Received(1).ExecuteAsync(Arg.Is<string>(s => s.Contains("DELETE FROM \"articles\"")), Arg.Any<object?>());
        await sql.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>());
    }
}
