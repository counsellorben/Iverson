using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>A scrolled point: its payload canonicalised to strings (as <c>SearchNamedAsync</c> does) and, when a
/// vector was selected and the point has it, that vector.</summary>
public sealed record ScrolledPoint(ulong Id, IReadOnlyDictionary<string, string> Payload, float[]? Vector);

/// <summary>One scroll page; <see cref="NextOffset"/> is null after the last page.</summary>
public sealed record VectorScrollPage(IReadOnlyList<ScrolledPoint> Points, PointId? NextOffset);
