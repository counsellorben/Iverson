namespace Iverson.Sql;

public sealed record DlqRow(Guid Id, string SourceTopic, string ConsumerGroup, string MessageKey,
    string? ExceptionType, string? ExceptionMessage, int Attempts, DateTimeOffset FailedAt, bool Replayed);

public sealed record DlqReplayRow(string SourceTopic, string MessageKey, string MessageValue);

public sealed record DlqMessage(
    string SourceTopic, string ConsumerGroup, string MessageKey, string MessageValue,
    string? ExceptionType, string? ExceptionMessage, int Attempts, DateTimeOffset FailedAt);
