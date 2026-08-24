using System.Text.Json;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S9 <c>error-contract</c>: proves each client library surfaces the server's error contract to
/// its caller, in both of the two shapes that contract has.
///
/// <para><b>Two shapes, not one.</b> A schema-rule violation is a gRPC status: the call fails and
/// the code and its detail are what the caller sees. An absent row is not — <c>Mapping.Get</c>
/// returns a SUCCESSFUL RPC carrying <c>MappingResponse { Success = false, Error = "'{type}:{key}'
/// not found." }</c> (<c>ObjectMappingGrpcService.Get</c>), which each client must render as
/// absence through its own read API. A library can get either shape right while getting the other
/// wrong, which is why the axis authors them as separate requirements.</para>
///
/// <para><b>What this scenario adds, and what it deliberately does not.</b> The registration and
/// write-payload rejections that <see cref="Requirements.ErrRegistrationRejectionIsInvalidArgument"/>
/// and <see cref="Requirements.ErrWriteRejectionIsInvalidArgument"/> name are already observed by
/// <see cref="NamingRejectedScenario"/> and <see cref="NavPropertyRejectedScenario"/>; those
/// assertions are CITED by the ERR consts rather than duplicated here, so one server regression
/// produces one red cell rather than two. This scenario covers only the two error classes nothing
/// else observed — an absent-row read and a write against a type with no registered schema — and
/// it covers them through the five CLIENT libraries, which is what the two orchestrator-only
/// scenarios above structurally cannot do.</para>
///
/// <para><b>The shape follows S8 identity's</b>, for the same reasons: one shared type
/// (<see cref="TypeName"/>) that every language writes into and reads back, registered once by the
/// .NET driver (<c>SchemaRegistry.RegisterAsync</c> replaces a stored descriptor wholesale, so five
/// registrations of one type name would leave four silent overwrites) and re-registered once by the
/// orchestrator with an authorization block, without which every seeded write is denied for a reason
/// that has nothing to do with the error contract.</para>
///
/// <para><b>The unregistered type.</b> <see cref="UnregisteredTypeName"/> is declared by all five
/// drivers through their own client libraries and registered by nothing — no driver, no scenario,
/// no orchestrator, in this run or any other. <c>ObjectMappingGrpcService.Post</c> calls
/// <c>RequireSchema</c> before authorization and before relation validation, so the
/// <c>FailedPrecondition</c> it produces is attributable to the missing schema and to no other
/// rule. Its detail is asserted as well as its code
/// (<see cref="Requirements.ErrMessageNamesOffendingElement"/>): that is the one message-preservation
/// observation in the harness made through a client library rather than the orchestrator's own
/// channel, so it is what proves the five clients hand the server's detail to the caller intact.</para>
///
/// <para><b>Status codes are compared numerically</b>, never by name — the five languages spell the
/// same code five ways (<c>FailedPrecondition</c>, <c>FAILED_PRECONDITION</c>, <c>9</c>) and a name
/// comparison would report a spelling difference as a conformance failure. Same rule S8 applies.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the same mapped read path finds the row
/// this run seeded" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Reporting
/// absence is exactly what a totally broken read path also does — a dropped acting-user header, an
/// unregistered type, a mangled key — so without a positive control
/// <see cref="Requirements.ErrAbsentRowReadReportsAbsence"/> would be green for a client that finds
/// nothing ever. The control uses the SAME client method, the SAME type and the SAME acting user,
/// differing only in which key is asked for. It fires unconditionally, on every language, before and
/// outside the absence assertions, and carries no requirement ID: "a row that exists is found" is
/// <c>LIFE</c>'s claim, not an <c>IVC-ERR-*</c> statement, and it is strictly weaker than
/// <see cref="Requirements.ErrAbsentRowReadReportsAbsence"/> wherever that can fail.</para>
/// </summary>
public sealed class ErrorContractScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    Action<string>? log = null)
{
    public const string Name = "error-contract";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and reads back. Relation-free on purpose.</summary>
    internal const string TypeName = "ErrorDoc";

    /// <summary>
    /// The type every driver declares and NOTHING ever registers. Spelled once here and mirrored in
    /// each of the five drivers. It must never be registered by any scenario or driver: the whole
    /// observation is that <c>RequireSchema</c> finds no schema for it.
    /// </summary>
    internal const string UnregisteredTypeName = "ErrorUnregisteredDoc";

    /// <summary>The logical key name every driver reports its seeded row under.</summary>
    internal const string RowKeyName = "error_doc";

    /// <summary>
    /// The numeric gRPC status code a write against an unregistered type must produce:
    /// <c>FAILED_PRECONDITION</c>. Numeric because the five languages spell it five ways.
    /// </summary>
    internal const int UnregisteredStatusCode = 9;

    internal const string RegisterStepName = "register_error_doc";
    internal const string WriteStepName = "write_error_doc";
    internal const string PresentStepName = "read_present_row";
    internal const string MissingStepName = "read_missing_row";
    internal const string UnregisteredStepName = "write_unregistered_type";

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
                $"S9 error-contract's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S9 error-contract's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language seeds the one row its positive control reads back ──
        await RunPhaseAsync(Phase.Write, states, context, ct);

        // ── read: the positive control, the absent-key read, and the unregistered-type write ───
        return GradeReads(states, await RunPhaseAsync(Phase.Read, states, context, ct));
    }

    /// <summary>
    /// Wires the read phase's documents through <see cref="Judge"/> and into cells. Extracted from
    /// <see cref="RunAsync"/> — and internal — for one reason: the wiring is exactly as
    /// safety-critical as the judgement, and it used to be reachable only from a live stack. Drop
    /// the <see cref="Judge"/> call below and every ERR assertion silently stops reaching a cell,
    /// leaving a fully green error-contract row that verified nothing. That mutation must redden a
    /// named test, so the wiring lives here where a test can call it.
    /// </summary>
    internal IReadOnlyList<ReportCell> GradeReads(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> reads)
    {
        foreach (var (language, document) in reads)
        {
            states[language].Assertions.AddRange(
                Judge(language, SeededKey(runner.KeysByLanguage, language), document));
        }

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>
    /// Judges one language's read phase. Pure over reported data (no I/O), so every branch below is
    /// exercisable from a unit test. Drivers report; they never judge — every status code and
    /// found/not-found flag below is data the driver observed, graded here.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(string language, Guid? seededKey, PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the ERR backstop (uncited by design — see the class doc comment) ──────────────────
        var presentStep = document.Steps.FirstOrDefault(s => s.Name == PresentStepName);
        var presentFound = presentStep is { Ok: true } && ReadBool(presentStep.Entity, "found") == true;

        assertions.Add(Assertion.From(
            $"{language}: the same mapped read path finds the row this run seeded",
            presentFound && seededKey is not null,
            presentStep is null
                ? $"the driver reported no '{PresentStepName}' step"
                : !presentStep.Ok
                    ? presentStep.Error ?? "the positive-control read failed"
                    : seededKey is null
                        ? "the write phase reported no row key for this language, so the control read " +
                          "had no existing row to find and the absence assertions below would be " +
                          "satisfied by a read path that finds nothing ever"
                        : presentFound
                            ? $"row '{seededKey}' was found through the same method the absent-key read uses"
                            : $"row '{seededKey}' was seeded but the same read path did not find it"));

        // ── IVC-ERR-004: an absent key reports absence, and does so without a status ──────────
        var missingStep = document.Steps.FirstOrDefault(s => s.Name == MissingStepName);
        var missingFound = missingStep is { Ok: true } ? ReadBool(missingStep.Entity, "found") : null;
        var missingCode = missingStep is { Ok: true } ? IdentityScenario.ReadStatusCode(missingStep.Entity) : null;

        assertions.Add(Assertion.From(
            $"{language}: a mapped read of a key with no row reports absence rather than an entity",
            missingFound == false,
            missingStep is null
                ? $"the driver reported no '{MissingStepName}' step"
                : !missingStep.Ok
                    ? missingStep.Error ?? "the absent-key read step failed"
                    : missingFound is null
                        ? "the driver reported no found/not-found flag, which is what it reports when " +
                          "its client library raised rather than returning"
                        : missingFound == true
                            ? "the driver's client library returned an entity for a key no row exists under"
                            : "the client library reported absence",
            Requirements.ErrAbsentRowReadReportsAbsence));

        assertions.Add(Assertion.From(
            $"{language}: the absent-row read completed rather than failing with a status",
            missingStep is { Ok: true } && missingCode is null,
            missingStep is null
                ? $"the driver reported no '{MissingStepName}' step"
                : !missingStep.Ok
                    ? missingStep.Error ?? "the absent-key read step failed"
                    : missingCode is null
                        ? "no gRPC status was raised, as the server's Success=false envelope requires"
                        : $"the client library raised gRPC status {missingCode}; the server answers an " +
                          "absent row with a SUCCESSFUL RPC carrying Success=false",
            Requirements.ErrAbsentRowReadReportsAbsence));

        // ── IVC-ERR-005 / IVC-ERR-002: the unregistered-type write ───────────────────────────
        var unregisteredStep = document.Steps.FirstOrDefault(s => s.Name == UnregisteredStepName);
        var unregisteredCode = unregisteredStep is { Ok: true }
            ? IdentityScenario.ReadStatusCode(unregisteredStep.Entity)
            : null;
        var unregisteredDetail = IdentityScenario.ReadString(unregisteredStep?.Entity, "detail");

        assertions.Add(Assertion.From(
            $"{language}: a mapped write against an unregistered type is refused with FailedPrecondition",
            unregisteredCode == UnregisteredStatusCode,
            unregisteredStep is null
                ? $"the driver reported no '{UnregisteredStepName}' step"
                : !unregisteredStep.Ok
                    ? $"the attempt itself broke, so no refusal was observed: {unregisteredStep.Error ?? "no error text"}"
                    : unregisteredCode is null
                        ? "the driver reported no gRPC status code, which is what it reports when the " +
                          $"write against '{UnregisteredTypeName}' was ACCEPTED"
                        : $"the driver reported gRPC status {unregisteredCode}, expected " +
                          $"{UnregisteredStatusCode} (FAILED_PRECONDITION)",
            Requirements.ErrUnregisteredTypeWriteIsFailedPrecondition));

        assertions.Add(Assertion.From(
            $"{language}: the refusal names the type the server has no schema for ('{UnregisteredTypeName}')",
            unregisteredDetail is not null
            && unregisteredDetail.Contains(UnregisteredTypeName, StringComparison.Ordinal),
            unregisteredDetail is null
                ? "the driver reported no status detail at all, so its client library did not hand the " +
                  "server's message to the caller"
                : $"detail='{unregisteredDetail}'",
            Requirements.ErrMessageNamesOffendingElement));

        return assertions;
    }

    /// <summary>
    /// The <c>error_doc</c> key <paramref name="language"/>'s write phase reported, parsed as a
    /// <see cref="Guid"/>, or null when it reported none (or something unparsable). This is the
    /// harness's own accounting of what it seeded, and the backstop's subject.
    /// </summary>
    internal static Guid? SeededKey(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage,
        string language) =>
        keysByLanguage.TryGetValue(language, out var byName)
        && byName.TryGetValue(RowKeyName, out var raw)
        && Guid.TryParse(raw, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// A boolean property off a driver's reported step entity, or null when the document is absent,
    /// is not an object, lacks the property, or carries something that is not a boolean there.
    /// Nothing here judges — a missing value becomes null and the assertion above reports it, and
    /// null is deliberately NOT folded into <c>false</c>: "the library returned nothing" and "the
    /// library reported no flag at all" are different observations and read differently.
    /// </summary>
    internal static bool? ReadBool(JsonElement? entity, string name) =>
        entity is { ValueKind: JsonValueKind.Object } document
        && document.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

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

    // ── phase plumbing (mirrors IdentityScenario's) ──────────────────────────────────────────

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

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
