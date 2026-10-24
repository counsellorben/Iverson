using Microsoft.AspNetCore.Mvc.Testing;

namespace Iverson.Api.Tests.Helpers;

// The two hosts behind HstsPipelineTests.cs, built the way AdminConsoleCorsTestWebApplicationFactories.cs
// builds its siblings: Program.cs reads Hsts:Enabled before builder.Build(), too early for
// ConfigureWebHost, so each factory sets the Hsts__Enabled env var in its instance constructor
// immediately before CreateClient(). Both clients address a host that is not loopback, over https://,
// because ASP.NET's HSTS middleware writes nothing on plain HTTP or for localhost.
public sealed class HstsEnabledTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    public HttpClient Client { get; }

    public HstsEnabledTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Hsts__Enabled", "true");
        Client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://iverson.test") });
    }
}

// The off counterpart, set explicitly to "false" so this host never depends on what the factory above
// left in the process-global env var.
public sealed class HstsDisabledTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    public HttpClient Client { get; }

    public HstsDisabledTestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Hsts__Enabled", "false");
        Client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://iverson.test") });
    }
}
