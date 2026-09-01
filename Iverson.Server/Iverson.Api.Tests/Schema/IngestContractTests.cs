using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Vector;
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
///
/// <para><b>Three INPUTS to the emit are hand-copied and therefore unverifiable here.</b> The
/// arithmetic downstream of them is read out of the write path, but the values fed in are not, so a
/// change to the benchmark entity leaves this gate green while the two paths diverge:
/// <list type="bullet">
///   <item><description><see cref="ChunkMaxTokens"/> / <see cref="ChunkOverlap"/> (512 / 64) are
///   <c>IversonChunkAttribute</c>'s defaults, copied because <c>BenchmarkDocument.Body</c> carries a
///   bare <c>[IversonChunk]</c>. Write <c>[IversonChunk(maxTokens: 256)]</c> on it and the emitted
///   <c>chunkWindow</c> stays 2048/1792, the gate stays green, and <c>ingest.py</c> windows
///   differently from the server.</description></item>
///   <item><description><see cref="ChunkFieldName"/> (<c>"Body"</c>) is the property name that feeds
///   <c>ComputeChunkPointId</c> and the chunk payload's <c>field</c> key. Rename the property and the
///   goldened point ids stay self-consistent while pointing at ids the server no longer
///   writes.</description></item>
///   <item><description><see cref="EntityName"/> (<c>"BenchmarkDocument"</c>) is the type name fed to
///   <see cref="SchemaBuilder.ToTableName"/> for the collection base.</description></item>
/// </list>
/// There is no clean fix from this assembly: <c>Iverson.Api.Tests</c> references neither
/// <c>Iverson.LoadTest</c> (where <c>BenchmarkDocument</c> lives) nor
/// <c>Iverson.Client.Attributes</c> (where <c>IversonChunkAttribute</c> lives), and adding the
/// former makes <c>WebApplicationFactory&lt;Program&gt;</c> CS0104-ambiguous across this assembly.
/// The superseded contract read all three off a real <c>EntityDescriptor</c>; this one cannot.</para>
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

    // The three hand-copied inputs. See the "Three INPUTS" paragraph on the class doc comment for
    // why each is unverifiable from this assembly and what goes silently wrong if one drifts.
    private const string EntityName     = "BenchmarkDocument";
    private const string ChunkFieldName = "Body";
    private const int    ChunkMaxTokens = 512;
    private const int    ChunkOverlap   = 64;

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
        var window = IntelligenceStoreConsumer.ChunkWindow(ChunkMaxTokens, ChunkOverlap);

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
            // Provenance, not contract DATA — the closed-key-set rule governs what ingest.py
            // reads, and Python ignores this key entirely. Every value is a compile-time literal:
            // no timestamp, no machine name, no commit SHA, because anything varying per run would
            // make the drift gate fail against its own committed copy.
            _generated = new
            {
                by         = "Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs",
                regenerate =
                    $"{RegenerateVariable}=1 dotnet test " +
                    "Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract",
                doNotEditByHand = true,
                entity          = EntityName
            },
            chunkWindow = new
            {
                maxChars             = window.MaxChars,
                step                 = window.Step,
                wordBoundaryLookback = window.Lookback
            },
            distance         = IntelligenceCollectionManager.Metric.ToString(),
            collectionNaming = DeriveCollectionNaming(),
            embedding = new
            {
                documentPrefixes,
                defaultDocumentPrefix = EmbeddingPrefixes.DefaultDocument
            },
            golden = new
            {
                chunking = GoldenChunking(window),
                pointIds = GoldenPointIds(),
                centroid = GoldenCentroid(),
                documentComposition
            }
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
            @base    = SchemaBuilder.ToTableName(EntityName),
            template = "{base}{suffix}" + tenantSlot,
            objectSuffix,
            chunksSuffix
        };
    }

    // ── Golden cases ────────────────────────────────────────────────────────────────────────
    //
    // These three exist because the spec (§6) justifies keeping ingest.py's hand-written
    // split_into_chunks / key_to_ulong / chunk_point_id / compute_centroid on the grounds that
    // "the contract pins their behaviour rather than their code". Without them that sentence is
    // false: four algorithms duplicated across two languages with nothing gating either.

    /// <summary>
    /// Five chunking cases, chosen to cover the failure that already happened rather than to
    /// enumerate the space. The expected chunks are whatever the real <c>SplitIntoChunks</c>
    /// returns — never a boundary computed here, which would pin this test against Python instead
    /// of pinning production against Python.
    /// </summary>
    private static object[] GoldenChunking((int MaxChars, int Step, int Lookback) window)
    {
        // Long enough for three windows at (maxChars 2048, step 1792), so `step` is applied more
        // than once rather than merely being possible to apply.
        var multiChunkLength = window.Step * 2 + window.MaxChars / 4;

        // The two word-boundary cases differ by ONE character, and that character is the whole
        // point. C#'s LastIndexOf(' ', end, count) examines [end - count + 1, end]; with end at
        // maxChars and count at the lookback, that is [maxChars - lookback + 1, maxChars]. A space
        // at exactly maxChars - lookback + 1 is inside the window; one at maxChars - lookback is
        // not. Getting that bound off by one is precisely the divergence fixed at 4771286, so the
        // goldens straddle it instead of testing somewhere safely in the middle.
        var insideLookback  = window.MaxChars - window.Lookback + 1;
        var outsideLookback = insideLookback - 1;

        var cases = new (string Name, string Why, string Text)[]
        {
            ("shorter-than-the-window",
             "single-chunk path, and the Trim() equality that makes ingest.py's embed-reuse gate valid",
             "The quick brown fox jumps over the lazy dog."),

            ("exactly-at-the-window-boundary",
             "off-by-one at end == text.Length; note the trailing partial window `step` still produces",
             Repeat("boundary ", window.MaxChars)),

            ("multi-chunk-with-overlap",
             "step applied repeatedly",
             Repeat("overlap ", multiChunkLength)),

            ("word-boundary-extension-fires",
             "the LastIndexOf(' ', end, lookback) branch, with the space on the last position the scan reaches",
             new string('a', insideLookback) + " " + new string('b', multiChunkLength - insideLookback - 1)),

            ("word-boundary-extension-does-not-fire",
             "the same text with the space moved one character out of the scan's reach — the class of 4771286",
             new string('a', outsideLookback) + " " + new string('b', multiChunkLength - outsideLookback - 1))
        };

        return cases
            .Select(c => (object)new
            {
                name   = c.Name,
                why    = c.Why,
                text   = c.Text,
                chunks = InvokeSplitIntoChunks(c.Text, ChunkMaxTokens, ChunkOverlap)
                    .Select(chunk => (object)new { index = chunk.Index, text = chunk.Text })
                    .ToArray()
            })
            .ToArray();
    }

    /// <summary>
    /// One GUID key. There is no non-GUID case: <c>KeyToUlong</c>'s FNV branch is documented as
    /// unreachable (keys are server-generated UUIDv7), and goldening it would pin dead code.
    /// Two chunk indexes rather than one, so the index term in the mixing function is exercised.
    /// </summary>
    private static object[] GoldenPointIds()
    {
        const string key = "01a03beb-3e97-7918-8474-9bc8745b2800";
        var parentId = IntelligenceStoreConsumer.KeyToUlong(key);

        return
        [
            new
            {
                key,
                parentId,
                chunks = new[] { 0, 1 }
                    .Select(index => (object)new
                    {
                        field      = ChunkFieldName,
                        chunkIndex = index,
                        pointId    = InvokeComputeChunkPointId(parentId, ChunkFieldName, index)
                    })
                    .ToArray()
            }
        ];
    }

    /// <summary>
    /// Fixed 4-dimensional synthetic vectors: the formula is dimension-agnostic, so small vectors
    /// exercise it exactly as 768 would while keeping the check Ollama-free.
    ///
    /// <para>A tolerance rather than exact equality, because the two pipelines are known to differ
    /// numerically and that is accepted, not a defect to chase (spec §8):
    /// <see cref="IntelligenceStoreConsumer.ComputeCentroid"/> accumulates in <c>float</c> with
    /// <c>MathF.Sqrt</c>, while <c>ingest.py</c> computes in float64. They agree to roughly 1e-7,
    /// so the emitted tolerance is 1e-6 — one order of magnitude of headroom over the observed
    /// difference, and still far below anything that reorders a result set. The tolerance is
    /// emitted as contract DATA rather than hard-coded Python-side so both ends move together.</para>
    /// </summary>
    private static object GoldenCentroid()
    {
        float[][] inputs =
        [
            [1f, 0f, 0f, 0f],
            [0f, 2f, 0f, 0f],
            [3f, 0f, 4f, 0f]
        ];

        return new
        {
            inputs,
            output    = IntelligenceStoreConsumer.ComputeCentroid(inputs),
            tolerance = 1e-6
        };
    }

    // ── Reflection call sites ───────────────────────────────────────────────────────────────
    //
    // Three helpers the emit needs are not directly callable, for two different reasons, and the
    // difference is worth stating because it looks arbitrary otherwise:
    //
    //   * SplitIntoChunks and ComputeChunkPointId are `private static` in Iverson.Api. Iverson.Api
    //     grants InternalsVisibleTo to Iverson.Api.Tests, but private is private — no IVT grant
    //     reaches it. (KeyToUlong and ComputeCentroid are `internal static` in the same class and
    //     ARE called directly above, which is exactly why they look different.)
    //   * ComposeDocumentInput is `internal static` in Iverson.Embeddings, which grants
    //     InternalsVisibleTo to Iverson.Embeddings.Tests ONLY — not to this assembly — so a direct
    //     call would not compile. Reflection reaches non-public members across assemblies without
    //     any grant, per the convention already used at IntelligenceStoreConsumerTests.cs:766.
    //
    // Binding by name means a rename fails here loudly rather than silently emitting a stale value:
    // each lookup throws with the member it could not find.

    private static MethodInfo NonPublicStatic(Type owner, string name) =>
        owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"{owner.FullName} no longer declares a non-public static '{name}'; the ingest " +
            "contract cannot be emitted from code that has moved out from under it.");

    private static string ComposeDocumentInput(string prefix, string text) =>
        (string)NonPublicStatic(typeof(EmbeddingService), "ComposeDocumentInput")
            .Invoke(null, [prefix, text])!;

    private static IEnumerable<(string Text, int Index)> InvokeSplitIntoChunks(string text, int maxTokens, int overlap) =>
        (IEnumerable<(string Text, int Index)>)
            NonPublicStatic(typeof(IntelligenceStoreConsumer), "SplitIntoChunks")
                .Invoke(null, [text, maxTokens, overlap])!;

    private static ulong InvokeComputeChunkPointId(ulong parentId, string fieldName, int chunkIndex) =>
        (ulong)NonPublicStatic(typeof(IntelligenceStoreConsumer), "ComputeChunkPointId")
            .Invoke(null, [parentId, fieldName, chunkIndex])!;

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    /// <summary>Repeats <paramref name="unit"/> and cuts the result to exactly <paramref name="length"/> characters.</summary>
    private static string Repeat(string unit, int length) =>
        string.Concat(Enumerable.Repeat(unit, length / unit.Length + 1))[..length];

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
