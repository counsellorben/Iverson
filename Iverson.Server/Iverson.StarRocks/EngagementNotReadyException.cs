namespace Iverson.StarRocks;

public sealed class EngagementNotReadyException(string message, Exception? innerException = null)
    : Exception(message, innerException);
