using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Sql;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Grpc;

public interface IEntityRelationResolver
{
    Task ResolveRelationsAsync(
        Struct entityStruct,
        SchemaDescriptor schema,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct);
}

public sealed class EntityRelationResolver(
    SchemaRegistry registry,
    IEntityRepository entities,
    IRowFieldAuthorizationEvaluator authEvaluator)
    : IEntityRelationResolver
{
    // CSR finding #6: (TypeName, Key) pairs already expanded along the current traversal — a
    // correctness backstop against a cyclic relation graph (A -> B -> A, which the finding notes
    // is legal in the schema model), independent of and in addition to MaxRelationDepth. The
    // depth cap alone bounds total work per request; this set additionally stops a cycle from
    // re-expanding an entity it has already visited, rather than relying solely on depth running
    // out. Case-insensitive on both TypeName (matching SchemaRegistry's own keying) and Key
    // (matching the OrdinalIgnoreCase key-list handling already used in ResolveManyToManyAsync).
    private sealed class VisitedComparer : IEqualityComparer<(string TypeName, string Key)>
    {
        public bool Equals((string TypeName, string Key) x, (string TypeName, string Key) y) =>
            string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string TypeName, string Key) obj) =>
            HashCode.Combine(
                obj.TypeName.ToUpperInvariant(),
                obj.Key.ToUpperInvariant());
    }

    /// <summary>
    /// Marks (<paramref name="typeName"/>, <paramref name="key"/>) as visited and reports whether
    /// it is safe to recurse into that entity's own relations — false either because the key is
    /// empty (nothing to recurse into) or because this exact entity is already an ancestor in the
    /// current traversal (the cycle-guard firing). The one-hop embed of the entity's own scalar
    /// data still happens regardless — only further expansion is skipped.
    /// </summary>
    private static bool ShouldExpand(
        HashSet<(string TypeName, string Key)> visited, string typeName, string? key) =>
        !string.IsNullOrWhiteSpace(key) && visited.Add((typeName, key));

    public Task ResolveRelationsAsync(
        Struct entityStruct,
        SchemaDescriptor schema,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct)
    {
        var visited = new HashSet<(string TypeName, string Key)>(new VisitedComparer());

        // Seed the root entity itself, so a relation graph that eventually points back to the
        // very entity this call started from (not just to some earlier ancestor) is also caught.
        var rootKey = StructFieldAccess.GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (!string.IsNullOrWhiteSpace(rootKey))
            visited.Add((schema.TypeName, rootKey));

        return ResolveRelationsCoreAsync(entityStruct, schema, depth, actingUser, ct, visited);
    }

    private async Task ResolveRelationsCoreAsync(
        Struct entityStruct,
        SchemaDescriptor schema,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct,
        HashSet<(string TypeName, string Key)> visited)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelationKind.ManyToOne:
                case SchemaRelationKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, actingUser, ct, visited);
                    break;
                case SchemaRelationKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, actingUser, ct, visited);
                    break;
                case SchemaRelationKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, actingUser, ct, visited);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                        $"Unhandled {nameof(SchemaRelationKind)} value in relation resolution — add a case above.");
            }
        }
    }

    private bool TryAuthorizeAndMask(Struct relatedStruct, AuthorizationDecision decision)
    {
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue) ||
            (decision.TenantColumn is not null &&
             StructFieldAccess.GetFieldString(relatedStruct, decision.TenantColumn) != decision.TenantValue))
            return false;
        AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);
        return true;
    }

    private async Task ResolveSingleRelationAsync(
        Struct entityStruct,
        SchemaRelationDescriptor relation,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct,
        HashSet<(string TypeName, string Key)> visited)
    {
        var fkValue = StructFieldAccess.GetFieldString(entityStruct, relation.ForeignKey);
        if (string.IsNullOrWhiteSpace(fkValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rowJson = await entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(relatedSchema),
            fkValue,
            tenantScoped: true,
            tenantId: actingUser?.FindFirst("tenant_id")?.Value);
        if (rowJson is null) return;

        var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);
        if (!TryAuthorizeAndMask(relatedStruct, decision)) return;

        if (depth > 1 && ShouldExpand(visited, relatedSchema.TypeName, fkValue))
            await ResolveRelationsCoreAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct, visited);

        entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
    }

    private async Task ResolveManyToManyAsync(
        Struct entityStruct,
        SchemaRelationDescriptor relation,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct,
        HashSet<(string TypeName, string Key)> visited)
    {
        var ids = StructFieldAccess.GetFieldStringList(entityStruct, relation.ForeignKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(relatedSchema),
            ids,
            tenantScoped: true,
            tenantId: actingUser?.FindFirst("tenant_id")?.Value);
        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            if (depth > 1 && ShouldExpand(visited, relatedSchema.TypeName, id))
                await ResolveRelationsCoreAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct, visited);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    private async Task ResolveOneToManyAsync(
        Struct entityStruct,
        SchemaDescriptor schema,
        SchemaRelationDescriptor relation,
        int depth,
        ClaimsPrincipal? actingUser,
        CancellationToken ct,
        HashSet<(string TypeName, string Key)> visited)
    {
        var keyValue = StructFieldAccess.GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(keyValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema),
            relation.ForeignKey,
            keyValue,
            tenantScoped: true,
            tenantId: actingUser?.FindFirst("tenant_id")?.Value);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            var relatedKey = StructFieldAccess.GetFieldString(relatedStruct, relatedSchema.KeyColumn.Name);
            if (depth > 1 && ShouldExpand(visited, relatedSchema.TypeName, relatedKey))
                await ResolveRelationsCoreAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct, visited);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }
}
