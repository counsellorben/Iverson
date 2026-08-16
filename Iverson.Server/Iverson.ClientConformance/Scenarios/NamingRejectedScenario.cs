using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Contracts;

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
/// <c>Author</c> property, rather than one member that IS both), so this scenario's CLIENT-side
/// fixtures cannot be expressed for them. The two languages are NOT equivalent, though. Java's
/// registrar (<c>SchemaRegistrar.inferForeignKey</c>) always derives the FK name as
/// <c>"{RelatedTypeName}Id"</c> with no override, so a misnamed FK is as unrepresentable for Java
/// as the scenario's premise assumes, and it still renders as <c>skip</c>. .NET's
/// <c>[ManyToOne]</c> attribute takes an explicit <c>foreignKey</c> argument (propagated verbatim
/// through <c>EntityRegistry</c>/<c>SchemaRegistrar</c>), so a misnamed FK like
/// <c>[ManyToOne(typeof(Author), "WriterId")]</c> IS expressible for .NET — and now that T4 added
/// <c>SchemaRegistrationOrchestrator</c>'s foreign-key naming check (~line 110-122,
/// <c>IVC-REG-001</c>), the <c>dotnet</c> column carries a real, orchestrator-side observation
/// instead of a skip: it hand-builds the equivalent descriptor and posts it to
/// <c>RegisterSchema</c> directly (mirroring <c>NavPropertyRejectedScenario</c>'s orchestrator-only
/// style, since no .NET driver process is needed to prove the SERVER rejects this), asserting the
/// server itself rejects it with <c>InvalidArgument</c>.
/// </summary>
public sealed class NamingRejectedScenario(
    DriverRunner runner,
    ObjectMappingService.ObjectMappingServiceClient mapping)
{
    public const string Name = "naming-rejected";

    /// <summary>Language that now gets a real orchestrator-side (server) check instead of a skip.</summary>
    private const string ServerSideCheckedLanguage = "dotnet";

    private const string ServerSideTypeName = "S2NamingDotNet";
    private const string ServerSideRelatedTypeName = "S2NamingAuthor";
    // The misnamed FK a .NET user could legitimately declare via
    // [ManyToOne(typeof(S2NamingAuthor), "WriterId")] — required name is "S2NamingAuthorId".
    private const string ServerSideActualForeignKeyName = "WriterId";

    /// <summary>The actual (wrong) member name every fixture uses, in some casing — the driver's
    /// own language dictates which. Checked case-/separator-insensitively.</summary>
    private const string ActualMemberName = "writer";

    /// <summary>The required foreign-key name every fixture's error message must also name.</summary>
    private const string RequiredForeignKeyName = "authorid";

    private static readonly HashSet<string> ClientSideCheckedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "go", "python", "typescript" };

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages, DriverContext context, string actingToken, CancellationToken ct = default)
    {
        var cells = new List<ReportCell>();

        var driverLanguages = languages
            .Where(l => ClientSideCheckedLanguages.Contains(l))
            .ToList();

        foreach (var language in languages.Where(l =>
                     !ClientSideCheckedLanguages.Contains(l) &&
                     !string.Equals(l, ServerSideCheckedLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            cells.Add(ReportCell.Skip(language, Name, SkipReason(language)));
        }

        if (languages.Contains(ServerSideCheckedLanguage, StringComparer.OrdinalIgnoreCase))
        {
            cells.Add(await RunServerSideCheckAsync(actingToken, ct));
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
                        $"driver broke during the register phase (exit {broken.ExitCode}): {Truncate(broken.Stderr)}", []));
                    break;
            }
        }

        // Same guard CrudRoundtripScenario applies: DriverRunner silently produces no outcome at
        // all for a language it does not recognize, which would otherwise fall through to no
        // cell for that language at all.
        foreach (var language in driverLanguages.Where(l => !reported.Contains(l)))
        {
            cells.Add(ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language", []));
        }

        return cells;
    }

    /// <summary>
    /// Skip text for Java, the only client this scenario now genuinely cannot check at all — its
    /// registrar derives the FK name with no override, so the misnaming this scenario provokes
    /// cannot be expressed in its declaration style, client-side or server-side.
    /// </summary>
    internal static string SkipReason(string language) =>
        "this client declares a many_to_one relation's foreign key as a separate field from " +
        "the navigation property, and its registrar (SchemaRegistrar.inferForeignKey) always " +
        "derives the FK name as \"{RelatedTypeName}Id\" with no override, so the misnaming " +
        "this scenario provokes cannot be expressed in this client's declaration style";

    /// <summary>
    /// The orchestrator-side, server-only check that discharges <c>IVC-REG-001</c> for the one
    /// client declaration style (.NET's <c>[ManyToOne(type, foreignKey)]</c>) that can express a
    /// misnamed foreign key at all. Hand-builds the equivalent descriptor and posts it directly
    /// to <c>RegisterSchema</c> — no .NET driver process runs — asserting that
    /// <c>SchemaRegistrationOrchestrator</c>'s naming check rejects it.
    /// </summary>
    private async Task<ReportCell> RunServerSideCheckAsync(string actingToken, CancellationToken ct)
    {
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };

        RpcException? caught = null;
        try
        {
            await RegisterMisnamedFixtureAsync(headers, ct);
        }
        catch (RpcException ex)
        {
            caught = ex;
        }
        catch (Exception ex)
        {
            return ReportCell.Fail(ServerSideCheckedLanguage, Name, $"fixture registration failed: {ex.GetType().Name}: {ex.Message}", []);
        }

        var assertions = JudgeServerSide(caught);
        var failures = assertions.Where(a => !a.Passed).ToList();
        return failures.Count == 0
            ? ReportCell.Ok(ServerSideCheckedLanguage, Name, assertions)
            : ReportCell.Fail(ServerSideCheckedLanguage, Name, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")), assertions);
    }

    /// <summary>
    /// The assertions themselves, as a pure function over what <c>RegisterSchema</c> produced —
    /// unit-testable without a live stack, mirroring <c>NavPropertyRejectedScenario.Judge</c>.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeServerSide(RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                "register: the server rejects a misnamed foreign key at registration",
                caught is not null,
                caught is null ? "the server registered the descriptor" : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegForeignKeyNamingEnforced),
        };

        if (caught is not null)
        {
            assertions.Add(Assertion.From(
                "register: rejected with InvalidArgument",
                caught.StatusCode == StatusCode.InvalidArgument,
                $"actual={caught.StatusCode}"));

            var message = caught.Status.Detail;
            assertions.Add(Assertion.From(
                $"register: the error names the actual, misnamed foreign key ('{ServerSideActualForeignKeyName}')",
                Normalize(message).Contains(ActualMemberName),
                $"error='{message}'"));
            assertions.Add(Assertion.From(
                $"register: the error names the required foreign-key name ('{ServerSideRelatedTypeName}Id')",
                Normalize(message).Contains(RequiredForeignKeyName),
                $"error='{message}'"));
        }

        return assertions;
    }

    private async Task RegisterMisnamedFixtureAsync(Metadata headers, CancellationToken ct)
    {
        var descriptor = new TypeDescriptor
        {
            TypeName = ServerSideTypeName,
            TenantField = "TenantId",
            Properties =
            {
                new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
                new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString, IsNullable = false },
                new PropertyDescriptor { Name = ServerSideActualForeignKeyName, ClrType = ClrType.ClrGuid, IsNullable = true },
            },
            Relations =
            {
                new RelationDescriptor
                {
                    PropertyName = "Author",
                    Kind = RelationKind.ManyToOne,
                    RelatedType = ServerSideRelatedTypeName,
                    ForeignKey = ServerSideActualForeignKeyName,
                },
            },
        };

        await mapping.RegisterSchemaAsync(
            new SchemaRequest { RootType = descriptor, TraceId = string.Empty },
            headers, cancellationToken: ct);
    }

    internal static ReportCell Judge(string language, PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == "register");
        if (step is null)
            return ReportCell.Fail(language, Name, "the driver reported no 'register' step", []);

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
            ? ReportCell.Ok(language, Name, assertions)
            : ReportCell.Fail(language, Name, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")), assertions);
    }

    /// <summary>Lower-cased, separator-stripped — mirrors <c>Verifier.Normalize</c> so the three
    /// languages' differently-cased/spelled error text compares alike.</summary>
    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();
}
