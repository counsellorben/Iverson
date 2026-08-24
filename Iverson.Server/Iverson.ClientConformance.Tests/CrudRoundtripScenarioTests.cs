using System.Text.Json;
using FluentAssertions;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Iverson.ClientConformance;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class CrudRoundtripScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: CrudRoundtripScenario.Name,
        Type: "Widget",
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s1-");

    /// <summary>
    /// Builds a scenario whose collaborators (gRPC client, Postgres probe) are never actually
    /// dialed/queried in this test: an unrecognized language never gets past the register phase,
    /// so nothing downstream of <c>DriverRunner</c> is touched. The channel/connection string are
    /// throwaway values that only need to construct, not connect.
    /// </summary>
    private static CrudRoundtripScenario BuildScenario(IDriverRunner? runner = null)
    {
        var channel = GrpcChannel.ForAddress("http://localhost:1");
        var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
        var reregistrar = new Reregistrar(mapping);
        var probe = new PostgresProbe("Host=localhost;Database=nonexistent");
        return new CrudRoundtripScenario(
            runner ?? new DriverRunner(repoRoot: "/tmp"), mapping, reregistrar, probe);
    }


    // ── RunAsync driven end to end (closes Ruling 38's N5 residual) ────────────────────────────
    //
    // The tests below grade JudgeDriverDepthRead directly. That is exactly what left mutant N5 —
    // deleting `JudgeDriverDepthRead(...)` from RunAsync's read loop — surviving the whole suite:
    // the helper stayed graded, the line that CALLS it did not, and IVC-LIFE-006/008 stayed cited
    // in source while grading nothing. The IDriverRunner seam is what makes the test below reach
    // that line; it asserts on the REPORT CELL, the only place the call site is observable.
    //
    // The scripting is shaped to avoid every live collaborator, deliberately and not incidentally:
    //   * `register_author` fails, so state.Author stays null and the three-way CompareAsync loop
    //     `continue`s before touching gRPC or Postgres — while `register` SUCCEEDS, so
    //     state.Article IS set and JudgeDriverDepthRead's SECOND assertion (IVC-LIFE-008, which is
    //     guarded on state.Article) is reached rather than silently skipped.
    //   * no write-phase keys are reported, so KeyOf returns null and the update and delete tails
    //     `continue` before their MappingGetAsync/FetchRowAsync calls.
    // Change either and this test starts dialing localhost:1.

    private static ScriptedDriverRunner DepthReadScript(StepResult depth1Step)
    {
        var registerDoc = new PhaseDocument("dotnet", "register",
        [
            new StepResult("register", true, TypeDescriptor: ArticleDescriptorJson()),
            new StepResult("register_author", false, Error: "deliberately failed — see the comment above"),
            new StepResult("register_tag", false, Error: "deliberately failed — see the comment above"),
        ]);

        return new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Success("dotnet", registerDoc))
            .Script(Phase.Write, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "write", [new StepResult("write_article", true)])))
            .Script(Phase.Read, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "read",
                [
                    new StepResult("get", true),
                    new StepResult("get_author", true),
                    depth1Step,
                ])))
            .Script(Phase.Update, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "update", [new StepResult("update", true)])))
            .Script(Phase.Delete, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "delete", [new StepResult("delete", true)])));
    }

    [Fact]
    public async Task RunAsync_DriverReportedItsDepth1Read_TheDepthJudgementReachesTheCell()
    {
        var runner = DepthReadScript(new StepResult("get_depth1", true));

        var cells = await BuildScenario(runner).RunAsync(["dotnet"], Context(), "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        var ids = cell.Assertions.Select(a => a.RequirementId).ToList();

        // Both of JudgeDriverDepthRead's assertions. Neither can be in the cell unless RunAsync
        // actually called it — deleting that one line empties both and this fails.
        ids.Should().Contain(Requirements.LifeDepthResolvedReadReachable);
        ids.Should().Contain(Requirements.LifeDepthResolvedReadHydrated);
    }

    [Fact]
    public async Task RunAsync_DriverReportedNoDepth1Step_TheReachabilityFailureReachesTheCell()
    {
        // The negative direction of the same call site: a test asserting only that the ids are
        // PRESENT would also pass against a judge that always emits passing assertions.
        var runner = DepthReadScript(new StepResult("unrelated_step", true));

        var cells = await BuildScenario(runner).RunAsync(["dotnet"], Context(), "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Assertions.Should().Contain(a =>
            a.RequirementId == Requirements.LifeDepthResolvedReadReachable && !a.Passed);
    }

    [Fact]
    public async Task RunAsync_OnUnrecognizedLanguage_ReportsAFailedCell_NotOk()
    {
        // DriverRunner only knows five languages (dotnet/python/typescript/go/java) — it silently
        // produces no outcome at all for anything else. Without the CrudRoundtripScenario-level
        // guard, that means no Terminal is ever set, no assertion is ever added, and Cell() falls
        // through to ReportCell.Ok for a plain typo like "typescrpt".
        var scenario = BuildScenario();

        var cells = await scenario.RunAsync(["typescrpt"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("typescrpt");
        cell.Status.Should().NotBe(CellStatus.Ok);
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("not a recognized conformance driver language");
    }

    // ── Ruling 31: the MU-R4 hole, closed for this scenario's two citation sites ─────────────
    //
    // Both tests below pin a WIRING claim, not a grading claim: that the judgement a call site
    // constructs actually reaches a report cell. Grading is VerifierTests' job. The hole they
    // close is the one MU-R4 found next door in TenantRejectedScenario — the coverage gate's
    // Check2 greps SOURCE TEXT for each const, so a call site can be deleted while every const it
    // reached is still "cited" inside Verifier.cs, leaving the gate green and the requirements
    // grading nothing at all.
    //
    // ── WHAT THIS PATTERN DOES NOT CLOSE (Ruling 38, an ACCEPTED and BOUNDED residual) ──────────
    //
    // What is graded is the EXTRACTED JUDGE. The line in RunAsync that CALLS it is not: delete
    // `JudgeDriverDepthRead(...)` or `TakeDescriptor(...)` from RunAsync and `dotnet test` still
    // passes — mutants N3 and N5 both survived at 439/439 when first measured, and both were
    // RE-MEASURED at 448/448 exit 0 in the final fix wave. This is a property of the pattern
    // itself, shared by every site that uses it (TakeDescriptor and JudgeDriverDepthRead here,
    // SchemaCatalogScenario.JudgeReadPhase, NamingRejectedScenario.BuildDriverCell), not of any
    // one application of it.
    //
    // What BOUNDS it: a full-matrix live run's `UntouchedRequirementIds` exit code catches every
    // ID-CARRYING instance — a deleted call site means the requirement is never touched and the
    // run exits 1. So the residual costs a CI-to-live delay, not a silent hole, wherever the
    // vanished assertions carry a requirement ID. The one place that argument does not reach is
    // NamingRejectedScenario.BuildDriverCell, whose three client-side assertions carry NO ID and
    // are therefore invisible to the tally — which is exactly why that site has a hand-written
    // test of its own rather than relying on this bound.
    //
    // The PROPER fix is to make DriverRunner substitutable (it is sealed with a non-virtual
    // RunPhaseAsync today), so one test per scenario could drive RunAsync end to end and pin every
    // call site at once — strictly better than N extracted helpers. That is a design change across
    // ten scenarios and is DEFERRED as a follow-up, deliberately out of scope here.

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>
    /// A conforming article descriptor in the shape the drivers report it: one UUID key, a
    /// many-to-one and a many-to-many relation, so the registration arms this scenario grades are
    /// actually exercised and the hydration loop has owning relations to walk.
    /// </summary>
    private static JsonElement ArticleDescriptorJson() => Json(
        """
        {
          "typeName": "Article",
          "properties": [
            { "name": "id", "clrType": "CLR_GUID", "isKey": true },
            { "name": "author_id", "clrType": "CLR_GUID" },
            { "name": "tag_ids", "clrType": "CLR_GUID", "isArray": true }
          ],
          "relations": [
            { "propertyName": "Author", "kind": "MANY_TO_ONE", "relatedType": "author", "foreignKey": "author_id" },
            { "propertyName": "Tags", "kind": "MANY_TO_MANY", "relatedType": "tag", "foreignKey": "tag_ids" }
          ]
        }
        """);

    /// <summary>An author descriptor whose only relation is the reverse one-to-many.</summary>
    private static JsonElement AuthorDescriptorJson() => Json(
        """
        {
          "typeName": "Author",
          "properties": [
            { "name": "id", "clrType": "CLR_GUID", "isKey": true }
          ],
          "relations": [
            { "propertyName": "Articles", "kind": "ONE_TO_MANY", "relatedType": "article", "foreignKey": "author_id" }
          ]
        }
        """);

    private static PhaseDocument RegisterDocument(JsonElement descriptor) => new(
        "dotnet", "register",
        [new StepResult("register", true, TypeDescriptor: descriptor)]);

    /// <summary>
    /// The <c>Verifier.VerifyRegistration</c> call inside
    /// <see cref="CrudRoundtripScenario.TakeDescriptor"/> is the ONLY place the orchestrator ever
    /// calls it. Delete that one line and IVC-DECL-001/003/006 and IVC-REL-001/002/003/004/010
    /// stop reaching every cell in the matrix, with the coverage gate still green. This test is
    /// what fails instead.
    ///
    /// <para>The expected set is read back off <c>VerifyRegistration</c> itself rather than
    /// hardcoded in full, because the claim is "whatever that function grades reaches the cell". A
    /// hardcoded floor is asserted first, so a fixture that quietly stopped exercising the relation
    /// arms cannot make the wiring claim vacuous.</para>
    /// </summary>
    [Fact]
    public void TakeDescriptor_TheRegistrationJudgement_ReachesTheCellCarryingItsDeclAndRelCitations()
    {
        var descriptorJson = ArticleDescriptorJson();
        RelationKind[] expectedKinds = [RelationKind.ManyToOne, RelationKind.ManyToMany];

        var expected = Verifier
            .VerifyRegistration("article", Verifier.ParseDescriptor(descriptorJson), expectedKinds)
            .Select(a => a.RequirementId)
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        expected.Should().Contain(
        [
            Requirements.DeclExactlyOneKeyProperty,
            Requirements.DeclKeyTypedUuid,
            Requirements.RelForeignKeySynthesizedForOwningKinds,
            Requirements.RelForeignKeyNamedRelatedTypeId,
            Requirements.RelNavPropertyDistinctFromForeignKey,
            Requirements.RelIsArraySetForManyToManyOnly,
        ], "the fixture must actually exercise the registration arms this test claims to pin");

        var state = new CrudRoundtripScenario.LanguageState();

        var captured = CrudRoundtripScenario.TakeDescriptor(
            state, RegisterDocument(descriptorJson), "register", "article", expectedKinds);

        captured.Should().NotBeNull();

        var cell = ScenarioCells.Cell("dotnet", CrudRoundtripScenario.Name, state);

        cell.Assertions.Select(a => a.RequirementId).Should().Contain(expected,
            "a citation that exists in source but never executes grades nothing — every requirement "
            + "VerifyRegistration constructs must reach the cell");
    }

    /// <summary>
    /// <see cref="CrudRoundtripScenario.CompareAsync"/>'s
    /// <c>Verifier.VerifyRelationHydrated</c> loop is the ONLY citation site for
    /// <see cref="Requirements.RelForeignKeyReadableAtDepth"/> (IVC-REL-006) and
    /// <see cref="Requirements.RelOneToManyReverseLookup"/> (IVC-REL-008) anywhere in the
    /// orchestrator — grep either const and this loop is the only hit outside
    /// <c>Requirements.cs</c>. Dropping it removes both requirements from the whole matrix.
    ///
    /// <para>BOTH descriptors are compared, because the two consts sit on opposite branches of
    /// <c>VerifyRelationHydrated</c>: the one-to-many branch cites IVC-REL-008 and returns before
    /// IVC-REL-006 is ever reached, so an article-only fixture would pin half the loop.</para>
    ///
    /// <para>The collaborators are deliberately dead (a gRPC channel on port 1, a connection string
    /// naming no database). That is not a limitation: the hydration assertions are then built from
    /// a NULL gRPC entity and FAIL, which is correct and still carries their citations. What this
    /// test pins is that they are built at all.</para>
    /// </summary>
    [Fact]
    public async Task CompareAsync_TheHydrationJudgement_ReachesTheCellCarryingItsRelCitations()
    {
        // Seeded keys, or CompareAsync short-circuits on "the write phase reported no key" and
        // never reaches the hydration loop — which would make the mutation this test exists for
        // indistinguishable from the truth.
        var runner = new DriverRunner(repoRoot: "/tmp");
        runner.MergeKeys("dotnet", new PhaseDocument("dotnet", "write",
        [
            new StepResult("write", true, Keys: new Dictionary<string, string>
            {
                ["article"] = Guid.NewGuid().ToString(),
                ["author"] = Guid.NewGuid().ToString(),
            }),
        ]));

        var scenario = BuildScenario(runner);
        var state = new CrudRoundtripScenario.LanguageState();

        var article = CrudRoundtripScenario.TakeDescriptor(
            state, RegisterDocument(ArticleDescriptorJson()), "register", "article",
            [RelationKind.ManyToOne, RelationKind.ManyToMany]);
        var author = CrudRoundtripScenario.TakeDescriptor(
            state, RegisterDocument(AuthorDescriptorJson()), "register", "author",
            [RelationKind.OneToMany]);

        article.Should().NotBeNull();
        author.Should().NotBeNull();

        await scenario.CompareAsync(
            state, "dotnet", article!, "article", driverEntity: null, "acting-token", default);
        await scenario.CompareAsync(
            state, "dotnet", author!, "author", driverEntity: null, "acting-token", default);

        var cell = ScenarioCells.Cell("dotnet", CrudRoundtripScenario.Name, state);
        var cited = cell.Assertions.Select(a => a.RequirementId).ToList();

        cited.Should().Contain(Requirements.RelForeignKeyReadableAtDepth,
            "IVC-REL-006 is cited nowhere but the hydration loop — if that loop stops reaching the "
            + "cell, nothing in the matrix grades it");
        cited.Should().Contain(Requirements.RelOneToManyReverseLookup,
            "IVC-REL-008 is cited nowhere but the hydration loop's one-to-many branch");
    }

    // ── Important 2: the three seams Ruling 31's first pass left open ────────────────────────
    //
    // Ruling 31 was closed for TWO of FIVE citation seams in this scenario, not two of two. The
    // three below are the rest. Each pins the same WIRING claim as the two above: that the
    // judgement a call site constructs actually reaches a report cell, which the coverage gate's
    // source-text Check2 structurally cannot see.

    /// <summary>
    /// <see cref="CrudRoundtripScenario.JudgeDriverDepthRead"/>'s
    /// <c>Verifier.VerifyDepthResolvedReadReachable</c> call is the SOLE citation site for
    /// <see cref="Requirements.LifeDepthResolvedReadReachable"/> (IVC-LIFE-006), and the
    /// <c>Verifier.VerifyDepthCapability</c> call beside it the SOLE site for
    /// <see cref="Requirements.LifeDepthResolvedReadHydrated"/> (IVC-LIFE-008). Delete either and
    /// its const is still cited inside <c>Verifier.cs</c>, so the gate stays green while the
    /// requirement grades nothing anywhere in the matrix.
    ///
    /// <para>A descriptor must be captured first or the LIFE-008 arm is skipped by its
    /// <c>state.Article is not null</c> guard — which would make deleting that arm
    /// indistinguishable from the truth, exactly the vacuity this test exists to rule out.</para>
    /// </summary>
    [Fact]
    public void JudgeDriverDepthRead_BothDepthJudgements_ReachTheCellCarryingTheirLifeCitations()
    {
        var state = new CrudRoundtripScenario.LanguageState();
        state.Article = CrudRoundtripScenario.TakeDescriptor(
            state, RegisterDocument(ArticleDescriptorJson()), "register", "article",
            [RelationKind.ManyToOne, RelationKind.ManyToMany]);
        state.Article.Should().NotBeNull("the LIFE-008 arm is guarded on a captured descriptor");

        var readDocument = new PhaseDocument("dotnet", "read",
        [
            new StepResult("get_depth1", true, Entity: Json(
                """
                {
                  "id": "00000000-0000-0000-0000-000000000001",
                  "Author": { "id": "00000000-0000-0000-0000-000000000002" }
                }
                """)),
        ]);

        CrudRoundtripScenario.JudgeDriverDepthRead(state, readDocument);

        var cited = ScenarioCells.Cell("dotnet", CrudRoundtripScenario.Name, state)
            .Assertions.Select(a => a.RequirementId).ToList();

        cited.Should().Contain(Requirements.LifeDepthResolvedReadReachable,
            "IVC-LIFE-006 is cited nowhere but this call — if it stops reaching the cell, nothing "
            + "in the matrix grades the driver's own depth-1 read being reachable at all");
        cited.Should().Contain(Requirements.LifeDepthResolvedReadHydrated,
            "IVC-LIFE-008 is cited nowhere but the VerifyDepthCapability call beside it");
    }

    /// <summary>
    /// <see cref="CrudRoundtripScenario.CompareAsync"/>'s <c>Verifier.VerifyThreeWay</c> loop is
    /// the scenario's core driver/gRPC/Postgres comparison, and it is the only citation site for
    /// <see cref="Requirements.DeclKeyWellFormedUuid"/> (IVC-DECL-004 — ALL THREE of its citations
    /// sit inside that one helper) and one of <see cref="Requirements.RelForeignKeyWellFormedUuid"/>
    /// (IVC-REL-010)'s two. Deleting the loop leaves both consts cited in <c>Verifier.cs</c>, so
    /// the coverage gate stays green while the scenario's central comparison grades nothing.
    ///
    /// <para>BOTH citations are pinned from one call because
    /// <c>Verifier.ComparedValueNames</c> yields the key and every owning relation's foreign key,
    /// and <c>VerifyThreeWay</c> partitions the two requirements across exactly that split —
    /// DECL-004 on the key firing, REL-010 on a foreign key. A key-only fixture would pin half the
    /// loop, so the article descriptor's two owning relations are load-bearing here.</para>
    ///
    /// <para>The collaborators are deliberately dead, as in the hydration test above: the
    /// assertions are then built from a null gRPC entity and FAIL, which is correct and still
    /// carries their citations. What this pins is that they are built at all.</para>
    /// </summary>
    [Fact]
    public async Task CompareAsync_TheThreeWayComparison_ReachesTheCellCarryingItsDeclAndRelCitations()
    {
        var runner = new DriverRunner(repoRoot: "/tmp");
        runner.MergeKeys("dotnet", new PhaseDocument("dotnet", "write",
        [
            new StepResult("write", true, Keys: new Dictionary<string, string>
            {
                ["article"] = Guid.NewGuid().ToString(),
            }),
        ]));

        var scenario = BuildScenario(runner);
        var state = new CrudRoundtripScenario.LanguageState();

        var article = CrudRoundtripScenario.TakeDescriptor(
            state, RegisterDocument(ArticleDescriptorJson()), "register", "article",
            [RelationKind.ManyToOne, RelationKind.ManyToMany]);
        article.Should().NotBeNull();

        Verifier.ComparedValueNames(article!.Descriptor).Should().HaveCountGreaterThan(1,
            "the fixture must yield a key AND at least one foreign key, or this test pins only "
            + "half of the requirement partition it claims to cover");

        await scenario.CompareAsync(
            state, "dotnet", article!, "article", driverEntity: null, "acting-token", default);

        var cited = ScenarioCells.Cell("dotnet", CrudRoundtripScenario.Name, state)
            .Assertions.Select(a => a.RequirementId).ToList();

        cited.Should().Contain(Requirements.DeclKeyWellFormedUuid,
            "IVC-DECL-004 is cited nowhere but VerifyThreeWay's isKey branch");
        cited.Should().Contain(Requirements.RelForeignKeyWellFormedUuid,
            "IVC-REL-010 loses one of its two citation sites if this loop stops reaching the cell");
    }
}
