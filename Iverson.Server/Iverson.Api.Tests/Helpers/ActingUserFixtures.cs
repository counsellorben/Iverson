using System.Security.Claims;

namespace Iverson.Api.Tests.Helpers;

public static class ActingUserFixtures
{
    public static ClaimsPrincipal Principal(string sub, params string[] groups)
    {
        var claims = new List<Claim> { new("sub", sub) };
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
