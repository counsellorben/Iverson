namespace Iverson.Sql;

public sealed class EnrichmentStateRepository(IRecordStoreQueryExecutor sql) : IEnrichmentStateRepository
{
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS iverson_enrichment_state (
                tenant_id  TEXT NOT NULL,
                type_name  TEXT NOT NULL,
                entity_key TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                enriched_at TIMESTAMPTZ NOT NULL,
                PRIMARY KEY (tenant_id, type_name, entity_key)
            )
            """);

    public Task<string?> GetHashAsync(string tenantId, string typeName, string entityKey) =>
        sql.QuerySingleOrDefaultAsync<string>(
            """
            SELECT source_hash FROM iverson_enrichment_state
            WHERE tenant_id = @TenantId AND type_name = @TypeName AND entity_key = @EntityKey
            """,
            new { TenantId = tenantId, TypeName = typeName, EntityKey = entityKey });

    public Task UpsertAsync(
        IDbTransactionContext tx, string tenantId, string typeName, string entityKey,
        string sourceHash, DateTimeOffset enrichedAt) =>
        tx.ExecuteAsync(
            """
            INSERT INTO iverson_enrichment_state (tenant_id, type_name, entity_key, source_hash, enriched_at)
            VALUES (@TenantId, @TypeName, @EntityKey, @SourceHash, @EnrichedAt)
            ON CONFLICT (tenant_id, type_name, entity_key) DO UPDATE
                SET source_hash = EXCLUDED.source_hash,
                    enriched_at = EXCLUDED.enriched_at
            """,
            new
            {
                TenantId = tenantId,
                TypeName = typeName,
                EntityKey = entityKey,
                SourceHash = sourceHash,
                EnrichedAt = enrichedAt
            });

    public Task DeleteAsync(string tenantId, string typeName, string entityKey) =>
        sql.ExecuteAsync(
            """
            DELETE FROM iverson_enrichment_state
            WHERE tenant_id = @TenantId AND type_name = @TypeName AND entity_key = @EntityKey
            """,
            new { TenantId = tenantId, TypeName = typeName, EntityKey = entityKey });
}
