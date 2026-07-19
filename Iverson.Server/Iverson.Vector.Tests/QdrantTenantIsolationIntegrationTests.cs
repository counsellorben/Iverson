using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

/// <summary>
/// Real-Qdrant-container proof that the tenant-scoped JWT mechanism (<see cref="QdrantTenantScope"/>)
/// actually enforces the isolation Tasks 1-3 wire into every call site: a JWT minted for exactly one
/// physical collection cannot read or write any other collection, a read-only JWT cannot write, and
/// the shared <see cref="QdrantClient"/> (constructed with no static credential, per Task 2) only
/// gets access via the per-call <c>RequestHeaders.Use("api-key", ...)</c> wrap that every production
/// call site (ObjectSearchGrpcService, IntelligenceStoreConsumer) now uses.
///
/// Unlike <see cref="QdrantIntegrationTests"/>'s container, this fixture enables
/// QDRANT__SERVICE__JWT_RBAC — matching the docker-compose/helm production configuration Task 1 added
/// — so these tests exercise the real access-control path, not just unauthenticated CRUD.
/// </summary>
public sealed class QdrantJwtRbacContainerFixture : IAsyncLifetime
{
    private const int GrpcPort = 6334;

    // HS256 requires a key of at least 256 bits (32 bytes); this doubles as both the plain
    // admin api-key (QdrantCollectionManager) and the JWT HMAC signing secret (QdrantTenantScope)
    // — exactly as docker-compose.yml's single QDRANT__SERVICE__API_KEY value serves both roles.
    public const string ApiKey = "test-signing-key-0123456789abcdef";

    private readonly DotNet.Testcontainers.Containers.IContainer _container =
        new ContainerBuilder()
            .WithImage("qdrant/qdrant:v1.18.2")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(6333,     assignRandomHostPort: true)
            .WithEnvironment("QDRANT__SERVICE__API_KEY", ApiKey)
            .WithEnvironment("QDRANT__SERVICE__JWT_RBAC", "true")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(GrpcPort))
            .Build();

    // Shared client with NO static credential — mirrors ServiceCollectionExtensions.AddQdrant's
    // `new QdrantClient(host, port, https: false, apiKey: null)` (Task 2). Every call in these
    // tests must supply its own credential via RequestHeaders.Use, exactly like production.
    public QdrantClient Client { get; private set; } = null!;
    public QdrantCollectionManager AdminCollectionManager { get; private set; } = null!;
    public QdrantVectorService Vector { get; private set; } = null!;
    public QdrantTenantScope TenantScope { get; } = new(ApiKey);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var host       = _container.Hostname;
        var mappedPort = _container.GetMappedPublicPort(GrpcPort);

        Client                 = new QdrantClient(host, mappedPort, https: false, apiKey: null);
        AdminCollectionManager = new QdrantCollectionManager(Client, ApiKey, NullLogger<QdrantCollectionManager>.Instance);
        Vector                 = new QdrantVectorService(Client);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class QdrantTenantIsolationIntegrationTests(QdrantJwtRbacContainerFixture fixture)
    : IClassFixture<QdrantJwtRbacContainerFixture>
{
    private readonly QdrantClient _client = fixture.Client;
    private readonly QdrantCollectionManager _admin = fixture.AdminCollectionManager;
    private readonly QdrantVectorService _vector = fixture.Vector;
    private readonly QdrantTenantScope _tenantScope = fixture.TenantScope;

    private static string UniqueBase() => "tn_" + Guid.NewGuid().ToString("N")[..8];

    private async Task CreateCollectionAsync(string name, ulong dim = 4) =>
        await _admin.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector("v", (int)dim)], []));

    // ── Cross-tenant isolation ───────────────────────────────────────────────

    [Fact]
    public async Task ScopedJwt_SameTenant_CanWriteThenReadItsOwnCollection()
    {
        var collection = _tenantScope.ResolveCollectionName(UniqueBase(), "tenant-a", isChunks: false);
        await CreateCollectionAsync(collection);

        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: false)))
        {
            await _vector.UpsertNamedAsync(collection, 1,
                new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] },
                new Dictionary<string, object> { ["label"] = "tenant-a-point" });
        }

        List<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: true)))
        {
            results = (await _vector.SearchNamedAsync(collection, "v", [1f, 0f, 0f, 0f], limit: 5)).ToList();
        }

        results.Should().ContainSingle(r => r.Id == 1UL && r.Payload["label"] == "tenant-a-point");
    }

    [Fact]
    public async Task ScopedJwt_CrossTenant_Write_IsForbidden()
    {
        var baseName = UniqueBase();
        var tenantACollection = _tenantScope.ResolveCollectionName(baseName, "tenant-a", isChunks: false);
        var tenantBCollection = _tenantScope.ResolveCollectionName(baseName, "tenant-b", isChunks: false);
        await CreateCollectionAsync(tenantACollection);
        await CreateCollectionAsync(tenantBCollection);

        // A JWT minted for tenant A's collection must not be able to write into tenant B's —
        // this is the core isolation guarantee every one of Task 3's 6 wrap points depends on.
        var act = async () =>
        {
            using var _ = RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantACollection, readOnly: false));
            await _vector.UpsertNamedAsync(tenantBCollection, 1, new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] });
        };

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task ScopedJwt_CrossTenant_Read_IsForbidden()
    {
        var baseName = UniqueBase();
        var tenantACollection = _tenantScope.ResolveCollectionName(baseName, "tenant-a", isChunks: false);
        var tenantBCollection = _tenantScope.ResolveCollectionName(baseName, "tenant-b", isChunks: false);
        await CreateCollectionAsync(tenantACollection);
        await CreateCollectionAsync(tenantBCollection);

        var act = async () =>
        {
            using var _ = RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantACollection, readOnly: true));
            await _vector.SearchNamedAsync(tenantBCollection, "v", [1f, 0f, 0f, 0f], limit: 5);
        };

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task ScopedJwt_ReadOnly_CannotWrite_ButCanRead()
    {
        var collection = _tenantScope.ResolveCollectionName(UniqueBase(), "tenant-a", isChunks: false);
        await CreateCollectionAsync(collection);

        // Seed with an rw token first so there's something a read-only token could legitimately see.
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: false)))
        {
            await _vector.UpsertNamedAsync(collection, 1, new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] });
        }

        var writeWithReadOnlyToken = async () =>
        {
            using var _ = RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: true));
            await _vector.UpsertNamedAsync(collection, 2, new Dictionary<string, float[]> { ["v"] = [0f, 1f, 0f, 0f] });
        };
        (await writeWithReadOnlyToken.Should().ThrowAsync<RpcException>())
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);

        List<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: true)))
        {
            results = (await _vector.SearchNamedAsync(collection, "v", [1f, 0f, 0f, 0f], limit: 5)).ToList();
        }
        results.Should().ContainSingle(r => r.Id == 1UL);
    }

    // ── Fail-closed sentinel path ────────────────────────────────────────────

    [Fact]
    public async Task WriteAgainstNeverCreatedCollection_Fails()
    {
        // Mirrors IntelligenceStoreConsumer's fail-closed carve-out: when authoritativeTenantValue
        // is null, EnsureCollectionAsync is deliberately skipped, so the sentinel collection this
        // resolves to was never created — the write must fail rather than silently succeed
        // somewhere unintended.
        var neverCreated = _tenantScope.ResolveCollectionName(UniqueBase(), null, isChunks: false);

        var act = async () =>
        {
            using var _ = RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(neverCreated, readOnly: false));
            await _vector.UpsertNamedAsync(neverCreated, 1, new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] });
        };

        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task ReadAgainstNeverCreatedCollection_Fails()
    {
        // Documents actual server behavior for a collection that was never created at all (a JWT
        // scoped to a collection name gives access to that name, but Qdrant still 404s the search
        // once past the access check — this is a real NotFound, not an empty result set). This is
        // distinct from ReadAgainstExistingButEmptyTenantCollection_ReturnsEmpty below, which is
        // the scenario that actually returns gracefully.
        var neverCreated = _tenantScope.ResolveCollectionName(UniqueBase(), null, isChunks: false);

        var act = async () =>
        {
            using var _ = RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(neverCreated, readOnly: true));
            await _vector.SearchNamedAsync(neverCreated, "v", [1f, 0f, 0f, 0f], limit: 5);
        };

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task ReadAgainstExistingButEmptyTenantCollection_ReturnsEmpty_NotError()
    {
        // A tenant whose collection has been lazily created (e.g. by an earlier write) but has no
        // matching points yet must see an empty result, not an error.
        var collection = _tenantScope.ResolveCollectionName(UniqueBase(), "tenant-fresh", isChunks: false);
        await CreateCollectionAsync(collection);

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: true)))
        {
            results = await _vector.SearchNamedAsync(collection, "v", [1f, 0f, 0f, 0f], limit: 5);
        }

        results.Should().BeEmpty();
    }

    // ── Lazy collection creation idempotency ─────────────────────────────────

    [Fact]
    public async Task LazyCollectionCreation_AppliedTwiceForSameTenant_IsIdempotent()
    {
        var collection = _tenantScope.ResolveCollectionName(UniqueBase(), "tenant-a", isChunks: false);
        var schema = new CollectionSchema(collection, [new NamedVector("v", 4)], []);

        await _admin.ApplyCollectionAsync(schema);
        var act = async () => await _admin.ApplyCollectionAsync(schema);

        await act.Should().NotThrowAsync();
    }

    // ── ApplyOwnership within a tenant-scoped collection ─────────────────────

    [Fact]
    public async Task ApplyOwnership_FiltersCorrectly_WithinTenantScopedCollection()
    {
        var collection = _tenantScope.ResolveCollectionName(UniqueBase(), "tenant-a", isChunks: false);
        await CreateCollectionAsync(collection);

        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: false)))
        {
            await _vector.UpsertNamedAsync(collection, 1,
                new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] },
                new Dictionary<string, object> { ["ownerId"] = "alice" });
            await _vector.UpsertNamedAsync(collection, 2,
                new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] },
                new Dictionary<string, object> { ["ownerId"] = "bob" });
        }

        var filter = QdrantFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "alice");

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(collection, readOnly: true)))
        {
            results = await _vector.SearchNamedAsync(collection, "v", [1f, 0f, 0f, 0f], limit: 10, filter);
        }

        results.Should().ContainSingle(r => r.Id == 1UL);
    }

    // ── Delete goes to the right tenant's collection ─────────────────────────

    [Fact]
    public async Task DeleteAsync_WithTenantScopedJwt_RemovesPointFromItsOwnCollection_LeavesOtherTenantsIdenticalIdUntouched()
    {
        // Same point ID (1) seeded into two different tenants' collections — proves a scoped
        // delete only ever reaches the collection its JWT names, matching HandleDeleteAsync's
        // per-event tenant-qualified collection resolution.
        var baseName = UniqueBase();
        var tenantACollection = _tenantScope.ResolveCollectionName(baseName, "tenant-a", isChunks: false);
        var tenantBCollection = _tenantScope.ResolveCollectionName(baseName, "tenant-b", isChunks: false);
        await CreateCollectionAsync(tenantACollection);
        await CreateCollectionAsync(tenantBCollection);

        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantACollection, readOnly: false)))
            await _vector.UpsertNamedAsync(tenantACollection, 1, new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] });
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantBCollection, readOnly: false)))
            await _vector.UpsertNamedAsync(tenantBCollection, 1, new Dictionary<string, float[]> { ["v"] = [1f, 0f, 0f, 0f] });

        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantACollection, readOnly: false)))
            await _vector.DeleteAsync(tenantACollection, 1);

        IReadOnlyList<VectorSearchResult> tenantAResults;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantACollection, readOnly: true)))
            tenantAResults = await _vector.SearchNamedAsync(tenantACollection, "v", [1f, 0f, 0f, 0f], limit: 5);
        tenantAResults.Should().BeEmpty();

        IReadOnlyList<VectorSearchResult> tenantBResults;
        using (RequestHeaders.Use("api-key", _tenantScope.MintScopedApiKey(tenantBCollection, readOnly: true)))
            tenantBResults = await _vector.SearchNamedAsync(tenantBCollection, "v", [1f, 0f, 0f, 0f], limit: 5);
        tenantBResults.Should().ContainSingle(r => r.Id == 1UL);
    }

    // ── Admin operations bypass jwt_rbac's per-collection scoping entirely ───

    [Fact]
    public async Task AdminCollectionManager_EnsureAndApplyCollectionAsync_StillSucceed_AgainstJwtRbacEnabledServer()
    {
        // Confirms Task 2 Step 5 (removing the shared QdrantClient's static admin key) didn't
        // silently break collection management: QdrantCollectionManager wraps its own calls in
        // RequestHeaders.Use(adminApiKey) internally, so admin ops need no caller-supplied header.
        var ensureAct = async () => await _admin.EnsureCollectionAsync(UniqueBase(), vectorSize: 4);
        await ensureAct.Should().NotThrowAsync();

        var applyAct = async () => await _admin.ApplyCollectionAsync(
            new CollectionSchema(UniqueBase(), [new NamedVector("v2", 4)], []));
        await applyAct.Should().NotThrowAsync();
    }
}
