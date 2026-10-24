namespace Iverson.Api;

/// <summary>
/// CSR round 12 §2.4: who, besides an <c>admin</c>-scoped automation token, is an Operator on this install.
/// <list type="bullet">
/// <item><see cref="Subjects"/>, from <c>Authorization:OperatorSubjects</c>: the <c>sub</c> of each human
/// operator. The chart renders it from <c>global.operatorSubjects</c> as
/// <c>Authorization__OperatorSubjects__N</c>. A <c>sub</c> is Authentik's per-install hash of the user, not
/// predictable from a username, and an Authentik group membership, which the orchestrator can write, no
/// longer yields Operator.</item>
/// <item><see cref="GroupFallback"/>, from <c>Authorization:OperatorGroupFallback</c>: compose's dev-only
/// fallback. Compose's dev operator gets its <c>sub</c> only at first database init, so no static compose
/// list can name it; with the fallback set, members of that group are Operators too. Only
/// <c>docker-compose.yml</c> sets it, the api logs a warning at startup while it is set, and the chart
/// never renders it. An empty setting counts as unset.</item>
/// </list>
/// The policies read it at each decision, as the <c>ConsoleClient</c> policy reads its audience.
/// </summary>
public sealed record OperatorSubjects(IReadOnlySet<string> Subjects, string? GroupFallback)
{
    public static OperatorSubjects FromConfiguration(IConfiguration configuration) => new(
        new HashSet<string>(
            configuration.GetSection("Authorization:OperatorSubjects").Get<string[]>() ?? Array.Empty<string>(),
            StringComparer.Ordinal),
        configuration["Authorization:OperatorGroupFallback"] is { Length: > 0 } group ? group : null);
}
