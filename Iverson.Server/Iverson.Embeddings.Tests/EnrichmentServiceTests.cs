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

    // The HttpClient's BaseAddress is deliberately NOT the options' BaseUrl: the service must build
    // an absolute URI from its own BaseUrl, never a relative path on the client.
    private static EnrichmentService CreateService(FakeHttpMessageHandler handler, EnrichmentServiceOptions? options = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        return new EnrichmentService(
            factory,
            Options.Create(options ?? new EnrichmentServiceOptions { ModelId = "Qwen/Qwen2.5-1.5B-Instruct", BaseUrl = "http://tgi:8092" }),
            NullLogger<EnrichmentService>.Instance);
    }

    // The OpenAI-compatible shape TGI and Ollama both serve at /v1/chat/completions.
    private static HttpResponseMessage ChatResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { role = "assistant", content } } } }),
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task GenerateAsync_PostsToV1ChatCompletions_OnTheConfiguredBaseUrl_NotTheClientBaseAddress()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("ok"));
        var svc = CreateService(handler);

        await svc.GenerateAsync("some prompt");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://tgi:8092/v1/chat/completions"));
    }

    [Fact]
    public async Task GenerateAsync_SendsModel_OneUserMessage_MaxTokens_TemperatureZero_StreamFalse()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("ok"));
        var svc = CreateService(handler);

        await svc.GenerateAsync("summarize this");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("Qwen/Qwen2.5-1.5B-Instruct");
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("summarize this");
        root.GetProperty("max_tokens").GetInt32().Should().Be(256);   // Iverson.Embeddings grants InternalsVisibleTo to nothing (P24)
        root.GetProperty("temperature").GetDouble().Should().Be(0);
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.TryGetProperty("format", out _).Should().BeFalse();
        root.TryGetProperty("prompt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_ReturnsTheFirstChoicesMessageContent()
    {
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("hello world")));

        var result = await svc.GenerateAsync("summarize this");

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task GenerateJsonAsync_SendsNoResponseFormat()
    {
        // TGI's grammars need a fixed property set the extraction hint cannot supply (spec §3.5);
        // a response_format here would 422 on TGI.
        var handler = new FakeHttpMessageHandler(ChatResponse("""{"key":"value"}"""));
        var svc = CreateService(handler);

        await svc.GenerateJsonAsync("extract this");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("response_format", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("format", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateJsonAsync_ExtractsTheFencedBlock_FromAProseWrappedReply()
    {
        // Ollama's shape without a format directive (spec §9 row 17): prose, a fenced object, a note.
        var reply = "Here is the extracted information formatted as JSON:\n\n```json\n{\n  \"revenue\": {\"figure\": 4.2},\n  \"year\": 2024\n}\n```\n\nNote: the percentage is not included.";
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse(reply)));

        var result = await svc.GenerateJsonAsync("extract this");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("year").GetInt32().Should().Be(2024);
        result.Should().NotContain("```").And.NotContain("Note:");
    }

    [Fact]
    public async Task GenerateJsonAsync_ExtractsTheFirstBalancedObject_WhenThereIsNoFence()
    {
        // A closing brace inside a string must not end the span; trailing prose must be dropped.
        var reply = "Sure: {\"a\": \"x } y\", \"b\": {\"c\": 1}} and that is all }";
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse(reply)));

        var result = await svc.GenerateJsonAsync("extract this");

        result.Should().Be("{\"a\": \"x } y\", \"b\": {\"c\": 1}}");
    }

    [Fact]
    public async Task GenerateJsonAsync_Throws_WhenTheReplyHoldsNoJsonObject()
    {
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("I could not find any structured information.")));

        await svc.Invoking(s => s.GenerateJsonAsync("extract this"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*I could not find any structured information.*");
    }

    [Fact]
    public async Task GenerateJsonAsync_Throws_WhenTheOnlyObjectDoesNotParse()
    {
        // The balanced span is found but JsonDocument.Parse rejects it (trailing comma); without the
        // parse guard this malformed text would be stored verbatim in the target column (spec §4).
        var svc = CreateService(new FakeHttpMessageHandler(ChatResponse("Result: {\"a\": 1,} — that's all")));

        await svc.Invoking(s => s.GenerateJsonAsync("extract this"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Result: {*");
    }

    [Fact]
    public async Task GenerateAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var svc = CreateService(new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await svc.Invoking(s => s.GenerateAsync("hello"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GenerateAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var svc = CreateService(new FakeHttpMessageHandler(malformed));

        await svc.Invoking(s => s.GenerateAsync("hello"))
                 .Should().ThrowAsync<Exception>();
    }
}
