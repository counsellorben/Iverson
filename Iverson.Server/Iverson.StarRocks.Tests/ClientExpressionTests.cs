using System.Globalization;
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.StarRocks.Expressions;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The client-expression parser behind every expression emission site: AggregationSpec.expression
/// (BuildAggregate), MetricSpec.expression (BuildGroupBy and Pipeline metrics) and
/// DeriveColumn.expr (Pipeline derive). The SQL is written from the parse tree, never from the
/// input text.
/// </summary>
public class ClientExpressionTests
{
    private const string Context = "Test expression";

    private static string QuoteAny(string name) => $"`{name}`";

    private static string Sql(string text, ExpressionContext context) =>
        ClientExpression.ToSql(text, context, QuoteAny, Context);

    // Assert.Throws checks the exact type: nothing but EngagementQueryTranslationException may escape.
    private static string Rejection(
        string text, ExpressionContext context, Func<string, string>? resolveQuoted = null) =>
        Assert.Throws<EngagementQueryTranslationException>(
            () => ClientExpression.ToSql(text, context, resolveQuoted ?? QuoteAny, Context)).Message;

    private static CultureInfo CommaDecimalCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    // ── Accepted corpus: every expression the suites and SDKs use today ───────────

    [Theory]
    [InlineData("Rating * 2", "`Rating` * 2")]
    [InlineData("WordCount * 2", "`WordCount` * 2")]
    [InlineData("Rating", "`Rating`")]
    [InlineData("COALESCE(Rating, 0)", "COALESCE(`Rating`, 0)")]
    [InlineData("Rating * (1 - 0)", "`Rating` * (1 - 0)")]
    [InlineData("Rating * (1 - 0) * (1 + 0)", "`Rating` * (1 - 0) * (1 + 0)")]
    [InlineData("price * (1 - discount)", "`price` * (1 - `discount`)")]
    [InlineData("extended_price * (1 - discount)", "`extended_price` * (1 - `discount`)")]
    [InlineData("ExtendedPrice * (1 - Discount) * (1 + Tax)", "`ExtendedPrice` * (1 - `Discount`) * (1 + `Tax`)")]
    [InlineData("ABS(Amount)", "ABS(`Amount`)")]
    [InlineData("ABS (Amount)", "ABS(`Amount`)")]
    [InlineData("ABS\t(Amount)", "ABS(`Amount`)")]
    [InlineData("ABS\n(Amount)", "ABS(`Amount`)")]
    [InlineData("abs(Amount)", "ABS(`Amount`)")]
    [InlineData("ROUND(NULLIF(Amount, 0), 2)", "ROUND(NULLIF(`Amount`, 0), 2)")]
    [InlineData("COALESCE(Amount, NULL, 0)", "COALESCE(`Amount`, NULL, 0)")]
    public void AggregateArgument_AcceptedCorpus_EmitsCanonicalSql(string text, string expected) =>
        Sql(text, ExpressionContext.AggregateArgument).Should().Be(expected);

    [Theory]
    [InlineData("WordCount * 2", "`WordCount` * 2")]
    [InlineData("WordCount + 1", "`WordCount` + 1")]
    [InlineData("WordCount", "`WordCount`")]
    [InlineData("100.0 * n / SUM(n) OVER ()", "100.0 * `n` / SUM(`n`) OVER ()")]
    [InlineData("100.0 * article_count / SUM(article_count) OVER ()", "100.0 * `article_count` / SUM(`article_count`) OVER ()")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)", "SUM(`Amount`) OVER (PARTITION BY `Region` ORDER BY `Amount` DESC)")]
    [InlineData("sum (Amount) over (partition by Region, Kind order by Amount asc, Id)", "SUM(`Amount`) OVER (PARTITION BY `Region`, `Kind` ORDER BY `Amount` ASC, `Id`)")]
    [InlineData("COUNT(*) OVER (PARTITION BY Region)", "COUNT(*) OVER (PARTITION BY `Region`)")]
    [InlineData("ROUND(AVG(Amount) OVER (), 2)", "ROUND(AVG(`Amount`) OVER (), 2)")]
    [InlineData("MIN(Amount) OVER () - MAX(Amount) OVER ()", "MIN(`Amount`) OVER () - MAX(`Amount`) OVER ()")]
    public void Derive_AcceptedCorpus_EmitsCanonicalSql(string text, string expected) =>
        Sql(text, ExpressionContext.Derive).Should().Be(expected);

    // ── Emitter: operators, parentheses and numbers come from the tree ─────────────

    [Theory]
    [InlineData("a - (b - c)", "`a` - (`b` - `c`)")]
    [InlineData("(a - b) - c", "`a` - `b` - `c`")]
    [InlineData("a * (b + c)", "`a` * (`b` + `c`)")]
    [InlineData("(a * b) + c", "`a` * `b` + `c`")]
    [InlineData("a / (b * c)", "`a` / (`b` * `c`)")]
    [InlineData("a % 7", "`a` % 7")]
    [InlineData("-a", "-`a`")]
    [InlineData("- -a", "-(-`a`)")]
    [InlineData("a - -1", "`a` - -1")]
    [InlineData("-(a + b)", "-(`a` + `b`)")]
    [InlineData("((a))", "`a`")]
    [InlineData("  a\t*\n2  ", "`a` * 2")]
    [InlineData("007", "7")]
    [InlineData("100.0", "100.0")]
    [InlineData("null", "NULL")]
    public void Emitter_WritesOperatorsParenthesesAndNumbersFromTheTree(string text, string expected) =>
        Sql(text, ExpressionContext.Derive).Should().Be(expected);

    [Fact]
    public void Emitter_PrintsNumbersInvariantly_UnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();
            Sql("2.5 * a", ExpressionContext.AggregateArgument).Should().Be("2.5 * `a`");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Emitter_TakesEveryColumnFromTheCallback_IncludingWindowColumns()
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = "`t`.`Amount`",
            ["region"] = "`t`.`Region`",
        };
        var seen = new List<string>();
        string Resolve(string name)
        {
            seen.Add(name);
            return canonical[name];
        }

        ClientExpression.ToSql(
                "SUM(amount) OVER (PARTITION BY REGION ORDER BY Amount DESC)", ExpressionContext.Derive, Resolve, Context)
            .Should().Be("SUM(`t`.`Amount`) OVER (PARTITION BY `t`.`Region` ORDER BY `t`.`Amount` DESC)");
        seen.Should().Equal("amount", "REGION", "Amount");
    }

    // ── Rejected corpus: the lexer ─────────────────────────────────────────────────

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")", "character '\"' at position 17")]
    [InlineData("COALESCE(Amount, 'a')", "character ''' at position 17")]
    [InlineData("Amount \\ 2", "character '\\' at position 7")]
    [InlineData("Amount;", "character ';' at position 6")]
    [InlineData("Amount #", "character '#' at position 7")]
    [InlineData("Amount`", "character '`' at position 6")]
    [InlineData("Amount -- comment", "comment sequence '--' at position 7")]
    [InlineData("Amount /* comment */ + 1", "comment sequence '/*' at position 7")]
    [InlineData("Amount > 0", "character '>' at position 7")]
    [InlineData("Amount = 0", "character '=' at position 7")]
    [InlineData("Amount < 0", "character '<' at position 7")]
    [InlineData("Amount & 1", "character '&' at position 7")]
    [InlineData("Amount | 1", "character '|' at position 7")]
    [InlineData("Amount ^ 1", "character '^' at position 7")]
    [InlineData("~Amount", "character '~' at position 0")]
    [InlineData("Type.Field", "character '.' at position 4")]
    [InlineData("\u00C9tat * 2", "character U+00C9 at position 0")]
    [InlineData("Amount\u00A0* 2", "character U+00A0 at position 6")]
    public void Lexer_RefusesCharactersOutsideTheGrammar(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive)
            .Should().Be($"{Context}: {expected} is not allowed in an expression.");

    [Theory]
    [InlineData("1.", "number at position 0 has no digits after its decimal point")]
    public void Lexer_RefusesAMalformedNumber(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: {expected}.");

    [Theory]
    [InlineData("0.00000000000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000000001")]
    public void Lexer_RefusesANumberWithMoreDigitsThanItCanHoldExactly(string literal) =>
        Rejection(literal, ExpressionContext.Derive)
            .Should().Be($"{Context}: number '{literal}' at position 0 is out of range.");

    [Theory]
    [InlineData("1234567890123456789012345678", "1234567890123456789012345678")]
    [InlineData("0.1234567890123456789012345678", "0.1234567890123456789012345678")]
    [InlineData("1.0000000000000000000000000000000", "1.0000000000000000000000000000")]
    public void Lexer_AcceptsALongNumberThatIsExact(string literal, string expected) =>
        Sql(literal, ExpressionContext.Derive).Should().Be(expected);

    [Fact]
    public void Lexer_RefusesANumberThatDoesNotFit_WithoutAnOverflowException()
    {
        var digits = new string('9', 40);
        Rejection(digits, ExpressionContext.AggregateArgument)
            .Should().Be($"{Context}: number '{digits}' at position 0 is out of range.");
    }

    // ── Rejected corpus: the parser ────────────────────────────────────────────────

    [Theory]
    [InlineData("LENGTH(Amount)", "unknown function 'LENGTH' at position 0")]
    [InlineData("Order * 2", "unexpected 'Order' at position 0")]
    [InlineData("Amount Amount", "unexpected 'Amount' at position 7")]
    [InlineData("COALESCE(*)", "unexpected '*' at position 9")]
    [InlineData("+Amount", "unexpected '+' at position 0")]
    [InlineData("Amount *", "unexpected end of expression at position 8")]
    [InlineData("(Amount", "unexpected end of expression at position 7")]
    [InlineData("ABS()", "unexpected ')' at position 4")]
    [InlineData("   ", "unexpected end of expression at position 3")]
    public void Parser_RefusesMalformedExpressions(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: {expected}.");

    [Theory]
    [InlineData("NULLIF(Amount)", "NULLIF", "2")]
    [InlineData("NULLIF(Amount, 0, 1)", "NULLIF", "2")]
    [InlineData("ABS(Amount, 1)", "ABS", "1")]
    [InlineData("ROUND(Amount, 1, 2)", "ROUND", "1 to 2")]
    public void Parser_RefusesAScalarFunctionCalledWithTheWrongNumberOfArguments(
        string text, string function, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().StartWith(
            $"{Context}: function '{function}' at position 0 takes {expected} argument(s), not ");

    [Fact]
    public void Parser_RefusesWrongArityInsideAnAggregateArgument() =>
        Rejection("COALESCE(ABS(Amount, 1), 0)", ExpressionContext.AggregateArgument)
            .Should().Be($"{Context}: function 'ABS' at position 9 takes 1 argument(s), not 2.");

    [Theory]
    [InlineData("SUM(Amount)", "SUM", 0)]
    [InlineData("sum (Amount)", "SUM", 0)]
    [InlineData("COUNT(*)", "COUNT", 0)]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)", "SUM", 0)]
    [InlineData("COALESCE(MAX(Amount), 0)", "MAX", 9)]
    public void AggregateArgument_RefusesAggregatesAndTheirWindows(string text, string function, int position) =>
        Rejection(text, ExpressionContext.AggregateArgument).Should().Be(
            $"{Context}: aggregate function '{function}' at position {position} is not allowed here; " +
            "only COALESCE, NULLIF, ROUND and ABS may be called.");

    [Fact]
    public void AggregateArgument_RefusesAWindowAfterAScalarFunction() =>
        Rejection("COALESCE(Amount, 0) OVER ()", ExpressionContext.AggregateArgument)
            .Should().Be($"{Context}: unexpected 'OVER' at position 20.");

    [Fact]
    public void Derive_RefusesAWindowAfterAScalarFunction() =>
        Rejection("ABS(Amount) OVER ()", ExpressionContext.Derive)
            .Should().Be($"{Context}: unexpected 'OVER' at position 12.");

    [Theory]
    [InlineData("SUM(Amount)", "SUM", 0)]
    [InlineData("sum(Amount)", "SUM", 0)]
    [InlineData("COUNT(*)", "COUNT", 0)]
    [InlineData("100.0 * n / SUM(n)", "SUM", 12)]
    public void Derive_RefusesAnAggregateWithoutOver(string text, string function, int position) =>
        Rejection(text, ExpressionContext.Derive)
            .Should().Be($"{Context}: aggregate function '{function}' at position {position} requires an OVER clause.");

    [Fact]
    public void Derive_RefusesAnAggregateInsideAnAggregatesArgument() =>
        Rejection("SUM(SUM(n) OVER ()) OVER ()", ExpressionContext.Derive).Should().Be(
            $"{Context}: aggregate function 'SUM' at position 4 is not allowed here; " +
            "only COALESCE, NULLIF, ROUND and ABS may be called.");

    [Theory]
    [InlineData("SUM(*) OVER ()", 4)]
    [InlineData("COUNT(*, Amount) OVER ()", 6)]
    [InlineData("COUNT(* + 1) OVER ()", 6)]
    public void Derive_AllowsStarOnlyAsCountsSoleArgument(string text, int position) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: unexpected '*' at position {position}.");

    [Theory]
    [InlineData("SUM(Amount) OVER (PARTITION BY ABS(Region))", "unexpected 'ABS' at position 31")]
    [InlineData("SUM(Amount) OVER (ORDER BY Amount DESC DESC)", "unexpected 'DESC' at position 39")]
    [InlineData("SUM(Amount) OVER (ORDER Amount)", "unexpected 'Amount' at position 24")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Order)", "unexpected 'Order' at position 31")]
    public void Derive_WindowClauseTakesOnlyColumns(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: {expected}.");

    // ── Limits: depth, then length (length is checked before anything is parsed) ───

    private static string Nest(int levels) => new string('(', levels) + "Amount" + new string(')', levels);

    private static string Negations(int count) => string.Join(" ", Enumerable.Repeat("-", count)) + " a";

    [Fact]
    public void Parser_AcceptsNestingAtTheCap() =>
        Sql(Nest(31), ExpressionContext.Derive).Should().Be("`Amount`");

    [Fact]
    public void Parser_RefusesNestingOverTheCap() =>
        Rejection(Nest(32), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression nests deeper than 32 levels at position 32.");

    [Fact]
    public void Parser_CountsUnaryMinusTowardTheCap()
    {
        Sql(Negations(31), ExpressionContext.Derive).Should().StartWith("-(-(");
        Rejection(Negations(32), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression nests deeper than 32 levels at position 64.");
    }

    [Fact]
    public void LengthCap_RunsBeforeParsing()
    {
        // 1,001 opening parentheses would trip the depth cap if parsing ran first.
        Rejection(new string('(', 1001), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression exceeds 1000 characters.");
    }

    [Fact]
    public void LengthCap_RefusesAValidIdentifierOverTheCap() =>
        Rejection(new string('A', 1001), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression exceeds 1000 characters.");

    [Fact]
    public void LengthCap_AcceptsExactly1000Characters() =>
        Sql(new string('A', 1000), ExpressionContext.Derive).Should().Be($"`{new string('A', 1000)}`");

    // ── Columns in arguments, PARTITION BY and ORDER BY all go through the callback ─

    private static string DenySecret(string name) =>
        name == "Secret"
            ? throw new EngagementQueryTranslationException("column 'Secret' is not authorized")
            : $"`{name}`";

    [Theory]
    [InlineData("COALESCE(Secret, 0)")]
    [InlineData("Amount + ABS(Secret)")]
    public void AggregateArgument_ColumnsInArgumentsGoThroughTheCallback(string text) =>
        Rejection(text, ExpressionContext.AggregateArgument, DenySecret).Should().Be("column 'Secret' is not authorized");

    [Theory]
    [InlineData("SUM(Secret) OVER ()")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Secret)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region, Secret)")]
    [InlineData("SUM(Amount) OVER (ORDER BY Secret DESC)")]
    public void Derive_ColumnsInArgumentsAndWindowsGoThroughTheCallback(string text) =>
        Rejection(text, ExpressionContext.Derive, DenySecret).Should().Be("column 'Secret' is not authorized");

    // ── Each emission site: canonical SQL, and its pinned messages through its callback ─

    private static EngagementQuerySchema AuthorSchema() => new("Author", "authors", "Id", ["Name", "Bio", "Rating"]);

    private static EngagementQuerySchema ArticleSchema() => new("Article", "articles", "Id", ["Title", "WordCount", "AuthorId"]);

    // "Sum" is a column named like a function, and the caller may not read it.
    private static EngagementQuerySchema LedgerSchema() => new("Ledger", "ledgers", "Id", ["Sum", "Amount", "Region"]);

    private static Dictionary<string, AuthorizationConstraint> LedgerAuthzWithoutSum() => new()
    {
        ["Ledger"] = new(AllowedFields: new HashSet<string> { "Id", "Amount", "Region" }, OwnerColumn: null, OwnerValue: null)
    };

    private static PipelineRequest PipelineRequestFor(string typeName, params PipelineStep[] steps)
    {
        var request = new PipelineRequest { TypeName = typeName };
        request.Steps.AddRange(steps);
        return request;
    }

    [Fact]
    public void BuildAggregate_EmitsTheParsedExpressionWithResolvedColumns()
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Sum, "Rating", Expression: "rating * 2");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Be("SELECT SUM(`Rating` * 2) AS metric_val FROM `authors`");
    }

    [Fact]
    public void BuildAggregate_WithAJoin_QualifiesEveryExpressionColumn()
    {
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var spec = new AggregationDescriptor(
            "total", AggregationKind.Count, "Rating", Expression: "COALESCE(WordCount, Rating)");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins,
            registry: TestSchemaRegistry.BuildRegistry(AuthorSchema(), ArticleSchema()));

        sql.Should().Be(
            "SELECT COUNT(DISTINCT COALESCE(`articles`.`WordCount`, `authors`.`Rating`)) AS metric_val " +
            "FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
    }

    [Fact]
    public void BuildAggregate_RestrictedColumnInsideAFunction_KeepsThePinnedMessage()
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Avg, "Amount", Expression: "COALESCE(Sum, 0)");

        var act = () => StarRocksQueryBuilder.BuildAggregate(
            "ledgers", LedgerSchema(), null, spec, authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Aggregation field 'Sum' on 'Ledger' is not authorized for this caller.");
    }

    [Fact]
    public void BuildGroupBy_EmitsTheParsedExpressionWithQualifiedColumns()
    {
        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "revenue", Type = AggregationType.Sum, Expression = "rating * 2" });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy(
            "authors", AuthorSchema(), request, TestSchemaRegistry.BuildRegistry(AuthorSchema()));

        sql.Should().Contain("SUM(`authors`.`Rating` * 2) AS `revenue`");
    }

    [Theory]
    [InlineData("COALESCE(Sum, 0)", "Field 'Sum' on 'Ledger' referenced by metric 'm' expression is not authorized for this caller.")]
    [InlineData("COALESCE(Nope, 0)", "Metric 'm' expression references unknown field 'Nope'.")]
    public void BuildGroupBy_ColumnsInsideAFunction_KeepThePinnedMessages(string expression, string message)
    {
        var request = new GroupByRequest { TypeName = "Ledger", Keys = { "Region" } };
        request.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = expression });

        var act = () => StarRocksQueryBuilder.BuildGroupBy(
            "ledgers", LedgerSchema(), request, TestSchemaRegistry.BuildRegistry(LedgerSchema()),
            authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>().WithMessage(message);
    }

    [Fact]
    public void PipelineMetric_EmitsTheParsedExpressionWithResolvedColumns()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "wc2", Type = AggregationType.Sum, Expression = "wordcount * 2" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), PipelineRequestFor("Article", step), TestSchemaRegistry.BuildRegistry());

        sql.Should().Contain("`agg` AS (SELECT `AuthorId`, SUM(`WordCount` * 2) AS `wc2` FROM `base` GROUP BY `AuthorId`)");
    }

    [Fact]
    public void PipelineMetric_RestrictedColumnInsideAFunction_KeepsThePinnedMessage()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "Region" });
        step.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = "COALESCE(Sum, 0)" });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), PipelineRequestFor("Ledger", step), TestSchemaRegistry.BuildRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'agg': metric 'm' expression references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    [Fact]
    public void PipelineDerive_EmitsTheParsedExpressionWithResolvedColumns_IncludingAnEarlierWindowAlias()
    {
        var step = new PipelineStep { Name = "d" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber, OrderBy = "WordCount" });
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = "wordcount + 1" });
        step.Derive.Add(new DeriveColumn { Alias = "y", Expr = "RN * X" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), PipelineRequestFor("Article", step), TestSchemaRegistry.BuildRegistry());

        sql.Should().Contain(
            "`d` AS (SELECT *, ROW_NUMBER() OVER (ORDER BY `WordCount` ASC) AS `rn`, " +
            "(`WordCount` + 1) AS `x`, (`rn` * `x`) AS `y` FROM `base`)");
    }

    [Theory]
    [InlineData("SUM(Amount) OVER (PARTITION BY Sum)")]
    [InlineData("SUM(Amount) OVER (ORDER BY Sum DESC)")]
    [InlineData("SUM(Sum) OVER ()")]
    public void PipelineDerive_RestrictedColumnAnywhereInTheWindow_KeepsThePinnedMessage(string expression)
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = expression });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), PipelineRequestFor("Ledger", step), TestSchemaRegistry.BuildRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'd': derive 'x' references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    // ── Range bucket bounds (QB BuildRangeSql) ─────────────────────────────────────

    [Theory]
    [InlineData(double.NaN, null)]
    [InlineData(null, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 3.0)]
    public void BuildAggregate_RangeBucketWithANonFiniteBound_IsRejected(double? from, double? to)
    {
        var spec = new AggregationDescriptor(
            "r", AggregationKind.Range, "Rating", RangeBuckets: [new RangeBucketDescriptor("b", from, to)]);

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Range bucket 0 has a non-finite bound; bounds must be finite numbers.");
    }

    [Fact]
    public void BuildAggregate_RangeBounds_AreFormattedInvariantly_UnderACommaDecimalCulture()
    {
        var spec = new AggregationDescriptor(
            "r", AggregationKind.Range, "Rating", RangeBuckets: [new RangeBucketDescriptor("mid", 2.5, 7.5)]);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();

            var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

            sql.Should().Contain("WHEN `Rating` >= 2.5 AND `Rating` < 7.5 THEN @__rb0");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
