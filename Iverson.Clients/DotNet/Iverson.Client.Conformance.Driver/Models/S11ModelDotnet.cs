using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S11 <c>model-rejected</c>'s .NET fixture (<c>Scenarios/ModelRejectedScenario.cs</c>). Unlike
/// S1's shared fixtures, each requested language registers its OWN instance of this scenario's
/// type rather than one type shared across all five — the subject is what happens to a type
/// ALREADY registered by THIS client, so five languages sharing one type would leave four of the
/// five columns grading a row a different client registered. Must be named exactly
/// <c>S11ModelDotnet</c>: <c>ModelRejectedScenario.TypeNameFor("dotnet")</c> derives and asserts
/// this name with ordinal comparison.
///
/// <para>Declares the deployment's default model explicitly
/// (<c>[IversonEmbeddingModel("nomic-embed-text")]</c>) rather than a second one, on purpose: this
/// exercises the whole declaration path while keeping the conformance environment single-model, so
/// no second model ever needs to be pulled. It also means the harness alone cannot distinguish "the
/// client stamped the declared model" from "the client sent <c>""</c> and the server fell back to
/// the same value" — that distinction is pinned by a client-side unit test instead
/// (<c>SchemaRegistrarTests.cs</c>'s
/// <c>RegisterAllAsync_StampsDeclaredEmbeddingModel_OnEmbeddingAndChunkProperties</c>).</para>
/// </summary>
[IversonEntity]
[IversonEmbeddingModel("nomic-embed-text")]
public class S11ModelDotnet
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;

    [IversonEmbedding] public string Title { get; set; } = string.Empty;
    [IversonChunk] public string Body { get; set; } = string.Empty;
}
