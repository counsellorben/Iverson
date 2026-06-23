# Ollama Embeddings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `EmbeddingModel` enum with a server-side Ollama integration so client attributes become zero-arg markers and all embedding calls route to a local `nomic-embed-text` model.

**Architecture:** Client attributes (`[IversonEmbedding]`, `[IversonChunk]`) lose all model/dimension knowledge and send `VectorDim=0`/`ModelId=""` to the server. On startup, `IEmbeddingService.InitializeAsync` makes one probe call to Ollama, measures the returned vector length, and caches `Dimension`. `ObjectMappingGrpcService` injects `IEmbeddingService` and fills in the real dimension and model ID when building `VectorDescriptor`/`ChunkDescriptor` during schema registration.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions, Ollama (`ollama/ollama:latest`), docker-compose v2 (`service_completed_successfully` condition)

## Global Constraints

- Target framework: `net10.0` throughout — do not change TFMs
- Test libraries already in use: xUnit 2.9.3, NSubstitute 5.3.0, FluentAssertions 7.0.0 — do not add new test packages
- Ollama model: `nomic-embed-text`, 768 dimensions
- Ollama OpenAI-compatible endpoint: `{BaseUrl}/v1/embeddings` — no `Authorization` header required
- `EmbedAsync` in `IntelligenceStoreConsumer` uses `vf.ModelId` / `cf.ModelId` today — both calls drop that arg after this plan
- NSubstitute quirk: never `await` the result of `_grpcClient.Received(n).SomeAsyncMethod(...)` — discard with `_ =`

---

## File Map

| File | Action |
|---|---|
| `Iverson.Client/Iverson.Client.Attributes/EmbeddingModel.cs` | **Delete** |
| `Iverson.Client/Iverson.Client.Attributes/EmbeddingModelExtensions.cs` | **Delete** |
| `Iverson.Client/Iverson.Client.Attributes/IversonEmbeddingAttribute.cs` | Modify — zero-arg marker |
| `Iverson.Client/Iverson.Client.Attributes/IversonChunkAttribute.cs` | Modify — keep MaxTokens/Overlap, remove Model/Dimension |
| `Iverson.Client/Iverson.Client.Sample/Models/Article.cs` | Modify — remove model args from attributes |
| `Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs` | Modify — `AddAnnotations` sends VectorDim=0, ModelId="" |
| `Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` | Modify — remove `EmbeddingModel` from fixture; update VectorDim assertion |
| `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs` | Modify — add Dimension, ModelId, InitializeAsync; drop modelId param |
| `Iverson.Server/Iverson.Embeddings/OpenAiEmbeddingService.cs` | **Delete** |
| `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs` | **Create** — renamed impl, no auth header, ModelId from config |
| `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs` | Modify — remove ApiKey, add ModelId, change BaseUrl default |
| `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs` | Modify — register `EmbeddingService` |
| `Iverson.Server/Iverson.Embeddings.Tests/OpenAiEmbeddingServiceTests.cs` | **Delete** |
| `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` | **Create** — updated tests for new service |
| `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` | Modify — drop modelId arg from both EmbedAsync calls |
| `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` | Modify — fix mock setup and Received() calls |
| `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` | Modify — inject IEmbeddingService, fill Dimension/ModelId in BuildDescriptor |
| `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` | Modify — mock IEmbeddingService, add new RegisterSchema test |
| `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` | Modify — update to 768/"nomic-embed-text" |
| `Iverson.Server/Iverson.Api/Program.cs` | Modify — add InitializeAsync before SchemaRegistry.LoadAsync |
| `Iverson.Server/docker-compose.yml` | Modify — add ollama + ollama-init services, update iverson-api env/depends_on |
| `Iverson.Server/Iverson.Launcher/Program.cs` | Modify — add ollama to compose up, add WaitForOllamaAsync |

---

### Task 1: Simplify client attributes and update SchemaRegistrar

**Files:**
- Delete: `Iverson.Client/Iverson.Client.Attributes/EmbeddingModel.cs`
- Delete: `Iverson.Client/Iverson.Client.Attributes/EmbeddingModelExtensions.cs`
- Modify: `Iverson.Client/Iverson.Client.Attributes/IversonEmbeddingAttribute.cs`
- Modify: `Iverson.Client/Iverson.Client.Attributes/IversonChunkAttribute.cs`
- Modify: `Iverson.Client/Iverson.Client.Sample/Models/Article.cs`
- Modify: `Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs`
- Modify: `Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

**Interfaces:**
- Produces: `[IversonEmbedding]` (no args), `[IversonChunk(maxTokens, overlap)]` (no model/dim)
- Produces: `SchemaRegistrar.AddAnnotations` sends `IsEmbedding=true, VectorDim=0, ModelId=""` and `IsChunk=true, ChunkMaxTokens, ChunkOverlap, ChunkModelId="", ChunkVectorDim=0`

- [ ] **Step 1: Write the failing test** — update `SchemaTestArticle` fixture and assertion in `SchemaRegistrarTests.cs`

In `Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`:

Change the `SchemaTestArticle` class at the top of the file — replace:
```csharp
[IversonEmbedding(EmbeddingModel.AllMiniLmL6V2)]
public string Body { get; set; } = string.Empty;
```
with:
```csharp
[IversonEmbedding]
public string Body { get; set; } = string.Empty;
```

In the `RegisterAllAsync_AppliesEmbeddingAnnotation_OnMarkedProperty` test, change:
```csharp
bodyProp.VectorDim.Should().BeGreaterThan(0);
```
to:
```csharp
bodyProp.IsEmbedding.Should().BeTrue();
bodyProp.VectorDim.Should().Be(0);
bodyProp.ModelId.Should().BeEmpty();
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Iverson.Client/Iverson.Client.Core.Tests/ -v q
```
Expected: compilation error — `EmbeddingModel` not found in `SchemaTestArticle`.

- [ ] **Step 3: Delete enum files**

```bash
git rm Iverson.Client/Iverson.Client.Attributes/EmbeddingModel.cs
git rm Iverson.Client/Iverson.Client.Attributes/EmbeddingModelExtensions.cs
```

- [ ] **Step 4: Replace `IversonEmbeddingAttribute.cs`**

Full file content:
```csharp
namespace Iverson.Client.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonEmbeddingAttribute : Attribute { }
```

- [ ] **Step 5: Replace `IversonChunkAttribute.cs`**

Full file content:
```csharp
namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as a source for chunk-level vector embeddings.
/// The server splits the field value into overlapping windows and stores each
/// chunk as a separate Qdrant point in a {collection}_chunks collection,
/// enabling passage-level RAG retrieval.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonChunkAttribute(int maxTokens = 512, int overlap = 64) : Attribute
{
    public int MaxTokens { get; } = maxTokens;
    public int Overlap   { get; } = overlap;
}
```

- [ ] **Step 6: Update `Article.cs`**

In `Iverson.Client/Iverson.Client.Sample/Models/Article.cs`, replace:
```csharp
[IversonEmbedding(EmbeddingModel.OpenAiTextEmbedding3Small)]
public string    Title       { get; set; } = string.Empty;

[IversonChunk(EmbeddingModel.OpenAiTextEmbedding3Small, maxTokens: 512, overlap: 64)]
public string    Body        { get; set; } = string.Empty;
```
with:
```csharp
[IversonEmbedding]
public string    Title       { get; set; } = string.Empty;

[IversonChunk(maxTokens: 512, overlap: 64)]
public string    Body        { get; set; } = string.Empty;
```

- [ ] **Step 7: Update `SchemaRegistrar.AddAnnotations`**

In `Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs`, replace the entire `AddAnnotations` method:
```csharp
private static void AddAnnotations(PropertyDescriptor pd, PropertyInfo prop)
{
    if (prop.GetCustomAttribute<IversonEmbeddingAttribute>() is not null)
    {
        pd.IsEmbedding = true;
        pd.VectorDim   = 0;
        pd.ModelId     = string.Empty;
    }

    if (prop.GetCustomAttribute<IversonChunkAttribute>() is { } chunk)
    {
        pd.IsChunk        = true;
        pd.ChunkMaxTokens = chunk.MaxTokens;
        pd.ChunkOverlap   = chunk.Overlap;
        pd.ChunkModelId   = string.Empty;
        pd.ChunkVectorDim = 0;
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

```bash
dotnet test Iverson.Client/Iverson.Client.Core.Tests/ -v q
```
Expected: all 8 tests pass.

- [ ] **Step 9: Commit**

```bash
git add Iverson.Client/Iverson.Client.Attributes/IversonEmbeddingAttribute.cs \
        Iverson.Client/Iverson.Client.Attributes/IversonChunkAttribute.cs \
        Iverson.Client/Iverson.Client.Sample/Models/Article.cs \
        Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs \
        Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs
git commit -m "feat: simplify IversonEmbedding/IversonChunk attributes — remove EmbeddingModel enum"
```

---

### Task 2: Refactor IEmbeddingService and EmbeddingService

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs`
- Delete: `Iverson.Server/Iverson.Embeddings/OpenAiEmbeddingService.cs`
- Create: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Delete: `Iverson.Server/Iverson.Embeddings.Tests/OpenAiEmbeddingServiceTests.cs`
- Create: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 (server-side only)
- Produces:
  ```csharp
  public interface IEmbeddingService
  {
      int    Dimension { get; }   // throws InvalidOperationException before InitializeAsync
      string ModelId   { get; }   // reads from EmbeddingServiceOptions, always safe
      Task   InitializeAsync(CancellationToken ct = default);
      Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: Write the failing tests** — create `EmbeddingServiceTests.cs`

Create `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Iverson.Server/Iverson.Embeddings.Tests/ -v q
```
Expected: compilation error — `EmbeddingService` not found.

- [ ] **Step 3: Update `IEmbeddingService.cs`**

Full file content:
```csharp
namespace Iverson.Embeddings;

public interface IEmbeddingService
{
    int    Dimension { get; }
    string ModelId   { get; }
    Task   InitializeAsync(CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
```

- [ ] **Step 4: Update `EmbeddingServiceOptions.cs`**

Full file content:
```csharp
namespace Iverson.Embeddings;

public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ModelId { get; set; } = "nomic-embed-text";
}
```

- [ ] **Step 5: Create `EmbeddingService.cs`**

Full file content:
```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingServiceOptions> opts,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private int _dimension;

    public int Dimension => _dimension > 0
        ? _dimension
        : throw new InvalidOperationException(
            "EmbeddingService not initialized — call InitializeAsync first.");

    public string ModelId => opts.Value.ModelId;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var probe = await EmbedAsync("probe", ct);
        _dimension = probe.Length;
        logger.LogInformation(
            "EmbeddingService initialized: model={Model} dimension={Dimension}",
            ModelId, _dimension);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        using var activity = Telemetry.Source.StartActivity("embeddings.embed", ActivityKind.Client);
        activity?.SetTag("embedding.model", ModelId);
        activity?.SetTag("embedding.input_chars", text.Length);

        try
        {
            using var http = httpClientFactory.CreateClient(Telemetry.HttpClientName);

            var body = JsonSerializer.Serialize(
                new { model = ModelId, input = text }, _jsonOpts);
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await http.SendAsync(msg, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc    = JsonDocument.Parse(responseJson);

            var embedding = doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding")
                .EnumerateArray()
                .Select(e => (float)e.GetDouble())
                .ToArray();

            activity?.SetTag("embedding.output_dims", embedding.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return embedding;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "EmbedAsync failed for model {Model}", ModelId);
            throw;
        }
    }
}
```

- [ ] **Step 6: Update `ServiceCollectionExtensions.cs`**

Replace `OpenAiEmbeddingService` with `EmbeddingService`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Embeddings;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmbeddingServiceOptions>(config.GetSection(EmbeddingServiceOptions.Section));

        services.AddHttpClient(Telemetry.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<EmbeddingServiceOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
        });

        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        return services;
    }
}
```

- [ ] **Step 7: Delete `OpenAiEmbeddingService.cs` and its test file**

```bash
git rm Iverson.Server/Iverson.Embeddings/OpenAiEmbeddingService.cs
git rm Iverson.Server/Iverson.Embeddings.Tests/OpenAiEmbeddingServiceTests.cs
```

- [ ] **Step 8: Run embedding tests to verify they pass**

```bash
dotnet test Iverson.Server/Iverson.Embeddings.Tests/ -v q
```
Expected: all 8 tests pass.

- [ ] **Step 9: Fix `IntelligenceStoreConsumer.cs` — drop modelId from EmbedAsync calls**

In `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`:

Change line ~81:
```csharp
namedVectors[$"{ToSnakeCase(vf.PropertyName)}_vector"] =
    await embedding.EmbedAsync(text, vf.ModelId, ct);
```
to:
```csharp
namedVectors[$"{ToSnakeCase(vf.PropertyName)}_vector"] =
    await embedding.EmbedAsync(text, ct);
```

Change line ~124:
```csharp
var chunkVector = await embedding.EmbedAsync(chunkText, cf.ModelId, ct);
```
to:
```csharp
var chunkVector = await embedding.EmbedAsync(chunkText, ct);
```

- [ ] **Step 10: Fix `IntelligenceStoreConsumerTests.cs` — update mock setup and Received() calls**

In `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`:

In the constructor, replace:
```csharp
_embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new float[1536]);
```
with:
```csharp
_embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new float[768]);
```

In `HandleCreated_WithVectorField_CallsEmbedAndUpsertNamed`, replace:
```csharp
var fakeVector = new float[1536];
_embedding.EmbedAsync("Great Title", "text-embedding-3-small", Arg.Any<CancellationToken>())
          .Returns(fakeVector);
```
with:
```csharp
var fakeVector = new float[768];
_embedding.EmbedAsync("Great Title", Arg.Any<CancellationToken>())
          .Returns(fakeVector);
```

And replace the Received() check:
```csharp
await _embedding.Received().EmbedAsync(
    "Great Title",
    "text-embedding-3-small",
    Arg.Any<CancellationToken>());
```
with:
```csharp
_ = _embedding.Received().EmbedAsync(
    "Great Title",
    Arg.Any<CancellationToken>());
```

In `SkipsEvent_WhenNoIntelligenceFlag`, replace:
```csharp
await _embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
```
with:
```csharp
_ = _embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
```

In `SkipsEmptyTextField_DoesNotCallEmbed`, replace:
```csharp
await _embedding.DidNotReceive().EmbedAsync(
    "",
    "text-embedding-3-small",
    Arg.Any<CancellationToken>());
```
with:
```csharp
_ = _embedding.DidNotReceive().EmbedAsync("", Arg.Any<CancellationToken>());
```

In `EmbedFailure_DoesNotPropagate`, replace:
```csharp
_embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns<float[]>(_ => throw new Exception("OpenAI timeout"));
```
with:
```csharp
_embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns<float[]>(_ => throw new Exception("Ollama timeout"));
```

- [ ] **Step 11: Run API tests to verify they pass**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/ -v q
```
Expected: all existing tests pass (ObjectMappingGrpcServiceTests constructor still compiles — `IEmbeddingService` is not yet injected there).

- [ ] **Step 12: Commit**

```bash
git add Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs \
        Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs \
        Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs \
        Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "feat: replace OpenAiEmbeddingService with Ollama-backed EmbeddingService"
```

---

### Task 3: Inject IEmbeddingService into ObjectMappingGrpcService

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`

**Interfaces:**
- Consumes: `IEmbeddingService.Dimension` (int), `IEmbeddingService.ModelId` (string) from Task 2
- Produces: `VectorDescriptor(propName, embeddingService.Dimension, embeddingService.ModelId)` and `ChunkDescriptor(propName, maxTokens, overlap, embeddingService.ModelId, embeddingService.Dimension)` in `BuildDescriptor`

- [ ] **Step 1: Write the failing test** — add new RegisterSchema test to `ObjectMappingGrpcServiceTests.cs`

In the test constructor, add `_embedding` field and setup (after the existing field declarations):
```csharp
private readonly IEmbeddingService _embedding;
```

In the constructor body, after the existing substitutes:
```csharp
_embedding = Substitute.For<IEmbeddingService>();
_embedding.Dimension.Returns(768);
_embedding.ModelId.Returns("nomic-embed-text");
```

Change the `_sut` construction from:
```csharp
_sut = new ObjectMappingGrpcService(
    _sql, _es, _vector, _events, _registry,
    NullLogger<ObjectMappingGrpcService>.Instance);
```
to:
```csharp
_sut = new ObjectMappingGrpcService(
    _sql, _es, _vector, _events, _registry, _embedding,
    NullLogger<ObjectMappingGrpcService>.Instance);
```

Add this new test method:
```csharp
[Fact]
public async Task RegisterSchema_SetsVectorDimAndModelId_FromEmbeddingService()
{
    var typeDesc = new TypeDescriptor { TypeName = "EmbeddableDoc" };
    typeDesc.Properties.Add(new PropertyDescriptor
    {
        Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true
    });
    typeDesc.Properties.Add(new PropertyDescriptor
    {
        Name    = "Content",
        ClrType = ClrType.ClrString,
        IsEmbedding = true,
        VectorDim   = 0,
        ModelId     = string.Empty
    });

    var request  = new SchemaRequest { RootType = typeDesc };
    var response = await _sut.RegisterSchema(request, Substitute.For<ServerCallContext>());

    response.Success.Should().BeTrue();
    var schema = _registry.Get("EmbeddableDoc")!;
    schema.VectorFields.Should().ContainSingle();
    schema.VectorFields[0].Dimension.Should().Be(768);
    schema.VectorFields[0].ModelId.Should().Be("nomic-embed-text");
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/ --filter "RegisterSchema_SetsVectorDimAndModelId" -v q
```
Expected: compilation error — `ObjectMappingGrpcService` constructor does not accept 7 arguments.

- [ ] **Step 3: Add `IEmbeddingService` to `ObjectMappingGrpcService` constructor**

In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, add `using Iverson.Embeddings;` to the using directives, then change the primary constructor from:
```csharp
public sealed class ObjectMappingGrpcService(
    IPostgresRepository _sql,
    IElasticsearchService _es,
    IVectorService _vector,
    IEventProducer events,
    SchemaRegistry registry,
    ILogger<ObjectMappingGrpcService> logger)
```
to:
```csharp
public sealed class ObjectMappingGrpcService(
    IPostgresRepository _sql,
    IElasticsearchService _es,
    IVectorService _vector,
    IEventProducer events,
    SchemaRegistry registry,
    IEmbeddingService _embedding,
    ILogger<ObjectMappingGrpcService> logger)
```

- [ ] **Step 4: Pass `_embedding` to `BuildDescriptor` and update VectorDescriptor/ChunkDescriptor construction**

In `RegisterSchema`, change:
```csharp
var descriptor = BuildDescriptor(typeDesc);
```
to:
```csharp
var descriptor = BuildDescriptor(typeDesc, _embedding);
```

Change the `BuildDescriptor` signature from:
```csharp
private static SchemaDescriptor BuildDescriptor(TypeDescriptor typeDesc)
```
to:
```csharp
private static SchemaDescriptor BuildDescriptor(TypeDescriptor typeDesc, IEmbeddingService embedding)
```

Inside `BuildDescriptor`, change:
```csharp
if (prop.IsEmbedding)
    vectors.Add(new VectorDescriptor(prop.Name, prop.VectorDim, prop.ModelId));

if (prop.IsChunk)
    chunks.Add(new ChunkDescriptor(prop.Name, prop.ChunkMaxTokens, prop.ChunkOverlap, prop.ChunkModelId, prop.ChunkVectorDim));
```
to:
```csharp
if (prop.IsEmbedding)
    vectors.Add(new VectorDescriptor(prop.Name, embedding.Dimension, embedding.ModelId));

if (prop.IsChunk)
    chunks.Add(new ChunkDescriptor(prop.Name, prop.ChunkMaxTokens, prop.ChunkOverlap, embedding.ModelId, embedding.Dimension));
```

- [ ] **Step 5: Update `SchemaFixtures.cs` to use 768/"nomic-embed-text"**

In `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, in `ArticleSchema()`, change:
```csharp
VectorFields   = [new VectorDescriptor("Title", 1536, "text-embedding-3-small")],
ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "text-embedding-3-small", 1536)],
```
to:
```csharp
VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
```

- [ ] **Step 6: Run tests to verify they all pass**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/ -v q
```
Expected: all tests pass, including the new `RegisterSchema_SetsVectorDimAndModelId_FromEmbeddingService`.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs
git commit -m "feat: inject IEmbeddingService into ObjectMappingGrpcService to fill vector dim and model id"
```

---

### Task 4: Wire startup probe in Program.cs

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Program.cs`

**Interfaces:**
- Consumes: `IEmbeddingService.InitializeAsync()` from Task 2

- [ ] **Step 1: Add `InitializeAsync` call before `SchemaRegistry.LoadAsync`**

In `Iverson.Server/Iverson.Api/Program.cs`, find:
```csharp
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();
```
and prepend:
```csharp
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();
```

Add `using Iverson.Embeddings;` to the using directives if not already present (it's already added transitively via `AddEmbeddings`, but the explicit using is needed for the `IEmbeddingService` type reference).

- [ ] **Step 2: Verify it builds**

```bash
dotnet build Iverson.Server/Iverson.Api/ -v q
```
Expected: build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add Iverson.Server/Iverson.Api/Program.cs
git commit -m "feat: probe Ollama at startup to discover embedding dimension"
```

---

### Task 5: Add Ollama to docker-compose and Launcher

**Files:**
- Modify: `Iverson.Server/docker-compose.yml`
- Modify: `Iverson.Server/Iverson.Launcher/Program.cs`

**Interfaces:**
- No code interfaces — infrastructure only

- [ ] **Step 1: Update `docker-compose.yml`**

Add the following two services after the `qdrant` service block (before `zookeeper`):
```yaml
  ollama:
    image: ollama/ollama:latest
    container_name: iverson-ollama
    restart: unless-stopped
    ports:
      - "11434:11434"
    volumes:
      - ollama_data:/root/.ollama
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:11434/api/tags || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10

  ollama-init:
    image: curlimages/curl:latest
    container_name: iverson-ollama-init
    depends_on:
      ollama:
        condition: service_healthy
    command: ["-X", "POST", "http://ollama:11434/api/pull", "-d", "{\"name\":\"nomic-embed-text\"}"]
    restart: "no"
```

In the `iverson-api` service, add to `depends_on`:
```yaml
      ollama-init:
        condition: service_completed_successfully
```

In the `iverson-api` service, add to `environment`:
```yaml
      - Embeddings__BaseUrl=http://ollama:11434
      - Embeddings__ModelId=nomic-embed-text
```

At the bottom in the `volumes:` block, add:
```yaml
  ollama_data:
```

- [ ] **Step 2: Update `Iverson.Launcher/Program.cs`**

Change the `docker compose up` command from:
```csharp
await RunCommandAsync("docker", "compose up -d postgres elasticsearch qdrant kafka zookeeper jaeger", solutionRoot, cts.Token);
```
to:
```csharp
await RunCommandAsync("docker", "compose up -d postgres elasticsearch qdrant kafka zookeeper jaeger ollama ollama-init", solutionRoot, cts.Token);
```

After `await WaitForPortAsync("localhost", 6333, "Qdrant", cts.Token);`, add:
```csharp
await WaitForOllamaAsync("http://localhost:11434/api/tags", cts.Token);
```

Add this static method alongside the other Wait* methods:
```csharp
static async Task WaitForOllamaAsync(string url, CancellationToken ct)
{
    Console.Write($"[Launcher] Waiting for Ollama at {url}");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine(" ready.");
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        Console.Write(".");
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }
    }
}
```

- [ ] **Step 3: Verify the Launcher builds**

```bash
dotnet build Iverson.Server/Iverson.Launcher/ -v q
```
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/docker-compose.yml \
        Iverson.Server/Iverson.Launcher/Program.cs
git commit -m "feat: add Ollama to docker-compose and wait for it in Launcher"
```
