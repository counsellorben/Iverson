using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Iverson.Client.Conformance.Driver;
using Iverson.Client.Conformance.Driver.Models;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Microsoft.Extensions.Logging.Abstractions;

// ── The .NET conformance driver ────────────────────────────────────────────────────────────────
// Reports; never asserts. Every judgement belongs to the orchestrator's Verifier. A step that
// throws becomes ok:false with an error message and the process still exits 0 — a non-zero exit
// means the driver itself broke (bad flags, unsupported scenario, unwritable --out).

const string Language = "dotnet";
const string Scenario = "crud-roundtrip";

var args_ = Args.Parse(args);

var scenario = args_.Require("--scenario");
if (!string.Equals(scenario, Scenario, StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"unsupported scenario '{scenario}'; this driver implements only '{Scenario}'");
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
    args_.Optional("--acting-token") ?? string.Empty);

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

EntityCoordinator<T> Coordinator<T>() where T : class =>
    new(registry, assembler, mapping, persistence, retrieval, search,
        NullLogger<EntityCoordinator<T>>.Instance);

// Keys are driver-chosen UUIDs derived from the run id, so two runs never collide and every
// phase after `write` can re-derive nothing — it reads them back from --keys.
var priorKeys = Keys.Parse(args_.Optional("--keys"), Language);
Guid KeyFor(string logicalName) =>
    priorKeys.TryGetValue(logicalName, out var existing) && Guid.TryParse(existing, out var parsed)
        ? parsed
        : Keys.Derive(idPrefix, logicalName);

var steps = new List<StepResult>();

switch (phase)
{
    case "register":
    {
        // The captured descriptor is attached whether or not the RPC succeeded: what the client
        // built and sent is the observation, and a server-side rejection is a separate fact.
        await Step("register",
            async result =>
            {
                var registrar = new SchemaRegistrar(registry, mappingForRegistration, NullLogger<SchemaRegistrar>.Instance);
                await registrar.RegisterAllAsync();
                return result;
            },
            result => result with
            {
                TypeDescriptor = Json.Element(capture.Select(typeHint, nameof(DotNetArticle))),
            });
        break;
    }

    case "write":
    {
        await Step("write", async result =>
        {
            var authorKey = Keys.Derive(idPrefix, "author");
            var tagKey = Keys.Derive(idPrefix, "tag");
            var articleKey = Keys.Derive(idPrefix, "article");

            await Coordinator<DotNetAuthor>().PostMappedAsync(new DotNetAuthor
            {
                Id = authorKey,
                TenantId = tenant,
                OwnerId = ownerId,
                Name = $"author-{idPrefix}",
            });

            await Coordinator<DotNetTag>().PostMappedAsync(new DotNetTag
            {
                Id = tagKey,
                TenantId = tenant,
                OwnerId = ownerId,
                Label = $"tag-{idPrefix}",
            });

            await Coordinator<DotNetArticle>().PostMappedAsync(new DotNetArticle
            {
                Id = articleKey,
                TenantId = tenant,
                OwnerId = ownerId,
                Title = $"title-{idPrefix}",
                DotNetAuthorId = authorKey,
                DotNetTagIds = [tagKey],
            });

            return result with
            {
                Keys = new Dictionary<string, string>
                {
                    ["author"] = authorKey.ToString(),
                    ["tag"] = tagKey.ToString(),
                    ["article"] = articleKey.ToString(),
                },
            };
        });
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
            });

            return result with { Entity = Json.Element(updated) };
        });
        break;
    }

    case "delete":
    {
        await Step("delete", async result =>
        {
            var coordinator = Coordinator<DotNetArticle>();
            var articleKey = KeyFor("article").ToString();

            await coordinator.DeleteAsync(articleKey);

            // Read back after the delete. The entity is present only if the row survived —
            // whether that is a defect is the Verifier's call, not this driver's.
            var afterDelete = await coordinator.GetMappedAsync(articleKey, depth: 0);
            return result with { Entity = Json.Element(afterDelete) };
        });
        break;
    }

    default:
        await Console.Error.WriteLineAsync($"unknown phase '{phase}'");
        return 2;
}

var document = new PhaseDocument(Language, phase, steps);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(document, Json.DocumentOptions));
return 0;

// A throwing step is data, not a driver failure: it becomes ok:false with an error and the
// process still exits 0. `always` enriches the result on both paths, for observations (like the
// captured descriptor) that are worth reporting even when the call itself failed.
async Task Step(
    string name,
    Func<StepResult, Task<StepResult>> body,
    Func<StepResult, StepResult>? always = null)
{
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

    steps.Add(always is null ? outcome : always(outcome));
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
                var value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? argv[++i]
                    : string.Empty;
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
