using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Qdrant.Client;

namespace Iverson.Vector;

/// <summary>
/// Read-only view of the Qdrant collections this deployment holds, for the admin console's
/// collection-stats widget.
/// <para>
/// A separate role from <see cref="IVectorSchemaManager"/> on purpose. That interface is the
/// <em>write</em> surface — <c>EnsureCollectionAsync</c> and <c>ApplyCollectionAsync</c> both
/// create collections, which is why <c>/health</c>'s use of it is a write probe. Reporting stats
/// must never be able to create anything, so the console's endpoint takes this interface instead
/// of widening that one.
/// </para>
/// <para>
/// Enumerating every collection is a <b>cross-tenant</b> operation: collection names are
/// tenant-scoped by <see cref="IntelligenceTenantScope.ResolveCollectionName"/>, so the caller's
/// endpoint must be Operator-gated.
/// </para>
/// </summary>
public interface IVectorCollectionReader
{
    Task<VectorCollectionListing> ListCollectionStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// What one enumeration found: the collections whose stats were actually read, and
/// <see cref="ListedCount"/> — how many Qdrant named in the first place.
/// <para>
/// <b>The two are separate on purpose and must not be collapsed to one number.</b> A
/// per-collection stats read can fail (the collection was dropped between the listing and the
/// read, or Qdrant faulted on that one collection); the implementation contains that failure so
/// one bad collection does not blank the whole view. Returning only the survivors would then hand
/// the caller a list that LOOKS complete and is not — "7 collections" while Qdrant holds 10 —
/// which is the exact defect the admin console's <c>deniedTypeCount</c> /
/// <c>unknownTypeCount</c> / <c>withheldTypeCount</c> fields exist to prevent everywhere else.
/// <see cref="ListedCount"/> is what makes the shortfall visible.
/// </para>
/// </summary>
public sealed record VectorCollectionListing(
    int ListedCount,
    IReadOnlyList<VectorCollectionStats> Collections);

/// <summary>
/// One collection's reported size. Both counts are <b>nullable</b> because Qdrant declares them
/// as proto3 optional fields (<c>CollectionInfo.HasPointsCount</c> /
/// <c>HasIndexedVectorsCount</c>): a collection that has never been optimized reports no
/// indexed-vectors count at all. Null is "not reported", which is not the same claim as zero,
/// and the endpoint keeps that distinction on the wire rather than flattening it to 0.
/// </summary>
public sealed record VectorCollectionStats(
    string Name,
    ulong? PointsCount,
    ulong? IndexedVectorsCount);

/// <summary>
/// <see cref="IVectorCollectionReader"/> over the same <see cref="QdrantClient"/> and the same
/// api-key convention <see cref="IntelligenceCollectionManager"/> uses: every call site opens a
/// <c>RequestHeaders.Use("api-key", apiKey)</c> scope, because that scope is ambient per async
/// flow and does not survive across awaits taken outside it.
/// </summary>
public sealed class IntelligenceCollectionReader(
    QdrantClient client,
    string apiKey,
    ILogger<IntelligenceCollectionReader> logger) : IVectorCollectionReader
{
    public async Task<VectorCollectionListing> ListCollectionStatsAsync(
        CancellationToken ct = default)
    {
        using var _ = RequestHeaders.Use("api-key", apiKey);
        using var activity = Telemetry.Source.StartActivity("qdrant.list_collection_stats", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");

        var names = await client.ListCollectionsAsync(cancellationToken: ct);

        var stats = new List<VectorCollectionStats>();
        foreach (var name in names)
        {
            // Per-collection failures are contained rather than fatal: a collection can be
            // dropped between the listing and this read, and one missing collection must not
            // blank the whole widget. Cancellation is NOT contained — it is the caller going
            // away, not a Qdrant fault.
            //
            // Contained is not the same as hidden: every collection skipped here still shows up
            // in the returned VectorCollectionListing.ListedCount, which is the count Qdrant
            // NAMED, not the count that survived this loop.
            try
            {
                var info = await client.GetCollectionInfoAsync(name, cancellationToken: ct);
                stats.Add(new VectorCollectionStats(
                    name,
                    info.HasPointsCount ? info.PointsCount : null,
                    info.HasIndexedVectorsCount ? info.IndexedVectorsCount : null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Skipping Qdrant collection {Collection}: its info could not be read", name);
            }
        }

        activity?.SetTag("qdrant.collection_count", stats.Count);
        // Reported separately from the count above, so a deployment where reads are quietly
        // failing is visible in traces rather than only in the warning log.
        activity?.SetTag("qdrant.listed_collection_count", names.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return new VectorCollectionListing(names.Count, stats);
    }
}
