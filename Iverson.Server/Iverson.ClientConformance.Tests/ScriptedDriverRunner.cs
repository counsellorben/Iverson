using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// An <see cref="IDriverRunner"/> that returns scripted outcomes instead of spawning processes, so
/// a scenario's <c>RunAsync</c> can be driven END TO END and every judgement call site inside it
/// pinned by the report cells that come out.
///
/// <para><b>What this closes.</b> Before the <see cref="IDriverRunner"/> seam existed, scenario
/// tests could only construct a <c>DriverRunner</c> rooted at a directory with no drivers in it,
/// so every phase came back <c>Broken</c> and <c>RunAsync</c> never reached its judgement calls.
/// That is why mutants N3 and N5 — deleting <c>JudgeReadPhase(...)</c> / <c>JudgeDriverDepthRead(...)</c>
/// from <c>RunAsync</c> — survived the whole suite (Ruling 38). A test using this double reaches
/// those lines, so deleting them now fails.</para>
///
/// <para>Scripted per phase rather than per call: a scenario may run the same phase once for a
/// subset of languages and again for another, and keying on the phase alone would silently serve
/// the first script to both. Each entry is consumed in order.</para>
/// </summary>
public sealed class ScriptedDriverRunner : IDriverRunner
{
    private readonly Dictionary<Phase, Queue<IReadOnlyList<DriverPhaseOutcome>>> _scripts = new();
    private readonly Dictionary<string, Dictionary<string, string>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call made, in order — so a test can assert which phases a scenario actually ran.</summary>
    public List<(Phase Phase, IReadOnlyList<string> Languages)> Calls { get; } = [];

    public ScriptedDriverRunner Script(Phase phase, params DriverPhaseOutcome[] outcomes)
    {
        if (!_scripts.TryGetValue(phase, out var queue))
            _scripts[phase] = queue = new Queue<IReadOnlyList<DriverPhaseOutcome>>();

        queue.Enqueue(outcomes);
        return this;
    }

    public Task<IReadOnlyList<DriverPhaseOutcome>> RunPhaseAsync(
        Phase phase,
        IReadOnlyCollection<string> languages,
        DriverContext context,
        CancellationToken ct = default)
    {
        Calls.Add((phase, languages.ToList()));

        if (!_scripts.TryGetValue(phase, out var queue) || queue.Count == 0)
        {
            // Loud, not empty. A scenario reaching an unscripted phase means the test's model of
            // the scenario is wrong, and returning [] would present as "no languages responded" —
            // a plausible-looking result that hides the mismatch.
            throw new InvalidOperationException(
                $"ScriptedDriverRunner has no remaining script for phase '{phase}'. " +
                $"Scripted phases: {(_scripts.Count == 0 ? "(none)" : string.Join(", ", _scripts.Keys))}.");
        }

        var outcomes = queue.Dequeue();

        // Mirrors DriverRunner.MergeKeys: the real runner accumulates reported keys across phases
        // and feeds them back. A double that skipped this would let a scenario depending on
        // KeysByLanguage pass here and fail live.
        foreach (var success in outcomes.OfType<DriverPhaseOutcome.Success>())
        {
            foreach (var step in success.Document.Steps)
            {
                if (step.Keys is not { Count: > 0 } keys)
                    continue;

                if (!_keys.TryGetValue(success.Language, out var forLanguage))
                    _keys[success.Language] = forLanguage = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var (name, key) in keys)
                    forLanguage[name] = key;
            }
        }

        // Only the requested languages, exactly as DriverRunner does — a script naming a language
        // the scenario did not ask for must not leak into its state.
        return Task.FromResult<IReadOnlyList<DriverPhaseOutcome>>(
            outcomes.Where(o => languages.Contains(o.Language, StringComparer.OrdinalIgnoreCase)).ToList());
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> KeysByLanguage =>
        _keys.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An <see cref="IReregistrar"/> that records rather than calls. Scenarios re-register through a
/// live gRPC channel, which a driven <c>RunAsync</c> test has no way to satisfy.
/// </summary>
public sealed class RecordingReregistrar : IReregistrar
{
    /// <summary>
    /// Every call, in order. <c>TypeName</c> and <c>ModelId</c> are recorded because
    /// <c>ModelRejectedScenario</c>'s whole subject is WHICH type it re-registers and with WHICH
    /// model override: a scenario that re-registered the right descriptor with no override at all
    /// would provoke no rejection live, while every assertion driven from a scripted throw stayed
    /// green.
    /// </summary>
    public List<(string ActingToken, string OwnerField, string? ModelId, string TypeName)> Calls { get; } = [];

    /// <summary>Set to have the next and every subsequent call throw, for the failure-path arms.</summary>
    public Exception? Throws { get; set; }

    public Task ReregisterAsync(
        JsonElement typeDescriptorJson,
        string actingToken,
        string ownerField = "OwnerId",
        string? modelId = null,
        CancellationToken ct = default)
    {
        var typeName = typeDescriptorJson.ValueKind == JsonValueKind.Object &&
                       typeDescriptorJson.TryGetProperty("typeName", out var name)
            ? name.GetString() ?? string.Empty
            : string.Empty;

        Calls.Add((actingToken, ownerField, modelId, typeName));
        return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
    }
}

/// <summary>
/// Drives a register-once scenario (<c>query</c>, <c>vector-search</c>, <c>match-pattern</c>)
/// through <c>RunAsync</c> to a write step that reports <c>ok: false</c>, with no live stack.
///
/// <para>The .NET driver registers successfully (a descriptor <c>Verifier.ParseDescriptor</c>
/// accepts), the <see cref="RecordingReregistrar"/> accepts the re-registration, and ONE language's
/// write phase reports the scenario's write step failed with <see cref="Error"/>. The projection
/// wait then probes an unreachable channel under <see cref="OneAttemptWaiter"/>, so it gives up
/// after a single refused attempt and the read phase never runs.</para>
///
/// <para>Every cell therefore fails on the wait's timeout regardless, so a test built on this must
/// assert on the WRITE step's own assertion and on the cell detail, not on the status alone:
/// the timeout detail names neither the write step nor <see cref="Error"/>.</para>
/// </summary>
internal static class FailedWriteScript
{
    /// <summary>The error the failing write step reports — distinctive, so a detail can be searched for it.</summary>
    internal const string Error = "scripted write failure: the store refused the row";

    /// <summary>The register phase succeeds, then <paramref name="language"/>'s write step fails.</summary>
    internal static ScriptedDriverRunner Runner(string registerStepName, string writeStepName, string language) =>
        new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Success("dotnet", new PhaseDocument("dotnet", "register",
            [
                new StepResult(registerStepName, true,
                    TypeDescriptor: JsonDocument.Parse("""{"typeName":"Scripted"}""").RootElement.Clone()),
            ])))
            .Script(Phase.Write, new DriverPhaseOutcome.Success(language, new PhaseDocument(language, "write",
            [
                new StepResult(writeStepName, false, Error: Error),
            ])));

    /// <summary>A waiter that probes exactly once and gives that attempt at most five seconds.</summary>
    internal static ProjectionWaiter OneAttemptWaiter() => new(TimeSpan.Zero, TimeSpan.FromSeconds(5));

    /// <summary>A search client on a port nothing listens on, so every projection probe is refused at once.</summary>
    internal static Iverson.Client.Contracts.ObjectSearchService.ObjectSearchServiceClient UnreachableSearch() =>
        new(Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:1"));

    /// <summary>
    /// <paramref name="language"/>'s cell fails, its write-step assertion is present and FAILED, and
    /// the cell detail names the write step and carries <see cref="Error"/>. The middle clause is
    /// what reddens when the scenario grades the step as passed regardless of <c>step.Ok</c>.
    /// </summary>
    internal static void ShouldCarryTheWriteFailure(
        IReadOnlyList<ReportCell> cells, string language, string writeStepName)
    {
        var cell = cells.Should().ContainSingle(c => c.Language == language).Subject;
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Assertions.Should().ContainSingle(a => a.Name == $"step '{writeStepName}' succeeded")
            .Which.Passed.Should().BeFalse("the driver reported the write step with ok: false");
        cell.Detail.Should().Contain($"'{writeStepName}'").And.Contain(Error);
    }
}
