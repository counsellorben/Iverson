namespace Iverson.Patterns;

/// <summary>
/// One output row. <see cref="Data"/> is keyed ordinally (spec §1): <c>ONE_ROW</c> holds the partition columns
/// then the measures; <c>ALL_ROWS_*</c> holds every input column then the measures. <see cref="MatchNumber"/> is 0
/// for an unmatched row; <see cref="Classifier"/> is the row's upper-case label for a matched <c>ALL_ROWS_*</c>
/// row and empty otherwise.
/// </summary>
public sealed record MatchOutputRow(IReadOnlyDictionary<string, object?> Data, long MatchNumber, string Classifier);

/// <summary>A distinct <c>SIMILARITY(col, 'text')</c> term, as written (spec §3.3 embeds <see cref="Text"/> once).</summary>
public sealed record SimilarityTerm(string Column, string Text);
