using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkArticle
{
    [IversonKey]          public Guid            Id              { get; set; }
    public string                                Title           { get; set; } = "";
    [IversonLargeField]   public string          Body            { get; set; } = "";
    public Guid                                  BenchmarkUserId { get; set; }
    [IversonSearchKey(0)] public string          Category        { get; set; } = "";
    public int                                   WordCount       { get; set; }
    [IversonSearchKey(1)] public DateTimeOffset  PublishedAt     { get; set; }

    [ManyToOne(typeof(BenchmarkUser))]
    public BenchmarkUser? Author { get; set; }
}
