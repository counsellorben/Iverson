# MatchPattern Engine (Plan 1 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-17-row-pattern-matching-design.md` (commit SHA: `c1ec7b4a`)

**Goal:** Build `Iverson.Server/Iverson.Patterns`, the store-free SQL:2016 row-pattern-matching engine of spec §1–§2, §3.1, §4 (compiled-program bound) and §9.1–§9.2, verified against a live Trino 483 oracle.

**Architecture:** A pattern is parsed (§1), optimized and compiled to a Trino-shaped instruction program under the `MaxProgramInstructions` cap, then run per partition by a port of Trino 483's thread-VM `Matcher` whose `define` predicates and `measures` are evaluated by an interpreter over Trino's navigation model. `PatternQuery.Compile` / `CompiledPattern.Run` is the only public entry point. Plan 2 (proto, StarRocks, Qdrant, Api) and Plan 3 (SDKs, conformance) consume it; nothing in this plan touches a store, the proto or an RPC.

**Tech stack:** .NET 10 (`net10.0`), xunit 2.9.3, FluentAssertions 8.11.0, Testcontainers 4.15.0 (oracle only), Trino 483 (`trinodb/trino:483`, test oracle only), upstream Trino source at tag `483` (Apache-2.0) as the port source.

---

## Global Constraints

- `Iverson.Patterns` has **no store dependencies** (spec §3.1): no `PackageReference` and no `ProjectReference` at all.
- Pattern-variable and subset names are case-insensitive and canonicalized to **upper case**; `CLASSIFIER()` returns the upper-case name (spec §1).
- Input names (columns, `partition_by`) resolve case-insensitively; **output** names are ordinal (spec §1, §3.2).
- SQL `NULL` is C# `null` throughout (spec §3.2). `NaN` follows the spec §2 list exactly.
- `Matcher` is a port of Trino's `io.trino.operator.window.matcher.Matcher` (Apache-2.0), attributed in each ported file's header and in the repository-root `THIRD-PARTY-NOTICES.md` (spec §3.1).
- Container-backed tests carry `[Trait("Category", "Integration")]`; CI runs `dotnet test Iverson.slnx --filter "Category!=Integration"`.
- **Manual mutation check in every task review** (spec §9.6): delete or invert the named production branch, confirm the covering test fails, restore, and confirm `git status --porcelain` is clean.

## File Structure

**Create** (`Iverson.Server/Iverson.Patterns/`):
- `Iverson.Patterns.csproj` — library, no references, `InternalsVisibleTo` the test project.
- `PatternRequest.cs` — public enums and request records (the `Compile` input).
- `PatternExceptions.cs` — `PatternValidationException`, `PatternEvaluationException`, `PatternBudgetExceededException`.
- `PatternBudget.cs` — `MaxActiveThreads` / `MaxSteps` counters.
- `MatchOutputRow.cs` — `MatchOutputRow`, `SimilarityTerm`.
- `ArrayView.cs` — port of Trino `ArrayView` (read-only int view).
- `Syntax/PatternNode.cs`, `Syntax/PatternParser.cs` — §1 grammar → AST.
- `Compilation/Instruction.cs`, `Compilation/PatternOptimizer.cs`, `Compilation/ProgramCompiler.cs` — AST → capped program.
- `Expressions/Navigation.cs`, `Expressions/Expr.cs`, `Expressions/ExpressionLexer.cs`, `Expressions/ExpressionParser.cs`, `Expressions/SimilarityTermTable.cs` — §2 grammar → lowered expression IR.
- `Expressions/EvaluationContext.cs`, `Expressions/SqlValues.cs`, `Expressions/ExpressionEvaluator.cs` — the interpreter.
- `Matching/IntList.cs`, `Matching/IntStack.cs`, `Matching/IntMultimap.cs`, `Matching/Captures.cs`, `Matching/MatchResult.cs`, `Matching/ILabelEvaluator.cs`, `Matching/ThreadEquivalence.cs`, `Matching/Matcher.cs` — the thread VM.
- `Matching/PartitionLabelEvaluator.cs`, `Matching/PartitionMatcher.cs` — per-partition loop (port of `PatternRecognitionPartition`).
- `PatternQuery.cs`, `CompiledPattern.cs` — public entry point.

**Create** (`Iverson.Server/Iverson.Patterns.Tests/`): `Iverson.Patterns.Tests.csproj`; `PatternBudgetTests.cs`; `PatternParserTests.cs`; `ProgramCompilerTests.cs`; `ExpressionParserTests.cs`; `ExpressionEvaluatorTests.cs`; `MatcherTests.cs`; `PatternQueryTests.cs`; `TestRows.cs`; `Oracle/TrinoContainerFixture.cs`, `Oracle/TrinoClient.cs`, `Oracle/OracleCase.cs`, `Oracle/OracleRunner.cs`, `Oracle/PortedTrinoCasesTests.cs`, `Oracle/GeneratedCasesTests.cs`, `Oracle/trino-483-cases.json`.

**Create** (repo root): `THIRD-PARTY-NOTICES.md`.

**Modify:** `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx` (register both projects).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and seven `critical-design-review` rounds at spec-write time and are NOT re-verified here. Trusted as ground truth (only the rows this plan rests on are repeated; the rest belong to Plans 2–3):

- `.NET` `double` `NaN` semantics as stated in §2 — CDR-1 probes P6, P13, P18.
- Trino 483 accepts `MATCH_NUMBER()` and a `RUNNING` prefix inside `DEFINE` on the operands a semantics prefix can take (`FIRST`, `LAST`, aggregates), and rejects `FINAL` there ("FINAL semantics is not supported in DEFINE clause") — CDR-3 probe P27 D/E/F; CDR-4 probe P40.
- No ported Trino case exercises `MATCH_NUMBER()` or `RUNNING` inside `DEFINE` — CDR-3 probe P28.
- Testcontainers available; Trino image ships a memory catalog — `Iverson.Api.Tests.csproj:24` (Testcontainers 4.15.0); `trinodb/trino` `core/docker/default/etc/catalog/memory.properties`.
- Trino matcher design, license and tests — upstream `Matcher.java` (`threadsAtInstructions`, `ThreadEquivalence`); `LICENSE` Apache-2.0; `TestRowPatternMatching.java` (160 `VALUES`).
- Iverson is MIT — repository `LICENSE`.
- No mutation-testing tool installed — no Stryker config; `~/.dotnet/tools` has only `ilspycmd`.
- Both solution files list every project — `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx`.
- Trino rejects an unknown column in `DEFINE` and in `MEASURES` at analysis time, even over zero rows — CDR-4 probe P32 Q1, Q2, Q5.
- Trino folds unquoted pattern-variable and subset names to upper case in every slot — CDR-4 probes P53 C1–C8 and P56.
- The `MATCH_NUMBER()` value inside `define` matches the oracle on every branch of its per-attempt loop (unmatched: no increment; empty and non-empty match: increment) — CDR-4 probe P49; P30 T1/T2/T6/T9.
- The evaluator can obtain the attempt's match number during `define` evaluation, as the ported contract provides — CDR-4 probe P33 (`LabelEvaluator.java@483:31,52-54,78`).
- Trino 483 expands `PERMUTE` to all k! orderings and unrolls bounded quantifiers one operand copy per repetition; its reachable-label precomputation and thread scheduling are recursive, and the precomputation runs from every instruction. Trino 483 fails `PERMUTE` of 7–9 items, `A{0,10000}` and `(A{0,100}){0,100}` with `StackOverflowError`. In .NET a stack overflow ends the process (exit 134), while an explicit-stack traversal of the same program completes. A cap checked on every append rejects `PERMUTE` of 12 after 5,000 instructions. Every ported Trino case compiles to at most 22 instructions — CDR-7 probes P80–P84, P94.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-23; probes ran against `docker.io/trinodb/trino:483`, image `1a95bd13a377`, digest `sha256:db58cc93…`, and the Trino source at tag `483`):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.Patterns/`, `Iverson.Server/Iverson.Patterns.Tests/` and `THIRD-PARTY-NOTICES.md` do not exist | `ls` → "No such file or directory" for all three |
| 2 | File path | `Iverson.Server/Iverson.Server.slnx` is a flat `<Solution>` of `<Project Path="X/X.csproj" />` lines; `Iverson.slnx` groups server projects under `<Folder Name="/Iverson.Server/">` with `Iverson.Server/`-prefixed paths | both files read in full |
| 3 | File path | Trino 483 sources exist at `raw.githubusercontent.com/trinodb/trino/483/core/trino-main/src/…`: `main/java/io/trino/operator/window/matcher/{ArrayView,Captures,Done,Instruction,IntList,IntMultimap,IntStack,IrRowPatternToProgramRewriter,Jump,MatchAggregations,MatchEnd,MatchLabel,MatchResult,MatchStart,Matcher,Program,Save,Split,ThreadEquivalence}.java`, `main/java/io/trino/operator/window/PatternRecognitionPartition.java` (418 lines), `main/java/io/trino/operator/window/pattern/{LabelEvaluator,LogicalIndexNavigation,MeasureComputation}.java`, `main/java/io/trino/sql/planner/rowpattern/{IrRowPatternFlattener,IrPatternAlternationOptimizer,RowPatternToIrRewriter}.java`, `main/java/io/trino/sql/planner/iterative/rule/OptimizeRowPattern.java`, `test/java/io/trino/sql/query/TestRowPatternMatching.java` | GitHub contents API listings at `ref=483`; every raw URL returned HTTP 200; `TestRowPatternMatching.java` md5 `e6255d4849956b450e6d7ca0b29781db` |
| 4 | File path | Docker Hub tag `trinodb/trino:483` exists and is the locally pulled image | Hub API: `483 2026-07-18T02:05:13Z sha256:db58cc93…`; local image repo digest `sha256:db58cc93…` |
| 5 | Signature | Each project `Iverson.X` uses file-scoped `namespace Iverson.X;` | `IVectorRoles.cs:3`, `AuthorizationConstraint.cs:1` |
| 6 | Signature | Container fixture pattern: pinned image constant, `IAsyncLifetime`, `new ContainerBuilder().WithImage(..).WithPortBinding(port, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(port)).Build()`, readiness loop running a real query, `[CollectionDefinition]` + `ICollectionFixture`, `[Trait("Category","Integration")]` | `StarRocksIntegrationTests.cs:21-24,49-110,204-213` |
| 7 | Signature | Exceptions use a primary constructor `(string message) : Exception(message)` | `EngagementQueryTranslationException.cs:9` |
| 8 | Signature | `InternalsVisibleTo` is declared as `<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo"><_Parameter1>…</_Parameter1></AssemblyAttribute>` in 3 of 4 projects that grant it | `Iverson.Vector.csproj:10-12`, `Iverson.StarRocks.csproj:8`, `Iverson.Api.csproj:10,13` (the fourth, `Iverson.ClientConformance.csproj:18`, uses `<InternalsVisibleTo>`) |
| 9 | Signature | FluentAssertions idiom `act.Should().Throw<T>().WithMessage("*x*")` and `BeEquivalentTo` are in use | `StarRocksQueryBuilderTests.cs:256`; 76 `BeEquivalentTo(` call sites |
| 10 | Signature | Trino REST: `POST /v1/statement` with header `X-Trino-User`, follow `nextUri`, accumulate `data`; `columns` carries names and types; errors in `error.message`/`errorName`/`errorType`; bigint is an exact JSON number, varchar a string, boolean a JSON bool, double `NaN`/`Infinity` the strings `"NaN"`/`"Infinity"`, decimal a string | probe: `SELECT nan(), infinity(), CAST(NULL AS double), 'x', true, BIGINT '9007199254740993'` → `[["NaN","Infinity",null,"x",true,9007199254740993]]`; `SELECT 1 + 2.5` → `decimal(12, 1)` `"3.5"` |
| 11 | Signature | Trino is query-ready only once `SELECT 1` returns data (not when HTTP answers) | probe: first data after 11–12 two-second polls on a fresh container |
| 12 | Command | CI runs `dotnet build Iverson.slnx` then `dotnet test Iverson.slnx --filter "Category!=Integration"` | `.github/workflows/dotnet-build.yml:21-22` |
| 13 | Command | No `Directory.Build.props` and no `TreatWarningsAsErrors` anywhere | `ls`; `grep -rn TreatWarningsAsErrors` → none |
| 14 | Command | Commit messages are lowercase imperative sentences (`add …`, `fix …`, `correct …`) | `git log --format=%s -15` |
| 15 | Ordering | Task dependencies: 2→3; 4→5; {3,5}→6; {2–6}→7; 7→8. Tasks 2 and 4 are independent; Task 1 precedes all | each task's `Consumes` list |
| 16 | Code validity | Test packages: `Microsoft.NET.Test.Sdk` 17.12.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 2.8.2, `FluentAssertions` 8.11.0, `coverlet.collector` 10.0.1, `Testcontainers` 4.15.0 — the set every test project uses | all 8 `Iverson.Server/*Tests/*.csproj` read |
| 17 | Code validity | Trino integer `/` truncates toward zero and `%` takes the dividend's sign (C# semantics); integer `/0` and `%0` raise `DIVISION_BY_ZERO`; double `/0` is `Infinity`, `0.0/0` is `NaN`; bigint overflow raises `bigint addition overflow` | probe: `7/2,-7/2,7%3,-7%3` → `[3,-3,1,-1]`; `1/0`, `1%0` → `Division by zero`; `DOUBLE '1.0'/0` → `"Infinity"`; `DOUBLE '0.0'/0` → `"NaN"`; `BIGINT '9223372036854775807' + 1` → overflow error |
| 18 | Code validity | Trino `round` rounds half away from zero for decimal and double; `round(DOUBLE '0.125', 2)` = 0.13; `round(bigint)` stays bigint | probe → `["3","-3",3.0,-3.0,0.13,7,…]` |
| 19 | Code validity | Trino `ThreadEquivalence.reachableLabels` is a set union over a DFS with one `visited` array per start instruction, so its result is plain reachability and does not depend on visit order; an explicit stack computes the same sets | `ThreadEquivalence.java@483:207-247` read |
| 20 | Code validity | Trino `Matcher.advanceAndSchedule`'s recursive calls are all in tail position (nothing runs after them) and `SPLIT` forks before the first branch; an explicit stack that, on popping a `SPLIT`, forks and pushes `(forked, second)` then `(thread, first)` reproduces the recursion's order exactly | `Matcher.java@483:207-254` read |
| 21 | Code validity | Trino compiles a pattern as `IrPatternAlternationOptimizer.optimize(IrRowPatternFlattener.optimize(ir))` then `IrRowPatternToProgramRewriter.rewrite`; `*`→(0,∞), `+`→(1,∞), `?`→range 0..1, `{…}`→range | `OptimizeRowPattern.java@483:40`; `RowPatternToIrRewriter.java@483:100-116` |
| 22 | Code validity | A Python transliteration of the four passes (`trino_program.py`, Appendix A) produces the golden programs; its instruction counts match the independently measured CDR-7 counts: `PERMUTE` of 6 = 5,759, of 5 = 839, `A{10000}` = 10,001, `(A{0,100}){0,100}` = 20,101, `A{0,5000}` = 10,001, `A{4999}` = 5,000 | run output, 6/6 `OK` |
| 23 | Code validity | `PatternRecognitionPartition` (MATCH_RECOGNIZE mode) searches the whole partition (`searchStart = partitionStart`, `searchEnd = partitionEnd`), so `PREV`/`NEXT` may read any partition row even in `define`; `MATCH_NUMBER` increments on empty and non-empty matches only; empty-match measures are NULL except `MATCH_NUMBER()`; ALL ROWS skips excluded rows; `SKIP TO FIRST/LAST` resolves through a navigation and throws on "not present" or "first row of match" | `PatternRecognitionPartition.java@483:171-400`, `MeasureComputation.java@483` read |
| 24 | Code validity | Trino raises an illegal `AFTER MATCH SKIP` only when a match occurs, as `GENERIC_USER_ERROR` "AFTER MATCH SKIP failed: cannot skip to first row of match" / "… pattern variable is not present in match" | probe: `SKIP TO FIRST A` over `(A B)` → error with a match, `[]` without one |
| 25 | Code validity | Trino analyzer rules the tests assert: labels inside one navigation must match (qualified and unqualified may not mix); `FIRST(PREV(..))` rejected, `PREV(FIRST(..))` accepted; aggregate inside navigation and navigation inside aggregate rejected; aggregate labels must match; unqualified `SUM(x)` and `COUNT(*)` range over all rows; a navigation must contain a column reference or `CLASSIFIER()` (`PREV(1)`, `PREV(MATCH_NUMBER())` rejected); unknown qualifier, unknown `CLASSIFIER(v)`, unknown subset element, a `define` for an unused variable and a subset named like a variable are rejected; a non-boolean `define` is rejected | 22-case probe run, messages recorded in Tasks 4 and 7 |
| 26 | Code validity | Trino returns a partition's rows in a stable order but the order of partitions varies between runs; unmatched rows carry NULL match number and classifier | same query run 4×: partition `x` first once, `y` first 3×; per-partition order identical |
| 27 | Code validity | The oracle's ported-case file is reproducible: `extract_sites.py` (Appendix B) evaluates all 141 `assertions.query` sites of `TestRowPatternMatching.java@483`; `structure_sites.py` structures 130 and excludes 11 (6 subquery, 1 quoted identifier, 1 `CAST`, 1 multiple `MATCH_RECOGNIZE`, 1 `EXISTS`, 1 `lower`); re-rendering each structured case and running it on Trino reproduces the original result multiset in 130/130 (7 are expected errors: 2 runtime skip failures, 5 unknown-qualifier compile failures); no case has an `ORDER BY` tie; `cases.json` md5 `3a778e9c636f004f720acc243d7dbefa` | run output (Python 3.14.4) |
| 28 | Consumer impact | Adding the two projects to `Iverson.slnx` adds them to CI's build and unit-test run; the oracle tests are excluded there by the `Integration` trait, and CodeQL's autobuild over `Iverson.slnx` compiles one more reference-free library | CI command (row 12); the projects reference nothing |
| 29 | Code validity | Trino 483 grammar and prefix rules the Task 2 and Task 4 tests assert: `A**` and `A |` rejected; `A{,}` accepted; `PERMUTE(A)` accepted as the variable `PERMUTE` followed by `(A)` (a permutation needs a top-level comma, as `PERMUTE()` and `PERMUTE((A))` also show); an upper bound of 0 rejected ("Pattern quantifier upper bound must be greater than or equal to 1"); `A{3,2}` rejected; `FIRST(LAST(..))`, `PREV(PREV(..))`, `PREV(NEXT(..))` rejected ("Cannot nest …"); `PREV(FIRST(A.x) + 1)` rejected ("Immediate nesting is required …"); `RUNNING`/`FINAL` accepted only on `FIRST`, `LAST` and aggregates (`PREV`, `NEXT`, `CLASSIFIER`, `MATCH_NUMBER` rejected; `FINAL A.x` a syntax error; `PREV(FINAL LAST(x))` accepted); a navigation offset must be a non-negative integer literal; duplicate `define` names rejected in any case; a duplicate subset element accepted; nested aggregates rejected; `COUNT(CLASSIFIER())`, `SUM(MATCH_NUMBER())`, `CLASSIFIER(U)` for a subset accepted; `FINAL` in `DEFINE` rejected; `running`/`final` are ordinary columns or qualifiers unless directly followed by a call (`final > 0`, `A.final > 0`, `running + 1` accepted) | 34-case probe run (V1–V24) against Trino 483, each result recorded; CIR-1 probes P26, P27, P29, P31, P53, re-confirmed on Trino 483 when these fixes were applied |
| 30 | Code validity | The Task 8 fixture starts `trinodb/trino:483` under Testcontainers 4.15.0 on this host's podman socket with the unqualified tag and a parameterless `ContainerBuilder()` | CIR-1 probe P7 |
| 31 | Code validity | The FluentAssertions 8.11 idioms the tests rely on behave as needed: `.Should().Be(double.NaN)` on `object` and `double`, and `BeEquivalentTo(…, o => o.WithStrictOrdering().ComparingByMembers<MatchOutputRow>())` | CIR-1 probes P14, P2 (every test file compiles) |
| 32 | Code validity | The 130 ported cases stay inside Task 2's pattern grammar and Task 4's expression grammar (function names, literal classes, all patterns) | CIR-1 probes P17, P32 |
| 33 | Code validity | Trino 483's empty-match measures: `COUNT(*)` 0, `SUM`/`LAST`/`CLASSIFIER()` NULL, `MATCH_NUMBER()` the match number — which the generator's measures meet | CIR-1 probe P15 |
| 34 | Consumer impact | Task 1's `.slnx` edits leave the server image build intact before Plan 2: the plan's only `ProjectReference` is the test project's, no repository `.csproj` names `Iverson.Patterns`, and no Dockerfile restores a solution file | CIR-1 probes P45, P22 |
| 35 | Code validity | `(A?){2499} B` compiles to exactly 5,000 instructions and drives `advanceAndSchedule` to recursion depth 2,500 (`A{0,2499} B` and `A{4999}` reach at most 2); a recursive scheduler overflows a 256 KB stack on it while the explicit-stack code completes | CIR-1 probes P9, P10, P47; Appendix A re-run when this fix was applied |

## Tasks

### Task 1: Project skeleton and public contract types

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj`, `PatternRequest.cs`, `PatternExceptions.cs`, `PatternBudget.cs`, `MatchOutputRow.cs`
- Create: `Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj`, `PatternBudgetTests.cs`
- Create: `THIRD-PARTY-NOTICES.md`
- Modify: `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx`

**Interfaces:**
- Produces: every public type below except `PatternQuery`/`CompiledPattern` (Task 7). `PatternBudget.Step()` and `PatternBudget.CheckActiveThreads(int)` are consumed by Task 6.

- [ ] **Step 1: Create the library project**

`Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>Iverson.Patterns.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the public contract types**

`Iverson.Server/Iverson.Patterns/PatternRequest.cs`:
```csharp
namespace Iverson.Patterns;

public enum PatternSource { TypeRows, Chunks }

public enum RowsPerMatch { OneRow, AllRowsShowEmpty, AllRowsOmitEmpty, AllRowsWithUnmatched }

public enum AfterMatchSkipKind { PastLastRow, ToNextRow, ToFirst, ToLast }

public sealed record NamedExpression(string Name, string Expression);

public sealed record SubsetDefinition(string Name, IReadOnlyList<string> Variables);

/// <summary>
/// Everything <c>PatternQuery.Compile</c> needs from a MatchPattern request (spec §1–§2). The Api maps the
/// proto onto this in Plan 2; the engine never sees the proto. <see cref="PartitionBy"/> is carried because a
/// <c>ONE_ROW</c> output row starts with the partition columns, and <see cref="TenantColumn"/> because
/// <c>Compile</c> owns the reserved-tenant-name check on <c>measures</c> names.
/// </summary>
public sealed record PatternRequest(
    PatternSource Source,
    IReadOnlyList<string> PartitionBy,
    string Pattern,
    IReadOnlyList<SubsetDefinition> Subsets,
    IReadOnlyList<NamedExpression> Define,
    IReadOnlyList<NamedExpression> Measures,
    RowsPerMatch RowsPerMatch,
    AfterMatchSkipKind AfterMatchSkip,
    string SkipVariable,
    string? TenantColumn);
```

`Iverson.Server/Iverson.Patterns/PatternExceptions.cs`:
```csharp
namespace Iverson.Patterns;

/// <summary>A pattern, subset, <c>define</c> or <c>measures</c> entry is invalid (spec §1–§2). Maps to <c>InvalidArgument</c>.</summary>
public sealed class PatternValidationException(string message) : Exception(message);

/// <summary>A run-time evaluation error: integer division by zero, overflow, a type mismatch, an illegal
/// <c>AFTER MATCH SKIP</c> (spec §1–§2). Maps to <c>InvalidArgument</c>.</summary>
public sealed class PatternEvaluationException(string message) : Exception(message);

/// <summary>A §5 budget was exceeded. <see cref="BudgetName"/> is the limit's name, e.g. <c>MaxSteps</c>.</summary>
public sealed class PatternBudgetExceededException(string budgetName)
    : Exception($"Pattern budget exceeded: {budgetName}.")
{
    public string BudgetName { get; } = budgetName;
}
```

`Iverson.Server/Iverson.Patterns/PatternBudget.cs`:
```csharp
namespace Iverson.Patterns;

/// <summary>
/// The matcher's share of the §5 limits. <see cref="MaxSteps"/> spans the whole request (one instance per
/// request); <see cref="MaxActiveThreads"/> bounds the live threads of one match attempt. A step is one
/// program instruction processed by the matcher or one <c>define</c> evaluation.
/// </summary>
public sealed class PatternBudget(int maxActiveThreads, long maxSteps)
{
    public int MaxActiveThreads { get; } = maxActiveThreads;
    public long MaxSteps { get; } = maxSteps;
    public long StepsUsed { get; private set; }

    internal void Step()
    {
        if (++StepsUsed > MaxSteps)
            throw new PatternBudgetExceededException(nameof(MaxSteps));
    }

    internal void CheckActiveThreads(int activeThreads)
    {
        if (activeThreads > MaxActiveThreads)
            throw new PatternBudgetExceededException(nameof(MaxActiveThreads));
    }
}
```

`Iverson.Server/Iverson.Patterns/MatchOutputRow.cs`:
```csharp
namespace Iverson.Patterns;

/// <summary>
/// One output row. <see cref="Data"/> is keyed ordinally (spec §1): <c>ONE_ROW</c> holds the partition columns
/// then the measures; <c>ALL_ROWS_*</c> holds every input column then the measures. <see cref="MatchNumber"/> is 0
/// for an unmatched row; <see cref="Classifier"/> is the row's upper-case label for a matched <c>ALL_ROWS_*</c>
/// row and empty otherwise.
/// </summary>
public sealed record MatchOutputRow(IReadOnlyDictionary<string, object?> Data, long MatchNumber, string Classifier);

/// <summary>A distinct <c>SIMILARITY(col, 'text')</c> term, as written (spec §3.3 embeds <see cref="Text"/> once).</summary>
public sealed record SimilarityTerm(string Column, string Text);
```

- [ ] **Step 3: Create the test project**

`Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="8.11.0" />
    <PackageReference Include="coverlet.collector" Version="10.0.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Iverson.Patterns/Iverson.Patterns.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the failing budget tests**

`Iverson.Server/Iverson.Patterns.Tests/PatternBudgetTests.cs`:
```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class PatternBudgetTests
{
    [Fact]
    public void Steps_up_to_the_limit_are_counted_and_the_next_one_throws()
    {
        var budget = new PatternBudget(maxActiveThreads: 10, maxSteps: 3);

        budget.Step(); budget.Step(); budget.Step();
        budget.StepsUsed.Should().Be(3);

        var act = budget.Step;
        act.Should().Throw<PatternBudgetExceededException>()
            .Which.BudgetName.Should().Be("MaxSteps");
    }

    [Fact]
    public void Active_threads_at_the_limit_pass_and_one_more_throws()
    {
        var budget = new PatternBudget(maxActiveThreads: 2, maxSteps: 100);

        budget.CheckActiveThreads(2);

        var act = () => budget.CheckActiveThreads(3);
        act.Should().Throw<PatternBudgetExceededException>()
            .Which.BudgetName.Should().Be("MaxActiveThreads");
    }
}
```

- [ ] **Step 5: Register both projects in the two solution files**

In `Iverson.Server/Iverson.Server.slnx`, add after the `Iverson.LoadTest.Tests` line:
```xml
  <Project Path="Iverson.Patterns/Iverson.Patterns.csproj" />
  <Project Path="Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj" />
```
In `Iverson.slnx`, inside `<Folder Name="/Iverson.Server/">`, add after the `Iverson.LoadTest` line:
```xml
    <Project Path="Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj" />
    <Project Path="Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj" />
```

- [ ] **Step 6: Create `THIRD-PARTY-NOTICES.md`**

```markdown
# Third-party notices

## Trino

`Iverson.Server/Iverson.Patterns` contains C# ports of source files from Trino 483
(https://github.com/trinodb/trino, tag `483`), used under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0). Each ported file names its Trino origin in its header.
Ported: `io.trino.operator.window.matcher` (`Matcher`, `ThreadEquivalence`, `Captures`, `IntList`,
`IntStack`, `IntMultimap`, `ArrayView`, `MatchResult`, `IrRowPatternToProgramRewriter`),
`io.trino.operator.window.PatternRecognitionPartition`, `io.trino.operator.window.pattern.LogicalIndexNavigation`,
and `io.trino.sql.planner.rowpattern` (`IrRowPatternFlattener`, `IrPatternAlternationOptimizer`).
Modifications: rewritten in C#; recursion replaced by explicit stacks; aggregations recomputed from matched
labels instead of carried per thread.
```

- [ ] **Step 7: Build and run the tests**

Run: `dotnet build Iverson.slnx && dotnet test Iverson.Server/Iverson.Patterns.Tests`
Expected: build succeeds; 2 tests pass.

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Patterns Iverson.Server/Iverson.Patterns.Tests THIRD-PARTY-NOTICES.md Iverson.slnx Iverson.Server/Iverson.Server.slnx
git commit -m "add the Iverson.Patterns project skeleton and its public contract types"
```

### Task 2: `PatternParser` — the §1 grammar

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/Syntax/PatternNode.cs`, `Iverson.Server/Iverson.Patterns/Syntax/PatternParser.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/PatternParserTests.cs`

**Interfaces:**
- Consumes: `SubsetDefinition`, `PatternValidationException` (Task 1).
- Produces: `PatternNode` (and subtypes), `ParsedPattern`, `PatternParser.Parse` — consumed by Tasks 3 and 7.

- [ ] **Step 1: Create the AST**

`Iverson.Server/Iverson.Patterns/Syntax/PatternNode.cs` — each node's `ToString()` is its canonical form, which the tests compare against:
```csharp
namespace Iverson.Patterns.Syntax;

internal abstract record PatternNode;

internal sealed record LabelNode(string Name) : PatternNode
{
    public override string ToString() => Name;
}

internal sealed record EmptyNode : PatternNode
{
    public override string ToString() => "()";
}

internal sealed record AnchorNode(bool PartitionStart) : PatternNode
{
    public override string ToString() => PartitionStart ? "^" : "$";
}

internal sealed record ExclusionNode(PatternNode Pattern) : PatternNode
{
    public override string ToString() => $"{{- {Pattern} -}}";
}

internal sealed record AlternationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "(" + string.Join(" | ", Patterns) + ")";
}

internal sealed record ConcatenationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "(" + string.Join(" ", Patterns) + ")";
}

internal sealed record PermutationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "PERMUTE(" + string.Join(", ", Patterns) + ")";
}

/// <summary><see cref="Max"/> null = unbounded. <c>*</c> is (0, null), <c>+</c> (1, null), <c>?</c> (0, 1).</summary>
internal sealed record QuantifiedNode(PatternNode Pattern, int Min, int? Max, bool Greedy) : PatternNode
{
    public override string ToString() =>
        $"{Pattern}{{{Min},{(Max is { } max ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}}}{(Greedy ? "" : "?")}";
}

/// <param name="Variables">Primary pattern variables, upper case, in order of first appearance.</param>
/// <param name="Subsets">Upper-case subset name → its upper-case member variables.</param>
internal sealed record ParsedPattern(
    PatternNode Root,
    IReadOnlyList<string> Variables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Subsets,
    bool HasExclusion);
```

- [ ] **Step 2: Write the failing parser tests**

`Iverson.Server/Iverson.Patterns.Tests/PatternParserTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class PatternParserTests
{
    private static ParsedPattern Parse(string pattern, params SubsetDefinition[] subsets) =>
        PatternParser.Parse(pattern, subsets);

    [Theory]
    [InlineData("A B+ C+", "(A B{1,} C{1,})")]
    [InlineData("a | b c", "(A | (B C))")]
    [InlineData("A (B C)", "(A (B C))")]
    [InlineData("(A)", "A")]
    [InlineData("()", "()")]
    [InlineData("^ A* $", "(^ A{0,} $)")]
    [InlineData("A*?", "A{0,}?")]
    [InlineData("A+?", "A{1,}?")]
    [InlineData("A?", "A{0,1}")]
    [InlineData("A??", "A{0,1}?")]
    [InlineData("A{2}", "A{2,2}")]
    [InlineData("A{2,}", "A{2,}")]
    [InlineData("A{,2}", "A{0,2}")]
    [InlineData("A{ 1 , 3 }?", "A{1,3}?")]
    [InlineData("A{,}", "A{0,}")]
    [InlineData("PERMUTE(a, b | c)", "PERMUTE(A, (B | C))")]
    [InlineData("permute(A)", "(PERMUTE A)")]
    [InlineData("PERMUTE()", "(PERMUTE ())")]
    [InlineData("A {- B -} C", "(A {- B -} C)")]
    [InlineData("{- A {- B -} -}", "{- (A {- B -}) -}")]
    [InlineData("(() | A)", "(() | A)")]
    [InlineData("^* | B", "(^{0,} | B)")]
    [InlineData("(A+)+ B", "(A{1,}{1,} B)")]
    [InlineData("A B | C D | E", "((A B) | (C D) | E)")]
    public void Parses_to_the_canonical_tree(string pattern, string expected) =>
        Parse(pattern).Root.ToString().Should().Be(expected);

    [Fact]
    public void Variables_are_upper_cased_deduplicated_and_in_first_appearance_order() =>
        Parse("b a+ | B c PERMUTE(c, d)").Variables.Should().Equal("B", "A", "C", "D");

    [Fact]
    public void Permute_is_a_keyword_only_before_a_parenthesis() =>
        Parse("PERMUTE LAST").Variables.Should().Equal("PERMUTE", "LAST");

    [Fact]
    public void Subsets_resolve_case_insensitively_to_upper_case()
    {
        var parsed = Parse("A B C", new SubsetDefinition("u", ["a", "C"]));

        parsed.Subsets.Should().ContainKey("U");
        parsed.Subsets["U"].Should().Equal("A", "C");
    }

    [Fact]
    public void HasExclusion_reports_an_exclusion_anywhere()
    {
        Parse("A B").HasExclusion.Should().BeFalse();
        Parse("A (B | {- C -})").HasExclusion.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A |")]
    [InlineData("| A")]
    [InlineData("A**")]
    [InlineData("A+*")]
    [InlineData("A{3,2}")]
    [InlineData("A{0}")]
    [InlineData("A{,0}")]
    [InlineData("A{0,0}")]
    [InlineData("A{")]
    [InlineData("A{x}")]
    [InlineData("A{99999999999}")]
    [InlineData("(A")]
    [InlineData("A)")]
    [InlineData("{- A")]
    [InlineData("PERMUTE(A,)")]
    [InlineData("A ; B")]
    [InlineData("A.B")]
    public void Rejects_malformed_patterns(string pattern)
    {
        var act = () => Parse(pattern);
        act.Should().Throw<PatternValidationException>();
    }

    [Fact]
    public void Rejects_a_subset_element_that_is_not_a_primary_variable()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", ["A", "C"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*C*");
    }

    [Fact]
    public void Rejects_a_subset_named_like_a_primary_variable_in_any_case()
    {
        var act = () => Parse("A B", new SubsetDefinition("a", ["B"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*A*");
    }

    [Fact]
    public void Rejects_duplicate_subset_names_in_any_case()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", ["A"]), new SubsetDefinition("u", ["B"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*U*");
    }

    [Fact]
    public void Rejects_a_subset_with_no_members()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", []));
        act.Should().Throw<PatternValidationException>();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~PatternParserTests"`
Expected: FAIL — `PatternParser` does not exist.

- [ ] **Step 4: Implement `PatternParser`**

`Iverson.Server/Iverson.Patterns/Syntax/PatternParser.cs` — `internal static class PatternParser` with
`public static ParsedPattern Parse(string pattern, IReadOnlyList<SubsetDefinition> subsets)`. Requirements (each pinned by a Step 2 test):

- **Tokens** (whitespace skipped): identifier `[A-Za-z_][A-Za-z0-9_]*`; `{-`; `-}`; a quantifier brace `{` digits? (`,` digits?)? `}` with optional inner whitespace; `(` `)` `|` `^` `$` `,` `*` `+` `?`. Any other character → `PatternValidationException("Unexpected character '…' at position … in pattern.")`. A bound that does not fit in `int` is rejected.
- **Grammar** (the SQL:2016 row-pattern grammar as Trino 483 accepts it):
  ```
  alternation   := concatenation ('|' concatenation)*
  concatenation := quantified+
  quantified    := primary quantifier?
  quantifier    := ('*' | '+' | '?' | '{' n? (',' m?)? '}') '?'?
  primary       := IDENT | '(' ')' | '(' alternation ')' | '{-' alternation '-}' | '^' | '$'
                 | PERMUTE '(' alternation (',' alternation)+ ')'
  ```
  At most one quantifier per primary (`A**` is an error). `{n}` = (n, n); `{n,}` = (n, ∞); `{,m}` = (0, m); `{,}` = (0, ∞); `min > max` is an error, and so is an upper bound of 0 (`{0}`, `{,0}`, `{0,0}`), as in Trino 483 ("Pattern quantifier upper bound must be greater than or equal to 1"). `PERMUTE` (any case) is the permutation keyword only when the next token is `(` and those parentheses hold a top-level comma, as in Trino 483; otherwise it is an identifier, so `PERMUTE()` is the variable `PERMUTE` then `()`, and `PERMUTE(x)` the variable then `(x)`. A parenthesized single alternation returns its inner node; `()` is `EmptyNode`. A one-element sequence is returned unwrapped (no one-element `ConcatenationNode`/`AlternationNode`). The whole input must be consumed.
- **Variables:** every `IDENT` primary becomes `LabelNode(name.ToUpperInvariant())`; `Variables` lists them deduplicated in first-appearance order.
- **Subsets:** each name is upper-cased; it must not equal a primary variable, must not repeat another subset name, must have at least one member, and every member (upper-cased) must be a primary variable. Violations throw `PatternValidationException` naming the offending (upper-case) name.
- **`HasExclusion`** is true when any `ExclusionNode` occurs.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~PatternParserTests"`
Expected: PASS (all cases).

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/Syntax Iverson.Server/Iverson.Patterns.Tests/PatternParserTests.cs
git commit -m "add the row-pattern parser for the MatchPattern grammar"
```

### Task 3: `ProgramCompiler` — optimizer passes, emission and the `MaxProgramInstructions` cap

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/Compilation/Instruction.cs`, `Compilation/PatternOptimizer.cs`, `Compilation/ProgramCompiler.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/ProgramCompilerTests.cs`

**Interfaces:**
- Consumes: `PatternNode` family, `PatternParser` (Task 2); `PatternValidationException` (Task 1).
- Produces: `InstructionKind`, `Instruction`, `ProgramCompiler.Compile` — consumed by Tasks 6 and 7.

**Port sources (Trino tag 483):** `sql/planner/rowpattern/IrRowPatternFlattener.java` (whole file), `sql/planner/rowpattern/IrPatternAlternationOptimizer.java` (whole file), `operator/window/matcher/IrRowPatternToProgramRewriter.java` (whole file); pipeline order from `sql/planner/iterative/rule/OptimizeRowPattern.java:40`. Fetch with `curl -sO https://raw.githubusercontent.com/trinodb/trino/483/core/trino-main/src/main/java/io/trino/<path>` into a scratch directory (not the repo).

**Deliberate deviations from the port source** (spec §3.1):
1. Trino emits a `Save` at each end of an exclusion. After the flattener removes nested exclusions they always come in non-nested pairs, so this port emits `ExclusionStart` then `ExclusionEnd` in their place (the spec's instruction names); there is no separate `Save` kind, because nothing else emits one. Counts are unchanged.
2. Trino materializes `orderedPermutations` before emitting; this port generates the orderings one at a time in the same lexicographic index order (next-permutation), so a `PERMUTE` that exceeds the cap stops after at most `maxInstructions` emissions.
3. Every append — `Done` included — checks the cap first and throws `PatternValidationException` naming `MaxProgramInstructions` when the program would exceed it.

- [ ] **Step 1: Create the instruction model**

`Iverson.Server/Iverson.Patterns/Compilation/Instruction.cs`:
```csharp
namespace Iverson.Patterns.Compilation;

internal enum InstructionKind { MatchLabel, Split, Jump, ExclusionStart, ExclusionEnd, MatchStart, MatchEnd, Done }

/// <summary>
/// One program instruction (port of Trino 483 <c>io.trino.operator.window.matcher.Instruction</c> and subclasses).
/// <c>MatchLabel</c>: <see cref="First"/> is the label index. <c>Split</c>: <see cref="First"/> is the preferred
/// target, <see cref="Second"/> the other. <c>Jump</c>: <see cref="First"/> is the target.
/// </summary>
internal readonly record struct Instruction(InstructionKind Kind, int First = 0, int Second = 0);
```

- [ ] **Step 2: Write the failing compiler tests**

`Iverson.Server/Iverson.Patterns.Tests/ProgramCompilerTests.cs` — the golden programs were produced by the Trino 483 transliteration in Appendix A (exclusion `save` pairs renamed as in deviation 1):
```csharp
using System.Diagnostics;
using FluentAssertions;
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class ProgramCompilerTests
{
    internal static Instruction[] CompileText(string pattern, int maxInstructions = 5000)
    {
        var parsed = PatternParser.Parse(pattern, []);
        var labels = parsed.Variables.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        return ProgramCompiler.Compile(parsed.Root, labels, maxInstructions);
    }

    internal static string Format(Instruction[] program, IReadOnlyList<string> labelNames) =>
        string.Join(" | ", program.Select(i => i.Kind switch
        {
            InstructionKind.MatchLabel => "label " + labelNames[i.First],
            InstructionKind.Split => $"split {i.First} {i.Second}",
            InstructionKind.Jump => $"jump {i.First}",
            InstructionKind.ExclusionStart => "exclusion-start",
            InstructionKind.ExclusionEnd => "exclusion-end",
            InstructionKind.MatchStart => "start",
            InstructionKind.MatchEnd => "end",
            InstructionKind.Done => "done",
            _ => throw new ArgumentOutOfRangeException(nameof(program)),
        }));

    [Theory]
    [InlineData("A B C", "label A | label B | label C | done")]
    [InlineData("A | B | C", "split 1 3 | label A | jump 7 | split 4 6 | label B | jump 7 | label C | done")]
    [InlineData("(A B)+", "label A | label B | split 0 3 | done")]
    [InlineData("()", "done")]
    [InlineData("A*", "split 1 3 | label A | split 1 3 | done")]
    [InlineData("A*?", "split 3 1 | label A | split 3 1 | done")]
    [InlineData("A+", "label A | split 0 2 | done")]
    [InlineData("A+?", "label A | split 2 0 | done")]
    [InlineData("A?", "split 1 2 | label A | done")]
    [InlineData("A??", "split 2 1 | label A | done")]
    [InlineData("A{2}", "label A | label A | done")]
    [InlineData("A{2,}", "label A | label A | split 1 3 | done")]
    [InlineData("A{,2}", "split 1 4 | label A | split 3 4 | label A | done")]
    [InlineData("A{1,3}", "label A | split 2 5 | label A | split 4 5 | label A | done")]
    [InlineData("A{1,3}?", "label A | split 5 2 | label A | split 5 4 | label A | done")]
    [InlineData("A{,}", "split 1 3 | label A | split 1 3 | done")]
    [InlineData("PERMUTE(A, B, C)",
        "split 1 5 | label A | label B | label C | jump 28 | split 6 10 | label A | label C | label B | jump 28 | " +
        "split 11 15 | label B | label A | label C | jump 28 | split 16 20 | label B | label C | label A | jump 28 | " +
        "split 21 25 | label C | label A | label B | jump 28 | label C | label B | label A | done")]
    [InlineData("A {- B -} C", "label A | exclusion-start | label B | exclusion-end | label C | done")]
    [InlineData("{- A {- B -} -}", "exclusion-start | label A | label B | exclusion-end | done")]
    [InlineData("^ A $", "start | label A | end | done")]
    [InlineData("(() | A)", "split 2 1 | label A | done")]
    [InlineData("(A | ())", "split 1 2 | label A | done")]
    [InlineData("A () B", "label A | label B | done")]
    [InlineData("PERMUTE(A, ())", "label A | done")]
    [InlineData("(A | B)* X", "split 1 6 | split 2 4 | label A | jump 5 | label B | split 1 6 | label X | done")]
    [InlineData("(A+)+ B", "label A | split 0 2 | split 0 3 | label B | done")]
    public void Compiles_to_the_Trino_483_program(string pattern, string expected)
    {
        var parsed = PatternParser.Parse(pattern, []);
        Format(CompileText(pattern), parsed.Variables).Should().Be(expected);
    }

    [Fact]
    public void A_program_of_exactly_the_cap_compiles()
    {
        CompileText("A{4999}", maxInstructions: 5000).Should().HaveCount(5000);
        CompileText("A{0,2499} B", maxInstructions: 5000).Should().HaveCount(5000);
    }

    [Theory]
    [InlineData("A{5000}")]
    [InlineData("PERMUTE(A, B, C, D, E, F, G, H)")]
    [InlineData("PERMUTE(A, B, C, D, E, F, G, H, I, J, K, L)")]
    [InlineData("A{100000}")]
    [InlineData("(A{0,100}){0,100}")]
    public void A_program_over_the_cap_is_rejected_promptly(string pattern)
    {
        var watch = Stopwatch.StartNew();
        var act = () => CompileText(pattern, maxInstructions: 5000);

        act.Should().Throw<PatternValidationException>().WithMessage("*MaxProgramInstructions*");
        // PERMUTE of 12 is 6,706,022,399 instructions uncapped; a rejection after at most the cap takes
        // milliseconds (CDR-7 P84: 2–11 ms).
        watch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ProgramCompilerTests"`
Expected: FAIL — `ProgramCompiler` does not exist.

- [ ] **Step 4: Implement `PatternOptimizer`**

`Iverson.Server/Iverson.Patterns/Compilation/PatternOptimizer.cs` — `internal static class PatternOptimizer` with `public static PatternNode Optimize(PatternNode root)` = `AlternationOptimizer(Flatten(root, inExclusion: false))`, a line-for-line port of the two Trino visitors onto the Task 2 records (`IrLabel`→`LabelNode`, `IrEmpty`→`EmptyNode`, `IrAnchor`→`AnchorNode`, `IrExclusion`→`ExclusionNode`, `IrAlternation`→`AlternationNode`, `IrConcatenation`→`ConcatenationNode`, `IrPermutation`→`PermutationNode`, `IrQuantified`→`QuantifiedNode`; `zeroOrOne(greedy)` → `new QuantifiedNode(p, 0, 1, greedy)`). Header comment: `// Port of Trino 483 IrRowPatternFlattener and IrPatternAlternationOptimizer (Apache-2.0); see THIRD-PARTY-NOTICES.md.` Recursion here is bounded by the parsed pattern's depth, which `MaxPatternLength` (1,000 characters) bounds.

- [ ] **Step 5: Implement `ProgramCompiler`**

`Iverson.Server/Iverson.Patterns/Compilation/ProgramCompiler.cs` — `internal static class ProgramCompiler` with
`public static Instruction[] Compile(PatternNode root, IReadOnlyDictionary<string, int> labelIndexes, int maxInstructions)`: optimize, then port `IrRowPatternToProgramRewriter.Rewriter` (placeholder-then-patch emission for `Split`/`Jump`; `loopingQuantified`, `loop`, `rangeQuantified` exactly as in Trino) onto a `List<Instruction>`, then append `Done`. All appends go through:
```csharp
private void Append(Instruction instruction)
{
    if (_instructions.Count + 1 > _maxInstructions)
        throw new PatternValidationException(
            $"The compiled pattern exceeds MaxProgramInstructions ({_maxInstructions} instructions).");
    _instructions.Add(instruction);
}
```
(placeholders are appended as `default` through `Append`, then overwritten by index). `PermutationNode` → an alternation whose parts are the orderings of its children, generated lazily in lexicographic order of child indexes (`0,1,2` → `0,2,1` → `1,0,2` → … — Trino's `orderedPermutations`), each part being the concatenation of the ordered children; the last ordering is the one with no next permutation, and only it gets no `Split`/`Jump`. `ExclusionNode` → `ExclusionStart`, pattern, `ExclusionEnd`. Header comment as in Step 4, naming `IrRowPatternToProgramRewriter`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ProgramCompilerTests"`
Expected: PASS.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/Compilation Iverson.Server/Iverson.Patterns.Tests/ProgramCompilerTests.cs
git commit -m "add the capped pattern-program compiler ported from Trino 483"
```

### Task 4: `ExpressionParser` — the §2 grammar lowered to Trino's navigation model

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/ArrayView.cs`, `Expressions/Navigation.cs`, `Expressions/Expr.cs`, `Expressions/SimilarityTermTable.cs`, `Expressions/ExpressionLexer.cs`, `Expressions/ExpressionParser.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/ExpressionParserTests.cs`

**Interfaces:**
- Consumes: `SimilarityTerm`, `PatternValidationException` (Task 1).
- Produces: `ArrayView`, `Navigation`, the `Expr` family, `SimilarityTermTable`, `ExpressionParser` — consumed by Tasks 5, 6 and 7.

**Port source:** `operator/window/pattern/LogicalIndexNavigation.java@483` (`resolvePosition`, `findLastAndBackwards`, `findFirstAndForward`, `adjustPosition`). **Lowering model** (Trino's): every value read goes through a `Navigation` — label set (empty = the universal row pattern variable), `LAST`/`FIRST`, `RUNNING`/`FINAL`, logical offset, physical offset. `A.x` ≡ `LAST(A.x)`; unqualified `x` ≡ `LAST(x)` over the universal variable, which is the current row in `define` and under `RUNNING`; `PREV(e, n)`/`NEXT(e, n)` add a physical offset of `-n`/`+n` (default 1); `FIRST(e, n)`/`LAST(e, n)` set the logical offset. Inside an aggregate's argument, a read has no navigation (`null`): it reads the row being aggregated.

- [ ] **Step 1: Create the view, navigation and IR types**

`Iverson.Server/Iverson.Patterns/ArrayView.cs`:
```csharp
namespace Iverson.Patterns;

/// <summary>A read-only view over the first <see cref="Length"/> items of an int array (port of Trino 483
/// <c>io.trino.operator.window.matcher.ArrayView</c>, Apache-2.0; see THIRD-PARTY-NOTICES.md).</summary>
internal readonly struct ArrayView(int[] array, int length)
{
    public static readonly ArrayView Empty = new([], 0);

    public int Length { get; } = length;

    public int this[int index] =>
        (uint)index < (uint)Length ? array[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public int[] ToArray() => array.AsSpan(0, Length).ToArray();
}
```

`Iverson.Server/Iverson.Patterns/Expressions/Navigation.cs`:
```csharp
namespace Iverson.Patterns.Expressions;

/// <summary>
/// Where a value read lands in the partition (port of Trino 483
/// <c>io.trino.operator.window.pattern.LogicalIndexNavigation</c>, Apache-2.0; see THIRD-PARTY-NOTICES.md).
/// <see cref="Labels"/> is sorted and distinct; empty means the universal row pattern variable.
/// Equality is structural, because <c>ThreadEquivalence</c> collects navigations in sets.
/// </summary>
internal sealed record Navigation(int[] Labels, bool Last, bool Running, int LogicalOffset, int PhysicalOffset)
{
    /// <returns>A position in the partition, or -1 when the navigation lands outside the match
    /// (logically) or outside <c>[searchStart, searchEnd)</c> (physically).</returns>
    public int ResolvePosition(int currentRow, ArrayView matchedLabels, int searchStart, int searchEnd, int patternStart)
    {
        int patternEndInclusive = Running ? currentRow - patternStart : matchedLabels.Length - 1;
        int relative = Last ? FindLastAndBackwards(patternEndInclusive, matchedLabels)
                            : FindFirstAndForward(patternEndInclusive, matchedLabels);
        if (relative == -1)
            return -1;
        int target = relative + patternStart + PhysicalOffset;
        return target < searchStart || target >= searchEnd ? -1 : target;
    }

    private int FindLastAndBackwards(int patternEndInclusive, ArrayView matchedLabels)
    {
        int position = patternEndInclusive + 1, found = 0;
        while (found <= LogicalOffset && position > 0)
        {
            position--;
            if (Matches(matchedLabels[position])) found++;
        }
        return found == LogicalOffset + 1 ? position : -1;
    }

    private int FindFirstAndForward(int patternEndInclusive, ArrayView matchedLabels)
    {
        int position = -1, found = 0;
        while (found <= LogicalOffset && position < patternEndInclusive)
        {
            position++;
            if (Matches(matchedLabels[position])) found++;
        }
        return found == LogicalOffset + 1 ? position : -1;
    }

    private bool Matches(int label) => Labels.Length == 0 || Array.BinarySearch(Labels, label) >= 0;

    public Navigation WithLogicalOffset(int offset) => this with { LogicalOffset = offset };
    public Navigation WithPhysicalOffset(int offset) => this with { PhysicalOffset = offset };

    public bool Equals(Navigation? other) =>
        other is not null && Last == other.Last && Running == other.Running &&
        LogicalOffset == other.LogicalOffset && PhysicalOffset == other.PhysicalOffset &&
        Labels.AsSpan().SequenceEqual(other.Labels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Last); hash.Add(Running); hash.Add(LogicalOffset); hash.Add(PhysicalOffset);
        foreach (var label in Labels) hash.Add(label);
        return hash.ToHashCode();
    }
}
```

`Iverson.Server/Iverson.Patterns/Expressions/Expr.cs`:
```csharp
namespace Iverson.Patterns.Expressions;

internal enum BinaryOp { Add, Subtract, Multiply, Divide, Modulo, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, And, Or }
internal enum UnaryOp { Negate, Not }
internal enum AggregateKind { Count, Sum, Avg, Min, Max }
internal enum ScalarFunction { Coalesce, NullIf, Round, Abs }
internal enum TimeUnit { Second, Minute, Hour, Day }

internal abstract record Expr;

/// <summary><see cref="Value"/> is <c>long</c>, <c>double</c>, <c>string</c>, <c>bool</c> or <c>null</c>.</summary>
internal sealed record LiteralExpr(object? Value) : Expr;

/// <summary>A column read. <see cref="Navigation"/> is null inside an aggregate argument: the aggregated row.</summary>
internal sealed record ColumnExpr(string Column, Navigation? Navigation) : Expr;

internal sealed record ClassifierExpr(Navigation? Navigation) : Expr;

internal sealed record MatchNumberExpr : Expr;

/// <summary><see cref="Term"/> indexes <see cref="SimilarityTermTable.Terms"/>.</summary>
internal sealed record SimilarityExpr(int Term, Navigation? Navigation) : Expr;

/// <summary><see cref="Argument"/> null = <c>COUNT(*)</c> / <c>COUNT(v.*)</c>. <see cref="Labels"/> sorted, empty =
/// universal. <see cref="ClassifierInvolved"/> = the argument calls <c>CLASSIFIER()</c>.</summary>
internal sealed record AggregateExpr(AggregateKind Kind, int[] Labels, Expr? Argument, bool Running, bool ClassifierInvolved) : Expr;

internal sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr;
internal sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr;
internal sealed record IsNullExpr(Expr Operand, bool Negated) : Expr;
internal sealed record InExpr(Expr Operand, IReadOnlyList<Expr> Values, bool Negated) : Expr;
internal sealed record BetweenExpr(Expr Operand, Expr Low, Expr High, bool Negated) : Expr;
internal sealed record CaseBranch(Expr When, Expr Then);
/// <summary><see cref="Operand"/> non-null = simple CASE; null = searched CASE.</summary>
internal sealed record CaseExpr(Expr? Operand, IReadOnlyList<CaseBranch> Branches, Expr? Else) : Expr;
internal sealed record FunctionExpr(ScalarFunction Function, IReadOnlyList<Expr> Arguments) : Expr;
internal sealed record TimestampDiffExpr(TimeUnit Unit, Expr From, Expr To) : Expr;
```

`Iverson.Server/Iverson.Patterns/Expressions/SimilarityTermTable.cs`:
```csharp
namespace Iverson.Patterns.Expressions;

/// <summary>Distinct <c>(col, text)</c> terms in first-appearance order. The column compares case-insensitively
/// (it resolves to a schema descriptor case-insensitively, spec §2); the text compares ordinally.</summary>
internal sealed class SimilarityTermTable
{
    private readonly List<SimilarityTerm> _terms = [];

    public IReadOnlyList<SimilarityTerm> Terms => _terms;

    public int GetOrAdd(string column, string text)
    {
        for (int i = 0; i < _terms.Count; i++)
            if (string.Equals(_terms[i].Column, column, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_terms[i].Text, text, StringComparison.Ordinal))
                return i;
        _terms.Add(new SimilarityTerm(column, text));
        return _terms.Count - 1;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`Iverson.Server/Iverson.Patterns.Tests/ExpressionParserTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Patterns.Expressions;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class ExpressionParserTests
{
    // Variables A=0, B=1; subset U = {A, B}.
    private static ExpressionParser NewParser(SimilarityTermTable? terms = null) =>
        new(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 },
            new Dictionary<string, int[]> { ["U"] = [0, 1] },
            terms ?? new SimilarityTermTable());

    private static Expr Measure(string text) => NewParser().ParseMeasure(text);
    private static Expr Define(string text) => NewParser().ParseDefine(text);

    private static Navigation Nav(int[] labels, bool last = true, bool running = true, int logical = 0, int physical = 0) =>
        new(labels, last, running, logical, physical);

    [Theory]
    [InlineData("x", new int[0], true, 0, 0)]
    [InlineData("A.x", new[] { 0 }, true, 0, 0)]
    [InlineData("a.x", new[] { 0 }, true, 0, 0)]
    [InlineData("U.x", new[] { 0, 1 }, true, 0, 0)]
    [InlineData("PREV(A.x)", new[] { 0 }, true, 0, -1)]
    [InlineData("NEXT(A.x, 3)", new[] { 0 }, true, 0, 3)]
    [InlineData("FIRST(A.x)", new[] { 0 }, false, 0, 0)]
    [InlineData("FIRST(A.x, 2)", new[] { 0 }, false, 2, 0)]
    [InlineData("LAST(x, 1)", new int[0], true, 1, 0)]
    [InlineData("PREV(FIRST(A.x, 2), 3)", new[] { 0 }, false, 2, -3)]
    [InlineData("NEXT(LAST(B.x))", new[] { 1 }, true, 0, 1)]
    public void Column_reads_lower_to_navigations(string text, int[] labels, bool last, int logical, int physical) =>
        Measure(text).Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav(labels, last, running: true, logical, physical));

    [Fact]
    public void Final_applies_to_first_last_and_aggregates_in_measures()
    {
        Measure("FINAL LAST(x)").Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav([], last: true, running: false));
        Measure("PREV(FINAL FIRST(A.x))").Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav([0], last: false, running: false, physical: -1));
        Measure("FINAL COUNT(*)").Should().BeOfType<AggregateExpr>().Which.Running.Should().BeFalse();
        Measure("RUNNING COUNT(*)").Should().BeOfType<AggregateExpr>().Which.Running.Should().BeTrue();
    }

    [Fact]
    public void A_navigation_applies_to_every_read_in_its_argument()
    {
        var sum = Measure("PREV(A.x + A.y, 2)").Should().BeOfType<BinaryExpr>().Subject;
        sum.Left.Should().BeOfType<ColumnExpr>().Which.Navigation.Should().Be(Nav([0], physical: -2));
        sum.Right.Should().BeOfType<ColumnExpr>().Which.Navigation.Should().Be(Nav([0], physical: -2));
    }

    [Fact]
    public void Classifier_and_match_number_lower_to_their_reads()
    {
        Measure("CLASSIFIER()").Should().BeOfType<ClassifierExpr>().Which.Navigation.Should().Be(Nav([]));
        Measure("CLASSIFIER(U)").Should().BeOfType<ClassifierExpr>().Which.Navigation.Should().Be(Nav([0, 1]));
        Measure("PREV(CLASSIFIER(), 1)").Should().BeOfType<ClassifierExpr>()
            .Which.Navigation.Should().Be(Nav([], physical: -1));
        Measure("MATCH_NUMBER()").Should().BeOfType<MatchNumberExpr>();
    }

    [Fact]
    public void Aggregates_read_the_aggregated_row_and_record_their_labels()
    {
        var countAll = Measure("COUNT(*)").Should().BeOfType<AggregateExpr>().Subject;
        countAll.Kind.Should().Be(AggregateKind.Count);
        countAll.Labels.Should().BeEmpty();
        countAll.Argument.Should().BeNull();
        countAll.Running.Should().BeTrue();

        var countA = Measure("count(a.*)").Should().BeOfType<AggregateExpr>().Subject;
        countA.Kind.Should().Be(AggregateKind.Count);
        countA.Labels.Should().Equal(0);
        countA.Argument.Should().BeNull();

        var sum = Measure("SUM(U.price)").Should().BeOfType<AggregateExpr>().Subject;
        sum.Labels.Should().Equal(0, 1);
        sum.Argument.Should().Be(new ColumnExpr("price", null));
        sum.ClassifierInvolved.Should().BeFalse();

        Measure("SUM(x)").Should().BeOfType<AggregateExpr>().Which.Labels.Should().BeEmpty();
        Measure("COUNT(CLASSIFIER())").Should().BeOfType<AggregateExpr>().Which.ClassifierInvolved.Should().BeTrue();
        Measure("SUM(MATCH_NUMBER())").Should().BeOfType<AggregateExpr>();
    }

    [Fact]
    public void Literals_and_operators_lower_with_sql_precedence()
    {
        Measure("1 + 2 * 3").Should().Be(new BinaryExpr(BinaryOp.Add, new LiteralExpr(1L),
            new BinaryExpr(BinaryOp.Multiply, new LiteralExpr(2L), new LiteralExpr(3L))));
        Measure("2.5").Should().Be(new LiteralExpr(2.5));
        Measure("'it''s'").Should().Be(new LiteralExpr("it's"));
        Measure("NULL").Should().Be(new LiteralExpr(null));
        Measure("-x").Should().BeOfType<UnaryExpr>().Which.Op.Should().Be(UnaryOp.Negate);
        Define("x = 1 OR x = 2 AND NOT x = 3").Should().BeOfType<BinaryExpr>().Which.Op.Should().Be(BinaryOp.Or);
        Define("x IS NOT NULL").Should().Be(new IsNullExpr(new ColumnExpr("x", Nav([])), Negated: true));
        Define("x NOT IN (1, 2)").Should().BeOfType<InExpr>().Which.Negated.Should().BeTrue();
        Define("x NOT BETWEEN 1 AND 2").Should().BeOfType<BetweenExpr>().Which.Negated.Should().BeTrue();
        Measure("CASE WHEN x > 1 THEN 'a' ELSE 'b' END").Should().BeOfType<CaseExpr>().Which.Operand.Should().BeNull();
        Measure("CASE x WHEN 1 THEN 'a' END").Should().BeOfType<CaseExpr>().Which.Operand.Should().NotBeNull();
        Measure("coalesce(x, 0)").Should().BeOfType<FunctionExpr>().Which.Function.Should().Be(ScalarFunction.Coalesce);
        Measure("TIMESTAMPDIFF(minute, A.t, B.t)").Should().BeOfType<TimestampDiffExpr>()
            .Which.Unit.Should().Be(TimeUnit.Minute);
    }

    [Fact]
    public void Similarity_terms_are_deduplicated_by_column_case_insensitively_and_text_ordinally()
    {
        var terms = new SimilarityTermTable();
        var parser = NewParser(terms);

        parser.ParseDefine("SIMILARITY(title, 'refund') > 0.5");
        parser.ParseMeasure("SIMILARITY(Title, 'refund')");
        parser.ParseMeasure("SIMILARITY(A.title, 'Refund')");

        // Title/title collapse to one term (column case-insensitive); 'Refund' is a second term (text ordinal).
        terms.Terms.Should().Equal(new SimilarityTerm("title", "refund"), new SimilarityTerm("title", "Refund"));
        parser.ParseMeasure("SIMILARITY(B.title, 'x')").Should().BeOfType<SimilarityExpr>()
            .Which.Navigation.Should().Be(Nav([1]));
    }

    [Fact]
    public void Referenced_columns_accumulate_case_insensitively_across_expressions()
    {
        var parser = NewParser();
        parser.ParseDefine("A.price > PREV(A.Price)");
        parser.ParseMeasure("SUM(B.qty) + SIMILARITY(title, 'x')");

        parser.ReferencedColumns.Should().BeEquivalentTo(["price", "qty", "title"]);
        parser.ReferencedColumns.Contains("PRICE").Should().BeTrue();
    }

    [Theory]
    // Trino 483 analyzer rules (plan-level assumption 25 and the V-probes; messages quoted where Trino's is used).
    [InlineData("PREV(A.x + B.x) > 0", "*must match*")]
    [InlineData("PREV(x + A.x) > 0", "*must match*")]
    [InlineData("FIRST(PREV(A.x)) > 0", "*nest*")]
    [InlineData("FIRST(LAST(A.x)) > 0", "*nest*")]
    [InlineData("PREV(PREV(A.x)) > 0", "*nest*")]
    [InlineData("PREV(NEXT(A.x)) > 0", "*nest*")]
    [InlineData("PREV(FIRST(A.x) + 1) > 0", "*Immediate nesting*")]
    [InlineData("PREV(COUNT(A.*)) > 0", "*nest*")]
    [InlineData("SUM(PREV(A.x)) > 0", "*nest*")]
    [InlineData("SUM(COUNT(A.*)) > 0", "*nest*")]
    [InlineData("SUM(A.x + B.x) > 0", "*must match*")]
    [InlineData("PREV(1) > 0", "*column reference or CLASSIFIER*")]
    [InlineData("PREV(MATCH_NUMBER()) > 0", "*column reference or CLASSIFIER*")]
    [InlineData("PREV(A.x, -1) > 0", "*non-negative*")]
    [InlineData("PREV(A.x, 1 + 1) > 0", "*number*")]
    [InlineData("RUNNING PREV(A.x) > 0", "*RUNNING*")]
    [InlineData("CLASSIFIER(C) = 'A'", "*C*")]
    [InlineData("C.x > 0", "*C*")]
    [InlineData("FINAL LAST(A.x) > 0", "*FINAL*DEFINE*")]
    [InlineData("A.x + 1", "*boolean*")]
    [InlineData("COUNT(*)", "*boolean*")]
    [InlineData("'yes'", "*boolean*")]
    [InlineData("SIMILARITY(title, '') > 0", "*SIMILARITY*")]
    [InlineData("SIMILARITY(title, x) > 0", "*SIMILARITY*")]
    [InlineData("SIMILARITY('t', 'x') > 0", "*SIMILARITY*")]
    [InlineData("TIMESTAMPDIFF(WEEK, A.t, B.t) > 0", "*TIMESTAMPDIFF*")]
    [InlineData("LOWER(x) = 'a'", "*LOWER*")]
    [InlineData("x = ", "*")]
    [InlineData("x = 'unterminated", "*")]
    [InlineData("99999999999999999999 > 0", "*")]
    public void Rejects_what_the_standard_and_Trino_reject(string define, string message)
    {
        var act = () => Define(define);
        act.Should().Throw<PatternValidationException>().WithMessage(message);
    }

    [Theory]
    [InlineData("FINAL A.x")]
    [InlineData("FINAL PREV(LAST(x))")]
    [InlineData("FINAL CLASSIFIER()")]
    [InlineData("FINAL MATCH_NUMBER()")]
    public void Rejects_a_semantics_prefix_on_anything_but_first_last_and_aggregates(string measure)
    {
        var act = () => Measure(measure);
        act.Should().Throw<PatternValidationException>();
    }

    [Fact]
    public void Running_and_final_are_columns_unless_they_prefix_a_call()
    {
        Define("final > 0").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("final", Nav([])));
        Define("A.final > 0").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("final", Nav([0])));
        Measure("running + 1").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("running", Nav([])));
    }

    [Fact]
    public void Accepts_running_and_match_number_inside_define()
    {
        Define("RUNNING LAST(A.x) > 0 AND MATCH_NUMBER() = 1").Should().BeOfType<BinaryExpr>();
        Define("RUNNING COUNT(A.*) < 3").Should().BeOfType<BinaryExpr>();
    }

    [Fact]
    public void A_navigation_resolves_positions_as_Trino_does()
    {
        // Match starts at partition row 2; labels A B A B (A=0, B=1); current row 4 (the second A).
        var labels = new ArrayView([0, 1, 0, 1], 4);

        Nav([0]).ResolvePosition(4, labels, 0, 10, 2).Should().Be(4);                     // LAST(A) running
        Nav([0], logical: 1).ResolvePosition(4, labels, 0, 10, 2).Should().Be(2);         // LAST(A, 1)
        Nav([0], logical: 2).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);        // LAST(A, 2): none
        Nav([1], last: false).ResolvePosition(4, labels, 0, 10, 2).Should().Be(3);        // FIRST(B)
        Nav([1], running: false).ResolvePosition(4, labels, 0, 10, 2).Should().Be(5);     // FINAL LAST(B)
        Nav([0], physical: -3).ResolvePosition(4, labels, 0, 10, 2).Should().Be(1);       // PREV(A.x, 3): before the match, inside the partition
        Nav([0], physical: -5).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);      // before the partition
        Nav([], physical: 6).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);        // NEXT past the partition end
    }
}
```
- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ExpressionParserTests"`
Expected: FAIL — `ExpressionParser` does not exist.

- [ ] **Step 4: Implement the lexer and parser**

`Expressions/ExpressionLexer.cs` — tokens (whitespace skipped): identifier `[A-Za-z_][A-Za-z0-9_]*`; integer `\d+` (→ `long`; overflow is a `PatternValidationException`); decimal `\d+\.\d*`, `\.\d+`, optional exponent `[eE][+-]?\d+` (→ `double`, invariant culture); string `'…'` with `''` as an escaped quote (unterminated → error); `<>` `<=` `>=` `<` `>` `=` `+` `-` `*` `/` `%` `(` `)` `,` `.`. Keywords and function names are case-insensitive; identifiers keep their spelling.

`Expressions/ExpressionParser.cs` — `internal sealed class ExpressionParser(IReadOnlyDictionary<string, int> labels, IReadOnlyDictionary<string, int[]> subsets, SimilarityTermTable similarityTerms)` with `public HashSet<string> ReferencedColumns { get; } = new(StringComparer.OrdinalIgnoreCase)`, `public Expr ParseDefine(string text)` and `public Expr ParseMeasure(string text)`. Recursive descent, lowering while parsing:

- **Precedence**, lowest first: `OR`; `AND`; `NOT`; comparison (`= <> < <= > >=`), `IS [NOT] NULL`, `[NOT] IN (list)`, `[NOT] BETWEEN a AND b`; `+ -`; `* / %`; unary `-`; primary. Literals: numbers, strings, `TRUE`, `FALSE`, `NULL`. `CASE [operand] WHEN … THEN … [ELSE …] END`.
- **Identifiers:** `v.col` where `v` (case-insensitive) is a key of `labels` or `subsets` → a read with label set `[labels[V]]` or `subsets[V]`; any other `x.col` qualifier → `PatternValidationException("Column 'x.col' cannot be resolved")`. A bare identifier not followed by `(` is a column over the universal set. Every column name read is added to `ReferencedColumns` as written.
- **Navigations** `PREV`/`NEXT(expr [, n])` and `FIRST`/`LAST(expr [, n])`: `n` must be an integer literal: a `-` followed by an integer literal → "requires a non-negative number as the second argument"; any other expression → "requires a number as the second argument". The argument is lowered with a pending navigation that each `ColumnExpr`/`ClassifierExpr`/`SimilarityExpr` read inside it receives. Rules (Trino's messages): all reads inside must share one label set ("All labels and classifiers inside the call to '…' must match"); it must contain a column read or `CLASSIFIER` ("… must contain at least one column reference or CLASSIFIER()"); `FIRST`/`LAST` may not contain any navigation ("Cannot nest … inside …"); `PREV`/`NEXT` may contain another navigation only as its whole argument and only `FIRST`/`LAST` ("Immediate nesting is required …" / "Cannot nest …"); no aggregate inside a navigation ("Cannot nest … aggregate function inside …").
- **Aggregates** `COUNT(*)`, `COUNT(v.*)`, `COUNT|SUM|AVG|MIN|MAX(expr)`: reads inside get navigation `null`; their qualifiers must agree and become `Labels` (none → universal); a `CLASSIFIER()` inside sets `ClassifierInvolved`; no navigation or aggregate inside ("Cannot nest …").
- **`RUNNING`/`FINAL`** is a semantics prefix only when the next two tokens are an identifier and `(` (a call), as in Trino 483; otherwise `running`/`final` is an ordinary identifier (a column, or a qualifier before `.`). As a prefix it may apply only to `FIRST`, `LAST` or an aggregate (default `RUNNING`); on any other call → "… semantics is not supported with …". `FINAL A.x` stays rejected (the identifier `FINAL` followed by a trailing token). A `FINAL` prefix in a `define` → "FINAL semantics is not supported in DEFINE clause".
- **`CLASSIFIER()`** / **`CLASSIFIER(v)`** (`v` a variable or subset, else "… is not a primary pattern variable or subset name"); **`MATCH_NUMBER()`**; **`COALESCE`** (≥ 1 argument), **`NULLIF`** (2), **`ROUND`** (1 or 2, the second an integer literal), **`ABS`** (1); **`TIMESTAMPDIFF(unit, a, b)`** with unit `SECOND|MINUTE|HOUR|DAY` (else an error naming `TIMESTAMPDIFF`); **`SIMILARITY(colref, 'text')`** — first argument a column read (qualified or not), second a non-empty string literal, else an error naming `SIMILARITY`; lowered to `SimilarityExpr(similarityTerms.GetOrAdd(column, text), navigation)`, and the column goes into `ReferencedColumns`. Any other function name → an error naming it.
- **`ParseDefine`** rejects a root that can never be boolean — arithmetic, unary minus, a numeric or string literal, `COUNT`/`SUM`/`AVG`, `MATCH_NUMBER()`, `CLASSIFIER`, `SIMILARITY`, `TIMESTAMPDIFF`, `ROUND`, `ABS` — with "Expression defining a label must be boolean"; any other root is checked at run time (Task 5).
- Trailing tokens after a complete expression are an error.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ExpressionParserTests"`
Expected: PASS.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/ArrayView.cs Iverson.Server/Iverson.Patterns/Expressions Iverson.Server/Iverson.Patterns.Tests/ExpressionParserTests.cs
git commit -m "add the define and measures expression parser with Trino's navigation lowering"
```

### Task 5: `ExpressionEvaluator` — three-valued logic, NaN, navigation and aggregates

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/Expressions/EvaluationContext.cs`, `Expressions/SqlValues.cs`, `Expressions/ExpressionEvaluator.cs`
- Create: `Iverson.Server/Iverson.Patterns.Tests/TestRows.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/ExpressionEvaluatorTests.cs`

**Interfaces:**
- Consumes: `ArrayView`, `Navigation`, the `Expr` family, `ExpressionParser` (Task 4); `PatternEvaluationException` (Task 1).
- Produces: `EvaluationContext`, `ExpressionEvaluator.Evaluate` / `EvaluateDefine` — consumed by Tasks 6 and 7; `TestRows.Rows` — consumed by the Task 6–8 tests.

**Port source for measure reads:** `operator/window/pattern/MeasureComputation.java@483` (`compute`, `computeEmpty`): a value read resolves its navigation with `searchStart = 0`, `searchEnd = Rows.Count` (MATCH_RECOGNIZE searches the whole partition); `-1` reads NULL; `CLASSIFIER` returns NULL for a position outside `[PatternStart, PatternStart + MatchedLabels.Length)` and otherwise the label name, even past the running position; an empty match reads NULL everywhere except `MATCH_NUMBER()`, `COUNT` is 0 and other aggregates NULL.

- [ ] **Step 1: Create the evaluation context**

`Iverson.Server/Iverson.Patterns/Expressions/EvaluationContext.cs`:
```csharp
namespace Iverson.Patterns.Expressions;

/// <summary>
/// The state an expression reads. In a <c>define</c>, <see cref="MatchedLabels"/> ends with the label being
/// tried and <see cref="CurrentRow"/> = <see cref="PatternStart"/> + <c>MatchedLabels.Length</c> − 1. For a
/// measure, <see cref="MatchedLabels"/> is the whole match and <see cref="CurrentRow"/> the row the measure is
/// computed for (the last row for <c>ONE_ROW</c>). <see cref="EmptyMatch"/> selects empty-match semantics.
/// </summary>
internal sealed class EvaluationContext
{
    public required IReadOnlyList<IDictionary<string, object?>> Rows { get; init; }
    public required IReadOnlyList<string> LabelNames { get; init; }
    /// <summary>(partition row index, similarity term index) → score, or null for an absent vector.</summary>
    public required Func<int, int, double?> Similarity { get; init; }
    public int PatternStart { get; set; }
    public int CurrentRow { get; set; }
    public ArrayView MatchedLabels { get; set; } = ArrayView.Empty;
    public long MatchNumber { get; set; }
    public bool EmptyMatch { get; set; }
}
```

- [ ] **Step 2: Create the shared row helper for tests**

`Iverson.Server/Iverson.Patterns.Tests/TestRows.cs`:
```csharp
namespace Iverson.Patterns.Tests;

internal static class TestRows
{
    /// <summary>Partition rows keyed case-insensitively, as both row sources build them (spec §3.2).</summary>
    public static List<IDictionary<string, object?>> Rows(string[] columns, params object?[][] values) =>
        values.Select(v => (IDictionary<string, object?>)columns.Zip(v)
                .ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
```

- [ ] **Step 3: Write the failing evaluator tests**

`Iverson.Server/Iverson.Patterns.Tests/ExpressionEvaluatorTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Patterns.Expressions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

public sealed class ExpressionEvaluatorTests
{
    // Variables A=0, B=1; subset U = {A, B}.
    private static ExpressionParser Parser() =>
        new(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 },
            new Dictionary<string, int[]> { ["U"] = [0, 1] },
            new SimilarityTermTable());

    private static EvaluationContext Ctx(List<IDictionary<string, object?>> rows, int[] labels, int patternStart = 0,
        int? currentRow = null, long matchNumber = 1, Func<int, int, double?>? similarity = null) =>
        new()
        {
            Rows = rows,
            LabelNames = ["A", "B"],
            Similarity = similarity ?? ((_, _) => null),
            PatternStart = patternStart,
            CurrentRow = currentRow ?? patternStart + labels.Length - 1,
            MatchedLabels = new ArrayView(labels, labels.Length),
            MatchNumber = matchNumber,
        };

    private static object? Eval(string measure, EvaluationContext ctx) =>
        ExpressionEvaluator.Evaluate(Parser().ParseMeasure(measure), ctx);

    // One row: s = NaN, n = NULL, i = 5, m = long.MinValue.
    private static EvaluationContext NaNRow() =>
        Ctx(Rows(["s", "n", "i", "m"], [double.NaN, null, 5L, long.MinValue]), [0]);

    [Theory]
    [InlineData("s = 0.5", false)]
    [InlineData("s <> 0.5", true)]
    [InlineData("s < 1", false)]
    [InlineData("s >= 0", false)]
    [InlineData("s = s", false)]
    [InlineData("NOT (s = 0.5)", true)]
    [InlineData("s IN (0.5, 1.5)", false)]
    [InlineData("s NOT IN (0.5)", true)]
    [InlineData("s BETWEEN 0 AND 1", false)]
    [InlineData("s NOT BETWEEN 0 AND 1", true)]
    [InlineData("s = n", null)]
    [InlineData("s IN (0.5, n)", null)]
    [InlineData("s BETWEEN 0 AND n", false)]
    [InlineData("s IS NULL", false)]
    [InlineData("s IS NOT NULL", true)]
    [InlineData("i = 5", true)]
    [InlineData("i = 5.0", true)]
    [InlineData("n = n", null)]
    public void NaN_and_null_comparisons_follow_spec_section_2(string expression, bool? expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("s + 1")]
    [InlineData("ROUND(s)")]
    [InlineData("ABS(s)")]
    [InlineData("COALESCE(s, 1)")]
    [InlineData("NULLIF(s, s)")]
    public void NaN_propagates_through_arithmetic_rounding_and_null_functions(string expression) =>
        Eval(expression, NaNRow()).Should().BeOfType<double>().Which.Should().Be(double.NaN);

    [Fact]
    public void Null_functions_treat_null_as_absent()
    {
        Eval("COALESCE(n, i)", NaNRow()).Should().Be(5L);
        Eval("NULLIF(i, 5)", NaNRow()).Should().BeNull();
    }

    [Fact]
    public void Aggregates_follow_the_NaN_rules()
    {
        var rows = Rows(["v"], [1.0], [double.NaN], [2.0]);
        var ctx = Ctx(rows, [0, 0, 0]);

        Eval("FINAL SUM(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL AVG(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL COUNT(A.v)", ctx).Should().Be(3L);
        Eval("FINAL MIN(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL MAX(A.v)", ctx).Should().Be(2.0);
        Eval("FINAL MAX(A.v)", Ctx(Rows(["v"], [double.NaN], [double.NaN]), [0, 0])).Should().Be(double.NaN);
    }

    [Theory]
    [InlineData("n AND FALSE", false)]
    [InlineData("n AND TRUE", null)]
    [InlineData("n OR TRUE", true)]
    [InlineData("n OR FALSE", null)]
    [InlineData("NOT n", null)]
    public void Boolean_logic_is_three_valued(string expression, bool? expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("7 / 2", 3L)]
    [InlineData("-7 / 2", -3L)]
    [InlineData("-7 % 3", -1L)]
    [InlineData("i * 2 - 1", 9L)]
    [InlineData("ABS(-3)", 3L)]
    [InlineData("ROUND(7)", 7L)]
    public void Integer_arithmetic_truncates_toward_zero(string expression, long expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("7 / 2.0", 3.5)]
    [InlineData("1 + 2.5", 3.5)]
    [InlineData("ROUND(2.5)", 3.0)]
    [InlineData("ROUND(-2.5)", -3.0)]
    [InlineData("ROUND(0.125, 2)", 0.13)]
    [InlineData("1.0 / 0", double.PositiveInfinity)]
    [InlineData("0.0 / 0", double.NaN)]
    public void Mixed_and_double_arithmetic_promotes_to_double(string expression, double expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("1 % 0")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("ABS(m)")]
    [InlineData("-m")]
    [InlineData("'a' + 1")]
    [InlineData("'a' < 1")]
    [InlineData("TIMESTAMPDIFF(SECOND, i, i)")]
    [InlineData("missing")]
    public void Run_time_errors_raise_PatternEvaluationException(string expression)
    {
        var act = () => Eval(expression, NaNRow());
        act.Should().Throw<PatternEvaluationException>();
    }

    [Fact]
    public void Strings_compare_ordinally_and_values_normalize_on_read()
    {
        Eval("'a' < 'B'", NaNRow()).Should().Be(false);
        Eval("'B' < 'a'", NaNRow()).Should().Be(true);
        Eval("v", Ctx(Rows(["v"], [7]), [0])).Should().Be(7L);          // Int32 reads as long
        Eval("v", Ctx(Rows(["v"], [1.5f]), [0])).Should().Be(1.5);      // Single reads as double
        var bytes = () => Eval("v", Ctx(Rows(["v"], [new byte[] { 1 }]), [0]));
        bytes.Should().Throw<PatternEvaluationException>();
    }

    [Fact]
    public void Case_expressions_never_match_null()
    {
        Eval("CASE WHEN i > 3 THEN 'big' ELSE 'small' END", NaNRow()).Should().Be("big");
        Eval("CASE i WHEN 5 THEN 'five' END", NaNRow()).Should().Be("five");
        Eval("CASE n WHEN n THEN 1 ELSE 2 END", NaNRow()).Should().Be(2L);
        Eval("CASE WHEN n THEN 1 END", NaNRow()).Should().BeNull();
    }

    [Fact]
    public void Timestampdiff_counts_whole_units_truncated_toward_zero()
    {
        var ctx = Ctx(Rows(["t1", "t2", "t3"],
            [new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 9, 58, 30), new DateTime(2026, 1, 3, 9, 0, 0)]), [0]);

        Eval("TIMESTAMPDIFF(SECOND, t1, t2)", ctx).Should().Be(-90L);
        Eval("TIMESTAMPDIFF(MINUTE, t1, t2)", ctx).Should().Be(-1L);
        Eval("TIMESTAMPDIFF(HOUR, t1, t3)", ctx).Should().Be(47L);
        Eval("TIMESTAMPDIFF(DAY, t1, t3)", ctx).Should().Be(1L);
    }

    // Five partition rows x = 10..50; the match starts at row 1 with labels A B B (rows 1–3).
    private static EvaluationContext Match(int currentRow, long matchNumber = 7, Func<int, int, double?>? similarity = null) =>
        Ctx(Rows(["x", "title"], [10L, "t0"], [20L, "t1"], [30L, "t2"], [40L, "t3"], [50L, "t4"]),
            [0, 1, 1], patternStart: 1, currentRow: currentRow, matchNumber: matchNumber, similarity: similarity);

    [Theory]
    [InlineData("PREV(A.x)", 10L)]          // before the match, inside the partition
    [InlineData("PREV(A.x, 2)", null)]      // before the partition
    [InlineData("NEXT(B.x)", 50L)]          // after the match
    [InlineData("NEXT(B.x, 2)", null)]      // after the partition
    [InlineData("FIRST(B.x)", 30L)]
    [InlineData("FIRST(B.x, 1)", 40L)]
    [InlineData("FIRST(B.x, 2)", null)]
    [InlineData("x", 40L)]
    [InlineData("CLASSIFIER()", "B")]
    [InlineData("PREV(CLASSIFIER(), 2)", "A")]
    [InlineData("PREV(CLASSIFIER(), 3)", null)] // outside the match
    [InlineData("MATCH_NUMBER()", 7L)]
    [InlineData("COUNT(*)", 3L)]
    [InlineData("SUM(B.x)", 70L)]
    [InlineData("MIN(B.x)", 30L)]
    [InlineData("AVG(U.x)", 30.0)]
    [InlineData("COUNT(CLASSIFIER())", 3L)]
    public void Measures_read_through_navigations_at_the_last_row(string measure, object? expected) =>
        Eval(measure, Match(currentRow: 3)).Should().Be(expected);

    [Fact]
    public void Running_reads_stop_at_the_current_row_and_final_reads_see_the_whole_match()
    {
        Eval("COUNT(B.*)", Match(currentRow: 2)).Should().Be(1L);
        Eval("FINAL COUNT(B.*)", Match(currentRow: 2)).Should().Be(2L);
        Eval("LAST(x)", Match(currentRow: 2)).Should().Be(30L);
        Eval("FINAL LAST(x)", Match(currentRow: 2)).Should().Be(40L);
        Eval("CLASSIFIER(B)", Match(currentRow: 1)).Should().BeNull();       // no B yet under RUNNING
    }

    [Fact]
    public void Similarity_reads_the_score_at_the_navigated_row()
    {
        Func<int, int, double?> scores = (row, term) => term == 0 && row == 3 ? 0.8 : null;

        Eval("SIMILARITY(title, 'q')", Match(3, similarity: scores)).Should().Be(0.8);
        Eval("PREV(SIMILARITY(title, 'q'))", Match(3, similarity: scores)).Should().BeNull();
    }

    [Fact]
    public void An_empty_match_reads_null_except_match_number_and_count()
    {
        var ctx = Match(currentRow: 1, matchNumber: 4);
        ctx.EmptyMatch = true;
        ctx.MatchedLabels = ArrayView.Empty;

        Eval("MATCH_NUMBER()", ctx).Should().Be(4L);
        Eval("CLASSIFIER()", ctx).Should().BeNull();
        Eval("x", ctx).Should().BeNull();
        Eval("COUNT(*)", ctx).Should().Be(0L);
        Eval("SUM(A.x)", ctx).Should().BeNull();
    }

    [Fact]
    public void A_define_labels_the_row_only_when_true()
    {
        var parser = Parser();

        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("B.x > PREV(B.x)"), Match(3)).Should().BeTrue();
        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("B.x < PREV(B.x)"), Match(3)).Should().BeFalse();
        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("PREV(B.x, 5) > 0"), Match(3)).Should().BeFalse(); // NULL

        var nonBoolean = () => ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("COALESCE(x, 0)"), Match(3));
        nonBoolean.Should().Throw<PatternEvaluationException>().WithMessage("*boolean*");
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ExpressionEvaluatorTests"`
Expected: FAIL — `ExpressionEvaluator` does not exist.

- [ ] **Step 5: Implement `SqlValues` and `ExpressionEvaluator`**

`Expressions/SqlValues.cs` — `internal static class SqlValues`:
- `Normalize(object? value, string column)`: `null`→`null`; `sbyte/byte/short/ushort/int/uint/long`→`long`; `float/double/decimal`→`double`; `string`, `bool`, `DateTime` unchanged; anything else → `PatternEvaluationException($"Unsupported value type {type.Name} in column '{column}'.")`.
- `Compare(object a, object b)` for non-null operands: two `long`s compare as `long`; any numeric pair with a `double` compares as `double` with IEEE semantics (every relation with `NaN` is false, `<>` true — implement relations with the C# operators, never `CompareTo`); strings with `string.CompareOrdinal`; `bool` with `bool`, `DateTime` with `DateTime`; any other pairing → `PatternEvaluationException("Cannot compare … with ….")`.
- Arithmetic: `long ∘ long` in `checked` context (`OverflowException` → `PatternEvaluationException`); `/` and `%` by integer zero → `PatternEvaluationException("Division by zero")`; C# `/` and `%` already truncate and take the dividend's sign; any `double` operand → IEEE `double` arithmetic; non-numeric operand → `PatternEvaluationException`. Unary minus on `long` is `checked`.

`Expressions/ExpressionEvaluator.cs` — `internal static class ExpressionEvaluator` with `public static object? Evaluate(Expr expr, EvaluationContext context)` and `public static bool EvaluateDefine(Expr expr, EvaluationContext context)` (TRUE → true; FALSE/NULL → false; any other value → `PatternEvaluationException("A define expression must evaluate to a boolean …")`). A private recursive `Evaluate(expr, ctx, int aggregationRow)` carries the aggregated row (-1 outside aggregates). Rules:
- **Reads** (`ColumnExpr`, `ClassifierExpr`, `SimilarityExpr`): if `ctx.EmptyMatch` → `null`. Navigation `null` → the aggregated row. Otherwise `position = navigation.ResolvePosition(ctx.CurrentRow, ctx.MatchedLabels, 0, ctx.Rows.Count, ctx.PatternStart)`; `-1` → `null`. A column absent from the row → `PatternEvaluationException("Column '…' is not in the row.")`; values go through `SqlValues.Normalize`. Classifier outside `[PatternStart, PatternStart + MatchedLabels.Length)` → `null`, else `LabelNames[MatchedLabels[position − PatternStart]]`. Similarity → `ctx.Similarity(position, term)`.
- **`MatchNumberExpr`** → `ctx.MatchNumber`.
- **Aggregates:** empty match → `COUNT` 0L, others `null`. Otherwise positions are the relative indexes `0..end` (`end = Running ? CurrentRow − PatternStart : MatchedLabels.Length − 1`) whose label is in `Labels` (all when `Labels` is empty); evaluate `Argument` with that absolute row as the aggregated row. `COUNT(*)`/`COUNT(v.*)` count positions; `COUNT(e)` counts non-null values (NaN counts); `SUM` of all-`long` → checked `long`, else `double` (NaN propagates), no non-null value → `null`; `AVG` → `double` sum / count, `null` if none; `MIN`/`MAX` over non-null values — for `double`s use `double.CompareTo` ordering (NaN lowest: `MIN` returns NaN if present, `MAX` ignores NaN unless all are NaN), else `SqlValues.Compare`.
- **Logic:** SQL three-valued `AND`/`OR`/`NOT` (non-boolean operand → `PatternEvaluationException`); comparisons with a `null` operand → `null`; `IN` = `OR` over `=`; `BETWEEN` = `>= AND <=`; `NOT IN`/`NOT BETWEEN` negate under three-valued logic; `IS [NOT] NULL` tests C# `null` only (NaN is not null).
- **Functions:** `COALESCE` → first non-null; `NULLIF(a, b)` → `null` when `a = b` is TRUE, else `a`; `ROUND(x)` → `long` unchanged, `double` via `Math.Round(x, MidpointRounding.AwayFromZero)`; `ROUND(x, d)` → `Math.Round(x, d, MidpointRounding.AwayFromZero)` for `double`, unchanged for `long`; `ABS` → checked for `long`, `Math.Abs` for `double`. `TIMESTAMPDIFF(unit, a, b)`: both `DateTime` (else `PatternEvaluationException`), `(b − a).Ticks / TimeSpan.TicksPerSecond|Minute|Hour|Day` (C# integer division truncates toward zero).
- **`CASE`:** searched — first `WHEN` that is TRUE; simple — first `WHEN` whose value equals the operand (TRUE under three-valued `=`, so `null` never matches); no match → `ELSE` or `null`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~ExpressionEvaluatorTests"`
Expected: PASS.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/Expressions/EvaluationContext.cs Iverson.Server/Iverson.Patterns/Expressions/SqlValues.cs Iverson.Server/Iverson.Patterns/Expressions/ExpressionEvaluator.cs Iverson.Server/Iverson.Patterns.Tests/TestRows.cs Iverson.Server/Iverson.Patterns.Tests/ExpressionEvaluatorTests.cs
git commit -m "add the expression evaluator with three-valued logic and the NaN rules"
```

### Task 6: `Matcher`, `ThreadEquivalence` and the budgets — the thread VM

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/Matching/IntList.cs`, `IntStack.cs`, `IntMultimap.cs`, `Captures.cs`, `MatchResult.cs`, `ILabelEvaluator.cs`, `ThreadEquivalence.cs`, `Matcher.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/MatcherTests.cs`

**Interfaces:**
- Consumes: `Instruction`, `InstructionKind`, `ProgramCompiler` (Task 3); `ArrayView`, `Navigation`, the `Expr` family, `ExpressionParser` (Task 4); `PatternBudget` (Task 1).
- Produces: `ILabelEvaluator`, `MatchResult`, `ThreadEquivalence`, `Matcher` — consumed by Task 7.

**Port sources (Trino tag 483, `operator/window/matcher/`):** `Matcher.java` (whole file, including the nested `Runtime`), `ThreadEquivalence.java` (whole file), `Captures.java`, `IntList.java`, `IntStack.java`, `IntMultimap.java`, `MatchResult.java`. Each ported file's header: `// Port of Trino 483 io.trino.operator.window.matcher.<Name> (Apache-2.0); see THIRD-PARTY-NOTICES.md.` Memory accounting (`getSizeInBytes`, memory contexts) is dropped.

**Deliberate deviations from the port source:**
1. **No recursion** (spec §9.1: the `Matcher` must run an at-cap program on a thread-pool thread without a stack overflow). `ThreadEquivalence`'s reachable-label computation and `Matcher.advanceAndSchedule` use explicit stacks — the code for both is given below and must be used as written: the push order is what keeps the thread priority order identical to Trino's (plan-level assumptions 19–20).
2. **Aggregations are recomputed from the matched labels** instead of being carried per thread (`MatchAggregations`). `ThreadEquivalence` therefore takes each label's lowered `define` (`Expr?`, null = no define) and derives Trino's three comparison sets from it: input navigations (`ColumnExpr`/`SimilarityExpr` reads with a non-null navigation over a non-empty label set, physical offset stripped), classifier navigations (`ClassifierExpr` reads with a non-null navigation, physical offset kept), and aggregations (`AggregateExpr`, split by `ClassifierInvolved && Labels.Length != 1`). An aggregation's "positions" are the relative indexes of the matched labels in its label set (all, for the universal set).
3. **Budgets:** every instruction popped in `AdvanceAndSchedule` and every `EvaluateLabel` call is one `budget.Step()`; `budget.CheckActiveThreads(activeThreadCount)` runs after every thread allocation (new or forked), where the active count is allocated thread ids minus freed ones.

- [ ] **Step 1: Write the failing matcher tests**

`Iverson.Server/Iverson.Patterns.Tests/MatcherTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Expressions;
using Iverson.Patterns.Matching;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class MatcherTests
{
    /// <summary>truth[row][label]: whether the label's define holds at that row. History-free by construction.</summary>
    private sealed class TableEvaluator(bool[][] truth, int patternStart = 0, bool atPartitionStart = true) : ILabelEvaluator
    {
        public int InputLength => truth.Length - patternStart;
        public bool MatchingAtPartitionStart => atPartitionStart;
        public bool EvaluateLabel(ArrayView matchedLabels) =>
            truth[patternStart + matchedLabels.Length - 1][matchedLabels[matchedLabels.Length - 1]];
    }

    private static (Matcher Matcher, IReadOnlyList<string> Labels) Build(string pattern, int maxInstructions = 5000)
    {
        var parsed = PatternParser.Parse(pattern, []);
        var program = ProgramCompilerTests.CompileText(pattern, maxInstructions);
        var equivalence = new ThreadEquivalence(program, new Expr?[parsed.Variables.Count]);
        return (new Matcher(program, equivalence), parsed.Variables);
    }

    private static bool[][] AllTrue(int rows, int labels) =>
        Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(true, labels).ToArray()).ToArray();

    private static PatternBudget Budget() => new(maxActiveThreads: 10_000, maxSteps: 10_000_000);

    [Fact]
    public void Alternation_prefers_the_left_branch()
    {
        var (matcher, _) = Build("A | B");
        var result = matcher.Run(new TableEvaluator(AllTrue(1, 2)), Budget());

        result.Matched.Should().BeTrue();
        result.Labels.ToArray().Should().Equal(0);
    }

    [Fact]
    public void Greedy_takes_the_longest_and_reluctant_the_shortest_match()
    {
        var (greedy, _) = Build("A*");
        greedy.Run(new TableEvaluator(AllTrue(3, 1)), Budget()).Labels.ToArray().Should().Equal(0, 0, 0);

        var (reluctant, _) = Build("A*?");
        var empty = reluctant.Run(new TableEvaluator(AllTrue(3, 1)), Budget());
        empty.Matched.Should().BeTrue();
        empty.Labels.Length.Should().Be(0);
    }

    [Fact]
    public void Permute_prefers_the_lexicographically_first_ordering()
    {
        var (matcher, _) = Build("PERMUTE(A, B)");
        matcher.Run(new TableEvaluator(AllTrue(2, 2)), Budget()).Labels.ToArray().Should().Equal(0, 1);
    }

    [Fact]
    public void Exclusions_are_reported_as_relative_start_end_pairs()
    {
        var (matcher, _) = Build("A {- B -} C");
        var result = matcher.Run(new TableEvaluator(AllTrue(3, 3)), Budget());

        result.Labels.ToArray().Should().Equal(0, 1, 2);
        result.Exclusions.ToArray().Should().Equal(1, 2);
    }

    [Fact]
    public void Anchors_constrain_the_partition_start_and_end()
    {
        var (start, _) = Build("^ A");
        start.Run(new TableEvaluator(AllTrue(2, 1), patternStart: 1, atPartitionStart: false), Budget())
            .Matched.Should().BeFalse();
        start.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Matched.Should().BeTrue();

        var (end, _) = Build("A $");
        end.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Matched.Should().BeFalse();

        var (endPlus, _) = Build("A+ $");
        endPlus.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Labels.ToArray().Should().Equal(0, 0);
    }

    [Fact]
    public void A_false_first_label_is_no_match()
    {
        var (matcher, _) = Build("A B");
        matcher.Run(new TableEvaluator([[false, true], [true, true]]), Budget()).Matched.Should().BeFalse();
    }

    [Fact]
    public void Equivalent_threads_are_pruned_so_an_exponential_pattern_runs_in_linear_steps()
    {
        // Trino's testPotentiallyExponentialMatch: (A+)+ B over rows where A holds and B never does.
        long Steps(int rows)
        {
            var (matcher, _) = Build("(A+)+ B");
            var truth = Enumerable.Range(0, rows).Select(_ => new[] { true, false }).ToArray();
            var budget = Budget();
            matcher.Run(new TableEvaluator(truth), budget).Matched.Should().BeFalse();
            return budget.StepsUsed;
        }

        var ten = Steps(10);
        var twenty = Steps(20);
        twenty.Should().BeLessThan(3 * ten, "pruning keeps the work linear in the input; unpruned it doubles per row");
    }

    [Fact]
    public void Exceeding_MaxActiveThreads_throws()
    {
        var (matcher, _) = Build("A | B");
        var act = () => matcher.Run(new TableEvaluator(AllTrue(1, 2)), new PatternBudget(maxActiveThreads: 1, maxSteps: 1_000));
        act.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxActiveThreads");
    }

    [Fact]
    public void Exceeding_MaxSteps_throws()
    {
        var (matcher, _) = Build("A+");
        var act = () => matcher.Run(new TableEvaluator(AllTrue(10, 1)), new PatternBudget(maxActiveThreads: 100, maxSteps: 5));
        act.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxSteps");
    }

    [Theory]
    [InlineData("A{0,2499} B")]   // 5,000 instructions: the reachable-label precomputation recurses 5,000 deep
    [InlineData("A{4999}")]
    [InlineData("(A?){2499} B")]  // 5,000 instructions; its Splits chain through their second branches: 2,500-deep scheduling
    public void An_at_cap_program_runs_without_a_stack_overflow_on_small_stacks(string pattern)
    {
        void RunOnce()
        {
            var (matcher, _) = Build(pattern);   // builds ThreadEquivalence: reachable labels from every instruction
            matcher.Run(new TableEvaluator([[false, true]]), Budget());
        }

        // A recursive traversal of this depth needs far more than 256 KB of stack; the explicit stacks use the heap.
        Exception? failure = null;
        var thread = new Thread(() => { try { RunOnce(); } catch (Exception e) { failure = e; } }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        failure.Should().BeNull();

        var onPool = () => Task.Run(RunOnce).Wait();   // the thread-pool thread spec §9.1 names
        onPool.Should().NotThrow();
    }

    // ThreadEquivalence over real defines. Pattern (A | B)+ C compiles to
    // split 1 3 | label A | jump 4 | label B | split 0 5 | label C | done — C's MatchLabel is instruction 5.
    private static ThreadEquivalence EquivalenceFor(string defineOfC)
    {
        var parser = new ExpressionParser(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2 },
            new Dictionary<string, int[]>(), new SimilarityTermTable());
        var program = ProgramCompilerTests.CompileText("(A | B)+ C");
        return new ThreadEquivalence(program, [null, null, parser.ParseDefine(defineOfC)]);
    }

    private static ArrayView View(params int[] labels) => new(labels, labels.Length);

    [Fact]
    public void Threads_are_equivalent_when_the_reachable_defines_read_the_same_rows()
    {
        var eq = EquivalenceFor("FIRST(A.x) > 0");

        eq.Equivalent(1, View(0, 1), 2, View(0, 0), pointer: 5).Should().BeTrue();   // FIRST(A) is row 0 in both
        eq.Equivalent(1, View(0, 0), 2, View(1, 0), pointer: 5).Should().BeFalse();  // row 0 vs row 1
    }

    [Fact]
    public void Threads_are_compared_on_the_labels_a_classifier_navigation_reads()
    {
        var eq = EquivalenceFor("PREV(CLASSIFIER()) = 'A'");

        eq.Equivalent(1, View(0, 0), 2, View(0, 0), pointer: 5).Should().BeTrue();
        eq.Equivalent(1, View(0, 0), 2, View(1, 0), pointer: 5).Should().BeFalse();
    }

    [Fact]
    public void Threads_are_compared_on_the_positions_an_aggregate_reads()
    {
        var eq = EquivalenceFor("COUNT(A.*) = 1");

        eq.Equivalent(1, View(1, 0), 2, View(1, 0), pointer: 5).Should().BeTrue();
        eq.Equivalent(1, View(0, 1), 2, View(1, 0), pointer: 5).Should().BeFalse();
    }

    [Fact]
    public void A_thread_is_equivalent_to_itself_and_empty_matches_are_equivalent()
    {
        var eq = EquivalenceFor("FIRST(A.x) > 0");

        eq.Equivalent(7, View(0, 0), 7, View(1, 0), pointer: 5).Should().BeTrue();   // same thread: an empty cycle
        eq.Equivalent(1, View(), 2, View(), pointer: 0).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~MatcherTests"`
Expected: FAIL — `Matcher` does not exist.

- [ ] **Step 3: Port the support structures**

`IntList`, `IntStack`, `IntMultimap`, `Captures`, `MatchResult` — straight ports (drop `getSizeInBytes`). `MatchResult` becomes:
```csharp
namespace Iverson.Patterns.Matching;

/// <summary>Labels of the matched rows (relative to the pattern start) and exclusion (start, end) pairs.</summary>
internal sealed record MatchResult(bool Matched, ArrayView Labels, ArrayView Exclusions)
{
    public static readonly MatchResult NoMatch = new(false, ArrayView.Empty, ArrayView.Empty);
}
```
`ILabelEvaluator.cs`:
```csharp
namespace Iverson.Patterns.Matching;

/// <summary>Port of Trino 483 <c>LabelEvaluator</c>'s contract with the matcher.</summary>
internal interface ILabelEvaluator
{
    /// <summary>Rows from the pattern start to the end of the search area.</summary>
    int InputLength { get; }
    bool MatchingAtPartitionStart { get; }
    /// <summary>Evaluates the last label of <paramref name="matchedLabels"/>, tentatively appended.</summary>
    bool EvaluateLabel(ArrayView matchedLabels);
}
```

- [ ] **Step 4: Implement `ThreadEquivalence`**

`internal sealed class ThreadEquivalence(Instruction[] program, IReadOnlyList<Expr?> labelDefinitions)` with
`public bool Equivalent(int firstThread, ArrayView firstLabels, int secondThread, ArrayView secondLabels, int pointer)` — a port of Trino's `equivalent` (same-thread or empty labels → true; then the three comparisons over the labels reachable from `pointer`), with deviation 2 for the comparison sets and aggregation positions, and `allPositionsToCompare` ported as-is. Navigations resolve with `ResolvePosition(labels.Length - 1, labels, 0, labels.Length, 0)`, as Trino's `resolvePosition` helper does. The reachable-label sets use this explicit-stack DFS (one `visited` array per start instruction, exactly as Trino):
```csharp
private static HashSet<int>[] ComputeReachableLabels(Instruction[] program)
{
    var result = new HashSet<int>[program.Length];
    var stack = new Stack<int>();
    for (int start = 0; start < program.Length; start++)
    {
        var labels = new HashSet<int>();
        var visited = new bool[program.Length];
        stack.Push(start);
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            if (visited[index]) continue;
            visited[index] = true;
            var instruction = program[index];
            switch (instruction.Kind)
            {
                case InstructionKind.MatchLabel:
                    labels.Add(instruction.First);
                    stack.Push(index + 1);
                    break;
                case InstructionKind.Jump:
                    stack.Push(instruction.First);
                    break;
                case InstructionKind.Split:
                    stack.Push(instruction.Second);
                    stack.Push(instruction.First);
                    break;
                case InstructionKind.MatchStart:
                case InstructionKind.MatchEnd:
                case InstructionKind.ExclusionStart:
                case InstructionKind.ExclusionEnd:
                    stack.Push(index + 1);
                    break;
                case InstructionKind.Done:
                    break;
            }
        }
        result[start] = labels;
    }
    return result;
}
```

- [ ] **Step 5: Implement `Matcher`**

`internal sealed class Matcher(Instruction[] program, ThreadEquivalence equivalence)` with `public MatchResult Run(ILabelEvaluator evaluator, PatternBudget budget)` — a port of Trino's `run` and `Runtime` (deviation 3 for the budget calls; `ExclusionStart`/`ExclusionEnd` both do `captures.Save(thread, inputIndex)`, as Trino's `Save`). `advanceAndSchedule` becomes the following, used as written:
```csharp
private readonly record struct Pending(int Thread, int Pointer);

private void AdvanceAndSchedule(IntList next, int threadId, int pointer, int inputIndex, Runtime runtime, PatternBudget budget)
{
    var stack = runtime.PendingStack;           // a Stack<Pending> owned by Runtime, empty between calls
    stack.Push(new Pending(threadId, pointer));
    while (stack.Count > 0)
    {
        var (thread, at) = stack.Pop();
        budget.Step();

        // avoid empty loops and exponential processing: kill a thread equivalent to one already here
        var threadsHere = runtime.ThreadsAtInstructions.GetArrayView(at);
        bool killed = false;
        for (int i = 0; i < threadsHere.Length; i++)
        {
            int other = threadsHere[i];
            if (equivalence.Equivalent(other, runtime.Captures.GetLabels(other), thread, runtime.Captures.GetLabels(thread), at))
            {
                runtime.ScheduleKill(thread);
                killed = true;
                break;
            }
        }
        if (killed) continue;
        runtime.ThreadsAtInstructions.Add(at, thread);

        var instruction = program[at];
        switch (instruction.Kind)
        {
            case InstructionKind.MatchStart:
                if (inputIndex == 0 && runtime.MatchingAtPartitionStart) stack.Push(new Pending(thread, at + 1));
                else runtime.ScheduleKill(thread);
                break;
            case InstructionKind.MatchEnd:
                if (inputIndex == runtime.InputLength) stack.Push(new Pending(thread, at + 1));
                else runtime.ScheduleKill(thread);
                break;
            case InstructionKind.Jump:
                stack.Push(new Pending(thread, instruction.First));
                break;
            case InstructionKind.Split:
                int forked = runtime.ForkThread(thread, budget);        // fork BEFORE the first branch, as Trino
                stack.Push(new Pending(forked, instruction.Second));    // pushed first → processed after
                stack.Push(new Pending(thread, instruction.First));     // the whole first branch completes first
                break;
            case InstructionKind.ExclusionStart:
            case InstructionKind.ExclusionEnd:
                runtime.Captures.Save(thread, inputIndex);
                stack.Push(new Pending(thread, at + 1));
                break;
            default: // MatchLabel or Done
                runtime.Threads.Set(thread, at);
                next.Add(thread);
                break;
        }
    }
}
```
In `Run`, every `labelEvaluator.evaluateLabel(...)` call is preceded by `budget.Step()`, and `runtime.NewThread(budget)` / `runtime.ForkThread(parent, budget)` call `budget.CheckActiveThreads(activeCount)` after allocating.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~MatcherTests"`
Expected: PASS.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/Matching Iverson.Server/Iverson.Patterns.Tests/MatcherTests.cs
git commit -m "port Trino's row-pattern matcher with explicit-stack traversal and budgets"
```

### Task 7: `PatternQuery.Compile`, `CompiledPattern.Run` and the partition loop

**Files:**
- Create: `Iverson.Server/Iverson.Patterns/PatternQuery.cs`, `CompiledPattern.cs`, `Matching/PartitionLabelEvaluator.cs`, `Matching/PartitionMatcher.cs`
- Test: `Iverson.Server/Iverson.Patterns.Tests/PatternQueryTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: `PatternQuery.Compile(PatternRequest, int maxProgramInstructions)` and `CompiledPattern` (`ReferencedColumns`, `SimilarityTerms`, `MeasureNames`, `Run`) — consumed by Task 8 and by Plan 2 (Api, and `MatchRowsAsync`'s projection and measure-name check).

**Port source:** `operator/window/PatternRecognitionPartition.java@483` `processNextRow` and its `output*`/`skipAfterMatch`/`updateLastMatchedPosition` helpers, in MATCH_RECOGNIZE mode (no `WINDOW` rows-per-match, no `SEEK`, no frame: `searchStart = 0`, `searchEnd = rows.Count`). Header comment as in Task 6.

- [ ] **Step 1: Create the public entry point**

`Iverson.Server/Iverson.Patterns/PatternQuery.cs`:
```csharp
namespace Iverson.Patterns;

public static class PatternQuery
{
    /// <summary>
    /// Parses and validates the request's pattern, subsets, defines and measures, and compiles the program under
    /// <paramref name="maxProgramInstructions"/> (spec §3.1, §5). Throws <see cref="PatternValidationException"/>.
    /// </summary>
    public static CompiledPattern Compile(PatternRequest request, int maxProgramInstructions) =>
        CompiledPattern.Create(request, maxProgramInstructions);
}
```

`Iverson.Server/Iverson.Patterns/CompiledPattern.cs` — `public sealed class CompiledPattern` with:
```csharp
    /// <summary>Every column <c>define</c>/<c>measures</c>/<c>SIMILARITY</c> reads, as written, compared case-insensitively.</summary>
    public IReadOnlySet<string> ReferencedColumns { get; }
    /// <summary>Distinct <c>SIMILARITY</c> terms; <c>Run</c>'s <c>similarity</c> callback is indexed by position in this list.</summary>
    public IReadOnlyList<SimilarityTerm> SimilarityTerms { get; }
    /// <summary>The <c>measures</c> names in request order (for Plan 2's TYPE_ROWS output-column comparison).</summary>
    public IReadOnlyList<string> MeasureNames { get; }

    /// <summary>
    /// Matches one partition, rows already in partition order, and yields its output rows lazily. Match numbers
    /// restart at 1 per call. Throws <see cref="PatternEvaluationException"/> and
    /// <see cref="PatternBudgetExceededException"/> during enumeration.
    /// </summary>
    public IEnumerable<MatchOutputRow> Run(
        IReadOnlyList<IDictionary<string, object?>> partitionRows,
        Func<int, int, double?> similarity,
        PatternBudget budget);

    internal static CompiledPattern Create(PatternRequest request, int maxProgramInstructions);
```

- [ ] **Step 2: Write the failing tests**

`Iverson.Server/Iverson.Patterns.Tests/PatternQueryTests.cs` — the `Run` expectations were produced by Trino 483 for the same data, pattern, defines and measures (plan-level assumptions 23–24; `task7-expectations` probe run), restated in this API's conventions: `match_number` 0 and an empty classifier for unmatched rows, an empty classifier for `ONE_ROW` and empty matches.
```csharp
using FluentAssertions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

public sealed class PatternQueryTests
{
    private static readonly List<IDictionary<string, object?>> D1 = Rows(["id", "value"],
        [1L, 90L], [2L, 80L], [3L, 70L], [4L, 80L], [5L, 90L], [6L, 50L], [7L, 40L], [8L, 60L]);

    private static readonly List<IDictionary<string, object?>> D2 = Rows(["id", "value"],
        [1L, 90L], [2L, 80L], [3L, 70L], [4L, 70L]);

    private static readonly (string, string)[] MS =
        [("match", "MATCH_NUMBER()"), ("val", "RUNNING LAST(value)"), ("label", "CLASSIFIER()")];
    private static readonly (string, string) BD = ("B", "B.value < PREV(B.value)");
    private static readonly (string, string) CD = ("C", "C.value > PREV(C.value)");

    private static PatternRequest Req(string pattern, (string Name, string Expr)[]? define = null,
        (string Name, string Expr)[]? measures = null, RowsPerMatch rows = RowsPerMatch.AllRowsShowEmpty,
        AfterMatchSkipKind skip = AfterMatchSkipKind.PastLastRow, string skipVariable = "",
        string[]? partitionBy = null, SubsetDefinition[]? subsets = null,
        PatternSource source = PatternSource.TypeRows, string? tenant = "TenantId") =>
        new(source, partitionBy ?? [], pattern, subsets ?? [],
            (define ?? []).Select(d => new NamedExpression(d.Name, d.Expr)).ToList(),
            (measures ?? []).Select(m => new NamedExpression(m.Name, m.Expr)).ToList(),
            rows, skip, skipVariable, tenant);

    private static List<MatchOutputRow> Run(PatternRequest request, List<IDictionary<string, object?>> rows,
        Func<int, int, double?>? similarity = null, PatternBudget? budget = null) =>
        PatternQuery.Compile(request, 5000)
            .Run(rows, similarity ?? ((_, _) => null), budget ?? new PatternBudget(10_000, 10_000_000))
            .ToList();

    private static MatchOutputRow Out(long matchNumber, string classifier, params (string Key, object? Value)[] data) =>
        new(data.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal), matchNumber, classifier);

    /// <summary>An ALL_ROWS_* row with the MS measures.</summary>
    private static MatchOutputRow AllRow(long id, long value, long matchNumber, string classifier,
        object? match, object? val, object? label) =>
        Out(matchNumber, classifier, ("id", id), ("value", value), ("match", match), ("val", val), ("label", label));

    /// <summary>A matched ALL_ROWS_* row with the MS measures, where RUNNING LAST(value) is the row's own value.</summary>
    private static MatchOutputRow Matched(long id, long value, long matchNumber, string label) =>
        AllRow(id, value, matchNumber, label, matchNumber, value, label);

    private static void ShouldMatch(List<MatchOutputRow> actual, params MatchOutputRow[] expected) =>
        actual.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering().ComparingByMembers<MatchOutputRow>());

    // ---- Run: expectations from Trino 483 ----

    [Fact]
    public void All_rows_per_match_with_prev_navigation()
    {
        ShouldMatch(Run(Req("A B+ C+", [BD, CD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"), Matched(4, 80, 1, "C"),
            Matched(5, 90, 1, "C"), Matched(6, 50, 2, "A"), Matched(7, 40, 2, "B"), Matched(8, 60, 2, "C"));
    }

    [Fact]
    public void One_row_per_match_shows_empty_matches()
    {
        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", null), ("label", null)),
            Out(2, "", ("match", 2L), ("val", 70L), ("label", "B")),
            Out(3, "", ("match", 3L), ("val", null), ("label", null)));

        ShouldMatch(Run(Req("B+", [BD], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", 70L), ("label", "B")));
    }

    [Fact]
    public void All_rows_modes_show_omit_and_unmatched()
    {
        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.AllRowsShowEmpty), D2),
            AllRow(1, 90, 1, "", 1L, null, null), Matched(2, 80, 2, "B"), Matched(3, 70, 2, "B"),
            AllRow(4, 70, 3, "", 3L, null, null));

        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.AllRowsOmitEmpty), D2),
            Matched(2, 80, 2, "B"), Matched(3, 70, 2, "B"));

        ShouldMatch(Run(Req("B+", [BD], MS, RowsPerMatch.AllRowsWithUnmatched), D2),
            AllRow(1, 90, 0, "", null, null, null), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
            AllRow(4, 70, 0, "", null, null, null));
    }

    private static readonly MatchOutputRow[] PastLast =
    [
        Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
        Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"), Matched(7, 40, 2, "B"),
    ];

    private static readonly MatchOutputRow[] NextRow =
    [
        Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"), Matched(2, 80, 2, "A"),
        Matched(3, 70, 2, "B"), Matched(5, 90, 3, "A"), Matched(6, 50, 3, "B"), Matched(7, 40, 3, "B"),
        Matched(6, 50, 4, "A"), Matched(7, 40, 4, "B"),
    ];

    [Theory]
    [InlineData(AfterMatchSkipKind.PastLastRow, "", false)]
    [InlineData(AfterMatchSkipKind.ToNextRow, "", true)]
    [InlineData(AfterMatchSkipKind.ToFirst, "b", true)]
    [InlineData(AfterMatchSkipKind.ToLast, "B", false)]
    public void After_match_skip_modes(AfterMatchSkipKind skip, string variable, bool overlapping)
    {
        var actual = Run(Req("A B+ | C", [BD, ("C", "FALSE")], MS, skip: skip, skipVariable: variable), D1);
        ShouldMatch(actual, overlapping ? NextRow : PastLast);
    }

    [Theory]
    [InlineData(AfterMatchSkipKind.ToFirst, "A", "*cannot skip to first row of match*")]
    [InlineData(AfterMatchSkipKind.ToLast, "C", "*not present in match*")]
    public void An_illegal_skip_fails_when_a_match_is_found(AfterMatchSkipKind skip, string variable, string message)
    {
        var act = () => Run(Req("A B+ | C", [BD, ("C", "FALSE")], MS, skip: skip, skipVariable: variable), D1);
        act.Should().Throw<PatternEvaluationException>().WithMessage(message);
    }

    [Fact]
    public void Excluded_rows_are_omitted_from_all_rows_output()
    {
        ShouldMatch(Run(Req("A {- B+ -} C+", [BD, CD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(4, 80, 1, "C"), Matched(5, 90, 1, "C"),
            Matched(6, 50, 2, "A"), Matched(8, 60, 2, "C"));
    }

    [Fact]
    public void Running_and_final_measures()
    {
        MatchOutputRow Row(long id, long value, long mn, string cls, long fl, long fc, long rc) =>
            Out(mn, cls, ("id", id), ("value", value), ("fl", fl), ("fc", fc), ("rc", rc));

        ShouldMatch(Run(Req("A B+ C+", [BD, CD],
                [("fl", "FINAL LAST(value)"), ("fc", "FINAL COUNT(*)"), ("rc", "RUNNING COUNT(*)")]), D1),
            Row(1, 90, 1, "A", 90, 5, 1), Row(2, 80, 1, "B", 90, 5, 2), Row(3, 70, 1, "B", 90, 5, 3),
            Row(4, 80, 1, "C", 90, 5, 4), Row(5, 90, 1, "C", 90, 5, 5),
            Row(6, 50, 2, "A", 60, 3, 1), Row(7, 40, 2, "B", 60, 3, 2), Row(8, 60, 2, "C", 60, 3, 3));
    }

    [Fact]
    public void Match_number_inside_define_is_the_number_the_attempt_will_receive()
    {
        ShouldMatch(Run(Req("A", [("A", "MATCH_NUMBER() <= 2")], MS), D2),
            Matched(1, 90, 1, "A"), Matched(2, 80, 2, "A"));

        ShouldMatch(Run(Req("A?", [("A", "MATCH_NUMBER() = 2")], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", null), ("label", null)),
            Out(2, "", ("match", 2L), ("val", 80L), ("label", "A")),
            Out(3, "", ("match", 3L), ("val", null), ("label", null)),
            Out(4, "", ("match", 4L), ("val", null), ("label", null)));
    }

    [Fact]
    public void Subsets_and_navigation_measures()
    {
        ShouldMatch(Run(Req("A B+ C+", [BD, CD], [("s", "FIRST(U.value) + LAST(U.value)"), ("c", "COUNT(U.*)")],
                RowsPerMatch.OneRow, subsets: [new SubsetDefinition("U", ["B", "C"])]), D1),
            Out(1, "", ("s", 170L), ("c", 4L)), Out(2, "", ("s", 100L), ("c", 2L)));

        ShouldMatch(Run(Req("A B+ C+", [BD, CD],
                [("n", "PREV(FIRST(B.value), 1) + NEXT(LAST(B.value), 1)"), ("f1", "FIRST(B.value, 1)"), ("l1", "LAST(C.value, 1)")],
                RowsPerMatch.OneRow), D1),
            Out(1, "", ("n", 170L), ("f1", 70L), ("l1", 80L)), Out(2, "", ("n", 110L), ("f1", null), ("l1", null)));
    }

    [Fact]
    public void Variable_names_are_case_insensitive_and_classified_in_upper_case()
    {
        ShouldMatch(Run(Req("a b+", [("b", "B.value < PREV(b.value)")], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
            Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"), Matched(7, 40, 2, "B"));
    }

    [Fact]
    public void Define_may_read_a_following_row()
    {
        ShouldMatch(Run(Req("A B", [("A", "NEXT(A.value) < A.value")], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"));
    }

    [Fact]
    public void Anchors_permute_and_reluctant_quantifiers()
    {
        ShouldMatch(Run(Req("^ A", [("A", "TRUE")], MS), D2), Matched(1, 90, 1, "A"));

        ShouldMatch(Run(Req("PERMUTE(A, B)", [("A", "A.value = 80"), ("B", "B.value = 90")], MS), D2),
            Matched(1, 90, 1, "B"), Matched(2, 80, 1, "A"));

        ShouldMatch(Run(Req("A B+?", [BD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"));
    }

    [Fact]
    public void History_dependent_defines_are_not_pruned_away()
    {
        // Trino's testExponentialMatch: the match B B B B B B B B LAST is the last of over 2^9 candidates.
        var rows = Rows(["value"], [1L], [2L], [3L], [4L], [5L], [6L], [7L], [8L], [9L]);
        var define = "FIRST(CLASSIFIER()) = 'B' AND FIRST(CLASSIFIER(), 1) = 'B' AND FIRST(CLASSIFIER(), 2) = 'B' AND " +
                     "FIRST(CLASSIFIER(), 3) = 'B' AND FIRST(CLASSIFIER(), 4) = 'B' AND FIRST(CLASSIFIER(), 5) = 'B' AND " +
                     "FIRST(CLASSIFIER(), 6) = 'B' AND FIRST(CLASSIFIER(), 7) = 'B'";

        var output = Run(Req("(A | B)+ LAST", [("LAST", define)], [("classy", "CLASSIFIER()")]), rows);

        output.Select(r => r.Data["classy"]).Should().Equal("B", "B", "B", "B", "B", "B", "B", "B", "LAST");
    }

    [Fact]
    public void One_row_output_names_partition_columns_as_the_row_spells_them()
    {
        var partition = Rows(["Part", "id", "value"], ["x", 1L, 10L], ["x", 2L, 20L]);

        ShouldMatch(Run(Req("A", measures: [("v", "A.value")], rows: RowsPerMatch.OneRow, partitionBy: ["part"]), partition),
            Out(1, "", ("Part", "x"), ("v", 10L)), Out(2, "", ("Part", "x"), ("v", 20L)));
    }

    [Fact]
    public void Chunks_one_row_output_starts_with_parent_key()
    {
        var chunks = Rows(["parent_key", "chunk_index", "text"], ["doc1", 0L, "intro"], ["doc1", 1L, "refund"]);

        ShouldMatch(Run(Req("A B", [("B", "B.text = 'refund'")], [("n", "COUNT(*)")], RowsPerMatch.OneRow,
                source: PatternSource.Chunks), chunks),
            Out(1, "", ("parent_key", "doc1"), ("n", 2L)));
    }

    [Fact]
    public void Similarity_scores_reach_defines_and_measures_by_row_and_term()
    {
        var rows = Rows(["id", "title"], [1L, "a"], [2L, "b"], [3L, "c"]);
        Func<int, int, double?> scores = (row, term) => term == 0 ? (row == 1 ? 0.9 : 0.1) : null;

        ShouldMatch(Run(Req("A", [("A", "SIMILARITY(title, 'refund') > 0.5")], [("s", "SIMILARITY(title, 'refund')")],
                RowsPerMatch.OneRow), rows, scores),
            Out(1, "", ("s", 0.9)));
    }

    [Fact]
    public void Run_time_errors_and_budgets_surface_during_enumeration()
    {
        var divide = () => Run(Req("A", [("A", "A.value / 0 > 1")]), D2);
        divide.Should().Throw<PatternEvaluationException>();

        var threads = () => Run(Req("A | B"), D2, budget: new PatternBudget(maxActiveThreads: 1, maxSteps: 1_000));
        threads.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxActiveThreads");
    }

    // ---- Compile: validation (spec §1, §2) ----

    private static void Rejects(PatternRequest request, string message = "*")
    {
        var act = () => PatternQuery.Compile(request, 5000);
        act.Should().Throw<PatternValidationException>().WithMessage(message);
    }

    [Fact]
    public void Define_entries_must_name_distinct_pattern_variables()
    {
        Rejects(Req("A B", [("B", "TRUE"), ("b", "FALSE")]), "*twice*");
        Rejects(Req("A B", [("C", "TRUE")]), "*not a primary pattern variable*");
    }

    [Fact]
    public void Measure_names_must_be_distinct_ordinally_and_not_the_tenant_column()
    {
        Rejects(Req("A", measures: [("m", "1"), ("m", "2")]), "*m*");
        PatternQuery.Compile(Req("A", measures: [("m", "1"), ("M", "2")]), 5000).MeasureNames.Should().Equal("m", "M");
        Rejects(Req("A", measures: [("tenantid", "1")], tenant: "TenantId"), "*tenantid*");
        PatternQuery.Compile(Req("A", measures: [("tenantid", "1")], tenant: null), 5000).Should().NotBeNull();
    }

    [Fact]
    public void Chunks_requests_are_checked_against_the_chunk_columns()
    {
        Rejects(Req("A", partitionBy: ["x"], source: PatternSource.Chunks));
        Rejects(Req("A", measures: [("parent_key", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), "*parent_key*");
        PatternQuery.Compile(Req("A", measures: [("text", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), 5000);
        Rejects(Req("A", measures: [("text", "1")], rows: RowsPerMatch.AllRowsShowEmpty, source: PatternSource.Chunks), "*text*");
        PatternQuery.Compile(Req("A", measures: [("Parent_Key", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), 5000);
        Rejects(Req("A", [("A", "A.title = 'x'")], source: PatternSource.Chunks), "*title*");
        PatternQuery.Compile(Req("A", [("A", "A.TEXT = 'x' AND chunk_index > 0")], source: PatternSource.Chunks), 5000);
        Rejects(Req("A", [("A", "SIMILARITY(chunk_index, 'x') > 0")], source: PatternSource.Chunks), "*SIMILARITY*");
        PatternQuery.Compile(Req("A", [("A", "SIMILARITY(TEXT, 'x') > 0")], source: PatternSource.Chunks), 5000);
    }

    [Fact]
    public void Exclusion_cannot_combine_with_unmatched_rows() =>
        Rejects(Req("A {- B -}", rows: RowsPerMatch.AllRowsWithUnmatched), "*xclusion*");

    [Fact]
    public void Skip_variables_must_fit_the_skip_mode()
    {
        Rejects(Req("A B", skip: AfterMatchSkipKind.ToFirst, skipVariable: ""));
        Rejects(Req("A B", skip: AfterMatchSkipKind.ToFirst, skipVariable: "C"), "*C*");
        Rejects(Req("A B", skip: AfterMatchSkipKind.PastLastRow, skipVariable: "A"));
        PatternQuery.Compile(Req("A B", skip: AfterMatchSkipKind.ToLast, skipVariable: "u",
            subsets: [new SubsetDefinition("U", ["A", "B"])]), 5000);
    }

    [Fact]
    public void The_program_cap_is_enforced_by_compile() =>
        Rejects(Req("A{5000}"), "*MaxProgramInstructions*");

    [Fact]
    public void Compile_exposes_referenced_columns_similarity_terms_and_measure_names()
    {
        var compiled = PatternQuery.Compile(Req("A B",
            [("A", "SIMILARITY(title, 'refund') > 0.5"), ("B", "B.Price > PREV(B.price)")],
            [("t", "SIMILARITY(Title, 'refund')"), ("q", "SUM(B.qty)")]), 5000);

        compiled.ReferencedColumns.Should().BeEquivalentTo(["title", "Price", "qty"]);
        compiled.ReferencedColumns.Contains("PRICE").Should().BeTrue();
        compiled.SimilarityTerms.Should().Equal(new SimilarityTerm("title", "refund"));
        compiled.MeasureNames.Should().Equal("t", "q");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~PatternQueryTests"`
Expected: FAIL — `PatternQuery` does not exist.

- [ ] **Step 4: Implement `CompiledPattern.Create`**

In order, each failure a `PatternValidationException`:
1. `PatternParser.Parse(request.Pattern, request.Subsets)`.
2. `define` names, upper-cased: distinct ("pattern variable with name: X is defined twice"), each a primary variable ("defined variable: X is not a primary pattern variable").
3. `measures` names: distinct ordinally; none equal to `TenantColumn` case-insensitively (when non-null). Messages name the offending measure (e.g. "Measure name 'tenantid' is reserved for the tenant column").
4. `Chunks` only: `PartitionBy` empty; no measure name equal (ordinal) to an output column of the shape — `parent_key` for `OneRow`, `parent_key`/`chunk_index`/`text` otherwise — with a message naming the measure.
5. `HasExclusion && RowsPerMatch == AllRowsWithUnmatched` → "Pattern exclusion is not allowed with ALL ROWS PER MATCH WITH UNMATCHED ROWS".
6. Skip: `ToFirst`/`ToLast` need a non-empty `SkipVariable` that (upper-cased) is a primary variable or subset name; `PastLastRow`/`ToNextRow` need it empty. The skip navigation is Trino's: `new Navigation(labels, Last: kind == ToLast, Running: false, 0, 0)` over the variable's label set.
7. Label indexes = `Variables` order; subset label sets sorted; `ProgramCompiler.Compile(root, labels, maxProgramInstructions)`.
8. One `ExpressionParser` and one `SimilarityTermTable`: `ParseDefine` each `define` (a variable with no `define` has `null` — always true), then `ParseMeasure` each measure.
9. `Chunks` only: every referenced column is `parent_key`, `chunk_index` or `text` (case-insensitive), else an error naming it; every `SIMILARITY` term's column is `text` (case-insensitive), else an error naming `SIMILARITY`.
10. `ThreadEquivalence(program, defines by label)` and one `Matcher`.

- [ ] **Step 5: Implement `Run` (the partition loop)**

`Matching/PartitionLabelEvaluator.cs` implements `ILabelEvaluator` over an `EvaluationContext` (`InputLength = rows.Count − patternStart`; `MatchingAtPartitionStart = patternStart == 0`; `EvaluateLabel` sets `PatternStart = patternStart`, `EmptyMatch = false`, `MatchedLabels` and `CurrentRow = patternStart + matchedLabels.Length − 1`, and returns `define is null || ExpressionEvaluator.EvaluateDefine(define, ctx)`, calling `budget.Step()` is the matcher's job). `Matching/PartitionMatcher.cs` ports `processNextRow` as an iterator: `matchNumber = 1`, `lastSkipped = lastMatched = -1`; for each `current` in `0..rows.Count−1` not `<= lastSkipped`, run the matcher from `patternStart = current` with `ctx.MatchNumber = matchNumber`, then:
- **no match:** for `AllRowsWithUnmatched`, if `current > lastMatched` yield the unmatched row (input columns, every measure `null`, `MatchNumber` 0, classifier `""`); `lastSkipped = current`.
- **empty match** (`Labels.Length == 0`): for `OneRow` and `AllRowsShowEmpty`/`AllRowsWithUnmatched` yield one row for `current` with measures evaluated under `EmptyMatch = true` (`MatchNumber = matchNumber`, classifier `""`); `lastSkipped = current`; `matchNumber++`.
- **match:** `OneRow` → one row of the partition columns (`parent_key` for `Chunks`; each `PartitionBy` entry emitted under the row's own key spelling, found case-insensitively among the row's keys) plus the measures computed at `CurrentRow = patternStart + Labels.Length − 1`, classifier `""`. `AllRows*` → one row per matched row outside the exclusion pairs, each with every input column (the row's keys, in enumeration order) plus the measures computed at that row, classifier = that row's upper-case label. Measures use `PatternStart = current`, `MatchedLabels = result.Labels`, `EmptyMatch = false`. Then `lastMatched = max(lastMatched, patternStart + Labels.Length − 1)`; skip: `PastLastRow` → `lastSkipped = patternStart + Labels.Length − 1`; `ToNextRow` → `lastSkipped = current`; `ToFirst`/`ToLast` → `position = skipNavigation.ResolvePosition(patternStart + Labels.Length − 1, Labels, 0, rows.Count, patternStart)`, `-1` → `PatternEvaluationException("AFTER MATCH SKIP failed: pattern variable is not present in match")`, `== patternStart` → `PatternEvaluationException("AFTER MATCH SKIP failed: cannot skip to first row of match")`, else `lastSkipped = position − 1`; `matchNumber++`.

Output `Data` dictionaries use `StringComparer.Ordinal`. `CompiledPattern.Run` returns `PartitionMatcher`'s iterator.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "FullyQualifiedName~PatternQueryTests"`
Expected: PASS.

- [ ] **Step 7: Run the whole unit suite**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests`
Expected: PASS (no container is started before Task 8).

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Patterns/PatternQuery.cs Iverson.Server/Iverson.Patterns/CompiledPattern.cs Iverson.Server/Iverson.Patterns/Matching/PartitionLabelEvaluator.cs Iverson.Server/Iverson.Patterns/Matching/PartitionMatcher.cs Iverson.Server/Iverson.Patterns.Tests/PatternQueryTests.cs
git commit -m "add PatternQuery.Compile and the per-partition matching loop"
```

### Task 8: Trino 483 differential oracle (spec §9.2)

**Files:**
- Modify: `Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj` (add Testcontainers; copy the case file to the output)
- Create: `Iverson.Server/Iverson.Patterns.Tests/Oracle/TrinoContainerFixture.cs`, `Oracle/TrinoClient.cs`, `Oracle/OracleCase.cs`, `Oracle/OracleRunner.cs`, `Oracle/PortedTrinoCasesTests.cs`, `Oracle/GeneratedCasesTests.cs`, `Oracle/trino-483-cases.json`

**Interfaces:**
- Consumes: `PatternQuery`, `CompiledPattern`, the public request types (Tasks 1, 7).

**What is compared** (plan-level assumptions 10, 24, 26): each case runs through `PatternQuery` and through `trinodb/trino:483` as an inline-`VALUES` `MATCH_RECOGNIZE` query. Both succeed with the same rows, or both fail (Trino `USER_ERROR` ⇔ our `PatternValidationException`/`PatternEvaluationException`). Rows are compared projected onto the query's select list; within a partition in order, and the set of partitions as a set (Trino's partition order varies between runs). Unmatched rows need no mapping: their measures are NULL on both sides, and `match_number`/`classifier` are compared only through measures.

- [ ] **Step 1: Generate the ported-case file**

In a scratch directory outside the repository: save Appendix B's two scripts as `extract_sites.py` and `structure_sites.py`, then
```bash
curl -sO https://raw.githubusercontent.com/trinodb/trino/483/core/trino-main/src/test/java/io/trino/sql/query/TestRowPatternMatching.java
md5sum TestRowPatternMatching.java   # expect e6255d4849956b450e6d7ca0b29781db
python3 extract_sites.py TestRowPatternMatching.java   # prints 141
python3 structure_sites.py                             # prints: cases 130 excluded 11, then the exclusion reasons
md5sum cases.json                                      # expect 3a778e9c636f004f720acc243d7dbefa
cp cases.json <repo>/Iverson.Server/Iverson.Patterns.Tests/Oracle/trino-483-cases.json
```
Every md5 must match; stop and report if one does not. The 11 exclusions are Trino features outside spec §1–§2 (subqueries ×6, a quoted identifier, `CAST`, two `MATCH_RECOGNIZE` clauses, `EXISTS`, `lower`).

- [ ] **Step 2: Add Testcontainers and the case file to the test project**

In `Iverson.Patterns.Tests.csproj`, add to the `PackageReference` item group:
```xml
    <PackageReference Include="Testcontainers" Version="4.15.0" />
```
and a new item group:
```xml
  <ItemGroup>
    <None Include="Oracle/trino-483-cases.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Create the fixture and the REST client**

`Oracle/TrinoContainerFixture.cs`:
```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

/// <summary>Pinned: the oracle's semantics are Trino 483's, the version every probe in the spec was run against.</summary>
public static class TrinoImage
{
    public const string Tag = "trinodb/trino:483";
}

/// <summary>ONE Trino container for every oracle test class (a class fixture would start one per class).</summary>
public sealed class TrinoContainerFixture : IAsyncLifetime
{
    private const int HttpPort = 8080;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(TrinoImage.Tag)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(HttpPort))
        .Build();

    public TrinoClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Client = new TrinoClient(new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}"));

        // Trino answers HTTP before it can run queries: ready means SELECT 1 returns a row.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            try
            {
                var result = await Client.QueryAsync("SELECT 1");
                if (result.Error is null && result.Rows.Count == 1) return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Trino did not become query-ready in 3 minutes.");
            await Task.Delay(2000);
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class TrinoCollection : ICollectionFixture<TrinoContainerFixture>
{
    public const string Name = "trino";
}
```

`Oracle/TrinoClient.cs`:
```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Iverson.Patterns.Tests.Oracle;

public sealed record TrinoResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, string? Error, string? ErrorType);

/// <summary>Minimal client for Trino's REST protocol: POST /v1/statement, then follow nextUri.</summary>
public sealed class TrinoClient(Uri baseUri) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseUri };

    public async Task<TrinoResult> QueryAsync(string sql)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, "/v1/statement") { Content = new StringContent(sql, Encoding.UTF8) };
        post.Headers.Add("X-Trino-User", "oracle");
        var page = await Send(post);

        var columns = new List<string>();
        var types = new List<string>();
        var rows = new List<object?[]>();
        while (true)
        {
            if (columns.Count == 0 && page.TryGetProperty("columns", out var cols))
                foreach (var c in cols.EnumerateArray())
                {
                    columns.Add(c.GetProperty("name").GetString()!);
                    types.Add(c.GetProperty("type").GetString()!);
                }
            if (page.TryGetProperty("data", out var data))
                foreach (var row in data.EnumerateArray())
                    rows.Add(row.EnumerateArray().Select((v, i) => Read(v, types[i])).ToArray());
            if (page.TryGetProperty("error", out var error))
                return new TrinoResult(columns, rows, error.GetProperty("message").GetString(), error.GetProperty("errorType").GetString());
            if (!page.TryGetProperty("nextUri", out var next))
                return new TrinoResult(columns, rows, null, null);

            using var get = new HttpRequestMessage(HttpMethod.Get, next.GetString());
            get.Headers.Add("X-Trino-User", "oracle");
            page = await Send(get);
        }
    }

    private async Task<JsonElement> Send(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static object? Read(JsonElement value, string type)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (type is "double" or "real")
            return value.ValueKind == JsonValueKind.String
                ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)   // "NaN", "Infinity"
                : value.GetDouble();
        if (type is "bigint" or "integer" or "smallint" or "tinyint") return value.GetInt64();
        if (type == "boolean") return value.GetBoolean();
        if (type.StartsWith("varchar", StringComparison.Ordinal)) return value.GetString();
        throw new NotSupportedException($"The oracle does not compare Trino type {type}.");
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: Create the case model and the runner**

`Oracle/OracleCase.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Iverson.Patterns.Tests.Oracle;

/// <summary>One MATCH_RECOGNIZE case, in the shape of trino-483-cases.json. Row values are SQL literals.</summary>
public sealed record OracleCase(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> PartitionBy,
    IReadOnlyList<(string Column, bool Descending)> OrderBy,
    IReadOnlyList<(string Name, string Expression)> Measures,
    RowsPerMatch RowsPerMatch,
    AfterMatchSkipKind Skip,
    string SkipVariable,
    string Pattern,
    IReadOnlyList<(string Name, IReadOnlyList<string> Variables)> Subsets,
    IReadOnlyList<(string Name, string Expression)> Define,
    IReadOnlyList<(string Source, string Alias)> Select)
{
    public override string ToString() => Name;

    public static IReadOnlyList<OracleCase> LoadPorted()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Oracle", "trino-483-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(c => new OracleCase(
            $"{c.GetProperty("method").GetString()}[{c.GetProperty("index").GetInt32()}]",
            Strings(c.GetProperty("columns")),
            c.GetProperty("rows").EnumerateArray().Select(Strings).ToList(),
            Strings(c.GetProperty("partitionBy")),
            c.GetProperty("orderBy").EnumerateArray().Select(o => (o[0].GetString()!, o[1].GetBoolean())).ToList(),
            Pairs(c.GetProperty("measures")),
            Enum.Parse<RowsPerMatch>(c.GetProperty("rowsPerMatch").GetString()!),
            Enum.Parse<AfterMatchSkipKind>(c.GetProperty("skip").GetString()!),
            c.GetProperty("skipVariable").GetString()!,
            c.GetProperty("pattern").GetString()!,
            c.GetProperty("subsets").EnumerateArray().Select(s => (s[0].GetString()!, Strings(s[1]))).ToList(),
            Pairs(c.GetProperty("define")),
            Pairs(c.GetProperty("select")))).ToList();

        static IReadOnlyList<string> Strings(JsonElement a) => a.EnumerateArray().Select(e => e.GetString()!).ToList();
        static IReadOnlyList<(string, string)> Pairs(JsonElement a) =>
            a.EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetString()!)).ToList();
    }

    /// <summary>The Trino query for this case (the same rendering Appendix B's validation ran: 130/130 reproduced).</summary>
    public string ToSql()
    {
        var select = string.Join(", ", Select.Select(s => s.Source == "*" ? "*" : $"m.{s.Source} AS {s.Alias}"));
        var values = string.Join(", ", Rows.Select(r => "(" + string.Join(", ", r) + ")"));
        var body = new List<string>();
        if (PartitionBy.Count > 0) body.Add("PARTITION BY " + string.Join(", ", PartitionBy));
        if (OrderBy.Count > 0) body.Add("ORDER BY " + string.Join(", ", OrderBy.Select(o => o.Column + (o.Descending ? " DESC" : ""))));
        if (Measures.Count > 0) body.Add("MEASURES " + string.Join(", ", Measures.Select(m => $"{m.Expression} AS {m.Name}")));
        body.Add(RowsPerMatch switch
        {
            RowsPerMatch.OneRow => "ONE ROW PER MATCH",
            RowsPerMatch.AllRowsShowEmpty => "ALL ROWS PER MATCH SHOW EMPTY MATCHES",
            RowsPerMatch.AllRowsOmitEmpty => "ALL ROWS PER MATCH OMIT EMPTY MATCHES",
            _ => "ALL ROWS PER MATCH WITH UNMATCHED ROWS",
        });
        body.Add(Skip switch
        {
            AfterMatchSkipKind.PastLastRow => "AFTER MATCH SKIP PAST LAST ROW",
            AfterMatchSkipKind.ToNextRow => "AFTER MATCH SKIP TO NEXT ROW",
            AfterMatchSkipKind.ToFirst => "AFTER MATCH SKIP TO FIRST " + SkipVariable,
            _ => "AFTER MATCH SKIP TO LAST " + SkipVariable,
        });
        body.Add($"PATTERN ({Pattern})");
        if (Subsets.Count > 0) body.Add("SUBSET " + string.Join(", ", Subsets.Select(s => $"{s.Name} = ({string.Join(", ", s.Variables)})")));
        if (Define.Count > 0) body.Add("DEFINE " + string.Join(", ", Define.Select(d => $"{d.Name} AS {d.Expression}")));
        return $"SELECT {select} FROM (VALUES {values}) t({string.Join(", ", Columns)}) MATCH_RECOGNIZE ({string.Join(" ", body)}) AS m";
    }

    /// <summary>A SQL literal from the case file → the C# value a row source would hold.</summary>
    public static object? Literal(string sql) =>
        sql.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
        : sql.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
        : sql.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
        : sql.StartsWith('\'') ? sql[1..^1].Replace("''", "'")
        : long.Parse(sql, CultureInfo.InvariantCulture);
}
```

`Oracle/OracleRunner.cs`:
```csharp
using FluentAssertions;

namespace Iverson.Patterns.Tests.Oracle;

public static class OracleRunner
{
    public static async Task AssertAgreesAsync(TrinoClient trino, OracleCase c)
    {
        var sql = c.ToSql();
        var expected = await trino.QueryAsync(sql);

        List<List<object?[]>>? ours = null;
        Exception? ourError = null;
        try { ours = RunOurs(c, expected.Columns); }
        catch (Exception e) when (e is PatternValidationException or PatternEvaluationException) { ourError = e; }

        if (expected.Error is not null)
        {
            ourError.Should().NotBeNull($"Trino rejected {c} ({expected.Error}); so must we.\n{sql}");
            expected.ErrorType.Should().Be("USER_ERROR", $"{c}: only user errors are comparable.\n{sql}");
            return;
        }
        ourError.Should().BeNull($"Trino accepted {c}.\n{sql}");

        var trinoPartitions = GroupByPartition(c, expected.Columns, expected.Rows);
        var ourPartitions = ours!.Select(p => (Key: PartitionKey(c, expected.Columns, p.FirstOrDefault()), Rows: p))
            .Where(p => p.Rows.Count > 0).ToList();

        ourPartitions.Select(p => p.Key).Should().BeEquivalentTo(trinoPartitions.Keys, $"{c}: partitions differ.\n{sql}");
        foreach (var (key, rows) in ourPartitions)
            rows.Select(Render).Should().Equal(trinoPartitions[key].Select(Render), $"{c}: partition '{key}' differs.\n{sql}");
    }

    /// <summary>Our output, one list per partition, each row projected onto Trino's result columns.</summary>
    private static List<List<object?[]>> RunOurs(OracleCase c, IReadOnlyList<string> columns)
    {
        var request = new PatternRequest(PatternSource.TypeRows, c.PartitionBy, c.Pattern,
            c.Subsets.Select(s => new SubsetDefinition(s.Name, s.Variables)).ToList(),
            c.Define.Select(d => new NamedExpression(d.Name, d.Expression)).ToList(),
            c.Measures.Select(m => new NamedExpression(m.Name, m.Expression)).ToList(),
            c.RowsPerMatch, c.Skip, c.SkipVariable, TenantColumn: null);
        var compiled = PatternQuery.Compile(request, maxProgramInstructions: 5000);

        var rows = c.Rows.Select(r => (IDictionary<string, object?>)c.Columns.Zip(r)
                .ToDictionary(p => p.First, p => OracleCase.Literal(p.Second), StringComparer.OrdinalIgnoreCase))
            .ToList();
        var keys = c.PartitionBy.Select(p => (Column: p, Descending: false))
            .Concat(c.OrderBy).ToList();
        var ordered = rows.OrderBy(r => r, new RowOrder(keys)).ToList();   // OrderBy is stable

        var partitions = new List<List<IDictionary<string, object?>>>();
        foreach (var row in ordered)
        {
            if (partitions.Count == 0 || !SamePartition(c, partitions[^1][0], row)) partitions.Add([]);
            partitions[^1].Add(row);
        }

        var budget = new PatternBudget(maxActiveThreads: 10_000, maxSteps: 10_000_000);
        var aliasToSource = c.Select.Where(s => s.Source != "*")
            .ToDictionary(s => s.Alias, s => s.Source, StringComparer.OrdinalIgnoreCase);
        return partitions.Select(p => compiled.Run(p, (_, _) => null, budget)
                .Select(o => columns.Select(col => Lookup(o.Data, aliasToSource.GetValueOrDefault(col, col))).ToArray())
                .ToList())
            .ToList();
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> data, string name) =>
        data.First(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool SamePartition(OracleCase c, IDictionary<string, object?> a, IDictionary<string, object?> b) =>
        c.PartitionBy.All(p => Equals(a[p], b[p]));

    private static Dictionary<string, List<object?[]>> GroupByPartition(OracleCase c, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var result = new Dictionary<string, List<object?[]>>();
        foreach (var row in rows)
        {
            var key = PartitionKey(c, columns, row);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(row);
        }
        return result;
    }

    /// <summary>Every partitioned ported case selects its partition columns (verified at plan time).</summary>
    private static string PartitionKey(OracleCase c, IReadOnlyList<string> columns, object?[]? row)
    {
        if (row is null || c.PartitionBy.Count == 0) return "";
        return string.Join("|", c.PartitionBy.Select(p =>
        {
            var alias = c.Select.FirstOrDefault(s => string.Equals(s.Source, p, StringComparison.OrdinalIgnoreCase)).Alias ?? p;
            var index = columns.ToList().FindIndex(col => string.Equals(col, alias, StringComparison.OrdinalIgnoreCase));
            return Render(row[index]);
        }));
    }

    private static string Render(object?[] row) => string.Join(", ", row.Select(Render));

    /// <summary>Type-insensitive numerics (our long 3 = Trino bigint 3; our double 3.5 = Trino double 3.5).</summary>
    private static string Render(object? v) => v switch
    {
        null => "NULL",
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d when d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => "'" + s + "'",
        _ => v.ToString()!,
    };

    private sealed class RowOrder(IReadOnlyList<(string Column, bool Descending)> keys) : IComparer<IDictionary<string, object?>>
    {
        public int Compare(IDictionary<string, object?>? x, IDictionary<string, object?>? y)
        {
            foreach (var (column, descending) in keys)
            {
                var a = x![column];
                var b = y![column];
                int cmp = (a, b) switch
                {
                    (null, null) => 0,
                    (null, _) => 1,           // Trino's default: NULLS LAST
                    (_, null) => -1,
                    (string sa, string sb) => string.CompareOrdinal(sa, sb),   // Trino orders varchar by binary value
                    _ => Comparer<object>.Default.Compare(a, b),
                };
                if (cmp != 0) return descending && a is not null && b is not null ? -cmp : cmp;
            }
            return 0;
        }
    }
}
```

- [ ] **Step 5: Create the ported-case tests**

`Oracle/PortedTrinoCasesTests.cs`:
```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

[Trait("Category", "Integration")]
[Collection(TrinoCollection.Name)]
public sealed class PortedTrinoCasesTests(TrinoContainerFixture fixture)
{
    public static TheoryData<OracleCase> Cases()
    {
        var data = new TheoryData<OracleCase>();
        foreach (var c in OracleCase.LoadPorted()) data.Add(c);
        return data;
    }

    [Fact]
    public void All_130_ported_cases_are_present() => OracleCase.LoadPorted().Should().HaveCount(130);

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Agrees_with_Trino(OracleCase c) => OracleRunner.AssertAgreesAsync(fixture.Client, c);
}
```

- [ ] **Step 6: Create the generated-case tests**

`Oracle/GeneratedCasesTests.cs` — a seeded generator restricted to constructs with identical semantics in both engines (spec §9.2): integer data 0–9 and integer literals only (Trino types `2.5` as DECIMAL; plan-level assumption 10), comparisons without arithmetic (Trino `integer` overflows at 32 bits), no `AVG`, `SIMILARITY` or `TIMESTAMPDIFF`, and lower- and mixed-case variable names:
```csharp
using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

[Trait("Category", "Integration")]
[Collection(TrinoCollection.Name)]
public sealed class GeneratedCasesTests(TrinoContainerFixture fixture)
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 0; seed < 200; seed++) data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public Task Agrees_with_Trino(int seed) => OracleRunner.AssertAgreesAsync(fixture.Client, Generate(seed));

    private static readonly string[] Variables = ["A", "B", "C"];

    internal static OracleCase Generate(int seed)
    {
        var random = new Random(seed);
        string Spell(string v) => random.Next(3) switch { 0 => v, 1 => v.ToLowerInvariant(), _ => v };  // mixed case

        var used = new List<string>();
        string Primary(int depth)
        {
            int roll = random.Next(depth >= 2 ? 6 : 10);
            string p;
            if (roll < 6)
            {
                var v = Variables[random.Next(Variables.Length)];
                if (!used.Contains(v)) used.Add(v);
                p = Spell(v);
            }
            else if (roll < 8) p = "(" + Alternation(depth + 1) + ")";
            else if (roll < 9) p = "PERMUTE(" + string.Join(", ", Enumerable.Range(0, random.Next(2, 4)).Select(_ => Alternation(depth + 1))) + ")";
            else p = "{- " + Alternation(depth + 1) + " -}";
            string quantifier = random.Next(5) switch
            {
                0 => "*", 1 => "+", 2 => "?",
                3 => "{" + random.Next(0, 2) + "," + random.Next(2, 4) + "}",
                _ => "",
            };
            return p + quantifier + (quantifier.Length > 0 && random.Next(3) == 0 ? "?" : "");
        }
        string Concatenation(int depth) => string.Join(" ", Enumerable.Range(0, random.Next(1, 4)).Select(_ => Primary(depth)));
        string Alternation(int depth) => string.Join(" | ", Enumerable.Range(0, depth >= 2 ? 1 : random.Next(1, 3)).Select(_ => Concatenation(depth)));

        var pattern = (random.Next(8) == 0 ? "^ " : "") + Alternation(0) + (random.Next(8) == 0 ? " $" : "");

        string DefineFor(string v)
        {
            var q = Spell(v);
            return random.Next(7) switch
            {
                0 => $"{q}.v > PREV({q}.v)",
                1 => $"{q}.v < PREV({q}.v)",
                2 => $"{q}.v = {random.Next(10)}",
                3 => $"{q}.v > {random.Next(10)}",
                4 => $"COUNT({q}.*) <= {random.Next(1, 4)}",
                5 => $"FIRST({q}.v) < {q}.v",
                _ => "TRUE",
            };
        }
        var define = used.Where(_ => random.Next(4) != 0).Select(v => (Spell(v), DefineFor(v))).ToList();
        if (define.Count == 0) define.Add((Spell(used[0]), DefineFor(used[0])));   // Trino requires a DEFINE entry

        var someVariable = Spell(used[random.Next(used.Count)]);
        var measures = new List<(string, string)>
        {
            ("m1", "MATCH_NUMBER()"), ("m2", "CLASSIFIER()"), ("m3", "RUNNING LAST(v)"),
            ("m4", "FINAL LAST(v)"), ("m5", "COUNT(*)"), ("m6", $"SUM({someVariable}.v)"),
        };

        var rowsPerMatch = (RowsPerMatch)random.Next(4);
        var skip = (AfterMatchSkipKind)random.Next(4);
        var skipVariable = skip is AfterMatchSkipKind.ToFirst or AfterMatchSkipKind.ToLast ? someVariable : "";

        bool partitioned = random.Next(2) == 0;
        var rows = new List<IReadOnlyList<string>>();
        int id = 1;
        foreach (var part in partitioned ? new[] { "'x'", "'y'" } : ["'x'"])
            for (int i = 0, n = random.Next(3, 13); i < n; i++)
                rows.Add([part, (id++).ToString(System.Globalization.CultureInfo.InvariantCulture), random.Next(10).ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var select = new List<(string, string)>();
        if (partitioned) select.Add(("p", "p"));
        if (rowsPerMatch != RowsPerMatch.OneRow) { select.Add(("id", "id")); select.Add(("v", "v")); }
        select.AddRange(measures.Select(m => (m.Item1, m.Item1)));

        return new OracleCase($"generated[{seed}]", ["p", "id", "v"], rows,
            partitioned ? ["p"] : [], [("id", false)], measures, rowsPerMatch, skip, skipVariable,
            pattern, [], define, select);
    }
}
```
If a generated case disagrees, the failure message carries the seed and the SQL; reproduce it with `Generate(seed)` in a unit test, find which side deviates from spec §1–§2 and Trino 483, fix the engine (or, if the construct's semantics legitimately differ between the engines, narrow the generator and record why in a comment) — never loosen the comparison.

- [ ] **Step 7: Run the oracle**

Run: `dotnet test Iverson.Server/Iverson.Patterns.Tests --filter "Category=Integration"`
Expected: PASS — 1 count test, 130 ported cases, 200 generated cases. First run pulls `trinodb/trino:483` (≈1 GB).

Run: `dotnet test Iverson.slnx --filter "Category!=Integration"`
Expected: PASS, and no Trino container is started (CI's filter).

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Patterns.Tests/Iverson.Patterns.Tests.csproj Iverson.Server/Iverson.Patterns.Tests/Oracle
git commit -m "add the Trino 483 differential oracle for the pattern engine"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's `Out of scope` section:

- A Postgres row source; SQL pushdown of simple patterns; the window-clause form of row pattern
  recognition (`SEEK`/`INITIAL`).
- `SIMILARITY` on a related type's vector (through an FK), or on anything other than the row's own
  type (or, for `CHUNKS`, the chunk's own vector).
- Using a pattern result as a ranking signal inside `SearchSimilar`/`SearchChunks`.
- Changing `SearchChunks`' existing silent AND of an OR filter.

Deferred to the later plans of this spec (not out of scope, just not Plan 1):

- **Plan 2 — proto, stores and Api:** the `MatchPattern` RPC and messages (§1); the `Iverson.Patterns.csproj` `COPY` line in `Iverson.Server/Iverson.Api/Dockerfile` and the `Iverson.Vector` → `Iverson.Patterns` reference (§3.1–§3.2); `MatchRowsAsync` and the streaming tenant-scoped wrapper (§3.2), including the §9.3 StarRocks-pause test that decides the open Known issue below; `IChunkRowSource` and `ScrollAsync` (§3.2); `SimilarityResolver` (§3.3); `ObjectSearchGrpcService.MatchPattern` (§3.4); `PatternQueryLimitOptions` and every §5 limit other than `MaxProgramInstructions`'s enforcement; the §6 mapping; §9.3–§9.4.
- **Plan 3 — clients:** the five SDK builders and coordinators (§3.5) and the §9.5 conformance cases.

## Known issues inherited from spec

- Ingest lag between StarRocks and Qdrant produces `NULL` similarity (§8).
- History-dependent `define` predicates can make matching exponential in the worst case. It is
  bounded by `MaxActiveThreads`, `MaxSteps` and the timeout, not prevented.
- Compilation is bounded separately. `PERMUTE` expands to every ordering and bounded quantifiers
  unroll, so `MaxProgramInstructions` rejects with `InvalidArgument` some patterns well inside
  `MaxPatternLength`: at the default, `PERMUTE(A, B, C, D, E, F)` (5,759 instructions), `A{10000}`
  and `(A{0,100}){0,100}`.
- Open, decided by the §9.3 pause test: if StarRocks stops responding after a request has written
  `limit` rows, disposing the reader drains until StarRocks answers again, and no MySqlConnector
  call bounds it, so the request does not complete until then.
- The `CHUNKS` source scans the filtered chunk set twice (the phase-1 parent list, then the batched
  phase-2 reads) to keep memory bounded.
- `NaN` from a zero-magnitude vector is kept (§2): `NOT`/`<>` predicates over it with a non-`NULL`
  operand qualify the row, and how each SDK decodes a `NaN` `number_value` is unverified.
- The unconfigured-vector case (§3.3) is detected by Qdrant message text, which may change across
  Qdrant versions, and it would also mask a wrong-vector-name bug.

## Appendix A — Trino 483 program transliteration (provenance of Task 3's golden programs)

Run as `python3 trino_program.py "<pattern>" …`; it prints each program one instruction per line (`save` is the port's `exclusion-start`/`exclusion-end` pair). Not part of the build.

```python
# Transliteration of Trino 483's pattern compile pipeline, used only to derive golden programs:
#   RowPatternToIrRewriter (quantifier mapping) -> IrRowPatternFlattener.optimize
#   -> IrPatternAlternationOptimizer.optimize -> IrRowPatternToProgramRewriter.rewrite (+ Done).
# Output format (one instruction per line): "label A", "split 1 3", "jump 5", "save", "start", "end", "done".
import re, sys, itertools

# ---------- IR ----------
class Label:
    def __init__(s, n): s.n = n
class Empty: pass
class Anchor:
    def __init__(s, start): s.start = start
class Exclusion:
    def __init__(s, p): s.p = p
class Alt:
    def __init__(s, ps): s.ps = ps
class Concat:
    def __init__(s, ps): s.ps = ps
class Perm:
    def __init__(s, ps): s.ps = ps
class Quant:
    def __init__(s, p, lo, hi, greedy): s.p, s.lo, s.hi, s.greedy = p, lo, hi, greedy

# ---------- parser (§1 grammar) ----------
TOK = re.compile(r'\{-|-\}|\{\s*\d*\s*(?:,\s*\d*\s*)?\}|[A-Za-z_][A-Za-z0-9_]*|[()|^$,*+?]')
def tokenize(s):
    out, i = [], 0
    while i < len(s):
        if s[i].isspace(): i += 1; continue
        m = TOK.match(s, i)
        assert m, ("bad token at", s[i:])
        out.append(m.group(0)); i = m.end()
    return out

class Parser:
    def __init__(s, t): s.t, s.i = t, 0
    def peek(s): return s.t[s.i] if s.i < len(s.t) else None
    def eat(s, x=None):
        v = s.peek(); assert x is None or v == x, (x, v); s.i += 1; return v
    def top_level_comma(s, open_index):
        # Trino 483: PERMUTE( opens a permutation only when its parentheses hold a top-level comma.
        depth = 0
        for tok in s.t[open_index:]:
            if tok in ('(', '{-'): depth += 1
            elif tok in (')', '-}'): depth -= 1
            if depth == 0: return False
            if tok == ',' and depth == 1: return True
        return False
    def alt(s):
        ps = [s.concat()]
        while s.peek() == '|': s.eat(); ps.append(s.concat())
        return ps[0] if len(ps) == 1 else Alt(ps)
    def concat(s):
        ps = []
        while s.peek() not in (None, '|', ')', '-}', ','): ps.append(s.quant())
        if not ps: return Empty()
        return ps[0] if len(ps) == 1 else Concat(ps)
    def quant(s):
        p = s.prim()
        while True:
            q = s.peek()
            if q in ('*', '+', '?'):
                s.eat(); greedy = True
                if s.peek() == '?': s.eat(); greedy = False
                p = {'*': Quant(p, 0, None, greedy), '+': Quant(p, 1, None, greedy), '?': Quant(p, 0, 1, greedy)}[q]
            elif q and q.startswith('{') and not q.startswith('{-'):
                s.eat(); body = q[1:-1].replace(' ', '')
                greedy = True
                if s.peek() == '?': s.eat(); greedy = False
                if ',' in body:
                    lo, hi = body.split(',')
                    lo = int(lo) if lo else 0
                    hi = int(hi) if hi else None
                else:
                    lo = hi = int(body)
                p = Quant(p, lo, hi, greedy)
            else:
                return p
    def prim(s):
        v = s.peek()
        if v == '(':
            s.eat()
            if s.peek() == ')': s.eat(); return Empty()
            p = s.alt(); s.eat(')'); return p
        if v == '{-':
            s.eat(); p = s.alt(); s.eat('-}'); return Exclusion(p)
        if v == '^': s.eat(); return Anchor(True)
        if v == '$': s.eat(); return Anchor(False)
        if v.upper() == 'PERMUTE' and s.t[s.i + 1:s.i + 2] == ['('] and s.top_level_comma(s.i + 1):
            s.eat(); s.eat('('); ps = [s.alt()]
            while s.peek() == ',': s.eat(); ps.append(s.alt())
            s.eat(')'); return Perm(ps)
        s.eat(); return Label(v.upper())

# ---------- IrRowPatternFlattener ----------
def flatten(n, inex=False):
    if isinstance(n, (Label, Anchor, Empty)): return n
    if isinstance(n, Exclusion):
        c = flatten(n.p, True)
        return c if inex else Exclusion(c)
    if isinstance(n, Alt):
        cs = [flatten(p, inex) for p in n.ps]
        fl = []
        for c in cs: fl.extend(c.ps if isinstance(c, Alt) else [c])
        first = next((c for c in fl if isinstance(c, Empty)), None)
        if first is None: return Alt(fl)
        fl = [c for c in fl if not isinstance(c, Empty) or c is first]
        return Empty() if len(fl) == 1 else Alt(fl)
    if isinstance(n, Concat):
        cs = [flatten(p, inex) for p in n.ps]
        fl = []
        for c in cs: fl.extend(c.ps if isinstance(c, Concat) else [c])
        fl = [c for c in fl if not isinstance(c, Empty)]
        if not fl: return Empty()
        return fl[0] if len(fl) == 1 else Concat(fl)
    if isinstance(n, Perm):
        cs = [c for c in (flatten(p, inex) for p in n.ps) if not isinstance(c, Empty)]
        if not cs: return Empty()
        return cs[0] if len(cs) == 1 else Perm(cs)
    if isinstance(n, Quant):
        c = flatten(n.p, inex)
        return c if isinstance(c, Empty) else Quant(c, n.lo, n.hi, n.greedy)
    raise TypeError(n)

# ---------- IrPatternAlternationOptimizer ----------
def altopt(n):
    if isinstance(n, (Label, Anchor, Empty)): return n
    if isinstance(n, Exclusion): return Exclusion(altopt(n.p))
    if isinstance(n, Alt):
        cs = [altopt(p) for p in n.ps]
        idx = [i for i, c in enumerate(cs) if isinstance(c, Empty)]
        assert len(idx) <= 1
        if not idx: return Alt(cs)
        e = idx[0]
        if e == 0:
            child = Quant(cs[1], 0, 1, False)
            return child if len(cs) == 2 else Alt([child] + cs[2:])
        cs = cs[:e - 1] + [Quant(cs[e - 1], 0, 1, True)] + cs[e + 1:]
        return cs[0] if len(cs) == 1 else Alt(cs)
    if isinstance(n, Concat): return Concat([altopt(p) for p in n.ps])
    if isinstance(n, Perm): return Perm([altopt(p) for p in n.ps])
    if isinstance(n, Quant): return Quant(altopt(n.p), n.lo, n.hi, n.greedy)
    raise TypeError(n)

# ---------- IrRowPatternToProgramRewriter ----------
def rewrite(n):
    ins = []
    def process(n):
        if isinstance(n, Label): ins.append(('label', n.n))
        elif isinstance(n, Empty): pass
        elif isinstance(n, Anchor): ins.append(('start',) if n.start else ('end',))
        elif isinstance(n, Exclusion): ins.append(('save',)); process(n.p); ins.append(('save',))
        elif isinstance(n, Alt): alternation([[p] for p in n.ps])
        elif isinstance(n, Concat):
            for p in n.ps: process(p)
        elif isinstance(n, Perm):
            alternation([[n.ps[i] for i in perm] for perm in itertools.permutations(range(len(n.ps)))])
        elif isinstance(n, Quant):
            if n.hi is not None: range_q(n.p, n.greedy, n.lo, n.hi)
            else: looping(n.p, n.greedy, n.lo)
        else: raise TypeError(n)
    def alternation(parts):
        jumps = []
        for part in parts[:-1]:
            sp = len(ins); ins.append(None); tgt = len(ins)
            for p in part: process(p)
            jumps.append(len(ins)); ins.append(None)
            ins[sp] = ('split', tgt, len(ins))
        for p in parts[-1]: process(p)
        for j in jumps: ins[j] = ('jump', len(ins))
    def loop(lp, greedy):
        ins.append(('split', lp, len(ins) + 1) if greedy else ('split', len(ins) + 1, lp))
    def looping(p, greedy, lo):
        if lo == 0:
            sp = len(ins); ins.append(None); tgt = len(ins); lp = len(ins)
            process(p); loop(lp, greedy)
            ins[sp] = ('split', tgt, len(ins)) if greedy else ('split', len(ins), tgt)
            return
        lp = len(ins)
        for _ in range(lo):
            lp = len(ins); process(p)
        loop(lp, greedy)
    def range_q(p, greedy, lo, hi):
        for _ in range(lo): process(p)
        if lo == hi: return
        sps, tgts = [], []
        for _ in range(lo, hi):
            sps.append(len(ins)); ins.append(None); tgts.append(len(ins)); process(p)
        for sp, t in zip(sps, tgts):
            ins[sp] = ('split', t, len(ins)) if greedy else ('split', len(ins), t)
    process(n)
    ins.append(('done',))
    return ins

def compile_pattern(text):
    p = Parser(tokenize(text)); n = p.alt(); assert p.peek() is None, p.t[p.i:]
    return rewrite(altopt(flatten(n)))

def fmt(ins):
    return [' '.join(str(x) for x in i) for i in ins]

if __name__ == '__main__':
    for text in sys.argv[1:]:
        print('### ' + text)
        for line in fmt(compile_pattern(text)): print(line)
```

## Appendix B — Ported-case extraction scripts (Task 8, Step 1)

`extract_sites.py`:
```python
# Evaluate every assertions.query(...) argument in TestRowPatternMatching.java@483 to its final SQL text.
# Java string literals, `+` concatenation, `format(template, args...)` and `String x = ...;` locals are supported;
# text blocks (""") are evaluated verbatim. Output: JSON list of {method, index, sql}.
import re, json, sys

src = open(sys.argv[1] if len(sys.argv) > 1 else "TestRowPatternMatching.java").read()

def java_expr_to_py(expr):
    # text blocks
    expr = re.sub(r'"""\n(.*?)"""', lambda m: repr(m.group(1)), expr, flags=re.S)
    expr = re.sub(r'(?m)(\+|,|\()\s*//[^\n]*$', r'\1', expr)
    expr = re.sub(r'(?m)^\s*//[^\n]*$', '', expr)
    expr = expr.replace("format(", "jformat(")
    return expr

def jformat(template, *args):
    return template.replace("%%", "\0") % tuple(args) if "%s" in template else template

methods = re.split(r'\n    @Test\n    public void ', src)[1:]
out = []
for body in methods:
    name = body[:body.index("(")]
    env = {"jformat": jformat}
    # locals: String name = <expr>;
    for m in re.finditer(r'String (\w+) = (.*?);\n', body, flags=re.S):
        env[m.group(1)] = eval("(" + java_expr_to_py(m.group(2)) + ")", env)
    # query sites: assertions.query(<expr>) — find balanced parens
    i = 0; k = 0
    while True:
        i = body.find("assertions.query(", i)
        if i < 0: break
        j = i + len("assertions.query("); depth = 1; s = j; inq = False; tb = False
        while depth:
            if body.startswith('"""', j): tb = not tb; j += 3; continue
            c = body[j]
            if not tb and c == '"' and body[j-1] != '\\': inq = not inq
            elif not inq and not tb:
                if c == '(': depth += 1
                elif c == ')': depth -= 1
            j += 1
        arg = body[s:j-1]
        sql = eval("(" + java_expr_to_py(arg) + ")", env)
        out.append({"method": name, "index": k, "sql": re.sub(r'\s+', ' ', sql).strip()})
        k += 1; i = j
json.dump(out, open("sites.json", "w"), indent=1)
print(len(out))
```

`structure_sites.py`:
```python
# Split each extracted site into the structured case the oracle harness takes, or record why it is excluded.
import json, re

UNSUPPORTED = [r'\bCAST\s*\(', r'\|\|', r'\blower\s*\(', r'\bconcat\s*\(', r'\bROW\s*\(', r'\bEXISTS\b', r'\(\s*SELECT\b',
               r'\bJOIN\b', r'\bWINDOW\b', r'\bINITIAL\b', r'\bSEEK\b', r'\d+\.\d+', r'\bWITH\s+\w+\s*\(', r'\bOVER\b',
               r'\barray\b', r'\bDATE\b', r'\bTIMESTAMP\b', r'\bINTERVAL\b']

def split_top(s, sep=','):
    out, depth, cur, q = [], 0, '', False
    for c in s:
        if c == "'": q = not q
        if not q:
            if c == '(': depth += 1
            elif c == ')': depth -= 1
            elif c == sep and depth == 0: out.append(cur.strip()); cur = ''; continue
        cur += c
    if cur.strip(): out.append(cur.strip())
    return out

def matching_paren(s, i):
    depth, q = 0, False
    for j in range(i, len(s)):
        c = s[j]
        if c == "'": q = not q
        if q: continue
        if c == '(': depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0: return j
    raise ValueError("unbalanced")

KW = ['PARTITION BY', 'ORDER BY', 'MEASURES', 'ONE ROW PER MATCH', 'ALL ROWS PER MATCH', 'AFTER MATCH SKIP', 'PATTERN', 'SUBSET', 'DEFINE']

def parse(sql):
    sql = re.sub(r"/\*.*?\*/", " ", sql)
    if '"' in sql: return None, "delimited (quoted) identifier"
    for u in UNSUPPORTED:
        if re.search(u, sql, flags=re.I): return None, "uses " + u
    if sql.upper().count('MATCH_RECOGNIZE') != 1: return None, "not exactly one MATCH_RECOGNIZE"
    m = re.match(r'SELECT (.*?) FROM \(\s*VALUES (.*?)\)\s*(?:(?:AS\s+)?t\s*\(([^)]*)\))?\s*MATCH_RECOGNIZE\s*\(', sql, flags=re.I | re.S)
    if not m: return None, "outer shape not SELECT ... FROM (VALUES ...) [t(...)] MATCH_RECOGNIZE"
    select, values = m.group(1), m.group(2)
    cols = [c.strip() for c in m.group(3).split(',')] if m.group(3) else None
    body_start = m.end() - 1
    body_end = matching_paren(sql, body_start)
    body = sql[body_start + 1:body_end].strip()
    tail = sql[body_end + 1:].strip()
    if not re.fullmatch(r'(AS\s+)?\w*', tail, flags=re.I): return None, "trailing clause after MATCH_RECOGNIZE: " + tail
    rows = []
    for r in split_top(values):
        r = r.strip()
        if r.startswith('('): r = r[1:matching_paren(r, 0)]
        rows.append([v.strip() for v in split_top(r)])
    if cols is None: cols = ["_col%d" % i for i in range(len(rows[0]))]
    # clause split
    upper = body.upper(); pos = []
    for kw in KW:
        for mm in re.finditer(r'\b' + kw.replace(' ', r'\s+') + r'\b', upper):
            # only top-level (depth 0) keywords
            depth = body[:mm.start()].count('(') - body[:mm.start()].count(')')
            if depth == 0: pos.append((mm.start(), kw, mm.end()))
    pos.sort()
    clauses = {}
    for i, (st, kw, en) in enumerate(pos):
        nxt = pos[i + 1][0] if i + 1 < len(pos) else len(body)
        clauses[kw] = body[en:nxt].strip()
    rows_per_match = 'OneRow'
    if 'ALL ROWS PER MATCH' in clauses:
        mod = clauses['ALL ROWS PER MATCH'].upper()
        rows_per_match = {'': 'AllRowsShowEmpty', 'SHOW EMPTY MATCHES': 'AllRowsShowEmpty', 'OMIT EMPTY MATCHES': 'AllRowsOmitEmpty',
                          'WITH UNMATCHED ROWS': 'AllRowsWithUnmatched'}[re.sub(r'\s+', ' ', mod).strip()]
    skip, skipvar = 'PastLastRow', ''
    if 'AFTER MATCH SKIP' in clauses:
        sk = re.sub(r'\s+', ' ', clauses['AFTER MATCH SKIP']).strip()
        mm = re.fullmatch(r'(PAST LAST ROW|TO NEXT ROW|TO FIRST (\w+)|TO LAST (\w+)|TO (\w+))', sk, flags=re.I)
        if not mm: return None, "skip " + sk
        t = mm.group(1).upper()
        if t == 'PAST LAST ROW': skip = 'PastLastRow'
        elif t == 'TO NEXT ROW': skip = 'ToNextRow'
        elif t.startswith('TO FIRST'): skip, skipvar = 'ToFirst', mm.group(2)
        elif t.startswith('TO LAST'): skip, skipvar = 'ToLast', mm.group(3)
        else: skip, skipvar = 'ToLast', mm.group(4)   # SQL:2016: SKIP TO v means SKIP TO LAST v
    pat = clauses['PATTERN'].strip()
    assert pat.startswith('(') and pat.endswith(')'), pat
    pattern = pat[1:-1].strip()
    measures = []
    for mdef in split_top(clauses.get('MEASURES', '')):
        mm = re.fullmatch(r'(.*)\s+AS\s+(\w+)', mdef, flags=re.I | re.S)
        measures.append([mm.group(2), mm.group(1).strip()])
    defines = []
    for d in split_top(clauses.get('DEFINE', '')):
        mm = re.fullmatch(r'(\w+)\s+AS\s+(.*)', d, flags=re.I | re.S)
        defines.append([mm.group(1), mm.group(2).strip()])
    subsets = []
    for sdef in split_top(clauses.get('SUBSET', '')):
        mm = re.fullmatch(r'(\w+)\s*=\s*\((.*)\)', sdef, flags=re.S)
        subsets.append([mm.group(1), [v.strip() for v in mm.group(2).split(',')]])
    partition = [c.strip() for c in split_top(clauses.get('PARTITION BY', ''))]
    order = []
    for o in split_top(clauses.get('ORDER BY', '')):
        parts = o.split()
        order.append([parts[0], len(parts) > 1 and parts[1].upper() == 'DESC'])
    select_cols = []
    for c in split_top(select):
        c = re.sub(r'^\w+\.', '', c.strip())
        if c == '*': select_cols.append(['*', '*']); continue
        mm = re.fullmatch(r'(\w+)(?:\s+AS\s+(\w+))?', c, flags=re.I)
        if not mm: return None, "select item " + c
        select_cols.append([mm.group(1), mm.group(2) or mm.group(1)])
    return {"columns": cols, "rows": rows, "partitionBy": partition, "orderBy": order, "measures": measures,
            "rowsPerMatch": rows_per_match, "skip": skip, "skipVariable": skipvar, "pattern": pattern,
            "subsets": subsets, "define": defines, "select": select_cols}, None

sites = json.load(open("sites.json"))
cases, excluded = [], []
for s in sites:
    try:
        c, why = parse(s["sql"])
    except Exception as e:
        c, why = None, "parse error: %r" % e
    if c is None:
        excluded.append({"method": s["method"], "index": s["index"], "why": why})
    else:
        c.update({"method": s["method"], "index": s["index"], "sql": s["sql"]}); cases.append(c)
json.dump(cases, open("cases.json", "w"), indent=1)
json.dump(excluded, open("excluded.json", "w"), indent=1)
print("cases", len(cases), "excluded", len(excluded))
from collections import Counter
print(Counter(e["why"] for e in excluded).most_common())
print(Counter(c["method"] for c in cases))
```
