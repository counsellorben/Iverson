using System.Text.Json;
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
    public List<(string ActingToken, string OwnerField)> Calls { get; } = [];

    /// <summary>Set to have the next and every subsequent call throw, for the failure-path arms.</summary>
    public Exception? Throws { get; set; }

    public Task ReregisterAsync(
        JsonElement typeDescriptorJson,
        string actingToken,
        string ownerField = "OwnerId",
        CancellationToken ct = default)
    {
        Calls.Add((actingToken, ownerField));
        return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
    }
}
