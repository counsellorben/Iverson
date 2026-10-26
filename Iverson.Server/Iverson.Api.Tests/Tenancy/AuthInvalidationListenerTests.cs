using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Npgsql;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>CSR round 12 §2.3: the listener's connection settings, and that the api role starts it.</summary>
public class AuthInvalidationListenerTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _factory;

    public AuthInvalidationListenerTests(AuthTestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void TheListenersConnection_IsKeptAliveEvery30Seconds_AndNeverReturnedToThePool()
    {
        var listener = new NpgsqlConnectionStringBuilder(AuthInvalidationListener.ListenerConnectionString(
            "Host=db.example;Port=5432;Database=iverson;Username=iverson_api;Password=not-a-secret;Application Name=api-1"));

        listener.KeepAlive.Should().Be(30);
        listener.Pooling.Should().BeFalse();
        listener.Host.Should().Be("db.example");
        listener.Username.Should().Be("iverson_api");
        listener.ApplicationName.Should().Be("api-1");
    }

    [Fact]
    public void TheApiRole_RegistersTheListener()
    {
        _ = _factory.Services; // builds the host, which runs Program.cs's registrations

        _factory.AuthInvalidationListenerWasRegistered.Should().BeTrue();
    }
}
