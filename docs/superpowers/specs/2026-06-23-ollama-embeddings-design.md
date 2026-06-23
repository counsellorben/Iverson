# Ollama Embeddings Design

**Date:** 2026-06-23  
**Status:** Approved

## Goal

Replace the `EmbeddingModel` enum with a fully server-side Ollama integration. Client attributes become pure markers with no model or dimension knowledge. All embedding calls route to a local Ollama instance in docker-compose running `nomic-embed-text` (768 dims).

## Motivation

- Eliminates dependency on external paid providers (OpenAI, Cohere, etc.)
- Removes model/dimension knowledge from the client library — clients don't need to know or track which embedding model the server uses
- Fully self-contained local dev stack via docker-compose

---

## Architecture

### Startup Sequence

1. Launcher runs `docker compose up` including `ollama` and `ollama-init`
2. `ollama-init` pulls `nomic-embed-text` and exits; `iverson-api` depends on `ollama-init` completing successfully
3. Launcher waits for Ollama on port 11434 before starting `Iverson.Api`
4. `Iverson.Api` calls `IEmbeddingService.InitializeAsync()` — one probe embed; the returned vector length is cached as `Dimension`
5. `SchemaRegistry.LoadAsync()` runs as today

### Schema Registration Flow

1. Client sends `RegisterSchema` with `IsEmbedding = true`, `VectorDim = 0`, `ModelId = ""`
2. `ObjectMappingGrpcService` (now injected with `IEmbeddingService`) fills in `Dimension` and `ModelId` from the service
3. `VectorDescriptor` and `ChunkDescriptor` are built with server-supplied values; all downstream consumers (Qdrant collection creation, `IntelligenceStoreConsumer`) are unchanged

### Embedding Call Flow

- `IEmbeddingService.EmbedAsync(text)` — model ID is no longer a parameter
- HTTP POST to `{BaseUrl}/v1/embeddings` (Ollama's OpenAI-compatible endpoint)
- No `Authorization` header

---

## Component Changes

### `Iverson.Client.Attributes`

- **Delete** `EmbeddingModel.cs` and `EmbeddingModelExtensions.cs`
- **`IversonEmbeddingAttribute`** — remove `Model` and `Dimension` properties; becomes a zero-arg marker:
  ```csharp
  [AttributeUsage(AttributeTargets.Property, Inherited = false)]
  public sealed class IversonEmbeddingAttribute : Attribute { }
  ```
- **`IversonChunkAttribute`** — remove `Model` and `Dimension`; keep `MaxTokens` (default 512) and `Overlap` (default 64):
  ```csharp
  public sealed class IversonChunkAttribute(int maxTokens = 512, int overlap = 64) : Attribute
  {
      public int MaxTokens { get; } = maxTokens;
      public int Overlap   { get; } = overlap;
  }
  ```

### `Iverson.Client.Core` — `SchemaRegistrar`

`AddAnnotations` changes:
- `IversonEmbeddingAttribute` present → set `IsEmbedding = true`; leave `VectorDim = 0` and `ModelId = ""`
- `IversonChunkAttribute` present → set `IsChunk = true`, `ChunkMaxTokens`, `ChunkOverlap`; leave `ChunkModelId = ""` and `ChunkVectorDim = 0`

### `Iverson.Embeddings`

**`EmbeddingServiceOptions`**
```csharp
public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ModelId { get; set; } = "nomic-embed-text";
}
```

**`IEmbeddingService`**
```csharp
public interface IEmbeddingService
{
    int    Dimension { get; }
    string ModelId   { get; }
    Task   InitializeAsync(CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
```
`Dimension` throws `InvalidOperationException` if accessed before `InitializeAsync` completes. `ModelId` reads directly from options and is always safe to access.

**`EmbeddingService`** (renamed from `OpenAiEmbeddingService`)
- `ModelId` comes from `EmbeddingServiceOptions.ModelId`
- `InitializeAsync` makes one `EmbedAsync("probe")` call and sets `_dimension = result.Length`
- `EmbedAsync` no longer takes a `modelId` parameter; uses `ModelId` from options
- No `Authorization` header

### `Iverson.Api` — `ObjectMappingGrpcService`

- Inject `IEmbeddingService`
- For `IsEmbedding = true` properties: `new VectorDescriptor(propName, embeddingService.Dimension, embeddingService.ModelId)`
- For `IsChunk = true` properties: `new ChunkDescriptor(propName, maxTokens, overlap, embeddingService.ModelId, embeddingService.Dimension)`

### `Iverson.Api` — `Program.cs`

Add before `SchemaRegistry.LoadAsync()`:
```csharp
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
```

### `Iverson.Launcher/Program.cs`

- `docker compose up` gains `ollama ollama-init` in the service list
- Add `WaitForOllamaAsync("http://localhost:11434/api/tags", cts.Token)` after the Qdrant wait
- Insert before `Iverson.Api` is started

### `docker-compose.yml`

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

`iverson-api` gains:
```yaml
depends_on:
  ollama-init:
    condition: service_completed_successfully
environment:
  - Embeddings__BaseUrl=http://ollama:11434
  - Embeddings__ModelId=nomic-embed-text
```

`volumes:` gains `ollama_data:`.

### `Iverson.Client.Sample` — `Article.cs`

```csharp
[IversonEmbedding]
public string Title { get; set; } = string.Empty;

[IversonChunk(maxTokens: 512, overlap: 64)]
public string Body { get; set; } = string.Empty;
```

---

## Tests

| File | Change |
|---|---|
| `OpenAiEmbeddingServiceTests.cs` → `EmbeddingServiceTests.cs` | Rename; remove auth header assertions; drop `modelId` param from `EmbedAsync` calls; add `InitializeAsync` test (sets `Dimension`); assert no `Authorization` header sent |
| `SchemaRegistrarTests.cs` | Remove model/dim assertions on embedding properties; assert `IsEmbedding = true`, `VectorDim = 0`, `ModelId = ""` |
| `ObjectMappingGrpcServiceTests.cs` | Mock `IEmbeddingService` returning `Dimension = 768`, `ModelId = "nomic-embed-text"`; assert `VectorDescriptor` is built with these values |
| `SchemaFixtures.cs` | Remove all `EmbeddingModel` references |

---

## What Does Not Change

- `IVectorService` / Qdrant integration — dimension flows in via `VectorDescriptor` as before
- `SchemaDescriptor` / `VectorDescriptor` / `ChunkDescriptor` shapes — unchanged
- ES indexing path — unaffected
- All other gRPC services

## Minor Call-Site Changes (not listed above)

- `IntelligenceStoreConsumer` — drop the `modelId` argument from `EmbedAsync` calls; no other logic change
