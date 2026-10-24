using System.Security.Claims;

namespace Iverson.Api.Tenancy;

/// <summary>
/// The revocation check for one principal. Both JwtBearer schemes run it when a token is validated
/// (<c>Program.RejectRevokedTokenAsync</c>), and <c>StreamRevalidationInterceptor</c> runs it before every
/// streamed message (CSR round 12 §2.3). A principal with no <c>sub</c> is never revoked. <c>iat</c> is
/// read as integer seconds; a token without one is judged by <see cref="ITokenRevocationCache.IsRevokedAsync"/>.
/// </summary>
public static class TokenRevocationCheck
{
    public static Task<bool> IsRevokedAsync(
        ITokenRevocationCache revocations, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (sub is null)
            return Task.FromResult(false);

        DateTimeOffset? issuedAt = long.TryParse(principal.FindFirst("iat")?.Value, out var iat)
            ? DateTimeOffset.FromUnixTimeSeconds(iat)
            : null;
        return revocations.IsRevokedAsync(sub, issuedAt, cancellationToken);
    }
}
