# Vector Ranking Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-31-vector-ranking-configuration-design.md` (commit SHA: `0c14f49`)

**Goal:** Bind the five compile-time constants that decide retrieval ordering to configuration, with defaults equal to the constants currently shipped, so a deployment can tune them without a rebuild.

**Architecture:** Two options classes, one per assembly. `VectorRankingOptions` in `Iverson.Vector` carries the three fusion weights and MMR's `Lambda`, injected as `IOptions<T>` into `ResultReranker` and `ResultDiversifier`, and registered by a new `AddVectorRanking(cfg)` that also takes over the two ranking singletons currently registered inside `AddQdrant`. `DecayOptions` in `Iverson.Api` carries `HalfLifeDays`, bound inline in `Program.cs` and threaded through `ComputeDecay` as a parameter so `DecayFieldResolver` stays `internal static` and pure. Both bind eagerly, validate, and throw at startup.

**Tech stack:** .NET 10, `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9 (already used by `Iverson.Embeddings` at the same version), xunit + FluentAssertions + NSubstitute.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **Defaults must equal the shipped constants at implementation time, whatever they are.** That equality is the entire basis of the "no behavioural change" claim. `main` carries `0.60 / 0.30 / 0.10`; branch `decay-share-triple-b` carries `0.45 / 0.45 / 0.10`. `Lambda` is `0.70` and `HalfLifeDays` is `180.0` on both. **Read the constants off the branch you are building on — do not copy the literals from this plan or the spec.**
- **This change enables tuning. It retunes nothing.** No default may be changed.
- **There is deliberately no sum-to-1 rule on the weights.** The fusion divides by the weights actually present, so `(0.60, 0.30)` and `(0.20, 0.10)` are the same configuration; constraining the sum would reject valid settings.
- **Validation is fail-fast and finiteness comes first.** Every validation check is a comparison, and every comparison against `NaN` is false, so a non-finite value passes every range check and produces silent `NaN` ranking. The finiteness guard must precede the range checks.

## File Structure

**Create**
- `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` — the four fusion/diversification values, `public`.
- `Iverson.Server/Iverson.Api/Grpc/DecayOptions.cs` — `HalfLifeDays`, `public` (CS0051; see Task 2 Step 1).
- `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs` — non-default-weight behaviour and `AddVectorRanking` validation.

**Modify**
- `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj` — one new package reference.
- `Iverson.Server/Iverson.Vector/ResultReranker.cs` — constants become injected options.
- `Iverson.Server/Iverson.Vector/ResultDiversifier.cs` — same, for `Lambda`.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — new `AddVectorRanking`; the two ranking singletons move out of `AddQdrant`.
- `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs` — `ComputeDecay` gains `double halfLifeDays`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — new constructor parameter; `DecayFor` gains the half-life.
- `Iverson.Server/Iverson.Api/Program.cs` — `AddVectorRanking(cfg)` call; inline `DecayOptions` bind + validate.

**Test**
- `Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs`, `ResultDiversifierTests.cs` — construction sites.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `Grpc/ObjectSearchVectorIntegrationTests.cs`, `Schema/DocumentTemplateValidationTests.cs` — construction sites.
- `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs` — `ComputeDecay` call sites, plus the non-default half-life test.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and reconfirmed by two CDR rounds. **Not re-verified here.** Full evidence for each is in the spec's `## Verified assumptions` table.

- **A1-A3** — `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9 supplies `IOptions<T>`, `Options.Create`, `Bind`, with no version conflict; `Iverson.Vector` does not already reference it; both test projects can reach `Options.Create`.
- **A4/A5/A17** — exactly six `ResultReranker`/`ResultDiversifier` construction sites; no non-test, non-DI consumer.
- **A6** — `ObjectSearchGrpcService` takes both as interfaces and calls only `Rerank`/`Diversify`.
- **A7** — no test resolves either interface from an `AddQdrant` container.
- **A8** — `cfg` is in scope at `Program.cs:24`.
- **A9** — `AddQdrant` has one production call site.
- **A10/A11** — the weights are used only inside `Rerank`; `Lambda` only inside `Diversify`.
- **A12** — `Bind` against an absent section leaves defaults intact; a partial section leaves unset values at defaults.
- **A13** — options-injected primary constructor is the established style (`EmbeddingService.cs:9-13`).
- **A14** — configuration binds `double` invariantly, not by host locale.
- **A15** — defaults equal the shipped constants (branch-dependent; see Global Constraints).
- **A16** — `VectorRankingOptions` is reachable from both test projects.
- **A18** — the sibling sweep found `HalfLifeDays` as the fifth ranking constant; it is in scope as `DecayOptions`.
- **A19** — `InternalsVisibleTo` → `Iverson.Api.Tests` (this covers `DecayFieldResolver` staying `internal`, not the options class).
- **A20** — `ComputeDecay`'s blast radius is bounded.
- **A21** — the production decay path runs through a static helper reachable from instance methods.
- **A22** — an options type used as a parameter of a `public` primary constructor must itself be `public` (CS0051).
- **A23** — `ObjectSearchGrpcService` has exactly three construction sites, one target-typed.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` does not exist yet | `ls` → `No such file or directory` |
| P2 | File path | `Iverson.Server/Iverson.Api/Grpc/DecayOptions.cs` does not exist yet | `ls` → `No such file or directory` |
| P3 | File path | `ResultDiversifierTests.cs` is at `Iverson.Server/Iverson.Vector.Tests/` | directory listing shows it alongside `ResultRerankerTests.cs` |
| P4 | File path | Vector tests live flat in `Iverson.Vector.Tests/`, named `<Type>Tests.cs` — no subfolder convention | listing: 8 test files, all flat, all `<Type>Tests.cs` |
| P5 | Signature | `Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK)`; `RerankedResult(ulong Id, double FusedScore)`; `DiversifyCandidate(ulong Id, double Score, float[]? DiversityVector)` — unchanged by this plan | `ResultDiversifier.cs:14`; `IResultReranker.cs:9`; `IResultDiversifier.cs:8` |
| P6 | Code validity | `Options.Create<T>(T)` returns `IOptions<T>` and `AddSingleton(Options.Create(..))` registers the interface | probe: `GetService<IOptions<VectorRankingOptions>>()` resolved non-null |
| P7 | Signature | `ObjectSearchGrpcService`'s parameter list ends with `IResultDiversifier diversifier)`, so a tenth parameter is a pure append | `ObjectSearchGrpcService.cs:39-40` |
| P8 | Command | `dotnet test <csproj>` is the invocation; no wrapper script | run this session: Vector.Tests 123 passed, Api.Tests 830 passed |
| P9 | Command | Both test projects are in `Iverson.slnx` | grep count 2 |
| P10 | Command | Commit style is lowercase imperative, no Conventional-Commits prefix, `Co-Authored-By` trailer | `git log --format=%s -6` |
| P11 | Ordering | Task 1 references nothing Task 2 introduces | Task 1 touches only `Iverson.Vector` + `Program.cs` + construction sites; `DecayOptions` appears in no Task 1 edit |
| P12 | Ordering | Task 2 edits the same call expression Task 1 rewrites, so Task 1 must run first | `ObjectSearchGrpcServiceTests.cs:65-69` is one call containing both `new ObjectSearchGrpcService(` and `new ResultReranker(), new ResultDiversifier())` |
| P13 | Ordering | The solution compiles at Task 1's commit | Task 1's step list includes all six construction sites, four of which are in `Iverson.Api.Tests` |
| P14 | Code validity | `double.IsFinite` exists in net10.0 | probe compiled clean using `double.IsFinite(o.WBase)` |
| P15 | Code validity | Adding the package to `Iverson.Vector` introduces no version conflict | `dotnet list package --include-transitive` shows **no** existing `Microsoft.Extensions.Options`/`Configuration` at any version |
| P16 | Code validity | Capturing a primary-constructor parameter into a `private readonly` field is valid here | probe compiled clean: `private readonly VectorRankingOptions _o = options.Value;` |
| P17 | Code validity | `Microsoft.Extensions.Options` is not an implicit using in `Iverson.Vector`; new files need it explicitly | `ImplicitUsings` enabled at `Iverson.Vector.csproj:5`, but zero `using Microsoft.Extensions.Options` in the assembly today |
| P18 | Consumer impact | All `ResultReranker`/`ResultDiversifier` constructor consumers are the six sites | whole-repo grep on the type names (not `new X`, which misses target-typed `= new()`) |
| P19 | Consumer impact | `ComputeDecay` has exactly 8 call sites: 7 in `DecayFieldResolverTests.cs`, 1 in `ObjectSearchGrpcService.cs`, plus its declaration | `grep -rn "ComputeDecay("` grouped by file: 7 / 1 / 1 |
| P20 | Consumer impact | `ObjectSearchGrpcService` has exactly three construction sites | `ObjectSearchGrpcServiceTests.cs:65`, `DocumentTemplateValidationTests.cs:327`, `ObjectSearchVectorIntegrationTests.cs:80` (target-typed) |
| P21 | Consumer impact | Moving the two `AddSingleton` lines out of `AddQdrant` breaks no caller | `ServiceCollectionExtensionsTests` asserts only `GetRequiredService<QdrantClient>()`; `AuthTestWebApplicationFactory` does not call `AddQdrant` (it boots `Program`); no hosted service or `Program.cs` factory lambda resolves either interface |
| P23 | File path | `Program.cs` already carries both usings the new code needs, so neither task adds one there | `Program.cs:7` `using Iverson.Api.Grpc;`, `Program.cs:13` `using Iverson.Vector;` |
| P24 | Signature | The half-life is applied at exactly one expression, `DecayFieldResolver.cs:85` | `return Math.Min(1.0, Math.Pow(0.5, ageDays / HalfLifeDays));` |
| P22 | Sibling sweep | Every identifier the plan's code blocks name resolves at its point of use — meta-class: *a referenced name has a definition reachable from the file using it* | Framework: `IOptions`, `Options.Create`, `IConfiguration`, `IServiceCollection`, `Bind`, `GetSection`, `double.IsFinite`, `InvalidOperationException` — all compiled in probes. Repo: `IResultReranker`/`IResultDiversifier`/`ResultReranker`/`ResultDiversifier` (`Iverson.Vector`), `DecayFieldResolver.ComputeDecay` (`DecayFieldResolver.cs:76`), `DecayFor` (`ObjectSearchGrpcService.cs:764`), `MapGrpcService` (`Program.cs:443`). `VectorRankingOptions`/`DecayOptions` are created by this plan |

## Tasks

### Task 1: Fusion weights and MMR Lambda become configuration

**Files:**
- Create: `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`
- Create: `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`
- Modify: `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`
- Modify: `Iverson.Server/Iverson.Vector/ResultReranker.cs`
- Modify: `Iverson.Server/Iverson.Vector/ResultDiversifier.cs`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:50-51`
- Modify: `Iverson.Server/Iverson.Api/Program.cs:182-186`
- Test: `Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs:16`
- Test: `Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs:8`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:69`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:89-90`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:332`

**Interfaces:**
- Produces: `VectorRankingOptions` (public, namespace `Iverson.Vector`); `AddVectorRanking(IServiceCollection, IConfiguration)`; rewritten construction expressions at the two shared `Iverson.Api.Tests` call sites that Task 2 also edits.

- [ ] **Step 1: Read the shipped constants — this is what the defaults must equal**

```bash
grep -n "private const double WBase" Iverson.Server/Iverson.Vector/ResultReranker.cs
grep -n "private const double Lambda" Iverson.Server/Iverson.Vector/ResultDiversifier.cs
```

Use exactly these values as the option defaults in Step 2. Do **not** copy the literals from this plan — see Global Constraints.

- [ ] **Step 2: Create `VectorRankingOptions.cs`**

Substitute the Step 1 values for the four defaults below.

```csharp
namespace Iverson.Vector;

public sealed class VectorRankingOptions
{
    public const string Section = "VectorRanking";

    public double WBase     { get; set; } = <WBase from Step 1>;
    public double WCentroid { get; set; } = <WCentroid from Step 1>;
    public double WDecay    { get; set; } = <WDecay from Step 1>;
    public double Lambda    { get; set; } = <Lambda from Step 1>;
}
```

Move the comment block that currently sits above `ResultReranker`'s constant onto these defaults — this is now where the shipped value is decided.

- [ ] **Step 3: Add the package reference**

In `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`, into the existing `PackageReference` `ItemGroup`:

```xml
<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.9" />
```

- [ ] **Step 4: Convert `ResultReranker` to injected options**

Add `using Microsoft.Extensions.Options;` (not an implicit using here — P17). Replace the `private const double WBase = ..., WCentroid = ..., WDecay = ...;` line and make the class take the options:

```csharp
public sealed class ResultReranker(IOptions<VectorRankingOptions> options) : IResultReranker
{
    private readonly VectorRankingOptions _o = options.Value;
```

Then replace `WBase` → `_o.WBase`, `WCentroid` → `_o.WCentroid`, `WDecay` → `_o.WDecay` in the `Rerank` body. The algorithm does not change, including the short-circuit that returns `BaseScore` when neither centroid nor decay is present — a weighted mean over one present signal is still that signal, for any weight.

- [ ] **Step 5: Convert `ResultDiversifier` the same way**

```csharp
public sealed class ResultDiversifier(IOptions<VectorRankingOptions> options) : IResultDiversifier
{
    private readonly VectorRankingOptions _o = options.Value;
```

Replace both `Lambda` references in `Diversify` (`ResultDiversifier.cs:76-77`) with `_o.Lambda`.

- [ ] **Step 6: Add `AddVectorRanking` and move the two registrations into it**

Delete `services.AddSingleton<IResultReranker, ResultReranker>();` and `services.AddSingleton<IResultDiversifier, ResultDiversifier>();` from `AddQdrant` (`ServiceCollectionExtensions.cs:50-51`) and add a new method to the same class. `AddQdrant`'s signature is untouched.

```csharp
public static IServiceCollection AddVectorRanking(this IServiceCollection services, IConfiguration config)
{
    var opts = new VectorRankingOptions();
    config.GetSection(VectorRankingOptions.Section).Bind(opts);

    if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
        !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.Lambda))
        throw new InvalidOperationException(
            $"{VectorRankingOptions.Section}: every value must be finite " +
            $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, Lambda={opts.Lambda}).");

    if (opts.WBase < 0 || opts.WCentroid < 0 || opts.WDecay < 0)
        throw new InvalidOperationException(
            $"{VectorRankingOptions.Section}: weights must be non-negative " +
            $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}).");

    if (opts.WBase + opts.WCentroid + opts.WDecay <= 0)
        throw new InvalidOperationException(
            $"{VectorRankingOptions.Section}: at least one weight must be greater than zero; " +
            "all-zero weights make every fused score NaN.");

    if (opts.Lambda is < 0 or > 1)
        throw new InvalidOperationException(
            $"{VectorRankingOptions.Section}:Lambda must be in [0,1] (was {opts.Lambda}).");

    services.AddSingleton(Options.Create(opts));
    services.AddSingleton<IResultReranker, ResultReranker>();
    services.AddSingleton<IResultDiversifier, ResultDiversifier>();
    return services;
}
```

Add `using Microsoft.Extensions.Configuration;` and `using Microsoft.Extensions.Options;` to the file.

- [ ] **Step 7: Call it from `Program.cs`**

Immediately after the existing `builder.Services.AddQdrant(...)` block (`Program.cs:182-186`):

```csharp
builder.Services.AddVectorRanking(cfg);
```

`using Iverson.Vector;` is already present at `Program.cs:13`.

- [ ] **Step 8: Update all six construction sites**

Each becomes `new ResultReranker(Options.Create(new VectorRankingOptions()))` / `new ResultDiversifier(Options.Create(new VectorRankingOptions()))`, adding `using Microsoft.Extensions.Options;` and `using Iverson.Vector;` to each file as needed.

Two are target-typed field initializers, invisible to a `new ResultReranker` grep:
- `Iverson.Vector.Tests/ResultRerankerTests.cs:16` — `private readonly ResultReranker _reranker = new();`
- `Iverson.Vector.Tests/ResultDiversifierTests.cs:8` — `private readonly ResultDiversifier _diversifier = new();`

Four are explicit:
- `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:69`
- `Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:89-90`
- `Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:332`

Because the defaults equal the shipped constants, **every existing hand-computed expectation stays valid unchanged.** Do not adjust any expected value.

- [ ] **Step 9: Add `VectorRankingOptionsTests.cs`**

The non-default test is the one that matters: every other test in this task would pass even if `ResultReranker` ignored the injected options entirely, because the defaults are identical.

- One test constructing `ResultReranker` with **non-default** weights (e.g. `WBase = 0.9, WCentroid = 0.1, WDecay = 0.0`) over a candidate with all three signals present, asserting the fused score the weighted mean gives for those weights — a value that differs from the default-weight result for the same candidate.
- One test constructing `ResultDiversifier` with a **non-default** `Lambda`, asserting a selection or score that differs from the default.
- Four validation tests, each asserting `AddVectorRanking` throws `InvalidOperationException` against an in-memory configuration: a negative weight; all three weights zero; `Lambda = 1.5`; and a non-finite value (`"NaN"`, and separately `"Infinity"`).

Build the configuration with `new ConfigurationBuilder().AddInMemoryCollection(...)`, matching the shape the validation reads.

- [ ] **Step 10: Run both suites**

```bash
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

Both must pass. `Iverson.Api.Tests` is the one that proves the construction-site updates are complete.

- [ ] **Step 11: Commit**

```bash
git add Iverson.Server/Iverson.Vector/VectorRankingOptions.cs \
        Iverson.Server/Iverson.Vector/Iverson.Vector.csproj \
        Iverson.Server/Iverson.Vector/ResultReranker.cs \
        Iverson.Server/Iverson.Vector/ResultDiversifier.cs \
        Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs \
        Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs \
        Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs
git commit -m "bind the fusion weights and MMR lambda to configuration

Defaults are the constants that were compiled in, so behaviour at default
configuration is unchanged and every hand-computed test expectation stands.
Validation is eager and fail-fast, with the finiteness guard first: every other
check is a comparison, and comparisons against NaN are false, so a non-finite
value would otherwise pass all of them and rank silently as NaN.

The two ranking singletons move out of AddQdrant into AddVectorRanking --
ranking was never a Qdrant concern, and AddQdrant's positional signature stays
untouched so its four call sites are undisturbed.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 2: The decay half-life becomes configuration

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/DecayOptions.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs:14,76,85`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:30-41,260,447,764-767`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs` (7 `ComputeDecay` call sites)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:65`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:327`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:80`

**Interfaces:**
- Consumes: Task 1's rewritten construction expressions at `ObjectSearchGrpcServiceTests.cs:65-69` and `DocumentTemplateValidationTests.cs:327-332` — the same calls, which this task extends with a tenth argument. Also `Program.cs`, where Task 1 has already added its registration.

- [ ] **Step 1: Create `DecayOptions.cs` — `public`, not `internal`**

```csharp
namespace Iverson.Api.Grpc;

public sealed class DecayOptions
{
    public const string Section = "Decay";
    public double HalfLifeDays { get; set; } = <HalfLifeDays from DecayFieldResolver.cs:14>;
}
```

It must be `public`: it is a parameter type of `ObjectSearchGrpcService`'s public primary constructor, and an `internal` type there is `CS0051: Inconsistent accessibility`. `InternalsVisibleTo` does not help — that governs test visibility, not the public-surface rule. Keeping it internal and adding a separate internal constructor is not an option either: `MapGrpcService<ObjectSearchGrpcService>()` (`Program.cs:443`) resolves the type from DI and needs an accessible constructor.

- [ ] **Step 2: Thread the half-life through `DecayFieldResolver`**

Delete `private const double HalfLifeDays = 180.0;` (`DecayFieldResolver.cs:14`) and add the parameter:

```csharp
internal static double? ComputeDecay(string? storedValue, DateTimeOffset now, double halfLifeDays)
```

with `Math.Pow(0.5, ageDays / halfLifeDays)` at line 85. `DecayFieldResolver` stays `internal static` and `ComputeDecay` stays pure; `ResolveDecayField` is untouched. Update the XML doc's "with a fixed 180-day half-life" wording to reflect that the half-life is now supplied by the caller.

- [ ] **Step 3: Thread it through `ObjectSearchGrpcService`**

Append the parameter to the primary constructor, after `IResultDiversifier diversifier`:

```csharp
    IResultDiversifier diversifier,
    IOptions<DecayOptions> decayOptions)
```

Add `using Microsoft.Extensions.Options;`. Give the static helper the same treatment as `now`:

```csharp
private static double? DecayFor(
    VectorSearchResult result, string? decayField, DateTimeOffset now, double halfLifeDays) =>
    decayField is not null && result.Payload.TryGetValue(decayField, out var stored)
        ? DecayFieldResolver.ComputeDecay(stored, now, halfLifeDays)
        : null;
```

Both call sites (`:260`, `:447`) are inside instance methods, so each passes `decayOptions.Value.HalfLifeDays`.

- [ ] **Step 4: Bind and validate in `Program.cs`**

Beside Task 1's `AddVectorRanking(cfg)` call. `using Iverson.Api.Grpc;` is already present at `Program.cs:7`.

```csharp
var decayOptions = new DecayOptions();
cfg.GetSection(DecayOptions.Section).Bind(decayOptions);

if (!double.IsFinite(decayOptions.HalfLifeDays) || decayOptions.HalfLifeDays <= 0)
    throw new InvalidOperationException(
        $"{DecayOptions.Section}:HalfLifeDays must be finite and greater than zero " +
        $"(was {decayOptions.HalfLifeDays}).");

builder.Services.AddSingleton(Options.Create(decayOptions));
```

Finiteness is checked first for the same reason as Task 1, and it is load-bearing here specifically: `Infinity` passes `> 0` and yields `0.5^0 = 1.0` for every document, disabling decay while appearing configured.

- [ ] **Step 5: Update the call sites**

Seven `ComputeDecay` calls in `DecayFieldResolverTests.cs` gain the half-life argument, passed as the value read in Step 1 so every existing expectation stays valid unchanged.

All three `ObjectSearchGrpcService` construction sites gain `Options.Create(new DecayOptions())` as the tenth argument:
- `ObjectSearchGrpcServiceTests.cs:65`
- `DocumentTemplateValidationTests.cs:327`
- `ObjectSearchVectorIntegrationTests.cs:80` — **target-typed `new(`**, so a `new ObjectSearchGrpcService` grep will not find it

- [ ] **Step 6: Add the tests**

In `DecayFieldResolverTests.cs`:
- One test calling `ComputeDecay` at a **non-default** half-life, asserting a decay value that differs from the same input at the default. Without it the whole task is unfalsifiable, exactly as in Task 1.
- Validation tests asserting the `Program.cs` guard's condition rejects `0`, a negative value, `NaN`, and `Infinity`. If the guard is not reachable from a test, assert the same predicate over a bound `DecayOptions` built from an in-memory configuration.

- [ ] **Step 7: Run both suites**

```bash
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/DecayOptions.cs \
        Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs
git commit -m "bind the decay half-life to configuration

Threaded as a parameter through ComputeDecay rather than injected, so
DecayFieldResolver stays internal static and pure and its ConditionalWeakTable
cache is untouched. DecayOptions is public because it is a parameter type of
ObjectSearchGrpcService's public primary constructor; internal there is CS0051.

Infinity passes a bare '> 0' check and yields 0.5^0 = 1.0 for every document,
disabling decay while appearing configured, so the guard tests finiteness first.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope":

- **Item 2 of the source document (embedding prefixes become model-conditional).** Deferred: its stated premise is false on `main`, which has no prefixing at all — `EmbedAsync` sends `new { model = ModelId, input = text }`. The prefix constants, `ingest-contract.json`, `verify_contract()` and ingest.py's `--model` flag exist only on branch `centroid-ablation` (9 ahead, 36 behind `main`). Item 2 gets its own spec once that work lands, at which point the source document's framing becomes true.
- **Retuning any default.** Every default equals what ships today.
- **Per-endpoint `Lambda`** (item 7 of the source document) — blocked behind α-nDCG over FreshStack's nugget qrels.
- **What the half-life or the weights *should* be.** This spec makes them settable.
- **Hot-reload / `IOptionsMonitor`.**
- **Any change to the sweep harness or to `ingest.py`.**
- **Items 3, 4 and 5** of the source document (harness build-identity, permutation test, chunk-budget guard) — benchmark tooling, unrelated to this change.

## Known issues inherited from spec

The source document is dated 2026-08-28 and three of its statements have since gone stale. They do not affect this plan, but a reader consulting it should know:

- **Item 6 is no longer untested.** It says re-running FreshStack under a correct arctic configuration is the only way to know whether the centroid's benefit is real. That ran on 2026-08-31: across 3.4x chunk density the optimum stayed at `w = 0.500` at every density while the gain magnitude fell monotonically. The chunks-per-document schedule is refuted and the centroid's benefit survives correct configuration.
- **Item 1's table is framed against `shipped w = 0.333`,** and the "explicitly not proposed" list says changing the shipped `WCentroid` default is not proposed. Branch `decay-share-triple-b` changes it to `w = 0.500`. The argument for configurability — opposite-signed significant effects across corpora — is untouched by this.
- **Item 4 names `scratchpad/stats.py`,** which no longer exists.
