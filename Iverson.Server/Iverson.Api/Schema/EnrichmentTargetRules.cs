namespace Iverson.Api.Schema;

/// <summary>
/// The enrichment-target rules, stated over a <see cref="SchemaDescriptor"/> alone (CSR round 12
/// #18), so that both admission paths apply the same ones:
///  * SchemaRegistrationOrchestrator.ValidateEnrichmentTargets refuses a registration that breaks
///    one, with the message returned here;
///  * SchemaRegistry.LoadAsync rehydrates stored rows without passing through registration, and
///    loads a stored descriptor that breaks one with no enrichment targets.
/// Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
/// are columns the enrichment pipeline writes to, not reads from; the rules keep that sound, and
/// keep a target's FieldPermissions consistent with the pipeline being its only writer and its
/// text being derived from the source properties.
/// </summary>
internal static class EnrichmentTargetRules
{
    // SchemaBuilder adds a chunk field of this name to a type with a document template. Its text is
    // rendered from the template, not read from a property, so it is not a source.
    private const string SyntheticDocumentField = "Document";

    /// <summary>
    /// The first rule <paramref name="descriptor"/> breaks, as a caller-facing message, or null when
    /// it breaks none. Pure: it reads nothing but the descriptor.
    /// </summary>
    internal static string? Violation(SchemaDescriptor descriptor)
    {
        if (descriptor.EnrichmentTargets.Count == 0)
            return null;

        var ownerField = descriptor.Authorization?.OwnerField;

        // Source text is the concatenation of the type's [IversonEmbedding]/[IversonChunk] properties
        // (EnrichmentConsumer.BuildSourceText) — nothing else counts. The descriptor records them as
        // its vector and chunk fields.
        var sourceNames = descriptor.VectorFields.Select(v => v.PropertyName)
            .Concat(descriptor.ChunkFields
                .Select(c => c.PropertyName)
                .Where(name => descriptor.DocumentTemplate is null ||
                               !string.Equals(name, SyntheticDocumentField, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in descriptor.EnrichmentTargets)
        {
            // The key is looked up too, as defence in depth: a target naming it would then reach the
            // key rule below. It is unreachable today. LoadAsync refuses a key column that is not a
            // UUID, and SchemaBuilder builds targets from non-key properties only, so a key-named
            // target is refused by the type rule first. A stored row is arbitrary JSON, so the
            // lookup stays.
            var column = descriptor.ScalarColumns.FirstOrDefault(c =>
                             string.Equals(c.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase))
                         ?? (string.Equals(descriptor.KeyColumn.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase)
                             ? descriptor.KeyColumn
                             : null);
            if (column is null)
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' is not a column " +
                       "of the type.";
            }

            // A string property maps to TEXT and a string array to TEXT[] (SchemaBuilder's type map);
            // no other property type does. Case-insensitive, like LoadAsync's UUID key check, because
            // hand-built descriptors spell SQL types in lower case.
            if (!string.Equals(column.SqlType, "TEXT", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(column.SqlType, "TEXT[]", StringComparison.OrdinalIgnoreCase))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' must be a string " +
                       $"property; its column type is '{column.SqlType}'.";
            }

            // The tenant clause cannot fire at registration, which rejects a declared property named
            // after the reserved tenant column before BuildDescriptor runs, but it can on a stored row.
            // The message names only what a caller can still collide with: tenant_field is no longer
            // accepted at all (RejectDeclaredTenantField).
            if (string.Equals(target.ColumnName, descriptor.KeyColumn.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.ColumnName, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(ownerField) &&
                 string.Equals(target.ColumnName, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot be the key " +
                       "or owner_field.";
            }

            if (sourceNames.Contains(target.ColumnName))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot also carry " +
                       "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
                       "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
                       "own republished event without bound.";
            }

            if (target.Kind == EnrichmentKind.Extracted && string.IsNullOrWhiteSpace(target.Hint))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' has [IversonExtracted] " +
                       "with an empty hint.";
            }
        }

        // A type with an enrichment target but no source property would hash an empty source text
        // and call the enrichment model with an empty prompt.
        if (sourceNames.Count == 0)
        {
            return $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                   "for the enrichment pipeline to read from.";
        }

        var targetNames = descriptor.EnrichmentTargets
            .Select(t => t.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The two rules below use RowFieldAuthorizationEvaluator's own comparisons: a
        // FieldPermission names a property case-insensitively, role names compare ordinally
        // (the way the caller's `groups` claim is matched), and an empty role list means
        // unrestricted, so only non-empty lists are acted on.
        var fieldPermissions = descriptor.Authorization?.FieldPermissions ?? [];

        // Write: the enrichment pipeline is a target's only writer, so a write restriction on
        // a target could never be enforced against anyone.
        var writeRestricted = fieldPermissions.FirstOrDefault(fp =>
            fp.WritableRoles.Count > 0 && targetNames.Contains(fp.FieldName));
        if (writeRestricted is not null)
        {
            return $"Enrichment target '{writeRestricted.FieldName}' on '{descriptor.TypeName}' carries a " +
                   "FieldPermission with writable_roles. The enrichment pipeline is the target's only writer, so " +
                   "a write restriction on it cannot be enforced; remove writable_roles from that FieldPermission.";
        }

        // Read: a target's text is generated from the source text, so every caller who can read
        // a target must be able to read every source. The evaluator excludes a field when ANY
        // FieldPermission naming it excludes the caller, so each restricted source permission
        // needs one target permission whose roles are a subset of its roles.
        foreach (var source in fieldPermissions.Where(fp =>
                     fp.ReadableRoles.Count > 0 && sourceNames.Contains(fp.FieldName)))
        {
            foreach (var target in descriptor.EnrichmentTargets)
            {
                var covered = fieldPermissions.Any(fp =>
                    fp.ReadableRoles.Count > 0 &&
                    string.Equals(fp.FieldName, target.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    fp.ReadableRoles.All(role => source.ReadableRoles.Contains(role, StringComparer.Ordinal)));

                if (!covered)
                {
                    return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' is generated from " +
                           $"source property '{source.FieldName}', whose readable_roles are restricted, but the " +
                           "target carries no FieldPermission whose readable_roles are a subset of them. Every " +
                           "caller who can read an enrichment target must be able to read every source it is " +
                           "generated from.";
                }
            }
        }

        return null;
    }
}
