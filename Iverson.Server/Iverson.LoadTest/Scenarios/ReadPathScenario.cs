using Dapper;
using System.Diagnostics;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Reporting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.LoadTest.Scenarios;

public sealed class ReadPathScenario(
    LoadTestConfig                                      config,
    ObjectRetrievalService.ObjectRetrievalServiceClient retrieval,
    ObjectSearchService.ObjectSearchServiceClient       search,
    ActingUserIdentities                                identities,
    ILogger<ReadPathScenario>                           logger)
{
    private static readonly int[] GetManyBatches    = [1, 10, 100, 500];
    private static readonly int[] AggregateCounts   = [1, 3, 6];

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        Console.WriteLine($"[read-path] concurrency={flags.Concurrency} iterations={flags.Iterations}\n");

        // Load a sample of article IDs from Postgres for key-based benchmarks
        Console.WriteLine("Loading article IDs from Postgres...");
        Guid[] articleIds;
        await using (var pg = new NpgsqlConnection(config.PostgresCs))
        {
            await pg.OpenAsync(ct);
            articleIds = [.. (await pg.QueryAsync<Guid>(
                "SELECT \"Id\" FROM benchmark_articles LIMIT 10000"))];
        }

        if (articleIds.Length == 0)
        {
            Console.Error.WriteLine("No benchmark_articles found. Run 'dotnet run -- seed' first.");
            return;
        }

        Console.WriteLine($"Loaded {articleIds.Length:N0} article IDs.\n");

        var path = BenchmarkReport.ResultsPath("read-path");
        await using var file = new StreamWriter(path, append: false);

        // ── GetMany — batch scaling ────────────────────────────────────────────
        foreach (var batchSize in GetManyBatches)
        {
            var report = new BenchmarkReport();
            Console.WriteLine($"[GetMany] batch={batchSize} iterations={flags.Iterations}...");

            for (var iter = 0; iter < flags.Iterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                var keys = SampleKeys(articleIds, batchSize, iter);
                var req  = new RetrievalManyRequest
                {
                    TypeName = "BenchmarkArticle",
                    TraceId  = Guid.NewGuid().ToString(),
                };
                req.Keys.AddRange(keys.Select(k => k.ToString()));

                var identity = identities.PickRandom();
                var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

                var t0 = BenchmarkReport.NowMicros();
                try
                {
                    var call = retrieval.GetMany(req, headers);
                    await foreach (var _ in call.ResponseStream.ReadAllAsync(ct)) { }
                    report.Record(BenchmarkReport.NowMicros() - t0);
                }
                catch (RpcException ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "GetMany failed");
                }
            }

            report.Print($"GetMany | batch={batchSize} | iterations={flags.Iterations}", file);
        }

        // ── GetMany — BenchmarkAuthor / BenchmarkTag (per CDR round 1 §3.1: read-path must
        // exercise all three entities' authorization rules, not just BenchmarkArticle's) ──
        foreach (var (typeName, tableName) in new[]
                 { ("BenchmarkAuthor", "benchmark_authors"), ("BenchmarkTag", "benchmark_tags") })
        {
            Guid[] ids;
            await using (var pg = new NpgsqlConnection(config.PostgresCs))
            {
                await pg.OpenAsync(ct);
                ids = [.. (await pg.QueryAsync<Guid>($"SELECT \"Id\" FROM {tableName} LIMIT 10000"))];
            }
            if (ids.Length == 0)
            {
                Console.WriteLine($"[GetMany] no {tableName} found — skipping.");
                continue;
            }

            foreach (var batchSize in GetManyBatches)
            {
                var report = new BenchmarkReport();
                Console.WriteLine($"[GetMany] type={typeName} batch={batchSize} iterations={flags.Iterations}...");

                for (var iter = 0; iter < flags.Iterations; iter++)
                {
                    ct.ThrowIfCancellationRequested();
                    var keys = SampleKeys(ids, batchSize, iter);
                    var req  = new RetrievalManyRequest { TypeName = typeName, TraceId = Guid.NewGuid().ToString() };
                    req.Keys.AddRange(keys.Select(k => k.ToString()));

                    var identity = identities.PickRandom();
                    var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

                    var t0 = BenchmarkReport.NowMicros();
                    try
                    {
                        var call = retrieval.GetMany(req, headers);
                        await foreach (var _ in call.ResponseStream.ReadAllAsync(ct)) { }
                        report.Record(BenchmarkReport.NowMicros() - t0);
                    }
                    catch (RpcException ex)
                    {
                        report.RecordError();
                        logger.LogDebug(ex, "GetMany failed for {Type}", typeName);
                    }
                }

                report.Print($"GetMany | type={typeName} | batch={batchSize} | iterations={flags.Iterations}", file);
            }
        }

        // ── Search — filter profiles ───────────────────────────────────────────
        var searchProfiles = new (string Label, SearchQuery Query)[]
        {
            (
                "Simple (1 clause)",
                new SearchQuery
                {
                    Logic = SearchLogic.And,
                    Clauses =
                    {
                        new SearchClause
                        {
                            Property = "Category",
                            Operator = SearchOperator.Equals,
                            Value    = new SearchValue { StringVal = "sports" },
                        },
                    },
                }
            ),
            (
                "Medium (2 clauses + sort)",
                new SearchQuery
                {
                    Logic = SearchLogic.And,
                    Clauses =
                    {
                        new SearchClause
                        {
                            Property = "Category",
                            Operator = SearchOperator.Equals,
                            Value    = new SearchValue { StringVal = "sports" },
                        },
                        new SearchClause
                        {
                            Property = "WordCount",
                            Operator = SearchOperator.GreaterThan,
                            Value    = new SearchValue { NumberVal = 500 },
                        },
                    },
                    Sort = { new SearchSort { Property = "PublishedAt", Descending = true } },
                }
            ),
            (
                "Complex (2 clauses + sort + LIKE)",
                new SearchQuery
                {
                    Logic = SearchLogic.And,
                    Clauses =
                    {
                        new SearchClause
                        {
                            Property = "Category",
                            Operator = SearchOperator.Equals,
                            Value    = new SearchValue { StringVal = "sports" },
                        },
                        new SearchClause
                        {
                            Property = "WordCount",
                            Operator = SearchOperator.GreaterThan,
                            Value    = new SearchValue { NumberVal = 500 },
                        },
                        new SearchClause
                        {
                            Property = "Title",
                            Operator = SearchOperator.Contains,
                            Value    = new SearchValue { StringVal = "championship" },
                        },
                    },
                    Sort = { new SearchSort { Property = "PublishedAt", Descending = true } },
                }
            ),
        };

        // Exclude Body so StarRocks can route through the search MV (sorted by Category, PublishedAt)
        string[] searchFields = ["Title", "BenchmarkAuthorId", "Category", "WordCount", "PublishedAt"];

        foreach (var (label, query) in searchProfiles)
        {
            var report = new BenchmarkReport();
            Console.WriteLine($"[Search] {label} — {flags.Iterations} iterations...");

            for (var iter = 0; iter < flags.Iterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                var req = new SearchRequest
                {
                    TypeName = "BenchmarkArticle",
                    Query    = query,
                    Page     = 0,
                    PageSize = 50,
                    TraceId  = Guid.NewGuid().ToString(),
                };
                req.Fields.AddRange(searchFields);

                var identity = identities.PickRandom();
                var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

                var t0 = BenchmarkReport.NowMicros();
                try
                {
                    var call = search.Search(req, headers);
                    await foreach (var _ in call.ResponseStream.ReadAllAsync(ct)) { }
                    report.Record(BenchmarkReport.NowMicros() - t0);
                }
                catch (RpcException ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "Search failed");
                }
            }

            report.Print($"Search | {label}", file);
        }

        // ── Aggregate — spec count scaling ────────────────────────────────────
        var allSpecs = new AggregationSpec[]
        {
            new() { Name = "by_category",   Type = AggregationType.Terms,         Field = "Category",    Size = 5 },
            new() { Name = "by_wordcount",  Type = AggregationType.Range,         Field = "WordCount",
                RangeBuckets =
                {
                    new RangeBucket { Key = "low",  To   = 200.0 },
                    new RangeBucket { Key = "mid",  From = 200.0, To = 500.0 },
                    new RangeBucket { Key = "high", From = 500.0 },
                }
            },
            new() { Name = "pub_by_month",  Type = AggregationType.DateHistogram, Field = "PublishedAt", CalendarInterval = "month" },
            new() { Name = "avg_wordcount", Type = AggregationType.Avg,           Field = "WordCount" },
            new() { Name = "min_wordcount", Type = AggregationType.Min,           Field = "WordCount" },
            new() { Name = "max_wordcount", Type = AggregationType.Max,           Field = "WordCount" },
        };

        var baseQuery = new SearchQuery
        {
            Logic = SearchLogic.And,
            Clauses =
            {
                new SearchClause
                {
                    Property = "Category",
                    Operator = SearchOperator.Equals,
                    Value = new SearchValue
                    { StringVal = "sports" }
                }
            }};

        foreach (var specCount in AggregateCounts)
        {
            var report = new BenchmarkReport();
            Console.WriteLine($"[Aggregate] specs={specCount} — {flags.Iterations} iterations...");

            for (var iter = 0; iter < flags.Iterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                var req = new AggregateRequest
                {
                    TypeName = "BenchmarkArticle",
                    Query    = baseQuery,
                    TraceId  = Guid.NewGuid().ToString(),
                };
                req.Aggregations.AddRange(allSpecs.Take(specCount));

                var identity = identities.PickRandom();
                var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

                var t0 = BenchmarkReport.NowMicros();
                try
                {
                    await search.AggregateAsync(req, headers, cancellationToken: ct);
                    report.Record(BenchmarkReport.NowMicros() - t0);
                }
                catch (RpcException ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "Aggregate failed");
                }
            }

            report.Print($"Aggregate | specs={specCount} | iterations={flags.Iterations}", file);
        }

        Console.WriteLine($"\nReport saved to {path}");
    }

    private static Guid[] SampleKeys(Guid[] pool, int count, int seed)
    {
        var result = new Guid[Math.Min(count, pool.Length)];
        for (var i = 0; i < result.Length; i++)
            result[i] = pool[(seed * count + i) % pool.Length];
        return result;
    }
}
