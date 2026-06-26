using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkUser
{
    [IversonKey] public Guid   Id    { get; set; }
    public string              Name  { get; set; } = "";
    public string              Email { get; set; } = "";
    public string              Bio   { get; set; } = "";
}
