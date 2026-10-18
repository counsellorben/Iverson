using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// For the orchestrator's own tests, which are about validation, not about the queue: applies
/// RegisterSchema's effects in-process, in the worker's order, and writes through
/// <see cref="SchemaRegistry.RegisterAsync"/> so the test's registry holds the type at once, as the
/// api's registry does after its reload in production. It does not reproduce the worker's
/// owner and adoption checks or its error mapping: a <see cref="SchemaDriftException"/> from the
/// DDL propagates as is, where the worker answers <c>FailedPrecondition</c>.
/// </summary>
internal sealed class InProcessSchemaRegistrationExecutor(
    IRecordStoreSchemaManager schemaManager,
    SchemaRegistry registry,
    IDocumentRerenderQueueRepository rerenderQueue) : ISchemaRegistrationExecutor
{
    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        foreach (var descriptor in descriptors)
        {
            await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            await registry.RegisterAsync(descriptor);
        }

        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);
    }
}
