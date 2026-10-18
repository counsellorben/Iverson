using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// Whether a configured popularity signal may be computed and ranked on (CSR round 12 #8). The
/// signal counts CHILD rows and writes the count onto the PARENT's Qdrant point. Every caller who
/// can read the parent sees the ranking move with it, so the signal is eligible only when every
/// such caller could read each counted child row, including the two columns the count is built
/// from.
///
/// Stated in RowFieldAuthorizationEvaluator's read semantics, and statically: access only grows
/// with a caller's groups, so it is enough to check each role that reads the parent. Rows are
/// tenant-scoped on both sides, and so is the count, so tenancy adds nothing.
///
/// Called by PopularitySignalUpdater (the consumer and the sweep), by ObjectSearchGrpcService when
/// ranking, and by PopularitySignalValidator.ValidateAtStartup, which logs each ineligible signal.
/// </summary>
internal static class PopularitySignalEligibility
{
    internal static bool IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaRegistry registry) =>
        IsEligible(parent, relation, registry.Get(relation.RelatedTypeName));

    internal static bool IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaDescriptor? child)
    {
        // Only a OneToMany relation has child rows to count, and an unregistered child has no read
        // rules to judge them by.
        if (relation.Kind != RelationKind.OneToMany || child is null)
            return false;

        // Nobody can read a parent with no Authorization, so its ranking reveals nothing to anyone.
        if (parent.Authorization is not { } parentRules)
            return true;

        // An owner-scoped parent is read by any caller with a `sub` (each sees their own rows, with no
        // role needed), while the count covers every caller's child rows.
        if (parentRules.OwnerField is not null)
            return false;

        // Nobody can read a child with no Authorization.
        if (child.Authorization is not { } childRules)
            return false;

        // So every parent reader holds a CanReadAll role on the parent, and each such role must read
        // every child row too. Roles compare ordinally, as the evaluator matches the `groups` claim.
        var parentReaders = Readers(parentRules);
        if (!parentReaders.IsSubsetOf(Readers(childRules)))
            return false;

        // The count filters on the foreign key and the recency series reads PopularitySignalColumn,
        // so a FieldPermission that hides either must still admit every parent reader. Field names
        // compare case-insensitively, and an empty ReadableRoles restricts nothing.
        return childRules.FieldPermissions
            .Where(fp => fp.ReadableRoles.Count > 0 &&
                         (string.Equals(fp.FieldName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(fp.FieldName, child.PopularitySignalColumn, StringComparison.OrdinalIgnoreCase)))
            .All(fp => parentReaders.IsSubsetOf(fp.ReadableRoles));
    }

    private static HashSet<string> Readers(AuthorizationRules rules) =>
        rules.RowPermissions.Where(rp => rp.CanReadAll).Select(rp => rp.Role).ToHashSet(StringComparer.Ordinal);
}
