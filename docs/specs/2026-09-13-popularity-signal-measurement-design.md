# Measuring the relation-popularity signal — experiment design

**Status:** design, not yet executed. **Amended 2026-09-14** — adds Phase 0 (triage) and corrects
seven claims about the shipped code that were written before the time-decay feature merged.
**Measures:** the `relation-popularity-signal` feature, **merged to local main at `b32f870`**
(this spec originally described it as the unmerged branch `30734e3..8190a2a`).
**Source feature spec:** `docs/specs/2026-09-13-relation-popularity-signal-design.md`.

## The premise this corrects

The feature's own design spec records that `WPopularity`

> will never be measurable against BEIR/FreshStack — neither corpus has a real engagement
> relation

and ships the weight chosen-not-measured, in the same position `WDecay` already occupies. **That
claim is false for SciFact.**

SciFact's BEIR `_id` values are Semantic Scholar CorpusIds, not an internal sequence. They range
from 4,983 to 198,309,074, and they resolve: `CorpusId:4983` returns "Microstructural Development
of Human Newborn Cerebral White Matter Assessed in Vivo by Diffusion Tensor Magnetic Resonance
Imaging", which matches `corpus.jsonl`'s title for that id byte-for-byte. Every SciFact document
therefore carries a real, public, per-document count of child rows — its citations.

A citation is structurally what the feature counts: rows in a one-to-many relation whose cardinality
changes independently of the parent's own text. The saturating transform `count / (count + S)`
applies unchanged. SciFact already has relevance judgments, Qdrant snapshots on disk, and a scored
harness pointed at it.

This does not make the feature's caveat harmless in general — it was right that *engagement* data
exists in no corpus here. It was wrong that no corpus supplies the signal's *shape*.

## What this experiment decides

Whether `WPopularity` earns a non-zero default, and at what `SaturationPoint`.

Since the 2026-09-14 amendment it decides this in three stages rather than two: Phase 0 chooses
which arms are worth running, Phase 1 screens them offline, Phase 2 is the live gate. Only Phase 2
is pre-registered; Phases 0 and 1 are exploratory and select nothing.

It does not decide whether the consumer, the StarRocks aggregate, or the reconciliation worker
function against live infrastructure. Those are a separate question, deliberately out of scope
(see "Out of scope").

## Phase 0 — triage

**Added 2026-09-14.** Purpose: buy the cheap evidence that decides which arms Phase 1 runs, before
committing to a grid. Phase 0 is **exploratory and selects nothing**. It may inform the choice among
{lifetime arm only, lifetime + decayed arm, abandon} and may inform Phase 1's grid bounds. It may
**not** alter Phase 2 (see "Phase 2 is locked against Phase 0 and Phase 1").

### Data

Complete the count + date fetch for all 5,183 corpus ids. The requirement is that **every resolved
id carries a citation count and, where Semantic Scholar has one, a publication date.**

This is not satisfied by resuming the existing cache. `citations-countonly.json.bak` holds 1,288
counts and 112 unresolved ids from a run that predates date collection, and the fetcher's resume
predicate is `known = set(cache['counts']) | set(cache['unresolved'])` — so those 1,288 ids are
treated as done and would never acquire a date. Since the age-preserving null is Phase 1's *binding*
comparison, that would silently hollow out the age control for the first 27% of the corpus, in
corpus order rather than at random. Either add a second pass over ids that have a count but no date,
or discard the partial cache and refetch clean. Refetching clean is 52 batches — about 2.6 minutes
unauthenticated — and is the simpler of the two.

### Measurements

Exactly two, both already defined in Phase 1's list and not repeated here:

- **Measurement 1 — pool-matched AUC.** The honest version of the citation-count signal. The 0.6201
  already on record is against *random corpus documents*, which is a different and weaker claim.
- **Measurement 4a and 4b — the age control's descriptive half.** AUC of publication date alone,
  then within-age-stratum AUC of citation count.

Measurements 2, 3, 4c and 4d — the ceiling and both nulls, and citations-per-year — are **not** in
Phase 0. They belong to whichever arm structure Phase 0's read selects.

### Output

A Phase 0 section appended to `docs/plans/2026-09-GATE-relation-popularity.md`, per this spec's
existing convention. It reports the numbers, the completed-fetch corpus median that fixes `S`, and
the resolved/unresolved split. It draws no conclusion the arm-structure choice has not been made on.

## Phase 1 — offline screen

**Purpose:** decide whether Phase 2's server time is worth spending, before spending it. This
mirrors the two-phase structure the chunk-coverage and aspect-coverage work already used, and the
closure logic that settled ranked-changes item 15: when a measured ceiling sits below the
instrument's MDE, the line closes without a live run.

### The identity that makes it exact

`BenchmarkDocument` declares no date property, so `DecayFieldResolver.ResolveDecayField` returns
null and `hasDecay` is false. Its `Body` carries both `[IversonEmbedding]` and `[IversonChunk]`, so
the centroid is present. The shipped fusion for this corpus is therefore

```
fused_old = (0.45·base + 0.45·centroid) / 0.90
```

and adding popularity gives

```
fused_new = (0.45·base + 0.45·centroid + W·pop) / (0.90 + W)
          = (0.90·fused_old + W·pop) / (0.90 + W)
```

**The identity applies only to candidates that have a popularity value.** `ResultReranker.cs:53-57`
adds `WPopularity` to the weighted sum *and* to the weight total under the same `hasPopularity`
guard, so a candidate whose payload carries no count keeps the unchanged divisor:

```
pop present:  fused_new = (0.90·fused_old + W·pop) / (0.90 + W)
pop absent:   fused_new = fused_old
```

Phase 1's re-ranking must implement both branches. Substituting `pop = 0` for the absent set would
score those documents at 0.0 and punish them hard; substituting a median would pull them toward the
pool. Both model a server that does not exist. The unresolved set is ~6% by this spec's own estimate
and **8.0%** across the 1,288 ids cached so far — about four documents in a 50-document pool, large
enough to move nDCG@10.

The recorded score is a sufficient statistic: Phase 1 reproduces the server's re-ranking exactly
from the run file alone, with no components and no re-ingest. Scoring needs no running server; the
one prerequisite below needs Qdrant restored, but not the API.

`LambdaSimilar = 1.00` and `IResultDiversifier.Diversify`'s contract is that λ = 1.00 "reduces to
`Take(topK)` exactly" (`IResultDiversifier.cs:13`, and `ResultDiversifier.cs:74-75` collapses the
MMR objective to the fused score at that λ). So `.similar.trec`'s score column *is* the fused score,
and `SearchSimilar` is Phase 1's primary arm. `LambdaChunks = 0.70` leaves MMR active on the chunk
path, so `.chunks.trec`'s score column is *not* the fused score and the identity cannot be applied
to it. Re-deriving it needs the raw hit dump (`<label>.chunks.hits.tsv`, written by
`BenchmarkQueryScenario.cs:306`), and **no such dump exists for `sci-2048`** — the archived run
predates the writer, and the only three dumps in the corpora repository are FreshStack
chunk-coverage runs. Producing one means a live `benchmark-query` run, which contradicts this
phase's "scoring needs no running server".

**Phase 1 is therefore `SearchSimilar`-only.** This costs the screen nothing: the Gate is already
stated on `SearchSimilar`, and the chunks RPC re-enters at Phase 2 as the second member of the Holm
family.

### Prerequisite: confirm the divisor is uniform

The identity assumes every object point has a centroid, so every candidate divides by 0.90.
`ingest.py:620-625` writes `body_centroid` only when at least one chunk vector has non-zero
magnitude; a document failing that gets no centroid and is scored at `weightTotal = 0.45`.

Phase 1 therefore begins by restoring `scifact-2048-qdrant-snapshots` and counting object points
lacking `body_centroid`. If the count is zero the identity is uniform and exact. If not, those
points take the 0.45 divisor and the screen handles them separately. This is a two-minute check
that converts an assumption into a fact; it is not optional.

### Inputs

Run and qrel paths are relative to `/home/ben/repositories/iverson-benchmark-corpora/` and
`/home/ben/iverson-benchmark-data/` respectively — two different roots, which is worth stating
because searching the wrong one makes the control run look missing.

| Input | Source |
|---|---|
| Control ranking | `scifact-2048-2026-09-06/runs/sci-2048.similar.trec` — 300 queries, pool depth exactly 50, 4,441 distinct docids, all present in the corpus |
| Judgments | `scifact-full/qrels/test.tsv` — 300 queries, 339 binary judgments, 283 distinct relevant docs; query sets match the run exactly |
| Counts | `citations.json` — one rate-limited, resumable, disk-cached fetch of all 5,183 corpus ids, reused by both phases |

### Measurements

1. **Pool-matched AUC.** Within each query's own retrieved pool, the AUC of relevant vs.
   non-relevant documents by citation count. Against *random* corpus documents this is already
   0.6201 (n = 174 vs. 370; Hanley–McNeil SE 0.0264; 95% CI [0.569, 0.672]; z ≈ 4.55). The
   pool-matched version is the honest one: it compares each relevant paper against other papers on
   the same topic, which is what ranking actually reorders, and it is the measurement that speaks
   to the corpus-construction threat below.

2. **The ceiling.** Re-rank each query's pool under the identity across a grid of
   `(W, SaturationPoint)`, rescore nDCG@10, take the maximum.

3. **The shuffled null.** Permute citation counts across doc ids — marginal distribution preserved,
   doc↔count pairing destroyed — and repeat measurement 2. With 300 queries and a two-dimensional
   grid, some cell will look good by chance; the shuffled ceiling measures how much the grid can
   manufacture from noise.

4. **The age control.** Citation count is age-confounded: older papers accrue citations regardless
   of merit. A decay mechanism now ships (`b32f870`, see "The signal is a lifetime count at the
   shipped default" below) but it is **inert at the default `RecencyBoost = 0.0`**, and it decays
   engagement recency rather than document age in any case; `BenchmarkDocument` has no date
   property, so `WDecay` is inert too — **no shipped mechanism controls for document age**. Age
   must therefore be carried as a measured covariate:

   a. Report the AUC of publication date alone. If age does not separate relevant from
      non-relevant documents, the confound is not live and b/c/d are reported for completeness only.
   b. Report the **within-stratum AUC** of citation count, so the comparison is between papers of
      the same vintage.
   c. Re-run measurement 3 as an **age-preserving null**: permute counts *within* age strata,
      destroying the doc↔count pairing while preserving the age structure. A real ceiling that
      beats this null is not an age effect.

      **Stratum definition — fixed before the ceilings are computed (chosen 2026-09-14).** Fixed
      **5-year calendar strata** on `publicationDate`, falling back to `year` where
      `publicationDate` is absent. Documents with a resolved count and no date of either kind form
      **their own stratum, permuted among themselves**. Both the real and the null ceiling are
      computed over the identical full population, so the two are never compared across different
      document sets. The no-date stratum preserves no age structure by construction — it behaves as
      a plain shuffle for that subset — so **its size is reported alongside the ceilings**, not
      folded in silently. This is the choice the Phase 1 gate binds on; it is recorded here rather
      than decided after the ceilings are seen.
   d. Report the ceiling for **citations per year** (`count / age`) as an alternative signal
      alongside the raw count. `age` is derived from the same date field with the same
      `publicationDate` → `year` fallback as 4c; documents with no date of either kind have no
      denominator and are **excluded** from this ceiling, with the excluded count reported. This is the cheap age-de-confounded variant: it costs no extra
      fetching, and it is the closest thing to a rate that the data supports for free.

   `publicationDate` is fetched in the same API call as the count — month-granular, occasionally
   day-granular, and strictly finer than `year`, which is kept as the fallback for rows lacking it.
   Both are separately nullable: a resolved paper with no date still yields a usable count, so the
   covariate's n is smaller than the count's, and that gap is reported rather than papered over.

### Gate

Proceed to Phase 2 only if **real ceiling − age-preserving null ceiling > 0.0152** nDCG@10 on
`SearchSimilar` (SciFact's measured MDE at n = 300). The age-preserving null is the binding
comparison; the plain shuffled null is reported alongside it, and a real ceiling that clears the
plain null but not the age-preserving one is an **age** finding, not a popularity finding.

### The signal is a lifetime count at the shipped default

Worth stating explicitly, because it is unrecorded in the feature spec and it is what makes the age
control necessary:

- `PopularityFor` (`ObjectSearchGrpcService.cs:1040`) is
  `effective = count + RecencyBoost × D; effective / (effective + SaturationPoint)`, where `D` is a
  half-life sum over the monthly bucket series and the method takes a `DateTimeOffset now`. **At the
  shipped default `RecencyBoost = 0.0` this reduces exactly to `count / (count + SaturationPoint)`**
  — a pure function of the count — which is what keeps this experiment's design valid. The clock is
  read but its contribution is multiplied by zero.
- `ResultReranker` documents itself as "Pure and I/O-free… reads no clock"; decay arrives
  pre-computed.
- `PopularitySignalConsumer.cs:60` aggregates `AggregationDescriptor("count", AggregationKind.Count,
  Field: "")` — an unfiltered lifetime `COUNT(*)`, no date predicate, no rolling window.

So popularity is **all-time and monotonically non-decreasing**. `WDecay` is orthogonal and decays
the *parent's own age*, never the age of the child rows: even with both signals active, a
three-year-old engagement counts exactly as much as today's. In production that means a document
popular three years ago and dead since ranks identically to one accruing engagement now, at equal
totals. That is a legitimate choice — all-time popularity rather than trending — but it is a
product decision the feature spec never states.

### Why a decayed-popularity arm is not in this experiment

Per-citation dates **are** available: `/paper/{id}/citations?fields=year,publicationDate` returns
each citing paper's date at day granularity. So the data to build a time-windowed or decayed
popularity exists. It is nevertheless out of scope **for Phase 1**, for the reasons below in increasing order of
importance — though only the first still stands, and Phase 0 exists to decide whether to spend it:

1. **Cost — measured, and smaller than first estimated.** 5,183 papers at a *measured* mean of
   **985** citations (n = 1,288 cached) is ≈ 5.1M citing records against a per-paper,
   1,000-per-page endpoint. Because a paper with fewer than 1,000 citations still costs one
   request, the true floor is **8,797 requests**, not the 4,100 first estimated — roughly 2×.
   At the keyed pace of 1.2 s that is **≈ 2.9 h**, which is real but not prohibitive. This reason
   is weaker than originally written and no longer carries the decision on its own.
2. **The obvious shortcut is unsound.** Citations appear to be returned newest-first (a probe
   returned 2026-08, 2026-05, 2026-04, 2026-03, 2026-02 in order), which would allow early-stopping
   at a window cutoff. S2 does not document citation ordering as guaranteed, and a measurement must
   not rest on an ordering inferred from five rows.
3. ~~**The shipped code cannot express it.**~~ **No longer true — corrected `2026-09-14`.** This
   read on `PopularitySignalConsumer.cs:60` was written at 13:15 on 2026-09-13; the time-decay
   feature merged at 20:21 the same day (`b32f870`). The consumer now *also* issues a
   `DateHistogram` aggregation over the child timestamp column and writes a monthly bucket series
   alongside the count, and `PopularityFor` consumes it. A decayed arm **is** a configuration of
   this feature — `RecencyBoost` and `RecencyHalfLifeDays` — not a different one. The count
   aggregate itself remains an unfiltered lifetime `COUNT(*)`, so the *count* is still all-time;
   what changed is that a decayed term can now be fused with it. Cost (reason 1) is the only
   reason that survives, and Phase 0 decides whether to spend it.

**Recorded as a separate design question:** should relation popularity be time-windowed rather than
lifetime? This experiment does not answer it, but it establishes that the data to answer it exists
and that the current implementation forecloses it. Measurement 4d's citations-per-year ceiling is a
free partial read on whether rate carries more signal than total.

### A conservative bias, stated

`benchmark-query` requests `DocumentBudget = 50` and the server over-fetches 4×, so the server ranks
over 200 candidates while the run file records 50. Offline re-ranking cannot see a document promoted
from rank 51.

**This overstates the ceiling, not understates it.** (Corrected 2026-09-14; the paragraph previously
claimed the opposite.) 311 of the 339 judgments already sit inside the recorded 50, leaving at most
28 relevant documents for ranks 51-200 across all 300 queries — 45,000 candidate slots that are
therefore ~99.94% non-relevant. Live, those candidates compete for top-10 places against documents
offline re-ranking has already scored; almost every promotion displaces a better-judged document.
The live effect is smaller than the offline ceiling.

**The consequence for the gate.** The GO direction does not transfer: clearing 0.0152 offline is
necessary, not sufficient. The NO-GO direction does — a signal that cannot clear the bar on the pool
where 92% of the relevance lives will not be rescued by 150 slots that are 99.94% non-relevant.
Phase 1 is trustworthy as a screen that *stops* work, not as one that licenses it — which is what
"decide whether Phase 2's server time is worth spending" already asks of it.

## Phase 2 — live gate

### Step 0: reproduction check

Restore both snapshots, leave `PopularitySignal__Signals__*` **unset**, set
`VectorRanking__WPopularity = 0`, and run `benchmark-query`.

**`Signals` must not be configured until Step 1's relation exists.** `ValidateAtStartup` warns and
skips only when the ParentType is unregistered (`PopularitySignalOptions.cs:89-97`); a *registered*
`BenchmarkDocument` whose `Citations` relation is missing **throws** instead (`:104-108`), and that
runs at `Program.cs:449`, before `app.Run()` (`:550`). With `restart: unless-stopped` on
`iverson-api` the container then crash-loops, and `RegisterSchema` — the only way to add the
relation — is served by the process that cannot start. Configure `Signals` from Step 2 onward.
Leaving it unset costs this check nothing: `ValidateAtStartup` iterates `options.Signals` and does
nothing when it is empty, and `PopularityFor` returns `null` when no signal matches the type.
Because `W = 0` contributes zero to both the weighted sum and the weight total
(`ResultReranker.cs:53-57`), this must reproduce `sci-2048.similar.trec` **byte-identically**.

The archived run was built at composite `9714c660b365fad1`; this branch changes `Iverson.Vector`
and `Iverson.Api`, so the composite differs. A byte-identical run across different builds is the
evidence that nothing else moved. If it does not reproduce, the comparison is invalid and the gate
stops here.

### Step 1: schema

`PopularitySignalValidator.ValidateAtStartup` requires a real `OneToMany` relation on the parent and
a registered, engagement-eligible child (`PopularitySignalOptions.cs:89-110`). LoadTest therefore
gains a `BenchmarkCitation` entity (key, `BenchmarkDocumentId` FK per the naming rule, `OwnerId`),
and `BenchmarkDocument` gains `[OneToMany(typeof(BenchmarkCitation))] public List<BenchmarkCitation>
Citations`.

**No citation rows are ever written.** The validator checks schema shape, never data.

Two consequences to verify rather than assume:

- `StoreTargeting.cs:32` makes any entity holding a `OneToMany` engagement-ineligible, so
  `BenchmarkDocument` loses its StarRocks target. Harmless for this corpus — `ingest.py` writes
  Qdrant directly — but `benchmark-ingest` must be confirmed unaffected.
- `IngestContractTests.cs` builds a schema from `BenchmarkDocument`. The generated contract encodes
  no relations (its keys are `_generated`, `chunkWindow`, `distance`, `collectionNaming`,
  `embedding`, `golden`), so the change should be inert — but the test must be re-run, not assumed.

### Step 2: inject

A script joins `keymap.json` to `citations.json` and patches `citationsCount` onto each object
point. The point id is `key_to_ulong(uuid5(NAMESPACE_URL, f"beir:{docId}"))` — the corpus-name
component is `beir`, recovered by matching candidate names against the real keymap.

The payload key is `Relation.ToCamelCase() + "Count"` and is read back through `long.TryParse`
(`ObjectSearchGrpcService.cs:944-982`), so the script writes an integer.

### Step 3: arms

Control (`W = 0`) and the single pre-registered cell. Each arm is a server restart setting
`VectorRanking__WPopularity`, `PopularitySignal__SaturationPoint`, and — from Step 2 onward —
`PopularitySignal__Signals__0__ParentType` and `PopularitySignal__Signals__0__Relation`, then one
`benchmark-query` run, one TREC file per RPC.

**These keys must first be added to `Iverson.Server/docker-compose.yml`'s `iverson-api`
`environment:` list.** That list is an explicit allowlist with no `env_file`, so a host variable not
named there never enters the process and the arm silently runs as a byte-identical control, with no
error. Add them in the bare pass-through form already used for
`VectorRanking__SimilarViaChunksTypes__0` (`:451`) — a key with no `=`, which Compose resolves from
the host environment and omits when unset, so the control arm needs no special handling and falls
back to `WPopularity = 0.0`.

Confirm per arm with `docker inspect` on the running container **before** the `benchmark-query` run,
as prior gates did. An arm whose variables did not land is indistinguishable from a null result.

### Step 4: score

`report.py --baseline <control run>`, run **once per RPC**, with Holm applied by hand across the
two-RPC family (`holm_adjust`, `report.py:471`). It reports paired t, seeded sign-flip permutation,
95% CI, Cohen's d_z and MDE. Written up as `docs/plans/2026-09-GATE-relation-popularity.md`.

**Not `--pair`.** `--pair` routes through `check_pool` (`report.py:732`), which `sys.exit`s unless
each query's doc-id *set* is unchanged — a precondition written for rerankers that reorder a fixed
pool. A fused `WPopularity` weight is applied server-side across the over-fetched candidate set
*before* the top-50 is cut, so the recorded set changes on nearly every query and `--pair` refuses
the arm rather than scoring it. `check_pool` has exactly one call site (`:784`, inside
`run_pair_statistics`); the `--baseline` route (`run_paired_statistics`, `:630`) does not call it.

### Sidecar attribution

`benchmark-query`'s `*.meta.json` (`BenchmarkQueryScenario.cs:206-222`) records the build composite
and chunk-budget multiplier but not `λ` — ranked-changes item 16. The same gap now recurs for
`WPopularity` and `SaturationPoint`: without them, which arm produced a run file is unattributable
afterwards. Both fields are added as part of this work rather than repeating item 16's mistake with
new constants.

## Pre-registration

Fixed before any outcome is examined.

**Parameters.** `SaturationPoint` = **the corpus median citation count**, the BM25 saturation
convention. The number originally written here was `186`, estimated from a 400-id sample; the
1,288 ids cached by 2026-09-14 have a median of **235**. `S` is pre-registered as a *rule*, so the
exact value is computed once from the completed fetch and recorded in the gate doc before any
outcome is examined — executing the rule, not revising it. Setting `S` to the median places the median document at `pop = 0.5`, the steepest point
of the saturation curve, which is where the transform discriminates best among typical documents.
A larger `S` (≈300) yields a marginally wider IQR across a random corpus sample, but it buys that
width in the heavy tail, among documents that rank on citations alone rather than on the margin
where ordering actually changes. The median is the principled choice and the conventional one; the
tail-widening alternative is left to Phase 1's exploratory grid.

`W` is pre-registered as a **rule**, not a number:

```
W = 0.90 × σ_fused / σ_popularity
```

where both are within-pool standard deviations measured on the control run at `S = 186`. This
equalises the two terms' contribution to score variance. It is a deterministic function of data
fixed in advance, so it carries no selection bias, and it does not depend on an arithmetic estimate
made before the counts were in hand.

Phase 1's grid still runs and still gates, but it **selects nothing**; it is reported as
exploratory. If the grid's argmax differs materially from the pre-registered cell, that cell is
tested too, clearly labelled secondary.

**Primary endpoint.** nDCG@10 on `SearchSimilar`, treatment vs. control, paired permutation test.

**Bar.** PASS requires the nDCG@10 delta to be positive and Holm-significant across the two-RPC
family, **and** R@50's lower CI bound to stay above −0.02 — the rule shape the embedding-migration
gate used, so the bar is not invented for this experiment.

**Missing counts.** Roughly 6% of documents will not resolve (the clean 400-id sample resolved
375). The primary arm leaves their payload **absent**, which is what production does under consumer
lag. Absent is not neutral: in a weighted mean over present signals, a document with no popularity
term is scored on base and centroid alone while its competitors are pulled toward the median
popularity. It is also not the same as `count = 0`, which scores 0.0 and is punished hard.
"Exclude unresolved documents from the corpus" is reported as a sensitivity check. If the two
diverge, that divergence is a finding about the shipped code.

**Phase 2 is locked against Phase 0 and Phase 1.** Added 2026-09-14. Phase 0's triage read and
Phase 1's grid select **arm structure only**. Phase 2's primary endpoint, its PASS bar, and both
parameter rules (`S` = corpus median, `W` = 0.90 × σ_fused / σ_popularity) are unchanged by anything
those phases return. `S`'s *number* is computed from the completed fetch because `S` is defined as a
rule; that is execution, not revision. If a later decision does move a Phase 2 parameter, this
section must record that it moved and why — the pre-registration's value is entirely in being able
to tell the two cases apart.

**Also reported, not gating.** Top-10 churn across arms, the measure that showed fusion triples A
and B are not interchangeable.

## Known limitations

**Corpus construction is a validity threat.** SciFact's relevant documents are papers cited in
review articles, and its distractors come from the same pool. Relevant documents may be more-cited
*by construction*, which would make popularity look good here for a reason that does not transfer
to user engagement in a production deployment. The pool-matched AUC narrows this — comparing against
topically-matched papers rather than the whole corpus — but does not eliminate it. Any PASS is
evidence that a count-shaped prior helps on this corpus; it is not evidence about engagement.

**One corpus, one domain, one notion of popularity.** FreshStack cannot supply a second: its corpus
metadata is `{start_byte, end_byte, url}`, and godot's 25,482 documents come from six repositories
with 73% in one — six distinct values is not a per-document signal. NFCorpus is possible but not
cheap: opaque `MED-xxx` ids with `metadata: null`, needing a PubMed mapping and a different citation
API.

**Age is a confound with no shipped control.** Citation count rises with paper age, and neither the
popularity term (a lifetime count at the default `RecencyBoost = 0.0`; and its decay term tracks
engagement recency, not document age) nor `WDecay` (inert — no date property on
`BenchmarkDocument`) can correct for it. Age is therefore handled statistically, not
mechanically: measurement 4's within-stratum AUC, the age-preserving null that binds the gate, and
the citations-per-year variant. A result that clears the plain shuffled null but not the
age-preserving one is an age finding and must be reported as one.

**The instrument is coarse.** 339 judgments over 300 queries is about 1.13 relevant documents per
query, all binary, so nDCG@10 behaves close to MRR. MDE is 0.0152.

**Citation counts drift.** They are fetched once and cached to disk; both phases read the same
snapshot. The cache file is the experiment's data of record.

**The unauthenticated Semantic Scholar rate limit is the slow step.** The fetch is resumable and
disk-cached, so it costs wall-clock rather than risk. A free API key would reduce it to minutes.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | SciFact `_id`s are Semantic Scholar CorpusIds | `CorpusId:4983` → title matches `corpus.jsonl` byte-for-byte; ids span 4,983–198,309,074 |
| A4 | `scifact-full` qrels matches the archived run | 300 qrels queries, 300 run queries, zero qrels queries absent from the run |
| A5 | Run docids join to citation ids | 4,441 distinct docids in `sci-2048.similar.trec`, 0 absent from `corpus.jsonl` |
| A6 | No decay field on `BenchmarkDocument` | `BenchmarkDocument.cs` declares no date property |
| A7 | Centroid present for every candidate | **Conditional** — `ingest.py:620-625` omits `body_centroid` for all-zero-magnitude chunks. Phase 1 counts such points before relying on the uniform divisor |
| A8 | `.similar.trec` scores are the fused score | `IResultDiversifier.cs:13` ("1.00 reduces to `Take(topK)` exactly"); `ResultDiversifier.cs:74-75` |
| A11 | Snapshots restore | `scifact-2048-qdrant-snapshots/RESTORE.md` + the curl loop in `scifact-512-qdrant-snapshots/RESTORE.md`; 5,183 object / 6,587 chunk points |
| A12 | Point-id derivation is recoverable | `corpus_name = "beir"` — only candidate matching the real keymap under `uuid5(NAMESPACE_URL, f"{c}:{docId}")` |
| A13 | `keymap.json` covers the corpus | 5,183 entries, exact bijection with `corpus.jsonl` ids |
| A14 | Set-payload preserves vectors | `QdrantVectorServiceTests.cs:260` — `SetPayloadAsync_AddsFieldWithoutTouchingVectorOrExistingPayload`, a Testcontainer test |
| A15 | Read path looks for `citationsCount` | `ObjectSearchGrpcService.cs:944-982`, `Relation.ToCamelCase() + "Count"`, parsed via `long.TryParse` |
| A18 | Attributes can express the relation | `Iverson.Client.Sample/Models/Article.cs:29` is `[OneToMany(typeof(UserArticle))]` — the feature spec's own motivating example |
| A19 | `VectorRanking__*` binds from env | `docker-compose.yml:445-447`, including `VectorRanking__SimilarViaChunksTypes__0` |
| A20 | `Signals__0__*` binds to a record list | Executed: env vars → `Signals.Count = 1`, `ParentType='BenchmarkDocument'`, `Relation='Citations'`, `SaturationPoint = 186` |
| A21 | Warn-and-skip at boot leaves ranking live | `Program.cs:422-424` runs `LoadAsync()` then validation; `ValidateAtStartup` is `void` and never filters `Signals`; the read path reads `_popularitySignal.Signals` directly (`ObjectSearchGrpcService.cs:241`) |
| A22 | The sidecar is extensible | `BenchmarkQueryScenario.cs:206-222` builds a plain dictionary |
| A23 | `report.py` does paired stats + Holm | `holm_adjust` at :471, `paired_comparison` at :502, seeded sign-flip permutation at :538, `--pair` with a pool-invariance check |
| A23b | The baseline-generator bug is fixed | `per_query_values` returns a dict (:499); `paired_comparison`'s docstring states the baseline is computed once and reused |
| A26 | `ingest-contract.json` encodes no relations | Keys are `_generated`, `chunkWindow`, `distance`, `collectionNaming`, `embedding`, `golden` |
| A27 | Base-score spread | **Failed as originally stated.** Not ~0.07: measured on `sci-2048.similar.trec`, rank1→rank50 spread is median 0.1621 (p10 0.1006, p90 0.2499), magnitude 0.42–0.88. This is why `W` is pre-registered as a rule rather than a number |
| A29 | `SearchSimilar` top_k is 50 | `BenchmarkQueryScenario.cs:42`, `DocumentBudget = 50`; over-fetch 4× → 200-candidate pool |
| — | Citation counts have usable variance | n = 375: min 3, p25 75, median 186, p75 460, p90 1,196, max 75,285. **Corrected 2026-09-14 on n = 1,288:** min 0, p25 95, median 235, max 67,471 — and the "zero documents with 0 citations" claim is **false**: CorpusId 4810810 has 0. The count=0 population is small but non-empty; the Pre-registration's "Missing counts" paragraph already distinguishes it from absent (`pop = 0.0`, punished hard) |
| — | Counts carry relevance information | AUC 0.6201 vs. random non-relevant (95% CI [0.569, 0.672], z ≈ 4.55) |
| — | The shipped `SaturationPoint = 50` suits this corpus | **No.** At S = 50 the median maps to 0.788 and IQR is 0.303; S ≈ 300 maximises spread (IQR 0.409). S = 50 compresses the corpus into the saturated tail |
| A30 | `RecencyBoost` defaults to `0.0`, so the shipped default transform is exactly `count/(count+S)` | `PopularitySignalOptions.cs:17`; `PopularityFor` (`ObjectSearchGrpcService.cs:1040`) computes `effective = count + RecencyBoost × D` |
| A31 | A decay mechanism ships and is expressible as configuration | `PopularitySignalConsumer.cs:108` issues a `DateHistogram` with `CalendarInterval: "month"`; `:131` writes the `…Buckets` payload key; merged `b32f870` at 20:21 on 2026-09-13, 7 h after this spec was written at 13:15 |
| A32 | Phase 1's inputs exist and match their stated shapes | `sci-2048.similar.trec` (in `iverson-benchmark-corpora`, not `iverson-benchmark-data`): 15,000 lines, 300 queries, 4,441 distinct docids, max rank 50. `qrels/test.tsv`: 300 queries, 339 judgments, 283 distinct relevant docs |
| A33 | The existing cache cannot supply dates, and resuming would not fix it | `citations-countonly.json.bak` has top-level keys `counts`, `unresolved` only — no `years`/`dates`; `fetch_citations.py:43` computes `known` from those two, so cached ids are skipped |
| A34 | The count + date fetch is cheap; the per-citation decay fetch is ~2.9 h | 5,183 corpus ids, 1,400 already known → 38 batches to resume / 52 clean, at 1.2 s keyed or 3.0 s anonymous. Decay arm: measured mean 985 citations over 1,288 papers → 8,797 paged requests ≈ 2.9 h keyed |
| A35 | `WDecay` still cannot control for document age | `BenchmarkDocument.cs` declares `Id`, `DocId`, `Title`, `Body`, `OwnerId` — no date property. Unchanged by `b32f870` |
| A36 | `check_pool` has one call site and the `--baseline` route bypasses it | `grep -n 'check_pool' report.py` → definition `:732`, single call `:784` inside `run_pair_statistics`; `run_paired_statistics` (`:630`) contains none. Set-equality test at `:746`; `POOL_MIN_REORDERED_FRACTION = 0.25` at `:700`. The historical `--baseline` generator bug is fixed: `:350`/`:359` require one materialised run, `:663` `baseline_values` is a dict comprehension |
| A37 | In-process env binding does not imply the container receives the variable | `grep -nE "WPopularity\|PopularitySignal\|env_file" Iverson.Server/docker-compose.yml` → **zero hits**. The `iverson-api` `environment:` list (`:445-451`) is an explicit allowlist; `:451` `VectorRanking__SimilarViaChunksTypes__0` is the bare pass-through form. A19/A20 verified the binder, not the boundary |
| A38 | `ValidateAtStartup` throws for a registered parent whose relation is missing | warn-and-skip is the unregistered branch only (`PopularitySignalOptions.cs:89-97`); missing relation is `?? throw` (`:104-108`). Runs at `Program.cs:449`, before `app.Run()` (`:550`); `iverson-api` carries `restart: unless-stopped`. Empty `Signals` is a no-op — the method body opens with `foreach (var signal in options.Signals)`, no pre-loop check |
| A39 | A candidate with no popularity value keeps `fused_new = fused_old` | `ResultReranker.cs:53-57` adds `WPopularity` to `weightedSum` **and** `weightTotal` under one `hasPopularity` guard. Unresolved rate measured over the full cache: 112 of 1,400 = 8.0%, ~4.0 per 50-document pool |
| A40 | The truncated pool overstates the ceiling | 311 of 339 positive judgments lie inside their own query's recorded 50 (computed over the complete judgment set against `sci-2048.similar.trec`); ranks 51-200 × 300 queries = 45,000 slots holding ≤28 relevant = 99.938% non-relevant. `OverFetchFactor` = 4 per `EngagementQueryLimitOptions.cs:76` |
| A41 | No chunk hit dump exists for `sci-2048` | `find /home/ben/repositories/iverson-benchmark-corpora -name "*hits*"` → exactly three files, all `fs2048-*` FreshStack chunk-coverage dumps; `scifact-2048-2026-09-06/runs/` has none. Writer is `BenchmarkQueryScenario.cs:306`, consumed at `BenchmarkAggregateScenario.cs:38` — it postdates the archived run |
| — | ~~The popularity signal has no time decay~~ **Superseded 2026-09-14:** a decay term ships (`b32f870`) but is inert at the default `RecencyBoost = 0.0`, so the measured transform is unchanged | `PopularityFor` is `count/(count+S)` with no clock (`ObjectSearchGrpcService.cs:1019`); `ResultReranker`'s doc comment states it "reads no clock"; `PopularitySignalConsumer.cs:60` aggregates an unfiltered `COUNT(*)` with no date predicate |
| — | `WDecay` cannot control for document age here | `BenchmarkDocument` declares no date property, so `DecayFieldResolver` returns null and the decay term is inert on this corpus |
| — | `publicationDate` is available and finer than `year` | Probe of 40 ids: 38 resolved, `year` 38/38, `publicationDate` 38/38, month-granular (e.g. `1998-10-01`), occasionally day-granular (`1993-11-15`) |
| — | Per-citation dates exist but are out of scope | `/paper/{id}/citations?fields=year,publicationDate` returns citing-paper dates at day granularity; ≈4.1M records over a 1,000-per-page per-paper endpoint, and the shipped consumer cannot express a windowed count |
| — | Arms retrieve identical pools | `popularityPossible` keys off `Signals`, not `W` (`ObjectSearchGrpcService.cs:241`); `centroidPossible` is already true for this schema, so `rerankIsIdentity` is false in both arms |

Assumptions not independently verified and carried as risk: A2 (full-corpus fetch completes within
the rate limit — mitigated by resumability), A3 (counts stable across the window — mitigated by
caching), A10 (the recorded MDE transfers to this comparison), A16/A17 (validator passes and
`benchmark-ingest` is unaffected — both are Phase 2 step-1 checks), A25 (`IngestContractTests` still
passes — a Phase 2 step-1 check).

## Out of scope

- **Whether the consumer, StarRocks aggregate, and reconciliation worker work live.** This
  experiment patches Qdrant payloads directly and exercises only the read path: fusion, the
  over-fetch gate, and the chunk-path parent lookup. The write path is untested by it.
- **A production `WPopularity` default for engagement data.** A PASS here licenses a default for a
  citation-shaped prior on a biomedical corpus. Transfer to user engagement is an inference.
- **The reconciliation sweep's log-flooding behaviour under a persistent StarRocks fault**, deferred
  from the branch's final review. It remains a prerequisite for enabling a signal on a live cluster
  and is unaffected by this experiment.
