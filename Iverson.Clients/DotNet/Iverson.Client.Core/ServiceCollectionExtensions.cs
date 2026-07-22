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
    public static IServiceCollection AddIversonClient(
        this IServiceCollection services,
        string grpcEndpoint,
        IversonClientCredentials? credentials = null,
        Func<Task<string>>? dataPlaneTokenProvider = null,
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
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
        }

        if (dataPlaneTokenProvider is not null)
        {
            AttachCredentials(persistenceBuilder, _ => dataPlaneTokenProvider());
            AttachCredentials(retrievalBuilder, _ => dataPlaneTokenProvider());
            AttachCredentials(searchBuilder, _ => dataPlaneTokenProvider());
        }
        else if (credentials is not null)
        {
            AttachCredentials(persistenceBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
            AttachCredentials(retrievalBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
            AttachCredentials(searchBuilder,
                sp => sp.GetRequiredService<CachedClientCredentialsTokenProvider>().GetTokenAsync());
        }

        services.AddTransient(typeof(EntityCoordinator<>));

        services.AddSingleton<SchemaRegistrar>();

        return services;
    }

    private static void AttachCredentials(IHttpClientBuilder builder, Func<IServiceProvider, Task<string>> getToken)
    {
        // Without UnsafeUseInsecureChannelCallCredentials=true, CallCredentials are silently
        // dropped over this repo's plaintext h2c channel — no exception, no Authorization
        // header. Confirmed via Microsoft's own docs and a real listening-server test.
        builder
            .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
            .AddCallCredentials(async (_, metadata, serviceProvider) =>
            {
                var token = await getToken(serviceProvider);
                metadata.Add("Authorization", $"Bearer {token}");
            });
    }
}
