using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S12 <c>model-inherited</c>: a type that declares no embedding model of its own inherits the
/// model its parent declared (<c>IVC-DECL-007</c>).
///
/// <para><b>Why the rule exists.</b> <c>[IversonEmbeddingModel]</c> (and each language's
/// equivalent) is now <c>Inherited = true</c>, so a subclass with no attribute of its own resolves
/// its model by walking up to the nearest ancestor that declared one — T7's registered fixture,
/// <c>S12Inherited&lt;Lang&gt;</c>, declares nothing and derives from a field-less parent,
/// <c>S12Declared&lt;Lang&gt;</c>, that declares <see cref="ExpectedModelId"/>. This scenario grades
/// what each of the five drivers actually reports for that inherited type — the residual T7's
/// driver-side change exists to close.</para>
///
/// <para><b>Register-only.</b> Unlike <see cref="ModelRejectedScenario"/>, nothing here
/// re-registers or reads back a stored row: the inherited value is entirely a property of what the
/// driver sends on ITS OWN registration, so one register phase and a read of the reported
/// descriptor is the whole scenario. No <see cref="IReregistrar"/>, no <see cref="SchemaProbe"/>.</para>
///
/// <para><b>Flag-guarded per property, never blanket.</b> <c>model_id</c> and
/// <c>chunk_model_id</c> are two separate fields of <c>PropertyDescriptor</c>
/// (<c>object_mapping.proto:52,56</c>), and every client stamps each one only under that
/// property's OWN flag — a client legitimately leaves <c>chunk_model_id</c> at its default on an
/// embedding-only property and vice versa. T7's fixtures follow <c>S11Model&lt;Lang&gt;</c>'s
/// shape: exactly one embedding-only property and one chunk-only property. Asserting both fields
/// on every property would therefore fail every language on a CORRECTLY inheriting client, so
/// <see cref="JudgeInheritance"/> asserts <c>IsEmbedding ⇒ ModelId == expected</c> and
/// <c>IsChunk ⇒ ChunkModelId == expected</c>, each scoped to the properties that carry that flag,
/// and separately requires at least one property of each kind so neither half can pass vacuously
/// over a descriptor missing that kind entirely.</para>
///
/// <para><b>Two load-bearing reading rules.</b> The five drivers do not serialize an undeclared
/// model alike: .NET, Java and Python emit <c>"modelId": ""</c>, while Go and TypeScript omit the
/// field entirely. <see cref="TryCaptureDescriptor"/> therefore reads every reported descriptor
/// through <see cref="Verifier.ParseDescriptor"/> (protobuf's own JSON parser), never by indexing
/// the raw <c>JsonElement</c> by hand — the parser lands an omitted field on the same default as an
/// explicitly-default one, which is what makes the two wire shapes comparable at all. And every
/// judgement below asserts EQUALITY with <see cref="ExpectedModelId"/>, never inequality with
/// <c>""</c>: against raw JSON a "not empty" assertion reads <c>null</c> for an absent field, and
/// <c>null != ""</c> passes — which would send the Go and TypeScript columns green on exactly the
/// regression this scenario exists to catch.</para>
/// </summary>
public sealed class InheritedModelScenario(
    IDriverRunner runner,
    Action<string>? log = null)
{
    public const string Name = "model-inherited";

    /// <summary>
    /// The single step every driver reports for this scenario's register phase, carrying the
    /// descriptor it registered. Part of the contract T7 implements against, alongside
    /// <see cref="TypeNameFor"/>.
    /// </summary>
    internal const string RegisterStepName = "register_inherited_doc";

    /// <summary>
    /// The model id every language's <c>S12Declared&lt;Lang&gt;</c> fixture (T7) declares on its
    /// field-less parent, and therefore the id <c>S12Inherited&lt;Lang&gt;</c> must resolve to when
    /// it inherits correctly. The one value this whole scenario asserts equality against.
    /// </summary>
    internal const string ExpectedModelId = "nomic-embed-text";

    /// <summary>
    /// The fixture type name each language registers, one per language and never shared — mirrors
    /// <see cref="ModelRejectedScenario.TypeNameFor"/>. Alphanumeric with no underscore because
    /// <c>SchemaRegistrationOrchestrator</c>'s identifier pattern is <c>^[A-Za-z][A-Za-z0-9]*$</c>.
    /// </summary>
    internal static string TypeNameFor(string language) =>
        "S12Inherited" + (language.Length == 0
            ? string.Empty
            : char.ToUpperInvariant(language[0]) + language[1..].ToLowerInvariant());

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

        // ── register: every requested language registers its OWN inherited fixture ─────────────
        foreach (var (language, document) in await RunRegisterPhaseAsync(states, context, ct))
        {
            var state = states[language];
            var (descriptor, failure) = TryCaptureDescriptor(language, document);
            if (descriptor is null)
            {
                state.Assertions.Add(Assertion.Fail(
                    $"{language}: the driver reported the descriptor it registered", failure!));
                continue;
            }

            // Uncited harness-contract check, not a client-conformance claim — mirrors
            // ModelRejectedScenario's own fixture-type check. Without it, a driver that reported
            // SOME OTHER already-registered type's descriptor under this scenario's step name
            // (e.g. S11ModelDotnet, which happens to carry the same ExpectedModelId via its own
            // direct [IversonEmbeddingModel] declaration) would grade fully green without the
            // inheritance path — the thing this scenario exists to exercise — ever running.
            state.Assertions.Add(Assertion.From(
                $"{language}: the driver registered this scenario's fixture type '{TypeNameFor(language)}'",
                string.Equals(descriptor.TypeName, TypeNameFor(language), StringComparison.Ordinal),
                $"the driver reported '{descriptor.TypeName}'"));

            state.Assertions.AddRange(JudgeInheritance(language, descriptor));
        }

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so every branch is exercisable without a live stack) ─────────────

    /// <summary>
    /// <c>IVC-DECL-007</c>, as a pure function over the descriptor a driver reported. Scoped per
    /// property and per kind — see the class doc for why a blanket check on every property would
    /// fail a correctly inheriting client, and why "at least one of each kind" is asserted
    /// separately rather than left implicit in the per-property loops (an empty loop asserts
    /// nothing, and a descriptor missing a kind entirely must not pass on that silence).
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeInheritance(string language, TypeDescriptor descriptor)
    {
        var embeddingProperties = descriptor.Properties.Where(p => p.IsEmbedding).ToList();
        var chunkProperties = descriptor.Properties.Where(p => p.IsChunk).ToList();

        var assertions = new List<Assertion>
        {
            Assertion.From(
                $"{language}: the descriptor declares at least one embedding property",
                embeddingProperties.Count > 0,
                $"properties=[{string.Join(", ", descriptor.Properties.Select(p => p.Name))}]",
                Requirements.DeclEmbeddingModelInherited),
            Assertion.From(
                $"{language}: the descriptor declares at least one chunk property",
                chunkProperties.Count > 0,
                $"properties=[{string.Join(", ", descriptor.Properties.Select(p => p.Name))}]",
                Requirements.DeclEmbeddingModelInherited),
        };

        foreach (var property in embeddingProperties)
        {
            assertions.Add(Assertion.From(
                $"{language}: embedding property '{property.Name}' inherits the declared model '{ExpectedModelId}'",
                property.ModelId == ExpectedModelId,
                $"modelId='{property.ModelId}'",
                Requirements.DeclEmbeddingModelInherited));
        }

        foreach (var property in chunkProperties)
        {
            assertions.Add(Assertion.From(
                $"{language}: chunk property '{property.Name}' inherits the declared model '{ExpectedModelId}'",
                property.ChunkModelId == ExpectedModelId,
                $"chunkModelId='{property.ChunkModelId}'",
                Requirements.DeclEmbeddingModelInherited));
        }

        return assertions;
    }

    /// <summary>
    /// The register step's descriptor, parsed to the strongly typed contract message, or why this
    /// language cannot be graded. Going through <see cref="Verifier.ParseDescriptor"/> rather than
    /// the raw <c>JsonElement</c> is load-bearing — see the class doc's "Two load-bearing reading
    /// rules".
    /// </summary>
    internal static (TypeDescriptor? Descriptor, string? Failure) TryCaptureDescriptor(
        string language, PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == RegisterStepName);
        if (step is null)
            return (null, $"the {language} driver reported no '{RegisterStepName}' step");

        if (!step.Ok)
            return (null, step.Error ?? "registration failed");

        if (step.TypeDescriptor is not { } json)
            return (null, "typeDescriptor was null on the register step");

        try
        {
            return (Verifier.ParseDescriptor(json), null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<(string Language, PhaseDocument Document)>> RunRegisterPhaseAsync(
        Dictionary<string, LanguageState> states, DriverContext context, CancellationToken ct)
    {
        var alive = ScenarioCells.Alive(states).ToList();
        if (alive.Count == 0)
            return [];

        log?.Invoke($"  phase {PhaseNames.ToToken(Phase.Register)}: {string.Join(", ", alive)}");

        var documents = new List<(string, PhaseDocument)>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(Phase.Register, alive, context, ct))
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
                        $"driver broke during the {PhaseNames.ToToken(Phase.Register)} phase " +
                        $"(exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}", state.Assertions);
                    break;
            }
        }

        // The same guard every other scenario applies: DriverRunner produces no outcome at all for
        // a language it does not recognize, which would otherwise leave that column with a cell
        // that graded nothing rather than a named failure.
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
