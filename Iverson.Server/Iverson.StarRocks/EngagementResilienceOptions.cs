namespace Iverson.StarRocks;

public sealed class EngagementResilienceOptions
{
    public TimeSpan BackendReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public EngagementCircuitBreakerOptions CircuitBreaker { get; init; } = new();

    public static EngagementResilienceOptions Default { get; } = new();
}

public sealed class EngagementCircuitBreakerOptions
{
    public double FailureRatio { get; init; } = 0.5;
    public int MinimumThroughput { get; init; } = 4;
    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(15);
}
