using System.Numerics.Tensors;
using Grpc.Core;
using Iverson.Embeddings;
using Iverson.Patterns;
using Iverson.Vector;
using Qdrant.Client;

namespace Iverson.Api.Grpc;

/// <summary>
/// SIMILARITY scores for MatchPattern (spec §3.3). Never uses RetrieveVectorsOrDegradeAsync: a retrieve failure fails
/// the request, except the two states that mean "no vector yet" — a missing object collection (NotFound) and a
/// collection that predates the vector (InvalidArgument "Not existing vector name") — which leave the batch's
/// scores NULL for that vector.
/// </summary>
internal sealed class SimilarityResolver(IVectorQueryService vector, IntelligenceTenantScope tenantScope)
{
    /// <summary>The query vector of each term, embedding each distinct text once.</summary>
    public static async Task<float[][]> EmbedAsync(IEmbeddingService embedding, IReadOnlyList<SimilarityTerm> terms, CancellationToken ct)
    {
        var byText = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var result = new float[terms.Count][];

        for (var i = 0; i < terms.Count; i++)
        {
            var text = terms[i].Text;
            if (!byText.TryGetValue(text, out var v))
            {
                v = await embedding.EmbedQueryAsync(text, ct);
                byText[text] = v;
            }

            result[i] = v;
        }

        return result;
    }

    /// <summary>TYPE_ROWS: <c>scores[row][term]</c> for the batch's point ids; each distinct vector name is retrieved
    /// once, under a read-only scoped key; an absent vector is <c>null</c>.</summary>
    public async Task<double?[][]> ScoreRowsAsync(string objectCollection, IReadOnlyList<ulong> pointIds,
        IReadOnlyList<string> termVectorNames, IReadOnlyList<float[]> termQueryVectors, CancellationToken ct)
    {
        var distinctIds = pointIds.Distinct().ToList();
        var byName = new Dictionary<string, IReadOnlyDictionary<ulong, float[]>>(StringComparer.Ordinal);

        foreach (var name in termVectorNames.Distinct(StringComparer.Ordinal))
            byName[name] = await RetrieveOneAsync(objectCollection, distinctIds, name, ct);

        var scores = new double?[pointIds.Count][];
        for (var r = 0; r < pointIds.Count; r++)
        {
            var row = new double?[termVectorNames.Count];
            for (var t = 0; t < termVectorNames.Count; t++)
            {
                if (byName[termVectorNames[t]].TryGetValue(pointIds[r], out var v))
                    row[t] = (double)TensorPrimitives.CosineSimilarity(termQueryVectors[t], v);
                else
                    row[t] = null;
            }

            scores[r] = row;
        }

        return scores;
    }

    private async Task<IReadOnlyDictionary<ulong, float[]>> RetrieveOneAsync(
        string objectCollection, IReadOnlyList<ulong> ids, string vectorName, CancellationToken ct)
    {
        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true)))
                return await vector.RetrieveNamedVectorAsync(objectCollection, ids, vectorName, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return new Dictionary<ulong, float[]>();
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.InvalidArgument
                                      && e.Status.Detail.Contains("Not existing vector name", StringComparison.Ordinal))
        {
            return new Dictionary<ulong, float[]>();
        }
    }

    /// <summary>CHUNKS: <c>scores[row][term]</c> against each row's own chunk vector (null when absent).</summary>
    public static double?[][] ScoreVectors(IReadOnlyList<float[]?> rowVectors, IReadOnlyList<float[]> termQueryVectors)
    {
        var scores = new double?[rowVectors.Count][];
        for (var r = 0; r < rowVectors.Count; r++)
        {
            var row = new double?[termQueryVectors.Count];
            var rowVector = rowVectors[r];
            for (var t = 0; t < termQueryVectors.Count; t++)
                row[t] = rowVector is null ? null : (double)TensorPrimitives.CosineSimilarity(termQueryVectors[t], rowVector);

            scores[r] = row;
        }

        return scores;
    }
}
