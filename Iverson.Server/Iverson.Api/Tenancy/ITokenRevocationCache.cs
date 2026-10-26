namespace Iverson.Api.Tenancy;

public interface ITokenRevocationCache
{
    /// <summary>
    /// True when <paramref name="sub"/> has been revoked and the token was issued no more than 5 s
    /// after that revocation (the clock-skew margin), or carries no <c>iat</c> at all. Cancelling
    /// stops this caller waiting for a reload; it does not cancel the reload other callers share.
    /// </summary>
    Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the next <see cref="IsRevokedAsync"/> reload regardless of the snapshot's age, so a
    /// revocation made by this process takes effect here at once rather than within 30 s.
    /// <see cref="AuthInvalidationListener"/> calls it for revocations made by any replica.
    /// </summary>
    void Invalidate();
}
