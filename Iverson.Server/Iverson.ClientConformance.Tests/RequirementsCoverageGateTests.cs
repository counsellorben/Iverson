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
/// Four independent checks:
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
/// 4. Every authored axis's `#### Coverage` ledger must bind its claimed areas to that axis's
///    `Active` requirements bidirectionally: every Active requirement must be claimed by exactly
///    one Covered area — NOT merely at least one; two areas claiming the same ID is Mode 7 —
///    every Covered/Deferred row must be well-formed and cite only existing, Active, same-axis
///    IDs, and an axis with at least one Active requirement must have a `#### Coverage` table at
///    all (see <see cref="CoverageTableParser"/> and <c>ComputeCoverageFailures</c> below for the
///    seven failure modes enforced).
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

    /// <summary>
    /// The computation behind Check4, factored out so it can be exercised against fixture markdown
    /// (proving individual failure modes fire, and only the expected one) as well as against the
    /// live standard. See <c>docs/specs/2026-08-17-axis-completeness-check-design.md</c>
    /// ("The check") for the six failure modes this enforces.
    /// </summary>
    private static List<string> ComputeCoverageFailures(string markdown)
    {
        var declared = ParseDeclaredRequirements(markdown);
        var active = declared.Where(r => r.Status == "Active").ToList();
        var retiredIds = declared.Where(r => r.Status == "Retired").Select(r => r.Id).ToHashSet();

        // Every requirement's axis, derived from its ID shape (A5). IDs that don't match the shape
        // or carry an unknown axis are already caught by Check3 and are excluded here.
        string? AxisOf(string id)
        {
            var match = IdShapePattern.Match(id);
            if (!match.Success)
            {
                return null;
            }

            var axis = match.Groups[1].Value;
            return KnownAxes.Contains(axis) ? axis : null;
        }

        var activeByAxis = active
            .Select(r => (r.Id, Axis: AxisOf(r.Id)))
            .Where(r => r.Axis is not null)
            .GroupBy(r => r.Axis!, r => r.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allIdsByAxis = declared
            .Select(r => (r.Id, r.Status, Axis: AxisOf(r.Id)))
            .Where(r => r.Axis is not null)
            .ToLookup(r => r.Axis!, r => (r.Id, r.Status));

        var coverage = CoverageTableParser.Parse(markdown, KnownAxes);
        var failures = new List<string>();

        // Mode 6: a malformed coverage row.
        foreach (var line in coverage.MalformedLines)
        {
            failures.Add($"malformed coverage row: {line}");
        }

        // Mode 1: an axis with >=1 Active requirement and no #### Coverage table.
        var axesWithLedger = coverage.Rows.Where(r => r.Axis is not null).Select(r => r.Axis!).ToHashSet();
        foreach (var axis in activeByAxis.Keys)
        {
            if (!axesWithLedger.Contains(axis))
            {
                failures.Add($"axis '{axis}' has {activeByAxis[axis].Count} Active requirement(s) but no #### Coverage table");
            }
        }

        // axis -> requirement ID -> the Covered areas that claimed it. A LIST, not a set of IDs:
        // Check4's contract is EXACTLY ONE Covered area per Active requirement (Mode 7 below), and a
        // HashSet<string> of IDs could only ever enforce AT LEAST ONE. That distinction is
        // load-bearing, not pedantic — it is the only MECHANICAL thing stopping a second area from
        // being labelled `Covered | <some existing ID>` and quietly widening that requirement to
        // cover a rule its Statement does not make (Ruling 14's caveat, Ruling 32).
        var claimedByAxis = new Dictionary<string, Dictionary<string, List<string>>>();

        foreach (var row in coverage.Rows)
        {
            if (row.Axis is null)
            {
                // Attributed to no axis (A15) — not part of any axis's binding.
                continue;
            }

            // Mode 2: Status must be exactly Covered or Deferred.
            if (row.Status != "Covered" && row.Status != "Deferred")
            {
                failures.Add($"axis '{row.Axis}' area '{row.Area}' has Status '{row.Status}', which is neither Covered nor Deferred");
                continue;
            }

            if (row.Status == "Deferred")
            {
                // Mode 4: a Deferred area with an empty reason.
                if (row.Evidence.Trim().Length == 0)
                {
                    failures.Add($"axis '{row.Axis}' area '{row.Area}' is Deferred with an empty reason");
                }

                continue;
            }

            // Status == "Covered": Mode 3 — must cite >=1 existing, Active, same-axis, non-Retired ID.
            var ids = row.Evidence.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (ids.Length == 0)
            {
                failures.Add($"axis '{row.Axis}' area '{row.Area}' is Covered but cites no requirement ID");
                continue;
            }

            if (!claimedByAxis.TryGetValue(row.Axis, out var claimed))
            {
                claimed = new Dictionary<string, List<string>>();
                claimedByAxis[row.Axis] = claimed;
            }

            foreach (var id in ids)
            {
                var idAxis = AxisOf(id);

                // Foreign-axis citation must be diagnosed before the existence check: a foreign-axis
                // ID is by definition absent from allIdsByAxis[row.Axis], so testing existence first
                // would swallow every foreign-axis case into the generic "does not exist" message
                // and make this branch dead code.
                if (idAxis is not null && idAxis != row.Axis)
                {
                    failures.Add($"axis '{row.Axis}' area '{row.Area}' cites '{id}', which belongs to axis '{idAxis}', not '{row.Axis}'");
                    continue;
                }

                if (!allIdsByAxis[row.Axis].Any(r => r.Id == id))
                {
                    failures.Add($"axis '{row.Axis}' area '{row.Area}' cites '{id}', which does not exist in axis '{row.Axis}'");
                    continue;
                }

                if (retiredIds.Contains(id))
                {
                    failures.Add($"axis '{row.Axis}' area '{row.Area}' cites '{id}', which is Retired");
                    continue;
                }

                if (!claimed.TryGetValue(id, out var claimants))
                {
                    claimants = new List<string>();
                    claimed[id] = claimants;
                }

                claimants.Add(row.Area);
            }
        }

        // Modes 5 and 7: every Active requirement is claimed by EXACTLY ONE Covered area — Mode 5
        // catches zero, Mode 7 catches two or more.
        foreach (var (axis, ids) in activeByAxis)
        {
            var claimed = claimedByAxis.TryGetValue(axis, out var c)
                ? c
                : new Dictionary<string, List<string>>();

            foreach (var id in ids)
            {
                if (!claimed.TryGetValue(id, out var claimants) || claimants.Count == 0)
                {
                    failures.Add($"'{id}' (axis '{axis}') is Active but claimed by no Covered area");
                    continue;
                }

                if (claimants.Count > 1)
                {
                    failures.Add(
                        $"'{id}' (axis '{axis}') is claimed by {claimants.Count} Covered areas "
                        + $"({string.Join("; ", claimants.Select(a => $"'{a}'"))}), but a requirement must be "
                        + "claimed by exactly one — a second area claiming it widens the requirement to cover "
                        + "a rule its Statement does not make. Author a requirement of its own for the second area.");
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// The axis-completeness check: binds each authored axis's `#### Coverage` ledger to its
    /// `Active` requirements, bidirectionally, against the live standard.
    /// </summary>
    [Fact]
    public void Check4_AxisCoverageLedgers_BindClaimedAreasToActiveRequirements()
    {
        var markdown = File.ReadAllText(StandardPath());
        var failures = ComputeCoverageFailures(markdown);

        failures.Should().BeEmpty(
            "every authored axis's #### Coverage ledger must bind claimed areas to its Active requirements bidirectionally, " +
            $"but these violations were found: {string.Join(" ~~~ ", failures)}");
    }

    [Fact]
    public void Check4_DeferredAreaWithEmptyReason_FailsNamingAxisAndArea_ViaMode4()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some area | Covered | IVC-REG-002 |
            | Reregistration | Deferred |  |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f => f.Contains("REG") && f.Contains("Reregistration") && f.Contains("empty reason"));
    }

    [Fact]
    public void Check4_CoveredAreaCitingForeignAxisId_FailsNamingTheForeignAxis_ViaMode3()
    {
        const string markdown = """
            ### DECL — Declaration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some area | Covered | IVC-DECL-001 |

            ### REL — Relations

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REL-001 | Active | Behaviour | Some other behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some other area | Covered | IVC-DECL-001 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().Contain(f =>
            f.Contains("REL") && f.Contains("IVC-DECL-001") && f.Contains("belongs to axis 'DECL'"));
        failures.Should().NotContain(f => f.Contains("does not exist in axis 'REL'"));
    }

    [Fact]
    public void Check4_AxisWithActiveRequirementAndNoCoverageTable_FailsNamingTheAxis_ViaMode1()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("REG") && f.Contains("no #### Coverage table"));
    }

    [Fact]
    public void Check4_CoverageRowWithStatusNeitherCoveredNorDeferred_FailsNamingAxisAndArea_ViaMode2()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some area | InProgress | IVC-REG-002 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("REG") && f.Contains("Some area") && f.Contains("neither Covered nor Deferred"));
    }

    [Fact]
    public void Check4_CoveredAreaCitingNonexistentSameAxisId_FailsNamingTheAxisAndId_ViaMode3Nonexistent()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some area | Covered | IVC-REG-002 |
            | Another area | Covered | IVC-REG-999 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("REG") && f.Contains("IVC-REG-999") && f.Contains("does not exist in axis 'REG'"));
    }

    [Fact]
    public void Check4_ActiveRequirementClaimedByNoArea_FailsNamingTheId_ViaMode5()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |
            | IVC-REG-003 | Active | Behaviour | Some other behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Some area | Covered | IVC-REG-002 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("IVC-REG-003") && f.Contains("REG") && f.Contains("claimed by no Covered area"));
    }

    [Fact]
    public void Check4_MalformedCoverageRow_FailsNamingTheOffendingLine_ViaMode6()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Only two columns | Covered |
            | Some area | Covered | IVC-REG-002 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("malformed coverage row") && f.Contains("Only two columns"));
    }
}
