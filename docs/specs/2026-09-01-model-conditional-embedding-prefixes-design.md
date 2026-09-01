# Model-Conditional Embedding Prefixes

**Supersedes `2026-08-26-embedding-prefixes-and-title-design.md`**, which was implemented on the
unmerged branch `embedding-prefixes-and-title` but never landed. That branch is now 69 commits
behind `main` with conflicts in ten files, including the two this repository changed on 2026-09-01.
Its content is carried forward here, re-derived against current `main` rather than merged, and its
central mechanism is changed: prefixes become configuration derived from the model, not `const`.

**Status: design, approved 2026-09-01.** Verified against `main` at `c4eb11c`.

## Context

Item 2 of `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md` asks for embedding
prefixes to become model-conditional. Its premise describes the branch, not `main`:

- It says `EmbeddingService` hard-codes `search_document: ` / `search_query: `. **`main` sends no
  prefix at all** — the constants exist only on `embedding-prefixes-and-title`.
- It says the drift gate in `IngestContractTests` already pins the document prefix. **No contract
  and no gate exist on `main`**; both are branch-only.

So this spec introduces the whole mechanism, as configuration, rather than converting constants.

**Why model-conditional rather than any single value.** Prefixes are model-specific and getting them
wrong is expensive. Running `snowflake-arctic-embed:s` under nomic's prefixes measured **0.2236**
nDCG@10 on NFCorpus against **0.3304** with its own — a 32% relative loss from four tokens of
misconfiguration, and the largest single effect measured in this project. Nothing in code or tests
noticed.

## Goal

Prefixes resolved from the configured model, overridable per deployment, applied identically by the
C# and Python ingest paths and pinned against divergence by a generated contract; the document title
composed into the embedded corpus text; and the existing 2026-08-27 measurement cited as the
evidence rather than regenerated.

Explicitly **not** goals: a model swap in any deployment; re-embedding existing tenant collections;
any RPC or client change; measuring λ; the ablation sweep.

## Base

`main` at `c4eb11c`.

## Design

### 1. Prefixes become nullable options

```csharp
public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string  BaseUrl        { get; set; } = "http://localhost:11434";
    public string  ModelId        { get; set; } = "nomic-embed-text";
    public string? DocumentPrefix { get; set; }   // null = derive from ModelId
    public string? QueryPrefix    { get; set; }   // null = derive from ModelId
}
```

**Nullable is load-bearing.** Arctic's document prefix *is* the empty string, so `""` cannot double
as "unset" — it is a legitimate configured value. `null` means derive; `""` means deliberately none.
A non-nullable `string = ""` would make arctic's correct configuration indistinguishable from an
unconfigured one, which is precisely the failure this spec exists to prevent.

`services.Configure<EmbeddingServiceOptions>` already binds the section (A1), so the new properties
bind with no additional wiring, and an unset `Embeddings__DocumentPrefix` leaves the property `null`
rather than `""`.

### 2. Derivation keyed on the model family

```csharp
public static class EmbeddingPrefixes
{
    // Ollama ids carry tags -- "snowflake-arctic-embed:s", "nomic-embed-text:latest" -- so the
    // family is everything before the first ':'.
    public static (string Document, string Query) For(string modelId);
}
```

| family | document | query |
|---|---|---|
| `nomic-embed-text` | `"search_document: "` | `"search_query: "` |
| `snowflake-arctic-embed` | `""` | `"Represent this sentence for searching relevant passages: "` |
| anything else | `""` | `""` |

Both pairs are verbatim from the branches that measured them (A5, A6), trailing spaces included.
**The class is `public`, not `internal`**, because §5's contract emit reads the table from
`Iverson.Api.Tests` and `Iverson.Embeddings` grants `InternalsVisibleTo` to nothing;
`EmbeddingServiceOptions` in the same assembly is already public for the same reason.

`EmbeddingService` resolves once at construction: `options.DocumentPrefix ?? derived.Document`.

**The resolved pair is named in the existing startup log**, not in a separate warning.
`EnsureInitializedAsync` already logs `EmbeddingService initialized: model={Model} dimension={Dim}`
(`EmbeddingService.cs:39-40`); it gains the resolved prefixes. An unfamiliar model therefore
announces the empty pair it fell back to, on the one line an operator already reads. A separate
warning would be a second thing to notice; this is the same thing, said completely.

### 3. `EmbedAsync` leaves the interface

```csharp
Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default);
Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
```

Removed, not kept alongside, so that all **89 references across 10 files** (A9) fail to compile until
consciously visited. A neutral method left in place would let a stub silently attach to it and keep
passing while covering nothing.

`EmbeddingService` keeps `EmbedAsync` **private**. Composition lives in two pure helpers,
`internal static string ComposeDocumentInput(string prefix, string text)` and
`ComposeQueryInput(string prefix, string text)`, so the contract's golden case is produced by the
real code path rather than a test-local replica. **They take the prefix as a parameter rather than
reading it**: it is resolved per instance from `IOptions` under §1, so a `static` helper has no
access to it, and an instance helper would force the golden case onto a constructed service — the
coupling the pure-helper extraction exists to avoid.

The four production call sites map unambiguously (A9):

| site | becomes |
|---|---|
| `IntelligenceStoreConsumer.cs:140` — object vector | `EmbedDocumentAsync` |
| `IntelligenceStoreConsumer.cs:252` — chunk vector | `EmbedDocumentAsync` |
| `ObjectSearchGrpcService.cs:201` — `SearchSimilar` | `EmbedQueryAsync` |
| `ObjectSearchGrpcService.cs:376` — `SearchChunks` | `EmbedQueryAsync` |

**Ordering rule: the task prefix is outermost.** When `PrefixWithContextAsync` composes contextual
chunk text, the embedded string is `search_document: {context}\n\n{chunk}`. The prefix is only
meaningful at position zero, so it is applied after context composition. Stated so nobody later
"optimises" by prefixing the chunk first.

`EnsureInitializedAsync`'s dimension probe calls the private `EmbedAsync` directly and unprefixed:
the dimension is prefix-independent, and a prefixed probe would mislead a reader into thinking the
probe is representative (A12).

`NoOpEmbeddingService` (`Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:17`) gains both methods
returning `new float[4]`, matching what it does today. It and `EmbeddingService` are the only
implementors (A8).

### 4. The empty-input guard moves — a regression this change would otherwise introduce

`EmbedAsync` currently throws `EmptyEmbeddingInputException` on empty-or-whitespace input
(shipped 2026-09-01). Left where it is, that guard becomes **inert under any non-empty prefix**:
`"search_document: " + ""` is not whitespace, so `IsNullOrWhiteSpace` never fires and an empty chunk
reaches Ollama exactly as it did before the guard existed.

The guard therefore moves into `EmbedDocumentAsync` and `EmbedQueryAsync`, testing the caller's raw
text **before** composition. The private `EmbedAsync` keeps no guard: after this change it cannot
receive empty input from anywhere.

The existing test (`EmbeddingServiceTests.cs:244`) is repointed, and **must assert the throw with a
non-empty prefix configured** — under the default empty prefix it would pass against the broken
placement too.

### 5. The contract emits the table, not a resolved value

`Iverson.Api.Tests/Schema/IngestContractTests.cs` owns a test that both writes
`Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json` (under
`IVERSON_REGENERATE_INGEST_CONTRACT=1`) and, by default, asserts a fresh emit equals the committed
copy, failing with a diff. The test locates the file by walking up from `AppContext.BaseDirectory`
to the `Iverson.slnx` marker — a new pattern in this repository, with no precedent to follow. Generator and drift gate are one artefact, which is what keeps
"generated" from decaying into "stale". Neither exists on `main` (A13); this spec creates them,
carrying forward the superseded design's mechanism, source-of-truth table and golden cases.

The prefix content departs from that design, and must:

```json
"embedding": {
  "documentPrefixes": {
    "nomic-embed-text": "search_document: ",
    "snowflake-arctic-embed": ""
  },
  "defaultDocumentPrefix": ""
}
```

**The lookup key is the model family: everything before the first `:` in the model id.** This is a
cross-language rule, not a C# detail — both sides derive the key from their own model id and must
derive it identically, or they read different rows from the same file and the drift gate sees
nothing, because the contract *data* matches while the *resolution* differs. A tagged id makes this
concrete: `nomic-embed-text:latest` unstripped matches no row, so Python would fall back to
`defaultDocumentPrefix` and write unprefixed vectors while `Iverson.Api` embeds queries prefixed.
`snowflake-arctic-embed:s` hides the same bug, because its fallback `""` is coincidentally arctic's
correct value.

**Why the table rather than one string.** The superseded design emitted a single resolved value
because the prefix was a `const` — deployment-independent by construction. It is now derived from
configuration, and the emitting test runs under *test* configuration. Emitting a resolved value
would pin whatever model the test happened to see, and `ingest.py` would then apply nomic's prefix
against an arctic stack. Emitting the mapping lets each side resolve for its own model.

**Query prefixes are deliberately not emitted.** Queries are embedded inside `Iverson.Api`, which is
C# and is itself the source; nothing Python-side would read them, and a contract field with no
consumer is dead surface.

This strictly widens the gate's reach. The superseded design had to record that "the contract pins
C#/Python agreement, not model-appropriateness" — change the model and both sides agree on the wrong
prefix. Here, changing `Embeddings__ModelId` changes what each side resolves, so a model change is
visible.

**One new golden case per family**: the composed document string for a plain chunk, obtained by
reflectively calling `ComposeDocumentInput` with that family's prefix — the
`BindingFlags.NonPublic | BindingFlags.Static` convention the contract already uses for
`ComputeChunkPointId` and `KeyToUlong`. **The golden is keyed by family, exactly as
`documentPrefixes` is**, plus one case for `defaultDocumentPrefix`. A single golden would pin
whichever family the emit happened to compose with, and `verify_contract()` — which resolves for its
own `--model` — would then fail for every other family, blocking the arctic ingest this mechanism
exists to enable. The context-composed form is not goldened, because the benchmark entity does not
enable contextual chunking.

### 6. `ingest.py`

Carried forward: delete the module-level constants (`MAX_CHARS` `:153`, `STEP` `:154`, the
collection names, and the `"Cosine"` literals at `:504` and `:509`) and load those from the
contract; **obtain the vector dimension from an Ollama probe against `--model`, not from the
contract** — the contract excludes `modelId` and `dimension` deliberately, because configuration and
a startup probe own them, so a `768` deleted without a probe to replace it leaves
`ensure_collection` with no `size` to pass. Keep the Python implementations of `split_into_chunks`,
`compute_centroid`, `key_to_ulong` and `chunk_point_id`, since the contract pins their behaviour
rather than their code; run
`verify_contract()` immediately after `parse_args()` and **before `--drop` acts** — dropping against
a drifted contract is as damaging as ingesting against one.

**Add `--model`, and wire it to the embedding call.** `ingest.py` has no `--model` argument at all
today (A17), and `embed()` hard-codes `{"model": "nomic-embed-text"}` at `:309`. The argument
selects both the Ollama model and the contract row used to resolve the document prefix, stripping
the tag to obtain the family exactly as §5 specifies; an unmatched family falls back to
`defaultDocumentPrefix`.

**The prefix is applied in one place: inside `embed()` (`:308`), the single `/api/embed` wrapper**
(A16). This matters for the reuse gate at `:369`, which decides "this document's single chunk equals
its trimmed body, so embed once" by comparing **raw** text. Applying the prefix at the embed
boundary leaves that comparison untouched and the gate valid (A18); prefixing earlier would force
the gate to reason about prefixed strings for no benefit.

**Empty chunk windows are filtered, as a side-fix this design makes necessary.** `main`'s `ingest.py`
has no such filter — the fix at `4d835c0` lives only on `centroid-ablation` (A19). Today `main` is
accidentally safe because `embed()` hard-codes nomic, whose prefix is non-empty. This spec makes an
empty document prefix reachable through configuration, and the first all-whitespace window then
kills the run at `:331`. One line, immediately after `chunks = list(split_into_chunks(body))`
(`:362`):

```python
chunks = [c for c in chunks if c[0]]
```

Dropping the window rather than renumbering preserves every surviving chunk's original index, so
`chunk_point_id` stays stable — the same rule the C# path follows.

### 7. Title composition

At corpus-build time in `sample_corpus.py`, the sole writer of `corpus.jsonl` (`:225-232`, A21):

```python
if title.strip():
    text = f"{title.strip()}\n\n{text}"   # unchanged when the title is absent or blank
```

**`.strip()` is required, not cosmetic.** SciFact titles carry trailing whitespace — the first
corpus row's title ends `"...Synaptic Input "` while the composed text reads `"...Synaptic Input\n\n"`.
Without the strip this spec would not reproduce the text that was actually measured.

**The guard and the interpolation must test and interpolate the same string.** Guarding on raw
`title` while interpolating `title.strip()` composes `"\n\n" + text` for a whitespace-only title — a
leading blank run in the embedded text. The branch that produced the corpus §8 cites hit exactly
this and records it at `sample_corpus.py:235-241`; it affected 3 documents, which matches the 3 of
200 sampled rows whose `text` does not begin with their raw `title`.

Both writers read `text` from `corpus.jsonl` — the C# path via `BenchmarkIngestScenario.cs:204`
(`Body = corpusDoc.Text`, A22), the Python path directly — so composing upstream reaches both with
no change to either, keeping them byte-identical. Composing at write time would put the same rule in
two languages, creating a fresh instance of the divergence the contract exists to eliminate.

The separate `title` field stays in `corpus.jsonl` and in the Qdrant payload for display and
filtering.

**Rejected: adding `[IversonEmbedding]` to `Title`.** A search targets exactly one embedded property
and there is no cross-vector fusion in the search path, so a `title_vector` would never be consulted
by a benchmark querying `Body`. Multi-property search is a real platform gap and a separate project.

### 8. The measurement is cited, not regenerated

**The run this spec would otherwise commission already exists.** On 2026-08-27 the branch's
implementation — nomic prefixes plus title composition — was ingested and scored over the same
5,183 documents, 300 queries and judgments, and both arms' run files are preserved beside the
baseline in `scifact-run-2026-08-26/runs/` (A24):

| | baseline | prefixed + titled | delta |
|---|---|---|---|
| `SearchChunks` nDCG@10 | 0.6820 | **0.6954** | +0.0134 |
| `SearchChunks` R@50 | 0.9160 | 0.9099 | −0.0061 |
| `SearchChunks` AP | 0.6377 | 0.6545 | +0.0168 |
| `SearchSimilar` nDCG@10 | 0.6638 | 0.6664 | +0.0026 |
| `SearchSimilar` R@50 | 0.8695 | 0.8691 | −0.0004 |
| `SearchSimilar` AP | 0.6212 | 0.6250 | +0.0038 |

Paired *t* = +1.30 on the headline figure: **not significant**. The ~2.3-point improvement predicted
from nomic's published 0.705 did not materialise, and that prediction is recorded as wrong.

**Why the result transfers to this spec's code.** At nomic defaults the derivation of §2 resolves to
the identical strings the branch hard-coded, so the composed inputs are byte-identical and the
vectors would be too. Byte-identity is proven by §5's golden case and §9's unit tests in seconds —
the same guarantee a four-hour re-ingest would give. No `--drop`; nothing irreversible.

**A hazard for anyone who does re-run it.** `scifact-run-2026-08-26/beir/corpus.jsonl` is dated
2026-08-27, a day after the baseline, and **already carries composed titles** — 197 of 200 sampled
rows have `text` beginning with `title`. The file that produced the 0.6820 baseline was overwritten;
the baseline survives only as its run files and `report.txt`, which is what a control actually needs.
Regenerating from that file rather than from the raw source would double every title. The raw BEIR
source is intact at `/home/ben/iverson-benchmark-data/scifact-full`.

### 9. Testing

**Unit, C#.** Both prefixes compose correctly for a known model; an unknown family resolves to the
empty pair; an explicit `""` override is honoured and is distinguishable from unset; the dimension
probe stays unprefixed; the empty-input guard throws **with a non-empty prefix configured** (§4).
`verify_contract()` under `--model <tagged id>` replays that family's golden and must match — this
is what asserts the C# and Python resolutions agree, and it runs on the Python side against the
contract the C# side emitted. A tagged id is required: an untagged one passes against a Python side
that never strips.
The four production call sites are asserted to use the correct method — a wrong choice raises no
error and produces only a worse number.

**The repointed references** — 89 across 10 files, concentrated in `ObjectSearchGrpcServiceTests`
(44) and `EmbeddingServiceTests` (16) — get a branch-coverage diff against a padded base, not merely
a green suite. The guarded failure is specific and has happened in this repository: a re-pointed test
keeps passing while silently losing the branch it existed to cover.

**Contract drift gate.** The emit-and-compare test covers `documentPrefixes` and the per-family
golden cases; `verify_contract()` replays its resolved family's case in `ingest.py` before `--drop`
acts, falling back to the `defaultDocumentPrefix` case for an unmatched family.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `EmbeddingServiceOptions` binds from `"Embeddings"`; new properties need no wiring | `Iverson.Embeddings/ServiceCollectionExtensions.cs:10` — `services.Configure<…>(config.GetSection(Section))` |
| A2 | Nothing constructs the options in a way new properties break | `EmbeddingServiceTests.cs:31` uses an object initializer setting `ModelId` only |
| A3 | An unset `Embeddings__DocumentPrefix` leaves the property `null`, not `""` | Standard `IConfiguration` binding over a `string?` property; no default assigned |
| A4 | Model ids carry `:` tags; the family precedes it | `docker-compose.yml:372,457` set `nomic-embed-text`; the experiments used `snowflake-arctic-embed:s` |
| A5 | Arctic's pair is `""` / `"Represent this sentence for searching relevant passages: "` | `centroid-ablation:Iverson.Embeddings/EmbeddingService.cs:18-19` |
| A6 | Nomic's pair is `"search_document: "` / `"search_query: "` | `embedding-prefixes-and-title:Iverson.Embeddings/EmbeddingService.cs:18-19` |
| A7 | A startup path exists where the resolved pair is logged | `Program.cs:241` `AddEmbeddings(cfg)`; `EmbeddingService.cs:39-40` already logs model and dimension |
| A8 | Exactly two implementors | `EmbeddingService.cs:12`; `Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:17` |
| A9 | 89 `EmbedAsync` references across 10 files; 4 production call sites | Per-file counts: `ObjectSearchGrpcServiceTests` 44, `EmbeddingServiceTests` 16, `IntelligenceStoreConsumerTests` 15, `ObjectSearchVectorIntegrationTests` 4, impl 3, consumer 2, gRPC service 2, interface 1, `NoOpEmbeddingService` 1, `DocumentTemplateValidationTests` 1 |
| A10 | Nothing outside `Iverson.Server/` calls `EmbedAsync` | No hits in `Iverson.Clients/` |
| A11 | The guard's test can be repointed without losing coverage | `EmbeddingServiceTests.cs:244` asserts the throw; §4 strengthens it with a configured prefix |
| A12 | The probe must stay unprefixed | `EmbeddingService.cs:37` — `EmbedAsync("probe", ct)`, private after §3 |
| A13 | No contract or drift gate on `main` | `ingest-contract.json` exists only under the `embedding-prefixes-and-title` worktree; no `IngestContract` symbol in `main` |
| A14 | `Iverson.Api.Tests` references `Iverson.Embeddings` | `Iverson.Api.Tests.csproj:35` |
| A15 | A contract location both sides can resolve | Branch precedent: `Iverson.LoadTest/scripts/ingest-contract.json` beside `ingest.py`, with the C# test walking up to the `Iverson.slnx` marker |
| A16 | `embed()` is the only `/api/embed` caller | `ingest.py:308` (`:331` is that call's error message) |
| A17 | **Failed as listed** — `ingest.py` has no `--model` argument at all, and `embed()` hard-codes the model | `grep '--model' ingest.py` returns nothing; `ingest.py:309` posts `{"model": "nomic-embed-text"}`. §6 adds the argument rather than wiring an existing one |
| A18 | The reuse gate compares raw text | `ingest.py:369` — `reuse = body == body.strip() and len(body) <= STEP` |
| A19 | `4d835c0`'s filter applies to `main`'s shape | `ingest.py:362` — `chunks = list(split_into_chunks(body))`; `git branch --contains 4d835c0` returns only `centroid-ablation` |
| A20 | The constants replaced by the contract **and by the probe** exist | `MAX_CHARS` `:153`, `STEP` `:154`, `OLLAMA_URL` `:116`, `768`/`"Cosine"` `:504`, `:509`; the contract carries `distance` but no dimension |
| A21 | `sample_corpus.py` is the sole `corpus.jsonl` writer | `:225-232` |
| A22 | The C# path reads `text` into `Body` | `BenchmarkIngestScenario.cs:204` |
| A23 | **Failed as listed** — the premise "the title is never embedded" holds for `main`'s *code*, but the corpus artifact on disk is already composed, and composition strips the title | `scifact-run-2026-08-26/beir/corpus.jsonl` dated 2026-08-27, 197/200 sampled rows have `text` starting with `title`; row 1's title ends with a trailing space the composed text lacks. Drives §7's `.strip()` and §8's hazard note |
| A24 | The baseline and prefixed-titled artifacts both survive | `scifact-run-2026-08-26/runs/{direct-ingest-baseline,prefixed-titled}.{chunks,similar}.trec`, all 15,000 rows / 300 queries; `report-prefixed-titled.txt` carries both arms |
| A27 | Ingest throughput on the direct path | `keymap.json.stats.json` — 2.649 s/document, 7,950 embed calls, 3,820 saved by the reuse gate |
| A28 | No reflection or DI-by-name on `EmbedAsync` | No `GetMethod("Embed…")` or `nameof(…EmbedAsync)` anywhere |

## Known issues, accepted

**Pre-existing collections hold prefix-less, title-less vectors.** Ben's decision, carried forward
from 2026-08-26: any collection written before this change holds vectors that are stale in exactly
the way a model change makes them stale, and must be re-embedded to be comparable. No migration
tooling is built; this is a dev stack with no production data to protect.

**The cited delta is combined and not attributable between prefixes and title.** Separating them
costs two further full ingests to explain a result that is not significant either way.

**`corpus.jsonl` is not a verbatim BEIR corpus file** — its `text` carries the title. Deliberate
benchmark preparation, stated here rather than left to be discovered by diffing against upstream.

**Centroid numerics differ between the pipelines, independently of this work.** `ComputeCentroid`
sums into `float[]` with `MathF.Sqrt` while `ingest.py` computes in float64, so the two produce
centroids differing at roughly 1e-7. The golden centroid check states a tolerance rather than
asserting exact equality. Far below anything that reorders a result set.

**The contract pins the settings and the goldened algorithms, not all behaviour.** A divergence in a
Python code path with no golden case remains undetectable.

**The derivation table is a hard-coded list of two model families.** A third model requires either a
code change or explicit configuration. Configuration is the escape hatch, which is why the override
exists; the table is not designed to be exhaustive.

## Out of scope

- **Landing `embedding-prefixes-and-title` or `centroid-ablation`.** This spec re-derives against
  `main`; those branches remain unmerged and are not touched.
- **Retiring the superseded spec's artifacts.** `2026-08-26-embedding-prefixes-and-title-design.md`
  and its plan carry the supersession note added here; nothing is deleted.
- **Measuring arctic on SciFact.** The mechanism makes it expressible; running it is a separate
  decision.
- **Multi-property search and cross-vector fusion.** A separate project.
