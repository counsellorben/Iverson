namespace Iverson.StarRocks.Expressions;

/// <summary>A node of a parsed client expression. <see cref="Position"/> is the source offset of its first token.</summary>
internal abstract record SqlNode(int Position);

internal sealed record NumberNode(decimal Value, int Position) : SqlNode(Position);

internal sealed record NullNode(int Position) : SqlNode(Position);

internal sealed record ColumnNode(string Name, int Position) : SqlNode(Position);

internal sealed record NegateNode(SqlNode Operand, int Position) : SqlNode(Position);

/// <param name="Operator">One of <c>+ - * / %</c>.</param>
internal sealed record BinaryNode(char Operator, SqlNode Left, SqlNode Right, int Position) : SqlNode(Position);

/// <param name="Function">The allow-list's own upper-case spelling, never the input's.</param>
/// <param name="CountStar">True for <c>COUNT(*)</c>; <see cref="Arguments"/> is then empty.</param>
/// <param name="Over">The window clause; present exactly when the function is an aggregate.</param>
internal sealed record CallNode(
    string Function, IReadOnlyList<SqlNode> Arguments, bool CountStar, WindowNode? Over, int Position)
    : SqlNode(Position);

internal sealed record WindowNode(IReadOnlyList<ColumnNode> PartitionBy, IReadOnlyList<OrderKeyNode> OrderBy);

/// <param name="Direction"><c>"ASC"</c>, <c>"DESC"</c>, or null when the input gave none.</param>
internal sealed record OrderKeyNode(ColumnNode Column, string? Direction);

/// <summary>
/// Recursive-descent parser for client expressions. Precedence, lowest first:
/// <code>
/// expression     := multiplicative (('+' | '-') multiplicative)*
/// multiplicative := unary (('*' | '/' | '%') unary)*
/// unary          := '-' unary | primary
/// primary        := number | NULL | column | call | '(' expression ')'
/// call           := function '(' (expression (',' expression)* | '*') ')' [over]
/// over           := OVER '(' [PARTITION BY column (',' column)*]
///                            [ORDER BY column [ASC | DESC] (',' column [ASC | DESC])*] ')'
/// </code>
/// <list type="bullet">
/// <item><description><c>*</c> is an argument only as COUNT's sole argument.</description></item>
/// <item><description><c>over</c> is required after an aggregate and allowed nowhere else.</description></item>
/// <item><description>Aggregates are allowed only in <see cref="ExpressionContext.Derive"/>.</description></item>
/// <item><description>The reserved words are never column names.</description></item>
/// <item><description>Nesting is capped at <see cref="MaxDepth"/> levels. The levels are the root,
/// each parenthesized expression or call argument, and each unary minus. So recursion is bounded
/// whatever the input.</description></item>
/// </list>
/// </summary>
internal sealed class SqlExpressionParser
{
    internal const int MaxDepth = 32;

    // Argument counts StarRocks accepts, as (min, max); max is int.MaxValue when unbounded.
    // The keys are the scalar allow-list.
    private static readonly Dictionary<string, (int Min, int Max)> ScalarFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["COALESCE"] = (1, int.MaxValue),
        ["NULLIF"] = (2, 2),
        ["ROUND"] = (1, 2),
        ["ABS"] = (1, 1),
    };

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT"
    };

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "OVER", "PARTITION", "BY", "ORDER", "ASC", "DESC"
    };

    private readonly List<SqlToken> _tokens;
    private readonly string _errorContext;
    private int _pos;
    private int _depth;

    private SqlExpressionParser(List<SqlToken> tokens, string errorContext)
    {
        _tokens = tokens;
        _errorContext = errorContext;
    }

    public static SqlNode Parse(List<SqlToken> tokens, ExpressionContext context, string errorContext)
    {
        var parser = new SqlExpressionParser(tokens, errorContext);
        var root = parser.ParseExpression(context);
        if (parser.Current.Kind != SqlTokenKind.End)
            throw parser.Unexpected();
        return root;
    }

    private SqlToken Current => _tokens[_pos];

    private SqlToken Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private SqlNode ParseExpression(ExpressionContext context)
    {
        Enter();
        var left = ParseMultiplicative(context);
        while (Current.Kind is SqlTokenKind.Plus or SqlTokenKind.Minus)
        {
            var op = Current;
            _pos++;
            left = new BinaryNode(op.Kind == SqlTokenKind.Plus ? '+' : '-', left, ParseMultiplicative(context), op.Position);
        }
        _depth--;
        return left;
    }

    private SqlNode ParseMultiplicative(ExpressionContext context)
    {
        var left = ParseUnary(context);
        while (Current.Kind is SqlTokenKind.Star or SqlTokenKind.Slash or SqlTokenKind.Percent)
        {
            var op = Current;
            _pos++;
            var symbol = op.Kind switch
            {
                SqlTokenKind.Star => '*',
                SqlTokenKind.Slash => '/',
                _ => '%',
            };
            left = new BinaryNode(symbol, left, ParseUnary(context), op.Position);
        }
        return left;
    }

    private SqlNode ParseUnary(ExpressionContext context)
    {
        if (Current.Kind != SqlTokenKind.Minus)
            return ParsePrimary(context);

        var minus = Current;
        _pos++;
        Enter();
        var operand = ParseUnary(context);
        _depth--;
        return new NegateNode(operand, minus.Position);
    }

    private SqlNode ParsePrimary(ExpressionContext context)
    {
        var token = Current;
        switch (token.Kind)
        {
            case SqlTokenKind.Number:
                _pos++;
                return new NumberNode(token.Number, token.Position);

            case SqlTokenKind.LParen:
            {
                _pos++;
                var inner = ParseExpression(context);
                Expect(SqlTokenKind.RParen);
                return inner;
            }

            case SqlTokenKind.Identifier when token.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase):
                _pos++;
                return new NullNode(token.Position);

            case SqlTokenKind.Identifier when ReservedWords.Contains(token.Text):
                throw Unexpected();

            case SqlTokenKind.Identifier when Peek(1).Kind == SqlTokenKind.LParen:
                return ParseCall(context);

            case SqlTokenKind.Identifier:
                _pos++;
                return new ColumnNode(token.Text, token.Position);

            default:
                throw Unexpected();
        }
    }

    private SqlNode ParseCall(ExpressionContext context)
    {
        var name = Current;
        string function;
        bool isAggregate;
        var arity = (Min: 0, Max: int.MaxValue);
        if (AggregateFunctions.TryGetValue(name.Text, out var aggregate))
        {
            function = aggregate;
            isAggregate = true;
        }
        else if (ScalarFunctions.TryGetValue(name.Text, out var scalarArity))
        {
            function = name.Text.ToUpperInvariant(); // the allow-list's keys are upper case
            arity = scalarArity;
            isAggregate = false;
        }
        else
        {
            throw Invalid($"unknown function '{name.Text}' at position {name.Position}");
        }

        if (isAggregate && context == ExpressionContext.AggregateArgument)
            throw Invalid($"aggregate function '{function}' at position {name.Position} is not allowed here; " +
                          "only COALESCE, NULLIF, ROUND and ABS may be called");

        _pos += 2; // the name and its '('

        // An aggregate's argument is itself an aggregate argument: no aggregate or window inside it.
        var argumentContext = isAggregate ? ExpressionContext.AggregateArgument : context;
        var arguments = new List<SqlNode>();
        var countStar = false;
        if (function == "COUNT" && Current.Kind == SqlTokenKind.Star && Peek(1).Kind == SqlTokenKind.RParen)
        {
            countStar = true;
            _pos++;
        }
        else
        {
            arguments.Add(ParseExpression(argumentContext));
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                arguments.Add(ParseExpression(argumentContext));
            }
        }
        Expect(SqlTokenKind.RParen);

        if (!isAggregate && (arguments.Count < arity.Min || arguments.Count > arity.Max))
        {
            var expected = arity.Max == int.MaxValue ? $"at least {arity.Min}"
                : arity.Min == arity.Max ? $"{arity.Min}" : $"{arity.Min} to {arity.Max}";
            throw Invalid($"function '{function}' at position {name.Position} takes {expected} " +
                          $"argument(s), not {arguments.Count}");
        }

        WindowNode? over = null;
        if (isAggregate)
        {
            if (!IsWord("OVER"))
                throw Invalid($"aggregate function '{function}' at position {name.Position} requires an OVER clause");
            over = ParseOver();
        }

        return new CallNode(function, arguments, countStar, over, name.Position);
    }

    private WindowNode ParseOver()
    {
        _pos++; // OVER
        Expect(SqlTokenKind.LParen);

        var partitionBy = new List<ColumnNode>();
        if (IsWord("PARTITION"))
        {
            _pos++;
            ExpectWord("BY");
            partitionBy.Add(ParseColumn());
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                partitionBy.Add(ParseColumn());
            }
        }

        var orderBy = new List<OrderKeyNode>();
        if (IsWord("ORDER"))
        {
            _pos++;
            ExpectWord("BY");
            orderBy.Add(ParseOrderKey());
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                orderBy.Add(ParseOrderKey());
            }
        }

        Expect(SqlTokenKind.RParen);
        return new WindowNode(partitionBy, orderBy);
    }

    private OrderKeyNode ParseOrderKey()
    {
        var column = ParseColumn();
        string? direction = null;
        if (IsWord("ASC"))
        {
            direction = "ASC";
            _pos++;
        }
        else if (IsWord("DESC"))
        {
            direction = "DESC";
            _pos++;
        }
        return new OrderKeyNode(column, direction);
    }

    private ColumnNode ParseColumn()
    {
        var token = Current;
        if (token.Kind != SqlTokenKind.Identifier || ReservedWords.Contains(token.Text)
            || Peek(1).Kind == SqlTokenKind.LParen)
            throw Unexpected();
        _pos++;
        return new ColumnNode(token.Text, token.Position);
    }

    private bool IsWord(string word) =>
        Current.Kind == SqlTokenKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private void ExpectWord(string word)
    {
        if (!IsWord(word))
            throw Unexpected();
        _pos++;
    }

    private void Expect(SqlTokenKind kind)
    {
        if (Current.Kind != kind)
            throw Unexpected();
        _pos++;
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw Invalid($"expression nests deeper than {MaxDepth} levels at position {Current.Position}");
    }

    private EngagementQueryTranslationException Unexpected() =>
        Current.Kind == SqlTokenKind.End
            ? Invalid($"unexpected end of expression at position {Current.Position}")
            : Invalid($"unexpected '{Current.Text}' at position {Current.Position}");

    private EngagementQueryTranslationException Invalid(string message) =>
        new($"{_errorContext}: {message}.");
}
