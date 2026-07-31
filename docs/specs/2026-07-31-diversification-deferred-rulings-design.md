# Diversification Deferred Rulings — Retrieve Unification, Gating, and the Centroid Zero Guard

Date: 2026-07-31
Status: Approved design, not yet planned or implemented

**Follow-up to part 4b of the metadata / tensor-search initiative.**

## Context

Part 4b (`docs/specs/2026-07-30-result-diversification-design.md`) added greedy MMR selection to
`SearchChunks` and `SearchSimilar`, merged at `main@d6f4ff2`. Its execution and final whole-branch
review closed out cleanly but left four items deliberately unaddressed — three raised by the final
review and parked with rulings, one carried as "Out of scope, but known" since the 4b spec itself.

This spec addresses all four. None of them changes what part 4b does; they clean up how it does it,
close one silent-failure path, and write down one consequence that has to stay a consequence.

**Base branch:** `main`, at `d6f4ff2`.

## Goal

Remove the duplicated retrieve shape that part 4b introduced, stop issuing a Qdrant round trip that
provably cannot affect the answer, make a degenerate chunk embedding fail visibly instead of
silently poisoning a document's centroid, and record the one bias the design accepts by construction.

## Design

### 1. One degrade-on-failure retrieve helper

Part 4b's chunk-vector retrieve (`ObjectSearchGrpcService.cs:394-412`) reproduces
`FetchCentroidsAsync` (`:690-709`) almost exactly: mint a scoped read-only api-key, retrieve, and on
failure `catch (Exception ex) when (ex is not OperationCanceledException)`, log a warning, return an
empty map. The bodies differ only in the log's trailing clause. Two copies of a cancellation-filter
rule is precisely the coupling that rots.

`FetchCentroidsAsync` becomes signal-neutral:

```csharp
private async Task<IReadOnlyDictionary<ulong, float[]>> RetrieveVectorsOrDegradeAsync(
    string collection, IReadOnlyList<ulong> ids, string vectorName, string rpcName, string consequence)
```

with the log message parameterized on `consequence`. Three call sites:

| Call site | `rpcName` | `consequence` |
|---|---|---|
| `SearchSimilar` object centroid (`:238`) | `"SearchSimilar"` | `"re-ranking without the centroid signal"` |
| `SearchChunks` parent centroid (`:382`) | `"SearchChunks"` | `"re-ranking without the centroid signal"` |
| `SearchChunks` chunk vectors (`:394-412`) | `"SearchChunks"` | `"selecting without the diversity signal"` |

`rpcName` and `consequence` are compile-time constants at every call site, so neither needs
`SanitizeForLog`; the collection and vector names keep theirs (A3).

The helper's `if (ids.Count == 0) return EmptyVectors;` guard covers all three sites, which subsumes
the chunk-vector site's current `if (results.Count > 0)` wrapper — that wrapper is replaced by §2's
gate rather than kept. `EmptyCentroids` is renamed `EmptyVectors` in the same change; it is
referenced only within this file (A2), and leaving a centroid-specific name as the return of a
signal-neutral helper is the half-renamed state that confuses readers.

### 2. Gate the chunk-vector retrieve on whether diversification can act

The chunk-vector retrieve currently fires whenever `results.Count > 0`. Guard it instead with:

```csharp
if (results.Count > 1 && topK > 1)
```

**Why exactly this condition.** A diversity vector is read only inside `Diversify`'s selection loop,
which runs only while `selected.Count < take` *after* the unconditional `Select(0)` — so only when
`take = Math.Min(topK, ranked.Count) >= 2`. That requires both `topK >= 2` and a pool of at least 2
(A7, A8). Below either threshold the retrieve costs a round trip over up to `4 × top_k` ids and
cannot change the returned set or its order.

**The gate must not be `pool > topK`.** MMR reorders even when the pool is exactly `topK` — reordering
is the point, not merely trimming — so the retrieve still matters there.

The gate is call-site-specific and stays at the `SearchChunks` chunk-vector site. It does **not**
move into the shared helper: the two centroid retrieves feed `Rerank`, which affects the fused score
at every `top_k`, including 1.

### 3. Enforce `ComputeCentroid`'s zero-magnitude invariant at the caller

`ComputeCentroid` (`IntelligenceStoreConsumer.cs:368-389`) divides every chunk vector by its own
magnitude with no zero check. A zero-magnitude vector yields `x / 0` → `NaN` across all dimensions,
and that `NaN` centroid is stored. Part 3 then computes a `NaN` fused score for that document and
`OrderByDescending` sinks it to the bottom of every result set — silently, with nothing logged.

The guard goes at the **caller** (`:209`), not inside `ComputeCentroid`. This was established during
verification rather than assumed: `ComputeCentroid` is `static` while `logger` is an instance member
(`:38`), and the context the warning must name — `ev.TypeName`, `ev.Key`, the field — exists only at
the call site (A16). A guard inside the function could not produce a useful log line.

So the caller partitions the freshly embedded chunk vectors **for the centroid computation only**:

- Vectors with zero magnitude are excluded and logged once per event as a warning naming type, key,
  field, and how many were dropped.
- `ComputeCentroid` is called with the survivors, and its result stored as today.
- The chunk-point write loop (`:212-243`) is unchanged: every chunk is still upserted with its own
  `<property>_vector`, degenerate ones included. Part 4b already handles a degenerate chunk vector on
  the read side — `CosineSimilarity` returns `NaN` and the diversifier treats it as absent — so
  dropping the chunk point would remove a retrievable passage for no gain.
- If **no** vector survives, no `<field>_centroid` entry is added to the `centroids` dictionary at
  all. An absent centroid is a state part 3 already handles — `RetrieveNamedVectorAsync` omits points
  lacking the named vector, and `ResultReranker` treats a null centroid as an absent signal
  (A13) — whereas a `NaN` centroid is not. The dictionary tolerates the omission and an empty
  dictionary skips the write entirely (A11).

`ComputeCentroid` keeps its `float[]` return and its `internal static` shape. It gains only a
corrected comment: the existing text at `:361-362` justifies the missing guard with "no caller passes
a zero vector (blank text is skipped upstream, and `SplitIntoChunks` always yields at least one chunk
from non-blank text)". That reasoning is about the input **text**; the failure mode is the embedding
**model** returning a zero vector for non-blank text, which it never covered. After this change the
claim becomes true and enforced at the boundary rather than assumed.

**Chosen over the alternative deliberately.** Returning `float[]?` with an `out int skippedDegenerate`
was considered and rejected: it adds an out-parameter to a pure function, and the nullable return
makes the two existing `ComputeCentroid` tests emit CS8602 where they index `result[0]`, requiring
null-assertion churn for no behavioural gain (A15, A19).

### 4. The absent-vector bias is documented, not fixed

A candidate whose diversity vector is **missing** contributes no similarity term and therefore takes
no penalty, scoring `λ · fused(c)`. A candidate whose vector is **present** and similar to something
already selected is penalised. So in a partially-populated chunks collection, the un-vectorised
points are quietly advantaged.

This is not fixed, and the reason is structural rather than a matter of effort. Removing the bias
means either substituting a value for the absent signal — which contradicts part 4b's core rule and
breaks its bit-for-bit `Take(topK)` degradation guarantee — or discarding all diversity data whenever
any vector is missing, which throws away a good signal over one bad point. Both cures are worse than
the disease at the frequency this occurs.

The only change is documentation: a short comment at each RPC's ranked-id-to-diversity-vector pairing
site (`ObjectSearchGrpcService.cs:255-260` and `:428-434`, A17), where a reader would otherwise wonder
why a missing vector is a free pass, plus the Known-issues entry below.

## Testing

- **§1** — covered by the existing centroid-degrade and chunk-vector-degrade tests; no test asserts
  the log text (A4), so the rename and parameterization need no new coverage.
- **§2** — at `top_k = 1`, no chunk-vector retrieve is issued while the parent-centroid retrieve
  still is. No existing test uses `TopK = 1` (A9), so this is new coverage rather than a change to
  an existing assertion.
- **§3** — the caller drops a zero-magnitude vector and still stores a centroid from the survivors;
  an all-degenerate field stores no centroid entry at all (while still writing its chunk points); and the two existing `ComputeCentroid`
  tests continue to pass unchanged, since neither supplies a degenerate vector (A14).

## Verified assumptions

Verified against the codebase at `main@d6f4ff2` before this spec was written. Listed cold against the
design, then checked — the enumeration is what surfaced A16, which changed §3's shape.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `FetchCentroidsAsync` has exactly two call sites plus its declaration, and no test references it by name | `grep -rn "FetchCentroidsAsync"` → `ObjectSearchGrpcService.cs:238`, `:382`, `:690` only |
| A2 | `EmptyCentroids` is referenced only within `ObjectSearchGrpcService.cs`, so renaming it is file-local | `grep -rn "EmptyCentroids"` → 7 hits, all in that file (`:235`, `:387`, `:394`, `:410`, `:682`, `:693`, `:707`) |
| A3 | Parameterizing the log's trailing clause is safe with respect to the repo's log-forging protection | `LoggingExtensions.cs:5` — `SanitizeForLog` is `value.ReplaceLineEndings("")`, applied to non-constant args. `rpcName`/`consequence` are compile-time constants at every call site and are passed as structured arguments, not concatenated into the template |
| A4 | No test asserts the exact retrieve-failure log text | `grep -rn "retrieve failed\|without the centroid signal"` returns only the two production sites (`:407`, `:704-705`) |
| A5 | The chunk-vector site's `results.Count > 0` wrapper is subsumed by the helper's `ids.Count == 0` guard | `:693` returns `EmptyCentroids` for an empty id list; an empty `results` yields an empty id list |
| A6 | `topK` is in scope at the chunk-vector site and is the same value passed to `Diversify` | Declared `ObjectSearchGrpcService.cs:347`; retrieve at `:400`; `Diversify(..., (int)topK)` at `:435` |
| A7 | At `topK == 1` the diversifier's **output** is unaffected by whether diversity vectors are present | `ResultDiversifier.cs` — `take = Math.Min(topK, ranked.Count)`; `Select(0)` runs unconditionally, then `while (selected.Count < take)` never executes at `take == 1`. `Select(0)` does compute similarities, but nothing reads them, so the returned list is `[ranked[0]]` either way |
| A8 | At a pool of 1 the output is that single candidate regardless of vectors | Same code: `take = Math.Min(topK, 1) = 1`, so the loop never runs |
| A9 | No existing test would break on the new gate | `TopK` values across the Api test file are 2, 3 and 5 only — none is 1 |
| A10 | `ComputeCentroid` has exactly one production caller plus two tests | `grep -rn "ComputeCentroid"` → `IntelligenceStoreConsumer.cs:209` (call), `:368` (declaration), `IntelligenceStoreConsumerTests.cs:1405`, `:1416` |
| A11 | The `centroids` dictionary tolerates an omitted key, and an empty dictionary skips the write | `IntelligenceStoreConsumer.cs:157` declares `new Dictionary<string, float[]>()`; `:210` assigns by key; `:267` gates the entire write on `centroids.Count > 0` |
| A12 | Omitting a `<field>_centroid` entry does not write a malformed point | The write path is `UpdateNamedVectorsAsync(collectionName, pointId, centroids)` (`:282`, `:314`) — a *named-vector update*, so an omitted key simply leaves that vector unwritten rather than producing a partial point |
| A13 | Part 3 handles a wholly absent centroid | `IntelligenceVectorService.cs:166-168` skips points whose named vector is missing or empty, so the id is absent from the map; `ResultReranker.cs:20` computes `hasCentroid = candidate.Centroid is not null && …` and renormalizes over present signals |
| A14 | The two existing `ComputeCentroid` tests still pass unchanged | `IntelligenceStoreConsumerTests.cs:1400-1421` — fixtures are `[[3,4],[1,0]]` and `[[3,4]]`; neither is degenerate, so no vector is filtered and the divisor is unchanged |
| A15 | A `float[]?` return would produce nullable warnings in those tests | Both projects set `<Nullable>enable</Nullable>` (`Iverson.Api.csproj:5`, `Iverson.Api.Tests.csproj:5`); the tests index `result[0]` directly, which warns CS8602 on a nullable array. This is one reason §3 keeps the non-null return |
| A16 | **Shape-changing.** The skip warning cannot be logged inside `ComputeCentroid` | `ComputeCentroid` is `internal static` (`:368`) while `logger` is a primary-constructor instance member (`:38`), and the type/key context the warning needs lives at the caller (`ev.TypeName`, `ev.Key`). The design originally placed the log inside the function; verification moved it to the caller and, with it, the whole guard |
| A17 | Each RPC has a ranked-id-to-diversity-vector pairing site suitable for the §4 comment | `ObjectSearchGrpcService.cs:255-260` (`centroids.TryGetValue`) and `:428-434` (`chunkVectors.TryGetValue`) |
| A18 | Both suites pass on `main@d6f4ff2` as a baseline | Run directly on the merged tree: `Iverson.Vector.Tests` 120/120, `Iverson.Api.Tests` 574/574, build 0 errors |
| A19 | No warnings-as-errors setting would turn a nullable warning into a build break | Neither `Iverson.Api.csproj` nor `Iverson.Api.Tests.csproj` sets `TreatWarningsAsErrors` or `WarningsAsErrors`; no `Directory.Build.props` exists. Recorded because it bounds A15's consequence to warnings rather than failures |
| A20 | *(Recurrence)* Every symbol this design renames or changes has had its consumers swept: `FetchCentroidsAsync` (A1), `EmptyCentroids` (A2), `ComputeCentroid` (A10) | Each swept by name across all `.cs` outside `obj/`. Method and field references are reliably found by name grep — unlike the constructor case that bit part 4b, where target-typed `new(` hid a call site from a `new TypeName(` pattern. No symbol here is a constructor |
| A21 | `ComputeCentroid` throws on an empty input list, so the caller must check before calling rather than call-then-check | `IntelligenceStoreConsumer.cs:370` opens with `var dims = vectors[0].Length;`, which raises `IndexOutOfRangeException` on an empty list |
| A22 | The pool `Diversify` sees has the same count as `results`, so §2's gate tests the right quantity | `ObjectSearchGrpcService.cs:417-425` builds `candidates` one-to-one from `results`; `Rerank` returns one `RerankedResult` per candidate; `.Select(...).ToList()` at `:428-433` preserves the count |

## Known issues

**The absent-vector bias is accepted by construction.** As §4 sets out, a candidate whose diversity
vector is missing takes no penalty and therefore outranks an otherwise-equal candidate that has a
vector and any positive similarity. In a partially-populated chunks collection this quietly favours
the un-vectorised points. Both available cures are worse: substituting a value contradicts part 4b's
"an absent signal is never replaced" rule and breaks its bit-for-bit `Take(topK)` guarantee, and
discarding all diversity whenever any vector is missing throws away a good signal over one bad point.
Ben was shown the three options and chose to document rather than code. Worth revisiting only if
partial chunk-vector maps are ever observed in production.

**No backfill for already-stored `NaN` centroids.** §3 stops new degenerate centroids from being
written but does nothing about any already in Qdrant. For one to exist, the embedding model must have
returned an exact zero vector for non-blank text, so there is most likely nothing to backfill — but
this could not be verified without querying a live Qdrant instance, so it is stated as a limit rather
than claimed as clean. If a document is ever found sitting at the bottom of every result set for no
apparent reason, this is the first thing to check.

## Not in this spec

- **A threshold-based partial-map policy.** Considered for §4 and rejected: it introduces a tuning
  constant with nothing to calibrate it against, on a problem that has not been observed.
- **Making `ComputeCentroid` defensive in its own right.** The invariant is enforced at the caller
  (§3); the function stays pure and total over the input its comment now accurately describes.
- **Any change to the diversifier itself.** `λ`, the incremental `maxSim`, the presence/magnitude
  split and the seeded argmax are all as part 4b left them.
- **Cross-corpus cluster centroids.** Still deferred, as in 4a and 4b.
