using System.Security.Claims;
using Iverson.Api.Grpc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class AuditLogTests
{
    private readonly ILogger<AuditLog> _logger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _sut;

    public AuditLogTests() => _sut = new AuditLog(_logger);

    private void AssertLogged(LogLevel level, string expectedSubstring) =>
        _logger.Received(1).Log(
            level,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public void Denied_WithActor_LogsWarningWithReason()
    {
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-1"), new Claim("tenant_id", "tenant-a")]));

        _sut.Denied(actor, "Read", "Article", "key-1", "TenantMismatch");

        AssertLogged(LogLevel.Warning, "TenantMismatch");
    }

    [Fact]
    public void Denied_NullActor_LogsWarningWithUnknownActor()
    {
        _sut.Denied(null, "Read", "Article", "key-1", "AccessDenied");

        AssertLogged(LogLevel.Warning, "unknown");
    }

    [Fact]
    public void AdminOperation_LogsInformationWithOperation()
    {
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "svc-1")]));

        _sut.AdminOperation(actor, "RegisterSchema", "Article");

        AssertLogged(LogLevel.Information, "RegisterSchema");
    }
}
