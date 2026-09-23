namespace Iverson.Vector;

/// <summary>
/// A candidate for diversified selection: its id, its fused score, and the vector whose
/// mutual cosine similarity defines redundancy. The vector is SUPPLIED, not derived — each
/// RPC decides what "diversity" means at the granularity of what it returns.
/// </summary>
public sealed record DiversifyCandidate(ulong Id, double Score, float[]? DiversityVector);

public interface IResultDiversifier
{
    /// <param name="ranked">Candidates in fused-descending order, as <c>IResultReranker.Rerank</c> returns them.</param>
    /// <param name="lambda">MMR trade-off in [0,1]; 1.00 reduces to <c>Take(topK)</c> exactly.</param>
    IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK, double lambda);
}
