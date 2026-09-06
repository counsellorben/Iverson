namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Refuses a chunk budget that cannot reach DocumentBudget distinct documents.
///
/// SearchChunks' top_k counts CHUNKS and the server does not dedup by parent, so at
/// high chunk density a nominally-50-document budget collapses to far fewer distinct
/// documents after max-passage aggregation -- and R@50 then measures the budget, not
/// retrieval, while looking like a retrieval finding.
///
/// reachable = topK / chunksPerDoc is a WORST case: it assumes a document's chunks are
/// retrieved together. The true count lies between it and topK.
///
/// A missing "documents" or "chunks" count in the caller's source data binds to 0, not an
/// exception -- and chunksPerDoc == 0 makes reachable == +Infinity, which compares >= to
/// anything as true. Finiteness is checked before the comparison so that degenerate input
/// fails closed (Ok == false) symmetrically for either missing count, rather than only one.
/// </summary>
public static class ChunkBudgetGuard
{
    public readonly record struct Result(
        bool   Ok,
        double ChunksPerDocument,
        int    ChunkTopK,
        double ReachableDocuments,
        int    MinimumMultiplier);

    public static Result Evaluate(
        int documents, int chunks, int documentBudget, int chunkBudgetMultiplier)
    {
        var chunksPerDoc = (double)chunks / documents;
        var topK         = documentBudget * chunkBudgetMultiplier;
        var reachable    = topK / chunksPerDoc;

        return new Result(
            Ok:                 double.IsFinite(reachable) && reachable >= documentBudget,
            ChunksPerDocument:  chunksPerDoc,
            ChunkTopK:          topK,
            ReachableDocuments: reachable,
            MinimumMultiplier:  (int)Math.Ceiling(chunksPerDoc));
    }
}
