using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S6 <c>query</c>: proves each client library can express a filtered search and an aggregation
/// through its OWN builder API, and that all five agree on the result set and the aggregate value
/// for the same query over the same seeded rows.
///
/// The shape follows S4 interop's, for the same reason: the subject is one shared type
/// (<c>QueryDoc</c>) that every language writes into and every language then queries, so
/// disagreement between two client libraries is observable. Only the .NET driver ever runs the
/// register phase — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor wholesale,
/// so five registrations of the same type name would leave four silent overwrites — and the
/// orchestrator re-registers the reported descriptor once with an authorization block before any
/// write, without which every seeded write is denied
/// (<c>RowFieldAuthorizationEvaluator.cs:10-12</c>).
///
/// Every driver stamps the run's <c>--id-prefix</c> on its row's <c>Marker</c> field and filters on
/// exactly that value. The marker is unique per run, so the expected result set is exactly the rows
/// this run seeded: no earlier run's rows and no other scenario's rows can match, which is what
/// makes <see cref="Requirements.QrySearchReturnsExactlyMatchingRows"/> an exact set comparison
/// rather than a subset one.
///
/// <para><b>The expected set is the harness's own accounting.</b> Both content assertions grade
/// against the row keys the WRITE phase reported (<c>DriverRunner.KeysByLanguage</c>), never
/// against anything the read phase being judged reported. In particular the aggregate is NOT
/// graded against this driver's own search result — that would let a client that got both wrong in
/// the same direction discharge <see cref="Requirements.QryAggregateCountsExactlyMatchingRows"/>
/// by agreeing with itself.</para>
///
/// <para><b>The projection wait.</b> <c>Search</c> and <c>Aggregate</c> are served from StarRocks,
/// which a mapped write reaches asynchronously through the outbox. Between the write and read
/// phases the orchestrator polls its own <c>Search</c> — via <see cref="ProjectionWaiter"/>, the
/// harness's one bounded-poll convention — until every seeded row is visible. Expiry is reported
/// as a failed step on every language, worded as the harness's own precondition failing.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the run seeded at least one row for this
/// query to match" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Both content
/// assertions compare against the expected key set; were that set empty, an empty result set would
/// compare equal and a zero aggregate would match — five clients agreeing on nothing, rendered
/// green. The backstop fires unconditionally, on every language, before and outside those
/// comparisons. It carries no requirement ID: no <c>IVC-QRY-*</c> statement owns "the run seeded
/// something" as such — that is a property of the harness's fixture, not of a client — and it is
/// strictly weaker than <see cref="Requirements.QrySearchReturnsExactlyMatchingRows"/> wherever
/// that can fail.</para>
/// </summary>
public sealed class QueryScenario(
    DriverRunner runner,
    Reregistrar reregistrar,
    ObjectSearchService.ObjectSearchServiceClient search,
    ProjectionWaiter? waiter = null,
    Action<string>? log = null)
{
    public const string Name = "query";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and queries. Relation-free on purpose.</summary>
    internal const string TypeName = "QueryDoc";

    /// <summary>The scalar property every driver filters on, stamped with the run's id prefix.</summary>
    internal const string MarkerProperty = "Marker";

    /// <summary>The logical key name every driver reports its seeded row under.</summary>
    internal const string RowKeyName = "query_doc";

    internal const string RegisterStepName = "register_query_doc";
    internal const string WriteStepName = "write_query_doc";
    internal const string SearchStepName = "search_by_marker";
    internal const string AggregateStepName = "aggregate_count";

    private readonly ProjectionWaiter _waiter = waiter ?? new ProjectionWaiter();

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
                $"'{RegisterLanguage}' driver broke during the register phase (exit {broken.ExitCode}): {Truncate(broken.Stderr)}"),
            _ => (null, $"'{RegisterLanguage}' produced no register-phase outcome"),
        };

        if (registerFailure is not null)
        {
            return FailEveryLanguage(languages,
                $"S6 query's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return FailEveryLanguage(languages,
                $"S6 query's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language seeds one row carrying the run's marker ────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            var step = document.Steps.FirstOrDefault(s => s.Name == WriteStepName);
            if (step is null)
            {
                state.Assertions.Add(Assertion.Fail($"step '{WriteStepName}'",
                    "the driver reported no such step, so this language seeded no row for the query to match"));
                continue;
            }

            state.Assertions.Add(Assertion.From(
                $"step '{WriteStepName}' succeeded", step.Ok, step.Error ?? "ok"));
        }

        // The expected set: every key the write phase actually produced, across all writers. Taken
        // from the runner's accumulated key map rather than from any read-phase report, so the
        // content assertions grade against something the thing being judged did not produce.
        var expectedKeys = ExpectedKeys(runner.KeysByLanguage);

        // ── wait for the StarRocks projection before any client reads it ───────────────────────
        var marker = context.IdPrefix;
        var wait = await _waiter.WaitAsync(
            $"the {expectedKeys.Count} '{TypeName}' row(s) marked '{marker}'",
            async token =>
            {
                var visible = await CountVisibleAsync(marker, actingToken, token);
                return ProjectionReady(visible, expectedKeys.Count)
                    ? ProbeOutcome.Ready($"{visible} row(s) visible to Search")
                    : ProbeOutcome.NotYet($"{visible} of {expectedKeys.Count} row(s) visible to Search");
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
                    $"{language}: the seeded rows reached the projection within the bounded wait",
                    wait.TimeoutDetail));
            }

            return states.Select(kv => Cell(kv.Key, kv.Value)).ToList();
        }

        // ── read: every alive language issues the same search and the same aggregate ───────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Read, states, context, ct))
        {
            foreach (var assertion in Judge(language, expectedKeys, document))
                states[language].Assertions.Add(assertion);
        }

        return states.Select(kv => Cell(kv.Key, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>
    /// Judges one language's read phase against <paramref name="expectedKeys"/> — the row keys the
    /// write phase produced. Pure over reported data (no I/O), so every branch below is exercisable
    /// from a unit test.
    ///
    /// Every assertion fires unconditionally: a missing step, a failed step and a step reporting
    /// nothing usable each become an explicit failure naming its consequence, never a silent skip,
    /// so no QRY requirement can be discharged vacuously.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(
        string language, IReadOnlySet<Guid> expectedKeys, PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the QRY backstop (uncited by design — see the class doc comment) ──────────────────
        assertions.Add(Assertion.From(
            $"{language}: the run seeded at least one row for this query to match",
            expectedKeys.Count > 0,
            $"the write phase produced {expectedKeys.Count} row key(s); with none, an empty result " +
            "set and a zero aggregate would both compare equal to the expectation"));

        var searchStep = document.Steps.FirstOrDefault(s => s.Name == SearchStepName);
        assertions.Add(searchStep is null
            ? Assertion.Fail(
                $"{language}: a filtered search is reachable through the client's public API",
                $"the driver reported no '{SearchStepName}' step",
                Requirements.QrySearchReachable)
            : Assertion.From(
                $"{language}: a filtered search is reachable through the client's public API",
                searchStep.Ok,
                searchStep.Error ?? "ok",
                Requirements.QrySearchReachable));

        var returned = ReadKeys(searchStep is { Ok: true } ? searchStep.Entity : null);
        var missing = expectedKeys.Except(returned).ToList();
        var unexpected = returned.Except(expectedKeys).ToList();

        assertions.Add(Assertion.From(
            $"{language}: the filtered search returned exactly the seeded rows",
            missing.Count == 0 && unexpected.Count == 0,
            missing.Count == 0 && unexpected.Count == 0
                ? $"{returned.Count} row(s), matching the {expectedKeys.Count} the write phase seeded"
                : $"seeded-but-absent: [{Join(missing)}]; returned-but-unseeded: [{Join(unexpected)}]",
            Requirements.QrySearchReturnsExactlyMatchingRows));

        var aggregateStep = document.Steps.FirstOrDefault(s => s.Name == AggregateStepName);
        assertions.Add(aggregateStep is null
            ? Assertion.Fail(
                $"{language}: an aggregation over a filtered set is reachable through the client's public API",
                $"the driver reported no '{AggregateStepName}' step",
                Requirements.QryAggregateReachable)
            : Assertion.From(
                $"{language}: an aggregation over a filtered set is reachable through the client's public API",
                aggregateStep.Ok,
                aggregateStep.Error ?? "ok",
                Requirements.QryAggregateReachable));

        var value = ReadMetric(aggregateStep is { Ok: true } ? aggregateStep.Entity : null);

        assertions.Add(Assertion.From(
            $"{language}: the aggregate counted exactly the seeded rows",
            value is not null && value.Value == expectedKeys.Count,
            value is null
                ? "the driver reported no numeric metric value for the aggregate"
                : $"aggregate={value.Value}, seeded={expectedKeys.Count}",
            Requirements.QryAggregateCountsExactlyMatchingRows));

        return assertions;
    }

    /// <summary>
    /// The row keys a driver's search step reported, read out of its <c>entity</c>. All five
    /// drivers emit the same deliberately minimal projection —
    /// <c>{"keys":["&lt;uuid&gt;", ...]}</c> — so this reader needs no per-language special-casing.
    /// Keys are parsed as <see cref="Guid"/> so the five languages' UUID spellings are comparable;
    /// an unparsable or malformed document yields an empty set, which the exact set comparison
    /// then reports as every seeded row missing. Nothing here judges.
    /// </summary>
    internal static IReadOnlySet<Guid> ReadKeys(JsonElement? entity)
    {
        var keys = new HashSet<Guid>();
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return keys;

        if (!document.TryGetProperty("keys", out var keysElement) ||
            keysElement.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (var element in keysElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String &&
                Guid.TryParse(element.GetString(), out var parsed))
            {
                keys.Add(parsed);
            }
        }

        return keys;
    }

    /// <summary>
    /// The metric value a driver's aggregate step reported, out of <c>{"value": &lt;number&gt;}</c>.
    /// Null when the step reported no usable number at all — distinct from a reported zero, which
    /// is a real (and wrong, given a positive expected count) observation.
    /// </summary>
    internal static double? ReadMetric(JsonElement? entity)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return null;

        return document.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    /// <summary>
    /// Every <c>query_doc</c> key the write phase reported, across all languages, parsed as
    /// <see cref="Guid"/>. This is the harness's own accounting of what it seeded — the
    /// independent expectation both content assertions grade against.
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

    // ── the orchestrator's own probe of the projection ───────────────────────────────────────

    /// <summary>
    /// The projection-wait predicate, extracted so it is testable without a live stack: the wait is
    /// satisfied only when Search can already see at least as many marked rows as the write phase
    /// produced. <paramref name="expected"/> of zero is deliberately NOT ready — no language seeded
    /// anything, so satisfying the wait would let the read phase grade against nothing and the
    /// harness must instead report its own precondition failing.
    /// </summary>
    internal static bool ProjectionReady(int visible, int expected) =>
        expected > 0 && visible >= expected;

    /// <summary>
    /// Counts the rows the marker matches through the orchestrator's OWN <c>Search</c> call. This
    /// is the projection probe, not a conformance observation: nothing it returns is ever compared
    /// against a client's report, so a driver cannot manufacture readiness.
    /// </summary>
    private async Task<int> CountVisibleAsync(string marker, string actingToken, CancellationToken ct)
    {
        var request = new SearchRequest
        {
            TypeName = TypeName,
            PageSize = 100,
            Query = new SearchQuery
            {
                Logic = SearchLogic.And,
                Clauses =
                {
                    new SearchClause
                    {
                        Property = MarkerProperty,
                        Operator = SearchOperator.Equals,
                        ClauseType = SearchClauseType.Filter,
                        Value = new SearchValue { StringVal = marker },
                    },
                },
            },
        };

        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        using var call = search.Search(request, headers, cancellationToken: ct);

        var count = 0;
        while (await call.ResponseStream.MoveNext(ct))
            count++;

        return count;
    }

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

    // ── phase plumbing (mirrors InteropScenario's) ───────────────────────────────────────────

    private async Task<IReadOnlyList<(string Language, PhaseDocument Document)>> RunPhaseAsync(
        Phase phase, Dictionary<string, LanguageState> states, DriverContext context, CancellationToken ct)
    {
        var alive = Alive(states).ToList();
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
                        $"(exit {broken.ExitCode}): {Truncate(broken.Stderr)}", state.Assertions);
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

    private static IReadOnlyList<ReportCell> FailEveryLanguage(IReadOnlyCollection<string> languages, string detail) =>
        languages.Select(l => ReportCell.Fail(l, Name, detail, [])).ToList();

    private static IEnumerable<string> Alive(Dictionary<string, LanguageState> states) =>
        states.Where(kv => kv.Value.Terminal is null).Select(kv => kv.Key).ToList();

    private static ReportCell Cell(string language, LanguageState state)
    {
        if (state.Terminal is not null)
            return state.Terminal;

        var failures = state.Assertions.Where(a => !a.Passed).ToList();
        return failures.Count == 0
            ? ReportCell.Ok(language, Name, state.Assertions)
            : ReportCell.Fail(language, Name, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")), state.Assertions);
    }

    private static string Join(IEnumerable<Guid> keys) => string.Join(", ", keys.Select(k => k.ToString()));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    private sealed class LanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
