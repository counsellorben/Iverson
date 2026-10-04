using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Api.Tests.Reconciliation;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// Carry-forward (CSR round 10 Finding #8) against a real Postgres: a stored value leaves as
/// <c>row_to_json</c> text through <c>FetchByKeyAsync</c>, travels through the payload Struct and
/// <c>SerializePayload</c>, and goes back in through <c>OutboxWriter</c>'s
/// <c>json_populate_record</c> full-row upsert. A mock cannot show that a <c>TIMESTAMPTZ</c> or a
/// <c>BYTEA</c> value survives that round trip unchanged.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class UpdateCarryForwardPostgresIntegrationTests(ReconciliationQueuePostgresContainerFixture fixture)
    : IClassFixture<ReconciliationQueuePostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    [Fact]
    public async Task Update_ByAFieldRestrictedCaller_LeavesOmittedRestrictedValuesIntactInPostgres()
    {
        var schema = SchemaFixtures.ReservedTenantDossierSchema() with
        {
            TableName = "dossiers_" + Guid.NewGuid().ToString("N")[..8]
        };
        var table = SchemaBuilder.ToTableSchema(schema);
        await _schemaManager.ApplySchemaAsync(table);
        await _schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);

        var key = Guid.CreateVersion7();
        // The container's connection is a superuser, so this seed bypasses RLS.
        await _repo.ExecuteAsync(
            $"""
            INSERT INTO "{schema.TableName}" ("Id", "Title", "Secret", "SealedAt", "Seal", "{SchemaDescriptor.TenantColumnName}")
            VALUES (@Id, 'old title', 'classified', '2026-10-03 12:34:56.789+00', '\x01ff'::bytea, 'test-tenant')
            """,
            new { Id = key });

        var registry = new SchemaRegistry(new SchemaRegistryRepository(_repo), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);
        var sut = new ObjectPersistenceGrpcService(
            Substitute.For<IOutboxPublisher>(),
            registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _repo, _repo),
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            new EntityRepository(_repo),
            new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") },
            new RowFieldAuthorizationEvaluator(),
            new AuditLog(NullLogger<AuditLog>.Instance));

        var before = await ReadRowAsync(schema.TableName, key);

        var payload = new Struct
        {
            Fields =
            {
                ["Id"]    = Value.ForString(key.ToString()),
                ["Title"] = Value.ForString("new title"),
            }
        };
        var response = await sut.Update(
            new PersistRequest { TypeName = "Dossier", Payload = payload }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        var after = await ReadRowAsync(schema.TableName, key);
        after.GetProperty("Title").GetString().Should().Be("new title");
        foreach (var restricted in new[] { "Secret", "SealedAt", "Seal" })
        {
            after.GetProperty(restricted).ValueKind.Should().NotBe(JsonValueKind.Null, restricted);
            after.GetProperty(restricted).GetRawText().Should().Be(before.GetProperty(restricted).GetRawText());
        }
        after.GetProperty(SchemaDescriptor.TenantColumnName).GetString().Should().Be("test-tenant");
    }

    [Fact]
    public async Task Update_ByAFieldRestrictedCaller_KeepsARestrictedIntegerAbove2Pow53Exact()
    {
        // A Struct number is a double; a stored BIGINT outside ±2^53 (-2^63 included) must not come back rounded.
        var baseSchema = SchemaFixtures.ReservedTenantDossierSchema();
        var schema = baseSchema with
        {
            TableName = "dossiers_" + Guid.NewGuid().ToString("N")[..8],
            ScalarColumns = [.. baseSchema.ScalarColumns,
                new ColumnDescriptor("Serial", "BIGINT", true), new ColumnDescriptor("Serials", "BIGINT[]", true)],
            Authorization = baseSchema.Authorization! with
            {
                FieldPermissions = [.. baseSchema.Authorization.FieldPermissions,
                    new Iverson.Api.Schema.FieldPermission("Serial", new List<string>(), new List<string> { "premium" }),
                    new Iverson.Api.Schema.FieldPermission("Serials", new List<string>(), new List<string> { "premium" })]
            }
        };
        await _schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(schema));
        await _schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);
        var key = Guid.CreateVersion7();
        await _repo.ExecuteAsync(
            $"""
            INSERT INTO "{schema.TableName}" ("Id", "Title", "Serial", "Serials", "{SchemaDescriptor.TenantColumnName}")
            VALUES (@Id, 'old title', 9007199254740993, ARRAY[9007199254740993, -9223372036854775808]::bigint[], 'test-tenant')
            """,
            new { Id = key });
        var registry = new SchemaRegistry(new SchemaRegistryRepository(_repo), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);
        var sut = new ObjectPersistenceGrpcService(
            Substitute.For<IOutboxPublisher>(), registry, new RelationValidator(), new PayloadSizeValidator(),
            new EntityKeyAccessor(), new OutboxWriter(ReconciliationSchema.TableName, _repo, _repo),
            NullLogger<ObjectPersistenceGrpcService>.Instance, new EntityRepository(_repo),
            new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") },
            new RowFieldAuthorizationEvaluator(), new AuditLog(NullLogger<AuditLog>.Instance));

        await sut.Update(new PersistRequest
        {
            TypeName = "Dossier",
            Payload = new Struct { Fields = { ["Id"] = Value.ForString(key.ToString()), ["Title"] = Value.ForString("new title") } }
        }, TestServerCallContext.Create());

        var stored = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Serial"::text || ' ' || "Serials"::text FROM "{schema.TableName}" WHERE "Id" = @Id""", new { Id = key });
        stored.Should().Be("9007199254740993 {9007199254740993,-9223372036854775808}");
    }

    private async Task<JsonElement> ReadRowAsync(string tableName, Guid key)
    {
        var json = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT row_to_json(t)::text FROM "{tableName}" t WHERE "Id" = @Id""", new { Id = key });
        return JsonDocument.Parse(json!).RootElement.Clone();
    }
}
