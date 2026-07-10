namespace Iverson.StarRocks.Tests;

/// <summary>
/// Shared Testcontainers-integration-test helper: builds a Func&lt;string, StarRocksQuerySchema?&gt;
/// registry lookup from a flat set of StarRocksQuerySchema fixtures. This mirrors the
/// per-file BuildRegistry helper pattern established independently in
/// StarRocksQueryBuilderTests.cs and StarRocksPipelineBuilderTests.cs (Tasks 2-3), but is
/// extracted to a shared class here because both StarRocksIntegrationTests.cs and
/// PipelineIntegrationTests.cs (Task 5) need the identical helper — duplicating it a third
/// and fourth time was not worth it.
/// </summary>
internal static class TestSchemaRegistry
{
    public static Func<string, StarRocksQuerySchema?> BuildRegistry(params StarRocksQuerySchema[] schemas)
    {
        var map = schemas.ToDictionary(s => s.TypeName, StringComparer.OrdinalIgnoreCase);
        return typeName => map.GetValueOrDefault(typeName);
    }
}
