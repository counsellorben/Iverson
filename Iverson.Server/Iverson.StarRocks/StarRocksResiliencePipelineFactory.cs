using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Iverson.StarRocks;

public static class StarRocksResiliencePipelineFactory
{
    internal static ResiliencePipeline Build(EngagementCircuitBreakerOptions options, ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio      = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration  = options.SamplingDuration,
                BreakDuration     = options.BreakDuration,
                ShouldHandle      = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient),
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
                ShouldHandle     = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)
            })
            .Build();

    /// <summary>
    /// The failures the breaker counts and the retry repeats: MySqlConnector's transient set, less a full
    /// connection pool (CSR round 12 §7.2). Pools are per tenant, so a full one says only that this tenant is
    /// busy in this process. Counting it would let one tenant open the one breaker every tenant shares, and
    /// retrying it would only wait out the connect timeout again.
    /// </summary>
    internal static bool IsBackendTransient(MySqlException ex) => ex.IsTransient && !IsPoolExhausted(ex);

    // MySqlConnector 2.6.2 raises a full pool as UnableToConnectToHost with "Connect Timeout expired. All
    // pooled connections are in use." (MySqlConnection.CreateSessionAsync). The same code without that suffix
    // is a real connect timeout, which still counts. Public so the gRPC read paths can answer a full pool as
    // Unavailable rather than letting the raw exception surface as Unknown. The match is on MySqlConnector's
    // English message text, which only the live StarRocksTenantLoginIntegrationTests
    // .OneTenantsFullPool_FailsOnlyThatTenant_AndNeverOpensTheBreaker test exercises: a MySqlConnector upgrade
    // must keep that test green, or a reworded message would put full pools back in the breaker and the retry.
    public static bool IsPoolExhausted(MySqlException ex) =>
        ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost &&
        ex.Message.Contains("All pooled connections are in use", StringComparison.Ordinal);

    /// <summary>
    /// What a caller is told when <see cref="IsPoolExhausted"/> holds: the gRPC read paths' <c>Unavailable</c>
    /// detail and the admin console's data-volume 503. It names no login, password or connection string.
    /// </summary>
    public const string TenantPoolFullMessage =
        "StarRocks is busy: every connection this tenant may hold in this process is in use. Retry shortly.";
}
