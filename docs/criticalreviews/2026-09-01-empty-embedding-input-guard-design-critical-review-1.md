# Critical Design Review: 2026-09-01-empty-embedding-input-guard-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-01-empty-embedding-input-guard-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S1 | Header / status | ok — "verified against main at `a03d353`" was true at write time; the only commit since is the spec's own (`f956a25`), no code moved |
| S2 | The problem — ingest | ok — re-read `IntelligenceStoreConsumer.cs:226`, `:229`, `:244`, `:667` and `EmbeddingService.cs:73`; the described path holds exactly |
| S3 | The problem — query | ok — `ObjectSearchGrpcService.cs:201`/`:372` reach `EmbedAsync` with no emptiness check; the proto3 `""` default is corroborated by `Iverson.Clients/TypeScript/generated/object_search.ts:1584` (`query: ""` in `createBase*`) |
| S4 | Design 1 — exception type | ok — matches `Iverson.Vector/FilterTranslationException.cs:8` in shape; namespace `Iverson.Embeddings` is reachable from `Iverson.Api` (`Iverson.Api.csproj:32`) |
| S5 | Design 2 — `EmbeddingService` guard | ok — see R1, R3 |
| S6 | Design 3 — consumer pre-filter | ok — see R2, D1, D2, D3 |
| S7 | Design 4 — query translation | ok — see R4, D6 |
| S8 | Testing | ok — see R5; the harness at `IntelligenceStoreConsumerTests.cs:671` already captures `UpsertNamedAsync` payloads, so `chunk_index` is observable |
| S9 | Verified assumptions | → §1 |
| S10 | Out of scope | ok — `4d835c0` touched only `ingest.py` (confirmed by `git show --stat`); `IngestContractTests.cs` is absent from main and present only on `centroid-ablation`; the item-2 precondition claim checks out against `centroid-ablation:Iverson.Embeddings/EmbeddingService.cs:18` (`DocumentPrefix = ""`) |

### Rules and operands (both failure directions)

| # | Rule | Disposition |
|---|---|---|
| R1 | `IsNullOrWhiteSpace(text)` in `EmbedAsync` | ok — **over-inclusion:** would reject a legitimately whitespace-only embed; no caller wants one (all five enumerated in A15). **Under-inclusion:** `""` and `"   "` both caught; `null` also caught, though unreachable — proto3 strings are never null, `:140` null-guards, `textToEmbed` is non-null |
| R2 | `.Where(c => c.Text.Length > 0)` | ok — **over-inclusion:** drops only windows with zero non-whitespace characters, since `c.Text` is already `Trim()`ed at `:667`. **Under-inclusion:** no window with content can strip to length 0 |
| R3 | The claim that R1 and R2 "denote the same set" | ok — `string.Trim()` and `string.IsNullOrWhiteSpace` both test `char.IsWhiteSpace`, so on trimmed input `Length == 0` and `IsNullOrWhiteSpace` are the same predicate. The claim is exact, not approximate |
| R4 | Catch-clause type match and ordering | ok — and the spec already states the sharp edge: the catch-all carries `when (ex is not OperationCanceledException)`, so a misordered clause raises no CS0160 and simply never runs. Flagged in the spec; the gRPC test pins it |
| R5 | Test arithmetic — "a run of at least `maxChars + step` guarantees an empty window" | ok — hand-traced. Window starts are multiples of `step`; an all-whitespace window needs a start in `[p, p+L-maxChars]`, which contains a multiple of `step` once `L - maxChars >= step`. At 50/10 that is 200+160 = 360, as the spec says. The word-boundary branch cannot rescue the window: `text[end]` inside the run is whitespace so no adjustment fires, and if `end` lands on the run's far edge the adjustment pulls `end` *back* into the run |
| R5b | "**interior** run" as the condition for a non-contiguous assertion | ok — load-bearing and correctly stated. A leading run would drop index 0, leaving `1..N` with no internal gap and failing a non-contiguity assertion against a *correct* implementation. "Interior" guarantees window 0 (starts at content) and the final window (ends at `text.Length`) both survive, so every dropped index is strictly interior |

### Data-flow arrows (persistence boundaries flagged)

| # | Arrow → consuming operation | Disposition |
|---|---|---|
| D1 | `SplitIntoChunks` → `.Where(...)` → `chunks.Select(...)` | ok — `Index` is assigned by `index++` *inside* the iterator (`:667`), so the filter cannot renumber. `ComputeChunkPointId(parentId, fieldName, chunkIndex)` (`:696`) is pure arithmetic over the index value, not over list position |
| D2 | filtered chunk → `PrefixWithContextAsync` → `EmbedAsync` | ok — and the ordering is load-bearing exactly as the spec argues: `:628` returns `$"{prefix.Trim()}\n\n{chunkText}"`, non-empty even for empty `chunkText`, so a post-context guard would not fire. Both return paths (`:628`, `:635`) are non-empty once the input is filtered |
| D3 | `chunkResults` → `centroidInput` → `ComputeCentroid` | ok — `:470` dereferences `vectors[0]`; the `:226` field guard leaves at least one content-bearing window, and the write is additionally gated on `centroidInput.Count > 0` at `:287` |
| D4 | `chunkResults` → Qdrant upsert payload **(persistence boundary)** | ok — `chunk_index` is written as a string at `:297` and read nowhere. Its only other appearances are reserved-key lists (`SchemaBuilder.cs:26`, `SchemaRegistrationOrchestrator.cs:603`). Nothing enumerates `0..N-1` to reconstruct point ids, so an index gap is inert |
| D5 | prior ingest's chunk points → `DeleteByFilterAsync` → new write **(persistence boundary)** | ok — `:223` deletes the field's points by `parent_id AND field` *before* the write, so a window that was non-empty at T1 and dropped at T2 leaves no orphaned point. Not covered by any listed assumption; see §1 span check |
| D6 | `request.Query` → `EmbedAsync` → catch → `RpcException` → 5 clients **(transport boundary)** | ok — the status change from `Unavailable` to `InvalidArgument` is client-visible. No client test asserts `Unavailable` for a search; the only client assertions on that status are schema-registration retry tests (`Iverson.Client.Core.Tests/SchemaRegistrarTests.cs:554`, `:556`, `:571`) |
| D7 | callers → `IEmbeddingService` → implementation | ok — exactly one implementation (`EmbeddingService.cs:12`) and one registration (`ServiceCollectionExtensions.cs:21`). No null-object or disabled-mode stand-in that would bypass the guard |
| D8 | second chunk-writing path? | ok — none exists. No file under `Iverson.Api/Consumers/` other than `IntelligenceStoreConsumer.cs` references a chunks collection; `DocumentRerenderConsumer` neither embeds nor chunks |

## 1. Verified-assumptions cross-check

All 21 reconfirmed under a fresh read. Spot-notes where this round re-derived rather than re-read:

- **A1, A2, A5–A11, A13, A15–A21** — re-read at the cited lines this round; all hold as written.
- **A3** — reconfirmed: `EmbedAsync` (`:48`) is the only method issuing `POST /api/embed` (`:60`); `IEmbeddingService.cs:9` declares no other embedding entry point.
- **A12** — reconfirmed as a *reasoning* claim rather than a file fact: the catch-all's `when` filter is what removes CS0160's protection. Correct as stated.
- **A14** — reconfirmed and extended. The spec's evidence (no `EmbedAsync("")` / empty-`Query` literals) is a narrow grep, so this round checked the tests that could break *behaviourally* instead. `HandleCreated_WithMultipleVectorFields_EmbedsAllFields` (`:622`) asserts `Received(2)` but declares `ChunkFields = []` and embeds `"Hello"`/`"World"` via the object-vector path — untouched. `ChunkSplitting_ProducesMultipleChunks_ForLongText` (`:671`) uses `new string('a', 3000)`, which contains no whitespace and so loses no window. No test fixture anywhere in `Iverson.Api.Tests` builds a whitespace run (`new string(' ', …)` has zero hits).

### Span check

Five design dependencies had no covering assumption as scoped. All five were verified in-round and none is a risk; they are recorded because the spec's assumption table does not reach them.

1. **Index gaps must be tolerated system-wide, not merely within `HandleAsync`.** A7's evidence is scoped to the `chunks` local (`:329`), but the design's real dependency is that nothing anywhere reconstructs chunk point ids by enumerating indexes. Verified: `ComputeChunkPointId` has no non-test caller outside the consumer, and `chunk_index` is written but never parsed.
2. **Stale points from an earlier ingest whose window is now dropped.** No assumption covers the update case. Verified clean via `:223`'s delete-by-`parent_id`+`field` preceding the write (D5).
3. **The status change is visible to all five client languages**, while A14's evidence is scoped to `Iverson.Server/`. Verified: no client asserts `Unavailable` on a search path (D6).
4. **`Trim()` and `IsNullOrWhiteSpace` must share a whitespace definition** for §2's "denote the same set" claim. Both are defined over `char.IsWhiteSpace`; the claim is exact (R3).
5. **`IntelligenceStoreConsumer` must be the sole chunk writer**, or the fix would be partial. Verified: it is (D8).

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ **Approve as-is.** §2 and §3 are both empty. The spec is ready for implementation planning.

Two properties of this spec are worth naming as the reason the review came back clean, since they are what a plan must preserve:

- The two predicates (`IsNullOrWhiteSpace` at the backstop, `Length > 0` at the filter) are provably the same set on trimmed input, so there is no gap between guard and filter for an input to fall through.
- The spec already identifies its own unenforceable constraint — catch-clause ordering compiles clean when wrong — and assigns it to a test rather than to the compiler.
