using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

internal sealed class PostgresFailedPublishSink(IPostgresRepository sql) : IFailedPublishSink
{
    public Task RecordAsync(string typeName, string key, string reason) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{ReconciliationSchema.TableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, @LastError, null)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow,
                LastError = reason
            });
}
