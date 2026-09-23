using System.Security.Claims;
using Iverson.Api.Authorization;
using Iverson.Client.Contracts;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind    = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Schema;

/// <summary>
/// Builds the authorization-filtered schema catalog: the set of object types, fields and relations
/// one caller may be told about.
/// <para>
/// The acting user is a <b>parameter</b>, never ambient state. <c>ObjectMappingGrpcService.GetSchema</c>
/// passes <c>IActingUserAccessor.ActingUser</c> (populated by the gRPC-only
/// <c>ActingUserInterceptor</c>); the admin-console JSON endpoint passes <c>HttpContext.User</c>.
/// This is load-bearing rather than stylistic: <see cref="IRowFieldAuthorizationEvaluator"/>
/// <b>denies</b> a <c>null</c> principal — every early return in <c>RowFieldAuthorizationEvaluator</c>
/// pairs <c>Denied = true</c>, pinned by <c>Evaluate_NoIdentity_ReturnsDenied</c>. So a caller that
/// forgets to supply one gets an <b>empty</b> catalog, not a widened one: every type is dropped in
/// pass one and every relation with it. The failure is loud in the UI and closed at the boundary,
/// which is why the principal is explicit here rather than ambient — a dropped principal must not be
/// masked by accessor state that happens to hold a stale value.
/// </para>
/// <para>
/// Two entry points over one implementation. <see cref="ReadCatalog"/> is the injectable form, for
/// the endpoint. <see cref="BuildCatalog"/> is the same logic as a static, called by the gRPC
/// service with the <see cref="SchemaRegistry"/>, evaluator and logger it already holds — so the
/// catalog's warnings keep that service's log category and its constructor is unchanged.
/// </para>
/// </summary>
public sealed class SchemaCatalogReader(
    SchemaRegistry registry,
    IRowFieldAuthorizationEvaluator authEvaluator,
    ILogger<SchemaCatalogReader> logger)
{
    /// <summary>
    /// The catalog as <paramref name="actingUser"/> may see it. See the null-principal note on the
    /// class: pass the caller's real principal. Passing <c>null</c> does not widen access — it
    /// yields an empty catalog, because the evaluator denies a null principal.
    /// </summary>
    public IReadOnlyList<SchemaType> ReadCatalog(ClaimsPrincipal? actingUser) =>
        BuildCatalog(registry, authEvaluator, actingUser, logger);

    internal static IReadOnlyList<SchemaType> BuildCatalog(
        SchemaRegistry registry,
        IRowFieldAuthorizationEvaluator authEvaluator,
        ClaimsPrincipal? actingUser,
        ILogger logger)
    {
        // Two-pass: pass one decides which types survive (row-level denial, then an empty
        // authorized field set), pass two emits relations, dropping any whose related_type
        // did not survive pass one — that cross-type check can't be made until every type has
        // been evaluated.
        var survivors = new List<(SchemaDescriptor Schema, List<SchemaField> Fields, AuthorizationDecision Decision)>();
        // OrdinalIgnoreCase to match SchemaRegistry's own keying (SchemaRegistry.cs) and every
        // other RelationDescriptor.RelatedTypeName lookup (EntityRelationResolver), so a relation
        // declaring a differently-cased related type is not silently dropped from the catalog.
        var survivingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in registry.All.Values)
        {
            // Main's cross-tenant fix (6509b6be): another tenant's schema is not even enumerable.
            if (schema.IsForeignTo(actingUser))
                continue;

            var decision = authEvaluator.Evaluate(schema, actingUser, AuthorizationAction.Read);
            if (decision.Denied)
                continue;

            // ScalarColumns position: EXCLUDE __TenantId. This catalog is client-facing — it is the
            // one view whose whole job is telling a client what it may address. The tenant column is
            // server-owned and never appears on the wire, so publishing it here would undo that.
            IEnumerable<ColumnDescriptor> candidates = new[] { schema.KeyColumn }
                .Concat(schema.ScalarColumns.Where(c => !SchemaDescriptor.IsTenantColumn(c.Name)));
            if (decision.AllowedFields is not null)
                candidates = candidates.Where(c => decision.AllowedFields.Contains(c.Name));

            var fields = candidates
                .Select(c => ProjectField(c, schema, logger))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
            if (fields.Count == 0)
                continue; // Fail-closed guard; the real evaluator always keeps the key column.

            survivors.Add((schema, fields, decision));
            survivingNames.Add(schema.TypeName);
        }

        var types = new List<SchemaType>();
        foreach (var (schema, fields, decision) in survivors)
        {
            var schemaType = new SchemaType
            {
                Name        = schema.TypeName,
                Description = schema.Description ?? string.Empty
            };
            schemaType.Fields.AddRange(fields);
            schemaType.Relations.AddRange(
                schema.Relations
                    // Two conditions. The related type must have survived pass one, and — only
                    // where the FK column is local to this type — the FK must itself be readable.
                    .Where(r => survivingNames.Contains(r.RelatedTypeName)
                             && ForeignKeyIsReadable(r, decision))
                    .Select(r => new SchemaRelation
                    {
                        PropertyName = r.PropertyName,
                        Kind = r.Kind switch
                        {
                            SchemaRelationKind.OneToOne   => ContractsRelationKind.OneToOne,
                            SchemaRelationKind.OneToMany  => ContractsRelationKind.OneToMany,
                            SchemaRelationKind.ManyToOne  => ContractsRelationKind.ManyToOne,
                            SchemaRelationKind.ManyToMany => ContractsRelationKind.ManyToMany,
                            _ => throw new ArgumentOutOfRangeException(nameof(r.Kind), r.Kind,
                                $"Unhandled {nameof(SchemaRelationKind)} value — add a case above.")
                        },
                        RelatedType = r.RelatedTypeName,
                        ForeignKey  = r.ForeignKey
                    }));
            types.Add(schemaType);
        }

        return types;
    }

    /// <summary>
    /// Whether <paramref name="relation"/>'s foreign key may be disclosed to this caller.
    /// <para>
    /// Every FK property is also an ordinary scalar column, so a <c>FieldPermission</c> that removed
    /// it from <c>fields</c> would otherwise still leak its exact name as <c>foreign_key</c>. But
    /// that reasoning only holds where the FK column lives on the <em>declaring</em> type, which
    /// depends on the relation kind (see <c>EntityRelationResolver</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><c>OneToOne</c> / <c>ManyToOne</c> — reads <c>ForeignKey</c> off the declaring entity.</item>
    /// <item><c>ManyToMany</c> — reads <c>ForeignKey</c> as a list off the declaring entity.</item>
    /// <item><c>OneToMany</c> — <c>ForeignKey</c> is a column on the <em>related</em> type's table,
    /// matched against this type's key. It is not a member of this schema at all.</item>
    /// </list>
    /// <para>
    /// <c>AllowedFields</c> only ever holds the declaring schema's own members
    /// (<c>RowFieldAuthorizationEvaluator</c>), so applying the check to <c>OneToMany</c> would drop
    /// every such relation from the catalog whenever any <c>FieldPermission</c> is active — the
    /// check can never be satisfied. Hence the kind gate.
    /// </para>
    /// </summary>
    private static bool ForeignKeyIsReadable(
        RelationDescriptor relation, AuthorizationDecision decision)
    {
        if (decision.AllowedFields is null)
            return true;

        return relation.Kind switch
        {
            SchemaRelationKind.OneToMany => true, // FK belongs to the related type, not this one.
            SchemaRelationKind.OneToOne or
            SchemaRelationKind.ManyToOne or
            SchemaRelationKind.ManyToMany => decision.AllowedFields.Contains(relation.ForeignKey),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation.Kind,
                $"Unhandled {nameof(SchemaRelationKind)} value — add a case above.")
        };
    }

    /// <summary>
    /// Projects one persisted column into its wire form, or <c>null</c> when the column's SQL type
    /// is not known to this build (a descriptor persisted by an older build). Skipping the column
    /// keeps the rest of the catalog available instead of failing the whole read.
    /// <para>
    /// The <b>key column is exempt</b>: it is not optional. Emitting a type with no <c>is_key</c>
    /// field would hand the caller a schema it cannot address a <c>Get</c> with, and the empty-field
    /// guard in pass one assumes the key always survives. An unmapped key type is unrecoverable for
    /// that type, so it throws and the type is failed loudly rather than silently degraded.
    /// </para>
    /// </summary>
    private static SchemaField? ProjectField(ColumnDescriptor col, SchemaDescriptor schema, ILogger logger)
    {
        if (!SchemaBuilder.TrySqlTypeToClr(col.SqlType, out var mapping))
        {
            if (col.Name == schema.KeyColumn.Name)
                throw new ArgumentOutOfRangeException(nameof(col), col.SqlType,
                    $"Key column '{schema.TypeName}.{col.Name}' has unmapped SQL type " +
                    $"'{col.SqlType}'; the catalog cannot describe a type without its key.");

            logger.LogWarning(
                "[GetSchema] Skipping column {Type}.{Column}: unmapped SQL type '{SqlType}'.",
                schema.TypeName.SanitizeForLog(), col.Name.SanitizeForLog(), col.SqlType.SanitizeForLog());
            return null;
        }

        var (clrType, isArray) = mapping;
        var searchKeyOrder = schema.SearchKeyColumns.IndexOf(col.Name);

        var field = new SchemaField
        {
            Name           = col.Name,
            Description    = schema.FieldDescriptions.TryGetValue(col.Name, out var desc) ? desc : string.Empty,
            ClrType        = clrType,
            IsArray        = isArray,
            IsKey          = col.Name == schema.KeyColumn.Name,
            IsNullable     = col.IsNullable,
            IsMetadata     = schema.MetadataColumns.Contains(col.Name),
            IsSearchKey    = searchKeyOrder >= 0,
            SearchKeyOrder = searchKeyOrder >= 0 ? searchKeyOrder : 0,
            IsEmbedding    = schema.VectorFields.Any(v => v.PropertyName == col.Name),
            IsChunk        = schema.ChunkFields.Any(c => c.PropertyName == col.Name)
        };

        field.Enrichment.AddRange(
            schema.EnrichmentTargets
                .Where(t => t.ColumnName == col.Name)
                .Select(t => t.Kind switch
                {
                    EnrichmentKind.Summary   => SchemaEnrichmentKind.EnrichmentSummary,
                    EnrichmentKind.Keywords  => SchemaEnrichmentKind.EnrichmentKeywords,
                    EnrichmentKind.Extracted => SchemaEnrichmentKind.EnrichmentExtracted,
                    _ => throw new ArgumentOutOfRangeException(nameof(t.Kind), t.Kind,
                        $"Unhandled {nameof(EnrichmentKind)} value — add a case above.")
                }));

        return field;
    }
}
