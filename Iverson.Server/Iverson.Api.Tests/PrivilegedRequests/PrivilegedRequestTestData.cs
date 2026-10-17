using System.Text.Json;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Tests.PrivilegedRequests;

internal static class PrivilegedRequestTestData
{
    /// <summary>
    /// A minimal registrable descriptor: a UUID key, one nullable column and the server-owned
    /// tenant column, named as SchemaBuilder names them.
    /// </summary>
    public static SchemaDescriptor Descriptor(string typeName, string? owner, string titleSqlType = "TEXT") => new()
    {
        TypeName      = typeName,
        TableName     = SchemaBuilder.ToTableName(typeName),
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title", titleSqlType, true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = SchemaDescriptor.TenantColumnName,
        OwnerTenantId = owner
    };

    /// <summary>A claimed RegisterSchema request carrying <paramref name="descriptors"/>.</summary>
    public static PrivilegedRequestRow RegisterRow(params SchemaDescriptor[] descriptors) => new(
        Guid.NewGuid(),
        PrivilegedRequestKinds.RegisterSchema,
        JsonSerializer.Serialize(new RegisterSchemaPayload(descriptors, []), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running,
        null);

    /// <summary>A request row as the api reads it back while waiting.</summary>
    public static PrivilegedRequestRow StatusRow(string status, PrivilegedRequestResult? result = null) => new(
        Guid.Empty, PrivilegedRequestKinds.RegisterSchema, "{}", status, result?.ToJson());
}
