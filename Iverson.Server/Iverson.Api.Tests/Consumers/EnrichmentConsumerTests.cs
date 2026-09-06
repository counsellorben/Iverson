using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class EnrichmentConsumerTests
{
    private readonly IEventConsumer _consumer = Substitute.For<IEventConsumer>();
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEnrichmentStateRepository _state = Substitute.For<IEnrichmentStateRepository>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly IOutboxPublisher _outboxPublisher = Substitute.For<IOutboxPublisher>();
    private readonly IRecordStoreTransactionRunner _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
    private readonly IDbTransactionContext _tx = Substitute.For<IDbTransactionContext>();
    private readonly IEnrichmentService _enrichment = Substitute.For<IEnrichmentService>();
    private readonly SchemaRegistry _registry;

    // Ordered log of every call made inside the writeback transaction, so tests can assert the
    // mandatory enter-scope → update → exit-scope → plumbing-writes ordering.
    private readonly List<string> _txCalls = [];

    private const string Key = "11111111-1111-1111-1111-111111111111";
    private const string Tenant = "test-tenant";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EnrichmentConsumerTests()
    {
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);

        _tx.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(ci => { _txCalls.Add((string)ci[0]!); return 0; });

        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
                 .Returns(ci => ((Func<IDbTransactionContext, Task>)ci[0]!)(_tx));

        _entities.UpdateColumnsAsync(
                Arg.Any<IDbTransactionContext>(), Arg.Any<TableSchema>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(_ => { _txCalls.Add("UPDATE_COLUMNS"); return Task.CompletedTask; });

        _state.UpsertAsync(
                Arg.Any<IDbTransactionContext>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns(_ => { _txCalls.Add("STATE_UPSERT"); return Task.CompletedTask; });

        _outboxWriter.EnqueueUpdateOutboxRowAsync(
                Arg.Any<IDbTransactionContext>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => { _txCalls.Add("OUTBOX_ENQUEUE"); return Task.CompletedTask; });

        _state.GetHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
              .Returns((string?)null);

        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("generated summary");
        _enrichment.GenerateJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("""{"a":1}""");

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>()).Returns(RowJson());
    }

    // Article with a chunk source property (Body) and two enrichment targets.
    private static SchemaDescriptor EnrichedArticle(string? extractHint = "the author's stated conclusion") => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Body",     "text", false),
            new ColumnDescriptor("Summary",  "text", true),
            new ColumnDescriptor("Extracted","text", true)
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
        Relations    = [],
        TenantColumn = "TenantId",
        EnrichmentTargets =
        [
            new EnrichmentTarget("Summary",   EnrichmentKind.Summary,   null),
            new EnrichmentTarget("Extracted", EnrichmentKind.Extracted, extractHint)
        ]
    };

    // The same Article with the Extracted column as its only enrichment target.
    private static SchemaDescriptor ExtractedOnlyArticle() => EnrichedArticle() with
    {
        EnrichmentTargets = [new EnrichmentTarget("Extracted", EnrichmentKind.Extracted, "the author's stated conclusion")]
    };

    // A pre-2026-07-17 _iverson_schema row for Article: exactly what RegisterAsync would have
    // written, minus the `tenantColumn` key, which did not exist before 63a577a. This is the only
    // way a tenant-less descriptor can reach a consumer now that SchemaDescriptor.TenantColumn is
    // non-nullable — RegisterAsync's argument is compile-time checked, LoadAsync's input is not.
    private static string LegacyArticleRowJson()
    {
        var json = JsonSerializer.Serialize(
            EnrichedArticle(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var stripped = json.Replace(",\"tenantColumn\":\"TenantId\"", "", StringComparison.Ordinal);
        stripped.Should().NotContain("tenantColumn");
        return stripped;
    }

    private static string RowJson(string body = "The source body text.", string? tenant = Tenant) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"]       = Key,
            ["Body"]     = body,
            ["Summary"]  = null,
            ["Extracted"] = null,
            ["TenantId"] = tenant
        });

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private string Event(EntityEventType type, string payload = "{}") =>
        Serialize(new EntityEvent(type, "Article", Key, payload, "trace", "1",
            DateTimeOffset.UtcNow, StoreTarget.All));

    private EnrichmentConsumer BuildSut() =>
        new(_consumer, _registry, _entities, _state, _outboxWriter, _outboxPublisher,
            _txRunner, _enrichment, NullLogger<EnrichmentConsumer>.Instance);

    // Reproduces the hash the consumer stores for a given schema + row, by running one
    // enrichment pass and capturing what it wrote to the state table.
    private async Task<string> CaptureHashAsync(SchemaDescriptor schema)
    {
        var registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);

        string? captured = null;
        var state = Substitute.For<IEnrichmentStateRepository>();
        state.GetHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);
        state.UpsertAsync(Arg.Any<IDbTransactionContext>(), Arg.Any<string>(), Arg.Any<string>(),
                          Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
             .Returns(ci => { captured = (string)ci[4]!; return Task.CompletedTask; });

        var sut = new EnrichmentConsumer(_consumer, registry, _entities, state, _outboxWriter,
            _outboxPublisher, _txRunner, _enrichment, NullLogger<EnrichmentConsumer>.Instance);
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        captured.Should().NotBeNull("the capture pass must have enriched");

        // The capture pass reuses the shared substitutes; reset them so the test's own
        // assertions only see the calls its own pass made.
        _enrichment.ClearReceivedCalls();
        _entities.ClearReceivedCalls();
        _txRunner.ClearReceivedCalls();
        _outboxWriter.ClearReceivedCalls();
        _outboxPublisher.ClearReceivedCalls();
        _txCalls.Clear();

        return captured!;
    }

    // ── The loop breaker ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleUpdated_WithUnchangedHash_DoesNotCallLlmAndDoesNotWrite()
    {
        var schema = EnrichedArticle();
        var hash = await CaptureHashAsync(schema);

        await _registry.RegisterAsync(schema);
        _state.GetHashAsync(Tenant, "Article", Key).Returns(hash);
        _enrichment.ClearReceivedCalls();

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _enrichment.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default);
        await _enrichment.DidNotReceiveWithAnyArgs().GenerateJsonAsync(default!, default);
        await _txRunner.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!);
        await _outboxPublisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default, default!, default!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task HandleUpdated_WithChangedExtractHint_ReEnrichesDespiteUnchangedSourceText()
    {
        // Hash recorded under the old hint...
        var oldHash = await CaptureHashAsync(EnrichedArticle("the author's stated conclusion"));

        // ...then the hint is edited; the source text is untouched.
        await _registry.RegisterAsync(EnrichedArticle("the publication date"));
        _state.GetHashAsync(Tenant, "Article", Key).Returns(oldHash);

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _enrichment.ReceivedWithAnyArgs().GenerateAsync(default!, default);
        await _entities.ReceivedWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!);
    }

    // ── Targeted writeback ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleUpdated_UpdatesOnlyEnrichmentColumns_PreservingConcurrentClientEdit()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        IReadOnlyDictionary<string, object?>? written = null;
        _entities.UpdateColumnsAsync(Arg.Any<IDbTransactionContext>(), Arg.Any<TableSchema>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(ci => { written = (IReadOnlyDictionary<string, object?>)ci[3]!; return Task.CompletedTask; });

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        written.Should().NotBeNull();
        written!.Keys.Should().BeEquivalentTo(["Summary", "Extracted"]);
        written.Should().NotContainKey("Body", "a client edit to a non-enrichment column must survive");
        written.Should().NotContainKey("TenantId");

        // The whole-row upsert path must not be used at all — it would clobber concurrent edits.
        await _outboxWriter.DidNotReceiveWithAnyArgs()
            .UpsertAndEnqueueOutboxAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleUpdated_ExitsTenantScopeBeforeStateAndOutboxWrites()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        _txCalls.Should().ContainInOrder(
            "SET LOCAL ROLE iverson_runtime",
            "UPDATE_COLUMNS",
            "RESET ROLE",
            "STATE_UPSERT",
            "OUTBOX_ENQUEUE");
    }

    [Fact]
    public async Task HandleUpdated_PublishesPostCommitRefetch_NotThePreGenerationSnapshot()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        // First fetch = pre-generation snapshot; second fetch = post-commit re-fetch, which
        // includes a client edit that landed during the LLM call.
        var freshRow = RowJson("The body a client edited mid-enrichment.");
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Key).Returns(RowJson(), freshRow);

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _outboxPublisher.Received().PublishAsync(
            EntityEventType.Updated, "Article", Key, freshRow,
            Arg.Any<string?>(), Arg.Any<StoreTarget>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // The cap sits on the SOURCE TEXT, not the assembled prompt: three prompts lead with their
    // instruction and the extraction prompt trails with its hint, so a cut on the prompt would drop
    // one of them (spec §3.5). A cap on the assembled prompt fails the EndWith assertion.
    [Fact]
    public async Task HandleUpdated_CutsTheSourceTextTo8000Chars_KeepingTheInstructionAndTheHint()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Key).Returns(RowJson(new string('x', 20_000)));
        string? extractionPrompt = null;
        _enrichment.GenerateJsonAsync(Arg.Do<string>(p => extractionPrompt = p), Arg.Any<CancellationToken>())
                   .Returns("""{"a":1}""");

        await BuildSut().HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        extractionPrompt.Should().NotBeNull();
        extractionPrompt.Should().StartWith("Extract structured information");
        extractionPrompt.Should().EndWith("Extract specifically: the author's stated conclusion");
        extractionPrompt.Should().Contain(new string('x', EnrichmentConsumer.MaxSourceChars));
        extractionPrompt.Should().NotContain(new string('x', EnrichmentConsumer.MaxSourceChars + 1));
    }

    // ── Null tenant ───────────────────────────────────────────────────────────

    // RE-POINTED by Task 7, not weakened. It previously registered a hand-built descriptor with
    // TenantColumn = null and asserted EnrichmentConsumer's own `schema.TenantColumn is not null`
    // branch skipped the row. SchemaDescriptor.TenantColumn is now non-nullable and that branch is
    // gone, so the test now drives the ONE path by which such a schema could ever have existed —
    // SchemaRegistry.LoadAsync rehydrating a pre-cutover _iverson_schema row — and asserts the
    // same outcome plus the mechanism that now produces it: the type is never registered at all,
    // so the consumer drops on its existing unknown-type guard. Strictly more than before: the
    // old version could not have caught a registry that admitted the row.
    [Fact]
    public async Task HandleUpdated_LegacySchemaWithNoTenantColumn_IsNotRegistered_AndWritesNoStateRow()
    {
        var repository = Substitute.For<ISchemaRegistryRepository>();
        repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", LegacyArticleRowJson())
        });
        var registry = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();

        registry.IsRegistered("Article").Should()
            .BeFalse("a rehydrated row with no server-owned tenant column must not be admitted");

        var sut = new EnrichmentConsumer(_consumer, registry, _entities, _state, _outboxWriter,
            _outboxPublisher, _txRunner, _enrichment, NullLogger<EnrichmentConsumer>.Instance);

        // RE-POINTED AGAIN by the Ruling 56 fix, and the change of outcome is deliberate. The
        // consumer's unknown-type guard used to RETURN, which commits the Kafka offset and loses
        // the event silently; it now throws, so the event reaches the DLQ. For a row the registry
        // will NEVER admit that means its events dead-letter rather than vanish — loud and
        // recoverable instead of silent, which is the whole point of the fix. The three
        // "wrote nothing" assertions below are unchanged and still the substance of the test.
        var act = () => sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Article*");

        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
        await _enrichment.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default);
        await _txRunner.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!);
    }

    [Fact]
    public async Task HandleUpdated_WithNullTenantValueInRow_SkipsAndWritesNoStateRow()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns(RowJson(tenant: null));

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
        await _txRunner.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!);
    }

    // ── Deletes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleDelete_RemovesStateRow_SourcingTenantFromPreDeleteSnapshot()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        var sut = BuildSut();
        await sut.HandleDeleteAsync(Key, Event(EntityEventType.Deleted, RowJson()), CancellationToken.None);

        await _state.Received().DeleteAsync(Tenant, "Article", Key);
        // The row is already gone by delete-consumption time — no re-fetch may be attempted.
        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(default!, default!);
    }

    [Fact]
    public async Task HandleDelete_WithNoTenantInSnapshot_SkipsTheStateDelete()
    {
        await _registry.RegisterAsync(EnrichedArticle());

        var sut = BuildSut();
        await sut.HandleDeleteAsync(Key, Event(EntityEventType.Deleted, RowJson(tenant: null)),
            CancellationToken.None);

        await _state.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default!);
    }

    [Fact]
    public async Task DeleteThenRecreateWithSameKey_Enriches_RatherThanInheritingStaleHash()
    {
        var schema = EnrichedArticle();
        var hash = await CaptureHashAsync(schema);
        await _registry.RegisterAsync(schema);

        // Delete removes the state row, so the recreate's lookup misses.
        var stored = hash;
        _state.GetHashAsync(Tenant, "Article", Key).Returns(_ => stored);
        _state.DeleteAsync(Tenant, "Article", Key).Returns(_ => { stored = null; return Task.CompletedTask; });

        var sut = BuildSut();
        await sut.HandleDeleteAsync(Key, Event(EntityEventType.Deleted, RowJson()), CancellationToken.None);
        await sut.HandleAsync(Key, Event(EntityEventType.Created), CancellationToken.None);

        await _entities.ReceivedWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!);
    }

    // ── Failure handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleUpdated_WhenLlmFails_LeavesObjectIntactAndDoesNotThrow()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Throws(new HttpRequestException("ollama down"));

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _entities.DidNotReceiveWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!);
        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
        await _outboxPublisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default, default!, default!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task HandleUpdated_WhenWritebackFails_DoesNotThrowPoisonMessageException()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
                 .Throws(new InvalidOperationException("connection reset"));

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // I3: GenerateJsonAsync throws InvalidOperationException when the reply holds no parseable
    // JSON object. That must skip only the Extracted column (spec §4: "nothing is stored for that
    // column") — not discard the object's already-generated Summary column, and not escape as an
    // unhandled exception that would retry the whole object forever at temperature 0.
    [Fact]
    public async Task HandleUpdated_WhenExtractionYieldsNoParseableJson_SkipsThatColumnButWritesTheRest()
    {
        await _registry.RegisterAsync(EnrichedArticle());
        _enrichment.GenerateJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Throws(new InvalidOperationException("no parseable JSON object in reply"));

        IReadOnlyDictionary<string, object?>? written = null;
        _entities.UpdateColumnsAsync(Arg.Any<IDbTransactionContext>(), Arg.Any<TableSchema>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(ci => { written = (IReadOnlyDictionary<string, object?>)ci[3]!; return Task.CompletedTask; });

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await act.Should().NotThrowAsync();
        written.Should().NotBeNull();
        written!.Should().ContainKey("Summary");
        written.Should().NotContainKey(
            "Extracted", "extraction with no parseable JSON must be skipped, not fail the whole object");
        await _state.ReceivedWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
    }

    // When the Extracted column is the type's ONLY target, skipping it leaves nothing to write —
    // and the consumer used to return without a state row, so every later event for the object
    // (and every ReconcileTypeAsync replay) re-ran the same temperature-0 generation and failed
    // the same way, forever. A generation pass that ran to completion IS the enrichment result
    // for this source+specification hash, empty or not: record it, exactly as the mixed case
    // above records a hash that already covers its skipped column, so the object is generated
    // again only when its source text or its specification changes.
    [Fact]
    public async Task HandleUpdated_WhenEveryTargetIsSkipped_WritesTheStateRowWithoutAWritebackOrARepublish()
    {
        await _registry.RegisterAsync(ExtractedOnlyArticle());
        _enrichment.GenerateJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Throws(new InvalidOperationException("no parseable JSON object in reply"));

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _state.Received(1).UpsertAsync(
            Arg.Any<IDbTransactionContext>(), Tenant, "Article", Key, Arg.Any<string>(), Arg.Any<DateTimeOffset>());
        await _entities.DidNotReceiveWithAnyArgs().UpdateColumnsAsync(default!, default!, default!, default!);
        await _outboxWriter.DidNotReceiveWithAnyArgs().EnqueueUpdateOutboxRowAsync(
            default!, default, default!, default!, default!);
        await _outboxPublisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default, default!, default!, default!, default, default, default, default!, default);
        _txCalls.Should().Equal(["STATE_UPSERT"], "no column update, so no tenant scope is entered either");
    }

    [Fact]
    public async Task HandleUpdated_AfterEveryTargetWasSkipped_DoesNotRetryTheSameSourceAndSpecification()
    {
        _enrichment.GenerateJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Throws(new InvalidOperationException("no parseable JSON object in reply"));
        var hash = await CaptureHashAsync(ExtractedOnlyArticle());

        await _registry.RegisterAsync(ExtractedOnlyArticle());
        _state.GetHashAsync(Tenant, "Article", Key).Returns(hash);

        await BuildSut().HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _enrichment.DidNotReceiveWithAnyArgs().GenerateJsonAsync(default!, default);
        await _state.DidNotReceiveWithAnyArgs().UpsertAsync(
            default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleUpdated_ForTypeWithNoEnrichmentTargets_DoesNothing()
    {
        await _registry.RegisterAsync(Helpers.SchemaFixtures.ArticleSchema());

        var sut = BuildSut();
        await sut.HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        await _state.DidNotReceiveWithAnyArgs().GetHashAsync(default!, default!, default!);
        await _enrichment.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default);
    }

    // ── Registration gate ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void AddEnrichmentPipeline_GatesOnlyTheHostedServiceOnEnabledFlag(string flag, bool expectHostedService)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Enrichment:Enabled"] = flag })
            .Build();

        services.AddEnrichmentPipeline(config, isWorker: true);

        var hasConsumer = services.Any(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EnrichmentConsumer));
        hasConsumer.Should().Be(expectHostedService);

        // IEnrichmentService is registered either way — other consumers resolve it regardless.
        services.Any(d => d.ServiceType == typeof(IEnrichmentService)).Should().BeTrue();
    }

    [Fact]
    public void AddEnrichmentPipeline_InApiRole_DoesNotRegisterTheHostedService()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddEnrichmentPipeline(config, isWorker: false);

        services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(EnrichmentConsumer)).Should().BeFalse();
        services.Any(d => d.ServiceType == typeof(IEnrichmentService)).Should().BeTrue();
    }
}
