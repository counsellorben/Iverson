using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

[Trait("Category", "Integration")]
public sealed class PipelineIntegrationTests : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksContainerFixture _fx;

    public PipelineIntegrationTests(StarRocksContainerFixture fx) => _fx = fx;

    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName      = "PipeArticle",
        TableName     = "pipe_articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("AuthorId",    "uuid",        true),
            new ColumnDescriptor("WordCount",   "integer",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
        ],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry EmptyRegistry()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        return new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    // Adapts a SchemaDescriptor-backed SchemaRegistry to the Func<string, StarRocksQuerySchema?>
    // lookup delegate StarRocksPipelineBuilder.Build now takes — same pattern used at
    // ObjectSearchGrpcService's Pipeline call site and throughout StarRocksIntegrationTests.cs.
    private static Func<string, StarRocksQuerySchema?> ResolvedRegistry(SchemaRegistry registry) =>
        t => registry.Get(t) is { } d ? SchemaBuilder.ToStarRocksQuerySchema(d) : null;

    private async Task SeedAsync()
    {
        // 6 rows: author A has 4 articles (WordCount 100/200/300/400, months Jan-Apr 2026),
        // author B has 2 (WordCount 500/600, Jan/Feb 2026). Fixed single-character ids keep
        // assertions deterministic. Idempotent: DROP TABLE IF EXISTS first. StarRocks
        // PRIMARY KEY columns must be NOT NULL and listed first. ENGINE/PROPERTIES mirror
        // StarRocksRepository.BuildCreateTableDdl's convention (replication_num=1 is required
        // on this single-BE allin1 test container; the default cluster replication factor
        // would otherwise exceed the number of available BEs and the CREATE TABLE would fail).
        await _fx.Repository.ExecuteAsync("DROP TABLE IF EXISTS pipe_articles");
        await _fx.Repository.ExecuteAsync("""
            CREATE TABLE pipe_articles (
                Id VARCHAR(64) NOT NULL, AuthorId VARCHAR(64), WordCount INT, PublishedAt DATETIME
            ) ENGINE=OLAP
            PRIMARY KEY (Id) DISTRIBUTED BY HASH(Id) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """);
        await _fx.Repository.ExecuteAsync("""
            INSERT INTO pipe_articles VALUES
            ('1','A',100,'2026-01-01 00:00:00'), ('2','A',200,'2026-02-01 00:00:00'),
            ('3','A',300,'2026-03-01 00:00:00'), ('4','A',400,'2026-04-01 00:00:00'),
            ('5','B',500,'2026-01-15 00:00:00'), ('6','B',600,'2026-02-15 00:00:00')
            """);
    }

    [Fact]
    public async Task TopNPerGroup_ReturnsTwoNewestPerAuthor()
    {
        await SeedAsync();

        var ranked = new PipelineStep { Name = "ranked" };
        ranked.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });
        var top = new PipelineStep { Name = "top2" };
        top.Where.Add(new SearchClause
        {
            Property = "rn", Operator = SearchOperator.LessThanOrEquals,
            Value = new SearchValue { NumberVal = 2 }, ClauseType = SearchClauseType.Filter
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(ranked);
        request.Steps.Add(top);

        var (sql, param) = StarRocksPipelineBuilder.Build(
            SchemaBuilder.ToStarRocksQuerySchema(ArticleSchema()), request, ResolvedRegistry(EmptyRegistry()));
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(4);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Id).Should().BeEquivalentTo(["3", "4", "5", "6"]);
    }

    [Fact]
    public async Task FilterThenAggregateThenHaving_OneQuery()
    {
        await SeedAsync();

        var agg = new PipelineStep { Name = "by_author" };
        agg.Where.Add(new SearchClause
        {
            Property = "WordCount", Operator = SearchOperator.GreaterThanOrEquals,
            Value = new SearchValue { NumberVal = 200 }, ClauseType = SearchClauseType.Filter
        });
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });
        agg.Having.Add(new SearchClause
        {
            Property = "articles", Operator = SearchOperator.GreaterThanOrEquals,
            Value = new SearchValue { NumberVal = 3 }, ClauseType = SearchClauseType.Filter
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(agg);

        var (sql, param) = StarRocksPipelineBuilder.Build(
            SchemaBuilder.ToStarRocksQuerySchema(ArticleSchema()), request, ResolvedRegistry(EmptyRegistry()));
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(1);   // only author A has >=3 articles with >=200 words
        ((string)rows[0].AuthorId).Should().Be("A");
    }

    [Fact]
    public async Task RunningTotal_OverMonthlyBuckets()
    {
        await SeedAsync();

        var monthly = new PipelineStep { Name = "monthly" };
        monthly.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        monthly.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var cume = new PipelineStep { Name = "cume" };
        cume.Windows.Add(new WindowFunction
        {
            Alias = "running_total", Kind = WindowFunctionKind.RunningSum,
            Field = "n", OrderBy = "PublishedAt_month"
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(monthly);
        request.Steps.Add(cume);
        request.OrderBy.Add(new SearchSort { Property = "PublishedAt_month" });

        var (sql, param) = StarRocksPipelineBuilder.Build(
            SchemaBuilder.ToStarRocksQuerySchema(ArticleSchema()), request, ResolvedRegistry(EmptyRegistry()));
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(4);                                  // Jan, Feb, Mar, Apr
        // (long) cast is required here, not just style: rows[^1].running_total is `dynamic`,
        // and Convert.ToInt64(dynamic) resolves via the runtime binder too, so its result stays
        // `dynamic` — the C# runtime binder then can't find an instance member '.Should()' on
        // the boxed long it unwraps to (RuntimeBinderException). Casting to (long) first forces
        // static typing before FluentAssertions' extension method is resolved. Same issue and
        // fix as the (long) casts throughout StarRocksIntegrationTests.cs.
        ((long)Convert.ToInt64(rows[^1].running_total)).Should().Be(6L);   // all six articles
    }

    [Fact]
    public async Task DerivedRatio_PercentOfTotal()
    {
        await SeedAsync();

        var byAuthor = new PipelineStep { Name = "by_author" };
        byAuthor.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        byAuthor.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var share = new PipelineStep { Name = "share" };
        share.Derive.Add(new DeriveColumn { Alias = "pct", Expr = "100.0 * n / SUM(n) OVER ()" });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(byAuthor);
        request.Steps.Add(share);

        var (sql, param) = StarRocksPipelineBuilder.Build(
            SchemaBuilder.ToStarRocksQuerySchema(ArticleSchema()), request, ResolvedRegistry(EmptyRegistry()));
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        var byAuthorPct = ((IEnumerable<dynamic>)rows)
            .ToDictionary(r => (string)r.AuthorId, r => (double)Convert.ToDouble(r.pct));
        byAuthorPct["A"].Should().BeApproximately(66.6667, 0.01);
        byAuthorPct["B"].Should().BeApproximately(33.3333, 0.01);
    }

    [Fact]
    public async Task JoinCteAgainstBase_EnrichesRowsWithAggregates()
    {
        await SeedAsync();

        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });
        enriched.Select.Add(new SelectItem { Source = "by_author", Column = "articles" });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (sql, param) = StarRocksPipelineBuilder.Build(
            SchemaBuilder.ToStarRocksQuerySchema(ArticleSchema()), request, ResolvedRegistry(EmptyRegistry()));
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(6);
        foreach (var row in (IEnumerable<dynamic>)rows)
        {
            var authorId = (string)row.AuthorId;
            int articles = Convert.ToInt32(row.articles);
            articles.Should().Be(authorId == "A" ? 4 : 2);
        }
    }
}
