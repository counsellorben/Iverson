using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class EnrichmentServiceTests
{
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest     { get; private set; }
        public string?             LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            LastRequest     = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return response;
        }
    }

    private EnrichmentService CreateService(FakeHttpMessageHandler handler, string modelId = "qwen2.5:3b")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        return new EnrichmentService(
            factory,
            Options.Create(new EnrichmentServiceOptions { ModelId = modelId }),
            NullLogger<EnrichmentService>.Instance);
    }

    private static HttpResponseMessage SuccessResponse(string responseText) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { response = responseText }),
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task GenerateAsync_ReturnsResponseField_OnSuccessResponse()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse("hello world"));
        var svc = CreateService(handler);

        var result = await svc.GenerateAsync("summarize this");

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task GenerateAsync_SendsConfiguredModel_AndStreamFalse_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse("ok"));
        var svc = CreateService(handler, modelId: "qwen2.5:3b");

        await svc.GenerateAsync("some prompt");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("qwen2.5:3b");
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_PostsTo_ApiGenerate_Endpoint()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse("ok"));
        var svc = CreateService(handler);

        await svc.GenerateAsync("some prompt");

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/generate");
    }

    [Fact]
    public async Task GenerateJsonAsync_SendsFormatJson_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse("""{"key":"value"}"""));
        var svc = CreateService(handler);

        var result = await svc.GenerateJsonAsync("extract this");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("format").GetString().Should().Be("json");
        result.Should().Be("""{"key":"value"}""");
    }

    [Fact]
    public async Task GenerateAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = CreateService(handler);

        await svc.Invoking(s => s.GenerateAsync("hello"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GenerateAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var svc = CreateService(new FakeHttpMessageHandler(malformed));

        await svc
            .Invoking(s => s.GenerateAsync("hello"))
            .Should().ThrowAsync<Exception>();
    }
}
