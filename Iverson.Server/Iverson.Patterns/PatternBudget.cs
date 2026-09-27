namespace Iverson.Patterns;

/// <summary>
/// The matcher's share of the §5 limits. <see cref="MaxSteps"/> spans the whole request (one instance per
/// request); <see cref="MaxActiveThreads"/> bounds the live threads of one match attempt. A step is one
/// program instruction processed by the matcher or one <c>define</c> evaluation.
/// </summary>
/// <param name="cancellationToken">Observed at every step and at every thread-equivalence comparison; once it is
/// cancelled, the run throws <see cref="OperationCanceledException"/>. Pass the request's timeout token, since
/// <see cref="MaxSteps"/> alone does not bound the time a step takes.</param>
public sealed class PatternBudget(int maxActiveThreads, long maxSteps, CancellationToken cancellationToken = default)
{
    public int MaxActiveThreads { get; } = maxActiveThreads;
    public long MaxSteps { get; } = maxSteps;
    public long StepsUsed { get; private set; }

    internal void Step()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++StepsUsed > MaxSteps)
            throw new PatternBudgetExceededException(nameof(MaxSteps));
    }

    /// <summary>Observes the cancellation token without counting a step (the equivalence scan: up to
    /// <see cref="MaxActiveThreads"/> comparisons can run inside one step).</summary>
    internal void ThrowIfCancellationRequested() => cancellationToken.ThrowIfCancellationRequested();

    internal void CheckActiveThreads(int activeThreads)
    {
        if (activeThreads > MaxActiveThreads)
            throw new PatternBudgetExceededException(nameof(MaxActiveThreads));
    }
}
