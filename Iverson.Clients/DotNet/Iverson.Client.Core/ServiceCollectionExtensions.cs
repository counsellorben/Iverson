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
    /// <paramref name="dataPlaneTokenProvider"/>) call credentials over a plaintext (h2c)
    /// channel. Without it, grpc-dotnet's own guard applies: call credentials over an
    /// insecure channel throw rather than silently sending the Authorization header in the
    /// clear. Pass <see langword="true"/> only for a known-local, non-TLS endpoint.
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
