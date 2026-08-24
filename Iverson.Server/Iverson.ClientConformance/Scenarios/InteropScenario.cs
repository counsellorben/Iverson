using System.Text.Json;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S4 <c>interop</c>: proves the five client libraries can read each other's rows.
///
/// Only the .NET driver ever runs this scenario's <c>register</c> phase, regardless of which
/// languages were requested — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor
/// wholesale, so five registrations of <c>SharedAuthor</c>/<c>SharedArticle</c> would leave four
/// silent overwrites of the descriptor under test (the same register-once rule S1 applies per
/// type, here applied to the whole scenario). The orchestrator then re-registers both reported
/// descriptors with an authorization block exactly once, before any driver's <c>write</c> phase.
///
/// Every REQUESTED language then writes its own <c>SharedAuthor</c>/<c>SharedArticle</c> pair
/// under a run-scoped key, and every requested language's <c>read</c> phase iterates the full,
/// language-qualified <c>--keys</c> map (not just its own slice) to read every language's
/// <c>shared_article</c> row — five languages each reading up to five rows is the fan-out that
/// produces up to twenty-five reads. What is judged is cross-client agreement: for each row,
/// does every reader's client library report the same <c>SharedAuthorId</c> foreign key? A
/// mismatch here means two client libraries disagree about what the server's own row contains,
/// which no single-client scenario (S1-S3) can ever observe.
/// </summary>
public sealed class InteropScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    Action<string>? log = null)
{
    public const string Name = "interop";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>
    /// Fixed order for the writer/reader loops below, independent of <c>--languages</c>'s order,
    /// so a re-run with the same language set always attributes the same reader to "canonical".
    /// </summary>
    private static readonly IReadOnlyList<string> LanguagePriority =
        ["dotnet", "go", "java", "python", "typescript"];

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

        var (sharedAuthor, sharedArticle, registerFailure) = registerOutcome switch
        {
            DriverPhaseOutcome.Success success => TryCaptureDescriptors(success.Document),
            DriverPhaseOutcome.Skipped skipped => (null, null, $"'{RegisterLanguage}' driver skipped: {skipped.Reason}"),
            DriverPhaseOutcome.Broken broken => (null, null,
                $"'{RegisterLanguage}' driver broke during the register phase (exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}"),
            _ => (null, null, $"'{RegisterLanguage}' produced no register-phase outcome"),
        };

        if (registerFailure is not null)
        {
            // Every requested language's row depends on this single registration — a failure
            // here is not one language's problem, it is every language's, so it is reported that
            // way rather than as a cascade of unrelated-looking write-phase denials.
            return languages
                .Select(l => ReportCell.Fail(l, Name,
                    $"S4 interop's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}", []))
                .ToList();
        }

        // ── re-register once, with row permissions, before any driver's write phase ────────────
        foreach (var (label, descriptor) in new[]
                 { ("SharedAuthor", sharedAuthor), ("SharedArticle", sharedArticle) })
        {
            try
            {
                await reregistrar.ReregisterAsync(descriptor!.Json, actingToken, ct: ct);
            }
            catch (Exception ex)
            {
                return languages
                    .Select(l => ReportCell.Fail(l, Name,
                        $"S4 interop's one-time re-registration of '{label}' with row permissions failed: {Describe(ex)}", []))
                    .ToList();
            }
        }

        // ── write ────────────────────────────────────────────────────────────────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            RequireStepOk(state, document, "write_shared_author");
            RequireStepOk(state, document, "write_shared_article");
        }

        // ── read: every alive reader reads every alive writer's row ─────────────────────────────
        var readDocuments = (await RunPhaseAsync(Phase.Read, states, context, ct))
            .ToDictionary(x => x.Language, x => x.Document, StringComparer.OrdinalIgnoreCase);

        var orderedRequested = LanguagePriority
            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var aliveLanguages = new HashSet<string>(ScenarioCells.Alive(states), StringComparer.OrdinalIgnoreCase);
        var readerLanguages = new HashSet<string>(readDocuments.Keys, StringComparer.OrdinalIgnoreCase);

        var (writers, readers, pairs) = SelectWriterReaderPairs(
            orderedRequested, aliveLanguages, runner.KeysByLanguage, readerLanguages);

        // Non-vacuity guard — see NonVacuityFailure's doc comment for why this exists.
        var nonVacuityFailure = NonVacuityFailure(orderedRequested, aliveLanguages, writers, readers, pairs);
        if (nonVacuityFailure is not null)
        {
            foreach (var language in states.Keys)
            {
                states[language].Assertions.Add(Assertion.Fail(
                    "S4 interop: the cross-client fan-out is non-vacuous", nonVacuityFailure));
            }
        }

        foreach (var (reader, assertion) in ApplyFanOut(pairs, readDocuments))
            states[reader].Assertions.Add(assertion);

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── writer/reader selection (the RunAsync wiring, pulled out so it is unit-testable) ────────

    /// <summary>
    /// Decides which languages count as writers, which count as readers, and every
    /// (writer, reader) pair whose agreement <see cref="JudgeAgreement"/> must judge — the pure
    /// decision <c>RunAsync</c> otherwise made inline. A writer must both be alive (no earlier
    /// phase set its <see cref="LanguageState.Terminal"/>) AND have actually reported a
    /// <c>shared_article</c> key in <paramref name="keysByLanguage"/> — a language that merely
    /// survived the write phase without the driver reporting a key (e.g. the write itself
    /// silently produced no row) must not enter the fan-out as a writer with nothing to read. A
    /// reader only needs to have produced a read-phase document at all; <see cref="JudgeAgreement"/>
    /// itself judges whether that document actually contains the expected read step.
    ///
    /// Both lists preserve <see cref="LanguagePriority"/> order (already applied to
    /// <paramref name="orderedRequested"/>), so a re-run with the same language set always
    /// produces the same writer/reader ordering — the same determinism <c>JudgeAgreement</c>'s
    /// "first alive reader is canonical" rule depends on.
    /// </summary>
    internal static (IReadOnlyList<string> Writers, IReadOnlyList<string> Readers, IReadOnlyList<(string Writer, string Reader)> Pairs)
        SelectWriterReaderPairs(
            IReadOnlyList<string> orderedRequested,
            IReadOnlySet<string> aliveLanguages,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage,
            IReadOnlySet<string> readerLanguages)
    {
        var writers = orderedRequested
            .Where(l => aliveLanguages.Contains(l) &&
                        keysByLanguage.TryGetValue(l, out var keys) &&
                        keys.ContainsKey("shared_article"))
            .ToList();

        var readers = orderedRequested
            .Where(readerLanguages.Contains)
            .ToList();

        var pairs = new List<(string, string)>();
        foreach (var writer in writers)
            foreach (var reader in readers)
                pairs.Add((writer, reader));

        return (writers, readers, pairs);
    }

    /// <summary>
    /// Detects a silently vacuous or shrunk fan-out and returns the detail text to fail every
    /// language with, or <c>null</c> when the fan-out is healthy. A language can survive the write
    /// phase (alive, <c>write_shared_article</c> reported ok) yet still be silently dropped as a
    /// writer if its driver simply never reported a <c>shared_article</c> key (see
    /// <see cref="SelectWriterReaderPairs"/>'s doc comment) — a client regression, not a scenario
    /// bug. If that happens to some or all candidate writers, <see cref="JudgeAgreement"/> is never
    /// called for the missing rows and every affected cell would otherwise fall through to
    /// <see cref="ReportCell.Ok"/> on the write-step assertions alone, i.e. green cells having
    /// performed fewer — or zero — cross-client reads than the run actually attempted.
    /// </summary>
    internal static string? NonVacuityFailure(
        IReadOnlyList<string> orderedRequested,
        IReadOnlySet<string> aliveLanguages,
        IReadOnlyList<string> writers,
        IReadOnlyList<string> readers,
        IReadOnlyList<(string Writer, string Reader)> pairs)
    {
        var candidateWriters = orderedRequested.Where(aliveLanguages.Contains).ToList();
        if (candidateWriters.Count == 0)
            return null; // nothing was ever eligible to write — not this guard's concern.

        if (writers.Count >= candidateWriters.Count && pairs.Count > 0)
            return null; // healthy: every eligible writer entered the fan-out and it produced reads.

        var droppedWriters = candidateWriters
            .Except(writers, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return
            $"S4 interop's cross-client fan-out observed {pairs.Count} (writer, reader) pair(s) " +
            $"from {writers.Count}/{candidateWriters.Count} eligible writer(s) and {readers.Count} reader(s)" +
            (droppedWriters.Count > 0
                ? $"; dropped from the fan-out despite a successful write (no 'shared_article' key " +
                  $"reported): {string.Join(", ", droppedWriters)}"
                : "");
    }

    /// <summary>
    /// Consumes <see cref="SelectWriterReaderPairs"/>'s <c>Pairs</c> — the actual cross product,
    /// grouped by writer so <see cref="JudgeAgreement"/>'s "first alive reader is canonical" rule
    /// still applies per writer — and calls <see cref="JudgeAgreement"/> for every one of them.
    /// This is the piece <c>RunAsync</c> used to reimplement inline with its own writer/reader
    /// nested loop, discarding <c>Pairs</c> entirely; every pair assertion in
    /// <c>InteropScenarioTests</c> (the 25-pair cross product, the count, the content) pinned only
    /// <see cref="SelectWriterReaderPairs"/>'s pure output, never this consumption — so a version
    /// that silently collapsed the fan-out to a self-read-only zip (each writer reading only its
    /// own row) rendered a fully green suite while performing zero cross-client verification. Pulled
    /// out as its own pure, unit-testable method specifically so that regression can be pinned by a
    /// test that does not require a live stack.
    /// </summary>
    internal static IReadOnlyList<(string Reader, Assertion Assertion)> ApplyFanOut(
        IReadOnlyList<(string Writer, string Reader)> pairs,
        IReadOnlyDictionary<string, PhaseDocument> readDocuments)
    {
        var results = new List<(string, Assertion)>();
        foreach (var group in pairs.GroupBy(p => p.Writer, p => p.Reader))
            results.AddRange(JudgeAgreement(group.Key, group.ToList(), readDocuments));
        return results;
    }

    // ── the cross-client agreement check ─────────────────────────────────────────────────────

    /// <summary>
    /// Judges one writer's row: every reader's <c>read_shared_article_{writer}</c> step must
    /// exist, report <c>ok:true</c>, and carry a <c>SharedAuthorId</c> that agrees with the
    /// group's canonical value — the first alive reader's own reading, in
    /// <see cref="LanguagePriority"/> order, so a re-run with the same language set always picks
    /// the same reader as canonical. A pure function over reported data, exactly like
    /// <c>NavPropertyRejectedScenario.Judge</c>, so the agreement rule is unit-testable without a
    /// live stack — but this is a HELPER, not a substitute for exercising <see cref="RunAsync"/>
    /// itself: this method never decides which readers/writers exist or what a driver's own
    /// register-phase failure means, so a regression in that surrounding logic would still slip a
    /// green suite that tested only this method.
    /// </summary>
    internal static IReadOnlyList<(string Reader, Assertion Assertion)> JudgeAgreement(
        string writer, IReadOnlyList<string> readers, IReadOnlyDictionary<string, PhaseDocument> readDocuments)
    {
        var results = new List<(string, Assertion)>();
        ObservedValue? canonical = null;

        foreach (var reader in readers)
        {
            var document = readDocuments[reader];
            var stepName = $"read_shared_article_{writer}";
            var step = document.Steps.FirstOrDefault(s => s.Name == stepName);

            if (step is null)
            {
                results.Add((reader, Assertion.Fail(
                    $"shared_article[{writer}] read by {reader}", "the driver reported no such read step")));
                continue;
            }

            if (!step.Ok)
            {
                results.Add((reader, Assertion.Fail(
                    $"shared_article[{writer}] read by {reader}", step.Error ?? "the driver reported the read as failed")));
                continue;
            }

            var fk = Verifier.FromJson(step.Entity, "SharedAuthorId");
            canonical ??= fk;
            var canonicalValue = canonical!;

            results.Add((reader, Assertion.From(
                $"shared_article[{writer}].SharedAuthorId: read by {reader} agrees with the rest of the group",
                fk.Uuids is { Count: > 0 } && fk.Matches(canonicalValue),
                $"reader={reader} value={fk} groupCanonical={canonicalValue}")));
        }

        return results;
    }

    // ── register-phase descriptor capture ────────────────────────────────────────────────────

    internal static (CapturedDescriptor? Author, CapturedDescriptor? Article, string? Failure) TryCaptureDescriptors(
        PhaseDocument document)
    {
        var authorStep = document.Steps.FirstOrDefault(s => s.Name == "register_shared_author");
        var articleStep = document.Steps.FirstOrDefault(s => s.Name == "register_shared_article");

        if (authorStep is null || articleStep is null)
            return (null, null,
                "the dotnet driver reported no 'register_shared_author'/'register_shared_article' step");

        if (!authorStep.Ok || !articleStep.Ok)
            return (null, null, authorStep.Error ?? articleStep.Error ?? "registration failed");

        if (authorStep.TypeDescriptor is not { } authorJson || articleStep.TypeDescriptor is not { } articleJson)
            return (null, null, "typeDescriptor was null on a register step");

        try
        {
            var author = Verifier.ParseDescriptor(authorJson);
            var article = Verifier.ParseDescriptor(articleJson);
            return (new CapturedDescriptor(author, authorJson), new CapturedDescriptor(article, articleJson), null);
        }
        catch (Exception ex)
        {
            return (null, null, Describe(ex));
        }
    }

    // ── phase plumbing (mirrors CrudRoundtripScenario's) ────────────────────────────────────────

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

    private static StepResult? RequireStepOk(LanguageState state, PhaseDocument document, string stepName)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == stepName);
        if (step is null)
        {
            state.Assertions.Add(Assertion.Fail($"step '{stepName}'", "the driver reported no such step"));
            return null;
        }

        state.Assertions.Add(Assertion.From(
            $"step '{stepName}' succeeded", step.Ok, step.Error ?? "ok"));
        return step.Ok ? step : null;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed record CapturedDescriptor(TypeDescriptor Descriptor, JsonElement Json);

    private sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
