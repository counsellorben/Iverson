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
    /// </summary>
    public static IResult GetSchema(HttpContext http, SchemaCatalogReader reader)
    {
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

        return Results.Ok(new SchemaCatalogResponse(types.Count, types));
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
    /// <b>An operator sees <c>types: []</c> with a non-zero <c>deniedTypeCount</c> today.</b> The
    /// evaluator denies any principal with no <c>tenant_id</c> claim, and an operator carries none.
    /// That is pre-existing authorization semantics, not something this endpoint may paper over;
    /// the response says so explicitly instead of returning zeroes.
    /// </para>
    /// </summary>
    public static async Task<IResult> GetDataVolumeAsync(
        HttpContext http, SchemaRegistry registry, AggregateReader reader)
    {
        var counted = new List<TypeRowCountEntry>();
        var denied = 0;
        var unknown = 0;

        // Snapshot the type names first: registry.All is a live ConcurrentDictionary that
        // SchemaRefreshWorker can mutate mid-enumeration, which is also the only way
        // TypeRowCountStatus.UnknownType can be reached from here.
        var typeNames = registry.All.Values.Select(s => s.TypeName).ToList();

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
    /// </summary>
    public static async Task<IResult> GetQdrantAsync(
        IVectorCollectionReader reader, CancellationToken ct)
    {
        var stats = await reader.ListCollectionStatsAsync(ct);
        var projected = stats
            .Select(c => new QdrantCollectionSummary(c.Name, c.PointsCount, c.IndexedVectorsCount))
            .ToList();
        return Results.Ok(new QdrantResponse(projected.Count, projected));
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

public sealed record SchemaCatalogResponse(int TypeCount, IReadOnlyList<SchemaTypeSummary> Types);

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

public sealed record QdrantResponse(int CollectionCount, IReadOnlyList<QdrantCollectionSummary> Collections);
