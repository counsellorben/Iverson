using System.Runtime.CompilerServices;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Patterns;
using Iverson.StarRocks;
using Iverson.Vector;
using Filter = Qdrant.Client.Grpc.Filter;
using EngineRowsPerMatch = Iverson.Patterns.RowsPerMatch;
using EngineSkipKind = Iverson.Patterns.AfterMatchSkipKind;
using ProtoRowsPerMatch = Iverson.Client.Contracts.RowsPerMatch;
using ProtoSkipKind = Iverson.Client.Contracts.AfterMatchSkipKind;

namespace Iverson.Api.Grpc;

public sealed partial class ObjectSearchGrpcService
{
    private readonly PatternQueryLimitOptions _patternLimits = patternLimits ?? PatternQueryLimitOptions.Default;
    private readonly IChunkRowSource _chunkRows = chunkRows ?? new QdrantChunkRowSource(vector, tenantScope);

    // ── MatchPattern (spec §3.4) ───────────────────────────────────────────────

    public override async Task MatchPattern(
        MatchPatternRequest request,
        IServerStreamWriter<MatchPatternResponse> responseStream,
        ServerCallContext context)
    {
        var limits = _patternLimits;
        var schema = RequireSchema(request.TypeName);                                   // step 1
        var chunks = request.Source == PatternRowSource.Chunks;

        var outputLimit = ValidateMatchPatternLimits(request, chunks, limits);          // step 2: §5, before Compile
        var compiled = CompileMatchPattern(request, limits);                            // step 2: Compile

        ChunkDescriptor? chunkDesc = null;                                               // step 3
        if (chunks)
        {
            chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
                    string.Equals(c.PropertyName, request.ChunkProperty, StringComparison.OrdinalIgnoreCase))
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Property '{request.ChunkProperty}' on '{request.TypeName}' has no [IversonChunk] annotation."));
            if (schema.CollectionName is null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Type '{request.TypeName}' has no Qdrant collection."));
        }

        // step 4: authorization; a denied caller gets an empty stream and no store is queried.
        IReadOnlyDictionary<string, AuthorizationConstraint> constraints;
        AuthorizationDecision? chunkDecision = null;
        string? tenantValue;
        IReadOnlySet<string>? allowedFields;
        if (chunks)
        {
            chunkDecision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
            if (chunkDecision.Denied) return;
            if (chunkDecision.AllowedFields is not null && !chunkDecision.AllowedFields.Contains(chunkDesc!.PropertyName))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Property '{request.ChunkProperty}' on '{request.TypeName}' is not authorized for this caller."));
            constraints = new Dictionary<string, AuthorizationConstraint>();
            tenantValue = chunkDecision.TenantValue;
            allowedFields = chunkDecision.AllowedFields;
        }
        else
        {
            var auth = EvaluateAuthorization(schema, []);
            if (auth.PrimaryDenied) return;
            constraints = auth.Constraints;
            tenantValue = auth.Constraints[schema.TypeName].TenantValue;
            allowedFields = auth.Constraints[schema.TypeName].AllowedFields;
        }

        var termVectorNames = ResolveSimilarityVectors(schema, compiled, chunks, chunkDesc, allowedFields);

        Filter? chunkFilter = null;
        if (chunks)
        {
            try
            {
                chunkFilter = BuildChunksFilter(schema, request.Where, chunkDecision!.AllowedFields);
            }
            catch (FilterTranslationException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            chunkFilter = IntelligenceFilterBuilder.ApplyOwnership(
                chunkFilter, chunkDecision.OwnershipRequired,
                schema.Authorization?.OwnerField?.ToCamelCase(), chunkDecision.OwnerValue);
        }

        if (logger.IsEnabled(LogLevel.Information))                                     // step 5
            logger.LogInformation(
                "[MatchPattern] type={Type} source={Source} defines={Defines} measures={Measures}",
                request.TypeName.SanitizeForLog(), request.Source, request.Define.Count, request.Measures.Count);

        // steps 6–7. One timeout token per request (spec §5: TimeoutSeconds, or the client deadline if earlier —
        // context.CancellationToken already fires at the deadline), carried by the one per-request budget.
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        var ct = timeout.Token;
        var budget = new PatternBudget(limits.MaxActiveThreads, limits.MaxSteps, ct);
        IAsyncEnumerator<PatternBatch>? batches = null;
        DetachedDisposalInput? input = null;
        try
        {
            var termVectors = await EmbedSimilarityTermsAsync(schema, compiled, ct);
            input = new DetachedDisposalInput(chunks
                ? ChunkInputAsync(new ChunkRowQuery(
                    tenantScope.ResolveCollectionName(schema.CollectionName!, tenantValue, isChunks: true),
                    chunkFilter, chunkDesc!.PropertyName,
                    compiled.SimilarityTerms.Count > 0 ? chunkDesc.PropertyName.ToSnakeCase() + "_vector" : null,
                    limits.BatchRows, limits.MaxPartitionRows, limits.MaxRowsScanned), ct)
                : TypeRowsInputAsync(schema, request, compiled, constraints, limits, ct));

            batches = PatternPartitionBatcher.BatchAsync(
                input, chunks ? ["parent_key"] : request.PartitionBy.ToList(),
                limits.BatchRows, limits.MaxPartitionRows, chunks ? null : limits.MaxRowsScanned, ct)
                .GetAsyncEnumerator(ct);

            var written = 0;
            while (await batches.MoveNextAsync())
            {
                var batch = batches.Current;
                var scores = await ScoreBatchAsync(schema, batch, termVectorNames, termVectors, chunks, tenantValue, ct);
                var offset = 0;
                foreach (var partition in batch.Partitions)
                {
                    var baseRow = offset;
                    Func<int, int, double?> similarity = scores is null ? (_, _) => null : (row, term) => scores[baseRow + row][term];
                    foreach (var output in compiled.Run(partition.Select(r => r.Columns).ToList(), similarity, budget))
                    {
                        ct.ThrowIfCancellationRequested();                              // spec §3.4 step 6
                        var data = new Dictionary<string, object?>(output.Data, StringComparer.Ordinal);
                        AuthorizationFieldMasking.RemoveTenantColumn(data);
                        await responseStream.WriteAsync(
                            new MatchPatternResponse
                            {
                                Data = DictToProtoStruct(data), MatchNumber = output.MatchNumber,
                                Classifier = output.Classifier, TraceId = request.TraceId,
                            },
                            context.CancellationToken);
                        if (++written == outputLimit)
                            return;                                                     // step 7
                    }
                    offset += partition.Count;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)                            // §6: checked before every other row
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "MatchPattern exceeded its time limit."));
        }
        catch (PatternEvaluationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (PatternBudgetExceededException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (FilterTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        finally
        {
            // Option B (§9.3 pause test), on every exit with a live enumerator — limit, completion and every error
            // status alike: don't await the drain, so a frozen StarRocks never delays the response or its status.
            // An error the batcher raises while enumerating its input has already disposed that input by now; the
            // adapter detached that disposal, and ReleaseDetachedAsync awaits it off the request path.
            if (batches is not null)
                _ = ReleaseDetachedAsync(batches, input!, timeout);
            else
                timeout.Dispose();
        }
    }

    /// <summary>
    /// The RPC completes — after <c>limit</c>, or with an error status mid-stream — without awaiting the row source's
    /// disposal: against a StarRocks that has stopped responding, disposing the reader drains until it answers again,
    /// and nothing bounds that (spec Known issues). The drain holds one pooled connection until then; the timeout
    /// source lives until it ends and is disposed here, exactly once. A disposal failure is logged, never thrown, so
    /// it cannot replace the status the caller already has.
    /// </summary>
    private async Task ReleaseDetachedAsync(
        IAsyncEnumerator<PatternBatch> batches, DetachedDisposalInput input, CancellationTokenSource timeout)
    {
        try
        {
            try
            {
                await batches.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[MatchPattern] disposing the batch enumerator failed.");
            }

            // Read only now: on the limit path it is batches.DisposeAsync() above that disposes the input.
            try
            {
                await input.Disposal;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[MatchPattern] the row source's detached disposal failed.");
            }
        }
        finally
        {
            timeout.Dispose();                                                         // after the drain: it may use the token
        }
    }

    /// <summary>
    /// The batcher's input, whose disposal never runs on the request path unless enumeration completed normally. C#'s
    /// <c>await foreach</c> disposes its source before an exception it raises propagates — so without this, an error
    /// <c>PatternPartitionBatcher.BatchAsync</c> raises mid-source (<c>MaxPartitionRows</c>, <c>MaxRowsScanned</c>)
    /// would await the StarRocks reader's drain inside <c>batches.MoveNextAsync()</c>, before the RPC could answer.
    /// Here that disposal is started, not awaited, and exposed as <see cref="Disposal"/> for
    /// <see cref="ReleaseDetachedAsync"/> to await. Enumerated once; the batcher stays unaware of the policy.
    /// </summary>
    private sealed class DetachedDisposalInput(IAsyncEnumerable<PatternInputRow> inner) : IAsyncEnumerable<PatternInputRow>
    {
        /// <summary>The inner enumerator's disposal: completed unless a detached one is still running.</summary>
        public Task Disposal { get; private set; } = Task.CompletedTask;

        public IAsyncEnumerator<PatternInputRow> GetAsyncEnumerator(CancellationToken ct = default) =>
            new Enumerator(this, inner.GetAsyncEnumerator(ct));

        private sealed class Enumerator(DetachedDisposalInput owner, IAsyncEnumerator<PatternInputRow> inner)
            : IAsyncEnumerator<PatternInputRow>
        {
            private bool _completed;

            public PatternInputRow Current => inner.Current;

            public async ValueTask<bool> MoveNextAsync()
            {
                var more = await inner.MoveNextAsync();
                _completed = !more;
                return more;
            }

            public ValueTask DisposeAsync()
            {
                if (_completed) return inner.DisposeAsync();                        // exhausted: nothing left to drain
                owner.Disposal = DisposeInnerAsync(inner);
                return ValueTask.CompletedTask;
            }

            // An async method, so even a synchronous throw from DisposeAsync lands in the task, never on the caller.
            private static async Task DisposeInnerAsync(IAsyncEnumerator<PatternInputRow> e) => await e.DisposeAsync();
        }
    }

    /// <summary>
    /// Spec §5's request-shape limits and §1's source-shape rules, checked before <c>Compile</c>: the engine's parsers
    /// recurse once per nesting level, so the length limits are what bound their stack depth. Returns the effective
    /// output-row limit.
    /// </summary>
    private int ValidateMatchPatternLimits(MatchPatternRequest request, bool chunks, PatternQueryLimitOptions limits)
    {
        if (request.Pattern.Length > limits.MaxPatternLength)
            throw InvalidMatchPattern($"pattern length {request.Pattern.Length} exceeds MaxPatternLength ({limits.MaxPatternLength}).");
        if (request.Define.Count > limits.MaxDefines)
            throw InvalidMatchPattern($"{request.Define.Count} define entries exceed MaxDefines ({limits.MaxDefines}).");
        if (request.Measures.Count > limits.MaxMeasures)
            throw InvalidMatchPattern($"{request.Measures.Count} measures entries exceed MaxMeasures ({limits.MaxMeasures}).");
        if (request.Subsets.Count > limits.MaxSubsets)
            throw InvalidMatchPattern($"{request.Subsets.Count} subsets exceed MaxSubsets ({limits.MaxSubsets}).");

        foreach (var entry in request.Define.Concat(request.Measures))
            if (entry.Expr.Length > limits.MaxExpressionLength)
                throw InvalidMatchPattern(
                    $"the expression for '{entry.Name.SanitizeForLog()}' ({entry.Expr.Length} characters) exceeds MaxExpressionLength ({limits.MaxExpressionLength}).");

        if (request.Where.Count > queryLimits.MaxClauses)
            throw InvalidMatchPattern($"{request.Where.Count} where clauses exceed MaxClauses ({queryLimits.MaxClauses}).");
        if (request.Limit < 0 || request.Limit > limits.MaxOutputRows)
            throw InvalidMatchPattern($"limit {request.Limit} is outside 0..MaxOutputRows ({limits.MaxOutputRows}).");

        if (chunks)
        {
            if (request.PartitionBy.Count > 0 || request.OrderBy.Count > 0)
                throw InvalidMatchPattern("the CHUNKS source takes no partition_by or order_by: it is partitioned by parent_key and ordered by chunk_index.");
            if (request.Where.Count > 1 && request.WhereLogic == SearchLogic.Or)
                throw InvalidMatchPattern("the CHUNKS source does not support an OR where clause.");
        }
        else if (request.OrderBy.Count == 0)
            throw InvalidMatchPattern("the TYPE_ROWS source requires at least one order_by entry.");

        return request.Limit == 0 ? PatternQueryLimitOptions.DefaultOutputRows : request.Limit;
    }

    private static RpcException InvalidMatchPattern(string message) =>
        new(new Status(StatusCode.InvalidArgument, $"MatchPattern: {message}"));

    private static CompiledPattern CompileMatchPattern(MatchPatternRequest request, PatternQueryLimitOptions limits)
    {
        var patternRequest = new PatternRequest(
            (PatternSource)(int)request.Source, request.PartitionBy.ToList(), request.Pattern,
            request.Subsets.Select(s => new SubsetDefinition(s.Name, s.Variables.ToList())).ToList(),
            request.Define.Select(d => new NamedExpression(d.Name, d.Expr)).ToList(),
            request.Measures.Select(m => new NamedExpression(m.Name, m.Expr)).ToList(),
            (EngineRowsPerMatch)(int)request.RowsPerMatch,
            (EngineSkipKind)(int)(request.AfterMatch?.Kind ?? ProtoSkipKind.PastLastRow),
            request.AfterMatch?.Variable ?? "",
            SchemaDescriptor.TenantColumnName);

        CompiledPattern compiled;
        try
        {
            compiled = PatternQuery.Compile(patternRequest, limits.MaxProgramInstructions);
        }
        catch (PatternValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        if (compiled.SimilarityTerms.Count > limits.MaxSimilarityTerms)
            throw InvalidMatchPattern(
                $"{compiled.SimilarityTerms.Count} distinct SIMILARITY terms exceed MaxSimilarityTerms ({limits.MaxSimilarityTerms}).");

        return compiled;
    }

    /// <summary>The named vector each SIMILARITY term reads (spec §2, §3.3), indexed like <c>SimilarityTerms</c>.</summary>
    private static string[] ResolveSimilarityVectors(
        SchemaDescriptor schema, CompiledPattern compiled, bool chunks, ChunkDescriptor? chunkDesc,
        IReadOnlySet<string>? allowedFields)
    {
        var terms = compiled.SimilarityTerms;
        var names = new string[terms.Count];
        for (var i = 0; i < terms.Count; i++)
        {
            if (chunks)
            {
                // Compile already requires a CHUNKS term's column to be `text`: the chunk's own vector.
                names[i] = chunkDesc!.PropertyName.ToSnakeCase() + "_vector";
                continue;
            }

            var descriptor = schema.VectorFields.FirstOrDefault(v =>
                    string.Equals(v.PropertyName, terms[i].Column, StringComparison.OrdinalIgnoreCase))
                ?? throw InvalidMatchPattern(
                    $"SIMILARITY column '{terms[i].Column.SanitizeForLog()}' on '{schema.TypeName}' has no [IversonEmbedding] annotation.");
            if (allowedFields is not null && !allowedFields.Contains(descriptor.PropertyName))
                throw InvalidMatchPattern(
                    $"SIMILARITY column '{terms[i].Column.SanitizeForLog()}' on '{schema.TypeName}' is not authorized for this caller.");
            names[i] = descriptor.PropertyName.ToSnakeCase() + "_vector";
        }

        if (terms.Count > 0 && schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{schema.TypeName}' has no Qdrant collection."));

        return names;
    }

    /// <summary>The query vector of each SIMILARITY term; embedding failures map exactly as <c>SearchChunks</c>'s do.</summary>
    private async Task<float[][]> EmbedSimilarityTermsAsync(SchemaDescriptor schema, CompiledPattern compiled, CancellationToken ct)
    {
        if (compiled.SimilarityTerms.Count == 0) return [];

        try
        {
            return await SimilarityResolver.EmbedAsync(resolver.Get(SchemaDescriptor.ModelOf(schema)), compiled.SimilarityTerms, ct);
        }
        catch (EmptyEmbeddingInputException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Embedding service unavailable");
            throw new RpcException(new Status(StatusCode.Unavailable, "Embedding service unavailable."));
        }
    }

    /// <summary>The TYPE_ROWS source (spec §3.2): the tenant-scoped StarRocks rows, minus the type's bytes columns.</summary>
    private async IAsyncEnumerable<PatternInputRow> TypeRowsInputAsync(
        SchemaDescriptor schema, MatchPatternRequest request, CompiledPattern compiled,
        IReadOnlyDictionary<string, AuthorizationConstraint> constraints, PatternQueryLimitOptions limits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rowsRequest = new MatchRowsRequest(request.Where.ToList(), request.WhereLogic, request.PartitionBy.ToList(),
            request.OrderBy.ToList(), compiled.ReferencedColumns.ToList(), compiled.MeasureNames,
            request.RowsPerMatch == ProtoRowsPerMatch.OneRow,
            schema.ScalarColumns.Where(c => string.Equals(c.SqlType, "BYTEA", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name).ToList(),
            limits.MaxRowsScanned);

        await foreach (var row in search.MatchRowsAsync(SchemaBuilder.ToEngagementQuerySchema(schema), rowsRequest, constraints, ct))
            yield return new PatternInputRow(row, null);
    }

    /// <summary>The CHUNKS source (spec §1, §3.2): <c>parent_key</c>, <c>chunk_index</c>, <c>text</c>, in that order.</summary>
    private async IAsyncEnumerable<PatternInputRow> ChunkInputAsync(ChunkRowQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var c in _chunkRows.ReadAsync(query, ct))
            yield return new PatternInputRow(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    { ["parent_key"] = c.ParentKey, ["chunk_index"] = c.ChunkIndex, ["text"] = c.Text },
                c.Vector);
    }

    /// <summary>
    /// <c>scores[row][term]</c> for the batch's rows in batch order (partition <c>p</c>'s row <c>r</c> is at
    /// <c>offset(p) + r</c>), or null when the request has no SIMILARITY term.
    /// </summary>
    private async Task<double?[][]?> ScoreBatchAsync(
        SchemaDescriptor schema, PatternBatch batch, string[] termVectorNames, float[][] termVectors, bool chunks,
        string? tenantValue, CancellationToken ct)
    {
        if (termVectorNames.Length == 0) return null;

        var rows = batch.Partitions.SelectMany(p => p).ToList();
        if (chunks)
            return SimilarityResolver.ScoreVectors(rows.Select(r => r.ChunkVector).ToList(), termVectors);

        return await new SimilarityResolver(vector, tenantScope).ScoreRowsAsync(
            tenantScope.ResolveCollectionName(schema.CollectionName!, tenantValue, isChunks: false),
            rows.Select(r => IntelligenceStoreConsumer.KeyToUlong((string)r.Columns[schema.KeyColumn.Name]!)).ToList(),
            termVectorNames, termVectors, ct);
    }
}
