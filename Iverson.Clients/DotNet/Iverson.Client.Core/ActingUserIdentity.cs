namespace Iverson.Client.Core;

/// <summary>
/// The ambient acting-user identity, if one was configured on the client. A container-resolvable
/// type rather than a bare <c>Func&lt;Task&lt;string&gt;&gt;?</c>, because <c>EntityCoordinator&lt;T&gt;</c>
/// is registered open-generic and activated by reflection.
/// </summary>
public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null, bool refusesPlaintextTokens = false)
{
    public Func<Task<string>>? TokenProvider { get; } = tokenProvider;

    /// <summary>
    /// True when the client's endpoint is plaintext and it was not opted in to sending credentials
    /// over it. <c>EntityCoordinator&lt;T&gt;.WithActingUser</c> then refuses a per-call token, as
    /// <c>AddIversonClient</c> refuses an ambient one.
    /// </summary>
    public bool RefusesPlaintextTokens { get; } = refusesPlaintextTokens;
}
