using System.Text.RegularExpressions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Grpc.Core;

namespace Iverson.Api.Grpc;

public interface ISchemaRegistrationOrchestrator
{
    Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct);
}

public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IEmbeddingService embedding,
    SchemaRegistry registry)
    : ISchemaRegistrationOrchestrator
{
    // TypeName/property names are string-interpolated unescaped into CREATE TABLE/ALTER TABLE
    // DDL by PostgresSchemaManager/StarRocksSchemaManager after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at
    // the source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be
    // a safe DDL identifier. No underscores are permitted in the input because ToSnakeCase
    // inserts its own; this pattern also naturally rejects an empty string.
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct)
    {
        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            try
            {
                await embedding.EnsureInitializedAsync(ct);
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"Embedding service is unavailable, so schema registration cannot determine the vector "
                    + $"dimension. Check that Ollama is reachable and retry. ({ex.Message})"));
            }

            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

            ValidateEnrichmentTargets(typeDesc, descriptor);

            var ownerField = descriptor.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerField))
                ValidateFieldReference(descriptor, ownerField, "owner_field");

            // tenant_field is MANDATORY (unlike owner_field): every schema must declare a
            // platform-enforced tenant boundary, independent of whatever AuthorizationRules
            // it configures.
            if (string.IsNullOrEmpty(descriptor.TenantColumn))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"tenant_field is required on '{descriptor.TypeName}'."));
            }
            ValidateFieldReference(descriptor, descriptor.TenantColumn, "tenant_field");

            // The key column is compared against a uuid parameter in every EntityRepository
            // predicate (FetchByKey/FetchMany/FetchByColumn/Delete/Update). A non-UUID key
            // registers cleanly and then fails every read with Postgres 42883.
            if (!string.Equals(descriptor.KeyColumn.SqlType, "UUID", StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Key property '{descriptor.KeyColumn.Name}' on '{descriptor.TypeName}' has SQL type " +
                    $"'{descriptor.KeyColumn.SqlType}', but a key column must be UUID. Declare the key as a " +
                    $"GUID/UUID-typed property in your client model (.NET: Guid; Java: UUID; Python: uuid.UUID; " +
                    $"Go: add the `iverson_guid:\"true\"` struct tag; TypeScript: add the @IversonGuid() decorator)."));
            }

            // Membership only — NOT ValidateFieldReference, which additionally requires a string-valued
            // SqlType for Qdrant filtering and would reject a ManyToMany's UUID[] foreign key.
            // OneToMany is exempt: its foreign key is a column on the RELATED type's row.
            foreach (var relation in descriptor.Relations.Where(r => r.Kind != Schema.RelationKind.OneToMany))
            {
                var column = descriptor.ScalarColumns.FirstOrDefault(c =>
                    string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

                if (column is null)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}', which is not a declared property."));
                }

                // ManyToMany's foreign key is a list of ids (UUID[]); the others are a single id (UUID).
                // Only the OneToMany reverse lookup compares an FK column in SQL, so the UUID[] arm is a
                // consistency rule — but a TEXT[] column would still be wrong by construction.
                var requiredSqlType = relation.Kind == Schema.RelationKind.ManyToMany ? "UUID[]" : "UUID";
                if (!string.Equals(column.SqlType, requiredSqlType, StringComparison.Ordinal))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}' with SQL type '{column.SqlType}', " +
                        $"but a {relation.Kind} foreign key must be {requiredSqlType}. Declare it as a " +
                        $"GUID/UUID-typed property{(relation.Kind == Schema.RelationKind.ManyToMany ? " array" : "")} " +
                        $"(.NET: Guid; Java: UUID; Python: uuid.UUID; Go: add the `iverson_guid:\"true\"` struct tag; " +
                        $"TypeScript: add the @IversonGuid() decorator)."));
                }

                // The foreign key must be named after the RELATED type, e.g. "AuthorId" for a
                // relation to Author — not whatever the caller happened to call the property.
                // OneToMany is exempt (see the loop filter above): its foreign key is named after
                // THIS type and lives on the related type's row, so this rule does not apply to it.
                var requiredSuffix = relation.Kind == Schema.RelationKind.ManyToMany ? "Ids" : "Id";
                var requiredForeignKey = relation.RelatedTypeName + requiredSuffix;
                if (!string.Equals(relation.ForeignKey, requiredForeignKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}', but a {relation.Kind} foreign key " +
                        $"referencing '{relation.RelatedTypeName}' must be named '{requiredForeignKey}'."));
                }
            }

            // The navigation-property name must be distinct from the foreign-key name, for every
            // relation kind including OneToMany (per the IVC-REL-003 ruling) — unlike the naming
            // check above, this pass is NOT filtered by kind. When the two names collide, there is
            // no separate nav property: writes and reads of the "nav property" silently alias the
            // foreign key, and ResolveManyToManyAsync-style hydration overwrites it outright.
            foreach (var relation in descriptor.Relations)
            {
                if (string.Equals(relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"has a navigation-property name identical to its foreign key '{relation.ForeignKey}'. " +
                        $"The navigation-property name must be distinct from the foreign key."));
                }
            }

            try
            {
                await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            }
            catch (SchemaDriftException ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
            }

            await registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        return registered;
    }

    // Shared by owner_field (optional) and tenant_field (mandatory) — both name a scalar
    // property that must resolve to a real column, be string-valued (Qdrant filtering requires
    // it), and not collide with a reserved chunk-payload key.
    private static void ValidateFieldReference(
        SchemaDescriptor descriptor,
        string fieldName,
        string fieldLabel)
    {
        if (!descriptor.ScalarColumns.Any(c => string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' does not match any declared scalar property."));
        }

        var column = descriptor.ScalarColumns.First(c =>
            string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        // Allow-list, not a reject-list: IntelligenceStoreConsumer.ExtractTypedValue's default branch
        // only produces a clean scalar string for these 4 SqlTypes. Every other SqlType — including
        // the array variants UUID[]/REAL[] that SchemaBuilder.ArrayTypeOverrides can also produce for
        // a scalar column — falls through to JsonElement.ToString(), which for a non-string JSON value
        // (a number, bool, or array) produces something that can never equal a real caller's identity
        // value, silently excluding every caller (including the legitimate owner/tenant) from every result.
        var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
        if (!stringValuedSqlTypes.Contains(column.SqlType.ToUpperInvariant()))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' has SqlType '{column.SqlType}', " +
                $"which is not string-valued; Qdrant filtering requires a string-valued {fieldLabel}."));
        }

        if (descriptor.ChunkFields.Count > 0)
        {
            var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
            var camelField = fieldName.ToCamelCase();
            if (reservedChunkKeys.Contains(camelField))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' camelCases to '{camelField}', " +
                    $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
            }
        }
    }

    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. Five rules keep that
    // invariant sound at registration time rather than failing obscurely at ingest.
    private static void ValidateEnrichmentTargets(TypeDescriptor typeDesc, SchemaDescriptor descriptor)
    {
        var ownerField = descriptor.Authorization?.OwnerField;

        foreach (var target in descriptor.EnrichmentTargets)
        {
            var property = typeDesc.Properties.First(p =>
                string.Equals(p.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase));

            if (property.ClrType != ClrType.ClrString)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' must be a string " +
                    $"property; it is '{property.ClrType}'."));
            }

            if (property.IsKey ||
                string.Equals(target.ColumnName, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(ownerField) &&
                 string.Equals(target.ColumnName, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot be the key, " +
                    "tenant_field, or owner_field."));
            }

            if (property.IsEmbedding || property.IsChunk)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot also carry " +
                    "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
                    "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
                    "own republished event without bound."));
            }

            if (target.Kind == EnrichmentKind.Extracted && string.IsNullOrWhiteSpace(target.Hint))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' has [IversonExtracted] " +
                    "with an empty hint."));
            }
        }

        if (descriptor.EnrichmentTargets.Count > 0)
        {
            var targetNames = descriptor.EnrichmentTargets
                .Select(t => t.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Source text is defined as the concatenation of the type's [IversonEmbedding]/
            // [IversonChunk] properties — nothing else counts. A type with an enrichment target
            // but no embedding/chunk property would hash an empty source text and call the
            // enrichment model with an empty prompt.
            var hasSourceProperty = typeDesc.Properties.Any(p => p.IsEmbedding || p.IsChunk);

            if (!hasSourceProperty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                    "for the enrichment pipeline to read from."));
            }
        }
    }

    private static void ValidateIdentifier(string name, string context)
    {
        if (!IdentifierPattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{context} '{name}' is not a valid identifier — it must start with a letter and contain only letters and digits."));
        }
    }
}
