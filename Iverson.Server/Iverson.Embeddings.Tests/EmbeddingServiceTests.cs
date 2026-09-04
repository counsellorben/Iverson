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
    private sealed class FakeHttpMessageHandler(
        HttpResponseMessage embedResponse,
        HttpResponseMessage? infoResponse = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest     { get; private set; }   // /v1/embeddings requests only
        public string?             LastRequestBody { get; private set; }
        public int                 InfoCalls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/info")
            {
                Interlocked.Increment(ref InfoCalls);
                return infoResponse is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)   // Ollama's answer
                    : await CopyAsync(infoResponse, ct);
            }

            LastRequest     = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return await CopyAsync(embedResponse, ct);
        }
    }

    // A fresh HttpResponseMessage per call: EnsureInitializedAsync now issues two requests and the
    // service reads each response's content stream once.
    private static async Task<HttpResponseMessage> CopyAsync(HttpResponseMessage source, CancellationToken ct) =>
        new(source.StatusCode)
        {
            Content = new StringContent(
                await source.Content.ReadAsStringAsync(ct), Encoding.UTF8, "application/json")
        };

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

    // The OpenAI-compatible shape both Ollama and TEI serve at /v1/embeddings.
    private static HttpResponseMessage SuccessResponse(float[] embedding) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$$"""{"object":"list","data":[{"object":"embedding","index":0,"embedding":[{{{string.Join(",", embedding)}}}]}],"model":"x","usage":{"prompt_tokens":1,"total_tokens":1}}""",
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage InfoResponse(string modelId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"model_id":"{{modelId}}","max_input_length":512,"auto_truncate":true}""",
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

    [Fact]
    public async Task EmbedDocumentAsync_PostsToV1Embeddings_OnTheConfiguredBaseUrl_NotTheClientBaseAddress()
    {
        // CreateService gives the HttpClient BaseAddress http://localhost:11434; the options say otherwise.
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler, new EmbeddingServiceOptions
            { ModelId = "some-unknown-model", BaseUrl = "http://tei-embed:8091" });

        await svc.EmbedDocumentAsync("hello");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri.Should().Be(new Uri("http://tei-embed:8091/v1/embeddings"));
    }

    [Fact]
    public async Task EnsureInitializedAsync_InfoReportsTheConfiguredModel_Initialises()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f, 0.2f]), InfoResponse("BAAI/bge-base-en-v1.5"));
        var svc = CreateService(handler, "BAAI/bge-base-en-v1.5");

        await svc.EnsureInitializedAsync();

        svc.Dimension.Should().Be(2);
        handler.InfoCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureInitializedAsync_InfoReportsAnotherModel_ThrowsNamingBothIds_OnEveryCall()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f, 0.2f]), InfoResponse("BAAI/bge-base-en-v1.5"));
        var svc = CreateService(handler, "BAAI/bge-small-en-v1.5");

        await svc.Invoking(s => s.EnsureInitializedAsync())
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*BAAI/bge-base-en-v1.5*BAAI/bge-small-en-v1.5*");
        var act = () => svc.Dimension;
        act.Should().Throw<InvalidOperationException>();

        // Guard-before-dimension: a second call must run the guard again and throw again. A guard
        // placed after `_dimension = probe.Length` returns at the top and this assertion fails.
        await svc.Invoking(s => s.EnsureInitializedAsync())
                 .Should().ThrowAsync<InvalidOperationException>();
        handler.InfoCalls.Should().Be(2);
    }

    [Fact]
    public async Task EnsureInitializedAsync_Info404_Initialises()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f, 0.2f, 0.3f]));   // no info response → 404
        var svc = CreateService(handler);

        await svc.EnsureInitializedAsync();

        svc.Dimension.Should().Be(3);
        handler.InfoCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SendsInfoAfterTheProbe_NotBeforeIt()
    {
        // A guard that ran before the probe would leave LastRequestBody null when it threw.
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]), InfoResponse("other-model"));
        var svc = CreateService(handler, "nomic-embed-text");

        await svc.Invoking(s => s.EnsureInitializedAsync()).Should().ThrowAsync<InvalidOperationException>();

        handler.LastRequestBody.Should().Contain("\"probe\"");
    }

    private sealed class CountingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount;   // /v1/embeddings requests only: "probe once per lifetime", not "one HTTP call"

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/info")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Interlocked.Increment(ref CallCount);
            await Task.Delay(20, ct); // widen the race window for concurrency test
            return await CopyAsync(response, ct);
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
        public int CallCount;   // /v1/embeddings requests only

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/info")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Interlocked.Increment(ref CallCount);
            if (CallCount == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return CopyAsync(success, ct);
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
    [InlineData("BAAI/bge-base-en-v1.5",    "",                  "Represent this sentence for searching relevant passages: ")]
    [InlineData("BAAI/bge-small-en-v1.5",   "",                  "Represent this sentence for searching relevant passages: ")]
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

    [Fact]
    public async Task DefaultService_WhoseModelIsListedInModels_UsesTheListedBaseUrl()
    {
        // The DI-constructed default service is built from the global options with ModelId set to
        // the TEI-only candidate (compose BENCH_EMBED_MODEL); the resolver short-circuits to it for
        // that id, so this service must resolve its own base URL through Models (CDR-1 §2.1).
        var handler = new FakeHttpMessageHandler(SuccessResponse([0.1f]));
        var svc = CreateService(handler, new EmbeddingServiceOptions
        {
            BaseUrl = "http://ollama:11434",
            ModelId = "BAAI/bge-base-en-v1.5",
            Models  = [new ModelEndpoint { Name = "BAAI/bge-base-en-v1.5", BaseUrl = "http://tei-embed:8091" }]
        });

        await svc.EmbedDocumentAsync("hello");

        handler.LastRequest!.RequestUri.Should().Be(new Uri("http://tei-embed:8091/v1/embeddings"));
    }
}
