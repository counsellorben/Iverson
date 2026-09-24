namespace Iverson.Patterns.Expressions;

/// <summary>Distinct <c>(col, text)</c> terms in first-appearance order. The column compares case-insensitively
/// (it resolves to a schema descriptor case-insensitively, spec §2); the text compares ordinally.</summary>
internal sealed class SimilarityTermTable
{
    private readonly List<SimilarityTerm> _terms = [];

    public IReadOnlyList<SimilarityTerm> Terms => _terms;

    public int GetOrAdd(string column, string text)
    {
        for (int i = 0; i < _terms.Count; i++)
            if (string.Equals(_terms[i].Column, column, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_terms[i].Text, text, StringComparison.Ordinal))
                return i;
        _terms.Add(new SimilarityTerm(column, text));
        return _terms.Count - 1;
    }
}
