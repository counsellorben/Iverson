namespace Iverson.Client.Search;

/// <summary>
/// Entry point for the Iverson search DSL.
/// Usage: <c>Query.For&lt;Article&gt;().Where(...).OrderBy(...).Page(1).Build()</c>
/// </summary>
public static class Query
{
    public static QueryBuilder<T> For<T>() where T : class => new(typeof(T).Name);
}
