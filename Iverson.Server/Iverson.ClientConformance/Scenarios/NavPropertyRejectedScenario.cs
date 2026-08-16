using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S3 <c>nav-property-rejected</c>: orchestrator-only, no driver. None of the five client
/// libraries can produce a payload keyed by a navigation property any more — the FK-only write
/// contract work made the wire member AND the foreign key the same name everywhere a client
/// writes one — so there is nothing for a driver to report here. Instead the orchestrator itself
/// hand-builds a <c>Struct</c> carrying a navigation-property key (<c>Author</c>) rather than the
/// foreign key (<c>AuthorId</c>) and posts it as a raw <c>MappingWriteRequest</c>, asserting that
/// <c>RelationValidator.ValidateAndNormalizeRelations</c>
/// (<c>Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs</c>) rejects it with
/// <c>InvalidArgument</c>, naming both the property and the foreign key.
///
/// The scenario registers its own single-type fixture, with an authorization block set from the
/// start, through this same <c>ObjectMappingServiceClient</c> — self-contained and runnable
/// alone rather than depending on a type an earlier scenario happened to register. Without the
/// authorization block, <c>Post</c> would deny the write before relation validation ever runs
/// (<c>ObjectMappingGrpcService.Post</c> calls <c>EnforceWriteAuthorization</c> first), so the
/// PermissionDenied that produces would name nothing about the real precondition this scenario
/// exists to check.
///
/// There is exactly ONE server-side observation here — no client library is involved at all — so
/// it must never be rendered as five independent per-language verifications the way S1's genuinely
/// independent driver runs are. Only one language (<see cref="CanonicalLanguage"/>, chosen
/// deterministically so the same column always carries it) gets the real
/// <see cref="CellStatus.Ok"/>/<see cref="CellStatus.Fail"/> outcome; every other requested
/// language renders as <see cref="CellStatus.Skip"/> with a reason naming the canonical column,
/// reusing S2's existing "this language did not run, and here is why" mechanism rather than
/// inventing a new report concept for one scenario.
/// </summary>
public sealed class NavPropertyRejectedScenario(
    ObjectMappingService.ObjectMappingServiceClient mapping)
{
    public const string Name = "nav-property-rejected";

    private const string TypeName = "S3NavArticle";
    private const string NavPropertyName = "Author";
    // Must be "{RelatedTypeName}Id" — Task 4's registration-time naming check
    // (SchemaRegistrationOrchestrator.cs) now rejects a ManyToOne foreign key named anything
    // else, and this scenario registers its own fixture through that same check. A plain
    // "AuthorId" here fails registration before the scenario ever reaches the payload it exists
    // to test, silently leaving IVC-REL-005 untouched.
    private const string ForeignKeyName = "S3NavAuthorId";
    private const string RelatedTypeName = "S3NavAuthor";

    /// <summary>
    /// Fixed priority order for picking which single requested language carries this scenario's
    /// one real outcome — independent of the order <c>--languages</c> happened to list them in,
    /// so re-running with the same language set always lands the result in the same column.
    /// </summary>
    private static readonly IReadOnlyList<string> LanguagePriority =
        ["dotnet", "go", "java", "python", "typescript"];

    /// <summary>
    /// Picks the one language, among those requested, that carries this scenario's single
    /// server-side observation. Falls back to whichever language happens to be first when none
    /// of the requested set matches the fixed priority list (e.g. a caller passing an unrecognized
    /// name) — deterministic within a single run either way, since it always resolves the same
    /// requested collection the same way. An empty <paramref name="languages"/> (e.g.
    /// <c>--languages ""</c>, which <c>CliFlags.Parse</c> turns into an empty, non-null list)
    /// resolves to <c>""</c> rather than throwing — <see cref="RunAsync"/>'s own
    /// <c>languages.Select(...)</c> then produces zero cells for it, same as every other scenario
    /// does for an empty language set, instead of the whole harness run crashing.
    /// </summary>
    internal static string CanonicalLanguage(IReadOnlyCollection<string> languages) =>
        LanguagePriority.FirstOrDefault(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
        ?? languages.FirstOrDefault()
        ?? string.Empty;

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        DriverContext context,
        string actingToken,
        CancellationToken ct = default)
    {
        // No language requested — no cell to produce, and (unlike a non-empty request whose
        // canonical column still has to make the one real check) nothing worth spending a
        // gRPC round trip on either.
        if (languages.Count == 0)
            return [];

        // The same headers the drivers use: the service bearer already rides the channel's own
        // CallCredentials (see Program.cs), and the acting-user identity goes per-call — both are
        // required for the request to reach relation validation rather than stopping at the
        // authorization gate (schema_admin for RegisterSchema; the fixture's own row permissions
        // for Post).
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        var canonical = CanonicalLanguage(languages);

        ReportCell canonicalCell;
        try
        {
            await RegisterFixtureAsync(headers, ct);

            var payload = BuildIllegalPayload(context);

            RpcException? caught = null;
            try
            {
                await mapping.PostAsync(
                    new MappingWriteRequest { TypeName = TypeName, Payload = payload },
                    headers, cancellationToken: ct);
            }
            catch (RpcException ex)
            {
                caught = ex;
            }

            var assertions = Judge(caught);
            var failures = assertions.Where(a => !a.Passed).ToList();
            canonicalCell = failures.Count == 0
                ? ReportCell.Ok(canonical, Name, assertions)
                : ReportCell.Fail(canonical, Name, string.Join(
                    Environment.NewLine + "    ",
                    failures.Select(f => $"{f.Name} — {f.Detail}")), assertions);
        }
        catch (Exception ex)
        {
            canonicalCell = ReportCell.Fail(canonical, Name, $"fixture registration failed: {Describe(ex)}", []);
        }

        // Every other requested language did not run anything for this scenario at all — reusing
        // S2's skip-with-reason mechanism rather than a copy of the canonical outcome is what
        // keeps the matrix from reading as five independent verifications of one server call.
        return languages
            .Select(l => string.Equals(l, canonical, StringComparison.OrdinalIgnoreCase)
                ? canonicalCell
                : ReportCell.Skip(l, Name,
                    "nav-property-rejected is a single orchestrator-side gRPC check — no client " +
                    "library is involved — so it runs exactly once rather than once per language; " +
                    $"see the '{canonical}' column for the real result"))
            .ToList();
    }

    /// <summary>
    /// The assertions themselves, as a pure function over what the <c>Post</c> call produced —
    /// unit-testable without a live stack, mirroring <c>Verifier</c>'s split between gathering
    /// observations and judging them.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                "post: the server rejects a navigation-property key rather than persisting it",
                caught is not null,
                caught is null ? "the write succeeded" : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RelWritePayloadForeignKeyOnly),
        };

        if (caught is not null)
        {
            // Asserted alongside the message text, not instead of it: a regression in either
            // precondition (the wrong status code, or the right code with the wrong reason) is
            // visible as itself rather than as a single conflated failure.
            assertions.Add(Assertion.From(
                "post: rejected with InvalidArgument",
                caught.StatusCode == StatusCode.InvalidArgument,
                $"actual={caught.StatusCode}"));

            var message = caught.Status.Detail;
            // NavPropertyName ("Author") is a substring of ForeignKeyName ("S3NavAuthorId"), so a
            // bare case-insensitive Contains(NavPropertyName) is satisfied by a message that names
            // only the foreign key — the two assertions below would then no longer be independent
            // observations, and a server regression that dropped the nav-property mention entirely
            // would still show both as green. RelationValidator.cs quotes the nav property in
            // single quotes exactly as `'{relation.PropertyName}'` (see
            // "Relation '{relation.PropertyName}' is a navigation property and cannot be written"),
            // and that quoted form is never a substring of the unquoted foreign-key mention, so
            // matching it makes this assertion falsifiable on its own.
            assertions.Add(Assertion.From(
                $"post: the error names the navigation property ('{NavPropertyName}')",
                message.Contains($"'{NavPropertyName}'", StringComparison.Ordinal),
                $"error='{message}'"));
            assertions.Add(Assertion.From(
                $"post: the error names the required foreign key ('{ForeignKeyName}')",
                message.Contains(ForeignKeyName, StringComparison.OrdinalIgnoreCase),
                $"error='{message}'"));
        }

        return assertions;
    }

    private async Task RegisterFixtureAsync(Metadata headers, CancellationToken ct)
    {
        var descriptor = new TypeDescriptor
        {
            TypeName = TypeName,
            TenantField = "TenantId",
            Authorization = new AuthorizationRules
            {
                OwnerField = "OwnerId",
                RowPermissions =
                {
                    new RowPermission
                    {
                        Role = "iverson-loadtest-bypass",
                        CanReadAll = true,
                        CanWriteAll = true,
                        CanDeleteAll = true,
                    },
                },
            },
            Properties =
            {
                new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
                new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString, IsNullable = false },
                new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrString, IsNullable = true },
                new PropertyDescriptor { Name = ForeignKeyName, ClrType = ClrType.ClrGuid, IsNullable = true },
            },
            Relations =
            {
                new RelationDescriptor
                {
                    PropertyName = NavPropertyName,
                    Kind = RelationKind.ManyToOne,
                    RelatedType = RelatedTypeName,
                    ForeignKey = ForeignKeyName,
                },
            },
        };

        await mapping.RegisterSchemaAsync(
            new SchemaRequest { RootType = descriptor, TraceId = string.Empty },
            headers, cancellationToken: ct);
    }

    private static Struct BuildIllegalPayload(DriverContext context)
    {
        var payload = new Struct();
        payload.Fields["TenantId"] = Value.ForString(context.Tenant);
        payload.Fields["OwnerId"] = Value.ForString(context.OwnerId);
        // The illegal key itself: a navigation-property value where only the foreign key is ever
        // allowed. RelationValidator's navIsDistinctKey gate trips on any non-null value under
        // the nav property's name — a bare GUID string is enough; nothing downstream ever gets
        // far enough to care that it is not actually a nested related object.
        payload.Fields[NavPropertyName] = Value.ForString(Guid.NewGuid().ToString());
        return payload;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
