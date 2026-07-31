# Critical Design Review: 2026-07-30-result-diversification-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-30-result-diversification-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Section | Disposition |
|---|---|
| Context / "What 4a deferred" | ok — the negative claim "MMR needs no batch job, no k, nothing stale" checked against the design body; nothing in §1–§7 introduces persisted or recomputed state |
| Goal | → §3.1 (the goal names redundancy among *entries*; for `SearchChunks` the entries are passages but the mechanism measures documents) |
| §1 The mechanism | ok — MMR formula, λ, tie-break and the incremental-`maxSim` claim all checked; cost arithmetic re-derived (`topK × pool`, not `topK² × pool`) |
| §2 The diversity vector | → §3.1 |
| §3 The contract | ok — signature, DI registration and `public` requirement checked against `ServiceCollectionExtensions.cs:50` and `Iverson.Vector.csproj:10-12` |
| §4 Composition with part 3 | ok — gate-correctness argument re-derived independently; see arrow rows for the parameter-sourcing check |
| §5 Degradation and edge cases | ok — order-preservation under `λ·fused` re-derived incl. float monotonicity and negative scores; both numeric hazards checked against the probe evidence |
| §6 Behavioural change to callers | ok — proto comment locations confirmed at `object_search.proto:86-90`, `:126-129` |
| §7 Testing | ok — each listed case maps to a mechanism claim in §1/§5; embedding-only case traced through the gate |
| Verified assumptions | → §1 (A9's cited evidence is incomplete for a load-bearing negative claim) |
| Out of scope, but known | ok — `ComputeCentroid` re-read at `IntelligenceStoreConsumer.cs:368-389`; no zero guard, finding accurately stated |
| Known issues | ok — quadratic cost arithmetic re-derived; λ-uncalibrated statement matches the design |
| Not in this spec | ok — each exclusion cross-checked against the design body for contradiction; none |

### Rules and operands

| Rule | Disposition |
|---|---|
| `mmr(c) = λ·fused(c) − (1−λ)·maxSim(c)` — over-suppression | ok — bounded by `(1−λ)=0.3`; cannot displace the first selection (step 1 is unconditional) |
| Same rule — under-suppression | → §3.1 (passage-level near-duplicates across topically-distinct parents produce a low penalty and are not suppressed) |
| Presence predicate, **candidate side** (`DiversityVector` null / length / NaN) | ok — traced to `RerankCandidate.Centroid`, which is null-when-absent at `ObjectSearchGrpcService.cs:249` and `:386-388` |
| Presence predicate, **already-selected side** | ok — structurally identical operand, same source, same nullability; §5's "either vector absent" wording covers both sides symmetrically |
| Zero-length vector reaching the guard | ok — unreachable: `IntelligenceVectorService.RetrieveNamedVectorAsync` only inserts when `data is { Count: > 0 }`, so an empty array never enters the centroid map |
| Length-mismatch guard, load-bearing or defensive | ok — claim tested: Qdrant fixes a named vector's dimension at collection level, so two centroids from one collection under one vector name cannot differ in length. Spec's "defensive rather than load-bearing" is accurate |
| Tie-break ("earlier in fused-descending order") | ok — required for §5's exactness claim and explicitly specified; `Rerank`'s `OrderByDescending` is stable, so the incoming order is deterministic |
| Identity/exclusion: can one candidate id appear twice in the pool? | ok — ids are Qdrant point ids within one result set; part 3's `ResultsById` already collapses duplicates via `TryAdd`, and the diversifier neither merges nor excludes by identity |
| Identity: same-parent chunks share a diversity vector | ok — intended and stated; `cos = 1.0` exactly, verified as the maximum penalty case |
| Eligibility: producers of a centroid map entry | ok — one producer, `FetchCentroidsAsync` (`ObjectSearchGrpcService.cs:650`), reached from `:237` and `:374`; both traced. Failure path returns empty (degrade), covered by §5 |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| `Rerank` output → `Diversify` input | ok — **checked for the missing-parameter class.** `RerankedResult` carries only `(Id, FusedScore)`, so `DiversityVector` does not exist in the artifact §4 says the stage consumes. The join source is in scope and unambiguous at both call sites: `candidates` (`List<RerankCandidate>`) holds `Id` + `Centroid`, and `Centroid` is exactly the diversity vector for each RPC. Implementable without a choice; covered by A4/A5. Spec wording under-specifies the join but the design is not broken |
| centroid map → `RerankCandidate.Centroid` (SearchChunks) | ok — keyed by *parent* id, re-derived per candidate at `:386-388`; the chunk-id/parent-id distinction is handled at construction, not at selection |
| centroid map → `RerankCandidate.Centroid` (SearchSimilar) | ok — keyed by candidate id at `:249`; same id space as `DiversifyCandidate.Id` |
| `Diversify` output → response re-join → stream | ok — re-join is `byId.TryGetValue(ranked.Id, …)` (`:256`, `:397`), unchanged by this spec; `SearchChunks` still sources `text`/`parent_id` from the candidate's own payload |
| Persistence/serialization boundaries | ok — none. Every arrow is in-memory within one request; no write-then-read of a persisted artifact anywhere in this design |
| Call-site multiplicity for `Diversify` | ok — two call sites (`SearchSimilar :254`, `SearchChunks :395`); parameter sourcing traced independently for each, not once "for the operation" |

### Dropped candidates

| Candidate | Why dropped |
|---|---|
| Real-corpus centroid cosines cluster high (~0.7–0.95), compressing the penalty's dynamic range and possibly making diversification near-inert | Calibration of λ, which the spec already names as a known issue and deliberately makes a compile-time constant. Not literal wrongness |
| Absent-centroid candidates are never penalised, advantaging pre-4a documents | Acknowledged trade in §5, explicitly raised with and accepted by the user before the spec was written |
| Step 1 (unconditional first selection) is redundant — running the formula on an empty selected set yields the same candidate | Harmless redundancy; changes no output |
| §6 says the ordering change is "documented alongside" part 3's proto comment but §7 lists no proto deliverable | Documentation-scope ambiguity, not a break in asked-for behavior |
| Quadratic cost at large `top_k` | Spec states it, accepts it on stated reasoning, and names the remedy. A picked decision is not a forced one |

## 1. Verified-assumptions cross-check

- **A1** — holds. `IResultReranker.cs:9`, `ResultReranker.cs:57-59`, `ObjectSearchGrpcService.cs:254`, `:395`.
- **A2** — holds. `ResultReranker.cs:35-51`.
- **A3 / A3b** — hold. Re-confirmed against the recorded probe results; the asymmetric treatment they mandate (pre-call length check vs. post-call NaN check) is what §5 specifies.
- **A4** — holds. `ObjectSearchGrpcService.cs:386-388`, map built `:366-379`.
- **A5** — holds. `ObjectSearchGrpcService.cs:246-250`.
- **A6** — holds. `IntelligenceStoreConsumer.cs:375-381` divides by magnitude with no zero guard.
- **A7** — holds. `:213` gate; `:341-345` confirms `SearchChunks` never gates.
- **A8** — holds. `ServiceCollectionExtensions.cs:50`; `Iverson.Vector.csproj:10-12` names only `Iverson.Vector.Tests`.
- **A9 — holds, but the cited evidence was incomplete.** A9 is a load-bearing negative claim ("no existing test depends on trim or ordering behaviour in a way MMR breaks") and cites only two tests. A fresh sweep found **five** tests stubbing `RetrieveNamedVectorAsync` plus one that throws. Verified in-round: `:2346` empty map; `:2462` empty map (captures ids only); `:2499` throws; `:2553` non-empty but a **single** result, so selection is trivial; `:2604` non-empty with two centroids that are mutually orthogonal (`e1` vs `e0`), giving a zero penalty either way. The claim survives across the full set — but as written A9 under-cites its own evidence. Recommend extending A9's evidence column to name all six.
- **A10** — holds.

**Span check.** No uncovered design dependency found. The one dependency that is not stated in prose — that `RerankCandidate.Centroid` is exactly the diversity vector at the selection site for both RPCs — is covered by A4 and A5 as scoped.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### §3.1 — Diversity is measured at document granularity, but `SearchChunks` returns passages

**The choice:** whether `SearchChunks` diversifies on the *parent document's* centroid (what the spec specifies) or on the *chunk's own* vector.

**Why it's forced:** a codebase constraint makes this an either/or the spec cannot avoid, and the spec picks one side without naming that a choice existed. `VectorSearchResult` carries `(Id, Score, Payload)` and no vector (`IVectorRoles.cs:49-52`), so a chunk's own embedding is not in hand at selection time. Passage-level diversity would require a second retrieve against the **chunks** collection — which contradicts the spec's central "no new I/O" justification for choosing MMR over cluster centroids in the first place. The parent centroid is free precisely because part 3 already fetched it for a different purpose.

The consequence is a concrete behavioral asymmetry with a failure direction in each sense:

- **Over-suppression.** Two genuinely distinct passages from the *same* document share a diversity vector, so `cos = 1.0` — the maximum penalty, `0.3` at λ=0.70. Adjacent fused scores in a top-`k` typically differ by far less than that, so in practice the single most relevant document contributes roughly **one** passage even when its top three passages are the three best answers available. The spec presents same-parent suppression as a benefit ("Same-document crowding is therefore reduced as a side effect, which is why no separate per-parent cap is specified") without stating this cost.
- **Under-suppression.** Two near-duplicate *passages* living in documents that are broadly different topically produce a low parent-centroid cosine and are not suppressed — the passage-level redundancy a RAG consumer actually reads goes uncaught.

The Goal names "entries" that are "topically redundant"; for `SearchChunks` the entries are passages, and the mechanism measures documents.

**The options:**

1. **Keep the parent centroid** (as specified), and state the granularity choice and its over-suppression cost explicitly in the spec, so the behavior is documented rather than discovered. Preserves "no new I/O".
2. **Diversify `SearchChunks` on chunk vectors**, accepting a second retrieve against the chunks collection for the over-fetched candidate ids. Measures the quantity the Goal names, at the cost of the spec's no-new-I/O property and an extra round trip proportional to `4 × top_k`.
3. **Split the two RPCs**: `SearchSimilar` diversifies on object centroids (already the right granularity — the entries *are* objects), `SearchChunks` either uses chunk vectors or is left out of scope for this part.

Not picking. The trade runs directly through the justification that selected this approach over 4b's cluster centroids, so it is the user's call.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty. §3.1 asks whether document-granularity diversity is the intended behavior for a passage-returning RPC; the answer may leave the spec unchanged apart from documenting the choice, but it should be an explicit decision rather than an implicit one. §2 is empty: nothing in the design breaks the asked-for behavior, and A9's under-citation (§1) is an evidence-completeness fix, not a defect.
