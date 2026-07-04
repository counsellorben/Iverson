namespace Iverson.StarRocks;

public sealed class StarRocksNotReadyException(string message, Exception? innerException = null)
    : Exception(message, innerException);
