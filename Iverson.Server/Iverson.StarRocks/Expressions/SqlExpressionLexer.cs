using System.Globalization;

namespace Iverson.StarRocks.Expressions;

internal enum SqlTokenKind
{
    Identifier,
    Number,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    LParen,
    RParen,
    Comma,
    End,
}

/// <param name="Text">The token's spelling: an identifier's name, a number's digits, or the operator.</param>
/// <param name="Number">The parsed value of a <see cref="SqlTokenKind.Number"/> token.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Position, decimal Number = 0m);

/// <summary>
/// Tokenizes a client expression. It accepts:
/// <list type="bullet">
/// <item><description>ASCII whitespace (space, tab, CR, LF);</description></item>
/// <item><description>identifiers <c>[A-Za-z_][A-Za-z0-9_]*</c>;</description></item>
/// <item><description>unsigned decimal numbers <c>\d+(\.\d+)?</c>;</description></item>
/// <item><description>the operators <c>+ - * / %</c>, parentheses and comma.</description></item>
/// </list>
/// Every other character is refused. So are the comment openers <c>--</c> and <c>/*</c>, although
/// each of their characters is an operator.
/// </summary>
internal static class SqlExpressionLexer
{
    public static List<SqlToken> Tokenize(string text, string errorContext)
    {
        var tokens = new List<SqlToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SqlToken(SqlTokenKind.Identifier, text[start..i], start));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                tokens.Add(ReadNumber(text, ref i, errorContext));
                continue;
            }

            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if ((c == '-' && next == '-') || (c == '/' && next == '*'))
                throw Refused(errorContext, $"comment sequence '{c}{next}' at position {i}");

            var kind = c switch
            {
                '+' => SqlTokenKind.Plus,
                '-' => SqlTokenKind.Minus,
                '*' => SqlTokenKind.Star,
                '/' => SqlTokenKind.Slash,
                '%' => SqlTokenKind.Percent,
                '(' => SqlTokenKind.LParen,
                ')' => SqlTokenKind.RParen,
                ',' => SqlTokenKind.Comma,
                _ => throw Refused(errorContext, $"character {Describe(c)} at position {i}"),
            };
            tokens.Add(new SqlToken(kind, c.ToString(), i));
            i++;
        }

        tokens.Add(new SqlToken(SqlTokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static SqlToken ReadNumber(string text, ref int i, string errorContext)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;

        if (i < text.Length && text[i] == '.')
        {
            i++;
            if (i >= text.Length || !char.IsAsciiDigit(text[i]))
                throw new EngagementQueryTranslationException(
                    $"{errorContext}: number at position {start} has no digits after its decimal point.");
            while (i < text.Length && char.IsAsciiDigit(text[i]))
                i++;
        }

        var spelling = text[start..i];

        // TryParse, never Parse: a value too large for decimal must come back as a refusal, not as
        // an OverflowException that the RPC layer would answer with UNKNOWN.
        if (!decimal.TryParse(spelling, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new EngagementQueryTranslationException(
                $"{errorContext}: number '{spelling}' at position {start} is out of range.");

        return new SqlToken(SqlTokenKind.Number, spelling, start, value);
    }

    // Printable ASCII is shown as itself; control characters and non-ASCII by code point.
    private static string Describe(char c) =>
        c is >= ' ' and <= '~' ? $"'{c}'" : $"U+{(int)c:X4}";

    private static EngagementQueryTranslationException Refused(string errorContext, string what) =>
        new($"{errorContext}: {what} is not allowed in an expression.");
}
