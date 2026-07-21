using Iverson.Embeddings;
using Iverson.Sql;

namespace Iverson.Api.Tests.Helpers;

// Program.cs runs a handful of awaited "schema hydration" calls between builder.Build()
// and app.Run() — IEmbeddingService.InitializeAsync(), SchemaRegistry.LoadAsync() (which
// calls ISchemaRegistryRepository), IRecordStoreSchemaManager.ApplySchemaAsync() calls, and
// (since Task 2 of the tenant-admin-apis plan) a legacy-tenant seeding loop over
// ITenantRepository.SeedIfMissingAsync(). WebApplicationFactory<Program> re-runs that exact
// code path on every test host start, and all of it talks to real infra (Ollama, Postgres)
// that isn't available in this sandbox. These no-op fakes stand in for that infra so the
// host can boot without it; they are wired in by AuthTestWebApplicationFactory and are not
// meant to validate hydration behavior itself (that's out of scope for the auth-pipeline
// tests they support).

internal sealed class NoOpEmbeddingService : IEmbeddingService
{
    public int Dimension => 4;
    public string ModelId => "test-noop";
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(new float[4]);
}

internal sealed class NoOpSchemaRegistryRepository : ISchemaRegistryRepository
{
    public Task EnsureTableAsync() => Task.CompletedTask;

    public Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync() =>
        Task.FromResult(Enumerable.Empty<(string TypeName, string SchemaJson)>());

    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;
}

internal sealed class NoOpRecordStoreSchemaManager : IRecordStoreSchemaManager
{
    public Task ApplySchemaAsync(TableSchema schema) => Task.CompletedTask;
    public Task EnsureRuntimeRoleAsync() => Task.CompletedTask;
}

internal sealed class NoOpTenantRepository : ITenantRepository
{
    public Task InsertAsync(string id, string displayName, string status) => Task.CompletedTask;
    public Task SeedIfMissingAsync(string id, string displayName, string status) => Task.CompletedTask;
    public Task<TenantRow?> GetAsync(string id) => Task.FromResult<TenantRow?>(null);
    public Task<IEnumerable<TenantRow>> ListAsync() => Task.FromResult(Enumerable.Empty<TenantRow>());
    public Task UpdateStatusAsync(string id, string status) => Task.CompletedTask;
    public Task DeleteAsync(string id) => Task.CompletedTask;
}
