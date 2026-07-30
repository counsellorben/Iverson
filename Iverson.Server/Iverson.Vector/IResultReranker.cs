namespace Iverson.Vector;

public sealed record RerankCandidate(
    ulong    Id,
    double   BaseScore,
    float[]? Centroid,
    double?  Decay);

public sealed record RerankedResult(ulong Id, double FusedScore);

public interface IResultReranker
{
    IReadOnlyList<RerankedResult> Rerank(float[] queryVector, IReadOnlyList<RerankCandidate> candidates);
}
