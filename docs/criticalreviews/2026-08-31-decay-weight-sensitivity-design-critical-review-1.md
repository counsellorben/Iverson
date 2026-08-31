# Critical Design Review: 2026-08-31-decay-weight-sensitivity-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-decay-weight-sensitivity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

**Sections**

| row | disposition |
|---|---|
| Purpose (L3-8) | ok — scope claim ("does not find the best decay weight") is consistent with Out of scope L121 |
| Background — triples table (L24-28) | ok — recomputed all nine share cells: 0.50/1.10=45.45%, 0.10/1.10=9.09%, 0.45/1.00=45%, 0.10/1.00=10%, shipped w=0.30/0.90=0.333. All correct |
| Background — closed form (L30-34) | → §2.1 |
| Background — bound vs median-gap argument (L36-40) | → §2.1 (the argument inherits the wrong bound) |
| Design/Capture — row format (L49-51) | → §2.2 |
| Design/Capture — singleton lock (L53-54) | ok — `ServiceCollectionExtensions.cs:50` confirms `AddSingleton`; lock requirement correctly stated |
| Design/Capture — correlation rule (L59-62) | → §2.3 |
| Design/Capture — run procedure (L64-65) | ok — covers concurrent traffic; does not cover the skipped-call case, which is §2.3 |
| Design/One run (L67-71) | ok — weight-independence re-read at `ResultReranker.cs:35-51`; neither component reads `WBase`/`WCentroid` |
| Design/Offline model — decay formula (L75-77) | ok — `0.5^(ageDays/180)` and the `<=1.0` clamp match `DecayFieldResolver.cs:14,85` exactly |
| Design/Offline model — scenario table (L81-86) | ok — scenarios vary spread, which is the quantity the algebra identifies |
| Design/Scope pre-MMR (L91-98) | ok — sufficiency argument holds: MMR consumes the reranked list, so identical input ordering gives identical output |
| Design/Decision rule (L100-108) | ok as mechanics; its coverage limit is §3.1 |
| Design/Reported-not-decisive (L110-117) | ok — correctly refuses to let a recency-blind measure pick the triple |
| Out of scope (L119-124) | ok — all four exclusions consistent with the body |
| Verified assumptions (L126-147) | → §1 |

**Rules and operands**

| row | disposition |
|---|---|
| R1 closed form, all-signals-present branch | ok — re-verified to 1e-12 over 200k draws |
| R1 closed form, **signal-absent branches** | → §2.1 — the reranker's weight accumulation is conditional; the spec evaluates only one branch |
| R2 call-index parity (over- and under-count) | → §2.3 — under-count direction (a skipped call) is unhandled |
| R3 decision threshold >=99% | ok — mechanics sound; the 99% value is a judgment, not a mechanic |
| R4 "uniform age reorders nothing" | dropped — conclusion is TRUE but not for the stated reason. At constant `d` the perturbation is not a constant offset; it is a positive rescaling (`A_i-A_j = 0.90909(s̄_i-s̄_j)` vs `B_i-B_j = 0.9(s̄_i-s̄_j)`), so sign is preserved and the control is valid. Stated mechanism imprecise, experiment unaffected — fails literal-wrongness |
| R5 offline decay mirrors `ComputeDecay` | ok — formula, half-life and clamp all match |

**Data-flow arrows**

| row | disposition |
|---|---|
| D1 `Rerank` internals → capture file (**persistence boundary**) | → §2.2 — the persisted row lacks a field the consumer needs |
| D2 capture file → offline scoring op (params: base, centroidCos, centroid-presence, doc grouping key) | → §2.2 — two of four params absent from the artifact |
| D3 offline scoring → top-10 set comparison | ok — A17 re-verified: 672 queries, min=max=50 results on both endpoints |
| D4 harness query order → `callIndex` (**process boundary**) | → §2.3 |

## 1. Verified-assumptions cross-check

All eighteen listed assumptions re-read against cited evidence this round; **all still hold**. Spot-notes on the load-bearing ones:

- **A1** reconfirmed — `ResultReranker.cs:40` is the only query-to-centroid cosine; `ResultDiversifier.cs:98` is candidate-to-candidate.
- **A9** reconfirmed — 2,291,100 rows, zero negative, min 0.443912. Note the assumption is scoped to signals in `[0,1]`; §2.1 shows the *bound derived from it* is branch-dependent, which is a gap in the design, not a failure of A9 as written.
- **A13** reconfirmed — `AddSingleton` at `ServiceCollectionExtensions.cs:50`.
- **A18** reconfirmed — and it is precisely this assumption that creates §3.1: the corpus structurally cannot produce the centroid-absent class.

**Span check — uncovered dependencies:**

1. *"Chunk payloads carry the parent's metadata columns, so decay is non-null on the SearchChunks endpoint."* No assumption covers this; the design's applicability to `SearchChunks` depends on it. **Verified in-round:** `IntelligenceStoreConsumer.cs:302-318` copies every `schema.MetadataColumns` entry onto each chunk payload. Dependency holds.
2. *"All chunks of one document share a single decay value."* Load-bearing for §2.2's fix and never stated. **Verified in-round:** same loop — each chunk's metadata is extracted from the parent's `payload`, so every chunk of a document carries the identical timestamp.
3. *"No code path reaches a search response without calling `Rerank`."* Load-bearing for the parity mapping, never stated. **Verified in-round and FALSE** — see §2.3.

## 2. Literal-wrongness findings

### 2.1 The ±0.00909 bound is wrong for a reachable — and for some schemas, default — input class

**Description.** The closed form `A - B = (1/110)·(mean(base,centroid) - decay)` is derived assuming base, centroid and decay are *all* present. But `ResultReranker` accumulates `weightTotal` only over signals present (`ResultReranker.cs:35-51`), so a candidate with decay but **no centroid** fuses as `(0.50b + 0.10d)/0.60` versus `(0.45b + 0.10d)/0.55`. That difference is:

```
A - B = (1/66) * (base - decay)        bound = 0.01515
```

verified to 1e-12 over 200,000 draws — **1.67x the bound the spec states**.

**Evidence.** Centroid-absent is reachable on both endpoints, and is the *default* for a schema whose searched property is not chunked: `ObjectSearchGrpcService.cs:242-243` sets `var centroids = EmptyVectors;` and skips the fetch entirely when `!centroidPossible`, so **every** candidate has `Centroid: null`. It is additionally reachable via degradation on both paths (`RetrieveVectorsOrDegradeAsync`, "re-ranking without the centroid signal" at `:255`; chunks path at `:430-435`), and per-candidate on the chunks path when `parent_id` is missing or the parent centroid is not in the dict (`:441-447`).

This matters beyond arithmetic: the spec's justification for running the experiment at all is the comparison of the bound against the 0.00173 median adjacent gap (L36-40). Under the correct branch bound the perturbation is **8.8x** the median gap, not 5.3x.

**Proposed fix.** State the bound per branch — `1/110` when centroid is present, `1/66` when it is absent (and note the both-absent branch short-circuits to `BaseScore`, where the triples are exactly identical) — and derive the significance argument from the branch the measured corpus actually exercises, flagging the other as unmeasured. This interacts with §3.1.

### 2.2 The capture row lacks the field the offline model needs to assign ages faithfully

**Description.** The capture format is `callIndex, candidateId, baseScore, centroidCos` (L50). The offline model is specified to "assign each **document** an age" (L75). On the `SearchChunks` path, `candidateId` is a **chunk** id, not a document id: `ObjectSearchGrpcService.cs:447` constructs `new RerankCandidate(r.Id, ...)` where `r` iterates chunk results.

Assigning ages per `candidateId` therefore gives sibling chunks of one document **different** decay values — a state production cannot produce, because `IntelligenceStoreConsumer.cs:302-318` denormalises the parent's metadata onto every chunk, so all chunks of a document carry one identical timestamp.

**Why this breaks the result, not just the fidelity.** The quantity that drives reordering is the spread of `mean(base,centroid) - decay` across a result set (the spec's own L38-40). Injecting impossible intra-document decay variance **inflates exactly that spread**, biasing the measurement toward "material" — so the decision rule at L102 can fail for a reason that is an artifact of the model rather than a property of the fusion.

Separately, the row cannot express "this candidate had no centroid": with `centroidCos` absent there is no value to write, and the offline model must know which fusion branch to apply (§2.1).

**Proposed fix.** Capture `parentId` (from `r.Payload["parent_id"]` on the chunks path; `candidateId` itself on the similar path) and an explicit `hasCentroid` flag. Assign ages per `parentId`, and select the fusion branch per `hasCentroid`.

### 2.3 A single pre-`Rerank` throw silently shifts the entire query correlation

**Description.** The correlation rule states as unconditional fact that "call index `2i` / `2i+1` maps to query `i`" (L61-62). This holds only if every query contributes exactly two `Rerank` calls. Both endpoints can throw before reaching `Rerank`: `ObjectSearchGrpcService.cs` raises `RpcException(StatusCode.Unavailable, ...)` on the embedding path in both `SearchSimilar` and `SearchChunks`, ahead of the `Rerank` call sites at `:268` and `:456`.

The harness does not abort on this — `BenchmarkQueryScenario.cs:97` accumulates `failures += similar.Failed + chunks.Failed` and continues the loop. So one embedding hiccup at query *k* shifts the parity of every subsequent capture row, joining captured components to the **wrong queries** for the remainder of the run, with no error and no artifact indicating it happened.

**Evidence.** `throw new RpcException(new Status(StatusCode.Unavailable,` appears before the `Rerank` call in both method bodies; `BenchmarkQueryScenario.cs:97` counts failures and proceeds.

**Proposed fix.** Either write the query id into the capture row directly (removing the inference), or add a validation gate before any analysis: assert the number of distinct `callIndex` values equals exactly `2 x <queries>` (1,344 for this corpus) and that `failures == 0`, refusing to proceed otherwise. The spec's existing run procedure (L64-65) guards concurrent traffic but not this.

## 3. Forced decisions

### 3.1 The chosen corpus structurally cannot exercise the branch with the larger bound

**The choice.** Which input class the sensitivity result is allowed to speak for.

**Why it's forced.** A18 records that benchmark candidates always carry a non-null centroid — `BenchmarkDocument.cs:16-18` marks `Body` with both `[IversonEmbedding]` and `[IversonChunk]`, which is what makes the centroid non-degenerate. That is exactly the property that makes this corpus unable to produce the centroid-absent class, and §2.1 shows that class carries the **larger** bound (`1/66` vs `1/110`) and is the default for any schema whose searched property is not chunked. The decision rule at L102-105 would therefore close the hold on evidence drawn only from the smaller-perturbation branch, while the shipped constant applies to both.

**The options.**

- **(a)** Scope the result explicitly to centroid-present schemas: run as designed, and state in the outcome that centroid-absent schemas are decided analytically by the `1/66` bound rather than measured.
- **(b)** Add a second capture arm that exercises the centroid-absent branch — e.g. a benchmark entity whose searched property carries `[IversonEmbedding]` without `[IversonChunk]`, which per `:242-243` makes every candidate's centroid absent — and apply the decision rule to both arms.
- **(c)** Decide the centroid-absent branch analytically now and narrow the experiment's stated purpose to the centroid-present branch only.

## 5. Recommendation

🛑 **Surface forced decisions to user** — §3 is non-empty (and §2 carries three findings).
