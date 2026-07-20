using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Boots the real Program.cs pipeline (the same base fixture AuthenticationPipelineTests and
// RegisterSchemaAuthorizationPipelineTests use), layering a per-test ILogger<AuditLog> substitute
// via WithWebHostBuilder so the audit signal from AuditingAuthorizationMiddlewareResultHandler
// can be asserted alongside the real HTTP status code.
public class AuditingAuthorizationMiddlewareResultHandlerTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public AuditingAuthorizationMiddlewareResultHandlerTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private (HttpClient client, ILogger<AuditLog> loggerSpy) CreateClientWithLoggerSpy()
    {
        var loggerSpy = Substitute.For<ILogger<AuditLog>>();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILogger<AuditLog>>();
                services.AddSingleton(loggerSpy);
            }));
        return (factory.CreateClient(), loggerSpy);
    }

    private static void AssertWarningLogged(ILogger<AuditLog> loggerSpy, string expectedSubstring) =>
        loggerSpy.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public async Task AuthenticatedWrongRole_AdminDlq_Returns403AndLogsAuditEntry()
    {
        var (client, loggerSpy) = CreateClientWithLoggerSpy();
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/admin/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertWarningLogged(loggerSpy, "PolicyRejected");
    }

    [Fact]
    public async Task NoToken_AdminDlq_Returns401AndDoesNotLogAuditEntry()
    {
        var (client, loggerSpy) = CreateClientWithLoggerSpy();

        var response = await client.GetAsync("/admin/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        loggerSpy.ReceivedCalls().Should().BeEmpty();
    }
}
