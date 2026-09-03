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

    private static string TestProjectDir() =>
        Path.Combine(
            RequirementsCoverageGateTests.RepositoryRoot(), "Iverson.Server",
            "Iverson.ClientConformance.Tests");

    private static string HarnessCsprojPath() =>
        Path.Combine(
            RequirementsCoverageGateTests.RepositoryRoot(), "Iverson.Server",
            "Iverson.ClientConformance", "Iverson.ClientConformance.csproj");

    /// <summary>
    /// One file's verdict: whether a real <c>Iverson.Api</c> code dependency survives comment
    /// stripping and string-literal blanking, and — separately — whether the blanking scan itself
    /// could not finish because a literal in the file never closed. The two are independent: an
    /// unterminated scan must FAIL the gate on its own, not be silently read as "no dependency
    /// found", which is exactly the false negative <see cref="BlankStringLiterals"/> used to
    /// produce for any construct it did not understand (Ruling: reviewer finding 1).
    /// </summary>
    private readonly record struct ScanResult(bool ReferencesIversonApi, int? UnterminatedLiteralLine);

    /// <summary>
    /// Scans one file's raw source. See the class doc for why comment-stripping runs before
    /// literal-blanking: <see cref="RequirementsCoverageGateTests.StripCommentLines"/> removes
    /// whole comment/doc lines — including ones whose PROSE contains unbalanced quote characters,
    /// such as <c>&lt;c&gt;Path.Combine("Iverson.Server", "Iverson.Api")&lt;/c&gt;</c> — before
    /// <see cref="BlankStringLiterals"/>'s quote-parity scanner ever has to make sense of them.
    /// Running the two in the other order would reintroduce, from comment prose, exactly the kind
    /// of quote-parity desync this gate exists to close in real code.
    ///
    /// <para><see cref="ScanResult.UnterminatedLiteralLine"/>, when set, counts lines of the
    /// COMMENT-STRIPPED source, not the original file — removed comment lines shift what remains
    /// upward, so the reported number is never greater than the true line and is always close
    /// enough, combined with the file name, to find the literal by search.</para>
    /// </summary>
    private static ScanResult Scan(string rawSource)
    {
        var withoutCommentLines = RequirementsCoverageGateTests.StripCommentLines(rawSource);
        var withoutStringLiterals =
            BlankStringLiterals(withoutCommentLines, out var unterminatedLiteralLine);
        var references = IversonApiCodeReference.IsMatch(withoutStringLiterals);
        return new ScanResult(references, unterminatedLiteralLine);
    }

    /// <summary>
    /// Blanks the CONTENTS of C# string, raw-string and char literals, and drops trailing line
    /// comments, so an identifier one of those spells out as DATA — or as prose after a
    /// same-line <c>//</c> — cannot be mistaken for a reference in CODE. Recognises:
    /// <list type="bullet">
    /// <item><description>Ordinary/interpolated <c>"..."</c>, where <c>\x</c> escapes any
    /// character without ending the literal — but a raw newline reached while still
    /// inside one, which C# never permits in this form, ends the scan as UNTERMINATED
    /// rather than being absorbed into the literal the way the multi-line forms below
    /// legitimately absorb one.</description></item>
    /// <item><description>Verbatim <c>@"..."</c>, where <c>""</c> escapes a quote without ending
    /// the literal.</description></item>
    /// <item><description>Raw string literals opened by a run of three or more <c>"</c>
    /// characters, closed by the first later run of AT LEAST that many consecutive
    /// <c>"</c> characters (only the matching count is consumed as the closer).</description></item>
    /// <item><description>Char literals — <c>'x'</c>, <c>'\x'</c>, <c>'\uXXXX'</c>, <c>'\''</c>,
    /// <c>'\\'</c> — recognised well enough that an embedded quote, as in <c>'"'</c>, cannot be
    /// misread as opening an ordinary string literal and desynchronising every quote that follows
    /// it in the file. A <c>'</c> that does not fit one of these shapes is left as an ordinary
    /// character rather than guessed at.</description></item>
    /// <item><description>A trailing <c>//</c> line comment reached OUTSIDE any literal — the
    /// rest of that line is blanked, so a quote inside prose like <c>// it's "quoted</c> cannot
    /// open a literal that swallows real code after it.</description></item>
    /// </list>
    ///
    /// <para>An interpolated string (<c>$"..."</c>, <c>$@"..."</c> or a raw interpolated string)
    /// is treated the same as its non-interpolated form — its <c>{expression}</c> holes are
    /// blanked along with everything else. That can never produce a false NEGATIVE (code this
    /// gate needs to see always sits outside a string literal, interpolation holes included, in
    /// every construct in this codebase); it can only ever over-blank, which is the safe
    /// direction for a gate whose failure mode to guard against is a missed dependency.</para>
    ///
    /// <para><b>Unterminated literals are not discarded.</b> If a literal opened by any of the
    /// three quote-delimited forms above never finds its close before the source ends — or, for
    /// an ordinary/interpolated literal specifically, reaches a newline first, which C# does not
    /// allow inside one — the scan stops there and <paramref name="unterminatedLiteralLine"/> is
    /// set to the (comment-stripped) line the literal opened on. Both are the same silent false
    /// negative in different shapes: without the newline check, an ordinary literal that opens
    /// mid-file and later finds an unrelated closing quote elsewhere in the file — even-parity
    /// overall, but never actually terminated on its own line — would blank everything in between,
    /// including a real <c>using Iverson.Api;</c>, and the scan would finish "successfully" having
    /// silently swallowed it. Turning either shape into a result the caller can, and must, fail
    /// loudly on is Ruling: reviewer finding 1's fix.</para>
    ///
    /// <para>This is deliberately not a general C# lexer: no preprocessor directives, no verbatim
    /// identifiers (<c>@class</c>), no numeric literal suffixes that happen to look like
    /// identifiers. It exists to close the false positives and false negatives this specific gate
    /// would otherwise hit on this specific codebase, not to parse arbitrary C#.</para>
    /// </summary>
    internal static string BlankStringLiterals(string source, out int? unterminatedLiteralLine)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        var line = 1;
        unterminatedLiteralLine = null;

        // Appends one content character from inside a literal, blanking it unless it is the
        // newline a multi-line verbatim or raw literal may legitimately contain — which must
        // survive so the line count stays correct for everything that follows.
        void AppendContentChar(char ch)
        {
            if (ch == '\n')
            {
                line++;
                result.Append('\n');
            }
            else
            {
                result.Append(' ');
            }
        }

        while (i < source.Length)
        {
            var c = source[i];

            // A trailing line comment reached outside any literal: nothing in it, including a
            // stray quote, can open one. Blank to (not including) the newline; the newline itself
            // is handled by the ordinary path below so the line counter stays in step.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    result.Append(' ');
                    i++;
                }

                continue;
            }

            // Raw string literal: a run of 3+ '"'. Content runs until the first later run of at
            // least as many consecutive '"'; only that many are consumed as the close.
            if (c == '"' && CountConsecutiveQuotes(source, i) >= 3)
            {
                var openLength = CountConsecutiveQuotes(source, i);
                var literalStartLine = line;
                result.Append(' ', openLength);
                i += openLength;

                var closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        var closeLength = CountConsecutiveQuotes(source, i);
                        if (closeLength >= openLength)
                        {
                            result.Append(' ', openLength);
                            i += openLength;
                            closed = true;
                            break;
                        }

                        result.Append(' ', closeLength);
                        i += closeLength;
                        continue;
                    }

                    AppendContentChar(source[i]);
                    i++;
                }

                if (!closed)
                {
                    unterminatedLiteralLine ??= literalStartLine;
                    break;
                }

                continue;
            }

            // Verbatim literal: @"...", where "" is an escaped quote that does not close it.
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                var literalStartLine = line;
                result.Append(' ', 2);
                i += 2;

                var closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            result.Append(' ', 2);
                            i += 2;
                            continue;
                        }

                        result.Append(' ');
                        i++;
                        closed = true;
                        break;
                    }

                    AppendContentChar(source[i]);
                    i++;
                }

                if (!closed)
                {
                    unterminatedLiteralLine ??= literalStartLine;
                    break;
                }

                continue;
            }

            // Ordinary (or interpolated) literal: "...", where \x is an escaped character pair
            // that does not close it, whatever x is.
            if (c == '"')
            {
                var literalStartLine = line;
                result.Append(' ');
                i++;

                var closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(' ', 2);
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        result.Append(' ');
                        i++;
                        closed = true;
                        break;
                    }

                    if (source[i] == '\n')
                    {
                        // C# does not permit a raw newline inside an ordinary or
                        // interpolated "..." literal — reaching one while still inside
                        // this literal means it never closed. Stop here, without
                        // consuming the newline, so the outer handling below reports it
                        // exactly like the end-of-input case: an unmatched quote later
                        // in the file (even-parity overall) must not silently re-sync
                        // and blank the intervening text, which could hide a real
                        // dependency (reviewer finding).
                        break;
                    }

                    AppendContentChar(source[i]);
                    i++;
                }

                if (!closed)
                {
                    unterminatedLiteralLine ??= literalStartLine;
                    break;
                }

                continue;
            }

            // Char literal: 'x', '\x', '\uXXXX', '\'', '\\'. Recognising this here is what stops
            // an embedded quote, as in '"', from being misread two branches up as opening an
            // ordinary string literal — which would desynchronise every quote for the rest of the
            // file (the exact bug this method's own first draft hit on itself).
            if (c == '\'')
            {
                var consumed = TryConsumeCharLiteral(source, i);
                if (consumed > 0)
                {
                    result.Append(' ', consumed);
                    i += consumed;
                    continue;
                }
            }

            if (c == '\n')
            {
                line++;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>How many consecutive <c>"</c> characters start at <paramref name="index"/>.</summary>
    private static int CountConsecutiveQuotes(string source, int index)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == '"')
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// If a recognised char literal — <c>'x'</c>, <c>'\x'</c> for any escaped character,
    /// <c>'\uXXXX'</c>, <c>'\''</c>, or <c>'\\'</c> — opens at <paramref name="index"/>, returns
    /// its total length including both quotes; otherwise 0, so the caller treats the opening
    /// <c>'</c> as an ordinary character rather than guessing at a shape this does not recognise.
    /// </summary>
    private static int TryConsumeCharLiteral(string source, int index)
    {
        var i = index + 1;
        if (i >= source.Length)
        {
            return 0;
        }

        if (source[i] == '\\')
        {
            i++;
            if (i >= source.Length)
            {
                return 0;
            }

            if (source[i] == 'u')
            {
                i++;
                var hexDigits = 0;
                while (i < source.Length && hexDigits < 4 && Uri.IsHexDigit(source[i]))
                {
                    i++;
                    hexDigits++;
                }
            }
            else
            {
                i++; // the single escaped character, e.g. \\, \', \n, \0, \t
            }
        }
        else
        {
            i++; // the single literal character, e.g. x in 'x', or " in '"'
        }

        if (i < source.Length && source[i] == '\'')
        {
            return i + 1 - index;
        }

        return 0;
    }

    private static bool IsBuildOutputPath(string filePath, string testProjectDir) =>
        filePath.StartsWith(Path.Combine(testProjectDir, "bin"), StringComparison.Ordinal)
        || filePath.StartsWith(Path.Combine(testProjectDir, "obj"), StringComparison.Ordinal);

    /// <summary>
    /// Assertion 1, in three parts, each with its own reason: the set of test-project files with
    /// a real <c>Iverson.Api</c> code dependency must equal <see cref="AllowlistedFiles"/> in both
    /// directions, AND the scan must have been able to finish for every file — an unterminated
    /// literal is graded as a failure of its own rather than folded into "no dependency found".
    /// </summary>
    [Fact]
    public void OnlyAllowlistedFiles_DependOnIversonApi()
    {
        var testProjectDir = TestProjectDir();

        var results = Directory
            .EnumerateFiles(testProjectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path, testProjectDir))
            .Select(path => (FileName: Path.GetFileName(path)!, Scan: Scan(File.ReadAllText(path))))
            .ToList();

        var unterminated = results
            .Where(r => r.Scan.UnterminatedLiteralLine is not null)
            .Select(r => $"{r.FileName}:{r.Scan.UnterminatedLiteralLine}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unterminated.Should().BeEmpty(
            "each of these has a string, raw-string or verbatim literal that never closes, at "
            + "the named (comment-stripped) line — BlankStringLiterals cannot judge whether the "
            + "rest of the file references Iverson.Api, so this must fail loudly rather than "
            + "silently reporting no dependency found");

        var dependentFiles = results
            .Where(r => r.Scan.ReferencesIversonApi)
            .Select(r => r.FileName)
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

        var offendingReferences = ExtractProjectReferenceIncludes(csprojContent)
            .Where(include => IversonApiCodeReference.IsMatch(include.Replace('\\', '/')))
            .ToList();

        offendingReferences.Should().BeEmpty(
            "Iverson.ClientConformance.csproj must not reference Iverson.Api: a probe that shares "
            + "the server's own constants cannot catch the server changing them. Only the TEST "
            + "project (via SchemaProbeTests) is allowed that exception, and only because it needs "
            + "the server's real SchemaDescriptor types to build fixtures.");
    }

    /// <summary>
    /// Extracts every <c>Include</c> value from a <c>&lt;ProjectReference&gt;</c> element in a
    /// csproj file's raw text. <c>Include</c> is not required to be the element's first attribute
    /// — MSBuild allows e.g. <c>&lt;ProjectReference Condition="..." Include="..." /&gt;</c> — and
    /// its value may be single- or double-quoted, so the pattern anchors only on the element name
    /// and the <c>Include=</c> token, not on attribute order or quote style (reviewer finding:
    /// the prior <c>Include</c>-must-be-first, double-quote-only pattern let a
    /// <c>Condition</c>-first or single-quoted reference slip past the gate undetected).
    /// </summary>
    internal static IEnumerable<string> ExtractProjectReferenceIncludes(string csprojContent) =>
        Regex
            .Matches(csprojContent, @"<ProjectReference[^>]*Include\s*=\s*[""']([^""']*)[""']")
            .Select(m => m.Groups[1].Value);

    private static string Blank(string source)
    {
        var blanked = BlankStringLiterals(source, out var unterminatedLiteralLine);
        unterminatedLiteralLine.Should().BeNull(
            "this fixture is meant to be a well-formed, fully-terminated source snippet");
        return blanked;
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

        var blanked = Blank(source);

        blanked.Should().NotContain("Iverson.Api",
            "the identifier is DATA inside the literal, not a real using directive");
        blanked.Should().Contain("var directive =",
            "code outside the literal must survive untouched");
    }

    /// <summary>
    /// The ordinary-literal branch's own escape handling (<c>\"</c> does not close the literal),
    /// pinned directly — previously only the verbatim literal's <c>""</c> escape had a test, so
    /// this branch could regress unnoticed (reviewer finding: this case was unpinned).
    /// </summary>
    [Fact]
    public void BlankStringLiterals_DoesNotEndAnOrdinaryLiteralEarlyOnABackslashEscapedQuote()
    {
        const string source = "var s = \"a \\\"quoted\\\" word\"; var real = Iverson.Api.Foo;";

        var blanked = Blank(source);

        blanked.Should().NotContain("quoted",
            "the \\\"-escaped quote does not close the literal, so its contents stay inside it "
            + "and must be blanked along with the rest");
        blanked.Should().Contain("Iverson.Api.Foo",
            "code that follows the literal's real closing quote must be read as code, which only "
            + "holds if the backslash-escaped quote inside the literal was not mistaken for its "
            + "end");
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

        var blanked = Blank(source);

        blanked.Should().NotContain("quoted",
            "the escaped-quote pair does not close the literal, so its contents stay inside it "
            + "and must be blanked along with the rest");
        blanked.Should().Contain("Iverson.Api.Foo",
            "code that follows the literal's real closing quote must be read as code, which only "
            + "holds if the escaped-quote pair inside the literal was not mistaken for its end");
    }

    /// <summary>
    /// A raw string literal (<c>"""..."""</c>) whose body contains a lone, unescaped <c>"</c>
    /// must not desynchronise the scan: the body's quote does not by itself close a 3+-quote
    /// delimiter, so a real <c>using Iverson.Api;</c> after the literal must still be readable as
    /// code (reviewer finding 1, construct (a)).
    /// </summary>
    [Fact]
    public void BlankStringLiterals_DoesNotDesyncOnALoneQuoteInsideARawStringLiteral()
    {
        const string source = """"
            var s = """
                a "quoted word inside a raw string
                """;
            using Iverson.Api;
            """";

        var blanked = Blank(source);

        blanked.Should().NotContain("quoted",
            "the raw string's body is DATA and must be blanked even though it contains a lone "
            + "unescaped quote");
        blanked.Should().Contain("using Iverson.Api;",
            "real code after the raw string's actual close must still read as code — a lone "
            + "quote inside the body must not be misread as the delimiter closing early");
    }

    /// <summary>
    /// A char literal spelling out a double-quote character, <c>'"'</c>, must not be misread as
    /// opening an ordinary string literal: doing so would consume everything up to the NEXT
    /// unrelated quote in the file as blanked "literal content", hiding a real dependency behind
    /// it (reviewer finding 1, construct (b) — the exact bug this gate's own first draft hit on
    /// itself).
    /// </summary>
    [Fact]
    public void BlankStringLiterals_DoesNotDesyncOnADoubleQuoteCharLiteral()
    {
        const string source = "var q = '\"'; using Iverson.Api;";

        var blanked = Blank(source);

        blanked.Should().Contain("using Iverson.Api;",
            "the char literal must be recognised and skipped as a unit, not misread as opening "
            + "a new ordinary string literal that swallows the real using directive after it");
    }

    /// <summary>
    /// A trailing <c>//</c> comment containing an odd quote — prose like <c>it's "quoted</c> —
    /// must not open a literal that swallows the rest of the file: StripCommentLines only removes
    /// WHOLE comment lines, so a same-line trailing comment reaches this scanner as-is (reviewer
    /// finding 1, construct (c)).
    /// </summary>
    [Fact]
    public void BlankStringLiterals_DoesNotDesyncOnATrailingCommentContainingAnOddQuote()
    {
        const string source = "var x = 1; // it's \"quoted\nusing Iverson.Api;";

        var blanked = Blank(source);

        blanked.Should().NotContain("quoted",
            "the trailing comment's prose is not code and must be blanked");
        blanked.Should().Contain("using Iverson.Api;",
            "code on the line after a trailing comment must still read as code — the odd quote "
            + "in the comment must not be misread as opening a literal that runs into it");
    }

    /// <summary>
    /// The loud-failure half of the fix: when a literal genuinely never closes, the scan must
    /// stop and report where it started, rather than silently returning as if nothing had been
    /// found. A real <c>using Iverson.Api;</c> placed after the unterminated literal is exactly
    /// the false negative that used to be discarded, so it is deliberately included here and
    /// must NOT be what the caller sees — only the unterminated report matters.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_ReportsAnUnterminatedLiteral_InsteadOfSilentlyFinishing()
    {
        const string source = "line one;\nvar s = \"never closes\nusing Iverson.Api;";

        BlankStringLiterals(source, out var unterminatedLiteralLine);

        unterminatedLiteralLine.Should().Be(2,
            "the ordinary literal opens on line 2 of the source, and never finds a closing quote "
            + "before the source ends");
    }

    /// <summary>
    /// The remaining silent case (reviewer finding): an ordinary literal that opens mid-file and
    /// is never closed on its OWN line can still, before this fix, re-sync on some unrelated later
    /// quote that happens to make the file's total quote count even — closing over everything in
    /// between, including a real <c>using Iverson.Api;</c>, and finishing with NO unterminated
    /// report at all. Here quote 1 opens the literal on line 1; the only other quote in the source
    /// closes on line 3, so the total is even (2) and the scan used to finish "successfully" having
    /// silently blanked the intervening <c>using Iverson.Api;</c> along with everything else. C#
    /// forbids a raw newline inside this literal form, so reaching one — as this source does,
    /// immediately after "never closes;" — must end the literal as UNTERMINATED right there,
    /// before it ever reaches that unrelated closing quote. (The originating brief's own
    /// example source, <c>var t = "x";</c>, was tried first and rejected: its quote count is
    /// odd, so the pre-fix scan reports it unterminated at line 3 rather than finishing
    /// silently, and it would not have pinned this case — see the batch report.)
    /// </summary>
    [Fact]
    public void BlankStringLiterals_EndsAnOrdinaryLiteralAtANewline_EvenWhenALaterQuoteWouldMakeParityEven()
    {
        const string source = "var s = \"never closes;\nusing Iverson.Api;\nvar t = x\";";

        BlankStringLiterals(source, out var unterminatedLiteralLine);

        unterminatedLiteralLine.Should().Be(1,
            "the literal opened on line 1 is never closed on its own line; a later, unrelated "
            + "closing quote on line 3 makes the file's total quote count even, but re-syncing on "
            + "that parity — instead of failing where the newline was actually reached — is "
            + "exactly the silent false negative this fix closes");
    }

    /// <summary>
    /// A verbatim literal is one of the two forms legitimately allowed to span multiple lines: the
    /// newline-terminates-the-literal fix added to the ordinary-literal branch above must not leak
    /// into this branch. Regression guard for the fix landing in the wrong branch.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_AVerbatimLiteralSpanningMultipleLines_StillReportsNoUnterminatedLiteral()
    {
        const string source = "var s = @\"first line\nsecond line\"; using Iverson.Api;";

        var blanked = Blank(source);

        blanked.Should().NotContain("first line",
            "the verbatim literal's multi-line body is DATA and must be blanked in full");
        blanked.Should().NotContain("second line",
            "the newline inside a verbatim literal is legitimate and must not end it early");
        blanked.Should().Contain("using Iverson.Api;",
            "code after a verbatim literal that legitimately spans lines must still read as code");
    }

    /// <summary>
    /// A raw string literal is the other form legitimately allowed to span multiple lines: same
    /// regression guard as the verbatim case above, for the other multi-line-legal branch.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_ARawStringLiteralSpanningMultipleLines_StillReportsNoUnterminatedLiteral()
    {
        const string source = """"
            var s = """
                first line
                second line
                """;
            using Iverson.Api;
            """";

        var blanked = Blank(source);

        blanked.Should().NotContain("first line",
            "the raw string literal's multi-line body is DATA and must be blanked in full");
        blanked.Should().NotContain("second line",
            "the newline inside a raw string literal is legitimate and must not end it early");
        blanked.Should().Contain("using Iverson.Api;",
            "code after a raw string literal that legitimately spans lines must still read as code");
    }

    /// <summary>
    /// An interpolated <c>$"..."</c> literal on a single line goes through the same ordinary-
    /// literal branch as a plain <c>"..."</c> — the newline fix above must not disturb the case
    /// that never reaches a newline at all.
    /// </summary>
    [Fact]
    public void BlankStringLiterals_AnInterpolatedLiteralOnOneLine_StillBlanksAndReportsNoUnterminatedLiteral()
    {
        const string source = "var s = $\"value: {x}\"; using Iverson.Api;";

        var blanked = Blank(source);

        blanked.Should().NotContain("value:",
            "the interpolated literal's text (and its {expression} hole) is DATA and must be "
            + "blanked");
        blanked.Should().Contain("using Iverson.Api;",
            "code after a single-line interpolated literal must still read as code");
    }

    /// <summary>
    /// Pins the fix for assertion 2's false-negative shape (reviewer finding): a
    /// <c>&lt;ProjectReference&gt;</c> whose <c>Include</c> attribute is neither first nor
    /// double-quoted — here, <c>Condition</c> comes first and <c>Include</c> is single-quoted —
    /// must still be detected as referencing <c>Iverson.Api</c>. The prior pattern anchored on
    /// <c>Include</c> being the first attribute and double-quoted, so this exact shape slipped
    /// through and the gate passed green on an undetected dependency.
    /// </summary>
    [Fact]
    public void ExtractProjectReferenceIncludes_MatchesAConditionFirstSingleQuotedInclude()
    {
        const string source =
            "<ProjectReference Condition=\"'$(Configuration)'=='Debug'\" "
            + "Include='../Iverson.Api/Iverson.Api.csproj' />";

        var includes = ExtractProjectReferenceIncludes(source).ToList();

        includes.Should().ContainSingle()
            .Which.Should().Be("../Iverson.Api/Iverson.Api.csproj",
                "the Include value must be captured even though Condition precedes it and the "
                + "quotes are single, not double — the shape the prior regex missed");
    }
}
