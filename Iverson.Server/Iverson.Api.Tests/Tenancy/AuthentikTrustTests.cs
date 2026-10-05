using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public class AuthentikTrustTests : IClassFixture<AuthTestWebApplicationFactory>, IDisposable
{
    private readonly AuthTestWebApplicationFactory _baseFactory;
    private readonly string _dir = Directory.CreateTempSubdirectory("authentik-trust-").FullName;

    public AuthentikTrustTests(AuthTestWebApplicationFactory baseFactory) => _baseFactory = baseFactory;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static X509Certificate2 NewCa(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    // A leaf for dnsName signed by issuer, or self-signed when issuer is null, exported and reloaded
    // so Kestrel gets a persisted private key on every platform. The CA-signed leaf ends a day before
    // its CA: CertificateRequest.Create rejects a leaf that outlives its issuer.
    private static X509Certificate2 NewLeaf(string dnsName, X509Certificate2? issuer)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = issuer is null
            ? req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))
            : req.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(8))
                 .CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private string WritePem(X509Certificate2 cert, string file)
    {
        var path = Path.Combine(_dir, file);
        File.WriteAllText(path, cert.ExportCertificatePem());
        return path;
    }

    private static async Task<HttpStatusCode> GetThroughHandler(X509Certificate2 serverCert, string caPath)
    {
        var builder = WebApplication.CreateBuilder();
        // The copied Iverson.Api appsettings.json would also bind Kestrel:Endpoints (8080/8081).
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(serverCert)));
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First()).Port;

        using var http = new HttpClient(AuthentikTrust.CreateHandler(caPath));
        return (await http.GetAsync($"https://localhost:{port}/")).StatusCode;
    }

    [Fact]
    public async Task Handler_AcceptsACertificateTheConfiguredCaSigned()
    {
        var ca = NewCa("test-ca");
        (await GetThroughHandler(NewLeaf("localhost", ca), WritePem(ca, "ca.crt"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Handler_RejectsACertificateAnotherCaSigned()
    {
        var trusted = NewCa("trusted-ca");
        var other = NewCa("other-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("localhost", other), WritePem(trusted, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Handler_RejectsACertificateForAnotherHost()
    {
        var ca = NewCa("test-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("other.example", ca), WritePem(ca, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Handler_RejectsASelfSignedCertificate()
    {
        var ca = NewCa("test-ca");
        await FluentActions.Awaiting(() => GetThroughHandler(NewLeaf("localhost", null), WritePem(ca, "ca.crt")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    private IConfiguration GoodConfig(Action<Dictionary<string, string?>>? change = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:MetadataAddress"] = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration",
            ["Authentication:ActingUser:MetadataAddress"] = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration",
            ["Authentication:InternalIssuer"] = "http://iverson-authentik:9000/",
            ["Authentik:BaseUrl"] = "https://iverson-authentik:8443",
            ["Authentik:CaCertificatePath"] = WritePem(NewCa("test-ca"), "guard-ca.crt"),
        };
        change?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    [Theory]
    [InlineData("Authentication:MetadataAddress", null)]
    [InlineData("Authentication:MetadataAddress", "http://iverson-authentik:9000/application/o/iverson-api/.well-known/openid-configuration")]
    [InlineData("Authentication:ActingUser:MetadataAddress", null)]
    [InlineData("Authentication:ActingUser:MetadataAddress", "http://iverson-authentik:9000/application/o/iverson-api/.well-known/openid-configuration")]
    [InlineData("Authentik:BaseUrl", null)]
    [InlineData("Authentik:BaseUrl", "http://iverson-authentik:9000")]
    [InlineData("Authentication:InternalIssuer", null)]
    [InlineData("Authentik:CaCertificatePath", null)]
    [InlineData("Authentik:CaCertificatePath", "/nonexistent/ca.crt")]
    public void Guard_RefusesTheApiRole_OutsideDevelopment(string key, string? value) =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v => v[key] = value), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");

    [Fact]
    public void Guard_RefusesACaFileThatIsNotAPemCertificate()
    {
        var notPem = Path.Combine(_dir, "not.pem");
        File.WriteAllText(notPem, "not a certificate");
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(v => v["Authentik:CaCertificatePath"] = notPem), "api", Env("Production")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Authentik:CaCertificatePath*");
    }

    [Fact]
    public void Guard_PassesAFullyConfiguredApiRole() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(GoodConfig(), "api", Env("Production"))).Should().NotThrow();

    [Fact]
    public void Guard_SkipsDevelopment() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(new ConfigurationBuilder().Build(), "api", Env("Development"))).Should().NotThrow();

    [Fact]
    public void Guard_SkipsTheWorkerRole() =>
        FluentActions.Invoking(() => AuthentikTrust.ValidateStartup(new ConfigurationBuilder().Build(), "worker", Env("Production"))).Should().NotThrow();

    [Theory]
    [InlineData(JwtBearerDefaults.AuthenticationScheme, "Authentication:MetadataAddress")]
    [InlineData("ActingUser", "Authentication:ActingUser:MetadataAddress")]
    public void BothSchemes_FetchMetadataOverTls_TrustTheCa_AndAcceptTheInternalIssuer(string scheme, string metadataKey)
    {
        const string metadata = "https://iverson-authentik:8443/application/o/iverson-api/.well-known/openid-configuration";
        var caPath = WritePem(NewCa("test-ca"), $"wiring-{scheme}.crt");
        using var factory = _baseFactory.WithWebHostBuilder(b => b
            .UseSetting(metadataKey, metadata)
            .UseSetting("Authentication:InternalIssuer", "http://iverson-authentik:9000/")
            .UseSetting("Authentik:CaCertificatePath", caPath));

        var opts = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(scheme);

        opts.RequireHttpsMetadata.Should().BeTrue();
        opts.MetadataAddress.Should().Be(metadata);
        opts.TokenValidationParameters.ValidIssuers.Should().Contain("http://iverson-authentik:9000/");
        opts.BackchannelHttpHandler.Should().BeOfType<SocketsHttpHandler>()
            .Which.SslOptions.RemoteCertificateValidationCallback.Should().NotBeNull();
    }
}
