namespace Iverson.Sql;

public sealed record DlqRow(Guid Id, string SourceTopic, string ConsumerGroup, string MessageKey,
    string? ExceptionType, string? ExceptionMessage, int Attempts, DateTime FailedAt, bool Replayed,
    string? TenantId);

public sealed record DlqReplayRow(string SourceTopic, string MessageKey, string MessageValue, string? TenantId);

/// <summary>What the api's replay checks read: the row's topic and tenant, never its message.</summary>
public sealed record DlqReplayCheckRow(string SourceTopic, string? TenantId);

public sealed record DlqMessage(
    string SourceTopic, string ConsumerGroup, string MessageKey, string MessageValue,
    string? ExceptionType, string? ExceptionMessage, int Attempts, DateTime FailedAt, string? TenantId);
