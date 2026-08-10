using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Iverson.Client.Contracts;

namespace Iverson.Client.Conformance.Driver;

/// <summary>
/// The sanctioned seam for observing what a client actually sends: no client exposes its
/// descriptor builder, so the driver wraps the public stub surface in an interceptor and records
/// the outgoing <see cref="SchemaRequest.RootType"/> of every schema registration, forwarding the
/// call unchanged. Nothing is judged here — the JSON is reported verbatim.
/// </summary>
public sealed class DescriptorCaptureInterceptor : Interceptor
{
    private static readonly JsonFormatter Formatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));

    private readonly List<(string TypeName, string Json)> _captured = [];

    /// <summary>Every root type descriptor sent, in send order.</summary>
    public IReadOnlyList<(string TypeName, string Json)> Captured => _captured;

    /// <summary>
    /// The descriptor for the first of <paramref name="preferredTypeNames"/> that was actually sent
    /// under that exact name, or null if none of them was. Never substitutes a different type's
    /// descriptor: each register step reports one named type, and a wrong-but-present descriptor
    /// would have the orchestrator re-register the wrong schema. Selection only — the orchestrator
    /// decides what it means.
    /// </summary>
    public string? Select(params string?[] preferredTypeNames)
    {
        foreach (var preferred in preferredTypeNames)
        {
            if (string.IsNullOrEmpty(preferred)) continue;
            foreach (var (typeName, json) in _captured)
                if (string.Equals(typeName, preferred, StringComparison.OrdinalIgnoreCase))
                    return json;
        }

        return null;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (request is SchemaRequest { RootType: { } rootType })
            _captured.Add((rootType.TypeName, Formatter.Format(rootType)));

        return continuation(request, context);
    }
}
