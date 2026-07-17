using System.Diagnostics;
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class OutboxPublisherTests
{
    private readonly IEventProducer _events = Substitute.For<IEventProducer>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly ILogger<OutboxPublisher> _logger = Substitute.For<ILogger<OutboxPublisher>>();
    private readonly OutboxPublisher _sut;

    public OutboxPublisherTests()
    {
        _sut = new OutboxPublisher(_events, _outboxWriter, _logger);
    }

    private void AssertWarningLogged(string expectedSubstring) =>
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public async Task PublishAsync_Success_DeletesOutboxRowAndLogsNothing()
    {
        var outboxRowId = Guid.NewGuid();

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, outboxRowId, "Mapping.Post");

        await _outboxWriter.Received(1).DeleteOutboxRowIfPresentAsync(outboxRowId);
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ProduceThrows_LogsOpportunisticFailureWarning_DoesNotDeleteOutboxRow()
    {
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
            .Returns<Task>(_ => throw new InvalidOperationException("kafka down"));

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        await _outboxWriter.DidNotReceive().DeleteOutboxRowIfPresentAsync(Arg.Any<Guid>());
        AssertWarningLogged("Opportunistic publish failed");
    }

    [Fact]
    public async Task PublishAsync_DeleteThrows_LogsCleanupFailureWarning()
    {
        var outboxRowId = Guid.NewGuid();
        _outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId)
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, outboxRowId, "Mapping.Post");

        AssertWarningLogged("Publish succeeded but outbox cleanup failed");
    }

    [Theory]
    [InlineData("caller-trace", "caller-trace")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public async Task PublishAsync_RequestTraceIdPresentOrAbsentNoActivity_ResolvesExpectedTraceId(
        string? requestTraceId, string expected)
    {
        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", requestTraceId,
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        captured!.TraceId.Should().Be(expected);
    }

    [Fact]
    public async Task PublishAsync_NoRequestTraceId_ActivityCurrentPresent_UsesActivityTraceId()
    {
        using var activitySource = new ActivitySource(nameof(OutboxPublisherTests));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("test-activity");

        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", requestTraceId: null,
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        captured!.TraceId.Should().Be(Activity.Current!.TraceId.ToString());
    }
}
