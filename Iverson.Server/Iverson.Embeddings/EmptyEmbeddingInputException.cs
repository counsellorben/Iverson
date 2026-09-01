namespace Iverson.Embeddings;

/// <summary>
/// Text offered for embedding was empty or whitespace-only. Transport-neutral by design —
/// <see cref="EmbeddingService"/> has no dependency on gRPC or Kafka; callers that need a
/// transport-specific error (e.g. an RpcException) translate this at their boundary.
/// </summary>
public sealed class EmptyEmbeddingInputException(string message) : Exception(message);
