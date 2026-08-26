using Iverson.StarRocks;

namespace Iverson.Api;

/// <summary>
/// How <c>/health</c> spells the engagement-store check in its response body.
/// <para>
/// A pure function beside <see cref="ReadinessPolicy"/> for the same reason that one exists:
/// the <c>/health</c> lambda in <c>Program.cs</c> is unreachable from a unit test, so the two
/// decisions it makes — the readiness verdict that picks the status code, and the wire spelling
/// that goes in the body — both live out here where they can be pinned directly.
/// </para>
/// <para>
/// <b><see cref="EngagementHealthStatus"/> has THREE values and this projection keeps all three.</b>
/// Flattening it to a boolean is what put a red "StarRocks: Down" chip on a correctly-progressing
/// fresh install: <see cref="ReadinessPolicy.Evaluate"/> deliberately treats
/// <see cref="EngagementHealthStatus.AuthPending"/> as ready — the create-user post-install hook
/// cannot run until this very probe passes, so failing on it would deadlock every first install —
/// and the body then said <c>starrocks: false</c>, which the console can only render as DOWN.
/// <c>"authPending"</c> is a fourth wire value alongside <c>"disabled"</c> so the console can say
/// what is actually happening. Consumers that only read the status code are unaffected.
/// </para>
/// </summary>
public static class HealthCheckWireFormat
{
    /// <summary>The engagement store is switched off in this deployment. Neither up nor down.</summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// StarRocks is reachable but the <c>iverson_app</c> user does not exist yet — expected on a
    /// fresh install, before the create-user post-install hook has run. NOT a fault, and
    /// <see cref="ReadinessPolicy"/> counts it as ready.
    /// </summary>
    public const string AuthPending = "authPending";

    /// <summary>
    /// The value of <c>checks.starrocks</c>: <c>true</c>, <c>false</c>,
    /// <see cref="AuthPending"/> or <see cref="Disabled"/>.
    /// <para>
    /// Returns <see cref="object"/> because the four are not one type. The property it feeds is
    /// therefore serialized polymorphically by its runtime type, exactly as the
    /// boolean-or-<c>"disabled"</c> ternary this replaces already was.
    /// </para>
    /// </summary>
    public static object StarRocksCheck(EngagementHealthStatus status, bool engagementEnabled)
    {
        // Checked before the switch: a disabled store is never probed, so whatever status the
        // fan-out happened to record for it says nothing.
        if (!engagementEnabled) return Disabled;

        return status switch
        {
            EngagementHealthStatus.Healthy     => true,
            EngagementHealthStatus.AuthPending => AuthPending,
            EngagementHealthStatus.Unhealthy   => false,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status,
                $"Unhandled {nameof(EngagementHealthStatus)} value — add a case above.")
        };
    }
}
