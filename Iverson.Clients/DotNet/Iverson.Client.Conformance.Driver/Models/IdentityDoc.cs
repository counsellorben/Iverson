using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S8 <c>identity</c>'s subject type. Every one of the five drivers declares the same type name and
/// shape; only the .NET driver ever registers it (register-once rule, as for S6's <c>QueryDoc</c>),
/// and every driver writes one row into it, reads that row back, and then attempts one update
/// under a deliberately wrong acting user.
///
/// Deliberately relation-free and search-free: the axis is about WHOSE identity the server resolves
/// a row's tenant and owner from, and a relation or a vector field would only add ways for the
/// scenario to go red for reasons that are not about identity.
/// </summary>
[IversonEntity]
public class IdentityDoc
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
