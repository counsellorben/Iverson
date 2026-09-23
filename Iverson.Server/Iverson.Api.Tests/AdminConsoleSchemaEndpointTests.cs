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
    [Fact]
    public async Task ForeignTenantSchema_IsAbsentFromTheCatalog()
    {
        var registry = new SchemaRegistry(
            new AdminConsoleSchemaRegistryRepository(), NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        await registry.RegisterAsync(AdminConsoleSchemaRegistryRepository.Article() with
        {
            TypeName = "ForeignArticle", OwnerTenantId = "tenant_beta"
        });
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

        var names = result.Should().BeOfType<Ok<SchemaCatalogResponse>>().Subject.Value!.Types
            .Select(t => t.Name).ToList();
        names.Should().Contain(AdminConsoleTestWebApplicationFactory.VisibleTypeWithRows);
        names.Should().NotContain("ForeignArticle");
    }
}
