using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Authorization;
using Iverson.Api.Console;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Api.Tests;

public class AdminConsoleSchemaEndpointTests
{
    /// <summary>
    /// Another tenant's schema is ABSENT: no name, and no effect on any count. Counting it in
    /// <c>withheldTypeCount</c> would tell any authenticated caller how many types other tenants
    /// own, and let it watch that number move — the cross-tenant existence oracle main's
    /// <c>GetSchema</c> closes by dropping a foreign schema uncounted. The expected counts come from
    /// the same caller against the same registry minus the foreign schemas, not from constants.
    /// Two foreign schemas, one this caller's groups could read and one they could not, so neither
    /// of <c>SchemaCatalogReader</c>'s drop grounds can carry a foreign type into the count.
    /// </summary>
    [Fact]
    public async Task ForeignTenantSchema_IsAbsentFromTheCatalog()
    {
        var baseline = await GetSchemaAsync();
        var withForeign = await GetSchemaAsync(
            AdminConsoleSchemaRegistryRepository.Article() with
            {
                TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
            },
            AdminConsoleSchemaRegistryRepository.Ledger() with
            {
                TypeName = "ForeignLedger", OwnerTenantId = "tenant_beta"
            });

        var names = withForeign.Types.Select(t => t.Name).ToList();
        names.Should().Contain(AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        names.Should().NotContain("ForeignArticle");
        names.Should().NotContain("ForeignLedger");

        baseline.WithheldTypeCount.Should().BePositive(
            "the fixture must withhold something of its own, or equal counts prove nothing");
        withForeign.WithheldTypeCount.Should().Be(baseline.WithheldTypeCount);
        withForeign.TypeCount.Should().Be(baseline.TypeCount);
        withForeign.Should().BeEquivalentTo(baseline);
    }

    private static async Task<SchemaCatalogResponse> GetSchemaAsync(params SchemaDescriptor[] extra)
    {
        var registry = new SchemaRegistry(
            new AdminConsoleSchemaRegistryRepository(), NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        foreach (var schema in extra)
            await registry.RegisterAsync(schema);
        var reader = new SchemaCatalogReader(
            registry, new RowFieldAuthorizationEvaluator(), NullLogger<SchemaCatalogReader>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", "tenant_alpha"),
                new Claim("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
            ], authenticationType: "test"))
        };

        var result = AdminConsoleEndpoints.GetSchema(http, registry, reader);

        return result.Should().BeOfType<Ok<SchemaCatalogResponse>>().Subject.Value!;
    }
}
