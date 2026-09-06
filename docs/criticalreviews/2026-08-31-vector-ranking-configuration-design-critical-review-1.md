# Critical Design Review: 2026-08-31-vector-ranking-configuration-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-vector-ranking-configuration-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| `# Vector ranking constants become configuration` (header/scope) | ok — scope statement matches the design body; "defaults equal whatever is shipped at implementation time" is consistent with §"Which defaults" |
| `## Why` | ok — the three cited effects (FreshStack +0.0059 AP p=0.0008; SciFact -0.0082 nDCG@10 t=-2.54; model control +0.0563 t=7.73) are reproduced from the source document unchanged; no new claim introduced |
| `### VectorRankingOptions` | ok — class shape mirrors `EmbeddingServiceOptions.cs` (Section const, `{ get; set; }` defaults); `public` matches `ResultReranker`'s own accessibility |
| `### DecayOptions` | **→ §2.1** — `internal` type used as a parameter of a `public` primary constructor |
| `### Registration and validation` | **→ §2.2** — validation is presented as complete ("Nothing beyond that is validated") but admits non-finite doubles |
| `### Which defaults` | ok — correctly states main = 0.60/0.30/0.10 and `decay-share-triple-b` = 0.45/0.45/0.10; instructs the implementer to read from the target branch rather than copy the literals |
| `### Tests` | **→ §1 span** — enumerates 6 reranker/diversifier sites and 7 `ComputeDecay` sites but no `ObjectSearchGrpcService` construction sites, which the `DecayOptions` change also breaks |
| `## Verified assumptions` | see §1 |
| `## The fifth constant` | ok — accurately records that `HalfLifeDays` came from the sibling sweep, not the source document, and that no measurement bears on its value |
| `## A consequence worth naming` | ok — `VectorRanking__WBase` follows the same `__` binding the deployed `Embeddings__ModelId` already uses (`docker-compose.yml:372`, `:457`); claim is scoped as a consequence, explicitly not in scope |
| `## Out of scope` | ok — the item-2 deferral reason is re-verified: `main`'s `EmbeddingService` has no `Prefix`/`Compose` symbol at all, and `ingest-contract.json` exists only on `centroid-ablation` |
| `## Known issues inherited` | ok — all three staleness claims re-checked: `scratchpad/stats.py` absent; source doc line 20-25 is framed against `shipped w = 0.333`; the crossover result is recorded in `docs/centroid-weighting-proposal.md` |

### Rules and operands (both failure directions)

| Rule | Disposition |
|---|---|
| Weight non-negativity (`W* < 0`) | over-inclusion: ok — rejects nothing valid, since the fusion normalises by weights present. under-inclusion: **→ §2.2** — `NaN < 0` is false, so NaN passes |
| All-zero guard (`sum <= 0`) | over-inclusion: ok — only fires when every weight is 0. under-inclusion: **→ §2.2** — `NaN <= 0` and `Infinity <= 0` are both false |
| `Lambda is < 0 or > 1` | over-inclusion: ok — 0 and 1 are both admitted, and both are meaningful (pure relevance / pure diversity). under-inclusion: **→ §2.2** — NaN satisfies neither comparison |
| `HalfLifeDays > 0` | over-inclusion: ok. under-inclusion: **→ §2.2** — same non-finite gap; `Infinity` yields `0.5^0 = 1.0` for every document, i.e. decay silently disabled |
| Unparseable value (`"abc"`) | ok — verified empirically: `Bind` throws `InvalidOperationException`, so this class already fails fast and needs no rule |
| "Defaults equal the shipped constants" | ok — spec makes this an equality the tests must assert rather than a literal to copy, which is the correct form given the two branches disagree |

### Data-flow arrows (persistence boundaries flagged)

| Arrow → consuming operation | Disposition |
|---|---|
| config section → `Bind(opts)` | ok — verified empirically: absent section leaves all four defaults intact; a partial section leaves unset values at defaults; `de-DE` culture still parses `"0.50"` as `0.5` |
| bound instance → `AddSingleton(Options.Create(opts))` → `IOptions<VectorRankingOptions>` | ok — verified empirically in a probe: `GetService<IOptions<VectorRankingOptions>>()` resolves non-null, so the generic inference registers the interface, not the wrapper |
| `IOptions<VectorRankingOptions>` → `ResultReranker._o` → `Rerank` | ok — every operand `Rerank` consumes (`WBase`, `WCentroid`, `WDecay`) exists on the options class; `Lambda` likewise for `Diversify` |
| `IOptions<DecayOptions>` → `ObjectSearchGrpcService` → `DecayFor` → `ComputeDecay` | **→ §2.1** — this arrow does not compile |
| `DecayFor` call sites (`:260`, `:447`) → `ComputeDecay(stored, now, halfLifeDays)` | ok — both call sites are inside instance methods, so both can source the injected value; one row per call site checked, not one per operation |
| No arrow crosses a persistence or serialization boundary | ok — configuration is read once at startup into memory; nothing is written and re-read |

## 1. Verified-assumptions cross-check

All 21 listed assumptions still hold under a fresh read. Spot-confirmed this round: A2 (`Iverson.Vector.csproj` still has no Options package), A6 (`ObjectSearchGrpcService.cs:39-40` still takes the two interfaces), A8 (`Program.cs:24` `var cfg = builder.Configuration;`), A13 (`EmbeddingService.cs:9-13` is still the options-injected primary-constructor precedent), A19 (`Iverson.Api.csproj:10-12` `InternalsVisibleTo` → `Iverson.Api.Tests`), A21 (`ObjectSearchGrpcService.cs:764` `private static DecayFor`, called at `:260` and `:447`).

**A19 deserves a specific note: it holds exactly as written and was mis-applied.** It establishes that an `internal` type in `Iverson.Api` is visible to `Iverson.Api.Tests`. It does not establish that an `internal` type may be a parameter of a `public` constructor — a different rule entirely. The assumption is reconfirmed; the design's use of it is the defect in §2.1.

### Span check — uncovered dependencies

**1.a — The design depends on `DecayOptions` being usable as a parameter type of `ObjectSearchGrpcService`'s public primary constructor. No listed assumption covers this.** A19 covers test visibility only. Verified in-round and it fails → §2.1.

**1.b — The design changes `ObjectSearchGrpcService`'s constructor, but no assumption enumerates that type's construction sites.** A4/A5/A17 cover `ResultReranker`/`ResultDiversifier`; A20 covers `ComputeDecay`. Nothing covers the class whose signature the design actually widens. Verified in-round: there are **three** sites, all of which gain a tenth argument —

- `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:65`
- `Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:327`
- `Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:80` — **target-typed `new(`**, invisible to a `new ObjectSearchGrpcService` grep

The third is the notable one: the spec's own Tests section warns that two `ResultReranker` field initializers use target-typed `new()` and are "invisible to a `new ResultReranker` grep", then relies on an enumeration that falls into that same blind spot for a different type. Add a covering assumption listing all three sites.

## 2. Literal-wrongness findings

### 2.1 — `internal DecayOptions` cannot be a parameter of `ObjectSearchGrpcService`'s public constructor; the design does not compile

**Evidence.** `ObjectSearchGrpcService.cs:30` declares `public sealed class ObjectSearchGrpcService(` with a primary constructor, which is public. The spec's `### DecayOptions` section specifies `internal sealed class DecayOptions` and its registration section adds `IOptions<DecayOptions> decayOptions` as a primary-constructor parameter.

Confirmed empirically in an isolated probe reproducing the exact shape (public sealed class, primary constructor, `IOptions<T>` where `T` is internal):

```
error CS0051: Inconsistent accessibility: parameter type 'IOptions<DecayOptions>'
is less accessible than method
'PublicServiceWithInternalOptions.PublicServiceWithInternalOptions(IOptions<DecayOptions>)'
```

The spec's stated outcome — `HalfLifeDays` becomes configurable through `ObjectSearchGrpcService` — is not merely wrong but unbuildable.

**Proposed fix.** Declare `DecayOptions` `public`. `Iverson.Api` is an application assembly, not a published library, so widening the type costs nothing and is consistent with `VectorRankingOptions` being public in `Iverson.Vector`. The alternative — keeping it internal and giving `ObjectSearchGrpcService` a separate internal constructor — conflicts with `MapGrpcService<ObjectSearchGrpcService>()` (`Program.cs:443`) resolving the type from DI, which needs an accessible constructor. Amend the spec's `### DecayOptions` code block and drop the parenthetical justifying `internal` via `InternalsVisibleTo`.

### 2.2 — Non-finite values pass every validation check, producing exactly the silent `NaN` ranking the validation exists to prevent

**Evidence.** The spec presents its validation as complete: *"The three checks are exactly the cases that rank silently and wrongly … Nothing beyond that is validated."* All four checks are comparisons, and every comparison against `NaN` is false. Running the spec's validation code verbatim against bound configuration:

```
NaN weight         bound WBase=NaN      | spec-validation PASSES=True | sample fused=NaN
Infinity weight    bound WBase=Infinity | spec-validation PASSES=True | sample fused=NaN
NaN lambda         bound Lambda=NaN     | spec-validation PASSES=True
garbage 'abc'      THREW at bind: InvalidOperationException
```

`"abc"` is already caught at `Bind`, so the gap is specifically the values .NET *does* parse as valid doubles: `NaN`, `Infinity`, `-Infinity`. A `NaN` weight makes every fused score `NaN`; `Infinity` makes `weightedSum/weightTotal` `NaN`; a `NaN` `Lambda` makes every MMR score `NaN`. For `HalfLifeDays`, `Infinity` passes `> 0` and yields `0.5^0 = 1.0` for every document — decay silently disabled while appearing configured.

This is literal-wrongness against the spec's own stated outcome, not a hypothetical: the spec's purpose for validating at all is that a misconfiguration must not rank silently and wrongly, and this class does exactly that while passing.

**Proposed fix.** Make finiteness the first check on every value, before the range checks, in both options classes:

```csharp
if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
    !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.Lambda))
    throw new InvalidOperationException(
        $"{VectorRankingOptions.Section}: every value must be finite " +
        $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, Lambda={opts.Lambda}).");
```

and `!double.IsFinite(opts.HalfLifeDays)` for `DecayOptions`. Add a validation test per non-finite class; the spec's existing four validation tests do not cover it.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 non-empty, §3 empty. Both findings are contained: §2.1 is a one-word accessibility change plus deleting a now-wrong justification, and §2.2 is one guard per options class plus its tests. The span-check items in §1 add a covering assumption and three call sites to the Tests section. None of it disturbs the design's shape.
