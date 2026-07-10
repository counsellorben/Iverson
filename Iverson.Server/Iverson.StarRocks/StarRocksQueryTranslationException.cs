namespace Iverson.StarRocks;

/// <summary>
/// A search/aggregate/group-by/pipeline request could not be translated into StarRocks SQL.
/// Transport-neutral by design — StarRocksQueryBuilder and StarRocksPipelineBuilder have no
/// dependency on gRPC or any other transport; callers that need a transport-specific error
/// (e.g. an RpcException) translate this at their boundary. Mirrors Iverson.Vector.FilterTranslationException.
/// </summary>
public sealed class StarRocksQueryTranslationException(string message) : Exception(message);
