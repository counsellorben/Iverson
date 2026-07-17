using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkTag
{
    [IversonKey] public Guid   Id       { get; set; }
    public string              Name     { get; set; } = "";
    public string              Category { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string TenantId { get; set; } = "";
}
