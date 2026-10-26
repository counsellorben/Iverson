using Grpc.Core;
using Grpc.Net.Client;
using Iverson.LoadTest.Auth;

namespace Iverson.ClientConformance;

/// <summary>
/// CSR round 12 §4.6: the harness's own gRPC channel, which carries the service and acting-user
/// tokens, takes its transport from the URL's scheme, as the five drivers do. An https:// URL dials
/// TLS against the system roots (SSL_CERT_FILE names the compose CA); an http:// URL dials plaintext
/// h2c, which <see cref="CheckEndpoints"/> admits only on loopback.
/// </summary>
public static class HarnessChannel
{
    /// <summary>
    /// Why these endpoints may not carry the harness's credentials, or null when they may: LoadTest's
    /// rule (CSR round-11 #15), with an https:// gRPC URL allowed because <see cref="Create"/> dials
    /// TLS for one.
    /// </summary>
    public static string? CheckEndpoints(string grpcUrl, string? tokenEndpoint) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true);

    /// <summary>
    /// A channel that sends <paramref name="callCredentials"/> on every call: TLS for an https:// URL,
    /// plaintext h2c otherwise. Grpc.Net.Client drops call credentials on a plaintext channel unless
    /// UnsafeUseInsecureChannelCallCredentials is set, so it is set exactly then.
    /// </summary>
    public static GrpcChannel Create(string grpcUrl, CallCredentials callCredentials)
    {
        var tls = grpcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return GrpcChannel.ForAddress(grpcUrl, new GrpcChannelOptions
        {
            UnsafeUseInsecureChannelCallCredentials = !tls,
            Credentials = ChannelCredentials.Create(
                tls ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure, callCredentials),
        });
    }
}
