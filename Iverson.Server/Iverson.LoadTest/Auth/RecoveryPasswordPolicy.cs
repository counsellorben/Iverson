namespace Iverson.LoadTest.Auth;

/// <summary>
/// The part of Authentik's recovery password policy that can be checked locally: a minimum length.
/// Authentik also requires a zxcvbn score of at least 2, which only Authentik computes. Checking the
/// length before CreateTenant stops a password that is bound to be refused from leaving a tenant
/// behind whose admin has no password.
/// </summary>
public static class RecoveryPasswordPolicy
{
    public const int MinimumLength = 8;

    public static bool IsTooShort(string password) => password.Length < MinimumLength;
}
