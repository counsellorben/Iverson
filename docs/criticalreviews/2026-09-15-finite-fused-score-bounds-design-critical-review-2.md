# Critical Design Review: 2026-09-15-finite-fused-score-bounds-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/.worktrees/recencyboost-finite-bound/docs/specs/2026-09-15-finite-fused-score-bounds-design.md`
**Artifact HEAD at review:** cc32ea9d2aeeb558cf7ca426d8640ed321421211
**Verified Assumptions section:** present

**Amendment detection** (round-1 anchor `e7f7ebf`, SHA form): content-identity check unequal (`git rev-parse e7f7ebf:<spec>` = `130dc348…`, `git hash-object <spec>` = `81faf78f…`). Authoritative hunk set: `git diff e7f7ebf -- <spec>`, which has 6 hunks: Why 10⁶, the closure sentence, the new out-of-scope bullet, and rows 4, 6 and 12. Forward window `git log e7f7ebf..HEAD -- <spec>` has two commits. `735506d applied 1 fix from …critical-review-1…` is in-band. `cc32ea9 scope the negative-count example in verified assumption 4 to a document with no bucket series` does not match the update skills' commit shapes, so it is out-of-band and reviewed at fix-equivalent rigor in row C1. The reverse window is empty, because `e7f7ebf` is an ancestor of HEAD. `git status --porcelain -- <spec>` is empty.

**Probes** (scratchpad `/tmp/claude-1000/-home-ben-repositories-Iverson/513a1973-d434-4010-8396-3a9127a00217/scratchpad/`). All of them ran this round against the compiled `Iverson.Api.dll` / `Iverson.Vector.dll` in `Iverson.Server/Iverson.Api.Tests/bin/Debug/net10.0`. Those DLLs were built 2026-09-15 10:41–10:42. The last commit touching `Iverson.Api` or `Iverson.Vector` source is `afe9e97` (2026-09-14), and `git status --porcelain Iverson.Server` is empty.

- **P-E** (`probe4/Program.cs`), the producer-shape matrix. The real private `IntelligenceStoreConsumer.BuildObjectPointPayload` builds a parent property named `AuthorCount` and, separately, one named `AuthorCountBuckets`. Each is built under every `SchemaBuilder` SQL type: `UUID, TEXT, INTEGER, BIGINT, REAL, DOUBLE PRECISION, BOOLEAN, TIMESTAMPTZ, BYTEA`, each of those as `[]`, plus a vector-field text property. Each gets 32 JSON values: `50, -50, 0, -0, 50.0, -50.0, 1.5, long.MaxValue, long.MaxValue+1, long.MinValue, 1e19, -1e19, 1e400, true, "50", "-50", " 50 ", "+50", "1.5", "99999999999999999999999"`, an ISO timestamp, `[50]`, `[-50]`, `["50"]`, `{"a":1}`, and valid, signed, malformed, list-wrapped and trailing-`;` series strings. Each built value then goes through the real `ToQdrantValue` → `ToCanonicalString`, which is the persisted-then-read shape, and on into the real `PopularityFor`.
  - **Count collision.** The run covered each shape × series `{none, "", <this month>:100, 60 × long.MaxValue}` × RecencyBoost `{0, 1, 1e6}` × S `{ε, 50, MaxValue}`. Output: `cells=7704 absent/unparsed=5794 parsed-non-negative=1584 (popularity outside [0,1] or NaN: 0) parsed-negative=720`. There are 20 distinct negative-parsing producer shapes: BIGINT/INTEGER/REAL/DOUBLE PRECISION numbers, UUID/BYTEA/TEXT numbers or strings, and vector-field text. Every one of them lands as a canonical `"-50"` or `"-9223372036854775808"`.
  - **Buckets collision.** The run covered every shape through the real `ComputeRecencySum` × half-life `{ε, 1, 180, 300}`, then through `PopularityFor` with count `{0, long.MaxValue}` × boost × S. Output: `D cells=856 negative/non-finite D or popularity outside [0,1]=0 maxD=1.783E+019`. Only the 12 plain-string shapes (UUID, BYTEA, TEXT, vector field) produce a non-zero D. List, number, bool and timestamp shapes all give D = 0.
- **P-F** (`probe5/Program.cs`), call-site parity and the new bullet's claims.
  - **Parity.** The real static `PopularityFor` (SearchSimilar reader) and the real private instance `RetrievePopularityOrDegradeAsync` were compared. The second was invoked on an uninitialized `ObjectSearchGrpcService` with a `DispatchProxy` `IVectorQueryService` and a real `IntelligenceTenantScope`, so it is the SearchChunks / SimilarViaChunks reader. Grid: counts `{0, 50, long.MaxValue, -0, +50, " 50 ", -50, -100, long.MinValue, 1.5, 1E+19, long.MaxValue+1, "[ 50 ]", true}` × series `{none, "", <this month>:50, <this month>:-5, 2099-01:100, 60 × long.MaxValue, list-wrapped}` × boost `{0, 1, 1e6}` × S `{ε, 50, MaxValue}`. Output: `cells=882 differing=0`.
  - **Named cases on both readers, each fused by the real `ResultReranker`:**
    - Count −50, S 50, no series: popularity `-Infinity` at boost 0, 1 and 1e6. The same holds with an empty series at 1e6.
    - Count −50 with series `<this month>:50`: `-Infinity` at boost 0 and `-0.0583` at boost 1.
    - Count −100 with series `2099-01:50`, S 50: `2` at boost 0 and `-Infinity` at boost 1.
    - Count −49, no series: `-49`, which fuses to `-14.7069` at `0.45/0.45/0.10/0.45`.
    - Count long.MaxValue at S ε, and count 50 with `2099-01:50` at boost 1e6 and S 1e-300: popularity exactly `1`.
    - A −∞ popularity fuses to `NaN` at `0.45/0.45/0.10/0`, and to `-Infinity` at `0.45/0.45/0.10/0.45`, at `1E-300/0/0/1e6` and at `1e6/1e6/1e6/1e6`.
- **P-G** (`probe6/Program.cs`), the admitted-weight closure. This is round 1's P-A harness with the grid changed: weights `{0, ε, 1e-300, 0.45, 1, 1e6}` per weight × 1,260 candidates per call. Base is `{-1, (double)-0.99999994f, 0, ε, 0.5, 1, (double)1.0000001f}`, centroid is `{absent, cos 1, −1, 0, ≈0.707, ≈1}`, decay is `{absent, 0, ε, 0.5, 1}`, and popularity is `{absent, 0, ε, 0.5, 1−2⁻⁵³, 1}`. Output: `current-admitted configs=1295, cells=1631700, non-finite=5705 in 90 configs` / `new-admitted configs=1080, cells=1360800, non-finite=0` / `non-finite cells in current-admitted-but-new-rejected configs=5705; all-finite configs newly rejected=125`.

## 0. Coverage enumeration

**Sections**

| # | Item | Disposition |
|---|---|---|
| S1 | Header: Status / Branch | ok, not load-bearing. `git log --oneline -3 c5f88b7` shows `c5f88b7 add failing reproduction tests for the RecencyBoost overflow`. |
| S2 | Problem 1: RecencyBoost overflow, and NaN even at `WPopularity = 0` | `[existence]` ok, not load-bearing for the fix (the failure it describes is what the rule rejects). Read `ResultReranker.cs:53-56`: popularity is added whenever it is present. P-F confirms the mechanism class: a non-finite popularity fused at `WPopularity = 0` gives `NaN`. The 10³⁰⁶/10³⁰⁷ transition is not load-bearing for the fix. |
| S3 | Problem 2: weight overflow | `[totality]` ok. P-G finds 5,705 non-finite cells in 90 currently admitted configs, and every one is in a config the new rules reject. |
| S4 | Problem 3: zero divisor, "54 of 2,040 … every one has `WBase = 0`" | `[totality]` ok, recomputed. The grid {0, 0.45, 1, 1e6}⁴ has 255 non-all-zero configs × 8 presence combinations = 2,040 cells. With `WBase = 0`, a non-empty present subset whose weights are all zero gives 0/0. Single-signal subsets give 3 × (4²−1) = 45, two-signal subsets give 3 × (4−1) = 9, and three-signal subsets give 1 × 0 = 0, for 54 in total. The empty subset takes the short-circuit at `ResultReranker.cs:26-34`. |
| S5 | Design intro ("closed at startup … No runtime code changes") | `[existence]` ok. `ServiceCollectionExtensions.cs:84`'s message reads "all-zero weights make every fused score NaN". `PopularitySignalOptions.cs:43-48` bounds RecencyHalfLifeDays to (0, 300], and `ServiceCollectionExtensions.cs:87-93` bounds λ to [0, 1]. |
| S6 | `PopularitySignalOptions` rule | → R1 |
| S7 | **Why 10⁶** (fix site a): "cannot overflow for any non-negative count the parsers accept … finite popularity (`1.0`) … smallest overflowing value for a 60-bucket series is `3.25 × 10²⁸⁷`" | over: `[existence]` ok. The narrowing admits nothing that is not finite: P-F gives popularity exactly `1` at count long.MaxValue with S ε. 1.797e308 / (60 × 9.223e18) = 3.248e287, which matches the sentence. / under: `[totality]` ok over every string the readers can see. **Count:** `long.TryParse` bounds count to [0, 9.22e18] once it is non-negative. P-E's 1,584 non-negative-parsed cells, across every producer shape, have 0 popularity values outside [0, 1]. **D:** `NumberStyles.None` (`DecayFieldResolver.cs:108`) and `Math.Min(1.0, …)` (`:112`) give D ≤ N × 9.22e18 for an N-entry series, so boost × D with boost ≤ 1e6 overflows only when N ≥ 1.95e283. No payload string has that many entries. P-E's Buckets matrix (0 of 856) and P-F's 60 × long.MaxValue cells are finite. The SearchChunks reader is identical to the SearchSimilar reader (P-F parity, 0 of 882 differ). |
| S8 | `VectorRankingOptions` rules | → R2, R3, R4 |
| S9 | **Closure sentence** (fix site a): "over each signal's full range — popularity in [0, 1], which holds for every non-negative count — fuses to a finite score" | over: `[totality]` ok. "Popularity in [0, 1] for every non-negative count" holds on P-E's full producer-shape matrix: 0 of 1,584 cells fall outside [0, 1]. The closed upper end is real: P-F reaches exactly `1`. / under: `[totality]` ok. P-G includes popularity `1` and `ε`, base `(double)1.0000001f`, and `WBase` ∈ {ε, 1e-300}, and gives 0 non-finite of 1,360,800 new-admitted cells. |
| S10 | Tests: `PopularitySignalOptionsTests` (1e6 binds, 1000001 throws, update `:343`/`:360`) | `[negative]` ok. grep `WithMessage` across `Iverson.Api.Tests` and `Iverson.Vector.Tests` finds RecencyBoost text only at `PopularitySignalOptionsTests.cs:343` and `:360`, both `"*RecencyBoost*finite and non-negative*"`. |
| S11 | Tests: reproduction tests at `:4716` / `:4760`, parameterised over `double.MaxValue` / `1000000` | `[existence]` ok. `ObjectSearchGrpcServiceTests.cs:4716` is `SearchSimilar_RecencyBoostTheValidatorAdmits_NeverEmitsANonFiniteScore` and `:4760` is `SearchChunks_…`. Both currently call `BindRecencyBoostOrRejected(double.MaxValue)`, which catches an `InvalidOperationException` whose message contains "RecencyBoost". Round 1 S11 ran both red, and nothing in the source has changed since. |
| S12 | Tests: `VectorRankingOptionsTests` | `[existence]` ok. `:85` `AllThreeWeightsZero` and `:129` `AllFourWeightsZero` both set `("WBase", "0")` (`:88`, `:132`). In the new order (finite, non-negative, `WBase > 0`), both throw at the `WBase` rule. |
| S13 | Tests: `ResultRerankerTests` finite at the largest admitted weights | `[totality]` ok. P-G's new-admitted grid contains `WBase` ∈ {ε, 1e6} with the other weights at 1e6, over all eight presence combinations: 0 non-finite. |
| S14 | Out of scope: NaN centroid | `[existence]` ok. `ResultDiversifier.cs:43-44` reads "A NaN fused score is reachable (a NaN centroid …)". |
| S15 | **Out of scope: negative `<relation>Count`** (fix site a, family b) | Split clause by clause into rows B1–B5 below. |
| S16 | Out of scope: NaN `MetricValue` | `[existence]` ok. `PopularitySignalConsumer.cs:97` is `(long)(result.MetricValue ?? 0.0)`. |
| S17 | Out of scope: `RegisterSchema_WithAuthorizationRules_…` failing on main | dropped. It concerns test-suite state, not the design's behavior. |
| S18 | Verified assumptions | → §1 |

**Round-1 fix text, clause by clause (families a + b): the new out-of-scope bullet**

| # | Clause | Disposition |
|---|---|---|
| B1 | "The readers parse the count with `long.TryParse` (`:1006`, `:1048`), which accepts a sign" | `[existence]` ok. `sed -n` shows `:1006` is `… && long.TryParse(stored, out var count))` and `:1048` is `… \|\| !long.TryParse(stored, out var count))`. `[totality]` P-F parity: both readers agree on the signed, whitespace, `+`, overflowing and list shapes (0 of 882 differ). |
| B2 | "the object upsert writes a parent property whose camelCase name equals `<relation>Count` under that same payload key (`IntelligenceStoreConsumer.cs:437`, `:450`)" | over: `[existence]` ok. `:437` is `pointPayload[vf.PropertyName.ToCamelCase()] = fieldText;` and `:450` is `pointPayload[col.Name.ToCamelCase()] = val;`. / under: `[totality]` ok. P-E ran both loops. Round 1 ran only the scalar loop and left `:437` read-tier; P-E upgrades it. There are 20 negative-parsing shapes across 7 scalar types plus vector-field text, and the bullet's "a parent property whose camelCase name equals" covers all of them. |
| B3 | "When `count + RecencyBoost × D = −SaturationPoint`, popularity is −∞ at every admitted weight, including `WPopularity = 0`" | `[totality]` ok. Popularity is computed before fusion (`ObjectSearchGrpcService.cs:1010-1011`, `:1053-1054`), so its value does not depend on any weight. P-F gives `-Infinity` on both readers at every pole case. IEEE addition of `x + (−x)` gives +0 exactly, and `x + y = 0` only when `y = −x`, so the pole is the equality and nothing near it. Off-pole values stay finite: P-F gives −49, 2 and 19.16. |
| B4 | "for some such payloads a positive `RecencyBoost` is what reaches the pole" | `[existence]` ok. P-F, both readers: count −100, series `2099-01:50`, S 50 gives `2` at boost 0 and `-Infinity` at boost 1. |
| B5 | Bullet heading: "A negative `<relation>Count` still fuses to **NaN**" | **dropped** (fails literal-wrongness). P-F shows the heading is exact only at the pole with `WPopularity = 0`, the shipped default. At the pole with `WPopularity > 0` the fused score is `-Infinity`, and off-pole negative counts fuse to finite scores outside the signal range (−14.71 at `0.45/0.45/0.10/0.45`). The bullet describes a class the design explicitly leaves unchanged. No rule, no test in §Tests, and no in-scope claim depends on the NaN-versus-−∞ distinction or on off-pole behavior, so the asked-for startup bounds and their finiteness guarantee are unaffected. |

**Rules and operands**

| # | Rule | Disposition |
|---|---|---|
| R1 | `RecencyBoost` finite and in [0, 1e6] | over: `[negative]` ok. grep `RecencyBoost\|WPopularity\|WBase\|WCentroid\|WDecay` over non-`.cs` files outside `docs/` finds 0 hits. grep `VectorRanking__\|PopularitySignal__` outside docs finds only `docker-compose.yml:463-465` (λs and SimilarViaChunksTypes). grep over `../iverson-benchmark-corpora` `*.py,*.sh,*.yml,*.yaml,*.env` for `RecencyBoost\|RECENCY_BOOST\|WPopularity` finds 0 files. / under: `[totality]` ok for non-negative counts, per S7. Negative counts are the scoped-out class of B2–B3. |
| R2 | Each weight ≤ 1e6 | over: `[negative]` ok, the same greps as R1. / under: `[totality]` ok. P-G gives 0 of 1,360,800. |
| R3 | `WBase > 0` | over: `[negative]` ok. grep `"WBase", "0"` / `WBase = 0` in `.cs` finds only `VectorRankingOptionsTests.cs:88`, `:132`, both of which expect a throw. P-G counts 125 all-finite grid configs that become rejected; that is the design-gate choice and is not re-litigated. / under: `[totality]` ok. P-G's 5,705 non-finite cells are all in rejected configs. |
| R4 | Drop the sum > 0 check | over: `[totality]` ok. The non-negativity check (`:76`) precedes it, so `WBase > 0` implies sum > 0 and removing the check rejects nothing the `WBase` rule admits. P-G's new-admitted set is computed without the sum check and contains no config with sum ≤ 0. / under: `[totality]` ok. Every config the sum check rejects is all-zero, so it has `WBase = 0` and R3 rejects it; P-G's grid includes 0/0/0/0. |
| R5 | Count reader `long.TryParse(stored, out count)`: which inputs become a count | over: `[totality]` ok. P-E shows no non-integer, list, bool, timestamp or out-of-`long`-range shape parses (5,794 cells stay absent), so no non-count value is admitted as a count. / under: `[totality]` ok. P-E shows every integer shape in every producer type parses (1,584 non-negative-parsed cells and 720 negative-parsed cells), and P-F shows both readers agree (0 of 882). The three resulting classes are unparsed (no signal), non-negative (covered by S7/S9) and negative (scoped out, B2). The matrix below disposes each writer. |
| R6 | Series reader `ComputeRecencySum`: D over any string | over: `[totality]` ok. P-E's Buckets matrix gives 0 of 856 cells with a negative or non-finite D. Malformed input returns 0.0 (`DecayFieldResolver.cs:101`, `:106`, `:109`), and a list-wrapped `[ "…" ]` shape always fails the first entry's `yyyy-MM` parse, so it gives 0. / under: `[totality]` ok. P-E shows the 12 well-formed string shapes (UUID, BYTEA, TEXT, vector field) all yield a non-zero D, up to `1.783E+019`, so a valid series is not discarded. |

**Population closure: (narrowed claims S7, S9 and scoped class B2) × every writer of the `<relation>Count` / `<relation>CountBuckets` keys × every shape that writer produces**

Writer roster, `[negative]`: grep `UpsertNamedAsync|vectorWrite\.|client\.UpsertAsync|SetPayloadAsync\(` over non-test server code, excluding LoadTest, finds `IntelligenceStoreConsumer.cs:161` and `:386` (both `BuildObjectPointPayload`), `:336` (chunk upsert), `PopularitySignalConsumer.cs:128`, `IntelligenceCollectionManager.cs:206`, and the primitives in `IntelligenceVectorService.cs`. grep `updater\.UpdateAsync` finds `PopularitySignalConsumer.cs:232`, `:238` and `PopularitySignalReconciliationWorker`. The writer set matches round 1's roster, with no new writer since `e7f7ebf`: `git log e7f7ebf..HEAD -- Iverson.Server` is empty.

| Writer | Count cell | Buckets cell |
|---|---|---|
| `PopularitySignalUpdater` (`PopularitySignalConsumer.cs:128-132`; consumer `:232`/`:238`; reconciliation worker) | `[existence]` ok. Writes `long count` (`:97`) from `COUNT(*)` (`StarRocksQueryBuilder.cs:296-298`, `:344`). `ToCanonicalString` IntegerValue uses invariant culture (`IntelligenceVectorService.cs:257`), so the value is non-negative and falls under S7/S9. | `[totality]` ok. `$"{b.Key}:{b.DocCount}"` over `TakeLast(60)` (`:121-123`). D ≥ 0 and finite by R6. |
| `BuildObjectPointPayload` scalar loop `:450`, × 18 SQL types | `[totality]` ok, per P-E. Arrays, BOOLEAN, TIMESTAMPTZ, non-integers and out-of-range values stay unparsed, so popularity is absent. Non-negative integers in any numeric or string type fall under S7/S9, with 0 violations. Negative integers are the scoped class (B2). | `[totality]` ok, per P-E. 0 of 856 give a negative or non-finite D, or a popularity outside [0, 1]. |
| `BuildObjectPointPayload` vector-field loop `:437` | `[totality]` ok, per P-E "VECTORFIELD" rows. The classes are the same as TEXT. | `[totality]` ok, per P-E. |
| `BuildObjectPointPayload` FK loop `:455` | `[negative]` ok. At `SchemaBuilder.cs:120-125`, an FK is added only when `prop.Name` ends in `Id`/`Ids`, so the name cannot end in `Count`. | `[negative]` ok, same reason: it cannot end in `CountBuckets`. |
| `BuildObjectPointPayload` literal `["key"]` (`:432`) | `[negative]` ok. The fixed name `key` cannot equal `…Count`. | `[negative]` ok. |
| `IntelligenceCollectionManager.cs:206` migration copy | `[negative]` ok. `:193` `ps.Payload[kvp.Key] = kvp.Value` re-emits other writers' values verbatim. | `[negative]` ok. |
| `IntelligenceStoreConsumer.cs:336` chunk upsert | `[negative]` ok. It writes the chunks collection. The readers read the object collection: `PopularityFor` reads results of `SearchNamedAsync(collectionName, …)` (`:279`), and `RetrievePopularityOrDegradeAsync` receives `ResolveCollectionName(…, isChunks: false)` (`:679`). | `[negative]` ok. |

**Data-flow arrows and call sites**

| # | Arrow | Disposition |
|---|---|---|
| A1 | Popularity reader, SearchSimilar: `PopularityFor` `:310` | `[totality]` ok. P-E and P-F both drive the real method. |
| A2 | Popularity reader, SearchChunks / SimilarViaChunks: `RetrievePopularityOrDegradeAsync` `:678` | `[bidirectional]` ok. P-F parity with A1 gives 0 of 882 differing, so each spec claim holds at both call sites. grep `RetrievePopularityOrDegradeAsync` finds only `:678` plus the declaration `:980`. |
| A3 | `Rerank` call sites | `[negative]` ok. grep `reranker\.Rerank` finds exactly `:318` and `:699`. |
| A4 | Options binding → reranker and readers | `[negative]` ok. grep `AddVectorRanking\|AddPopularitySignalOptions` finds only `Program.cs:225` and `:227`. grep `new VectorRankingOptions\|new PopularitySignalOptions\|IOptionsMonitor<…>\|IOptionsSnapshot<…>\|Configure<…>` in non-test code finds only the two binders (`ServiceCollectionExtensions.cs:64`, `PopularitySignalOptions.cs:30`). |
| A5 | **Persistence boundary**: object payload write (`ToQdrantValue`) → Qdrant → read (`ToCanonicalString`) → readers | `[compat]` ok. P-E pushes the real builder's output through both real conversions before it reaches the real reader. The persisted shape of a DOUBLE `-50.0` is `"-50"`, which parses. A list becomes `[ … ]`, which never parses as a count and gives D = 0 as a series. |
| A6 | Settings roster: every setting read on the fusion path, for row 10 | `[negative]` ok. grep of `_ranking.`, `_decayOptions.` and `_popularitySignal.` in `ObjectSearchGrpcService.cs` finds `HalfLifeDays`, `Signals`, `LambdaSimilar`, `LambdaChunks`, `SimilarViaChunksTypes`. `ResultReranker` reads the four weights, and the readers read `SaturationPoint`, `RecencyBoost` and `RecencyHalfLifeDays`. `Signals` and `SimilarViaChunksTypes` only select or route: routed calls reach `SearchChunksFusedAsync`, which A2 covers. |

**Amendment hunks (family c) and in-band additions**

| # | Hunk | Disposition |
|---|---|---|
| C1 | `cc32ea9`, row 4: the example "count −50, S 50, any boost" becomes "count −50, S 50, no bucket series so D = 0, at any boost" | over: `[totality]` ok. P-F, both readers: count −50, S 50 with no series gives `-Infinity` at boost 0, 1 and 1e6, and an empty series at 1e6 also gives `-Infinity`. / under: `[existence]` ok. The commit's premise holds: P-F count −50 with series `<this month>:50` gives `-0.0583` at boost 1, off the pole. The example is marked "e.g.", so it need not list the other D = 0 payloads (empty or malformed series, per R6). Restatement check: grep the spec for `any boost` finds only row 4. The out-of-scope bullet states the pole conditionally (`count + RecencyBoost × D = −SaturationPoint`), so it is right as written. |
| C2 | `735506d`, row 12's added parenthetical "(popularity in [0, 1), i.e. non-negative counts)". Round 1's fix did not propose this text. | **dropped** (fails literal-wrongness). Non-negative counts reach popularity exactly `1` (P-F), so the half-open interval understates their range and disagrees with the closure sentence's `[0, 1]`. The design dependency this could leave uncovered is that popularity = 1 fuses finite under every admitted weight. It is verified in-round by P-G, where popularity includes `1` and 0 of 1,360,800 cells are non-finite. No rule or test depends on the interval's endpoint. |
| C3 | `735506d`, row 6's rewrite (the popularity writer's sign, `StarRocksQueryBuilder.cs:296-298`/`:344`, the reconciliation worker through the same updater, and the `BuildObjectPointPayload` collision) | `[existence]` ok. The cites point at `isCountAll` and `SELECT COUNT(*) AS metric_val`. The reconciliation worker calls `updater.UpdateAsync`. "`AuthorCount = -50` lands as `authorCount = "-50"` and parses" is reproduced by P-E (BIGINT `-50` → `"-50"`). |
| C4 | `735506d`, rows 4 and 6 and the Why-10⁶ / closure / bullet hunks | Covered by S7, S9, B1–B5 and §1 rows 4 and 6. |

## 1. Verified-assumptions cross-check

1. Holds. `AddPopularitySignalOptions` is called only at `Program.cs:227`, and `new PopularitySignalOptions` appears only at `PopularitySignalOptions.cs:30`, which ends in `services.AddSingleton(Options.Create(opts))` (`:50`). No `IOptionsMonitor`, `IOptionsSnapshot` or `Configure<>` exists for it.
2. Holds. `PopularitySignalOptions.cs:38` reads `!double.IsFinite(opts.RecencyBoost) || opts.RecencyBoost < 0`.
3. Holds. `DecayFieldResolver.cs:108` uses `NumberStyles.None`, `:112` uses `Math.Min(1.0, …)`, and `:98` iterates every entry.
4. Holds as narrowed. For non-negative counts: P-E finds 0 of 1,584 non-negative-parsed producer-shape cells outside [0, 1], across S {ε, 50, MaxValue} and boost ≤ 1e6. The negative-count example as scoped by `cc32ea9` is confirmed on both readers (C1).
5. Holds. grep of non-test `RecencyBoost` readers finds `ObjectSearchGrpcService.cs:1010` and `:1053`, plus `PopularitySignalOptions.cs:38-41` and `:127`.
6. Holds as narrowed (C3). The popularity writer's count is `(long)` of `COUNT(*)`, and the collision writer is reproduced by P-E.
7. Holds. See the grep in R1; the benchmark-corpora repo was also checked.
8. Holds. `IntelligenceCollectionManager.cs:17` is `public const Distance Metric = Distance.Cosine;`.
9. Holds. `DecayFieldResolver.cs:83`; `DecayOptions.cs:21` reads `!double.IsFinite(opts.HalfLifeDays) || opts.HalfLifeDays <= 0`.
10. Holds. The settings roster is in A6. λ enters only MMR. `SaturationPoint`, `RecencyHalfLifeDays` and `HalfLifeDays` are exercised at their extremes by P-E, P-F and round 1's P-D.
11. Holds. `ResultReranker.cs:37-59` has no overflow guard, and P-G's current-admitted set has 5,705 non-finite cells.
12. Holds. P-G gives 0 non-finite among new-admitted configs, and every currently non-finite cell is in a rejected config. The "54 of 2,040, all `WBase = 0`" count is recomputed in S4. The parenthetical's `[0, 1)` is narrower than the real range; see C2, where the gap is covered.
13. Holds. `("WBase", "0")` appears only at `VectorRankingOptionsTests.cs:88` and `:132`, both expecting `Throw`.
14. Holds. grep `WithMessage` finds `VectorRankingOptionsTests.cs:207` (Lambda) and `:234` (SimilarViaChunksTypes), and in `Iverson.Api.Tests` only `PopularitySignalOptionsTests.cs:343` and `:360` match RecencyBoost or weight text.
15. Holds. With count 5, S 100 and D ∈ [88.7, 100], effective ≈ 8.87e7–1e8, so pop = effective / (effective + 100) ≈ 0.999999. The value is finite, consistent with P-E's 0 violations at boost 1e6.

Span check:
- **Do both readers compute the same popularity?** Verified by P-F parity (A2).
- **Is every writer of either key, in every shape, covered by a narrowed claim or the scoped class?** Verified by the population matrix via P-E. No fourth input class exists.
- **Does popularity exactly 1, which non-negative counts reach, fuse finite?** Verified by P-G (C2).
- **Does the Buckets-key collision, which no row names, stay within "D non-negative and bounded"?** Verified by P-E's Buckets matrix and R6.

All verified assumptions reconfirmed; the span check found no uncovered dependency.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** (a negative `<relation>Count` is a producible input the spec neither closed nor scoped out, so its totality claims were false) is resolved by `735506d` and `cc32ea9`:
  - Why 10⁶ and row 4 are narrowed to non-negative counts.
  - The closure sentence states the popularity range.
  - Row 6 names the collision writer.
  - The new out-of-scope bullet scopes the class out.
  - This round finds the narrowed claims true over every writer × shape (P-E) at both reader call sites (P-F).

## 5. Recommendation

✅ **Approve as-is**. The narrowed claims hold over every producer shape of both payload keys at both reader call sites, and the startup rules close the admitted weight population (P-G: 0 of 1,360,800). Two text imprecisions were dropped because they fail the literal-wrongness test: B5 (the bullet heading says NaN where a positive `WPopularity` gives −∞) and C2 (row 12 says `[0, 1)`). No rule, test or in-scope claim depends on either.
