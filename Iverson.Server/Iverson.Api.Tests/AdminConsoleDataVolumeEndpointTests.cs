using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Authorization;
using Iverson.Api.Console;
using Iverson.Api.Schema;
using Iverson.Api.Search;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.StarRocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// Iverson.Client.Contracts declares its own AggregationResult (the proto message); this is
// the StarRocks domain record IEngagementStoreSearchService actually returns.
using EngagementAggResult = Iverson.StarRocks.AggregationResult;

namespace Iverson.Api.Tests;

/// <summary>
/// The <c>/admin/console/data-volume</c> handler against the store conditions the pipeline tests
/// cannot stage.
/// <para>
/// <c>AggregateReader.CountRowsAsync</c> deliberately does NOT translate
/// <c>EngagementNotReadyException</c> or <c>EngagementStoreDisabledException</c> — it has no
/// transport to translate them into. The endpoint is that transport, so this is where the
/// translation is pinned. Both are live paths, not hypotheticals:
/// <c>DisabledEngagementStoreSearchService</c> throws unconditionally whenever
/// <c>Engagement__Enabled=false</c>, which <c>values-laptop.yaml</c> ships.
/// </para>
/// <para>
/// Driven at the handler rather than through a fifth <c>WebApplicationFactory</c> host on purpose.
/// The store's state is a whole-host property, so covering it in the pipeline would mean two more
/// booted hosts, or a mutable switch shared across an <c>IClassFixture</c> that would couple test
/// ordering. Neither buys anything: the authorization gate and the principal routing are already
/// pinned through the real pipeline by <c>AdminConsoleEndpointsPipelineTests</c>, and what is left
/// to verify here is exception-to-status translation, which is entirely inside this handler.
/// </para>
/// </summary>
public class AdminConsoleDataVolumeEndpointTests
{
    private static async Task<SchemaRegistry> SeededRegistryAsync()
    {
        var registry = new SchemaRegistry(
            new AdminConsoleSchemaRegistryRepository(), NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        return registry;
    }

    private static HttpContext ReaderContext() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", "tenant_alpha"),
            new Claim("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
        ], authenticationType: "test"))
    };

    private static async Task<IResult> InvokeAsync(IEngagementStoreSearchService search)
    {
        var registry = await SeededRegistryAsync();
        var reader = new AggregateReader(search, registry, new RowFieldAuthorizationEvaluator());
        return await AdminConsoleEndpoints.GetDataVolumeAsync(ReaderContext(), registry, reader);
    }

    [Fact]
    public async Task StarRocksNotReady_Returns503WithNotReadyReason()
    {
        var result = await InvokeAsync(
            new ThrowingSearchService(() => new EngagementNotReadyException("backends still starting")));

        var json = result.Should().BeOfType<JsonHttpResult<EngagementUnavailableResponse>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.Value!.Reason.Should().Be("notReady");
        json.Value.Error.Should().Contain("backends still starting");
    }

    [Fact]
    public async Task EngagementStoreDisabled_Returns503WithDisabledReason()
    {
        var result = await InvokeAsync(
            new ThrowingSearchService(() => new EngagementStoreDisabledException("not deployed in this instance")));

        var json = result.Should().BeOfType<JsonHttpResult<EngagementUnavailableResponse>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.Value!.Reason.Should().Be("disabled");
        json.Value.Error.Should().Be("not deployed in this instance");
    }

    /// <summary>
    /// The store returning <c>null</c> — no tenant database to address — is the one outcome
    /// <c>CountRowsAsync</c> folds to zero (<c>result?.MetricValue ?? 0d</c>). It reaches the wire
    /// as a genuine <c>counted</c> zero, which is correct: a tenant whose id cannot address a
    /// database genuinely has no rows. It must not be confused with denial, which is why the two
    /// have different shapes.
    /// </summary>
    [Fact]
    public async Task NullStoreResult_IsReportedAsACountedZero()
    {
        var result = await InvokeAsync(new NullReturningSearchService());

        var ok = result.Should().BeOfType<Ok<DataVolumeResponse>>().Subject;
        ok.Value!.Types.Should().HaveCount(2);
        ok.Value.Types.Should().OnlyContain(t => t.Status == "counted" && t.RowCount == 0);
        ok.Value.DeniedTypeCount.Should().Be(1);
    }

    private abstract class SearchServiceBase : IEngagementStoreSearchService
    {
        public abstract Task<EngagementAggResult?> AggregateAsync(
            EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
            SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
            Func<string, EngagementQuerySchema?>? registry = null,
            IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);

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

    private sealed class ThrowingSearchService(Func<Exception> factory) : SearchServiceBase
    {
        public override Task<EngagementAggResult?> AggregateAsync(
            EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
            SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
            Func<string, EngagementQuerySchema?>? registry = null,
            IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
            => throw factory();
    }

    private sealed class NullReturningSearchService : SearchServiceBase
    {
        public override Task<EngagementAggResult?> AggregateAsync(
            EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
            SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
            Func<string, EngagementQuerySchema?>? registry = null,
            IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
            => Task.FromResult<EngagementAggResult?>(null);
    }

    /// <summary>
    /// Another tenant's schema is ABSENT: no name, and no effect on any count. Folding it into
    /// <c>deniedTypeCount</c> (or <c>unknownTypeCount</c>) would tell any authenticated caller how
    /// many types other tenants own, and let it watch that number move. The expected response is
    /// the same caller's against the same registry minus the foreign schemas, not a constant. Two
    /// foreign schemas, one this caller's groups could read and one they could not.
    /// </summary>
    [Fact]
    public async Task ForeignTenantSchema_IsAbsentFromTheDataVolume()
    {
        var baseline = await DataVolumeAsync();
        var withForeign = await DataVolumeAsync(
            AdminConsoleSchemaRegistryRepository.Article() with
            {
                TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
            },
            AdminConsoleSchemaRegistryRepository.Ledger() with
            {
                TypeName = "ForeignLedger", OwnerTenantId = "tenant_beta"
            });

        var names = withForeign.Types.Select(t => t.TypeName).ToList();
        names.Should().Contain(AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        names.Should().NotContain("ForeignArticle");
        names.Should().NotContain("ForeignLedger");

        baseline.DeniedTypeCount.Should().BePositive(
            "the fixture must deny something of its own, or equal counts prove nothing");
        withForeign.DeniedTypeCount.Should().Be(baseline.DeniedTypeCount);
        withForeign.UnknownTypeCount.Should().Be(baseline.UnknownTypeCount);
        withForeign.Types.Should().HaveSameCount(baseline.Types);
        withForeign.Should().BeEquivalentTo(baseline);
    }

    /// <summary>
    /// The reader on its own, below the handler's pre-filter: a foreign schema answers exactly as
    /// an unregistered name does. <c>Denied</c> would claim the type exists and is merely closed to
    /// this caller, which is the distinction main's <c>677d85aa</c> removed as an existence oracle.
    /// </summary>
    [Fact]
    public async Task ForeignTenantSchema_CountRows_IsUnknownTypeNotDenied()
    {
        var registry = await SeededRegistryAsync();
        await registry.RegisterAsync(AdminConsoleSchemaRegistryRepository.Article() with
        {
            TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
        });
        var reader = new AggregateReader(
            new NullReturningSearchService(), registry, new RowFieldAuthorizationEvaluator());

        var foreign = await reader.CountRowsAsync("ForeignArticle", ReaderContext().User);
        var unregistered = await reader.CountRowsAsync("NoSuchType", ReaderContext().User);

        foreign.Should().Be(TypeRowCount.UnknownType);
        foreign.Should().Be(unregistered);
    }

    private static async Task<DataVolumeResponse> DataVolumeAsync(params SchemaDescriptor[] extra)
    {
        var registry = await SeededRegistryAsync();
        foreach (var schema in extra)
            await registry.RegisterAsync(schema);
        var reader = new AggregateReader(
            new NullReturningSearchService(), registry, new RowFieldAuthorizationEvaluator());

        var result = await AdminConsoleEndpoints.GetDataVolumeAsync(ReaderContext(), registry, reader);

        return result.Should().BeOfType<Ok<DataVolumeResponse>>().Subject.Value!;
    }

    [Fact]
    public async Task OwnTenantSchema_IsEnumeratedByName()
    {
        var registry = await SeededRegistryAsync();
        await registry.RegisterAsync(AdminConsoleSchemaRegistryRepository.Article() with
        {
            TypeName = "OwnedArticle", OwnerTenantId = "tenant_alpha"
        });
        var reader = new AggregateReader(
            new NullReturningSearchService(), registry, new RowFieldAuthorizationEvaluator());

        var result = await AdminConsoleEndpoints.GetDataVolumeAsync(ReaderContext(), registry, reader);

        var names = result.Should().BeOfType<Ok<DataVolumeResponse>>().Subject.Value!.Types
            .Select(t => t.TypeName).ToList();
        names.Should().Contain("OwnedArticle");
    }
}
