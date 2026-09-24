namespace Iverson.Patterns;

public static class PatternQuery
{
    /// <summary>
    /// Parses and validates the request's pattern, subsets, defines and measures, and compiles the program under
    /// <paramref name="maxProgramInstructions"/> (spec §3.1, §5). Throws <see cref="PatternValidationException"/>.
    /// <para>
    /// Caller contract: apply the spec §5 <c>MaxPatternLength</c> and <c>MaxExpressionLength</c> limits (and the
    /// other request-shape limits) before calling. The pattern and expression parsers are recursive descent and
    /// recurse once per nesting level, so the length limits are what bound their stack depth; for the same
    /// reason call <c>Compile</c> and <see cref="CompiledPattern.Run"/> on threads with a normal-size stack
    /// (thread-pool threads qualify), never on reduced-stack threads. See <see cref="CompiledPattern.Run"/> for
    /// the row, similarity-callback, budget and cancellation contract.
    /// </para>
    /// </summary>
    public static CompiledPattern Compile(PatternRequest request, int maxProgramInstructions) =>
        CompiledPattern.Create(request, maxProgramInstructions);
}
