using Xunit;

namespace Iverson.Vector.Tests;

/// <summary>
/// The collection every container-backed test class in this assembly joins, so its two
/// Qdrant-backed classes start their containers one at a time rather than both at once. See
/// <c>Iverson.Api.Tests/ContainerCollection.cs</c> for the full rationale; the short version is
/// that xunit runs collections in parallel and an unmarked class is its own collection.
///
/// Each class keeps its own <c>IClassFixture</c>, so this changes scheduling, not isolation.
/// </summary>
[CollectionDefinition(ContainerCollection.Name)]
public sealed class ContainerCollection
{
    public const string Name = "containers";
}
