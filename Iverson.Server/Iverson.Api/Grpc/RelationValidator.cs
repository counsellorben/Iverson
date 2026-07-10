using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

public interface IRelationValidator
{
    void ValidateRelations(Struct payload, SchemaDescriptor schema);
}

public sealed class RelationValidator(SchemaRegistry registry) : IRelationValidator
{
    public void ValidateRelations(Struct payload, SchemaDescriptor schema)
    {
        var errors = new List<string>();

        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case RelationKind.ManyToOne:
                case RelationKind.OneToOne:
                    ValidateSingleRelation(payload, relation, schema, errors);
                    break;

                case RelationKind.ManyToMany:
                    ValidateCollectionRelation(payload, relation, errors);
                    break;

                case RelationKind.OneToMany:
                    break; // FK lives on the related entity

                default:
                    throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                        $"Unhandled {nameof(RelationKind)} value in relation validation — add a case above.");
            }
        }

        if (errors.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Join(" | ", errors)));
    }

    private void ValidateSingleRelation(
        Struct payload, RelationDescriptor relation, SchemaDescriptor schema, List<string> errors)
    {
        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        if (fkValue is not null)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var g) || g == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
            return;
        }

        var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
        if (navValue?.StructValue is { } nested)
        {
            ValidateNestedObject(nested, relation.PropertyName, relation.RelatedTypeName, errors);
            return;
        }

        var fkCol = schema.ScalarColumns.FirstOrDefault(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

        if (fkCol is null || !fkCol.IsNullable)
            errors.Add(
                $"Relation '{relation.PropertyName}' ({relation.Kind}) is required. " +
                $"Provide '{relation.ForeignKey}' (GUID reference) or " +
                $"'{relation.PropertyName}' (embedded object).");
    }

    private void ValidateCollectionRelation(
        Struct payload, RelationDescriptor relation, List<string> errors)
    {
        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        if (fkValue?.ListValue is { } fkList)
        {
            for (var i = 0; i < fkList.Values.Count; i++)
            {
                var str = fkList.Values[i].StringValue;
                if (!Guid.TryParse(str, out var g) || g == Guid.Empty)
                    errors.Add($"'{relation.ForeignKey}[{i}]': invalid GUID '{str}'.");
            }
            return;
        }

        var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
        if (navValue?.ListValue is { } navList)
        {
            for (var i = 0; i < navList.Values.Count; i++)
            {
                var item = navList.Values[i].StructValue;
                if (item is null)
                {
                    errors.Add($"'{relation.PropertyName}[{i}]': expected an object, got a scalar.");
                    continue;
                }
                ValidateNestedObject(item, $"{relation.PropertyName}[{i}]", relation.RelatedTypeName, errors);
            }
        }
        // empty collection is valid
    }

    private void ValidateNestedObject(Struct nested, string path, string relatedTypeName, List<string> errors)
    {
        var relatedSchema  = registry.Get(relatedTypeName);
        var keyColumnName  = relatedSchema?.KeyColumn.Name ?? "Id";
        var nestedKeyValue = StructFieldAccess.GetFieldValue(nested, keyColumnName);
        var nestedKey      = nestedKeyValue?.StringValue;

        var isExistingEntity = !string.IsNullOrWhiteSpace(nestedKey)
                            && nestedKey != Guid.Empty.ToString()
                            && Guid.TryParse(nestedKey, out _);

        if (isExistingEntity && nested.Fields.Count > 1)
            errors.Add(
                $"'{path}': existing entity (key='{nestedKey}') must only include " +
                $"the key field '{keyColumnName}' — remove extra properties.");
    }
}
