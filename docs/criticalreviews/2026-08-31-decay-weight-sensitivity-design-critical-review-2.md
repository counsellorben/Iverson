# Critical Design Review: 2026-08-31-decay-weight-sensitivity-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-31-decay-weight-sensitivity-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

**Sections**

| row | disposition |
|---|---|
| Purpose + branch scoping (L3-14) | ok — scoping to the centroid-present branch is consistent with the Background table (L46-48) and the analytical decision (L126-141) |
| Background — shares table (L30-34) | ok — all nine cells recomputed; correct |
| Background — branch table (L36-44) | ok — re-derived all three branches: `1/110`, `1/66`, and exact agreement when decay is absent (`(0.5b+0.5c)/1.0 == (0.45b+0.45c)/0.9`) or when both are absent (short-circuit to `BaseScore`, `ResultReranker.cs:24-31`) |
| Design/Capture — row format (L61-68) | → §2.1 |
| Design/Capture — singleton lock (L70-71) | ok — `ServiceCollectionExtensions.cs:50` |
| Design/Capture — correlation rule (L76-79) | ok — `BenchmarkQueryScenario.cs:85-107` re-read; sequential, two calls per query |
| Design/Capture — validation gate (L84-89) | ok — `failures` is genuinely observable: `BenchmarkQueryScenario.cs:122-124` prints `{failures:N0} search RPC(s) failed`. 2 x 672 = 1,344 is arithmetically right |
| Design/One run (L91-95) | ok — weight-independence re-read at `ResultReranker.cs:35-51` |
| Design/Offline model (L97-115) | ok — decay formula and clamp still match `DecayFieldResolver.cs:14,85`; branch selection by `hasCentroid` is coherent with the L40-44 table |
| Design/Scope — pre-MMR sufficiency (L117-124) | ok — MMR consumes the reranked list at `ObjectSearchGrpcService.cs:268`/`:456`; identical input ordering gives identical output |
| Design/Scope — analytical decision (L126-141) | ok — re-derived independently: branch-1 share needs `WBase = 4.5*WDecay`, branch-2 needs `WBase = 6*WDecay`; simultaneous solution forces `WDecay = 0`. Both share columns recomputed and correct |
| Design/Decision rule (L143-151) | ok — "in the measured branch" qualifier now matches the L126-141 finding |
| Design/Reported-not-decisive (L153-160) | ok — unchanged from round 1 |
| Out of scope (L162-167) | ok — four exclusions consistent with the body |
| Verified assumptions (L169-192) | → §1 |

**Rules and operands**

| row | disposition |
|---|---|
| R1 branch-dependent closed form, all three branches | ok — each re-derived this round |
| R2 `hasCentroid` predicate vs the reranker's own test | ok — `ResultReranker.cs:20` gates on `Centroid is not null && Centroid.Length == queryVector.Length`; the spec says `hasCentroid` records "whether the centroid term was included", which matches the compound condition rather than mere null-ness |
| R3 call-index parity, under-count direction | ok — the L84-89 gate catches a skipped call (1,343 != 1,344) |
| R3 call-index parity, **zero-candidate direction** | dropped — a `Rerank` call with an empty candidate list emits no rows and is indistinguishable from a skipped call, but A17 establishes min=max=50 results for all 672 queries on this corpus, so an empty pool cannot arise here. Fails literal-wrongness on the corpus in scope |
| R4 age key identity across endpoints | dropped — `parentId` is a ulong object id on the similar path and a parent key string on the chunks path, so one document can draw two different ages across endpoints. Each endpoint's A-vs-B comparison uses internally consistent ages, so the measured quantity is unaffected. Fidelity wrinkle, not literal-wrongness |
| R5 decision threshold >=99% | ok — mechanics sound; the value is a judgment |

**Data-flow arrows**

| row | disposition |
|---|---|
| D1 `Rerank` internals → capture file (**persistence boundary**) | → §2.1 — a named field does not exist at the producing site |
| D2 capture file → offline scoring op (params: base, centroidCos, hasCentroid, parentId, branch) | → §2.1 — one of five parameters is unobtainable where the spec says to write it |
| D3 offline scoring → top-10 set comparison | ok — A17 re-verified |
| D4 harness query order → `callIndex` | ok — guarded by the L84-89 gate |

## 1. Verified-assumptions cross-check

All twenty listed assumptions re-read against cited evidence; **all still hold**. A19 and A20 (added since round 1) reconfirmed at `IntelligenceStoreConsumer.cs:302-318`.

**Span check — uncovered dependencies:**

1. *"The fields named in the capture format are reachable at the capture site."* No assumption covers this. **Verified in-round and FALSE** — see §2.1. A1, A5, A10 and A13 each speak about `ResultReranker` internals, and A18/A19/A20 speak about payload contents in `ObjectSearchGrpcService` and `IntelligenceStoreConsumer`, but nothing states that the capture site can see both. The gap sits exactly between two verified assumptions.

## 2. Literal-wrongness findings

### 2.1 `parentId` cannot be captured where the spec says to capture it

**Description.** The capture is specified to happen inside `ResultReranker.Rerank` (L58-59), emitting `callIndex, candidateId, parentId, hasCentroid, baseScore, centroidCos` (L62). But `Rerank` receives only:

```csharp
public sealed record RerankCandidate(
    ulong    Id,
    double   BaseScore,
    float[]? Centroid,
    double?  Decay);

IReadOnlyList<RerankedResult> Rerank(float[] queryVector, IReadOnlyList<RerankCandidate> candidates);
```

There is **no payload** on `RerankCandidate` and none on the method. `r.Payload["parent_id"]` — which L65-66 names as the source — exists only in `ObjectSearchGrpcService`, one layer up. The capture as specified cannot be written.

The two sites are complementary rather than interchangeable, so relocating the capture does not fix it either:

- **Inside `Rerank`:** has `centroidCos` (computed at `ResultReranker.cs:40`, and A1 records this is the *only* place it is computed), `baseScore`, `Id`, `hasCentroid`. Missing `parentId`.
- **At the call site** (`ObjectSearchGrpcService.cs:444-447`): has `parentId`, `baseScore`, `Id`, and centroid presence. Missing `centroidCos`.

Neither site alone can produce the specified row.

**Evidence.** `IResultReranker.cs:3-7` (record shape) and `:12` (method signature); `ObjectSearchGrpcService.cs:444-445` for the payload access; `ResultReranker.cs:40` for the cosine.

**Why it is literal-wrongness rather than an implementation detail.** The offline model's age assignment is keyed on `parentId` (L99-100), and that keying is itself the round-1 §2.2 fix that prevents impossible intra-document decay variance from inflating the measured spread. Without `parentId` the model must fall back to per-candidate ages — the exact defect round 1 identified. The spec would produce a biased answer, not merely an inconvenient one.

**Proposed fix.** On the scratch branch, add a `ParentId` field to `RerankCandidate` and populate it at both call sites (`r.Payload["parent_id"]` on the chunks path, `r.Id` on the similar path). The record is internal to the scratch branch and never committed, so widening it costs nothing and keeps the capture in one place, preserving A1's single-computation-site property.

Alternative if the record must stay untouched: emit two files — `(callIndex, candidateId, parentId)` at the call site and `(callIndex, candidateId, hasCentroid, baseScore, centroidCos)` inside `Rerank` — and join on `(callIndex, candidateId)`. This works but adds a join whose correctness depends on the same parity assumption the L84-89 gate already has to police.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **R1 §2.1** (bound stated only for the all-signals branch) — resolved: L36-44 now gives all three branches with `1/110`, `1/66` and exact agreement, and L46-48 anchors the significance argument to the measured branch.
- **R1 §2.2** (capture row lacked document linkage and branch flag) — *partially* resolved: `parentId` and `hasCentroid` are now named in the format and consumed correctly by the offline model, but `parentId` is not obtainable at the specified site. See §2.1.
- **R1 §2.3** (silent parity break) — resolved: the L84-89 validation gate asserts 1,344 distinct `callIndex` values and `failures == 0`, and `failures` is genuinely observable at `BenchmarkQueryScenario.cs:122-124`.
- **R1 §3.1** (corpus cannot exercise the larger-bound branch) — resolved as option (c): L7-11 scopes the experiment to the centroid-present branch, and L126-141 decides the centroid-absent branch analytically, establishing that no triple at `w = 0.500` preserves both branches and that the branches favour opposite triples.
- **R1 §1 span check** (chunk metadata denormalisation, shared decay per document) — resolved: A19 and A20 added.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one finding, §3 is empty.
