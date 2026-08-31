using System.Numerics.Tensors;

namespace Iverson.Vector;

/// <summary>
/// Fuses base similarity, centroid similarity and decay signals into a single score.
/// Pure and I/O-free: performs no network calls and reads no clock. The decay signal
/// is consumed as a pre-computed value in [0,1]; the decay curve itself is owned elsewhere.
/// </summary>
public sealed class ResultReranker : IResultReranker
{
    // Triple B. Chosen 2026-08-31 over triple A (0.50/0.50/0.10) as a product decision about
    // decay's intended share, not an empirical one: the two triples reorder ~47% of top-10
    // document sets once ages vary, so they are not interchangeable, and no available corpus
    // judges recency well enough to choose between them. B keeps decay's share at 10.00% on the
    // centroid-present branch, matching what 0.60/0.30/0.10 gave. The share is branch-dependent
    // -- 18.18% when the centroid is absent -- and no triple at this centroid ratio preserves
    // both. See docs/centroid-weighting-proposal.md.
    private const double WBase = 0.45, WCentroid = 0.45, WDecay = 0.10;

    public IReadOnlyList<RerankedResult> Rerank(float[] queryVector, IReadOnlyList<RerankCandidate> candidates)
    {
        var results = new List<RerankedResult>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var hasCentroid = candidate.Centroid is not null && candidate.Centroid.Length == queryVector.Length;
            var hasDecay = candidate.Decay is not null;

            double fusedScore;
            if (!hasCentroid && !hasDecay)
            {
                // No other signal present: the weighted mean over signals present is
                // (WBase * BaseScore) / WBase, which must equal BaseScore exactly rather
                // than merely approximately (a multiply-then-divide round trip is not
                // guaranteed bit-exact in floating point). Short-circuit to preserve
                // today's ordering bit-for-bit.
                fusedScore = candidate.BaseScore;
            }
            else
            {
                var weightedSum = WBase * candidate.BaseScore;
                var weightTotal = WBase;

                if (hasCentroid)
                {
                    var centroidSimilarity = TensorPrimitives.CosineSimilarity(queryVector, candidate.Centroid!);
                    weightedSum += WCentroid * centroidSimilarity;
                    weightTotal += WCentroid;
                }

                if (hasDecay)
                {
                    weightedSum += WDecay * candidate.Decay!.Value;
                    weightTotal += WDecay;
                }

                fusedScore = weightedSum / weightTotal;
            }

            results.Add(new RerankedResult(candidate.Id, fusedScore));
        }

        return results
            .OrderByDescending(r => r.FusedScore)
            .ToList();
    }
}
