using System.Text.Json;

namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// S8 <c>identity</c>: proves each client library carries BOTH identities on one call — the
/// service identity in <c>authorization</c> and the acting-user identity in
/// <c>x-acting-user-authorization</c> — that the server resolves a row's tenant and owner from the
/// acting user rather than from the payload, and that an acting user belonging to a different
/// tenant is denied a write to that row.
///
/// The shape follows S6 query's and S7 vector-search's, for the same reasons: the subject is one
/// shared type (<c>IdentityDoc</c>) that every language writes into and every language then reads
/// back, so disagreement between two client libraries is observable. Only the .NET driver ever runs
/// the register phase — <c>SchemaRegistry.RegisterAsync</c> replaces the stored descriptor
/// wholesale, so five registrations of the same type name would leave four silent overwrites — and
/// the orchestrator re-registers the reported descriptor once with an authorization block before
/// any write, without which every seeded write is denied for a reason that has nothing to do with
/// identity and the two legs become indistinguishable
/// (<c>RowFieldAuthorizationEvaluator.cs:10-12</c>).
///
/// <para><b>The positive leg.</b> Each driver creates one row through its own mapped write path
/// while carrying its own acting-user token, then reads that row back through its own mapped read
/// path. What it reports is a deliberately minimal, cross-language-identical projection —
/// <c>{"key":"…","tenant":"…","owner":"…"}</c> — read off the entity its client library returned.
/// A driver-native serialization would differ per language (Python reports snake_case members,
/// Java omits nulls), which would make a naming difference render as a conformance failure.</para>
///
/// <para><b>The owner assertion grades an echo, not a derivation.</b> Unlike the tenant column,
/// the owner column is NOT force-set by the server for this run's acting user: it holds
/// <c>iverson-loadtest-bypass</c>, <see cref="Reregistrar"/> grants that role <c>CanWriteAll</c>,
/// and <c>RowFieldAuthorizationEvaluator</c> therefore reports <c>ownershipRequired: false</c>, so
/// <c>EnforceWriteAuthorization</c> leaves whatever owner the payload carried. The driver stamps
/// that owner from <c>--owner-id</c>, which is <c>TokenBroker.GetOwnerIdAsync()</c> — the same
/// value the assertion compares against. Verified live: a driver stamping a made-up owner reads
/// that made-up owner back, while the tenant column in the same run is force-set correctly. The
/// assertion is therefore a round-trip claim (the row this acting user wrote comes back carrying
/// the owner it propagated), NOT a claim that the server derived the owner from the token. It is
/// kept because a client that mangles or drops the owner on the write path still fails it, and the
/// tenant assertion beside it is what defends the derivation claim. Observing owner DERIVATION
/// would need an acting user without a bypass role, which is a stack-provisioning change; it is
/// recorded as a Deferred area in the IDN coverage ledger.</para>
///
/// <para><b>The tenant every driver sends is deliberately wrong.</b> Every driver stamps
/// <see cref="WrongTenantValue"/> — never the acting user's tenant — on the row it creates. The
/// server force-sets the tenant column from the acting-user token's <c>tenant_id</c> claim on a
/// create (<c>AuthorizationFieldMasking.EnforceWriteAuthorization</c>'s no-existing-row branch), so
/// the read-back must observe the ACTING user's tenant. Had the drivers stamped the correct tenant
/// (as every other scenario does), the tenant assertion would agree by construction with what the
/// driver sent and would pass for a client that propagated no acting user at all.</para>
///
/// <para><b>The negative leg.</b> The orchestrator mints a SECOND acting-user token, belonging to a
/// different active tenant (<c>TokenBroker.GetOtherTenantActingTokenAsync</c>), and passes it to
/// every driver as <c>--wrong-acting-token</c>. Each driver attempts a mapped UPDATE of the row it
/// just created, carrying that token in place of its own, and reports the gRPC status code it
/// received. Drivers report; they never judge. The code is reported and compared NUMERICALLY
/// (<see cref="DeniedStatusCode"/>), because the five languages spell the same code five ways
/// (<c>PermissionDenied</c>, <c>PERMISSION_DENIED</c>, <c>7</c>) and a name comparison would report
/// a spelling difference as a conformance failure.</para>
///
/// <para><b>Why the update payload carries the RIGHT tenant.</b> The create stamps
/// <see cref="WrongTenantValue"/>, but the negative leg's update stamps the acting user's own
/// tenant. On an existing row the server rejects a payload tenant that differs from the caller's
/// claim as "Tenant field is immutable" — also <c>PermissionDenied</c>, and fired for ANY caller,
/// including the right one. Verified live: with the wrong tenant in the update payload a driver
/// sending its OWN acting token was still denied (audit reason <c>TenantImmutable</c>) and the
/// cell stayed green. With the acting tenant there, that same driver's write is accepted and the
/// cell goes red, so the only thing left that can deny the write is which end user is calling.</para>
///
/// <para><b>What the status code cannot distinguish.</b> <c>PermissionDenied</c> (7) is the
/// server's answer to several distinct refusals on this path, and it carries the SAME message for
/// all of them — <c>"Not authorized to update this entity."</c>, the one <c>deniedMessage</c>
/// <c>ObjectMappingGrpcService.Update</c> passes into
/// <c>AuthorizationFieldMasking.EnforceWriteAuthorization</c> for every branch — and no trailers.
/// Two consequences, both verified live and neither of them fixable from the client side:
/// <list type="bullet">
/// <item><description><b>A driver that attaches NO acting user at all still goes green.</b>
/// <c>ActingUserInterceptor.ValidateActingUserAsync</c> returns early on an empty header, the
/// acting-user principal is null, <c>RowFieldAuthorizationEvaluator.Evaluate</c> returns
/// <c>Denied</c>, and the same status 7 with the same message comes back. The server's audit log
/// tells the two apart (<c>reason=TenantMismatch</c> versus <c>reason=AccessDenied</c>, with
/// <c>actor=unknown tenant=unknown</c>), but nothing a client can read does — so no assertion here
/// can. This is recorded as a Deferred area in the IDN coverage ledger rather than papered over: a
/// driver self-report ("I attached the header") would be worthless in exactly the case it exists
/// for, since a library that silently DROPPED the header would still have its driver report
/// success.</description></item>
/// <item><description><b>Which tenancy check ran is not isolated either.</b> With the payload
/// tenant set to the caller's own claim, the wrong caller trips the existing row's tenant check;
/// were it set to anything else it would additionally trip the immutability check. Both compare
/// against the CALLER's own <c>tenant_id</c> claim, so the denial stays identity-derived whichever
/// fires — which is why the assertion does not try to tell them apart.</description></item>
/// </list></para>
///
/// <para><b>Why an update, and not a create.</b> A create by the wrong acting user is NOT denied:
/// with no existing row, <c>EnforceWriteAuthorization</c> force-sets tenant and owner from the
/// caller's own claims and lets the write through, into the caller's own tenant. The denial exists
/// only against an EXISTING row whose tenant differs from the caller's — which is what makes the
/// backstop below load-bearing rather than decorative.</para>
///
/// <para><b>Backstop assertion.</b> <see cref="Judge"/>'s "the write phase reported a row key for
/// this language" assertion is this axis's backstop, in the sense
/// <c>docs/standards/iverson-client-standard.md</c>'s REL authoring notes require. Without a
/// seeded row the negative leg's update would take the create branch described above and SUCCEED,
/// and a scenario whose denial never had anything to deny would render green. The backstop fires
/// unconditionally, on every language, before and outside both the read-back and the denial
/// assertions. It carries no requirement ID: no <c>IVC-IDN-*</c> statement owns "this language
/// seeded a row" as such — that is a property of the harness's fixture, not of a client — and it is
/// strictly weaker than <see cref="Requirements.IdnActingUserPropagatedToRow"/> and
/// <see cref="Requirements.IdnTenancyDerivedAndEnforced"/> wherever either can fail.</para>
/// </summary>
public sealed class IdentityScenario(
    DriverRunner runner,
    Reregistrar reregistrar,
    Action<string>? log = null)
{
    public const string Name = "identity";

    /// <summary>The only driver ever asked to run this scenario's register phase.</summary>
    private const string RegisterLanguage = "dotnet";

    /// <summary>The type every language writes into and reads back. Relation-free on purpose.</summary>
    internal const string TypeName = "IdentityDoc";

    /// <summary>The logical key name every driver reports its seeded row under.</summary>
    internal const string RowKeyName = "identity_doc";

    /// <summary>
    /// The tenant value every driver stamps on the row it creates — deliberately NOT the acting
    /// user's tenant. Spelled once here and mirrored in each of the five drivers. It must never
    /// coincide with a real tenant id: the point is that the server ignores it entirely and
    /// force-sets the acting user's own tenant instead.
    /// </summary>
    internal const string WrongTenantValue = "tenant_not_the_acting_user";

    /// <summary>
    /// The numeric gRPC status code the negative leg must produce: <c>PERMISSION_DENIED</c>.
    /// Numeric because the five languages spell the same code five ways.
    /// </summary>
    internal const int DeniedStatusCode = 7;

    internal const string RegisterStepName = "register_identity_doc";
    internal const string WriteStepName = "write_identity_doc";
    internal const string ReadStepName = "read_identity_doc";
    internal const string DeniedStepName = "denied_update_wrong_acting_user";

    public async Task<IReadOnlyList<ReportCell>> RunAsync(
        IReadOnlyCollection<string> languages,
        DriverContext context,
        string actingToken,
        string otherTenant,
        CancellationToken ct = default)
    {
        if (languages.Count == 0)
            return [];

        if (PreconditionFailure(context.Tenant, otherTenant, context.WrongActingToken) is { } precondition)
            return ScenarioCells.FailEveryLanguage(languages, Name, precondition);

        var states = languages.ToDictionary(
            l => l, _ => new LanguageState(), StringComparer.OrdinalIgnoreCase);

        // ── register (dotnet only, register-once — see the class doc comment) ──────────────────
        var registerOutcomes = await runner.RunPhaseAsync(Phase.Register, [RegisterLanguage], context, ct);
        var registerOutcome = registerOutcomes.Count > 0 ? registerOutcomes[0] : null;

        var (descriptorJson, registerFailure) = registerOutcome switch
        {
            DriverPhaseOutcome.Success success => TryCaptureDescriptor(success.Document),
            DriverPhaseOutcome.Skipped skipped => (null, $"'{RegisterLanguage}' driver skipped: {skipped.Reason}"),
            DriverPhaseOutcome.Broken broken => (null,
                $"'{RegisterLanguage}' driver broke during the register phase (exit {broken.ExitCode}): {ScenarioCells.Truncate(broken.Stderr)}"),
            _ => (null, $"'{RegisterLanguage}' produced no register-phase outcome"),
        };

        if (registerFailure is not null)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S8 identity's register phase (run once, by '{RegisterLanguage}') failed: {registerFailure}");
        }

        try
        {
            await reregistrar.ReregisterAsync(descriptorJson!.Value, actingToken, ct: ct);
        }
        catch (Exception ex)
        {
            return ScenarioCells.FailEveryLanguage(languages, Name,
                $"S8 identity's one-time re-registration of '{TypeName}' with row permissions failed: {Describe(ex)}");
        }

        // ── write: every requested language creates one row under its own acting user ──────────
        GradeWrites(states, await RunPhaseAsync(Phase.Write, states, context, ct));

        // ── read: read-back under the right acting user, denied update under the wrong one ─────
        return GradeReads(
            states, await RunPhaseAsync(Phase.Read, states, context, ct), context.Tenant, context.OwnerId);
    }

    /// <summary>
    /// Wires the write phase's documents through <see cref="JudgeWrite"/> into each language's
    /// state. Extracted from <see cref="RunAsync"/> — and internal — for the same reason as
    /// <see cref="GradeReads"/>: dropping the call silently removes assertions from a cell without
    /// reddening anything, so the wiring has to be reachable from a unit test.
    /// </summary>
    internal static void GradeWrites(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> writes)
    {
        foreach (var (language, document) in writes)
            states[language].Assertions.AddRange(JudgeWrite(language, document));
    }

    /// <summary>
    /// Wires the read phase's documents through <see cref="Judge"/> and into cells. Extracted from
    /// <see cref="RunAsync"/> — and internal — because the wiring is exactly as safety-critical as
    /// the judgement and used to be reachable only from a live stack: drop the <see cref="Judge"/>
    /// call below and every IDN assertion silently stops reaching a cell, leaving a fully green
    /// identity row that verified nothing. That mutation must redden a named test.
    /// </summary>
    internal IReadOnlyList<ReportCell> GradeReads(
        Dictionary<string, LanguageState> states,
        IReadOnlyList<(string Language, PhaseDocument Document)> reads,
        string tenant,
        string ownerId)
    {
        foreach (var (language, document) in reads)
        {
            states[language].Assertions.AddRange(Judge(
                language, tenant, ownerId, SeededKey(runner.KeysByLanguage, language), document));
        }

        return states.Select(kv => ScenarioCells.Cell(kv.Key, Name, kv.Value)).ToList();
    }

    /// <summary>
    /// Why this scenario's negative leg is worth running at all, extracted so it is testable
    /// without a live stack. Returns null when the run is usable, or the reason it is not.
    ///
    /// Two things must hold, and neither is observable from any assertion downstream:
    /// <list type="bullet">
    /// <item><description>There must BE a second token. With none, every driver would send its own
    /// acting-user token on the "wrong user" update and the server would ALLOW it — a red cell
    /// whose cause is the harness, reported as five identical client defects.</description></item>
    /// <item><description>The two identities must belong to different tenants. A second acting user
    /// inside the SAME tenant is denied on ownership, or (as a bypass-role member) not denied at
    /// all; either way the cell stops being evidence about tenancy while still rendering as
    /// though it were.</description></item>
    /// </list>
    /// </summary>
    internal static string? PreconditionFailure(string actingTenant, string otherTenant, string wrongActingToken)
    {
        if (string.IsNullOrEmpty(wrongActingToken))
        {
            return "S8 identity has no wrong-acting-user token: without one every driver would send " +
                   "its own acting-user token on the negative leg and the server would ALLOW that write.";
        }

        return string.Equals(actingTenant, otherTenant, StringComparison.Ordinal)
            ? $"S8 identity's two acting users share the tenant '{actingTenant}', so the negative leg " +
              "could only be denied for ownership (or not at all) and would be evidence about " +
              "something other than tenancy."
            : null;
    }

    // ── the judgement (pure, so it is unit-testable without a live stack) ────────────────────

    /// <summary>
    /// Judges one language's write phase. The accepted write is what discharges
    /// <see cref="Requirements.IdnDualIdentityAcceptedOnWrite"/>: it is the only observation in the
    /// run that requires both identities to have arrived AND to have been read as different
    /// subjects. Fires unconditionally — a missing step is an explicit failure naming its
    /// consequence, never a silent skip.
    /// </summary>
    internal static IReadOnlyList<Assertion> JudgeWrite(string language, PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == WriteStepName);

        return
        [
            step is null
                ? Assertion.Fail(
                    $"{language}: a mapped write carrying both the service identity and the acting-user " +
                    "identity is accepted",
                    $"the driver reported no '{WriteStepName}' step, so it never attempted the write " +
                    "this requirement is observed through",
                    Requirements.IdnDualIdentityAcceptedOnWrite)
                : Assertion.From(
                    $"{language}: a mapped write carrying both the service identity and the acting-user " +
                    "identity is accepted",
                    step.Ok,
                    step.Error ?? "ok",
                    Requirements.IdnDualIdentityAcceptedOnWrite),
        ];
    }

    /// <summary>
    /// Judges one language's read phase against the acting-user identity the orchestrator resolved
    /// and the row key the write phase produced. Pure over reported data (no I/O), so every branch
    /// below is exercisable from a unit test.
    /// </summary>
    internal static IReadOnlyList<Assertion> Judge(
        string language,
        string expectedTenant,
        string expectedOwnerId,
        Guid? seededKey,
        PhaseDocument document)
    {
        var assertions = new List<Assertion>();

        // ── the IDN backstop (uncited by design — see the class doc comment) ──────────────────
        assertions.Add(Assertion.From(
            $"{language}: the write phase reported a row key for this language",
            seededKey is not null,
            seededKey is not null
                ? $"row '{seededKey}' is what the read-back and the denied update both target"
                : "with no seeded row, the wrong acting user's update would be treated as a create and " +
                  "SUCCEED, rendering a denial assertion green that had nothing to deny"));

        // ── IVC-IDN-002: the acting user's own row reads back, carrying its owner ─────────────
        var readStep = document.Steps.FirstOrDefault(s => s.Name == ReadStepName);
        var readEntity = readStep is { Ok: true } ? readStep.Entity : null;
        var reportedKey = ReadString(readEntity, "key");
        var readBack = readStep is { Ok: true }
            && Guid.TryParse(reportedKey, out var parsedKey)
            && seededKey is { } expectedKey
            && parsedKey == expectedKey;

        assertions.Add(Assertion.From(
            $"{language}: the row is readable back by the acting user that wrote it",
            readBack,
            readStep is null
                ? $"the driver reported no '{ReadStepName}' step"
                : !readStep.Ok
                    ? readStep.Error ?? "the read step failed"
                    : readBack
                        ? $"read back row '{reportedKey}'"
                        : $"the driver reported key '{reportedKey ?? "<none>"}' where the write phase " +
                          $"seeded '{seededKey?.ToString() ?? "<none>"}'",
            Requirements.IdnActingUserPropagatedToRow));

        var owner = ReadString(readEntity, "owner");
        assertions.Add(Assertion.From(
            $"{language}: the row carries the owner identity the acting user propagated",
            owner is not null && string.Equals(owner, expectedOwnerId, StringComparison.Ordinal),
            $"the driver reported owner '{owner ?? "<none>"}'; the acting-user token's subject is " +
            $"'{expectedOwnerId}'",
            Requirements.IdnActingUserPropagatedToRow));

        // ── IVC-IDN-003, derivation: the server's tenant wins over the client's ───────────────
        var tenant = ReadString(readEntity, "tenant");
        assertions.Add(Assertion.From(
            $"{language}: the stored row carries the acting user's own tenant, not the tenant the client sent",
            tenant is not null && string.Equals(tenant, expectedTenant, StringComparison.Ordinal),
            $"the driver reported tenant '{tenant ?? "<none>"}'; the driver sent '{WrongTenantValue}' and " +
            $"the acting-user token claims '{expectedTenant}'",
            Requirements.IdnTenancyDerivedAndEnforced));

        // ── IVC-IDN-003, enforcement: another tenant's acting user is denied ──────────────────
        var deniedStep = document.Steps.FirstOrDefault(s => s.Name == DeniedStepName);
        var code = deniedStep is { Ok: true } ? ReadStatusCode(deniedStep.Entity) : null;

        // The status NAME and MESSAGE are reported alongside the code purely as diagnostics — no
        // assertion grades them, because the server's message is byte-identical across the
        // refusals this axis can provoke (see the class doc comment's "What the status code cannot
        // distinguish"). Carrying them in the detail is what let that be established empirically
        // rather than only read off the server source.
        var reportedStatus = ReadString(deniedStep?.Entity, "status");
        var reportedDetail = ReadString(deniedStep?.Entity, "detail");

        assertions.Add(Assertion.From(
            $"{language}: an acting user of another tenant is denied a write to this row",
            code == DeniedStatusCode,
            deniedStep is null
                ? $"the driver reported no '{DeniedStepName}' step"
                : !deniedStep.Ok
                    ? $"the attempt itself broke, so no denial was observed: {deniedStep.Error ?? "no error text"}"
                    : code is null
                        ? "the driver reported no gRPC status code, which is what it reports when the " +
                          "wrong acting user's write was ACCEPTED"
                        : $"the driver reported gRPC status {code}, expected {DeniedStatusCode} " +
                          "(PERMISSION_DENIED)",
            Requirements.IdnTenancyDerivedAndEnforced));

        assertions[^1] = assertions[^1] with
        {
            Detail = assertions[^1].Detail +
                     $" [reported status '{reportedStatus ?? "<none>"}', message '{reportedDetail ?? "<none>"}']",
        };

        return assertions;
    }

    /// <summary>
    /// The <c>identity_doc</c> key <paramref name="language"/>'s write phase reported, parsed as a
    /// <see cref="Guid"/>, or null when it reported none (or something unparsable). This is the
    /// harness's own accounting of what it seeded — the backstop's subject, and the value the
    /// read-back is required to have returned.
    /// </summary>
    internal static Guid? SeededKey(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keysByLanguage,
        string language) =>
        keysByLanguage.TryGetValue(language, out var byName)
        && byName.TryGetValue(RowKeyName, out var raw)
        && Guid.TryParse(raw, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// A string property off a driver's reported step entity, or null when the document is absent,
    /// is not an object, lacks the property, or carries something that is not a string there.
    /// Nothing here judges — a missing value becomes null and the assertion above reports it.
    /// </summary>
    internal static string? ReadString(JsonElement? entity, string name) =>
        entity is { ValueKind: JsonValueKind.Object } document
        && document.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The numeric gRPC status code a driver reported for its denied-update step, or null when it
    /// reported none — which is what a driver whose wrong-acting-user update SUCCEEDED reports, and
    /// is graded as a failure by the assertion that reads it, never skipped.
    /// </summary>
    internal static int? ReadStatusCode(JsonElement? entity) =>
        entity is { ValueKind: JsonValueKind.Object } document
        && document.TryGetProperty("statusCode", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var code)
            ? code
            : null;

    // ── register-phase descriptor capture ────────────────────────────────────────────────────

    internal static (JsonElement? Descriptor, string? Failure) TryCaptureDescriptor(PhaseDocument document)
    {
        var step = document.Steps.FirstOrDefault(s => s.Name == RegisterStepName);
        if (step is null)
            return (null, $"the {RegisterLanguage} driver reported no '{RegisterStepName}' step");

        if (!step.Ok)
            return (null, step.Error ?? "registration failed");

        if (step.TypeDescriptor is not { } json)
            return (null, "typeDescriptor was null on the register step");

        try
        {
            // Parsed but discarded: parsing is the validation — the Reregistrar needs the raw JSON,
            // and a descriptor that cannot be parsed here would fail there with a worse message.
            Verifier.ParseDescriptor(json);
            return (json, null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ── phase plumbing (mirrors VectorSearchScenario's) ──────────────────────────────────────

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

        foreach (var language in alive.Where(l => !reported.Contains(l)))
        {
            states[language].Terminal = ReportCell.Fail(language, Name,
                $"'{language}' is not a recognized conformance driver language", states[language].Assertions);
        }

        return documents;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    internal sealed class LanguageState : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];

        /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
        public ReportCell? Terminal { get; set; }
    }
}
