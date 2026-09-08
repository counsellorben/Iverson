# SearchSimilar Centroid Retrieval — Design

Written 2026-09-07 against local `main` `c27eb98`.

Input: rule 7.3 of `docs/specs/2026-09-06-tier1-retrieval-defaults-design.md`, whose verdict in
`docs/plans/2026-09-GATE-tier1-defaults.md` recommends this follow-up.

## 1. Why

Rule 7.3 measured raw `body_centroid` against raw `body_vector` on three arms. The centroid won
significantly on both FreshStack arms — nDCG@10 +0.0251 / +0.0290 and R@50 +0.0576 / +0.0598, all
Holm p_adj ≤ 0.0006 — and was statistically indistinguishable on SciFact (CI lower bounds −0.0163
and −0.0168, both clearing the −0.02 floor).

**The current architecture cannot capture that result, and the reason is structural rather than a
matter of tuning.** `SearchSimilar` retrieves candidates with one `SearchNamedAsync` call against
`<property>_vector` (`ObjectSearchGrpcService.cs:221`, `:246`), *then* fetches
`<property>_centroid` for the ids it already has (`:265`) and fuses at WBase 0.45 / WCentroid 0.45 /
WDecay 0.10. The centroid is a **re-ranking signal over a candidate set the head vector chose**. It
can reorder; it can never add. R@50 +0.058 at depth 50 is precisely a set of documents the head
vector never returned, so no assignment of fusion weights reaches them.

The question this spec answers is therefore narrower than "is the centroid better": it is whether
making the centroid the **retrieval** vector improves `SearchSimilar` on the real fused path, which
rule 7.3 did not measure.

## 2. Scope

`SearchSimilar` only, and only for a property that is **both** embedded and chunked — the condition
`centroidPossible` already computes at `ObjectSearchGrpcService.cs:228-229` from `schema.ChunkFields`.
A property with no chunks has no `<property>_centroid` named vector and is untouched.

### Out of scope

- `SearchChunks`. It retrieves from the chunks collection (`:402`, `:417`) and separately fuses the
  **object** collection's `<property>_centroid` (`:441`, per the comment at `:429`). Its retrieval
  vector does not move. Naming all four derivation sites here is deliberate: two of them mention a
  centroid and only one of those is in scope.
- The fusion weights WBase / WCentroid / WDecay.
- The two-query "union head and centroid candidate sets" shape. It would capture recall from both
  vectors but adds a Qdrant round trip per search, and its benefit over centroid-alone is entirely
  unmeasured. Not proposed here.
- Re-ingest. The three arms restore from existing snapshots.

## 3. The change

One **temporary** setting on `VectorRankingOptions`, removed by this spec's own consequences:

```csharp
// Temporary, for the gate in docs/specs/2026-09-07-similar-centroid-retrieval-design.md §5.
// "head" | "centroid" — which named vector SearchSimilar RETRIEVES by for a chunked property.
// Deleted once the gate rules; the winner is hard-coded.
public string SimilarRetrievalVector { get; set; } = "head";
```

`AddVectorRanking` (`ServiceCollectionExtensions.cs:55`) rejects any other value, alongside the
existing obsolete-key, finiteness, non-negativity and range checks. The existing checks are all
numeric, so this needs its own comparison rather than riding the finiteness guard.

Compose gains `- VectorRanking__SimilarRetrievalVector=${VECTOR_RANKING_SIMILAR_RETRIEVAL:-head}`
next to the two λ entries.

Where the setting applies, the retrieval and fusion names swap roles. The vector fetched at `:265`
has **two** consumers, not one — it is the fusion input at `:274` and MMR's diversity vector at
`:288` — so the three roles are stated separately:

| `SimilarRetrievalVector` | retrieval vector (`:221` → `:246`) | secondary vector (`:265`, fused at WCentroid) | MMR diversity vector (`:288`) |
|---|---|---|---|
| `head` (default, today's behaviour) | `<prop>_vector` | `<prop>_centroid` | `<prop>_centroid` |
| `centroid` | `<prop>_centroid` | `<prop>_vector` | `<prop>_centroid` |

**The diversity vector stays the centroid under both settings.** Letting it swap would diversify by
the one representation Qdrant did *not* match against, contradicting the reason `SearchChunks` gives
for its own diversity vector at `:445-447` ("the same representation Qdrant matched the query
against"), and it would diversify by the representation this spec argues is the weaker of the two.

`LambdaSimilar` is 1.00 (`VectorRankingOptions.cs:24`), and at λ = 1.00 `Mmr` drops the similarity
term entirely (`ResultDiversifier.cs:82-96`), so the diversity vector is never read and holding it
fixed costs nothing in this campaign. It is not free in general: under `centroid`, retrieval no
longer yields the centroid as a fetched vector, so a configured λ < 1 needs a second retrieve. The
single-round-trip claim below is therefore conditional on λ = 1.00.

One Qdrant round trip at λ = 1.00; two under `centroid` at λ < 1. Same
`RetrieveVectorsOrDegradeAsync` degradation path for the *secondary* fetch — it already takes the
vector name as a parameter (`:759-760`) — but see the retrieval asymmetry below, which that
parameterisation does not cover. Same fusion, decay and `Take(topK)`. The `rerankIsIdentity`
over-fetch (`:238-239`) keys off `centroidPossible` and `decayField`, both unchanged by the swap,
and stays correct: whenever the swap applies, `centroidPossible` is true and the over-fetch is 4×
either way.

### Retrieval asymmetry: a missing centroid excludes rather than degrades

Today a point carrying `<prop>_vector` with no `<prop>_centroid` is retrieved normally and merely
loses the centroid term — the null branches at `:275`, `:288` and `ResultReranker.cs:21` exist for
exactly this state. Under `centroid` the centroid becomes the retrieval vector, and a Qdrant point
that does not carry a named vector is not in that vector's index, so it cannot be returned at all.
The degradation path cannot rescue it, because the point never enters `results`.

The writer produces that state deliberately: no centroid key when every chunk vector is
zero-magnitude (`IntelligenceStoreConsumer.cs:302`), a silent drop on the documented
delete-then-recreate race (`:365`, comment at `:361-364`), and a window between the head-vector
upsert (`:161`) and the centroid write (`:380`) even on the happy path.

**Accepted as a known consequence** (Ben, 2026-09-07), recorded in Known issues and in §6's PASS
branch. The alternatives were weighed and rejected: making the centroid write unconditional
reintroduces the NaN centroid that the `:302` guard exists to prevent, and falling back to head
retrieval is not detectable in one round trip, since an excluded point leaves no trace to fall back
from. The benchmark corpora contain no points in this state (`ingest.py:623-637` writes both vectors
in one upsert), so **the gate cannot exercise it** — it is a design decision, not a measured one.

`RerankCandidate.Centroid` becomes "the secondary vector" rather than literally a centroid. The
field is a bare `float[]?` and `ResultReranker` only takes its cosine against the query, so nothing
breaks — but the name misleads. **It is renamed only on a PASS**, to avoid churning the reranker and
its tests for an arm that may be reverted.

### A property worth stating: the swap is a no-op for single-chunk documents

`ingest.py`'s `compute_centroid` (`:267-275`) L2-normalizes each chunk vector and then averages, so a
document with exactly one chunk has a centroid equal to the unit-normalized head vector. Under
cosine distance that ranks identically to the head vector. The production writer takes the same
shape (`IntelligenceStoreConsumer.cs:485-505`).

The change therefore can only affect documents with two or more chunks. The single-chunk rule is
`len(body) ≤ step`, **not** `≤ max_chars`: `split_into_chunks` advances by `start += step`
(`ingest.py:240-258`), so at 2048/1792 a body of 1,793–2,048 characters yields two chunks, and
`ingest.py:604` uses `len(body) <= step` as its own single-chunk test.

Replaying that chunker over this campaign's `sci-2048` corpus reproduces the arm's sidecar exactly
(6,587 chunks; `embeds_saved` 3,820, which fires once per single-chunk document): **3,820 of 5,183
documents (73.7 %) are single-chunk**, and the remaining **1,363 carry 2,767** of the arm's chunks.
Roughly three documents in four cannot move — so rule 7.3's SciFact null is largely structural, and
SciFact's role here is a guard on the mixed regime rather than a second chance at the win. Its
movable minority is nonetheless substantial, which is part of why the arm is worth running.

## 4. The campaign

Six query runs from **one binary**, three corpora × two settings. Restoring a snapshot repopulates
both named vectors and all five payload indexes, so no re-ingest is needed.

| Arm | Restore from | Multiplier | Queries | Per run |
|---|---|---|---|---|
| `fs-2048` | `freshstack-2048-qdrant-snapshots/` | 11 | 672 | ~31 min |
| `fs-512` | `freshstack-512-qdrant-snapshots/` | 11 | 672 | ~31 min |
| `sci-2048` | `scifact-2048-qdrant-snapshots/` | 5 | 300 | ~4 min |

Roughly 75 minutes of query time plus restores. Each arm: restore both collections, clear the
`BenchmarkDocument` schema row, recreate `iverson-api` with the setting, run `benchmark-query`, which
reaches `SearchSimilar` at `BenchmarkQueryScenario.cs:353`. `LambdaSimilar` stays at its new default
of 1.00 throughout; only `SimilarRetrievalVector` varies.

**Same-build control.** Both arms come from one image, so the comparison is paired under identical
code and `report.py` prints no `BUILD MISMATCH`. This is the reason for the setting: a swap-only
binary compared against the recorded runs would be a cross-build comparison, which
`project-benchmark-run-integrity`'s guard exists to flag.

**Validity check, not a gate condition.** The new binary's `head` runs should reproduce the recorded
`fs-2048-l100.similar.trec` and `fs-512-l100.similar.trec` from build `9714c660b365fad1`, which were
made at `LambdaSimilar=1.00`. If they match, the setting is inert on the control path. If they do
not, the check is **non-diagnostic**: `meta.json` records the build composite and
`chunkBudgetMultiplier` but not the λ values, so a mismatch cannot distinguish a leaky setting from
a λ difference in the recorded runs. Record the outcome either way; do not gate on it.

## 5. The rule

One `report.py --baseline <head run> --run <centroid run>` invocation per arm, so the Holm family is
one comparison per measure per arm — the same shape rule 7.3 used, applied to the fused path so the
two results are directly comparable.

> **PASS** if centroid-retrieval beats head-retrieval on **both** nDCG@10 and R@50 with Holm
> p_adj < 0.05 on **both** FreshStack arms, **and** SciFact's 95 % CI lower bounds on both measures
> exceed −0.02.
>
> Anything else is a **FAIL**.

A FAIL is informative rather than merely negative: it would mean the 45 % head-vector term in the
fusion masks a gain the raw vectors show clearly, which is an argument about the weights, not about
the centroid.

## 6. Consequences

**On PASS** — the retrieval asymmetry in §3 ships with the change and is accepted, not fixed: a
chunked point with no centroid becomes unreachable through `SearchSimilar`. Then hard-code centroid
retrieval for chunked properties, delete `SimilarRetrievalVector`
and its compose entry, rename `RerankCandidate.Centroid` to reflect that it holds the secondary
vector, and record the verdict in `docs/plans/2026-09-GATE-similar-centroid.md`.

**On FAIL** — delete the setting and its compose entry, keep head retrieval, record the verdict.

The setting does not survive this spec under either outcome.

## 7. Testing

- Vector-name selection across three cases: chunked + `centroid` → retrieves `_centroid` and fetches
  `_vector`; chunked + `head` → today's names; not chunked → `_vector` regardless of the setting.
- A fourth case pinning the diversity vector: under **both** settings the named vector reaching the
  diversifier at `:288` is `<prop>_centroid`. None of the three cases above would catch a regression
  here, because they assert on the retrieval and fusion names only.
- `AddVectorRanking` throws on an unrecognised `SimilarRetrievalVector`, following the existing
  rejection tests at `VectorRankingOptionsTests.cs:71-137`.
- The defaults test asserts `head`.
- Existing `SearchSimilar` suites stay green under the default. They construct options with object
  initializers (`ObjectSearchGrpcServiceTests.cs:79`, `ObjectSearchVectorIntegrationTests.cs:94`,
  `DocumentTemplateValidationTests.cs:338`), so a new defaulted property does not disturb them.

## Verified assumptions

Verified 2026-09-07 against `main` `c27eb98`.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `VectorRankingOptions` binds from section `VectorRanking` | `ServiceCollectionExtensions.cs:57-63` — `config.GetSection(...)`, `section.Bind(opts)` |
| A2 | `AddVectorRanking` has a startup-validation site | `:58-95` — obsolete-key, finiteness, non-negativity, sum>0, two range checks. All numeric; a string needs its own check |
| A3 | Compose binds `VectorRanking__*` | `docker-compose.yml:445-446`; confirmed live by `docker inspect` during the Tier 1 λ sweep |
| A4 | Retrieval name is `<prop>.ToSnakeCase() + "_vector"`, one `SearchNamedAsync` | `:221`, `:246` |
| A5 | `centroidPossible` is exactly "embedded AND chunked" | `:228-229` over `schema.ChunkFields` |
| A6 | `RetrieveVectorsOrDegradeAsync` takes the vector name as a parameter | `:759-760` |
| A7 | `ResultReranker` treats the second vector generically | `IResultReranker.cs:3-7` declares `float[]? Centroid`; `ResultReranker.cs:21,41` only length-checks it and takes its cosine |
| A8 | The over-fetch logic stays correct under the swap | `:238-239` keys off `centroidPossible` and `decayField`, neither of which the swap changes |
| A9 | `<prop>_vector` exists wherever the swap applies | `IntelligenceStoreConsumer.cs:139` writes it for every `[IversonEmbedding]` property; the swap requires `vectorDesc` from `schema.VectorFields`, so both vectors are present |
| A10 | `<prop>_centroid` is on the object collection, same dimension | `IntelligenceStoreConsumer.cs:303` (call site), normalise-then-average at `:485-505`; `ingest.py:807` declares both at the probed dimension |
| A11 | The benchmark collections really carry `body_centroid` | `ingest.py:807` creates both named vectors, `:625` writes the centroid — the benchmark corpus is ingested by `ingest.py`, not the consumer |
| A12 | `benchmark-query` reaches `SearchSimilar` | `BenchmarkQueryScenario.cs:353` |
| A13 | **Partly failed.** Recorded λ=1.00 runs are comparable, but `meta.json` does not record λ | Grep for `lambda` in `BenchmarkQueryScenario.cs` returns nothing; meta carries `composite` and `chunkBudgetMultiplier` only. Handled in §4 — the control is re-run, and the validity check is non-diagnostic on mismatch |
| A14 | `report.py --baseline/--run` gives paired stats, Holm and 95 % CIs | Produced every rule report in the Tier 1 campaign |
| A15 | Snapshot restore repopulates named vectors and payload indexes | The live collection is itself a Task 7 restore: `body_centroid` + `body_vector`, five payload indexes, 5,183 points |
| A16 | All three snapshot directories exist | Each holds two `.snapshot` files and a `RESTORE.md` |
| A17 | Nothing else breaks when a non-`double` property is added | Only `ObjectSearchGrpcService.cs:42,47` reads the options outside `Iverson.Vector` |
| A18 | `SearchSimilar` is the only object-collection retrieval site | Two `SearchNamedAsync` callers: `:246` (object) and `:417` (chunks) |
| A19 | **Failed as stated.** Four name-derivation sites, not two | `:221`, `:265` (SearchSimilar) and `:402`, `:441` (SearchChunks — its secondary reads the **object** centroid). Only the first two move; §2 records this |
| A20 | Existing tests survive a new defaulted property | Object initializers at `ObjectSearchGrpcServiceTests.cs:79`, `ObjectSearchVectorIntegrationTests.cs:94`, `DocumentTemplateValidationTests.cs:338` |
| A21 | A rejection-test pattern exists to follow | `VectorRankingOptionsTests.cs:71-137` |

## Known issues

- **SciFact is partly a null test.** Single-chunk documents have a centroid equal to their
  normalized head vector, so the swap cannot change their ranking, and 73.7 % of the `sci-2048`
  corpus is single-chunk. 1,363 of 5,183 documents can move. Ben chose (2026-09-07) to keep the arm:
  it is the only near-single-chunk guard available, its multi-chunk minority is substantial, and it
  costs about eight minutes across both settings.
- **`SearchSimilar` under `centroid` cannot return a point that has a head vector but no centroid** —
  it is excluded from retrieval, not degraded. Ben accepted this (2026-09-07); see §3's retrieval
  asymmetry for the writer paths that produce the state and the rejected alternatives. The benchmark
  corpora contain no such points, so the gate cannot measure the exposure.
- **The validity check cannot diagnose a mismatch**, only confirm a match — see A13 and §4.
