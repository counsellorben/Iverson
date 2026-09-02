using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class EmbeddingServiceResolverTests
{
    private sealed class RecordingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            return response;
        }
    }

    private static HttpResponseMessage SuccessResponse(float[] embedding) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"embeddings":[[{{string.Join(",", embedding)}}]]}""",
                Encoding.UTF8,
                "application/json")
        };

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("http://localhost:11434") });
        return factory;
    }

    [Fact]
    public void Get_ForNullEmptyOrTheConfiguredDefault_ReturnsTheSameDefaultInstance()
    {
        var defaultService = Substitute.For<IEmbeddingService>();
        var options = Options.Create(new EmbeddingServiceOptions { ModelId = "nomic-embed-text" });
        var resolver = new EmbeddingServiceResolver(
            Substitute.For<IHttpClientFactory>(),
            options,
            defaultService,
            NullLogger<EmbeddingService>.Instance);

        resolver.Get(null).Should().BeSameAs(defaultService);
        resolver.Get("").Should().BeSameAs(defaultService);
        resolver.Get("nomic-embed-text").Should().BeSameAs(defaultService);
    }

    [Fact]
    public void Get_ForAnotherModel_ReturnsADistinctCachedInstance()
    {
        var defaultService = Substitute.For<IEmbeddingService>();
        var options = Options.Create(new EmbeddingServiceOptions { ModelId = "nomic-embed-text" });
        var resolver = new EmbeddingServiceResolver(
            Substitute.For<IHttpClientFactory>(),
            options,
            defaultService,
            NullLogger<EmbeddingService>.Instance);

        var first  = resolver.Get("snowflake-arctic-embed:s");
        var second = resolver.Get("snowflake-arctic-embed:s");

        first.Should().NotBeSameAs(defaultService);
        first.ModelId.Should().Be("snowflake-arctic-embed:s");
        second.Should().BeSameAs(first);
    }

    // The constraint under test: a non-default model must derive its prefixes from
    // EmbeddingPrefixes.For(), never from the DEFAULT model's configured DocumentPrefix/
    // QueryPrefix overrides. The configured overrides below are deliberately distinctive
    // strings that would be visibly wrong if they leaked onto arctic's embeddings — and arctic's
    // OWN query prefix ("Represent this sentence for searching relevant passages: ") is
    // non-empty, so a correct resolution and a leaked-override resolution produce genuinely
    // different request bodies.
    [Fact]
    public async Task Get_ForNonDefaultModel_DerivesQueryPrefixFromTable_NotFromConfiguredOverride()
    {
        var handler = new RecordingHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var options = Options.Create(new EmbeddingServiceOptions
        {
            BaseUrl        = "http://localhost:11434",
            ModelId        = "nomic-embed-text",
            DocumentPrefix = "CONFIGURED_OVERRIDE_DOC: ",
            QueryPrefix    = "CONFIGURED_OVERRIDE_QUERY: "
        });
        var defaultService = new EmbeddingService(
            FactoryFor(handler), options, NullLogger<EmbeddingService>.Instance);
        var resolver = new EmbeddingServiceResolver(
            FactoryFor(handler), options, defaultService, NullLogger<EmbeddingService>.Instance);

        var arctic = resolver.Get("snowflake-arctic-embed:s");
        await arctic.EmbedQueryAsync("hello");

        handler.LastRequestBody.Should()
            .Contain("Represent this sentence for searching relevant passages: hello");
        handler.LastRequestBody.Should().NotContain("CONFIGURED_OVERRIDE_QUERY");
    }

    [Fact]
    public async Task Get_ForNonDefaultModel_DerivesDocumentPrefixFromTable_NotFromConfiguredOverride()
    {
        var handler = new RecordingHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
        var options = Options.Create(new EmbeddingServiceOptions
        {
            BaseUrl        = "http://localhost:11434",
            ModelId        = "nomic-embed-text",
            DocumentPrefix = "CONFIGURED_OVERRIDE_DOC: ",
            QueryPrefix    = "CONFIGURED_OVERRIDE_QUERY: "
        });
        var defaultService = new EmbeddingService(
            FactoryFor(handler), options, NullLogger<EmbeddingService>.Instance);
        var resolver = new EmbeddingServiceResolver(
            FactoryFor(handler), options, defaultService, NullLogger<EmbeddingService>.Instance);

        // arctic's own document prefix is "" (EmbeddingPrefixes.Table), so a correct resolution
        // sends the text unprefixed; a leaked override would prepend CONFIGURED_OVERRIDE_DOC.
        var arctic = resolver.Get("snowflake-arctic-embed:s");
        await arctic.EmbedDocumentAsync("hello");

        handler.LastRequestBody.Should().NotContain("CONFIGURED_OVERRIDE_DOC");
    }
}
