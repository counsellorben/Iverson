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

    private EmbeddingService CreateService(HttpMessageHandler handler, string modelId = "nomic-embed-text")
    {
        // EmbedAsync disposes the HttpClient it gets from the factory on every call
        // (`using var client = httpClientFactory.CreateClient(...)`), matching real
        // IHttpClientFactory usage where each CreateClient() call returns a fresh client
        // over a shared, undisposed handler. Return a new client per call here too, so
        // tests that call EnsureInitializedAsync/EmbedDocumentAsync more than once on the
        // same service instance don't hit a spurious ObjectDisposedException from client reuse.
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("http://localhost:11434") });
        return new EmbeddingService(
            factory,
            Options.Create(new EmbeddingServiceOptions { ModelId = modelId }),
            NullLogger<EmbeddingService>.Instance);
    }

    private EmbeddingService CreateService(HttpMessageHandler handler, EmbeddingServiceOptions options)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("http://localhost:11434") });
        return new EmbeddingService(
            factory,
            Options.Create(options),
            NullLogger<EmbeddingService>.Instance);
    }

    private static HttpResponseMessage SuccessResponse(float[] embedding) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"embeddings":[[{{string.Join(",", embedding)}}]]}""",
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task EmbedDocumentAsync_ReturnsCorrectVector_OnSuccessResponse()
    {
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var handler = new FakeHttpMessageHandler(SuccessResponse(expected));
        var svc = CreateService(handler);

        var result = await svc.EmbedDocumentAsync("hello");

        result.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task EmbedDocumentAsync_SendsModelId_FromOptions_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler, modelId: "nomic-embed-text");

        await svc.EmbedDocumentAsync("some text");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("nomic-embed-text");
    }

    [Fact]
    public async Task EmbedDocumentAsync_DoesNotSendAuthorizationHeader()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler);

        await svc.EmbedDocumentAsync("hello");

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task EmbedDocumentAsync_SendsInputText_InRequestBody()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        // no prefix for this model family, so the input text travels unmodified
        var svc = CreateService(handler, modelId: "some-unknown-model");
        const string inputText = "the quick brown fox";

        await svc.EmbedDocumentAsync(inputText);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("input").GetString().Should().Be(inputText);
    }

    [Fact]
    public async Task EmbedDocumentAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = CreateService(handler);

        await svc.Invoking(s => s.EmbedDocumentAsync("hello"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedDocumentAsync_Throws_OnMalformedResponseJson()
    {
        var malformed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"unexpected":"format"}""", Encoding.UTF8, "application/json")
        };
        var svc = CreateService(new FakeHttpMessageHandler(malformed));

        await svc
            .Invoking(s => s.EmbedDocumentAsync("hello"))
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
    public async Task EnsureInitializedAsync_ProbesWithoutAPrefix()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(handler, "nomic-embed-text");   // non-empty document prefix

        await sut.EnsureInitializedAsync();

        handler.LastRequestBody.Should().Contain("\"probe\"");
        handler.LastRequestBody.Should().NotContain("search_document: ");
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

    private sealed class CountingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            await Task.Delay(20, ct); // widen the race window for concurrency test
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(
                    await response.Content!.ReadAsStringAsync(ct), Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_CalledTwice_ProbesOnlyOnce()
    {
        var handler = new CountingHttpMessageHandler(SuccessResponse([0.1f, 0.2f]));
        var svc = CreateService(handler);

        await svc.EnsureInitializedAsync();
        await svc.EnsureInitializedAsync();

        handler.CallCount.Should().Be(1);
        svc.Dimension.Should().Be(2);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ConcurrentCallers_ProbeOnlyOnce()
    {
        var handler = new CountingHttpMessageHandler(SuccessResponse([0.1f, 0.2f, 0.3f]));
        var svc = CreateService(handler);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => svc.EnsureInitializedAsync()));

        handler.CallCount.Should().Be(1);
        svc.Dimension.Should().Be(3);
    }

    private sealed class FlakyThenSuccessHandler(HttpResponseMessage success) : HttpMessageHandler
    {
        public int CallCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            if (CallCount == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(success);
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_FailingProbe_ThrowsButLeavesServiceUsableForLaterSuccess()
    {
        var handler = new FlakyThenSuccessHandler(SuccessResponse([0.1f, 0.2f]));
        var svc = CreateService(handler);

        await svc.Invoking(s => s.EnsureInitializedAsync())
                 .Should().ThrowAsync<HttpRequestException>();

        var act = () => svc.Dimension;
        act.Should().Throw<InvalidOperationException>();

        await svc.EnsureInitializedAsync();

        svc.Dimension.Should().Be(2);
        handler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData("nomic-embed-text",          "search_document: ", "search_query: ")]
    [InlineData("nomic-embed-text:latest",   "search_document: ", "search_query: ")]
    [InlineData("snowflake-arctic-embed:s",  "",                  "Represent this sentence for searching relevant passages: ")]
    [InlineData("some-unknown-model",        "",                  "")]
    public void For_ResolvesByFamily_StrippingAnyTag(string modelId, string doc, string query)
    {
        var pair = EmbeddingPrefixes.For(modelId);
        pair.Document.Should().Be(doc);
        pair.Query.Should().Be(query);
    }

    [Fact]
    public async Task EmbedDocumentAsync_PrependsTheResolvedPrefix()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(handler, "nomic-embed-text");

        await sut.EmbedDocumentAsync("hello");

        handler.LastRequestBody.Should().Contain("search_document: hello");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task EmbedDocumentAsync_WithEmptyInput_ThrowsEvenWhenAPrefixWouldMakeItNonEmpty(string input)
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(handler, "nomic-embed-text");   // non-empty document prefix

        var act = async () => await sut.EmbedDocumentAsync(input);

        await act.Should().ThrowAsync<EmptyEmbeddingInputException>();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task EmbedDocumentAsync_WithExplicitEmptyPrefixOverride_SendsNoPrefix()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(
            handler,
            new EmbeddingServiceOptions { ModelId = "nomic-embed-text", DocumentPrefix = "" });

        await sut.EmbedDocumentAsync("hello");

        handler.LastRequestBody.Should().NotContain("search_document: ");
    }

    [Fact]
    public async Task EmbedQueryAsync_PrependsTheResolvedPrefix()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(handler, "nomic-embed-text");

        await sut.EmbedQueryAsync("hello");

        handler.LastRequestBody.Should().Contain("search_query: hello");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task EmbedQueryAsync_WithEmptyInput_ThrowsEvenWhenAPrefixWouldMakeItNonEmpty(string input)
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(handler, "nomic-embed-text");   // non-empty query prefix

        var act = async () => await sut.EmbedQueryAsync(input);

        await act.Should().ThrowAsync<EmptyEmbeddingInputException>();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task EmbedQueryAsync_WithExplicitEmptyPrefixOverride_SendsNoPrefix()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var sut     = CreateService(
            handler,
            new EmbeddingServiceOptions { ModelId = "nomic-embed-text", QueryPrefix = "" });

        await sut.EmbedQueryAsync("hello");

        handler.LastRequestBody.Should().NotContain("search_query: ");
    }
}
