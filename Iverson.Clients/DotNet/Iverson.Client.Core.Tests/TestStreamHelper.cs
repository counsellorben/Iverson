using Grpc.Core;

namespace Iverson.Client.Core.Tests;

/// <summary>
/// Shared helper for building a fake <see cref="AsyncServerStreamingCall{TResponse}"/> from a
/// fixed set of items, for tests that exercise <c>EntityCoordinator&lt;T&gt;</c>'s streaming
/// wrappers. Wraps <see cref="FakeAsyncStreamReader{T}"/> (defined in GraphAssemblerTests.cs)
/// rather than mocking <see cref="IAsyncStreamReader{T}"/> via NSubstitute directly — an
/// NSubstitute <c>.Returns(Func&lt;CallInfo,T&gt;)</c> callback composed with a separate
/// <c>.When(...).Do(...)</c> side effect on the same call has no guaranteed execution order,
/// which can silently desync a "has more items" check from the "advance" side effect.
/// A hand-rolled enumerator sidesteps that hazard entirely.
/// </summary>
internal static class TestStreamHelper
{
    public static AsyncServerStreamingCall<TResponse> MakeCall<TResponse>(IReadOnlyList<TResponse> items) =>
        new(
            new FakeAsyncStreamReader<TResponse>(items),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
}
