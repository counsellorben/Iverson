namespace Iverson.Vector;

/// <summary>
/// A DSL clause could not be translated into a Qdrant filter. Transport-neutral by design —
/// <see cref="QdrantFilterBuilder"/> has no dependency on gRPC or any other transport; callers
/// that need a transport-specific error (e.g. an RpcException) translate this at their boundary.
/// </summary>
public sealed class FilterTranslationException(string message) : Exception(message);
