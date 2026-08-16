namespace Iverson.ClientConformance;

/// <summary>
/// The registry of requirement IDs from <c>docs/standards/iverson-client-standard.md</c>.
///
/// A const exists here only for a requirement whose Status is <c>Active</c> in the standard.
/// <c>Retired</c> rows are not represented here at all. The coverage gate
/// (<see cref="RequirementsCoverageGateTests"/> in the test project) enforces, at build time,
/// that this class's set of <c>public const string</c> fields exactly matches the standard's set
/// of <c>Active</c> IDs, and that every const here is cited by at least one
/// <see cref="Assertion"/> constructed somewhere under <c>Iverson.ClientConformance/</c> (outside
/// this file and outside the test project).
/// </summary>
public static class Requirements
{
}
