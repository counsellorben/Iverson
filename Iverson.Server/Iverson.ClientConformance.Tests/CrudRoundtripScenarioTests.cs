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
    private static CrudRoundtripScenario BuildScenario(DriverRunner? runner = null)
    {
        var channel = GrpcChannel.ForAddress("http://localhost:1");
        var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
        var reregistrar = new Reregistrar(mapping);
        var probe = new PostgresProbe("Host=localhost;Database=nonexistent");
        return new CrudRoundtripScenario(
            runner ?? new DriverRunner(repoRoot: "/tmp"), mapping, reregistrar, probe);
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
}
