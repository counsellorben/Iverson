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
            // A PropertyName/ForeignKey collision — Java and .NET could both produce that for
            // ManyToMany — is rejected outright at registration (SchemaRegistrationOrchestrator),
            // per the IVC-REL-003 ruling: the payload key must be distinct from the nav property
            // name for every relation kind. A schema registered BEFORE this check existed can
            // still carry a collision, though: SchemaRegistry.LoadAsync rehydrates descriptors
            // straight from Postgres JSON and does not route them back through the orchestrator,
            // so such a descriptor CAN still reach this validator in production (SchemaRegistry
            // logs an ERROR for it on load). The message below accounts for that case explicitly
            // rather than assuming it is unreachable.
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

            {
                var navValue = StructFieldAccess.GetFieldValue(payload, relation.PropertyName);
                // A NullValue nav key counts as ABSENT, matching the foreign-key rule below: .NET and
                // Java serialize every property, so an unset nav member arrives as `Author: null`.
                if (navValue is not null && navValue.KindCase != Value.KindOneofCase.NullValue)
                {
                    if (RelationCollisionCheck.IsCollision(relation))
                    {
                        // Telling the caller to "send the foreign key instead" is nonsensical when
                        // the foreign key IS the payload key they just sent under the nav-property
                        // name — that shape means the schema itself is broken (a legacy/rehydrated
                        // descriptor that predates the registration-time collision check), not that
                        // the caller did something wrong.
                        errors.Add(
                            $"Relation '{relation.PropertyName}' on '{schema.TypeName}' has a navigation-" +
                            $"property name identical to its foreign key '{relation.ForeignKey}'. This " +
                            "schema is invalid and must be re-registered with a distinct navigation " +
                            "property name.");
                    }
                    else
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
