# Finite fused scores: bound the ranking constants that can make them NaN

**Status:** design approved 2026-09-15 (bugfix pipeline, design-only tier, G2 root cause signed off).
**Branch:** `recencyboost-finite-bound` — failing reproduction tests already committed (`c5f88b7`).

## Problem

Three ranking settings are validated only as finite and non-negative, and each can make a result's fused score NaN.
A NaN fused score sorts below every real score (`OrderByDescending`'s default double comparer), so the affected
document is silently demoted and is dropped entirely when `TopK` is smaller than the candidate set; clients receive
`Score = NaN`. No error or log is produced.

1. **`PopularitySignal:RecencyBoost` overflow.** `PopularityFor` computes `effective = count + RecencyBoost × D`
   (`ObjectSearchGrpcService.cs:1010` chunks path, `:1053` objects path) and returns `effective / (effective + S)`.
   Past about `RecencyBoost = 10³⁰⁷` for a document with ~95 recent citations, the product overflows to +∞ and the
   quotient is NaN. The NaN poisons fusion **even at `WPopularity = 0`**: `ResultReranker.cs:55` adds
   `WPopularity × Popularity` whenever popularity is present, and `0 × NaN = NaN`. It hits only documents with recent
   citations — exactly the ones recency exists to promote. Reproduced end-to-end through `SearchSimilar`:
   `10⁹` and `10³⁰⁶` rank the recently-cited document first; `10³⁰⁷` and `double.MaxValue` return it last with NaN.
2. **`VectorRanking` weight overflow.** `ResultReranker.cs:37-59` accumulates the present signals' weights into
   `weightTotal`. The validator (`ServiceCollectionExtensions.cs:67-80`) bounds each weight below only, so two weights
   near `double.MaxValue` overflow the total to +∞: weights `MaxValue/MaxValue/0/0` fuse to NaN.
3. **`VectorRanking` zero divisor.** The validator's `sum of all four weights > 0` rule (`:84`) does not protect the
   division, which is over the weights of the signals *present on that candidate*. With `WBase = 0`, a candidate whose
   present signals all weigh zero fuses as `0/0 = NaN` — e.g. weights `0/0/0/0.45` are admitted, and a candidate carrying
   a centroid but no popularity computes NaN. 54 of 2,040 admitted (configuration, signal-presence) cells are NaN;
   every one has `WBase = 0`.

## Design

All three are closed at startup, matching this code's existing convention of rejecting a configuration that makes
fused scores NaN (`ServiceCollectionExtensions.cs:84`'s own message) and of bounding domain-limited settings
(`RecencyHalfLifeDays` to `(0, 300]`, λ to `[0, 1]`). No runtime code changes.

### `PopularitySignalOptions` (`Iverson.Api/Grpc/PopularitySignalOptions.cs:38-41`)

`RecencyBoost` must be finite and in **`[0, 1000000]`**. The finiteness check stays first, so NaN is rejected before any
comparison. The message states the reason in the style of the `RecencyHalfLifeDays` message: beyond the bound,
popularity saturates towards a constant for almost every document, so larger values cannot usefully change ranking
and only risk overflow.

Why 10⁶: with `RecencyBoost ≤ 10⁶` the arithmetic cannot overflow for any non-negative count the parsers accept — a probe with
`count = long.MaxValue` and a million series entries each at `long.MaxValue` still yields a finite popularity (`1.0`);
the smallest overflowing value for a 60-bucket series is `3.25 × 10²⁸⁷`. On the real 4,137-document SciFact citation
data, 96% of documents already have popularity above 0.99 at `RecencyBoost = 10⁶` (S = 50). A tighter bound such as 10³
would already saturate 38% of that data but could reject a legitimate setting on a corpus whose recent engagement is
sparse relative to lifetime counts, since the useful range scales with that ratio.

### `VectorRankingOptions` (`Iverson.Vector/ServiceCollectionExtensions.cs`, `AddVectorRanking`)

- **Each of `WBase`, `WCentroid`, `WDecay`, `WPopularity` must be ≤ 1000000**, added beside the existing finiteness and
  non-negativity checks. Weights matter only relative to one another, so an absolute cap costs no expressiveness.
- **`WBase` must be greater than zero.** The base similarity score is the only signal every candidate carries, so a
  positive `WBase` guarantees a positive divisor on every fusion path. This **replaces** the "at least one weight must be
  greater than zero" check at `:83-85`, which a positive `WBase` makes unreachable. The new message gives that reason.

With both rules, every combination of admitted weights and signal presence over each signal's full range — popularity
in [0, 1], which holds for every non-negative count — fuses to a finite score (probe: 0 non-finite of 76,544 cells with a positive divisor, `WBase` down to the smallest positive
double).

### Tests

- **`PopularitySignalOptionsTests`:** `RecencyBoost = 1000000` binds; `1000001` throws naming `RecencyBoost`. The two
  existing tests that assert the old wording (`:343`, `:360`, `"*RecencyBoost*finite and non-negative*"`) are updated to
  the new message.
- **`ObjectSearchGrpcServiceTests`, the committed reproduction tests** (`SearchSimilar_…` `:4716`, `SearchChunks_…`
  `:4760`): parameterised over `double.MaxValue` (must be rejected, naming `RecencyBoost`) and `1000000` (must be admitted,
  and both RPCs must emit only finite scores). Without the admitted case they would exercise only the rejection path.
- **`VectorRankingOptionsTests`:** a weight at `1000000` binds; `1000001` throws; `WBase = 0` with another weight positive
  throws. The existing `AllThreeWeightsZero` and `AllFourWeightsZero` tests still throw, now through the `WBase` rule.
- **`ResultRerankerTests`:** the largest admitted weights (`WBase` smallest positive and `1000000`, others `1000000`) over
  every signal-presence combination fuse to a finite score.

## Out of scope

- **A NaN centroid** still fuses to NaN. It is a separately documented known issue (`ResultDiversifier.cs:43-44`) with a
  different source — a zero-magnitude centroid vector, not a configuration value — and is not changed here.
- **A negative `<relation>Count`** still fuses to NaN. The readers parse the count with `long.TryParse`
  (`ObjectSearchGrpcService.cs:1006`, `:1048`), which accepts a sign, and the object upsert writes a parent property whose
  camelCase name equals `<relation>Count` under that same payload key (`IntelligenceStoreConsumer.cs:437`, `:450`). When
  `count + RecencyBoost × D = −SaturationPoint`, popularity is −∞ at every admitted weight, including `WPopularity = 0`;
  for some such payloads a positive `RecencyBoost` is what reaches the pole. Its source is payload data, not a
  configuration value, and it is not changed here.
- **A NaN `MetricValue`** at `PopularitySignalConsumer.cs:97` would cast to `long.MinValue`. The value is an aggregate
  `COUNT`, which cannot be NaN, so the path is unreachable; not changed.
- **`RegisterSchema_WithAuthorizationRules_ProvisionsAllStoresAndRoundTripsThroughPostgres`** fails on `main` independently
  of this work (deterministic, reproduced without these changes) and is not addressed here.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `AddPopularitySignalOptions` is the only production path that binds `PopularitySignalOptions`, so a startup bound covers production | grep of all bindings and constructions outside tests: only `Program.cs:227`, which registers `Options.Create(opts)` — no `IOptionsMonitor` or `IOptionsSnapshot` reload |
| 2 | The current `RecencyBoost` check is `!IsFinite \|\| < 0` | `PopularitySignalOptions.cs:38-41` |
| 3 | `D` is non-negative and bounded by its bucket counts | `DecayFieldResolver.cs:93-115`: counts parsed with `NumberStyles.None`, weight `Math.Min(1.0, …)`; reads every entry, not only 60 |
| 4 | With `RecencyBoost ≤ 10⁶`, popularity is finite for every parseable input with a non-negative count, including `SaturationPoint` at its extremes | IEEE-double mirror of `PopularityFor`: 0 non-finite of 216 cells over count, D, boost and S from the smallest positive double to `double.MaxValue`; negative counts reach −∞ at `effective = −S` (e.g. count −50, S 50, any boost), confirmed against the real `PopularityFor` and `ResultReranker` |
| 5 | Only the two computation sites feed `RecencyBoost` into arithmetic | grep of every non-test reader: `ObjectSearchGrpcService.cs:1010`, `:1053`; plus the `ValidateAtStartup` marker warning at `PopularitySignalOptions.cs:127`, which an upper bound does not affect |
| 6 | The popularity writer never writes a negative `<relation>Count` | `PopularitySignalConsumer.cs:130` writes `count = (long)(result.MetricValue ?? 0.0)` from an aggregate `COUNT(*)` (`StarRocksQueryBuilder.cs:296-298`, `:344`), and the reconciliation worker writes through the same updater. The object upsert (`IntelligenceStoreConsumer.cs:437`, `:450`) can write a negative value under that key for a parent property whose camelCase name collides with `<relation>Count` — confirmed against the real `BuildObjectPointPayload` (`AuthorCount = -50` lands as `authorCount = "-50"` and parses); that input class is out of scope |
| 7 | No configuration in the repository sets `RecencyBoost` or any ranking weight | grep across json, yaml, yml, env and tpl files: no hits outside design documents |
| 8 | Base scores are cosine similarities in [−1, 1] | `IntelligenceCollectionManager.cs:17`: `Distance.Cosine` |
| 9 | Decay values are in [0, 1] | `DecayFieldResolver.cs:83`: `Math.Min(1.0, Math.Pow(0.5, ageDays / halfLifeDays))` with `HalfLifeDays` validated finite and positive (`DecayOptions.cs:21`) |
| 10 | The four weights are the only other settings that can make a fused score non-finite | `SaturationPoint`, `RecencyHalfLifeDays`, `DecayOptions.HalfLifeDays` and both λs checked by the probes in rows 4 and 12; λ only enters MMR, bounded to [0, 1] |
| 11 | Two large finite weights overflow the fused score to NaN | IEEE-double mirror of `ResultReranker.cs:37-59`: `MaxValue/MaxValue/0/0` and `1e308/0/0/1e308` fuse to NaN |
| 12 | Every non-finite admitted cell has `WBase = 0`, and `WBase > 0` with weights ≤ 10⁶ closes all of them | probe over weights {0, 0.45, 1, 10⁶} and all eight presence combinations: 54 of 2,040 admitted cells NaN, all `WBase = 0`; with `WBase > 0`, 0; and 0 of 76,544 cells with a positive divisor over full signal ranges (popularity in [0, 1), i.e. non-negative counts) |
| 13 | Nothing relies on `WBase = 0` being admitted | only `VectorRankingOptionsTests.cs:88` and `:132` use `WBase = 0`, and both expect a throw |
| 14 | No test asserts the text of the `VectorRanking` weight messages | grep of `WithMessage` across `Iverson.Vector.Tests` and `Iverson.Api.Tests`: only the two `RecencyBoost` assertions |
| 15 | The recently-cited test payload at `RecencyBoost = 10⁶` yields a finite popularity | IEEE-double mirror: `pop ≈ 0.999999` for D between 88.7 and 100 |
