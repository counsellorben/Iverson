namespace Iverson.Client.Core;

/// <summary>
/// The ambient acting-user identity, if one was configured on the client. A container-resolvable
/// type rather than a bare <c>Func&lt;Task&lt;string&gt;&gt;?</c>, because <c>EntityCoordinator&lt;T&gt;</c>
/// is registered open-generic and activated by reflection.
/// </summary>
public sealed class ActingUserIdentity(Func<Task<string>>? tokenProvider = null)
{
    public Func<Task<string>>? TokenProvider { get; } = tokenProvider;
}
