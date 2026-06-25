# Replace Elasticsearch with StarRocks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Elasticsearch with StarRocks as the engagement read store, eliminating the fan-out pattern and the `Iverson.Elasticsearch` project entirely.

**Architecture:** Kafka events flow to a new `EngagementStoreConsumer` that upserts/deletes rows directly in StarRocks (Primary Key table model). `ObjectSearchGrpcService` executes SQL WHERE queries and aggregations against StarRocks via a new `Iverson.StarRocks` project. `ObjectRetrievalGrpcService` continues reading from the Postgres write store unchanged.

**Tech Stack:** StarRocks (MySQL wire protocol, port 9030), MySqlConnector 2.3.x, Dapper 2.x, xunit, NSubstitute, FluentAssertions, .NET 10.

## Global Constraints

- Target framework: `net10.0` on all projects
- NSubstitute 5.3.0, FluentAssertions 7.0.0, xunit 2.9.3 for tests
- StarRocks docker image: `starrocks/allin1-ubuntu:latest`
- MySQL connection port: 9030 (StarRocks FE query port)
- StarRocks table model: PRIMARY KEY (supports UPDATE/DELETE)
- Column names in StarRocks match the schema PascalCase (same as Postgres write store)
- No `StoreTarget.EngagementFanout` after this change — fan-out is eliminated
- `SchemaDescriptor.IndexName` is removed; `TableName` serves both Postgres and StarRocks
- `SchemaRegistry` inverse index and all fan-out methods are removed
- Build command: `dotnet build Iverson.Server/Iverson.Server.slnx`
- Test command: `dotnet test Iverson.Server/Iverson.Server.slnx`

---

## File Map

**Create:**
- `Iverson.StarRocks/Iverson.StarRocks.csproj`
- `Iverson.StarRocks/IStarRocksRepository.cs`
- `Iverson.StarRocks/StarRocksRepository.cs`
- `Iverson.StarRocks/StarRocksTableSchema.cs`
- `Iverson.StarRocks/Aggregation.cs`
- `Iverson.StarRocks/ServiceCollectionExtensions.cs`
- `Iverson.StarRocks/Telemetry.cs`
- `Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj`
- `Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs`
- `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`

**Modify:**
- `Iverson.Server.slnx` — add StarRocks + StarRocks.Tests, remove ES + ES.Tests
- `docker-compose.yml` — replace `elasticsearch` service with `starrocks`
- `Iverson.Events/EntityEvent.cs` — remove `EngagementFanout` from `StoreTarget`
- `Iverson.Api/Schema/SchemaDescriptor.cs` — remove `IndexName`
- `Iverson.Api/Schema/SchemaBuilder.cs` — remove ES helpers, add `ToStarRocksTableSchema` + `ClrTypeToStarRocksType`
- `Iverson.Api/Schema/SchemaRegistry.cs` — remove inverse index, fan-out methods
- `Iverson.Api/Consumers/EngagementStoreConsumer.cs` — full rewrite (StarRocks upsert, no fan-out)
- `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — remove `IElasticsearchService` dep + `ApplyMappingAsync` call + `EngagementFanout`
- `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — remove `EngagementFanout`
- `Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — full rewrite (StarRocks SQL)
- `Iverson.Api/Program.cs` — remove ES, add StarRocks, update health check
- `Iverson.Api/Iverson.Api.csproj` — remove ES reference, add StarRocks reference
- `Iverson.Api.Tests/Iverson.Api.Tests.csproj` — remove ES reference, add StarRocks reference
- `Iverson.Api.Tests/Helpers/SchemaFixtures.cs` — remove `IndexName`
- `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs` — full rewrite
- `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — remove ES mock + `IndexName`
- `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — full rewrite
- `Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — remove fan-out tests
- `Iverson.Events.Tests/EntityEventTests.cs` — remove `EngagementFanout` tests

**Delete:**
- `Iverson.Elasticsearch/` (entire directory)
- `Iverson.Elasticsearch.Tests/` (entire directory)

---

### Task 1: Create `Iverson.StarRocks` project

**Files:**
- Create: `Iverson.StarRocks/Iverson.StarRocks.csproj`
- Create: `Iverson.StarRocks/IStarRocksRepository.cs`
- Create: `Iverson.StarRocks/StarRocksTableSchema.cs`
- Create: `Iverson.StarRocks/Aggregation.cs`
- Create: `Iverson.StarRocks/Telemetry.cs`
- Create: `Iverson.StarRocks/ServiceCollectionExtensions.cs`
- Create: `Iverson.StarRocks/StarRocksRepository.cs`
- Create: `Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj`
- Create: `Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs`
- Modify: `Iverson.Server.slnx` — add both new projects

**Interfaces produced (used by later tasks):**
```csharp
// IStarRocksRepository
Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
Task<int> ExecuteAsync(string sql, object? param = null);
Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
Task DeleteAsync(string tableName, string keyColumn, string keyValue);
Task ApplyTableAsync(StarRocksTableSchema schema);
Task<bool> IsHealthyAsync();

// StarRocksTableSchema
record StarRocksTableSchema(string TableName, StarRocksColumnSchema KeyColumn, IReadOnlyList<StarRocksColumnSchema> Columns);
record StarRocksColumnSchema(string Name, string SrType, bool IsNullable);

// Aggregation types (replace Iverson.Elasticsearch equivalents)
enum AggregationKind { Terms, DateHistogram, Range, Avg, Sum, Min, Max, Count }
record AggregationDescriptor(string Name, AggregationKind Kind, string Field, int Size = 10,
    string? CalendarInterval = null, string? TimeZone = null,
    IReadOnlyList<RangeBucketDescriptor>? RangeBuckets = null);
record RangeBucketDescriptor(string Key, double? From, double? To);
record AggregationResult(string Name, AggregationKind Kind,
    IReadOnlyList<AggregationBucket>? Buckets = null, double? MetricValue = null);
record AggregationBucket(string Key, long DocCount);
```

- [ ] **Step 1: Write the failing test**

Create `Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs`:

```csharp
using FluentAssertions;
using Iverson.StarRocks;
using NSubstitute;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksRepositoryTests
{
    [Fact]
    public void IStarRocksRepository_ExistsAsInterface()
    {
        // Verifies the interface can be substituted — used by all consumer/service tests
        var sut = Substitute.For<IStarRocksRepository>();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void StarRocksTableSchema_StoresColumns()
    {
        var key  = new StarRocksColumnSchema("Id", "VARCHAR(36)", false);
        var cols = new List<StarRocksColumnSchema>
        {
            new("Name", "STRING", false),
            new("Bio",  "STRING", true)
        };
        var schema = new StarRocksTableSchema("authors", key, cols);

        schema.TableName.Should().Be("authors");
        schema.KeyColumn.Name.Should().Be("Id");
        schema.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void AggregationDescriptor_DefaultSizeIsTen()
    {
        var spec = new AggregationDescriptor("n", AggregationKind.Terms, "Name");
        spec.Size.Should().Be(10);
    }
}
```

- [ ] **Step 2: Create project files (tests will fail to compile until sources exist)**

Create `Iverson.StarRocks/Iverson.StarRocks.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Dapper" Version="2.1.79" />
    <PackageReference Include="MySqlConnector" Version="2.4.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
  </ItemGroup>
</Project>
```

Create `Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.3.0" />
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
    <PackageReference Include="coverlet.collector" Version="6.0.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Iverson.StarRocks/Iverson.StarRocks.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add projects to solution**

Edit `Iverson.Server.slnx` — add two lines inside `<Solution>`:
```xml
  <Project Path="Iverson.StarRocks/Iverson.StarRocks.csproj" />
  <Project Path="Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj" />
```

- [ ] **Step 4: Create `Iverson.StarRocks/StarRocksTableSchema.cs`**

```csharp
namespace Iverson.StarRocks;

public sealed record StarRocksTableSchema(
    string TableName,
    StarRocksColumnSchema KeyColumn,
    IReadOnlyList<StarRocksColumnSchema> Columns);

public sealed record StarRocksColumnSchema(string Name, string SrType, bool IsNullable);
```

- [ ] **Step 5: Create `Iverson.StarRocks/Aggregation.cs`**

```csharp
namespace Iverson.StarRocks;

public enum AggregationKind
{
    Terms,
    DateHistogram,
    Range,
    Avg,
    Sum,
    Min,
    Max,
    Count
}

public sealed record AggregationDescriptor(
    string Name,
    AggregationKind Kind,
    string Field,
    int Size = 10,
    string? CalendarInterval = null,
    string? TimeZone = null,
    IReadOnlyList<RangeBucketDescriptor>? RangeBuckets = null);

public sealed record RangeBucketDescriptor(string Key, double? From, double? To);

public sealed record AggregationResult(
    string Name,
    AggregationKind Kind,
    IReadOnlyList<AggregationBucket>? Buckets = null,
    double? MetricValue = null);

public sealed record AggregationBucket(string Key, long DocCount);
```

- [ ] **Step 6: Create `Iverson.StarRocks/Telemetry.cs`**

```csharp
using System.Diagnostics;

namespace Iverson.StarRocks;

internal static class Telemetry
{
    internal static readonly ActivitySource Source = new("Iverson.StarRocks");
}
```

- [ ] **Step 7: Create `Iverson.StarRocks/IStarRocksRepository.cs`**

```csharp
namespace Iverson.StarRocks;

public interface IStarRocksRepository
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue);
    Task ApplyTableAsync(StarRocksTableSchema schema);
    Task<bool> IsHealthyAsync();
}
```

- [ ] **Step 8: Create `Iverson.StarRocks/ServiceCollectionExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.StarRocks;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStarRocks(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IStarRocksRepository>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StarRocksRepository>>();
            return new StarRocksRepository(connectionString, logger);
        });
        return services;
    }
}
```

- [ ] **Step 9: Create `Iverson.StarRocks/StarRocksRepository.cs`**

```csharp
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Iverson.StarRocks;

public sealed class StarRocksRepository(string connectionString, ILogger<StarRocksRepository> logger)
    : IStarRocksRepository
{
    private readonly string _dbName = new MySqlConnectionStringBuilder(connectionString).Database;

    private MySqlConnection CreateConnection() => new(connectionString);

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("sr.query", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
        try
        {
            var results = await conn.QueryAsync<T>(sql, param);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return results;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("sr.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
        try
        {
            var rows = await conn.ExecuteAsync(sql, param);
            activity?.SetTag("db.rows_affected", rows);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return rows;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    public async Task UpsertAsync(StarRocksTableSchema schema, string payloadJson)
    {
        using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();

        if (entries.Count == 0) return;

        var colList   = string.Join(", ", entries.Select(e => $"`{e.Key}`"));
        var paramList = string.Join(", ", entries.Select((_, i) => $"@p{i}"));
        var sql       = $"INSERT INTO `{schema.TableName}` ({colList}) VALUES ({paramList})";

        var param = new DynamicParameters();
        for (var i = 0; i < entries.Count; i++)
            param.Add($"p{i}", JsonElementToObject(entries[i].Value));

        await ExecuteAsync(sql, param);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public Task DeleteAsync(string tableName, string keyColumn, string keyValue) =>
        ExecuteAsync(
            $"DELETE FROM `{tableName}` WHERE `{keyColumn}` = @key",
            new { key = keyValue });

    public async Task ApplyTableAsync(StarRocksTableSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("sr.apply_table", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        await EnsureDatabaseAsync();

        await using var conn = CreateConnection();

        var exists = await conn.QuerySingleOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @db AND table_name = @tbl",
            new { db = _dbName, tbl = schema.TableName });

        if (exists == 0)
        {
            var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
            var colsSql = schema.Columns.Select(c =>
                $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

            var ddl = $"""
                CREATE TABLE IF NOT EXISTS `{schema.TableName}` (
                    {keySql},
                    {string.Join(",\n    ", colsSql)}
                ) ENGINE=OLAP
                PRIMARY KEY(`{schema.KeyColumn.Name}`)
                DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4
                PROPERTIES ("replication_num" = "1")
                """;

            await conn.ExecuteAsync(ddl);
            logger.LogInformation("Created StarRocks table {Table}", schema.TableName);
        }
        else
        {
            var existingCols = (await conn.QueryAsync<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = @db AND table_name = @tbl",
                new { db = _dbName, tbl = schema.TableName }
            )).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var col in schema.Columns.Where(c => !existingCols.Contains(c.Name)))
            {
                await conn.ExecuteAsync(
                    $"ALTER TABLE `{schema.TableName}` ADD COLUMN `{col.Name}` {col.SrType}");
                logger.LogInformation("Added column {Col} to StarRocks table {Table}", col.Name, schema.TableName);
            }
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var result = await QueryAsync<int>("SELECT 1");
            return result.Any();
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureDatabaseAsync()
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = string.Empty };
        await using var conn = new MySqlConnection(builder.ToString());
        await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{_dbName}`");
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Array  => el.GetRawText(),
        _                    => el.GetRawText()
    };
}
```

- [ ] **Step 10: Run tests**

```bash
dotnet test Iverson.Server/Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj -v minimal
```
Expected: 3 tests pass.

- [ ] **Step 11: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/ Iverson.Server/Iverson.StarRocks.Tests/ Iverson.Server/Iverson.Server.slnx
git commit -m "feat: add Iverson.StarRocks project with IStarRocksRepository and aggregation types"
```

---

### Task 2: Update docker-compose

**Files:**
- Modify: `docker-compose.yml`

- [ ] **Step 1: Replace `elasticsearch` service with `starrocks`**

In `docker-compose.yml`:
- Remove the entire `elasticsearch:` service block (lines 23–38 inclusive, including the `elasticsearch_data` volume).
- Add after the `postgres:` service:

```yaml
  starrocks:
    image: starrocks/allin1-ubuntu:latest
    container_name: iverson-starrocks
    restart: unless-stopped
    ports:
      - "8030:8030"   # FE HTTP / admin
      - "9030:9030"   # FE MySQL query port
    volumes:
      - starrocks_data:/data/deploy
    healthcheck:
      test: ["CMD-SHELL", "mysql -h 127.0.0.1 -P 9030 -u root --connect-timeout=3 -e 'SELECT 1' 2>/dev/null || exit 1"]
      interval: 20s
      timeout: 10s
      retries: 15
      start_period: 60s
```

- In the `volumes:` section at the bottom, replace `elasticsearch_data:` with `starrocks_data:`.
- In the `iverson-api` service's `depends_on:` block, replace:
  ```yaml
      elasticsearch:
        condition: service_healthy
  ```
  with:
  ```yaml
      starrocks:
        condition: service_healthy
  ```
- In `iverson-api` environment, remove `Elasticsearch__Url` if present (it is not currently set as env var, only as default in `Program.cs`).

- [ ] **Step 2: Commit**

```bash
git add Iverson.Server/docker-compose.yml
git commit -m "infra: replace Elasticsearch with StarRocks in docker-compose"
```

---

### Task 3: Remove fan-out from events and schema layer

**Files:**
- Modify: `Iverson.Events/EntityEvent.cs`
- Modify: `Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Api/Schema/SchemaRegistry.cs`
- Modify: `Iverson.Events.Tests/EntityEventTests.cs`
- Modify: `Iverson.Api.Tests/Helpers/SchemaFixtures.cs`
- Modify: `Iverson.Api.Tests/Schema/SchemaRegistryTests.cs`

- [ ] **Step 1: Write failing test — StoreTarget no longer has EngagementFanout**

In `Iverson.Events.Tests/EntityEventTests.cs`, remove the two tests that reference `EngagementFanout`:
- `StoreTarget_All_ExcludesEngagementFanout`
- `StoreTarget_FlagsCanBeCombined` (uses `EngagementFanout`)

These tests will become compile errors once we remove the flag, so delete them before making the source change.

The remaining tests in `EntityEventTests.cs` that should still pass:
```csharp
[Fact] public void StoreTarget_All_IncludesRecord()
[Fact] public void StoreTarget_All_IncludesEngagement()
[Fact] public void StoreTarget_All_IncludesIntelligence()
// (plus any others that don't reference EngagementFanout)
```

- [ ] **Step 2: Update `Iverson.Events/EntityEvent.cs`**

Replace the `StoreTarget` enum with:
```csharp
[Flags]
public enum StoreTarget
{
    None         = 0,
    Record       = 1 << 0,  // PostgreSQL — system of record
    Engagement   = 1 << 1,  // StarRocks — engagement read store
    Intelligence = 1 << 2,  // Qdrant — vector/chunk fields

    All = Record | Engagement | Intelligence
}
```

- [ ] **Step 3: Remove `IndexName` from `SchemaDescriptor`**

In `Iverson.Api/Schema/SchemaDescriptor.cs`, remove the line:
```csharp
public required string IndexName      { get; init; }   // Elasticsearch
```

- [ ] **Step 4: Update `SchemaBuilder.cs`**

Remove from `SchemaBuilder.cs`:
- The `using Iverson.Elasticsearch;` import
- The `using EsFieldType = Iverson.Elasticsearch.EsFieldType;` alias
- `IndexName = tableName,` line in `BuildDescriptor` return
- The entire `ToIndexSchema` method
- The entire `SqlTypeToEsType` method

Add the following two methods (after `ToTableSchema`):

```csharp
internal static StarRocksTableSchema ToStarRocksTableSchema(SchemaDescriptor d) => new(
    d.TableName,
    new StarRocksColumnSchema(d.KeyColumn.Name, ClrTypeToStarRocksType(d.KeyColumn.SqlType), false),
    d.ScalarColumns.Select(c => new StarRocksColumnSchema(c.Name, ClrTypeToStarRocksType(c.SqlType), c.IsNullable)).ToList());

internal static string ClrTypeToStarRocksType(string sqlType) => sqlType.ToUpperInvariant() switch
{
    "UUID"             => "VARCHAR(36)",
    "UUID[]"           => "STRING",
    "TEXT"             => "STRING",
    "INTEGER"          => "INT",
    "BIGINT"           => "BIGINT",
    "REAL"             => "FLOAT",
    "REAL[]"           => "STRING",
    "DOUBLE PRECISION" => "DOUBLE",
    "BOOLEAN"          => "BOOLEAN",
    "TIMESTAMPTZ"      => "DATETIME",
    "BYTEA"            => "VARBINARY",
    _                  => "STRING"
};
```

Add `using Iverson.StarRocks;` at the top of `SchemaBuilder.cs`.

- [ ] **Step 5: Remove fan-out from `SchemaRegistry.cs`**

In `SchemaRegistry.cs`:
- Remove the `_inverseIndex` field declaration
- Remove `HasEngagementDependents` method
- Remove `GetDirectEngagementDependents` method
- Remove `RebuildInverseIndex` method
- Remove `IsCompleteForEngagement` static method
- Remove the two `RebuildInverseIndex()` calls in `RegisterAsync` and `UnregisterAsync`
- Remove the comment `// typeName → set of ES-eligible schema TypeNames that embed it as a relation`

The final `RegisterAsync` body becomes:
```csharp
public async Task RegisterAsync(SchemaDescriptor descriptor)
{
    await EnsureMetadataTableAsync();

    var json = JsonSerializer.Serialize(descriptor, s_jsonOptions);

    await sql.ExecuteAsync(
        """
        INSERT INTO _iverson_schema (type_name, schema_json, updated_at)
        VALUES (@TypeName, @Json::jsonb, now())
        ON CONFLICT (type_name) DO UPDATE
            SET schema_json = EXCLUDED.schema_json,
                updated_at  = EXCLUDED.updated_at
        """,
        new { TypeName = descriptor.TypeName, Json = json });

    _schemas[descriptor.TypeName] = descriptor;
    logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);
}
```

The final `UnregisterAsync` body becomes:
```csharp
public async Task UnregisterAsync(string typeName)
{
    await sql.ExecuteAsync(
        "DELETE FROM _iverson_schema WHERE type_name = @TypeName",
        new { TypeName = typeName });

    _schemas.TryRemove(typeName, out _);
    logger.LogInformation("Unregistered schema for {TypeName}", typeName);
}
```

- [ ] **Step 6: Update `SchemaFixtures.cs`**

In `Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, remove `IndexName = "..."` from every `SchemaDescriptor` initializer (it appears in `AuthorSchema`, `ArticleSchema`, `ArticleWithOneToManySchema`, `UserArticleSchema`).

- [ ] **Step 7: Update `SchemaRegistryTests.cs`**

Remove the four fan-out tests:
- `RegisterAsync_BuildsInverseIndex_ForManyToOne_Relations`
- `HasEngagementDependents_ReturnsFalse_ForLeafType`
- `GetDirectEngagementDependents_ReturnsArticle_ForAuthor`
- `UnregisterAsync_UpdatesInverseIndex`

(The remaining 4 tests — `Get_ReturnsNull`, `RegisterAsync_StoresDescriptor`, `IsRegistered_ReturnsFalse`, `IsRegistered_ReturnsTrue`, `UnregisterAsync_RemovesSchema` — continue unchanged.)

- [ ] **Step 8: Add `Iverson.StarRocks` reference to `Iverson.Api.csproj`**

In `Iverson.Api/Iverson.Api.csproj`, add inside the `<ItemGroup>` with other `ProjectReference` entries:
```xml
<ProjectReference Include="..\Iverson.StarRocks\Iverson.StarRocks.csproj" />
```

- [ ] **Step 9: Run tests**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal
```
Expected: all tests pass (ES tests still run — they are not deleted yet).

- [ ] **Step 10: Commit**

```bash
git add Iverson.Server/Iverson.Events/EntityEvent.cs \
        Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs \
        Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs \
        Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs \
        Iverson.Server/Iverson.Api/Iverson.Api.csproj \
        Iverson.Server/Iverson.Events.Tests/EntityEventTests.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs
git commit -m "refactor: remove EngagementFanout, IndexName, and SchemaRegistry inverse index"
```

---

### Task 4: Rewrite `EngagementStoreConsumer` + add `StarRocksQueryBuilder`

**Files:**
- Create: `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`
- Modify: `Iverson.Api/Consumers/EngagementStoreConsumer.cs` (full rewrite)
- Modify: `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs` (full rewrite)

- [ ] **Step 1: Write failing tests for the new consumer**

Replace the entire contents of `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class EngagementStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IStarRocksRepository _sr;
    private readonly IPostgresRepository _sql;
    private readonly Api.Schema.SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EngagementStoreConsumerTests()
    {
        _consumer = Substitute.For<IEventConsumer>();
        _sr       = Substitute.For<IStarRocksRepository>();
        _sql      = Substitute.For<IPostgresRepository>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);
        _sr.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        _registry = new Api.Schema.SchemaRegistry(_sql, NullLogger<Api.Schema.SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private EngagementStoreConsumer BuildSut() =>
        new(_consumer, _sr, _registry, NullLogger<EngagementStoreConsumer>.Instance);

    [Fact]
    public async Task HandleUpsert_WithEngagementFlag_CallsUpsertAsync()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).UpsertAsync(
            Arg.Is<StarRocksTableSchema>(s => s.TableName == "authors"),
            Arg.Any<string>());
    }

    [Fact]
    public async Task HandleDelete_WithEngagementFlag_CallsDeleteAsync()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var key = Guid.NewGuid().ToString();

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           key,
            PayloadJson:   "{}",
            TraceId:       "trace-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement);

        await BuildSut().HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).DeleteAsync("authors", "Id", key);
    }

    [Fact]
    public async Task SkipsEvent_WhenNoEngagementFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandlesMalformedJson_WithoutThrowing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var act = async () =>
            await BuildSut().HandleUpsertAsync("some-key", "NOT_VALID_JSON{{{", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DropsEvent_WhenSchemaNotRegistered()
    {
        var ev = new EntityEvent(
            TypeName:      "Unknown",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   "{}",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>());
    }
}
```

- [ ] **Step 2: Run test — expect compile failure**

```bash
dotnet build Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | head -20
```
Expected: compile error — `EngagementStoreConsumer` constructor doesn't match yet.

- [ ] **Step 3: Rewrite `EngagementStoreConsumer.cs`**

Replace entire file `Iverson.Api/Consumers/EngagementStoreConsumer.cs`:

```csharp
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.StarRocks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Consumers;

public sealed class EngagementStoreConsumer(
    IEventConsumer consumer,
    IStarRocksRepository sr,
    SchemaRegistry registry,
    ILogger<EngagementStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.engagement";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(
            consumer.ConsumeAsync(EntityTopics.Created, GroupId, HandleUpsertAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Updated, GroupId, HandleUpsertAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Deleted, GroupId + ".delete", HandleDeleteAsync, ct));

    internal async Task HandleUpsertAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError("[Engagement] Dropped upsert — no schema for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        var srSchema = SchemaBuilder.ToStarRocksTableSchema(schema);
        await sr.UpsertAsync(srSchema, ev.PayloadJson);
        logger.LogInformation("[Engagement] Upserted {Type}:{Key}", ev.TypeName, key);
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError("[Engagement] Dropped delete — no schema for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        await sr.DeleteAsync(schema.TableName, schema.KeyColumn.Name, ev.Key);
        logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName, key);
    }

    private EntityEvent? Deserialize(string key, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Engagement] Failed to deserialize event key={Key}", key);
            return null;
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
```

- [ ] **Step 4: Create `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`**

```csharp
using System.Text;
using Dapper;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.StarRocks;

using SrAggKind  = Iverson.StarRocks.AggregationKind;
using SrAggSpec  = Iverson.StarRocks.AggregationDescriptor;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;

namespace Iverson.Api.StarRocks;

internal static class StarRocksQueryBuilder
{
    internal static (string Sql, DynamicParameters Param) BuildSearch(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        int page,
        int pageSize)
    {
        var param = new DynamicParameters();
        var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var order = BuildOrder(schema, query?.Sort);

        var limit  = pageSize > 0 ? pageSize : 50;
        var offset = page > 0 ? page * limit : 0;

        var sb = new StringBuilder($"SELECT * FROM `{tableName}`");
        if (where.Length > 0) sb.Append($" WHERE {where}");
        if (order.Length > 0) sb.Append($" ORDER BY {order}");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return (sb.ToString(), param);
    }

    internal static (string Sql, DynamicParameters Param) BuildAggregate(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        SrAggSpec spec)
    {
        var param = new DynamicParameters();
        var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var col   = ResolveColumn(schema, spec.Field) ?? spec.Field;
        var wc    = where.Length > 0 ? $" WHERE {where}" : "";

        var sql = spec.Kind switch
        {
            SrAggKind.Terms =>
                $"SELECT `{col}` AS bucket_key, COUNT(*) AS doc_count " +
                $"FROM `{tableName}`{wc} " +
                $"GROUP BY `{col}` " +
                $"ORDER BY doc_count DESC " +
                $"LIMIT {(spec.Size > 0 ? spec.Size : 10)}",

            SrAggKind.DateHistogram =>
                $"SELECT DATE_FORMAT(`{col}`, '{DateFormatFor(spec.CalendarInterval)}') AS bucket_key, " +
                $"COUNT(*) AS doc_count " +
                $"FROM `{tableName}`{wc} " +
                $"GROUP BY bucket_key ORDER BY bucket_key",

            SrAggKind.Range => BuildRangeSql(tableName, col, spec.RangeBuckets, wc),

            SrAggKind.Avg         => $"SELECT AVG(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Sum         => $"SELECT SUM(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Min         => $"SELECT MIN(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Max         => $"SELECT MAX(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Count => $"SELECT COUNT(DISTINCT `{col}`) AS metric_val FROM `{tableName}`{wc}",

            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind))
        };

        return (sql, param);
    }

    internal static string BuildWhere(
        SchemaDescriptor schema,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        out int nextIdx)
    {
        nextIdx = 0;
        if (clauses is null) return "";

        var parts = new List<string>();

        foreach (var clause in clauses)
        {
            if (clause.Operator == SearchOperator.VectorSimilar) continue;

            var col = ResolveColumn(schema, clause.Property);
            if (col is null) continue;

            var pName = $"p{nextIdx++}";

            var condition = clause.Operator switch
            {
                SearchOperator.Equals => BuildEq(col, pName, clause.Value, param),
                SearchOperator.NotEquals =>
                    Condition($"`{col}` <> @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.Contains =>
                    Condition($"`{col}` LIKE @{pName}", pName, $"%{clause.Value?.StringVal}%", param),
                SearchOperator.StartsWith =>
                    Condition($"`{col}` LIKE @{pName}", pName, $"{clause.Value?.StringVal}%", param),
                SearchOperator.GreaterThan =>
                    Condition($"`{col}` > @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.GreaterThanOrEquals =>
                    Condition($"`{col}` >= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThan =>
                    Condition($"`{col}` < @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThanOrEquals =>
                    Condition($"`{col}` <= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.In => BuildIn(col, pName, clause.Value, param),
                _ => null
            };

            if (condition is null) continue;

            var wrapped = clause.ClauseType == SearchClauseType.MustNot
                ? $"NOT ({condition})"
                : condition;

            parts.Add(wrapped);
        }

        if (parts.Count == 0) return "";
        var sep = logic == SearchLogic.Or ? " OR " : " AND ";
        return string.Join(sep, parts);
    }

    internal static string? ResolveColumn(SchemaDescriptor schema, string property)
    {
        var candidates = schema.ScalarColumns.Select(c => c.Name).Append(schema.KeyColumn.Name);
        return candidates.FirstOrDefault(c => string.Equals(c, property, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildOrder(SchemaDescriptor schema, IEnumerable<SearchSort>? sorts)
    {
        if (sorts is null) return "";
        var parts = sorts
            .Select(s => (col: ResolveColumn(schema, s.Property), s.Descending))
            .Where(x => x.col is not null)
            .Select(x => $"`{x.col}` {(x.Descending ? "DESC" : "ASC")}");
        return string.Join(", ", parts);
    }

    private static string BuildRangeSql(
        string tableName, string col,
        IReadOnlyList<SrRangeSpec>? buckets, string wc)
    {
        if (buckets is null || buckets.Count == 0)
            return $"SELECT NULL AS bucket_key, COUNT(*) AS doc_count FROM `{tableName}`{wc}";

        var cases = buckets.Select(b =>
        {
            if (b.From is null && b.To is not null)
                return $"WHEN `{col}` < {b.To.Value} THEN '{b.Key}'";
            if (b.From is not null && b.To is null)
                return $"WHEN `{col}` >= {b.From.Value} THEN '{b.Key}'";
            if (b.From is not null && b.To is not null)
                return $"WHEN `{col}` >= {b.From.Value} AND `{col}` < {b.To.Value} THEN '{b.Key}'";
            return null;
        }).OfType<string>();

        return $"SELECT CASE {string.Join(" ", cases)} END AS bucket_key, " +
               $"COUNT(*) AS doc_count FROM `{tableName}`{wc} GROUP BY bucket_key";
    }

    private static string? BuildEq(string col, string pName, SearchValue? val, DynamicParameters param)
    {
        param.Add(pName, GetScalarValue(val));
        return $"`{col}` = @{pName}";
    }

    private static string? BuildIn(string col, string pName, SearchValue? val, DynamicParameters param)
    {
        var list = val?.StringList?.Values.ToList() ?? [];
        if (list.Count == 0) return null;
        param.Add(pName, list);
        return $"`{col}` IN @{pName}";
    }

    private static string Condition(string expr, string pName, object? value, DynamicParameters param)
    {
        param.Add(pName, value);
        return expr;
    }

    private static object? GetScalarValue(SearchValue? v) => v?.KindCase switch
    {
        SearchValue.KindOneofCase.StringVal => (object?)v.StringVal,
        SearchValue.KindOneofCase.NumberVal => v.NumberVal,
        SearchValue.KindOneofCase.BoolVal   => v.BoolVal,
        _                                   => null
    };

    private static string DateFormatFor(string? interval) => interval?.ToLowerInvariant() switch
    {
        "minute"  => "%Y-%m-%d %H:%i",
        "hour"    => "%Y-%m-%d %H",
        "day"     => "%Y-%m-%d",
        "week"    => "%Y-%u",
        "quarter" => "%Y-%m",
        "year"    => "%Y",
        _         => "%Y-%m"
    };
}
```

- [ ] **Step 5: Add StarRocks reference to Api.Tests project**

In `Iverson.Api.Tests/Iverson.Api.Tests.csproj`, add:
```xml
<ProjectReference Include="../Iverson.StarRocks/Iverson.StarRocks.csproj" />
```

- [ ] **Step 6: Run tests**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal --filter "EngagementStoreConsumer"
```
Expected: 5 new consumer tests pass.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs \
        Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs \
        Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
git commit -m "feat: rewrite EngagementStoreConsumer to use StarRocks; add StarRocksQueryBuilder"
```

---

### Task 5: Rewrite `ObjectSearchGrpcService`

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (full rewrite of ES paths)
- Modify: `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (full rewrite)

- [ ] **Step 1: Write failing tests**

Replace entire `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`:

```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectSearchGrpcServiceTests
{
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _registry;
    private readonly IStarRocksRepository _sr;
    private readonly IVectorService _vector;
    private readonly IEmbeddingService _embedding;
    private readonly ObjectSearchGrpcService _sut;

    public ObjectSearchGrpcServiceTests()
    {
        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry  = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sr        = Substitute.For<IStarRocksRepository>();
        _vector    = Substitute.For<IVectorService>();
        _embedding = Substitute.For<IEmbeddingService>();

        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        _sut = new ObjectSearchGrpcService(
            _registry, _sr, _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance);
    }

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
    {
        var written = new List<T>();
        var writer  = Substitute.For<IServerStreamWriter<T>>();
        writer.WriteAsync(Arg.Do<T>(written.Add), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
        return (writer, written);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Search(
            new SearchRequest { TypeName = "Ghost" }, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Search_CallsStarRocksQueryAsync_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var fakeRow = new Dictionary<string, object> { ["Name"] = "Alice" };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await _sr.Received(1).QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public async Task Search_SqlContainsTableName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        capturedSql.Should().Contain("authors");
    }

    [Fact]
    public async Task Search_ContainsClause_ProducesLikeSql()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var request = new SearchRequest { TypeName = "Author", Query = new SearchQuery() };
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Contains,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(request, writer, TestServerCallContext.Create());

        capturedSql.Should().Contain("LIKE");
    }

    // ── Aggregate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var act = async () => await _sut.Aggregate(
            new AggregateRequest { TypeName = "Ghost" }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenNoAggregationsSpecified()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var act = async () => await _sut.Aggregate(
            new AggregateRequest { TypeName = "Author" }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Aggregate_Terms_ReturnsBuckets()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var row1 = new Dictionary<string, object> { ["bucket_key"] = "Alice", ["doc_count"] = 10L };
        var row2 = new Dictionary<string, object> { ["bucket_key"] = "Bob",   ["doc_count"] = 5L  };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)row1, (dynamic)row2 }.AsEnumerable());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "name_terms", Type = AggregationType.Terms, Field = "Name", Size = 5
        });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].Name.Should().Be("name_terms");
        response.Results[0].Buckets.Should().HaveCount(2);
        response.Results[0].Buckets[0].Key.Should().Be("Alice");
        response.Results[0].Buckets[0].DocCount.Should().Be(10);
    }

    [Fact]
    public async Task Aggregate_Avg_ReturnsMetricValue()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var metricRow = new Dictionary<string, object> { ["metric_val"] = 42.5 };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)metricRow }.AsEnumerable());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "bio_avg", Type = AggregationType.Avg, Field = "Bio"
        });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].MetricValue.Should().BeApproximately(42.5, 0.001);
    }

    // ── SearchSimilar / SearchChunks — Qdrant paths unchanged ─────────────────

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Ghost", Property = "Name", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchSimilar_CallsEmbedThenQdrant_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var vectorResult = new VectorSearchResult(
            Id: 1, Score: 0.95,
            Payload: new Dictionary<string, string> { ["title"] = "Great Article" });

        _vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Score.Should().BeApproximately(0.95f, 0.001f);
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure**

```bash
dotnet build Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | head -20
```
Expected: `ObjectSearchGrpcService` constructor mismatch.

- [ ] **Step 3: Rewrite `ObjectSearchGrpcService.cs`**

Replace entire file:

```csharp
using System.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging;

using SrAggKind   = Iverson.StarRocks.AggregationKind;
using SrAggSpec   = Iverson.StarRocks.AggregationDescriptor;
using SrAggResult = Iverson.StarRocks.AggregationResult;
using SrAggBucket = Iverson.StarRocks.AggregationBucket;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;
using ProtoAggBucket = Iverson.Client.Contracts.AggregationBucket;
using ProtoAggResult = Iverson.Client.Contracts.AggregationResult;
using ProtoAggSpec   = Iverson.Client.Contracts.AggregationSpec;

namespace Iverson.Api.Grpc;

/// <summary>
/// Three search paths:
///   Search        — StarRocks SQL WHERE query.
///   SearchSimilar — Embeds the query text and searches the entity's Qdrant named vector collection.
///   SearchChunks  — Embeds the query text and searches the {collection}_chunks Qdrant collection.
/// </summary>
public sealed class ObjectSearchGrpcService(
    SchemaRegistry registry,
    IStarRocksRepository sr,
    IVectorService vector,
    IEmbeddingService embedding,
    ILogger<ObjectSearchGrpcService> logger)
    : ObjectSearchService.ObjectSearchServiceBase
{
    // ── SQL Search ─────────────────────────────────────────────────────────────

    public override async Task Search(
        SearchRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Search] type={Type} clauses={Clauses} page={Page}/{Size}",
                request.TypeName, request.Query?.Clauses.Count ?? 0, request.Page, request.PageSize);

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, request.Query, request.Page, request.PageSize);

        var rows = await sr.QueryAsync<dynamic>(sql, param);

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            await responseStream.WriteAsync(new SearchResponse
            {
                Data    = DictToProtoStruct(dict),
                Score   = 1.0f,
                TraceId = request.TraceId
            }, context.CancellationToken);
        }
    }

    // ── Vector Similarity Search ───────────────────────────────────────────────

    public override async Task SearchSimilar(
        SearchSimilarRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var vectorDesc = schema.VectorFields.FirstOrDefault(v =>
            string.Equals(v.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonEmbedding] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        logger.LogInformation("[SearchSimilar] type={Type} property={Prop} topK={K}",
            request.TypeName, request.Property, request.TopK);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName = vectorDesc.PropertyName.ToSnakeCase() + "_vector";
        var topK       = (ulong)Math.Max(1, (int)request.TopK);
        var results    = await vector.SearchNamedAsync(schema.CollectionName, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
                protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

            await responseStream.WriteAsync(new SearchResponse
            {
                Data    = protoStruct,
                Score   = (float)r.Score,
                TraceId = request.TraceId
            }, context.CancellationToken);
        }
    }

    // ── Chunk / RAG Search ─────────────────────────────────────────────────────

    public override async Task SearchChunks(
        SearchChunksRequest request,
        IServerStreamWriter<ChunkSearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
            string.Equals(c.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonChunk] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        logger.LogInformation("[SearchChunks] type={Type} property={Prop} topK={K}",
            request.TypeName, request.Property, request.TopK);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName       = chunkDesc.PropertyName.ToSnakeCase() + "_vector";
        var chunksCollection = schema.CollectionName + "_chunks";
        var topK             = (ulong)Math.Max(1, (int)request.TopK);
        var results          = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            r.Payload.TryGetValue("text",      out var chunkText);
            r.Payload.TryGetValue("parent_id", out var parentId);

            await responseStream.WriteAsync(new ChunkSearchResponse
            {
                ParentKey = parentId  ?? string.Empty,
                ChunkText = chunkText ?? string.Empty,
                Score     = (float)r.Score,
                TraceId   = request.TraceId
            }, context.CancellationToken);
        }
    }

    // ── Aggregation ────────────────────────────────────────────────────────────

    public override async Task<AggregateResponse> Aggregate(
        AggregateRequest request,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (request.Aggregations.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "At least one aggregation spec is required."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Aggregate] type={Type} aggs={Count}", request.TypeName, request.Aggregations.Count);

        var response = new AggregateResponse { TraceId = request.TraceId };

        foreach (var spec in request.Aggregations)
        {
            var srSpec = ProtoToSrSpec(spec);
            var result = await RunAggregationAsync(schema, request.Query, srSpec);
            if (result is not null) response.Results.Add(SrResultToProto(result));
        }

        return response;
    }

    private async Task<SrAggResult?> RunAggregationAsync(
        SchemaDescriptor schema, SearchQuery? query, SrAggSpec spec)
    {
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(schema.TableName, schema, query, spec);
        var rows = (await sr.QueryAsync<dynamic>(sql, param)).ToList();

        switch (spec.Kind)
        {
            case SrAggKind.Terms:
            case SrAggKind.DateHistogram:
            case SrAggKind.Range:
            {
                var buckets = rows
                    .Select(r => (IDictionary<string, object>)r)
                    .Where(r => r.TryGetValue("bucket_key", out var k) && k is not null)
                    .Select(r => new SrAggBucket(
                        r["bucket_key"]?.ToString() ?? string.Empty,
                        Convert.ToInt64(r["doc_count"])))
                    .ToList();
                return new SrAggResult(spec.Name, spec.Kind, Buckets: buckets);
            }

            default:
            {
                if (rows.Count == 0) return new SrAggResult(spec.Name, spec.Kind, MetricValue: null);
                var val = ((IDictionary<string, object>)rows[0]).GetValueOrDefault("metric_val");
                return new SrAggResult(spec.Name, spec.Kind,
                    MetricValue: val is null ? null : Convert.ToDouble(val));
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private static SrAggSpec ProtoToSrSpec(ProtoAggSpec proto) =>
        new(
            Name:             proto.Name,
            Kind:             ProtoKindToSr(proto.Type),
            Field:            proto.Field,
            Size:             proto.Size > 0 ? proto.Size : 10,
            CalendarInterval: string.IsNullOrEmpty(proto.CalendarInterval) ? null : proto.CalendarInterval,
            TimeZone:         string.IsNullOrEmpty(proto.TimeZone)         ? null : proto.TimeZone,
            RangeBuckets:     proto.RangeBuckets.Count > 0
                ? proto.RangeBuckets.Select(b => new SrRangeSpec(b.Key, b.From?.Value, b.To?.Value)).ToList()
                : null);

    private static SrAggKind ProtoKindToSr(AggregationType type) => type switch
    {
        AggregationType.Terms         => SrAggKind.Terms,
        AggregationType.DateHistogram => SrAggKind.DateHistogram,
        AggregationType.Range         => SrAggKind.Range,
        AggregationType.Avg           => SrAggKind.Avg,
        AggregationType.Sum           => SrAggKind.Sum,
        AggregationType.Min           => SrAggKind.Min,
        AggregationType.Max           => SrAggKind.Max,
        AggregationType.Count   => SrAggKind.Count,
        _                             => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static ProtoAggResult SrResultToProto(SrAggResult result)
    {
        var proto = new ProtoAggResult
        {
            Name        = result.Name,
            Type        = SrKindToProto(result.Kind),
            MetricValue = result.MetricValue ?? 0.0
        };
        if (result.Buckets is not null)
            foreach (var b in result.Buckets)
                proto.Buckets.Add(new ProtoAggBucket { Key = b.Key, DocCount = b.DocCount });
        return proto;
    }

    private static AggregationType SrKindToProto(SrAggKind kind) => kind switch
    {
        SrAggKind.Terms         => AggregationType.Terms,
        SrAggKind.DateHistogram => AggregationType.DateHistogram,
        SrAggKind.Range         => AggregationType.Range,
        SrAggKind.Avg           => AggregationType.Avg,
        SrAggKind.Sum           => AggregationType.Sum,
        SrAggKind.Min           => AggregationType.Min,
        SrAggKind.Max           => AggregationType.Max,
        SrAggKind.Count   => AggregationType.Count,
        _                       => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static Struct DictToProtoStruct(Dictionary<string, object?> doc)
    {
        var s = new Struct();
        foreach (var (k, v) in doc)
            s.Fields[k] = ToProtoValue(v);
        return s;
    }

    private static Value ToProtoValue(object? v) => v switch
    {
        null           => Value.ForNull(),
        string s       => Value.ForString(s),
        bool b         => Value.ForBool(b),
        double d       => Value.ForNumber(d),
        float f        => Value.ForNumber(f),
        int i          => Value.ForNumber(i),
        long l         => Value.ForNumber(l),
        _              => Value.ForString(v.ToString()!)
    };
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal --filter "ObjectSearchGrpcService"
```
Expected: all ObjectSearchGrpcService tests pass (ES tests still present — they'll fail at compile once we remove the reference in next task, so we fix that next).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat: rewrite ObjectSearchGrpcService to use StarRocks SQL"
```

---

### Task 6: Remove ES from `ObjectMappingGrpcService` and csproj references

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`
- Modify: `Iverson.Api/Iverson.Api.csproj`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Iverson.Api.Tests.csproj`

- [ ] **Step 1: Remove ES mock from `ObjectMappingGrpcServiceTests.cs`**

In `ObjectMappingGrpcServiceTests.cs`:
- Remove `using Iverson.Elasticsearch;`
- Remove `private readonly IElasticsearchService _es;`
- Remove `_es = Substitute.For<IElasticsearchService>();` from constructor
- Change `_sut = new ObjectMappingGrpcService(_sql, _es, _vector, _events, _registry, _embedding, ...)` to `_sut = new ObjectMappingGrpcService(_sql, _vector, _events, _registry, _embedding, ...)`
- In `MakeSchema` helper, remove `IndexName = typeName.ToLower() + "s",`

- [ ] **Step 2: Remove `_es` from `ObjectMappingGrpcService`**

In `ObjectMappingGrpcService.cs`:
- Remove `using Iverson.Elasticsearch;`
- Remove the ES-related `using` aliases (`EsFieldType`, etc.)
- Remove `IElasticsearchService _elasticsearchService` from constructor parameters
- In `RegisterSchema`, remove the line `await _elasticsearchService.ApplyMappingAsync(SchemaBuilder.ToIndexSchema(descriptor));`
- In `DetermineTargetStores`, remove `if (_registry.HasEngagementDependents(typeName)) stores |= StoreTarget.EngagementFanout;`

- [ ] **Step 3: Remove `EngagementFanout` from `ObjectPersistenceGrpcService.cs`**

In each of the `Post` and `Update` methods, remove the line:
```csharp
if (registry.HasEngagementDependents(request.TypeName)) targetStores |= StoreTarget.EngagementFanout;
```

- [ ] **Step 4: Remove ES project references**

In `Iverson.Api/Iverson.Api.csproj`, remove:
```xml
<ProjectReference Include="..\Iverson.Elasticsearch\Iverson.Elasticsearch.csproj" />
```

In `Iverson.Api.Tests/Iverson.Api.Tests.csproj`, remove:
```xml
<ProjectReference Include="../Iverson.Elasticsearch/Iverson.Elasticsearch.csproj" />
```

- [ ] **Step 5: Run tests**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal
```
Expected: all non-ES tests pass. The `Iverson.Elasticsearch.Tests` project will fail to build (it references the Elasticsearch project which still exists) — that's fine, it gets deleted in Task 8.

Actually at this point, the solution still includes ES projects in the `.slnx`, so `dotnet test` on the full solution will build them. Run test filtered instead:

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal \
  --filter "FullyQualifiedName!~Iverson.Elasticsearch"
```
Expected: all non-ES tests pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs \
        Iverson.Server/Iverson.Api/Iverson.Api.csproj \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
git commit -m "refactor: remove IElasticsearchService dependency from ObjectMappingGrpcService"
```

---

### Task 7: Update `Program.cs` and health endpoint

**Files:**
- Modify: `Iverson.Api/Program.cs`

- [ ] **Step 1: Update Program.cs**

Replace the ES-related blocks in `Program.cs`:

a) In the OpenTelemetry tracing sources, replace `"Iverson.Elasticsearch"` with `"Iverson.StarRocks"`:
```csharp
.AddSource(
    "Iverson.Sql",
    "Iverson.StarRocks",
    "Iverson.Vector",
    "Iverson.Events",
    "Iverson.Embeddings")
```

b) Replace `builder.Services.AddElasticsearch(...)` with:
```csharp
builder.Services.AddStarRocks(
    cfg.GetConnectionString("StarRocks")
    ?? "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;AllowPublicKeyRetrieval=true;");
```

Add `using Iverson.StarRocks;` at the top.

c) Update the `/health` endpoint to replace `IElasticsearchService es` with `IStarRocksRepository sr`:

```csharp
app.MapGet("/health", async (
    IPostgresRepository db,
    IStarRocksRepository sr,
    IVectorService vector,
    IEventProducer kafka) =>
{
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var srTask     = sr.IsHealthyAsync();
    var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
    var kafkaTask  = kafka.ProduceAsync("iverson-health-probe", "probe", new { ts = DateTime.UtcNow })
                         .ContinueWith(t => t.IsCompletedSuccessfully);

    await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

    var checks = new
    {
        postgres   = pgTask.Result,
        starrocks  = await srTask,
        qdrant     = vectorTask.Result,
        kafka      = kafkaTask.Result
    };

    var allHealthy = checks.postgres && checks.starrocks && checks.qdrant && checks.kafka;

    return allHealthy
        ? Results.Ok(new { status = "healthy", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);
})
.WithName("Health");
```

d) Replace the `/probe/elasticsearch` endpoint with a StarRocks probe:
```csharp
app.MapGet("/probe/starrocks", async (IStarRocksRepository sr) =>
{
    var healthy = await sr.IsHealthyAsync();
    return Results.Ok(new { connected = healthy, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeStarRocks");
```

e) Remove `using Iverson.Elasticsearch;` import.

- [ ] **Step 2: Update `EngagementStoreConsumer` registration in Program.cs**

The `EngagementStoreConsumer` constructor now takes `IStarRocksRepository` instead of `IElasticsearchService`. The `AddHostedService<EngagementStoreConsumer>()` call stays unchanged — DI resolves the new constructor automatically.

- [ ] **Step 3: Build to verify no compile errors**

```bash
dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj
```
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.Api/Program.cs
git commit -m "feat: wire StarRocks into Program.cs; update health endpoint"
```

---

### Task 8: Delete Elasticsearch projects

**Files:**
- Delete: `Iverson.Elasticsearch/` directory
- Delete: `Iverson.Elasticsearch.Tests/` directory
- Modify: `Iverson.Server.slnx` — remove ES project entries

- [ ] **Step 1: Remove ES entries from solution**

In `Iverson.Server.slnx`, remove:
```xml
  <Project Path="Iverson.Elasticsearch.Tests/Iverson.Elasticsearch.Tests.csproj" />
  <Project Path="Iverson.Elasticsearch/Iverson.Elasticsearch.csproj" />
```

- [ ] **Step 2: Delete ES project directories**

```bash
rm -rf Iverson.Server/Iverson.Elasticsearch Iverson.Server/Iverson.Elasticsearch.Tests
```

- [ ] **Step 3: Run full test suite**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx -v minimal
```
Expected: all tests pass, no ES tests in output.

- [ ] **Step 4: Commit**

```bash
git add -A Iverson.Server/
git commit -m "chore: delete Iverson.Elasticsearch and Iverson.Elasticsearch.Tests projects"
```

---

## Self-Review

**Spec coverage check:**
- ✅ StarRocks replaces ES as engagement store
- ✅ `StoreTarget.EngagementFanout` removed
- ✅ `SchemaDescriptor.IndexName` removed
- ✅ `SchemaRegistry` inverse index removed
- ✅ `EngagementStoreConsumer` rewrites to StarRocks upsert/delete
- ✅ `ObjectSearchGrpcService` rewrites to StarRocks SQL
- ✅ `ObjectMappingGrpcService` removes ES dep and `ApplyMappingAsync` call
- ✅ `ObjectPersistenceGrpcService` removes `EngagementFanout`
- ✅ `Program.cs` updated with StarRocks registration and health
- ✅ docker-compose updated
- ✅ ES projects deleted from solution
- ✅ `SchemaBuilder.ToStarRocksTableSchema` added for use by consumer
- ✅ `StarRocksQueryBuilder` added for Search and Aggregate SQL

**Type consistency check:**
- `StarRocksTableSchema` / `StarRocksColumnSchema` defined in Task 1, used in Tasks 4 and 5 ✅
- `AggregationKind` / `AggregationDescriptor` / `AggregationResult` / `AggregationBucket` defined in Task 1, used in Tasks 4/5 ✅
- `IStarRocksRepository` defined in Task 1, injected in Tasks 4 and 5 ✅
- `SchemaBuilder.ToStarRocksTableSchema` added in Task 3, called in Task 4 ✅
- `SchemaDescriptor.IndexName` removed in Task 3; `SchemaFixtures` updated in Task 3 ✅

**Known limitation:** `StarRocksRepository.ApplyTableAsync` opens a connection without a database to run `CREATE DATABASE` — the `MySqlConnectionStringBuilder` requires the connection string to include a `Database=` segment, otherwise `_dbName` is empty. Ensure the connection string always includes `Database=iverson`.
