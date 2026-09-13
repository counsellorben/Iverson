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

        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>(), Arg.Any<EntityAccess>())
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
        _entities
            .FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<EntityAccess>())
            .Returns(new[] { new KeyedRow(tagId1, tag1Json), new KeyedRow(tagId2, tag2Json) });

        var entityStruct = JsonParser.Default.Parse<Struct>(postJson);
        var schema = _registry.Get("Post")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 1, ActingUser, CancellationToken.None);

        await _entities.Received(1)
            .FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 2), Arg.Any<EntityAccess>());

        entityStruct.Fields["Tags"].ListValue.Values.Should().HaveCount(2);
    }

    // ── CSR finding #6: cyclic relation graph terminates ────────────────────────

    [Fact]
    public async Task ResolveRelationsAsync_WithCyclicRelationGraph_TerminatesInsteadOfLooping()
    {
        // A -> B -> A via a self-referencing ManyToOne (Employee.Manager -> Employee), which the
        // finding notes is legal in the schema model. A large depth (10) proves this terminates
        // because of the visited-set cycle guard, not merely because depth happened to run out
        // first — without the guard this would recurse the full 10 levels (alternating A/B) and
        // still terminate, just far deeper than the 2-hop graph actually contains.
        var idA = "55555555-0000-0000-0000-00000000000a";
        var idB = "55555555-0000-0000-0000-00000000000b";
        var employeeAJson = $$"""{"Id":"{{idA}}","Name":"Alice","ManagerId":"{{idB}}","TenantId":"test-tenant"}""";
        var employeeBJson = $$"""{"Id":"{{idB}}","Name":"Bob","ManagerId":"{{idA}}","TenantId":"test-tenant"}""";

        await _registry.RegisterAsync(SchemaFixtures.EmployeeSchema());

        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "employees"), Arg.Is<string>(k => k == idB), Arg.Any<EntityAccess>())
            .Returns(employeeBJson);
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "employees"), Arg.Is<string>(k => k == idA), Arg.Any<EntityAccess>())
            .Returns(employeeAJson);

        var entityStruct = JsonParser.Default.Parse<Struct>(employeeAJson);
        var schema = _registry.Get("Employee")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 10, ActingUser, CancellationToken.None);

        // One hop: A's manager is B.
        var manager = entityStruct.Fields["Manager"].StructValue;
        manager.Fields["Name"].StringValue.Should().Be("Bob");

        // Two hops: B's manager is A again — the cycle is allowed to close once...
        var managersManager = manager.Fields["Manager"].StructValue;
        managersManager.Fields["Name"].StringValue.Should().Be("Alice");

        // ...but not a third time: the re-visited A is embedded as a leaf, not expanded again.
        managersManager.Fields.Should().NotContainKey("Manager");

        // Exactly 2 fetches total (one per distinct entity in the cycle) — not 10, which is what
        // the depth cap alone would have allowed.
        await _entities.Received(2).FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "employees"), Arg.Any<string>(), Arg.Any<EntityAccess>());
    }

    [Fact]
    public async Task ResolveRelationsAsync_WithDiamondReference_ExpandsBothOccurrencesFully()
    {
        // Fix-round 1 (Medium finding): a DIAMOND, not a cycle — DiamondDocument.CreatedBy and
        // .UpdatedBy are two DIFFERENT relations that both happen to point at the SAME
        // DiamondUser. A traversal-global "seen anywhere in this request" cycle guard would mark
        // that user visited while expanding CreatedBy and then silently skip expanding
        // User.Team on the second occurrence (UpdatedBy) — even though nothing here is cyclic and
        // depth (3) is never exhausted. The path-scoped fix must expand Team on BOTH occurrences.
        var docId  = "66666666-0000-0000-0000-000000000001";
        var userId = "66666666-0000-0000-0000-000000000002"; // SAME user for both relations
        var teamId = "66666666-0000-0000-0000-000000000003";

        await _registry.RegisterAsync(SchemaFixtures.DiamondDocumentSchema());
        await _registry.RegisterAsync(SchemaFixtures.DiamondUserSchema());
        await _registry.RegisterAsync(SchemaFixtures.DiamondTeamSchema());

        var docJson  = $$"""{"Id":"{{docId}}","Title":"Spec","CreatedById":"{{userId}}","UpdatedById":"{{userId}}","TenantId":"test-tenant"}""";
        var userJson = $$"""{"Id":"{{userId}}","Name":"Alice","TeamId":"{{teamId}}","TenantId":"test-tenant"}""";
        var teamJson = $$"""{"Id":"{{teamId}}","Name":"Platform","TenantId":"test-tenant"}""";

        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "diamond_users"), Arg.Is<string>(k => k == userId), Arg.Any<EntityAccess>())
            .Returns(userJson);
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "diamond_teams"), Arg.Is<string>(k => k == teamId), Arg.Any<EntityAccess>())
            .Returns(teamJson);

        var entityStruct = JsonParser.Default.Parse<Struct>(docJson);
        var schema = _registry.Get("DiamondDocument")!;

        // depth: 3 so User -> Team (the second hop) actually has room to expand; nothing here
        // approaches MaxRelationDepth's real-world default of 5.
        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 3, ActingUser, CancellationToken.None);

        var createdBy = entityStruct.Fields["CreatedBy"].StructValue;
        var updatedBy = entityStruct.Fields["UpdatedBy"].StructValue;

        createdBy.Fields["Name"].StringValue.Should().Be("Alice");
        updatedBy.Fields["Name"].StringValue.Should().Be("Alice");

        // The actual regression: BOTH occurrences must have their own Team relation expanded.
        createdBy.Fields.Should().ContainKey("Team");
        updatedBy.Fields.Should().ContainKey("Team");
        createdBy.Fields["Team"].StructValue.Fields["Name"].StringValue.Should().Be("Platform");
        updatedBy.Fields["Team"].StructValue.Fields["Name"].StringValue.Should().Be("Platform");

        // The user is fetched independently for each relation (CreatedBy/UpdatedBy don't share a
        // cache) and its Team is fetched once per fetch of the user — 2 users, 2 teams.
        await _entities.Received(2).FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "diamond_users"), Arg.Any<string>(), Arg.Any<EntityAccess>());
        await _entities.Received(2).FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "diamond_teams"), Arg.Any<string>(), Arg.Any<EntityAccess>());
    }

    [Fact]
    public async Task ResolveRelationsAsync_OmitsTheServerOwnedTenantColumnFromTheResolvedRelation()
    {
        // A nested relation struct is built from the RELATED row's row_to_json, so it carries the
        // related type's __TenantId. Depth > 0 makes this a second, independent wire path out of
        // the same response — the parent's own strip does not cover the nested struct.
        var tenantAuthor = SchemaFixtures.AuthorSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Name", "text", false),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
            ],
            TenantColumn = SchemaDescriptor.TenantColumnName
        };
        await _registry.RegisterAsync(tenantAuthor);
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Alice","{{SchemaDescriptor.TenantColumnName}}":"test-tenant"}""");

        var entityStruct = JsonParser.Default.Parse<Struct>(ArticleJson);
        var schema = _registry.Get("Article")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 1, ActingUser, CancellationToken.None);

        var author = entityStruct.Fields["Author"].StructValue;
        author.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        author.Fields["Name"].StringValue.Should().Be("Alice");
    }
}
