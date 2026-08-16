using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// The coverage gate for <c>docs/standards/iverson-client-standard.md</c>. This is not a test of
/// application behaviour; it is a test of the standard itself and of the registry that binds it
/// to executable evidence. It fails the build whenever the document, the registry
/// (<see cref="Requirements"/>), and the orchestrator source fall out of agreement.
///
/// Three independent checks:
/// 1. The set of `Active` IDs declared by requirement-table rows in the standard must exactly
///    equal the set of consts reflected off <see cref="Requirements"/>.
/// 2. Every const's C# identifier must appear at least once under
///    <c>Iverson.Server/Iverson.ClientConformance/</c>, excluding <c>Requirements.cs</c> itself
///    and the test project — i.e. it must be cited by an assertion the orchestrator actually
///    constructs, not merely declared.
/// 3. Every ID (Active or Retired) parsed from the standard must match
///    <c>IVC-[A-Z]+-\d{3}</c> with an axis drawn from the standard's known nine-axis set, and no
///    `|`-leading line inside a requirement table may be left unparsed (see
///    <see cref="RequirementTableParser"/> — an unparsed row must never silently drop the rows
///    that follow it from checks 1 and 3).
/// </summary>
public class RequirementsCoverageGateTests
{
    private static readonly Regex IdShapePattern = new(@"^IVC-([A-Z]+)-\d{3}$", RegexOptions.Compiled);

    private static readonly string[] KnownAxes =
    {
        "DECL", "REL", "REG", "IDN", "LIFE", "QRY", "VEC", "SCH", "ERR",
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iverson.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not locate repository root (a directory containing Iverson.slnx) by walking up from {AppContext.BaseDirectory}.");
        }

        return dir.FullName;
    }

    private static string StandardPath() =>
        Path.Combine(RepositoryRoot(), "docs", "standards", "iverson-client-standard.md");

    private static string ConformanceSourceDir() =>
        Path.Combine(RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance");

    /// <summary>
    /// Rows declared out of a requirement table: `| ID | Status | Kind | Statement |`. IDs
    /// mentioned in prose elsewhere in the document are not requirement declarations and must
    /// never be parsed as such — only rows that live inside one of the axis tables count. See
    /// <see cref="RequirementTableParser"/> for the parsing rules, including how malformed rows
    /// are handled.
    /// </summary>
    private static List<(string Id, string Status)> ParseDeclaredRequirements(string markdown) =>
        RequirementTableParser.Parse(markdown).Rows;

    private static List<string> ReflectRegistryConsts()
    {
        return typeof(Requirements)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
    }

    private static Dictionary<string, string> ReflectRegistryConstsByIdentifier()
    {
        return typeof(Requirements)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
    }

    private static IEnumerable<string> ConformanceSourceFiles()
    {
        var root = ConformanceSourceDir();
        var testProjectDir = Path.Combine(RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance.Tests");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(root, "bin"), StringComparison.Ordinal))
            .Where(f => !f.StartsWith(Path.Combine(root, "obj"), StringComparison.Ordinal))
            .Where(f => !f.StartsWith(testProjectDir, StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "Requirements.cs");
    }

    [Fact]
    public void Check1_ActiveIdsInStandard_ExactlyMatchConstsInRegistry()
    {
        var markdown = File.ReadAllText(StandardPath());
        var declared = ParseDeclaredRequirements(markdown);

        var activeIds = declared
            .Where(r => r.Status == "Active")
            .Select(r => r.Id)
            .ToHashSet();

        var registryIds = ReflectRegistryConsts().ToHashSet();

        var missingFromRegistry = activeIds.Except(registryIds).ToList();
        var missingFromStandard = registryIds.Except(activeIds).ToList();

        missingFromRegistry.Should().BeEmpty(
            "every Active requirement in the standard must have a matching const in Requirements.cs, " +
            $"but these Active IDs have no const: {string.Join(", ", missingFromRegistry)}");
        missingFromStandard.Should().BeEmpty(
            "every const in Requirements.cs must correspond to an Active requirement row in the standard, " +
            $"but these consts have no matching Active row: {string.Join(", ", missingFromStandard)}");
    }

    [Fact]
    public void Check2_EveryRegistryConst_IsCitedByAssertionCodeOutsideRequirementsAndTests()
    {
        var constsByIdentifier = ReflectRegistryConstsByIdentifier();
        var sourceFiles = ConformanceSourceFiles().ToList();

        var uncited = new List<string>();

        foreach (var (identifier, _) in constsByIdentifier)
        {
            var cited = sourceFiles.Any(f => File.ReadAllText(f).Contains(identifier, StringComparison.Ordinal));
            if (!cited)
            {
                uncited.Add(identifier);
            }
        }

        uncited.Should().BeEmpty(
            "every const in Requirements.cs must be cited by an assertion the orchestrator constructs " +
            $"under Iverson.ClientConformance/ (excluding Requirements.cs and the test project), but these are uncited: {string.Join(", ", uncited)}");
    }

    [Fact]
    public void Check3_NoUnparsableRowsInAnyRequirementTable()
    {
        var markdown = File.ReadAllText(StandardPath());
        var malformed = RequirementTableParser.Parse(markdown).MalformedLines;

        malformed.Should().BeEmpty(
            "every `|`-leading line inside a requirement table must parse as a well-formed " +
            $"`| ID | Status | Kind | Statement |` row, but these did not: {string.Join(" ~~~ ", malformed)}");
    }

    [Fact]
    public void Check3_EveryDeclaredId_MatchesShapeWithKnownAxis()
    {
        var markdown = File.ReadAllText(StandardPath());
        var declared = ParseDeclaredRequirements(markdown);

        var malformed = new List<string>();

        foreach (var (id, _) in declared)
        {
            var match = IdShapePattern.Match(id);
            if (!match.Success)
            {
                malformed.Add($"{id} (does not match IVC-[A-Z]+-\\d{{3}})");
                continue;
            }

            var axis = match.Groups[1].Value;
            if (!KnownAxes.Contains(axis))
            {
                malformed.Add($"{id} (unknown axis '{axis}')");
            }
        }

        malformed.Should().BeEmpty(
            $"every requirement ID must match IVC-<AXIS>-NNN with an axis from the known set, but these are malformed: {string.Join(", ", malformed)}");
    }

    [Fact]
    public void Check3_DeclaredIds_AreUniqueAcrossTheDocument()
    {
        var markdown = File.ReadAllText(StandardPath());
        var declared = ParseDeclaredRequirements(markdown);

        var duplicates = declared
            .GroupBy(r => r.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty($"every requirement ID must be unique across the document, but these repeat: {string.Join(", ", duplicates)}");
    }
}
