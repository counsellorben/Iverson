using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S7 <c>vector-search</c>: proves each client library can express a vector similarity search and a
/// chunk search through its OWN builder API, and that all five agree on what comes back for the
/// same query text over the same seeded rows.
///
/// The shape follows S6 query's, for the same reasons: the subject is one shared type
/// (<c>VectorDoc</c>) that every language writes into and every language then searches, so
/// disagreement between two client libraries is observable. Only the .NET driver ever runs the
/// register phase — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor wholesale,
/// so five registrations of the same type name would leave four silent overwrites — and the
/// orchestrator re-registers the reported descriptor once with an authorization block before any
/// write, without which every seeded write is denied
/// (<c>RowFieldAuthorizationEvaluator.cs:10-12</c>).
///
/// Every driver stamps the run's <c>--id-prefix</c> on its row's <c>Marker</c> property and sends
/// exactly that value as the filter accompanying BOTH queries. <c>Marker</c> is declared
/// <c>[IversonMetadata]</c>, which is what lets one value scope both stores: <c>SearchSimilar</c>
/// filters it as an ordinary scalar payload clause on the object collection, and
/// <c>SearchChunks</c> filters it as a metadata column denormalized onto every chunk point
/// (<c>IntelligenceStoreConsumer</c>, <c>ObjectSearchGrpcService.BuildChunksFilter</c>). The marker
/// is unique per run, so the expected result set is exactly the rows this run seeded — which is
/// what makes both content assertions exact set comparisons rather than subset ones.
///
/// <para><b>The expected sets are the harness's own accounting.</b> Both content assertions grade
/// against what the WRITE phase reported (<c>DriverRunner.KeysByLanguage</c>), never against
/// anything the read phase being judged reported.</para>
///
/// <para><b>Why the similarity assertion grades labels and the chunk assertion grades keys.</b>
/// <c>SearchChunks</c> returns <c>parent_key</c> explicitly, so its parent set is comparable
/// against the write phase's row keys directly. <c>SearchSimilar</c> instead streams the Qdrant
/// point payload, whose row key lives under the reserved <c>key</c> entry
/// (<c>IntelligenceStoreConsumer.BuildObjectPointPayload</c>) that no client library's typed
/// projection binds to the entity's own key property — every one of the five would report an empty
/// id. <c>Label</c> is the per-language-unique scalar all five typed projections DO carry, so it is
/// the row identity the similarity comparison uses. It is derived, not reported: the harness knows
/// the label a given language stamps (<see cref="LabelFor"/>) and expects exactly the labels of the
/// languages whose write phase reported a key.</para>
///
/// <para><b>The projection wait.</b> Both RPCs are served from Qdrant, which a mapped write reaches
/// asynchronously through the outbox and only after the embedding model has vectorized every
/// annotated field. Between the write and read phases the orchestrator polls its OWN
/// <c>SearchSimilar</c> and <c>SearchChunks</c> — via <see cref="ProjectionWaiter"/>, the harness's
/// one bounded-poll convention — until both collections can see every seeded row. Both are polled,
/// not just one: they are separate upserts by the same consumer, so a wait satisfied on the object
/// collection alone would let the chunk read race the chunk upsert. Expiry is reported as a failed
/// step on every language, worded as the harness's own precondition failing and carrying no
/// requirement ID.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the run seeded at least one row for
/// these vector queries to match" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Both content
/// assertions compare against sets derived from the write phase; were those sets empty, an empty
/// similarity result and an empty chunk result would both compare equal — five clients agreeing on
/// nothing, rendered green. The backstop fires unconditionally, on every language, before and
/// outside those comparisons. It carries no requirement ID: no <c>IVC-VEC-*</c> statement owns "the
/// run seeded something" as such — that is a property of the harness's fixture, not of a client —
/// and it is strictly weaker than <see cref="Requirements.VecSimilarityReturnsExactlyFilteredRows"/>
/// and <see cref="Requirements.VecChunkSearchReturnsExactlyFilteredParents"/> wherever either can
/// fail.</para>
/// </summary>
public sealed class VectorSearchScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    ObjectSearchService.ObjectSearchServiceClient search,
    ProjectionWaiter? waiter = null,
    Action<string>? log = null)
{
    public const string Name = "vector-search";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and searches. Relation-free on purpose.</summary>
    internal const string TypeName = "VectorDoc";

    /// <summary>The <c>[IversonEmbedding]</c> property <c>SearchSimilar</c> searches.</summary>
    internal const string EmbeddedProperty = "Title";

    /// <summary>The <c>[IversonChunk]</c> property <c>SearchChunks</c> searches.</summary>
    internal const string ChunkedProperty = "Body";

    /// <summary>The <c>[IversonMetadata]</c> property both queries filter on.</summary>
    internal const string MarkerProperty = "Marker";

    /// <summary>The logical key name every driver reports its seeded row under.</summary>
    internal const string RowKeyName = "vector_doc";

    /// <summary>
    /// The query text all five drivers send, verbatim, to both RPCs. Shared so the five requests
    /// differ in nothing at all — a per-language query text would make a disagreement between two
    /// cells un-attributable to the client libraries.
    /// </summary>
    internal const string QueryText = "a short note about vector search conformance";

    /// <summary>
    /// Deliberately larger than the number of rows any run seeds (five, one per language), so the
    /// whole seeded set fits below the truncation boundary and both content assertions stay exact
    /// set comparisons rather than prefix comparisons. Truncation itself is Deferred in the VEC
    /// coverage ledger.
    /// </summary>
    internal const int TopK = 50;

    internal const string RegisterStepName = "register_vector_doc";
    internal const string WriteStepName = "write_vector_doc";
    internal const string SimilarStepName = "search_similar_by_title";
    internal const string ChunksStepName = "search_chunks_by_marker";

    /// <summary>
    /// The <c>Label</c> value a given language's driver stamps on its row. The one place the
    /// harness and the five drivers have to agree on a literal, so it is spelled once here and
    /// mirrored in each driver rather than re-derived per comparison.
    /// </summary>
    internal static string LabelFor(string language) => $"vec-{language}";

    /// <summary>
    /// The Qdrant wait is more patient AND less frequent than the StarRocks one, for one reason:
    /// this scenario's probe is not free to the thing it is waiting on. Each attempt embeds the
    /// query text twice through the same Ollama instance the ingest path is using to vectorize the
    /// rows being waited for, so polling every two seconds spends the model's throughput on the
    /// observation instead of on the work. A slower poll and a longer budget observe the same
    /// event without competing with it.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(6);

    private readonly ProjectionWaiter _waiter = waiter ?? new ProjectionWaiter(
        WaitTimeout, WaitInterval, ProjectionWaitResult.QdrantExplanation);

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        DriverContext context,
        string actingToken,
        CancellationToken ct = default)
    {
        if (languages.Count == 0)
            return [];

        var states = languages.ToDictionary(
            l => l, _ => new LanguageState(), StringComparer.OrdinalIgnoreCase);

        // ── register (dotnet only, register-once — see the class doc comment) ──────────────────
        var registerOutcomes = await runner.RunPhaseAsync(Phase.Register, [RegisterLanguage], context, ct);
        var registerOutcome = registerOutcomes.Count > 0 ? registerOutcomes[0] : null;

        var (descriptorJson, registerFailure) = registerOutcome switch
        {
            DriverPhaseOutcome.Success success => TryCaptureDescriptor(success.Document),
            DriverPhaseOutcome.Skipped skipped => (null, $"'{RegisterLanguage}' driver skipped: {skipped.Reason}"),
            DriverPhaseOutcome.Broken broken => (null,
                $"'{RegisterLanguage}' driver broke during the register phase (exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}"),
            _ => (null, $"'{RegisterLanguage}' produced no register-phase outcome"),
        };

        if (registerFailure is not null)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S7 vector-search's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S7 vector-search's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language seeds one row carrying the run's marker ────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            var step = document.Steps.FirstOrDefault(s => s.Name == WriteStepName);
            if (step is null)
            {
                state.Assertions.Add(Assertion.Fail($"step '{WriteStepName}'",
                    "the driver reported no such step, so this language seeded no row for these queries to match"));
                continue;
            }

            state.Assertions.Add(Assertion.From(
                $"step '{WriteStepName}' succeeded", step.Ok, step.Error ?? "ok"));
        }

        // The expected sets: taken from the runner's accumulated key map rather than from any
        // read-phase report, so the content assertions grade against something the thing being
        // judged did not produce.
        var expectedKeys = ExpectedKeys(runner.KeysByLanguage);
        var expectedLabels = ExpectedLabels(runner.KeysByLanguage);

        // ── wait for BOTH Qdrant collections before any client reads them ──────────────────────
        var marker = context.IdPrefix;
        var wait = await _waiter.WaitAsync(
            $"the {expectedKeys.Count} '{TypeName}' row(s) marked '{marker}' (object vectors and chunks)",
            async token =>
            {
                var similar = await CountSimilarVisibleAsync(marker, actingToken, token);
                var chunkParents = await CountChunkParentsVisibleAsync(marker, actingToken, token);
                return ProjectionReady(similar, chunkParents, expectedKeys.Count)
                    ? ProbeOutcome.Ready(
                        $"{similar} object vector(s) and {chunkParents} chunk parent(s) visible")
                    : ProbeOutcome.NotYet(
                        $"{similar} object vector(s) and {chunkParents} chunk parent(s) visible " +
                        $"of {expectedKeys.Count} seeded row(s)");
            },
            ct);

        log?.Invoke($"  projection wait: {(wait.Satisfied ? "satisfied" : "TIMED OUT")} " +
                    $"after {wait.Elapsed.TotalSeconds:0.0}s over {wait.Attempts} attempt(s) — {wait.LastDetail}");

        if (!wait.Satisfied)
        {
            // A shared precondition, so it fails every row: running the read phase after it would
            // grade five client libraries on rows the store cannot yet see.
            foreach (var (language, state) in states)
            {
                if (state.Terminal is not null) continue;
                state.Assertions.Add(Assertion.Fail(
                    $"{language}: the seeded rows reached the vector store within the bounded wait",
                    wait.TimeoutDetail));
            }

            return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
        }

        // ── read: every alive language issues the same two queries ─────────────────────────────
        return GradeReads(
            states, await RunPhaseAsync(Phase.Read, states, context, ct), expectedKeys, expectedLabels);
    }

    /// <summary>
    /// Wires the read phase's documents through <see cref="Judge"/> and into cells. Extracted from
    /// <see cref="RunAsync"/> — and internal — because the wiring is exactly as safety-critical as
    /// the judgement and used to be reachable only from a live stack: drop the <see cref="Judge"/>
    /// call below and every VEC assertion silently stops reaching a cell, leaving a fully green
    /// vector-search row that verified nothing. That mutation must redden a named test.
    /// </summary>
    internal static IReadOnlyList<ReportCell> GradeReads(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> reads,
        IReadOnlySet<Guid> expectedKeys,
        IReadOnlySet<string> expectedLabels)
    {
        foreach (var (language, document) in reads)
            states[language].Assertions.AddRange(Judge(language, expectedKeys, expectedLabels, document));

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>
    /// Judges one language's read phase against the two expectations the write phase produced.
    /// Pure over reported data (no I/O), so every branch below is exercisable from a unit test.
    ///
    /// Every assertion fires unconditionally: a missing step, a failed step and a step reporting
    /// nothing usable each become an explicit failure naming its consequence, never a silent skip,
    /// so no VEC requirement can be discharged vacuously.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(
        string language,
        IReadOnlySet<Guid> expectedKeys,
        IReadOnlySet<string> expectedLabels,
        PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the VEC backstop (uncited by design — see the class doc comment) ──────────────────
        assertions.Add(Assertion.From(
            $"{language}: the run seeded at least one row for these vector queries to match",
            expectedKeys.Count > 0,
            $"the write phase produced {expectedKeys.Count} row key(s); with none, an empty " +
            "similarity result and an empty chunk result would both compare equal to the expectation"));

        var similarStep = document.Steps.FirstOrDefault(s => s.Name == SimilarStepName);
        assertions.Add(similarStep is null
            ? Assertion.Fail(
                $"{language}: a vector similarity search is reachable through the client's public API",
                $"the driver reported no '{SimilarStepName}' step",
                Requirements.VecSimilaritySearchReachable)
            : Assertion.From(
                $"{language}: a vector similarity search is reachable through the client's public API",
                similarStep.Ok,
                similarStep.Error ?? "ok",
                Requirements.VecSimilaritySearchReachable));

        var returnedLabels = ReadLabels(similarStep is { Ok: true } ? similarStep.Entity : null);
        var missingLabels = expectedLabels.Except(returnedLabels, StringComparer.Ordinal).ToList();
        var unexpectedLabels = returnedLabels.Except(expectedLabels, StringComparer.Ordinal).ToList();

        assertions.Add(Assertion.From(
            $"{language}: the similarity search returned exactly the seeded rows",
            missingLabels.Count == 0 && unexpectedLabels.Count == 0,
            missingLabels.Count == 0 && unexpectedLabels.Count == 0
                ? $"{returnedLabels.Count} row(s), matching the {expectedLabels.Count} the write phase seeded"
                // The label count is stated explicitly because a label can legitimately be the
                // empty string: a client that streamed five rows but bound none of their payload
                // fields reports five EMPTY labels, which renders identically to having returned no
                // rows at all once joined into a list. The count tells the two apart, and they have
                // very different causes (a projection that cannot materialize the payload, versus a
                // filter that matched nothing).
                : $"the driver reported {returnedLabels.Count} distinct label(s); " +
                  $"seeded-but-absent: [{string.Join(", ", missingLabels)}]; " +
                  $"returned-but-unseeded: [{string.Join(", ", unexpectedLabels)}]",
            Requirements.VecSimilarityReturnsExactlyFilteredRows));

        var chunksStep = document.Steps.FirstOrDefault(s => s.Name == ChunksStepName);
        assertions.Add(chunksStep is null
            ? Assertion.Fail(
                $"{language}: a chunk search is reachable through the client's public API",
                $"the driver reported no '{ChunksStepName}' step",
                Requirements.VecChunkSearchReachable)
            : Assertion.From(
                $"{language}: a chunk search is reachable through the client's public API",
                chunksStep.Ok,
                chunksStep.Error ?? "ok",
                Requirements.VecChunkSearchReachable));

        var returnedParents = ReadParentKeys(chunksStep is { Ok: true } ? chunksStep.Entity : null);
        var missingParents = expectedKeys.Except(returnedParents).ToList();
        var unexpectedParents = returnedParents.Except(expectedKeys).ToList();

        assertions.Add(Assertion.From(
            $"{language}: the chunk search returned chunks for exactly the seeded rows",
            missingParents.Count == 0 && unexpectedParents.Count == 0,
            missingParents.Count == 0 && unexpectedParents.Count == 0
                ? $"{returnedParents.Count} distinct parent row(s), matching the {expectedKeys.Count} " +
                  "the write phase seeded"
                : $"seeded-but-absent: [{Join(missingParents)}]; returned-but-unseeded: [{Join(unexpectedParents)}]",
            Requirements.VecChunkSearchReturnsExactlyFilteredParents));

        return assertions;
    }

    /// <summary>
    /// The row labels a driver's similarity step reported, read out of its <c>entity</c>. All five
    /// drivers emit the same deliberately minimal projection — <c>{"labels":["vec-go", ...]}</c> —
    /// so this reader needs no per-language special-casing. A non-string entry is skipped rather
    /// than stringified: a client that reported a number where a label belongs has not reported a
    /// label, and coercing it would invent agreement. An unparsable or malformed document yields an
    /// empty set, which the exact set comparison then reports as every seeded row missing. Nothing
    /// here judges.
    /// </summary>
    internal static IReadOnlySet<string> ReadLabels(JsonElement? entity)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in ArrayProperty(entity, "labels"))
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } label)
                labels.Add(label);
        }

        return labels;
    }

    /// <summary>
    /// The DISTINCT parent row keys a driver's chunk step reported, out of
    /// <c>{"parentKeys":["&lt;uuid&gt;", ...]}</c>. Distinct because one parent row may own several
    /// chunks: <c>IVC-VEC-004</c> constrains which parents the filter admits, not how many windows
    /// the server split their text into. Keys are parsed as <see cref="Guid"/> so the five
    /// languages' UUID spellings are comparable.
    /// </summary>
    internal static IReadOnlySet<Guid> ReadParentKeys(JsonElement? entity)
    {
        var keys = new HashSet<Guid>();
        foreach (var element in ArrayProperty(entity, "parentKeys"))
        {
            if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var parsed))
                keys.Add(parsed);
        }

        return keys;
    }

    /// <summary>
    /// The elements of <paramref name="name"/> on <paramref name="entity"/>, or nothing at all when
    /// the document is absent, is not an object, lacks the property, or carries something that is
    /// not an array there. Shared by both readers so a malformed report degrades identically for
    /// each — never by throwing.
    /// </summary>
    private static IEnumerable<JsonElement> ArrayProperty(JsonElement? entity, string name)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return [];

        return document.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];
    }

    /// <summary>
    /// Every <c>vector_doc</c> key the write phase reported, across all languages, parsed as
    /// <see cref="Guid"/>. This is the harness's own accounting of what it seeded — the
    /// independent expectation <see cref="Requirements.VecChunkSearchReturnsExactlyFilteredParents"/>
    /// grades against.
    /// </summary>
    internal static IReadOnlySet<Guid> ExpectedKeys(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage)
    {
        var keys = new HashSet<Guid>();
        foreach (var (_, byName) in keysByLanguage)
        {
            if (byName.TryGetValue(RowKeyName, out var raw) && Guid.TryParse(raw, out var parsed))
                keys.Add(parsed);
        }

        return keys;
    }

    /// <summary>
    /// The row labels the harness expects a similarity search to return: one per language whose
    /// write phase actually reported a <c>vector_doc</c> key. Derived from the SAME key map
    /// <see cref="ExpectedKeys"/> reads, so a language that seeded nothing is absent from both
    /// expectations and no client is graded against a row that does not exist.
    /// </summary>
    internal static IReadOnlySet<string> ExpectedLabels(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (language, byName) in keysByLanguage)
        {
            if (byName.TryGetValue(RowKeyName, out var raw) && Guid.TryParse(raw, out _))
                labels.Add(LabelFor(language));
        }

        return labels;
    }

    // ── the orchestrator's own probe of the two Qdrant collections ───────────────────────────

    /// <summary>
    /// The projection-wait predicate, extracted so it is testable without a live stack: the wait is
    /// satisfied only when BOTH Qdrant collections can already see at least as many marked rows as
    /// the write phase produced. Both, not either — the object vectors and the chunk points are
    /// separate upserts by the same consumer, so a wait satisfied on one alone would let the other
    /// read race its own upsert. <paramref name="expected"/> of zero is deliberately NOT ready — no
    /// language seeded anything, so satisfying the wait would let the read phase grade against
    /// nothing and the harness must instead report its own precondition failing.
    /// </summary>
    internal static bool ProjectionReady(int similarVisible, int chunkParentsVisible, int expected) =>
        expected > 0 && similarVisible >= expected && chunkParentsVisible >= expected;

    /// <summary>
    /// Counts the object-collection points the marker matches through the orchestrator's OWN
    /// <c>SearchSimilar</c> call. This is a projection probe, not a conformance observation:
    /// nothing it returns is ever compared against a client's report, so a driver cannot
    /// manufacture readiness.
    /// </summary>
    private async Task<int> CountSimilarVisibleAsync(string marker, string actingToken, CancellationToken ct)
    {
        var request = new SearchSimilarRequest
        {
            TypeName = TypeName,
            Property = EmbeddedProperty,
            Query = QueryText,
            TopK = TopK,
            FilterLogic = SearchLogic.And,
            Filter = { MarkerClause(marker) },
        };

        using var call = search.SearchSimilar(request, ActingHeaders(actingToken), cancellationToken: ct);

        var count = 0;
        while (await call.ResponseStream.MoveNext(ct))
            count++;

        return count;
    }

    /// <summary>
    /// Counts the DISTINCT parent rows the chunks collection can already see for this marker,
    /// through the orchestrator's OWN <c>SearchChunks</c> call. Distinct for the same reason
    /// <see cref="ReadParentKeys"/> is: a parent owning several chunks must not make the wait look
    /// satisfied before every parent has been ingested.
    /// </summary>
    private async Task<int> CountChunkParentsVisibleAsync(string marker, string actingToken, CancellationToken ct)
    {
        var request = new SearchChunksRequest
        {
            TypeName = TypeName,
            Property = ChunkedProperty,
            Query = QueryText,
            TopK = TopK,
            Filter = { MarkerClause(marker) },
        };

        using var call = search.SearchChunks(request, ActingHeaders(actingToken), cancellationToken: ct);

        var parents = new HashSet<string>(StringComparer.Ordinal);
        while (await call.ResponseStream.MoveNext(ct))
            parents.Add(call.ResponseStream.Current.ParentKey);

        return parents.Count;
    }

    private static SearchClause MarkerClause(string marker) => new()
    {
        Property = MarkerProperty,
        Operator = SearchOperator.Equals,
        ClauseType = SearchClauseType.Filter,
        Value = new SearchValue { StringVal = marker },
    };

    private static Metadata ActingHeaders(string actingToken) =>
        new() { { "x-acting-user-authorization", $"Bearer {actingToken}" } };

    // ── register-phase descriptor capture ────────────────────────────────────────────────────

    internal static (JsonElement? Descriptor, string? Failure) TryCaptureDescriptor(PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == RegisterStepName);
        if (step is null)
            return (null, $"the {RegisterLanguage} driver reported no '{RegisterStepName}' step");

        if (!step.Ok)
            return (null, step.Error ?? "registration failed");

        if (step.TypeDescriptor is not { } json)
            return (null, "typeDescriptor was null on the register step");

        try
        {
            // Parsed but discarded: parsing is the validation — the Reregistrar needs the raw JSON,
            // and a descriptor that cannot be parsed here would fail there with a worse message.
            Verifier.ParseDescriptor(json);
            return (json, null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ── phase plumbing (mirrors QueryScenario's) ─────────────────────────────────────────────

    private async Task<IReadOnlyList<(string Language, PhaseDocument Document)>> RunPhaseAsync(
        Phase phase, Dictionary<string, LanguageState> states, DriverContext context, CancellationToken ct)
    {
        var alive = ScenarioCells.Alive(states).ToList();
        if (alive.Count == 0)
            return [];

        log?.Invoke($"  phase {PhaseNames.ToToken(phase)}: {string.Join(", ", alive)}");

        var documents = new List<(string, PhaseDocument)>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(phase, alive, context, ct))
        {
            reported.Add(outcome.Language);
            var state = states[outcome.Language];
            switch (outcome)
            {
                case DriverPhaseOutcome.Success success:
                    documents.Add((outcome.Language, success.Document));
                    break;
                case DriverPhaseOutcome.Skipped skipped:
                    state.Terminal = ReportCell.Skip(outcome.Language, Name, skipped.Reason, state.Assertions);
                    break;
                case DriverPhaseOutcome.Broken broken:
                    state.Terminal = ReportCell.Fail(outcome.Language, Name,
                        $"driver broke during the {PhaseNames.ToToken(phase)} phase " +
                        $"(exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}", state.Assertions);
                    break;
            }
        }

        foreach (var language in alive.Where(l => !reported.Contains(l)))
        {
            states[language].Terminal = ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language", states[language].Assertions);
        }

        return documents;
    }

    private static string Join(IEnumerable<Guid> keys) => string.Join(", ", keys.Select(k => k.ToString()));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
