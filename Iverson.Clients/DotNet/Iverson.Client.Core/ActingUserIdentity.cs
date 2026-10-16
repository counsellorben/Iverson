namespace Iverson.Client.Core;

/// <summary>
/// The ambient acting-user identity, if one was configured on the client. A container-resolvable
/// type rather than a bare <c>Func&lt;Task&lt;string&gt;&gt;?</c>, because <c>EntityCoordinator&lt;T&gt;</c>
/// is registered open-generic and activated by reflection.
/// </summary>
public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null, bool refusesPlaintextTokens = true)
{
    public Func<Task<string>>? TokenProvider { get; } = tokenProvider;

    /// <summary>
    /// True when the client's endpoint is plaintext and it was not opted in to sending credentials
    /// over it, or when the transport is unknown. <c>AddIversonClient</c> computes it from the
    /// endpoint. An identity built by hand defaults to true (CSR round-11 #22: fail closed), so it
    /// says <c>false</c> only for a TLS endpoint or a known-local, opted-in one. While it is true,
    /// <c>EntityCoordinator&lt;T&gt;.WithActingUser</c> refuses a per-call token, and the
    /// <c>EntityCoordinator&lt;T&gt;</c> and <c>SchemaCatalogClient</c> constructors refuse an identity
    /// that carries a <see cref="TokenProvider"/>, as <c>AddIversonClient</c> refuses an ambient one.
    /// </summary>
    public bool RefusesPlaintextTokens { get; } = refusesPlaintextTokens;

    /// <summary>
    /// Returns <paramref name="identity"/> unchanged, or throws when it carries an ambient token
    /// provider while it refuses plaintext tokens. Called by the <c>EntityCoordinator&lt;T&gt;</c> and
    /// <c>SchemaCatalogClient</c> constructors.
    /// </summary>
    internal static ActingUserIdentity? RequireSendableAmbientToken(ActingUserIdentity? identity) =>
        identity is { TokenProvider: not null, RefusesPlaintextTokens: true }
            ? throw new InvalidOperationException(
                "Refusing an ambient acting-user token provider whose ActingUserIdentity refuses " +
                "plaintext tokens: the endpoint is plaintext without an explicit " +
                "allowInsecureChannelCallCredentials=true opt-in, or the transport is unknown (an " +
                "ActingUserIdentity built by hand defaults to refusesPlaintextTokens: true). The token " +
                "travels as raw Metadata, which no channel option guards. Pass " +
                "refusesPlaintextTokens: false only for a TLS endpoint or a known-local, non-TLS one.")
            : identity;
}
