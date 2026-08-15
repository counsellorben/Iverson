namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S2 <c>naming-rejected</c>: Go, Python and TypeScript each register a fixture type whose
/// <c>many_to_one</c> member is misnamed (<c>writer_id</c>/<c>writerId</c>/<c>WriterId</c>
/// against an <c>Author</c>-named related type, instead of the required <c>AuthorId</c>).
/// Registration must fail CLIENT-SIDE, before any RPC — each of the three client libraries
/// carries its own reflection-time check (Go: <c>iverson/registrar.go</c>; Python:
/// <c>iverson_client/core.py</c>; TypeScript: <c>src/core.ts</c>) that raises before
/// <c>RegisterSchema</c> is ever called. This is a register-phase-only scenario: no write, read,
/// update or delete phase is ever run.
///
/// .NET and Java declare the relation's foreign key as a separate field from the navigation
/// property (an <c>AuthorId</c> scalar plus a <c>[ManyToOne]</c>/<c>@ManyToOne</c>-annotated
/// <c>Author</c> property, rather than one member that IS both), so this scenario's fixtures
/// cannot be expressed for them and they render as <c>skip</c> rather than as a driver outcome.
/// The two languages are NOT equivalent, though: Java's registrar (<c>SchemaRegistrar.inferForeignKey</c>)
/// always derives the FK name as <c>"{RelatedTypeName}Id"</c> with no override, so a misnamed FK is as
/// unrepresentable for Java as the scenario's premise assumes. .NET's <c>[ManyToOne]</c> attribute
/// takes an explicit <c>foreignKey</c> argument (propagated verbatim through
/// <c>EntityRegistry</c>/<c>SchemaRegistrar</c>), so a misnamed FK like
/// <c>[ManyToOne(typeof(Author), "WriterId")]</c> is expressible and registers today — nothing
/// client-side or server-side rejects it, since the server's own registration check
/// (<c>SchemaRegistrationOrchestrator</c>) only verifies that the declared FK exists as a property
/// with the correct SQL type (UUID/UUID[]), never that it is spelled a particular way. That is a
/// genuine divergence from Go/Python/TypeScript, left for a human call on whether it should fail
/// the matrix.
/// </summary>
public sealed class NamingRejectedScenario(DriverRunner runner)
{
    public const string Name = "naming-rejected";

    /// <summary>The actual (wrong) member name every fixture uses, in some casing — the driver's
    /// own language dictates which. Checked case-/separator-insensitively.</summary>
    private const string ActualMemberName = "writer";

    /// <summary>The required foreign-key name every fixture's error message must also name.</summary>
    private const string RequiredForeignKeyName = "authorid";

    private static readonly HashSet<string> ClientSideCheckedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "go", "python", "typescript" };

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages, DriverContext context, CancellationToken ct = default)
    {
        var cells = new List<ReportCell>();

        var driverLanguages = languages
            .Where(l => ClientSideCheckedLanguages.Contains(l))
            .ToList();

        foreach (var language in languages.Where(l => !ClientSideCheckedLanguages.Contains(l)))
        {
            cells.Add(ReportCell.Skip(language, Name, SkipReason(language)));
        }

        if (driverLanguages.Count == 0)
            return cells;

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(Phase.Register, driverLanguages, context, ct))
        {
            reported.Add(outcome.Language);
            switch (outcome)
            {
                case DriverPhaseOutcome.Success success:
                    cells.Add(Judge(outcome.Language, success.Document));
                    break;
                case DriverPhaseOutcome.Skipped skipped:
                    cells.Add(ReportCell.Skip(outcome.Language, Name, skipped.Reason));
                    break;
                case DriverPhaseOutcome.Broken broken:
                    cells.Add(ReportCell.Fail(outcome.Language, Name,
                        $"driver broke during the register phase (exit {broken.ExitCode}): {Truncate(broken.Stderr)}"));
                    break;
            }
        }

        // Same guard CrudRoundtripScenario applies: DriverRunner silently produces no outcome at
        // all for a language it does not recognize, which would otherwise fall through to no
        // cell for that language at all.
        foreach (var language in driverLanguages.Where(l => !reported.Contains(l)))
        {
            cells.Add(ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language"));
        }

        return cells;
    }

    /// <summary>
    /// Per-language skip text for the two clients this scenario's fixtures cannot be expressed
    /// for. Both declare the FK as a separate field from the navigation property, but that is
    /// where the equivalence ends — see the class doc comment for what was verified against each
    /// client's and the server's source.
    /// </summary>
    internal static string SkipReason(string language) =>
        string.Equals(language, "java", StringComparison.OrdinalIgnoreCase)
            ? "this client declares a many_to_one relation's foreign key as a separate field from " +
              "the navigation property, and its registrar (SchemaRegistrar.inferForeignKey) always " +
              "derives the FK name as \"{RelatedTypeName}Id\" with no override, so the misnaming " +
              "this scenario provokes cannot be expressed in this client's declaration style"
            : "this client declares a many_to_one relation's foreign key as a separate field from " +
              "the navigation property, so this scenario's fixtures cannot be expressed for it as " +
              "written — but unlike Java, its [ManyToOne] attribute accepts an explicit foreignKey " +
              "argument, so a misnamed FK (e.g. [ManyToOne(typeof(Author), \"WriterId\")]) IS " +
              "expressible and registers today: the server's registration check only verifies the " +
              "declared FK exists with the correct SQL type, never that it is named a particular way";

    internal static ReportCell Judge(string language, PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == "register");
        if (step is null)
            return ReportCell.Fail(language, Name, "the driver reported no 'register' step");

        var message = step.Error ?? string.Empty;

        var assertions = new List<Assertion>
        {
            Assertion.From(
                "register: the misnamed relation failed client-side, before any RPC",
                !step.Ok,
                step.Ok ? "the driver reported the registration as ok" : (step.Error ?? "(no error text)")),

            Assertion.From(
                $"register: the error names the actual, misnamed member ('{ActualMemberName}*')",
                Normalize(message).Contains(ActualMemberName),
                $"error='{message}'"),

            Assertion.From(
                $"register: the error names the required foreign-key name ('{RequiredForeignKeyName}')",
                Normalize(message).Contains(RequiredForeignKeyName),
                $"error='{message}'"),
        };

        var failures = assertions.Where(a => !a.Passed).ToList();
        return failures.Count == 0
            ? ReportCell.Ok(language, Name)
            : ReportCell.Fail(language, Name, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")));
    }

    /// <summary>Lower-cased, separator-stripped — mirrors <c>Verifier.Normalize</c> so the three
    /// languages' differently-cased/spelled error text compares alike.</summary>
    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();
}
