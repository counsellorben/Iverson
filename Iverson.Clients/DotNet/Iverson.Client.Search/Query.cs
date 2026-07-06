using System.Linq.Expressions;

namespace Iverson.Client.Search;

/// <summary>
/// Entry point for the Iverson search DSL.
/// Usage: <c>Query.For&lt;Article&gt;().Where(...).OrderBy(...).Page(0).Build()</c>
/// </summary>
public static class Query
{
    public static QueryBuilder<T> For<T>() where T : class => new(typeof(T).Name);

    /// <summary>
    /// Entry point for the GROUP BY DSL. Not generic on a single type, since joins bring
    /// multiple registered types into scope.
    /// </summary>
    public static GroupByBuilder GroupBy(string typeName) => new(typeName);

    /// <summary>
    /// Entry point for the pipeline (CTE chain) DSL. String-based like GroupBy —
    /// steps and joins bring multiple sources into scope. Equivalent to
    /// <see cref="Iverson.Client.Search.Pipeline.For(string)"/>.
    /// </summary>
    public static PipelineBuilder Pipeline(string typeName) => new(typeName);

    /// <summary>
    /// Entry point for Qdrant vector similarity search on a property annotated with
    /// <c>[IversonEmbedding]</c>.
    /// </summary>
    public static QuerySimilarBuilder<T> Similar<T>(Expression<Func<T, object>> property) where T : class =>
        new(typeof(T).Name, PropertyNameObj(property));

    /// <summary>
    /// Entry point for Qdrant chunk/RAG search on a property annotated with <c>[IversonChunk]</c>.
    /// </summary>
    public static QueryChunksBuilder<T> Chunks<T>(Expression<Func<T, object>> property) where T : class =>
        new(typeof(T).Name, PropertyNameObj(property));

    private static string PropertyNameObj<TSource>(Expression<Func<TSource, object>> expr) =>
        expr.Body switch
        {
            MemberExpression member => member.Member.Name,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
                when unary.Operand is MemberExpression innerMember => innerMember.Member.Name,
            _ => throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title")
        };
}
