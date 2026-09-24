using System.Globalization;

namespace Iverson.Patterns.Expressions;

internal enum ExpressionTokenKind
{
    Identifier,
    Integer,
    Decimal,
    String,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    LParen,
    RParen,
    Comma,
    Dot,
    End,
}

/// <param name="Text">The token's source spelling (for a string, the unescaped contents).</param>
/// <param name="Value">A <c>long</c> for <see cref="ExpressionTokenKind.Integer"/>, a <c>double</c> for
/// <see cref="ExpressionTokenKind.Decimal"/>, the unescaped <c>string</c> for <see cref="ExpressionTokenKind.String"/>.</param>
internal readonly record struct ExpressionToken(ExpressionTokenKind Kind, string Text, int Position, object? Value = null);

/// <summary>
/// Tokenizes a <c>define</c> or <c>measures</c> expression (spec §2). Whitespace is skipped. Identifiers are
/// <c>[A-Za-z_][A-Za-z0-9_]*</c> and keep their spelling; keywords are recognized case-insensitively by the parser.
/// Numbers are unsigned: <c>\d+</c> is a <c>long</c>; <c>\d+\.\d*</c>, <c>\.\d+</c> and any of them with an exponent
/// <c>[eE][+-]?\d+</c> are a <c>double</c>. Strings are <c>'…'</c> with <c>''</c> as an escaped quote.
/// </summary>
internal static class ExpressionLexer
{
    public static List<ExpressionToken> Tokenize(string text)
    {
        var tokens = new List<ExpressionToken>();
        var i = 0;
        var length = text.Length;

        while (i < length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new ExpressionToken(ExpressionTokenKind.Identifier, text[start..i], start));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < length && char.IsAsciiDigit(text[i + 1])))
            {
                tokens.Add(ReadNumber(text, ref i));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(ReadString(text, ref i));
                continue;
            }

            var operatorStart = i;
            var next = i + 1 < length ? text[i + 1] : '\0';
            ExpressionTokenKind kind;
            var width = 1;

            switch (c)
            {
                case '<' when next == '>':
                    kind = ExpressionTokenKind.NotEqual;
                    width = 2;
                    break;
                case '<' when next == '=':
                    kind = ExpressionTokenKind.LessOrEqual;
                    width = 2;
                    break;
                case '>' when next == '=':
                    kind = ExpressionTokenKind.GreaterOrEqual;
                    width = 2;
                    break;
                case '<':
                    kind = ExpressionTokenKind.Less;
                    break;
                case '>':
                    kind = ExpressionTokenKind.Greater;
                    break;
                case '=':
                    kind = ExpressionTokenKind.Equal;
                    break;
                case '+':
                    kind = ExpressionTokenKind.Plus;
                    break;
                case '-':
                    kind = ExpressionTokenKind.Minus;
                    break;
                case '*':
                    kind = ExpressionTokenKind.Star;
                    break;
                case '/':
                    kind = ExpressionTokenKind.Slash;
                    break;
                case '%':
                    kind = ExpressionTokenKind.Percent;
                    break;
                case '(':
                    kind = ExpressionTokenKind.LParen;
                    break;
                case ')':
                    kind = ExpressionTokenKind.RParen;
                    break;
                case ',':
                    kind = ExpressionTokenKind.Comma;
                    break;
                case '.':
                    kind = ExpressionTokenKind.Dot;
                    break;
                default:
                    throw new PatternValidationException($"Unexpected character '{c}' at position {i} in expression.");
            }

            i += width;
            tokens.Add(new ExpressionToken(kind, text[operatorStart..i], operatorStart));
        }

        tokens.Add(new ExpressionToken(ExpressionTokenKind.End, string.Empty, length));
        return tokens;
    }

    private static ExpressionToken ReadNumber(string text, ref int i)
    {
        var start = i;
        var isDecimal = false;

        SkipDigits(text, ref i);

        if (i < text.Length && text[i] == '.')
        {
            isDecimal = true;
            i++;
            SkipDigits(text, ref i);
        }

        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var exponent = i + 1;
            if (exponent < text.Length && (text[exponent] == '+' || text[exponent] == '-'))
            {
                exponent++;
            }

            if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
            {
                isDecimal = true;
                i = exponent;
                SkipDigits(text, ref i);
            }
        }

        var spelling = text[start..i];

        if (isDecimal)
        {
            var value = double.Parse(spelling, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value))
            {
                throw new PatternValidationException(
                    $"Numeric literal '{spelling}' at position {start} is out of range.");
            }

            return new ExpressionToken(ExpressionTokenKind.Decimal, spelling, start, value);
        }

        if (!long.TryParse(spelling, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
        {
            throw new PatternValidationException(
                $"Integer literal '{spelling}' at position {start} does not fit in a 64-bit integer.");
        }

        return new ExpressionToken(ExpressionTokenKind.Integer, spelling, start, integer);
    }

    private static void SkipDigits(string text, ref int i)
    {
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }
    }

    private static ExpressionToken ReadString(string text, ref int i)
    {
        var start = i;
        var builder = new System.Text.StringBuilder();
        i++; // consume the opening quote

        while (true)
        {
            if (i >= text.Length)
            {
                throw new PatternValidationException(
                    $"Unterminated string literal starting at position {start} in expression.");
            }

            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i += 2;
                    continue;
                }

                i++; // consume the closing quote
                var value = builder.ToString();
                return new ExpressionToken(ExpressionTokenKind.String, value, start, value);
            }

            builder.Append(text[i]);
            i++;
        }
    }
}
