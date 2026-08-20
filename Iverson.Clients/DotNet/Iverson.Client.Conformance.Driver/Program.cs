using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Conformance.Driver;
using Iverson.Client.Conformance.Driver.Models;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.Client.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ── The .NET conformance driver ────────────────────────────────────────────────────────────────
// Reports; never asserts. Every judgement belongs to the orchestrator's Verifier. A step that
// throws becomes ok:false with an error message and the process still exits 0 — a non-zero exit
// means the driver itself broke (bad flags, unsupported scenario, unwritable --out).

const string Language = "dotnet";
const string CrudRoundtripScenario = "crud-roundtrip";
// interop (S4): only the .NET driver ever runs this scenario's register phase — see
// Scenarios/InteropScenario.cs for the register-once rule. The other four drivers only ever see
// this scenario's write/read phases.
const string InteropScenario = "interop";
// schema-catalog (S5): register + read only. Registers DotNetAuthor, then fetches the catalogue
// back through SchemaCatalogClient — the client library's own public schema-retrieval surface.
const string SchemaCatalogScenario = "schema-catalog";
// query (S6): register (this driver only, register-once), write, read. Seeds one QueryDoc row
// carrying the run's marker, then issues a filtered search and a count aggregate through the
// client library's own QueryBuilder/AggregateBuilder.
const string QueryScenario = "query";
var supportedScenarios = new[]
    { CrudRoundtripScenario, InteropScenario, SchemaCatalogScenario, QueryScenario };

var args_ = Args.Parse(args);

var scenario = args_.Require("--scenario");
if (!supportedScenarios.Contains(scenario, StringComparer.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"unsupported scenario '{scenario}'; this driver implements {string.Join(", ", supportedScenarios)}");
    return 2;
}

var phase = args_.Require("--phase");
var tenant = args_.Require("--tenant");
var ownerId = args_.Require("--owner-id");
var idPrefix = args_.Require("--id-prefix");
var outPath = args_.Require("--out");
var typeHint = args_.Optional("--type");

var invoker = Auth.BuildInvoker(
    args_.Require("--grpc"),
    args_.Optional("--client-id"),
    args_.Optional("--client-secret"),
    args_.Optional("--token-endpoint"),
    args_.Optional("--acting-token") ?? string.Empty,
    args_.Optional("--service-token"));

var capture = new DescriptorCaptureInterceptor();
// The capture seam wraps only the mapping stub used for schema registration; every other stub
// talks to the same authenticated invoker untouched.
var mappingForRegistration = new ObjectMappingService.ObjectMappingServiceClient(
    Grpc.Core.Interceptors.CallInvokerExtensions.Intercept(invoker, capture));
var mapping = new ObjectMappingService.ObjectMappingServiceClient(invoker);
var persistence = new ObjectPersistenceService.ObjectPersistenceServiceClient(invoker);
var retrieval = new ObjectRetrievalService.ObjectRetrievalServiceClient(invoker);
var search = new ObjectSearchService.ObjectSearchServiceClient(invoker);

var registry = new EntityRegistry([typeof(DotNetArticle).Assembly]);
var assembler = new GraphAssembler(retrieval, registry, NullLogger<GraphAssembler>.Instance);

// EntityCoordinator reports a server-side refusal (MappingResponse.Success = false — denials come
// back in the response body, not as an RpcException) by returning null and logging the server's
// error text. Nothing else surfaces it, so the driver collects those log lines and reports them
// verbatim: without this a fully denied write phase would look like a clean success.
var clientErrors = new ClientErrors();

EntityCoordinator<T> Coordinator<T>() where T : class =>
    new(registry, assembler, mapping, persistence, retrieval, search,
        new ClientErrorLogger<EntityCoordinator<T>>(clientErrors));

// Keys are driver-chosen UUIDs derived from the run id, so two runs never collide and every
// phase after `write` can re-derive nothing — it reads them back from --keys.
var priorKeys = Keys.Parse(args_.Optional("--keys"), Language);
Guid KeyFor(string logicalName) =>
    priorKeys.TryGetValue(logicalName, out var existing) && Guid.TryParse(existing, out var parsed)
        ? parsed
        : Keys.Derive(idPrefix, logicalName);

var steps = new List<StepResult>();

if (scenario == InteropScenario)
{
    await RunInteropAsync();
}
else if (scenario == SchemaCatalogScenario)
{
    await RunSchemaCatalogAsync();
}
else if (scenario == QueryScenario)
{
    await RunQueryAsync();
}
else
{
    await RunCrudRoundtripAsync();
}

var document = new PhaseDocument(Language, phase, steps);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(document, Json.DocumentOptions));
return 0;

// ── S4 interop ───────────────────────────────────────────────────────────────────────────────
async Task RunInteropAsync()
{
    switch (phase)
    {
        case "register":
        {
            // Only the .NET driver ever runs this phase for interop (register-once rule; see
            // Scenarios/InteropScenario.cs). Registers SharedAuthor then SharedArticle, without
            // an authorization block — the orchestrator re-registers both once with one added.
            string? registerOutcome = null;
            foreach (var typeName in new[] { nameof(SharedAuthor), nameof(SharedArticle) })
            {
                capture.OnlySendTypeName = typeName;
                registerOutcome = await Run(async () =>
                {
                    var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                    await registrar.RegisterAllAsync();
                });
                if (registerOutcome is not null) break;
            }

            capture.OnlySendTypeName = null;

            steps.Add(new StepResult(
                "register_shared_author", Ok: registerOutcome is null, Error: registerOutcome,
                TypeDescriptor: Json.Element(capture.Select(nameof(SharedAuthor)))));
            steps.Add(new StepResult(
                "register_shared_article", Ok: registerOutcome is null, Error: registerOutcome,
                TypeDescriptor: Json.Element(capture.Select(nameof(SharedArticle)))));
            break;
        }

        case "write":
        {
            Guid? authorKey = null;

            await Step("write_shared_author",
                async result =>
                {
                    var written = await Coordinator<SharedAuthor>().PostMappedAsync(new SharedAuthor
                    {
                        TenantId = tenant,
                        OwnerId = ownerId,
                        Name = $"shared-author-{idPrefix}",
                    });
                    authorKey = written?.Id;
                    return result with { Entity = Json.Element(written) };
                },
                result => authorKey is { } key
                    ? result with { Keys = new Dictionary<string, string> { ["shared_author"] = key.ToString() } }
                    : result);

            await Step("write_shared_article",
                async result =>
                {
                    var written = await Coordinator<SharedArticle>().PostMappedAsync(new SharedArticle
                    {
                        TenantId = tenant,
                        OwnerId = ownerId,
                        Title = $"shared-title-{idPrefix}",
                        SharedAuthorId = authorKey ?? Guid.Empty,
                    });
                    return result with { Entity = Json.Element(written), Keys = written is null
                        ? null
                        : new Dictionary<string, string> { ["shared_article"] = written.Id.ToString() } };
                });
            break;
        }

        case "read":
        {
            // Iterates every language's reported "shared_article" key from the full --keys map
            // (not just this language's own slice), so this one driver invocation reads all five
            // languages' rows — the fan-out that produces 25 reads across the five drivers.
            var allKeys = Keys.ParseAll(args_.Optional("--keys"));
            foreach (var (writerLanguage, key) in AllSharedArticleKeys(allKeys))
            {
                await Step($"read_shared_article_{writerLanguage}", async result =>
                {
                    var article = await Coordinator<SharedArticle>().GetMappedAsync(key, depth: 0);
                    return result with { Entity = Json.Element(article) };
                });
            }
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"unknown phase '{phase}' for scenario '{scenario}'");
            Environment.Exit(2);
            break;
    }
}

static IEnumerable<(string Language, string Key)> AllSharedArticleKeys(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> allKeys)
{
    foreach (var (language, keys) in allKeys.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        if (keys.TryGetValue("shared_article", out var key) && key.Length > 0)
            yield return (language, key);
}

// ── S5 schema-catalog ────────────────────────────────────────────────────────────────────────
async Task RunSchemaCatalogAsync()
{
    switch (phase)
    {
        case "register":
        {
            // One relation-free type, registered WITHOUT an authorization block on purpose: the
            // orchestrator re-registers it with one before the read phase, and until it does the
            // type is Denied for Read and GetSchema omits it entirely. DotNetAuthor is this
            // language's own type name, so all five languages registering concurrently overwrite
            // nothing.
            capture.OnlySendTypeName = nameof(DotNetAuthor);
            var registerOutcome = await Run(async () =>
            {
                var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                await registrar.RegisterAllAsync();
            });
            capture.OnlySendTypeName = null;

            steps.Add(new StepResult(
                "register_schema_type",
                Ok: registerOutcome is null,
                Error: registerOutcome,
                TypeDescriptor: Json.Element(capture.Select(nameof(DotNetAuthor)))));
            break;
        }

        case "read":
        {
            // SchemaCatalogClient is the client library's public schema-retrieval surface; the
            // acting-user identity rides on the invoker this driver built (Auth.cs), which is why
            // no token provider is passed here. The catalogue is reported verbatim and judged by
            // nobody in this process.
            await Step("get_schema", async result =>
            {
                var catalogue = await new SchemaCatalogClient(mapping).GetSchemaAsync();
                return result with { Entity = Json.Element(CatalogueToReport(catalogue)) };
            });
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"unknown phase '{phase}' for scenario '{scenario}'");
            Environment.Exit(2);
            break;
    }
}

/// <summary>
/// The deliberately minimal, cross-language-identical projection of a GetSchema catalogue that all
/// five drivers report. Copies names verbatim out of the SchemaType messages the client library
/// returned; filters nothing and decides nothing.
/// </summary>
static object CatalogueToReport(IReadOnlyList<SchemaType> types) => new
{
    types = types.Select(t => new
    {
        name = t.Name,
        fields = t.Fields.Select(f => new { name = f.Name }).ToList(),
        relations = t.Relations.Select(r => new { propertyName = r.PropertyName }).ToList(),
    }).ToList(),
};

// ── S6 query ─────────────────────────────────────────────────────────────────────────────────
async Task RunQueryAsync()
{
    switch (phase)
    {
        case "register":
        {
            // Only the .NET driver ever runs this phase for query (register-once rule; see
            // Scenarios/QueryScenario.cs). Registered WITHOUT an authorization block — the
            // orchestrator re-registers it with one before any driver's write phase.
            capture.OnlySendTypeName = nameof(QueryDoc);
            var registerOutcome = await Run(async () =>
            {
                var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                await registrar.RegisterAllAsync();
            });
            capture.OnlySendTypeName = null;

            steps.Add(new StepResult(
                "register_query_doc",
                Ok: registerOutcome is null,
                Error: registerOutcome,
                TypeDescriptor: Json.Element(capture.Select(nameof(QueryDoc)))));
            break;
        }

        case "write":
        {
            // One row, stamped with the run's marker. The key is reported unconditionally when the
            // write returned one — it is the orchestrator's expected-set accounting, and a row
            // seeded but never reported would silently shrink what every language is graded
            // against.
            Guid? key = null;
            await Step("write_query_doc",
                async result =>
                {
                    var written = await Coordinator<QueryDoc>().PostMappedAsync(new QueryDoc
                    {
                        TenantId = tenant,
                        OwnerId = ownerId,
                        Marker = idPrefix,
                        Label = $"doc-{Language}",
                    });
                    key = written?.Id;
                    return result with { Entity = Json.Element(written) };
                },
                result => key is { } k
                    ? result with { Keys = new Dictionary<string, string> { ["query_doc"] = k.ToString() } }
                    : result);
            break;
        }

        case "read":
        {
            // The filter and the aggregation are both built with the client library's own builder
            // API (Query.For<T>() / AggregateBuilder) and executed through EntityCoordinator, never
            // through the generated stub. What is reported is the row keys and the metric value,
            // verbatim; the orchestrator decides what they mean.
            await Step("search_by_marker", async result =>
            {
                var keys = new List<string>();
                var query = Query.For<QueryDoc>()
                    .Where(d => d.Marker, SearchOperator.Equals, idPrefix)
                    .Page(0, 100);
                await foreach (var hit in Coordinator<QueryDoc>().SearchAsync(query))
                    keys.Add(hit.Entity.Id.ToString());

                return result with { Entity = Json.Element(new { keys }) };
            });

            await Step("aggregate_count", async result =>
            {
                var aggregate = new AggregateBuilder(nameof(QueryDoc))
                    .Where(nameof(QueryDoc.Marker), SearchOperator.Equals, idPrefix)
                    .CountAll("count");
                var response = await Coordinator<QueryDoc>().AggregateAsync(aggregate);
                var metric = response.Results.Count > 0 ? response.Results[0].MetricValue : (double?)null;

                return result with { Entity = Json.Element(new { value = metric, total = response.Total }) };
            });
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"unknown phase '{phase}' for scenario '{scenario}'");
            Environment.Exit(2);
            break;
    }
}

// ── S1 crud-roundtrip ────────────────────────────────────────────────────────────────────────
async Task RunCrudRoundtripAsync()
{
switch (phase)
{
    case "register":
    {
        // One step per registered type. Every type the orchestrator has to re-register with
        // authorization rules needs its own descriptor reported: a type whose stored schema has
        // no Authorization block is writable by nobody, so reporting only the article's
        // descriptor would leave the author and tag rows un-writable in the write phase.
        //
        // RegisterAllAsync issues one RegisterSchema call per type, sequentially over the
        // registry, and rethrows the RpcException the server raises on a validation failure
        // (RegisterSchema has no Success=false path) — so the sequence aborts at the first
        // failing type and the types after it are never sent. All three steps therefore share
        // the aborted sequence's outcome; what distinguishes them is `typeDescriptor`, which is
        // present only for the types actually sent and null for those never reached.
        //
        // These steps deliberately bypass Step(), so clientErrors is never cleared here. That is
        // safe only because no coordinator call precedes them in this phase — SchemaRegistrar
        // gets its own null logger. Adding any coordinator call before this point would leak its
        // logged error text into all three register steps; move to Step()/Clear() if that happens.
        //
        // Order is fixed to author -> tag -> article, matching the other four drivers, so the
        // types the article's relations reference already exist when the article is sent. The
        // registrar walks EntityRegistry.All, whose order is a dictionary's, so the driver drives
        // the order itself: one RegisterAllAsync pass per type with the other two suppressed at
        // the capture interceptor (nothing is sent for them). The first failing pass aborts the
        // rest, preserving the abort-at-first-failure semantics the other drivers get for free.
        string? registerOutcome = null;
        foreach (var typeName in new[] { nameof(DotNetAuthor), nameof(DotNetTag), nameof(DotNetArticle) })
        {
            capture.OnlySendTypeName = typeName;
            registerOutcome = await Run(async () =>
            {
                var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                await registrar.RegisterAllAsync();
            });
            if (registerOutcome is not null) break;
        }

        capture.OnlySendTypeName = null;

        AddStep("register", registerOutcome, capture.Select(typeHint, nameof(DotNetArticle)));
        AddStep("register_author", registerOutcome, capture.Select(nameof(DotNetAuthor)));
        AddStep("register_tag", registerOutcome, capture.Select(nameof(DotNetTag)));

        void AddStep(string name, string? error, string? descriptorJson) =>
            steps.Add(new StepResult(
                name,
                Ok: error is null,
                Error: error,
                TypeDescriptor: Json.Element(descriptorJson)));

        break;
    }

    case "write":
    {
        // One step per row. A denied or failed write must not abort the other two, and each row's
        // key is reported unconditionally (via `always`) so the orchestrator can address the row
        // in later phases even when this write failed — a phase that reported no keys would leave
        // the read/update/delete phases with nothing to ask for.
        // Keys are now server-assigned: create requests must omit Id, and the row's actual key is
        // read back from the write response (and only reported if the write returned one).
        Guid? authorKey = null;
        Guid? tagKey = null;

        await Step("write_author",
            async result =>
            {
                var written = await Coordinator<DotNetAuthor>().PostMappedAsync(new DotNetAuthor
                {
                    TenantId = tenant,
                    OwnerId = ownerId,
                    Name = $"author-{idPrefix}",
                });
                authorKey = written?.Id;
                return result with { Entity = Json.Element(written) };
            },
            result => authorKey is { } key
                ? result with { Keys = new Dictionary<string, string> { ["author"] = key.ToString() } }
                : result);

        await Step("write_tag",
            async result =>
            {
                var written = await Coordinator<DotNetTag>().PostMappedAsync(new DotNetTag
                {
                    TenantId = tenant,
                    OwnerId = ownerId,
                    Label = $"tag-{idPrefix}",
                });
                tagKey = written?.Id;
                return result with { Entity = Json.Element(written) };
            },
            result => tagKey is { } key
                ? result with { Keys = new Dictionary<string, string> { ["tag"] = key.ToString() } }
                : result);

        Guid? articleKey = null;

        await Step("write_article",
            async result =>
            {
                var written = await Coordinator<DotNetArticle>().PostMappedAsync(new DotNetArticle
                {
                    TenantId = tenant,
                    OwnerId = ownerId,
                    Title = $"title-{idPrefix}",
                    DotNetAuthorId = authorKey ?? Guid.Empty,
                    DotNetTagIds = tagKey is { } t ? [t] : [],
                    DotNetTagId = tagKey ?? Guid.Empty,
                });
                articleKey = written?.Id;
                return result with { Entity = Json.Element(written) };
            },
            result => articleKey is { } key
                ? result with { Keys = new Dictionary<string, string> { ["article"] = key.ToString() } }
                : result);

        break;
    }

    case "read":
    {
        // Two gets at depth 0, reported separately so the orchestrator has a driver-side
        // observation of each row rather than one conflated step.
        await Step("get", async result =>
        {
            var article = await Coordinator<DotNetArticle>().GetMappedAsync(KeyFor("article").ToString(), depth: 0);
            return result with { Entity = Json.Element(article) };
        });

        await Step("get_author", async result =>
        {
            var author = await Coordinator<DotNetAuthor>().GetMappedAsync(KeyFor("author").ToString(), depth: 0);
            return result with { Entity = Json.Element(author) };
        });

        // IVC-LIFE-006/IVC-LIFE-008: a depth-1 read through this driver's OWN client library,
        // reported as its own step — proves the CLIENT can express the request (LIFE-006) and
        // materialize the hydrated result (LIFE-007), distinct from the orchestrator's own
        // depth-1 MappingGet which only proves the SERVER hydrates.
        await Step("get_depth1", async result =>
        {
            var article = await Coordinator<DotNetArticle>().GetMappedAsync(KeyFor("article").ToString(), depth: 1);
            return result with { Entity = Json.Element(article) };
        });
        break;
    }

    case "update":
    {
        await Step("update", async result =>
        {
            var updated = await Coordinator<DotNetArticle>().UpdateMappedAsync(new DotNetArticle
            {
                Id = KeyFor("article"),
                TenantId = tenant,
                OwnerId = ownerId,
                Title = $"title-{idPrefix}-updated",
                DotNetAuthorId = KeyFor("author"),
                DotNetTagIds = [KeyFor("tag")],
                DotNetTagId = KeyFor("tag"),
            });

            return result with { Entity = Json.Element(updated) };
        });
        break;
    }

    case "delete":
    {
        var deleteCoordinator = Coordinator<DotNetArticle>();
        var deleteKey = KeyFor("article").ToString();

        await Step("delete", async result =>
        {
            await deleteCoordinator.DeleteAsync(deleteKey);
            return result;
        });

        // The read-back is its own step. A null entity alone cannot tell "gone" from "read
        // denied" from "tenant mismatch", so the client's own error text is reported alongside
        // it; conflating this with the delete's error text would destroy that distinction too.
        await Step("get_after_delete", async result =>
        {
            var afterDelete = await deleteCoordinator.GetMappedAsync(deleteKey, depth: 0);
            return result with { Entity = Json.Element(afterDelete) };
        });
        break;
    }

    default:
        Console.Error.WriteLine($"unknown phase '{phase}'");
        Environment.Exit(2);
        break;
}
}

// A throwing step is data, not a driver failure: it becomes ok:false with an error and the
// process still exits 0. `always` enriches the result on both paths, for observations (like the
// captured descriptor) that are worth reporting even when the call itself failed.
async Task Step(
    string name,
    Func<StepResult, Task<StepResult>> body,
    Func<StepResult, StepResult>? always = null)
{
    clientErrors.Clear();

    var seed = new StepResult(name, true);
    StepResult outcome;
    try
    {
        outcome = await body(seed);
    }
    catch (Exception ex)
    {
        outcome = seed with { Ok = false, Error = Describe(ex) };
    }

    // A refusal the client surfaced by returning null and logging (rather than throwing) is
    // reported exactly like a thrown one: the client library's own failure signal is the
    // observation. The driver still does not look at any value to decide this.
    if (outcome.Ok && clientErrors.Any)
        outcome = outcome with { Ok = false, Error = clientErrors.Combined };

    steps.Add(always is null ? outcome : always(outcome));
}

/// <summary>Runs an action and returns its failure text, or null when it completed.</summary>
async Task<string?> Run(Func<Task> body)
{
    try
    {
        await body();
        return null;
    }
    catch (Exception ex)
    {
        return Describe(ex);
    }
}

static string Describe(Exception ex) => ex is RpcException rpc
    ? $"{rpc.StatusCode}: {rpc.Status.Detail}"
    : $"{ex.GetType().Name}: {ex.Message}";

namespace Iverson.Client.Conformance.Driver
{
    /// <summary>One step's outcome, serialized to the phase document the orchestrator reads.</summary>
    internal sealed record StepResult(
        string Name,
        bool Ok,
        string? Error = null,
        JsonElement? TypeDescriptor = null,
        IReadOnlyDictionary<string, string>? Keys = null,
        JsonElement? Entity = null);

    /// <summary>The whole <c>--out</c> document for one phase invocation.</summary>
    internal sealed record PhaseDocument(string Language, string Phase, IReadOnlyList<StepResult> Steps);

    /// <summary>
    /// Collects the error lines the client library logs. <c>EntityCoordinator</c> reports a
    /// server-side refusal by returning null and calling <c>LogError</c> with the server's error
    /// text — with a null logger that text is lost and a denied call is indistinguishable from a
    /// successful one.
    /// </summary>
    internal sealed class ClientErrors
    {
        private readonly List<string> _messages = [];

        public bool Any => _messages.Count > 0;
        public string Combined => string.Join("; ", _messages);

        public void Add(string message) => _messages.Add(message);
        public void Clear() => _messages.Clear();
    }

    internal sealed class ClientErrorLogger<T>(ClientErrors errors) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            errors.Add(formatter(state, exception));
        }
    }

    internal static class Json
    {
        /// <summary>camelCase, matching the orchestrator's <c>JsonSerializerDefaults.Web</c> reader.</summary>
        public static readonly JsonSerializerOptions DocumentOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Entities are serialized with their declared property names (PascalCase in .NET) rather
        /// than re-cased: what the client library produced is the observation.
        /// </summary>
        private static readonly JsonSerializerOptions EntityOptions = new();

        public static JsonElement? Element<T>(T? value) where T : class =>
            value is null ? null : JsonSerializer.SerializeToElement(value, EntityOptions);

        public static JsonElement? Element(string? rawJson) =>
            rawJson is null ? null : JsonDocument.Parse(rawJson).RootElement.Clone();
    }

    internal static class Keys
    {
        /// <summary>
        /// Derives a stable, collision-free UUID from the run id and a logical name. Deterministic
        /// so a phase can re-derive a key without --keys; distinct per run because --id-prefix is.
        /// </summary>
        public static Guid Derive(string idPrefix, string logicalName) =>
            new(MD5.HashData(Encoding.UTF8.GetBytes($"{idPrefix}:{logicalName}")));

        /// <summary>Reads this language's slice out of the language-qualified <c>--keys</c> map.</summary>
        public static IReadOnlyDictionary<string, string> Parse(string? keysJson, string language)
        {
            if (string.IsNullOrWhiteSpace(keysJson)) return new Dictionary<string, string>();

            var byLanguage = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                keysJson, Json.DocumentOptions);

            return byLanguage is not null && byLanguage.TryGetValue(language, out var mine)
                ? mine
                : new Dictionary<string, string>();
        }

        /// <summary>
        /// The full language-qualified <c>--keys</c> map, unlike <see cref="Parse"/> which slices
        /// out one language. S4 interop's read phase needs every language's reported
        /// <c>shared_article</c> key, not just this driver's own.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseAll(string? keysJson)
        {
            if (string.IsNullOrWhiteSpace(keysJson))
                return new Dictionary<string, IReadOnlyDictionary<string, string>>();

            var byLanguage = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                keysJson, Json.DocumentOptions);

            return byLanguage is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string>>()
                : byLanguage.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, string>)kv.Value,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    internal sealed class Args
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public static Args Parse(string[] argv)
        {
            var parsed = new Args();
            for (var i = 0; i < argv.Length; i++)
            {
                var flag = argv[i];
                if (!flag.StartsWith("--", StringComparison.Ordinal)) continue;
                // The next argument is the value whatever it looks like: the harness always emits
                // `--flag <value>` pairs (empty string included), and legitimate values — a base64
                // token, a JSON blob — can begin with "--". Treating a leading "--" as "no value"
                // would silently drop them.
                var value = i + 1 < argv.Length ? argv[++i] : string.Empty;
                parsed._values[flag] = value;
            }
            return parsed;
        }

        public string Require(string flag) =>
            _values.TryGetValue(flag, out var value) && value.Length > 0
                ? value
                : throw new ArgumentException($"missing required flag {flag}");

        public string? Optional(string flag) =>
            _values.TryGetValue(flag, out var value) && value.Length > 0 ? value : null;
    }
}
