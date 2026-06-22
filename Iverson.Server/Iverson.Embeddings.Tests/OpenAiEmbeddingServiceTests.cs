using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class OpenAiEmbeddingServiceTests
{
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest    { get; private set; }
        public string?             LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest     = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return response;
        }
    }

    private OpenAiEmbeddingService CreateService(FakeHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
               .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") });
        return new OpenAiEmbeddingService(
            factory,
            Options.Create(new EmbeddingServiceOptions { ApiKey = "test-key" }),
            NullLogger<OpenAiEmbeddingService>.Instance);
    }

    private static HttpResponseMessage SuccessResponse(float[] embedding) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"data":[{"embedding":[{{string.Join(",", embedding)}}]}]}""",
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task EmbedAsync_ReturnsCorrectVector_OnSuccessResponse()
    {
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var handler = new FakeHttpMessageHandler(SuccessResponse(expected));
        var svc = CreateService(handler);

        var result = await svc.EmbedAsync("hello", "text-embedding-3-small");

        result.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task EmbedAsync_SendsModelId_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);
        const string modelId = "text-embedding-3-large";

        await svc.EmbedAsync("some text", modelId);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be(modelId);
    }

    [Fact]
    public async Task EmbedAsync_SendsAuthorizationHeader_WithApiKey()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);

        await svc.EmbedAsync("hello", "text-embedding-3-small");

        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("test-key");
    }

    [Fact]
    public async Task EmbedAsync_SendsInputText_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);
        const string inputText = "the quick brown fox";

        await svc.EmbedAsync(inputText, "text-embedding-3-small");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("input").GetString().Should().Be(inputText);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var svc = CreateService(handler);

        await svc.Invoking(s => s.EmbedAsync("hello", "text-embedding-3-small"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var handler = new FakeHttpMessageHandler(malformed);
        var svc = CreateService(handler);

        await svc.Invoking(s => s.EmbedAsync("hello", "text-embedding-3-small"))
                 .Should().ThrowAsync<Exception>();
    }
}
