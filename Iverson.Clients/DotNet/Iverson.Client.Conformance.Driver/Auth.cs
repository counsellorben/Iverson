using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using IdentityModel.Client;

namespace Iverson.Client.Conformance.Driver;

/// <summary>
/// Wires the two identities every driver call needs onto one channel.
///
/// The service identity (<c>--client-id/--client-secret/--token-endpoint</c>) rides as a
/// <see cref="CallCredentials"/>-supplied <c>Authorization: Bearer</c> header; the acting-user
/// identity (<c>--acting-token</c>) rides as an <c>x-acting-user-authorization</c> header added by
/// a channel-level <see cref="Interceptor"/>. Both must be attached at the channel because
/// <c>EntityCoordinator</c>'s mapped CRUD methods take no header parameter, and
/// <c>AddIversonClient</c> routes the acting-user token only to <c>SchemaCatalogClient</c>.
/// </summary>
public static class Auth
{
    /// <summary>
    /// Builds the channel and returns a call invoker that stamps both identities on every call.
    /// When no client credentials are supplied, only the acting-user header is attached.
    /// </summary>
    public static CallInvoker BuildInvoker(
        string grpcUrl,
        string? clientId,
        string? clientSecret,
        string? tokenEndpoint,
        string actingToken,
        string? serviceToken = null)
    {
        var options = new GrpcChannelOptions
        {
            // Without this, CallCredentials are silently dropped over a plaintext h2c channel —
            // no exception, no Authorization header. Same reason AddIversonClient sets it.
            UnsafeUseInsecureChannelCallCredentials = true,
        };

        // A pre-minted service token wins over the client-credentials trio. Authentik stamps the
        // JWT's `iss` from the request's Host header and grants scopes only when the token
        // request asks for them, so a token this driver minted for itself would be rejected by
        // the API on issuer validation (401) and would carry no `schema_admin` scope (403).
        // The orchestrator mints one correctly and passes it via --service-token.
        if (!string.IsNullOrEmpty(serviceToken))
        {
            var staticCredentials = CallCredentials.FromInterceptor((_, metadata) =>
            {
                metadata.Add("Authorization", $"Bearer {serviceToken}");
                return Task.CompletedTask;
            });
            options.Credentials = ChannelCredentials.Create(ChannelCredentials.Insecure, staticCredentials);
        }
        else if (!string.IsNullOrEmpty(clientId) &&
            !string.IsNullOrEmpty(clientSecret) &&
            !string.IsNullOrEmpty(tokenEndpoint))
        {
            var provider = new ServiceTokenProvider(clientId, clientSecret, tokenEndpoint);
            var callCredentials = CallCredentials.FromInterceptor(async (_, metadata) =>
            {
                var token = await provider.GetTokenAsync();
                metadata.Add("Authorization", $"Bearer {token}");
            });

            options.Credentials = ChannelCredentials.Create(ChannelCredentials.Insecure, callCredentials);
        }

        var channel = GrpcChannel.ForAddress(grpcUrl, options);
        return channel.CreateCallInvoker().Intercept(new ActingUserInterceptor(actingToken));
    }
}

/// <summary>
/// Fetches and caches a client-credentials access token. Deliberately minimal: the client's own
/// cached provider is internal, and the driver only needs one token for one short process.
/// </summary>
internal sealed class ServiceTokenProvider(string clientId, string clientSecret, string tokenEndpoint)
{
    private readonly HttpClient _http = new();
    private string? _token;

    public async Task<string> GetTokenAsync()
    {
        if (_token is not null) return _token;

        var response = await _http.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
        {
            Address = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
        });

        if (response.IsError)
            throw new InvalidOperationException($"failed to acquire service token: {response.Error}");

        _token = response.AccessToken!;
        return _token;
    }
}

/// <summary>
/// Adds the acting-user token to every outgoing call's metadata, the header the server reads to
/// resolve the end-user identity that row/field authorization is evaluated against.
/// </summary>
internal sealed class ActingUserInterceptor(string actingToken) : Interceptor
{
    private ClientInterceptorContext<TRequest, TResponse> WithHeader<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (string.IsNullOrEmpty(actingToken)) return context;

        var options = context.Options;
        var headers = options.Headers ?? new Metadata();
        headers.Add("x-acting-user-authorization", $"Bearer {actingToken}");

        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, options.WithHeaders(headers));
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithHeader(context));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithHeader(context));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithHeader(context));
}
