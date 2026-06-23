using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class EmbeddingServiceTests
{
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest     { get; private set; }
        public string?             LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest     = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return response;
        }
    }

    private EmbeddingService CreateService(FakeHttpMessageHandler handler, string modelId = "nomic-embed-text")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
               .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        return new EmbeddingService(
            factory,
            Options.Create(new EmbeddingServiceOptions { ModelId = modelId }),
            NullLogger<EmbeddingService>.Instance);
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

        var result = await svc.EmbedAsync("hello");

        result.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task EmbedAsync_SendsModelId_FromOptions_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler, modelId: "nomic-embed-text");

        await svc.EmbedAsync("some text");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("nomic-embed-text");
    }

    [Fact]
    public async Task EmbedAsync_DoesNotSendAuthorizationHeader()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);

        await svc.EmbedAsync("hello");

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_SendsInputText_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);
        const string inputText = "the quick brown fox";

        await svc.EmbedAsync(inputText);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("input").GetString().Should().Be(inputText);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = CreateService(handler);

        await svc.Invoking(s => s.EmbedAsync("hello"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var svc = CreateService(new FakeHttpMessageHandler(malformed));

        await svc.Invoking(s => s.EmbedAsync("hello"))
                 .Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task InitializeAsync_SetsDimension_FromProbeEmbedLength()
    {
        var probe = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }; // 5-dim probe
        var handler = new FakeHttpMessageHandler(SuccessResponse(probe));
        var svc = CreateService(handler);

        await svc.InitializeAsync();

        svc.Dimension.Should().Be(5);
    }

    [Fact]
    public void Dimension_BeforeInitializeAsync_ThrowsInvalidOperationException()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);

        var act = () => svc.Dimension;

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not initialized*");
    }
}
