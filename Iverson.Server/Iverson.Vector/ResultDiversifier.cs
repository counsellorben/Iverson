using System.Numerics.Tensors;

namespace Iverson.Vector;

/// <summary>
/// Greedy maximal marginal relevance over an already-fused, fused-descending candidate list.
/// Pure and I/O-free. Selection replaces a plain Take(topK): the first candidate is always the
/// highest-fused one, and each subsequent pick maximises lambda*fused - (1-lambda)*maxSim.
/// </summary>
public sealed class ResultDiversifier : IResultDiversifier
{
    private const double Lambda = 0.70;

    public IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK)
    {
        if (ranked.Count == 0 || topK <= 0) return [];

        var take      = Math.Min(topK, ranked.Count);
        var selected  = new List<RerankedResult>(take);
        var taken     = new bool[ranked.Count];

        // Running maximum similarity of each remaining candidate against the SELECTED set,
        // updated against only the newly-selected candidate each round. NaN is never stored:
        // an unusable similarity is an ABSENT one, leaving the running maximum untouched.
        // Presence is tracked SEPARATELY from magnitude: cosine similarity ranges over
        // [-1, 1], so the value 0.0 cannot stand in for "no similarity term" — a candidate
        // anti-similar to everything selected is the MOST diverse one there is, and must not
        // score as though its vector were absent.
        var maxSim = new double[ranked.Count];
        var hasSim = new bool[ranked.Count];

        // Step 1 of the mechanism: the highest-fused candidate is selected unconditionally.
        // `ranked` is fused-descending, so that is index 0.
        Select(0);

        while (selected.Count < take)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            for (var i = 0; i < ranked.Count; i++)
            {
                if (taken[i]) continue;

                // Seed on the first untaken candidate so the scan is TOTAL. A NaN fused score
                // is reachable (a NaN centroid — see Known issues — fuses to NaN) and loses
                // every `>` comparison; without the seed, a tail of them would leave bestIndex
                // at -1 and return FEWER results than Take(topK) does.
                if (bestIndex < 0)
                {
                    bestIndex = i;
                    bestScore = Mmr(i);
                    continue;
                }

                var mmr = Mmr(i);

                // Strict `>` keeps the EARLIER candidate on a tie, and `ranked` is
                // fused-descending — the exactness of the all-absent guarantee rests on this.
                if (mmr > bestScore)
                {
                    bestScore = mmr;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) break;   // nothing untaken remains
            Select(bestIndex);
        }

        return selected;

        // An absent similarity term contributes NO penalty — never a substituted 0.0.
        double Mmr(int i) =>
            hasSim[i]
                ? Lambda * ranked[i].Score - (1 - Lambda) * maxSim[i]
                : Lambda * ranked[i].Score;

        void Select(int index)
        {
            taken[index] = true;
            selected.Add(new RerankedResult(ranked[index].Id, ranked[index].Score));

            var justSelected = ranked[index].DiversityVector;
            if (justSelected is null) return;

            for (var i = 0; i < ranked.Count; i++)
            {
                if (taken[i]) continue;

                var other = ranked[i].DiversityVector;

                // Length equality is checked BEFORE the call: CosineSimilarity THROWS
                // ArgumentException on differing lengths (and on two empty spans), it does
                // not return NaN. A mismatched pair has no similarity term.
                if (other is null || other.Length != justSelected.Length || other.Length == 0) continue;

                double similarity = TensorPrimitives.CosineSimilarity(justSelected, other);

                // A zero-magnitude vector yields NaN. Treat it as absent rather than letting it
                // reach the argmax, where every `>` comparison against it is false.
                if (double.IsNaN(similarity)) continue;

                if (!hasSim[i] || similarity > maxSim[i])
                {
                    maxSim[i] = similarity;
                    hasSim[i] = true;
                }
            }
        }
    }
}
