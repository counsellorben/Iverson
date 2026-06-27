# StarRocks Unique Key + Sync MV Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the StarRocks `PRIMARY KEY` table model with `UNIQUE KEY`, create per-entity sync materialized views sorted by `[IversonSearchKey]`-annotated columns, and add explicit field projection to `BuildSearch` so the optimizer can route narrow queries through the MV.

**Architecture:** Two new client attributes (`[IversonSearchKey]`, `[IversonLargeField]`) flow through the proto `PropertyDescriptor` to `SchemaDescriptor`, then into `StarRocksTableSchema`. `ApplyTableAsync` uses the enriched schema to emit `UNIQUE KEY` DDL and a `CREATE MATERIALIZED VIEW` statement. `BuildSearch` emits an explicit `SELECT` column list; clients may supply a `fields` include-list in `SearchRequest` to restrict the projection so the StarRocks optimizer routes to the MV. A `reset-starrocks` LoadTest command drops all tables (and their MVs) for the greenfield migration workflow.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions, Dapper/MySqlConnector, protobuf-net/grpc.

## Global Constraints

- All server-side test commands run from `Iverson.Server/`; client-side test commands run from the repo root (`/home/ben/repositories/Iverson/`)
- Do not change method signatures on `IPostgresRepository`, `IStarRocksRepository`, `IEventProducer`, or any gRPC service base class
- Commit after each task passes all tests
- Do not add comments explaining what code does

## File Map

| Task | Files |
|------|-------|
| 1 | **Create** `Iverson.Client/Iverson.Client.Attributes/IversonSearchKeyAttribute.cs`, `IversonLargeFieldAttribute.cs` · **Modify** `Iverson.Client/Iverson.Client.Contracts/Protos/object_mapping.proto`, `Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs`, `Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` |
| 2 | **Modify** `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`, `Iverson.Server/Iverson.StarRocks/StarRocksTableSchema.cs`, `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` |
| 3 | **Modify** `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`, `Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs` |
| 4 | **Modify** `Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`, `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs`, `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs` |
| 5 | **Modify** `Iverson.Client/Iverson.Client.Contracts/Protos/object_search.proto`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` |
| 6 | **Modify** `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs` |
| 7 | **Modify** `Iverson.Server/Iverson.LoadTest/Program.cs` |

---

### Task 1: New attributes, proto fields, SchemaRegistrar

**Interfaces:**
- Produces: `IversonSearchKeyAttribute(int Order)`, `IversonLargeFieldAttribute`; proto `PropertyDescriptor` fields `is_search_key`, `search_key_order`, `is_large_field`; `SchemaRegistrar` sets them when building `TypeDescriptor`

- [ ] **Step 1: Write failing tests**

Add to `Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` — new test entity at the top of the file alongside the other test entities:

```csharp
[IversonEntity]
internal sealed class SearchAnnotationTestEntity
{
    [IversonKey]          public Guid            Id          { get; set; }
    [IversonSearchKey(0)] public string          Category    { get; set; } = "";
    [IversonSearchKey(1)] public DateTimeOffset  PublishedAt { get; set; }
    [IversonLargeField]   public string          Body        { get; set; } = "";
}
```

Add two new test methods to the `SchemaRegistrarTests` class:

```csharp
[Fact]
public async Task RegisterAllAsync_SetsIsSearchKey_AndSearchKeyOrder_OnAnnotatedProperties()
{
    SchemaRequest? req = null;
    _mappingClient
        .RegisterSchemaAsync(
            Arg.Do<SchemaRequest>(r =>
            {
                if (r.RootType?.TypeName == "SearchAnnotationTestEntity") req = r;
            }),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>())
        .Returns(new AsyncUnaryCall<SchemaResponse>(
            Task.FromResult(new SchemaResponse { Success = true }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    await _sut.RegisterAllAsync();

    req.Should().NotBeNull();
    var category    = req!.RootType!.Properties.Single(p => p.Name == "Category");
    var publishedAt = req!.RootType!.Properties.Single(p => p.Name == "PublishedAt");
    category.IsSearchKey.Should().BeTrue();
    category.SearchKeyOrder.Should().Be(0);
    publishedAt.IsSearchKey.Should().BeTrue();
    publishedAt.SearchKeyOrder.Should().Be(1);
}

[Fact]
public async Task RegisterAllAsync_SetsIsLargeField_OnAnnotatedProperty()
{
    SchemaRequest? req = null;
    _mappingClient
        .RegisterSchemaAsync(
            Arg.Do<SchemaRequest>(r =>
            {
                if (r.RootType?.TypeName == "SearchAnnotationTestEntity") req = r;
            }),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>())
        .Returns(new AsyncUnaryCall<SchemaResponse>(
            Task.FromResult(new SchemaResponse { Success = true }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    await _sut.RegisterAllAsync();

    req.Should().NotBeNull();
    var body = req!.RootType!.Properties.Single(p => p.Name == "Body");
    body.IsLargeField.Should().BeTrue();
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
# from repo root
dotnet test Iverson.Client/Iverson.Client.Core.Tests/ --filter "SetsIsSearchKey|SetsIsLargeField" -v n
```

Expected: compile error — `IversonSearchKeyAttribute`, `IversonLargeFieldAttribute`, `IsSearchKey`, `SearchKeyOrder`, `IsLargeField` not defined.

- [ ] **Step 3: Create `IversonSearchKeyAttribute`**

Create `Iverson.Client/Iverson.Client.Attributes/IversonSearchKeyAttribute.cs`:

```csharp
namespace Iverson.Client.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonSearchKeyAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}
```

- [ ] **Step 4: Create `IversonLargeFieldAttribute`**

Create `Iverson.Client/Iverson.Client.Attributes/IversonLargeFieldAttribute.cs`:

```csharp
namespace Iverson.Client.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonLargeFieldAttribute : Attribute;
```

- [ ] **Step 5: Add fields to `PropertyDescriptor` proto**

In `Iverson.Client/Iverson.Client.Contracts/Protos/object_mapping.proto`, add three fields to `message PropertyDescriptor` after field 13:

```protobuf
    bool   is_search_key    = 14;  // [IversonSearchKey] present
    int32  search_key_order = 15;  // sort position when is_search_key = true
    bool   is_large_field   = 16;  // [IversonLargeField] present — excluded from MV
```

- [ ] **Step 6: Update `SchemaRegistrar.AddAnnotations`**

In `Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs`, extend `AddAnnotations` to handle the two new attributes:

```csharp
private static void AddAnnotations(PropertyDescriptor descriptor, PropertyInfo prop)
{
    if (prop.GetCustomAttribute<IversonEmbeddingAttribute>() is not null)
    {
        descriptor.IsEmbedding = true;
        descriptor.VectorDim   = 0;
        descriptor.ModelId     = string.Empty;
    }

    if (prop.GetCustomAttribute<IversonChunkAttribute>() is { } chunk)
    {
        descriptor.IsChunk        = true;
        descriptor.ChunkMaxTokens = chunk.MaxTokens;
        descriptor.ChunkOverlap   = chunk.Overlap;
        descriptor.ChunkModelId   = string.Empty;
        descriptor.ChunkVectorDim = 0;
    }

    if (prop.GetCustomAttribute<IversonSearchKeyAttribute>() is { } sk)
    {
        descriptor.IsSearchKey    = true;
        descriptor.SearchKeyOrder = sk.Order;
    }

    if (prop.GetCustomAttribute<IversonLargeFieldAttribute>() is not null)
        descriptor.IsLargeField = true;
}
```

- [ ] **Step 7: Run tests**

```bash
# from repo root
dotnet test Iverson.Client/Iverson.Client.Core.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Client/Iverson.Client.Attributes/IversonSearchKeyAttribute.cs \
        Iverson.Client/Iverson.Client.Attributes/IversonLargeFieldAttribute.cs \
        Iverson.Client/Iverson.Client.Contracts/Protos/object_mapping.proto \
        Iverson.Client/Iverson.Client.Core/SchemaRegistrar.cs \
        Iverson.Client/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs
git commit -m "feat: add [IversonSearchKey] and [IversonLargeField] attributes — wire through proto and SchemaRegistrar"
```

---

### Task 2: SchemaDescriptor, SchemaBuilder, StarRocksTableSchema

**Interfaces:**
- Consumes: Task 1's `PropertyDescriptor.IsSearchKey`, `.SearchKeyOrder`, `.IsLargeField`, `.IsEmbedding`, `.IsChunk`
- Produces: `SchemaDescriptor.SearchKeyColumns: IReadOnlyList<(string Name, int Order)>`, `SchemaDescriptor.LargeFieldColumns: IReadOnlySet<string>`; `StarRocksTableSchema.MvSortKey: IReadOnlyList<string>`, `StarRocksTableSchema.MvExcludedColumns: IReadOnlySet<string>`

- [ ] **Step 1: Write failing tests**

Add to `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`:

```csharp
[Fact]
public void BuildDescriptor_PopulatesSearchKeyColumns_FromIsSearchKeyProperties()
{
    var embedding = Substitute.For<IEmbeddingService>();
    embedding.Dimension.Returns(768);
    embedding.ModelId.Returns("nomic-embed-text");

    var typeDesc = new TypeDescriptor { TypeName = "Article" };
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,     IsKey = true });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Category",    ClrType = ClrType.ClrString,   IsSearchKey = true,  SearchKeyOrder = 0 });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "PublishedAt", ClrType = ClrType.ClrDatetime, IsSearchKey = true,  SearchKeyOrder = 1 });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString,   IsLargeField = true });

    var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

    descriptor.SearchKeyColumns.Should().HaveCount(2);
    descriptor.SearchKeyColumns[0].Should().Be(("Category", 0));
    descriptor.SearchKeyColumns[1].Should().Be(("PublishedAt", 1));
}

[Fact]
public void BuildDescriptor_PopulatesLargeFieldColumns_FromExplicitAndImplicitSources()
{
    var embedding = Substitute.For<IEmbeddingService>();
    embedding.Dimension.Returns(768);
    embedding.ModelId.Returns("nomic-embed-text");

    var typeDesc = new TypeDescriptor { TypeName = "Article" };
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,   IsKey = true });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString, IsLargeField = true });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "EmbedField",  ClrType = ClrType.ClrString, IsEmbedding  = true });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "ChunkField",  ClrType = ClrType.ClrString, IsChunk      = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
    typeDesc.Properties.Add(new PropertyDescriptor { Name = "Normal",      ClrType = ClrType.ClrString });

    var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

    descriptor.LargeFieldColumns.Should().BeEquivalentTo(
        new[] { "Body", "EmbedField", "ChunkField" },
        opts => opts.WithoutStrictOrdering());
    descriptor.LargeFieldColumns.Should().NotContain("Normal");
}

[Fact]
public void ToStarRocksTableSchema_PopulatesMvSortKey_AndMvExcludedColumns()
{
    var descriptor = new SchemaDescriptor
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id",          "UUID",  false),
        ScalarColumns  = [
            new ColumnDescriptor("Category",    "TEXT",        false),
            new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
            new ColumnDescriptor("Body",        "TEXT",        false),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = [],
        SearchKeyColumns = [("Category", 0), ("PublishedAt", 1)],
        LargeFieldColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body" }
    };

    var schema = SchemaBuilder.ToStarRocksTableSchema(descriptor);

    schema.MvSortKey.Should().Equal("Category", "PublishedAt");
    schema.MvExcludedColumns.Should().Contain("Body");
    schema.MvExcludedColumns.Should().NotContain("Category");
}
```

- [ ] **Step 2: Run to confirm failures**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ --filter "PopulatesSearchKeyColumns|PopulatesLargeFieldColumns|PopulatesMvSortKey" -v n
```

Expected: compile errors — `SearchKeyColumns`, `LargeFieldColumns`, `IsLargeField` not defined.

- [ ] **Step 3: Update `SchemaDescriptor`**

In `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, add two new properties with default values (preserving backward compatibility with all existing `new SchemaDescriptor { ... }` constructors):

```csharp
public sealed record SchemaDescriptor
{
    public required string TypeName       { get; init; }
    public required string TableName      { get; init; }
    public string?         CollectionName { get; init; }

    public required ColumnDescriptor                    KeyColumn     { get; init; }
    public required IReadOnlyList<ColumnDescriptor>     ScalarColumns { get; init; }
    public required IReadOnlyList<ForeignKeyDescriptor> FkColumns     { get; init; }
    public required IReadOnlyList<VectorDescriptor>     VectorFields  { get; init; }
    public required IReadOnlyList<ChunkDescriptor>      ChunkFields   { get; init; }
    public required IReadOnlyList<RelationDescriptor>   Relations     { get; init; }

    public IReadOnlyList<(string Name, int Order)> SearchKeyColumns  { get; init; } = [];
    public IReadOnlySet<string>                    LargeFieldColumns { get; init; } = new HashSet<string>();
}
```

- [ ] **Step 4: Update `StarRocksTableSchema`**

In `Iverson.Server/Iverson.StarRocks/StarRocksTableSchema.cs`, extend the record with two new init-only properties:

```csharp
public sealed record StarRocksTableSchema(
    string TableName,
    StarRocksColumnSchema KeyColumn,
    IReadOnlyList<StarRocksColumnSchema> Columns)
{
    public IReadOnlyList<string> MvSortKey         { get; init; } = [];
    public IReadOnlySet<string>  MvExcludedColumns { get; init; } = new HashSet<string>();
}
```

- [ ] **Step 5: Update `SchemaBuilder.BuildDescriptor`**

In `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`, update `BuildDescriptor` to populate the new collections. Replace the existing `foreach` that builds scalars/fks/vectors/chunks with:

```csharp
var scalars    = new List<ColumnDescriptor>();
var fks        = new List<ForeignKeyDescriptor>();
var vectors    = new List<VectorDescriptor>();
var chunks     = new List<ChunkDescriptor>();
var searchKeys = new List<(string Name, int Order)>();
var largeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var prop in typeDesc.Properties.Where(p => !p.IsKey))
{
    var sqlType = ClrTypeToSql(prop.ClrType, prop.IsArray);
    scalars.Add(new ColumnDescriptor(prop.Name, sqlType, prop.IsNullable));

    if (prop.IsEmbedding)
    {
        vectors.Add(new VectorDescriptor(prop.Name, embedding.Dimension, embedding.ModelId));
        largeFields.Add(prop.Name);
    }

    if (prop.IsChunk)
    {
        chunks.Add(new ChunkDescriptor(prop.Name, prop.ChunkMaxTokens, prop.ChunkOverlap, embedding.ModelId, embedding.Dimension));
        largeFields.Add(prop.Name);
    }

    if (prop.IsLargeField)
        largeFields.Add(prop.Name);

    if (prop.IsSearchKey)
        searchKeys.Add((prop.Name, prop.SearchKeyOrder));

    if (prop.Name.EndsWith("Id",  StringComparison.OrdinalIgnoreCase) ||
        prop.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
    {
        var relatedType = typeDesc.Relations
            .FirstOrDefault(r => r.ForeignKey == prop.Name)?.RelatedType ?? string.Empty;
        fks.Add(new ForeignKeyDescriptor(prop.Name, relatedType));
    }
}

searchKeys.Sort((a, b) => a.Order.CompareTo(b.Order));
```

Update the `return new SchemaDescriptor { ... }` block to include the new collections:

```csharp
return new SchemaDescriptor
{
    TypeName          = typeDesc.TypeName,
    TableName         = tableName,
    CollectionName    = (vectors.Count > 0 || chunks.Count > 0) ? tableName : null,
    KeyColumn         = new ColumnDescriptor(keyProp.Name, ClrTypeToSql(keyProp.ClrType, false), false),
    ScalarColumns     = scalars,
    FkColumns         = fks,
    VectorFields      = vectors,
    ChunkFields       = chunks,
    Relations         = relations,
    SearchKeyColumns  = searchKeys,
    LargeFieldColumns = largeFields
};
```

- [ ] **Step 6: Update `SchemaBuilder.ToStarRocksTableSchema`**

Replace the existing one-liner with:

```csharp
internal static StarRocksTableSchema ToStarRocksTableSchema(SchemaDescriptor d) => new(
    d.TableName,
    new StarRocksColumnSchema(d.KeyColumn.Name, ClrTypeToStarRocksType(d.KeyColumn.SqlType), false),
    d.ScalarColumns.Select(c => new StarRocksColumnSchema(c.Name, ClrTypeToStarRocksType(c.SqlType), c.IsNullable)).ToList())
{
    MvSortKey         = d.SearchKeyColumns.Select(sk => sk.Name).ToList(),
    MvExcludedColumns = d.LargeFieldColumns
};
```

- [ ] **Step 7: Run tests**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs \
        Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs \
        Iverson.Server/Iverson.StarRocks/StarRocksTableSchema.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs
git commit -m "feat: add SearchKeyColumns and LargeFieldColumns to SchemaDescriptor; populate MvSortKey and MvExcludedColumns in StarRocksTableSchema"
```

---

### Task 3: `ApplyTableAsync` — UNIQUE KEY DDL + MV creation

**Interfaces:**
- Consumes: `StarRocksTableSchema.MvSortKey`, `StarRocksTableSchema.MvExcludedColumns` from Task 2
- Produces: `StarRocksRepository.BuildCreateTableDdl(StarRocksTableSchema): string` (internal static); `StarRocksRepository.BuildCreateMvDdl(StarRocksTableSchema): string?` (internal static, null if no MV); `ApplyTableAsync` uses both

- [ ] **Step 1: Write failing tests**

Add to `Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs`:

```csharp
[Fact]
public void BuildCreateTableDdl_EmitsUniqueKey_NotPrimaryKey()
{
    var schema = new StarRocksTableSchema(
        "articles",
        new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
        [new StarRocksColumnSchema("Title", "STRING", false)]);

    var ddl = StarRocksRepository.BuildCreateTableDdl(schema);

    ddl.Should().Contain("UNIQUE KEY(`Id`)");
    ddl.Should().NotContain("PRIMARY KEY");
    ddl.Should().Contain("CREATE TABLE IF NOT EXISTS `articles`");
    ddl.Should().Contain("`Id` VARCHAR(36) NOT NULL");
    ddl.Should().Contain("`Title` STRING NOT NULL");
}

[Fact]
public void BuildCreateTableDdl_NullableColumn_OmitsNotNull()
{
    var schema = new StarRocksTableSchema(
        "authors",
        new StarRocksColumnSchema("Id",  "VARCHAR(36)", false),
        [new StarRocksColumnSchema("Bio", "STRING",     true)]);

    var ddl = StarRocksRepository.BuildCreateTableDdl(schema);

    ddl.Should().Contain("`Bio` STRING\n");
    ddl.Should().NotContain("`Bio` STRING NOT NULL");
}

[Fact]
public void BuildCreateMvDdl_ReturnsDdl_WhenMvSortKeyIsPopulated()
{
    var schema = new StarRocksTableSchema(
        "articles",
        new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
        [
            new StarRocksColumnSchema("Category",    "STRING",   false),
            new StarRocksColumnSchema("PublishedAt", "DATETIME", false),
            new StarRocksColumnSchema("Body",        "STRING",   false),
        ])
    {
        MvSortKey         = ["Category", "PublishedAt"],
        MvExcludedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body" }
    };

    var ddl = StarRocksRepository.BuildCreateMvDdl(schema);

    ddl.Should().NotBeNull();
    ddl!.Should().Contain("CREATE MATERIALIZED VIEW IF NOT EXISTS `articles_search_mv`");
    ddl.Should().Contain("`Id`");
    ddl.Should().Contain("`Category`");
    ddl.Should().Contain("`PublishedAt`");
    ddl.Should().NotContain("`Body`");
    ddl.Should().Contain("ORDER BY `Category`, `PublishedAt`");
}

[Fact]
public void BuildCreateMvDdl_ReturnsNull_WhenNoMvSortKey()
{
    var schema = new StarRocksTableSchema(
        "authors",
        new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
        [new StarRocksColumnSchema("Name", "STRING", false)]);

    var ddl = StarRocksRepository.BuildCreateMvDdl(schema);

    ddl.Should().BeNull();
}
```

- [ ] **Step 2: Run to confirm failures**

```bash
# from Iverson.Server/
dotnet test Iverson.StarRocks.Tests/ --filter "BuildCreateTableDdl|BuildCreateMvDdl" -v n
```

Expected: compile error — `BuildCreateTableDdl` and `BuildCreateMvDdl` not defined.

- [ ] **Step 3: Extract DDL methods and update `ApplyTableAsync`**

In `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`, add two internal static methods and update `ApplyTableAsync`:

```csharp
internal static string BuildCreateTableDdl(StarRocksTableSchema schema)
{
    var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
    var colsSql = schema.Columns.Select(c =>
        $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

    return $"""
        CREATE TABLE IF NOT EXISTS `{schema.TableName}` (
            {keySql},
            {string.Join(",\n    ", colsSql)}
        ) ENGINE=OLAP
        UNIQUE KEY(`{schema.KeyColumn.Name}`)
        DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4
        PROPERTIES ("replication_num" = "1")
        """;
}

internal static string? BuildCreateMvDdl(StarRocksTableSchema schema)
{
    if (schema.MvSortKey.Count == 0) return null;

    var mvCols = schema.Columns
        .Where(c => !schema.MvExcludedColumns.Contains(c.Name))
        .Select(c => $"`{c.Name}`")
        .Prepend($"`{schema.KeyColumn.Name}`");

    var colList = string.Join(", ", mvCols);
    var sortKey = string.Join(", ", schema.MvSortKey.Select(k => $"`{k}`"));

    return $"CREATE MATERIALIZED VIEW IF NOT EXISTS `{schema.TableName}_search_mv` " +
           $"AS SELECT {colList} FROM `{schema.TableName}` ORDER BY {sortKey}";
}
```

Replace the `if (exists == 0)` branch inside `ApplyTableAsync` to use the new methods:

```csharp
if (exists == 0)
{
    await conn.ExecuteAsync(BuildCreateTableDdl(schema));
    logger.LogInformation("Created StarRocks table {Table}", schema.TableName);

    var mvDdl = BuildCreateMvDdl(schema);
    if (mvDdl is not null)
    {
        await conn.ExecuteAsync(mvDdl);
        logger.LogInformation("Created materialized view {Table}_search_mv", schema.TableName);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
# from Iverson.Server/
dotnet test Iverson.StarRocks.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs \
        Iverson.Server/Iverson.StarRocks/StarRocksTableSchema.cs \
        Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs
git commit -m "feat: ApplyTableAsync emits UNIQUE KEY DDL and creates sync MV when MvSortKey is populated"
```

---

### Task 4: `BuildSearch` — explicit SELECT + field projection

**Interfaces:**
- Consumes: `SchemaDescriptor.ScalarColumns`, `SchemaDescriptor.KeyColumn` from Task 2
- Produces: `BuildSearch(..., IReadOnlyList<string>? fields = null)` — emits explicit column list; key always included; unknown field names ignored

- [ ] **Step 1: Add fixture to `SchemaFixtures.cs`**

In `Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, add a new fixture at the end of the class:

```csharp
// Article with search key and large-field metadata for projection tests
public static SchemaDescriptor ArticleWithProjectionSchema() => new()
{
    TypeName       = "Article",
    TableName      = "articles",
    CollectionName = null,
    KeyColumn      = new ColumnDescriptor("Id",          "uuid",        false),
    ScalarColumns  =
    [
        new ColumnDescriptor("Title",       "text",        false),
        new ColumnDescriptor("Category",    "text",        false),
        new ColumnDescriptor("WordCount",   "integer",     false),
        new ColumnDescriptor("PublishedAt", "timestamptz", false),
        new ColumnDescriptor("Body",        "text",        false),
    ],
    FkColumns    = [],
    VectorFields = [],
    ChunkFields  = [],
    Relations    = [],
    SearchKeyColumns  = [("Category", 0), ("PublishedAt", 1)],
    LargeFieldColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body" }
};
```

- [ ] **Step 2: Write failing tests for `BuildSearch`**

Add to `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs`:

```csharp
// ── BuildSearch — explicit SELECT columns ─────────────────────────────────────

[Fact]
public void BuildSearch_NoFields_SelectsAllColumnsExplicitly()
{
    var schema = SchemaFixtures.ArticleWithProjectionSchema();

    var (sql, _) = StarRocksQueryBuilder.BuildSearch("articles", schema, null, 0, 10);

    sql.Should().StartWith("SELECT ");
    sql.Should().NotContain("SELECT *");
    sql.Should().Contain("`Id`");
    sql.Should().Contain("`Title`");
    sql.Should().Contain("`Body`");
    sql.Should().Contain("`Category`");
    sql.Should().Contain("`PublishedAt`");
}

[Fact]
public void BuildSearch_WithFields_SelectsOnlyRequestedColumnsAndKey()
{
    var schema = SchemaFixtures.ArticleWithProjectionSchema();

    var (sql, _) = StarRocksQueryBuilder.BuildSearch(
        "articles", schema, null, 0, 10,
        fields: ["Category", "PublishedAt", "Title"]);

    sql.Should().Contain("`Id`");
    sql.Should().Contain("`Category`");
    sql.Should().Contain("`PublishedAt`");
    sql.Should().Contain("`Title`");
    sql.Should().NotContain("`Body`");
    sql.Should().NotContain("`WordCount`");
}

[Fact]
public void BuildSearch_WithFields_KeyAlwaysIncludedEvenIfNotRequested()
{
    var schema = SchemaFixtures.ArticleWithProjectionSchema();

    var (sql, _) = StarRocksQueryBuilder.BuildSearch(
        "articles", schema, null, 0, 10,
        fields: ["Category"]);

    sql.Should().Contain("`Id`");
    sql.Should().Contain("`Category`");
    sql.Should().NotContain("`Body`");
}

[Fact]
public void BuildSearch_WithFields_UnknownFieldNamesAreIgnored()
{
    var schema = SchemaFixtures.ArticleWithProjectionSchema();

    var (sql, _) = StarRocksQueryBuilder.BuildSearch(
        "articles", schema, null, 0, 10,
        fields: ["Category", "NonExistentField"]);

    sql.Should().Contain("`Id`");
    sql.Should().Contain("`Category`");
    sql.Should().NotContain("NonExistentField");
}
```

- [ ] **Step 3: Run to confirm failures**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ --filter "BuildSearch_NoFields|BuildSearch_WithFields" -v n
```

Expected: compile error on the new `fields` parameter.

- [ ] **Step 4: Update `BuildSearch` in `StarRocksQueryBuilder`**

In `Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`, replace the `BuildSearch` method and add a `BuildSelectColumns` helper:

```csharp
internal static (string Sql, DynamicParameters Param) BuildSearch(
    string tableName,
    SchemaDescriptor schema,
    SearchQuery? query,
    int page,
    int pageSize,
    IReadOnlyList<string>? fields = null)
{
    var param = new DynamicParameters();
    var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
    var order = BuildOrder(schema, query?.Sort);

    var limit  = pageSize > 0 ? pageSize : 50;
    var offset = page > 0 ? page * limit : 0;

    var selectCols = BuildSelectColumns(schema, fields);
    var sb = new StringBuilder($"SELECT {selectCols} FROM `{tableName}`");
    if (where.Length > 0) sb.Append($" WHERE {where}");
    if (order.Length > 0) sb.Append($" ORDER BY {order}");
    sb.Append($" LIMIT {limit} OFFSET {offset}");

    return (sb.ToString(), param);
}

private static string BuildSelectColumns(SchemaDescriptor schema, IReadOnlyList<string>? fields)
{
    if (fields is null || fields.Count == 0)
    {
        var all = schema.ScalarColumns
            .Select(c => $"`{c.Name}`")
            .Prepend($"`{schema.KeyColumn.Name}`");
        return string.Join(", ", all);
    }

    var resolved = new List<string> { schema.KeyColumn.Name };
    foreach (var f in fields)
    {
        var col = ResolveColumn(schema, f);
        if (col is not null && !col.Equals(schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
            resolved.Add(col);
    }
    return string.Join(", ", resolved.Select(c => $"`{c}`"));
}
```

- [ ] **Step 5: Run tests**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs \
        Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/SchemaFixtures.cs
git commit -m "feat: BuildSearch emits explicit SELECT columns; add fields projection parameter"
```

---

### Task 5: `SearchRequest.fields` proto field + `ObjectSearchGrpcService`

**Interfaces:**
- Consumes: `BuildSearch(..., fields)` from Task 4
- Produces: `SearchRequest.fields` proto field (field 6); `ObjectSearchGrpcService.Search` passes it to `BuildSearch`

- [ ] **Step 1: Write failing test**

Add to `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`:

```csharp
[Fact]
public async Task Search_WithFieldsProjection_PassesFieldsToQueryBuilder()
{
    await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());

    string? capturedSql = null;
    _sr.QueryAsync<dynamic>(Arg.Do<string>(sql => capturedSql = sql), Arg.Any<object?>())
       .Returns(Enumerable.Empty<dynamic>());

    var req = new SearchRequest
    {
        TypeName = "Article",
        PageSize = 10,
    };
    req.Fields.Add("Category");
    req.Fields.Add("PublishedAt");

    var (writer, _) = MakeStream<SearchResponse>();
    await _sut.Search(req, writer, TestServerCallContext.Create());

    capturedSql.Should().NotBeNull();
    capturedSql!.Should().Contain("`Category`");
    capturedSql!.Should().Contain("`PublishedAt`");
    capturedSql!.Should().NotContain("`Body`");
    capturedSql!.Should().Contain("`Id`");
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ --filter "Search_WithFieldsProjection" -v n
```

Expected: compile error — `SearchRequest.Fields` not defined.

- [ ] **Step 3: Add `fields` to `SearchRequest` proto**

In `Iverson.Client/Iverson.Client.Contracts/Protos/object_search.proto`, add field 6 to `message SearchRequest`:

```protobuf
message SearchRequest {
    string      type_name = 1;
    SearchQuery query     = 2;
    int32       page      = 3;
    int32       page_size = 4;
    string      trace_id  = 5;
    repeated string fields = 6;  // property names to include; empty = all fields
}
```

- [ ] **Step 4: Update `ObjectSearchGrpcService.Search`**

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, update the `BuildSearch` call to pass `request.Fields`:

```csharp
var (sql, param) = StarRocksQueryBuilder.BuildSearch(
    schema.TableName, schema, request.Query, request.Page, request.PageSize,
    fields: request.Fields.Count > 0 ? request.Fields : null);
```

- [ ] **Step 5: Run tests**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Client/Iverson.Client.Contracts/Protos/object_search.proto \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat: add fields projection to SearchRequest proto; wire through ObjectSearchGrpcService"
```

---

### Task 6: Annotate `BenchmarkArticle`

**Interfaces:**
- Consumes: `[IversonSearchKey]`, `[IversonLargeField]` from Task 1

- [ ] **Step 1: Annotate `BenchmarkArticle`**

Replace `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs` with:

```csharp
using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkArticle
{
    [IversonKey]          public Guid            Id              { get; set; }
    public string                                Title           { get; set; } = "";
    [IversonLargeField]   public string          Body            { get; set; } = "";
    public Guid                                  BenchmarkUserId { get; set; }
    [IversonSearchKey(0)] public string          Category        { get; set; } = "";
    public int                                   WordCount       { get; set; }
    [IversonSearchKey(1)] public DateTimeOffset  PublishedAt     { get; set; }

    [ManyToOne(typeof(BenchmarkUser))]
    public BenchmarkUser? Author { get; set; }
}
```

- [ ] **Step 2: Run full test suite**

```bash
# from Iverson.Server/
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass (entity annotations have no server-side test coverage; correctness verified at registration time).

- [ ] **Step 3: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Entities/BenchmarkArticle.cs
git commit -m "feat: annotate BenchmarkArticle with [IversonSearchKey] and [IversonLargeField]"
```

---

### Task 7: LoadTest `reset-starrocks` command

**Interfaces:**
- Consumes: `LoadTestConfig.StarRocksCs` (already present in `Program.cs`)

- [ ] **Step 1: Add `reset-starrocks` to `Program.cs`**

In `Iverson.Server/Iverson.LoadTest/Program.cs`:

1. Add `"reset-starrocks"` to the switch, before the `default` case:

```csharp
case "reset-starrocks":
    await ResetStarRocksAsync(config.StarRocksCs);
    break;
```

2. Add the static helper function near the bottom of the file, after the `Env` helper:

```csharp
static async Task ResetStarRocksAsync(string connectionString)
{
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    var tables = (await conn.QueryAsync<string>("SHOW TABLES")).ToList();

    if (tables.Count == 0)
    {
        Console.WriteLine("No tables found — nothing to drop.");
        return;
    }

    foreach (var table in tables)
    {
        await conn.ExecuteAsync($"DROP TABLE IF EXISTS `{table}`");
        Console.WriteLine($"Dropped: {table}");
    }

    Console.WriteLine($"\nDropped {tables.Count} table(s).");
}
```

3. Add `reset-starrocks` to the usage text inside the `default` case:

```
  reset-starrocks  Drop all StarRocks tables (and their MVs) for greenfield re-registration
```

4. Add `using MySqlConnector;` and `using Dapper;` at the top of `Program.cs` if not already present.

- [ ] **Step 2: Verify it builds**

```bash
# from Iverson.Server/Iverson.LoadTest/
dotnet build -v q
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "feat: add reset-starrocks command to LoadTest — drops all StarRocks tables and MVs"
```

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
|------------------|------|
| UNIQUE KEY table model | Task 3 |
| Sync MV created at registration | Task 3 |
| `[IversonSearchKey(order)]` attribute | Task 1 |
| `[IversonLargeField]` attribute | Task 1 |
| `[IversonEmbedding]`/`[IversonChunk]` auto-exclude from MV | Task 2 (Step 5) |
| MV sort key from `SearchKeyColumns` | Tasks 2 + 3 |
| `BuildSearch` explicit column list | Task 4 |
| `SearchRequest.fields` include-list | Task 5 |
| `reset-starrocks` LoadTest command | Task 7 |
| BenchmarkArticle annotated | Task 6 |

**No gaps identified.**
