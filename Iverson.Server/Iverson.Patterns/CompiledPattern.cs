using System.Collections.ObjectModel;
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Expressions;
using Iverson.Patterns.Matching;
using Iverson.Patterns.Syntax;

namespace Iverson.Patterns;

/// <summary>
/// A validated, compiled MatchPattern request (spec §3.1). Immutable once built; <see cref="Run"/> may be called for
/// many partitions, one after another, each with its own evaluation and matcher-run state.
/// </summary>
public sealed class CompiledPattern
{
    private const string ParentKey = "parent_key";
    private const string ChunkIndex = "chunk_index";
    private const string Text = "text";

    private static readonly string[] ChunkColumns = [ParentKey, ChunkIndex, Text];

    private readonly PartitionMatcher _partitionMatcher;

    private CompiledPattern(
        IReadOnlySet<string> referencedColumns,
        IReadOnlyList<SimilarityTerm> similarityTerms,
        IReadOnlyList<string> measureNames,
        PartitionMatcher partitionMatcher)
    {
        ReferencedColumns = referencedColumns;
        SimilarityTerms = similarityTerms;
        MeasureNames = measureNames;
        _partitionMatcher = partitionMatcher;
    }

    /// <summary>Every column <c>define</c>/<c>measures</c>/<c>SIMILARITY</c> reads, as written, compared case-insensitively.</summary>
    public IReadOnlySet<string> ReferencedColumns { get; }

    /// <summary>Distinct <c>SIMILARITY</c> terms; <c>Run</c>'s <c>similarity</c> callback is indexed by position in this list.</summary>
    public IReadOnlyList<SimilarityTerm> SimilarityTerms { get; }

    /// <summary>The <c>measures</c> names in request order (for Plan 2's TYPE_ROWS output-column comparison).</summary>
    public IReadOnlyList<string> MeasureNames { get; }

    /// <summary>
    /// Matches one partition, rows already in partition order, and yields its output rows lazily. Match numbers
    /// restart at 1 per call. Throws <see cref="PatternEvaluationException"/>,
    /// <see cref="PatternBudgetExceededException"/> and, once the budget's cancellation token is cancelled,
    /// <see cref="OperationCanceledException"/>, all during enumeration.
    /// <para>
    /// Caller contract:
    /// </para>
    /// <list type="bullet">
    /// <item><description><paramref name="partitionRows"/>: every row dictionary must compare keys with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> (spec §3.2). Columns are looked up under the spelling the
    /// expressions use, so with an ordinal dictionary a column written in another case fails with a
    /// <see cref="PatternEvaluationException"/> ("Column '…' is not in the row").</description></item>
    /// <item><description><paramref name="similarity"/>: called as <c>similarity(row, term)</c>, where
    /// <c>row</c> is the row's index within <paramref name="partitionRows"/> and <c>term</c> indexes
    /// <see cref="SimilarityTerms"/>; null is SQL <c>NULL</c>.</description></item>
    /// <item><description><paramref name="budget"/>: pass the same instance to every partition of one request.
    /// <see cref="PatternBudget.MaxSteps"/> is a per-request limit; only
    /// <see cref="PatternBudget.MaxActiveThreads"/> applies per match attempt.</description></item>
    /// <item><description>Run on a thread with a normal-size stack (not a reduced-stack thread): see
    /// <see cref="PatternQuery.Compile"/>.</description></item>
    /// </list>
    /// </summary>
    public IEnumerable<MatchOutputRow> Run(
        IReadOnlyList<IDictionary<string, object?>> partitionRows,
        Func<int, int, double?> similarity,
        PatternBudget budget) =>
        _partitionMatcher.Run(partitionRows, similarity, budget);

    internal static CompiledPattern Create(PatternRequest request, int maxProgramInstructions)
    {
        // 0. enum values: an undefined value (a cast integer) must not reach a switch that would mis-handle it
        RequireDefined(request.Source, "source");
        RequireDefined(request.RowsPerMatch, "rows_per_match");
        RequireDefined(request.AfterMatchSkip, "after_match");

        // 1. pattern and subsets
        var parsed = PatternParser.Parse(request.Pattern, request.Subsets);

        // 2. define names
        var variables = new HashSet<string>(parsed.Variables, StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var define in request.Define)
        {
            var name = define.Name.ToUpperInvariant();
            if (!defined.Add(name))
            {
                throw new PatternValidationException($"pattern variable with name: {name} is defined twice");
            }

            if (!variables.Contains(name))
            {
                throw new PatternValidationException($"defined variable: {name} is not a primary pattern variable");
            }
        }

        // 3. measures names
        var measureNames = new List<string>(request.Measures.Count);
        var distinctMeasures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var measure in request.Measures)
        {
            if (!distinctMeasures.Add(measure.Name))
            {
                throw new PatternValidationException($"Measure name '{measure.Name}' is used more than once.");
            }

            if (request.TenantColumn is { } tenant &&
                string.Equals(measure.Name, tenant, StringComparison.OrdinalIgnoreCase))
            {
                throw new PatternValidationException(
                    $"Measure name '{measure.Name}' is reserved for the tenant column.");
            }

            measureNames.Add(measure.Name);
        }

        // 4. Chunks: no partition_by, and no measure named like an output column
        bool chunks = request.Source == PatternSource.Chunks;
        if (chunks)
        {
            if (request.PartitionBy.Count > 0)
            {
                throw new PatternValidationException(
                    "partition_by must be empty for CHUNKS: the chunks are partitioned by parent_key.");
            }

            string[] outputColumns = request.RowsPerMatch == RowsPerMatch.OneRow ? [ParentKey] : ChunkColumns;
            foreach (var name in measureNames)
            {
                if (outputColumns.Contains(name, StringComparer.Ordinal))
                {
                    throw new PatternValidationException(
                        $"Measure name '{name}' collides with the CHUNKS output column '{name}'.");
                }
            }
        }

        // 4a. partition_by names each column once: input names resolve case-insensitively (spec §1)
        var partitionColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in request.PartitionBy)
        {
            if (!partitionColumns.Add(column))
            {
                throw new PatternValidationException($"partition_by column '{column}' is listed more than once.");
            }
        }

        // 5. exclusion with unmatched rows
        if (parsed.HasExclusion && request.RowsPerMatch == RowsPerMatch.AllRowsWithUnmatched)
        {
            throw new PatternValidationException(
                "Pattern exclusion is not allowed with ALL ROWS PER MATCH WITH UNMATCHED ROWS");
        }

        // 7 (label indexes and subset label sets, needed by 6). Label indexes follow the Variables order.
        var labelIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parsed.Variables.Count; i++)
        {
            labelIndexes.Add(parsed.Variables[i], i);
        }

        var subsetLabels = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (name, members) in parsed.Subsets)
        {
            subsetLabels.Add(name, members.Select(m => labelIndexes[m]).Distinct().Order().ToArray());
        }

        // 6. AFTER MATCH SKIP variable
        var skipNavigation = SkipNavigation(request, labelIndexes, subsetLabels);

        // 7. the program
        var program = ProgramCompiler.Compile(parsed.Root, labelIndexes, maxProgramInstructions);

        // 8. define and measures expressions
        var similarityTerms = new SimilarityTermTable();
        var parser = new ExpressionParser(labelIndexes, subsetLabels, similarityTerms);
        var defines = new Expr?[parsed.Variables.Count];
        foreach (var define in request.Define)
        {
            defines[labelIndexes[define.Name.ToUpperInvariant()]] = parser.ParseDefine(define.Expression);
        }

        var measures = request.Measures.Select(m => parser.ParseMeasure(m.Expression)).ToArray();

        // 9. Chunks: only the chunk columns, and SIMILARITY only on text
        if (chunks)
        {
            foreach (var column in parser.ReferencedColumns)
            {
                if (!ChunkColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    throw new PatternValidationException(
                        $"Column '{column}' is not a CHUNKS column (parent_key, chunk_index, text).");
                }
            }

            foreach (var term in similarityTerms.Terms)
            {
                if (!string.Equals(term.Column, Text, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PatternValidationException(
                        $"SIMILARITY over CHUNKS is only supported on the text column, not '{term.Column}'.");
                }
            }
        }

        // 10. the matcher, built once: the reachable-label precomputation is O(P²) in the program size
        var matcher = new Matcher(program, new ThreadEquivalence(program, defines));

        var partitionMatcher = new PartitionMatcher(
            matcher,
            defines,
            parsed.Variables,
            measureNames,
            measures,
            request.RowsPerMatch,
            request.AfterMatchSkip,
            skipNavigation,
            chunks ? [ParentKey] : request.PartitionBy.ToArray(),
            oneRowColumnsAsWritten: chunks);

        return new CompiledPattern(
            new ReadOnlySet<string>(parser.ReferencedColumns),
            similarityTerms.Terms.ToArray(),
            measureNames.AsReadOnly(),
            partitionMatcher);
    }

    private static void RequireDefined<TEnum>(TEnum value, string field) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new PatternValidationException(
                $"{field}: {Convert.ToInt64(value)} is not a defined {typeof(TEnum).Name} value.");
        }
    }

    /// <summary>Trino's skip-to navigation: <c>FIRST(v)</c> or <c>LAST(v)</c>, FINAL, over the variable's label set.</summary>
    private static Navigation? SkipNavigation(
        PatternRequest request, Dictionary<string, int> labelIndexes, Dictionary<string, int[]> subsetLabels)
    {
        var kind = request.AfterMatchSkip;
        var variable = request.SkipVariable;

        if (kind is AfterMatchSkipKind.PastLastRow or AfterMatchSkipKind.ToNextRow)
        {
            if (!string.IsNullOrEmpty(variable))
            {
                throw new PatternValidationException(
                    $"AFTER MATCH SKIP {kind} does not take a pattern variable, but '{variable}' was given.");
            }

            return null;
        }

        if (string.IsNullOrEmpty(variable))
        {
            throw new PatternValidationException($"AFTER MATCH SKIP {kind} requires a pattern variable.");
        }

        var name = variable.ToUpperInvariant();
        int[] labels;
        if (labelIndexes.TryGetValue(name, out int label))
        {
            labels = [label];
        }
        else if (!subsetLabels.TryGetValue(name, out labels!))
        {
            throw new PatternValidationException(
                $"AFTER MATCH SKIP target '{variable}' is not a pattern variable or subset name.");
        }

        return new Navigation(labels, Last: kind == AfterMatchSkipKind.ToLast, Running: false, 0, 0);
    }
}
