using System.Linq.Expressions;

namespace Iverson.Client.Search;

/// <summary>
/// Extracts a property name from a direct member-access expression, e.g. <c>x => x.Title</c>.
/// Shared by <see cref="QueryBuilder{T}"/>, <see cref="QuerySimilarBuilder{T}"/>, and
/// <see cref="QueryChunksBuilder{T}"/> — all three previously defined byte-identical private copies.
/// </summary>
internal static class PropertyNameExtractor
{
    internal static string PropertyName<T, TValue>(Expression<Func<T, TValue>> expr) =>
        expr.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title");
}
