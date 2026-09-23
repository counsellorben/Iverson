using Iverson.Api.Schema;
using Iverson.Api.Search;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;

// NAMESPACE NOTE: this namespace makes the simple name `Console` resolve to it, not to
// System.Console, for any file whose own namespace sits inside Iverson.Api (and for
// Iverson.Api.Tests, which nests under it by name-lookup rules). Nothing in either assembly
// uses System.Console today, and the failure mode if something ever does is a compile error
// with an obvious fix (`System.Console.WriteLine`), not silent misbehaviour. The folder/namespace
// correspondence every other folder in this project keeps (Schema/, Search/, Grpc/, Tenancy/)
// is worth more than avoiding that.
namespace Iverson.Api.Console;

/// <summary>
/// The admin console's four read-only JSON endpoints, under <c>/admin/console/</c>.
/// <para>
/// These are ordinary minimal-API endpoints on the same app as the gRPC services, reached by the
/// browser over the dedicated <c>admin-api</c> hostname. They return <b>projections</b> — what a
/// widget renders — not the descriptors or proto messages behind them.
/// </para>
/// <para>
/// <b>Two of the four are authenticated-only, deliberately, and must not be normalised to
/// <c>Operator</c>.</b> <c>ObjectMappingGrpcService.GetSchema</c> carries no <c>[Authorize]</c> and
/// <c>ObjectSearchGrpcService</c> is mapped without one, both because they filter per-row and
/// per-field internally rather than at the door. <c>/schema</c> and <c>/data-volume</c> project
/// exactly those two surfaces, so gating them on <c>Operator</c> would silently change who can see
/// what.
/// </para>
/// <para>
/// <b>Those two pass <see cref="HttpContext.User"/> explicitly.</b> The acting user is populated
/// for gRPC by <c>ActingUserInterceptor</c>, which never runs on this pipeline, and
/// <c>IRowFieldAuthorizationEvaluator</c> <b>denies</b> a null principal (every early return in
/// <c>RowFieldAuthorizationEvaluator</c> pairs <c>Denied = true</c>). Dropping the principal here
/// would therefore produce an <b>empty</b> view, not a widened one — a functionality failure, loud
/// in the UI, rather than a disclosure. It is still a bug, and
/// <c>AdminConsoleEndpointsPipelineTests</c> pins the principal actually reaching both readers.
/// </para>
/// </summary>
public static class AdminConsoleEndpoints
{
    public const string RoutePrefix = "/admin/console";

    /// <summary>
    /// Maps the four endpoints. Registered from <c>Program.cs</c> alongside the other
    /// <c>/admin</c> routes, and — like them — after <c>UseAuthentication</c>/<c>UseAuthorization</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAdminConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{RoutePrefix}/tenants", GetTenantsAsync)
            .WithName("AdminConsoleTenants")
            .RequireAuthorization("Operator");

        // RequireAuthorization() with no policy name applies the DefaultPolicy
        // (RequireAuthenticatedUser). Stated explicitly rather than relying on the
        // FallbackPolicy, so these two stay authenticated even if that fallback ever changes.
        //
        // NOT PINNED BY A TEST, deliberately, so nobody assumes otherwise. The anonymous-401
        // assertions in AdminConsoleEndpointsPipelineTests are satisfied by the FallbackPolicy
        // alone, which means deleting either RequireAuthorization() call below leaves the whole
        // suite green. That is not a security gap — the fallback denies by default, so the
        // endpoints stay closed either way — and pinning it would take endpoint-metadata
        // introspection tests that assert on registration rather than behaviour. These two calls
        // are defence-in-depth against a future change to that fallback, and nothing more.
        app.MapGet($"{RoutePrefix}/schema", GetSchema)
            .WithName("AdminConsoleSchema")
            .RequireAuthorization();

        app.MapGet($"{RoutePrefix}/data-volume", GetDataVolumeAsync)
            .WithName("AdminConsoleDataVolume")
            .RequireAuthorization();

        app.MapGet($"{RoutePrefix}/qdrant", GetQdrantAsync)
            .WithName("AdminConsoleQdrant")
            .RequireAuthorization("Operator");

        return app;
    }

    /// <summary>Tenant roster. Operator-gated: it is a cross-tenant enumeration.</summary>
    public static async Task<IResult> GetTenantsAsync(ITenantRepository tenants)
    {
        var rows = await tenants.ListAsync();
        var projected = rows
            .Select(t => new TenantSummary(t.Id, t.DisplayName, t.Status, t.CreatedAt))
            .ToList();
        return Results.Ok(new TenantsResponse(projected.Count, projected));
    }

    /// <summary>
    /// The schema catalog as this caller may see it: one entry per object type with its field
    /// count and its relation edges. Field <em>names</em> are deliberately not returned — the
    /// widget renders a count and a graph, and the full per-field descriptor already has a
    /// surface (<c>GetSchema</c>) with its own client contract.
    /// <para>
    /// <b>An empty catalog is reported as withheld, not as zero</b> — the same rule
    /// <see cref="GetDataVolumeAsync"/> follows. Without
    /// <see cref="SchemaCatalogResponse.WithheldTypeCount"/> a caller denied every type it can
    /// enumerate (an operator, having no <c>tenant_id</c> claim, is denied every unscoped type)
    /// would receive a body byte-identical to a deployment with none, and the widget would render
    /// "0 object types" when the truth is "you may see none of the N in your scope". Disclosing
    /// that count re-discloses nothing: <c>/data-volume</c> already reports the same figure to the
    /// same authenticated audience, and neither endpoint names them.
    /// </para>
    /// <para>
    /// <b>Another tenant's types are absent, not withheld</b>: every count here is taken over the
    /// types this caller's tenant may enumerate at all (<see cref="SchemaTenantScope"/>), so no
    /// foreign type moves any number in the response.
    /// </para>
    /// </summary>
    public static IResult GetSchema(HttpContext http, SchemaRegistry registry, SchemaCatalogReader reader)
    {
        // Snapshot the in-scope registry size BEFORE reading the catalog. registry.All is a live
        // ConcurrentDictionary that SchemaRefreshWorker can grow or shrink between the two reads;
        // taking the count first means a type registered mid-request can only ever make the
        // subtraction below smaller (down to the clamp), never invent a withheld type that was
        // never registered. Foreign types are excluded here exactly as the reader excludes them.
        var registeredTypeCount = registry.All.Values.Count(s => !s.IsForeignTo(http.User));

        // HttpContext.User, never null and never elided — see the class note.
        var catalog = reader.ReadCatalog(http.User);

        var types = catalog
            .Select(t => new SchemaTypeSummary(
                t.Name,
                t.Description,
                t.Fields.Count,
                t.Relations
                    .Select(r => new SchemaRelationEdge(
                        r.PropertyName, WireRelationKind(r.Kind), r.RelatedType, r.ForeignKey))
                    .ToList()))
            .ToList();

        // Math.Max, not a bare subtraction: the two reads above are not atomic, so a type
        // registered between them would otherwise yield a negative count.
        return Results.Ok(new SchemaCatalogResponse(
            types.Count, types, Math.Max(0, registeredTypeCount - types.Count)));
    }

    /// <summary>
    /// One row count per registered object type, with the caller's own row/field authorization
    /// applied.
    /// <para>
    /// <b>The three outcomes of <see cref="AggregateReader.CountRowsAsync"/> stay distinguishable
    /// on the wire, and a denied type is never reported as a count of zero.</b> Only a
    /// <c>Counted</c> type appears in <see cref="DataVolumeResponse.Types"/>, and every entry there
    /// carries a real number; denial and the unregistered-type race are reported as their own
    /// top-level counts. A console rendering "0 rows" where the truth is "you may not see this"
    /// would be worse than one rendering nothing, so the shape makes the false zero unconstructible
    /// rather than merely discouraged.
    /// </para>
    /// <para>
    /// <b>Denied types are counted, not named.</b> Naming them would tell any authenticated caller
    /// that a type it is denied exists — precisely what <c>GetSchema</c>'s pass-one filtering
    /// withholds, and what the <c>Aggregate</c> RPC withholds by omitting the result entirely. The
    /// aggregate count is what the no-access state needs ("you may see none of the N registered
    /// types") without re-disclosing the names that filtering removed.
    /// </para>
    /// <para>
    /// <b>Another tenant's types are absent, not denied</b>: they are dropped from the snapshot
    /// before any count is taken (<see cref="SchemaTenantScope"/>), so no foreign type moves
    /// <c>deniedTypeCount</c> or <c>unknownTypeCount</c>. Counting them would tell any
    /// authenticated caller how many types other tenants own.
    /// </para>
    /// <para>
    /// <b>An operator sees <c>types: []</c>, structurally and permanently — this is unrelated to
    /// whether any human is currently a member of the
    /// <c>operators</c> Authentik group (see <c>docs/runbooks/operator-access-onboarding.md</c>
    /// for that separate, unrelated onboarding step, which gates <c>/tenants</c> and
    /// <c>/qdrant</c> below, not this endpoint).</b> Operators are cross-tenant by design and so
    /// never carry a <c>tenant_id</c> claim, and the evaluator denies any principal without one:
    /// every unscoped type (no <c>OwnerTenantId</c>) lands in <c>deniedTypeCount</c>, and every
    /// tenant-owned type is absent under the rule above.
    /// This endpoint is not <c>Operator</c>-gated — any authenticated caller reaches it — so
    /// onboarding an operator does not change this outcome. That is pre-existing authorization
    /// semantics, not something this endpoint may paper over; the response says so explicitly
    /// instead of returning zeroes.
    /// </para>
    /// </summary>
    public static async Task<IResult> GetDataVolumeAsync(
        HttpContext http, SchemaRegistry registry, AggregateReader reader)
    {
        var counted = new List<TypeRowCountEntry>();
        var denied = 0;
        var unknown = 0;

        // Snapshot the in-scope type names first: registry.All is a live ConcurrentDictionary that
        // SchemaRefreshWorker can mutate mid-enumeration, which is also the only way
        // TypeRowCountStatus.UnknownType can be reached from here. Foreign types are filtered HERE,
        // not left to CountRowsAsync: it reports them as UnknownType, which would still count them.
        var typeNames = registry.All.Values
            .Where(s => !s.IsForeignTo(http.User))
            .Select(s => s.TypeName)
            .ToList();

        try
        {
            foreach (var typeName in typeNames)
            {
                // HttpContext.User, never null and never elided — see the class note.
                var result = await reader.CountRowsAsync(typeName, http.User);
                switch (result.Status)
                {
                    case TypeRowCountStatus.Counted:
                        counted.Add(new TypeRowCountEntry(typeName, "counted", result.Count));
                        break;
                    case TypeRowCountStatus.Denied:
                        denied++;
                        break;
                    case TypeRowCountStatus.UnknownType:
                        unknown++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(result), result.Status,
                            $"Unhandled {nameof(TypeRowCountStatus)} value — add a case above.");
                }
            }
        }
        // AggregateReader deliberately does not translate these two: it has no transport to
        // translate them into. THIS is that transport, so the translation happens here.
        // Both are whole-store conditions rather than per-type ones — StarRocks is unreachable,
        // or was never deployed — so one failed type means every remaining type would fail the
        // same way, and a partial body with the rest silently missing would read as real data.
        // Both answer 503: the store cannot serve this widget. `reason` separates the transient
        // case from the permanent one so the console can word it correctly.
        catch (EngagementNotReadyException ex)
        {
            return Results.Json(
                new EngagementUnavailableResponse("notReady", $"StarRocks is not ready: {ex.Message}"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (EngagementStoreDisabledException ex)
        {
            return Results.Json(
                new EngagementUnavailableResponse("disabled", ex.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new DataVolumeResponse(counted, denied, unknown));
    }

    /// <summary>
    /// Qdrant collection stats. Operator-gated: collection names are tenant-scoped, so
    /// enumerating them is a cross-tenant read.
    /// <para>
    /// <b>A collection whose stats could not be read is counted, not silently dropped</b> — the
    /// same rule <see cref="GetSchema"/> and <see cref="GetDataVolumeAsync"/> follow.
    /// <c>IntelligenceCollectionReader</c> contains a per-collection failure so one bad
    /// collection cannot blank the widget, which without
    /// <see cref="QdrantResponse.UnreadableCollectionCount"/> would show an operator "7
    /// collections" as a complete answer while Qdrant actually holds 10.
    /// <see cref="VectorCollectionListing.ListedCount"/> is the count Qdrant NAMED, so the
    /// subtraction below is the shortfall.
    /// </para>
    /// </summary>
    public static async Task<IResult> GetQdrantAsync(
        IVectorCollectionReader reader, CancellationToken ct)
    {
        var listing = await reader.ListCollectionStatsAsync(ct);
        var projected = listing.Collections
            .Select(c => new QdrantCollectionSummary(c.Name, c.PointsCount, c.IndexedVectorsCount))
            .ToList();
        // Math.Max for the same reason GetSchema uses it: the reader is the only thing that can
        // make these two counts consistent, and a clamp here means a future reader that reports
        // them wrong yields 0 rather than a negative count on the wire.
        return Results.Ok(new QdrantResponse(
            projected.Count, projected, Math.Max(0, listing.ListedCount - projected.Count)));
    }

    /// <summary>
    /// The wire spelling of a relation kind. Switched explicitly rather than calling
    /// <c>ToString()</c> on the generated proto enum, so a codegen change to those member names
    /// cannot silently rewrite this endpoint's JSON.
    /// </summary>
    private static string WireRelationKind(ContractsRelationKind kind) => kind switch
    {
        ContractsRelationKind.OneToOne   => "OneToOne",
        ContractsRelationKind.OneToMany  => "OneToMany",
        ContractsRelationKind.ManyToOne  => "ManyToOne",
        ContractsRelationKind.ManyToMany => "ManyToMany",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            $"Unhandled {nameof(ContractsRelationKind)} value — add a case above.")
    };
}

// ── Response contracts ─────────────────────────────────────────────────────────
// Plain records, serialized by the minimal-API pipeline's web defaults (camelCase). They exist
// so the wire shape is declared in one place and reviewable, rather than being an anonymous
// object inline in a lambda.

public sealed record TenantSummary(string Id, string DisplayName, string Status, DateTime CreatedAt);

public sealed record TenantsResponse(int Count, IReadOnlyList<TenantSummary> Tenants);

public sealed record SchemaRelationEdge(string PropertyName, string Kind, string RelatedType, string ForeignKey);

public sealed record SchemaTypeSummary(
    string Name, string Description, int FieldCount, IReadOnlyList<SchemaRelationEdge> Relations);

/// <summary>
/// The catalog projection, plus the count of registered types this caller did not get.
/// <para>
/// <b><see cref="WithheldTypeCount"/> is "withheld", not "denied", and the name is the contract.</b>
/// <c>SchemaCatalogReader</c> drops an in-scope type on two grounds — row-level denial, and an empty
/// authorized field set after <c>FieldPermission</c> filtering (<c>SchemaCatalogReader.cs</c>, the
/// <c>fields.Count == 0</c> guard) — and this figure merges them, because it is derived from the
/// size of the registry subset this caller's tenant may enumerate rather than from the reader's own
/// reasons. Another tenant's types are outside that subset, so they are absent, not withheld. Both
/// grounds genuinely mean "withheld from you", so the merge is honest; calling it
/// <c>deniedTypeCount</c> would not be, since it would claim a precision the derivation does not
/// have. Contrast <see cref="DataVolumeResponse.DeniedTypeCount"/>, which is incremented from an
/// actual <c>TypeRowCountStatus.Denied</c> and therefore may make that stronger claim.
/// </para>
/// <para>
/// Withheld types are counted, never named — the same rule <see cref="DataVolumeResponse"/>
/// follows, and the whole point of the reader's filtering.
/// </para>
/// </summary>
public sealed record SchemaCatalogResponse(
    int TypeCount, IReadOnlyList<SchemaTypeSummary> Types, int WithheldTypeCount);

/// <summary>
/// One type's row count. <c>Status</c> is always <c>"counted"</c> as this endpoint is written —
/// it is carried anyway so the discriminator is explicit on the wire and a future outcome cannot
/// be added by widening the meaning of a bare number.
/// </summary>
public sealed record TypeRowCountEntry(string TypeName, string Status, long RowCount);

public sealed record DataVolumeResponse(
    IReadOnlyList<TypeRowCountEntry> Types, int DeniedTypeCount, int UnknownTypeCount);

/// <summary>Why the engagement store could not answer. <c>Reason</c> is <c>notReady</c> or <c>disabled</c>.</summary>
public sealed record EngagementUnavailableResponse(string Reason, string Error);

public sealed record QdrantCollectionSummary(string Name, ulong? PointsCount, ulong? IndexedVectorsCount);

/// <summary>
/// The collections read, plus the count Qdrant listed but whose stats could not be read.
/// <para>
/// <b><see cref="CollectionCount"/> is the length of <see cref="Collections"/>, not the size of
/// the deployment.</b> The two differ whenever <see cref="UnreadableCollectionCount"/> is
/// non-zero, and that field is the whole reason a short list is not the same claim as a complete
/// one — exactly as <see cref="DataVolumeResponse.DeniedTypeCount"/> is for row counts. A
/// collection is dropped from the list on one ground only (its <c>GetCollectionInfoAsync</c>
/// failed — dropped mid-enumeration, or a Qdrant fault on that collection), so unlike
/// <see cref="SchemaCatalogResponse.WithheldTypeCount"/> this figure merges nothing and is not an
/// authorization statement: the endpoint is <c>Operator</c>-gated, and a caller who gets this far
/// is entitled to every collection.
/// </para>
/// </summary>
public sealed record QdrantResponse(
    int CollectionCount,
    IReadOnlyList<QdrantCollectionSummary> Collections,
    int UnreadableCollectionCount);
