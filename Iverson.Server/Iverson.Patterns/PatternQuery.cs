namespace Iverson.Patterns;

public static class PatternQuery
{
    /// <summary>
    /// Parses and validates the request's pattern, subsets, defines and measures, and compiles the program under
    /// <paramref name="maxProgramInstructions"/> (spec §3.1, §5). Throws <see cref="PatternValidationException"/>.
    /// </summary>
    public static CompiledPattern Compile(PatternRequest request, int maxProgramInstructions) =>
        CompiledPattern.Create(request, maxProgramInstructions);
}
