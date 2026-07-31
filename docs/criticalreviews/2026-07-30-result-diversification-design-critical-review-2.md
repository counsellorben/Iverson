# Critical Design Review: 2026-07-30-result-diversification-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-07-30-result-diversification-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built against the current spec before prior-review history was consulted.

### Sections

| Section | Disposition |
|---|---|
| Title | dropped — "MMR over Fetched Centroids" is now inaccurate for `SearchChunks`, which uses chunk vectors rather than centroids. Cosmetic; no behavior depends on it |
| Context / "What 4a deferred" | ok — the qualified no-new-I/O claim re-checked against §2 and §4; the two now agree (`SearchSimilar` none, `SearchChunks` one retrieve) |
| Goal | ok — "entries…topically redundant" now matches the mechanism for both RPCs; the round-1 granularity mismatch is gone |
| §1 The mechanism | ok — formula, λ, tie-break, incremental-`maxSim` and the `topK × poolSize` arithmetic re-derived independently |
| §2 The diversity vector | → §1 span check (what the chunk vector actually represents is not covered by any assumption) |
| §3 The contract | ok — signature unchanged and still granularity-agnostic, which is what makes the §2 divergence expressible; DI site and `public` requirement re-checked |
| §4 Composition with part 3 | → §1 (A9 failure: the second retrieve breaks an existing test) |
| §5 Degradation and edge cases | ok — order-preservation under `λ·fused` re-derived; the dimension-guard justification correctly updated to name both vector sources |
| §6 Behavioural change to callers | ok — proto line references re-confirmed at `object_search.proto:86-90`, `:126-129` |
| §7 Testing | → §1 (omits the update to the existing test the design breaks) |
| Verified assumptions | → §1 |
| Out of scope, but known | ok — `ComputeCentroid` re-read at `IntelligenceStoreConsumer.cs:368-389`; unchanged and accurately described |
| Known issues | ok — the batching claim checked: `IntelligenceVectorService.cs:153` sets `BatchSize = 512`, so the new `4 × top_k` retrieve is genuinely paged |
| Not in this spec | ok — the per-parent-cap entry's revised justification matches §2's chunk-level mechanism; no contradiction |

### Rules and operands

| Rule | Disposition |
|---|---|
| `mmr(c) = λ·fused(c) − (1−λ)·maxSim(c)` — over-suppression | ok — bounded by `0.3`; step 1 protects the top hit |
| Same rule — under-suppression | ok — with chunk-level vectors the round-1 miss (passage duplicates across distinct parents) is now caught; verified the vector compared is the one Qdrant matched on |
| Presence predicate, candidate side | ok — null / length / NaN, traced to both sources |
| Presence predicate, already-selected side | ok — same source and nullability on both sides; §5's "either vector absent" is symmetric |
| Diversity-vector source, `SearchSimilar` | ok — object centroid, `ObjectSearchGrpcService.cs:246-250`, granularity matches its entries |
| Diversity-vector source, `SearchChunks` | ok — chunk's own vector; name `<property>_vector` confirmed identical to the search's own at `:337` |
| Length-mismatch guard, still defensive? | ok — re-derived for the new source: chunk vectors within one `_chunks` collection under one named vector share a Qdrant-fixed dimension. Claim holds |
| Eligibility: producers of a chunk point carrying `<property>_vector` | ok — grep'd every `UpsertNamedAsync` caller outside tests: `IntelligenceStoreConsumer.cs:148`, `:238`, `:288`. Only `:238` writes the chunks collection, and it writes exactly `<property>_vector`. No second producer, no unhandled input class |
| Over-fetch gate correctness under the new design | ok — the gate lives only in `SearchSimilar` (`:213`); `SearchChunks` never gates (`:341-345`), so the gate's reasoning is unaffected by `SearchChunks` no longer depending on `centroidPossible` for diversity |

### Data-flow arrows

| Arrow → operation | Disposition |
|---|---|
| `Rerank` output → `Diversify` input | ok — §4 now states the by-id pairing explicitly and names a different source per RPC; both sources traced to values in scope at their call site |
| centroid map → `DiversifyCandidate.DiversityVector` (`SearchSimilar`) | ok — `RerankCandidate.Centroid`, keyed by candidate id, same id space |
| chunk-vector map → `DiversifyCandidate.DiversityVector` (`SearchChunks`) | ok — keyed by chunk point id, which is the candidate id here; distinct from the parent-keyed centroid map, and §4 says so explicitly |
| chunk-vector retrieve → `RetrieveNamedVectorAsync` | ok — all three parameters exist at the call site: `chunksCollection` (`:348`), candidate ids, `vectorName` (`:337`). Scoped-key pattern matches `FetchCentroidsAsync` (`:650-669`), which mints the key internally and degrades to empty on any non-cancellation exception |
| `Diversify` output → re-join → stream | ok — `byId.TryGetValue` unchanged at `:256`, `:397` |
| Call-site multiplicity for `RetrieveNamedVectorAsync` in `SearchChunks` | → §1 (this is where the A9 failure lives: two call sites now, and an existing test asserts one) |
| Persistence/serialization boundaries | ok — none; every arrow is in-memory within one request |

### Dropped candidates

| Candidate | Why dropped |
|---|---|
| Title still says "over Fetched Centroids" | Cosmetic. No behavior depends on the title |
| `FetchCentroidsAsync` hard-codes its degrade log and `EmptyCentroids` return, so the chunk-vector retrieve needs a sibling or a generalized helper | Implementation shaping, not a design break. §4 specifies the required behavior (log, continue, vectors absent) and that is what matters at design time |
| The second retrieve doubles `SearchChunks`' retrieve volume at large `top_k` | Spec states it in Known issues, and the batching claim was verified accurate. A stated, accepted cost is not a finding |
| λ remains uncalibrated for the new vector source — chunk-vector cosines have a different distribution than centroid cosines, so 0.70 is even less grounded | Calibration of a value the spec already flags as uncalibrated. Not literal wrongness |

## 1. Verified-assumptions cross-check

- **A1** — holds. `IResultReranker.cs:9`, `ResultReranker.cs:58`, `ObjectSearchGrpcService.cs:254`, `:395`.
- **A2** — holds. `ResultReranker.cs:35-51`.
- **A3 / A3b** — hold; the asymmetric handling they mandate is what §5 specifies.
- **A4** — holds, and its new scope note is accurate: `:386-388` resolves the parent centroid, which now serves the re-rank signal only.
- **A5** — holds. `:246-250`.
- **A6** — holds. `IntelligenceStoreConsumer.cs:375-381`.
- **A7** — holds. `:213`; `:341-345`.
- **A8** — holds. `ServiceCollectionExtensions.cs:50`; `Iverson.Vector.csproj:10-12`.
- **A10** — holds as re-scoped onto A5 and A11.
- **A11** — holds. `SchemaBuilder.cs:213` declares the named vector; `IntelligenceStoreConsumer.cs:238-242` writes it; `ObjectSearchGrpcService.cs:337` builds the identical name. Producer sweep found no second writer.

### A9 — FAILED

**Assumption as written:** "No existing test depends on trim or ordering behaviour in a way MMR breaks," with evidence ending "All still pass."

**Why it now fails.** The trim-and-ordering half is still true. The evidence's closing claim is not. `SearchChunks` now issues **two** `RetrieveNamedVectorAsync` calls — part 3's parent-centroid retrieve against the object collection, plus §4's new chunk-vector retrieve against the chunks collection — and `ObjectSearchGrpcServiceTests.cs:2445` (`SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds`) is written against exactly one:

- its stub matches `Arg.Any<string>()` for the collection, so it intercepts **both** calls and its captured locals are overwritten by the second;
- `await _vector.Received(1).RetrieveNamedVectorAsync(…)` fails — the call count is 2;
- `capturedIds.Should().ContainSingle()` fails — the second call passes three chunk ids, not one parent id;
- `capturedCollection.Should().Be("articles_test-tenant")` fails — overwritten with the chunks collection;
- `capturedVectorName.Should().Be("body_centroid")` fails — overwritten with `body_vector`.

Four assertions in one existing test. §7 lists new tests to write but never mentions updating this one, so an implementer following the spec exactly lands a red suite and has to reverse-engineer whether the failure is a genuine regression or expected churn.

The other eight tests reaching the selection step were re-checked against the *current* design and do survive: `:2485` throws for any collection, so the new retrieve also throws and degrades; `:2575`'s stub returns a parent-keyed dictionary that the chunk-id lookup simply misses, leaving diversity vectors absent and the fused order intact; `:2421` leaves the retrieve unstubbed; the rest fetch no centroids at all.

**Proposed fix.** Correct A9 to say one existing test breaks and name it, and add a line to §7 requiring `SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds` to be updated so its stub and assertions discriminate the two retrieves by collection rather than matching `Arg.Any<string>()`.

### Span check — one uncovered dependency

**What a `SearchChunks` chunk vector actually represents is verified nowhere.** A11 establishes that the named vector exists, carries the right name and is retrievable. Nothing covers what it *encodes* — yet §2's entire argument for chunk-level granularity rests on it being a passage representation ("a cosine between two of them is true passage-level similarity", and "chunks sharing a parent … are suppressed exactly to the extent their passages are actually similar").

Verified in-round: when contextual chunk prefixes are active (part 2's feature — `contextualEnabled && cf.Contextual`), the embedded text is not the passage. `IntelligenceStoreConsumer.cs:191-202` computes `documentContext` as the object summary, or the parent text's first `ParentTextContextChars`, and embeds `PrefixWithContextAsync(documentContext, chunkText)`. That prefix is **identical for every chunk of that field in that document**, so same-parent chunk vectors carry a shared component that raises their mutual cosine independently of passage content.

The consequence is narrower than round 1's finding — the mechanism still diversifies, and diversifying on the same representation Qdrant matched against is coherent — but two of §2's stated claims are inaccurate for contextual fields: same-parent chunks retain a similarity floor from the shared prefix, so they *are* suppressed partly for sharing a parent. There is no un-prefixed chunk vector stored, so no alternative exists and no choice is forced; this is a claim to correct, not a decision to make.

**Proposed fix.** Add a covering assumption recording what the chunk vector encodes on both paths, and soften §2's two absolute claims to hold for non-contextual fields while noting the shared-prefix floor for contextual ones.

## 2. Literal-wrongness findings

No literal-wrongness findings. The design's mechanism is sound for both RPCs; the actionable item this round is the failed A9 above, which the category table treats as §2-equivalent.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §3.1 (granularity mismatch)** — resolved. §2 now splits the two RPCs, `SearchChunks` uses the chunk's own vector, and §4 specifies the retrieve, its scoped key and its degrade path. The over-suppression and under-suppression consequences round 1 described are both gone.
- **Round 1 §1 (A9 under-cited its evidence)** — resolved as to citation: A9 now enumerates all nine tests reaching the selection step rather than two. The *claim* has since been falsified by the fix itself, which is the new §1 finding above — a different defect, not a re-raise.
- **Round 1 §0 note (per-parent cap justified as "subsumed")** — resolved. The entry in "Not in this spec" no longer rests on the parent-centroid mechanism that was removed.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §3 is empty, so nothing blocks on a user decision. §2 is empty in the strict sense, but A9 **failed** (§1), which the finding-category table treats as a high-priority §2-equivalent: an existing test breaks in four assertions and the spec's testing section doesn't mention it. Fix A9's text plus the §7 line, and correct the span-check claim about what a chunk vector encodes; the design itself needs no change.
