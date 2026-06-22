namespace Iverson.Client.Attributes;

/// <summary>
/// Declares a many-to-many relationship between this entity and <paramref name="related"/>.
/// The decorated property must be a collection type. The server returns the join keys
/// within the payload; the GraphAssembler uses <paramref name="joinKey"/> to locate them
/// and issues a streamed multi-retrieval for the related entities.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ManyToManyAttribute(Type related, string? joinKey = null) : Attribute
{
    public Type Related { get; } = related;

    /// <summary>
    /// Key within the server payload that contains the array of related entity ids.
    /// If null, inferred as "{RelatedTypeName}Ids".
    /// </summary>
    public string? JoinKey { get; } = joinKey;
}
