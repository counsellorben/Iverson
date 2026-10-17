using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// RegisterSchema's privileged half, in the worker (CSR round 12 §5.3), per descriptor in batch
/// order: the stored owner (#7), the adoption check (#1), the DDL and the conditional registry
/// write; then the re-renders. The caller holds the registration lock.
/// </summary>
internal sealed class SchemaRegistrationApplier(
    ISchemaRegistryRepository registryRepository,
    IExistingTableInspector tables,
    IRecordStoreSchemaManager schemaManager,
    IDocumentRerenderQueueRepository rerenderQueue) : ISchemaRegistrationExecutor
{
    internal static RpcException OwnedByAnotherTenant(string typeName) =>
        new(new Status(StatusCode.PermissionDenied,
            $"Type '{typeName}' is registered to another tenant and cannot be re-registered here."));

    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        foreach (var descriptor in descriptors)
        {
            // 1. The stored owner, from the row itself rather than any process's cache. Every case
            // variant of the name counts, because the registry folds them into one type.
            var storedOwners = await registryRepository.GetOwnersAsync(descriptor.TypeName);
            if (storedOwners.Any(owner => owner != descriptor.OwnerTenantId))
                throw OwnedByAnotherTenant(descriptor.TypeName);

            // 2. A type with no registry row whose table already exists would adopt that table. The
            // row must derive this table: ToTableName puts '_' before every capital after the first
            // letter, so a case variant of a stored name ("CaseProbe" for "Caseprobe") derives another
            // table, which the stored row never adopted; a first-letter variant derives the same one.
            if (!await registryRepository.ExistsAsync(descriptor.TypeName))
                await RequireAdoptableAsync(descriptor);

            // 3. The DDL.
            try
            {
                await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            }
            catch (SchemaDriftException ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
            }

            // 4. The registry write, which replaces a row only of the same owner.
            var json = JsonSerializer.Serialize(descriptor, SchemaRegistry.JsonOptions);
            if (!await registryRepository.UpsertIfOwnerAsync(descriptor.TypeName, json, descriptor.OwnerTenantId))
                throw OwnedByAnotherTenant(descriptor.TypeName);
        }

        // 5. A changed template invalidates every document of that type, because the rendered
        // text is derived data with no stored copy — a type-level row is how "re-render
        // everything of this type" is represented before the key set is known.
        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);
    }

    // Adopting is right only for this type's own table, the documented model-change recovery
    // (delete the registry row, then register again). A plumbing table, a view, or a table that
    // holds another tenant's rows is refused.
    private async Task RequireAdoptableAsync(SchemaDescriptor descriptor)
    {
        var existing = await tables.DescribeAsync(descriptor.TableName, descriptor.TenantColumn);
        if (existing is null)
            return;

        if (!existing.IsOrdinaryTable)
            throw NotAdoptable(descriptor, "is not an ordinary table");
        if (!existing.HasTenantColumn)
            throw NotAdoptable(descriptor, $"has no {descriptor.TenantColumn} column");
        if (descriptor.OwnerTenantId is { } owner &&
            await tables.HasRowsOfAnotherTenantAsync(descriptor.TableName, descriptor.TenantColumn, owner))
            throw NotAdoptable(descriptor, "holds rows of another tenant");
    }

    private static RpcException NotAdoptable(SchemaDescriptor descriptor, string reason) =>
        new(new Status(StatusCode.AlreadyExists,
            $"Type '{descriptor.TypeName}' is not registered, but its table '{descriptor.TableName}' already " +
            $"exists and {reason}, so the registration cannot take it over. Register the type under another name."));
}
