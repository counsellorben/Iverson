using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S7 <c>vector-search</c>'s subject type. Every one of the five drivers declares the same type
/// name and shape; only the .NET driver ever registers it (register-once rule, as for S6's
/// <c>QueryDoc</c>), and every driver writes one row into it and then searches it.
///
/// Deliberately relation-free, and deliberately without any enrichment annotation
/// (<c>[IversonSummary]</c>, <c>[IversonKeywords]</c>, contextual chunking): the scenario's exact
/// set comparisons must not depend on generative output that differs run to run.
///
/// <list type="bullet">
/// <item><description><c>Marker</c> carries the run's <c>--id-prefix</c> and is the property both
/// queries filter on. It is <c>[IversonMetadata]</c> so that one value scopes BOTH stores: the
/// object collection filters it as an ordinary scalar payload clause, and the chunks collection
/// can filter it only because metadata columns are denormalized onto every chunk point
/// (<c>IntelligenceStoreConsumer</c>). Without the annotation, <c>SearchChunks</c> rejects the
/// clause outright.</description></item>
/// <item><description><c>Title</c> is the <c>[IversonEmbedding]</c> property <c>SearchSimilar</c>
/// searches — embedded whole, one named vector per row.</description></item>
/// <item><description><c>Body</c> is the <c>[IversonChunk]</c> property <c>SearchChunks</c>
/// searches. Short enough on purpose to produce a single window per row: chunk windowing is
/// Deferred in the VEC coverage ledger and no assertion observes a window boundary.</description>
/// </item>
/// <item><description><c>Label</c> is the row's per-language identity. <c>SearchSimilar</c> streams
/// the Qdrant payload, whose row key lives under a reserved <c>key</c> entry no client's typed
/// projection binds to <c>Id</c> — the label is the value the similarity comparison grades on. Its
/// spelling must match <c>VectorSearchScenario.LabelFor</c>.</description></item>
/// </list>
/// </summary>
[IversonEntity]
public class VectorDoc
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    [IversonMetadata] public string Marker { get; set; } = string.Empty;
    [IversonEmbedding] public string Title { get; set; } = string.Empty;
    [IversonChunk(maxTokens: 256, overlap: 32)] public string Body { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
