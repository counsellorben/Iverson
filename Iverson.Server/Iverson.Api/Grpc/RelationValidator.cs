using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

public interface IRelationValidator
{
    void ValidateAndNormalizeRelations(Struct payload, SchemaDescriptor schema);
}

public sealed class RelationValidator(SchemaRegistry registry) : IRelationValidator
{
    public void ValidateAndNormalizeRelations(Struct payload, SchemaDescriptor schema)
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
                    throw new ArgumentOutOfRangeException(
                        nameof(relation.Kind),
                        relation.Kind,
                        $"Unhandled {nameof(RelationKind)} value in relation validation — add a case above.");
            }
        }

        if (errors.Count > 0)
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    string.Join(" | ", errors)));
    }

    private void ValidateSingleRelation(
        Struct payload,
        RelationDescriptor relation,
        SchemaDescriptor schema,
        List<string> errors)
    {
        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        // A NullValue FK counts as ABSENT. The .NET client serializes every property, so a null
        // nullable FK arrives as `authorId: null`; treating that as present made it fail GUID
        // validation (a nullable FK the validator explicitly intends to be omittable) and made
        // the embedded-object branch unreachable from that client entirely.
        if (fkValue is not null && fkValue.KindCase != Value.KindOneofCase.NullValue)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var g) || g == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
            return;
        }

        var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
        if (navValue?.StructValue is { } nested)
        {
            var nestedKey = ValidateNestedObject(
                nested, relation.PropertyName, relation.RelatedTypeName, relation.ForeignKey, errors);
            if (nestedKey is not null)
                StructFieldAccess.SetField(payload, relation.ForeignKey, Value.ForString(nestedKey));
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
            var keys = new List<Value>(navList.Values.Count);
            var allResolved = true;

            for (var i = 0; i < navList.Values.Count; i++)
            {
                var item = navList.Values[i].StructValue;
                if (item is null)
                {
                    errors.Add($"'{relation.PropertyName}[{i}]': expected an object, got a scalar.");
                    allResolved = false;
                    continue;
                }

                var key = ValidateNestedObject(
                    item, $"{relation.PropertyName}[{i}]", relation.RelatedTypeName,
                    relation.ForeignKey, errors);
                if (key is null)
                    allResolved = false;
                else
                    keys.Add(Value.ForString(key));
            }

            if (allResolved)
                StructFieldAccess.SetField(payload, relation.ForeignKey, Value.ForList(keys.ToArray()));
        }
        // empty collection is valid
    }

    /// <returns>
    /// The nested entity's key when it is a valid existing-entity reference, or null when it is
    /// not — in which case an error has been recorded. Callers use the returned key to normalize
    /// the reference into the FK column.
    /// </returns>
    private string? ValidateNestedObject(
        Struct nested, string path, string relatedTypeName, string foreignKey, List<string> errors)
    {
        var relatedSchema  = registry.Get(relatedTypeName);
        var keyColumnName  = relatedSchema?.KeyColumn.Name ?? "Id";
        var nestedKeyValue = StructFieldAccess.GetFieldValue(nested, keyColumnName);
        var nestedKey      = nestedKeyValue?.StringValue;

        var isExistingEntity = !string.IsNullOrWhiteSpace(nestedKey)
                            && nestedKey != Guid.Empty.ToString()
                            && Guid.TryParse(nestedKey, out _);

        // Previously a keyless nested object passed silently and the FK was never populated, so
        // the row was written with a NULL FK. Cascade-inserting the related entity is out of
        // scope, so this is an explicit error instead.
        if (!isExistingEntity)
        {
            errors.Add(
                $"'{path}': embedded new entities are not supported — create the related " +
                $"{relatedTypeName} first, then reference it by '{foreignKey}' (GUID) or by an " +
                $"embedded object containing only '{keyColumnName}'.");
            return null;
        }

        // Only *meaningful* siblings count as extra properties. The .NET client serializes every
        // property, so `new Author { Id = id }` arrives as the key plus a null for each unset
        // property — the same fact that forces the NullValue-FK-means-absent rule above. A key
        // plus only nulls is still a reference, so nothing is cascade-inserted by accepting it.
        var keyNames = StructFieldAccess.Candidates(keyColumnName).ToHashSet(StringComparer.Ordinal);
        var extras = nested.Fields
            .Where(f => !keyNames.Contains(f.Key) && f.Value.KindCase != Value.KindOneofCase.NullValue)
            .Select(f => f.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (extras.Count > 0)
        {
            errors.Add(
                $"'{path}': existing entity (key='{nestedKey}') must only include " +
                $"the key field '{keyColumnName}' — remove extra properties " +
                $"({string.Join(", ", extras.Select(n => $"'{n}'"))}).");
            return null;
        }

        return nestedKey;
    }
}
