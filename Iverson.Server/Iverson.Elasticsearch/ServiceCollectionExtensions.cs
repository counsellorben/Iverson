using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Elasticsearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearch(this IServiceCollection services, string url)
    {
        services.AddSingleton(_ => new ElasticsearchClient(new Uri(url)));
        services.AddSingleton<IElasticsearchService, ElasticsearchService>();
        return services;
    }
}
