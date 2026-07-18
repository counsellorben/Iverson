using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Xunit;

namespace Iverson.Api.Tests.Authorization;

public class RowFieldAuthorizationEvaluatorTests
{
    private readonly IRowFieldAuthorizationEvaluator _evaluator = new RowFieldAuthorizationEvaluator();

    private static ClaimsPrincipal ActingUser(string sub, params string[] groups)
    {
        return ActingUserWithTenant(sub, "test-tenant", groups);  // Default tenant_id
    }

    private static ClaimsPrincipal ActingUserWithTenant(string sub, string? tenantId, params string[] groups)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (tenantId is not null)
            claims.Add(new("tenant_id", tenantId));
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static SchemaDescriptor SchemaWithAuthorization(AuthorizationRules? authorization = null, string? tenantColumn = "tenant_id")  // Default tenantColumn
    {
        return new SchemaDescriptor
        {
            TypeName = "TestType",
            TableName = "test_table",
            KeyColumn = new ColumnDescriptor("Id", "INT", false),
            ScalarColumns = new List<ColumnDescriptor>
            {
                new("Name", "VARCHAR(255)", false),
                new("OwnerId", "VARCHAR(255)", false)
            },
            FkColumns = [],
            VectorFields = [],
            ChunkFields = [],
            Relations = [],
            Authorization = authorization,
            TenantColumn = tenantColumn
        };
    }

    [Fact]
    public void Evaluate_NoAuthorizationRules_ReturnsDenied()
    {
        var schema = SchemaWithAuthorization(null);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoIdentity_ReturnsDenied()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>(),
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);

        var result = _evaluator.Evaluate(schema, null, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_OwnershipRequiredWithMatchingSub_NotDeniedWithOwnershipRequired()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>(),
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeTrue();
        result.OwnerFieldName.Should().Be("OwnerId");
        result.OwnerValue.Should().Be("user123");
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RoleBypassRead_NotDeniedWithoutOwnershipRequired()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RoleBypassWrite_NotDeniedWithoutOwnershipRequired()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: false, CanWriteAll: true, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Write);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RoleBypassDelete_NotDeniedWithoutOwnershipRequired()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: false, CanWriteAll: false, CanDeleteAll: true)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Delete);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RolePresentButNoBypassAndNoOwnerField_ReturnsDenied()
    {
        var rules = new AuthorizationRules(
            null,
            new List<RowPermission>
            {
                new("admin", CanReadAll: false, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RolePresentButNoBypassWithOwnerField_FallsToOwnership()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: false, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeTrue();
        result.OwnerFieldName.Should().Be("OwnerId");
        result.OwnerValue.Should().Be("user123");
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FieldLevelNonMatchingRoleExcludesField()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string> { "premium" }, new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeFalse();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().Contain("Id");
        result.AllowedFields.Should().Contain("OwnerId");
        result.AllowedFields.Should().NotContain("Name");
    }

    [Fact]
    public void Evaluate_FieldLevelEmptyReadRolesList_NeverExcludes()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string>(), new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FieldLevelNoEntryForField_NeverExcludes()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string> { "premium" }, new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().Contain("OwnerId");
    }

    [Fact]
    public void Evaluate_FieldLevelNothingExcluded_CollapsesAllowedFieldsToNull()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string> { "admin" }, new List<string>()),
                new("OwnerId", new List<string>(), new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DeleteActionIgnoresFieldPermissions()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: false, CanWriteAll: false, CanDeleteAll: true)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string> { "premium" }, new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Delete);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_MultipleFieldsWithMixedRoles_ExcludesOnlyNonMatching()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("viewer", CanReadAll: false, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string> { "premium" }, new List<string>()),
                new("OwnerId", new List<string> { "viewer" }, new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "viewer");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeTrue();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().Contain("Id");
        result.AllowedFields.Should().Contain("OwnerId");
        result.AllowedFields.Should().NotContain("Name");
    }

    [Fact]
    public void Evaluate_FieldLevelExclusion_IncludesFkVectorAndChunkColumnsInAllowedFields()
    {
        var schema = new SchemaDescriptor
        {
            TypeName = "TestType",
            TableName = "test_table",
            KeyColumn = new ColumnDescriptor("Id", "INT", false),
            ScalarColumns = new List<ColumnDescriptor>
            {
                new("Name", "VARCHAR(255)", false),
                new("OwnerId", "VARCHAR(255)", false)
            },
            FkColumns = new List<ForeignKeyDescriptor> { new("AuthorId", "Author") },
            VectorFields = new List<VectorDescriptor> { new("Title", 768, "test-model") },
            ChunkFields = new List<ChunkDescriptor> { new("Body", 512, 64, "test-model", 768) },
            Relations = [],
            Authorization = new AuthorizationRules(
                "OwnerId",
                new List<RowPermission>
                {
                    new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
                },
                new List<FieldPermission>
                {
                    new("Name", new List<string> { "premium" }, new List<string>())
                }),
            TenantColumn = "tenant_id"  // Default tenant column to match evaluator requirements
        };
        var user = ActingUser("user123", "admin");  // Uses default tenant_id from ActingUser()

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().Contain("AuthorId");
        result.AllowedFields.Should().Contain("Title");
        result.AllowedFields.Should().Contain("Body");
        result.AllowedFields.Should().NotContain("Name");
    }

    [Fact]
    public void Evaluate_WriteActionUsesWritableRolesNotReadableRoles()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Name", new List<string>(), new List<string> { "editor" })
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Write);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeTrue();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().NotContain("Name");
    }

    [Fact]
    public void Evaluate_OwnershipRequiredWithNoSubClaim_ReturnsDenied()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>(),
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new("groups", "admin") }, "test"));

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.OwnershipRequired.Should().BeFalse();
        result.OwnerFieldName.Should().BeNull();
        result.OwnerValue.Should().BeNull();
        result.AllowedFields.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FieldLevelExclusionTargetingKeyColumn_NeverExcludesKeyColumn()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>
            {
                new("Id", new List<string> { "superadmin" }, new List<string>()),
                new("Name", new List<string> { "superadmin" }, new List<string>())
            });
        var schema = SchemaWithAuthorization(rules);
        var user = ActingUser("user123", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields.Should().Contain("Id");
        result.AllowedFields.Should().NotContain("Name");
    }

    [Fact]
    public void Evaluate_TenantPresentWithCanReadAllRole_CarriesTenantColumnAndValue()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules, "TenantId");
        var user = ActingUserWithTenant("user123", "tenant-abc", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.TenantColumn.Should().Be("TenantId");
        result.TenantValue.Should().Be("tenant-abc");
    }

    [Fact]
    public void Evaluate_NoTenantIdClaim_ReturnsDenied()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>(),
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules, "TenantId");
        var user = ActingUserWithTenant("user123", null);  // Explicitly no tenant_id claim

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.TenantColumn.Should().BeNull();
        result.TenantValue.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SchemaTenantColumnNull_ReturnsDenied()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>(),
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules, null);
        var user = ActingUserWithTenant("user123", "tenant-abc");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeTrue();
        result.TenantColumn.Should().BeNull();
        result.TenantValue.Should().BeNull();
    }

    [Fact]
    public void Evaluate_BypassRoleStillCarriesTenantBoundary()
    {
        var rules = new AuthorizationRules(
            "OwnerId",
            new List<RowPermission>
            {
                new("admin", CanReadAll: true, CanWriteAll: false, CanDeleteAll: false)
            },
            new List<FieldPermission>());
        var schema = SchemaWithAuthorization(rules, "TenantId");
        var user = ActingUserWithTenant("user123", "tenant-xyz", "admin");

        var result = _evaluator.Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.OwnershipRequired.Should().BeFalse();
        result.TenantColumn.Should().Be("TenantId");
        result.TenantValue.Should().Be("tenant-xyz");
    }
}
