namespace Iverson.Api.Tenancy;

public interface ITokenRevocationCache
{
    /// <summary>
    /// True when <paramref name="sub"/> has been revoked and the token was issued at or before
    /// that revocation, or carries no <c>iat</c> at all.
    /// </summary>
    Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt);
}
