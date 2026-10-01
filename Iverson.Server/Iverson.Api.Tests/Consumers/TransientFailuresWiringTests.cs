using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.StarRocks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

/// <summary>
/// Pins the Program.cs composition: <c>AddKafka(cfg, isTransient: TransientFailures.IsTransient)</c>. The
/// dispatcher's default classifies nothing as transient, so dropping that argument would silently turn every
/// outage into dead-lettering. Resolved from the real host; no containers are started.
/// </summary>
public class TransientFailuresWiringTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _factory;

    public TransientFailuresWiringTests(AuthTestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void DispatcherOptions_UseTheProjectionTransientClassifier()
    {
        var options = _factory.Services.GetRequiredService<MessageDispatcherOptions>();

        options.IsTransient(new EngagementNotReadyException("x")).Should().BeTrue();
        options.IsTransient(new InvalidOperationException("x")).Should().BeFalse();
    }
}
