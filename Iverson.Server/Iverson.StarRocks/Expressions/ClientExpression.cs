namespace Iverson.StarRocks.Expressions;

/// <summary>
/// Which emission site a client expression is compiled for; it decides which functions the
/// expression may call.
/// </summary>
internal enum ExpressionContext
{
    /// <summary>
    /// <c>AggregationSpec.expression</c> and <c>MetricSpec.expression</c>. The site wraps the result
    /// in an aggregate, so only the scalar functions COALESCE, NULLIF, ROUND and ABS may be called:
    /// an aggregate or a window there would nest one aggregate inside another.
    /// </summary>
    AggregateArgument,

    /// <summary>
    /// <c>DeriveColumn.expr</c>. The scalar functions plus SUM, AVG, MIN, MAX and COUNT (with
    /// <c>COUNT(*)</c>). A derive step has no GROUP BY, so the five aggregates must carry an
    /// <c>OVER (…)</c> clause. An aggregate's own argument is an aggregate argument.
    /// </summary>
    Derive,
}

/// <summary>
/// Compiles a client-supplied expression into StarRocks SQL. The text is length-checked, lexed and
/// parsed into a tree, and the SQL is written from the tree:
/// <list type="bullet">
/// <item><description>columns come from <c>resolveQuoted</c>;</description></item>
/// <item><description>function names come from the allow-list;</description></item>
/// <item><description>numbers are re-printed invariantly;</description></item>
/// <item><description>operators and parentheses come from the tree's shape.</description></item>
/// </list>
/// Nothing of the input text is copied to the output. Every failure is an
/// <see cref="EngagementQueryTranslationException"/>, which the RPC layer maps to InvalidArgument.
/// </summary>
internal static class ClientExpression
{
    internal const int MaxLength = 1000;

    /// <param name="text">The client's expression.</param>
    /// <param name="context">The emission site's rules.</param>
    /// <param name="resolveQuoted">
    /// Called for every column the expression names, including <c>PARTITION BY</c> and
    /// <c>ORDER BY</c> columns. It resolves and authorizes the name for the site, and returns its
    /// quoted SQL identifier or throws with the site's own message.
    /// </param>
    /// <param name="errorContext">Prefix for every message, naming the site and field.</param>
    public static string ToSql(
        string text, ExpressionContext context, Func<string, string> resolveQuoted, string errorContext)
    {
        // Before lexing: a valid identifier can be any length, so only this cap rejects it.
        if (text.Length > MaxLength)
            throw new EngagementQueryTranslationException(
                $"{errorContext}: expression exceeds {MaxLength} characters.");

        var tokens = SqlExpressionLexer.Tokenize(text, errorContext);
        var root = SqlExpressionParser.Parse(tokens, context, errorContext);
        return SqlExpressionEmitter.Emit(root, resolveQuoted);
    }
}
