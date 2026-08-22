namespace Iverson.Sql;

public sealed record DocumentRerenderQueueRow(
    Guid Id,
    string? TenantId,
    string TypeName,
    string? EntityKey,
    string? Cursor,
    int Attempts,
    // Echoed back as a guard on AdvanceCursorAsync/DeleteTypeRowAsync: EnqueueTypeAsync's
    // ON CONFLICT DO UPDATE reuses this row's Id and stamps a new EnqueuedAt, so Id alone
    // cannot distinguish the row this worker polled from a re-enqueue that landed since.
    // DateTime, not DateTimeOffset: Npgsql materializes TIMESTAMPTZ as DateTime, and
    // Dapper matches a record's constructor by exact parameter type — a DateTimeOffset
    // here fails materialization outright. Matches DlqRow.FailedAt.
    DateTime EnqueuedAt);
