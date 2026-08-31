# Vector ranking constants become configuration

**Source:** item 1 of `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`,
plus a fifth constant that document does not name (see "The fifth constant" below).

**Scope:** the five compile-time constants that decide retrieval ordering become bound
configuration, with defaults equal to whatever is shipped at implementation time. This change
enables tuning. It retunes nothing.

## Why

A single compile-time value cannot be correct, because the optimum differs by corpus and the
difference is significant in both directions. Raising the centroid ratio to `w = 0.500` is worth
+0.0059 AP on FreshStack (p = 0.0008, Holm-significant) and costs -0.0082 nDCG@10 on SciFact
(t = -2.54). Same change, opposite sign, both significant: any constant is wrong for half the
deployments.

The model control makes it stronger. Holding chunking, budget, corpus and qrels fixed and changing
*only* the embedding model moves the optimum from `w = 0.333` to `w = 1.000` (+0.0563 nDCG@10,
t = 7.73). The optimum is a property of the corpus *and* the model, and it swings across the whole
range — so a compile-time constant cannot survive an embedding-model upgrade, which is a thing
deployments do.

## Design

### `VectorRankingOptions` — the four fusion and diversification values

New file `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`. One class, one section, four
values. The `Section` const and `{ get; set; }` defaults mirror `EmbeddingServiceOptions`, so it
reads as a sibling of what already exists.

```csharp
namespace Iverson.Vector;

public sealed class VectorRankingOptions
{
    public const string Section = "VectorRanking";

    public double WBase     { get; set; } = 0.45;
    public double WCentroid { get; set; } = 0.45;
    public double WDecay    { get; set; } = 0.10;
    public double Lambda    { get; set; } = 0.70;
}
```

**The defaults must equal the shipped constants at implementation time, whatever they are.** That
equality is the whole basis of the "no behavioural change" claim, and it is the one thing an
implementer must check rather than copy from this spec: the values above are `main`'s successor,
not `main`'s present. See "Which defaults" below.

`ResultReranker` and `ResultDiversifier` become primary-constructor classes taking
`IOptions<VectorRankingOptions>` and capturing `.Value` into a private readonly field, so the hot
loop reads a field rather than a property:

```csharp
public sealed class ResultReranker(IOptions<VectorRankingOptions> options) : IResultReranker
{
    private readonly VectorRankingOptions _o = options.Value;
    // WBase -> _o.WBase, etc. Body otherwise unchanged.
}
```

Neither algorithm changes. The short-circuit at `ResultReranker.cs:26-31` stays correct: a weighted
mean over a single present signal is still that signal, for any weight.

The comment block recording *why* the shipped triple is what it is moves from the constant to the
options class, because that is now where the shipped value is decided.

### `DecayOptions` — the fifth constant

New file `Iverson.Server/Iverson.Api/Grpc/DecayOptions.cs`, `public` — it is a parameter type of
`ObjectSearchGrpcService`'s public primary constructor, so an `internal` type would be
`CS0051: Inconsistent accessibility`. `Iverson.Api` is an application assembly rather than a
published library, so widening costs nothing, and it matches `VectorRankingOptions` being public in
`Iverson.Vector`. Keeping it internal and adding a separate internal constructor is not an option:
`MapGrpcService<ObjectSearchGrpcService>()` (`Program.cs:443`) resolves the type from DI and needs
an accessible constructor.

```csharp
public sealed class DecayOptions
{
    public const string Section = "Decay";
    public double HalfLifeDays { get; set; } = 180.0;
}
```

`DecayFieldResolver` stays `internal static` and `ComputeDecay` stays pure. Converting it to a DI
service would drag in its `ConditionalWeakTable` cache and fourteen test call sites for no gain.
Instead the value is threaded exactly the way `now` already is: `ComputeDecay(storedValue, now)`
gains a `double halfLifeDays` parameter, and so does the `private static DecayFor(...)` helper at
`ObjectSearchGrpcService.cs:764` that wraps it. Both stay static and pure. The two `DecayFor` call
sites (`:260`, `:447`) sit inside instance methods, so they read the injected options.
`ObjectSearchGrpcService` gains `IOptions<DecayOptions> decayOptions` as a primary-constructor
parameter.

### Registration and validation

`services.Configure<T>()` binds lazily, which would defer a bad value until the first search — a
startup typo surfacing as a mid-traffic `NaN`. Both options are instead bound once, validated, and
registered as a concrete `IOptions<T>`:

```csharp
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
```

Throwing from the registration call fails startup **without** `Iverson.Vector` taking a dependency
on `Microsoft.Extensions.Hosting` merely to reach `ValidateOnStart()`. Registering a bound instance
also means no reload, matching `EmbeddingServiceOptions`; hot-reload is not wanted here.

`DecayOptions` gets the same three steps with two checks, finite and `HalfLifeDays > 0`. Zero does
not throw on its own — `ageDays / 0` is infinity and `Math.Min(1.0, ...)` swallows it — it silently
makes every document maximally fresh or maximally stale. A negative half-life inverts the curve so
older documents rank *fresher*. `Infinity` passes `> 0` and yields `0.5^0 = 1.0` for every document,
disabling decay while appearing configured; `NaN` fails every comparison and so passes the range
check unchallenged. Unparseable strings like `"abc"` already throw at `Bind`, so the gap is exactly
the values .NET parses as valid doubles. All of these are the silent wrongness this spec exists to
prevent.

**There is deliberately no sum-to-1 rule on the weights.** The fusion divides by the weights
actually present, so `(0.60, 0.30)` and `(0.20, 0.10)` are the same configuration; constraining the
sum would reject valid settings.

**Where each is registered.** `Iverson.Vector` gains
`AddVectorRanking(this IServiceCollection, IConfiguration)`, which takes over the two
`AddSingleton` lines currently at `ServiceCollectionExtensions.cs:50-51`. `AddQdrant` keeps its
positional-primitive signature untouched — threading `IConfiguration` through it would churn four
test call sites to no benefit, and ranking was never a Qdrant concern. `Program.cs` gains one call
after the existing `AddQdrant`. `DecayOptions` is bound and validated inline in `Program.cs`
instead: `Iverson.Api` is the composition root and already does inline `cfg.GetValue(...)` wiring,
whereas `Iverson.Vector` is a library that needs its own registration surface.

`ObjectSearchGrpcService` needs no change beyond the new constructor parameter — it already depends
on `IResultReranker`/`IResultDiversifier` and calls only `Rerank`/`Diversify`.

### Which defaults

At the time of writing, `main` carries `0.60 / 0.30 / 0.10` and branch `decay-share-triple-b`
(commit `4525b7e`, unmerged) carries `0.45 / 0.45 / 0.10`. `Lambda` is `0.70` on both;
`HalfLifeDays` is `180.0` on both. The implementer sets the defaults from the constants present on
the branch this work builds on, and the assertion the tests must make is *equality with those
constants*, not equality with the literals in this document.

### Tests

Six construction sites update mechanically — `new ResultReranker()` becomes
`new ResultReranker(Options.Create(new VectorRankingOptions()))`. Two are field initializers using
target-typed `new()` in `Iverson.Vector.Tests` (invisible to a `new ResultReranker` grep); four are
explicit in `Iverson.Api.Tests`. Seven `ComputeDecay` call sites in `DecayFieldResolverTests` gain
the half-life argument.

`ObjectSearchGrpcService`'s three construction sites gain the new `IOptions<DecayOptions>` argument:
`ObjectSearchGrpcServiceTests.cs:65`, `DocumentTemplateValidationTests.cs:327`, and
`ObjectSearchVectorIntegrationTests.cs:80` — the last is a target-typed `new(`, so a
`new ObjectSearchGrpcService` grep will not find it.

Because the defaults equal the shipped constants, **every existing hand-computed expectation stays
valid unchanged.** That is what demonstrates "no behavioural change at defaults", and it is the
reason the re-pointed tests are worth keeping rather than rewriting.

**Two tests carry the actual weight of this change.** Everything above would pass even if
`ResultReranker` ignored the injected options entirely and kept using constants, because the
defaults are identical — a green suite would prove nothing. So the implementation must include:

- at least one test constructed with **non-default** weights, asserting a correspondingly different
  fused score; and
- at least one test computing decay at a **non-default** half-life, asserting a correspondingly
  different decay value.

Plus five validation tests: negative weights, all-zero weights, `Lambda` outside `[0,1]`,
non-positive `HalfLifeDays`, and non-finite values (`NaN`, `Infinity`) in each options class.

## Verified assumptions

Verified against the codebase at `main` (commit `b9f283a`). Line numbers below are `main`'s.
Note that on branch `decay-share-triple-b` the added provenance comment shifts `ResultReranker.cs`
bodies down by seven lines. Empirical checks were run, not reasoned about.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.9 supplies `IOptions<T>`, `Options.Create`, `Bind`; no version conflict | Probe project built and ran against 10.0.9; `Options.Create` round-tripped |
| A2 | `Iverson.Vector.csproj` does not already reference it | grep count 0; it is the only new package, and `Iverson.Embeddings.csproj` already uses the same package at the same version |
| A3 | Both test projects can reach `Options.Create` | `Iverson.Vector.Tests.csproj:26` project-references `Iverson.Vector`, so the package flows transitively; `Iverson.Api.Tests` carries `Microsoft.AspNetCore.Mvc.Testing` |
| A4/A5/A17 | Exactly six construction sites; no non-test, non-DI consumer | Whole-repo grep on the type names (not `new X`, which misses target-typed `= new()`): `ResultRerankerTests.cs:16`, `ResultDiversifierTests.cs:8`, `ObjectSearchVectorIntegrationTests.cs:89-90`, `ObjectSearchGrpcServiceTests.cs:69`, `DocumentTemplateValidationTests.cs:332` |
| A6 | `ObjectSearchGrpcService` takes both as interfaces, calls only `Rerank`/`Diversify` | `ObjectSearchGrpcService.cs:39-40` (ctor), `:268`, `:288`, `:456`, `:463` (calls) |
| A7 | No test resolves either interface from an `AddQdrant` container, so moving the registrations is safe | `ServiceCollectionExtensionsTests.cs` asserts only `GetRequiredService<QdrantClient>()` |
| A8 | `cfg` is in scope where `AddVectorRanking(cfg)` goes | `Program.cs:24` `var cfg = builder.Configuration;`; `AddQdrant` call at `:182-186` |
| A9 | `AddQdrant` has one production call site | `Program.cs:182`; all others are tests |
| A10/A11 | The weights appear only inside `Rerank`; `Lambda` only inside `Diversify` | `ResultReranker.cs:35-48` (declared `:12`); `ResultDiversifier.cs:76-77` (declared `:12`) |
| **A12** | **`Bind` against an absent section leaves defaults intact** | **Probe: absent section bound to `0.45/0.45/0.1` λ `0.7`; a partial section setting only `WBase` left the other three at defaults.** Design-breaking if false — every integration test booting `Program` without a `VectorRanking` section would fail startup validation on all-zero weights |
| A13 | Primary-constructor-with-interface, options-injected, is the established style | `EmbeddingService.cs:9-13` takes `IOptions<EmbeddingServiceOptions>` exactly this way; `ObjectSearchGrpcService.cs:30-42` |
| **A14** | **Configuration binds `double` invariantly, not by host locale** | **Probe under `de-DE`: `"0.50"` bound to `0.5` and `"0.65"` to `0.65`.** A comma-decimal host locale cannot silently turn a weight into 50 |
| A15 | Defaults equal the shipped constants | `main` `ResultReranker.cs:12` = `0.60/0.30/0.10`; `decay-share-triple-b` `:19` = `0.45/0.45/0.10`; `ResultDiversifier.cs:12` = `0.70` on both — see "Which defaults" |
| A16 | `VectorRankingOptions` is reachable from both test projects | `public` in namespace `Iverson.Vector`, which both already use |
| A18 | *(sibling sweep)* Every hard-coded ranking constant in production code is covered | **FAILED as originally scoped** — the sweep found `DecayFieldResolver.cs:14 HalfLifeDays = 180.0`, a fifth ranking-affecting constant. Folded into the design as `DecayOptions` |
| A19 | An `internal` options class in `Iverson.Api` is testable | `Iverson.Api.csproj:10-12` `InternalsVisibleTo` → `Iverson.Api.Tests` |
| A22 | An options type used as a parameter of a `public` primary constructor must itself be `public` | Probe reproducing the exact shape (public sealed class, primary constructor, `IOptions<T>` with internal `T`) fails: `error CS0051: Inconsistent accessibility: parameter type 'IOptions<DecayOptions>' is less accessible than method`. `InternalsVisibleTo` (A19) does not affect this — it governs test visibility, not the public-surface rule |
| A23 | `ObjectSearchGrpcService` has exactly three construction sites, all of which gain the new argument | `ObjectSearchGrpcServiceTests.cs:65`, `DocumentTemplateValidationTests.cs:327`, and `ObjectSearchVectorIntegrationTests.cs:80` — the third is target-typed `new(`, invisible to a `new ObjectSearchGrpcService` grep |
| A20 | `ComputeDecay`'s signature change has bounded blast radius | 9 mentions: 1 declaration (`DecayFieldResolver.cs:76`), 1 production call (`ObjectSearchGrpcService.cs:766`), 7 in `DecayFieldResolverTests` |
| A21 | The production decay path runs through a static helper reachable from instance methods | `ObjectSearchGrpcService.cs:764` `private static DecayFor(...)`, called at `:260` and `:447`, both inside instance methods |

## The fifth constant

`HalfLifeDays` is not in the source document's item 1, and the decay-weight spec
(`docs/specs/2026-08-31-decay-weight-sensitivity-design.md`) explicitly placed the 180-day
half-life out of scope. It is included here because it was found by sweeping for the *class* of
thing this spec makes tunable rather than by working the source document's list: a spec that makes
decay's *weight* configurable while its *curve* stays compiled in leaves the surface half-done in a
way a reader would notice. No measurement in this project bears on what the half-life should be;
this makes it settable, not correct.

## A consequence worth naming

Standard binding means `VectorRanking__WBase=0.50` works as a docker-compose environment variable,
the same mechanism `Embeddings__ModelId` already uses. That is what would let a weight sweep restart
a container instead of rebuilding an image per configuration — the current sed-and-rebuild loop
costs roughly eight minutes a point. Making the harness exploit this is **not** in this spec.

## Out of scope

- **Item 2 of the source document (embedding prefixes become model-conditional).** Deferred: its
  stated premise is false on `main`, which has no prefixing at all — `EmbedAsync` sends
  `new { model = ModelId, input = text }`. The prefix constants, `ingest-contract.json`,
  `verify_contract()` and ingest.py's `--model` flag exist only on branch `centroid-ablation`
  (9 ahead, 36 behind `main`). Item 2 gets its own spec once that work lands, at which point the
  source document's framing becomes true.
- **Retuning any default.** Every default equals what ships today.
- **Per-endpoint `Lambda`** (item 7 of the source document) — blocked behind α-nDCG over
  FreshStack's nugget qrels.
- **What the half-life or the weights *should* be.** This spec makes them settable.
- **Hot-reload / `IOptionsMonitor`.**
- **Any change to the sweep harness or to `ingest.py`.**
- **Items 3, 4 and 5** of the source document (harness build-identity, permutation test,
  chunk-budget guard) — benchmark tooling, unrelated to this change.

## Known issues inherited from the source document

The source document is dated 2026-08-28 and three of its statements have since gone stale. They do
not affect this spec, but a reader consulting it should know:

- **Item 6 is no longer untested.** It says re-running FreshStack under a correct arctic
  configuration is the only way to know whether the centroid's benefit is real. That ran on
  2026-08-31: across 3.4x chunk density the optimum stayed at `w = 0.500` at every density while
  the gain magnitude fell monotonically. The chunks-per-document schedule is refuted and the
  centroid's benefit survives correct configuration.
- **Item 1's table is framed against `shipped w = 0.333`,** and the "explicitly not proposed" list
  says changing the shipped `WCentroid` default is not proposed. Branch `decay-share-triple-b`
  changes it to `w = 0.500`. The argument for configurability — opposite-signed significant effects
  across corpora — is untouched by this.
- **Item 4 names `scratchpad/stats.py`,** which no longer exists.
