# Embedding Task Prefixes and Title Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-26-embedding-prefixes-and-title-design.md` (commit SHA: `dbc5fb6`)

**Goal:** Write prefixed, title-bearing vectors identically from the C# and Python paths, with the C#-owned constants generated into a contract `ingest.py` reads, then re-measure BEIR SciFact against the 0.6820 / 0.6638 baseline.

**Architecture:** `IEmbeddingService` gains `EmbedDocumentAsync`/`EmbedQueryAsync` and loses `EmbedAsync`, so every call site declares its side and nothing compiles until visited. Composition lives in pure helpers a test can call. A test in `Iverson.Api.Tests` emits `ingest-contract.json` from the real C# code paths and fails when the committed copy drifts; `ingest.py` reads that contract instead of repeating its constants. Title composition happens once, in `sample_corpus.py`, so both writers inherit it unchanged.

**Tech stack:** .NET 10, xunit + NSubstitute + FluentAssertions, Qdrant.Client, Ollama (`nomic-embed-text`, 768 dims), stdlib-only Python 3.14.

---

## Global Constraints

Project-wide rules every task must hold to, copied from the spec:

- **Both halves ship together.** Prefixed documents queried without `search_query: ` place the query vector in a different region than the corpus vectors, which is plausibly worse than prefixing neither. No task may land the document side without the query side.
- **The task prefix is outermost.** When contextual chunking composes chunk text, the embedded string is `search_document: {context}\n{chunk}`. The prefix is applied *after* context composition, never before.
- **`queryPrefix` is never emitted into the contract.** Nothing Python-side reads it; queries are embedded inside `Iverson.Api`, which is the constant's own source.
- **The contract pins C#/Python agreement, not model-appropriateness.** Changing `Embeddings__ModelId` to a model with different conventions leaves both sides agreeing on the wrong prefix. The contract cannot detect that.
- **Commit messages:** lowercase imperative, no Conventional-Commits prefix (verified against `git log --oneline -12`).

## File Structure

**Modify**
- `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs` — swap `EmbedAsync` for the two intent-named methods
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs` — constants, the two pure compose helpers, `EmbedAsync` made private
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — two document call sites; `ChunkWindow` extraction
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — two query call sites
- `Iverson.Server/Iverson.Vector/IntelligenceCollectionManager.cs` — one distance constant, two call sites
- `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` — `NoOpEmbeddingService` gains the two methods
- `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` and four `Iverson.Api.Tests` files — the 75 repointed references
- `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` — add the `Iverson.LoadTest` project reference
- `Iverson.Server/Iverson.LoadTest/scripts/ingest.py` — consume the contract, prefix inside `embed()`
- `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py` — title composition

**Create**
- `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs` — the emit and the drift gate, one artefact
- `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json` — the committed contract

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and re-confirmed across two CDR rounds. **Not re-verified here.**

| # | Assumption | Evidence |
|---|---|---|
| A1 | Exactly two implementors of `IEmbeddingService` | `EmbeddingService.cs:12`; `StartupNoOpFakes.cs:17` |
| A2 | Removing `EmbedAsync` from the interface breaks every caller at compile time | All four call sites resolve through the injected interface |
| A3 | The four production call sites are the only places text is embedded | `IntelligenceStoreConsumer.cs:140,244`; `ObjectSearchGrpcService.cs:197,368` |
| A4 | The probe can use a private raw embed; dimension is prefix-independent | `EmbeddingService.cs:37` |
| A5 | `Iverson.Embeddings` constants reachable from `Iverson.Api.Tests` | `Iverson.Api.Tests.csproj:35` |
| A6 | `BenchmarkDocument` does not use contextual chunking | No `Contextual` on the entity; `IntelligenceStoreConsumer.cs:233` also gates on enrichment being enabled |
| A7 | `ingest.py` has one function wrapping `/api/embed` | `ingest.py:307` |
| A8 | The reuse gate compares raw text before embedding | `ingest.py:369` |
| A9 | `sample_corpus.py` is the sole writer of `corpus.jsonl` | `sample_corpus.py:225` |
| A10 | The C# path maps corpus `text`→`Body`, `title`→`Title` | `JsonlCorpusParser.cs:23-24,38`; `BenchmarkIngestScenario.cs:203-204` |
| A11 | Queries and qrels are unaffected by title composition | `sample_corpus.py:237` |
| A12 | `--drop` has a drop-only path that exits before ingesting | `ingest.py:55-61` (documented); implementation at `:483,485,491` |
| A13 | Baseline figures are recorded retrievably | `scifact-run-2026-08-26/report.txt` |
| A14 | Nothing else consumes `EmbedAsync`; no prefix string exists in the repo | Full non-test grep |
| A15 | The superseded design's V1–V19 still hold post-merge | `Iverson.Api.csproj:10-13`; `Iverson.LoadTest.csproj:10-11` |
| A16 | `Iverson.Api.Tests` does not yet reference `Iverson.LoadTest`; adding it creates no cycle | `Iverson.Api.Tests.csproj:30-35` |
| A17 | Prefix + a full chunk stays inside the model's context | ~390 of a 2,048-token context |
| A18 | Title composition raises ingest cost ~7% (~5.1 h) | Measured over the real corpus |
| A19 | `report.py` needs no change | Absolute figures; comparison is spec-level |
| A20 | The `title` payload field stays populated | Sourced from the separate `title` field |
| A21 | No proto or client change | No RPC signature changes |
| A22 | Ollama injects no prefix of its own | `/api/show` — `template` is `{{ .Prompt }}` |
| A23 | Re-running `sample_corpus.py` selects the same document set | `sample_corpus.py:208-209`, `:218` |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The four modified C# files exist at the cited paths | `find` returned all four |
| P2 | File path | `ingest-contract.json` does not exist yet (Task 2 creates it) | `ls` returned nothing |
| P3 | File path | The 75 references live in exactly 6 files | `EmbeddingServiceTests.cs`, `IntelligenceStoreConsumerTests.cs`, `ObjectSearchVectorIntegrationTests.cs`, `ObjectSearchGrpcServiceTests.cs`, `StartupNoOpFakes.cs`, `DocumentTemplateValidationTests.cs` |
| P4 | File path | `IngestContractTests.cs` collides with no existing file | `Iverson.Api.Tests/Schema/` listing |
| P5 | Signature | `SplitIntoChunks` is `private static IEnumerable<(string Text, int Index)>(string, int, int)` with one call site | `IntelligenceStoreConsumer.cs:647` decl, `:229` sole call |
| P6 | Signature | `ToCollectionSchema` / `ToChunkCollectionSchema` / `SqlTypeToPayloadKind` are `internal static` | `SchemaBuilder.cs:332,313,432` |
| P7 | Signature | `ResolveCollectionName(string baseName, string? tenantId, bool isChunks)` is public | `IntelligenceTenantScope.cs:11` |
| P8 | Signature | `EntityRegistry.Get<T>()` is public; `SchemaRegistrar.BuildTypeDescriptor` is `private static` | `EntityRegistry.cs:105`; `SchemaRegistrar.cs:52` |
| P9 | Signature | Golden targets and their accessibility: `ComputeCentroid` and `KeyToUlong` are `internal static`; `ComputeChunkPointId` and `SplitIntoChunks` are `private static` | `IntelligenceStoreConsumer.cs:470,677,696,647` |
| P10 | Signature | `Distance` is `Qdrant.Client.Grpc.Distance`; `Distance.Cosine` appears at exactly two real call sites | `IntelligenceCollectionManager.cs:3-4,35,230`; the third repo hit is a comment |
| P11 | Command | Two test projects exist: `Iverson.Api.Tests` and `Iverson.Embeddings.Tests` | `.csproj` files present |
| P12 | Command | `IVERSON_REGENERATE_INGEST_CONTRACT` collides with no existing env var | `IVERSON_*` sweep shows only `ACTING_USER_*`, `CLIENT_ID`, and siblings |
| P13 | Command | No Python test runner exists, so script changes are verified by direct invocation | No `pytest.ini`, `pyproject.toml`, `setup.cfg`, `tox.ini` |
| P14 | Ordering | `Iverson.LoadTest` references only `Iverson.Client.Core` and `Iverson.Events`, so `Iverson.Api.Tests → Iverson.LoadTest` creates no cycle and drags in no ASP.NET host | `Iverson.LoadTest.csproj:10-11` |
| P15 | Code validity | **`Iverson.Embeddings` grants NO `InternalsVisibleTo`.** The emit must reach `ComposeDocumentInput` by **reflection**, unlike `ComputeCentroid`/`KeyToUlong`, which `Iverson.Api`'s IVT to `Iverson.Api.Tests` makes directly callable | No `InternalsVisibleTo` in `Iverson.Embeddings.csproj`; `Iverson.Api.csproj:10-11` grants to `Iverson.Api.Tests` |
| P16 | Consumer impact | No `EmbedAsync` consumer exists outside `Iverson.Server` | Grep across `Iverson.Clients` and `Iverson.LoadTest` returned nothing |
| P17 | Sweep (C#) | Every C# identifier the plan names resolves with the accessibility assumed — `SplitIntoChunks`, `ToCollectionSchema`, `ToChunkCollectionSchema`, `SqlTypeToPayloadKind`, `ResolveCollectionName`, `EntityRegistry.Get<T>`, `BuildTypeDescriptor`, `ComputeChunkPointId`, `KeyToUlong`, `ComputeCentroid`, `IsZeroMagnitude`, `NoOpEmbeddingService`, `BenchmarkDocument`, `Distance.Cosine` | Per-symbol greps recorded in P5–P10 |
| P18 | Sweep (Python) | Every Python identifier the plan names exists as named, and `verify_contract` does **not** yet exist | `ingest.py` sweep: 13 present, `verify_contract` 0 occurrences |
| P19 | Code validity | `Iverson.LoadTest` is `OutputType Exe`; referencing it from a test project is supported, and `BenchmarkDocument` is public so the emit needs no IVT from it | `Iverson.LoadTest.csproj:3` (`<OutputType>Exe</OutputType>`, `net10.0`); `BenchmarkDocument.cs` declares `public sealed class` |
| P20 | Code validity | `SchemaRegistrar` and `EntityRegistry` live in `Iverson.Clients/DotNet/Iverson.Client.Core/`, not `Iverson.Server`; Task 2 Step 5 reaches them **transitively** through the `Iverson.LoadTest` reference Step 3 adds. Marking that reference `PrivateAssets="all"` would break the emit with no other symptom | `SchemaRegistrar.cs:52`, `EntityRegistry.cs:105` are under `Iverson.Clients/DotNet/Iverson.Client.Core/`; `Iverson.LoadTest.csproj:10` references it; `Iverson.Api.Tests.csproj:30-35` does not |

## Tasks

### Task 1: The C# prefix contract

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs`, `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:140,244`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:197,368`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:17-24`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` plus the four `Iverson.Api.Tests` files in P3

**Interfaces:**
- Produces: `EmbeddingService.DocumentPrefix` / `QueryPrefix` (`public const`), and `ComposeDocumentInput` / `ComposeQueryInput` (`internal static`) — Task 2's emit reads both.

- [ ] **Step 1: Add the constants and the pure compose helpers to `EmbeddingService`.**
```csharp
public const string DocumentPrefix = "search_document: ";
public const string QueryPrefix    = "search_query: ";

internal static string ComposeDocumentInput(string text) => DocumentPrefix + text;
internal static string ComposeQueryInput(string text)    => QueryPrefix + text;
```
They are pure and separately named so Task 2's emit can call the real composition rather than re-implementing it. Keep them free of any HTTP or state.

- [ ] **Step 2: Write the composition tests first.** In `EmbeddingServiceTests.cs`, assert `ComposeDocumentInput("x") == "search_document: x"` and `ComposeQueryInput("x") == "search_query: x"`, including that the trailing space is present. Then assert the dimension probe is unprefixed — the request body captured by the existing stubbed handler for `EnsureInitializedAsync` must contain `"probe"` and not `"search_document: probe"`.

- [ ] **Step 3: Change `IEmbeddingService`.** Add `EmbedDocumentAsync` and `EmbedQueryAsync`, both `Task<float[]>(string text, CancellationToken ct = default)`. **Remove `EmbedAsync` from the interface.** Removing it is what forces every stub and call site to be visited; leaving it would let a stub silently attach to a method nothing calls.

- [ ] **Step 4: Implement them in `EmbeddingService`.** Make the existing `EmbedAsync` private and unchanged. The two public methods call the matching `Compose*Input` and pass the result to it. `EnsureInitializedAsync` keeps calling the private `EmbedAsync("probe", ct)` directly.

- [ ] **Step 5: Repoint the four production call sites.** `IntelligenceStoreConsumer.cs:140` and `:244` → `EmbedDocumentAsync`; `ObjectSearchGrpcService.cs:197` and `:368` → `EmbedQueryAsync`. At `:244` the argument stays `textToEmbed`, the already-context-composed string — this is what makes the prefix outermost. Do not move the prefix inside `PrefixWithContextAsync`.

- [ ] **Step 6: Add the two methods to `NoOpEmbeddingService`,** each returning `Task.FromResult(new float[4])`, matching what its `EmbedAsync` does today. Delete its `EmbedAsync`.

- [ ] **Step 7: Repoint the 75 test references across the six files in P3.** Each is a stub or assertion on `EmbedAsync`; move it to whichever method the code under test now calls. **Do not add a stub for a method the test's subject does not call** — an over-broad stub is how a re-pointed test keeps passing while covering nothing.

- [ ] **Step 8: Run both suites and take a branch-coverage diff for the repointed files against a padded base.** Line coverage is blind to a re-pointed test losing a branch; the branch diff against a padded base is what catches it. Any branch present in the base and absent after is a repoint that must be corrected, not accepted.
```bash
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 9: Commit.**
```bash
git add Iverson.Server/Iverson.Embeddings Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests Iverson.Server/Iverson.Embeddings.Tests
git commit -m "send nomic task prefixes on every embedding path"
```

### Task 2: Contract generation

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:647` (`ChunkWindow` extraction)
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceCollectionManager.cs:35,230` (distance constant)
- Modify: `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` (add the `Iverson.LoadTest` reference)
- Create: `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json`

**Interfaces:**
- Consumes: Task 1's `DocumentPrefix` and `ComposeDocumentInput`.
- Produces: `ingest-contract.json` — Task 3 reads it.

- [ ] **Step 1: Extract `ChunkWindow`.** The window arithmetic is inline in `SplitIntoChunks` and unreachable from a test. Lift it to `internal static (int MaxChars, int Step, int Lookback) ChunkWindow(int maxTokens, int overlap)`, which `SplitIntoChunks` then calls. Behaviour is unchanged; `SplitIntoChunks` has exactly one call site (P5) and no test binds it by name.

- [ ] **Step 2: Introduce one distance constant in `Iverson.Vector`** and point both `Distance.Cosine` literals (`:35`, `:230`) at it. This removes an existing duplication rather than adding one, and makes the contract complete instead of complete-except-one-field.

- [ ] **Step 3: Add the `Iverson.LoadTest` project reference to `Iverson.Api.Tests.csproj`.** The reference direction is `Iverson.Api.Tests → Iverson.LoadTest`, which creates no cycle (P14). It does pull Dapper, Npgsql, MySqlConnector and Confluent.Kafka in transitively; that is accepted, not a problem to solve.

- [ ] **Step 4: Write `IngestContractTests.cs` as one artefact that both emits and gates.** Under `IVERSON_REGENERATE_INGEST_CONTRACT=1` it writes `ingest-contract.json`; by default it emits fresh and asserts equality with the committed copy, failing with a diff. Generator and drift gate being the same test is what keeps "generated" from decaying into "stale". Locate the file by walking up from `AppContext.BaseDirectory` to the `Iverson.slnx` marker — a new pattern in this repo, stated here because no precedent exists to follow.

- [ ] **Step 5: Source each contract field from the real code.** Derive the descriptor from the real `BenchmarkDocument` via `new EntityRegistry([typeof(BenchmarkDocument).Assembly]).Get<BenchmarkDocument>()` and `SchemaRegistrar.BuildTypeDescriptor` (reflection — it is `private static`, P8), so entity drift fails the gate. Take vectors and payload indexes from `ToCollectionSchema` / `ToChunkCollectionSchema`, the naming rule from `ResolveCollectionName`, the window from `ChunkWindow`, and the distance from Step 2's constant.

  **`documentPrefix` comes from `EmbeddingService.DocumentPrefix` read directly** — it is `public const` and `Iverson.Api.Tests` already references `Iverson.Embeddings` (A5). **`queryPrefix` is not emitted** (Global Constraints). Neither `modelId` nor `dimension` is emitted: configuration and a startup probe own those.

- [ ] **Step 6: Emit the golden cases.** Five chunking cases — shorter than the window; exactly at the boundary; multi-chunk with overlap; word-boundary extension fires; word-boundary extension must not fire (no space in the last 50 chars, the class of the divergence that already happened at `4771286`). Plus point ids for a GUID key, a centroid over fixed 4-dimensional synthetic vectors with a stated tolerance, and **the composed document string**.

  **The prefix golden must be obtained by reflectively calling `ComposeDocumentInput`, not by concatenating in the test.** `Iverson.Embeddings` grants no `InternalsVisibleTo` (P15), so a direct call will not compile — and a test-local concatenation would pin the test against Python rather than production against Python, which is the whole point of the golden. Note the asymmetry: `ComputeCentroid` and `KeyToUlong` *are* directly callable, because `Iverson.Api` does grant IVT to `Iverson.Api.Tests`.

  No non-GUID point-id case: `KeyToUlong`'s FNV branch is documented unreachable, and goldening it would pin dead code.

- [ ] **Step 7: Generate the contract, run the gate, and confirm it fails on drift.** Regenerate, run the suite clean, then hand-edit one committed value, confirm the test fails with a diff, and revert. A gate never seen to fail is not known to be a gate.
```bash
IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 8: Commit.**
```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Vector/IntelligenceCollectionManager.cs Iverson.Server/Iverson.Api.Tests Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json
git commit -m "generate the ingest contract from the real C# write path"
```

### Task 3: `ingest.py` consumes the contract

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/ingest.py`

**Interfaces:**
- Consumes: Task 2's `ingest-contract.json`.

- [ ] **Step 1: Load the contract and delete the constants it replaces.** Remove `MAX_CHARS`, `STEP`, `DEFAULT_OBJECT_COLLECTION`, `DEFAULT_CHUNKS_COLLECTION`, `OBJECT_PAYLOAD_INDEXES`, `CHUNKS_PAYLOAD_INDEXES`, and the hardcoded `768` / `"Cosine"` at `:504,509`. Resolve the contract relative to `__file__`. `split_into_chunks`, `compute_centroid`, `key_to_ulong` and `chunk_point_id` **keep their Python implementations** — the contract pins their behaviour, not their code.

  **The word-boundary lookback is not a module-level constant and needs a behavioural change, not a deletion.** `split_into_chunks` hardcodes it inline as `count = min(end - start, 50)`. It must come from the contract's `chunkWindow.wordBoundaryLookback` instead — either widen the signature the way `max_chars` and `step` already are (`def split_into_chunks(text, max_chars=..., step=..., lookback=...)`) or read it from the loaded contract inside the function. Left as-is, the contract emits a field no consumer reads while the one constant with a history of C#/Python divergence (`4771286`) stays duplicated.

  **Where the contract loads is constrained, not free.** `split_into_chunks` binds `MAX_CHARS` and `STEP` as **default parameter values**, which Python evaluates at function-definition time. Removing them and loading the contract inside `main()` — or lazily, or in a helper called from `main()` — raises `NameError` at import, before `parse_args()` and before `verify_contract()` can report anything. Either load the contract at module scope above `split_into_chunks`, binding the same names, or drop the module-level defaults from the signature and pass the window in from the caller. This failure is loud and immediate rather than silent, so it costs minutes; it is stated here because Step 1 otherwise points at the broken shape as readily as the working one.

- [ ] **Step 2: Apply the prefix inside `embed()` (`:307`), and nowhere else.** The reuse gate at `:369` compares **raw** text (`body == body.strip() and len(body) <= STEP`); prefixing at the embed boundary leaves that comparison untouched and the gate valid, where prefixing earlier would force the gate to reason about prefixed strings for no benefit. The 4,180 saved embed calls must stay saved.

- [ ] **Step 3: Add `--model` (default `nomic-embed-text`) and probe Ollama for the dimension,** the way `EmbeddingService.EnsureInitializedAsync` does. Document `--model` as needing to match the API's `Embeddings__ModelId`. This replaces the hardcoded `768` and makes the Python and query paths agree by construction rather than by two literals happening to match.

- [ ] **Step 4: Add `verify_contract()` and call it immediately after `parse_args()` (`:483`) and before `--drop` acts (`:491`).** It replays the golden cases — the five chunking cases, the point ids, the centroid within tolerance, and the prefix composition — and exits non-zero on mismatch, printing expected and actual. Dropping collections against a drifted contract is as damaging as ingesting against one, and a five-hour run must not begin on one.

- [ ] **Step 5: Verify by direct invocation.** No Python test runner exists in this repo (P13), so: run `--help`; run `verify_contract()` against the committed contract and confirm it passes; hand-edit one golden value, confirm a non-zero exit naming the mismatch, and revert; confirm a `--drop` invocation against throwaway collection names still exits before ingesting.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/ingest.py
git commit -m "read the ingest contract instead of repeating its constants"
```

### Task 4: `sample_corpus.py` title composition

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py:225-232`

- [ ] **Step 1: Compose the title into the emitted `text`.** Where the corpus row is written, emit `f"{title}\n\n{text}"` when a title is present and `text` unchanged when it is not. Keep the separate `title` field exactly as it is — the Qdrant payload and the C# `Title` property both read it, and both must keep working (A10, A20).

- [ ] **Step 2: Record the consequence in the script's docstring.** The emitted `corpus.jsonl` is no longer a verbatim BEIR corpus file, because its `text` carries the title too. State it where a reader meets the file, not only in the spec, so nobody discovers it by diffing against upstream.

- [ ] **Step 3: Verify by direct invocation.** Regenerate a small slice with `--target-size`, and confirm a written row's `text` begins with its `title` and that `title` is still present and unmodified.

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py
git commit -m "compose the document title into the embedded corpus text"
```

### Task 5: The measured run

**Files:** none — this task produces measurements, not code.

**Interfaces:**
- Consumes: Tasks 1–4.

**Paths** (both live outside the repository and are not discoverable from it):
- Full SciFact download — `/home/ben/iverson-benchmark-data/scifact-full/` — a flat BEIR directory holding `corpus.jsonl`, `queries.jsonl` and `qrels/test.tsv`. This is Step 1's `--corpus-dir`.
- Run directory — `/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/` — Step 1's `--out-dir`, the parent of `ingest.py --corpus`'s `beir/corpus.jsonl`, `benchmark-query`'s `--output-dir` (its `runs/`), and `report.py --run`'s argument. It already holds the baseline's `qrels.trec`, `keymap.json` and `runs/direct-ingest-baseline.*.trec`.

- [ ] **Step 1: Regenerate the corpus.** Re-run `sample_corpus.py` with the `--corpus-dir` and `--out-dir` above. **The title composition of Task 4 takes effect here and nowhere else.**

- [ ] **Step 2: Confirm the regenerated `text` carries the title,** by inspecting one row. This step exists because skipping step 1 fails silently: the ingest would succeed, every structural check would pass, and the result would be prefix-only while being reported as prefix + title.

- [ ] **Step 3: Drop and ingest.** `stack.py ingest`, then `ingest.py --drop`, then the full ingest. **Checkpoint before proceeding:** the reported document count equals 5,183 and a Qdrant point count confirms it. Expect roughly 6,400 chunks and ~7,750 embed calls at ~5.1 hours (A18) — materially different figures mean something upstream is wrong, not that the estimate was off.

  Dropping the collection is **irreversible** and destroys the current vectors. Confirm with the user before running it.

- [ ] **Step 4: Query.** `stack.py query`, then `benchmark-query` with `--config-label prefixed-titled`. The distinct label is load-bearing, not cosmetic: reusing `direct-ingest-baseline` would overwrite the control's run files, and step 3's drop removes any way to regenerate them.

- [ ] **Step 5: Score and report.** Run `report.py --run <runs-dir>` over the directory so both runs are scored side by side, redirecting to a new report file (`report.py` has no output flag — A19). Report `nDCG@10`, `R@50` and `AP` for both RPCs against the baseline's 0.6820 / 0.9160 / 0.6377 and 0.6638 / 0.8695 / 0.6212, and state plainly that the delta is **combined** across prefixes and title and is not attributable between them.

## Tasks NOT in this plan

Inherited from the spec's stated non-goals. A new spec → plan cycle is required to add any of these:

the ablation sweep; measuring λ; changing any RPC signature or client; re-embedding existing tenant collections; a model swap.

## Known issues inherited from spec

These exist by design — accepted during brainstorming.

**Pre-existing collections hold prefix-less, title-less vectors.** Ben's decision, 2026-08-26: this work re-ingests the benchmark collection only. Any other collection written before this change holds vectors that are now stale in exactly the way a model change makes them stale, and must be re-embedded to be comparable. No migration tooling is built, because this is a dev stack with no production data to protect.

**The measured delta is not attributable between the two changes.** Prefixes and title are both ingest-side, so separating them costs a second and third full ingest — roughly 10 extra hours to explain a result that, if it lands well, needs no explanation. Attribution is spent only if the combined result disappoints.

**`corpus.jsonl` is no longer a verbatim BEIR corpus file** — its `text` carries the title too.

**Centroid numerics differ between the pipelines, independently of this work.** `ComputeCentroid` sums into `float[]` with `MathF.Sqrt` while `ingest.py` computes in float64, so the two produce centroids differing at roughly 1e-7. The golden centroid check states a tolerance rather than asserting exact equality. Far below anything that reorders a result set. Accepted by Ben.

**Locating the contract file from a test is a new pattern.** The test walks up from `AppContext.BaseDirectory` to the `Iverson.slnx` marker. No precedent exists in this repo.

**The contract pins settings and the goldened algorithms — not all behaviour.** A divergence in a Python code path with no golden case remains undetectable.
