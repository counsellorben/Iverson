using Microsoft.Extensions.Logging;

namespace Iverson.Api.Consumers;

/// <summary>
/// Wraps a background consumer's run loop so a fault restarts it with backoff
/// instead of propagating out of BackgroundService.ExecuteAsync — which, by
/// default, crashes the entire host (BackgroundServiceExceptionBehavior.StopHost).
/// </summary>
internal static class ConsumerResilience
{
    public static readonly TimeSpan DefaultRestartDelay = TimeSpan.FromSeconds(10);

    public static async Task RunWithRestartAsync(
        Func<Task> runConsumers,
        ILogger logger,
        string label,
        CancellationToken ct,
        TimeSpan? restartDelay = null)
    {
        var delay = restartDelay ?? DefaultRestartDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await runConsumers();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex,
                    "[{Label}] Consumer loop faulted — restarting in {DelaySeconds}s",
                    label, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
