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
                [RelationKind.ManyToOne, RelationKind.ManyToMany, RelationKind.OneToOne]);
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
                RequireStepOk(state, document, step, Requirements.LifeMappedCrudReachable);
            // `entity` on a write step is deliberately never read: only the .NET driver returns
            // the server's entity there, while Python/TypeScript/Go report the locally
            // constructed pre-call object and Java reports null. Comparing it would be comparing
            // a driver's own input against the server's state.

            // IVC-LIFE-002: the key reported for "article" must be a server-assigned UUIDv7, not
            // a client-chosen one. `ObjectPersistenceGrpcService.Post` mints a UUIDv7
            // unconditionally and discards whatever key the driver sent
            // (2026-08-10-server-generated-ids-and-mapped-crud-parity-design.md), so the version
            // nibble — hex digit 13 of the unformatted key — must read '7'.
            var articleKey = KeyOf(language, "article");
            state.Assertions.Add(Assertion.From(
                "write_article: create returned a server-assigned UUIDv7 key",
                Verifier.IsUuidV7(articleKey),
                articleKey is null ? "no key reported" : $"key={articleKey}",
                Requirements.LifeCreateReturnsServerAssignedKey));

            // IVC-LIFE-002's second clause — "never a client-supplied one" — has no driver-
            // supplied candidate key available orchestrator-side to diff against: all five
            // drivers' write steps never transmit a client-chosen key on a mapped create (see
            // Requirements.LifeCreateReturnsServerAssignedKey's doc comment for the source
            // citations). Guid.Empty ("00000000-0000-0000-0000-000000000000") is nonetheless a
            // real, non-fabricated candidate to rule out: it is the literal wire value .NET's
            // create payload carries for an unset Id property (StructConverter.ToStruct
            // serializes every declared property that isn't a nav property, and write_article
            // never sets DotNetArticle.Id), and it is the sentinel an unset/absent identifier
            // deserializes to in every other language's typed model. A server that regressed to
            // echoing back whatever identity value it received — rather than unconditionally
            // minting a fresh one — would surface here as the returned key equalling this
            // specific sentinel, independent of the UUIDv7 check above (which a coincidentally
            // v7-shaped echoed value would still pass).
            state.Assertions.Add(Assertion.From(
                "write_article: create returned key is not the empty-key placeholder",
                !Verifier.IsEmptyKeyPlaceholder(articleKey),
                articleKey is null ? "no key reported" : $"key={articleKey}",
                Requirements.LifeCreateReturnsServerAssignedKey));
        }

        // ── read (the driver's independent leg) ──────────────────────────────────────────────
        var driverReads = new Dictionary<string, (JsonElement? Article, JsonElement? Author)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (language, document) in await RunPhaseAsync(Phase.Read, states, context, ct))
        {
            var state = states[language];
            var article = RequireStepOk(state, document, "get", Requirements.LifeMappedCrudReachable);
            var author = RequireStepOk(state, document, "get_author", Requirements.LifeMappedCrudReachable);
            driverReads[language] = (article?.Entity, author?.Entity);

            JudgeDriverDepthRead(state, document);
        }

        // ── the orchestrator's own two legs, and the three-way comparison ────────────────────
        var preUpdateTitles = new Dictionary<string, ObservedTitle>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in ScenarioCells.Alive(states))
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
            RequireStepOk(states[language], document, "update", Requirements.LifeMappedCrudReachable);

        foreach (var language in ScenarioCells.Alive(states))
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
                $"before='{before.Value ?? "<none>"}' after='{afterTitle ?? "<none>"}'",
                Requirements.LifeUpdateReflectedInRead));

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
            RequireStepOk(state, document, "delete", Requirements.LifeMappedCrudReachable);

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

        foreach (var language in ScenarioCells.Alive(states))
        {
            var state = states[language];
            if (state.Article is null) continue;
            var key = KeyOf(language, "article");
            if (key is null) continue;

            var grpc = await MappingGetAsync(state.Article.Descriptor.TypeName, key, 0, actingToken, ct);
            state.Assertions.Add(Assertion.From(
                "delete: the orchestrator's gRPC read no longer finds the row",
                grpc is null,
                grpc is null ? "not found" : grpc.Value.GetRawText(),
                Requirements.LifeDeleteRemovesRow));

            var row = await FetchRowAsync(state, state.Article, key, ct);
            state.Assertions.Add(Assertion.From(
                "delete: the Postgres row is gone",
                row is null,
                row is null ? "no row" : $"row still present with {row.Count} columns",
                Requirements.LifeDeleteRemovesRow));
        }

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
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
        var alive = ScenarioCells.Alive(states).ToList();
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
                    state.Terminal = ReportCell.Skip(outcome.Language, Name, skipped.Reason, state.Assertions);
                    break;
                case DriverPhaseOutcome.Broken broken:
                    state.Terminal = ReportCell.Fail(outcome.Language, Name,
                        $"driver broke during the {PhaseNames.ToToken(phase)} phase " +
                        $"(exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}", state.Assertions);
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
                $"'{language}' is not a recognized conformance driver language", states[language].Assertions);
        }

        return documents;
    }

    private static StepResult? RequireStepOk(
        LanguageState state, PhaseDocument document, string stepName, string? requirementId = null)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == stepName);
        if (step is null)
        {
            state.Assertions.Add(Assertion.Fail(
                $"step '{stepName}'", "the driver reported no such step", requirementId));
            return null;
        }

        state.Assertions.Add(Assertion.From(
            $"step '{stepName}' succeeded", step.Ok, step.Error ?? "ok", requirementId));
        return step.Ok ? step : null;
    }

    /// <summary>
    /// IVC-LIFE-006 (reachability, supersedes retired IVC-REL-009) and IVC-LIFE-008 (hydration) —
    /// split from the retired IVC-LIFE-005: each driver performs its OWN depth-1 read through its
    /// own client library — a separate step, <c>get_depth1</c> — and the step succeeding discharges
    /// IVC-LIFE-006, while the returned entity actually carrying a hydrated relation discharges
    /// IVC-LIFE-008. This is what makes reachability and hydration independently gradable: the
    /// orchestrator's own <c>MappingGet(depth: 1)</c> proves only that the SERVER hydrates.
    ///
    /// <para>Internal and extracted from <c>RunAsync</c>'s read loop for the same reason as
    /// <see cref="TakeDescriptor"/>: these two calls are the SOLE citation sites for
    /// <see cref="Requirements.LifeDepthResolvedReadReachable"/> and
    /// <see cref="Requirements.LifeDepthResolvedReadHydrated"/> anywhere in the orchestrator.
    /// Delete either and its const stays cited inside <c>Verifier.cs</c>, so the coverage gate's
    /// Check2 — which reads SOURCE TEXT, not the call graph — stays green while the requirement
    /// grades nothing. <c>CrudRoundtripScenarioTests</c> is what fails instead.</para>
    ///
    /// <para><b>The residual this does NOT close (Ruling 38).</b> What the test grades is THIS
    /// METHOD; the line in <c>RunAsync</c> that calls it is not graded. Deleting
    /// <c>JudgeDriverDepthRead(...)</c> from <c>RunAsync</c> still passes <c>dotnet test</c>
    /// (mutant N5; survived 439/439 when first measured and RE-MEASURED at 448/448, exit 0, in the
    /// final fix wave — the figure is dated because the suite grows, but the SURVIVAL is the
    /// claim). Bounding it: both assertions here carry requirement IDs
    /// (IVC-LIFE-006 and IVC-LIFE-008), so a full-matrix live run's <c>UntouchedRequirementIds</c>
    /// exit code catches the deletion — the cost is a CI-to-live delay, not a silent hole. The
    /// proper fix is making <c>DriverRunner</c> substitutable so a test can drive <c>RunAsync</c>
    /// and pin every call site at once; that is a design change across ten scenarios and is a
    /// DEFERRED follow-up, not this plan's work.</para>
    /// </summary>
    internal static void JudgeDriverDepthRead(LanguageState state, PhaseDocument document)
    {
        var depth1Step = document.Steps.FirstOrDefault(s => s.Name == "get_depth1");
        state.Assertions.Add(Verifier.VerifyDepthResolvedReadReachable(depth1Step));
        var depth1 = depth1Step is { Ok: true } ? depth1Step : null;
        if (state.Article is not null)
        {
            state.Assertions.Add(Verifier.VerifyDepthCapability(
                "article", state.Article.Descriptor, depth1?.Entity));
        }
    }

    /// <summary>
    /// Internal, not private, so <c>CrudRoundtripScenarioTests</c> can prove the
    /// <c>Verifier.VerifyRegistration</c> call BELOW actually reaches a cell. That call is the sole
    /// citation site for IVC-DECL-001/003/006 and IVC-REL-001/002/003/004/010: deleting it leaves
    /// every one of those consts still cited in <c>Verifier.cs</c> source, so the coverage gate's
    /// Check2 — which reads SOURCE TEXT, not the call graph — stays green while seven requirements
    /// silently stop grading anything. That is exactly the hole MU-R4 found next door in
    /// <c>TenantRejectedScenario</c>.
    /// </summary>
    internal static CapturedDescriptor? TakeDescriptor(
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

    /// <summary>
    /// Internal for the same reason as <see cref="TakeDescriptor"/>, and for TWO seams inside, not
    /// one. The <c>Verifier.VerifyRelationHydrated</c> loop is the ONLY citation site for
    /// IVC-REL-006 and IVC-REL-008 anywhere in the orchestrator. The
    /// <c>Verifier.VerifyThreeWay</c> loop above it is the only citation site for IVC-DECL-004 —
    /// all three of that requirement's citations sit inside that one helper — and one of
    /// IVC-REL-010's two. Dropping either loop removes those requirements from every cell without
    /// reddening the gate, because Check2 reads source text and the consts stay cited in
    /// <c>Verifier.cs</c>. Each is pinned by its own named test in
    /// <c>CrudRoundtripScenarioTests</c>.
    /// </summary>
    internal async Task<ObservedTitle> CompareAsync(
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

        // IVC-REL-007: a many-to-many foreign key must be sent as a JSON array, never a
        // delimited string, in the driver's own depth-0 read — the leg the driver constructed
        // (or received back) from its own write, before this scenario ever touches gRPC/Postgres.
        foreach (var relation in captured.Descriptor.Relations.Where(r => r.Kind == RelationKind.ManyToMany))
        {
            var element = Verifier.FindProperty(driverEntity, relation.ForeignKey);
            state.Assertions.Add(Assertion.From(
                $"{label}.{relation.ForeignKey}: multi-valued foreign key is sent as a JSON array, not a delimited string",
                element is { ValueKind: JsonValueKind.Array },
                element is null
                    ? "(absent)"
                    : $"kind={element.Value.ValueKind} raw={element.Value.GetRawText()}",
                Requirements.RelMultiValuedForeignKeyAsList));
        }

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

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed record CapturedDescriptor(TypeDescriptor Descriptor, JsonElement Json);

    internal readonly record struct ObservedTitle(string? Value);

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];
        public CapturedDescriptor? Article { get; set; }
        public CapturedDescriptor? Author { get; set; }

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
