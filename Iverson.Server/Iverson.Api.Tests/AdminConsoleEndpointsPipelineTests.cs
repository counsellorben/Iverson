using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// Task 6's four <c>/admin/console/*</c> endpoints, driven through the real <c>Program.cs</c>
/// pipeline (<see cref="AdminConsoleTestWebApplicationFactory"/>) so the real JwtBearer handler,
/// the real <c>Operator</c> policy, the real <c>FallbackPolicy</c> and the real minimal-API JSON
/// serialization all run — the same pattern as <c>ProbeAuthorizationPipelineTests</c> and
/// <c>AdminConsoleCorsPipelineTests</c>.
/// <para>
/// Three things are pinned here that a shape-only test would miss:
/// </para>
/// <list type="number">
/// <item><b>Authorization per endpoint</b> — anonymous is rejected on all four; an authenticated
/// non-operator is served by the two authenticated rows and rejected by the two Operator rows.</item>
/// <item><b>The principal actually reaches the readers.</b> The fixtures are authorized to a group
/// only the reader token carries, so a handler that passed <c>null</c> instead of
/// <c>HttpContext.User</c> would see the evaluator DENY (its null-principal branch pairs
/// <c>Denied = true</c>) and return an empty catalog and no counts — and the assertions below
/// would fail. This was verified by making that exact edit; see the task report.</item>
/// <item><b>A denied type is distinguishable from a zero count in the serialized body</b> — the
/// assertion the response contract exists for.</item>
/// </list>
/// </summary>
public class AdminConsoleEndpointsPipelineTests : IClassFixture<AdminConsoleTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AdminConsoleEndpointsPipelineTests(AdminConsoleTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private const string Tenants    = "/admin/console/tenants";
    private const string Schema     = "/admin/console/schema";
    private const string DataVolume = "/admin/console/data-volume";
    private const string Qdrant     = "/admin/console/qdrant";

    /// <summary>
    /// A tenant-scoped human: carries the <c>tenant_id</c> the evaluator requires and the group the
    /// two visible fixtures grant read access to. Carries NO <c>operators</c> group and no admin
    /// scope, so it is also the non-operator half of every Operator-gate assertion.
    /// </summary>
    private static string ReaderToken() => TestJwtFactory.CreateToken(
        "test-service-audience",
        "console-reader",
        extraClaims:
        [
            new Claim("tenant_id", "tenant_alpha"),
            new Claim("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
        ]);

    /// <summary>
    /// An operator, exactly as <c>ProbeAuthorizationPipelineTests</c> builds one. Note what it does
    /// NOT carry: a <c>tenant_id</c> claim. That is not an omission in the fixture — it is the live
    /// state Design 4d records, and the two authenticated endpoints' behaviour under it is pinned
    /// below rather than papered over.
    /// </summary>
    private static string OperatorToken() => TestJwtFactory.CreateToken(
        "test-service-audience",
        "human-operator",
        extraClaims: [new Claim("groups", "operators")]);

    private async Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ── Authorization ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task AnonymousRequest_Returns401(string path)
    {
        var response = await GetAsync(path, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Qdrant)]
    public async Task AuthenticatedNonOperator_OperatorGatedEndpoint_Returns403(string path)
    {
        var response = await GetAsync(path, ReaderToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    public async Task AuthenticatedNonOperator_AuthenticatedOnlyEndpoint_Returns200(string path)
    {
        var response = await GetAsync(path, ReaderToken());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── /admin/console/tenants ─────────────────────────────────────────────────

    [Fact]
    public async Task Operator_Tenants_ReturnsTheRoster()
    {
        var response = await GetAsync(Tenants, OperatorToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);
        body.GetProperty("count").GetInt32().Should().Be(2);

        var tenants = body.GetProperty("tenants").EnumerateArray().ToList();
        tenants.Select(t => t.GetProperty("id").GetString())
            .Should().Equal("tenant_alpha", "tenant_beta");
        tenants[0].GetProperty("displayName").GetString().Should().Be("Alpha");
        tenants[1].GetProperty("status").GetString().Should().Be("provisioning");
    }

    // ── /admin/console/schema ──────────────────────────────────────────────────

    [Fact]
    public async Task Reader_Schema_ReturnsOnlyThePermittedTypesAsProjections()
    {
        var response = await GetAsync(Schema, ReaderToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);
        var types = body.GetProperty("types").EnumerateArray().ToList();

        // THE PRINCIPAL-ROUTING ASSERTION. Both fixtures are readable only by the group this
        // token carries; a null principal would be denied and this list would be empty.
        types.Select(t => t.GetProperty("name").GetString())
            .Should().BeEquivalentTo(
                AdminConsoleTestWebApplicationFactory.VisibleTypeWithZeroRows,
                AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        body.GetProperty("typeCount").GetInt32().Should().Be(2);

        // The denied fixture is absent, name and all.
        body.ToString().Should().NotContain(AdminConsoleTestWebApplicationFactory.DeniedType);

        var author = types.Single(t =>
            t.GetProperty("name").GetString() == AdminConsoleTestWebApplicationFactory.VisibleTypeWithZeroRows);
        author.GetProperty("fieldCount").GetInt32().Should().Be(3);          // Id + Name + Bio
        author.GetProperty("description").GetString().Should().Be("People who write things");
        author.GetProperty("relations").GetArrayLength().Should().Be(0);

        // A projection, not a descriptor: field names are not on the wire.
        author.TryGetProperty("fields", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reader_Schema_ProjectsRelationEdges()
    {
        var response = await GetAsync(Schema, ReaderToken());

        var body = await BodyAsync(response);
        var article = body.GetProperty("types").EnumerateArray().Single(t =>
            t.GetProperty("name").GetString() == AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);

        article.GetProperty("fieldCount").GetInt32().Should().Be(4);          // Id + Title + Body + AuthorId

        var edge = article.GetProperty("relations").EnumerateArray().Single();
        edge.GetProperty("propertyName").GetString().Should().Be("Author");
        edge.GetProperty("kind").GetString().Should().Be("ManyToOne");
        edge.GetProperty("relatedType").GetString().Should().Be("Author");
        edge.GetProperty("foreignKey").GetString().Should().Be("AuthorId");
    }

    /// <summary>
    /// Design 4d, pinned rather than papered over: an operator carries no <c>tenant_id</c> claim,
    /// and the evaluator denies every type without one. The endpoint reports an empty catalog —
    /// it does NOT fall back to an unfiltered one, which is what a dropped principal would ALSO
    /// look like, and is why the reader assertions above are the ones that pin the routing.
    /// </summary>
    [Fact]
    public async Task Operator_Schema_ReturnsEmptyCatalogBecauseTheOperatorHasNoTenantClaim()
    {
        var response = await GetAsync(Schema, OperatorToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);
        body.GetProperty("typeCount").GetInt32().Should().Be(0);
        body.GetProperty("types").GetArrayLength().Should().Be(0);
    }

    // ── /admin/console/data-volume ─────────────────────────────────────────────

    /// <summary>
    /// THE ASSERTION THE RESPONSE CONTRACT EXISTS FOR. One registered type is visible with a row
    /// count of exactly zero, and another is denied outright. Both must be readable from the
    /// serialized body as different things — a denied type must never arrive as a count of 0.
    /// </summary>
    [Fact]
    public async Task Reader_DataVolume_DeniedTypeIsDistinguishableFromAZeroCount()
    {
        var response = await GetAsync(DataVolume, ReaderToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw  = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var entries = body.GetProperty("types").EnumerateArray().ToList();

        // The zero-count type IS present, with a real number.
        var zero = entries.Single(e =>
            e.GetProperty("typeName").GetString() == AdminConsoleTestWebApplicationFactory.VisibleTypeWithZeroRows);
        zero.GetProperty("status").GetString().Should().Be("counted");
        zero.GetProperty("rowCount").GetInt64().Should().Be(0);

        // The denied type is NOT present as an entry at all — so it cannot be read as a count of
        // any value, zero included — and the denial is reported in its own right.
        entries.Select(e => e.GetProperty("typeName").GetString())
            .Should().NotContain(AdminConsoleTestWebApplicationFactory.DeniedType);
        body.GetProperty("deniedTypeCount").GetInt32().Should().Be(1);
        body.GetProperty("unknownTypeCount").GetInt32().Should().Be(0);

        // …and it is not named anywhere in the body either: reporting denial must not re-disclose
        // what the catalog's own filtering withheld.
        raw.Should().NotContain(AdminConsoleTestWebApplicationFactory.DeniedType);
    }

    [Fact]
    public async Task Reader_DataVolume_CountsThePermittedTypes()
    {
        var response = await GetAsync(DataVolume, ReaderToken());

        var body = await BodyAsync(response);
        var entries = body.GetProperty("types").EnumerateArray().ToList();

        // THE PRINCIPAL-ROUTING ASSERTION for this endpoint: a null principal is denied every
        // type, so this list would be empty and deniedTypeCount would be 3.
        entries.Should().HaveCount(2);
        entries.Single(e =>
                e.GetProperty("typeName").GetString() == AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows)
            .GetProperty("rowCount").GetInt64()
            .Should().Be(AdminConsoleTestWebApplicationFactory.VisibleTypeRowCount);
    }

    /// <summary>Design 4d again, on the counting endpoint: every type denied, nothing reported as zero.</summary>
    [Fact]
    public async Task Operator_DataVolume_ReportsEveryTypeDeniedRatherThanZeroRows()
    {
        var response = await GetAsync(DataVolume, OperatorToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);
        body.GetProperty("types").GetArrayLength().Should().Be(0);
        body.GetProperty("deniedTypeCount").GetInt32().Should().Be(3);
    }

    // ── /admin/console/qdrant ──────────────────────────────────────────────────

    [Fact]
    public async Task Operator_Qdrant_ReturnsCollectionStats()
    {
        var response = await GetAsync(Qdrant, OperatorToken());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);
        body.GetProperty("collectionCount").GetInt32().Should().Be(2);

        var collections = body.GetProperty("collections").EnumerateArray().ToList();
        var withStats = collections.Single(c =>
            c.GetProperty("name").GetString() == AdminConsoleTestWebApplicationFactory.CollectionWithStats);
        withStats.GetProperty("pointsCount").GetUInt64().Should().Be(1234);
        withStats.GetProperty("indexedVectorsCount").GetUInt64().Should().Be(1200);

        // Not-reported stays null on the wire rather than collapsing to 0 — the same "a number
        // you did not get is not zero" rule the data-volume endpoint follows.
        var withoutStats = collections.Single(c =>
            c.GetProperty("name").GetString() == AdminConsoleTestWebApplicationFactory.CollectionWithoutStats);
        withoutStats.GetProperty("pointsCount").ValueKind.Should().Be(JsonValueKind.Null);
        withoutStats.GetProperty("indexedVectorsCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Nothing else moved ─────────────────────────────────────────────────────

    /// <summary>
    /// The global constraint, colocated with the diff that could break it: adding routes under
    /// <c>/admin/console/</c> must leave <c>/metrics</c> anonymous.
    /// </summary>
    [Fact]
    public async Task AnonymousGet_Metrics_StillReturns200()
    {
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
