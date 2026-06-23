using System.Collections.Concurrent;
using System.Reflection;
using Iverson.Client.Attributes;

namespace Iverson.Client.Core;

public sealed class EntityRegistry
{
    private readonly ConcurrentDictionary<Type, EntityDescriptor>   _byType = new();
    private readonly ConcurrentDictionary<string, EntityDescriptor> _byName = new();

    public EntityRegistry(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
            Scan(assembly);
    }

    private void Scan(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var entityAttr = type.GetCustomAttribute<IversonEntityAttribute>();
            if (entityAttr is null) continue;

            var keyProp = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetCustomAttribute<IversonKeyAttribute>() is not null)
                ?? throw new InvalidOperationException(
                    $"'{type.Name}' is marked [IversonEntity] but has no [IversonKey] property.");

            var relations = BuildRelations(type);
            var name      = entityAttr.Name ?? type.Name;

            var descriptor = new EntityDescriptor
            {
                EntityType  = type,
                EntityName  = name,
                KeyProperty = keyProp,
                Relations   = relations
            };

            _byType[type] = descriptor;
            _byName[name] = descriptor;
        }
    }

    private static IReadOnlyList<RelationDescriptor> BuildRelations(Type type)
    {
        var relations = new List<RelationDescriptor>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<OneToOneAttribute>() is { } oto)
                relations.Add(new RelationDescriptor { Property = prop, RelatedType = oto.Related,  Kind = RelationKind.OneToOne,  ForeignKey = oto.ForeignKey });

            else if (prop.GetCustomAttribute<OneToManyAttribute>() is { } otm)
                relations.Add(new RelationDescriptor { Property = prop, RelatedType = otm.Related,  Kind = RelationKind.OneToMany,  ForeignKey = otm.ForeignKey });

            else if (prop.GetCustomAttribute<ManyToOneAttribute>() is { } mto)
                relations.Add(new RelationDescriptor { Property = prop, RelatedType = mto.Related,  Kind = RelationKind.ManyToOne,  ForeignKey = mto.ForeignKey });

            else if (prop.GetCustomAttribute<ManyToManyAttribute>() is { } mtm)
                relations.Add(new RelationDescriptor { Property = prop, RelatedType = mtm.Related,  Kind = RelationKind.ManyToMany, ForeignKey = mtm.JoinKey });
        }

        return relations;
    }

    public IEnumerable<EntityDescriptor> All => _byType.Values;

    public EntityDescriptor Get<T>()          => Get(typeof(T));
    public EntityDescriptor Get(Type type)    => _byType.TryGetValue(type,  out var d) ? d : Throw(type.Name);
    public EntityDescriptor GetByName(string name) => _byName.TryGetValue(name, out var d) ? d : Throw(name);

    private static EntityDescriptor Throw(string name) =>
        throw new InvalidOperationException($"No IversonEntity registered for '{name}'.");
}
