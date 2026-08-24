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
/// 2. Every const's C# identifier must appear at least once, as a WHOLE identifier and outside
///    a whole-line comment, under <c>Iverson.Server/Iverson.ClientConformance/</c>, excluding
///    <c>Requirements.cs</c> itself, build output and the test project — i.e. it must be cited by
///    an assertion the orchestrator actually constructs, not merely declared. All FOUR moving
///    parts (the strip, the search root and file selection, the SEARCH PATTERN, and the identifier
///    feed) are graded: see <see cref="UncitedIdentifiers"/>, <see cref="IsGradableSourceFile"/>,
///    <see cref="Check2Inputs"/> and
///    <see cref="Check2_FileSelection_ExcludesRequirementsItselfTheTestProjectAndBuildOutput"/>,
///    which is where BOTH the search root and the glob are actually asserted. This comment said
///    THREE for one round while the glob went ungraded, which is how the ninth hole was born
///    (Ruling 44) — and then said FOUR for a round while this cref list still named only three, so
///    a reader following the links never reached the assertion that closed it.
/// 3. Every ID (Active or Retired) parsed from the standard must match
///    <c>IVC-[A-Z]+-\d{3}</c> with an axis drawn from the standard's known nine-axis set, and no
///    `|`-leading line inside a requirement table may be left unparsed (see
///    <see cref="RequirementTableParser"/> — an unparsed row must never silently drop the rows
///    that follow it from checks 1 and 3).
/// 4. Every authored axis's `#### Coverage` ledger must bind its claimed areas to that axis's
///    `Active` requirements bidirectionally: every Active requirement must be claimed by exactly
///    one Covered area — NOT merely at least one; two areas claiming the same ID is Mode 7 —
///    every Covered/Deferred row must be well-formed and cite only existing, Active, same-axis
///    IDs, an axis with at least one Active requirement must have a `#### Coverage` table at
///    all, and a `#### Coverage` table must sit under a known axis heading (Mode 8) rather than
///    float unattributed (see <see cref="CoverageTableParser"/> and
///    <c>ComputeCoverageFailures</c> below for the eight failure modes enforced).
///
/// <para><b>What Check4 does and does not guard.</b> Check4 enforces exactly-one CLAIMANT AREA IN
/// THE LEDGER; the code side rests on per-site named tests. An assertion that CITES a requirement
/// whose Statement it does not grade satisfies every check here — Check1 sees the const, Check2
/// sees the identifier in source, Check3 sees a well-formed ID and Check4 sees one ledger row —
/// so re-pointing a citation from one const to a semantically unrelated one passes this gate
/// entirely. What catches that is a hand-written per-site test naming the requirement the site
/// must cite (e.g.
/// <c>IdentityScenarioTests.JudgeTenantDerivation_TheGrpcControl_CitesIdn004AndNotIdn003</c>).
/// Do not read Check4 as protection against a mis-aimed citation in code.</para>
///
/// <para><b>Every rule in this file grades itself.</b> The gate's own rules added in August
/// 2026 shipped UNFALSIFIABLE, four times running and each time in a new place: Mode 7's
/// exactly-one, Check2's comment strip, then the whole-identifier match (graded on a PREFIX pair
/// only, so the left-hand bound and the underscore were still revertible with the suite green),
/// then Check2's INPUT SELECTION — which files and which identifiers it is fed, the widest of
/// them, since one deleted line made the check assert nothing at all. Each could be reverted with
/// the whole suite green, observable only through the live standard, if at all.
/// STANDING RULE: any change to this gate lands with a test that fails if the change is
/// reverted — and "the change" means EVERY clause of it, including the input wiring and every
/// bound of a boundary rule, not the one clause a single fixture happens to exercise.</para>
/// </summary>
public class RequirementsCoverageGateTests
{
    /// <summary>
    /// The shape half of Check3's ID rule, and an INPUT to it exactly as <see cref="KnownAxes"/> is
    /// the axis half — the class doc above states the two as ONE rule. Consumed by
    /// <see cref="Check3_EveryDeclaredId_MatchesShapeWithKnownAxis"/> and by <c>AxisOf</c> inside
    /// <c>ComputeCoverageFailures</c>, where a null match silently drops the requirement from every
    /// Check4 mode.
    ///
    /// <para>THE TENTH HOLE (Ruling 50). Round 4 closed the axis half and left this one: nothing
    /// named this field in any test, and <c>\d{3}</c> -&gt; <c>\d+</c> passed 442/442, admitting a
    /// malformed <c>IVC-SCH-3</c> end to end. Graded by
    /// <see cref="IdShapePattern_MatchesEveryDeclaredIdInTheStandard_AndRejectsEachShapeItBounds"/>.
    /// Note WHY that fixture needs a negative half: a live-standard sweep asserting every declared
    /// ID MATCHES is a positive assertion over conforming data, and RELAXING a pattern can never
    /// falsify one — every live ID is three-digit, so <c>\d+</c> survives it. Only an assertion
    /// that a malformed ID is REJECTED can die. Residual, unchanged: RequirementTableParser's own
    /// <c>IdCellPattern</c> still bounds a declared ID to <c>IVC-[A-Za-z]+-\d+</c> and KnownAxes
    /// still rejects a lowercase axis, so what this field uniquely decides is the DIGIT COUNT.</para>
    /// </summary>
    private static readonly Regex IdShapePattern = new(@"^IVC-([A-Z]+)-\d{3}$", RegexOptions.Compiled);

    /// <summary>
    /// The axes the standard declares. Graded against the document's own
    /// <c>### &lt;AXIS&gt; — &lt;Name&gt;</c> headings by
    /// <see cref="KnownAxes_ExactlyMatchTheAxisHeadingsInTheStandard"/> — this is an INPUT to
    /// Check3 and to Mode 8's attribution, not a constant of nature (Ruling 44, Minor 1).
    /// </summary>
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

    /// <summary>
    /// Whether one candidate path is a file Check2 may read a citation out of. Extracted from
    /// <see cref="ConformanceSourceFiles"/> for exactly the reason <see cref="UncitedIdentifiers"/>
    /// takes RAW sources: the selection rule is part of what Check2 ASSERTS, so it has to sit
    /// inside a unit a fixture can drive.
    ///
    /// <para><b>THE SEVENTH GATE HOLE (Ruling 39).</b> It did not, and Check2's live INPUT
    /// SELECTION was graded by nothing. Deleting the <c>Requirements.cs</c> clause passed the whole
    /// suite at 439/439: every const is then trivially "cited" by its own declaration line, which
    /// is real code and survives the strip. ONE deleted line made an entire check vacuously green
    /// — proved end to end, not by argument: a clean de-citation of IVC-IDN-004 dies by name on
    /// Check2, and that same de-citation PLUS the deleted clause passed 439/439.</para>
    ///
    /// <para>The test-project clause is DEFENSIVE and, as the tree stands, unreachable: the
    /// enumeration roots at <c>Iverson.ClientConformance/</c> and never descends into its sibling
    /// <c>Iverson.ClientConformance.Tests/</c>, so no real path can exercise it. That is precisely
    /// why it is graded by a FIXTURE path rather than by an outcome over the live tree — a rule no
    /// live input reaches is a rule no live-input assertion can falsify. It stays because Check2's
    /// subject is a citation the ORCHESTRATOR constructs; a const cited only by a test assertion is
    /// not that, and a future test project nested under the conformance directory would otherwise
    /// silently start satisfying the gate.</para>
    /// </summary>
    internal static bool IsGradableSourceFile(string path)
    {
        var root = ConformanceSourceDir();
        var testProjectDir = Path.Combine(RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance.Tests");

        return !path.StartsWith(Path.Combine(root, "bin"), StringComparison.Ordinal)
               && !path.StartsWith(Path.Combine(root, "obj"), StringComparison.Ordinal)
               && !path.StartsWith(testProjectDir, StringComparison.Ordinal)
               && Path.GetFileName(path) != "Requirements.cs";
    }

    private static IEnumerable<string> ConformanceSourceFiles() =>
        Directory.EnumerateFiles(ConformanceSourceDir(), "*.cs", SearchOption.AllDirectories)
            .Where(IsGradableSourceFile);

    /// <summary>
    /// Check2's LIVE INPUTS, both of them, in one expression that Check2 and its fixtures share:
    /// which files are read, and which identifiers are looked for. Ruling 39's second and third
    /// elements. Narrowing either — an axis filter, a <c>Take</c>, a hand-written list that goes
    /// stale on the next authored requirement, a widened search root — shrinks what Check2 grades
    /// without touching a single assertion in it.
    ///
    /// <para>Returned as ONE tuple deliberately: the fixtures below assert against the very value
    /// Check2 consumes, so a narrowing applied HERE is caught there.</para>
    ///
    /// <para><b>The residual this left open, now CLOSED.</b> Asserting against this method's
    /// RETURN VALUE can never see a narrowing applied AFTER it returns — leave
    /// <c>var (sourceFiles, identifiers0) = Check2Inputs();</c> fully intact, add
    /// <c>var identifiers = identifiers0.Take(1);</c>, and Check2 graded one const out of 43 while
    /// this method was still called and both fixtures still passed (mutant E2 / N9-C; Ruling 42
    /// first recorded the reach as "abandoning this method and inlining a narrowed feed", which
    /// Ruling 45 corrected to a token). The remedy Ruling 42 named — have the check publish what
    /// it actually consumed — is now implemented: <see cref="UncitedIdentifiers"/> records its
    /// materialized inputs into <see cref="LastGraded"/>, and
    /// <see cref="Check2_GradedTheFullInputSet_NotANarrowedSubset"/> drives Check2 and asserts on
    /// that record. E2 now fails. Do not "simplify" that observation into an assertion over this
    /// method's return value — that is precisely the version that could not see the defect.</para>
    /// </summary>
    internal static (IReadOnlyList<string> Files, IReadOnlyList<string> Identifiers) Check2Inputs() =>
        (ConformanceSourceFiles().ToList(), ReflectRegistryConstsByIdentifier().Keys.ToList());

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

    /// <summary>
    /// Drops whole-line comments — <c>///</c> XML docs and <c>//</c> lines alike — before Check2
    /// looks for a const's identifier.
    ///
    /// <para>Without this, a <c>&lt;see cref="Requirements.Xxx"/&gt;</c> in a doc comment COUNTS AS
    /// A CITATION and Check2 passes for a const no assertion constructs. That is not hypothetical:
    /// de-citing <c>IVC-IDN-004</c>'s assertion (replacing the const with the string literal it
    /// evaluates to) survived the gate at 13/13 solely because the method's own doc comment named
    /// the const. The cref is worth keeping — it is the doc link a reader follows — so the check
    /// gets stricter instead.</para>
    ///
    /// <para>Only lines whose trimmed form STARTS with a comment marker are dropped, never a
    /// trailing <c>//</c>. That is deliberate: a <c>//</c> appearing mid-line may be inside a string
    /// literal (a URL, say), and truncating there could hide a real citation and report a false
    /// gap. Under-stripping can only ever make this check more permissive, which is the safe
    /// direction for a heuristic; over-stripping could red the gate for a citation that exists.</para>
    ///
    /// <para><b>The residual, MEASURED rather than estimated.</b> Ruling 33's permissive trade-off
    /// stands; what follows is what it costs, each item confirmed by a surviving mutant or by
    /// reading this method. Still counted as a citation:
    /// <list type="a">
    /// <item><description>Any TRAILING <c>//</c> comment — de-citing an assertion to a string
    /// literal and appending <c>// cites Requirements.Xxx</c> to the SAME line passes. This is
    /// exactly as wide as the doc-comment hole above, one character to the left, and closing it is
    /// the thing the paragraph above deliberately refuses to do.</description></item>
    /// <item><description>A <c>/* */</c> block whose interior lines start with anything other than
    /// <c>*</c> — only <c>//</c> and <c>*</c> openers are recognised.</description></item>
    /// <item><description>The identifier inside a STRING LITERAL, which is live code to this check
    /// and prose to a reader.</description></item>
    /// <item><description><c>nameof(Requirements.Xxx)</c>, which names the const without
    /// constructing an assertion from it.</description></item>
    /// <item><description>Live code that never EXECUTES — the deepest limitation, recorded in
    /// Ruling 32. A citation can sit in a method nothing calls, or in a call site deleted from the
    /// scenario that still leaves the const cited inside <c>Verifier.cs</c>. No text search can see
    /// that; the per-scenario reaches-the-cell tests are the only instrument that
    /// can.</description></item>
    /// </list>
    /// Closing (a)–(d) mechanically needs a Roslyn syntax walk, which is larger than this gate
    /// should carry for the residual risk.</para>
    /// </summary>
    private static string StripCommentLines(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal)
                       && !trimmed.StartsWith("*", StringComparison.Ordinal);
            }));

    /// <summary>
    /// The computation behind Check2, factored out so the comment strip can be exercised against
    /// FIXTURE sources as well as against the live tree. Without this seam the strip is
    /// unfalsifiable from the suite: removing <see cref="StripCommentLines"/> from the pipeline
    /// leaves every real const cited by real code, so the gate stays green and nothing names the
    /// regression (the reviewer's mutant R6, which survived 426/426).
    ///
    /// <paramref name="rawSources"/> are RAW file contents — stripping happens here, inside the
    /// unit under test, deliberately: a helper that took already-stripped text would move the very
    /// wiring this exists to grade back out of reach.
    /// </summary>
    /// <summary>
    /// What the LAST call to <see cref="UncitedIdentifiers"/> actually received, recorded by that
    /// method itself. Ruling 42's remedy for the E2 residual, in the form it named: let a test
    /// observe WHAT THE LIVE CODE ACTUALLY CONSUMED.
    ///
    /// <para>Written at the consumption point, PAST any narrowing. A <c>.Take(1)</c> or a stray
    /// <c>.Where(...)</c> inserted between <see cref="Check2Inputs"/> and the assertion — the
    /// one-token mutant E2/N9-C, which used to survive the whole suite — changes what arrives
    /// here, so <see cref="Check2_GradedTheFullInputSet_NotANarrowedSubset"/> fails. Asserting
    /// against <see cref="Check2Inputs"/>'s RETURN VALUE could never catch that: the narrowing
    /// happens after it returns.</para>
    /// </summary>
    internal static (int SourceCount, IReadOnlyList<string> Identifiers)? LastGraded;

    internal static List<string> UncitedIdentifiers(
        IEnumerable<string> rawSources, IEnumerable<string> identifiers)
    {
        var sourceCode = rawSources.Select(StripCommentLines).ToList();
        var graded = identifiers.ToList();

        // Recorded from the materialized lists, not from the parameters: an IEnumerable narrowed
        // by a lazy operator would otherwise be recorded un-narrowed and the observation would
        // certify the wrong thing.
        LastGraded = (sourceCode.Count, graded);

        return graded
            .Where(identifier => !sourceCode.Any(text => ContainsIdentifier(text, identifier)))
            .ToList();
    }

    /// <summary>
    /// Whole-identifier match, not a bare <c>Contains</c>. A substring search reports
    /// <c>RegForeignKeyNaming</c> as cited by a file that only ever names
    /// <c>RegForeignKeyNamingEnforced</c> — so a const whose identifier is a PREFIX (or any
    /// substring) of another const's would pass Check2 having been cited nowhere, and the gate
    /// would be green over an entirely ungraded requirement. No such pair exists in
    /// <c>Requirements.cs</c> today, which is exactly why the hazard is worth closing now: it is
    /// created by an innocuous future rename, not by anything visible at the point of the mistake,
    /// and the gate would go on passing.
    ///
    /// <para>Bounded on both sides by "not a C# identifier character" — letters, digits, and
    /// underscore — so <c>Requirements.Xxx</c>, <c>(Xxx)</c> and <c>Xxx,</c> all still match,
    /// while <c>XxxSomething</c> and <c>PrefixXxx</c> do not. This can only ever make the check
    /// STRICTER, never more permissive, so it cannot introduce the false-green direction it
    /// exists to remove.</para>
    /// </summary>
    private static bool ContainsIdentifier(string text, string identifier)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(identifier, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var beforeOk = at == 0 || !IsIdentifierChar(text[at - 1]);
            var end = at + identifier.Length;
            var afterOk = end >= text.Length || !IsIdentifierChar(text[end]);

            if (beforeOk && afterOk)
            {
                return true;
            }

            from = at + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    [Fact]
    public void Check2_EveryRegistryConst_IsCitedByAssertionCodeOutsideRequirementsAndTests()
    {
        var (sourceFiles, identifiers) = Check2Inputs();

        var uncited = UncitedIdentifiers(sourceFiles.Select(File.ReadAllText), identifiers);

        uncited.Should().BeEmpty(
            "every const in Requirements.cs must be cited by an assertion the orchestrator constructs " +
            $"under Iverson.ClientConformance/ (excluding Requirements.cs and the test project), but these are uncited: {string.Join(", ", uncited)}");
    }

    /// <summary>
    /// CLOSES THE E2 RESIDUAL (Rulings 42 and 45). Check2 above asserts that nothing is uncited;
    /// it says nothing about HOW MUCH it looked at. A one-token narrowing at its consumption point
    /// — <c>var identifiers = identifiers0.Take(1);</c>, or a debugging <c>.Where(i =&gt; ...)</c>,
    /// or an <c>.Except(knownStale)</c> added to unblock a rename — left <see cref="Check2Inputs"/>
    /// fully intact, kept both of its fixtures passing, and graded ONE const out of 43. Ruling 42
    /// first recorded that as costing a rewrite; Ruling 45 corrected it to a token.
    ///
    /// <para>This test drives Check2 itself and then reads what the GRADER received, so the
    /// observation sits past any narrowing. It calls the check rather than depending on it having
    /// run: xunit orders tests within a class arbitrarily, and reading a static left by a test
    /// that may not have run yet is an unfalsifiable pass waiting to happen.</para>
    /// </summary>
    [Fact]
    public void Check2_GradedTheFullInputSet_NotANarrowedSubset()
    {
        LastGraded = null;

        Check2_EveryRegistryConst_IsCitedByAssertionCodeOutsideRequirementsAndTests();

        var expected = Check2Inputs();
        LastGraded.Should().NotBeNull("Check2 must grade through UncitedIdentifiers, which is what records this");

        var (sourceCount, identifiers) = LastGraded!.Value;
        identifiers.Should().BeEquivalentTo(expected.Identifiers,
            "Check2 must grade EVERY const in Requirements.cs — a narrowing applied between " +
            "Check2Inputs() and the assertion shrinks what the gate covers without touching a " +
            "single assertion in it");
        sourceCount.Should().Be(expected.Files.Count,
            "Check2 must read EVERY gradable source file, for the same reason");
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

    /// <summary>
    /// The duplicate computation behind <see cref="Check3_DeclaredIds_AreUniqueAcrossTheDocument"/>,
    /// factored out for the same reason <c>ComputeCoverageFailures</c> is: so its THRESHOLD can be
    /// driven by fixture markdown. Ruling 51(a) — none of Check3's three facts used fixture
    /// markdown at all, and <c>g.Count() &gt; 1</c> -&gt; <c>&gt; 2</c> passed 442/442, so a
    /// document declaring one ID exactly twice was invisible. Two rows for one ID means one
    /// requirement's Statement is authored twice and one const's evidence stands for both;
    /// duplicating a row today does redden this, but only because the LIVE document happens to be
    /// the only input, which is exactly the shape of every hole this gate has grown.
    /// </summary>
    internal static List<string> DuplicateDeclaredIds(string markdown) =>
        ParseDeclaredRequirements(markdown)
            .GroupBy(r => r.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

    [Fact]
    public void Check3_DeclaredIds_AreUniqueAcrossTheDocument()
    {
        var duplicates = DuplicateDeclaredIds(File.ReadAllText(StandardPath()));

        duplicates.Should().BeEmpty($"every requirement ID must be unique across the document, but these repeat: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Ruling 51(a): the threshold itself. An ID declared EXACTLY TWICE — the cheapest and by far
    /// the likeliest duplication — must be reported, which is what <c>&gt; 2</c> stops being true
    /// of (mutant C2). The unique-document half is the positive control: without it the fixture
    /// above would also pass against a rule that reports every ID it ever sees.
    /// </summary>
    [Fact]
    public void Check3_AnIdDeclaredExactlyTwice_IsReportedDuplicate_AndAUniqueDocumentIsNot()
    {
        const string twice = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |
            | IVC-REG-002 | Retired | Behaviour | The very same ID, declared a second time. |
            """;

        DuplicateDeclaredIds(twice).Should().ContainSingle(
            "an ID declared exactly twice is a duplicate — the threshold is `> 1`, and `> 2` "
            + "(mutant C2) reports nothing here while still reddening on a THREE-row document")
            .Which.Should().Be("IVC-REG-002");

        const string once = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |
            | IVC-REG-003 | Active | Behaviour | Some other behaviour. |
            """;

        DuplicateDeclaredIds(once).Should().BeEmpty(
            "the positive control: distinct IDs must not be reported, or the assertion above is "
            + "satisfied by a rule that flags everything");
    }

    /// <summary>
    /// Ruling 50, THE TENTH HOLE: <see cref="IdShapePattern"/> is an ungraded input to Check3 and
    /// to <c>AxisOf</c>. Symmetric with
    /// <see cref="KnownAxes_ExactlyMatchTheAxisHeadingsInTheStandard"/>, which closed the OTHER
    /// half of the same conjunction a round earlier.
    ///
    /// <para>The live sweep and its <c>NotBeEmpty</c> control catch the pattern being TIGHTENED or
    /// broken outright. They cannot catch it being RELAXED — every ID the standard declares is
    /// three-digit, so <c>\d{3}</c> -&gt; <c>\d+</c> passes a positive sweep unchanged (that is
    /// mutant C1R, which survived 442/442). The rejection half below is the half that dies, and it
    /// pins each bound the field decides on its own.</para>
    /// </summary>
    [Fact]
    public void IdShapePattern_MatchesEveryDeclaredIdInTheStandard_AndRejectsEachShapeItBounds()
    {
        var markdown = File.ReadAllText(StandardPath());

        // This ID-cell pattern is deliberately WIDER than RequirementTableParser.IdCellPattern
        // (`IVC-[A-Za-z]+-\d+`): `IVC-\S+` accepts anything non-blank after the prefix, and it
        // scans the WHOLE document rather than the parser's row set. Divergence between the two can
        // therefore only ever hand this fixture MORE id cells than the parser would accept — a
        // FALSE RED, never a false green — so the widening is safe in the one direction that
        // matters. Same argument, same shape, as the KnownAxes fixture's note about
        // CoverageTableParser.AxisHeadingPattern, which that fixture states and this one did not
        // (final-review Task 5 minor 4).
        var idCells = Regex.Matches(
                markdown,
                @"^\|\s*(IVC-\S+)\s*\|\s*(?:Active|Retired)\s*\|",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        idCells.Should().NotBeEmpty(
            "the parse itself must find requirement rows in the standard, or every assertion "
            + "below degrades into a sweep over an empty list");

        idCells.Should().OnlyContain(id => IdShapePattern.IsMatch(id),
            "IdShapePattern is Check3's shape rule read directly off the field, so it must accept "
            + "every ID the standard actually declares — tightening it (an axis-length bound, a "
            + "dropped anchor) reddens here rather than in a message about the document");

        IdShapePattern.IsMatch("IVC-SCH-3").Should().BeFalse(
            "EXACTLY three digits. Relaxing to `\\d+` (mutant C1R) admits IVC-SCH-3 through "
            + "Check3 and through AxisOf end to end, and no positive sweep over the live standard "
            + "can see it because every live ID already has three");
        IdShapePattern.IsMatch("IVC-SCH-0003").Should().BeFalse(
            "and the same bound on the other side — `\\d{3,}` is as wrong as `\\d+`");
        IdShapePattern.IsMatch("IVC-sch-003").Should().BeFalse(
            "the axis is upper-case: a lower-case axis would be attributed to no known axis, "
            + "which AxisOf turns into a SILENT DROP from every Check4 mode rather than a failure");
        IdShapePattern.IsMatch("IVC-SCH-003 and trailing prose").Should().BeFalse(
            "anchored at the end, or an ID cell carrying commentary parses as a requirement ID");
        IdShapePattern.IsMatch("XIVC-SCH-003").Should().BeFalse(
            "and anchored at the start");
    }

    /// <summary>
    /// Ruling 47 as corrected by Ruling 49: TWO CONSTS HOLDING THE SAME STRING VALUE.
    ///
    /// <para>Reach, verified rather than argued. REPOINTING an existing const at another's value
    /// does NOT survive: <c>ToHashSet()</c> collapses the duplicate on the REGISTRY side only,
    /// while the standard still declares the now-orphaned ID, so Check1's comparison does not
    /// balance — mutant DUP1 (IdnServerTenantColumnAbsentFromReadBack -&gt; "IVC-IDN-003") fails
    /// FOUR tests with Check1 among them. What survives is an ADDITIONAL const with no standard row
    /// of its own: mutant DUP2 (a new <c>IdnAliasOfIdn003 = "IVC-IDN-003"</c>, cited from
    /// Verifier.cs) passed 442/442. Check1 balances because the extra value is already in the set,
    /// Check2 works on IDENTIFIERS, which stay distinct, and Check3's uniqueness is over the
    /// DOCUMENT's declared IDs. Two assertions' evidence then silently merges under one
    /// requirement and ComputeUntouched reports it touched.</para>
    ///
    /// <para>This is falsifiable where the assertion fix round 4 DELETED was not: that one ran over
    /// <c>Dictionary.Keys</c>, unique by construction. This runs over the reflected VALUES, which
    /// nothing makes unique.</para>
    /// </summary>
    [Fact]
    public void RegistryConstValues_AreUniqueAcrossTheRegistry()
    {
        ReflectRegistryConsts().Should().OnlyHaveUniqueItems(
            "two consts holding one requirement ID merge two assertions' evidence under a single "
            + "requirement, and no other check in this gate can see it (Rulings 47 and 49)");
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
        // HashSet<string> of IDs could only ever enforce AT LEAST ONE. Scope that precisely: Check4
        // enforces exactly-one CLAIMANT AREA IN THE LEDGER; the CODE side rests on per-site named
        // tests. It stops a second markdown area being labelled `Covered | <some existing ID>`; it
        // does NOT stop an ASSERTION citing a const whose Statement it does not grade — re-pointing
        // a citation to a foreign-axis const passes all four checks (Ruling 35, mutant R2). Mode 7
        // is graded by Check4_ActiveRequirementClaimedByTwoAreas_..._ViaMode7 below.
        var claimedByAxis = new Dictionary<string, Dictionary<string, List<string>>>();

        foreach (var row in coverage.Rows)
        {
            if (row.Axis is null)
            {
                // Mode 8: a `#### Coverage` table under a heading whose leading token is not a
                // known axis (or before any axis heading at all). Design assumption A15 held that
                // no non-axis `###` heading could be read as an axis heading, and it still does —
                // but the CONVERSE was never checked, and it is the dangerous direction. A heading
                // such as `### Tenancy — notes` carries the ` — ` separator, so
                // CoverageTableParser matches it, finds `Tenancy` is not a known axis and sets the
                // current axis to null; every row of a coverage table beneath it was then SKIPPED
                // here. A ledger claiming `Covered | IVC-IDN-003` from such a table was invisible
                // to Mode 7, so the second claimant that Mode 7 exists to catch could be hidden by
                // putting it under the wrong heading. A coverage ledger that binds to no axis
                // binds nothing; it is a defect in the document, not a row to ignore.
                failures.Add(
                    $"coverage area '{row.Area}' sits in a #### Coverage table attributed to no axis — "
                    + "a coverage ledger must appear under a '### <AXIS> — <Name>' heading whose axis "
                    + "is one of the known axes, or its claims bind to nothing and Mode 7 never sees them");
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
                // RULING 55(b), recorded rather than deleted. `|| claimants.Count == 0` is
                // UNREACHABLE BY CONSTRUCTION: claimed[id] is created on the line before an
                // unconditional claimants.Add above, so no list in this dictionary is ever empty.
                // Dropping the disjunct survives the whole suite, and no fixture can grade it —
                // Ruling 43's precedent (grade unreachable defensive code through a fixture path)
                // does not apply, because that path was INPUT-driven and this is an internal
                // invariant. It is kept as a fail-closed backstop on that invariant: if a future
                // edit ever creates the list before deciding whether to Add, the requirement would
                // otherwise pass Mode 5 with zero claimants. Introduced by this plan at cc98eb9 and
                // missed by seven review rounds including round 4's exhaustive enumeration.
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

    /// <summary>
    /// Mode 3's EMPTY-EVIDENCE arm, found by re-running the fix-round-4 review's per-input
    /// enumeration against the branches this round touched rather than only its named items.
    /// Nothing constrained it either: replacing <c>ids.Length == 0</c> with <c>false</c> (mutant
    /// C5) passed 447/447 with Ruling 51(b)'s new fixtures already in place.
    ///
    /// <para>Reach, stated precisely rather than inflated. A <c>Covered</c> row with empty Evidence
    /// contributes no claimant, so if its axis's Active requirements are claimed by NO other area,
    /// Mode 5 reddens anyway and the only loss is a message that blames the requirement instead of
    /// the row. The false green is the case below: another area already claims the requirement, so
    /// Mode 5 is silent, and a row reading <c>Covered</c> with nothing behind it survives the gate
    /// — a ledger asserting coverage it does not have, which is the document-side form of exactly
    /// the failure this gate exists to prevent.</para>
    /// </summary>
    [Fact]
    public void Check4_CoveredAreaCitingNothing_FailsNamingTheArea_ViaMode3Empty()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REG-002 |
            | An area covered by nothing at all | Covered |  |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("REG") && f.Contains("An area covered by nothing at all")
            && f.Contains("cites no requirement ID"),
            "the ledger's other area already claims IVC-REG-002, so Mode 5 is silent and this row "
            + "is the only thing that can redden — with the branch disabled (mutant C5) the gate "
            + "passes over an area that declares itself Covered with no evidence whatsoever");
    }

    /// <summary>
    /// Ruling 51(b): Mode 3's RETIRED arm. Every other one of Check4's eight modes had a named
    /// fixture and this one did not, though the class doc claims it — disabling the arm passed
    /// 442/442 (mutant C4). A Retired requirement is kept for history and ID-uniqueness only and
    /// takes no const, so a ledger area citing one claims evidence that cannot exist; with the arm
    /// off the citation is accepted and the area reads as Covered by nothing.
    ///
    /// <para>Note the arm is invisible to the LIVE standard for the same reason it needed a
    /// fixture: no live Coverage row cites a Retired ID, and if one ever did the failure would be
    /// a document defect rather than evidence the arm works.</para>
    /// </summary>
    [Fact]
    public void Check4_CoveredAreaCitingARetiredId_FailsNamingTheId_ViaMode3Retired()
    {
        const string markdown = """
            ### REL — Relations

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REL-001 | Active | Behaviour | Some behaviour. |
            | IVC-REL-009 | Retired | Capability | A superseded capability, kept for history. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REL-001 |
            | A legacy area | Covered | IVC-REL-009 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("REL") && f.Contains("A legacy area") && f.Contains("IVC-REL-009")
            && f.Contains("is Retired"));
    }

    /// <summary>
    /// The control for the fixture above: the SAME ledger with the Retired row's citation swapped
    /// for the Active one produces no failure. Without it, the Retired assertion would also pass
    /// against a Mode 3 that rejected every Covered citation outright, and "cites a Retired ID" and
    /// "cites anything at all" would be indistinguishable.
    /// </summary>
    [Fact]
    public void Check4_CoveredAreaCitingOnlyActiveIds_Passes_TheMode3RetiredControl()
    {
        const string markdown = """
            ### REL — Relations

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REL-001 | Active | Behaviour | Some behaviour. |
            | IVC-REL-009 | Retired | Capability | A superseded capability, kept for history. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REL-001 |
            """;

        ComputeCoverageFailures(markdown).Should().BeEmpty(
            "a Retired requirement takes no const and is not subject to coverage, so simply "
            + "DECLARING one must not redden the ledger — only CITING one may");
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

    // ── the gate's own two August-2026 rules, each graded by a test that fails if it is reverted ──

    /// <summary>
    /// Mode 7 (exactly-one, not at-least-one) modelled on
    /// <see cref="Check4_ActiveRequirementClaimedByNoArea_FailsNamingTheId_ViaMode5"/>. Reverting
    /// <c>claimed</c> to a <c>HashSet&lt;string&gt;</c> of IDs — or weakening the
    /// <c>claimants.Count &gt; 1</c> branch — cannot express exactly-one, and this fixture is what
    /// goes red. Without it the rule was observable only by mutating the live standard.
    /// </summary>
    [Fact]
    public void Check4_ActiveRequirementClaimedByTwoAreas_FailsNamingTheIdAndBothAreas_ViaMode7()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REG-002 |
            | A second area riding along | Covered | IVC-REG-002 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("IVC-REG-002")
            && f.Contains("claimed by 2 Covered areas")
            && f.Contains("'The authored area'")
            && f.Contains("'A second area riding along'")
            && f.Contains("exactly one"));
    }

    /// <summary>
    /// The same ledger with only ONE claimant must produce NO failure — otherwise the test above
    /// would pass against a gate that simply rejects every Covered row, and "exactly one" would be
    /// indistinguishable from "none".
    /// </summary>
    [Fact]
    public void Check4_ActiveRequirementClaimedByExactlyOneArea_Passes_TheMode7Control()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REG-002 |
            """;

        ComputeCoverageFailures(markdown).Should().BeEmpty();
    }

    /// <summary>
    /// Mode 8: a `#### Coverage` table under a heading that carries the ` — ` separator but whose
    /// leading token is not a known axis. Before this, CoverageTableParser attributed such a table
    /// to no axis and <c>ComputeCoverageFailures</c> skipped its rows outright — so the SECOND
    /// claimant Mode 7 exists to catch could be hidden simply by writing it under the wrong
    /// heading, and the gate stayed green.
    /// </summary>
    [Fact]
    public void Check4_CoverageTableUnderANonAxisHeading_FailsNamingTheArea_ViaMode8()
    {
        const string markdown = """
            ### REG — Registration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REG-002 | Active | Behaviour | Some behaviour. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | The authored area | Covered | IVC-REG-002 |

            ### Tenancy — a heading that is not an axis

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | A second claimant hiding under a non-axis heading | Covered | IVC-REG-002 |
            """;

        var failures = ComputeCoverageFailures(markdown);

        failures.Should().ContainSingle(f =>
            f.Contains("A second claimant hiding under a non-axis heading")
            && f.Contains("attributed to no axis"));
    }

    /// <summary>
    /// The direct unit test of <see cref="StripCommentLines"/> Ruling 36 requires: an identifier
    /// carried ONLY by a `///` doc line and nowhere else must not survive the strip. Reverting the
    /// strip to the identity function is what this fails on.
    /// </summary>
    [Fact]
    public void StripCommentLines_DropsDocAndSlashSlashLines_AndKeepsEveryCodeLine()
    {
        const string source = """
            /// <see cref="Requirements.OnlyNamedInADocComment"/>
            // Requirements.OnlyNamedInASlashSlashComment
            /*
             * Requirements.OnlyNamedInAStarredBlockComment
             */
                /// Requirements.OnlyNamedInAnIndentedDocComment
            var x = Requirements.NamedByRealCode;
            """;

        var stripped = StripCommentLines(source);

        stripped.Should().NotContain("OnlyNamedInADocComment");
        stripped.Should().NotContain("OnlyNamedInASlashSlashComment");
        stripped.Should().NotContain("OnlyNamedInAStarredBlockComment");
        stripped.Should().NotContain("OnlyNamedInAnIndentedDocComment");
        stripped.Should().Contain("Requirements.NamedByRealCode",
            "over-stripping would red the gate for a citation that really exists, which is the "
            + "failure direction the strip is explicitly built to avoid");
    }

    /// <summary>
    /// The strip WIRED INTO Check2's computation, not merely the strip in isolation: an identifier
    /// named only by a doc comment must come back UNCITED. Dropping
    /// <see cref="StripCommentLines"/> from <see cref="UncitedIdentifiers"/> — the reviewer's
    /// mutant R6, which survived the whole suite at 426/426 — is what this fails on.
    /// </summary>
    [Fact]
    public void Check2_IdentifierNamedOnlyByADocComment_IsReportedUncited()
    {
        const string source = """
            /// <summary>
            /// Discharged by <see cref="Requirements.CitedOnlyFromProse"/>.
            /// </summary>
            internal static Assertion Judge() => Assertion.From("something", true, "ok");
            """;

        UncitedIdentifiers([source], ["CitedOnlyFromProse"])
            .Should().ContainSingle().Which.Should().Be("CitedOnlyFromProse");
    }

    /// <summary>
    /// The FIFTH gate hole, found this round and closed with the rule that closes it: Check2 used
    /// a bare <c>Contains</c>, so an identifier that is a SUBSTRING of another const's identifier
    /// was reported cited by a file naming only the longer one. Reverting
    /// <see cref="ContainsIdentifier"/> to <c>text.Contains(identifier)</c> is what this fails on.
    /// </summary>
    [Fact]
    public void Check2_IdentifierThatIsOnlyASubstringOfALongerCitedIdentifier_IsReportedUncited()
    {
        const string source = """
            internal static Assertion Judge() =>
                Assertion.From("something", true, "ok", Requirements.RegForeignKeyNamingEnforced);
            """;

        UncitedIdentifiers([source], ["RegForeignKeyNaming"])
            .Should().ContainSingle().Which.Should().Be("RegForeignKeyNaming",
                "a const cited nowhere must not be reported cited merely because a LONGER const's "
                + "identifier happens to contain it — an innocuous future rename would otherwise "
                + "leave a requirement ungraded with the gate green");

        UncitedIdentifiers([source], ["RegForeignKeyNamingEnforced"]).Should().BeEmpty(
            "the longer identifier is genuinely cited and must still be reported so");

        // The pair above is a PREFIX pair, and it grades only the RIGHT-hand bound. The left-hand
        // bound and the underscore are SEPARATE rules and were each revertible with the whole suite
        // green (mutants B3 and B4), which is the fifth hole reappearing inside its own fix.
        const string citesTheShorterConst = """
            internal static Assertion Judge() =>
                Assertion.From("something", true, "ok", Requirements.RegForeignKeyNaming);
            """;

        UncitedIdentifiers([citesTheShorterConst], ["ForeignKeyNaming"])
            .Should().ContainSingle().Which.Should().Be("ForeignKeyNaming",
                "a SUFFIX pair is the mirror image of the prefix pair above, and it is graded by "
                + "the LEFT-hand bound alone: hard-coding `beforeOk` to true (mutant B3) reverts "
                + "every suffix pair to bare-substring matching while the test above still passes");

        const string citesAnUnderscoreSuffixedConst = """
            internal static Assertion Judge() =>
                Assertion.From("something", true, "ok", Requirements.RegFoo_Legacy);
            """;

        UncitedIdentifiers([citesAnUnderscoreSuffixedConst], ["RegFoo"])
            .Should().ContainSingle().Which.Should().Be("RegFoo",
                "underscore is a C# identifier character, so `RegFoo_Legacy` does not cite "
                + "`RegFoo`; dropping `|| c == '_'` from IsIdentifierChar (mutant B4) makes it "
                + "read as one, and neither bound test above notices");
    }

    /// <summary>
    /// The control for the test above: an identifier a real code line names must be reported
    /// CITED. Without this, a strip that returned the empty string would satisfy the uncited test
    /// and Check2 would report every const as a gap.
    /// </summary>
    [Fact]
    public void Check2_IdentifierNamedByRealCode_IsReportedCited()
    {
        const string source = """
            /// <summary>Nothing in this comment names it.</summary>
            internal static Assertion Judge() =>
                Assertion.From("something", true, "ok", Requirements.CitedByRealCode);
            """;

        UncitedIdentifiers([source], ["CitedByRealCode"]).Should().BeEmpty();
    }

    /// <summary>
    /// THE SEVENTH GATE HOLE (Ruling 39), first half: WHICH FILES Check2 reads. The
    /// <see cref="UncitedIdentifiers"/> refactor moved one of THREE live-wiring elements inside the
    /// graded unit — the comment strip — and left the other two ungraded. Deleting the
    /// <c>Requirements.cs</c> exclusion (mutant B1) passed 439/439 while making Check2 assert
    /// nothing at all, because every const is cited by its own declaration line.
    ///
    /// <para>Graded at two levels deliberately: <see cref="IsGradableSourceFile"/> by FIXTURE paths
    /// (the only way to reach the test-project clause, which no real path exercises — mutant B2),
    /// and <see cref="ConformanceSourceFiles"/> over the LIVE tree (so that dropping the
    /// <c>.Where</c> altogether, rather than a clause inside it, fails here too).</para>
    /// </summary>
    [Fact]
    public void Check2_FileSelection_ExcludesRequirementsItselfTheTestProjectAndBuildOutput()
    {
        var root = ConformanceSourceDir();
        var testProjectDir = Path.Combine(RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance.Tests");

        IsGradableSourceFile(Path.Combine(root, "Requirements.cs")).Should().BeFalse(
            "Requirements.cs DECLARES every const, so reading it back reports all 43 cited by "
            + "their own declaration lines and Check2 grades nothing whatsoever (mutant B1)");
        IsGradableSourceFile(Path.Combine(root, "Scenarios", "Requirements.cs")).Should().BeFalse(
            "the exclusion is by file NAME at any depth, not by one hard-coded path");
        IsGradableSourceFile(Path.Combine(testProjectDir, "RequirementsCoverageGateTests.cs"))
            .Should().BeFalse(
                "Check2's subject is a citation the ORCHESTRATOR constructs; a const named only by "
                + "a test assertion is not one, and would read as graded when it is not (mutant B2)");
        IsGradableSourceFile(Path.Combine(root, "bin", "Debug", "net10.0", "Copied.cs")).Should().BeFalse(
            "build output is a copy of sources, so counting it would let a DELETED citation go on "
            + "satisfying the gate out of a stale bin/");
        IsGradableSourceFile(Path.Combine(root, "obj", "Debug", "Generated.cs")).Should().BeFalse();

        IsGradableSourceFile(Path.Combine(root, "Scenarios", "IdentityScenario.cs")).Should().BeTrue(
            "a real orchestrator source must still be READ — without this control every assertion "
            + "above is satisfied by a selection that excludes everything and reports all 43 uncited");
        IsGradableSourceFile(Path.Combine(root, "Verifier.cs")).Should().BeTrue();

        var live = Check2Inputs().Files;

        live.Should().NotBeEmpty("the live enumeration must find the orchestrator's sources at all");
        live.Should().NotContain(f => Path.GetFileName(f) == "Requirements.cs",
            "the live enumeration must APPLY the rule above, not merely have it available");
        live.Should().Contain(f => Path.GetFileName(f) == "Verifier.cs",
            "and must still reach the file that carries most of the citations");

        // THE EIGHTH HOLE, found by mutating this round's own fix: the search ROOT is the third
        // live-wiring element, and nothing above pins it. Widening ConformanceSourceDir() to
        // RepositoryRoot() (mutant E1) passed 441/441 — every exclusion above still holds, and
        // Check2 silently degrades from "cited by an assertion the ORCHESTRATOR constructs" to
        // "this identifier appears SOMEWHERE IN THE REPOSITORY". The path segment is spelled out
        // as a LITERAL rather than taken from ConformanceSourceDir(), or the assertion would be
        // satisfied by whatever that method happened to return.
        var conformanceProject =
            Path.Combine("Iverson.Server", "Iverson.ClientConformance") + Path.DirectorySeparatorChar;

        live.Should().OnlyContain(f => f.Contains(conformanceProject, StringComparison.Ordinal),
            "Check2 grades the ORCHESTRATOR's citations, so its inputs must all live inside "
            + $"{conformanceProject} — widening the search root makes the check pass on an "
            + "identifier that appears anywhere in the tree at all (mutant E1)");
        live.Should().NotContain(f => f.Contains(
                Path.Combine("Iverson.Server", "Iverson.Api") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            "named concretely as well as by the rule above: server sources are the first thing a "
            + "widened root sweeps in, and they are not orchestrator assertions");

        // THE NINTH HOLE (Ruling 44): the SEARCH PATTERN is the fourth widening-capable input to
        // the same enumeration, and for one round nothing constrained it while this file's own
        // class doc claimed there were only three. Mutant N9-A ("*.cs" -> "*") survived 441/441
        // with every assertion above still holding. Check2's subject is a citation in CODE, so a
        // .md, a JSON fixture or a generated .g.txt under the conformance project naming a
        // requirement identifier must never count — a de-cited assertion would then read as cited
        // and Check2 would grade nothing for that const.
        live.Should().OnlyContain(f => f.EndsWith(".cs", StringComparison.Ordinal),
            "Check2 grades citations in C# SOURCE, so the search pattern is part of what it "
            + "asserts: relaxing the glob lets any non-code file under the conformance project "
            + "satisfy a citation (mutant N9-A)");
    }

    /// <summary>
    /// <see cref="KnownAxes"/> is an INPUT, not a constant of nature: Check3 rejects any ID whose
    /// axis is outside it, and <c>ComputeCoverageFailures</c> uses it both to attribute
    /// requirements to axes and (via <see cref="CoverageTableParser"/>) to decide which headings a
    /// <c>#### Coverage</c> table may sit under — Mode 8. Nothing tied it to the document for one
    /// round: mutant N9-B (adding <c>"TENANCY"</c>) survived 441/441, admitting IVC-TENANCY-001 to
    /// Check3 and admitting a <c>### TENANCY — ...</c> heading as a legitimate Mode 8 attribution
    /// point. Narrower in reach than the input holes above — hence a Minor and not a tenth hole —
    /// but the same named shape: an input to a check that the check's own test did not constrain.
    /// </summary>
    [Fact]
    public void KnownAxes_ExactlyMatchTheAxisHeadingsInTheStandard()
    {
        var markdown = File.ReadAllText(StandardPath());

        // This pattern is deliberately STRICTER than CoverageTableParser.AxisHeadingPattern
        // (`^###\s+(\S+)\s+—`): it requires single spaces and an upper-case-only leading token.
        // Divergence between the two can therefore only ever cost this fixture a heading the
        // parser would have accepted — a FALSE RED, never a false green — so the narrowing is safe
        // in the one direction that matters (Ruling 44 review, Minor 4).
        var headingAxes = Regex.Matches(markdown, @"^### ([A-Z]+) \u2014 ", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        headingAxes.Should().NotBeEmpty(
            "the parse itself must find something, or this fixture degrades into comparing an "
            + "empty list against a KnownAxes that has also been emptied");

        headingAxes.Should().BeEquivalentTo(KnownAxes,
            "KnownAxes is the gate's notion of which axes EXIST, and the standard's `### <AXIS> "
            + "— <Name>` headings are the document's — an axis in one and not the other means "
            + "either an authored axis whose IDs Check3 rejects, or a phantom axis that admits IDs "
            + "and Coverage tables the standard never declared (mutant N9-B)");
    }

    /// <summary>
    /// THE SEVENTH GATE HOLE (Ruling 39), second half: WHICH IDENTIFIERS Check2 is fed. A feed
    /// narrowed to a subset grades a subset, with every assertion in Check2 unchanged and the gate
    /// green over whatever fell out.
    /// </summary>
    [Fact]
    public void Check2_IdentifierFeed_IsEveryConstInTheRegistry()
    {
        var fed = Check2Inputs().Identifiers;

        fed.Should().HaveCount(ReflectRegistryConsts().Count,
            "Check2 must grade EVERY const in the registry — the count is taken from the values "
            + "side of the reflection so that a filter applied to the identifier side is visible");
        // No uniqueness assertion here. `fed` is Dictionary.Keys.ToList(), so it is unique BY
        // CONSTRUCTION and no mutation of Check2Inputs can falsify it — the same vacuity fix
        // round 2 removed from IdentityScenarioTests, shipped again in this fixture (Ruling 44's
        // Minor 2). An assertion nothing can break is not coverage.
        fed.Should().Contain("IdnServerTenantColumnAbsentFromReadBack",
            "the const this plan authored must be inside the feed, not merely inside the registry");
        fed.Should().Contain("RegForeignKeyNamingEnforced");
    }
}
