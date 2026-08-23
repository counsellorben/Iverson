using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S6 <c>query</c>'s subject type. Every one of the five drivers declares the same type name and
/// shape; only the .NET driver ever registers it (register-once rule, as for S4's
/// <c>SharedAuthor</c>), and every driver writes one row into it and then queries it.
///
/// Deliberately relation-free: the scenario's exact result-set comparison is over row keys, and a
/// relation would drag hydration into what a search returns without adding anything the QRY axis
/// asserts. <c>Marker</c> carries the run's <c>--id-prefix</c> and is the property every driver
/// filters on — unique per run, so the expected result set is exactly this run's rows.
/// </summary>
[IversonEntity]
public class QueryDoc
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Marker { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
