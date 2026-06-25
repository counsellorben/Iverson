namespace Iverson.Events;

/// <summary>
/// Thrown by a projection handler when a message can never succeed regardless of
/// how many times it is retried (e.g. malformed JSON, an event that deserializes
/// to null). The dispatcher routes these straight to the DLQ without retrying.
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message) : base(message) { }
    public PoisonMessageException(string message, Exception inner) : base(message, inner) { }
}
