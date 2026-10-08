using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// Guards the shipped <c>orchestrator-federation.yaml</c> blueprint (CSR round-10 #4, sub-project
/// C2) and the service-clients entries it depends on, at text level.
///
/// WHY THIS EXISTS. Authentik checks only the signature and expiry of a federated JWT, so the pin
/// policy, the in-cluster-only marker and the "all, not any" policy engine mode are the only things
/// that limit minting to the API's own service account. The live
/// <see cref="AuthentikRecoveryFlowIntegrationTests"/> federation fact proves those expressions
/// behave; it takes them from this file's helpers, so it runs the shipped text. This class is the
/// other half: it proves the structure around them (engine mode, bindings, clauses, source and
/// provider wiring, the pre-created user and the retired token) is still what was tested, with no
/// cluster. Nothing else notices a binding dropped or a mode reverted.
/// </summary>
public sealed class AuthentikOrchestratorFederationBlueprintTests
{
    private const string HelmSubject = "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-api";
    private const string WorkloadUser = "iverson-admin-orchestrator-workload";

    private static string TemplatePath(string name) =>
        Path.Combine(AuthentikOrchestratorRoleBlueprintTests.AuthentikChartDirectory(), "templates", name);

    // ---- helpers shared with the live fact ----------------------------------------------------

    /// <summary>The shipped pin policy expression, with the Helm subject replaced by <paramref name="subject"/>.</summary>
    internal static string PinPolicyExpression(string subject)
    {
        var expression = EntryExpression(FederationEntry("name: iverson-admin-orchestrator-workload"));
        expression.Should().Contain(HelmSubject, "the shipped pin carries the Helm-templated subject");
        return expression.Replace(HelmSubject, subject);
    }

    /// <summary>The shipped in-cluster-only marker policy expression.</summary>
    internal static string InClusterOnlyExpression() =>
        EntryExpression(FederationEntry("name: iverson-admin-orchestrator-in-cluster-only"));

    /// <summary>The shipped username property-mapping expression.</summary>
    internal static string UsernameMappingExpression()
    {
        var entry = FederationEntry("name: \"Iverson: k8s workload username\"");
        var line = entry.Single(l => l.Trim().StartsWith("expression: '", StringComparison.Ordinal)).Trim();
        return line["expression: '".Length..^1];
    }

    // ---- facts ---------------------------------------------------------------------------------

    [Fact]
    public void OrchestratorApp_RequiresEveryBinding_NotAny()
    {
        FederationEntry("slug: iverson-admin-orchestrator")
            .Select(l => l.Trim())
            .Should().Contain("policy_engine_mode: all",
                "with the default 'any' the marker policy alone would admit every in-cluster request");
    }

    [Fact]
    public void OrchestratorApp_HasExactlyThePinAndMarkerBindings_InOrder()
    {
        var bindings = FederationEntries()
            .Where(e => e.Any(l => l.Trim() == "model: authentik_policies.policybinding"))
            .ToList();

        bindings.Should().HaveCount(2);
        foreach (var binding in bindings)
            binding.Select(l => l.Trim()).Should().Contain("target: !KeyOf orchestrator-app");

        Has(bindings, "policy: !KeyOf orchestrator-workload", "order: 0").Should().BeTrue("the pin is order 0");
        Has(bindings, "policy: !KeyOf orchestrator-in-cluster-only", "order: 1").Should().BeTrue("the marker is order 1");

        static bool Has(List<List<string>> all, string policy, string order) =>
            all.Count(b => b.Select(l => l.Trim()).Contains(policy) && b.Select(l => l.Trim()).Contains(order)) == 1;
    }

    [Fact]
    public void PinExpression_PinsSubjectAudienceAndGroupGate()
    {
        var pin = PinPolicyExpression("SUBJECT");

        pin.Should().Contain("jwt.get(\"sub\") == \"SUBJECT\"");
        pin.Should().Contain("\"iverson-authentik-orchestrator\" in auds");
        pin.Should().Contain("ak_is_group_member(user, name=\"iverson-admin-orchestrators\")");
        pin.Should().Contain("user is not None");
        // The Helm form must still pin the API's own service account.
        EntryExpression(FederationEntry("name: iverson-admin-orchestrator-workload"))
            .Should().Contain($"jwt.get(\"sub\") == \"{HelmSubject}\"");
    }

    [Fact]
    public void InClusterMarker_RefusesThePublicIngressHeader() =>
        InClusterOnlyExpression().Trim()
            .Should().Be("return \"HTTP_X_IVERSON_PUBLIC_INGRESS\" not in request.http_request.META",
                "the marker is the only thing that refuses an exchange arriving through the public listener");

    [Fact]
    public void Source_ReadsTheRelay_AndProviderFederatesFromIt()
    {
        var source = FederationEntry("slug: iverson-k8s-workload").Select(l => l.Trim()).ToList();
        source.Should().Contain("oidc_jwks_url: http://127.0.0.1:8001/openid/v1/jwks");
        source.Should().Contain("- !KeyOf workload-username");

        var provider = FederationEntry("client_id: iverson-admin-orchestrator").Select(l => l.Trim()).ToList();
        var at = provider.IndexOf("jwt_federation_sources:");
        at.Should().BeGreaterThanOrEqualTo(0);
        provider[at + 1].Should().Be("- !KeyOf k8s-workload-source");
    }

    [Fact]
    public void WorkloadUser_HasNoPassword_AndIsInTheOrchestratorGroup()
    {
        var user = ServiceClientEntries().Single(e =>
            e.Any(l => l.Trim() == "model: authentik_core.user") &&
            e.Any(l => l.Trim() == $"username: {WorkloadUser}"))
            .Select(l => l.Trim()).ToList();

        user.Should().NotContain(l => l.StartsWith("password:", StringComparison.Ordinal));
        user.Should().NotContain("state: absent");
        user.Should().Contain(l => l.Contains("iverson-admin-orchestrators") && l.StartsWith("- !Find [authentik_core.group", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticOrchestratorToken_AndItsUser_AreRetired_TokenFirst()
    {
        var entries = ServiceClientEntries();
        var token = entries.FindIndex(e => e.Any(l => l.Trim() == "model: authentik_core.token") &&
                                           e.Any(l => l.Trim() == "identifier: iverson-admin-orchestrator-token"));
        var user = entries.FindIndex(e => e.Any(l => l.Trim() == "model: authentik_core.user") &&
                                         e.Any(l => l.Trim() == "username: iverson-admin-orchestrator"));

        token.Should().BeGreaterThanOrEqualTo(0);
        user.Should().BeGreaterThanOrEqualTo(0);
        entries[token].Select(l => l.Trim()).Should().Contain("state: absent");
        entries[user].Select(l => l.Trim()).Should().Contain("state: absent");
        token.Should().BeLessThan(user, "the token is removed before the user that owns it");
    }

    // ---- parsing -------------------------------------------------------------------------------

    private static string[] FederationBlockLines()
    {
        var lines = File.ReadAllLines(TemplatePath("blueprints-configmap.yaml"));
        var key = Array.FindIndex(lines, l => l == "  orchestrator-federation.yaml: |");
        key.Should().BeGreaterThanOrEqualTo(0, "the ConfigMap must carry orchestrator-federation.yaml");
        var block = new List<string>();
        for (var i = key + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0 && !lines[i].StartsWith("    ", StringComparison.Ordinal)) break;
            block.Add(lines[i]);
        }
        return [.. block];
    }

    private static List<List<string>> FederationEntries() => SplitEntries(FederationBlockLines());

    private static List<List<string>> ServiceClientEntries() =>
        SplitEntries(File.ReadAllLines(TemplatePath("secret-service-clients.yaml")));

    /// <summary>The single federation entry containing a line equal to <paramref name="marker"/>.</summary>
    private static List<string> FederationEntry(string marker)
    {
        var matches = FederationEntries().Where(e => e.Any(l => l.Trim() == marker)).ToList();
        matches.Should().HaveCount(1, $"exactly one federation entry must contain '{marker}'");
        return matches[0];
    }

    /// <summary>Splits the list after the first <c>entries:</c> key into entries, by the list-item indent.</summary>
    private static List<List<string>> SplitEntries(string[] lines)
    {
        var entriesAt = Array.FindIndex(lines, l => l.Trim() == "entries:");
        entriesAt.Should().BeGreaterThanOrEqualTo(0);
        var first = Array.FindIndex(lines, entriesAt + 1, l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        var indent = lines[first].Length - lines[first].TrimStart().Length;

        var entries = new List<List<string>>();
        for (var i = first; i < lines.Length; i++)
        {
            var isStart = lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal) &&
                          lines[i].Length - lines[i].TrimStart().Length == indent;
            if (isStart) entries.Add([]);
            // The entry's first line is "- id: x" / "- model: x"; expose it as a plain key line.
            entries[^1].Add(isStart ? new string(' ', indent + 2) + lines[i].TrimStart()[2..] : lines[i]);
        }
        return entries;
    }

    /// <summary>The <c>expression: |</c> block of an entry, dedented to its first line.</summary>
    private static string EntryExpression(List<string> entry)
    {
        var at = entry.FindIndex(l => l.Trim() == "expression: |");
        at.Should().BeGreaterThanOrEqualTo(0, "the entry must hold a block-scalar expression");
        var keyIndent = entry[at].Length - entry[at].TrimStart().Length;
        var body = entry.Skip(at + 1)
            .TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > keyIndent)
            .ToList();
        var strip = body.First(l => l.Trim().Length > 0).Length - body.First(l => l.Trim().Length > 0).TrimStart().Length;
        return string.Join('\n', body.Select(l => l.Length >= strip ? l[strip..] : l.Trim())).TrimEnd() + "\n";
    }
}
