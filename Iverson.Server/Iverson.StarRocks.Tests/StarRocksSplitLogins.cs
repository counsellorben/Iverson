using MySqlConnector;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The two StarRocks logins production uses (CSR round 11 §3), set up through the fixture's root
/// repository with the job-create-user.yaml grant set, excluding the job's root SET PASSWORD and the
/// trailing ALTER USER 'iverson_app' SET PROPERTIES session caps (query timeout and memory limit).
/// iverson_app holds no grant at all (CSR round 12 §3.5: the readiness probe needs none) and runs no tenant
/// query (since CSR round 12 each tenant's queries run on that tenant's own login);
/// iverson_provisioner provisions tenants and holds user_admin plus a direct CREATE DATABASE.
///
/// <para><b>Why one helper.</b> The users live in the one container every class in
/// <see cref="StarRocksCollection"/> shares, so they outlive any one class. Every class that needs them
/// calls <see cref="EnsureAsync"/> from its constructor, and the statements end in the same state
/// whatever state they start from: CREATE ... IF NOT EXISTS and a repeated GRANT are no-ops, and a
/// REVOKE of something the user does not hold succeeds silently. The one REVOKE form that can fail, a
/// database-scoped one naming a database that does not exist, comes after CREATE DATABASE IF NOT
/// EXISTS iverson, as in the job.</para>
///
/// <para><b>Why OPERATE ON SYSTEM is revoked.</b> The job granted it until CSR round 12 for SHOW BACKENDS,
/// which the readiness probe no longer runs; an upgraded install still holds it, so the job revokes it. A
/// REVOKE of a privilege the user does not hold succeeds.</para>
///
/// <para><b>Why the connection strings name no default database.</b> Neither login holds a grant on a
/// database outside the tenant roles, and a connection whose default database the user cannot use
/// fails at the handshake. Every statement either login runs is qualified to its database.</para>
/// </summary>
internal static class StarRocksSplitLogins
{
    internal const string AppPassword = "test_pw";

    /// <summary>The key every test repository derives tenant login passwords from (CSR round 12 §7.1).</summary>
    internal const string TenantLoginKey = "test-tenant-login-key";
    private const string ProvisionerPassword = "test_provisioner_pw";

    public static async Task<(string App, string Provisioner)> EnsureAsync(StarRocksContainerFixture fx)
    {
        await fx.Repository.ExecuteAsync($"""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '{AppPassword}';
            CREATE USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '{ProvisionerPassword}';
            CREATE DATABASE IF NOT EXISTS iverson;
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';
            GRANT user_admin TO 'iverson_provisioner'@'%';
            REVOKE SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';
            REVOKE CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';
            REVOKE CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';
            REVOKE user_admin FROM 'iverson_app'@'%';
            REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%';
            """);

        return (ConnectionStringFor(fx, "iverson_app", AppPassword),
                ConnectionStringFor(fx, "iverson_provisioner", ProvisionerPassword));
    }

    private static string ConnectionStringFor(StarRocksContainerFixture fx, string user, string password) =>
        new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = user,
            Password = password,
            Database = ""
        }.ToString();
}
