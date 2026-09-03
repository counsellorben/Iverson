# A reasoning agent over Iverson — design

Status: design, verified against the codebase 2026-09-03 (`main@7fb527b`).

## 1. Purpose and scope

This document describes how to build a **reasoning agent** — a service that answers a user's
question by retrieving the **top-k documents** from Iverson on each reasoning turn, reasoning over
them with Claude, and citing them. It is written for someone building the agent in **Python**
against the `iverson_client` package, but everything below the client surface applies to the
other four clients, because they share one protobuf contract.

The agent is a **hybrid loop**: a deterministic first retrieval guarantees grounding, then the
model may call two retrieval tools within a hard budget when it flags a gap. The unit of retrieval
is the **document, located by its passages** (§4.2–4.3) — not a bag of passages and not whole
entities ranked by a single vector.

In scope: corpus modelling (§2), client and identity (§3), the loop (§4), configuration (§5),
error handling (§6), evaluation (§7), failure modes (§8). Out of scope: ingestion pipelines, any
change to the Iverson server or client libraries, and the choice of front end that supplies the
question and the end user's token.

## 2. Corpus modelling

The agent retrieves over one registered entity type. Four deliberate choices make that type
retrievable *by an agent*, as opposed to merely searchable:

```python
import uuid
from datetime import datetime
from iverson_client import (
    iverson_entity, iverson_key, iverson_description, iverson_metadata,
    iverson_chunk, iverson_summary, iverson_keywords,
)

@iverson_entity(description="A policy document the assistant may cite in an answer.")
class PolicyDoc:
    id: uuid.UUID = iverson_key()
    title: str = iverson_description("Document title, shown to the user beside a citation.")
    source: str = iverson_metadata(
        description="Originating system: one of 'hr', 'legal', 'it'.")
    jurisdiction: str = iverson_metadata(
        description="ISO 3166-1 alpha-2 country code the policy applies to.")
    published_at: datetime = iverson_metadata(
        description="Publication timestamp; newer supersedes older on the same topic.")
    body: str = iverson_chunk(
        max_tokens=512, overlap=64, contextual=True,
        description="Full policy text. The passages the assistant retrieves and quotes.")
    summary: str = iverson_summary(
        description="Server-generated summary of body; shown when body would exceed budget.")
    keywords: str = iverson_keywords(description="Server-generated keywords.")
```

1. **One `iverson_chunk()` field** holds the passage text. `SearchChunks` searches *this* field
   and nothing else. `contextual=True` prefixes each chunk with document context before it is
   embedded; the per-document retrieval in §4.3 depends on isolated fragments still carrying the
   thread of their document.
2. **`iverson_metadata()` on every field the planner may filter by.** The server accepts a
   `SearchChunks` filter clause only on the primary key or a `[IversonMetadata]` column, and only
   with `EQUALS`; anything else is `InvalidArgument`
   (`ObjectSearchGrpcService.BuildChunksFilter`). A field that is not metadata cannot be used to
   narrow chunk retrieval, full stop.
3. **`description` on the type and every field.** The planner (§4.1) reads `GetSchema` and
   chooses filters from the descriptions. An undescribed field is, for planning purposes,
   invisible.
4. **Enrichment (`iverson_summary`, `iverson_keywords`) is optional.** The server populates these
   columns at ingest from the concatenated embedding/chunk fields (`EnrichmentConsumer`). The
   agent uses `summary` as the per-document fallback when the passage budget is exhausted (§4.3).

Not used: `[IversonDocument]` templates. The Python client has no decorator for them and a single
chunk field is sufficient. Not used: `iverson_embedding()` on a title or abstract — `SearchSimilar`
is not on the agent's path (see §4.2 for why).

## 3. Client and identity

### 3.1 Two identities, always

Every call the agent makes carries two tokens:

- the agent's **own** OAuth2 client-credentials identity (`IversonClientCredentials`), which
  authenticates the service; and
- the **end user's** Authentik access token, sent as `x-acting-user-authorization`, under which
  the server evaluates tenant, row, and field authorization for that call.

The second is the one that matters for a RAG agent: **the model can only be shown what the human
asking could read themselves.** The front end that receives the question must supply the end
user's token; the agent never mints one.

### 3.2 Wiring in the Python client

```python
from iverson_client import IversonClient, IversonClientCredentials

SERVICE = IversonClientCredentials(
    client_id=..., client_secret=..., token_endpoint=...)

# One long-lived client per process, for the search/retrieval path.
client = IversonClient(host, port, use_tls=True, credentials=SERVICE)

# Per request: bind the end user's token to a coordinator. Every search_chunks / get_many
# call on `docs` now carries x-acting-user-authorization.
docs = client.coordinator(PolicyDoc).with_acting_user(end_user_token)
```

`EntityCoordinator.with_acting_user()` returns a bound copy sharing the channel; the search and
retrieval methods (`search_chunks`, `get_many`, `search_similar`, `search`) all send the header.

### 3.3 Schema discovery is also per user

`GetSchema` applies the acting user's authorization: denied types are dropped and field-masked
columns are omitted (`ObjectMappingGrpcService.GetSchema`). The planner must therefore see the
schema *as the end user*, or it will plan filters on fields the user cannot read.

In the Python client `get_schema()` lives on `IversonClient` and uses the token given at
construction; there is no coordinator-style per-request override. The agent handles this with a
**per-user schema cache**:

```python
@dataclass
class SchemaCache:
    ttl: timedelta
    _entries: dict[str, tuple[datetime, list[SchemaType]]] = field(default_factory=dict)

    def get(self, user_key: str, end_user_token: str) -> list[SchemaType]:
        hit = self._entries.get(user_key)
        if hit and datetime.utcnow() - hit[0] < self.ttl:
            return hit[1]
        with IversonClient(host, port, use_tls=True, credentials=SERVICE,
                           acting_user_token=end_user_token) as per_user:
            types = per_user.get_schema()
        self._entries[user_key] = (datetime.utcnow(), types)
        return types
```

`user_key` is the token's subject (plus tenant if the deployment maps one user to several). The
cost is one extra channel per user per TTL; the schema changes only on re-registration, so a TTL
of minutes is safe.

### 3.4 Consequences the agent must handle

| Situation | What Iverson does | What the agent does |
|---|---|---|
| User denied read on the type | `SearchChunks` returns an **empty stream**; `GetMany` streams `found=false` per key; `GetSchema` omits the type | Report "no accessible documents"; never fall back to the service identity |
| User field-masked on a metadata column | Filtering on it is `InvalidArgument` ("not authorized for this caller") — a value oracle | Cannot happen if the schema was fetched per user (§3.3); if it does, drop the clause (§6) |
| Chunk field itself masked | `SearchChunks` is `InvalidArgument` | Report "no accessible documents" |

### 3.5 The reasoning model

Anthropic Messages API, model `claude-opus-5`, adaptive thinking (the default; the `thinking`
parameter is omitted). Two call shapes are used — a structured-output call for planning (§4.1)
and a tool-use call for reasoning (§4.4). The model id is a configuration value (§5).

## 4. The loop

One request is one `AgentSession(question, end_user_token)`. Stages 4.1–4.4 run once, in order;
4.5 runs zero or more times under budget.

```
question ──► 4.1 plan ──► 4.2 locate ──► 4.3 assemble ──► 4.4 reason ──► answer + citations
                              ▲               ▲                 │
                              └───────────────┴── 4.5 tools ────┘  (≤ N calls, ≤ token ceiling)
```

### 4.1 Plan

One model call, no tools, structured output. Input: the question and a compact rendering of the
schema — type name, type description, and for each field flagged `is_metadata` its name,
description and CLR type. Output:

```python
class Filter(BaseModel):
    field: str
    value: str | float | bool

class RetrievalQuery(BaseModel):
    query_text: str          # embedding-friendly rewrite of (part of) the question
    filters: list[Filter]    # EQUALS clauses on metadata fields, ANDed

class Plan(BaseModel):
    queries: list[RetrievalQuery]   # 1..3

plan = anthropic_client.messages.parse(
    model=cfg.model, max_tokens=2048,
    system=PLANNER_SYSTEM,           # frozen text: role, rules, output shape
    messages=[{"role": "user", "content": render_schema(schema) + "\n\nQuestion: " + question}],
    output_format=Plan,
).parsed_output
```

The planner is asked for at most three queries: a rewrite of the question itself, plus up to two
decompositions when the question spans distinct topics. It is told the filter rules verbatim: only
the listed metadata fields, equality only, and "no filter" is the right answer when unsure.

**Local validation before anything reaches the wire.** Each planned filter is checked against the
schema the planner was shown: unknown field → dropped; field not `is_metadata` → dropped; value
coerced to the field's CLR type, failure → dropped. Every drop is logged with the trace id. A
planner hallucination therefore never becomes an `InvalidArgument`, and the retry in §6 is the
backstop, not the plan.

### 4.2 Locate (stage 1)

For each planned query, one `SearchChunks` call fetching `k × fanout` chunks:

```python
from iverson_client.generated import object_search_pb2 as pb
from iverson_client.search import _to_search_value   # same coercion the builders use

def chunks_request(q: RetrievalQuery, top_k: int, trace_id: str) -> pb.SearchChunksRequest:
    return pb.SearchChunksRequest(
        type_name="PolicyDoc", property="body", query=q.query_text, top_k=top_k,
        filter=[pb.SearchClause(property=f.field, operator=pb.EQUALS,
                                value=_to_search_value(f.value), clause_type=pb.FILTER)
                for f in q.filters],
        filter_logic=pb.AND, trace_id=trace_id)

hits: list[pb.ChunkSearchResponse] = []
for q in plan.queries:
    hits += docs.search_chunks(chunks_request(q, cfg.k * cfg.fanout, trace_id))
```

The request is built from the generated protos rather than `ChunksBuilder` because the builder
accepts exactly one clause; the server accepts any number of ANDed `EQUALS` clauses over the
primary key and metadata columns.

**Group by parent, rank by best chunk.** Merged hits are grouped by `parent_key`; each parent's
score is the **maximum** of its chunk scores — not the sum (sum rewards long documents) and not
the first seen. Take the top-k parents. This is the same rule the benchmark harness uses
(`MaxPassageAggregator` → `DocumentRanking.CollapseByDocId`), so the retrieval layer of the agent
and the harness are one ranker (§7.1).

Why the over-fetch and the group-by exist: `top_k` on `SearchChunks` counts *chunks*, and the
server neither dedups by parent nor returns them in fused-score order — results are diversified by
MMR before streaming. `k` chunks routinely span more than `k` parents at one fragment each. `fanout`
turns a chunk budget back into a document budget.

**Scores are for ordering only.** The `score` on a chunk is a fused value (cosine blended with a
parent-centroid similarity and a recency decay) that MMR has already reordered. Its magnitude is
not a cosine and is not stable across corpora or configuration. The agent never thresholds it.

Why not `SearchSimilar`: it ranks whole entities by one vector over one property. It is good at
"documents *about* X" and poor at locating the passage that *answers* X, which is what the model
needs on the page in front of it. Chunk vectors locate; the parent grouping restores the document.

### 4.3 Assemble (stage 2)

Two calls, not `2k`:

1. **Hydrate.** `docs.get_many(parent_keys, trace_id)` — one streaming call returning the k
   entities as `PolicyDoc` instances with every column (`row_to_json` from Postgres): identity,
   metadata, `summary`, and `body`.
2. **Top up thin documents.** For each parent that surfaced fewer than `m` passages in stage 1,
   one `SearchChunks` with `top_k=m` and a single `EQUALS` clause on the primary key, using the
   **original question** as the query text:

   ```python
   def passages_for(parent_key: str, question: str, m: int, trace_id: str):
       req = pb.SearchChunksRequest(
           type_name="PolicyDoc", property="body", query=question, top_k=m,
           filter=[pb.SearchClause(property="id", operator=pb.EQUALS,
                                   value=_to_search_value(parent_key), clause_type=pb.FILTER)],
           trace_id=trace_id)
       return docs.search_chunks(req)
   ```

   The key clause is matched case-insensitively against the registered key column and its value
   is read as a string, so passing the Python field name and the key as a string is correct.
   Parents that already have ≥ `m` passages skip this call.

The output is `k` records:

```python
@dataclass
class DocumentContext:
    key: str
    title: str
    metadata: dict[str, object]     # every is_metadata field
    summary: str | None
    passages: list[tuple[float, str]]   # (score, text), best first
    best_score: float
```

**Rendering under budget.** Each `DocumentContext` becomes one numbered block:

```
[doc 3] key=… title="…" source=legal jurisdiction=GB published_at=2025-11-02
  passage: …
  passage: …
```

A per-session token ceiling (`context_tokens`) caps the rendered context. Passages are dropped
lowest-score-first across all documents until the render fits; a document reduced to zero
passages shows its `summary` instead, so every one of the k documents remains citable. Documents
themselves are never dropped by the budget — k is the promise.

### 4.4 Reason

One model call with the rendered context, the question, and the two tools of §4.5 attached:

```python
messages = [{"role": "user", "content": render_context(contexts) + "\n\nQuestion: " + question}]
response = anthropic_client.messages.create(
    model=cfg.model, max_tokens=16000,
    system=REASONER_SYSTEM,            # frozen; cache_control on it
    tools=TOOLS, messages=messages)
```

`REASONER_SYSTEM` instructs the model to: answer from the numbered documents; cite by `[doc n]`;
say explicitly when the documents do not contain the answer; and use the tools only when a
specific gap can be named. The system text is frozen so the prefix caches; the volatile parts
(context, question) follow it.

### 4.5 Escape hatch — bounded tool-calling

Two tools, both thin wrappers over stages 4.2–4.3:

| Tool | Input | What it does |
|---|---|---|
| `search_more` | `query_text`, `filters[]` | Validates filters as in §4.1, runs stage 1 + stage 2 on that one query, returns only documents **not already in context**, appended as `[doc k+1…]` |
| `expand_document` | `doc_number`, `query_text` | Runs the PK-filtered `SearchChunks` of §4.3 for that document with a fresh query, returns the passages not already shown |

The manual tool loop is the standard one — append the assistant content, execute each `tool_use`
block, return all `tool_result` blocks in a single user message, repeat until `stop_reason` is
`end_turn`:

```python
calls = 0
while response.stop_reason == "tool_use":
    messages.append({"role": "assistant", "content": response.content})
    results = []
    for block in (b for b in response.content if b.type == "tool_use"):
        calls += 1
        if calls > cfg.max_tool_calls or session.context_tokens > cfg.context_tokens:
            results.append({"type": "tool_result", "tool_use_id": block.id, "is_error": True,
                            "content": "Retrieval budget exhausted; answer from the documents shown."})
            continue
        results.append({"type": "tool_result", "tool_use_id": block.id,
                        "content": run_tool(block.name, block.input, session)})
    messages.append({"role": "user", "content": results})
    response = anthropic_client.messages.create(..., tools=TOOLS, messages=messages)
```

Bounds are hard stops: at most `max_tool_calls` per session (default 3) and the `context_tokens`
ceiling. Once either is hit every further tool call receives the budget-exhausted result, which
forces a final answer. The session remembers every retrieved document by key, so a tool call
that re-finds a document already in context adds nothing to the prompt.

### 4.6 Output

The answer text plus the citation list: for each `[doc n]` the model cited, the document's key,
title, and the passages that were on the page when it answered. **Citation precision is
enforced, not measured**: a `[doc n]` that does not correspond to a document in context is
rejected before the answer is returned (the answer is re-requested once with the invalid citation
named; a second failure returns the answer with that citation stripped and flagged).

## 5. Configuration

One dataclass; defaults chosen to be measured in §7, not trusted.

```python
@dataclass(frozen=True)
class AgentConfig:
    model: str = "claude-opus-5"
    k: int = 5                 # documents per turn
    fanout: int = 4            # stage-1 chunk budget = k * fanout
    m: int = 3                 # passages per document after stage 2
    max_tool_calls: int = 3    # escape-hatch budget per session
    context_tokens: int = 24_000
    schema_ttl: timedelta = timedelta(minutes=10)
```

`fanout=4` mirrors the server's own over-fetch factor for the fusion/MMR stage; it is a starting
point, and §7.1 is how to choose it.

## 6. Error handling

Only at the Iverson boundary, and only for what has been observed to happen:

| Error | Cause | Response |
|---|---|---|
| `InvalidArgument` on `SearchChunks` | A filter clause the server rejects (operator, property, masked column) | Drop **all** planned filters for that query, retry once, log the trace id. If it recurs, the query is skipped. |
| `InvalidArgument` "no [IversonChunk] annotation" / "not authorized" on the chunk field | Misconfiguration or a masked chunk field | Fail the session: "no accessible documents" |
| `Unavailable` | Embedding backend down | Fail the session; do **not** answer without retrieval |
| Empty stream | Nothing matched, or read denied | The model is told "no documents were found for: <query>" explicitly; it is never given an empty context silently |
| `FailedPrecondition` "no Qdrant collection" | Type registered but never written | Fail the session |

Model-side: the `refusal` stop reason is checked before content is read; `max_tokens` on the
reasoning call is 16 000 so a long cited answer is not truncated.

## 7. Evaluation

Two layers, measured separately and in this order. A `fanout` change moves both, so the retrieval
layer is stabilised first.

### 7.1 Retrieval layer — offline, no LLM

Stage 1 plus the group-by-parent step *is* a document ranker, and it is the same ranker the
benchmark harness already runs: `BenchmarkQueryScenario` issues `SearchChunks` with
`top_k = DocumentBudget × ChunkBudgetMultiplier` and collapses via `MaxPassageAggregator`. So the
agent's retrieval layer is scored **with the existing harness and no new code**:

1. Use a corpus already ingested under `~/repositories/iverson-benchmark-corpora/<corpus>/`
   (each has `keymap.json`, `qrels.trec`, and `runs/`). Do not change the embedding model between
   arms — the standing rule from the reranker spec: an encoder change invalidates every number
   measured before it.
2. Arm A (baseline): the existing "chunks" run at the current multiplier.
3. Arm B: the same scenario at the agent's `fanout` (the multiplier is the only difference).
4. Score: `scripts/report.py --run <runs dir> --qrels qrels.trec --baseline <arm A>.trec` for
   nDCG@10, R@50, AP with paired t-test, sign-flip permutation, and Holm correction.

**Known harness defect.** At `main`, `report.py:552` binds the baseline run as a generator and
re-iterates it per measure, so the R@50 and AP rows of the `--baseline` comparison are computed
against an all-zero baseline. The fix (`list(...)`) is Step 3 of
`docs/plans/2026-09-03-reranker-phase1-implementation-plan.md`. Until it lands, **trust only the
nDCG@10 row** of the paired output; the R@50/AP absolute scores in the per-run section are
unaffected.

The question §7.1 answers is one number: the `fanout` at which document-level nDCG@10 stops
improving. Larger `fanout` costs one larger Qdrant fetch and nothing else, so the smallest value
at the plateau wins.

### 7.2 Agent layer — online, with the LLM

A hand-labelled set of at least 50 items: `(question, expected_document_keys, expected_facts)`,
drawn from the real corpus and the real user population's questions. Per item, run one full
session and record:

| Metric | Definition | Target |
|---|---|---|
| Citation precision | cited keys ⊆ context keys | 1.0 by construction (§4.6); asserted, and any violation is a bug |
| Citation recall | expected keys ∩ cited keys / expected keys | Report; compare across `k` and `m` |
| Grounding | each answer claim supported by a cited passage on the page — judged by a second model call given only the answer and the cited passages | Report; the primary quality metric |
| Insufficiency honesty | on items with no expected keys (unanswerable), the answer says so | 1.0 |
| Tool calls / session | count | ≤ `max_tool_calls`; distribution reported |
| Context tokens / session | rendered context + tool results | ≤ `context_tokens`; distribution reported |

Tune `k`, `m`, and `max_tool_calls` on these; never `fanout` (that is §7.1's). Re-run §7.2 whenever
the corpus's chunking, the embedding model, or the reasoning model changes.

## 8. Failure modes

Each with its symptom and its fix, so an operator can recognise it.

| Failure | Symptom | Fix |
|---|---|---|
| Thresholding fused scores | Good documents silently missing; results vary with corpus size | Never threshold; order only (§4.2) |
| Planner picks a non-metadata field | `InvalidArgument` on every query, or (after §6) filters silently gone | Local validation (§4.1) — check the drop log |
| Stage-2 PK filter returns nothing | A document hydrated by `get_many` has no passages | Chunks not yet projected (eventual consistency) or projected under another tenant; use the stage-1 fragments, log the trace id |
| Read-denied user | Every stage empty | Report "no accessible documents"; never hallucinate; never retry as the service |
| Schema fetched with the service identity | Planner filters on masked fields → `InvalidArgument` "not authorized" | §3.3 — always per user |
| Embedding model changed server-side | Retrieval metrics move with no agent change | Re-run arm A before any comparison (§7.1) |
| Escape-hatch loop | Sessions hit `max_tool_calls` routinely | Hard stop is working; inspect the questions — usually a corpus-coverage gap, not an agent bug |
| Context over budget after tool calls | Answers truncate or lose earlier documents | The ceiling counts tool results too (§4.5); lower `m` before lowering `k` |

## 9. Verified assumptions

Every claim above about Iverson's behaviour was checked against `main@7fb527b` on 2026-09-03.

| # | Assumption | Evidence |
|---|---|---|
| 1 | `get_schema()` returns `SchemaType` with type/field descriptions and `is_metadata`/`is_key`/`is_chunk` | `Iverson.Clients/Python/iverson_client/core.py:885-891`; `object_mapping.proto` `SchemaType`/`SchemaField` |
| 2 | `get_schema()` uses the constructor-time acting-user token; `with_acting_user()` exists only on `EntityCoordinator` | `core.py:860,889` and `core.py:624-628` — the reason for §3.3's per-user client |
| 3 | `GetSchema` applies the acting user's row and field authorization | `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:79-89` |
| 4 | Coordinator search/retrieval methods send `x-acting-user-authorization` | `core.py:630-636`, `748-785` |
| 5 | `search_chunks()` accepts a raw `SearchChunksRequest` and returns `list[ChunkSearchResponse]` | `core.py:783-785` |
| 6 | `ChunksBuilder.where()` allows one clause; the server allows many | `vector_search.py:74-82` vs `ObjectSearchGrpcService.cs:869-913` |
| 7 | Server filter rules: `EQUALS`+`FILTER` only, on the PK (→ `parent_id` match) or a metadata column; masked column → `InvalidArgument` | `ObjectSearchGrpcService.cs:878-912` |
| 8 | PK clause is matched case-insensitively and read as `string_val` | `ObjectSearchGrpcService.cs:885-887`; `search.py:38-53` stringifies a `UUID` |
| 9 | Read-denied → empty stream (`SearchChunks`), `found=false` (`GetMany`) | `ObjectSearchGrpcService.cs:337-339`; `ObjectRetrievalGrpcService.cs:96-102` |
| 10 | Empty query → `InvalidArgument`; embedding backend failure → `Unavailable` | `ObjectSearchGrpcService.cs:381-391` |
| 11 | Server over-fetch factor is 4 for the fusion/MMR stage | `ObjectSearchGrpcService.cs:747,408` |
| 12 | Chunk `score` is fused and MMR-reordered — order only | `object_search.proto` `ChunkSearchResponse`; `ObjectSearchGrpcService.cs:404-470` |
| 13 | `GetMany` returns every column, large fields and enrichment included | `Iverson.Sql/EntityRepository.cs:12-21` (`row_to_json(t)`) |
| 14 | Summary/keywords are populated server-side from the embedding+chunk fields | `Iverson.Api/Consumers/EnrichmentConsumer.cs:82-300` |
| 15 | Python has no `[IversonDocument]` decorator | `grep document iverson_client/*.py` — none; `SchemaRegistrar._build_request` never sets `document_template` |
| 16 | `iverson_chunk(contextual=)`, `iverson_metadata`, `iverson_description`, `iverson_summary`, `iverson_keywords`, `@iverson_entity(description=)` reach the wire | `annotations.py:133-179,222`; `core.py:269-293,329` |
| 17 | No existing agent or RAG sample in the Python client to reuse | `sample/`, `conformance/` — only `driver.py:679-686` calls `search_chunks` |
| 18 | `MaxPassageAggregator` = max chunk score per document, descending | `Iverson.LoadTest/Benchmark/{MaxPassageAggregator,DocumentRanking}.cs` |
| 19 | The harness already runs chunks at `DocumentBudget × ChunkBudgetMultiplier` then collapses | `Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs:300-317` |
| 20 | `report.py --baseline` R@50/AP defect and its scheduled fix | `scripts/report.py:552-557`; `docs/plans/2026-09-03-reranker-phase1-implementation-plan.md:224-236` |
| 21 | Corpora with `keymap.json` (`parent_key → doc id`) and `qrels.trec` exist | `~/repositories/iverson-benchmark-corpora/nfcorpus-arctic-2026-08-29/` et al. |
| 22 | "Do not change the encoder and measure in the same run" | `docs/specs/2026-09-03-reranker-design.md` §2 |
| 23 | Anthropic SDK shapes: manual tool loop, `tool_result` batching, `messages.parse(output_format=Model)`; forced `tool_choice` avoided (rejected on Fable 5.1) | `claude-api` skill, `python/claude-api/tool-use.md` §Manual Agentic Loop, §Structured Outputs |
