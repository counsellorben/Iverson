using System.Diagnostics;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Microsoft.Extensions.Logging;

namespace Iverson.Client.Core;

/// <summary>
/// The single surface developers use to interact with Iverson entities.
/// Routes each operation to the correct gRPC service; completely ignorant of
/// what backing stores the server uses.
/// </summary>
public sealed class EntityCoordinator<T>(
    EntityRegistry registry,
    GraphAssembler assembler,
    ObjectMappingService.ObjectMappingServiceClient mapping,
    ObjectPersistenceService.ObjectPersistenceServiceClient persistence,
    ObjectRetrievalService.ObjectRetrievalServiceClient retrieval,
    ObjectSearchService.ObjectSearchServiceClient search,
    ILogger<EntityCoordinator<T>> logger,
    ActingUserIdentity? identity = null)
    where T : class
{
    private readonly EntityDescriptor _descriptor = registry.Get<T>();

    private IReadOnlyCollection<string> NavPropertyNames =>
        _descriptor.Relations.Select(r => r.Property.Name).ToList();

    // Deliberately not `readonly`: WithActingUser assigns it through an object initializer on a
    // freshly constructed instance, which the compiler rejects on a readonly field. Nothing else
    // ever writes it, so the instance is still effectively immutable after construction.
    private Func<Task<string>>? _boundActingUser;

    /// <summary>
    /// Returns a coordinator bound to <paramref name="tokenProvider"/>, leaving this one
    /// untouched. The bound identity outranks the client's ambient one; explicit headers
    /// carrying an acting-user entry outrank both.
    /// </summary>
    public EntityCoordinator<T> WithActingUser(Func<Task<string>> tokenProvider) =>
        new(registry, assembler, mapping, persistence, retrieval, search, logger, identity)
        {
            _boundActingUser = tokenProvider,
        };

    private async Task<Metadata> ResolveHeadersAsync(Metadata? headers)
    {
        headers ??= new Metadata();
        if (headers.Get(ActingUserMetadata.MetadataKey) is not null)
            return headers;

        var provider = _boundActingUser ?? identity?.TokenProvider;
        if (provider is null)
            return headers;

        // Never write into the caller's own Metadata instance: build a fresh copy so a bag the
        // caller reuses across calls (e.g. on different coordinators bound to different
        // identities) isn't mutated out from under them.
        var resolved = new Metadata();
        foreach (var entry in headers)
            resolved.Add(entry.IsBinary
                ? new Metadata.Entry(entry.Key, entry.ValueBytes)
                : new Metadata.Entry(entry.Key, entry.Value));
        resolved.WithActingUser(await provider());

        return resolved;
    }

    // ── Object Mapping ─────────────────────────────────────────────────────────
    // Full CRUD with server-side relationship resolution.

    public async Task<T?> GetMappedAsync(
        string key, int depth = 1, Metadata? headers = null, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectMapping.Get {Entity}:{Key} depth={Depth}", _descriptor.EntityName, key, depth);
        var response = await mapping.GetAsync(
            new MappingGetRequest
            {
                TypeName = _descriptor.EntityName,
                Key      = key,
                Depth    = depth,
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(headers), cancellationToken: ct);

        if (!response.Success) { LogError(response.Error); return null; }
        return StructConverter.FromStruct<T>(response.Data);
    }

    public async Task<T?> PostMappedAsync(T entity, Metadata? headers = null, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectMapping.Post {Entity}", _descriptor.EntityName);
        var response = await mapping.PostAsync(
            new MappingWriteRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity, NavPropertyNames),
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(headers), cancellationToken: ct);

        if (!response.Success) { LogError(response.Error); return null; }
        return StructConverter.FromStruct<T>(response.Data);
    }

    public async Task<T?> UpdateMappedAsync(T entity, Metadata? headers = null, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectMapping.Update {Entity}", _descriptor.EntityName);
        var response = await mapping.UpdateAsync(
            new MappingWriteRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity, NavPropertyNames),
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(headers), cancellationToken: ct);

        if (!response.Success) { LogError(response.Error); return null; }
        return StructConverter.FromStruct<T>(response.Data);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectMapping.Delete {Entity}:{Key}", _descriptor.EntityName, key);
        var response = await mapping.DeleteAsync(
            new MappingDeleteRequest
            {
                TypeName = _descriptor.EntityName,
                Key      = key,
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(null), cancellationToken: ct);

        if (!response.Success) LogError(response.Error);
        return response.Success;
    }

    // ── Object Persistence ─────────────────────────────────────────────────────
    // Lightweight writes — no relation traversal on the server.

    public async Task<string?> PersistAsync(T entity, Metadata? headers = null, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectPersistence.Post {Entity}", _descriptor.EntityName);
        var response = await persistence.PostAsync(
            new PersistRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity, NavPropertyNames),
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(headers), cancellationToken: ct);

        if (!response.Success) { LogError(response.Error); return null; }
        return response.Key;
    }

    public async Task<string?> UpdateAsync(T entity, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectPersistence.Update {Entity}", _descriptor.EntityName);
        var response = await persistence.UpdateAsync(
            new PersistRequest
            {
                TypeName = _descriptor.EntityName,
                Payload  = StructConverter.ToStruct(entity, NavPropertyNames),
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(null), cancellationToken: ct);

        if (!response.Success) { LogError(response.Error); return null; }
        return response.Key;
    }

    // ── Object Retrieval ───────────────────────────────────────────────────────
    // Key-based fetch; client assembles the graph locally from relation metadata.

    public async Task<T?> GetAsync(string key, bool assembleGraph = true, CancellationToken ct = default)
    {
        logger.LogDebug("ObjectRetrieval.Get {Entity}:{Key}", _descriptor.EntityName, key);
        var response = await retrieval.GetAsync(
            new RetrievalRequest
            {
                TypeName = _descriptor.EntityName,
                Key      = key,
                TraceId  = CurrentTraceId()
            },
            await ResolveHeadersAsync(null), cancellationToken: ct);

        if (!response.Found) return null;

        var entity = StructConverter.FromStruct<T>(response.Data);
        if (entity is not null && assembleGraph)
            await assembler.AssembleAsync(entity, ct);

        return entity;
    }

    public async IAsyncEnumerable<T> GetManyAsync(
        IEnumerable<string> keys,
        bool assembleGraph = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new RetrievalManyRequest { TypeName = _descriptor.EntityName, TraceId = CurrentTraceId() };
        request.Keys.AddRange(keys);

        logger.LogDebug("ObjectRetrieval.GetMany {Entity} ({Count} keys)", _descriptor.EntityName, request.Keys.Count);

        // Buffer all results so we can batch-assemble relations in one pass
        var buffer = new List<T>();
        var stream  = retrieval.GetMany(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!response.Found) continue;
            var entity = StructConverter.FromStruct<T>(response.Data);
            if (entity is null) continue;
            buffer.Add(entity);
        }

        if (assembleGraph && buffer.Count > 0)
            await assembler.AssembleManyAsync(buffer, ct);

        foreach (var entity in buffer)
            yield return entity;
    }

    // ── Object Search ──────────────────────────────────────────────────────────
    // DSL-driven; returns streamed results with relevance scores.

    public async IAsyncEnumerable<SearchResult<T>> SearchAsync(
        QueryBuilder<T> query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Search {Entity} ({Clauses} clauses)",
            _descriptor.EntityName, request.Query?.Clauses.Count ?? 0);

        var stream = search.Search(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            var entity = StructConverter.FromStruct<T>(response.Data);
            if (entity is not null)
                yield return new SearchResult<T>(entity, response.Score);
        }
    }

    public async IAsyncEnumerable<SearchResult<T>> SearchSimilarAsync(
        QuerySimilarBuilder<T> query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.SearchSimilar {Entity} property={Property}",
            _descriptor.EntityName, request.Property);

        var stream = search.SearchSimilar(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            var entity = StructConverter.FromStruct<T>(response.Data);
            if (entity is not null) yield return new SearchResult<T>(entity, response.Score);
        }
    }

    public async IAsyncEnumerable<ChunkSearchResponse> SearchChunksAsync(
        QueryChunksBuilder<T> query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.SearchChunks {Entity} property={Property}",
            _descriptor.EntityName, request.Property);

        var stream = search.SearchChunks(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return response;
    }

    /// <summary>
    /// Executes a pipeline (CTE chain) and streams untyped rows. Column set depends on the
    /// pipeline's final step, so rows come back as string-keyed dictionaries.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> PipelineAsync(
        PipelineBuilder pipeline,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = pipeline.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Pipeline {Entity} ({Steps} steps)",
            _descriptor.EntityName, request.Steps.Count);

        var stream = search.Pipeline(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return StructConverter.ToDictionary(response.Data);
    }

    /// <summary>
    /// Executes a pipeline and projects each row onto <typeparamref name="TResult"/>
    /// (any class whose property names match the pipeline's output aliases).
    /// </summary>
    public async IAsyncEnumerable<TResult> PipelineAsync<TResult>(
        PipelineBuilder pipeline,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TResult : class
    {
        var request     = pipeline.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Pipeline {Entity} ({Steps} steps, typed)",
            _descriptor.EntityName, request.Steps.Count);

        var stream = search.Pipeline(request, await ResolveHeadersAsync(null), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            var row = StructConverter.FromStruct<TResult>(response.Data);
            if (row is not null) yield return row;
        }
    }

    /// <summary>
    /// Executes a GROUP BY aggregation and streams untyped rows (one row per output group).
    /// Column set depends on the query's keys/metrics, so rows come back as string-keyed
    /// dictionaries, same as <see cref="PipelineAsync(PipelineBuilder,CancellationToken)"/>.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> GroupByAsync(
        GroupByBuilder query,
        Metadata? headers = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.GroupBy {Entity} ({Keys} keys)",
            _descriptor.EntityName, request.Keys.Count);

        var stream = search.GroupBy(request, await ResolveHeadersAsync(headers), cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return StructConverter.ToDictionary(response.Data);
    }

    /// <summary>
    /// Executes an aggregation request and returns the full <see cref="AggregateResponse"/>
    /// (one <see cref="AggregationResult"/> per requested <see cref="AggregationSpec"/>).
    /// </summary>
    public async Task<AggregateResponse> AggregateAsync(
        AggregateBuilder query,
        Metadata? headers = null,
        CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Aggregate {Entity} ({Aggregations} aggregations)",
            _descriptor.EntityName, request.Aggregations.Count);

        return await search.AggregateAsync(request, await ResolveHeadersAsync(headers), cancellationToken: ct);
    }

    private static string CurrentTraceId() =>
        Activity.Current?.TraceId.ToString() ?? string.Empty;

    private void LogError(string error) =>
        logger.LogError("Iverson gRPC error for {Entity}: {Error}", _descriptor.EntityName, error);
}

public sealed record SearchResult<T>(T Entity, float Score);
