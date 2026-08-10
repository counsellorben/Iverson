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
    /// When set, only the named root type is actually sent; every other schema registration is
    /// dropped (not captured, not forwarded) and answered with an empty response.
    ///
    /// This exists solely so the driver can control registration ORDER. All five drivers must
    /// register author, then tag, then article, so a referenced type exists before the type that
    /// references it; the other four take an explicit type list, but .NET's
    /// <c>SchemaRegistrar.RegisterAllAsync</c> walks <c>EntityRegistry.All</c>, whose enumeration
    /// order is a dictionary's and not the driver's to choose. Running the registrar once per type
    /// with the other two suppressed here is the ordering control the public surface does not give.
    /// </summary>
    public string? OnlySendTypeName { get; set; }

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
        {
            if (OnlySendTypeName is { Length: > 0 } only &&
                !string.Equals(rootType.TypeName, only, StringComparison.OrdinalIgnoreCase))
            {
                return Suppressed<TResponse>();
            }

            _captured.Add((rootType.TypeName, Formatter.Format(rootType)));
        }

        return continuation(request, context);
    }

    /// <summary>
    /// An already-completed call carrying an empty <see cref="SchemaResponse"/>, used for the
    /// registrations <see cref="OnlySendTypeName"/> filters out. Nothing reaches the wire, and the
    /// registrar (which reads only <c>response.Registered</c>) continues to the next type.
    /// </summary>
    private static AsyncUnaryCall<TResponse> Suppressed<TResponse>() =>
        new(Task.FromResult((TResponse)(object)new SchemaResponse()),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });
}
