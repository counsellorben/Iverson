using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S4 <c>interop</c>'s "one" side. Registered only once, by the .NET driver — see
/// <c>Scenarios/InteropScenario.cs</c> for why five registrations of the same type name would
/// silently overwrite one another.
/// </summary>
[IversonEntity]
public class SharedAuthor
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
