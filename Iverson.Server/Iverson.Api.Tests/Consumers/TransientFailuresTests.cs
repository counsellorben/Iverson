using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Consumers;
using Iverson.Events;
using Iverson.StarRocks;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public sealed class TransientFailuresTests
{
    // MySqlException's and MySqlEndOfStreamException's constructors are internal to MySqlConnector
    // with no InternalsVisibleTo grant, so instances are built via reflection against the exact
    // overload (the StarRocksResiliencePipelineFactoryTests convention), naming every parameter
    // type to avoid AmbiguousMatchException against sibling overloads.
    private static MySqlException MySql(MySqlErrorCode errorCode, string message, Exception? inner = null) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, message, inner]);

    private static MySqlEndOfStreamException EndOfStream() =>
        (MySqlEndOfStreamException)typeof(MySqlEndOfStreamException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(int), typeof(int)],
                modifiers: null)!
            .Invoke([4, 0]);

    private static PostgresException Pg(string sqlState) =>
        new("synthetic", "ERROR", "ERROR", sqlState);

    private static HttpRequestException Http(HttpStatusCode? status) =>
        new("synthetic", inner: null, statusCode: status);

    private static RpcException Rpc(StatusCode code) => new(new Status(code, "synthetic"));

    // Keyed by a readable name so each case is its own discovered test (Exception is not
    // xunit-serializable, so passing instances through MemberData would fold them into one).
    private static readonly Dictionary<string, Func<Exception>> Transient = new()
    {
        ["Postgres 57P03 recovery mode"]           = () => Pg("57P03"),
        ["Postgres 08006 connection failure"]      = () => Pg("08006"),
        ["Postgres 53300 too many connections"]    = () => Pg("53300"),
        ["Postgres 40001 serialization failure"]   = () => Pg("40001"),
        ["Npgsql wrapping SocketException"]        = () => new NpgsqlException("refused", new SocketException((int)SocketError.ConnectionRefused)),
        ["MySql UnableToConnectToHost"]            = () => MySql(MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts."),
        ["EngagementNotReadyException"]            = () => new EngagementNotReadyException("circuit open"),
        ["Rpc Unavailable"]                        = () => Rpc(StatusCode.Unavailable),
        ["Rpc DeadlineExceeded"]                   = () => Rpc(StatusCode.DeadlineExceeded),
        ["Rpc ResourceExhausted"]                  = () => Rpc(StatusCode.ResourceExhausted),
        ["Rpc Aborted"]                            = () => Rpc(StatusCode.Aborted),
        ["Http null status (connection failure)"]  = () => Http(null),
        ["Http 500"]                               = () => Http(HttpStatusCode.InternalServerError),
        ["Http 503"]                               = () => Http(HttpStatusCode.ServiceUnavailable),
        ["Http 429"]                               = () => Http(HttpStatusCode.TooManyRequests),
        ["TaskCanceled wrapping TimeoutException"] = () => new TaskCanceledException("timed out", new TimeoutException()),
        ["InvalidOperation wrapping transient"]    = () => new InvalidOperationException("outer", Rpc(StatusCode.Unavailable)),
        ["Aggregate, transient second member"]     = () => new AggregateException(new InvalidOperationException("permanent"), Pg("57P03")),
        ["Transient two wrappers deep"]            = () => new InvalidOperationException("outer", new InvalidOperationException("middle", Pg("57P03"))),
        ["Aggregate as an inner exception"]        = () => new InvalidOperationException("outer", new AggregateException(new InvalidOperationException("permanent"), Pg("57P03"))),

        // StarRocks shapes MySqlConnector's own IsTransient does not cover (spec §1).
        ["MySql CommandTimeoutExpired"]            = () => MySql(MySqlErrorCode.CommandTimeoutExpired, "The Command Timeout expired before the operation completed.", new SocketException((int)SocketError.TimedOut)),
        ["MySql Backend node not found"]           = () => MySql(MySqlErrorCode.ParseError, "Backend node not found. Check if any backend node is down."),
        ["MySql wrapping MySqlEndOfStream"]        = () => MySql(MySqlErrorCode.None, "Failed to read the result set.", EndOfStream()),
        ["MySql ParseError Unexpected input '@'"]  = () => MySql(MySqlErrorCode.ParseError, "Getting syntax error at line 1, column 21. Detail message: Unexpected input '@', the most similar input is {'OUTFILE'}."),
    };

    private static readonly Dictionary<string, Func<Exception>> NotTransient = new()
    {
        ["MySql ParseError cannot find role"]       = () => MySql(MySqlErrorCode.ParseError, "cannot find role t_abc"),
        ["MySql ParseError is not granted to"]      = () => MySql(MySqlErrorCode.ParseError, "Role t_abc is not granted to 'iverson'@'%'"),
        ["MySql ParseError ordinary syntax"]        = () => MySql(MySqlErrorCode.ParseError, "Getting syntax error at line 1, column 7. Detail message: Unexpected input 'FORM'."),
        ["MySql 5502 unknown table"]                = () => MySql((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
        ["Postgres 42P01 undefined table"]          = () => Pg("42P01"),
        ["Postgres 42601 syntax error"]             = () => Pg("42601"),
        ["Postgres 23505 unique violation"]         = () => Pg("23505"),
        ["Postgres 22P02 invalid text"]             = () => Pg("22P02"),
        ["Rpc InvalidArgument"]                     = () => Rpc(StatusCode.InvalidArgument),
        ["Rpc NotFound"]                            = () => Rpc(StatusCode.NotFound),
        ["Http 400"]                                = () => Http(HttpStatusCode.BadRequest),
        ["Http 413"]                                = () => Http(HttpStatusCode.RequestEntityTooLarge),
        ["PoisonMessageException"]                  = () => new PoisonMessageException("bad json"),
        ["InvalidOperationException"]               = () => new InvalidOperationException("boom"),
        ["JsonException"]                           = () => new JsonException("bad output"),
        ["TaskCanceled without TimeoutException"]   = () => new TaskCanceledException("cancelled"),
        ["Aggregate, every member permanent"]       = () => new AggregateException(new InvalidOperationException("a"), Pg("23505")),
    };

    public static TheoryData<string> TransientCases() => new(Transient.Keys);
    public static TheoryData<string> NotTransientCases() => new(NotTransient.Keys);

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void IsTransient_DependencyOutageShape_ReturnsTrue(string name) =>
        TransientFailures.IsTransient(Transient[name]()).Should().BeTrue(name);

    [Theory]
    [MemberData(nameof(NotTransientCases))]
    public void IsTransient_BadMessageShape_ReturnsFalse(string name) =>
        TransientFailures.IsTransient(NotTransient[name]()).Should().BeFalse(name);
}
