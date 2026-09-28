using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S13 <c>match-pattern</c>'s subject type. Every one of the five drivers declares the same type
/// name and shape; only the .NET driver ever registers it (register-once rule, as for S7's
/// <c>VectorDoc</c>), and every driver writes three rows into it and then matches over them.
///
/// Deliberately relation-free and chunk-free: both requests read TYPE_ROWS, and no assertion
/// observes a chunk.
///
/// <list type="bullet">
/// <item><description><c>Marker</c> carries the run's <c>--id-prefix</c> and is the property both
/// requests pre-filter on. It is <c>[IversonMetadata]</c> exactly as on <c>VectorDoc</c>.</description>
/// </item>
/// <item><description><c>Label</c> is the row's per-language identity and the PARTITION BY column,
/// so each language's three rows form one partition. Its spelling (<c>pat-&lt;lang&gt;</c>) must
/// match the orchestrator's <c>MatchPatternScenario</c>.</description></item>
/// <item><description><c>Seq</c> is the ORDER BY column and the value <c>DEFINE B AS Seq &gt;
/// PREV(Seq)</c> compares; an <c>int</c> so it registers as CLR_INT32.</description></item>
/// <item><description><c>Title</c> is the <c>[IversonEmbedding]</c> property the similarity
/// request's <c>SIMILARITY(Title, …)</c> scores.</description></item>
/// </list>
/// </summary>
[IversonEntity]
public class PatternDoc
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    [IversonMetadata] public string Marker { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Seq { get; set; }
    [IversonEmbedding] public string Title { get; set; } = string.Empty;
}
