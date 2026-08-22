using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S6's judgement, which is pure over reported data
/// (<see cref="QueryScenario.Judge"/>, <see cref="QueryScenario.ReadKeys"/>,
/// <see cref="QueryScenario.ReadMetric"/>, <see cref="QueryScenario.ExpectedKeys"/>) and so is
/// exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and the whole point of this file is that each QRY requirement's cell goes red for
/// exactly the defect its statement describes and for nothing else.
/// </summary>
public class QueryScenarioTests
{
    private static readonly Guid KeyA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid KeyB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KeyC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static JsonElement SearchEntity(params Guid[] keys) =>
        JsonSerializer.SerializeToElement(new { keys = keys.Select(k => k.ToString()).ToList() });

    private static JsonElement AggregateEntity(double? value) =>
        JsonSerializer.SerializeToElement(new { value, total = 0 });

    private static StepResult SearchStep(params Guid[] keys) =>
        new(QueryScenario.SearchStepName, true, Entity: SearchEntity(keys));

    private static StepResult AggregateStep(double? value) =>
        new(QueryScenario.AggregateStepName, true, Entity: AggregateEntity(value));

    private static Assertion Cited(IReadOnlyList<Assertion> assertions, string requirementId) =>
        assertions.Single(a => a.RequirementId == requirementId);

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_SearchReturnsTheSeededRowsAndAggregateCountsThem_AllAssertionsPass()
    {
        var expected = new HashSet<Guid> { KeyA, KeyB };
        var document = ReadDocument(SearchStep(KeyA, KeyB), AggregateStep(2));

        var assertions = QueryScenario.Judge("dotnet", expected, document);

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().Contain(a => a.RequirementId == Requirements.QrySearchReachable);
        assertions.Should().Contain(a => a.RequirementId == Requirements.QrySearchReturnsExactlyMatchingRows);
        assertions.Should().Contain(a => a.RequirementId == Requirements.QryAggregateReachable);
        assertions.Should().Contain(a => a.RequirementId == Requirements.QryAggregateCountsExactlyMatchingRows);
    }

    [Fact]
    public void Judge_SearchResultOrderDiffersFromTheSeedOrder_StillPasses()
    {
        // The comparison is a set comparison on purpose: no client promises a result order, and
        // IVC-QRY-002 says nothing about one (ordering is Deferred in the QRY coverage ledger).
        var assertions = QueryScenario.Judge(
            "go", new HashSet<Guid> { KeyA, KeyB }, ReadDocument(SearchStep(KeyB, KeyA), AggregateStep(2)));

        Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows).Passed.Should().BeTrue();
    }

    // ── IVC-QRY-002: the exact result set, both directions ────────────────────────────────────

    [Fact]
    public void Judge_SearchDroppedASeededRow_FailsOnlyTheResultSetRequirement()
    {
        // The mutation this pins: a client whose filter is too narrow (or whose paging truncates)
        // returns fewer rows than the run seeded. Reachability still passes — the call completed.
        var assertions = QueryScenario.Judge(
            "python", new HashSet<Guid> { KeyA, KeyB }, ReadDocument(SearchStep(KeyA), AggregateStep(2)));

        Cited(assertions, Requirements.QrySearchReachable).Passed.Should().BeTrue();
        var resultSet = Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows);
        resultSet.Passed.Should().BeFalse();
        resultSet.Detail.Should().Contain(KeyB.ToString());
        Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SearchReturnedARowTheRunNeverSeeded_FailsTheResultSetRequirement()
    {
        // The other direction: a client whose filter is too wide picks up rows from another run.
        // A one-way subset check would let this pass, which is why the assertion compares both ways.
        var assertions = QueryScenario.Judge(
            "java", new HashSet<Guid> { KeyA }, ReadDocument(SearchStep(KeyA, KeyC), AggregateStep(1)));

        var resultSet = Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows);
        resultSet.Passed.Should().BeFalse();
        resultSet.Detail.Should().Contain(KeyC.ToString());
    }

    [Fact]
    public void Judge_SearchStepFailed_FailsBothReachabilityAndTheResultSet()
    {
        var document = ReadDocument(
            new StepResult(QueryScenario.SearchStepName, false, Error: "Unavailable"),
            AggregateStep(1));

        var assertions = QueryScenario.Judge("typescript", new HashSet<Guid> { KeyA }, document);

        Cited(assertions, Requirements.QrySearchReachable).Passed.Should().BeFalse();
        // Not skipped: a failed search is an empty result set, and reporting nothing for
        // IVC-QRY-002 would discharge it vacuously.
        Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_SearchStepAbsent_FailsReachabilityNamingTheMissingStep()
    {
        var assertions = QueryScenario.Judge(
            "go", new HashSet<Guid> { KeyA }, ReadDocument(AggregateStep(1)));

        var reachability = Cited(assertions, Requirements.QrySearchReachable);
        reachability.Passed.Should().BeFalse();
        reachability.Detail.Should().Contain(QueryScenario.SearchStepName);
    }

    // ── IVC-QRY-003/004: the aggregate ────────────────────────────────────────────────────────

    [Fact]
    public void Judge_AggregateValueDisagreesWithTheSeededCount_FailsOnlyTheAggregateValue()
    {
        // The mutation this pins: a client whose aggregate filter differs from its search filter.
        // Its search can be perfectly right and its aggregate still wrong — which is exactly why
        // the aggregate is graded against the harness's seed count, not against the search step.
        var assertions = QueryScenario.Judge(
            "python", new HashSet<Guid> { KeyA, KeyB }, ReadDocument(SearchStep(KeyA, KeyB), AggregateStep(7)));

        Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows).Passed.Should().BeTrue();
        Cited(assertions, Requirements.QryAggregateReachable).Passed.Should().BeTrue();
        var value = Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows);
        value.Passed.Should().BeFalse();
        value.Detail.Should().Contain("aggregate=7").And.Contain("seeded=2");
    }

    [Fact]
    public void Judge_AggregateAgreesWithAWrongSearch_StillFails()
    {
        // Both wrong in the same direction. Grading the aggregate against the driver's own search
        // result would pass this; grading it against the harness's seed count fails it.
        var assertions = QueryScenario.Judge(
            "typescript", new HashSet<Guid> { KeyA, KeyB }, ReadDocument(SearchStep(KeyA), AggregateStep(1)));

        Cited(assertions, Requirements.QrySearchReturnsExactlyMatchingRows).Passed.Should().BeFalse();
        Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_AggregateStepReportedNoNumericValue_FailsTheAggregateValueNotOnlyReachability()
    {
        var assertions = QueryScenario.Judge(
            "java", new HashSet<Guid> { KeyA }, ReadDocument(SearchStep(KeyA), AggregateStep(null)));

        Cited(assertions, Requirements.QryAggregateReachable).Passed.Should().BeTrue();
        Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_AggregateStepFailed_FailsReachabilityAndTheValue()
    {
        // Distinct from the absent-step case below: a step that is PRESENT but reports ok:false
        // goes down the other branch of the reachability assertion, and mutation testing found that
        // branch unfalsifiable without this test — forcing it to `true` left the suite green.
        var document = ReadDocument(
            SearchStep(KeyA),
            new StepResult(QueryScenario.AggregateStepName, false, Error: "Unimplemented"));

        var assertions = QueryScenario.Judge("go", new HashSet<Guid> { KeyA }, document);

        var reachability = Cited(assertions, Requirements.QryAggregateReachable);
        reachability.Passed.Should().BeFalse();
        reachability.Detail.Should().Contain("Unimplemented");
        Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_AggregateStepAbsent_FailsBothAggregateRequirements()
    {
        var assertions = QueryScenario.Judge(
            "dotnet", new HashSet<Guid> { KeyA }, ReadDocument(SearchStep(KeyA)));

        Cited(assertions, Requirements.QryAggregateReachable).Passed.Should().BeFalse();
        Cited(assertions, Requirements.QryAggregateCountsExactlyMatchingRows).Passed.Should().BeFalse();
    }

    // ── the backstop ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_NothingWasSeeded_FailsTheBackstopEvenThoughEverySetComparisonAgrees()
    {
        // Without the backstop this document is fully green: an empty result set equals an empty
        // expectation and a zero aggregate equals a zero count — five clients agreeing on nothing.
        var assertions = QueryScenario.Judge("go", new HashSet<Guid>(), ReadDocument(SearchStep(), AggregateStep(0)));

        var backstop = Named(assertions, "seeded at least one row");
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull("the backstop is uncited by design — see the QRY backstop note");
        assertions.Where(a => a.RequirementId is not null).Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void Judge_BackstopFiresOnEveryPathIncludingAnEmptyDocument()
    {
        var assertions = QueryScenario.Judge("python", new HashSet<Guid> { KeyA }, ReadDocument());

        Named(assertions, "seeded at least one row").Passed.Should().BeTrue();
        assertions.Where(a => a.RequirementId is not null).Should().OnlyContain(a => !a.Passed);
    }

    // ── the readers ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadKeys_MalformedOrAbsentDocument_YieldsNoKeysRatherThanThrowing()
    {
        QueryScenario.ReadKeys(null).Should().BeEmpty();
        QueryScenario.ReadKeys(JsonSerializer.SerializeToElement("not-an-object")).Should().BeEmpty();
        QueryScenario.ReadKeys(JsonSerializer.SerializeToElement(new { keys = "not-an-array" })).Should().BeEmpty();
        QueryScenario.ReadKeys(JsonSerializer.SerializeToElement(new { keys = new[] { "not-a-uuid" } }))
            .Should().BeEmpty();
    }

    [Fact]
    public void ReadKeys_ParsesUuidsIrrespectiveOfSpelling()
    {
        var entity = JsonSerializer.SerializeToElement(new
        {
            keys = new[] { KeyA.ToString().ToUpperInvariant(), KeyB.ToString("B") },
        });

        QueryScenario.ReadKeys(entity).Should().BeEquivalentTo(new[] { KeyA, KeyB });
    }

    [Fact]
    public void ReadMetric_DistinguishesAReportedZeroFromNoValueAtAll()
    {
        QueryScenario.ReadMetric(AggregateEntity(0)).Should().Be(0);
        QueryScenario.ReadMetric(AggregateEntity(null)).Should().BeNull();
        QueryScenario.ReadMetric(null).Should().BeNull();
        QueryScenario.ReadMetric(JsonSerializer.SerializeToElement(new { value = "3" })).Should().BeNull();
    }

    [Fact]
    public void ExpectedKeys_CollectsEveryLanguagesRowKeyAndIgnoresOtherLogicalNames()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new Dictionary<string, string> { [QueryScenario.RowKeyName] = KeyA.ToString() },
            ["go"] = new Dictionary<string, string>
            {
                [QueryScenario.RowKeyName] = KeyB.ToString(),
                ["article"] = KeyC.ToString(),
            },
            ["java"] = new Dictionary<string, string> { ["article"] = KeyC.ToString() },
        };

        QueryScenario.ExpectedKeys(keys).Should().BeEquivalentTo(new[] { KeyA, KeyB });
    }

    // ── register-phase capture ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryCaptureDescriptor_MissingFailedOrDescriptorlessStep_ReportsAFailureNamingTheCause()
    {
        QueryScenario.TryCaptureDescriptor(new PhaseDocument("dotnet", "register", []))
            .Failure.Should().Contain(QueryScenario.RegisterStepName);

        QueryScenario.TryCaptureDescriptor(new PhaseDocument("dotnet", "register",
                [new StepResult(QueryScenario.RegisterStepName, false, Error: "boom")]))
            .Failure.Should().Be("boom");

        QueryScenario.TryCaptureDescriptor(new PhaseDocument("dotnet", "register",
                [new StepResult(QueryScenario.RegisterStepName, true)]))
            .Failure.Should().Contain("typeDescriptor");
    }

    // ── the projection-wait predicate ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void ProjectionReady_FewerRowsVisibleThanWritten_IsNotReady(int visible, int expected)
    {
        // Catches the mutation that makes the probe predicate constantly true: the read phase would
        // then fire against a projection that has not caught up, reddening QRY-002/QRY-004 for
        // every language and blaming five client libraries for the outbox.
        QueryScenario.ProjectionReady(visible, expected).Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void ProjectionReady_AtLeastAsManyRowsVisibleAsWritten_IsReady(int visible, int expected)
    {
        QueryScenario.ProjectionReady(visible, expected).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    public void ProjectionReady_NothingWasWritten_IsDeliberatelyNotReady(int visible, int expected)
    {
        // expected == 0 means the write phase produced no keys at all: "0 >= 0" would satisfy the
        // wait instantly and let the read phase grade against nothing. The wait must expire so the
        // harness reports its own precondition failing.
        QueryScenario.ProjectionReady(visible, expected).Should().BeFalse();
    }
}
