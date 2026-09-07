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

Where the setting applies, the two names swap roles and nothing else moves:

| `SimilarRetrievalVector` | retrieval vector (`:221` → `:246`) | secondary vector (`:265`, fused at WCentroid) |
|---|---|---|
| `head` (default, today's behaviour) | `<prop>_vector` | `<prop>_centroid` |
| `centroid` | `<prop>_centroid` | `<prop>_vector` |

Same single Qdrant round trip. Same `RetrieveVectorsOrDegradeAsync` degradation path — it already
takes the vector name as a parameter (`:759-760`). Same fusion, decay, `LambdaSimilar` and
`Take(topK)`. The `rerankIsIdentity` over-fetch (`:238-239`) keys off `centroidPossible` and
`decayField`, both unchanged by the swap, and stays correct: whenever the swap applies,
`centroidPossible` is true and the over-fetch is 4× either way.

`RerankCandidate.Centroid` becomes "the secondary vector" rather than literally a centroid. The
field is a bare `float[]?` and `ResultReranker` only takes its cosine against the query, so nothing
breaks — but the name misleads. **It is renamed only on a PASS**, to avoid churning the reranker and
its tests for an arm that may be reverted.

### A property worth stating: the swap is a no-op for single-chunk documents

`ingest.py`'s `compute_centroid` (`:267-275`) L2-normalizes each chunk vector and then averages, so a
document with exactly one chunk has a centroid equal to the unit-normalized head vector. Under
cosine distance that ranks identically to the head vector. The production writer takes the same
shape (`IntelligenceStoreConsumer.cs:303`).

The change therefore can only affect documents with two or more chunks. Measured on this campaign's
own `sci-2048` corpus, **4,542 of 5,183 documents (87.6 %) have a body of 2,048 characters or fewer**
and so yield exactly one chunk; the remaining 641 carry 2,045 of the arm's 6,587 chunks. That is why
rule 7.3's SciFact comparison came out null — for seven documents in eight the two vectors it
compared were the same vector — and it means SciFact's role in this gate is a guard on the mixed
regime rather than a second chance at the win.

(The multivector spec records 81 % / 6,219 chunks for this window. That figure disagrees with this
campaign's measured 6,587 and is not used here; the numbers above are computed from the corpus and
sidecar this gate will actually restore.)

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

**On PASS** — hard-code centroid retrieval for chunked properties, delete `SimilarRetrievalVector`
and its compose entry, rename `RerankCandidate.Centroid` to reflect that it holds the secondary
vector, and record the verdict in `docs/plans/2026-09-GATE-similar-centroid.md`.

**On FAIL** — delete the setting and its compose entry, keep head retrieval, record the verdict.

The setting does not survive this spec under either outcome.

## 7. Testing

- Vector-name selection across three cases: chunked + `centroid` → retrieves `_centroid` and fetches
  `_vector`; chunked + `head` → today's names; not chunked → `_vector` regardless of the setting.
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
| A3 | Compose binds `VectorRanking__*` | `docker-compose.yml:444-445`; confirmed live by `docker inspect` during the Tier 1 λ sweep |
| A4 | Retrieval name is `<prop>.ToSnakeCase() + "_vector"`, one `SearchNamedAsync` | `:221`, `:246` |
| A5 | `centroidPossible` is exactly "embedded AND chunked" | `:228-229` over `schema.ChunkFields` |
| A6 | `RetrieveVectorsOrDegradeAsync` takes the vector name as a parameter | `:759-760` |
| A7 | `ResultReranker` treats the second vector generically | `RerankCandidate.cs:6` is `float[]? Centroid`; `ResultReranker.cs` only calls `CosineSimilarity` on it |
| A8 | The over-fetch logic stays correct under the swap | `:238-239` keys off `centroidPossible` and `decayField`, neither of which the swap changes |
| A9 | `<prop>_vector` exists wherever the swap applies | `IntelligenceStoreConsumer.cs:139` writes it for every `[IversonEmbedding]` property; the swap requires `vectorDesc` from `schema.VectorFields`, so both vectors are present |
| A10 | `<prop>_centroid` is on the object collection, same dimension | `IntelligenceStoreConsumer.cs:303`; `ingest.py:807` declares both at the probed dimension |
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

- **SciFact is largely a null test.** Single-chunk documents have a centroid equal to their
  normalized head vector, so the swap cannot change their ranking, and 87.6 % of the `sci-2048`
  corpus is single-chunk. Only 641 of 5,183 documents can move at all. Ben chose (2026-09-07) to
  keep the arm anyway: it is the only near-single-chunk guard available, its multi-chunk minority
  still exercises the change, and it costs about eight minutes across both settings.
- **The validity check cannot diagnose a mismatch**, only confirm a match — see A13 and §4.
