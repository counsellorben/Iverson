using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Schema;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// <see cref="SchemaProbe"/> is the harness's ONLY window onto the model the server actually
/// stored — that value is on no wire — so <c>ModelRejectedScenario.JudgeParity</c> and the
/// stored-model arm of its rejection judgement both rest entirely on this parse being right. A
/// parse that silently returned null would not redden either of them: null is also the legitimate
/// answer for a type carrying no embedding, so the parity assertion would simply go vacuous.
///
/// <para><b>Fixtures are REAL serialized <c>SchemaDescriptor</c>s, never hand-written JSON.</b> A
/// literal would re-encode exactly the assumptions under test — which arrays a row carries, and
/// (the trap) that <c>ChunkDescriptor</c>'s member is <c>ModelId</c> and not <c>ChunkModelId</c>,
/// unlike the wire's <c>PropertyDescriptor</c>, which really does have both. That is why the test
/// project — and only the test project, never the harness — references <c>Iverson.Api</c>; see the
/// comment on that reference in the csproj.</para>
/// </summary>
public class SchemaProbeTests
{
    /// <summary>
    /// Mirrors <c>SchemaRegistry.s_jsonOptions</c> (<c>SchemaRegistry.cs:263-267</c>), which is
    /// private, so this is the one assumption these fixtures still restate rather than derive. It
    /// is the cheap half: the naming policy is a single line in one file, whereas the SHAPE — the
    /// arrays and their member names — is what actually drifts, and that comes from the real types
    /// below.
    /// </summary>
    private static readonly JsonSerializerOptions RowOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>A registered row exactly as the server writes it: a real descriptor, serialized
    /// the way <c>SchemaRegistry.RegisterAsync</c> serializes it before the upsert.</summary>
    private static string Row(
        IReadOnlyList<VectorDescriptor> vectors,
        IReadOnlyList<ChunkDescriptor> chunks) =>
        JsonSerializer.Serialize(new SchemaDescriptor
        {
            TypeName = "S11ModelDotnet",
            TableName = "s11_model_dotnets",
            KeyColumn = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns = [new ColumnDescriptor("Title", "text", true)],
            FkColumns = [],
            VectorFields = vectors,
            ChunkFields = chunks,
            Relations = [],
            TenantColumn = SchemaDescriptor.TenantColumnName,
        }, RowOptions);

    private static VectorDescriptor Vector(string modelId) => new("Title", 768, modelId);

    private static ChunkDescriptor Chunk(string modelId) => new("Body", 512, 64, modelId, 768);

    [Fact]
    public void ModelIn_AVectorOnlyRow_ReadsTheVectorFieldsModel()
    {
        SchemaProbe.ModelIn(Row([Vector("nomic-embed-text")], [])).Should().Be("nomic-embed-text");
    }

    /// <summary>
    /// THE trap. <c>chunkFields</c>'s key is <c>modelId</c>, because it serializes
    /// <c>ChunkDescriptor.ModelId</c> — NOT <c>chunkModelId</c>, which is the WIRE's separate
    /// <c>PropertyDescriptor.chunk_model_id</c> field and does not appear in a stored row at all.
    /// "Correcting" the probe to read <c>chunkModelId</c> here throws rather than returning null,
    /// and this is the only fixture that reaches that key.
    /// </summary>
    [Fact]
    public void ModelIn_AChunkOnlyRow_ReadsTheChunkFieldsModelUnderTheSameModelIdKey()
    {
        SchemaProbe.ModelIn(Row([], [Chunk("snowflake-arctic-embed")]))
            .Should().Be("snowflake-arctic-embed");
    }

    /// <summary>
    /// vectorFields wins, mirroring <c>SchemaDescriptor.ModelOf</c>'s
    /// <c>VectorFields.FirstOrDefault() ?? ChunkFields.FirstOrDefault()</c>. The two models differ
    /// in this fixture on purpose: with one value they would agree by construction and the ordering
    /// would be graded by nothing. (A live type never carries two — the declaration is class-level
    /// — which is precisely why the ordering can drift unnoticed.)
    /// </summary>
    [Fact]
    public void ModelIn_ARowCarryingBoth_PrefersVectorFields_AsModelOfDoes()
    {
        SchemaProbe.ModelIn(Row([Vector("nomic-embed-text")], [Chunk("snowflake-arctic-embed")]))
            .Should().Be("nomic-embed-text");
    }

    /// <summary>
    /// A registered type with no embedded content at all — the state the re-registration guard
    /// treats as "this type is not changing its model, it is ceasing to have one". Null here is a
    /// real answer, not a failure, which is exactly why the three fixtures above have to pin the
    /// non-null cases.
    /// </summary>
    [Fact]
    public void ModelIn_ARowWithNeitherArrayPopulated_ReportsNoModel()
    {
        SchemaProbe.ModelIn(Row([], [])).Should().BeNull();
    }

    /// <summary>
    /// A row written before either array existed. <c>SchemaRegistry.LoadAsync</c> admits legacy
    /// rows verbatim, so the probe meets them: absent keys must read as "no model", never throw.
    /// </summary>
    [Fact]
    public void ModelIn_ALegacyRowMissingBothKeysEntirely_ReportsNoModel()
    {
        SchemaProbe.ModelIn("""{"typeName":"S11ModelDotnet","tableName":"s11_model_dotnets"}""")
            .Should().BeNull();
    }

    /// <summary>The harness's own copy of the server's registry table name — the same
    /// keep-a-separate-copy rule <see cref="PostgresProbe.TableName"/> follows, and the value
    /// <c>ModelRejectedScenario</c> builds its <c>DELETE FROM</c> assertion out of.</summary>
    [Fact]
    public void SchemaTable_IsTheServersSchemaRegistryTable()
    {
        SchemaProbe.SchemaTable.Should().Be("_iverson_schema");
    }

    [Fact]
    public async Task FetchModelAsync_WithAnUnusableConnectionString_ThrowsRatherThanReportingNoModel()
    {
        var probe = new SchemaProbe(string.Empty);

        var read = async () => await probe.FetchModelAsync("S11ModelDotnet");

        await read.Should().ThrowAsync<Exception>(
            "a failed read must be distinguishable from a type that legitimately carries no "
            + "embedding model, which is what a null return means");
    }
}
