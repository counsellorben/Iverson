using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Iverson.StarRocks;

public static class TenantIdentifier
{
    private static readonly Regex AllowedPattern = new("^(?!.*--)([A-Za-z0-9_-]{1,52})$", RegexOptions.Compiled);

    public static bool IsValid(string tenantId) => AllowedPattern.IsMatch(tenantId);

    /// <summary>
    /// Database names accept hyphens when back-quoted, and accept more than 64 characters — both
    /// verified directly against <c>starrocks/allin1-ubuntu:4.1.1</c>. So a tenant id goes in
    /// verbatim here, and only <see cref="RoleName"/> needs encoding. Do not "fix" this to match
    /// the role encoding: the database name is what an operator greps for.
    /// </summary>
    internal static string DatabaseName(string tenantId) => $"iverson_tenant_{tenantId}";

    // ── role names ────────────────────────────────────────────────────────────────────────────

    private const string RolePrefix = "role_tenant_";

    /// <summary>StarRocks' hard limit on a role name. Binary-searched against a live 4.1.1: 64 is
    /// accepted, 65 is rejected with "invalid role format".</summary>
    private const int MaxRoleNameLength = 64;

    /// <summary>
    /// Base-36 digits needed for 128 bits: ceil(128 / log2(36)) = 25. Fixed width, so the whole
    /// role name has a constant length and the readable part has a constant budget.
    /// </summary>
    private const int HashDigits = 25;

    /// <summary>Whatever is left for the human-readable part after the prefix, hash and separator.</summary>
    private static readonly int ReadableBudget = MaxRoleNameLength - RolePrefix.Length - HashDigits - 1;

    /// <summary>
    /// The StarRocks role for a tenant: <c>role_tenant_{readable}_{hash}</c>.
    ///
    /// <para><b>Why not the tenant id verbatim.</b> StarRocks rejects a hyphen in a role name
    /// UNCONDITIONALLY — even back-quoted (`invalid role format`) — while
    /// <see cref="AllowedPattern"/> admits hyphens. Every hyphenated tenant could therefore be
    /// created through Iverson's own API and then never provisioned in StarRocks, so Search,
    /// Aggregate, GroupBy and Pipeline were all impossible for it. That included the dev acting
    /// tenant <c>tenant-bypass</c> and the documented <c>iverson-loadtest-dynamic</c>.</para>
    ///
    /// <para><b>Why a hash rather than an escape.</b> Role names are capped at 64 characters and
    /// the prefix costs 12, leaving 52 — exactly the tenant-id maximum. Tenant ids draw on 64
    /// symbols (<c>A-Za-z0-9_-</c>) and role names on 63 (no hyphen), so no injective encoding into
    /// the same width exists; an escape scheme needs up to 104 characters. Injectivity is not
    /// cosmetic here — two tenants sharing a role means each can read the other's database — so the
    /// hash is over the ORIGINAL id at 128 bits, where a collision is not reachable even if tenant
    /// ids are attacker-chosen.</para>
    ///
    /// <para><b>Consequence to know about.</b> This changes the role name for EVERY tenant, not
    /// just hyphenated ones. Provisioning is lazy and idempotent (<c>CREATE ROLE IF NOT EXISTS</c>
    /// on the type's next write), so deployments self-heal with no migration step. A previously
    /// provisioned tenant leaves its old role behind, still granted to <c>iverson_app</c> and still
    /// pointing at that same tenant's own database — clutter, not exposure. Drop them by hand if
    /// you care: <c>SHOW ROLES</c> lists them.</para>
    /// </summary>
    internal static string RoleName(string tenantId)
    {
        var readable = Sanitize(tenantId);
        if (readable.Length > ReadableBudget)
            readable = readable[..ReadableBudget];

        return $"{RolePrefix}{readable}_{Fingerprint(tenantId)}";
    }

    /// <summary>
    /// The readable half: every character a role name cannot carry becomes <c>_</c>. Lossy and
    /// deliberately so — <see cref="Fingerprint"/> is what keeps distinct tenants distinct, and
    /// this exists only so <c>SHOW ROLES</c> is greppable.
    /// </summary>
    private static string Sanitize(string tenantId)
    {
        var chars = tenantId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>
    /// The low 128 bits of SHA-256 over the tenant id, as exactly <see cref="HashDigits"/> base-36
    /// digits. Over the ORIGINAL id, never the sanitized one — sanitizing collapses <c>a-b</c> and
    /// <c>a_b</c> onto the same string, and it is precisely that collapse the fingerprint exists to
    /// undo.
    /// </summary>
    private static string Fingerprint(string tenantId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));

        // Big-endian accumulate the first 16 bytes into two ulongs, then emit base-36 digits from
        // the low end. A UInt128 keeps the whole 128 bits in play without BigInteger allocation.
        UInt128 value = 0;
        for (var i = 0; i < 16; i++)
            value = (value << 8) | digest[i];

        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var buffer = new char[HashDigits];
        for (var i = HashDigits - 1; i >= 0; i--)
        {
            buffer[i] = Digits[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer);
    }

    internal static string Qualify(string? tenantDatabase, string tableName) =>
        tenantDatabase is null ? $"`{tableName}`" : $"`{tenantDatabase}`.`{tableName}`";
}
