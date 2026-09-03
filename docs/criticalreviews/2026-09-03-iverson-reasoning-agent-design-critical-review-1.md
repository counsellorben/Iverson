# Critical Design Review: 2026-09-03-iverson-reasoning-agent-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-03-iverson-reasoning-agent-design.md` (committed at `main@53c81d7`; spec self-declares verification at `main@7fb527b`)
**Verified Assumptions section:** present (§9, 23 items)

All file:line references below are against the working tree at `main@53c81d7`, read directly; the Anthropic SDK claims were checked against the bundled `claude-api` skill files under `/tmp/claude-1000/bundled-skills/2.1.257/84ccb932f158c226e3235b7cec5dd15f/claude-api/`.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| §1 Purpose and scope | ok — scope is a Python agent over `iverson_client`; no server/client change proposed; checked no section below contradicts that (none does) |
| §2 Corpus modelling | ok — every decorator in the code block exists with the kwargs used (`annotations.py:133-179`, `:222`); `contextual`, `description`, `metadata`, `summary`, `keywords` all reach `PropertyDescriptor`/`TypeDescriptor` (`core.py:269-293`, `:329`); the "filter only on PK or metadata, EQUALS only" claim matches `ObjectSearchGrpcService.cs:869-913`; `EnrichmentConsumer.BuildSourceText` concatenates embedding + chunk fields (`EnrichmentConsumer.cs:297`) |
| §3.1 Two identities | ok — `IversonClientCredentials(client_id, client_secret, token_endpoint)` at `auth.py:17-21`; `ACTING_USER_METADATA_KEY` header emitted by `EntityCoordinator._acting_user_metadata` (`core.py:630-636`) |
| §3.2 Wiring | ok — `IversonClient.__init__(host, port, use_tls, *, credentials, acting_user_token)` at `core.py:821-832`; `coordinator()` + `with_acting_user()` at `core.py:881`, `:624-628`; `search_chunks`/`get_many`/`search_similar`/`search` all pass the metadata (`core.py:748-785`) |
| §3.3 Per-user schema | ok — `get_schema()` is on `IversonClient` and uses the constructor token only (`core.py:885-891`); context-manager protocol exists (`core.py:875-880`); `GetSchema` drops denied types and filters fields by `AllowedFields` (`ObjectMappingGrpcService.cs:79-89`) |
| §3.4 Consequences table | ok — row 1: `SearchChunks` denied → `return` (`ObjectSearchGrpcService.cs:337-339`), `GetMany` denied → `Found=false` (`ObjectRetrievalGrpcService.cs:96-102`), `GetSchema` → `continue` (`:81`); row 2: masked metadata clause → `InvalidArgument` "not authorized" (`ObjectSearchGrpcService.cs:895-897`); row 3: masked chunk field → `InvalidArgument` (`:350-352`) |
| §3.5 Reasoning model | ok — `claude-opus-5` is the id used throughout `python/claude-api/README.md`; adaptive thinking is the default when `thinking` is omitted on Opus 5 (`README.md:253`); `refusal` stop reason exists (`README.md:429`) |
| §4.1 Plan | → §2.2 (field-name spelling the validator compares against) — otherwise ok: `client.messages.parse(..., output_format=Model).parsed_output` matches `tool-use.md:516-527`; `anyOf` (Pydantic `str \| float \| bool`) is a supported schema construct (`shared/tool-use-concepts.md:491`); `SchemaField` carries `name`, `description`, `clr_type`, `is_metadata` (`object_mapping.proto:139-152`) |
| §4.2 Locate | ok — `SearchChunksRequest` has `filter` and `filter_logic` (`object_search.proto:113-124`; generated pb2 field list confirmed by import); `pb.EQUALS`/`pb.FILTER`/`pb.AND` resolve in `object_search_pb2`; `_to_search_value` is importable from `iverson_client.search:38-53`; `ChunksBuilder.where` raises on a second clause (`vector_search.py:79-80`); server ANDs every clause into `Filter.Must` and ignores `filter_logic` (`:878-908`); no parent dedup and MMR-ordered streaming confirmed at `:480-503`; `OverFetchFactor = 4` at `:746` |
| §4.3 Assemble | → §2.2 (rendered metadata names) — otherwise ok: `get_many(ids, trace_id)` signature `core.py:748`; `row_to_json(t)` in `EntityRepository.cs:12-21`; PK clause matched `OrdinalIgnoreCase` against `schema.KeyColumn.Name` and read via `StringVal` (`:885-886`); `MatchParentId` matches payload `parent_id` (`IntelligenceFilterBuilder.cs:57-62`), which is what `ParentKey` is populated from (`ObjectSearchGrpcService.cs:493-498`) — the round-trip string is identical |
| §4.4 Reason | ok — `system` block with `cache_control` shape at `README.md:127`; `tools=` + `messages=` on `messages.create` at `tool-use.md:181-186` |
| §4.5 Escape hatch | ok — manual loop, `tool_result` batching in one user message, `is_error` all match `tool-use.md:168-230`, `:299-305`; `pause_turn` only arises from server-side tools, which this design does not attach; unbounded-turn concern dropped (see R8) |
| §4.6 Output | ok — citation check is a closed-set membership test over `[doc n]` against contexts held in session; no external dependency |
| §5 Configuration | ok — `fanout=4` equals server `OverFetchFactor` (`:746`); note the harness's *current* multiplier is 5, not 4 (`BenchmarkQueryScenario.cs:41`) — consistent with the spec calling arm A "the current multiplier" and arm B "the agent's fanout" |
| §6 Error handling | ok — every status/message in the table exists at the cited handler (`:350-352` chunk annotation, `:354-356` FailedPrecondition, `:383-391` InvalidArgument/Unavailable, `:337-339` empty stream). The FailedPrecondition *cause* column is mislabeled (see R12) but the response is "fail the session" either way |
| §7.1 Retrieval evaluation | → §2.1 |
| §7.2 Agent evaluation | ok — no codebase dependency; metrics are defined over the agent's own outputs |
| §8 Failure modes | ok — each row maps to a §3–§6 mechanism already checked; "Stage-2 PK filter returns nothing" is consistent with NotFound-collection → empty (`:417-422`) |
| §9 Verified assumptions | → §1 |

### Rules and operands (both failure directions)

| Row | Disposition |
|---|---|
| R1 Server chunk-filter rule: operator ∈ {EQUALS}, clause_type ∈ {FILTER}, property ∈ {KeyColumn (ci)} ∪ MetadataColumns (ci) | ok — over-inclusion: no other operator/property passes (`:878-912`); under-inclusion: key match is `OrdinalIgnoreCase` so `"id"` reaches `"Id"`; `MetadataColumns` is an `OrdinalIgnoreCase` set per the comment at `:891-894` |
| R2 Local filter validation (planner and `search_more`): field ∈ shown schema ∧ `is_metadata` ∧ value coerces | → §2.2 — the two operands (planner-emitted names from `GetSchema`, reasoner-emitted names copied from the rendered `[doc n]` blocks) are spelled differently by the design itself; tested the assumed-clean side (rendered names) against the real wire names |
| R3 Group-by-parent, max score | ok — over-merge: `parent_key` is the entity PK, unique within the tenant-scoped chunks collection (`IntelligenceStoreConsumer.cs:310`, `SchemaBuilder.cs:333`); under-merge: every chunk of one entity carries the same `ev.Key` string; identical to `DocumentRanking.CollapseByDocId` |
| R4 Cross-query merge (max across 1–3 queries' fused scores) | dropped — fused scores from different queries share corpus/config so the ordering is meaningful; the spec already forbids thresholding; no literal breakage |
| R5 Top-up predicate (< `m` passages → PK-filtered `SearchChunks`, `top_k=m`) | ok — server fetches `4m`, MMR selects `m` (`:408`, `:486`); PK clause with the Python field name `"id"` matches (`:885`) |
| R6 Dedup keys: `search_more` by document key; `expand_document` by passage | ok — document key = `parent_key` = `DocumentContext.key`; `ChunkSearchResponse` carries no chunk id (`object_search.proto:126-135`), so passage identity is `chunk_text` equality — sufficient, implementation detail |
| R7 `SchemaCache` key = token subject (+ tenant) | ok — over-merge across tenants is named and handled by the spec; staleness inside the TTL degrades to the §6 InvalidArgument retry path, which the spec already covers |
| R8 Tool budget: `calls > max_tool_calls ∨ context_tokens > ceiling` → error result | dropped — the *retrieval* bound is honored exactly as asked; a model that keeps issuing `tool_use` after error results is an implementation-time loop-termination edge case for critical-implementation-review, not a design defect in the asked-for behaviour |
| R9 Citation validation: `[doc n]` ∈ contexts | ok — closed set; re-request once, then strip and flag |
| R10 Context-budget trimming: drop lowest-score passages across all docs; zero-passage doc shows `summary` | ok — mechanics only reference fields the `DocumentContext` carries (`passages`, `summary`); no entity is dropped |
| R11 Eligibility "field flagged `is_metadata`" — producers | ok — single producer: `ProjectField` sets `IsMetadata = schema.MetadataColumns.Contains(col.Name)` (`ObjectMappingGrpcService.cs:214`); the server's filter rule reads the same `MetadataColumns` set (`:888`), so planner-eligible ≡ server-accepted |
| R12 Eligibility "read denied / nothing to read" — producers | dropped — producers enumerated: `SearchChunks` denied → empty (`:337-339`); tenant chunks collection NotFound → empty (`:417-422`); `GetMany` denied / owner-or-tenant mismatch → `Found=false` (`:96-102`, `:120-131`); `GetSchema` → type omitted (`:81`). One mislabel: `FailedPrecondition "no Qdrant collection"` fires when `CollectionName is null`, which `SchemaBuilder.cs:213` sets only for a type with *no* vector or chunk fields — not "registered but never written" (that case is the NotFound → empty-stream path). Both routes end in "fail / no documents", so the asked-for behaviour is unchanged; the cause column in §6 is wrong but not load-bearing |
| R13 §7.1 arm rule: "the multiplier is the only difference" | → §2.1 |

### Data-flow arrows (persistence boundaries flagged ⧫)

| Row | Disposition |
|---|---|
| A1 `GetSchema` → `render_schema` (name, description, clr_type, is_metadata) | ok — all four exist on `SchemaField` (`object_mapping.proto:139-152`) and are populated by `ProjectField` (`:206-215`) |
| A2 `Plan.queries[].filters[]` → `SearchClause(property, EQUALS, _to_search_value(value), FILTER)` + `filter_logic=AND` | ok — every constructor kwarg is a real proto field; `str/float/bool` all have a `_to_search_value` arm (`search.py:38-53`) |
| A3 `SearchChunks` stream → `(parent_key, chunk_text, score)` → group-by | ok — the three fields are what the server writes (`:496-500`) |
| A4 top-k `parent_key`s → `get_many` → `PolicyDoc` | ok — `parent_key` is the persisted `ev.Key` uuid string; `FetchManyByKeysAsync` does `Guid.Parse` (`EntityRepository.cs:16`); a `Found=false` row is silently dropped by the Python client (`core.py:759`) — a deletion race between projection and hydration is a CIR edge case, dropped |
| A5 `PolicyDoc` → `DocumentContext(title, metadata, summary)` → rendered `[doc n]` block | → §2.2 — attribute names on the instance are snake_case (`_entity_from_struct` maps `PascalCase` → member name, `core.py:555-566`), while the names the planner and server use are PascalCase (`SchemaBuilder.cs:62`, `:214`; `ObjectMappingGrpcService.cs:208`) |
| A6 stage-2 `parent_key` → PK clause → Qdrant `parent_id` match | ok — see §4.3 row |
| A7 contexts + question → `messages.create(system, tools, messages)` | ok |
| A8 `tool_use` blocks → `run_tool` → `tool_result[]` in one user message | ok — matches `tool-use.md:212-227` |
| A9 answer text → citation validation → `(answer, citations)` | ok |
| A10 ⧫ harness `runs/*.chunks.trec` + `qrels.trec` → `report.py --run --qrels --baseline` | ok on flags (`Iverson.Server/Iverson.LoadTest/scripts/report.py:594-612` defines exactly `--run`, `--qrels`, `--stats-path`, `--baseline`); the spec's path `scripts/report.py` does not exist at the repo root — see §1 item 20 |
| A11 ⧫ `keymap.json` (`parent_key → doc id`) read by `MaxPassageAggregator` | ok — real file is `{"<uuid>": "MED-10", ...}` (`~/repositories/iverson-benchmark-corpora/nfcorpus-arctic-2026-08-29/keymap.json`), matching `Aggregate(chunks, keyMap, limit)` |
| A12 §7.2 labelled set → session → grounding judge | ok — no codebase artifact involved |
| A13 `SchemaCache` → `IversonClient(..., acting_user_token=end_user_token).get_schema()` | ok — kwarg exists (`core.py:831`), metadata is attached (`core.py:887-889`) |

## 1. Verified-assumptions cross-check

| # | Status | Fresh-read note |
|---|---|---|
| 1 | holds | `core.py:885-891`; `SchemaField` fields at `object_mapping.proto:139-152` |
| 2 | holds | `get_schema` reads `self._acting_user_token` set at `core.py:860`; `with_acting_user` only on `EntityCoordinator` (`core.py:624-628`) |
| 3 | holds | `ObjectMappingGrpcService.cs:79-89` |
| 4 | holds | `core.py:630-636`, `:748-785` |
| 5 | holds | `core.py:783-785` returns `list(...)` of `ChunkSearchResponse` |
| 6 | holds | `vector_search.py:74-82` vs `ObjectSearchGrpcService.cs:869-913` |
| 7 | holds | `ObjectSearchGrpcService.cs:878-912` |
| 8 | holds | `:885-886`; `_to_search_value` falls through to `str()` for a `UUID` (`search.py:52-53`) |
| 9 | holds | `:337-339`; `ObjectRetrievalGrpcService.cs:96-102` |
| 10 | holds | `:381-391` |
| 11 | holds | `OverFetchFactor = 4` at `:746`; `fetchLimit = topK * OverFetchFactor` at `:408` |
| 12 | holds | proto comment `object_search.proto:129-135`; fusion + `Diversify` at `:480-503` |
| 13 | holds | `EntityRepository.cs:12-21`; note `GetMany` then applies `MaskDisallowedFields` (`ObjectRetrievalGrpcService.cs:137`) — "every column" means every column the acting user may read, which §3.4 already accounts for |
| 14 | holds | `EnrichmentConsumer.cs:297` (`EmbeddingFields ∪ ChunkFields` → `BuildSourceText`) |
| 15 | holds | `grep -i document Iverson.Clients/Python/iverson_client/*.py` → 0 hits; `document_template` → 0 hits outside `generated/` |
| 16 | holds | `annotations.py:133-179`, `:222`; `core.py:269-293`, `:329` |
| 17 | holds | `sample/main.py` is a `QueryBuilder` demo; the only non-test `search_chunks` caller is `conformance/driver.py:686`; no `anthropic`/agent references anywhere under `Iverson.Clients/Python` |
| 18 | holds | `MaxPassageAggregator.cs` → `DocumentRanking.CollapseByDocId` (max, descending, `Take(limit)`) |
| 19 | holds | `BenchmarkQueryScenario.cs:305-309` (`TopK(DocumentBudget * ChunkBudgetMultiplier)`), aggregate at `:322` |
| 20 | holds, **path wrong** | The generator-binding defect is real at `Iverson.Server/Iverson.LoadTest/scripts/report.py:552` and the fix is Step 3 of the phase-1 plan (`:224-236`). There is no `scripts/report.py` at the repository root, so the path in §9 item 20 and the command in §7.1 step 4 must read `Iverson.Server/Iverson.LoadTest/scripts/report.py` |
| 21 | holds | `nfcorpus-arctic-2026-08-29/` has `keymap.json`, `qrels.trec`, `runs/` (with `*.chunks.trec` and `*.similar.trec`) |
| 22 | holds | `docs/specs/2026-09-03-reranker-design.md:45-47` |
| 23 | holds, evidence elsewhere | Manual loop / `tool_result` batching / `messages.parse(output_format=)` are at `tool-use.md:168-230`, `:498-527` as cited. The "forced `tool_choice` rejected on Fable 5.1" clause is not in those sections; it is confirmed at `shared/tool-use-concepts.md:58` and `shared/error-codes.md:117` (400 on `any`/`tool` for Fable 5.1 / Mythos 5.1; Opus 5 accepts them) |

**Span check — dependencies with no covering assumption:**

- **GetSchema field names are the PascalCase wire names, not the Python attribute names.** The Python registrar sends `name=_to_pascal_case(field_name)` (`core.py:273`, `:96-98`); `SchemaBuilder` uses `prop.Name` verbatim as the column name (`SchemaBuilder.cs:62`); `GetSchema` returns `Name = col.Name` (`ObjectMappingGrpcService.cs:208`); `_entity_from_struct` maps back to the snake_case member (`core.py:555-566`). No §9 item states which spelling the agent's validator, renderer, and tool inputs use. Verified in-round → §2.2.
- **The harness's `DocumentBudget` and `ChunkBudgetMultiplier` are compile-time constants.** `private const int DocumentBudget = 50; private const int ChunkBudgetMultiplier = 5;` (`BenchmarkQueryScenario.cs:40-41`); no CLI or config binding exists (grep over `Iverson.LoadTest` finds only `ChunkBudgetGuard` reading them). §7.1's "no new code" and "the multiplier is the only difference" both rest on this. Verified in-round → §2.1.
- `SearchChunksRequest.filter_logic` exists (spec §4.2 code sets it): `object_search.proto:123`. Verified — ok.
- Structured outputs accept `anyOf` (for `Filter.value: str | float | bool`): `shared/tool-use-concepts.md:491`. Verified — ok.
- Omitting `thinking` on `claude-opus-5` runs adaptive thinking: `python/claude-api/README.md:253`. Verified — ok.

## 2. Literal-wrongness findings

### 2.1 §7.1 does not run the agent's ranker, and cannot run either arm without editing the harness

**Description.** §7.1 says the agent's retrieval layer "is scored with the existing harness and no new code", that arm B is "the same scenario at the agent's `fanout` (the multiplier is the only difference)", and that the answer is "the `fanout` at which document-level nDCG@10 stops improving". Two facts break this as written:

1. `DocumentBudget` (50) and `ChunkBudgetMultiplier` (5) are `private const` in `BenchmarkQueryScenario.cs:40-41`. Arm B (multiplier = 4, or any sweep value) requires a source edit and rebuild; there is no run-time knob.
2. Even with only the multiplier edited, the harness issues `top_k = 50 × fanout` and collapses to 50 documents, whereas the agent issues `top_k = k × fanout = 5 × fanout` and collapses to 5. These are different `SearchChunks` requests: the server fetches `4 × top_k` candidates, fuses with centroid + decay, and MMR-selects exactly `top_k` (`ObjectSearchGrpcService.cs:408`, `:480-486`). A 1000-candidate pool selected down to 250 and a 80-candidate pool selected down to 20 do not share a prefix, so the top-5 documents the harness measures are not the top-5 the agent would retrieve at the same `fanout`. The plateau §7.1 finds is the plateau of a different request.

**Evidence.** `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:40-41`, `:305-309`; `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:408`, `:480-486`.

**Proposed fix.** State in §7.1 that both constants are edited per arm (or, as the one code change, promoted to scenario options): `DocumentBudget = cfg.k` and `ChunkBudgetMultiplier = fanout`, so the harness issues the agent's exact `top_k` and collapses to the agent's `k`. Note the consequences the spec must then own: (a) `ChunkBudgetGuard` re-evaluates at the new budget (`BenchmarkQueryScenario.cs:94`); (b) with `k = 5` the ranking is 5 deep, so nDCG@10 is scored over a 5-deep list (ranks 6–10 contribute zero) — still a valid paired comparison across `fanout` values, but say so; (c) arm A is no longer "the existing chunks run" (those were produced at 50 × 5) — it is a fresh run at `k × <current multiplier>`, which also preserves §7.1's own rule that the encoder and everything else stay fixed between arms. Drop "no new code" or qualify it to "no new ranking code".

### 2.2 The design emits two spellings of every metadata field and validates against only one

**Description.** Field names enter the agent from two sources that the design treats as the same string:

- The planner (§4.1) is shown `GetSchema` names and its filters are validated by exact lookup in that schema ("unknown field → dropped"). Those names are the wire/column names — PascalCase, e.g. `Source`, `Jurisdiction`, `PublishedAt` — because the Python registrar sends `_to_pascal_case(field_name)` (`core.py:273`) and the server stores and returns that spelling (`SchemaBuilder.cs:62`, `ObjectMappingGrpcService.cs:208`).
- The reasoner (§4.4) is shown `[doc n]` blocks rendered from `DocumentContext.metadata`, built from the hydrated `PolicyDoc` whose attributes are snake_case (`core.py:555-566`); the spec's own example renders `source=legal jurisdiction=GB published_at=…`. The `search_more` tool (§4.5) then "validates filters as in §4.1" — against the PascalCase schema.

A `search_more` call that copies `published_at` (or `source`) from the page fails the exact-match check and is silently dropped, so the tool call runs unfiltered. If the validator were relaxed to case-insensitive matching to mirror the server (`MetadataColumns` is `OrdinalIgnoreCase`, `ObjectSearchGrpcService.cs:891-894`), `source`/`jurisdiction` would pass but `published_at` still would not — the server compares `published_at` against `PublishedAt` and rejects it as an unknown property (`:906-911`), which §6 then answers by dropping *all* filters for that query. Either way the asked-for behaviour of `search_more` ("validates filters, runs stage 1 + stage 2 on that one query") is not what happens for any multi-word metadata field, and the failure is silent.

**Evidence.** `Iverson.Clients/Python/iverson_client/core.py:96-98`, `:273`, `:555-566`; `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:62`; `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:208`; `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:888-911`; spec §4.3 render example.

**Proposed fix.** Pick one canonical spelling — the `GetSchema` name — and use it everywhere the model can see or emit a field name: render `[doc n]` metadata under schema names (map each `SchemaField.name` to the instance attribute via the same `_to_pascal_case` inverse the client uses, or read the `Struct` directly), describe the `search_more` filter parameter in terms of those names, and have the §4.1 validator normalise by case-insensitive comparison against `SchemaField.name` before the `is_metadata` and coercion checks. State this explicitly in §4.1 and §4.3 so the rule has one operand.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has two items (2.1, 2.2); §3 is empty. Address both, and correct the `report.py` path noted in §1 item 20, then proceed to implementation planning.
