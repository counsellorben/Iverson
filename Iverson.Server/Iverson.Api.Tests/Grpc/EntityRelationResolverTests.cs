using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class EntityRelationResolverTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly SchemaRegistry _registry;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly EntityRelationResolver _sut;

    private static readonly ClaimsPrincipal ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass");
    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string ArticleId = "22222222-0000-0000-0000-000000000002";
    private static readonly string AuthorJson  = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"test-tenant"}""";
    private static readonly string ArticleJson = $$"""{"Id":"{{ArticleId}}","Title":"Hello","Body":"World","AuthorId":"{{AuthorId}}","TenantId":"test-tenant"}""";

    public EntityRelationResolverTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new EntityRelationResolver(_registry, _entities, _authEvaluator);
    }

    [Fact]
    public async Task ResolveRelationsAsync_WithDepth1_ResolvesManyToOneRelation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>())
            .Returns(AuthorJson);

        var entityStruct = JsonParser.Default.Parse<Struct>(ArticleJson);
        var schema = _registry.Get("Article")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 1, ActingUser, CancellationToken.None);

        entityStruct.Fields.Should().ContainKey("Author");
        entityStruct.Fields["Author"].StructValue.Fields["Name"].StringValue.Should().Be("Alice");
    }

    // Note: "Get_WithDepth0_DoesNotResolveRelations" was NOT moved here (deliberate deviation
    // from the task brief's Step 4). ResolveRelationsAsync has no
    // depth==0 early-return of its own — that gate lives entirely in
    // ObjectMappingGrpcService.Get's `if (request.Depth > 0)` check, both before and after this
    // extraction. Inside this resolver, `depth` only gates recursion to a *second* level
    // (`depth > 1`); it never gates whether the immediate level resolves. Calling
    // ResolveRelationsAsync directly with depth: 0 exercises a state that never occurs in
    // production and does NOT skip resolving the immediate relation — verified empirically: it
    // throws parsing an unconfigured mock's default (empty-string) FetchByKeyAsync return,
    // proving the resolver attempted the fetch. The real "depth 0" behavior — Get never invoking
    // the resolver at all — is now fully covered by
    // ObjectMappingGrpcServiceTests.Get_WithDepthZero_DoesNotCallRelationResolver instead.

    [Fact]
    public async Task ResolveRelationsAsync_WithManyToManyRelation_IssuesSingleBatchQuery()
    {
        var postId = "33333333-0000-0000-0000-000000000003";
        var tagId1 = "44444444-0000-0000-0000-000000000004";
        var tagId2 = "44444444-0000-0000-0000-000000000005";

        await _registry.RegisterAsync(SchemaFixtures.PostWithTagsSchema());
        await _registry.RegisterAsync(SchemaFixtures.TagSchema());

        var postJson = $$"""{"Id":"{{postId}}","Title":"Hello","TagIds":["{{tagId1}}","{{tagId2}}"],"TenantId":"test-tenant"}""";

        var tag1Json = $$"""{"Id":"{{tagId1}}","Label":"dotnet","TenantId":"test-tenant"}""";
        var tag2Json = $$"""{"Id":"{{tagId2}}","Label":"csharp","TenantId":"test-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(new[] { new KeyedRow(tagId1, tag1Json), new KeyedRow(tagId2, tag2Json) });

        var entityStruct = JsonParser.Default.Parse<Struct>(postJson);
        var schema = _registry.Get("Post")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 1, ActingUser, CancellationToken.None);

        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(),
            Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 2));

        entityStruct.Fields["Tags"].ListValue.Values.Should().HaveCount(2);
    }
}
