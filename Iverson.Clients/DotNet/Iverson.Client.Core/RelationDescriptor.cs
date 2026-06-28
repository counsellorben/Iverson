using System.Reflection;

namespace Iverson.Client.Core;

public enum RelationKind { OneToOne, OneToMany, ManyToOne, ManyToMany }

public sealed class RelationDescriptor
{
    public required PropertyInfo Property    { get; init; }
    public required Type         RelatedType { get; init; }
    public required RelationKind Kind        { get; init; }

    /// <summary>
    /// For OneToOne / ManyToOne: property name on *this* entity that holds the related key.
    /// For OneToMany: property name on the *related* entity that holds this entity's key.
    /// For ManyToMany: payload key containing the array of related ids.
    /// Null triggers inference logic in GraphAssembler.
    /// </summary>
    public string? ForeignKey { get; init; }

    /// <summary>
    /// Resolved at startup for OneToOne / ManyToOne. Null for OneToMany / ManyToMany.
    /// </summary>
    public PropertyInfo? ForeignKeyProperty { get; init; }
}
