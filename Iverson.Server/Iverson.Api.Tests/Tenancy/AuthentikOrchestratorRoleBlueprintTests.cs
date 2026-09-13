using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// Guards the scoped <c>iverson-admin-orchestrator</c> RBAC role (CSR round-3 finding #1) in the
/// TWO places it is declared, and guards them against each other.
///
/// WHY THIS EXISTS. The role is declared twice: once in the Helm template
/// (<c>charts/authentik/templates/blueprints-secret-service-clients.yaml</c>, which is what
/// kind/cloud deploys) and once in the hand-rendered docker-compose mirror
/// (<c>charts/authentik/blueprints/compose-only/service-clients.yaml</c>, which is also what
/// <see cref="AuthentikContainerFixture"/> applies). CSR round 1 replaced the orchestrator's
/// blanket superuser grant with a scoped role in the Helm template ONLY — the compose mirror kept
/// <c>is_superuser: true</c> for a further round, which meant the dev/compose target ran the
/// tenant orchestrator as a full Authentik superuser and that no live test could have caught it.
/// Nothing but a check like this one notices that class of drift.
///
/// This is deliberately a cheap text-level assertion, NOT a container test: it is the live
/// <see cref="AuthentikRecoveryFlowIntegrationTests"/> facts that prove the role actually behaves
/// as scoped, and those run against the compose mirror. This test's job is the other half —
/// proving the mirror still says what the deployed template says.
/// </summary>
public sealed class AuthentikOrchestratorRoleBlueprintTests
{
    /// <summary>
    /// The only global model permissions the orchestrator may hold. Every other verb it needs is
    /// object-scoped through the InitialPermissions entry asserted below. Changing this list is a
    /// security decision — it should fail here first, loudly, and be justified in the blueprint's
    /// own comment before the list is edited.
    /// </summary>
    private static readonly string[] AllowedGlobalPermissions =
    [
        "authentik_core.add_user",
        "authentik_core.view_user",
        "authentik_core.view_group"
    ];

    /// <summary>Permissions granted per-object on users the orchestrator itself creates.</summary>
    private static readonly string[] ExpectedInitialPermissions =
    [
        "change_user",
        "reset_user_password"
    ];

    public static TheoryData<string> BlueprintFiles() => new()
    {
        Path.Combine(AuthentikChartDirectory(), "templates", "blueprints-secret-service-clients.yaml"),
        Path.Combine(AuthentikChartDirectory(), "blueprints", "compose-only", "service-clients.yaml")
    };

    [Theory]
    [MemberData(nameof(BlueprintFiles))]
    public void OrchestratorRole_GrantsOnlyTheAllowedGlobalPermissions(string blueprintPath)
    {
        File.Exists(blueprintPath).Should().BeTrue($"'{blueprintPath}' must exist");
        var lines = File.ReadAllLines(blueprintPath);

        ListAfter(lines, "name: iverson-admin-orchestrator", "permissions:", line => line.StartsWith("- authentik_"))
            .Select(line => line[2..])
            .Should().BeEquivalentTo(AllowedGlobalPermissions,
                $"a global grant in '{Path.GetFileName(blueprintPath)}' applies to every user and " +
                "group in the IdP, not just the ones this service created (CSR round-3 finding #1)");
    }

    [Theory]
    [MemberData(nameof(BlueprintFiles))]
    public void OrchestratorRole_ObjectScopesTheAccountTakeoverVerbsViaInitialPermissions(string blueprintPath)
    {
        var lines = File.ReadAllLines(blueprintPath);

        var codenames = ListAfter(
                lines,
                "name: iverson-admin-orchestrator-created-users",
                "permissions:",
                line => line.StartsWith("- !Find [auth.permission,"))
            .Select(CodenameOf)
            .ToArray();

        codenames.Should().BeEquivalentTo(ExpectedInitialPermissions,
            $"'{Path.GetFileName(blueprintPath)}' must object-scope exactly the verbs that let the " +
            "orchestrator take over an account, and no others");
    }

    [Theory]
    [MemberData(nameof(BlueprintFiles))]
    public void OrchestratorGroup_IsNotASuperuserGroup(string blueprintPath)
    {
        // Comments are excluded on purpose: both files DISCUSS the removed `is_superuser: true`
        // grant in their rationale, and a naive whole-file string search would fail on the very
        // comment that explains the fix.
        var directives = File.ReadAllLines(blueprintPath)
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith('#'))
            .ToArray();

        directives.Should().Contain("is_superuser: false",
            $"'{Path.GetFileName(blueprintPath)}' must pin the orchestrator group's superuser flag " +
            "OFF explicitly — blueprint attrs are a partial update, so merely omitting the flag " +
            "leaves a pre-existing is_superuser: true in place and the scoped role decorative");
        directives.Should().NotContain("is_superuser: true",
            $"nothing in '{Path.GetFileName(blueprintPath)}' may grant superuser; the orchestrator's " +
            "blanket superuser grant is exactly what the scoped role replaced");
    }

    /// <summary>
    /// Collects the items of a YAML list that follows <paramref name="listKey"/>, which in turn
    /// follows <paramref name="anchor"/>. Indentation-insensitive (the two files nest the same
    /// entries at different depths, since one is embedded in a Helm Secret's stringData), which is
    /// what lets one assertion cover both.
    /// </summary>
    private static List<string> ListAfter(
        string[] lines,
        string anchor,
        string listKey,
        Func<string, bool> isItem)
    {
        var anchorIndex = Array.FindIndex(lines, line => line.Trim() == anchor);
        anchorIndex.Should().BeGreaterThanOrEqualTo(0, $"the blueprint must declare '{anchor}'");

        var listIndex = Array.FindIndex(lines, anchorIndex, line => line.Trim() == listKey);
        listIndex.Should().BeGreaterThanOrEqualTo(0, $"'{anchor}' must be followed by '{listKey}'");

        var items = new List<string>();
        for (var i = listIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (!isItem(trimmed))
                break;
            items.Add(trimmed);
        }

        items.Should().NotBeEmpty($"'{listKey}' after '{anchor}' must not be empty");
        return items;
    }

    /// <summary>Pulls <c>change_user</c> out of <c>- !Find [auth.permission, [codename, change_user], …]</c>.</summary>
    private static string CodenameOf(string findTag)
    {
        const string marker = "[codename, ";
        var start = findTag.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{findTag}' must look up the permission by codename");
        start += marker.Length;
        var end = findTag.IndexOf(']', start);
        return findTag[start..end].Trim();
    }

    /// <summary>
    /// Resolves the chart directory from this source file's own path, so the test fails if the
    /// blueprints are moved rather than silently passing against nothing.
    /// </summary>
    private static string AuthentikChartDirectory([CallerFilePath] string thisFilePath = "")
    {
        var serverDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFilePath)))!;
        return Path.Combine(serverDir, "deploy", "helm", "iverson", "charts", "authentik");
    }
}
