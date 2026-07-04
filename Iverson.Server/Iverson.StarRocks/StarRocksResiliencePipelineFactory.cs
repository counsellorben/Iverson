using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Iverson.StarRocks;

internal static class StarRocksResiliencePipelineFactory
{
    public static ResiliencePipeline Build(StarRocksCircuitBreakerOptions options, ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio      = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration  = options.SamplingDuration,
                BreakDuration     = options.BreakDuration,
                ShouldHandle      = new PredicateBuilder().Handle<MySqlException>(ex => ex.IsTransient),
                OnOpened = args =>
                {
                    logger.LogWarning(
                        "StarRocks circuit breaker opened — failing fast for {BreakDuration}", args.BreakDuration);
                    return default;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("StarRocks circuit breaker closed — backend recovered");
                    return default;
                }
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay            = TimeSpan.FromMilliseconds(200),
                BackoffType      = DelayBackoffType.Constant,
                ShouldHandle     = new PredicateBuilder().Handle<MySqlException>(ex => ex.IsTransient)
            })
            .Build();
}
