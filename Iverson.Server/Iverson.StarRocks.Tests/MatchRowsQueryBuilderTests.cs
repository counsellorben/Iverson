using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.StarRocks.Tests;

public sealed class MatchRowsQueryBuilderTests
{
    // "Blob" is a bytes column (passed as excluded); "__TenantId" is the server-owned tenant column.
    private static EngagementQuerySchema Schema() => new(
        "Event", "events", "Id", ["UserId", "Kind", "At", "Score", "OwnerId", "Secret", "Blob", "__TenantId"],
        TenantColumnName: "__TenantId");

    private static IReadOnlyDictionary<string, AuthorizationConstraint> Authz(
        IReadOnlySet<string>? allowed = null, string? ownerColumn = null) =>
        new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = new(allowed, ownerColumn, ownerColumn is null ? null : "u1", "__TenantId", "t1")
        };

    private static MatchRowsRequest Req(
        string[]? partitionBy = null, (string Property, bool Descending)[]? orderBy = null,
        string[]? referenced = null, string[]? measures = null, bool oneRow = true,
        SearchClause[]? where = null, SearchLogic logic = SearchLogic.And, int maxRowsScanned = 100) =>
        new(where ?? [], logic, partitionBy ?? [],
            (orderBy ?? [("At", false)]).Select(o => new SearchSort { Property = o.Property, Descending = o.Descending }).ToList(),
            referenced ?? [], measures ?? [], oneRow, ["Blob"], maxRowsScanned);

    private static (string Sql, DynamicParameters Param) Build(MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null) =>
        MatchRowsQueryBuilder.Build(Schema(), request, authz ?? Authz(), "iverson_tenant_t1");

    [Fact]
    public void One_row_selects_the_key_then_each_partition_and_referenced_column_once_in_canonical_spelling()
    {
        var (sql, param) = Build(Req(partitionBy: ["userid"], referenced: ["score", "UserId", "KIND"]));

        sql.Should().Be(
            "SELECT `Id`, `UserId`, `Score`, `Kind` FROM `iverson_tenant_t1`.`events` " +
            "WHERE `__TenantId` = @__tenantVal ORDER BY `UserId`, `At`, `Id` LIMIT 101");
        param.Get<string>("__tenantVal").Should().Be("t1");
    }

    [Fact]
    public void All_rows_selects_every_visible_column_except_bytes_and_tenant()
    {
        var (sql, _) = Build(Req(oneRow: false));

        sql.Should().StartWith("SELECT `Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `Secret` FROM ");
    }

    [Fact]
    public void Order_by_keeps_descending_and_ends_with_the_key_tie_breaker()
    {
        var (sql, _) = Build(Req(partitionBy: ["Kind"], orderBy: [("At", true), ("score", false)]));

        sql.Should().EndWith("ORDER BY `Kind`, `At` DESC, `Score`, `Id` LIMIT 101");
    }

    [Fact]
    public void Where_owner_and_tenant_predicates_compose_as_the_other_read_paths_do()
    {
        var clause = new SearchClause
        {
            Property = "kind", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "view" },
            ClauseType = SearchClauseType.Filter,
        };
        var (sql, param) = Build(Req(where: [clause]), Authz(ownerColumn: "OwnerId"));

        sql.Should().Contain("WHERE ((`Kind` = @p0) AND `OwnerId` = @__ownerVal) AND `__TenantId` = @__tenantVal ORDER BY");
        param.Get<string>("p0").Should().Be("view");
        param.Get<string>("__ownerVal").Should().Be("u1");
    }

    public static TheoryData<string, string> BadColumns() => new()
    {
        { "Nope", "unknown" },
        { "Secret", "hidden" },            // not in AllowedFields (see the Authz below)
        { "__tenantid", "tenant" },
        { "Blob", "bytes" },
    };

    [Theory]
    [MemberData(nameof(BadColumns))]
    public void Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column(string column, string _)
    {
        var allowed = new HashSet<string> { "Id", "UserId", "Kind", "At", "Score", "OwnerId", "Blob", "__TenantId" };
        var where = new SearchClause
        {
            Property = column, Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" },
            ClauseType = SearchClauseType.Filter,
        };

        foreach (var request in new[]
        {
            Req(partitionBy: [column]),
            Req(orderBy: [(column, false)]),
            Req(where: [where]),
            Req(referenced: [column]),
        })
        {
            var act = () => Build(request, Authz(allowed));
            act.Should().Throw<EngagementQueryTranslationException>().WithMessage($"*'{column}'*");
        }
    }

    [Fact]
    public void One_row_rejects_a_measure_named_like_a_partition_column_ordinally_only()
    {
        var act = () => Build(Req(partitionBy: ["userid"], measures: ["UserId"]));
        act.Should().Throw<EngagementQueryTranslationException>().WithMessage("*UserId*");

        Build(Req(partitionBy: ["userid"], measures: ["userid"]));          // differs from the canonical spelling
        Build(Req(partitionBy: ["userid"], measures: ["Kind"]));            // not a ONE_ROW output column
    }

    [Fact]
    public void All_rows_rejects_a_measure_named_like_any_output_column()
    {
        foreach (var name in new[] { "Id", "Kind", "Secret" })
        {
            var act = () => Build(Req(oneRow: false, measures: [name]));
            act.Should().Throw<EngagementQueryTranslationException>().WithMessage($"*{name}*");
        }

        Build(Req(oneRow: false, measures: ["kind"]));                       // ordinal: not an output column
        Build(Req(oneRow: false, measures: ["Blob"]));                       // excluded, so not an output column
    }

    [Fact]
    public void Validation_runs_before_any_sql_is_built_even_with_no_where()
    {
        var act = () => MatchRowsQueryBuilder.Build(Schema(), Req(partitionBy: ["Nope"]), Authz(), null);
        act.Should().Throw<EngagementQueryTranslationException>();
    }
}
