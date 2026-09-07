namespace Iverson.Vector;

public sealed class VectorRankingOptions
{
    public const string Section = "VectorRanking";

    // Triple B. Chosen 2026-08-31 over triple A (0.50/0.50/0.10) as a product decision about
    // decay's intended share, not an empirical one: the two triples reorder ~47% of top-10
    // document sets once ages vary, so they are not interchangeable, and no available corpus
    // judges recency well enough to choose between them. B keeps decay's share at 10.00% on the
    // centroid-present branch, matching what 0.60/0.30/0.10 gave. The share is branch-dependent
    // -- 18.18% when the centroid is absent -- and no triple at this centroid ratio preserves
    // both. See docs/centroid-weighting-proposal.md.
    public double WBase     { get; set; } = 0.45;
    public double WCentroid { get; set; } = 0.45;
    public double WDecay    { get; set; } = 0.10;

    // MMR λ per endpoint. Both 0.70 until the Tier 1 gate (docs/plans/2026-09-GATE-tier1-defaults.md)
    // sets each by rule: SearchChunks returns the chunk list itself, so its λ is a caller-visible
    // choice the document-level benchmark cannot see; SearchSimilar's measured R@50 price for
    // diversification is 9–13 % (spec 2026-09-06-tier1-retrieval-defaults-design §1).
    public double LambdaSimilar { get; set; } = 0.70;
    public double LambdaChunks  { get; set; } = 0.70;
}
