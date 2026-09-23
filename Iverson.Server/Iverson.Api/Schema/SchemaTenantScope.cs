using System.Security.Claims;

namespace Iverson.Api.Schema;

/// <summary>
/// Whether a registered schema belongs to a tenant other than the caller's, and so must not exist
/// as far as that caller is concerned.
/// <para>
/// This is main's cross-tenant predicate from <c>GetSchema</c> (<c>6509b6be</c>), verbatim: a schema
/// with an <see cref="SchemaDescriptor.OwnerTenantId"/> belongs to the caller only when the caller's
/// <c>tenant_id</c> claim matches it, and a schema with no owner is visible to every caller. It lives
/// here once so the schema catalog and both admin-console endpoints cannot drift from it.
/// </para>
/// <para>
/// <b>A foreign schema is absent, never denied or counted.</b> A count of the types a caller may not
/// see is itself a cross-tenant existence oracle once it includes other tenants' types — the same
/// reason main's <c>677d85aa</c> (CSR round 9 #5) removed a denied-vs-absent distinction.
/// </para>
/// </summary>
public static class SchemaTenantScope
{
    public static bool IsForeignTo(this SchemaDescriptor schema, ClaimsPrincipal? caller) =>
        schema.OwnerTenantId is not null
        && schema.OwnerTenantId != caller?.FindFirst("tenant_id")?.Value;
}
