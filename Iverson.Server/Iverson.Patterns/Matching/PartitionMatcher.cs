// Port of Trino 483 io.trino.operator.window.PatternRecognitionPartition (Apache-2.0); see THIRD-PARTY-NOTICES.md.
using Iverson.Patterns.Expressions;

namespace Iverson.Patterns.Matching;

/// <summary>
/// The per-partition loop of <c>MATCH_RECOGNIZE</c>: attempts a match at every row the previous match's
/// <c>AFTER MATCH SKIP</c> did not skip, and yields the output rows of each attempt.
/// <para>
/// Only Trino's <c>MATCH_RECOGNIZE</c> mode is ported: no <c>WINDOW</c> rows-per-match, no <c>SEEK</c> (every
/// attempt is <c>INITIAL</c>), no frame (the search area is the whole partition, so the pattern start is always
/// the current row), no window functions and no peer groups. Match aggregations are not reset per match because
/// the evaluator recomputes them from the matched labels. Output rows are produced lazily by an iterator instead
/// of being appended to a page.
/// </para>
/// </summary>
/// <param name="defines">The <c>define</c> expression of every label, by label index; null = always true.</param>
/// <param name="labelNames">Upper-case label names, by label index.</param>
/// <param name="oneRowColumns">The <c>ONE_ROW</c> leading columns: the <c>partition_by</c> columns (emitted under
/// the row's own spelling), or <c>parent_key</c> for Chunks (emitted as written).</param>
/// <param name="oneRowColumnsAsWritten">True when <paramref name="oneRowColumns"/> are emitted under their given
/// spelling rather than the row's.</param>
/// <param name="skipNavigation">The <c>AFTER MATCH SKIP TO FIRST/LAST</c> navigation; null for the other modes.</param>
internal sealed class PartitionMatcher(
    Matcher matcher,
    IReadOnlyList<Expr?> defines,
    IReadOnlyList<string> labelNames,
    IReadOnlyList<string> measureNames,
    IReadOnlyList<Expr> measures,
    RowsPerMatch rowsPerMatch,
    AfterMatchSkipKind skip,
    Navigation? skipNavigation,
    IReadOnlyList<string> oneRowColumns,
    bool oneRowColumnsAsWritten)
{
    private bool OneRow => rowsPerMatch == RowsPerMatch.OneRow;

    /// <summary>Trino's <c>RowsPerMatch.isEmptyMatches</c>.</summary>
    private bool ShowsEmptyMatches => rowsPerMatch != RowsPerMatch.AllRowsOmitEmpty;

    /// <summary>Trino's <c>RowsPerMatch.isUnmatchedRows</c>.</summary>
    private bool ShowsUnmatchedRows => rowsPerMatch == RowsPerMatch.AllRowsWithUnmatched;

    /// <summary>Trino's <c>processNextRow</c>, looped over the partition.</summary>
    public IEnumerable<MatchOutputRow> Run(
        IReadOnlyList<IDictionary<string, object?>> rows, Func<int, int, double?> similarity, PatternBudget budget)
    {
        var context = new EvaluationContext { Rows = rows, LabelNames = labelNames, Similarity = similarity };
        int lastSkippedPosition = -1;
        int lastMatchedPosition = -1;
        long matchNumber = 1;

        for (int current = 0; current < rows.Count; current++)
        {
            if (current <= lastSkippedPosition)
            {
                // skipped by AFTER MATCH SKIP of some previous row: no pattern match is attempted
                continue;
            }

            int patternStart = current;
            context.MatchNumber = matchNumber;
            var result = matcher.Run(new PartitionLabelEvaluator(context, defines, patternStart), budget);

            if (!result.Matched)
            {
                if (ShowsUnmatchedRows && current > lastMatchedPosition)
                {
                    yield return UnmatchedRow(rows[current]);
                }

                lastSkippedPosition = current;
            }
            else if (result.Labels.Length == 0)
            {
                if (ShowsEmptyMatches)
                {
                    yield return EmptyMatchRow(context, rows[current], current, matchNumber);
                }

                lastSkippedPosition = current;
                matchNumber++;
            }
            else
            {
                if (OneRow)
                {
                    yield return OneRowPerMatch(context, rows[current], result, patternStart, matchNumber);
                }
                else
                {
                    foreach (var row in AllRowsPerMatch(context, rows, result, current, matchNumber))
                    {
                        yield return row;
                    }
                }

                // updateLastMatchedPosition
                lastMatchedPosition = Math.Max(lastMatchedPosition, patternStart + result.Labels.Length - 1);
                lastSkippedPosition = SkipAfterMatch(result, patternStart, current, rows.Count);
                matchNumber++;
            }
        }
    }

    /// <summary>Trino's <c>outputUnmatchedRow</c>: every measure is null.</summary>
    private MatchOutputRow UnmatchedRow(IDictionary<string, object?> row)
    {
        var data = OutputColumns(row);
        foreach (var name in measureNames)
        {
            data.Add(name, null);
        }

        return new MatchOutputRow(data, 0, "");
    }

    /// <summary>Trino's <c>outputEmptyMatch</c>: measures under empty-match semantics.</summary>
    private MatchOutputRow EmptyMatchRow(
        EvaluationContext context, IDictionary<string, object?> row, int current, long matchNumber)
    {
        context.PatternStart = current;
        context.CurrentRow = current;
        context.MatchedLabels = ArrayView.Empty;
        context.MatchNumber = matchNumber;
        context.EmptyMatch = true;

        var data = OutputColumns(row);
        AddMeasures(data, context);
        return new MatchOutputRow(data, matchNumber, "");
    }

    /// <summary>Trino's <c>outputOneRowPerMatch</c>: measures computed at the last row of the match.</summary>
    private MatchOutputRow OneRowPerMatch(
        EvaluationContext context, IDictionary<string, object?> row, MatchResult result, int patternStart,
        long matchNumber)
    {
        SetMatch(context, result.Labels, patternStart, patternStart + result.Labels.Length - 1, matchNumber);

        var data = OutputColumns(row);
        AddMeasures(data, context);
        return new MatchOutputRow(data, matchNumber, "");
    }

    /// <summary>Trino's <c>outputAllRowsPerMatch</c>: one row per matched row outside the exclusions.</summary>
    private IEnumerable<MatchOutputRow> AllRowsPerMatch(
        EvaluationContext context, IReadOnlyList<IDictionary<string, object?>> rows, MatchResult result, int current,
        long matchNumber)
    {
        var labels = result.Labels;
        var exclusions = result.Exclusions;

        int start = 0;
        for (int index = 0; index < exclusions.Length; index += 2)
        {
            int end = exclusions[index];

            for (int i = start; i < end; i++)
            {
                yield return OutputRow(context, rows, labels, i, current, matchNumber);
            }

            start = exclusions[index + 1];
        }

        for (int i = start; i < labels.Length; i++)
        {
            yield return OutputRow(context, rows, labels, i, current, matchNumber);
        }
    }

    /// <summary>Trino's <c>outputRow</c>: measures computed from the row's own position (RUNNING semantics).</summary>
    private MatchOutputRow OutputRow(
        EvaluationContext context, IReadOnlyList<IDictionary<string, object?>> rows, ArrayView labels, int offset,
        int current, long matchNumber)
    {
        int position = current + offset;
        SetMatch(context, labels, current, position, matchNumber);

        var data = OutputColumns(rows[position]);
        AddMeasures(data, context);
        return new MatchOutputRow(data, matchNumber, labelNames[labels[offset]]);
    }

    /// <summary>Trino's <c>skipAfterMatch</c>; returns the new last skipped position.</summary>
    private int SkipAfterMatch(MatchResult result, int patternStart, int current, int rowCount)
    {
        var labels = result.Labels;
        switch (skip)
        {
            case AfterMatchSkipKind.PastLastRow:
                return patternStart + labels.Length - 1;
            case AfterMatchSkipKind.ToNextRow:
                return current;
            case AfterMatchSkipKind.ToFirst:
            case AfterMatchSkipKind.ToLast:
                int position = skipNavigation!.ResolvePosition(
                    patternStart + labels.Length - 1, labels, 0, rowCount, patternStart);
                if (position == -1)
                {
                    throw new PatternEvaluationException(
                        "AFTER MATCH SKIP failed: pattern variable is not present in match");
                }

                if (position == patternStart)
                {
                    throw new PatternEvaluationException("AFTER MATCH SKIP failed: cannot skip to first row of match");
                }

                return position - 1;
            default:
                throw new InvalidOperationException($"Unexpected AFTER MATCH SKIP kind: {skip}.");
        }
    }

    private static void SetMatch(
        EvaluationContext context, ArrayView labels, int patternStart, int currentRow, long matchNumber)
    {
        context.PatternStart = patternStart;
        context.CurrentRow = currentRow;
        context.MatchedLabels = labels;
        context.MatchNumber = matchNumber;
        context.EmptyMatch = false;
    }

    private void AddMeasures(Dictionary<string, object?> data, EvaluationContext context)
    {
        for (int i = 0; i < measures.Count; i++)
        {
            data.Add(measureNames[i], ExpressionEvaluator.Evaluate(measures[i], context));
        }
    }

    /// <summary>
    /// The leading output columns of <paramref name="row"/>: the <c>ONE_ROW</c> columns, or for <c>ALL_ROWS_*</c>
    /// every input column in the row's enumeration order.
    /// </summary>
    private Dictionary<string, object?> OutputColumns(IDictionary<string, object?> row)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (!OneRow)
        {
            foreach (var (key, value) in row)
            {
                data.Add(key, value);
            }

            return data;
        }

        foreach (var column in oneRowColumns)
        {
            var key = FindKey(row, column);
            data.Add(oneRowColumnsAsWritten || key is null ? column : key, key is null ? null : row[key]);
        }

        return data;
    }

    /// <summary>The row's own spelling of <paramref name="column"/>, found case-insensitively; null if absent.</summary>
    private static string? FindKey(IDictionary<string, object?> row, string column)
    {
        foreach (var key in row.Keys)
        {
            if (string.Equals(key, column, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }
}
