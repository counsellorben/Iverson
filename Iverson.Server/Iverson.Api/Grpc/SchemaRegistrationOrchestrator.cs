using System.Text.RegularExpressions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Grpc.Core;

namespace Iverson.Api.Grpc;

public interface ISchemaRegistrationOrchestrator
{
    Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct);
}

public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IVectorSchemaManager vector,
    IEngagementStoreSchemaManager starRocks,
    IEmbeddingService embedding,
    SchemaRegistry registry)
    : ISchemaRegistrationOrchestrator
{
    // TypeName/property names are string-interpolated unescaped into CREATE TABLE/ALTER TABLE
    // DDL by PostgresSchemaManager/StarRocksSchemaManager after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at
    // the source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be
    // a safe DDL identifier. No underscores are permitted in the input because ToSnakeCase
    // inserts its own; this pattern also naturally rejects an empty string.
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct)
    {
        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

            var ownerField = descriptor.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerField) &&
                !descriptor.ScalarColumns.Any(c => string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"owner_field '{ownerField}' on '{descriptor.TypeName}' does not match any declared scalar property."));
            }

            if (!string.IsNullOrEmpty(ownerField))
            {
                var ownerColumn = descriptor.ScalarColumns.First(c =>
                    string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase));

                // Allow-list, not a reject-list: IntelligenceStoreConsumer.ExtractTypedValue's default branch
                // only produces a clean scalar string for these 4 SqlTypes. Every other SqlType — including
                // the array variants UUID[]/REAL[] that SchemaBuilder.ArrayTypeOverrides can also produce for
                // a scalar column — falls through to JsonElement.ToString(), which for a non-string JSON value
                // (a number, bool, or array) produces something that can never equal a real caller's identity
                // value, silently excluding every caller (including the legitimate owner) from every result.
                var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
                if (!stringValuedSqlTypes.Contains(ownerColumn.SqlType.ToUpperInvariant()))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"owner_field '{ownerField}' on '{descriptor.TypeName}' has SqlType '{ownerColumn.SqlType}', " +
                        "which is not string-valued; Qdrant ownership filtering requires a string-valued owner field."));
                }

                if (descriptor.ChunkFields.Count > 0)
                {
                    var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
                    var camelOwnerField = ownerField.ToCamelCase();
                    if (reservedChunkKeys.Contains(camelOwnerField))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            $"owner_field '{ownerField}' on '{descriptor.TypeName}' camelCases to '{camelOwnerField}', " +
                            $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
                    }
                }
            }

            await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));

            try
            {
                await starRocks.ApplyTableAsync(SchemaBuilder.ToStarRocksTableSchema(descriptor));
            }
            catch (StarRocksNotReadyException ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"StarRocks is not ready: {ex.Message}"));
            }

            if (descriptor.VectorFields.Count > 0)
                await vector.ApplyCollectionAsync(SchemaBuilder.ToCollectionSchema(descriptor));

            if (descriptor.ChunkFields.Count > 0)
                await vector.ApplyCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(descriptor));

            await registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        return registered;
    }

    private static void ValidateIdentifier(string name, string context)
    {
        if (!IdentifierPattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{context} '{name}' is not a valid identifier — it must start with a letter and contain only letters and digits."));
        }
    }
}
