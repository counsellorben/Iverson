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
        sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns("{\"Id\":\"k1\"}");
        var repo = new EntityRepository(sql);

        var result = await repo.FetchByKeyAsync(ArticleSchema, "k1", EntityAccess.CrossTenantMaintenance);

        result.Should().Be("{\"Id\":\"k1\"}");
        await sql.Received(1).QuerySingleOrDefaultAsync<string>(
            Arg.Is<string>(s => s.Contains("\"articles\"") && s.Contains("\"Id\"") && s.Contains("::uuid")),
            Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task FetchManyByKeysAsync_BindsKeysAsGuidArray_NotStringArray()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<KeyedRow> { new("k1", "{}") });
        var repo = new EntityRepository(sql);
        var key = Guid.NewGuid().ToString();

        await repo.FetchManyByKeysAsync(ArticleSchema, [key], EntityAccess.CrossTenantMaintenance);

        var call = sql.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRecordStoreQueryExecutor.QueryAsync));
        var param = call.GetArguments()[1];
        var keysProp = param!.GetType().GetProperty("Keys")!.GetValue(param);
        keysProp.Should().BeOfType<Guid[]>();
    }

    [Fact]
    public async Task FetchByColumnAsync_FiltersByGivenColumn_NotTheKeyColumn()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<string> { "{}" });
        var repo = new EntityRepository(sql);

        await repo.FetchByColumnAsync(ArticleSchema, "AuthorId", "a1", EntityAccess.CrossTenantMaintenance);

        await sql.Received(1).QueryAsync<string>(
            Arg.Is<string>(s => s.Contains("\"AuthorId\"")), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task FetchAllAsync_HasNoWhereClause()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<string> { "{}" });
        var repo = new EntityRepository(sql);

        await repo.FetchAllAsync(ArticleSchema, EntityAccess.CrossTenantMaintenance);

        await sql.Received(1).QueryAsync<string>(
            Arg.Is<string>(s => !s.Contains("WHERE")), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task DeleteAsync_ExecutesOnTheGivenTransactionContext_NotTheInjectedExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EntityRepository(sql);

        await repo.DeleteAsync(tx, ArticleSchema, "k1", EntityAccess.CrossTenantMaintenance);

        await tx.Received(1).ExecuteAsync(Arg.Is<string>(s => s.Contains("DELETE FROM \"articles\"")), Arg.Any<object?>());
        await sql.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>());
    }

    // ── EntityAccess threading (Part B1 Task 2; re-cut to EntityAccess by CSR-3 #5) ────

    [Fact]
    public async Task FetchByKeyAsync_ThreadsTenantScopedAndTenantId_IntoQueryExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns("{}");
        var repo = new EntityRepository(sql);

        await repo.FetchByKeyAsync(ArticleSchema, "k1", EntityAccess.ForTenant("tenant-a"));

        await sql.Received(1).QuerySingleOrDefaultAsync<string>(
            Arg.Any<string>(), Arg.Any<object?>(), RecordStoreRole.TenantRuntime, "tenant-a");
    }

    [Fact]
    public async Task FetchManyByKeysAsync_ThreadsTenantScopedAndTenantId_IntoQueryExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<KeyedRow>());
        var repo = new EntityRepository(sql);
        var key = Guid.NewGuid().ToString();

        await repo.FetchManyByKeysAsync(ArticleSchema, [key], EntityAccess.ForTenant("tenant-a"));

        await sql.Received(1).QueryAsync<KeyedRow>(
            Arg.Any<string>(), Arg.Any<object?>(), RecordStoreRole.TenantRuntime, "tenant-a");
    }

    [Fact]
    public async Task FetchByColumnAsync_ThreadsTenantScopedAndTenantId_IntoQueryExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<string>());
        var repo = new EntityRepository(sql);

        await repo.FetchByColumnAsync(ArticleSchema, "AuthorId", "a1", EntityAccess.ForTenant("tenant-a"));

        await sql.Received(1).QueryAsync<string>(
            Arg.Any<string>(), Arg.Any<object?>(), RecordStoreRole.TenantRuntime, "tenant-a");
    }

    [Fact]
    public async Task FetchAllAsync_ThreadsTenantScopedAndTenantId_IntoQueryExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
           .Returns(new List<string>());
        var repo = new EntityRepository(sql);

        await repo.FetchAllAsync(ArticleSchema, EntityAccess.ForTenant("tenant-a"));

        await sql.Received(1).QueryAsync<string>(
            Arg.Any<string>(), Arg.Any<object?>(), RecordStoreRole.TenantRuntime, "tenant-a");
    }

    [Fact]
    public async Task DeleteAsync_ForTenant_IssuesSetLocalRoleAndSetConfig_BeforeDelete_AndResetsRoleAfter_EvenWhenTenantIdIsNull()
    {
        // The critical case for EntityAccess.ForTenant: a null tenant id must still switch
        // roles (fail-closed on the DB side), not skip the switch.
        // It must also reset the role afterwards: SET LOCAL ROLE persists for the rest of the
        // transaction, and callers (ObjectMappingGrpcService.Delete) go on to write to plumbing
        // tables (the reconciliation/outbox queue) in the same transaction that iverson_runtime
        // has no grant on — omitting the reset breaks every tenant-scoped delete at runtime.
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var calls = new List<string>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call => calls.Add(call.ArgAt<string>(0)));
        var repo = new EntityRepository(sql);

        await repo.DeleteAsync(tx, ArticleSchema, "k1", EntityAccess.ForTenant(null));

        calls.Should().HaveCount(4);
        calls[0].Should().Contain("SET LOCAL ROLE iverson_runtime");
        calls[1].Should().Contain("set_config");
        calls[2].Should().Contain("DELETE FROM \"articles\"");
        calls[3].Should().Contain("RESET ROLE");
    }

    [Fact]
    public async Task DeleteAsync_CrossTenantMaintenance_SwitchesToTheBypassrlsRole_SetsNoTenantGuc_AndStillResets()
    {
        // CSR-3 #5: there is no longer an access value that runs the DELETE on the connection's
        // own (table-owning) role — that was the silent cross-tenant path. The deliberate
        // cross-tenant case now names itself, switches to iverson_maintenance, and must still
        // reset: ObjectMappingGrpcService.Delete writes an outbox row in the same transaction and
        // iverson_maintenance's grants cover entity tables, not the plumbing ones.
        //
        // No set_config: app.tenant_id would be inert under BYPASSRLS, and setting one would imply
        // a filtering that is not happening.
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var calls = new List<string>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call => calls.Add(call.ArgAt<string>(0)));
        var repo = new EntityRepository(sql);

        await repo.DeleteAsync(tx, ArticleSchema, "k1", EntityAccess.CrossTenantMaintenance);

        calls.Should().HaveCount(3);
        calls[0].Should().Contain("SET LOCAL ROLE iverson_maintenance");
        calls[1].Should().Contain("DELETE FROM \"articles\"");
        calls[2].Should().Contain("RESET ROLE");
        calls.Should().NotContain(c => c.Contains("set_config"));
    }

    [Fact]
    public void EntityAccess_Default_IsForTenantWithNoTenantId_TheFailClosedValue()
    {
        // default(EntityAccess) is reachable from `default` in a test or a `new T()` in future
        // code. It must land on the fail-closed side (tenant-scoped, no tenant => zero rows),
        // never on the cross-tenant side.
        default(EntityAccess).CrossTenant.Should().BeFalse();
        default(EntityAccess).TenantId.Should().BeNull();
        default(EntityAccess).Should().Be(EntityAccess.ForTenant(null));
        EntityAccess.CrossTenantMaintenance.Should().NotBe(default(EntityAccess));
    }

    [Fact]
    public void EntityAccess_CrossTenantMaintenance_CarriesNoTenantId()
    {
        EntityAccess.CrossTenantMaintenance.CrossTenant.Should().BeTrue();
        EntityAccess.CrossTenantMaintenance.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateColumnsAsync_ExecutesOnTheGivenTransactionContext_NotTheInjectedExecutor()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EntityRepository(sql);

        await repo.UpdateColumnsAsync(tx, ArticleSchema, "k1", new Dictionary<string, object?> { ["Title"] = "New Title" });

        await tx.Received(1).ExecuteAsync(Arg.Is<string>(s => s.Contains("UPDATE \"articles\"")), Arg.Any<object?>());
        await sql.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public async Task UpdateColumnsAsync_SetsOnlySuppliedColumns_AndFiltersByKeyColumn()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EntityRepository(sql);

        await repo.UpdateColumnsAsync(
            tx, ArticleSchema, "k1",
            new Dictionary<string, object?> { ["Title"] = "New Title", ["Body"] = "New Body" });

        await tx.Received(1).ExecuteAsync(
            Arg.Is<string>(s =>
                s.Contains("SET \"Title\" = @Title") &&
                s.Contains("\"Body\" = @Body") &&
                s.Contains("WHERE \"Id\" = @Key::uuid")),
            Arg.Any<object?>());
    }
}
