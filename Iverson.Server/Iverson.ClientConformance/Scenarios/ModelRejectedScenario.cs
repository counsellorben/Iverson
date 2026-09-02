using System.Text.Json;
using Grpc.Core;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S11 <c>model-rejected</c>: the server refuses a re-registration that would change a registered
/// type's embedding model (<c>IVC-REG-006</c>).
///
/// <para><b>Why the rule exists.</b> A type's vectors live in one Qdrant collection per tenant. Two
/// models produce vectors in two incompatible spaces, and nothing downstream can tell them apart
/// once they share a collection — a dimension check catches only the subset of model changes that
/// also change the dimension, and two models of the same width are exactly the case it cannot see.
/// So the server rejects the registration outright rather than accepting it and quietly poisoning
/// the collection, and the rejection's message carries the two-step remedy (clear the schema row
/// AND drop the per-tenant collections), because doing either half alone leaves the deployment
/// stuck: the row alone leaves the mixed vectors, the collections alone leave the row, and the next
/// registration is rejected identically.</para>
///
/// <para><b>Shape: driver-registered fixture, orchestrator-provoked rejection.</b> Each requested
/// language registers its OWN vector-carrying fixture (<see cref="TypeNameFor"/>) through its own
/// client library, reporting the descriptor it sent; the orchestrator then re-registers THAT
/// descriptor, verbatim except for a model override
/// (<see cref="IReregistrar.ReregisterAsync"/>'s <c>modelId</c>), and grades the refusal. One
/// fixture per language rather than one shared type, unlike <see cref="VectorSearchScenario"/>: the
/// subject here is what happens to a type ALREADY registered by a given client, so five languages
/// sharing one type would leave four of the five columns grading a row a different client
/// registered.</para>
///
/// <para>The override model is never pulled and never needs to be: the guard runs in
/// <c>SchemaRegistrationOrchestrator.RegisterAsync</c>'s phase-1 loop, BEFORE
/// <c>EnsureInitializedAsync</c> ever contacts the embedding service, and before any DDL or
/// registry write — so a rejected attempt also leaves the stored schema byte-unchanged and every
/// other scenario's view of these types intact.</para>
///
/// <para><b>Each arm must be rejected for its OWN reason.</b> As in
/// <see cref="TenantRejectedScenario"/>, a bare "some rejection happened" would be satisfied by any
/// other guard on the registration path. Every arm therefore asserts the guard's own message text:
/// both models by name, the <c>DELETE FROM</c> naming this type's schema row, and BOTH
/// tenant-qualified collection names. The collection halves are asserted tenant-qualified
/// deliberately — an earlier wording of the guard named the bare collection base, which is never a
/// real Qdrant collection and would send an operator looking for something that never existed, and
/// an assertion loose enough to accept both wordings would not have caught that.</para>
///
/// <para><b>What the parity assertion does and does not establish.</b>
/// <see cref="JudgeParity"/> reads all five fixtures' rows through <see cref="SchemaProbe"/> and
/// asserts they resolved to the SAME model — the positive control beside the four negative arms,
/// and the harness's only observation of the model the server actually stored (it is on no wire).
/// In a single-model conformance environment it CANNOT distinguish "the client stamped the declared
/// model" from "the client sent <c>""</c> and the server fell back to the same value", because both
/// produce the same stored value. Per-client stamping is pinned by a client-side unit test in each
/// of T6-T10; what this assertion covers is server-side parity across the five, which is what it is
/// for. Do not read a green cell here as evidence that any particular client transmitted a model
/// id.</para>
///
/// <para><b>Until T6-T10 land, no driver implements this scenario.</b> A driver whose toolchain is
/// absent reports <see cref="DriverPhaseOutcome.Skipped"/> and renders as a Skip, exactly as every
/// other scenario handles it. A driver that runs but does not recognize the scenario name exits
/// non-zero, which is <see cref="DriverPhaseOutcome.Broken"/> and a red cell — that is the correct
/// reading (the harness asked a driver for something it could not do) and it is what T6-T10 close,
/// one language at a time.</para>
/// </summary>
public sealed class ModelRejectedScenario(
    IDriverRunner runner,
    IReregistrar reregistrar,
    SchemaProbe schema,
    Action<string>? log = null)
{
    public const string Name = "model-rejected";

    /// <summary>
    /// The single step every driver reports for this scenario's register phase, carrying the
    /// descriptor it registered. Part of the contract T6-T10 implement against, alongside
    /// <see cref="TypeNameFor"/>.
    /// </summary>
    internal const string RegisterStepName = "register_model_doc";

    /// <summary>
    /// The model the orchestrator's re-registration claims. Deliberately a name no Ollama
    /// deployment can hold, so it can never coincide with the configured default and make the
    /// guard's two models equal — which would leave every arm below passing over a registration
    /// that changed nothing.
    /// </summary>
    internal const string OverrideModelId = "iverson-conformance-model-that-is-not-deployed";

    /// <summary>
    /// The fixture type name each language registers, one per language and never shared. The
    /// second half of the contract T6-T10 implement against. Alphanumeric with no underscore
    /// because <c>SchemaRegistrationOrchestrator</c>'s identifier pattern is
    /// <c>^[A-Za-z][A-Za-z0-9]*$</c> — <c>ToSnakeCase</c> inserts its own separators.
    /// </summary>
    internal static string TypeNameFor(string language) =>
        "S11Model" + (language.Length == 0
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

        // ── register: every requested language registers its OWN fixture ───────────────────────
        var fixtures = new Dictionary<string, CapturedFixture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, document) in await RunRegisterPhaseAsync(states, context, ct))
        {
            var state = states[language];
            var (fixture, failure) = TryCaptureFixture(language, document);
            if (fixture is null)
            {
                state.Assertions.Add(Assertion.Fail(
                    $"{language}: the driver reported the fixture it registered", failure!));
                continue;
            }

            fixtures[language] = fixture;

            // Uncited harness-contract check, not a client-conformance claim: it grades whether the
            // driver registered THIS scenario's fixture, which is what makes the probe below and
            // the re-registration beneath it aim at the same row.
            state.Assertions.Add(Assertion.From(
                $"{language}: the driver registered this scenario's fixture type '{TypeNameFor(language)}'",
                string.Equals(fixture.TypeName, TypeNameFor(language), StringComparison.Ordinal),
                $"the driver reported '{fixture.TypeName}'"));
        }

        var ordered = languages.Where(fixtures.ContainsKey).ToList();

        // ── the positive control: what model did the server actually store, for each fixture ───
        var observations = new List<ModelObservation>();
        foreach (var language in ordered)
            observations.Add(await ObserveModelAsync(language, fixtures[language].TypeName, ct));

        log?.Invoke($"  stored models: {DescribeObservations(observations)}");

        foreach (var (language, assertion) in JudgeParity(observations))
            states[language].Assertions.Add(assertion);

        // ── the rejection: one re-registration per language, each with the model override ──────
        foreach (var language in ordered)
        {
            var fixture = fixtures[language];
            RpcException? caught;
            try
            {
                caught = await TryReregisterAsync(fixture, actingToken, ct);
            }
            catch (Exception ex)
            {
                // Anything that is NOT a gRPC status is the harness's own failure, not an
                // observation of the server: graded as a failure for THIS language rather than
                // allowed to escape, which would abort the whole run (Program.cs's outer catch)
                // and lose every other scenario's already-collected cells.
                states[language].Assertions.Add(Assertion.Fail(
                    $"{language}: the harness could re-register '{fixture.TypeName}' at all",
                    $"the re-registration failed with something other than a gRPC status: {Describe(ex)}"));
                continue;
            }

            states[language].Assertions.AddRange(JudgeRejection(
                language,
                fixture.TypeName,
                observations.First(o => string.Equals(o.Language, language, StringComparison.OrdinalIgnoreCase)).Model,
                caught));
        }

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    // ── the judgement (pure, so every branch is exercisable without a live stack) ─────────────

    /// <summary>
    /// One language's stored-model observation: the model <see cref="SchemaProbe"/> read, or why it
    /// could not be read. A failed probe and an absent row are distinct and neither is a silent
    /// skip — both make <see cref="JudgeParity"/> fail with the reason in its detail.
    /// </summary>
    internal sealed record ModelObservation(string Language, string TypeName, string? Model, string? ProbeError)
    {
        public string Describe() => ProbeError is not null
            ? $"{Language}/{TypeName}: the schema probe failed ({ProbeError})"
            : Model is null
                ? $"{Language}/{TypeName}: no schema row carrying an embedding model"
                : $"{Language}/{TypeName}='{Model}'";
    }

    /// <summary>
    /// The positive control: every requested language's fixture resolved to the same embedding
    /// model, read out of the schema registry itself rather than off any wire.
    ///
    /// <para>Rendered as one assertion PER LANGUAGE, all of which fail together when the set
    /// disagrees. That is deliberate: parity is a joint property of the whole set, and picking one
    /// language's value as the reference would render a two-way disagreement as one green column
    /// and one red one, attributing the defect to whichever language happened not to be chosen.
    /// The detail carries every observation, so the diverging one is named in every cell.</para>
    /// </summary>
    internal static IReadOnlyList<(string Language, Assertion Assertion)> JudgeParity(
        IReadOnlyList<ModelObservation> observations)
    {
        var models = observations.Select(o => o.Model).ToList();
        var agreed = models.Count > 0
                     && models.All(m => m is not null)
                     && models.Distinct(StringComparer.Ordinal).Count() == 1;

        var detail = DescribeObservations(observations);

        return observations
            .Select(o => (o.Language, Assertion.From(
                $"{o.Language}: every fixture this run registered carries one embedding model",
                agreed,
                detail,
                Requirements.RegEmbeddingModelChangeRejected)))
            .ToList();
    }

    /// <summary>
    /// <c>IVC-REG-006</c>, as a pure function over what the re-registration produced. Every
    /// assertion cites the same requirement on purpose: the statement is that the server rejects
    /// THIS registration, and a rejection carrying another guard's status code or another rule's
    /// message text is not evidence of it. The status-code half has no <c>IVC-ERR-*</c> requirement
    /// of its own — the ERR axis authors one per rejection FAMILY and this is the only
    /// <c>FailedPrecondition</c> registration refusal in the standard — so it is graded here rather
    /// than left ungraded, and no ERR requirement is widened to cover it.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeRejection(
        string language, string typeName, string? priorModel, RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                $"{language}: the server rejects a re-registration that changes '{typeName}'s embedding model",
                caught is not null,
                caught is null
                    ? $"the server accepted a registration resolving to '{OverrideModelId}', so '{typeName}'s "
                      + "collections would now hold vectors from two incompatible spaces"
                    : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegEmbeddingModelChangeRejected),
        };

        if (caught is null)
            return assertions;

        assertions.Add(Assertion.From(
            $"{language}: rejected with FailedPrecondition",
            caught.StatusCode == StatusCode.FailedPrecondition,
            $"actual={caught.StatusCode}",
            Requirements.RegEmbeddingModelChangeRejected));

        var message = caught.Status.Detail;

        // Both models, asserted separately. The prior one comes from the schema probe, not from the
        // message being judged, so this compares the server's claim against an independent reading
        // of the row it is claiming about; an unreadable row fails here rather than downgrading the
        // assertion to "some model was named".
        assertions.Add(Assertion.From(
            $"{language}: the error names the model '{typeName}'s registered schema carries",
            priorModel is not null && message.Contains($"embedding model '{priorModel}'", StringComparison.Ordinal),
            priorModel is null
                ? $"the schema probe read no model for '{typeName}', so the message's claim about the stored "
                  + $"model could not be checked against the row — error='{message}'"
                : $"expected the message to name '{priorModel}' — error='{message}'",
            Requirements.RegEmbeddingModelChangeRejected));

        assertions.Add(Assertion.From(
            $"{language}: the error names the model this registration resolved to ('{OverrideModelId}')",
            message.Contains($"resolves to '{OverrideModelId}'", StringComparison.Ordinal),
            $"error='{message}'",
            Requirements.RegEmbeddingModelChangeRejected));

        // The remedy, both halves. Doing either alone leaves the deployment unable to move: the row
        // alone leaves the mixed vectors in place, the collections alone leave the row and the next
        // registration is rejected identically. A message naming only one half is a message that
        // will be followed only halfway.
        assertions.Add(Assertion.From(
            $"{language}: the error names the schema row to clear",
            message.Contains(
                $"DELETE FROM {SchemaProbe.SchemaTable} WHERE type_name = '{typeName}'",
                StringComparison.Ordinal),
            $"error='{message}'",
            Requirements.RegEmbeddingModelChangeRejected));

        // TENANT-QUALIFIED, and both collections. IntelligenceTenantScope.ResolveCollectionName
        // qualifies every collection by tenant, so the bare base name ("s11_model_dotnets") is never a
        // real collection — an assertion that accepted it would pass over a message sending the
        // operator to look for something that has never existed.
        var collectionBase = PostgresProbe.TableName(typeName);
        assertions.Add(Assertion.From(
            $"{language}: the error names both tenant-qualified Qdrant collections to drop",
            message.Contains($"'{collectionBase}_<tenantId>'", StringComparison.Ordinal) &&
            message.Contains($"'{collectionBase}_chunks_<tenantId>'", StringComparison.Ordinal),
            $"expected both '{collectionBase}_<tenantId>' and '{collectionBase}_chunks_<tenantId>' — "
            + $"error='{message}'",
            Requirements.RegEmbeddingModelChangeRejected));

        return assertions;
    }

    /// <summary>The descriptor a driver reported for its register step, plus the type name read
    /// out of it — the row the probe reads and the message assertions name.</summary>
    internal sealed record CapturedFixture(JsonElement Json, string TypeName);

    /// <summary>
    /// The register step's descriptor, or why this language cannot be graded. Mirrors
    /// <see cref="VectorSearchScenario.TryCaptureDescriptor"/>, except that the type name is
    /// KEPT rather than the parse being discarded: everything downstream — the probe, the
    /// <c>DELETE FROM</c> assertion and the collection-name assertion — addresses the type the
    /// driver actually registered, so reading that name off the driver's own report is what keeps
    /// the three from drifting apart when a driver registers something unexpected.
    /// </summary>
    internal static (CapturedFixture? Fixture, string? Failure) TryCaptureFixture(
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
            return (new CapturedFixture(json, Verifier.ParseDescriptor(json).TypeName), null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────

    private async Task<ModelObservation> ObserveModelAsync(
        string language, string typeName, CancellationToken ct)
    {
        try
        {
            return new ModelObservation(language, typeName, await schema.FetchModelAsync(typeName, ct), null);
        }
        catch (Exception ex)
        {
            return new ModelObservation(language, typeName, null, Describe(ex));
        }
    }

    private async Task<RpcException?> TryReregisterAsync(
        CapturedFixture fixture, string actingToken, CancellationToken ct)
    {
        try
        {
            await reregistrar.ReregisterAsync(
                fixture.Json, actingToken, modelId: OverrideModelId, ct: ct);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

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

    private static string DescribeObservations(IReadOnlyList<ModelObservation> observations) =>
        observations.Count == 0
            ? "no language reported a fixture to read"
            : string.Join("; ", observations.Select(o => o.Describe()));

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
