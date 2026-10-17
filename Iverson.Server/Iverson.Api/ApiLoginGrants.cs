using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;

namespace Iverson.Api;

/// <summary>
/// What the api's Postgres login, <see cref="PostgresSchemaManager.ApiLoginName"/>, may do beyond
/// its <c>iverson_runtime</c> membership, which covers every entity table (CSR round 12 §6.2). The
/// worker issues these at startup, as the database owner, in one transaction
/// (<see cref="WorkerStoreBootstrap"/>), and every one is idempotent. Nothing else grants the login
/// anything: it owns nothing, holds no BYPASSRLS, is no member of <c>iverson_maintenance</c>, and
/// has no CREATE on any schema. Tables not listed here, the document re-render queue and the
/// enrichment state, are the worker's alone.
/// </summary>
internal static class ApiLoginGrants
{
    private const string Login = PostgresSchemaManager.ApiLoginName;

    public static readonly IReadOnlyList<string> Statements =
    [
        // On Helm the Authentik hook revokes PUBLIC's CONNECT on this database, and post-upgrade
        // hooks only run once the release is ready, which needs this login connected: so the worker
        // grants it. The database name differs between Helm, compose and the test containers.
        $"DO $$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO {Login}', current_database()); END $$",

        // The outbox: rows inserted in the same transaction as each entity write (including Delete's
        // EventType and Payload), and the fast path's delete of its own row by Id. Payloads stay
        // unreadable.
        $"""GRANT INSERT, DELETE ON "{ReconciliationSchema.TableName}" TO {Login}""",
        $"""GRANT SELECT ("Id") ON "{ReconciliationSchema.TableName}" TO {Login}""",

        // The /admin/dlq list and the replay route's checks: every column except MessageValue, which
        // only the worker reads. A query that selects * is therefore refused, so the api's DLQ
        // queries name their columns.
        $"""GRANT SELECT ("Id", "SourceTopic", "ConsumerGroup", "MessageKey", "ExceptionType", "ExceptionMessage", "Attempts", "FailedAt", "Replayed", "TenantId") ON "{DlqSchema.TableName}" TO {Login}""",

        // Tenant lifecycle (including CreateTenant's rollback delete), the status cache and the
        // console readers.
        $"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{TenantSchema.TableName}" TO {Login}""",

        // RemoveUser and SetTenantAdmin's revocation upsert, and the revocation cache.
        $"GRANT SELECT, INSERT, UPDATE ON iverson_token_revocations TO {Login}",

        // Loading the registry. Registration writes it through the worker (spec §5).
        $"GRANT SELECT ON _iverson_schema TO {Login}",

        // Spec §5's privileged requests: the api queues them, polls them, and expires its own.
        $"""GRANT INSERT, SELECT ON "{PrivilegedRequestSchema.TableName}" TO {Login}""",
        $"""GRANT UPDATE ("Status") ON "{PrivilegedRequestSchema.TableName}" TO {Login}""",

        // Spec §7.3's provisioned-tenant check before every StarRocks read. Only the worker records.
        $"""GRANT SELECT ON "{StarRocksTenantSchema.TableName}" TO {Login}""",
    ];

    /// <summary>
    /// /health's grants check. One-argument <c>has_table_privilege</c> checks <c>current_user</c>:
    /// for the api that is its own login, and for the worker, which owns the table, it is always
    /// true. The worker grants everything in <see cref="Statements"/> in one transaction, so this
    /// one privilege stands for all of them. It raises 42P01, which /health reads as false, until
    /// the worker has created the table.
    /// </summary>
    public static readonly string ReadinessProbeSql =
        $"""SELECT has_table_privilege('"{ReconciliationSchema.TableName}"', 'INSERT')""";
}
