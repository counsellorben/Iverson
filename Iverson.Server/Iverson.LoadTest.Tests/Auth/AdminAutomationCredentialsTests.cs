using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

public class AdminAutomationCredentialsTests
{
    private const string Endpoint = "http://localhost:9000/application/o/token/";

    private sealed record Sent(string? Host, string? Authorization, Dictionary<string, string> Form);

    // Answers every token request with a token that expires inside the 60 s re-use margin, so each
    // GetTokenAsync call sends a new request, and records what was sent.
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = HttpUtility.ParseQueryString(body);
            Requests.Add(new Sent(
                request.Headers.Host,
                request.Headers.Authorization?.ToString(),
                form.AllKeys.ToDictionary(k => k!, k => form[k]!)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"operator-{{Requests.Count}}","token_type":"Bearer","expires_in":30}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Theory]
    [InlineData("IVERSON_CLIENT_ID")]
    [InlineData("IVERSON_TOKEN_ENDPOINT")]
    public void FromEnvironment_IsNull_WithoutClientIdOrTokenEndpoint(string missing)
    {
        var all = new[] { ("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint), ("IVERSON_CLIENT_SECRET", "s") };

        AdminAutomationCredentials.FromEnvironment(Env(all.Where(v => v.Item1 != missing).ToArray()), null)
            .Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_IsNull_WithNeitherSecretNorAssertionFile() =>
        AdminAutomationCredentials.FromEnvironment(Env(("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint)), null)
            .Should().BeNull();

    [Fact]
    public void FromEnvironment_PrefersTheAssertionFile_OverASecret()
    {
        using var creds = AdminAutomationCredentials.FromEnvironment(Env(
            ("IVERSON_CLIENT_ID", "id"), ("IVERSON_TOKEN_ENDPOINT", Endpoint),
            ("IVERSON_CLIENT_SECRET", "s"), ("IVERSON_CLIENT_ASSERTION_FILE", "/tmp/sa-token")), null)!;

        creds.UsesAssertion.Should().BeTrue();
        creds.ClientSecret.Should().BeNull("a secret must never be sent alongside the assertion");
        creds.SdkCredentials.Should().BeNull("in assertion mode the SDK takes the service-token provider instead");
    }

    [Fact]
    public async Task GetTokenAsync_WithASecret_SendsTheSecretAndNoAssertion()
    {
        var endpoint = new TokenEndpoint();
        using var creds = new AdminAutomationCredentials("id", Endpoint, "s3cret", null, "admin schema_admin", "authentik-server:9000", endpoint);

        (await creds.GetTokenAsync()).Should().Be("operator-1");

        var sent = endpoint.Requests.Single();
        sent.Form["grant_type"].Should().Be("client_credentials");
        sent.Form["scope"].Should().Be("admin schema_admin");
        sent.Form.Should().NotContainKey("client_assertion").And.NotContainKey("client_secret");
        sent.Authorization.Should().StartWith("Basic ", "IdentityModel's default sends the secret as HTTP Basic, as today");
        sent.Host.Should().Be("authentik-server:9000");
        creds.SdkCredentials.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTokenAsync_WithAnAssertionFile_SendsTheFileAsAJwtBearerAssertion_ReadingItForEachMint()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "first.jwt.token\n");
            var endpoint = new TokenEndpoint();
            using var creds = new AdminAutomationCredentials("id", Endpoint, null, file, "admin", null, endpoint);

            (await creds.GetTokenAsync()).Should().Be("operator-1");
            await File.WriteAllTextAsync(file, "second.jwt.token");
            (await creds.GetTokenAsync()).Should().Be("operator-2");

            endpoint.Requests.Should().HaveCount(2);
            foreach (var sent in endpoint.Requests)
            {
                sent.Form["grant_type"].Should().Be("client_credentials");
                sent.Form["client_id"].Should().Be("id");
                sent.Form["client_assertion_type"].Should().Be(AdminAutomationCredentials.JwtBearerAssertionType);
                sent.Form.Should().NotContainKey("client_secret");
                sent.Authorization.Should().BeNull();
            }
            endpoint.Requests[0].Form["client_assertion"].Should().Be("first.jwt.token");
            endpoint.Requests[1].Form["client_assertion"].Should().Be("second.jwt.token",
                "the file is read again for every mint, so a refreshed token is picked up");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task GetTokenAsync_WithAMissingAssertionFile_SaysHowToMintOne()
    {
        using var creds = new AdminAutomationCredentials("id", Endpoint, null, "/nonexistent/sa-token", null, null, new TokenEndpoint());

        var act = () => creds.GetTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kubectl create token*");
    }
}
