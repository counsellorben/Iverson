using System.Runtime.CompilerServices;
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Patterns;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class PatternPartitionBatcherTests
{
    private static PatternInputRow Row(object? partition, int id) =>
        new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["p"] = partition, ["id"] = id }, null);

    /// <summary>Rows for consecutive partitions of the given sizes; partition i has key "k{i}".</summary>
    private static List<PatternInputRow> Partitions(params int[] sizes)
    {
        var rows = new List<PatternInputRow>();
        var id = 0;
        for (var i = 0; i < sizes.Length; i++)
            for (var j = 0; j < sizes[i]; j++) rows.Add(Row($"k{i}", id++));
        return rows;
    }

    private static async IAsyncEnumerable<PatternInputRow> Stream(IEnumerable<PatternInputRow> rows, Action? onEach = null)
    {
        foreach (var row in rows)
        {
            onEach?.Invoke();
            await Task.Yield();
            yield return row;
        }
    }

    /// <summary>An endless single-partition source that actually observes its enumerator's cancellation token —
    /// via <see cref="EnumeratorCancellationAttribute"/>, the same mechanism <c>BatchAsync</c>'s
    /// <c>rows.WithCancellation(ct)</c> call is supposed to feed. If that call is ever dropped, this source never
    /// sees cancellation and loops forever instead of throwing.</summary>
    private static async IAsyncEnumerable<PatternInputRow> InfiniteRows([EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = 0;
        while (true)
        {
            await Task.Delay(10, ct);
            yield return Row("k0", id++);
        }
    }

    private static async Task<List<PatternBatch>> BatchAllAsync(IEnumerable<PatternInputRow> rows,
        int batchRows = 2_000, int maxPartitionRows = 10_000, int? maxRowsScanned = null, string[]? partitionBy = null)
    {
        var batches = new List<PatternBatch>();
        await foreach (var b in PatternPartitionBatcher.BatchAsync(Stream(rows), partitionBy ?? ["p"], batchRows, maxPartitionRows, maxRowsScanned))
            batches.Add(b);
        return batches;
    }

    private static int[][] Shape(IEnumerable<PatternBatch> batches) =>
        batches.Select(b => b.Partitions.Select(p => p.Count).ToArray()).ToArray();

    [Fact]
    public async Task Partitions_are_never_split_and_batches_pack_to_BatchRows()
    {
        Shape(await BatchAllAsync(Partitions(2, 2, 3, 1), batchRows: 4))
            .Should().BeEquivalentTo(new[] { new[] { 2, 2 }, new[] { 3, 1 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_partition_larger_than_BatchRows_forms_a_batch_on_its_own()
    {
        Shape(await BatchAllAsync(Partitions(1, 5, 1), batchRows: 3))
            .Should().BeEquivalentTo(new[] { new[] { 1 }, new[] { 5 }, new[] { 1 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task No_partition_columns_makes_the_whole_input_one_partition()
    {
        var batches = await BatchAllAsync(Partitions(2, 3), batchRows: 2, partitionBy: []);

        Shape(batches).Should().BeEquivalentTo(new[] { new[] { 5 } });
    }

    [Fact]
    public async Task Null_partition_values_group_together_and_values_compare_by_value()
    {
        var rows = new[] { Row(null, 0), Row(null, 1), Row(1L, 2), Row(1L, 3), Row("1", 4) };

        Shape(await BatchAllAsync(rows, batchRows: 100))
            .Should().BeEquivalentTo(new[] { new[] { 2, 2, 1 } });
    }

    [Fact]
    public async Task Rows_keep_their_order_inside_each_partition()
    {
        var batches = await BatchAllAsync(Partitions(3, 2), batchRows: 100);

        batches.Single().Partitions.SelectMany(p => p).Select(r => r.Columns["id"]).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public async Task One_partition_over_MaxPartitionRows_raises_the_budget_exception()
    {
        var act = () => BatchAllAsync(Partitions(1, 3), maxPartitionRows: 2);

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxPartitionRows");
    }

    [Fact]
    public async Task MaxRowsScanned_counts_every_row_and_trips_after_earlier_batches_were_yielded()
    {
        var yielded = new List<PatternBatch>();
        var act = async () =>
        {
            await foreach (var b in PatternPartitionBatcher.BatchAsync(Stream(Partitions(1, 1, 1, 1, 1)), ["p"], 1, 100, 4))
                yielded.Add(b);
        };

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxRowsScanned");
        yielded.Should().NotBeEmpty("a budget hit after output was written must still end the stream with an error");
    }

    [Fact]
    public async Task A_batch_is_yielded_before_the_source_is_exhausted()
    {
        var produced = 0;
        var source = Stream(Partitions(Enumerable.Repeat(1, 5_000).ToArray()), () => produced++);

        await foreach (var _ in PatternPartitionBatcher.BatchAsync(source, ["p"], 2_000, 10_000, null))
        {
            produced.Should().BeLessThan(5_000, "rows stream through; the batcher never buffers the whole source");
            break;
        }
    }

    [Fact]
    public async Task Cancelling_the_token_stops_enumeration()
    {
        using var cts = new CancellationTokenSource();

        Func<Task> act = async () =>
        {
            await foreach (var _ in PatternPartitionBatcher.BatchAsync(InfiniteRows(), ["p"], 2_000, 10_000, null, cts.Token))
            {
            }
        };

        var enumerationTask = act();
        await Task.Delay(20);
        cts.Cancel();

        // Bounded wait: if cancellation stopped reaching the source, the loop above runs forever instead of
        // throwing, so this must fail on a timeout rather than hang the test run.
        var finished = await Task.WhenAny(enumerationTask, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().Be(enumerationTask, "cancellation should stop the stream instead of hanging forever");

        Func<Task> awaitResult = () => enumerationTask;
        await awaitResult.Should().ThrowAsync<OperationCanceledException>();
    }
}
