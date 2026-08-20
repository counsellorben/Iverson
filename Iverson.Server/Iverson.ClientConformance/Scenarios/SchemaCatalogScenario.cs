using System.Text.Json;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S5 <c>schema-catalog</c>: proves each client library can retrieve the agent-facing schema
/// catalogue (<c>GetSchema</c>) and that what comes back describes the type that client itself
/// registered.
///
/// Unlike S4, every requested language runs its OWN <c>register</c> phase: each language registers
/// a differently-named type (<c>DotNetAuthor</c>, <c>PyAuthor</c>, <c>TsAuthor</c>,
/// <c>GoAuthor</c>, <c>JavaAuthor</c>), so five registrations overwrite nothing and "every language
/// sees the type it registered" is a per-language claim with a per-language subject. The type is
/// deliberately relation-free, which is what makes <see cref="Requirements.SchCatalogFieldSetMatchesDescriptor"/>
/// an exact set comparison rather than a subset one: <c>SchemaBuilder</c> turns the key property
/// into the key column and every other declared property into a scalar column, and
/// <c>ObjectMappingGrpcService.GetSchema</c> emits exactly key + scalars when no
/// <c>FieldPermission</c> narrows the set — so the catalogue's field set must equal the
/// descriptor's property set, name for name.
///
/// Each reported descriptor is re-registered with an authorization block before the read phase.
/// A schema with no authorization block is <c>Denied</c> for every action including <c>Read</c>
/// (<c>RowFieldAuthorizationEvaluator.cs:10-12</c>), and <c>GetSchema</c> skips denied schemas
/// outright (<c>ObjectMappingGrpcService.cs:78-81</c>) — so without the re-registration every
/// driver's own type would be invisible in the catalogue it just fetched and the scenario would
/// fail for all five languages for a reason that has nothing to do with the client libraries.
///
/// <para><b>Backstop assertion.</b> <see cref="JudgeCatalogue"/>'s "the driver reported a non-empty
/// schema catalogue" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. It fires
/// unconditionally, outside the per-type search that
/// <see cref="Requirements.SchCatalogIncludesRegisteredType"/> and
/// <see cref="Requirements.SchCatalogFieldSetMatchesDescriptor"/> depend on, so a client that
/// silently reports an empty (or step-less) catalogue cannot produce a green-but-empty cell. It
/// carries no requirement ID of its own: no <c>IVC-SCH-*</c> statement owns "the catalogue is
/// non-empty" as such, and it is strictly weaker than
/// <see cref="Requirements.SchCatalogIncludesRegisteredType"/>. It is not redundant with it,
/// though — when the register phase produced no descriptor there is no type NAME to look for, so
/// the SCH-002 assertion cannot be evaluated at all and only the backstop still fires.</para>
/// </summary>
public sealed class SchemaCatalogScenario(
    DriverRunner runner,
    Reregistrar reregistrar,
    Action<string>? log = null)
{
    public const string Name = "schema-catalog";

    /// <summary>The register-phase step every driver reports its catalogue subject type under.</summary>
    internal const string RegisterStepName = "register_schema_type";

    /// <summary>The read-phase step every driver reports its retrieved catalogue under.</summary>
    internal const string ReadStepName = "get_schema";

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

        // ── register: every language registers its own, differently-named type ────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Register, states, context, ct))
        {
            var state = states[language];
            var step = document.Steps.FirstOrDefault(s => s.Name == RegisterStepName);

            if (step is null)
            {
                state.Assertions.Add(Assertion.Fail(
                    $"step '{RegisterStepName}'",
                    "the driver reported no such step, so there is no type name to look for in the " +
                    "catalogue and no descriptor to compare its fields against"));
                continue;
            }

            state.Assertions.Add(Assertion.From(
                $"step '{RegisterStepName}' succeeded", step.Ok, step.Error ?? "ok"));

            if (!step.Ok)
                continue;

            if (step.TypeDescriptor is not { } descriptorJson)
            {
                // Never a silent skip — matching CrudRoundtripScenario.cs:70-84, the missing
                // descriptor is reported as a failed assertion that names its consequence.
                state.Assertions.Add(Assertion.Fail(
                    $"step '{RegisterStepName}' reported a type descriptor",
                    "typeDescriptor was null, so the type cannot be re-registered with row " +
                    "permissions and would be invisible in the catalogue for reasons unrelated " +
                    "to the client library"));
                continue;
            }

            try
            {
                state.Descriptor = Verifier.ParseDescriptor(descriptorJson);
            }
            catch (Exception ex)
            {
                state.Assertions.Add(Assertion.Fail(
                    $"step '{RegisterStepName}' reported a parsable type descriptor", Describe(ex)));
                continue;
            }

            var descriptorName = state.DescriptorName;
            try
            {
                await reregistrar.ReregisterAsync(descriptorJson, actingToken, ct: ct);
            }
            catch (Exception ex)
            {
                state.Descriptor = null;
                state.Assertions.Add(Assertion.Fail(
                    $"re-registering '{descriptorName}' with row permissions",
                    $"{Describe(ex)} — without an authorization block the type is Denied for Read " +
                    "and GetSchema omits it entirely"));
            }
        }

        // ── read: each driver fetches the catalogue through its own client library ─────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Read, states, context, ct))
        {
            var state = states[language];
            foreach (var assertion in JudgeCatalogue(language, state.Descriptor, document))
                state.Assertions.Add(assertion);
        }

        return states.Select(kv => Cell(kv.Key, kv.Value)).ToList();
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>
    /// Judges one language's read phase. Pure over reported data — no I/O — so every branch below
    /// is exercisable from a unit test.
    ///
    /// <paramref name="descriptor"/> is null when the register phase produced nothing usable for
    /// this language; the reachability assertion (<see cref="Requirements.SchCatalogRetrievalReachable"/>)
    /// and the non-empty backstop still fire in that case, and the two descriptor-dependent
    /// assertions are replaced by an explicit failure rather than skipped in silence.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeCatalogue(
        string language, TypeDescriptor? descriptor, PhaseDocument document)
    {
        var assertions = new List<Assertion>();
        var step = document.Steps.FirstOrDefault(s => s.Name == ReadStepName);

        if (step is null)
        {
            assertions.Add(Assertion.Fail(
                $"{language}: schema-catalogue retrieval is reachable through the client's public API",
                $"the driver reported no '{ReadStepName}' step",
                Requirements.SchCatalogRetrievalReachable));
        }
        else
        {
            assertions.Add(Assertion.From(
                $"{language}: schema-catalogue retrieval is reachable through the client's public API",
                step.Ok,
                step.Error ?? "ok",
                Requirements.SchCatalogRetrievalReachable));
        }

        var types = ReadTypes(step is { Ok: true } ? step.Entity : null);

        // ── the SCH backstop (uncited by design — see the class doc comment) ──────────────────
        assertions.Add(Assertion.From(
            $"{language}: the driver reported a non-empty schema catalogue",
            types.Count > 0,
            $"the catalogue carried {types.Count} type(s)"));

        if (descriptor is null)
        {
            assertions.Add(Assertion.Fail(
                $"{language}: the catalogue contains the type this client registered",
                "the register phase produced no usable descriptor for this language, so there is " +
                "no registered type name to look for",
                Requirements.SchCatalogIncludesRegisteredType));
            return assertions;
        }

        var expectedName = descriptor.TypeName;
        var match = types.FirstOrDefault(
            t => Verifier.Normalize(t.Name) == Verifier.Normalize(expectedName));

        assertions.Add(Assertion.From(
            $"{language}: the catalogue contains '{expectedName}', the type this client registered",
            match is not null,
            match is not null
                ? "present"
                : $"absent; the catalogue named: {(types.Count == 0 ? "(nothing)" : string.Join(", ", types.Select(t => t.Name)))}",
            Requirements.SchCatalogIncludesRegisteredType));

        if (match is null)
            return assertions;

        var declared = descriptor.Properties
            .Select(p => Verifier.Normalize(p.Name))
            .ToHashSet();
        var catalogued = match.FieldNames
            .Select(Verifier.Normalize)
            .ToHashSet();

        var missing = descriptor.Properties
            .Where(p => !catalogued.Contains(Verifier.Normalize(p.Name)))
            .Select(p => p.Name)
            .ToList();
        var unexpected = match.FieldNames
            .Where(n => !declared.Contains(Verifier.Normalize(n)))
            .ToList();

        assertions.Add(Assertion.From(
            $"{language}: '{expectedName}' carries exactly the field set its descriptor declared",
            missing.Count == 0 && unexpected.Count == 0,
            missing.Count == 0 && unexpected.Count == 0
                ? $"{catalogued.Count} field(s), matching the descriptor"
                : $"declared-but-absent: [{string.Join(", ", missing)}]; " +
                  $"catalogued-but-undeclared: [{string.Join(", ", unexpected)}]",
            Requirements.SchCatalogFieldSetMatchesDescriptor));

        return assertions;
    }

    /// <summary>
    /// The catalogue a driver reported, read out of the step's <c>entity</c>. The five drivers all
    /// emit the same deliberately minimal projection —
    /// <c>{"types":[{"name":...,"fields":[{"name":...}],"relations":[{"propertyName":...}]}]}</c> —
    /// so this reader needs no per-language special-casing. A missing or malformed document yields
    /// an empty list, which the backstop assertion above is what catches; nothing here judges.
    /// </summary>
    internal static IReadOnlyList<CatalogueType> ReadTypes(JsonElement? entity)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } document)
            return [];

        if (!document.TryGetProperty("types", out var typesElement) ||
            typesElement.ValueKind != JsonValueKind.Array)
            return [];

        var types = new List<CatalogueType>();
        foreach (var typeElement in typesElement.EnumerateArray())
        {
            if (typeElement.ValueKind != JsonValueKind.Object)
                continue;

            var name = typeElement.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            var fieldNames = new List<string>();
            if (typeElement.TryGetProperty("fields", out var fieldsElement) &&
                fieldsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var fieldElement in fieldsElement.EnumerateArray())
                {
                    if (fieldElement.ValueKind == JsonValueKind.Object &&
                        fieldElement.TryGetProperty("name", out var fieldName) &&
                        fieldName.ValueKind == JsonValueKind.String &&
                        fieldName.GetString() is { Length: > 0 } value)
                    {
                        fieldNames.Add(value);
                    }
                }
            }

            if (name.Length > 0)
                types.Add(new CatalogueType(name, fieldNames));
        }

        return types;
    }

    internal sealed record CatalogueType(string Name, IReadOnlyList<string> FieldNames);

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

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    private sealed class LanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>The type this language registered, or null when its register phase produced
        /// nothing usable. Also cleared when re-registration failed — the type is then still
        /// legitimately absent from the catalogue, so comparing against it would grade the
        /// harness's own plumbing failure as a client defect.</summary>
        public TypeDescriptor? Descriptor { get; set; }

        public string DescriptorName => Descriptor?.TypeName ?? "(unknown type)";

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
