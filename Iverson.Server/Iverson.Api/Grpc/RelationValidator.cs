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
            // When PropertyName and ForeignKey collide — Python, TypeScript and Java can all
            // produce that for ManyToMany — the "nav property" and the foreign key are the SAME
            // payload key. There is no separate object to strip, and stripping would delete the
            // foreign key itself.
            var navIsDistinctKey = !string.Equals(
                relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase);

            switch (relation.Kind)
            {
                case RelationKind.ManyToOne:
                case RelationKind.OneToOne:
                    ValidateSingleRelation(payload, relation, schema, navIsDistinctKey, errors);
                    break;

                case RelationKind.ManyToMany:
                    ValidateCollectionRelation(payload, relation, navIsDistinctKey, errors);
                    break;

                case RelationKind.OneToMany:
                    break; // FK lives on the related entity

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(relation.Kind),
                        relation.Kind,
                        $"Unhandled {nameof(RelationKind)} value in relation validation — add a case above.");
            }

            // The stores already ignore nav properties; removing them here keeps them out of the
            // Kafka event body, which is the only place they were still observable.
            if (navIsDistinctKey)
                StructFieldAccess.RemoveField(payload, relation.PropertyName);
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
        bool navIsDistinctKey,
        List<string> errors)
    {
        // Resolved before the nav branch: normalizing an embedded reference writes this column into
        // the payload, so a relation whose FK is not a local column must error rather than have a
        // phantom key injected (the stores silently ignore unknown keys, so it would be a no-op).
        var fkCol = schema.ScalarColumns.FirstOrDefault(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        var navValue = navIsDistinctKey
            ? StructFieldAccess.GetFieldValue(payload, relation.PropertyName)
            : null;
        // A NullValue FK counts as ABSENT. The .NET client serializes every property, so a null
        // nullable FK arrives as `authorId: null`; treating that as present made it fail GUID
        // validation (a nullable FK the validator explicitly intends to be omittable) and made
        // the embedded-object branch unreachable from that client entirely.
        if (fkValue is not null && fkValue.KindCase != Value.KindOneofCase.NullValue)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var fk) || fk == Guid.Empty)
            {
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
                return;
            }

            // Cross-check only. The nav object arrives fully hydrated from the read path, so the
            // key-only rule must not apply; an unreadable key just means no second opinion.
            if (navValue?.StructValue is { } crossCheck
                && ReadNestedKey(crossCheck, relation.RelatedTypeName) is { } navKey
                && navKey != fk)
            {
                errors.Add(
                    $"'{relation.PropertyName}' references '{navKey}' but '{relation.ForeignKey}' " +
                    $"is '{fk}'. Remove one, or make them agree.");
            }

            return;
        }

        if (navValue?.StructValue is { } nested)
        {
            if (fkCol is null)
            {
                errors.Add(
                    $"Relation '{relation.PropertyName}' ({relation.Kind}) cannot be set by embedded " +
                    $"object: '{relation.ForeignKey}' is not a column on this type.");
                return;
            }

            var nestedKey = ValidateNestedObject(
                nested, relation.PropertyName, relation.RelatedTypeName, relation.ForeignKey, errors);
            if (nestedKey is not null)
                StructFieldAccess.SetField(payload, relation.ForeignKey, Value.ForString(nestedKey));
            return;
        }

        if (fkCol is null || !fkCol.IsNullable)
            errors.Add(
                $"Relation '{relation.PropertyName}' ({relation.Kind}) is required. " +
                $"Provide '{relation.ForeignKey}' (GUID reference) or " +
                $"'{relation.PropertyName}' (embedded object).");
    }

    private void ValidateCollectionRelation(
        Struct payload, RelationDescriptor relation, bool navIsDistinctKey, List<string> errors)
    {
        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        var navValue = navIsDistinctKey
            ? StructFieldAccess.GetFieldValue(payload, relation.PropertyName)
            : null;

        if (fkValue?.ListValue is { } fkList)
        {
            var fkKeys     = new HashSet<Guid>();
            var fkAllValid = true;

            for (var i = 0; i < fkList.Values.Count; i++)
            {
                var str = fkList.Values[i].StringValue;
                if (!Guid.TryParse(str, out var key) || key == Guid.Empty)
                {
                    errors.Add($"'{relation.ForeignKey}[{i}]': invalid GUID '{str}'.");
                    fkAllValid = false;
                }
                else
                {
                    fkKeys.Add(key);
                }
            }

            // An empty nav list means "not supplied" and the FK list wins. A non-empty one is a
            // second opinion and must agree exactly.
            if (fkAllValid && navValue?.ListValue is { } crossList && crossList.Values.Count > 0)
            {
                var navKeys = new HashSet<Guid>();
                foreach (var item in crossList.Values)
                    if (item.StructValue is { } nested
                        && ReadNestedKey(nested, relation.RelatedTypeName) is { } key)
                        navKeys.Add(key);

                // No item yielded a readable key, so the nav list offers no second opinion at all.
                // Treat that as silence rather than disagreement, matching ValidateSingleRelation's
                // rule for an unreadable nested key. Reporting "disagree" here would reject a write
                // whose real problem is item shape (or a registry miss in KeyColumnNameFor) with a
                // message naming neither.
                if (navKeys.Count > 0 && !navKeys.SetEquals(fkKeys))
                    errors.Add(
                        $"'{relation.PropertyName}' and '{relation.ForeignKey}' disagree. " +
                        $"Remove one, or make them agree.");
            }

            return;
        }

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

    private string KeyColumnNameFor(string relatedTypeName) =>
        registry.Get(relatedTypeName)?.KeyColumn.Name ?? "Id";

    /// <summary>
    /// Reads a nested object's key as a parsed <see cref="Guid"/>, or null when it carries no
    /// usable one. Records NO error: callers decide what an unusable key means. The normalize
    /// path treats it as an unsupported cascade-insert; conflict detection treats it as "no
    /// second opinion" and lets the foreign key stand.
    /// </summary>
    private Guid? ReadNestedKey(Struct nested, string relatedTypeName)
    {
        var keyValue = StructFieldAccess.GetFieldValue(nested, KeyColumnNameFor(relatedTypeName));
        return Guid.TryParse(keyValue?.StringValue, out var key) && key != Guid.Empty ? key : null;
    }

    /// <returns>
    /// The nested entity's key when it is a valid bare existing-entity reference, or null when it
    /// is not — in which case an error has been recorded. Used by the normalize path only;
    /// conflict detection calls <see cref="ReadNestedKey"/> directly, because a cross-checked nav
    /// property arrives fully hydrated and must not be held to the key-only rule.
    /// </returns>
    private string? ValidateNestedObject(
        Struct nested, string path, string relatedTypeName, string foreignKey, List<string> errors)
    {
        var keyColumnName = KeyColumnNameFor(relatedTypeName);
        var rawKey        = StructFieldAccess.GetFieldValue(nested, keyColumnName)?.StringValue;

        // Previously a keyless nested object passed silently and the FK was never populated, so
        // the row was written with a NULL FK. Cascade-inserting the related entity is out of
        // scope, so this is an explicit error instead.
        if (ReadNestedKey(nested, relatedTypeName) is null)
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
                $"'{path}': existing entity (key='{rawKey}') must only include " +
                $"the key field '{keyColumnName}' — remove extra properties " +
                $"({string.Join(", ", extras.Select(n => $"'{n}'"))}).");
            return null;
        }

        // The RAW spelling, not the parsed form: this value is written into the FK column, and the
        // projection stores keep payload strings verbatim. Canonicalising here would write an FK
        // that no longer matches the related row's key in StarRocks and Qdrant.
        return rawKey;
    }
}
