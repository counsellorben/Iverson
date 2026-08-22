using System.Security.Claims;
using Iverson.Api.Schema;

namespace Iverson.Api.Authorization;

public sealed class RowFieldAuthorizationEvaluator : IRowFieldAuthorizationEvaluator
{
    public AuthorizationDecision Evaluate(SchemaDescriptor schema, ClaimsPrincipal? actingUser, AuthorizationAction action)
    {
        var rules = schema.Authorization;
        if (rules is null)
            return new AuthorizationDecision(true, false, null, null, null, null, null);

        if (actingUser is null)
            return new AuthorizationDecision(true, false, null, null, null, null, null);

        // Tenant is strictly additive: all non-denied paths must have a tenant_id claim and the schema must have a tenant column
        if (string.IsNullOrEmpty(schema.TenantColumn))
            return new AuthorizationDecision(true, false, null, null, null, null, null);
        var tenantId = actingUser.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrEmpty(tenantId))
            return new AuthorizationDecision(true, false, null, null, null, null, null);

        var userGroups = actingUser.FindAll("groups").Select(c => c.Value).ToHashSet();
        var bypass = rules.RowPermissions.Any(p => userGroups.Contains(p.Role) && action switch
        {
            AuthorizationAction.Read   => p.CanReadAll,
            AuthorizationAction.Write  => p.CanWriteAll,
            AuthorizationAction.Delete => p.CanDeleteAll,
            _ => false
        });

        bool ownershipRequired;
        string? ownerFieldName = null, ownerValue = null;

        if (bypass)
        {
            ownershipRequired = false;
        }
        else if (!string.IsNullOrEmpty(rules.OwnerField))
        {
            var sub = actingUser.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub))
                return new AuthorizationDecision(true, false, null, null, null, null, null);

            ownershipRequired = true;
            ownerFieldName = rules.OwnerField;
            ownerValue = sub;
        }
        else
        {
            return new AuthorizationDecision(true, false, null, null, null, null, null);
        }

        IReadOnlySet<string>? allowedFields = null;
        if (action != AuthorizationAction.Delete && rules.FieldPermissions.Count > 0)
        {
            var excluded = rules.FieldPermissions
                .Where(fp =>
                {
                    var roles = action == AuthorizationAction.Read ? fp.ReadableRoles : fp.WritableRoles;
                    return roles.Count > 0 && !roles.Any(userGroups.Contains);
                })
                .Select(fp => fp.FieldName)
                .Where(f => !string.Equals(f, schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
                // Case-insensitive to match the key-column filter above: a FieldPermission naming
                // `authorId` must exclude the column `AuthorId`. Ordinal comparison let a
                // case-mismatched permission silently protect nothing.
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (excluded.Count > 0)
            {
                var allFields = new[] { schema.KeyColumn.Name }
                    // ScalarColumns position: EXCLUDE __TenantId. AllowedFields is the set of fields
                    // a caller may read or write; the tenant column is neither permissionable nor
                    // client-addressable, and the tenant boundary is enforced separately (and
                    // unconditionally) by TenantColumn/TenantValue on the decision.
                    .Concat(schema.ScalarColumns
                        .Where(c => !SchemaDescriptor.IsTenantColumn(c.Name))
                        .Select(c => c.Name))
                    .Concat(schema.FkColumns.Select(fk => fk.ColumnName))
                    .Concat(schema.VectorFields.Select(v => v.PropertyName))
                    .Concat(schema.ChunkFields.Select(c => c.PropertyName));

                // A relation property is writable exactly when its FK column is writable: writing
                // `Author` IS writing `AuthorId`, so one permission governs one concept. Nothing
                // is normalized any more — RelationValidator rejects a nav property outright — but
                // this carve-out still matters: without it, a caller sending `Author` while
                // `AuthorId` is excluded would fail here with an opaque authorization error instead
                // of reaching the validator's clearer nav-property rejection. A caller whose
                // `AuthorId` is excluded still fails at authorization, which remains correct.
                //
                // OneToMany is the carve-out: its FK lives on the RELATED entity, so there is no
                // local column to gate on. Permitted unconditionally — inert on write (the
                // validator skips the kind) and injected on read after masking.
                //
                // Write actions only: AllowedFields also drives search filter/sort/vector
                // authorization, which evaluates with Read; relation names have no meaning there
                // and admitting them would widen search permissions.
                if (action == AuthorizationAction.Write)
                    allFields = allFields.Concat(schema.Relations
                        .Where(r => r.Kind == RelationKind.OneToMany || !excluded.Contains(r.ForeignKey))
                        .Select(r => r.PropertyName));

                allowedFields = allFields.Where(f => !excluded.Contains(f)).ToHashSet();
            }
        }

        return new AuthorizationDecision(false, ownershipRequired, ownerFieldName, ownerValue, allowedFields,
            schema.TenantColumn, tenantId);
    }
}
