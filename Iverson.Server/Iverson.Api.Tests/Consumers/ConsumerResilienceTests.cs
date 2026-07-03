using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Iverson.Api.Consumers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class ConsumerResilienceTests
{
    [Fact]
    public async Task RunWithRestartAsync_RetriesAfterFailure_ThenExitsOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var callCount = 0;

        Task RunConsumers()
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException("boom");
            cts.Cancel();
            return Task.CompletedTask;
        }

        await ConsumerResilience.RunWithRestartAsync(
            RunConsumers, NullLogger.Instance, "Test", cts.Token, TimeSpan.FromMilliseconds(1));

        callCount.Should().Be(3);
    }

    [Fact]
    public async Task RunWithRestartAsync_AlreadyCancelled_NeverInvokesRunConsumers()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callCount = 0;

        Task RunConsumers()
        {
            callCount++;
            return Task.CompletedTask;
        }

        await ConsumerResilience.RunWithRestartAsync(
            RunConsumers, NullLogger.Instance, "Test", cts.Token, TimeSpan.FromMilliseconds(1));

        callCount.Should().Be(0);
    }

    [Fact]
    public async Task RunWithRestartAsync_RunConsumersThrowsOperationCanceledException_ExitsWithoutRestart()
    {
        using var cts = new CancellationTokenSource();
        var callCount = 0;

        Task RunConsumers()
        {
            callCount++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        await ConsumerResilience.RunWithRestartAsync(
            RunConsumers, NullLogger.Instance, "Test", cts.Token, TimeSpan.FromMilliseconds(1));

        callCount.Should().Be(1);
    }
}
