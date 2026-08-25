using System.Security.Claims;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.StarRocks;
using EngagementAggResult = Iverson.StarRocks.AggregationResult;
using EngagementAggSpec = Iverson.StarRocks.AggregationDescriptor;

namespace Iverson.Api.Search;

/// <summary>
/// Runs StarRocks aggregations against a registered object type, with the caller's row/field
/// authorization applied.
/// <para>
/// The acting user is a <b>parameter</b>, never ambient state — the same contract as
/// <see cref="Iverson.Api.Schema.SchemaCatalogReader"/>, and for the same reason:
/// <see cref="IRowFieldAuthorizationEvaluator"/> <b>denies</b> a <c>null</c> principal — every early
/// return in <c>RowFieldAuthorizationEvaluator</c> pairs <c>Denied = true</c>, pinned by
/// <c>Evaluate_NoIdentity_ReturnsDenied</c>. Passing <c>null</c> therefore yields
/// <see cref="TypeRowCount.Denied"/> for every type rather than an unscoped count. The principal is
/// explicit so a dropped one fails visibly here instead of being masked by ambient accessor state.
/// </para>
/// <para>
/// Two entry points over one call. <see cref="CountRowsAsync"/> is the injectable form used by the
/// admin-console endpoint: it resolves the schema, evaluates authorization itself, and reports
/// denial <em>distinctly</em> — something the <c>Aggregate</c> RPC cannot do, because returning an
/// empty result there is what keeps a denied caller from learning the type exists.
/// <see cref="RunAsync"/> is the shared seam as a static, called by <c>ObjectSearchGrpcService</c>
/// with the <see cref="SchemaRegistry"/> and search service it already holds — so that service's
/// constructor, its own authorization evaluation across request-level joins, and its exception
/// translation are all unchanged.
/// </para>
/// </summary>
public sealed class AggregateReader(
    IEngagementStoreSearchService search,
    SchemaRegistry registry,
    IRowFieldAuthorizationEvaluator authEvaluator)
{
    /// <summary>The aggregation name carried on the COUNT spec and its result.</summary>
    private const string CountAggregationName = "count";

    /// <summary>
    /// Row count for <paramref name="typeName"/> as <paramref name="actingUser"/> may see it.
    /// <para>
    /// Callers must handle the StarRocks-availability exceptions this can surface from the store —
    /// <c>EngagementNotReadyException</c> and <c>EngagementStoreDisabledException</c> — which are
    /// deliberately not translated here: this reader has no transport to translate them into.
    /// </para>
    /// </summary>
    public async Task<TypeRowCount> CountRowsAsync(string typeName, ClaimsPrincipal? actingUser)
    {
        var schema = registry.Get(typeName);
        if (schema is null)
            return TypeRowCount.UnknownType;

        var decision = authEvaluator.Evaluate(schema, actingUser, AuthorizationAction.Read);
        if (decision.Denied)
            return TypeRowCount.Denied;

        // Same shape ObjectSearchGrpcService.EvaluateAuthorization builds for the primary type:
        // OrdinalIgnoreCase keyed, because the store looks the constraint up by type name.
        // There are no joins on this path, so the primary type is the only entry.
        var constraints = new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.TypeName] = new AuthorizationConstraint(
                decision.AllowedFields, decision.OwnerFieldName, decision.OwnerValue,
                decision.TenantColumn, decision.TenantValue)
        };

        // Neither Field nor Expression set — StarRocksQueryBuilder emits COUNT(*) for that
        // combination, with the ownership and tenant predicates appended from the constraints.
        var spec = new EngagementAggSpec(
            Name:  CountAggregationName,
            Kind:  AggregationKind.Count,
            Field: string.Empty);

        var result = await RunAsync(
            search, registry, schema, query: null, spec, having: null, joins: null, authz: constraints);

        // A null result means the store had no tenant database to address. EngagementRepository
        // returns null both for an absent or not-yet-provisioned tenant AND when the tenant id is
        // null or fails TenantIdentifier.IsValid — the evaluator checks the tenant_id claim is
        // non-empty but never checks it is well-formed. Both render as zero rows: provisioning uses
        // the same identifier, so a tenant whose id cannot address a database genuinely has none.
        // Note this DIVERGES deliberately from the Aggregate RPC, which omits the result entirely
        // for the same null; a count widget needs a number, and zero is the honest one.
        return TypeRowCount.Counted((long)(result?.MetricValue ?? 0d));
    }

    /// <summary>
    /// Executes one aggregation against <paramref name="schema"/>. Authorization must already have
    /// been evaluated by the caller and passed in as <paramref name="authz"/>.
    /// </summary>
    internal static Task<EngagementAggResult?> RunAsync(
        IEngagementStoreSearchService search,
        SchemaRegistry registry,
        SchemaDescriptor schema,
        SearchQuery? query,
        EngagementAggSpec spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null) =>
        search.AggregateAsync(
            SchemaBuilder.ToEngagementQuerySchema(schema),
            query,
            spec,
            having,
            joins,
            t => registry.Get(t) is { } d ? SchemaBuilder.ToEngagementQuerySchema(d) : null,
            authz);
}

/// <summary>Outcome of <see cref="AggregateReader.CountRowsAsync"/>.</summary>
public enum TypeRowCountStatus
{
    /// <summary><see cref="TypeRowCount.Count"/> holds the row count.</summary>
    Counted,
    /// <summary>The caller is denied read access to the type.</summary>
    Denied,
    /// <summary>No schema is registered under that type name.</summary>
    UnknownType
}

/// <summary>Row count for one object type, or why there isn't one.</summary>
public sealed record TypeRowCount(TypeRowCountStatus Status, long Count)
{
    public static TypeRowCount Counted(long count) => new(TypeRowCountStatus.Counted, count);
    public static TypeRowCount Denied { get; } = new(TypeRowCountStatus.Denied, 0);
    public static TypeRowCount UnknownType { get; } = new(TypeRowCountStatus.UnknownType, 0);
}
