using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
// Iverson.Client.Contracts declares its own AggregationResult (the proto message); this is
// the StarRocks domain record IEngagementStoreSearchService actually returns.
using EngagementAggResult = Iverson.StarRocks.AggregationResult;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// The host <c>AdminConsoleEndpointsPipelineTests</c> drives. Derives from
/// <see cref="AuthTestWebApplicationFactory"/> for its NoOp startup infra (see that file) and
/// swaps four more registrations, each for the same reason: the four
/// <c>/admin/console/*</c> endpoints are the first ones in this suite that run all the way
/// through to a real backing service, so those services have to answer without real
/// Postgres / StarRocks / Qdrant.
/// <list type="bullet">
/// <item><see cref="ISchemaRegistryRepository"/> — seeds three real descriptors, so the schema
/// catalog and the per-type counts have something to filter. A repository, not
/// <c>SchemaRegistry.RegisterAsync</c>: <c>SchemaRefreshWorker</c> polls every 30 s and
/// <c>LoadAsync</c>'s reconcile loop EVICTS any cached type the repository does not return, so a
/// registry seeded in memory only would empty itself mid-run.</item>
/// <item><see cref="ITenantRepository"/> — two rows, replacing the base factory's empty NoOp.</item>
/// <item><see cref="IEngagementStoreSearchService"/> — fixed counts per type name.</item>
/// <item><see cref="IVectorCollectionReader"/> — two collections.</item>
/// </list>
/// <para>
/// No environment variable is set here, deliberately: the two CORS factories document why a
/// process-global env var is the one piece of test setup that cannot be made race-free across
/// xunit's parallel collections, and everything this factory needs can be expressed as a service
/// swap instead.
/// </para>
/// </summary>
public sealed class AdminConsoleTestWebApplicationFactory : AuthTestWebApplicationFactory
{
    /// <summary>The group both visible fixtures grant full read access to.</summary>
    public const string ReaderGroup = "console-readers";
    /// <summary>The group the denied fixture grants read access to, and no test token carries.</summary>
    public const string LedgerGroup = "ledger-admins";

    public const string VisibleTypeWithZeroRows = "Author";
    public const string VisibleTypeWithRows     = "Article";
    /// <summary>Denied to every principal these tests use — its only row permission is <see cref="LedgerGroup"/>.</summary>
    public const string DeniedType              = "Ledger";

    public const long VisibleTypeRowCount = 42;

    public const string CollectionWithStats    = "iverson-articles";
    public const string CollectionWithoutStats = "iverson-unoptimized";
    /// <summary>
    /// How many collections Qdrant NAMED. Deliberately one more than the two whose stats the
    /// fake reader returns, so the shortfall the endpoint has to surface is non-zero in every
    /// test that reaches <c>/admin/console/qdrant</c>. A fixture where listed == read would let
    /// the endpoint compute <c>unreadableCollectionCount</c> from the wrong number and stay green.
    /// </summary>
    public const int ListedCollectionCount = 3;

    public static readonly TenantRow[] Tenants =
    [
        new("tenant_alpha", "Alpha", "active",       new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
        new("tenant_beta",  "Beta",  "provisioning", new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc))
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISchemaRegistryRepository>();
            services.AddSingleton<ISchemaRegistryRepository, AdminConsoleSchemaRegistryRepository>();

            services.RemoveAll<ITenantRepository>();
            services.AddSingleton<ITenantRepository, AdminConsoleTenantRepository>();

            services.RemoveAll<IEngagementStoreSearchService>();
            services.AddSingleton<IEngagementStoreSearchService, AdminConsoleSearchService>();

            services.RemoveAll<IVectorCollectionReader>();
            services.AddSingleton<IVectorCollectionReader, AdminConsoleVectorCollectionReader>();
        });
    }
}

/// <summary>
/// The three descriptors above, serialized exactly the way <c>SchemaRegistry.RegisterAsync</c>
/// writes them (camelCase) so <c>LoadAsync</c>'s real deserialization path is what admits them —
/// including its refusal of any descriptor with no tenant column.
/// </summary>
internal sealed class AdminConsoleSchemaRegistryRepository : ISchemaRegistryRepository
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Deliberately the legacy client-declared "TenantId" rather than the reserved
    // SchemaDescriptor.TenantColumnName: it is what SchemaFixtures uses throughout this assembly,
    // and what matters for these tests is only that the column is non-empty, since
    // RowFieldAuthorizationEvaluator denies outright when it is not.
    private const string TenantColumn = "TenantId";

    private static Iverson.Api.Schema.AuthorizationRules ReadableBy(string group) =>
        new(null, [new Iverson.Api.Schema.RowPermission(group, true, true, true)], []);

    internal static SchemaDescriptor Author() => new()
    {
        TypeName      = AdminConsoleTestWebApplicationFactory.VisibleTypeWithZeroRows,
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Bio", "text", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        Authorization = ReadableBy(AdminConsoleTestWebApplicationFactory.ReaderGroup),
        TenantColumn  = TenantColumn,
        Description   = "People who write things"
    };

    internal static SchemaDescriptor Article() => new()
    {
        TypeName      = AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows,
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title", "text", false),
            new ColumnDescriptor("Body", "text", false),
            new ColumnDescriptor("AuthorId", "uuid", false)
        ],
        FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [new Iverson.Api.Schema.RelationDescriptor("Author", Iverson.Api.Schema.RelationKind.ManyToOne, "Author", "AuthorId")],
        Authorization = ReadableBy(AdminConsoleTestWebApplicationFactory.ReaderGroup),
        TenantColumn  = TenantColumn
    };

    internal static SchemaDescriptor Ledger() => new()
    {
        TypeName      = AdminConsoleTestWebApplicationFactory.DeniedType,
        TableName     = "ledgers",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Amount", "text", false)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        Authorization = ReadableBy(AdminConsoleTestWebApplicationFactory.LedgerGroup),
        TenantColumn  = TenantColumn
    };

    internal static IEnumerable<SchemaDescriptor> All() => [Author(), Article(), Ledger()];

    public Task EnsureTableAsync() => Task.CompletedTask;

    public Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync() =>
        Task.FromResult(All().Select(d => (d.TypeName, JsonSerializer.Serialize(d, Options))));

    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;
}

internal sealed class AdminConsoleTenantRepository : ITenantRepository
{
    public Task InsertAsync(string id, string displayName, string status) => Task.CompletedTask;
    public Task SeedIfMissingAsync(string id, string displayName, string status) => Task.CompletedTask;
    public Task<TenantRow?> GetAsync(string id) => Task.FromResult<TenantRow?>(null);
    public Task<IEnumerable<TenantRow>> ListAsync() =>
        Task.FromResult<IEnumerable<TenantRow>>(AdminConsoleTestWebApplicationFactory.Tenants);
    public Task UpdateStatusAsync(string id, string status) => Task.CompletedTask;
    public Task DeleteAsync(string id) => Task.CompletedTask;
}

/// <summary>
/// Answers <c>AggregateAsync</c> with a fixed count per type name. Everything else throws:
/// nothing on the console's path calls them, and a silent empty answer would hide it if that
/// ever changed.
/// </summary>
internal sealed class AdminConsoleSearchService : IEngagementStoreSearchService
{
    public Task<EngagementAggResult?> AggregateAsync(
        EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
        SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var value = schema.TypeName switch
        {
            AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows =>
                (double)AdminConsoleTestWebApplicationFactory.VisibleTypeRowCount,
            _ => 0d
        };
        return Task.FromResult<EngagementAggResult?>(
            new EngagementAggResult(spec.Name, AggregationKind.Count, null, value));
    }

    public Task<IEnumerable<dynamic>> SearchAsync(
        EngagementQuerySchema schema, SearchQuery? query, int page, int pageSize,
        IReadOnlyList<string>? fields = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new NotSupportedException();

    public Task<IEnumerable<dynamic>> GroupByAsync(
        EngagementQuerySchema schema, GroupByRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new NotSupportedException();

    public Task<IEnumerable<dynamic>> PipelineAsync(
        EngagementQuerySchema schema, PipelineRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
        EngagementQuerySchema schema, MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Two collections read out of three listed.
/// <para>
/// One of the two reports no counts at all — the shape Qdrant really returns for a collection
/// that has never been optimized (both counts are proto3 optional). The third is the one whose
/// <c>GetCollectionInfoAsync</c> failed: <c>IntelligenceCollectionReader</c> contains that
/// failure and leaves the collection out of the list, but still reports it in
/// <see cref="VectorCollectionListing.ListedCount"/>, and the endpoint must turn that gap into
/// <c>unreadableCollectionCount</c> rather than letting the collection vanish.
/// </para>
/// </summary>
internal sealed class AdminConsoleVectorCollectionReader : IVectorCollectionReader
{
    public Task<VectorCollectionListing> ListCollectionStatsAsync(CancellationToken ct = default) =>
        Task.FromResult(new VectorCollectionListing(
            AdminConsoleTestWebApplicationFactory.ListedCollectionCount,
            [
                new VectorCollectionStats(AdminConsoleTestWebApplicationFactory.CollectionWithStats, 1234, 1200),
                new VectorCollectionStats(AdminConsoleTestWebApplicationFactory.CollectionWithoutStats, null, null)
            ]));
}
