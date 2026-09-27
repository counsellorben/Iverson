using System.Globalization;

namespace Iverson.Patterns.Syntax;

/// <summary>
/// Parses the SQL:2016 row-pattern grammar (spec §1) as Trino 483 accepts it:
/// <code>
/// alternation   := concatenation ('|' concatenation)*
/// concatenation := quantified+
/// quantified    := primary quantifier?
/// quantifier    := ('*' | '+' | '?' | '{' n? (',' m?)? '}') '?'?
/// primary       := IDENT | '(' ')' | '(' alternation ')' | '{-' alternation '-}' | '^' | '$'
///                | PERMUTE '(' alternation (',' alternation)+ ')'
/// </code>
/// </summary>
internal static class PatternParser
{
    public static ParsedPattern Parse(string pattern, IReadOnlyList<SubsetDefinition> subsets)
    {
        var tokens = Tokenize(pattern);
        var parser = new Parser(tokens);

        var root = parser.ParseAlternation();
        parser.ExpectEnd();

        var variables = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectVariables(root, variables, seen);

        var resolvedSubsets = ResolveSubsets(subsets, seen);

        return new ParsedPattern(root, variables, resolvedSubsets, ContainsExclusion(root));
    }

    private static void CollectVariables(PatternNode node, List<string> variables, HashSet<string> seen)
    {
        switch (node)
        {
            case LabelNode label:
                if (seen.Add(label.Name))
                {
                    variables.Add(label.Name);
                }

                break;
            case ExclusionNode exclusion:
                CollectVariables(exclusion.Pattern, variables, seen);
                break;
            case AlternationNode alternation:
                foreach (var child in alternation.Patterns)
                {
                    CollectVariables(child, variables, seen);
                }

                break;
            case ConcatenationNode concatenation:
                foreach (var child in concatenation.Patterns)
                {
                    CollectVariables(child, variables, seen);
                }

                break;
            case PermutationNode permutation:
                foreach (var child in permutation.Patterns)
                {
                    CollectVariables(child, variables, seen);
                }

                break;
            case QuantifiedNode quantified:
                CollectVariables(quantified.Pattern, variables, seen);
                break;
        }
    }

    private static bool ContainsExclusion(PatternNode node) => node switch
    {
        ExclusionNode => true,
        AlternationNode alternation => alternation.Patterns.Any(ContainsExclusion),
        ConcatenationNode concatenation => concatenation.Patterns.Any(ContainsExclusion),
        PermutationNode permutation => permutation.Patterns.Any(ContainsExclusion),
        QuantifiedNode quantified => ContainsExclusion(quantified.Pattern),
        _ => false,
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveSubsets(
        IReadOnlyList<SubsetDefinition> subsets, HashSet<string> primaryVariables)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var subset in subsets)
        {
            var name = subset.Name.ToUpperInvariant();

            if (primaryVariables.Contains(name))
            {
                throw new PatternValidationException(
                    $"Subset name '{name}' must not be the same as a primary pattern variable.");
            }

            if (!result.TryAdd(name, ResolveMembers(subset, name, primaryVariables)))
            {
                throw new PatternValidationException($"Duplicate subset name '{name}'.");
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ResolveMembers(
        SubsetDefinition subset, string subsetName, HashSet<string> primaryVariables)
    {
        if (subset.Variables.Count == 0)
        {
            throw new PatternValidationException($"Subset '{subsetName}' must have at least one member.");
        }

        var members = new List<string>(subset.Variables.Count);

        foreach (var variable in subset.Variables)
        {
            var upper = variable.ToUpperInvariant();

            if (!primaryVariables.Contains(upper))
            {
                throw new PatternValidationException(
                    $"Subset '{subsetName}' member '{upper}' is not a primary pattern variable.");
            }

            members.Add(upper);
        }

        return members;
    }

    // --- Tokenizer ---

    private enum TokenKind { Identifier, Brace, OpenBrace, CloseBrace, LParen, RParen, Pipe, Caret, Dollar, Comma, Star, Plus, Question, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Position, int BoundMin = 0, int? BoundMax = null);

    private static List<Token> Tokenize(string pattern)
    {
        var tokens = new List<Token>();
        var i = 0;
        var length = pattern.Length;

        while (i < length)
        {
            var c = pattern[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '{' && i + 1 < length && pattern[i + 1] == '-')
            {
                tokens.Add(new Token(TokenKind.OpenBrace, "{-", i));
                i += 2;
                continue;
            }

            if (c == '-' && i + 1 < length && pattern[i + 1] == '}')
            {
                tokens.Add(new Token(TokenKind.CloseBrace, "-}", i));
                i += 2;
                continue;
            }

            if (c == '{')
            {
                tokens.Add(ReadQuantifierBrace(pattern, ref i));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < length && (char.IsAsciiLetterOrDigit(pattern[i]) || pattern[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, pattern[start..i], start));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", i));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", i));
                    i++;
                    continue;
                case '|':
                    tokens.Add(new Token(TokenKind.Pipe, "|", i));
                    i++;
                    continue;
                case '^':
                    tokens.Add(new Token(TokenKind.Caret, "^", i));
                    i++;
                    continue;
                case '$':
                    tokens.Add(new Token(TokenKind.Dollar, "$", i));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", i));
                    i++;
                    continue;
                case '*':
                    tokens.Add(new Token(TokenKind.Star, "*", i));
                    i++;
                    continue;
                case '+':
                    tokens.Add(new Token(TokenKind.Plus, "+", i));
                    i++;
                    continue;
                case '?':
                    tokens.Add(new Token(TokenKind.Question, "?", i));
                    i++;
                    continue;
                default:
                    throw new PatternValidationException(
                        $"Unexpected character '{c}' at position {i} in pattern.");
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, length));
        return tokens;
    }

    /// <summary>
    /// Reads a quantifier brace <c>{n}</c> / <c>{n,}</c> / <c>{,m}</c> / <c>{,}</c> / <c>{n,m}</c> starting at
    /// <paramref name="i"/> (positioned on the opening <c>{</c>), resolving it to a concrete (min, max?) bound.
    /// A missing lower bound resolves to 0; a missing upper bound resolves to unbounded (<c>null</c>). The
    /// no-comma form <c>{n}</c> requires digits and resolves to the exact count (min == max == n).
    /// </summary>
    private static Token ReadQuantifierBrace(string pattern, ref int i)
    {
        var start = i;
        var length = pattern.Length;
        i++; // consume '{'
        i = SkipWhitespace(pattern, i);

        var hasMinDigits = TryReadDigits(pattern, ref i, out var minDigits);
        i = SkipWhitespace(pattern, i);

        var hasComma = i < length && pattern[i] == ',';
        var hasMaxDigits = false;
        var maxDigits = 0;

        if (hasComma)
        {
            i++;
            i = SkipWhitespace(pattern, i);
            hasMaxDigits = TryReadDigits(pattern, ref i, out maxDigits);
            i = SkipWhitespace(pattern, i);
        }

        if (i >= length || pattern[i] != '}')
        {
            throw new PatternValidationException(
                $"Malformed quantifier bound starting at position {start} in pattern.");
        }

        i++; // consume '}'

        if (!hasComma && !hasMinDigits)
        {
            throw new PatternValidationException(
                $"Malformed quantifier bound starting at position {start} in pattern.");
        }

        int min;
        int? max;

        if (!hasComma)
        {
            // Exact-count form {n}: min == max == n.
            min = minDigits;
            max = minDigits;
        }
        else
        {
            min = hasMinDigits ? minDigits : 0;
            max = hasMaxDigits ? maxDigits : null;
        }

        return new Token(TokenKind.Brace, pattern[start..i], start, min, max);
    }

    private static int SkipWhitespace(string pattern, int i)
    {
        while (i < pattern.Length && char.IsWhiteSpace(pattern[i]))
        {
            i++;
        }

        return i;
    }

    private static bool TryReadDigits(string pattern, ref int i, out int value)
    {
        var start = i;
        while (i < pattern.Length && char.IsAsciiDigit(pattern[i]))
        {
            i++;
        }

        if (i == start)
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(pattern[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            throw new PatternValidationException(
                $"Pattern quantifier bound at position {start} does not fit in a 32-bit integer.");
        }

        return true;
    }

    // --- Recursive-descent parser ---

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        private Token Current => tokens[_pos];

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
            {
                throw new PatternValidationException(
                    $"Unexpected token '{Current.Text}' at position {Current.Position} in pattern.");
            }
        }

        public PatternNode ParseAlternation()
        {
            var branches = new List<PatternNode> { ParseConcatenation() };

            while (Current.Kind == TokenKind.Pipe)
            {
                _pos++;
                branches.Add(ParseConcatenation());
            }

            return branches.Count == 1 ? branches[0] : new AlternationNode(branches);
        }

        private PatternNode ParseConcatenation()
        {
            var items = new List<PatternNode> { ParseQuantified() };

            while (IsPrimaryStart())
            {
                items.Add(ParseQuantified());
            }

            return items.Count == 1 ? items[0] : new ConcatenationNode(items);
        }

        private bool IsPrimaryStart() => Current.Kind switch
        {
            TokenKind.Identifier or TokenKind.LParen or TokenKind.OpenBrace or TokenKind.Caret or TokenKind.Dollar => true,
            _ => false,
        };

        private PatternNode ParseQuantified()
        {
            var primary = ParsePrimary();
            return TryParseQuantifier(primary);
        }

        private PatternNode TryParseQuantifier(PatternNode primary)
        {
            switch (Current.Kind)
            {
                case TokenKind.Star:
                    _pos++;
                    return new QuantifiedNode(primary, 0, null, ConsumeOptionalLazy());
                case TokenKind.Plus:
                    _pos++;
                    return new QuantifiedNode(primary, 1, null, ConsumeOptionalLazy());
                case TokenKind.Question:
                    _pos++;
                    return new QuantifiedNode(primary, 0, 1, ConsumeOptionalLazy());
                case TokenKind.Brace:
                {
                    var token = Current;
                    _pos++;

                    var min = token.BoundMin;
                    var max = token.BoundMax;

                    if (max is { } m && m < min)
                    {
                        throw new PatternValidationException(
                            $"Pattern quantifier lower bound must be less than or equal to upper bound "
                            + $"at position {token.Position} in pattern.");
                    }

                    if (max == 0)
                    {
                        throw new PatternValidationException(
                            "Pattern quantifier upper bound must be greater than or equal to 1 "
                            + $"at position {token.Position} in pattern.");
                    }

                    return new QuantifiedNode(primary, min, max, ConsumeOptionalLazy());
                }

                default:
                    return primary;
            }
        }

        private bool ConsumeOptionalLazy()
        {
            if (Current.Kind == TokenKind.Question)
            {
                _pos++;
                return false;
            }

            return true;
        }

        private PatternNode ParsePrimary()
        {
            switch (Current.Kind)
            {
                case TokenKind.Caret:
                    _pos++;
                    return new AnchorNode(PartitionStart: true);
                case TokenKind.Dollar:
                    _pos++;
                    return new AnchorNode(PartitionStart: false);
                case TokenKind.OpenBrace:
                    return ParseExclusion();
                case TokenKind.LParen:
                    return ParseParenthesized();
                case TokenKind.Identifier:
                    return ParseIdentifierPrimary();
                default:
                    throw new PatternValidationException(
                        $"Unexpected token '{Current.Text}' at position {Current.Position} in pattern.");
            }
        }

        private PatternNode ParseExclusion()
        {
            _pos++; // consume '{-'
            var inner = ParseAlternation();

            if (Current.Kind != TokenKind.CloseBrace)
            {
                throw new PatternValidationException(
                    $"Expected '-}}' at position {Current.Position} in pattern.");
            }

            _pos++; // consume '-}'
            return new ExclusionNode(inner);
        }

        private PatternNode ParseParenthesized()
        {
            var openPos = Current.Position;
            _pos++; // consume '('

            if (Current.Kind == TokenKind.RParen)
            {
                _pos++;
                return new EmptyNode();
            }

            var inner = ParseAlternation();

            if (Current.Kind != TokenKind.RParen)
            {
                throw new PatternValidationException(
                    $"Expected ')' to match '(' at position {openPos} in pattern.");
            }

            _pos++; // consume ')'
            return inner;
        }

        private PatternNode ParseIdentifierPrimary()
        {
            var identifier = Current;

            if (string.Equals(identifier.Text, "PERMUTE", StringComparison.OrdinalIgnoreCase)
                && IsPermuteInvocation(_pos + 1))
            {
                _pos++; // consume 'PERMUTE'
                return ParsePermutation();
            }

            _pos++; // consume identifier
            return new LabelNode(identifier.Text.ToUpperInvariant());
        }

        /// <summary>
        /// True when the token at <paramref name="lparenIndex"/> is <c>(</c> and its matching <c>)</c>
        /// contains a top-level comma — the Trino-parity rule distinguishing the PERMUTE keyword from an
        /// identifier named "permute".
        /// </summary>
        private bool IsPermuteInvocation(int lparenIndex)
        {
            if (tokens[lparenIndex].Kind != TokenKind.LParen)
            {
                return false;
            }

            var depth = 0;
            for (var j = lparenIndex; j < tokens.Count; j++)
            {
                switch (tokens[j].Kind)
                {
                    case TokenKind.LParen:
                        depth++;
                        break;
                    case TokenKind.RParen:
                        depth--;
                        if (depth == 0)
                        {
                            return false;
                        }

                        break;
                    case TokenKind.Comma when depth == 1:
                        return true;
                    case TokenKind.End:
                        return false;
                }
            }

            return false;
        }

        private PatternNode ParsePermutation()
        {
            var openPos = Current.Position;
            _pos++; // consume '('

            var branches = new List<PatternNode> { ParseAlternation() };

            while (Current.Kind == TokenKind.Comma)
            {
                _pos++;
                branches.Add(ParseAlternation());
            }

            if (Current.Kind != TokenKind.RParen)
            {
                throw new PatternValidationException(
                    $"Expected ')' to match '(' at position {openPos} in pattern.");
            }

            _pos++; // consume ')'
            return new PermutationNode(branches);
        }
    }
}
