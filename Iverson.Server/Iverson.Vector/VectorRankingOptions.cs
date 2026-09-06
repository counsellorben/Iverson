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
    public double Lambda    { get; set; } = 0.70;
}
