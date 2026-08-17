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
/// <c>IVC-REG-003</c>, scoped to <c>many_to_one</c>/<c>one_to_one</c>/<c>many_to_many</c> — the
/// retired <c>IVC-REG-001</c> read unqualified over every kind, which was factually wrong for
/// <c>one_to_many</c>), the server-side check normally carries a real, orchestrator-side
/// observation on the <c>dotnet</c> column instead of a skip: it hand-builds equivalent
/// <c>many_to_one</c> and <c>many_to_many</c> descriptors and posts each to
/// <c>RegisterSchema</c> directly (mirroring <c>NavPropertyRejectedScenario</c>'s orchestrator-only
/// style, since no .NET driver process is needed to prove the SERVER rejects this), asserting the
/// server itself rejects both with <c>InvalidArgument</c>. This check is purely server-side, so a
/// requested language set that omits <c>dotnet</c> still carries it — see
/// <see cref="ServerCheckPriority"/>.
/// </summary>
public sealed class NamingRejectedScenario(
    DriverRunner runner,
    ObjectMappingService.ObjectMappingServiceClient mapping)
{
    public const string Name = "naming-rejected";

    /// <summary>Language that would ideally carry the orchestrator-side (server) check, since
    /// .NET's <c>[ManyToOne(type, foreignKey)]</c> is the one client-declaration style that can
    /// express a misnamed foreign key at all.</summary>
    private const string ServerSideCheckedLanguage = "dotnet";

    /// <summary>
    /// Fallback priority for which requested language actually carries the server-side check when
    /// <see cref="ServerSideCheckedLanguage"/> was not requested. IVC-REG-003 is purely
    /// server-side — it does not depend on any driver process — so it must not go untouched merely
    /// because a partial <c>--languages</c> run happened to omit dotnet, the same reasoning
    /// <see cref="NavPropertyRejectedScenario.CanonicalLanguage"/> already applies. <c>java</c> is
    /// second priority because it is the one client whose registrar can never express the
    /// misnaming client-side either (its FK name is always derived, no override), so it otherwise
    /// carries nothing but a bare skip and has no other outcome to collide with. If neither dotnet
    /// nor java was requested, the check's assertions are merged into whichever go/python/typescript
    /// column the fallback lands on, alongside that language's own client-side check, rather than
    /// being dropped — see <see cref="RunAsync"/>.
    /// </summary>
    private static readonly IReadOnlyList<string> ServerCheckPriority =
        [ServerSideCheckedLanguage, "java", "go", "python", "typescript"];

    private const string ServerSideTypeName = "S2NamingDotNet";
    private const string ServerSideRelatedTypeName = "S2NamingAuthor";
    // The misnamed FK a .NET user could legitimately declare via
    // [ManyToOne(typeof(S2NamingAuthor), "WriterId")] — required name is "S2NamingAuthorId".
    private const string ServerSideActualForeignKeyName = "WriterId";

    // The many_to_many arm: a .NET user could declare [ManyToMany(typeof(S2NamingTag), "TagIds")]
    // with a misnamed foreign key. This gives the parenthetical "Ids for many_to_many" half of
    // IVC-REG-003's statement its own citation — previously only the many_to_one half had one.
    private const string ServerSideManyToManyTypeName = "S2NamingDotNetTags";
    private const string ServerSideManyToManyRelatedTypeName = "S2NamingTag";
    private const string ServerSideManyToManyActualForeignKeyName = "TagRefs";
    private const string ServerSideManyToManyRequiredForeignKeyName = "S2NamingTagIds";

    /// <summary>The actual (wrong) member name every fixture uses, in some casing — the driver's
    /// own language dictates which. Checked case-/separator-insensitively.</summary>
    private const string ActualMemberName = "writer";

    /// <summary>The required foreign-key name every fixture's error message must also name.</summary>
    private const string RequiredForeignKeyName = "authorid";

    private static readonly HashSet<string> ClientSideCheckedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "go", "python", "typescript" };

    /// <summary>
    /// Picks the one language, among those requested, that carries the server-side check —
    /// mirrors <see cref="NavPropertyRejectedScenario.CanonicalLanguage"/>'s fallback so a partial
    /// <c>--languages</c> run never silently leaves IVC-REG-003 untouched.
    /// </summary>
    internal static string ServerCheckLanguage(IReadOnlyCollection<string> languages) =>
        ServerCheckPriority.FirstOrDefault(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
        ?? languages.FirstOrDefault()
        ?? string.Empty;

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages, DriverContext context, string actingToken, CancellationToken ct = default)
    {
        var cells = new List<ReportCell>();

        var driverLanguages = languages
            .Where(l => ClientSideCheckedLanguages.Contains(l))
            .ToList();

        var serverCheckLanguage = ServerCheckLanguage(languages);
        var mergeServerCheckIntoDriverCell =
            serverCheckLanguage.Length > 0 && ClientSideCheckedLanguages.Contains(serverCheckLanguage);

        foreach (var language in languages.Where(l =>
                     !ClientSideCheckedLanguages.Contains(l) &&
                     !string.Equals(l, serverCheckLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            cells.Add(ReportCell.Skip(language, Name, SkipReason(language)));
        }

        IReadOnlyList<Assertion>? serverCheckAssertions = null;
        if (serverCheckLanguage.Length > 0)
        {
            try
            {
                serverCheckAssertions = await RunServerSideCheckAsync(actingToken, ct);
            }
            catch (Exception ex)
            {
                cells.Add(ReportCell.Fail(serverCheckLanguage, Name,
                    $"fixture registration failed: {ex.GetType().Name}: {ex.Message}", []));
                serverCheckAssertions = null;
                mergeServerCheckIntoDriverCell = false;
                // The harness precondition for this language already failed and its one cell is
                // in the report above — do not also run its driver, which would otherwise add a
                // SECOND cell for the same (language, scenario) pair. Report.RenderText grids by
                // FirstOrDefault, so a second cell here would silently never render, breaking the
                // one-cell-per-language-per-scenario invariant without ever failing loudly.
                driverLanguages.RemoveAll(l => string.Equals(l, serverCheckLanguage, StringComparison.OrdinalIgnoreCase));
            }

            if (serverCheckAssertions is not null && !mergeServerCheckIntoDriverCell)
            {
                cells.Add(BuildCell(serverCheckLanguage, serverCheckAssertions));
            }
        }

        if (driverLanguages.Count == 0)
            return cells;

        // Whether `language` is the one driver column IVC-REG-003's (already-computed) assertions
        // must be attached to. Only meaningful once serverCheckAssertions is non-null, which is
        // exactly the condition below that guards every use of this.
        bool CarriesServerCheck(string language) =>
            mergeServerCheckIntoDriverCell &&
            serverCheckAssertions is not null &&
            string.Equals(language, serverCheckLanguage, StringComparison.OrdinalIgnoreCase);

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(Phase.Register, driverLanguages, context, ct))
        {
            reported.Add(outcome.Language);
            var carriesServerCheck = CarriesServerCheck(outcome.Language);

            switch (outcome)
            {
                case DriverPhaseOutcome.Success success:
                    var clientAssertions = JudgeClientSideAssertions(success.Document);
                    if (clientAssertions is null)
                    {
                        cells.Add(carriesServerCheck
                            ? MergeServerCheckIntoDriverFailure(outcome.Language,
                                "the driver reported no 'register' step", serverCheckAssertions!)
                            : ReportCell.Fail(outcome.Language, Name, "the driver reported no 'register' step", []));
                        break;
                    }

                    var mergedAssertions = carriesServerCheck
                        ? clientAssertions.Concat(serverCheckAssertions!).ToList()
                        : clientAssertions;
                    cells.Add(BuildCell(outcome.Language, mergedAssertions));
                    break;
                case DriverPhaseOutcome.Skipped skipped:
                    cells.Add(carriesServerCheck
                        ? MergeServerCheckIntoDriverSkip(outcome.Language, skipped.Reason, serverCheckAssertions!)
                        : ReportCell.Skip(outcome.Language, Name, skipped.Reason));
                    break;
                case DriverPhaseOutcome.Broken broken:
                    var brokenDetail =
                        $"driver broke during the register phase (exit {broken.ExitCode}): {Truncate(broken.Stderr)}";
                    cells.Add(carriesServerCheck
                        ? MergeServerCheckIntoDriverFailure(outcome.Language, brokenDetail, serverCheckAssertions!)
                        : ReportCell.Fail(outcome.Language, Name, brokenDetail, []));
                    break;
            }
        }

        // Same guard CrudRoundtripScenario applies: DriverRunner silently produces no outcome at
        // all for a language it does not recognize, which would otherwise fall through to no
        // cell for that language at all.
        foreach (var language in driverLanguages.Where(l => !reported.Contains(l)))
        {
            var unrecognizedDetail = $"'{language}' is not a recognized conformance driver language";
            cells.Add(CarriesServerCheck(language)
                ? MergeServerCheckIntoDriverFailure(language, unrecognizedDetail, serverCheckAssertions!)
                : ReportCell.Fail(language, Name, unrecognizedDetail, []));
        }

        return cells;
    }

    /// <summary>
    /// Merges IVC-REG-003's (already-decided) server-side outcome into a driver-side FAILURE
    /// (missing register step / broken driver / unrecognized language). The rule: a driver
    /// failure never gets masked by a passing server-side check — the cell stays Fail either way
    /// — but when the server-side check ITSELF failed, that failure's own detail replaces
    /// <paramref name="driverDetail"/> as the headline reason (via <see cref="BuildCell"/>) so a
    /// real server-side regression is never reported as merely "the driver broke". Either way the
    /// server-side assertions are always attached, so REG-003 is exercised on this path
    /// regardless of which failure produced the red cell.
    /// </summary>
    internal static ReportCell MergeServerCheckIntoDriverFailure(
        string language, string driverDetail, IReadOnlyList<Assertion> serverCheckAssertions)
    {
        if (serverCheckAssertions.Any(a => !a.Passed))
            return BuildCell(language, serverCheckAssertions);

        return ReportCell.Fail(language, Name, driverDetail, serverCheckAssertions);
    }

    /// <summary>
    /// Merges IVC-REG-003's (already-decided) server-side outcome into a driver-side SKIP. The
    /// rule: a driver skip must never turn a real server-side FAILURE green — if the server-side
    /// check itself failed, this renders as Fail (via <see cref="BuildCell"/>), overriding the
    /// skip outright, since a skipped driver carries no outcome of its own to protect. Only when
    /// the server-side check passed does the driver's own Skip stand, with the passing assertions
    /// attached purely so REG-003 is recorded as exercised.
    /// </summary>
    private static ReportCell MergeServerCheckIntoDriverSkip(
        string language, string skipReason, IReadOnlyList<Assertion> serverCheckAssertions)
    {
        if (serverCheckAssertions.Any(a => !a.Passed))
            return BuildCell(language, serverCheckAssertions);

        return ReportCell.Skip(language, Name, skipReason, serverCheckAssertions);
    }

    /// <summary>
    /// Skip text for a language reaching this scenario's "no check at all" branch — either Java
    /// (when it was requested but dotnet already claimed the server-side check) or a language this
    /// harness does not recognize at all (e.g. <c>--languages dotnet,rust</c>). The two have
    /// different, non-interchangeable reasons: Java's is a real limitation of its declaration
    /// style (checked client-side AND server-side, and it can express neither); an unrecognized
    /// language simply is not a conformance driver this harness knows how to run. Returning Java's
    /// explanation for both — as this once did — would render Java's registrar-specific reasoning
    /// under a language it says nothing about.
    /// </summary>
    internal static string SkipReason(string language) =>
        string.Equals(language, "java", StringComparison.OrdinalIgnoreCase)
            ? "this client declares a many_to_one relation's foreign key as a separate field from " +
              "the navigation property, and its registrar (SchemaRegistrar.inferForeignKey) always " +
              "derives the FK name as \"{RelatedTypeName}Id\" with no override, so the misnaming " +
              "this scenario provokes cannot be expressed in this client's declaration style"
            : $"'{language}' is not a recognized conformance driver language for naming-rejected " +
              "(only go, python and typescript carry the client-side check; dotnet and java are " +
              "handled separately)";

    /// <summary>
    /// The orchestrator-side, server-only check that discharges <c>IVC-REG-003</c>. Hand-builds
    /// two equivalent descriptors — a <c>many_to_one</c> fixture (the one client declaration style,
    /// .NET's <c>[ManyToOne(type, foreignKey)]</c>, that can express a misnamed singular foreign
    /// key at all) and a <c>many_to_many</c> fixture (giving the statement's <c>Ids</c>
    /// parenthetical its own citation) — and posts each directly to <c>RegisterSchema</c>. No
    /// driver process runs for either.
    /// </summary>
    private async Task<IReadOnlyList<Assertion>> RunServerSideCheckAsync(string actingToken, CancellationToken ct)
    {
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };

        var manyToOne = await TryRegisterAsync(() => RegisterMisnamedFixtureAsync(headers, ct));
        var manyToMany = await TryRegisterAsync(() => RegisterMisnamedManyToManyFixtureAsync(headers, ct));

        return JudgeServerSide(manyToOne).Concat(JudgeServerSideManyToMany(manyToMany)).ToList();
    }

    private static async Task<RpcException?> TryRegisterAsync(Func<Task> register)
    {
        try
        {
            await register();
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// The assertions themselves, as a pure function over what <c>RegisterSchema</c> produced for
    /// the <c>many_to_one</c> fixture — unit-testable without a live stack, mirroring
    /// <c>NavPropertyRejectedScenario.Judge</c>.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeServerSide(RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                "register (many_to_one): the server rejects a misnamed foreign key at registration",
                caught is not null,
                caught is null ? "the server registered the descriptor" : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegForeignKeyNamingEnforced),
        };

        if (caught is not null)
        {
            assertions.Add(Assertion.From(
                "register (many_to_one): rejected with InvalidArgument",
                caught.StatusCode == StatusCode.InvalidArgument,
                $"actual={caught.StatusCode}"));

            var message = caught.Status.Detail;
            assertions.Add(Assertion.From(
                $"register (many_to_one): the error names the actual, misnamed foreign key ('{ServerSideActualForeignKeyName}')",
                Normalize(message).Contains(ActualMemberName),
                $"error='{message}'"));
            assertions.Add(Assertion.From(
                $"register (many_to_one): the error names the required foreign-key name ('{ServerSideRelatedTypeName}Id')",
                Normalize(message).Contains(RequiredForeignKeyName),
                $"error='{message}'"));
        }

        return assertions;
    }

    /// <summary>
    /// The <c>many_to_many</c> counterpart of <see cref="JudgeServerSide"/> — the fixture the
    /// review found missing, with no citation at all previously behind the statement's
    /// <c>{RelatedTypeName}Ids</c> half.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeServerSideManyToMany(RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                "register (many_to_many): the server rejects a misnamed foreign key at registration",
                caught is not null,
                caught is null ? "the server registered the descriptor" : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegForeignKeyNamingEnforced),
        };

        if (caught is not null)
        {
            assertions.Add(Assertion.From(
                "register (many_to_many): rejected with InvalidArgument",
                caught.StatusCode == StatusCode.InvalidArgument,
                $"actual={caught.StatusCode}"));

            var message = caught.Status.Detail;
            assertions.Add(Assertion.From(
                $"register (many_to_many): the error names the actual, misnamed foreign key ('{ServerSideManyToManyActualForeignKeyName}')",
                Normalize(message).Contains(Normalize(ServerSideManyToManyActualForeignKeyName)),
                $"error='{message}'"));
            assertions.Add(Assertion.From(
                $"register (many_to_many): the error names the required foreign-key name ('{ServerSideManyToManyRequiredForeignKeyName}')",
                Normalize(message).Contains(Normalize(ServerSideManyToManyRequiredForeignKeyName)),
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

    /// <summary>
    /// The <c>many_to_many</c> sibling of <see cref="RegisterMisnamedFixtureAsync"/>: a foreign
    /// key typed <c>Guid[]</c> (so it registers as <c>UUID[]</c>, satisfying the earlier typing
    /// check) but misnamed — <c>TagRefs</c> instead of the required <c>S2NamingTagIds</c>.
    /// </summary>
    private async Task RegisterMisnamedManyToManyFixtureAsync(Metadata headers, CancellationToken ct)
    {
        var descriptor = new TypeDescriptor
        {
            TypeName = ServerSideManyToManyTypeName,
            TenantField = "TenantId",
            Properties =
            {
                new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
                new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString, IsNullable = false },
                new PropertyDescriptor
                {
                    Name = ServerSideManyToManyActualForeignKeyName,
                    ClrType = ClrType.ClrGuid,
                    IsArray = true,
                    IsNullable = true,
                },
            },
            Relations =
            {
                new RelationDescriptor
                {
                    PropertyName = "Tags",
                    Kind = RelationKind.ManyToMany,
                    RelatedType = ServerSideManyToManyRelatedTypeName,
                    ForeignKey = ServerSideManyToManyActualForeignKeyName,
                },
            },
        };

        await mapping.RegisterSchemaAsync(
            new SchemaRequest { RootType = descriptor, TraceId = string.Empty },
            headers, cancellationToken: ct);
    }

    /// <summary>
    /// The client-side driver assertions, as a pure function over the driver's reported document —
    /// returns <c>null</c> when the driver reported no 'register' step at all, distinct from a
    /// harness-level failure that a report cell must render directly rather than merge with a
    /// server-side check's assertions.
    /// </summary>
    internal static IReadOnlyList<Assertion>? JudgeClientSideAssertions(PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == "register");
        if (step is null)
            return null;

        var message = step.Error ?? string.Empty;

        return new List<Assertion>
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
    }

    /// <summary>Builds the report cell for a language from its full assertion list — shared by the
    /// pure server-side path and the merged server+client path.</summary>
    internal static ReportCell BuildCell(string language, IReadOnlyList<Assertion> assertions)
    {
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
