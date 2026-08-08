using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

public interface IRelationValidator
{
    void ValidateAndNormalizeRelations(Struct payload, SchemaDescriptor schema);
}

public sealed class RelationValidator : IRelationValidator
{
    public void ValidateAndNormalizeRelations(Struct payload, SchemaDescriptor schema)
    {
        var errors = new List<string>();

        foreach (var relation in schema.Relations)
        {
            // When PropertyName and ForeignKey collide — Python, TypeScript and Java can all
            // produce that for ManyToMany — the "nav property" and the foreign key are the SAME
            // payload key. There is nothing to reject: the payload key IS the foreign key.
            var navIsDistinctKey = !string.Equals(
                relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase);

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

            if (navIsDistinctKey)
            {
                var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
                // A NullValue nav key counts as ABSENT, matching the foreign-key rule below: .NET and
                // Java serialize every property, so an unset nav member arrives as `Author: null`.
                if (navValue is not null && navValue.KindCase != Value.KindOneofCase.NullValue)
                {
                    // A OneToMany carries no key in the payload at all: its foreign key is a column
                    // on the related entity's row, so there is no key to name as the alternative.
                    var remedy = relation.Kind == RelationKind.OneToMany
                        ? $"set '{relation.ForeignKey}' on each related {relation.RelatedTypeName} instead."
                        : $"send '{relation.ForeignKey}' instead.";
                    errors.Add(
                        $"Relation '{relation.PropertyName}' is a navigation property and cannot be " +
                        $"written — {remedy}");
                }
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
        var fkCol = schema.ScalarColumns.FirstOrDefault(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

        var fkValue = StructFieldAccess.GetFieldValue(payload, relation.ForeignKey);
        // A NullValue FK counts as ABSENT. The .NET client serializes every property, so a null
        // nullable FK arrives as `authorId: null`; treating that as present would fail GUID
        // validation for a nullable FK the validator explicitly intends to be omittable.
        if (fkValue is not null && fkValue.KindCase != Value.KindOneofCase.NullValue)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var fk) || fk == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");

            return;
        }

        if (fkCol is null || !fkCol.IsNullable)
            errors.Add(
                $"Relation '{relation.PropertyName}' ({relation.Kind}) is required. " +
                $"Provide '{relation.ForeignKey}' (GUID reference).");
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
                if (!Guid.TryParse(str, out var key) || key == Guid.Empty)
                    errors.Add($"'{relation.ForeignKey}[{i}]': invalid GUID '{str}'.");
            }
        }
        // empty or absent collection is valid
    }
}
