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
}
