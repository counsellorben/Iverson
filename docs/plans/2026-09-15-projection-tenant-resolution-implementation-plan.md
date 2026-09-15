# Projection Tenant Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-15-projection-tenant-resolution-design.md` (commit SHA: `d8f1bbdcd1c81bab37122a4a203152dd67621ca4`)

**Goal:** Replace the five entity-event consumers' copies of the projection tenant/owner rule with one static helper that drops on a gone authoritative row and dead-letters a tenantless or malformed row/snapshot, and reject the Qdrant no-tenant sentinel as a tenant id.

**Architecture:** A new `internal static class ProjectionTenantResolution` in `Iverson.Api/Consumers/` takes `IEntityRepository` as a parameter. It returns an `AuthoritativeRow(TenantId, Row)` or `null` for Created/Updated, a tenant string for Deleted, and throws `PoisonMessageException` for case (ii). Each consumer calls it in place of its private copy, with no DI or constructor change. Separately, `TenantLifecycleGrpcService.CreateTenant` rejects `IntelligenceTenantScope.NoTenantSentinel`.

**Tech stack:** .NET 10, System.Text.Json, xUnit 2.9.3, NSubstitute 5.3.0 (+ `NSubstitute.ExceptionExtensions`), FluentAssertions 8.11.0.

All paths below are relative to `Iverson.Server/`, and every `dotnet` command runs from `Iverson.Server/`.

---

## File Structure

**Create**
- `Iverson.Api/Consumers/ProjectionTenantResolution.cs`: the helper (`FetchAuthoritativeRowAsync`, `TenantFromSnapshot`, `ReadString`) and the `AuthoritativeRow` record.
- `Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs`: unit tests for the helper's contract.

**Modify**
- `Iverson.Api/Consumers/DocumentRerenderConsumer.cs`: `ResolveTenantIdAsync` and the caller comment.
- `Iverson.Api/Consumers/PopularitySignalConsumer.cs`: `ResolveTenantIdAsync` and the duplication comment.
- `Iverson.Api/Consumers/EngagementStoreConsumer.cs`: upsert fetch/owner read, delete snapshot, delete the private fetch helper.
- `Iverson.Api/Consumers/EnrichmentConsumer.cs`: `HandleAsync` fetch and outbox payload, `HandleDeleteAsync` snapshot.
- `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`: single fetch plus drop, dead null-tenant branches, delete snapshot, delete the private fetch helper.
- `Iverson.Vector/IntelligenceTenantScope.cs`: `NoTenantSentinel` becomes `public`.
- `Iverson.Api/Grpc/TenantLifecycleGrpcService.cs`: reject the sentinel id.

**Test (modify)**
- `Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs`
- `Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs`
- `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`
- `Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`
- `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`
- `Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs`

## Inherited from spec

`thorough-brainstorming` verified these assumptions when the spec was written, and `update-design-doc` corrected them after CDR round 1. They are **not** re-verified here and are trusted as ground truth. Evidence for each is in the spec's "Verified assumptions" table under the same number.

1. `IEntityRepository.FetchByKeyAsync(TableSchema, string, EntityAccess)` returns `Task<string?>` with no `CancellationToken`; `EntityAccess.CrossTenantMaintenance` exists.
2. `SchemaDescriptor.TenantColumn` is non-null for every registered schema.
3. `PoisonMessageException(string)` and `(string, Exception)` exist and are public.
4. All five copies build the table via `SchemaBuilder.ToTableSchema(schema)`.
5. Row JSON and delete snapshots carry the tenant under the exact `TenantColumn` name.
6. Each consumer's current tenant code and failure behaviour are as described in spec §2 and its Motivation table.
7. The listed Intelligence branches are the only code depending on a null tenant.
8. Every consumer's row-gone return precedes all side effects.
9. No catch-all swallows the new `PoisonMessageException`.
10. All five consumers run handlers through `MessageDispatcher`, where Poison dead-letters immediately and other exceptions retry.
11. No reflection binds the deleted private methods.
12. Exactly the seven tests in spec §4 assert changed behaviour, plus one test (`:740`) whose fixture must change.
13. Consumer fixtures do not rely on NSubstitute's default `null` row for happy paths; the one reserved-name schema without its own stub is `IntelligenceStoreConsumerTests.cs:740`.
14. `ObjectSearchVectorIntegrationTests` does not use the null-tenant path.
15. `Iverson.Api.Tests` can see `internal` types in `Iverson.Api`.
16. The quick-loop filter's matched set (the bare `~Consumers` filter includes the Kafka container test).
17. Test stack: xunit 2.9.3, NSubstitute 5.3.0, FluentAssertions 8.11.0.
18. The Qdrant spec's lazy-create rule stays satisfied after removing the null gates.
19. `EnrichmentConsumer` reads `row` only in `BuildSourceText` (`:145`) and `rowJson` only in the outbox enqueue (`:223`).
20. Nothing outside the consumers depends on their private resolvers.
21. The Kafka ordering container test does not depend on a tenantless row.
22. A write to the missing sentinel collection fails in real Qdrant (probe).
23. Case (i) is always followed by the entity's Deleted event.
24. `JsonElement.ToString()` on JSON null yields `""` (probe).
25. A tenant id enters only via `CreateTenant`, the hard-coded seeds, or an admitted claim.
26. `TenantIdentifier.IsValid` accepts `__no-tenant-claim__`.
27. Only a byte-identical id collides with the sentinel collection name.
28. `Iverson.Api` references `Iverson.Vector`, and `TenantLifecycleGrpcServiceTests` has an invalid-id test to mirror.
29. No ownership-filtered read can carry an empty owner value.
30. The log prefixes used for `consumer`: `"[Intelligence]"`, `"[Engagement]"`, `"[Enrichment]"`, `"[PopularitySignal]"`, `"[DocumentRerender]"`.
31. `row.Row.GetRawText()` supplies the `string payload` that `EnqueueUpdateOutboxRowAsync` requires, and an `Updated` outbox row's stored payload is never read on replay.
32. Every current write path stamps a non-null tenant, and the column is `NOT NULL`.
33. Every producer of a `Deleted` entity event ships the full pre-delete row as `PayloadJson`.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Api/Consumers/ProjectionTenantResolution.cs` and `Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs` are new, and no type named `ProjectionTenantResolution` or `AuthoritativeRow` exists | `ls` on both paths → "No such file or directory". `grep -rn 'ProjectionTenantResolution\|AuthoritativeRow\b'` → only the unrelated test name `Dispatch_CategoryDeleted_FindsDependentsViaPayloadSnapshotNotAuthoritativeRow` (`DocumentRerenderConsumerTests.cs:388`) |
| 2 | Signature | The helper's dependencies resolve with its usings: `SchemaBuilder` is in `Iverson.Api.Schema` with `internal static TableSchema ToTableSchema(SchemaDescriptor d)` (same assembly); `IEntityRepository`, `EntityAccess`, `TableSchema` are in `Iverson.Sql`; `PoisonMessageException` is in `Iverson.Events`; `SchemaDescriptor.TypeName` exists | `Iverson.Api/Schema/SchemaBuilder.cs:15` (namespace), `:260`; `Iverson.Sql/IRecordStoreRoles.cs:1` (namespace), `:165` (`IEntityRepository`), `:244` (`TableSchema`); `Iverson.Events/PoisonMessageException.cs:1`; `Iverson.Api/Schema/SchemaDescriptor.cs:30` |
| 3 | Code validity | `Iverson.Api` and `Iverson.Api.Tests` have `ImplicitUsings` and `Nullable` enabled; no `TreatWarningsAsErrors` anywhere, so the now-unused `ct` parameter kept on `ResolveTenantIdAsync` is not an error | `Iverson.Api/Iverson.Api.csproj:5-6`; `Iverson.Api.Tests/Iverson.Api.Tests.csproj:5-6`; `grep TreatWarningsAsErrors\|WarningsAsErrors` over both csprojs → none; no `Directory.Build.props` at `Iverson.Server/` or the repo root |
| 4 | Code validity | `ReadString(row, ownerField)` never receives `""` (which would index `""[0]` in the camelCase fallback) | `SchemaDescriptor.cs:160`: `AuthorizationRules.OwnerField` initializer is `string.IsNullOrEmpty(OwnerField) ? null : OwnerField`, so proto3's `""` becomes `null` and the `ownerField is not null` gates skip it |
| 5 | Code validity | `async Task<string?> M(...) => deleted ? Sync() : (await FetchAsync())?.TenantId;` compiles and yields the snapshot value, the row tenant, or `null` | .NET 10 single-file probe of exactly that shape printed `snap t <null>` |
| 6 | Consumer impact | No existing test asserts the text of a `PoisonMessageException`, so the new helper messages break nothing | `grep -rn WithMessage Iverson.Api.Tests/Consumers/` → only `InvalidOperationException` (`EngagementStoreConsumerTests.cs:110`, `:188`; `EnrichmentConsumerTests.cs:365`) and a generic `Exception` (`IntelligenceStoreConsumerTests.cs:647`), none on Poison |
| 7 | Command | `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~Consumers&FullyQualifiedName!~KafkaOrdering"` is valid and excludes the container test | `--no-build --list-tests` → 165 cases, 0 containing `KafkaOrdering` |
| 8 | Command | Each per-class filter matches only its own class | `--list-tests`: `~DocumentRerenderConsumerTests` → 18 in 1 class; `~PopularitySignalConsumerTests` → 17 in 1; `~EngagementStoreConsumerTests` → 18 in 1 (not the KafkaOrdering class); `~EnrichmentConsumerTests` → 22 in 1; `~IntelligenceStoreConsumerTests` → 66 in 1; `~TenantLifecycleGrpcServiceTests` → 10 in 1; `~ProjectionTenantResolutionTests` → 0 today |
| 9 | Ordering | Tasks 2–5 consume only Task 1's symbols; Task 6 consumes nothing from Tasks 1–5; no two tasks edit the same file | File lists per task below are disjoint by construction; Task 6 touches only `IntelligenceTenantScope.cs`, `TenantLifecycleGrpcService.cs` and its test, none of which references the helper |
| 10 | Consumer impact | `IntelligenceTenantScope.NoTenantSentinel` has no other reference, so making it `public` affects nothing else | `grep -rn NoTenantSentinel --include=*.cs` → only `Iverson.Vector/IntelligenceTenantScope.cs:10` (declaration) and `:43` (use); no test binds it by reflection |
| 11 | Consumer impact | `EnrichmentConsumer.ExtractString` remains used after Task 4, so it stays | `grep -n ExtractString EnrichmentConsumer.cs` → `135`, `295` (both replaced) and `376` (inside `BuildSourceText`, kept) |
| 12 | Consumer impact | `DocumentRerenderConsumer.ExtractString` and `PopularitySignalConsumer.ExtractString` remain used after Task 2 | Rerender `:160`, `:174`, `:184`, `:194`; Popularity `:230`, `:236` |
| 13 | Signature | `DocumentRenderer.RenderAsync(SchemaDescriptor, JsonElement, string tenantId, CancellationToken)` and `EnrichmentConsumer.BuildSourceText(SchemaDescriptor, JsonElement)` | `Iverson.Api/Consumers/DocumentRenderer.cs:25-26`; `EnrichmentConsumer.cs:370` |
| 14 | Signature | Test fixtures used by the plan exist with the stated shapes | `Helpers/SchemaFixtures.cs:39` `AuthorSchema()` (`TenantColumn "TenantId"` `:51`), `:55` `ArticleSchema()` (`CollectionName "articles"`, `TenantColumn "TenantId"`); `EnrichmentConsumerTests.cs:25` `_state`, `:28` `_txRunner`, `:38` `Key`, `:83` `EnrichedArticle`, `:127` `RowJson(body, tenant)`, `:139-141` `Event(type, payload)`, `:143` `BuildSut()`; `DocumentRerenderConsumerTests.cs:18` `_queue`, `:44` `BuildSut()`, `:52` `WidgetSchema`, `:109` `CommentSchema`, `:135` `MakeEvent`; `PopularitySignalConsumerTests.cs:30` `_search`, `:54` `OptionsWith`, `:59` `BuildSut(options)`, `:73` `ArticleSchema`, `:90` `CommentSchema`, `:108` `MakeEvent`; `IEngagementStoreSearchService.AggregateAsync` has 7 parameters (`Iverson.StarRocks/IEngagementStoreRoles.cs:36-43`); `Helpers/TestServerCallContext.cs:26` `Create(CancellationToken ct = default, ClaimsPrincipal? user = null)` |
| 15 | Code validity | The repo's convention for a throwing substitute on a `Task`-returning method is `.Throws(...)` from `NSubstitute.ExceptionExtensions` (no `ThrowsAsync` is used anywhere) | `EnrichmentConsumerTests.cs:15` `using NSubstitute.ExceptionExtensions;`, `:495-496` `_txRunner.ExecuteInTransactionAsync(...).Throws(new InvalidOperationException("connection reset"))`; `grep 'ThrowsAsync('` over `Iverson.Api.Tests` → none |
| 16 | Code validity | Every consumer test file touched already imports `Iverson.Events` (for `PoisonMessageException`) and `FluentAssertions` | `EngagementStoreConsumerTests.cs` usings include `Iverson.Events`; `EnrichmentConsumerTests.cs`, `DocumentRerenderConsumerTests.cs`, `PopularitySignalConsumerTests.cs` and `IntelligenceStoreConsumerTests.cs` usings include `Iverson.Events` and `FluentAssertions` |
| 17 | Code validity | Adding `using Iverson.Vector;` to `TenantLifecycleGrpcService.cs` and its test introduces no ambiguous type name, and the test project references `Iverson.Vector` | `Iverson.Vector` public types: `CollectionSchema DiversifyCandidate FilterTranslationException IResultDiversifier IResultReranker IVectorQueryService IVectorSchemaManager IVectorWriteService IntelligenceCollectionManager IntelligenceFilterBuilder IntelligenceTenantScope IntelligenceVectorService NamedVector PayloadIndex PayloadIndexKind RerankCandidate RerankedResult ResultDiversifier ResultReranker ServiceCollectionExtensions VectorRankingOptions VectorSearchResult`; names used in the two files: `AuditLog CreateUserResult Empty ListTenantsResponse Status StatusCode Tenant TenantRow`, so there is no overlap; `Iverson.Api.Tests.csproj:37` references `Iverson.Vector` |
| 18 | File path | The Rerender "THREE producers" comment block is `DocumentRerenderConsumer.cs:57-66`, directly above `var tenantId = await ResolveTenantIdAsync(...)` (`:67`) | Read of `:50-68` |
| 19 | Command | The full-suite completion gate needs Docker, because `Iverson.Api.Tests` uses Testcontainers | `grep -l Testcontainers Iverson.Api.Tests/Iverson.Api.Tests.csproj` → matches |
| 21 | Code validity | The new `row` local introduces no name collision in `IntelligenceStoreConsumer.HandleAsync` or `EngagementStoreConsumer.HandleUpsertAsync` (Enrichment already names its local `row`), and every replaced or deleted range's boundary lines are as cited | `awk` for `\<row\>` over `IntelligenceStoreConsumer.cs:73-401` and `EngagementStoreConsumer.cs:43-95` → comments only. Boundary reads: `EnrichmentConsumer.cs:295-302` (tenant `if` closes at `:302`); `PopularitySignalConsumerTests.cs:474`, `DocumentRerenderConsumerTests.cs:541`, `IntelligenceStoreConsumerTests.cs:2375` and `TenantLifecycleGrpcServiceTests.cs:97` close their tests; `IntelligenceStoreConsumerTests.cs:763` is `RegisterAsync(twoVectorSchema)` |
| 20 | Convention | Commit messages are lowercase imperative with no conventional-commit prefix; `docs/plans/` is gitignored (`git add -f` is needed for the plan only, not for code) | `git log --format=%s -8` (e.g. `resolve Value ambiguity from FluentAssertions 8.11.0 in .NET test projects`); `git check-ignore -v docs/plans/x.md` → `.gitignore:49:**/docs/plans/` |

## Tasks

### Task 1: The `ProjectionTenantResolution` helper

**Files:**
- Create: `Iverson.Api/Consumers/ProjectionTenantResolution.cs`
- Test: `Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs`

**Interfaces:**
- Produces: `ProjectionTenantResolution.FetchAuthoritativeRowAsync(IEntityRepository, SchemaDescriptor, string key, string consumer) → Task<AuthoritativeRow?>`; `ProjectionTenantResolution.TenantFromSnapshot(string payloadJson, SchemaDescriptor, string key, string consumer) → string`; `ProjectionTenantResolution.ReadString(JsonElement, string) → string?`; `record AuthoritativeRow(string TenantId, JsonElement Row)` with `ReadString(string) → string?`. Tasks 2–5 consume all of these.

- [ ] **Step 1: Write the failing tests**

Create `Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class ProjectionTenantResolutionTests
{
    private const string Key = "11111111-1111-1111-1111-111111111111";

    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();

    // TenantColumn = "TenantId" (SchemaFixtures.cs:55-74).
    private readonly SchemaDescriptor _schema = SchemaFixtures.ArticleSchema();

    private void RowIs(string? rowJson) =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns(rowJson);

    private Task<AuthoritativeRow?> Fetch() =>
        ProjectionTenantResolution.FetchAuthoritativeRowAsync(_entities, _schema, Key, "[Test]");

    private static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── FetchAuthoritativeRowAsync ───────────────────────────────────────────

    [Fact]
    public async Task FetchAuthoritativeRow_RowGone_ReturnsNull()
    {
        RowIs(null);

        (await Fetch()).Should().BeNull();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_RowWithTenant_ReturnsTenantAndARowReadableAfterTheCall()
    {
        RowIs("""{"Title":"T","TenantId":"tenant-a"}""");

        var row = await Fetch();

        row.Should().NotBeNull();
        row!.TenantId.Should().Be("tenant-a");
        row.ReadString("Title").Should().Be("T");
    }

    [Fact]
    public async Task FetchAuthoritativeRow_TenantJsonNull_ThrowsPoison()
    {
        RowIs("""{"Title":"T","TenantId":null}""");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_TenantKeyAbsent_ThrowsPoison()
    {
        RowIs("""{"Title":"T"}""");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_CamelCaseOnlyTenantKey_Resolves()
    {
        RowIs("""{"tenantId":"tenant-a"}""");

        (await Fetch())!.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task FetchAuthoritativeRow_MalformedRowJson_ThrowsPoison()
    {
        RowIs("NOT_VALID_JSON{{{");

        await ((Func<Task>)Fetch).Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_RepositoryThrows_PropagatesUnwrapped()
    {
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Throws(new InvalidOperationException("connection reset"));

        await ((Func<Task>)Fetch).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FetchAuthoritativeRow_ReadsCrossTenant()
    {
        RowIs("""{"TenantId":"tenant-a"}""");

        await Fetch();

        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), Key, EntityAccess.CrossTenantMaintenance);
    }

    // ── TenantFromSnapshot ───────────────────────────────────────────────────

    [Fact]
    public void TenantFromSnapshot_TenantPresent_ReturnsIt()
    {
        ProjectionTenantResolution.TenantFromSnapshot("""{"TenantId":"tenant-a"}""", _schema, Key, "[Test]")
            .Should().Be("tenant-a");
    }

    [Fact]
    public void TenantFromSnapshot_TenantKeyAbsent_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("""{"Title":"T"}""", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void TenantFromSnapshot_TenantJsonNull_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("""{"TenantId":null}""", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void TenantFromSnapshot_MalformedJson_ThrowsPoison()
    {
        var act = () => ProjectionTenantResolution.TenantFromSnapshot("NOT_VALID_JSON{{{", _schema, Key, "[Test]");

        act.Should().Throw<PoisonMessageException>();
    }

    // ── ReadString ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadString_ExactKeyWinsOverCamelCase()
    {
        ProjectionTenantResolution.ReadString(Element("""{"TenantId":"exact","tenantId":"camel"}"""), "TenantId")
            .Should().Be("exact");
    }

    [Fact]
    public void ReadString_JsonNull_ReturnsNull()
    {
        ProjectionTenantResolution.ReadString(Element("""{"TenantId":null}"""), "TenantId")
            .Should().BeNull();
    }

    [Fact]
    public void ReadString_Number_ReturnsRawText()
    {
        ProjectionTenantResolution.ReadString(Element("""{"Count":42}"""), "Count")
            .Should().Be("42");
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ProjectionTenantResolutionTests"`
Expected: the build fails with `CS0103`/`CS0246` for `ProjectionTenantResolution` and `AuthoritativeRow`.

- [ ] **Step 3: Implement the helper**

Create `Iverson.Api/Consumers/ProjectionTenantResolution.cs`:

```csharp
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Consumers;

/// <summary>
/// The one tenant-resolution rule every entity-event projection consumer applies — the
/// projection side of CSR #7. Created/Updated re-derive from the authoritative Postgres row,
/// because the event payload is unsigned; Deleted reads the pre-delete snapshot, because the row
/// is already gone. A gone row returns <c>null</c> (the caller drops: the entity's Deleted event
/// follows). A row or snapshot that is present but carries no tenant value, or does not parse,
/// is an invariant violation — every write path stamps the tenant — and dead-letters.
/// </summary>
internal static class ProjectionTenantResolution
{
    internal static async Task<AuthoritativeRow?> FetchAuthoritativeRowAsync(
        IEntityRepository entities, SchemaDescriptor schema, string key, string consumer)
    {
        // Cross-tenant by necessity: this read is what derives the tenant, so there is no tenant
        // to scope it to yet, and the unsigned event payload must not be trusted for one.
        var rowJson = await entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema), key, EntityAccess.CrossTenantMaintenance);
        if (rowJson is null) return null;

        JsonElement row;
        try
        {
            using var doc = JsonDocument.Parse(rowJson);
            row = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException(
                $"{consumer} Malformed authoritative row JSON type={schema.TypeName} key={key}", ex);
        }

        var tenant = ReadString(row, schema.TenantColumn)
            ?? throw new PoisonMessageException(
                $"{consumer} No tenant value on authoritative row type={schema.TypeName} key={key}");

        return new AuthoritativeRow(tenant, row);
    }

    internal static string TenantFromSnapshot(
        string payloadJson, SchemaDescriptor schema, string key, string consumer)
    {
        JsonElement snapshot;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            snapshot = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException(
                $"{consumer} Malformed delete snapshot JSON type={schema.TypeName} key={key}", ex);
        }

        return ReadString(snapshot, schema.TenantColumn)
            ?? throw new PoisonMessageException(
                $"{consumer} No tenant value in delete snapshot type={schema.TypeName} key={key}");
    }

    // Exact name, then camelCase fallback. JSON null is absent (null), never "" — the
    // JsonElement.ToString() of a Null is "", which is how two of the replaced copies let a
    // null-tenant snapshot through.
    internal static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString()
                 : v.ValueKind == JsonValueKind.Null   ? null
                 : v.ToString();

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(camel, out var vc))
            return vc.ValueKind == JsonValueKind.String ? vc.GetString()
                 : vc.ValueKind == JsonValueKind.Null   ? null
                 : vc.ToString();

        return null;
    }
}

internal sealed record AuthoritativeRow(string TenantId, JsonElement Row)
{
    internal string? ReadString(string propertyName) => ProjectionTenantResolution.ReadString(Row, propertyName);
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ProjectionTenantResolutionTests"`
Expected: `Passed: 15, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/ProjectionTenantResolution.cs Iverson.Server/Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs
git commit -m "add ProjectionTenantResolution: one tenant-resolution rule for the entity-event consumers

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 2: `DocumentRerenderConsumer` and `PopularitySignalConsumer`

**Files:**
- Modify: `Iverson.Api/Consumers/DocumentRerenderConsumer.cs:57-66`, `:131-150`
- Modify: `Iverson.Api/Consumers/PopularitySignalConsumer.cs:250-282`
- Test: `Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs:525-541`
- Test: `Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs:457-474`

**Interfaces:**
- Consumes: `ProjectionTenantResolution.FetchAuthoritativeRowAsync`, `ProjectionTenantResolution.TenantFromSnapshot` (Task 1).

- [ ] **Step 1: Rewrite the two no-tenant tests to expect Poison**

In `DocumentRerenderConsumerTests.cs`, replace the whole `Dispatch_AuthoritativeRowHasNoTenantValue_EnqueuesNothing` test (`:525-541`) with:

```csharp
    [Fact]
    public async Task Dispatch_AuthoritativeRowHasNoTenantValue_ThrowsPoisonAndEnqueuesNothing()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema());

        // Row present, tenant column absent from it — an invariant violation, so it dead-letters.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}"}""");

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId,
            $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}"}""");

        var act = () => BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _queue.DidNotReceiveWithAnyArgs().EnqueueEntityAsync(default, default!, default!);
    }
```

In `PopularitySignalConsumerTests.cs`, replace the whole `Dispatch_AuthoritativeRowHasNoTenantValue_SkipsWithoutCallingAggregate` test (`:457-474`) with:

```csharp
    [Fact]
    public async Task Dispatch_AuthoritativeRowHasNoTenantValue_ThrowsPoisonWithoutCallingAggregate()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // Row present, tenant column absent from it — an invariant violation, so it dead-letters.
        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
    }
```

Before replacing, confirm each test's closing brace is at the cited end line (`:541` and `:474`). If a line has drifted, replace the whole method by name.

- [ ] **Step 2: Run the tests and confirm the two rewritten tests fail**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~DocumentRerenderConsumerTests|FullyQualifiedName~PopularitySignalConsumerTests"`
Expected: exactly 2 failures, the two `…_ThrowsPoison…` tests ("Expected a PoisonMessageException to be thrown, but no exception was thrown"). All other tests pass.

- [ ] **Step 3: Wire `DocumentRerenderConsumer`**

Replace the comment block at `:57-66`, from `// THREE producers can put a null here; …` through `// judged unlikely rather than proven impossible. Recorded, not tested.`, with:

```csharp
        //
        // A null here means only one thing: the authoritative Postgres row is already gone by
        // consumption time (ProjectionTenantResolution.FetchAuthoritativeRowAsync returned null),
        // and the entity's Deleted event follows. A row or delete snapshot that is present but
        // carries no tenant value throws PoisonMessageException inside the helper instead.
```

Replace the whole `ResolveTenantIdAsync` method (`:131-150`) with:

```csharp
    // Created/Updated re-derive from the authoritative Postgres row (the event payload is
    // unsigned and must not decide which rows a tenant-scoped lookup returns); Deleted reads
    // the pre-delete snapshot. See ProjectionTenantResolution for the drop-vs-dead-letter rule.
    private async Task<string?> ResolveTenantIdAsync(EntityEvent ev, SchemaDescriptor changedSchema, CancellationToken ct) =>
        ev.EventType == EntityEventType.Deleted
            ? ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, changedSchema, ev.Key, "[DocumentRerender]")
            : (await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, changedSchema, ev.Key, "[DocumentRerender]"))?.TenantId;
```

- [ ] **Step 4: Wire `PopularitySignalConsumer`**

Replace `:250-282`, the comment starting `// Identical shape to DocumentRerenderConsumer's private helper of the same name` together with the whole `ResolveTenantIdAsync` method, with:

```csharp
    // Created/Updated re-derive from the authoritative Postgres row (the event payload is
    // unsigned and must not decide which rows a tenant-scoped lookup returns); Deleted reads
    // the pre-delete snapshot. See ProjectionTenantResolution for the drop-vs-dead-letter rule.
    private async Task<string?> ResolveTenantIdAsync(EntityEvent ev, SchemaDescriptor changedSchema, CancellationToken ct) =>
        ev.EventType == EntityEventType.Deleted
            ? ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, changedSchema, ev.Key, "[PopularitySignal]")
            : (await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, changedSchema, ev.Key, "[PopularitySignal]"))?.TenantId;
```

Leave the `if (tenantId is null) return;` comment at `:198-199` ("mirrors DocumentRerenderConsumer.cs:68 …") unchanged. It stays accurate.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~DocumentRerenderConsumerTests|FullyQualifiedName~PopularitySignalConsumerTests"`
Expected: `Failed: 0` (35 tests). `Dispatch_DeletedEventWithMalformedPayloadJson_ThrowsPoisonMessageException` still passes, now via `TenantFromSnapshot`.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
git commit -m "route DocumentRerender and PopularitySignal tenant resolution through ProjectionTenantResolution

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 3: `EngagementStoreConsumer`

**Files:**
- Modify: `Iverson.Api/Consumers/EngagementStoreConsumer.cs:58-64`, `:77-91`, `:109-127`, `:135-160`
- Test: `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs:419-447` (rewrite) plus one new test

**Interfaces:**
- Consumes: `ProjectionTenantResolution.FetchAuthoritativeRowAsync`, `ProjectionTenantResolution.TenantFromSnapshot`, `AuthoritativeRow.ReadString` (Task 1).

- [ ] **Step 1: Rewrite `:419-447` and add the delete test**

Replace the whole `HandleUpsert_WithNoAuthoritativeTenantValue_SkipsProvisioningAndUpsert` test (`:419-447`) with the two tests below:

```csharp
    [Fact]
    public async Task HandleUpsert_WithNoAuthoritativeTenantValue_ThrowsPoisonWithoutProvisioningOrUpsert()
    {
        // A present authoritative row with no tenant value is an invariant violation (every write
        // path stamps the tenant), so it dead-letters rather than dropping silently — and it must
        // never provision or write to any tenant database on the way.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns("""{"Name":"Alice"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-no-tenant",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        var act = () => BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _sr.DidNotReceive().EnsureTenantProvisionedAsync(Arg.Any<string>(), Arg.Any<EngagementTableSchema>());
        await _sr.DidNotReceive().UpsertAsync(
            Arg.Any<EngagementTableSchema>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task HandleDelete_WithJsonNullTenantInSnapshot_ThrowsPoisonAndDoesNotDelete()
    {
        // JsonElement.ToString() of a JSON null is "", which the old inline read passed through as
        // tenant "" and deleted under. A null tenant in the snapshot is an invariant violation.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"TenantId":null}""",
            TraceId:       "trace-null-tenant-delete",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        var act = () => BuildSut().HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _sr.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default!, default!);
    }
```

- [ ] **Step 2: Run the tests and confirm both fail**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~EngagementStoreConsumerTests"`
Expected: exactly 2 failures, the two tests above. Today the upsert drops without throwing, and the delete calls `DeleteAsync(..., "")`.

- [ ] **Step 3: Wire the upsert**

Replace `:58-64`:

```csharp
        var authoritativeTenantValue =
            await FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct);
        if (authoritativeTenantValue is null)
        {
            logger.LogWarning("[Engagement] Dropped upsert — no authoritative tenant value for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
            return;
        }
```

with:

```csharp
        var row = await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, schema, ev.Key, "[Engagement]");
        if (row is null)
        {
            logger.LogWarning("[Engagement] Dropped upsert — no authoritative tenant value for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
            return;
        }

        var authoritativeTenantValue = row.TenantId;
```

In the owner block (`:77-91`), replace only the line

```csharp
            var authoritativeOwnerValue = await FetchAuthoritativeOwnerValueAsync(schema, ownerField, ev.Key, ct);
```

with:

```csharp
            var authoritativeOwnerValue = row.ReadString(ownerField);
```

- [ ] **Step 4: Wire the delete**

Replace `:109-127`, from `JsonElement payload;` through the closing `}` of the `if (tenantValue is null)` block, with:

```csharp
        var tenantValue = ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, schema, ev.Key, "[Engagement]");
```

- [ ] **Step 5: Delete the private fetch helper**

Under `// ── Helpers ──…` (`:133`), delete the comment `:135-139` and the whole `FetchAuthoritativeOwnerValueAsync` method `:140-160`. `WithOwnerValue` and `Deserialize` stay.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~EngagementStoreConsumerTests"`
Expected: `Failed: 0` (19 tests).

These tests must still pass unchanged:
- `HandleUpsert_WithForgedOwnerValueInPayload_UpsertsAuthoritativeValueNotPayloadValue`;
- `HandleUpsert_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromPayload`;
- both `Received(1).FetchByKeyAsync` tests (`HandleUpsert_WithProtoDefaultEmptyOwnerField_…` and `HandleUpsert_WithNoOwnerFieldConfigured_…`).

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs
git commit -m "route EngagementStoreConsumer tenant and owner resolution through ProjectionTenantResolution

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 4: `EnrichmentConsumer`

**Files:**
- Modify: `Iverson.Api/Consumers/EnrichmentConsumer.cs:105-145`, `:223`, `:280-302`
- Test: `Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs:373-386`, `:403-413`

**Interfaces:**
- Consumes: `ProjectionTenantResolution.FetchAuthoritativeRowAsync`, `ProjectionTenantResolution.TenantFromSnapshot`, `AuthoritativeRow.TenantId`/`.Row` (Task 1).

- [ ] **Step 1: Rewrite the two no-tenant tests to expect Poison**

Replace the whole `HandleUpdated_WithNullTenantValueInRow_SkipsAndWritesNoStateRow` test (`:373-386`) with:

```csharp
    [Fact]
    public async Task HandleUpdated_WithNullTenantValueInRow_ThrowsPoisonAndWritesNoStateRow()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns(RowJson(tenant: null));

        var sut = BuildSut();
        var act = () => sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
        await _txRunner.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!);
    }
```

Replace the whole `HandleDelete_WithNoTenantInSnapshot_SkipsTheStateDelete` test (`:403-413`) with:

```csharp
    [Fact]
    public async Task HandleDelete_WithNoTenantInSnapshot_ThrowsPoisonAndSkipsTheStateDelete()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        var sut = BuildSut();
        var act = () => sut.HandleDeleteAsync(Key, Event(EntityEventType.Deleted, RowJson(tenant: null)),
            CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _state.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default!);
    }
```

- [ ] **Step 2: Run the tests and confirm both fail**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~EnrichmentConsumerTests"`
Expected: exactly 2 failures, the two rewritten tests ("no exception was thrown").

- [ ] **Step 3: Wire `HandleAsync`**

Replace `:105-145`, from the comment `// Cross-tenant by necessity: this read is what DERIVES the tenant …` through `var sourceText = BuildSourceText(schema, row);`, keeping `var tableSchema = SchemaBuilder.ToTableSchema(schema);` at `:104`, with:

```csharp
        // Fails closed with PoisonMessageException when the row carries no tenant value: with a
        // null tenant EnterTenantScopeAsync would set app.tenant_id to NULL, the targeted UPDATE
        // would match zero rows, and recording a hash would mark the object enriched forever.
        var row = await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, schema, ev.Key, "[Enrichment]");
        if (row is null)
        {
            logger.LogWarning(
                "[Enrichment] No authoritative row for type={Type} key={Key} — skipping.",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        var tenantValue = row.TenantId;

        // ── Step 2: hash source text + enrichment specification, and compare ──────
        var sourceText = BuildSourceText(schema, row.Row);
```

At `:223`, replace

```csharp
                    tx, outboxRowId, schema.TypeName, ev.Key, rowJson);
```

with:

```csharp
                    tx, outboxRowId, schema.TypeName, ev.Key, row.Row.GetRawText());
```

- [ ] **Step 4: Wire `HandleDeleteAsync`**

Replace `:280-302`, from `JsonElement payload;` through the closing `}` of the `if (tenantValue is null)` block, with:

```csharp
        var tenantValue = ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, schema, ev.Key, "[Enrichment]");
```

The comment above it (`:274-279`) and the early return at `:272-273` stay.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~EnrichmentConsumerTests"`
Expected: `Failed: 0` (22 tests).

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
git commit -m "route EnrichmentConsumer tenant resolution through ProjectionTenantResolution

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 5: `IntelligenceStoreConsumer`

**Files:**
- Modify: `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:113-127`, `:156`, `:174`, `:197-216`, `:361-365`, `:532-546`, `:571-593`
- Test: `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs:423-462` (rewrite), `:740-786` (fixture), `:2322-2375` (delete), plus one new test

**Interfaces:**
- Consumes: `ProjectionTenantResolution.FetchAuthoritativeRowAsync`, `ProjectionTenantResolution.TenantFromSnapshot`, `AuthoritativeRow.TenantId`/`.ReadString` (Task 1).

- [ ] **Step 1: Rewrite, fix the fixture, delete, and add the tests**

**(a)** Replace the whole `HandleCreated_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromChunkPayload` test (`:423-462`) with:

```csharp
    [Fact]
    public async Task HandleCreated_WithNoAuthoritativeRow_DropsWithoutAnyVectorWrite()
    {
        // Case (i): the authoritative row is gone — the entity was deleted after this write, and
        // its Deleted event follows and cleans up. The event is dropped before any embedding or
        // Qdrant call: no sentinel-collection write, no retry, no dead letter.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns((string?)null);

        var ev = new EntityEvent(
            EventType: EntityEventType.Created, TypeName: "Article", Key: Guid.NewGuid().ToString(),
            PayloadJson: """{"Title":"Test","Body":"Body text"}""", TraceId: "trace-missing-row", SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow, TargetStores: StoreTarget.Intelligence);

        var act = () => BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _ = _embedding.DidNotReceiveWithAnyArgs().EmbedDocumentAsync(default!, default);
        await _vectorSchema.DidNotReceiveWithAnyArgs().ApplyCollectionAsync(default!);
        await _vectorWrite.DidNotReceiveWithAnyArgs().UpsertNamedAsync(default!, default, default!, default);
        await _vectorWrite.DidNotReceiveWithAnyArgs().DeleteByFilterAsync(default!, default!);
    }

    [Fact]
    public async Task HandleDeleted_WithNoTenantInSnapshot_ThrowsPoisonAndDeletesNothing()
    {
        // The snapshot is the raw pre-delete row, which carries the tenant by construction; one
        // without it is an invariant violation. It used to delete against the sentinel collection.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   "{}",
            TraceId:       "trace-no-tenant-delete",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var act = () => BuildSut().HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _vectorWrite.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await _vectorWrite.DidNotReceiveWithAnyArgs().DeleteByFilterAsync(default!, default!);
    }
```

**(b)** In `HandleCreated_WithMultipleVectorFields_EmbedsAllFields` (`:740-786`), insert directly after `await _registry.RegisterAsync(twoVectorSchema);` (`:763`):

```csharp

        // This schema names the reserved tenant column, but the constructor's row stub carries the
        // tenant under "TenantId" — without this override the row would have no tenant value.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns($$"""{"{{SchemaDescriptor.TenantColumnName}}":"test-tenant"}""");
```

**(c)** Delete the whole `HandleCreated_AuthoritativeTenantValueMissing_RendersDocumentWithoutThrowingAndLogsWarning` test (`:2322-2375`, `[Fact]` through its closing brace). Its scenario, rendering a document with a null tenant, is unreachable after Step 3.

Before each edit, confirm the method boundaries by name (the line numbers above are pre-edit and shift after (a)). Apply (c) before (a) to keep the earlier line numbers valid, or locate each method by name.

- [ ] **Step 2: Run the tests and confirm the two new tests fail**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~IntelligenceStoreConsumerTests"`
Expected: exactly 2 failures.
- `HandleCreated_WithNoAuthoritativeRow_DropsWithoutAnyVectorWrite`: today the event embeds and upserts to the sentinel collection on the substitute.
- `HandleDeleted_WithNoTenantInSnapshot_ThrowsPoisonAndDeletesNothing`: today it deletes against the sentinel collection without throwing.

`HandleCreated_WithMultipleVectorFields_EmbedsAllFields` passes both before and after.

- [ ] **Step 3: Wire `HandleAsync`**

Replace `:113-127`, from `// Re-derive the ownership value from the authoritative Postgres row rather than` through `await FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct);`, with:

```csharp
        // Tenant and owner come from the authoritative Postgres row, never the unsigned event
        // payload (CSR #7): the tenant routes which physical Qdrant collection this point is
        // written to, and the owner feeds Qdrant's read-time row authorization filtering. One
        // read serves both, and the chunk and centroid blocks below reuse these values.
        var row = await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, schema, ev.Key, "[Intelligence]");
        if (row is null)
        {
            logger.LogWarning(
                "[Intelligence] Dropped event — no authoritative row for type={Type} key={Key}",
                ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            return;
        }

        var ownerField = schema.Authorization?.OwnerField;
        var authoritativeOwnerValue = ownerField is not null ? row.ReadString(ownerField) : null;
        var authoritativeTenantValue = row.TenantId;
```

Remove the null gate before the object-collection lazy-create. Replace (`:156-157`)

```csharp
                if (authoritativeTenantValue is not null)
                    await EnsureCollectionAsync(SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName });
```

with:

```csharp
                await EnsureCollectionAsync(SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName });
```

Remove the null gate before the chunks-collection lazy-create. Replace (`:174-175`)

```csharp
            if (authoritativeTenantValue is not null)
                await EnsureCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(schema) with { CollectionName = chunksCollectionName });
```

with:

```csharp
            await EnsureCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(schema) with { CollectionName = chunksCollectionName });
```

In the `if (cf.PropertyName == "Document")` branch (`:197-216`), replace the branch body, which is the null-tenant comment (`:199-205`), the `if (authoritativeTenantValue is null) { logger.LogWarning(...); }` block (`:206-212`) and the `RenderAsync(..., authoritativeTenantValue ?? string.Empty, ct)` call (`:214-215`), with:

```csharp
                        text = await documentRenderer.RenderAsync(schema, payload, authoritativeTenantValue, ct);
```

Remove the centroid gate. Replace (`:361-365`)

```csharp
        // Gated on authoritativeTenantValue is not null, unlike the chunk upserts above (:239,
        // ungated) — inherited asymmetry, not accidental: on the documented delete-then-recreate
        // race (authoritative row missing), chunks are still written but the centroid is silently
        // dropped. Plan-conformant; not changed here.
        if (centroids.Count > 0 && authoritativeTenantValue is not null)
```

with:

```csharp
        if (centroids.Count > 0)
```

- [ ] **Step 4: Wire `HandleDeleteAsync`**

Keep the three-line comment at `:532-534`. Replace `:535-546`, from `JsonElement payload;` through `var tenantValue = ExtractString(payload, schema.TenantColumn);`, with:

```csharp
        var tenantValue = ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, schema, ev.Key, "[Intelligence]");
```

- [ ] **Step 5: Delete the private fetch helper**

Delete the comment `:571-575` (`// Re-derives the ownership value from the authoritative Postgres row instead of …`) and the whole `FetchAuthoritativeOwnerValueAsync` method (`:576-593`). `FetchSummaryAsync`, `ExtractString` and the other helpers stay.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~IntelligenceStoreConsumerTests"`
Expected: `Failed: 0` (67 tests: 66 − 1 deleted + 2 added).

These tests must still pass unchanged:
- `HandleCreated_WithForgedOwnerValueInPayload_ChunkPayloadUsesAuthoritativeValueNotPayloadValue`;
- `HandleCreated_WithForgedOwnerValueInPayload_PointPayloadUsesAuthoritativeValueNotPayloadValue`;
- both `Received(1).FetchByKeyAsync` tests (`HandleCreated_WithNoOwnerFieldConfigured_StillCallsFetchByKeyAsyncForTenant`, `HandleCreated_WithProtoDefaultEmptyOwnerField_TreatsItAsNoOwnerFieldAndWritesThePoint`).

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "route IntelligenceStoreConsumer tenant and owner resolution through ProjectionTenantResolution

A gone authoritative row now drops the event instead of writing to the
no-tenant sentinel collection and dead-lettering; the dead null-tenant
gates are removed.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 6: Reject the no-tenant sentinel as a tenant id, and run the completion gate

Run this task last. It doesn't depend on Tasks 1–5, but its final step gates the whole change.

**Files:**
- Modify: `Iverson.Vector/IntelligenceTenantScope.cs:10`
- Modify: `Iverson.Api/Grpc/TenantLifecycleGrpcService.cs:1-6` (usings), `:17`
- Test: `Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs` (usings; new test after `:97`)

- [ ] **Step 1: Write the failing test**

Add `using Iverson.Vector;` to the usings of `TenantLifecycleGrpcServiceTests.cs`, in alphabetical position after `using Iverson.Sql;`. Then insert this test directly after `CreateTenant_InvalidTenantId_ThrowsInvalidArgumentAndTouchesNoDependency` (which ends at `:97`):

```csharp

    [Fact]
    public async Task CreateTenant_NoTenantSentinelId_ThrowsInvalidArgumentAndTouchesNoDependency()
    {
        // The literal passes TenantIdentifier.IsValid, but a tenant provisioned under it would own
        // the Qdrant no-tenant sentinel collection that null-tenant reads and writes rely on never
        // existing.
        var request = new CreateTenantRequest
        {
            TenantId = IntelligenceTenantScope.NoTenantSentinel,
            DisplayName = "Sentinel",
            AdminUsername = "sentinel-admin",
            AdminEmail = "admin@sentinel.example"
        };

        var act = () => _sut.CreateTenant(request, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        await _tenantRepository.DidNotReceive()
            .InsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>());
        await _authentikAdminClient.DidNotReceive()
            .CreateUserAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>());
    }
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantLifecycleGrpcServiceTests"`
Expected: the build fails with `CS0122` ("`IntelligenceTenantScope.NoTenantSentinel` is inaccessible due to its protection level").

- [ ] **Step 3: Make the sentinel public**

In `Iverson.Vector/IntelligenceTenantScope.cs:10`, replace

```csharp
    private const string NoTenantSentinel = "__no-tenant-claim__";
```

with:

```csharp
    public const string NoTenantSentinel = "__no-tenant-claim__";
```

- [ ] **Step 4: Run the test and confirm it now fails on behaviour**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantLifecycleGrpcServiceTests"`
Expected: 1 failure, `CreateTenant_NoTenantSentinelId_ThrowsInvalidArgumentAndTouchesNoDependency` ("no exception was thrown"). The other 10 pass.

- [ ] **Step 5: Reject the sentinel in `CreateTenant`**

Add `using Iverson.Vector;` to `TenantLifecycleGrpcService.cs`, after `using Iverson.StarRocks;`. Replace `:17`

```csharp
        if (!TenantIdentifier.IsValid(request.TenantId))
```

with:

```csharp
        // The no-tenant sentinel passes IsValid, but a tenant under it would own the Qdrant sentinel
        // collection that null-tenant reads and writes rely on never existing. Ordinal: only a
        // byte-identical id fingerprints to the sentinel collection name.
        if (!TenantIdentifier.IsValid(request.TenantId)
            || string.Equals(request.TenantId, IntelligenceTenantScope.NoTenantSentinel, StringComparison.Ordinal))
```

- [ ] **Step 6: Run the test and confirm it passes**

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantLifecycleGrpcServiceTests"`
Expected: `Failed: 0` (11 tests).

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Vector/IntelligenceTenantScope.cs Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs
git commit -m "reject the Qdrant no-tenant sentinel as a tenant id in CreateTenant

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

- [ ] **Step 8: Completion gate: quick loop, then the full suite**

This needs Docker running for the Testcontainers-based tests.

Run: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~Consumers&FullyQualifiedName!~KafkaOrdering"`
Expected: `Failed: 0`. Before counting, confirm the matched set with `--list-tests`: 165 today, plus 15 from Task 1, plus 1 from Task 3, plus 1 net from Task 5, for 182.

Run: `dotnet test Iverson.Api.Tests`
Expected: `Failed: 0`. If anything fails, report the failing test names and output. Do not weaken or skip tests to make the gate pass.

## Tasks NOT in this plan

- Round 1 Finding 1 and round 2 Findings 1 and 2 (orchestrator extraction, retrieval Engine, tenant-aware `Iverson.Vector`).
- Collapsing the non-tenant `ExtractString` copies and the five `Deserialize` copies.
- Reusing the authoritative row for `IntelligenceStoreConsumer`'s summary read.
- The `DlqMonitorConsumer` tenant-from-payload attribution used by `/admin/dlq` visibility.
- Existing tenants or seeds whose id already equals the sentinel. None exist: the legacy seed list is hard-coded in `Program.cs:511`, and no path besides `CreateTenant` inserts an arbitrary id.
