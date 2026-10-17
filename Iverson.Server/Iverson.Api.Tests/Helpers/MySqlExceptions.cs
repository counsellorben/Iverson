using System.Reflection;
using MySqlConnector;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant, so instances are
/// built via reflection against the exact overload (the StarRocksResiliencePipelineFactoryTests convention).
/// </summary>
internal static class MySqlExceptions
{
    public static MySqlException Create(MySqlErrorCode errorCode, string message) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, message, null]);

    /// <summary>What MySqlConnector 2.6.2 raises when a pool is full (MySqlConnection.CreateSessionAsync).</summary>
    public static MySqlException PoolExhausted() =>
        Create(MySqlErrorCode.UnableToConnectToHost, "Connect Timeout expired. All pooled connections are in use.");
}
