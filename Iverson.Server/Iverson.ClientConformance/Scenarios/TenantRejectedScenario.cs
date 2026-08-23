using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S10 <c>tenant-rejected</c>: orchestrator-only, no driver. The server owns the tenant boundary
/// outright — <c>SchemaBuilder</c> injects the <c>__TenantId</c> column into every descriptor and
/// the acting user's <c>tenant_id</c> claim supplies its value — and it enforces two registration
/// rules that follow from that ownership:
/// <list type="bullet">
/// <item><description><c>IVC-REG-004</c>: a descriptor that DECLARES a tenant field (proto field 5,
/// still on the wire for compatibility) is rejected rather than ignored.</description></item>
/// <item><description><c>IVC-REG-005</c>: a descriptor that NAMES <c>__TenantId</c> in any of the
/// six name-bearing positions on <c>TypeDescriptor</c> is rejected.</description></item>
/// </list>
///
/// <para><b>Why no driver runs here, and why that is not a hole.</b> No conformance driver can
/// produce either request. Four clients omit <c>tenant_field</c> entirely and TypeScript sends the
/// proto default <c>""</c> (ts-proto types it as required), which the guard treats as absent; and
/// no client declaration style can name a member <c>__TenantId</c> — .NET and Java derive names
/// from CLR members, and Go/Python/TypeScript would fail their own identifier checks first. A
/// requirement graded through the driver channel would therefore be unfalsifiable BY CONSTRUCTION,
/// which is the precise defect the coverage gate exists to prevent. So this scenario hand-builds
/// raw <c>TypeDescriptor</c>s in orchestrator-side C# and posts them to <c>RegisterSchema</c>
/// directly — the mechanism <see cref="NamingRejectedScenario"/> and
/// <see cref="NavPropertyRejectedScenario"/> already use — grading the SERVER's rule without
/// needing any client to be able to express the violation. That is the right channel rather than a
/// compromise: both rules exist to defend against STALE CLIENT BUILDS and hand-rolled callers.
///
/// <para><b>One real observation, rendered once.</b> As in
/// <see cref="NavPropertyRejectedScenario"/>, no client library is involved at all, so only one
/// language (<see cref="CanonicalLanguage"/>, chosen deterministically) carries the real
/// Ok/Fail outcome; every other requested language renders as a Skip naming that column, rather
/// than the matrix reading as five independent verifications of one server call.</para>
///
/// <para><b>Each fixture must be rejected for its OWN reason.</b> Every arm asserts the message
/// text the guard itself produces, never merely <c>InvalidArgument</c>. A bare "some rejection
/// happened" would be satisfied by <c>ValidateIdentifier</c> (which rejects a leading underscore
/// with a generic message), by the FK-naming rule, or by the collision check — all of which would
/// reject these same fixtures for entirely the wrong reason if the tenant guards ever regressed.
/// The reserved-name arms additionally assert the guard's own SITE LABEL ("Property", "Key
/// property", "Relation foreign key", "Relation navigation property", "Owner field", "Field
/// permission"), so an arm rejected by a DIFFERENT site's check reddens instead of passing.</para>
/// </summary>
public sealed class TenantRejectedScenario(
    ObjectMappingService.ObjectMappingServiceClient mapping)
{
    public const string Name = "tenant-rejected";

    /// <summary>
    /// The reserved server-owned column name. Aliased from
    /// <see cref="PostgresProbe.ServerOwnedTenantColumn"/>, which is the harness's single copy of
    /// the server's constant and carries the cross-task contract that goes with it — this scenario
    /// must not spell the name a second time, or a change to the server's reserved name would fix
    /// one of the two and leave the other silently grading a name nothing reserves.
    /// </summary>
    internal const string ReservedTenantColumnName = PostgresProbe.ServerOwnedTenantColumn;

    /// <summary>The tenant field a stale client build would still declare — an ordinary,
    /// perfectly legal property name, so the ONLY thing wrong with this descriptor is that
    /// <c>tenant_field</c> is populated at all.</summary>
    private const string DeclaredTenantFieldName = "TenantId";

    private const string DeclaredTenantFieldTypeName = "S10TenantDeclared";

    /// <summary>
    /// One fixture per addressing site in <c>RejectReservedTenantName</c>'s closed enumeration.
    /// <paramref name="SiteLabel"/> is the label the guard puts in its own message, and asserting
    /// it is what keeps the six arms independent: without it, an arm whose guard was deleted would
    /// still be rejected by whichever LATER arm the fixture happens to also trip, and pass.
    /// </summary>
    internal sealed record ReservedNameFixture(string Site, string TypeName, string SiteLabel);

    internal static readonly IReadOnlyList<ReservedNameFixture> ReservedNameFixtures =
    [
        new("scalar property", "S10TenantProperty", "Property"),
        new("key property", "S10TenantKey", "Key property"),
        new("relation foreign key", "S10TenantForeignKey", "Relation foreign key"),
        new("relation navigation property", "S10TenantNavProperty", "Relation navigation property"),
        new("authorization.owner_field", "S10TenantOwnerField", "Owner field"),
        new("authorization.field_permissions[].field_name", "S10TenantFieldPermission", "Field permission"),
    ];

    private static readonly IReadOnlyList<string> LanguagePriority =
        ["dotnet", "go", "java", "python", "typescript"];

    /// <summary>
    /// Picks the one language, among those requested, that carries this scenario's single
    /// server-side observation — mirrors <see cref="NavPropertyRejectedScenario.CanonicalLanguage"/>
    /// exactly, including its empty-collection behaviour, so a partial <c>--languages</c> run never
    /// silently drops IVC-REG-004/005.
    /// </summary>
    internal static string CanonicalLanguage(IReadOnlyCollection<string> languages) =>
        LanguagePriority.FirstOrDefault(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
        ?? languages.FirstOrDefault()
        ?? string.Empty;

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        string actingToken,
        CancellationToken ct = default)
    {
        if (languages.Count == 0)
            return [];

        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        var canonical = CanonicalLanguage(languages);

        ReportCell canonicalCell;
        try
        {
            var declared = await TryRegisterAsync(() => RegisterDeclaredTenantFieldFixtureAsync(headers, ct));

            var reserved = new List<(ReservedNameFixture Fixture, RpcException? Caught)>();
            foreach (var fixture in ReservedNameFixtures)
                reserved.Add((fixture, await TryRegisterAsync(() => RegisterReservedNameFixtureAsync(fixture, headers, ct))));

            var assertions = JudgeDeclaredTenantField(declared).Concat(JudgeReservedNames(reserved)).ToList();
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

        return languages
            .Select(l => string.Equals(l, canonical, StringComparison.OrdinalIgnoreCase)
                ? canonicalCell
                : ReportCell.Skip(l, Name,
                    "tenant-rejected is a set of orchestrator-side gRPC registration checks — no " +
                    "client library can express either violation — so it runs exactly once rather " +
                    $"than once per language; see the '{canonical}' column for the real result"))
            .ToList();
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
    /// <c>IVC-REG-004</c>, as a pure function over what <c>RegisterSchema</c> produced — so every
    /// branch is exercisable without a live stack.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeDeclaredTenantField(RpcException? caught)
    {
        var assertions = new List<Assertion>
        {
            Assertion.From(
                "register: the server rejects a descriptor that declares a tenant field",
                caught is not null,
                caught is null
                    ? "the server registered a descriptor declaring tenant_field, so the declaration was silently ignored"
                    : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegDeclaredTenantFieldRejected),
        };

        if (caught is null)
            return assertions;

        assertions.Add(Assertion.From(
            "register (tenant_field): rejected with InvalidArgument",
            caught.StatusCode == StatusCode.InvalidArgument,
            $"actual={caught.StatusCode}",
            Requirements.ErrRegistrationRejectionIsInvalidArgument));

        var message = caught.Status.Detail;
        // Both halves matter and are asserted separately. "tenant_field" alone would also be
        // produced by a hypothetical guard that rejected every registration; naming the value the
        // caller actually declared is what makes the message actionable, and it is the half no
        // other guard on this path could produce.
        assertions.Add(Assertion.From(
            "register (tenant_field): the error names tenant_field as the rejected declaration",
            message.Contains("tenant_field", StringComparison.Ordinal),
            $"error='{message}'",
            Requirements.ErrMessageNamesOffendingElement));
        assertions.Add(Assertion.From(
            $"register (tenant_field): the error names the declared value ('{DeclaredTenantFieldName}')",
            message.Contains($"'{DeclaredTenantFieldName}'", StringComparison.Ordinal),
            $"error='{message}'",
            Requirements.ErrMessageNamesOffendingElement));

        return assertions;
    }

    /// <summary>
    /// <c>IVC-REG-005</c>, one assertion set per addressing site, as a pure function over what
    /// <c>RegisterSchema</c> produced for each fixture.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeReservedNames(
        IReadOnlyList<(ReservedNameFixture Fixture, RpcException? Caught)> results)
    {
        var assertions = new List<Assertion>();

        foreach (var (fixture, caught) in results)
        {
            assertions.Add(Assertion.From(
                $"register: the server rejects the reserved tenant column name at registration ({fixture.Site})",
                caught is not null,
                caught is null
                    ? $"the server registered a descriptor naming '{ReservedTenantColumnName}' as its {fixture.Site}"
                    : $"{caught.StatusCode}: {caught.Status.Detail}",
                Requirements.RegReservedTenantColumnNameRejected));

            if (caught is null)
                continue;

            assertions.Add(Assertion.From(
                $"register (reserved name, {fixture.Site}): rejected with InvalidArgument",
                caught.StatusCode == StatusCode.InvalidArgument,
                $"actual={caught.StatusCode}",
                Requirements.ErrRegistrationRejectionIsInvalidArgument));

            var message = caught.Status.Detail;
            // The SITE LABEL is what makes this arm independent of the other five: the guard runs
            // all six checks in a fixed order, so a fixture whose own arm was deleted would still
            // be rejected by a later arm it also happens to trip, and a label-blind assertion would
            // pass. "reserved server-owned column name" is asserted alongside it so that a generic
            // ValidateIdentifier rejection (which fires on the same leading underscore, with a
            // message that never mentions the reservation) cannot satisfy this either.
            assertions.Add(Assertion.From(
                $"register (reserved name, {fixture.Site}): the error names the reserved column and " +
                $"identifies the site as '{fixture.SiteLabel}'",
                message.Contains($"{fixture.SiteLabel} '{ReservedTenantColumnName}'", StringComparison.Ordinal) &&
                message.Contains("reserved server-owned column name", StringComparison.Ordinal),
                $"error='{message}'",
                Requirements.ErrMessageNamesOffendingElement));
        }

        return assertions;
    }

    /// <summary>
    /// A descriptor whose ONLY defect is a populated <c>tenant_field</c>: every name on it is a
    /// legal identifier and none is reserved, so nothing else in the registration path can reject
    /// it. Deliberately relation-free — a relation would give the naming and collision checks
    /// something to reject first.
    /// </summary>
    private async Task RegisterDeclaredTenantFieldFixtureAsync(Metadata headers, CancellationToken ct)
    {
        var descriptor = new TypeDescriptor
        {
            TypeName = DeclaredTenantFieldTypeName,
            TenantField = DeclaredTenantFieldName,
            Properties =
            {
                new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
                new PropertyDescriptor { Name = DeclaredTenantFieldName, ClrType = ClrType.ClrString, IsNullable = false },
            },
        };

        await mapping.RegisterSchemaAsync(
            new SchemaRequest { RootType = descriptor, TraceId = string.Empty },
            headers, cancellationToken: ct);
    }

    /// <summary>
    /// A descriptor addressing <see cref="ReservedTenantColumnName"/> from exactly ONE site, with
    /// every other name legal — so the arm under test is the only guard that can fire. None of
    /// these fixtures populates <c>tenant_field</c>: <c>RejectDeclaredTenantField</c> runs
    /// immediately BEFORE <c>RejectReservedTenantName</c>, so a populated tenant field would reject
    /// every one of them before the arm under test ever ran, and all six would pass while grading
    /// nothing.
    /// </summary>
    private async Task RegisterReservedNameFixtureAsync(
        ReservedNameFixture fixture, Metadata headers, CancellationToken ct)
    {
        var descriptor = new TypeDescriptor { TypeName = fixture.TypeName };

        switch (fixture.Site)
        {
            case "scalar property":
                descriptor.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
                descriptor.Properties.Add(new PropertyDescriptor
                {
                    Name = ReservedTenantColumnName, ClrType = ClrType.ClrString, IsNullable = false,
                });
                break;

            case "key property":
                descriptor.Properties.Add(new PropertyDescriptor
                {
                    Name = ReservedTenantColumnName, ClrType = ClrType.ClrGuid, IsKey = true,
                });
                break;

            case "relation foreign key":
                descriptor.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
                descriptor.Relations.Add(new RelationDescriptor
                {
                    PropertyName = "Owner",
                    Kind = RelationKind.ManyToOne,
                    RelatedType = "S10TenantOwner",
                    ForeignKey = ReservedTenantColumnName,
                });
                break;

            case "relation navigation property":
                descriptor.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
                // The foreign key is the correctly-derived "{RelatedType}Id" and IS a declared
                // property, so the naming check and the FK-is-declared check both pass and the nav
                // property's own arm is the only thing left that can reject this.
                descriptor.Properties.Add(new PropertyDescriptor
                {
                    Name = "S10TenantNavOwnerId", ClrType = ClrType.ClrGuid, IsNullable = true,
                });
                descriptor.Relations.Add(new RelationDescriptor
                {
                    PropertyName = ReservedTenantColumnName,
                    Kind = RelationKind.ManyToOne,
                    RelatedType = "S10TenantNavOwner",
                    ForeignKey = "S10TenantNavOwnerId",
                });
                break;

            case "authorization.owner_field":
                descriptor.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
                descriptor.Authorization = new AuthorizationRules { OwnerField = ReservedTenantColumnName };
                break;

            case "authorization.field_permissions[].field_name":
                descriptor.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
                descriptor.Authorization = new AuthorizationRules
                {
                    FieldPermissions =
                    {
                        new FieldPermission { FieldName = ReservedTenantColumnName },
                    },
                };
                break;

            default:
                throw new InvalidOperationException(
                    $"'{fixture.Site}' is not a reserved-name addressing site this scenario knows how to build. " +
                    "A fixture added to ReservedNameFixtures must be built here too, or its arm silently " +
                    "grades nothing.");
        }

        await mapping.RegisterSchemaAsync(
            new SchemaRequest { RootType = descriptor, TraceId = string.Empty },
            headers, cancellationToken: ct);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
