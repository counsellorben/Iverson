using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S13 <c>match-pattern</c>: proves each client library can express a row pattern match
/// (<c>MatchPattern</c>, SQL:2016 <c>MATCH_RECOGNIZE</c>) through its OWN builder API, and that all
/// five agree on what comes back for the same two patterns over the same seeded rows.
///
/// The shape follows S7 vector-search's, for the same reasons: the subject is one shared type
/// (<c>PatternDoc</c>) that every language writes into and every language then matches, so
/// disagreement between two client libraries is observable. Only the .NET driver ever runs the
/// register phase — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor wholesale —
/// and the orchestrator re-registers the reported descriptor once with an authorization block
/// before any write, without which every seeded write is denied.
///
/// <para><b>The fixture.</b> Every language writes THREE rows — one partition — stamped with the
/// run's <c>--id-prefix</c> as <c>Marker</c>, its own <see cref="LabelFor"/> as <c>Label</c>, and
/// <c>Seq</c> 1, 2 and 3, reporting their server-assigned keys as <c>pattern_doc_1</c>..<c>3</c>.
/// Both read steps filter on the marker and partition by <c>Label</c> ordered by <c>Seq</c>, so
/// each seeded language is exactly one partition and no earlier run's rows can match.</para>
///
/// <para><b>The two patterns.</b> <c>match_pattern_scalar</c> is <c>A B+</c> with
/// <c>B AS Seq &gt; PREV(Seq)</c>, one row per match, measuring <c>n = COUNT(*)</c>,
/// <c>first_seq = FIRST(A.Seq)</c> and <c>last_seq = LAST(B.Seq)</c>: over one strictly increasing
/// partition of three rows that is exactly one match with <c>n = 3, first_seq = 1, last_seq = 3</c>
/// — values that depend on the filter, the partitioning AND the ordering all being honoured.
/// <c>match_pattern_similarity</c> is <c>A+</c> with <c>A AS SIMILARITY(Title, '…') IS NOT NULL</c>,
/// all rows per match, measuring the score <c>s</c>: every seeded row is scored and returned under
/// classifier <c>A</c> in match 1. Scores are never compared to absolute values — only finiteness
/// and the cosine range — because they belong to the embedding model, not to any client.</para>
///
/// <para><b>The expected sets are the harness's own accounting.</b> Both content assertions grade
/// against what the WRITE phase reported (<c>DriverRunner.KeysByLanguage</c>, read by
/// <see cref="ExpectedRows"/>), never against anything the read phase being judged reported.</para>
///
/// <para><b>The projection wait.</b> A TYPE_ROWS match reads its rows from StarRocks and resolves
/// <c>SIMILARITY</c> against the Qdrant object vectors, and a mapped write reaches both
/// asynchronously through the outbox — Qdrant only after the embedding model has vectorized
/// <c>Title</c>. Between the write and read phases the orchestrator polls its OWN
/// <c>MatchPattern</c> probe, whose pattern matches a row only once it is both visible and scoreable
/// and which reports each such row's <c>Id</c>, until every seeded row's key is among them. The check
/// is a superset, not an exact count: rows written under the marker by a language that did not
/// report all three keys are not expected, and must not stall every other language's wait. Expiry
/// is reported as a failed step on every language, worded as the harness's own precondition failing
/// and carrying no requirement ID.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the run seeded at least one row for
/// these pattern queries to match" assertion is this scenario's backstop, exactly as
/// <see cref="QueryScenario"/>'s is for the rest of the QRY axis: with an empty expectation both
/// content comparisons would agree with an empty result. It carries no requirement ID.</para>
/// </summary>
public sealed class MatchPatternScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    ObjectSearchService.ObjectSearchServiceClient search,
    ProjectionWaiter? waiter = null,
    Action<string>? log = null)
{
    public const string Name = "match-pattern";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and matches. Relation-free on purpose.</summary>
    internal const string TypeName = "PatternDoc";

    /// <summary>The <c>[IversonMetadata]</c> property every pattern query filters on.</summary>
    internal const string MarkerProperty = "Marker";

    /// <summary>The integer property every pattern query orders by.</summary>
    internal const string SeqProperty = "Seq";

    /// <summary>
    /// The query text of every <c>SIMILARITY</c> term — the drivers' similarity step and the
    /// orchestrator's probe alike. Each seeded <c>Title</c> is <c>"a note about row pattern
    /// matching, part &lt;n&gt;"</c>, so the text is close to every row without equalling any.
    /// </summary>
    internal const string SimilarityText = "a note about row pattern matching";

    /// <summary>The <c>SIMILARITY</c> term, spelled exactly as the drivers spell it.</summary>
    internal const string SimilarityExpression = "SIMILARITY(Title, '" + SimilarityText + "')";

    /// <summary>The logical key names every driver reports its three seeded rows under, Seq 1..3.</summary>
    internal static readonly string[] RowKeyNames = ["pattern_doc_1", "pattern_doc_2", "pattern_doc_3"];

    internal const string RegisterStepName = "register_pattern_doc";
    internal const string WriteStepName = "write_pattern_docs";
    internal const string ScalarStepName = "match_pattern_scalar";
    internal const string SimilarityStepName = "match_pattern_similarity";

    /// <summary>
    /// The <c>Label</c> value a given language's driver stamps on its three rows — and therefore the
    /// partition key that language's rows form. Spelled once here and mirrored in each driver.
    /// </summary>
    internal static string LabelFor(string language) => $"pat-{language}";

    /// <summary>
    /// Why the wait is as patient as vector-search's: the probe is gated on the same embedding work
    /// (a row is scoreable only once <c>Title</c> is vectorized), and each attempt embeds the query
    /// text through the same backend that work is using.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(6);

    /// <summary>Why a MatchPattern read lags a write — the wait's <c>StoreExplanation</c>.</summary>
    internal const string StoreExplanation =
        "MatchPattern reads TYPE_ROWS from the StarRocks projection and scores SIMILARITY against " +
        "the Qdrant object vectors, both of which a mapped write reaches asynchronously through the " +
        "outbox — Qdrant only after the embedding model has vectorized the embedded field";

    private readonly ProjectionWaiter _waiter = waiter ?? new ProjectionWaiter(
        WaitTimeout, WaitInterval, StoreExplanation);

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
                $"S13 match-pattern's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S13 match-pattern's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language seeds one three-row partition carrying the marker ──
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            var step = document.Steps.FirstOrDefault(s => s.Name == WriteStepName);
            if (step is null)
            {
                state.Assertions.Add(Assertion.Fail($"step '{WriteStepName}'",
                    "the driver reported no such step, so this language seeded no rows for these patterns to match"));
                continue;
            }

            state.Assertions.Add(Assertion.From(
                $"step '{WriteStepName}' succeeded", step.Ok, step.Error ?? "ok"));
        }

        // The expectation: taken from the runner's accumulated key map rather than from any
        // read-phase report, so the content assertions grade against something the thing being
        // judged did not produce.
        var expectedRows = ExpectedRows(runner.KeysByLanguage);
        var expectedKeys = expectedRows.Keys.ToHashSet();

        // ── wait until every seeded row is visible AND scoreable ───────────────────────────────
        var marker = context.IdPrefix;
        var wait = await _waiter.WaitAsync(
            $"the {expectedKeys.Count} '{TypeName}' row(s) marked '{marker}' (rows and their Title vectors)",
            async token =>
            {
                var scoreable = await ScoreableIdsAsync(marker, actingToken, token);
                var missing = MissingFromProbe(expectedKeys, scoreable);
                return ProjectionReady(expectedKeys, scoreable)
                    ? ProbeOutcome.Ready(
                        $"all {expectedKeys.Count} seeded row(s) scoreable ({scoreable.Count} marked row(s) visible)")
                    : ProbeOutcome.NotYet(
                        $"{missing} of {expectedKeys.Count} seeded row(s) not yet scoreable " +
                        $"({scoreable.Count} marked row(s) visible)");
            },
            ct);

        log?.Invoke($"  projection wait: {(wait.Satisfied ? "satisfied" : "TIMED OUT")} " +
                    $"after {wait.Elapsed.TotalSeconds:0.0}s over {wait.Attempts} attempt(s) — {wait.LastDetail}");

        if (!wait.Satisfied)
        {
            // A shared precondition, so it fails every row: running the read phase after it would
            // grade five client libraries on rows the stores cannot yet see.
            foreach (var (language, state) in states)
            {
                if (state.Terminal is not null) continue;
                state.Assertions.Add(Assertion.Fail(
                    $"{language}: the seeded rows reached both stores within the bounded wait",
                    wait.TimeoutDetail));
            }

            return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
        }

        // ── read: every alive language issues the same two pattern queries ─────────────────────
        return GradeReads(states, await RunPhaseAsync(Phase.Read, states, context, ct), expectedRows);
    }

    /// <summary>
    /// Wires the read phase's documents through <see cref="Judge"/> and into cells. Extracted from
    /// <see cref="RunAsync"/> — and internal — because the wiring is exactly as safety-critical as
    /// the judgement: drop the <see cref="Judge"/> call below and every QRY-005..007 assertion
    /// silently stops reaching a cell. That mutation must redden a named test.
    /// </summary>
    internal static IReadOnlyList<ReportCell> GradeReads(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> reads,
        IReadOnlyDictionary<Guid, string> expectedRows)
    {
        foreach (var (language, document) in reads)
            states[language].Assertions.AddRange(Judge(language, expectedRows, document));

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>One reported output row: <c>{"matchNumber":…,"classifier":"…","data":{…}}</c>.</summary>
    internal sealed record PatternRow(double MatchNumber, string Classifier, JsonElement Data);

    /// <summary>
    /// Judges one language's read phase against the write phase's accounting,
    /// <paramref name="expectedRows"/> (every seeded row key → the language that wrote it). Pure
    /// over reported data (no I/O), so every branch below is exercisable from a unit test.
    ///
    /// Every assertion fires unconditionally: a missing step, a failed step and a malformed report
    /// each become an explicit failure naming its consequence, never a silent skip, so no QRY
    /// requirement can be discharged vacuously.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(
        string language,
        IReadOnlyDictionary<Guid, string> expectedRows,
        PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the backstop (uncited by design — see the class doc comment) ──────────────────────
        assertions.Add(Assertion.From(
            $"{language}: the run seeded at least one row for these pattern queries to match",
            expectedRows.Count > 0,
            $"the write phase produced {expectedRows.Count} complete row key(s); with none, an empty " +
            "scalar result and an empty similarity result would both compare equal to the expectation"));

        var scalarStep = document.Steps.FirstOrDefault(s => s.Name == ScalarStepName);
        var similarityStep = document.Steps.FirstOrDefault(s => s.Name == SimilarityStepName);

        assertions.Add(Reachable(language, "scalar", ScalarStepName, scalarStep));
        assertions.Add(Reachable(language, "SIMILARITY", SimilarityStepName, similarityStep));
        assertions.Add(JudgeScalar(language, expectedRows, scalarStep));
        assertions.Add(JudgeSimilarity(language, expectedRows, similarityStep));

        return assertions;
    }

    private static Assertion Reachable(string language, string which, string stepName, StepResult? step)
    {
        var name = $"{language}: a {which} row pattern match is reachable through the client's public API";
        return step is null
            ? Assertion.Fail(name, $"the driver reported no '{stepName}' step", Requirements.QryMatchPatternReachable)
            : Assertion.From(name, step.Ok, step.Error ?? "ok", Requirements.QryMatchPatternReachable);
    }

    /// <summary><c>IVC-QRY-006</c>: exactly one match per seeded language, with the right measures.</summary>
    private static Assertion JudgeScalar(
        string language, IReadOnlyDictionary<Guid, string> expectedRows, StepResult? step)
    {
        var name = $"{language}: the scalar row pattern returned exactly one expected match per seeded language";
        var (rows, unusable) = UsableRows(ScalarStepName, step);
        if (rows is null)
            return Assertion.Fail(name, unusable, Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

        var expectedLabels = ExpectedLabels(expectedRows);
        var problems = new List<string>();

        foreach (var label in expectedLabels.Order(StringComparer.Ordinal))
        {
            var matches = rows.Where(r => StringField(r.Data, "Label") == label).ToList();
            if (matches.Count != 1)
            {
                problems.Add($"'{label}': {matches.Count} match(es), expected exactly 1");
                continue;
            }

            var match = matches[0];
            if (match.MatchNumber != 1)
                problems.Add($"'{label}': matchNumber {match.MatchNumber}, expected 1");

            foreach (var (measure, expected) in new[] { ("n", 3.0), ("first_seq", 1.0), ("last_seq", 3.0) })
            {
                var actual = NumberField(match.Data, measure);
                if (actual != expected)
                {
                    problems.Add(actual is null
                        ? $"'{label}': measure '{measure}' is absent or not a number, expected {expected}"
                        : $"'{label}': measure '{measure}' = {actual}, expected {expected}");
                }
            }
        }

        var unexpected = rows
            .Select(r => StringField(r.Data, "Label") ?? "(no Label)")
            .Where(l => !expectedLabels.Contains(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unexpected.Count > 0)
            problems.Add($"returned-but-unseeded label(s): [{string.Join(", ", unexpected)}]");

        return Assertion.From(
            name,
            problems.Count == 0,
            problems.Count == 0
                ? $"{rows.Count} match(es), one per seeded language, each with n = 3, first_seq = 1, last_seq = 3"
                : string.Join("; ", problems),
            Requirements.QryMatchPatternReturnsExactlyExpectedMatches);
    }

    /// <summary>
    /// <c>IVC-QRY-007</c>: exactly the seeded rows, each classified <c>A</c> in match 1, carrying a
    /// finite score in [-1, 1], under the label of the language that wrote it.
    /// </summary>
    private static Assertion JudgeSimilarity(
        string language, IReadOnlyDictionary<Guid, string> expectedRows, StepResult? step)
    {
        var name = $"{language}: the SIMILARITY row pattern scored exactly the seeded rows";
        var (rows, unusable) = UsableRows(SimilarityStepName, step);
        if (rows is null)
            return Assertion.Fail(name, unusable, Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);

        var problems = new List<string>();
        var returned = new HashSet<Guid>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = Guid.TryParse(StringField(row.Data, "Id"), out var parsed) ? parsed : (Guid?)null;
            var where = id is { } k ? $"row {i} (Id {k})" : $"row {i}";

            if (id is null)
                problems.Add($"{where}: 'Id' is absent or not a UUID");
            else if (!returned.Add(id.Value))
                problems.Add($"{where}: returned more than once");

            if (row.Classifier != "A")
                problems.Add($"{where}: classifier '{row.Classifier}', expected 'A'");

            if (row.MatchNumber != 1)
                problems.Add($"{where}: matchNumber {row.MatchNumber}, expected 1");

            if (ScoreProblem(row.Data) is { } scoreProblem)
                problems.Add($"{where}: {scoreProblem}");

            if (id is { } key && expectedRows.TryGetValue(key, out var writer)
                && StringField(row.Data, "Label") is var label && label != LabelFor(writer))
            {
                problems.Add($"{where}: Label '{label ?? "(none)"}', but '{LabelFor(writer)}' wrote that row");
            }
        }

        var missing = expectedRows.Keys.Where(k => !returned.Contains(k)).ToList();
        var unexpected = returned.Where(k => !expectedRows.ContainsKey(k)).ToList();
        if (missing.Count > 0 || unexpected.Count > 0)
            problems.Add($"seeded-but-absent: [{Join(missing)}]; returned-but-unseeded: [{Join(unexpected)}]");

        return Assertion.From(
            name,
            problems.Count == 0,
            problems.Count == 0
                ? $"{rows.Count} row(s), matching the {expectedRows.Count} the write phase seeded, each scored"
                : string.Join("; ", problems),
            Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);
    }

    /// <summary>
    /// A score is a JSON number that is finite and a cosine. System.Text.Json refuses to read an
    /// overflowing literal such as <c>1e400</c> as a double, and a driver serializing NaN or an
    /// infinity must spell it as a string — so "not a finite number" covers all three.
    /// </summary>
    private static string? ScoreProblem(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var s))
            return "has no score 's'";

        if (s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out var score) || !double.IsFinite(score))
            return $"score 's' is not a finite number (got {s.GetRawText()})";

        return score is < -1 or > 1 ? $"score 's' = {score} is outside [-1, 1]" : null;
    }

    /// <summary>The step's rows, or why there are none to judge.</summary>
    private static (IReadOnlyList<PatternRow>? Rows, string Unusable) UsableRows(string stepName, StepResult? step)
    {
        if (step is null)
            return (null, $"the driver reported no '{stepName}' step, so there is no result to judge");

        if (!step.Ok)
            return (null, $"the '{stepName}' step failed, so there is no result to judge: {step.Error ?? "(no error)"}");

        var (rows, problem) = ReadRows(step.Entity);
        return rows is null ? (null, $"the '{stepName}' step's report is malformed: {problem}") : (rows, "");
    }

    /// <summary>
    /// The output rows a driver's read step reported, in stream order, out of
    /// <c>{"rows":[{"matchNumber":…,"classifier":"…","data":{…}}, …]}</c>. All five drivers emit this
    /// same shape, with <c>data</c>'s keys verbatim from the server. Unlike the vector-search
    /// readers this one does not degrade to an empty set: a malformed report yields
    /// <c>Rows = null</c> and names the malformation, because an empty result is a legitimate
    /// (and gradeable) answer while an unreadable one is not. Nothing here judges.
    /// </summary>
    internal static (IReadOnlyList<PatternRow>? Rows, string? Problem) ReadRows(JsonElement? entity)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return (null, "the entity is absent or not a JSON object");

        if (!document.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array)
            return (null, "the entity has no 'rows' array");

        var rows = new List<PatternRow>();
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return (null, $"rows[{i}] is not an object");

            if (!element.TryGetProperty("matchNumber", out var matchNumber)
                || matchNumber.ValueKind != JsonValueKind.Number
                || !matchNumber.TryGetDouble(out var number))
            {
                return (null, $"rows[{i}] has no numeric 'matchNumber'");
            }

            if (!element.TryGetProperty("classifier", out var classifier) || classifier.ValueKind != JsonValueKind.String)
                return (null, $"rows[{i}] has no string 'classifier'");

            if (!element.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (null, $"rows[{i}] has no 'data' object");

            rows.Add(new PatternRow(number, classifier.GetString()!, data));
            i++;
        }

        return (rows, null);
    }

    private static string? StringField(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? NumberField(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                                 && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>
    /// Every seeded row key → the language that wrote it, for each language whose write phase
    /// reported ALL THREE <see cref="RowKeyNames"/> as UUIDs. A language that reported fewer did not
    /// seed its partition, so it is expected in neither comparison. Keys are parsed as
    /// <see cref="Guid"/> so the five languages' UUID spellings are comparable. This is the
    /// harness's own accounting of what it seeded — the independent expectation both content
    /// assertions grade against.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, string> ExpectedRows(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage)
    {
        var rows = new Dictionary<Guid, string>();
        foreach (var (language, byName) in keysByLanguage)
        {
            var keys = new List<Guid>();
            foreach (var keyName in RowKeyNames)
            {
                if (byName.TryGetValue(keyName, out var raw) && Guid.TryParse(raw, out var parsed))
                    keys.Add(parsed);
            }

            if (keys.Count != RowKeyNames.Length)
                continue;

            foreach (var key in keys)
                rows[key] = language;
        }

        return rows;
    }

    private static IReadOnlySet<string> ExpectedLabels(IReadOnlyDictionary<Guid, string> expectedRows) =>
        expectedRows.Values.Select(LabelFor).ToHashSet(StringComparer.Ordinal);

    // ── the orchestrator's own probe ─────────────────────────────────────────────────────────

    /// <summary>
    /// The projection-wait predicate: ready when every expected key is among the ids the probe
    /// returned. A SUPERSET check, not an exact count — the probe also sees rows written under the
    /// run marker by a language that did not report all three keys (so is not expected), and those
    /// must not stall every other language's wait. <paramref name="expected"/> empty is deliberately
    /// NOT ready — no language seeded anything, so satisfying the wait would let the read phase
    /// grade against nothing and the harness must instead report its own precondition failing.
    /// </summary>
    internal static bool ProjectionReady(IReadOnlyCollection<Guid> expected, IReadOnlySet<Guid> visible) =>
        expected.Count > 0 && MissingFromProbe(expected, visible) == 0;

    /// <summary>How many expected keys the probe has not yet returned — the wait's progress detail.</summary>
    internal static int MissingFromProbe(IReadOnlyCollection<Guid> expected, IReadOnlySet<Guid> visible) =>
        expected.Count(key => !visible.Contains(key));

    /// <summary>
    /// The ids of the marked rows that are visible AND scoreable, through the orchestrator's OWN
    /// <c>MatchPattern</c> call: pattern <c>A</c> with <c>A AS SIMILARITY(…) IS NOT NULL</c>, one
    /// row per match, measuring <c>id = FIRST(A.Id)</c>, so each response is one row whose Title
    /// vector exists and carries that row's <c>Id</c>. This is a projection probe, not a
    /// conformance observation: nothing it returns is ever compared against a client's report, so
    /// a driver cannot manufacture readiness.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ScoreableIdsAsync(string marker, string actingToken, CancellationToken ct)
    {
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        using var call = search.MatchPattern(ProbeRequest(marker), headers, cancellationToken: ct);

        var ids = new HashSet<Guid>();
        while (await call.ResponseStream.MoveNext(ct))
        {
            if (call.ResponseStream.Current.Data?.Fields.TryGetValue("id", out var id) == true
                && Guid.TryParse(id.StringValue, out var parsed))
            {
                ids.Add(parsed);
            }
        }

        return ids;
    }

    /// <summary>
    /// The projection probe's request. The marker filter bounds it to the rows this run wrote, so
    /// its <c>Limit</c> only has to cover those, and must stay well under the server's
    /// <c>MaxOutputRows</c> cap: a limit above a deployment's cap is refused with
    /// <c>InvalidArgument</c>, and the probe would then fail on every attempt.
    /// </summary>
    internal static MatchPatternRequest ProbeRequest(string marker) => new()
    {
        TypeName = TypeName,
        Source = PatternRowSource.TypeRows,
        Where =
        {
            new SearchClause
            {
                Property = MarkerProperty,
                Operator = SearchOperator.Equals,
                ClauseType = SearchClauseType.Filter,
                Value = new SearchValue { StringVal = marker },
            },
        },
        OrderBy = { new SearchSort { Property = SeqProperty } },
        Pattern = "A",
        Define = { new NamedExpr { Name = "A", Expr = SimilarityExpression + " IS NOT NULL" } },
        Measures = { new NamedExpr { Name = "id", Expr = "FIRST(A.Id)" } },
        RowsPerMatch = RowsPerMatch.OneRow,
        Limit = 100,
    };

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

    // ── phase plumbing (mirrors VectorSearchScenario's) ──────────────────────────────────────

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
