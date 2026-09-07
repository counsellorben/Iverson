namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Distinct parent documents among the first 10 and first 50 raw SearchChunks hits — the
/// diversity a caller consuming the chunk list actually sees. Taken BEFORE max-passage
/// aggregation, which is the only place the number exists (spec §3.3).
/// </summary>
public static class ChunkDiversity
{
    public static (int At10, int At50) Count(IReadOnlyList<string> parentKeysInRankOrder)
    {
        var at10 = parentKeysInRankOrder.Take(10).Distinct(StringComparer.Ordinal).Count();
        var at50 = parentKeysInRankOrder.Take(50).Distinct(StringComparer.Ordinal).Count();
        return (at10, at50);
    }
}
