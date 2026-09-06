using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// The collection every container-backed test class in this assembly joins.
///
/// WHY. xunit runs test collections in PARALLEL, and a class with no <c>[Collection]</c> is its own
/// collection. This assembly's six container classes therefore used to start their containers all
/// at once — Kafka, Qdrant, two Postgres, a StarRocks readiness cluster, and
/// <c>AllStoresContainerFixture</c>, which alone starts Postgres + StarRocks + Qdrant. Seven
/// containers, two of them StarRocks clusters, on a four-core dev box. That is not a hypothetical:
/// it drove this assembly from 2m22s to over an hour and produced four spurious failures, every one
/// of them a container that never finished starting rather than an assertion that disagreed.
///
/// Joining one collection makes these classes run one at a time. It does NOT share their
/// containers: each class keeps its own <c>IClassFixture</c>, so isolation is exactly what it was
/// and only the scheduling changed.
///
/// Adding a new container-backed test class? Put <c>[Collection(ContainerCollection.Name)]</c> on
/// it. A class that starts a container and joins no collection silently reintroduces the pile-up.
/// </summary>
[CollectionDefinition(ContainerCollection.Name)]
public sealed class ContainerCollection
{
    public const string Name = "containers";
}
