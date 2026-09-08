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

    // MMR λ per endpoint, set by the Tier 1 gate (docs/plans/2026-09-GATE-tier1-defaults.md, rule 7.2).
    // SearchSimilar: 1.00. No λ beat 0.70 on α-nDCG@10 on either FreshStack arm and λ=1.00 was not
    // worse, so the rule's none-qualify clause applies; λ=1.00 also buys R@50 (+0.0506 fs-2048,
    // +0.0518 fs-512, both significant) at no measurable diversity cost.
    // SearchChunks: 0.70. SearchChunks returns the chunk list itself, so λ is caller-visible: λ=1.00
    // costs 1.44 (fs-2048) and 1.97 (fs-512) distinct parents in the top 10, past the rule's 1.0 bar.
    public double LambdaSimilar { get; set; } = 1.00;
    public double LambdaChunks  { get; set; } = 0.70;
}
