using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api;
using Iverson.Api.Consumers;
using Iverson.Embeddings;
using Iverson.Vector;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// Generates <c>Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json</c> from the real C#
/// write path, and fails when the committed copy has drifted away from it.
///
/// <para><b>Generator and drift gate are deliberately the same test.</b> <c>ingest.py</c> windows
/// and prefixes text without going through <see cref="IntelligenceStoreConsumer"/> or
/// <see cref="EmbeddingService"/> at all, so it has to reproduce their numbers by hand. Emitting the
/// contract from a separate command and asserting against it in a separate test would let the emit
/// rot; one artefact that regenerates on demand and otherwise gates cannot.</para>
///
/// <para>Regenerate with:
/// <code>
/// IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract
/// </code></para>
///
/// <para><b>What the contract does not pin.</b> Query prefixes are not emitted: queries are
/// embedded inside <c>Iverson.Api</c>, which is itself that constant's source, so no Python
/// consumer exists for one. Collection-creation parity (vector names, payload indexes) is also not
/// emitted — Ben's call, 2026-09-01 — because those derive from <c>Type.GetProperties()</c>, whose
/// order the CLR does not guarantee, and pinning them would require de-duplicating and
/// ordinal-sorting to stop the gate flaking against its own committed copy.</para>
/// </summary>
public class IngestContractTests
{
    private const string RegenerateVariable = "IVERSON_REGENERATE_INGEST_CONTRACT";

    // Fixed sample text for every golden composition case, so the only thing that varies between
    // cases is the prefix under test.
    private const string SampleText = "the quick brown fox";

    // Probe tokens for reading the collection-naming rule back out of ResolveCollectionName. They
    // must not appear in the rule's own literal text (the separators, the "_chunks" suffix).
    private const string BaseProbe   = "BASEPROBE";
    private const string TenantProbe = "TENANTPROBE";

    private static readonly MethodInfo ComposeDocumentInputMethod =
        typeof(EmbeddingService).GetMethod("ComposeDocumentInput", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "EmbeddingService no longer declares a non-public static ComposeDocumentInput; the " +
            "ingest contract cannot be emitted from code that has moved out from under it.");

    [Fact]
    public void IngestContract_EmittedFromTheRealWritePath_MatchesTheCommittedCopy()
    {
        var emitted = Normalize(EmitContract());
        var path    = LocateContractFile();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
            File.WriteAllText(path, emitted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.Exists(path).Should().BeTrue(
            $"the ingest contract must be committed at {path} — run: " +
            $"{RegenerateVariable}=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract");

        var committed = Normalize(File.ReadAllText(path));

        // Assert.Fail with a line-level diff rather than committed.Should().Be(emitted): a raw
        // FluentAssertions string comparison dumps both documents in full and reports a character
        // offset, which says less than the three lines below.
        if (!string.Equals(committed, emitted, StringComparison.Ordinal))
            Assert.Fail(
                $"The committed ingest contract has drifted from the C# write path.{Environment.NewLine}" +
                $"{Environment.NewLine}{FirstDifference(committed, emitted)}{Environment.NewLine}" +
                $"{Environment.NewLine}If the C# side is right, regenerate: {RegenerateVariable}=1 dotnet test " +
                $"Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract{Environment.NewLine}" +
                "If it is not, the contract just caught a change to the write path that ingest.py " +
                "has not been taught about.");
    }

    // ── The emit ────────────────────────────────────────────────────────────────────────────

    private static string EmitContract()
    {
        // BenchmarkDocument.Body carries a bare [IversonChunk], and IversonChunkAttribute's
        // defaults are maxTokens = 512, overlap = 64 (BenchmarkDocument.cs / IversonChunkAttribute.cs)
        // — the values the benchmark ingest actually chunks with. The arithmetic itself is read
        // out of ChunkWindow, never re-derived here.
        var window = IntelligenceStoreConsumer.ChunkWindow(512, 64);

        var documentPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (family, prefix) in EmbeddingPrefixes.Table)
            documentPrefixes[family] = prefix.Document;

        var documentComposition = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (family, prefix) in EmbeddingPrefixes.Table)
            documentComposition[family] = new
            {
                text     = SampleText,
                composed = ComposeDocumentInput(prefix.Document, SampleText)
            };
        documentComposition["__default__"] = new
        {
            text     = SampleText,
            composed = ComposeDocumentInput(EmbeddingPrefixes.DefaultDocument, SampleText)
        };

        var contract = new
        {
            chunkWindow = new
            {
                maxChars             = window.MaxChars,
                step                 = window.Step,
                wordBoundaryLookback = window.Lookback
            },
            distance         = Distance.Cosine.ToString(),
            collectionNaming = DeriveCollectionNaming(),
            embedding = new
            {
                documentPrefixes,
                defaultDocumentPrefix = EmbeddingPrefixes.DefaultDocument
            },
            golden = new { documentComposition }
        };

        return JsonSerializer.Serialize(contract, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // ── Collection naming, read back out of ResolveCollectionName ───────────────────────────

    /// <summary>
    /// Emits the naming RULE, recovered by calling the real
    /// <see cref="IntelligenceTenantScope.ResolveCollectionName"/> with probe tokens and
    /// substituting them back out — a change to the separator or the "_chunks" suffix changes the
    /// contract, rather than the contract silently stating a rule the code no longer follows.
    /// </summary>
    private static object DeriveCollectionNaming()
    {
        var scope = new IntelligenceTenantScope(apiKey: "unused-for-naming");

        var objectResolved = scope.ResolveCollectionName(BaseProbe, TenantProbe, isChunks: false);
        var chunksResolved = scope.ResolveCollectionName(BaseProbe, TenantProbe, isChunks: true);

        var tenantSuffix = "_" + TenantProbe;
        if (!objectResolved.StartsWith(BaseProbe, StringComparison.Ordinal) ||
            !objectResolved.EndsWith(tenantSuffix, StringComparison.Ordinal) ||
            !chunksResolved.StartsWith(BaseProbe, StringComparison.Ordinal) ||
            !chunksResolved.EndsWith(tenantSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ResolveCollectionName no longer produces '{{base}}{{suffix}}_{{tenant}}' " +
                $"('{objectResolved}', '{chunksResolved}'); the emitted naming rule assumes it does.");

        var objectSuffix = objectResolved[BaseProbe.Length..^tenantSuffix.Length];
        var chunksSuffix = chunksResolved[BaseProbe.Length..^tenantSuffix.Length];

        var tenantSlot = objectResolved[(BaseProbe.Length + objectSuffix.Length)..]
            .Replace(TenantProbe, "{tenant}", StringComparison.Ordinal);

        return new
        {
            @base    = "BenchmarkDocument".ToSnakeCase() + "s",
            template = "{base}{suffix}" + tenantSlot,
            objectSuffix,
            chunksSuffix
        };
    }

    // ── Reflection call site ────────────────────────────────────────────────────────────────
    //
    // ComposeDocumentInput is `internal static` in Iverson.Embeddings, which grants
    // InternalsVisibleTo to Iverson.Embeddings.Tests only — not to this assembly — so a direct
    // call would not compile. Reflection reaches it without any grant, per the convention already
    // used at IntelligenceStoreConsumerTests.cs:766.

    private static string ComposeDocumentInput(string prefix, string text) =>
        (string)ComposeDocumentInputMethod.Invoke(null, [prefix, text])!;

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locating a repo file from a test is a new pattern here — nothing in this assembly walked up
    /// from <see cref="AppContext.BaseDirectory"/> before — so the marker is stated rather than
    /// inherited: <c>Iverson.slnx</c> sits at the repository root and nowhere else.
    /// </summary>
    private static string LocateContractFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iverson.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException(
                $"Walked up from '{AppContext.BaseDirectory}' without finding Iverson.slnx, so the " +
                "repository root — and with it the ingest contract — cannot be located.");

        return Path.Combine(directory.FullName, "Iverson.Server", "Iverson.LoadTest", "scripts", "ingest-contract.json");
    }

    /// <summary>
    /// Line endings and the trailing newline are normalized on BOTH sides so the gate reports real
    /// drift rather than a checkout's autocrlf setting.
    /// </summary>
    private static string Normalize(string json) =>
        json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    /// <summary>
    /// A line-level pointer at the first divergence between the committed and freshly emitted
    /// contract. A raw string comparison on a multi-KB document reports a character offset, which
    /// is not something a reader can act on.
    /// </summary>
    private static string FirstDifference(string committed, string emitted)
    {
        var committedLines = committed.Split('\n');
        var emittedLines   = emitted.Split('\n');
        var count          = Math.Min(committedLines.Length, emittedLines.Length);

        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(committedLines[i], emittedLines[i], StringComparison.Ordinal))
                return $"First differing line ({i + 1}):{Environment.NewLine}" +
                       $"  committed: {committedLines[i]}{Environment.NewLine}" +
                       $"  emitted:   {emittedLines[i]}";
        }

        return committedLines.Length == emittedLines.Length
            ? "Contents are identical but the comparison reported a difference — this should be unreachable."
            : $"Files agree for the first {count} lines; committed has {committedLines.Length} lines, " +
              $"emitted has {emittedLines.Length}.";
    }
}
