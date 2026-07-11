using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class SchemaRegistryRepositoryTests
{
    [Fact]
    public async Task EnsureTableAsync_CreatesTableIfNotExists()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new SchemaRegistryRepository(sql);

        await repo.EnsureTableAsync();

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("CREATE TABLE IF NOT EXISTS _iverson_schema")), Arg.Any<object?>());
    }

    [Fact]
    public async Task LoadAllAsync_SelectsTypeNameAndSchemaJson()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<(string type_name, string schema_json)>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<(string, string)> { ("Article", "{}") });
        var repo = new SchemaRegistryRepository(sql);

        var result = (await repo.LoadAllAsync()).ToList();

        result.Should().ContainSingle(r => r.TypeName == "Article" && r.SchemaJson == "{}");
    }

    [Fact]
    public async Task UpsertAsync_InsertsWithOnConflictUpdate()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new SchemaRegistryRepository(sql);

        await repo.UpsertAsync("Article", "{}");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("ON CONFLICT (type_name) DO UPDATE")),
            Arg.Is<object>(p => (string)p.GetType().GetProperty("TypeName")!.GetValue(p)! == "Article"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesByTypeName()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new SchemaRegistryRepository(sql);

        await repo.DeleteAsync("Article");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM _iverson_schema")), Arg.Any<object?>());
    }
}
