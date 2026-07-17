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
    Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, ClaimsPrincipal? actingUser, CancellationToken ct);
}

public sealed class EntityRelationResolver(
    SchemaRegistry registry,
    IEntityRepository entities,
    IRowFieldAuthorizationEvaluator authEvaluator)
    : IEntityRelationResolver
{
    public async Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelationKind.ManyToOne:
                case SchemaRelationKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, actingUser, ct);
                    break;
                case SchemaRelationKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, actingUser, ct);
                    break;
                case SchemaRelationKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, actingUser, ct);
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
             StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
            return false;
        AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);
        return true;
    }

    private async Task ResolveSingleRelationAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var fkValue = StructFieldAccess.GetFieldString(entityStruct, relation.ForeignKey);
        if (string.IsNullOrWhiteSpace(fkValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(relatedSchema), fkValue);
        if (rowJson is null) return;

        var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);
        if (!TryAuthorizeAndMask(relatedStruct, decision)) return;

        if (depth > 1)
            await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);

        entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
    }

    private async Task ResolveManyToManyAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var ids = StructFieldAccess.GetFieldStringList(entityStruct, relation.ForeignKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(relatedSchema), ids);
        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    private async Task ResolveOneToManyAsync(
        Struct entityStruct, SchemaDescriptor schema, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var keyValue = StructFieldAccess.GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(keyValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema), relation.ForeignKey, keyValue);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }
}
