using System.Data.Common;
using System.Net;
using Grpc.Core;
using Iverson.StarRocks;
using MySqlConnector;

namespace Iverson.Api.Consumers;

/// <summary>
/// The one place that decides whether a projection failure is a dependency outage (wait and
/// redeliver) or a genuinely bad message (dead-letter). It lives in <c>Iverson.Api</c> because
/// this is the composition root that references every client library; <c>Iverson.Events</c>
/// receives it only as a <c>Func&lt;Exception, bool&gt;</c>.
///
/// The whole <see cref="Exception.InnerException"/> chain is walked, and every member of an
/// <see cref="AggregateException"/> is checked: a failure is transient when ANY link is.
///
/// Misclassification costs in both directions. A transient failure classified as permanent is
/// dead-lettered; a permanent failure classified as transient stalls its consumer until someone
/// intervenes. <c>TransientFailuresTests</c> pins both directions.
/// </summary>
internal static class TransientFailures
{
    public static bool IsTransient(Exception ex) => ex switch
    {
        AggregateException agg => agg.InnerExceptions.Any(IsTransient),
        _ => IsTransientLink(ex) || (ex.InnerException is { } inner && IsTransient(inner)),
    };

    private static bool IsTransientLink(Exception ex) => ex switch
    {
        // StarRocks shapes MySqlConnector's IsTransient does not cover (it marks only 1040, 1042,
        // 1205, 1213 and 1614). Checked before the DbException rule, which they would otherwise
        // fall through as non-transient.
        MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired } => true,      // frozen server
        MySqlException my when my.Message.Contains("Backend node not found",
                                                    StringComparison.Ordinal) => true,  // BE down, FE up
        MySqlException { InnerException: MySqlEndOfStreamException } => true,           // FE died mid-query
        MySqlException { ErrorCode: MySqlErrorCode.ParseError } my
            when my.Message.Contains("Unexpected input '@'", StringComparison.Ordinal) => true,
                                                    // statement outlived its command timeout: StarRocks
                                                    // rejects MySqlConnector's cancellation cleanup

        // Postgres (Npgsql) and StarRocks (MySqlConnector) both override DbException.IsTransient.
        DbException db => db.IsTransient,

        // StarRocks circuit open, or the readiness gate timed out.
        EngagementNotReadyException => true,

        // Qdrant.
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable
                                           or StatusCode.DeadlineExceeded
                                           or StatusCode.ResourceExhausted
                                           or StatusCode.Aborted,

        // TEI / Ollama via EnsureSuccessStatusCode(); a null status is a connection failure.
        HttpRequestException http => http.StatusCode is null
                                     || (int)http.StatusCode.Value >= 500
                                     || http.StatusCode == HttpStatusCode.TooManyRequests,

        // An HttpClient timeout surfaces as TaskCanceledException wrapping TimeoutException.
        TimeoutException => true,

        _ => false,
    };
}
