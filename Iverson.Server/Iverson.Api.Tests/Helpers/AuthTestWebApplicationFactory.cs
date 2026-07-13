using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iverson.Api.Tests.Helpers;

// Boots the real Program.cs (real JwtBearer + FallbackPolicy + endpoint auth metadata wiring)
// via TestServer, with only the startup-blocking real-infra calls (embedding init, schema
// registry load/apply against Postgres) swapped for no-ops — see StartupNoOpFakes.cs. Every
// other registration (Postgres/StarRocks/Qdrant/Kafka clients, hosted services) is left as-is:
// none of them connect eagerly at construction time, so they're inert for these tests, which
// only exercise the authentication/authorization middleware pipeline, not real request handling
// against those stores.
public sealed class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    static AuthTestWebApplicationFactory()
    {
        // Program.cs's `WebApplication.CreateBuilder(args)` runs before ConfigureWebHost below
        // ever gets a chance to touch the builder, so config-reload-on-change (which spins up a
        // FileSystemWatcher per appsettings*.json, each consuming an inotify instance) can only
        // be disabled via the "DOTNET_hostBuilder__reloadConfigOnChange" bootstrap env var, read
        // by the host before any of our code runs. This sandbox's inotify instance quota
        // (fs.inotify.max_user_instances) is already exhausted by other tooling, and the test
        // host doesn't need hot-reload, so disable it outright.
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService, NoOpEmbeddingService>();

            services.RemoveAll<ISchemaRegistryRepository>();
            services.AddSingleton<ISchemaRegistryRepository, NoOpSchemaRegistryRepository>();

            services.RemoveAll<IRecordStoreSchemaManager>();
            services.AddSingleton<IRecordStoreSchemaManager, NoOpRecordStoreSchemaManager>();
        });
    }
}
