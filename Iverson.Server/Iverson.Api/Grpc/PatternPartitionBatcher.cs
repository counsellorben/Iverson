using System.Runtime.CompilerServices;
using Iverson.Patterns;

namespace Iverson.Api.Grpc;

/// <summary>One engine input row and, for CHUNKS, the chunk's own vector (null otherwise).</summary>
internal sealed record PatternInputRow(IDictionary<string, object?> Columns, float[]? ChunkVector);

/// <summary>Whole partitions, each in partition order (spec §4).</summary>
internal sealed record PatternBatch(IReadOnlyList<IReadOnlyList<PatternInputRow>> Partitions)
{
    public int RowCount => Partitions.Sum(p => p.Count);
}

internal static class PatternPartitionBatcher
{
    /// <summary>
    /// Groups <paramref name="rows"/> (already ordered by partition) into whole partitions and packs them into batches
    /// of about <paramref name="batchRows"/> rows; a partition larger than that forms a batch on its own (spec §4).
    /// Throws <see cref="PatternBudgetExceededException"/> naming <c>MaxPartitionRows</c> when one partition exceeds
    /// <paramref name="maxPartitionRows"/>, and <c>MaxRowsScanned</c> when more than <paramref name="maxRowsScanned"/>
    /// rows arrive (null: the source enforces it, as the CHUNKS source does).
    /// </summary>
    public static async IAsyncEnumerable<PatternBatch> BatchAsync(
        IAsyncEnumerable<PatternInputRow> rows, IReadOnlyList<string> partitionBy,
        int batchRows, int maxPartitionRows, int? maxRowsScanned, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var scanned = 0;
        var pending = new List<IReadOnlyList<PatternInputRow>>();
        var pendingRows = 0;
        List<PatternInputRow>? currentPartition = null;
        object?[]? currentKey = null;

        await foreach (var row in rows.WithCancellation(ct))
        {
            scanned++;
            if (maxRowsScanned is { } max && scanned > max)
                throw new PatternBudgetExceededException("MaxRowsScanned");

            var key = partitionBy.Select(c => row.Columns[c]).ToArray();

            if (currentPartition is null || !SameKey(currentKey!, key))
            {
                if (currentPartition is not null)
                {
                    foreach (var ready in AddPartition(pending, currentPartition, ref pendingRows, batchRows))
                        yield return ready;
                }

                currentPartition = [];
                currentKey = key;
            }

            currentPartition.Add(row);
            if (currentPartition.Count > maxPartitionRows)
                throw new PatternBudgetExceededException("MaxPartitionRows");
        }

        if (currentPartition is not null)
        {
            foreach (var ready in AddPartition(pending, currentPartition, ref pendingRows, batchRows))
                yield return ready;
        }

        if (pending.Count > 0)
            yield return new PatternBatch(pending);
    }

    private static bool SameKey(object?[] a, object?[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Adds a completed partition to the pending batch, per spec: yield the pending batch first if adding the
    /// partition would overflow it, then add, then yield if the batch is now at or above <paramref name="batchRows"/>.
    /// Materializes eagerly (never more than 2 entries: the pre-existing overflowed batch, and/or the freshly
    /// filled one) so the caller — an iterator — can yield each with a plain <c>foreach</c> instead of taking
    /// <paramref name="pendingRows"/> by <c>ref</c> across a <c>yield return</c>, which C# disallows.
    /// </summary>
    private static List<PatternBatch> AddPartition(
        List<IReadOnlyList<PatternInputRow>> pending, List<PatternInputRow> partition, ref int pendingRows, int batchRows)
    {
        var ready = new List<PatternBatch>();

        if (pendingRows > 0 && pendingRows + partition.Count > batchRows)
        {
            ready.Add(new PatternBatch([.. pending]));
            pending.Clear();
            pendingRows = 0;
        }

        pending.Add(partition);
        pendingRows += partition.Count;

        if (pendingRows >= batchRows)
        {
            ready.Add(new PatternBatch([.. pending]));
            pending.Clear();
            pendingRows = 0;
        }

        return ready;
    }
}
