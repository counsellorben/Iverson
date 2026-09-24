using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Api.Tests.Helpers;

// Three sibling factories backing AdminConsoleCorsPipelineTests.cs: one per AdminConsole:Origin
// state, plus an admin-listener variant that shares the configured state. All derive from
// AuthTestWebApplicationFactory to reuse its NoOp infra swaps (see that file) rather than
// duplicating them -- the only thing any of them adds is the AdminConsole__Origin env var (the
// admin-listener variant also stamps the request's LocalPort, see below).
//
// Why an env var set in an INSTANCE constructor, immediately followed by CreateClient(),
// rather than a static constructor (the pattern AuthTestWebApplicationFactory itself uses for
// Qdrant__ApiKey): Program.cs reads AdminConsole:Origin at the top of its own script, before
// builder.Build() -- the same "too late for ConfigureWebHost/ConfigureAppConfiguration" spot
// AuthTestWebApplicationFactory's class comment documents for Qdrant__ApiKey. Static
// constructors run once per type, but WHEN they run relative to a SIBLING type's static
// constructor is not something a test author controls: xunit resolves every IClassFixture<T>
// a test class asks for before calling that class's own constructor, so if both factories'
// env-var writes happened in static constructors, one could be resolved (and its host
// booted, reading whatever the env var holds at that instant) before the other factory's
// static constructor has even run -- two different factories racing to set the SAME
// process-global key, with the eventual reader getting whichever write happened to land
// last. Setting the var and calling CreateClient() together, inside one instance
// constructor, pins "this factory's value is set" and "Program.cs reads it" to the same
// uninterrupted call, so the two factories' setup can never interleave regardless of which
// one xunit constructs first.
public sealed class CorsConfiguredTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    // The single source of truth for the configured origin -- AdminConsoleCorsPipelineTests
    // reads this same constant for both the request's Origin header and the expected
    // Access-Control-Allow-Origin response header, rather than each hardcoding its own copy
    // of the string (the review finding this test suite responds to: a hand-picked origin
    // that appears in no configuration file tests the code, not the configuration -- this
    // constant IS this factory's "configuration", so both sides of every assertion trace back
    // to one place instead of two literals that could silently drift apart).
    public const string ConfiguredOrigin = "https://console.iverson.test";

    public HttpClient Client { get; }

    public CorsConfiguredTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("AdminConsole__Origin", ConfiguredOrigin);
        Client = CreateClient();
    }
}

// The fail-closed counterpart: AdminConsole__Origin explicitly set to "" (not merely left
// unset) so this factory's host never depends on ambient process state -- including whatever
// CorsConfiguredTestWebApplicationFactory above may have already written to the same
// process-global env var during this test run.
public sealed class CorsDisabledTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    public HttpClient Client { get; }

    public CorsDisabledTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("AdminConsole__Origin", "");
        Client = CreateClient();
    }
}

// The configured origin again, plus a startup filter that stamps every request's LocalPort as
// 8081 -- the Http1 listener the admin-api Ingress targets -- so ListenerPortGateAsync runs
// exactly as it does for a real browser request there (TestServer otherwise reports
// LocalPort 0, which the gate lets through). Built the same way as its siblings, and used only
// by AdminConsoleCorsPipelineTests, for the env-var reason documented above.
public sealed class CorsConfiguredAdminListenerTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    public HttpClient Client { get; }

    public CorsConfiguredAdminListenerTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("AdminConsole__Origin", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
        Client = CreateClient();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, AdminListenerPortStamp>());
    }

    private sealed class AdminListenerPortStamp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.LocalPort = 8081;
                return nextMiddleware();
            });
            next(app);
        };
    }
}
