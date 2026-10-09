using System.Globalization;

namespace Iverson.StarRocks.Expressions;

/// <summary>
/// Writes StarRocks SQL from a parsed client expression. Nothing is copied from the input text:
/// <list type="bullet">
/// <item><description>columns come from the site's callback;</description></item>
/// <item><description>function names come from the parser's allow-list;</description></item>
/// <item><description>numbers are re-printed with <see cref="CultureInfo.InvariantCulture"/>;</description></item>
/// <item><description>every operator and parenthesis comes from the tree.</description></item>
/// </list>
/// A child is parenthesized when its precedence is lower than its position needs, so the output
/// parses back to the same tree.
/// </summary>
internal static class SqlExpressionEmitter
{
    private const int AdditivePrecedence = 1;
    private const int MultiplicativePrecedence = 2;
    private const int UnaryPrecedence = 3;
    private const int PrimaryPrecedence = 4;

    public static string Emit(SqlNode node, Func<string, string> resolveQuoted) => node switch
    {
        NumberNode number => number.Value.ToString(CultureInfo.InvariantCulture),
        NullNode => "NULL",
        ColumnNode column => resolveQuoted(column.Name),
        // The operand is parenthesized unless it is a primary, so a negated negation is written
        // "-(-x)". Two adjacent minus signs would open a SQL line comment.
        NegateNode negate => "-" + Wrap(negate.Operand, PrimaryPrecedence, resolveQuoted),
        // Left-associative: an equal-precedence right operand keeps its parentheses, so
        // "a - (b - c)" is not rewritten as "a - b - c".
        BinaryNode binary =>
            $"{Wrap(binary.Left, Precedence(binary), resolveQuoted)} {binary.Operator} " +
            $"{Wrap(binary.Right, Precedence(binary) + 1, resolveQuoted)}",
        CallNode call => EmitCall(call, resolveQuoted),
        _ => throw new EngagementQueryTranslationException(
            $"Unsupported expression node '{node.GetType().Name}'."),
    };

    private static string Wrap(SqlNode child, int minimumPrecedence, Func<string, string> resolveQuoted)
    {
        var sql = Emit(child, resolveQuoted);
        return Precedence(child) < minimumPrecedence ? $"({sql})" : sql;
    }

    private static int Precedence(SqlNode node) => node switch
    {
        BinaryNode { Operator: '+' or '-' } => AdditivePrecedence,
        BinaryNode => MultiplicativePrecedence,
        NegateNode => UnaryPrecedence,
        _ => PrimaryPrecedence,
    };

    private static string EmitCall(CallNode call, Func<string, string> resolveQuoted)
    {
        var arguments = call.CountStar
            ? "*"
            : string.Join(", ", call.Arguments.Select(argument => Emit(argument, resolveQuoted)));
        var sql = $"{call.Function}({arguments})";
        if (call.Over is null)
            return sql;

        var clauses = new List<string>();
        if (call.Over.PartitionBy.Count > 0)
            clauses.Add("PARTITION BY " +
                        string.Join(", ", call.Over.PartitionBy.Select(column => resolveQuoted(column.Name))));
        if (call.Over.OrderBy.Count > 0)
            clauses.Add("ORDER BY " + string.Join(", ", call.Over.OrderBy.Select(key =>
                key.Direction is null
                    ? resolveQuoted(key.Column.Name)
                    : $"{resolveQuoted(key.Column.Name)} {key.Direction}")));
        return $"{sql} OVER ({string.Join(" ", clauses)})";
    }
}
