using System.Security.Claims;

namespace Iverson.Api.Schema;

/// <summary>
/// The by-name type lookup every data RPC makes (CSR round 11 #20). A type is <b>visible</b> to a
/// caller when it is registered and is not <see cref="SchemaTenantScope.IsForeignTo"/> that
/// caller, so an unowned type is visible to everyone.
/// <para>
/// An invisible type, unregistered or owned by another tenant, comes back <c>null</c>. Every call
/// site answers both cases from that one branch, so a type name tells a caller nothing about
/// another tenant. <see cref="SchemaRegistry.Get"/> stays for the server's consumers and workers,
/// which act across tenants on the server's own behalf.
/// </para>
/// </summary>
public static class SchemaVisibility
{
    public static SchemaDescriptor? TryGetVisible(
        this SchemaRegistry registry, string typeName, ClaimsPrincipal? actingUser) =>
        registry.Get(typeName) is { } schema && !schema.IsForeignTo(actingUser) ? schema : null;
}
