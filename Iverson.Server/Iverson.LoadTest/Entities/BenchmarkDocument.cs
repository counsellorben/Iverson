using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkDocument
{
    [IversonKey] public Guid Id { get; set; }

    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";

    // Both annotations, deliberately: the chunk field and the vector field sharing one
    // property name is what makes centroidPossible true on the server, and therefore what
    // makes the centroid term in the fusion non-degenerate (spec A4).
    [IversonEmbedding]
    [IversonChunk]
    public string Body { get; set; } = "";

    public string OwnerId { get; set; } = "";
}
