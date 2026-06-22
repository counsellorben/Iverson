using System.Diagnostics;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Reindex;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;

namespace Iverson.Elasticsearch;

public class ElasticsearchService(ElasticsearchClient client, ILogger<ElasticsearchService> logger) : IElasticsearchService
{
    public async Task IndexDocumentAsync<T>(string indexName, string id, T document) where T : class
    {
        using var activity = Telemetry.Source.StartActivity("es.index", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);
        activity?.SetTag("elasticsearch.document_id", id);

        var response = await client.IndexAsync(document, i => i.Index(indexName).Id(id));

        if (!response.IsValidResponse)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ElasticsearchServerError?.ToString());
            logger.LogError("Failed to index document {Id} in {Index}: {Error}", id, indexName, response.ElasticsearchServerError);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    public async Task<T?> GetDocumentAsync<T>(string indexName, string id) where T : class
    {
        using var activity = Telemetry.Source.StartActivity("es.get", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);
        activity?.SetTag("elasticsearch.document_id", id);

        var response = await client.GetAsync<T>(id, g => g.Index(indexName));
        activity?.SetTag("elasticsearch.found", response.Found);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return response.IsValidResponse ? response.Source : null;
    }

    public async Task<IReadOnlyCollection<T>> SearchAsync<T>(string indexName, string query) where T : class
    {
        using var activity = Telemetry.Source.StartActivity("es.search", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);
        activity?.SetTag("db.statement", query);

        var response = await client.SearchAsync<T>(s => s
            .Indices(indexName)
            .Query(q => q.QueryString(qs => qs.Query(
                string.IsNullOrWhiteSpace(query) ? "*" : query))));

        if (!response.IsValidResponse)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ElasticsearchServerError?.ToString());
            logger.LogError("Search failed in {Index}: {Error}", indexName, response.ElasticsearchServerError);
            return [];
        }

        activity?.SetTag("elasticsearch.hits", response.Total);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return response.Documents;
    }

    public async Task DeleteDocumentAsync(string indexName, string id)
    {
        using var activity = Telemetry.Source.StartActivity("es.delete", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);
        activity?.SetTag("elasticsearch.document_id", id);

        var response = await client.DeleteAsync(indexName, id);

        if (!response.IsValidResponse)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            logger.LogError("Failed to delete document {Id} from {Index}", id, indexName);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    public async Task<bool> IndexExistsAsync(string indexName)
    {
        using var activity = Telemetry.Source.StartActivity("es.index_exists", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);

        var response = await client.Indices.ExistsAsync(indexName);
        activity?.SetTag("elasticsearch.exists", response.Exists);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return response.Exists;
    }

    public async Task CreateIndexAsync(string indexName)
    {
        using var activity = Telemetry.Source.StartActivity("es.create_index", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);

        if (await IndexExistsAsync(indexName))
        {
            activity?.SetTag("elasticsearch.already_existed", true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        var response = await client.Indices.CreateAsync(indexName);
        if (!response.IsValidResponse)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ElasticsearchServerError?.ToString());
            logger.LogError("Failed to create index {Index}: {Error}", indexName, response.ElasticsearchServerError);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    public async Task ApplyMappingAsync(IndexSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("es.apply_mapping", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", schema.IndexName);

        try
        {
            var properties = BuildProperties(schema);

            if (!await IndexExistsAsync(schema.IndexName))
            {
                // First registration: create versioned physical index + alias
                var physical = VersionedName(schema.IndexName);
                await CreatePhysicalIndexAsync(physical, properties);
                await client.Indices.PutAliasAsync(new PutAliasRequest(physical, schema.IndexName));
                logger.LogInformation("Created ES index {Physical} with alias {Alias}", physical, schema.IndexName);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            // Index or alias already exists — detect field removals
            var existingFields = await GetExistingFieldNamesAsync(schema.IndexName);
            var schemaFields   = schema.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed        = existingFields.Except(schemaFields).ToList();

            if (removed.Count == 0)
            {
                // Additive only — PUT _mapping directly on the alias (resolves to physical)
                var putResp = await client.Indices.PutMappingAsync(new PutMappingRequest(schema.IndexName)
                {
                    Properties = properties
                });
                if (!putResp.IsValidResponse)
                    logger.LogError("Failed to update mapping for {Index}: {Error}",
                        schema.IndexName, putResp.ElasticsearchServerError);
                else
                    logger.LogInformation("Updated ES mapping for {Index}", schema.IndexName);

                activity?.SetStatus(putResp.IsValidResponse ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                return;
            }

            // Field removal detected — rebuild index via reindex + alias swap
            logger.LogInformation("Field removal in {Index} [{Fields}] — rebuilding",
                schema.IndexName, string.Join(", ", removed));

            var (isAlias, currentPhysical) = await ResolvePhysicalIndexAsync(schema.IndexName);
            var newPhysical = VersionedName(schema.IndexName);

            await CreatePhysicalIndexAsync(newPhysical, properties);
            await ReindexAsync(schema.IndexName, newPhysical);

            if (isAlias)
            {
                // Swap alias: remove from old physical, add to new physical
                await client.Indices.DeleteAliasAsync(new DeleteAliasRequest(currentPhysical, schema.IndexName));
                await client.Indices.PutAliasAsync(new PutAliasRequest(newPhysical, schema.IndexName));
                await client.Indices.DeleteAsync(new DeleteIndexRequest(currentPhysical));
            }
            else
            {
                // Convert from bare real index to versioned physical + alias
                await client.Indices.DeleteAsync(new DeleteIndexRequest(schema.IndexName));
                await client.Indices.PutAliasAsync(new PutAliasRequest(newPhysical, schema.IndexName));
            }

            logger.LogInformation("Migrated ES index {Alias}: {Old} → {New}",
                schema.IndexName, currentPhysical, newPhysical);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "ApplyMappingAsync failed for {Index}", schema.IndexName);
            throw;
        }
    }

    public async Task<IReadOnlyList<AggregationResult>> AggregateAsync(
        string indexName,
        string queryText,
        IReadOnlyList<AggregationSpec> specs)
    {
        using var activity = Telemetry.Source.StartActivity("es.aggregate", ActivityKind.Client);
        activity?.SetTag("db.system", "elasticsearch");
        activity?.SetTag("elasticsearch.index", indexName);
        activity?.SetTag("elasticsearch.aggregation_count", specs.Count);

        var aggregations = new Dictionary<string, Aggregation>(specs.Count);
        foreach (var spec in specs)
            aggregations[spec.Name] = BuildAggregation(spec);

        var request = new SearchRequest(indexName)
        {
            Size         = 0,
            Query        = new QueryStringQuery { Query = string.IsNullOrWhiteSpace(queryText) ? "*" : queryText },
            Aggregations = aggregations
        };

        var response = await client.SearchAsync<JsonElement>(request);

        if (!response.IsValidResponse)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ElasticsearchServerError?.ToString());
            logger.LogError("Aggregate failed for {Index}: {Error}", indexName, response.ElasticsearchServerError);
            return [];
        }

        var results = specs
            .Select(spec => ExtractResult(spec, response.Aggregations!))
            .OfType<AggregationResult>()
            .ToList();

        activity?.SetTag("elasticsearch.aggregation_results", results.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return results;
    }

    private static Aggregation BuildAggregation(AggregationSpec spec) => spec.Kind switch
    {
        AggregationKind.Terms => new TermsAggregation { Field = spec.Field, Size = spec.Size },

        AggregationKind.DateHistogram => new DateHistogramAggregation
        {
            Field            = spec.Field,
            CalendarInterval = ParseCalendarInterval(spec.CalendarInterval),
            TimeZone         = spec.TimeZone
        },

        AggregationKind.Range => new RangeAggregation
        {
            Field  = spec.Field,
            Ranges = spec.RangeBuckets?.Select(b => new AggregationRange
            {
                Key  = string.IsNullOrEmpty(b.Key) ? null : b.Key,
                From = b.From,
                To   = b.To
            }).ToList()
        },

        AggregationKind.Avg         => new AverageAggregation    { Field = spec.Field },
        AggregationKind.Sum         => new SumAggregation         { Field = spec.Field },
        AggregationKind.Min         => new MinAggregation         { Field = spec.Field },
        AggregationKind.Max         => new MaxAggregation         { Field = spec.Field },
        AggregationKind.Cardinality => new CardinalityAggregation { Field = spec.Field },

        _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind), spec.Kind, null)
    };

    private static AggregationResult? ExtractResult(AggregationSpec spec, AggregateDictionary aggs) =>
        spec.Kind switch
        {
            AggregationKind.Terms         => ExtractTerms(spec, aggs),

            AggregationKind.DateHistogram => aggs.GetDateHistogram(spec.Name) is { } dh
                ? new AggregationResult(spec.Name, spec.Kind,
                    Buckets: dh.Buckets
                        .Select(b => new AggregationBucket(
                            b.KeyAsString ?? b.Key.ToString("yyyy-MM-dd"),
                            b.DocCount))
                        .ToList())
                : null,

            AggregationKind.Range => aggs.GetRange(spec.Name) is { } r
                ? new AggregationResult(spec.Name, spec.Kind,
                    Buckets: r.Buckets
                        .Select(b => new AggregationBucket(b.Key ?? string.Empty, b.DocCount))
                        .ToList())
                : null,

            AggregationKind.Avg         => aggs.GetAverage(spec.Name)    is { } avg ? new(spec.Name, spec.Kind, MetricValue: avg.Value)           : null,
            AggregationKind.Sum         => aggs.GetSum(spec.Name)         is { } sum ? new(spec.Name, spec.Kind, MetricValue: sum.Value)           : null,
            AggregationKind.Min         => aggs.GetMin(spec.Name)         is { } mn  ? new(spec.Name, spec.Kind, MetricValue: mn.Value)            : null,
            AggregationKind.Max         => aggs.GetMax(spec.Name)         is { } mx  ? new(spec.Name, spec.Kind, MetricValue: mx.Value)            : null,
            AggregationKind.Cardinality => aggs.GetCardinality(spec.Name) is { } c   ? new(spec.Name, spec.Kind, MetricValue: (double?)c.Value)    : null,

            _ => null
        };

    private static AggregationResult? ExtractTerms(AggregationSpec spec, AggregateDictionary aggs)
    {
        List<AggregationBucket>? buckets = null;

        if (aggs.GetStringTerms(spec.Name) is { } st)
            buckets = st.Buckets.Select(b => new AggregationBucket(b.Key.ToString(), b.DocCount)).ToList();
        else if (aggs.GetLongTerms(spec.Name) is { } lt)
            buckets = lt.Buckets.Select(b => new AggregationBucket(b.Key.ToString(), b.DocCount)).ToList();
        else if (aggs.GetDoubleTerms(spec.Name) is { } dt)
            buckets = dt.Buckets.Select(b => new AggregationBucket(b.Key.ToString(), b.DocCount)).ToList();

        return buckets is not null ? new AggregationResult(spec.Name, spec.Kind, Buckets: buckets) : null;
    }

    private static CalendarInterval ParseCalendarInterval(string? interval) =>
        interval?.ToLowerInvariant() switch
        {
            "minute"  => CalendarInterval.Minute,
            "hour"    => CalendarInterval.Hour,
            "day"     => CalendarInterval.Day,
            "week"    => CalendarInterval.Week,
            "quarter" => CalendarInterval.Quarter,
            "year"    => CalendarInterval.Year,
            _         => CalendarInterval.Month
        };

    private async Task<HashSet<string>> GetExistingFieldNamesAsync(string indexName)
    {
        var resp = await client.Indices.GetMappingAsync(new GetMappingRequest(indexName));
        if (!resp.IsValidResponse) return [];

        // GetMappingResponse.Mappings: IReadOnlyDictionary<string, IndexMappingRecord>
        // IndexMappingRecord.Mappings: TypeMapping → .Properties: Properties (IDictionary<PropertyName, IProperty>)
        var properties = resp.Mappings.Values.FirstOrDefault()?.Mappings.Properties;
        return properties is null
            ? []
            : properties.Select(kvp => kvp.Key.ToString())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Returns (isAlias, physicalIndexName).
    // GET /_alias/{alias_name} returns aliases=populated when it is an alias name;
    // returns 404/empty when the name is a real index.
    private async Task<(bool IsAlias, string Physical)> ResolvePhysicalIndexAsync(string logicalName)
    {
        // Disambiguate: pass as Names (alias lookup), not Indices (index lookup)
        var resp = await client.Indices.GetAliasAsync(new GetAliasRequest((Names)logicalName));

        if (resp.IsValidResponse && resp.Aliases.Count > 0)
            return (true, resp.Aliases.Keys.First());

        return (false, logicalName);
    }

    private async Task CreatePhysicalIndexAsync(string physicalName, Properties properties)
    {
        var resp = await client.Indices.CreateAsync(new CreateIndexRequest(physicalName)
        {
            Mappings = new TypeMapping { Properties = properties }
        });
        if (!resp.IsValidResponse)
            throw new InvalidOperationException(
                $"Failed to create ES index '{physicalName}': {resp.ElasticsearchServerError}");
    }

    private async Task ReindexAsync(string source, string dest)
    {
        var resp = await client.ReindexAsync(new ReindexRequest
        {
            Source = new Source { Indices = Indices.Index(source) },
            Dest   = new Destination { Index = dest }
        });
        if (!resp.IsValidResponse)
            throw new InvalidOperationException(
                $"Reindex '{source}' → '{dest}' failed: {resp.ElasticsearchServerError}");
    }

    private Properties BuildProperties(IndexSchema schema)
    {
        var properties = new Properties();
        foreach (var field in schema.Fields)
            AddProperty(properties, field);
        return properties;
    }

    private static string VersionedName(string logicalName) =>
        $"{logicalName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

    private static void AddProperty(Properties properties, FieldMapping field)
    {
        IProperty prop = field.FieldType switch
        {
            EsFieldType.Text        => new TextProperty
                                       {
                                           Fields = new Properties
                                           {
                                               { "keyword", new KeywordProperty() }
                                           }
                                       },
            EsFieldType.Keyword     => new KeywordProperty(),
            EsFieldType.Integer     => new IntegerNumberProperty(),
            EsFieldType.Long        => new LongNumberProperty(),
            EsFieldType.Float       => new FloatNumberProperty(),
            EsFieldType.Double      => new DoubleNumberProperty(),
            EsFieldType.Boolean     => new BooleanProperty(),
            EsFieldType.Date        => new DateProperty(),
            EsFieldType.DenseVector => new DenseVectorProperty
                                       {
                                           Dims      = field.VectorDims ?? 1536,
                                           Similarity = DenseVectorSimilarity.Cosine
                                       },
            _                       => new KeywordProperty()
        };
        properties.Add(field.Name, prop);
    }
}
