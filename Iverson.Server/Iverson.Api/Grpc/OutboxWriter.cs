using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

public interface IOutboxWriter
{
    Task<Guid> UpsertAndEnqueueOutboxAsync(SchemaDescriptor schema, string typeName, string key, string payloadJson);
    Task DeleteOutboxRowIfPresentAsync(Guid outboxRowId);
}

public sealed class OutboxWriter(
    IPostgresQueryExecutor sql,
    IPostgresTransactionRunner txRunner) : IOutboxWriter
{
    public async Task<Guid> UpsertAndEnqueueOutboxAsync(
        SchemaDescriptor schema, string typeName, string key, string payloadJson)
    {
        var allCols   = schema.ScalarColumns.Select(c => c.Name).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

        var upsertSql =
            $"""
            INSERT INTO "{schema.TableName}"
            SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
            ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
            """;

        var outboxSql =
            $"""
            INSERT INTO "{ReconciliationSchema.TableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null)
            """;

        var outboxRowId = Guid.CreateVersion7();

        await txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            await tx.ExecuteAsync(outboxSql, new
            {
                Id = outboxRowId,
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow
            });
        });

        return outboxRowId;
    }

    public Task DeleteOutboxRowIfPresentAsync(Guid outboxRowId) =>
        sql.ExecuteAsync(
            $"""
            DELETE FROM "{ReconciliationSchema.TableName}"
            WHERE "Id" = @Id
            """,
            new { Id = outboxRowId });
}
