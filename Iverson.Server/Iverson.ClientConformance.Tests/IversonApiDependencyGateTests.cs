using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Enforces the rule stated in <c>Iverson.ClientConformance.Tests.csproj</c>'s own comment on its
/// <c>Iverson.Api</c> reference: the conformance harness (<c>Iverson.ClientConformance</c>) must
/// never reference <c>Iverson.Api</c>, and within the test project exactly one file may — <see
/// cref="SchemaProbeTests"/>, which needs the server's real <c>SchemaDescriptor</c> types to build
/// fixtures no hand-written JSON literal could stand in for (see the comment on that reference in
/// the csproj). Every other file sharing the server's own constants would defeat the harness's
/// purpose: a probe that shares the server's value cannot catch the server changing it.
///
/// <para>Two independent assertions:</para>
/// <list type="number">
/// <item><description>The set of test-project <c>.cs</c> files with a real <c>Iverson.Api</c> code
/// dependency equals the allowlist — in BOTH directions, so a stale allowlist entry (a file that
/// no longer depends on <c>Iverson.Api</c> but is still listed) fails just as loudly as an
/// unlisted new dependency.</description></item>
/// <item><description><c>Iverson.ClientConformance.csproj</c> — the harness itself, not the test
/// project — declares no <c>ProjectReference</c> to <c>Iverson.Api</c>.</description></item>
/// </list>
///
/// <para>"A real code dependency" means a <c>using Iverson.Api</c> (or <c>using
/// Iverson.Api.&lt;anything&gt;</c>) directive, or a fully-qualified <c>Iverson.Api.</c> type
/// reference, surviving after (a) whole-line comments are dropped via <see
/// cref="RequirementsCoverageGateTests.StripCommentLines"/> and (b) string literal contents are
/// blanked via <see cref="BlankStringLiterals"/>. Both are necessary: without (a), the prose in
/// <c>TenantRejectedScenarioTests.cs</c> explaining why it does NOT reference <c>Iverson.Api</c>
/// would itself look like a dependency; without (b), <c>RequirementsCoverageGateTests.cs</c>'s own
/// <c>Path.Combine("Iverson.Server", "Iverson.Api")</c> string literal would too.</para>
/// </summary>
public class IversonApiDependencyGateTests
{
    /// <summary>
    /// The one file sanctioned to depend on <c>Iverson.Api</c>. See the class doc and the
    /// comment in <c>Iverson.ClientConformance.Tests.csproj</c> on its <c>Iverson.Api</c>
    /// reference for why.
    /// </summary>
    private static readonly HashSet<string> AllowlistedFiles = new(StringComparer.Ordinal)
    {
        "SchemaProbeTests.cs",
    };

    /// <summary>
    /// Matches <c>Iverson.Api</c> as a whole dotted segment — a <c>using</c> directive naming it
    /// (<c>using Iverson.Api;</c> or <c>using Iverson.Api.Schema;</c>) or a fully-qualified type
    /// reference (<c>Iverson.Api.Schema.SchemaDescriptor</c>). The trailing <c>\b</c> rejects a
    /// hypothetical unrelated identifier like <c>Iverson.ApiClient</c>, which shares the prefix
    /// but is not this namespace.
    /// </summary>
    private static readonly Regex IversonApiCodeReference =
        new(@"\bIverson\.Api\b", RegexOptions.Compiled);

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

    private static string TestProjectDir() =>
        Path.Combine(RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance.Tests");

    private static string HarnessCsprojPath() =>
        Path.Combine(
            RepositoryRoot(), "Iverson.Server", "Iverson.ClientConformance",
            "Iverson.ClientConformance.csproj");

    /// <summary>
    /// Whether a real <c>Iverson.Api</c> code dependency survives in <paramref name="rawSource"/>
    /// once whole-line comments are dropped and string literal contents are blanked. See the
    /// class doc for why both steps are required.
    /// </summary>
    private static bool ReferencesIversonApi(string rawSource)
    {
        var withoutCommentLines = RequirementsCoverageGateTests.StripCommentLines(rawSource);
        var withoutStringLiterals = BlankStringLiterals(withoutCommentLines);
        return IversonApiCodeReference.IsMatch(withoutStringLiterals);
    }

    /// <summary>
    /// Blanks the CONTENTS of C# string literals — ordinary <c>"..."</c> literals (where
    /// <c>\"</c> escapes a quote without ending the literal) and verbatim <c>@"..."</c> literals
    /// (where <c>""</c> escapes a quote without ending the literal) — replacing each character
    /// inside with a space so an identifier the literal spells out as DATA, such as
    /// <c>Path.Combine("Iverson.Server", "Iverson.Api")</c>'s second argument, cannot be mistaken
    /// for a reference in CODE. The opening and closing quote characters themselves, and any
    /// leading <c>@</c>, are blanked too; every other character (including newlines, which a
    /// verbatim literal may legitimately contain) is preserved so line numbers in the result line
    /// up with <paramref name="source"/>.
    ///
    /// <para>An interpolated string (<c>$"..."</c> or <c>$@"..."</c>) is treated as an ordinary
    /// or verbatim literal respectively — its <c>{expression}</c> holes are blanked along with
    /// everything else, which never produces a false negative here: this test only needs to
    /// avoid mistaking literal DATA for a code reference, not to preserve code embedded inside an
    /// interpolation hole.</para>
    ///
    /// <para>This is deliberately not a general C# lexer — it does not recognise character
    /// literals, raw string literals (<c>"""..."""</c>), or comments (that is <see
    /// cref="RequirementsCoverageGateTests.StripCommentLines"/>'s job, applied before this runs).
    /// It exists to close exactly the false positive this gate would otherwise hit on its own
    /// codebase: a string literal that spells out <c>Iverson.Api</c> as text.</para>
    /// </summary>
    internal static string BlankStringLiterals(string source)
    {
        // Quote characters below are spelled '"' rather than as a bare double-quote char
        // literal deliberately: this file is itself graded by this gate, and a char literal like
        // that would itself contain an un-paired double-quote character that this scanner — which
        // knows nothing of char-literal syntax — would misread as opening a new ordinary string
        // literal, desynchronising every quote that follows it in the file.
        var result = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '\u0022')
            {
                // Verbatim literal: @"...", where "" is an escaped quote that does not close it.
                result.Append(' ', 2);
                i += 2;

                while (i < source.Length)
                {
                    if (source[i] == '\u0022')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '\u0022')
                        {
                            result.Append(' ', 2);
                            i += 2;
                            continue;
                        }

                        result.Append(' ');
                        i++;
                        break;
                    }

                    result.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '\u0022')
            {
                // Ordinary (or interpolated) literal: "...", where \x is an escaped character
                // pair that does not close it, whatever x is.
                result.Append(' ');
                i++;

                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(' ', 2);
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\u0022')
                    {
                        result.Append(' ');
                        i++;
                        break;
                    }

                    result.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    private static bool IsBuildOutputPath(string filePath, string testProjectDir) =>
        filePath.StartsWith(Path.Combine(testProjectDir, "bin"), StringComparison.Ordinal)
        || filePath.StartsWith(Path.Combine(testProjectDir, "obj"), StringComparison.Ordinal);

    /// <summary>
    /// Assertion 1: the set of test-project files with a real <c>Iverson.Api</c> code dependency
    /// equals <see cref="AllowlistedFiles"/>, in both directions. Checked as two separate
    /// collections rather than one set-equality assertion so each direction's failure carries its
    /// own reason: an unlisted file explains why the dependency is a problem, and a stale
    /// allowlist entry explains why leaving it would be dangerous even though nothing in it
    /// currently fires.
    /// </summary>
    [Fact]
    public void OnlyAllowlistedFiles_DependOnIversonApi()
    {
        var testProjectDir = TestProjectDir();

        var dependentFiles = Directory
            .EnumerateFiles(testProjectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path, testProjectDir))
            .Where(path => ReferencesIversonApi(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = dependentFiles.Except(AllowlistedFiles)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        unlisted.Should().BeEmpty(
            "each of these files depends on Iverson.Api. The conformance harness must not share "
            + "the server's own constants — a probe that does cannot catch the server changing "
            + "them. Only SchemaProbeTests.cs is sanctioned.");

        var stale = AllowlistedFiles.Except(dependentFiles)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty(
            "each of these files is allowlisted to depend on Iverson.Api but no longer does; a "
            + "stale entry could hide a future real dependency introduced somewhere else, since "
            + "the allowlist is meant to name exactly the files that need the exception, not a "
            + "superset of them");
    }

    /// <summary>
    /// Assertion 2: the harness project itself — as opposed to its test project, which is
    /// entitled to the one sanctioned exception above — declares no <c>ProjectReference</c> to
    /// <c>Iverson.Api</c> at all.
    /// </summary>
    [Fact]
    public void ClientConformanceProject_DeclaresNoIversonApiProjectReference()
    {
        var csprojPath = HarnessCsprojPath();
        File.Exists(csprojPath).Should().BeTrue(
            $"the harness project file must exist at {csprojPath} for this gate to check anything");

        var csprojContent = File.ReadAllText(csprojPath);

        var offendingReferences = Regex
            .Matches(csprojContent, @"<ProjectReference\s+Include\s*=\s*""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(include => IversonApiCodeReference.IsMatch(include.Replace('\\', '/')))
            .ToList();

        offendingReferences.Should().BeEmpty(
            "Iverson.ClientConformance.csproj must not reference Iverson.Api: a probe that shares "
            + "the server's own constants cannot catch the server changing them. Only the TEST "
            + "project (via SchemaProbeTests) is allowed that exception, and only because it needs "
            + "the server's real SchemaDescriptor types to build fixtures.");
    }

    /// <summary>
    /// The identifier this whole gate exists to catch survives detection when it is CODE, and is
    /// erased when it is only DATA spelled out inside an ordinary string literal — the false
    /// positive this test's own use of a fixture string would otherwise be.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_ErasesAnIdentifierSpelledOutInsideAnOrdinaryLiteral()
    {
        const string source = "var directive = \"using Iverson.Api;\";";

        var blanked = BlankStringLiterals(source);

        blanked.Should().NotContain("Iverson.Api",
            "the identifier is DATA inside the literal, not a real using directive");
        blanked.Should().Contain("var directive =",
            "code outside the literal must survive untouched");
    }

    /// <summary>
    /// A verbatim literal's <c>""</c> is an escaped quote, not the literal's end. Ending the scan
    /// early at the first <c>"</c> of the pair would leave the rest of the source — including a
    /// possible real <c>Iverson.Api</c> reference further along the same line — misread as being
    /// inside (or outside) the literal, which is exactly the bug RequirementsCoverageGateTests.cs
    /// line 1361's own <c>Path.Combine("Iverson.Server", "Iverson.Api")</c> would need this to
    /// avoid, one level further out, for a verbatim literal that quotes a literal quote.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_DoesNotEndAVerbatimLiteralEarlyOnAnEscapedQuote()
    {
        const string source = "var s = @\"a \"\"quoted\"\" word\"; var real = Iverson.Api.Foo;";

        var blanked = BlankStringLiterals(source);

        blanked.Should().NotContain("quoted",
            "the escaped-quote pair does not close the literal, so its contents stay inside it "
            + "and must be blanked along with the rest");
        blanked.Should().Contain("Iverson.Api.Foo",
            "code that follows the literal's real closing quote must be read as code, which only "
            + "holds if the escaped-quote pair inside the literal was not mistaken for its end");
    }
}
