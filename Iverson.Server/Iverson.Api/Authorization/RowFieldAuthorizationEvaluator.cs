using System.Security.Claims;
using Iverson.Api.Schema;

namespace Iverson.Api.Authorization;

public sealed class RowFieldAuthorizationEvaluator : IRowFieldAuthorizationEvaluator
{
    public AuthorizationDecision Evaluate(SchemaDescriptor schema, ClaimsPrincipal? actingUser, AuthorizationAction action)
    {
        var rules = schema.Authorization;
        if (rules is null)
            return new AuthorizationDecision(true, false, null, null, null);

        if (actingUser is null)
            return new AuthorizationDecision(true, false, null, null, null);

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
            ownershipRequired = true;
            ownerFieldName = rules.OwnerField;
            ownerValue = actingUser.FindFirst("sub")?.Value;
        }
        else
        {
            return new AuthorizationDecision(true, false, null, null, null);
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
                .ToHashSet();

            if (excluded.Count > 0)
            {
                var allFields = new[] { schema.KeyColumn.Name }
                    .Concat(schema.ScalarColumns.Select(c => c.Name))
                    .Concat(schema.FkColumns.Select(fk => fk.ColumnName))
                    .Concat(schema.VectorFields.Select(v => v.PropertyName))
                    .Concat(schema.ChunkFields.Select(c => c.PropertyName));
                allowedFields = allFields.Where(f => !excluded.Contains(f)).ToHashSet();
            }
        }

        return new AuthorizationDecision(false, ownershipRequired, ownerFieldName, ownerValue, allowedFields);
    }
}
