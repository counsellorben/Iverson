using System.Text.Json;

namespace Iverson.ClientConformance;

/// <summary>
/// The five phases every scenario drives a client library through. The enum values partition a
/// scenario's steps: each driver invocation runs exactly one phase and reports exactly one
/// <see cref="PhaseDocument"/> for it.
/// </summary>
public enum Phase
{
    Register,
    Write,
    Read,
    Update,
    Delete,
}

/// <summary>
/// Maps a <see cref="Phase"/> to the literal token drivers receive on <c>--phase</c> and are
/// expected to echo back as <see cref="PhaseDocument.Phase"/>.
/// </summary>
public static class PhaseNames
{
    public static string ToToken(Phase phase) => phase switch
    {
        Phase.Register => "register",
        Phase.Write => "write",
        Phase.Read => "read",
        Phase.Update => "update",
        Phase.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}

/// <summary>
/// One JSON document written by a driver at its <c>--out</c> path for a single phase invocation.
/// Drivers report; they never assert — every <see cref="StepResult"/> is raw data for the
/// orchestrator's Verifier (Task 8) to judge, including failed steps (<c>ok: false</c> with
/// <see cref="StepResult.Error"/> set, and the driver still exits 0).
/// </summary>
public sealed record PhaseDocument(string Language, string Phase, IReadOnlyList<StepResult> Steps);

/// <summary>
/// One step's outcome within a phase document. <see cref="TypeDescriptor"/> is populated on the
/// register phase's step(s); <see cref="Keys"/> on the write phase's step(s), keyed by logical
/// name (driver-chosen, not shared across languages) to a driver-chosen row UUID; <see cref="Entity"/>
/// on read steps.
/// </summary>
public sealed record StepResult(
    string Name,
    bool Ok,
    string? Error = null,
    JsonElement? TypeDescriptor = null,
    IReadOnlyDictionary<string, string>? Keys = null,
    JsonElement? Entity = null);
