using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S9 <c>error-contract</c>'s subject type. Every one of the five drivers declares the same type
/// name and shape; only the .NET driver ever registers it (register-once rule, as for S8's
/// <c>IdentityDoc</c>), and every driver seeds one row into it, reads that row back as a positive
/// control, and then reads a key no row exists under.
///
/// Deliberately relation-free and search-free: the axis is about what the server's two error shapes
/// look like when they reach a caller, and a relation or a vector field would only add ways for the
/// scenario to go red for reasons that are not about the error contract.
/// </summary>
[IversonEntity]
public class ErrorDoc
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
