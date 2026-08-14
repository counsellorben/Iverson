using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S1 <c>crud-roundtrip</c>: drives every client library through register → write → read →
/// update → delete and judges what came back.
///
/// The phase order is load-bearing rather than cosmetic. Each driver EXITS between phases, so the
/// orchestrator's own reads in step 4 run against rows a separate, already-finished process wrote
/// — not against anything still held in a client's memory:
/// <code>
/// driver register  →  orchestrator re-register with row permissions
/// driver write     →  driver read (article and author, depth 0)
/// orchestrator     →  MappingGet(article, depth 1)   FK survives hydration, nav property beside it
/// orchestrator     →  MappingGet(author,  depth 1)   one_to_many resolves — the reverse nav is
///                                                      declared explicitly on the author descriptor
/// driver update    →  driver delete
/// </code>
/// </summary>
public sealed class CrudRoundtripScenario(
    DriverRunner runner,
    ObjectMappingService.ObjectMappingServiceClient mapping,
    Reregistrar reregistrar,
    PostgresProbe probe,
    Action<string>? log = null)
{
    public const string Name = "crud-roundtrip";

    private static readonly JsonFormatter StructFormatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        DriverContext context,
        string actingToken,
        CancellationToken ct = default)
    {
        var states = languages.ToDictionary(
            l => l, _ => new LanguageState(), StringComparer.OrdinalIgnoreCase);

        // ── register ─────────────────────────────────────────────────────────────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Register, states, context, ct))
        {
            var state = states[language];

            // Each type's expected relation shape comes from the caller, which knows the label.
            // EntityRelationResolver (Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs)
            // only ever iterates relations on the entity's OWN stored schema — there is no
            // reverse-derivation on the server. So the author's one-to-many hydrates only because
            // every driver declares the reverse navigation explicitly on its own descriptor (see
            // DotNetAuthor.cs, JavaAuthor.java, and the Python/TypeScript/Go models added
            // alongside this comment); tag genuinely declares no relations at all.
            state.Article = TakeDescriptor(state, document, "register", "article",
                [RelationKind.ManyToOne, RelationKind.ManyToMany]);
            state.Author = TakeDescriptor(state, document, "register_author", "author",
                [RelationKind.OneToMany]);
            var tag = TakeDescriptor(state, document, "register_tag", "tag", []);

            // The orchestrator re-registers each reported descriptor with row permissions added
            // and NOTHING else changed. A type whose stored schema carries no authorization block
            // is writable by nobody, so all three must be re-registered or the write phase is
            // denied outright.
            foreach (var (label, descriptor) in
                     new[] { ("author", state.Author), ("tag", tag), ("article", state.Article) })
            {
                // A missing descriptor is reported, never skipped in silence. Its own step-failure
                // assertion says the register step failed; it does NOT say that the consequence is
                // an un-re-registered type whose every later write dies as PermissionDenied "Not
                // authorized to create this entity" — an authorization error that names nothing
                // about registration. Without this assertion the two are four phases apart in the
                // report with nothing connecting them, which is exactly how Go's write failures
                // went un-root-caused.
                if (descriptor is null)
                {
                    state.Assertions.Add(Assertion.Fail(
                        $"{label}: re-registered with row permissions",
                        "skipped — no descriptor was reported for this type, so its stored schema " +
                        "keeps no authorization block and every write against it will be denied"));
                    continue;
                }

                try
                {
                    await reregistrar.ReregisterAsync(descriptor.Json, actingToken, ct: ct);
                }
                catch (Exception ex)
                {
                    state.Assertions.Add(Assertion.Fail(
                        $"reregister {descriptor.Descriptor.TypeName}", Describe(ex)));
                }
            }
        }

        // ── write ────────────────────────────────────────────────────────────────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Write, states, context, ct))
        {
            var state = states[language];
            foreach (var step in new[] { "write_author", "write_tag", "write_article" })
                RequireStepOk(state, document, step);
            // `entity` on a write step is deliberately never read: only the .NET driver returns
            // the server's entity there, while Python/TypeScript/Go report the locally
            // constructed pre-call object and Java reports null. Comparing it would be comparing
            // a driver's own input against the server's state.
        }

        // ── read (the driver's independent leg) ──────────────────────────────────────────────
        var driverReads = new Dictionary<string, (JsonElement? Article, JsonElement? Author)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (language, document) in await RunPhaseAsync(Phase.Read, states, context, ct))
        {
            var state = states[language];
            var article = RequireStepOk(state, document, "get");
            var author = RequireStepOk(state, document, "get_author");
            driverReads[language] = (article?.Entity, author?.Entity);
        }

        // ── the orchestrator's own two legs, and the three-way comparison ────────────────────
        var preUpdateTitles = new Dictionary<string, ObservedTitle>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in Alive(states))
        {
            var state = states[language];
            if (state.Article is null || state.Author is null)
                continue;

            driverReads.TryGetValue(language, out var read);

            preUpdateTitles[language] = await CompareAsync(
                state, language, state.Article, "article", read.Article, actingToken, ct);

            await CompareAsync(
                state, language, state.Author, "author", read.Author, actingToken, ct);
        }

        // ── update ───────────────────────────────────────────────────────────────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Update, states, context, ct))
            RequireStepOk(states[language], document, "update");

        foreach (var language in Alive(states))
        {
            var state = states[language];
            if (state.Article is null || !preUpdateTitles.TryGetValue(language, out var before))
                continue;

            var key = KeyOf(language, "article");
            if (key is null) continue;

            var after = await MappingGetAsync(state.Article.Descriptor.TypeName, key, 1, actingToken, ct);
            var afterTitle = TitleOf(after);
            var row = await FetchRowAsync(state, state.Article, key, ct);
            var rowTitle = RowTitleOf(row);

            // What the update changed is asserted by comparing the server's own before and after
            // — never against a value the driver told us it sent, which would certify the
            // driver's input rather than the server's state.
            state.Assertions.Add(Assertion.From(
                "article.Title: the update changed the server's stored value",
                before.Value is not null && afterTitle is not null && before.Value != afterTitle,
                $"before='{before.Value ?? "<none>"}' after='{afterTitle ?? "<none>"}'"));

            state.Assertions.Add(Assertion.From(
                "article.Title: the Postgres row agrees with the gRPC read after the update",
                afterTitle is not null && afterTitle == rowTitle,
                $"grpc='{afterTitle ?? "<none>"}' postgres='{rowTitle ?? "<none>"}'"));

            // The relation foreign keys must survive an update that re-sent them.
            foreach (var valueName in Verifier.ComparedValueNames(state.Article.Descriptor))
            {
                var grpc = Verifier.FromJson(after, valueName);
                var postgres = Verifier.FromRow(row, valueName);
                state.Assertions.Add(Assertion.From(
                    $"article.{valueName}: survives the update (gRPC vs Postgres)",
                    grpc.Uuids is { Count: > 0 } && grpc.Matches(postgres),
                    $"grpc={grpc} postgres={postgres}"));
            }
        }

        // ── delete ───────────────────────────────────────────────────────────────────────────
        foreach (var (language, document) in await RunPhaseAsync(Phase.Delete, states, context, ct))
        {
            var state = states[language];
            RequireStepOk(state, document, "delete");

            // get_after_delete is deliberately NOT held to step.Ok. The five clients disagree on
            // how a deleted row is signalled — .NET surfaces the server's "not found" as a
            // logged client error, Go's coordinator turns it into a Go error (its driver
            // re-flattens that), and the others hand back a null entity on an otherwise clean
            // call. None of those is more correct than the others, and requiring one shape here
            // would fail conforming clients for a client-API difference. What IS judged is the
            // observation every one of them can make: no entity came back. Whether the row is
            // actually gone (rather than merely unreadable) is settled below by the
            // orchestrator's own gRPC and Postgres legs, which do not depend on any client.
            var afterDelete = document.Steps.FirstOrDefault(s => s.Name == "get_after_delete");
            if (afterDelete is null)
            {
                state.Assertions.Add(Assertion.Fail("step 'get_after_delete'", "the driver reported no such step"));
            }
            else
            {
                // Absent and null are the same answer: Java's Gson omits null fields where the
                // other four emit them explicitly.
                var entity = afterDelete.Entity;
                state.Assertions.Add(Assertion.From(
                    "get_after_delete: the client read back nothing",
                    entity is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined },
                    $"entity={(entity is null ? "(absent)" : entity.Value.GetRawText())}" +
                    $" clientSignal={(afterDelete.Ok ? "ok" : afterDelete.Error)}"));
            }
        }

        foreach (var language in Alive(states))
        {
            var state = states[language];
            if (state.Article is null) continue;
            var key = KeyOf(language, "article");
            if (key is null) continue;

            var grpc = await MappingGetAsync(state.Article.Descriptor.TypeName, key, 0, actingToken, ct);
            state.Assertions.Add(Assertion.From(
                "delete: the orchestrator's gRPC read no longer finds the row",
                grpc is null,
                grpc is null ? "not found" : grpc.Value.GetRawText()));

            var row = await FetchRowAsync(state, state.Article, key, ct);
            state.Assertions.Add(Assertion.From(
                "delete: the Postgres row is gone",
                row is null,
                row is null ? "no row" : $"row still present with {row.Count} columns"));
        }

        return states.Select(kv => Cell(kv.Key, kv.Value)).ToList();
    }

    // ── phase plumbing ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one phase for every language still alive and returns the parsed documents. A skipped
    /// or broken driver terminates that language's row here — with a reason, never silently —
    /// and it is excluded from every later phase so the report carries the first real cause
    /// rather than a cascade of downstream noise.
    /// </summary>
    private async Task<IReadOnlyList<(string Language, PhaseDocument Document)>> RunPhaseAsync(
        Phase phase, Dictionary<string, LanguageState> states, DriverContext context, CancellationToken ct)
    {
        var alive = Alive(states).ToList();
        if (alive.Count == 0)
            return [];

        log?.Invoke($"  phase {PhaseNames.ToToken(phase)}: {string.Join(", ", alive)}");

        var documents = new List<(string, PhaseDocument)>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in await runner.RunPhaseAsync(phase, alive, context, ct))
        {
            reported.Add(outcome.Language);
            var state = states[outcome.Language];
            switch (outcome)
            {
                case DriverPhaseOutcome.Success success:
                    documents.Add((outcome.Language, success.Document));
                    break;
                case DriverPhaseOutcome.Skipped skipped:
                    state.Terminal = ReportCell.Skip(outcome.Language, Name, skipped.Reason);
                    break;
                case DriverPhaseOutcome.Broken broken:
                    state.Terminal = ReportCell.Fail(outcome.Language, Name,
                        $"driver broke during the {PhaseNames.ToToken(phase)} phase " +
                        $"(exit {broken.ExitCode}): {Truncate(broken.Stderr)}");
                    break;
            }
        }

        // DriverRunner only iterates its own five known driver specs, so a requested language it
        // does not recognize produces no outcome at all — no terminal state, no assertions — and
        // would otherwise fall through every later phase and reach Cell() with zero failed
        // assertions, i.e. a green row for a typo like "typescrpt". Any language left alive with
        // no outcome for this phase is exactly that case.
        foreach (var language in alive.Where(l => !reported.Contains(l)))
        {
            states[language].Terminal = ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language");
        }

        return documents;
    }

    private static IEnumerable<string> Alive(Dictionary<string, LanguageState> states) =>
        states.Where(kv => kv.Value.Terminal is null).Select(kv => kv.Key).ToList();

    private static StepResult? RequireStepOk(LanguageState state, PhaseDocument document, string stepName)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == stepName);
        if (step is null)
        {
            state.Assertions.Add(Assertion.Fail($"step '{stepName}'", "the driver reported no such step"));
            return null;
        }

        state.Assertions.Add(Assertion.From(
            $"step '{stepName}' succeeded", step.Ok, step.Error ?? "ok"));
        return step.Ok ? step : null;
    }

    private static CapturedDescriptor? TakeDescriptor(
        LanguageState state, PhaseDocument document, string stepName, string label,
        IReadOnlyCollection<RelationKind> expectedRelationKinds)
    {
        var step = RequireStepOk(state, document, stepName);
        if (step?.TypeDescriptor is not { } json)
        {
            if (step is not null)
                state.Assertions.Add(Assertion.Fail($"{label}: descriptor reported", "typeDescriptor was null"));
            return null;
        }

        TypeDescriptor descriptor;
        try
        {
            descriptor = Verifier.ParseDescriptor(json);
        }
        catch (Exception ex)
        {
            state.Assertions.Add(Assertion.Fail($"{label}: descriptor parses", Describe(ex)));
            return null;
        }

        state.Assertions.AddRange(Verifier.VerifyRegistration(label, descriptor, expectedRelationKinds));
        return new CapturedDescriptor(descriptor, json);
    }

    // ── the three-way comparison ─────────────────────────────────────────────────────────────

    private async Task<ObservedTitle> CompareAsync(
        LanguageState state,
        string language,
        CapturedDescriptor captured,
        string label,
        JsonElement? driverEntity,
        string actingToken,
        CancellationToken ct)
    {
        var key = KeyOf(language, label);
        if (key is null)
        {
            state.Assertions.Add(Assertion.Fail($"{label}: key reported", "the write phase reported no key"));
            return new ObservedTitle(null);
        }

        // depth 1: the foreign key must survive hydration with the nav property beside it, and
        // the author's one_to_many must resolve — see Verifier.VerifyRelationHydrated below for
        // what actually proves that, rather than merely asserting the pre-hydration scalars.
        var grpc = await MappingGetAsync(captured.Descriptor.TypeName, key, 1, actingToken, ct);
        state.Assertions.Add(Assertion.From(
            $"{label}: the orchestrator's gRPC read found the row", grpc is not null, $"key={key}"));

        var row = await FetchRowAsync(state, captured, key, ct);
        state.Assertions.Add(Assertion.From(
            $"{label}: the Postgres row exists", row is not null,
            $"table={PostgresProbe.TableName(captured.Descriptor.TypeName)} key={key}"));

        var keyPropertyName = captured.Descriptor.Properties.FirstOrDefault(p => p.IsKey)?.Name;

        foreach (var valueName in Verifier.ComparedValueNames(captured.Descriptor))
        {
            var isKey = keyPropertyName is not null &&
                        string.Equals(Verifier.Normalize(keyPropertyName), Verifier.Normalize(valueName),
                            StringComparison.Ordinal);

            state.Assertions.AddRange(Verifier.VerifyThreeWay(label, valueName, new ThreeLegs(
                Verifier.FromJson(driverEntity, valueName),
                Verifier.FromJson(grpc, valueName),
                Verifier.FromRow(row, valueName)), isKey));
        }

        // The depth-1 hydration itself: every relation the descriptor declares must actually have
        // hydrated in the gRPC read above, not merely have its pre-hydration scalar present.
        foreach (var relation in captured.Descriptor.Relations)
            state.Assertions.AddRange(Verifier.VerifyRelationHydrated(label, relation, grpc));

        return new ObservedTitle(TitleOf(grpc));
    }

    private async Task<IReadOnlyDictionary<string, object?>?> FetchRowAsync(
        LanguageState state, CapturedDescriptor captured, string key, CancellationToken ct)
    {
        var keyColumn = captured.Descriptor.Properties.FirstOrDefault(p => p.IsKey)?.Name;
        if (keyColumn is null)
            return null;

        try
        {
            return await probe.FetchRowAsync(captured.Descriptor.TypeName, keyColumn, key, ct);
        }
        catch (Exception ex)
        {
            state.Assertions.Add(Assertion.Fail(
                $"postgres probe {PostgresProbe.TableName(captured.Descriptor.TypeName)}", Describe(ex)));
            return null;
        }
    }

    private string? KeyOf(string language, string logicalName) =>
        runner.KeysByLanguage.TryGetValue(language, out var keys) &&
        keys.TryGetValue(logicalName, out var key) && key.Length > 0
            ? key
            : null;

    /// <summary>Null when the row is absent or the read was refused — both are "not there".</summary>
    private async Task<JsonElement?> MappingGetAsync(
        string typeName, string key, int depth, string actingToken, CancellationToken ct)
    {
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        MappingResponse response;
        try
        {
            response = await mapping.GetAsync(
                new MappingGetRequest { TypeName = typeName, Key = key, Depth = depth },
                headers, cancellationToken: ct);
        }
        catch (RpcException)
        {
            return null;
        }

        if (!response.Success || response.Data is null)
            return null;

        using var parsed = JsonDocument.Parse(StructFormatter.Format(response.Data));
        return parsed.RootElement.Clone();
    }

    private static string? TitleOf(JsonElement? entity)
    {
        if (entity is not { ValueKind: JsonValueKind.Object } obj)
            return null;
        foreach (var property in obj.EnumerateObject())
            if (Verifier.Normalize(property.Name) == "title" && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static string? RowTitleOf(IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null) return null;
        foreach (var (column, value) in row)
            if (Verifier.Normalize(column) == "title")
                return value as string;
        return null;
    }

    private static ReportCell Cell(string language, LanguageState state)
    {
        if (state.Terminal is not null)
            return state.Terminal;

        var failures = state.Assertions.Where(a => !a.Passed).ToList();
        return failures.Count == 0
            ? ReportCell.Ok(language, Name)
            : ReportCell.Fail(language, Name, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")));
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string Truncate(string text) =>
        text.Length <= 2000 ? text.Trim() : text[^2000..].Trim();

    private sealed record CapturedDescriptor(TypeDescriptor Descriptor, JsonElement Json);

    private readonly record struct ObservedTitle(string? Value);

    private sealed class LanguageState
    {
        public List<Assertion> Assertions { get; } = [];
        public CapturedDescriptor? Article { get; set; }
        public CapturedDescriptor? Author { get; set; }

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
