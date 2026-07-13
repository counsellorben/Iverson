using System.Security.Claims;

namespace Iverson.Api;

public interface IActingUserAccessor
{
    ClaimsPrincipal? ActingUser { get; set; }
}

public sealed class ActingUserAccessor : IActingUserAccessor
{
    public ClaimsPrincipal? ActingUser { get; set; }
}
