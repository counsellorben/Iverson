using System.Reflection;
using Iverson.Client.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Client.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full Iverson client framework.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="grpcEndpoint">Base URI of the Iverson.Api gRPC endpoint.</param>
    /// <param name="credentials">
    /// OAuth2 client-credentials identity attached to the schema-registration (mapping) client as
    /// a Bearer token, and as the data-plane fallback when <paramref name="dataPlaneTokenProvider"/>
    /// is omitted. Omit only for calls to endpoints that don't require authentication.
    /// </param>
    /// <param name="dataPlaneTokenProvider">
    /// Token source attached to the persistence/retrieval/search clients instead of
    /// <paramref name="credentials"/>, when supplied. Lets a caller use a different identity
    /// (e.g. a human/acting-user login) for data-plane calls than for schema registration.
    /// </param>
    /// <param name="entityAssemblies">
    /// Assemblies to scan for <c>[IversonEntity]</c> classes.
    /// Defaults to the calling assembly if none are provided.
    /// </param>
    /// <param name="allowInsecureChannelCallCredentials">
    /// Explicit opt-in required to attach <see cref="credentials"/> (or
    /// <paramref name="dataPlaneTokenProvider"/>) call credentials, or an
    /// <paramref name="actingUserTokenProvider"/>, over a plaintext (h2c) endpoint. Without
    /// it: grpc-dotnet's own guard refuses call credentials over an insecure channel rather
    /// than silently sending the Authorization header in the clear, and this method applies
    /// the same default-deny explicitly to the acting-user token, which rides as raw
    /// Metadata rather than CallCredentials and so is otherwise invisible to grpc-dotnet's
    /// guard. Pass <see langword="true"/> only for a known-local, non-TLS endpoint.
    /// </param>
    public static IServiceCollection AddIversonClient(
        this IServiceCollection services,
        string grpcEndpoint,
        IversonClientCredentials? credentials = null,
        Func<Task<string>>? dataPlaneTokenProvider = null,
        Func<Task<string>>? actingUserTokenProvider = null,
        bool allowInsecureChannelCallCredentials = false,
        params Assembly[] entityAssemblies)
    {
        var assemblies = entityAssemblies.Length > 0
            ? entityAssemblies
            : [Assembly.GetCallingAssembly()];

        // CSR round-3 finding #4: the acting-user identity rides as raw Metadata
        // (see ActingUserMetadata.WithActingUser), added per-call by EntityCoordinator and
        // SchemaCatalogClient — not as CallCredentials — so it never reaches
        // AttachCredentials/UnsafeUseInsecureChannelCallCredentials below, which only guards
        // `credentials`/`dataPlaneTokenProvider`. grpc-dotnet's own insecure-channel guard is
        // therefore structurally blind to it, and this check exists to close that gap
        // explicitly, mirroring the same default-deny applied to the service credential.
        if (actingUserTokenProvider is not null &&
            !allowInsecureChannelCallCredentials &&
            string.Equals(new Uri(grpcEndpoint).Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to attach an acting-user token provider to a plaintext (h2c) endpoint " +
                "without an explicit allowInsecureChannelCallCredentials=true opt-in. The " +
                "acting-user identity travels as raw Metadata, not CallCredentials, so grpc-dotnet's " +
                "own UnsafeUseInsecureChannelCallCredentials guard cannot see it — the acting-user " +
                "Bearer token would otherwise be sent in the clear. Pass " +
                "allowInsecureChannelCallCredentials: true only for a known-local, non-TLS endpoint.");
        }

        services.AddSingleton(new EntityRegistry(assemblies));
        services.AddSingleton<GraphAssembler>();

        var mappingBuilder = services.AddGrpcClient<ObjectMappingService.ObjectMappingServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var persistenceBuilder = services.AddGrpcClient<ObjectPersistenceService.ObjectPersistenceServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var retrievalBuilder = services.AddGrpcClient<ObjectRetrievalService.ObjectRetrievalServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));
        var searchBuilder = services.AddGrpcClient<ObjectSearchService.ObjectSearchServiceClient>(
            o => o.Address = new Uri(grpcEndpoint));

        if (credentials is not null)
        {
            services.AddSingleton(sp => new CachedClientCredentialsTokenProvider(credentials));
            AttachCredentials(mappingBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync(),
                allowInsecureChannelCallCredentials);
        }

        if (dataPlaneTokenProvider is not null)
        {
            AttachCredentials(persistenceBuilder, _ => dataPlaneTokenProvider(), allowInsecureChannelCallCredentials);
            AttachCredentials(retrievalBuilder, _ => dataPlaneTokenProvider(), allowInsecureChannelCallCredentials);
            AttachCredentials(searchBuilder, _ => dataPlaneTokenProvider(), allowInsecureChannelCallCredentials);
        }
        else if (credentials is not null)
        {
            AttachCredentials(persistenceBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync(),
                allowInsecureChannelCallCredentials);
            AttachCredentials(retrievalBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync(),
                allowInsecureChannelCallCredentials);
            AttachCredentials(searchBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync(),
                allowInsecureChannelCallCredentials);
        }

        services.AddTransient(typeof(EntityCoordinator<>));
        services.AddSingleton(new ActingUserIdentity(actingUserTokenProvider));

        services.AddSingleton<SchemaRegistrar>();

        services.AddSingleton(sp => new SchemaCatalogClient(
            sp.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>(),
            actingUserTokenProvider));

        return services;
    }

    private static void AttachCredentials(
        IHttpClientBuilder builder,
        Func<IServiceProvider, Task<string>> getToken,
        bool allowInsecureChannelCallCredentials)
    {
        // grpc-dotnet refuses to send CallCredentials over a plaintext (h2c) channel unless
        // UnsafeUseInsecureChannelCallCredentials is set — otherwise the Authorization header
        // would go out in the clear. Setting it unconditionally would defeat that guard for
        // every consumer, including ones pointed at a real TLS endpoint, so it is set only
        // when the caller has explicitly opted in for a known-local, non-TLS endpoint.
        if (allowInsecureChannelCallCredentials)
        {
            builder.ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true);
        }

        builder.AddCallCredentials(async (_, metadata, serviceProvider) =>
        {
            var token = await getToken(serviceProvider);
            metadata.Add("Authorization", $"Bearer {token}");
        });
    }
}
