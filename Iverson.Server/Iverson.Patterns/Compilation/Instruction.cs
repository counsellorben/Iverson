// Port of Trino 483 io.trino.operator.window.matcher.Instruction (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Compilation;

internal enum InstructionKind { MatchLabel, Split, Jump, ExclusionStart, ExclusionEnd, MatchStart, MatchEnd, Done }

/// <summary>
/// One program instruction (port of Trino 483 <c>io.trino.operator.window.matcher.Instruction</c> and subclasses).
/// <c>MatchLabel</c>: <see cref="First"/> is the label index. <c>Split</c>: <see cref="First"/> is the preferred
/// target, <see cref="Second"/> the other. <c>Jump</c>: <see cref="First"/> is the target.
/// </summary>
internal readonly record struct Instruction(InstructionKind Kind, int First = 0, int Second = 0);
