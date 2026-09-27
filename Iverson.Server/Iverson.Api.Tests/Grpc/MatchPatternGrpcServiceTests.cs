using System.Diagnostics;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using ProtoRowsPerMatch = Iverson.Client.Contracts.RowsPerMatch;
using AuthorizationRules = Iverson.Api.Schema.AuthorizationRules;
using RowPermission = Iverson.Api.Schema.RowPermission;
using FieldPermission = Iverson.Api.Schema.FieldPermission;

namespace Iverson.Api.Tests.Grpc;

public sealed class MatchPatternGrpcServiceTests
{
    private const string SigningKey = "test-signing-key-0123456789abcdef";
    private readonly SchemaRegistry _registry;
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorQueryService _vector = Substitute.For<IVectorQueryService>();
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly IEmbeddingServiceResolver _resolver = Substitute.For<IEmbeddingServiceResolver>();
    private readonly IChunkRowSource _chunkRows = Substitute.For<IChunkRowSource>();
    private readonly ActingUserAccessor _actingUserAccessor = new()
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly IntelligenceTenantScope _tenantScope = new(SigningKey);

    public MatchPatternGrpcServiceTests()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(sql), NullLogger<SchemaRegistry>.Instance);
        _resolver.Get(Arg.Any<string?>()).Returns(_embedding);
    }

    private ObjectSearchGrpcService Sut(PatternQueryLimitOptions? limits = null) => new(
        _registry, _search, _vector, _resolver, NullLogger<ObjectSearchGrpcService>.Instance,
        _actingUserAccessor, new RowFieldAuthorizationEvaluator(), _tenantScope,
        new ResultReranker(Options.Create(new VectorRankingOptions())), new ResultDiversifier(),
        Options.Create(new VectorRankingOptions()), Options.Create(new DecayOptions()),
        Options.Create(new PopularitySignalOptions()), EngagementQueryLimitOptions.Default,
        limits ?? PatternQueryLimitOptions.Default, _chunkRows);

    private static (IServerStreamWriter<MatchPatternResponse> Writer, List<MatchPatternResponse> Written) MakeStream()
    {
        var written = new List<MatchPatternResponse>();
        var writer = Substitute.For<IServerStreamWriter<MatchPatternResponse>>();
        writer.WriteAsync(Arg.Do<MatchPatternResponse>(written.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (writer, written);
    }

    private static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private void SourceRows(params IDictionary<string, object?>[] rows) =>
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Any<MatchRowsRequest>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Async(rows));

    private static MatchPatternRequest Req(
        string pattern, (string Name, string Expr)[]? define = null, (string Name, string Expr)[]? measures = null,
        ProtoRowsPerMatch rows = ProtoRowsPerMatch.OneRow, string type = "Author", string[]? orderBy = null,
        string[]? partitionBy = null, int limit = 0, PatternRowSource source = PatternRowSource.TypeRows,
        string chunkProperty = "")
    {
        var r = new MatchPatternRequest
        {
            TypeName = type, Source = source, ChunkProperty = chunkProperty, Pattern = pattern,
            RowsPerMatch = rows, Limit = limit, TraceId = "t",
        };
        foreach (var o in orderBy ?? (source == PatternRowSource.TypeRows ? ["Id"] : [])) r.OrderBy.Add(new SearchSort { Property = o });
        foreach (var p in partitionBy ?? []) r.PartitionBy.Add(p);
        foreach (var (n, e) in define ?? []) r.Define.Add(new NamedExpr { Name = n, Expr = e });
        foreach (var (n, e) in measures ?? []) r.Measures.Add(new NamedExpr { Name = n, Expr = e });
        return r;
    }

    private async Task<List<MatchPatternResponse>> RunAsync(MatchPatternRequest request, PatternQueryLimitOptions? limits = null)
    {
        var (writer, written) = MakeStream();
        await Sut(limits).MatchPattern(request, writer, TestServerCallContext.Create());
        return written;
    }

    private async Task<StatusCode> StatusOfAsync(MatchPatternRequest request, PatternQueryLimitOptions? limits = null)
    {
        var act = () => RunAsync(request, limits);
        return (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode;
    }

    // ── happy paths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Type_rows_all_rows_output_carries_columns_measures_match_number_classifier_and_trace_id()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        MatchRowsRequest? sent = null;
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Do<MatchRowsRequest>(r => sent = r),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Async(new[]
            {
                Row(("Id", "1"), ("Name", "a"), ("Bio", "x")), Row(("Id", "2"), ("Name", "b"), ("Bio", "y")),
                Row(("Id", "3"), ("Name", "c"), ("Bio", "z")),
            }));

        var written = await RunAsync(Req("A B+", [("B", "B.Name > PREV(B.Name)")], [("m", "MATCH_NUMBER()")],
            ProtoRowsPerMatch.AllRowsShowEmpty));

        written.Select(w => w.Classifier).Should().Equal("A", "B", "B");
        written.Should().OnlyContain(w => w.MatchNumber == 1 && w.TraceId == "t");
        written[0].Data.Fields.Keys.Should().Equal("Id", "Name", "Bio", "m");
        written[1].Data.Fields["Name"].StringValue.Should().Be("b");
        sent!.OneRowPerMatch.Should().BeFalse();
        sent.ReferencedColumns.Should().BeEquivalentTo(["Name"]);
        sent.MeasureNames.Should().Equal("m");
        sent.MaxRowsScanned.Should().Be(PatternQueryLimitOptions.Default.MaxRowsScanned);
        sent.OrderBy.Select(o => o.Property).Should().Equal("Id");
    }

    [Fact]
    public async Task One_row_emits_the_partition_column_under_the_rows_spelling_then_the_measures()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")), Row(("Id", "2"), ("Name", "a")), Row(("Id", "3"), ("Name", "b")));

        var written = await RunAsync(Req("A+", measures: [("n", "COUNT(*)")], partitionBy: ["name"]));

        written.Should().HaveCount(2);
        written[0].Data.Fields.Keys.Should().Equal("Name", "n");
        written[0].Data.Fields["n"].NumberValue.Should().Be(2);
        written[0].Classifier.Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenant_column_is_stripped_from_every_output_row()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a"), (SchemaDescriptor.TenantColumnName, "test-tenant")));

        var written = await RunAsync(Req("A", rows: ProtoRowsPerMatch.AllRowsShowEmpty));

        written.Single().Data.Fields.Keys.Should().NotContain(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public async Task Limit_stops_the_stream_and_zero_means_the_default()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Enumerable.Range(1, 5).Select(i => Row(("Id", $"{i}"), ("Name", "a"))).ToArray());

        (await RunAsync(Req("A", limit: 3))).Should().HaveCount(3);
        (await RunAsync(Req("A", limit: 0))).Should().HaveCount(5);
    }

    [Fact]
    public async Task Null_values_flow_through_as_sql_null()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a"), ("Bio", null)));

        var all = await RunAsync(Req("A", [("A", "A.Bio IS NULL")],
            [("c", "COALESCE(A.Bio, 'none')"), ("f", "FIRST(A.Bio)")], ProtoRowsPerMatch.AllRowsShowEmpty));
        all.Single().Data.Fields["Bio"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);
        all.Single().Data.Fields["c"].StringValue.Should().Be("none");
        all.Single().Data.Fields["f"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);

        var one = await RunAsync(Req("A", partitionBy: ["Bio"]));
        one.Single().Data.Fields["Bio"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);
    }

    [Fact]
    public async Task Type_rows_similarity_scores_reach_the_engine_from_the_callers_tenant_collection()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var (id1, id2) = ("0190a1b2-0000-7000-8000-000000000001", "0190a1b2-0000-7000-8000-000000000002");
        SourceRows(Row(("Id", id1), ("Title", "refund policy")), Row(("Id", id2), ("Title", "shipping")));
        _embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);
        var collection = _tenantScope.ResolveCollectionName("articles", "test-tenant", isChunks: false);
        _vector.RetrieveNamedVectorAsync(collection, Arg.Any<IReadOnlyList<ulong>>(), "title_vector", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<ulong, float[]>
            {
                [IntelligenceStoreConsumer.KeyToUlong(id1)] = [1f, 0f, 0f, 0f],
                [IntelligenceStoreConsumer.KeyToUlong(id2)] = [0f, 1f, 0f, 0f],
            });

        var written = await RunAsync(Req("A", [("A", "SIMILARITY(title, 'refund') > 0.5")],
            [("s", "SIMILARITY(Title, 'refund')")], type: "Article"));

        written.Should().ContainSingle().Which.Data.Fields["s"].NumberValue.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public async Task Chunks_feed_parent_key_partitions_and_chunk_vectors_from_the_callers_chunks_collection()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        ChunkRowQuery? query = null;
        _chunkRows.ReadAsync(Arg.Do<ChunkRowQuery>(q => query = q), Arg.Any<CancellationToken>()).Returns(_ => Async(new[]
        {
            new ChunkRow("p1", 0, "intro", [0f, 1f, 0f, 0f]), new ChunkRow("p1", 1, "refunds", [1f, 0f, 0f, 0f]),
            new ChunkRow("p2", 0, "refunds", [1f, 0f, 0f, 0f]),
        }));
        _embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);

        var written = await RunAsync(Req("A* B", [("B", "SIMILARITY(text, 'refund') > 0.9")], [("n", "COUNT(*)")],
            type: "Article", source: PatternRowSource.Chunks, chunkProperty: "body"));

        written.Select(w => (w.Data.Fields["parent_key"].StringValue, w.Data.Fields["n"].NumberValue))
            .Should().Equal(("p1", 2.0), ("p2", 1.0));
        query!.ChunksCollection.Should().Be(_tenantScope.ResolveCollectionName("articles", "test-tenant", isChunks: true));
        query.Field.Should().Be("Body");
        query.VectorName.Should().Be("body_vector");
        query.BatchRows.Should().Be(PatternQueryLimitOptions.Default.BatchRows);
        query.MaxRowsScanned.Should().Be(PatternQueryLimitOptions.Default.MaxRowsScanned);
    }

    [Fact]
    public async Task A_re_cased_chunk_property_is_accepted_for_a_field_restricted_caller_allowed_the_property()
    {
        var restricted = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(null, [new RowPermission("test-bypass", true, true, true)],
                [new FieldPermission("Title", ["admin"], [])]),
        };
        await _registry.RegisterAsync(restricted);
        _chunkRows.ReadAsync(Arg.Any<ChunkRowQuery>(), Arg.Any<CancellationToken>()).Returns(_ => Async(new[] { new ChunkRow("p1", 0, "x", null) }));

        var written = await RunAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "BODY"));

        written.Should().ContainSingle();
    }

    [Fact]
    public async Task Tenant_isolation_the_callers_tenant_scopes_the_row_read()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null;
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Any<MatchRowsRequest>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>>(a => authz = a), Arg.Any<CancellationToken>())
            .Returns(_ => Async(Array.Empty<IDictionary<string, object?>>()));

        await RunAsync(Req("A"));

        authz!["Author"].TenantValue.Should().Be("test-tenant");
    }

    [Fact]
    public async Task A_1000_character_nested_pattern_and_expression_run_on_a_thread_pool_thread()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")));
        var pattern = new string('(', 499) + "A" + new string(')', 499);                    // 999 characters
        var define = new string('(', 498) + "TRUE" + new string(')', 498);                  // 1,000 characters

        var written = await Task.Run(() => RunAsync(Req(pattern, [("A", define)])));

        written.Should().ContainSingle();
    }

    // ── §6 failure semantics ────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_type_is_FailedPrecondition() =>
        (await StatusOfAsync(Req("A", type: "Nope"))).Should().Be(StatusCode.FailedPrecondition);

    [Fact]
    public async Task A_denied_caller_gets_an_empty_stream_and_no_store_is_read()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _actingUserAccessor.ActingUser = null;

        (await RunAsync(Req("A", [("A", "SIMILARITY(title, 'q') > 0")], type: "Article"))).Should().BeEmpty();
        (await RunAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body"))).Should().BeEmpty();

        _search.ReceivedCalls().Should().BeEmpty();
        _chunkRows.ReceivedCalls().Should().BeEmpty();
        _embedding.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_precedes_the_denial_check()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        (await StatusOfAsync(Req("A |"))).Should().Be(StatusCode.InvalidArgument);
    }

    public static TheoryData<string> LimitViolations() => new()
    {
        "pattern", "expression", "defines", "measures", "subsets", "where", "limit-high", "limit-negative",
        "type-rows-no-order-by", "chunks-partition-by", "chunks-order-by", "chunks-or-filter",
    };

    [Theory]
    [MemberData(nameof(LimitViolations))]
    public async Task A_limit_or_shape_violation_is_InvalidArgument_before_any_store_call(string violation)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var limits = new PatternQueryLimitOptions
        {
            MaxPatternLength = 10, MaxExpressionLength = 10, MaxDefines = 1, MaxMeasures = 1, MaxSubsets = 1, MaxOutputRows = 5,
        };
        var clause = new SearchClause { Property = "Title", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" } };
        var request = violation switch
        {
            "pattern" => Req("A B C D E F", type: "Article"),
            "expression" => Req("A", [("A", "A.Title = 'abcdefgh'")], type: "Article"),
            "defines" => Req("A B", [("A", "TRUE"), ("B", "TRUE")], type: "Article"),
            "measures" => Req("A", measures: [("m", "1"), ("n", "2")], type: "Article"),
            "subsets" => WithSubsets(Req("A B", type: "Article")),
            "where" => WithWhere(Req("A", type: "Article"), Enumerable.Repeat(clause, EngagementQueryLimitOptions.Default.MaxClauses + 1)),
            "limit-high" => Req("A", type: "Article", limit: 6),
            "limit-negative" => Req("A", type: "Article", limit: -1),
            "type-rows-no-order-by" => Req("A", type: "Article", orderBy: []),
            "chunks-partition-by" => Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body", partitionBy: ["Title"]),
            "chunks-order-by" => Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body", orderBy: ["Title"]),
            _ => WithWhere(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body"), [clause, clause], SearchLogic.Or),
        };

        (await StatusOfAsync(request, limits)).Should().Be(StatusCode.InvalidArgument);
        _search.ReceivedCalls().Should().BeEmpty();
        _chunkRows.ReceivedCalls().Should().BeEmpty();
    }

    private static MatchPatternRequest WithSubsets(MatchPatternRequest r)
    {
        r.Subsets.Add(new PatternSubset { Name = "U", Variables = { "A" } });
        r.Subsets.Add(new PatternSubset { Name = "V", Variables = { "B" } });
        return r;
    }

    private static MatchPatternRequest WithWhere(MatchPatternRequest r, IEnumerable<SearchClause> clauses, SearchLogic logic = SearchLogic.And)
    {
        r.Where.AddRange(clauses);
        r.WhereLogic = logic;
        return r;
    }

    [Theory]
    [InlineData("A |", null)]                           // pattern grammar
    [InlineData("A", "A.Title +")]                      // expression grammar
    [InlineData("A{5000}", null)]                       // MaxProgramInstructions (default 5,000)
    [InlineData("A", "SIMILARITY(Body, 'q') > 0")]      // Body has no [IversonEmbedding]
    public async Task Pattern_expression_and_similarity_validation_is_InvalidArgument_before_any_store_call(string pattern, string? define)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        (await StatusOfAsync(Req(pattern, define is null ? null : [("A", define)], type: "Article")))
            .Should().Be(StatusCode.InvalidArgument);
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task More_distinct_similarity_terms_than_MaxSimilarityTerms_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'a') > SIMILARITY(Title, 'b')")], type: "Article"),
            new PatternQueryLimitOptions { MaxSimilarityTerms = 1 })).Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_similarity_column_the_caller_may_not_read_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(null, [new RowPermission("test-bypass", true, true, true)],
                [new FieldPermission("Title", ["admin"], [])]),
        });

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(title, 'q') > 0")], type: "Article")))
            .Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_bad_chunk_property_is_InvalidArgument_and_a_type_without_a_collection_is_FailedPrecondition()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        (await StatusOfAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Title")))
            .Should().Be(StatusCode.InvalidArgument);

        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { TypeName = "Bare", TableName = "bares", CollectionName = null });
        (await StatusOfAsync(Req("A", type: "Bare", source: PatternRowSource.Chunks, chunkProperty: "Body")))
            .Should().Be(StatusCode.FailedPrecondition);
        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'q') > 0")], type: "Bare")))
            .Should().Be(StatusCode.FailedPrecondition);
    }

    public static TheoryData<string, StatusCode> StoreFailures() => new()
    {
        { "translation", StatusCode.InvalidArgument },
        { "not-ready", StatusCode.Unavailable },
        { "disabled", StatusCode.FailedPrecondition },
        { "budget", StatusCode.ResourceExhausted },
    };

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task Store_exceptions_map_as_the_spec_section_6_table_says(string failure, StatusCode expected)
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        Exception ex = failure switch
        {
            "translation" => new EngagementQueryTranslationException("unknown column 'x'"),
            "not-ready" => new EngagementNotReadyException("warming up"),
            "disabled" => new EngagementStoreDisabledException("off"),
            _ => new Iverson.Patterns.PatternBudgetExceededException("MaxRowsScanned"),
        };
        _search.MatchRowsAsync(default!, default!, default!, default).ThrowsForAnyArgs(ex);

        (await StatusOfAsync(Req("A"))).Should().Be(expected);
    }

    [Fact]
    public async Task Any_other_store_exception_propagates_unchanged()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.MatchRowsAsync(default!, default!, default!, default).ThrowsForAnyArgs(new InvalidOperationException("boom"));

        var act = () => RunAsync(Req("A"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task A_run_time_evaluation_error_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")));

        (await StatusOfAsync(Req("A", [("A", "A.Name < 1")]))).Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_budget_hit_after_rows_were_written_still_ends_the_stream_with_an_error()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")), Row(("Id", "2"), ("Name", "b")), Row(("Id", "3"), ("Name", "c")));
        var (writer, written) = MakeStream();

        var act = () => Sut(new PatternQueryLimitOptions { MaxRowsScanned = 2, BatchRows = 1 })
            .MatchPattern(Req("A", partitionBy: ["Name"]), writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        written.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("unavailable", StatusCode.Unavailable)]
    [InlineData("empty", StatusCode.InvalidArgument)]
    public async Task Embedding_failures_map_as_SearchChunks_does(string failure, StatusCode expected)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedQueryAsync(default!, default).ThrowsAsyncForAnyArgs(failure == "empty"
            ? new EmptyEmbeddingInputException("empty")
            : new HttpRequestException("down"));

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'q') > 0")], type: "Article"))).Should().Be(expected);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("budget")]
    public async Task Any_exception_raised_after_the_timeout_token_fires_is_DeadlineExceeded(string failure)
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.MatchRowsAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(ci => WaitThenFail(ci.ArgAt<CancellationToken>(3), failure));

        (await StatusOfAsync(Req("A"), new PatternQueryLimitOptions { TimeoutSeconds = 1 })).Should().Be(StatusCode.DeadlineExceeded);

        static async IAsyncEnumerable<IDictionary<string, object?>> WaitThenFail(CancellationToken ct, string failure)
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested)
            {
                // "budget": a §6 row the timeout rule must still win over, so the catch order is pinned.
                Exception ex = failure == "budget"
                    ? new Iverson.Patterns.PatternBudgetExceededException("MaxRowsScanned")
                    : new InvalidOperationException("a cancelled StarRocks read surfaces as some other exception (spec §3.2)");
                throw ex;
            }
            yield break;
        }
    }

    [Fact]
    public async Task The_timeout_stops_an_output_heavy_run_within_about_one_output_row()
    {
        // CDR-8 P6's shape: one 10,000-row match, ALL_ROWS, 50 SUM measures — O(n²) measure work (576 s in full).
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Enumerable.Range(0, 10_000).Select(i => Row(("Id", $"{i:D5}"), ("Name", "a"), ("x", 1L))).ToArray());
        var measures = Enumerable.Range(0, 50).Select(i => ($"s{i}", "SUM(A.x)")).ToArray();
        var watch = Stopwatch.StartNew();

        var status = await StatusOfAsync(Req("A+", measures: measures, rows: ProtoRowsPerMatch.AllRowsShowEmpty, limit: 10_000),
            new PatternQueryLimitOptions { TimeoutSeconds = 1 });

        status.Should().Be(StatusCode.DeadlineExceeded);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the token is checked before each output row");
    }

    [Fact]
    public async Task The_budget_token_stops_a_matching_heavy_run_that_writes_no_row()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Enumerable.Range(0, 3_000).Select(i => Row(("Id", $"{i:D5}"), ("Name", "a"), ("x", 1L))).ToArray());

        var status = await StatusOfAsync(Req("A+ B", [("A", "SUM(A.x) >= 0"), ("B", "FALSE")]),
            new PatternQueryLimitOptions { TimeoutSeconds = 1 }).WaitAsync(TimeSpan.FromSeconds(10));

        status.Should().Be(StatusCode.DeadlineExceeded);
    }

    [Fact]
    public async Task After_limit_the_rpc_completes_without_awaiting_the_row_source_disposal()
    {
        // Option B of the §9.3 pause decision: a frozen StarRocks makes reader disposal wait until it resumes
        // (MatchRowsIntegrationTests proves the premise); the RPC must not wait with it.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var release = new TaskCompletionSource();
        var disposed = new TaskCompletionSource();
        _search.MatchRowsAsync(default!, default!, default!, default).ReturnsForAnyArgs(_ => Endless(release, disposed));

        var rpc = RunAsync(Req("A", partitionBy: ["Id"], limit: 2), new PatternQueryLimitOptions { BatchRows = 1 });

        (await rpc.WaitAsync(TimeSpan.FromSeconds(5))).Should().HaveCount(2);
        disposed.Task.IsCompleted.Should().BeFalse("disposal is still draining");
        release.SetResult();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        static async IAsyncEnumerable<IDictionary<string, object?>> Endless(TaskCompletionSource release, TaskCompletionSource disposed)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    await Task.Yield();
                    yield return Row(("Id", $"{i}"), ("Name", "a"));
                }
            }
            finally
            {
                await release.Task;
                disposed.SetResult();
            }
        }
    }

    // ── ruling additions: branches the brief's tests did not cover ─────────

    [Fact]
    public async Task A_chunk_property_the_caller_may_not_read_is_InvalidArgument_and_no_chunk_is_read()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(null, [new RowPermission("test-bypass", true, true, true)],
                [new FieldPermission("Body", ["admin"], [])]),
        });

        (await StatusOfAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body")))
            .Should().Be(StatusCode.InvalidArgument);
        _chunkRows.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Type_rows_excludes_the_schemas_bytes_columns_from_the_row_read()
    {
        var author = SchemaFixtures.AuthorSchema();
        await _registry.RegisterAsync(author with
        {
            ScalarColumns = [.. author.ScalarColumns, new ColumnDescriptor("Avatar", "BYTEA", true)],
        });
        MatchRowsRequest? sent = null;
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Do<MatchRowsRequest>(r => sent = r),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Async(Array.Empty<IDictionary<string, object?>>()));

        await RunAsync(Req("A"));

        sent!.ExcludedColumns.Should().BeEquivalentTo(["Avatar"]);
    }
}
