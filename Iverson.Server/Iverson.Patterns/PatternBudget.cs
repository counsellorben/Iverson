namespace Iverson.Patterns;

/// <summary>
/// The matcher's share of the §5 limits. <see cref="MaxSteps"/> spans the whole request (one instance per
/// request); <see cref="MaxActiveThreads"/> bounds the live threads of one match attempt. A step is one
/// program instruction processed by the matcher or one <c>define</c> evaluation.
/// </summary>
public sealed class PatternBudget(int maxActiveThreads, long maxSteps)
{
    public int MaxActiveThreads { get; } = maxActiveThreads;
    public long MaxSteps { get; } = maxSteps;
    public long StepsUsed { get; private set; }

    internal void Step()
    {
        if (++StepsUsed > MaxSteps)
            throw new PatternBudgetExceededException(nameof(MaxSteps));
    }

    internal void CheckActiveThreads(int activeThreads)
    {
        if (activeThreads > MaxActiveThreads)
            throw new PatternBudgetExceededException(nameof(MaxActiveThreads));
    }
}
