using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkAuthor
{
    [IversonKey] public Guid   Id    { get; set; }
    public string              Name  { get; set; } = "";
    public string              Email { get; set; } = "";
    public string              Bio   { get; set; } = "";
    public string OwnerId { get; set; } = "";
}
