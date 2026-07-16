using System.Diagnostics;
using System.Reflection;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;

namespace Iverson.Client.Core;

/// <summary>
/// Reflects over all [IversonEntity] types in the EntityRegistry and registers
/// their schemas with the server before any CRUD operations are attempted.
/// </summary>
public sealed class SchemaRegistrar(
    EntityRegistry registry,
    ObjectMappingService.ObjectMappingServiceClient mapping,
    ILogger<SchemaRegistrar> logger)
{
    public async Task RegisterAllAsync(
        IReadOnlyDictionary<string, AuthorizationRules>? authorizationByTypeName = null,
        CancellationToken ct = default)
    {
        foreach (var descriptor in registry.All)
        {
            var typeDesc = BuildTypeDescriptor(descriptor);
            if (authorizationByTypeName is not null &&
                authorizationByTypeName.TryGetValue(descriptor.EntityName, out var authorization))
            {
                typeDesc.Authorization = authorization;
            }
            try
            {
                var response = await mapping.RegisterSchemaAsync(
                    new SchemaRequest
                    {
                        RootType = typeDesc,
                        TraceId  = Activity.Current?.TraceId.ToString() ?? string.Empty
                    },
                    cancellationToken: ct);

                logger.LogInformation("Schema registered: {Types}",
                    string.Join(", ", response.Registered));
            }
            catch (RpcException ex)
            {
                logger.LogError(ex, "Failed to register schema for {Type}", descriptor.EntityName);
                throw;
            }
        }
    }

    private static TypeDescriptor BuildTypeDescriptor(EntityDescriptor descriptor)
    {
        var typeDesc = new TypeDescriptor { TypeName = descriptor.EntityName };
        var navProps = descriptor.Relations
            .Select(r => r.Property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        typeDesc.Properties.Add(BuildKeyDescriptor(descriptor.KeyProperty));

        foreach (var prop in descriptor.EntityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop == descriptor.KeyProperty) continue;
            if (navProps.Contains(prop.Name)) continue;

            var propDescriptor = TryBuildPropertyDescriptor(prop);
            if (propDescriptor is not null) typeDesc.Properties.Add(propDescriptor);
        }

        foreach (var relation in descriptor.Relations)
        {
            var fk = relation.ForeignKey ?? InferForeignKey(relation, descriptor.EntityName);
            typeDesc.Relations.Add(
                new Contracts.RelationDescriptor
                {
                    PropertyName = relation.Property.Name,
                    Kind         = ToProtoKind(relation.Kind),
                    RelatedType  = relation.RelatedType.Name,
                    ForeignKey   = fk ?? string.Empty
                });
        }

        return typeDesc;
    }

    private static PropertyDescriptor BuildKeyDescriptor(PropertyInfo prop)
    {
        var (clrType, isArray, _, _) = DetectType(prop.PropertyType);
        var descriptor = new PropertyDescriptor
        {
            Name       = prop.Name,
            ClrType    = clrType,
            IsKey      = true,
            IsNullable = false,
            IsArray    = isArray
        };
        AddAnnotations(descriptor, prop);
        return descriptor;
    }

    private static PropertyDescriptor? TryBuildPropertyDescriptor(PropertyInfo prop)
    {
        var (clrType, isArray, isNullable, ok) = DetectType(prop.PropertyType);
        if (!ok) return null;

        var descriptor = new PropertyDescriptor
        {
            Name       = prop.Name,
            ClrType    = clrType,
            IsKey      = false,
            IsNullable = isNullable,
            IsArray    = isArray
        };
        AddAnnotations(descriptor, prop);
        return descriptor;
    }

    private static void AddAnnotations(PropertyDescriptor descriptor, PropertyInfo prop)
    {
        if (prop.GetCustomAttribute<IversonEmbeddingAttribute>() is not null)
        {
            descriptor.IsEmbedding = true;
            descriptor.VectorDim   = 0;
            descriptor.ModelId     = string.Empty;
        }

        if (prop.GetCustomAttribute<IversonChunkAttribute>() is { } chunk)
        {
            descriptor.IsChunk        = true;
            descriptor.ChunkMaxTokens = chunk.MaxTokens;
            descriptor.ChunkOverlap   = chunk.Overlap;
            descriptor.ChunkModelId   = string.Empty;
            descriptor.ChunkVectorDim = 0;
        }

        if (prop.GetCustomAttribute<IversonSearchKeyAttribute>() is { } sk)
        {
            descriptor.IsSearchKey    = true;
            descriptor.SearchKeyOrder = sk.Order;
        }

        if (prop.GetCustomAttribute<IversonLargeFieldAttribute>() is not null)
            descriptor.IsLargeField = true;
    }

    // Returns (clrType, isArray, isNullable, isSupported)
    private static (ClrType, bool, bool, bool) DetectType(Type type)
    {
        var isNullable = !type.IsValueType;

        if (Nullable.GetUnderlyingType(type) is { } nn)
        {
            type       = nn;
            isNullable = true;
        }

        // byte[] is a primitive scalar — check before the generic array unwrap
        if (type == typeof(byte[]))
            return (ClrType.ClrBytes, false, isNullable, true);

        var isArray = false;
        if (type.IsArray)
        {
            type    = type.GetElementType()!;
            isArray = true;
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            type    = type.GetGenericArguments()[0];
            isArray = true;
        }

        if (type == typeof(string))          return (ClrType.ClrString,   isArray, isNullable, true);
        if (type == typeof(Guid))            return (ClrType.ClrGuid,     isArray, false,       true);
        if (type == typeof(int))             return (ClrType.ClrInt32,    isArray, false,       true);
        if (type == typeof(long))            return (ClrType.ClrInt64,    isArray, false,       true);
        if (type == typeof(float))           return (ClrType.ClrFloat,    isArray, false,       true);
        if (type == typeof(double))          return (ClrType.ClrDouble,   isArray, false,       true);
        if (type == typeof(bool))            return (ClrType.ClrBool,     isArray, false,       true);
        if (type == typeof(DateTime))        return (ClrType.ClrDatetime, isArray, false,       true);
        if (type == typeof(DateTimeOffset))  return (ClrType.ClrDatetime, isArray, false,       true);

        return (default, false, false, false); // unsupported — skip
    }

    private static string? InferForeignKey(RelationDescriptor relation, string thisEntityName) =>
        relation.Kind switch
        {
            RelationKind.ManyToOne  => $"{relation.RelatedType.Name}Id",
            RelationKind.OneToOne   => $"{relation.RelatedType.Name}Id",
            RelationKind.ManyToMany => $"{relation.RelatedType.Name}Ids",
            RelationKind.OneToMany  => $"{thisEntityName}Id",
            _                       => null
        };

    private static Contracts.RelationKind ToProtoKind(RelationKind kind) => kind switch
    {
        RelationKind.OneToOne   => Contracts.RelationKind.OneToOne,
        RelationKind.OneToMany  => Contracts.RelationKind.OneToMany,
        RelationKind.ManyToOne  => Contracts.RelationKind.ManyToOne,
        RelationKind.ManyToMany => Contracts.RelationKind.ManyToMany,
        _                       => Contracts.RelationKind.OneToOne
    };
}
