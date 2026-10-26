namespace Iverson.Sql;

/// <summary>
/// CSR round 12 §2.3: the channel every api replica LISTENs on (<c>AuthInvalidationListener</c>). Every
/// write to the token-revocation table and to the tenant registry appends one of the clauses below, so it
/// notifies in its own statement: the notification is sent when, and only when, the write commits, and
/// once per row written. The payload is only a hint; a listener drops both of its caches on any
/// notification. <c>pg_notify</c> takes constants only, so the clause needs no privilege beyond the write.
/// </summary>
public static class AuthInvalidation
{
    public const string Channel = "iverson_auth_invalidation";

    internal const string ReturningTokenRevocationNotification = $"RETURNING pg_notify('{Channel}', 'token-revocation')";

    internal const string ReturningTenantStatusNotification = $"RETURNING pg_notify('{Channel}', 'tenant-status')";
}
